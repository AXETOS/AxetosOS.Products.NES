using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Logic;

/// <summary>A width-configurable non-inverting tri-state buffer.</summary>
public sealed class TriStateBuffer : VirtualHardwareComponent
{
    public TriStateBuffer(string componentId, int width)
        : base(componentId)
    {
        if (width is <= 0 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        var inputs = new DigitalPin[width];
        var outputs = new DigitalPin[width];
        for (var bit = 0; bit < width; bit++)
        {
            inputs[bit] = AddPin($"A{bit}", PinDirection.Input);
            outputs[bit] = AddPin($"Y{bit}", PinDirection.Output);
        }

        Inputs = new DigitalBus($"{componentId}.A", inputs);
        Outputs = new DigitalBus($"{componentId}.Y", outputs);
        OutputEnableBar = AddPin("/OE", PinDirection.Input);
    }

    public DigitalBus Inputs { get; }
    public DigitalBus Outputs { get; }
    public DigitalPin OutputEnableBar { get; }

    public override void Evaluate()
    {
        if (OutputEnableBar.SampledLevel != DigitalLevel.Low)
        {
            Outputs.Release();
            return;
        }

        if (Inputs.TrySample(out var value))
        {
            Outputs.Drive(value);
        }
        else
        {
            foreach (var pin in Outputs.Pins)
            {
                pin.Drive(DigitalLevel.Unknown);
            }
        }
    }
}
