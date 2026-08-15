namespace AxetosOS.Products.NES.Host.Windows;

public enum NativeKey
{
    Unknown = 0,
    Left,
    Right,
    Up,
    Down,
    Z,
    X,
    Enter,
    RightShift,
    Escape,
    Space,
    O,
    R,
    F5,
    F7,
    F11
}

[Flags]
public enum NativeKeyModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4
}

public readonly record struct NativeKeyEvent(
    NativeKey Key,
    bool Pressed,
    NativeKeyModifiers Modifiers,
    bool IsRepeat);
