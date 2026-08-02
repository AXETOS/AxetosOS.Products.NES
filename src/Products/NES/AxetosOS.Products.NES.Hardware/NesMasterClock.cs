using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

public sealed class NesMasterClock
{
    private readonly IClockedHardwareModule _cpu;
    private readonly IClockedHardwareModule? _ppu;
    private readonly IClockedHardwareModule? _apu;
    private readonly int _cpuNumerator;
    private readonly int _cpuDenominator;
    private int _cpuAccumulator;

    public NesMasterClock(IClockedHardwareModule cpu, IClockedHardwareModule? ppu = null, IClockedHardwareModule? apu = null, NesTimingProfile? timing = null)
    {
        _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
        _ppu = ppu;
        _apu = apu;
        var profile = timing ?? NesTimingProfile.For(AxetosOS.Products.NES.Cartridges.NesTimingMode.Ntsc);
        _cpuNumerator = profile.CpuTicksPerPpuNumerator;
        _cpuDenominator = profile.CpuTicksPerPpuDenominator;
    }

    public ulong PpuCycles { get; private set; }
    public ulong CpuCycles { get; private set; }

    public void Tick()
    {
        _ppu?.Clock();
        PpuCycles++;

        _cpuAccumulator += _cpuNumerator;
        if (_cpuAccumulator < _cpuDenominator) return;
        _cpuAccumulator -= _cpuDenominator;

        _apu?.Clock();
        _cpu.Clock();
        CpuCycles++;
    }
}
