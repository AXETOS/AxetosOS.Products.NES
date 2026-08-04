using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using AxetosOS.Products.NES.VirtualHardware.Components.Nes;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareNesPpuTimingTests
{
    [Fact]
    public void Ppu_timing_enters_vblank_at_scanline_241_dot_1()
    {
        var machine = CreateMachine(0x00);
        machine.PowerOn();

        machine.AdvancePpuDots(machine.PpuTiming.VblankStartScanline * machine.PpuTiming.DotsPerScanline + 1);

        Assert.Equal(machine.PpuTiming.VblankStartScanline, machine.PpuTiming.Scanline);
        Assert.Equal(1, machine.PpuTiming.Dot);
        Assert.True(machine.PpuTiming.IsVblank);
        Assert.Equal(DigitalLevel.High, machine.Board.Nets.Single(net => net.Name == "PPU_VBLANK").Level);
    }

    [Fact]
    public void Ppu_timing_clears_vblank_on_pre_render_scanline()
    {
        var machine = CreateMachine(0x00);
        machine.PowerOn();
        machine.AdvancePpuDots(machine.PpuTiming.VblankStartScanline * machine.PpuTiming.DotsPerScanline + 1);

        var remainingDots = (machine.PpuTiming.PreRenderScanline - machine.PpuTiming.VblankStartScanline)
            * machine.PpuTiming.DotsPerScanline;
        machine.AdvancePpuDots(remainingDots);

        Assert.Equal(machine.PpuTiming.PreRenderScanline, machine.PpuTiming.Scanline);
        Assert.Equal(1, machine.PpuTiming.Dot);
        Assert.False(machine.PpuTiming.IsVblank);
        Assert.Equal(DigitalLevel.Low, machine.Board.Nets.Single(net => net.Name == "PPU_VBLANK").Level);
    }

    [Fact]
    public void Ppuctrl_nmi_enable_drives_open_drain_cpu_nmi_during_vblank()
    {
        var machine = CreateMachine(
            0xA9, 0x80, 0x8D, 0x00, 0x20,
            0x00);
        machine.PowerOn();
        machine.ReleaseReset();
        machine.RunUntilHalted();

        Assert.Equal(DigitalLevel.High, machine.Board.Nets.Single(net => net.Name == "/NMI").Level);

        machine.SetPpuVblank(true);
        Assert.Equal(DigitalLevel.Low, machine.Board.Nets.Single(net => net.Name == "/NMI").Level);

        machine.SetPpuVblank(false);
        Assert.Equal(DigitalLevel.High, machine.Board.Nets.Single(net => net.Name == "/NMI").Level);
        Assert.DoesNotContain(machine.Board.Nets, net => net.Level == DigitalLevel.Contention);
    }

    [Fact]
    public void Disabled_ppu_nmi_leaves_cpu_nmi_pulled_high_during_vblank()
    {
        var machine = CreateMachine(0x00);
        machine.PowerOn();
        machine.SetPpuVblank(true);

        Assert.True(machine.PpuTiming.IsVblank);
        Assert.Equal(DigitalLevel.High, machine.Board.Nets.Single(net => net.Name == "/NMI").Level);
    }

    private static NesCpuMotherboard CreateMachine(params byte[] program)
    {
        var prg = new byte[NesCpuMotherboard.PrgRomSize];
        program.CopyTo(prg, 0);
        prg[0x7FFC] = 0x00;
        prg[0x7FFD] = 0x80;
        return new NesCpuMotherboard(prg);
    }
}
