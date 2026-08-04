using AxetosOS.Products.NES.Cartridges;

namespace AxetosOS.Products.NES.Hardware;

/// <summary>
/// Electrical clock profile for an NES-compatible console. All chip clocks are
/// derived from one integer master oscillator so CPU/PPU phase never depends on
/// floating-point time or host scheduling.
/// </summary>
public sealed record NesTimingProfile(
    NesTimingMode Mode,
    string Name,
    double FramesPerSecond,
    double CpuClockHz,
    int PpuScanlines,
    int MasterClockHz,
    int PpuMasterDivisor,
    int CpuMasterDivisor)
{
    public int CpuTicksPerPpuNumerator => PpuMasterDivisor;
    public int CpuTicksPerPpuDenominator => CpuMasterDivisor;
    public double PpuClockHz => (double)MasterClockHz / PpuMasterDivisor;

    public static NesTimingProfile For(NesTimingMode mode) => mode switch
    {
        // RP2A07 PAL: 26.601712 MHz master, PPU /5, CPU /16.
        NesTimingMode.Pal => new(mode, "PAL", 50.00698, 1_662_607.0, 312, 26_601_712, 5, 16),

        // Dendy uses the PAL crystal/PPU cadence with an NTSC-like CPU divide.
        NesTimingMode.Dendy => new(mode, "Dendy", 50.00698, 1_773_448.0, 312, 26_601_712, 5, 15),

        // RP2A03/RP2C02 NTSC: 21.477272 MHz master, PPU /4, CPU /12.
        _ => new(NesTimingMode.Ntsc, "NTSC", 60.0988, 1_789_773.0, 262, 21_477_272, 4, 12)
    };
}
