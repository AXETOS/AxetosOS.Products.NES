using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;

/// <summary>
/// Standalone PAL Ricoh RP2C07 package. All observable behaviour is driven by
/// package power, reset, clock and bus pins. The chip owns only physical
/// internal state (registers, address latches, read buffer and primary OAM).
/// External PPU memory is accessed exclusively through AD0-AD7, A8-A13, ALE,
/// /RD and /WR.
/// </summary>
public sealed class Rp2C07 : VirtualHardwareComponent
{
    private const int DotsPerScanline = 341;
    private const int ScanlinesPerFrame = 312;
    private const int VblankStartScanline = 241;
    private const int PreRenderScanline = 311;
    // Four 16-bit background shift-register lanes are physically clocked in
    // parallel. Keep the same four retained hardware lanes in one 64-bit word
    // so one host operation advances all of them without changing PPU state.
    private const ulong BackgroundShiftLaneMask = 0xFFFEFFFEFFFEFFFEUL;
    private const ulong BackgroundLoadHighByteMask = 0xFF00FF00FF00FF00UL;
    private const ulong BackgroundAttributeLowFill = 0x000000FF00000000UL;
    private const ulong BackgroundAttributeHighFill = 0x00FF000000000000UL;


    private readonly byte[] _primaryOam = new byte[256];
    private readonly byte[] _paletteRam = new byte[32];
    private bool _cpuSelectedLast;
    private bool _cpuReadLatchValid;
    private byte _cpuReadLatch;
    private bool _vblank;
    private bool _spriteZeroHit;
    private bool _spriteOverflow;
    private byte _control;
    private byte _mask;
    private bool _nmiEnabled;
    private ushort _cpuVramIncrement = 1;
    private ushort _backgroundPatternTableBase;
    private ushort _spritePatternTableBase;
    private int _spriteHeight = 8;
    private bool _backgroundRenderingEnabled;
    private bool _spriteRenderingEnabled;
    private bool _renderingEnabled;
    private bool _showBackgroundLeft;
    private bool _showSpriteLeft;
    private bool _greyscaleEnabled;
    private byte _decodedColorEmphasis;
    private byte _oamAddress;
    private byte _openBus;
    private byte _readBuffer;
    private ushort _vramAddress;
    private ushort _temporaryAddress;
    private byte _fineX;
    private bool _writeToggle;
    private VramTransaction _transaction;
    private int _transactionPhase;
    private int _transactionCompletionPhase;
    private VramTransactionPurpose _transactionPurpose;
    private bool _presentedAdDriven;
    private byte _presentedAdValue;
    private bool _presentedHighAddressDriven;
    private byte _presentedHighAddressValue;
    private DigitalLevel _presentedAle = DigitalLevel.Unknown;
    private DigitalLevel _presentedReadBar = DigitalLevel.Unknown;
    private DigitalLevel _presentedWriteBar = DigitalLevel.Unknown;
    private byte _nextTileId;
    private byte _nextTileAttribute;
    private ulong _nextBackgroundLoad;
    private ulong _backgroundShifters;
    private byte _backgroundTapShift = 15;
    private readonly SpriteEntry[] _secondaryOam = new SpriteEntry[8];
    private readonly SpriteRenderUnit[] _activeSprites = new SpriteRenderUnit[8];
    private readonly SpriteRenderUnit[] _nextSprites = new SpriteRenderUnit[8];
    private int _secondarySpriteCount;
    private int _activeSpriteCount;
    private int _nextSpriteCount;
    private int _spriteEvaluationIndex;
    private int _spriteFetchSlot;
    private bool _nmiAsserted;
    private bool _suppressVblankSet;
    private byte _oamDataBusLatch;
    private int _spriteOverflowByteOffset;
    private bool _packagePowered;
    private bool _resetAsserted;
    private readonly ulong _powerInputMask;
    private readonly ulong _clockInputMask;
    private readonly ulong _resetInputMask;
    private readonly ulong _cpuPortInputMask;
    private readonly ulong _cpuOrdinaryInputMask;
    private readonly ulong _cpuChipSelectInputMask;

    public Rp2C07(string componentId) : base(componentId)
    {
        Vcc = AddPin("VCC", PinDirection.Input);
        Gnd = AddPin("GND", PinDirection.Input);
        Clock = AddPin("CLK", PinDirection.Input, DigitalInputActivation.RisingEdge, 1);
        ResetBar = AddPin("/RES", PinDirection.Input);
        NmiBar = AddPin("/NMI", PinDirection.Output);

        RegisterSelect = CreateBus("RS", 3, PinDirection.Input);
        CpuData = CreateBus("D", 8, PinDirection.Bidirectional);
        CpuReadWrite = AddPin("R/W", PinDirection.Input);
        ChipSelectBar = AddPin("/CS", PinDirection.Input);

        // Every CPU-facing package pin remains a normal electrically delivered
        // input. /CS gates the register circuit inside this chip; the motherboard
        // never suppresses RS/RW/D changes on the chip's behalf.

        MultiplexedAddressData = CreateBus("AD", 8, PinDirection.Bidirectional);
        HighAddress = CreateBus("A", 6, PinDirection.Output, firstBitNumber: 8);
        AddressLatchEnable = AddPin("ALE", PinDirection.Output);
        VramReadBar = AddPin("/RD", PinDirection.Output);
        VramWriteBar = AddPin("/WR", PinDirection.Output);
        Extension = CreateBus("EXT", 4, PinDirection.Bidirectional);
        VideoOutput = new BufferedOutputPin<RicohVideoPixelSample>(
            $"{componentId}.VIDEO",
            new RicohVideoPixelSample(0, 0, 0, 0, 0));
    
        _powerInputMask = Vcc.InputChangeMask | Gnd.InputChangeMask;
        _clockInputMask = Clock.InputChangeMask;
        _resetInputMask = ResetBar.InputChangeMask;
        _cpuOrdinaryInputMask = RegisterSelect.InputChangeMask
            | CpuData.InputChangeMask
            | CpuReadWrite.InputChangeMask;
        _cpuChipSelectInputMask = ChipSelectBar.InputChangeMask;
        _cpuPortInputMask = _cpuOrdinaryInputMask | _cpuChipSelectInputMask;

        RegisterSelect.SetOwnerWakeEnabled(false);
        CpuData.SetOwnerWakeEnabled(false);
        CpuReadWrite.SetOwnerWakeEnabled(false);

        // External VRAM data is sampled synchronously by the internal transaction
        // sequencer on a later PPU clock phase. Changes on AD0-AD7 must therefore
        // update the physical package pins without recursively waking the PPU.
        // EXT is not consumed by this package model and likewise cannot activate
        // internal work merely because an external level changes.
        MultiplexedAddressData.SetOwnerWakeEnabled(false);
        Extension.SetOwnerWakeEnabled(false);

        InitializePackageState();
    }

