namespace AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;

public readonly record struct RicohAudioDacSample(
    ulong MasterClock,
    byte DacLevel);

public readonly record struct RicohVideoPixelSample(
    ulong Frame,
    int X,
    int Y,
    byte ColorCode,
    byte Emphasis);
