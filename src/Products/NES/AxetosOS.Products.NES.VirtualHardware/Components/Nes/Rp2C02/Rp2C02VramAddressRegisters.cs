using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Nes.Rp2C02;

/// <summary>
/// Physical RP2C02 scrolling/address register block containing the internal
/// v, t, fine-X and write-toggle state. State changes occur only on command
/// pin edges and sampled input buses.
/// </summary>
public sealed class Rp2C02VramAddressRegisters : VirtualHardwareComponent
{
    private bool _writePulseLast;
    private bool _incrementPulseLast;
    private bool _copyHorizontalLast;
    private bool _copyVerticalLast;
    private bool _incrementXLast;
    private bool _incrementYLast;
    private bool _resetToggleLast;

    public Rp2C02VramAddressRegisters(string componentId) : base(componentId)
    {
        CpuData = CreateBus("CPU_D", 8, PinDirection.Input);
        ControlNametable = CreateBus("CTRL_NT", 2, PinDirection.Input);
        CurrentAddress = CreateBus("V", 15, PinDirection.Output);
        TemporaryAddress = CreateBus("T", 15, PinDirection.Output);
        FineXBus = CreateBus("FINE_X", 3, PinDirection.Output);

        RegisterSelect = CreateBus("REG", 3, PinDirection.Input);
        WritePulse = AddPin("WRITE_PULSE", PinDirection.Input);
        IncrementPulse = AddPin("INCREMENT_PULSE", PinDirection.Input);
        IncrementBy32 = AddPin("INCREMENT_BY_32", PinDirection.Input);
        CopyHorizontal = AddPin("COPY_HORIZONTAL", PinDirection.Input);
        CopyVertical = AddPin("COPY_VERTICAL", PinDirection.Input);
        IncrementCoarseX = AddPin("INCREMENT_COARSE_X", PinDirection.Input);
        IncrementFineY = AddPin("INCREMENT_FINE_Y", PinDirection.Input);
        ResetWriteToggle = AddPin("RESET_WRITE_TOGGLE", PinDirection.Input);
        WriteToggleOutput = AddPin("WRITE_TOGGLE", PinDirection.Output);
    }

    public DigitalBus CpuData { get; }
    public DigitalBus ControlNametable { get; }
    public DigitalBus RegisterSelect { get; }
    public DigitalBus CurrentAddress { get; }
    public DigitalBus TemporaryAddress { get; }
    public DigitalBus FineXBus { get; }
    public DigitalPin WritePulse { get; }
    public DigitalPin IncrementPulse { get; }
    public DigitalPin IncrementBy32 { get; }
    public DigitalPin CopyHorizontal { get; }
    public DigitalPin CopyVertical { get; }
    public DigitalPin IncrementCoarseX { get; }
    public DigitalPin IncrementFineY { get; }
    public DigitalPin ResetWriteToggle { get; }
    public DigitalPin WriteToggleOutput { get; }

    public ushort Current { get; private set; }
    public ushort Temporary { get; private set; }
    public byte FineX { get; private set; }
    public bool WriteToggle { get; private set; }

    public override void PowerOn()
    {
        Current = 0;
        Temporary = 0;
        FineX = 0;
        WriteToggle = false;
        _writePulseLast = _incrementPulseLast = _copyHorizontalLast = false;
        _copyVerticalLast = _incrementXLast = _incrementYLast = _resetToggleLast = false;
        DriveOutputs();
    }

    public override void Reset()
    {
        WriteToggle = false;
        DriveOutputs();
    }