    public DigitalPin Vcc { get; }
    public DigitalPin Gnd { get; }
    public DigitalPin Clock { get; }
    public DigitalPin ResetBar { get; }
    public DigitalPin NmiBar { get; }
    public DigitalBus RegisterSelect { get; }
    public DigitalBus CpuData { get; }
    public DigitalPin CpuReadWrite { get; }
    public DigitalPin ChipSelectBar { get; }
    public DigitalBus MultiplexedAddressData { get; }
    public DigitalBus HighAddress { get; }
    public DigitalPin AddressLatchEnable { get; }
    public DigitalPin VramReadBar { get; }
    public DigitalPin VramWriteBar { get; }
    public DigitalBus Extension { get; }
    public BufferedOutputPin<RicohVideoPixelSample> VideoOutput { get; }

    public int Dot { get; private set; }
    public int Scanline { get; private set; }
    public ulong Frame { get; private set; }
    public ulong MasterClockRisingEdgeCount => Clock.InputActivationEdgeCount;
    public ulong CompletedVramReadCount { get; private set; }
    public ulong CompletedVramWriteCount { get; private set; }
    public bool Vblank => _vblank;
    public bool NmiEnabled => _nmiEnabled;
    public byte ControlRegister => _control;
    public byte MaskRegister => _mask;
    public byte OamAddress => _oamAddress;
    public ushort VramAddress => _vramAddress;
    public ushort TemporaryVramAddress => _temporaryAddress;
    public byte FineX => _fineX;
    public bool WriteToggle => _writeToggle;
    public byte ReadBuffer => _readBuffer;
    public bool VramTransactionActive => _transaction != VramTransaction.None;
    public ulong BackgroundNametableFetchCount { get; private set; }
    public ulong BackgroundAttributeFetchCount { get; private set; }
    public ulong BackgroundPatternFetchCount { get; private set; }
    public byte BackgroundPixelIndex { get; private set; }
    public byte NextTileId => _nextTileId;
    public byte NextTileAttribute => _nextTileAttribute;
    public ushort PatternShiftLow => (ushort)_backgroundShifters;
    public ushort PatternShiftHigh => (ushort)(_backgroundShifters >> 16);
    public ulong SpriteEvaluationCount { get; private set; }
    public ulong SpritePatternFetchCount { get; private set; }
    public int EvaluatedSpriteCount => _secondarySpriteCount;
    public bool SpriteOverflow => _spriteOverflow;
    public bool SpriteZeroHit => _spriteZeroHit;
    public byte SpritePixelIndex { get; private set; }
    public byte PixelPaletteIndex { get; private set; }
    public byte OutputColorCode { get; private set; }
    /// <summary>
    /// PAL RP2C07 logical colour-emphasis channels. The physical PPUMASK
    /// red and green controls are reversed relative to RP2C02. Returned bits
    /// remain R,G,B in positions 0,1,2 for board/output consumers.
    /// </summary>
    public byte ColorEmphasis => _decodedColorEmphasis;

    public bool IsPalTiming => true;
    public int ScanlinesPerFrameCount => ScanlinesPerFrame;
    public ulong NmiFallingEdgeCount { get; private set; }
    public ulong VblankSuppressionCount { get; private set; }
    public ulong RenderingOamWriteCount { get; private set; }
    public ulong ForcedBlankPaletteOutputCount { get; private set; }

    private void DecodeControlRegister()
    {
        // These are package-internal decode lines driven by the retained PPUCTRL
        // latch. Recompute only when that physical register changes rather than
        // re-decoding the same bits on every PPU dot.
        _nmiEnabled = (_control & 0x80) != 0;
        _cpuVramIncrement = (_control & 0x04) != 0 ? (ushort)32 : (ushort)1;
        _spritePatternTableBase = (_control & 0x08) != 0 ? (ushort)0x1000 : (ushort)0;
        _backgroundPatternTableBase = (_control & 0x10) != 0 ? (ushort)0x1000 : (ushort)0;
        _spriteHeight = (_control & 0x20) != 0 ? 16 : 8;
    }

    private void DecodeMaskRegister()
    {
        // PPUMASK likewise fans out to stable internal control lines. PAL swaps
        // the physical red/green emphasis controls at the package level.
        _greyscaleEnabled = (_mask & 0x01) != 0;
        _showBackgroundLeft = (_mask & 0x02) != 0;
        _showSpriteLeft = (_mask & 0x04) != 0;
        _backgroundRenderingEnabled = (_mask & 0x08) != 0;
        _spriteRenderingEnabled = (_mask & 0x10) != 0;
        _renderingEnabled = _backgroundRenderingEnabled || _spriteRenderingEnabled;
        var physicalEmphasis = (byte)((_mask >> 5) & 0x07);
        _decodedColorEmphasis = (byte)(((physicalEmphasis & 0x01) << 1)
            | ((physicalEmphasis & 0x02) >> 1)
            | (physicalEmphasis & 0x04));
    }

    private void RefreshCpuPortWakeState()
    {
        var enabled = _packagePowered
            && !_resetAsserted
            && ChipSelectBar.SampledLevel != DigitalLevel.High
            && !_cpuSelectedLast;
        RegisterSelect.SetOwnerWakeEnabled(enabled);
        CpuData.SetOwnerWakeEnabled(enabled);
        CpuReadWrite.SetOwnerWakeEnabled(enabled);
    }

    private bool Powered => Vcc.SampledLevel == DigitalLevel.High && Gnd.SampledLevel == DigitalLevel.Low;

    public byte InspectOam(byte address) => _primaryOam[address];
    public byte InspectPalette(ushort address) => ReadPalette(address);

    private void InitializePackageState()
    {
        Dot = 0;
        Scanline = 0;
        Frame = 0;
        Clock.ResetInputActivationCounter();
        CompletedVramReadCount = 0;
        CompletedVramWriteCount = 0;
        BackgroundNametableFetchCount = 0;
        BackgroundAttributeFetchCount = 0;
        BackgroundPatternFetchCount = 0;
        BackgroundPixelIndex = 0;
        SpritePixelIndex = 0;
        PixelPaletteIndex = 0;
        OutputColorCode = 0;
        NmiFallingEdgeCount = 0;
        VblankSuppressionCount = 0;
        RenderingOamWriteCount = 0;
        ForcedBlankPaletteOutputCount = 0;
        SpriteEvaluationCount = 0;
        SpritePatternFetchCount = 0;
        _secondarySpriteCount = 0;
        _activeSpriteCount = 0;
        _nextSpriteCount = 0;
        _nmiAsserted = false;
        _suppressVblankSet = false;
        _spriteEvaluationIndex = 0;
        _spriteFetchSlot = 0;
        _spriteOverflowByteOffset = 0;
        _oamDataBusLatch = 0xFF;
        _nmiAsserted = false;
        Array.Clear(_secondaryOam);
        Array.Clear(_activeSprites);
        Array.Clear(_nextSprites);
        _cpuSelectedLast = false;
        _cpuReadLatchValid = false;
        _cpuReadLatch = 0;
        _vblank = false;
        _suppressVblankSet = false;
        _spriteZeroHit = false;
        _spriteOverflow = false;
        _control = 0;
        DecodeControlRegister();
        _mask = 0;
        DecodeMaskRegister();
        _oamAddress = 0;
        _openBus = 0;
        _readBuffer = 0;
        _vramAddress = 0;
        _temporaryAddress = 0;
        _fineX = 0;
        _writeToggle = false;
        _transaction = VramTransaction.None;
        _transactionPhase = 0;
        _transactionCompletionPhase = 0;
        _transactionPurpose = VramTransactionPurpose.None;
        _nextTileId = 0;
        _nextTileAttribute = 0;
        _nextBackgroundLoad = 0;
        _backgroundShifters = 0;
        _backgroundTapShift = 15;
        Array.Clear(_primaryOam);
        Array.Clear(_paletteRam);
        ReleasePackageOutputs();
    }

