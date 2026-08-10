using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Machines.Nes;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareMapper34CartridgeTests
{
    [Fact]
    public void Legacy_ines_chr_ram_geometry_resolves_to_bnrom_hardware()
    {
        var image = CreateImage(CreatePrg32K(4), [], submapper: null,
            headerFormat: VirtualHardwareNesHeaderFormat.INes,
            explicitRam: false);
        var cartridge = new Mapper34Cartridge("TEST.M34.LEGACY.BNROM");
        cartridge.LoadImage(image);

        Assert.Equal(Mapper34BoardVariant.Bnrom, cartridge.BoardVariant);
        Assert.Equal(8 * 1024, cartridge.ChrRamSizeBytes);
        Assert.Equal(0, cartridge.ChrRomSizeBytes);
        Assert.Equal(0, cartridge.PrgRamSizeBytes);
    }

    [Fact]
    public void Legacy_ines_chr_rom_larger_than_eight_kib_resolves_to_nina001_hardware()
    {
        var image = CreateImage(CreatePrg32K(2), CreateChr4K(16), submapper: null,
            headerFormat: VirtualHardwareNesHeaderFormat.INes,
            explicitRam: false);
        var cartridge = new Mapper34Cartridge("TEST.M34.LEGACY.NINA");
        cartridge.LoadImage(image);

        Assert.Equal(Mapper34BoardVariant.Nina001, cartridge.BoardVariant);
        Assert.Equal(8 * 1024, cartridge.PrgRamSizeBytes);
        Assert.Equal(64 * 1024, cartridge.ChrRomSizeBytes);
    }

    [Theory]
    [InlineData(1, Mapper34BoardVariant.Nina001)]
    [InlineData(2, Mapper34BoardVariant.Bnrom)]
    public void Nes20_submapper_selects_the_explicit_physical_board(int submapper, Mapper34BoardVariant expected)
    {
        var image = expected == Mapper34BoardVariant.Nina001
            ? CreateNinaImage(submapper)
            : CreateBnromImage(submapper);
        var cartridge = new Mapper34Cartridge("TEST.M34.SUBMAPPER");
        cartridge.LoadImage(image);

        Assert.Equal(expected, cartridge.BoardVariant);
    }

    [Fact]
    public void Bnrom_power_on_selects_first_32k_prg_bank()
    {
        var cartridge = CreateBnrom();
        var cpu = CpuRomTarget(cartridge);

        Assert.Equal(Mapper34BoardVariant.Bnrom, cartridge.BoardVariant);
        Assert.Equal(0, cartridge.SelectedBnromPrgBank);
        Assert.Equal((byte)0x40, cpu.Read!(0x8000));
    }

    [Fact]
    public void Bnrom_two_bit_latch_switches_the_entire_32k_prg_window()
    {
        var cartridge = CreateBnrom(writeSiteByte: 0x03);
        var cpu = CpuRomTarget(cartridge);

        cpu.Write!(0x9000, 0x03);

        Assert.Equal((byte)0x03, cartridge.BnromBankRegister);
        Assert.Equal(3, cartridge.SelectedBnromPrgBank);
        Assert.Equal((byte)0x43, cpu.Read!(0x8000));
        Assert.Equal(1UL, cartridge.MapperWriteCount);
    }

    [Fact]
    public void Bnrom_bus_conflict_ands_cpu_data_with_the_current_prg_rom_output()
    {
        var cartridge = CreateBnrom(writeSiteByte: 0x01);
        var cpu = CpuRomTarget(cartridge);

        cpu.Write!(0x9000, 0x03);

        Assert.True(cartridge.BusConflictsEnabled);
        Assert.Equal((byte)0x03, cartridge.LastMapperWriteData);
        Assert.Equal((byte)0x01, cartridge.LastEffectiveMapperWriteData);
        Assert.Equal((byte)0x01, cartridge.BnromBankRegister);
        Assert.Equal(1, cartridge.SelectedBnromPrgBank);
        Assert.Equal(1UL, cartridge.BusConflictModifiedWriteCount);
    }

    [Fact]
    public void Bnrom_chr_ram_is_an_unbanked_read_write_pattern_device()
    {
        var cartridge = CreateBnrom();
        var ppu = PpuTarget(cartridge);

        Assert.NotNull(ppu.Write);
        ppu.Write!(0x0123, 0xA5);
        ppu.Write!(0x1123, 0x5A);

        Assert.Equal((byte)0xA5, ppu.Read!(0x0123));
        Assert.Equal((byte)0x5A, ppu.Read!(0x1123));
        Assert.Equal(2UL, cartridge.PpuWriteCount);
    }

    [Fact]
    public void Bnrom_can_fit_one_fixed_eight_kib_chr_rom_instead_of_chr_ram()
    {
        var image = CreateBnromImage(submapper: 2, chrRom: CreateFixedChr8K());
        var cartridge = new Mapper34Cartridge("TEST.M34.BNROM.CHRROM");
        cartridge.LoadImage(image);
        var ppu = PpuTarget(cartridge);

        Assert.Equal(0, cartridge.ChrRamSizeBytes);
        Assert.Equal(8 * 1024, cartridge.ChrRomSizeBytes);
        Assert.Null(ppu.Write);
        Assert.Equal((byte)0x6A, ppu.Read!(0x0123));
    }

    [Fact]
    public void Bnrom_submapper_two_can_fit_the_documented_optional_eight_kib_prg_ram_extension()
    {
        var image = CreateBnromImage(submapper: 2, prgRamSize: 8 * 1024);
        var cartridge = new Mapper34Cartridge("TEST.M34.BNROM.RAM");
        cartridge.LoadImage(image);
        var ram = CpuRamTarget(cartridge);

        ram.Write!(0x6123, 0xCC);

        Assert.Equal(8 * 1024, cartridge.PrgRamSizeBytes);
        Assert.Equal((byte)0xCC, ram.Read!(0x6123));
        Assert.Equal(1UL, cartridge.PrgRamWriteCount);
    }

    [Fact]
    public void Nina001_power_on_exposes_first_prg_and_first_chr_banks()
    {
        var cartridge = CreateNina();
        var rom = CpuRomTarget(cartridge);
        var ppu = PpuTarget(cartridge);

        Assert.Equal(Mapper34BoardVariant.Nina001, cartridge.BoardVariant);
        Assert.Equal(0, cartridge.SelectedNinaPrgBank);
        Assert.Equal(0, cartridge.SelectedNinaChrBank0);
        Assert.Equal(0, cartridge.SelectedNinaChrBank1);
        Assert.Equal((byte)0x40, rom.Read!(0x8000));
        Assert.Equal((byte)0x60, ppu.Read!(0x0000));
        Assert.Equal((byte)0x60, ppu.Read!(0x1000));
    }

    [Fact]
    public void Nina001_7ffd_selects_prg_bank_and_the_same_write_reaches_prg_ram()
    {
        var cartridge = CreateNina();
        var ram = CpuRamTarget(cartridge);
        var rom = CpuRomTarget(cartridge);

        ram.Write!(0x7FFD, 0x01);

        Assert.Equal((byte)0x01, cartridge.NinaPrgRegister);
        Assert.Equal(1, cartridge.SelectedNinaPrgBank);
        Assert.Equal((byte)0x41, rom.Read!(0x8000));
        Assert.Equal((byte)0x01, ram.Read!(0x7FFD));
        Assert.Equal(1UL, cartridge.MapperWriteCount);
        Assert.Equal(1UL, cartridge.PrgRamWriteCount);
    }

    [Fact]
    public void Nina001_7ffe_selects_lower_four_kib_chr_bank_and_writes_prg_ram()
    {
        var cartridge = CreateNina();
        var ram = CpuRamTarget(cartridge);
        var ppu = PpuTarget(cartridge);

        ram.Write!(0x7FFE, 0x05);

        Assert.Equal((byte)0x05, cartridge.NinaChr0Register);
        Assert.Equal(5, cartridge.SelectedNinaChrBank0);
        Assert.Equal((byte)0x65, ppu.Read!(0x0000));
        Assert.Equal((byte)0x05, ram.Read!(0x7FFE));
    }

    [Fact]
    public void Nina001_7fff_selects_upper_four_kib_chr_bank_and_writes_prg_ram()
    {
        var cartridge = CreateNina();
        var ram = CpuRamTarget(cartridge);
        var ppu = PpuTarget(cartridge);

        ram.Write!(0x7FFF, 0x0E);

        Assert.Equal((byte)0x0E, cartridge.NinaChr1Register);
        Assert.Equal(14, cartridge.SelectedNinaChrBank1);
        Assert.Equal((byte)0x6E, ppu.Read!(0x1000));
        Assert.Equal((byte)0x0E, ram.Read!(0x7FFF));
    }

    [Fact]
    public void Nina001_register_addresses_read_back_prg_ram_not_a_separate_register_bus()
    {
        var cartridge = CreateNina();
        var ram = CpuRamTarget(cartridge);

        ram.Write!(0x7FFD, 0x81);
        ram.Write!(0x7FFE, 0xA5);
        ram.Write!(0x7FFF, 0xB6);

        Assert.Equal((byte)0x81, ram.Read!(0x7FFD));
        Assert.Equal((byte)0xA5, ram.Read!(0x7FFE));
        Assert.Equal((byte)0xB6, ram.Read!(0x7FFF));
        Assert.Equal((byte)0x01, cartridge.NinaPrgRegister);
        Assert.Equal((byte)0x05, cartridge.NinaChr0Register);
        Assert.Equal((byte)0x06, cartridge.NinaChr1Register);
    }

    [Fact]
    public void Nina001_has_no_cpu_rom_bus_conflict_path()
    {
        var cartridge = CreateNina();
        var rom = CpuRomTarget(cartridge);
        var ram = CpuRamTarget(cartridge);

        Assert.False(cartridge.BusConflictsEnabled);
        Assert.Null(rom.Write);
        Assert.Empty(rom.WriteConditions);
        ram.Write!(0x7FFD, 0x01);
        Assert.Equal((byte)0x01, cartridge.NinaPrgRegister);
        Assert.Equal(0UL, cartridge.BusConflictModifiedWriteCount);
    }

    [Theory]
    [InlineData(VirtualHardwareNesMirroring.Horizontal, DigitalLevel.High)]
    [InlineData(VirtualHardwareNesMirroring.Vertical, DigitalLevel.Low)]
    public void Both_mapper34_board_families_use_fixed_header_selected_ciram_wiring(
        VirtualHardwareNesMirroring mirroring,
        DigitalLevel expected)
    {
        var cartridges = new[] { CreateBnrom(mirroring: mirroring), CreateNina(mirroring) };

        foreach (var cartridge in cartridges)
        {
            var staticFacet = Assert.IsAssignableFrom<ICompiledStaticCombinationalComponent>(cartridge);
            Assert.True(staticFacet.TryEvaluateCompiledStaticOutput(
                cartridge.CiramA10,
                pin => ReferenceEquals(pin, cartridge.PpuAddress.Pins[11])
                    ? DigitalLevel.High
                    : DigitalLevel.Low,
                out var drive));
            Assert.Equal(expected, drive.Level);
        }
    }

    [Fact]
    public void Bnrom_bank_latch_samples_the_physical_cpu_data_bus_on_falling_m2()
    {
        var cartridge = CreateBnrom(writeSiteByte: 0x01);
        var board = new VirtualHardwareBoard("M34.BNROM.PHYSICAL.LATCH");
        board.Add(cartridge);

        var vcc = AttachSource(board, "VCC", cartridge.Vcc);
        var gnd = AttachSource(board, "GND", cartridge.Gnd);
        var rw = AttachSource(board, "RW", cartridge.CpuReadWrite);
        var m2 = AttachSource(board, "M2", cartridge.CpuM2);
        var romsel = AttachSource(board, "ROMSEL", cartridge.CpuRomSelectBar);
        var address = cartridge.CpuAddress.Pins.Select((pin, bit) => AttachSource(board, $"A{bit}", pin)).ToArray();
        var data = cartridge.CpuData.Pins.Select((pin, bit) => AttachSource(board, $"D{bit}", pin)).ToArray();
        _ = new VirtualHardwareSimulator(board);

        gnd.Set(DigitalLevel.Low);
        vcc.Set(DigitalLevel.High);
        DriveAddress(address, 0x1000);
        DriveByte(data, 0x01);
        romsel.Set(DigitalLevel.Low);
        rw.Set(DigitalLevel.Low);
        m2.Set(DigitalLevel.High);

        Assert.Equal(0, cartridge.SelectedBnromPrgBank);
        m2.Set(DigitalLevel.Low);

        Assert.Equal(1, cartridge.SelectedBnromPrgBank);
        Assert.Equal(1UL, cartridge.MapperWriteCount);
        Assert.Equal((ushort)0x9000, cartridge.LastMapperWriteAddress);
    }

    [Fact]
    public void Mapper34_register_latches_commit_at_completed_cpu_bus_phase()
    {
        var bnrom = CreateBnrom();
        var nina = CreateNina();

        Assert.Equal(CompiledBusWritePhase.Complete, CpuRomTarget(bnrom).WritePhase);
        Assert.Equal(CompiledBusWritePhase.Complete, CpuRamTarget(nina).WritePhase);
    }

    [Fact]
    public void Generic_compiled_and_raw_physical_bnrom_execute_the_same_bank_switch_program()
    {
        var image = CreateBnromExecutionImage();
        var compiled = new RegionalNesVirtualMachine();
        var reference = new RegionalNesVirtualMachine();

        compiled.SetCompiledLabExecutionEnabled(true);
        compiled.InsertRom(image, "BNROM execution (Japan).nes");
        reference.InsertRom(image, "BNROM execution (Japan).nes");
        reference.Famicom.SetCompiledPhysicalMachineEnabled(false);

        compiled.PowerOn();
        reference.PowerOn();
        compiled.ReleaseReset();
        reference.ReleaseReset();
        const int masterCycles = 12_000;
        compiled.AdvanceMasterCycles(masterCycles);
        reference.AdvanceMasterCycles(masterCycles);

        var compiledCartridge = Assert.IsType<Mapper34Cartridge>(compiled.Slot.Cartridge);
        var referenceCartridge = Assert.IsType<Mapper34Cartridge>(reference.Slot.Cartridge);
        Assert.True(compiled.Famicom.CompiledLabMotherboardEnabled);
        Assert.Equal(Mapper34BoardVariant.Bnrom, compiledCartridge.BoardVariant);
        Assert.Equal(1, compiledCartridge.SelectedBnromPrgBank);
        Assert.Equal(referenceCartridge.SelectedBnromPrgBank, compiledCartridge.SelectedBnromPrgBank);
        Assert.Equal(referenceCartridge.MapperWriteCount, compiledCartridge.MapperWriteCount);
        Assert.Equal(reference.Famicom.Cpu.ProgramCounter, compiled.Famicom.Cpu.ProgramCounter);
        Assert.Equal(reference.Famicom.Cpu.CompletedInstructionCount, compiled.Famicom.Cpu.CompletedInstructionCount);
        Assert.Equal(reference.Famicom.Ppu.InspectDiagnosticState(), compiled.Famicom.Ppu.InspectDiagnosticState());
        Assert.Equal(reference.Famicom.Ciram.InspectStateHash(), compiled.Famicom.Ciram.InspectStateHash());
    }

    [Fact]
    public void Generic_compiled_and_raw_physical_nina001_execute_the_same_prg_chr_register_program()
    {
        var image = CreateNinaExecutionImage();
        var compiled = new RegionalNesVirtualMachine();
        var reference = new RegionalNesVirtualMachine();

        compiled.SetCompiledLabExecutionEnabled(true);
        compiled.InsertRom(image, "NINA-001 execution (Japan).nes");
        reference.InsertRom(image, "NINA-001 execution (Japan).nes");
        reference.Famicom.SetCompiledPhysicalMachineEnabled(false);

        compiled.PowerOn();
        reference.PowerOn();
        compiled.ReleaseReset();
        reference.ReleaseReset();
        const int masterCycles = 18_000;
        compiled.AdvanceMasterCycles(masterCycles);
        reference.AdvanceMasterCycles(masterCycles);

        var compiledCartridge = Assert.IsType<Mapper34Cartridge>(compiled.Slot.Cartridge);
        var referenceCartridge = Assert.IsType<Mapper34Cartridge>(reference.Slot.Cartridge);
        Assert.True(compiled.Famicom.CompiledLabMotherboardEnabled);
        Assert.Equal(Mapper34BoardVariant.Nina001, compiledCartridge.BoardVariant);
        Assert.Equal(1, compiledCartridge.SelectedNinaPrgBank);
        Assert.Equal(5, compiledCartridge.SelectedNinaChrBank0);
        Assert.Equal(6, compiledCartridge.SelectedNinaChrBank1);
        Assert.Equal(referenceCartridge.MapperWriteCount, compiledCartridge.MapperWriteCount);
        Assert.Equal(referenceCartridge.PrgRamWriteCount, compiledCartridge.PrgRamWriteCount);
        Assert.Equal(reference.Famicom.Cpu.ProgramCounter, compiled.Famicom.Cpu.ProgramCounter);
        Assert.Equal(reference.Famicom.Cpu.CompletedInstructionCount, compiled.Famicom.Cpu.CompletedInstructionCount);
        Assert.Equal(reference.Famicom.Ppu.InspectDiagnosticState(), compiled.Famicom.Ppu.InspectDiagnosticState());
        Assert.Equal(reference.Famicom.Ciram.InspectStateHash(), compiled.Famicom.Ciram.InspectStateHash());
    }

    [Fact]
    public void Mapper34_rejects_four_screen_unknown_submapper_and_invalid_variant_geometry()
    {
        var cartridge = new Mapper34Cartridge("TEST.M34.INVALID");
        var fourScreen = CreateBnromImage(2) with { Mirroring = VirtualHardwareNesMirroring.FourScreen };
        var unknown = CreateBnromImage(3);
        var badBnrom = CreateImage(new byte[48 * 1024], [], 2, explicitRam: true,
            prgRamSize: 0, chrRamSize: 8 * 1024);
        var badNina = CreateImage(CreatePrg32K(2), new byte[12 * 1024], 1, explicitRam: true,
            prgRamSize: 8 * 1024, chrRamSize: 0);

        Assert.Throws<NotSupportedException>(() => cartridge.LoadImage(fourScreen));
        Assert.Throws<NotSupportedException>(() => cartridge.LoadImage(unknown));
        Assert.Throws<ArgumentException>(() => cartridge.LoadImage(badBnrom));
        Assert.Throws<NotSupportedException>(() => cartridge.LoadImage(badNina));
    }

    [Fact]
    public void Factory_constructs_mapper_thirty_four_as_the_resolved_replaceable_hardware()
    {
        var image = CreateNinaImage(1);

        var cartridge = VirtualCartridgeHardwareFactory.Create(image);

        var mapper34 = Assert.IsType<Mapper34Cartridge>(cartridge);
        Assert.Equal(34, mapper34.MapperNumber);
        Assert.Equal(Mapper34BoardVariant.Nina001, mapper34.BoardVariant);
        Assert.True(mapper34.IsInserted);
    }

    private static Mapper34Cartridge CreateBnrom(
        byte writeSiteByte = 0x03,
        VirtualHardwareNesMirroring mirroring = VirtualHardwareNesMirroring.Horizontal)
    {
        var image = CreateBnromImage(2, mirroring: mirroring, writeSiteByte: writeSiteByte);
        var cartridge = new Mapper34Cartridge("TEST.M34.BNROM");
        cartridge.LoadImage(image);
        return cartridge;
    }

    private static Mapper34Cartridge CreateNina(
        VirtualHardwareNesMirroring mirroring = VirtualHardwareNesMirroring.Horizontal)
    {
        var cartridge = new Mapper34Cartridge("TEST.M34.NINA");
        var image = CreateNinaImage(1) with { Mirroring = mirroring };
        cartridge.LoadImage(image);
        return cartridge;
    }

    private static VirtualHardwareNesRomImage CreateBnromImage(
        int? submapper,
        byte[]? chrRom = null,
        int prgRamSize = 0,
        VirtualHardwareNesMirroring mirroring = VirtualHardwareNesMirroring.Horizontal,
        byte writeSiteByte = 0x03)
    {
        var prg = CreatePrg32K(4);
        for (var bank = 0; bank < 4; bank++)
            prg[(bank * 32 * 1024) + 0x1000] = writeSiteByte;
        var chr = chrRom ?? [];
        return CreateImage(prg, chr, submapper, mirroring, explicitRam: true,
            prgRamSize: prgRamSize,
            chrRamSize: chr.Length == 0 ? 8 * 1024 : 0);
    }

    private static VirtualHardwareNesRomImage CreateNinaImage(int? submapper)
    {
        return CreateImage(CreatePrg32K(2), CreateChr4K(16), submapper,
            explicitRam: true, prgRamSize: 8 * 1024, chrRamSize: 0);
    }

    private static byte[] CreatePrg32K(int banks)
    {
        var prg = new byte[banks * 32 * 1024];
        for (var bank = 0; bank < banks; bank++)
            Array.Fill(prg, (byte)(0x40 + bank), bank * 32 * 1024, 32 * 1024);
        return prg;
    }

    private static byte[] CreateChr4K(int banks)
    {
        var chr = new byte[banks * 4 * 1024];
        for (var bank = 0; bank < banks; bank++)
            Array.Fill(chr, (byte)(0x60 + bank), bank * 4 * 1024, 4 * 1024);
        return chr;
    }

    private static byte[] CreateFixedChr8K()
    {
        var chr = new byte[8 * 1024];
        Array.Fill(chr, (byte)0x6A);
        return chr;
    }

    private static VirtualHardwareNesRomImage CreateBnromExecutionImage()
    {
        var prg = new byte[4 * 32 * 1024];
        Array.Fill(prg, (byte)0xEA);
        var program = new byte[]
        {
            0x78,
            0xA9, 0x01,
            0x8D, 0x00, 0x80,
            0x4C, 0x06, 0x81
        };

        for (var bank = 0; bank < 4; bank++)
        {
            var bankBase = bank * 32 * 1024;
            prg[bankBase] = 0x01;
            program.CopyTo(prg, bankBase + 0x0100);
            WriteVectors(prg, bankBase, 0x8100);
        }

        return CreateImage(prg, [], 2, explicitRam: true, prgRamSize: 0, chrRamSize: 8 * 1024);
    }

    private static VirtualHardwareNesRomImage CreateNinaExecutionImage()
    {
        var prg = new byte[2 * 32 * 1024];
        Array.Fill(prg, (byte)0xEA);
        var program = new byte[]
        {
            0x78,
            0xA9, 0x01,
            0x8D, 0xFD, 0x7F,
            0xA9, 0x05,
            0x8D, 0xFE, 0x7F,
            0xA9, 0x06,
            0x8D, 0xFF, 0x7F,
            0x4C, 0x10, 0x81
        };

        for (var bank = 0; bank < 2; bank++)
        {
            var bankBase = bank * 32 * 1024;
            program.CopyTo(prg, bankBase + 0x0100);
            WriteVectors(prg, bankBase, 0x8100);
        }

        return CreateImage(prg, CreateChr4K(16), 1,
            explicitRam: true, prgRamSize: 8 * 1024, chrRamSize: 0);
    }

    private static void WriteVectors(byte[] prg, int bankBase, ushort address)
    {
        var lo = (byte)address;
        var hi = (byte)(address >> 8);
        prg[bankBase + 0x7FFA] = lo; prg[bankBase + 0x7FFB] = hi;
        prg[bankBase + 0x7FFC] = lo; prg[bankBase + 0x7FFD] = hi;
        prg[bankBase + 0x7FFE] = lo; prg[bankBase + 0x7FFF] = hi;
    }

    private static VirtualHardwareNesRomImage CreateImage(
        byte[] prg,
        byte[] chr,
        int? submapper,
        VirtualHardwareNesMirroring mirroring = VirtualHardwareNesMirroring.Horizontal,
        VirtualHardwareNesHeaderFormat headerFormat = VirtualHardwareNesHeaderFormat.Nes20,
        bool explicitRam = true,
        int prgRamSize = 0,
        int chrRamSize = 0) =>
        new(
            headerFormat,
            MapperNumber: 34,
            SubmapperNumber: submapper,
            PrgRomSizeBytes: prg.Length,
            ChrRomSizeBytes: chr.Length,
            HasTrainer: false,
            HasBatteryBackedMemory: false,
            mirroring,
            VirtualHardwareNesHeaderTiming.Ntsc,
            prg,
            chr)
        {
            PrgRamSizeBytes = prgRamSize,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = chrRamSize,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = explicitRam
        };

    private static CompiledBusTargetDescriptor CpuRomTarget(Mapper34Cartridge cartridge) =>
        Assert.Single(
            ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.AddressPins.Count == 15
                && target.ReadConditions.Any(condition => ReferenceEquals(condition.Pin, cartridge.CpuRomSelectBar)
                    && condition.RequiredLevel == DigitalLevel.Low));

    private static CompiledBusTargetDescriptor CpuRamTarget(Mapper34Cartridge cartridge) =>
        Assert.Single(
            ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.AddressPins.Count == 15
                && target.ReadConditions.Any(condition => ReferenceEquals(condition.Pin, cartridge.CpuRomSelectBar)
                    && condition.RequiredLevel == DigitalLevel.High));

    private static CompiledBusTargetDescriptor PpuTarget(Mapper34Cartridge cartridge) =>
        Assert.Single(
            ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.AddressPins.Count == 14);

    private static DigitalSignalSource AttachSource(VirtualHardwareBoard board, string id, DigitalPin pin)
    {
        var source = board.Add(new DigitalSignalSource($"TEST.{id}", DigitalLevel.HighImpedance));
        board.Connect($"TEST.{id}.NET", source.Output, pin);
        return source;
    }

    private static void DriveByte(IReadOnlyList<DigitalSignalSource> sources, byte value)
    {
        for (var bit = 0; bit < sources.Count; bit++)
            sources[bit].Set((value & (1 << bit)) != 0 ? DigitalLevel.High : DigitalLevel.Low);
    }

    private static void DriveAddress(IReadOnlyList<DigitalSignalSource> sources, ushort value)
    {
        for (var bit = 0; bit < sources.Count; bit++)
            sources[bit].Set((value & (1 << bit)) != 0 ? DigitalLevel.High : DigitalLevel.Low);
    }
}
