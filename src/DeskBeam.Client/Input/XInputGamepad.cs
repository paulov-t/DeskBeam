using System.Runtime.InteropServices;
using DeskBeam.Core.Protocol;

namespace DeskBeam.Client.Input;

/// <summary>
/// Reads Xbox controllers through XInput on the client PC. XInput is present on every modern
/// Windows install, so this needs no driver or extra runtime.
/// </summary>
public sealed class XInputGamepad : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepadState
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short LeftThumbX;
        public short LeftThumbY;
        public short RightThumbX;
        public short RightThumbY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepadState Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputVibration
    {
        public ushort LeftMotorSpeed;
        public ushort RightMotorSpeed;
    }

    private delegate uint XInputGetStateDelegate(int index, out XInputState state);

    private delegate uint XInputSetStateDelegate(int index, ref XInputVibration vibration);

    private readonly IntPtr _library;
    private readonly XInputGetStateDelegate? _getState;
    private readonly XInputSetStateDelegate? _setState;
    private int _activeIndex = -1;

    public XInputGamepad()
    {
        foreach (var name in new[] { "xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll" })
        {
            if (!NativeLibrary.TryLoad(name, out _library))
                continue;

            try
            {
                var getState = NativeLibrary.GetExport(_library, "XInputGetState");
                var setState = NativeLibrary.GetExport(_library, "XInputSetState");
                _getState = Marshal.GetDelegateForFunctionPointer<XInputGetStateDelegate>(getState);
                _setState = Marshal.GetDelegateForFunctionPointer<XInputSetStateDelegate>(setState);
                break;
            }
            catch (Exception)
            {
                NativeLibrary.Free(_library);
                _library = IntPtr.Zero;
            }
        }
    }

    public bool IsAvailable => _getState is not null;

    public string Status => !IsAvailable
        ? "XInput is not available on this PC."
        : _activeIndex >= 0
            ? $"Xbox controller {_activeIndex + 1} connected."
            : "No Xbox controller detected. Connect one to this PC.";

    /// <summary>Polls controller slots 1-4 and returns the first connected controller.</summary>
    public bool TryPoll(out GamepadState state)
    {
        state = new GamepadState(0, 0, 0, 0, 0, 0, 0, 0, false);
        if (_getState is null)
            return false;

        for (var i = 0; i < 4; i++)
        {
            if (_getState(i, out var native) != 0)
                continue;

            _activeIndex = i;
            state = new GamepadState(
                native.PacketNumber,
                native.Gamepad.Buttons,
                native.Gamepad.LeftTrigger,
                native.Gamepad.RightTrigger,
                native.Gamepad.LeftThumbX,
                native.Gamepad.LeftThumbY,
                native.Gamepad.RightThumbX,
                native.Gamepad.RightThumbY,
                true);
            return true;
        }

        _activeIndex = -1;
        return false;
    }

    public void SetVibration(byte leftMotor, byte rightMotor)
    {
        if (_setState is null || _activeIndex < 0)
            return;

        try
        {
            var vibration = new XInputVibration
            {
                LeftMotorSpeed = (ushort)(leftMotor * 257),
                RightMotorSpeed = (ushort)(rightMotor * 257),
            };
            _setState(_activeIndex, ref vibration);
        }
        catch (Exception)
        {
            // Controller may have been unplugged.
        }
    }

    public void Dispose()
    {
        if (_library != IntPtr.Zero)
        {
            try
            {
                NativeLibrary.Free(_library);
            }
            catch (Exception)
            {
                // ignored
            }
        }
    }
}