    private void ApplyResetState()
    {
        Dot = 0;
        Scanline = 0;
        _vblank = false;
        _spriteZeroHit = false;
        _spriteOverflow = false;
        _control = 0;
        DecodeControlRegister();
        _mask = 0;
        DecodeMaskRegister();
        _writeToggle = false;
        _cpuSelectedLast = false;
        _transaction = VramTransaction.None;
        _transactionPhase = 0;
        _transactionCompletionPhase = 0;
        _transactionPurpose = VramTransactionPurpose.None;
        BackgroundPixelIndex = 0;
        SpritePixelIndex = 0;
        PixelPaletteIndex = 0;
        _secondarySpriteCount = 0;
        _activeSpriteCount = 0;
        _nextSpriteCount = 0;
        _spriteOverflowByteOffset = 0;
        _oamDataBusLatch = 0xFF;
        Array.Clear(_secondaryOam);
        Array.Clear(_activeSprites);
        Array.Clear(_nextSprites);
        _backgroundShifters = 0;
        ReleasePackageOutputs();
    }

    protected override void OnInputChanges(ulong changedInputMask)
    {
        if (changedInputMask == _clockInputMask && _packagePowered && !_resetAsserted)
        {
            // The divided package clock is already a decoded internal PPU-dot
            // enable. Execute the hardwired dot circuit directly; no generic
            // rendering-stage polling is performed on the steady-state path.
            ClockPpuDot();
            return;
        }

        // Every package pin has already accepted its new electrical level before
        // this method is entered.  Keep the chip smart: if power is absent, or
        // if ordinary CPU-register pins move while /CS is definitely inactive,
        // stop here before touching any PPU state.  /CS itself always wakes the
        // register interface so selection/deselection is handled immediately.
        var powerChanged = (changedInputMask & _powerInputMask) != 0;
        if (!_packagePowered && !powerChanged) return;

        var clockChanged = (changedInputMask & _clockInputMask) != 0;
        var resetChanged = (changedInputMask & _resetInputMask) != 0;
        var chipSelectChanged = (changedInputMask & _cpuChipSelectInputMask) != 0;
        var ordinaryCpuPortChanged = (changedInputMask & _cpuOrdinaryInputMask) != 0;
        if (!powerChanged && !clockChanged && !resetChanged && !chipSelectChanged)
        {
            if (!ordinaryCpuPortChanged) return;
            if (ChipSelectBar.SampledLevel == DigitalLevel.High) return;
        }

        var newlyPowered = false;
        if (!_packagePowered || (changedInputMask & _powerInputMask) != 0)
        {
            if (!Powered)
            {
                ReleasePackageOutputs();
                _packagePowered = false;
                _resetAsserted = false;
                _cpuSelectedLast = false;
                RefreshCpuPortWakeState();
                return;
            }

            if (!_packagePowered)
            {
                InitializePackageState();
                _packagePowered = true;
                newlyPowered = true;
                PresentVramIdle();
                RefreshCpuPortWakeState();
            }
        }

        if (_resetAsserted)
        {
            if (!resetChanged || ResetBar.SampledLevel == DigitalLevel.Low) return;
            _resetAsserted = false;
            PresentVramIdle();
            RefreshCpuPortWakeState();
        }
        else if ((newlyPowered || resetChanged) && ResetBar.SampledLevel == DigitalLevel.Low)
        {
            ApplyResetState();
            _resetAsserted = true;
            RefreshCpuPortWakeState();
            return;
        }

        if ((changedInputMask & _cpuPortInputMask) != 0
            && (chipSelectChanged || ChipSelectBar.SampledLevel != DigitalLevel.High)
            && (chipSelectChanged || !_cpuSelectedLast))
        {
            // /CS owns the transaction boundary. Once the CPU port has latched
            // this selected cycle, later RS/RW/D settling is electrically
            // visible at the pins but cannot create another register access.
            HandleCpuPort();
            RefreshCpuPortWakeState();
        }

        if (clockChanged && Clock.SampledLevel == DigitalLevel.High)
            ClockPpuDot();
    }

    protected override void OnInputChangesProfiled(
        ulong changedInputMask,
        VirtualHardwareProfileSample sample)
    {
        // Every package pin has already accepted its new electrical level before
        // this method is entered.  Keep the chip smart: if power is absent, or
        // if ordinary CPU-register pins move while /CS is definitely inactive,
        // stop here before touching any PPU state.  /CS itself always wakes the
        // register interface so selection/deselection is handled immediately.
        var powerChanged = (changedInputMask & _powerInputMask) != 0;
        if (!_packagePowered && !powerChanged) return;

        var clockChanged = (changedInputMask & _clockInputMask) != 0;
        var resetChanged = (changedInputMask & _resetInputMask) != 0;
        var chipSelectChanged = (changedInputMask & _cpuChipSelectInputMask) != 0;
        var ordinaryCpuPortChanged = (changedInputMask & _cpuOrdinaryInputMask) != 0;
        if (!powerChanged && !clockChanged && !resetChanged && !chipSelectChanged)
        {
            if (!ordinaryCpuPortChanged) return;
            if (ChipSelectBar.SampledLevel == DigitalLevel.High) return;
        }

        var newlyPowered = false;
        if (!_packagePowered || (changedInputMask & _powerInputMask) != 0)
        {
            if (!Powered)
            {
                ReleasePackageOutputs();
                _packagePowered = false;
                _resetAsserted = false;
                _cpuSelectedLast = false;
                RefreshCpuPortWakeState();
                return;
            }

            if (!_packagePowered)
            {
                InitializePackageState();
                _packagePowered = true;
                newlyPowered = true;
                PresentVramIdle();
                RefreshCpuPortWakeState();
            }
        }

        if (_resetAsserted)
        {
            if (!resetChanged || ResetBar.SampledLevel == DigitalLevel.Low) return;
            _resetAsserted = false;
            PresentVramIdle();
            RefreshCpuPortWakeState();
        }
        else if ((newlyPowered || resetChanged) && ResetBar.SampledLevel == DigitalLevel.Low)
        {
            ApplyResetState();
            _resetAsserted = true;
            RefreshCpuPortWakeState();
            return;
        }

        if ((changedInputMask & _cpuPortInputMask) != 0
            && (chipSelectChanged || ChipSelectBar.SampledLevel != DigitalLevel.High)
            && (chipSelectChanged || !_cpuSelectedLast))
        {
            // /CS owns the transaction boundary. Once the CPU port has latched
            // this selected cycle, later RS/RW/D settling is electrically
            // visible at the pins but cannot create another register access.
            var cpuPortStarted = sample.BeginSection();
            HandleCpuPort();
            sample.EndSection(VirtualHardwareProfileSection.Rp2C02CpuPort, cpuPortStarted);
            RefreshCpuPortWakeState();
        }

        if (clockChanged && Clock.SampledLevel == DigitalLevel.High)
            ClockPpuDotProfiled(sample);
    }

