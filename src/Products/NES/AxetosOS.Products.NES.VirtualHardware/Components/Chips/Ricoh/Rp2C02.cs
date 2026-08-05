using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;

/// <summary>
/// Standalone NTSC Ricoh RP2C02 package. All observable behaviour is driven by
/// package power, reset, clock and bus pins. The chip owns only physical
/// internal state (registers, address latches, read buffer and primary OAM).
/// External PPU memory is accessed exclusively through AD0-AD7, A8-A13, ALE,
/// /RD and /WR.
/// </summary>
public sealed class Rp2C02 : VirtualHardwareComponent
{
    private const int DotsPerScanline = 341;
    private const int ScanlinesPerFrame = 262;
    private const int VblankStartScanline = 241;
    private const int PreRenderScanline = 261;

    private readonly byte[] _primaryOam = new byte[256];
    private DigitalLevel _previousClock;
    private bool _cpuSelectedLast;
    private bool _vblank;
    private bool _spriteZeroHit;
    private bool _spriteOverflow;
    private byte _control;
    private byte _mask;
    private byte _oamAddress;
    private byte _openBus;
    private byte _readBuffer;
    private ushort _vramAddress;
    private ushort _temporaryAddress;
    private byte _fineX;
    private bool _writeToggle;
    private VramTransaction _transaction;
    private int _transactionPhase;
    private VramTransactionPurpose _transactionPurpose;
    private byte _nextTileId;
    private byte _nextTileAttribute;
    private byte _nextTileLow;
    private byte _nextTileHigh;
    private ushort _patternShiftLow;
    private ushort _patternShiftHigh;
    private ushort _attributeShiftLow;
    private ushort _attributeShiftHigh;

