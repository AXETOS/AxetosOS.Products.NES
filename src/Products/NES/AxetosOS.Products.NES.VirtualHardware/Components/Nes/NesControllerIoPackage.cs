using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Nes;

/// <summary>
/// Pin-driven NES controller I/O package for CPU addresses $4016 and $4017.
/// Button order is A, B, Select, Start, Up, Down, Left, Right.
/// The package observes only its address, data, R/W and button pins.
/// </summary>
public sealed class NesControllerIoPackage : VirtualHardwareComponent
{
    private byte _latched1;
    private byte _latched2;
    private byte _shift1;
    private byte _shift2;
    private bool _strobe;
    private bool _readActiveLast;
    private ushort _activeReadAddress;
    private byte _activeReadValue;

    public NesControllerIoPackage(string componentId)
        : base(componentId)
    {
        var addressPins = new DigitalPin[16];
        for (var bit = 0; bit < addressPins.Length; bit++)
        {
            addressPins[bit] = AddPin($"A{bit}", PinDirection.Input);
        }

        var dataPins = new DigitalPin[8];
        for (var bit = 0; bit < dataPins.Length; bit++)
        {
            dataPins[bit] = AddPin($"D{bit}", PinDirection.Bidirectional);
        }

        var controller1Pins = new DigitalPin[8];
        var controller2Pins = new DigitalPin[8];
        for (var button = 0; button < 8; button++)
        {
            controller1Pins[button] = AddPin($"P1_{button}", PinDirection.Input);
            controller2Pins[button] = AddPin($"P2_{button}", PinDirection.Input);
        }

        Address = new DigitalBus($"{componentId}.A", addressPins);
        Data = new DigitalBus($"{componentId}.D", dataPins);
        Controller1 = new DigitalBus($"{componentId}.P1", controller1Pins);
        Controller2 = new DigitalBus($"{componentId}.P2", controller2Pins);
        ReadWrite = AddPin("R/W", PinDirection.Input);
    }

    public DigitalBus Address { get; }
    public DigitalBus Data { get; }
    public DigitalBus Controller1 { get; }
    public DigitalBus Controller2 { get; }
    public DigitalPin ReadWrite { get; }
    public bool Strobe => _strobe;
    public ulong ReadCount { get; private set; }
    public ulong LatchCount { get; private set; }

    public override void PowerOn()
    {
        _latched1 = 0;
        _latched2 = 0;
        _shift1 = 0;
        _shift2 = 0;
        _strobe = false;
        _readActiveLast = false;
        _activeReadAddress = 0;
        _activeReadValue = 0x40;
        ReadCount = 0;
        LatchCount = 0;
        Data.Release();
    }

    public override void Reset()
    {
        _readActiveLast = false;
        Data.Release();
    }

    public override void Evaluate()
    {
        if (!Address.TrySample(out var rawAddress))
        {
            EndRead();
            return;
        }

        var address = (ushort)rawAddress;
        var isWrite = ReadWrite.SampledLevel == DigitalLevel.Low;
        var isRead = ReadWrite.SampledLevel == DigitalLevel.High;

        if (isWrite && address == 0x4016 && Data.TrySample(out var rawData))
        {
            EndRead();
            SetStrobe((rawData & 1) != 0);
            return;
        }

        if (isRead && address is 0x4016 or 0x4017)
        {
            BeginOrContinueRead(address);
            Data.Drive(_activeReadValue);
            return;
        }

        EndRead();
    }

    private void SetStrobe(bool next)
    {
        if (_strobe && !next)
        {
            CaptureControllers();
        }

        _strobe = next;
        if (_strobe)
        {
            CaptureControllers();
        }
    }

    private void CaptureControllers()
    {
        _latched1 = SampleButtons(Controller1);
        _latched2 = SampleButtons(Controller2);
        _shift1 = _latched1;
        _shift2 = _latched2;
        LatchCount++;
    }

    private void BeginOrContinueRead(ushort address)
    {
        if (_readActiveLast && _activeReadAddress == address)
        {
            return;
        }

        _readActiveLast = true;
        _activeReadAddress = address;
        var bit = address == 0x4016 ? ReadPort1() : ReadPort2();
        _activeReadValue = (byte)(0x40 | bit);
        ReadCount++;
    }

    private byte ReadPort1()
    {
        if (_strobe)
        {
            _latched1 = SampleButtons(Controller1);
            return (byte)(_latched1 & 1);
        }

        var bit = (byte)(_shift1 & 1);
        _shift1 = (byte)((_shift1 >> 1) | 0x80);
        return bit;
    }

    private byte ReadPort2()
    {
        if (_strobe)
        {
            _latched2 = SampleButtons(Controller2);
            return (byte)(_latched2 & 1);
        }

        var bit = (byte)(_shift2 & 1);
        _shift2 = (byte)((_shift2 >> 1) | 0x80);
        return bit;
    }

    private void EndRead()
    {
        _readActiveLast = false;
        Data.Release();
    }

    private static byte SampleButtons(DigitalBus buttons)
    {
        byte value = 0;
        for (var bit = 0; bit < 8; bit++)
        {
            if (buttons.Pins[bit].SampledLevel == DigitalLevel.High)
            {
                value |= (byte)(1 << bit);
            }
        }

        return value;
    }
}
