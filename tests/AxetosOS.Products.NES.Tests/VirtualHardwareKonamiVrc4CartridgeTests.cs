using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Machines.Nes;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareKonamiVrc4CartridgeTests
{
    [Fact]
    public void Power_on_exposes_two_switchable_prg_windows_two_fixed_windows_and_eight_chr_windows()
    {
        var cartridge = CreateCartridge(21, null, VirtualHardwareNesHeaderFormat.INes, prgBanks: 16, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuTarget(cartridge);

        Assert.Equal((byte)0x40, cpu.Read!(0x0000));
        Assert.Equal((byte)0x41, cpu.Read!(0x2000));
        Assert.Equal((byte)0x4E, cpu.Read!(0x4000));
        Assert.Equal((byte)0x4F, cpu.Read!(0x6000));
        Assert.Equal(new[] { 0, 1, 14, 15 }, cartridge.PrgWindowBanks.ToArray());
        for (var slot = 0; slot < 8; slot++)
            Assert.Equal((byte)(0x80 + slot), ppu.Read!(slot * 0x400));
        Assert.Equal(0UL, cartridge.PpuWriteCount);
    }

    [Fact]
    public void Two_prg_registers_and_prg_mode_swap_only_the_first_and_second_last_windows()
    {
        var cartridge = CreateCartridge(21, 1, VirtualHardwareNesHeaderFormat.Nes20, prgBanks: 32, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);

        WriteHigh(cpu, 0x8000, 0x05);
        WriteHigh(cpu, 0xA000, 0x06);
        Assert.Equal(new[] { 5, 6, 30, 31 }, cartridge.PrgWindowBanks.ToArray());

        WriteHigh(cpu, 0x9004, 0x02); // VRC4a A1<-CPU A2 => translated $9002
        Assert.True(cartridge.PrgMode);
        Assert.Equal(new[] { 30, 6, 5, 31 }, cartridge.PrgWindowBanks.ToArray());
        Assert.Equal((byte)0x5E, cpu.Read!(0x0000));
        Assert.Equal((byte)0x46, cpu.Read!(0x2000));
        Assert.Equal((byte)0x45, cpu.Read!(0x4000));
    }

    [Fact]
    public void Chr_low_and_high_register_nibbles_form_independent_nine_bit_one_kib_bank_outputs()
    {
        var cartridge = CreateCartridge(23, 1, VirtualHardwareNesHeaderFormat.Nes20, prgBanks: 16, chrBanks: 512);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuTarget(cartridge);

        WriteHigh(cpu, 0xB000, 0x0A); // VRC4f logical B000 low CHR0
        WriteHigh(cpu, 0xB001, 0x11); // VRC4f logical B001 high CHR0
        WriteHigh(cpu, 0xB002, 0x03); // low CHR1
        WriteHigh(cpu, 0xB003, 0x02); // high CHR1

        Assert.Equal((ushort)0x11A, cartridge.GetChrRegister(0));
        Assert.Equal((ushort)0x023, cartridge.GetChrRegister(1));
        Assert.Equal(0x11A, cartridge.ChrWindowBanks[0]);
        Assert.Equal(0x023, cartridge.ChrWindowBanks[1]);
        Assert.Equal((byte)(0x80 + (0x11A & 0x7F)), ppu.Read!(0x0000));
        Assert.Equal((byte)(0x80 + (0x023 & 0x7F)), ppu.Read!(0x0400));
    }

    [Theory]
    [InlineData(0x00, KonamiVrcNametableMode.Vertical, DigitalLevel.High)]
    [InlineData(0x01, KonamiVrcNametableMode.Horizontal, DigitalLevel.Low)]
    [InlineData(0x02, KonamiVrcNametableMode.SingleScreenPage0, DigitalLevel.Low)]
    [InlineData(0x03, KonamiVrcNametableMode.SingleScreenPage1, DigitalLevel.High)]
    public void Mirroring_register_exposes_all_four_ciram_routes(byte value, KonamiVrcNametableMode expected, DigitalLevel expectedAt0400)
    {
        var cartridge = CreateCartridge(21, 1, VirtualHardwareNesHeaderFormat.Nes20, prgBanks: 16, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);
        var dynamic = Assert.IsAssignableFrom<ICompiledCombinationalComponent>(cartridge);

        WriteHigh(cpu, 0x9000, value);

        Assert.Equal(expected, cartridge.NametableMode);
        Assert.True(dynamic.TryEvaluateCompiledOutput(cartridge.CiramA10, pin => SamplePpuAddress(cartridge, pin, 0x0400), out var drive));
        Assert.Equal(expectedAt0400, drive.Level);
    }

    [Theory]
    [InlineData(21, 1, KonamiVrc4Variant.Vrc4A, 0xF002, 0xF001, 0xF004, 0xF002)]
    [InlineData(21, 2, KonamiVrc4Variant.Vrc4C, 0xF040, 0xF001, 0xF080, 0xF002)]
    [InlineData(23, 1, KonamiVrc4Variant.Vrc4F, 0xF001, 0xF001, 0xF002, 0xF002)]
    [InlineData(23, 2, KonamiVrc4Variant.Vrc4E, 0xF004, 0xF001, 0xF008, 0xF002)]
    [InlineData(25, 1, KonamiVrc4Variant.Vrc4B, 0xF002, 0xF001, 0xF001, 0xF002)]
    [InlineData(25, 2, KonamiVrc4Variant.Vrc4D, 0xF008, 0xF001, 0xF004, 0xF002)]
    public void Nes20_submappers_select_exact_vrc4_package_address_wiring(
        int mapper, int submapper, KonamiVrc4Variant expectedVariant,
        int physicalForLogicalF001, int expectedF001,
        int physicalForLogicalF002, int expectedF002)
    {
        var cartridge = CreateCartridge(mapper, submapper, VirtualHardwareNesHeaderFormat.Nes20, 16, 32);

        Assert.Equal(expectedVariant, cartridge.Variant);
        Assert.False(cartridge.UsesLegacyAddressDecode);
        Assert.Equal((ushort)expectedF001, cartridge.TranslateMapperAddress((ushort)physicalForLogicalF001));
        Assert.Equal((ushort)expectedF002, cartridge.TranslateMapperAddress((ushort)physicalForLogicalF002));
    }

    [Theory]
    [InlineData(21, KonamiVrc4Variant.LegacyMapper21, 0xF040, 0xF001, 0xF080, 0xF002)]
    [InlineData(23, KonamiVrc4Variant.LegacyMapper23, 0xF004, 0xF001, 0xF008, 0xF002)]
    [InlineData(25, KonamiVrc4Variant.LegacyMapper25, 0xF008, 0xF001, 0xF004, 0xF002)]
    public void Legacy_ines_uses_family_address_line_compatibility_decode(
        int mapper, KonamiVrc4Variant expectedVariant,
        int physicalForLogicalF001, int expectedF001,
        int physicalForLogicalF002, int expectedF002)
    {
        var cartridge = CreateCartridge(mapper, null, VirtualHardwareNesHeaderFormat.INes, 16, 32);

        Assert.Equal(expectedVariant, cartridge.Variant);
        Assert.True(cartridge.UsesLegacyAddressDecode);
        Assert.Equal((ushort)expectedF001, cartridge.TranslateMapperAddress((ushort)physicalForLogicalF001));
        Assert.Equal((ushort)expectedF002, cartridge.TranslateMapperAddress((ushort)physicalForLogicalF002));
    }

    [Fact]
    public void Irq_cycle_mode_reloads_at_ff_and_asserts_external_output()
    {
        var cartridge = CreateCartridge(21, 1, VirtualHardwareNesHeaderFormat.Nes20, 16, 32);
        var cpu = CpuRomTarget(cartridge);

        WriteHigh(cpu, 0xF000, 0x0E); // reload low
        WriteHigh(cpu, 0xF002, 0x0F); // physical VRC4a -> logical F001, reload high
        WriteHigh(cpu, 0xF004, 0x06); // physical VRC4a -> logical F002, enable + cycle mode
        ClockCpu(cpu, 2);

        Assert.Equal((byte)0xFE, cartridge.Irq.ReloadValue);
        Assert.Equal((byte)0xFE, cartridge.Irq.Counter);
        Assert.True(cartridge.Irq.CycleMode);
        Assert.True(cartridge.Irq.Enabled);
        Assert.True(cartridge.Irq.Asserted);
        Assert.Equal(341, cartridge.Irq.Prescaler);
        Assert.Equal(2UL, cartridge.Irq.CounterClockCount);
        Assert.Equal(1UL, cartridge.Irq.AssertCount);
    }

    [Fact]
    public void Irq_scanline_mode_uses_three_dot_cpu_decrement_against_341_dot_prescaler()
    {
        var cartridge = CreateCartridge(23, 1, VirtualHardwareNesHeaderFormat.Nes20, 16, 32);
        var cpu = CpuRomTarget(cartridge);

        WriteHigh(cpu, 0xF000, 0x00);
        WriteHigh(cpu, 0xF001, 0x00);
        WriteHigh(cpu, 0xF002, 0x02); // enabled, scanline mode
        ClockCpu(cpu, 113);
        Assert.Equal((byte)0x00, cartridge.Irq.Counter);
        Assert.Equal(0UL, cartridge.Irq.CounterClockCount);
        ClockCpu(cpu, 1);
        Assert.Equal((byte)0x01, cartridge.Irq.Counter);
        Assert.Equal(1UL, cartridge.Irq.CounterClockCount);
        Assert.Equal(340, cartridge.Irq.Prescaler);
    }

    [Fact]
    public void Irq_acknowledge_uses_enable_after_ack_latch_and_clears_asserted_line()
    {
        var cartridge = CreateCartridge(23, 1, VirtualHardwareNesHeaderFormat.Nes20, 16, 32);
        var cpu = CpuRomTarget(cartridge);

        WriteHigh(cpu, 0xF000, 0x0F);
        WriteHigh(cpu, 0xF001, 0x0F);
        WriteHigh(cpu, 0xF002, 0x07); // enable-after-ack + enable + cycle
        ClockCpu(cpu, 1);
        Assert.True(cartridge.Irq.Asserted);

        WriteHigh(cpu, 0xF003, 0x00);
        Assert.False(cartridge.Irq.Asserted);
        Assert.True(cartridge.Irq.Enabled);
        Assert.True(cartridge.Irq.EnabledAfterAcknowledge);
    }

    [Fact]
    public void Optional_eight_kib_work_ram_is_a_separate_six_thousand_cpu_window()
    {
        var cartridge = CreateCartridge(25, 1, VirtualHardwareNesHeaderFormat.Nes20, 16, 32, workRamBytes: 8 * 1024);
        var ram = CpuWorkRamTarget(cartridge);

        ram.Write!(0x6000, 0xA5);
        ram.Write!(0x7FFF, 0x5A);

        Assert.Equal((byte)0xA5, ram.Read!(0x6000));
        Assert.Equal((byte)0x5A, ram.Read!(0x7FFF));
        Assert.Equal((byte)0xA5, cartridge.InspectWorkRamByte(0));
        Assert.Equal((byte)0x5A, cartridge.InspectWorkRamByte(0x1FFF));
        Assert.Equal(2UL, cartridge.WorkRamWriteCount);
        Assert.Equal(2UL, cartridge.WorkRamReadCount);
    }

    [Fact]
    public void Mapper_register_writes_and_irq_clock_use_completed_cpu_bus_phase()
    {
        var cartridge = CreateCartridge(21, 1, VirtualHardwareNesHeaderFormat.Nes20, 16, 32);
        var cpu = CpuRomTarget(cartridge);

        Assert.Equal(CompiledBusWritePhase.Complete, cpu.WritePhase);
        Assert.Equal(CompiledBusCycleObservationPhase.Complete, cpu.ObserveBusCyclePhase);
    }

    [Fact]
    public void Generic_compiled_and_raw_physical_vrc4_execute_same_banking_mirroring_and_irq_program()
    {
        var image = CreateExecutionImage();
        var compiled = new RegionalNesVirtualMachine();
        var reference = new RegionalNesVirtualMachine();

        compiled.SetCompiledLabExecutionEnabled(true);
        compiled.InsertRom(image, "VRC4 synthetic (Japan).nes");
        reference.InsertRom(image, "VRC4 synthetic (Japan).nes");
        reference.Famicom.SetCompiledPhysicalMachineEnabled(false);

        compiled.PowerOn();
        reference.PowerOn();
        compiled.ReleaseReset();
        reference.ReleaseReset();

        const int masterCycles = 60_000;
        compiled.AdvanceMasterCycles(masterCycles);
        reference.AdvanceMasterCycles(masterCycles);

        var actual = Assert.IsType<KonamiVrc4Cartridge>(compiled.Slot.Cartridge);
        var expected = Assert.IsType<KonamiVrc4Cartridge>(reference.Slot.Cartridge);
        Assert.True(compiled.Famicom.CompiledLabMotherboardEnabled);
        Assert.Equal(new byte[] { 5, 6 }, actual.PrgBankRegisters.ToArray());
        Assert.True(actual.PrgMode);
        Assert.Equal(KonamiVrcNametableMode.SingleScreenPage1, actual.NametableMode);
        Assert.Equal(Enumerable.Range(1, 8).ToArray(), actual.ChrWindowBanks.ToArray());
        Assert.True(actual.Irq.Asserted);
        Assert.Equal(expected.PrgWindowBanks.ToArray(), actual.PrgWindowBanks.ToArray());
        Assert.Equal(expected.ChrWindowBanks.ToArray(), actual.ChrWindowBanks.ToArray());
        Assert.Equal(expected.Irq.Counter, actual.Irq.Counter);
        Assert.Equal(expected.Irq.Prescaler, actual.Irq.Prescaler);
        Assert.Equal(expected.Irq.Asserted, actual.Irq.Asserted);
        Assert.Equal(expected.Irq.CpuClockCount, actual.Irq.CpuClockCount);
        Assert.Equal(expected.MapperWriteCount, actual.MapperWriteCount);
        Assert.Equal(reference.Famicom.Cpu.ProgramCounter, compiled.Famicom.Cpu.ProgramCounter);
        Assert.Equal(reference.Famicom.Cpu.CompletedInstructionCount, compiled.Famicom.Cpu.CompletedInstructionCount);
        Assert.Equal(reference.Famicom.Ppu.InspectDiagnosticState(), compiled.Famicom.Ppu.InspectDiagnosticState());
        Assert.Equal(reference.Famicom.Ciram.InspectStateHash(), compiled.Famicom.Ciram.InspectStateHash());
    }

    [Theory]
    [InlineData(21)]
    [InlineData(23)]
    [InlineData(25)]
    public void Factory_constructs_target_vrc4_mapper_numbers_as_replaceable_konami_hardware(int mapper)
    {
        var image = CreateImage(mapper, null, VirtualHardwareNesHeaderFormat.INes, CreatePrg(16), CreateChr(32), 8 * 1024);
        var cartridge = VirtualCartridgeHardwareFactory.Create(image);

        Assert.Equal(mapper, cartridge.MapperNumber);
        Assert.IsType<KonamiVrc4Cartridge>(cartridge);
    }

    [Fact]
    public void Nes20_vrc2_submapper_ids_are_rejected_instead_of_being_reinterpreted_as_vrc4()
    {
        var mapper23Vrc2 = CreateImage(23, 3, VirtualHardwareNesHeaderFormat.Nes20, CreatePrg(16), CreateChr(32), 0);
        var mapper25Vrc2 = CreateImage(25, 3, VirtualHardwareNesHeaderFormat.Nes20, CreatePrg(16), CreateChr(32), 0);

        Assert.Throws<NotSupportedException>(() => new KonamiVrc4Cartridge("TEST.VRC2B").LoadImage(mapper23Vrc2));
        Assert.Throws<NotSupportedException>(() => new KonamiVrc4Cartridge("TEST.VRC2C").LoadImage(mapper25Vrc2));
    }

    [Fact]
    public void Invalid_four_screen_chr_ram_bad_prg_and_oversized_work_ram_are_rejected()
    {
        var basic = CreateImage(21, 1, VirtualHardwareNesHeaderFormat.Nes20, CreatePrg(16), CreateChr(32), 0);
        var fourScreen = basic with { Mirroring = VirtualHardwareNesMirroring.FourScreen };
        var chrRam = basic with { ChrRamSizeBytes = 8 * 1024 };
        var noChr = basic with { ChrRom = [], ChrRomSizeBytes = 0 };
        var badPrg = basic with { PrgRom = new byte[5 * 8 * 1024], PrgRomSizeBytes = 5 * 8 * 1024 };
        var badRam = basic with { PrgRamSizeBytes = 16 * 1024 };

        Assert.Throws<NotSupportedException>(() => new KonamiVrc4Cartridge("TEST.VRC4.4S").LoadImage(fourScreen));
        Assert.Throws<NotSupportedException>(() => new KonamiVrc4Cartridge("TEST.VRC4.CHRRAM").LoadImage(chrRam));
        Assert.Throws<NotSupportedException>(() => new KonamiVrc4Cartridge("TEST.VRC4.NOCHR").LoadImage(noChr));
        Assert.Throws<NotSupportedException>(() => new KonamiVrc4Cartridge("TEST.VRC4.BADPRG").LoadImage(badPrg));
        Assert.Throws<NotSupportedException>(() => new KonamiVrc4Cartridge("TEST.VRC4.BADRAM").LoadImage(badRam));
    }

    private static KonamiVrc4Cartridge CreateCartridge(
        int mapper, int? submapper, VirtualHardwareNesHeaderFormat header,
        int prgBanks, int chrBanks, int workRamBytes = 0)
    {
        var cartridge = new KonamiVrc4Cartridge("TEST.KONAMI.VRC4");
        cartridge.LoadImage(CreateImage(mapper, submapper, header, CreatePrg(prgBanks), CreateChr(chrBanks), workRamBytes));
        return cartridge;
    }

    private static VirtualHardwareNesRomImage CreateImage(
        int mapper, int? submapper, VirtualHardwareNesHeaderFormat header,
        byte[] prg, byte[] chr, int workRamBytes)
    {
        return new VirtualHardwareNesRomImage(
            header,
            mapper,
            submapper,
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
            HasExplicitRamSizes = header == VirtualHardwareNesHeaderFormat.Nes20
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
        AddSta(program, 0xA000, 0x06);
        AddSta(program, 0x9000, 0x03);
        AddSta(program, 0x9004, 0x02); // VRC4a logical $9002 PRG mode

        for (var slot = 0; slot < 8; slot++)
        {
            var page = slot / 2;
            var physicalBase = (ushort)(0xB000 + (page * 0x1000));
            var logicalPair = slot & 1;
            var lowAddress = (ushort)(physicalBase + (logicalPair == 0 ? 0x0000 : 0x0004));
            var highAddress = (ushort)(physicalBase + (logicalPair == 0 ? 0x0002 : 0x0006));
            AddSta(program, lowAddress, (byte)((slot + 1) & 0x0F));
            AddSta(program, highAddress, 0x00);
        }

        AddSta(program, 0xF000, 0x0E);
        AddSta(program, 0xF002, 0x0F); // VRC4a logical F001
        AddSta(program, 0xF004, 0x06); // VRC4a logical F002: enable + cycle

        var loopAddress = (ushort)(0xE000 + program.Count);
        program.AddRange(new byte[] { 0x4C, (byte)loopAddress, (byte)(loopAddress >> 8) });

        var fixedLast = 15 * 8 * 1024;
        program.ToArray().CopyTo(prg, fixedLast);
        prg[fixedLast + 0x1FFA] = 0x00; prg[fixedLast + 0x1FFB] = 0xE0;
        prg[fixedLast + 0x1FFC] = 0x00; prg[fixedLast + 0x1FFD] = 0xE0;
        prg[fixedLast + 0x1FFE] = 0x00; prg[fixedLast + 0x1FFF] = 0xE0;

        return CreateImage(21, 1, VirtualHardwareNesHeaderFormat.Nes20, prg, CreateChr(64), 0);
    }

    private static void AddSta(List<byte> program, ushort address, byte value) =>
        program.AddRange(new byte[] { 0xA9, value, 0x8D, (byte)address, (byte)(address >> 8) });

    private static CompiledBusTargetDescriptor CpuRomTarget(KonamiVrc4Cartridge cartridge) =>
        Assert.Single(
            ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.AddressPins.Count == 15 && target.ObserveBusCycle is not null);

    private static CompiledBusTargetDescriptor CpuWorkRamTarget(KonamiVrc4Cartridge cartridge) =>
        Assert.Single(
            ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.AddressPins.Count == 15 && target.ObserveBusCycle is null);

    private static CompiledBusTargetDescriptor PpuTarget(KonamiVrc4Cartridge cartridge) =>
        Assert.Single(
            ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.AddressPins.Count == 14);

    private static void WriteHigh(CompiledBusTargetDescriptor cpu, ushort address, byte value) =>
        cpu.Write!(address & 0x7FFF, value);

    private static void ClockCpu(CompiledBusTargetDescriptor cpu, int cycles)
    {
        var observer = Assert.IsType<Action<bool>>(cpu.ObserveBusCycle);
        for (var index = 0; index < cycles; index++) observer(false);
    }

    private static DigitalLevel SamplePpuAddress(KonamiVrc4Cartridge cartridge, DigitalPin pin, ushort address)
    {
        for (var bit = 0; bit < cartridge.PpuAddress.Width; bit++)
        {
            if (!ReferenceEquals(pin, cartridge.PpuAddress.Pins[bit])) continue;
            return (address & (1 << bit)) != 0 ? DigitalLevel.High : DigitalLevel.Low;
        }
        return DigitalLevel.Unknown;
    }
}
