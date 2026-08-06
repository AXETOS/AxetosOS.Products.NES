using System.Diagnostics;
using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Simulation;

public sealed record VirtualHardwareComponentProfile(
    string ComponentId,
    ulong EvaluationCount,
    TimeSpan EvaluationTime);

public sealed record VirtualHardwareSimulationProfile(
    string BoardId,
    ulong SettleCalls,
    ulong PropagationPasses,
    int MaximumPassesPerSettle,
    TimeSpan TotalSettleTime,
    TimeSpan NetResolutionTime,
    IReadOnlyList<VirtualHardwareComponentProfile> Components)
{
    public double AveragePassesPerSettle => SettleCalls == 0 ? 0 : (double)PropagationPasses / SettleCalls;
}

/// <summary>
/// Compiled event kernel for virtual hardware boards. The live topology is
/// assigned stable integer indexes once, after which pin changes, dirty nets,
/// and active components travel through fixed-size ring buffers without hash
/// lookups, collection scans, or per-transition allocation.
/// </summary>
public sealed class VirtualHardwareSimulator
{
    private const ulong ProfileTimingSampleInterval = 256;

    private DigitalNet[] _nets;
    private IVirtualHardwareComponent[] _components;
    private IEventDrivenVirtualHardwareComponent?[] _eventDrivenComponents;
    private ICompiledInputDrivenVirtualHardwareComponent?[] _compiledInputComponents;
    private bool[] _directRouteEligibleComponents;
    private ulong[] _componentChangedInputMasks;
    private int _knownNetCount;
    private int _knownComponentCount;
    private ulong _knownTopologyRevision;

    private bool[] _componentQueued;
    private int[] _componentQueue;
    private int _componentQueueHead;
    private int _componentQueueTail;
    private int _componentQueueCount;

    private bool[] _netQueued;
    private int[] _netQueue;
    private int _netQueueHead;
    private int _netQueueTail;
    private int _netQueueCount;

    // Direct motherboard signal-chain queues. These are used only when the
    // global kernel has reached a single causal branch. Output nets and their
    // downstream packages then remain inside one compiled routing loop instead
    // of being returned to the global queues between every chip and wire.
    private bool _directRouting;
    private bool[] _directComponentQueued;
    private int[] _directComponentQueue;
    private int _directComponentQueueHead;
    private int _directComponentQueueTail;
    private int _directComponentQueueCount;
    private bool[] _directNetQueued;
    private int[] _directNetQueue;
    private int _directNetQueueHead;
    private int _directNetQueueTail;
    private int _directNetQueueCount;

    private int[] _continuouslyPolledComponents;
    private bool _strictEventDrivenTopology;

    private bool _profilingEnabled;
    private ulong _profileSettleCalls;
    private ulong _profilePropagationPasses;
    private int _profileMaximumPasses;
    private long _profileSettleTicks;
    private long _profileNetResolutionTicks;
    private ulong[] _profileComponentEvaluations;
    private ulong[] _profileComponentTimedEvaluations;
    private long[] _profileComponentTicks;

    public VirtualHardwareSimulator(VirtualHardwareBoard board)
    {
        Board = board ?? throw new ArgumentNullException(nameof(board));
        _nets = Board.Nets.ToArray();
        _components = Board.Components.ToArray();
        _eventDrivenComponents = new IEventDrivenVirtualHardwareComponent?[_components.Length];
        _compiledInputComponents = new ICompiledInputDrivenVirtualHardwareComponent?[_components.Length];
        _directRouteEligibleComponents = new bool[_components.Length];
        _componentChangedInputMasks = new ulong[_components.Length];
        _componentQueued = new bool[_components.Length];
        _componentQueue = new int[Math.Max(1, _components.Length)];
        _netQueued = new bool[_nets.Length];
        _netQueue = new int[Math.Max(1, _nets.Length)];
        _directComponentQueued = new bool[_components.Length];
        _directComponentQueue = new int[Math.Max(1, _components.Length)];
        _directNetQueued = new bool[_nets.Length];
        _directNetQueue = new int[Math.Max(1, _nets.Length)];
        _continuouslyPolledComponents = FindContinuouslyPolledComponents();
        _strictEventDrivenTopology = _continuouslyPolledComponents.Length == 0;
        _profileComponentEvaluations = new ulong[_components.Length];
        _profileComponentTimedEvaluations = new ulong[_components.Length];
        _profileComponentTicks = new long[_components.Length];
        _knownNetCount = _nets.Length;
        _knownComponentCount = _components.Length;
        _knownTopologyRevision = Board.TopologyRevision;
        CompileTopology();
        QueueAllComponents();
    }

