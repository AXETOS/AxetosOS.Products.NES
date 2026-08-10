using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Machines.Nes;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareCnromCartridgeTests
{
    [Fact]
    public void Power_on_exposes_fixed_prg_and_first_chr_bank()
    {
        var cartridge = CreateCartridge(chrBanks: 4);
        var cpu = CpuTarget(cartridge);
        var ppu = PpuTarget(cartridge);

        Assert.Equal((byte)0xA5, cpu.Read!(0x8000));
        Assert.Equal((byte)0x5A, cpu.Read!(0xFFFF));
        Assert.Equal((byte)0x60, ppu.Read!(0x0000));
        Assert.Equal(0, cartridge.SelectedChrBank);
        Assert.Equal(4, cartridge.ChrBankCount);
    }

    [Fact]
    public void Bank_latch_switches_only_the_eight_kib_chr_window()
    {
        var cartridge = CreateCartridge(chrBanks: 4, submapper: 1);
        var cpu = CpuTarget(cartridge);
        var ppu = PpuTarget(cartridge);

        var beforePrg = cpu.Read!(0x8123);
        cpu.Write!(0x8000, 0x02);

        Assert.Equal((byte)0x62, ppu.Read!(0x0000));
        Assert.Equal((byte)0x62, ppu.Read!(0x1FFF));
        Assert.Equal(beforePrg, cpu.Read!(0x8123));
        Assert.Equal((byte)0x02, cartridge.BankRegister);
        Assert.Equal(2, cartridge.SelectedChrBank);
        Assert.Equal(1UL, cartridge.MapperWriteCount);
    }

    [Fact]
    public void Bank_latch_exposes_only_address_lines_physically_populated_by_the_fitted_chr_rom()
    {
        var cartridge = CreateCartridge(chrBanks: 2, submapper: 1);
        var cpu = CpuTarget(cartridge);
        var ppu = PpuTarget(cartridge);

        cpu.Write!(0x8000, 0xFF);

        Assert.Equal((byte)0x01, cartridge.BankRegister);
        Assert.Equal(1, cartridge.SelectedChrBank);
        Assert.Equal((byte)0x61, ppu.Read!(0x0100));
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
        var cartridge = CreateCartridge(chrBanks: 4, mirroring: mirroring);
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
        var noConflict = CreateCartridge(chrBanks: 4, submapper: 1, prgAt8000: 0x01);
        var conflict = CreateCartridge(chrBanks: 4, submapper: 2, prgAt8000: 0x01);
        var noConflictCpu = CpuTarget(noConflict);
        var conflictCpu = CpuTarget(conflict);

        noConflictCpu.Write!(0x8000, 0x03);
        conflictCpu.Write!(0x8000, 0x03);

        Assert.False(noConflict.BusConflictsEnabled);
        Assert.Equal((byte)0x03, noConflict.BankRegister);
        Assert.Equal(3, noConflict.SelectedChrBank);
        Assert.Equal((byte)0x03, noConflict.LastEffectiveMapperWriteData);

        Assert.True(conflict.BusConflictsEnabled);
        Assert.Equal((byte)0x01, conflict.BankRegister);
        Assert.Equal(1, conflict.SelectedChrBank);
        Assert.Equal((byte)0x01, conflict.LastEffectiveMapperWriteData);
        Assert.Equal(1UL, conflict.BusConflictModifiedWriteCount);
    }

    [Fact]
    public void Mapper_bank_latch_samples_the_physical_cpu_data_bus_on_falling_M2_edge()
    {
        var cartridge = CreateCartridge(chrBanks: 4, submapper: 1);
        var board = new VirtualHardwareBoard("CNROM.PHYSICAL.LATCH.TEST");
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
        DriveByte(data, 0x02);
        romsel.Set(DigitalLevel.Low);
        rw.Set(DigitalLevel.Low);
        m2.Set(DigitalLevel.High);

        Assert.Equal(0, cartridge.SelectedChrBank);
        Assert.Equal(0UL, cartridge.MapperWriteCount);

        m2.Set(DigitalLevel.Low);

        Assert.Equal(2, cartridge.SelectedChrBank);
        Assert.Equal((byte)0x02, cartridge.BankRegister);
        Assert.Equal(1UL, cartridge.MapperWriteCount);
        Assert.Equal((ushort)0x8000, cartridge.LastMapperWriteAddress);
    }

    [Fact]
    public void Compiled_mapper_write_is_latched_at_bus_cycle_completion()
    {
        var cartridge = CreateCartridge(chrBanks: 4);
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
        var cartridge = CreateCartridge(chrBanks: 4);
        var ppu = PpuTarget(cartridge);
        var before = ppu.Read!(0x0012);

        Assert.Null(ppu.Write);
        Assert.Empty(ppu.WriteConditions);
        Assert.Equal(before, ppu.Read!(0x0012));
        Assert.Equal(0UL, cartridge.PpuWriteCount);
    }

    [Fact]
    public void Generic_compiled_and_raw_physical_cnrom_execute_the_same_chr_bank_switch_program()
    {
        var image = CreateExecutionImage();
        var compiled = new RegionalNesVirtualMachine();
        var reference = new RegionalNesVirtualMachine();

        compiled.SetCompiledLabExecutionEnabled(true);
        compiled.InsertRom(image, "CNROM execution (Japan).nes");
        reference.InsertRom(image, "CNROM execution (Japan).nes");
        reference.Famicom.SetCompiledPhysicalMachineEnabled(false);

        compiled.PowerOn();
        reference.PowerOn();
        compiled.ReleaseReset();
        reference.ReleaseReset();

        const int masterCycles = 12_000;
        compiled.AdvanceMasterCycles(masterCycles);
        reference.AdvanceMasterCycles(masterCycles);

        var compiledCartridge = Assert.IsType<CnromCartridge>(compiled.Slot.Cartridge);
        var referenceCartridge = Assert.IsType<CnromCartridge>(reference.Slot.Cartridge);
        Assert.True(compiled.Famicom.CompiledLabMotherboardEnabled);
        Assert.Equal((byte)0x02, compiledCartridge.BankRegister);
        Assert.Equal((byte)0x02, referenceCartridge.BankRegister);
        Assert.Equal(referenceCartridge.MapperWriteCount, compiledCartridge.MapperWriteCount);
        Assert.Equal(reference.Famicom.Cpu.ProgramCounter, compiled.Famicom.Cpu.ProgramCounter);
        Assert.Equal(reference.Famicom.Cpu.CompletedInstructionCount, compiled.Famicom.Cpu.CompletedInstructionCount);
        Assert.Equal(reference.Famicom.Ppu.InspectDiagnosticState(), compiled.Famicom.Ppu.InspectDiagnosticState());
        Assert.Equal(reference.Famicom.Ciram.InspectStateHash(), compiled.Famicom.Ciram.InspectStateHash());
    }

    [Fact]
    public void Standard_cnrom_rejects_hardware_that_requires_a_different_board()
    {
        var cartridge = new CnromCartridge("TEST.CNROM.INVALID");
        var badPrg = CreateImage(new byte[16 * 1024], new byte[4 * 8 * 1024], submapper: 1);
        var noChrRom = CreateImage(new byte[32 * 1024], [], submapper: 1);
        var withRam = CreateImage(new byte[32 * 1024], new byte[4 * 8 * 1024], submapper: 1) with
        {
            PrgRamSizeBytes = 8 * 1024,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 0,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        };

        Assert.Throws<ArgumentException>(() => cartridge.LoadImage(badPrg));
        Assert.Throws<ArgumentException>(() => cartridge.LoadImage(noChrRom));
        Assert.Throws<NotSupportedException>(() => cartridge.LoadImage(withRam));
    }

    private static CnromCartridge CreateCartridge(
        int chrBanks,
        int submapper = 1,
        VirtualHardwareNesMirroring mirroring = VirtualHardwareNesMirroring.Horizontal,
        byte prgAt8000 = 0xA5)
    {
        var prg = new byte[32 * 1024];
        Array.Fill(prg, (byte)0xEA);
        prg[0] = prgAt8000;
        prg[^1] = 0x5A;

        var chr = new byte[chrBanks * 8 * 1024];
        for (var bank = 0; bank < chrBanks; bank++)
            Array.Fill(chr, (byte)(0x60 + bank), bank * 8 * 1024, 8 * 1024);

        var image = CreateImage(prg, chr, submapper, mirroring) with
        {
            PrgRamSizeBytes = 0,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 0,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        };
        var cartridge = new CnromCartridge("TEST.CNROM");
        cartridge.LoadImage(image);
        return cartridge;
    }

    private static VirtualHardwareNesRomImage CreateExecutionImage()
    {
        var prg = new byte[32 * 1024];
        Array.Fill(prg, (byte)0xEA);
        var program = new byte[]
        {
            0x78,                   // SEI
            0xA9, 0x02,             // LDA #$02
            0x8D, 0x00, 0x80,       // STA $8000 - select CHR bank 2
            0x4C, 0x06, 0x80        // JMP $8006
        };
        program.CopyTo(prg, 0);
        prg[0x7FFA] = 0x00; prg[0x7FFB] = 0x80;
        prg[0x7FFC] = 0x00; prg[0x7FFD] = 0x80;
        prg[0x7FFE] = 0x00; prg[0x7FFF] = 0x80;

        var chr = new byte[4 * 8 * 1024];
        for (var bank = 0; bank < 4; bank++)
            Array.Fill(chr, (byte)(0x60 + bank), bank * 8 * 1024, 8 * 1024);

        return CreateImage(prg, chr, submapper: 1) with
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
        int submapper,
        VirtualHardwareNesMirroring mirroring = VirtualHardwareNesMirroring.Horizontal) =>
        new(
            VirtualHardwareNesHeaderFormat.Nes20,
            MapperNumber: 3,
            SubmapperNumber: submapper,
            PrgRomSizeBytes: prg.Length,
            ChrRomSizeBytes: chr.Length,
            HasTrainer: false,
            HasBatteryBackedMemory: false,
            mirroring,
            VirtualHardwareNesHeaderTiming.Ntsc,
            prg,
            chr);

    private static CompiledBusTargetDescriptor CpuTarget(CnromCartridge cartridge) =>
        ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets()
            .Single(target => target.AddressPins.Count == 15);

    private static CompiledBusTargetDescriptor PpuTarget(CnromCartridge cartridge) =>
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

    private static DigitalLevel SamplePpuAddress(CnromCartridge cartridge, DigitalPin pin, ushort address)
    {
        for (var bit = 0; bit < cartridge.PpuAddress.Width; bit++)
        {
            if (!ReferenceEquals(pin, cartridge.PpuAddress.Pins[bit])) continue;
            return (address & (1 << bit)) != 0 ? DigitalLevel.High : DigitalLevel.Low;
        }
        return DigitalLevel.Unknown;
    }
}
