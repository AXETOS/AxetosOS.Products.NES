using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Simulation;

/// <summary>
/// Whole-circuit hardware-lab compiler.
///
/// This compiler has no board, product, console, mapper, memory-map, or named
/// chip rules. Components advertise only generic compilable hardware facets;
/// the compiler combines those facets strictly from physical pin connectivity.
/// All fast paths are therefore consequences of the assembled circuit. Change
/// a chip or wire and the generated routes, permutations, clock schedule, and
/// fused signal paths are rebuilt from the new hardware.
/// </summary>
public sealed class CompiledLabMotherboardExecutionPlan : IDisposable
{
    private readonly VirtualHardwareBoard _board;
    private readonly ICompiledClockSource _clockSource;
    private readonly HardwareCompiler _compiler;
    private readonly CompiledClockSchedule _clockSchedule;
    private readonly CompiledBusFabric[] _busFabrics;
    private readonly CompiledResetBinding[] _resetBindings;
    private readonly HashSet<ICompiledExternalDevice> _externalDevices = new(ReferenceEqualityComparer.Instance);
    private ulong _clockRisingEdges;

    public CompiledLabMotherboardExecutionPlan(
        VirtualHardwareBoard board,
        ICompiledClockSource clockSource)
    {
        ArgumentNullException.ThrowIfNull(board);
        _board = board;
        _clockSource = clockSource ?? throw new ArgumentNullException(nameof(clockSource));
        CompilationId = Guid.NewGuid();

        // The fixed unit is compiled from motherboard-owned hardware only.
        // Replaceable packages may already be physically connected when this
        // constructor is called, but they are deliberately excluded from the
        // immutable motherboard target set and bound separately below.
        _compiler = new HardwareCompiler(board);
        var signalRouter = new CompiledSignalRouter(board);
        _clockSchedule = _compiler.CompileClockSchedule(clockSource);
        _resetBindings = _compiler.CompileResetBindings();
        _clockRisingEdges =
            (clockSource.CompiledHalfCycleCount
             + (clockSource.CompiledClockLevel == DigitalLevel.High ? 1UL : 0UL)) / 2UL;

        var masters = _compiler.BusMasters;
        _busFabrics = new CompiledBusFabric[masters.Count];
        for (var index = 0; index < masters.Count; index++)
        {
            var fabric = _compiler.CompileBus(
                masters[index],
                () => _clockRisingEdges,
                signalRouter);
            _busFabrics[index] = fabric;
            masters[index].AttachFabric(fabric);
        }

        InternalComponentCount = board.Components.Count(component => component is not ICompiledExternalDevice);

        foreach (var device in board.Components.OfType<ICompiledExternalDevice>())
        {
            if (device.ReadyForCompiledExecution) AttachExternalDevice(device);
        }
    }

    public Guid CompilationId { get; }
    public ulong MasterClockRisingEdges => _clockRisingEdges;
    public int RuntimeUnits => 1 + _externalDevices.Count;
    public int InternalComponentCount { get; }
    public int BoundaryTraceCount => _board.Nets.Count(net =>
        net.Pins.Any(pin => pin.OwnerComponent is ICompiledExternalDevice device && _externalDevices.Contains(device)));
    public int FoldedInternalTraceCount => _board.Nets.Count - BoundaryTraceCount;

    /// <summary>
    /// Binds one replaceable hardware package to the already-compiled fixed
    /// circuit. Only the package's own generic facets and the live physical
    /// connector topology are compiled here; the motherboard plan and its
    /// CompilationId remain unchanged.
    /// </summary>
    public void AttachExternalDevice(ICompiledExternalDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!device.ReadyForCompiledExecution)
            throw new InvalidOperationException("The replaceable hardware device is not ready for compiled execution.");
        if (device is not IVirtualHardwareComponent component)
            throw new InvalidOperationException("Compiled external hardware must also be a physical virtual-hardware component.");
        if (!_externalDevices.Add(device)) return;

