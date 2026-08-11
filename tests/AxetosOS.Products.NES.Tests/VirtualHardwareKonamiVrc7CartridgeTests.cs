using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Machines.Nes;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareKonamiVrc7CartridgeTests
{
    [Fact]
    public void Power_on_exposes_three_switchable_8k_prg_fixed_last_and_eight_chr_windows()
    {
        var cartridge = CreateCartridge(16, 64);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuTarget(cartridge);

        Assert.Equal(new[] { 0, 0, 0, 15 }, cartridge.PrgWindowBanks.ToArray());
        Assert.Equal((byte)0x40, cpu.Read!(0x0000));
        Assert.Equal((byte)0x4F, cpu.Read!(0x6000));
        Assert.All(cartridge.ChrWindowBanks, bank => Assert.Equal(0, bank));
        Assert.Equal((byte)0x80, ppu.Read!(0x0000));
        Assert.False(cartridge.IsChrRam);
        Assert.Null(ppu.Write);
        Assert.Equal(0UL, cartridge.PpuWriteCount);
    }

    [Fact]
    public void Prg_registers_select_three_independent_8k_windows()
    {
        var cartridge = CreateCartridge(32, 64);
        var cpu = CpuRomTarget(cartridge);
        WriteHigh(cpu, 0x8000, 0x05);
        WriteHigh(cpu, 0x8008, 0x06);
        WriteHigh(cpu, 0x9000, 0x17);

        Assert.Equal(new byte[] { 5, 6, 0x17 }, cartridge.PrgBankRegisters.ToArray());
        Assert.Equal(new[] { 5, 6, 23, 31 }, cartridge.PrgWindowBanks.ToArray());
        Assert.Equal((byte)0x45, cpu.Read!(0x0000));
        Assert.Equal((byte)0x46, cpu.Read!(0x2000));
        Assert.Equal((byte)0x57, cpu.Read!(0x4000));
        Assert.Equal((byte)0x5F, cpu.Read!(0x6000));
    }

    [Theory]
    [InlineData(0xA010, 1)]
    [InlineData(0xB010, 3)]
    [InlineData(0xC010, 5)]
    [InlineData(0xD010, 7)]
    public void X010_aliases_normalize_to_x008_chr_registers(int address, int slot)
    {
        var cartridge = CreateCartridge(16, 64);
        var cpu = CpuRomTarget(cartridge);
        WriteHigh(cpu, (ushort)address, 0x2A);

        Assert.Equal((byte)0x2A, cartridge.ChrBankRegisters[slot]);
        Assert.Equal((ushort)((address & 0xF000) | 0x0008), cartridge.LastNormalizedMapperWriteAddress);
    }

    [Fact]
    public void Audio_ports_preserve_9010_and_9030_instead_of_chr_alias_normalization()
    {
        Assert.Equal((ushort)0x9010, KonamiVrc7Cartridge.NormalizeRegisterAddress(0x9010));
        Assert.Equal((ushort)0x9030, KonamiVrc7Cartridge.NormalizeRegisterAddress(0x9030));
        Assert.Equal((ushort)0xA008, KonamiVrc7Cartridge.NormalizeRegisterAddress(0xA010));
    }

    [Fact]
    public void Eight_chr_registers_select_independent_1k_pages()
    {
        var cartridge = CreateCartridge(16, 128);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuTarget(cartridge);
        ushort[] registers = [0xA000,0xA008,0xB000,0xB008,0xC000,0xC008,0xD000,0xD008];
        for (var slot = 0; slot < 8; slot++) WriteHigh(cpu, registers[slot], (byte)(0x20 + slot));

        Assert.Equal(Enumerable.Range(0x20, 8).ToArray(), cartridge.ChrWindowBanks.ToArray());
        for (var slot = 0; slot < 8; slot++)
            Assert.Equal((byte)(0x80 + 0x20 + slot), ppu.Read!(slot * 0x400));
    }

    [Theory]
    [InlineData(0x00, KonamiVrcNametableMode.Vertical)]
    [InlineData(0x01, KonamiVrcNametableMode.Horizontal)]
    [InlineData(0x02, KonamiVrcNametableMode.SingleScreenPage0)]
    [InlineData(0x03, KonamiVrcNametableMode.SingleScreenPage1)]
    public void Control_register_selects_all_four_ciram_modes(int flags, KonamiVrcNametableMode expected)
    {
        var cartridge = CreateCartridge(16, 64);
        WriteHigh(CpuRomTarget(cartridge), 0xE000, (byte)flags);
        Assert.Equal(expected, cartridge.NametableMode);
    }

    [Fact]
    public void Ciram_a10_combinational_output_matches_selected_mirroring()
    {
        var cartridge = CreateCartridge(16, 64);
        var cpu = CpuRomTarget(cartridge);
        var direct = Assert.IsAssignableFrom<ICompiledBusAddressCombinationalComponent>(cartridge);
        WriteHigh(cpu, 0xE000, 0x00);
        Assert.Equal(DigitalLevel.Low, EvaluateOutput(cartridge, cartridge.CiramA10, 0x2000));
        Assert.Equal(DigitalLevel.High, EvaluateOutput(cartridge, cartridge.CiramA10, 0x2400));
        Assert.True(direct.TryEvaluateCompiledBusAddressOutput(cartridge.CiramA10, 0x2400, readCycle: true, out var directVertical));
        Assert.Equal(DigitalLevel.High, directVertical.Level);
        WriteHigh(cpu, 0xE000, 0x01);
        Assert.Equal(DigitalLevel.Low, EvaluateOutput(cartridge, cartridge.CiramA10, 0x2400));
        Assert.Equal(DigitalLevel.High, EvaluateOutput(cartridge, cartridge.CiramA10, 0x2800));
        Assert.True(direct.TryEvaluateCompiledBusAddressOutput(cartridge.CiramChipEnableBar, 0x2000, readCycle: true, out var directCe));
        Assert.Equal(DigitalLevel.Low, directCe.Level);
    }

    [Fact]
    public void Work_ram_is_electrically_gated_by_control_bit_7()
    {
        var cartridge = CreateCartridge(16, 64, 8 * 1024);
        var cpu = CpuRomTarget(cartridge);
        var ram = CpuWorkRamTarget(cartridge);
        Assert.False(cartridge.WorkRamEnabled);
        Assert.False(ram.IsSelected!(0x6000, true));

        WriteHigh(cpu, 0xE000, 0x80);
        Assert.True(cartridge.WorkRamEnabled);
        Assert.True(ram.IsSelected!(0x6000, true));
        ram.Write!(0x6000, 0x5A);
        Assert.Equal((byte)0x5A, cartridge.InspectWorkRamByte(0));
    }

    [Fact]
    public void Control_bit_6_mutes_fm_and_disregards_audio_port_writes()
    {
        var cartridge = CreateCartridge(16, 64);
        var cpu = CpuRomTarget(cartridge);
        WriteHigh(cpu, 0xE000, 0x40);
        WriteHigh(cpu, 0x9010, 0x10);
        WriteHigh(cpu, 0x9030, 0x7F);

        Assert.True(cartridge.AudioMuted);
        Assert.True(cartridge.Audio.Muted);
        Assert.Equal(2UL, cartridge.Audio.IgnoredWriteCount);
        Assert.Equal((byte)0, cartridge.Audio.Registers[0x10]);
    }

    [Fact]
    public void Irq_uses_full_byte_reload_and_cycle_mode_keeps_prescaler_dormant()
    {
        var cartridge = CreateCartridge(16, 64);
        var cpu = CpuRomTarget(cartridge);
        WriteHigh(cpu, 0xE008, 0xFE);
        WriteHigh(cpu, 0xF000, 0x06);
        ClockCpu(cpu, 2);

        Assert.Equal((byte)0xFE, cartridge.Irq.ReloadValue);
        Assert.Equal(341, cartridge.Irq.Prescaler);
        Assert.True(cartridge.Irq.CycleMode);
        Assert.True(cartridge.Irq.Asserted);
        Assert.Equal(1UL, cartridge.Irq.AssertCount);
    }

    [Fact]
    public void Irq_scanline_mode_uses_bounded_341_dot_prescaler()
    {
        var cartridge = CreateCartridge(16, 64);
        var cpu = CpuRomTarget(cartridge);
        WriteHigh(cpu, 0xE008, 0x80);
        WriteHigh(cpu, 0xF000, 0x02);
        ClockCpu(cpu, 114);

        Assert.False(cartridge.Irq.CycleMode);
        Assert.InRange(cartridge.Irq.Prescaler, 1, 341);
        Assert.True(cartridge.Irq.CounterClockCount >= 1);
    }

    [Fact]
    public void Irq_acknowledge_clears_open_drain_output_and_honors_enable_after_ack()
    {
        var cartridge = CreateCartridge(16, 64);
        var cpu = CpuRomTarget(cartridge);
        WriteHigh(cpu, 0xE008, 0xFF);
        WriteHigh(cpu, 0xF000, 0x07);
        ClockCpu(cpu, 1);
        Assert.True(cartridge.Irq.Asserted);
        WriteHigh(cpu, 0xF008, 0x00);
        Assert.False(cartridge.Irq.Asserted);
        Assert.True(cartridge.Irq.Enabled);
    }

    [Fact]
    public void Audio_address_and_data_ports_program_channel_registers()
    {
        var audio = new KonamiVrc7Audio();
        audio.WritePort(0x9010, 0x10);
        audio.WritePort(0x9030, 0x34);
        audio.WritePort(0x9010, 0x20);
        audio.WritePort(0x9030, 0x15);
        audio.WritePort(0x9010, 0x30);
        audio.WritePort(0x9030, 0x20);
        for (var i = 0; i < 36; i++) audio.ClockCpuCycle();

        var channel = audio.Channels[0];
        Assert.Equal((ushort)0x134, channel.FNumber);
        Assert.Equal((byte)2, channel.Block);
        Assert.True(channel.KeyOn);
        Assert.Equal((byte)2, channel.Instrument);
        Assert.Equal((byte)0, channel.Volume);
        Assert.Equal(1UL, audio.KeyOnCount);
    }

    [Fact]
    public void Audio_custom_patch_ram_is_writable_through_register_zero_to_seven()
    {
        var audio = new KonamiVrc7Audio();
        for (byte register = 0; register < 8; register++)
        {
            audio.WritePort(0x9010, register);
            audio.WritePort(0x9030, (byte)(0xA0 + register));
        }
        Assert.Equal(8UL, audio.DataWriteCount);
        for (var register = 0; register < 8; register++)
            Assert.Equal((byte)(0xA0 + register), audio.Registers[register]);
    }

    [Fact]
    public void Audio_clocks_one_fm_sample_every_36_cpu_cycles()
    {
        var audio = new KonamiVrc7Audio();
        for (var cycle = 0; cycle < 35; cycle++) audio.ClockCpuCycle();
        Assert.Equal(0UL, audio.SampleClockCount);
        audio.ClockCpuCycle();
        Assert.Equal(1UL, audio.SampleClockCount);
        for (var cycle = 0; cycle < 72; cycle++) audio.ClockCpuCycle();
        Assert.Equal(3UL, audio.SampleClockCount);
        Assert.Equal(108UL, audio.CpuClockCount);
    }

    [Fact]
    public void Audio_has_six_independent_melodic_channels()
    {
        var audio = new KonamiVrc7Audio();
        for (var channel = 0; channel < 6; channel++)
        {
            WriteAudio(audio, (byte)(0x10 + channel), (byte)(0x20 + channel));
            WriteAudio(audio, (byte)(0x20 + channel), 0x15);
            WriteAudio(audio, (byte)(0x30 + channel), (byte)(((channel + 1) << 4) | channel));
        }
        for (var cycle = 0; cycle < 72; cycle++) audio.ClockCpuCycle();

        Assert.Equal(6, audio.Channels.Count);
        Assert.All(audio.Channels, channel => Assert.True(channel.KeyOn));
        Assert.Equal(6UL, audio.KeyOnCount);
        Assert.All(audio.Channels, channel => Assert.True(channel.PhaseAdvanceCount > 0));
    }

    [Fact]
    public void Audio_key_on_and_key_off_drive_attack_then_release_state_without_stopping_cpu_clock()
    {
        var audio = new KonamiVrc7Audio();
        WriteAudio(audio, 0x10, 0x80);
        WriteAudio(audio, 0x20, 0x15);
        WriteAudio(audio, 0x30, 0x10);
        for (var cycle = 0; cycle < 360; cycle++) audio.ClockCpuCycle();
        var before = audio.Channels[0].PhaseAdvanceCount;
        WriteAudio(audio, 0x20, 0x05);
        for (var cycle = 0; cycle < 360; cycle++) audio.ClockCpuCycle();

        Assert.False(audio.Channels[0].KeyOn);
        Assert.True(audio.Channels[0].PhaseAdvanceCount >= before);
        Assert.Equal(720UL, audio.CpuClockCount);
    }

    [Fact]
    public void Compiled_mapper_latches_writes_and_clocks_irq_audio_on_completion_edge()
    {
        var cartridge = CreateCartridge(16, 64);
        var cpu = CpuRomTarget(cartridge);
        Assert.Equal(CompiledBusWritePhase.Complete, cpu.WritePhase);
        Assert.Equal(CompiledBusCycleObservationPhase.Complete, cpu.ObserveBusCyclePhase);
        ClockCpu(cpu, 72);
        Assert.Equal(72UL, cartridge.Irq.CpuClockCount);
        Assert.Equal(72UL, cartridge.Audio.CpuClockCount);
        Assert.Equal(2UL, cartridge.Audio.SampleClockCount);
    }

    [Fact]
    public void Generic_compiled_and_raw_physical_vrc7_execute_same_banking_ram_irq_audio_and_ppu_program()
    {
        var image = CreateExecutionImage();
        var compiled = new RegionalNesVirtualMachine();
        var reference = new RegionalNesVirtualMachine();

        compiled.SetCompiledLabExecutionEnabled(true);
        compiled.InsertRom(image, "VRC7 synthetic (Japan).nes");
        reference.InsertRom(image, "VRC7 synthetic (Japan).nes");
        reference.Famicom.SetCompiledPhysicalMachineEnabled(false);
        compiled.PowerOn();
        reference.PowerOn();
        compiled.ReleaseReset();
        reference.ReleaseReset();

        const int masterCycles = 80_000;
        compiled.AdvanceMasterCycles(masterCycles);
        reference.AdvanceMasterCycles(masterCycles);

        var actual = Assert.IsType<KonamiVrc7Cartridge>(compiled.Slot.Cartridge);
        var expected = Assert.IsType<KonamiVrc7Cartridge>(reference.Slot.Cartridge);
        Assert.True(compiled.Famicom.CompiledLabMotherboardEnabled);
        Assert.Equal(new[] { 5, 6, 7, 15 }, actual.PrgWindowBanks.ToArray());
        Assert.Equal(Enumerable.Range(1, 8).ToArray(), actual.ChrWindowBanks.ToArray());
        Assert.True(actual.WorkRamEnabled);
        Assert.Equal((byte)0x5A, actual.InspectWorkRamByte(0));
        Assert.True(actual.Irq.Asserted);
        Assert.True(actual.Audio.DataWriteCount > 0);
        Assert.True(actual.Audio.SampleClockCount > 0);
        Assert.Equal(expected.PrgWindowBanks.ToArray(), actual.PrgWindowBanks.ToArray());
        Assert.Equal(expected.ChrWindowBanks.ToArray(), actual.ChrWindowBanks.ToArray());
        Assert.Equal(expected.NametableMode, actual.NametableMode);
        Assert.Equal(expected.Irq.Counter, actual.Irq.Counter);
        Assert.Equal(expected.Irq.Prescaler, actual.Irq.Prescaler);
        Assert.Equal(expected.Irq.Asserted, actual.Irq.Asserted);
        Assert.Equal(expected.Irq.CpuClockCount, actual.Irq.CpuClockCount);
        Assert.Equal(expected.Audio.CpuClockCount, actual.Audio.CpuClockCount);
        Assert.Equal(expected.Audio.SampleClockCount, actual.Audio.SampleClockCount);
        Assert.Equal(expected.Audio.MixedDacLevel, actual.Audio.MixedDacLevel);
        Assert.Equal(expected.Audio.Channels[0].PhaseAdvanceCount, actual.Audio.Channels[0].PhaseAdvanceCount);
        Assert.Equal(expected.MapperWriteCount, actual.MapperWriteCount);
        Assert.Equal(expected.WorkRamWriteCount, actual.WorkRamWriteCount);
        Assert.Equal(reference.Famicom.Cpu.ProgramCounter, compiled.Famicom.Cpu.ProgramCounter);
        Assert.Equal(reference.Famicom.Cpu.CompletedInstructionCount, compiled.Famicom.Cpu.CompletedInstructionCount);
        Assert.Equal(reference.Famicom.Ppu.InspectDiagnosticState(), compiled.Famicom.Ppu.InspectDiagnosticState());
        Assert.Equal(reference.Famicom.Ciram.InspectStateHash(), compiled.Famicom.Ciram.InspectStateHash());
    }

    [Fact]
    public void Factory_constructs_mapper85_as_vrc7_hardware()
    {
        var cartridge = VirtualCartridgeHardwareFactory.Create(CreateImage(CreatePrg(16), CreateChr(64), 0));
        var vrc7 = Assert.IsType<KonamiVrc7Cartridge>(cartridge);
        Assert.Equal(85, vrc7.MapperNumber);
    }

    [Fact]
    public void Chr_ram_only_topology_banks_and_writes_selected_1k_pages()
    {
        var cartridge = new KonamiVrc7Cartridge("TEST.VRC7.CHRRAM");
        cartridge.LoadImage(CreateChrRamImage(CreatePrg(16), 8 * 1024));
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuTarget(cartridge);

        Assert.True(cartridge.IsChrRam);
        Assert.Equal(8 * 1024, cartridge.ChrMemorySizeBytes);
        Assert.NotNull(ppu.Write);
        WriteHigh(cpu, 0xA000, 0x07);
        ppu.Write!(0x0000, 0x5A);

        Assert.Equal(7, cartridge.ChrWindowBanks[0]);
        Assert.Equal((byte)0x5A, cartridge.InspectChrByte(7 * 1024));
        Assert.Equal((byte)0x5A, ppu.Read!(0x0000));
        Assert.Equal(1UL, cartridge.PpuWriteCount);
    }

    [Fact]
    public void Generic_compiled_and_raw_physical_vrc7_execute_same_chr_ram_ppu_write_program()
    {
        var image = CreateChrRamExecutionImage();
        var compiled = new RegionalNesVirtualMachine();
        var reference = new RegionalNesVirtualMachine();

        compiled.SetCompiledLabExecutionEnabled(true);
        compiled.InsertRom(image, "VRC7 CHR RAM synthetic (Japan).nes");
        reference.InsertRom(image, "VRC7 CHR RAM synthetic (Japan).nes");
        reference.Famicom.SetCompiledPhysicalMachineEnabled(false);
        compiled.PowerOn();
        reference.PowerOn();
        compiled.ReleaseReset();
        reference.ReleaseReset();

        const int masterCycles = 80_000;
        compiled.AdvanceMasterCycles(masterCycles);
        reference.AdvanceMasterCycles(masterCycles);

        var actual = Assert.IsType<KonamiVrc7Cartridge>(compiled.Slot.Cartridge);
        var expected = Assert.IsType<KonamiVrc7Cartridge>(reference.Slot.Cartridge);
        Assert.True(actual.IsChrRam);
        Assert.True(actual.PpuWriteCount > 0);
        Assert.Equal(expected.PpuWriteCount, actual.PpuWriteCount);
        Assert.Equal((byte)0x5A, actual.InspectChrByte(7 * 1024));
        Assert.Equal(expected.InspectChrByte(7 * 1024), actual.InspectChrByte(7 * 1024));
        Assert.Equal(reference.Famicom.Cpu.ProgramCounter, compiled.Famicom.Cpu.ProgramCounter);
        Assert.Equal(reference.Famicom.Ppu.InspectDiagnosticState(), compiled.Famicom.Ppu.InspectDiagnosticState());
    }

    [Fact]
    public void Invalid_four_screen_mixed_chr_missing_chr_memory_bad_prg_oversized_chr_and_oversized_work_ram_are_rejected()
    {
        var basic = CreateImage(CreatePrg(16), CreateChr(64), 0);
        var fourScreen = basic with { Mirroring = VirtualHardwareNesMirroring.FourScreen };
        var chrRam = basic with { ChrRamSizeBytes = 8 * 1024 };
        var noChr = basic with { ChrRom = [], ChrRomSizeBytes = 0 };
        var badPrg = basic with { PrgRom = new byte[5 * 8 * 1024], PrgRomSizeBytes = 5 * 8 * 1024 };
        var tooMuchChr = basic with { ChrRom = new byte[512 * 1024], ChrRomSizeBytes = 512 * 1024 };
        var badRam = basic with { PrgRamSizeBytes = 16 * 1024 };

        Assert.Throws<NotSupportedException>(() => new KonamiVrc7Cartridge("TEST.VRC7.4S").LoadImage(fourScreen));
        Assert.Throws<NotSupportedException>(() => new KonamiVrc7Cartridge("TEST.VRC7.CHRRAM").LoadImage(chrRam));
        Assert.Throws<NotSupportedException>(() => new KonamiVrc7Cartridge("TEST.VRC7.NOCHR").LoadImage(noChr));
        Assert.Throws<NotSupportedException>(() => new KonamiVrc7Cartridge("TEST.VRC7.BADPRG").LoadImage(badPrg));
        Assert.Throws<NotSupportedException>(() => new KonamiVrc7Cartridge("TEST.VRC7.BIGCHR").LoadImage(tooMuchChr));
        Assert.Throws<NotSupportedException>(() => new KonamiVrc7Cartridge("TEST.VRC7.BADRAM").LoadImage(badRam));
    }

    private static KonamiVrc7Cartridge CreateCartridge(int prgBanks, int chrBanks, int workRamBytes = 0)
    {
        var cartridge = new KonamiVrc7Cartridge("TEST.KONAMI.VRC7");
        cartridge.LoadImage(CreateImage(CreatePrg(prgBanks), CreateChr(chrBanks), workRamBytes));
        return cartridge;
    }

    private static VirtualHardwareNesRomImage CreateImage(byte[] prg, byte[] chr, int workRamBytes)
    {
        return new VirtualHardwareNesRomImage(
            VirtualHardwareNesHeaderFormat.Nes20,
            85,
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

    private static VirtualHardwareNesRomImage CreateChrRamImage(byte[] prg, int chrRamBytes)
    {
        return new VirtualHardwareNesRomImage(
            VirtualHardwareNesHeaderFormat.Nes20,
            85,
            0,
            prg.Length,
            0,
            false,
            false,
            VirtualHardwareNesMirroring.Vertical,
            VirtualHardwareNesHeaderTiming.Ntsc,
            prg,
            [])
        {
            PrgRamSizeBytes = 0,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = chrRamBytes,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        };
    }

    private static VirtualHardwareNesRomImage CreateChrRamExecutionImage()
    {
        var prg = CreatePrg(16);
        var program = new List<byte> { 0x78, 0xD8 };
        AddSta(program, 0xA000, 0x07);
        AddSta(program, 0x2006, 0x00);
        AddSta(program, 0x2006, 0x00);
        AddSta(program, 0x2007, 0x5A);
        var loopAddress = (ushort)(0xE000 + program.Count);
        program.AddRange(new byte[] { 0x4C, (byte)loopAddress, (byte)(loopAddress >> 8) });

        var fixedLast = 15 * 8 * 1024;
        program.ToArray().CopyTo(prg, fixedLast);
        prg[fixedLast + 0x1FFA] = 0x00; prg[fixedLast + 0x1FFB] = 0xE0;
        prg[fixedLast + 0x1FFC] = 0x00; prg[fixedLast + 0x1FFD] = 0xE0;
        prg[fixedLast + 0x1FFE] = 0x00; prg[fixedLast + 0x1FFF] = 0xE0;
        return CreateChrRamImage(prg, 8 * 1024);
    }

    private static VirtualHardwareNesRomImage CreateExecutionImage()
    {
        var prg = CreatePrg(16);
        var program = new List<byte> { 0x78, 0xD8 };
        AddSta(program, 0x8000, 0x05);
        AddSta(program, 0x8008, 0x06);
        AddSta(program, 0x9000, 0x07);
        ushort[] chrRegisters = [0xA000,0xA008,0xB000,0xB008,0xC000,0xC008,0xD000,0xD008];
        for (var slot = 0; slot < 8; slot++) AddSta(program, chrRegisters[slot], (byte)(slot + 1));
        AddSta(program, 0xE000, 0x83);
        AddSta(program, 0x6000, 0x5A);
        AddSta(program, 0x9010, 0x10); AddSta(program, 0x9030, 0x34);
        AddSta(program, 0x9010, 0x20); AddSta(program, 0x9030, 0x15);
        AddSta(program, 0x9010, 0x30); AddSta(program, 0x9030, 0x10);
        AddSta(program, 0xE008, 0xFE);
        AddSta(program, 0xF000, 0x06);
        var loopAddress = (ushort)(0xE000 + program.Count);
        program.AddRange(new byte[] { 0x4C, (byte)loopAddress, (byte)(loopAddress >> 8) });

        var fixedLast = 15 * 8 * 1024;
        program.ToArray().CopyTo(prg, fixedLast);
        prg[fixedLast + 0x1FFA] = 0x00; prg[fixedLast + 0x1FFB] = 0xE0;
        prg[fixedLast + 0x1FFC] = 0x00; prg[fixedLast + 0x1FFD] = 0xE0;
        prg[fixedLast + 0x1FFE] = 0x00; prg[fixedLast + 0x1FFF] = 0xE0;
        return CreateImage(prg, CreateChr(64), 8 * 1024);
    }

    private static void AddSta(List<byte> program, ushort address, byte value) =>
        program.AddRange(new byte[] { 0xA9, value, 0x8D, (byte)address, (byte)(address >> 8) });

    private static void WriteAudio(KonamiVrc7Audio audio, byte register, byte value)
    {
        audio.WritePort(0x9010, register);
        audio.WritePort(0x9030, value);
    }

    private static CompiledBusTargetDescriptor CpuRomTarget(KonamiVrc7Cartridge cartridge) =>
        Assert.Single(((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.AddressPins.Count == 15 && target.ObserveBusCycle is not null);

    private static CompiledBusTargetDescriptor CpuWorkRamTarget(KonamiVrc7Cartridge cartridge) =>
        Assert.Single(((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.AddressPins.Count == 15 && target.ObserveBusCycle is null);

    private static CompiledBusTargetDescriptor PpuTarget(KonamiVrc7Cartridge cartridge) =>
        Assert.Single(((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.AddressPins.Count == 14);

    private static void WriteHigh(CompiledBusTargetDescriptor cpu, ushort address, byte value) => cpu.Write!(address & 0x7FFF, value);

    private static void ClockCpu(CompiledBusTargetDescriptor cpu, int cycles)
    {
        var observer = Assert.IsType<Action<bool>>(cpu.ObserveBusCycle);
        for (var cycle = 0; cycle < cycles; cycle++) observer(false);
    }

    private static DigitalLevel EvaluateOutput(KonamiVrc7Cartridge cartridge, DigitalPin output, ushort address)
    {
        var found = ((ICompiledCombinationalComponent)cartridge).TryEvaluateCompiledOutput(
            output,
            pin => SamplePpuAddress(cartridge, pin, address),
            out var drive);
        Assert.True(found);
        return drive.Level;
    }

    private static DigitalLevel SamplePpuAddress(KonamiVrc7Cartridge cartridge, DigitalPin pin, ushort address)
    {
        for (var bit = 0; bit < cartridge.PpuAddress.Width; bit++)
        {
            if (!ReferenceEquals(pin, cartridge.PpuAddress.Pins[bit])) continue;
            return (address & (1 << bit)) != 0 ? DigitalLevel.High : DigitalLevel.Low;
        }
        return DigitalLevel.Unknown;
    }
}
