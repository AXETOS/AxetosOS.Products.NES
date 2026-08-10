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

public sealed class VirtualHardwareBandaiFcgCartridgeTests
{
    [Fact]
    public void Power_on_maps_prg_bank_zero_and_fixed_last_bank()
    {
        var cartridge = CreateCartridge(BandaiFcgVariant.Lz93D50, prgBanks: 16, chrBanks: 256);
        var cpu = CpuRomTarget(cartridge);

        Assert.Equal((byte)0x40, cpu.Read!(0x0000));
        Assert.Equal((byte)0x40, cpu.Read!(0x3FFF));
        Assert.Equal((byte)0x4F, cpu.Read!(0x4000));
        Assert.Equal((byte)0x4F, cpu.Read!(0x7FFF));
        Assert.Equal(0, cartridge.SelectedPrgBank);
        Assert.Equal(15, cartridge.FixedPrgBank);
    }

    [Fact]
    public void Eight_chr_registers_independently_select_one_kib_windows()
    {
        var cartridge = CreateCartridge(BandaiFcgVariant.Lz93D50, prgBanks: 8, chrBanks: 64);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuTarget(cartridge);

        for (var slot = 0; slot < 8; slot++)
            cpu.Write!(slot, (byte)(slot + 9));

        for (var slot = 0; slot < 8; slot++)
            Assert.Equal((byte)(0x80 + slot + 9), ppu.Read!(slot * 0x400));

        Assert.Equal(8UL, cartridge.MapperWriteCount);
    }

    [Fact]
    public void Fitted_prg_and_chr_address_lines_mask_bank_outputs()
    {
        var cartridge = CreateCartridge(BandaiFcgVariant.Lz93D50, prgBanks: 4, chrBanks: 16);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuTarget(cartridge);

        cpu.Write!(0x0008, 0x0F);
        cpu.Write!(0x0000, 0xFF);

        Assert.Equal(3, cartridge.SelectedPrgBank);
        Assert.Equal((byte)0x43, cpu.Read!(0x0000));
        Assert.Equal((byte)0x8F, ppu.Read!(0x0000));
    }

    [Theory]
    [InlineData(0x00, BandaiFcgNametableMode.Vertical, DigitalLevel.High)]
    [InlineData(0x01, BandaiFcgNametableMode.Horizontal, DigitalLevel.Low)]
    [InlineData(0x02, BandaiFcgNametableMode.SingleScreenPage0, DigitalLevel.Low)]
    [InlineData(0x03, BandaiFcgNametableMode.SingleScreenPage1, DigitalLevel.High)]
    public void Mirroring_register_exposes_all_four_live_ciram_routes(
        byte value,
        BandaiFcgNametableMode expectedMode,
        DigitalLevel expectedAt0400)
    {
        var cartridge = CreateCartridge(BandaiFcgVariant.Lz93D50, prgBanks: 8, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);
        var dynamic = Assert.IsAssignableFrom<ICompiledCombinationalComponent>(cartridge);
        var statik = Assert.IsAssignableFrom<ICompiledStaticCombinationalComponent>(cartridge);

        cpu.Write!(0x0009, value);

        Assert.Equal(expectedMode, cartridge.NametableMode);
        Assert.False(statik.TryEvaluateCompiledStaticOutput(
            cartridge.CiramA10,
            pin => SamplePpuAddress(cartridge, pin, 0x0400),
            out _));
        Assert.True(dynamic.TryEvaluateCompiledOutput(
            cartridge.CiramA10,
            pin => SamplePpuAddress(cartridge, pin, 0x0400),
            out var drive));
        Assert.Equal(expectedAt0400, drive.Level);
    }

