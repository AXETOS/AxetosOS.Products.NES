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

public sealed class NesControllerPorts : INesHardwareModule, ICpuBusDevice
{
    private readonly INesControllerInput _input;
    private readonly byte[] _shiftRegisters = new byte[2];
    private bool _strobe;

    public NesControllerPorts(INesControllerInput input)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
    }

    public string ModuleId => "nes.io.controller-ports";
    public bool Strobe => _strobe;

    public void PowerOn()
    {
        _strobe = false;
        Array.Clear(_shiftRegisters);
        LatchControllers();
    }

    public void Reset()
    {
        _strobe = false;
        LatchControllers();
    }

    public bool HandlesCpuAddress(ushort address) => address is 0x4016 or 0x4017;

    public byte CpuRead(ushort address)
    {
        var port = address == 0x4016 ? 0 : 1;
        if (_strobe)
        {
            return (byte)((byte)_input.ReadButtons(port) & 0x01);
        }

        var value = (byte)(_shiftRegisters[port] & 0x01);
        _shiftRegisters[port] = (byte)((_shiftRegisters[port] >> 1) | 0x80);
        return value;
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address != 0x4016)
        {
            return;
        }

        var nextStrobe = (value & 0x01) != 0;
        if (_strobe || nextStrobe)
        {
            LatchControllers();
        }

        _strobe = nextStrobe;
    }

    private void LatchControllers()
    {
        _shiftRegisters[0] = (byte)_input.ReadButtons(0);
        _shiftRegisters[1] = (byte)_input.ReadButtons(1);
    }
}
