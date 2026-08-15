namespace AxetosOS.Products.NES.Host.Windows;

public sealed record NativeApplicationMenuItem(int CommandId, string Text, bool IsSeparator = false)
{
    public static NativeApplicationMenuItem Separator() => new(0, string.Empty, true);
}

public sealed record NativeApplicationMenuGroup(
    string Text,
    IReadOnlyList<NativeApplicationMenuItem> Items);
