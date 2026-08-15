namespace AxetosOS.Products.NES.Host.Windows;

public interface IFramePresenter : IDisposable
{
    bool IsOpen { get; }
    int ClientWidth { get; }
    int ClientHeight { get; }
    void PumpEvents();
    void Present(FrameSurface surface, ScalingMode scalingMode = ScalingMode.IntegerNearest);
}
