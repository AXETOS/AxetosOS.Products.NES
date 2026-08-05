using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareRp2C02StandaloneTests
{
    [Fact]
    public void Package_advances_raster_only_from_powered_master_clock_edges()
    {
        var fixture = new Fixture();

        fixture.PulseClock(342);

        Assert.Equal(342UL, fixture.Chip.MasterClockRisingEdgeCount);
        Assert.Equal(1, fixture.Chip.Scanline);
        Assert.Equal(1, fixture.Chip.Dot);
        Assert.Equal(0UL, fixture.Chip.Frame);
    }

    [Fact]
    public void Cpu_control_register_is_written_only_through_package_bus_pins()
    {
        var fixture = new Fixture();

        fixture.WriteRegister(0, 0x80);

        Assert.Equal((byte)0x80, fixture.Chip.ControlRegister);
        Assert.True(fixture.Chip.NmiEnabled);
    }

    [Fact]
    public void Cpu_registers_own_oam_and_scroll_address_latches_inside_the_chip()
    {
        var fixture = new Fixture();

        fixture.WriteRegister(1, 0x1E);
        fixture.WriteRegister(3, 0xFE);
        fixture.WriteRegister(4, 0xA5);
        fixture.WriteRegister(4, 0x5A);
        fixture.WriteRegister(5, 0x2D);
        fixture.WriteRegister(5, 0x73);

        Assert.Equal((byte)0x1E, fixture.Chip.MaskRegister);
        Assert.Equal((byte)0xA5, fixture.Chip.InspectOam(0xFE));
        Assert.Equal((byte)0x5A, fixture.Chip.InspectOam(0xFF));
        Assert.Equal((byte)0x00, fixture.Chip.OamAddress);
        Assert.Equal((byte)5, fixture.Chip.FineX);
        Assert.False(fixture.Chip.WriteToggle);
        Assert.NotEqual((ushort)0, fixture.Chip.TemporaryVramAddress);
    }

    [Fact]
    public void Ppudata_write_is_emitted_as_a_multiplexed_external_vram_bus_transaction()
    {
        var fixture = new Fixture();
        fixture.WriteRegister(6, 0x23);
        fixture.WriteRegister(6, 0x45);

        fixture.WriteRegister(7, 0xA6);

        Assert.True(fixture.Chip.VramTransactionActive);
        Assert.Equal(DigitalLevel.High, fixture.Chip.AddressLatchEnable.DriveLevel);
        Assert.True(fixture.Chip.MultiplexedAddressData.TrySample(out var lowAddress));
        Assert.Equal(0x45UL, lowAddress);
        Assert.True(fixture.Chip.HighAddress.TrySample(out var highAddress));
        Assert.Equal(0x23UL, highAddress);

        fixture.PulseClock(1);

        Assert.Equal(DigitalLevel.Low, fixture.Chip.AddressLatchEnable.DriveLevel);
        Assert.Equal(DigitalLevel.Low, fixture.Chip.VramWriteBar.DriveLevel);
        Assert.True(fixture.Chip.MultiplexedAddressData.TrySample(out var writeData));
        Assert.Equal(0xA6UL, writeData);

        fixture.PulseClock(2);

        Assert.False(fixture.Chip.VramTransactionActive);
        Assert.Equal(1UL, fixture.Chip.CompletedVramWriteCount);
        Assert.Equal((ushort)0x2346, fixture.Chip.VramAddress);
    }

    [Fact]
    public void Ppudata_read_returns_the_old_buffer_then_refills_it_from_external_pins()
    {
        var fixture = new Fixture();
        fixture.WriteRegister(6, 0x20);
        fixture.WriteRegister(6, 0x00);

        var first = fixture.ReadRegister(7);
        Assert.Equal((byte)0x00, first);

        fixture.PulseClock(1);
        fixture.DriveExternalVramData(0x6C);
        fixture.PulseClock(2);
        fixture.ReleaseExternalVramData();

        Assert.Equal(1UL, fixture.Chip.CompletedVramReadCount);
        Assert.Equal((byte)0x6C, fixture.Chip.ReadBuffer);
        Assert.Equal((byte)0x6C, fixture.ReadRegister(7));
    }

    private sealed class Fixture
    {
        private readonly VirtualHardwareSimulator _sim;
        private readonly DigitalSignalSource[] _data;
        private readonly DigitalSignalSource[] _rs;
        private readonly DigitalSignalSource[] _externalAd;
        private readonly DigitalSignalSource _clock;
        private readonly DigitalSignalSource _cs;
        private readonly DigitalSignalSource _rw;

        public Fixture()
        {
            var board = new VirtualHardwareBoard("rp2c02-standalone");
            Chip = board.Add(new Rp2C02("U1"));
            var vcc = board.Add(new DigitalSignalSource("vcc", DigitalLevel.High));
            var gnd = board.Add(new DigitalSignalSource("gnd", DigitalLevel.Low));
            var reset = board.Add(new DigitalSignalSource("reset", DigitalLevel.High));
            _clock = board.Add(new DigitalSignalSource("clock", DigitalLevel.Low));
            _cs = board.Add(new DigitalSignalSource("cs", DigitalLevel.High));
            _rw = board.Add(new DigitalSignalSource("rw", DigitalLevel.High));
            _data = Sources(board, "d", 8, DigitalLevel.HighImpedance);
            _rs = Sources(board, "rs", 3, DigitalLevel.Low);
            _externalAd = Sources(board, "vram-d", 8, DigitalLevel.HighImpedance);

            board.Connect("VCC", vcc.Output, Chip.Vcc);
            board.Connect("GND", gnd.Output, Chip.Gnd);
            board.Connect("/RES", reset.Output, Chip.ResetBar);
            board.Connect("CLK", _clock.Output, Chip.Clock);
            board.Connect("/CS", _cs.Output, Chip.ChipSelectBar);
            board.Connect("R/W", _rw.Output, Chip.CpuReadWrite);
            Connect(board, Chip.CpuData, _data, "D");
            Connect(board, Chip.RegisterSelect, _rs, "RS");
            board.Connect("/NMI", Chip.NmiBar);
            for (var bit = 0; bit < 8; bit++)
            {
                board.Connect($"AD{bit}", Chip.MultiplexedAddressData.Pins[bit], _externalAd[bit].Output);
            }
            for (var bit = 0; bit < 6; bit++) board.Connect($"A{bit + 8}", Chip.HighAddress.Pins[bit]);
            for (var bit = 0; bit < 4; bit++) board.Connect($"EXT{bit}", Chip.Extension.Pins[bit]);
            board.Connect("ALE", Chip.AddressLatchEnable);
            board.Connect("/RD", Chip.VramReadBar);
            board.Connect("/WR", Chip.VramWriteBar);

            _sim = new VirtualHardwareSimulator(board);
            board.PowerOn();
            _sim.Settle();
        }

        public Rp2C02 Chip { get; }

        public void PulseClock(int count)
        {
            for (var cycle = 0; cycle < count; cycle++)
            {
                _clock.Set(DigitalLevel.High); _sim.Settle();
                _clock.Set(DigitalLevel.Low); _sim.Settle();
            }
        }

        public void WriteRegister(byte register, byte value)
        {
            Set(_rs, register);
            Set(_data, value);
            _rw.Set(DigitalLevel.Low);
            _cs.Set(DigitalLevel.Low); _sim.Settle();
            _cs.Set(DigitalLevel.High); _sim.Settle();
            _rw.Set(DigitalLevel.High);
            Release(_data);
            _sim.Settle();
        }

        public byte ReadRegister(byte register)
        {
            Set(_rs, register);
            Release(_data);
            _rw.Set(DigitalLevel.High);
            _cs.Set(DigitalLevel.Low); _sim.Settle();
            Assert.True(Chip.CpuData.TrySample(out var value));
            _cs.Set(DigitalLevel.High); _sim.Settle();
            return (byte)value;
        }

        public void DriveExternalVramData(byte value)
        {
            Set(_externalAd, value);
            _sim.Settle();
        }

        public void ReleaseExternalVramData()
        {
            Release(_externalAd);
            _sim.Settle();
        }

        private static DigitalSignalSource[] Sources(
            VirtualHardwareBoard board,
            string prefix,
            int count,
            DigitalLevel initialLevel)
        {
            var result = new DigitalSignalSource[count];
            for (var bit = 0; bit < count; bit++) result[bit] = board.Add(new DigitalSignalSource($"{prefix}{bit}", initialLevel));
            return result;
        }

        private static void Connect(VirtualHardwareBoard board, DigitalBus bus, DigitalSignalSource[] sources, string prefix)
        {
            for (var bit = 0; bit < sources.Length; bit++) board.Connect($"{prefix}{bit}", sources[bit].Output, bus.Pins[bit]);
        }

        private static void Set(DigitalSignalSource[] sources, ulong value)
        {
            for (var bit = 0; bit < sources.Length; bit++) sources[bit].Set((value & (1UL << bit)) != 0 ? DigitalLevel.High : DigitalLevel.Low);
        }

        private static void Release(DigitalSignalSource[] sources)
        {
            foreach (var source in sources) source.Set(DigitalLevel.HighImpedance);
        }
    }
}
