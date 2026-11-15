using System.Buffers;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace DeskBeam.Engine.Capture;

internal enum HdrMode
{
    ScRgb = 0,
    Pq = 1,
    PassThrough = 2,
}

/// <summary>
/// Converts an HDR desktop surface (FP16 scRGB or 10-bit HDR10) into SDR BGRA with tone mapping,
/// while downscaling to the streaming resolution in the same GPU pass. Reading HDR surfaces as if
/// they were BGRA8 was the source of the pink/green noise and the huge non-compressible frames.
/// </summary>
internal sealed class HdrConverter : IDisposable
{
    private const string ShaderFileName = "tonemap-v1.hlsl";

    private const string ShaderSource = """
        cbuffer Params : register(b0)
        {
            float SdrWhiteScale;   // 80 / SdrWhiteNits, for linear scRGB input
            float SdrWhiteNits;    // absolute nits of the SDR white point
            float Mode;            // 0 = scRGB (FP16), 1 = HDR10 PQ (BT.2020)
            float Padding;
        };

        Texture2D<float4> Source : register(t0);
        SamplerState LinearSampler : register(s0);

        struct VSOut
        {
            float4 pos : SV_Position;
            float2 uv : TEXCOORD0;
        };

        VSOut VSMain(uint id : SV_VertexID)
        {
            VSOut o;
            o.uv = float2((id << 1) & 2, id & 2);
            o.pos = float4(o.uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
            return o;
        }

        float3 SrgbFromLinear(float3 c)
        {
            c = saturate(c);
            return c <= 0.0031308 ? 12.92 * c : 1.055 * pow(c, 1.0 / 2.4) - 0.055;
        }

        // Hue-preserving highlight rolloff: everything at or below SDR white is untouched, above it
        // compresses smoothly instead of clipping to a flat white (which shifts hues).
        float3 ToneMap(float3 c)
        {
            c = max(c, 0.0);
            float y = dot(c, float3(0.2126, 0.7152, 0.0722));
            if (y <= 1.0)
                return c;

            const float softness = 0.45;
            float mapped = 1.0 + softness * (1.0 - exp(-(y - 1.0) / softness));
            return c * (mapped / max(y, 1e-5));
        }

        float3 PqToNits(float3 pq)
        {
            const float m1 = 0.1593017578125;
            const float m2 = 78.84375;
            const float c1 = 0.8359375;
            const float c2 = 18.8515625;
            const float c3 = 18.6875;

            float3 p = pow(saturate(pq), 1.0 / m2);
            float3 num = max(p - c1, 0.0);
            float3 den = c2 - c3 * p;
            return 10000.0 * pow(num / max(den, 1e-5), 1.0 / m1);
        }

        float4 PSMain(VSOut input) : SV_Target
        {
            uint w, h;
            Source.GetDimensions(w, h);
            float2 off = 0.25 / float2(max(w, 1), max(h, 1));

            // Four-tap box sample keeps downscaling crisp without needing mipmaps.
            float4 s = (
                Source.Sample(LinearSampler, input.uv + float2(-off.x, -off.y)) +
                Source.Sample(LinearSampler, input.uv + float2( off.x, -off.y)) +
                Source.Sample(LinearSampler, input.uv + float2(-off.x,  off.y)) +
                Source.Sample(LinearSampler, input.uv + float2( off.x,  off.y))) * 0.25;

            float3 lin;
            if (Mode < 0.5)
            {
                lin = s.rgb * SdrWhiteScale;
            }
            else if (Mode < 1.5)
            {
                lin = PqToNits(s.rgb) / SdrWhiteNits;
                // BT.2020 -> BT.709 primaries.
                lin = mul(float3x3(
                    1.6605, -0.5876, -0.0728,
                    -0.1246, 1.1329, -0.0083,
                    -0.0182, -0.1006, 1.1187), lin);
                lin = max(lin, 0.0);
            }
            else
            {
                // 10-bit SDR desktop: values are already gamma-encoded, so pass them through.
                return float4(s.rgb, 1.0);
            }

            return float4(SrgbFromLinear(ToneMap(lin)), 1.0);
        }
        """;

    private readonly ID3D11Device _device;
    private readonly Action<string>? _log;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11Buffer _constants;

    private ID3D11Texture2D? _sourceTexture;
    private ID3D11ShaderResourceView? _sourceView;
    private ID3D11Texture2D? _targetTexture;
    private ID3D11RenderTargetView? _targetView;
    private ID3D11Texture2D? _stagingTexture;

    private int _sourceWidth;
    private int _sourceHeight;
    private Format _sourceFormat;
    private int _outputWidth;
    private int _outputHeight;

