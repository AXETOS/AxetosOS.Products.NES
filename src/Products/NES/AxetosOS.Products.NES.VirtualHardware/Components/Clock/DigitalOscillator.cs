using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Clock;

/// <summary>
/// A digital oscillator whose passage of time is advanced by the simulator.
/// </summary>
public sealed class DigitalOscillator : VirtualHardwareComponent, IExternalBoardSource, ICompiledClockSource
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

    /// <summary>
    /// Fused-machine full-cycle accounting. Two physical clock levels occur per
    /// complete cycle and the oscillator returns to the same drive level, so no
    /// package-pin publication is needed by the compiled circuit.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AdvanceFullCyclesCompiledWithoutPropagation(int cycles)
    {
        HalfCycleCount += (ulong)cycles * 2UL;
    }

    /// <summary>Debug/conformance half-cycle path for the fused runtime.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool AdvanceHalfCycleCompiledWithoutPropagation()
    {
        _high = !_high;
        HalfCycleCount++;
        Output.DriveCompiledSource(_high ? DigitalLevel.High : DigitalLevel.Low);
        return _high;
    }

    public void ApplyPowerOnDrive()
    {
        _high = false;
        HalfCycleCount = 0;
        Output.Drive(DigitalLevel.Low);
    }

    DigitalPin ICompiledClockSource.CompiledClockOutput => Output;
    ulong ICompiledClockSource.CompiledHalfCycleCount => HalfCycleCount;
    DigitalLevel ICompiledClockSource.CompiledClockLevel => Output.DriveLevel;
    bool ICompiledClockSource.AdvanceCompiledHalfCycleWithoutPropagation() =>
        AdvanceHalfCycleCompiledWithoutPropagation();
    void ICompiledClockSource.AdvanceCompiledFullCyclesWithoutPropagation(int cycles) =>
        AdvanceFullCyclesCompiledWithoutPropagation(cycles);

}
