using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;

/// <summary>
/// Operating state of the standalone 3195-series CIC lock package.
/// </summary>
public enum Cic3195AuthenticationState
{
    Startup,
    Authenticating,
    Authenticated,
    RetryHold
}

/// <summary>
/// Standalone 3195-series CIC lock package.
///
/// The component is package-driven. It observes only power, reset, clock,
/// configuration, seed and serial pins, and it controls only its serial and
/// active-low reset outputs. It contains no motherboard, cartridge, CPU or
/// emulator-service references.
/// </summary>
public sealed class Cic3195 : VirtualHardwareComponent
{
    private const int StartupClockCount = 4;
    private const int InitialAuthenticationRoundCount = 16;
    private const int RetryHoldClockCount = 8;

    private bool _wasPowered;
    private bool _lastResetAsserted;
    private byte _serialInputShift;
    private byte _serialOutputShift;
    private int _serialBitCount;
    private int _startupClockCount;
    private bool _serialOutputBit;
    private int _authenticationRound;
    private int _retryClockCount;
    private byte _currentChallengeNibble;
    private byte _expectedResponseNibble;
    private byte _streamState;
    private byte _responseHistory;
    private bool _capturedSeedHigh;
    private bool _capturedPalAOnlyMode;

    private readonly ulong _powerInputMask;
    private readonly ulong _resetInputMask;
    private readonly ulong _clockInputMask;

