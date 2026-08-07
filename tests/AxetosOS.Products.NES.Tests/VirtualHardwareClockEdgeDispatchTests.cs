using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareClockEdgeDispatchTests
{
    [Fact]
    public void Famicom_clock_falling_level_reaches_pins_without_waking_rising_edge_packages()
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
        Assert.Equal(
            AxetosOS.Products.NES.VirtualHardware.Electrical.DigitalLevel.Low,
            board.Cpu.MasterClock.SampledLevel);
        Assert.Equal(
            AxetosOS.Products.NES.VirtualHardware.Electrical.DigitalLevel.Low,
            board.Ppu.Clock.SampledLevel);
    }

    [Fact]
    public void Famicom_compiled_clock_falling_edge_only_updates_pin_level()
    {
        var board = new FamicomMotherboard();
        board.PowerOn();

        board.AdvanceMasterHalfCycle(); // rising, compiled fast path
        var cpuEdgesAfterRise = board.Cpu.MasterClockRisingEdgeCount;
        var ppuEdgesAfterRise = board.Ppu.MasterClockRisingEdgeCount;

        board.AdvanceMasterHalfCycle(); // falling, must not activate either chip

        Assert.Equal(cpuEdgesAfterRise, board.Cpu.MasterClockRisingEdgeCount);
        Assert.Equal(ppuEdgesAfterRise, board.Ppu.MasterClockRisingEdgeCount);
        Assert.Equal(
            AxetosOS.Products.NES.VirtualHardware.Electrical.DigitalLevel.Low,
            board.Cpu.MasterClock.SampledLevel);
        Assert.Equal(
            AxetosOS.Products.NES.VirtualHardware.Electrical.DigitalLevel.Low,
            board.Ppu.Clock.SampledLevel);
    }

    [Fact]
    public void Bulk_master_cycles_match_scalar_half_cycle_clocking()
    {
        var bulk = new FamicomMotherboard();
        var scalar = new FamicomMotherboard();
        bulk.PowerOn();
        scalar.PowerOn();
        bulk.ReleaseReset();
        scalar.ReleaseReset();

        const int cycles = 1_200;
        bulk.AdvanceMasterCycles(cycles);
        for (var halfCycle = 0; halfCycle < cycles * 2; halfCycle++)
            scalar.AdvanceMasterHalfCycle();

        Assert.Equal(scalar.MasterClock.HalfCycleCount, bulk.MasterClock.HalfCycleCount);
        Assert.Equal(scalar.Cpu.MasterClockRisingEdgeCount, bulk.Cpu.MasterClockRisingEdgeCount);
        Assert.Equal(scalar.Ppu.MasterClockRisingEdgeCount, bulk.Ppu.MasterClockRisingEdgeCount);
        Assert.Equal(scalar.Cpu.RisingEdgeCount, bulk.Cpu.RisingEdgeCount);
        Assert.Equal(scalar.Cpu.CompletedInstructionCount, bulk.Cpu.CompletedInstructionCount);
        Assert.Equal(scalar.Cpu.CurrentM2Level, bulk.Cpu.CurrentM2Level);
        Assert.Equal(scalar.Ppu.Scanline, bulk.Ppu.Scanline);
        Assert.Equal(scalar.Ppu.Dot, bulk.Ppu.Dot);
        Assert.Equal(scalar.Ppu.Frame, bulk.Ppu.Frame);
        Assert.Equal(
            AxetosOS.Products.NES.VirtualHardware.Electrical.DigitalLevel.Low,
            bulk.Cpu.MasterClock.SampledLevel);
        Assert.Equal(
            AxetosOS.Products.NES.VirtualHardware.Electrical.DigitalLevel.Low,
            bulk.Ppu.Clock.SampledLevel);
    }
}
