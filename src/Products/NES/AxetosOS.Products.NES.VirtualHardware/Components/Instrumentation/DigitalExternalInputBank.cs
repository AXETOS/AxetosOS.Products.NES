using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;

/// <summary>
/// Generic host/external-world digital stimulus with one physical output pin per
/// bit.  It carries no product or protocol semantics: callers change external
/// switch/input levels and the resulting values reach hardware only through the
/// connected board nets.
/// </summary>
public sealed class DigitalExternalInputBank : VirtualHardwareComponent, IExternalBoardSource
{
    private readonly ulong _valueMask;
    private ulong _value;

    public DigitalExternalInputBank(string componentId, int width, ulong initialValue = 0)
        : base(componentId)
    {
        if (width is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(width));

        _valueMask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
        _value = initialValue & _valueMask;

        var outputs = new DigitalPin[width];
        for (var bit = 0; bit < width; bit++)
        {
            outputs[bit] = AddPin($"OUT{bit}", PinDirection.Output);
            outputs[bit].Drive((_value & (1UL << bit)) != 0 ? DigitalLevel.High : DigitalLevel.Low);
        }
        Outputs = new DigitalBus($"{componentId}.OUT", outputs);
    }

    public DigitalBus Outputs { get; }
    public ulong Value => _value;

    /// <summary>
    /// External controls retain their current physical position across machine
    /// power application.  A held button therefore remains held during power-on.
    /// </summary>
    public void ApplyPowerOnDrive() => Outputs.Drive(_value);

    public void SetBit(int bit, bool high)
    {
        if ((uint)bit >= (uint)Outputs.Width) throw new ArgumentOutOfRangeException(nameof(bit));

        var mask = 1UL << bit;
        var next = high ? _value | mask : _value & ~mask;
        if (next == _value) return;

        _value = next;
        Outputs.Pins[bit].Drive(high ? DigitalLevel.High : DigitalLevel.Low);
    }

    public void SetValue(ulong value)
    {
        value &= _valueMask;
        if (value == _value) return;
        _value = value;
        Outputs.Drive(value);
    }
}
