using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Components.Clock;
using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Simulation;

/// <summary>
/// Compiled direct path for the physical master oscillator. The clock is still
/// only an output pin connected to a motherboard trace; this plan merely removes
/// generic single-driver resolution work from every master-clock edge. No signal
/// queue, scheduler, or settle phase is involved.
/// </summary>
public sealed class CompiledClockExecutionPlan
{
    private readonly DigitalOscillator _clock;
    private readonly VirtualHardwareSimulator _simulator;
    private DigitalNet _compiledClockNet = null!;

    public CompiledClockExecutionPlan(
        DigitalOscillator clock,
        VirtualHardwareSimulator simulator)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _simulator = simulator ?? throw new ArgumentNullException(nameof(simulator));
        CompileRoute();
    }

    public void RecompileTopology()
    {
        _simulator.RecompileTopology();
        CompileRoute();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AdvanceHalfCycle()
    {
        if (_simulator.ProfilingEnabled)
        {
            AdvanceHalfCycleProfiled();
            return;
        }

        AdvanceHalfCycleFast();
    }

    public void AdvanceCycles(int cycles)
    {
        if (cycles < 0) throw new ArgumentOutOfRangeException(nameof(cycles));
        if (cycles == 0) return;

        if (_simulator.ProfilingEnabled)
        {
            for (var cycle = 0; cycle < cycles; cycle++)
            {
                AdvanceHalfCycleProfiled();
                AdvanceHalfCycleProfiled();
            }
            return;
        }

        // Every physical High and Low level is presented to the connected
        // package pins. Rising-edge/divider filtering belongs to those chip
        // pins; the motherboard/clock path never suppresses a real signal.
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            AdvanceHalfCycleFast();
            AdvanceHalfCycleFast();
        }
    }

    private void CompileRoute()
    {
        _compiledClockNet = _clock.Output.Net
            ?? throw new InvalidOperationException(
                $"Clock '{_clock.ComponentId}' is not connected to a motherboard trace.");
        _compiledClockNet.ValidateCompiledSingleDriverSource(_clock.Output);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AdvanceHalfCycleFast()
    {
        _clock.AdvanceHalfCycleCompiled();
        _compiledClockNet.PropagateCompiledSingleDriverFast();
    }

    private void AdvanceHalfCycleProfiled()
    {
        // Keep profiling on the same compiled physical-clock transport used by
        // normal execution. Diagnostics sample that path instead of switching
        // to the slower generic resolver and profiling a different machine.
        _clock.AdvanceHalfCycleCompiled();
        _compiledClockNet.PropagateCompiledSingleDriverProfiled(_simulator);
        _simulator.RecordCompiledClockDispatch();
    }
}
