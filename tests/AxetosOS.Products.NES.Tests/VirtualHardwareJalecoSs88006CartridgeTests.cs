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

public sealed class VirtualHardwareJalecoSs88006CartridgeTests
{
    [Fact]
    public void Power_on_uses_ss88006_reset_prg_and_chr_mapping()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuTarget(cartridge);

        Assert.Equal((byte)0x40, cpu.Read!(0x0000));
        Assert.Equal((byte)0x41, cpu.Read!(0x2000));
        Assert.Equal((byte)0x4E, cpu.Read!(0x4000));
        Assert.Equal((byte)0x4F, cpu.Read!(0x6000));
        Assert.Equal(new[] { 0, 1, 14, 15 }, cartridge.PrgWindowBanks.ToArray());
        for (var slot = 0; slot < 8; slot++)
            Assert.Equal((byte)(0x80 + slot), ppu.Read!(slot * 0x400));
        Assert.All(cartridge.PrgBankRegisters, value => Assert.Equal((byte)0, value));
        Assert.All(cartridge.ChrBankRegisters, value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void Split_nibble_prg_registers_control_three_independent_eight_kib_windows()
    {
        var cartridge = CreateCartridge(prgBanks: 64, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);

        WriteByteRegister(cpu, 0x0000, 0x0001, 0x25);
        WriteByteRegister(cpu, 0x0002, 0x0003, 0x16);
        WriteByteRegister(cpu, 0x1000, 0x1001, 0x37);

        Assert.Equal(new byte[] { 0x25, 0x16, 0x37 }, cartridge.PrgBankRegisters.ToArray());
        Assert.Equal(new[] { 37, 22, 55, 63 }, cartridge.PrgWindowBanks.ToArray());
        Assert.Equal((byte)(0x40 + 37), cpu.Read!(0x0000));
        Assert.Equal((byte)(0x40 + 22), cpu.Read!(0x2000));
        Assert.Equal((byte)(0x40 + 55), cpu.Read!(0x4000));
        Assert.Equal((byte)(0x40 + 63), cpu.Read!(0x6000));
    }

    [Fact]
    public void Fitted_prg_address_lines_mask_the_full_eight_bit_bank_register()
    {
        var cartridge = CreateCartridge(prgBanks: 8, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);

        WriteByteRegister(cpu, 0x0000, 0x0001, 0xFE);

        Assert.Equal((byte)0xFE, cartridge.PrgBankRegisters[0]);
        Assert.Equal(6, cartridge.PrgWindowBanks[0]);
        Assert.Equal((byte)0x46, cpu.Read!(0x0000));
    }

    [Fact]
    public void Eight_split_nibble_chr_registers_control_independent_one_kib_windows()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 64);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuTarget(cartridge);
        var values = new byte[] { 9, 10, 11, 12, 13, 14, 15, 16 };

        for (var slot = 0; slot < 8; slot++)
        {
            var group = slot / 2;
            var lowAddress = 0x2000 + (group * 0x1000) + ((slot & 1) * 2);
            WriteByteRegister(cpu, lowAddress, lowAddress + 1, values[slot]);
        }

