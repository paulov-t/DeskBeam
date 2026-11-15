using System.Runtime.InteropServices;

namespace DeskBeam.Engine.Capture;

/// <summary>
/// Reads the Windows "SDR content brightness" level for a display. HDR desktop frames are scRGB
/// (1.0 = 80 nits), so SDR white content sits at <c>SdrWhiteNits / 80</c>; dividing by that value
/// keeps SDR windows looking exactly like they do on an SDR output.
/// </summary>
internal static class DisplayColorInfo
{
    private const uint QdcOnlyActivePaths = 0x00000002;
    private const int GetSourceName = 1;
    private const int GetSdrWhiteLevel = 11;

    /// <summary>Returns the SDR white level in nits, or 200 nits when Windows does not report it.</summary>
    public static float GetSdrWhiteNits(string? gdiDeviceName)
    {
        if (string.IsNullOrWhiteSpace(gdiDeviceName))
            return 200f;

        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0)
                    return 200f;

                var paths = new PathInfo[pathCount];
                var modes = new ModeInfo[modeCount];
                if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes,
                        IntPtr.Zero) != 0)
                    continue;

                for (var i = 0; i < pathCount; i++)
                {
                    var name = new SourceDeviceName
                    {
                        Header = new DeviceInfoHeader
                        {
                            Type = GetSourceName,
                            Size = (uint)Marshal.SizeOf<SourceDeviceName>(),
                            AdapterId = paths[i].Source.AdapterId,
                            Id = paths[i].Source.Id,
                        },
                        ViewGdiDeviceName = string.Empty,
                    };

                    if (DisplayConfigGetDeviceInfo(ref name) != 0)
                        continue;

                    if (!string.Equals(name.ViewGdiDeviceName, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var white = new SdrWhiteLevelInfo
                    {
                        Header = new DeviceInfoHeader
                        {
                            Type = GetSdrWhiteLevel,
                            Size = (uint)Marshal.SizeOf<SdrWhiteLevelInfo>(),
                            AdapterId = paths[i].Target.AdapterId,
                            Id = paths[i].Target.Id,
                        },
                    };

                    if (DisplayConfigGetDeviceInfo(ref white) != 0)
                        return 200f;

                    // The value is in thousandths of an 80-nit scRGB unit:
                    // nits = raw / 1000 * 80 (e.g. 4500 => 360 nits).
                    var nits = white.SdrWhiteLevel / 1000f * 80f;
                    return Math.Clamp(nits, 80f, 1000f);
                }

                return 200f;
            }
        }
        catch (Exception)
        {
            // Fall through to the default.
        }

        return 200f;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements,
        [Out] PathInfo[] pathInfoArray, ref uint numModeInfoArrayElements,
        [Out] ModeInfo[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName deviceInfo);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfo(ref SdrWhiteLevelInfo deviceInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public int OutputTechnology;
        public int Rotation;
        public int Scaling;
        public Rational RefreshRate;
        public int ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    // QueryDisplayConfig writes 72 bytes per path on Windows 10/11 even though the documented
    // members add up to 68. The size must match or the last entry writes past the managed array.
    [StructLayout(LayoutKind.Sequential, Size = 72)]
    private struct PathInfo
    {
        public PathSourceInfo Source;
        public PathTargetInfo Target;
    }

    [StructLayout(LayoutKind.Sequential, Size = 72)]
    private struct ModeInfo
    {
        public long Reserved0;
        public long Reserved1;
        public long Reserved2;
        public long Reserved3;
        public long Reserved4;
        public long Reserved5;
        public long Reserved6;
        public long Reserved7;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        public int Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName
    {
        public DeviceInfoHeader Header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SdrWhiteLevelInfo
    {
        public DeviceInfoHeader Header;
        public uint SdrWhiteLevel;
    }
}
