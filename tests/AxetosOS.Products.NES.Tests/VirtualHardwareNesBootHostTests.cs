using Xunit;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Components.Nes;
using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Machines.Nes;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareNesBootHostTests
{
    [Fact]
    public void Nrom_boot_host_observes_reset_vector_and_real_cpu_execution_through_slot_bus()
    {
        var prg = Enumerable.Repeat((byte)0xEA, 16 * 1024).ToArray();
        prg[0] = 0x4C; // JMP $8000
        prg[1] = 0x00;
        prg[2] = 0x80;
        prg[0x3FFC] = 0x00;
        prg[0x3FFD] = 0x80;
        var image = new VirtualHardwareNesRomImage(
            VirtualHardwareNesHeaderFormat.INes, 0, null, prg.Length, 8 * 1024,
            false, false, VirtualHardwareNesMirroring.Horizontal,
            VirtualHardwareNesHeaderTiming.Ntsc, prg, new byte[8 * 1024]);

        var video = new VirtualNesFrameBuffer();
        var audio = new VirtualNesPcmBuffer();
        var host = new VirtualNesBootHost { VideoSink = video, AudioSink = audio };
        host.LoadRom(image, "boot-test (Japan).nes", NesRegionSelection.NtscJapan);
        host.PowerAndReleaseReset();
        var result = host.RunUntil(d => d.CpuInstructions >= 2, 2_000);

        Assert.Equal(ActiveNesMotherboard.Famicom, result.Motherboard);
        Assert.True(result.ResetVectorObserved);
        Assert.True(result.FirstOpcodeObserved);
        Assert.True(result.CartridgeCpuReads >= 4);
        Assert.True(result.CpuInstructions >= 2);
        Assert.Equal((ushort)0x8000, result.ProgramCounter);
        Assert.NotEmpty(audio.Samples);
        Assert.True(video.WrittenPixelCount > 0);
    }

    [Fact]
    public void Boot_host_rejects_unsupported_mapper_before_power_is_applied()
    {
        var image = new VirtualHardwareNesRomImage(
            VirtualHardwareNesHeaderFormat.INes, 2, null, 32 * 1024, 8 * 1024,
            false, false, VirtualHardwareNesMirroring.Horizontal,
            VirtualHardwareNesHeaderTiming.Ntsc, new byte[32 * 1024], new byte[8 * 1024]);
        var host = new VirtualNesBootHost();
        Assert.Throws<NotSupportedException>(() => host.LoadRom(image, "mapper2.nes"));
        Assert.False(host.Machine.IsPowered);
    }

    [Fact]
    public void Boot_host_preserves_specialized_nrom_compiler_as_the_default_production_runtime()
    {
        var image = CreateImage(0);
        var host = new VirtualNesBootHost();

        host.LoadRom(image, "NROM Production (Japan).nes", NesRegionSelection.NtscJapan);

        Assert.False(host.Machine.IsPowered);
        Assert.True(host.Machine.Famicom.CompiledPhysicalMachineEnabled);
        Assert.False(host.Machine.Famicom.CompiledLabMotherboardEnabled);
    }

    [Fact]
    public void Boot_host_automatically_compiles_mmc1_before_power_is_applied()
    {
        var image = CreateImage(1);
        var host = new VirtualNesBootHost();

        host.LoadRom(image, "MMC1 Production (Japan).nes", NesRegionSelection.NtscJapan);

        Assert.False(host.Machine.IsPowered);
        Assert.False(host.Machine.Famicom.CompiledPhysicalMachineEnabled);
        Assert.True(host.Machine.Famicom.CompiledLabMotherboardEnabled);
        Assert.Equal(2, host.Machine.Famicom.CompiledLabRuntimeUnitCount);
    }

    [Fact]
    public void Boot_host_can_explicitly_leave_mmc1_on_the_raw_physical_runtime_for_diagnostics()
    {
        var image = CreateImage(1);
        var host = new VirtualNesBootHost { AutomaticCompiledExecutionEnabled = false };

        host.LoadRom(image, "MMC1 Raw Diagnostic (Japan).nes", NesRegionSelection.NtscJapan);

        Assert.False(host.Machine.IsPowered);
        Assert.False(host.Machine.Famicom.CompiledPhysicalMachineEnabled);
        Assert.False(host.Machine.Famicom.CompiledLabMotherboardEnabled);
    }

    [Fact]
    public void Boot_host_machine_state_restores_exact_hardware_point_and_replays_deterministically()
    {
        var prg = Enumerable.Repeat((byte)0xEA, 16 * 1024).ToArray();
        prg[0] = 0xE8; // INX
        prg[1] = 0x8E; // STX $0000
        prg[2] = 0x00;
        prg[3] = 0x00;
        prg[4] = 0x4C; // JMP $8000
        prg[5] = 0x00;
        prg[6] = 0x80;
        prg[0x3FFC] = 0x00;
        prg[0x3FFD] = 0x80;
        var image = new VirtualHardwareNesRomImage(
            VirtualHardwareNesHeaderFormat.INes, 0, null, prg.Length, 8 * 1024,
            false, false, VirtualHardwareNesMirroring.Horizontal,
            VirtualHardwareNesHeaderTiming.Ntsc, prg, new byte[8 * 1024]);

        var host = new VirtualNesBootHost();
        host.LoadRom(image, "save-state-test (Japan).nes", NesRegionSelection.NtscJapan);
        host.PowerAndReleaseReset();
        host.AdvanceMasterCycles(80_000);

        var savedDiagnostics = host.Snapshot();
        var savedPpu = host.Machine.Famicom.Ppu.InspectDiagnosticState();
        var savedCpuRam = host.Machine.Famicom.CpuRam.InspectStateHash();
        var savedCiram = host.Machine.Famicom.Ciram.InspectStateHash();
        var state = host.CaptureState();

        const int replayCycles = 120_000;
        host.AdvanceMasterCycles(replayCycles);
        var futureDiagnostics = host.Snapshot();
        var futurePpu = host.Machine.Famicom.Ppu.InspectDiagnosticState();
        var futureCpuRam = host.Machine.Famicom.CpuRam.InspectStateHash();
        var futureCiram = host.Machine.Famicom.Ciram.InspectStateHash();

        host.RestoreState(state);

        var restoredDiagnostics = host.Snapshot();
        var restoredPpu = host.Machine.Famicom.Ppu.InspectDiagnosticState();
        var restoredCpuRam = host.Machine.Famicom.CpuRam.InspectStateHash();
        var restoredCiram = host.Machine.Famicom.Ciram.InspectStateHash();
        Assert.Equal(savedDiagnostics.MasterCycles, restoredDiagnostics.MasterCycles);
        Assert.Equal(savedDiagnostics.CpuInstructions, restoredDiagnostics.CpuInstructions);
        Assert.Equal(savedDiagnostics.ProgramCounter, restoredDiagnostics.ProgramCounter);
        Assert.Equal(savedDiagnostics.CurrentOpcode, restoredDiagnostics.CurrentOpcode);
        Assert.Equal(savedDiagnostics.CpuCycleState, restoredDiagnostics.CpuCycleState);
        Assert.Equal(savedDiagnostics.PpuFrames, restoredDiagnostics.PpuFrames);
        Assert.Equal(savedPpu, restoredPpu);
        Assert.Equal(savedCpuRam, restoredCpuRam);
        Assert.Equal(savedCiram, restoredCiram);

        host.AdvanceMasterCycles(replayCycles);
        var replayDiagnostics = host.Snapshot();
        var replayPpu = host.Machine.Famicom.Ppu.InspectDiagnosticState();
        var replayCpuRam = host.Machine.Famicom.CpuRam.InspectStateHash();
        var replayCiram = host.Machine.Famicom.Ciram.InspectStateHash();

        Assert.Equal(futureDiagnostics.MasterCycles, replayDiagnostics.MasterCycles);
        Assert.Equal(futureDiagnostics.CpuInstructions, replayDiagnostics.CpuInstructions);
        Assert.Equal(futureDiagnostics.ProgramCounter, replayDiagnostics.ProgramCounter);
        Assert.Equal(futureDiagnostics.CurrentOpcode, replayDiagnostics.CurrentOpcode);
        Assert.Equal(futureDiagnostics.CpuCycleState, replayDiagnostics.CpuCycleState);
        Assert.Equal(futureDiagnostics.CartridgeCpuReads, replayDiagnostics.CartridgeCpuReads);
        Assert.Equal(futureDiagnostics.CartridgePpuReads, replayDiagnostics.CartridgePpuReads);
        Assert.Equal(futureDiagnostics.PpuFrames, replayDiagnostics.PpuFrames);
        Assert.Equal(futurePpu, replayPpu);
        Assert.Equal(futureCpuRam, replayCpuRam);
        Assert.Equal(futureCiram, replayCiram);
    }

    [Fact]
    public void Boot_host_machine_state_replays_compiled_mmc1_runtime_deterministically()
    {
        var prg = Enumerable.Repeat((byte)0xEA, 32 * 1024).ToArray();
        prg[0] = 0xE8; // INX
        prg[1] = 0x4C; // JMP $8000
        prg[2] = 0x00;
        prg[3] = 0x80;
        prg[^4] = 0x00;
        prg[^3] = 0x80;
        var image = new VirtualHardwareNesRomImage(
            VirtualHardwareNesHeaderFormat.INes, 1, null, prg.Length, 8 * 1024,
            false, false, VirtualHardwareNesMirroring.Horizontal,
            VirtualHardwareNesHeaderTiming.Ntsc, prg, new byte[8 * 1024]);

        var host = new VirtualNesBootHost();
        host.LoadRom(image, "save-state-mmc1 (Japan).nes", NesRegionSelection.NtscJapan);
        host.PowerAndReleaseReset();
        host.AdvanceMasterCycles(90_000);

        var cartridge = Assert.IsType<Mmc1Cartridge>(host.Machine.Slot.Cartridge);
        var savedDiagnostics = host.Snapshot();
        var state = host.CaptureState();
        var savedSerial = cartridge.SerialShiftRegister;
        var savedPrgBank = cartridge.PrgBankRegister;

        const int replayCycles = 130_000;
        host.AdvanceMasterCycles(replayCycles);
        var futureDiagnostics = host.Snapshot();
        var futurePpu = host.Machine.Famicom.Ppu.InspectDiagnosticState();

        host.RestoreState(state);
        var restoredDiagnostics = host.Snapshot();
        Assert.Equal(savedDiagnostics.MasterCycles, restoredDiagnostics.MasterCycles);
        Assert.Equal(savedDiagnostics.ProgramCounter, restoredDiagnostics.ProgramCounter);
        Assert.Equal(savedSerial, cartridge.SerialShiftRegister);
        Assert.Equal(savedPrgBank, cartridge.PrgBankRegister);

        host.AdvanceMasterCycles(replayCycles);
        var replayDiagnostics = host.Snapshot();
        var replayPpu = host.Machine.Famicom.Ppu.InspectDiagnosticState();
        Assert.Equal(futureDiagnostics.MasterCycles, replayDiagnostics.MasterCycles);
        Assert.Equal(futureDiagnostics.CpuInstructions, replayDiagnostics.CpuInstructions);
        Assert.Equal(futureDiagnostics.ProgramCounter, replayDiagnostics.ProgramCounter);
        Assert.Equal(futureDiagnostics.CartridgeCpuReads, replayDiagnostics.CartridgeCpuReads);
        Assert.Equal(futureDiagnostics.CartridgePpuReads, replayDiagnostics.CartridgePpuReads);
        Assert.Equal(futurePpu, replayPpu);
    }

    [Fact]
    public void Boot_host_machine_state_does_not_rewind_current_external_controller_contacts()
    {
        var image = CreateImage(0);
        var host = new VirtualNesBootHost();
        host.LoadRom(image, "controller-state (Japan).nes", NesRegionSelection.NtscJapan);
        host.PowerAndReleaseReset();
        host.Machine.SetControllerButton(0, NesControllerButton.A, true);
        Assert.NotEqual((byte)0, host.Machine.InspectControllerButtons(0));

        var state = host.CaptureState();
        host.Machine.SetControllerButton(0, NesControllerButton.A, false);
        Assert.Equal((byte)0, host.Machine.InspectControllerButtons(0));

        host.RestoreState(state);

        Assert.Equal((byte)0, host.Machine.InspectControllerButtons(0));
    }

    [Fact]
    public void Boot_host_portable_machine_state_restores_into_a_new_host_and_replays_deterministically()
    {
        var prg = Enumerable.Repeat((byte)0xEA, 16 * 1024).ToArray();
        prg[0] = 0xE8; // INX
        prg[1] = 0x8E; // STX $0000
        prg[2] = 0x00;
        prg[3] = 0x00;
        prg[4] = 0x4C; // JMP $8000
        prg[5] = 0x00;
        prg[6] = 0x80;
        prg[0x3FFC] = 0x00;
        prg[0x3FFD] = 0x80;
        var image = new VirtualHardwareNesRomImage(
            VirtualHardwareNesHeaderFormat.INes, 0, null, prg.Length, 8 * 1024,
            false, false, VirtualHardwareNesMirroring.Horizontal,
            VirtualHardwareNesHeaderTiming.Ntsc, prg, new byte[8 * 1024]);

        var first = new VirtualNesBootHost();
        first.LoadRom(image, "portable-state-test (Japan).nes", NesRegionSelection.NtscJapan);
        first.VideoSink = new VirtualNesFrameBuffer();
        first.AudioSink = new VirtualNesPcmBuffer();
        first.PowerAndReleaseReset();
        first.AdvanceMasterCycles(90_000);

        var savedDiagnostics = first.Snapshot();
        var savedPpu = first.Machine.Famicom.Ppu.InspectDiagnosticState();
        var savedCpuRam = first.Machine.Famicom.CpuRam.InspectStateHash();
        var savedCiram = first.Machine.Famicom.Ciram.InspectStateHash();
        var state = first.CapturePortableState();

        const int replayCycles = 130_000;
        first.AdvanceMasterCycles(replayCycles);
        var futureDiagnostics = first.Snapshot();
        var futurePpu = first.Machine.Famicom.Ppu.InspectDiagnosticState();
        var futureCpuRam = first.Machine.Famicom.CpuRam.InspectStateHash();
        var futureCiram = first.Machine.Famicom.Ciram.InspectStateHash();

        var second = new VirtualNesBootHost();
        second.LoadRom(image, "portable-state-test (Japan).nes", NesRegionSelection.NtscJapan);
        second.VideoSink = new VirtualNesFrameBuffer();
        second.AudioSink = new VirtualNesPcmBuffer();
        second.PowerAndReleaseReset();
        second.RestorePortableState(state);

        var restoredDiagnostics = second.Snapshot();
        Assert.Equal(savedDiagnostics.MasterCycles, restoredDiagnostics.MasterCycles);
        Assert.Equal(savedDiagnostics.CpuInstructions, restoredDiagnostics.CpuInstructions);
        Assert.Equal(savedDiagnostics.ProgramCounter, restoredDiagnostics.ProgramCounter);
        Assert.Equal(savedDiagnostics.CurrentOpcode, restoredDiagnostics.CurrentOpcode);
        Assert.Equal(savedDiagnostics.CpuCycleState, restoredDiagnostics.CpuCycleState);
        Assert.Equal(savedDiagnostics.PpuFrames, restoredDiagnostics.PpuFrames);
        Assert.Equal(savedPpu, second.Machine.Famicom.Ppu.InspectDiagnosticState());
        Assert.Equal(savedCpuRam, second.Machine.Famicom.CpuRam.InspectStateHash());
        Assert.Equal(savedCiram, second.Machine.Famicom.Ciram.InspectStateHash());

        second.AdvanceMasterCycles(replayCycles);
        var replayDiagnostics = second.Snapshot();
        Assert.Equal(futureDiagnostics.MasterCycles, replayDiagnostics.MasterCycles);
        Assert.Equal(futureDiagnostics.CpuInstructions, replayDiagnostics.CpuInstructions);
        Assert.Equal(futureDiagnostics.ProgramCounter, replayDiagnostics.ProgramCounter);
        Assert.Equal(futureDiagnostics.CartridgeCpuReads, replayDiagnostics.CartridgeCpuReads);
        Assert.Equal(futureDiagnostics.CartridgePpuReads, replayDiagnostics.CartridgePpuReads);
        Assert.Equal(futureDiagnostics.PpuFrames, replayDiagnostics.PpuFrames);
        Assert.Equal(futurePpu, second.Machine.Famicom.Ppu.InspectDiagnosticState());
        Assert.Equal(futureCpuRam, second.Machine.Famicom.CpuRam.InspectStateHash());
        Assert.Equal(futureCiram, second.Machine.Famicom.Ciram.InspectStateHash());
    }

    [Fact]
    public void Boot_host_portable_machine_state_does_not_embed_cartridge_rom_bytes()
    {
        var prg = new byte[32 * 1024];
        var chr = new byte[8 * 1024];
        var seed = 0x13579BDFu;
        for (var index = 0; index < prg.Length; index++)
        {
            seed = (seed * 1664525u) + 1013904223u;
            prg[index] = (byte)(seed >> 24);
        }
        for (var index = 0; index < chr.Length; index++)
        {
            seed = (seed * 1664525u) + 1013904223u;
            chr[index] = (byte)(seed >> 24);
        }
        prg[^4] = 0x00;
        prg[^3] = 0x80;

        var image = new VirtualHardwareNesRomImage(
            VirtualHardwareNesHeaderFormat.INes, 0, null, prg.Length, chr.Length,
            false, false, VirtualHardwareNesMirroring.Horizontal,
            VirtualHardwareNesHeaderTiming.Ntsc, prg, chr);
        var host = new VirtualNesBootHost();
        host.LoadRom(image, "portable-rom-exclusion (Japan).nes", NesRegionSelection.NtscJapan);
        host.PowerAndReleaseReset();

        var state = host.CapturePortableState();

        Assert.Equal(-1, state.AsSpan().IndexOf(prg.AsSpan(4_096, 2_048)));
        Assert.Equal(-1, state.AsSpan().IndexOf(chr.AsSpan(2_048, 2_048)));
    }

    [Fact]
    public void Boot_host_portable_machine_state_rejects_incompatible_loaded_mapper()
    {
        var nrom = CreateImage(0);
        var first = new VirtualNesBootHost();
        first.LoadRom(nrom, "portable-nrom (Japan).nes", NesRegionSelection.NtscJapan);
        first.PowerAndReleaseReset();
        first.AdvanceMasterCycles(2_000);
        var state = first.CapturePortableState();

        var mmc1 = CreateImage(1);
        var second = new VirtualNesBootHost();
        second.LoadRom(mmc1, "portable-mmc1 (Japan).nes", NesRegionSelection.NtscJapan);
        second.PowerAndReleaseReset();

        Assert.Throws<InvalidOperationException>(() => second.RestorePortableState(state));
    }

    [Fact]
    public void Boot_host_machine_state_is_bound_to_the_host_that_captured_it()
    {
        var image = CreateImage(0);
        var first = new VirtualNesBootHost();
        first.LoadRom(image, "first (Japan).nes", NesRegionSelection.NtscJapan);
        first.PowerAndReleaseReset();
        first.AdvanceMasterCycles(2_000);
        var state = first.CaptureState();

        var second = new VirtualNesBootHost();
        second.LoadRom(image, "second (Japan).nes", NesRegionSelection.NtscJapan);
        second.PowerAndReleaseReset();

        Assert.Throws<InvalidOperationException>(() => second.RestoreState(state));
    }

    private static VirtualHardwareNesRomImage CreateImage(int mapper)
    {
        var prg = new byte[32 * 1024];
        prg[^4] = 0x00;
        prg[^3] = 0x80;
        return new VirtualHardwareNesRomImage(
            VirtualHardwareNesHeaderFormat.INes, mapper, null, prg.Length, 8 * 1024,
            false, false, VirtualHardwareNesMirroring.Horizontal,
            VirtualHardwareNesHeaderTiming.Ntsc, prg, new byte[8 * 1024]);
    }
}
