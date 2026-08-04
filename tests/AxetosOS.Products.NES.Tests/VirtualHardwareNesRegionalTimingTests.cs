using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareNesRegionalTimingTests
{
    [Theory]
    [InlineData(NesHardwareRegion.NtscNorthAmerica, "NTSC-U", 1_789_773L, 5_369_318L, 262, 261)]
    [InlineData(NesHardwareRegion.NtscJapan, "NTSC-J", 1_789_773L, 5_369_318L, 262, 261)]
    [InlineData(NesHardwareRegion.Pal, "PAL", 1_662_607L, 5_320_342L, 312, 311)]
    public void Motherboard_uses_selected_regional_hardware_profile(
        NesHardwareRegion region,
        string displayName,
        long cpuClock,
        long ppuClock,
        int scanlines,
        int preRenderScanline)
    {
        var machine = new NesCpuMotherboard(CreatePrg(), region);

        Assert.Equal(region, machine.Region);
        Assert.Equal(displayName, machine.TimingProfile.DisplayName);
        Assert.Equal(cpuClock, machine.Clock.FrequencyHertz);
        Assert.Equal(ppuClock, machine.PpuClock.FrequencyHertz);
        Assert.Equal(scanlines, machine.PpuTiming.ScanlinesPerFrame);
        Assert.Equal(preRenderScanline, machine.PpuTiming.PreRenderScanline);
    }

    [Fact]
    public void Ntsc_u_and_ntsc_j_share_timing_but_remain_distinct_hardware_profiles()
    {
        var ntscU = NesHardwareTimingProfile.NtscNorthAmerica;
        var ntscJ = NesHardwareTimingProfile.NtscJapan;

        Assert.NotEqual(ntscU.Region, ntscJ.Region);
        Assert.Equal(ntscU.CpuClockHertz, ntscJ.CpuClockHertz);
        Assert.Equal(ntscU.PpuClockHertz, ntscJ.PpuClockHertz);
        Assert.Equal(ntscU.ScanlinesPerFrame, ntscJ.ScanlinesPerFrame);
    }

    [Fact]
    public void Pal_phase_accumulator_advances_sixteen_ppu_half_cycles_for_five_cpu_half_cycles()
    {
        var machine = new NesCpuMotherboard(CreatePrg(), NesHardwareRegion.Pal);
        machine.PowerOn();
        for (var halfCycle = 0; halfCycle < 5; halfCycle++)
        {
            machine.AdvanceHalfCycle();
        }

        Assert.Equal(16UL, machine.PpuClock.HalfCycleCount);
        Assert.Equal(5UL, machine.Clock.HalfCycleCount);
    }

    [Fact]
    public void Motherboard_defaults_to_north_american_ntsc_hardware()
    {
        var machine = new NesCpuMotherboard(CreatePrg());

        Assert.Equal(NesHardwareRegion.NtscNorthAmerica, machine.Region);
        Assert.Same(NesHardwareTimingProfile.NtscNorthAmerica, machine.TimingProfile);
    }

    private static byte[] CreatePrg()
    {
        var prg = new byte[32 * 1024];
        prg[0] = 0x00;
        prg[0x7FFC] = 0x00;
        prg[0x7FFD] = 0x80;
        return prg;
    }
}
