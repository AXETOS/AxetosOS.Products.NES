using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;

/// <summary>
/// Standalone 3193-series CIC lock package.
///
/// The component is intentionally package-driven: it observes only its power,
/// reset, clock, configuration, seed and serial pins and controls the host and
/// slave reset pins. It contains no motherboard, cartridge or CPU references.
/// </summary>
public sealed class Cic3193 : VirtualHardwareComponent
{
    private bool _lastClockHigh;
    private bool _lastResetAsserted;
    private byte _serialInputShift;
    private byte _serialOutputShift;
    private int _serialBitCount;
    private int _startupClockCount;
    private bool _serialOutputBit;

    public Cic3193(string componentId) : base(componentId)
    {
        DataOut = AddPin("DATA_OUT", PinDirection.Output);
        DataIn = AddPin("DATA_IN", PinDirection.Input);
        Seed = AddPin("SEED", PinDirection.Input);
        Config = AddPin("CONFIG", PinDirection.Input);
        Nc5 = AddPin("NC5", PinDirection.Input);
        Clock = AddPin("CLK", PinDirection.Input);
        ResetBar = AddPin("RESET_BAR", PinDirection.Input);
        Gnd = AddPin("GND", PinDirection.Input);
        HostResetBar = AddPin("HOST_RESET_BAR", PinDirection.Output);
        SlaveResetBar = AddPin("SLAVE_RESET_BAR", PinDirection.Output);
        Nc11 = AddPin("NC11", PinDirection.Input);
        Nc12 = AddPin("NC12", PinDirection.Input);
        Nc13 = AddPin("NC13", PinDirection.Input);
        Nc14 = AddPin("NC14", PinDirection.Input);
        Nc15 = AddPin("NC15", PinDirection.Input);
        Vcc = AddPin("VCC", PinDirection.Input);
    }

    public DigitalPin DataOut { get; }
    public DigitalPin DataIn { get; }
    public DigitalPin Seed { get; }
    public DigitalPin Config { get; }
    public DigitalPin Nc5 { get; }
    public DigitalPin Clock { get; }
    public DigitalPin ResetBar { get; }
    public DigitalPin Gnd { get; }
    public DigitalPin HostResetBar { get; }
    public DigitalPin SlaveResetBar { get; }
    public DigitalPin Nc11 { get; }
    public DigitalPin Nc12 { get; }
    public DigitalPin Nc13 { get; }
    public DigitalPin Nc14 { get; }
    public DigitalPin Nc15 { get; }
    public DigitalPin Vcc { get; }

    public bool Powered { get; private set; }
    public bool ResetAsserted { get; private set; }
    public bool StartupComplete { get; private set; }
    public bool NtscOnlyMode { get; private set; }
    public bool SeedHigh { get; private set; }
    public ulong ClockRisingEdgeCount { get; private set; }
    public ulong CompletedSerialNibbleCount { get; private set; }
    public byte LastReceivedNibble { get; private set; }
    public byte SerialInputShift => _serialInputShift;
    public byte SerialOutputShift => _serialOutputShift;
    public int SerialBitCount => _serialBitCount;

    public override void PowerOn()
    {
        ResetInternalState();
    }

    public override void Reset()
    {
        ResetInternalState();
    }

    public override void Evaluate()
    {
        Powered = Vcc.SampledLevel == DigitalLevel.High && Gnd.SampledLevel == DigitalLevel.Low;
        if (!Powered)
        {
            ReleaseOutputs();
            _lastClockHigh = false;
            return;
        }

        NtscOnlyMode = Config.SampledLevel == DigitalLevel.Low;
        SeedHigh = Seed.SampledLevel == DigitalLevel.High;

        ResetAsserted = ResetBar.SampledLevel != DigitalLevel.High;
        if (ResetAsserted)
        {
            if (!_lastResetAsserted)
            {
                ResetInternalState();
                Powered = true;
                ResetAsserted = true;
                NtscOnlyMode = Config.SampledLevel == DigitalLevel.Low;
                SeedHigh = Seed.SampledLevel == DigitalLevel.High;
            }

            HostResetBar.Drive(DigitalLevel.Low);
            SlaveResetBar.Drive(DigitalLevel.Low);
            DataOut.Drive(DigitalLevel.Low);
            _lastResetAsserted = true;
            _lastClockHigh = Clock.SampledLevel == DigitalLevel.High;
            return;
        }

        _lastResetAsserted = false;
        var clockHigh = Clock.SampledLevel == DigitalLevel.High;
        if (clockHigh && !_lastClockHigh)
        {
            ClockRisingEdgeCount++;
            AdvanceClockedState();
        }

        _lastClockHigh = clockHigh;

        // Both reset outputs are active-low. The host remains held until the
        // startup sequence has completed; the slave reset is released first.
        SlaveResetBar.Drive(_startupClockCount >= 2 ? DigitalLevel.High : DigitalLevel.Low);
        HostResetBar.Drive(StartupComplete ? DigitalLevel.High : DigitalLevel.Low);
        DataOut.Drive(_serialOutputBit ? DigitalLevel.High : DigitalLevel.Low);
    }

    private void AdvanceClockedState()
    {
        if (!StartupComplete)
        {
            _startupClockCount++;
            if (_startupClockCount >= 4)
            {
                StartupComplete = true;
                _serialBitCount = 0;
                _serialInputShift = 0;
                _serialOutputShift = SeedHigh ? (byte)0x0A : (byte)0x05;
                if (!NtscOnlyMode)
                {
                    _serialOutputShift ^= 0x0F;
                }
                _serialOutputBit = (_serialOutputShift & 0x08) != 0;
            }

            return;
        }

        var inputBit = DataIn.SampledLevel == DigitalLevel.High ? 1 : 0;
        _serialInputShift = (byte)(((_serialInputShift << 1) | inputBit) & 0x0F);
        _serialBitCount++;

        _serialOutputShift = (byte)((_serialOutputShift << 1) & 0x0F);
        _serialOutputBit = (_serialOutputShift & 0x08) != 0;

        if (_serialBitCount < 4)
        {
            return;
        }

        LastReceivedNibble = _serialInputShift;
        CompletedSerialNibbleCount++;
        _serialBitCount = 0;
        _serialInputShift = 0;
    }

    private void ResetInternalState()
    {
        _lastClockHigh = false;
        _lastResetAsserted = false;
        _serialInputShift = 0;
        _serialOutputShift = 0;
        _serialBitCount = 0;
        _startupClockCount = 0;
        _serialOutputBit = false;
        Powered = false;
        ResetAsserted = false;
        StartupComplete = false;
        NtscOnlyMode = false;
        SeedHigh = false;
        ClockRisingEdgeCount = 0;
        CompletedSerialNibbleCount = 0;
        LastReceivedNibble = 0;
    }

    private void ReleaseOutputs()
    {
        DataOut.Release();
        HostResetBar.Release();
        SlaveResetBar.Release();
    }
}
