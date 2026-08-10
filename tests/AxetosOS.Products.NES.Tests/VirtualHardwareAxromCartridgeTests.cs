using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Machines.Nes;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareAxromCartridgeTests
{
    [Fact]
    public void Power_on_selects_first_thirty_two_kib_prg_bank_and_nametable_zero()
    {
        var cartridge = CreateCartridge(prgBanks: 4);
        var cpu = CpuTarget(cartridge);

        Assert.Equal((byte)0x40, cpu.Read!(0x8000));
        Assert.Equal((byte)0x40, cpu.Read!(0xFFFF));
        Assert.Equal(0, cartridge.SelectedPrgBank);
        Assert.Equal(0, cartridge.SelectedNametablePage);
        Assert.Equal((byte)0x00, cartridge.BankRegister);
    }

    [Fact]
    public void Bank_latch_switches_the_entire_thirty_two_kib_prg_window()
    {
        var cartridge = CreateCartridge(prgBanks: 8);
        var cpu = CpuTarget(cartridge);

        cpu.Write!(0x9000, 0x05);

        Assert.Equal((byte)0x45, cpu.Read!(0x8000));
        Assert.Equal((byte)0x45, cpu.Read!(0xFFFF));
        Assert.Equal((byte)0x05, cartridge.BankRegister);
        Assert.Equal(5, cartridge.SelectedPrgBank);
        Assert.Equal(0, cartridge.SelectedNametablePage);
        Assert.Equal(1UL, cartridge.MapperWriteCount);
    }

    [Fact]
    public void Bank_latch_exposes_only_fitted_prg_address_lines_but_retains_ciram_select()
    {
        var cartridge = CreateCartridge(prgBanks: 2);
        var cpu = CpuTarget(cartridge);

        cpu.Write!(0x8000, 0x17);

        Assert.Equal((byte)0x11, cartridge.BankRegister);
        Assert.Equal(1, cartridge.SelectedPrgBank);
        Assert.Equal(1, cartridge.SelectedNametablePage);
        Assert.Equal((byte)0x41, cpu.Read!(0x8000));
    }

    [Fact]
    public void Chr_ram_round_trips_through_the_cartridge_compiled_ppu_facet()
    {
        var cartridge = CreateCartridge(prgBanks: 4);
        var ppu = PpuTarget(cartridge);

        ppu.Write!(0x0012, 0xA5);
        ppu.Write!(0x1FFE, 0x5A);

        Assert.Equal((byte)0xA5, ppu.Read!(0x0012));
        Assert.Equal((byte)0x5A, ppu.Read!(0x1FFE));
        Assert.Equal(2UL, cartridge.PpuWriteCount);
        Assert.Equal(2UL, cartridge.PpuReadCount);
        Assert.Equal(8 * 1024, cartridge.ChrRamSizeBytes);
    }

    [Theory]
    [InlineData(0x00, DigitalLevel.Low)]
    [InlineData(0x10, DigitalLevel.High)]
    public void Mapper_bit_four_drives_single_screen_ciram_a10(byte registerValue, DigitalLevel expected)
    {
        var cartridge = CreateCartridge(prgBanks: 4);
        var cpu = CpuTarget(cartridge);
        var dynamicFacet = Assert.IsAssignableFrom<ICompiledCombinationalComponent>(cartridge);

        cpu.Write!(0x8000, registerValue);

        Assert.True(dynamicFacet.TryEvaluateCompiledOutput(cartridge.CiramA10, _ => DigitalLevel.Low, out var drive));
        Assert.Equal(expected, drive.Level);
        Assert.Equal(registerValue == 0 ? 0 : 1, cartridge.SelectedNametablePage);
    }

    [Fact]
    public void Mapper_selected_ciram_a10_is_live_and_is_not_folded_as_static_topology()
    {
        var cartridge = CreateCartridge(prgBanks: 4);
        var staticFacet = Assert.IsAssignableFrom<ICompiledStaticCombinationalComponent>(cartridge);

        Assert.False(staticFacet.TryEvaluateCompiledStaticOutput(
            cartridge.CiramA10,
            _ => DigitalLevel.Low,
            out _));
    }

    [Fact]
    public void Nes20_submappers_select_board_local_bus_conflict_behavior()
    {
        var noConflict = CreateCartridge(prgBanks: 4, submapper: 1, firstBankByte: 0x11);
        var conflict = CreateCartridge(prgBanks: 4, submapper: 2, firstBankByte: 0x11);
        var noConflictCpu = CpuTarget(noConflict);
        var conflictCpu = CpuTarget(conflict);

        noConflictCpu.Write!(0x8000, 0x13);
        conflictCpu.Write!(0x8000, 0x13);

        Assert.False(noConflict.BusConflictsEnabled);
        Assert.Equal((byte)0x13, noConflict.BankRegister);
        Assert.Equal(3, noConflict.SelectedPrgBank);
        Assert.Equal(1, noConflict.SelectedNametablePage);

        Assert.True(conflict.BusConflictsEnabled);
        Assert.Equal((byte)0x11, conflict.BankRegister);
        Assert.Equal(1, conflict.SelectedPrgBank);
        Assert.Equal(1, conflict.SelectedNametablePage);
        Assert.Equal((byte)0x11, conflict.LastEffectiveMapperWriteData);
        Assert.Equal(1UL, conflict.BusConflictModifiedWriteCount);
    }

    [Fact]
    public void Legacy_mapper_seven_defaults_to_no_bus_conflicts()
    {
        var cartridge = CreateCartridge(prgBanks: 4, submapper: null, firstBankByte: 0x00,
            headerFormat: VirtualHardwareNesHeaderFormat.INes);
        var cpu = CpuTarget(cartridge);

        cpu.Write!(0x8000, 0x12);

        Assert.False(cartridge.BusConflictsEnabled);
        Assert.Equal((byte)0x12, cartridge.BankRegister);
        Assert.Equal(2, cartridge.SelectedPrgBank);
        Assert.Equal(1, cartridge.SelectedNametablePage);
        Assert.Equal(0UL, cartridge.BusConflictModifiedWriteCount);
    }

    [Fact]
    public void Mapper_bank_latch_samples_the_physical_cpu_data_bus_on_falling_M2_edge()
    {
        var cartridge = CreateCartridge(prgBanks: 4, submapper: 1);
        var board = new VirtualHardwareBoard("AXROM.PHYSICAL.LATCH.TEST");
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
        DriveAddress(address, 0x0000);
        DriveByte(data, 0x12);
        romsel.Set(DigitalLevel.Low);
        rw.Set(DigitalLevel.Low);
        m2.Set(DigitalLevel.High);

        Assert.Equal(0, cartridge.SelectedPrgBank);
        Assert.Equal(0, cartridge.SelectedNametablePage);
        Assert.Equal(0UL, cartridge.MapperWriteCount);

        m2.Set(DigitalLevel.Low);

        Assert.Equal(2, cartridge.SelectedPrgBank);
        Assert.Equal(1, cartridge.SelectedNametablePage);
        Assert.Equal((byte)0x12, cartridge.BankRegister);
        Assert.Equal(1UL, cartridge.MapperWriteCount);
        Assert.Equal((ushort)0x8000, cartridge.LastMapperWriteAddress);
        Assert.Equal(DigitalLevel.High, cartridge.CiramA10.DriveLevel);
    }

    [Fact]
    public void Compiled_mapper_write_is_latched_at_bus_cycle_completion()
    {
        var cartridge = CreateCartridge(prgBanks: 4);
        var cpu = CpuTarget(cartridge);

        Assert.Equal(CompiledBusWritePhase.Complete, cpu.WritePhase);
        Assert.Contains(cpu.WriteConditions, condition => ReferenceEquals(condition.Pin, cartridge.CpuReadWrite)
            && condition.RequiredLevel == DigitalLevel.Low);
        Assert.Contains(cpu.WriteConditions, condition => ReferenceEquals(condition.Pin, cartridge.CpuRomSelectBar)
            && condition.RequiredLevel == DigitalLevel.Low);
    }

    [Fact]
    public void Generic_compiled_and_raw_physical_axrom_execute_the_same_bank_and_nametable_switch_program()
    {
        var image = CreateExecutionImage();
        var compiled = new RegionalNesVirtualMachine();
        var reference = new RegionalNesVirtualMachine();

        compiled.SetCompiledLabExecutionEnabled(true);
        compiled.InsertRom(image, "AxROM execution (Japan).nes");
        reference.InsertRom(image, "AxROM execution (Japan).nes");
        reference.Famicom.SetCompiledPhysicalMachineEnabled(false);

        compiled.PowerOn();
        reference.PowerOn();
        compiled.ReleaseReset();
        reference.ReleaseReset();

        const int masterCycles = 12_000;
        compiled.AdvanceMasterCycles(masterCycles);
        reference.AdvanceMasterCycles(masterCycles);

        var compiledCartridge = Assert.IsType<AxromCartridge>(compiled.Slot.Cartridge);
        var referenceCartridge = Assert.IsType<AxromCartridge>(reference.Slot.Cartridge);
        Assert.True(compiled.Famicom.CompiledLabMotherboardEnabled);
        Assert.Equal((byte)0x11, compiledCartridge.BankRegister);
        Assert.Equal((byte)0x11, referenceCartridge.BankRegister);
        Assert.Equal(1, compiledCartridge.SelectedPrgBank);
        Assert.Equal(1, compiledCartridge.SelectedNametablePage);
        Assert.Equal(referenceCartridge.MapperWriteCount, compiledCartridge.MapperWriteCount);
        Assert.Equal(reference.Famicom.Cpu.ProgramCounter, compiled.Famicom.Cpu.ProgramCounter);
        Assert.Equal(reference.Famicom.Cpu.CompletedInstructionCount, compiled.Famicom.Cpu.CompletedInstructionCount);
        Assert.Equal(reference.Famicom.Ppu.InspectDiagnosticState(), compiled.Famicom.Ppu.InspectDiagnosticState());
        Assert.Equal(reference.Famicom.Ciram.InspectStateHash(), compiled.Famicom.Ciram.InspectStateHash());
    }

    [Fact]
    public void Standard_axrom_rejects_chr_rom_prg_ram_and_four_screen_hardware()
    {
        var cartridge = new AxromCartridge("TEST.AXROM.INVALID");
        var prg = new byte[4 * 32 * 1024];
        var withChrRom = CreateImage(prg, new byte[8 * 1024], submapper: 1);
        var withPrgRam = CreateImage(prg, [], submapper: 1) with
        {
            PrgRamSizeBytes = 8 * 1024,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 8 * 1024,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        };
        var fourScreen = CreateImage(prg, [], submapper: 1, mirroring: VirtualHardwareNesMirroring.FourScreen);

        Assert.Throws<NotSupportedException>(() => cartridge.LoadImage(withChrRom));
        Assert.Throws<NotSupportedException>(() => cartridge.LoadImage(withPrgRam));
        Assert.Throws<NotSupportedException>(() => cartridge.LoadImage(fourScreen));
    }

    [Fact]
    public void Factory_constructs_mapper_seven_as_replaceable_axrom_hardware()
    {
        var image = CreateImage(CreatePrg(4), [], submapper: 1) with
        {
            PrgRamSizeBytes = 0,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 8 * 1024,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        };

        var cartridge = VirtualCartridgeHardwareFactory.Create(image);

        Assert.Equal(7, cartridge.MapperNumber);
        Assert.IsType<AxromCartridge>(cartridge);
        Assert.True(cartridge.IsInserted);
    }

    private static AxromCartridge CreateCartridge(
        int prgBanks,
        int? submapper = 1,
        byte? firstBankByte = null,
        VirtualHardwareNesHeaderFormat headerFormat = VirtualHardwareNesHeaderFormat.Nes20)
    {
        var prg = CreatePrg(prgBanks);
        if (firstBankByte.HasValue) prg[0] = firstBankByte.Value;

        var image = CreateImage(prg, [], submapper, headerFormat: headerFormat) with
        {
            PrgRamSizeBytes = 0,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 8 * 1024,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = headerFormat == VirtualHardwareNesHeaderFormat.Nes20
        };
        var cartridge = new AxromCartridge("TEST.AXROM");
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

    private static VirtualHardwareNesRomImage CreateExecutionImage()
    {
        var prg = new byte[4 * 32 * 1024];
        Array.Fill(prg, (byte)0xEA);

        // The whole 32 KiB CPU window changes immediately after STA $8000, so
        // each physical PRG bank carries the same hand-off code at the same pins.
        var program = new byte[]
        {
            0x78,                   // SEI
            0xA9, 0x11,             // LDA #$11 - bank 1 + CIRAM page 1
            0x8D, 0x00, 0x80,       // STA $8000
            0x4C, 0x06, 0x80        // JMP $8006 in the newly selected bank
        };

        for (var bank = 0; bank < 4; bank++)
        {
            var bankBase = bank * 32 * 1024;
            program.CopyTo(prg, bankBase);
            prg[bankBase + 0x7FFA] = 0x00; prg[bankBase + 0x7FFB] = 0x80;
            prg[bankBase + 0x7FFC] = 0x00; prg[bankBase + 0x7FFD] = 0x80;
            prg[bankBase + 0x7FFE] = 0x00; prg[bankBase + 0x7FFF] = 0x80;
        }

        return CreateImage(prg, [], submapper: 1) with
        {
            PrgRamSizeBytes = 0,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 8 * 1024,
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
            MapperNumber: 7,
            SubmapperNumber: submapper,
            PrgRomSizeBytes: prg.Length,
            ChrRomSizeBytes: chr.Length,
            HasTrainer: false,
            HasBatteryBackedMemory: false,
            mirroring,
            VirtualHardwareNesHeaderTiming.Ntsc,
            prg,
            chr);

    private static CompiledBusTargetDescriptor CpuTarget(AxromCartridge cartridge) =>
        ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets()
            .Single(target => target.AddressPins.Count == 15);

    private static CompiledBusTargetDescriptor PpuTarget(AxromCartridge cartridge) =>
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
