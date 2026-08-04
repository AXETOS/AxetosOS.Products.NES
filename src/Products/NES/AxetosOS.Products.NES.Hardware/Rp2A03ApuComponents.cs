using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

public readonly record struct ApuPulseSnapshot(
    bool Enabled,
    byte Duty,
    byte SequenceStep,
    byte LengthCounter,
    ushort TimerPeriod,
    ushort Timer,
    bool ConstantVolume,
    byte Volume,
    byte EnvelopeDecay,
    bool SweepEnabled,
    bool SweepNegate,
    byte SweepShift,
    byte Output);

public readonly record struct ApuTriangleSnapshot(
    bool Enabled,
    byte LengthCounter,
    byte LinearCounter,
    byte LinearReloadValue,
    ushort TimerPeriod,
    ushort Timer,
    byte SequenceStep,
    byte Output);

public readonly record struct ApuNoiseSnapshot(
    bool Enabled,
    byte LengthCounter,
    bool Mode,
    ushort TimerPeriod,
    ushort Timer,
    ushort ShiftRegister,
    bool ConstantVolume,
    byte Volume,
    byte EnvelopeDecay,
    byte Output);

public readonly record struct ApuDmcSnapshot(
    bool Enabled,
    bool IrqEnabled,
    bool Loop,
    ushort TimerPeriod,
    ushort Timer,
    ushort SampleAddress,
    ushort SampleLength,
    ushort CurrentAddress,
    ushort BytesRemaining,
    byte Output,
    byte BitsRemaining,
    bool Silence,
    bool DmaPending,
    bool IrqAsserted);

public sealed class Rp2A03ApuFrameSequencer : INesHardwareModule
{
    private readonly Rp2A03Apu _apu;

    internal Rp2A03ApuFrameSequencer(Rp2A03Apu apu) => _apu = apu;

    public string ModuleId => "nes.chip.rp2a03.apu.frame-sequencer";
    public int Cycle => _apu.FrameCycle;
    public bool FiveStepMode => _apu.FiveStepMode;
    public bool IrqInhibit => _apu.FrameIrqInhibit;
    public bool IrqAsserted => _apu.FrameIrqAsserted;

    public void PowerOn() { }
    public void Reset() { }
}

public sealed class Rp2A03PulseChannelComponent : INesHardwareModule
{
    private readonly Rp2A03Apu _apu;
    private readonly int _channel;

    internal Rp2A03PulseChannelComponent(Rp2A03Apu apu, int channel)
    {
        _apu = apu;
        _channel = channel;
    }

    public string ModuleId => $"nes.chip.rp2a03.apu.pulse{_channel}";
    public int ChannelNumber => _channel;
    public ApuPulseSnapshot State => _apu.GetPulseSnapshot(_channel);

    public void PowerOn() { }
    public void Reset() { }
}

public sealed class Rp2A03TriangleChannelComponent : INesHardwareModule
{
    private readonly Rp2A03Apu _apu;

    internal Rp2A03TriangleChannelComponent(Rp2A03Apu apu) => _apu = apu;

    public string ModuleId => "nes.chip.rp2a03.apu.triangle";
    public ApuTriangleSnapshot State => _apu.TriangleSnapshot;

    public void PowerOn() { }
    public void Reset() { }
}

public sealed class Rp2A03NoiseChannelComponent : INesHardwareModule
{
    private readonly Rp2A03Apu _apu;

    internal Rp2A03NoiseChannelComponent(Rp2A03Apu apu) => _apu = apu;

    public string ModuleId => "nes.chip.rp2a03.apu.noise";
    public ApuNoiseSnapshot State => _apu.NoiseSnapshot;

    public void PowerOn() { }
    public void Reset() { }
}

public sealed class Rp2A03DmcChannelComponent : INesHardwareModule
{
    private readonly Rp2A03Apu _apu;

    internal Rp2A03DmcChannelComponent(Rp2A03Apu apu) => _apu = apu;

    public string ModuleId => "nes.chip.rp2a03.apu.dmc";
    public ApuDmcSnapshot State => _apu.DmcSnapshot;

    public void PowerOn() { }
    public void Reset() { }
}
