namespace AxetosOS.Products.NES.VirtualHardware.Boards.Nes;

/// <summary>
/// Immutable motherboard timing values for a regional NES hardware family.
/// Frequencies describe the clocks presented to chips; raster dimensions
/// describe the connected PPU timing generator.
/// </summary>
public sealed record NesHardwareTimingProfile(
    NesHardwareRegion Region,
    string DisplayName,
    long CpuClockHertz,
    long PpuClockHertz,
    int PpuHalfCyclesPerCpuHalfCycleNumerator,
    int PpuHalfCyclesPerCpuHalfCycleDenominator,
    int DotsPerScanline,
    int ScanlinesPerFrame,
    int VblankStartScanline,
    int PreRenderScanline)
{
    public static NesHardwareTimingProfile NtscNorthAmerica { get; } = new(
        NesHardwareRegion.NtscNorthAmerica,
        "NTSC-U",
        1_789_773,
        5_369_318,
        3,
        1,
        341,
        262,
        241,
        261);

    public static NesHardwareTimingProfile NtscJapan { get; } = new(
        NesHardwareRegion.NtscJapan,
        "NTSC-J",
        1_789_773,
        5_369_318,
        3,
        1,
        341,
        262,
        241,
        261);

    public static NesHardwareTimingProfile Pal { get; } = new(
        NesHardwareRegion.Pal,
        "PAL",
        1_662_607,
        5_320_342,
        16,
        5,
        341,
        312,
        241,
        311);

    public static NesHardwareTimingProfile For(NesHardwareRegion region) => region switch
    {
        NesHardwareRegion.NtscNorthAmerica => NtscNorthAmerica,
        NesHardwareRegion.NtscJapan => NtscJapan,
        NesHardwareRegion.Pal => Pal,
        _ => throw new ArgumentOutOfRangeException(nameof(region), region, "Unknown NES hardware region.")
    };
}