    [Fact]
    public void Submapper_four_fcg12_responds_only_in_low_register_window()
    {
        var cartridge = CreateCartridge(BandaiFcgVariant.Fcg12, prgBanks: 8, chrBanks: 32);
        var targets = ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets().ToArray();
        var rom = CpuRomTarget(cartridge);
        var low = CpuLowRegisterTarget(cartridge);

        Assert.Null(rom.Write);
        Assert.Equal(0, cartridge.SelectedPrgBank);
        low.Write!(0x6008, 0x05);

        Assert.Equal(5, cartridge.SelectedPrgBank);
        Assert.DoesNotContain(targets, target => target.DataPins.Count == 1 && ReferenceEquals(target.DataPins[0], cartridge.CpuData.Pins[4]));
    }

    [Fact]
    public void Submapper_five_lz93d50_responds_only_in_high_register_window()
    {
        var cartridge = CreateCartridge(BandaiFcgVariant.Lz93D50, prgBanks: 8, chrBanks: 32);
        var targets = ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets().ToArray();
        var rom = CpuRomTarget(cartridge);

        Assert.DoesNotContain(targets, target => target.Write is not null
            && target.WriteConditions.Any(condition => ReferenceEquals(condition.Pin, cartridge.CpuRomSelectBar)
                && condition.RequiredLevel == DigitalLevel.High));

        rom.Write!(0x0008, 0x06);
        Assert.Equal(6, cartridge.SelectedPrgBank);
    }

    [Fact]
    public void Submapper_zero_exposes_both_historical_register_windows()
    {
        var cartridge = CreateCartridge(BandaiFcgVariant.Compatibility, prgBanks: 8, chrBanks: 32);
        var rom = CpuRomTarget(cartridge);
        var low = CpuLowRegisterTarget(cartridge);

        low.Write!(0x6008, 0x03);
        Assert.Equal(3, cartridge.SelectedPrgBank);
        rom.Write!(0x0008, 0x06);
        Assert.Equal(6, cartridge.SelectedPrgBank);
    }

    [Fact]
    public void Fcg12_irq_low_and_high_writes_modify_running_counter_directly()
    {
        var cartridge = CreateCartridge(BandaiFcgVariant.Fcg12, prgBanks: 8, chrBanks: 32);
        var low = CpuLowRegisterTarget(cartridge);

        low.Write!(0x600B, 0x34);
        low.Write!(0x600C, 0x12);

        Assert.Equal((ushort)0x1234, cartridge.IrqCounter);
        Assert.Equal((ushort)0x0000, cartridge.IrqLatch);
        low.Write!(0x600A, 0x01);
        Assert.Equal((ushort)0x1234, cartridge.IrqCounter);
        Assert.True(cartridge.IrqEnabled);
    }

    [Fact]
    public void Lz93d50_irq_uses_reload_latch_when_enabled()
    {
        var cartridge = CreateCartridge(BandaiFcgVariant.Lz93D50, prgBanks: 8, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);

        cpu.Write!(0x000B, 0x34);
        cpu.Write!(0x000C, 0x12);
        Assert.Equal((ushort)0x1234, cartridge.IrqLatch);
        Assert.Equal((ushort)0x0000, cartridge.IrqCounter);

        cpu.Write!(0x000A, 0x01);
        Assert.Equal((ushort)0x1234, cartridge.IrqCounter);
        Assert.True(cartridge.IrqEnabled);
    }

    [Fact]
    public void Cpu_cycle_observer_decrements_lz_irq_and_asserts_at_zero()
    {
        var cartridge = CreateCartridge(BandaiFcgVariant.Lz93D50, prgBanks: 8, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);
        cpu.Write!(0x000B, 0x03);
        cpu.Write!(0x000C, 0x00);
        cpu.Write!(0x000A, 0x01);

        ClockCpu(cpu, 2);
        Assert.Equal((ushort)1, cartridge.IrqCounter);
        Assert.False(cartridge.IrqAsserted);

        ClockCpu(cpu, 1);
        Assert.Equal((ushort)0xFFFF, cartridge.IrqCounter);
        Assert.True(cartridge.IrqAsserted);
        Assert.False(cartridge.IrqEnabled);
        Assert.Equal(3UL, cartridge.IrqClockCount);
        Assert.Equal(1UL, cartridge.IrqAssertCount);
    }

