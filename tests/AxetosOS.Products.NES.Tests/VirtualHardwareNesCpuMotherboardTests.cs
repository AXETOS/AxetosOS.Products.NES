using Xunit;
using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareNesCpuMotherboardTests
{
    [Fact]
    public void Motherboard_boots_from_prg_rom_and_executes_through_pin_wiring()
    {
        var prg = CreatePrgRom(
            0xA9, 0x42,       // LDA #$42
            0x8D, 0x05, 0x00, // STA $0005
            0xAD, 0x05, 0x00, // LDA $0005
            0x00);            // diagnostic halt marker

        var machine = new NesCpuMotherboard(prg);
        machine.PowerOn();
        machine.ReleaseReset();
        machine.RunUntilHalted();

        Assert.Equal((byte)0x42, machine.Cpu.Accumulator);
        Assert.Equal((byte)0x42, machine.WorkRam.Inspect(0x0005));
        Assert.Equal((ulong)1, machine.WorkRam.WriteCount);
        Assert.True(machine.PrgRom.ReadDriveCount > 0);
        Assert.DoesNotContain(machine.Board.Nets, net => net.Level == DigitalLevel.Contention);
    }

    [Fact]
    public void Internal_two_kibibyte_ram_is_mirrored_through_1fff_by_board_wiring()
    {
        var prg = CreatePrgRom(
            0xA9, 0x11,       // LDA #$11
            0x8D, 0xA5, 0x00, // STA $00A5
            0xA9, 0x7C,       // LDA #$7C
            0x8D, 0xA5, 0x18, // STA $18A5, mirror of $00A5
            0xAD, 0xA5, 0x08, // LDA $08A5, same physical byte
            0x00);

        var machine = new NesCpuMotherboard(prg);
        machine.PowerOn();
        machine.ReleaseReset();
        machine.RunUntilHalted();

        Assert.Equal((byte)0x7C, machine.Cpu.Accumulator);
        Assert.Equal((byte)0x7C, machine.WorkRam.Inspect(0x00A5));
        Assert.Equal((ulong)2, machine.WorkRam.WriteCount);
    }

    [Fact]
    public void Sixteen_kibibyte_nrom_prg_is_physically_mirrored_into_both_cpu_banks()
    {
        var prg = new byte[16 * 1024];
        prg[0x0000] = 0x4A;
        prg[0x3FFC] = 0x00;
        prg[0x3FFD] = 0x80;

        var machine = new NesCpuMotherboard(prg);

        Assert.Equal((byte)0x4A, machine.PrgRom.Inspect(0x0000));
        Assert.Equal((byte)0x4A, machine.PrgRom.Inspect(0x4000));
        Assert.Equal((byte)0x00, machine.PrgRom.Inspect(0x7FFC));
        Assert.Equal((byte)0x80, machine.PrgRom.Inspect(0x7FFD));
    }

    [Fact]
    public void Passive_bus_analyzer_records_opcode_reads_and_ram_writes()
    {
        var prg = CreatePrgRom(
            0xA9, 0x5A,
            0x8D, 0x10, 0x00,
            0x00);

        var machine = new NesCpuMotherboard(prg);
        machine.PowerOn();
        machine.ReleaseReset();
        machine.RunUntilHalted();

        Assert.Equal(machine.Cpu.RisingEdgeCount, machine.Analyzer.ObservedRisingEdges);
        Assert.Contains(machine.Analyzer.Cycles, cycle => cycle.IsOpcodeFetch && cycle.Address == 0x8000);
        Assert.Contains(machine.Analyzer.Cycles, cycle => !cycle.IsRead && cycle.Address == 0x0010 && cycle.Data == 0x5A);
        Assert.Equal((ulong)0, machine.Analyzer.DroppedCycleCount);
    }

    private static byte[] CreatePrgRom(params byte[] program)
    {
        var prg = new byte[NesCpuMotherboard.PrgRomSize];
        program.CopyTo(prg, 0);
        prg[0x7FFC] = 0x00;
        prg[0x7FFD] = 0x80;
        return prg;
    }
}
