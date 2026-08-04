using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareNesPpuRegisterTests
{
    [Fact]
    public void Cpu_can_write_control_mask_oam_and_mirrored_ppu_registers()
    {
        var machine = CreateMachine(
            0xA9, 0x84, 0x8D, 0x00, 0x20, // PPUCTRL
            0xA9, 0x1E, 0x8D, 0x09, 0x20, // mirror of PPUMASK
            0xA9, 0x10, 0x8D, 0x03, 0x20, // OAMADDR
            0xA9, 0x5A, 0x8D, 0x04, 0x20, // OAMDATA
            0x00);

        Run(machine);

        Assert.Equal((byte)0x84, machine.PpuRegisters.Control);
        Assert.Equal((byte)0x1E, machine.PpuRegisters.Mask);
        Assert.Equal((byte)0x5A, machine.PpuRegisters.InspectOam(0x10));
        Assert.Equal((byte)0x11, machine.PpuRegisters.OamAddress);
    }

    [Fact]
    public void Ppuaddr_and_ppudata_write_vram_and_obey_increment_mode()
    {
        var machine = CreateMachine(
            0xA9, 0x04, 0x8D, 0x00, 0x20, // increment 32
            0xA9, 0x21, 0x8D, 0x06, 0x20,
            0xA9, 0x00, 0x8D, 0x06, 0x20,
            0xA9, 0xAA, 0x8D, 0x07, 0x20,
            0xA9, 0xBB, 0x8D, 0x07, 0x20,
            0x00);

        Run(machine);

        Assert.Equal((byte)0xAA, machine.PpuRegisters.InspectVram(0x2100));
        Assert.Equal((byte)0xBB, machine.PpuRegisters.InspectVram(0x2120));
        Assert.Equal((ushort)0x2140, machine.PpuRegisters.VramAddress);
    }

    [Fact]
    public void Ppudata_reads_are_buffered_but_palette_reads_are_immediate()
    {
        var machine = CreateMachine(
            0xA9, 0x20, 0x8D, 0x06, 0x20,
            0xA9, 0x00, 0x8D, 0x06, 0x20,
            0xA9, 0x66, 0x8D, 0x07, 0x20,
            0xA9, 0x20, 0x8D, 0x06, 0x20,
            0xA9, 0x00, 0x8D, 0x06, 0x20,
            0xAD, 0x07, 0x20, 0x8D, 0x00, 0x00,
            0xAD, 0x07, 0x20, 0x8D, 0x01, 0x00,
            0xA9, 0x3F, 0x8D, 0x06, 0x20,
            0xA9, 0x00, 0x8D, 0x06, 0x20,
            0xA9, 0x77, 0x8D, 0x07, 0x20,
            0xA9, 0x3F, 0x8D, 0x06, 0x20,
            0xA9, 0x00, 0x8D, 0x06, 0x20,
            0xAD, 0x07, 0x20, 0x8D, 0x02, 0x00,
            0x00);

        Run(machine);

        Assert.Equal((byte)0x66, machine.WorkRam.Inspect(1));
        Assert.Equal((byte)0x77, machine.WorkRam.Inspect(2));
    }

    [Fact]
    public void Status_read_reports_and_clears_vblank_and_resets_address_latch()
    {
        var machine = CreateMachine(
            0xA9, 0x21, 0x8D, 0x06, 0x20, // first address write leaves toggle set
            0xAD, 0x02, 0x20, 0x8D, 0x00, 0x00,
            0xAD, 0x02, 0x20, 0x8D, 0x01, 0x00,
            0x00);
        machine.PowerOn();
        machine.SetPpuVblank(true);
        machine.ReleaseReset();
        machine.RunUntilHalted();

        Assert.Equal(0x80, machine.WorkRam.Inspect(0) & 0x80);
        Assert.Equal(0x00, machine.WorkRam.Inspect(1) & 0x80);
        Assert.False(machine.PpuRegisters.WriteToggle);
        Assert.DoesNotContain(machine.Board.Nets, net => net.Level == DigitalLevel.Contention);
    }

    private static NesCpuMotherboard CreateMachine(params byte[] program)
    {
        var prg = new byte[NesCpuMotherboard.PrgRomSize];
        program.CopyTo(prg, 0);
        prg[0x7FFC] = 0x00;
        prg[0x7FFD] = 0x80;
        return new NesCpuMotherboard(prg);
    }

    private static void Run(NesCpuMotherboard machine)
    {
        machine.PowerOn();
        machine.ReleaseReset();
        machine.RunUntilHalted();
    }
}
