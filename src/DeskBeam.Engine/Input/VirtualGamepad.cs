using DeskBeam.Core.Protocol;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace DeskBeam.Engine.Input;

/// <summary>
/// Creates a virtual Xbox 360 controller through ViGEmBus. While a remote client is connected,
/// Windows lists it like any other gamepad, so games (including XInput-based ones) pick it up.
/// </summary>
public sealed class VirtualGamepad : IDisposable
{
    private static readonly (ushort Bit, Xbox360Button Button)[] ButtonMap =
    [
        (0x0001, Xbox360Button.Up),
        (0x0002, Xbox360Button.Down),
        (0x0004, Xbox360Button.Left),
        (0x0008, Xbox360Button.Right),
        (0x0010, Xbox360Button.Start),
        (0x0020, Xbox360Button.Back),
        (0x0040, Xbox360Button.LeftThumb),
        (0x0080, Xbox360Button.RightThumb),
        (0x0100, Xbox360Button.LeftShoulder),
        (0x0200, Xbox360Button.RightShoulder),
        (0x1000, Xbox360Button.A),
        (0x2000, Xbox360Button.B),
        (0x4000, Xbox360Button.X),
        (0x8000, Xbox360Button.Y),
    ];

    private ViGEmClient? _client;
    private IXbox360Controller? _pad;
    private int _lastRumbleLeft;
    private int _lastRumbleRight;

    public VirtualGamepad()
    {
        _client = new ViGEmClient();
        _pad = _client.CreateXbox360Controller();
        _pad.FeedbackReceived += OnFeedbackReceived;
        _pad.Connect();
    }

    public event Action<byte, byte>? RumbleRequested;

    public static bool Probe(out string? error)
    {
        try
        {
            using var client = new ViGEmClient();
            var pad = client.CreateXbox360Controller();
            pad.Connect();
            pad.Disconnect();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void Update(GamepadState state)
    {
        if (_pad is null)
            return;

        try
        {
            foreach (var (bit, button) in ButtonMap)
                _pad.SetButtonState(button, (state.Buttons & bit) != 0);

            _pad.SetAxisValue(Xbox360Axis.LeftThumbX, state.LeftThumbX);
            _pad.SetAxisValue(Xbox360Axis.LeftThumbY, state.LeftThumbY);
            _pad.SetAxisValue(Xbox360Axis.RightThumbX, state.RightThumbX);
            _pad.SetAxisValue(Xbox360Axis.RightThumbY, state.RightThumbY);
            _pad.SetSliderValue(Xbox360Slider.LeftTrigger, state.LeftTrigger);
            _pad.SetSliderValue(Xbox360Slider.RightTrigger, state.RightTrigger);
            _pad.SubmitReport();
        }
        catch (Exception)
        {
            // A disconnected or removed bus should not take the session down.
        }
    }

    private void OnFeedbackReceived(object? sender, Xbox360FeedbackReceivedEventArgs e)
    {
        var left = (int)e.LargeMotor;
        var right = (int)e.SmallMotor;
        if (left == _lastRumbleLeft && right == _lastRumbleRight)
            return;

        _lastRumbleLeft = left;
        _lastRumbleRight = right;
        RumbleRequested?.Invoke((byte)left, (byte)right);
    }

    public void Dispose()
    {
        try
        {
            if (_pad is not null)
            {
                _pad.FeedbackReceived -= OnFeedbackReceived;
                _pad.Disconnect();
                _pad = null;
            }
        }
        catch (Exception)
        {
            // ignored
        }

        try
        {
            _client?.Dispose();
        }
        catch (Exception)
        {
            // ignored
        }

        _client = null;
    }
}
