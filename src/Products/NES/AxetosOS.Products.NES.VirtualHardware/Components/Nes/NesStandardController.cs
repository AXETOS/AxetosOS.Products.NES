using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Nes;

/// <summary>
/// Pin-driven standard NES/Famicom controller shift-register package.
/// Button order is A, B, Select, Start, Up, Down, Left, Right.
/// </summary>
public sealed class NesStandardController : VirtualHardwareComponent, ICompiledSerialPeripheralProvider
{
    private byte _shiftRegister;
    private DigitalLevel _previousStrobe = DigitalLevel.Low;
    private bool _packagePowered;
    private readonly ulong _powerInputMask;
    private readonly ulong _strobeInputMask;
    private readonly ulong _clockInputMask;
    private readonly ulong _buttonInputMask;

    public NesStandardController(string componentId)
        : base(componentId)
    {
        Vcc = AddPin("VCC", PinDirection.Input);
        Gnd = AddPin("GND", PinDirection.Input);
        Strobe = AddPin("STROBE", PinDirection.Input);
        // The controller shifts after the active-low clock pulse completes.
        // Low is still delivered to the pin, but only Low->High can wake logic.
        ClockBar = AddPin("/CLOCK", PinDirection.Input, DigitalInputActivation.RisingEdge);
        Data = AddPin("DATA", PinDirection.Output);

        var buttons = new DigitalPin[8];
        for (var index = 0; index < buttons.Length; index++)
            buttons[index] = AddPin($"BUTTON{index}", PinDirection.Input);
        Buttons = new DigitalBus($"{componentId}.BUTTONS", buttons);

        _powerInputMask = Vcc.InputChangeMask | Gnd.InputChangeMask;
        _strobeInputMask = Strobe.InputChangeMask;
        _clockInputMask = ClockBar.InputChangeMask;
        _buttonInputMask = Buttons.InputChangeMask;

        InitializePackageState();
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

    private void InitializePackageState()
    {
        _shiftRegister = 0;
        _previousStrobe = DigitalLevel.Low;
        _packagePowered = false;
        LatchCount = 0;
        ShiftCount = 0;
        Data.Release();
    }

    protected override void OnInputChanges(ulong changedInputMask)
    {
        var powerChanged = (changedInputMask & _powerInputMask) != 0;
        if (!_packagePowered && !powerChanged) return;

        if (!IsPowered())
        {
            if (_packagePowered)
            {
                _shiftRegister = 0;
                Data.Release();
            }
            _packagePowered = false;
            _previousStrobe = Strobe.SampledLevel;
            return;
        }

        var newlyPowered = !_packagePowered;
        _packagePowered = true;

        var strobeChanged = (changedInputMask & _strobeInputMask) != 0;
        var clockRising = (changedInputMask & _clockInputMask) != 0;
        var buttonsChanged = (changedInputMask & _buttonInputMask) != 0;
        if (!newlyPowered && !strobeChanged && !clockRising && !buttonsChanged) return;

        var strobe = Strobe.SampledLevel;

        // Button pins can toggle freely while STROBE is Low; the held shift
        // register is disconnected from those inputs until the next latch.
        if (buttonsChanged && !newlyPowered && !strobeChanged && !clockRising
            && strobe != DigitalLevel.High)
        {
            return;
        }

        if (strobe == DigitalLevel.High)
        {
            CaptureButtons(countLatch: _previousStrobe != DigitalLevel.High);
        }
        else if (strobe == DigitalLevel.Low)
        {
            if (_previousStrobe == DigitalLevel.High && strobeChanged)
            {
                // Falling STROBE freezes the last live button state.
                CaptureButtons(countLatch: false);
            }
            else if (clockRising)
            {
                _shiftRegister = (byte)((_shiftRegister >> 1) | 0x80);
                ShiftCount++;
            }
        }

        _previousStrobe = strobe;
        DriveCurrentBit();
    }

    private bool IsPowered() =>
        Vcc.SampledLevel == DigitalLevel.High && Gnd.SampledLevel == DigitalLevel.Low;

    private void CaptureButtons(bool countLatch)
    {
        byte value = 0;
        for (var bit = 0; bit < Buttons.Width; bit++)
        {
            if (Buttons.Pins[bit].SampledLevel == DigitalLevel.High)
                value |= (byte)(1 << bit);
        }

        _shiftRegister = value;
        if (countLatch) LatchCount++;
    }

    private void DriveCurrentBit() =>
        Data.Drive((_shiftRegister & 0x01) != 0 ? DigitalLevel.High : DigitalLevel.Low);

    IEnumerable<CompiledSerialPeripheralDescriptor> ICompiledSerialPeripheralProvider.GetCompiledSerialPeripherals()
    {
        yield return new CompiledSerialPeripheralDescriptor(
            this,
            Data,
            ClockBar,
            Strobe,
            ReadCompiledSerial,
            WriteCompiledLatch);
    }

    private byte ReadCompiledSerial()
    {
        var value = (byte)(_shiftRegister & 0x01);
        if (_previousStrobe != DigitalLevel.High)
        {
            _shiftRegister = (byte)((_shiftRegister >> 1) | 0x80);
            ShiftCount++;
        }
        return value;
    }

    private void WriteCompiledLatch(bool high)
    {
        var next = high ? DigitalLevel.High : DigitalLevel.Low;
        if (high)
        {
            CaptureButtons(countLatch: _previousStrobe != DigitalLevel.High);
        }
        else if (_previousStrobe == DigitalLevel.High)
        {
            CaptureButtons(countLatch: false);
        }
        _previousStrobe = next;
    }


}
