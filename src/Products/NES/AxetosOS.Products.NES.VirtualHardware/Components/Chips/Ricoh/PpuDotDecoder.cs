namespace AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;

/// <summary>
/// Chip-local RP2C0x horizontal timing decoder. The real PPU does not ask a
/// software scheduler what should happen on a dot: horizontal counter outputs
/// feed fixed decode gates which enable background, OAM, scroll and pixel
/// circuits. This table is the package-internal equivalent of those hardwired
/// decoder lines and is immutable for the lifetime of the process.
/// </summary>
internal static class PpuDotDecoder
{
    internal const int DotsPerScanline = 341;

    [Flags]
    internal enum Lines : uint
    {
        None = 0,
        VisiblePixel = 1u << 0,
        BackgroundShift = 1u << 1,
        BackgroundLoad = 1u << 2,
        BackgroundNametable = 1u << 3,
        BackgroundAttribute = 1u << 4,
        BackgroundPatternLow = 1u << 5,
        BackgroundPatternHigh = 1u << 6,
        IncrementCoarseX = 1u << 7,
        IncrementY = 1u << 8,
        CopyHorizontal = 1u << 9,
        CopyVertical = 1u << 10,
        SpriteActivate = 1u << 11,
        SpriteEvaluationReset = 1u << 12,
        SpriteEvaluate = 1u << 13,
        SpriteLoad = 1u << 14,
        SpritePatternLow = 1u << 15,
        SpritePatternHigh = 1u << 16,
        SpriteVisibleClock = 1u << 17
    }

    internal const Lines BackgroundCircuitMask = Lines.BackgroundShift
        | Lines.BackgroundLoad
        | Lines.BackgroundNametable
        | Lines.BackgroundAttribute
        | Lines.BackgroundPatternLow
        | Lines.BackgroundPatternHigh
        | Lines.IncrementCoarseX;

    internal static readonly uint[] DecodeLines = BuildDecodeLines();
    internal static readonly byte[] SpriteFetchSlot = BuildSpriteFetchSlots();
    internal static readonly byte[] PaletteIndex = BuildPaletteIndex();

    private static uint[] BuildDecodeLines()
    {
        var lines = new uint[DotsPerScanline];
        for (var dot = 0; dot < DotsPerScanline; dot++)
        {
            Lines decoded = Lines.None;

            if (dot is >= 1 and <= 256)
            {
                decoded |= Lines.VisiblePixel | Lines.SpriteVisibleClock;
            }

            if (dot is >= 1 and <= 256 || dot is >= 321 and <= 336)
            {
                decoded |= Lines.BackgroundShift;
                switch ((dot - 1) & 7)
                {
                    case 0:
                        decoded |= Lines.BackgroundLoad | Lines.BackgroundNametable;
                        break;
                    case 2:
                        decoded |= Lines.BackgroundAttribute;
                        break;
                    case 4:
                        decoded |= Lines.BackgroundPatternLow;
                        break;
                    case 6:
                        decoded |= Lines.BackgroundPatternHigh;
                        break;
                    case 7:
                        decoded |= Lines.IncrementCoarseX;
                        break;
                }
            }

            if (dot == 1) decoded |= Lines.SpriteActivate;
            if (dot == 65) decoded |= Lines.SpriteEvaluationReset;
            if (dot is >= 65 and <= 256 && ((dot - 65) % 3) == 0)
                decoded |= Lines.SpriteEvaluate;
            if (dot == 256) decoded |= Lines.IncrementY;
            if (dot == 257) decoded |= Lines.CopyHorizontal | Lines.SpriteLoad;
            if (dot is >= 280 and <= 304) decoded |= Lines.CopyVertical;

            if (dot is >= 257 and <= 320)
            {
                switch ((dot - 257) & 7)
                {
                    case 4:
                        decoded |= Lines.SpritePatternLow;
                        break;
                    case 6:
                        decoded |= Lines.SpritePatternHigh;
                        break;
                }
            }

            lines[dot] = (uint)decoded;
        }

        return lines;
    }

    private static byte[] BuildSpriteFetchSlots()
    {
        var slots = new byte[DotsPerScanline];
        for (var dot = 257; dot <= 320; dot++)
            slots[dot] = (byte)((dot - 257) >> 3);
        return slots;
    }

    private static byte[] BuildPaletteIndex()
    {
        var map = new byte[32];
        for (var index = 0; index < map.Length; index++)
        {
            var physicalIndex = index;
            if ((physicalIndex & 0x13) == 0x10) physicalIndex &= 0x0F;
            map[index] = (byte)physicalIndex;
        }
        return map;
    }
}
