using Xunit;
using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Components.Logic;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Components.Clock;
using AxetosOS.Products.NES.VirtualHardware.Components.Passives;
using AxetosOS.Products.NES.VirtualHardware.Components.Power;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareFoundationTests
{
    [Fact]
    public void Net_resolves_power_rail_into_connected_input_pin()
    {
        var board = new VirtualHardwareBoard("test.board.power");
        var vcc = board.Add(new DigitalPowerRail("test.vcc", DigitalLevel.High));
        var gate = board.Add(new NotGate("test.inverter"));
        board.Connect("VCC", vcc.Output, gate.Input);

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();

        Assert.Equal(DigitalLevel.High, gate.Input.SampledLevel);
    }

    [Fact]
    public void Pin_driven_gate_reacts_only_to_resolved_input_and_drives_output_net()
    {
        var board = new VirtualHardwareBoard("test.board.logic");
        var ground = board.Add(new DigitalPowerRail("test.ground", DigitalLevel.Low));
        var inverter = board.Add(new NotGate("test.inverter"));
        board.Connect("INPUT", ground.Output, inverter.Input);
        var output = board.Connect("OUTPUT", inverter.Output);

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();

        Assert.Equal(DigitalLevel.Low, inverter.Input.SampledLevel);
        Assert.Equal(DigitalLevel.High, output.Level);
    }

    [Fact]
    public void Equal_strength_opposing_outputs_are_reported_as_contention()
    {
        var board = new VirtualHardwareBoard("test.board.contention");
        var vcc = board.Add(new DigitalPowerRail("test.vcc", DigitalLevel.High));
        var ground = board.Add(new DigitalPowerRail("test.ground", DigitalLevel.Low));
        var net = board.Connect("SHORTED_NET", vcc.Output, ground.Output);

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();

        Assert.Equal(DigitalLevel.Contention, net.Level);
    }

    [Fact]
    public void Strong_chip_output_overrides_weak_pull_resistor()
    {
        var board = new VirtualHardwareBoard("test.board.pullup");
        var vcc = board.Add(new DigitalPowerRail("test.vcc", DigitalLevel.High));
        var ground = board.Add(new DigitalPowerRail("test.ground", DigitalLevel.Low));
        var pullUp = board.Add(new PullResistor("test.pullup"));

        board.Connect("VCC", vcc.Output, pullUp.Rail);
        var signal = board.Connect("SIGNAL", pullUp.Node, ground.Output);

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();

        Assert.Equal(DigitalLevel.Low, signal.Level);
        Assert.Equal(DigitalDriveStrength.Weak, pullUp.Node.DriveStrength);
    }
    [Fact]
    public void Compiled_clock_plan_advances_real_oscillator_and_settles_connected_hardware()
    {
        var board = new VirtualHardwareBoard("test.board.compiled-clock");
        var clock = board.Add(new DigitalOscillator("test.clock", 1_000_000));
        var inverter = board.Add(new NotGate("test.inverter"));
        board.Connect("CLOCK", clock.Output, inverter.Input);
        var output = board.Connect("OUTPUT", inverter.Output);

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();
        var plan = new CompiledClockExecutionPlan(clock, simulator);

        plan.AdvanceCycles(1);

        Assert.Equal((ulong)2, clock.HalfCycleCount);
        Assert.Equal(DigitalLevel.Low, clock.Output.DriveLevel);
        Assert.Equal(DigitalLevel.High, output.Level);
    }

    [Fact]
    public void Compiled_clock_plan_applies_an_explicit_topology_recompile()
    {
        var board = new VirtualHardwareBoard("test.board.topology-revision");
        var clock = board.Add(new DigitalOscillator("test.clock", 1_000_000));
        var first = board.Add(new NotGate("test.first"));
        board.Connect("CLOCK", clock.Output, first.Input);

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();
        var plan = new CompiledClockExecutionPlan(clock, simulator);

        var second = board.Add(new NotGate("test.second"));
        board.Connect("CLOCK", second.Input);
        var secondOutput = board.Connect("SECOND.OUTPUT", second.Output);
        plan.RecompileTopology();

        plan.AdvanceHalfCycle();

        Assert.Equal(DigitalLevel.High, second.Input.SampledLevel);
        Assert.Equal(DigitalLevel.Low, secondOutput.Level);
    }

    [Fact]
    public void Compiled_clock_plan_resolves_the_known_clock_net_for_each_phase()
    {
        var board = new VirtualHardwareBoard("test.board.compiled-phase-root");
        var clock = board.Add(new DigitalOscillator("test.clock", 1_000_000));
        var inverter = board.Add(new NotGate("test.inverter"));
        var clockNet = board.Connect("CLOCK", clock.Output, inverter.Input);
        board.Connect("OUTPUT", inverter.Output);

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();
        var plan = new CompiledClockExecutionPlan(clock, simulator);
        simulator.SetProfilingEnabled(true);
        plan.AdvanceCycles(4);

        var counters = simulator.GetPerformanceCounters();
        Assert.Equal((ulong)8, clock.HalfCycleCount);
        Assert.Equal(DigitalLevel.Low, clockNet.Level);
        Assert.Equal((ulong)8, counters.CompiledClockSourceDispatches);
    }

    [Fact]
    public void Every_changed_input_delivery_is_processed_by_the_receiving_chip()
    {
        var board = new VirtualHardwareBoard("test.board.activation-contract");
        var gate = board.Add(new DigitalSignalSource("test.gate", DigitalLevel.Low));
        var data = board.Add(new DigitalSignalSource("test.data", DigitalLevel.Low));
        var probe = board.Add(new GatedProbe("test.probe"));
        board.Connect("GATE", gate.Output, probe.Gate);
        board.Connect("DATA", data.Output, probe.Data);

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();
        var initialEvaluations = probe.EvaluationCount;

        data.Set(DigitalLevel.High);
        simulator.Settle();
        Assert.Equal(initialEvaluations + 1, probe.EvaluationCount);

        gate.Set(DigitalLevel.High);
        simulator.Settle();
        Assert.Equal(initialEvaluations + 2, probe.EvaluationCount);

        data.Set(DigitalLevel.Low);
        simulator.Settle();
        Assert.Equal(initialEvaluations + 3, probe.EvaluationCount);
    }

    [Fact]
    public void Compiled_receiver_fanout_preserves_output_sampling_and_input_activation()
    {
        var board = new VirtualHardwareBoard("receiver-fanout");
        var source = board.Add(new DigitalSignalSource("source"));
        var inverter = board.Add(new NotGate("inverter"));
        board.Connect("signal", source.Output, inverter.Input);
        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();
        simulator.SetProfilingEnabled(true);

        source.Set(DigitalLevel.High);
        simulator.Settle();

        Assert.Equal(DigitalLevel.High, source.Output.SampledLevel);
        Assert.Equal(DigitalLevel.High, inverter.Input.SampledLevel);
        Assert.Equal(DigitalLevel.Low, inverter.Output.DriveLevel);
        var counters = simulator.GetPerformanceCounters();
        Assert.True(counters.ReceiverDeliveries >= 1);
        Assert.True(counters.PinSampleDeliveries >= counters.ReceiverDeliveries);
    }

    private sealed class GatedProbe : VirtualHardwareComponent
    {
        public GatedProbe(string componentId) : base(componentId)
        {
            Gate = AddPin("GATE", PinDirection.Input);
            Data = AddPin("DATA", PinDirection.Input);
        }

        public DigitalPin Gate { get; }
        public DigitalPin Data { get; }
        public int EvaluationCount { get; private set; }

        protected override void OnInputChanges(ulong changedInputMask) => EvaluationCount++;
    }

    [Fact]
    public void Changed_input_pins_are_delivered_immediately_without_waiting_for_settle()
    {
        var board = new VirtualHardwareBoard("test.board.package-mask");
        var first = board.Add(new DigitalSignalSource("test.first", DigitalLevel.Low));
        var second = board.Add(new DigitalSignalSource("test.second", DigitalLevel.Low));
        var probe = board.Add(new MaskProbe("test.mask-probe"));
        board.Connect("FIRST", first.Output, probe.First);
        board.Connect("SECOND", second.Output, probe.Second);

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();
        var initialEvaluations = probe.EvaluationCount;

        first.Set(DigitalLevel.High);
        Assert.Equal(initialEvaluations + 1, probe.EvaluationCount);
        Assert.Equal(DigitalLevel.High, probe.First.SampledLevel);

        second.Set(DigitalLevel.High);
        Assert.Equal(initialEvaluations + 2, probe.EvaluationCount);
        Assert.Equal(DigitalLevel.High, probe.Second.SampledLevel);
        Assert.Equal(0b11UL, probe.ObservedInputMask & 0b11UL);
    }

    private sealed class MaskProbe : VirtualHardwareComponent
    {
        public MaskProbe(string componentId) : base(componentId)
        {
            First = AddPin("FIRST", PinDirection.Input);
            Second = AddPin("SECOND", PinDirection.Input);
        }

        public DigitalPin First { get; }
        public DigitalPin Second { get; }
        public int EvaluationCount { get; private set; }
        public ulong ObservedInputMask { get; private set; }

        protected override void OnInputChanges(ulong changedInputMask)
        {
            if (changedInputMask == 0) return;
            ObservedInputMask |= changedInputMask;
            EvaluationCount++;
        }
    }


    [Fact]
    public void Strict_event_routing_preserves_complete_combinational_signal_chain()
    {
        var board = new VirtualHardwareBoard("test.board.direct-signal-chain");
        var source = board.Add(new DigitalSignalSource("test.source", DigitalLevel.Low));
        var first = board.Add(new NotGate("test.first"));
        var second = board.Add(new NotGate("test.second"));
        board.Connect("SOURCE", source.Output, first.Input);
        board.Connect("MIDDLE", first.Output, second.Input);
        var output = board.Connect("OUTPUT", second.Output);

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();
        source.Set(DigitalLevel.High);
        simulator.Settle();

        Assert.Equal(DigitalLevel.High, first.Input.SampledLevel);
        Assert.Equal(DigitalLevel.Low, first.Output.SampledLevel);
        Assert.Equal(DigitalLevel.Low, second.Input.SampledLevel);
        Assert.Equal(DigitalLevel.High, output.Level);
    }


    [Fact]
    public void Direct_trace_propagation_requires_no_settle_call()
    {
        var board = new VirtualHardwareBoard("test.board.immediate-trace");
        var source = board.Add(new DigitalSignalSource("source", DigitalLevel.Low));
        var inverter = board.Add(new NotGate("inverter"));
        board.Connect("INPUT", source.Output, inverter.Input);
        var output = board.Connect("OUTPUT", inverter.Output);

        _ = new VirtualHardwareSimulator(board);

        source.Set(DigitalLevel.High);

        Assert.Equal(DigitalLevel.High, inverter.Input.SampledLevel);
        Assert.Equal(DigitalLevel.Low, output.Level);
    }

    [Fact]
    public void Propagation_kernel_has_no_runtime_signal_or_component_queues()
    {
        var queueFields = typeof(VirtualHardwareSimulator)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Where(field => field.Name.Contains("queue", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(queueFields);
    }

    [Fact]
    public void One_chip_reaction_publishes_its_changed_outputs_as_one_direct_change_set()
    {
        var board = new VirtualHardwareBoard("test.board.package-output-change-set");
        var trigger = board.Add(new DigitalSignalSource("trigger", DigitalLevel.Low));
        var producer = board.Add(new DualOutputProbe("producer"));
        var receiver = board.Add(new DualInputProbe("receiver"));
        board.Connect("TRIGGER", trigger.Output, producer.Trigger);
        board.Connect("FIRST", producer.First, receiver.First);
        board.Connect("SECOND", producer.Second, receiver.Second);

        _ = new VirtualHardwareSimulator(board);
        var baseline = receiver.EvaluationCount;

        trigger.Set(DigitalLevel.High);

        Assert.Equal(baseline + 1, receiver.EvaluationCount);
        Assert.True(receiver.LastFirstHigh);
        Assert.True(receiver.LastSecondHigh);
    }

    [Fact]
    public void Initial_topology_is_fully_presented_before_the_first_package_reaction()
    {
        var board = new VirtualHardwareBoard("test.board.complete-initial-state");
        var high = board.Add(new DigitalSignalSource("high", DigitalLevel.High));
        var low = board.Add(new DigitalSignalSource("low", DigitalLevel.Low));
        var reset = board.Add(new DigitalSignalSource("reset", DigitalLevel.High));
        var probe = board.Add(new InitialStateProbe("probe"));
        board.Connect("VCC", high.Output, probe.Vcc);
        board.Connect("GND", low.Output, probe.Gnd);
        board.Connect("RESET", reset.Output, probe.ResetBar);

        _ = new VirtualHardwareSimulator(board);

        Assert.Equal(1, probe.EvaluationCount);
        Assert.True(probe.SawCompleteInitialState);
        Assert.False(probe.SawIndeterminateInitialState);
    }

    private sealed class DualOutputProbe : VirtualHardwareComponent
    {
        public DualOutputProbe(string componentId) : base(componentId)
        {
            Trigger = AddPin("TRIGGER", PinDirection.Input);
            First = AddPin("FIRST", PinDirection.Output);
            Second = AddPin("SECOND", PinDirection.Output);
            First.Drive(DigitalLevel.Low);
            Second.Drive(DigitalLevel.Low);
        }

        public DigitalPin Trigger { get; }
        public DigitalPin First { get; }
        public DigitalPin Second { get; }

        protected override void OnInputChanges(ulong changedInputMask)
        {
            var level = Trigger.SampledLevel == DigitalLevel.High
                ? DigitalLevel.High
                : DigitalLevel.Low;
            First.Drive(level);
            Second.Drive(level);
        }
    }

    private sealed class DualInputProbe : VirtualHardwareComponent
    {
        public DualInputProbe(string componentId) : base(componentId)
        {
            First = AddPin("FIRST", PinDirection.Input);
            Second = AddPin("SECOND", PinDirection.Input);
        }

        public DigitalPin First { get; }
        public DigitalPin Second { get; }
        public int EvaluationCount { get; private set; }
        public bool LastFirstHigh { get; private set; }
        public bool LastSecondHigh { get; private set; }

        protected override void OnInputChanges(ulong changedInputMask)
        {
            EvaluationCount++;
            LastFirstHigh = First.SampledLevel == DigitalLevel.High;
            LastSecondHigh = Second.SampledLevel == DigitalLevel.High;
        }
    }

    private sealed class InitialStateProbe : VirtualHardwareComponent
    {
        public InitialStateProbe(string componentId) : base(componentId)
        {
            Vcc = AddPin("VCC", PinDirection.Input);
            Gnd = AddPin("GND", PinDirection.Input);
            ResetBar = AddPin("RESET_BAR", PinDirection.Input);
        }

        public DigitalPin Vcc { get; }
        public DigitalPin Gnd { get; }
        public DigitalPin ResetBar { get; }
        public int EvaluationCount { get; private set; }
        public bool SawCompleteInitialState { get; private set; }
        public bool SawIndeterminateInitialState { get; private set; }

        protected override void OnInputChanges(ulong changedInputMask)
        {
            EvaluationCount++;
            var complete = Vcc.SampledLevel == DigitalLevel.High &&
                           Gnd.SampledLevel == DigitalLevel.Low &&
                           ResetBar.SampledLevel == DigitalLevel.High;
            SawCompleteInitialState |= complete;
            SawIndeterminateInitialState |= !complete;
        }
    }

    [Fact]
    public void Performance_counters_report_and_reset_compiled_motherboard_activity()
    {
        var board = new VirtualHardwareBoard("test.board.performance-counters");
        var source = board.Add(new DigitalSignalSource("test.source", DigitalLevel.Low));
        var gate = board.Add(new NotGate("test.gate"));
        board.Connect("INPUT", source.Output, gate.Input);
        board.Connect("OUTPUT", gate.Output);

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();
        simulator.SetProfilingEnabled(true);

        source.Set(DigitalLevel.High);
        simulator.Settle();

        var counters = simulator.GetPerformanceCounters();
        Assert.True(counters.SettleCalls > 0);
        Assert.True(counters.StrictEvents > 0);
        Assert.True(counters.ComponentEvaluations > 0);
        Assert.True(counters.NetResolutionAttempts > 0);
        Assert.True(counters.NetLevelChanges > 0);
        Assert.True(counters.PinSampleDeliveries > 0);
        Assert.True(counters.ReceiverDeliveries > 0);

        simulator.ResetPerformanceCounters();
        counters = simulator.GetPerformanceCounters();
        Assert.Equal(0UL, counters.SettleCalls);
        Assert.Equal(0UL, counters.StrictEvents);
        Assert.Equal(0UL, counters.ComponentEvaluations);
        Assert.Equal(0UL, counters.NetResolutionAttempts);
    }


    [Fact]
    public void Topology_compiled_output_route_delivers_input_and_chip_owns_reaction()
    {
        var board = new VirtualHardwareBoard("compiled-receiver-slot");
        var source = board.Add(new DigitalSignalSource("source", DigitalLevel.Low));
        var sink = board.Add(new NotGate("sink"));
        board.Connect("signal", source.Output, sink.Input);
        var output = board.Connect("output", sink.Output);

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();
        simulator.SetProfilingEnabled(true);

        source.Set(DigitalLevel.High);
        simulator.Settle();

        Assert.Equal(DigitalLevel.High, sink.Input.SampledLevel);
        Assert.Equal(DigitalLevel.Low, output.Level);
        var counters = simulator.GetPerformanceCounters();
        Assert.True(counters.ReceiverDeliveries >= 1);
        Assert.True(counters.ComponentEvaluations >= 1);
    }

}
