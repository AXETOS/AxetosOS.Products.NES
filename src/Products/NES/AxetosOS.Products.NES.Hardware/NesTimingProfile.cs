using AxetosOS.Products.NES.Cartridges;

namespace AxetosOS.Products.NES.Hardware;

public sealed record NesTimingProfile(
    NesTimingMode Mode,
    string Name,
    double FramesPerSecond,
    double CpuClockHz,
    int PpuScanlines,
    int CpuTicksPerPpuNumerator,
    int CpuTicksPerPpuDenominator)
{
    public static NesTimingProfile For(NesTimingMode mode) => mode switch
    {
        NesTimingMode.Pal => new(mode, "PAL", 50.00698, 1_662_607.0, 312, 5, 16),
        NesTimingMode.Dendy => new(mode, "Dendy", 50.00698, 1_773_448.0, 312, 1, 3),
        _ => new(NesTimingMode.Ntsc, "NTSC", 60.0988, 1_789_773.0, 262, 1, 3)
    };
}
