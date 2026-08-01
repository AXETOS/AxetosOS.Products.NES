using Xunit;
using AxetosOS.Products.NES.Cartridges;

namespace AxetosOS.Products.NES.Tests;

public sealed class NesRomReaderTests
{
    [Fact]
    public void ReadsMapperZeroINesImage()
    {
        var bytes = new byte[16 + (16 * 1024) + (8 * 1024)];
        bytes[0] = 0x4E;
        bytes[1] = 0x45;
        bytes[2] = 0x53;
        bytes[3] = 0x1A;
        bytes[4] = 1;
        bytes[5] = 1;
        bytes[6] = 0x01;

        using var stream = new MemoryStream(bytes);
        var image = NesRomReader.Read(stream);

        Assert.Equal(NesHeaderFormat.INes, image.HeaderFormat);
        Assert.Equal(0, image.MapperNumber);
        Assert.Equal(16 * 1024, image.PrgRomSizeBytes);
        Assert.Equal(8 * 1024, image.ChrRomSizeBytes);
        Assert.Equal(NametableMirroring.Vertical, image.Mirroring);
    }

    [Fact]
    public void RejectsInvalidMagic()
    {
        using var stream = new MemoryStream(new byte[16]);
        Assert.Throws<InvalidDataException>(() => NesRomReader.Read(stream));
    }
}
