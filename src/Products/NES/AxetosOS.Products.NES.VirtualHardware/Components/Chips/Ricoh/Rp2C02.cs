using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;

public sealed record Rp2C02SplitTraceEvent(
    ulong Frame,
    int Scanline,
    int Dot,
    string Operation,
    byte Value,
    ushort VramAddress,
    ushort TemporaryAddress,
    byte FineX,
    bool WriteToggle);

public enum Rp2C02RenderingFetchKind
{
    BackgroundNametable,
    BackgroundAttribute,
    BackgroundPatternLow,
    BackgroundPatternHigh,
    DummyNametable,
    SpritePatternLow,
    SpritePatternHigh
}

public readonly record struct Rp2C02RenderingFetchTraceEvent(
    ulong Frame,
    int Scanline,
    int Dot,
    Rp2C02RenderingFetchKind Kind,
    ushort Address,
    byte Data,
    ushort VramAddress,
    ushort TemporaryAddress);

public sealed record Rp2C02DiagnosticSnapshot(
    ulong Frame,
    int Scanline,
    int Dot,
    byte Control,
    byte Mask,
    byte OamAddress,
    byte OpenBus,
    byte ReadBuffer,
    ushort VramAddress,
    ushort TemporaryAddress,
    byte FineX,
    bool WriteToggle,
    byte NextTileId,
    byte NextTileAttribute,
    ulong NextBackgroundLoad,
    ulong BackgroundShifters,
    int SecondarySpriteCount,
    int ActiveSpriteCount,
    int NextSpriteCount,
    byte BackgroundPixelIndex,
    byte SpritePixelIndex,
    byte PixelPaletteIndex,
    byte OutputColorCode,
    byte ColorEmphasis,
    ulong PaletteRamHash,
    ulong PrimaryOamHash,
    ulong SecondaryOamHash,
    ulong ActiveSpriteHash,
    ulong NextSpriteHash,
    ulong PipelineStateHash,
    ulong CompletedVramReads,
    ulong CompletedVramWrites,
    ulong BackgroundNametableFetches,
    ulong BackgroundAttributeFetches,
    ulong BackgroundPatternFetches,
    ulong DummyNametableFetches,
    ulong SpritePatternFetches);

/// <summary>
/// Standalone NTSC Ricoh RP2C02 package. All observable behaviour is driven by
/// package power, reset, clock and bus pins. The chip owns only physical
/// internal state (registers, address latches, read buffer and primary OAM).
/// External PPU memory is accessed exclusively through AD0-AD7, A8-A13, ALE,
/// /RD and /WR.
/// </summary>
public sealed class Rp2C02 : VirtualHardwareComponent, ICompiledBusMasterProvider, ICompiledBusTargetProvider, ICompiledClockedComponent
{
    private const ulong DiagnosticHashOffset = 14_695_981_039_346_656_037UL;
    private const ulong DiagnosticHashPrime = 1_099_511_628_211UL;
    private const int DotsPerScanline = 341;
    private const int ScanlinesPerFrame = 262;
    private const int VblankStartScanline = 241;
    private const int PreRenderScanline = 261;
    // CPU-side VRAM address changes reach the internal address generators on
    // later PPU clock phases rather than at the instant /CS selects the port.
    private const int CpuVramAddressCommitDelayDots = 3;
    private const int CpuVramIncrementDelayDots = 6;
    // Four 16-bit background shift-register lanes are physically clocked in
    // parallel. Keep the same four retained hardware lanes in one 64-bit word
    // so one host operation advances all of them without changing PPU state.
    private const ulong BackgroundShiftLaneMask = 0xFFFEFFFEFFFEFFFEUL;
    private const ulong BackgroundLoadHighByteMask = 0xFF00FF00FF00FF00UL;
    private const ulong BackgroundAttributeLowFill = 0x000000FF00000000UL;
    private const ulong BackgroundAttributeHighFill = 0x00FF000000000000UL;
    private const ulong SpriteTileMask = 0x00000000000000FFUL;
    private const ulong SpriteXMask = 0x0000000000FF0000UL;
    private const ulong SpriteRowMask = 0x00000000FF000000UL;
    private const ulong SpritePatternLowMask = 0x000000FF00000000UL;
    private const ulong SpritePatternHighMask = 0x0000FF0000000000UL;
    private const ulong SpritePatternMask = SpritePatternLowMask | SpritePatternHighMask;
    private const ulong SpritePatternShiftLaneMask = 0x0000FEFE00000000UL;
    private const ulong SpriteZeroMask = 1UL << 48;
    private const ulong SpriteHorizontalFlipMask = 1UL << 14;
    private const ulong SpriteBehindBackgroundMask = 1UL << 13;


    private readonly byte[] _primaryOam = new byte[256];
    private readonly byte[] _paletteRam = new byte[32];
    private bool _cpuTransactionActive;
    private bool _cpuTransactionRead;
    private int _cpuTransactionRegister;
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
    private ulong _ppuDotEpoch;
    private ulong _cpuVramIncrementSchedule;
    private bool _vramAddressCommitPending;
    private ulong _vramAddressCommitDueEpoch;
    private ushort _pendingVramAddressCommit;
    private VramTransaction _transaction;
    private int _transactionPhase;
    private byte _renderReadPhase;
    private ushort _renderReadAddress;
    private VramTransactionPurpose _renderReadPurpose;
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
    // Eight sprite circuits are retained as eight packed 64-bit state words.
    // Packing changes only the host representation: each word still contains
    // one sprite unit's independent tile/attribute/X/row/pattern/zero state.
    private readonly ulong[] _secondaryOam = new ulong[8];
    private readonly ulong[] _activeSprites = new ulong[8];
    private readonly ulong[] _nextSprites = new ulong[8];
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
    private ICompiledBusFabric? _compiledBusFabric;
    private bool _compiledResetAsserted;
    private byte _compiledVramReadLatch;
    private readonly ulong _cpuOrdinaryInputMask;
    private readonly ulong _cpuChipSelectInputMask;

