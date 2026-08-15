namespace AxetosOS.Products.NES.Host.Windows;

public readonly record struct PresentationViewport(int X, int Y, int Width, int Height)
{
    public static PresentationViewport Calculate(
        int sourceWidth,
        int sourceHeight,
        int destinationWidth,
        int destinationHeight,
        ScalingMode scalingMode)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(destinationWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(destinationHeight);

        if (scalingMode == ScalingMode.StretchNearest)
        {
            return new PresentationViewport(0, 0, destinationWidth, destinationHeight);
        }

        var scaleX = destinationWidth / (double)sourceWidth;
        var scaleY = destinationHeight / (double)sourceHeight;
        var scale = Math.Min(scaleX, scaleY);

        if (scalingMode == ScalingMode.IntegerNearest && scale >= 1.0)
        {
            scale = Math.Max(1.0, Math.Floor(scale));
        }

        var width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        return new PresentationViewport(
            (destinationWidth - width) / 2,
            (destinationHeight - height) / 2,
            width,
            height);
    }
}
