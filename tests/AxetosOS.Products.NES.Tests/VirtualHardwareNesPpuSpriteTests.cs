using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareNesPpuSpriteTests
{
    [Fact]
    public void Sprite_pipeline_evaluates_oam_fetches_pattern_and_outputs_sprite_palette()
    {
        var machine = CreateMachine(mask: 0x14);
        LoadSingleSprite(machine, y: 0, tile: 1, attributes: 0, x: 0);
        machine.PpuRegisters.LoadPpuMemory(0x0010, new byte[] { 0x80 });
        machine.PpuRegisters.LoadPpuMemory(0x3F11, new byte[] { 0x2A });

        machine.AdvancePpuDots(342);

        Assert.Equal(0x2A, machine.PpuRegisters.InspectPixel(0, 1));
        Assert.Equal(1, machine.PpuRegisters.SecondarySpriteCount);
        Assert.True(machine.PpuRegisters.SpriteFetchCount >= 2);
        Assert.True(machine.PpuRegisters.SpritePixelCount >= 1);
    }

    [Fact]
    public void Sprite_priority_places_behind_background_and_sets_sprite_zero_hit()
    {
        var machine = CreateMachine(mask: 0x1E);
        LoadSingleSprite(machine, y: 0, tile: 2, attributes: 0x20, x: 0);
        machine.PpuRegisters.LoadPpuMemory(0x2000, new byte[] { 1 });
        machine.PpuRegisters.LoadPpuMemory(0x0011, new byte[] { 0x80 });
        machine.PpuRegisters.LoadPpuMemory(0x0020, new byte[] { 0x80 });
        machine.PpuRegisters.LoadPpuMemory(0x3F00, new byte[] { 0x0F, 0x21 });
        machine.PpuRegisters.LoadPpuMemory(0x3F11, new byte[] { 0x2A });

        machine.AdvancePpuDots(342);

        Assert.Equal(0x21, machine.PpuRegisters.InspectPixel(0, 1));
        Assert.True(machine.PpuRegisters.SpriteZeroHit);
        Assert.Equal((ulong)1, machine.PpuRegisters.SpriteZeroHitCount);
    }

    [Fact]
    public void Sprite_pipeline_obeys_horizontal_and_vertical_flip_attributes()
    {
        var machine = CreateMachine(mask: 0x14);
        LoadSingleSprite(machine, y: 0, tile: 1, attributes: 0xC0, x: 0);
        var pattern = new byte[16];
        pattern[7] = 0x01;
        machine.PpuRegisters.LoadPpuMemory(0x0010, pattern);
        machine.PpuRegisters.LoadPpuMemory(0x3F11, new byte[] { 0x35 });

        machine.AdvancePpuDots(342);

        Assert.Equal(0x35, machine.PpuRegisters.InspectPixel(0, 1));
    }

    [Fact]
    public void Ninth_in_range_sprite_sets_overflow_and_secondary_oam_keeps_first_eight()
    {
        var machine = CreateMachine(mask: 0x14);
        var oam = Enumerable.Repeat((byte)0xFF, 256).ToArray();
        for (var sprite = 0; sprite < 9; sprite++)
        {
            var offset = sprite * 4;
            oam[offset] = 0;
            oam[offset + 1] = 0;
            oam[offset + 2] = 0;
            oam[offset + 3] = (byte)(sprite * 8);
        }
        machine.PpuRegisters.LoadOamMemory(0, oam);

        machine.AdvancePpuDots(342);

        Assert.Equal(8, machine.PpuRegisters.SecondarySpriteCount);
        Assert.True(machine.PpuRegisters.SpriteOverflow);
        Assert.True(machine.PpuRegisters.SpriteOverflowCount >= 1);
        Assert.Equal((byte)56, machine.PpuRegisters.InspectSecondaryOam(7, 3));
    }

    private static void LoadSingleSprite(NesCpuMotherboard machine, byte y, byte tile, byte attributes, byte x)
    {
        var oam = Enumerable.Repeat((byte)0xFF, 256).ToArray();
        oam[0] = y;
        oam[1] = tile;
        oam[2] = attributes;
        oam[3] = x;
        machine.PpuRegisters.LoadOamMemory(0, oam);
    }

    private static NesCpuMotherboard CreateMachine(byte mask)
    {
        var prg = new byte[NesCpuMotherboard.PrgRomSize];
        var program = new byte[] { 0xA9, mask, 0x8D, 0x01, 0x20, 0x00 };
        program.CopyTo(prg, 0);
        prg[0x7FFC] = 0x00;
        prg[0x7FFD] = 0x80;
        var machine = new NesCpuMotherboard(prg);
        machine.PowerOn();
        machine.ReleaseReset();
        machine.RunUntilHalted();
        machine.PpuTiming.Reset();
        machine.Simulator.Settle();
        return machine;
    }
}
