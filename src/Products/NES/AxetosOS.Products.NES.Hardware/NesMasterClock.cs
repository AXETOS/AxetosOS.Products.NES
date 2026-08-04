using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

/// <summary>
/// Integer master-oscillator scheduler shared by the CPU, PPU, and APU.
/// <see cref="Tick"/> advances exactly one PPU clock for host compatibility,
/// while internally preserving the exact master-clock phase of every chip.
/// </summary>
public sealed class NesMasterClock
{
    private readonly IClockedHardwareModule _cpu;
    private readonly IClockedHardwareModule? _ppu;
    private readonly IClockedHardwareModule? _apu;
    private readonly int _ppuDivisor;
    private readonly int _cpuDivisor;
    private int _ppuPhase;
    private int _cpuPhase;

    public NesMasterClock(
        IClockedHardwareModule cpu,
        IClockedHardwareModule? ppu = null,
        IClockedHardwareModule? apu = null,
        NesTimingProfile? timing = null)
    {
        _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
        _ppu = ppu;
        _apu = apu;
        Timing = timing ?? NesTimingProfile.For(AxetosOS.Products.NES.Cartridges.NesTimingMode.Ntsc);
        _ppuDivisor = Timing.PpuMasterDivisor;
        _cpuDivisor = Timing.CpuMasterDivisor;

        if (_ppuDivisor <= 0) throw new ArgumentOutOfRangeException(nameof(timing), "PPU master divisor must be positive.");
        if (_cpuDivisor <= 0) throw new ArgumentOutOfRangeException(nameof(timing), "CPU master divisor must be positive.");

        Oscillator = new NesMasterOscillatorComponent(this);
        PpuDivider = new NesClockDividerComponent(this, ppu: true);
        CpuDivider = new NesClockDividerComponent(this, ppu: false);
    }

    public NesTimingProfile Timing { get; }
    public NesMasterOscillatorComponent Oscillator { get; }
    public NesClockDividerComponent PpuDivider { get; }
    public NesClockDividerComponent CpuDivider { get; }
    public ulong MasterCycles { get; private set; }
    public ulong PpuCycles { get; private set; }
    public ulong CpuCycles { get; private set; }
    public int PpuMasterPhase => _ppuPhase;
    public int CpuMasterPhase => _cpuPhase;

    /// <summary>Advances one raw crystal/master-oscillator cycle.</summary>
    public void TickMaster()
    {
        MasterCycles++;
        _ppuPhase++;
        _cpuPhase++;

        var ppuEdge = _ppuPhase == _ppuDivisor;
        var cpuEdge = _cpuPhase == _cpuDivisor;

        if (ppuEdge)
        {
            _ppuPhase = 0;
            _ppu?.Clock();
            PpuCycles++;
        }

        if (cpuEdge)
        {
            _cpuPhase = 0;
            // The APU is part of the RP2A03 and advances on the CPU clock domain.
            _apu?.Clock();
            _cpu.Clock();
            CpuCycles++;
        }
    }

    /// <summary>
    /// Advances to the next PPU edge. This keeps the existing host loop API while
    /// retaining exact NTSC, PAL, and Dendy CPU/PPU phase relationships.
    /// </summary>
    public void Tick()
    {
        // The host advances the machine one PPU edge at a time. Calling
        // TickMaster once for every intervening crystal edge multiplied the
        // hot-loop call count by four or five million calls per second. Advance
        // the same integer phases in one step instead; this is mathematically
        // identical because every supported CPU divisor is greater than the
        // PPU divisor, so at most one CPU edge can occur before the next PPU
        // edge. When both edges coincide, the PPU is clocked first just as it is
        // in TickMaster.
        var masterCyclesToPpuEdge = _ppuDivisor - _ppuPhase;

        MasterCycles += (ulong)masterCyclesToPpuEdge;
        _ppuPhase = 0;
        _cpuPhase += masterCyclesToPpuEdge;

        _ppu?.Clock();
        PpuCycles++;

        if (_cpuPhase >= _cpuDivisor)
        {
            _cpuPhase -= _cpuDivisor;
            _apu?.Clock();
            _cpu.Clock();
            CpuCycles++;
        }
    }
    /// <summary>
    /// Advances the connected RP2C02 until it completes exactly one frame.
    /// All CPU, PPU, and APU edges still pass through <see cref="Tick"/>; the
    /// loop lives here only to remove tens of thousands of host call boundaries
    /// from every displayed frame.
    /// </summary>
    public void TickFrame(Rp2C02Ppu ppu)
    {
        ArgumentNullException.ThrowIfNull(ppu);
        if (!ReferenceEquals(_ppu, ppu))
            throw new ArgumentException("The supplied PPU is not connected to this master clock.", nameof(ppu));

        var startingFrame = ppu.Frame;
        do
        {
            Tick();
        }
        while (ppu.Frame == startingFrame);
    }

}
