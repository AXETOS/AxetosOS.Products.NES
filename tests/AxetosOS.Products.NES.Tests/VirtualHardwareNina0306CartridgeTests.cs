using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Machines.Nes;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareNina0306CartridgeTests
{
    [Fact]
    public void Power_on_selects_first_prg_and_chr_banks()
    {
        var cartridge = CreateCartridge(prgBanks: 2, chrBanks: 8);
        var cpu = CpuPrgTarget(cartridge);
        var ppu = PpuTarget(cartridge);

        Assert.Equal((byte)0x40, cpu.Read!(0x8000));
        Assert.Equal((byte)0x60, ppu.Read!(0x0012));
        Assert.Equal(0, cartridge.SelectedPrgBank);
        Assert.Equal(0, cartridge.SelectedChrBank);
        Assert.Equal((byte)0x00, cartridge.BankRegister);
    }

    [Fact]
    public void Control_latch_switches_prg_and_chr_windows_from_d3_and_d0_through_d2()
    {
        var cartridge = CreateCartridge(prgBanks: 2, chrBanks: 8);
        var control = ControlTarget(cartridge);
        var cpu = CpuPrgTarget(cartridge);
        var ppu = PpuTarget(cartridge);

        control.Write!(0x4100, 0x0D);

        Assert.Equal((byte)0x0D, cartridge.BankRegister);
        Assert.Equal(1, cartridge.SelectedPrgBank);
        Assert.Equal(5, cartridge.SelectedChrBank);
        Assert.Equal((byte)0x41, cpu.Read!(0x8000));
        Assert.Equal((byte)0x65, ppu.Read!(0x0012));
        Assert.Equal(1UL, cartridge.MapperWriteCount);
    }

    [Fact]
    public void Fitted_rom_address_lines_mask_unconnected_bank_outputs_instead_of_modulo_normalizing()
    {
        var cartridge = CreateCartridge(prgBanks: 1, chrBanks: 2);
        var control = ControlTarget(cartridge);
        var cpu = CpuPrgTarget(cartridge);
        var ppu = PpuTarget(cartridge);

        control.Write!(0x4100, 0xFF);

        Assert.Equal((byte)0x01, cartridge.BankRegister);
        Assert.Equal(0, cartridge.SelectedPrgBank);
        Assert.Equal(1, cartridge.SelectedChrBank);
        Assert.Equal((byte)0x40, cpu.Read!(0x8000));
        Assert.Equal((byte)0x61, ppu.Read!(0x0012));
    }

    [Theory]
    [InlineData(0x4100)]
    [InlineData(0x43FF)]
    [InlineData(0x5D42)]
    [InlineData(0x5FFF)]
    public void Address_decoder_accepts_every_010x_xxx1_xxxx_xxxx_control_window(int address)
    {
        var cartridge = CreateCartridge(prgBanks: 2, chrBanks: 8);
        var control = ControlTarget(cartridge);

        control.Write!(address, 0x0D);

        Assert.Equal(1UL, cartridge.MapperWriteCount);
        Assert.Equal((ushort)address, cartridge.LastMapperWriteAddress);
        Assert.Equal((byte)0x0D, cartridge.BankRegister);
    }

    [Theory]
    [InlineData(0x4000)]
    [InlineData(0x4200)]
    [InlineData(0x6100)]
    [InlineData(0xC100)]
    public void Address_decoder_ignores_nearby_or_high_half_cpu_addresses(int address)
    {
        var cartridge = CreateCartridge(prgBanks: 2, chrBanks: 8);
        var control = ControlTarget(cartridge);

        control.Write!(address, 0x0D);

        Assert.Equal(0UL, cartridge.MapperWriteCount);
        Assert.Equal((byte)0x00, cartridge.BankRegister);
    }

    [Fact]
    public void Control_write_has_no_cpu_rom_bus_conflict_and_unconnected_high_data_bits_are_not_latched()
    {
        var cartridge = CreateCartridge(prgBanks: 2, chrBanks: 8);
        var control = ControlTarget(cartridge);

        control.Write!(0x4100, 0xFD);

        Assert.False(cartridge.BusConflictsEnabled);
        Assert.Equal((byte)0xFD, cartridge.LastMapperWriteData);
        Assert.Equal((byte)0x0D, cartridge.BankRegister);
        Assert.Equal(1, cartridge.SelectedPrgBank);
        Assert.Equal(5, cartridge.SelectedChrBank);
    }

    [Theory]
    [InlineData(VirtualHardwareNesMirroring.Horizontal, DigitalLevel.High)]
    [InlineData(VirtualHardwareNesMirroring.Vertical, DigitalLevel.Low)]
    public void Fixed_board_mirroring_routes_the_expected_ppu_address_line_to_ciram_a10(
        VirtualHardwareNesMirroring mirroring,
        DigitalLevel expected)
    {
        var cartridge = CreateCartridge(prgBanks: 2, chrBanks: 8, mirroring: mirroring);
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
    public void Control_latch_samples_the_physical_cpu_data_bus_on_falling_M2_edge()
    {
        var cartridge = CreateCartridge(prgBanks: 2, chrBanks: 8);
        var board = new VirtualHardwareBoard("NINA.PHYSICAL.LATCH.TEST");
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
        DriveAddress(address, 0x4100);
        DriveByte(data, 0x0D);
        romsel.Set(DigitalLevel.High);
        rw.Set(DigitalLevel.Low);
        m2.Set(DigitalLevel.High);

        Assert.Equal(0, cartridge.SelectedPrgBank);
        Assert.Equal(0, cartridge.SelectedChrBank);
        Assert.Equal(0UL, cartridge.MapperWriteCount);

        m2.Set(DigitalLevel.Low);

        Assert.Equal((byte)0x0D, cartridge.BankRegister);
        Assert.Equal(1, cartridge.SelectedPrgBank);
        Assert.Equal(5, cartridge.SelectedChrBank);
        Assert.Equal(1UL, cartridge.MapperWriteCount);
        Assert.Equal((ushort)0x4100, cartridge.LastMapperWriteAddress);
    }

    [Fact]
    public void Physical_decoder_requires_inactive_romsel_so_the_same_low_address_pins_do_not_alias_the_high_cpu_half()
    {
        var cartridge = CreateCartridge(prgBanks: 2, chrBanks: 8);
        var board = new VirtualHardwareBoard("NINA.PHYSICAL.A15.TEST");
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
        DriveAddress(address, 0x4100);
        DriveByte(data, 0x0D);
        romsel.Set(DigitalLevel.Low); // A15=1 at the cartridge boundary.
        rw.Set(DigitalLevel.Low);
        m2.Set(DigitalLevel.High);
        m2.Set(DigitalLevel.Low);

        Assert.Equal(0UL, cartridge.MapperWriteCount);
        Assert.Equal((byte)0x00, cartridge.BankRegister);
    }

    [Fact]
    public void Compiled_control_write_preserves_the_physical_address_decode_and_latches_at_cycle_completion()
    {
        var cartridge = CreateCartridge(prgBanks: 2, chrBanks: 8);
        var control = ControlTarget(cartridge);

        Assert.Equal(CompiledBusWritePhase.Complete, control.WritePhase);
        Assert.Null(control.Read);
        Assert.Contains(control.WriteConditions, condition => ReferenceEquals(condition.Pin, cartridge.CpuReadWrite)
            && condition.RequiredLevel == DigitalLevel.Low);
        Assert.Contains(control.WriteConditions, condition => ReferenceEquals(condition.Pin, cartridge.CpuRomSelectBar)
            && condition.RequiredLevel == DigitalLevel.High);
        Assert.Contains(control.WriteConditions, condition => ReferenceEquals(condition.Pin, cartridge.CpuAddress.Pins[14])
            && condition.RequiredLevel == DigitalLevel.High);
        Assert.Contains(control.WriteConditions, condition => ReferenceEquals(condition.Pin, cartridge.CpuAddress.Pins[13])
            && condition.RequiredLevel == DigitalLevel.Low);
        Assert.Contains(control.WriteConditions, condition => ReferenceEquals(condition.Pin, cartridge.CpuAddress.Pins[8])
            && condition.RequiredLevel == DigitalLevel.High);
    }

    [Fact]
    public void Prg_rom_target_is_read_only_and_selected_only_by_active_romsel()
    {
        var cartridge = CreateCartridge(prgBanks: 2, chrBanks: 8);
        var cpu = CpuPrgTarget(cartridge);

        Assert.Null(cpu.Write);
        Assert.Empty(cpu.WriteConditions);
        Assert.Contains(cpu.ReadConditions, condition => ReferenceEquals(condition.Pin, cartridge.CpuRomSelectBar)
            && condition.RequiredLevel == DigitalLevel.Low);
        Assert.Equal((byte)0x40, cpu.Read!(0x8000));
    }

    [Fact]
    public void Chr_rom_is_read_only_on_the_compiled_ppu_bus()
    {
        var cartridge = CreateCartridge(prgBanks: 2, chrBanks: 8);
        var ppu = PpuTarget(cartridge);
        var before = ppu.Read!(0x0012);

        Assert.Null(ppu.Write);
        Assert.Empty(ppu.WriteConditions);
        Assert.Equal(before, ppu.Read!(0x0012));
        Assert.Equal(0UL, cartridge.PpuWriteCount);
    }

    [Fact]
    public void Generic_compiled_and_raw_physical_nina_execute_the_same_low_address_dual_bank_switch_program()
    {
        var image = CreateExecutionImage();
        var compiled = new RegionalNesVirtualMachine();
        var reference = new RegionalNesVirtualMachine();

        compiled.SetCompiledLabExecutionEnabled(true);
        compiled.InsertRom(image, "NINA execution (Japan).nes");
        reference.InsertRom(image, "NINA execution (Japan).nes");
        reference.Famicom.SetCompiledPhysicalMachineEnabled(false);

        compiled.PowerOn();
        reference.PowerOn();
        compiled.ReleaseReset();
        reference.ReleaseReset();

        const int masterCycles = 16_000;
        compiled.AdvanceMasterCycles(masterCycles);
        reference.AdvanceMasterCycles(masterCycles);

        var compiledCartridge = Assert.IsType<Nina0306Cartridge>(compiled.Slot.Cartridge);
        var referenceCartridge = Assert.IsType<Nina0306Cartridge>(reference.Slot.Cartridge);
        Assert.True(compiled.Famicom.CompiledLabMotherboardEnabled);
        Assert.Equal((byte)0x0D, compiledCartridge.BankRegister);
        Assert.Equal((byte)0x0D, referenceCartridge.BankRegister);
        Assert.Equal(1, compiledCartridge.SelectedPrgBank);
        Assert.Equal(5, compiledCartridge.SelectedChrBank);
        Assert.Equal(referenceCartridge.MapperWriteCount, compiledCartridge.MapperWriteCount);
        Assert.Equal(reference.Famicom.Cpu.ProgramCounter, compiled.Famicom.Cpu.ProgramCounter);
        Assert.Equal(reference.Famicom.Cpu.CompletedInstructionCount, compiled.Famicom.Cpu.CompletedInstructionCount);
        Assert.Equal(reference.Famicom.Ppu.InspectDiagnosticState(), compiled.Famicom.Ppu.InspectDiagnosticState());
        Assert.Equal(reference.Famicom.Ciram.InspectStateHash(), compiled.Famicom.Ciram.InspectStateHash());
    }

    [Fact]
    public void Standard_nina_rejects_ram_four_screen_battery_and_invalid_rom_geometry()
    {
        var cartridge = new Nina0306Cartridge("TEST.NINA.INVALID");
        var validPrg = CreatePrg(2);
        var validChr = CreateChr(8);
        var badPrg = CreateImage(new byte[48 * 1024], validChr, submapper: 0);
        var tooLargePrg = CreateImage(new byte[96 * 1024], validChr, submapper: 0);
        var badChr = CreateImage(validPrg, new byte[24 * 1024], submapper: 0);
        var noChr = CreateImage(validPrg, [], submapper: 0);
        var withRam = CreateImage(validPrg, validChr, submapper: 0) with
        {
            PrgRamSizeBytes = 8 * 1024,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 0,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        };
        var withBattery = CreateImage(validPrg, validChr, submapper: 0) with
        {
            HasBatteryBackedMemory = true
        };
        var fourScreen = CreateImage(validPrg, validChr, submapper: 0,
            mirroring: VirtualHardwareNesMirroring.FourScreen);

        Assert.Throws<ArgumentException>(() => cartridge.LoadImage(badPrg));
        Assert.Throws<ArgumentException>(() => cartridge.LoadImage(tooLargePrg));
        Assert.Throws<NotSupportedException>(() => cartridge.LoadImage(badChr));
        Assert.Throws<ArgumentException>(() => cartridge.LoadImage(noChr));
        Assert.Throws<NotSupportedException>(() => cartridge.LoadImage(withRam));
        Assert.Throws<NotSupportedException>(() => cartridge.LoadImage(withBattery));
        Assert.Throws<NotSupportedException>(() => cartridge.LoadImage(fourScreen));
    }

    [Fact]
    public void Undefined_nonzero_mapper_seventy_nine_submapper_is_rejected_instead_of_guessing_a_board_variant()
    {
        var cartridge = new Nina0306Cartridge("TEST.NINA.SUBMAPPER");
        var image = CreateImage(CreatePrg(2), CreateChr(8), submapper: 1);

        Assert.Throws<NotSupportedException>(() => cartridge.LoadImage(image));
    }

    [Fact]
    public void Factory_constructs_mapper_seventy_nine_as_replaceable_nina_hardware()
    {
        var image = CreateImage(CreatePrg(2), CreateChr(8), submapper: 0);

        var cartridge = VirtualCartridgeHardwareFactory.Create(image);

        Assert.Equal(79, cartridge.MapperNumber);
        Assert.IsType<Nina0306Cartridge>(cartridge);
        Assert.True(cartridge.IsInserted);
    }

    private static Nina0306Cartridge CreateCartridge(
        int prgBanks,
        int chrBanks,
        VirtualHardwareNesMirroring mirroring = VirtualHardwareNesMirroring.Horizontal)
    {
        var image = CreateImage(CreatePrg(prgBanks), CreateChr(chrBanks), submapper: 0, mirroring: mirroring) with
        {
            PrgRamSizeBytes = 0,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 0,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        };
        var cartridge = new Nina0306Cartridge("TEST.NINA");
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
        var prg = new byte[2 * 32 * 1024];
        Array.Fill(prg, (byte)0xEA);

        var program = new byte[]
        {
            0x78,                   // SEI
            0xA9, 0x0D,             // LDA #$0D - PRG bank 1 + CHR bank 5
            0x8D, 0x00, 0x41,       // STA $4100 - NINA address-decoded control latch
            0x4C, 0x06, 0x81        // JMP $8106 in the newly selected PRG bank
        };

        for (var bank = 0; bank < 2; bank++)
        {
            var bankBase = bank * 32 * 1024;
            program.CopyTo(prg, bankBase + 0x0100);
            prg[bankBase + 0x7FFA] = 0x00; prg[bankBase + 0x7FFB] = 0x81;
            prg[bankBase + 0x7FFC] = 0x00; prg[bankBase + 0x7FFD] = 0x81;
            prg[bankBase + 0x7FFE] = 0x00; prg[bankBase + 0x7FFF] = 0x81;
        }

        return CreateImage(prg, CreateChr(8), submapper: 0) with
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
            MapperNumber: 79,
            SubmapperNumber: submapper,
            PrgRomSizeBytes: prg.Length,
            ChrRomSizeBytes: chr.Length,
            HasTrainer: false,
            HasBatteryBackedMemory: false,
            mirroring,
            VirtualHardwareNesHeaderTiming.Ntsc,
            prg,
            chr);

    private static CompiledBusTargetDescriptor CpuPrgTarget(Nina0306Cartridge cartridge) =>
        ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets()
            .Single(target => target.AddressPins.Count == 15 && target.Read is not null);

    private static CompiledBusTargetDescriptor ControlTarget(Nina0306Cartridge cartridge) =>
        ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets()
            .Single(target => target.AddressPins.Count == 15 && target.Write is not null);

    private static CompiledBusTargetDescriptor PpuTarget(Nina0306Cartridge cartridge) =>
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
