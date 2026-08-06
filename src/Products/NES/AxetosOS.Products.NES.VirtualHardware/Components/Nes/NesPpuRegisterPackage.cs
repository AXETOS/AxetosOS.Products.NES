using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Nes;

/// <summary>
/// CPU-facing RP2C02 register package. It reacts only to the CPU address/data
/// bus, R/W and the external vblank signal. Register mirrors, write latch,
/// buffered PPUDATA reads, OAM access, VRAM incrementing, and the first
/// background/sprite pixel pipelines are modelled here without using the
/// playable emulator PPU.
/// </summary>
public sealed class NesPpuRegisterPackage : VirtualHardwareComponent, IEventDrivenVirtualHardwareComponent, ISelectiveInputDrivenVirtualHardwareComponent
{
    private readonly byte[] _vram = new byte[0x4000];
    private readonly byte[] _oam = new byte[256];
    private readonly SpriteEntry[] _secondaryOam = new SpriteEntry[8];
    private int _secondarySpriteCount;
    private bool _spriteZeroHit;
    private bool _spriteOverflow;
    private bool _transactionActive;
    private bool _transactionRead;
    private ushort _transactionAddress;
    private byte _readValue;
    private bool _writeToggle;
    private byte _readBuffer;
    private bool _vblank;
    private bool _vblankPinLast;
    private bool _dotTickWasHigh;
    private bool _dmaWriteWasHigh;
    private readonly byte[] _frameBuffer = new byte[256 * 240];
    private RenderFetchState _renderFetchState;
    private int _renderX;
    private int _renderY;
    private int _renderFineX;
    private int _renderFineY;
    private int _renderPaletteSelect;
    private byte _renderTileIndex;
    private byte _renderAttribute;
    private byte _renderLowPlane;
    private byte _renderHighPlane;
    private PixelSample _renderBackground;
    private SpriteSample _renderSprite;
    private int _renderSpriteIndex;
    private SpriteEntry _renderSpriteEntry;
    private int _renderSpriteColumn;
    private int _renderSpriteRow;
    private byte _renderSpriteLow;
    private byte _renderSpriteHigh;
    private bool _renderReadIssued;
    private ushort _renderReadAddress;

    public NesPpuRegisterPackage(string componentId) : base(componentId)
    {
        var addressPins = new DigitalPin[16];
        for (var bit = 0; bit < 16; bit++) addressPins[bit] = AddPin($"A{bit}", PinDirection.Input);
        var dataPins = new DigitalPin[8];
        for (var bit = 0; bit < 8; bit++) dataPins[bit] = AddPin($"D{bit}", PinDirection.Bidirectional);

        Address = new DigitalBus($"{componentId}.A", addressPins);
        Data = new DigitalBus($"{componentId}.D", dataPins);
        ReadWrite = AddPin("R/W", PinDirection.Input);
        Vblank = AddPin("VBLANK", PinDirection.Input);
        NmiEnable = AddPin("NMI_ENABLE", PinDirection.Output);

        var scanlinePins = new DigitalPin[9];
        var dotPins = new DigitalPin[9];
        for (var bit = 0; bit < 9; bit++)
        {
            scanlinePins[bit] = AddPin($"SCANLINE{bit}", PinDirection.Input);
            dotPins[bit] = AddPin($"DOT{bit}", PinDirection.Input);
        }

        Scanline = new DigitalBus($"{componentId}.SCANLINE", scanlinePins);
        Dot = new DigitalBus($"{componentId}.DOT", dotPins);
        var pixelPins = new DigitalPin[6];
        for (var bit = 0; bit < 6; bit++) pixelPins[bit] = AddPin($"PIXEL{bit}", PinDirection.Output);
        Pixel = new DigitalBus($"{componentId}.PIXEL", pixelPins);
        PixelValid = AddPin("PIXEL_VALID", PinDirection.Output);
        DotTick = AddPin("DOT_TICK", PinDirection.Input);

        var ppuAddressPins = new DigitalPin[14];
        for (var bit = 0; bit < 14; bit++) ppuAddressPins[bit] = AddPin($"PPU_A{bit}", PinDirection.Output);
        PpuAddress = new DigitalBus($"{componentId}.PPU_A", ppuAddressPins);
        var ppuDataPins = new DigitalPin[8];
        for (var bit = 0; bit < 8; bit++) ppuDataPins[bit] = AddPin($"PPU_D{bit}", PinDirection.Bidirectional);
        PpuData = new DigitalBus($"{componentId}.PPU_D", ppuDataPins);
        PpuReadBar = AddPin("PPU_/RD", PinDirection.Output);
        PpuWriteBar = AddPin("PPU_/WR", PinDirection.Output);

        var dmaDataPins = new DigitalPin[8];
        for (var bit = 0; bit < 8; bit++) dmaDataPins[bit] = AddPin($"DMA_D{bit}", PinDirection.Input);
        DmaData = new DigitalBus($"{componentId}.DMA_D", dmaDataPins);
        DmaWrite = AddPin("DMA_WRITE", PinDirection.Input);
    }

