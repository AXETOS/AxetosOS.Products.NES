using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Nes;

/// <summary>
/// Pin-driven NROM PPU memory subsystem. It exposes the RP2C02's fourteen-bit
/// address bus, eight-bit data bus and active-low read/write strobes. CHR,
/// CIRAM and palette behavior are selected exclusively from resolved pin
/// levels; no CPU or PPU implementation is called.
/// </summary>
public sealed class NesPpuMemoryDevice : VirtualHardwareComponent
{
    private readonly byte[] _characterMemory = new byte[8 * 1024];
    private readonly byte[] _ciram;
    private readonly byte[] _palette = new byte[32];
    private bool _writeActive;
    private ushort _writeAddress;
    private byte _writeValue;

    public NesPpuMemoryDevice(
        string componentId,
        ReadOnlySpan<byte> chrRom,
        NesNametableMirroring mirroring)
        : base(componentId)
    {
        if (chrRom.Length is not (0 or 8 * 1024))
            throw new ArgumentException("NROM CHR memory must be empty (CHR RAM) or exactly 8 KiB (CHR ROM).", nameof(chrRom));

        IsCharacterRam = chrRom.Length == 0;
        if (!IsCharacterRam) chrRom.CopyTo(_characterMemory);
        Mirroring = mirroring;
        _ciram = new byte[mirroring == NesNametableMirroring.FourScreen ? 4 * 1024 : 2 * 1024];

        var addressPins = new DigitalPin[14];
        for (var bit = 0; bit < addressPins.Length; bit++)
            addressPins[bit] = AddPin($"A{bit}", PinDirection.Input);
        Address = new DigitalBus($"{componentId}.A", addressPins);

        var dataPins = new DigitalPin[8];
        for (var bit = 0; bit < dataPins.Length; bit++)
            dataPins[bit] = AddPin($"D{bit}", PinDirection.Bidirectional);
        Data = new DigitalBus($"{componentId}.D", dataPins);

        ReadBar = AddPin("/RD", PinDirection.Input);
        WriteBar = AddPin("/WR", PinDirection.Input);
    }

    public DigitalBus Address { get; }
    public DigitalBus Data { get; }
    public DigitalPin ReadBar { get; }
    public DigitalPin WriteBar { get; }
    public NesNametableMirroring Mirroring { get; }
    public bool IsCharacterRam { get; }
    public ulong ReadDriveCount { get; private set; }
    public ulong WriteCount { get; private set; }

    public override void PowerOn()
    {
        if (IsCharacterRam) Array.Clear(_characterMemory);
        Array.Clear(_ciram);
        Array.Clear(_palette);
        _writeActive = false;
        ReadDriveCount = 0;
        WriteCount = 0;
        Data.Release();
    }

    public override void Reset()
    {
        _writeActive = false;
        Data.Release();
    }

    public override void Evaluate()
    {
        if (!Address.TrySample(out var rawAddress))
        {
            EndWrite();
            Data.Release();
            return;
        }

        var address = NormalizeAddress((ushort)rawAddress);
        var reading = ReadBar.SampledLevel == DigitalLevel.Low && WriteBar.SampledLevel != DigitalLevel.Low;
        var writing = WriteBar.SampledLevel == DigitalLevel.Low && ReadBar.SampledLevel != DigitalLevel.Low;

        if (reading)
        {
            EndWrite();
            Data.Drive(ReadMapped(address));
            ReadDriveCount++;
            return;
        }

        Data.Release();
        if (!writing || !Data.TrySample(out var rawValue))
        {
            EndWrite();
            return;
        }

        var value = (byte)rawValue;
        if (!_writeActive || _writeAddress != address || _writeValue != value)
        {
            WriteMapped(address, value);
            _writeActive = true;
            _writeAddress = address;
            _writeValue = value;
        }
    }

    public byte Inspect(ushort address) => ReadMapped(NormalizeAddress(address));

    public void LoadForDiagnostics(ushort address, ReadOnlySpan<byte> data)
    {
        for (var index = 0; index < data.Length; index++)
            WriteMapped(NormalizeAddress((ushort)(address + index)), data[index], allowChrRom: true);
    }

    private static ushort NormalizeAddress(ushort address) => (ushort)(address & 0x3FFF);

    private byte ReadMapped(ushort address)
    {
        if (address < 0x2000) return _characterMemory[address];
        if (address < 0x3F00) return _ciram[MapCiramAddress(address)];
        return _palette[MapPaletteAddress(address)];
    }

    private void WriteMapped(ushort address, byte value, bool allowChrRom = false)
    {
        if (address < 0x2000)
        {
            if (IsCharacterRam || allowChrRom)
            {
                if (_characterMemory[address] != value)
                {
                    _characterMemory[address] = value;
                    WriteCount++;
                }
            }
            return;
        }

        if (address < 0x3F00)
        {
            var mapped = MapCiramAddress(address);
            if (_ciram[mapped] != value)
            {
                _ciram[mapped] = value;
                WriteCount++;
            }
            return;
        }

        var paletteAddress = MapPaletteAddress(address);
        value &= 0x3F;
        if (_palette[paletteAddress] != value)
        {
            _palette[paletteAddress] = value;
            WriteCount++;
        }
    }

    private int MapCiramAddress(ushort address)
    {
        var offset = (address - 0x2000) & 0x0FFF;
        var table = offset >> 10;
        var withinTable = offset & 0x03FF;
        var physicalTable = Mirroring switch
        {
            NesNametableMirroring.Vertical => table & 1,
            NesNametableMirroring.Horizontal => table >> 1,
            NesNametableMirroring.FourScreen => table,
            _ => throw new InvalidOperationException($"Unknown mirroring mode {Mirroring}.")
        };
        return (physicalTable * 0x400) + withinTable;
    }

    private static int MapPaletteAddress(ushort address)
    {
        var mapped = (address - 0x3F00) & 0x1F;
        return mapped switch
        {
            0x10 => 0x00,
            0x14 => 0x04,
            0x18 => 0x08,
            0x1C => 0x0C,
            _ => mapped
        };
    }

    private void EndWrite() => _writeActive = false;
}
