using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Clock;

/// <summary>
/// A digital oscillator whose passage of time is advanced by the simulator.
/// </summary>
public sealed class DigitalOscillator : VirtualHardwareComponent, IExternalBoardSource
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

    /// <summary>
    /// Hot path for the topology-validated master-clock trace. This changes
    /// only the oscillator's own output driver; the compiled trace immediately
    /// presents that 0/1 level to its connected inputs.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AdvanceHalfCycleCompiled()
    {
        _high = !_high;
        HalfCycleCount++;
        Output.DriveCompiledSource(_high ? DigitalLevel.High : DigitalLevel.Low);
    }

    public void ApplyPowerOnDrive()
    {
        _high = false;
        HalfCycleCount = 0;
        Output.Drive(DigitalLevel.Low);
    }
}
