using Xunit;
using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components.Logic;
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
}
