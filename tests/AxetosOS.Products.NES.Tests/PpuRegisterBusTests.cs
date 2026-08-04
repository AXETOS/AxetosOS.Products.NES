using AxetosOS.Products.NES.Cartridges;
using AxetosOS.Products.NES.Hardware;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class PpuRegisterBusTests
{
    [Fact]
    public void WriteOnlyPpuRegistersReturnTheCurrentIoDataBusValue()
    {
        var ppu = CreatePpu();

        ppu.CpuWrite(0x2000, 0xA5);

        Assert.Equal((byte)0xA5, ppu.CpuRead(0x2001));
        Assert.Equal((byte)0xA5, ppu.CpuRead(0x2003));
        Assert.Equal((byte)0xA5, ppu.CpuRead(0x2005));
        Assert.Equal((byte)0xA5, ppu.CpuRead(0x2006));
    }

    [Fact]
    public void StatusReadCombinesHardwareFlagsWithIoDataBusLowBits()
    {
        var ppu = CreatePpu();
        for (var i = 0; i < 256; i++)
        {
            ppu.WriteOamDmaByte(0xFF);
        }
        ppu.CpuWrite(0x2000, 0x1B);
        ClockUntil(ppu, 0, 241, 2);

        var status = ppu.CpuRead(0x2002);

        Assert.Equal((byte)0x9B, status);
        Assert.Equal((byte)0x9B, ppu.IoDataBus);
        Assert.False(ppu.InVBlank);
        Assert.False(ppu.WriteToggle);
    }

    [Fact]
    public void WritesToReadOnlyStatusRegisterStillDriveTheIoDataBus()
    {
        var ppu = CreatePpu();

        ppu.CpuWrite(0x2002, 0x6D);

        Assert.Equal((byte)0x6D, ppu.CpuRead(0x2000));
    }

    [Fact]
    public void PaletteReadUsesOpenBusHighBitsAndRefillsBufferedNametableRead()
    {
        var (ppu, bus) = CreatePpuWithBus();
        bus.Write(0x3F00, 0x2A);
        bus.Write(0x2F00, 0x5C);
        WritePpuAddress(ppu, 0x3F00);
        ppu.CpuWrite(0x2002, 0xC0); // Prime the I/O latch without changing v.

        var palette = ppu.CpuRead(0x2007);
        WritePpuAddress(ppu, 0x2000);
        var buffered = ppu.CpuRead(0x2007);

        Assert.Equal((byte)0xEA, palette);
        Assert.Equal((byte)0x5C, buffered);
    }

    [Fact]
    public void GreyscaleMaskRestrictsPaletteDataReadToEmphasisRange()
    {
        var (ppu, bus) = CreatePpuWithBus();
        bus.Write(0x3F00, 0x2F);
        ppu.CpuWrite(0x2001, 0x01);
        WritePpuAddress(ppu, 0x3F00);
        ppu.CpuWrite(0x2002, 0x80); // Prime the I/O latch without changing v.

        Assert.Equal((byte)0xA0, ppu.CpuRead(0x2007));
    }

    private static void WritePpuAddress(Rp2C02Ppu ppu, ushort address)
    {
        _ = ppu.CpuRead(0x2002);
        ppu.CpuWrite(0x2006, (byte)(address >> 8));
        ppu.CpuWrite(0x2006, (byte)address);
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

    private static Rp2C02Ppu CreatePpu() => CreatePpuWithBus().Ppu;

    private static (Rp2C02Ppu Ppu, PpuBus Bus) CreatePpuWithBus()
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
        return (ppu, bus);
    }
}
