using Xunit;
using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Components.Power;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareRp2A03ApuAccuracyTests
{
    [Fact]
    public void Audio_dac_uses_nonlinear_rp2a03_mixer_curve()
    {
        var fixture = CreateFixture([
            0xA9, 0x40, 0x8D, 0x11, 0x40,
            0x00
        ]);

        fixture.RunCpuCycles(80);

        Assert.True(fixture.Chip.IsHalted);
        Assert.Equal((byte)0x40, fixture.Chip.DmcOutputLevel);
        Assert.Equal((byte)90, fixture.Chip.AudioDacLevel);
    }

    [Fact]
    public void Status_read_reports_active_channels_through_internal_apu_register()
    {
        var fixture = CreateFixture([
            0xA9, 0x05, 0x8D, 0x15, 0x40, // enable pulse 1 and triangle
            0xA9, 0x10, 0x8D, 0x00, 0x40,
            0xA9, 0x08, 0x8D, 0x02, 0x40,
            0xA9, 0x08, 0x8D, 0x03, 0x40,
            0xA9, 0x80, 0x8D, 0x08, 0x40,
            0xA9, 0x08, 0x8D, 0x0A, 0x40,
            0xA9, 0x08, 0x8D, 0x0B, 0x40,
            0xAD, 0x15, 0x40,
            0x8D, 0x00, 0x60,
            0x00
        ]);

        fixture.RunCpuCycles(220);

        Assert.True(fixture.Chip.IsHalted);
        Assert.Equal((byte)0x05, (byte)(fixture.Memory.Peek(0x6000) & 0x1F));
    }

    [Fact]
    public void Frame_counter_write_is_applied_after_hardware_delay()
    {
        var fixture = CreateFixture([
            0xA9, 0x80,
            0x8D, 0x17, 0x40,
            0x00
        ]);

        for (var cycle = 0; cycle < 100 && !fixture.Chip.FrameCounterWritePending; cycle++)
            fixture.RunCpuCycles(1);

        Assert.True(fixture.Chip.FrameCounterWritePending);
        Assert.False(fixture.Chip.FrameFiveStepMode);

        fixture.RunCpuCycles(2);
        Assert.True(fixture.Chip.FrameCounterWritePending);
        Assert.False(fixture.Chip.FrameFiveStepMode);

        fixture.RunCpuCycles(2);
        Assert.False(fixture.Chip.FrameCounterWritePending);
        Assert.True(fixture.Chip.FrameFiveStepMode);
        Assert.InRange(fixture.Chip.FrameSequenceCycle, 0, 2);
    }

    private static Fixture CreateFixture(byte[] program)
    {
        var board = new VirtualHardwareBoard("chiptest.rp2a03.apu-accuracy");
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
        board.Connect("cpu.AUDIO", chip.AudioOut);

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
        }

        public DigitalBus Address { get; }
        public DigitalBus Data { get; }
        public DigitalPin ReadWrite { get; }
        public DigitalPin M2 { get; }
        public void Load(int address, ReadOnlySpan<byte> bytes) => bytes.CopyTo(_storage.AsSpan(address));
        public void Poke(int address, byte value) => _storage[address] = value;
        public byte Peek(int address) => _storage[address];

        public override void PowerOn() { _writeCapturedDuringHighPhase = false; Data.Release(); }

        public override void Evaluate()
        {
            if (ReadWrite.SampledLevel == DigitalLevel.High && Address.TrySample(out var readAddress)) Data.Drive(_storage[(ushort)readAddress]);
            else Data.Release();

            if (M2.SampledLevel == DigitalLevel.Low) _writeCapturedDuringHighPhase = false;
            else if (M2.SampledLevel == DigitalLevel.High && !_writeCapturedDuringHighPhase &&
                     ReadWrite.SampledLevel == DigitalLevel.Low && Address.TrySample(out var writeAddress) && Data.TrySample(out var writeData))
            {
                _storage[(ushort)writeAddress] = (byte)writeData;
                _writeCapturedDuringHighPhase = true;
            }
        }
    }
}
