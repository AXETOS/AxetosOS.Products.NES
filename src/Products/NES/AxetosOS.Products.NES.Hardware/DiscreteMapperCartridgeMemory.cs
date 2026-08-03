using AxetosOS.Products.NES.Abstractions;
using AxetosOS.Products.NES.Cartridges;

namespace AxetosOS.Products.NES.Hardware;

/// <summary>
/// Reusable latch-and-bank cartridge hardware for common discrete mapper boards.
/// Mapper-specific address decoding remains explicit while PRG/CHR storage and
/// bank-window mapping are shared.
/// </summary>
public sealed class DiscreteMapperCartridgeMemory : INesHardwareModule, ICpuBusDevice, IPpuBusDevice,
    ICartridgeMirroringProvider
{
    private readonly NesRomImage _image;
    private readonly byte[] _prg;
    private readonly byte[] _chr;
    private readonly bool _chrWritable;
    private byte _register;
    private NametableMirroring _mirroring;

    public DiscreteMapperCartridgeMemory(NesRomImage image)
    {
        _image = image ?? throw new ArgumentNullException(nameof(image));
        if (image.MapperNumber is not (3 or 7 or 11 or 66 or 71 or 79))
            throw new ArgumentException("This cartridge device supports mappers 3, 7, 11, 66, 71 and 79.", nameof(image));

        _prg = image.PrgRom.ToArray();
        _chrWritable = image.ChrRom.Length == 0;
        _chr = _chrWritable ? new byte[8 * 1024] : image.ChrRom.ToArray();
        _mirroring = image.Mirroring;
        PowerOn();
    }

    public string ModuleId => $"nes.cartridge.mapper{_image.MapperNumber}";
    public NametableMirroring Mirroring => _mirroring;
    public event Action<NametableMirroring>? MirroringChanged;

    public void PowerOn()
    {
        _register = 0;
        if (_chrWritable) Array.Clear(_chr);
    }

    public void Reset() => _register = 0;

    public bool HandlesCpuAddress(ushort address) => _image.MapperNumber == 79
        ? address >= 0x4100
        : address >= 0x8000;

    public byte CpuRead(ushort address)
    {
        if (address < 0x8000) return 0xFF;
        var mapped = _image.MapperNumber switch
        {
            3 => MapFixedPrg(address),
            7 => Map32KPrg(address, _register & 0x07),
            11 => Map32KPrg(address, _register & 0x03),
            66 => Map32KPrg(address, (_register >> 4) & 0x03),
            71 => MapUxRomPrg(address, _register & 0x0F),
            79 => Map32KPrg(address, (_register >> 3) & 0x01),
            _ => 0
        };
        return _prg[mapped];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (_image.MapperNumber == 79 && address is < 0x4100 or > 0x5FFF) return;

        if (_image.MapperNumber == 71 && address is >= 0x9000 and <= 0x9FFF)
        {
            SetMirroring((value & 0x10) == 0
                ? NametableMirroring.SingleScreenLower
                : NametableMirroring.SingleScreenUpper);
            return;
        }

        _register = value;
        if (_image.MapperNumber == 7)
        {
            SetMirroring((value & 0x10) == 0
                ? NametableMirroring.SingleScreenLower
                : NametableMirroring.SingleScreenUpper);
        }
    }

    public bool HandlesPpuAddress(ushort address) => address <= 0x1FFF;

    public byte PpuRead(ushort address) => _chr[MapChr(address)];

    public void PpuWrite(ushort address, byte value)
    {
        if (_chrWritable) _chr[MapChr(address)] = value;
    }

    private int MapFixedPrg(ushort address)
    {
        var window = _prg.Length >= 0x8000 ? 0x8000 : 0x4000;
        return (address - 0x8000) % window;
    }

    private int Map32KPrg(ushort address, int bank)
    {
        const int size = 0x8000;
        var count = Math.Max(1, _prg.Length / size);
        return ((bank % count) * size) + (address - 0x8000);
    }

    private int MapUxRomPrg(ushort address, int bank)
    {
        const int size = 0x4000;
        var count = Math.Max(1, _prg.Length / size);
        var selected = address < 0xC000 ? bank % count : count - 1;
        return (selected * size) + (address & 0x3FFF);
    }

    private int MapChr(ushort address)
    {
        if (_chrWritable) return address & 0x1FFF;
        var bank = _image.MapperNumber switch
        {
            3 => _register & 0x03,
            11 => (_register >> 4) & 0x0F,
            66 => _register & 0x03,
            79 => _register & 0x07,
            _ => 0
        };
        const int size = 0x2000;
        var count = Math.Max(1, _chr.Length / size);
        return ((bank % count) * size) + (address & 0x1FFF);
    }

    private void SetMirroring(NametableMirroring value)
    {
        if (_mirroring == value) return;
        _mirroring = value;
        MirroringChanged?.Invoke(value);
    }
}
