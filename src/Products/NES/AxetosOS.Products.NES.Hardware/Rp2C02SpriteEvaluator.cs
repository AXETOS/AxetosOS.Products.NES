using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

public readonly record struct EvaluatedSprite(
    byte PrimaryOamIndex,
    bool IsSpriteZero,
    byte Y,
    byte Tile,
    byte Attributes,
    byte X);

/// <summary>
/// Models the RP2C02 primary-to-secondary OAM evaluation circuit for one
/// upcoming scanline. The evaluator is clocked once per PPU dot.
/// </summary>
public sealed class Rp2C02SpriteEvaluator : INesHardwareModule
{
    private readonly byte[] _secondaryOam = new byte[32];
    private readonly byte[] _secondaryPrimaryIndices = new byte[8];
    private readonly bool[] _secondarySpriteZero = new bool[8];
    private byte _readLatch;
    private int _primaryIndex;
    private int _byteIndex;
    private int _secondaryAddress;
    private int _selectedSprites;
    private int _targetScanline;
    private int _spriteHeight;
    private bool _copyingSprite;
    private int _bytesCopiedForSprite;
    private bool _overflowSearch;
    private int _startingPrimaryIndex;

    public Rp2C02SpriteEvaluator()
    {
        Reset();
    }

    public string ModuleId => "nes.rp2c02.sprite-evaluator";
    public int TargetScanline => _targetScanline;
    public int SelectedSpriteCount => _selectedSprites;
    public bool SpriteZeroSelected { get; private set; }
    public bool OverflowDetected { get; private set; }
    public ReadOnlySpan<byte> SecondaryOam => _secondaryOam;
    public byte OamBusValue => _readLatch;

    public byte ReadSecondaryOamByte(int offset)
    {
        if ((uint)offset >= (uint)_secondaryOam.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        return _secondaryOam[offset];
    }

    public void PowerOn() => Reset();

    public void Reset()
    {
        Array.Fill(_secondaryOam, (byte)0xFF);
        Array.Fill(_secondaryPrimaryIndices, (byte)0xFF);
        Array.Clear(_secondarySpriteZero);
        _readLatch = 0xFF;
        _primaryIndex = 0;
        _byteIndex = 0;
        _startingPrimaryIndex = 0;
        _secondaryAddress = 0;
        _selectedSprites = 0;
        _targetScanline = 0;
        _spriteHeight = 8;
        _copyingSprite = false;
        _bytesCopiedForSprite = 0;
        _overflowSearch = false;
        SpriteZeroSelected = false;
        OverflowDetected = false;
    }

    public void BeginScanline(int targetScanline, int spriteHeight, byte oamAddress = 0)
    {
        _targetScanline = targetScanline;
        _spriteHeight = spriteHeight;
        _readLatch = 0xFF;
        _primaryIndex = oamAddress >> 2;
        _byteIndex = oamAddress & 0x03;
        _startingPrimaryIndex = _primaryIndex;
        _secondaryAddress = 0;
        _selectedSprites = 0;
        _copyingSprite = false;
        _bytesCopiedForSprite = 0;
        _overflowSearch = false;
        Array.Clear(_secondarySpriteZero);
        SpriteZeroSelected = false;
        OverflowDetected = false;
    }

    public void Clock(int dot, ReadOnlySpan<byte> primaryOam)
    {
        if (primaryOam.Length < 256)
        {
            throw new ArgumentException("Primary OAM must contain 256 bytes.", nameof(primaryOam));
        }

        if (dot is >= 1 and <= 64)
        {
            ClockSecondaryOamClear(dot);
            return;
        }

        if (dot is < 65 or > 256)
        {
            return;
        }

        if ((dot & 1) != 0)
        {
            ClockPrimaryRead(primaryOam);
        }
        else
        {
            ClockEvaluationWrite();
        }
    }

    public EvaluatedSprite GetSelectedSprite(int slot)
    {
        if ((uint)slot >= (uint)_selectedSprites)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        var offset = slot * 4;
        return new EvaluatedSprite(
            _secondaryPrimaryIndices[slot],
            _secondarySpriteZero[slot],
            _secondaryOam[offset],
            _secondaryOam[offset + 1],
            _secondaryOam[offset + 2],
            _secondaryOam[offset + 3]);
    }

    private void ClockSecondaryOamClear(int dot)
    {
        // The physical PPU performs a read on odd dots and writes $FF on even
        // dots. Only the write is externally observable in this model.
        if ((dot & 1) == 0)
        {
            _secondaryOam[(dot >> 1) - 1] = 0xFF;
        }
    }

    private void ClockPrimaryRead(ReadOnlySpan<byte> primaryOam)
    {
        if (_primaryIndex >= 64)
        {
            _readLatch = 0xFF;
            return;
        }

        _readLatch = primaryOam[(_primaryIndex * 4) + _byteIndex];
    }

    private void ClockEvaluationWrite()
    {
        if (_primaryIndex >= 64)
        {
            return;
        }

        if (_overflowSearch)
        {
            ClockOverflowSearch();
            return;
        }

        if (!_copyingSprite)
        {
            if (!IsSpriteInRange(_readLatch))
            {
                _primaryIndex++;
                return;
            }

            if (_selectedSprites >= 8)
            {
                _overflowSearch = true;
                ClockOverflowSearch();
                return;
            }

            _secondaryPrimaryIndices[_selectedSprites] = (byte)_primaryIndex;
            _secondarySpriteZero[_selectedSprites] = _selectedSprites == 0 && _primaryIndex == _startingPrimaryIndex;
            _secondaryOam[_secondaryAddress++] = _readLatch;
            _copyingSprite = true;
            _bytesCopiedForSprite = 1;
            AdvancePrimaryByteAddress();
            return;
        }

        _secondaryOam[_secondaryAddress++] = _readLatch;
        _bytesCopiedForSprite++;
        AdvancePrimaryByteAddress();
        if (_bytesCopiedForSprite < 4)
        {
            return;
        }

        if (_secondarySpriteZero[_selectedSprites])
        {
            SpriteZeroSelected = true;
        }

        _selectedSprites++;
        _copyingSprite = false;
        _bytesCopiedForSprite = 0;
        if (_selectedSprites >= 8)
        {
            _overflowSearch = true;
        }
    }

    private void AdvancePrimaryByteAddress()
    {
        _byteIndex++;
        if (_byteIndex < 4)
        {
            return;
        }

        _byteIndex = 0;
        _primaryIndex++;
    }

    private void ClockOverflowSearch()
    {
        // Once secondary OAM is full, the 2C02's comparator continues treating
        // the current m-indexed byte as a Y coordinate. A match advances both n
        // and m, producing the documented diagonal overflow bug.
        if (IsSpriteInRange(_readLatch))
        {
            OverflowDetected = true;
            _byteIndex = (_byteIndex + 1) & 0x03;
        }

        _primaryIndex++;
    }

    private bool IsSpriteInRange(byte y)
    {
        var row = _targetScanline - (y + 1);
        return row >= 0 && row < _spriteHeight;
    }
}