    private void ClockPpuDot()
    {
        AdvanceRaster();
        var vramTransactionCompleted = AdvanceVramTransaction();

        var visibleScanline = Scanline < 240;
        if (_renderingEnabled && (visibleScanline || Scanline == PreRenderScanline))
        {
            var decodeLines = (PpuDotDecoder.Lines)PpuDotDecoder.DecodeLines[Dot];
            ExecuteDecodedBackgroundCircuit(decodeLines, visibleScanline);
            ExecuteDecodedSpriteCircuit(decodeLines, visibleScanline);
        }
        else if (visibleScanline && Dot is >= 1 and <= 256)
        {
            // Forced blank disconnects the fetch/OAM sequencers. The color DAC
            // remains physically active and may expose palette RAM selected by v.
            BackgroundPixelIndex = 0;
            SpritePixelIndex = 0;
            PixelPaletteIndex = 0;
            UpdateOutputColor();
        }

        if (vramTransactionCompleted && _transaction == VramTransaction.None)
            PresentVramIdle();

        if (visibleScanline && Dot is >= 1 and <= 256)
        {
            VideoOutput.Drive(new RicohVideoPixelSample(
                Frame,
                Dot - 1,
                Scanline,
                OutputColorCode,
                ColorEmphasis));
        }
    }

    private void ClockPpuDotProfiled(VirtualHardwareProfileSample sample)
    {
        var rasterStarted = sample.BeginSection();
        AdvanceRaster();
        sample.EndSection(VirtualHardwareProfileSection.Rp2C02Raster, rasterStarted);

        var vramStarted = sample.BeginSection();
        var vramTransactionCompleted = AdvanceVramTransaction();
        sample.EndSection(VirtualHardwareProfileSection.Rp2C02Vram, vramStarted);

        var visibleScanline = Scanline < 240;
        if (_renderingEnabled && (visibleScanline || Scanline == PreRenderScanline))
        {
            var decodeLines = (PpuDotDecoder.Lines)PpuDotDecoder.DecodeLines[Dot];

            var backgroundStarted = sample.BeginSection();
            ExecuteDecodedBackgroundCircuit(decodeLines, visibleScanline);
            sample.EndSection(VirtualHardwareProfileSection.Rp2C02Background, backgroundStarted);

            var spriteStarted = sample.BeginSection();
            ExecuteDecodedSpriteCircuit(decodeLines, visibleScanline);
            sample.EndSection(VirtualHardwareProfileSection.Rp2C02Sprite, spriteStarted);
        }
        else if (visibleScanline && Dot is >= 1 and <= 256)
        {
            BackgroundPixelIndex = 0;
            SpritePixelIndex = 0;
            PixelPaletteIndex = 0;
            UpdateOutputColor();
        }

        if (vramTransactionCompleted && _transaction == VramTransaction.None)
        {
            var outputsStarted = sample.BeginSection();
            PresentVramIdle();
            sample.EndSection(VirtualHardwareProfileSection.Rp2C02PackageOutputs, outputsStarted);
        }

        if (visibleScanline && Dot is >= 1 and <= 256)
        {
            var videoStarted = sample.BeginSection();
            VideoOutput.Drive(new RicohVideoPixelSample(
                Frame,
                Dot - 1,
                Scanline,
                OutputColorCode,
                ColorEmphasis));
            sample.EndSection(VirtualHardwareProfileSection.Rp2C02VideoOutput, videoStarted);
        }
    }

    private void ExecuteDecodedBackgroundCircuit(PpuDotDecoder.Lines lines, bool visibleScanline)
    {
        switch (lines & PpuDotDecoder.BackgroundCircuitMask)
        {
            case PpuDotDecoder.Lines.BackgroundShift:
                ShiftBackgroundRegisters();
                break;

            case PpuDotDecoder.Lines.BackgroundShift
                | PpuDotDecoder.Lines.BackgroundLoad
                | PpuDotDecoder.Lines.BackgroundNametable:
                ShiftBackgroundRegisters();
                LoadBackgroundShifters();
                BeginBackgroundRead(
                    (ushort)(0x2000 | (_vramAddress & 0x0FFF)),
                    VramTransactionPurpose.BackgroundNametable);
                break;

            case PpuDotDecoder.Lines.BackgroundShift
                | PpuDotDecoder.Lines.BackgroundAttribute:
            {
                ShiftBackgroundRegisters();
                var attributeAddress = (ushort)(0x23C0
                    | (_vramAddress & 0x0C00)
                    | ((_vramAddress >> 4) & 0x38)
                    | ((_vramAddress >> 2) & 0x07));
                BeginBackgroundRead(attributeAddress, VramTransactionPurpose.BackgroundAttribute);
                break;
            }

            case PpuDotDecoder.Lines.BackgroundShift
                | PpuDotDecoder.Lines.BackgroundPatternLow:
                ShiftBackgroundRegisters();
                BeginBackgroundRead(
                    PatternAddress(highPlane: false),
                    VramTransactionPurpose.BackgroundPatternLow);
                break;

            case PpuDotDecoder.Lines.BackgroundShift
                | PpuDotDecoder.Lines.BackgroundPatternHigh:
                ShiftBackgroundRegisters();
                BeginBackgroundRead(
                    PatternAddress(highPlane: true),
                    VramTransactionPurpose.BackgroundPatternHigh);
                break;

            case PpuDotDecoder.Lines.BackgroundShift
                | PpuDotDecoder.Lines.IncrementCoarseX:
                ShiftBackgroundRegisters();
                IncrementCoarseX();
                break;
        }

        if ((lines & PpuDotDecoder.Lines.IncrementY) != 0)
            IncrementY();
        if ((lines & PpuDotDecoder.Lines.CopyHorizontal) != 0)
            CopyHorizontalScrollBits();
        if (Scanline == PreRenderScanline
            && (lines & PpuDotDecoder.Lines.CopyVertical) != 0)
        {
            CopyVerticalScrollBits();
        }

        if (!visibleScanline || (lines & PpuDotDecoder.Lines.VisiblePixel) == 0)
            return;

        if (_backgroundRenderingEnabled) UpdateBackgroundPixel();
        else BackgroundPixelIndex = 0;
    }

