using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareMmc1CartridgeTests
{
    [Fact]
    public void Power_on_state_fixes_last_prg_bank_and_exposes_switchable_lower_bank()
    {
        var cartridge = CreateCartridge(prgBanks: 4, chrBanks4K: 2);
        var cpuRom = CpuRomTarget(cartridge);

        Assert.Equal((byte)0x40, cpuRom.Read!(0x8000));
        Assert.Equal((byte)0x43, cpuRom.Read!(0xC000));

        WriteSerial(cpuRom, 0xE000, 0x02);

        Assert.Equal((byte)0x42, cpuRom.Read!(0x8000));
        Assert.Equal((byte)0x43, cpuRom.Read!(0xC000));
        Assert.Equal((byte)0x02, cartridge.PrgBankRegister);
        Assert.Equal(5UL, cartridge.MapperWriteCount);
    }

    [Fact]
    public void Serial_reset_discards_partial_load_and_forces_fixed_last_prg_mode()
    {
        var cartridge = CreateCartridge(prgBanks: 4, chrBanks4K: 2);
        var cpuRom = CpuRomTarget(cartridge);

        cpuRom.Write!(0x8000, 0x01);
        cpuRom.Write!(0x8000, 0x00);
        Assert.NotEqual(0x10, cartridge.SerialShiftRegister);

        cpuRom.Write!(0x8000, 0x80);

        Assert.Equal((byte)0x10, cartridge.SerialShiftRegister);
        Assert.Equal((byte)0x0C, (byte)(cartridge.ControlRegister & 0x0C));
        Assert.Equal((byte)0x43, cpuRom.Read!(0xC000));
    }

    [Fact]
    public void Control_register_selects_two_independent_four_kib_chr_banks()
    {
        var cartridge = CreateCartridge(prgBanks: 4, chrBanks4K: 4);
        var cpuRom = CpuRomTarget(cartridge);
        var ppuChr = PpuChrTarget(cartridge);

        WriteSerial(cpuRom, 0x8000, 0x1C); // 4 KiB CHR mode + fixed-last PRG mode.
        WriteSerial(cpuRom, 0xA000, 0x02);
        WriteSerial(cpuRom, 0xC000, 0x03);

        Assert.Equal((byte)0x52, ppuChr.Read!(0x0000));
        Assert.Equal((byte)0x53, ppuChr.Read!(0x1000));
        Assert.Equal((byte)0x02, cartridge.ChrBank0Register);
        Assert.Equal((byte)0x03, cartridge.ChrBank1Register);
    }

    [Fact]
    public void Compiled_ppu_target_observes_read_assertion_separately_from_data_resolution()
    {
        var cartridge = CreateCartridge(prgBanks: 4, chrBanks4K: 4);
        var ppuChr = PpuChrTarget(cartridge);
        var observeReadBegin = Assert.IsType<Action<int>>(ppuChr.ObserveReadBegin);

        var before = cartridge.PpuReadCount;
        observeReadBegin(0x1000);
        Assert.Equal(before + 1, cartridge.PpuReadCount);

        _ = ppuChr.Read!(0x1000);
        Assert.Equal(before + 1, cartridge.PpuReadCount);
    }

    [Fact]
    public void Prg_ram_is_cartridge_local_and_round_trips_through_its_compiled_bus_facet()
    {
        var cartridge = CreateCartridge(prgBanks: 2, chrBanks4K: 2);
        var prgRam = CpuRamTarget(cartridge);

        prgRam.Write!(0x6000, 0xA5);
        prgRam.Write!(0x7FFF, 0x5A);

        Assert.Equal((byte)0xA5, prgRam.Read!(0x6000));
        Assert.Equal((byte)0x5A, prgRam.Read!(0x7FFF));
    }


    [Fact]
    public void Nes20_zero_prg_ram_description_builds_no_cpu_ram_hardware_target()
    {
        var cartridge = CreateCartridge(prgBanks: 4, chrBanks4K: 4, prgRamSizeBytes: 0);
        var cpuTargets = ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets()
            .Where(target => target.AddressPins.Count == 15)
            .ToArray();

        Assert.Equal(0, cartridge.PrgRamSizeBytes);
        Assert.False(cartridge.PrgRamEnabled);
        Assert.Single(cpuTargets);
    }

    [Fact]
    public void Existing_prg_ram_chip_enable_is_mapper_local_and_dynamic_in_compiled_facet()
    {
        var cartridge = CreateCartridge(prgBanks: 4, chrBanks4K: 4, prgRamSizeBytes: 8 * 1024);
        var cpuRom = CpuRomTarget(cartridge);
        var prgRam = CpuRamTarget(cartridge);
        var isSelected = Assert.IsType<Func<int, bool, bool>>(prgRam.IsSelected);

        Assert.Equal(8 * 1024, cartridge.PrgRamSizeBytes);
        Assert.True(cartridge.PrgRamEnabled);
        Assert.True(isSelected(0x6000, false));

        WriteSerial(cpuRom, 0xE000, 0x10);

        Assert.False(cartridge.PrgRamEnabled);
        Assert.False(isSelected(0x6000, false));
        Assert.False(isSelected(0x6000, true));

        WriteSerial(cpuRom, 0xE000, 0x00);

        Assert.True(cartridge.PrgRamEnabled);
        Assert.True(isSelected(0x6000, false));
    }

    [Fact]
    public void Cpu_connector_exposes_A0_through_A14_plus_M2_and_romsel_not_A15()
    {
        var cartridge = CreateCartridge(prgBanks: 4, chrBanks4K: 4);

        Assert.Equal(15, cartridge.CpuAddress.Width);
        Assert.EndsWith(".CPU.A14", cartridge.CpuAddress.Pins[^1].Name);
        Assert.EndsWith(".CPU.M2", cartridge.CpuM2.Name);
        Assert.EndsWith(".CPU.ROMSEL_BAR", cartridge.CpuRomSelectBar.Name);
        Assert.DoesNotContain(cartridge.Pins, pin => pin.Name.EndsWith(".CPU.A15", StringComparison.Ordinal));
    }

    [Fact]
    public void Compiled_prg_ram_window_is_M2_qualified_and_requires_inactive_romsel_A14_A13()
    {
        var cartridge = CreateCartridge(prgBanks: 4, chrBanks4K: 4, prgRamSizeBytes: 8 * 1024);
        var prgRam = CpuRamTarget(cartridge);

        Assert.Contains(prgRam.ReadConditions, condition => ReferenceEquals(condition.Pin, cartridge.CpuM2)
            && condition.RequiredLevel == DigitalLevel.High);
        Assert.Contains(prgRam.ReadConditions, condition => ReferenceEquals(condition.Pin, cartridge.CpuRomSelectBar)
            && condition.RequiredLevel == DigitalLevel.High);
        Assert.Contains(prgRam.ReadConditions, condition => ReferenceEquals(condition.Pin, cartridge.CpuAddress.Pins[14])
            && condition.RequiredLevel == DigitalLevel.High);
        Assert.Contains(prgRam.ReadConditions, condition => ReferenceEquals(condition.Pin, cartridge.CpuAddress.Pins[13])
            && condition.RequiredLevel == DigitalLevel.High);
    }

    [Theory]
    [InlineData(0x00, 0x0000, DigitalLevel.Low)]
    [InlineData(0x01, 0x0000, DigitalLevel.High)]
    [InlineData(0x02, 0x0400, DigitalLevel.High)]
    [InlineData(0x02, 0x0800, DigitalLevel.Low)]
    [InlineData(0x03, 0x0400, DigitalLevel.Low)]
    [InlineData(0x03, 0x0800, DigitalLevel.High)]
    public void Mirroring_is_mapper_local_ciram_a10_circuitry(byte mode, ushort ppuAddress, DigitalLevel expected)
    {
        var cartridge = CreateCartridge(prgBanks: 2, chrBanks4K: 2);
        var cpuRom = CpuRomTarget(cartridge);
        WriteSerial(cpuRom, 0x8000, (byte)(0x0C | mode));

        var combinational = (ICompiledCombinationalComponent)cartridge;
        var evaluated = combinational.TryEvaluateCompiledOutput(
            cartridge.CiramA10,
            pin => SamplePpuAddress(cartridge, pin, ppuAddress),
            out var drive);

        Assert.True(evaluated);
        Assert.Equal(expected, drive.Level);
    }


    [Fact]
    public void Physical_cartridge_connector_keeps_ppu_address_and_data_on_separate_buses()
    {
        var cartridge = CreateCartridge(prgBanks: 2, chrBanks4K: 2);
        var board = new VirtualHardwareBoard("MMC1.PPU.CONNECTOR.TEST");
        board.Add(cartridge);

        var vcc = AttachSource(board, "VCC", cartridge.Vcc);
        var gnd = AttachSource(board, "GND", cartridge.Gnd);
        var readBar = AttachSource(board, "RD", cartridge.PpuReadBar);
        var writeBar = AttachSource(board, "WR", cartridge.PpuWriteBar);
        var address = cartridge.PpuAddress.Pins
            .Select((pin, bit) => AttachSource(board, $"A{bit}", pin))
            .ToArray();
        var data = cartridge.PpuData.Pins
            .Select((pin, bit) => AttachSource(board, $"D{bit}", pin))
            .ToArray();

        _ = new VirtualHardwareSimulator(board);

        DriveAddress(address, 0x0000);
        DriveByte(data, null);
        readBar.Set(DigitalLevel.High);
        writeBar.Set(DigitalLevel.High);
        gnd.Set(DigitalLevel.Low);
        vcc.Set(DigitalLevel.High);

        readBar.Set(DigitalLevel.Low);
        Assert.True(cartridge.PpuData.TrySample(out var chrData));
        Assert.Equal(0x50UL, chrData);
        Assert.True(cartridge.PpuAddress.TrySample(out var addressWhileReading));
        Assert.Equal(0x0000UL, addressWhileReading);

        // Changing A0-A13 is independent of the CHR data bus. There is no
        // cartridge-local ALE latch and no address/data contention.
        DriveAddress(address, 0x0001);
        Assert.True(cartridge.PpuAddress.TrySample(out var nextAddress));
        Assert.Equal(0x0001UL, nextAddress);
        Assert.True(cartridge.PpuData.TrySample(out var nextData));
        Assert.Equal(0x50UL, nextData);
        Assert.DoesNotContain(cartridge.PpuData.Pins, pin => pin.SampledLevel == DigitalLevel.Contention);
    }


    [Fact]
    public void Active_physical_chr_read_tracks_bank_output_change_without_a_new_ppu_edge()
    {
        var cartridge = CreateCartridge(prgBanks: 4, chrBanks4K: 4);
        var cpuRom = CpuRomTarget(cartridge);
        WriteSerial(cpuRom, 0x8000, 0x1C); // independent 4 KiB CHR banks
        WriteSerial(cpuRom, 0xC000, 0x01);

        var board = new VirtualHardwareBoard("MMC1.ACTIVE.CHR.REMAP.TEST");
        board.Add(cartridge);
        var vcc = AttachSource(board, "ACTIVE.VCC", cartridge.Vcc);
        var gnd = AttachSource(board, "ACTIVE.GND", cartridge.Gnd);
        var readBar = AttachSource(board, "ACTIVE.RD", cartridge.PpuReadBar);
        var writeBar = AttachSource(board, "ACTIVE.WR", cartridge.PpuWriteBar);
        var address = cartridge.PpuAddress.Pins
            .Select((pin, bit) => AttachSource(board, $"ACTIVE.A{bit}", pin))
            .ToArray();
        var data = cartridge.PpuData.Pins
            .Select((pin, bit) => AttachSource(board, $"ACTIVE.D{bit}", pin))
            .ToArray();
        _ = new VirtualHardwareSimulator(board);

        DriveAddress(address, 0x1000);
        DriveByte(data, null);
        readBar.Set(DigitalLevel.High);
        writeBar.Set(DigitalLevel.High);
        gnd.Set(DigitalLevel.Low);
        vcc.Set(DigitalLevel.High);

        // Assert /RD and leave it active. Bank 1 currently selects 4 KiB bank 1.
        readBar.Set(DigitalLevel.Low);
        Assert.True(cartridge.PpuData.TrySample(out var before));
        Assert.Equal(0x51UL, before);
        var readsBeforeRemap = cartridge.PpuReadCount;

        // A completed MMC1 register load changes the mapper's CHR address
        // outputs combinationally. No PPU address or /RD transition is required
        // for the selected ROM byte to change while the read window remains open.
        WriteSerial(cpuRom, 0xC000, 0x03);

        Assert.True(cartridge.PpuData.TrySample(out var after));
        Assert.Equal(0x53UL, after);
        Assert.Equal(readsBeforeRemap, cartridge.PpuReadCount);
    }

    [Fact]
    public void Diagnostic_mapping_reports_selected_chr_bank_and_physical_ciram_page()
    {
        var cartridge = CreateCartridge(prgBanks: 4, chrBanks4K: 8);
        var cpuRom = CpuRomTarget(cartridge);

        WriteSerial(cpuRom, 0x8000, 0x1E); // vertical mirroring + 4 KiB CHR + fixed-last PRG
        WriteSerial(cpuRom, 0xA000, 0x02);
        WriteSerial(cpuRom, 0xC000, 0x03);

        var lowPattern = cartridge.InspectPpuMapping(0x0123);
        var highPattern = cartridge.InspectPpuMapping(0x1456);
        var nametable0 = cartridge.InspectPpuMapping(0x2000);
        var nametable1 = cartridge.InspectPpuMapping(0x2400);
        var nametable2 = cartridge.InspectPpuMapping(0x2800);
        var nametable3 = cartridge.InspectPpuMapping(0x2C00);

        Assert.Equal(Mmc1PpuMappingKind.Chr, lowPattern.Kind);
        Assert.Equal(0x2123, lowPattern.PhysicalAddress);
        Assert.Equal(2, lowPattern.Bank4K);
        Assert.Equal(Mmc1PpuMappingKind.Chr, highPattern.Kind);
        Assert.Equal(0x3456, highPattern.PhysicalAddress);
        Assert.Equal(3, highPattern.Bank4K);

        Assert.Equal((0, 0x000), (nametable0.CiramPage, nametable0.PhysicalAddress));
        Assert.Equal((1, 0x400), (nametable1.CiramPage, nametable1.PhysicalAddress));
        Assert.Equal((0, 0x000), (nametable2.CiramPage, nametable2.PhysicalAddress));
        Assert.Equal((1, 0x400), (nametable3.CiramPage, nametable3.PhysicalAddress));
    }

    [Fact]
    public void Register_diagnostic_output_reports_only_reset_or_completed_serial_register_actions()
    {
        var cartridge = CreateCartridge(prgBanks: 4, chrBanks4K: 4);
        var cpuRom = CpuRomTarget(cartridge);
        cartridge.RegisterTraceOutput.SetCaptureEnabled(true);

        for (var bit = 0; bit < 4; bit++)
            cpuRom.Write!(0xA000, (byte)((0x03 >> bit) & 1));
        Assert.Equal(0, cartridge.RegisterTraceOutput.CapturedCount);

        cpuRom.Write!(0xA000, (byte)((0x03 >> 4) & 1));
        var commit = Assert.Single(cartridge.RegisterTraceOutput.Drain());
        Assert.Equal(Mmc1RegisterOperation.ChrBank0, commit.Operation);
        Assert.Equal((byte)0x03, commit.ChrBank0);
        Assert.Equal(5UL, commit.MapperWriteCount);
        Assert.Equal(cartridge.PpuReadCount, commit.PpuReadCountAtCommit);
        Assert.Equal(cartridge.PpuWriteCount, commit.PpuWriteCountAtCommit);

        cpuRom.Write!(0x8000, 0x80);
        var reset = Assert.Single(cartridge.RegisterTraceOutput.Drain());
        Assert.Equal(Mmc1RegisterOperation.Reset, reset.Operation);
        Assert.Equal((byte)0x0C, (byte)(reset.Control & 0x0C));
    }

    [Fact]
    public void Compiled_cpu_mapper_target_latches_writes_at_bus_cycle_completion()
    {
        var cartridge = CreateCartridge(prgBanks: 4, chrBanks4K: 8);
        var cpuRom = CpuRomTarget(cartridge);

        Assert.Equal(CompiledBusWritePhase.Complete, cpuRom.WritePhase);
    }

    [Fact]
    public void Consecutive_cpu_write_cycle_suppresses_second_serial_bit_but_never_bit7_reset()
    {
        var cartridge = CreateCartridge(prgBanks: 4, chrBanks4K: 2);
        var cpuRom = CpuRomTarget(cartridge);
        var observe = Assert.IsType<Action<bool>>(cpuRom.ObserveBusCycle);

        observe(false);
        observe(true);
        cpuRom.Write!(0xE000, 0x00);
        Assert.Equal((byte)0x08, cartridge.SerialShiftRegister);

        observe(true);
        cpuRom.Write!(0xE000, 0x01);
        Assert.Equal((byte)0x08, cartridge.SerialShiftRegister);
        Assert.Equal(1UL, cartridge.IgnoredConsecutiveMapperWriteCount);

        // Reset remains active on a consecutive write, matching the MMC1 pin
        // behavior required by RMW sequences whose second write sets bit 7.
        observe(true);
        cpuRom.Write!(0xE000, 0x80);
        Assert.Equal((byte)0x10, cartridge.SerialShiftRegister);
        Assert.Equal(1UL, cartridge.IgnoredConsecutiveMapperWriteCount);
    }

    private static Mmc1Cartridge CreateCartridge(int prgBanks, int chrBanks4K, int? prgRamSizeBytes = null)
    {
        var prg = new byte[prgBanks * 16 * 1024];
        for (var bank = 0; bank < prgBanks; bank++)
            Array.Fill(prg, (byte)(0x40 + bank), bank * 16 * 1024, 16 * 1024);

        var chr = new byte[chrBanks4K * 4 * 1024];
        for (var bank = 0; bank < chrBanks4K; bank++)
            Array.Fill(chr, (byte)(0x50 + bank), bank * 4 * 1024, 4 * 1024);

        var cartridge = new Mmc1Cartridge("TEST.MMC1");
        var image = new VirtualHardwareNesRomImage(
            prgRamSizeBytes.HasValue ? VirtualHardwareNesHeaderFormat.Nes20 : VirtualHardwareNesHeaderFormat.INes,
            MapperNumber: 1,
            SubmapperNumber: prgRamSizeBytes.HasValue ? 0 : null,
            PrgRomSizeBytes: prg.Length,
            ChrRomSizeBytes: chr.Length,
            HasTrainer: false,
            HasBatteryBackedMemory: false,
            VirtualHardwareNesMirroring.Horizontal,
            VirtualHardwareNesHeaderTiming.Unknown,
            prg,
            chr);
        if (prgRamSizeBytes.HasValue)
        {
            image = image with
            {
                PrgRamSizeBytes = prgRamSizeBytes.Value,
                PrgNvRamSizeBytes = 0,
                ChrRamSizeBytes = 0,
                ChrNvRamSizeBytes = 0,
                HasExplicitRamSizes = true
            };
        }

        cartridge.LoadImage(image);
        return cartridge;
    }

    private static CompiledBusTargetDescriptor CpuRomTarget(Mmc1Cartridge cartridge) =>
        ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets()
            .Single(target => target.AddressPins.Count == 15
                && target.ReadConditions.Any(condition => ReferenceEquals(condition.Pin, cartridge.CpuRomSelectBar)
                    && condition.RequiredLevel == DigitalLevel.Low));

    private static CompiledBusTargetDescriptor CpuRamTarget(Mmc1Cartridge cartridge) =>
        ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets()
            .Single(target => target.AddressPins.Count == 15
                && target.ReadConditions.Any(condition => ReferenceEquals(condition.Pin, cartridge.CpuRomSelectBar)
                    && condition.RequiredLevel == DigitalLevel.High));

    private static CompiledBusTargetDescriptor PpuChrTarget(Mmc1Cartridge cartridge) =>
        ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets()
            .Single(target => target.AddressPins.Count == 14);

    private static void WriteSerial(CompiledBusTargetDescriptor cpuTarget, ushort address, byte value)
    {
        for (var bit = 0; bit < 5; bit++)
            cpuTarget.Write!(address, (byte)((value >> bit) & 0x01));
    }

    private static DigitalSignalSource AttachSource(VirtualHardwareBoard board, string id, DigitalPin pin)
    {
        var source = board.Add(new DigitalSignalSource($"TEST.{id}", DigitalLevel.HighImpedance));
        board.Connect($"TEST.{id}.NET", source.Output, pin);
        return source;
    }

    private static void DriveByte(IReadOnlyList<DigitalSignalSource> sources, byte? value)
    {
        for (var bit = 0; bit < sources.Count; bit++)
        {
            if (value is null) sources[bit].Set(DigitalLevel.HighImpedance);
            else sources[bit].Set((value.Value & (1 << bit)) != 0 ? DigitalLevel.High : DigitalLevel.Low);
        }
    }

    private static void DriveAddress(IReadOnlyList<DigitalSignalSource> sources, ushort value)
    {
        for (var bit = 0; bit < sources.Count; bit++)
            sources[bit].Set((value & (1 << bit)) != 0 ? DigitalLevel.High : DigitalLevel.Low);
    }

    private static DigitalLevel SamplePpuAddress(Mmc1Cartridge cartridge, DigitalPin pin, ushort address)
    {
        for (var bit = 0; bit < cartridge.PpuAddress.Width; bit++)
        {
            if (!ReferenceEquals(pin, cartridge.PpuAddress.Pins[bit])) continue;
            return (address & (1 << bit)) != 0 ? DigitalLevel.High : DigitalLevel.Low;
        }
        return DigitalLevel.Unknown;
    }

}