    [Fact]
    public void Irq_control_write_acknowledges_asserted_irq_and_can_leave_timer_disabled()
    {
        var cartridge = CreateCartridge(BandaiFcgVariant.Lz93D50, prgBanks: 8, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);
        cpu.Write!(0x000B, 0x01);
        cpu.Write!(0x000C, 0x00);
        cpu.Write!(0x000A, 0x01);
        ClockCpu(cpu, 1);
        Assert.True(cartridge.IrqAsserted);

        cpu.Write!(0x000A, 0x00);

        Assert.False(cartridge.IrqAsserted);
        Assert.False(cartridge.IrqEnabled);
    }

    [Fact]
    public void Irq_output_is_live_dynamic_open_collector_state_not_static_fold()
    {
        var cartridge = CreateCartridge(BandaiFcgVariant.Lz93D50, prgBanks: 8, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);
        var dynamic = Assert.IsAssignableFrom<ICompiledCombinationalComponent>(cartridge);
        var statik = Assert.IsAssignableFrom<ICompiledStaticCombinationalComponent>(cartridge);

        Assert.False(statik.TryEvaluateCompiledStaticOutput(cartridge.IrqBar, _ => DigitalLevel.Unknown, out _));
        Assert.True(dynamic.TryEvaluateCompiledOutput(cartridge.IrqBar, _ => DigitalLevel.Unknown, out var idle));
        Assert.Equal(DigitalLevel.HighImpedance, idle.Level);

        cpu.Write!(0x000B, 0x01);
        cpu.Write!(0x000C, 0x00);
        cpu.Write!(0x000A, 0x01);
        ClockCpu(cpu, 1);
        Assert.True(dynamic.TryEvaluateCompiledOutput(cartridge.IrqBar, _ => DigitalLevel.Unknown, out var asserted));
        Assert.Equal(DigitalLevel.Low, asserted.Level);
    }

    [Fact]
    public void Raw_physical_m2_falling_edges_clock_irq_even_when_cartridge_rom_is_not_selected()
    {
        var cartridge = CreateCartridge(BandaiFcgVariant.Lz93D50, prgBanks: 8, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);
        cpu.Write!(0x000B, 0x03);
        cpu.Write!(0x000C, 0x00);
        cpu.Write!(0x000A, 0x01);

        var board = new VirtualHardwareBoard("BANDAI.IRQ.PHYSICAL");
        board.Add(cartridge);
        var vcc = AttachSource(board, "VCC", cartridge.Vcc);
        var gnd = AttachSource(board, "GND", cartridge.Gnd);
        var m2 = AttachSource(board, "M2", cartridge.CpuM2);
        _ = new VirtualHardwareSimulator(board);
        gnd.Set(DigitalLevel.Low);
        m2.Set(DigitalLevel.Low);
        vcc.Set(DigitalLevel.High);

        for (var cycle = 0; cycle < 3; cycle++)
        {
            m2.Set(DigitalLevel.High);
            m2.Set(DigitalLevel.Low);
        }

        Assert.Equal((ushort)0xFFFF, cartridge.IrqCounter);
        Assert.True(cartridge.IrqAsserted);
        Assert.Equal(3UL, cartridge.IrqClockCount);
    }

    [Fact]
    public void Lz93d50_without_nvram_has_no_eeprom_data_target()
    {
        var cartridge = CreateCartridge(BandaiFcgVariant.Lz93D50, prgBanks: 8, chrBanks: 32, eeprom: false);

        Assert.Equal(0, cartridge.EepromSizeBytes);
        Assert.DoesNotContain(
            ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.DataPins.Count == 1 && ReferenceEquals(target.DataPins[0], cartridge.CpuData.Pins[4]));
    }