    public DigitalBus Address { get; }
    public DigitalBus Data { get; }
    public DigitalPin ReadWrite { get; }
    public DigitalPin Vblank { get; }
    public DigitalPin NmiEnable { get; }
    public DigitalBus Scanline { get; }
    public DigitalBus Dot { get; }
    public DigitalBus Pixel { get; }
    public DigitalPin PixelValid { get; }
    public DigitalPin DotTick { get; }
    public DigitalBus PpuAddress { get; }
    public DigitalBus PpuData { get; }
    public DigitalPin PpuReadBar { get; }
    public DigitalPin PpuWriteBar { get; }
    public DigitalBus DmaData { get; }
    public DigitalPin DmaWrite { get; }

    public byte Control { get; private set; }
    public byte Mask { get; private set; }
    public byte OamAddress { get; private set; }
    public ushort VramAddress { get; private set; }
    public ushort TemporaryVramAddress { get; private set; }
    public byte FineX { get; private set; }
    public bool WriteToggle => _writeToggle;
    public ulong RegisterReadCount { get; private set; }
    public ulong RegisterWriteCount { get; private set; }
    public ulong BackgroundFetchCount { get; private set; }
    public ulong RenderedPixelCount { get; private set; }
    public ulong SpriteEvaluationCount { get; private set; }
    public ulong SpriteFetchCount { get; private set; }
    public ulong SpritePixelCount { get; private set; }
    public ulong SpriteZeroHitCount { get; private set; }
    public ulong SpriteOverflowCount { get; private set; }
    public ulong DmaWriteCount { get; private set; }
    public ulong ExternalPpuReadCount { get; private set; }
    public ulong ExternalPpuWriteCount { get; private set; }
    public ulong RenderBusReadCount { get; private set; }
    public bool SpriteZeroHit => _spriteZeroHit;
    public bool SpriteOverflow => _spriteOverflow;
    public int SecondarySpriteCount => _secondarySpriteCount;
    public ReadOnlyMemory<byte> FrameBuffer => _frameBuffer;
    public bool HasPendingInternalWork => _renderFetchState != RenderFetchState.Idle;

    public PinActivationContract CompileInputActivation(DigitalPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        if (pin.Direction == PinDirection.Input) return PinActivationContract.Always;
        if (Data.Pins.Contains(pin))
        {
            return PinActivationContract.When(() =>
                ReadWrite.SampledLevel != DigitalLevel.High);
        }

        if (PpuData.Pins.Contains(pin))
        {
            return PinActivationContract.When(() =>
                _renderReadIssued || PpuReadBar.DriveLevel == DigitalLevel.Low);
        }

        return PinActivationContract.Never;
    }

    public bool ShouldWakeForSampledPin(DigitalPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);

        // Input pins always represent external electrical information.
        if (pin.Direction == PinDirection.Input) return true;

        // CPU data is an input only while the CPU is writing. During a CPU
        // read the RP2C02 owns the bus and must not wake on its own resolved
        // output echo.
        if (Data.Pins.Contains(pin))
        {
            return ReadWrite.SampledLevel != DigitalLevel.High;
        }

        // The external PPU data bus is sampled only while a read phase is
        // outstanding. During writes, releases, and idle time its resolved
        // echo is not an input dependency.
        if (PpuData.Pins.Contains(pin))
        {
            return _renderReadIssued || PpuReadBar.DriveLevel == DigitalLevel.Low;
        }