    public VirtualHardwareBoard Board { get; }
    public ulong SettleCount { get; private set; }
    public ulong DirectSignalChainCount { get; private set; }
    public bool ProfilingEnabled => _profilingEnabled;
    public bool UsesStrictEventKernel => _strictEventDrivenTopology;
    public IReadOnlyList<string> LegacyPollingComponents =>
        _continuouslyPolledComponents.Select(index => _components[index].ComponentId).ToArray();

    public void SetProfilingEnabled(bool enabled, bool reset = true)
    {
        _profilingEnabled = enabled;
        if (reset) ResetProfile();
    }

    public void ResetProfile()
    {
        _profileSettleCalls = 0;
        _profilePropagationPasses = 0;
        _profileMaximumPasses = 0;
        _profileSettleTicks = 0;
        _profileNetResolutionTicks = 0;
        Array.Clear(_profileComponentEvaluations);
        Array.Clear(_profileComponentTimedEvaluations);
        Array.Clear(_profileComponentTicks);
    }

    public VirtualHardwareSimulationProfile GetProfileSnapshot()
    {
        var components = new VirtualHardwareComponentProfile[_components.Length];
        for (var index = 0; index < _components.Length; index++)
        {
            var measured = _profileComponentTimedEvaluations[index];
            var estimatedTicks = measured == 0
                ? 0
                : (long)((double)_profileComponentTicks[index] * _profileComponentEvaluations[index] / measured);
            components[index] = new VirtualHardwareComponentProfile(
                _components[index].ComponentId,
                _profileComponentEvaluations[index],
                StopwatchTicksToTimeSpan(estimatedTicks));
        }

        return new VirtualHardwareSimulationProfile(
            Board.BoardId,
            _profileSettleCalls,
            _profilePropagationPasses,
            _profileMaximumPasses,
            StopwatchTicksToTimeSpan(_profileSettleTicks),
            StopwatchTicksToTimeSpan(_profileNetResolutionTicks),
            components);
    }

