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


    [Fact]
    public void Register_transfers_and_immediate_alu_execute_through_fetch_cycles()
    {
        var romImage = new byte[ushort.MaxValue + 1];
        var program = new byte[]
        {
            0xA9, 0x40, // LDA #$40
            0x69, 0x40, // ADC #$40 => $80, overflow set
            0xAA,       // TAX
            0xE8,       // INX => $81
            0x8A,       // TXA
            0x49, 0xFF, // EOR #$FF => $7E
            0x00
        };
        Array.Copy(program, 0, romImage, 0x8000, program.Length);
        romImage[0xFFFC] = 0x00;
        romImage[0xFFFD] = 0x80;

        var fixture = CreateRomFixture("test.mos6502.alu", romImage, DigitalLevel.High, DigitalLevel.High);
        RunUntilHalted(fixture);

        Assert.Equal((byte)0x7E, fixture.Cpu.Accumulator);
        Assert.Equal((byte)0x81, fixture.Cpu.X);
        Assert.Equal((ulong)7, fixture.Cpu.CompletedInstructionCount);
        Assert.NotEqual(0, fixture.Cpu.Status & 0x40);
    }

    [Fact]
    public void Conditional_branch_uses_signed_relative_offset()
    {
        var romImage = new byte[ushort.MaxValue + 1];
        var program = new byte[]
        {
            0xA9, 0x00, // Z set
            0xF0, 0x02, // BEQ +2
            0xA9, 0x11, // skipped
            0xA9, 0x22,
            0x00
        };
        Array.Copy(program, 0, romImage, 0x8000, program.Length);
        romImage[0xFFFC] = 0x00;
        romImage[0xFFFD] = 0x80;

        var fixture = CreateRomFixture("test.mos6502.branch", romImage, DigitalLevel.High, DigitalLevel.High);
        RunUntilHalted(fixture);

        Assert.Equal((byte)0x22, fixture.Cpu.Accumulator);
        Assert.Equal((ushort)0x8009, fixture.Cpu.ProgramCounter);
    }

    [Fact]
    public void Jsr_uses_external_stack_bus_cycles_and_enters_subroutine()
    {
        var romImage = new byte[ushort.MaxValue + 1];
        romImage[0x8000] = 0x20; // JSR $9000
        romImage[0x8001] = 0x00;
        romImage[0x8002] = 0x90;
        romImage[0x8003] = 0x00;
        romImage[0x9000] = 0xA9;
        romImage[0x9001] = 0x33;
        romImage[0x9002] = 0x00;
        romImage[0xFFFC] = 0x00;
        romImage[0xFFFD] = 0x80;

        var fixture = CreateRomFixture("test.mos6502.jsr", romImage, DigitalLevel.High, DigitalLevel.High);
        var writes = new List<(ushort Address, byte Data)>();
        fixture.Board.PowerOn();
        fixture.Simulator.Settle();
        fixture.Reset.Set(DigitalLevel.High);
        fixture.Simulator.Settle();
        for (var cycle = 0; cycle < 64 && !fixture.Cpu.IsHalted; cycle++)
        {
            Tick(fixture.Clock, fixture.Simulator);
            if (fixture.Cpu.ReadWrite.DriveLevel == DigitalLevel.Low &&
                fixture.Cpu.Address.TrySample(out var address) && fixture.Cpu.Data.TrySample(out var data))
            {
                writes.Add(((ushort)address, (byte)data));
            }
        }

        Assert.True(fixture.Cpu.IsHalted);
        Assert.Equal((byte)0x33, fixture.Cpu.Accumulator);
        Assert.Contains(((ushort)0x01FD, (byte)0x80), writes);
        Assert.Contains(((ushort)0x01FC, (byte)0x02), writes);
    }

    [Fact]
    public void Absolute_store_drives_address_data_and_write_control_pins()
    {
        var romImage = new byte[ushort.MaxValue + 1];
        var program = new byte[] { 0xA9, 0x5A, 0x8D, 0x34, 0x12, 0x00 };
        Array.Copy(program, 0, romImage, 0x8000, program.Length);
        romImage[0xFFFC] = 0x00;
        romImage[0xFFFD] = 0x80;

        var fixture = CreateRomFixture("test.mos6502.store", romImage, DigitalLevel.High, DigitalLevel.High);
        fixture.Board.PowerOn();
        fixture.Simulator.Settle();
        fixture.Reset.Set(DigitalLevel.High);
        fixture.Simulator.Settle();
        (ushort Address, byte Data)? observedWrite = null;
        for (var cycle = 0; cycle < 32 && !fixture.Cpu.IsHalted; cycle++)
        {
            Tick(fixture.Clock, fixture.Simulator);
            if (fixture.Cpu.ReadWrite.DriveLevel == DigitalLevel.Low &&
                fixture.Cpu.Address.TrySample(out var address) && fixture.Cpu.Data.TrySample(out var data))
            {
                observedWrite = ((ushort)address, (byte)data);
            }
        }

        Assert.True(observedWrite.HasValue);
        Assert.Equal(((ushort)0x1234, (byte)0x5A), observedWrite.Value);
    }

    [Fact]
    public void Pha_pushes_accumulator_and_decrements_stack_pointer()
    {
        var romImage = new byte[ushort.MaxValue + 1];
        var program = new byte[] { 0xA9, 0xA5, 0x48, 0x00 };
        Array.Copy(program, 0, romImage, 0x8000, program.Length);
        romImage[0xFFFC] = 0x00;
        romImage[0xFFFD] = 0x80;

        var fixture = CreateRomFixture("test.mos6502.pha", romImage, DigitalLevel.High, DigitalLevel.High);
        fixture.Board.PowerOn();
        fixture.Simulator.Settle();
        fixture.Reset.Set(DigitalLevel.High);
        fixture.Simulator.Settle();
        (ushort Address, byte Data)? observedWrite = null;
        for (var cycle = 0; cycle < 32 && !fixture.Cpu.IsHalted; cycle++)
        {
            Tick(fixture.Clock, fixture.Simulator);
            if (fixture.Cpu.ReadWrite.DriveLevel == DigitalLevel.Low &&
                fixture.Cpu.Address.TrySample(out var address) && fixture.Cpu.Data.TrySample(out var data))
            {
                observedWrite = ((ushort)address, (byte)data);
            }
        }

        Assert.True(observedWrite.HasValue);
        Assert.Equal(((ushort)0x01FD, (byte)0xA5), observedWrite.Value);
        Assert.Equal((byte)0xFC, fixture.Cpu.StackPointer);
    }

    [Fact]
    public void Indexed_addressing_reads_zero_page_and_absolute_operands_through_pins()
    {
        var romImage = new byte[ushort.MaxValue + 1];
        var program = new byte[]
        {
            0xA2, 0x03,       // LDX #$03
            0xB5, 0x20,       // LDA $20,X -> $23
            0xA0, 0x02,       // LDY #$02
            0x79, 0x00, 0x90, // ADC $9000,Y -> $9002
            0x00
        };
        Array.Copy(program, 0, romImage, 0x8000, program.Length);
        romImage[0x0023] = 0x10;
        romImage[0x9002] = 0x22;
        romImage[0xFFFC] = 0x00;
        romImage[0xFFFD] = 0x80;

        var fixture = CreateRomFixture("test.mos6502.indexed", romImage, DigitalLevel.High, DigitalLevel.High);
        RunUntilHalted(fixture);

        Assert.Equal((byte)0x32, fixture.Cpu.Accumulator);
        Assert.Equal((byte)0x03, fixture.Cpu.X);
        Assert.Equal((byte)0x02, fixture.Cpu.Y);
    }

    [Fact]
    public void Indexed_indirect_and_indirect_indexed_modes_follow_zero_page_pointer_bus_cycles()
    {
        var romImage = new byte[ushort.MaxValue + 1];
        var program = new byte[]
        {
            0xA2, 0x04, // LDX #$04
            0xA1, 0x20, // LDA ($20,X), pointer at $24/$25
            0xA0, 0x03, // LDY #$03
            0x11, 0x30, // ORA ($30),Y
            0x00
        };
        Array.Copy(program, 0, romImage, 0x8000, program.Length);
        romImage[0x0024] = 0x00;
        romImage[0x0025] = 0x90;
        romImage[0x0030] = 0x10;
        romImage[0x0031] = 0x90;
        romImage[0x9000] = 0x40;
        romImage[0x9013] = 0x05;
        romImage[0xFFFC] = 0x00;
        romImage[0xFFFD] = 0x80;

        var fixture = CreateRomFixture("test.mos6502.indirect-indexed", romImage, DigitalLevel.High, DigitalLevel.High);
        RunUntilHalted(fixture);

        Assert.Equal((byte)0x45, fixture.Cpu.Accumulator);
    }

    [Fact]
    public void Read_modify_write_performs_dummy_and_final_external_writes()
    {
        var romImage = new byte[ushort.MaxValue + 1];
        var program = new byte[] { 0xE6, 0x40, 0x00 }; // INC $40
        Array.Copy(program, 0, romImage, 0x8000, program.Length);
        romImage[0x0040] = 0x7F;
        romImage[0xFFFC] = 0x00;
        romImage[0xFFFD] = 0x80;

        var fixture = CreateRomFixture("test.mos6502.rmw", romImage, DigitalLevel.High, DigitalLevel.High);
        var writes = new List<(ushort Address, byte Data)>();
        fixture.Board.PowerOn();
        fixture.Simulator.Settle();
        fixture.Reset.Set(DigitalLevel.High);
        fixture.Simulator.Settle();
        for (var cycle = 0; cycle < 32 && !fixture.Cpu.IsHalted; cycle++)
        {
            Tick(fixture.Clock, fixture.Simulator);
            if (fixture.Cpu.ReadWrite.DriveLevel == DigitalLevel.Low &&
                fixture.Cpu.Address.TrySample(out var address) && fixture.Cpu.Data.TrySample(out var data))
            {
                writes.Add(((ushort)address, (byte)data));
            }
        }

        Assert.Equal(2, writes.Count);
        Assert.Equal(((ushort)0x0040, (byte)0x7F), writes[0]);
        Assert.Equal(((ushort)0x0040, (byte)0x80), writes[1]);
        Assert.NotEqual(0, fixture.Cpu.Status & 0x80);
    }

    [Fact]
    public void Accumulator_rotates_and_bit_update_the_expected_status_flags()
    {
        var romImage = new byte[ushort.MaxValue + 1];
        var program = new byte[]
        {
            0xA9, 0x80, // LDA #$80
            0x0A,       // ASL A -> $00, carry and zero
            0x2A,       // ROL A -> $01
            0x24, 0x50, // BIT $50: A&$C0 == 0, N and V copied
            0x00
        };
        Array.Copy(program, 0, romImage, 0x8000, program.Length);
        romImage[0x0050] = 0xC0;
        romImage[0xFFFC] = 0x00;
        romImage[0xFFFD] = 0x80;

        var fixture = CreateRomFixture("test.mos6502.bit-shift", romImage, DigitalLevel.High, DigitalLevel.High);
        RunUntilHalted(fixture);

        Assert.Equal((byte)0x01, fixture.Cpu.Accumulator);
        Assert.NotEqual(0, fixture.Cpu.Status & 0x02);
        Assert.NotEqual(0, fixture.Cpu.Status & 0x40);
        Assert.NotEqual(0, fixture.Cpu.Status & 0x80);
    }

    [Fact]
    public void Indirect_jump_reproduces_the_nmos_page_boundary_wrap()
    {
        var romImage = new byte[ushort.MaxValue + 1];
        romImage[0x8000] = 0x6C; // JMP ($30FF)
        romImage[0x8001] = 0xFF;
        romImage[0x8002] = 0x30;
        romImage[0x30FF] = 0x34;
        romImage[0x3000] = 0x92; // NMOS wrap, not $3100
        romImage[0x3100] = 0x88;
        romImage[0x9234] = 0x00;
        romImage[0xFFFC] = 0x00;
        romImage[0xFFFD] = 0x80;

        var fixture = CreateRomFixture("test.mos6502.jmp-indirect", romImage, DigitalLevel.High, DigitalLevel.High);
        RunUntilHalted(fixture);

        Assert.Equal((ushort)0x9235, fixture.Cpu.ProgramCounter);
    }

    [Fact]
    public void Rti_pulls_status_and_program_counter_from_the_external_stack_bus()
    {
        var romImage = new byte[ushort.MaxValue + 1];
        romImage[0x8000] = 0x40; // RTI
        romImage[0x01FE] = 0xC1; // N, V and C set
        romImage[0x01FF] = 0x34;
        romImage[0x0100] = 0x92;
        romImage[0x9234] = 0x00;
        romImage[0xFFFC] = 0x00;
        romImage[0xFFFD] = 0x80;

        var fixture = CreateRomFixture("test.mos6502.rti", romImage, DigitalLevel.High, DigitalLevel.High);
        RunUntilHalted(fixture);

        Assert.Equal((ushort)0x9235, fixture.Cpu.ProgramCounter);
        Assert.Equal((byte)0x00, fixture.Cpu.StackPointer);
        Assert.NotEqual(0, fixture.Cpu.Status & 0x01);
        Assert.NotEqual(0, fixture.Cpu.Status & 0x40);
        Assert.NotEqual(0, fixture.Cpu.Status & 0x80);
        Assert.Equal(0, fixture.Cpu.Status & 0x10);
    }

    private static void RunUntilHalted(RomFixture fixture)
    {
        fixture.Board.PowerOn();
        fixture.Simulator.Settle();
        fixture.Reset.Set(DigitalLevel.High);
        fixture.Simulator.Settle();
        for (var cycle = 0; cycle < 128 && !fixture.Cpu.IsHalted; cycle++)
        {
            Tick(fixture.Clock, fixture.Simulator);
        }
        Assert.True(fixture.Cpu.IsHalted);
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
