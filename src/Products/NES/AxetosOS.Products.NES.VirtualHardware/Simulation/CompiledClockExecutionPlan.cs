using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Components.Clock;

namespace AxetosOS.Products.NES.VirtualHardware.Simulation;

/// <summary>
/// A reusable motherboard execution primitive for a fixed oscillator-driven
/// topology. The motherboard chooses the clock and phase count; the simulator
/// still resolves the real nets and executes the real chip packages after each
/// physical clock transition.
/// </summary>
public sealed class CompiledClockExecutionPlan
{
    private readonly DigitalOscillator _clock;
    private readonly VirtualHardwareSimulator _simulator;
    private readonly int _maximumPropagationPasses;

    public CompiledClockExecutionPlan(
        DigitalOscillator clock,
        VirtualHardwareSimulator simulator,
        int maximumPropagationPasses = 64)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _simulator = simulator ?? throw new ArgumentNullException(nameof(simulator));
        if (maximumPropagationPasses <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPropagationPasses));
        }

        _maximumPropagationPasses = maximumPropagationPasses;
    }

    public void AdvanceHalfCycle()
    {
        _simulator.EnsureCompiledTopologyCurrent();
        AdvanceHalfCycleUnchecked();
    }

    public void AdvanceCycles(int cycles) => AdvanceCycles(cycles, null);

    public void AdvanceCycles(int cycles, Action? afterCycle)
    {
        if (cycles < 0) throw new ArgumentOutOfRangeException(nameof(cycles));
        if (cycles == 0) return;

        // Cartridge insertion, chip replacement, or rewiring is checked once
        // for the complete host batch. The static motherboard flow is then
        // reused for every phase in that batch.
        _simulator.EnsureCompiledTopologyCurrent();
        if (afterCycle is null)
        {
            for (var cycle = 0; cycle < cycles; cycle++)
            {
                AdvanceHalfCycleUnchecked();
                AdvanceHalfCycleUnchecked();
            }
            return;
        }

        for (var cycle = 0; cycle < cycles; cycle++)
        {
            AdvanceHalfCycleUnchecked();
            AdvanceHalfCycleUnchecked();
            afterCycle();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AdvanceHalfCycleUnchecked()
    {
        _clock.AdvanceHalfCycle();
        _simulator.SettleCompiled(_maximumPropagationPasses);
    }
}