    public Rp2C02(string componentId) : base(componentId)
    {
        Vcc = AddPin("VCC", PinDirection.Input);
        Gnd = AddPin("GND", PinDirection.Input);
        Clock = AddPin("CLK", PinDirection.Input);
        ResetBar = AddPin("/RES", PinDirection.Input);
        NmiBar = AddPin("/NMI", PinDirection.Output);

        RegisterSelect = CreateBus("RS", 3, PinDirection.Input);
        CpuData = CreateBus("D", 8, PinDirection.Bidirectional);
        CpuReadWrite = AddPin("R/W", PinDirection.Input);
        ChipSelectBar = AddPin("/CS", PinDirection.Input);

        MultiplexedAddressData = CreateBus("AD", 8, PinDirection.Bidirectional);
        HighAddress = CreateBus("A", 6, PinDirection.Output, firstBitNumber: 8);
        AddressLatchEnable = AddPin("ALE", PinDirection.Output);
        VramReadBar = AddPin("/RD", PinDirection.Output);
        VramWriteBar = AddPin("/WR", PinDirection.Output);
        Extension = CreateBus("EXT", 4, PinDirection.Bidirectional);
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

    public int Dot { get; private set; }
    public int Scanline { get; private set; }
    public ulong Frame { get; private set; }
    public ulong MasterClockRisingEdgeCount { get; private set; }
    public ulong CompletedVramReadCount { get; private set; }
    public ulong CompletedVramWriteCount { get; private set; }
    public bool Vblank => _vblank;
    public bool NmiEnabled => (_control & 0x80) != 0;
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
    public ushort PatternShiftLow => _patternShiftLow;
    public ushort PatternShiftHigh => _patternShiftHigh;

    private bool Powered => Vcc.SampledLevel == DigitalLevel.High && Gnd.SampledLevel == DigitalLevel.Low;

    public byte InspectOam(byte address) => _primaryOam[address];

    public override void PowerOn()
    {
        Dot = 0;
        Scanline = 0;
        Frame = 0;
        MasterClockRisingEdgeCount = 0;
        CompletedVramReadCount = 0;
        CompletedVramWriteCount = 0;
        BackgroundNametableFetchCount = 0;
        BackgroundAttributeFetchCount = 0;
        BackgroundPatternFetchCount = 0;
        BackgroundPixelIndex = 0;
        _previousClock = DigitalLevel.Low;
        _cpuSelectedLast = false;
        _vblank = false;
        _spriteZeroHit = false;
        _spriteOverflow = false;
        _control = 0;
        _mask = 0;
        _oamAddress = 0;
        _openBus = 0;
        _readBuffer = 0;
        _vramAddress = 0;
        _temporaryAddress = 0;
        _fineX = 0;
        _writeToggle = false;
        _transaction = VramTransaction.None;
        _transactionPhase = 0;
        _transactionPurpose = VramTransactionPurpose.None;
        _nextTileId = 0;
        _nextTileAttribute = 0;
        _nextTileLow = 0;
        _nextTileHigh = 0;
        _patternShiftLow = 0;
        _patternShiftHigh = 0;
        _attributeShiftLow = 0;
        _attributeShiftHigh = 0;
        Array.Clear(_primaryOam);
        ReleasePackageOutputs();
    }

    public override void Reset()
    {
        Dot = 0;
        Scanline = 0;
        _vblank = false;
        _spriteZeroHit = false;
        _spriteOverflow = false;
        _control = 0;
        _mask = 0;
        _writeToggle = false;
        _cpuSelectedLast = false;
        _transaction = VramTransaction.None;
        _transactionPhase = 0;
        _transactionPurpose = VramTransactionPurpose.None;
        BackgroundPixelIndex = 0;
        _patternShiftLow = 0;
        _patternShiftHigh = 0;
        _attributeShiftLow = 0;
        _attributeShiftHigh = 0;
        ReleasePackageOutputs();
    }

    public override void Evaluate()
    {
        if (!Powered)
        {
            ReleasePackageOutputs();
            _previousClock = Clock.SampledLevel;
            _cpuSelectedLast = false;
            return;
        }

        if (ResetBar.SampledLevel == DigitalLevel.Low)
        {
            Reset();
            _previousClock = Clock.SampledLevel;
            return;
        }

        var clock = Clock.SampledLevel;
        if (clock == DigitalLevel.High && _previousClock != DigitalLevel.High)
        {
            MasterClockRisingEdgeCount++;
            AdvanceRaster();
            AdvanceVramTransaction();
            AdvanceBackgroundPipeline();
        }
        _previousClock = clock;

        HandleCpuPort();
        DriveVramBus();
        DriveNmi();
    }

    private void AdvanceRaster()
    {
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

        if (Scanline == VblankStartScanline && Dot == 1) _vblank = true;
        else if (Scanline == PreRenderScanline && Dot == 1)
        {
            _vblank = false;
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
            var value = ReadCpuRegister(register, !_cpuSelectedLast);
            CpuData.Drive(value);
        }
        else
        {
            CpuData.Release();
            if (!_cpuSelectedLast && CpuData.TrySample(out var rawValue))
            {
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
                    _vblank = false;
                    _writeToggle = false;
                }
                break;
            case 4: // OAMDATA
                value = _primaryOam[_oamAddress];
                break;
            case 7: // PPUDATA
                value = _readBuffer;
                if (firstSelectedEvaluation && _transaction == VramTransaction.None)
                {
                    StartVramTransaction(VramTransaction.Read, 0, VramTransactionPurpose.CpuRead);
                    IncrementVramAddress();
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
                _temporaryAddress = (ushort)((_temporaryAddress & ~0x0C00) | ((value & 0x03) << 10));
                break;
            case 1: // PPUMASK
                _mask = value;
                break;
            case 3: // OAMADDR
                _oamAddress = value;
                break;
            case 4: // OAMDATA
                _primaryOam[_oamAddress++] = value;
                break;
            case 5: // PPUSCROLL
                if (!_writeToggle)
                {
                    _fineX = (byte)(value & 0x07);
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
                if (_transaction == VramTransaction.None)
                {
                    StartVramTransaction(VramTransaction.Write, value, VramTransactionPurpose.CpuWrite);
                    IncrementVramAddress();
                }
                break;
        }
    }

    private void StartVramTransaction(VramTransaction transaction, byte writeData, VramTransactionPurpose purpose)
    {
        _transaction = transaction;
        _transactionPhase = 0;
        _transactionAddress = (ushort)(_vramAddress & 0x3FFF);
        _transactionWriteData = writeData;
        _transactionPurpose = purpose;
    }

    private ushort _transactionAddress;
    private byte _transactionWriteData;

    private void IncrementVramAddress()
    {
        _vramAddress = (ushort)((_vramAddress + (((_control & 0x04) != 0) ? 32 : 1)) & 0x3FFF);
    }

    private void AdvanceVramTransaction()
    {
        if (_transaction == VramTransaction.None) return;

        _transactionPhase++;
        var completionPhase = _transactionPurpose is VramTransactionPurpose.CpuRead or VramTransactionPurpose.CpuWrite ? 3 : 2;
        if (_transactionPhase < completionPhase) return;

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
        _transactionPurpose = VramTransactionPurpose.None;
    }

    private bool BackgroundRenderingEnabled => (_mask & 0x08) != 0;

    private void AdvanceBackgroundPipeline()
    {
        if (!BackgroundRenderingEnabled || !IsRenderingScanline())
        {
            BackgroundPixelIndex = 0;
            return;
        }

        if ((Dot >= 1 && Dot <= 256) || (Dot >= 321 && Dot <= 336))
        {
            ShiftBackgroundRegisters();
            UpdateBackgroundPixel();

            switch ((Dot - 1) & 7)
            {
                case 0:
                    LoadBackgroundShifters();
                    BeginBackgroundRead((ushort)(0x2000 | (_vramAddress & 0x0FFF)), VramTransactionPurpose.BackgroundNametable);
                    break;
                case 2:
                    var attributeAddress = (ushort)(0x23C0
                        | (_vramAddress & 0x0C00)
                        | ((_vramAddress >> 4) & 0x38)
                        | ((_vramAddress >> 2) & 0x07));
                    BeginBackgroundRead(attributeAddress, VramTransactionPurpose.BackgroundAttribute);
                    break;
                case 4:
                    BeginBackgroundRead(PatternAddress(highPlane: false), VramTransactionPurpose.BackgroundPatternLow);
                    break;
                case 6:
                    BeginBackgroundRead(PatternAddress(highPlane: true), VramTransactionPurpose.BackgroundPatternHigh);
                    break;
                case 7:
                    IncrementCoarseX();
                    break;
            }
        }

        if (Dot == 256) IncrementY();
        if (Dot == 257) CopyHorizontalScrollBits();
        if (Scanline == PreRenderScanline && Dot >= 280 && Dot <= 304) CopyVerticalScrollBits();
    }

    private bool IsRenderingScanline() => Scanline < 240 || Scanline == PreRenderScanline;

    private void BeginBackgroundRead(ushort address, VramTransactionPurpose purpose)
    {
        if (_transaction != VramTransaction.None) return;
        _transaction = VramTransaction.Read;
        _transactionPhase = 0;
        _transactionAddress = (ushort)(address & 0x3FFF);
        _transactionWriteData = 0;
        _transactionPurpose = purpose;
    }

    private ushort PatternAddress(bool highPlane)
    {
        var table = (_control & 0x10) != 0 ? 0x1000 : 0;
        var fineY = (_vramAddress >> 12) & 7;
        return (ushort)(table | (_nextTileId << 4) | fineY | (highPlane ? 8 : 0));
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
                var shift = (byte)(((_vramAddress >> 4) & 4) | (_vramAddress & 2));
                _nextTileAttribute = (byte)((data >> shift) & 3);
                BackgroundAttributeFetchCount++;
                break;
            case VramTransactionPurpose.BackgroundPatternLow:
                _nextTileLow = data;
                BackgroundPatternFetchCount++;
                break;
            case VramTransactionPurpose.BackgroundPatternHigh:
                _nextTileHigh = data;
                BackgroundPatternFetchCount++;
                break;
        }
    }

    private void LoadBackgroundShifters()
    {
        _patternShiftLow = (ushort)((_patternShiftLow & 0xFF00) | _nextTileLow);
        _patternShiftHigh = (ushort)((_patternShiftHigh & 0xFF00) | _nextTileHigh);
        _attributeShiftLow = (ushort)((_attributeShiftLow & 0xFF00) | ((_nextTileAttribute & 1) != 0 ? 0xFF : 0));
        _attributeShiftHigh = (ushort)((_attributeShiftHigh & 0xFF00) | ((_nextTileAttribute & 2) != 0 ? 0xFF : 0));
    }

    private void ShiftBackgroundRegisters()
    {
        _patternShiftLow <<= 1;
        _patternShiftHigh <<= 1;
        _attributeShiftLow <<= 1;
        _attributeShiftHigh <<= 1;
    }

    private void UpdateBackgroundPixel()
    {
        if (Dot > 256)
        {
            BackgroundPixelIndex = 0;
            return;
        }

        var selector = (ushort)(0x8000 >> _fineX);
        var pattern = (byte)(((_patternShiftLow & selector) != 0 ? 1 : 0)
            | ((_patternShiftHigh & selector) != 0 ? 2 : 0));
        var palette = (byte)(((_attributeShiftLow & selector) != 0 ? 1 : 0)
            | ((_attributeShiftHigh & selector) != 0 ? 2 : 0));
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

    private void DriveVramBus()
    {
        Extension.Release();
        if (_transaction == VramTransaction.None)
        {
            MultiplexedAddressData.Release();
            HighAddress.Release();
            AddressLatchEnable.Drive(DigitalLevel.Low);
            VramReadBar.Drive(DigitalLevel.High);
            VramWriteBar.Drive(DigitalLevel.High);
            return;
        }

        HighAddress.Drive((ulong)(_transactionAddress >> 8));
        if (_transactionPhase == 0)
        {
            MultiplexedAddressData.Drive((byte)_transactionAddress);
            AddressLatchEnable.Drive(DigitalLevel.High);
            VramReadBar.Drive(DigitalLevel.High);
            VramWriteBar.Drive(DigitalLevel.High);
            return;
        }

        AddressLatchEnable.Drive(DigitalLevel.Low);
        if (_transaction == VramTransaction.Read)
        {
            MultiplexedAddressData.Release();
            VramReadBar.Drive(DigitalLevel.Low);
            VramWriteBar.Drive(DigitalLevel.High);
        }
        else
        {
            MultiplexedAddressData.Drive(_transactionWriteData);
            VramReadBar.Drive(DigitalLevel.High);
            VramWriteBar.Drive(DigitalLevel.Low);
        }
    }

    private void DriveNmi()
    {
        if (_vblank && NmiEnabled) NmiBar.Drive(DigitalLevel.Low);
        else NmiBar.Release();
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
        BackgroundPatternHigh
    }
}
