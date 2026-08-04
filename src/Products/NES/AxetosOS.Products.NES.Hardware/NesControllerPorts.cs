using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

[Flags]
public enum NesButtons : byte
{
    None = 0,
    A = 1 << 0,
    B = 1 << 1,
    Select = 1 << 2,
    Start = 1 << 3,
    Up = 1 << 4,
    Down = 1 << 5,
    Left = 1 << 6,
    Right = 1 << 7
}

public interface INesControllerInput
{
    NesButtons ReadButtons(int port);
}

public sealed class MutableNesControllerInput : INesControllerInput
{
    private readonly NesButtons[] _ports = new NesButtons[2];

    public NesButtons ReadButtons(int port) => port is 0 or 1 ? _ports[port] : NesButtons.None;

    public void SetButtons(int port, NesButtons buttons)
    {
        if (port is not (0 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        _ports[port] = buttons;
    }
}

public sealed record NesInputEvent(ulong CpuCycle, NesButtons Controller1, NesButtons Controller2);

public sealed class ScriptedNesControllerInput : INesControllerInput
{
    private readonly NesInputEvent[] _events;
    private int _nextEventIndex;
    private NesButtons _controller1;
    private NesButtons _controller2;

    public ScriptedNesControllerInput(IEnumerable<NesInputEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        _events = events.OrderBy(static item => item.CpuCycle).ToArray();
        AdvanceTo(0);
    }

    public NesButtons ReadButtons(int port) => port switch
    {
        0 => _controller1,
        1 => _controller2,
        _ => NesButtons.None
    };

    public void AdvanceTo(ulong cpuCycle)
    {
        while (_nextEventIndex < _events.Length && _events[_nextEventIndex].CpuCycle <= cpuCycle)
        {
            var inputEvent = _events[_nextEventIndex++];
            _controller1 = inputEvent.Controller1;
            _controller2 = inputEvent.Controller2;
        }
    }
}

/// <summary>
/// Inspectable serial controller connector. The live shift register remains
/// owned by <see cref="NesControllerPorts"/>; this component exposes that same
/// state for motherboard visualization without creating a second input path.
/// </summary>
public sealed class NesControllerPort : INesHardwareModule
{
    internal NesControllerPort(int portIndex)
    {
        PortIndex = portIndex;
    }

    public string ModuleId => $"nes.io.controller-port.{PortIndex + 1}";
    public int PortIndex { get; }
    public NesButtons LatchedButtons { get; internal set; }
    public byte ShiftRegister { get; internal set; }
    public byte SerialOutputBit => (byte)(ShiftRegister & 0x01);
    public ulong SerialReadCount { get; internal set; }

    public void PowerOn()
    {
        LatchedButtons = NesButtons.None;
        ShiftRegister = 0;
        SerialReadCount = 0;
    }

    public void Reset()
    {
        SerialReadCount = 0;
    }
}

/// <summary>
/// The shared OUT0 strobe line driven by CPU writes to $4016.
/// </summary>
public sealed class NesControllerStrobeLine : INesHardwareModule, ISignalLine
{
    public string ModuleId => "nes.signal.controller-strobe";
    public bool IsAsserted { get; private set; }

    public void Assert() => IsAsserted = true;
    public void Release() => IsAsserted = false;
    public void PowerOn() => Release();
    public void Reset() => Release();
}

public sealed class NesControllerPorts : INesHardwareModule, ICpuBusDevice, IHardwareCompositeModule
{
    private readonly INesControllerInput _input;
    private readonly NesControllerPort[] _ports;
    private readonly HardwareComponentDescriptor[] _components;
    private readonly HardwareConnectionDescriptor[] _connections;

    public NesControllerPorts(INesControllerInput input)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        Port1 = new NesControllerPort(0);
        Port2 = new NesControllerPort(1);
        StrobeLine = new NesControllerStrobeLine();
        _ports = [Port1, Port2];

        _components =
        [
            new(ModuleId, "Controller I/O registers", HardwareComponentKind.InputOutput, this),
            new(Port1.ModuleId, "Controller port 1 serial connector", HardwareComponentKind.InputOutput, Port1),
            new(Port2.ModuleId, "Controller port 2 serial connector", HardwareComponentKind.InputOutput, Port2),
            new(StrobeLine.ModuleId, "Controller OUT0 strobe line", HardwareComponentKind.SignalBundle, StrobeLine)
        ];

        _connections =
        [
            new(ModuleId, StrobeLine.ModuleId, HardwareConnectionKind.Signal, "$4016 OUT0"),
            new(StrobeLine.ModuleId, Port1.ModuleId, HardwareConnectionKind.Signal, "latch/continuous A"),
            new(StrobeLine.ModuleId, Port2.ModuleId, HardwareConnectionKind.Signal, "latch/continuous A"),
            new(Port1.ModuleId, ModuleId, HardwareConnectionKind.Internal, "serial data D0"),
            new(Port2.ModuleId, ModuleId, HardwareConnectionKind.Internal, "serial data D0")
        ];
    }

    public string ModuleId => "nes.io.controller-ports";
    public bool Strobe => StrobeLine.IsAsserted;
    public NesControllerPort Port1 { get; }
    public NesControllerPort Port2 { get; }
    public NesControllerStrobeLine StrobeLine { get; }
    public IReadOnlyList<HardwareComponentDescriptor> HardwareComponents => _components;
    public IReadOnlyList<HardwareConnectionDescriptor> HardwareConnections => _connections;

    public void PowerOn()
    {
        StrobeLine.PowerOn();
        Port1.PowerOn();
        Port2.PowerOn();
        LatchControllers();
    }

    public void Reset()
    {
        StrobeLine.Reset();
        Port1.Reset();
        Port2.Reset();
        LatchControllers();
    }

    public bool HandlesCpuAddress(ushort address) => address is 0x4016 or 0x4017;

    public byte CpuRead(ushort address)
    {
        var port = address == 0x4016 ? 0 : 1;
        var connector = _ports[port];
        if (Strobe)
        {
            var liveButtons = _input.ReadButtons(port);
            connector.LatchedButtons = liveButtons;
            connector.ShiftRegister = (byte)liveButtons;
            connector.SerialReadCount++;
            return WithControllerOpenBus((byte)((byte)liveButtons & 0x01));
        }

        var value = connector.SerialOutputBit;
        connector.ShiftRegister = (byte)((connector.ShiftRegister >> 1) | 0x80);
        connector.SerialReadCount++;
        return WithControllerOpenBus(value);
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address != 0x4016)
        {
            return;
        }

        var nextStrobe = (value & 0x01) != 0;
        if (Strobe || nextStrobe)
        {
            LatchControllers();
        }

        if (nextStrobe) StrobeLine.Assert();
        else StrobeLine.Release();
    }

    private static byte WithControllerOpenBus(byte controllerBit)
    {
        // The controller ports drive only the low data lines. On a normal NES,
        // bits 7-5 retain the $40 high-address byte that was on the CPU data bus.
        // A few commercial games compare the complete $40/$41 result instead of
        // masking bit 0, so returning only 0/1 makes valid button presses invisible.
        return (byte)(0x40 | (controllerBit & 0x01));
    }

    private void LatchControllers()
    {
        LatchController(Port1);
        LatchController(Port2);
    }

    private void LatchController(NesControllerPort connector)
    {
        var buttons = _input.ReadButtons(connector.PortIndex);
        connector.LatchedButtons = buttons;
        connector.ShiftRegister = (byte)buttons;
    }
}
