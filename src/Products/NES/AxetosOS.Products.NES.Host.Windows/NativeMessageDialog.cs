using System.Runtime.InteropServices;

namespace AxetosOS.Products.NES.Host.Windows;

public static class NativeMessageDialog
{
    public static void ShowError(string title, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        MessageBoxW(IntPtr.Zero, message, title, 0x00000010 | 0x00000000);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
