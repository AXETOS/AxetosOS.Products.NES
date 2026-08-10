using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Machines.Nes;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareRegionalNesMachineTests
{
    [Theory]
    [InlineData("Game (Japan).nes", ActiveNesMotherboard.Famicom)]
    [InlineData("Game (USA).nes", ActiveNesMotherboard.NtscNes)]
    [InlineData("Game (Europe).nes", ActiveNesMotherboard.PalNes)]
    public void One_shared_slot_selects_exactly_one_regional_motherboard(
        string fileName,
        ActiveNesMotherboard expected)
    {
        var machine = new RegionalNesVirtualMachine();
        machine.InsertRom(CreateImage(), fileName);

        Assert.Equal(expected, machine.ActiveMotherboard);
        Assert.NotNull(machine.ActiveBoard);
        Assert.True(machine.Slot.IsOccupied);
        Assert.Equal(1UL, machine.SelectionCount);
    }

    [Fact]
    public void Manual_region_override_wins_over_rom_name()
    {
        var machine = new RegionalNesVirtualMachine();
        machine.InsertRom(CreateImage(), "Game (Japan).nes", NesRegionSelection.Pal, PalCicVariant.PalB3197);

        Assert.Equal(ActiveNesMotherboard.PalNes, machine.ActiveMotherboard);
        Assert.Equal(PalCicVariant.PalB3197, machine.PalNes.CicVariant);
        Assert.NotNull(machine.PalNes.Cic3197);
        Assert.Null(machine.PalNes.Cic3195);
    }

    [Fact]
    public void Only_the_selected_board_is_clocked_by_machine_operations()
    {
        var machine = new RegionalNesVirtualMachine();
        machine.InsertRom(CreateImage(), "Game (Japan).nes");
        machine.PowerOn();
        machine.ReleaseReset();
        machine.AdvanceMasterCycles(12);

        Assert.Equal(12UL, machine.Famicom.Cpu.MasterClockRisingEdgeCount);
        Assert.Equal(0UL, machine.NtscNes.Cpu.MasterClockRisingEdgeCount);
        Assert.Equal(0UL, machine.PalNes.Cpu.MasterClockRisingEdgeCount);
    }

    [Fact]
    public void Slot_has_one_normalized_bus_shape_for_all_regions()
    {
        Assert.Equal(15, SharedVirtualRomSlot.CpuAddressWidth);
        Assert.Equal(8, SharedVirtualRomSlot.CpuDataWidth);
        Assert.Equal(14, SharedVirtualRomSlot.PpuAddressWidth);
        Assert.Equal(8, SharedVirtualRomSlot.PpuDataWidth);
    }

    [Fact]
    public void Powered_machine_rejects_rom_replacement_and_ejection()
    {
        var machine = new RegionalNesVirtualMachine();
        machine.InsertRom(CreateImage(), "Game (USA).nes");
        machine.PowerOn();

        Assert.Throws<InvalidOperationException>(() => machine.InsertRom(CreateImage(), "Other (Japan).nes"));
        Assert.Throws<InvalidOperationException>(machine.EjectRom);
    }


    [Fact]
    public void Mapper_zero_rom_is_installed_as_a_real_cartridge_component_on_the_selected_board()
    {
        var machine = new RegionalNesVirtualMachine();
        machine.InsertRom(CreateImage(), "Game (Japan).nes");

        var cartridge = machine.Slot.Cartridge;
        Assert.NotNull(cartridge);
        Assert.Contains(cartridge!, machine.Famicom.Board.Components);
        Assert.Equal(15, cartridge!.CpuAddress.Pins.Count);
        Assert.Same(machine.Famicom.CpuAddressNets[14], cartridge.CpuAddress.Pins[14].Net);
        Assert.Same(machine.Famicom.CpuM2Net, cartridge.CpuM2.Net);
        Assert.Same(machine.Famicom.CpuRomSelectBarNet, cartridge.CpuRomSelectBar.Net);
        Assert.Same(machine.Famicom.PpuDataNets[0], cartridge!.PpuData.Pins[0].Net);
        Assert.Same(machine.Famicom.PpuLowAddressNets[0], cartridge.PpuAddress.Pins[0].Net);
        Assert.NotSame(cartridge.PpuData.Pins[0].Net, cartridge.PpuAddress.Pins[0].Net);
        Assert.DoesNotContain(cartridge.Pins, pin => ReferenceEquals(pin.Net, machine.Famicom.PpuAleNet));
        Assert.Same(machine.Famicom.CiramChipEnableBarNet, cartridge!.CiramChipEnableBar.Net);
        Assert.Same(machine.Famicom.CiramA10Net, cartridge!.CiramA10.Net);
    }

    [Fact]
    public void Famicom_nrom_compiles_one_fused_physical_runtime_after_cartridge_attachment()
    {
        var machine = new RegionalNesVirtualMachine();
        machine.InsertRom(CreateImage(), "Game (Japan).nes");

        Assert.True(machine.Famicom.CompiledPhysicalMachineEnabled);
        Assert.Equal(1, machine.Famicom.CompiledRuntimeUnitCount);
        Assert.Equal(47, machine.Famicom.CompiledFoldedPhysicalTraceCount);

        // Compilation is an execution representation only. The authoritative
        // physical package pins remain attached to the original motherboard
        // traces and can still be inspected even though the fused hot loop no
        // longer dispatches through them.
        Assert.Same(machine.Famicom.CpuAddressNets[0], machine.Famicom.Cpu.Address.Pins[0].Net);
        Assert.Same(machine.Famicom.CpuDataNets[0], machine.Slot.Cartridge!.CpuData.Pins[0].Net);
        Assert.Same(machine.Famicom.PpuDataNets[0], machine.Famicom.Ppu.MultiplexedAddressData.Pins[0].Net);
    }

    [Fact]
    public void Famicom_compiled_machine_can_be_disabled_for_same_build_reference_comparison()
    {
        var machine = new RegionalNesVirtualMachine();
        machine.InsertRom(CreateImage(), "Game (Japan).nes");
        Assert.True(machine.Famicom.CompiledPhysicalMachineEnabled);

        machine.Famicom.SetCompiledPhysicalMachineEnabled(false);

        Assert.False(machine.Famicom.CompiledPhysicalMachineEnabled);
        Assert.Same(machine.Famicom.CpuDataNets[0], machine.Famicom.Cpu.Data.Pins[0].Net);
        Assert.Same(machine.Famicom.PpuDataNets[0], machine.Famicom.Ppu.MultiplexedAddressData.Pins[0].Net);
    }

    [Fact]
    public void Compiled_famicom_nrom_matches_reference_runtime_at_the_same_master_cycle_boundary()
    {
        var image = CreateImage();
        var compiled = new RegionalNesVirtualMachine();
        var reference = new RegionalNesVirtualMachine();
        compiled.InsertRom(image, "Game (Japan).nes");
        reference.InsertRom(image, "Game (Japan).nes");
        reference.Famicom.SetCompiledPhysicalMachineEnabled(false);

        compiled.PowerOn();
        reference.PowerOn();
        compiled.ReleaseReset();
        reference.ReleaseReset();

        const int masterCycles = 1_200;
        compiled.AdvanceMasterCycles(masterCycles);
        reference.AdvanceMasterCycles(masterCycles);

        Assert.Equal(reference.Famicom.MasterClock.HalfCycleCount, compiled.Famicom.MasterClock.HalfCycleCount);
        Assert.Equal(reference.Famicom.Cpu.MasterClockRisingEdgeCount, compiled.Famicom.Cpu.MasterClockRisingEdgeCount);
        Assert.Equal(reference.Famicom.Ppu.MasterClockRisingEdgeCount, compiled.Famicom.Ppu.MasterClockRisingEdgeCount);
        Assert.Equal(reference.Famicom.Cpu.RisingEdgeCount, compiled.Famicom.Cpu.RisingEdgeCount);
        Assert.Equal(reference.Famicom.Cpu.CompletedInstructionCount, compiled.Famicom.Cpu.CompletedInstructionCount);
        Assert.Equal(reference.Famicom.Cpu.ProgramCounter, compiled.Famicom.Cpu.ProgramCounter);
        Assert.Equal(reference.Famicom.Cpu.CurrentOpcode, compiled.Famicom.Cpu.CurrentOpcode);
        Assert.Equal(reference.Famicom.Cpu.CurrentM2Level, compiled.Famicom.Cpu.CurrentM2Level);
        Assert.Equal(reference.Famicom.Ppu.Scanline, compiled.Famicom.Ppu.Scanline);
        Assert.Equal(reference.Famicom.Ppu.Dot, compiled.Famicom.Ppu.Dot);
        Assert.Equal(reference.Famicom.Ppu.Frame, compiled.Famicom.Ppu.Frame);
        var referenceCartridge = reference.Slot.Cartridge!;
        var compiledCartridge = compiled.Slot.Cartridge!;
        Assert.Equal(referenceCartridge.CpuReadCount, compiledCartridge.CpuReadCount);
        Assert.Equal(referenceCartridge.LastCpuReadAddress, compiledCartridge.LastCpuReadAddress);
        Assert.Equal(referenceCartridge.LastCpuReadData, compiledCartridge.LastCpuReadData);
        Assert.Equal(referenceCartridge.PpuReadCount, compiledCartridge.PpuReadCount);
    }



    [Fact]
    public void Whole_circuit_lab_compiler_keeps_replaceable_cartridge_outside_motherboard_unit()
    {
        var machine = new RegionalNesVirtualMachine();
        machine.InsertRom(CreateImage(), "Game (Japan).nes");
        var cartridge = machine.Slot.Cartridge!;

        machine.Famicom.SetCompiledLabMotherboardEnabled(true);

        Assert.True(machine.Famicom.CompiledLabMotherboardEnabled);
        Assert.False(machine.Famicom.CompiledPhysicalMachineEnabled);
        Assert.Equal(2, machine.Famicom.CompiledLabRuntimeUnitCount);
        Assert.True(machine.Famicom.CompiledLabInternalComponentCount > 0);
        Assert.True(machine.Famicom.CompiledLabFoldedInternalTraceCount > 0);
        Assert.True(machine.Famicom.CompiledLabBoundaryTraceCount > 0);
        Assert.Contains(cartridge, machine.Famicom.Board.Components);
        Assert.Same(machine.Famicom.CpuDataNets[0], cartridge.CpuData.Pins[0].Net);
        Assert.Same(machine.Famicom.PpuDataNets[0], cartridge.PpuData.Pins[0].Net);
        Assert.Same(machine.Famicom.PpuLowAddressNets[0], cartridge.PpuAddress.Pins[0].Net);
    }

    [Fact]
    public void Whole_circuit_lab_compiler_matches_reference_runtime_at_same_master_cycle_boundary()
    {
        var image = CreateImage();
        var compiled = new RegionalNesVirtualMachine();
        var reference = new RegionalNesVirtualMachine();
        compiled.InsertRom(image, "Game (Japan).nes");
        reference.InsertRom(image, "Game (Japan).nes");
        compiled.Famicom.SetCompiledLabMotherboardEnabled(true);
        reference.Famicom.SetCompiledPhysicalMachineEnabled(false);

        compiled.PowerOn();
        reference.PowerOn();
        compiled.ReleaseReset();
        reference.ReleaseReset();

        const int masterCycles = 1_200;
        compiled.AdvanceMasterCycles(masterCycles);
        reference.AdvanceMasterCycles(masterCycles);

        Assert.Equal(reference.Famicom.MasterClock.HalfCycleCount, compiled.Famicom.MasterClock.HalfCycleCount);
        Assert.Equal(reference.Famicom.Cpu.MasterClockRisingEdgeCount, compiled.Famicom.Cpu.MasterClockRisingEdgeCount);
        Assert.Equal(reference.Famicom.Ppu.MasterClockRisingEdgeCount, compiled.Famicom.Ppu.MasterClockRisingEdgeCount);
        Assert.Equal(reference.Famicom.Cpu.RisingEdgeCount, compiled.Famicom.Cpu.RisingEdgeCount);
        Assert.Equal(reference.Famicom.Cpu.CompletedInstructionCount, compiled.Famicom.Cpu.CompletedInstructionCount);
        Assert.Equal(reference.Famicom.Cpu.ProgramCounter, compiled.Famicom.Cpu.ProgramCounter);
        Assert.Equal(reference.Famicom.Cpu.CurrentOpcode, compiled.Famicom.Cpu.CurrentOpcode);
        Assert.Equal(reference.Famicom.Ppu.Scanline, compiled.Famicom.Ppu.Scanline);
        Assert.Equal(reference.Famicom.Ppu.Dot, compiled.Famicom.Ppu.Dot);
        Assert.Equal(reference.Famicom.Ppu.Frame, compiled.Famicom.Ppu.Frame);
        Assert.Equal(reference.Slot.Cartridge!.CpuReadCount, compiled.Slot.Cartridge!.CpuReadCount);
        Assert.Equal(reference.Slot.Cartridge!.LastCpuReadAddress, compiled.Slot.Cartridge!.LastCpuReadAddress);
        Assert.Equal(reference.Slot.Cartridge!.LastCpuReadData, compiled.Slot.Cartridge!.LastCpuReadData);
        Assert.Equal(reference.Slot.Cartridge!.PpuReadCount, compiled.Slot.Cartridge!.PpuReadCount);
    }

    [Fact]
    public void Whole_circuit_lab_compiler_preserves_apu_register_writes_and_dac_output()
    {
        var image = CreatePulseAudioImage();
        var compiled = new RegionalNesVirtualMachine();
        var reference = new RegionalNesVirtualMachine();
        compiled.InsertRom(image, "Pulse Audio (Japan).nes");
        reference.InsertRom(image, "Pulse Audio (Japan).nes");
        compiled.Famicom.SetCompiledLabMotherboardEnabled(true);
        reference.Famicom.SetCompiledPhysicalMachineEnabled(false);

        compiled.Famicom.Cpu.AudioDacOutput.SetCaptureEnabled(true);
        reference.Famicom.Cpu.AudioDacOutput.SetCaptureEnabled(true);
        compiled.PowerOn();
        reference.PowerOn();
        compiled.ReleaseReset();
        reference.ReleaseReset();

        const int masterCycles = 60_000;
        compiled.AdvanceMasterCycles(masterCycles);
        reference.AdvanceMasterCycles(masterCycles);

        var compiledSamples = compiled.Famicom.Cpu.AudioDacOutput.Drain();
        var referenceSamples = reference.Famicom.Cpu.AudioDacOutput.Drain();
        Assert.Contains(compiledSamples, sample => sample.DacLevel > 0);
        Assert.Equal(reference.Famicom.Cpu.ApuCpuCycleCount, compiled.Famicom.Cpu.ApuCpuCycleCount);
        Assert.Equal(reference.Famicom.Cpu.AudioDacLevel, compiled.Famicom.Cpu.AudioDacLevel);
        Assert.Equal(referenceSamples, compiledSamples);
    }

    [Fact]
    public void Compiled_famicom_nrom_preserves_apu_register_writes_and_dac_output()
    {
        var image = CreatePulseAudioImage();
        var compiled = new RegionalNesVirtualMachine();
        var reference = new RegionalNesVirtualMachine();
        compiled.InsertRom(image, "Pulse Audio (Japan).nes");
        reference.InsertRom(image, "Pulse Audio (Japan).nes");
        reference.Famicom.SetCompiledPhysicalMachineEnabled(false);

        compiled.Famicom.Cpu.AudioDacOutput.SetCaptureEnabled(true);
        reference.Famicom.Cpu.AudioDacOutput.SetCaptureEnabled(true);
        compiled.PowerOn();
        reference.PowerOn();
        compiled.ReleaseReset();
        reference.ReleaseReset();

        const int masterCycles = 60_000;
        compiled.AdvanceMasterCycles(masterCycles);
        reference.AdvanceMasterCycles(masterCycles);

        var compiledSamples = compiled.Famicom.Cpu.AudioDacOutput.Drain();
        var referenceSamples = reference.Famicom.Cpu.AudioDacOutput.Drain();
        Assert.Contains(compiledSamples, sample => sample.DacLevel > 0);
        Assert.Equal(reference.Famicom.Cpu.ApuCpuCycleCount, compiled.Famicom.Cpu.ApuCpuCycleCount);
        Assert.Equal(reference.Famicom.Cpu.AudioDacLevel, compiled.Famicom.Cpu.AudioDacLevel);
        Assert.Equal(referenceSamples, compiledSamples);
    }

    [Fact]
    public void Mapper_one_rom_constructs_mmc1_cartridge_hardware_before_power_is_applied()
    {
        var machine = new RegionalNesVirtualMachine();
        var image = CreateMmc1Image();

        machine.InsertRom(image, "MMC1 (Japan).nes");

        Assert.False(machine.IsPowered);
        Assert.Equal(ActiveNesMotherboard.Famicom, machine.ActiveMotherboard);
        Assert.IsType<Mmc1Cartridge>(machine.Slot.Cartridge);
        Assert.Equal(1, machine.Slot.Cartridge!.MapperNumber);
        Assert.Contains(machine.Slot.Cartridge!, machine.Famicom.Board.Components);
    }

    [Fact]
    public void Compiled_motherboard_identity_survives_nrom_to_mmc1_cartridge_replacement()
    {
        var machine = new RegionalNesVirtualMachine();
        machine.SetCompiledLabExecutionEnabled(true);

        machine.InsertRom(CreateImage(), "NROM (Japan).nes");
        var compiledBoardId = machine.Famicom.CompiledLabCompilationId;
        Assert.NotNull(compiledBoardId);
        Assert.IsType<NromCartridge>(machine.Slot.Cartridge);
        Assert.Equal(2, machine.Famicom.CompiledLabRuntimeUnitCount);

        machine.EjectRom();

        Assert.Equal(compiledBoardId, machine.Famicom.CompiledLabCompilationId);
        Assert.Equal(1, machine.Famicom.CompiledLabRuntimeUnitCount);

        machine.InsertRom(CreateMmc1Image(), "MMC1 (Japan).nes");

        Assert.Equal(compiledBoardId, machine.Famicom.CompiledLabCompilationId);
        Assert.IsType<Mmc1Cartridge>(machine.Slot.Cartridge);
        Assert.Equal(2, machine.Famicom.CompiledLabRuntimeUnitCount);
        Assert.Contains(machine.Slot.Cartridge!, machine.Famicom.Board.Components);
    }

    [Fact]
    public void Compiled_mmc1_cartridge_matches_physical_reference_while_bank_switching()
    {
        var image = CreateMmc1ExecutionImage();
        var compiled = new RegionalNesVirtualMachine();
        var reference = new RegionalNesVirtualMachine();

        compiled.SetCompiledLabExecutionEnabled(true);
        compiled.InsertRom(image, "MMC1 Execution (Japan).nes");
        reference.InsertRom(image, "MMC1 Execution (Japan).nes");
        reference.Famicom.SetCompiledPhysicalMachineEnabled(false);

        compiled.PowerOn();
        reference.PowerOn();
        compiled.ReleaseReset();
        reference.ReleaseReset();

        const int masterCycles = 12_000;
        compiled.AdvanceMasterCycles(masterCycles);
        reference.AdvanceMasterCycles(masterCycles);

        var compiledCartridge = Assert.IsType<Mmc1Cartridge>(compiled.Slot.Cartridge);
        var referenceCartridge = Assert.IsType<Mmc1Cartridge>(reference.Slot.Cartridge);
        Assert.Equal((byte)0x02, compiledCartridge.PrgBankRegister);
        Assert.Equal((byte)0x42, compiled.Famicom.Cpu.Accumulator);
        Assert.Equal(referenceCartridge.PrgBankRegister, compiledCartridge.PrgBankRegister);
        Assert.Equal(referenceCartridge.MapperWriteCount, compiledCartridge.MapperWriteCount);
        Assert.Equal(referenceCartridge.MapperCommitCount, compiledCartridge.MapperCommitCount);
        Assert.Equal(referenceCartridge.MapperResetWriteCount, compiledCartridge.MapperResetWriteCount);
        Assert.Equal(referenceCartridge.MapperWriteHash, compiledCartridge.MapperWriteHash);
        Assert.Equal(reference.Famicom.Cpu.Accumulator, compiled.Famicom.Cpu.Accumulator);
        Assert.Equal(reference.Famicom.Cpu.ProgramCounter, compiled.Famicom.Cpu.ProgramCounter);
        Assert.Equal(reference.Famicom.Cpu.CompletedInstructionCount, compiled.Famicom.Cpu.CompletedInstructionCount);
        Assert.Equal(reference.Famicom.Ppu.Scanline, compiled.Famicom.Ppu.Scanline);
        Assert.Equal(reference.Famicom.Ppu.Dot, compiled.Famicom.Ppu.Dot);
        Assert.Equal(reference.Famicom.Ppu.InspectDiagnosticState(), compiled.Famicom.Ppu.InspectDiagnosticState());
        Assert.Equal(reference.Famicom.Ciram.InspectStateHash(), compiled.Famicom.Ciram.InspectStateHash());
    }


    [Fact]
    public void Compiled_and_physical_mmc1_chr_commits_land_on_the_same_ppu_read_boundary()
    {
        var image = CreateMmc1ChrSwitchTimingImage();
        var compiled = new RegionalNesVirtualMachine();
        var reference = new RegionalNesVirtualMachine();

        compiled.SetCompiledLabExecutionEnabled(true);
        compiled.InsertRom(image, "MMC1 CHR Timing (Japan).nes");
        reference.InsertRom(image, "MMC1 CHR Timing (Japan).nes");
        reference.Famicom.SetCompiledPhysicalMachineEnabled(false);

        var compiledCartridge = Assert.IsType<Mmc1Cartridge>(compiled.Slot.Cartridge);
        var referenceCartridge = Assert.IsType<Mmc1Cartridge>(reference.Slot.Cartridge);
        compiledCartridge.RegisterTraceOutput.SetCaptureEnabled(true);
        referenceCartridge.RegisterTraceOutput.SetCaptureEnabled(true);

        compiled.PowerOn();
        reference.PowerOn();
        compiled.ReleaseReset();
        reference.ReleaseReset();

        const int masterCycles = 80_000;
        compiled.AdvanceMasterCycles(masterCycles);
        reference.AdvanceMasterCycles(masterCycles);

        var compiledCommits = compiledCartridge.RegisterTraceOutput.Drain()
            .Where(evt => evt.Operation == Mmc1RegisterOperation.ChrBank1)
            .ToArray();
        var referenceCommits = referenceCartridge.RegisterTraceOutput.Drain()
            .Where(evt => evt.Operation == Mmc1RegisterOperation.ChrBank1)
            .ToArray();

        Assert.True(referenceCommits.Length >= 8);
        Assert.Equal(referenceCommits.Length, compiledCommits.Length);
        for (var index = 0; index < referenceCommits.Length; index++)
        {
            Assert.Equal(referenceCommits[index].MapperWriteCount, compiledCommits[index].MapperWriteCount);
            Assert.Equal(referenceCommits[index].ChrBank1, compiledCommits[index].ChrBank1);
            Assert.Equal(referenceCommits[index].PpuReadCountAtCommit, compiledCommits[index].PpuReadCountAtCommit);
        }

        Assert.Equal(reference.Famicom.Cpu.ProgramCounter, compiled.Famicom.Cpu.ProgramCounter);
        Assert.Equal(reference.Famicom.Ppu.Scanline, compiled.Famicom.Ppu.Scanline);
        Assert.Equal(reference.Famicom.Ppu.Dot, compiled.Famicom.Ppu.Dot);
        Assert.Equal(reference.Famicom.Ppu.InspectDiagnosticState(), compiled.Famicom.Ppu.InspectDiagnosticState());
        Assert.Equal(reference.Famicom.Ciram.InspectStateHash(), compiled.Famicom.Ciram.InspectStateHash());
    }

    [Fact]
    public void Compiled_and_physical_mmc1_ignore_second_rmw_serial_write_cycle()
    {
        var image = CreateMmc1RmwExecutionImage();
        var compiled = new RegionalNesVirtualMachine();
        var reference = new RegionalNesVirtualMachine();

        compiled.SetCompiledLabExecutionEnabled(true);
        compiled.InsertRom(image, "MMC1 RMW (Japan).nes");
        reference.InsertRom(image, "MMC1 RMW (Japan).nes");
        reference.Famicom.SetCompiledPhysicalMachineEnabled(false);

        compiled.PowerOn();
        reference.PowerOn();
        compiled.ReleaseReset();
        reference.ReleaseReset();

        const int masterCycles = 6_000;
        compiled.AdvanceMasterCycles(masterCycles);
        reference.AdvanceMasterCycles(masterCycles);

        var compiledCartridge = Assert.IsType<Mmc1Cartridge>(compiled.Slot.Cartridge);
        var referenceCartridge = Assert.IsType<Mmc1Cartridge>(reference.Slot.Cartridge);
        Assert.Equal((byte)0x10, compiledCartridge.SerialShiftRegister);
        Assert.Equal((byte)0x10, referenceCartridge.SerialShiftRegister);
        Assert.True(compiledCartridge.IgnoredConsecutiveMapperWriteCount > 0);
        Assert.Equal(referenceCartridge.IgnoredConsecutiveMapperWriteCount, compiledCartridge.IgnoredConsecutiveMapperWriteCount);
        Assert.Equal(referenceCartridge.MapperWriteCount, compiledCartridge.MapperWriteCount);
        Assert.Equal(referenceCartridge.MapperWriteHash, compiledCartridge.MapperWriteHash);
        Assert.Equal(reference.Famicom.Cpu.ProgramCounter, compiled.Famicom.Cpu.ProgramCounter);
        Assert.Equal(reference.Famicom.Cpu.CompletedInstructionCount, compiled.Famicom.Cpu.CompletedInstructionCount);
    }

    [Fact]
    public void Unsupported_mapper_is_rejected_before_power_is_applied()
    {
        var machine = new RegionalNesVirtualMachine();
        var image = CreateImage() with { MapperNumber = 3 };

        Assert.Throws<NotSupportedException>(() => machine.InsertRom(image, "CNROM (USA).nes"));
        Assert.False(machine.IsPowered);
    }




    private static VirtualHardwareNesRomImage CreateMmc1ChrSwitchTimingImage()
    {
        var prg = new byte[4 * 16 * 1024];
        for (var bank = 0; bank < 3; bank++)
            Array.Fill(prg, (byte)(0x40 + bank), bank * 16 * 1024, 16 * 1024);

        var program = new List<byte>
        {
            0x78,                         // SEI
            0xA9, 0x10,                   // LDA #$10 - background pattern table at $1000
            0x8D, 0x00, 0x20,             // STA $2000
            0xA9, 0x08,                   // LDA #$08 - background rendering on
            0x8D, 0x01, 0x20              // STA $2001
        };

        var loopAddress = (ushort)(0xC000 + program.Count);
        program.AddRange(new byte[]
        {
            0xA9, 0x00,
            0x8D, 0x00, 0xC0,
            0x8D, 0x00, 0xC0,
            0x8D, 0x00, 0xC0,
            0x8D, 0x00, 0xC0,
            0x8D, 0x00, 0xC0,             // commit CHR1=$00
            0xA9, 0x01,
            0x8D, 0x00, 0xC0,
            0x8D, 0x00, 0xC0,
            0x8D, 0x00, 0xC0,
            0x8D, 0x00, 0xC0,
            0x8D, 0x00, 0xC0,             // commit CHR1=$1F
            0x4C, (byte)loopAddress, (byte)(loopAddress >> 8)
        });

        var fixedBank = 3 * 16 * 1024;
        program.CopyTo(prg, fixedBank);
        prg[fixedBank + 0x3FFA] = 0x00; prg[fixedBank + 0x3FFB] = 0xC0;
        prg[fixedBank + 0x3FFC] = 0x00; prg[fixedBank + 0x3FFD] = 0xC0;
        prg[fixedBank + 0x3FFE] = 0x00; prg[fixedBank + 0x3FFF] = 0xC0;

        var chr = new byte[8 * 1024];
        Array.Fill(chr, (byte)0x55, 0, 4 * 1024);
        Array.Fill(chr, (byte)0xAA, 4 * 1024, 4 * 1024);
        return new VirtualHardwareNesRomImage(
            VirtualHardwareNesHeaderFormat.INes,
            MapperNumber: 1,
            SubmapperNumber: null,
            PrgRomSizeBytes: prg.Length,
            ChrRomSizeBytes: chr.Length,
            HasTrainer: false,
            HasBatteryBackedMemory: false,
            VirtualHardwareNesMirroring.Horizontal,
            VirtualHardwareNesHeaderTiming.Unknown,
            prg,
            chr);
    }

    private static VirtualHardwareNesRomImage CreateMmc1RmwExecutionImage()
    {
        var prg = new byte[4 * 16 * 1024];
        for (var bank = 0; bank < 3; bank++)
            Array.Fill(prg, (byte)(0x40 + bank), bank * 16 * 1024, 16 * 1024);

        var fixedBank = 3 * 16 * 1024;
        var program = new byte[]
        {
            0x78,                   // SEI
            0xEE, 0x00, 0xE1,       // INC $E100 -> writes $FF then $00 on consecutive cycles
            0x4C, 0x04, 0xC0        // JMP $C004
        };
        program.CopyTo(prg, fixedBank);
        prg[fixedBank + 0x2100] = 0xFF;
        prg[fixedBank + 0x3FFA] = 0x00; prg[fixedBank + 0x3FFB] = 0xC0;
        prg[fixedBank + 0x3FFC] = 0x00; prg[fixedBank + 0x3FFD] = 0xC0;
        prg[fixedBank + 0x3FFE] = 0x00; prg[fixedBank + 0x3FFF] = 0xC0;

        return new VirtualHardwareNesRomImage(
            VirtualHardwareNesHeaderFormat.INes,
            MapperNumber: 1,
            SubmapperNumber: null,
            PrgRomSizeBytes: prg.Length,
            ChrRomSizeBytes: 8 * 1024,
            HasTrainer: false,
            HasBatteryBackedMemory: false,
            VirtualHardwareNesMirroring.Horizontal,
            VirtualHardwareNesHeaderTiming.Unknown,
            prg,
            new byte[8 * 1024]);
    }

    private static VirtualHardwareNesRomImage CreateMmc1ExecutionImage()
    {
        var prg = new byte[4 * 16 * 1024];
        for (var bank = 0; bank < 3; bank++)
            Array.Fill(prg, (byte)(0x40 + bank), bank * 16 * 1024, 16 * 1024);

        var program = new byte[]
        {
            0x78,                         // SEI
            0xA9, 0x80,                   // LDA #$80
            0x8D, 0x00, 0x80,             // STA $8000 - serial reset / fixed-last mode
            0xA9, 0x00, 0x8D, 0x00, 0xE0, // PRG bank value 2, bit 0
            0xA9, 0x01, 0x8D, 0x00, 0xE0, // bit 1
            0xA9, 0x00, 0x8D, 0x00, 0xE0, // bit 2
            0xA9, 0x00, 0x8D, 0x00, 0xE0, // bit 3
            0xA9, 0x00, 0x8D, 0x00, 0xE0, // bit 4 / commit bank 2
            0xAD, 0x00, 0x80,             // LDA $8000 -> first byte of bank 2 = $42
            0x4C, 0x22, 0xC0              // JMP $C022, keep A stable
        };
        var fixedBank = 3 * 16 * 1024;
        program.CopyTo(prg, fixedBank);
        prg[fixedBank + 0x3FFA] = 0x00; prg[fixedBank + 0x3FFB] = 0xC0;
        prg[fixedBank + 0x3FFC] = 0x00; prg[fixedBank + 0x3FFD] = 0xC0;
        prg[fixedBank + 0x3FFE] = 0x00; prg[fixedBank + 0x3FFF] = 0xC0;

        return new VirtualHardwareNesRomImage(
            VirtualHardwareNesHeaderFormat.INes,
            MapperNumber: 1,
            SubmapperNumber: null,
            PrgRomSizeBytes: prg.Length,
            ChrRomSizeBytes: 8 * 1024,
            HasTrainer: false,
            HasBatteryBackedMemory: false,
            VirtualHardwareNesMirroring.Horizontal,
            VirtualHardwareNesHeaderTiming.Unknown,
            prg,
            new byte[8 * 1024]);
    }

    private static VirtualHardwareNesRomImage CreateMmc1Image()
    {
        var prg = new byte[4 * 16 * 1024];
        for (var bank = 0; bank < 4; bank++)
            Array.Fill(prg, (byte)(0x20 + bank), bank * 16 * 1024, 16 * 1024);

        var lastBank = 3 * 16 * 1024;
        prg[lastBank + 0x3FFA] = 0x00; prg[lastBank + 0x3FFB] = 0xC0;
        prg[lastBank + 0x3FFC] = 0x00; prg[lastBank + 0x3FFD] = 0xC0;
        prg[lastBank + 0x3FFE] = 0x00; prg[lastBank + 0x3FFF] = 0xC0;

        var chr = new byte[8 * 1024];
        return new VirtualHardwareNesRomImage(
            VirtualHardwareNesHeaderFormat.INes,
            MapperNumber: 1,
            SubmapperNumber: null,
            PrgRomSizeBytes: prg.Length,
            ChrRomSizeBytes: chr.Length,
            HasTrainer: false,
            HasBatteryBackedMemory: false,
            VirtualHardwareNesMirroring.Horizontal,
            VirtualHardwareNesHeaderTiming.Unknown,
            prg,
            chr);
    }

    private static VirtualHardwareNesRomImage CreatePulseAudioImage()
    {
        var prg = new byte[16 * 1024];
        var program = new byte[]
        {
            0x78,                   // SEI
            0xA9, 0x01,             // LDA #$01
            0x8D, 0x15, 0x40,       // STA $4015 - enable pulse 1
            0xA9, 0xBF,             // LDA #$BF - duty + constant volume 15
            0x8D, 0x00, 0x40,       // STA $4000
            0xA9, 0x20,             // LDA #$20 - timer low
            0x8D, 0x02, 0x40,       // STA $4002
            0xA9, 0x08,             // LDA #$08 - timer high + length reload
            0x8D, 0x03, 0x40,       // STA $4003
            0x4C, 0x15, 0x80        // JMP $8015
        };
        program.CopyTo(prg, 0);
        prg[0x3FFA] = 0x00; prg[0x3FFB] = 0x80;
        prg[0x3FFC] = 0x00; prg[0x3FFD] = 0x80;
        prg[0x3FFE] = 0x00; prg[0x3FFF] = 0x80;

        return new VirtualHardwareNesRomImage(
            VirtualHardwareNesHeaderFormat.INes,
            MapperNumber: 0,
            SubmapperNumber: null,
            PrgRomSizeBytes: 16 * 1024,
            ChrRomSizeBytes: 8 * 1024,
            HasTrainer: false,
            HasBatteryBackedMemory: false,
            VirtualHardwareNesMirroring.Horizontal,
            VirtualHardwareNesHeaderTiming.Unknown,
            prg,
            new byte[8 * 1024]);
    }

    private static VirtualHardwareNesRomImage CreateImage() => new(
        VirtualHardwareNesHeaderFormat.INes,
        MapperNumber: 0,
        SubmapperNumber: null,
        PrgRomSizeBytes: 16 * 1024,
        ChrRomSizeBytes: 8 * 1024,
        HasTrainer: false,
        HasBatteryBackedMemory: false,
        VirtualHardwareNesMirroring.Horizontal,
        VirtualHardwareNesHeaderTiming.Unknown,
        new byte[16 * 1024],
        new byte[8 * 1024]);
}
