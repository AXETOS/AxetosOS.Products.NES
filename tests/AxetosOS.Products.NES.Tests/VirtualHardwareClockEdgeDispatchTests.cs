using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareClockEdgeDispatchTests
{
    [Fact]
    public void Famicom_clock_falling_edge_settles_without_running_full_CPU_or_PPU_packages()
    {
        var board = new FamicomMotherboard();
        board.PowerOn();
        board.Simulator.SetProfilingEnabled(true);

        board.AdvanceMasterHalfCycle(); // rising
        var afterRising = board.Simulator.GetProfileSnapshot();
        board.AdvanceMasterHalfCycle(); // falling
        var afterFalling = board.Simulator.GetProfileSnapshot();

        var cpuRise = afterRising.Components.Single(x => x.ComponentId == "U1.RP2A03").EvaluationCount;
        var ppuRise = afterRising.Components.Single(x => x.ComponentId == "U2.RP2C02").EvaluationCount;
        var cpuFall = afterFalling.Components.Single(x => x.ComponentId == "U1.RP2A03").EvaluationCount;
        var ppuFall = afterFalling.Components.Single(x => x.ComponentId == "U2.RP2C02").EvaluationCount;

        Assert.Equal(cpuRise, cpuFall);
        Assert.Equal(ppuRise, ppuFall);
    }
}