    public Rp2C02(string componentId) : base(componentId)
    {
        Vcc = AddPin("VCC", PinDirection.Input);
        Gnd = AddPin("GND", PinDirection.Input);
        Clock = AddPin("CLK", PinDirection.Input, DigitalInputActivation.RisingEdge, 4);
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
        SplitTraceOutput = new BufferedOutputPin<Rp2C02SplitTraceEvent>(
            $"{componentId}.SPLIT_TRACE");
        RenderingFetchTraceOutput = new BufferedOutputPin<Rp2C02RenderingFetchTraceEvent>(
            $"{componentId}.RENDER_FETCH_TRACE");
    
        _powerInputMask = Vcc.InputChangeMask | Gnd.InputChangeMask;
        _clockInputMask = Clock.InputChangeMask;
        _resetInputMask = ResetBar.InputChangeMask;
        _cpuOrdinaryInputMask = RegisterSelect.InputChangeMask
            | CpuData.InputChangeMask
            | CpuReadWrite.InputChangeMask;
        _cpuChipSelectInputMask = ChipSelectBar.InputChangeMask;
        _cpuPortInputMask = _cpuOrdinaryInputMask | _cpuChipSelectInputMask;

        // RS/RW/D are physically present at the package pins on every CPU bus
        // transition, but they cannot wake the register circuit while /CS is
        // inactive or after the current selected transaction has already latched.
        // /CS itself remains ungated and is therefore the activation switch.
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
    public BufferedOutputPin<Rp2C02SplitTraceEvent> SplitTraceOutput { get; }
    public BufferedOutputPin<Rp2C02RenderingFetchTraceEvent> RenderingFetchTraceOutput { get; }

    public int Dot { get; private set; }
    public int Scanline { get; private set; }
    public ulong Frame { get; private set; }
    public ulong MasterClockRisingEdgeCount => _compiledBusFabric?.ClockRisingEdges ?? Clock.InputActivationEdgeCount;
    public ulong CompletedVramReadCount { get; private set; }
    public ulong CompletedVramWriteCount { get; private set; }
    public ulong DelayedVramAddressCommitCount { get; private set; }
    public ulong DelayedPpudataIncrementCount { get; private set; }
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
    public bool VramTransactionActive => VramBusBusy;
    public ulong BackgroundNametableFetchCount { get; private set; }
    public ulong BackgroundAttributeFetchCount { get; private set; }
    public ulong BackgroundPatternFetchCount { get; private set; }
    public ulong DummyNametableFetchCount { get; private set; }
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
    public byte ColorEmphasis => _decodedColorEmphasis;
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
        // PPUMASK likewise fans out to stable package-internal control lines.
        _greyscaleEnabled = (_mask & 0x01) != 0;
        _showBackgroundLeft = (_mask & 0x02) != 0;
        _showSpriteLeft = (_mask & 0x04) != 0;
        _backgroundRenderingEnabled = (_mask & 0x08) != 0;
        _spriteRenderingEnabled = (_mask & 0x10) != 0;
        _renderingEnabled = _backgroundRenderingEnabled || _spriteRenderingEnabled;
        _decodedColorEmphasis = (byte)((_mask >> 5) & 0x07);
    }

    private void RefreshCpuPortWakeState()
    {
        var enabled = _packagePowered
            && !_resetAsserted
            && ChipSelectBar.SampledLevel != DigitalLevel.High
            && !_cpuTransactionActive;
        RegisterSelect.SetOwnerWakeEnabled(enabled);
        CpuData.SetOwnerWakeEnabled(enabled);
        CpuReadWrite.SetOwnerWakeEnabled(enabled);
    }

    private bool Powered => Vcc.SampledLevel == DigitalLevel.High && Gnd.SampledLevel == DigitalLevel.Low;

    public byte InspectOam(byte address) => _primaryOam[address];
    public byte InspectPalette(ushort address) => ReadPalette(address);

