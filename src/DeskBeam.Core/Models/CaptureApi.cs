namespace DeskBeam.Core.Models;

/// <summary>Which Windows screen-capture API the host should use.</summary>
public enum CaptureApi
{
    /// <summary>Pick the best available API (Windows Graphics Capture, then Desktop Duplication, then GDI).</summary>
    Auto = 0,

    /// <summary>Windows.Graphics.Capture (DWM). Survives display-mode changes and usually captures fullscreen games.</summary>
    WindowsGraphicsCapture = 1,

    /// <summary>DXGI Desktop Duplication. Lowest overhead for a static desktop, but loses the output on mode changes.</summary>
    DesktopDuplication = 2,

    /// <summary>GDI BitBlt. Slow compatibility fallback.</summary>
    Gdi = 3,
}
