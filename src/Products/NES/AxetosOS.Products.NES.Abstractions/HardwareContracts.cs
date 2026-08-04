namespace AxetosOS.Products.NES.Abstractions;

public interface INesHardwareModule
{
    string ModuleId { get; }
    void PowerOn();
    void Reset();
}

public interface IClockedHardwareModule
{
    void Clock();
}

public interface ICpuBusDevice
{
    bool HandlesCpuAddress(ushort address);
    byte CpuRead(ushort address);
    void CpuWrite(ushort address, byte value);
}

/// <summary>
/// Optional CPU-bus extension for hardware whose write behavior depends on the
/// originating CPU cycle. Devices that do not need cycle information continue
/// to use <see cref="ICpuBusDevice.CpuWrite"/>.
/// </summary>
public interface ICpuCycleAwareBusDevice : ICpuBusDevice
{
    void CpuWrite(ushort address, byte value, ulong cpuCycle);
}

public interface IPpuBusDevice
{
    bool HandlesPpuAddress(ushort address);
    byte PpuRead(ushort address);
    void PpuWrite(ushort address, byte value);
}

public interface ISignalLine
{
    bool IsAsserted { get; }
    void Assert();
    void Release();
}

/// <summary>
/// Common inspection contract for physical memory devices. Runtime bus access
/// remains owned by the device-specific CPU/PPU interfaces; this contract lets
/// AxetosOS inspect the same live storage without introducing another execution
/// path or copying memory on every inspection.
/// </summary>
public interface IInspectableMemoryModule : INesHardwareModule
{
    int CapacityBytes { get; }
    byte ReadPhysicalByte(int offset);
    void CopyPhysicalBytes(Span<byte> destination);
}

public enum BusAccessDirection
{
    None,
    Read,
    Write
}

/// <summary>
/// Allocation-free snapshot of the most recently completed bus transaction.
/// The device reference is the live hardware object that responded first; a
/// write can have more than one participant on partially decoded addresses.
/// </summary>
public readonly record struct BusTransactionSnapshot(
    ulong Sequence,
    ushort Address,
    byte Data,
    BusAccessDirection Direction,
    object? PrimaryDevice,
    int ParticipantCount,
    ulong ClockCycle);

/// <summary>
/// Common inspection contract for live address/data buses. Implementations
/// update the snapshot in-place during normal bus activity and do not create
/// tracing objects or an alternate execution path.
/// </summary>
public interface IInspectableBusModule : INesHardwareModule
{
    int AddressWidthBits { get; }
    int DataWidthBits { get; }
    byte OpenBus { get; }
    IReadOnlyList<object> AttachedDevices { get; }
    BusTransactionSnapshot LastTransaction { get; }
}

public enum HardwareComponentKind
{
    Board,
    Chip,
    Bus,
    Memory,
    Clock,
    SignalBundle,
    InputOutput,
    DmaController,
    Cartridge,
    Internal
}

public enum HardwareConnectionKind
{
    Clock,
    CpuBus,
    PpuBus,
    Signal,
    Dma,
    CartridgeConnector,
    Internal
}

public sealed record HardwareComponentDescriptor(
    string Id,
    string DisplayName,
    HardwareComponentKind Kind,
    object Instance);

public sealed record HardwareConnectionDescriptor(
    string SourceId,
    string TargetId,
    HardwareConnectionKind Kind,
    string? Name = null);

/// <summary>
/// Describes a hardware assembly as independently addressable components and
/// explicit connections. The descriptors are intended for AxetosOS inspection,
/// visualization, tracing, and future board composition tooling.
/// </summary>
public interface IHardwareCompositeModule
{
    IReadOnlyList<HardwareComponentDescriptor> HardwareComponents { get; }
    IReadOnlyList<HardwareConnectionDescriptor> HardwareConnections { get; }
}
