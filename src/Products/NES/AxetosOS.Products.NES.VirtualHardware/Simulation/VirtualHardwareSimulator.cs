using System.Diagnostics;
using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Simulation;

public sealed record VirtualHardwarePerformanceCounters(
    ulong SettleCalls,
    ulong StrictEvents,
    ulong ComponentEvaluations,
    ulong NetResolutionAttempts,
    ulong NetLevelChanges,
    ulong PinSampleDeliveries,
    ulong ReceiverDeliveries,
    ulong TopologyCompilations,
    ulong CompiledClockSourceDispatches);

public sealed record VirtualHardwareComponentProfile(
    string ComponentId,
    ulong EvaluationCount,
    TimeSpan EvaluationTime)
{
    public ulong TimedEvaluationCount { get; init; }
}

public sealed record VirtualHardwareSectionProfile(
    string ComponentId,
    string Section,
    ulong SampleCount,
    TimeSpan EstimatedTime);

public sealed record VirtualHardwareSimulationProfile(
    string BoardId,
    ulong SettleCalls,
    ulong PropagationEvents,
    int MaximumEventsPerSettle,
    TimeSpan TotalSettleTime,
    TimeSpan NetResolutionTime,
    IReadOnlyList<VirtualHardwareComponentProfile> Components)
{
    public double AverageEventsPerSettle => SettleCalls == 0 ? 0 : (double)PropagationEvents / SettleCalls;
    public ulong NetResolutionAttempts { get; init; }
    public ulong TimedNetResolutionSamples { get; init; }
    public IReadOnlyList<VirtualHardwareSectionProfile> Sections { get; init; } = [];
}

/// <summary>
/// Topology compiler and optional diagnostics collector for the virtual board.
/// Runtime signal propagation does not pass through this object when profiling
/// is disabled: output pins change their attached nets immediately, nets
/// present the resulting level immediately, and receiving packages execute
/// directly. Profiling is opt-in and samples only one in 256 hot operations.
/// </summary>
public sealed class VirtualHardwareSimulator
{
    private const ulong ProfileTimingSampleInterval = 256;
    private static readonly int ProfileSectionCount = Enum.GetValues<VirtualHardwareProfileSection>().Length;

    private DigitalNet[] _nets = [];
    private VirtualHardwareComponent[] _components = [];

    private bool _profilingEnabled;
    private ulong _profileSettleCalls;
    private ulong _profilePropagationEvents;
    private int _profileMaximumEvents;
    private long _profileSettleTicks;
    private long _profileNetResolutionTicks;
    private ulong _profileNetResolutionAttempts;
    private ulong _profileTimedNetResolutions;
    private ulong[] _profileComponentEvaluations = [];
    private ulong[] _profileComponentTimedEvaluations = [];
    private long[] _profileComponentTicks = [];
    private ulong[] _profileSectionSamples = [];
    private long[] _profileSectionTicks = [];

    private ulong _counterSettleCalls;
    private ulong _counterStrictEvents;
    private ulong _counterComponentEvaluations;
    private ulong _counterNetResolutionAttempts;
    private ulong _counterNetLevelChanges;
    private ulong _counterPinSampleDeliveries;
    private ulong _counterReceiverDeliveries;
    private ulong _counterTopologyCompilations;
    private ulong _counterCompiledClockSourceDispatches;

    public VirtualHardwareSimulator(VirtualHardwareBoard board)
    {
        Board = board ?? throw new ArgumentNullException(nameof(board));
        RecompileTopology();
    }

    public VirtualHardwareBoard Board { get; }
    public bool ProfilingEnabled => _profilingEnabled;

    public VirtualHardwarePerformanceCounters GetPerformanceCounters() => new(
        _counterSettleCalls,
        _counterStrictEvents,
        _counterComponentEvaluations,
        _counterNetResolutionAttempts,
        _counterNetLevelChanges,
        _counterPinSampleDeliveries,
        _counterReceiverDeliveries,
        _counterTopologyCompilations,
        _counterCompiledClockSourceDispatches);

    public void ResetPerformanceCounters()
    {
        _counterSettleCalls = 0;
        _counterStrictEvents = 0;
        _counterComponentEvaluations = 0;
        _counterNetResolutionAttempts = 0;
        _counterNetLevelChanges = 0;
        _counterPinSampleDeliveries = 0;
        _counterReceiverDeliveries = 0;
        _counterTopologyCompilations = 0;
        _counterCompiledClockSourceDispatches = 0;
    }

