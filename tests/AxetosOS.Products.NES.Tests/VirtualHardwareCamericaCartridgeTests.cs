using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Machines.Nes;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareCamericaCartridgeTests
{
    [Fact]
    public void Power_on_selects_first_switchable_prg_bank_and_fixes_last_bank_high()
    {
        var cartridge = CreateCartridge(prgBanks: 8, submapper: 0);
        var cpu = CpuTarget(cartridge);

        Assert.Equal((byte)0x40, cpu.Read!(0x8000));
        Assert.Equal((byte)0x47, cpu.Read!(0xC000));
        Assert.Equal(0, cartridge.SelectedPrgBank);
        Assert.Equal(7, cartridge.FixedPrgBank);
    }

    [Fact]
    public void Prg_bank_latch_decodes_c000_ffff_and_switches_only_lower_sixteen_kib_window()
    {
        var cartridge = CreateCartridge(prgBanks: 16, submapper: 0);
        var cpu = CpuTarget(cartridge);

        cpu.Write!(0xC000, 0x0A);

        Assert.Equal((byte)0x4A, cpu.Read!(0x8000));
        Assert.Equal((byte)0x4F, cpu.Read!(0xC000));
        Assert.Equal((byte)0x0A, cartridge.BankRegister);
        Assert.Equal(10, cartridge.SelectedPrgBank);
        Assert.Equal(15, cartridge.FixedPrgBank);
        Assert.Equal(1UL, cartridge.PrgBankWriteCount);
    }

    [Fact]
    public void Standard_camerica_board_has_no_cpu_rom_bus_conflict()
    {
        var cartridge = CreateCartridge(prgBanks: 8, submapper: 0, writeSiteByte: 0x01);
        var cpu = CpuTarget(cartridge);

        cpu.Write!(0xC000, 0x05);

        Assert.Equal((byte)0x05, cartridge.BankRegister);
        Assert.Equal(5, cartridge.SelectedPrgBank);
        Assert.Equal((byte)0x45, cpu.Read!(0x8000));
    }

    [Fact]
    public void Standard_board_ignores_8000_bfff_writes_because_only_fire_hawk_populates_that_register()
    {
        var cartridge = CreateCartridge(prgBanks: 8, submapper: 0);
        var cpu = CpuTarget(cartridge);

        cpu.Write!(0x8000, 0x07);
        cpu.Write!(0x9FFF, 0x06);
        cpu.Write!(0xA000, 0x05);
        cpu.Write!(0xBFFF, 0x04);

        Assert.Equal((byte)0x00, cartridge.BankRegister);
        Assert.Equal(0, cartridge.SelectedPrgBank);
        Assert.Equal(0UL, cartridge.MapperWriteCount);
        Assert.Equal(0UL, cartridge.MirroringWriteCount);
    }

    [Fact]
    public void Chr_ram_round_trips_through_the_cartridge_compiled_ppu_facet()
    {
        var cartridge = CreateCartridge(prgBanks: 8, submapper: 0);
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
    public void Standard_board_fixed_mirroring_is_static_ciram_wiring(
        VirtualHardwareNesMirroring mirroring,
        ushort ppuAddress,
        DigitalLevel expected)
    {
        var cartridge = CreateCartridge(prgBanks: 8, submapper: 0, mirroring: mirroring);
        var staticFacet = Assert.IsAssignableFrom<ICompiledStaticCombinationalComponent>(cartridge);

        Assert.True(staticFacet.TryEvaluateCompiledStaticOutput(
            cartridge.CiramA10,
            pin => SamplePpuAddress(cartridge, pin, ppuAddress),
            out var drive));
        Assert.Equal(expected, drive.Level);
    }

    [Fact]
    public void Fire_hawk_submapper_routes_live_mirroring_latch_to_ciram_a10_and_prevents_static_folding()
    {
        var cartridge = CreateCartridge(prgBanks: 8, submapper: 1);
        var cpu = CpuTarget(cartridge);
        var staticFacet = Assert.IsAssignableFrom<ICompiledStaticCombinationalComponent>(cartridge);
        var liveFacet = Assert.IsAssignableFrom<ICompiledCombinationalComponent>(cartridge);

        Assert.False(staticFacet.TryEvaluateCompiledStaticOutput(
            cartridge.CiramA10,
            _ => DigitalLevel.Low,
            out _));

        cpu.Write!(0x9000, 0x10);
        Assert.Equal(1, cartridge.SelectedNametablePage);
        Assert.Equal(1UL, cartridge.MirroringWriteCount);
        Assert.True(liveFacet.TryEvaluateCompiledOutput(cartridge.CiramA10, _ => DigitalLevel.Low, out var high));
        Assert.Equal(DigitalLevel.High, high.Level);

        cpu.Write!(0x9000, 0x00);
        Assert.Equal(0, cartridge.SelectedNametablePage);
        Assert.True(liveFacet.TryEvaluateCompiledOutput(cartridge.CiramA10, _ => DigitalLevel.High, out var low));
        Assert.Equal(DigitalLevel.Low, low.Level);
    }

    [Fact]
    public void Fire_hawk_bf9097_exposes_only_three_prg_bank_bits()
    {
        var cartridge = CreateCartridge(prgBanks: 8, submapper: 1);
        var cpu = CpuTarget(cartridge);

        cpu.Write!(0xC000, 0xFF);

        Assert.Equal((byte)0x07, cartridge.BankRegister);
        Assert.Equal(7, cartridge.SelectedPrgBank);
        Assert.Equal((byte)0x47, cpu.Read!(0x8000));
    }

    [Fact]
    public void E000_ffff_writes_also_clock_cic_stun_latch_from_cpu_a0()
    {
        var cartridge = CreateCartridge(prgBanks: 8, submapper: 0);
        var cpu = CpuTarget(cartridge);

        cpu.Write!(0xE001, 0x03);
        Assert.True(cartridge.CicStunLatch);
        Assert.Equal((byte)0x03, cartridge.BankRegister);

        cpu.Write!(0xE000, 0x04);
        Assert.False(cartridge.CicStunLatch);
        Assert.Equal((byte)0x04, cartridge.BankRegister);
        Assert.Equal(2UL, cartridge.CicStunWriteCount);
        Assert.Equal(2UL, cartridge.PrgBankWriteCount);
    }

    [Fact]
    public void Prg_bank_latch_samples_physical_cpu_data_bus_on_falling_m2_edge()
    {
        var cartridge = CreateCartridge(prgBanks: 8, submapper: 0);
        var board = new VirtualHardwareBoard("CAMERICA.PHYSICAL.LATCH.TEST");
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
        DriveAddress(address, 0x4000); // /ROMSEL supplies A15, yielding logical $C000.
        DriveByte(data, 0x05);
        romsel.Set(DigitalLevel.Low);
        rw.Set(DigitalLevel.Low);
        m2.Set(DigitalLevel.High);

        Assert.Equal(0, cartridge.SelectedPrgBank);
        Assert.Equal(0UL, cartridge.MapperWriteCount);

        m2.Set(DigitalLevel.Low);

        Assert.Equal(5, cartridge.SelectedPrgBank);
        Assert.Equal((byte)0x05, cartridge.BankRegister);
        Assert.Equal(1UL, cartridge.MapperWriteCount);
        Assert.Equal((ushort)0xC000, cartridge.LastMapperWriteAddress);
    }

    [Fact]
    public void Compiled_mapper_write_is_latched_at_bus_cycle_completion()
    {
        var cartridge = CreateCartridge(prgBanks: 8, submapper: 0);
        var cpu = CpuTarget(cartridge);

        Assert.Equal(CompiledBusWritePhase.Complete, cpu.WritePhase);
        Assert.Contains(cpu.WriteConditions, condition => ReferenceEquals(condition.Pin, cartridge.CpuReadWrite)
            && condition.RequiredLevel == DigitalLevel.Low);
        Assert.Contains(cpu.WriteConditions, condition => ReferenceEquals(condition.Pin, cartridge.CpuRomSelectBar)
            && condition.RequiredLevel == DigitalLevel.Low);
    }

    [Fact]
    public void Generic_compiled_and_raw_physical_camerica_execute_the_same_bank_and_mirroring_program()
    {
        var image = CreateExecutionImage();
        var compiled = new RegionalNesVirtualMachine();
        var reference = new RegionalNesVirtualMachine();

        compiled.SetCompiledLabExecutionEnabled(true);
        compiled.InsertRom(image, "Camerica execution (Japan).nes");
        reference.InsertRom(image, "Camerica execution (Japan).nes");
        reference.Famicom.SetCompiledPhysicalMachineEnabled(false);

        compiled.PowerOn();
        reference.PowerOn();
        compiled.ReleaseReset();
        reference.ReleaseReset();

        const int masterCycles = 12_000;
        compiled.AdvanceMasterCycles(masterCycles);
        reference.AdvanceMasterCycles(masterCycles);

        var compiledCartridge = Assert.IsType<CamericaCartridge>(compiled.Slot.Cartridge);
        var referenceCartridge = Assert.IsType<CamericaCartridge>(reference.Slot.Cartridge);
        Assert.True(compiled.Famicom.CompiledLabMotherboardEnabled);
        Assert.Equal((byte)0x01, compiledCartridge.BankRegister);
        Assert.Equal((byte)0x01, referenceCartridge.BankRegister);
        Assert.Equal(1, compiledCartridge.SelectedPrgBank);
        Assert.Equal(1, compiledCartridge.SelectedNametablePage);
        Assert.Equal(referenceCartridge.MapperWriteCount, compiledCartridge.MapperWriteCount);
        Assert.Equal(referenceCartridge.MirroringWriteCount, compiledCartridge.MirroringWriteCount);
        Assert.Equal(reference.Famicom.Cpu.ProgramCounter, compiled.Famicom.Cpu.ProgramCounter);
        Assert.Equal(reference.Famicom.Cpu.CompletedInstructionCount, compiled.Famicom.Cpu.CompletedInstructionCount);
        Assert.Equal(reference.Famicom.Ppu.InspectDiagnosticState(), compiled.Famicom.Ppu.InspectDiagnosticState());
        Assert.Equal(reference.Famicom.Ciram.InspectStateHash(), compiled.Famicom.Ciram.InspectStateHash());
    }

    [Fact]
    public void Camerica_rejects_invalid_memory_geometry_ram_four_screen_and_fire_hawk_256k_prg()
    {
        var cartridge = new CamericaCartridge("TEST.CAMERICA.INVALID");
        var valid128 = CreatePrg(8);
        var bad64 = CreateImage(CreatePrg(4), [], submapper: 0);
        var withChrRom = CreateImage(valid128, new byte[8 * 1024], submapper: 0);
        var withPrgRam = CreateImage(valid128, [], submapper: 0) with
        {
            PrgRamSizeBytes = 8 * 1024,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 8 * 1024,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        };
        var fourScreen = CreateImage(valid128, [], submapper: 0,
            mirroring: VirtualHardwareNesMirroring.FourScreen);
        var fireHawk256 = CreateImage(CreatePrg(16), [], submapper: 1);

        Assert.Throws<NotSupportedException>(() => cartridge.LoadImage(bad64));
        Assert.Throws<NotSupportedException>(() => cartridge.LoadImage(withChrRom));
        Assert.Throws<NotSupportedException>(() => cartridge.LoadImage(withPrgRam));
        Assert.Throws<NotSupportedException>(() => cartridge.LoadImage(fourScreen));
        Assert.Throws<NotSupportedException>(() => cartridge.LoadImage(fireHawk256));
    }

    [Fact]
    public void Undefined_mapper_seventy_one_submapper_is_rejected_instead_of_guessing_board_wiring()
    {
        var cartridge = new CamericaCartridge("TEST.CAMERICA.SUBMAPPER");
        var image = CreateImage(CreatePrg(8), [], submapper: 2);

        Assert.Throws<NotSupportedException>(() => cartridge.LoadImage(image));
    }

    [Fact]
    public void Factory_constructs_mapper_seventy_one_as_replaceable_camerica_hardware()
    {
        var image = CreateImage(CreatePrg(8), [], submapper: 0) with
        {
            PrgRamSizeBytes = 0,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 8 * 1024,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        };

        var cartridge = VirtualCartridgeHardwareFactory.Create(image);

        Assert.Equal(71, cartridge.MapperNumber);
        Assert.IsType<CamericaCartridge>(cartridge);
        Assert.True(cartridge.IsInserted);
    }

    private static CamericaCartridge CreateCartridge(
        int prgBanks,
        int? submapper,
        VirtualHardwareNesMirroring mirroring = VirtualHardwareNesMirroring.Horizontal,
        byte? writeSiteByte = null)
    {
        var prg = CreatePrg(prgBanks);
        if (writeSiteByte.HasValue)
        {
            // The write-under-ROM conflict probe belongs at logical $C000,
            // which is the first byte of the fixed-last 16 KiB bank. Keep the
            // switchable banks' marker bytes intact so a successful no-conflict
            // bank selection can still be observed independently at $8000.
            prg[(prgBanks - 1) * 16 * 1024] = writeSiteByte.Value;
        }

        var image = CreateImage(prg, [], submapper, mirroring) with
        {
            PrgRamSizeBytes = 0,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 8 * 1024,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        };
        var cartridge = new CamericaCartridge("TEST.CAMERICA");
        cartridge.LoadImage(image);
        return cartridge;
    }

    private static byte[] CreatePrg(int prgBanks)
    {
        var prg = new byte[prgBanks * 16 * 1024];
        for (var bank = 0; bank < prgBanks; bank++)
            Array.Fill(prg, (byte)(0x40 + bank), bank * 16 * 1024, 16 * 1024);
        return prg;
    }

    private static VirtualHardwareNesRomImage CreateExecutionImage()
    {
        var prg = new byte[8 * 16 * 1024];
        Array.Fill(prg, (byte)0xEA);

        // Bank 1 becomes the post-switch execution target.
        prg[1 * 16 * 1024 + 0] = 0x4C;
        prg[1 * 16 * 1024 + 1] = 0x00;
        prg[1 * 16 * 1024 + 2] = 0x80;

        var fixedBase = 7 * 16 * 1024;
        var program = new byte[]
        {
            0x78,                   // SEI
            0xA9, 0x10,             // LDA #$10 - Fire Hawk single-screen page 1
            0x8D, 0x00, 0x90,       // STA $9000
            0xA9, 0x01,             // LDA #$01 - PRG bank 1
            0x8D, 0x00, 0xC0,       // STA $C000
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
        int? submapper,
        VirtualHardwareNesMirroring mirroring = VirtualHardwareNesMirroring.Horizontal,
        VirtualHardwareNesHeaderFormat headerFormat = VirtualHardwareNesHeaderFormat.Nes20) =>
        new(
            headerFormat,
            MapperNumber: 71,
            SubmapperNumber: submapper,
            PrgRomSizeBytes: prg.Length,
            ChrRomSizeBytes: chr.Length,
            HasTrainer: false,
            HasBatteryBackedMemory: false,
            mirroring,
            VirtualHardwareNesHeaderTiming.Ntsc,
            prg,
            chr);

    private static CompiledBusTargetDescriptor CpuTarget(CamericaCartridge cartridge) =>
        ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets()
            .Single(target => target.AddressPins.Count == 15);

    private static CompiledBusTargetDescriptor PpuTarget(CamericaCartridge cartridge) =>
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

    private static DigitalLevel SamplePpuAddress(CamericaCartridge cartridge, DigitalPin pin, ushort address)
    {
        for (var bit = 0; bit < cartridge.PpuAddress.Width; bit++)
        {
            if (!ReferenceEquals(pin, cartridge.PpuAddress.Pins[bit])) continue;
            return (address & (1 << bit)) != 0 ? DigitalLevel.High : DigitalLevel.Low;
        }
        return DigitalLevel.Unknown;
    }
}
