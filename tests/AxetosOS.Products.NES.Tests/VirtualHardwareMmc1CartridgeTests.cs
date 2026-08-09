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
            .Where(target => target.AddressPins.Count == 16)
            .ToArray();

        Assert.Equal(0, cartridge.PrgRamSizeBytes);
        Assert.False(cartridge.PrgRamEnabled);
        Assert.Single(cpuTargets);
        Assert.DoesNotContain(
            cpuTargets,
            target => target.ReadConditions.Any(condition =>
                ReferenceEquals(condition.Pin, cartridge.CpuAddress.Pins[15]) &&
                condition.RequiredLevel == DigitalLevel.Low));
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
            pin => SamplePpuHighAddress(cartridge, pin, ppuAddress),
            out var drive);

        Assert.True(evaluated);
        Assert.Equal(expected, drive.Level);
    }


    [Fact]
    public void Physical_ppu_address_phase_releases_previous_chr_data_before_latching_next_low_address()
    {
        var cartridge = CreateCartridge(prgBanks: 2, chrBanks4K: 2);
        var board = new VirtualHardwareBoard("MMC1.PPU.BUS.TEST");
        board.Add(cartridge);

        var vcc = AttachSource(board, "VCC", cartridge.Vcc);
        var gnd = AttachSource(board, "GND", cartridge.Gnd);
        var ale = AttachSource(board, "ALE", cartridge.PpuAle);
        var readBar = AttachSource(board, "RD", cartridge.PpuReadBar);
        var writeBar = AttachSource(board, "WR", cartridge.PpuWriteBar);
        var high = cartridge.PpuHighAddress.Pins
            .Select((pin, bit) => AttachSource(board, $"AH{bit}", pin))
            .ToArray();
        var ad = cartridge.PpuAddressData.Pins
            .Select((pin, bit) => AttachSource(board, $"AD{bit}", pin))
            .ToArray();

        // Physical source changes propagate only after the laboratory board's
        // traces have been topology-compiled, exactly as in a running machine.
        _ = new VirtualHardwareSimulator(board);

        foreach (var source in high) source.Set(DigitalLevel.Low);
        DriveByte(ad, 0x00);
        ale.Set(DigitalLevel.High);
        readBar.Set(DigitalLevel.High);
        writeBar.Set(DigitalLevel.High);
        gnd.Set(DigitalLevel.Low);
        vcc.Set(DigitalLevel.High);

        // Complete a CHR read at $0000 so the cartridge owns AD0-AD7 with $50.
        ale.Set(DigitalLevel.Low);
        DriveByte(ad, null);
        readBar.Set(DigitalLevel.Low);
        Assert.True(cartridge.PpuAddressData.TrySample(out var chrData));
        Assert.Equal(0x50UL, chrData);

        // The RP2C0x starts the next address phase by driving AD before ALE
        // rises. That temporarily contends with the previous CHR byte. ALE is
        // the physical signal that must make the cartridge release its drivers.
        DriveByte(ad, 0xA5);
        Assert.Contains(cartridge.PpuAddressData.Pins, pin => pin.SampledLevel == DigitalLevel.Contention);

        ale.Set(DigitalLevel.High);

        Assert.All(cartridge.PpuAddressData.Pins, pin => Assert.Equal(DigitalLevel.HighImpedance, pin.DriveLevel));
        Assert.True(cartridge.PpuAddressData.TrySample(out var nextLowAddress));
        Assert.Equal(0xA5UL, nextLowAddress);
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
            .Single(target => target.AddressPins.Count == 16
                && target.ReadConditions.Any(condition => ReferenceEquals(condition.Pin, cartridge.CpuAddress.Pins[15])
                    && condition.RequiredLevel == DigitalLevel.High));

    private static CompiledBusTargetDescriptor CpuRamTarget(Mmc1Cartridge cartridge) =>
        ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets()
            .Single(target => target.AddressPins.Count == 16
                && target.ReadConditions.Any(condition => ReferenceEquals(condition.Pin, cartridge.CpuAddress.Pins[15])
                    && condition.RequiredLevel == DigitalLevel.Low));

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

    private static DigitalLevel SamplePpuHighAddress(Mmc1Cartridge cartridge, DigitalPin pin, ushort address)
    {
        for (var bit = 0; bit < cartridge.PpuHighAddress.Width; bit++)
        {
            if (!ReferenceEquals(pin, cartridge.PpuHighAddress.Pins[bit])) continue;
            return (address & (1 << (bit + 8))) != 0 ? DigitalLevel.High : DigitalLevel.Low;
        }
        return DigitalLevel.Unknown;
    }
}