    public void SetProfilingEnabled(bool enabled, bool reset = true)
    {
        _profilingEnabled = enabled;
        for (var index = 0; index < _nets.Length; index++)
            _nets[index].Diagnostics = enabled ? this : null;

        if (reset)
        {
            ResetProfile();
            ResetPerformanceCounters();
        }
    }

    public void ResetProfile()
    {
        _profileSettleCalls = 0;
        _profilePropagationEvents = 0;
        _profileMaximumEvents = 0;
        _profileSettleTicks = 0;
        _profileNetResolutionTicks = 0;
        _profileNetResolutionAttempts = 0;
        _profileTimedNetResolutions = 0;
        Array.Clear(_profileComponentEvaluations);
        Array.Clear(_profileComponentTimedEvaluations);
        Array.Clear(_profileComponentTicks);
        Array.Clear(_profileSectionSamples);
        Array.Clear(_profileSectionTicks);
    }

    public VirtualHardwareSimulationProfile GetProfileSnapshot()
    {
        var components = new VirtualHardwareComponentProfile[_components.Length];
        for (var index = 0; index < _components.Length; index++)
        {
            var measured = _profileComponentTimedEvaluations[index];
            var estimatedTicks = ScaleSampledTicks(
                _profileComponentTicks[index],
                _profileComponentEvaluations[index],
                measured);
            components[index] = new VirtualHardwareComponentProfile(
                _components[index].ComponentId,
                _profileComponentEvaluations[index],
                StopwatchTicksToTimeSpan(estimatedTicks))
            {
                TimedEvaluationCount = measured
            };
        }

        var sections = new List<VirtualHardwareSectionProfile>();
        for (var componentIndex = 0; componentIndex < _components.Length; componentIndex++)
        {
            var measuredComponentEvaluations = _profileComponentTimedEvaluations[componentIndex];
            if (measuredComponentEvaluations == 0) continue;

            for (var sectionIndex = 0; sectionIndex < ProfileSectionCount; sectionIndex++)
            {
                var flatIndex = (componentIndex * ProfileSectionCount) + sectionIndex;
                var samples = _profileSectionSamples[flatIndex];
                if (samples == 0) continue;

                var estimatedTicks = ScaleSampledTicks(
                    _profileSectionTicks[flatIndex],
                    _profileComponentEvaluations[componentIndex],
                    measuredComponentEvaluations);
                sections.Add(new VirtualHardwareSectionProfile(
                    _components[componentIndex].ComponentId,
                    ((VirtualHardwareProfileSection)sectionIndex).ToString(),
                    samples,
                    StopwatchTicksToTimeSpan(estimatedTicks)));
            }
        }

        var estimatedNetTicks = ScaleSampledTicks(
            _profileNetResolutionTicks,
            _profileNetResolutionAttempts,
            _profileTimedNetResolutions);

        return new VirtualHardwareSimulationProfile(
            Board.BoardId,
            _profileSettleCalls,
            _profilePropagationEvents,
            _profileMaximumEvents,
            StopwatchTicksToTimeSpan(_profileSettleTicks),
            StopwatchTicksToTimeSpan(estimatedNetTicks),
            components)
        {
            NetResolutionAttempts = _profileNetResolutionAttempts,
            TimedNetResolutionSamples = _profileTimedNetResolutions,
            Sections = sections
        };
    }

    /// <summary>
    /// Compatibility synchronization point for existing callers/tests. Signal
    /// propagation is already complete when this method is reached, so it does
    /// no electrical work and owns no queue to drain.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Settle()
    {
        if (!_profilingEnabled) return;
        _counterSettleCalls++;
        _profileSettleCalls++;
    }

    internal void RecompileTopologyInternal() => RecompileTopology();

