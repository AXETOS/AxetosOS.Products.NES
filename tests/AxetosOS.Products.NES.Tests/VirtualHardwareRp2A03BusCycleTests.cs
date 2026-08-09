using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Components.Power;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareRp2A03BusCycleTests
{
    [Fact]
    public void Implied_instruction_performs_its_second_cycle_dummy_read_from_the_next_pc()
    {
        var fixture = CreateFixture([0x18, 0xEA, 0x02]); // CLC, NOP, KIL
        fixture.RunUntilBus(0x8000, read: true);

        fixture.RunCpuCycles(1); // opcode fetch -> cycle 2

        Assert.Equal((ushort)0x8001, fixture.Chip.CurrentBusAddress);
        Assert.True(fixture.Chip.CurrentBusIsRead);
        Assert.Equal((byte)0x18, fixture.Chip.CurrentOpcode);
    }

    [Fact]
    public void Jsr_reads_low_operand_then_stack_dummy_pushes_return_and_only_then_reads_high_operand()
    {
        var fixture = CreateFixture([
            0x20, 0x34, 0x12, // JSR $1234
            0x02
        ]);
        fixture.Memory.Poke(0x1234, 0x02);
        fixture.RunUntilBus(0x8000, read: true);

        fixture.RunCpuCycles(1);
        AssertBus(fixture.Chip, 0x8001, read: true); // target low

        fixture.RunCpuCycles(1);
        AssertBus(fixture.Chip, 0x01FD, read: true); // stack dummy

        fixture.RunCpuCycles(1);
        AssertBus(fixture.Chip, 0x01FD, read: false); // return PCH

        fixture.RunCpuCycles(1);
        AssertBus(fixture.Chip, 0x01FC, read: false); // return PCL

        fixture.RunCpuCycles(1);
        AssertBus(fixture.Chip, 0x8002, read: true); // target high read last

        fixture.RunCpuCycles(1);
        Assert.Equal((ushort)0x1234, fixture.Chip.ProgramCounter);
        AssertBus(fixture.Chip, 0x1234, read: true);
    }

    private static void AssertBus(Rp2A03 chip, ushort address, bool read)
    {
        Assert.Equal(address, chip.CurrentBusAddress);
        Assert.Equal(read, chip.CurrentBusIsRead);
    }

    private static Fixture CreateFixture(byte[] program)
    {
        var board = new VirtualHardwareBoard("chiptest.rp2a03.bus-cycles");
        var chip = board.Add(new Rp2A03("U1"));
        var memory = board.Add(new TestBusMemory("MEM"));
        var high = board.Add(new DigitalPowerRail("VCC", DigitalLevel.High));
        var low = board.Add(new DigitalPowerRail("GND", DigitalLevel.Low));
        board.Connect("power.vcc", high.Output, chip.Vcc);
        board.Connect("power.gnd", low.Output, chip.Gnd);

        var clock = Source(board, "CLK", DigitalLevel.Low, chip.MasterClock);
        Source(board, "RES", DigitalLevel.High, chip.ResetBar);
        Source(board, "IRQ", DigitalLevel.High, chip.IrqBar);
        Source(board, "NMI", DigitalLevel.High, chip.NmiBar);
        Source(board, "IN0", DigitalLevel.Low, chip.ControllerData1);
        Source(board, "IN1", DigitalLevel.Low, chip.ControllerData2);

        for (var bit = 0; bit < 16; bit++) board.Connect($"cpu.A{bit}", chip.Address.Pins[bit], memory.Address.Pins[bit]);
        for (var bit = 0; bit < 8; bit++) board.Connect($"cpu.D{bit}", chip.Data.Pins[bit], memory.Data.Pins[bit]);
        board.Connect("cpu.RW", chip.ReadWrite, memory.ReadWrite);
        board.Connect("cpu.M2", chip.M2, memory.M2);

        memory.Load(0x8000, program);
        memory.Poke(0xFFFC, 0x00);
        memory.Poke(0xFFFD, 0x80);

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();
        return new Fixture(chip, memory, clock, simulator);
    }

    private static DigitalSignalSource Source(VirtualHardwareBoard board, string id, DigitalLevel level, DigitalPin target)
    {
        var source = board.Add(new DigitalSignalSource($"src.{id}", level));
        board.Connect($"net.{id}", source.Output, target);
        return source;
    }

    private sealed record Fixture(Rp2A03 Chip, TestBusMemory Memory, DigitalSignalSource Clock, VirtualHardwareSimulator Simulator)
    {
        public void RunCpuCycles(int cycles)
        {
            for (var edge = 0; edge < cycles * 12; edge++)
            {
                Clock.Set(DigitalLevel.High); Simulator.Settle();
                Clock.Set(DigitalLevel.Low); Simulator.Settle();
            }
        }

        public void RunUntilBus(ushort address, bool read)
        {
            for (var cycle = 0; cycle < 64; cycle++)
            {
                if (Chip.CurrentBusAddress == address && Chip.CurrentBusIsRead == read) return;
                RunCpuCycles(1);
            }
            throw new InvalidOperationException($"CPU did not reach {(read ? "read" : "write")} ${address:X4}.");
        }
    }

    private sealed class TestBusMemory : VirtualHardwareComponent
    {
        private readonly byte[] _storage = new byte[65536];
        private bool _writeCapturedDuringHighPhase;

        public TestBusMemory(string id) : base(id)
        {
            Address = new DigitalBus($"{id}.A", Enumerable.Range(0, 16).Select(bit => AddPin($"A{bit}", PinDirection.Input)).ToArray());
            Data = new DigitalBus($"{id}.D", Enumerable.Range(0, 8).Select(bit => AddPin($"D{bit}", PinDirection.Bidirectional)).ToArray());
            ReadWrite = AddPin("R/W", PinDirection.Input);
            M2 = AddPin("M2", PinDirection.Input);
            Data.Release();
        }

        public DigitalBus Address { get; }
        public DigitalBus Data { get; }
        public DigitalPin ReadWrite { get; }
        public DigitalPin M2 { get; }
        public void Load(int address, ReadOnlySpan<byte> bytes) => bytes.CopyTo(_storage.AsSpan(address));
        public void Poke(int address, byte value) => _storage[address] = value;

        protected override void OnInputChanges(ulong changedInputMask)
        {
            if (ReadWrite.SampledLevel == DigitalLevel.High && Address.TrySample(out var readAddress)) Data.Drive(_storage[(ushort)readAddress]);
            else Data.Release();

            if (M2.SampledLevel == DigitalLevel.Low) _writeCapturedDuringHighPhase = false;
            else if (M2.SampledLevel == DigitalLevel.High && !_writeCapturedDuringHighPhase
                     && ReadWrite.SampledLevel == DigitalLevel.Low && Address.TrySample(out var writeAddress) && Data.TrySample(out var writeData))
            {
                _storage[(ushort)writeAddress] = (byte)writeData;
                _writeCapturedDuringHighPhase = true;
            }
        }
    }
}
