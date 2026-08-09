namespace AxetosOS.Products.NES.VirtualHardware.Loading;

/// <summary>
/// Strict iNES/NES 2.0 reader for the independent VirtualHardware launch path.
/// It parses cartridge metadata and bytes only; it does not create or execute
/// any legacy emulator cartridge, CPU, PPU or APU object.
/// </summary>
public static class VirtualHardwareNesRomReader
{
    private static ReadOnlySpan<byte> Magic => [0x4E, 0x45, 0x53, 0x1A];

    public static VirtualHardwareNesRomImage ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static VirtualHardwareNesRomImage Read(ReadOnlySpan<byte> rom)
    {
        using var stream = new MemoryStream(rom.ToArray(), writable: false);
        return Read(stream);
    }

    public static VirtualHardwareNesRomImage Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
            throw new ArgumentException("The ROM stream must be readable.", nameof(stream));

        Span<byte> header = stackalloc byte[16];
        ReadExactly(stream, header);
        if (!header[..4].SequenceEqual(Magic))
            throw new InvalidDataException("The file does not contain a valid iNES/NES 2.0 header.");

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

            // Exponent/multiplier encoding (0xF nibble) is intentionally
            // rejected until the cartridge package supports unusually sized
            // ROM chips. Ordinary linear NES 2.0 sizes are handled here.
            if ((header[9] & 0x0F) == 0x0F || (header[9] & 0xF0) == 0xF0)
                throw new NotSupportedException("NES 2.0 exponent/multiplier ROM sizes are not supported yet.");

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

        // NES 2.0 describes the actual volatile and nonvolatile memory fitted
        // to the cartridge in bytes 10 and 11.  A zero shift means that chip is
        // physically absent; non-zero values encode 64 << shift bytes.  Legacy
        // iNES has only an ambiguous 8 KiB PRG-RAM unit count and battery flag,
        // so retain the historical compatibility assumptions there but mark
        // them as non-explicit metadata.
        int prgRamSize;
        int prgNvRamSize;
        int chrRamSize;
        int chrNvRamSize;
        if (isNes20)
        {
            prgRamSize = DecodeRamShift(header[10] & 0x0F);
            prgNvRamSize = DecodeRamShift(header[10] >> 4);
            chrRamSize = DecodeRamShift(header[11] & 0x0F);
            chrNvRamSize = DecodeRamShift(header[11] >> 4);
        }
        else
        {
            var legacyPrgRamUnits = header[8] == 0 ? 1 : header[8];
            var legacyPrgRamSize = checked(legacyPrgRamUnits * 8 * 1024);
            if ((flags6 & 0x02) != 0)
            {
                prgRamSize = 0;
                prgNvRamSize = legacyPrgRamSize;
            }
            else
            {
                prgRamSize = legacyPrgRamSize;
                prgNvRamSize = 0;
            }
            chrRamSize = chrSize == 0 ? 8 * 1024 : 0;
            chrNvRamSize = 0;
        }
        if (prgSize == 0)
            throw new InvalidDataException("A NES cartridge must contain PRG ROM.");

        var prgRom = new byte[prgSize];
        var chrRom = new byte[chrSize];
        ReadExactly(stream, prgRom);
        ReadExactly(stream, chrRom);

        var mirroring = (flags6 & 0x08) != 0
            ? VirtualHardwareNesMirroring.FourScreen
            : (flags6 & 0x01) != 0
                ? VirtualHardwareNesMirroring.Vertical
                : VirtualHardwareNesMirroring.Horizontal;

        return new VirtualHardwareNesRomImage(
            isNes20 ? VirtualHardwareNesHeaderFormat.Nes20 : VirtualHardwareNesHeaderFormat.INes,
            mapper,
            submapper,
            prgSize,
            chrSize,
            hasTrainer,
            (flags6 & 0x02) != 0,
            mirroring,
            ReadTiming(header, isNes20),
            prgRom,
            chrRom)
        {
            PrgRamSizeBytes = prgRamSize,
            PrgNvRamSizeBytes = prgNvRamSize,
            ChrRamSizeBytes = chrRamSize,
            ChrNvRamSizeBytes = chrNvRamSize,
            HasExplicitRamSizes = isNes20
        };
    }

    private static int DecodeRamShift(int shift)
    {
        if (shift == 0) return 0;
        if ((uint)shift > 15u) throw new InvalidDataException("NES 2.0 RAM shift count is invalid.");
        return checked(64 << shift);
    }

    private static VirtualHardwareNesHeaderTiming ReadTiming(ReadOnlySpan<byte> header, bool isNes20)
    {
        if (isNes20)
        {
            return (header[12] & 0x03) switch
            {
                0 => VirtualHardwareNesHeaderTiming.Ntsc,
                1 => VirtualHardwareNesHeaderTiming.Pal,
                2 => VirtualHardwareNesHeaderTiming.MultiRegion,
                3 => VirtualHardwareNesHeaderTiming.Dendy,
                _ => VirtualHardwareNesHeaderTiming.Unknown
            };
        }

        return (header[9] & 0x01) != 0
            ? VirtualHardwareNesHeaderTiming.Pal
            : VirtualHardwareNesHeaderTiming.Unknown;
    }

    private static void ReadExactly(Stream stream, Span<byte> destination)
    {
        var totalRead = 0;
        while (totalRead < destination.Length)
        {
            var read = stream.Read(destination[totalRead..]);
            if (read == 0)
                throw new EndOfStreamException("The ROM ended before all declared data could be read.");
            totalRead += read;
        }
    }
}