    private void ExecuteDecodedSpriteCircuit(PpuDotDecoder.Lines lines, bool visibleScanline)
    {
        if ((lines & PpuDotDecoder.Lines.SpriteActivate) != 0)
        {
            _activeSpriteCount = _nextSpriteCount;
            Array.Copy(_nextSprites, _activeSprites, _activeSpriteCount);
        }

        if (visibleScanline
            && (lines & PpuDotDecoder.Lines.SpriteEvaluationReset) != 0)
        {
            _secondarySpriteCount = 0;
            _spriteEvaluationIndex = 0;
            _spriteOverflowByteOffset = 0;
            Array.Clear(_secondaryOam);
        }

        if (visibleScanline
            && (lines & PpuDotDecoder.Lines.SpriteEvaluate) != 0
            && _spriteEvaluationIndex < 64)
        {
            EvaluateOneSpriteForNextScanline(_spriteEvaluationIndex++);
        }

        if ((lines & PpuDotDecoder.Lines.SpriteLoad) != 0)
        {
            _nextSpriteCount = _secondarySpriteCount;
            for (var index = 0; index < _nextSpriteCount; index++)
            {
                var entry = _secondaryOam[index];
                _nextSprites[index] = new SpriteRenderUnit
                {
                    XCounter = entry.X,
                    Attributes = entry.Attributes,
                    SpriteZero = entry.SpriteIndex == 0,
                    Tile = entry.Tile,
                    Row = entry.Row
                };
            }
            for (var index = _nextSpriteCount; index < 8; index++)
                _nextSprites[index] = default;
        }

        if ((lines & (PpuDotDecoder.Lines.SpritePatternLow | PpuDotDecoder.Lines.SpritePatternHigh)) != 0)
        {
            _spriteFetchSlot = PpuDotDecoder.SpriteFetchSlot[Dot];
            if (_spriteFetchSlot < _nextSpriteCount)
            {
                if ((lines & PpuDotDecoder.Lines.SpritePatternLow) != 0)
                {
                    BeginBackgroundRead(
                        SpritePatternAddress(_nextSprites[_spriteFetchSlot], false),
                        VramTransactionPurpose.SpritePatternLow);
                }
                else
                {
                    BeginBackgroundRead(
                        SpritePatternAddress(_nextSprites[_spriteFetchSlot], true),
                        VramTransactionPurpose.SpritePatternHigh);
                }
            }
        }

        if (visibleScanline && (lines & PpuDotDecoder.Lines.SpriteVisibleClock) != 0)
            UpdateSpritePixelCompositionAndAdvance();
    }

    private void AdvanceRaster()
    {
        // PAL RP2C07 always emits the complete 341 x 312 raster. Unlike the
        // NTSC RP2C02 it never skips a pre-render dot on alternate frames.
        Dot++;
        if (Dot >= DotsPerScanline)
        {
            Dot = 0;
            Scanline++;
            if (Scanline >= ScanlinesPerFrame)
            {
                Scanline = 0;
                Frame++;
            }
        }

        if (Scanline == VblankStartScanline && Dot == 1)
        {
            if (_suppressVblankSet)
            {
                SetVblankState(false);
                _suppressVblankSet = false;
                VblankSuppressionCount++;
            }
            else SetVblankState(true);
        }
        else if (Scanline == PreRenderScanline && Dot == 1)
        {
            SetVblankState(false);
            _spriteZeroHit = false;
            _spriteOverflow = false;
        }
    }

    private void HandleCpuPort()
    {
        var selected = ChipSelectBar.SampledLevel == DigitalLevel.Low;
        if (!selected)
        {
            CpuData.Release();
            _cpuSelectedLast = false;
            _cpuReadLatchValid = false;
            return;
        }

        if (!RegisterSelect.TrySample(out var rawRegister))
        {
            CpuData.Release();
            return;
        }

        var register = (int)(rawRegister & 7);
        var read = CpuReadWrite.SampledLevel == DigitalLevel.High;
        if (read)
        {
            // A selected CPU read is one physical bus cycle even though the
            // electrical simulator may settle the component multiple times.
            // Latch the first result so side effects (PPUSTATUS clearing,
            // PPUDATA incrementing and transaction starts) occur only once and
            // D0-D7 remain stable until /CS is released.
            if (!_cpuReadLatchValid)
            {
                _cpuReadLatch = ReadCpuRegister(register, firstSelectedEvaluation: true);
                _cpuReadLatchValid = true;
            }
            CpuData.Drive(_cpuReadLatch);
        }
        else
        {
            _cpuReadLatchValid = false;
            CpuData.Release();
            if (!_cpuSelectedLast)
            {
                // /CS may arrive before the write byte has settled. Do not mark
                // the selected cycle consumed until a valid D0-D7 value exists.
                if (!CpuData.TrySample(out var rawValue)) return;
                var value = (byte)rawValue;
                _openBus = value;
                WriteCpuRegister(register, value);
            }
        }

        _cpuSelectedLast = true;
    }

    private byte ReadCpuRegister(int register, bool firstSelectedEvaluation)
    {
        byte value;
        switch (register)
        {
            case 2: // PPUSTATUS
                value = (byte)((_vblank ? 0x80 : 0)
                    | (_spriteZeroHit ? 0x40 : 0)
                    | (_spriteOverflow ? 0x20 : 0)
                    | (_openBus & 0x1F));
                if (firstSelectedEvaluation)
                {
                    // Reading status at the scanline-241 boundary can prevent
                    // the vblank latch from setting at all, rather than merely
                    // clearing it after an /NMI edge has escaped.
                    if (Scanline == VblankStartScanline && Dot <= 1)
                    {
                        _suppressVblankSet = true;
                    }
                    SetVblankState(false);
                    _writeToggle = false;
                }
                break;
            case 4: // OAMDATA
                value = RenderingBusActive ? _oamDataBusLatch : _primaryOam[_oamAddress];
                break;
            case 7: // PPUDATA
                if ((_vramAddress & 0x3F00) == 0x3F00)
                {
                    // Palette RAM drives only D0-D5; D6-D7 retain the PPU open bus.
                    value = (byte)((_openBus & 0xC0) | ReadPalette(_vramAddress));
                    if (firstSelectedEvaluation)
                    {
                        // Palette reads bypass the delayed buffer, while the mirrored
                        // nametable byte still refills it through the external bus.
                        if (_transaction == VramTransaction.None)
                        {
                            StartVramTransactionAt((ushort)(_vramAddress & 0x2FFF), VramTransaction.Read, 0, VramTransactionPurpose.CpuRead);
                        }
                        IncrementVramAddressAfterCpuAccess();
                    }
                }
                else
                {
                    value = _readBuffer;
                    if (firstSelectedEvaluation)
                    {
                        if (_transaction == VramTransaction.None)
                        {
                            StartVramTransaction(VramTransaction.Read, 0, VramTransactionPurpose.CpuRead);
                        }
                        IncrementVramAddressAfterCpuAccess();
                    }
                }
                break;
            default:
                value = _openBus;
                break;
        }

        _openBus = value;
        return value;
    }