        Assert.Equal(values, cartridge.ChrBankRegisters.ToArray());
        for (var slot = 0; slot < 8; slot++)
            Assert.Equal((byte)(0x80 + values[slot]), ppu.Read!(slot * 0x400));
    }

    [Fact]
    public void Fitted_chr_address_lines_mask_bank_register_outputs()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 16);
        var cpu = CpuRomTarget(cartridge);
        var ppu = PpuTarget(cartridge);

        WriteByteRegister(cpu, 0x2000, 0x2001, 0x2F);

        Assert.Equal((byte)0x2F, cartridge.ChrBankRegisters[0]);
        Assert.Equal(15, cartridge.ChrWindowBanks[0]);
        Assert.Equal((byte)0x8F, ppu.Read!(0x0000));
    }

    [Fact]
    public void Optional_wram_is_disabled_on_reset_and_protect_bits_gate_read_and_write_separately()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32, wram: true);
        var cpu = CpuRomTarget(cartridge);
        var ram = WramTarget(cartridge);
        var selected = Assert.IsType<Func<int, bool, bool>>(ram.IsSelected);

        Assert.False(selected(0x6000, false));
        Assert.False(selected(0x6000, true));

        cpu.Write!(0x1002, 0x01);
        Assert.True(selected(0x6000, false));
        Assert.False(selected(0x6000, true));

        cpu.Write!(0x1002, 0x03);
        Assert.True(selected(0x6000, false));
        Assert.True(selected(0x6000, true));
        ram.Write!(0x6000, 0xA6);
        Assert.Equal((byte)0xA6, ram.Read!(0x6000));
        Assert.Equal((byte)0xA6, cartridge.InspectWramByte(0));
        Assert.Equal(1UL, cartridge.WramWriteCount);
        Assert.Equal(1UL, cartridge.WramReadCount);
    }

    [Fact]
    public void Legacy_nonbattery_images_do_not_invent_the_ines_default_eight_kib_wram()
    {
        var image = CreateImage(CreatePrg(16), CreateChr(32), wram: false, header: VirtualHardwareNesHeaderFormat.INes);
        var cartridge = new JalecoSs88006Cartridge("TEST.JALECO.LEGACY");

        cartridge.LoadImage(image);

        Assert.Equal(0, cartridge.WramSizeBytes);
        Assert.DoesNotContain(((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(), target => target.IsSelected is not null);
    }

    [Fact]
    public void Legacy_battery_image_fits_one_eight_kib_wram_chip()
    {
        var image = CreateImage(CreatePrg(16), CreateChr(32), wram: false, header: VirtualHardwareNesHeaderFormat.INes, battery: true);
        var cartridge = new JalecoSs88006Cartridge("TEST.JALECO.LEGACY.BATTERY");

        cartridge.LoadImage(image);

        Assert.Equal(8 * 1024, cartridge.WramSizeBytes);
    }

    [Theory]
    [InlineData(0x00, JalecoSs88006NametableMode.Horizontal, DigitalLevel.Low)]
    [InlineData(0x01, JalecoSs88006NametableMode.Vertical, DigitalLevel.High)]
    [InlineData(0x02, JalecoSs88006NametableMode.SingleScreenPage0, DigitalLevel.Low)]
    [InlineData(0x03, JalecoSs88006NametableMode.SingleScreenPage1, DigitalLevel.High)]
    public void Mirroring_register_exposes_all_four_live_ciram_routes(byte value, JalecoSs88006NametableMode expected, DigitalLevel expectedAt0400)
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);
        var dynamic = Assert.IsAssignableFrom<ICompiledCombinationalComponent>(cartridge);
        var statik = Assert.IsAssignableFrom<ICompiledStaticCombinationalComponent>(cartridge);

        cpu.Write!(0x7002, value);

        Assert.Equal(expected, cartridge.NametableMode);
        Assert.False(statik.TryEvaluateCompiledStaticOutput(cartridge.CiramA10, pin => SamplePpuAddress(cartridge, pin, 0x0400), out _));
        Assert.True(dynamic.TryEvaluateCompiledOutput(cartridge.CiramA10, pin => SamplePpuAddress(cartridge, pin, 0x0400), out var drive));
        Assert.Equal(expectedAt0400, drive.Level);
    }

    [Fact]
    public void Four_irq_latch_nibbles_form_the_full_sixteen_bit_reload_value()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);

        cpu.Write!(0x6000, 0x04);
        cpu.Write!(0x6001, 0x03);
        cpu.Write!(0x6002, 0x02);
        cpu.Write!(0x6003, 0x01);
        cpu.Write!(0x7000, 0x00);

        Assert.Equal((ushort)0x1234, cartridge.IrqLatch);
        Assert.Equal((ushort)0x1234, cartridge.IrqCounter);
    }

    [Theory]
    [InlineData(0x00, 0xFFFF)]
    [InlineData(0x02, 0x0FFF)]
    [InlineData(0x04, 0x00FF)]
    [InlineData(0x08, 0x000F)]
    public void Irq_mode_selects_sixteen_twelve_eight_or_four_bit_underflow_counter(byte mode, int expectedMask)
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);
        cpu.Write!(0x6000, 0x01);
        cpu.Write!(0x7000, 0x00);
        cpu.Write!(0x7001, (byte)(mode | 0x01));

        ClockCpu(cpu, 2);

        Assert.Equal((ushort)expectedMask, cartridge.IrqCounterMask);
        Assert.Equal((ushort)expectedMask, cartridge.IrqCounter);
        Assert.True(cartridge.IrqAsserted);
        Assert.True(cartridge.IrqEnabled);
        Assert.Equal(2UL, cartridge.IrqClockCount);
        Assert.Equal(1UL, cartridge.IrqAssertCount);
    }

    [Fact]
    public void Irq_mode_priority_matches_ss88006_bit_three_then_two_then_one_decode()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);

        cpu.Write!(0x7001, 0x0F);
        Assert.Equal((ushort)0x000F, cartridge.IrqCounterMask);
        cpu.Write!(0x7001, 0x07);
        Assert.Equal((ushort)0x00FF, cartridge.IrqCounterMask);
        cpu.Write!(0x7001, 0x03);
        Assert.Equal((ushort)0x0FFF, cartridge.IrqCounterMask);
    }

    [Fact]
    public void Reload_and_control_registers_both_acknowledge_an_asserted_irq()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);
        cpu.Write!(0x6000, 0x00);
        cpu.Write!(0x7000, 0x00);
        cpu.Write!(0x7001, 0x09);
        ClockCpu(cpu, 1);
        Assert.True(cartridge.IrqAsserted);

        cpu.Write!(0x7000, 0x00);
        Assert.False(cartridge.IrqAsserted);
        ClockCpu(cpu, 1);
        Assert.True(cartridge.IrqAsserted);

        cpu.Write!(0x7001, 0x00);
        Assert.False(cartridge.IrqAsserted);
        Assert.False(cartridge.IrqEnabled);
    }

    [Fact]
    public void Irq_output_is_dynamic_open_collector_state_and_not_static_foldable()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);
        var dynamic = Assert.IsAssignableFrom<ICompiledCombinationalComponent>(cartridge);
        var statik = Assert.IsAssignableFrom<ICompiledStaticCombinationalComponent>(cartridge);

        Assert.False(statik.TryEvaluateCompiledStaticOutput(cartridge.IrqBar, _ => DigitalLevel.Unknown, out _));
        Assert.True(dynamic.TryEvaluateCompiledOutput(cartridge.IrqBar, _ => DigitalLevel.Unknown, out var idle));
        Assert.Equal(DigitalLevel.HighImpedance, idle.Level);

        cpu.Write!(0x7000, 0x00);
        cpu.Write!(0x7001, 0x09);
        ClockCpu(cpu, 1);
        Assert.True(dynamic.TryEvaluateCompiledOutput(cartridge.IrqBar, _ => DigitalLevel.Unknown, out var asserted));
        Assert.Equal(DigitalLevel.Low, asserted.Level);
    }

    [Fact]
    public void Raw_physical_m2_falling_edges_clock_irq_without_mapper_chip_select()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);
        cpu.Write!(0x6000, 0x01);
        cpu.Write!(0x7000, 0x00);
        cpu.Write!(0x7001, 0x09);

        var board = new VirtualHardwareBoard("JALECO.IRQ.PHYSICAL");
        board.Add(cartridge);
        var vcc = AttachSource(board, "VCC", cartridge.Vcc);
        var gnd = AttachSource(board, "GND", cartridge.Gnd);
        var m2 = AttachSource(board, "M2", cartridge.CpuM2);
        _ = new VirtualHardwareSimulator(board);
        gnd.Set(DigitalLevel.Low);
        m2.Set(DigitalLevel.Low);
        vcc.Set(DigitalLevel.High);

        for (var cycle = 0; cycle < 2; cycle++)
        {
            m2.Set(DigitalLevel.High);
            m2.Set(DigitalLevel.Low);
        }

        Assert.Equal((ushort)0x000F, cartridge.IrqCounter);
        Assert.True(cartridge.IrqAsserted);
        Assert.Equal(2UL, cartridge.IrqClockCount);
    }

    [Fact]
    public void Sample_control_register_records_external_adpcm_trigger_without_synthesizing_audio()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);

        cpu.Write!(0x7003, (byte)((17 << 2) | 0x02));

        Assert.Equal((byte)0x46, cartridge.SampleControlRegister);
        Assert.Equal(17, cartridge.LastSampleIndex);
        Assert.Equal(1UL, cartridge.SampleControlWriteCount);
        Assert.Equal(1UL, cartridge.SampleTriggerCount);
    }

    [Fact]
    public void Nontrigger_sample_control_value_is_still_latched_as_physical_asic_output_state()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        var cpu = CpuRomTarget(cartridge);

        cpu.Write!(0x7003, 0x45);

        Assert.Equal((byte)0x45, cartridge.SampleControlRegister);
        Assert.Equal(-1, cartridge.LastSampleIndex);
        Assert.Equal(1UL, cartridge.SampleControlWriteCount);
        Assert.Equal(0UL, cartridge.SampleTriggerCount);
    }

    [Fact]
    public void Mapper_register_writes_latch_at_completed_cpu_bus_phase()
    {
        var cartridge = CreateCartridge(prgBanks: 16, chrBanks: 32);
        Assert.Equal(CompiledBusWritePhase.Complete, CpuRomTarget(cartridge).WritePhase);
    }

    [Fact]
    public void Generic_compiled_and_raw_physical_ss88006_execute_same_banking_mirroring_and_irq_program()
    {
        var image = CreateExecutionImage();
        var compiled = new RegionalNesVirtualMachine();
        var reference = new RegionalNesVirtualMachine();

        compiled.SetCompiledLabExecutionEnabled(true);
        compiled.InsertRom(image, "Jaleco SS88006 execution (Japan).nes");
        reference.InsertRom(image, "Jaleco SS88006 execution (Japan).nes");
        reference.Famicom.SetCompiledPhysicalMachineEnabled(false);

        compiled.PowerOn();
        reference.PowerOn();
        compiled.ReleaseReset();
        reference.ReleaseReset();

        const int masterCycles = 60_000;
        compiled.AdvanceMasterCycles(masterCycles);
        reference.AdvanceMasterCycles(masterCycles);

        var actual = Assert.IsType<JalecoSs88006Cartridge>(compiled.Slot.Cartridge);
        var expected = Assert.IsType<JalecoSs88006Cartridge>(reference.Slot.Cartridge);
        Assert.True(compiled.Famicom.CompiledLabMotherboardEnabled);
        Assert.Equal(new byte[] { 5, 6, 7 }, actual.PrgBankRegisters.ToArray());
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, actual.ChrBankRegisters.ToArray());
        Assert.Equal(JalecoSs88006NametableMode.SingleScreenPage1, actual.NametableMode);
        Assert.True(actual.IrqAsserted);
        Assert.Equal(expected.PrgWindowBanks.ToArray(), actual.PrgWindowBanks.ToArray());
        Assert.Equal(expected.ChrWindowBanks.ToArray(), actual.ChrWindowBanks.ToArray());
        Assert.Equal(expected.IrqCounter, actual.IrqCounter);
        Assert.Equal(expected.IrqAsserted, actual.IrqAsserted);
        Assert.Equal(expected.IrqClockCount, actual.IrqClockCount);
        Assert.Equal(expected.MapperWriteCount, actual.MapperWriteCount);
        Assert.Equal(reference.Famicom.Cpu.ProgramCounter, compiled.Famicom.Cpu.ProgramCounter);
        Assert.Equal(reference.Famicom.Cpu.CompletedInstructionCount, compiled.Famicom.Cpu.CompletedInstructionCount);
        Assert.Equal(reference.Famicom.Ppu.InspectDiagnosticState(), compiled.Famicom.Ppu.InspectDiagnosticState());
        Assert.Equal(reference.Famicom.Ciram.InspectStateHash(), compiled.Famicom.Ciram.InspectStateHash());
    }

    [Fact]
    public void Invalid_four_screen_chr_ram_and_bad_wram_sizes_are_rejected()
    {
        var basic = CreateImage(CreatePrg(16), CreateChr(32), wram: false);
        var fourScreen = basic with { Mirroring = VirtualHardwareNesMirroring.FourScreen };
        var chrRam = basic with { ChrRamSizeBytes = 8 * 1024 };
        var noChr = basic with { ChrRom = [], ChrRomSizeBytes = 0 };
        var badWram = basic with { PrgRamSizeBytes = 4 * 1024 };

        Assert.Throws<NotSupportedException>(() => new JalecoSs88006Cartridge("TEST.JALECO.4S").LoadImage(fourScreen));
        Assert.Throws<NotSupportedException>(() => new JalecoSs88006Cartridge("TEST.JALECO.CHRRAM").LoadImage(chrRam));
        Assert.Throws<NotSupportedException>(() => new JalecoSs88006Cartridge("TEST.JALECO.NOCHR").LoadImage(noChr));
        Assert.Throws<NotSupportedException>(() => new JalecoSs88006Cartridge("TEST.JALECO.WRAM").LoadImage(badWram));
    }

    [Fact]
    public void Factory_constructs_mapper_eighteen_as_replaceable_ss88006_hardware()
    {
        var image = CreateImage(CreatePrg(16), CreateChr(32), wram: false);
        var cartridge = VirtualCartridgeHardwareFactory.Create(image);

        Assert.Equal(18, cartridge.MapperNumber);
        Assert.IsType<JalecoSs88006Cartridge>(cartridge);
        Assert.True(cartridge.IsInserted);
    }

    private static JalecoSs88006Cartridge CreateCartridge(int prgBanks, int chrBanks, bool wram = false)
    {
        var cartridge = new JalecoSs88006Cartridge("TEST.JALECO.SS88006");
        cartridge.LoadImage(CreateImage(CreatePrg(prgBanks), CreateChr(chrBanks), wram));
        return cartridge;
    }

    private static byte[] CreatePrg(int banks)
    {
        var prg = new byte[banks * 8 * 1024];
        for (var bank = 0; bank < banks; bank++)
            Array.Fill(prg, (byte)(0x40 + (bank & 0x3F)), bank * 8 * 1024, 8 * 1024);
        return prg;
    }

    private static byte[] CreateChr(int banks)
    {
        var chr = new byte[banks * 1024];
        for (var bank = 0; bank < banks; bank++)
            Array.Fill(chr, (byte)(0x80 + (bank & 0x7F)), bank * 1024, 1024);
        return chr;
    }

    private static VirtualHardwareNesRomImage CreateImage(
        byte[] prg,
        byte[] chr,
        bool wram,
        VirtualHardwareNesHeaderFormat header = VirtualHardwareNesHeaderFormat.Nes20,
        bool battery = false) =>
        new(
            header,
            MapperNumber: 18,
            SubmapperNumber: header == VirtualHardwareNesHeaderFormat.Nes20 ? 0 : null,
            PrgRomSizeBytes: prg.Length,
            ChrRomSizeBytes: chr.Length,
            HasTrainer: false,
            HasBatteryBackedMemory: battery,
            VirtualHardwareNesMirroring.Vertical,
            VirtualHardwareNesHeaderTiming.Ntsc,
            prg,
            chr)
        {
            PrgRamSizeBytes = wram ? 8 * 1024 : 0,
            PrgNvRamSizeBytes = 0,
            ChrRamSizeBytes = 0,
            ChrNvRamSizeBytes = 0,
            HasExplicitRamSizes = header == VirtualHardwareNesHeaderFormat.Nes20
        };

    private static VirtualHardwareNesRomImage CreateExecutionImage()
    {
        var prg = CreatePrg(16);
        var program = new List<byte> { 0x78 }; // SEI

        AddRegisterByte(program, 0x8000, 0x8001, 5);
        AddRegisterByte(program, 0x8002, 0x8003, 6);
        AddRegisterByte(program, 0x9000, 0x9001, 7);
        for (var slot = 0; slot < 8; slot++)
        {
            var group = slot / 2;
            var lowAddress = (ushort)(0xA000 + (group * 0x1000) + ((slot & 1) * 2));
            AddRegisterByte(program, lowAddress, (ushort)(lowAddress + 1), (byte)(slot + 1));
        }

        AddSta(program, 0xF002, 0x03);
        AddSta(program, 0xE000, 0x03);
        AddSta(program, 0xE001, 0x00);
        AddSta(program, 0xE002, 0x00);
        AddSta(program, 0xE003, 0x00);
        AddSta(program, 0xF000, 0x00);
        AddSta(program, 0xF001, 0x09);

        var loopAddress = (ushort)(0xE000 + program.Count);
        program.AddRange(new byte[] { 0x4C, (byte)loopAddress, (byte)(loopAddress >> 8) });

        var fixedLast = 15 * 8 * 1024;
        program.ToArray().CopyTo(prg, fixedLast);
        prg[fixedLast + 0x1FFA] = 0x00; prg[fixedLast + 0x1FFB] = 0xE0;
        prg[fixedLast + 0x1FFC] = 0x00; prg[fixedLast + 0x1FFD] = 0xE0;
        prg[fixedLast + 0x1FFE] = 0x00; prg[fixedLast + 0x1FFF] = 0xE0;
        return CreateImage(prg, CreateChr(64), wram: false);
    }

    private static void AddRegisterByte(List<byte> program, int lowAddress, int highAddress, byte value)
    {
        AddSta(program, (ushort)lowAddress, (byte)(value & 0x0F));
        AddSta(program, (ushort)highAddress, (byte)(value >> 4));
    }

    private static void AddSta(List<byte> program, ushort address, byte value) =>
        program.AddRange(new byte[] { 0xA9, value, 0x8D, (byte)address, (byte)(address >> 8) });

    private static CompiledBusTargetDescriptor CpuRomTarget(JalecoSs88006Cartridge cartridge) =>
        Assert.Single(
            ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.AddressPins.Count == 15
                && target.DataPins.Count == 8
                && target.Read is not null
                && target.ReadConditions.Any(condition => ReferenceEquals(condition.Pin, cartridge.CpuRomSelectBar)
                    && condition.RequiredLevel == DigitalLevel.Low));

    private static CompiledBusTargetDescriptor WramTarget(JalecoSs88006Cartridge cartridge) =>
        Assert.Single(
            ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.IsSelected is not null);

    private static CompiledBusTargetDescriptor PpuTarget(JalecoSs88006Cartridge cartridge) =>
        Assert.Single(
            ((ICompiledBusTargetProvider)cartridge).GetCompiledBusTargets(),
            target => target.AddressPins.Count == 14);

    private static void WriteByteRegister(CompiledBusTargetDescriptor cpu, int lowAddress, int highAddress, byte value)
    {
        cpu.Write!(lowAddress, (byte)(value & 0x0F));
        cpu.Write!(highAddress, (byte)(value >> 4));
    }

    private static void ClockCpu(CompiledBusTargetDescriptor cpu, int cycles)
    {
        var observer = Assert.IsType<Action<bool>>(cpu.ObserveBusCycle);
        for (var index = 0; index < cycles; index++) observer(false);
    }

    private static DigitalLevel SamplePpuAddress(JalecoSs88006Cartridge cartridge, DigitalPin pin, ushort address)
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
}