        for (var index = 0; index < _busFabrics.Length; index++)
            _busFabrics[index].BindExternalDevice(component);
    }

    public void DetachExternalDevice(ICompiledExternalDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!_externalDevices.Remove(device)) return;
        if (device is not IVirtualHardwareComponent component) return;
        for (var index = 0; index < _busFabrics.Length; index++)
            _busFabrics[index].UnbindExternalDevice(component);
    }

    public void SynchronizePowerOn()
    {
        _clockRisingEdges = 0;
        for (var index = 0; index < _busFabrics.Length; index++)
            _busFabrics[index].ResetTransientState();
        for (var index = 0; index < _resetBindings.Length; index++)
            _resetBindings[index].Refresh();
    }

    /// <summary>
    /// Re-evaluates compiled sinks reached from one changed external source.
    /// This is intentionally pin/net based; the compiler does not know what the
    /// source means to the board.
    /// </summary>
    public void RefreshExternalSource(DigitalPin sourcePin)
    {
        ArgumentNullException.ThrowIfNull(sourcePin);
        var net = sourcePin.Net;
        if (net is null) return;
        for (var index = 0; index < _resetBindings.Length; index++)
        {
            if (ReferenceEquals(_resetBindings[index].Pin.Net, net))
                _resetBindings[index].Refresh();
        }
    }

    public void AdvanceHalfCycle()
    {
        if (!_clockSource.AdvanceCompiledHalfCycleWithoutPropagation()) return;
        AdvanceOneRisingEdge();
    }

    public void AdvanceCycles(int cycles)
    {
        if (cycles < 0) throw new ArgumentOutOfRangeException(nameof(cycles));
        if (cycles == 0) return;

        _clockSource.AdvanceCompiledFullCyclesWithoutPropagation(cycles);
        var period = _clockSchedule.Period;

        while (cycles > 0 && (_clockRisingEdges % (ulong)period) != 0)
        {
            AdvanceOneRisingEdge();
            cycles--;
        }

        while (cycles >= period)
        {
            AdvanceOneCompiledPeriod();
            cycles -= period;
        }

        while (cycles-- > 0)
            AdvanceOneRisingEdge();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AdvanceOneCompiledPeriod()
    {
        var events = _clockSchedule.Events;

        // Generic circuit-pattern shortcut. Four event groups are common for
        // small mixed-divider clock domains; unroll the generated event list
        // without knowing which components are in those groups.
        if (events.Length == 4)
        {
            _clockRisingEdges += (ulong)events[0].Delta;
            events[0].Invoke();
            _clockRisingEdges += (ulong)events[1].Delta;
            events[1].Invoke();
            _clockRisingEdges += (ulong)events[2].Delta;
            events[2].Invoke();
            _clockRisingEdges += (ulong)events[3].Delta;
            events[3].Invoke();
            return;
        }

        for (var index = 0; index < events.Length; index++)
        {
            _clockRisingEdges += (ulong)events[index].Delta;
            events[index].Invoke();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AdvanceOneRisingEdge()
    {
        var edge = ++_clockRisingEdges;
        var offset = (int)(edge % (ulong)_clockSchedule.Period);
        if (offset == 0) offset = _clockSchedule.Period;
        _clockSchedule.EventByOffset[offset]?.Invoke();
    }

    public void Dispose()
    {
        for (var index = 0; index < _busFabrics.Length; index++)
            _busFabrics[index].Master.DetachFabric();
    }

    private sealed class HardwareCompiler
    {
        private readonly VirtualHardwareBoard _board;
        private readonly CompiledBusTargetDescriptor[] _targets;
        private readonly CompiledSerialPeripheralDescriptor[] _serialPeripherals;

        public HardwareCompiler(VirtualHardwareBoard board)
        {
            _board = board;
            BusMasters = board.Components
                .Where(component => component is not ICompiledExternalDevice)
                .OfType<ICompiledBusMasterProvider>()
                .SelectMany(provider => provider.GetCompiledBusMasters())
                .ToArray();
            _targets = board.Components
                .Where(component => component is not ICompiledExternalDevice)
                .OfType<ICompiledBusTargetProvider>()
                .SelectMany(provider => provider.GetCompiledBusTargets())
                .ToArray();
            _serialPeripherals = board.Components
                .Where(component => component is not ICompiledExternalDevice)
                .OfType<ICompiledSerialPeripheralProvider>()
                .SelectMany(provider => provider.GetCompiledSerialPeripherals())
                .ToArray();
        }

        public IReadOnlyList<CompiledBusMasterDescriptor> BusMasters { get; }

        public CompiledResetBinding[] CompileResetBindings() =>
            _board.Components
                .Where(component => component is not ICompiledExternalDevice)
                .OfType<ICompiledClockedComponent>()
                .Where(component => component.CompiledResetInput is not null)
                .Select(component => new CompiledResetBinding(component))
                .ToArray();

        public CompiledClockSchedule CompileClockSchedule(ICompiledClockSource source)
        {
            var net = source.CompiledClockOutput.Net
                ?? throw new InvalidOperationException("The compiled clock source is not attached to a physical net.");

            var sinks = new List<ICompiledClockedComponent>();
            foreach (var pin in net.Pins)
            {
                if (pin.OwnerComponent is ICompiledExternalDevice) continue;
                if (pin.OwnerComponent is not ICompiledClockedComponent sink) continue;
                if (!ReferenceEquals(sink.CompiledClockInput, pin)) continue;
                sinks.Add(sink);
            }
            if (sinks.Count == 0)
                throw new InvalidOperationException("The compiled clock trace has no compilable clocked receivers.");

            var period = 1;
            for (var index = 0; index < sinks.Count; index++)
            {
                period = LeastCommonMultiple(period, sinks[index].CompiledClockInput.InputActivationPeriod);
                if (period > 4096)
                    throw new NotSupportedException("The compiled clock-domain repeat period exceeds 4096 source edges.");
            }

            var events = new List<CompiledClockEvent>();
            var eventByOffset = new CompiledClockEvent?[period + 1];
            var previousOffset = 0;
            for (var offset = 1; offset <= period; offset++)
            {
                var active = new List<Action>(sinks.Count);
                for (var index = 0; index < sinks.Count; index++)
                {
                    var sink = sinks[index];
                    if ((offset % sink.CompiledClockInput.InputActivationPeriod) == 0)
                        active.Add(sink.ExecuteCompiledClockActivation);
                }
                if (active.Count == 0) continue;

                var compiledEvent = new CompiledClockEvent(offset - previousOffset, active);
                events.Add(compiledEvent);
                eventByOffset[offset] = compiledEvent;
                previousOffset = offset;
            }

            if (events.Count == 0 || previousOffset != period)
                throw new InvalidOperationException("The compiled clock schedule does not close on its repeat boundary.");
            return new CompiledClockSchedule(period, events.ToArray(), eventByOffset);
        }

        public CompiledBusFabric CompileBus(
            CompiledBusMasterDescriptor master,
            Func<ulong> clockEdgeProvider,
            CompiledSignalRouter signalRouter)
        {
            if (master.AddressPins.Count is <= 0 or > 16)
                throw new NotSupportedException("Compiled byte-bus address widths must be between 1 and 16 bits.");
            if (master.DataPins.Count is <= 0 or > 8)
                throw new NotSupportedException("Compiled byte-bus data widths must be between 1 and 8 bits.");

            var targets = CompileTargetRuntimes(master, _targets, allowExternalProjection: false);
            ValidateDataBusCoverage(master, targets.Runtimes);
            var serialBindings = CompileSerialBindings(master);
            return new CompiledBusFabric(
                this,
                master,
                targets,
                serialBindings,
                clockEdgeProvider,
                signalRouter);
        }

        public CompiledTargetSet CompileExternalTargets(
            CompiledBusMasterDescriptor master,
            IVirtualHardwareComponent component)
        {
            if (component is not ICompiledBusTargetProvider provider)
                return CompiledTargetSet.Empty(master.AddressPins.Count);

            return CompileTargetRuntimes(
                master,
                provider.GetCompiledBusTargets().ToArray(),
                allowExternalProjection: true);
        }

        private CompiledTargetSet CompileTargetRuntimes(
            CompiledBusMasterDescriptor master,
            IReadOnlyList<CompiledBusTargetDescriptor> descriptors,
            bool allowExternalProjection)
        {
            var runtimes = new List<CompiledBusTargetRuntime>();
            for (var index = 0; index < descriptors.Count; index++)
            {
                var target = descriptors[index];
                if (!TryCompileDataPermutation(target.DataPins, master.DataPins, out var targetToMaster))
                    continue;
                runtimes.Add(new CompiledBusTargetRuntime(target, targetToMaster));
            }

            var targetArray = runtimes.ToArray();
            var addressSources = new int[targetArray.Length][];
            for (var targetIndex = 0; targetIndex < targetArray.Length; targetIndex++)
            {
                var target = targetArray[targetIndex].Descriptor;
                var sources = new int[target.AddressPins.Count];
                for (var bit = 0; bit < sources.Length; bit++)
                    sources[bit] = FindRootBit(
                        target.AddressPins[bit],
                        master.AddressPins,
                        master,
                        0,
                        allowExternalProjection);
                addressSources[targetIndex] = sources;
            }

            var addressCount = 1 << master.AddressPins.Count;
            var dynamicTargets = new bool[targetArray.Length];
            for (var targetIndex = 0; targetIndex < targetArray.Length; targetIndex++)
                dynamicTargets[targetIndex] = !CanStaticallyCompileTarget(
                    master, targetArray[targetIndex], addressSources[targetIndex], addressCount);

            // Read routes are phase-specialized once at compile time. The hot
            // runtime path no longer walks a selected target only to discover
            // that it belongs to the other half of the bus transaction. This
            // is especially important for the RP2C0x, which performs tens of
            // thousands of real VRAM reads per frame.
            var readBeginRoutes = new CompiledBusRoute[addressCount];
            var readCompleteRoutes = new CompiledBusRoute[addressCount];
            var readObserverRoutes = new CompiledBusRoute[addressCount];
            var writeRoutes = new CompiledBusRoute[addressCount];
            for (var raw = 0; raw < addressCount; raw++)
            {
                var address = (uint)raw;
                readBeginRoutes[raw] = CompileRoute(
                    master, targetArray, addressSources, dynamicTargets, address, readCycle: true,
                    readPhase: CompiledBusReadPhase.Begin);
                readCompleteRoutes[raw] = CompileRoute(
                    master, targetArray, addressSources, dynamicTargets, address, readCycle: true,
                    readPhase: CompiledBusReadPhase.Complete);
                readObserverRoutes[raw] = CompileRoute(
                    master, targetArray, addressSources, dynamicTargets, address, readCycle: true,
                    observeReadBeginOnly: true);
                writeRoutes[raw] = CompileRoute(
                    master, targetArray, addressSources, dynamicTargets, address, readCycle: false);
            }

            var dynamicIndices = Enumerable.Range(0, dynamicTargets.Length)
                .Where(index => dynamicTargets[index])
                .ToArray();

            // A target may contain package circuitry clocked by every cycle on
            // this physical bus rather than only by cycles that select it. Keep
            // one instance of each observer after topology has proven that its
            // target data pins belong to this bus master. This remains entirely
            // topology/facet derived and carries no board or product semantics.
            var beginBusCycleObservers = new List<Action<bool>>();
            var completeBusCycleObservers = new List<Action<bool>>();
            for (var targetIndex = 0; targetIndex < targetArray.Length; targetIndex++)
            {
                var descriptor = targetArray[targetIndex].Descriptor;
                var observer = descriptor.ObserveBusCycle;
                if (observer is null) continue;

                var observers = descriptor.ObserveBusCyclePhase == CompiledBusCycleObservationPhase.Complete
                    ? completeBusCycleObservers
                    : beginBusCycleObservers;
                if (!observers.Contains(observer)) observers.Add(observer);
            }

            return new CompiledTargetSet(
                targetArray,
                addressSources,
                dynamicIndices,
                readBeginRoutes,
                readCompleteRoutes,
                readObserverRoutes,
                writeRoutes,
                beginBusCycleObservers.ToArray(),
                completeBusCycleObservers.ToArray());
        }

        private bool CanStaticallyCompileTarget(
            CompiledBusMasterDescriptor master,
            CompiledBusTargetRuntime target,
            int[] addressSources,
            int addressCount)
        {
            var descriptor = target.Descriptor;
            if (descriptor.IsSelected is not null) return false;
            if ((descriptor.Read is not null || descriptor.ObserveReadBegin is not null)
                && !CanStaticallyCompileCycle(master, descriptor, addressSources, addressCount, readCycle: true))
                return false;
            if (descriptor.Write is not null
                && !CanStaticallyCompileCycle(master, descriptor, addressSources, addressCount, readCycle: false))
                return false;
            return true;
        }

        private bool CanStaticallyCompileCycle(
            CompiledBusMasterDescriptor master,
            CompiledBusTargetDescriptor descriptor,
            int[] addressSources,
            int addressCount,
            bool readCycle)
        {
            var conditions = readCycle ? descriptor.ReadConditions : descriptor.WriteConditions;
            for (var raw = 0; raw < addressCount; raw++)
            {
                var address = (uint)raw;
                var selected = true;
                for (var conditionIndex = 0; conditionIndex < conditions.Count; conditionIndex++)
                {
                    DigitalLevel level;
                    try
                    {
                        level = EvaluateInput(conditions[conditionIndex].Pin, address, readCycle, master, 0, allowExternalDevices: false);
                    }
                    catch (NotSupportedException)
                    {
                        return false;
                    }

                    if (level is not (DigitalLevel.Low or DigitalLevel.High)) return false;
                    if (level != conditions[conditionIndex].RequiredLevel)
                    {
                        selected = false;
                        break;
                    }
                }
                if (!selected) continue;

                for (var bit = 0; bit < addressSources.Length; bit++)
                {
                    if (addressSources[bit] >= 0) continue;
                    DigitalLevel level;
                    try
                    {
                        level = EvaluateInput(descriptor.AddressPins[bit], address, readCycle, master, 0, allowExternalDevices: false);
                    }
                    catch (NotSupportedException)
                    {
                        return false;
                    }
                    if (level is not (DigitalLevel.Low or DigitalLevel.High)) return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Classifies the physical pin conditions for one dynamic target/address
        /// using only the immutable motherboard plus state-independent facets of
        /// the currently attached replaceable hardware. 0 = proven rejected,
        /// 1 = all physical conditions proven selected, 2 = runtime evaluation
        /// is still required. No mutable mapper state is sampled by this proof.
        /// </summary>
        public byte ClassifyBoundStaticConditions(
            CompiledBusMasterDescriptor master,
            CompiledBusTargetRuntime target,
            ushort masterAddress,
            bool readCycle)
        {
            var descriptor = target.Descriptor;
            var conditions = readCycle ? descriptor.ReadConditions : descriptor.WriteConditions;
            var address = (uint)masterAddress;

            for (var conditionIndex = 0; conditionIndex < conditions.Count; conditionIndex++)
            {
                DigitalLevel level;
                try
                {
                    level = EvaluateInput(
                        conditions[conditionIndex].Pin,
                        address,
                        readCycle,
                        master,
                        0,
                        allowExternalDevices: false,
                        allowStaticExternalDevices: true);
                }
                catch (NotSupportedException)
                {
                    return 2;
                }

                if (level is not (DigitalLevel.Low or DigitalLevel.High)) return 2;
                if (level != conditions[conditionIndex].RequiredLevel) return 0;
            }

            return 1;
        }

        public bool TryResolveRuntimeTarget(
            CompiledBusMasterDescriptor master,
            CompiledBusTargetRuntime target,
            int[] addressSources,
            ushort masterAddress,
            bool readCycle,
            out int localAddress)
        {
            var descriptor = target.Descriptor;
            var conditions = readCycle ? descriptor.ReadConditions : descriptor.WriteConditions;
            var address = (uint)masterAddress;

            for (var conditionIndex = 0; conditionIndex < conditions.Count; conditionIndex++)
            {
                DigitalLevel level;
                try
                {
                    level = EvaluateInput(conditions[conditionIndex].Pin, address, readCycle, master, 0);
                }
                catch (NotSupportedException)
                {
                    localAddress = 0;
                    return false;
                }
                if (level != conditions[conditionIndex].RequiredLevel)
                {
                    localAddress = 0;
                    return false;
                }
            }

            return TryResolveRuntimeAddress(
                master,
                target,
                addressSources,
                masterAddress,
                writeCycle: !readCycle,
                out localAddress);
        }

        public bool TryResolveRuntimeAddress(
            CompiledBusMasterDescriptor master,
            CompiledBusTargetRuntime target,
            int[] addressSources,
            ushort masterAddress,
            bool writeCycle,
            out int localAddress)
        {
            localAddress = 0;
            var descriptor = target.Descriptor;
            var address = (uint)masterAddress;
            var readCycle = !writeCycle;

            for (var bit = 0; bit < addressSources.Length; bit++)
            {
                var root = addressSources[bit];
                DigitalLevel level;
                if (root >= 0)
                {
                    level = (address & (1u << root)) != 0 ? DigitalLevel.High : DigitalLevel.Low;
                }
                else
                {
                    try
                    {
                        level = EvaluateInput(descriptor.AddressPins[bit], address, readCycle, master, 0);
                    }
                    catch (NotSupportedException)
                    {
                        return false;
                    }
                }

                if (level == DigitalLevel.High) localAddress |= 1 << bit;
                else if (level != DigitalLevel.Low) return false;
            }

            return descriptor.IsSelected?.Invoke(localAddress, writeCycle) ?? true;
        }

        public bool TryResolveRuntimeAddressFromPlan(
            CompiledBusMasterDescriptor master,
            CompiledBusTargetRuntime target,
            int localAddressBase,
            int[] runtimeAddressBits,
            ushort masterAddress,
            bool writeCycle,
            out int localAddress)
        {
            localAddress = localAddressBase;
            var descriptor = target.Descriptor;
            var address = (uint)masterAddress;
            var readCycle = !writeCycle;

            for (var index = 0; index < runtimeAddressBits.Length; index++)
            {
                var bit = runtimeAddressBits[index];
                DigitalLevel level;
                try
                {
                    level = EvaluateInput(descriptor.AddressPins[bit], address, readCycle, master, 0);
                }
                catch (NotSupportedException)
                {
                    return false;
                }

                if (level == DigitalLevel.High) localAddress |= 1 << bit;
                else if (level != DigitalLevel.Low) return false;
            }

            return descriptor.IsSelected?.Invoke(localAddress, writeCycle) ?? true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public DigitalLevel EvaluateRuntimeInput(
            DigitalPin input,
            ushort masterAddress,
            bool readCycle,
            CompiledBusMasterDescriptor master) =>
            EvaluateInput(input, masterAddress, readCycle, master, 0);

        private static void ValidateDataBusCoverage(
            CompiledBusMasterDescriptor master,
            IReadOnlyList<CompiledBusTargetRuntime> targets)
        {
            var coveredPins = new HashSet<DigitalPin>(ReferenceEqualityComparer.Instance);
            for (var targetIndex = 0; targetIndex < targets.Count; targetIndex++)
            {
                var pins = targets[targetIndex].Descriptor.DataPins;
                for (var bit = 0; bit < pins.Count; bit++) coveredPins.Add(pins[bit]);
            }

            for (var bit = 0; bit < master.DataPins.Count; bit++)
            {
                var masterPin = master.DataPins[bit];
                var net = masterPin.Net;
                if (net is null) continue;
                foreach (var pin in net.Pins)
                {
                    if (!pin.IsOutputCapable || ReferenceEquals(pin, masterPin)) continue;
                    if (pin.OwnerComponent is ICompiledExternalDevice) continue;
                    if (coveredPins.Contains(pin)) continue;
                    throw new NotSupportedException(
                        $"The hardware compiler found an unmodelled physical data-bus driver on net '{net.Name}'. " +
                        "The owning component must expose a generic compiled bus-target facet before this circuit can be safely collapsed.");
                }
            }
        }

        private CompiledSerialBinding[] CompileSerialBindings(CompiledBusMasterDescriptor master)
        {
            if (master.SerialInputPins.Count == 0) return Array.Empty<CompiledSerialBinding>();
            var result = new CompiledSerialBinding[master.SerialInputPins.Count];

            for (var channel = 0; channel < result.Length; channel++)
            {
                var input = master.SerialInputPins[channel];
                var inputNet = input.Net;
                if (inputNet is null) continue;

                CompiledSerialPeripheralDescriptor? matched = null;
                for (var index = 0; index < _serialPeripherals.Length; index++)
                {
                    if (!ReferenceEquals(_serialPeripherals[index].DataPin.Net, inputNet)) continue;
                    matched = _serialPeripherals[index];
                    break;
                }
                if (matched is null) continue;

                if (channel < master.SerialReadEnablePins.Count)
                {
                    var enableNet = master.SerialReadEnablePins[channel].Net;
                    if (enableNet is not null && !ReferenceEquals(enableNet, matched.ClockPin.Net))
                        throw new InvalidOperationException("A serial peripheral data path was found but its physical shift-clock trace does not match the master channel.");
                }

                var latchBit = -1;
                for (var bit = 0; bit < master.ParallelOutputPins.Count; bit++)
                {
                    if (master.ParallelOutputPins[bit].Net is not null
                        && ReferenceEquals(master.ParallelOutputPins[bit].Net, matched.LatchPin.Net))
                    {
                        latchBit = bit;
                        break;
                    }
                }
                if (latchBit < 0)
                    throw new InvalidOperationException("A serial peripheral is connected to the master input but its latch input is not connected to a compiled parallel output bit.");

                result[channel] = new CompiledSerialBinding(matched, latchBit);
            }

            return result;
        }

        private CompiledBusRoute CompileRoute(
            CompiledBusMasterDescriptor master,
            CompiledBusTargetRuntime[] targets,
            int[][] addressSources,
            bool[] dynamicTargets,
            uint address,
            bool readCycle,
            CompiledBusReadPhase? readPhase = null,
            bool observeReadBeginOnly = false)
        {
            var selectedTargets = new List<int>(2);
            var localAddresses = new List<int>(2);

            for (var targetIndex = 0; targetIndex < targets.Length; targetIndex++)
            {
                if (dynamicTargets[targetIndex]) continue;
                var descriptor = targets[targetIndex].Descriptor;
                if (readCycle)
                {
                    // Observer-only hardware is allowed to watch the physical
                    // address/control transaction without driving the data bus.
                    // This is a generic circuit capability for edge-sensitive
                    // devices whose internal state clocks from bus addresses.
                    if (observeReadBeginOnly)
                    {
                        if (descriptor.ObserveReadBegin is null) continue;
                    }
                    else
                    {
                        if (descriptor.Read is null) continue;
                        if (readPhase.HasValue && descriptor.ReadPhase != readPhase.Value) continue;
                    }
                }
                else if (descriptor.Write is null) continue;

                var conditions = readCycle ? descriptor.ReadConditions : descriptor.WriteConditions;
                var selected = true;
                for (var conditionIndex = 0; conditionIndex < conditions.Count; conditionIndex++)
                {
                    if (EvaluateInput(conditions[conditionIndex].Pin, address, readCycle, master, 0)
                        != conditions[conditionIndex].RequiredLevel)
                    {
                        selected = false;
                        break;
                    }
                }
                if (!selected) continue;

                var localAddress = 0;
                var sources = addressSources[targetIndex];
                for (var bit = 0; bit < sources.Length; bit++)
                {
                    var root = sources[bit];
                    DigitalLevel level;
                    if (root >= 0)
                        level = (address & (1u << root)) != 0 ? DigitalLevel.High : DigitalLevel.Low;
                    else
                        level = EvaluateInput(descriptor.AddressPins[bit], address, readCycle, master, 0);

                    if (level == DigitalLevel.High) localAddress |= 1 << bit;
                    else if (level != DigitalLevel.Low)
                        throw new InvalidOperationException("A selected target has an address pin whose physical value cannot be proven for the compiled bus transaction.");
                }

                selectedTargets.Add(targetIndex);
                localAddresses.Add(localAddress);
            }

            return CompiledBusRoute.Create(selectedTargets, localAddresses);
        }

        private int FindRootBit(
            DigitalPin target,
            IReadOnlyList<DigitalPin> roots,
            CompiledBusMasterDescriptor master,
            int depth,
            bool allowExternalDevices)
        {
            if (depth > 16)
                throw new InvalidOperationException("Compiled bit-projection recursion exceeded the hardware compiler limit.");
            var net = target.Net;
            if (net is null) return -1;

            foreach (var driver in net.Pins)
            {
                if (!driver.IsOutputCapable || ReferenceEquals(driver, target)) continue;
                for (var root = 0; root < roots.Count; root++)
                {
                    if (ReferenceEquals(driver, roots[root])) return root;
                }

                if (!allowExternalDevices && driver.OwnerComponent is ICompiledExternalDevice) continue;
                if (driver.OwnerComponent is not ICompiledBitProjectionComponent projection) continue;
                if (!projection.TryTraceCompiledOutput(
                    driver,
                    pin => EvaluateStaticInput(pin, allowExternalDevices),
                    out var input)) continue;
                var projected = FindRootBit(input, roots, master, depth + 1, allowExternalDevices);
                if (projected >= 0) return projected;
            }
            return -1;
        }

        private DigitalLevel EvaluateStaticInput(DigitalPin input, bool allowExternalDevices)
        {
            try
            {
                return EvaluateInput(
                    input,
                    0,
                    readCycle: true,
                    master: null,
                    depth: 0,
                    allowExternalDevices: allowExternalDevices);
            }
            catch (NotSupportedException)
            {
                return DigitalLevel.Unknown;
            }
        }

        private DigitalLevel EvaluateInput(
            DigitalPin input,
            uint address,
            bool readCycle,
            CompiledBusMasterDescriptor? master,
            int depth,
            bool allowExternalDevices = true,
            bool allowStaticExternalDevices = false)
        {
            if (depth > 16)
                throw new InvalidOperationException("Combinational topology recursion exceeded the hardware compiler limit.");
            var net = input.Net;
            return net is null
                ? DigitalLevel.Unknown
                : EvaluateNet(net, address, readCycle, master, depth + 1, allowExternalDevices, allowStaticExternalDevices);
        }

        private DigitalLevel EvaluateNet(
            DigitalNet net,
            uint address,
            bool readCycle,
            CompiledBusMasterDescriptor? master,
            int depth,
            bool allowExternalDevices = true,
            bool allowStaticExternalDevices = false)
        {
            var haveStrong = false;
            var strongLow = false;
            var strongHigh = false;
            var strongUnknown = false;
            var haveWeak = false;
            var weakLow = false;
            var weakHigh = false;
            var weakUnknown = false;

            foreach (var driver in net.Pins)
            {
                if (!driver.IsOutputCapable) continue;
                var drive = EvaluateDriver(
                    driver,
                    address,
                    readCycle,
                    master,
                    depth + 1,
                    allowExternalDevices,
                    allowStaticExternalDevices);
                if (drive.Level == DigitalLevel.HighImpedance) continue;
                if (drive.Strength == DigitalDriveStrength.Strong)
                {
                    haveStrong = true;
                    strongLow |= drive.Level == DigitalLevel.Low;
                    strongHigh |= drive.Level == DigitalLevel.High;
                    strongUnknown |= drive.Level is not (DigitalLevel.Low or DigitalLevel.High);
                }
                else
                {
                    haveWeak = true;
                    weakLow |= drive.Level == DigitalLevel.Low;
                    weakHigh |= drive.Level == DigitalLevel.High;
                    weakUnknown |= drive.Level is not (DigitalLevel.Low or DigitalLevel.High);
                }
            }

            if (haveStrong) return FinishResolution(strongLow, strongHigh, strongUnknown);
            if (haveWeak) return FinishResolution(weakLow, weakHigh, weakUnknown);
            return DigitalLevel.Unknown;
        }

        private CompiledDriveState EvaluateDriver(
            DigitalPin driver,
            uint address,
            bool readCycle,
            CompiledBusMasterDescriptor? master,
            int depth,
            bool allowExternalDevices = true,
            bool allowStaticExternalDevices = false)
        {
            if (!allowExternalDevices && driver.OwnerComponent is ICompiledExternalDevice)
            {
                if (!allowStaticExternalDevices
                    || driver.OwnerComponent is not ICompiledStaticCombinationalComponent staticCombinational
                    || !staticCombinational.TryEvaluateCompiledStaticOutput(
                        driver,
                        pin => EvaluateInput(
                            pin,
                            address,
                            readCycle,
                            master,
                            depth + 1,
                            allowExternalDevices: false,
                            allowStaticExternalDevices: true),
                        out var staticDrive))
                {
                    throw new NotSupportedException("Replaceable hardware remains outside the immutable fixed-circuit compilation unit.");
                }

                return staticDrive;
            }
            if (master is not null)
            {
                var masterDrive = master.EvaluateDrivenPin(driver, address, readCycle);
                if (masterDrive.HasValue) return masterDrive.Value;

                if (driver.OwnerComponent is ICompiledBusAddressCombinationalComponent busAddressCombinational
                    && busAddressCombinational.TryEvaluateCompiledBusAddressOutput(
                        driver,
                        address,
                        readCycle,
                        out var directDrive))
                {
                    return directDrive;
                }
            }

            if (driver.OwnerComponent is ICompiledCombinationalComponent combinational
                && combinational.TryEvaluateCompiledOutput(
                    driver,
                    pin => EvaluateInput(
                        pin,
                        address,
                        readCycle,
                        master,
                        depth + 1,
                        allowExternalDevices,
                        allowStaticExternalDevices),
                    out var drive))
            {
                return drive;
            }

            throw new NotSupportedException(
                $"The hardware compiler cannot prove the output behavior of physical pin '{driver.Name}'.");
        }

        private static bool TryCompileDataPermutation(
            IReadOnlyList<DigitalPin> targetPins,
            IReadOnlyList<DigitalPin> masterPins,
            out int[] targetToMaster)
        {
            targetToMaster = Array.Empty<int>();
            if (targetPins.Count != masterPins.Count || targetPins.Count == 0) return false;
            var mapping = new int[targetPins.Count];
            var used = new bool[masterPins.Count];

            for (var targetBit = 0; targetBit < targetPins.Count; targetBit++)
            {
                var net = targetPins[targetBit].Net;
                if (net is null) return false;
                var found = -1;
                for (var masterBit = 0; masterBit < masterPins.Count; masterBit++)
                {
                    if (!ReferenceEquals(net, masterPins[masterBit].Net)) continue;
                    found = masterBit;
                    break;
                }
                if (found < 0 || used[found]) return false;
                used[found] = true;
                mapping[targetBit] = found;
            }

            targetToMaster = mapping;
            return true;
        }

        private static DigitalLevel FinishResolution(bool low, bool high, bool unknown)
        {
            if (low && high) return DigitalLevel.Contention;
            if (unknown) return DigitalLevel.Unknown;
            if (low) return DigitalLevel.Low;
            if (high) return DigitalLevel.High;
            return DigitalLevel.Unknown;
        }

        private static int GreatestCommonDivisor(int left, int right)
        {
            while (right != 0)
            {
                var remainder = left % right;
                left = right;
                right = remainder;
            }
            return Math.Abs(left);
        }

        private static int LeastCommonMultiple(int left, int right) =>
            checked(left / GreatestCommonDivisor(left, right) * right);
    }

    private sealed class CompiledTargetSet
    {
        public CompiledTargetSet(
            CompiledBusTargetRuntime[] runtimes,
            int[][] addressSources,
            int[] dynamicIndices,
            CompiledBusRoute[] readBeginRoutes,
            CompiledBusRoute[] readCompleteRoutes,
            CompiledBusRoute[] readObserverRoutes,
            CompiledBusRoute[] writeRoutes,
            Action<bool>[] beginBusCycleObservers,
            Action<bool>[] completeBusCycleObservers)
        {
            Runtimes = runtimes;
            AddressSources = addressSources;
            DynamicIndices = dynamicIndices;
            ReadBeginRoutes = readBeginRoutes;
            ReadCompleteRoutes = readCompleteRoutes;
            ReadObserverRoutes = readObserverRoutes;
            WriteRoutes = writeRoutes;
            BeginBusCycleObservers = beginBusCycleObservers;
            CompleteBusCycleObservers = completeBusCycleObservers;
        }

        public CompiledBusTargetRuntime[] Runtimes { get; }
        public int[][] AddressSources { get; }
        public int[] DynamicIndices { get; }
        public CompiledBusRoute[] ReadBeginRoutes { get; }
        public CompiledBusRoute[] ReadCompleteRoutes { get; }
        public CompiledBusRoute[] ReadObserverRoutes { get; }
        public CompiledBusRoute[] WriteRoutes { get; }
        public Action<bool>[] BeginBusCycleObservers { get; }
        public Action<bool>[] CompleteBusCycleObservers { get; }

        public static CompiledTargetSet Empty(int addressWidth)
        {
            var count = 1 << addressWidth;
            return new CompiledTargetSet(
                Array.Empty<CompiledBusTargetRuntime>(),
                Array.Empty<int[]>(),
                Array.Empty<int>(),
                new CompiledBusRoute[count],
                new CompiledBusRoute[count],
                new CompiledBusRoute[count],
                new CompiledBusRoute[count],
                Array.Empty<Action<bool>>(),
                Array.Empty<Action<bool>>());
        }
    }

    private sealed class CompiledExternalBinding
    {
        public CompiledExternalBinding(IVirtualHardwareComponent component, CompiledTargetSet targets)
        {
            Component = component;
            Targets = targets;
        }

        public IVirtualHardwareComponent Component { get; }
        public CompiledTargetSet Targets { get; }
    }

    private readonly struct CompiledRuntimePinResolver
    {
        private readonly DigitalPin _input;
        private readonly ICompiledBusAddressCombinationalComponent? _directComponent;
        private readonly DigitalPin? _directOutput;

        public CompiledRuntimePinResolver(
            DigitalPin input,
            ICompiledBusAddressCombinationalComponent? directComponent,
            DigitalPin? directOutput)
        {
            _input = input;
            _directComponent = directComponent;
            _directOutput = directOutput;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public DigitalLevel Sample(
            HardwareCompiler compiler,
            CompiledBusMasterDescriptor master,
            ushort address,
            bool readCycle)
        {
            var direct = _directComponent;
            if (direct is not null
                && direct.TryEvaluateCompiledBusAddressOutput(
                    _directOutput!,
                    address,
                    readCycle,
                    out var drive))
            {
                return drive.Level;
            }

            return compiler.EvaluateRuntimeInput(_input, address, readCycle, master);
        }
    }

    private sealed class CompiledDynamicTargetBinding
    {
        public CompiledDynamicTargetBinding(
            CompiledBusTargetRuntime target,
            int[] addressSources,
            int[] localAddressBases,
            int[] runtimeAddressBits,
            CompiledRuntimePinResolver[] runtimeAddressResolvers,
            byte[] readConditionClasses,
            byte[] writeConditionClasses,
            CompiledRuntimePinResolver[] readConditionResolvers,
            CompiledRuntimePinResolver[] writeConditionResolvers)
        {
            Target = target;
            AddressSources = addressSources;
            LocalAddressBases = localAddressBases;
            RuntimeAddressBits = runtimeAddressBits;
            RuntimeAddressResolvers = runtimeAddressResolvers;
            ReadConditionClasses = readConditionClasses;
            WriteConditionClasses = writeConditionClasses;
            ReadConditionResolvers = readConditionResolvers;
            WriteConditionResolvers = writeConditionResolvers;
        }

        public CompiledBusTargetRuntime Target { get; }
        public int[] AddressSources { get; }
        public int[] LocalAddressBases { get; }
        public int[] RuntimeAddressBits { get; }
        public CompiledRuntimePinResolver[] RuntimeAddressResolvers { get; }
        public byte[] ReadConditionClasses { get; }
        public byte[] WriteConditionClasses { get; }
        public CompiledRuntimePinResolver[] ReadConditionResolvers { get; }
        public CompiledRuntimePinResolver[] WriteConditionResolvers { get; }
    }

    private sealed class CompiledBusFabric : ICompiledBusFabric
    {
        private readonly HardwareCompiler _compiler;
        private readonly CompiledTargetSet _internalTargets;
        private readonly List<CompiledExternalBinding> _externalBindings = [];
        private readonly CompiledSerialBinding[] _serialBindings;
        private readonly Func<ulong> _clockEdgeProvider;
        private readonly CompiledSignalRouter _signalRouter;
        private CompiledSignalSampler? _interruptRequestSampler;
        private Action<bool>? _singleBeginBusCycleObserver;
        private Action<bool>? _singleCompleteBusCycleObserver;
        private const byte DynamicReadBeginFlag = 1 << 0;
        private const byte DynamicReadCompleteFlag = 1 << 1;
        private const byte DynamicReadObserverFlag = 1 << 2;
        private const byte DynamicWriteBeginFlag = 1 << 3;
        private const byte DynamicWriteCompleteFlag = 1 << 4;

        private Action<bool>[] _beginBusCycleObservers = [];
        private Action<bool>[] _completeBusCycleObservers = [];
        private CompiledDynamicTargetBinding[] _dynamicTargets = [];
        private byte[] _dynamicRouteFlags = [];
        private bool _hasReadBeginData;
        private bool _hasReadBeginObservers;
        private CompiledDirectRoute[] _readBeginRoutes = [];
        private CompiledDirectRoute[] _readCompleteRoutes = [];
        private CompiledDirectRoute[] _readObserverRoutes = [];
        private CompiledDirectRoute[] _writeBeginRoutes = [];
        private CompiledDirectRoute[] _writeCompleteRoutes = [];
        private ushort _latchedAddress;
        private byte _latchedValue;
        private bool _latchedValueValid;
        private bool _latchedConflict;
        private ushort _pendingWriteAddress;
        private byte _pendingWriteValue;
        private bool _pendingWriteCompletion;

        public CompiledBusFabric(
            HardwareCompiler compiler,
            CompiledBusMasterDescriptor master,
            CompiledTargetSet internalTargets,
            CompiledSerialBinding[] serialBindings,
            Func<ulong> clockEdgeProvider,
            CompiledSignalRouter signalRouter)
        {
            _compiler = compiler;
            Master = master;
            _internalTargets = internalTargets;
            _serialBindings = serialBindings;
            _clockEdgeProvider = clockEdgeProvider;
            _signalRouter = signalRouter;
            RebuildCompiledDispatch();
        }

        public CompiledBusMasterDescriptor Master { get; }
        public ulong ClockRisingEdges => _clockEdgeProvider();
        public bool InterruptRequestLow =>
            _interruptRequestSampler?.Sample() == DigitalLevel.Low;
        public bool HasCompleteBusCycleObservers =>
            _singleCompleteBusCycleObserver is not null || _completeBusCycleObservers.Length != 0;

        public void BindExternalDevice(IVirtualHardwareComponent component)
        {
            if (_externalBindings.Any(binding => ReferenceEquals(binding.Component, component))) return;
            var targets = _compiler.CompileExternalTargets(Master, component);
            if (targets.Runtimes.Length == 0) return;
            _externalBindings.Add(new CompiledExternalBinding(component, targets));
            RebuildCompiledDispatch();
        }

        public void UnbindExternalDevice(IVirtualHardwareComponent component)
        {
            var removed = false;
            for (var index = _externalBindings.Count - 1; index >= 0; index--)
            {
                if (!ReferenceEquals(_externalBindings[index].Component, component)) continue;
                _externalBindings.RemoveAt(index);
                removed = true;
            }
            if (removed) RebuildCompiledDispatch();
        }

        /// <summary>
        /// Flattens the immutable motherboard routes and the currently attached
        /// replaceable-device routes into direct runtime references. This is a
        /// dispatch cache only: the external target runtimes remain owned by the
        /// separate cartridge binding and this table is rebuilt on replacement.
        /// No mapper, address-map, board or product meaning enters the compiler.
        /// </summary>
        private void RebuildCompiledDispatch()
        {
            // A replaceable package can add or remove a physical driver from a
            // motherboard net, so signal samplers are rebound together with the
            // cartridge dispatch rather than being frozen in the motherboard unit.
            _interruptRequestSampler = Master.InterruptRequestPin?.Net is { } interruptNet
                ? _signalRouter.CompileSampler(interruptNet)
                : null;

            var sets = new CompiledTargetSet[1 + _externalBindings.Count];
            sets[0] = _internalTargets;
            for (var index = 0; index < _externalBindings.Count; index++)
                sets[index + 1] = _externalBindings[index].Targets;

            _readBeginRoutes = CompileDirectRoutes(sets, static set => set.ReadBeginRoutes);
            _readCompleteRoutes = CompileDirectRoutes(sets, static set => set.ReadCompleteRoutes);
            _readObserverRoutes = CompileDirectRoutes(sets, static set => set.ReadObserverRoutes);
            _writeBeginRoutes = CompileDirectRoutes(
                sets,
                static set => set.WriteRoutes,
                CompiledBusWritePhase.Begin);
            _writeCompleteRoutes = CompileDirectRoutes(
                sets,
                static set => set.WriteRoutes,
                CompiledBusWritePhase.Complete);

            // Dynamic targets remain package/live-state owned, but their fixed
            // physical select conditions can be classified for every bus address
            // when a replaceable device is bound. This creates a tiny per-address
            // candidate mask without freezing mutable mapper state such as MMC1
            // CIRAM A10. The mask is rebuilt whenever the cartridge changes.
            _dynamicTargets = CompileDynamicTargets(sets, _readBeginRoutes.Length);
            _dynamicRouteFlags = CompileDynamicRouteFlags(_dynamicTargets, _readBeginRoutes.Length);

            // Collapse transaction-shape decisions once at bind time. The hot
            // runtime can now skip entire empty phases and dynamic passes.
            _hasReadBeginData = HasAnyRoute(_readBeginRoutes) || HasAnyDynamicFlag(DynamicReadBeginFlag);
            _hasReadBeginObservers = HasAnyRoute(_readObserverRoutes) || HasAnyDynamicFlag(DynamicReadObserverFlag);
            RebuildBusCycleObservers();
        }

        private static CompiledDirectRoute[] CompileDirectRoutes(
            IReadOnlyList<CompiledTargetSet> sets,
            Func<CompiledTargetSet, CompiledBusRoute[]> routeSelector,
            CompiledBusWritePhase? writePhase = null)
        {
            var addressCount = routeSelector(sets[0]).Length;
            var result = new CompiledDirectRoute[addressCount];
            var targets = new List<CompiledBusTargetRuntime>(4);
            var addresses = new List<int>(4);

            for (var raw = 0; raw < addressCount; raw++)
            {
                targets.Clear();
                addresses.Clear();
                for (var setIndex = 0; setIndex < sets.Count; setIndex++)
                {
                    var set = sets[setIndex];
                    var routes = routeSelector(set);
                    var route = routes[raw & (routes.Length - 1)];
                    AppendDirectRoute(set.Runtimes, route, writePhase, targets, addresses);
                }
                result[raw] = CompiledDirectRoute.Create(targets, addresses);
            }

            return result;
        }

        private static void AppendDirectRoute(
            CompiledBusTargetRuntime[] runtimes,
            CompiledBusRoute route,
            CompiledBusWritePhase? writePhase,
            List<CompiledBusTargetRuntime> targets,
            List<int> addresses)
        {
            if (route.Count == 0) return;
            AppendDirectTarget(runtimes[route.Target0], route.Address0, writePhase, targets, addresses);
            if (route.Count == 1) return;
            AppendDirectTarget(runtimes[route.Target1], route.Address1, writePhase, targets, addresses);
            if (route.Count == 2) return;
            var overflow = route.Overflow!;
            for (var index = 0; index < overflow.Targets.Length; index++)
                AppendDirectTarget(runtimes[overflow.Targets[index]], overflow.Addresses[index], writePhase, targets, addresses);
        }

        private static void AppendDirectTarget(
            CompiledBusTargetRuntime target,
            int address,
            CompiledBusWritePhase? writePhase,
            List<CompiledBusTargetRuntime> targets,
            List<int> addresses)
        {
            if (writePhase.HasValue && target.Descriptor.WritePhase != writePhase.Value) return;
            targets.Add(target);
            addresses.Add(address);
        }

        private static bool HasAnyRoute(CompiledDirectRoute[] routes)
        {
            for (var index = 0; index < routes.Length; index++)
            {
                if (routes[index].Count != 0) return true;
            }
            return false;
        }

        private CompiledDynamicTargetBinding[] CompileDynamicTargets(
            IReadOnlyList<CompiledTargetSet> sets,
            int addressCount)
        {
            var bindings = new List<CompiledDynamicTargetBinding>();
            for (var setIndex = 0; setIndex < sets.Count; setIndex++)
            {
                var set = sets[setIndex];
                var dynamic = set.DynamicIndices;
                for (var index = 0; index < dynamic.Length; index++)
                {
                    var targetIndex = dynamic[index];
                    var target = set.Runtimes[targetIndex];
                    var descriptor = target.Descriptor;
                    var readClasses = descriptor.Read is not null || target.ObserveReadBegin is not null
                        ? CompileConditionClasses(target, addressCount, readCycle: true)
                        : [];
                    var writeClasses = descriptor.Write is not null
                        ? CompileConditionClasses(target, addressCount, readCycle: false)
                        : [];
                    var addressSources = set.AddressSources[targetIndex];
                    var runtimeAddressBits = CompileRuntimeAddressBits(addressSources);
                    bindings.Add(new CompiledDynamicTargetBinding(
                        target,
                        addressSources,
                        CompileLocalAddressBases(addressSources, addressCount),
                        runtimeAddressBits,
                        CompileRuntimeAddressResolvers(descriptor, runtimeAddressBits),
                        readClasses,
                        writeClasses,
                        CompileConditionResolvers(descriptor.ReadConditions),
                        CompileConditionResolvers(descriptor.WriteConditions)));
                }
            }
            return bindings.ToArray();
        }

        private static int[] CompileLocalAddressBases(int[] addressSources, int addressCount)
        {
            var bases = new int[addressCount];
            for (var raw = 0; raw < addressCount; raw++)
            {
                var local = 0;
                for (var bit = 0; bit < addressSources.Length; bit++)
                {
                    var root = addressSources[bit];
                    if (root >= 0 && (raw & (1 << root)) != 0) local |= 1 << bit;
                }
                bases[raw] = local;
            }
            return bases;
        }

        private static int[] CompileRuntimeAddressBits(int[] addressSources)
        {
            var bits = new List<int>();
            for (var bit = 0; bit < addressSources.Length; bit++)
            {
                if (addressSources[bit] < 0) bits.Add(bit);
            }
            return bits.Count == 0 ? [] : bits.ToArray();
        }

        private static CompiledRuntimePinResolver[] CompileRuntimeAddressResolvers(
            CompiledBusTargetDescriptor descriptor,
            int[] runtimeAddressBits)
        {
            if (runtimeAddressBits.Length == 0) return [];
            var resolvers = new CompiledRuntimePinResolver[runtimeAddressBits.Length];
            for (var index = 0; index < runtimeAddressBits.Length; index++)
                resolvers[index] = CompileRuntimePinResolver(descriptor.AddressPins[runtimeAddressBits[index]]);
            return resolvers;
        }

        private static CompiledRuntimePinResolver[] CompileConditionResolvers(
            IReadOnlyList<CompiledPinCondition> conditions)
        {
            if (conditions.Count == 0) return [];
            var resolvers = new CompiledRuntimePinResolver[conditions.Count];
            for (var index = 0; index < conditions.Count; index++)
                resolvers[index] = CompileRuntimePinResolver(conditions[index].Pin);
            return resolvers;
        }

        private static CompiledRuntimePinResolver CompileRuntimePinResolver(DigitalPin input)
        {
            var net = input.Net;
            if (net is null) return new CompiledRuntimePinResolver(input, null, null);

            DigitalPin? directOutput = null;
            foreach (var pin in net.Pins)
            {
                if (!pin.IsOutputCapable || ReferenceEquals(pin, input)) continue;
                if (directOutput is not null)
                    return new CompiledRuntimePinResolver(input, null, null);
                directOutput = pin;
            }

            return directOutput?.OwnerComponent is ICompiledBusAddressCombinationalComponent direct
                ? new CompiledRuntimePinResolver(input, direct, directOutput)
                : new CompiledRuntimePinResolver(input, null, null);
        }

        private byte[] CompileConditionClasses(
            CompiledBusTargetRuntime target,
            int addressCount,
            bool readCycle)
        {
            var classes = new byte[addressCount];
            for (var raw = 0; raw < addressCount; raw++)
                classes[raw] = _compiler.ClassifyBoundStaticConditions(
                    Master,
                    target,
                    (ushort)raw,
                    readCycle);
            return classes;
        }

        private static byte[] CompileDynamicRouteFlags(
            IReadOnlyList<CompiledDynamicTargetBinding> targets,
            int addressCount)
        {
            var flags = new byte[addressCount];
            for (var targetIndex = 0; targetIndex < targets.Count; targetIndex++)
            {
                var binding = targets[targetIndex];
                var descriptor = binding.Target.Descriptor;
                for (var raw = 0; raw < addressCount; raw++)
                {
                    if (binding.ReadConditionClasses.Length != 0
                        && binding.ReadConditionClasses[raw] != 0)
                    {
                        if (descriptor.Read is not null)
                            flags[raw] |= descriptor.ReadPhase == CompiledBusReadPhase.Begin
                                ? DynamicReadBeginFlag
                                : DynamicReadCompleteFlag;
                        if (binding.Target.ObserveReadBegin is not null)
                            flags[raw] |= DynamicReadObserverFlag;
                    }

                    if (binding.WriteConditionClasses.Length != 0
                        && binding.WriteConditionClasses[raw] != 0
                        && descriptor.Write is not null)
                    {
                        flags[raw] |= descriptor.WritePhase == CompiledBusWritePhase.Begin
                            ? DynamicWriteBeginFlag
                            : DynamicWriteCompleteFlag;
                    }
                }
            }
            return flags;
        }

        private bool HasAnyDynamicFlag(byte flag)
        {
            var flags = _dynamicRouteFlags;
            for (var index = 0; index < flags.Length; index++)
            {
                if ((flags[index] & flag) != 0) return true;
            }
            return false;
        }

        private void RebuildBusCycleObservers()
        {
            RebuildBusCycleObservers(
                static set => set.BeginBusCycleObservers,
                out _singleBeginBusCycleObserver,
                out _beginBusCycleObservers);
            RebuildBusCycleObservers(
                static set => set.CompleteBusCycleObservers,
                out _singleCompleteBusCycleObserver,
                out _completeBusCycleObservers);
        }

        private void RebuildBusCycleObservers(
            Func<CompiledTargetSet, Action<bool>[]> selector,
            out Action<bool>? singleObserver,
            out Action<bool>[] observersArray)
        {
            var observers = new List<Action<bool>>(selector(_internalTargets));
            for (var bindingIndex = 0; bindingIndex < _externalBindings.Count; bindingIndex++)
            {
                var external = selector(_externalBindings[bindingIndex].Targets);
                for (var observerIndex = 0; observerIndex < external.Length; observerIndex++)
                {
                    var observer = external[observerIndex];
                    if (!observers.Contains(observer)) observers.Add(observer);
                }
            }

            if (observers.Count == 1)
            {
                singleObserver = observers[0];
                observersArray = [];
                return;
            }

            singleObserver = null;
            observersArray = observers.Count == 0 ? [] : observers.ToArray();
        }

        public void ResetTransientState()
        {
            _latchedValueValid = false;
            _latchedConflict = false;
            _latchedAddress = 0;
            _latchedValue = 0;
            _pendingWriteAddress = 0;
            _pendingWriteValue = 0;
            _pendingWriteCompletion = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void BeginRead(ushort address)
        {
            ObserveBeginBusCycle(writeCycle: false);

            var observerIndex = address & (_readObserverRoutes.Length - 1);
            var dynamicFlags = _dynamicRouteFlags[observerIndex];
            if (_hasReadBeginObservers)
            {
                ObserveDirectReadBeginRoute(_readObserverRoutes[observerIndex]);
                if ((dynamicFlags & DynamicReadObserverFlag) != 0) ObserveDynamicReadBegins(address);
            }

            // If topology proves no target drives data during the begin phase,
            // there is no latch state to clear or address to remember. This is
            // the normal PPU memory-bus shape and removes bookkeeping from every
            // pattern/nametable fetch while preserving begin-edge observers.
            if (!_hasReadBeginData) return;

            _latchedAddress = address;
            _latchedValueValid = false;
            _latchedConflict = false;
            var routeIndex = address & (_readBeginRoutes.Length - 1);
            AccumulateDirectRouteReads(
                _readBeginRoutes[routeIndex],
                ref _latchedValue, ref _latchedValueValid, ref _latchedConflict);
            if ((dynamicFlags & DynamicReadBeginFlag) != 0)
            {
                AccumulateDynamicReads(
                    address, CompiledBusReadPhase.Begin,
                    ref _latchedValue, ref _latchedValueValid, ref _latchedConflict);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool CompleteRead(ushort address, out byte value)
        {
            var routeIndex = address & (_readCompleteRoutes.Length - 1);
            var route = _readCompleteRoutes[routeIndex];

            var dynamicComplete = (_dynamicRouteFlags[routeIndex] & DynamicReadCompleteFlag) != 0;

            // The dominant compiled-memory case is one statically resolved
            // physical target and no begin-phase driver. Return that target
            // directly instead of constructing generic contention state. A
            // dynamic target elsewhere in the address space no longer blocks
            // this fast route.
            if (!_hasReadBeginData && !dynamicComplete && route.Count == 1)
            {
                value = route.Target0!.Read(route.Address0);
                return true;
            }

            var beginValueValid = _hasReadBeginData && _latchedValueValid && address == _latchedAddress;
            var any = beginValueValid;
            var conflict = any && _latchedConflict;
            var result = any ? _latchedValue : (byte)0;
            AccumulateDirectRouteReads(route, ref result, ref any, ref conflict);
            if (dynamicComplete)
                AccumulateDynamicReads(address, CompiledBusReadPhase.Complete, ref result, ref any, ref conflict);
            _latchedValueValid = false;
            _latchedConflict = false;
            value = result;
            return any && !conflict;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(ushort address, byte value)
        {
            ObserveBeginBusCycle(writeCycle: true);
            _latchedValueValid = false;
            _latchedConflict = false;
            _pendingWriteAddress = address;
            _pendingWriteValue = value;
            _pendingWriteCompletion = true;
            var routeIndex = address & (_writeBeginRoutes.Length - 1);
            WriteDirectRoute(_writeBeginRoutes[routeIndex], value);
            if ((_dynamicRouteFlags[routeIndex] & DynamicWriteBeginFlag) != 0)
                WriteDynamicTargets(address, value, CompiledBusWritePhase.Begin);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CompleteCycle()
        {
            if (!_pendingWriteCompletion) return;
            var address = _pendingWriteAddress;
            var value = _pendingWriteValue;
            _pendingWriteCompletion = false;
            var routeIndex = address & (_writeCompleteRoutes.Length - 1);
            WriteDirectRoute(_writeCompleteRoutes[routeIndex], value);
            if ((_dynamicRouteFlags[routeIndex] & DynamicWriteCompleteFlag) != 0)
                WriteDynamicTargets(address, value, CompiledBusWritePhase.Complete);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ObserveBeginBusCycle(bool writeCycle)
        {
            ObserveBusCycleObservers(
                _singleBeginBusCycleObserver,
                _beginBusCycleObservers,
                writeCycle);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ObserveCompleteBusCycle(bool writeCycle)
        {
            ObserveBusCycleObservers(
                _singleCompleteBusCycleObserver,
                _completeBusCycleObservers,
                writeCycle);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ObserveBusCycleObservers(
            Action<bool>? single,
            Action<bool>[] observers,
            bool writeCycle)
        {
            if (single is not null)
            {
                single(writeCycle);
                return;
            }

            for (var index = 0; index < observers.Length; index++)
                observers[index](writeCycle);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte ReadSerialInput(int channel)
        {
            if ((uint)channel >= (uint)_serialBindings.Length) return 0;
            var binding = _serialBindings[channel];
            return binding.Peripheral is null ? (byte)0 : binding.Peripheral.ReadSerial();
        }

        public void WriteParallelOutputs(byte value)
        {
            for (var channel = 0; channel < _serialBindings.Length; channel++)
            {
                var binding = _serialBindings[channel];
                if (binding.Peripheral is null) continue;

                var duplicate = false;
                for (var previous = 0; previous < channel; previous++)
                {
                    if (ReferenceEquals(_serialBindings[previous].Peripheral, binding.Peripheral))
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (duplicate) continue;

                binding.Peripheral.WriteLatch((value & (1 << binding.LatchBit)) != 0);
            }
        }

        public void PresentOutputSignal(DigitalPin sourcePin, DigitalLevel level) =>
            _signalRouter.Present(sourcePin, level);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ObserveDynamicReadBegins(ushort address)
        {
            var targets = _dynamicTargets;
            var routeIndex = address & (_dynamicRouteFlags.Length - 1);
            for (var index = 0; index < targets.Length; index++)
            {
                var binding = targets[index];
                var observer = binding.Target.ObserveReadBegin;
                if (observer is null || binding.ReadConditionClasses.Length == 0) continue;
                var conditionClass = binding.ReadConditionClasses[routeIndex];
                if (conditionClass == 0) continue;
                if (!TryResolveDynamicTarget(binding, address, readCycle: true, conditionClass, out var localAddress))
                    continue;
                observer(localAddress);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AccumulateDynamicReads(
            ushort address,
            CompiledBusReadPhase phase,
            ref byte value,
            ref bool any,
            ref bool conflict)
        {
            var targets = _dynamicTargets;
            var routeIndex = address & (_dynamicRouteFlags.Length - 1);
            for (var index = 0; index < targets.Length; index++)
            {
                var binding = targets[index];
                var target = binding.Target;
                var descriptor = target.Descriptor;
                if (descriptor.Read is null || descriptor.ReadPhase != phase || binding.ReadConditionClasses.Length == 0)
                    continue;
                var conditionClass = binding.ReadConditionClasses[routeIndex];
                if (conditionClass == 0) continue;
                if (!TryResolveDynamicTarget(binding, address, readCycle: true, conditionClass, out var localAddress))
                    continue;
                AccumulateReadValue(target.Read(localAddress), ref value, ref any, ref conflict);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteDynamicTargets(ushort address, byte value, CompiledBusWritePhase phase)
        {
            var targets = _dynamicTargets;
            var routeIndex = address & (_dynamicRouteFlags.Length - 1);
            for (var index = 0; index < targets.Length; index++)
            {
                var binding = targets[index];
                var target = binding.Target;
                var descriptor = target.Descriptor;
                if (descriptor.Write is null || descriptor.WritePhase != phase || binding.WriteConditionClasses.Length == 0)
                    continue;
                var conditionClass = binding.WriteConditionClasses[routeIndex];
                if (conditionClass == 0) continue;
                if (!TryResolveDynamicTarget(binding, address, readCycle: false, conditionClass, out var localAddress))
                    continue;
                target.Write(localAddress, value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryResolveDynamicTarget(
            CompiledDynamicTargetBinding binding,
            ushort address,
            bool readCycle,
            byte conditionClass,
            out int localAddress)
        {
            var descriptor = binding.Target.Descriptor;

            if (conditionClass == 2)
            {
                var conditions = readCycle ? descriptor.ReadConditions : descriptor.WriteConditions;
                var resolvers = readCycle ? binding.ReadConditionResolvers : binding.WriteConditionResolvers;
                for (var index = 0; index < conditions.Count; index++)
                {
                    if (resolvers[index].Sample(_compiler, Master, address, readCycle)
                        != conditions[index].RequiredLevel)
                    {
                        localAddress = 0;
                        return false;
                    }
                }
            }

            var routeIndex = address & (binding.LocalAddressBases.Length - 1);
            localAddress = binding.LocalAddressBases[routeIndex];
            var runtimeBits = binding.RuntimeAddressBits;
            var runtimeResolvers = binding.RuntimeAddressResolvers;
            for (var index = 0; index < runtimeBits.Length; index++)
            {
                var level = runtimeResolvers[index].Sample(_compiler, Master, address, readCycle);
                if (level == DigitalLevel.High)
                    localAddress |= 1 << runtimeBits[index];
                else if (level != DigitalLevel.Low)
                    return false;
            }

            return descriptor.IsSelected?.Invoke(localAddress, !readCycle) ?? true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ObserveDirectReadBeginRoute(CompiledDirectRoute route)
        {
            if (route.Count == 0) return;
            route.Target0!.ObserveReadBegin?.Invoke(route.Address0);
            if (route.Count == 1) return;
            route.Target1!.ObserveReadBegin?.Invoke(route.Address1);
            if (route.Count == 2) return;
            var overflow = route.Overflow!;
            for (var index = 0; index < overflow.Targets.Length; index++)
                overflow.Targets[index].ObserveReadBegin?.Invoke(overflow.Addresses[index]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AccumulateDirectRouteReads(
            CompiledDirectRoute route,
            ref byte value,
            ref bool any,
            ref bool conflict)
        {
            if (route.Count == 0) return;
            AccumulateReadValue(route.Target0!.Read(route.Address0), ref value, ref any, ref conflict);
            if (route.Count == 1) return;
            AccumulateReadValue(route.Target1!.Read(route.Address1), ref value, ref any, ref conflict);
            if (route.Count == 2) return;
            var overflow = route.Overflow!;
            for (var index = 0; index < overflow.Targets.Length; index++)
                AccumulateReadValue(overflow.Targets[index].Read(overflow.Addresses[index]), ref value, ref any, ref conflict);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteDirectRoute(CompiledDirectRoute route, byte value)
        {
            if (route.Count == 0) return;
            route.Target0!.Write(route.Address0, value);
            if (route.Count == 1) return;
            route.Target1!.Write(route.Address1, value);
            if (route.Count == 2) return;
            var overflow = route.Overflow!;
            for (var index = 0; index < overflow.Targets.Length; index++)
                overflow.Targets[index].Write(overflow.Addresses[index], value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AccumulateReadValue(byte read, ref byte value, ref bool any, ref bool conflict)
        {
            if (!any)
            {
                value = read;
                any = true;
            }
            else if (read != value)
            {
                conflict = true;
            }
        }
    }

    private sealed class CompiledBusTargetRuntime
    {
        private readonly byte[]? _targetToMaster;
        private readonly byte[]? _masterToTarget;
        private readonly Func<int, byte>? _read;
        private readonly Action<int, byte>? _write;

        public CompiledBusTargetRuntime(CompiledBusTargetDescriptor descriptor, int[] targetToMaster)
        {
            Descriptor = descriptor;
            _read = descriptor.Read;
            _write = descriptor.Write;
            ObserveReadBegin = descriptor.ObserveReadBegin;
            var identity = true;
            for (var bit = 0; bit < targetToMaster.Length; bit++)
                identity &= targetToMaster[bit] == bit;
            if (identity) return;

            _targetToMaster = BuildPermutation(targetToMaster, reverse: false);
            _masterToTarget = BuildPermutation(targetToMaster, reverse: true);
        }

        public CompiledBusTargetDescriptor Descriptor { get; }
        public Action<int>? ObserveReadBegin { get; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte Read(int address)
        {
            var value = _read!(address);
            return _targetToMaster is null ? value : _targetToMaster[value];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(int address, byte masterValue)
        {
            var write = _write;
            if (write is null) return;
            write(address, _masterToTarget is null ? masterValue : _masterToTarget[masterValue]);
        }

        private static byte[] BuildPermutation(int[] targetToMaster, bool reverse)
        {
            var table = new byte[256];
            for (var raw = 0; raw < table.Length; raw++)
            {
                var mapped = 0;
                for (var targetBit = 0; targetBit < targetToMaster.Length; targetBit++)
                {
                    var masterBit = targetToMaster[targetBit];
                    if (!reverse)
                    {
                        if ((raw & (1 << targetBit)) != 0) mapped |= 1 << masterBit;
                    }
                    else
                    {
                        if ((raw & (1 << masterBit)) != 0) mapped |= 1 << targetBit;
                    }
                }
                table[raw] = (byte)mapped;
            }
            return table;
        }
    }

    private readonly struct CompiledDirectRoute
    {
        private readonly CompiledBusTargetRuntime? _target0;
        private readonly ushort _address0;
        private readonly CompiledBusTargetRuntime? _target1;
        private readonly ushort _address1;
        private readonly CompiledDirectRouteOverflow? _overflow;

        private CompiledDirectRoute(
            int count,
            CompiledBusTargetRuntime? target0,
            int address0,
            CompiledBusTargetRuntime? target1,
            int address1,
            CompiledDirectRouteOverflow? overflow)
        {
            Count = (byte)count;
            _target0 = target0;
            _address0 = (ushort)address0;
            _target1 = target1;
            _address1 = (ushort)address1;
            _overflow = overflow;
        }

        public byte Count { get; }

        public static CompiledDirectRoute Create(
            IReadOnlyList<CompiledBusTargetRuntime> targets,
            IReadOnlyList<int> addresses)
        {
            if (targets.Count != addresses.Count) throw new ArgumentException("Route target/address counts differ.");
            return targets.Count switch
            {
                0 => default,
                1 => new CompiledDirectRoute(1, targets[0], addresses[0], null, 0, null),
                2 => new CompiledDirectRoute(2, targets[0], addresses[0], targets[1], addresses[1], null),
                _ => new CompiledDirectRoute(
                    targets.Count,
                    targets[0],
                    addresses[0],
                    targets[1],
                    addresses[1],
                    new CompiledDirectRouteOverflow(targets.Skip(2).ToArray(), addresses.Skip(2).ToArray()))
            };
        }

        public CompiledBusTargetRuntime? Target0 => _target0;
        public int Address0 => _address0;
        public CompiledBusTargetRuntime? Target1 => _target1;
        public int Address1 => _address1;
        public CompiledDirectRouteOverflow? Overflow => _overflow;
    }

    private sealed record CompiledDirectRouteOverflow(
        CompiledBusTargetRuntime[] Targets,
        int[] Addresses);

    private readonly struct CompiledBusRoute
    {
        private readonly ushort _target0;
        private readonly ushort _address0;
        private readonly ushort _target1;
        private readonly ushort _address1;
        private readonly CompiledBusRouteOverflow? _overflow;

        private CompiledBusRoute(
            int count,
            int target0,
            int address0,
            int target1,
            int address1,
            CompiledBusRouteOverflow? overflow)
        {
            Count = (byte)count;
            _target0 = (ushort)target0;
            _address0 = (ushort)address0;
            _target1 = (ushort)target1;
            _address1 = (ushort)address1;
            _overflow = overflow;
        }

        public byte Count { get; }

        public static CompiledBusRoute Create(IReadOnlyList<int> targets, IReadOnlyList<int> addresses)
        {
            if (targets.Count != addresses.Count) throw new ArgumentException("Route target/address counts differ.");
            return targets.Count switch
            {
                0 => default,
                1 => new CompiledBusRoute(1, targets[0], addresses[0], 0, 0, null),
                2 => new CompiledBusRoute(2, targets[0], addresses[0], targets[1], addresses[1], null),
                _ => new CompiledBusRoute(
                    targets.Count,
                    targets[0],
                    addresses[0],
                    targets[1],
                    addresses[1],
                    new CompiledBusRouteOverflow(targets.Skip(2).ToArray(), addresses.Skip(2).ToArray()))
            };
        }

        public int Target0 => _target0;
        public int Address0 => _address0;
        public int Target1 => _target1;
        public int Address1 => _address1;
        public CompiledBusRouteOverflow? Overflow => _overflow;
    }

    private sealed record CompiledBusRouteOverflow(int[] Targets, int[] Addresses);

    private readonly record struct CompiledSerialBinding(
        CompiledSerialPeripheralDescriptor? Peripheral,
        int LatchBit);

    private sealed class CompiledSignalRouter
    {
        private readonly Dictionary<DigitalNet, CompiledSignalSinkDescriptor[]> _sinksByNet = new();

        public CompiledSignalRouter(VirtualHardwareBoard board)
        {
            var grouped = board.Components
                .Where(component => component is not ICompiledExternalDevice)
                .OfType<ICompiledSignalSinkProvider>()
                .SelectMany(provider => provider.GetCompiledSignalSinks())
                .Where(sink => sink.Pin.Net is not null)
                .GroupBy(sink => sink.Pin.Net!);
            foreach (var group in grouped)
                _sinksByNet[group.Key] = group.ToArray();
        }

        public void Present(DigitalPin sourcePin, DigitalLevel sourceLevel)
        {
            var net = sourcePin.Net;
            if (net is null || !_sinksByNet.TryGetValue(net, out var sinks)) return;
            var resolved = ResolveNet(net, sourcePin, sourceLevel);
            for (var index = 0; index < sinks.Length; index++)
            {
                if (ReferenceEquals(sinks[index].Pin, sourcePin)) continue;
                sinks[index].PresentLevel(resolved);
            }
        }

        public DigitalLevel SampleNet(DigitalNet? net) =>
            net is null ? DigitalLevel.Unknown : ResolveNet(net, null, DigitalLevel.Unknown);

        public CompiledSignalSampler CompileSampler(DigitalNet net) => new(net);

        private static DigitalLevel ResolveNet(DigitalNet net, DigitalPin? overriddenPin, DigitalLevel overriddenLevel)
        {
            var haveStrong = false;
            var strongLow = false;
            var strongHigh = false;
            var strongUnknown = false;
            var haveWeak = false;
            var weakLow = false;
            var weakHigh = false;
            var weakUnknown = false;

            foreach (var pin in net.Pins)
            {
                if (!pin.IsOutputCapable) continue;
                var level = ReferenceEquals(pin, overriddenPin) ? overriddenLevel : pin.DriveLevel;
                if (level == DigitalLevel.HighImpedance) continue;
                var strength = pin.DriveStrength;
                if (strength == DigitalDriveStrength.Strong)
                {
                    haveStrong = true;
                    strongLow |= level == DigitalLevel.Low;
                    strongHigh |= level == DigitalLevel.High;
                    strongUnknown |= level is not (DigitalLevel.Low or DigitalLevel.High);
                }
                else
                {
                    haveWeak = true;
                    weakLow |= level == DigitalLevel.Low;
                    weakHigh |= level == DigitalLevel.High;
                    weakUnknown |= level is not (DigitalLevel.Low or DigitalLevel.High);
                }
            }

            if (haveStrong) return Finish(strongLow, strongHigh, strongUnknown);
            if (haveWeak) return Finish(weakLow, weakHigh, weakUnknown);
            return DigitalLevel.Unknown;
        }

        private static DigitalLevel Finish(bool low, bool high, bool unknown)
        {
            if (low && high) return DigitalLevel.Contention;
            if (unknown) return DigitalLevel.Unknown;
            if (low) return DigitalLevel.Low;
            if (high) return DigitalLevel.High;
            return DigitalLevel.Unknown;
        }
    }

    private sealed class CompiledSignalSampler
    {
        private readonly DigitalPin[] _drivers;

        public CompiledSignalSampler(DigitalNet net)
        {
            _drivers = net.Pins.Where(static pin => pin.IsOutputCapable).ToArray();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public DigitalLevel Sample()
        {
            var haveStrong = false;
            var strongLow = false;
            var strongHigh = false;
            var strongUnknown = false;
            var haveWeak = false;
            var weakLow = false;
            var weakHigh = false;
            var weakUnknown = false;

            var drivers = _drivers;
            for (var index = 0; index < drivers.Length; index++)
            {
                var pin = drivers[index];
                var level = pin.DriveLevel;
                if (level == DigitalLevel.HighImpedance) continue;
                if (pin.DriveStrength == DigitalDriveStrength.Strong)
                {
                    haveStrong = true;
                    strongLow |= level == DigitalLevel.Low;
                    strongHigh |= level == DigitalLevel.High;
                    strongUnknown |= level is not (DigitalLevel.Low or DigitalLevel.High);
                }
                else
                {
                    haveWeak = true;
                    weakLow |= level == DigitalLevel.Low;
                    weakHigh |= level == DigitalLevel.High;
                    weakUnknown |= level is not (DigitalLevel.Low or DigitalLevel.High);
                }
            }

            if (haveStrong) return Finish(strongLow, strongHigh, strongUnknown);
            if (haveWeak) return Finish(weakLow, weakHigh, weakUnknown);
            return DigitalLevel.Unknown;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static DigitalLevel Finish(bool low, bool high, bool unknown)
        {
            if (low && high) return DigitalLevel.Contention;
            if (unknown) return DigitalLevel.Unknown;
            if (low) return DigitalLevel.Low;
            if (high) return DigitalLevel.High;
            return DigitalLevel.Unknown;
        }
    }

    private sealed class CompiledResetBinding
    {
        private readonly ICompiledClockedComponent _component;

        public CompiledResetBinding(ICompiledClockedComponent component)
        {
            _component = component;
            Pin = component.CompiledResetInput!;
        }

        public DigitalPin Pin { get; }

        public void Refresh() =>
            _component.SetCompiledResetAsserted(Pin.SampledLevel == _component.CompiledResetAssertedLevel);
    }

    private sealed class CompiledClockSchedule
    {
        public CompiledClockSchedule(
            int period,
            CompiledClockEvent[] events,
            CompiledClockEvent?[] eventByOffset)
        {
            Period = period;
            Events = events;
            EventByOffset = eventByOffset;
        }

        public int Period { get; }
        public CompiledClockEvent[] Events { get; }
        public CompiledClockEvent?[] EventByOffset { get; }
    }

    private sealed class CompiledClockEvent
    {
        private readonly Action _first;
        private readonly Action? _second;
        private readonly Action[]? _additional;

        public CompiledClockEvent(int delta, IReadOnlyList<Action> actions)
        {
            Delta = delta;
            _first = actions[0];
            if (actions.Count > 1) _second = actions[1];
            if (actions.Count > 2) _additional = actions.Skip(2).ToArray();
        }

        public int Delta { get; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Invoke()
        {
            _first();
            _second?.Invoke();
            var additional = _additional;
            if (additional is null) return;
            for (var index = 0; index < additional.Length; index++) additional[index]();
        }
    }
}
