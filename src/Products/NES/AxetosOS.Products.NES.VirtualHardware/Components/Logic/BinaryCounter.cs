using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Logic;

/// <summary>Rising-edge binary counter with active-low reset and enable.</summary>
public sealed class BinaryCounter : VirtualHardwareComponent
{
    private DigitalLevel _previousClock = DigitalLevel.Unknown;
    private readonly ulong _mask;

    public BinaryCounter(string componentId, int width)
        : base(componentId)
    {
        if (width is <= 0 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        var outputs = new DigitalPin[width];
        for (var bit = 0; bit < width; bit++)
        {
            outputs[bit] = AddPin($"Q{bit}", PinDirection.Output);
        }

        Outputs = new DigitalBus($"{componentId}.Q", outputs);
        Clock = AddPin("CLK", PinDirection.Input);
        ResetBar = AddPin("/RESET", PinDirection.Input);
        Enable = AddPin("EN", PinDirection.Input);
        _mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
    }

    public DigitalPin Clock { get; }
    public DigitalPin ResetBar { get; }
    public DigitalPin Enable { get; }
    public DigitalBus Outputs { get; }
    public ulong Value { get; private set; }

    public override void PowerOn()
    {
        Value = 0;
        _previousClock = DigitalLevel.Unknown;
        Outputs.Drive(0);
    }

    public override void Reset() => PowerOn();

    public override void Evaluate()
    {
        if (ResetBar.SampledLevel == DigitalLevel.Low)
        {
            Value = 0;
        }
        else if (_previousClock == DigitalLevel.Low &&
                 Clock.SampledLevel == DigitalLevel.High &&
                 Enable.SampledLevel == DigitalLevel.High)
        {
            Value = (Value + 1) & _mask;
        }

        _previousClock = Clock.SampledLevel;
        Outputs.Drive(Value);
    }
}
