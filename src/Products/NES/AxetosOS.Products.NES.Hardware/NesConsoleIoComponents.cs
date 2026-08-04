using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

public sealed class NesVideoOutputConnector : INesHardwareModule
{
    private readonly Rp2C02Ppu _ppu;
    internal NesVideoOutputConnector(Rp2C02Ppu ppu) => _ppu = ppu;
    public string ModuleId => "nes.io.video-output";
    public int Width => 256;
    public int Height => 240;
    public ReadOnlyMemory<uint> Framebuffer => _ppu.Framebuffer;
    public ulong FrameNumber => _ppu.Frame;
    public bool FrameCompleted => _ppu.FrameCompleted;
    public void PowerOn() { }
    public void Reset() { }
}

public sealed class NesAudioOutputConnector : INesHardwareModule
{
    private readonly Rp2A03Apu _apu;
    internal NesAudioOutputConnector(Rp2A03Apu apu) => _apu = apu;
    public string ModuleId => "nes.io.audio-output";
    public int SampleRate => _apu.SampleRate;
    public int BufferedSampleCount => _apu.Samples.Count;
    public IReadOnlyList<float> Samples => _apu.Samples;
    public void PowerOn() { }
    public void Reset() { }
}

public sealed class NesControllerSocket : INesHardwareModule
{
    internal NesControllerSocket(int socketNumber, NesControllerPort port)
    {
        SocketNumber = socketNumber;
        Port = port;
    }

    public string ModuleId => $"nes.connector.controller.{SocketNumber}";
    public int SocketNumber { get; }
    public NesControllerPort Port { get; }
    public byte SerialData => Port.SerialOutputBit;
    public void PowerOn() { }
    public void Reset() { }
}

public sealed class NesPowerSwitch : INesHardwareModule
{
    private readonly Action _powerOn;
    internal NesPowerSwitch(Action powerOn) => _powerOn = powerOn;
    public string ModuleId => "nes.control.power";
    public bool IsPowered { get; private set; }
    public ulong PowerOnCount { get; private set; }
    public void SwitchOn() => _powerOn();
    internal void MarkPowered()
    {
        IsPowered = true;
        PowerOnCount++;
    }
    public void PowerOn() { }
    public void Reset() { }
}

public sealed class NesResetButton : INesHardwareModule
{
    private readonly Action _reset;
    internal NesResetButton(Action reset) => _reset = reset;
    public string ModuleId => "nes.control.reset";
    public ulong PressCount { get; private set; }
    public void Press()
    {
        PressCount++;
        _reset();
    }
    public void PowerOn() { }
    public void Reset() { }
}

public sealed class NesConsoleIoPackage : INesHardwareModule, IHardwareCompositeModule
{
    private readonly HardwareComponentDescriptor[] _components;
    private readonly HardwareConnectionDescriptor[] _connections;

    internal NesConsoleIoPackage(Rp2C02Ppu ppu, Rp2A03Apu apu, NesControllerPorts controllers, Action powerOn, Action reset)
    {
        Video = new NesVideoOutputConnector(ppu);
        Audio = new NesAudioOutputConnector(apu);
        Controller1 = new NesControllerSocket(1, controllers.Port1);
        Controller2 = new NesControllerSocket(2, controllers.Port2);
        PowerSwitch = new NesPowerSwitch(powerOn);
        ResetButton = new NesResetButton(reset);

        _components =
        [
            new(ModuleId, "NES console I/O panel", HardwareComponentKind.InputOutput, this),
            new(Video.ModuleId, "Composite video output", HardwareComponentKind.InputOutput, Video),
            new(Audio.ModuleId, "Mono audio output", HardwareComponentKind.InputOutput, Audio),
            new(Controller1.ModuleId, "Controller socket 1", HardwareComponentKind.InputOutput, Controller1),
            new(Controller2.ModuleId, "Controller socket 2", HardwareComponentKind.InputOutput, Controller2),
            new(PowerSwitch.ModuleId, "Power switch", HardwareComponentKind.InputOutput, PowerSwitch),
            new(ResetButton.ModuleId, "Reset button", HardwareComponentKind.InputOutput, ResetButton)
        ];

        _connections =
        [
            new(ppu.ModuleId, Video.ModuleId, HardwareConnectionKind.Signal, "pixel video"),
            new(apu.ModuleId, Audio.ModuleId, HardwareConnectionKind.Signal, "mixed audio"),
            new(Controller1.ModuleId, controllers.Port1.ModuleId, HardwareConnectionKind.Signal, "serial controller contacts"),
            new(Controller2.ModuleId, controllers.Port2.ModuleId, HardwareConnectionKind.Signal, "serial controller contacts"),
            new(PowerSwitch.ModuleId, ModuleId, HardwareConnectionKind.Signal, "power control"),
            new(ResetButton.ModuleId, ModuleId, HardwareConnectionKind.Signal, "reset control")
        ];
    }

    public string ModuleId => "nes.io.console-panel";
    public NesVideoOutputConnector Video { get; }
    public NesAudioOutputConnector Audio { get; }
    public NesControllerSocket Controller1 { get; }
    public NesControllerSocket Controller2 { get; }
    public NesPowerSwitch PowerSwitch { get; }
    public NesResetButton ResetButton { get; }
    public IReadOnlyList<HardwareComponentDescriptor> HardwareComponents => _components;
    public IReadOnlyList<HardwareConnectionDescriptor> HardwareConnections => _connections;
    public void PowerOn() { }
    public void Reset() { }
}
