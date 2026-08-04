using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

/// <summary>
/// Allocation-free point-in-time view of the most important live motherboard
/// state. The snapshot contains values only; all execution remains owned by the
/// connected hardware components.
/// </summary>
public readonly record struct NesHardwareInspectionSnapshot(
    bool IsPowered,
    ulong MasterCycles,
    ulong CpuClockCycles,
    ulong PpuClockCycles,
    ulong CpuCoreCycles,
    ushort ProgramCounter,
    byte CurrentOpcode,
    bool AtInstructionBoundary,
    int PpuScanline,
    int PpuDot,
    ulong PpuFrame,
    BusTransactionSnapshot CpuBus,
    BusTransactionSnapshot PpuBus,
    bool NmiAsserted,
    bool IrqAsserted,
    bool ResetAsserted,
    bool RdyAsserted);

/// <summary>
/// Stable lookup and snapshot boundary for AxetosOS hardware inspection tools.
/// It indexes the descriptors already published by the motherboard and reads
/// state directly from the live machine without introducing a parallel model.
/// </summary>
public sealed class NesHardwareInspectionRegistry
{
    private readonly NesMotherboard _motherboard;
    private readonly Dictionary<string, HardwareComponentDescriptor> _componentsById;

    internal NesHardwareInspectionRegistry(
        NesMotherboard motherboard,
        IReadOnlyList<HardwareComponentDescriptor> components,
        IReadOnlyList<HardwareConnectionDescriptor> connections)
    {
        _motherboard = motherboard ?? throw new ArgumentNullException(nameof(motherboard));
        Components = components ?? throw new ArgumentNullException(nameof(components));
        Connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _componentsById = new Dictionary<string, HardwareComponentDescriptor>(components.Count, StringComparer.Ordinal);

        foreach (var component in components)
        {
            // Composite packages and the motherboard can both describe the same
            // live part. The first descriptor is the canonical registry entry;
            // repeated descriptions do not create another hardware instance.
            _componentsById.TryAdd(component.Id, component);
        }
    }

    public IReadOnlyList<HardwareComponentDescriptor> Components { get; }
    public IReadOnlyList<HardwareConnectionDescriptor> Connections { get; }
    public int ComponentCount => Components.Count;
    public int RegisteredComponentCount => _componentsById.Count;
    public int ConnectionCount => Connections.Count;

    public bool TryGetComponent(string moduleId, out HardwareComponentDescriptor component)
    {
        ArgumentNullException.ThrowIfNull(moduleId);
        return _componentsById.TryGetValue(moduleId, out component!);
    }

    public HardwareComponentDescriptor GetRequiredComponent(string moduleId)
    {
        ArgumentNullException.ThrowIfNull(moduleId);
        return _componentsById.TryGetValue(moduleId, out var component)
            ? component
            : throw new KeyNotFoundException($"NES hardware component '{moduleId}' is not registered.");
    }

    public NesHardwareInspectionSnapshot CaptureSnapshot() => new(
        _motherboard.ConsoleIo.PowerSwitch.IsPowered,
        _motherboard.Clock.MasterCycles,
        _motherboard.Clock.CpuCycles,
        _motherboard.Clock.PpuCycles,
        _motherboard.Cpu.TotalCycles,
        _motherboard.Cpu.ProgramCounter,
        _motherboard.Cpu.LastOpcode,
        _motherboard.Cpu.IsInstructionBoundary,
        _motherboard.Ppu.Scanline,
        _motherboard.Ppu.Dot,
        _motherboard.Ppu.Frame,
        _motherboard.CpuBus.LastTransaction,
        _motherboard.PpuBus.LastTransaction,
        _motherboard.CpuSignals.Nmi.IsAsserted,
        _motherboard.CpuSignals.Irq.IsAsserted,
        _motherboard.CpuSignals.Reset.IsAsserted,
        _motherboard.CpuSignals.Rdy.IsAsserted);
}
