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

public sealed class VirtualHardwareMmc4CartridgeTests
{
    [Fact]
    public void Power_on_maps_bank_zero_and_fixed_last_sixteen_kib_prg_bank()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);

        Assert.Equal((byte)0x40, cpu.Read!(0x0000));
        Assert.Equal((byte)0x40, cpu.Read!(0x3FFF));
        Assert.Equal((byte)0x4F, cpu.Read!(0x4000));
        Assert.Equal((byte)0x4F, cpu.Read!(0x7FFF));
        Assert.Equal(0, cartridge.SelectedPrgBank);
        Assert.Equal(8 * 1024, cartridge.PrgRamSizeBytes);
    }

    [Fact]
    public void A000_register_switches_only_the_low_sixteen_kib_prg_window()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);

        cpu.Write!(0x2500, 0x07);

        Assert.Equal((byte)0x07, cartridge.PrgBankRegister);
        Assert.Equal(7, cartridge.SelectedPrgBank);
        Assert.Equal((byte)0x47, cpu.Read!(0x0000));
        Assert.Equal((byte)0x47, cpu.Read!(0x3FFF));
        Assert.Equal((byte)0x4F, cpu.Read!(0x4000));
    }

    [Fact]
    public void Fitted_prg_address_lines_mask_the_switchable_bank()
    {
        var cartridge = CreateCartridge(prgBanks: 8, chrBanks: 8);
        var cpu = CpuRomTarget(cartridge);

        cpu.Write!(0x2000, 0x0F);

        Assert.Equal((byte)0x0F, cartridge.PrgBankRegister);
        Assert.Equal(7, cartridge.SelectedPrgBank);
        Assert.Equal((byte)0x47, cpu.Read!(0x0000));
        Assert.Equal((byte)0x47, cpu.Read!(0x4000));
    }

    [Fact]
    public void B_through_E_registers_latch_only_the_five_chr_bank_output_bits()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);

        cpu.Write!(0x3000, 0xE3);
        cpu.Write!(0x4000, 0xE4);
        cpu.Write!(0x5000, 0xE5);
        cpu.Write!(0x6000, 0xE6);

        Assert.Equal(new byte[] { 0x03, 0x04, 0x05, 0x06 }, cartridge.ChrBankRegisters.ToArray());
        Assert.Equal(4UL, cartridge.MapperWriteCount);
    }

    [Fact]
    public void Low_pattern_fd_latch_decodes_the_full_mmc4_trigger_range()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuTarget(cartridge);
        cpu.Write!(0x3000, 0x03);
        cpu.Write!(0x4000, 0x04);

        Assert.Equal((byte)0x84, ppu.Read!(0x0FDF));
        Assert.Equal((byte)0xFD, cartridge.Latch0);
        Assert.Equal(3, cartridge.SelectedChrBank0);
        Assert.Equal((byte)0x83, ppu.Read!(0x0000));
        Assert.Equal(1UL, cartridge.Latch0FdTriggerCount);
    }

    [Fact]
    public void Low_pattern_fe_latch_decodes_the_full_mmc4_trigger_range()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuTarget(cartridge);
        cpu.Write!(0x3000, 0x03);
        cpu.Write!(0x4000, 0x04);
        _ = ppu.Read!(0x0FD9);

        Assert.Equal((byte)0x83, ppu.Read!(0x0FEF));
        Assert.Equal((byte)0xFE, cartridge.Latch0);
        Assert.Equal(4, cartridge.SelectedChrBank0);
        Assert.Equal((byte)0x84, ppu.Read!(0x0000));
        Assert.Equal(1UL, cartridge.Latch0FeTriggerCount);
    }

    [Fact]
    public void Mmc4_low_pattern_latch_accepts_addresses_that_mmc2_does_not()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var ppu = PpuTarget(cartridge);

        _ = ppu.Read!(0x0FD9);
        Assert.Equal((byte)0xFD, cartridge.Latch0);
        _ = ppu.Read!(0x0FEA);
        Assert.Equal((byte)0xFE, cartridge.Latch0);
        Assert.Equal(2UL, cartridge.LatchTriggerCount);
    }

    [Fact]
    public void High_pattern_fd_latch_decodes_the_full_trigger_range()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuTarget(cartridge);
        cpu.Write!(0x5000, 0x05);
        cpu.Write!(0x6000, 0x06);

        Assert.Equal((byte)0x86, ppu.Read!(0x1FDF));
        Assert.Equal((byte)0xFD, cartridge.Latch1);
        Assert.Equal(5, cartridge.SelectedChrBank1);
        Assert.Equal((byte)0x85, ppu.Read!(0x1000));
    }

    [Fact]
    public void High_pattern_fe_latch_decodes_the_full_trigger_range()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuTarget(cartridge);
        cpu.Write!(0x5000, 0x05);
        cpu.Write!(0x6000, 0x06);
        _ = ppu.Read!(0x1FD8);

        Assert.Equal((byte)0x85, ppu.Read!(0x1FEF));
        Assert.Equal((byte)0xFE, cartridge.Latch1);
        Assert.Equal(6, cartridge.SelectedChrBank1);
        Assert.Equal((byte)0x86, ppu.Read!(0x1000));
    }

    [Fact]
    public void Triggering_read_returns_old_chr_bank_before_latch_changes()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuTarget(cartridge);
        cpu.Write!(0x3000, 0x03);
        cpu.Write!(0x4000, 0x04);

        Assert.Equal((byte)0x84, ppu.Read!(0x0FD8));
        Assert.Equal((byte)0xFD, cartridge.Latch0);
        Assert.Equal((byte)0x83, ppu.Read!(0x0000));
    }

    [Fact]
    public void Raw_physical_ppu_trigger_holds_old_chr_data_for_the_triggering_read()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);
        cpu.Write!(0x3000, 0x03);
        cpu.Write!(0x4000, 0x04);

        var board = new VirtualHardwareBoard("MMC4.PHYSICAL.PPU.TEST");
        board.Add(cartridge);
        var vcc = AttachSource(board, "VCC", cartridge.Vcc);
        var gnd = AttachSource(board, "GND", cartridge.Gnd);
        var rd = AttachSource(board, "RD", cartridge.PpuReadBar);
        var wr = AttachSource(board, "WR", cartridge.PpuWriteBar);
        var address = cartridge.PpuAddress.Pins
            .Select((pin, bit) => AttachSource(board, $"PA{bit}", pin))
            .ToArray();
        _ = cartridge.PpuData.Pins
            .Select((pin, bit) => AttachSource(board, $"PD{bit}", pin))
            .ToArray();
        _ = new VirtualHardwareSimulator(board);

        gnd.Set(DigitalLevel.Low);
        vcc.Set(DigitalLevel.High);
        wr.Set(DigitalLevel.High);
        rd.Set(DigitalLevel.High);
        DriveAddress(address, 0x0FDF);
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
    public void Fixed_eight_kib_prg_ram_round_trips_independently_of_rom_banking()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var rom = CpuRomTarget(cartridge);
        var ram = CpuRamTarget(cartridge);

        ram.Write!(0x6123, 0xA5);
        rom.Write!(0x2000, 0x07);

        Assert.Equal((byte)0xA5, ram.Read!(0x6123));
        Assert.Equal(1UL, cartridge.PrgRamWriteCount);
        Assert.Equal(7, cartridge.SelectedPrgBank);
    }

    [Fact]
    public void F000_mirroring_register_is_live_dynamic_ciram_wiring_not_static_fold()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32,
            mirroring: VirtualHardwareNesMirroring.Vertical);
        var cpu = CpuRomTarget(cartridge);
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

        cpu.Write!(0x7000, 0x01);
        Assert.Equal(VirtualHardwareNesMirroring.Horizontal, cartridge.Mirroring);
        Assert.True(dynamic.TryEvaluateCompiledOutput(
            cartridge.CiramA10,
            pin => SamplePpuAddress(cartridge, pin, 0x0400),
            out var horizontal));
        Assert.Equal(DigitalLevel.Low, horizontal.Level);
    }

    [Fact]
    public void Writes_to_8000_9fff_do_not_alias_any_mmc4_register()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);

        cpu.Write!(0x0000, 0x09);
        cpu.Write!(0x1000, 0x0A);

        Assert.Equal((byte)0x00, cartridge.PrgBankRegister);
        Assert.All(cartridge.ChrBankRegisters, value => Assert.Equal((byte)0x00, value));
        Assert.Equal(0UL, cartridge.MapperWriteCount);
    }

    [Fact]
    public void Mmc4_has_no_cpu_rom_bus_conflict_and_latches_cpu_data_directly()
    {
        var prg = CreatePrg(16);
        prg[2 * 16 * 1024] = 0x00;
        var image = CreateImage(prg, CreateChr(32));
        var cartridge = new Mmc4Cartridge("TEST.MMC4.NO.CONFLICT");
        cartridge.LoadImage(image);
        var cpu = CpuRomTarget(cartridge);

        cpu.Write!(0x2000, 0x05);

        Assert.Equal((byte)0x05, cartridge.PrgBankRegister);
        Assert.Equal(5, cartridge.SelectedPrgBank);
    }

    [Fact]
    public void Compiled_mapper_and_prg_ram_writes_commit_at_completed_cpu_bus_phase()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);

        Assert.Equal(CompiledBusWritePhase.Complete, CpuRomTarget(cartridge).WritePhase);
        Assert.Equal(CompiledBusWritePhase.Complete, CpuRamTarget(cartridge).WritePhase);
    }

    [Fact]
    public void Generic_compiled_and_raw_physical_mmc4_execute_the_same_banking_ram_and_latch_program()
    {
        var image = CreateExecutionImage();
        var compiled = new RegionalNesVirtualMachine();
        var reference = new RegionalNesVirtualMachine();

        compiled.SetCompiledLabExecutionEnabled(true);
        compiled.InsertRom(image, "MMC4 execution (Japan).nes");
        reference.InsertRom(image, "MMC4 execution (Japan).nes");
        reference.Famicom.SetCompiledPhysicalMachineEnabled(false);

        compiled.PowerOn();
        reference.PowerOn();
        compiled.ReleaseReset();
        reference.ReleaseReset();

        const int masterCycles = 45_000;
        compiled.AdvanceMasterCycles(masterCycles);
        reference.AdvanceMasterCycles(masterCycles);

        var compiledCartridge = Assert.IsType<Mmc4Cartridge>(compiled.Slot.Cartridge);
        var referenceCartridge = Assert.IsType<Mmc4Cartridge>(reference.Slot.Cartridge);
        Assert.True(compiled.Famicom.CompiledLabMotherboardEnabled);
        Assert.Equal((byte)0x03, compiledCartridge.PrgBankRegister);
        Assert.Equal(new byte[] { 0x05, 0x06, 0x07, 0x08 }, compiledCartridge.ChrBankRegisters.ToArray());
        Assert.Equal(VirtualHardwareNesMirroring.Horizontal, compiledCartridge.Mirroring);
        Assert.Equal(1UL, compiledCartridge.PrgRamWriteCount);
        Assert.Equal(referenceCartridge.Latch0, compiledCartridge.Latch0);
        Assert.Equal(referenceCartridge.Latch1, compiledCartridge.Latch1);
        Assert.Equal(referenceCartridge.LatchTriggerCount, compiledCartridge.LatchTriggerCount);
        Assert.Equal(referenceCartridge.MapperWriteCount, compiledCartridge.MapperWriteCount);
        Assert.Equal(referenceCartridge.PrgRamWriteCount, compiledCartridge.PrgRamWriteCount);
        Assert.Equal(reference.Famicom.Cpu.ProgramCounter, compiled.Famicom.Cpu.ProgramCounter);
        Assert.Equal(reference.Famicom.Cpu.CompletedInstructionCount, compiled.Famicom.Cpu.CompletedInstructionCount);
        Assert.Equal(reference.Famicom.Ppu.InspectDiagnosticState(), compiled.Famicom.Ppu.InspectDiagnosticState());
        Assert.Equal(reference.Famicom.Ciram.InspectStateHash(), compiled.Famicom.Ciram.InspectStateHash());
    }

    [Fact]
    public void Legacy_ines_gets_the_physical_eight_kib_fxrom_prg_ram_window()
    {
        var image = CreateImage(CreatePrg(8), CreateChr(16),
            headerFormat: VirtualHardwareNesHeaderFormat.INes) with
        {
            PrgRamSizeBytes = 8 * 1024,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 0,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = false
        };
        var cartridge = new Mmc4Cartridge("TEST.MMC4.INES");

        cartridge.LoadImage(image);

        Assert.Equal(8 * 1024, cartridge.PrgRamSizeBytes);
        Assert.Equal(CompiledBusWritePhase.Complete, CpuRamTarget(cartridge).WritePhase);
    }

    [Fact]
    public void Unsupported_fxrom_ram_chr_four_screen_and_submapper_variants_require_distinct_hardware()
    {
        var baseImage = CreateImage(CreatePrg(16), CreateChr(32));
        var noPrgRam = baseImage with
        {
            PrgRamSizeBytes = 0,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 0,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        };
        var chrRam = baseImage with
        {
            PrgRamSizeBytes = 8 * 1024,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 8 * 1024,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        };
        var fourScreen = baseImage with { Mirroring = VirtualHardwareNesMirroring.FourScreen };
        var submapper = baseImage with { SubmapperNumber = 1 };
        var noChrRom = CreateImage(CreatePrg(16), []);

        Assert.Throws<NotSupportedException>(() => new Mmc4Cartridge("TEST.MMC4.NORAM").LoadImage(noPrgRam));
        Assert.Throws<NotSupportedException>(() => new Mmc4Cartridge("TEST.MMC4.CRAM").LoadImage(chrRam));
        Assert.Throws<NotSupportedException>(() => new Mmc4Cartridge("TEST.MMC4.4S").LoadImage(fourScreen));
        Assert.Throws<NotSupportedException>(() => new Mmc4Cartridge("TEST.MMC4.SUB").LoadImage(submapper));
        Assert.Throws<NotSupportedException>(() => new Mmc4Cartridge("TEST.MMC4.NOCHR").LoadImage(noChrRom));
    }

    [Fact]
    public void Factory_constructs_mapper_ten_as_replaceable_mmc4_hardware()
    {
        var image = CreateImage(CreatePrg(16), CreateChr(32));
        var cartridge = VirtualCartridgeHardwareFactory.Create(image);

        Assert.Equal(10, cartridge.MapperNumber);
        Assert.IsType<Mmc4Cartridge>(cartridge);
        Assert.True(cartridge.IsInserted);
    }

    private static Mmc4Cartridge CreateCartridge(
        int prgBanks,
        int chrBanks,
        VirtualHardwareNesMirroring mirroring = VirtualHardwareNesMirroring.Horizontal)
    {
        var cartridge = new Mmc4Cartridge("TEST.MMC4");
        cartridge.LoadImage(CreateImage(CreatePrg(prgBanks), CreateChr(chrBanks), mirroring: mirroring));
        return cartridge;
    }

    private static byte[] CreatePrg(int banks)
    {
        var prg = new byte[banks * 16 * 1024];
        for (var bank = 0; bank < banks; bank++)
            Array.Fill(prg, (byte)(0x40 + bank), bank * 16 * 1024, 16 * 1024);
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
        VirtualHardwareNesHeaderFormat headerFormat = VirtualHardwareNesHeaderFormat.Nes20,
        bool battery = false) =>
        new(
            headerFormat,
            MapperNumber: 10,
            SubmapperNumber: submapper,
            PrgRomSizeBytes: prg.Length,
            ChrRomSizeBytes: chr.Length,
            HasTrainer: false,
            HasBatteryBackedMemory: battery,
            mirroring,
            VirtualHardwareNesHeaderTiming.Ntsc,
            prg,
            chr)
        {
            PrgRamSizeBytes = battery ? 0 : 8 * 1024,
            PrgNvRamSizeBytes = battery ? 8 * 1024 : 0,
            ChrRamSizeBytes = 0,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        };

    private static VirtualHardwareNesRomImage CreateExecutionImage()
    {
        var prg = CreatePrg(16);
        var program = new List<byte>
        {
            0x78,                               // SEI
            0xA9, 0xA5, 0x8D, 0x23, 0x61,     // PRG RAM $6123 = $A5
            0xA9, 0x03, 0x8D, 0x00, 0xA0,     // PRG bank 3
            0xA9, 0x05, 0x8D, 0x00, 0xB0,     // CHR0 FD bank 5
            0xA9, 0x06, 0x8D, 0x00, 0xC0,     // CHR0 FE bank 6
            0xA9, 0x07, 0x8D, 0x00, 0xD0,     // CHR1 FD bank 7
            0xA9, 0x08, 0x8D, 0x00, 0xE0,     // CHR1 FE bank 8
            0xA9, 0x01, 0x8D, 0x00, 0xF0,     // horizontal mirroring
            0xAD, 0x02, 0x20                    // LDA $2002 - reset PPU address latch
        };
        AddPpuRead(program, 0x0FDF);
        AddPpuRead(program, 0x0FEF);
        AddPpuRead(program, 0x1FDF);
        AddPpuRead(program, 0x1FEF);
        var loopAddress = (ushort)(0xC000 + program.Count);
        program.Add(0x4C);
        program.Add((byte)(loopAddress & 0xFF));
        program.Add((byte)(loopAddress >> 8));

        var fixedLast = 15 * 16 * 1024;
        program.ToArray().CopyTo(prg, fixedLast);
        prg[fixedLast + 0x3FFA] = 0x00; prg[fixedLast + 0x3FFB] = 0xC0;
        prg[fixedLast + 0x3FFC] = 0x00; prg[fixedLast + 0x3FFD] = 0xC0;
        prg[fixedLast + 0x3FFE] = 0x00; prg[fixedLast + 0x3FFF] = 0xC0;

        return CreateImage(prg, CreateChr(32));
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

    private static CompiledBusTargetDescriptor CpuRomTarget(Mmc4Cartridge cartridge) =>
        Assert.Single(
            ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.AddressPins.Count == 15
                && target.ReadConditions.Any(condition => ReferenceEquals(condition.Pin, cartridge.CpuRomSelectBar)
                    && condition.RequiredLevel == DigitalLevel.Low));

    private static CompiledBusTargetDescriptor CpuRamTarget(Mmc4Cartridge cartridge) =>
        Assert.Single(
            ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.AddressPins.Count == 15
                && target.ReadConditions.Any(condition => ReferenceEquals(condition.Pin, cartridge.CpuRomSelectBar)
                    && condition.RequiredLevel == DigitalLevel.High));

    private static CompiledBusTargetDescriptor PpuTarget(Mmc4Cartridge cartridge) =>
        Assert.Single(
            ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.AddressPins.Count == 14);

    private static DigitalLevel SamplePpuAddress(Mmc4Cartridge cartridge, DigitalPin pin, ushort address)
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