    private void WriteCpuRegister(int register, byte value)
    {
        switch (register)
        {
            case 0: // PPUCTRL
                _control = value;
                DecodeControlRegister();
                UpdateNmiOutput();
                _temporaryAddress = (ushort)((_temporaryAddress & ~0x0C00) | ((value & 0x03) << 10));
                break;
            case 1: // PPUMASK
                _mask = value;
                DecodeMaskRegister();
                break;
            case 3: // OAMADDR
                _oamAddress = value;
                break;
            case 4: // OAMDATA
                if (RenderingBusActive)
                {
                    // During rendering the OAM port is owned by sprite evaluation.
                    // CPU writes do not reach OAM and the address counter advances
                    // by four, matching the package's internal entry stride.
                    _oamAddress = (byte)(_oamAddress + 4);
                    RenderingOamWriteCount++;
                }
                else _primaryOam[_oamAddress++] = value;
                break;
            case 5: // PPUSCROLL
                if (!_writeToggle)
                {
                    _fineX = (byte)(value & 0x07);
                    _backgroundTapShift = (byte)(15 - _fineX);
                    _temporaryAddress = (ushort)((_temporaryAddress & ~0x001F) | (value >> 3));
                }
                else
                {
                    _temporaryAddress = (ushort)((_temporaryAddress & ~0x73E0)
                        | ((value & 0x07) << 12)
                        | ((value & 0xF8) << 2));
                }
                _writeToggle = !_writeToggle;
                break;
            case 6: // PPUADDR
                if (!_writeToggle)
                {
                    _temporaryAddress = (ushort)((_temporaryAddress & 0x00FF) | ((value & 0x3F) << 8));
                }
                else
                {
                    _temporaryAddress = (ushort)((_temporaryAddress & 0x7F00) | value);
                    _vramAddress = (ushort)(_temporaryAddress & 0x3FFF);
                }
                _writeToggle = !_writeToggle;
                break;
            case 7: // PPUDATA
                if ((_vramAddress & 0x3F00) == 0x3F00)
                {
                    WritePalette(_vramAddress, value);
                    IncrementVramAddressAfterCpuAccess();
                }
                else
                {
                    if (_transaction == VramTransaction.None)
                    {
                        StartVramTransaction(VramTransaction.Write, value, VramTransactionPurpose.CpuWrite);
                    }
                    IncrementVramAddressAfterCpuAccess();
                }
                break;
        }
    }

    private void StartVramTransaction(VramTransaction transaction, byte writeData, VramTransactionPurpose purpose)
        => StartVramTransactionAt((ushort)(_vramAddress & 0x3FFF), transaction, writeData, purpose);

    private void StartVramTransactionAt(ushort address, VramTransaction transaction, byte writeData, VramTransactionPurpose purpose)
    {
        _transaction = transaction;
        _transactionPhase = 0;
        _transactionCompletionPhase = purpose is VramTransactionPurpose.CpuRead or VramTransactionPurpose.CpuWrite ? 3 : 2;
        _transactionAddress = (ushort)(address & 0x3FFF);
        _transactionWriteData = writeData;
        _transactionPurpose = purpose;
        PresentVramAddressPhase();
    }

    private ushort _transactionAddress;
    private byte _transactionWriteData;