    [Fact]
    public void Lz93d50_with_256_byte_nvram_exposes_only_eeprom_data_on_cpu_d4()
    {
        var cartridge = CreateCartridge(BandaiFcgVariant.Lz93D50, prgBanks: 8, chrBanks: 32, eeprom: true);
        var target = EepromTarget(cartridge);

        Assert.Equal(256, cartridge.EepromSizeBytes);
        Assert.Single(target.DataPins);
        Assert.Same(cartridge.CpuData.Pins[4], target.DataPins[0]);
        Assert.Equal((byte)1, target.Read!(0x6000));
    }

    [Fact]
    public void Lz93d50_24c02_serial_write_stores_a_byte_through_i2c_lines()
    {
        var cartridge = CreateCartridge(BandaiFcgVariant.Lz93D50, prgBanks: 8, chrBanks: 32, eeprom: true);

        I2cStart(cartridge);
        I2cSendByte(cartridge, 0xA0);
        I2cAckClock(cartridge);
        I2cSendByte(cartridge, 0x2A);
        I2cAckClock(cartridge);
        I2cSendByte(cartridge, 0x5C);
        I2cAckClock(cartridge);
        I2cStop(cartridge);

        Assert.Equal((byte)0x5C, cartridge.InspectEepromByte(0x2A));
        Assert.Equal(1UL, cartridge.EepromWriteCount);
        Assert.True(cartridge.EepromControlWriteCount > 0);
    }

    [Fact]
    public void Lz93d50_24c02_serial_random_read_returns_written_byte_on_d4()
    {
        var cartridge = CreateCartridge(BandaiFcgVariant.Lz93D50, prgBanks: 8, chrBanks: 32, eeprom: true);
        I2cStart(cartridge);
        I2cSendByte(cartridge, 0xA0);
        I2cAckClock(cartridge);
        I2cSendByte(cartridge, 0x37);
        I2cAckClock(cartridge);
        I2cSendByte(cartridge, 0xA6);
        I2cAckClock(cartridge);
        I2cStop(cartridge);

        I2cStart(cartridge);
        I2cSendByte(cartridge, 0xA0);
        I2cAckClock(cartridge);
        I2cSendByte(cartridge, 0x37);
        I2cAckClock(cartridge);
        I2cRepeatedStart(cartridge);
        I2cSendByte(cartridge, 0xA1);
        I2cAckClock(cartridge);
        var value = I2cReadByteAndNack(cartridge);
        I2cStop(cartridge);

        Assert.Equal((byte)0xA6, value);
        Assert.Equal(1UL, cartridge.EepromReadCount);
    }

    [Fact]
    public void Legacy_ines_battery_flag_maps_to_lz_256_byte_serial_eeprom_not_fake_8k_ram()
    {
        var image = CreateImage(BandaiFcgVariant.Compatibility, CreatePrg(8), CreateChr(32), eeprom: false,
            headerFormat: VirtualHardwareNesHeaderFormat.INes, battery: true) with
        {
            PrgRamSizeBytes = 0,
            PrgNvRamSizeBytes = 8 * 1024,
            ChrRamSizeBytes = 0,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = false
        };
        var cartridge = new BandaiFcgCartridge("TEST.BANDAI.LEGACY.EEPROM");

        cartridge.LoadImage(image);

        Assert.Equal(BandaiFcgVariant.Compatibility, cartridge.Variant);
        Assert.Equal(256, cartridge.EepromSizeBytes);
    }

    [Fact]
    public void Mapper_and_eeprom_control_writes_latch_at_completed_cpu_bus_phase()
    {
        var cartridge = CreateCartridge(BandaiFcgVariant.Lz93D50, prgBanks: 8, chrBanks: 32, eeprom: true);

        Assert.Equal(CompiledBusWritePhase.Complete, CpuRomTarget(cartridge).WritePhase);
    }

