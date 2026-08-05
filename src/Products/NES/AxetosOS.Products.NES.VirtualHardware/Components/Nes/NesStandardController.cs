using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Nes;

/// <summary>
/// Pin-driven standard NES/Famicom controller shift-register package.
/// Button order is A, B, Select, Start, Up, Down, Left, Right.
/// The package reacts only to VCC/GND, STROBE, /CLOCK, button pins and its
/// serial DATA output. A high strobe continuously presents A; a low strobe
/// shifts one button after each completed active-low clock pulse.
/// </summary>
public sealed class NesStandardController : VirtualHardwareComponent, IInputDrivenVirtualHardwareComponent
{
    private byte _shiftRegister;
    private DigitalLevel _previousClockBar = DigitalLevel.High;
    private DigitalLevel _previousStrobe = DigitalLevel.Low;

    public NesStandardController(string componentId)
        : base(componentId)
    {
        Vcc = AddPin("VCC", PinDirection.Input);
        Gnd = AddPin("GND", PinDirection.Input);
        Strobe = AddPin("STROBE", PinDirection.Input);
        ClockBar = AddPin("/CLOCK", PinDirection.Input);
        Data = AddPin("DATA", PinDirection.Output);

        var buttons = new DigitalPin[8];
        for (var index = 0; index < buttons.Length; index++)
        {
            buttons[index] = AddPin($"BUTTON{index}", PinDirection.Input);
        }

        Buttons = new DigitalBus($"{componentId}.BUTTONS", buttons);
    }

    public DigitalPin Vcc { get; }
    public DigitalPin Gnd { get; }
    public DigitalPin Strobe { get; }
    public DigitalPin ClockBar { get; }
    public DigitalPin Data { get; }
    public DigitalBus Buttons { get; }

    public byte ShiftRegister => _shiftRegister;
    public ulong LatchCount { get; private set; }
    public ulong ShiftCount { get; private set; }

    public override void PowerOn()
    {
        _shiftRegister = 0;
        _previousClockBar = DigitalLevel.High;
        _previousStrobe = DigitalLevel.Low;
        LatchCount = 0;
        ShiftCount = 0;
        Data.Release();
    }

    public override void Reset()
    {
        _shiftRegister = 0;
        _previousClockBar = ClockBar.SampledLevel;
        _previousStrobe = Strobe.SampledLevel;
        DriveCurrentBit();
    }

    public override void Evaluate()
    {
        if (!IsPowered())
        {
            _shiftRegister = 0;
            _previousClockBar = ClockBar.SampledLevel;
            _previousStrobe = Strobe.SampledLevel;
            Data.Release();
            return;
        }

        var strobe = Strobe.SampledLevel;
        var clockBar = ClockBar.SampledLevel;

        if (strobe == DigitalLevel.High)
        {
            CaptureButtons(countLatch: _previousStrobe != DigitalLevel.High);
        }
        else if (strobe == DigitalLevel.Low &&
                 _previousClockBar == DigitalLevel.Low &&
                 clockBar == DigitalLevel.High)
        {
            _shiftRegister = (byte)((_shiftRegister >> 1) | 0x80);
            ShiftCount++;
        }

        _previousClockBar = clockBar;
        _previousStrobe = strobe;
        DriveCurrentBit();
    }

    private bool IsPowered() =>
        Vcc.SampledLevel == DigitalLevel.High &&
        Gnd.SampledLevel == DigitalLevel.Low;

    private void CaptureButtons(bool countLatch)
    {
        byte value = 0;
        for (var bit = 0; bit < Buttons.Width; bit++)
        {
            if (Buttons.Pins[bit].SampledLevel == DigitalLevel.High)
            {
                value |= (byte)(1 << bit);
            }
        }

        if (_shiftRegister != value)
        {
            _shiftRegister = value;
        }

        if (countLatch)
        {
            LatchCount++;
        }
    }

    private void DriveCurrentBit()
    {
        Data.Drive((_shiftRegister & 0x01) != 0 ? DigitalLevel.High : DigitalLevel.Low);
    }
}
