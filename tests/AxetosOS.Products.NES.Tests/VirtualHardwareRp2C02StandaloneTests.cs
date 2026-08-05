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


    [Fact]
    public void Background_pipeline_fetches_tile_data_through_external_vram_pins()
    {
        var fixture = new Fixture();
        fixture.WriteRegister(1, 0x08); // enable background rendering

        var addresses = fixture.PulseClockWithVram(40, address => address switch
        {
            >= 0x2000 and <= 0x23BF => 0x12,
            >= 0x23C0 and <= 0x23FF => 0x03,
            0x0120 => 0xAA,
            0x0128 => 0x55,
            _ => 0x00
        });

        Assert.Contains((ushort)0x2000, addresses);
        Assert.Contains((ushort)0x23C0, addresses);
        Assert.Contains((ushort)0x0120, addresses);
        Assert.Contains((ushort)0x0128, addresses);
        Assert.True(fixture.Chip.BackgroundNametableFetchCount >= 4);
        Assert.True(fixture.Chip.BackgroundAttributeFetchCount >= 4);
        Assert.True(fixture.Chip.BackgroundPatternFetchCount >= 8);
        Assert.Equal((byte)0x12, fixture.Chip.NextTileId);
        Assert.Equal((byte)0x03, fixture.Chip.NextTileAttribute);
        Assert.NotEqual((ushort)0, (ushort)(fixture.Chip.PatternShiftLow | fixture.Chip.PatternShiftHigh));
    }

    [Fact]
    public void Sprite_pipeline_evaluates_fetches_and_composes_a_visible_sprite_through_vram_pins()
    {
        var fixture = new Fixture();
        fixture.WriteRegister(3, 0x00);
        for (var sprite = 0; sprite < 64; sprite++)
        {
            fixture.WriteRegister(4, sprite == 0 ? (byte)0x00 : (byte)0xF0);
            fixture.WriteRegister(4, 0x02);
            fixture.WriteRegister(4, sprite == 0 ? (byte)0x01 : (byte)0x00);
            fixture.WriteRegister(4, 0x00);
        }
        fixture.WriteRegister(1, 0x14); // sprites enabled, including the leftmost eight pixels

        var addresses = fixture.PulseClockWithVram(342, address => address switch
        {
            0x0020 => 0x80,
            0x0028 => 0x00,
            _ => 0x00
        });

        Assert.Contains((ushort)0x0020, addresses);
        Assert.Contains((ushort)0x0028, addresses);
        Assert.True(fixture.Chip.SpriteEvaluationCount >= 64);
        Assert.Equal(1, fixture.Chip.EvaluatedSpriteCount);
        Assert.Equal(2UL, fixture.Chip.SpritePatternFetchCount);
        Assert.Equal((byte)0x15, fixture.Chip.SpritePixelIndex);
        Assert.Equal((byte)0x15, fixture.Chip.PixelPaletteIndex);
    }

    [Fact]
    public void Sprite_evaluation_limits_secondary_oam_to_eight_entries_and_sets_overflow()
    {
        var fixture = new Fixture();
        fixture.WriteRegister(3, 0x00);
        for (var sprite = 0; sprite < 64; sprite++)
        {
            fixture.WriteRegister(4, sprite < 9 ? (byte)0x00 : (byte)0xF0);
            fixture.WriteRegister(4, (byte)sprite);
            fixture.WriteRegister(4, 0x00);
            fixture.WriteRegister(4, (byte)(sprite * 2));
        }
        fixture.WriteRegister(1, 0x10);

        fixture.PulseClock(257);

        Assert.Equal(8, fixture.Chip.EvaluatedSpriteCount);
        Assert.True(fixture.Chip.SpriteOverflow);
    }

    [Fact]
    public void Palette_ram_is_internal_mirrored_and_masks_color_values()
    {
        var fixture = new Fixture();
        fixture.WriteRegister(6, 0x3F);
        fixture.WriteRegister(6, 0x10);
        fixture.WriteRegister(7, 0xFF);

        Assert.Equal((byte)0x3F, fixture.Chip.InspectPalette(0x3F00));
        Assert.Equal((byte)0x3F, fixture.Chip.InspectPalette(0x3F10));
        Assert.False(fixture.Chip.VramTransactionActive);
    }

    [Fact]
    public void Palette_ppudata_read_bypasses_the_delayed_read_buffer()
    {
        var fixture = new Fixture();
        fixture.WriteRegister(6, 0x3F);
        fixture.WriteRegister(6, 0x04);
        fixture.WriteRegister(7, 0x2A);
        fixture.WriteRegister(6, 0x3F);
        fixture.WriteRegister(6, 0x04);

        Assert.Equal((byte)0x2A, fixture.ReadRegister(7));
        Assert.True(fixture.Chip.VramTransactionActive);
    }


    [Fact]
    public void Odd_rendering_frame_skips_the_final_pre_render_cycle()
    {
        var fixture = new Fixture();
        fixture.WriteRegister(1, 0x08);

        fixture.PulseClock(341 * 262);
        Assert.Equal(1UL, fixture.Chip.Frame);
        Assert.Equal(0, fixture.Chip.Scanline);
        Assert.Equal(0, fixture.Chip.Dot);

        fixture.PulseClock((341 * 262) - 1);

        Assert.Equal(2UL, fixture.Chip.Frame);
        Assert.Equal(0, fixture.Chip.Scanline);
        Assert.Equal(0, fixture.Chip.Dot);
    }

    [Fact]
    public void Enabling_nmi_during_vblank_asserts_the_open_drain_pin_once()
    {
        var fixture = new Fixture();
        fixture.PulseClock((241 * 341) + 1);

        Assert.True(fixture.Chip.Vblank);
        Assert.Equal(0UL, fixture.Chip.NmiFallingEdgeCount);

        fixture.WriteRegister(0, 0x80);

        Assert.Equal(DigitalLevel.Low, fixture.Chip.NmiBar.DriveLevel);
        Assert.Equal(1UL, fixture.Chip.NmiFallingEdgeCount);
        fixture.PulseClock(8);
        Assert.Equal(1UL, fixture.Chip.NmiFallingEdgeCount);
    }


    [Fact]
    public void Ppustatus_read_spanning_vblank_start_suppresses_flag_and_nmi_edge()
    {
        var fixture = new Fixture();
        fixture.WriteRegister(0, 0x80);
        fixture.PulseClock(241 * 341);

        Assert.Equal(241, fixture.Chip.Scanline);
        Assert.Equal(0, fixture.Chip.Dot);

        fixture.BeginRegisterRead(2);
        fixture.PulseClock(1);
        fixture.EndRegisterRead();

        Assert.Equal(241, fixture.Chip.Scanline);
        Assert.Equal(1, fixture.Chip.Dot);
        Assert.False(fixture.Chip.Vblank);
        Assert.Equal(DigitalLevel.HighImpedance, fixture.Chip.NmiBar.DriveLevel);
        Assert.Equal(0UL, fixture.Chip.NmiFallingEdgeCount);
        Assert.Equal(1UL, fixture.Chip.VblankSuppressionCount);
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

        public IReadOnlyList<ushort> PulseClockWithVram(int count, Func<ushort, byte> readMemory)
        {
            var addresses = new List<ushort>();
            ushort latchedAddress = 0;

            for (var cycle = 0; cycle < count; cycle++)
            {
                _clock.Set(DigitalLevel.High);
                _sim.Settle();

                if (Chip.AddressLatchEnable.DriveLevel == DigitalLevel.High)
                {
                    Release(_externalAd);
                    _sim.Settle();
                    Assert.True(Chip.MultiplexedAddressData.TrySample(out var low));
                    Assert.True(Chip.HighAddress.TrySample(out var high));
                    latchedAddress = (ushort)(((high & 0x3F) << 8) | low);
                    addresses.Add(latchedAddress);
                }

                if (Chip.VramReadBar.DriveLevel == DigitalLevel.Low)
                {
                    Set(_externalAd, readMemory(latchedAddress));
                    _sim.Settle();
                }

                _clock.Set(DigitalLevel.Low);
                _sim.Settle();
            }

            Release(_externalAd);
            _sim.Settle();
            return addresses;
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
            BeginRegisterRead(register);
            Assert.True(Chip.CpuData.TrySample(out var value));
            EndRegisterRead();
            return (byte)value;
        }

        public void BeginRegisterRead(byte register)
        {
            Set(_rs, register);
            Release(_data);
            _rw.Set(DigitalLevel.High);
            _cs.Set(DigitalLevel.Low);
            _sim.Settle();
        }

        public void EndRegisterRead()
        {
            _cs.Set(DigitalLevel.High);
            _sim.Settle();
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
