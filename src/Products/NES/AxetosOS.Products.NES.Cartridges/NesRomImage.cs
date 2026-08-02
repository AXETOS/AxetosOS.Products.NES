namespace AxetosOS.Products.NES.Cartridges;

public sealed record NesRomImage(
    NesHeaderFormat HeaderFormat,
    int MapperNumber,
    int? SubmapperNumber,
    int PrgRomSizeBytes,
    int ChrRomSizeBytes,
    bool HasTrainer,
    bool HasBatteryBackedMemory,
    NametableMirroring Mirroring,
    NesTimingMode HeaderTiming,
    byte[] PrgRom,
    byte[] ChrRom);

public enum NesHeaderFormat
{
    INes,
    Nes20
}

public enum NametableMirroring
{
    Horizontal,
    Vertical,
    FourScreen,
    SingleScreenLower,
    SingleScreenUpper
}

public enum NesTimingMode
{
    Unknown,
    Ntsc,
    Pal,
    MultiRegion,
    Dendy
}
