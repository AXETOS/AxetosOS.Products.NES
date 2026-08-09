using Xunit;
using AxetosOS.Products.NES.VirtualHardware.Loading;
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
