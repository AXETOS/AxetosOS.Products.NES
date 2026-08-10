using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Machines.Nes;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareGxromCartridgeTests
{
    [Fact]
    public void Power_on_selects_first_prg_and_chr_banks()
    {
        var cartridge = CreateCartridge(prgBanks: 4, chrBanks: 4);
        var cpu = CpuTarget(cartridge);
        var ppu = PpuTarget(cartridge);

        Assert.Equal((byte)0x40, cpu.Read!(0x8000));
        Assert.Equal((byte)0x60, ppu.Read!(0x0012));
        Assert.Equal(0, cartridge.SelectedPrgBank);
        Assert.Equal(0, cartridge.SelectedChrBank);
        Assert.Equal((byte)0x00, cartridge.BankRegister);
    }

    [Fact]
    public void Quad_latch_switches_prg_and_chr_windows_from_the_same_register()
    {
        var cartridge = CreateCartridge(prgBanks: 4, chrBanks: 4);
        var cpu = CpuTarget(cartridge);
        var ppu = PpuTarget(cartridge);

        cpu.Write!(0x9000, 0x21);

        Assert.Equal((byte)0x21, cartridge.BankRegister);
        Assert.Equal(2, cartridge.SelectedPrgBank);
        Assert.Equal(1, cartridge.SelectedChrBank);
        Assert.Equal((byte)0x42, cpu.Read!(0x8000));
        Assert.Equal((byte)0x61, ppu.Read!(0x0012));
        Assert.Equal(1UL, cartridge.MapperWriteCount);
    }

    [Fact]
    public void Fitted_rom_address_lines_mask_prg_and_chr_banks_independently_and_unwired_latch_bits_are_absent()
    {
        var cartridge = CreateCartridge(prgBanks: 2, chrBanks: 2);
        var cpu = CpuTarget(cartridge);
        var ppu = PpuTarget(cartridge);

        cpu.Write!(0x9000, 0xFF);

        Assert.Equal((byte)0x11, cartridge.BankRegister);
        Assert.Equal(1, cartridge.SelectedPrgBank);
        Assert.Equal(1, cartridge.SelectedChrBank);
        Assert.Equal((byte)0x41, cpu.Read!(0x8000));
        Assert.Equal((byte)0x61, ppu.Read!(0x0012));
    }

    [Fact]
    public void Standard_board_bus_conflict_ands_cpu_data_with_current_mapped_prg_rom()
    {
        var cartridge = CreateCartridge(prgBanks: 4, chrBanks: 4, writeSiteByte: 0x11);
        var cpu = CpuTarget(cartridge);

        cpu.Write!(0x9000, 0x31);

        Assert.True(cartridge.BusConflictsEnabled);
        Assert.Equal((byte)0x31, cartridge.LastMapperWriteData);
        Assert.Equal((byte)0x11, cartridge.LastEffectiveMapperWriteData);
        Assert.Equal((byte)0x11, cartridge.BankRegister);
        Assert.Equal(1, cartridge.SelectedPrgBank);
        Assert.Equal(1, cartridge.SelectedChrBank);
        Assert.Equal(1UL, cartridge.BusConflictModifiedWriteCount);
    }

    [Theory]
    [InlineData(VirtualHardwareNesMirroring.Horizontal, DigitalLevel.High)]
    [InlineData(VirtualHardwareNesMirroring.Vertical, DigitalLevel.Low)]
    public void Fixed_board_mirroring_routes_the_expected_ppu_address_line_to_ciram_a10(
        VirtualHardwareNesMirroring mirroring,
        DigitalLevel expected)
    {
        var cartridge = CreateCartridge(prgBanks: 4, chrBanks: 4, mirroring: mirroring);
        var staticFacet = Assert.IsAssignableFrom<ICompiledStaticCombinationalComponent>(cartridge);

        Assert.True(staticFacet.TryEvaluateCompiledStaticOutput(
            cartridge.CiramA10,
            pin => ReferenceEquals(pin, cartridge.PpuAddress.Pins[11])
                ? DigitalLevel.High
                : DigitalLevel.Low,
            out var drive));
        Assert.Equal(expected, drive.Level);
    }

    [Fact]
    public void Bank_latch_samples_the_physical_cpu_data_bus_on_falling_M2_edge()
    {
        var cartridge = CreateCartridge(prgBanks: 4, chrBanks: 4, writeSiteByte: 0x21);
        var board = new VirtualHardwareBoard("GXROM.PHYSICAL.LATCH.TEST");
        board.Add(cartridge);

        var vcc = AttachSource(board, "VCC", cartridge.Vcc);
        var gnd = AttachSource(board, "GND", cartridge.Gnd);
        var rw = AttachSource(board, "RW", cartridge.CpuReadWrite);
        var m2 = AttachSource(board, "M2", cartridge.CpuM2);
        var romsel = AttachSource(board, "ROMSEL", cartridge.CpuRomSelectBar);
        var address = cartridge.CpuAddress.Pins
            .Select((pin, bit) => AttachSource(board, $"A{bit}", pin))
            .ToArray();
        var data = cartridge.CpuData.Pins
            .Select((pin, bit) => AttachSource(board, $"D{bit}", pin))
            .ToArray();
        _ = new VirtualHardwareSimulator(board);

        gnd.Set(DigitalLevel.Low);
        vcc.Set(DigitalLevel.High);
        DriveAddress(address, 0x1000);
        DriveByte(data, 0x21);
        romsel.Set(DigitalLevel.Low);
        rw.Set(DigitalLevel.Low);
        m2.Set(DigitalLevel.High);

        Assert.Equal(0, cartridge.SelectedPrgBank);
        Assert.Equal(0, cartridge.SelectedChrBank);
        Assert.Equal(0UL, cartridge.MapperWriteCount);

        m2.Set(DigitalLevel.Low);

        Assert.Equal((byte)0x21, cartridge.BankRegister);
        Assert.Equal(2, cartridge.SelectedPrgBank);
        Assert.Equal(1, cartridge.SelectedChrBank);
        Assert.Equal(1UL, cartridge.MapperWriteCount);
        Assert.Equal((ushort)0x9000, cartridge.LastMapperWriteAddress);
    }

    [Fact]
    public void Compiled_mapper_write_is_latched_at_bus_cycle_completion()
    {
        var cartridge = CreateCartridge(prgBanks: 4, chrBanks: 4);
        var cpu = CpuTarget(cartridge);

        Assert.Equal(CompiledBusWritePhase.Complete, cpu.WritePhase);
        Assert.Contains(cpu.WriteConditions, condition => ReferenceEquals(condition.Pin, cartridge.CpuReadWrite)
            && condition.RequiredLevel == DigitalLevel.Low);
        Assert.Contains(cpu.WriteConditions, condition => ReferenceEquals(condition.Pin, cartridge.CpuRomSelectBar)
            && condition.RequiredLevel == DigitalLevel.Low);
    }

    [Fact]
    public void Chr_rom_is_read_only_on_the_compiled_ppu_bus()
    {
        var cartridge = CreateCartridge(prgBanks: 4, chrBanks: 4);
        var ppu = PpuTarget(cartridge);
        var before = ppu.Read!(0x0012);

        Assert.Null(ppu.Write);
        Assert.Empty(ppu.WriteConditions);
        Assert.Equal(before, ppu.Read!(0x0012));
        Assert.Equal(0UL, cartridge.PpuWriteCount);
    }

    [Fact]
    public void Generic_compiled_and_raw_physical_gxrom_execute_the_same_dual_bank_switch_program()
    {
        var image = CreateExecutionImage();
        var compiled = new RegionalNesVirtualMachine();
        var reference = new RegionalNesVirtualMachine();

        compiled.SetCompiledLabExecutionEnabled(true);
        compiled.InsertRom(image, "GxROM execution (Japan).nes");
        reference.InsertRom(image, "GxROM execution (Japan).nes");
        reference.Famicom.SetCompiledPhysicalMachineEnabled(false);

        compiled.PowerOn();
        reference.PowerOn();
        compiled.ReleaseReset();
        reference.ReleaseReset();

        const int masterCycles = 12_000;
        compiled.AdvanceMasterCycles(masterCycles);
        reference.AdvanceMasterCycles(masterCycles);

        var compiledCartridge = Assert.IsType<GxromCartridge>(compiled.Slot.Cartridge);
        var referenceCartridge = Assert.IsType<GxromCartridge>(reference.Slot.Cartridge);
        Assert.True(compiled.Famicom.CompiledLabMotherboardEnabled);
        Assert.Equal((byte)0x21, compiledCartridge.BankRegister);
        Assert.Equal((byte)0x21, referenceCartridge.BankRegister);
        Assert.Equal(2, compiledCartridge.SelectedPrgBank);
        Assert.Equal(1, compiledCartridge.SelectedChrBank);
        Assert.Equal(referenceCartridge.MapperWriteCount, compiledCartridge.MapperWriteCount);
        Assert.Equal(reference.Famicom.Cpu.ProgramCounter, compiled.Famicom.Cpu.ProgramCounter);
        Assert.Equal(reference.Famicom.Cpu.CompletedInstructionCount, compiled.Famicom.Cpu.CompletedInstructionCount);
        Assert.Equal(reference.Famicom.Ppu.InspectDiagnosticState(), compiled.Famicom.Ppu.InspectDiagnosticState());
        Assert.Equal(reference.Famicom.Ciram.InspectStateHash(), compiled.Famicom.Ciram.InspectStateHash());
    }

    [Fact]
    public void Standard_gxrom_rejects_ram_four_screen_and_invalid_rom_geometry()
    {
        var cartridge = new GxromCartridge("TEST.GXROM.INVALID");
        var validPrg = CreatePrg(4);
        var validChr = CreateChr(4);
        var badPrg = CreateImage(new byte[48 * 1024], validChr, submapper: 0);
        var noChr = CreateImage(validPrg, [], submapper: 0);
        var withRam = CreateImage(validPrg, validChr, submapper: 0) with
        {
            PrgRamSizeBytes = 8 * 1024,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 0,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        };
        var fourScreen = CreateImage(validPrg, validChr, submapper: 0,
            mirroring: VirtualHardwareNesMirroring.FourScreen);

        Assert.Throws<ArgumentException>(() => cartridge.LoadImage(badPrg));
        Assert.Throws<ArgumentException>(() => cartridge.LoadImage(noChr));
        Assert.Throws<NotSupportedException>(() => cartridge.LoadImage(withRam));
        Assert.Throws<NotSupportedException>(() => cartridge.LoadImage(fourScreen));
    }

    [Fact]
    public void Undefined_nonzero_mapper_sixty_six_submapper_is_rejected_instead_of_guessing_a_board_variant()
    {
        var cartridge = new GxromCartridge("TEST.GXROM.SUBMAPPER");
        var image = CreateImage(CreatePrg(4), CreateChr(4), submapper: 1);

        Assert.Throws<NotSupportedException>(() => cartridge.LoadImage(image));
    }

    [Fact]
    public void Factory_constructs_mapper_sixty_six_as_replaceable_gxrom_hardware()
    {
        var image = CreateImage(CreatePrg(4), CreateChr(4), submapper: 0);

        var cartridge = VirtualCartridgeHardwareFactory.Create(image);

        Assert.Equal(66, cartridge.MapperNumber);
        Assert.IsType<GxromCartridge>(cartridge);
        Assert.True(cartridge.IsInserted);
    }

    private static GxromCartridge CreateCartridge(
        int prgBanks,
        int chrBanks,
        byte writeSiteByte = 0xFF,
        VirtualHardwareNesMirroring mirroring = VirtualHardwareNesMirroring.Horizontal)
    {
        var prg = CreatePrg(prgBanks);
        for (var bank = 0; bank < prgBanks; bank++)
            prg[(bank * 32 * 1024) + 0x1000] = writeSiteByte;

        var image = CreateImage(prg, CreateChr(chrBanks), submapper: 0, mirroring: mirroring) with
        {
            PrgRamSizeBytes = 0,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 0,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        };
        var cartridge = new GxromCartridge("TEST.GXROM");
        cartridge.LoadImage(image);
        return cartridge;
    }

    private static byte[] CreatePrg(int prgBanks)
    {
        var prg = new byte[prgBanks * 32 * 1024];
        for (var bank = 0; bank < prgBanks; bank++)
            Array.Fill(prg, (byte)(0x40 + bank), bank * 32 * 1024, 32 * 1024);
        return prg;
    }

    private static byte[] CreateChr(int chrBanks)
    {
        var chr = new byte[chrBanks * 8 * 1024];
        for (var bank = 0; bank < chrBanks; bank++)
            Array.Fill(chr, (byte)(0x60 + bank), bank * 8 * 1024, 8 * 1024);
        return chr;
    }

    private static VirtualHardwareNesRomImage CreateExecutionImage()
    {
        var prg = new byte[4 * 32 * 1024];
        Array.Fill(prg, (byte)0xEA);

        // $8000 in the initial bank contains the exact latch value so the
        // physical bus-conflict AND leaves the intended write unchanged.
        var program = new byte[]
        {
            0x78,                   // SEI
            0xA9, 0x21,             // LDA #$21 - PRG bank 2 + CHR bank 1
            0x8D, 0x00, 0x80,       // STA $8000
            0x4C, 0x06, 0x81        // JMP $8106 in the newly selected PRG bank
        };

        for (var bank = 0; bank < 4; bank++)
        {
            var bankBase = bank * 32 * 1024;
            prg[bankBase] = 0x21;
            program.CopyTo(prg, bankBase + 0x0100);
            prg[bankBase + 0x7FFA] = 0x00; prg[bankBase + 0x7FFB] = 0x81;
            prg[bankBase + 0x7FFC] = 0x00; prg[bankBase + 0x7FFD] = 0x81;
            prg[bankBase + 0x7FFE] = 0x00; prg[bankBase + 0x7FFF] = 0x81;
        }

        return CreateImage(prg, CreateChr(4), submapper: 0) with
        {
            PrgRamSizeBytes = 0,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 0,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        };
    }

    private static VirtualHardwareNesRomImage CreateImage(
        byte[] prg,
        byte[] chr,
        int? submapper,
        VirtualHardwareNesMirroring mirroring = VirtualHardwareNesMirroring.Horizontal,
        VirtualHardwareNesHeaderFormat headerFormat = VirtualHardwareNesHeaderFormat.Nes20) =>
        new(
            headerFormat,
            MapperNumber: 66,
            SubmapperNumber: submapper,
            PrgRomSizeBytes: prg.Length,
            ChrRomSizeBytes: chr.Length,
            HasTrainer: false,
            HasBatteryBackedMemory: false,
            mirroring,
            VirtualHardwareNesHeaderTiming.Ntsc,
            prg,
            chr);

    private static CompiledBusTargetDescriptor CpuTarget(GxromCartridge cartridge) =>
        ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets()
            .Single(target => target.AddressPins.Count == 15);

    private static CompiledBusTargetDescriptor PpuTarget(GxromCartridge cartridge) =>
        ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets()
            .Single(target => target.AddressPins.Count == 14);

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
