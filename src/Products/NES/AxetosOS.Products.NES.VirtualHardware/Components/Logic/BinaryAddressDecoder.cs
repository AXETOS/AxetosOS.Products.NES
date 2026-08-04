using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Logic;

/// <summary>Pin-driven one-of-N binary address decoder with active-low enable.</summary>
public sealed class BinaryAddressDecoder : VirtualHardwareComponent
{
    public BinaryAddressDecoder(string componentId, int addressWidth)
        : base(componentId)
    {
        if (addressWidth is <= 0 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(addressWidth));
        }

        var addressPins = new DigitalPin[addressWidth];
        for (var bit = 0; bit < addressWidth; bit++)
        {
            addressPins[bit] = AddPin($"A{bit}", PinDirection.Input);
        }

        var outputs = new DigitalPin[1 << addressWidth];
        for (var index = 0; index < outputs.Length; index++)
        {
            outputs[index] = AddPin($"Y{index}", PinDirection.Output);
        }

        Address = new DigitalBus($"{componentId}.A", addressPins);
        Outputs = outputs;
        EnableBar = AddPin("/E", PinDirection.Input);
    }

    public DigitalBus Address { get; }
    public IReadOnlyList<DigitalPin> Outputs { get; }
    public DigitalPin EnableBar { get; }

    public override void Evaluate()
    {
        if (EnableBar.SampledLevel != DigitalLevel.Low || !Address.TrySample(out var selected))
        {
            foreach (var output in Outputs)
            {
                output.Drive(DigitalLevel.High);
            }
            return;
        }

        for (var index = 0; index < Outputs.Count; index++)
        {
            Outputs[index].Drive(index == (int)selected ? DigitalLevel.Low : DigitalLevel.High);
        }
    }
}
