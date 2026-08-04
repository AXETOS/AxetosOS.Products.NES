using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

public sealed class NesMasterOscillatorComponent : INesHardwareModule
{
    private readonly NesMasterClock _clock;
    internal NesMasterOscillatorComponent(NesMasterClock clock) => _clock = clock;
    public string ModuleId => "nes.clock.oscillator";
    public int FrequencyHz => _clock.Timing.MasterClockHz;
    public ulong EdgeCount => _clock.MasterCycles;
    public void PowerOn() { }
    public void Reset() { }
}

public sealed class NesClockDividerComponent : INesHardwareModule
{
    private readonly NesMasterClock _clock;
    private readonly bool _ppu;
    internal NesClockDividerComponent(NesMasterClock clock, bool ppu)
    {
        _clock = clock;
        _ppu = ppu;
    }

    public string ModuleId => _ppu ? "nes.clock.divider.ppu" : "nes.clock.divider.cpu";
    public int Divisor => _ppu ? _clock.Timing.PpuMasterDivisor : _clock.Timing.CpuMasterDivisor;
    public int Phase => _ppu ? _clock.PpuMasterPhase : _clock.CpuMasterPhase;
    public ulong OutputEdges => _ppu ? _clock.PpuCycles : _clock.CpuCycles;
    public double OutputFrequencyHz => (double)_clock.Timing.MasterClockHz / Divisor;
    public void PowerOn() { }
    public void Reset() { }
}

public sealed class NesSignalLineComponent : INesHardwareModule
{
    private readonly SignalLine _line;
    internal NesSignalLineComponent(string moduleId, string pinName, SignalLine line, bool activeLow)
    {
        ModuleId = moduleId;
        PinName = pinName;
        _line = line;
        ActiveLow = activeLow;
    }

    public string ModuleId { get; }
    public string PinName { get; }
    public bool ActiveLow { get; }
    public bool IsAsserted => _line.IsAsserted;
    public bool PhysicalLevelHigh => ActiveLow ? !IsAsserted : IsAsserted;
    public SignalLine Line => _line;
    public void PowerOn() { }
    public void Reset() { }
}

public sealed class NesClockNetworkPackage : INesHardwareModule, IHardwareCompositeModule
{
    private readonly HardwareComponentDescriptor[] _components;
    private readonly HardwareConnectionDescriptor[] _connections;

    public NesClockNetworkPackage(NesMasterClock clock)
    {
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Oscillator = clock.Oscillator;
        PpuDivider = clock.PpuDivider;
        CpuDivider = clock.CpuDivider;
        _components =
        [
            new(ModuleId, "NES clock network", HardwareComponentKind.Clock, this),
            new(Oscillator.ModuleId, "Master crystal oscillator", HardwareComponentKind.Clock, Oscillator),
            new(PpuDivider.ModuleId, "PPU clock divider", HardwareComponentKind.Clock, PpuDivider),
            new(CpuDivider.ModuleId, "CPU/APU clock divider", HardwareComponentKind.Clock, CpuDivider)
        ];
        _connections =
        [
            new(Oscillator.ModuleId, PpuDivider.ModuleId, HardwareConnectionKind.Clock, $"divide by {PpuDivider.Divisor}"),
            new(Oscillator.ModuleId, CpuDivider.ModuleId, HardwareConnectionKind.Clock, $"divide by {CpuDivider.Divisor}")
        ];
    }

    public string ModuleId => "nes.clock.network";
    public NesMasterClock Clock { get; }
    public NesMasterOscillatorComponent Oscillator { get; }
    public NesClockDividerComponent PpuDivider { get; }
    public NesClockDividerComponent CpuDivider { get; }
    public IReadOnlyList<HardwareComponentDescriptor> HardwareComponents => _components;
    public IReadOnlyList<HardwareConnectionDescriptor> HardwareConnections => _connections;
    public void PowerOn() { }
    public void Reset() { }
}

public sealed class Rp2A03SignalNetworkPackage : INesHardwareModule, IHardwareCompositeModule
{
    private readonly HardwareComponentDescriptor[] _components;
    private readonly HardwareConnectionDescriptor[] _connections;

    public Rp2A03SignalNetworkPackage(Rp2A03SignalLines lines, IrqLineCombiner irqCombiner)
    {
        Lines = lines ?? throw new ArgumentNullException(nameof(lines));
        IrqCombiner = irqCombiner ?? throw new ArgumentNullException(nameof(irqCombiner));
        Nmi = new("nes.signal.nmi", "NMI", lines.Nmi, activeLow: true);
        Irq = new("nes.signal.irq", "IRQ", lines.Irq, activeLow: true);
        Reset = new("nes.signal.reset", "RESET", lines.Reset, activeLow: true);
        Rdy = new("nes.signal.rdy", "RDY", lines.Rdy, activeLow: false);
        _components =
        [
            new(ModuleId, "RP2A03 signal network", HardwareComponentKind.SignalBundle, this),
            new(Nmi.ModuleId, "NMI line", HardwareComponentKind.SignalBundle, Nmi),
            new(Irq.ModuleId, "IRQ line", HardwareComponentKind.SignalBundle, Irq),
            new(Reset.ModuleId, "RESET line", HardwareComponentKind.SignalBundle, Reset),
            new(Rdy.ModuleId, "RDY line", HardwareComponentKind.SignalBundle, Rdy),
            new("nes.signal.irq-combiner", "IRQ wired-OR combiner", HardwareComponentKind.Internal, irqCombiner)
        ];
        _connections =
        [
            new("nes.signal.irq-combiner", Irq.ModuleId, HardwareConnectionKind.Signal, "combined level-sensitive IRQ")
        ];
    }

    public string ModuleId => "nes.signal.network.rp2a03";
    public Rp2A03SignalLines Lines { get; }
    public IrqLineCombiner IrqCombiner { get; }
    public NesSignalLineComponent Nmi { get; }
    public NesSignalLineComponent Irq { get; }
    public NesSignalLineComponent Reset { get; }
    public NesSignalLineComponent Rdy { get; }
    public IReadOnlyList<HardwareComponentDescriptor> HardwareComponents => _components;
    public IReadOnlyList<HardwareConnectionDescriptor> HardwareConnections => _connections;
    public void PowerOn() { }
    void INesHardwareModule.Reset() { }
}