    public HdrConverter(ID3D11Device device, Action<string>? log)
    {
        _device = device;
        _log = log;

        var shaderPath = EnsureShaderFile();
        var vs = Compiler.CompileFromFile(shaderPath, "VSMain", "vs_5_0", ShaderFlags.OptimizationLevel3,
            EffectFlags.None);
        var ps = Compiler.CompileFromFile(shaderPath, "PSMain", "ps_5_0", ShaderFlags.OptimizationLevel3,
            EffectFlags.None);

        _vertexShader = device.CreateVertexShader(vs.Span);
        _pixelShader = device.CreatePixelShader(ps.Span);
        _sampler = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
        });
        _constants = device.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer, ResourceUsage.Default));
    }

    private static string EnsureShaderFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DeskBeam");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ShaderFileName);
        File.WriteAllText(path, ShaderSource);
        return path;
    }

    /// <summary>
    /// Renders the HDR source into an SDR BGRA frame of the requested size and returns it as a
    /// pooled raw frame. Returns null if the conversion could not be performed.
    /// </summary>
    public unsafe RawFrame? Convert(ID3D11DeviceContext context, ID3D11Texture2D source, int sourceWidth,
        int sourceHeight, Format sourceFormat, int outputWidth, int outputHeight, HdrMode mode, float sdrWhiteNits)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || outputWidth <= 0 || outputHeight <= 0)
            return null;

        try
        {
            EnsureResources(sourceWidth, sourceHeight, sourceFormat, outputWidth, outputHeight);

            // Copy first: the duplication texture is not guaranteed to be shader-readable.
            context.CopyResource(_sourceTexture!, source);

            var constants = new[]
            {
                80f / Math.Max(80f, sdrWhiteNits),
                Math.Max(80f, sdrWhiteNits),
                (float)mode,
                0f,
            };
            context.UpdateSubresource(constants.AsSpan(), _constants);

            context.OMSetRenderTargets(_targetView!);
            context.RSSetViewports(new[] { new Viewport(0, 0, outputWidth, outputHeight, 0f, 1f) });
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.VSSetShader(_vertexShader);
            context.PSSetShader(_pixelShader);
            context.PSSetShaderResources(0, new[] { _sourceView! });
            context.PSSetSamplers(0, new[] { _sampler });
            context.PSSetConstantBuffers(0, new[] { _constants });
            context.Draw(3, 0);

            context.CopyResource(_stagingTexture!, _targetTexture!);
            var mapped = context.Map(_stagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

            var stride = outputWidth * 4;
            var buffer = ArrayPool<byte>.Shared.Rent(stride * outputHeight);
            try
            {
                var destination = buffer.AsSpan(0, stride * outputHeight);
                for (var y = 0; y < outputHeight; y++)
                {
                    var row = (byte*)mapped.DataPointer + (long)y * mapped.RowPitch;
                    new ReadOnlySpan<byte>(row, stride).CopyTo(destination.Slice(y * stride, stride));
                }
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw;
            }
            finally
            {
                context.Unmap(_stagingTexture!, 0);
            }

            return new RawFrame(buffer, outputWidth, outputHeight, stride, 0,
                b => ArrayPool<byte>.Shared.Return(b));
        }
        catch (Exception ex)
        {
            _log?.Invoke("HDR conversion failed: " + ex.Message);
            return null;
        }
    }

    private void EnsureResources(int sourceWidth, int sourceHeight, Format sourceFormat, int outputWidth,
        int outputHeight)
    {
        if (_sourceTexture is null || _sourceWidth != sourceWidth || _sourceHeight != sourceHeight ||
            _sourceFormat != sourceFormat)
        {
            _sourceView?.Dispose();
            _sourceTexture?.Dispose();

            _sourceWidth = sourceWidth;
            _sourceHeight = sourceHeight;
            _sourceFormat = sourceFormat;

            _sourceTexture = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)sourceWidth,
                Height = (uint)sourceHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = sourceFormat,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            });
            _sourceView = _device.CreateShaderResourceView(_sourceTexture);
        }

        if (_targetTexture is null || _outputWidth != outputWidth || _outputHeight != outputHeight)
        {
            _targetView?.Dispose();
            _targetTexture?.Dispose();
            _stagingTexture?.Dispose();

            _outputWidth = outputWidth;
            _outputHeight = outputHeight;

            var description = new Texture2DDescription
            {
                Width = (uint)outputWidth,
                Height = (uint)outputHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            };
            _targetTexture = _device.CreateTexture2D(description);
            _targetView = _device.CreateRenderTargetView(_targetTexture);

            _stagingTexture = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)outputWidth,
                Height = (uint)outputHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            });
        }
    }

    public void Dispose()
    {
        _sourceView?.Dispose();
        _sourceTexture?.Dispose();
        _targetView?.Dispose();
        _targetTexture?.Dispose();
        _stagingTexture?.Dispose();
        _constants.Dispose();
        _sampler.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
    }
}
