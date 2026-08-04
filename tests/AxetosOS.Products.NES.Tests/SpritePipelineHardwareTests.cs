using AxetosOS.Products.NES.Cartridges;
using AxetosOS.Products.NES.Hardware;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class SpritePipelineHardwareTests
{
    [Fact]
    public void EvaluatedSecondaryOamFeedsTheVisibleSpriteOutputUnits()
    {
        var image = CreateNromImage();
        for (var row = 0; row < 8; row++)
        {
            image.ChrRom[16 + row] = 0xFF; // opaque background tile 1
            image.ChrRom[32 + row] = 0x80; // one opaque pixel in sprite tile 2
        }

        var (ppu, bus) = CreatePpu(image);
        for (var address = 0x2000; address < 0x3000; address++)
        {
            bus.Write((ushort)address, 1);
        }
        bus.Write(0x3F01, 0x01);
        bus.Write(0x3F11, 0x30);

        ppu.CpuWrite(0x2003, 0x00);
        ppu.CpuWrite(0x2004, 0x00); // top = scanline 1
        ppu.CpuWrite(0x2004, 0x02);
        ppu.CpuWrite(0x2004, 0x00);
        ppu.CpuWrite(0x2004, 0x10);
        ppu.CpuWrite(0x2001, 0x1E);

        ClockUntil(ppu, frame: 1, scanline: 1, dot: 32);

        Assert.True((ppu.Status & 0x40) != 0);
        Assert.NotEqual(ppu.Framebuffer[(1 * 256) + 15], ppu.Framebuffer[(1 * 256) + 16]);
    }

    [Fact]
    public void SpritePriorityUsesPrimaryOamOrderAfterSecondaryOamEvaluation()
    {
        var image = CreateNromImage();
        for (var row = 0; row < 8; row++)
        {
            image.ChrRom[32 + row] = 0x80;
            image.ChrRom[48 + row] = 0x80;
        }

        var (ppu, bus) = CreatePpu(image);
        bus.Write(0x3F11, 0x11);
        bus.Write(0x3F15, 0x30);

        ppu.CpuWrite(0x2003, 0x00);
        WriteSprite(ppu, y: 0x00, tile: 0x02, attributes: 0x00, x: 0x20);
        WriteSprite(ppu, y: 0x00, tile: 0x03, attributes: 0x01, x: 0x20);
        // Direct OAMDATA writes leave OAMADDR at $08. Real software normally
        // performs a complete 256-byte DMA, which wraps OAMADDR to zero before
        // rendering. Reset it here so this test isolates primary-OAM priority
        // rather than the separate nonzero-OAMADDR rotation behavior.
        ppu.CpuWrite(0x2003, 0x00);
        ppu.CpuWrite(0x2001, 0x14);

        ClockUntil(ppu, frame: 1, scanline: 1, dot: 40);

        var firstSpriteColor = ppu.Framebuffer[(1 * 256) + 0x20];
        Assert.Equal(0xFF084CC4U, firstSpriteColor);
    }

    [Fact]
    public void HorizontalFlipIsAppliedWhenPatternBytesEnterTheSpriteShiftRegisters()
    {
        var image = CreateNromImage();
        image.ChrRom[32] = 0x80;

        var (ppu, bus) = CreatePpu(image);
        bus.Write(0x3F11, 0x30);

        ppu.CpuWrite(0x2003, 0x00);
        WriteSprite(ppu, y: 0x00, tile: 0x02, attributes: 0x40, x: 0x20);
        ppu.CpuWrite(0x2001, 0x14);

        ClockUntil(ppu, frame: 1, scanline: 1, dot: 48);

        var backdrop = ppu.Framebuffer[(1 * 256) + 0x20];
        var flippedPixel = ppu.Framebuffer[(1 * 256) + 0x27];
        Assert.NotEqual(backdrop, flippedPixel);
    }

    private static void WriteSprite(Rp2C02Ppu ppu, byte y, byte tile, byte attributes, byte x)
    {
        ppu.CpuWrite(0x2004, y);
        ppu.CpuWrite(0x2004, tile);
        ppu.CpuWrite(0x2004, attributes);
        ppu.CpuWrite(0x2004, x);
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

    private static (Rp2C02Ppu Ppu, PpuBus Bus) CreatePpu(NesRomImage image)
    {
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
        var ppu = new Rp2C02Ppu(bus, new SignalLine());
        ppu.PowerOn();
        return (ppu, bus);
    }

    private static NesRomImage CreateNromImage() => new(
        NesHeaderFormat.INes,
        MapperNumber: 0,
        SubmapperNumber: null,
        PrgRomSizeBytes: 16 * 1024,
        ChrRomSizeBytes: 8 * 1024,
        HasTrainer: false,
        HasBatteryBackedMemory: false,
        Mirroring: NametableMirroring.Horizontal,
        HeaderTiming: NesTimingMode.Unknown,
        PrgRom: new byte[16 * 1024],
        ChrRom: new byte[8 * 1024]);
}
