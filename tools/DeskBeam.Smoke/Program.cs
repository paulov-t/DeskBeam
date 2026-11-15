using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using DeskBeam.Core.Audio;
using DeskBeam.Core.Models;
using DeskBeam.Core.Protocol;
using DeskBeam.Core.Security;
using DeskBeam.Core.Video;
using DeskBeam.Engine.Capture;
using DeskBeam.Engine.Streaming;

namespace DeskBeam.Smoke;

internal static class Program
{
    private static int _failures;

    private static async Task<int> Main(string[] args)
    {
        Console.WriteLine("DeskBeam smoke test");
        Console.WriteLine("===================");
        SetProcessDpiAwarenessContext(DpiAwarenessPerMonitorV2);

        var monitors = ScreenCaptureFactory.EnumerateMonitors();
        if (monitors.Count == 0)
        {
            Console.WriteLine("FAIL: no monitors detected.");
            return 2;
        }

        foreach (var monitor in monitors)
            Console.WriteLine($"Monitor {monitor.Index}: {monitor.Display} at {monitor.X},{monitor.Y}");

        var selectedMonitor = monitors[Math.Clamp(ParseMonitorIndex(args), 0, monitors.Count - 1)];
        var controllerOnly = args.Contains("--controller-only");
        var audioTest = args.Contains("--audio");
        var options = new HostOptions
        {
            Port = 47800,
            Pin = "246810",
            ServerName = "SmokeHost",
            MonitorIndex = selectedMonitor.Index,
            MaxWidth = 1280,
            MaxHeight = 720,
            Fps = 60,
            Quality = 65,
            AllowInput = !controllerOnly,
            EnableVirtualGamepad = true,
            IncludeCursor = true,
            CaptureApi = ParseCaptureApi(args),
            EnableAudio = audioTest,
            AudioBitrateKbps = 64,
            AudioExcludedProcesses = "Discord,DiscordPTB,DiscordCanary,DiscordDevelopment",
        };
        Console.WriteLine($"  Capture API: {ScreenCaptureFactory.Describe(options.CaptureApi)}");

        TestOpusRoundTrip();

        await using var server = new HostServer();
        server.Log += message => Console.WriteLine("  [host] " + message);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = timeout.Token;

        await TestTileCodecAsync(ct);

        try
        {
            await server.StartAsync(options);
            Assert(server.IsRunning, "host is running");
            Assert(server.GetSnapshot().Width > 0, "host reports an output size");
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL: host did not start: " + ex);
            return 2;
        }

        try
        {
            await TestWrongPinAsync(options, ct);
            await TestStreamingAsync(server, options, selectedMonitor, args.Contains("--input"), args.Contains("--dump"),
                ParseSoakSeconds(args), controllerOnly, audioTest, ct);

            if (args.Contains("--upnp"))
            {
                Console.WriteLine("Testing UPnP discovery...");
                var result = await server.MapPortsAsync(ct);
                Console.WriteLine("  UPnP: " + result.Message);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL: " + ex);
            _failures++;
        }
        finally
        {
            await server.StopAsync();
        }

        Assert(!server.IsRunning, "host stopped cleanly");

        // A second start in the same process reuses the broadcaster and creates a fresh pipeline.
        try
        {
            await server.StartAsync(options);
            await TestRestartAsync(server, options, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL: restart: " + ex);
            _failures++;
        }
        finally
        {
            await server.StopAsync();
        }

        Assert(!server.IsRunning, "host stopped cleanly after a restart");

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static CaptureApi ParseCaptureApi(string[] args)
    {
        var value = args.FirstOrDefault(a => a.StartsWith("--capture=", StringComparison.OrdinalIgnoreCase))
            ?.Split('=', 2)[1]
            .ToLowerInvariant();

        return value switch
        {
            "wgc" or "windowsgraphicscapture" => CaptureApi.WindowsGraphicsCapture,
            "dxgi" or "desktopduplication" => CaptureApi.DesktopDuplication,
            "gdi" => CaptureApi.Gdi,
            _ => CaptureApi.Auto,
        };
    }

    private static int ParseMonitorIndex(string[] args)
    {
        var value = args.FirstOrDefault(a => a.StartsWith("--monitor=", StringComparison.OrdinalIgnoreCase))
            ?.Split('=', 2)[1];
        return int.TryParse(value, out var index) ? index : 0;
    }

    private static int ParseSoakSeconds(string[] args)
    {
        var value = args.FirstOrDefault(a => a.StartsWith("--soak=", StringComparison.OrdinalIgnoreCase))
            ?.Split('=', 2)[1];
        return int.TryParse(value, out var seconds) ? Math.Clamp(seconds, 0, 300) : 0;
    }

    private static void TestOpusRoundTrip()
    {
        Console.WriteLine("Testing Opus encode/decode...");
        var encoder = new OpusAudioEncoder(64);
        var decoder = new OpusAudioDecoder();
        var pcm = new byte[AudioConstants.PcmBytesPerFrame];
        var packet = new byte[AudioConstants.MaxOpusPacketBytes];
        var decoded = new byte[AudioConstants.PcmBytesPerFrame];
        var samples = MemoryMarshal.Cast<byte, short>(pcm.AsSpan());
        var ok = true;
        var encodedBytes = 0L;

        for (var frame = 0; frame < 100; frame++)
        {
            for (var i = 0; i < AudioConstants.SamplesPerFramePerChannel; i++)
            {
                var seconds = (frame * AudioConstants.SamplesPerFramePerChannel + i) /
                              (double)AudioConstants.SampleRate;
                var value = (short)(Math.Sin(2 * Math.PI * 440 * seconds) * 12000);
                samples[i * 2] = value;
                samples[i * 2 + 1] = value;
            }

            var length = encoder.Encode(pcm, packet);
            if (length is <= 0 or >= AudioConstants.MaxOpusPacketBytes)
                ok = false;

            encodedBytes += length;
            if (!decoder.TryDecode(packet, 0, length, decoded) || !decoded.AsSpan().ContainsAnyExcept((byte)0))
                ok = false;
        }

        var plc = decoder.TryDecodeLost(decoded);
        var fec = decoder.TryDecodeFec(packet, decoded);
        Assert(ok, "100 Opus frames encode and decode (10 ms, 48 kHz stereo)");
        Assert(plc, "Opus packet-loss concealment returns a frame");
        Assert(fec, "Opus in-band FEC recovery returns a frame");
        Console.WriteLine($"  Opus: {encodedBytes * 8.0 / 1000.0:F0} kbps for a 440 Hz tone");
    }

    private static async Task TestTileCodecAsync(CancellationToken ct)
    {
        Console.WriteLine("Testing tile video codec...");
        const int width = 320;
        const int height = 180;
        var options = new VideoEncoderOptions(width, height, 60);

        // High-frequency content makes the keyframe realistically large, while solid blocks stay
        // accurate through JPEG so the composited result can be checked exactly.
        var frameA = new byte[width * height * 4];
        var seed = 12345u;
        for (var i = 0; i < frameA.Length; i += 4)
        {
            seed = seed * 1664525 + 1013904223;
            frameA[i] = (byte)(seed >> 24);
            frameA[i + 1] = (byte)(seed >> 16);
            frameA[i + 2] = (byte)(seed >> 8);
            frameA[i + 3] = 255;
        }

        Fill(frameA, width, 200, 100, 60, 60, 30, 90, 180);
        Fill(frameA, width, 20, 20, 70, 70, 10, 10, 10);

        using var encoder = new JpegTileEncoder();
        var key = encoder.Encode(frameA, width, height, width * 4, options, 0, forceKeyframe: false);
        Assert(key is { IsKeyframe: true, KeyframeJpeg: not null }, "tile encoder emits an initial keyframe");

        var unchanged = encoder.Encode(frameA, width, height, width * 4, options, 0, forceKeyframe: false);
        Assert(unchanged is null, "tile encoder skips an unchanged frame");

        var cached = encoder.EncodeCachedKeyframe(options, 0);
        Assert(cached is { IsKeyframe: true }, "tile encoder can re-emit its canvas for a joining viewer");
        Assert(encoder.EncodeCachedKeyframe(new VideoEncoderOptions(width + 2, height, 60), 0) is null,
            "cached keyframe is rejected when the stream size changed");

        var frameB = (byte[])frameA.Clone();
        Fill(frameB, width, 20, 20, 70, 70, 255, 255, 255);
        var delta = encoder.Encode(frameB, width, height, width * 4, options, 0, forceKeyframe: false);
        Assert(delta is { IsKeyframe: false } && delta.Tiles.Count == 4,
            "tile encoder sends only the four tiles that changed");
        Assert(key is not null && delta is not null && delta.ByteCount < key.ByteCount,
            "a small change is much smaller than a keyframe");

        if (key?.KeyframeJpeg is null || delta is null)
            return;

        using var compositor = new FrameCompositor();
        var appliedKey = compositor.TryApply(
            new VideoFrame(1, width, height, true, key.KeyframeJpeg, Array.Empty<VideoTile>()),
            out var composedWidth, out var composedHeight, out _);
        Assert(appliedKey && composedWidth == width && composedHeight == height,
            "keyframe composes to the stream size");

        var appliedDelta = compositor.TryApply(
            new VideoFrame(2, width, height, false, null, delta.Tiles),
            out _, out _, out var composed);
        Assert(appliedDelta, "delta tiles apply on top of the keyframe");
        Assert(Near(Pixel(composed, width, 30, 30, 0), 255, 16) &&
               Near(Pixel(composed, width, 30, 30, 1), 255, 16) &&
               Near(Pixel(composed, width, 30, 30, 2), 255, 16),
            "changed tile shows the new pixels");
        Assert(Near(Pixel(composed, width, 220, 120, 0), 30, 16) &&
               Near(Pixel(composed, width, 220, 120, 1), 90, 16) &&
               Near(Pixel(composed, width, 220, 120, 2), 180, 16),
            "unchanged area keeps its original pixels");

        // Round-trip the actual wire framing, since that is what a viewer consumes.
        using var wire = new MemoryStream();
        await Wire.SendVideoAsync(wire, key, ct);
        await Wire.SendVideoAsync(wire, delta, ct);
        wire.Position = 0;
        var wireKey = await Wire.ReceiveVideoAsync(wire, ct);
        var wireDelta = await Wire.ReceiveVideoAsync(wire, ct);
        Assert(wireKey is { IsKeyframe: true, Width: width, Height: height } &&
               wireKey.KeyframeJpeg is not null,
            "keyframe survives the wire format");
        Assert(wireDelta is { IsKeyframe: false } && wireDelta.Tiles.Count == 4 &&
               wireDelta.Width == width && wireDelta.Height == height,
            "delta tiles survive the wire format");

        if (wireKey is null || wireDelta is null)
            return;

        using var wireCompositor = new FrameCompositor();
        var wireKeyOk = wireCompositor.TryApply(wireKey, out _, out _, out _);
        var wireDeltaOk = wireCompositor.TryApply(wireDelta, out _, out _, out var wirePixels);
        Assert(wireKeyOk && wireDeltaOk && Near(Pixel(wirePixels, width, 30, 30, 1), 255, 16),
            "wire frames compose to the expected image");
        Assert(!wireCompositor.TryApply(new VideoFrame(3, width + 2, height, false, null, wireDelta.Tiles),
                out _, out _, out _),
            "compositor rejects a delta encoded for a different canvas size");

        // Regression: a host restart starts a new pipeline at sequence 1. The broadcaster is reused,
        // so it must forget the old session's sequence instead of rejecting the new frames as older.
        var hub = new FrameBroadcaster();
        hub.Publish(EncodedFrame.Delta(width, height, 5000, 0, delta.Tiles));
        hub.Reset();
        Assert(hub.Latest is null, "broadcaster reset clears the previous session");
        hub.Publish(EncodedFrame.Delta(width, height, 1, 0, delta.Tiles));
        Assert(hub.Latest?.Sequence == 1, "broadcaster accepts the first frame of a new session");
        using var subscriber = hub.Subscribe();
        hub.Publish(EncodedFrame.Delta(width, height, 2, 0, delta.Tiles));
        var received = await subscriber.WaitAsync(ct);
        Assert(received?.Sequence == 2, "subscriber receives frames after a broadcaster reset");
    }

    private static void Fill(byte[] pixels, int width, int x, int y, int fillWidth, int fillHeight,
        byte blue, byte green, byte red)
    {
        for (var row = y; row < y + fillHeight; row++)
        {
            for (var column = x; column < x + fillWidth; column++)
            {
                var offset = (row * width + column) * 4;
                pixels[offset] = blue;
                pixels[offset + 1] = green;
                pixels[offset + 2] = red;
                pixels[offset + 3] = 255;
            }
        }
    }

    private static int Pixel(byte[] pixels, int width, int x, int y, int channel) =>
        pixels[(y * width + x) * 4 + channel];

    private static bool Near(int value, int expected, int tolerance) =>
        Math.Abs(value - expected) <= tolerance;

    private static async Task TestAudioAsync(HostServer server, HostOptions options, StreamInfo info,
        NetworkStream controlStream, CancellationToken ct)
    {
        Console.WriteLine("Testing UDP audio channel...");
        if (!uint.TryParse(info.AudioId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var audioId))
        {
            Assert(false, "audio session id is a hex value");
            return;
        }

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Connect(IPAddress.Loopback, info.AudioPort);
        var bind = AudioPacket.CreateBind(info.SessionToken);
        await udp.SendAsync(bind, ct);

        var bound = false;
        for (var i = 0; i < 40 && !bound; i++)
        {
            await Task.Delay(50, ct);
            bound = server.GetClients().Any(c => c.AudioConnected);
        }

        Assert(bound, "host bound the viewer's UDP audio endpoint");
        await udp.SendAsync(bind, ct);

        var decoder = new OpusAudioDecoder();
        var pcm = new byte[AudioConstants.PcmBytesPerFrame];
        var packets = 0;
        var decodedFrames = 0;
        var sequenceGaps = 0;
        uint lastSequence = 0;
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < TimeSpan.FromSeconds(2))
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(TimeSpan.FromMilliseconds(500));

            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(readCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                continue;
            }
            catch (SocketException ex)
            {
                Console.WriteLine("  Audio socket error: " + ex.Message);
                break;
            }

            if (!AudioPacket.TryParse(result.Buffer, out var header) ||
                header.Kind != AudioPacketKind.Audio ||
                header.SessionId != audioId)
                continue;

            if (lastSequence != 0 && header.Sequence != lastSequence + 1)
                sequenceGaps++;

            lastSequence = header.Sequence;
            packets++;

            if (decoder.TryDecode(result.Buffer, AudioConstants.HeaderBytes, header.PayloadLength, pcm))
                decodedFrames++;
        }

        var snapshot = server.GetSnapshot();
        Console.WriteLine($"  Audio packets: {packets} ({decodedFrames} decoded), sequence gaps: {sequenceGaps}");
        Console.WriteLine($"  Audio status: {snapshot.AudioStatus ?? "(none)"}, {snapshot.AudioKbps:F0} kbps");

        if (packets > 0)
        {
            Assert(decodedFrames > 0, "received Opus packets decode to PCM");
            Assert(packets >= 20, "audio stream sustained at least 20 packets in 2 s");
        }
        else
        {
            Console.WriteLine("  NOTE: no audio packets arrived (quiet desktop or no capture device);");
            Console.WriteLine("        the UDP bind itself was verified, so video is unaffected.");
        }

        // Exercise the report path: the host uses it to mute a congested viewer without
        // touching the video stream.
        await Wire.SendControlAsync(controlStream, new AudioReport(packets / 2.0, 0, 0, false), ct);
        Assert(server.IsRunning, "host still runs after an audio report");

        if (packets > 0)
        {
            await Wire.SendControlAsync(controlStream, new AudioReport(packets / 2.0, 30, 0, false), ct);
            await Wire.SendControlAsync(controlStream, new AudioReport(packets / 2.0, 30, 0, false), ct);
            await Task.Delay(100, ct);
            Assert(server.GetClients().FirstOrDefault()?.AudioMuted == true,
                "host mutes audio for a congested viewer");
            Assert(server.IsRunning && server.GetSnapshot().Clients == 1,
                "video session survives an audio mute");
        }
    }

    private static async Task TestWrongPinAsync(HostOptions options, CancellationToken ct)
    {
        Console.WriteLine("Testing PIN rejection...");
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", options.Port, ct);
        var stream = client.GetStream();

        var hello = await Wire.ReceiveControlAsync(stream, ct) as ServerHello;
        Assert(hello is not null, "received ServerHello");
        Assert(hello!.RequiresPassword, "host requires a PIN");

        var wrongProof = Auth.ComputeProof("000000", hello.Nonce, "intruder", ProtocolConstants.Version);
        await Wire.SendControlAsync(stream, new AuthRequest("intruder", wrongProof), ct);

        var result = await Wire.ReceiveControlAsync(stream, ct) as AuthResult;
        Assert(result is { Ok: false }, "wrong PIN is rejected");
    }

    private static async Task TestStreamingAsync(HostServer server, HostOptions options, MonitorDescriptor monitor,
        bool testInput, bool dump, int soakSeconds, bool controllerOnly, bool audioTest, CancellationToken ct)
    {
        Console.WriteLine("Testing control handshake...");
        using var control = new TcpClient { NoDelay = true };
        await control.ConnectAsync("127.0.0.1", options.Port, ct);
        var controlStream = control.GetStream();

        var hello = await Wire.ReceiveControlAsync(controlStream, ct) as ServerHello;
        Assert(hello is not null, "received ServerHello");
        Assert(hello!.ProtocolVersion == ProtocolConstants.Version, "protocol version matches");

        var proof = Auth.ComputeProof(options.Pin, hello.Nonce, "smoke-client", ProtocolConstants.Version);
        await Wire.SendControlAsync(controlStream, new AuthRequest("smoke-client", proof), ct);

        var auth = await Wire.ReceiveControlAsync(controlStream, ct) as AuthResult;
        Assert(auth is { Ok: true }, "authentication succeeded");

        var info = await Wire.ReceiveControlAsync(controlStream, ct) as StreamInfo;
        Assert(info is not null, "received StreamInfo");
        info ??= new StreamInfo(options.VideoPort, string.Empty, 0, 0, 0, 0, string.Empty, 0, false,
            AudioConstants.SampleRate, AudioConstants.Channels, AudioConstants.FrameMilliseconds,
            AudioConstants.DefaultBitrateKbps, string.Empty);

        var expected = JpegCodec.FitSize(monitor.Width, monitor.Height, options.MaxWidth, options.MaxHeight);
        Assert(info.Width == expected.Width && info.Height == expected.Height,
            $"stream size is {info.Width}x{info.Height} (expected {expected.Width}x{expected.Height})");
        Assert(!string.IsNullOrWhiteSpace(info.SessionToken), "session token issued");

        if (audioTest)
        {
            Assert(info.AudioAvailable, "host advertises the audio channel");
            Assert(info.AudioPort == options.AudioPort, $"audio port is {options.AudioPort} (UDP)");
            Assert(!string.IsNullOrWhiteSpace(info.AudioId), "audio session id issued");
            Assert(info.AudioSampleRate == AudioConstants.SampleRate &&
                   info.AudioChannels == AudioConstants.Channels &&
                   info.AudioFrameMs == AudioConstants.FrameMilliseconds,
                "audio format is 48 kHz stereo with 10 ms frames");
        }

        Console.WriteLine("Connecting video channel...");
        using var video = new TcpClient { NoDelay = true };
        await video.ConnectAsync("127.0.0.1", info.VideoPort, ct);
        var videoStream = video.GetStream();
        await Wire.SendStringAsync(videoStream, info.SessionToken, ct);
        var accept = await Wire.ReceiveStringAsync(videoStream, ct);
        Assert(accept == "OK", "video channel accepted");

        using var compositor = new FrameCompositor();
        var frames = 0;
        var decoded = 0;
        var keyframes = 0;
        var deltas = 0;
        var bytes = 0L;
        int width = 0, height = 0;
        byte[]? lastFrame = null;
        var clock = Stopwatch.StartNew();

        var frameDeadline = Stopwatch.StartNew();
        var lastHeartbeat = Stopwatch.StartNew();
        var receivedAny = false;
        var readSeconds = soakSeconds > 0 ? soakSeconds : 6;
        while (clock.Elapsed < TimeSpan.FromSeconds(readSeconds))
        {
            // Desktop Duplication only produces frames when the screen changes, so a quiet desktop
            // must not hang the test. Bail out of an individual read instead.
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(TimeSpan.FromSeconds(soakSeconds > 0 ? 5 : 2));

            VideoFrame? frame;
            try
            {
                frame = await Wire.ReceiveVideoAsync(videoStream, readCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                break;
            }

            if (frame is null)
                break;

            receivedAny = true;
            frames++;
            bytes += frame.ByteCount;
            if (frame.IsKeyframe)
                keyframes++;
            else
                deltas++;

            if (soakSeconds > 0 && lastHeartbeat.Elapsed > TimeSpan.FromSeconds(5))
            {
                await Wire.SendControlAsync(controlStream, new Ping(Stopwatch.GetTimestamp()), ct);
                lastHeartbeat.Restart();
            }

            if (compositor.TryApply(frame, out var w, out var h, out var pixels))
            {
                decoded++;
                width = w;
                height = h;
                lastFrame = pixels;
            }

            if (soakSeconds <= 0 && frameDeadline.Elapsed > TimeSpan.FromSeconds(1) && frames >= 10)
                break;
        }

        var elapsed = clock.Elapsed.TotalSeconds;
        Console.WriteLine($"  Received {frames} frames ({keyframes} keyframes, {deltas} deltas; " +
                          $"{decoded} decoded, {width}x{height}) in {elapsed:F2}s");
        Console.WriteLine($"  Throughput: {frames / elapsed:F1} fps, {bytes * 8.0 / 1000.0 / elapsed:F0} kbps");
        Console.WriteLine($"  Average frame: {(frames > 0 ? bytes / frames / 1024 : 0)} KB");

        Assert(receivedAny && frames >= 1, "received at least one video frame");
        Assert(keyframes >= 1, "viewer starts with a keyframe");
        if (frames > 0 && frames < 3)
        {
            Console.WriteLine("  NOTE: only one or two frames arrived. Windows Graphics Capture and Desktop");
            Console.WriteLine("        Duplication are change-driven, and GDI skips identical frames, so a");
            Console.WriteLine("        static desktop legitimately stays quiet; run the test with something");
            Console.WriteLine("        moving on the captured monitor to measure fps.");
        }
        Assert(decoded >= 1, "video frames decode successfully");
        Assert(width == expected.Width && height == expected.Height, "decoded frame dimensions match");

        // Ask for a keyframe explicitly. On a static desktop the cached canvas is the only source
        // until the periodic refresh, so this covers both KeyframeRequest and the re-emit path.
        Console.WriteLine("Requesting a keyframe...");
        await Wire.SendControlAsync(controlStream, new KeyframeRequest(), ct);
        var keyframeAnswered = false;
        using (var keyframeCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            keyframeCts.CancelAfter(TimeSpan.FromSeconds(3));
            for (var attempt = 0; attempt < 120 && !keyframeAnswered; attempt++)
            {
                VideoFrame? requested;
                try
                {
                    requested = await Wire.ReceiveVideoAsync(videoStream, keyframeCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    break;
                }

                if (requested is null)
                    break;
                keyframeAnswered = requested.IsKeyframe;
            }
        }

        Assert(keyframeAnswered, "host answers KeyframeRequest with a keyframe");

        if (dump && lastFrame is not null && width > 0 && height > 0)
        {
            var path = Path.Combine(Path.GetTempPath(), "deskbeam-smoke-frame.png");
            SavePng(lastFrame, width, height, path);
            Console.WriteLine($"  Saved decoded frame: {path}");
        }

        // Exercise the gamepad path (safe: only touches the virtual pad).
        await Wire.SendControlAsync(controlStream,
            new GamepadState(1, 0x1000, 10, 20, -100, 200, 300, -400, true), ct);

        if (testInput && controllerOnly)
        {
            Console.WriteLine("  Skipping pointer injection: --controller-only disables mouse/keyboard.");
        }
        else if (testInput)
        {
            // Pointer and keyboard injection move the real cursor / type into the live desktop,
            // so only run this when explicitly requested with --input.
            await TestPointerInjectionAsync(controlStream, monitor, ct);
            await Wire.SendControlAsync(controlStream, new KeyInput(0x41, true), ct);
            await Wire.SendControlAsync(controlStream, new KeyInput(0x41, false), ct);
        }

        // Ping/pong round trip.
        var pingSent = Stopwatch.GetTimestamp();
        await Wire.SendControlAsync(controlStream, new Ping(pingSent), ct);
        Pong? pong = null;
        for (var i = 0; i < 32 && pong is null; i++)
        {
            var message = await Wire.ReceiveControlAsync(controlStream, ct);
            if (message is Pong p && p.ClientTicks == pingSent)
                pong = p;
        }

        Assert(pong is not null, "received Pong");
        if (pong is not null)
        {
            var rtt = (Stopwatch.GetTimestamp() - pong.ClientTicks) * 1000.0 / Stopwatch.Frequency;
            Console.WriteLine($"  Ping round trip: {rtt:F1} ms (host clock {pong.HostTicks})");
        }

        if (audioTest)
            await TestAudioAsync(server, options, info, controlStream, ct);

        var snapshot = server.GetSnapshot();
        Console.WriteLine($"  Host snapshot: {snapshot.CaptureFps:F1} fps, {snapshot.EncodeMs:F2} ms encode, {snapshot.Kbps:F0} kbps, {snapshot.Clients} client(s)");
        Console.WriteLine($"  Gamepad status: {snapshot.VirtualPadStatus}");

        Assert(snapshot.Clients >= 1, "host sees the connected client");
        Assert(snapshot.Width == expected.Width && snapshot.Height == expected.Height,
            "capture pipeline reports the streamed size");

        if (controllerOnly)
        {
            // Regression: a host that turns off mouse/keyboard must still create the virtual pad.
            Assert(snapshot.VirtualPadStatus?.StartsWith("Virtual Xbox 360 pad active", StringComparison.Ordinal) == true,
                "controller-only host still creates the virtual pad");
        }

        // The fps window in the snapshot may legitimately be zero if the desktop stopped changing
        // just before it was sampled; the frames the viewer already decoded prove the pipeline works.
    }

    private static async Task TestRestartAsync(HostServer server, HostOptions options, CancellationToken ct)
    {
        Console.WriteLine("Testing a host restart...");

        using var control = new TcpClient { NoDelay = true };
        await control.ConnectAsync("127.0.0.1", options.Port, ct);
        var controlStream = control.GetStream();

        var hello = await Wire.ReceiveControlAsync(controlStream, ct) as ServerHello;
        Assert(hello is not null, "restarted host sends ServerHello");
        if (hello is null)
            return;

        var proof = Auth.ComputeProof(options.Pin, hello.Nonce, "restart-client", ProtocolConstants.Version);
        await Wire.SendControlAsync(controlStream, new AuthRequest("restart-client", proof), ct);
        var auth = await Wire.ReceiveControlAsync(controlStream, ct) as AuthResult;
        Assert(auth is { Ok: true }, "restarted host accepts the PIN");

        var info = await Wire.ReceiveControlAsync(controlStream, ct) as StreamInfo;
        Assert(info is not null, "restarted host sends StreamInfo");
        if (info is null)
            return;

        using var video = new TcpClient { NoDelay = true };
        await video.ConnectAsync("127.0.0.1", info.VideoPort, ct);
        var videoStream = video.GetStream();
        await Wire.SendStringAsync(videoStream, info.SessionToken, ct);
        Assert(await Wire.ReceiveStringAsync(videoStream, ct) == "OK", "restarted video channel accepted");

        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(TimeSpan.FromSeconds(5));

        // Change-driven backends stay quiet on a static desktop. A one-pixel cursor move forces a
        // fresh capture without injecting a click or keystroke, and the position is restored.
        var moved = GetCursorPos(out var original);
        if (moved)
            SetCursorPos(original.X + 1, original.Y);

        VideoFrame? frame = null;
        try
        {
            frame = await Wire.ReceiveVideoAsync(videoStream, readCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // No frame within five seconds.
        }
        finally
        {
            if (moved)
                SetCursorPos(original.X, original.Y);
        }

        Assert(frame is { IsKeyframe: true }, "a restarted host delivers a keyframe to a new viewer");
    }

    private static async Task TestPointerInjectionAsync(NetworkStream controlStream, MonitorDescriptor monitor,
        CancellationToken ct)
    {
        Console.WriteLine("Testing pointer injection...");
        if (!GetCursorPos(out var original))
            original = default;

        var targetX = monitor.X + monitor.Width / 2;
        var targetY = monitor.Y + monitor.Height / 2;

        await Wire.SendControlAsync(controlStream, new PointerInput(PointerAction.Move, 0.5, 0.5, 0, 0), ct);
        await Task.Delay(250, ct);

        var moved = GetCursorPos(out var current);
        var dx = Math.Abs(current.X - targetX);
        var dy = Math.Abs(current.Y - targetY);
        Console.WriteLine($"  Pointer: sent center ({targetX},{targetY}), cursor at ({current.X},{current.Y})");
        Assert(moved && dx <= 16 && dy <= 16, "pointer injection moves the host cursor to the monitor center");

        SetCursorPos(original.X, original.Y);
    }

    private static void Assert(bool condition, string message)
    {
        if (condition)
        {
            Console.WriteLine("  PASS: " + message);
        }
        else
        {
            Console.WriteLine("  FAIL: " + message);
            _failures++;
        }
    }

    private static void SavePng(byte[] bgra, int width, int height, string path)
    {
        var info = new SkiaSharp.SKImageInfo(width, height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Opaque);
        using var bitmap = new SkiaSharp.SKBitmap();
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            bitmap.InstallPixels(info, handle.AddrOfPinnedObject(), width * 4);
            using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(path);
            data.SaveTo(stream);
        }
        finally
        {
            bitmap.Reset();
            handle.Free();
        }
    }

    private static readonly IntPtr DpiAwarenessPerMonitorV2 = new(-4);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }
}