    /// <summary>
    /// Inspection-only snapshot of retained RP2C02 circuitry. This never feeds
    /// execution and exists so physical and compiled paths can be compared at
    /// one exact master-clock boundary without exposing software-emulator state.
    /// </summary>
    public Rp2C02DiagnosticSnapshot InspectDiagnosticState()
    {
        var paletteHash = HashBytes(_paletteRam);
        var primaryOamHash = HashBytes(_primaryOam);
        var secondaryOamHash = HashWords(_secondaryOam);
        var activeSpriteHash = HashWords(_activeSprites);
        var nextSpriteHash = HashWords(_nextSprites);

        var pipelineHash = DiagnosticHashOffset;
        HashWord(ref pipelineHash, Frame);
        HashWord(ref pipelineHash, (ulong)(uint)Scanline);
        HashWord(ref pipelineHash, (ulong)(uint)Dot);
        HashByte(ref pipelineHash, _control);
        HashByte(ref pipelineHash, _mask);
        HashByte(ref pipelineHash, _oamAddress);
        HashByte(ref pipelineHash, _openBus);
        HashByte(ref pipelineHash, _readBuffer);
        HashWord(ref pipelineHash, _vramAddress);
        HashWord(ref pipelineHash, _temporaryAddress);
        HashByte(ref pipelineHash, _fineX);
        HashByte(ref pipelineHash, _writeToggle ? (byte)1 : (byte)0);
        HashWord(ref pipelineHash, _cpuVramIncrementSchedule);
        HashByte(ref pipelineHash, _vramAddressCommitPending ? (byte)1 : (byte)0);
        HashWord(ref pipelineHash, _pendingVramAddressCommit);
        HashByte(ref pipelineHash, _nextTileId);
        HashByte(ref pipelineHash, _nextTileAttribute);
        HashWord(ref pipelineHash, _nextBackgroundLoad);
        HashWord(ref pipelineHash, _backgroundShifters);
        HashWord(ref pipelineHash, (ulong)(uint)_secondarySpriteCount);
        HashWord(ref pipelineHash, (ulong)(uint)_activeSpriteCount);
        HashWord(ref pipelineHash, (ulong)(uint)_nextSpriteCount);
        HashByte(ref pipelineHash, BackgroundPixelIndex);
        HashByte(ref pipelineHash, SpritePixelIndex);
        HashByte(ref pipelineHash, PixelPaletteIndex);
        HashByte(ref pipelineHash, OutputColorCode);
        HashByte(ref pipelineHash, ColorEmphasis);
        HashWord(ref pipelineHash, paletteHash);
        HashWord(ref pipelineHash, primaryOamHash);
        HashWord(ref pipelineHash, secondaryOamHash);
        HashWord(ref pipelineHash, activeSpriteHash);
        HashWord(ref pipelineHash, nextSpriteHash);

        return new Rp2C02DiagnosticSnapshot(
            Frame, Scanline, Dot, _control, _mask, _oamAddress, _openBus, _readBuffer,
            _vramAddress, _temporaryAddress, _fineX, _writeToggle, _nextTileId,
            _nextTileAttribute, _nextBackgroundLoad, _backgroundShifters,
            _secondarySpriteCount, _activeSpriteCount, _nextSpriteCount,
            BackgroundPixelIndex, SpritePixelIndex, PixelPaletteIndex, OutputColorCode,
            ColorEmphasis, paletteHash, primaryOamHash, secondaryOamHash, activeSpriteHash,
            nextSpriteHash, pipelineHash, CompletedVramReadCount, CompletedVramWriteCount,
            BackgroundNametableFetchCount, BackgroundAttributeFetchCount,
            BackgroundPatternFetchCount, DummyNametableFetchCount, SpritePatternFetchCount);
    }

    private static ulong HashBytes(ReadOnlySpan<byte> values)
    {
        var hash = DiagnosticHashOffset;
        foreach (var value in values) HashByte(ref hash, value);
        return hash;
    }

