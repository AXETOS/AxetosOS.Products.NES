using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareNesControllerIoTests
{
    [Fact]
    public void Controller_one_is_latched_and_shifted_in_nes_button_order()
    {
        var machine = CreateMachineWithReadProgram(0x4016, 9);
        machine.SetControllerButtons(1, 0b1000_0101); // A, Select, Right

        machine.PowerOn();
        machine.SetControllerButtons(1, 0b1000_0101);
        machine.ReleaseReset();
        machine.RunUntilHalted();

        byte[] expected = [1, 0, 1, 0, 0, 0, 0, 1, 1];
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal((byte)(0x40 | expected[index]), machine.WorkRam.Inspect(index));
        }

        Assert.True(machine.ControllerIo.LatchCount >= 2);
        Assert.Equal((ulong)9, machine.ControllerIo.ReadCount);
        Assert.DoesNotContain(machine.Board.Nets, net => net.Level == DigitalLevel.Contention);
    }

    [Fact]
    public void Controller_two_uses_4017_and_an_independent_shift_register()
    {
        var machine = CreateMachineWithReadProgram(0x4017, 8);
        machine.PowerOn();
        machine.SetControllerButtons(1, 0xFF);
        machine.SetControllerButtons(2, 0b0010_1010); // B, Start, Down
        machine.ReleaseReset();
        machine.RunUntilHalted();

        byte[] expected = [0, 1, 0, 1, 0, 1, 0, 0];
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal((byte)(0x40 | expected[index]), machine.WorkRam.Inspect(index));
        }
    }

    [Fact]
    public void High_strobe_reports_live_a_button_without_advancing_the_register()
    {
        var program = new List<byte>
        {
            0xA9, 0x01,             // LDA #$01
            0x8D, 0x16, 0x40,       // STA $4016, strobe high
            0xAD, 0x16, 0x40,       // LDA $4016
            0x8D, 0x00, 0x00,       // STA $0000
            0xAD, 0x16, 0x40,       // LDA $4016 again
            0x8D, 0x01, 0x00,       // STA $0001
            0x00
        };

        var machine = new NesCpuMotherboard(CreatePrgRom(program));
        machine.PowerOn();
        machine.SetControllerButtons(1, 0x01);
        machine.ReleaseReset();
        machine.RunUntilHalted();

        Assert.Equal((byte)0x41, machine.WorkRam.Inspect(0));
        Assert.Equal((byte)0x41, machine.WorkRam.Inspect(1));
        Assert.True(machine.ControllerIo.Strobe);
    }

    [Fact]
    public void Controller_access_is_visible_as_real_cpu_bus_cycles()
    {
        var machine = CreateMachineWithReadProgram(0x4016, 1);
        machine.PowerOn();
        machine.SetControllerButtons(1, 0x01);
        machine.ReleaseReset();
        machine.RunUntilHalted();

        Assert.Contains(machine.Analyzer.Cycles, cycle => !cycle.IsRead && cycle.Address == 0x4016 && cycle.Data == 0x01);
        Assert.Contains(machine.Analyzer.Cycles, cycle => !cycle.IsRead && cycle.Address == 0x4016 && cycle.Data == 0x00);
        Assert.Contains(machine.Analyzer.Cycles, cycle => cycle.IsRead && cycle.Address == 0x4016 && cycle.Data == 0x41);
    }

    private static NesCpuMotherboard CreateMachineWithReadProgram(ushort address, int readCount)
    {
        var program = new List<byte>
        {
            0xA9, 0x01,
            0x8D, 0x16, 0x40,
            0xA9, 0x00,
            0x8D, 0x16, 0x40
        };

        for (var index = 0; index < readCount; index++)
        {
            program.Add(0xAD);
            program.Add((byte)address);
            program.Add((byte)(address >> 8));
            program.Add(0x8D);
            program.Add((byte)index);
            program.Add(0x00);
        }

        program.Add(0x00);
        return new NesCpuMotherboard(CreatePrgRom(program));
    }

    private static byte[] CreatePrgRom(IEnumerable<byte> program)
    {
        var prg = new byte[NesCpuMotherboard.PrgRomSize];
        program.ToArray().CopyTo(prg, 0);
        prg[0x7FFC] = 0x00;
        prg[0x7FFD] = 0x80;
        return prg;
    }
}