    [Fact]
    public void Generic_compiled_and_raw_physical_bandai_execute_same_banking_mirroring_and_irq_program()
    {
        var image = CreateExecutionImage();
        var compiled = new RegionalNesVirtualMachine();
        var reference = new RegionalNesVirtualMachine();

        compiled.SetCompiledLabExecutionEnabled(true);
        compiled.InsertRom(image, "Bandai FCG execution (Japan).nes");
        reference.InsertRom(image, "Bandai FCG execution (Japan).nes");
        reference.Famicom.SetCompiledPhysicalMachineEnabled(false);

        compiled.PowerOn();
        reference.PowerOn();
        compiled.ReleaseReset();
        reference.ReleaseReset();

        const int masterCycles = 60_000;
        compiled.AdvanceMasterCycles(masterCycles);
        reference.AdvanceMasterCycles(masterCycles);

        var actual = Assert.IsType<BandaiFcgCartridge>(compiled.Slot.Cartridge);
        var expected = Assert.IsType<BandaiFcgCartridge>(reference.Slot.Cartridge);
        Assert.True(compiled.Famicom.CompiledLabMotherboardEnabled);
        Assert.Equal((byte)0x03, actual.PrgBankRegister);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, actual.ChrBankRegisters.ToArray());
        Assert.Equal(BandaiFcgNametableMode.SingleScreenPage1, actual.NametableMode);
        Assert.True(actual.IrqAsserted);
        Assert.Equal(expected.IrqCounter, actual.IrqCounter);
        Assert.Equal(expected.IrqAsserted, actual.IrqAsserted);
        Assert.Equal(expected.IrqClockCount, actual.IrqClockCount);
        Assert.Equal(expected.MapperWriteCount, actual.MapperWriteCount);
        Assert.Equal(reference.Famicom.Cpu.ProgramCounter, compiled.Famicom.Cpu.ProgramCounter);
        Assert.Equal(reference.Famicom.Cpu.CompletedInstructionCount, compiled.Famicom.Cpu.CompletedInstructionCount);
        Assert.Equal(reference.Famicom.Ppu.InspectDiagnosticState(), compiled.Famicom.Ppu.InspectDiagnosticState());
        Assert.Equal(reference.Famicom.Ciram.InspectStateHash(), compiled.Famicom.Ciram.InspectStateHash());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Deprecated_mapper16_submappers_require_their_distinct_mapper_hardware(int submapper)
    {
        var image = CreateImage(BandaiFcgVariant.Lz93D50, CreatePrg(8), CreateChr(32), eeprom: false) with
        {
            SubmapperNumber = submapper
        };

        Assert.Throws<NotSupportedException>(() => new BandaiFcgCartridge("TEST.BANDAI.DEPRECATED").LoadImage(image));
    }

    [Fact]
    public void Invalid_four_screen_chr_ram_and_non_24c02_nvram_are_rejected()
    {
        var basic = CreateImage(BandaiFcgVariant.Lz93D50, CreatePrg(8), CreateChr(32), eeprom: false);
        var fourScreen = basic with { Mirroring = VirtualHardwareNesMirroring.FourScreen };
        var chrRam = basic with { ChrRamSizeBytes = 8 * 1024 };
        var badNvram = basic with { PrgNvRamSizeBytes = 128 };
        var noChr = basic with { ChrRom = [], ChrRomSizeBytes = 0 };

        Assert.Throws<NotSupportedException>(() => new BandaiFcgCartridge("TEST.BANDAI.4S").LoadImage(fourScreen));
        Assert.Throws<NotSupportedException>(() => new BandaiFcgCartridge("TEST.BANDAI.CHRRAM").LoadImage(chrRam));
        Assert.Throws<NotSupportedException>(() => new BandaiFcgCartridge("TEST.BANDAI.NVRAM").LoadImage(badNvram));
        Assert.Throws<NotSupportedException>(() => new BandaiFcgCartridge("TEST.BANDAI.NOCHR").LoadImage(noChr));
    }

    [Fact]
    public void Factory_constructs_mapper_sixteen_as_replaceable_bandai_fcg_hardware()
    {
        var image = CreateImage(BandaiFcgVariant.Lz93D50, CreatePrg(8), CreateChr(32), eeprom: false);
        var cartridge = VirtualCartridgeHardwareFactory.Create(image);

        Assert.Equal(16, cartridge.MapperNumber);
        Assert.IsType<BandaiFcgCartridge>(cartridge);
        Assert.True(cartridge.IsInserted);
    }

