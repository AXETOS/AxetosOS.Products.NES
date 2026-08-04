using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareNesOamDmaTests
{
    [Fact]
    public void Cpu_write_to_4014_transfers_an_entire_work_ram_page_into_oam()
    {
        var machine = CreateDmaMachine(oamAddress: 0x00);
        Run(machine);

        Assert.Equal((byte)0x11, machine.PpuRegisters.InspectOam(0x00));
        Assert.Equal((byte)0x22, machine.PpuRegisters.InspectOam(0x01));
        Assert.Equal((byte)0x33, machine.PpuRegisters.InspectOam(0xFF));
        Assert.Equal((ulong)256, machine.OamDma.TransferCount);
        Assert.Equal((ulong)256, machine.PpuRegisters.DmaWriteCount);
        Assert.Equal((ulong)1, machine.OamDma.CompletedDmaCount);
    }

    [Fact]
    public void Oam_dma_starts_at_oamaddr_and_wraps_after_256_writes()
    {
        var machine = CreateDmaMachine(oamAddress: 0xFE);
        Run(machine);

        Assert.Equal((byte)0x11, machine.PpuRegisters.InspectOam(0xFE));
        Assert.Equal((byte)0x22, machine.PpuRegisters.InspectOam(0xFF));
        Assert.Equal((byte)0x33, machine.PpuRegisters.InspectOam(0xFD));
        Assert.Equal((byte)0xFE, machine.PpuRegisters.OamAddress);
    }

    [Fact]
    public void Cpu_is_stalled_during_dma_and_resumes_after_bus_release()
    {
        var machine = CreateDmaMachine(oamAddress: 0x00);
        Run(machine);

        Assert.True(machine.OamDma.CpuStallCycleCount >= 513);
        Assert.Equal((byte)0x77, machine.WorkRam.Inspect(0x10));
        Assert.False(machine.OamDma.IsActive);
        Assert.Equal(DigitalLevel.High, machine.Cpu.BusEnable.SampledLevel);
        Assert.Equal(DigitalLevel.High, machine.Cpu.Ready.SampledLevel);
    }

    [Fact]
    public void Dma_memory_reads_are_visible_on_the_system_bus_without_contention()
    {
        var machine = CreateDmaMachine(oamAddress: 0x00);
        Run(machine);

        Assert.Contains(machine.Analyzer.Cycles, cycle => !cycle.IsRead && cycle.Address == 0x4014 && cycle.Data == 0x02);
        Assert.Contains(machine.Analyzer.Cycles, cycle => cycle.IsRead && cycle.Address == 0x0200 && cycle.Data == 0x11);
        Assert.Contains(machine.Analyzer.Cycles, cycle => cycle.IsRead && cycle.Address == 0x0201 && cycle.Data == 0x22);
        Assert.Contains(machine.Analyzer.Cycles, cycle => cycle.IsRead && cycle.Address == 0x02FF && cycle.Data == 0x33);
        Assert.DoesNotContain(machine.Board.Nets, net => net.Level == DigitalLevel.Contention);
    }

    private static NesCpuMotherboard CreateDmaMachine(byte oamAddress)
    {
        var program = new byte[]
        {
            0xA9, 0x11, 0x8D, 0x00, 0x02, // LDA #$11 / STA $0200
            0xA9, 0x22, 0x8D, 0x01, 0x02, // LDA #$22 / STA $0201
            0xA9, 0x33, 0x8D, 0xFF, 0x02, // LDA #$33 / STA $02FF
            0xA9, oamAddress, 0x8D, 0x03, 0x20, // OAMADDR
            0xA9, 0x02, 0x8D, 0x14, 0x40, // OAMDMA from page $02
            0xA9, 0x77, 0x8D, 0x10, 0x00, // proves CPU resumed
            0x00
        };

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
