using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;
using AxetosOS.Products.NES.VirtualHardware.Machines.Nes;

namespace AxetosOS.Products.NES.Desktop;

internal sealed class NesPcmOutput(double masterClockHz, int sampleRate) : IVirtualNesAudioSink
{
    private readonly Queue<float> _samples = new();
    private readonly double _masterCyclesPerSample = masterClockHz / sampleRate;
    private double _nextSampleCycle;
    private byte _currentDacLevel;

    public void AcceptLevelChange(ulong masterCycle, byte dacLevel)
    {
        FillBefore(masterCycle);
        _currentDacLevel = dacLevel;
    }

    public void AcceptLevelChanges(ReadOnlySpan<RicohAudioDacSample> samples)
    {
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = samples[index];
            FillBefore(sample.MasterClock);
            _currentDacLevel = sample.DacLevel;
        }
    }

    public NesPcmState CaptureState() => new(_nextSampleCycle, _currentDacLevel);

    public void RestoreState(NesPcmState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _samples.Clear();
        _nextSampleCycle = state.NextSampleCycle;
        _currentDacLevel = state.CurrentDacLevel;
    }

    public void ResetTimeline(ulong masterCycle, byte dacLevel)
    {
        _samples.Clear();
        _currentDacLevel = dacLevel;
        _nextSampleCycle = masterCycle;
    }

    public void CompleteThrough(ulong masterCycle, byte dacLevel)
    {
        _currentDacLevel = dacLevel;
        while (_nextSampleCycle <= masterCycle)
        {
            EnqueueCurrentLevel();
            _nextSampleCycle += _masterCyclesPerSample;
        }
    }

    public int Drain(float[] destination)
    {
        var count = Math.Min(destination.Length, _samples.Count);
        for (var index = 0; index < count; index++)
        {
            destination[index] = _samples.Dequeue();
        }
        return count;
    }

    public void Clear() => _samples.Clear();

    private void FillBefore(ulong masterCycle)
    {
        while (_nextSampleCycle < masterCycle)
        {
            EnqueueCurrentLevel();
            _nextSampleCycle += _masterCyclesPerSample;
        }
    }

    private void EnqueueCurrentLevel()
    {
        var normalized = (_currentDacLevel / 127.5f) - 1.0f;
        _samples.Enqueue(Math.Clamp(normalized, -1.0f, 1.0f));
    }
}

internal sealed record NesPcmState(double NextSampleCycle, byte CurrentDacLevel);