    public Cic3195(string componentId) : base(componentId)
    {
        DataOut = AddPin("DATA_OUT", PinDirection.Output);
        DataIn = AddPin("DATA_IN", PinDirection.Input);
        Seed = AddPin("SEED", PinDirection.Input);
        Config = AddPin("CONFIG", PinDirection.Input);
        Nc5 = AddPin("NC5", PinDirection.Input);
        Clock = AddPin("CLK", PinDirection.Input, DigitalInputActivation.RisingEdge);
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

        _powerInputMask = Vcc.InputChangeMask | Gnd.InputChangeMask;
        _resetInputMask = ResetBar.InputChangeMask;
        _clockInputMask = Clock.InputChangeMask;
    
        InitializePackageState();
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
    public bool PalAOnlyMode => _capturedPalAOnlyMode;
    public bool SeedHigh => _capturedSeedHigh;
    public ulong ClockRisingEdgeCount { get; private set; }
    public ulong CompletedSerialNibbleCount { get; private set; }
    public ulong CompletedAuthenticationRoundCount { get; private set; }
    public byte LastReceivedNibble { get; private set; }
    public byte SerialInputShift => _serialInputShift;
    public byte SerialOutputShift => _serialOutputShift;
    public int SerialBitCount => _serialBitCount;
    public Cic3195AuthenticationState AuthenticationState { get; private set; }
    public int AuthenticationRound => _authenticationRound;
    public byte CurrentChallengeNibble => _currentChallengeNibble;
    public byte ExpectedResponseNibble => _expectedResponseNibble;
    public byte StreamState => _streamState;
    public ulong SuccessfulAuthenticationCount { get; private set; }
    public ulong FailedAuthenticationCount { get; private set; }
    public ulong HostResetPulseCount { get; private set; }
    public ulong IndeterminateInputSampleCount { get; private set; }
    public ulong ExternalResetCount { get; private set; }

    private void InitializePackageState()
    {
        ResetInternalState(clearLifetimeCounters: true);
    }

    protected override void OnInputChanges(ulong changedInputMask)
    {
        // DATA/SEED/CONFIG/NC pins are continuously present electrically, but
        // this CIC samples them only from its powered, reset-released clocked
        // state machine.  They therefore never execute protocol logic by
        // themselves.  CLK is declared rising-edge-only at the package pin.
        var powerChanged = (changedInputMask & _powerInputMask) != 0;
        if (!_wasPowered && !powerChanged) return;

        var resetChanged = (changedInputMask & _resetInputMask) != 0;
        var clockRising = (changedInputMask & _clockInputMask) != 0;
        if (!powerChanged && !resetChanged && !clockRising) return;

        Powered = Vcc.SampledLevel == DigitalLevel.High && Gnd.SampledLevel == DigitalLevel.Low;
        if (!Powered)
        {
            if (_wasPowered) ResetProtocolState();
            ReleaseOutputs();
            _lastResetAsserted = false;
            _wasPowered = false;
            return;
        }

        if (!_wasPowered)
        {
            ResetProtocolState();
            Powered = true;
        }
        _wasPowered = true;

        ResetAsserted = ResetBar.SampledLevel != DigitalLevel.High;
        if (ResetAsserted)
        {
            if (!_lastResetAsserted)
            {
                ExternalResetCount++;
                ResetProtocolState();
                Powered = true;
                ResetAsserted = true;
            }

            HostResetBar.Drive(DigitalLevel.Low);
            SlaveResetBar.Drive(DigitalLevel.Low);
            DataOut.Drive(DigitalLevel.Low);
            _lastResetAsserted = true;
            return;
        }

        _lastResetAsserted = false;
        if (clockRising && Clock.SampledLevel == DigitalLevel.High)
        {
            ClockRisingEdgeCount++;
            AdvanceClockedState();
        }

        SlaveResetBar.Drive(_startupClockCount >= 2 ? DigitalLevel.High : DigitalLevel.Low);
        HostResetBar.Drive(StartupComplete && AuthenticationState != Cic3195AuthenticationState.RetryHold
            ? DigitalLevel.High
            : DigitalLevel.Low);
        DataOut.Drive(_serialOutputBit ? DigitalLevel.High : DigitalLevel.Low);
    }

    private void AdvanceClockedState()
    {
        if (!StartupComplete)
        {
            _startupClockCount++;
            if (_startupClockCount == 1)
            {
                CaptureModePins();
            }

            if (_startupClockCount >= StartupClockCount)
            {
                StartupComplete = true;
                BeginAuthentication(reseed: true);
            }

            return;
        }

        if (AuthenticationState == Cic3195AuthenticationState.RetryHold)
        {
            _retryClockCount++;
            if (_retryClockCount >= RetryHoldClockCount)
            {
                BeginAuthentication(reseed: true);
            }

            return;
        }

        var inputBit = SampleSerialInputBit();
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
        CompleteReceivedNibble(LastReceivedNibble);
    }

    private int SampleSerialInputBit()
    {
        return DataIn.SampledLevel switch
        {
            DigitalLevel.High => 1,
            DigitalLevel.Low => 0,
            _ => CountIndeterminateAndReturnLow()
        };
    }

    private int CountIndeterminateAndReturnLow()
    {
        IndeterminateInputSampleCount++;
        return 0;
    }

    private void CaptureModePins()
    {
        _capturedPalAOnlyMode = Config.SampledLevel == DigitalLevel.Low;
        _capturedSeedHigh = Seed.SampledLevel == DigitalLevel.High;
    }

    private void BeginAuthentication(bool reseed)
    {
        AuthenticationState = Cic3195AuthenticationState.Authenticating;
        _authenticationRound = 0;
        _retryClockCount = 0;
        _serialBitCount = 0;
        _serialInputShift = 0;
        _responseHistory = 0;

        if (reseed)
        {
            _streamState = BuildInitialStreamState();
        }

        LoadChallengeForRound();
    }

    private void CompleteReceivedNibble(byte receivedNibble)
    {
        if (receivedNibble != _expectedResponseNibble)
        {
            FailedAuthenticationCount++;
            HostResetPulseCount++;
            AuthenticationState = Cic3195AuthenticationState.RetryHold;
            _retryClockCount = 0;
            _serialBitCount = 0;
            _serialInputShift = 0;
            _serialOutputShift = 0;
            _serialOutputBit = false;
            return;
        }

        CompletedAuthenticationRoundCount++;
        _responseHistory = receivedNibble;
        _authenticationRound++;
        AdvanceStreamState();

        if (AuthenticationState == Cic3195AuthenticationState.Authenticating &&
            _authenticationRound >= InitialAuthenticationRoundCount)
        {
            SuccessfulAuthenticationCount++;
            AuthenticationState = Cic3195AuthenticationState.Authenticated;
            _authenticationRound = 0;
        }

        LoadChallengeForRound();
    }

    private void LoadChallengeForRound()
    {
        _currentChallengeNibble = ComputeChallengeNibble();
        _expectedResponseNibble = ComputeExpectedResponseNibble(
            _currentChallengeNibble,
            _responseHistory,
            _authenticationRound,
            _capturedPalAOnlyMode);
        _serialOutputShift = _currentChallengeNibble;
        _serialOutputBit = (_serialOutputShift & 0x08) != 0;
    }

    private byte BuildInitialStreamState()
    {
        var state = _capturedSeedHigh ? 0x0D : 0x04;
        if (!_capturedPalAOnlyMode)
        {
            state ^= 0x07;
        }

        state &= 0x0F;
        return (byte)(state == 0 ? 1 : state);
    }

    private byte ComputeChallengeNibble()
    {
        var roundMix = (_authenticationRound * 0x03) & 0x0F;
        var regionMix = _capturedPalAOnlyMode ? 0x05 : 0x0A;
        return (byte)((_streamState ^ roundMix ^ regionMix) & 0x0F);
    }

    private static byte ComputeExpectedResponseNibble(
        byte challenge,
        byte responseHistory,
        int round,
        bool palRegionMode)
    {
        var rotated = ((challenge << 1) | (challenge >> 3)) & 0x0F;
        var historyMix = ((responseHistory << 1) | (responseHistory >> 3)) & 0x0F;
        var regionMix = palRegionMode ? 0x05 : 0x0A;
        return (byte)((rotated ^ historyMix ^ regionMix ^ round) & 0x0F);
    }

    private void AdvanceStreamState()
    {
        // Four-bit maximal-length LFSR. This gives a deterministic 15-state
        // stream selected by the sampled seed/configuration pins.
        var feedback = ((_streamState >> 3) ^ (_streamState >> 2)) & 1;
        _streamState = (byte)(((_streamState << 1) | feedback) & 0x0F);
        if (_streamState == 0)
        {
            _streamState = 1;
        }
    }

    private void ResetInternalState(bool clearLifetimeCounters)
    {
        if (clearLifetimeCounters)
        {
            ClockRisingEdgeCount = 0;
            CompletedSerialNibbleCount = 0;
            CompletedAuthenticationRoundCount = 0;
            SuccessfulAuthenticationCount = 0;
            FailedAuthenticationCount = 0;
            HostResetPulseCount = 0;
            IndeterminateInputSampleCount = 0;
            ExternalResetCount = 0;
        }

        ResetProtocolState();
        _wasPowered = false;
        Powered = false;
        ResetAsserted = false;
    }

    private void ResetProtocolState()
    {
        _serialInputShift = 0;
        _serialOutputShift = 0;
        _serialBitCount = 0;
        _startupClockCount = 0;
        _serialOutputBit = false;
        _authenticationRound = 0;
        _retryClockCount = 0;
        _currentChallengeNibble = 0;
        _expectedResponseNibble = 0;
        _streamState = 0;
        _responseHistory = 0;
        _capturedSeedHigh = false;
        _capturedPalAOnlyMode = false;
        StartupComplete = false;
        LastReceivedNibble = 0;
        AuthenticationState = Cic3195AuthenticationState.Startup;
    }

    private void ReleaseOutputs()
    {
        DataOut.Release();
        HostResetBar.Release();
        SlaveResetBar.Release();
    }
}
