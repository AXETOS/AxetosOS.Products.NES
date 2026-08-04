using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Clock;

/// <summary>
/// A digital oscillator whose passage of time is advanced by the simulator.
/// </summary>
public sealed class DigitalOscillator : VirtualHardwareComponent
{
    private bool _high;

    public DigitalOscillator(string componentId, long frequencyHertz)
        : base(componentId)
    {
        if (frequencyHertz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frequencyHertz));
        }

        FrequencyHertz = frequencyHertz;
        Output = AddPin("CLK", PinDirection.Output);
        Output.Drive(DigitalLevel.Low);
    }

    public long FrequencyHertz { get; }
    public ulong HalfCycleCount { get; private set; }
    public DigitalPin Output { get; }

    public void AdvanceHalfCycle()
    {
        _high = !_high;
        HalfCycleCount++;
        Output.Drive(_high ? DigitalLevel.High : DigitalLevel.Low);
    }

    public override void PowerOn()
    {
        _high = false;
        HalfCycleCount = 0;
        Output.Drive(DigitalLevel.Low);
    }

    public override void Reset() => PowerOn();
    public override void Evaluate() { }
}