    private void IncrementVramAddressAfterCpuAccess()
    {
        if (RenderingBusActive)
        {
            // While either renderer owns the VRAM address generators, a $2007
            // access clocks both the horizontal and vertical increment paths.
            IncrementCoarseX();
            IncrementY();
            _vramAddress &= 0x7FFF;
            return;
        }

        _vramAddress = (ushort)((_vramAddress + (_cpuVramIncrement)) & 0x3FFF);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool AdvanceVramTransaction()
    {
        if (_transaction == VramTransaction.None) return false;

        var phase = ++_transactionPhase;
        if (phase == 1) PresentVramDataPhase();
        if (phase < _transactionCompletionPhase) return false;

        if (_transaction == VramTransaction.Read)
        {
            if (MultiplexedAddressData.TrySample(out var data))
            {
                CompleteRead((byte)data);
                CompletedVramReadCount++;
            }
        }
        else
        {
            CompletedVramWriteCount++;
        }

        _transaction = VramTransaction.None;
        _transactionPhase = 0;
        _transactionCompletionPhase = 0;
        _transactionPurpose = VramTransactionPurpose.None;
        return true;
    }

    private bool BackgroundRenderingEnabled => _backgroundRenderingEnabled;
    private bool RenderingEnabled => _renderingEnabled;
    private bool RenderingBusActive => RenderingEnabled
        && IsRenderingScanline()
        && ((Dot >= 1 && Dot <= 256) || (Dot >= 321 && Dot <= 340));

    private bool IsRenderingScanline() => Scanline < 240 || Scanline == PreRenderScanline;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void BeginBackgroundRead(ushort address, VramTransactionPurpose purpose)
    {
        if (_transaction != VramTransaction.None) return;

        // The internal fetch decoder starts a real package-level VRAM cycle at
        // this dot. Present the multiplexed address/ALE phase immediately; the
        // following PPU dot advances to the data-/RD phase and the next one
        // samples/completes the read. This is the same physical two-dot fetch
        // cadence the previous continuously-driven output stage exposed, but
        // without polling all package outputs on unrelated dots.
        _transaction = VramTransaction.Read;
        _transactionPhase = 0;
        _transactionCompletionPhase = 2;
        _transactionAddress = (ushort)(address & 0x3FFF);
        _transactionWriteData = 0;
        _transactionPurpose = purpose;
        PresentVramAddressPhase();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort PatternAddress(bool highPlane)
    {
        var fineY = (_vramAddress >> 12) & 7;
        return (ushort)(_backgroundPatternTableBase | (_nextTileId << 4) | fineY | (highPlane ? 8 : 0));
    }

    private void CompleteRead(byte data)
    {
        switch (_transactionPurpose)
        {
            case VramTransactionPurpose.CpuRead:
                _readBuffer = data;
                break;
            case VramTransactionPurpose.BackgroundNametable:
                _nextTileId = data;
                BackgroundNametableFetchCount++;
                break;
            case VramTransactionPurpose.BackgroundAttribute:
            {
                var shift = (byte)(((_vramAddress >> 4) & 4) | (_vramAddress & 2));
                _nextTileAttribute = (byte)((data >> shift) & 3);
                _nextBackgroundLoad &= 0x00000000FFFFFFFFUL;
                if ((_nextTileAttribute & 1) != 0) _nextBackgroundLoad |= BackgroundAttributeLowFill;
                if ((_nextTileAttribute & 2) != 0) _nextBackgroundLoad |= BackgroundAttributeHighFill;
                BackgroundAttributeFetchCount++;
                break;
            }
            case VramTransactionPurpose.BackgroundPatternLow:
                _nextBackgroundLoad = (_nextBackgroundLoad & ~0xFFUL) | data;
                BackgroundPatternFetchCount++;
                break;
            case VramTransactionPurpose.BackgroundPatternHigh:
                _nextBackgroundLoad = (_nextBackgroundLoad & ~(0xFFUL << 16)) | ((ulong)data << 16);
                BackgroundPatternFetchCount++;
                break;
            case VramTransactionPurpose.SpritePatternLow:
                if (_spriteFetchSlot < _nextSpriteCount)
                {
                    var unit = _nextSprites[_spriteFetchSlot];
                    unit.PatternLow = unit.HorizontalFlip ? ReverseBits(data) : data;
                    _nextSprites[_spriteFetchSlot] = unit;
                    SpritePatternFetchCount++;
                }
                break;
            case VramTransactionPurpose.SpritePatternHigh:
                if (_spriteFetchSlot < _nextSpriteCount)
                {
                    var unit = _nextSprites[_spriteFetchSlot];
                    unit.PatternHigh = unit.HorizontalFlip ? ReverseBits(data) : data;
                    _nextSprites[_spriteFetchSlot] = unit;
                    SpritePatternFetchCount++;
                }
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LoadBackgroundShifters()
    {
        _backgroundShifters = (_backgroundShifters & BackgroundLoadHighByteMask) | _nextBackgroundLoad;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ShiftBackgroundRegisters()
    {
        _backgroundShifters = (_backgroundShifters << 1) & BackgroundShiftLaneMask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateBackgroundPixel()
    {
        // Four independent physical shifter outputs are sampled by the fine-X
        // mux. They are packed only in the host representation; lane boundaries
        // remain masked exactly as four 16-bit shift registers.
        var taps = _backgroundShifters >> _backgroundTapShift;
        var pattern = (byte)((taps & 1) | ((taps >> 15) & 2));
        var palette = (byte)(((taps >> 32) & 1) | ((taps >> 47) & 2));
        BackgroundPixelIndex = pattern == 0 ? (byte)0 : (byte)((palette << 2) | pattern);
    }

    private void IncrementCoarseX()
    {
        if ((_vramAddress & 0x001F) == 31)
        {
            _vramAddress &= 0x7FE0;
            _vramAddress ^= 0x0400;
        }
        else _vramAddress++;
    }

    private void IncrementY()
    {
        if ((_vramAddress & 0x7000) != 0x7000)
        {
            _vramAddress += 0x1000;
            return;
        }

        _vramAddress &= 0x0FFF;
        var coarseY = (_vramAddress & 0x03E0) >> 5;
        if (coarseY == 29)
        {
            coarseY = 0;
            _vramAddress ^= 0x0800;
        }
        else if (coarseY == 31) coarseY = 0;
        else coarseY++;
        _vramAddress = (ushort)((_vramAddress & ~0x03E0) | (coarseY << 5));
    }

    private void CopyHorizontalScrollBits()
    {
        _vramAddress = (ushort)((_vramAddress & ~0x041F) | (_temporaryAddress & 0x041F));
    }

    private void CopyVerticalScrollBits()
    {
        _vramAddress = (ushort)((_vramAddress & ~0x7BE0) | (_temporaryAddress & 0x7BE0));
    }


    private bool SpriteRenderingEnabled => _spriteRenderingEnabled;

    private void EvaluateOneSpriteForNextScanline(int spriteIndex)
    {
        SpriteEvaluationCount++;
        var baseAddress = spriteIndex * 4;
        var targetScanline = Scanline == PreRenderScanline ? 0 : Scanline + 1;
        var height = _spriteHeight;

        if (_secondarySpriteCount >= 8)
        {
            // Once secondary OAM is full, the RP2C02's broken evaluation logic
            // advances diagonally through primary OAM. Non-Y bytes can therefore
            // be interpreted as Y coordinates and create false-positive overflow.
            var candidate = _primaryOam[baseAddress + _spriteOverflowByteOffset];
            _oamDataBusLatch = candidate;
            var rowCandidate = targetScanline - (candidate + 1);
            if (rowCandidate >= 0 && rowCandidate < height) _spriteOverflow = true;
            _spriteOverflowByteOffset = (_spriteOverflowByteOffset + 1) & 3;
            return;
        }

        var y = _primaryOam[baseAddress];
        _oamDataBusLatch = y;
        var row = targetScanline - (y + 1);
        if (row < 0 || row >= height) return;

        var attributes = _primaryOam[baseAddress + 2];
        if ((attributes & 0x80) != 0) row = height - 1 - row;
        _secondaryOam[_secondarySpriteCount++] = new SpriteEntry
        {
            Tile = _primaryOam[baseAddress + 1],
            Attributes = attributes,
            X = _primaryOam[baseAddress + 3],
            Row = (byte)row,
            SpriteIndex = (byte)spriteIndex
        };
    }

    private ushort SpritePatternAddress(SpriteRenderUnit sprite, bool highPlane)
    {
        if (_spriteHeight == 8)
        {
            return (ushort)(_spritePatternTableBase | (sprite.Tile << 4) | sprite.Row | (highPlane ? 8 : 0));
        }

        var tableBase = (sprite.Tile & 1) << 12;
        var tile = sprite.Tile & 0xFE;
        var row = sprite.Row;
        if (row >= 8)
        {
            tile++;
            row -= 8;
        }
        return (ushort)(tableBase | (tile << 4) | row | (highPlane ? 8 : 0));
    }

    private void UpdateSpritePixelCompositionAndAdvance()
    {
        byte spritePattern = 0;
        byte spritePalette = 0;
        bool spriteBehindBackground = false;
        bool spriteZero = false;
        var spriteOutputEnabled = _spriteRenderingEnabled && (Dot > 8 || _showSpriteLeft);
        var haveOpaqueSprite = false;

        // The eight RP2C0x sprite output units operate in parallel in silicon.
        // Model their current pixel selection and their counter/shifter clock in
        // one pass instead of reading/writing the same eight software units twice.
        for (var index = 0; index < _activeSpriteCount; index++)
        {
            var sprite = _activeSprites[index];

            if (spriteOutputEnabled && !haveOpaqueSprite && sprite.XCounter == 0)
            {
                var patternValue = (byte)(((sprite.PatternLow & 0x80) != 0 ? 1 : 0)
                    | ((sprite.PatternHigh & 0x80) != 0 ? 2 : 0));
                if (patternValue != 0)
                {
                    spritePattern = patternValue;
                    spritePalette = (byte)(sprite.Attributes & 3);
                    spriteBehindBackground = (sprite.Attributes & 0x20) != 0;
                    spriteZero = sprite.SpriteZero;
                    haveOpaqueSprite = true;
                }
            }

            if (sprite.XCounter > 0) sprite.XCounter--;
            else
            {
                sprite.PatternLow <<= 1;
                sprite.PatternHigh <<= 1;
            }
            _activeSprites[index] = sprite;
        }

        var background = BackgroundPixelIndex;
        if (Dot <= 8 && !_showBackgroundLeft) background = 0;
        var backgroundOpaque = (background & 3) != 0;
        var spriteOpaque = spritePattern != 0;
        SpritePixelIndex = spriteOpaque ? (byte)(0x10 | (spritePalette << 2) | spritePattern) : (byte)0;

        if (spriteZero && spriteOpaque && backgroundOpaque && Dot < 256) _spriteZeroHit = true;

        if (!spriteOpaque) PixelPaletteIndex = background;
        else if (!backgroundOpaque || !spriteBehindBackground) PixelPaletteIndex = SpritePixelIndex;
        else PixelPaletteIndex = background;
        UpdateOutputColor();
    }

    private static byte ReverseBits(byte value)
    {
        value = (byte)(((value & 0x55) << 1) | ((value >> 1) & 0x55));
        value = (byte)(((value & 0x33) << 2) | ((value >> 2) & 0x33));
        return (byte)((value << 4) | (value >> 4));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PresentVramAddressPhase()
    {
        PresentHighAddress((byte)(_transactionAddress >> 8));
        PresentAd((byte)_transactionAddress);
        PresentAle(DigitalLevel.High);
        PresentReadBar(DigitalLevel.High);
        PresentWriteBar(DigitalLevel.High);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PresentVramDataPhase()
    {
        PresentHighAddress((byte)(_transactionAddress >> 8));
        PresentAle(DigitalLevel.Low);
        if (_transaction == VramTransaction.Read)
        {
            PresentAdReleased();
            PresentReadBar(DigitalLevel.Low);
            PresentWriteBar(DigitalLevel.High);
        }
        else
        {
            PresentAd(_transactionWriteData);
            PresentReadBar(DigitalLevel.High);
            PresentWriteBar(DigitalLevel.Low);
        }
    }

    private void PresentVramIdle()
    {
        PresentAdReleased();
        PresentHighAddressReleased();
        PresentAle(DigitalLevel.Low);
        PresentReadBar(DigitalLevel.High);
        PresentWriteBar(DigitalLevel.High);
    }

    private void PresentAd(byte value)
    {
        if (_presentedAdDriven && _presentedAdValue == value) return;
        MultiplexedAddressData.Drive(value);
        _presentedAdDriven = true;
        _presentedAdValue = value;
    }

    private void PresentAdReleased()
    {
        if (!_presentedAdDriven) return;
        MultiplexedAddressData.Release();
        _presentedAdDriven = false;
    }

    private void PresentHighAddress(byte value)
    {
        value &= 0x3F;
        if (_presentedHighAddressDriven && _presentedHighAddressValue == value) return;
        HighAddress.Drive(value);
        _presentedHighAddressDriven = true;
        _presentedHighAddressValue = value;
    }

    private void PresentHighAddressReleased()
    {
        if (!_presentedHighAddressDriven) return;
        HighAddress.Release();
        _presentedHighAddressDriven = false;
    }

    private void PresentAle(DigitalLevel level)
    {
        if (_presentedAle == level) return;
        AddressLatchEnable.Drive(level);
        _presentedAle = level;
    }

    private void PresentReadBar(DigitalLevel level)
    {
        if (_presentedReadBar == level) return;
        VramReadBar.Drive(level);
        _presentedReadBar = level;
    }

    private void PresentWriteBar(DigitalLevel level)
    {
        if (_presentedWriteBar == level) return;
        VramWriteBar.Drive(level);
        _presentedWriteBar = level;
    }

    private void SetVblankState(bool value)
    {
        if (_vblank == value) return;
        _vblank = value;
        UpdateNmiOutput();
    }

    private void UpdateNmiOutput()
    {
        var assert = _vblank && _nmiEnabled;
        if (assert == _nmiAsserted) return;

        if (assert)
        {
            NmiBar.Drive(DigitalLevel.Low);
            NmiFallingEdgeCount++;
        }
        else NmiBar.Release();
        _nmiAsserted = assert;
    }

    private static int PaletteIndex(ushort address)
    {
        var index = address & 0x1F;
        if ((index & 0x13) == 0x10) index &= 0x0F;
        return index;
    }

    private byte ReadPalette(ushort address) => _paletteRam[PaletteIndex(address)];

    private void WritePalette(ushort address, byte value) => _paletteRam[PaletteIndex(address)] = (byte)(value & 0x3F);

    private void UpdateOutputColor()
    {
        ushort paletteAddress;
        if (!_renderingEnabled && (_vramAddress & 0x3F00) == 0x3F00)
        {
            // During forced blank the external pixel pipeline is disconnected;
            // when v points into palette space, that palette entry appears at
            // the package color output instead of the universal background.
            paletteAddress = (ushort)(_vramAddress & 0x3FFF);
            ForcedBlankPaletteOutputCount++;
        }
        else
        {
            var paletteIndex = PpuDotDecoder.PaletteIndex[PixelPaletteIndex & 0x1F];
            var paletteColor = _paletteRam[paletteIndex];
            if (_greyscaleEnabled) paletteColor &= 0x30;
            OutputColorCode = paletteColor;
            return;
        }

        var forcedBlankColor = ReadPalette(paletteAddress);
        if (_greyscaleEnabled) forcedBlankColor &= 0x30;
        OutputColorCode = forcedBlankColor;
    }

    private void ReleasePackageOutputs()
    {
        CpuData.Release();
        MultiplexedAddressData.Release();
        HighAddress.Release();
        Extension.Release();
        AddressLatchEnable.Release();
        VramReadBar.Release();
        VramWriteBar.Release();
        NmiBar.Release();

        _presentedAdDriven = false;
        _presentedHighAddressDriven = false;
        _presentedAle = DigitalLevel.HighImpedance;
        _presentedReadBar = DigitalLevel.HighImpedance;
        _presentedWriteBar = DigitalLevel.HighImpedance;
    }

    private DigitalBus CreateBus(string prefix, int width, PinDirection direction, int firstBitNumber = 0)
    {
        var pins = new DigitalPin[width];
        for (var bit = 0; bit < width; bit++) pins[bit] = AddPin($"{prefix}{bit + firstBitNumber}", direction);
        return new DigitalBus($"{ComponentId}.{prefix}", pins);
    }

    private enum VramTransaction { None, Read, Write }

    private enum VramTransactionPurpose
    {
        None,
        CpuRead,
        CpuWrite,
        BackgroundNametable,
        BackgroundAttribute,
        BackgroundPatternLow,
        BackgroundPatternHigh,
        SpritePatternLow,
        SpritePatternHigh
    }

    private struct SpriteEntry
    {
        public byte Tile;
        public byte Attributes;
        public byte X;
        public byte Row;
        public byte SpriteIndex;
    }

    private struct SpriteRenderUnit
    {
        public byte Tile;
        public byte Attributes;
        public byte XCounter;
        public byte Row;
        public byte PatternLow;
        public byte PatternHigh;
        public bool SpriteZero;
        public bool HorizontalFlip => (Attributes & 0x40) != 0;
    }
}
