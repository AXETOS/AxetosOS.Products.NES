using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Logic;

/// <summary>Rising-edge binary counter with active-low reset and enable.</summary>
public sealed class BinaryCounter : VirtualHardwareComponent
{
    private readonly ulong _mask;
    private readonly ulong _clockInputMask;
    private readonly ulong _resetInputMask;
    private readonly ulong _enableInputMask;

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
        Clock = AddPin("CLK", PinDirection.Input, DigitalInputActivation.RisingEdge);
        ResetBar = AddPin("/RESET", PinDirection.Input);
        Enable = AddPin("EN", PinDirection.Input);
        _mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
        _clockInputMask = Clock.InputChangeMask;
        _resetInputMask = ResetBar.InputChangeMask;
        _enableInputMask = Enable.InputChangeMask;
    
        InitializePackageState();
    }

    public DigitalPin Clock { get; }
    public DigitalPin ResetBar { get; }
    public DigitalPin Enable { get; }
    public DigitalBus Outputs { get; }
    public ulong Value { get; private set; }

    private void InitializePackageState()
    {
        Value = 0;
        Outputs.Drive(0);
    }

    protected override void OnInputChanges(ulong changedInputMask)
    {
        var resetChanged = (changedInputMask & _resetInputMask) != 0;
        var clockRising = (changedInputMask & _clockInputMask) != 0;
        var enableChanged = (changedInputMask & _enableInputMask) != 0;
        if (!resetChanged && !clockRising && !enableChanged) return;

        if (ResetBar.SampledLevel == DigitalLevel.Low)
        {
            if (Value != 0)
            {
                Value = 0;
                Outputs.Drive(0);
            }
            return;
        }

        // Enable transitions alone never clock the counter. Falling clock edges
        // are accepted electrically by the pin but do not enter this method.
        if (!clockRising || Clock.SampledLevel != DigitalLevel.High ||
            Enable.SampledLevel != DigitalLevel.High) return;

        Value = (Value + 1) & _mask;
        Outputs.Drive(Value);
    }
}
