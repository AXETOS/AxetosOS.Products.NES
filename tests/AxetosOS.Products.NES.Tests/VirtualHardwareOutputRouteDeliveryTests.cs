using Xunit;
using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Logic;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Memory;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Components.Power;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareOutputRouteDeliveryTests
{
    [Fact]
    public void Latched_data_delivery_is_processed_by_the_chip_while_output_remains_unchanged()
    {
        var board = new VirtualHardwareBoard("test.output-route.latch");
        var vcc = board.Add(new DigitalPowerRail("vcc", DigitalLevel.High));
        var gnd = board.Add(new DigitalPowerRail("gnd", DigitalLevel.Low));
        var source = board.Add(new DigitalSignalSource("source", DigitalLevel.Low));
        var latch = board.Add(new Sn74Ls373("latch"));

        board.Connect("VCC", vcc.Output, latch.Vcc, latch.OutputEnableBar);
        board.Connect("GND", gnd.Output, latch.Gnd, latch.LatchEnable);
        board.Connect("D0", source.Output, latch.D.Pins[0]);

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();
        simulator.SetProfilingEnabled(true);

        source.Set(DigitalLevel.High);
        simulator.Settle();

        Assert.Equal(DigitalLevel.High, latch.D.Pins[0].SampledLevel);
        Assert.Equal(0, latch.LatchedKnownMask & 1);
        var counters = simulator.GetPerformanceCounters();
        Assert.Equal(1UL, counters.ComponentEvaluations);
    }

    [Fact]
    public void Sram_address_delivery_is_processed_by_the_chip_while_deselected_output_remains_unchanged()
    {
        var board = new VirtualHardwareBoard("test.output-route.sram");
        var vcc = board.Add(new DigitalPowerRail("vcc", DigitalLevel.High));
        var gnd = board.Add(new DigitalPowerRail("gnd", DigitalLevel.Low));
        var source = board.Add(new DigitalSignalSource("source", DigitalLevel.Low));
        var ram = board.Add(new Hm6116("ram"));

        board.Connect(
            "VCC",
            vcc.Output,
            ram.Vcc,
            ram.ChipSelectBar,
            ram.OutputEnableBar,
            ram.WriteEnableBar);
        board.Connect("GND", gnd.Output, ram.Gnd);
        board.Connect("A0", source.Output, ram.Address.Pins[0]);

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();
        simulator.SetProfilingEnabled(true);

        source.Set(DigitalLevel.High);
        simulator.Settle();

        Assert.Equal(DigitalLevel.High, ram.Address.Pins[0].SampledLevel);
        var counters = simulator.GetPerformanceCounters();
        Assert.Equal(1UL, counters.ComponentEvaluations);
    }
}
