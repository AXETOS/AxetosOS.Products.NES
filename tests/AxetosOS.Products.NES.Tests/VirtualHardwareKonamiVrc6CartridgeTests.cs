using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Machines.Nes;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareKonamiVrc6CartridgeTests
{
    [Fact]
    public void Power_on_exposes_16k_switchable_8k_switchable_fixed_prg_and_eight_chr_windows()
    {
        var cartridge = CreateCartridge(24, 16, 64);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuPatternTarget(cartridge);

        Assert.Equal((byte)0x40, cpu.Read!(0x0000));
        Assert.Equal((byte)0x41, cpu.Read!(0x2000));
        Assert.Equal((byte)0x40, cpu.Read!(0x4000));
        Assert.Equal((byte)0x4F, cpu.Read!(0x6000));
        Assert.Equal(new[] { 0, 1, 0, 15 }, cartridge.PrgWindowBanks.ToArray());
        for (var slot = 0; slot < 8; slot++)
            Assert.Equal((byte)(0x80 + slot), ppu.Read!(slot * 0x400));

        var staticFacet = (ICompiledStaticCombinationalComponent)cartridge;
        Assert.True(staticFacet.TryEvaluateCompiledStaticOutput(
            cartridge.CiramChipEnableBar,
            pin => SamplePpuAddress(cartridge, pin, 0x0000),
            out var patternCiramEnable));
        Assert.Equal(DigitalLevel.High, patternCiramEnable.Level);
        Assert.False(staticFacet.TryEvaluateCompiledStaticOutput(
            cartridge.CiramChipEnableBar,
            pin => SamplePpuAddress(cartridge, pin, 0x2000),
            out _));

        var directFacet = Assert.IsAssignableFrom<ICompiledBusAddressCombinationalComponent>(cartridge);
        Assert.True(directFacet.TryEvaluateCompiledBusAddressOutput(
            cartridge.CiramChipEnableBar, 0x0000, readCycle: true, out var directPatternCe));
        Assert.Equal(DigitalLevel.High, directPatternCe.Level);
        Assert.True(directFacet.TryEvaluateCompiledBusAddressOutput(
            cartridge.CiramChipEnableBar, 0x2000, readCycle: true, out var directNametableCe));
        Assert.Equal(DigitalLevel.Low, directNametableCe.Level);
        Assert.Equal(0UL, cartridge.PpuWriteCount);
    }

    [Fact]
    public void Mapper24_programs_16k_and_8k_prg_outputs_directly()
    {
        var cartridge = CreateCartridge(24, 32, 64);
        var cpu = CpuRomTarget(cartridge);

        WriteHigh(cpu, 0x8000, 0x05);
        WriteHigh(cpu, 0xC000, 0x12);

        Assert.Equal((byte)0x05, cartridge.Prg16BankRegister);
        Assert.Equal((byte)0x12, cartridge.Prg8BankRegister);
        Assert.Equal(new[] { 10, 11, 18, 31 }, cartridge.PrgWindowBanks.ToArray());
        Assert.Equal((byte)0x4A, cpu.Read!(0x0000));
        Assert.Equal((byte)0x4B, cpu.Read!(0x2000));
        Assert.Equal((byte)0x52, cpu.Read!(0x4000));
        Assert.Equal((byte)0x5F, cpu.Read!(0x6000));
    }

    [Fact]
    public void Mapper26_swaps_physical_A0_A1_before_register_decode()
    {
        var cartridge = CreateCartridge(26, 16, 64);
        var cpu = CpuRomTarget(cartridge);

        WriteHigh(cpu, 0xD001, 0x12); // physical A0 -> logical A1 => D002
        WriteHigh(cpu, 0xD002, 0x34); // physical A1 -> logical A0 => D001

        Assert.Equal(KonamiVrc6Variant.Vrc6B, cartridge.Variant);
        Assert.Equal((byte)0x34, cartridge.ChrBankRegisters[1]);
        Assert.Equal((byte)0x12, cartridge.ChrBankRegisters[2]);
        Assert.Equal((ushort)0xD001, cartridge.LastTranslatedMapperWriteAddress);
    }

    [Fact]
    public void Mapper26_swaps_audio_and_irq_control_register_lines_as_part_of_the_package()
    {
        var cartridge = CreateCartridge(26, 16, 64);
        var cpu = CpuRomTarget(cartridge);

        WriteHigh(cpu, 0x9002, 0x34); // physical A1 -> logical 9001 frequency low
        WriteHigh(cpu, 0x9001, 0x80); // physical A0 -> logical 9002 enable/high period
        WriteHigh(cpu, 0xF000, 0xFE);
        WriteHigh(cpu, 0xF002, 0x06); // physical A1 -> logical F001 control
        ClockCpu(cpu, 2);

        Assert.Equal((ushort)0x034, cartridge.Audio.Pulse1.Frequency);
        Assert.True(cartridge.Audio.Pulse1.Enabled);
        Assert.True(cartridge.Irq.CycleMode);
        Assert.True(cartridge.Irq.Asserted);

        WriteHigh(cpu, 0xF001, 0x00); // physical A0 -> logical F002 acknowledge
        Assert.False(cartridge.Irq.Asserted);
    }

    [Fact]
    public void Mapper24_keeps_physical_A0_A1_order()
    {
        var cartridge = CreateCartridge(24, 16, 64);
        var cpu = CpuRomTarget(cartridge);

        WriteHigh(cpu, 0xD001, 0x12);
        WriteHigh(cpu, 0xD002, 0x34);

        Assert.Equal(KonamiVrc6Variant.Vrc6A, cartridge.Variant);
        Assert.Equal((byte)0x12, cartridge.ChrBankRegisters[1]);
        Assert.Equal((byte)0x34, cartridge.ChrBankRegisters[2]);
        Assert.Equal((ushort)0xD002, cartridge.LastTranslatedMapperWriteAddress);
    }

    [Fact]
    public void Chr_mode_zero_maps_all_eight_registers_as_independent_1k_pages()
    {
        var cartridge = CreateCartridge(24, 16, 128);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuPatternTarget(cartridge);

        for (var slot = 0; slot < 4; slot++) WriteHigh(cpu, (ushort)(0xD000 + slot), (byte)(0x20 + slot));
        for (var slot = 0; slot < 4; slot++) WriteHigh(cpu, (ushort)(0xE000 + slot), (byte)(0x24 + slot));
        WriteHigh(cpu, 0xB003, 0x00);

        Assert.Equal(Enumerable.Range(0x20, 8).ToArray(), cartridge.ChrWindowBanks.ToArray());
        for (var slot = 0; slot < 8; slot++)
            Assert.Equal((byte)(0x80 + 0x20 + slot), ppu.Read!(slot * 0x400));
    }

    [Theory]
    [InlineData(0x01, "8,8,9,9,10,10,11,11")]
    [InlineData(0x21, "8,9,8,9,10,11,10,11")]
    public void Chr_mode_one_groups_four_source_registers_into_2k_pairs(int bankingMode, string expectedText)
    {
        var cartridge = CreateCartridge(24, 16, 128);
        var cpu = CpuRomTarget(cartridge);
        WriteHigh(cpu, 0xD000, 8);
        WriteHigh(cpu, 0xD001, 9);
        WriteHigh(cpu, 0xD002, 10);
        WriteHigh(cpu, 0xD003, 11);
        WriteHigh(cpu, 0xB003, (byte)bankingMode);

        var expected = expectedText.Split(',').Select(int.Parse).ToArray();
        Assert.Equal(expected, cartridge.ChrWindowBanks.ToArray());
    }

    [Theory]
    [InlineData(0x22)]
    [InlineData(0x23)]
    public void Chr_modes_two_and_three_keep_first_four_1k_and_pair_last_four(int bankingMode)
    {
        var cartridge = CreateCartridge(24, 16, 128);
        var cpu = CpuRomTarget(cartridge);
        for (var slot = 0; slot < 4; slot++) WriteHigh(cpu, (ushort)(0xD000 + slot), (byte)(0x10 + slot));
        WriteHigh(cpu, 0xE000, 0x20);
        WriteHigh(cpu, 0xE001, 0x22);
        WriteHigh(cpu, 0xB003, (byte)bankingMode);

        Assert.Equal(new[] { 0x10, 0x11, 0x12, 0x13, 0x20, 0x21, 0x22, 0x23 }, cartridge.ChrWindowBanks.ToArray());
    }

    [Theory]
    [InlineData(0x20, "0,1,0,1")]
    [InlineData(0x23, "0,0,1,1")]
    [InlineData(0x28, "0,0,0,0")]
    [InlineData(0x2B, "1,1,1,1")]
    public void Ciram_special_modes_cover_vertical_horizontal_and_both_single_screen_routes(int mode, string expectedText)
    {
        var cartridge = CreateCartridge(24, 16, 128);
        var cpu = CpuRomTarget(cartridge);
        WriteHigh(cpu, 0xB003, (byte)mode);

        var expected = expectedText.Split(',').Select(int.Parse).ToArray();
        Assert.False(cartridge.NametablesUseChrRom);
        Assert.Equal(expected, cartridge.NametablePages.ToArray());
        var directFacet = Assert.IsAssignableFrom<ICompiledBusAddressCombinationalComponent>(cartridge);
        for (var page = 0; page < 4; page++)
        {
            var address = (ushort)(0x2000 + page * 0x400);
            var expectedLevel = expected[page] == 0 ? DigitalLevel.Low : DigitalLevel.High;
            Assert.Equal(expectedLevel, EvaluateOutput(cartridge, cartridge.CiramA10, address));
            Assert.True(directFacet.TryEvaluateCompiledBusAddressOutput(
                cartridge.CiramA10, address, readCycle: true, out var directA10));
            Assert.Equal(expectedLevel, directA10.Level);
        }
    }

    [Fact]
    public void Ciram_dynamic_mode_uses_chr_register_low_bits_as_nametable_page_routes()
    {
        var cartridge = CreateCartridge(24, 16, 128);
        var cpu = CpuRomTarget(cartridge);
        WriteHigh(cpu, 0xE000, 0x04);
        WriteHigh(cpu, 0xE001, 0x05);
        WriteHigh(cpu, 0xE002, 0x06);
        WriteHigh(cpu, 0xE003, 0x07);
        WriteHigh(cpu, 0xB003, 0x01);

        Assert.Equal(new[] { 0, 1, 0, 1 }, cartridge.NametablePages.ToArray());
    }

    [Fact]
    public void Chr_rom_nametable_mode_disables_ciram_and_drives_banked_chr_pages()
    {
        var cartridge = CreateCartridge(24, 16, 128);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuChrNametableTarget(cartridge);
        WriteHigh(cpu, 0xE002, 0x24);
        WriteHigh(cpu, 0xE003, 0x2A);
        WriteHigh(cpu, 0xB003, 0x30); // CHR nametables + special mode 0x20

        Assert.True(cartridge.NametablesUseChrRom);
        Assert.Equal(new[] { 0x24, 0x25, 0x2A, 0x2B }, cartridge.NametablePages.ToArray());
        var pattern = PpuPatternTarget(cartridge);
        Assert.Null(pattern.IsSelected);
        Assert.NotNull(ppu.IsSelected);
        Assert.True(ppu.IsSelected!(0x2000, false));
        Assert.Equal((byte)0xA4, ppu.Read!(0x2000));
        Assert.Equal((byte)0xA5, ppu.Read!(0x2400));
        Assert.Equal((byte)0xAA, ppu.Read!(0x2800));
        Assert.Equal((byte)0xAB, ppu.Read!(0x2C00));
        Assert.Equal(DigitalLevel.High, EvaluateOutput(cartridge, cartridge.CiramChipEnableBar, 0x2000));
        Assert.Equal(4UL, cartridge.ChrNametableReadCount);
    }

    [Fact]
    public void Work_ram_is_electrically_open_until_banking_mode_bit7_enables_it()
    {
        var cartridge = CreateCartridge(24, 16, 64, 8 * 1024);
        var high = CpuRomTarget(cartridge);
        var ram = CpuWorkRamTarget(cartridge);

        Assert.False(cartridge.WorkRamEnabled);
        Assert.False(ram.IsSelected!(0x6000, false));
        WriteHigh(high, 0xB003, 0x80);
        Assert.True(cartridge.WorkRamEnabled);
        Assert.True(ram.IsSelected!(0x6000, false));
        ram.Write!(0x6000, 0x5A);
        Assert.Equal((byte)0x5A, ram.Read!(0x6000));
        Assert.Equal((byte)0x5A, cartridge.InspectWorkRamByte(0));
        Assert.Equal(1UL, cartridge.WorkRamWriteCount);
        Assert.Equal(1UL, cartridge.WorkRamReadCount);
    }

    [Fact]
    public void Vrc6_uses_full_byte_irq_reload_register_and_shared_cycle_mode_counter()
    {
        var cartridge = CreateCartridge(24, 16, 64);
        var cpu = CpuRomTarget(cartridge);
        WriteHigh(cpu, 0xF000, 0xFE);
        WriteHigh(cpu, 0xF001, 0x06);

        ClockCpu(cpu, 2);

        Assert.Equal((byte)0xFE, cartridge.Irq.ReloadValue);
        Assert.Equal((byte)0xFE, cartridge.Irq.Counter);
        Assert.True(cartridge.Irq.CycleMode);
        Assert.True(cartridge.Irq.Asserted);
        Assert.Equal(341, cartridge.Irq.Prescaler);
        Assert.Equal(1UL, cartridge.Irq.AssertCount);
    }

    [Fact]
    public void Vrc6_irq_scanline_mode_uses_bounded_341_dot_prescaler()
    {
        var cartridge = CreateCartridge(24, 16, 64);
        var cpu = CpuRomTarget(cartridge);
        WriteHigh(cpu, 0xF000, 0xFE);
        WriteHigh(cpu, 0xF001, 0x02);

        ClockCpu(cpu, 114);

        Assert.False(cartridge.Irq.CycleMode);
        Assert.InRange(cartridge.Irq.Prescaler, 1, 341);
        Assert.Equal(1UL, cartridge.Irq.CounterClockCount);
    }

    [Fact]
    public void Irq_acknowledge_releases_open_drain_output_and_honors_enable_after_ack()
    {
        var cartridge = CreateCartridge(24, 16, 64);
        var cpu = CpuRomTarget(cartridge);
        WriteHigh(cpu, 0xF000, 0xFF);
        WriteHigh(cpu, 0xF001, 0x07);
        ClockCpu(cpu, 1);
        Assert.True(cartridge.Irq.Asserted);

        WriteHigh(cpu, 0xF002, 0x00);

        Assert.False(cartridge.Irq.Asserted);
        Assert.True(cartridge.Irq.Enabled);
        Assert.Equal(DigitalLevel.HighImpedance, EvaluateOutput(cartridge, cartridge.IrqBar, 0x0000));
    }

    [Fact]
    public void Pulse_channels_implement_volume_duty_period_enable_and_phase_steps()
    {
        var audio = new KonamiVrc6Audio();
        audio.WriteRegister(0x9000, 0x8F); // ignore duty, full volume
        audio.WriteRegister(0x9001, 0x00);
        audio.WriteRegister(0x9002, 0x80);

        audio.ClockCpuCycle();
        audio.ClockCpuCycle();

        Assert.True(audio.Pulse1.Enabled);
        Assert.True(audio.Pulse1.IgnoreDuty);
        Assert.Equal((byte)15, audio.Pulse1.OutputLevel);
        Assert.True(audio.Pulse1.TimerStepCount >= 1);
        Assert.Equal((byte)15, audio.MixedDacLevel);
    }

    [Fact]
    public void Saw_channel_runs_14_step_accumulator_and_outputs_high_five_bits()
    {
        var audio = new KonamiVrc6Audio();
        audio.WriteRegister(0xB000, 0x20);
        audio.WriteRegister(0xB001, 0x00);
        audio.WriteRegister(0xB002, 0x80);

        for (var cycle = 0; cycle < 8; cycle++) audio.ClockCpuCycle();

        Assert.True(audio.Saw.Enabled);
        Assert.True(audio.Saw.TimerStepCount >= 4);
        Assert.True(audio.Saw.AccumulatorStepCount >= 2);
        Assert.True(audio.Saw.OutputLevel > 0);
    }

    [Theory]
    [InlineData(0x00, false, 0)]
    [InlineData(0x01, true, 0)]
    [InlineData(0x02, false, 4)]
    [InlineData(0x04, false, 8)]
    [InlineData(0x06, false, 8)]
    public void Audio_global_control_sets_halt_and_frequency_shift(int value, bool halted, int shift)
    {
        var audio = new KonamiVrc6Audio();
        audio.WriteRegister(0x9003, (byte)value);

        Assert.Equal(halted, audio.Halted);
        Assert.Equal((byte)shift, audio.FrequencyShift);
        Assert.Equal((byte)shift, audio.Pulse1.FrequencyShift);
        Assert.Equal((byte)shift, audio.Pulse2.FrequencyShift);
        Assert.Equal((byte)shift, audio.Saw.FrequencyShift);
    }

    [Fact]
    public void Audio_halt_stops_channel_phase_but_cpu_clock_node_continues()
    {
        var audio = new KonamiVrc6Audio();
        audio.WriteRegister(0x9000, 0x8F);
        audio.WriteRegister(0x9001, 0x00);
        audio.WriteRegister(0x9002, 0x80);
        audio.ClockCpuCycle();
        var steps = audio.Pulse1.TimerStepCount;
        audio.WriteRegister(0x9003, 0x01);
        for (var cycle = 0; cycle < 20; cycle++) audio.ClockCpuCycle();

        Assert.Equal(steps, audio.Pulse1.TimerStepCount);
        Assert.Equal(21UL, audio.CpuClockCount);
    }

    [Fact]
    public void Compiled_mapper_latches_writes_and_clocks_irq_audio_on_completion_edge()
    {
        var cartridge = CreateCartridge(24, 16, 64);
        var cpu = CpuRomTarget(cartridge);

        Assert.Equal(CompiledBusWritePhase.Complete, cpu.WritePhase);
        Assert.Equal(CompiledBusCycleObservationPhase.Complete, cpu.ObserveBusCyclePhase);
        ClockCpu(cpu, 12);
        Assert.Equal(12UL, cartridge.Irq.CpuClockCount);
        Assert.Equal(12UL, cartridge.Audio.CpuClockCount);
    }

    [Fact]
    public void Generic_compiled_and_raw_physical_vrc6_execute_same_banking_ram_irq_and_audio_program()
    {
        var image = CreateExecutionImage();
        var compiled = new RegionalNesVirtualMachine();
        var reference = new RegionalNesVirtualMachine();

        compiled.SetCompiledLabExecutionEnabled(true);
        compiled.InsertRom(image, "VRC6 synthetic (Japan).nes");
        reference.InsertRom(image, "VRC6 synthetic (Japan).nes");
        reference.Famicom.SetCompiledPhysicalMachineEnabled(false);

        compiled.PowerOn();
        reference.PowerOn();
        compiled.ReleaseReset();
        reference.ReleaseReset();

        const int masterCycles = 60_000;
        compiled.AdvanceMasterCycles(masterCycles);
        reference.AdvanceMasterCycles(masterCycles);

        var actual = Assert.IsType<KonamiVrc6Cartridge>(compiled.Slot.Cartridge);
        var expected = Assert.IsType<KonamiVrc6Cartridge>(reference.Slot.Cartridge);
        Assert.True(compiled.Famicom.CompiledLabMotherboardEnabled);
        Assert.Equal((byte)0x05, actual.Prg16BankRegister);
        Assert.Equal((byte)0x07, actual.Prg8BankRegister);
        Assert.Equal(Enumerable.Range(1, 8).ToArray(), actual.ChrWindowBanks.ToArray());
        Assert.True(actual.WorkRamEnabled);
        Assert.Equal((byte)0x5A, actual.InspectWorkRamByte(0));
        Assert.True(actual.Irq.Asserted);
        Assert.True(actual.Audio.Pulse1.RegisterWriteCount > 0);
        Assert.True(actual.Audio.Pulse2.RegisterWriteCount > 0);
        Assert.True(actual.Audio.Saw.RegisterWriteCount > 0);
        Assert.Equal(expected.PrgWindowBanks.ToArray(), actual.PrgWindowBanks.ToArray());
        Assert.Equal(expected.ChrWindowBanks.ToArray(), actual.ChrWindowBanks.ToArray());
        Assert.Equal(expected.NametablePages.ToArray(), actual.NametablePages.ToArray());
        Assert.Equal(expected.Irq.Counter, actual.Irq.Counter);
        Assert.Equal(expected.Irq.Prescaler, actual.Irq.Prescaler);
        Assert.Equal(expected.Irq.Asserted, actual.Irq.Asserted);
        Assert.Equal(expected.Irq.CpuClockCount, actual.Irq.CpuClockCount);
        Assert.Equal(expected.Audio.CpuClockCount, actual.Audio.CpuClockCount);
        Assert.Equal(expected.Audio.MixedDacLevel, actual.Audio.MixedDacLevel);
        Assert.Equal(expected.Audio.Pulse1.Step, actual.Audio.Pulse1.Step);
        Assert.Equal(expected.Audio.Pulse2.Step, actual.Audio.Pulse2.Step);
        Assert.Equal(expected.Audio.Saw.Step, actual.Audio.Saw.Step);
        Assert.Equal(expected.MapperWriteCount, actual.MapperWriteCount);
        Assert.Equal(expected.WorkRamWriteCount, actual.WorkRamWriteCount);
        Assert.Equal(reference.Famicom.Cpu.ProgramCounter, compiled.Famicom.Cpu.ProgramCounter);
        Assert.Equal(reference.Famicom.Cpu.CompletedInstructionCount, compiled.Famicom.Cpu.CompletedInstructionCount);
        Assert.Equal(reference.Famicom.Ppu.InspectDiagnosticState(), compiled.Famicom.Ppu.InspectDiagnosticState());
        Assert.Equal(reference.Famicom.Ciram.InspectStateHash(), compiled.Famicom.Ciram.InspectStateHash());
    }

    [Theory]
    [InlineData(24, KonamiVrc6Variant.Vrc6A)]
    [InlineData(26, KonamiVrc6Variant.Vrc6B)]
    public void Factory_constructs_both_vrc6_mapper_variants(int mapper, KonamiVrc6Variant variant)
    {
        var image = CreateImage(mapper, CreatePrg(16), CreateChr(64), 0);
        var cartridge = VirtualCartridgeHardwareFactory.Create(image);

        var vrc6 = Assert.IsType<KonamiVrc6Cartridge>(cartridge);
        Assert.Equal(mapper, vrc6.MapperNumber);
        Assert.Equal(variant, vrc6.Variant);
    }

    [Fact]
    public void Invalid_four_screen_chr_ram_bad_prg_oversized_chr_and_oversized_work_ram_are_rejected()
    {
        var basic = CreateImage(24, CreatePrg(16), CreateChr(64), 0);
        var fourScreen = basic with { Mirroring = VirtualHardwareNesMirroring.FourScreen };
        var chrRam = basic with { ChrRamSizeBytes = 8 * 1024 };
        var noChr = basic with { ChrRom = [], ChrRomSizeBytes = 0 };
        var badPrg = basic with { PrgRom = new byte[5 * 8 * 1024], PrgRomSizeBytes = 5 * 8 * 1024 };
        var tooMuchChr = basic with { ChrRom = new byte[512 * 1024], ChrRomSizeBytes = 512 * 1024 };
        var badRam = basic with { PrgRamSizeBytes = 16 * 1024 };

        Assert.Throws<NotSupportedException>(() => new KonamiVrc6Cartridge("TEST.VRC6.4S").LoadImage(fourScreen));
        Assert.Throws<NotSupportedException>(() => new KonamiVrc6Cartridge("TEST.VRC6.CHRRAM").LoadImage(chrRam));
        Assert.Throws<NotSupportedException>(() => new KonamiVrc6Cartridge("TEST.VRC6.NOCHR").LoadImage(noChr));
        Assert.Throws<NotSupportedException>(() => new KonamiVrc6Cartridge("TEST.VRC6.BADPRG").LoadImage(badPrg));
        Assert.Throws<NotSupportedException>(() => new KonamiVrc6Cartridge("TEST.VRC6.BIGCHR").LoadImage(tooMuchChr));
        Assert.Throws<NotSupportedException>(() => new KonamiVrc6Cartridge("TEST.VRC6.BADRAM").LoadImage(badRam));
    }

    private static KonamiVrc6Cartridge CreateCartridge(int mapper, int prgBanks, int chrBanks, int workRamBytes = 0)
    {
        var cartridge = new KonamiVrc6Cartridge("TEST.KONAMI.VRC6");
        cartridge.LoadImage(CreateImage(mapper, CreatePrg(prgBanks), CreateChr(chrBanks), workRamBytes));
        return cartridge;
    }

    private static VirtualHardwareNesRomImage CreateImage(int mapper, byte[] prg, byte[] chr, int workRamBytes)
    {
        return new VirtualHardwareNesRomImage(
            VirtualHardwareNesHeaderFormat.Nes20,
            mapper,
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
            Array.Fill(data, (byte)(0x40 + bank), bank * 8 * 1024, 8 * 1024);
        return data;
    }

    private static byte[] CreateChr(int banks)
    {
        var data = new byte[banks * 1024];
        for (var bank = 0; bank < banks; bank++)
            Array.Fill(data, (byte)(0x80 + (bank & 0x7F)), bank * 1024, 1024);
        return data;
    }

    private static VirtualHardwareNesRomImage CreateExecutionImage()
    {
        var prg = CreatePrg(16);
        var program = new List<byte> { 0x78, 0xD8 }; // SEI, CLD
        AddSta(program, 0x8000, 0x05);
        AddSta(program, 0xC000, 0x07);
        for (var slot = 0; slot < 4; slot++) AddSta(program, (ushort)(0xD000 + slot), (byte)(slot + 1));
        for (var slot = 0; slot < 4; slot++) AddSta(program, (ushort)(0xE000 + slot), (byte)(slot + 5));
        AddSta(program, 0xB003, 0xA8); // WRAM enable + single-screen CIRAM page 0
        AddSta(program, 0x6000, 0x5A);

        AddSta(program, 0x9000, 0x8F);
        AddSta(program, 0x9001, 0x10);
        AddSta(program, 0x9002, 0x80);
        AddSta(program, 0xA000, 0x4A);
        AddSta(program, 0xA001, 0x20);
        AddSta(program, 0xA002, 0x80);
        AddSta(program, 0xB000, 0x20);
        AddSta(program, 0xB001, 0x30);
        AddSta(program, 0xB002, 0x80);
        AddSta(program, 0x9003, 0x00);

        AddSta(program, 0xF000, 0xFE);
        AddSta(program, 0xF001, 0x06);
        var loopAddress = (ushort)(0xE000 + program.Count);
        program.AddRange(new byte[] { 0x4C, (byte)loopAddress, (byte)(loopAddress >> 8) });

        var fixedLast = 15 * 8 * 1024;
        program.ToArray().CopyTo(prg, fixedLast);
        prg[fixedLast + 0x1FFA] = 0x00; prg[fixedLast + 0x1FFB] = 0xE0;
        prg[fixedLast + 0x1FFC] = 0x00; prg[fixedLast + 0x1FFD] = 0xE0;
        prg[fixedLast + 0x1FFE] = 0x00; prg[fixedLast + 0x1FFF] = 0xE0;
        return CreateImage(24, prg, CreateChr(64), 8 * 1024);
    }

    private static void AddSta(List<byte> program, ushort address, byte value) =>
        program.AddRange(new byte[] { 0xA9, value, 0x8D, (byte)address, (byte)(address >> 8) });

    private static CompiledBusTargetDescriptor CpuRomTarget(KonamiVrc6Cartridge cartridge) =>
        Assert.Single(((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.AddressPins.Count == 15 && target.ObserveBusCycle is not null);

    private static CompiledBusTargetDescriptor CpuWorkRamTarget(KonamiVrc6Cartridge cartridge) =>
        Assert.Single(((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.AddressPins.Count == 15 && target.ObserveBusCycle is null);

    private static CompiledBusTargetDescriptor PpuPatternTarget(KonamiVrc6Cartridge cartridge) =>
        Assert.Single(((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.AddressPins.Count == 14 && target.IsSelected is null);

    private static CompiledBusTargetDescriptor PpuChrNametableTarget(KonamiVrc6Cartridge cartridge) =>
        Assert.Single(((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.AddressPins.Count == 14 && target.IsSelected is not null);

    private static void WriteHigh(CompiledBusTargetDescriptor cpu, ushort address, byte value) => cpu.Write!(address & 0x7FFF, value);

    private static void ClockCpu(CompiledBusTargetDescriptor cpu, int cycles)
    {
        var observer = Assert.IsType<Action<bool>>(cpu.ObserveBusCycle);
        for (var cycle = 0; cycle < cycles; cycle++) observer(false);
    }

    private static DigitalLevel EvaluateOutput(KonamiVrc6Cartridge cartridge, DigitalPin output, ushort address)
    {
        var found = ((ICompiledCombinationalComponent)cartridge).TryEvaluateCompiledOutput(
            output,
            pin => SamplePpuAddress(cartridge, pin, address),
            out var drive);
        Assert.True(found);
        return drive.Level;
    }

    private static DigitalLevel SamplePpuAddress(KonamiVrc6Cartridge cartridge, DigitalPin pin, ushort address)
    {
        for (var bit = 0; bit < cartridge.PpuAddress.Width; bit++)
        {
            if (!ReferenceEquals(pin, cartridge.PpuAddress.Pins[bit])) continue;
            return (address & (1 << bit)) != 0 ? DigitalLevel.High : DigitalLevel.Low;
        }
        return DigitalLevel.Unknown;
    }
}
