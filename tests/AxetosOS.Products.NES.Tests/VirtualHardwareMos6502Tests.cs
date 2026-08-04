using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components.Clock;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Components.Memory;
using AxetosOS.Products.NES.VirtualHardware.Components.Processors.Mos6502;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareMos6502Tests
{
    [Fact]
    public void Processor_fetches_reset_vector_and_program_only_through_pins()
    {
        var romImage = new byte[ushort.MaxValue + 1];
        romImage[0x8000] = 0xA9; // LDA #$2A
        romImage[0x8001] = 0x2A;
        romImage[0x8002] = 0xEA; // NOP
        romImage[0x8003] = 0x00; // temporary stop marker
        romImage[0xFFFC] = 0x00;
        romImage[0xFFFD] = 0x80;

        var board = new VirtualHardwareBoard("test.mos6502");
        var cpu = board.Add(new Mos6502Processor("cpu"));
        var rom = board.Add(new ProgramRomChip("rom", 16, romImage));
        var clock = board.Add(new DigitalOscillator("clock", 1_000_000));
        var reset = board.Add(new DigitalSignalSource("reset", DigitalLevel.Low));
        var irqHigh = board.Add(new DigitalSignalSource("irq-high", DigitalLevel.High));
        var nmiHigh = board.Add(new DigitalSignalSource("nmi-high", DigitalLevel.High));
        var readyHigh = board.Add(new DigitalSignalSource("ready-high", DigitalLevel.High));
        var chipSelectLow = board.Add(new DigitalSignalSource("chip-select-low", DigitalLevel.Low));
        var outputEnableLow = board.Add(new DigitalSignalSource("output-enable-low", DigitalLevel.Low));

        for (var bit = 0; bit < 16; bit++)
        {
            board.Connect($"A{bit}", cpu.Address.Pins[bit], rom.Address.Pins[bit]);
        }

        for (var bit = 0; bit < 8; bit++)
        {
            board.Connect($"D{bit}", cpu.Data.Pins[bit], rom.Data.Pins[bit]);
        }

        board.Connect("PHI2", clock.Output, cpu.Clock);
        board.Connect("/RESET", reset.Output, cpu.ResetBar);
        board.Connect("/IRQ", irqHigh.Output, cpu.IrqBar);
        board.Connect("/NMI", nmiHigh.Output, cpu.NmiBar);
        board.Connect("RDY", readyHigh.Output, cpu.Ready);
        board.Connect("/CS", chipSelectLow.Output, rom.ChipSelectBar);
        board.Connect("/OE", outputEnableLow.Output, rom.OutputEnableBar);

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();
        reset.Set(DigitalLevel.High);
        simulator.Settle();

        for (var cycle = 0; cycle < 16 && !cpu.IsHalted; cycle++)
        {
            clock.AdvanceHalfCycle();
            simulator.Settle();
            clock.AdvanceHalfCycle();
            simulator.Settle();
        }

        Assert.True(cpu.IsHalted);
        Assert.Equal((ushort)0x8004, cpu.ProgramCounter);
        Assert.Equal((byte)0x2A, cpu.Accumulator);
        Assert.Equal((ulong)3, cpu.CompletedInstructionCount);
        Assert.Equal(DigitalLevel.High, cpu.ReadWrite.DriveLevel);
        Assert.NotEqual(DigitalLevel.Contention, cpu.Data.Pins[0].SampledLevel);
    }

    [Fact]
    public void Ready_pin_stalls_the_current_bus_cycle()
    {
        var board = new VirtualHardwareBoard("test.mos6502.rdy");
        var cpu = board.Add(new Mos6502Processor("cpu"));
        var clock = board.Add(new DigitalOscillator("clock", 1_000_000));
        var reset = board.Add(new DigitalSignalSource("reset", DigitalLevel.High));
        var irqHigh = board.Add(new DigitalSignalSource("irq-high", DigitalLevel.High));
        var nmiHigh = board.Add(new DigitalSignalSource("nmi-high", DigitalLevel.High));
        var ready = board.Add(new DigitalSignalSource("ready", DigitalLevel.Low));

        board.Connect("PHI2", clock.Output, cpu.Clock);
        board.Connect("/RESET", reset.Output, cpu.ResetBar);
        board.Connect("/IRQ", irqHigh.Output, cpu.IrqBar);
        board.Connect("/NMI", nmiHigh.Output, cpu.NmiBar);
        board.Connect("RDY", ready.Output, cpu.Ready);

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();
        clock.AdvanceHalfCycle();
        simulator.Settle();

        Assert.Equal((ulong)1, cpu.RisingEdgeCount);
        Assert.Equal((ulong)1, cpu.ReadyStallCount);
        Assert.Equal((ushort)0x0000, cpu.ProgramCounter);
    }
}
