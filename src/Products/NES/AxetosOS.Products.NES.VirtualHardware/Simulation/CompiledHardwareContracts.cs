using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Simulation;

/// <summary>
/// Product-agnostic contracts consumed by the hardware-lab compiler.  Components
/// describe only their own package pins and compilable physical behavior.  The
/// compiler never receives board/product semantics, address-map names, or mapper
/// knowledge; every cross-package optimization is derived from these contracts
/// plus the assembled physical netlist.
/// </summary>
public readonly record struct CompiledDriveState(
    DigitalLevel Level,
    DigitalDriveStrength Strength = DigitalDriveStrength.Strong);

public enum CompiledBusReadPhase : byte
{
    Begin,
    Complete
}

public enum CompiledBusWritePhase : byte
{
    Begin,
    Complete
}

/// <summary>
/// Physical edge at which package-local circuitry observes a bus cycle.
/// Begin corresponds to the active bus window opening; Complete corresponds
/// to the closing edge after read data has been sampled and before any
/// end-of-cycle write latch consumes the bus value.
/// </summary>
public enum CompiledBusCycleObservationPhase : byte
{
    Begin,
    Complete
}

public readonly record struct CompiledPinCondition(DigitalPin Pin, DigitalLevel RequiredLevel);

public interface ICompiledBusFabric
{
    ulong ClockRisingEdges { get; }
    bool InterruptRequestLow { get; }

    void BeginRead(ushort address);
    bool CompleteRead(ushort address, out byte value);
    void Write(ushort address, byte value);
    void CompleteCycle();

    bool HasCompleteBusCycleObservers { get; }
    void ObserveCompleteBusCycle(bool writeCycle);

    byte ReadSerialInput(int channel);
    void WriteParallelOutputs(byte value);
    void PresentOutputSignal(DigitalPin sourcePin, DigitalLevel level);
}

public sealed class CompiledBusMasterDescriptor
{
    public CompiledBusMasterDescriptor(
        VirtualHardwareComponent component,
        IReadOnlyList<DigitalPin> addressPins,
        IReadOnlyList<DigitalPin> dataPins,
        Func<DigitalPin, uint, bool, CompiledDriveState?> evaluateDrivenPin,
        Action<ICompiledBusFabric> attachFabric,
        Action detachFabric,
        IReadOnlyList<DigitalPin>? serialInputPins = null,
        IReadOnlyList<DigitalPin>? serialReadEnablePins = null,
        IReadOnlyList<DigitalPin>? parallelOutputPins = null,
        DigitalPin? interruptRequestPin = null)
    {
        Component = component;
        AddressPins = addressPins;
        DataPins = dataPins;
        EvaluateDrivenPin = evaluateDrivenPin;
        AttachFabric = attachFabric;
        DetachFabric = detachFabric;
        SerialInputPins = serialInputPins ?? Array.Empty<DigitalPin>();
        SerialReadEnablePins = serialReadEnablePins ?? Array.Empty<DigitalPin>();
        ParallelOutputPins = parallelOutputPins ?? Array.Empty<DigitalPin>();
        InterruptRequestPin = interruptRequestPin;
    }

    public VirtualHardwareComponent Component { get; }
    public IReadOnlyList<DigitalPin> AddressPins { get; }
    public IReadOnlyList<DigitalPin> DataPins { get; }
    public Func<DigitalPin, uint, bool, CompiledDriveState?> EvaluateDrivenPin { get; }
    public Action<ICompiledBusFabric> AttachFabric { get; }
    public Action DetachFabric { get; }
    public IReadOnlyList<DigitalPin> SerialInputPins { get; }
    public IReadOnlyList<DigitalPin> SerialReadEnablePins { get; }
    public IReadOnlyList<DigitalPin> ParallelOutputPins { get; }
    public DigitalPin? InterruptRequestPin { get; }
}

public interface ICompiledBusMasterProvider
{
    IEnumerable<CompiledBusMasterDescriptor> GetCompiledBusMasters();
}

public sealed class CompiledBusTargetDescriptor
{
    public CompiledBusTargetDescriptor(
        VirtualHardwareComponent component,
        IReadOnlyList<DigitalPin> addressPins,
        IReadOnlyList<DigitalPin> dataPins,
        IReadOnlyList<CompiledPinCondition> readConditions,
        IReadOnlyList<CompiledPinCondition> writeConditions,
        CompiledBusReadPhase readPhase,
        Func<int, byte>? read,
        Action<int, byte>? write,
        Action<bool>? observeBusCycle = null,
        Func<int, bool, bool>? isSelected = null,
        CompiledBusWritePhase writePhase = CompiledBusWritePhase.Begin,
        Action<int>? observeReadBegin = null,
        CompiledBusCycleObservationPhase observeBusCyclePhase = CompiledBusCycleObservationPhase.Begin)
    {
        Component = component;
        AddressPins = addressPins;
        DataPins = dataPins;
        ReadConditions = readConditions;
        WriteConditions = writeConditions;
        ReadPhase = readPhase;
        Read = read;
        Write = write;
        ObserveBusCycle = observeBusCycle;
        ObserveBusCyclePhase = observeBusCyclePhase;
        IsSelected = isSelected;
        WritePhase = writePhase;
        ObserveReadBegin = observeReadBegin;
    }

