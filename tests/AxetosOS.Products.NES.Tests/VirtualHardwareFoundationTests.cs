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
    public void Compiled_clock_plan_rebuilds_when_existing_net_receives_a_new_pin()
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
        var before = clockNet.ResolutionCount;

        plan.AdvanceCycles(4);

        Assert.Equal(before + 8, clockNet.ResolutionCount);
        Assert.Equal((ulong)8, clock.HalfCycleCount);
        Assert.Equal(DigitalLevel.Low, clockNet.Level);
    }

    [Fact]
    public void Chip_activation_contract_is_compiled_once_and_gates_runtime_wakeup()
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
        Assert.Equal(initialEvaluations, probe.EvaluationCount);

        gate.Set(DigitalLevel.High);
        simulator.Settle();
        Assert.Equal(initialEvaluations + 1, probe.EvaluationCount);

        data.Set(DigitalLevel.Low);
        simulator.Settle();
        Assert.Equal(initialEvaluations + 2, probe.EvaluationCount);
    }

    private sealed class GatedProbe : VirtualHardwareComponent, IInputActivationContractProvider
    {
        public GatedProbe(string componentId) : base(componentId)
        {
            Gate = AddPin("GATE", PinDirection.Input);
            Data = AddPin("DATA", PinDirection.Input);
        }

        public DigitalPin Gate { get; }
        public DigitalPin Data { get; }
        public int EvaluationCount { get; private set; }

        public PinActivationContract CompileInputActivation(DigitalPin pin)
        {
            if (ReferenceEquals(pin, Gate)) return PinActivationContract.Always;
            if (ReferenceEquals(pin, Data))
            {
                return PinActivationContract.When(() =>
                    Gate.SampledLevel == DigitalLevel.High);
            }

            return PinActivationContract.Never;
        }

        public override void Evaluate() => EvaluationCount++;
    }

}
