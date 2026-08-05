using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Components.Power;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareInputDrivenSchedulingTests
{
    [Fact]
    public void Settled_input_driven_component_is_not_polled_until_a_package_pin_changes()
    {
        var board = new VirtualHardwareBoard("input-driven-scheduling");
        var source = board.Add(new DigitalSignalSource("source", DigitalLevel.Low));
        var component = board.Add(new CountingInputDrivenComponent("component"));
        board.Connect("INPUT", source.Output, component.Input);
        var output = board.Connect("OUTPUT", component.Output);

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();
        var evaluationsAfterInitialSettlement = component.EvaluationCount;

        simulator.Settle();
        simulator.Settle();

        Assert.Equal(evaluationsAfterInitialSettlement, component.EvaluationCount);
        Assert.Equal(DigitalLevel.High, output.Level);

        source.Set(DigitalLevel.High);
        simulator.Settle();

        Assert.True(component.EvaluationCount > evaluationsAfterInitialSettlement);
        Assert.Equal(DigitalLevel.Low, output.Level);
    }


    [Fact]
    public void Input_driven_component_does_not_requeue_itself_when_only_its_output_drive_changes()
    {
        var board = new VirtualHardwareBoard("input-driven-output-scheduling");
        var source = board.Add(new DigitalSignalSource("source", DigitalLevel.Low));
        var component = board.Add(new CountingInputDrivenComponent("component"));
        board.Connect("INPUT", source.Output, component.Input);
        board.Connect("OUTPUT", component.Output);

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();

        Assert.Equal(1, component.EvaluationCount);

        simulator.Settle();
        Assert.Equal(1, component.EvaluationCount);
    }

    [Fact]
    public void Event_driven_component_continues_until_its_internal_work_is_complete()
    {
        var board = new VirtualHardwareBoard("event-driven-scheduling");
        var component = board.Add(new CountingEventDrivenComponent("component", 3));
        board.Connect("OUTPUT", component.Output);

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();

        Assert.Equal(3, component.EvaluationCount);
        Assert.False(component.HasPendingInternalWork);

        simulator.Settle();
        Assert.Equal(3, component.EvaluationCount);
    }

    [Fact]
    public void Digital_signal_source_is_evaluated_only_once_after_topology_initialization()
    {
        var board = new VirtualHardwareBoard("one-shot-source-scheduling");
        var source = board.Add(new DigitalSignalSource("source", DigitalLevel.High));
        board.Connect("OUTPUT", source.Output);

        var simulator = new VirtualHardwareSimulator(board);
        simulator.SetProfilingEnabled(true);
        board.PowerOn();
        simulator.Settle();
        simulator.Settle();

        var profile = simulator.GetProfileSnapshot();
        var sourceProfile = Assert.Single(profile.Components, item => item.ComponentId == "source");
        Assert.Equal(1UL, sourceProfile.EvaluationCount);
    }

    private sealed class CountingEventDrivenComponent :
        VirtualHardwareComponent,
        IEventDrivenVirtualHardwareComponent
    {
        private int _remainingEvaluations;

        public CountingEventDrivenComponent(string componentId, int evaluations) : base(componentId)
        {
            _remainingEvaluations = evaluations;
            Output = AddPin("OUT", PinDirection.Output);
        }

        public DigitalPin Output { get; }
        public int EvaluationCount { get; private set; }
        public bool HasPendingInternalWork => _remainingEvaluations > 0;

        public override void Evaluate()
        {
            if (_remainingEvaluations <= 0) return;
            EvaluationCount++;
            _remainingEvaluations--;
            Output.Drive((EvaluationCount & 1) == 0 ? DigitalLevel.Low : DigitalLevel.High);
        }
    }

    private sealed class CountingInputDrivenComponent :
        VirtualHardwareComponent,
        IInputDrivenVirtualHardwareComponent
    {
        public CountingInputDrivenComponent(string componentId) : base(componentId)
        {
            Input = AddPin("IN", PinDirection.Input);
            Output = AddPin("OUT", PinDirection.Output);
        }

        public DigitalPin Input { get; }
        public DigitalPin Output { get; }
        public int EvaluationCount { get; private set; }

        public override void Evaluate()
        {
            EvaluationCount++;
            Output.Drive(Input.SampledLevel switch
            {
                DigitalLevel.Low => DigitalLevel.High,
                DigitalLevel.High => DigitalLevel.Low,
                _ => DigitalLevel.Unknown,
            });
        }
    }
}
