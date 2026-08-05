using Xunit;
using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Components.Power;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareRp2A07StandaloneTests
{
    [Fact]
    public void Rp2A07_divides_the_external_master_clock_by_sixteen_into_cpu_cycles()
    {
        var board = new VirtualHardwareBoard("chiptest.rp2a07.clock");
        var chip = board.Add(new Rp2A07("U1"));
        Power(board, chip.Vcc, chip.Gnd);

        var clock = Source(board, "CLK", DigitalLevel.Low, chip.MasterClock);
        Source(board, "RES", DigitalLevel.High, chip.ResetBar);
        Source(board, "IRQ", DigitalLevel.High, chip.IrqBar);
        Source(board, "NMI", DigitalLevel.High, chip.NmiBar);
        Source(board, "IN0", DigitalLevel.High, chip.ControllerData1);
        Source(board, "IN1", DigitalLevel.High, chip.ControllerData2);
        var m2 = board.Connect("observe.M2", chip.M2);

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();

        PulseMasterClock(clock, simulator, 7);
        Assert.Equal(DigitalLevel.Low, m2.Level);
        Assert.Equal(0UL, chip.RisingEdgeCount);

        PulseMasterClock(clock, simulator, 1);
        Assert.Equal(DigitalLevel.High, m2.Level);
        Assert.Equal(1UL, chip.RisingEdgeCount);

        PulseMasterClock(clock, simulator, 8);
        Assert.Equal(DigitalLevel.Low, m2.Level);
        Assert.Equal(1UL, chip.RisingEdgeCount);
        Assert.Equal(16UL, chip.MasterClockRisingEdgeCount);
    }

    [Fact]
    public void Rp2A07_releases_every_driven_package_bus_when_unpowered()
    {
        var board = new VirtualHardwareBoard("chiptest.rp2a07.power");
        var chip = board.Add(new Rp2A07("U1"));
        Source(board, "VCC", DigitalLevel.Low, chip.Vcc);
        Source(board, "GND", DigitalLevel.Low, chip.Gnd);
        Source(board, "CLK", DigitalLevel.Low, chip.MasterClock);
        Source(board, "RES", DigitalLevel.High, chip.ResetBar);
        Source(board, "IRQ", DigitalLevel.High, chip.IrqBar);
        Source(board, "NMI", DigitalLevel.High, chip.NmiBar);
        Source(board, "IN0", DigitalLevel.High, chip.ControllerData1);
        Source(board, "IN1", DigitalLevel.High, chip.ControllerData2);

        var m2 = board.Connect("observe.M2", chip.M2);
        var rw = board.Connect("observe.RW", chip.ReadWrite);
        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();

        Assert.Equal(DigitalLevel.Unknown, m2.Level);
        Assert.Equal(DigitalLevel.Unknown, rw.Level);
        Assert.All(chip.Address.Pins, pin => Assert.Equal(DigitalLevel.Unknown, pin.SampledLevel));
    }

    private static void PulseMasterClock(DigitalSignalSource clock, VirtualHardwareSimulator simulator, int risingEdges)
    {
        for (var index = 0; index < risingEdges; index++)
        {
            clock.Set(DigitalLevel.High);
            simulator.Settle();
            clock.Set(DigitalLevel.Low);
            simulator.Settle();
        }
    }

    private static void Power(VirtualHardwareBoard board, DigitalPin vcc, DigitalPin gnd)
    {
        var high = board.Add(new DigitalPowerRail($"{vcc.Name}.rail", DigitalLevel.High));
        var low = board.Add(new DigitalPowerRail($"{gnd.Name}.rail", DigitalLevel.Low));
        board.Connect($"{vcc.Name}.net", high.Output, vcc);
        board.Connect($"{gnd.Name}.net", low.Output, gnd);
    }

    private static DigitalSignalSource Source(VirtualHardwareBoard board, string id, DigitalLevel level, DigitalPin target)
    {
        var source = board.Add(new DigitalSignalSource($"src.{id}", level));
        board.Connect($"net.{id}", source.Output, target);
        return source;
    }
}