    public VirtualHardwareComponent Component { get; }
    public IReadOnlyList<DigitalPin> AddressPins { get; }
    public IReadOnlyList<DigitalPin> DataPins { get; }
    public IReadOnlyList<CompiledPinCondition> ReadConditions { get; }
    public IReadOnlyList<CompiledPinCondition> WriteConditions { get; }
    public CompiledBusReadPhase ReadPhase { get; }
    public Func<int, byte>? Read { get; }
    public Action<int, byte>? Write { get; }
    /// <summary>
    /// Physical phase at which the target samples a compiled write. Components
    /// that latch from an end-of-cycle strobe can request Complete without the
    /// compiler learning what that strobe or component means.
    /// </summary>
    public CompiledBusWritePhase WritePhase { get; }

    /// <summary>
    /// Optional package-local observation of each bus cycle on the physical bus
    /// to which this target descriptor resolves. The compiler invokes it even
    /// when this target is not selected for that cycle, preserving hardware that
    /// clocks internal state from bus activity rather than from chip-select alone.
    /// The bool is true for a write cycle and false for a read cycle.
    /// </summary>
    public Action<bool>? ObserveBusCycle { get; }

    /// <summary>
    /// Physical edge at which <see cref="ObserveBusCycle"/> runs. This phase is
    /// declared by the component facet itself; the compiler remains unaware of
    /// the observer's electrical meaning.
    /// </summary>
    public CompiledBusCycleObservationPhase ObserveBusCyclePhase { get; }

    /// <summary>
    /// Optional package-local dynamic select gate evaluated after physical pin
    /// conditions and address projection have selected this target. This is for
    /// circuitry such as a state-controlled RAM chip-enable whose state lives
    /// inside the component rather than on another package pin. The compiler
    /// treats such a target as dynamic and never interprets what the gate means.
    /// The bool is true for a write cycle and false for a read cycle.
    /// </summary>
    public Func<int, bool, bool>? IsSelected { get; }

    /// <summary>
    /// Optional package-local observation of a selected read at the instant the
    /// bus master asserts the read window. This can clock internal diagnostic or
    /// edge-sensitive state without forcing the data value itself to be resolved
    /// until a later physical read phase. A descriptor may intentionally expose
    /// an observer with no Read delegate when the package watches address/control
    /// pins but never drives the data bus for that transaction.
    /// </summary>
    public Action<int>? ObserveReadBegin { get; }
}

public interface ICompiledBusTargetProvider
{
    IEnumerable<CompiledBusTargetDescriptor> GetCompiledBusTargets();
}

public interface ICompiledCombinationalComponent
{
    bool TryEvaluateCompiledOutput(
        DigitalPin output,
        Func<DigitalPin, DigitalLevel> sampleInput,
        out CompiledDriveState drive);
}

/// <summary>
/// Optional subset of combinational package outputs whose behavior is guaranteed
/// not to depend on mutable internal state while the device remains attached.
/// The whole-circuit compiler may use these facets when rebuilding dispatch for
/// the currently bound replaceable hardware, but never folds the package into
/// the immutable motherboard unit. Replacing the package rebuilds the proof.
/// </summary>
public interface ICompiledStaticCombinationalComponent
{
    bool TryEvaluateCompiledStaticOutput(
        DigitalPin output,
        Func<DigitalPin, DigitalLevel> sampleInput,
        out CompiledDriveState drive);
}

public interface ICompiledBitProjectionComponent
{
    bool TryTraceCompiledOutput(
        DigitalPin output,
        Func<DigitalPin, DigitalLevel> sampleStaticInput,
        out DigitalPin input);
}

public interface ICompiledClockSource
{
    DigitalPin CompiledClockOutput { get; }
    ulong CompiledHalfCycleCount { get; }
    DigitalLevel CompiledClockLevel { get; }
    bool AdvanceCompiledHalfCycleWithoutPropagation();
    void AdvanceCompiledFullCyclesWithoutPropagation(int cycles);
}

public interface ICompiledClockedComponent
{
    DigitalPin CompiledClockInput { get; }
    DigitalPin? CompiledResetInput { get; }
    DigitalLevel CompiledResetAssertedLevel { get; }
    void ExecuteCompiledClockActivation();
    void SetCompiledResetAsserted(bool asserted);
}

public readonly record struct CompiledSignalSinkDescriptor(
    DigitalPin Pin,
    Action<DigitalLevel> PresentLevel);

public interface ICompiledSignalSinkProvider
{
    IEnumerable<CompiledSignalSinkDescriptor> GetCompiledSignalSinks();
}

public sealed class CompiledSerialPeripheralDescriptor
{
    public CompiledSerialPeripheralDescriptor(
        VirtualHardwareComponent component,
        DigitalPin dataPin,
        DigitalPin clockPin,
        DigitalPin latchPin,
        Func<byte> readSerial,
        Action<bool> writeLatch)
    {
        Component = component;
        DataPin = dataPin;
        ClockPin = clockPin;
        LatchPin = latchPin;
        ReadSerial = readSerial;
        WriteLatch = writeLatch;
    }

    public VirtualHardwareComponent Component { get; }
    public DigitalPin DataPin { get; }
    public DigitalPin ClockPin { get; }
    public DigitalPin LatchPin { get; }
    public Func<byte> ReadSerial { get; }
    public Action<bool> WriteLatch { get; }
}

public interface ICompiledSerialPeripheralProvider
{
    IEnumerable<CompiledSerialPeripheralDescriptor> GetCompiledSerialPeripherals();
}

/// <summary>
/// Marker for replaceable hardware that remains a separately executable unit.
/// Its package still exposes the same generic compiler facets as any other
/// component, allowing the fixed-board compiler to optimize the connector
/// boundary without learning what the external device represents.
/// </summary>
public interface ICompiledExternalDevice
{
    bool ReadyForCompiledExecution { get; }
}
