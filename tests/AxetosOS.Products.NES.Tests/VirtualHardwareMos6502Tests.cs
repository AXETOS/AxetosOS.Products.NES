using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components.Clock;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Components.Memory;
using AxetosOS.Products.NES.VirtualHardware.Components.Logic;
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

    [Fact]
    public void Reset_asserted_during_execution_restarts_the_pin_driven_reset_sequence()
    {
        var romImage = new byte[ushort.MaxValue + 1];
        romImage[0x8000] = 0xA9;
        romImage[0x8001] = 0x44;
        romImage[0x8002] = 0xEA;
        romImage[0xFFFC] = 0x00;
        romImage[0xFFFD] = 0x80;

        var fixture = CreateRomFixture("test.mos6502.reset-during-execution", romImage, DigitalLevel.High, DigitalLevel.High);
        fixture.Board.PowerOn();
        fixture.Simulator.Settle();
        fixture.Reset.Set(DigitalLevel.High);
        fixture.Simulator.Settle();

        for (var cycle = 0; cycle < 9; cycle++)
        {
            Tick(fixture.Clock, fixture.Simulator);
        }

        Assert.Equal((byte)0x44, fixture.Cpu.Accumulator);
        fixture.Reset.Set(DigitalLevel.Low);
        fixture.Simulator.Settle();
        fixture.Reset.Set(DigitalLevel.High);
        fixture.Simulator.Settle();

        for (var cycle = 0; cycle < 7; cycle++)
        {
            Tick(fixture.Clock, fixture.Simulator);
        }

        Assert.Equal((ushort)0x8000, fixture.Cpu.ProgramCounter);
        Assert.Equal((byte)0xFD, fixture.Cpu.StackPointer);
        Assert.True(fixture.Cpu.InterruptDisable);
        Assert.Equal(DigitalLevel.High, fixture.Cpu.Sync.DriveLevel);
    }

    [Fact]
    public void Irq_is_masked_until_cli_then_pushes_pc_and_status_through_the_stack_bus()
    {
        var romImage = new byte[ushort.MaxValue + 1];
        romImage[0x8000] = 0x58; // CLI
        romImage[0x8001] = 0xEA; // must not execute before the pending IRQ
        romImage[0x9000] = 0x00; // temporary stop marker at IRQ handler
        romImage[0xFFFC] = 0x00;
        romImage[0xFFFD] = 0x80;
        romImage[0xFFFE] = 0x00;
        romImage[0xFFFF] = 0x90;

        var fixture = CreateRomFixture("test.mos6502.irq", romImage, DigitalLevel.Low, DigitalLevel.High);
        fixture.Board.PowerOn();
        fixture.Simulator.Settle();
        fixture.Reset.Set(DigitalLevel.High);
        fixture.Simulator.Settle();

        var writes = new List<(ushort Address, byte Data)>();
        for (var cycle = 0; cycle < 32 && !fixture.Cpu.IsHalted; cycle++)
        {
            Tick(fixture.Clock, fixture.Simulator);
            if (fixture.Cpu.ReadWrite.DriveLevel == DigitalLevel.Low &&
                fixture.Cpu.Address.TrySample(out var address) &&
                fixture.Cpu.Data.TrySample(out var data))
            {
                writes.Add(((ushort)address, (byte)data));
            }
        }

        Assert.True(fixture.Cpu.IsHalted);
        Assert.Equal((ulong)1, fixture.Cpu.CompletedInterruptCount);
        Assert.Equal((ushort)0x9001, fixture.Cpu.ProgramCounter);
        Assert.Equal((byte)0xFA, fixture.Cpu.StackPointer);
        Assert.Equal(3, writes.Count);
        Assert.Equal(((ushort)0x01FD, (byte)0x80), writes[0]);
        Assert.Equal(((ushort)0x01FC, (byte)0x01), writes[1]);
        Assert.Equal((ushort)0x01FB, writes[2].Address);
        Assert.Equal(0, writes[2].Data & 0x10); // hardware IRQ pushes B clear
        Assert.NotEqual(0, writes[2].Data & 0x04); // interrupt-disable set
    }

    [Fact]
    public void Nmi_falling_edge_is_latched_and_uses_the_nmi_vector()
    {
        var romImage = new byte[ushort.MaxValue + 1];
        romImage[0x8000] = 0xEA;
        romImage[0x8001] = 0xEA;
        romImage[0xA000] = 0x00;
        romImage[0xFFFA] = 0x00;
        romImage[0xFFFB] = 0xA0;
        romImage[0xFFFC] = 0x00;
        romImage[0xFFFD] = 0x80;

        var fixture = CreateRomFixture("test.mos6502.nmi", romImage, DigitalLevel.High, DigitalLevel.High);
        fixture.Board.PowerOn();
        fixture.Simulator.Settle();
        fixture.Reset.Set(DigitalLevel.High);
        fixture.Simulator.Settle();

        for (var cycle = 0; cycle < 8; cycle++)
        {
            Tick(fixture.Clock, fixture.Simulator);
        }

        fixture.Nmi.Set(DigitalLevel.Low);
        fixture.Simulator.Settle();
        fixture.Nmi.Set(DigitalLevel.High);
        fixture.Simulator.Settle();

        for (var cycle = 0; cycle < 16 && !fixture.Cpu.IsHalted; cycle++)
        {
            Tick(fixture.Clock, fixture.Simulator);
        }

        Assert.True(fixture.Cpu.IsHalted);
        Assert.Equal((ulong)1, fixture.Cpu.CompletedInterruptCount);
        Assert.Equal((ushort)0xA001, fixture.Cpu.ProgramCounter);
        Assert.False(fixture.Cpu.NmiPending);
    }

    [Fact]
    public void Ready_stalls_reads_but_not_interrupt_stack_writes()
    {
        var romImage = new byte[ushort.MaxValue + 1];
        romImage[0x8000] = 0x58; // CLI
        romImage[0x9000] = 0x00;
        romImage[0xFFFC] = 0x00;
        romImage[0xFFFD] = 0x80;
        romImage[0xFFFE] = 0x00;
        romImage[0xFFFF] = 0x90;

        var fixture = CreateRomFixture("test.mos6502.rdy-writes", romImage, DigitalLevel.Low, DigitalLevel.High);
        fixture.Board.PowerOn();
        fixture.Simulator.Settle();
        fixture.Reset.Set(DigitalLevel.High);
        fixture.Simulator.Settle();

        for (var cycle = 0; cycle < 10; cycle++)
        {
            Tick(fixture.Clock, fixture.Simulator);
        }

        Assert.Equal(DigitalLevel.Low, fixture.Cpu.ReadWrite.DriveLevel);
        fixture.Ready.Set(DigitalLevel.Low);
        fixture.Simulator.Settle();
        var stallsBeforeWrite = fixture.Cpu.ReadyStallCount;
        var stackBeforeWrite = fixture.Cpu.StackPointer;

        Tick(fixture.Clock, fixture.Simulator);

        Assert.Equal(stallsBeforeWrite, fixture.Cpu.ReadyStallCount);
        Assert.Equal((byte)(stackBeforeWrite - 1), fixture.Cpu.StackPointer);
    }

    private static RomFixture CreateRomFixture(
        string id,
        byte[] romImage,
        DigitalLevel irqLevel,
        DigitalLevel nmiLevel)
    {
        var board = new VirtualHardwareBoard(id);
        var cpu = board.Add(new Mos6502Processor("cpu"));
        var rom = board.Add(new ProgramRomChip("rom", 16, romImage));
        var readInverter = board.Add(new NotGate("read-inverter"));
        var clock = board.Add(new DigitalOscillator("clock", 1_000_000));
        var reset = board.Add(new DigitalSignalSource("reset", DigitalLevel.Low));
        var irq = board.Add(new DigitalSignalSource("irq", irqLevel));
        var nmi = board.Add(new DigitalSignalSource("nmi", nmiLevel));
        var ready = board.Add(new DigitalSignalSource("ready", DigitalLevel.High));
        var chipSelectLow = board.Add(new DigitalSignalSource("chip-select-low", DigitalLevel.Low));

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
        board.Connect("/IRQ", irq.Output, cpu.IrqBar);
        board.Connect("/NMI", nmi.Output, cpu.NmiBar);
        board.Connect("RDY", ready.Output, cpu.Ready);
        board.Connect("R/W", cpu.ReadWrite, readInverter.Input);
        board.Connect("/OE", readInverter.Output, rom.OutputEnableBar);
        board.Connect("/CS", chipSelectLow.Output, rom.ChipSelectBar);

        return new RomFixture(board, new VirtualHardwareSimulator(board), cpu, clock, reset, irq, nmi, ready);
    }

    private static void Tick(DigitalOscillator clock, VirtualHardwareSimulator simulator)
    {
        clock.AdvanceHalfCycle();
        simulator.Settle();
        clock.AdvanceHalfCycle();
        simulator.Settle();
    }

    private sealed record RomFixture(
        VirtualHardwareBoard Board,
        VirtualHardwareSimulator Simulator,
        Mos6502Processor Cpu,
        DigitalOscillator Clock,
        DigitalSignalSource Reset,
        DigitalSignalSource Irq,
        DigitalSignalSource Nmi,
        DigitalSignalSource Ready);

}
