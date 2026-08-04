using AxetosOS.Products.NES.Cartridges;
using AxetosOS.Products.NES.Hardware;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class PpuMidScanlineOamTests
{
    [Fact]
    public void OamDataBusDrivesFfDuringSecondaryOamClear()
    {
        var ppu = CreatePpu();
        ppu.CpuWrite(0x2001, 0x18);
        ClockUntil(ppu, 0, 0, 20);

        Assert.Equal((byte)0xFF, ppu.CpuRead(0x2004));
    }

    [Fact]
    public void OamDataBusExposesSecondaryOamBytesDuringSpriteFetch()
    {
        var ppu = CreatePpu();
        ppu.CpuWrite(0x2003, 0x00);
        ppu.CpuWrite(0x2004, 0x00);
        ppu.CpuWrite(0x2004, 0x22);
        ppu.CpuWrite(0x2004, 0x43);
        ppu.CpuWrite(0x2004, 0x55);
        ppu.CpuWrite(0x2003, 0x00);
        ppu.CpuWrite(0x2001, 0x18);

        ClockUntil(ppu, 0, 0, 257);
        Assert.Equal((byte)0x00, ppu.CpuRead(0x2004));
        ClockUntil(ppu, 0, 0, 259);
        Assert.Equal((byte)0x22, ppu.CpuRead(0x2004));
        ClockUntil(ppu, 0, 0, 261);
        Assert.Equal((byte)0x43, ppu.CpuRead(0x2004));
        ClockUntil(ppu, 0, 0, 263);
        Assert.Equal((byte)0x55, ppu.CpuRead(0x2004));
    }

    [Fact]
    public void EnablingRenderingInsideSpriteFetchWindowJoinsCurrentPhaseAndZerosOamAddress()
    {
        var ppu = CreatePpu();
        ClockUntil(ppu, 0, 0, 270);
        ppu.CpuWrite(0x2003, 0x44);
        ppu.CpuWrite(0x2001, 0x18);

        ppu.Clock();

        Assert.Equal((byte)0x00, ppu.OamAddress);
    }

    private static void ClockUntil(Rp2C02Ppu ppu, ulong frame, int scanline, int dot)
    {
        var safety = 3 * 312 * 341;
        while ((ppu.Frame < frame || ppu.Scanline < scanline || (ppu.Scanline == scanline && ppu.Dot < dot)) && safety-- > 0)
        {
            ppu.Clock();
        }
        Assert.True(safety > 0);
    }

    private static Rp2C02Ppu CreatePpu()
    {
        var image = new NesRomImage(
            NesHeaderFormat.INes, 0, null, 16 * 1024, 8 * 1024,
            false, false, NametableMirroring.Horizontal, NesTimingMode.Unknown,
            new byte[16 * 1024], new byte[8 * 1024]);
        var bus = new PpuBus();
        var chr = new NromChrMemory(image);
        var ciram = new CiramNametableRam(image.Mirroring);
        var palette = new PpuPaletteRam();
        chr.PowerOn();
        ciram.PowerOn();
        palette.PowerOn();
        bus.Attach(chr);
        bus.Attach(ciram);
        bus.Attach(palette);
        var ppu = new Rp2C02Ppu(bus, new SignalLine(), NesTimingProfile.For(NesTimingMode.Ntsc));
        ppu.PowerOn();
        return ppu;
    }
}