    private static BandaiFcgCartridge CreateCartridge(
        BandaiFcgVariant variant,
        int prgBanks,
        int chrBanks,
        bool eeprom = false)
    {
        var cartridge = new BandaiFcgCartridge("TEST.BANDAI.FCG");
        cartridge.LoadImage(CreateImage(variant, CreatePrg(prgBanks), CreateChr(chrBanks), eeprom));
        return cartridge;
    }

    private static byte[] CreatePrg(int banks)
    {
        var prg = new byte[banks * 16 * 1024];
        for (var bank = 0; bank < banks; bank++)
            Array.Fill(prg, (byte)(0x40 + bank), bank * 16 * 1024, 16 * 1024);
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
        BandaiFcgVariant variant,
        byte[] prg,
        byte[] chr,
        bool eeprom,
        VirtualHardwareNesHeaderFormat headerFormat = VirtualHardwareNesHeaderFormat.Nes20,
        bool battery = false)
    {
        var submapper = variant switch
        {
            BandaiFcgVariant.Compatibility => 0,
            BandaiFcgVariant.Fcg12 => 4,
            BandaiFcgVariant.Lz93D50 => 5,
            _ => 0
        };
        return new VirtualHardwareNesRomImage(
            headerFormat,
            MapperNumber: 16,
            SubmapperNumber: headerFormat == VirtualHardwareNesHeaderFormat.Nes20 ? submapper : null,
            PrgRomSizeBytes: prg.Length,
            ChrRomSizeBytes: chr.Length,
            HasTrainer: false,
            HasBatteryBackedMemory: battery || eeprom,
            VirtualHardwareNesMirroring.Vertical,
            VirtualHardwareNesHeaderTiming.Ntsc,
            prg,
            chr)
        {
            PrgRamSizeBytes = 0,
            PrgNvRamSizeBytes = eeprom ? 256 : 0,
            ChrRamSizeBytes = 0,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = headerFormat == VirtualHardwareNesHeaderFormat.Nes20
        };
    }

    private static VirtualHardwareNesRomImage CreateExecutionImage()
    {
        var prg = CreatePrg(16);
        var program = new List<byte> { 0x78 }; // SEI - observe asserted IRQ without CPU servicing it.
        for (var register = 0; register < 8; register++)
        {
            program.AddRange(new byte[]
            {
                0xA9, (byte)(register + 1),
                0x8D, (byte)register, 0x80
            });
        }
        program.AddRange(new byte[]
        {
            0xA9, 0x03, 0x8D, 0x08, 0x80, // PRG bank 3
            0xA9, 0x03, 0x8D, 0x09, 0x80, // single-screen page 1
            0xA9, 0x10, 0x8D, 0x0B, 0x80, // IRQ latch low
            0xA9, 0x00, 0x8D, 0x0C, 0x80, // IRQ latch high
            0xA9, 0x01, 0x8D, 0x0A, 0x80  // IRQ enable/reload
        });
        var loopAddress = (ushort)(0xC000 + program.Count);
        program.AddRange(new byte[] { 0x4C, (byte)loopAddress, (byte)(loopAddress >> 8) });

        var fixedLast = 15 * 16 * 1024;
        program.ToArray().CopyTo(prg, fixedLast);
        prg[fixedLast + 0x3FFA] = 0x00; prg[fixedLast + 0x3FFB] = 0xC0;
        prg[fixedLast + 0x3FFC] = 0x00; prg[fixedLast + 0x3FFD] = 0xC0;
        prg[fixedLast + 0x3FFE] = 0x00; prg[fixedLast + 0x3FFF] = 0xC0;

        return CreateImage(BandaiFcgVariant.Lz93D50, prg, CreateChr(64), eeprom: false);
    }

