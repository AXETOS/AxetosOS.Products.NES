using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Components.Nes;
using AxetosOS.Products.NES.VirtualHardware.Components.Power;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareNesStandardControllerTests
{
    [Fact]
    public void Standard_controller_latches_and_shifts_buttons_only_through_package_pins()
    {
        var board = new VirtualHardwareBoard("controller.standard");
        var controller = board.Add(new NesStandardController("J1"));
        var vcc = board.Add(new DigitalPowerRail("vcc", DigitalLevel.High));
        var ground = board.Add(new DigitalPowerRail("ground", DigitalLevel.Low));
        var strobe = board.Add(new DigitalSignalSource("strobe", DigitalLevel.Low));
        var clockBar = board.Add(new DigitalSignalSource("clock", DigitalLevel.High));
        var buttons = Enumerable.Range(0, 8)
            .Select(index => board.Add(new DigitalSignalSource($"button{index}", DigitalLevel.Low)))
            .ToArray();

        board.Connect("VCC", vcc.Output, controller.Vcc);
        board.Connect("GND", ground.Output, controller.Gnd);
        board.Connect("STROBE", strobe.Output, controller.Strobe);
        board.Connect("CLOCK_BAR", clockBar.Output, controller.ClockBar);
        var data = board.Connect("DATA", controller.Data);
        for (var index = 0; index < buttons.Length; index++)
        {
            board.Connect($"BUTTON{index}", buttons[index].Output, controller.Buttons.Pins[index]);
        }

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();

        // A and Start pressed: 00001001 in controller serial order.
        buttons[0].Set(DigitalLevel.High);
        buttons[3].Set(DigitalLevel.High);
        strobe.Set(DigitalLevel.High);
        simulator.Settle();
        strobe.Set(DigitalLevel.Low);
        simulator.Settle();

        Assert.Equal(DigitalLevel.High, data.Level);

        PulseReadClock(clockBar, simulator);
        Assert.Equal(DigitalLevel.Low, data.Level);
        PulseReadClock(clockBar, simulator);
        Assert.Equal(DigitalLevel.Low, data.Level);
        PulseReadClock(clockBar, simulator);
        Assert.Equal(DigitalLevel.High, data.Level);
    }

    [Fact]
    public void Regional_motherboards_install_two_determinate_controller_packages()
    {
        var famicom = new FamicomMotherboard();
        var ntsc = new NtscNesMotherboard();
        var pal = new PalNesMotherboard(PalCicVariant.PalA3195);

        AssertControllerWiring(famicom.Cpu.ControllerData1, famicom.Cpu.ControllerRead1Bar, famicom.Cpu.ControllerOut0, famicom.Controller1);
        AssertControllerWiring(ntsc.Cpu.ControllerData1, ntsc.Cpu.ControllerRead1Bar, ntsc.Cpu.ControllerOut0, ntsc.Controller1);
        AssertControllerWiring(pal.Cpu.ControllerData1, pal.Cpu.ControllerRead1Bar, pal.Cpu.ControllerOut0, pal.Controller1);
    }

    private static void PulseReadClock(DigitalSignalSource clockBar, VirtualHardwareSimulator simulator)
    {
        clockBar.Set(DigitalLevel.Low);
        simulator.Settle();
        clockBar.Set(DigitalLevel.High);
        simulator.Settle();
    }

    private static void AssertControllerWiring(
        DigitalPin cpuData,
        DigitalPin cpuClockBar,
        DigitalPin cpuStrobe,
        NesStandardController controller)
    {
        Assert.Same(cpuData.Net, controller.Data.Net);
        Assert.Same(cpuClockBar.Net, controller.ClockBar.Net);
        Assert.Same(cpuStrobe.Net, controller.Strobe.Net);
    }
}