        return false;
    }

    public byte InspectVram(ushort address) => _vram[address & 0x3FFF];
    public byte InspectOam(byte address) => _oam[address];
    public byte InspectSecondaryOam(int sprite, int field)
    {
        if ((uint)sprite >= 8 || (uint)field >= 4) throw new ArgumentOutOfRangeException();
        var entry = _secondaryOam[sprite];
        return field switch { 0 => entry.Y, 1 => entry.Tile, 2 => entry.Attributes, _ => entry.X };
    }
    public byte InspectPixel(int x, int y)
    {
        if ((uint)x >= 256 || (uint)y >= 240) throw new ArgumentOutOfRangeException();
        return _frameBuffer[(y * 256) + x];
    }

    public void LoadOamMemory(byte address, ReadOnlySpan<byte> data)
    {
        for (var index = 0; index < data.Length; index++)
        {
            _oam[(byte)(address + index)] = data[index];
        }
    }

    public void LoadPpuMemory(ushort address, ReadOnlySpan<byte> data)
    {
        for (var index = 0; index < data.Length; index++)
        {
            _vram[(address + index) & 0x3FFF] = data[index];
        }
    }

    public override void PowerOn()
    {
        Array.Clear(_vram);
        Array.Clear(_oam);
        Array.Clear(_frameBuffer);
        Control = 0;
        Mask = 0;
        OamAddress = 0;
        VramAddress = 0;
        TemporaryVramAddress = 0;
        FineX = 0;
        _writeToggle = false;
        _readBuffer = 0;
        _vblank = false;
        _vblankPinLast = false;
        _transactionActive = false;
        RegisterReadCount = 0;
        RegisterWriteCount = 0;
        BackgroundFetchCount = 0;
        RenderedPixelCount = 0;
        SpriteEvaluationCount = 0;
        SpriteFetchCount = 0;
        SpritePixelCount = 0;
        SpriteZeroHitCount = 0;
        SpriteOverflowCount = 0;
        DmaWriteCount = 0;
        ExternalPpuReadCount = 0;
        ExternalPpuWriteCount = 0;
        RenderBusReadCount = 0;
        _renderFetchState = RenderFetchState.Idle;
        _renderReadIssued = false;
        _secondarySpriteCount = 0;
        _spriteZeroHit = false;
        _spriteOverflow = false;
        Array.Clear(_secondaryOam);
        _dotTickWasHigh = false;
        _dmaWriteWasHigh = false;
        Data.Release();
        NmiEnable.Drive(DigitalLevel.Low);
        Pixel.Drive(0);
        PixelValid.Drive(DigitalLevel.Low);
        EndExternalPpuTransaction();
    }

    public override void Reset()
    {
        _transactionActive = false;
        _writeToggle = false;
        Data.Release();
        _renderFetchState = RenderFetchState.Idle;
        _renderReadIssued = false;
        EndExternalPpuTransaction();
    }

    public override void Evaluate()
    {
        EvaluateOamDmaWrite();
        AdvanceRenderFetch();
        EvaluateBackgroundPixel();
        var vblankPinHigh = Vblank.SampledLevel == DigitalLevel.High;
        if (vblankPinHigh && !_vblankPinLast) _vblank = true;
        _vblankPinLast = vblankPinHigh;

        if (!Address.TrySample(out var rawAddress) || rawAddress is < 0x2000 or > 0x3FFF)
        {
            EndTransaction();
            return;
        }

        var address = (ushort)rawAddress;
        var isRead = ReadWrite.SampledLevel == DigitalLevel.High;
        var isWrite = ReadWrite.SampledLevel == DigitalLevel.Low;
        if (!isRead && !isWrite)
        {
            EndTransaction();
            return;
        }

        if (!_transactionActive || _transactionAddress != address || _transactionRead != isRead)
        {
            if (isRead)
            {
                _transactionActive = true;
                _transactionAddress = address;
                _transactionRead = true;
                _readValue = ReadRegister(address);
                RegisterReadCount++;
            }
            else
            {
                // During settling the CPU can present address and R/W before
                // its write data has resolved. Do not consume the transaction
                // until a valid byte is actually present on D0-D7; otherwise
                // the later settled evaluation would be suppressed.
                Data.Release();
                if (!Data.TrySample(out var rawData))
                {
                    return;
                }

                _transactionActive = true;
                _transactionAddress = address;
                _transactionRead = false;
                WriteRegister(address, (byte)rawData);
                RegisterWriteCount++;
            }
        }

        if (isRead) Data.Drive(_readValue);
        else Data.Release();
    }


    private void EvaluateOamDmaWrite()
    {
        var high = DmaWrite.SampledLevel == DigitalLevel.High;
        if (high && !_dmaWriteWasHigh && DmaData.TrySample(out var value))
        {
            _oam[OamAddress] = (byte)value;
            OamAddress++;
            DmaWriteCount++;
        }
        _dmaWriteWasHigh = high;
    }

    private void EvaluateBackgroundPixel()
    {
        var dotTickHigh = DotTick.SampledLevel == DigitalLevel.High;
        if (dotTickHigh == _dotTickWasHigh)
        {
            if (_renderFetchState == RenderFetchState.Idle) PixelValid.Drive(DigitalLevel.Low);
            return;
        }

        _dotTickWasHigh = dotTickHigh;
        if (!Scanline.TrySample(out var rawScanline) || !Dot.TrySample(out var rawDot))
        {
            PixelValid.Drive(DigitalLevel.Low);
            return;
        }

        var scanline = (int)rawScanline;
        var dot = (int)rawDot;
        if (scanline == 261 && dot == 1)
        {
            _spriteZeroHit = false;
            _spriteOverflow = false;
        }

        if (scanline is < 0 or >= 240 || dot is < 1 or > 256)
        {
            PixelValid.Drive(DigitalLevel.Low);
            return;
        }

        if (_renderFetchState != RenderFetchState.Idle)
            throw new InvalidOperationException("RP2C02 rendering fetch did not complete before the next PPU dot.");

        _renderX = dot - 1;
        _renderY = scanline;
        if (_renderX == 0) EvaluateSpritesForScanline(scanline);
        _renderBackground = default;
        _renderSprite = default;
        _renderSpriteIndex = 0;
        PixelValid.Drive(DigitalLevel.Low);

        if ((Mask & 0x08) == 0 || (_renderX < 8 && (Mask & 0x02) == 0))
        {
            QueueRenderRead(0x3F00, RenderFetchState.AwaitBackgroundPalette);
            return;
        }

        var coarseX = TemporaryVramAddress & 0x001F;
        var coarseY = (TemporaryVramAddress >> 5) & 0x001F;
        var fineYScroll = (TemporaryVramAddress >> 12) & 0x0007;
        var worldX = _renderX + (coarseX * 8) + FineX;
        var worldY = _renderY + (coarseY * 8) + fineYScroll;
        var baseNametable = Control & 0x03;
        var nametableX = (baseNametable & 1) ^ ((worldX / 256) & 1);
        var nametableY = ((baseNametable >> 1) & 1) ^ ((worldY / 240) & 1);
        var nametable = nametableX | (nametableY << 1);
        var localX = worldX % 256;
        var localY = worldY % 240;
        var tileX = localX >> 3;
        var tileY = localY >> 3;
        _renderFineX = localX & 7;
        _renderFineY = localY & 7;
        var nametableBase = 0x2000 + (nametable * 0x400);
        _renderReadAddress = (ushort)(nametableBase + (tileY * 32) + tileX);
        _renderAttribute = 0;
        _renderPaletteSelect = ((tileY & 2) << 1) | (tileX & 2);
        QueueRenderRead(_renderReadAddress, RenderFetchState.AwaitNametable);
    }

    private void AdvanceRenderFetch()
    {
        if (_renderFetchState == RenderFetchState.Idle) return;

        if (!_renderReadIssued)
        {
            PpuAddress.Drive((ulong)(_renderReadAddress & 0x3FFF));
            PpuData.Release();
            PpuWriteBar.Drive(DigitalLevel.High);
            PpuReadBar.Drive(DigitalLevel.Low);
            _renderReadIssued = true;
            ExternalPpuReadCount++;
            RenderBusReadCount++;
            return;
        }

        if (!PpuData.TrySample(out var rawValue)) return;
        var value = (byte)rawValue;
        PpuReadBar.Drive(DigitalLevel.High);
        PpuAddress.Release();
        PpuData.Release();
        _renderReadIssued = false;

        switch (_renderFetchState)
        {
            case RenderFetchState.AwaitNametable:
            {
                _renderTileIndex = value;
                BackgroundFetchCount++;
                var coarseX = TemporaryVramAddress & 0x001F;
                var coarseY = (TemporaryVramAddress >> 5) & 0x001F;
                var worldX = _renderX + (coarseX * 8) + FineX;
                var worldY = _renderY + (coarseY * 8) + ((TemporaryVramAddress >> 12) & 7);
                var baseNametable = Control & 0x03;
                var nametableX = (baseNametable & 1) ^ ((worldX / 256) & 1);
                var nametableY = ((baseNametable >> 1) & 1) ^ ((worldY / 240) & 1);
                var nametable = nametableX | (nametableY << 1);
                var tileX = (worldX % 256) >> 3;
                var tileY = (worldY % 240) >> 3;
                var address = 0x2000 + (nametable * 0x400) + 0x3C0 + ((tileY >> 2) * 8) + (tileX >> 2);
                QueueRenderRead((ushort)address, RenderFetchState.AwaitAttribute);
                break;
            }
            case RenderFetchState.AwaitAttribute:
                _renderAttribute = value;
                BackgroundFetchCount++;
                _renderPaletteSelect = (_renderAttribute >> _renderPaletteSelect) & 0x03;
                QueueRenderRead((ushort)(((Control & 0x10) != 0 ? 0x1000 : 0x0000) + (_renderTileIndex * 16) + _renderFineY), RenderFetchState.AwaitBackgroundLow);
                break;
            case RenderFetchState.AwaitBackgroundLow:
                _renderLowPlane = value;
                BackgroundFetchCount++;
                QueueRenderRead((ushort)((_renderReadAddress + 8) & 0x3FFF), RenderFetchState.AwaitBackgroundHigh);
                break;
            case RenderFetchState.AwaitBackgroundHigh:
            {
                _renderHighPlane = value;
                BackgroundFetchCount++;
                var bit = 7 - _renderFineX;
                var pixel = ((_renderLowPlane >> bit) & 1) | (((_renderHighPlane >> bit) & 1) << 1);
                var paletteAddress = pixel == 0 ? 0x3F00 : 0x3F00 + (_renderPaletteSelect * 4) + pixel;
                _renderBackground = new PixelSample(0, pixel != 0);
                QueueRenderRead((ushort)paletteAddress, RenderFetchState.AwaitBackgroundPalette);
                break;
            }
            case RenderFetchState.AwaitBackgroundPalette:
                _renderBackground = new PixelSample((byte)(value & 0x3F), _renderBackground.Opaque);
                BeginNextSpriteFetchOrFinalize();
                break;
            case RenderFetchState.AwaitSpriteLow:
                _renderSpriteLow = value;
                SpriteFetchCount++;
                QueueRenderRead((ushort)((_renderReadAddress + 8) & 0x3FFF), RenderFetchState.AwaitSpriteHigh);
                break;
            case RenderFetchState.AwaitSpriteHigh:
            {
                _renderSpriteHigh = value;
                SpriteFetchCount++;
                var bit = 7 - _renderSpriteColumn;
                var pixel = ((_renderSpriteLow >> bit) & 1) | (((_renderSpriteHigh >> bit) & 1) << 1);
                if (pixel == 0)
                {
                    _renderSpriteIndex++;
                    BeginNextSpriteFetchOrFinalize();
                    break;
                }

                SpritePixelCount++;
                _renderSprite = new SpriteSample(0, true, (_renderSpriteEntry.Attributes & 0x20) != 0, _renderSpriteEntry.OriginalIndex == 0);
                var palette = _renderSpriteEntry.Attributes & 0x03;
                QueueRenderRead((ushort)(0x3F10 + (palette * 4) + pixel), RenderFetchState.AwaitSpritePalette);
                break;
            }
            case RenderFetchState.AwaitSpritePalette:
                _renderSprite = _renderSprite with { Color = (byte)(value & 0x3F) };
                FinalizeRenderedPixel();
                break;
        }
    }

    private void BeginNextSpriteFetchOrFinalize()
    {
        if ((Mask & 0x10) == 0 || (_renderX < 8 && (Mask & 0x04) == 0))
        {
            FinalizeRenderedPixel();
            return;
        }

        var height = (Control & 0x20) != 0 ? 16 : 8;
        while (_renderSpriteIndex < _secondarySpriteCount)
        {
            var sprite = _secondaryOam[_renderSpriteIndex];
            var column = _renderX - sprite.X;
            var row = _renderY - (sprite.Y + 1);
            if (column < 0 || column >= 8 || row < 0 || row >= height)
            {
                _renderSpriteIndex++;
                continue;
            }

            if ((sprite.Attributes & 0x40) != 0) column = 7 - column;
            if ((sprite.Attributes & 0x80) != 0) row = height - 1 - row;
            _renderSpriteEntry = sprite;
            _renderSpriteColumn = column;
            _renderSpriteRow = row;
            QueueRenderRead((ushort)SpritePatternAddress(sprite.Tile, row, height), RenderFetchState.AwaitSpriteLow);
            return;
        }

        FinalizeRenderedPixel();
    }

    private void FinalizeRenderedPixel()
    {
        var color = ComposePixel(_renderX, _renderBackground, _renderSprite);
        _frameBuffer[(_renderY * 256) + _renderX] = color;
        Pixel.Drive(color);
        PixelValid.Drive(DigitalLevel.High);
        RenderedPixelCount++;
        _renderFetchState = RenderFetchState.Idle;
        _renderReadIssued = false;
        EndExternalPpuTransaction();
    }

    private void QueueRenderRead(ushort address, RenderFetchState nextState)
    {
        _renderReadAddress = (ushort)(address & 0x3FFF);
        _renderFetchState = nextState;
        _renderReadIssued = false;
    }

    private void EvaluateSpritesForScanline(int scanline)
    {
        _secondarySpriteCount = 0;
        _spriteOverflow = false;
        Array.Clear(_secondaryOam);
        var height = (Control & 0x20) != 0 ? 16 : 8;
        for (var spriteIndex = 0; spriteIndex < 64; spriteIndex++)
        {
            SpriteEvaluationCount++;
            var offset = spriteIndex * 4;
            var y = _oam[offset];
            var row = scanline - (y + 1);
            if (row < 0 || row >= height) continue;

            if (_secondarySpriteCount < 8)
            {
                _secondaryOam[_secondarySpriteCount++] = new SpriteEntry(
                    y, _oam[offset + 1], _oam[offset + 2], _oam[offset + 3], spriteIndex);
            }
            else
            {
                _spriteOverflow = true;
                SpriteOverflowCount++;
                break;
            }
        }
    }

    private int SpritePatternAddress(byte tile, int row, int height)
    {
        if (height == 8)
        {
            var patternBase = (Control & 0x08) != 0 ? 0x1000 : 0x0000;
            return patternBase + (tile * 16) + row;
        }

        var table = (tile & 1) * 0x1000;
        var tileIndex = tile & 0xFE;
        if (row >= 8)
        {
            tileIndex++;
            row -= 8;
        }
        return table + (tileIndex * 16) + row;
    }

    private byte ComposePixel(int screenX, PixelSample background, SpriteSample sprite)
    {
        if (!sprite.Opaque) return background.Color;
        if (!background.Opaque) return sprite.Color;

        if (sprite.SpriteZero && screenX < 255 && (Mask & 0x18) == 0x18)
        {
            if (!_spriteZeroHit) SpriteZeroHitCount++;
            _spriteZeroHit = true;
        }

        return sprite.BehindBackground ? background.Color : sprite.Color;
    }

    private byte ReadRegister(ushort cpuAddress)
    {
        switch (cpuAddress & 7)
        {
            case 2: // PPUSTATUS
            {
                var result = (byte)((_vblank ? 0x80 : 0x00) | (_spriteZeroHit ? 0x40 : 0x00) | (_spriteOverflow ? 0x20 : 0x00) | (_readBuffer & 0x1F));
                _vblank = false;
                _writeToggle = false;
                return result;
            }
            case 4: // OAMDATA
                return _oam[OamAddress];
            case 7: // PPUDATA
            {
                var address = (ushort)(VramAddress & 0x3FFF);
                BeginExternalPpuRead(address);
                var value = _vram[address];
                byte result;
                if (address >= 0x3F00)
                {
                    result = value;
                    _readBuffer = _vram[(address - 0x1000) & 0x3FFF];
                }
                else
                {
                    result = _readBuffer;
                    _readBuffer = value;
                }
                IncrementVramAddress();
                return result;
            }
            default:
                return _readBuffer;
        }
    }

    private void WriteRegister(ushort cpuAddress, byte value)
    {
        switch (cpuAddress & 7)
        {
            case 0: // PPUCTRL
                Control = value;
                NmiEnable.Drive((value & 0x80) != 0 ? DigitalLevel.High : DigitalLevel.Low);
                TemporaryVramAddress = (ushort)((TemporaryVramAddress & 0xF3FF) | ((value & 0x03) << 10));
                break;
            case 1: // PPUMASK
                Mask = value;
                break;
            case 3: // OAMADDR
                OamAddress = value;
                break;
            case 4: // OAMDATA
                _oam[OamAddress++] = value;
                break;
            case 5: // PPUSCROLL
                if (!_writeToggle)
                {
                    FineX = (byte)(value & 7);
                    TemporaryVramAddress = (ushort)((TemporaryVramAddress & 0xFFE0) | (value >> 3));
                }
                else
                {
                    TemporaryVramAddress = (ushort)((TemporaryVramAddress & 0x8C1F) | ((value & 7) << 12) | ((value & 0xF8) << 2));
                }
                _writeToggle = !_writeToggle;
                break;
            case 6: // PPUADDR
                if (!_writeToggle)
                    TemporaryVramAddress = (ushort)((TemporaryVramAddress & 0x00FF) | ((value & 0x3F) << 8));
                else
                {
                    TemporaryVramAddress = (ushort)((TemporaryVramAddress & 0x7F00) | value);
                    VramAddress = TemporaryVramAddress;
                }
                _writeToggle = !_writeToggle;
                break;
            case 7: // PPUDATA
                BeginExternalPpuWrite((ushort)(VramAddress & 0x3FFF), value);
                _vram[VramAddress & 0x3FFF] = value;
                _readBuffer = value;
                IncrementVramAddress();
                break;
        }
    }

    private void IncrementVramAddress() => VramAddress = (ushort)((VramAddress + ((Control & 0x04) != 0 ? 32 : 1)) & 0x7FFF);

    private readonly record struct PixelSample(byte Color, bool Opaque);
    private readonly record struct SpriteSample(byte Color, bool Opaque, bool BehindBackground, bool SpriteZero);
    private readonly record struct SpriteEntry(byte Y, byte Tile, byte Attributes, byte X, int OriginalIndex);

    private void BeginExternalPpuRead(ushort address)
    {
        PpuAddress.Drive((ulong)(address & 0x3FFF));
        PpuData.Release();
        PpuWriteBar.Drive(DigitalLevel.High);
        PpuReadBar.Drive(DigitalLevel.Low);
        ExternalPpuReadCount++;
    }

    private void BeginExternalPpuWrite(ushort address, byte value)
    {
        PpuAddress.Drive((ulong)(address & 0x3FFF));
        PpuData.Drive(value);
        PpuReadBar.Drive(DigitalLevel.High);
        PpuWriteBar.Drive(DigitalLevel.Low);
        ExternalPpuWriteCount++;
    }

    private void EndExternalPpuTransaction()
    {
        PpuReadBar.Drive(DigitalLevel.High);
        PpuWriteBar.Drive(DigitalLevel.High);
        PpuAddress.Release();
        PpuData.Release();
    }

    private void EndTransaction()
    {
        _transactionActive = false;
        Data.Release();
        if (_renderFetchState == RenderFetchState.Idle) EndExternalPpuTransaction();
    }

    private enum RenderFetchState
    {
        Idle,
        AwaitNametable,
        AwaitAttribute,
        AwaitBackgroundLow,
        AwaitBackgroundHigh,
        AwaitBackgroundPalette,
        AwaitSpriteLow,
        AwaitSpriteHigh,
        AwaitSpritePalette
    }
}
