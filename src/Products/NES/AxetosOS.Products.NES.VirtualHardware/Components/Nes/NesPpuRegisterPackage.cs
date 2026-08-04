using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Nes;

/// <summary>
/// CPU-facing RP2C02 register package. It reacts only to the CPU address/data
/// bus, R/W and the external vblank signal. Register mirrors, write latch,
/// buffered PPUDATA reads, OAM access and VRAM incrementing are modelled here;
/// rendering remains a later independent PPU component.
/// </summary>
public sealed class NesPpuRegisterPackage : VirtualHardwareComponent
{
    private readonly byte[] _vram = new byte[0x4000];
    private readonly byte[] _oam = new byte[256];
    private bool _transactionActive;
    private bool _transactionRead;
    private ushort _transactionAddress;
    private byte _readValue;
    private bool _writeToggle;
    private byte _readBuffer;
    private bool _vblank;
    private bool _vblankPinLast;

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
    }

    public DigitalBus Address { get; }
    public DigitalBus Data { get; }
    public DigitalPin ReadWrite { get; }
    public DigitalPin Vblank { get; }

    public byte Control { get; private set; }
    public byte Mask { get; private set; }
    public byte OamAddress { get; private set; }
    public ushort VramAddress { get; private set; }
    public ushort TemporaryVramAddress { get; private set; }
    public byte FineX { get; private set; }
    public bool WriteToggle => _writeToggle;
    public ulong RegisterReadCount { get; private set; }
    public ulong RegisterWriteCount { get; private set; }

    public byte InspectVram(ushort address) => _vram[address & 0x3FFF];
    public byte InspectOam(byte address) => _oam[address];

    public override void PowerOn()
    {
        Array.Clear(_vram);
        Array.Clear(_oam);
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
        Data.Release();
    }

    public override void Reset()
    {
        _transactionActive = false;
        _writeToggle = false;
        Data.Release();
    }

    public override void Evaluate()
    {
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

    private byte ReadRegister(ushort cpuAddress)
    {
        switch (cpuAddress & 7)
        {
            case 2: // PPUSTATUS
            {
                var result = (byte)((_vblank ? 0x80 : 0x00) | (_readBuffer & 0x1F));
                _vblank = false;
                _writeToggle = false;
                return result;
            }
            case 4: // OAMDATA
                return _oam[OamAddress];
            case 7: // PPUDATA
            {
                var address = (ushort)(VramAddress & 0x3FFF);
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
                _vram[VramAddress & 0x3FFF] = value;
                _readBuffer = value;
                IncrementVramAddress();
                break;
        }
    }

    private void IncrementVramAddress() => VramAddress = (ushort)((VramAddress + ((Control & 0x04) != 0 ? 32 : 1)) & 0x7FFF);

    private void EndTransaction()
    {
        _transactionActive = false;
        Data.Release();
    }
}
