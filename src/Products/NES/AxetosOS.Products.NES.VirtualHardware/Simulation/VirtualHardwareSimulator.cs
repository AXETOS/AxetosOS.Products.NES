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
    private int _knownNetCount;
    private int _knownComponentCount;

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
        _componentQueued = new bool[_components.Length];
        _componentQueue = new int[Math.Max(1, _components.Length)];
        _netQueued = new bool[_nets.Length];
        _netQueue = new int[Math.Max(1, _nets.Length)];
        _continuouslyPolledComponents = FindContinuouslyPolledComponents();
        _strictEventDrivenTopology = _continuouslyPolledComponents.Length == 0;
        _profileComponentEvaluations = new ulong[_components.Length];
        _profileComponentTimedEvaluations = new ulong[_components.Length];
        _profileComponentTicks = new long[_components.Length];
        _knownNetCount = _nets.Length;
        _knownComponentCount = _components.Length;
        CompileTopology();
        QueueAllComponents();
    }

    public VirtualHardwareBoard Board { get; }
    public ulong SettleCount { get; private set; }
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

    private int SettleStrict(int maximumWaves)
    {
        var wave = 0;
        while (_componentQueueCount != 0 || _netQueueCount != 0)
        {
            if (++wave > maximumWaves)
            {
                ThrowDidNotSettle(maximumWaves, "event waves");
            }

            ResolveDirtyNets();
            EvaluateActiveComponents(false);
            SettleCount++;
        }

        if (wave == 0)
        {
            SettleCount++;
            return 1;
        }

        return wave;
    }

    private int SettleStrictProfiled(int maximumWaves)
    {
        var started = Stopwatch.GetTimestamp();
        _profileSettleCalls++;
        var wave = 0;
        try
        {
            while (_componentQueueCount != 0 || _netQueueCount != 0)
            {
                if (++wave > maximumWaves)
                {
                    ThrowDidNotSettle(maximumWaves, "event waves");
                }

                ResolveDirtyNetsProfiled();
                EvaluateActiveComponents(true);
                SettleCount++;
                _profilePropagationPasses++;
            }

            if (wave == 0)
            {
                SettleCount++;
                _profilePropagationPasses++;
                wave = 1;
            }

            if (wave > _profileMaximumPasses) _profileMaximumPasses = wave;
            return wave;
        }
        finally
        {
            _profileSettleTicks += Stopwatch.GetTimestamp() - started;
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

            if (!profile)
            {
                _components[index].Evaluate();
            }
            else
            {
                var evaluation = ++_profileComponentEvaluations[index];
                if ((evaluation - 1) % ProfileTimingSampleInterval == 0)
                {
                    var started = Stopwatch.GetTimestamp();
                    _components[index].Evaluate();
                    _profileComponentTicks[index] += Stopwatch.GetTimestamp() - started;
                    _profileComponentTimedEvaluations[index]++;
                }
                else
                {
                    _components[index].Evaluate();
                }
            }

            if (_components[index] is IEventDrivenVirtualHardwareComponent { HasPendingInternalWork: true })
            {
                QueueComponent(index);
            }
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
            net.SchedulerIndex = netIndex;
            net.SchedulerDirtied = QueueDirtyNet;
            if (net.IsDirty) QueueDirtyNet(netIndex);
        }

        for (var componentIndex = 0; componentIndex < _components.Length; componentIndex++)
        {
            var pins = _components[componentIndex].Pins;
            for (var pinIndex = 0; pinIndex < pins.Count; pinIndex++)
            {
                var pin = pins[pinIndex];
                pin.OwnerComponentIndex = componentIndex;
                pin.SchedulerSampledChanged = OnPinSampledChanged;
            }
        }
    }

    private void OnPinSampledChanged(int componentIndex, DigitalPin pin)
    {
        if (pin.Direction == PinDirection.Output) return;

        var component = _components[componentIndex];
        if (component is ISelectiveInputDrivenVirtualHardwareComponent selective &&
            !selective.ShouldWakeForSampledPin(pin))
        {
            return;
        }

        QueueComponent(componentIndex);
    }

    private void QueueAllComponents()
    {
        for (var index = 0; index < _components.Length; index++) QueueComponent(index);
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

    private void QueueDirtyNet(int index)
    {
        if (_netQueued[index]) return;
        _netQueued[index] = true;
        _netQueue[_netQueueTail] = index;
        _netQueueTail++;
        if (_netQueueTail == _netQueue.Length) _netQueueTail = 0;
        _netQueueCount++;
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
        if (_knownNetCount == Board.Nets.Count && _knownComponentCount == Board.Components.Count) return;

        _nets = Board.Nets.ToArray();
        _components = Board.Components.ToArray();
        _knownNetCount = _nets.Length;
        _knownComponentCount = _components.Length;

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
