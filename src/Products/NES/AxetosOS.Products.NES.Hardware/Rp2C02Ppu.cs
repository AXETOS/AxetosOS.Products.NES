using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

public sealed record SpriteZeroHitTraceEvent(
    ulong Frame,
    int Scanline,
    int Dot,
    int ScreenX,
    int ScreenY,
    ushort VramAddress,
    ushort ScanlineVramAddress,
    byte FineXScroll);

public sealed record SpriteZeroEvaluationTraceEvent(
    ulong Frame,
    int Scanline,
    byte OamY,
    byte TileIndex,
    byte Attributes,
    byte OamX,
    int SpriteHeight,
    int SourceRow,
    int PatternRow,
    ushort PatternAddress,
    byte LowPlane,
    byte HighPlane,
    byte SpriteOpaqueMask,
    byte BackgroundOpaqueMask,
    byte OverlapMask,
    bool SelectedForScanline,
    bool BackgroundEnabled,
    bool SpritesEnabled,
    bool BackgroundLeftEnabled,
    bool SpritesLeftEnabled,
    ushort ScanlineVramAddress,
    byte FineXScroll,
    string RejectionReason);

public sealed record OamWriteTraceEvent(
    ulong Frame,
    int Scanline,
    int Dot,
    string Source,
    byte Address,
    byte PreviousValue,
    byte Value,
    byte NextAddress);

public sealed record SpriteScanlineSelectionTraceEvent(
    ulong Frame,
    int Scanline,
    int SpriteHeight,
    int SpritesOnScanline,
    int EvaluatedSprites,
    bool SpriteZeroOnScanline,
    bool SpriteZeroSelected,
    int SpriteZeroSelectionSlot,
    byte OamY,
    byte TileIndex,
    byte Attributes,
    byte OamX);

public sealed record PpuStatusReadTraceEvent(
    ulong Frame,
    int Scanline,
    int Dot,
    byte Value);

public sealed record PpuDiagnosticsSnapshot(
    ulong Frame,
    int Scanline,
    int Dot,
    byte Control,
    byte Mask,
    byte Status,
    ushort VramAddress,
    ushort TemporaryVramAddress,
    ushort ActiveScanlineVramAddress,
    byte FineXScroll,
    bool WriteToggle,
    bool InVBlank,
    bool NmiOutput,
    bool BackgroundEnabled,
    bool SpritesEnabled,
    int BackgroundPatternTable,
    int SpritePatternTable,
    ulong VBlankStarts,
    ulong NmiEdges,
    ulong StatusReads,
    ulong SpriteZeroHits,
    ulong LastSpriteZeroHitFrame,
    int LastSpriteZeroHitX,
    int LastSpriteZeroHitY);


public sealed class Rp2C02Ppu : INesHardwareModule, IClockedHardwareModule, ICpuBusDevice
{
    public const int ScreenWidth = 256;
    public const int ScreenHeight = 240;

    private static readonly uint[] SystemPalette =
    [
        0xFF545454, 0xFF001E74, 0xFF081090, 0xFF300088, 0xFF440064, 0xFF5C0030, 0xFF540400, 0xFF3C1800,
        0xFF202A00, 0xFF083A00, 0xFF004000, 0xFF003C00, 0xFF00323C, 0xFF000000, 0xFF000000, 0xFF000000,
        0xFF989698, 0xFF084CC4, 0xFF3032EC, 0xFF5C1EE4, 0xFF8814B0, 0xFFA01464, 0xFF982220, 0xFF783C00,
        0xFF545A00, 0xFF287200, 0xFF087C00, 0xFF007628, 0xFF006678, 0xFF000000, 0xFF000000, 0xFF000000,
        0xFFECEEEC, 0xFF4C9AEC, 0xFF787CEC, 0xFFB062EC, 0xFFE454EC, 0xFFEC58B4, 0xFFEC6A64, 0xFFD48820,
        0xFFA0AA00, 0xFF74C400, 0xFF4CD020, 0xFF38CC6C, 0xFF38B4CC, 0xFF3C3C3C, 0xFF000000, 0xFF000000,
        0xFFECEEEC, 0xFFA8CCEC, 0xFFBCBCEC, 0xFFD4B2EC, 0xFFECAEEC, 0xFFECAED4, 0xFFECB4B0, 0xFFE4C490,
        0xFFCCD278, 0xFFB4DE78, 0xFFA8E290, 0xFF98E2B4, 0xFFA0D6E4, 0xFFA0A2A0, 0xFF000000, 0xFF000000
    ];

    private readonly PpuBus _bus;
    private readonly ISignalLine _nmi;
    private readonly byte[] _oam = new byte[256];
    private byte _control;
    private byte _mask;
    private byte _status;
    private byte _oamAddress;
    private byte _readBuffer;
    private byte _ioDataBus;
    private byte _fineX;
    private bool _writeToggle;
    private ushort _vramAddress;
    private ushort _temporaryAddress;
    private ushort _scanlineAddress;
    private ushort _backgroundPatternLowShift;
    private ushort _backgroundPatternHighShift;
    private ushort _backgroundAttributeLowShift;
    private ushort _backgroundAttributeHighShift;
    private byte _nextBackgroundTile;
    private byte _nextBackgroundAttribute;
    private byte _nextBackgroundLowPlane;
    private byte _nextBackgroundHighPlane;
    private readonly Rp2C02SpriteEvaluator _spriteEvaluator = new();
    private readonly SpriteFetchSlot[] _spriteFetchSlots = new SpriteFetchSlot[8];
    private readonly SpriteOutputUnit[] _spriteOutputUnits = new SpriteOutputUnit[8];
    private int _latchedSpriteCount;
    private bool _latchedSpriteZeroSelected;
    private int _latchedSpriteZeroSlot = -1;
    private int _spriteFetchLatchedScanline = int.MinValue;
    private SpriteZeroEvaluationTraceEvent? _pendingSpriteZeroEvaluation;
    private readonly int _preRenderScanline;
    private readonly bool _usesNtscOddFrameSkip;
    private readonly bool _hasOamRefreshBug;
    private bool _nmiOutput;
    private bool _suppressVblankStart;

