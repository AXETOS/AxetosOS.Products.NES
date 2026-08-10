using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Machines.Nes;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareMapper227CartridgeTests
{
    [Fact]
    public void Power_on_clears_address_latch_and_maps_bank_zero_into_both_cpu_halves()
    {
        var cartridge = CreateCartridge(prgBanks: 32);
        var cpu = CpuTarget(cartridge);

        Assert.Equal((ushort)0x0000, cartridge.AddressLatch);
        Assert.Equal(0, cartridge.LowerPrgBank);
        Assert.Equal(0, cartridge.UpperPrgBank);
        Assert.Equal("UNROM-fixed-0", cartridge.PrgMode);
        Assert.Equal(VirtualHardwareNesMirroring.Vertical, cartridge.Mirroring);
        Assert.False(cartridge.ChrRamWriteProtected);
        Assert.Equal((byte)0x40, cpu.Read!(0x8000));
        Assert.Equal((byte)0x40, cpu.Read!(0xC000));
    }

    [Fact]
    public void Nrom128_mode_mirrors_one_selected_sixteen_kib_bank()
    {
        var cartridge = CreateCartridge(prgBanks: 32);
        var cpu = CpuTarget(cartridge);

        cpu.Write!(0x8094, 0xA5); // O=1, S=0, PRG seed=5

        Assert.Equal("NROM-128", cartridge.PrgMode);
        Assert.Equal(5, cartridge.LowerPrgBank);
        Assert.Equal(5, cartridge.UpperPrgBank);
        Assert.Equal((byte)0x45, cpu.Read!(0x8000));
        Assert.Equal((byte)0x45, cpu.Read!(0xC000));
        Assert.True(cartridge.ChrRamWriteProtected);
    }

    [Fact]
    public void Nrom256_mode_maps_the_selected_even_odd_thirty_two_kib_pair()
    {
        var cartridge = CreateCartridge(prgBanks: 32);
        var cpu = CpuTarget(cartridge);

        cpu.Write!(0x80AF, 0x5A); // O=1, S=1, M=1, seed=11 -> banks 10/11

        Assert.Equal((ushort)0x00AF, cartridge.AddressLatch);
        Assert.Equal("NROM-256", cartridge.PrgMode);
        Assert.Equal(10, cartridge.LowerPrgBank);
        Assert.Equal(11, cartridge.UpperPrgBank);
        Assert.Equal(VirtualHardwareNesMirroring.Horizontal, cartridge.Mirroring);
        Assert.Equal((byte)0x4A, cpu.Read!(0x8000));
        Assert.Equal((byte)0x4B, cpu.Read!(0xC000));
    }

    [Fact]
    public void Unrom_mode_selects_lower_inner_bank_and_fixes_inner_bank_seven_above_c000()
    {
        var cartridge = CreateCartridge(prgBanks: 32);
        var cpu = CpuTarget(cartridge);

        cpu.Write!(0x8214, 0xFF); // L=1, O=0, S=0, seed=5

        Assert.Equal("UNROM-fixed-7", cartridge.PrgMode);
        Assert.Equal(5, cartridge.LowerPrgBank);
        Assert.Equal(7, cartridge.UpperPrgBank);
        Assert.Equal((byte)0x45, cpu.Read!(0x8000));
        Assert.Equal((byte)0x47, cpu.Read!(0xC000));
        Assert.False(cartridge.ChrRamWriteProtected);
    }

    [Fact]
    public void Inverse_unrom_mode_fixes_inner_bank_zero_in_the_selected_outer_group()
    {
        var cartridge = CreateCartridge(prgBanks: 64);
        var cpu = CpuTarget(cartridge);

        cpu.Write!(0x8160, 0x00); // seed=$38, L=0, O=0, S=0

        Assert.Equal(56, cartridge.LowerPrgBank);
        Assert.Equal(56, cartridge.UpperPrgBank);
        Assert.Equal((byte)0x78, cpu.Read!(0x8000));
        Assert.Equal((byte)0x78, cpu.Read!(0xC000));
    }

    [Fact]
    public void S_flag_forces_even_switchable_inner_bank_in_unrom_like_mode()
    {
        var cartridge = CreateCartridge(prgBanks: 32);
        var cpu = CpuTarget(cartridge);

        cpu.Write!(0x8215, 0x00); // seed=5, S=1, L=1, O=0

        Assert.Equal(4, cartridge.LowerPrgBank);
        Assert.Equal(7, cartridge.UpperPrgBank);
    }

    [Fact]
    public void Five_hundred_twelve_kib_rom_masks_the_unconnected_a19_bank_output()
    {
        var cartridge = CreateCartridge(prgBanks: 32);
        var cpu = CpuTarget(cartridge);

        cpu.Write!(0x8314, 0x00); // seed=$25; A19 request is physically unconnected

        Assert.Equal(5, cartridge.LowerPrgBank);
        Assert.Equal(7, cartridge.UpperPrgBank);
        Assert.Equal((byte)0x45, cpu.Read!(0x8000));
        Assert.Equal((byte)0x47, cpu.Read!(0xC000));
    }

    [Fact]
    public void Legacy_ines_five_hundred_twelve_kib_multicart_geometry_is_accepted()
    {
        var image = CreateImage(CreatePrg(32), submapper: null, headerFormat: VirtualHardwareNesHeaderFormat.INes);
        var cartridge = new Mapper227Cartridge("TEST.M227.LEGACY");

        cartridge.LoadImage(image);

        Assert.True(cartridge.IsInserted);
        Assert.Equal(512 * 1024, cartridge.PrgRomSizeBytes);
        Assert.Equal(32, cartridge.PrgBankCount);
        Assert.Equal(8 * 1024, cartridge.ChrRamSizeBytes);
        Assert.Equal(0, cartridge.LowerPrgBank);
        Assert.Equal(0, cartridge.UpperPrgBank);
    }

    [Theory]
    [InlineData(0x8000, VirtualHardwareNesMirroring.Vertical)]
    [InlineData(0x8002, VirtualHardwareNesMirroring.Horizontal)]
    public void Address_bit_one_controls_live_horizontal_vertical_ciram_wiring(
        int writeAddress,
        VirtualHardwareNesMirroring expected)
    {
        var cartridge = CreateCartridge(prgBanks: 32);
        var cpu = CpuTarget(cartridge);

        cpu.Write!(writeAddress, 0x00);

        Assert.Equal(expected, cartridge.Mirroring);
    }

    [Fact]
    public void Live_ciram_a10_route_is_dynamic_and_not_statically_foldable()
    {
        var cartridge = CreateCartridge(prgBanks: 32);
        var cpu = CpuTarget(cartridge);
        var staticFacet = Assert.IsAssignableFrom<ICompiledStaticCombinationalComponent>(cartridge);
        var dynamicFacet = Assert.IsAssignableFrom<ICompiledCombinationalComponent>(cartridge);

        Assert.False(staticFacet.TryEvaluateCompiledStaticOutput(
            cartridge.CiramA10,
            _ => DigitalLevel.Low,
            out _));

        cpu.Write!(0x8002, 0x00); // horizontal: CIRAM A10 <- PPU A11
        Assert.True(dynamicFacet.TryEvaluateCompiledOutput(
            cartridge.CiramA10,
            pin => ReferenceEquals(pin, cartridge.PpuAddress.Pins[11])
                ? DigitalLevel.High
                : DigitalLevel.Low,
            out var horizontal));
        Assert.Equal(DigitalLevel.High, horizontal.Level);

        cpu.Write!(0x8000, 0x00); // vertical: CIRAM A10 <- PPU A10
        Assert.True(dynamicFacet.TryEvaluateCompiledOutput(
            cartridge.CiramA10,
            pin => ReferenceEquals(pin, cartridge.PpuAddress.Pins[10])
                ? DigitalLevel.High
                : DigitalLevel.Low,
            out var vertical));
        Assert.Equal(DigitalLevel.High, vertical.Level);
    }

    [Fact]
    public void Legacy_multicart_chr_ram_is_writable_in_unrom_modes_and_protected_in_nrom_modes()
    {
        var cartridge = CreateCartridge(prgBanks: 32);
        var cpu = CpuTarget(cartridge);
        var ppu = PpuTarget(cartridge);

        ppu.Write!(0x0123, 0x5A);
        Assert.Equal((byte)0x5A, ppu.Read!(0x0123));
        Assert.Equal(1UL, cartridge.PpuWriteCount);

        cpu.Write!(0x8080, 0x00); // O=1 -> NROM mode -> multicart CHR write protect
        Assert.True(cartridge.ChrRamWriteProtected);
        ppu.Write!(0x0123, 0xA5);

        Assert.Equal((byte)0x5A, ppu.Read!(0x0123));
        Assert.Equal(1UL, cartridge.PpuWriteCount);
        Assert.Equal(1UL, cartridge.ProtectedChrWriteCount);
    }

    [Fact]
    public void Submapper_zero_keeps_chr_ram_writable_in_nrom_mode()
    {
        var cartridge = CreateCartridge(prgBanks: 32, submapper: 0);
        var cpu = CpuTarget(cartridge);
        var ppu = PpuTarget(cartridge);

        cpu.Write!(0x8080, 0x00);
        ppu.Write!(0x0042, 0xA6);

        Assert.False(cartridge.ChrRamWriteProtected);
        Assert.Equal((byte)0xA6, ppu.Read!(0x0042));
        Assert.Equal(1UL, cartridge.PpuWriteCount);
        Assert.Equal(0UL, cartridge.ProtectedChrWriteCount);
    }

    [Fact]
    public void Submapper_one_solder_pad_mode_replaces_prg_a3_through_a0_on_reads()
    {
        var prg = CreatePrg(64);
        var bankFiveBase = 5 * 16 * 1024;
        for (var i = 0; i < 16; i++) prg[bankFiveBase + i] = (byte)(0x90 + i);

        var cartridge = new Mapper227Cartridge("TEST.M227.PADS", solderPadValue: 0x0A);
        cartridge.LoadImage(CreateExplicitImage(prg, submapper: 1));
        var cpu = CpuTarget(cartridge);

        cpu.Write!(0x8494, 0x00); // m=1, O=1, seed=5, NROM-128
        Assert.True(cartridge.SolderPadReadActive);

        Assert.Equal((byte)0x9A, cpu.Read!(0x8003));
        Assert.Equal((byte)0x9A, cpu.Read!(0x800F));
    }

    [Fact]
    public void Legacy_ines_multicart_retains_the_solder_pad_address_mux()
    {
        var prg = CreatePrg(32);
        var bankFiveBase = 5 * 16 * 1024;
        for (var i = 0; i < 16; i++) prg[bankFiveBase + i] = (byte)(0x90 + i);

        var cartridge = new Mapper227Cartridge("TEST.M227.LEGACY.PADS", solderPadValue: 0x0A);
        cartridge.LoadImage(CreateImage(prg, submapper: null, headerFormat: VirtualHardwareNesHeaderFormat.INes));
        var cpu = CpuTarget(cartridge);

        cpu.Write!(0x8494, 0x00);
        Assert.True(cartridge.SolderPadReadSupported);
        Assert.True(cartridge.SolderPadReadActive);
        Assert.Equal((byte)0x9A, cpu.Read!(0x8003));
        Assert.Equal((byte)0x9A, cpu.Read!(0x800F));
    }

    [Fact]
    public void Submapper_two_forces_a18_and_a17_low_for_fixed_inner_bank_zero()
    {
        var standard = CreateCartridge(prgBanks: 64, submapper: 1);
        var special = CreateCartridge(prgBanks: 64, submapper: 2);
        var standardCpu = CpuTarget(standard);
        var specialCpu = CpuTarget(special);

        standardCpu.Write!(0x8160, 0x00); // seed=$38, L=0
        specialCpu.Write!(0x8160, 0x00);

        Assert.Equal(56, standard.UpperPrgBank);
        Assert.Equal(32, special.UpperPrgBank);
    }

    [Fact]
    public void Physical_address_latch_clocks_on_falling_m2_and_does_not_use_cpu_data_for_banking()
    {
        var cartridge = CreateCartridge(prgBanks: 32);
        var board = new VirtualHardwareBoard("M227.PHYSICAL.LATCH.TEST");
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
        DriveAddress(address, 0x8214);
        DriveByte(data, 0xA5);
        romsel.Set(DigitalLevel.Low);
        rw.Set(DigitalLevel.Low);
        m2.Set(DigitalLevel.High);

        Assert.Equal((ushort)0x0000, cartridge.AddressLatch);
        Assert.Equal(0UL, cartridge.MapperWriteCount);

        m2.Set(DigitalLevel.Low);

        Assert.Equal((ushort)0x0214, cartridge.AddressLatch);
        Assert.Equal(5, cartridge.LowerPrgBank);
        Assert.Equal(7, cartridge.UpperPrgBank);
        Assert.Equal((byte)0xA5, cartridge.LastMapperWriteData);
        Assert.Equal(1UL, cartridge.MapperWriteCount);
    }

    [Fact]
    public void Compiled_mapper_write_uses_normal_rom_window_and_cycle_completion()
    {
        var cartridge = CreateCartridge(prgBanks: 32);
        var cpu = CpuTarget(cartridge);

        Assert.Equal(CompiledBusWritePhase.Complete, cpu.WritePhase);
        Assert.NotNull(cpu.Read);
        Assert.NotNull(cpu.Write);
        Assert.Contains(cpu.ReadConditions, condition => ReferenceEquals(condition.Pin, cartridge.CpuRomSelectBar)
            && condition.RequiredLevel == DigitalLevel.Low);
        Assert.Contains(cpu.WriteConditions, condition => ReferenceEquals(condition.Pin, cartridge.CpuRomSelectBar)
            && condition.RequiredLevel == DigitalLevel.Low);
        Assert.Contains(cpu.WriteConditions, condition => ReferenceEquals(condition.Pin, cartridge.CpuReadWrite)
            && condition.RequiredLevel == DigitalLevel.Low);

        cpu.Write!(0x8214, 0x3C);
        Assert.Equal((ushort)0x0214, cartridge.AddressLatch);
        Assert.Equal((byte)0x3C, cartridge.LastMapperWriteData);
    }

    [Fact]
    public void Generic_compiled_and_raw_physical_mapper_227_execute_the_same_address_latch_program()
    {
        var image = CreateExecutionImage();
        var compiled = new RegionalNesVirtualMachine();
        var reference = new RegionalNesVirtualMachine();

        compiled.SetCompiledLabExecutionEnabled(true);
        compiled.InsertRom(image, "Mapper 227 execution (Japan).nes");
        reference.InsertRom(image, "Mapper 227 execution (Japan).nes");
        reference.Famicom.SetCompiledPhysicalMachineEnabled(false);

        compiled.PowerOn();
        reference.PowerOn();
        compiled.ReleaseReset();
        reference.ReleaseReset();

        const int masterCycles = 16_000;
        compiled.AdvanceMasterCycles(masterCycles);
        reference.AdvanceMasterCycles(masterCycles);

        var compiledCartridge = Assert.IsType<Mapper227Cartridge>(compiled.Slot.Cartridge);
        var referenceCartridge = Assert.IsType<Mapper227Cartridge>(reference.Slot.Cartridge);
        Assert.True(compiled.Famicom.CompiledLabMotherboardEnabled);
        Assert.Equal((ushort)0x0214, compiledCartridge.AddressLatch);
        Assert.Equal((ushort)0x0214, referenceCartridge.AddressLatch);
        Assert.Equal(5, compiledCartridge.LowerPrgBank);
        Assert.Equal(7, compiledCartridge.UpperPrgBank);
        Assert.Equal(referenceCartridge.MapperWriteCount, compiledCartridge.MapperWriteCount);
        Assert.Equal(reference.Famicom.Cpu.ProgramCounter, compiled.Famicom.Cpu.ProgramCounter);
        Assert.Equal(reference.Famicom.Cpu.CompletedInstructionCount, compiled.Famicom.Cpu.CompletedInstructionCount);
        Assert.Equal(reference.Famicom.Ppu.InspectDiagnosticState(), compiled.Famicom.Ppu.InspectDiagnosticState());
        Assert.Equal(reference.Famicom.Ciram.InspectStateHash(), compiled.Famicom.Ciram.InspectStateHash());
    }

    [Fact]
    public void Standard_multicart_rejects_chr_rom_prg_ram_battery_four_screen_and_invalid_geometry()
    {
        var cartridge = new Mapper227Cartridge("TEST.M227.INVALID");
        var validPrg = CreatePrg(32);
        var badPrg = new byte[24 * 1024];
        var nonPowerOfTwoPrg = new byte[48 * 1024];
        var tooLargePrg = new byte[(1024 + 16) * 1024];
        var withChrRom = CreateExplicitImage(validPrg, submapper: 1) with
        {
            ChrRomSizeBytes = 8 * 1024,
            ChrRom = new byte[8 * 1024],
            ChrRamSizeBytes = 0
        };
        var withPrgRam = CreateExplicitImage(validPrg, submapper: 1) with
        {
            PrgRamSizeBytes = 8 * 1024
        };
        var withBattery = CreateExplicitImage(validPrg, submapper: 1) with
        {
            HasBatteryBackedMemory = true
        };
        var fourScreen = CreateExplicitImage(validPrg, submapper: 1) with
        {
            Mirroring = VirtualHardwareNesMirroring.FourScreen
        };

        Assert.Throws<ArgumentException>(() => cartridge.LoadImage(CreateExplicitImage(badPrg, submapper: 1)));
        Assert.Throws<NotSupportedException>(() => cartridge.LoadImage(CreateExplicitImage(nonPowerOfTwoPrg, submapper: 1)));
        Assert.Throws<ArgumentException>(() => cartridge.LoadImage(CreateExplicitImage(tooLargePrg, submapper: 1)));
        Assert.Throws<NotSupportedException>(() => cartridge.LoadImage(withChrRom));
        Assert.Throws<NotSupportedException>(() => cartridge.LoadImage(withPrgRam));
        Assert.Throws<NotSupportedException>(() => cartridge.LoadImage(withBattery));
        Assert.Throws<NotSupportedException>(() => cartridge.LoadImage(fourScreen));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(15)]
    public void Undefined_mapper_227_submappers_are_rejected_instead_of_approximated(int submapper)
    {
        var cartridge = new Mapper227Cartridge("TEST.M227.SUBMAPPER");

        Assert.Throws<NotSupportedException>(() =>
            cartridge.LoadImage(CreateExplicitImage(CreatePrg(32), submapper)));
    }

    [Fact]
    public void Factory_constructs_mapper_227_as_replaceable_address_latch_multicart_hardware()
    {
        var image = CreateImage(CreatePrg(32), submapper: null, headerFormat: VirtualHardwareNesHeaderFormat.INes);

        var cartridge = VirtualCartridgeHardwareFactory.Create(image);

        Assert.Equal(227, cartridge.MapperNumber);
        Assert.IsType<Mapper227Cartridge>(cartridge);
        Assert.True(cartridge.IsInserted);
    }

    private static Mapper227Cartridge CreateCartridge(int prgBanks, int? submapper = null)
    {
        var image = submapper is null
            ? CreateImage(CreatePrg(prgBanks), null, VirtualHardwareNesHeaderFormat.INes)
            : CreateExplicitImage(CreatePrg(prgBanks), submapper.Value);
        var cartridge = new Mapper227Cartridge("TEST.M227");
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
        var prg = new byte[32 * 16 * 1024];
        Array.Fill(prg, (byte)0xEA);

        var program = new byte[]
        {
            0x78,                   // SEI
            0xA9, 0x5A,             // LDA #$5A - CPU data is diagnostic only
            0x8D, 0x14, 0x82,       // STA $8214 - seed 5, UNROM mode, fixed inner bank 7
            0x4C, 0x06, 0x81        // JMP $8106 in newly selected lower bank
        };

        for (var bank = 0; bank < 32; bank++)
        {
            var bankBase = bank * 16 * 1024;
            program.CopyTo(prg, bankBase + 0x0100);
            prg[bankBase + 0x3FFA] = 0x00; prg[bankBase + 0x3FFB] = 0x81;
            prg[bankBase + 0x3FFC] = 0x00; prg[bankBase + 0x3FFD] = 0x81;
            prg[bankBase + 0x3FFE] = 0x00; prg[bankBase + 0x3FFF] = 0x81;
        }

        return CreateImage(prg, submapper: null, headerFormat: VirtualHardwareNesHeaderFormat.INes);
    }

    private static VirtualHardwareNesRomImage CreateExplicitImage(byte[] prg, int submapper) =>
        CreateImage(prg, submapper, VirtualHardwareNesHeaderFormat.Nes20) with
        {
            PrgRamSizeBytes = 0,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 8 * 1024,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        };

    private static VirtualHardwareNesRomImage CreateImage(
        byte[] prg,
        int? submapper,
        VirtualHardwareNesHeaderFormat headerFormat) =>
        new(
            headerFormat,
            MapperNumber: 227,
            SubmapperNumber: submapper,
            PrgRomSizeBytes: prg.Length,
            ChrRomSizeBytes: 0,
            HasTrainer: false,
            HasBatteryBackedMemory: false,
            VirtualHardwareNesMirroring.Horizontal,
            VirtualHardwareNesHeaderTiming.Ntsc,
            prg,
            []);

    private static CompiledBusTargetDescriptor CpuTarget(Mapper227Cartridge cartridge) =>
        ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets()
            .Single(target => target.AddressPins.Count == 15);

    private static CompiledBusTargetDescriptor PpuTarget(Mapper227Cartridge cartridge) =>
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
