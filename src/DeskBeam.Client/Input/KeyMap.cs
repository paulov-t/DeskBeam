using Avalonia.Input;

namespace DeskBeam.Client.Input;

/// <summary>Maps Avalonia keys to Windows virtual-key codes for remote injection.</summary>
public static class KeyMap
{
    public static int ToVirtualKey(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
            return 0x41 + (key - Key.A);

        if (key is >= Key.D0 and <= Key.D9)
            return 0x30 + (key - Key.D0);

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
            return 0x60 + (key - Key.NumPad0);

        if (key is >= Key.F1 and <= Key.F12)
            return 0x70 + (key - Key.F1);

        return key switch
        {
            Key.Back => 0x08,
            Key.Tab => 0x09,
            Key.Clear => 0x0C,
            Key.Enter => 0x0D,
            Key.Pause => 0x13,
            Key.CapsLock => 0x14,
            Key.Escape => 0x1B,
            Key.Space => 0x20,
            Key.PageUp => 0x21,
            Key.PageDown => 0x22,
            Key.End => 0x23,
            Key.Home => 0x24,
            Key.Left => 0x25,
            Key.Up => 0x26,
            Key.Right => 0x27,
            Key.Down => 0x28,
            Key.Select => 0x29,
            Key.Print => 0x2A,
            Key.Execute => 0x2B,
            Key.PrintScreen => 0x2C,
            Key.Insert => 0x2D,
            Key.Delete => 0x2E,
            Key.Help => 0x2F,
            Key.LeftShift => 0xA0,
            Key.RightShift => 0xA1,
            Key.LeftCtrl => 0xA2,
            Key.RightCtrl => 0xA3,
            Key.LeftAlt => 0xA4,
            Key.RightAlt => 0xA5,
            Key.LWin => 0x5B,
            Key.RWin => 0x5C,
            Key.Apps => 0x5D,
            Key.Sleep => 0x5F,
            Key.Multiply => 0x6A,
            Key.Add => 0x6B,
            Key.Separator => 0x6C,
            Key.Subtract => 0x6D,
            Key.Decimal => 0x6E,
            Key.Divide => 0x6F,
            Key.NumLock => 0x90,
            Key.Scroll => 0x91,
            Key.OemSemicolon => 0xBA,
            Key.OemPlus => 0xBB,
            Key.OemComma => 0xBC,
            Key.OemMinus => 0xBD,
            Key.OemPeriod => 0xBE,
            Key.Oem2 => 0xBF,
            Key.Oem3 => 0xC0,
            Key.Oem4 => 0xDB,
            Key.Oem5 => 0xDC,
            Key.Oem6 => 0xDD,
            Key.Oem7 => 0xDE,
            Key.Oem8 => 0xDF,
            Key.Oem102 => 0xE2,
            _ => 0,
        };
    }
}
