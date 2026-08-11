using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Machines.Nes;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareNamco163CartridgeTests
{
    [Fact]
    public void Power_on_exposes_three_switchable_prg_windows_fixed_last_bank_and_default_chr_windows()
    {
        var cartridge = CreateCartridge(32, 128);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuTarget(cartridge);

        Assert.Equal(new[] { 0, 0, 0, 31 }, cartridge.PrgWindowBanks.ToArray());
        Assert.Equal((byte)0x40, cpu.Read!(0x0000));
        Assert.Equal((byte)0x40, cpu.Read!(0x2000));
        Assert.Equal((byte)0x40, cpu.Read!(0x4000));
        Assert.Equal((byte)0x5F, cpu.Read!(0x6000));
        for (var slot = 0; slot < 8; slot++)
        {
            Assert.True(ppu.IsSelected!(slot * 0x400, false));
            Assert.Equal((byte)(0x80 + slot), ppu.Read!(slot * 0x400));
        }
        Assert.Equal(0UL, cartridge.PpuWriteCount);
    }

    [Fact]
    public void Prg_registers_select_three_independent_8k_windows()
    {
        var cartridge = CreateCartridge(32, 128);
        var cpu = CpuRomTarget(cartridge);
        WriteHigh(cpu, 0xE000, 0x05);
        WriteHigh(cpu, 0xE800, 0x12);
        WriteHigh(cpu, 0xF000, 0x1A);

        Assert.Equal(new byte[] { 5, 0x12, 0x1A }, cartridge.PrgBankRegisters.ToArray());
        Assert.Equal(new[] { 5, 18, 26, 31 }, cartridge.PrgWindowBanks.ToArray());
        Assert.Equal((byte)0x45, cpu.Read!(0x0000));
        Assert.Equal((byte)0x52, cpu.Read!(0x2000));
        Assert.Equal((byte)0x5A, cpu.Read!(0x4000));
        Assert.Equal((byte)0x5F, cpu.Read!(0x6000));
    }

    [Fact]
    public void Eight_pattern_registers_select_independent_1k_chr_pages()
    {
        var cartridge = CreateCartridge(16, 128);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuTarget(cartridge);
        var addresses = new ushort[] { 0x8000, 0x8800, 0x9000, 0x9800, 0xA000, 0xA800, 0xB000, 0xB800 };
        for (var slot = 0; slot < 8; slot++) WriteHigh(cpu, addresses[slot], (byte)(0x20 + slot));

        for (var slot = 0; slot < 8; slot++)
        {
            Assert.Equal((byte)(0x20 + slot), cartridge.PpuBankRegisters[slot]);
            Assert.True(ppu.IsSelected!(slot * 0x400, false));
            Assert.Equal((byte)(0xA0 + slot), ppu.Read!(slot * 0x400));
        }
    }

    [Fact]
    public void Four_nametable_registers_can_drive_chr_rom_instead_of_ciram()
    {
        var cartridge = CreateCartridge(16, 128);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuTarget(cartridge);
        WriteHigh(cpu, 0xC000, 0x30);
        WriteHigh(cpu, 0xC800, 0x31);
        WriteHigh(cpu, 0xD000, 0x32);
        WriteHigh(cpu, 0xD800, 0x33);

        for (var page = 0; page < 4; page++)
        {
            var address = 0x2000 + page * 0x400;
            Assert.True(ppu.IsSelected!(address, false));
            Assert.Equal((byte)(0xB0 + page), ppu.Read!(address));
            Assert.Equal(DigitalLevel.High, EvaluateDirect(cartridge, cartridge.CiramChipEnableBar, (ushort)address));
        }
    }

    [Theory]
    [InlineData(0x8000, 0x0000, 0)]
    [InlineData(0x8800, 0x0400, 1)]
    [InlineData(0xC000, 0x2000, 0)]
    [InlineData(0xC800, 0x2400, 1)]
    public void E0_E1_bank_values_route_selected_ppu_windows_to_ciram(int register, int ppuAddress, int page)
    {
        var cartridge = CreateCartridge(16, 128);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuTarget(cartridge);
        WriteHigh(cpu, (ushort)register, (byte)(0xE0 | page));

        Assert.False(ppu.IsSelected!(ppuAddress, false));
        Assert.Equal(DigitalLevel.Low, EvaluateDirect(cartridge, cartridge.CiramChipEnableBar, (ushort)ppuAddress));
        Assert.Equal(page == 0 ? DigitalLevel.Low : DigitalLevel.High,
            EvaluateDirect(cartridge, cartridge.CiramA10, (ushort)ppuAddress));
    }

    [Fact]
    public void E800_bit6_disables_ciram_substitution_for_lower_four_pattern_windows()
    {
        var cartridge = CreateCartridge(16, 256);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuTarget(cartridge);
        WriteHigh(cpu, 0x8000, 0xE0);
        Assert.False(ppu.IsSelected!(0x0000, false));

        WriteHigh(cpu, 0xE800, 0x40);
        Assert.True(cartridge.LowChrCiramDisabled);
        Assert.True(ppu.IsSelected!(0x0000, false));
        Assert.Equal((byte)0xE0, ppu.Read!(0x0000));
    }

    [Fact]
    public void E800_bit7_disables_ciram_substitution_for_upper_four_pattern_windows()
    {
        var cartridge = CreateCartridge(16, 256);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuTarget(cartridge);
        WriteHigh(cpu, 0xA000, 0xE1);
        Assert.False(ppu.IsSelected!(0x1000, false));

        WriteHigh(cpu, 0xE800, 0x80);
        Assert.True(cartridge.HighChrCiramDisabled);
        Assert.True(ppu.IsSelected!(0x1000, false));
        Assert.Equal((byte)0xE1, ppu.Read!(0x1000));
    }

    [Fact]
    public void Nametable_registers_always_keep_E0_E1_as_ciram_even_when_pattern_flags_are_disabled()
    {
        var cartridge = CreateCartridge(16, 256);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuTarget(cartridge);
        WriteHigh(cpu, 0xE800, 0xC0);
        WriteHigh(cpu, 0xC000, 0xE1);

        Assert.False(ppu.IsSelected!(0x2000, false));
        Assert.Equal(DigitalLevel.Low, EvaluateDirect(cartridge, cartridge.CiramChipEnableBar, 0x2000));
        Assert.Equal(DigitalLevel.High, EvaluateDirect(cartridge, cartridge.CiramA10, 0x2000));
    }

    [Fact]
    public void Generic_direct_bus_address_facet_matches_package_combinational_ciram_outputs()
    {
        var cartridge = CreateCartridge(16, 256);
        var cpu = CpuRomTarget(cartridge);
        WriteHigh(cpu, 0x8000, 0xE1);
        WriteHigh(cpu, 0xC000, 0x22);
        var direct = Assert.IsAssignableFrom<ICompiledBusAddressCombinationalComponent>(cartridge);

        foreach (var address in new ushort[] { 0x0000, 0x0400, 0x2000, 0x2400, 0x2C00 })
        {
            Assert.True(direct.TryEvaluateCompiledBusAddressOutput(cartridge.CiramChipEnableBar, address, true, out var ce));
            Assert.True(direct.TryEvaluateCompiledBusAddressOutput(cartridge.CiramA10, address, true, out var a10));
            Assert.Equal(EvaluateOutput(cartridge, cartridge.CiramChipEnableBar, address), ce.Level);
            Assert.Equal(EvaluateOutput(cartridge, cartridge.CiramA10, address), a10.Level);
        }
    }

    [Fact]
    public void Work_ram_is_readable_but_power_on_write_protection_blocks_writes()
    {
        var cartridge = CreateCartridge(16, 128, workRamBytes: 8 * 1024);
        var ram = CpuRamTarget(cartridge);
        ram.Write!(0x6000, 0x5A);

        Assert.Equal((byte)0x00, ram.Read!(0x6000));
        Assert.Equal(0UL, cartridge.WorkRamWriteCount);
        Assert.Equal(1UL, cartridge.BlockedWorkRamWriteCount);
    }

    [Fact]
    public void Work_ram_global_enable_allows_all_four_2k_blocks_when_block_bits_are_clear()
    {
        var cartridge = CreateCartridge(16, 128, workRamBytes: 8 * 1024);
        var high = CpuRomTarget(cartridge);
        var ram = CpuRamTarget(cartridge);
        WriteHigh(high, 0xF800, 0x40);
        for (var block = 0; block < 4; block++)
            ram.Write!(0x6000 + block * 0x800, (byte)(0x50 + block));

        for (var block = 0; block < 4; block++)
            Assert.Equal((byte)(0x50 + block), cartridge.InspectWorkRamByte(block * 0x800));
        Assert.Equal(4UL, cartridge.WorkRamWriteCount);
    }

    [Theory]
    [InlineData(0, 0x41)]
    [InlineData(1, 0x42)]
    [InlineData(2, 0x44)]
    [InlineData(3, 0x48)]
    public void Work_ram_has_independent_active_high_write_protect_bits_per_2k_block(int protectedBlock, int protectValue)
    {
        var cartridge = CreateCartridge(16, 128, workRamBytes: 8 * 1024);
        var high = CpuRomTarget(cartridge);
        var ram = CpuRamTarget(cartridge);
        WriteHigh(high, 0xF800, (byte)protectValue);
        for (var block = 0; block < 4; block++) ram.Write!(0x6000 + block * 0x800, 0x5A);

        for (var block = 0; block < 4; block++)
            Assert.Equal((byte)(block == protectedBlock ? 0 : 0x5A), cartridge.InspectWorkRamByte(block * 0x800));
        Assert.Equal(3UL, cartridge.WorkRamWriteCount);
        Assert.Equal(1UL, cartridge.BlockedWorkRamWriteCount);
    }

    [Fact]
    public void Irq_low_high_registers_are_readable_enable_15bit_counting_and_assert_at_7fff()
    {
        var cartridge = CreateCartridge(16, 128);
        var high = CpuRomTarget(cartridge);
        var low = CpuLowRegisterTarget(cartridge);
        low.Write!(0x5000, 0xFD);
        low.Write!(0x5800, 0xFF); // enable + high seven bits = 0x7F

        Assert.Equal((byte)0xFD, low.Read!(0x5000));
        Assert.Equal((byte)0xFF, low.Read!(0x5800));
        ClockCpu(high, 2);

        Assert.Equal((ushort)0x7FFF, cartridge.IrqCounter);
        Assert.True(cartridge.IrqEnabled);
        Assert.True(cartridge.IrqAsserted);
        Assert.Equal(2UL, cartridge.IrqClockCount);
        Assert.Equal(1UL, cartridge.IrqAssertCount);
    }

    [Fact]
    public void Disabled_irq_does_not_count()
    {
        var cartridge = CreateCartridge(16, 128);
        var high = CpuRomTarget(cartridge);
        var low = CpuLowRegisterTarget(cartridge);
        low.Write!(0x5000, 0xF0);
        low.Write!(0x5800, 0x7F);
        ClockCpu(high, 100);

        Assert.False(cartridge.IrqEnabled);
        Assert.Equal((ushort)0x7FF0, cartridge.IrqCounter);
        Assert.Equal(0UL, cartridge.IrqClockCount);
    }

    [Fact]
    public void Either_irq_register_write_acknowledges_asserted_irq()
    {
        var cartridge = CreateCartridge(16, 128);
        var high = CpuRomTarget(cartridge);
        var low = CpuLowRegisterTarget(cartridge);
        low.Write!(0x5000, 0xFE);
        low.Write!(0x5800, 0xFF);
        ClockCpu(high, 1);
        Assert.True(cartridge.IrqAsserted);
        low.Write!(0x5000, 0x00);
        Assert.False(cartridge.IrqAsserted);
    }

    [Fact]
    public void Audio_address_and_data_ports_support_auto_increment_for_writes()
    {
        var audio = new Namco163Audio();
        audio.SetAddressRegister(0xFE); // address 0x7E + autoincrement
        audio.WriteData(0x12);
        audio.WriteData(0x34);
        audio.WriteData(0x56);

        Assert.Equal((byte)0x12, audio.InspectRamByte(0x7E));
        Assert.Equal((byte)0x34, audio.InspectRamByte(0x7F));
        Assert.Equal((byte)0x56, audio.InspectRamByte(0x00));
        Assert.Equal((byte)0x01, audio.RamAddress);
        Assert.Equal(3UL, audio.AutoIncrementCount);
    }

    [Fact]
    public void Audio_data_reads_share_the_same_auto_increment_port()
    {
        var audio = new Namco163Audio();
        audio.SetAddressRegister(0x80);
        audio.WriteData(0xA5);
        audio.SetAddressRegister(0x80);

        Assert.Equal((byte)0xA5, audio.ReadData());
        Assert.Equal((byte)0x01, audio.RamAddress);
        Assert.Equal(1UL, audio.RamDataReadCount);
    }

    [Fact]
    public void Audio_updates_exactly_one_channel_every_fifteen_cpu_cycles()
    {
        var audio = new Namco163Audio();
        WriteAudioRam(audio, 0x7F, 0x10); // two active channels
        for (var i = 0; i < 14; i++) audio.ClockCpuCycle();
        Assert.Equal(0UL, audio.ChannelUpdateCount);
        audio.ClockCpuCycle();
        Assert.Equal(1UL, audio.ChannelUpdateCount);
        Assert.Equal(6, audio.CurrentChannel);
    }

    [Fact]
    public void Audio_active_channel_count_controls_round_robin_lower_channel_bound()
    {
        var audio = new Namco163Audio();
        WriteAudioRam(audio, 0x7F, 0x30); // 4 active channels: 7,6,5,4
        for (var update = 0; update < 4; update++)
            for (var cycle = 0; cycle < 15; cycle++) audio.ClockCpuCycle();

        Assert.Equal(4, audio.ActiveChannelCount);
        Assert.Equal(7, audio.CurrentChannel);
        Assert.Equal(4UL, audio.ChannelUpdateCount);
    }

    [Fact]
    public void Audio_advances_18bit_frequency_through_24bit_phase_and_reads_packed_4bit_wave_samples()
    {
        var audio = new Namco163Audio();
        WriteAudioRam(audio, 0x00, 0xA2); // samples 2, A
        var b = 0x40 + 7 * 8;
        WriteAudioRam(audio, b + 0, 0x00); // frequency low
        WriteAudioRam(audio, b + 2, 0x00); // frequency mid
        WriteAudioRam(audio, b + 4, 0x01); // frequency high=1, length=256
        WriteAudioRam(audio, b + 6, 0x00); // wave address
        WriteAudioRam(audio, b + 7, 0x0F); // 1 channel, volume 15

        for (var cycle = 0; cycle < 15; cycle++) audio.ClockCpuCycle();

        Assert.Equal((uint)0x010000, audio.GetPhase(7));
        Assert.Equal((byte)0x0A, audio.LastWaveSample); // phase sample index 1 -> high nibble
        Assert.Equal((byte)0x0F, audio.LastVolume);
        Assert.Equal(30, audio.SerialDacLevel); // (10 - 8) * 15
    }

    [Fact]
    public void Audio_sound_disable_holds_channel_sequencer_and_drives_dac_to_zero()
    {
        var audio = new Namco163Audio();
        for (var cycle = 0; cycle < 15; cycle++) audio.ClockCpuCycle();
        var updates = audio.ChannelUpdateCount;
        audio.SetSoundDisabled(true);
        for (var cycle = 0; cycle < 100; cycle++) audio.ClockCpuCycle();

        Assert.Equal(updates, audio.ChannelUpdateCount);
        Assert.Equal(0, audio.SerialDacLevel);
        Assert.Equal(115UL, audio.CpuClockCount);
    }

    [Fact]
    public void E000_programs_prg0_and_sound_disable_on_the_same_physical_register()
    {
        var cartridge = CreateCartridge(32, 128);
        var high = CpuRomTarget(cartridge);
        WriteHigh(high, 0xE000, 0x45);

        Assert.Equal((byte)0x05, cartridge.PrgBankRegisters[0]);
        Assert.Equal(5, cartridge.PrgWindowBanks[0]);
        Assert.True(cartridge.Audio.SoundDisabled);
    }

    [Fact]
    public void F800_programs_work_ram_protection_and_audio_ram_address_on_the_same_physical_register()
    {
        var cartridge = CreateCartridge(16, 128, workRamBytes: 8 * 1024);
        var high = CpuRomTarget(cartridge);
        WriteHigh(high, 0xF800, 0xC5);

        Assert.Equal((byte)0xC5, cartridge.WriteProtectRegister);
        Assert.Equal((byte)0x45, cartridge.Audio.RamAddress);
        Assert.True(cartridge.Audio.AutoIncrement);
    }

    [Fact]
    public void Compiled_low_register_target_preserves_audio_read_side_effect_once_per_completed_read()
    {
        var cartridge = CreateCartridge(16, 128);
        var high = CpuRomTarget(cartridge);
        var low = CpuLowRegisterTarget(cartridge);
        WriteHigh(high, 0xF800, 0x80);
        low.Write!(0x4800, 0x5A);
        WriteHigh(high, 0xF800, 0x80);

        Assert.Equal((byte)0x5A, low.Read!(0x4800));
        ClockCpu(high, 1); // completion-edge package observer applies the read-port increment
        Assert.Equal((byte)0x01, cartridge.Audio.RamAddress);
        Assert.Equal(1UL, cartridge.Audio.RamDataReadCount);
    }

    [Fact]
    public void Compiled_mapper_latches_writes_and_clocks_irq_audio_on_completion_edge()
    {
        var cartridge = CreateCartridge(16, 128);
        var high = CpuRomTarget(cartridge);
        Assert.Equal(CompiledBusWritePhase.Complete, high.WritePhase);
        Assert.Equal(CompiledBusCycleObservationPhase.Complete, high.ObserveBusCyclePhase);
        ClockCpu(high, 30);
        Assert.Equal(30UL, cartridge.CpuCycleClockCount);
        Assert.Equal(30UL, cartridge.Audio.CpuClockCount);
        Assert.Equal(2UL, cartridge.Audio.ChannelUpdateCount);
    }

    [Fact]
    public void Generic_compiled_and_raw_physical_namco163_execute_same_banking_ram_irq_audio_and_ppu_program()
    {
        var image = CreateExecutionImage();
        var compiled = new RegionalNesVirtualMachine();
        var reference = new RegionalNesVirtualMachine();
        compiled.SetCompiledLabExecutionEnabled(true);
        compiled.InsertRom(image, "Namco 163 synthetic (Japan).nes");
        reference.InsertRom(image, "Namco 163 synthetic (Japan).nes");
        reference.Famicom.SetCompiledPhysicalMachineEnabled(false);
        compiled.PowerOn();
        reference.PowerOn();
        compiled.ReleaseReset();
        reference.ReleaseReset();

        const int masterCycles = 90_000;
        compiled.AdvanceMasterCycles(masterCycles);
        reference.AdvanceMasterCycles(masterCycles);

        var actual = Assert.IsType<Namco163Cartridge>(compiled.Slot.Cartridge);
        var expected = Assert.IsType<Namco163Cartridge>(reference.Slot.Cartridge);
        Assert.True(compiled.Famicom.CompiledLabMotherboardEnabled);
        Assert.Equal(new[] { 5, 6, 7, 15 }, actual.PrgWindowBanks.ToArray());
        Assert.Equal((byte)0x5A, actual.InspectWorkRamByte(0));
        Assert.True(actual.IrqAsserted);
        Assert.True(actual.Audio.RamDataWriteCount > 0);
        Assert.True(actual.Audio.ChannelUpdateCount > 0);
        Assert.Equal(expected.PrgWindowBanks.ToArray(), actual.PrgWindowBanks.ToArray());
        Assert.Equal(expected.PpuBankRegisters.ToArray(), actual.PpuBankRegisters.ToArray());
        Assert.Equal(expected.WriteProtectRegister, actual.WriteProtectRegister);
        Assert.Equal(expected.IrqCounter, actual.IrqCounter);
        Assert.Equal(expected.IrqAsserted, actual.IrqAsserted);
        Assert.Equal(expected.CpuCycleClockCount, actual.CpuCycleClockCount);
        Assert.Equal(expected.Audio.CpuClockCount, actual.Audio.CpuClockCount);
        Assert.Equal(expected.Audio.ChannelUpdateCount, actual.Audio.ChannelUpdateCount);
        Assert.Equal(expected.Audio.RamAddress, actual.Audio.RamAddress);
        Assert.Equal(expected.Audio.SerialDacLevel, actual.Audio.SerialDacLevel);
        Assert.Equal(expected.MapperWriteCount, actual.MapperWriteCount);
        Assert.Equal(expected.WorkRamWriteCount, actual.WorkRamWriteCount);
        Assert.Equal(reference.Famicom.Cpu.ProgramCounter, compiled.Famicom.Cpu.ProgramCounter);
        Assert.Equal(reference.Famicom.Cpu.CompletedInstructionCount, compiled.Famicom.Cpu.CompletedInstructionCount);
        Assert.Equal(reference.Famicom.Ppu.InspectDiagnosticState(), compiled.Famicom.Ppu.InspectDiagnosticState());
        Assert.Equal(reference.Famicom.Ciram.InspectStateHash(), compiled.Famicom.Ciram.InspectStateHash());
    }

    [Fact]
    public void Factory_constructs_mapper19_as_namco163_hardware()
    {
        var cartridge = VirtualCartridgeHardwareFactory.Create(CreateImage(CreatePrg(16), CreateChr(128), 0));
        var namco = Assert.IsType<Namco163Cartridge>(cartridge);
        Assert.Equal(19, namco.MapperNumber);
    }

    [Fact]
    public void Invalid_mapper_four_screen_chr_ram_bad_rom_geometry_and_oversized_ram_are_rejected()
    {
        var basic = CreateImage(CreatePrg(16), CreateChr(128), 0);
        var wrongMapper = basic with { MapperNumber = 18 };
        var fourScreen = basic with { Mirroring = VirtualHardwareNesMirroring.FourScreen };
        var chrRam = basic with { ChrRamSizeBytes = 8 * 1024 };
        var noChr = basic with { ChrRom = [], ChrRomSizeBytes = 0 };
        var badPrg = basic with { PrgRom = new byte[5 * 8 * 1024], PrgRomSizeBytes = 5 * 8 * 1024 };
        var tooMuchChr = basic with { ChrRom = new byte[512 * 1024], ChrRomSizeBytes = 512 * 1024 };
        var badRam = basic with { PrgRamSizeBytes = 16 * 1024 };

        Assert.Throws<NotSupportedException>(() => new Namco163Cartridge("TEST.N163.WRONG").LoadImage(wrongMapper));
        Assert.Throws<NotSupportedException>(() => new Namco163Cartridge("TEST.N163.4S").LoadImage(fourScreen));
        Assert.Throws<NotSupportedException>(() => new Namco163Cartridge("TEST.N163.CHRRAM").LoadImage(chrRam));
        Assert.Throws<NotSupportedException>(() => new Namco163Cartridge("TEST.N163.NOCHR").LoadImage(noChr));
        Assert.Throws<NotSupportedException>(() => new Namco163Cartridge("TEST.N163.BADPRG").LoadImage(badPrg));
        Assert.Throws<NotSupportedException>(() => new Namco163Cartridge("TEST.N163.BIGCHR").LoadImage(tooMuchChr));
        Assert.Throws<NotSupportedException>(() => new Namco163Cartridge("TEST.N163.BADRAM").LoadImage(badRam));
    }

    private static Namco163Cartridge CreateCartridge(int prgBanks, int chrBanks, int workRamBytes = 0)
    {
        var cartridge = new Namco163Cartridge("TEST.NAMCO163");
        cartridge.LoadImage(CreateImage(CreatePrg(prgBanks), CreateChr(chrBanks), workRamBytes));
        return cartridge;
    }

    private static VirtualHardwareNesRomImage CreateImage(byte[] prg, byte[] chr, int workRamBytes)
    {
        return new VirtualHardwareNesRomImage(
            VirtualHardwareNesHeaderFormat.Nes20,
            19,
            0,
            prg.Length,
            chr.Length,
            false,
            workRamBytes != 0,
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
        AddSta(program, 0xE000, 0x05);
        AddSta(program, 0xE800, 0x06);
        AddSta(program, 0xF000, 0x07);
        AddSta(program, 0x8000, 0x01);
        AddSta(program, 0x8800, 0xE1);
        AddSta(program, 0xC000, 0xE0);
        AddSta(program, 0xC800, 0x21);
        AddSta(program, 0xF800, 0x40); // RAM global write enable, audio address $40
        AddSta(program, 0x6000, 0x5A);

        AddSta(program, 0xF800, 0xC0); // audio addr $40, autoincrement; RAM write enabled
        for (var i = 0; i < 16; i++) AddSta(program, 0x4800, (byte)(i * 0x11));
        AddSta(program, 0xF800, 0xFF); // audio addr $7F, autoincrement; also protected RAM
        AddSta(program, 0x4800, 0x00); // one active channel

        AddSta(program, 0x5000, 0xFE);
        AddSta(program, 0x5800, 0xFF);
        var loopAddress = (ushort)(0xE000 + program.Count);
        program.AddRange(new byte[] { 0x4C, (byte)loopAddress, (byte)(loopAddress >> 8) });

        var fixedLast = 15 * 8 * 1024;
        program.ToArray().CopyTo(prg, fixedLast);
        prg[fixedLast + 0x1FFA] = 0x00; prg[fixedLast + 0x1FFB] = 0xE0;
        prg[fixedLast + 0x1FFC] = 0x00; prg[fixedLast + 0x1FFD] = 0xE0;
        prg[fixedLast + 0x1FFE] = 0x00; prg[fixedLast + 0x1FFF] = 0xE0;
        return CreateImage(prg, CreateChr(128), 8 * 1024);
    }

    private static void AddSta(List<byte> program, ushort address, byte value) =>
        program.AddRange(new byte[] { 0xA9, value, 0x8D, (byte)address, (byte)(address >> 8) });

    private static void WriteAudioRam(Namco163Audio audio, int address, byte value)
    {
        audio.SetAddressRegister((byte)address);
        audio.WriteData(value);
    }

    private static CompiledBusTargetDescriptor CpuRomTarget(Namco163Cartridge cartridge) =>
        Assert.Single(((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.AddressPins.Count == 15 && target.ObserveBusCycle is not null);

    private static CompiledBusTargetDescriptor CpuLowRegisterTarget(Namco163Cartridge cartridge) =>
        Assert.Single(((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.AddressPins.Count == 15 && target.ObserveBusCycle is null && target.ReadConditions.Count == 3);

    private static CompiledBusTargetDescriptor CpuRamTarget(Namco163Cartridge cartridge) =>
        Assert.Single(((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.AddressPins.Count == 15 && target.ObserveBusCycle is null && target.ReadConditions.Count == 5);

    private static CompiledBusTargetDescriptor PpuTarget(Namco163Cartridge cartridge) =>
        Assert.Single(((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(), target => target.AddressPins.Count == 14);

    private static void WriteHigh(CompiledBusTargetDescriptor cpu, ushort address, byte value) => cpu.Write!(address & 0x7FFF, value);

    private static void ClockCpu(CompiledBusTargetDescriptor cpu, int cycles)
    {
        var observer = Assert.IsType<Action<bool>>(cpu.ObserveBusCycle);
        for (var cycle = 0; cycle < cycles; cycle++) observer(false);
    }

    private static DigitalLevel EvaluateDirect(Namco163Cartridge cartridge, DigitalPin output, ushort address)
    {
        var direct = Assert.IsAssignableFrom<ICompiledBusAddressCombinationalComponent>(cartridge);
        Assert.True(direct.TryEvaluateCompiledBusAddressOutput(output, address, true, out var drive));
        return drive.Level;
    }

    private static DigitalLevel EvaluateOutput(Namco163Cartridge cartridge, DigitalPin output, ushort address)
    {
        Assert.True(((ICompiledCombinationalComponent)cartridge).TryEvaluateCompiledOutput(
            output,
            pin => SamplePpuAddress(cartridge, pin, address),
            out var drive));
        return drive.Level;
    }

    private static DigitalLevel SamplePpuAddress(Namco163Cartridge cartridge, DigitalPin pin, ushort address)
    {
        for (var bit = 0; bit < cartridge.PpuAddress.Width; bit++)
            if (ReferenceEquals(pin, cartridge.PpuAddress.Pins[bit]))
                return (address & (1 << bit)) != 0 ? DigitalLevel.High : DigitalLevel.Low;
        return DigitalLevel.Unknown;
    }
}