    public int Settle(int maximumPropagationPasses = 64)
    {
        if (maximumPropagationPasses <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPropagationPasses));
        }

        RefreshTopologyIfNeeded();
        if (_strictEventDrivenTopology)
        {
            return _profilingEnabled
                ? SettleStrictProfiled(maximumPropagationPasses)
                : SettleStrict(maximumPropagationPasses);
        }

        return _profilingEnabled
            ? SettleCompatibilityProfiled(maximumPropagationPasses)
            : SettleCompatibility(maximumPropagationPasses);
    }

    /// <summary>
    /// Executes settlement for a motherboard-owned compiled plan. The plan
    /// validates topology once per batch, so individual clock phases avoid
    /// repeating public argument and topology-discovery work.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal int SettleCompiled(int maximumPropagationPasses = 64)
    {
        if (_strictEventDrivenTopology)
        {
            return _profilingEnabled
                ? SettleStrictProfiled(maximumPropagationPasses)
                : SettleStrict(maximumPropagationPasses);
        }

        return _profilingEnabled
            ? SettleCompatibilityProfiled(maximumPropagationPasses)
            : SettleCompatibility(maximumPropagationPasses);
    }

    /// <summary>
    /// Settles a transition whose first dirty net is already known by a
    /// motherboard execution plan. The net still performs its normal
    /// electrical resolution and pin callbacks; this only avoids routing the
    /// known phase root through the generic dirty-net queue.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal int SettleCompiledFromSource(
        DigitalNet sourceNet,
        int maximumPropagationPasses = 64)
    {
        ArgumentNullException.ThrowIfNull(sourceNet);

        if (!_strictEventDrivenTopology ||
            !ReferenceEquals(sourceNet.Scheduler, this) ||
            sourceNet.SchedulerIndex < 0)
        {
            return SettleCompiled(maximumPropagationPasses);
        }

        return _profilingEnabled
            ? SettleStrictFromSourceProfiled(sourceNet, maximumPropagationPasses)
            : SettleStrictFromSource(sourceNet, maximumPropagationPasses);
    }

    internal void EnsureCompiledTopologyCurrent() => RefreshTopologyIfNeeded();

    private int SettleStrictFromSource(DigitalNet sourceNet, int maximumEvents)
    {
        var events = ResolveCompiledSource(sourceNet) ? 1 : 0;
        events += DrainStrictEventQueue(maximumEvents, false);
        SettleCount++;
        return events == 0 ? 1 : events;
    }

    private int SettleStrictFromSourceProfiled(DigitalNet sourceNet, int maximumEvents)
    {
        var started = Stopwatch.GetTimestamp();
        _profileSettleCalls++;
        try
        {
            var netStarted = Stopwatch.GetTimestamp();
            var events = ResolveCompiledSource(sourceNet) ? 1 : 0;
            _profileNetResolutionTicks += Stopwatch.GetTimestamp() - netStarted;
            events += DrainStrictEventQueue(maximumEvents, true);
            SettleCount++;
            var reported = events == 0 ? 1 : events;
            _profilePropagationPasses += (ulong)reported;
            if (reported > _profileMaximumPasses) _profileMaximumPasses = reported;
            return reported;
        }
        finally
        {
            _profileSettleTicks += Stopwatch.GetTimestamp() - started;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private bool ResolveCompiledSource(DigitalNet sourceNet)
    {
        var sourceIndex = sourceNet.SchedulerIndex;
        if (_netQueueCount == 0 || _netQueue[_netQueueHead] != sourceIndex)
        {
            return false;
        }

        DequeueNet();
        _netQueued[sourceIndex] = false;
        if (!sourceNet.IsDirty)
        {
            return false;
        }

        sourceNet.Resolve();
        return true;
    }

    private int SettleStrict(int maximumEvents)
    {
        var events = DrainStrictEventQueue(maximumEvents, false);
        SettleCount++;
        return events == 0 ? 1 : events;
    }

    private int SettleStrictProfiled(int maximumEvents)
    {
        var started = Stopwatch.GetTimestamp();
        _profileSettleCalls++;
        try
        {
            var events = DrainStrictEventQueue(maximumEvents, true);
            SettleCount++;
            var reported = events == 0 ? 1 : events;
            _profilePropagationPasses += (ulong)reported;
            if (reported > _profileMaximumPasses) _profileMaximumPasses = reported;
            return reported;
        }
        finally
        {
            _profileSettleTicks += Stopwatch.GetTimestamp() - started;
        }
    }

    private int DrainStrictEventQueue(int maximumEvents, bool profile)
    {
        var events = 0;
        var eventLimit = maximumEvents * 32;
        while (_netQueueCount != 0 || _componentQueueCount != 0)
        {
            if (++events > eventLimit)
            {
                ThrowDidNotSettle(eventLimit, "queued events");
            }

            // Electrical changes always settle before the next package runs.
            // This preserves causality while avoiding propagation-wave loops.
            if (_netQueueCount != 0)
            {
                if (profile)
                {
                    var started = Stopwatch.GetTimestamp();
                    ResolveOneDirtyNet();
                    _profileNetResolutionTicks += Stopwatch.GetTimestamp() - started;
                }
                else
                {
                    ResolveOneDirtyNet();
                }
                continue;
            }

            var index = DequeueComponent();
            _componentQueued[index] = false;

            // Direct causal-chain execution remains disabled until it can
            // preserve every CPU/RAM bus phase across complete motherboard
            // workloads. Always use the validated strict global ordering.
            EvaluateComponent(index, profile);
            var eventDriven = _eventDrivenComponents[index];
            if (eventDriven is not null && eventDriven.HasPendingInternalWork)
            {
                QueueComponent(index);
            }
        }

        return events;
    }

    private int DrainDirectSignalChain(int firstComponentIndex, bool profile, int remainingEventLimit)
    {
        _directRouting = true;
        DirectSignalChainCount++;
        var events = 0;
        try
        {
            QueueDirectComponent(firstComponentIndex);

            while (_directNetQueueCount != 0 || _directComponentQueueCount != 0)
            {
                if (++events > remainingEventLimit)
                {
                    ThrowDidNotSettle(remainingEventLimit, "direct routed events");
                }

                if (_directNetQueueCount != 0)
                {
                    var netIndex = DequeueDirectNet();
                    _directNetQueued[netIndex] = false;
                    var net = _nets[netIndex];
                    if (net.IsDirty)
                    {
                        if (profile)
                        {
                            var started = Stopwatch.GetTimestamp();
                            net.Resolve();
                            _profileNetResolutionTicks += Stopwatch.GetTimestamp() - started;
                        }
                        else
                        {
                            net.Resolve();
                        }
                    }

                    continue;
                }

                var componentIndex = DequeueDirectComponent();
                _directComponentQueued[componentIndex] = false;
                EvaluateComponent(componentIndex, profile);

                var eventDriven = _eventDrivenComponents[componentIndex];
                if (eventDriven is not null && eventDriven.HasPendingInternalWork)
                {
                    QueueDirectComponent(componentIndex);
                }
            }
        }
        finally
        {
            _directRouting = false;
        }

        // The caller already counted the first component as a global event.
        return events == 0 ? 0 : events - 1;
    }

    private void ResolveOneDirtyNet()
    {
        var index = DequeueNet();
        _netQueued[index] = false;
        var net = _nets[index];
        if (net.IsDirty) net.Resolve();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void EvaluateComponent(int index, bool profile)
    {
        var changedInputMask = _componentChangedInputMasks[index];
        _componentChangedInputMasks[index] = 0;
        var compiled = _compiledInputComponents[index];

        if (!profile)
        {
            if (compiled is not null) compiled.Evaluate(changedInputMask);
            else _components[index].Evaluate();
            return;
        }

        var evaluation = ++_profileComponentEvaluations[index];
        if ((evaluation - 1) % ProfileTimingSampleInterval == 0)
        {
            var started = Stopwatch.GetTimestamp();
            if (compiled is not null) compiled.Evaluate(changedInputMask);
            else _components[index].Evaluate();
            _profileComponentTicks[index] += Stopwatch.GetTimestamp() - started;
            _profileComponentTimedEvaluations[index]++;
        }
        else
        {
            if (compiled is not null) compiled.Evaluate(changedInputMask);
            else _components[index].Evaluate();
        }
    }

    private int SettleCompatibility(int maximumPasses)
    {
        for (var pass = 1; pass <= maximumPasses; pass++)
        {
            QueueContinuouslyPolledComponents();
            ResolveDirtyNets();
            EvaluateActiveComponents(false);
            ResolveDirtyNets();
            SettleCount++;
            if (_componentQueueCount == 0 && _netQueueCount == 0) return pass;
        }

        ThrowDidNotSettle(maximumPasses, "propagation passes");
        return 0;
    }

    private int SettleCompatibilityProfiled(int maximumPasses)
    {
        var started = Stopwatch.GetTimestamp();
        _profileSettleCalls++;
        try
        {
            for (var pass = 1; pass <= maximumPasses; pass++)
            {
                QueueContinuouslyPolledComponents();
                ResolveDirtyNetsProfiled();
                EvaluateActiveComponents(true);
                ResolveDirtyNetsProfiled();
                SettleCount++;
                _profilePropagationPasses++;
                if (_componentQueueCount == 0 && _netQueueCount == 0)
                {
                    if (pass > _profileMaximumPasses) _profileMaximumPasses = pass;
                    return pass;
                }
            }
        }
        finally
        {
            _profileSettleTicks += Stopwatch.GetTimestamp() - started;
        }

        ThrowDidNotSettle(maximumPasses, "propagation passes");
        return 0;
    }

    private void EvaluateActiveComponents(bool profile)
    {
        var count = _componentQueueCount;
        for (var item = 0; item < count; item++)
        {
            var index = DequeueComponent();
            _componentQueued[index] = false;

            EvaluateComponent(index, profile);

            var eventDriven = _eventDrivenComponents[index];
            if (eventDriven is not null && eventDriven.HasPendingInternalWork) QueueComponent(index);
        }
    }

    private void ResolveDirtyNets()
    {
        while (_netQueueCount != 0)
        {
            var index = DequeueNet();
            _netQueued[index] = false;
            var net = _nets[index];
            if (net.IsDirty) net.Resolve();
        }
    }

    private void ResolveDirtyNetsProfiled()
    {
        var started = Stopwatch.GetTimestamp();
        ResolveDirtyNets();
        _profileNetResolutionTicks += Stopwatch.GetTimestamp() - started;
    }

    private void CompileTopology()
    {
        for (var netIndex = 0; netIndex < _nets.Length; netIndex++)
        {
            var net = _nets[netIndex];
            net.CompileTopology();
            net.SchedulerIndex = netIndex;
            net.Scheduler = this;
            if (net.IsDirty) NotifyNetDirty(netIndex);
        }

        for (var componentIndex = 0; componentIndex < _components.Length; componentIndex++)
        {
            var component = _components[componentIndex];
            _eventDrivenComponents[componentIndex] = component as IEventDrivenVirtualHardwareComponent;
            _compiledInputComponents[componentIndex] = component as ICompiledInputDrivenVirtualHardwareComponent;
            _directRouteEligibleComponents[componentIndex] = component is ICombinationalVirtualHardwareComponent;
            var clockEdgeOwner = component as IClockEdgeDrivenVirtualHardwareComponent;
            var activationProvider = component as IInputActivationContractProvider;
            var selectiveInputOwner = component as ISelectiveInputDrivenVirtualHardwareComponent;
            var pins = component.Pins;
            for (var pinIndex = 0; pinIndex < pins.Count; pinIndex++)
            {
                var pin = pins[pinIndex];
                pin.OwnerComponentIndex = componentIndex;
                pin.OwnerInputChangeMask = pinIndex < 64 ? 1UL << pinIndex : ulong.MaxValue;
                pin.Scheduler = this;
                pin.ClockEdgeOwner = clockEdgeOwner;
                pin.WakeOwnerOnSampleChange = pin.Direction != PinDirection.Output;
                pin.ActivationContract = CompilePinActivation(
                    pin,
                    activationProvider,
                    selectiveInputOwner);
            }
        }
    }


    private static PinActivationContract CompilePinActivation(
        DigitalPin pin,
        IInputActivationContractProvider? activationProvider,
        ISelectiveInputDrivenVirtualHardwareComponent? selectiveInputOwner)
    {
        if (pin.Direction == PinDirection.Output)
        {
            return PinActivationContract.Never;
        }

        if (activationProvider is not null)
        {
            return activationProvider.CompileInputActivation(pin);
        }

        if (selectiveInputOwner is not null)
        {
            // Compatibility path for packages not yet migrated to explicit
            // topology-time activation contracts. The predicate is captured
            // once during compilation rather than rediscovered at runtime.
            return PinActivationContract.When(
                () => selectiveInputOwner.ShouldWakeForSampledPin(pin));
        }

        return PinActivationContract.Always;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void NotifySampledPinChanged(
        int componentIndex,
        ulong changedInputMask,
        PinActivationContract activationContract)
    {
        if (componentIndex < 0) return;

        // Once a package is already scheduled, further changed pins are folded
        // into its pending package-level mask. There is no need to re-run gate
        // predicates or queue logic for every additional pin transition.
        if (_componentQueued[componentIndex] || _directComponentQueued[componentIndex])
        {
            _componentChangedInputMasks[componentIndex] |= changedInputMask;
            return;
        }

        if (!activationContract.IsActive()) return;
        _componentChangedInputMasks[componentIndex] |= changedInputMask;
        if (_directRouting && _directRouteEligibleComponents[componentIndex]) QueueDirectComponent(componentIndex);
        else QueueComponent(componentIndex);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void NotifyComponentActive(int componentIndex)
    {
        if (componentIndex < 0) return;
        if (_directRouting && _directRouteEligibleComponents[componentIndex]) QueueDirectComponent(componentIndex);
        else QueueComponent(componentIndex);
    }

    private void QueueAllComponents()
    {
        for (var index = 0; index < _components.Length; index++)
        {
            _componentChangedInputMasks[index] = ulong.MaxValue;
            QueueComponent(index);
        }
    }

    private void QueueContinuouslyPolledComponents()
    {
        for (var index = 0; index < _continuouslyPolledComponents.Length; index++)
        {
            QueueComponent(_continuouslyPolledComponents[index]);
        }
    }

    private void QueueComponent(int index)
    {
        if (_componentQueued[index]) return;
        _componentQueued[index] = true;
        _componentQueue[_componentQueueTail] = index;
        _componentQueueTail++;
        if (_componentQueueTail == _componentQueue.Length) _componentQueueTail = 0;
        _componentQueueCount++;
    }

    private int DequeueComponent()
    {
        var index = _componentQueue[_componentQueueHead];
        _componentQueueHead++;
        if (_componentQueueHead == _componentQueue.Length) _componentQueueHead = 0;
        _componentQueueCount--;
        return index;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void NotifyNetDirty(int index)
    {
        if (_directRouting)
        {
            QueueDirectNet(index);
            return;
        }

        if (_netQueued[index]) return;
        _netQueued[index] = true;
        _netQueue[_netQueueTail] = index;
        _netQueueTail++;
        if (_netQueueTail == _netQueue.Length) _netQueueTail = 0;
        _netQueueCount++;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void QueueDirectComponent(int index)
    {
        if (_directComponentQueued[index]) return;
        _directComponentQueued[index] = true;
        _directComponentQueue[_directComponentQueueTail] = index;
        _directComponentQueueTail++;
        if (_directComponentQueueTail == _directComponentQueue.Length) _directComponentQueueTail = 0;
        _directComponentQueueCount++;
    }

    private int DequeueDirectComponent()
    {
        var index = _directComponentQueue[_directComponentQueueHead];
        _directComponentQueueHead++;
        if (_directComponentQueueHead == _directComponentQueue.Length) _directComponentQueueHead = 0;
        _directComponentQueueCount--;
        return index;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void QueueDirectNet(int index)
    {
        if (_directNetQueued[index]) return;
        _directNetQueued[index] = true;
        _directNetQueue[_directNetQueueTail] = index;
        _directNetQueueTail++;
        if (_directNetQueueTail == _directNetQueue.Length) _directNetQueueTail = 0;
        _directNetQueueCount++;
    }

    private int DequeueDirectNet()
    {
        var index = _directNetQueue[_directNetQueueHead];
        _directNetQueueHead++;
        if (_directNetQueueHead == _directNetQueue.Length) _directNetQueueHead = 0;
        _directNetQueueCount--;
        return index;
    }

    private int DequeueNet()
    {
        var index = _netQueue[_netQueueHead];
        _netQueueHead++;
        if (_netQueueHead == _netQueue.Length) _netQueueHead = 0;
        _netQueueCount--;
        return index;
    }

    private int[] FindContinuouslyPolledComponents() =>
        _components
            .Select((component, index) => (component, index))
            .Where(static item => item.component is not IInputDrivenVirtualHardwareComponent)
            .Select(static item => item.index)
            .ToArray();

    private void RefreshTopologyIfNeeded()
    {
        if (_knownTopologyRevision == Board.TopologyRevision &&
            _knownNetCount == Board.Nets.Count &&
            _knownComponentCount == Board.Components.Count)
        {
            return;
        }

        _nets = Board.Nets.ToArray();
        _components = Board.Components.ToArray();
        _knownNetCount = _nets.Length;
        _knownComponentCount = _components.Length;
        _knownTopologyRevision = Board.TopologyRevision;

        _eventDrivenComponents = new IEventDrivenVirtualHardwareComponent?[_components.Length];
        _compiledInputComponents = new ICompiledInputDrivenVirtualHardwareComponent?[_components.Length];
        _directRouteEligibleComponents = new bool[_components.Length];
        _componentChangedInputMasks = new ulong[_components.Length];
        _componentQueued = new bool[_components.Length];
        _componentQueue = new int[Math.Max(1, _components.Length)];
        _componentQueueHead = 0;
        _componentQueueTail = 0;
        _componentQueueCount = 0;

        _netQueued = new bool[_nets.Length];
        _netQueue = new int[Math.Max(1, _nets.Length)];
        _netQueueHead = 0;
        _netQueueTail = 0;
        _netQueueCount = 0;

        _directRouting = false;
        _directComponentQueued = new bool[_components.Length];
        _directComponentQueue = new int[Math.Max(1, _components.Length)];
        _directComponentQueueHead = 0;
        _directComponentQueueTail = 0;
        _directComponentQueueCount = 0;
        _directNetQueued = new bool[_nets.Length];
        _directNetQueue = new int[Math.Max(1, _nets.Length)];
        _directNetQueueHead = 0;
        _directNetQueueTail = 0;
        _directNetQueueCount = 0;

        _continuouslyPolledComponents = FindContinuouslyPolledComponents();
        _strictEventDrivenTopology = _continuouslyPolledComponents.Length == 0;
        _profileComponentEvaluations = new ulong[_components.Length];
        _profileComponentTimedEvaluations = new ulong[_components.Length];
        _profileComponentTicks = new long[_components.Length];
        CompileTopology();
        QueueAllComponents();
        ResetProfile();
    }

    private void ThrowDidNotSettle(int maximum, string unit) =>
        throw new InvalidOperationException(
            $"Board '{Board.BoardId}' did not settle after {maximum} {unit}.");

    private static TimeSpan StopwatchTicksToTimeSpan(long ticks) =>
        TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);
}
