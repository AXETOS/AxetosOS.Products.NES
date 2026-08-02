namespace AxetosOS.Products.NES.Cartridges;

public static class NesRomReader
{
    private static ReadOnlySpan<byte> Magic => [0x4E, 0x45, 0x53, 0x1A];

    public static NesRomImage Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The ROM stream must be readable.", nameof(stream));
        }

        Span<byte> header = stackalloc byte[16];
        ReadExactly(stream, header);

        if (!header[..4].SequenceEqual(Magic))
        {
            throw new InvalidDataException("The file does not contain a valid iNES/NES 2.0 header.");
        }

        var flags6 = header[6];
        var flags7 = header[7];
        var isNes20 = (flags7 & 0x0C) == 0x08;
        var mapper = (flags6 >> 4) | (flags7 & 0xF0);
        int? submapper = null;

        var prgUnits = (int)header[4];
        var chrUnits = (int)header[5];

        if (isNes20)
        {
            mapper |= (header[8] & 0x0F) << 8;
            submapper = header[8] >> 4;
            prgUnits |= (header[9] & 0x0F) << 8;
            chrUnits |= (header[9] & 0xF0) << 4;
        }

        var hasTrainer = (flags6 & 0x04) != 0;
        if (hasTrainer)
        {
            Span<byte> trainer = stackalloc byte[512];
            ReadExactly(stream, trainer);
        }

        var prgSize = checked(prgUnits * 16 * 1024);
        var chrSize = checked(chrUnits * 8 * 1024);
        var prgRom = new byte[prgSize];
        var chrRom = new byte[chrSize];
        ReadExactly(stream, prgRom);
        ReadExactly(stream, chrRom);

        var mirroring = (flags6 & 0x08) != 0
            ? NametableMirroring.FourScreen
            : (flags6 & 0x01) != 0
                ? NametableMirroring.Vertical
                : NametableMirroring.Horizontal;

        return new NesRomImage(
            isNes20 ? NesHeaderFormat.Nes20 : NesHeaderFormat.INes,
            mapper,
            submapper,
            prgSize,
            chrSize,
            hasTrainer,
            (flags6 & 0x02) != 0,
            mirroring,
            ReadTimingMode(header, isNes20),
            prgRom,
            chrRom);
    }

    private static NesTimingMode ReadTimingMode(ReadOnlySpan<byte> header, bool isNes20)
    {
        if (isNes20)
        {
            return (header[12] & 0x03) switch
            {
                0 => NesTimingMode.Ntsc,
                1 => NesTimingMode.Pal,
                2 => NesTimingMode.MultiRegion,
                3 => NesTimingMode.Dendy,
                _ => NesTimingMode.Unknown
            };
        }

        // The legacy iNES PAL bit is not universally trustworthy, but it remains
        // useful when no stronger metadata is available.
        return (header[9] & 0x01) != 0 ? NesTimingMode.Pal : NesTimingMode.Unknown;
    }

    private static void ReadExactly(Stream stream, Span<byte> destination)
    {
        var totalRead = 0;
        while (totalRead < destination.Length)
        {
            var read = stream.Read(destination[totalRead..]);
            if (read == 0)
            {
                throw new EndOfStreamException("The ROM ended before all declared data could be read.");
            }

            totalRead += read;
        }
    }
}
