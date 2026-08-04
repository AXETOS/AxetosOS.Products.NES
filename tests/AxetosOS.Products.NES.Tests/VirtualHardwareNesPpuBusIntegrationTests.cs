using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareNesPpuBusIntegrationTests
{
    [Fact]
    public void Cpu_ppudata_write_reaches_external_ciram_through_ppu_bus_pins()
    {
        var machine = CreateMachine(
            0xA9, 0x20, 0x8D, 0x06, 0x20,
            0xA9, 0x00, 0x8D, 0x06, 0x20,
            0xA9, 0x5A, 0x8D, 0x07, 0x20,
            0x00);

        Run(machine);

        Assert.Equal((byte)0x5A, machine.PpuMemory.Inspect(0x2000));
        Assert.True(machine.PpuRegisters.ExternalPpuWriteCount > 0);
        Assert.DoesNotContain(machine.Board.Nets, net => net.Level == DigitalLevel.Contention);
    }

    [Fact]
    public void Cpu_ppudata_access_drives_external_read_strobe_and_address_bus()
    {
        var machine = CreateMachine(
            0xA9, 0x20, 0x8D, 0x06, 0x20,
            0xA9, 0x00, 0x8D, 0x06, 0x20,
            0xAD, 0x07, 0x20,
            0x00);
        machine.PpuMemory.LoadForDiagnostics(0x2000, [0x77]);

        Run(machine);

        Assert.True(machine.PpuRegisters.ExternalPpuReadCount > 0);
        Assert.True(machine.PpuMemory.ReadDriveCount > 0);
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
