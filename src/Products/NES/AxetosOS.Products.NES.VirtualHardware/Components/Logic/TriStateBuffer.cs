using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Logic;

/// <summary>A width-configurable non-inverting tri-state buffer.</summary>
public sealed class TriStateBuffer : VirtualHardwareComponent
{
    private readonly ulong _inputMask;
    private readonly ulong _enableMask;
    private bool _outputsReleased = true;
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
        _inputMask = Inputs.InputChangeMask;
        _enableMask = OutputEnableBar.InputChangeMask;
    }

    public DigitalBus Inputs { get; }
    public DigitalBus Outputs { get; }
    public DigitalPin OutputEnableBar { get; }

    protected override void OnInputChanges(ulong changedInputMask)
    {
        var enableChanged = (changedInputMask & _enableMask) != 0;
        var inputsChanged = (changedInputMask & _inputMask) != 0;
        if (!enableChanged && !inputsChanged) return;

        if (OutputEnableBar.SampledLevel != DigitalLevel.Low)
        {
            if (!_outputsReleased)
            {
                Outputs.Release();
                _outputsReleased = true;
            }
            return;
        }

        if (Inputs.TrySample(out var value))
            Outputs.Drive(value);
        else
            for (var index = 0; index < Outputs.Width; index++) Outputs.Pins[index].Drive(DigitalLevel.Unknown);
        _outputsReleased = false;
    }
}
