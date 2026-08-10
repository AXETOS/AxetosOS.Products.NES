using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Machines.Nes;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareMmc2CartridgeTests
{
    [Fact]
    public void Power_on_maps_bank_zero_then_the_final_three_eight_kib_prg_banks()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuTarget(cartridge);

        Assert.Equal((byte)0x40, cpu.Read!(0x8000));
        Assert.Equal((byte)0x4D, cpu.Read!(0xA000));
        Assert.Equal((byte)0x4E, cpu.Read!(0xC000));
        Assert.Equal((byte)0x4F, cpu.Read!(0xE000));
        Assert.Equal(0, cartridge.SelectedPrgBank);
    }

    [Fact]
    public void A000_register_switches_only_the_low_eight_kib_prg_window()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuTarget(cartridge);

        cpu.Write!(0xA5A5, 0x07);

        Assert.Equal((byte)0x07, cartridge.PrgBankRegister);
        Assert.Equal(7, cartridge.SelectedPrgBank);
        Assert.Equal((byte)0x47, cpu.Read!(0x8000));
        Assert.Equal((byte)0x4D, cpu.Read!(0xA000));
        Assert.Equal((byte)0x4F, cpu.Read!(0xE000));
    }

    [Fact]
    public void Fitted_prg_address_lines_mask_the_switchable_bank_without_affecting_fixed_windows()
    {
        var cartridge = CreateCartridge(prgBanks: 8, chrBanks: 8);
        var cpu = CpuTarget(cartridge);

        cpu.Write!(0xA000, 0x0F);

        Assert.Equal((byte)0x0F, cartridge.PrgBankRegister);
        Assert.Equal(7, cartridge.SelectedPrgBank);
        Assert.Equal((byte)0x47, cpu.Read!(0x8000));
        Assert.Equal((byte)0x45, cpu.Read!(0xA000));
        Assert.Equal((byte)0x46, cpu.Read!(0xC000));
        Assert.Equal((byte)0x47, cpu.Read!(0xE000));
    }

    [Fact]
    public void B_through_E_registers_latch_only_the_five_chr_bank_output_bits()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuTarget(cartridge);

        cpu.Write!(0xB000, 0xE3);
        cpu.Write!(0xC000, 0xE4);
        cpu.Write!(0xD000, 0xE5);
        cpu.Write!(0xE000, 0xE6);

        Assert.Equal(new byte[] { 0x03, 0x04, 0x05, 0x06 }, cartridge.ChrBankRegisters.ToArray());
        Assert.Equal(4UL, cartridge.MapperWriteCount);
    }

    [Fact]
    public void Low_pattern_latch_fd_trigger_changes_bank_after_the_current_read()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuTarget(cartridge);
        var ppu = PpuTarget(cartridge);
        cpu.Write!(0xB000, 0x03);
        cpu.Write!(0xC000, 0x04);

        Assert.Equal((byte)0x84, ppu.Read!(0x0000));
        Assert.Equal((byte)0x84, ppu.Read!(0x0FD8)); // trigger read still comes from old FE bank
        Assert.Equal((byte)0xFD, cartridge.Latch0);
        Assert.Equal(3, cartridge.SelectedChrBank0);
        Assert.Equal((byte)0x83, ppu.Read!(0x0000));
        Assert.Equal(1UL, cartridge.Latch0FdTriggerCount);
    }

    [Fact]
    public void Low_pattern_latch_fe_trigger_changes_bank_after_the_current_read()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuTarget(cartridge);
        var ppu = PpuTarget(cartridge);
        cpu.Write!(0xB000, 0x03);
        cpu.Write!(0xC000, 0x04);
        _ = ppu.Read!(0x0FD8);

        Assert.Equal((byte)0x83, ppu.Read!(0x0FE8));
        Assert.Equal((byte)0xFE, cartridge.Latch0);
        Assert.Equal(4, cartridge.SelectedChrBank0);
        Assert.Equal((byte)0x84, ppu.Read!(0x0000));
        Assert.Equal(1UL, cartridge.Latch0FeTriggerCount);
    }

    [Fact]
    public void Mmc2_low_pattern_latch_uses_exact_trigger_addresses_not_mmc4_ranges()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var ppu = PpuTarget(cartridge);

        _ = ppu.Read!(0x0FD9);
        _ = ppu.Read!(0x0FDF);
        _ = ppu.Read!(0x0FE9);
        _ = ppu.Read!(0x0FEF);

        Assert.Equal((byte)0xFE, cartridge.Latch0);
        Assert.Equal(0UL, cartridge.LatchTriggerCount);
    }

    [Fact]
    public void High_pattern_fd_trigger_decodes_the_full_1fd8_1fdf_range()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuTarget(cartridge);
        var ppu = PpuTarget(cartridge);
        cpu.Write!(0xD000, 0x05);
        cpu.Write!(0xE000, 0x06);

        Assert.Equal((byte)0x86, ppu.Read!(0x1000));
        Assert.Equal((byte)0x86, ppu.Read!(0x1FDF));
        Assert.Equal((byte)0xFD, cartridge.Latch1);
        Assert.Equal(5, cartridge.SelectedChrBank1);
        Assert.Equal((byte)0x85, ppu.Read!(0x1000));
    }

    [Fact]
    public void High_pattern_fe_trigger_decodes_the_full_1fe8_1fef_range()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuTarget(cartridge);
        var ppu = PpuTarget(cartridge);
        cpu.Write!(0xD000, 0x05);
        cpu.Write!(0xE000, 0x06);
        _ = ppu.Read!(0x1FD8);

        Assert.Equal((byte)0x85, ppu.Read!(0x1FEF));
        Assert.Equal((byte)0xFE, cartridge.Latch1);
        Assert.Equal(6, cartridge.SelectedChrBank1);
        Assert.Equal((byte)0x86, ppu.Read!(0x1000));
    }

    [Fact]
    public void Raw_physical_ppu_trigger_holds_old_chr_data_for_the_triggering_bus_read()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuTarget(cartridge);
        cpu.Write!(0xB000, 0x03);
        cpu.Write!(0xC000, 0x04);

        var board = new VirtualHardwareBoard("MMC2.PHYSICAL.PPU.TEST");
        board.Add(cartridge);
        var vcc = AttachSource(board, "VCC", cartridge.Vcc);
        var gnd = AttachSource(board, "GND", cartridge.Gnd);
        var rd = AttachSource(board, "RD", cartridge.PpuReadBar);
        var wr = AttachSource(board, "WR", cartridge.PpuWriteBar);
        var address = cartridge.PpuAddress.Pins
            .Select((pin, bit) => AttachSource(board, $"PA{bit}", pin))
            .ToArray();
        // PPU D0-D7 are package pins on physical traces too.  A pin with no
        // attached net can drive electrically but has no resolved level to
        // sample.  Attach passive high-impedance observation traces just as
        // the other raw-cartridge bus tests do; they do not drive the bus.
        _ = cartridge.PpuData.Pins
            .Select((pin, bit) => AttachSource(board, $"PD{bit}", pin))
            .ToArray();
        _ = new VirtualHardwareSimulator(board);

        gnd.Set(DigitalLevel.Low);
        vcc.Set(DigitalLevel.High);
        wr.Set(DigitalLevel.High);
        rd.Set(DigitalLevel.High);
        DriveAddress(address, 0x0FD8);
        rd.Set(DigitalLevel.Low);

        Assert.True(cartridge.PpuData.TrySample(out var triggerData));
        Assert.Equal((ulong)0x84, triggerData);
        Assert.Equal((byte)0xFD, cartridge.Latch0);

        rd.Set(DigitalLevel.High);
        DriveAddress(address, 0x0000);
        rd.Set(DigitalLevel.Low);
        Assert.True(cartridge.PpuData.TrySample(out var nextData));
        Assert.Equal((ulong)0x83, nextData);
    }

    [Fact]
    public void F000_mirroring_register_is_live_dynamic_ciram_wiring_not_a_static_fold()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32,
            mirroring: VirtualHardwareNesMirroring.Vertical);
        var cpu = CpuTarget(cartridge);
        var dynamic = Assert.IsAssignableFrom<ICompiledCombinationalComponent>(cartridge);
        var statik = Assert.IsAssignableFrom<ICompiledStaticCombinationalComponent>(cartridge);

        Assert.False(statik.TryEvaluateCompiledStaticOutput(
            cartridge.CiramA10,
            pin => SamplePpuAddress(cartridge, pin, 0x0400),
            out _));
        Assert.True(dynamic.TryEvaluateCompiledOutput(
            cartridge.CiramA10,
            pin => SamplePpuAddress(cartridge, pin, 0x0400),
            out var vertical));
        Assert.Equal(DigitalLevel.High, vertical.Level);

        cpu.Write!(0xF000, 0x01);
        Assert.Equal(VirtualHardwareNesMirroring.Horizontal, cartridge.Mirroring);
        Assert.True(dynamic.TryEvaluateCompiledOutput(
            cartridge.CiramA10,
            pin => SamplePpuAddress(cartridge, pin, 0x0400),
            out var horizontal));
        Assert.Equal(DigitalLevel.Low, horizontal.Level);
    }

    [Fact]
    public void Writes_below_A000_do_not_alias_any_mmc2_register()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuTarget(cartridge);

        cpu.Write!(0x8000, 0x09);
        cpu.Write!(0x9000, 0x0A);

        Assert.Equal((byte)0x00, cartridge.PrgBankRegister);
        Assert.All(cartridge.ChrBankRegisters, value => Assert.Equal((byte)0x00, value));
        Assert.Equal(0UL, cartridge.MapperWriteCount);
    }

    [Fact]
    public void Mmc2_has_no_cpu_rom_bus_conflict_and_latches_cpu_data_directly()
    {
        var prg = CreatePrg(16);
        prg[0x2000] = 0x00;
        var image = CreateImage(prg, CreateChr(32));
        var cartridge = new Mmc2Cartridge("TEST.MMC2.NO.CONFLICT");
        cartridge.LoadImage(image);
        var cpu = CpuTarget(cartridge);

        cpu.Write!(0xA000, 0x05);

        Assert.Equal((byte)0x05, cartridge.PrgBankRegister);
        Assert.Equal(5, cartridge.SelectedPrgBank);
    }

    [Fact]
    public void Compiled_mapper_writes_commit_at_completed_cpu_bus_phase()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuTarget(cartridge);

        Assert.Equal(CompiledBusWritePhase.Complete, cpu.WritePhase);
    }

    [Fact]
    public void Generic_compiled_and_raw_physical_mmc2_execute_the_same_cpu_and_ppu_latch_program()
    {
        var image = CreateExecutionImage();
        var compiled = new RegionalNesVirtualMachine();
        var reference = new RegionalNesVirtualMachine();

        compiled.SetCompiledLabExecutionEnabled(true);
        compiled.InsertRom(image, "MMC2 execution (Japan).nes");
        reference.InsertRom(image, "MMC2 execution (Japan).nes");
        reference.Famicom.SetCompiledPhysicalMachineEnabled(false);

        compiled.PowerOn();
        reference.PowerOn();
        compiled.ReleaseReset();
        reference.ReleaseReset();

        const int masterCycles = 40_000;
        compiled.AdvanceMasterCycles(masterCycles);
        reference.AdvanceMasterCycles(masterCycles);

        var compiledCartridge = Assert.IsType<Mmc2Cartridge>(compiled.Slot.Cartridge);
        var referenceCartridge = Assert.IsType<Mmc2Cartridge>(reference.Slot.Cartridge);
        Assert.True(compiled.Famicom.CompiledLabMotherboardEnabled);
        Assert.Equal((byte)0x03, compiledCartridge.PrgBankRegister);
        Assert.Equal(new byte[] { 0x05, 0x06, 0x07, 0x08 }, compiledCartridge.ChrBankRegisters.ToArray());
        Assert.Equal(VirtualHardwareNesMirroring.Horizontal, compiledCartridge.Mirroring);
        Assert.Equal(referenceCartridge.Latch0, compiledCartridge.Latch0);
        Assert.Equal(referenceCartridge.Latch1, compiledCartridge.Latch1);
        Assert.Equal(referenceCartridge.LatchTriggerCount, compiledCartridge.LatchTriggerCount);
        Assert.Equal(referenceCartridge.MapperWriteCount, compiledCartridge.MapperWriteCount);
        Assert.Equal(reference.Famicom.Cpu.ProgramCounter, compiled.Famicom.Cpu.ProgramCounter);
        Assert.Equal(reference.Famicom.Cpu.CompletedInstructionCount, compiled.Famicom.Cpu.CompletedInstructionCount);
        Assert.Equal(reference.Famicom.Ppu.InspectDiagnosticState(), compiled.Famicom.Ppu.InspectDiagnosticState());
        Assert.Equal(reference.Famicom.Ciram.InspectStateHash(), compiled.Famicom.Ciram.InspectStateHash());
    }

    [Fact]
    public void Legacy_ines_inferred_prg_ram_is_not_silently_added_to_pxrom()
    {
        var image = CreateImage(CreatePrg(16), CreateChr(32),
            headerFormat: VirtualHardwareNesHeaderFormat.INes) with
        {
            PrgRamSizeBytes = 8 * 1024,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 0,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = false
        };
        var cartridge = new Mmc2Cartridge("TEST.MMC2.INES");

        cartridge.LoadImage(image);

        Assert.True(cartridge.IsInserted);
        Assert.Single(((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.AddressPins.Count == 15);
    }

    [Fact]
    public void Unsupported_pxrom_ram_four_screen_and_submapper_variants_require_distinct_hardware()
    {
        var baseImage = CreateImage(CreatePrg(16), CreateChr(32));
        var prgRam = baseImage with
        {
            PrgRamSizeBytes = 8 * 1024,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 0,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        };
        var chrRam = baseImage with
        {
            PrgRamSizeBytes = 0,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 8 * 1024,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        };
        var fourScreen = baseImage with { Mirroring = VirtualHardwareNesMirroring.FourScreen };
        var submapper = baseImage with { SubmapperNumber = 1 };
        var noChrRom = CreateImage(CreatePrg(16), []);

        Assert.Throws<NotSupportedException>(() => new Mmc2Cartridge("TEST.MMC2.PRAM").LoadImage(prgRam));
        Assert.Throws<NotSupportedException>(() => new Mmc2Cartridge("TEST.MMC2.CRAM").LoadImage(chrRam));
        Assert.Throws<NotSupportedException>(() => new Mmc2Cartridge("TEST.MMC2.4S").LoadImage(fourScreen));
        Assert.Throws<NotSupportedException>(() => new Mmc2Cartridge("TEST.MMC2.SUB").LoadImage(submapper));
        Assert.Throws<NotSupportedException>(() => new Mmc2Cartridge("TEST.MMC2.NOCHR").LoadImage(noChrRom));
    }

    [Fact]
    public void Factory_constructs_mapper_nine_as_replaceable_mmc2_hardware()
    {
        var image = CreateImage(CreatePrg(16), CreateChr(32));
        var cartridge = VirtualCartridgeHardwareFactory.Create(image);

        Assert.Equal(9, cartridge.MapperNumber);
        Assert.IsType<Mmc2Cartridge>(cartridge);
        Assert.True(cartridge.IsInserted);
    }

    private static Mmc2Cartridge CreateCartridge(
        int prgBanks,
        int chrBanks,
        VirtualHardwareNesMirroring mirroring = VirtualHardwareNesMirroring.Horizontal)
    {
        var cartridge = new Mmc2Cartridge("TEST.MMC2");
        cartridge.LoadImage(CreateImage(CreatePrg(prgBanks), CreateChr(chrBanks), mirroring: mirroring) with
        {
            PrgRamSizeBytes = 0,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 0,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        });
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
        var chr = new byte[banks * 4 * 1024];
        for (var bank = 0; bank < banks; bank++)
            Array.Fill(chr, (byte)(0x80 + bank), bank * 4 * 1024, 4 * 1024);
        return chr;
    }

    private static VirtualHardwareNesRomImage CreateImage(
        byte[] prg,
        byte[] chr,
        int? submapper = 0,
        VirtualHardwareNesMirroring mirroring = VirtualHardwareNesMirroring.Horizontal,
        VirtualHardwareNesHeaderFormat headerFormat = VirtualHardwareNesHeaderFormat.Nes20) =>
        new(
            headerFormat,
            MapperNumber: 9,
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
        var program = new List<byte>
        {
            0x78,                               // SEI
            0xA9, 0x03, 0x8D, 0x00, 0xA0,     // PRG bank 3
            0xA9, 0x05, 0x8D, 0x00, 0xB0,     // CHR0 FD bank 5
            0xA9, 0x06, 0x8D, 0x00, 0xC0,     // CHR0 FE bank 6
            0xA9, 0x07, 0x8D, 0x00, 0xD0,     // CHR1 FD bank 7
            0xA9, 0x08, 0x8D, 0x00, 0xE0,     // CHR1 FE bank 8
            0xA9, 0x01, 0x8D, 0x00, 0xF0,     // horizontal mirroring
            0xAD, 0x02, 0x20                    // LDA $2002 - reset PPU address latch
        };
        AddPpuRead(program, 0x0FD8);
        AddPpuRead(program, 0x0FE8);
        AddPpuRead(program, 0x1FD8);
        AddPpuRead(program, 0x1FE8);
        var loopAddress = (ushort)(0xE000 + program.Count);
        program.Add(0x4C);
        program.Add((byte)(loopAddress & 0xFF));
        program.Add((byte)(loopAddress >> 8));

        var fixedLast = 15 * 8 * 1024;
        program.ToArray().CopyTo(prg, fixedLast);
        prg[fixedLast + 0x1FFA] = 0x00; prg[fixedLast + 0x1FFB] = 0xE0;
        prg[fixedLast + 0x1FFC] = 0x00; prg[fixedLast + 0x1FFD] = 0xE0;
        prg[fixedLast + 0x1FFE] = 0x00; prg[fixedLast + 0x1FFF] = 0xE0;

        return CreateImage(prg, CreateChr(32)) with
        {
            PrgRamSizeBytes = 0,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 0,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        };
    }

    private static void AddPpuRead(List<byte> program, ushort address)
    {
        program.AddRange(new byte[]
        {
            0xA9, (byte)(address >> 8),
            0x8D, 0x06, 0x20,
            0xA9, (byte)(address & 0xFF),
            0x8D, 0x06, 0x20,
            0xAD, 0x07, 0x20
        });
    }

    private static CompiledBusTargetDescriptor CpuTarget(Mmc2Cartridge cartridge) =>
        ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets()
            .Single(target => target.AddressPins.Count == 15);

    private static CompiledBusTargetDescriptor PpuTarget(Mmc2Cartridge cartridge) =>
        ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets()
            .Single(target => target.AddressPins.Count == 14);

    private static DigitalLevel SamplePpuAddress(Mmc2Cartridge cartridge, DigitalPin pin, ushort address)
    {
        for (var bit = 0; bit < cartridge.PpuAddress.Width; bit++)
        {
            if (!ReferenceEquals(pin, cartridge.PpuAddress.Pins[bit])) continue;
            return (address & (1 << bit)) != 0 ? DigitalLevel.High : DigitalLevel.Low;
        }
        return DigitalLevel.Unknown;
    }

    private static DigitalSignalSource AttachSource(VirtualHardwareBoard board, string id, DigitalPin pin)
    {
        var source = board.Add(new DigitalSignalSource($"TEST.{id}", DigitalLevel.HighImpedance));
        board.Connect($"TEST.{id}.NET", source.Output, pin);
        return source;
    }

    private static void DriveAddress(IReadOnlyList<DigitalSignalSource> sources, ushort value)
    {
        for (var bit = 0; bit < sources.Count; bit++)
            sources[bit].Set((value & (1 << bit)) != 0 ? DigitalLevel.High : DigitalLevel.Low);
    }
}