    public Rp2C02Ppu(PpuBus bus, ISignalLine nmi, NesTimingProfile? timing = null)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _nmi = nmi ?? throw new ArgumentNullException(nameof(nmi));
        var timingProfile = timing ?? NesTimingProfile.For(AxetosOS.Products.NES.Cartridges.NesTimingMode.Ntsc);
        _preRenderScanline = timingProfile.PpuScanlines - 1;
        _usesNtscOddFrameSkip = timingProfile.Mode == AxetosOS.Products.NES.Cartridges.NesTimingMode.Ntsc;
        _hasOamRefreshBug = timingProfile.Mode != AxetosOS.Products.NES.Cartridges.NesTimingMode.Pal;
        Framebuffer = new uint[ScreenWidth * ScreenHeight];
    }

    public string ModuleId => "nes.chip.rp2c02";
    public int Scanline { get; private set; }
    public int Dot { get; private set; }
    public ulong Frame { get; private set; }
    public bool FrameCompleted { get; private set; }
    public bool InVBlank => (_status & 0x80) != 0;
    public byte Control => _control;
    public byte Mask => _mask;
    public byte Status => _status;
    public ushort VramAddress => _vramAddress;
    public ushort TemporaryVramAddress => _temporaryAddress;
    public ushort ActiveScanlineVramAddress => _scanlineAddress;
    public byte FineXScroll => _fineX;
    public bool WriteToggle => _writeToggle;
    public uint[] Framebuffer { get; }
    public Rp2C02SpriteEvaluator SpriteEvaluator => _spriteEvaluator;
    public byte OamAddress => _oamAddress;
    public byte IoDataBus => _ioDataBus;
    public ushort BackgroundPatternLowShift => _backgroundPatternLowShift;
    public ushort BackgroundPatternHighShift => _backgroundPatternHighShift;
    public ushort BackgroundAttributeLowShift => _backgroundAttributeLowShift;
    public ushort BackgroundAttributeHighShift => _backgroundAttributeHighShift;
    public byte NextBackgroundTile => _nextBackgroundTile;
    public byte NextBackgroundAttribute => _nextBackgroundAttribute;
    public byte NextBackgroundLowPlane => _nextBackgroundLowPlane;
    public byte NextBackgroundHighPlane => _nextBackgroundHighPlane;
    public int LatchedSpriteCount => _latchedSpriteCount;
    public bool LatchedSpriteZeroSelected => _latchedSpriteZeroSelected;
    public int LatchedSpriteZeroSlot => _latchedSpriteZeroSlot;
    public bool NmiOutputActive => _nmiOutput;
    public bool VblankStartSuppressed => _suppressVblankStart;
    public bool BackgroundRenderingEnabled => (_mask & 0x08) != 0;
    public bool SpriteRenderingEnabled => (_mask & 0x10) != 0;
    public ulong VBlankStarts { get; private set; }
    public ulong NmiEdges { get; private set; }
    public ulong StatusReads { get; private set; }
    public ulong SpriteZeroHits { get; private set; }
    public ulong LastSpriteZeroHitFrame { get; private set; }
    public int LastSpriteZeroHitX { get; private set; } = -1;
    public int LastSpriteZeroHitY { get; private set; } = -1;

    public event Action<SpriteZeroHitTraceEvent>? SpriteZeroHit;
    public event Action<SpriteZeroEvaluationTraceEvent>? SpriteZeroEvaluated;
    public event Action<PpuStatusReadTraceEvent>? StatusRead;
    public event Action<OamWriteTraceEvent>? OamWritten;
    public event Action<SpriteScanlineSelectionTraceEvent>? SpriteScanlineSelected;
    public bool DiagnosticsTraceEnabled { get; set; }

    public PpuDiagnosticsSnapshot GetDiagnostics() => new(
        Frame,
        Scanline,
        Dot,
        _control,
        _mask,
        _status,
        _vramAddress,
        _temporaryAddress,
        _scanlineAddress,
        _fineX,
        _writeToggle,
        InVBlank,
        _nmiOutput,
        (_mask & 0x08) != 0,
        (_mask & 0x10) != 0,
        (_control & 0x10) != 0 ? 0x1000 : 0x0000,
        (_control & 0x08) != 0 ? 0x1000 : 0x0000,
        VBlankStarts,
        NmiEdges,
        StatusReads,
        SpriteZeroHits,
        LastSpriteZeroHitFrame,
        LastSpriteZeroHitX,
        LastSpriteZeroHitY);

    public byte ReadOamByte(byte address) => _oam[address];

    public void WriteOamDmaByte(byte value)
    {
        WriteOamByte(value, "dma");
    }

    public void PowerOn()
    {
        _control = 0;
        _mask = 0;
        _status = 0;
        _oamAddress = 0;
        _readBuffer = 0;
        _ioDataBus = 0;
        _fineX = 0;
        _writeToggle = false;
        _vramAddress = 0;
        _temporaryAddress = 0;
        _scanlineAddress = 0;
        _backgroundPatternLowShift = 0;
        _backgroundPatternHighShift = 0;
        _backgroundAttributeLowShift = 0;
        _backgroundAttributeHighShift = 0;
        _nextBackgroundTile = 0;
        _nextBackgroundAttribute = 0;
        _nextBackgroundLowPlane = 0;
        _nextBackgroundHighPlane = 0;
        Scanline = 0;
        Dot = 0;
        Frame = 0;
        FrameCompleted = false;
        Array.Clear(_oam);
        Array.Clear(Framebuffer);
        Array.Clear(_spriteFetchSlots);
        Array.Clear(_spriteOutputUnits);
        _latchedSpriteCount = 0;
        _latchedSpriteZeroSelected = false;
        _latchedSpriteZeroSlot = -1;
        _spriteFetchLatchedScanline = int.MinValue;
        _pendingSpriteZeroEvaluation = null;
        _nmiOutput = false;
        _suppressVblankStart = false;
        VBlankStarts = 0;
        NmiEdges = 0;
        StatusReads = 0;
        SpriteZeroHits = 0;
        LastSpriteZeroHitFrame = 0;
        LastSpriteZeroHitX = -1;
        LastSpriteZeroHitY = -1;
        _spriteEvaluator.Reset();
        _nmi.Release();
    }

    public void Reset()
    {
        _control = 0;
        _mask = 0;
        _writeToggle = false;
        _backgroundPatternLowShift = 0;
        _backgroundPatternHighShift = 0;
        _backgroundAttributeLowShift = 0;
        _backgroundAttributeHighShift = 0;
        _spriteEvaluator.Reset();
        Array.Clear(_spriteFetchSlots);
        Array.Clear(_spriteOutputUnits);
        _latchedSpriteCount = 0;
        _latchedSpriteZeroSelected = false;
        _latchedSpriteZeroSlot = -1;
        _spriteFetchLatchedScanline = int.MinValue;
        _suppressVblankStart = false;
        SetNmiOutput(false);
    }

    public bool HandlesCpuAddress(ushort address) => address is >= 0x2000 and <= 0x3FFF;

    public byte CpuRead(ushort address)
    {
        var result = (address & 0x0007) switch
        {
            2 => ReadStatus(),
            4 => ReadOamData(),
            7 => ReadData(),
            _ => _ioDataBus
        };

        _ioDataBus = result;
        return result;
    }

    public void CpuWrite(ushort address, byte value)
    {
        // Every CPU write to the PPU register window drives the RP2C02 I/O
        // data bus, including writes to nominally read-only registers.
        _ioDataBus = value;

        switch (address & 0x0007)
        {
            case 0:
                WriteControl(value);
                break;
            case 1:
                _mask = value;
                break;
            case 3:
                WriteOamAddress(value);
                break;
            case 4:
                WriteOamByte(value, "cpu");
                break;
            case 5:
                WriteScroll(value);
                break;
            case 6:
                WriteAddress(value);
                break;
            case 7:
                _bus.Write(_vramAddress, value);
                IncrementVramAddress();
                break;
        }
    }

    private void WriteOamAddress(byte value)
    {
        if (DiagnosticsTraceEnabled)
        {
            OamWritten?.Invoke(new OamWriteTraceEvent(
                Frame, Scanline, Dot, "address", _oamAddress, _oamAddress, value, value));
        }

        _oamAddress = value;
    }

    private byte ReadOamData()
    {
        if (!IsRenderingPeriod)
        {
            return _oam[_oamAddress];
        }

        // $2004 is connected to the PPU's internal OAM data bus while
        // rendering. Secondary OAM clear drives $FF, evaluation exposes the
        // current primary-OAM read latch, and sprite fetch exposes the byte
        // currently being consumed from secondary OAM.
        if (Dot is >= 1 and <= 64)
        {
            return 0xFF;
        }

        if (Dot is >= 65 and <= 256)
        {
            return _spriteEvaluator.OamBusValue;
        }

        if (Dot is >= 257 and <= 320)
        {
            var slot = (Dot - 257) >> 3;
            var phase = (Dot - 257) & 0x07;
            var byteIndex = phase >> 1;
            return _spriteEvaluator.SecondaryOam[(slot * 4) + byteIndex];
        }

        return _spriteEvaluator.OamBusValue;
    }

    private void WriteOamByte(byte value, string source)
    {
        var address = _oamAddress;
        var previous = _oam[address];

        if (IsRenderingPeriod)
        {
            // During active rendering the OAM write drivers are disconnected.
            // The address logic still glitches forward by one sprite entry,
            // preserving the low two byte-select bits.
            _oamAddress = (byte)((_oamAddress + 4) & 0xFF);
            if (DiagnosticsTraceEnabled)
            {
                OamWritten?.Invoke(new OamWriteTraceEvent(
                    Frame, Scanline, Dot, source + "-blocked", address, previous, value, _oamAddress));
            }
            return;
        }

        _oam[address] = value;
        _oamAddress++;
        if (DiagnosticsTraceEnabled)
        {
            OamWritten?.Invoke(new OamWriteTraceEvent(
                Frame, Scanline, Dot, source, address, previous, value, _oamAddress));
        }
    }

    private bool IsRenderingPeriod => RenderingEnabled
        && (Scanline is >= 0 and < ScreenHeight || Scanline == _preRenderScanline);

    public void Clock()
    {
        FrameCompleted = false;

        var renderingScanline = Scanline is >= 0 and < ScreenHeight || Scanline == _preRenderScanline;
        var visiblePixel = Scanline is >= 0 and < ScreenHeight && Dot is >= 1 and <= ScreenWidth;
        var spriteHeight = (_control & 0x20) != 0 ? 16 : 8;

        // Sprite evaluation is performed only on visible scanlines and prepares
        // sprites for the following scanline. The pre-render line performs no
        // evaluation, which is why hardware cannot display sprites on scanline 0.
        if (Scanline is >= 0 and < ScreenHeight && Dot == 1)
        {
            _spriteEvaluator.BeginScanline(Scanline + 1, spriteHeight, _oamAddress);
        }

        if (Scanline is >= 0 and < ScreenHeight && RenderingEnabled && Dot is >= 1 and <= 256)
        {
            if (Dot == 65)
            {
                ApplyOamRefreshBugAtEvaluationStart();
            }
            _spriteEvaluator.Clock(Dot, _oam);
        }

        if (Scanline is >= 0 and < ScreenHeight && Dot == 1)
        {
            _scanlineAddress = _vramAddress;
            PrepareSpriteDiagnostics(Scanline);
        }

        if (Scanline is >= 0 and < ScreenHeight && RenderingEnabled && Dot == 256)
        {
            if (_spriteEvaluator.OverflowDetected)
            {
                _status |= 0x20;
            }
        }

        if (renderingScanline && RenderingEnabled && Dot is >= 257 and <= 320)
        {
            // Enabling rendering after dot 257 joins the sprite-fetch sequencer
            // at its current hardware phase. Latch the evaluator once on the
            // first active fetch dot rather than requiring rendering to have
            // been enabled at exactly dot 257.
            if (_spriteFetchLatchedScanline != Scanline)
            {
                if (Scanline == _preRenderScanline)
                {
                    ClearSpritePipelineForScanlineZero();
                }
                else
                {
                    LatchEvaluatedSprites();
                }
                _spriteFetchLatchedScanline = Scanline;
            }

            // Sprite fetch forces OAMADDR to zero throughout this interval.
            _oamAddress = 0;
            ClockSpriteFetch();
        }

        // Pixel output uses the background shift registers filled by the real PPU
        // fetch cadence. This preserves prefetched/stale tile data across scroll and
        // rendering transitions instead of reconstructing a pixel directly from a
        // frozen scanline address.
        if (visiblePixel)
        {
            RenderVisiblePixel(Dot - 1, Scanline);
        }

        if (Scanline is >= 0 and < ScreenHeight && Dot == 257)
        {
            CompleteSpriteZeroEvaluation();
        }

        if (RenderingEnabled && renderingScanline)
        {
            if (Dot is >= 1 and <= 256 || Dot is >= 321 and <= 337)
            {
                ShiftBackgroundRegisters();
                ClockBackgroundFetch();
            }

            if (Dot == 256)
            {
                IncrementVerticalAddress();
            }

            if (Dot == 257)
            {
                LoadBackgroundRegisters();
                CopyHorizontalAddress();
            }

            if (Scanline == _preRenderScanline && Dot is >= 280 and <= 304)
            {
                CopyVerticalAddress();
            }

            if (Dot is 338 or 340)
            {
                _nextBackgroundTile = _bus.Read((ushort)(0x2000 | (_vramAddress & 0x0FFF)));
            }
        }

        // MMC3-family cartridges receive one qualified scanline clock. The current
        // renderer still exposes one filtered A12 edge per rendered scanline.
        if (RenderingEnabled && Scanline is >= 0 and < ScreenHeight && Dot == 260)
        {
            _bus.ClockScanline();
        }

        if (Scanline == 241 && Dot == 1)
        {
            VBlankStarts++;

            // A PPUSTATUS read on dot 0 or at the dot-1 boundary suppresses
            // the vblank flag and its NMI pulse for this frame. This models
            // the RP2C02 race where the status read clears the internal
            // vblank latch while it is being set.
            if (_suppressVblankStart)
            {
                _suppressVblankStart = false;
                _status &= 0x7F;
                SetNmiOutput(false);
            }
            else
            {
                _status |= 0x80;
                UpdateNmiLine();
            }
        }
        else if (Scanline == _preRenderScanline && Dot == 1)
        {
            _suppressVblankStart = false;
            _status &= 0x1F;
            UpdateNmiLine();
        }

        // The NTSC 2C02 shortens every odd rendered frame by one PPU clock.
        // With rendering enabled, the pre-render scanline jumps directly from
        // dot 339 to scanline 0, dot 0, omitting dot 340. The parity flag still
        // advances every frame even when rendering is disabled.
        if (_usesNtscOddFrameSkip
            && (Frame & 1UL) != 0
            && RenderingEnabled
            && Scanline == _preRenderScanline
            && Dot == 339)
        {
            Dot = 0;
            Scanline = 0;
            Frame++;
            FrameCompleted = true;
            return;
        }

        Dot++;
        if (Dot <= 340)
        {
            return;
        }

        Dot = 0;
        Scanline++;
        if (Scanline <= _preRenderScanline)
        {
            return;
        }

        Scanline = 0;
        Frame++;
        FrameCompleted = true;
    }

    private void ClockBackgroundFetch()
    {
        switch ((Dot - 1) & 0x07)
        {
            case 0:
                LoadBackgroundRegisters();
                _nextBackgroundTile = _bus.Read((ushort)(0x2000 | (_vramAddress & 0x0FFF)));
                break;
            case 2:
            {
                var attributeAddress = (ushort)(0x23C0
                    | (_vramAddress & 0x0C00)
                    | ((_vramAddress >> 4) & 0x38)
                    | ((_vramAddress >> 2) & 0x07));
                var attribute = _bus.Read(attributeAddress);
                var shift = (int)(((_vramAddress >> 4) & 0x04) | (_vramAddress & 0x02));
                _nextBackgroundAttribute = (byte)((attribute >> shift) & 0x03);
                break;
            }
            case 4:
            {
                var patternAddress = ((_control & 0x10) != 0 ? 0x1000 : 0x0000)
                    + (_nextBackgroundTile * 16)
                    + ((_vramAddress >> 12) & 0x07);
                _nextBackgroundLowPlane = _bus.Read((ushort)patternAddress);
                break;
            }
            case 6:
            {
                var patternAddress = ((_control & 0x10) != 0 ? 0x1000 : 0x0000)
                    + (_nextBackgroundTile * 16)
                    + ((_vramAddress >> 12) & 0x07);
                _nextBackgroundHighPlane = _bus.Read((ushort)(patternAddress + 8));
                break;
            }
            case 7:
                IncrementCoarseX();
                break;
        }
    }

    private void LoadBackgroundRegisters()
    {
        _backgroundPatternLowShift = (ushort)((_backgroundPatternLowShift & 0xFF00) | _nextBackgroundLowPlane);
        _backgroundPatternHighShift = (ushort)((_backgroundPatternHighShift & 0xFF00) | _nextBackgroundHighPlane);
        _backgroundAttributeLowShift = (ushort)((_backgroundAttributeLowShift & 0xFF00)
            | ((_nextBackgroundAttribute & 0x01) != 0 ? 0x00FF : 0x0000));
        _backgroundAttributeHighShift = (ushort)((_backgroundAttributeHighShift & 0xFF00)
            | ((_nextBackgroundAttribute & 0x02) != 0 ? 0x00FF : 0x0000));
    }

    private void ShiftBackgroundRegisters()
    {
        _backgroundPatternLowShift <<= 1;
        _backgroundPatternHighShift <<= 1;
        _backgroundAttributeLowShift <<= 1;
        _backgroundAttributeHighShift <<= 1;
    }

    private bool RenderingEnabled => (_mask & 0x18) != 0;

    private void RenderVisiblePixel(int screenX, int screenY)
    {
        var background = ReadBackgroundPixel(screenX, screenY);
        RecordSpriteZeroBackgroundPixel(screenX, background.Opaque);
        var sprite = ReadSpritePixel(screenX);

        if (sprite.Opaque && background.Opaque && sprite.IsSpriteZero && screenX < 255)
        {
            var wasClear = (_status & 0x40) == 0;
            _status |= 0x40;
            if (wasClear)
            {
                SpriteZeroHits++;
                LastSpriteZeroHitFrame = Frame;
                LastSpriteZeroHitX = screenX;
                LastSpriteZeroHitY = screenY;
                if (DiagnosticsTraceEnabled)
                    SpriteZeroHit?.Invoke(new SpriteZeroHitTraceEvent(
                    Frame,
                    Scanline,
                    Dot,
                    screenX,
                    screenY,
                    _vramAddress,
                    _scanlineAddress,
                    _fineX));
            }
        }

        var paletteIndex = sprite.Opaque && (!background.Opaque || !sprite.BehindBackground)
            ? sprite.PaletteIndex
            : background.PaletteIndex;

        WriteFramebufferPixel(screenX, screenY, paletteIndex);
    }

    private PixelSample ReadBackgroundPixel(int screenX, int screenY)
    {
        if ((_mask & 0x08) == 0 || (screenX < 8 && (_mask & 0x02) == 0))
        {
            return new PixelSample(_bus.Read(0x3F00), false);
        }

        var selector = (ushort)(0x8000 >> _fineX);
        var low = (_backgroundPatternLowShift & selector) != 0 ? 1 : 0;
        var high = (_backgroundPatternHighShift & selector) != 0 ? 2 : 0;
        var pixel = low | high;
        if (pixel == 0)
        {
            return new PixelSample(_bus.Read(0x3F00), false);
        }

        var paletteLow = (_backgroundAttributeLowShift & selector) != 0 ? 1 : 0;
        var paletteHigh = (_backgroundAttributeHighShift & selector) != 0 ? 2 : 0;
        var palette = paletteLow | paletteHigh;
        return new PixelSample(_bus.Read((ushort)(0x3F00 + (palette * 4) + pixel)), true);
    }

    private SpriteSample ReadSpritePixel(int screenX)
    {
        var spritesVisible = (_mask & 0x10) != 0 && (screenX >= 8 || (_mask & 0x04) != 0);
        var selected = SpriteSample.Transparent;

        for (var slot = 0; slot < _spriteOutputUnits.Length; slot++)
        {
            ref var unit = ref _spriteOutputUnits[slot];
            if (!unit.Active)
            {
                continue;
            }

            if (unit.XCounter != 0)
            {
                unit.XCounter--;
                continue;
            }

            var pixel = ((unit.PatternLow & 0x80) != 0 ? 1 : 0)
                | ((unit.PatternHigh & 0x80) != 0 ? 2 : 0);

            if (spritesVisible && pixel != 0 && !selected.Opaque)
            {
                var palette = unit.Attributes & 0x03;
                var paletteIndex = _bus.Read((ushort)(0x3F10 + (palette * 4) + pixel));
                selected = new SpriteSample(
                    paletteIndex,
                    true,
                    (unit.Attributes & 0x20) != 0,
                    unit.PrimaryOamIndex,
                    unit.IsSpriteZero);
            }

            unit.PatternLow <<= 1;
            unit.PatternHigh <<= 1;
        }

        return selected;
    }

    private void ApplyOamRefreshBugAtEvaluationStart()
    {
        if (!_hasOamRefreshBug || _oamAddress < 8)
        {
            return;
        }

        var source = _oamAddress & 0xF8;
        for (var i = 0; i < 8; i++)
        {
            _oam[i] = _oam[(source + i) & 0xFF];
        }
    }

    private void ClearSpritePipelineForScanlineZero()
    {
        _latchedSpriteCount = 0;
        _latchedSpriteZeroSelected = false;
        _latchedSpriteZeroSlot = -1;
        Array.Clear(_spriteFetchSlots);
        Array.Clear(_spriteOutputUnits);
    }

    private void LatchEvaluatedSprites()
    {
        _latchedSpriteCount = _spriteEvaluator.SelectedSpriteCount;
        _latchedSpriteZeroSelected = _spriteEvaluator.SpriteZeroSelected;
        _latchedSpriteZeroSlot = -1;
        Array.Clear(_spriteFetchSlots);
        Array.Clear(_spriteOutputUnits);

        for (var slot = 0; slot < 8; slot++)
        {
            var evaluated = slot < _latchedSpriteCount
                ? _spriteEvaluator.GetSelectedSprite(slot)
                : new EvaluatedSprite(0xFF, false, 0xFF, 0xFF, 0xFF, 0xFF);
            _spriteFetchSlots[slot] = new SpriteFetchSlot(evaluated);
            if (evaluated.IsSpriteZero)
            {
                _latchedSpriteZeroSlot = slot;
            }
        }
    }

    private void ClockSpriteFetch()
    {
        var relativeDot = Dot - 257;
        var slot = relativeDot >> 3;
        var phase = relativeDot & 0x07;
        ref var fetch = ref _spriteFetchSlots[slot];

        // The RP2C02 occupies all eight dots for each sprite. The first four
        // cycles perform garbage nametable reads; the final four fetch the two
        // pattern planes and transfer them into the sprite output unit.
        if (phase is 0 or 2)
        {
            _ = _bus.Read((ushort)(0x2000 | (_vramAddress & 0x0FFF)));
            return;
        }

        if (phase == 4)
        {
            fetch.PatternAddress = GetSpriteFetchPatternAddress(fetch.Sprite, _spriteEvaluator.TargetScanline);
            fetch.PatternLow = _bus.Read(fetch.PatternAddress);
            return;
        }

        if (phase == 6)
        {
            fetch.PatternHigh = _bus.Read((ushort)(fetch.PatternAddress + 8));
            return;
        }

        if (phase != 7)
        {
            return;
        }

        var low = fetch.PatternLow;
        var high = fetch.PatternHigh;
        if ((fetch.Sprite.Attributes & 0x40) != 0)
        {
            low = ReverseBits(low);
            high = ReverseBits(high);
        }

        _spriteOutputUnits[slot] = new SpriteOutputUnit(
            slot < _latchedSpriteCount,
            fetch.Sprite.PrimaryOamIndex,
            fetch.Sprite.IsSpriteZero,
            fetch.Sprite.Attributes,
            fetch.Sprite.X,
            low,
            high);
    }

    private ushort GetSpriteFetchPatternAddress(EvaluatedSprite sprite, int targetScanline)
    {
        var spriteHeight = (_control & 0x20) != 0 ? 16 : 8;
        var sourceRow = targetScanline - (sprite.Y + 1);
        if (sourceRow < 0 || sourceRow >= spriteHeight)
        {
            sourceRow = 0;
        }

        var patternRow = (sprite.Attributes & 0x80) != 0
            ? spriteHeight - 1 - sourceRow
            : sourceRow;
        return GetSpritePatternAddress(sprite.Tile, patternRow, spriteHeight);
    }

    private static byte ReverseBits(byte value)
    {
        value = (byte)(((value & 0xF0) >> 4) | ((value & 0x0F) << 4));
        value = (byte)(((value & 0xCC) >> 2) | ((value & 0x33) << 2));
        value = (byte)(((value & 0xAA) >> 1) | ((value & 0x55) << 1));
        return value;
    }

    private void PrepareSpriteDiagnostics(int screenY)
    {
        if (!DiagnosticsTraceEnabled)
        {
            return;
        }

        var spriteHeight = (_control & 0x20) != 0 ? 16 : 8;
        var spriteZeroOnScanline = _latchedSpriteZeroSelected;
        if (spriteZeroOnScanline)
        {
            SpriteScanlineSelected?.Invoke(new SpriteScanlineSelectionTraceEvent(
                Frame, screenY, spriteHeight, _latchedSpriteCount, _latchedSpriteCount,
                true, true, _latchedSpriteZeroSlot,
                _oam[0], _oam[1], _oam[2], _oam[3]));
        }

        TraceSpriteZeroEvaluation(screenY, spriteHeight);
    }

    private void TraceSpriteZeroEvaluation(int screenY, int spriteHeight)
    {
        var oamY = _oam[0];
        var tileIndex = _oam[1];
        var attributes = _oam[2];
        var oamX = _oam[3];
        var spriteTop = oamY + 1;
        var sourceRow = screenY - spriteTop;
        var selectedForScanline = sourceRow >= 0 && sourceRow < spriteHeight;
        var patternRow = selectedForScanline
            ? ((attributes & 0x80) != 0 ? spriteHeight - 1 - sourceRow : sourceRow)
            : -1;
        ushort patternAddress = 0;
        byte lowPlane = 0;
        byte highPlane = 0;
        byte spriteMask = 0;
        byte backgroundMask = 0;
        byte overlapMask = 0;

        if (selectedForScanline)
        {
            patternAddress = GetSpritePatternAddress(tileIndex, patternRow, spriteHeight);
            lowPlane = _bus.Read(patternAddress);
            highPlane = _bus.Read((ushort)(patternAddress + 8));

            for (var outputColumn = 0; outputColumn < 8; outputColumn++)
            {
                var sourceColumn = (attributes & 0x40) != 0 ? 7 - outputColumn : outputColumn;
                var bit = 7 - sourceColumn;
                var pixel = ((lowPlane >> bit) & 0x01) | (((highPlane >> bit) & 0x01) << 1);
                if (pixel != 0)
                {
                    spriteMask |= (byte)(1 << (7 - outputColumn));
                }

                // Background opacity is captured from the pixels actually emitted by
                // the live fetch/shift pipeline in RecordSpriteZeroBackgroundPixel.
                // Do not predict future columns by repeatedly sampling the current
                // shifter position here; that produced false diagnostic overlap masks.
            }

            overlapMask = (byte)(spriteMask & backgroundMask);
        }

        var backgroundEnabled = (_mask & 0x08) != 0;
        var spritesEnabled = (_mask & 0x10) != 0;
        var backgroundLeftEnabled = (_mask & 0x02) != 0;
        var spritesLeftEnabled = (_mask & 0x04) != 0;
        var rejectionReason = !selectedForScanline ? "not-on-scanline"
            : !backgroundEnabled ? "background-disabled"
            : !spritesEnabled ? "sprites-disabled"
            : spriteMask == 0 ? "sprite-transparent"
            : backgroundMask == 0 ? "background-transparent"
            : overlapMask == 0 ? "no-overlap"
            : oamX == 255 ? "x-255-suppressed"
            : oamX < 8 && (!backgroundLeftEnabled || !spritesLeftEnabled) ? "left-edge-clipped"
            : "hit-possible";

        _pendingSpriteZeroEvaluation = new SpriteZeroEvaluationTraceEvent(
            Frame,
            screenY,
            oamY,
            tileIndex,
            attributes,
            oamX,
            spriteHeight,
            sourceRow,
            patternRow,
            patternAddress,
            lowPlane,
            highPlane,
            spriteMask,
            backgroundMask,
            overlapMask,
            selectedForScanline,
            backgroundEnabled,
            spritesEnabled,
            backgroundLeftEnabled,
            spritesLeftEnabled,
            _scanlineAddress,
            _fineX,
            rejectionReason);
    }

    private void RecordSpriteZeroBackgroundPixel(int screenX, bool opaque)
    {
        if (!opaque || _pendingSpriteZeroEvaluation is not { SelectedForScanline: true } pending)
        {
            return;
        }

        var column = screenX - pending.OamX;
        if (column is < 0 or >= 8)
        {
            return;
        }

        var bit = (byte)(1 << (7 - column));
        _pendingSpriteZeroEvaluation = pending with
        {
            BackgroundOpaqueMask = (byte)(pending.BackgroundOpaqueMask | bit)
        };
    }

    private void CompleteSpriteZeroEvaluation()
    {
        if (!DiagnosticsTraceEnabled || _pendingSpriteZeroEvaluation is not { } pending)
        {
            _pendingSpriteZeroEvaluation = null;
            return;
        }

        var overlap = (byte)(pending.SpriteOpaqueMask & pending.BackgroundOpaqueMask);
        var reason = !pending.SelectedForScanline ? "not-on-scanline"
            : !pending.BackgroundEnabled ? "background-disabled"
            : !pending.SpritesEnabled ? "sprites-disabled"
            : pending.SpriteOpaqueMask == 0 ? "sprite-transparent"
            : pending.BackgroundOpaqueMask == 0 ? "background-transparent"
            : overlap == 0 ? "no-overlap"
            : pending.OamX == 255 ? "x-255-suppressed"
            : pending.OamX < 8 && (!pending.BackgroundLeftEnabled || !pending.SpritesLeftEnabled) ? "left-edge-clipped"
            : "hit-possible";

        SpriteZeroEvaluated?.Invoke(pending with
        {
            OverlapMask = overlap,
            RejectionReason = reason
        });
        _pendingSpriteZeroEvaluation = null;
    }

    private ushort GetSpritePatternAddress(byte tileIndex, int row, int spriteHeight)
    {
        if (spriteHeight == 8)
        {
            var patternBase = (_control & 0x08) != 0 ? 0x1000 : 0x0000;
            return (ushort)(patternBase + (tileIndex * 16) + row);
        }

        var patternTable = (tileIndex & 0x01) * 0x1000;
        var tile = tileIndex & 0xFE;
        if (row >= 8)
        {
            tile++;
            row -= 8;
        }

        return (ushort)(patternTable + (tile * 16) + row);
    }

    private struct SpriteFetchSlot
    {
        public SpriteFetchSlot(EvaluatedSprite sprite)
        {
            Sprite = sprite;
            PatternAddress = 0;
            PatternLow = 0;
            PatternHigh = 0;
        }

        public EvaluatedSprite Sprite;
        public ushort PatternAddress;
        public byte PatternLow;
        public byte PatternHigh;
    }

    private struct SpriteOutputUnit
    {
        public SpriteOutputUnit(bool active, byte primaryOamIndex, bool isSpriteZero, byte attributes, byte xCounter, byte patternLow, byte patternHigh)
        {
            Active = active;
            PrimaryOamIndex = primaryOamIndex;
            IsSpriteZero = isSpriteZero;
            Attributes = attributes;
            XCounter = xCounter;
            PatternLow = patternLow;
            PatternHigh = patternHigh;
        }

        public bool Active;
        public byte PrimaryOamIndex;
        public bool IsSpriteZero;
        public byte Attributes;
        public byte XCounter;
        public byte PatternLow;
        public byte PatternHigh;
    }

    private readonly record struct PixelSample(byte PaletteIndex, bool Opaque);

    private readonly record struct SpriteSample(byte PaletteIndex, bool Opaque, bool BehindBackground, int SpriteIndex, bool IsSpriteZero)
    {
        public static SpriteSample Transparent => new(0, false, false, -1, false);
    }

    private void WriteFramebufferPixel(int x, int y, byte paletteIndex)
    {
        Framebuffer[(y * ScreenWidth) + x] = SystemPalette[paletteIndex & 0x3F];
    }

    private void WriteControl(byte value)
    {
        var wasNmiEnabled = (_control & 0x80) != 0;
        _control = value;
        _temporaryAddress = (ushort)((_temporaryAddress & 0xF3FF) | ((value & 0x03) << 10));

        // NMI is edge-triggered. Enabling it during VBlank must create a rising edge,
        // while repeated writes with bit 7 already set must not queue duplicate NMIs.
        _ = wasNmiEnabled;
        UpdateNmiLine();
    }

    private byte ReadStatus()
    {
        StatusReads++;

        // Reading on the clock before vblank starts, or at the dot-1
        // boundary before the set operation has executed, suppresses the
        // vblank flag and NMI for the entire frame.
        if (Scanline == 241 && Dot is 0 or 1)
        {
            _suppressVblankStart = true;
        }

        // PPUSTATUS drives only bits 7-5. Bits 4-0 retain the previous value
        // on the RP2C02 I/O data bus.
        var value = (byte)((_status & 0xE0) | (_ioDataBus & 0x1F));
        if (DiagnosticsTraceEnabled)
            StatusRead?.Invoke(new PpuStatusReadTraceEvent(Frame, Scanline, Dot, value));
        _status &= 0x7F;
        _writeToggle = false;

        // Clearing the vblank latch immediately releases /NMI. If this read
        // occurs just after dot 1, the pulse can be shorter than one CPU
        // clock and therefore remain unseen by the edge-sampling RP2A03.
        SetNmiOutput(false);
        return value;
    }

    private byte ReadData()
    {
        var address = (ushort)(_vramAddress & 0x3FFF);
        var value = _bus.Read(address);
        byte result;

        if (address >= 0x3F00)
        {
            // Palette RAM drives only the lower six I/O-bus bits. The upper
            // two retain their previous latch state. Palette reads are not
            // delayed, but they still refill the normal PPUDATA buffer from
            // the mirrored nametable address underneath the palette range.
            var paletteValue = (byte)(value & ((_mask & 0x01) != 0 ? 0x30 : 0x3F));
            result = (byte)((_ioDataBus & 0xC0) | paletteValue);
            _readBuffer = _bus.Read((ushort)(address & 0x2FFF));
        }
        else
        {
            result = _readBuffer;
            _readBuffer = value;
        }

        IncrementVramAddress();
        return result;
    }

    private void WriteScroll(byte value)
    {
        if (!_writeToggle)
        {
            _fineX = (byte)(value & 0x07);
            _temporaryAddress = (ushort)((_temporaryAddress & 0xFFE0) | (value >> 3));
        }
        else
        {
            _temporaryAddress = (ushort)((_temporaryAddress & 0x8FFF) | ((value & 0x07) << 12));
            _temporaryAddress = (ushort)((_temporaryAddress & 0xFC1F) | ((value & 0xF8) << 2));
        }

        _writeToggle = !_writeToggle;
    }

    private void WriteAddress(byte value)
    {
        if (!_writeToggle)
        {
            _temporaryAddress = (ushort)((_temporaryAddress & 0x00FF) | ((value & 0x3F) << 8));
        }
        else
        {
            _temporaryAddress = (ushort)((_temporaryAddress & 0xFF00) | value);
            _vramAddress = _temporaryAddress;
        }

        _writeToggle = !_writeToggle;
    }

    private void IncrementVramAddress()
    {
        _vramAddress = (ushort)((_vramAddress + ((_control & 0x04) != 0 ? 32 : 1)) & 0x7FFF);
    }

    private void IncrementCoarseX()
    {
        if ((_vramAddress & 0x001F) == 31)
        {
            _vramAddress &= 0x7FE0;
            _vramAddress ^= 0x0400;
            return;
        }

        _vramAddress++;
    }

    private void IncrementVerticalAddress()
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
        else if (coarseY == 31)
        {
            coarseY = 0;
        }
        else
        {
            coarseY++;
        }

        _vramAddress = (ushort)((_vramAddress & 0x7C1F) | (coarseY << 5));
    }

    private void CopyHorizontalAddress()
    {
        _vramAddress = (ushort)((_vramAddress & 0x7BE0) | (_temporaryAddress & 0x041F));
    }

    private void CopyVerticalAddress()
    {
        _vramAddress = (ushort)((_vramAddress & 0x041F) | (_temporaryAddress & 0x7BE0));
    }

    private void UpdateNmiLine()
    {
        SetNmiOutput(InVBlank && (_control & 0x80) != 0);
    }

    private void SetNmiOutput(bool asserted)
    {
        if (_nmiOutput == asserted)
        {
            return;
        }

        _nmiOutput = asserted;
        if (asserted)
        {
            NmiEdges++;
            _nmi.Assert();
        }
        else
        {
            _nmi.Release();
        }
    }
}
