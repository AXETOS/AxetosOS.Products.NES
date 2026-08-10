using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Machines.Nes;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareMmc3CartridgeTests
{
    [Fact]
    public void Power_on_maps_two_switchable_and_two_fixed_prg_windows()
    {
        var cartridge = CreateCartridge(prgBanks: 8, chrBanks: 16);
        var cpu = CpuRomTarget(cartridge);

        Assert.Equal((byte)0x40, cpu.Read!(0x8000));
        Assert.Equal((byte)0x40, cpu.Read!(0xA000));
        Assert.Equal((byte)0x46, cpu.Read!(0xC000));
        Assert.Equal((byte)0x47, cpu.Read!(0xE000));
    }

    [Fact]
    public void Bank_select_and_data_drive_prg_windows_and_prg_mode()
    {
        var cartridge = CreateCartridge(prgBanks: 8, chrBanks: 16);
        var cpu = CpuRomTarget(cartridge);

        cpu.Write!(0x8000, 0x06);
        cpu.Write!(0x8001, 0x03);
        cpu.Write!(0x8000, 0x07);
        cpu.Write!(0x8001, 0x04);

        Assert.Equal((byte)0x43, cpu.Read!(0x8000));
        Assert.Equal((byte)0x44, cpu.Read!(0xA000));
        Assert.Equal((byte)0x46, cpu.Read!(0xC000));
        Assert.Equal((byte)0x47, cpu.Read!(0xE000));

        cpu.Write!(0x8000, 0x46);
        Assert.Equal((byte)0x46, cpu.Read!(0x8000));
        Assert.Equal((byte)0x44, cpu.Read!(0xA000));
        Assert.Equal((byte)0x43, cpu.Read!(0xC000));
        Assert.Equal((byte)0x47, cpu.Read!(0xE000));
    }

    [Fact]
    public void Chr_registers_expose_two_two_kib_and_four_one_kib_banks_in_both_modes()
    {
        var cartridge = CreateCartridge(prgBanks: 8, chrBanks: 16);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuPatternTarget(cartridge);

        WriteBank(cpu, 0, 2);
        WriteBank(cpu, 1, 4);
        WriteBank(cpu, 2, 6);
        WriteBank(cpu, 3, 7);
        WriteBank(cpu, 4, 8);
        WriteBank(cpu, 5, 9);

        Assert.Equal((byte)0x62, ppu.Read!(0x0000));
        Assert.Equal((byte)0x63, ppu.Read!(0x0400));
        Assert.Equal((byte)0x64, ppu.Read!(0x0800));
        Assert.Equal((byte)0x65, ppu.Read!(0x0C00));
        Assert.Equal((byte)0x66, ppu.Read!(0x1000));
        Assert.Equal((byte)0x67, ppu.Read!(0x1400));
        Assert.Equal((byte)0x68, ppu.Read!(0x1800));
        Assert.Equal((byte)0x69, ppu.Read!(0x1C00));

        cpu.Write!(0x8000, 0x80);
        Assert.Equal((byte)0x66, ppu.Read!(0x0000));
        Assert.Equal((byte)0x67, ppu.Read!(0x0400));
        Assert.Equal((byte)0x68, ppu.Read!(0x0800));
        Assert.Equal((byte)0x69, ppu.Read!(0x0C00));
        Assert.Equal((byte)0x62, ppu.Read!(0x1000));
        Assert.Equal((byte)0x63, ppu.Read!(0x1400));
        Assert.Equal((byte)0x64, ppu.Read!(0x1800));
        Assert.Equal((byte)0x65, ppu.Read!(0x1C00));
    }

    [Fact]
    public void Two_kib_chr_registers_ignore_the_low_bank_bit()
    {
        var cartridge = CreateCartridge(prgBanks: 8, chrBanks: 16);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuPatternTarget(cartridge);

        WriteBank(cpu, 0, 3);

        Assert.Equal((byte)0x02, cartridge.BankRegisters[0]);
        Assert.Equal((byte)0x62, ppu.Read!(0x0000));
        Assert.Equal((byte)0x63, ppu.Read!(0x0400));
    }

    [Fact]
    public void Mirroring_register_changes_live_ciram_a10_wiring()
    {
        var cartridge = CreateCartridge(prgBanks: 8, chrBanks: 16,
            mirroring: VirtualHardwareNesMirroring.Vertical);
        var cpu = CpuRomTarget(cartridge);
        var combinational = Assert.IsAssignableFrom<ICompiledCombinationalComponent>(cartridge);

        Assert.True(combinational.TryEvaluateCompiledOutput(
            cartridge.CiramA10,
            pin => SamplePpuAddress(cartridge, pin, 0x0400),
            out var vertical));
        Assert.Equal(DigitalLevel.High, vertical.Level);

        cpu.Write!(0xA000, 0x01);
        Assert.Equal(VirtualHardwareNesMirroring.Horizontal, cartridge.Mirroring);
        Assert.True(combinational.TryEvaluateCompiledOutput(
            cartridge.CiramA10,
            pin => SamplePpuAddress(cartridge, pin, 0x0400),
            out var horizontal));
        Assert.Equal(DigitalLevel.Low, horizontal.Level);
    }

    [Fact]
    public void Submapper_two_keeps_header_mirroring_hardwired()
    {
        var cartridge = CreateCartridge(prgBanks: 8, chrBanks: 16, submapper: 2,
            mirroring: VirtualHardwareNesMirroring.Vertical);
        var cpu = CpuRomTarget(cartridge);

        cpu.Write!(0xA000, 0x01);

        Assert.True(cartridge.HardwiredMirroring);
        Assert.Equal(VirtualHardwareNesMirroring.Vertical, cartridge.Mirroring);
        Assert.True(Assert.IsAssignableFrom<ICompiledStaticCombinationalComponent>(cartridge)
            .TryEvaluateCompiledStaticOutput(
                cartridge.CiramA10,
                pin => SamplePpuAddress(cartridge, pin, 0x0400),
                out var drive));
        Assert.Equal(DigitalLevel.High, drive.Level);
    }

    [Fact]
    public void Mmc3_prg_ram_enable_and_write_protect_are_package_owned()
    {
        var cartridge = CreateCartridge(prgBanks: 8, chrBanks: 16, prgRamSize: 8 * 1024);
        var rom = CpuRomTarget(cartridge);
        var ram = CpuRamTarget(cartridge);

        Assert.False(ram.IsSelected!(0x6000, false));
        rom.Write!(0xA001, 0x80);
        Assert.True(ram.IsSelected!(0x6000, false));
        Assert.True(ram.IsSelected!(0x6000, true));
        ram.Write!(0x6000, 0xA5);
        Assert.Equal((byte)0xA5, ram.Read!(0x6000));

        rom.Write!(0xA001, 0xC0);
        Assert.True(ram.IsSelected!(0x6000, false));
        Assert.False(ram.IsSelected!(0x6000, true));
        rom.Write!(0xA001, 0x00);
        Assert.False(ram.IsSelected!(0x6000, false));
    }

    [Fact]
    public void Mmc6_submapper_exposes_one_kib_internal_ram_and_split_read_write_enables()
    {
        var cartridge = CreateCartridge(prgBanks: 8, chrBanks: 16, submapper: 1, prgRamSize: 1024);
        var rom = CpuRomTarget(cartridge);
        var ram = CpuRamTarget(cartridge);

        Assert.True(cartridge.IsMmc6);
        Assert.Equal(1024, cartridge.PrgRamSizeBytes);
        rom.Write!(0x8000, 0x20); // global MMC6 RAM enable
        rom.Write!(0xA001, 0x30); // low 512 B read+write

        Assert.True(ram.IsSelected!(0x7000, false));
        Assert.True(ram.IsSelected!(0x7000, true));
        ram.Write!(0x7000, 0x5A);
        Assert.Equal((byte)0x5A, ram.Read!(0x7000));
        Assert.Equal((byte)0x00, ram.Read!(0x7200));

        rom.Write!(0x8000, 0x00);
        Assert.False(ram.IsSelected!(0x7000, false));
        Assert.Equal((byte)0x00, cartridge.PrgRamProtectRegister);
    }

    [Fact]
    public void Filtered_a12_rises_clock_irq_counter_and_e000_acknowledges_irq()
    {
        var cartridge = CreateCartridge(prgBanks: 8, chrBanks: 16);
        var cpu = CpuRomTarget(cartridge);
        var observer = PpuAddressObserver(cartridge);

        cpu.Write!(0xC000, 0x02);
        cpu.Write!(0xC001, 0x00);
        cpu.Write!(0xE001, 0x00);

        ClockQualifiedA12Rise(cpu, observer);
        Assert.Equal((byte)0x02, cartridge.IrqCounter);
        Assert.False(cartridge.IrqAsserted);
        ClockQualifiedA12Rise(cpu, observer);
        Assert.Equal((byte)0x01, cartridge.IrqCounter);
        Assert.False(cartridge.IrqAsserted);
        ClockQualifiedA12Rise(cpu, observer);
        Assert.Equal((byte)0x00, cartridge.IrqCounter);
        Assert.True(cartridge.IrqAsserted);
        Assert.Equal(DigitalLevel.Low, cartridge.IrqBar.DriveLevel);

        cpu.Write!(0xE000, 0x00);
        Assert.False(cartridge.IrqEnabled);
        Assert.False(cartridge.IrqAsserted);
        Assert.Equal(DigitalLevel.HighImpedance, cartridge.IrqBar.DriveLevel);
    }

    [Fact]
    public void Old_irq_revision_does_not_assert_when_zero_is_reloaded_from_zero()
    {
        var cartridge = CreateCartridge(prgBanks: 8, chrBanks: 16, submapper: 4);
        var cpu = CpuRomTarget(cartridge);
        var observer = PpuAddressObserver(cartridge);

        cpu.Write!(0xC000, 0x00);
        cpu.Write!(0xC001, 0x00);
        cpu.Write!(0xE001, 0x00);
        ClockQualifiedA12Rise(cpu, observer);

        Assert.True(cartridge.OldIrqRevision);
        Assert.Equal((byte)0x00, cartridge.IrqCounter);
        Assert.False(cartridge.IrqAsserted);
    }

    [Fact]
    public void Ppu_address_observer_is_data_bus_neutral_and_sees_every_compiled_read_address()
    {
        var cartridge = CreateCartridge(prgBanks: 8, chrBanks: 16);
        var observer = PpuAddressObserver(cartridge);

        Assert.Null(observer.Read);
        Assert.NotNull(observer.ObserveReadBegin);
        Assert.NotNull(observer.Write);
        Assert.Empty(observer.ReadConditions);
        Assert.Empty(observer.WriteConditions);
    }

    [Fact]
    public void Chr_ram_variant_is_banked_and_writable()
    {
        var cartridge = CreateCartridge(prgBanks: 8, chrBanks: 0, chrRamSize: 8 * 1024);
        var ppu = PpuPatternTarget(cartridge);

        Assert.True(cartridge.IsChrRam);
        Assert.NotNull(ppu.Write);
        ppu.Write!(0x0123, 0x7B);
        Assert.Equal((byte)0x7B, ppu.Read!(0x0123));
        Assert.Equal(1UL, cartridge.PpuWriteCount);
    }

    [Fact]
    public void Four_screen_variant_owns_cartridge_nametable_ram_and_disables_ciram()
    {
        var cartridge = CreateCartridge(prgBanks: 8, chrBanks: 16,
            mirroring: VirtualHardwareNesMirroring.FourScreen);
        var targets = ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets().ToArray();
        var nametable = targets.Single(target => target.Read is not null
            && target.AddressPins.Count == 14
            && target.ReadConditions.Any(condition => ReferenceEquals(condition.Pin, cartridge.PpuAddress.Pins[13])
                && condition.RequiredLevel == DigitalLevel.High));

        nametable.Write!(0x2400, 0xCC);
        Assert.Equal((byte)0xCC, nametable.Read!(0x2400));
        Assert.True(cartridge.HasFourScreenRam);
        Assert.True(Assert.IsAssignableFrom<ICompiledStaticCombinationalComponent>(cartridge)
            .TryEvaluateCompiledStaticOutput(cartridge.CiramChipEnableBar, _ => DigitalLevel.Low, out var ce));
        Assert.Equal(DigitalLevel.High, ce.Level);
    }

    [Fact]
    public void Generic_compiled_and_raw_physical_mmc3_execute_the_same_prg_bank_switch_program()
    {
        var image = CreateExecutionImage();
        var compiled = new RegionalNesVirtualMachine();
        var reference = new RegionalNesVirtualMachine();

        compiled.SetCompiledLabExecutionEnabled(true);
        compiled.InsertRom(image, "MMC3 execution (Japan).nes");
        reference.InsertRom(image, "MMC3 execution (Japan).nes");
        reference.Famicom.SetCompiledPhysicalMachineEnabled(false);

        compiled.PowerOn();
        reference.PowerOn();
        compiled.ReleaseReset();
        reference.ReleaseReset();

        const int masterCycles = 20_000;
        compiled.AdvanceMasterCycles(masterCycles);
        reference.AdvanceMasterCycles(masterCycles);

        var compiledCartridge = Assert.IsType<Mmc3Cartridge>(compiled.Slot.Cartridge);
        var referenceCartridge = Assert.IsType<Mmc3Cartridge>(reference.Slot.Cartridge);
        Assert.True(compiled.Famicom.CompiledLabMotherboardEnabled);
        Assert.Equal((byte)0x01, compiledCartridge.BankRegisters[6]);
        Assert.Equal(referenceCartridge.BankRegisters[6], compiledCartridge.BankRegisters[6]);
        Assert.Equal(referenceCartridge.MapperWriteCount, compiledCartridge.MapperWriteCount);
        Assert.Equal(reference.Famicom.Cpu.ProgramCounter, compiled.Famicom.Cpu.ProgramCounter);
        Assert.Equal(reference.Famicom.Cpu.CompletedInstructionCount, compiled.Famicom.Cpu.CompletedInstructionCount);
        Assert.Equal(reference.Famicom.Ppu.InspectDiagnosticState(), compiled.Famicom.Ppu.InspectDiagnosticState());
        Assert.Equal(reference.Famicom.Ciram.InspectStateHash(), compiled.Famicom.Ciram.InspectStateHash());
    }

    [Fact]
    public void Compiled_ppu_address_observer_clocks_live_mmc3_irq_hardware_during_rendering()
    {
        var machine = new RegionalNesVirtualMachine();
        machine.SetCompiledLabExecutionEnabled(true);
        machine.InsertRom(CreateIrqExecutionImage(), "MMC3 IRQ execution (Japan).nes");
        machine.PowerOn();
        machine.ReleaseReset();

        machine.AdvanceMasterCycles(500_000);

        var cartridge = Assert.IsType<Mmc3Cartridge>(machine.Slot.Cartridge);
        Assert.True(machine.Famicom.CompiledLabMotherboardEnabled);
        Assert.True(machine.Famicom.Ppu.InspectDiagnosticState().BackgroundPatternFetches > 0);
        Assert.True(cartridge.QualifiedA12RiseCount > 0);
        Assert.True(cartridge.IrqAssertCount > 0);
        Assert.True(cartridge.IrqAsserted);
    }

    [Fact]
    public void Unsupported_mmc3_family_submappers_require_distinct_external_hardware()
    {
        var mcAcc = CreateImage(CreatePrg(8), CreateChr(16), submapper: 3);
        var scrambled = CreateImage(CreatePrg(8), CreateChr(16), submapper: 5);

        Assert.Throws<NotSupportedException>(() => new Mmc3Cartridge("TEST.MMC3.MCACC").LoadImage(mcAcc));
        Assert.Throws<NotSupportedException>(() => new Mmc3Cartridge("TEST.MMC3.T9552").LoadImage(scrambled));
    }

    [Fact]
    public void Factory_constructs_mapper_four_as_replaceable_mmc3_hardware()
    {
        var image = CreateImage(CreatePrg(8), CreateChr(16), submapper: 0);
        var cartridge = VirtualCartridgeHardwareFactory.Create(image);

        Assert.Equal(4, cartridge.MapperNumber);
        Assert.IsType<Mmc3Cartridge>(cartridge);
        Assert.True(cartridge.IsInserted);
    }

    private static Mmc3Cartridge CreateCartridge(
        int prgBanks,
        int chrBanks,
        int submapper = 0,
        int prgRamSize = 0,
        int chrRamSize = 0,
        VirtualHardwareNesMirroring mirroring = VirtualHardwareNesMirroring.Horizontal)
    {
        var image = CreateImage(CreatePrg(prgBanks), chrBanks == 0 ? [] : CreateChr(chrBanks), submapper, mirroring) with
        {
            PrgRamSizeBytes = prgRamSize,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = chrRamSize,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        };
        var cartridge = new Mmc3Cartridge("TEST.MMC3");
        cartridge.LoadImage(image);
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
        var chr = new byte[banks * 1024];
        for (var bank = 0; bank < banks; bank++)
            Array.Fill(chr, (byte)(0x60 + bank), bank * 1024, 1024);
        return chr;
    }

    private static VirtualHardwareNesRomImage CreateImage(
        byte[] prg,
        byte[] chr,
        int submapper,
        VirtualHardwareNesMirroring mirroring = VirtualHardwareNesMirroring.Horizontal) =>
        new(
            VirtualHardwareNesHeaderFormat.Nes20,
            MapperNumber: 4,
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
        var prg = CreatePrg(8);
        var fixedLast = 7 * 8 * 1024;
        var program = new byte[]
        {
            0x78,                   // SEI
            0xA9, 0x06,             // LDA #$06 - select R6
            0x8D, 0x00, 0x80,       // STA $8000
            0xA9, 0x01,             // LDA #$01
            0x8D, 0x01, 0x80,       // STA $8001 - bank 1 at $8000
            0x4C, 0x0B, 0xE0        // JMP $E00B
        };
        program.CopyTo(prg, fixedLast);
        prg[fixedLast + 0x1FFA] = 0x00; prg[fixedLast + 0x1FFB] = 0xE0;
        prg[fixedLast + 0x1FFC] = 0x00; prg[fixedLast + 0x1FFD] = 0xE0;
        prg[fixedLast + 0x1FFE] = 0x00; prg[fixedLast + 0x1FFF] = 0xE0;

        return CreateImage(prg, CreateChr(16), submapper: 0) with
        {
            PrgRamSizeBytes = 0,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 0,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        };
    }

    private static VirtualHardwareNesRomImage CreateIrqExecutionImage()
    {
        var prg = CreatePrg(8);
        var fixedLast = 7 * 8 * 1024;
        var program = new byte[]
        {
            0x78,                         // SEI - leave CPU IRQ masked so line remains observable
            0xA9, 0x08,                   // sprite patterns at $1000, background at $0000
            0x8D, 0x00, 0x20,             // STA $2000
            0xA9, 0x18,                   // background + sprite rendering
            0x8D, 0x01, 0x20,             // STA $2001
            0xA9, 0x02,
            0x8D, 0x00, 0xC0,             // IRQ latch = 2
            0xA9, 0x00,
            0x8D, 0x01, 0xC0,             // request reload
            0x8D, 0x01, 0xE0,             // enable IRQ
            0x4C, 0x18, 0xE0              // JMP $E018
        };
        program.CopyTo(prg, fixedLast);
        prg[fixedLast + 0x1FFA] = 0x00; prg[fixedLast + 0x1FFB] = 0xE0;
        prg[fixedLast + 0x1FFC] = 0x00; prg[fixedLast + 0x1FFD] = 0xE0;
        prg[fixedLast + 0x1FFE] = 0x00; prg[fixedLast + 0x1FFF] = 0xE0;

        return CreateImage(prg, CreateChr(16), submapper: 0) with
        {
            PrgRamSizeBytes = 0,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 0,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = true
        };
    }

    private static void WriteBank(CompiledBusTargetDescriptor cpu, byte register, byte value)
    {
        cpu.Write!(0x8000, register);
        cpu.Write!(0x8001, value);
    }

    private static void ClockQualifiedA12Rise(CompiledBusTargetDescriptor cpu, CompiledBusTargetDescriptor observer)
    {
        observer.ObserveReadBegin!(0x0000);
        cpu.ObserveBusCycle!(false);
        cpu.ObserveBusCycle!(false);
        cpu.ObserveBusCycle!(false);
        observer.ObserveReadBegin!(0x1000);
    }

    private static CompiledBusTargetDescriptor CpuRomTarget(Mmc3Cartridge cartridge) =>
        ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets()
            .Single(target => target.AddressPins.Count == 15
                && target.ReadConditions.Any(condition => ReferenceEquals(condition.Pin, cartridge.CpuRomSelectBar)
                    && condition.RequiredLevel == DigitalLevel.Low));

    private static CompiledBusTargetDescriptor CpuRamTarget(Mmc3Cartridge cartridge) =>
        ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets()
            .Single(target => target.AddressPins.Count == 15
                && target.ReadConditions.Any(condition => ReferenceEquals(condition.Pin, cartridge.CpuRomSelectBar)
                    && condition.RequiredLevel == DigitalLevel.High));

    private static CompiledBusTargetDescriptor PpuPatternTarget(Mmc3Cartridge cartridge) =>
        ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets()
            .Single(target => target.AddressPins.Count == 14 && target.Read is not null
                && target.ReadConditions.Any(condition => ReferenceEquals(condition.Pin, cartridge.PpuAddress.Pins[13])
                    && condition.RequiredLevel == DigitalLevel.Low));

    private static CompiledBusTargetDescriptor PpuAddressObserver(Mmc3Cartridge cartridge) =>
        ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets()
            .Single(target => target.AddressPins.Count == 14 && target.Read is null && target.ObserveReadBegin is not null);

    private static DigitalLevel SamplePpuAddress(Mmc3Cartridge cartridge, DigitalPin pin, ushort address)
    {
        for (var bit = 0; bit < cartridge.PpuAddress.Width; bit++)
        {
            if (!ReferenceEquals(pin, cartridge.PpuAddress.Pins[bit])) continue;
            return (address & (1 << bit)) != 0 ? DigitalLevel.High : DigitalLevel.Low;
        }
        return DigitalLevel.Unknown;
    }
}