    private static CompiledBusTargetDescriptor CpuRomTarget(BandaiFcgCartridge cartridge) =>
        Assert.Single(
            ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.AddressPins.Count == 15
                && target.DataPins.Count == 8
                && target.Read is not null
                && target.ReadConditions.Any(condition => ReferenceEquals(condition.Pin, cartridge.CpuRomSelectBar)
                    && condition.RequiredLevel == DigitalLevel.Low));

    private static CompiledBusTargetDescriptor CpuLowRegisterTarget(BandaiFcgCartridge cartridge) =>
        Assert.Single(
            ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.AddressPins.Count == 15
                && target.DataPins.Count == 8
                && target.Read is null
                && target.Write is not null
                && target.WriteConditions.Any(condition => ReferenceEquals(condition.Pin, cartridge.CpuRomSelectBar)
                    && condition.RequiredLevel == DigitalLevel.High));

    private static CompiledBusTargetDescriptor EepromTarget(BandaiFcgCartridge cartridge) =>
        Assert.Single(
            ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.DataPins.Count == 1 && ReferenceEquals(target.DataPins[0], cartridge.CpuData.Pins[4]));

    private static CompiledBusTargetDescriptor PpuTarget(BandaiFcgCartridge cartridge) =>
        Assert.Single(
            ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.AddressPins.Count == 14);

    private static void ClockCpu(CompiledBusTargetDescriptor cpu, int cycles)
    {
        var observer = Assert.IsType<Action<bool>>(cpu.ObserveBusCycle);
        for (var index = 0; index < cycles; index++) observer(false);
    }

    private static DigitalLevel SamplePpuAddress(BandaiFcgCartridge cartridge, DigitalPin pin, ushort address)
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

    private static void WriteEepromControl(BandaiFcgCartridge cartridge, bool scl, bool sda, bool release = false)
    {
        var value = (byte)((release ? 0x80 : 0x00) | (sda ? 0x40 : 0x00) | (scl ? 0x20 : 0x00));
        CpuRomTarget(cartridge).Write!(0x000D, value);
    }

    private static void I2cStart(BandaiFcgCartridge cartridge)
    {
        WriteEepromControl(cartridge, true, true);
        WriteEepromControl(cartridge, true, false);
    }

    private static void I2cRepeatedStart(BandaiFcgCartridge cartridge) => I2cStart(cartridge);

    private static void I2cStop(BandaiFcgCartridge cartridge)
    {
        WriteEepromControl(cartridge, false, false);
        WriteEepromControl(cartridge, true, false);
        WriteEepromControl(cartridge, true, true);
    }

    private static void I2cSendByte(BandaiFcgCartridge cartridge, byte value)
    {
        for (var bit = 7; bit >= 0; bit--)
        {
            var high = (value & (1 << bit)) != 0;
            WriteEepromControl(cartridge, false, high);
            WriteEepromControl(cartridge, true, high);
            WriteEepromControl(cartridge, false, high);
        }
    }

    private static void I2cAckClock(BandaiFcgCartridge cartridge)
    {
        Assert.False(cartridge.EepromDataOutHigh);
        WriteEepromControl(cartridge, false, true, release: true);
        WriteEepromControl(cartridge, true, true, release: true);
        WriteEepromControl(cartridge, false, true, release: true);
    }

    private static byte I2cReadByteAndNack(BandaiFcgCartridge cartridge)
    {
        byte value = 0;
        for (var bit = 7; bit >= 0; bit--)
        {
            WriteEepromControl(cartridge, true, true, release: true);
            if (EepromTarget(cartridge).Read!(0x6000) != 0)
                value |= (byte)(1 << bit);
            WriteEepromControl(cartridge, false, true, release: true);
        }

        // NACK the byte (SDA high) so the EEPROM releases the bus.
        WriteEepromControl(cartridge, false, true);
        WriteEepromControl(cartridge, true, true);
        WriteEepromControl(cartridge, false, true);
        return value;
    }
}
