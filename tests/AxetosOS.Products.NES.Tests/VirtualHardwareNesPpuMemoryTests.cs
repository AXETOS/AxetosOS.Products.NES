using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Components.Nes;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareNesPpuMemoryTests
{
    [Fact]
    public void Chr_rom_is_read_through_pins_and_ignores_bus_writes()
    {
        var chr = new byte[8 * 1024];
        chr[0x1234] = 0xA5;
        var fixture = new PpuMemoryFixture(chr, NesNametableMirroring.Horizontal);

        Assert.Equal(0xA5, fixture.Read(0x1234));
        fixture.Write(0x1234, 0x5A);
        Assert.Equal(0xA5, fixture.Read(0x1234));
        Assert.False(fixture.Memory.IsCharacterRam);
    }

    [Fact]
    public void Empty_chr_payload_constructs_writable_chr_ram()
    {
        var fixture = new PpuMemoryFixture([], NesNametableMirroring.Horizontal);

        fixture.Write(0x0456, 0x6C);

        Assert.True(fixture.Memory.IsCharacterRam);
        Assert.Equal(0x6C, fixture.Read(0x0456));
    }

    [Theory]
    [InlineData(NesNametableMirroring.Vertical, 0x2007, 0x2807)]
    [InlineData(NesNametableMirroring.Vertical, 0x2407, 0x2C07)]
    [InlineData(NesNametableMirroring.Horizontal, 0x2007, 0x2407)]
    [InlineData(NesNametableMirroring.Horizontal, 0x2807, 0x2C07)]
    public void Ciram_mirroring_is_created_by_the_selected_cartridge_wiring(
        NesNametableMirroring mirroring,
        int first,
        int mirror)
    {
        var fixture = new PpuMemoryFixture([], mirroring);

        fixture.Write((ushort)first, 0x39);

        Assert.Equal(0x39, fixture.Read((ushort)mirror));
    }

    [Fact]
    public void Palette_ram_applies_nes_mirrors_and_six_bit_storage()
    {
        var fixture = new PpuMemoryFixture([], NesNametableMirroring.Horizontal);

        fixture.Write(0x3F10, 0xFF);

        Assert.Equal(0x3F, fixture.Read(0x3F00));
        Assert.Equal(0x3F, fixture.Read(0x3F30));
    }

    [Fact]
    public void Rom_factory_carries_chr_and_mirroring_into_the_motherboard()
    {
        var rom = CreateNrom(chrValue: 0x7B, verticalMirroring: true);

        var machine = VirtualHardwareNesMachineFactory.Load(rom, "Game (USA).nes");

        Assert.Equal(NesNametableMirroring.Vertical, machine.Motherboard.PpuMemory.Mirroring);
        Assert.False(machine.Motherboard.PpuMemory.IsCharacterRam);
        Assert.Equal(0x7B, machine.Motherboard.PpuMemory.Inspect(0));
    }

    private static byte[] CreateNrom(byte chrValue, bool verticalMirroring)
    {
        var rom = new byte[16 + (16 * 1024) + (8 * 1024)];
        rom[0] = 0x4E;
        rom[1] = 0x45;
        rom[2] = 0x53;
        rom[3] = 0x1A;
        rom[4] = 1;
        rom[5] = 1;
        rom[6] = verticalMirroring ? (byte)0x01 : (byte)0x00;
        rom[16 + 0x3FFC] = 0x00;
        rom[16 + 0x3FFD] = 0x80;
        rom[16 + (16 * 1024)] = chrValue;
        return rom;
    }

    private sealed class PpuMemoryFixture
    {
        private readonly DigitalSignalSource[] _address;
        private readonly DigitalSignalSource[] _data;
        private readonly DigitalSignalSource _readBar;
        private readonly DigitalSignalSource _writeBar;
        private readonly VirtualHardwareSimulator _simulator;
        private readonly VirtualHardwareBoard _board;

        public PpuMemoryFixture(ReadOnlySpan<byte> chr, NesNametableMirroring mirroring)
        {
            _board = new VirtualHardwareBoard("ppu-memory-test");
            Memory = _board.Add(new NesPpuMemoryDevice("memory", chr, mirroring));
            _address = CreateSources("address", 14, DigitalLevel.Low);
            _data = CreateSources("data", 8, DigitalLevel.HighImpedance);
            _readBar = _board.Add(new DigitalSignalSource("read", DigitalLevel.High));
            _writeBar = _board.Add(new DigitalSignalSource("write", DigitalLevel.High));
            for (var bit = 0; bit < 14; bit++) _board.Connect($"A{bit}", _address[bit].Output, Memory.Address.Pins[bit]);
            for (var bit = 0; bit < 8; bit++) _board.Connect($"D{bit}", _data[bit].Output, Memory.Data.Pins[bit]);
            _board.Connect("/RD", _readBar.Output, Memory.ReadBar);
            _board.Connect("/WR", _writeBar.Output, Memory.WriteBar);
            _simulator = new VirtualHardwareSimulator(_board);
            _board.PowerOn();
            _simulator.Settle();
        }

        public NesPpuMemoryDevice Memory { get; }

        public byte Read(ushort address)
        {
            SetAddress(address);
            ReleaseData();
            _writeBar.Set(DigitalLevel.High);
            _readBar.Set(DigitalLevel.Low);
            _simulator.Settle();
            var value = SampleData();
            _readBar.Set(DigitalLevel.High);
            _simulator.Settle();
            return value;
        }

        public void Write(ushort address, byte value)
        {
            SetAddress(address);
            _readBar.Set(DigitalLevel.High);
            SetData(value);
            _writeBar.Set(DigitalLevel.Low);
            _simulator.Settle();
            _writeBar.Set(DigitalLevel.High);
            ReleaseData();
            _simulator.Settle();
        }

        private DigitalSignalSource[] CreateSources(string prefix, int count, DigitalLevel initial)
        {
            var result = new DigitalSignalSource[count];
            for (var bit = 0; bit < count; bit++) result[bit] = _board.Add(new DigitalSignalSource($"{prefix}{bit}", initial));
            return result;
        }

        private void SetAddress(ushort address)
        {
            for (var bit = 0; bit < _address.Length; bit++)
                _address[bit].Set((address & (1 << bit)) != 0 ? DigitalLevel.High : DigitalLevel.Low);
        }

        private void SetData(byte value)
        {
            for (var bit = 0; bit < _data.Length; bit++)
                _data[bit].Set((value & (1 << bit)) != 0 ? DigitalLevel.High : DigitalLevel.Low);
        }

        private void ReleaseData()
        {
            foreach (var source in _data) source.Set(DigitalLevel.HighImpedance);
        }

        private byte SampleData()
        {
            byte result = 0;
            for (var bit = 0; bit < 8; bit++)
            {
                var net = _board.Nets.Single(candidate => candidate.Name == $"D{bit}");
                if (net.Level == DigitalLevel.High) result |= (byte)(1 << bit);
                else Assert.Equal(DigitalLevel.Low, net.Level);
            }
            return result;
        }
    }
}
