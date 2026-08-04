using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;

namespace AxetosOS.Products.NES.VirtualHardware.Loading;

public enum VirtualHardwareNesHeaderFormat
{
    INes,
    Nes20
}

public enum VirtualHardwareNesMirroring
{
    Horizontal,
    Vertical,
    FourScreen
}

public enum VirtualHardwareNesHeaderTiming
{
    Unknown,
    Ntsc,
    Pal,
    MultiRegion,
    Dendy
}

/// <summary>
/// Immutable cartridge metadata consumed by the VirtualHardware host. This is
/// deliberately separate from the playable emulator's cartridge runtime.
/// </summary>
public sealed record VirtualHardwareNesRomImage(
    VirtualHardwareNesHeaderFormat HeaderFormat,
    int MapperNumber,
    int? SubmapperNumber,
    int PrgRomSizeBytes,
    int ChrRomSizeBytes,
    bool HasTrainer,
    bool HasBatteryBackedMemory,
    VirtualHardwareNesMirroring Mirroring,
    VirtualHardwareNesHeaderTiming HeaderTiming,
    byte[] PrgRom,
    byte[] ChrRom);

public enum NesRegionSelection
{
    Auto,
    NtscNorthAmerica,
    NtscJapan,
    Pal
}

public enum NesRegionSelectionSource
{
    ManualOverride,
    Nes20Header,
    INesHeader,
    FileName,
    Default
}

public sealed record NesResolvedRegion(
    NesHardwareRegion Region,
    NesRegionSelectionSource Source,
    string Reason);
