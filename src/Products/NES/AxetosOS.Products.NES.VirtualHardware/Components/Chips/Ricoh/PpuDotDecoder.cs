namespace AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;

/// <summary>
/// Chip-local RP2C0x horizontal timing decoder. The real PPU does not ask a
/// software scheduler what should happen on a dot: horizontal counter outputs
/// feed fixed decode gates which enable background, OAM, scroll and pixel
/// circuits. The immutable execution words below are the package-internal
/// equivalent of those hardwired decoder outputs, packed so the host can read
/// one word per PPU dot instead of repeatedly interpreting a flag graph.
/// </summary>
internal static class PpuDotDecoder
{
    internal const int DotsPerScanline = 341;

    // Bits 0-2 select the mutually-exclusive background circuit enabled on the
    // current dot. Every non-zero background action includes the physical shift
    // clock; the specialised values additionally assert the corresponding load,
    // fetch or coarse-X decoder line.
    internal const uint BackgroundActionMask = 0x07u;
    internal const uint BackgroundNone = 0u;
    internal const uint BackgroundShift = 1u;
    internal const uint BackgroundNametable = 2u;
    internal const uint BackgroundAttribute = 3u;
    internal const uint BackgroundPatternLow = 4u;
    internal const uint BackgroundPatternHigh = 5u;
    internal const uint BackgroundIncrementCoarseX = 6u;

    internal const uint VisibleDot = 1u << 3;
    internal const uint IncrementY = 1u << 4;
    internal const uint CopyHorizontal = 1u << 5;
    internal const uint CopyVertical = 1u << 6;
    internal const uint SpriteActivate = 1u << 7;
    internal const uint SpriteEvaluationReset = 1u << 8;
    internal const uint SpriteEvaluate = 1u << 9;
    internal const uint SpriteLoad = 1u << 10;

    internal const int SpriteFetchShift = 11;
    internal const uint SpriteFetchMask = 0x03u << SpriteFetchShift;
    internal const uint SpriteFetchNone = 0u << SpriteFetchShift;
    internal const uint SpriteFetchPatternLow = 1u << SpriteFetchShift;
    internal const uint SpriteFetchPatternHigh = 2u << SpriteFetchShift;

    internal const int SpriteSlotShift = 13;
    internal const uint SpriteSlotMask = 0x07u << SpriteSlotShift;

    internal static readonly uint[] ExecutionPlan = BuildExecutionPlan();
    internal static readonly byte[] PaletteIndex = BuildPaletteIndex();
    internal static readonly byte[] ReverseByte = BuildReverseByte();

    private static uint[] BuildExecutionPlan()
    {
        var plan = new uint[DotsPerScanline];
        for (var dot = 0; dot < DotsPerScanline; dot++)
        {
            uint word = 0;

            if (dot is >= 1 and <= 256)
                word |= VisibleDot;

            if (dot is >= 1 and <= 256 || dot is >= 321 and <= 336)
            {
                word |= ((dot - 1) & 7) switch
                {
                    0 => BackgroundNametable,
                    2 => BackgroundAttribute,
                    4 => BackgroundPatternLow,
                    6 => BackgroundPatternHigh,
                    7 => BackgroundIncrementCoarseX,
                    _ => BackgroundShift
                };
            }

            if (dot == 1) word |= SpriteActivate;
            if (dot == 65) word |= SpriteEvaluationReset;
            if (dot is >= 65 and <= 256 && ((dot - 65) % 3) == 0)
                word |= SpriteEvaluate;
            if (dot == 256) word |= IncrementY;
            if (dot == 257) word |= CopyHorizontal | SpriteLoad;
            if (dot is >= 280 and <= 304) word |= CopyVertical;

            if (dot is >= 257 and <= 320)
            {
                var fetchPhase = (dot - 257) & 7;
                if (fetchPhase == 4)
                    word |= SpriteFetchPatternLow;
                else if (fetchPhase == 6)
                    word |= SpriteFetchPatternHigh;

                if (fetchPhase is 4 or 6)
                    word |= (uint)(((dot - 257) >> 3) << SpriteSlotShift);
            }

            plan[dot] = word;
        }

        return plan;
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

    private static byte[] BuildReverseByte()
    {
        var map = new byte[256];
        for (var index = 0; index < map.Length; index++)
        {
            var value = (byte)index;
            value = (byte)(((value & 0x55) << 1) | ((value >> 1) & 0x55));
            value = (byte)(((value & 0x33) << 2) | ((value >> 2) & 0x33));
            map[index] = (byte)((value << 4) | (value >> 4));
        }
        return map;
    }
}
