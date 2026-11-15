using System.Runtime.InteropServices;
using DeskBeam.Core.Models;
using DeskBeam.Core.Protocol;

namespace DeskBeam.Engine.Input;

/// <summary>Injects remote mouse and keyboard input into the local desktop with SendInput.</summary>
public sealed class DesktopInjector
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    private const uint MouseMove = 0x0001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseMiddleDown = 0x0020;
    private const uint MouseMiddleUp = 0x0040;
    private const uint MouseXDown = 0x0080;
    private const uint MouseXUp = 0x0100;
    private const uint MouseWheel = 0x0800;
    private const uint MouseAbsolute = 0x8000;
    private const uint MouseVirtualDesk = 0x4000;
    private const uint KeyUp = 0x0002;
    private const uint KeyExtended = 0x0001;

    private static readonly HashSet<int> ExtendedKeys =
    [
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, // PgUp/PgDn/End/Home/arrows
        0x2C, 0x2D, 0x2E, // PrintScreen/Insert/Delete
        0x5B, 0x5C, 0x5D, // Left/Right Win, Apps
        0x6F, // Numpad divide
        0x90, // NumLock
        0xA3, 0xA5, // Right Ctrl / Right Alt
    ];

    private readonly int _monitorX;
    private readonly int _monitorY;
    private readonly int _monitorWidth;
    private readonly int _monitorHeight;
    private readonly HashSet<int> _pressedKeys = [];
    private readonly HashSet<int> _pressedButtons = [];

    public DesktopInjector(MonitorDescriptor monitor)
    {
        _monitorX = monitor.X;
        _monitorY = monitor.Y;
        _monitorWidth = Math.Max(1, monitor.Width);
        _monitorHeight = Math.Max(1, monitor.Height);
    }

    public void Apply(PointerInput input)
    {
        switch (input.Action)
        {
            case PointerAction.Move:
                MoveTo(input.X, input.Y);
                break;
            case PointerAction.Down:
                MouseButton(input.Button, true);
                break;
            case PointerAction.Up:
                MouseButton(input.Button, false);
                break;
            case PointerAction.Wheel:
                Wheel(input.Wheel);
                break;
        }
    }

    public void MoveTo(double normalizedX, double normalizedY)
    {
        var x = Math.Clamp(normalizedX, 0, 1);
        var y = Math.Clamp(normalizedY, 0, 1);

        var virtualX = GetSystemMetrics(76);
        var virtualY = GetSystemMetrics(77);
        var virtualWidth = Math.Max(1, GetSystemMetrics(78));
        var virtualHeight = Math.Max(1, GetSystemMetrics(79));

        var screenX = _monitorX + x * _monitorWidth;
        var screenY = _monitorY + y * _monitorHeight;

        var absoluteX = (int)Math.Round((screenX - virtualX) * 65535.0 / Math.Max(1, virtualWidth - 1));
        var absoluteY = (int)Math.Round((screenY - virtualY) * 65535.0 / Math.Max(1, virtualHeight - 1));

        SendMouse(MouseMove | MouseAbsolute | MouseVirtualDesk, absoluteX, absoluteY, 0);
    }

    public void MouseButton(int button, bool down)
    {
        var (flag, data) = button switch
        {
            1 => (down ? MouseLeftDown : MouseLeftUp, 0),
            2 => (down ? MouseRightDown : MouseRightUp, 0),
            3 => (down ? MouseMiddleDown : MouseMiddleUp, 0),
            4 => (down ? MouseXDown : MouseXUp, 1),
            5 => (down ? MouseXDown : MouseXUp, 2),
            _ => (0u, 0),
        };

        if (flag != 0)
        {
            if (down)
                _pressedButtons.Add(button);
            else
                _pressedButtons.Remove(button);

            SendMouse(flag, 0, 0, data);
        }
    }

    public void Wheel(int delta)
    {
        var clamped = Math.Clamp(delta, -1200, 1200);
        if (clamped != 0)
            SendMouse(MouseWheel, 0, 0, clamped);
    }

    public void Key(int virtualKey, bool pressed)
    {
        if (virtualKey is <= 0 or > 0xFF)
            return;

        if (pressed)
            _pressedKeys.Add(virtualKey);
        else
            _pressedKeys.Remove(virtualKey);

        var scan = MapVirtualKey((uint)virtualKey, 0);
        var flags = (pressed ? 0u : KeyUp) | (ExtendedKeys.Contains(virtualKey) ? KeyExtended : 0u);

        var input = new Input
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = (ushort)virtualKey,
                    ScanCode = (ushort)scan,
                    Flags = flags,
                },
            },
        };

        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    /// <summary>Releases every key and mouse button this injector pressed, e.g. on disconnect.</summary>
    public void ReleaseAll()
    {
        foreach (var key in _pressedKeys.ToArray())
            Key(key, false);

        foreach (var button in _pressedButtons.ToArray())
            MouseButton(button, false);
    }

    private static void SendMouse(uint flags, int dx, int dy, int data)
    {
        var input = new Input
        {
            Type = InputMouse,
            Union = new InputUnion
            {
                Mouse = new MouseInput
                {
                    Dx = dx,
                    Dy = dy,
                    MouseData = unchecked((uint)data),
                    Flags = flags,
                },
            },
        };

        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
}