    public override void Evaluate()
    {
        var resetToggle = ResetWriteToggle.SampledLevel == DigitalLevel.High;
        if (resetToggle && !_resetToggleLast) WriteToggle = false;
        _resetToggleLast = resetToggle;

        var writePulse = WritePulse.SampledLevel == DigitalLevel.High;
        if (writePulse && !_writePulseLast) ApplyCpuWrite();
        _writePulseLast = writePulse;

        var incrementPulse = IncrementPulse.SampledLevel == DigitalLevel.High;
        if (incrementPulse && !_incrementPulseLast)
        {
            Current = (ushort)((Current + (IncrementBy32.SampledLevel == DigitalLevel.High ? 32 : 1)) & 0x7FFF);
        }
        _incrementPulseLast = incrementPulse;

        var copyHorizontal = CopyHorizontal.SampledLevel == DigitalLevel.High;
        if (copyHorizontal && !_copyHorizontalLast)
            Current = (ushort)((Current & ~0x041F) | (Temporary & 0x041F));
        _copyHorizontalLast = copyHorizontal;

        var copyVertical = CopyVertical.SampledLevel == DigitalLevel.High;
        if (copyVertical && !_copyVerticalLast)
            Current = (ushort)((Current & ~0x7BE0) | (Temporary & 0x7BE0));
        _copyVerticalLast = copyVertical;

        var incrementX = IncrementCoarseX.SampledLevel == DigitalLevel.High;
        if (incrementX && !_incrementXLast) IncrementHorizontal();
        _incrementXLast = incrementX;

        var incrementY = IncrementFineY.SampledLevel == DigitalLevel.High;
        if (incrementY && !_incrementYLast) IncrementVertical();
        _incrementYLast = incrementY;

        DriveOutputs();
    }

    private void ApplyCpuWrite()
    {
        if (!CpuData.TrySample(out var rawData) || !RegisterSelect.TrySample(out var rawRegister)) return;
        var data = (byte)rawData;
        switch ((int)rawRegister)
        {
            case 0: // PPUCTRL nametable bits
                Temporary = (ushort)((Temporary & ~0x0C00) | ((data & 0x03) << 10));
                break;
            case 5: // PPUSCROLL
                if (!WriteToggle)
                {
                    FineX = (byte)(data & 0x07);
                    Temporary = (ushort)((Temporary & ~0x001F) | (data >> 3));
                }
                else
                {
                    Temporary = (ushort)((Temporary & ~0x73E0)
                        | ((data & 0x07) << 12)
                        | ((data & 0xF8) << 2));
                }
                WriteToggle = !WriteToggle;
                break;
            case 6: // PPUADDR
                if (!WriteToggle)
                {
                    Temporary = (ushort)((Temporary & 0x00FF) | ((data & 0x3F) << 8));
                    Temporary &= 0x3FFF;
                }
                else
                {
                    Temporary = (ushort)((Temporary & 0x7F00) | data);
                    Current = Temporary;
                }
                WriteToggle = !WriteToggle;
                break;
        }
    }

    private void IncrementHorizontal()
    {
        if ((Current & 0x001F) == 31)
        {
            Current &= unchecked((ushort)~0x001F);
            Current ^= 0x0400;
        }
        else Current++;
    }

    private void IncrementVertical()
    {
        if ((Current & 0x7000) != 0x7000)
        {
            Current += 0x1000;
            return;
        }

        Current &= unchecked((ushort)~0x7000);
        var coarseY = (Current & 0x03E0) >> 5;
        if (coarseY == 29)
        {
            coarseY = 0;
            Current ^= 0x0800;
        }
        else if (coarseY == 31) coarseY = 0;
        else coarseY++;
        Current = (ushort)((Current & ~0x03E0) | (coarseY << 5));
    }

    private void DriveOutputs()
    {
        CurrentAddress.Drive(Current);
        TemporaryAddress.Drive(Temporary);
        FineXBus.Drive(FineX);
        WriteToggleOutput.Drive(WriteToggle ? DigitalLevel.High : DigitalLevel.Low);
    }

    private DigitalBus CreateBus(string name, int width, PinDirection direction)
    {
        var pins = new DigitalPin[width];
        for (var bit = 0; bit < width; bit++) pins[bit] = AddPin($"{name}{bit}", direction);
        return new DigitalBus($"{ComponentId}.{name}", pins);
    }
}
