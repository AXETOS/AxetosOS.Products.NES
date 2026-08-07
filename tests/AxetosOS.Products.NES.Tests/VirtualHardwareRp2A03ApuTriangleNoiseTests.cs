using Xunit;
using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Components.Power;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareRp2A03ApuTriangleNoiseTests
{
    [Fact]
    public void Triangle_registers_load_timer_length_linear_counter_and_generate_output()
    {
        var fixture = CreateFixture(
        [
            0xA9, 0x04, 0x8D, 0x15, 0x40, // enable triangle
            0xA9, 0x85, 0x8D, 0x08, 0x40, // control flag, linear reload 5
            0xA9, 0x02, 0x8D, 0x0A, 0x40, // timer low
            0xA9, 0x08, 0x8D, 0x0B, 0x40, // timer high 0, length index 1
            0x02
        ]);

        fixture.RunCpuCycles(120);

        Assert.True(fixture.Chip.IsHalted);
        Assert.Equal((ushort)2, fixture.Chip.TriangleTimerPeriod);
        Assert.Equal((byte)254, fixture.Chip.TriangleLengthCounter);

        fixture.RunCpuCycles(7_500);

        Assert.True(fixture.Chip.TriangleLinearCounter > 0);
        var observedNonZero = false;
        for (var cycle = 0; cycle < 128; cycle++)
        {
            fixture.RunCpuCycles(1);
            observedNonZero |= fixture.Chip.TriangleOutputLevel > 0;
        }
        Assert.True(observedNonZero);
    }

    [Fact]
    public void Noise_registers_select_period_load_length_and_advance_lfsr()
    {
        var fixture = CreateFixture(
        [
            0xA9, 0x08, 0x8D, 0x15, 0x40, // enable noise
            0xA9, 0x3F, 0x8D, 0x0C, 0x40, // halt length, constant volume 15
            0xA9, 0x00, 0x8D, 0x0E, 0x40, // long mode, period table index 0
            0xA9, 0x08, 0x8D, 0x0F, 0x40, // length index 1
            0x02
        ]);

        fixture.RunCpuCycles(120);

        Assert.True(fixture.Chip.IsHalted);
        Assert.Equal((ushort)4, fixture.Chip.NoiseTimerPeriod);
        Assert.Equal((byte)254, fixture.Chip.NoiseLengthCounter);

        var initialShift = fixture.Chip.NoiseShiftRegister;
        var observedNonZero = false;
        for (var cycle = 0; cycle < 200; cycle++)
        {
            fixture.RunCpuCycles(1);
            observedNonZero |= fixture.Chip.NoiseOutputLevel > 0;
        }

        Assert.NotEqual(initialShift, fixture.Chip.NoiseShiftRegister);
        Assert.True(observedNonZero);
    }

    [Fact]
    public void Status_disable_clears_triangle_and_noise_length_counters()
    {
        var fixture = CreateFixture(
        [
            0xA9, 0x0C, 0x8D, 0x15, 0x40,
            0xA9, 0x80, 0x8D, 0x08, 0x40,
            0xA9, 0x02, 0x8D, 0x0A, 0x40,
            0xA9, 0x08, 0x8D, 0x0B, 0x40,
            0xA9, 0x10, 0x8D, 0x0C, 0x40,
            0xA9, 0x00, 0x8D, 0x0E, 0x40,
            0xA9, 0x08, 0x8D, 0x0F, 0x40,
            0xA9, 0x00, 0x8D, 0x15, 0x40,
            0x02
        ]);

        fixture.RunCpuCycles(220);

        Assert.True(fixture.Chip.IsHalted);
        Assert.Equal((byte)0, fixture.Chip.TriangleLengthCounter);
        Assert.Equal((byte)0, fixture.Chip.NoiseLengthCounter);
        Assert.Equal((byte)0, fixture.Chip.TriangleOutputLevel);
        Assert.Equal((byte)0, fixture.Chip.NoiseOutputLevel);
    }

    private static Fixture CreateFixture(byte[] program)
    {
        var board = new VirtualHardwareBoard("chiptest.rp2a03.apu-triangle-noise");
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

        for (var bit = 0; bit < 16; bit++)
            board.Connect($"cpu.A{bit}", chip.Address.Pins[bit], memory.Address.Pins[bit]);
        for (var bit = 0; bit < 8; bit++)
            board.Connect($"cpu.D{bit}", chip.Data.Pins[bit], memory.Data.Pins[bit]);
        board.Connect("cpu.RW", chip.ReadWrite, memory.ReadWrite);
        board.Connect("cpu.M2", chip.M2, memory.M2);
        board.Connect("cpu.AUDIO", chip.AudioOut);

        memory.Load(0x8000, program);
        memory.Poke(0xFFFC, 0x00);
        memory.Poke(0xFFFD, 0x80);

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();
        return new Fixture(chip, clock, simulator);
    }

    private static DigitalSignalSource Source(VirtualHardwareBoard board, string id, DigitalLevel level, DigitalPin target)
    {
        var source = board.Add(new DigitalSignalSource($"src.{id}", level));
        board.Connect($"net.{id}", source.Output, target);
        return source;
    }

    private sealed record Fixture(Rp2A03 Chip, DigitalSignalSource Clock, VirtualHardwareSimulator Simulator)
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
        private bool _writeCapturedDuringHighPhase;

        public TestBusMemory(string id) : base(id)
        {
            Address = new DigitalBus($"{id}.A", Enumerable.Range(0, 16).Select(bit => AddPin($"A{bit}", PinDirection.Input)).ToArray());
            Data = new DigitalBus($"{id}.D", Enumerable.Range(0, 8).Select(bit => AddPin($"D{bit}", PinDirection.Bidirectional)).ToArray());
            ReadWrite = AddPin("R/W", PinDirection.Input);
            M2 = AddPin("M2", PinDirection.Input);
        
            InitializeState();
        }

        public DigitalBus Address { get; }
        public DigitalBus Data { get; }
        public DigitalPin ReadWrite { get; }
        public DigitalPin M2 { get; }
        public void Load(int address, ReadOnlySpan<byte> bytes) => bytes.CopyTo(_storage.AsSpan(address));
        public void Poke(int address, byte value) => _storage[address] = value;

        private void InitializeState()
        {
            _writeCapturedDuringHighPhase = false;
            Data.Release();
        }

        protected override void OnInputChanges(ulong changedInputMask)
        {
            if (ReadWrite.SampledLevel == DigitalLevel.High && Address.TrySample(out var readAddress))
                Data.Drive(_storage[(ushort)readAddress]);
            else
                Data.Release();

            if (M2.SampledLevel == DigitalLevel.Low)
            {
                _writeCapturedDuringHighPhase = false;
            }
            else if (M2.SampledLevel == DigitalLevel.High &&
                     !_writeCapturedDuringHighPhase &&
                     ReadWrite.SampledLevel == DigitalLevel.Low &&
                     Address.TrySample(out var writeAddress) &&
                     Data.TrySample(out var writeData))
            {
                _storage[(ushort)writeAddress] = (byte)writeData;
                _writeCapturedDuringHighPhase = true;
            }
        }
    }
}
