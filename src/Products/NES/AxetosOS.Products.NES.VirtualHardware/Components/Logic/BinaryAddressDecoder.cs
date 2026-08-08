using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Logic;

/// <summary>Pin-driven one-of-N binary address decoder with active-low enable.</summary>
public sealed class BinaryAddressDecoder : VirtualHardwareComponent, ICompiledCombinationalComponent
{
    private readonly ulong _addressInputMask;
    private readonly ulong _enableInputMask;
    private bool _outputsInitialized;
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
        _addressInputMask = Address.InputChangeMask;
        _enableInputMask = EnableBar.InputChangeMask;
    }

    public DigitalBus Address { get; }
    public IReadOnlyList<DigitalPin> Outputs { get; }
    public DigitalPin EnableBar { get; }

    protected override void OnInputChanges(ulong changedInputMask)
    {
        var enableChanged = (changedInputMask & _enableInputMask) != 0;
        var addressChanged = (changedInputMask & _addressInputMask) != 0;
        if (!enableChanged && !addressChanged) return;

        if (EnableBar.SampledLevel == DigitalLevel.High)
        {
            // Address pins still toggle electrically while disabled, but the
            // decoder matrix is disconnected from the active-low outputs.
            if (_outputsInitialized && !enableChanged) return;
            for (var index = 0; index < Outputs.Count; index++) Outputs[index].Drive(DigitalLevel.High);
            _outputsInitialized = true;
            return;
        }

        if (EnableBar.SampledLevel != DigitalLevel.Low || !Address.TrySample(out var selected))
        {
            for (var index = 0; index < Outputs.Count; index++) Outputs[index].Drive(DigitalLevel.High);
            _outputsInitialized = true;
            return;
        }

        for (var index = 0; index < Outputs.Count; index++)
            Outputs[index].Drive(index == (int)selected ? DigitalLevel.Low : DigitalLevel.High);
        _outputsInitialized = true;
    }
    bool ICompiledCombinationalComponent.TryEvaluateCompiledOutput(
        DigitalPin output,
        Func<DigitalPin, DigitalLevel> sampleInput,
        out CompiledDriveState drive)
    {
        var outputIndex = -1;
        for (var index = 0; index < Outputs.Count; index++)
        {
            if (!ReferenceEquals(output, Outputs[index])) continue;
            outputIndex = index;
            break;
        }
        if (outputIndex < 0)
        {
            drive = default;
            return false;
        }

        if (sampleInput(EnableBar) != DigitalLevel.Low)
        {
            drive = new CompiledDriveState(DigitalLevel.High);
            return true;
        }

        var selected = 0;
        for (var bit = 0; bit < Address.Width; bit++)
        {
            var level = sampleInput(Address.Pins[bit]);
            if (level == DigitalLevel.High) selected |= 1 << bit;
            else if (level != DigitalLevel.Low)
            {
                drive = new CompiledDriveState(DigitalLevel.High);
                return true;
            }
        }
        drive = new CompiledDriveState(outputIndex == selected ? DigitalLevel.Low : DigitalLevel.High);
        return true;
    }

}
