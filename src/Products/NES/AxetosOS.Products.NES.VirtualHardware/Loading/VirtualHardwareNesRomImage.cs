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
    byte[] ChrRom)
{
    /// <summary>Volatile PRG RAM described by the cartridge image, in bytes.</summary>
    public int PrgRamSizeBytes { get; init; } = -1;

    /// <summary>Battery/nonvolatile PRG RAM described by the cartridge image, in bytes.</summary>
    public int PrgNvRamSizeBytes { get; init; } = -1;

    /// <summary>Volatile CHR RAM described by the cartridge image, in bytes.</summary>
    public int ChrRamSizeBytes { get; init; } = -1;

    /// <summary>Nonvolatile CHR RAM described by the cartridge image, in bytes.</summary>
    public int ChrNvRamSizeBytes { get; init; } = -1;

    /// <summary>
    /// True when the file format explicitly describes RAM/NVRAM capacities.
    /// NES 2.0 does; legacy iNES metadata remains compatibility-oriented.
    /// </summary>
    public bool HasExplicitRamSizes { get; init; }

    public int TotalPrgRamSizeBytes =>
        Math.Max(0, PrgRamSizeBytes) + Math.Max(0, PrgNvRamSizeBytes);

    public int TotalChrRamSizeBytes =>
        Math.Max(0, ChrRamSizeBytes) + Math.Max(0, ChrNvRamSizeBytes);
}

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
