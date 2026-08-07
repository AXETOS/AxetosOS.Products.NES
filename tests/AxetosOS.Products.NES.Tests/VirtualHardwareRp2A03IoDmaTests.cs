using Xunit;
using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Components.Power;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareRp2A03IoDmaTests
{
    [Fact]
    public void Controller_output_register_drives_all_three_package_pins()
    {
        var fixture = CreateFixture([0xA9, 0x07, 0x8D, 0x16, 0x40, 0x02]);

        fixture.RunCpuCycles(40);

        Assert.True(fixture.Chip.IsHalted);
        Assert.Equal(0x07, fixture.Chip.ControllerOutputLatch);
        Assert.Equal(DigitalLevel.High, fixture.Out0.Level);
        Assert.Equal(DigitalLevel.High, fixture.Out1.Level);
        Assert.Equal(DigitalLevel.High, fixture.Out2.Level);
    }

    [Fact]
    public void Controller_read_uses_input_pin_and_asserts_the_selected_output_enable()
    {
        var fixture = CreateFixture([0xAD, 0x16, 0x40, 0x8D, 0x00, 0x60, 0x02], controller1: DigitalLevel.High);

        fixture.RunCpuCycles(50);

        Assert.True(fixture.Chip.IsHalted);
        Assert.Equal((byte)1, fixture.Memory.Inspect(0x6000));
        Assert.Contains(DigitalLevel.Low, fixture.Memory.Controller1EnableHistory);
        Assert.DoesNotContain(DigitalLevel.Low, fixture.Memory.Controller2EnableHistory);
    }

    [Fact]
    public void Oam_dma_copies_exactly_256_external_bus_bytes_to_2004()
    {
        var fixture = CreateFixture([0xA9, 0x02, 0x8D, 0x14, 0x40, 0x02]);
        for (var index = 0; index < 256; index++)
        {
            fixture.Memory.Poke(0x0200 + index, (byte)index);
        }

        fixture.RunCpuCycles(600);

        Assert.True(fixture.Chip.IsHalted);
        Assert.False(fixture.Chip.DmaActive);
        Assert.Equal(256UL, fixture.Chip.DmaTransferCount);
        Assert.Equal(256, fixture.Memory.OamWrites.Count);
        Assert.Equal(Enumerable.Range(0, 256).Select(value => (byte)value), fixture.Memory.OamWrites);
    }



    [Fact]
    public void Reset_decrements_the_existing_stack_pointer_and_preserves_cpu_flags()
    {
        var fixture = CreateFixture([
            0xA2, 0x80, // LDX #$80
            0x9A,       // TXS
            0x38,       // SEC
            0x58,       // CLI
            0x02        // KIL halt
        ]);

        fixture.RunCpuCycles(40);

        Assert.True(fixture.Chip.IsHalted);
        Assert.Equal((byte)0x80, fixture.Chip.StackPointer);
        Assert.Equal((byte)0x01, (byte)(fixture.Chip.Status & 0x01));
        Assert.False(fixture.Chip.InterruptDisable);

        fixture.Reset.Set(DigitalLevel.Low);
        fixture.RunCpuCycles(1);
        fixture.Reset.Set(DigitalLevel.High);
        fixture.RunCpuCycles(7);

        Assert.Equal((byte)0x7D, fixture.Chip.StackPointer);
        Assert.Equal((byte)0x01, (byte)(fixture.Chip.Status & 0x01));
        Assert.True(fixture.Chip.InterruptDisable);
        Assert.Equal((ushort)0x8000, fixture.Chip.ProgramCounter);
    }


    [Fact]
    public void Hardware_irq_stacks_the_pre_interrupt_status_before_setting_interrupt_disable()
    {
        var fixture = CreateFixture([
            0x58, // CLI
            0xEA, // NOP -- IRQ is accepted at the following opcode boundary
            0x02
        ]);

        fixture.Memory.Poke(0xFFFE, 0x00);
        fixture.Memory.Poke(0xFFFF, 0x90);
        fixture.Memory.Poke(0x9000, 0x02);
        fixture.Irq.Set(DigitalLevel.Low);

        fixture.RunCpuCycles(40);

        Assert.Equal(1UL, fixture.Chip.CompletedInterruptCount);
        Assert.True(fixture.Chip.InterruptDisable);
        Assert.Equal((byte)0x00, (byte)(fixture.Memory.Inspect(0x01FB) & 0x04));
    }

    [Fact]
    public void Dmc_fetch_uses_an_oam_read_slot_without_corrupting_the_transfer()
    {
        var program = new byte[]
        {
            0xA9, 0x0F, 0x8D, 0x10, 0x40, // fastest DMC period
            0xA9, 0x00, 0x8D, 0x12, 0x40, // sample starts at $C000
            0xA9, 0x01, 0x8D, 0x13, 0x40, // 17-byte sample
            0xA9, 0x10, 0x8D, 0x15, 0x40, // enable DMC
            0xA9, 0x02, 0x8D, 0x14, 0x40, // first OAM DMA from $0200
            0xA9, 0x02, 0x8D, 0x14, 0x40, // second consecutive OAM DMA
            0x02
        };

        var fixture = CreateFixture(program);

        for (var index = 0; index < 256; index++)
        {
            fixture.Memory.Poke(0x0200 + index, (byte)(index ^ 0xA5));
        }
        for (var index = 0; index < 17; index++)
        {
            fixture.Memory.Poke(0xC000 + index, 0xFF);
        }

        // One OAM transfer can finish before the DMC's retained power-up
        // divider phase empties its first sample buffer.  Two consecutive
        // transfers provide a deterministic bus-ownership window without
        // reaching into the chip or forcing its private timer state.
        fixture.RunCpuCycles(1_500);

        Assert.True(fixture.Chip.IsHalted);
        Assert.False(fixture.Chip.DmaActive);
        Assert.True(fixture.Chip.DmcOamDmaInterleaveCount > 0);
        Assert.True(fixture.Chip.DmcMemoryReadCount > 1);
        Assert.Equal(512UL, fixture.Chip.DmaTransferCount);

        var expectedTransfer = Enumerable.Range(0, 256).Select(value => (byte)(value ^ 0xA5));
        Assert.Equal(expectedTransfer.Concat(expectedTransfer), fixture.Memory.OamWrites);
    }


    [Fact]
    public void Brk_reads_padding_stacks_pc_and_break_status_then_uses_irq_vector()
    {
        var fixture = CreateFixture([
            0x18,       // CLC
            0x00, 0xEA, // BRK plus padding byte
            0x02        // return target sentinel
        ]);
        fixture.Memory.Poke(0xFFFE, 0x00);
        fixture.Memory.Poke(0xFFFF, 0x90);
        fixture.Memory.Poke(0x9000, 0x02);

        fixture.RunCpuCycles(40);

        Assert.True(fixture.Chip.IsHalted);
        Assert.Equal((byte)0x80, fixture.Memory.Inspect(0x01FD));
        Assert.Equal((byte)0x03, fixture.Memory.Inspect(0x01FC));
        Assert.Equal((byte)0x30, (byte)(fixture.Memory.Inspect(0x01FB) & 0x30));
        Assert.True(fixture.Chip.InterruptDisable);
    }

    [Fact]
    public void Stable_unofficial_lax_sax_and_slo_execute_through_the_external_bus()
    {
        var fixture = CreateFixture([
            0xA9, 0xF3,       // LDA #$F3
            0xA2, 0x3F,       // LDX #$3F
            0x87, 0x10,       // SAX $10 => $33
            0xA7, 0x11,       // LAX $11 => A=X=$81
            0x07, 0x11,       // SLO $11 => $02, A=$83, C=1
            0x02
        ]);
        fixture.Memory.Poke(0x0011, 0x81);

        fixture.RunCpuCycles(80);

        Assert.True(fixture.Chip.IsHalted);
        Assert.Equal((byte)0x33, fixture.Memory.Inspect(0x0010));
        Assert.Equal((byte)0x02, fixture.Memory.Inspect(0x0011));
        Assert.Equal((byte)0x83, fixture.Chip.Accumulator);
        Assert.Equal((byte)0x81, fixture.Chip.X);
        Assert.Equal((byte)0x01, (byte)(fixture.Chip.Status & 0x01));
    }

    [Fact]
    public void Unofficial_read_nops_preserve_instruction_lengths_and_kil_waits_for_reset()
    {
        var fixture = CreateFixture([
            0x80, 0xAA,       // immediate NOP
            0x04, 0x10,       // zero-page read NOP
            0x0C, 0x00, 0x60, // absolute read NOP
            0x02
        ]);
        fixture.Memory.Poke(0x0010, 0x55);
        fixture.Memory.Poke(0x6000, 0xAA);

        fixture.RunCpuCycles(50);

        Assert.True(fixture.Chip.IsHalted);
        Assert.Equal((ushort)0x8008, fixture.Chip.ProgramCounter);

        fixture.Reset.Set(DigitalLevel.Low);
        fixture.RunCpuCycles(1);
        fixture.Reset.Set(DigitalLevel.High);
        fixture.RunCpuCycles(7);
        Assert.False(fixture.Chip.IsHalted);
        Assert.Equal((ushort)0x8000, fixture.Chip.ProgramCounter);
    }

    private static Fixture CreateFixture(byte[] program, DigitalLevel controller1 = DigitalLevel.Low)
    {
        var board = new VirtualHardwareBoard("chiptest.rp2a03.io-dma");
        var chip = board.Add(new Rp2A03("U1"));
        var memory = board.Add(new TestBusMemory("MEM"));

        var high = board.Add(new DigitalPowerRail("VCC", DigitalLevel.High));
        var low = board.Add(new DigitalPowerRail("GND", DigitalLevel.Low));
        board.Connect("power.vcc", high.Output, chip.Vcc);
        board.Connect("power.gnd", low.Output, chip.Gnd);

        var clock = Source(board, "CLK", DigitalLevel.Low, chip.MasterClock);
        var reset = Source(board, "RES", DigitalLevel.High, chip.ResetBar);
        var irq = Source(board, "IRQ", DigitalLevel.High, chip.IrqBar);
        Source(board, "NMI", DigitalLevel.High, chip.NmiBar);
        Source(board, "IN0", controller1, chip.ControllerData1);
        Source(board, "IN1", DigitalLevel.Low, chip.ControllerData2);

        for (var bit = 0; bit < 16; bit++)
        {
            board.Connect($"cpu.A{bit}", chip.Address.Pins[bit], memory.Address.Pins[bit]);
        }

        for (var bit = 0; bit < 8; bit++)
        {
            board.Connect($"cpu.D{bit}", chip.Data.Pins[bit], memory.Data.Pins[bit]);
        }

        board.Connect("cpu.RW", chip.ReadWrite, memory.ReadWrite);
        board.Connect("cpu.M2", chip.M2, memory.M2);
        var oe1 = board.Connect("cpu.OE1", chip.ControllerRead1Bar, memory.ControllerRead1Bar);
        var oe2 = board.Connect("cpu.OE2", chip.ControllerRead2Bar, memory.ControllerRead2Bar);
        var out0 = board.Connect("cpu.OUT0", chip.ControllerOut0);
        var out1 = board.Connect("cpu.OUT1", chip.ControllerOut1);
        var out2 = board.Connect("cpu.OUT2", chip.ControllerOut2);

        memory.Load(0x8000, program);
        memory.Poke(0xFFFC, 0x00);
        memory.Poke(0xFFFD, 0x80);

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();

        return new Fixture(chip, memory, clock, reset, irq, simulator, out0, out1, out2, oe1, oe2);
    }

    private static DigitalSignalSource Source(VirtualHardwareBoard board, string id, DigitalLevel level, DigitalPin target)
    {
        var source = board.Add(new DigitalSignalSource($"src.{id}", level));
        board.Connect($"net.{id}", source.Output, target);
        return source;
    }

    private sealed record Fixture(
        Rp2A03 Chip,
        TestBusMemory Memory,
        DigitalSignalSource Clock,
        DigitalSignalSource Reset,
        DigitalSignalSource Irq,
        VirtualHardwareSimulator Simulator,
        DigitalNet Out0,
        DigitalNet Out1,
        DigitalNet Out2,
        DigitalNet Oe1,
        DigitalNet Oe2)
    {
        public void RunCpuCycles(int cycles)
        {
            for (var edge = 0; edge < cycles * 12; edge++)
            {
                Clock.Set(DigitalLevel.High);
                Simulator.Settle();
                Clock.Set(DigitalLevel.Low);
                Simulator.Settle();
            }
        }
    }

    private sealed class TestBusMemory : VirtualHardwareComponent
    {
        private readonly byte[] _storage = new byte[65536];
        private DigitalLevel _previousM2 = DigitalLevel.Low;
        private bool _writeCapturedDuringHighPhase;

        public TestBusMemory(string id) : base(id)
        {
            Address = new DigitalBus($"{id}.A", Enumerable.Range(0, 16).Select(bit => AddPin($"A{bit}", PinDirection.Input)).ToArray());
            Data = new DigitalBus($"{id}.D", Enumerable.Range(0, 8).Select(bit => AddPin($"D{bit}", PinDirection.Bidirectional)).ToArray());
            ReadWrite = AddPin("R/W", PinDirection.Input);
            M2 = AddPin("M2", PinDirection.Input);
            ControllerRead1Bar = AddPin("/OE1", PinDirection.Input);
            ControllerRead2Bar = AddPin("/OE2", PinDirection.Input);
        
            InitializeState();
        }

        public DigitalBus Address { get; }
        public DigitalBus Data { get; }
        public DigitalPin ReadWrite { get; }
        public DigitalPin M2 { get; }
        public DigitalPin ControllerRead1Bar { get; }
        public DigitalPin ControllerRead2Bar { get; }
        public List<byte> OamWrites { get; } = [];
        public List<DigitalLevel> Controller1EnableHistory { get; } = [];
        public List<DigitalLevel> Controller2EnableHistory { get; } = [];

        public void Load(int address, ReadOnlySpan<byte> bytes) => bytes.CopyTo(_storage.AsSpan(address));
        public void Poke(int address, byte value) => _storage[address] = value;
        public byte Inspect(int address) => _storage[address];

        private void InitializeState()
        {
            _previousM2 = DigitalLevel.Low;
            _writeCapturedDuringHighPhase = false;
            OamWrites.Clear();
            Controller1EnableHistory.Clear();
            Controller2EnableHistory.Clear();
            Data.Release();
        }

        protected override void OnInputChanges(ulong changedInputMask)
        {
            Controller1EnableHistory.Add(ControllerRead1Bar.SampledLevel);
            Controller2EnableHistory.Add(ControllerRead2Bar.SampledLevel);

            if (ReadWrite.SampledLevel == DigitalLevel.High && Address.TrySample(out var readAddress))
            {
                Data.Drive(_storage[(ushort)readAddress]);
            }
            else
            {
                Data.Release();
            }

            var currentM2 = M2.SampledLevel;
            if (currentM2 == DigitalLevel.Low)
            {
                _writeCapturedDuringHighPhase = false;
            }
            else if (currentM2 == DigitalLevel.High &&
                     !_writeCapturedDuringHighPhase &&
                     ReadWrite.SampledLevel == DigitalLevel.Low &&
                     Address.TrySample(out var writeAddress) && Data.TrySample(out var writeData))
            {
                // The chip, bus nets and memory model settle over multiple
                // propagation passes while M2 remains high. Capture the write
                // once the complete bus transaction is resolved rather than
                // only on the first pass where M2 changes state.
                var address = (ushort)writeAddress;
                var value = (byte)writeData;
                _storage[address] = value;
                if (address == 0x2004) OamWrites.Add(value);
                _writeCapturedDuringHighPhase = true;
            }

            if (currentM2 is DigitalLevel.Low or DigitalLevel.High) _previousM2 = currentM2;
        }
    }
}
