using DeskBeam.Core.Models;

namespace DeskBeam.Engine.Capture;

public static class ScreenCaptureFactory
{
    /// <summary>Lists monitors, preferring DXGI ordering (which matches the duplication indices).</summary>
    public static IReadOnlyList<MonitorDescriptor> EnumerateMonitors()
    {
        try
        {
            var monitors = DxgiCapturer.EnumerateMonitors();
            if (monitors.Count > 0)
                return monitors;
        }
        catch (Exception)
        {
            // Fall through to GDI.
        }

        return GdiCapturer.EnumerateMonitors();
    }

    public static string Describe(CaptureApi api) => api switch
    {
        CaptureApi.WindowsGraphicsCapture => "Windows Graphics Capture",
        CaptureApi.DesktopDuplication => "DXGI Desktop Duplication",
        CaptureApi.Gdi => "GDI BitBlt",
        _ => "automatic capture",
    };

    /// <summary>
    /// Creates the best capturer for the requested API. If an explicit choice is unavailable the
    /// remaining APIs are tried in order, so the host never ends up with a dead stream because a
    /// driver or a Windows build lacks one capture path.
    /// </summary>
    public static ICapturer Create(MonitorDescriptor monitor, CaptureApi api, bool includeCursor,
        Action<string>? log = null)
    {
        var order = api switch
        {
            CaptureApi.WindowsGraphicsCapture =>
                new CaptureApi[] { CaptureApi.WindowsGraphicsCapture, CaptureApi.DesktopDuplication, CaptureApi.Gdi },
            CaptureApi.DesktopDuplication =>
                new CaptureApi[] { CaptureApi.DesktopDuplication, CaptureApi.WindowsGraphicsCapture, CaptureApi.Gdi },
            CaptureApi.Gdi => new CaptureApi[] { CaptureApi.Gdi },
            _ => new CaptureApi[] { CaptureApi.WindowsGraphicsCapture, CaptureApi.DesktopDuplication, CaptureApi.Gdi },
        };

        Exception? last = null;
        foreach (var candidate in order)
        {
            try
            {
                ICapturer capturer = candidate switch
                {
                    CaptureApi.WindowsGraphicsCapture => new WgcCapturer(monitor, includeCursor, log),
                    CaptureApi.DesktopDuplication => new DxgiCapturer(monitor.Index, monitor.DeviceName,
                        includeCursor, log),
                    _ => new GdiCapturer(monitor, includeCursor),
                };

                if (candidate != api && api != CaptureApi.Auto)
                    log?.Invoke($"Falling back to {Describe(candidate)}.");

                return capturer;
            }
            catch (Exception ex)
            {
                last = ex;
                log?.Invoke($"{Describe(candidate)} unavailable ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        throw new InvalidOperationException("No screen-capture API is available.", last);
    }
}
