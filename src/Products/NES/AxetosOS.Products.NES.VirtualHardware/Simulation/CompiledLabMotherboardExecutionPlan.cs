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

            var readRoutes = new CompiledBusRoute[addressCount];
            var writeRoutes = new CompiledBusRoute[addressCount];
            for (var raw = 0; raw < addressCount; raw++)
            {
                var address = (uint)raw;
                readRoutes[raw] = CompileRoute(master, targetArray, addressSources, dynamicTargets, address, readCycle: true);
                writeRoutes[raw] = CompileRoute(master, targetArray, addressSources, dynamicTargets, address, readCycle: false);
            }

            var dynamicIndices = Enumerable.Range(0, dynamicTargets.Length)
                .Where(index => dynamicTargets[index])
                .ToArray();
            return new CompiledTargetSet(
                targetArray,
                addressSources,
                dynamicIndices,
                readRoutes,
                writeRoutes);
        }

        private bool CanStaticallyCompileTarget(
            CompiledBusMasterDescriptor master,
            CompiledBusTargetRuntime target,
            int[] addressSources,
            int addressCount)
        {
            var descriptor = target.Descriptor;
            if (descriptor.Read is not null
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

        public bool TryResolveRuntimeTarget(
            CompiledBusMasterDescriptor master,
            CompiledBusTargetRuntime target,
            int[] addressSources,
            ushort masterAddress,
            bool readCycle,
            out int localAddress)
        {
            localAddress = 0;
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
                    return false;
                }
                if (level != conditions[conditionIndex].RequiredLevel) return false;
            }

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
            return true;
        }

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
            bool readCycle)
        {
            var selectedTargets = new List<int>(2);
            var localAddresses = new List<int>(2);

            for (var targetIndex = 0; targetIndex < targets.Length; targetIndex++)
            {
                if (dynamicTargets[targetIndex]) continue;
                var descriptor = targets[targetIndex].Descriptor;
                if (readCycle && descriptor.Read is null) continue;
                if (!readCycle && descriptor.Write is null) continue;

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
            bool allowExternalDevices = true)
        {
            if (depth > 16)
                throw new InvalidOperationException("Combinational topology recursion exceeded the hardware compiler limit.");
            var net = input.Net;
            return net is null
                ? DigitalLevel.Unknown
                : EvaluateNet(net, address, readCycle, master, depth + 1, allowExternalDevices);
        }

        private DigitalLevel EvaluateNet(
            DigitalNet net,
            uint address,
            bool readCycle,
            CompiledBusMasterDescriptor? master,
            int depth,
            bool allowExternalDevices = true)
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
                var drive = EvaluateDriver(driver, address, readCycle, master, depth + 1, allowExternalDevices);
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
            bool allowExternalDevices = true)
        {
            if (!allowExternalDevices && driver.OwnerComponent is ICompiledExternalDevice)
                throw new NotSupportedException("Replaceable hardware remains outside the fixed-circuit compilation unit.");
            if (master is not null)
            {
                var masterDrive = master.EvaluateDrivenPin(driver, address, readCycle);
                if (masterDrive.HasValue) return masterDrive.Value;
            }

            if (driver.OwnerComponent is ICompiledCombinationalComponent combinational
                && combinational.TryEvaluateCompiledOutput(
                    driver,
                    pin => EvaluateInput(pin, address, readCycle, master, depth + 1, allowExternalDevices),
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
            CompiledBusRoute[] readRoutes,
            CompiledBusRoute[] writeRoutes)
        {
            Runtimes = runtimes;
            AddressSources = addressSources;
            DynamicIndices = dynamicIndices;
            ReadRoutes = readRoutes;
            WriteRoutes = writeRoutes;
        }

        public CompiledBusTargetRuntime[] Runtimes { get; }
        public int[][] AddressSources { get; }
        public int[] DynamicIndices { get; }
        public CompiledBusRoute[] ReadRoutes { get; }
        public CompiledBusRoute[] WriteRoutes { get; }

        public static CompiledTargetSet Empty(int addressWidth)
        {
            var count = 1 << addressWidth;
            return new CompiledTargetSet(
                Array.Empty<CompiledBusTargetRuntime>(),
                Array.Empty<int[]>(),
                Array.Empty<int>(),
                new CompiledBusRoute[count],
                new CompiledBusRoute[count]);
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

    private sealed class CompiledBusFabric : ICompiledBusFabric
    {
        private readonly HardwareCompiler _compiler;
        private readonly CompiledTargetSet _internalTargets;
        private readonly List<CompiledExternalBinding> _externalBindings = [];
        private readonly CompiledSerialBinding[] _serialBindings;
        private readonly Func<ulong> _clockEdgeProvider;
        private readonly CompiledSignalRouter _signalRouter;
        private ushort _latchedAddress;
        private byte _latchedValue;
        private bool _latchedValueValid;
        private bool _latchedConflict;

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
        }

        public CompiledBusMasterDescriptor Master { get; }
        public ulong ClockRisingEdges => _clockEdgeProvider();
        public bool InterruptRequestLow =>
            Master.InterruptRequestPin is not null
            && _signalRouter.SampleNet(Master.InterruptRequestPin.Net) == DigitalLevel.Low;

        public void BindExternalDevice(IVirtualHardwareComponent component)
        {
            if (_externalBindings.Any(binding => ReferenceEquals(binding.Component, component))) return;
            var targets = _compiler.CompileExternalTargets(Master, component);
            if (targets.Runtimes.Length == 0) return;
            _externalBindings.Add(new CompiledExternalBinding(component, targets));
        }

        public void UnbindExternalDevice(IVirtualHardwareComponent component)
        {
            for (var index = _externalBindings.Count - 1; index >= 0; index--)
            {
                if (ReferenceEquals(_externalBindings[index].Component, component))
                    _externalBindings.RemoveAt(index);
            }
        }

        public void ResetTransientState()
        {
            _latchedValueValid = false;
            _latchedConflict = false;
            _latchedAddress = 0;
            _latchedValue = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void BeginRead(ushort address)
        {
            _latchedAddress = address;
            _latchedValueValid = false;
            _latchedConflict = false;
            AccumulateTargetSetReads(
                _internalTargets, address, CompiledBusReadPhase.Begin,
                ref _latchedValue, ref _latchedValueValid, ref _latchedConflict);
            for (var index = 0; index < _externalBindings.Count; index++)
                AccumulateTargetSetReads(
                    _externalBindings[index].Targets, address, CompiledBusReadPhase.Begin,
                    ref _latchedValue, ref _latchedValueValid, ref _latchedConflict);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool CompleteRead(ushort address, out byte value)
        {
            var any = _latchedValueValid && address == _latchedAddress;
            var conflict = any && _latchedConflict;
            var result = any ? _latchedValue : (byte)0;
            AccumulateTargetSetReads(
                _internalTargets, address, CompiledBusReadPhase.Complete,
                ref result, ref any, ref conflict);
            for (var index = 0; index < _externalBindings.Count; index++)
                AccumulateTargetSetReads(
                    _externalBindings[index].Targets, address, CompiledBusReadPhase.Complete,
                    ref result, ref any, ref conflict);
            _latchedValueValid = false;
            _latchedConflict = false;
            value = result;
            return any && !conflict;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(ushort address, byte value)
        {
            _latchedValueValid = false;
            _latchedConflict = false;
            WriteTargetSet(_internalTargets, address, value);
            for (var index = 0; index < _externalBindings.Count; index++)
                WriteTargetSet(_externalBindings[index].Targets, address, value);
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
        private void AccumulateTargetSetReads(
            CompiledTargetSet set,
            ushort address,
            CompiledBusReadPhase phase,
            ref byte value,
            ref bool any,
            ref bool conflict)
        {
            var route = set.ReadRoutes[address & (set.ReadRoutes.Length - 1)];
            AccumulateRouteReads(set.Runtimes, route, phase, ref value, ref any, ref conflict);

            var dynamic = set.DynamicIndices;
            for (var index = 0; index < dynamic.Length; index++)
            {
                var targetIndex = dynamic[index];
                var target = set.Runtimes[targetIndex];
                if (target.Descriptor.Read is null || target.Descriptor.ReadPhase != phase) continue;
                if (!_compiler.TryResolveRuntimeTarget(
                    Master, target, set.AddressSources[targetIndex], address, readCycle: true, out var localAddress))
                    continue;
                AccumulateReadValue(target.Read(localAddress), ref value, ref any, ref conflict);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteTargetSet(CompiledTargetSet set, ushort address, byte value)
        {
            var route = set.WriteRoutes[address & (set.WriteRoutes.Length - 1)];
            WriteRoute(set.Runtimes, route, value);

            var dynamic = set.DynamicIndices;
            for (var index = 0; index < dynamic.Length; index++)
            {
                var targetIndex = dynamic[index];
                var target = set.Runtimes[targetIndex];
                if (target.Descriptor.Write is null) continue;
                if (!_compiler.TryResolveRuntimeTarget(
                    Master, target, set.AddressSources[targetIndex], address, readCycle: false, out var localAddress))
                    continue;
                target.Write(localAddress, value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteRoute(CompiledBusTargetRuntime[] targets, CompiledBusRoute route, byte value)
        {
            if (route.Count == 0) return;
            targets[route.Target0].Write(route.Address0, value);
            if (route.Count == 1) return;
            targets[route.Target1].Write(route.Address1, value);
            if (route.Count == 2) return;
            var overflow = route.Overflow!;
            for (var index = 0; index < overflow.Targets.Length; index++)
                targets[overflow.Targets[index]].Write(overflow.Addresses[index], value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AccumulateRouteReads(
            CompiledBusTargetRuntime[] targets,
            CompiledBusRoute route,
            CompiledBusReadPhase phase,
            ref byte value,
            ref bool any,
            ref bool conflict)
        {
            if (route.Count == 0) return;
            AccumulateReadTarget(targets, route.Target0, route.Address0, phase, ref value, ref any, ref conflict);
            if (route.Count == 1) return;
            AccumulateReadTarget(targets, route.Target1, route.Address1, phase, ref value, ref any, ref conflict);
            if (route.Count == 2) return;
            var overflow = route.Overflow!;
            for (var index = 0; index < overflow.Targets.Length; index++)
                AccumulateReadTarget(targets, overflow.Targets[index], overflow.Addresses[index], phase, ref value, ref any, ref conflict);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AccumulateReadTarget(
            CompiledBusTargetRuntime[] targets,
            int targetIndex,
            int localAddress,
            CompiledBusReadPhase phase,
            ref byte value,
            ref bool any,
            ref bool conflict)
        {
            var target = targets[targetIndex];
            if (target.Descriptor.ReadPhase != phase || target.Descriptor.Read is null) return;
            AccumulateReadValue(target.Read(localAddress), ref value, ref any, ref conflict);
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

        public CompiledBusTargetRuntime(CompiledBusTargetDescriptor descriptor, int[] targetToMaster)
        {
            Descriptor = descriptor;
            var identity = true;
            for (var bit = 0; bit < targetToMaster.Length; bit++)
                identity &= targetToMaster[bit] == bit;
            if (identity) return;

            _targetToMaster = BuildPermutation(targetToMaster, reverse: false);
            _masterToTarget = BuildPermutation(targetToMaster, reverse: true);
        }

        public CompiledBusTargetDescriptor Descriptor { get; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte Read(int address)
        {
            var value = Descriptor.Read!(address);
            return _targetToMaster is null ? value : _targetToMaster[value];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(int address, byte masterValue)
        {
            if (Descriptor.Write is null) return;
            Descriptor.Write(address, _masterToTarget is null ? masterValue : _masterToTarget[masterValue]);
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
