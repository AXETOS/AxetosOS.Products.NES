using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

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
    private byte _fineX;
    private bool _writeToggle;
    private ushort _vramAddress;
    private ushort _temporaryAddress;
    private ushort _scanlineAddress;
    private readonly SpriteSample[] _scanlineSprites = new SpriteSample[ScreenWidth];
    private readonly int _preRenderScanline;
    private bool _nmiOutput;

    public Rp2C02Ppu(PpuBus bus, ISignalLine nmi, NesTimingProfile? timing = null)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _nmi = nmi ?? throw new ArgumentNullException(nameof(nmi));
        _preRenderScanline = (timing ?? NesTimingProfile.For(AxetosOS.Products.NES.Cartridges.NesTimingMode.Ntsc)).PpuScanlines - 1;
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
    public byte OamAddress => _oamAddress;
    public ulong VBlankStarts { get; private set; }
    public ulong NmiEdges { get; private set; }
    public ulong StatusReads { get; private set; }

    public byte ReadOamByte(byte address) => _oam[address];

    public void WriteOamDmaByte(byte value) => _oam[_oamAddress++] = value;

    public void PowerOn()
    {
        _control = 0;
        _mask = 0;
        _status = 0;
        _oamAddress = 0;
        _readBuffer = 0;
        _fineX = 0;
        _writeToggle = false;
        _vramAddress = 0;
        _temporaryAddress = 0;
        _scanlineAddress = 0;
        Scanline = 0;
        Dot = 0;
        Frame = 0;
        FrameCompleted = false;
        Array.Clear(_oam);
        Array.Clear(Framebuffer);
        Array.Clear(_scanlineSprites);
        _nmiOutput = false;
        VBlankStarts = 0;
        NmiEdges = 0;
        StatusReads = 0;
        _nmi.Release();
    }

    public void Reset()
    {
        _control = 0;
        _mask = 0;
        _writeToggle = false;
        SetNmiOutput(false);
    }

    public bool HandlesCpuAddress(ushort address) => address is >= 0x2000 and <= 0x3FFF;

    public byte CpuRead(ushort address)
    {
        return (address & 0x0007) switch
        {
            2 => ReadStatus(),
            4 => _oam[_oamAddress],
            7 => ReadData(),
            _ => 0
        };
    }

    public void CpuWrite(ushort address, byte value)
    {
        switch (address & 0x0007)
        {
            case 0:
                WriteControl(value);
                break;
            case 1:
                _mask = value;
                break;
            case 3:
                _oamAddress = value;
                break;
            case 4:
                _oam[_oamAddress++] = value;
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

    public void Clock()
    {
        FrameCompleted = false;

        if (Scanline is >= 0 and < ScreenHeight && Dot == 1)
        {
            // The active rendering address is latched for this scanline. CPU writes to
            // $2005/$2006 update the temporary address and must not immediately replace
            // the playfield currently being drawn. Horizontal bits are transferred at
            // dot 257 and vertical bits during the pre-render scanline.
            _scanlineAddress = Scanline == 0 && Frame == 0
                ? _vramAddress
                : RewindPrefetchedTiles(_vramAddress);
            PrepareSpriteScanline(Scanline);
        }

        if (Scanline is >= 0 and < ScreenHeight && Dot is >= 1 and <= ScreenWidth)
        {
            RenderVisiblePixel(Dot - 1, Scanline);
        }

        // MMC3-family cartridges receive one qualified scanline clock. The current
        // direct framebuffer renderer does not model every PPU fetch/A12 transition,
        // so this represents the filtered A12 rising edge produced during rendering.
        if (RenderingEnabled && Scanline is >= 0 and < ScreenHeight && Dot == 260)
        {
            _bus.ClockScanline();
        }

        if (Scanline == 241 && Dot == 1)
        {
            _status |= 0x80;
            VBlankStarts++;
            UpdateNmiLine();
        }
        else if (Scanline == _preRenderScanline && Dot == 1)
        {
            _status &= 0x1F;
            UpdateNmiLine();
        }

        if (RenderingEnabled && (Scanline is >= 0 and < 240 || Scanline == _preRenderScanline))
        {
            if ((Dot is >= 8 and <= 256 || Dot is 328 or 336) && Dot % 8 == 0)
            {
                IncrementCoarseX();
            }

            if (Dot == 256)
            {
                IncrementVerticalAddress();
            }

            if (Dot == 257)
            {
                CopyHorizontalAddress();
            }

            if (Scanline == _preRenderScanline && Dot is >= 280 and <= 304)
            {
                CopyVerticalAddress();
            }
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


    private static ushort RewindPrefetchedTiles(ushort address)
    {
        // At dots 328 and 336 the real PPU advances v while prefetching the first
        // two tiles for the next scanline. A cycle-accurate renderer consumes those
        // tiles from shift registers. This direct framebuffer renderer does not have
        // those shift registers, so compensate when taking the visible-scanline base.
        var coarseX = address & 0x001F;
        var nametableX = (address >> 10) & 0x01;

        if (coarseX >= 2)
        {
            coarseX -= 2;
        }
        else
        {
            coarseX = (coarseX + 32) - 2;
            nametableX ^= 0x01;
        }

        return (ushort)((address & ~0x041F) | coarseX | (nametableX << 10));
    }

    private bool RenderingEnabled => (_mask & 0x18) != 0;

    private void RenderVisiblePixel(int screenX, int screenY)
    {
        var background = ReadBackgroundPixel(screenX, screenY);
        var sprite = ReadSpritePixel(screenX);

        if (sprite.Opaque && background.Opaque && sprite.SpriteIndex == 0 && screenX < 255)
        {
            _status |= 0x40;
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

        // Render from the PPU's active VRAM address for this scanline, not from the
        // temporary scroll address. Games such as Super Mario Bros. rewrite $2005 while
        // the frame is active to prepare the next nametable; those writes must not make
        // the current scanline jump to the newly prepared screen.
        var coarseX = _scanlineAddress & 0x001F;
        var coarseY = (_scanlineAddress >> 5) & 0x001F;
        var nametableX = (_scanlineAddress >> 10) & 0x01;
        var nametableY = (_scanlineAddress >> 11) & 0x01;
        var fineY = (_scanlineAddress >> 12) & 0x07;

        var horizontalPixel = screenX + _fineX;
        var tileAdvance = horizontalPixel >> 3;
        var tileXWithWrap = coarseX + tileAdvance;
        nametableX ^= (tileXWithWrap >> 5) & 0x01;
        var tileX = tileXWithWrap & 0x1F;
        var tileY = coarseY;
        var fineX = horizontalPixel & 0x07;

        var baseNametable = 0x2000 + (nametableY * 0x0800) + (nametableX * 0x0400);

        var tileIndex = _bus.Read((ushort)(baseNametable + (tileY * 32) + tileX));
        var patternBase = (_control & 0x10) != 0 ? 0x1000 : 0x0000;
        var patternAddress = patternBase + (tileIndex * 16) + fineY;
        var lowPlane = _bus.Read((ushort)patternAddress);
        var highPlane = _bus.Read((ushort)(patternAddress + 8));
        var bit = 7 - fineX;
        var pixel = ((lowPlane >> bit) & 0x01) | (((highPlane >> bit) & 0x01) << 1);

        if (pixel == 0)
        {
            return new PixelSample(_bus.Read(0x3F00), false);
        }

        var attributeAddress = baseNametable + 0x03C0 + ((tileY >> 2) * 8) + (tileX >> 2);
        var attribute = _bus.Read((ushort)attributeAddress);
        var quadrantShift = ((tileY & 0x02) != 0 ? 4 : 0) + ((tileX & 0x02) != 0 ? 2 : 0);
        var palette = (attribute >> quadrantShift) & 0x03;
        return new PixelSample(_bus.Read((ushort)(0x3F00 + (palette * 4) + pixel)), true);
    }

    private SpriteSample ReadSpritePixel(int screenX)
    {
        if ((_mask & 0x10) == 0 || (screenX < 8 && (_mask & 0x04) == 0))
        {
            return SpriteSample.Transparent;
        }

        return _scanlineSprites[screenX];
    }

    private void PrepareSpriteScanline(int screenY)
    {
        Array.Clear(_scanlineSprites);

        if ((_mask & 0x10) == 0)
        {
            return;
        }

        var spriteHeight = (_control & 0x20) != 0 ? 16 : 8;
        var spritesOnScanline = 0;
        var evaluatedSprites = 0;

        for (var spriteIndex = 0; spriteIndex < 64; spriteIndex++)
        {
            var offset = spriteIndex * 4;
            var spriteTop = _oam[offset] + 1;
            var sourceRow = screenY - spriteTop;
            if (sourceRow < 0 || sourceRow >= spriteHeight)
            {
                continue;
            }

            spritesOnScanline++;
            if (evaluatedSprites >= 8)
            {
                continue;
            }

            evaluatedSprites++;
            var attributes = _oam[offset + 2];
            var patternRow = (attributes & 0x80) != 0
                ? spriteHeight - 1 - sourceRow
                : sourceRow;
            var patternAddress = GetSpritePatternAddress(_oam[offset + 1], patternRow, spriteHeight);
            var lowPlane = _bus.Read(patternAddress);
            var highPlane = _bus.Read((ushort)(patternAddress + 8));
            var spriteX = _oam[offset + 3];

            for (var outputColumn = 0; outputColumn < 8; outputColumn++)
            {
                var screenX = spriteX + outputColumn;
                if (screenX >= ScreenWidth || _scanlineSprites[screenX].Opaque)
                {
                    continue;
                }

                var sourceColumn = (attributes & 0x40) != 0 ? 7 - outputColumn : outputColumn;
                var bit = 7 - sourceColumn;
                var pixel = ((lowPlane >> bit) & 0x01) | (((highPlane >> bit) & 0x01) << 1);
                if (pixel == 0)
                {
                    continue;
                }

                var palette = attributes & 0x03;
                var paletteIndex = _bus.Read((ushort)(0x3F10 + (palette * 4) + pixel));
                _scanlineSprites[screenX] = new SpriteSample(
                    paletteIndex,
                    true,
                    (attributes & 0x20) != 0,
                    spriteIndex);
            }
        }

        if (spritesOnScanline > 8)
        {
            _status |= 0x20;
        }
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

    private readonly record struct PixelSample(byte PaletteIndex, bool Opaque);

    private readonly record struct SpriteSample(byte PaletteIndex, bool Opaque, bool BehindBackground, int SpriteIndex)
    {
        public static SpriteSample Transparent => new(0, false, false, -1);
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
        var value = _status;
        _status &= 0x7F;
        _writeToggle = false;
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
            result = value;
            _readBuffer = _bus.Read((ushort)(address - 0x1000));
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
