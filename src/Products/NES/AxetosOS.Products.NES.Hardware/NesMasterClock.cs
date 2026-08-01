using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

public sealed class NesMasterClock
{
    private readonly IClockedHardwareModule _cpu;
    private readonly IClockedHardwareModule? _ppu;

    public NesMasterClock(IClockedHardwareModule cpu, IClockedHardwareModule? ppu = null)
    {
        _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
        _ppu = ppu;
    }

    public ulong PpuCycles { get; private set; }
    public ulong CpuCycles { get; private set; }

    public void Tick()
    {
        _ppu?.Clock();
        PpuCycles++;

        if ((PpuCycles - 1) % 3 != 0)
        {
            return;
        }

        _cpu.Clock();
        CpuCycles++;
    }
}
