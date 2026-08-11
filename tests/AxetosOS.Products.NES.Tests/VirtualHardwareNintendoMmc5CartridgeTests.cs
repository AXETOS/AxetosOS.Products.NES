using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Machines.Nes;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareNintendoMmc5CartridgeTests
{
    [Fact]
    public void Power_on_uses_8k_prg_mode_and_fixed_last_rom_bank()
    {
        var cartridge = CreateCartridge(64, 256, 32 * 1024);
        Assert.Equal((byte)3, cartridge.PrgMode);
        Assert.Equal((byte)0xFF, cartridge.PrgBankRegisters[4]);
        Assert.Equal(63, cartridge.PrgWindowBanks[3]);
        Assert.False(cartridge.PrgWindowUsesRam[3]);
        Assert.Equal((byte)0x7F, CpuHighTarget(cartridge).Read!(0x6000));
    }

    [Fact]
    public void Prg_mode_zero_selects_one_32k_rom_region_from_5117()
    {
        var cartridge = CreateCartridge(64, 256, 32 * 1024);
        var low = CpuLowTarget(cartridge);
        WriteLow(low, 0x5100, 0x00);
        WriteLow(low, 0x5117, 0x8F);
        Assert.Equal(new[] { 12, 13, 14, 15 }, cartridge.PrgWindowBanks.ToArray());
        Assert.All(cartridge.PrgWindowUsesRam, Assert.False);
    }

    [Fact]
    public void Prg_mode_one_selects_16k_ram_or_rom_then_fixed_16k_rom()
    {
        var cartridge = CreateCartridge(64, 256, 32 * 1024);
        var low = CpuLowTarget(cartridge);
        WriteLow(low, 0x5100, 0x01);
        WriteLow(low, 0x5115, 0x02);
        WriteLow(low, 0x5117, 0x9F);
        Assert.Equal(new[] { 2, 3, 30, 31 }, cartridge.PrgWindowBanks.ToArray());
        Assert.True(cartridge.PrgWindowUsesRam[0]);
        Assert.True(cartridge.PrgWindowUsesRam[1]);
        Assert.False(cartridge.PrgWindowUsesRam[2]);
        Assert.False(cartridge.PrgWindowUsesRam[3]);
    }

    [Fact]
    public void Prg_mode_two_selects_16k_plus_8k_plus_fixed_8k()
    {
        var cartridge = CreateCartridge(64, 256, 32 * 1024);
        var low = CpuLowTarget(cartridge);
        WriteLow(low, 0x5100, 0x02);
        WriteLow(low, 0x5115, 0x88);
        WriteLow(low, 0x5116, 0x8B);
        WriteLow(low, 0x5117, 0xBF);
        Assert.Equal(new[] { 8, 9, 11, 63 }, cartridge.PrgWindowBanks.ToArray());
        Assert.All(cartridge.PrgWindowUsesRam, Assert.False);
    }

    [Fact]
    public void Prg_mode_three_selects_three_independent_8k_windows_and_fixed_last()
    {
        var cartridge = CreateCartridge(64, 256, 32 * 1024);
        var low = CpuLowTarget(cartridge);
        WriteLow(low, 0x5114, 0x85);
        WriteLow(low, 0x5115, 0x86);
        WriteLow(low, 0x5116, 0x87);
        WriteLow(low, 0x5117, 0xBF);
        Assert.Equal(new[] { 5, 6, 7, 63 }, cartridge.PrgWindowBanks.ToArray());
        Assert.All(cartridge.PrgWindowUsesRam, Assert.False);
    }

    [Fact]
    public void Prg_ram_writes_require_the_two_hardware_protect_keys()
    {
        var cartridge = CreateCartridge(64, 256, 32 * 1024);
        var low = CpuLowTarget(cartridge);
        var ram = Cpu6000Target(cartridge);
        WriteLow(low, 0x5113, 0x00);
        ram.Write!(0x6000, 0x11);
        Assert.Equal((byte)0x00, cartridge.InspectWorkRamByte(0));
        WriteLow(low, 0x5102, 0x02);
        WriteLow(low, 0x5103, 0x01);
        ram.Write!(0x6000, 0x5A);
        Assert.True(cartridge.PrgRamWriteEnabled);
        Assert.Equal((byte)0x5A, cartridge.InspectWorkRamByte(0));

        var wideRam = CreateCartridge(64, 256, 128 * 1024);
        var wideLow = CpuLowTarget(wideRam);
        var wideWindow = Cpu6000Target(wideRam);
        WriteLow(wideLow, 0x5102, 0x02);
        WriteLow(wideLow, 0x5103, 0x01);
        WriteLow(wideLow, 0x5113, 0x0F);
        wideWindow.Write!(0x6000, 0xA6);
        Assert.Equal((byte)0xA6, wideRam.InspectWorkRamByte(15 * 8 * 1024));
    }

    [Fact]
    public void One_8k_ram_socket_mirrors_banks_zero_to_three_and_leaves_four_to_seven_open()
    {
        var cartridge = CreateCartridge(64, 256, 8 * 1024);
        var low = CpuLowTarget(cartridge);
        var ram = Cpu6000Target(cartridge);
        WriteLow(low, 0x5113, 0x03);
        Assert.True(ram.IsSelected!(0x6000, false));
        WriteLow(low, 0x5113, 0x04);
        Assert.False(ram.IsSelected!(0x6000, false));
    }

    [Fact]
    public void Chr_mode_zero_uses_one_8k_bank_from_register_5127()
    {
        var cartridge = CreateCartridge(64, 256);
        var low = CpuLowTarget(cartridge);
        WriteLow(low, 0x5101, 0x00);
        WriteLow(low, 0x5127, 0x03);
        Assert.Equal(24, cartridge.ResolveCurrentChrBank(0));
        Assert.Equal(31, cartridge.ResolveCurrentChrBank(7));
    }

    [Fact]
    public void Chr_mode_one_uses_two_4k_regions_from_5123_and_5127()
    {
        var cartridge = CreateCartridge(64, 256);
        var low = CpuLowTarget(cartridge);
        WriteLow(low, 0x5101, 0x01);
        WriteLow(low, 0x5123, 0x04);
        WriteLow(low, 0x5127, 0x06);
        Assert.Equal(new[] { 16, 17, 18, 19, 24, 25, 26, 27 }, CurrentChr(cartridge));
    }

    [Fact]
    public void Chr_mode_two_uses_four_2k_regions_from_odd_a_registers()
    {
        var cartridge = CreateCartridge(64, 256);
        var low = CpuLowTarget(cartridge);
        WriteLow(low, 0x5101, 0x02);
        WriteLow(low, 0x5121, 0x01);
        WriteLow(low, 0x5123, 0x02);
        WriteLow(low, 0x5125, 0x03);
        WriteLow(low, 0x5127, 0x04);
        Assert.Equal(new[] { 2, 3, 4, 5, 6, 7, 8, 9 }, CurrentChr(cartridge));
    }

    [Fact]
    public void Chr_mode_three_uses_eight_independent_1k_a_registers()
    {
        var cartridge = CreateCartridge(64, 256);
        var low = CpuLowTarget(cartridge);
        WriteLow(low, 0x5101, 0x03);
        for (var i = 0; i < 8; i++) WriteLow(low, (ushort)(0x5120 + i), (byte)(0x20 + i));
        Assert.Equal(Enumerable.Range(0x20, 8).ToArray(), CurrentChr(cartridge));
    }

    [Fact]
    public void Large_sprite_mode_can_select_background_b_chr_register_set()
    {
        var cartridge = CreateCartridge(64, 256);
        var low = CpuLowTarget(cartridge);
        WriteLow(low, 0x5101, 0x03);
        PpuControlTarget(cartridge).Write!(0x2000, 0x20);
        WriteLow(low, 0x5128, 0x30);
        WriteLow(low, 0x5129, 0x31);
        WriteLow(low, 0x512A, 0x32);
        WriteLow(low, 0x512B, 0x33);
        Assert.Equal(new[] { 0x30, 0x31, 0x32, 0x33, 0x30, 0x31, 0x32, 0x33 }, CurrentChr(cartridge));
    }

    [Fact]
    public void Returning_to_8x8_sprites_restores_a_chr_set_selection()
    {
        var cartridge = CreateCartridge(64, 256);
        var low = CpuLowTarget(cartridge);
        WriteLow(low, 0x5101, 0x03);
        WriteLow(low, 0x5120, 0x11);
        PpuControlTarget(cartridge).Write!(0x2000, 0x20);
        WriteLow(low, 0x5128, 0x30);
        Assert.Equal(0x30, cartridge.ResolveCurrentChrBank(0));
        PpuControlTarget(cartridge).Write!(0x2000, 0x00);
        Assert.Equal(0x11, cartridge.ResolveCurrentChrBank(0));
    }

    [Fact]
    public void Large_sprite_chr_set_selection_is_latched_across_scanline_tile_counter_reset()
    {
        var cartridge = CreateCartridge(64, 256);
        var low = CpuLowTarget(cartridge);
        var observer = PpuObserverTarget(cartridge);
        var pattern = PpuPatternTarget(cartridge);

        WriteLow(low, 0x5101, 0x03);
        for (var i = 0; i < 8; i++) WriteLow(low, (ushort)(0x5120 + i), (byte)(0x11 + i));
        PpuControlTarget(cartridge).Write!(0x2000, 0x20);
        for (var i = 0; i < 4; i++) WriteLow(low, (ushort)(0x5128 + i), (byte)(0x30 + i));

        EnterFrame(observer);
        Assert.False(cartridge.ActiveChrSetA);

        // Advance the MMC5's physical nametable-fetch detector into the sprite
        // CHR-A window. Use distinct addresses so scanline detection does not
        // reset the tile counter while reaching that window.
        for (var i = 1; i <= 31; i++) observer.ObserveReadBegin!(0x2000 + i);
        Assert.True(cartridge.ActiveChrSetA);

        // Three identical nametable reads reset the scanline tile counter, but
        // the CHR set chosen before that reset remains latched until the next
        // nametable fetch. This is the timing Castlevania III relies on for its
        // 8x16 sprite fetch interval.
        observer.ObserveReadBegin!(0x2200);
        observer.ObserveReadBegin!(0x2200);
        observer.ObserveReadBegin!(0x2200);
        Assert.True(cartridge.ActiveChrSetA);
        Assert.Equal(0x11, cartridge.ResolveCurrentChrBank(0));
        Assert.Equal((byte)0x91, pattern.Read!(0x0000));
        Assert.Equal(1UL, cartridge.ChrSetAReadCount);

        observer.ObserveReadBegin!(0x2201);
        Assert.False(cartridge.ActiveChrSetA);
        Assert.Equal(0x30, cartridge.ResolveCurrentChrBank(0));
        Assert.Equal((byte)0xB0, pattern.Read!(0x0000));
        Assert.Equal(1UL, cartridge.ChrSetBReadCount);
        Assert.True(cartridge.ChrSetSwitchCount >= 2);
    }

    [Fact]
    public void Large_sprite_chr_a_window_covers_all_sixteen_sprite_fetch_nametable_cycles()
    {
        var cartridge = CreateCartridge(64, 256);
        var low = CpuLowTarget(cartridge);
        var observer = PpuObserverTarget(cartridge);
        var pattern = PpuPatternTarget(cartridge);

        WriteLow(low, 0x5101, 0x03);
        WriteLow(low, 0x5120, 0x11);
        PpuControlTarget(cartridge).Write!(0x2000, 0x20);
        WriteLow(low, 0x5128, 0x30);

        EnterFrame(observer);
        Assert.False(cartridge.ActiveChrSetA);

        // Background fetches occupy MMC5 tile counts 0-31.  The PPU then
        // performs sixteen nametable fetch events while fetching eight 8x16
        // sprites (two per sprite), so CHR set A must remain selected for the
        // complete 32-47 interval.
        for (var i = 1; i <= 31; i++) observer.ObserveReadBegin!((ushort)(0x2000 + i));
        Assert.True(cartridge.ActiveChrSetA);
        Assert.Equal((byte)0x91, pattern.Read!(0x0000));

        for (var i = 32; i <= 46; i++) observer.ObserveReadBegin!((ushort)(0x2000 + i));
        Assert.True(cartridge.ActiveChrSetA);
        Assert.Equal((byte)0x91, pattern.Read!(0x0000));

        observer.ObserveReadBegin!(0x202F);
        Assert.False(cartridge.ActiveChrSetA);
        Assert.Equal((byte)0xB0, pattern.Read!(0x0000));
    }

    [Fact]
    public void Chr_upper_bits_are_latched_into_subsequent_bank_register_writes()
    {
        var cartridge = CreateCartridge(64, 1024);
        var low = CpuLowTarget(cartridge);
        WriteLow(low, 0x5130, 0x02);
        WriteLow(low, 0x5120, 0x34);
        Assert.Equal((ushort)0x234, cartridge.ChrBankRegisters[0]);
    }

    [Fact]
    public void Nametable_mapping_can_independently_select_both_ciram_pages()
    {
        var cartridge = CreateCartridge(64, 256);
        var low = CpuLowTarget(cartridge);
        WriteLow(low, 0x5105, 0x44); // A, B, A, B => 00 01 00 01
        Assert.Equal(DigitalLevel.Low, EvaluateDirect(cartridge, cartridge.CiramA10, 0x2000));
        Assert.Equal(DigitalLevel.High, EvaluateDirect(cartridge, cartridge.CiramA10, 0x2400));
        Assert.Equal(DigitalLevel.Low, EvaluateDirect(cartridge, cartridge.CiramChipEnableBar, 0x2800));
    }

    [Fact]
    public void Nametable_source_two_uses_exram_in_modes_zero_and_one()
    {
        var cartridge = CreateCartridge(64, 256);
        var low = CpuLowTarget(cartridge);
        var nt = PpuNametableTarget(cartridge);
        WriteLow(low, 0x5104, 0x00);
        WriteLow(low, 0x5105, 0x02);
        Assert.True(nt.IsSelected!(0x2000, true));
        nt.Write!(0x2000, 0x5A);
        Assert.Equal((byte)0x5A, cartridge.InspectExRamByte(0));
        Assert.True(nt.IsSelected!(0x2000, false));
        Assert.Equal((byte)0x5A, nt.Read!(0x2000));
    }

    [Fact]
    public void Exram_mode_two_is_cpu_read_write_and_not_a_ppu_nametable_source()
    {
        var cartridge = CreateCartridge(64, 256);
        var low = CpuLowTarget(cartridge);
        var nt = PpuNametableTarget(cartridge);
        WriteLow(low, 0x5104, 0x02);
        low.Write!(0x5C00, 0x66);
        Assert.Equal((byte)0x66, cartridge.InspectExRamByte(0));
        Assert.True(low.IsSelected!(0x5C00, false));
        Assert.Equal((byte)0x66, low.Read!(0x5C00));
        WriteLow(low, 0x5105, 0x02);
        Assert.True(nt.IsSelected!(0x2000, false));
        Assert.Equal((byte)0x00, nt.Read!(0x2000));
    }

    [Fact]
    public void Exram_mode_three_is_cpu_read_only()
    {
        var cartridge = CreateCartridge(64, 256);
        var low = CpuLowTarget(cartridge);
        WriteLow(low, 0x5104, 0x02);
        low.Write!(0x5C12, 0x77);
        WriteLow(low, 0x5104, 0x03);
        Assert.True(low.IsSelected!(0x5C12, false));
        Assert.False(low.IsSelected!(0x5C12, true));
        Assert.Equal((byte)0x77, low.Read!(0x5C12));
    }

    [Fact]
    public void Fill_mode_drives_programmed_tile_for_nametable_bytes()
    {
        var cartridge = CreateCartridge(64, 256);
        var low = CpuLowTarget(cartridge);
        var nt = PpuNametableTarget(cartridge);
        WriteLow(low, 0x5105, 0x03);
        WriteLow(low, 0x5106, 0xA5);
        Assert.True(nt.IsSelected!(0x2000, false));
        Assert.Equal((byte)0xA5, nt.Read!(0x2000));
    }

    [Fact]
    public void Fill_mode_replicates_two_bit_color_for_attribute_bytes()
    {
        var cartridge = CreateCartridge(64, 256);
        var low = CpuLowTarget(cartridge);
        var nt = PpuNametableTarget(cartridge);
        WriteLow(low, 0x5105, 0x03);
        WriteLow(low, 0x5107, 0x02);
        Assert.Equal((byte)0xAA, nt.Read!(0x23C0));
    }

    [Fact]
    public void Extended_attribute_mode_replaces_attribute_palette_from_exram()
    {
        var cartridge = CreateCartridge(64, 256);
        var low = CpuLowTarget(cartridge);
        var observer = PpuObserverTarget(cartridge);
        var nt = PpuNametableTarget(cartridge);
        WriteLow(low, 0x5104, 0x02);
        low.Write!(0x5C00, 0xC3);
        WriteLow(low, 0x5104, 0x01);
        EnterFrame(observer);
        observer.ObserveReadBegin!(0x2000);
        observer.ObserveReadBegin!(0x23C0);
        Assert.True(nt.IsSelected!(0x23C0, false));
        Assert.Equal((byte)0xFF, nt.Read!(0x23C0));
        Assert.True(cartridge.ExtendedAttributeReadCount > 0);
    }

    [Fact]
    public void Extended_attribute_mode_redirects_following_background_chr_fetches()
    {
        var cartridge = CreateCartridge(64, 256);
        var low = CpuLowTarget(cartridge);
        var observer = PpuObserverTarget(cartridge);
        var pattern = PpuPatternTarget(cartridge);
        WriteLow(low, 0x5104, 0x02);
        low.Write!(0x5C00, 0x03);
        WriteLow(low, 0x5104, 0x01);
        EnterFrame(observer);
        observer.ObserveReadBegin!(0x2000);
        observer.ObserveReadBegin!(0x23C0);
        observer.ObserveReadBegin!(0x0000);
        Assert.Equal((byte)0x8C, pattern.Read!(0x0000));
        Assert.True(cartridge.ExtendedChrReadCount > 0);
    }

    [Fact]
    public void Vertical_split_can_replace_nametable_fetch_with_exram()
    {
        var cartridge = CreateCartridge(64, 256);
        var low = CpuLowTarget(cartridge);
        var observer = PpuObserverTarget(cartridge);
        var nt = PpuNametableTarget(cartridge);
        WriteLow(low, 0x5104, 0x02);
        low.Write!(0x5C04, 0x5A);
        WriteLow(low, 0x5104, 0x00);
        EnterFrame(observer);
        WriteLow(low, 0x5200, 0xC4); // enabled, right side, delimiter column 4
        observer.ObserveReadBegin!(0x2001);
        Assert.True(nt.IsSelected!(0x2001, false));
        Assert.Equal((byte)0x5A, nt.Read!(0x2001));
        Assert.True(cartridge.VerticalSplitReadCount > 0);
    }

    [Fact]
    public void Vertical_split_redirects_pattern_fetches_to_split_4k_bank()
    {
        var cartridge = CreateCartridge(64, 256);
        var low = CpuLowTarget(cartridge);
        var observer = PpuObserverTarget(cartridge);
        var pattern = PpuPatternTarget(cartridge);
        EnterFrame(observer);
        WriteLow(low, 0x5200, 0xC4);
        WriteLow(low, 0x5202, 0x02);
        observer.ObserveReadBegin!(0x2001);
        observer.ObserveReadBegin!(0x0000);
        Assert.Equal((byte)0x88, pattern.Read!(0x0000));
    }

    [Fact]
    public void Scanline_detector_sets_pending_and_open_drain_irq_at_programmed_target()
    {
        var cartridge = CreateCartridge(64, 256);
        var low = CpuLowTarget(cartridge);
        var observer = PpuObserverTarget(cartridge);
        WriteLow(low, 0x5203, 0x01);
        WriteLow(low, 0x5204, 0x80);
        EnterFrame(observer);
        SignalNextScanline(observer, 0x2400, 0x27C0);
        Assert.True(cartridge.IrqPending);
        Assert.True(cartridge.IrqAsserted);
        Assert.Equal((byte)1, cartridge.ScanlineCounter);
        Assert.Equal(1UL, cartridge.IrqAssertCount);
    }

    [Fact]
    public void Reading_5204_reports_in_frame_and_pending_then_clears_scanline_irq()
    {
        var cartridge = CreateCartridge(64, 256);
        var low = CpuLowTarget(cartridge);
        var observer = PpuObserverTarget(cartridge);
        WriteLow(low, 0x5203, 0x01);
        WriteLow(low, 0x5204, 0x80);
        EnterFrame(observer);
        SignalNextScanline(observer, 0x2400, 0x27C0);
        var status = low.Read!(0x5204);
        Assert.Equal(0xC0, status & 0xC0);
        Assert.False(cartridge.IrqPending);
    }

    [Fact]
    public void Reading_nmi_vector_resets_mmc5_in_frame_tracking_and_pending_irq()
    {
        var cartridge = CreateCartridge(64, 256);
        var observer = PpuObserverTarget(cartridge);
        EnterFrame(observer);
        Assert.True(cartridge.PpuInFrame);
        CpuHighTarget(cartridge).Read!(0x7FFA);
        Assert.False(cartridge.PpuInFrame);
        Assert.False(cartridge.IrqPending);
        Assert.Equal((byte)0, cartridge.ScanlineCounter);
    }

    [Fact]
    public void Hardware_multiplier_returns_low_and_high_product_bytes()
    {
        var cartridge = CreateCartridge(64, 256);
        var low = CpuLowTarget(cartridge);
        WriteLow(low, 0x5205, 13);
        WriteLow(low, 0x5206, 17);
        Assert.Equal((ushort)221, cartridge.MultiplierResult);
        Assert.Equal((byte)0xDD, low.Read!(0x5205));
        Assert.Equal((byte)0x00, low.Read!(0x5206));
    }

    [Fact]
    public void Pulse_channels_decode_control_period_length_and_status()
    {
        var audio = new NintendoMmc5Audio();
        audio.WriteRegister(0x5015, 0x01);
        audio.WriteRegister(0x5000, 0xDF);
        audio.WriteRegister(0x5002, 0x34);
        audio.WriteRegister(0x5003, 0x08);
        Assert.True(audio.Pulse1.Enabled);
        Assert.Equal((ushort)0x034, audio.Pulse1.Period);
        Assert.True(audio.Pulse1.LengthCounter > 0);
        Assert.Equal((byte)0, audio.Pulse1.DutyStep);
        Assert.Equal(0x01, audio.ReadRegister(0x5015) & 0x01);
        for (var i = 0; i < 108; i++) audio.ClockCpuCycle();
        Assert.NotEqual((byte)0, audio.Pulse1.DutyStep);
        audio.WriteRegister(0x5003, 0x08);
        Assert.Equal((byte)0, audio.Pulse1.DutyStep);
    }

    [Fact]
    public void Pulse_sweep_register_has_no_effect()
    {
        var audio = new NintendoMmc5Audio();
        audio.WriteRegister(0x5002, 0x55);
        audio.WriteRegister(0x5003, 0x02);
        var period = audio.Pulse1.Period;
        audio.WriteRegister(0x5001, 0xFF);
        Assert.Equal(period, audio.Pulse1.Period);
    }

    [Fact]
    public void Pulse_timers_run_on_every_second_cpu_cycle()
    {
        var audio = new NintendoMmc5Audio();
        for (var i = 0; i < 100; i++) audio.ClockCpuCycle();
        Assert.Equal(100UL, audio.CpuClockCount);
        Assert.Equal(50UL, audio.ApuHalfClockCount);
        Assert.Equal(50UL, audio.Pulse1.TimerClockCount);
    }

    [Fact]
    public void Mmc5_frame_clock_ticks_length_and_envelope_at_approximately_240hz()
    {
        var audio = new NintendoMmc5Audio();
        audio.WriteRegister(0x5015, 0x01);
        audio.WriteRegister(0x5000, 0x0F);
        audio.WriteRegister(0x5003, 0x08);
        for (var i = 0; i < 7457; i++) audio.ClockCpuCycle();
        Assert.Equal(1UL, audio.FrameClockCount);
        Assert.Equal(1UL, audio.Pulse1.LengthClockCount);
        Assert.Equal(1UL, audio.Pulse1.EnvelopeClockCount);
    }

    [Fact]
    public void Pcm_direct_mode_accepts_nonzero_5011_and_ignores_zero()
    {
        var audio = new NintendoMmc5Audio();
        audio.WriteRegister(0x5011, 0x66);
        audio.WriteRegister(0x5011, 0x00);
        Assert.Equal((byte)0x66, audio.PcmOutput);
    }

    [Fact]
    public void Pcm_read_mode_samples_cpu_reads_from_8000_to_bfff_and_zero_can_assert_irq()
    {
        var audio = new NintendoMmc5Audio();
        audio.WriteRegister(0x5010, 0x81);
        audio.ObserveCpuRead(0x8000, 0x55);
        Assert.Equal((byte)0x55, audio.PcmOutput);
        audio.ObserveCpuRead(0xBFFF, 0x00);
        Assert.True(audio.PcmIrqPending);
        Assert.Equal(2UL, audio.PcmReadSampleCount);
        Assert.Equal(1UL, audio.PcmIrqAssertCount);
    }

    [Fact]
    public void Reading_5010_reports_and_clears_pcm_irq()
    {
        var audio = new NintendoMmc5Audio();
        audio.WriteRegister(0x5010, 0x81);
        audio.ObserveCpuRead(0x8000, 0x00);
        Assert.Equal((byte)0x80, audio.ReadRegister(0x5010));
        Assert.False(audio.PcmIrqPending);
    }

    [Fact]
    public void Chr_ram_topology_writes_through_current_mmc5_bank_mapping()
    {
        var cartridge = CreateChrRamCartridge(64, 8 * 1024);
        var low = CpuLowTarget(cartridge);
        var pattern = PpuPatternTarget(cartridge);
        WriteLow(low, 0x5101, 0x03);
        WriteLow(low, 0x5120, 0x07);
        pattern.Write!(0x0000, 0x5A);
        Assert.True(cartridge.IsChrRam);
        Assert.Equal((byte)0x5A, cartridge.InspectChrByte(7 * 1024));
        Assert.Equal((byte)0x5A, pattern.Read!(0x0000));
        Assert.Equal(1UL, cartridge.PpuWriteCount);
    }

    [Fact]
    public void Compiled_mmc5_cpu_latches_writes_and_clocks_package_on_completion_edge()
    {
        var cartridge = CreateCartridge(64, 256);
        var high = CpuHighTarget(cartridge);
        Assert.Equal(CompiledBusWritePhase.Complete, high.WritePhase);
        Assert.Equal(CompiledBusCycleObservationPhase.Complete, high.ObserveBusCyclePhase);
        ClockCpu(high, 100);
        Assert.Equal(100UL, cartridge.CpuCycleClockCount);
        Assert.Equal(100UL, cartridge.Audio.CpuClockCount);
    }

    [Fact]
    public void Generic_compiled_and_raw_physical_mmc5_execute_same_banking_ram_audio_irq_and_ppu_program()
    {
        var image = CreateExecutionImage();
        var compiled = new RegionalNesVirtualMachine();
        var reference = new RegionalNesVirtualMachine();
        compiled.SetCompiledLabExecutionEnabled(true);
        compiled.InsertRom(image, "MMC5 synthetic (Japan).nes");
        reference.InsertRom(image, "MMC5 synthetic (Japan).nes");
        reference.Famicom.SetCompiledPhysicalMachineEnabled(false);
        compiled.PowerOn();
        reference.PowerOn();
        compiled.ReleaseReset();
        reference.ReleaseReset();

        const int masterCycles = 160_000;
        compiled.AdvanceMasterCycles(masterCycles);
        reference.AdvanceMasterCycles(masterCycles);

        var actual = Assert.IsType<NintendoMmc5Cartridge>(compiled.Slot.Cartridge);
        var expected = Assert.IsType<NintendoMmc5Cartridge>(reference.Slot.Cartridge);
        Assert.True(compiled.Famicom.CompiledLabMotherboardEnabled);
        Assert.Equal(expected.PrgMode, actual.PrgMode);
        Assert.Equal(expected.PrgBankRegisters.ToArray(), actual.PrgBankRegisters.ToArray());
        Assert.Equal(expected.PrgWindowBanks.ToArray(), actual.PrgWindowBanks.ToArray());
        Assert.Equal(expected.PrgWindowUsesRam.ToArray(), actual.PrgWindowUsesRam.ToArray());
        Assert.Equal(expected.ChrBankRegisters.ToArray(), actual.ChrBankRegisters.ToArray());
        Assert.Equal(expected.ActiveChrSetA, actual.ActiveChrSetA);
        Assert.Equal(expected.ChrSetAReadCount, actual.ChrSetAReadCount);
        Assert.Equal(expected.ChrSetBReadCount, actual.ChrSetBReadCount);
        Assert.Equal(expected.ChrSetSwitchCount, actual.ChrSetSwitchCount);
        Assert.Equal(expected.NametableMapping, actual.NametableMapping);
        Assert.Equal(expected.ExRamMode, actual.ExRamMode);
        Assert.Equal(expected.MultiplierResult, actual.MultiplierResult);
        Assert.Equal(expected.Audio.CpuClockCount, actual.Audio.CpuClockCount);
        Assert.Equal(expected.Audio.Pulse1.Period, actual.Audio.Pulse1.Period);
        Assert.Equal(expected.Audio.Pulse2.Period, actual.Audio.Pulse2.Period);
        Assert.Equal(expected.MapperWriteCount, actual.MapperWriteCount);
        Assert.Equal(expected.WorkRamWriteCount, actual.WorkRamWriteCount);
        Assert.Equal(expected.PpuBusReadObservationCount, actual.PpuBusReadObservationCount);
        Assert.Equal(reference.Famicom.Cpu.ProgramCounter, compiled.Famicom.Cpu.ProgramCounter);
        Assert.Equal(reference.Famicom.Cpu.CompletedInstructionCount, compiled.Famicom.Cpu.CompletedInstructionCount);
        Assert.Equal(reference.Famicom.Ppu.InspectDiagnosticState(), compiled.Famicom.Ppu.InspectDiagnosticState());
        Assert.Equal(reference.Famicom.Ciram.InspectStateHash(), compiled.Famicom.Ciram.InspectStateHash());
    }

    [Fact]
    public void Factory_constructs_mapper5_as_mmc5_hardware()
    {
        var cartridge = VirtualCartridgeHardwareFactory.Create(CreateImage(CreatePrg(64), CreateChr(256), 32 * 1024));
        var mmc5 = Assert.IsType<NintendoMmc5Cartridge>(cartridge);
        Assert.Equal(5, mmc5.MapperNumber);
    }

    [Fact]
    public void Invalid_four_screen_mixed_chr_bad_prg_oversized_chr_and_oversized_ram_are_rejected()
    {
        var basic = CreateImage(CreatePrg(64), CreateChr(256), 32 * 1024);
        var fourScreen = basic with { Mirroring = VirtualHardwareNesMirroring.FourScreen };
        var mixed = basic with { ChrRamSizeBytes = 8 * 1024 };
        var badPrg = basic with { PrgRom = new byte[6 * 8 * 1024], PrgRomSizeBytes = 6 * 8 * 1024 };
        var tooMuchChr = basic with { ChrRom = new byte[2 * 1024 * 1024], ChrRomSizeBytes = 2 * 1024 * 1024 };
        var badRam = basic with { PrgRamSizeBytes = 136 * 1024 };
        Assert.Throws<NotSupportedException>(() => new NintendoMmc5Cartridge("TEST.MMC5.4S").LoadImage(fourScreen));
        Assert.Throws<NotSupportedException>(() => new NintendoMmc5Cartridge("TEST.MMC5.MIX").LoadImage(mixed));
        Assert.Throws<NotSupportedException>(() => new NintendoMmc5Cartridge("TEST.MMC5.BADPRG").LoadImage(badPrg));
        Assert.Throws<NotSupportedException>(() => new NintendoMmc5Cartridge("TEST.MMC5.BIGCHR").LoadImage(tooMuchChr));
        Assert.Throws<NotSupportedException>(() => new NintendoMmc5Cartridge("TEST.MMC5.BIGRAM").LoadImage(badRam));
    }

    private static NintendoMmc5Cartridge CreateCartridge(int prgBanks, int chrBanks, int workRamBytes = 0)
    {
        var cartridge = new NintendoMmc5Cartridge("TEST.NINTENDO.MMC5");
        cartridge.LoadImage(CreateImage(CreatePrg(prgBanks), CreateChr(chrBanks), workRamBytes));
        return cartridge;
    }

    private static NintendoMmc5Cartridge CreateChrRamCartridge(int prgBanks, int chrRamBytes)
    {
        var cartridge = new NintendoMmc5Cartridge("TEST.NINTENDO.MMC5.CHRRAM");
        cartridge.LoadImage(new VirtualHardwareNesRomImage(
            VirtualHardwareNesHeaderFormat.Nes20, 5, 0, prgBanks * 8 * 1024, 0, false, false,
            VirtualHardwareNesMirroring.Vertical, VirtualHardwareNesHeaderTiming.Ntsc,
            CreatePrg(prgBanks), [])
        {
            PrgRamSizeBytes = 0,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = chrRamBytes,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        });
        return cartridge;
    }

    private static VirtualHardwareNesRomImage CreateImage(byte[] prg, byte[] chr, int workRamBytes)
    {
        return new VirtualHardwareNesRomImage(
            VirtualHardwareNesHeaderFormat.Nes20,
            5,
            0,
            prg.Length,
            chr.Length,
            false,
            false,
            VirtualHardwareNesMirroring.Vertical,
            VirtualHardwareNesHeaderTiming.Ntsc,
            prg,
            chr)
        {
            PrgRamSizeBytes = workRamBytes,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 0,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        };
    }

    private static byte[] CreatePrg(int banks)
    {
        var data = new byte[banks * 8 * 1024];
        for (var bank = 0; bank < banks; bank++)
            Array.Fill(data, (byte)(0x40 + (bank & 0x3F)), bank * 8 * 1024, 8 * 1024);
        return data;
    }

    private static byte[] CreateChr(int banks)
    {
        var data = new byte[banks * 1024];
        for (var bank = 0; bank < banks; bank++)
            Array.Fill(data, (byte)(0x80 + (bank & 0x7F)), bank * 1024, 1024);
        return data;
    }

    private static int[] CurrentChr(NintendoMmc5Cartridge cartridge) =>
        Enumerable.Range(0, 8).Select(cartridge.ResolveCurrentChrBank).ToArray();

    private static void EnterFrame(CompiledBusTargetDescriptor observer)
    {
        observer.ObserveReadBegin!(0x2000);
        observer.ObserveReadBegin!(0x2000);
        observer.ObserveReadBegin!(0x2000);
        observer.ObserveReadBegin!(0x23C0);
        observer.ObserveReadBegin!(0x2000);
    }

    private static void SignalNextScanline(CompiledBusTargetDescriptor observer, ushort nt, ushort attr)
    {
        observer.ObserveReadBegin!(nt);
        observer.ObserveReadBegin!(nt);
        observer.ObserveReadBegin!(nt);
        observer.ObserveReadBegin!(attr);
    }

    private static VirtualHardwareNesRomImage CreateExecutionImage()
    {
        var prg = CreatePrg(64);
        var program = new List<byte> { 0x78, 0xD8 };
        AddSta(program, 0x5100, 0x03);
        AddSta(program, 0x5101, 0x03);
        AddSta(program, 0x5102, 0x02);
        AddSta(program, 0x5103, 0x01);
        AddSta(program, 0x5113, 0x00);
        AddSta(program, 0x5114, 0x85);
        AddSta(program, 0x5115, 0x86);
        AddSta(program, 0x5116, 0x87);
        for (var i = 0; i < 8; i++) AddSta(program, (ushort)(0x5120 + i), (byte)(i + 1));
        AddSta(program, 0x5105, 0xE4);
        AddSta(program, 0x5106, 0x5A);
        AddSta(program, 0x5107, 0x02);
        AddSta(program, 0x6000, 0xA5);
        AddSta(program, 0x5203, 0x03);
        AddSta(program, 0x5204, 0x80);
        AddSta(program, 0x5205, 13);
        AddSta(program, 0x5206, 17);
        AddSta(program, 0x5015, 0x03);
        AddSta(program, 0x5000, 0xDF);
        AddSta(program, 0x5002, 0x20);
        AddSta(program, 0x5003, 0x08);
        AddSta(program, 0x5004, 0x9A);
        AddSta(program, 0x5006, 0x40);
        AddSta(program, 0x5007, 0x08);
        AddSta(program, 0x5011, 0x40);
        AddSta(program, 0x2000, 0x80);
        AddSta(program, 0x2001, 0x18);
        var loopAddress = (ushort)(0xE000 + program.Count);
        program.AddRange(new byte[] { 0x4C, (byte)loopAddress, (byte)(loopAddress >> 8) });

        var fixedLast = 63 * 8 * 1024;
        program.ToArray().CopyTo(prg, fixedLast);
        prg[fixedLast + 0x1FFA] = 0x00; prg[fixedLast + 0x1FFB] = 0xE0;
        prg[fixedLast + 0x1FFC] = 0x00; prg[fixedLast + 0x1FFD] = 0xE0;
        prg[fixedLast + 0x1FFE] = 0x00; prg[fixedLast + 0x1FFF] = 0xE0;
        return CreateImage(prg, CreateChr(256), 32 * 1024);
    }

    private static void AddSta(List<byte> program, ushort address, byte value) =>
        program.AddRange(new byte[] { 0xA9, value, 0x8D, (byte)address, (byte)(address >> 8) });

    private static void WriteLow(CompiledBusTargetDescriptor low, ushort address, byte value) => low.Write!(address, value);

    private static void ClockCpu(CompiledBusTargetDescriptor cpu, int cycles)
    {
        var observer = Assert.IsType<Action<bool>>(cpu.ObserveBusCycle);
        for (var i = 0; i < cycles; i++) observer(false);
    }

    private static CompiledBusTargetDescriptor CpuHighTarget(NintendoMmc5Cartridge cartridge) =>
        Assert.Single(Targets(cartridge), target => target.AddressPins.Count == 15 && target.ObserveBusCycle is not null);

    private static CompiledBusTargetDescriptor Cpu6000Target(NintendoMmc5Cartridge cartridge) =>
        Assert.Single(Targets(cartridge), target => target.AddressPins.Count == 15 && target.ObserveBusCycle is null &&
            target.Read is not null && target.IsSelected is not null && target.ReadConditions.Count >= 5);

    private static CompiledBusTargetDescriptor CpuLowTarget(NintendoMmc5Cartridge cartridge) =>
        Assert.Single(Targets(cartridge), target => target.AddressPins.Count == 15 && target.Read is not null &&
            target.IsSelected is not null && target.ReadConditions.Count == 3);

    private static CompiledBusTargetDescriptor PpuControlTarget(NintendoMmc5Cartridge cartridge) =>
        Assert.Single(Targets(cartridge), target => target.AddressPins.Count == 15 && target.Read is null &&
            target.Write is not null && target.IsSelected is not null);

    private static CompiledBusTargetDescriptor PpuObserverTarget(NintendoMmc5Cartridge cartridge) =>
        Assert.Single(Targets(cartridge), target => target.AddressPins.Count == 14 && target.ObserveReadBegin is not null);

    private static CompiledBusTargetDescriptor PpuPatternTarget(NintendoMmc5Cartridge cartridge) =>
        Assert.Single(Targets(cartridge), target => target.AddressPins.Count == 14 && target.Read is not null &&
            target.IsSelected is null);

    private static CompiledBusTargetDescriptor PpuNametableTarget(NintendoMmc5Cartridge cartridge) =>
        Assert.Single(Targets(cartridge), target => target.AddressPins.Count == 14 && target.Read is not null &&
            target.IsSelected is not null);

    private static CompiledBusTargetDescriptor[] Targets(NintendoMmc5Cartridge cartridge) =>
        ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets().ToArray();

    private static DigitalLevel EvaluateDirect(NintendoMmc5Cartridge cartridge, DigitalPin output, ushort address)
    {
        var direct = Assert.IsAssignableFrom<ICompiledBusAddressCombinationalComponent>(cartridge);
        Assert.True(direct.TryEvaluateCompiledBusAddressOutput(output, address, readCycle: true, out var drive));
        return drive.Level;
    }
}