    public void RecompileTopology()
    {
        _nets = Board.Nets.ToArray();
        _components = Board.Components
            .Select(component => component as VirtualHardwareComponent
                ?? throw new InvalidOperationException($"Component '{component.ComponentId}' does not use the pin-reactive package base."))
            .ToArray();

        _profileComponentEvaluations = new ulong[_components.Length];
        _profileComponentTimedEvaluations = new ulong[_components.Length];
        _profileComponentTicks = new long[_components.Length];
        _profileSectionSamples = new ulong[_components.Length * ProfileSectionCount];
        _profileSectionTicks = new long[_components.Length * ProfileSectionCount];

        for (var componentIndex = 0; componentIndex < _components.Length; componentIndex++)
        {
            var pins = _components[componentIndex].Pins;
            for (var pinIndex = 0; pinIndex < pins.Count; pinIndex++)
                pins[pinIndex].OwnerComponentIndex = componentIndex;
        }

        // Compile every trace first. Initial board publication is deliberately
        // two-phase: every connected pin first sees the complete static board
        // state, then packages react. This is still direct propagation and owns
        // no runtime queue; it only prevents construction order from creating
        // software-only startup edges.
        for (var netIndex = 0; netIndex < _nets.Length; netIndex++)
        {
            var net = _nets[netIndex];
            net.Diagnostics = _profilingEnabled ? this : null;
            net.CompileTopology();
        }

        if (_profilingEnabled) _counterTopologyCompilations++;

        for (var netIndex = 0; netIndex < _nets.Length; netIndex++)
            _nets[netIndex].PresentInitialState();

        for (var netIndex = 0; netIndex < _nets.Length; netIndex++)
            _nets[netIndex].ReactPresentedInputs();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void DeliverInputImmediate(
        int componentIndex,
        VirtualHardwareComponent component,
        ulong changedInputMask)
    {
        _counterReceiverDeliveries++;
        _counterComponentEvaluations++;
        _counterStrictEvents++;
        _profilePropagationEvents++;
        if (_profileMaximumEvents < 1) _profileMaximumEvents = 1;

        if ((uint)componentIndex >= (uint)_profileComponentEvaluations.Length)
        {
            component.ReceiveInputChanges(changedInputMask);
            return;
        }

        var evaluation = ++_profileComponentEvaluations[componentIndex];
        if ((evaluation - 1) % ProfileTimingSampleInterval != 0)
        {
            component.ReceiveInputChanges(changedInputMask);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        component.ReceiveInputChangesProfiled(
            changedInputMask,
            new VirtualHardwareProfileSample(this, componentIndex));
        _profileComponentTicks[componentIndex] += Stopwatch.GetTimestamp() - started;
        _profileComponentTimedEvaluations[componentIndex]++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RecordNetResolutionAttempt()
    {
        _counterNetResolutionAttempts++;
        _counterStrictEvents++;
        _profilePropagationEvents++;
        _profileNetResolutionAttempts++;
        if (_profileMaximumEvents < 1) _profileMaximumEvents = 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal long BeginNetResolutionTimingSample()
    {
        if ((_profileNetResolutionAttempts - 1) % ProfileTimingSampleInterval != 0) return 0;
        _profileTimedNetResolutions++;
        return Stopwatch.GetTimestamp();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EndNetResolutionTimingSample(long started)
    {
        if (started == 0) return;
        _profileNetResolutionTicks += Stopwatch.GetTimestamp() - started;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RecordNetLevelChange(int pinDeliveries)
    {
        _counterNetLevelChanges++;
        _counterPinSampleDeliveries += (ulong)pinDeliveries;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RecordCompiledClockDispatch()
    {
        if (!_profilingEnabled) return;
        _counterCompiledClockSourceDispatches++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RecordProfileSection(
        int componentIndex,
        VirtualHardwareProfileSection section,
        long ticks)
    {
        if ((uint)componentIndex >= (uint)_components.Length || ticks < 0) return;
        var flatIndex = (componentIndex * ProfileSectionCount) + (int)section;
        // A sampled section can legitimately complete within one Stopwatch tick on
        // a fast host. It is still a real profiling sample even when its measured
        // duration quantizes to zero; keep the sample count and let accumulated
        // timing remain zero for that individual observation.
        _profileSectionSamples[flatIndex]++;
        _profileSectionTicks[flatIndex] += ticks;
    }

    private static long ScaleSampledTicks(long sampledTicks, ulong total, ulong measured)
    {
        if (sampledTicks <= 0 || total == 0 || measured == 0) return 0;
        return (long)((double)sampledTicks * total / measured);
    }

    private static TimeSpan StopwatchTicksToTimeSpan(long ticks) =>
        ticks <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);
}
