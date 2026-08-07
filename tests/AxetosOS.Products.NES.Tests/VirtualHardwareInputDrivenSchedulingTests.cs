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
    public void Scheduler_owned_internal_work_contract_has_been_removed()
    {
        var assembly = typeof(VirtualHardwareComponent).Assembly;
        Assert.Null(assembly.GetType(
            "AxetosOS.Products.NES.VirtualHardware.Components.IEventDrivenVirtualHardwareComponent"));
    }

    [Fact]
    public void Output_only_signal_source_is_never_invoked_as_an_input_driven_chip()
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
        Assert.Equal(0UL, sourceProfile.EvaluationCount);
    }

    private sealed class CountingInputDrivenComponent : VirtualHardwareComponent
    {
        public CountingInputDrivenComponent(string componentId) : base(componentId)
        {
            Input = AddPin("IN", PinDirection.Input);
            Output = AddPin("OUT", PinDirection.Output);
        }

        public DigitalPin Input { get; }
        public DigitalPin Output { get; }
        public int EvaluationCount { get; private set; }

        protected override void OnInputChanges(ulong changedInputMask)
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