    private static ulong HashWords(ReadOnlySpan<ulong> values)
    {
        var hash = DiagnosticHashOffset;
        foreach (var value in values) HashWord(ref hash, value);
        return hash;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HashByte(ref ulong hash, byte value)
    {
        hash ^= value;
        hash *= DiagnosticHashPrime;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HashWord(ref ulong hash, ulong value)
    {
        for (var shift = 0; shift < 64; shift += 8)
            HashByte(ref hash, (byte)(value >> shift));
    }

    private void InitializePackageState()
    {
        Dot = 0;
        Scanline = 0;
        Frame = 0;
        Clock.ResetInputActivationCounter();
        CompletedVramReadCount = 0;
        CompletedVramWriteCount = 0;
        DelayedVramAddressCommitCount = 0;
        DelayedPpudataIncrementCount = 0;
        BackgroundNametableFetchCount = 0;
        BackgroundAttributeFetchCount = 0;
        BackgroundPatternFetchCount = 0;
        DummyNametableFetchCount = 0;
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
        _cpuTransactionActive = false;
        _cpuTransactionRead = false;
        _cpuTransactionRegister = 0;
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
        _ppuDotEpoch = 0;
        _cpuVramIncrementSchedule = 0;
        _vramAddressCommitPending = false;
        _vramAddressCommitDueEpoch = 0;
        _pendingVramAddressCommit = 0;
        _transaction = VramTransaction.None;
        _transactionPhase = 0;
        _renderReadPhase = 0;
        _renderReadAddress = 0;
        _renderReadPurpose = VramTransactionPurpose.None;
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
        _cpuVramIncrementSchedule = 0;
        _vramAddressCommitPending = false;
        _vramAddressCommitDueEpoch = 0;
        _pendingVramAddressCommit = 0;
        _cpuTransactionActive = false;
        _cpuTransactionRead = false;
        _cpuTransactionRegister = 0;
        _transaction = VramTransaction.None;
        _transactionPhase = 0;
        _renderReadPhase = 0;
        _renderReadAddress = 0;
        _renderReadPurpose = VramTransactionPurpose.None;
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

    internal void AttachCompiledBusFabric(ICompiledBusFabric fabric)
    {
        _compiledBusFabric = fabric ?? throw new ArgumentNullException(nameof(fabric));
        _compiledResetAsserted = ResetBar.SampledLevel == DigitalLevel.Low;
    }

    internal void DetachCompiledBusFabric() => _compiledBusFabric = null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadCompiledMemory(ushort address)
    {
        var fabric = _compiledBusFabric!;
        fabric.BeginRead(address);
        return fabric.CompleteRead(address, out var value) ? value : (byte)0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteCompiledMemory(ushort address, byte value) =>
        _compiledBusFabric!.Write(address, value);

    internal void SetCompiledResetAsserted(bool asserted)
    {
        _compiledResetAsserted = asserted;
        if (asserted && !_resetAsserted)
        {
            ApplyResetState();
            _resetAsserted = true;
        }
        else if (!asserted && _resetAsserted)
        {
            _resetAsserted = false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExecuteCompiledPpuDot()
    {
        if (!_packagePowered || _compiledResetAsserted || _resetAsserted) return;
        ClockPpuDot();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte CompiledCpuReadRegister(int register)
    {
        var value = ReadCpuRegister(register & 7, firstSelectedEvaluation: true);
        _cpuTransactionActive = false;
        _cpuTransactionRead = false;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CompiledCpuWriteRegister(int register, byte value)
    {
        _openBus = value;
        WriteCpuRegister(register & 7, value);
        _cpuTransactionActive = false;
        _cpuTransactionRead = false;
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
                _cpuTransactionActive = false;
                _cpuTransactionRead = false;
                RefreshCpuPortWakeState();
                CpuData.Release();
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
            && (chipSelectChanged || !_cpuTransactionActive))
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
                _cpuTransactionActive = false;
                _cpuTransactionRead = false;
                RefreshCpuPortWakeState();
                CpuData.Release();
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
            && (chipSelectChanged || !_cpuTransactionActive))
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
        AdvanceDelayedCpuAddressCircuits();

        var visibleScanline = Scanline < 240;
        var executionPlan = PpuDotDecoder.ExecutionPlan[Dot];
        var visibleDot = visibleScanline && (executionPlan & PpuDotDecoder.VisibleDot) != 0;
        if (_renderingEnabled && (visibleScanline || Scanline == PreRenderScanline))
        {
            ExecuteDecodedBackgroundCircuit(executionPlan, visibleScanline);
            ExecuteDecodedSpriteCircuit(executionPlan, visibleScanline);
        }
        else if (visibleDot)
        {
            // Forced blank disconnects the fetch/OAM sequencers. The color DAC
            // remains physically active and may expose palette RAM selected by v.
            BackgroundPixelIndex = 0;
            SpritePixelIndex = 0;
            PixelPaletteIndex = 0;
            UpdateOutputColor();
        }

        if (vramTransactionCompleted && !VramBusBusy)
            PresentVramIdle();

        if (visibleDot)
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
        AdvanceDelayedCpuAddressCircuits();
        sample.EndSection(VirtualHardwareProfileSection.Rp2C02Vram, vramStarted);

        var visibleScanline = Scanline < 240;
        var executionPlan = PpuDotDecoder.ExecutionPlan[Dot];
        var visibleDot = visibleScanline && (executionPlan & PpuDotDecoder.VisibleDot) != 0;
        if (_renderingEnabled && (visibleScanline || Scanline == PreRenderScanline))
        {

            var backgroundStarted = sample.BeginSection();
            ExecuteDecodedBackgroundCircuit(executionPlan, visibleScanline);
            sample.EndSection(VirtualHardwareProfileSection.Rp2C02Background, backgroundStarted);

            var spriteStarted = sample.BeginSection();
            ExecuteDecodedSpriteCircuit(executionPlan, visibleScanline);
            sample.EndSection(VirtualHardwareProfileSection.Rp2C02Sprite, spriteStarted);
        }
        else if (visibleDot)
        {
            BackgroundPixelIndex = 0;
            SpritePixelIndex = 0;
            PixelPaletteIndex = 0;
            UpdateOutputColor();
        }

        if (vramTransactionCompleted && !VramBusBusy)
        {
            var outputsStarted = sample.BeginSection();
            PresentVramIdle();
            sample.EndSection(VirtualHardwareProfileSection.Rp2C02PackageOutputs, outputsStarted);
        }

        if (visibleDot)
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ExecuteDecodedBackgroundCircuit(uint executionPlan, bool visibleScanline)
    {
        switch (executionPlan & PpuDotDecoder.BackgroundActionMask)
        {
            case PpuDotDecoder.BackgroundShift:
                ShiftBackgroundRegisters();
                break;

            case PpuDotDecoder.BackgroundNametable:
                ShiftBackgroundRegisters();
                LoadBackgroundShifters();
                BeginBackgroundRead(
                    (ushort)(0x2000 | (_vramAddress & 0x0FFF)),
                    VramTransactionPurpose.BackgroundNametable);
                break;

            case PpuDotDecoder.BackgroundAttribute:
            {
                ShiftBackgroundRegisters();
                var attributeAddress = (ushort)(0x23C0
                    | (_vramAddress & 0x0C00)
                    | ((_vramAddress >> 4) & 0x38)
                    | ((_vramAddress >> 2) & 0x07));
                BeginBackgroundRead(attributeAddress, VramTransactionPurpose.BackgroundAttribute);
                break;
            }

            case PpuDotDecoder.BackgroundPatternLow:
                ShiftBackgroundRegisters();
                BeginBackgroundRead(
                    PatternAddress(highPlane: false),
                    VramTransactionPurpose.BackgroundPatternLow);
                break;

            case PpuDotDecoder.BackgroundPatternHigh:
                ShiftBackgroundRegisters();
                BeginBackgroundRead(
                    PatternAddress(highPlane: true),
                    VramTransactionPurpose.BackgroundPatternHigh);
                break;

            case PpuDotDecoder.BackgroundIncrementCoarseX:
                ShiftBackgroundRegisters();
                IncrementCoarseX();
                break;

            case PpuDotDecoder.BackgroundDummyNametable:
                BeginBackgroundRead(
                    (ushort)(0x2000 | (_vramAddress & 0x0FFF)),
                    VramTransactionPurpose.DummyNametable);
                break;
        }

        if ((executionPlan & PpuDotDecoder.IncrementY) != 0)
            IncrementY();
        if ((executionPlan & PpuDotDecoder.CopyHorizontal) != 0)
            CopyHorizontalScrollBits();
        if (Scanline == PreRenderScanline
            && (executionPlan & PpuDotDecoder.CopyVertical) != 0)
        {
            CopyVerticalScrollBits();
        }

        if (!visibleScanline || (executionPlan & PpuDotDecoder.VisibleDot) == 0)
            return;

        if (_backgroundRenderingEnabled) UpdateBackgroundPixel();
        else BackgroundPixelIndex = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ExecuteDecodedSpriteCircuit(uint executionPlan, bool visibleScanline)
    {
        if ((executionPlan & PpuDotDecoder.SpriteActivate) != 0)
        {
            _activeSpriteCount = _nextSpriteCount;
            Array.Copy(_nextSprites, _activeSprites, _activeSpriteCount);
        }

        if (visibleScanline
            && (executionPlan & PpuDotDecoder.SpriteEvaluationReset) != 0)
        {
            _secondarySpriteCount = 0;
            _spriteEvaluationIndex = 0;
            _spriteOverflowByteOffset = 0;
            Array.Clear(_secondaryOam);
        }

        if (visibleScanline
            && (executionPlan & PpuDotDecoder.SpriteEvaluate) != 0
            && _spriteEvaluationIndex < 64)
        {
            EvaluateOneSpriteForNextScanline(_spriteEvaluationIndex++);
        }

        if ((executionPlan & PpuDotDecoder.SpriteLoad) != 0)
        {
            _nextSpriteCount = _secondarySpriteCount;
            for (var index = 0; index < _nextSpriteCount; index++)
            {
                var entry = _secondaryOam[index];
                var sprite = entry & 0x00000000FFFFFFFFUL;
                if (((entry >> 32) & 0xFF) == 0) sprite |= SpriteZeroMask;
                _nextSprites[index] = sprite;
            }
            for (var index = _nextSpriteCount; index < 8; index++)
                _nextSprites[index] = 0;
        }

        var spriteFetch = executionPlan & PpuDotDecoder.SpriteFetchMask;
        if (spriteFetch != PpuDotDecoder.SpriteFetchNone)
        {
            _spriteFetchSlot = (int)((executionPlan & PpuDotDecoder.SpriteSlotMask) >> PpuDotDecoder.SpriteSlotShift);
            if (_spriteFetchSlot < _nextSpriteCount)
            {
                var highPlane = spriteFetch == PpuDotDecoder.SpriteFetchPatternHigh;
                BeginBackgroundRead(
                    SpritePatternAddress(_nextSprites[_spriteFetchSlot], highPlane),
                    highPlane
                        ? VramTransactionPurpose.SpritePatternHigh
                        : VramTransactionPurpose.SpritePatternLow);
            }
        }

        if (visibleScanline && (executionPlan & PpuDotDecoder.VisibleDot) != 0)
            UpdateSpritePixelCompositionAndAdvance();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AdvanceRaster()
    {
        // The horizontal counter is the hot state. Keep scanline/vblank decode
        // off the normal dot path and enter it only on the actual decoded dots.
        var dot = Dot;
        if (dot == 339
            && Scanline == PreRenderScanline
            && (Frame & 1UL) != 0
            && _renderingEnabled)
        {
            Dot = 0;
            Scanline = 0;
            Frame++;
            return;
        }

        dot++;
        if (dot != DotsPerScanline)
        {
            Dot = dot;
            if (dot == 1) HandleRasterDotOne();
            return;
        }

        Dot = 0;
        var scanline = Scanline + 1;
        if (scanline == ScanlinesPerFrame)
        {
            Scanline = 0;
            Frame++;
        }
        else Scanline = scanline;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void HandleRasterDotOne()
    {
        if (Scanline == VblankStartScanline)
        {
            if (_suppressVblankSet)
            {
                SetVblankState(false);
                _suppressVblankSet = false;
                VblankSuppressionCount++;
            }
            else SetVblankState(true);
        }
        else if (Scanline == PreRenderScanline)
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
            _cpuTransactionActive = false;
            _cpuTransactionRead = false;
            return;
        }

        // /CS defines the physical CPU-to-PPU register transaction. Address,
        // R/W and D0-D7 are captured only when /CS first becomes active and
        // remain authoritative until /CS is released. This prevents transient
        // address or R/W changes during circuit settlement from creating a
        // second register access inside the same physical CPU bus cycle.
        if (!_cpuTransactionActive)
        {
            if (!RegisterSelect.TrySample(out var rawRegister))
            {
                CpuData.Release();
                return;
            }

            var register = (int)(rawRegister & 7);
            var read = CpuReadWrite.SampledLevel == DigitalLevel.High;
            if (read)
            {
                _cpuTransactionRegister = register;
                _cpuTransactionRead = true;
                _cpuReadLatch = ReadCpuRegister(register, firstSelectedEvaluation: true);
                _cpuTransactionActive = true;
            }
            else
            {
                // /CS can settle before D0-D7 become a valid write byte. Keep
                // the write transaction unlatched until the data bus is valid so
                // a later pin transition can complete the same physical cycle.
                CpuData.Release();
                if (!CpuData.TrySample(out var rawValue)) return;
                _cpuTransactionRegister = register;
                _cpuTransactionRead = false;
                _openBus = (byte)rawValue;
                WriteCpuRegister(register, _openBus);
                _cpuTransactionActive = true;
            }
        }

        if (_cpuTransactionRead) CpuData.Drive(_cpuReadLatch);
        else CpuData.Release();
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
                    TraceSplit("PPUSTATUS read", value);
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
                        if (!VramBusBusy)
                        {
                            StartVramTransactionAt((ushort)(_vramAddress & 0x2FFF), VramTransaction.Read, 0);
                        }
                        ScheduleVramAddressIncrementAfterCpuAccess();
                    }
                }
                else
                {
                    value = _readBuffer;
                    if (firstSelectedEvaluation)
                    {
                        if (!VramBusBusy)
                        {
                            StartVramTransaction(VramTransaction.Read, 0);
                        }
                        ScheduleVramAddressIncrementAfterCpuAccess();
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
                TraceSplit("PPUSCROLL write", value);
                break;
            case 6: // PPUADDR
                if (!_writeToggle)
                {
                    _temporaryAddress = (ushort)((_temporaryAddress & 0x00FF) | ((value & 0x3F) << 8));
                }
                else
                {
                    _temporaryAddress = (ushort)((_temporaryAddress & 0x7F00) | value);
                    ScheduleVramAddressCommit((ushort)(_temporaryAddress & 0x3FFF));
                }
                _writeToggle = !_writeToggle;
                TraceSplit("PPUADDR write", value);
                break;
            case 7: // PPUDATA
                if ((_vramAddress & 0x3F00) == 0x3F00)
                {
                    WritePalette(_vramAddress, value);
                    ScheduleVramAddressIncrementAfterCpuAccess();
                }
                else
                {
                    if (!VramBusBusy)
                    {
                        StartVramTransaction(VramTransaction.Write, value);
                    }
                    ScheduleVramAddressIncrementAfterCpuAccess();
                }
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void StartVramTransaction(VramTransaction transaction, byte writeData)
        => StartVramTransactionAt((ushort)(_vramAddress & 0x3FFF), transaction, writeData);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void StartVramTransactionAt(ushort address, VramTransaction transaction, byte writeData)
    {
        _transaction = transaction;
        _transactionPhase = 0;
        _transactionAddress = (ushort)(address & 0x3FFF);
        _transactionWriteData = writeData;
        if (_compiledBusFabric is null) PresentVramAddressPhase();
    }

    private ushort _transactionAddress;
    private byte _transactionWriteData;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ScheduleVramAddressCommit(ushort address)
    {
        _pendingVramAddressCommit = (ushort)(address & 0x3FFF);
        _vramAddressCommitDueEpoch = _ppuDotEpoch + CpuVramAddressCommitDelayDots;
        _vramAddressCommitPending = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ScheduleVramAddressIncrementAfterCpuAccess()
    {
        // Keep this timing entirely chip-local. CPU register selection may land
        // anywhere between PPU edges; six following PPU edges reproduce the
        // observed five/six-dot propagation interval without a global scheduler.
        var dueEpoch = _ppuDotEpoch + CpuVramIncrementDelayDots;
        _cpuVramIncrementSchedule |= 1UL << (int)(dueEpoch & 63);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AdvanceDelayedCpuAddressCircuits()
    {
        _ppuDotEpoch++;

        // The second $2006 write reaches v before the rendering decoder for the
        // same dot. Horizontal/vertical copy circuitry can therefore overwrite
        // the corresponding bits on collision, as on the physical address path.
        if (_vramAddressCommitPending && _ppuDotEpoch >= _vramAddressCommitDueEpoch)
        {
            _vramAddress = _pendingVramAddressCommit;
            _vramAddressCommitPending = false;
            DelayedVramAddressCommitCount++;
        }

        var incrementBit = 1UL << (int)(_ppuDotEpoch & 63);
        if ((_cpuVramIncrementSchedule & incrementBit) == 0) return;

        _cpuVramIncrementSchedule &= ~incrementBit;
        IncrementVramAddressAfterCpuAccess();
        DelayedPpudataIncrementCount++;
    }

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
        if (_renderReadPhase != 0)
            return AdvanceRenderingVramRead();

        return _transaction != VramTransaction.None && AdvanceCpuVramTransaction();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool AdvanceRenderingVramRead()
    {
        if (_renderReadPhase == 1)
        {
            _renderReadPhase = 2;
            if (_compiledBusFabric is not null)
                _compiledVramReadLatch = ReadCompiledMemory(_renderReadAddress);
            else PresentRenderingVramDataPhase();
            return false;
        }

        if (_compiledBusFabric is not null)
        {
            CompleteRenderingRead(_compiledVramReadLatch);
            CompletedVramReadCount++;
        }
        else if (MultiplexedAddressData.TrySample(out var data))
        {
            CompleteRenderingRead((byte)data);
            CompletedVramReadCount++;
        }

        _renderReadPhase = 0;
        _renderReadPurpose = VramTransactionPurpose.None;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool AdvanceCpuVramTransaction()
    {
        var phase = ++_transactionPhase;
        if (phase == 1)
        {
            if (_compiledBusFabric is not null)
            {
                if (_transaction == VramTransaction.Read)
                    _compiledVramReadLatch = ReadCompiledMemory(_transactionAddress);
                else
                    WriteCompiledMemory(_transactionAddress, _transactionWriteData);
            }
            else PresentVramDataPhase();
            return false;
        }
        if (phase < 3) return false;

        if (_transaction == VramTransaction.Read)
        {
            if (_compiledBusFabric is not null)
            {
                _readBuffer = _compiledVramReadLatch;
                CompletedVramReadCount++;
            }
            else if (MultiplexedAddressData.TrySample(out var data))
            {
                _readBuffer = (byte)data;
                CompletedVramReadCount++;
            }
        }
        else
        {
            CompletedVramWriteCount++;
        }

        _transaction = VramTransaction.None;
        _transactionPhase = 0;
        return true;
    }

    private bool BackgroundRenderingEnabled => _backgroundRenderingEnabled;
    private bool RenderingEnabled => _renderingEnabled;
    private bool VramBusBusy => _transaction != VramTransaction.None || _renderReadPhase != 0;
    private bool RenderingBusActive => RenderingEnabled
        && IsRenderingScanline()
        && ((Dot >= 1 && Dot <= 256) || (Dot >= 321 && Dot <= 340));

    private bool IsRenderingScanline() => Scanline < 240 || Scanline == PreRenderScanline;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void BeginBackgroundRead(ushort address, VramTransactionPurpose purpose)
    {
        if (VramBusBusy) return;

        // Rendering fetches are a fixed two-dot physical circuit: this decoder
        // edge presents A/AD + ALE, the following PPU dot asserts /RD with AD
        // released, and the next decoder edge samples the returned byte. Keep
        // that hot path separate from the slower CPU $2007 read/write sequencer
        // so no transaction kind/completion-policy decoding occurs per fetch.
        _renderReadPhase = 1;
        _renderReadAddress = (ushort)(address & 0x3FFF);
        _renderReadPurpose = purpose;
        if (_compiledBusFabric is null) PresentRenderingVramAddressPhase();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort PatternAddress(bool highPlane)
    {
        var fineY = (_vramAddress >> 12) & 7;
        return (ushort)(_backgroundPatternTableBase | (_nextTileId << 4) | fineY | (highPlane ? 8 : 0));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetRenderingFetchKind(VramTransactionPurpose purpose, out Rp2C02RenderingFetchKind kind)
    {
        kind = purpose switch
        {
            VramTransactionPurpose.BackgroundNametable => Rp2C02RenderingFetchKind.BackgroundNametable,
            VramTransactionPurpose.BackgroundAttribute => Rp2C02RenderingFetchKind.BackgroundAttribute,
            VramTransactionPurpose.BackgroundPatternLow => Rp2C02RenderingFetchKind.BackgroundPatternLow,
            VramTransactionPurpose.BackgroundPatternHigh => Rp2C02RenderingFetchKind.BackgroundPatternHigh,
            VramTransactionPurpose.DummyNametable => Rp2C02RenderingFetchKind.DummyNametable,
            VramTransactionPurpose.SpritePatternLow => Rp2C02RenderingFetchKind.SpritePatternLow,
            VramTransactionPurpose.SpritePatternHigh => Rp2C02RenderingFetchKind.SpritePatternHigh,
            _ => default
        };
        return purpose != VramTransactionPurpose.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CompleteRenderingRead(byte data)
    {
        if (RenderingFetchTraceOutput.CaptureEnabled && TryGetRenderingFetchKind(_renderReadPurpose, out var kind))
        {
            RenderingFetchTraceOutput.Drive(new Rp2C02RenderingFetchTraceEvent(
                Frame,
                Scanline,
                Dot,
                kind,
                _renderReadAddress,
                data,
                _vramAddress,
                _temporaryAddress));
        }

        switch (_renderReadPurpose)
        {
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
            case VramTransactionPurpose.DummyNametable:
                // Dots 337 and 339 perform real nametable bus reads but do not
                // feed a visible tile latch. Keeping them as package-owned bus
                // activity matters to cartridge hardware that observes PPU A-lines.
                DummyNametableFetchCount++;
                break;
            case VramTransactionPurpose.SpritePatternLow:
                if (_spriteFetchSlot < _nextSpriteCount)
                {
                    var sprite = _nextSprites[_spriteFetchSlot];
                    var pattern = (sprite & SpriteHorizontalFlipMask) != 0
                        ? PpuDotDecoder.ReverseByte[data]
                        : data;
                    _nextSprites[_spriteFetchSlot] = (sprite & ~SpritePatternLowMask)
                        | ((ulong)pattern << 32);
                    SpritePatternFetchCount++;
                }
                break;
            case VramTransactionPurpose.SpritePatternHigh:
                if (_spriteFetchSlot < _nextSpriteCount)
                {
                    var sprite = _nextSprites[_spriteFetchSlot];
                    var pattern = (sprite & SpriteHorizontalFlipMask) != 0
                        ? PpuDotDecoder.ReverseByte[data]
                        : data;
                    _nextSprites[_spriteFetchSlot] = (sprite & ~SpritePatternHighMask)
                        | ((ulong)pattern << 40);
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
            // Once secondary OAM is full, the RP2C0x's broken evaluation logic
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
        _secondaryOam[_secondarySpriteCount++] =
            (ulong)_primaryOam[baseAddress + 1]
            | ((ulong)attributes << 8)
            | ((ulong)_primaryOam[baseAddress + 3] << 16)
            | ((ulong)(byte)row << 24)
            | ((ulong)(byte)spriteIndex << 32);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort SpritePatternAddress(ulong sprite, bool highPlane)
    {
        var tile = (byte)(sprite & SpriteTileMask);
        var row = (byte)((sprite & SpriteRowMask) >> 24);
        if (_spriteHeight == 8)
        {
            return (ushort)(_spritePatternTableBase | (tile << 4) | row | (highPlane ? 8 : 0));
        }

        var tableBase = (tile & 1) << 12;
        tile &= 0xFE;
        if (row >= 8)
        {
            tile++;
            row -= 8;
        }
        return (ushort)(tableBase | (tile << 4) | row | (highPlane ? 8 : 0));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateSpritePixelCompositionAndAdvance()
    {
        byte spritePattern = 0;
        byte spritePalette = 0;
        var spriteBehindBackground = false;
        var spriteZero = false;
        var spriteOutputEnabled = _spriteRenderingEnabled && (Dot > 8 || _showSpriteLeft);
        var haveOpaqueSprite = false;

        // The eight sprite output units are independent physical circuits. Their
        // retained state is packed one unit per ulong so each hot-loop iteration
        // is one load and one store rather than a multi-field struct copy.
        for (var index = 0; index < _activeSpriteCount; index++)
        {
            var sprite = _activeSprites[index];
            var xCounter = (byte)((sprite & SpriteXMask) >> 16);

            if (spriteOutputEnabled && !haveOpaqueSprite && xCounter == 0)
            {
                var patternValue = (byte)(((sprite >> 39) & 1) | ((sprite >> 46) & 2));
                if (patternValue != 0)
                {
                    spritePattern = patternValue;
                    spritePalette = (byte)((sprite >> 8) & 3);
                    spriteBehindBackground = (sprite & SpriteBehindBackgroundMask) != 0;
                    spriteZero = (sprite & SpriteZeroMask) != 0;
                    haveOpaqueSprite = true;
                }
            }

            if (xCounter != 0)
            {
                sprite -= 1UL << 16;
            }
            else
            {
                sprite = (sprite & ~SpritePatternMask)
                    | ((sprite << 1) & SpritePatternShiftLaneMask);
            }
            _activeSprites[index] = sprite;
        }

        var background = BackgroundPixelIndex;
        if (Dot <= 8 && !_showBackgroundLeft) background = 0;
        var backgroundOpaque = (background & 3) != 0;
        var spriteOpaque = spritePattern != 0;
        SpritePixelIndex = spriteOpaque ? (byte)(0x10 | (spritePalette << 2) | spritePattern) : (byte)0;

        if (spriteZero && spriteOpaque && backgroundOpaque && Dot < 256 && !_spriteZeroHit)
        {
            _spriteZeroHit = true;
            TraceSplit("sprite-zero hit", 0);
        }

        if (!spriteOpaque) PixelPaletteIndex = background;
        else if (!backgroundOpaque || !spriteBehindBackground) PixelPaletteIndex = SpritePixelIndex;
        else PixelPaletteIndex = background;
        UpdateOutputColor();
    }

    private void TraceSplit(string operation, byte value)
    {
        SplitTraceOutput.Drive(new Rp2C02SplitTraceEvent(
            Frame, Scanline, Dot, operation, value, _vramAddress, _temporaryAddress, _fineX, _writeToggle));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PresentRenderingVramAddressPhase()
    {
        PresentHighAddress((byte)(_renderReadAddress >> 8));
        PresentAd((byte)_renderReadAddress);
        PresentAle(DigitalLevel.High);
        PresentReadBar(DigitalLevel.High);
        PresentWriteBar(DigitalLevel.High);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PresentRenderingVramDataPhase()
    {
        PresentHighAddress((byte)(_renderReadAddress >> 8));
        PresentAle(DigitalLevel.Low);
        PresentAdReleased();
        PresentReadBar(DigitalLevel.Low);
        PresentWriteBar(DigitalLevel.High);
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
        if (_compiledBusFabric is not null) return;
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

        if (_compiledBusFabric is not null)
        {
            _compiledBusFabric.PresentOutputSignal(NmiBar, assert ? DigitalLevel.Low : DigitalLevel.HighImpedance);
            if (assert) NmiFallingEdgeCount++;
        }
        else if (assert)
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
        BackgroundNametable,
        BackgroundAttribute,
        BackgroundPatternLow,
        BackgroundPatternHigh,
        DummyNametable,
        SpritePatternLow,
        SpritePatternHigh
    }


    IEnumerable<CompiledBusMasterDescriptor> ICompiledBusMasterProvider.GetCompiledBusMasters()
    {
        var addressRoots = new DigitalPin[MultiplexedAddressData.Width + HighAddress.Width];
        for (var bit = 0; bit < MultiplexedAddressData.Width; bit++)
            addressRoots[bit] = MultiplexedAddressData.Pins[bit];
        for (var bit = 0; bit < HighAddress.Width; bit++)
            addressRoots[bit + MultiplexedAddressData.Width] = HighAddress.Pins[bit];

        yield return new CompiledBusMasterDescriptor(
            this,
            addressRoots,
            MultiplexedAddressData.Pins,
            EvaluateCompiledVramBusDriver,
            AttachCompiledBusFabric,
            DetachCompiledBusFabric);
    }

    private CompiledDriveState? EvaluateCompiledVramBusDriver(DigitalPin pin, uint address, bool readCycle)
    {
        for (var bit = 0; bit < MultiplexedAddressData.Width; bit++)
        {
            if (ReferenceEquals(pin, MultiplexedAddressData.Pins[bit]))
            {
                return new CompiledDriveState(
                    (address & (1u << bit)) != 0 ? DigitalLevel.High : DigitalLevel.Low);
            }
        }
        for (var bit = 0; bit < HighAddress.Width; bit++)
        {
            if (ReferenceEquals(pin, HighAddress.Pins[bit]))
            {
                return new CompiledDriveState(
                    (address & (1u << (bit + 8))) != 0 ? DigitalLevel.High : DigitalLevel.Low);
            }
        }

        if (ReferenceEquals(pin, AddressLatchEnable))
            return new CompiledDriveState(DigitalLevel.Low);
        if (ReferenceEquals(pin, VramReadBar))
            return new CompiledDriveState(readCycle ? DigitalLevel.Low : DigitalLevel.High);
        if (ReferenceEquals(pin, VramWriteBar))
            return new CompiledDriveState(readCycle ? DigitalLevel.High : DigitalLevel.Low);
        return null;
    }

    IEnumerable<CompiledBusTargetDescriptor> ICompiledBusTargetProvider.GetCompiledBusTargets()
    {
        yield return new CompiledBusTargetDescriptor(
            this,
            RegisterSelect.Pins,
            CpuData.Pins,
            new[]
            {
                new CompiledPinCondition(ChipSelectBar, DigitalLevel.Low),
                new CompiledPinCondition(CpuReadWrite, DigitalLevel.High)
            },
            new[]
            {
                new CompiledPinCondition(ChipSelectBar, DigitalLevel.Low),
                new CompiledPinCondition(CpuReadWrite, DigitalLevel.Low)
            },
            CompiledBusReadPhase.Begin,
            address => CompiledCpuReadRegister(address),
            (address, value) => CompiledCpuWriteRegister(address, value));
    }

    DigitalPin ICompiledClockedComponent.CompiledClockInput => Clock;
    DigitalPin? ICompiledClockedComponent.CompiledResetInput => ResetBar;
    DigitalLevel ICompiledClockedComponent.CompiledResetAssertedLevel => DigitalLevel.Low;
    void ICompiledClockedComponent.ExecuteCompiledClockActivation() => ExecuteCompiledPpuDot();
    void ICompiledClockedComponent.SetCompiledResetAsserted(bool asserted) => SetCompiledResetAsserted(asserted);



}
