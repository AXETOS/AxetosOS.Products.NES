namespace AxetosOS.Products.NES.Desktop;

internal enum NesShellCommand
{
    OpenRom,
    SaveState,
    LoadState,
    QuickSave,
    QuickLoad,
    Reset,
    TogglePause,
    ToggleFullscreen,
    LeaveFullscreen,
    Exit
}

internal static class NesShellCommandIds
{
    public const int OpenRom = 1001;
    public const int QuickSave = 1002;
    public const int QuickLoad = 1003;
    public const int Exit = 1004;
    public const int SaveState = 1005;
    public const int LoadState = 1006;
    public const int Reset = 1101;
    public const int TogglePause = 1102;
    public const int ToggleFullscreen = 1201;
}
