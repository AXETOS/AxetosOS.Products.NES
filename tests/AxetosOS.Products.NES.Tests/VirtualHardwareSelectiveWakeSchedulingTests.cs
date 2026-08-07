using Xunit;
using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Components.Clock;
using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Memory;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Components.Power;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareSelectiveWakeSchedulingTests
{
    [Fact]
    public void Power_rails_and_oscillators_settle_once_until_their_output_is_changed_externally()
    {
        var board = new VirtualHardwareBoard("one-shot-sources");
        var rail = board.Add(new DigitalPowerRail("vcc", DigitalLevel.High));
        var clock = board.Add(new DigitalOscillator("clock", 1));
        board.Connect("vcc.net", rail.Output);
        board.Connect("clock.net", clock.Output);
        var simulator = new VirtualHardwareSimulator(board);

        simulator.Settle();
        simulator.Settle();

        clock.AdvanceHalfCycle();
        simulator.Settle();
        Assert.Equal(DigitalLevel.High, clock.Output.SampledLevel);
    }

    [Fact]
    public void Bidirectional_pin_does_not_reenter_its_chip_from_its_own_output_drive()
    {
        var board = new VirtualHardwareBoard("bidirectional-package-boundary");
        var external = board.Add(new DigitalSignalSource("external", DigitalLevel.Low));
        var component = board.Add(new SelectiveBidirectionalProbe("probe"));
        board.Connect("bus", external.Output, component.Bus);
        var simulator = new VirtualHardwareSimulator(board);

        simulator.Settle();
        Assert.Equal(1, component.EvaluationCount);

        component.Bus.Drive(DigitalLevel.High);
        simulator.Settle();
        Assert.Equal(1, component.EvaluationCount);

        component.Bus.Release();
        simulator.Settle();
        Assert.Equal(1, component.EvaluationCount);
        Assert.Equal(DigitalLevel.Low, component.Bus.SampledLevel);

        external.Set(DigitalLevel.High);
        simulator.Settle();
        Assert.Equal(2, component.EvaluationCount);
        Assert.Equal(DigitalLevel.High, component.Bus.SampledLevel);
    }

    [Fact]
    public void Selective_wake_contract_type_has_been_removed()
    {
        var assembly = typeof(VirtualHardwareComponent).Assembly;
        Assert.Null(assembly.GetType(
            "AxetosOS.Products.NES.VirtualHardware.Components.ISelectiveInputDrivenVirtualHardwareComponent"));
    }

    [Fact]
    public void Hm6116_owns_input_transition_handling_without_motherboard_selective_wake_policy()
    {
        var ram = new Hm6116("ram");

        Assert.IsAssignableFrom<VirtualHardwareComponent>(ram);
        Assert.NotNull(ram.Pins);
    }

    private sealed class SelectiveBidirectionalProbe : VirtualHardwareComponent
    {
        public SelectiveBidirectionalProbe(string componentId) : base(componentId)
        {
            Bus = AddPin("BUS", PinDirection.Bidirectional);
        }

        public DigitalPin Bus { get; }
        public int EvaluationCount { get; private set; }
        protected override void OnInputChanges(ulong changedInputMask) => EvaluationCount++;
    }
}
