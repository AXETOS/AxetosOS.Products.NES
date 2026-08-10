using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Machines.Nes;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareSunsoftFme7CartridgeTests
{
    [Fact]
    public void Power_on_exposes_three_switchable_prg_windows_fixed_last_bank_and_eight_chr_windows()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuTarget(cartridge);

        Assert.Equal((byte)0x40, cpu.Read!(0x0000));
        Assert.Equal((byte)0x41, cpu.Read!(0x2000));
        Assert.Equal((byte)0x42, cpu.Read!(0x4000));
        Assert.Equal((byte)0x4F, cpu.Read!(0x6000));
        Assert.Equal(new[] { 0, 1, 2, 15 }, cartridge.PrgWindowBanks.ToArray());
        Assert.Equal(new byte[] { 0, 1, 2 }, cartridge.PrgBankRegisters.ToArray());
        for (var slot = 0; slot < 8; slot++)
            Assert.Equal((byte)(0x80 + slot), ppu.Read!(slot * 0x400));
        Assert.Equal(0UL, cartridge.PpuWriteCount);
    }

    [Fact]
    public void Command_and_parameter_ports_control_three_independent_eight_kib_prg_banks()
    {
        var cartridge = CreateCartridge(prgBanks: 64, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);

        WriteCommand(cpu, 0x09, 0x25);
        WriteCommand(cpu, 0x0A, 0x16);
        WriteCommand(cpu, 0x0B, 0x37);

        Assert.Equal(new byte[] { 0x25, 0x16, 0x37 }, cartridge.PrgBankRegisters.ToArray());
        Assert.Equal(new[] { 37, 22, 55, 63 }, cartridge.PrgWindowBanks.ToArray());
        Assert.Equal((byte)(0x40 + 37), cpu.Read!(0x0000));
        Assert.Equal((byte)(0x40 + 22), cpu.Read!(0x2000));
        Assert.Equal((byte)(0x40 + 55), cpu.Read!(0x4000));
        Assert.Equal((byte)(0x40 + 63), cpu.Read!(0x6000));
        Assert.Equal((byte)0x0B, cartridge.CommandRegister);
    }

    [Fact]
    public void Fitted_prg_address_lines_mask_six_bit_bank_register_outputs()
    {
        var cartridge = CreateCartridge(prgBanks: 8, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);

        WriteCommand(cpu, 0x09, 0x3E);

        Assert.Equal((byte)0x3E, cartridge.PrgBankRegisters[0]);
        Assert.Equal(6, cartridge.PrgWindowBanks[0]);
        Assert.Equal((byte)0x46, cpu.Read!(0x0000));
    }

    [Fact]
    public void Eight_command_registers_control_independent_one_kib_chr_windows()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 64);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuTarget(cartridge);
        var values = new byte[] { 9, 10, 11, 12, 13, 14, 15, 16 };

        for (byte slot = 0; slot < 8; slot++) WriteCommand(cpu, slot, values[slot]);

        Assert.Equal(values, cartridge.ChrBankRegisters.ToArray());
        for (var slot = 0; slot < 8; slot++)
            Assert.Equal((byte)(0x80 + values[slot]), ppu.Read!(slot * 0x400));
    }

    [Fact]
    public void Fitted_chr_address_lines_mask_full_eight_bit_chr_register_outputs()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 16);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuTarget(cartridge);

        WriteCommand(cpu, 0x00, 0x2F);

        Assert.Equal((byte)0x2F, cartridge.ChrBankRegisters[0]);
        Assert.Equal(15, cartridge.ChrWindowBanks[0]);
        Assert.Equal((byte)0x8F, ppu.Read!(0x0000));
    }

    [Fact]
    public void Six_thousand_window_can_select_prg_rom_or_enabled_banked_work_ram_or_open_bus()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32, wramBanks: 2);
        var cpu = CpuRomTarget(cartridge);
        var window = Cpu6000Target(cartridge);
        var selected = Assert.IsType<Func<int, bool, bool>>(window.IsSelected);

        WriteCommand(cpu, 0x08, 0x05);
        Assert.True(cartridge.Prg6000RomSelected);
        Assert.Equal(5, cartridge.Prg6000Bank);
        Assert.True(selected(0x6000, false));
        Assert.False(selected(0x6000, true));
        Assert.Equal((byte)0x45, window.Read!(0x6000));
        Assert.Equal(1UL, cartridge.Prg6000RomReadCount);

        WriteCommand(cpu, 0x08, 0x40);
        Assert.True(cartridge.Prg6000RamSelected);
        Assert.False(cartridge.Prg6000RamEnabled);
        Assert.False(selected(0x6000, false));
        Assert.False(selected(0x6000, true));

        WriteCommand(cpu, 0x08, 0xC0);
        Assert.True(selected(0x6000, false));
        Assert.True(selected(0x6000, true));
        window.Write!(0x6000, 0xA6);

        WriteCommand(cpu, 0x08, 0xC1);
        window.Write!(0x6000, 0x5C);
        Assert.Equal(1, cartridge.WramBank);
        Assert.Equal((byte)0x5C, window.Read!(0x6000));

        WriteCommand(cpu, 0x08, 0xC0);
        Assert.Equal((byte)0xA6, window.Read!(0x6000));
        Assert.Equal((byte)0xA6, cartridge.InspectWramByte(0));
        Assert.Equal((byte)0x5C, cartridge.InspectWramByte(8 * 1024));
        Assert.Equal(2UL, cartridge.WramWriteCount);
        Assert.Equal(2UL, cartridge.WramReadCount);
    }

    [Fact]
    public void Legacy_nonbattery_mapper_sixty_nine_does_not_invent_work_ram_but_battery_image_fits_eight_kib()
    {
        var noBattery = CreateImage(CreatePrg(16), CreateChr(32), wramBanks: 0,
            header: VirtualHardwareNesHeaderFormat.INes, battery: false);
        var battery = CreateImage(CreatePrg(16), CreateChr(32), wramBanks: 0,
            header: VirtualHardwareNesHeaderFormat.INes, battery: true);
        var plain = new SunsoftFme7Cartridge("TEST.SUNSOFT.PLAIN");
        var saved = new SunsoftFme7Cartridge("TEST.SUNSOFT.SAVED");

        plain.LoadImage(noBattery);
        saved.LoadImage(battery);

        Assert.Equal(0, plain.WramSizeBytes);
        Assert.Equal(8 * 1024, saved.WramSizeBytes);
    }

    [Theory]
    [InlineData(0x00, SunsoftFme7NametableMode.Vertical, DigitalLevel.High)]
    [InlineData(0x01, SunsoftFme7NametableMode.Horizontal, DigitalLevel.Low)]
    [InlineData(0x02, SunsoftFme7NametableMode.SingleScreenPage0, DigitalLevel.Low)]
    [InlineData(0x03, SunsoftFme7NametableMode.SingleScreenPage1, DigitalLevel.High)]
    public void Mirroring_command_exposes_all_four_live_ciram_routes(byte value, SunsoftFme7NametableMode expected, DigitalLevel expectedAt0400)
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);
        var dynamic = Assert.IsAssignableFrom<ICompiledCombinationalComponent>(cartridge);
        var statik = Assert.IsAssignableFrom<ICompiledStaticCombinationalComponent>(cartridge);

        WriteCommand(cpu, 0x0C, value);

        Assert.Equal(expected, cartridge.NametableMode);
        Assert.False(statik.TryEvaluateCompiledStaticOutput(cartridge.CiramA10, pin => SamplePpuAddress(cartridge, pin, 0x0400), out _));
        Assert.True(dynamic.TryEvaluateCompiledOutput(cartridge.CiramA10, pin => SamplePpuAddress(cartridge, pin, 0x0400), out var drive));
        Assert.Equal(expectedAt0400, drive.Level);
    }

    [Fact]
    public void Irq_low_and_high_commands_directly_program_sixteen_bit_counter_and_underflow_asserts()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);

        WriteCommand(cpu, 0x0E, 0x01);
        WriteCommand(cpu, 0x0F, 0x00);
        WriteCommand(cpu, 0x0D, 0x81);
        ClockCpu(cpu, 2);

        Assert.Equal((ushort)0xFFFF, cartridge.IrqCounter);
        Assert.True(cartridge.IrqCounterEnabled);
        Assert.True(cartridge.IrqOutputEnabled);
        Assert.True(cartridge.IrqAsserted);
        Assert.Equal(2UL, cartridge.IrqClockCount);
        Assert.Equal(1UL, cartridge.IrqAssertCount);
    }

    [Fact]
    public void Irq_counter_enable_and_output_enable_are_independent_and_control_write_acknowledges_irq()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);

        WriteCommand(cpu, 0x0E, 0x00);
        WriteCommand(cpu, 0x0F, 0x00);
        WriteCommand(cpu, 0x0D, 0x80);
        ClockCpu(cpu, 1);
        Assert.Equal((ushort)0xFFFF, cartridge.IrqCounter);
        Assert.False(cartridge.IrqAsserted);

        WriteCommand(cpu, 0x0D, 0x81);
        Assert.False(cartridge.IrqAsserted);
        ClockCpu(cpu, 1);
        Assert.Equal((ushort)0xFFFE, cartridge.IrqCounter);
        Assert.False(cartridge.IrqAsserted);

        WriteCommand(cpu, 0x0E, 0x00);
        WriteCommand(cpu, 0x0F, 0x00);
        ClockCpu(cpu, 1);
        Assert.True(cartridge.IrqAsserted);

        WriteCommand(cpu, 0x0D, 0x00);
        Assert.False(cartridge.IrqAsserted);
        Assert.False(cartridge.IrqCounterEnabled);
        Assert.False(cartridge.IrqOutputEnabled);
    }

    [Fact]
    public void Irq_output_is_dynamic_open_collector_state_and_not_static_foldable()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);
        var dynamic = Assert.IsAssignableFrom<ICompiledCombinationalComponent>(cartridge);
        var statik = Assert.IsAssignableFrom<ICompiledStaticCombinationalComponent>(cartridge);

        Assert.False(statik.TryEvaluateCompiledStaticOutput(cartridge.IrqBar, _ => DigitalLevel.Unknown, out _));
        Assert.True(dynamic.TryEvaluateCompiledOutput(cartridge.IrqBar, _ => DigitalLevel.Unknown, out var idle));
        Assert.Equal(DigitalLevel.HighImpedance, idle.Level);

        WriteCommand(cpu, 0x0E, 0x00);
        WriteCommand(cpu, 0x0F, 0x00);
        WriteCommand(cpu, 0x0D, 0x81);
        ClockCpu(cpu, 1);
        Assert.True(dynamic.TryEvaluateCompiledOutput(cartridge.IrqBar, _ => DigitalLevel.Unknown, out var asserted));
        Assert.Equal(DigitalLevel.Low, asserted.Level);
    }

    [Fact]
    public void Raw_physical_m2_falling_edges_clock_irq_and_psg_without_mapper_chip_select()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);
        WriteCommand(cpu, 0x0E, 0x01);
        WriteCommand(cpu, 0x0F, 0x00);
        WriteCommand(cpu, 0x0D, 0x81);

        var board = new VirtualHardwareBoard("SUNSOFT.IRQ.PHYSICAL");
        board.Add(cartridge);
        var vcc = AttachSource(board, "VCC", cartridge.Vcc);
        var gnd = AttachSource(board, "GND", cartridge.Gnd);
        var m2 = AttachSource(board, "M2", cartridge.CpuM2);
        _ = new VirtualHardwareSimulator(board);
        gnd.Set(DigitalLevel.Low);
        m2.Set(DigitalLevel.Low);
        vcc.Set(DigitalLevel.High);

        for (var cycle = 0; cycle < 16; cycle++)
        {
            m2.Set(DigitalLevel.High);
            m2.Set(DigitalLevel.Low);
        }

        Assert.Equal(16UL, cartridge.CpuCycleClockCount);
        Assert.Equal(16UL, cartridge.IrqClockCount);
        Assert.Equal(16UL, cartridge.Psg.CpuClockCount);
        Assert.Equal(1UL, cartridge.Psg.GeneratorTickCount);
        Assert.True(cartridge.IrqAsserted);
    }

    [Fact]
    public void Sunsoft_5b_select_and_data_ports_latch_registers_and_high_select_nibble_disables_data_writes()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);

        WritePsg(cpu, 0x00, 0x34);
        WritePsg(cpu, 0x01, 0x12);
        Assert.Equal((ushort)0x0234, cartridge.Psg.TonePeriodA);

        cpu.Write!(0x4000, 0xF0);
        cpu.Write!(0x6000, 0xAA);
        Assert.True(cartridge.Psg.DataWritesDisabled);
        Assert.Equal((byte)0x34, cartridge.Psg.Registers[0]);
        Assert.Equal(1UL, cartridge.Psg.IgnoredDataWriteCount);

        cpu.Write!(0x4000, 0x00);
        cpu.Write!(0x6000, 0x56);
        Assert.False(cartridge.Psg.DataWritesDisabled);
        Assert.Equal((byte)0x56, cartridge.Psg.Registers[0]);
    }

    [Fact]
    public void Sunsoft_5b_tone_generator_clocks_once_per_sixteen_cpu_cycles_and_period_zero_behaves_as_one()
    {
        var psg = new Sunsoft5bPsg();
        psg.WriteRegisterSelect(0x07); psg.WriteRegisterData(0x3E);
        psg.WriteRegisterSelect(0x08); psg.WriteRegisterData(0x0F);
        psg.WriteRegisterSelect(0x00); psg.WriteRegisterData(0x00);
        psg.WriteRegisterSelect(0x01); psg.WriteRegisterData(0x00);
        var initialOutput = psg.ToneOutputs[0];
        Assert.False(initialOutput);
        Assert.Equal((byte)0, psg.MixedDacLevel);

        ClockPsgCpu(psg, 15);
        Assert.Equal(initialOutput, psg.ToneOutputs[0]);
        ClockPsgCpu(psg, 1);
        Assert.NotEqual(initialOutput, psg.ToneOutputs[0]);
        Assert.Equal(3UL, psg.ToneFlipCount);
        Assert.True(psg.MixedDacLevel > 0);

        ClockPsgCpu(psg, 16);
        Assert.Equal(initialOutput, psg.ToneOutputs[0]);
        Assert.Equal((byte)0, psg.MixedDacLevel);
    }

    [Fact]
    public void Sunsoft_5b_noise_generator_uses_seventeen_bit_lfsr_and_advances_every_thirty_two_cpu_clocks_at_period_one()
    {
        var psg = new Sunsoft5bPsg();
        psg.WriteRegisterSelect(0x06); psg.WriteRegisterData(0x01);
        var initial = psg.NoiseLfsr;
        Assert.Equal(1u, initial);

        ClockPsgCpu(psg, 31);
        Assert.Equal(initial, psg.NoiseLfsr);
        ClockPsgCpu(psg, 1);

        Assert.Equal(0x10000u, psg.NoiseLfsr);
        Assert.False(psg.NoiseOutput);
        Assert.Equal(1UL, psg.NoiseShiftCount);
    }

    [Theory]
    [InlineData(0x09, 0)]
    [InlineData(0x0B, 31)]
    [InlineData(0x0D, 31)]
    [InlineData(0x0F, 0)]
    public void Sunsoft_5b_hold_envelope_shapes_reach_documented_terminal_level(byte shape, int terminalLevel)
    {
        var psg = new Sunsoft5bPsg();
        psg.WriteRegisterSelect(0x0B); psg.WriteRegisterData(0x01);
        psg.WriteRegisterSelect(0x0C); psg.WriteRegisterData(0x00);
        psg.WriteRegisterSelect(0x0D); psg.WriteRegisterData(shape);

        ClockPsgGeneratorTicks(psg, 32);

        Assert.Equal(terminalLevel, psg.EnvelopeLevel);
        Assert.True(psg.EnvelopeHolding);
    }

    [Fact]
    public void Sunsoft_5b_triangle_envelope_reverses_without_holding()
    {
        var psg = new Sunsoft5bPsg();
        psg.WriteRegisterSelect(0x0B); psg.WriteRegisterData(0x01);
        psg.WriteRegisterSelect(0x0C); psg.WriteRegisterData(0x00);
        psg.WriteRegisterSelect(0x0D); psg.WriteRegisterData(0x0A);

        ClockPsgGeneratorTicks(psg, 31);
        Assert.Equal(0, psg.EnvelopeLevel);
        ClockPsgGeneratorTicks(psg, 1);
        Assert.Equal(0, psg.EnvelopeLevel);
        ClockPsgGeneratorTicks(psg, 31);
        Assert.Equal(31, psg.EnvelopeLevel);
        ClockPsgGeneratorTicks(psg, 1);
        Assert.Equal(31, psg.EnvelopeLevel);
        Assert.False(psg.EnvelopeHolding);
    }

    [Fact]
    public void Mapper_and_audio_register_writes_latch_at_completed_cpu_bus_phase()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        Assert.Equal(CompiledBusWritePhase.Complete, CpuRomTarget(cartridge).WritePhase);
        Assert.Equal(CompiledBusCycleObservationPhase.Complete, CpuRomTarget(cartridge).ObserveBusCyclePhase);
        Assert.Equal(CompiledBusWritePhase.Complete, Cpu6000Target(cartridge).WritePhase);
    }

    [Fact]
    public void Generic_compiled_and_raw_physical_fme7_execute_same_banking_mirroring_irq_and_psg_program()
    {
        var image = CreateExecutionImage();
        var compiled = new RegionalNesVirtualMachine();
        var reference = new RegionalNesVirtualMachine();

        compiled.SetCompiledLabExecutionEnabled(true);
        compiled.InsertRom(image, "Sunsoft FME-7 execution (Japan).nes");
        reference.InsertRom(image, "Sunsoft FME-7 execution (Japan).nes");
        reference.Famicom.SetCompiledPhysicalMachineEnabled(false);

        compiled.PowerOn();
        reference.PowerOn();
        compiled.ReleaseReset();
        reference.ReleaseReset();

        const int masterCycles = 60_000;
        compiled.AdvanceMasterCycles(masterCycles);
        reference.AdvanceMasterCycles(masterCycles);

        var actual = Assert.IsType<SunsoftFme7Cartridge>(compiled.Slot.Cartridge);
        var expected = Assert.IsType<SunsoftFme7Cartridge>(reference.Slot.Cartridge);
        Assert.True(compiled.Famicom.CompiledLabMotherboardEnabled);
        Assert.Equal(new byte[] { 5, 6, 7 }, actual.PrgBankRegisters.ToArray());
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, actual.ChrBankRegisters.ToArray());
        Assert.Equal(SunsoftFme7NametableMode.SingleScreenPage1, actual.NametableMode);
        Assert.True(actual.IrqAsserted);
        Assert.Equal((byte)0x0F, actual.Psg.Registers[0x08]);
        Assert.Equal((byte)0x3E, actual.Psg.Registers[0x07]);
        Assert.Equal(expected.PrgWindowBanks.ToArray(), actual.PrgWindowBanks.ToArray());
        Assert.Equal(expected.ChrWindowBanks.ToArray(), actual.ChrWindowBanks.ToArray());
        Assert.Equal(expected.IrqCounter, actual.IrqCounter);
        Assert.Equal(expected.IrqAsserted, actual.IrqAsserted);
        Assert.Equal(expected.IrqClockCount, actual.IrqClockCount);
        Assert.Equal(expected.CpuCycleClockCount, actual.CpuCycleClockCount);
        Assert.Equal(expected.MapperWriteCount, actual.MapperWriteCount);
        Assert.Equal(expected.Psg.RegisterDataWriteCount, actual.Psg.RegisterDataWriteCount);
        Assert.Equal(expected.Psg.GeneratorTickCount, actual.Psg.GeneratorTickCount);
        Assert.Equal(reference.Famicom.Cpu.ProgramCounter, compiled.Famicom.Cpu.ProgramCounter);
        Assert.Equal(reference.Famicom.Cpu.CompletedInstructionCount, compiled.Famicom.Cpu.CompletedInstructionCount);
        Assert.Equal(reference.Famicom.Ppu.InspectDiagnosticState(), compiled.Famicom.Ppu.InspectDiagnosticState());
        Assert.Equal(reference.Famicom.Ciram.InspectStateHash(), compiled.Famicom.Ciram.InspectStateHash());
    }

    [Fact]
    public void Invalid_four_screen_chr_ram_bad_prg_and_non_bank_aligned_wram_sizes_are_rejected()
    {
        var basic = CreateImage(CreatePrg(16), CreateChr(32), wramBanks: 0);
        var fourScreen = basic with { Mirroring = VirtualHardwareNesMirroring.FourScreen };
        var chrRam = basic with { ChrRamSizeBytes = 8 * 1024 };
        var noChr = basic with { ChrRom = [], ChrRomSizeBytes = 0 };
        var badPrg = basic with { PrgRom = new byte[5 * 8 * 1024], PrgRomSizeBytes = 5 * 8 * 1024 };
        var badWram = basic with { PrgRamSizeBytes = 4 * 1024 };

        Assert.Throws<NotSupportedException>(() => new SunsoftFme7Cartridge("TEST.SUNSOFT.4S").LoadImage(fourScreen));
        Assert.Throws<NotSupportedException>(() => new SunsoftFme7Cartridge("TEST.SUNSOFT.CHRRAM").LoadImage(chrRam));
        Assert.Throws<NotSupportedException>(() => new SunsoftFme7Cartridge("TEST.SUNSOFT.NOCHR").LoadImage(noChr));
        Assert.Throws<NotSupportedException>(() => new SunsoftFme7Cartridge("TEST.SUNSOFT.BADPRG").LoadImage(badPrg));
        Assert.Throws<NotSupportedException>(() => new SunsoftFme7Cartridge("TEST.SUNSOFT.WRAM").LoadImage(badWram));
    }

    [Fact]
    public void Factory_constructs_mapper_sixty_nine_as_replaceable_sunsoft_family_hardware()
    {
        var image = CreateImage(CreatePrg(16), CreateChr(32), wramBanks: 0);
        var cartridge = VirtualCartridgeHardwareFactory.Create(image);

        Assert.Equal(69, cartridge.MapperNumber);
        Assert.IsType<SunsoftFme7Cartridge>(cartridge);
        Assert.True(cartridge.IsInserted);
    }

    private static SunsoftFme7Cartridge CreateCartridge(int prgBanks, int chrBanks, int wramBanks = 0)
    {
        var cartridge = new SunsoftFme7Cartridge("TEST.SUNSOFT.FME7");
        cartridge.LoadImage(CreateImage(CreatePrg(prgBanks), CreateChr(chrBanks), wramBanks));
        return cartridge;
    }

    private static byte[] CreatePrg(int banks)
    {
        var prg = new byte[banks * 8 * 1024];
        for (var bank = 0; bank < banks; bank++)
            Array.Fill(prg, (byte)(0x40 + (bank & 0x3F)), bank * 8 * 1024, 8 * 1024);
        return prg;
    }

    private static byte[] CreateChr(int banks)
    {
        var chr = new byte[banks * 1024];
        for (var bank = 0; bank < banks; bank++)
            Array.Fill(chr, (byte)(0x80 + (bank & 0x7F)), bank * 1024, 1024);
        return chr;
    }

    private static VirtualHardwareNesRomImage CreateImage(
        byte[] prg,
        byte[] chr,
        int wramBanks,
        VirtualHardwareNesHeaderFormat header = VirtualHardwareNesHeaderFormat.Nes20,
        bool battery = false) =>
        new(
            header,
            MapperNumber: 69,
            SubmapperNumber: header == VirtualHardwareNesHeaderFormat.Nes20 ? 0 : null,
            PrgRomSizeBytes: prg.Length,
            ChrRomSizeBytes: chr.Length,
            HasTrainer: false,
            HasBatteryBackedMemory: battery,
            VirtualHardwareNesMirroring.Vertical,
            VirtualHardwareNesHeaderTiming.Ntsc,
            prg,
            chr)
        {
            PrgRamSizeBytes = wramBanks * 8 * 1024,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 0,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = header == VirtualHardwareNesHeaderFormat.Nes20
        };

    private static VirtualHardwareNesRomImage CreateExecutionImage()
    {
        var prg = CreatePrg(16);
        var program = new List<byte> { 0x78 }; // SEI

        for (byte slot = 0; slot < 8; slot++) AddCommand(program, slot, (byte)(slot + 1));
        AddCommand(program, 0x09, 5);
        AddCommand(program, 0x0A, 6);
        AddCommand(program, 0x0B, 7);
        AddCommand(program, 0x0C, 3);
        AddCommand(program, 0x0E, 3);
        AddCommand(program, 0x0F, 0);
        AddCommand(program, 0x0D, 0x81);
        AddPsgWrite(program, 0x00, 0x01);
        AddPsgWrite(program, 0x01, 0x00);
        AddPsgWrite(program, 0x07, 0x3E);
        AddPsgWrite(program, 0x08, 0x0F);

        var loopAddress = (ushort)(0xE000 + program.Count);
        program.AddRange(new byte[] { 0x4C, (byte)loopAddress, (byte)(loopAddress >> 8) });

        var fixedLast = 15 * 8 * 1024;
        program.ToArray().CopyTo(prg, fixedLast);
        prg[fixedLast + 0x1FFA] = 0x00; prg[fixedLast + 0x1FFB] = 0xE0;
        prg[fixedLast + 0x1FFC] = 0x00; prg[fixedLast + 0x1FFD] = 0xE0;
        prg[fixedLast + 0x1FFE] = 0x00; prg[fixedLast + 0x1FFF] = 0xE0;
        return CreateImage(prg, CreateChr(64), wramBanks: 0);
    }

    private static void AddCommand(List<byte> program, byte command, byte value)
    {
        AddSta(program, 0x8000, command);
        AddSta(program, 0xA000, value);
    }

    private static void AddPsgWrite(List<byte> program, byte register, byte value)
    {
        AddSta(program, 0xC000, register);
        AddSta(program, 0xE000, value);
    }

    private static void AddSta(List<byte> program, ushort address, byte value) =>
        program.AddRange(new byte[] { 0xA9, value, 0x8D, (byte)address, (byte)(address >> 8) });

    private static CompiledBusTargetDescriptor CpuRomTarget(SunsoftFme7Cartridge cartridge) =>
        Assert.Single(
            ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.AddressPins.Count == 15
                && target.DataPins.Count == 8
                && target.Read is not null
                && target.IsSelected is null);

    private static CompiledBusTargetDescriptor Cpu6000Target(SunsoftFme7Cartridge cartridge) =>
        Assert.Single(
            ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.AddressPins.Count == 15
                && target.IsSelected is not null);

    private static CompiledBusTargetDescriptor PpuTarget(SunsoftFme7Cartridge cartridge) =>
        Assert.Single(
            ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.AddressPins.Count == 14);

    private static void WriteCommand(CompiledBusTargetDescriptor cpu, byte command, byte value)
    {
        cpu.Write!(0x0000, command);
        cpu.Write!(0x2000, value);
    }

    private static void WritePsg(CompiledBusTargetDescriptor cpu, byte register, byte value)
    {
        cpu.Write!(0x4000, register);
        cpu.Write!(0x6000, value);
    }

    private static void ClockCpu(CompiledBusTargetDescriptor cpu, int cycles)
    {
        var observer = Assert.IsType<Action<bool>>(cpu.ObserveBusCycle);
        for (var index = 0; index < cycles; index++) observer(false);
    }

    private static void ClockPsgCpu(Sunsoft5bPsg psg, int cycles)
    {
        for (var index = 0; index < cycles; index++) psg.ClockCpuCycle();
    }

    private static void ClockPsgGeneratorTicks(Sunsoft5bPsg psg, int ticks) => ClockPsgCpu(psg, ticks * 16);

    private static DigitalLevel SamplePpuAddress(SunsoftFme7Cartridge cartridge, DigitalPin pin, ushort address)
    {
        for (var bit = 0; bit < cartridge.PpuAddress.Width; bit++)
        {
            if (!ReferenceEquals(pin, cartridge.PpuAddress.Pins[bit])) continue;
            return (address & (1 << bit)) != 0 ? DigitalLevel.High : DigitalLevel.Low;
        }
        return DigitalLevel.Unknown;
    }

    private static DigitalSignalSource AttachSource(VirtualHardwareBoard board, string id, DigitalPin pin)
    {
        var source = board.Add(new DigitalSignalSource($"TEST.{id}", DigitalLevel.HighImpedance));
        board.Connect($"TEST.{id}.NET", source.Output, pin);
        return source;
    }
}
