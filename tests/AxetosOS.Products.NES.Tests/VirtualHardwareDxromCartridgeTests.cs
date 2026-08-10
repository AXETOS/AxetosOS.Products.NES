using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Machines.Nes;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareDxromCartridgeTests
{
    [Fact]
    public void Power_on_maps_two_zeroed_switchable_prg_windows_and_two_fixed_last_windows()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 64);
        var cpu = CpuRomTarget(cartridge);

        Assert.Equal((byte)0x40, cpu.Read!(0x8000));
        Assert.Equal((byte)0x40, cpu.Read!(0xA000));
        Assert.Equal((byte)0x4E, cpu.Read!(0xC000));
        Assert.Equal((byte)0x4F, cpu.Read!(0xE000));
        Assert.Equal(14, cartridge.FixedPrgBank0);
        Assert.Equal(15, cartridge.FixedPrgBank1);
    }

    [Fact]
    public void R6_and_r7_switch_the_two_low_prg_windows_without_mmc3_prg_mode()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 64);
        var cpu = CpuRomTarget(cartridge);

        WriteBank(cpu, 6, 0x05);
        WriteBank(cpu, 7, 0x09);

        Assert.Equal((byte)0x45, cpu.Read!(0x8000));
        Assert.Equal((byte)0x49, cpu.Read!(0xA000));
        Assert.Equal((byte)0x4E, cpu.Read!(0xC000));
        Assert.Equal((byte)0x4F, cpu.Read!(0xE000));

        cpu.Write!(0x8000, 0xC6); // only RRR is physically decoded
        Assert.Equal((byte)0x06, cartridge.BankSelectRegister);
        Assert.Equal((byte)0x45, cpu.Read!(0x8000));
        Assert.Equal((byte)0x4E, cpu.Read!(0xC000));
    }

    [Fact]
    public void Bank_data_latches_only_the_low_six_mapper_output_bits()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 64);
        var cpu = CpuRomTarget(cartridge);

        WriteBank(cpu, 6, 0xD5);
        WriteBank(cpu, 2, 0xFF);

        Assert.Equal((byte)0x05, cartridge.BankRegisters[6]);
        Assert.Equal((byte)0x3F, cartridge.BankRegisters[2]);
        Assert.Equal((byte)0x45, cpu.Read!(0x8000));
    }

    [Fact]
    public void Chr_registers_expose_two_two_kib_and_four_one_kib_windows_without_inversion()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 64);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuPatternTarget(cartridge);

        WriteBank(cpu, 0, 0x02);
        WriteBank(cpu, 1, 0x08);
        WriteBank(cpu, 2, 0x10);
        WriteBank(cpu, 3, 0x11);
        WriteBank(cpu, 4, 0x12);
        WriteBank(cpu, 5, 0x13);

        Assert.Equal((byte)0x62, ppu.Read!(0x0000));
        Assert.Equal((byte)0x63, ppu.Read!(0x0400));
        Assert.Equal((byte)0x68, ppu.Read!(0x0800));
        Assert.Equal((byte)0x69, ppu.Read!(0x0C00));
        Assert.Equal((byte)0x70, ppu.Read!(0x1000));
        Assert.Equal((byte)0x71, ppu.Read!(0x1400));
        Assert.Equal((byte)0x72, ppu.Read!(0x1800));
        Assert.Equal((byte)0x73, ppu.Read!(0x1C00));
    }

    [Fact]
    public void Two_kib_chr_registers_ignore_the_low_bank_bit()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 64);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuPatternTarget(cartridge);

        WriteBank(cpu, 0, 0x07);

        Assert.Equal((byte)0x06, cartridge.BankRegisters[0]);
        Assert.Equal((byte)0x66, ppu.Read!(0x0000));
        Assert.Equal((byte)0x67, ppu.Read!(0x0400));
    }

    [Fact]
    public void Writes_outside_8000_9fff_do_not_alias_mmc3_control_registers()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 64);
        var cpu = CpuRomTarget(cartridge);

        cpu.Write!(0xA000, 0x06);
        cpu.Write!(0xA001, 0x03);
        cpu.Write!(0xC000, 0x07);
        cpu.Write!(0xE001, 0x09);

        Assert.Equal((byte)0x00, cartridge.BankSelectRegister);
        Assert.All(cartridge.BankRegisters, value => Assert.Equal((byte)0x00, value));
        Assert.Equal(0UL, cartridge.MapperWriteCount);
        Assert.Equal(4UL, cartridge.IgnoredMapperWriteCount);
    }

    [Theory]
    [InlineData(VirtualHardwareNesMirroring.Vertical, 0x0400, DigitalLevel.High)]
    [InlineData(VirtualHardwareNesMirroring.Horizontal, 0x0400, DigitalLevel.Low)]
    public void Hardwired_header_mirroring_is_a_static_ciram_route(
        VirtualHardwareNesMirroring mirroring,
        ushort ppuAddress,
        DigitalLevel expected)
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 64, mirroring: mirroring);
        var combinational = Assert.IsAssignableFrom<ICompiledStaticCombinationalComponent>(cartridge);

        Assert.True(combinational.TryEvaluateCompiledStaticOutput(
            cartridge.CiramA10,
            pin => SamplePpuAddress(cartridge, pin, ppuAddress),
            out var drive));
        Assert.Equal(expected, drive.Level);
    }

    [Fact]
    public void Drrom_four_screen_variant_owns_eight_kib_cartridge_nametable_ram_and_disables_ciram()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 64,
            mirroring: VirtualHardwareNesMirroring.FourScreen);
        var targets = ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets().ToArray();
        var nametable = targets.Single(target => target.Read is not null
            && target.AddressPins.Count == 14
            && target.ReadConditions.Any(condition => ReferenceEquals(condition.Pin, cartridge.PpuAddress.Pins[13])
                && condition.RequiredLevel == DigitalLevel.High));

        nametable.Write!(0x2400, 0xCC);
        nametable.Write!(0x3400, 0x5A);
        Assert.Equal((byte)0xCC, nametable.Read!(0x2400));
        Assert.Equal((byte)0x5A, nametable.Read!(0x3400));
        Assert.True(cartridge.HasFourScreenRam);
        Assert.True(Assert.IsAssignableFrom<ICompiledStaticCombinationalComponent>(cartridge)
            .TryEvaluateCompiledStaticOutput(cartridge.CiramChipEnableBar, _ => DigitalLevel.Low, out var ce));
        Assert.Equal(DigitalLevel.High, ce.Level);
    }

    [Fact]
    public void Submapper_one_models_direct_cpu_address_wiring_for_unbanked_32k_prg_boards()
    {
        var cartridge = CreateCartridge(prgBanks: 4, chrBanks: 32, submapper: 1);
        var cpu = CpuRomTarget(cartridge);

        WriteBank(cpu, 6, 0x03);
        WriteBank(cpu, 7, 0x02);

        Assert.True(cartridge.Unbanked32KPrg);
        Assert.Equal((byte)0x40, cpu.Read!(0x8000));
        Assert.Equal((byte)0x41, cpu.Read!(0xA000));
        Assert.Equal((byte)0x42, cpu.Read!(0xC000));
        Assert.Equal((byte)0x43, cpu.Read!(0xE000));
        Assert.Equal((byte)0x03, cartridge.BankRegisters[6]); // mapper package still latched it; ROM pins ignore it
    }

    [Fact]
    public void Explicit_eight_kib_prg_ram_models_the_known_mimic1_prototype_exception()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 64, prgRamSize: 8 * 1024);
        var ram = CpuRamTarget(cartridge);

        Assert.Equal(8 * 1024, cartridge.PrgRamSizeBytes);
        ram.Write!(0x6123, 0xA5);
        Assert.Equal((byte)0xA5, ram.Read!(0x6123));
    }

    [Fact]
    public void Legacy_ines_inferred_prg_ram_is_not_silently_added_to_standard_dxrom()
    {
        var image = CreateImage(CreatePrg(16), CreateChr(64), submapper: null,
            headerFormat: VirtualHardwareNesHeaderFormat.INes) with
        {
            PrgRamSizeBytes = 8 * 1024,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 0,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = false
        };
        var cartridge = new DxromCartridge("TEST.DXROM.INES");
        cartridge.LoadImage(image);

        Assert.Equal(0, cartridge.PrgRamSizeBytes);
        Assert.Single(
            ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.AddressPins.Count == 15);
    }

    [Fact]
    public void Compiled_mapper_register_writes_commit_at_the_completed_cpu_bus_phase()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 64);
        var target = CpuRomTarget(cartridge);

        Assert.Equal(CompiledBusWritePhase.Complete, target.WritePhase);
    }

    [Fact]
    public void Generic_compiled_and_raw_physical_dxrom_execute_the_same_bank_switch_program()
    {
        var image = CreateExecutionImage();
        var compiled = new RegionalNesVirtualMachine();
        var reference = new RegionalNesVirtualMachine();

        compiled.SetCompiledLabExecutionEnabled(true);
        compiled.InsertRom(image, "DxROM execution (Japan).nes");
        reference.InsertRom(image, "DxROM execution (Japan).nes");
        reference.Famicom.SetCompiledPhysicalMachineEnabled(false);

        compiled.PowerOn();
        reference.PowerOn();
        compiled.ReleaseReset();
        reference.ReleaseReset();

        const int masterCycles = 20_000;
        compiled.AdvanceMasterCycles(masterCycles);
        reference.AdvanceMasterCycles(masterCycles);

        var compiledCartridge = Assert.IsType<DxromCartridge>(compiled.Slot.Cartridge);
        var referenceCartridge = Assert.IsType<DxromCartridge>(reference.Slot.Cartridge);
        Assert.True(compiled.Famicom.CompiledLabMotherboardEnabled);
        Assert.Equal((byte)0x01, compiledCartridge.BankRegisters[6]);
        Assert.Equal((byte)0x02, compiledCartridge.BankRegisters[7]);
        Assert.Equal((byte)0x04, compiledCartridge.BankRegisters[0]);
        Assert.Equal(referenceCartridge.BankSelectRegister, compiledCartridge.BankSelectRegister);
        Assert.Equal(referenceCartridge.BankRegisters.ToArray(), compiledCartridge.BankRegisters.ToArray());
        Assert.Equal(referenceCartridge.MapperWriteCount, compiledCartridge.MapperWriteCount);
        Assert.Equal(reference.Famicom.Cpu.ProgramCounter, compiled.Famicom.Cpu.ProgramCounter);
        Assert.Equal(reference.Famicom.Cpu.CompletedInstructionCount, compiled.Famicom.Cpu.CompletedInstructionCount);
        Assert.Equal(reference.Famicom.Ppu.InspectDiagnosticState(), compiled.Famicom.Ppu.InspectDiagnosticState());
        Assert.Equal(reference.Famicom.Ciram.InspectStateHash(), compiled.Famicom.Ciram.InspectStateHash());
    }

    [Fact]
    public void Unsupported_dxrom_geometries_and_submappers_require_distinct_hardware()
    {
        Assert.Throws<NotSupportedException>(() => new DxromCartridge("TEST.DXROM.NOCHR")
            .LoadImage(CreateImage(CreatePrg(16), [], submapper: 0)));
        Assert.Throws<NotSupportedException>(() => new DxromCartridge("TEST.DXROM.BIGPRG")
            .LoadImage(CreateImage(CreatePrg(32), CreateChr(64), submapper: 0)));
        Assert.Throws<NotSupportedException>(() => new DxromCartridge("TEST.DXROM.BIGCHR")
            .LoadImage(CreateImage(CreatePrg(16), CreateChr(128), submapper: 0)));
        Assert.Throws<NotSupportedException>(() => new DxromCartridge("TEST.DXROM.SUB1BIG")
            .LoadImage(CreateImage(CreatePrg(8), CreateChr(32), submapper: 1)));
        Assert.Throws<NotSupportedException>(() => new DxromCartridge("TEST.DXROM.SUB2")
            .LoadImage(CreateImage(CreatePrg(16), CreateChr(64), submapper: 2)));

        var mixedChr = CreateImage(CreatePrg(16), CreateChr(64), submapper: 0) with
        {
            PrgRamSizeBytes = 0,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 8 * 1024,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        };
        Assert.Throws<NotSupportedException>(() => new DxromCartridge("TEST.DXROM.MIXEDCHR").LoadImage(mixedChr));
    }

    [Fact]
    public void Factory_constructs_mapper_206_as_replaceable_dxrom_hardware()
    {
        var image = CreateImage(CreatePrg(16), CreateChr(64), submapper: 0);
        var cartridge = VirtualCartridgeHardwareFactory.Create(image);

        Assert.Equal(206, cartridge.MapperNumber);
        Assert.IsType<DxromCartridge>(cartridge);
        Assert.True(cartridge.IsInserted);
    }

    private static DxromCartridge CreateCartridge(
        int prgBanks,
        int chrBanks,
        int submapper = 0,
        int prgRamSize = 0,
        VirtualHardwareNesMirroring mirroring = VirtualHardwareNesMirroring.Horizontal)
    {
        var image = CreateImage(CreatePrg(prgBanks), CreateChr(chrBanks), submapper, mirroring) with
        {
            PrgRamSizeBytes = prgRamSize,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 0,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        };
        var cartridge = new DxromCartridge("TEST.DXROM");
        cartridge.LoadImage(image);
        return cartridge;
    }

    private static byte[] CreatePrg(int banks)
    {
        var prg = new byte[banks * 8 * 1024];
        for (var bank = 0; bank < banks; bank++)
            Array.Fill(prg, (byte)(0x40 + bank), bank * 8 * 1024, 8 * 1024);
        return prg;
    }

    private static byte[] CreateChr(int banks)
    {
        var chr = new byte[banks * 1024];
        for (var bank = 0; bank < banks; bank++)
            Array.Fill(chr, (byte)(0x60 + bank), bank * 1024, 1024);
        return chr;
    }

    private static VirtualHardwareNesRomImage CreateImage(
        byte[] prg,
        byte[] chr,
        int? submapper,
        VirtualHardwareNesMirroring mirroring = VirtualHardwareNesMirroring.Horizontal,
        VirtualHardwareNesHeaderFormat headerFormat = VirtualHardwareNesHeaderFormat.Nes20) =>
        new(
            headerFormat,
            MapperNumber: 206,
            SubmapperNumber: submapper,
            PrgRomSizeBytes: prg.Length,
            ChrRomSizeBytes: chr.Length,
            HasTrainer: false,
            HasBatteryBackedMemory: false,
            mirroring,
            VirtualHardwareNesHeaderTiming.Ntsc,
            prg,
            chr);

    private static VirtualHardwareNesRomImage CreateExecutionImage()
    {
        var prg = CreatePrg(16);
        var fixedLast = 15 * 8 * 1024;
        var program = new byte[]
        {
            0x78,                         // SEI
            0xA9, 0x06,                   // LDA #$06 - R6
            0x8D, 0x00, 0x80,             // STA $8000
            0xA9, 0x01,
            0x8D, 0x01, 0x80,             // STA $8001 - PRG bank 1 at $8000
            0xA9, 0x07,
            0x8D, 0x00, 0x80,             // select R7
            0xA9, 0x02,
            0x8D, 0x01, 0x80,             // PRG bank 2 at $A000
            0xA9, 0x00,
            0x8D, 0x00, 0x80,             // select R0
            0xA9, 0x04,
            0x8D, 0x01, 0x80,             // 2 KiB CHR bank pair 4/5 at $0000
            0x4C, 0x1F, 0xE0              // JMP $E01F
        };
        program.CopyTo(prg, fixedLast);
        prg[fixedLast + 0x1FFA] = 0x00; prg[fixedLast + 0x1FFB] = 0xE0;
        prg[fixedLast + 0x1FFC] = 0x00; prg[fixedLast + 0x1FFD] = 0xE0;
        prg[fixedLast + 0x1FFE] = 0x00; prg[fixedLast + 0x1FFF] = 0xE0;

        return CreateImage(prg, CreateChr(64), submapper: 0) with
        {
            PrgRamSizeBytes = 0,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 0,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        };
    }

    private static void WriteBank(CompiledBusTargetDescriptor cpu, byte register, byte value)
    {
        cpu.Write!(0x8000, register);
        cpu.Write!(0x8001, value);
    }

    private static CompiledBusTargetDescriptor CpuRomTarget(DxromCartridge cartridge) =>
        ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets()
            .Single(target => target.AddressPins.Count == 15
                && target.ReadConditions.Any(condition => ReferenceEquals(condition.Pin, cartridge.CpuRomSelectBar)
                    && condition.RequiredLevel == DigitalLevel.Low));

    private static CompiledBusTargetDescriptor CpuRamTarget(DxromCartridge cartridge) =>
        ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets()
            .Single(target => target.AddressPins.Count == 15
                && target.ReadConditions.Any(condition => ReferenceEquals(condition.Pin, cartridge.CpuRomSelectBar)
                    && condition.RequiredLevel == DigitalLevel.High));

    private static CompiledBusTargetDescriptor PpuPatternTarget(DxromCartridge cartridge) =>
        ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets()
            .Single(target => target.AddressPins.Count == 14 && target.Read is not null
                && target.ReadConditions.Any(condition => ReferenceEquals(condition.Pin, cartridge.PpuAddress.Pins[13])
                    && condition.RequiredLevel == DigitalLevel.Low));

    private static DigitalLevel SamplePpuAddress(DxromCartridge cartridge, DigitalPin pin, ushort address)
    {
        for (var bit = 0; bit < cartridge.PpuAddress.Width; bit++)
        {
            if (!ReferenceEquals(pin, cartridge.PpuAddress.Pins[bit])) continue;
            return (address & (1 << bit)) != 0 ? DigitalLevel.High : DigitalLevel.Low;
        }
        return DigitalLevel.Unknown;
    }
}
