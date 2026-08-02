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

    public Rp2C02Ppu(PpuBus bus, ISignalLine nmi)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _nmi = nmi ?? throw new ArgumentNullException(nameof(nmi));
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
    public uint[] Framebuffer { get; }

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
        Scanline = 0;
        Dot = 0;
        Frame = 0;
        FrameCompleted = false;
        Array.Clear(_oam);
        Array.Clear(Framebuffer);
        _nmi.Release();
    }

    public void Reset()
    {
        _control = 0;
        _mask = 0;
        _writeToggle = false;
        _nmi.Release();
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

        if (Scanline is >= 0 and < ScreenHeight && Dot is >= 1 and <= ScreenWidth)
        {
            RenderVisiblePixel(Dot - 1, Scanline);
        }

        if (Scanline == 241 && Dot == 1)
        {
            _status |= 0x80;
            UpdateNmiLine();
        }
        else if (Scanline == 261 && Dot == 1)
        {
            _status &= 0x1F;
            _nmi.Release();
        }

        Dot++;
        if (Dot <= 340)
        {
            return;
        }

        Dot = 0;
        Scanline++;
        if (Scanline <= 261)
        {
            return;
        }

        Scanline = 0;
        Frame++;
        FrameCompleted = true;
    }

    private void RenderVisiblePixel(int screenX, int screenY)
    {
        if ((_mask & 0x08) == 0)
        {
            WriteFramebufferPixel(screenX, screenY, _bus.Read(0x3F00));
            return;
        }

        var scrollX = ((_temporaryAddress & 0x001F) << 3) | _fineX;
        var scrollY = (((_temporaryAddress >> 5) & 0x001F) << 3) | ((_temporaryAddress >> 12) & 0x07);
        var worldX = (screenX + scrollX) % 512;
        var worldY = (screenY + scrollY) % 480;
        var nametableX = worldX / 256;
        var nametableY = worldY / 240;
        var localX = worldX % 256;
        var localY = worldY % 240;
        var baseNametable = 0x2000 + (nametableY * 0x0800) + (nametableX * 0x0400);
        var tileX = localX >> 3;
        var tileY = localY >> 3;
        var fineX = localX & 0x07;
        var fineY = localY & 0x07;

        var tileIndex = _bus.Read((ushort)(baseNametable + (tileY * 32) + tileX));
        var patternBase = (_control & 0x10) != 0 ? 0x1000 : 0x0000;
        var patternAddress = patternBase + (tileIndex * 16) + fineY;
        var lowPlane = _bus.Read((ushort)patternAddress);
        var highPlane = _bus.Read((ushort)(patternAddress + 8));
        var bit = 7 - fineX;
        var pixel = ((lowPlane >> bit) & 0x01) | (((highPlane >> bit) & 0x01) << 1);

        if (pixel == 0)
        {
            WriteFramebufferPixel(screenX, screenY, _bus.Read(0x3F00));
            return;
        }

        var attributeAddress = baseNametable + 0x03C0 + ((tileY >> 2) * 8) + (tileX >> 2);
        var attribute = _bus.Read((ushort)attributeAddress);
        var quadrantShift = ((tileY & 0x02) != 0 ? 4 : 0) + ((tileX & 0x02) != 0 ? 2 : 0);
        var palette = (attribute >> quadrantShift) & 0x03;
        var paletteAddress = (ushort)(0x3F00 + (palette * 4) + pixel);
        WriteFramebufferPixel(screenX, screenY, _bus.Read(paletteAddress));
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

        if (!wasNmiEnabled && (_control & 0x80) != 0 && InVBlank)
        {
            _nmi.Assert();
        }
        else
        {
            UpdateNmiLine();
        }
    }

    private byte ReadStatus()
    {
        var value = _status;
        _status &= 0x7F;
        _writeToggle = false;
        _nmi.Release();
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

    private void UpdateNmiLine()
    {
        if (InVBlank && (_control & 0x80) != 0)
        {
            _nmi.Assert();
        }
        else
        {
            _nmi.Release();
        }
    }
}
