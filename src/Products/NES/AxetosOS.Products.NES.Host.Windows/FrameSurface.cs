namespace AxetosOS.Products.NES.Host.Windows;

public sealed class FrameSurface
{
    public FrameSurface(int width, int height, PixelFormat pixelFormat = PixelFormat.Bgra32)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Width = width;
        Height = height;
        PixelFormat = pixelFormat;
        Pixels = new uint[checked(width * height)];
    }

    public int Width { get; }
    public int Height { get; }
    public PixelFormat PixelFormat { get; }
    public uint[] Pixels { get; }
    public Span<uint> PixelSpan => Pixels;
    public ReadOnlySpan<uint> ReadOnlyPixelSpan => Pixels;
}
