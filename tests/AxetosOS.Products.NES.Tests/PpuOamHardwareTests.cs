using AxetosOS.Products.NES.Cartridges;
using AxetosOS.Products.NES.Hardware;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class PpuOamHardwareTests
{
    [Fact]
    public void OamDataWriteDuringRenderingDoesNotModifyOamAndAdvancesOneSprite()
    {
        var (ppu, _) = CreatePpu(NesTimingMode.Ntsc);
        ppu.CpuWrite(0x2003, 0x01);
        ppu.CpuWrite(0x2004, 0x55);
        ppu.CpuWrite(0x2001, 0x18);
        ClockUntil(ppu, frame: 0, scanline: 0, dot: 10);

        ppu.CpuWrite(0x2003, 0x01);
        ppu.CpuWrite(0x2004, 0xAA);

        Assert.Equal((byte)0x55, ppu.ReadOamByte(0x01));
        Assert.Equal((byte)0x05, ppu.OamAddress);
    }

    [Fact]
    public void OamDataReadDuringEvaluationExposesTheInternalOamBus()
    {
        var (ppu, _) = CreatePpu(NesTimingMode.Ntsc);
        ppu.CpuWrite(0x2003, 0x00);
        ppu.CpuWrite(0x2004, 0x12);
        ppu.CpuWrite(0x2004, 0x34);
        ppu.CpuWrite(0x2004, 0x56);
        ppu.CpuWrite(0x2004, 0x78);
        // Direct OAMDATA writes increment OAMADDR. Normal rendering starts
        // evaluation from zero after a complete OAM update/DMA, so restore the
        // address explicitly before observing the first evaluation read.
        ppu.CpuWrite(0x2003, 0x00);
        ppu.CpuWrite(0x2001, 0x18);

        ClockUntil(ppu, frame: 0, scanline: 0, dot: 66);

        Assert.Equal((byte)0x12, ppu.CpuRead(0x2004));
    }

    [Fact]
    public void SpriteFetchIntervalForcesOamAddressBackToZero()
    {
        var (ppu, _) = CreatePpu(NesTimingMode.Ntsc);
        ppu.CpuWrite(0x2001, 0x18);
        ClockUntil(ppu, frame: 0, scanline: 0, dot: 257);
        ppu.CpuWrite(0x2003, 0x44);

        ppu.Clock();

        Assert.Equal((byte)0x00, ppu.OamAddress);
    }

    [Fact]
    public void NtscEvaluationRefreshCopiesSelectedOamRowIntoFirstEightBytes()
    {
        var (ppu, _) = CreatePpu(NesTimingMode.Ntsc);
        WriteOamRange(ppu, 0x00, 0x10);
        WriteOamRange(ppu, 0x20, 0x80);
        ppu.CpuWrite(0x2003, 0x20);
        ppu.CpuWrite(0x2001, 0x18);

        ClockUntil(ppu, frame: 0, scanline: 0, dot: 66);

        for (var i = 0; i < 8; i++)
        {
            Assert.Equal((byte)(0x80 + i), ppu.ReadOamByte((byte)i));
        }
    }

    [Fact]
    public void PalPpuDoesNotApplyTheNtscOamRefreshCorruption()
    {
        var (ppu, _) = CreatePpu(NesTimingMode.Pal);
        WriteOamRange(ppu, 0x00, 0x10);
        WriteOamRange(ppu, 0x20, 0x80);
        ppu.CpuWrite(0x2003, 0x20);
        ppu.CpuWrite(0x2001, 0x18);

        ClockUntil(ppu, frame: 0, scanline: 0, dot: 66);

        for (var i = 0; i < 8; i++)
        {
            Assert.Equal((byte)(0x10 + i), ppu.ReadOamByte((byte)i));
        }
    }

    [Fact]
    public void EvaluationStartsAtOamAddressAndTreatsTheFirstSelectedEntryAsSpriteZero()
    {
        var evaluator = new Rp2C02SpriteEvaluator();
        var oam = new byte[256];
        Array.Fill(oam, (byte)0xF0);
        oam[8] = 0x00;
        oam[9] = 0x22;
        oam[10] = 0x00;
        oam[11] = 0x40;

        evaluator.BeginScanline(targetScanline: 1, spriteHeight: 8, oamAddress: 0x08);
        for (var dot = 1; dot <= 256; dot++)
        {
            evaluator.Clock(dot, oam);
        }

        var sprite = evaluator.GetSelectedSprite(0);
        Assert.Equal((byte)2, sprite.PrimaryOamIndex);
        Assert.True(sprite.IsSpriteZero);
        Assert.True(evaluator.SpriteZeroSelected);
    }

    [Fact]
    public void EightBySixteenVerticalFlipSelectsTheCorrectPatternTableAndTileHalf()
    {
        var image = CreateNromImage();
        // Tile index $01 selects pattern table $1000 in 8x16 mode. A vertically
        // flipped top output row uses source row 15: second tile, row 7.
        image.ChrRom[0x1000 + 16 + 7] = 0x80;
        var (ppu, bus) = CreatePpu(NesTimingMode.Ntsc, image);
        bus.Write(0x3F11, 0x30);

        ppu.CpuWrite(0x2000, 0x20);
        ppu.CpuWrite(0x2003, 0x00);
        WriteSprite(ppu, y: 0x00, tile: 0x01, attributes: 0x80, x: 0x20);
        ppu.CpuWrite(0x2001, 0x14);

        ClockUntil(ppu, frame: 1, scanline: 1, dot: 48);

        Assert.NotEqual(ppu.Framebuffer[(1 * 256) + 0x1F], ppu.Framebuffer[(1 * 256) + 0x20]);
    }

    private static void WriteOamRange(Rp2C02Ppu ppu, byte start, byte firstValue)
    {
        ppu.CpuWrite(0x2003, start);
        for (var i = 0; i < 8; i++)
        {
            ppu.CpuWrite(0x2004, (byte)(firstValue + i));
        }
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

    private static (Rp2C02Ppu Ppu, PpuBus Bus) CreatePpu(NesTimingMode mode, NesRomImage? image = null)
    {
        image ??= CreateNromImage();
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
        var ppu = new Rp2C02Ppu(bus, new SignalLine(), NesTimingProfile.For(mode));
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
