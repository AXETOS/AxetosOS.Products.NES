using AxetosOS.Products.NES.Abstractions;
using AxetosOS.Products.NES.Cartridges;

namespace AxetosOS.Products.NES.Hardware;

public interface IPpuScanlineClock
{
    void ClockScanline();
}

public interface ICartridgeIrqProvider
{
    bool IrqAsserted { get; }
    event Action<bool>? IrqLineChanged;
}

public sealed record Mmc3DiagnosticsSnapshot(
    byte BankSelect,
    IReadOnlyList<byte> Registers,
    IReadOnlyList<int> PrgBanks,
    IReadOnlyList<int> ChrBanks,
    byte IrqLatch,
    byte IrqCounter,
    bool IrqReloadPending,
    bool IrqEnabled,
    bool IrqAsserted,
    bool PrgRamEnabled,
    bool PrgRamWriteProtected,
    NametableMirroring Mirroring,
    long RegisterWrites,
    long ScanlineClocks,
    long IrqAssertions);

/// <summary>Shared MMC3-family banking core used by mapper 4 and mapper 206.</summary>
public sealed class Mmc3CartridgeMemory : INesHardwareModule, ICpuBusDevice, IPpuBusDevice,
    IPpuScanlineClock, ICartridgeMirroringProvider, IBatteryBackedMemory, ICartridgeIrqProvider
{
    private const int PrgBankSize = 0x2000;
    private const int ChrBankSize = 0x0400;
    private readonly byte[] _prg;
    private readonly byte[] _chr;
    private readonly byte[] _prgRam = new byte[0x2000];
    private readonly byte[] _banks = new byte[8];
    private readonly bool _chrWritable;
    private readonly bool _hasIrq;
    private byte _bankSelect;
    private byte _irqLatch;
    private byte _irqCounter;
    private bool _irqReload;
    private bool _irqEnabled;
    private bool _prgRamEnabled;
    private bool _prgRamWriteProtected;
    private NametableMirroring _mirroring;
    private long _registerWrites;
    private long _scanlineClocks;
    private long _irqAssertions;

    public Mmc3CartridgeMemory(NesRomImage image, bool hasIrq)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.MapperNumber is not (4 or 206))
            throw new ArgumentException("MMC3-family hardware requires mapper 4 or 206.", nameof(image));
        _prg = image.PrgRom.ToArray();
        _chrWritable = image.ChrRom.Length == 0;
        _chr = _chrWritable ? new byte[0x2000] : image.ChrRom.ToArray();
        _hasIrq = hasIrq;
        _mirroring = image.Mirroring;
        HasBattery = image.HasBatteryBackedMemory;
        PowerOn();
    }

    public string ModuleId => _hasIrq ? "nes.cartridge.mmc3" : "nes.cartridge.dxrom";
    public NametableMirroring Mirroring => _mirroring;
    public bool HasBattery { get; }
    public int PersistentSize => _prgRam.Length;
    public bool IrqAsserted { get; private set; }
    public event Action<NametableMirroring>? MirroringChanged;
    public event Action<bool>? IrqLineChanged;

    public void PowerOn()
    {
        Array.Clear(_banks);
        Array.Clear(_prgRam);
        if (_chrWritable) Array.Clear(_chr);
        _bankSelect = _irqLatch = _irqCounter = 0;
        _irqReload = _irqEnabled = IrqAsserted = false;
        _prgRamEnabled = true;
        _prgRamWriteProtected = false;
        _registerWrites = _scanlineClocks = _irqAssertions = 0;
    }

    public void Reset()
    {
        _irqEnabled = false;
        SetIrq(false);
    }

    public bool HandlesCpuAddress(ushort address) => address >= 0x6000;

    public byte CpuRead(ushort address)
    {
        if (address < 0x8000) return _prgRamEnabled ? _prgRam[address - 0x6000] : (byte)0xFF;
        return _prg[MapPrg(address)];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address < 0x8000)
        {
            if (_prgRamEnabled && !_prgRamWriteProtected)
                _prgRam[address - 0x6000] = value;
            return;
        }

        _registerWrites++;
        var even = (address & 1) == 0;
        if (address <= 0x9FFF)
        {
            if (even) _bankSelect = value;
            else _banks[_bankSelect & 7] = value;
        }
        else if (address <= 0xBFFF)
        {
            if (even && _hasIrq)
            {
                SetMirroring((value & 1) == 0 ? NametableMirroring.Vertical : NametableMirroring.Horizontal);
            }
            else if (!even)
            {
                _prgRamEnabled = (value & 0x80) != 0;
                _prgRamWriteProtected = (value & 0x40) != 0;
            }
        }
        else if (address <= 0xDFFF && _hasIrq)
        {
            if (even) _irqLatch = value;
            else _irqReload = true;
        }
        else if (_hasIrq)
        {
            if (even)
            {
                _irqEnabled = false;
                SetIrq(false);
            }
            else _irqEnabled = true;
        }
    }

    public bool HandlesPpuAddress(ushort address) => address <= 0x1FFF;
    public byte PpuRead(ushort address) => _chr[MapChr(address)];
    public void PpuWrite(ushort address, byte value)
    {
        if (_chrWritable) _chr[MapChr(address)] = value;
    }

    public void ClockScanline()
    {
        if (!_hasIrq) return;
        _scanlineClocks++;
        ClockIrqCounter();
    }

    public Mmc3DiagnosticsSnapshot GetDiagnostics()
    {
        var prgBanks = new int[4];
        for (var slot = 0; slot < prgBanks.Length; slot++)
            prgBanks[slot] = MapPrgBank(slot);
        var chrBanks = new int[8];
        for (var slot = 0; slot < chrBanks.Length; slot++)
            chrBanks[slot] = MapChrBank(slot);
        return new Mmc3DiagnosticsSnapshot(
            _bankSelect,
            _banks.ToArray(),
            prgBanks,
            chrBanks,
            _irqLatch,
            _irqCounter,
            _irqReload,
            _irqEnabled,
            IrqAsserted,
            _prgRamEnabled,
            _prgRamWriteProtected,
            _mirroring,
            _registerWrites,
            _scanlineClocks,
            _irqAssertions);
    }

    public void LoadPersistent(ReadOnlySpan<byte> data) => data[..Math.Min(data.Length, _prgRam.Length)].CopyTo(_prgRam);
    public byte[] SavePersistent() => _prgRam.ToArray();

    private int MapPrg(ushort address)
    {
        var slot = (address - 0x8000) / PrgBankSize;
        return (MapPrgBank(slot) * PrgBankSize) + (address & 0x1FFF);
    }

    private int MapPrgBank(int slot)
    {
        var count = Math.Max(1, _prg.Length / PrgBankSize);
        var last = count - 1;
        var secondLast = Math.Max(0, count - 2);
        var mode = (_bankSelect & 0x40) != 0;
        var bank = slot switch
        {
            0 => mode ? secondLast : _banks[6],
            1 => _banks[7],
            2 => mode ? _banks[6] : secondLast,
            _ => last
        };
        return bank % count;
    }

    private int MapChr(ushort address)
    {
        var slot = address / ChrBankSize;
        return (MapChrBank(slot) * ChrBankSize) + (address & 0x03FF);
    }

    private int MapChrBank(int slot)
    {
        var count = Math.Max(1, _chr.Length / ChrBankSize);
        var inversion = (_bankSelect & 0x80) != 0;
        int bank;
        if (!inversion)
        {
            bank = slot switch
            {
                0 => _banks[0] & 0xFE,
                1 => (_banks[0] & 0xFE) + 1,
                2 => _banks[1] & 0xFE,
                3 => (_banks[1] & 0xFE) + 1,
                4 => _banks[2], 5 => _banks[3], 6 => _banks[4], _ => _banks[5]
            };
        }
        else
        {
            bank = slot switch
            {
                0 => _banks[2], 1 => _banks[3], 2 => _banks[4], 3 => _banks[5],
                4 => _banks[0] & 0xFE, 5 => (_banks[0] & 0xFE) + 1,
                6 => _banks[1] & 0xFE, _ => (_banks[1] & 0xFE) + 1
            };
        }
        return bank % count;
    }

    private void ClockIrqCounter()
    {
        if (_irqCounter == 0 || _irqReload)
        {
            _irqCounter = _irqLatch;
            _irqReload = false;
        }
        else _irqCounter--;
        if (_irqCounter == 0 && _irqEnabled) SetIrq(true);
    }

    private void SetMirroring(NametableMirroring value)
    {
        if (_mirroring == NametableMirroring.FourScreen || _mirroring == value) return;
        _mirroring = value;
        MirroringChanged?.Invoke(value);
    }

    private void SetIrq(bool value)
    {
        if (IrqAsserted == value) return;
        IrqAsserted = value;
        if (value) _irqAssertions++;
        IrqLineChanged?.Invoke(value);
    }
}
