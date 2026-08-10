using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Machines.Nes;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareUxromCartridgeTests
{
    [Fact]
    public void Power_on_selects_first_switchable_prg_bank_and_fixes_last_bank_high()
    {
        var cartridge = CreateCartridge(prgBanks: 4);
        var cpu = CpuTarget(cartridge);

        Assert.Equal((byte)0x40, cpu.Read!(0x8000));
        Assert.Equal((byte)0x43, cpu.Read!(0xC000));
        Assert.Equal(0, cartridge.SelectedPrgBank);
        Assert.Equal(3, cartridge.FixedPrgBank);
    }

    [Fact]
    public void Bank_latch_switches_only_the_lower_sixteen_kib_window()
    {
        var cartridge = CreateCartridge(prgBanks: 8);
        var cpu = CpuTarget(cartridge);

        cpu.Write!(0x8000, 0x05);

        Assert.Equal((byte)0x45, cpu.Read!(0x8000));
        Assert.Equal((byte)0x47, cpu.Read!(0xC000));
        Assert.Equal((byte)0x05, cartridge.BankRegister);
        Assert.Equal(5, cartridge.SelectedPrgBank);
        Assert.Equal(7, cartridge.FixedPrgBank);
        Assert.Equal(1UL, cartridge.MapperWriteCount);
    }

    [Fact]
    public void Bank_latch_exposes_only_address_lines_physically_populated_by_the_fitted_prg_rom()
    {
        var cartridge = CreateCartridge(prgBanks: 4);
        var cpu = CpuTarget(cartridge);

        cpu.Write!(0x8000, 0xFF);

        Assert.Equal((byte)0x03, cartridge.BankRegister);
        Assert.Equal(3, cartridge.SelectedPrgBank);
        Assert.Equal((byte)0x43, cpu.Read!(0x8000));
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
    [InlineData(VirtualHardwareNesMirroring.Vertical, 0x0400, DigitalLevel.High)]
    [InlineData(VirtualHardwareNesMirroring.Vertical, 0x0800, DigitalLevel.Low)]
    [InlineData(VirtualHardwareNesMirroring.Horizontal, 0x0400, DigitalLevel.Low)]
    [InlineData(VirtualHardwareNesMirroring.Horizontal, 0x0800, DigitalLevel.High)]
    public void Fixed_mirroring_is_exposed_as_static_cartridge_ciram_wiring(
        VirtualHardwareNesMirroring mirroring,
        ushort ppuAddress,
        DigitalLevel expected)
    {
        var cartridge = CreateCartridge(prgBanks: 4, mirroring: mirroring);
        var staticFacet = Assert.IsAssignableFrom<ICompiledStaticCombinationalComponent>(cartridge);

        Assert.True(staticFacet.TryEvaluateCompiledStaticOutput(
            cartridge.CiramA10,
            pin => SamplePpuAddress(cartridge, pin, ppuAddress),
            out var drive));
        Assert.Equal(expected, drive.Level);
    }

    [Fact]
    public void Nes20_submappers_select_board_local_bus_conflict_behavior()
    {
        var noConflict = CreateCartridge(prgBanks: 4, submapper: 1, firstBankByte: 0x01);
        var conflict = CreateCartridge(prgBanks: 4, submapper: 2, firstBankByte: 0x01);
        var noConflictCpu = CpuTarget(noConflict);
        var conflictCpu = CpuTarget(conflict);

        noConflictCpu.Write!(0x8000, 0x03);
        conflictCpu.Write!(0x8000, 0x03);

        Assert.False(noConflict.BusConflictsEnabled);
        Assert.Equal((byte)0x03, noConflict.BankRegister);
        Assert.Equal(3, noConflict.SelectedPrgBank);
        Assert.Equal((byte)0x03, noConflict.LastEffectiveMapperWriteData);

        Assert.True(conflict.BusConflictsEnabled);
        Assert.Equal((byte)0x01, conflict.BankRegister);
        Assert.Equal(1, conflict.SelectedPrgBank);
        Assert.Equal((byte)0x01, conflict.LastEffectiveMapperWriteData);
        Assert.Equal(1UL, conflict.BusConflictModifiedWriteCount);
    }

    [Fact]
    public void Mapper_bank_latch_samples_the_physical_cpu_data_bus_on_falling_M2_edge()
    {
        var cartridge = CreateCartridge(prgBanks: 4, submapper: 1);
        var board = new VirtualHardwareBoard("UXROM.PHYSICAL.LATCH.TEST");
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
        DriveAddress(address, 0x0000); // /ROMSEL supplies the missing logical A15=$8000 window.
        DriveByte(data, 0x02);
        romsel.Set(DigitalLevel.Low);
        rw.Set(DigitalLevel.Low);
        m2.Set(DigitalLevel.High);

        Assert.Equal(0, cartridge.SelectedPrgBank);
        Assert.Equal(0UL, cartridge.MapperWriteCount);

        m2.Set(DigitalLevel.Low);

        Assert.Equal(2, cartridge.SelectedPrgBank);
        Assert.Equal((byte)0x02, cartridge.BankRegister);
        Assert.Equal(1UL, cartridge.MapperWriteCount);
        Assert.Equal((ushort)0x8000, cartridge.LastMapperWriteAddress);
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
    public void Generic_compiled_and_raw_physical_uxrom_execute_the_same_bank_switch_program()
    {
        var image = CreateExecutionImage();
        var compiled = new RegionalNesVirtualMachine();
        var reference = new RegionalNesVirtualMachine();

        compiled.SetCompiledLabExecutionEnabled(true);
        compiled.InsertRom(image, "UxROM execution (Japan).nes");
        reference.InsertRom(image, "UxROM execution (Japan).nes");
        reference.Famicom.SetCompiledPhysicalMachineEnabled(false);

        compiled.PowerOn();
        reference.PowerOn();
        compiled.ReleaseReset();
        reference.ReleaseReset();

        const int masterCycles = 12_000;
        compiled.AdvanceMasterCycles(masterCycles);
        reference.AdvanceMasterCycles(masterCycles);

        var compiledCartridge = Assert.IsType<UxromCartridge>(compiled.Slot.Cartridge);
        var referenceCartridge = Assert.IsType<UxromCartridge>(reference.Slot.Cartridge);
        Assert.True(compiled.Famicom.CompiledLabMotherboardEnabled);
        Assert.Equal((byte)0x01, compiledCartridge.BankRegister);
        Assert.Equal((byte)0x01, referenceCartridge.BankRegister);
        Assert.Equal(referenceCartridge.MapperWriteCount, compiledCartridge.MapperWriteCount);
        Assert.Equal(reference.Famicom.Cpu.ProgramCounter, compiled.Famicom.Cpu.ProgramCounter);
        Assert.Equal(reference.Famicom.Cpu.CompletedInstructionCount, compiled.Famicom.Cpu.CompletedInstructionCount);
        Assert.Equal(reference.Famicom.Ppu.InspectDiagnosticState(), compiled.Famicom.Ppu.InspectDiagnosticState());
        Assert.Equal(reference.Famicom.Ciram.InspectStateHash(), compiled.Famicom.Ciram.InspectStateHash());
    }

    [Fact]
    public void Standard_uxrom_rejects_chr_rom_or_explicit_prg_ram_hardware()
    {
        var cartridge = new UxromCartridge("TEST.UXROM.INVALID");
        var prg = new byte[4 * 16 * 1024];
        var withChrRom = CreateImage(prg, new byte[8 * 1024], submapper: 1);
        var withPrgRam = CreateImage(prg, [], submapper: 1) with
        {
            PrgRamSizeBytes = 8 * 1024,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 8 * 1024,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        };

        Assert.Throws<NotSupportedException>(() => cartridge.LoadImage(withChrRom));
        Assert.Throws<NotSupportedException>(() => cartridge.LoadImage(withPrgRam));
    }

    private static UxromCartridge CreateCartridge(
        int prgBanks,
        int submapper = 1,
        VirtualHardwareNesMirroring mirroring = VirtualHardwareNesMirroring.Horizontal,
        byte? firstBankByte = null)
    {
        var prg = new byte[prgBanks * 16 * 1024];
        for (var bank = 0; bank < prgBanks; bank++)
            Array.Fill(prg, (byte)(0x40 + bank), bank * 16 * 1024, 16 * 1024);
        if (firstBankByte.HasValue) prg[0] = firstBankByte.Value;

        var image = CreateImage(prg, [], submapper, mirroring) with
        {
            PrgRamSizeBytes = 0,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 8 * 1024,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        };
        var cartridge = new UxromCartridge("TEST.UXROM");
        cartridge.LoadImage(image);
        return cartridge;
    }

    private static VirtualHardwareNesRomImage CreateExecutionImage()
    {
        var prg = new byte[4 * 16 * 1024];
        Array.Fill(prg, (byte)0xEA);

        // Bank 1 becomes an endless JMP $8000 target after the fixed bank selects it.
        prg[1 * 16 * 1024 + 0] = 0x4C;
        prg[1 * 16 * 1024 + 1] = 0x00;
        prg[1 * 16 * 1024 + 2] = 0x80;

        var fixedBase = 3 * 16 * 1024;
        var program = new byte[]
        {
            0x78,                   // SEI
            0xA9, 0x01,             // LDA #$01
            0x8D, 0x00, 0x80,       // STA $8000 - select bank 1
            0x4C, 0x00, 0x80        // JMP $8000
        };
        program.CopyTo(prg, fixedBase);
        prg[fixedBase + 0x3FFA] = 0x00; prg[fixedBase + 0x3FFB] = 0xC0;
        prg[fixedBase + 0x3FFC] = 0x00; prg[fixedBase + 0x3FFD] = 0xC0;
        prg[fixedBase + 0x3FFE] = 0x00; prg[fixedBase + 0x3FFF] = 0xC0;

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
        int submapper,
        VirtualHardwareNesMirroring mirroring = VirtualHardwareNesMirroring.Horizontal) =>
        new(
            VirtualHardwareNesHeaderFormat.Nes20,
            MapperNumber: 2,
            SubmapperNumber: submapper,
            PrgRomSizeBytes: prg.Length,
            ChrRomSizeBytes: chr.Length,
            HasTrainer: false,
            HasBatteryBackedMemory: false,
            mirroring,
            VirtualHardwareNesHeaderTiming.Ntsc,
            prg,
            chr);

    private static CompiledBusTargetDescriptor CpuTarget(UxromCartridge cartridge) =>
        ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets()
            .Single(target => target.AddressPins.Count == 15);

    private static CompiledBusTargetDescriptor PpuTarget(UxromCartridge cartridge) =>
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

    private static DigitalLevel SamplePpuAddress(UxromCartridge cartridge, DigitalPin pin, ushort address)
    {
        for (var bit = 0; bit < cartridge.PpuAddress.Width; bit++)
        {
            if (!ReferenceEquals(pin, cartridge.PpuAddress.Pins[bit])) continue;
            return (address & (1 << bit)) != 0 ? DigitalLevel.High : DigitalLevel.Low;
        }
        return DigitalLevel.Unknown;
    }
}
