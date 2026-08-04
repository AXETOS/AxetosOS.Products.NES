using AxetosOS.Products.NES.VirtualHardware.Boards.Examples;
using AxetosOS.Products.NES.VirtualHardware.Components.Processors.Tiny8;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareMicrocomputerTests
{
    [Fact]
    public void Rom_releases_the_shared_data_bus_outside_its_selected_address_half()
    {
        byte[] program = [Tiny8Processor.HaltOpcode];
        var machine = new PinDrivenMicrocomputer(program);

        machine.PowerOn();
        machine.ReleaseReset();

        Assert.Equal(DigitalLevel.Low, machine.Rom.ChipSelectBar.SampledLevel);

        machine.Cpu.Address.Drive(0x00);
        machine.Simulator.Settle();

        Assert.Equal(DigitalLevel.High, machine.Rom.ChipSelectBar.SampledLevel);
        Assert.All(machine.Rom.Data.Pins, pin => Assert.Equal(DigitalLevel.HighImpedance, pin.DriveLevel));
    }

    [Fact]
    public void Complete_microcomputer_executes_program_through_only_pins_and_nets()
    {
        byte[] program =
        [
            Tiny8Processor.LoadImmediateOpcode, 0x2A,
            Tiny8Processor.StoreAbsoluteOpcode, 0x05,
            Tiny8Processor.LoadImmediateOpcode, 0x00,
            Tiny8Processor.LoadAbsoluteOpcode, 0x05,
            Tiny8Processor.HaltOpcode
        ];

        var machine = new PinDrivenMicrocomputer(program);
        machine.PowerOn();
        machine.ReleaseReset();
        machine.RunUntilHalted();

        Assert.True(machine.Cpu.IsHalted);
        Assert.Equal(0x2A, machine.Cpu.Accumulator);
        Assert.Equal(0x2A, machine.Ram.Inspect(0x05));
        Assert.Equal(5UL, machine.Cpu.InstructionCount);
        Assert.True(machine.Ram.WriteCount >= 1);
        Assert.True(machine.Rom.ReadDriveCount >= 1);
        Assert.DoesNotContain(machine.Board.Nets, net => net.Level == DigitalLevel.Contention);
    }
}
