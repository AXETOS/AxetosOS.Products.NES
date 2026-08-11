using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

public enum KonamiVrc6Variant : byte
{
    Vrc6A,
    Vrc6B
}

/// <summary>
/// Konami VRC6-family replaceable cartridge hardware for mappers 24 and 26.
/// The package owns its A0/A1 register-pin variant, 16 KiB + 8 KiB PRG banking,
/// eight CHR registers and their 1/2 KiB grouping modes, CIRAM/CHR nametable
/// routing, work-RAM gate, reusable VRC IRQ block and chip-local pulse/saw audio.
/// Motherboard and compiler see only package pins and generic physical facets.
/// </summary>
public sealed class KonamiVrc6Cartridge : VirtualHardwareComponent, IReplaceableCartridgeHardware,
    ICompiledBusTargetProvider, ICompiledCombinationalComponent
{
    private const int PrgBankSize = 8 * 1024;
    private const int ChrBankSize = 1 * 1024;
    private const int WorkRamWindowSize = 8 * 1024;
    private const int MaximumPrgBanks = 32;
    private const int MaximumChrBanks = 256;

    private byte[] _prg = [];
    private byte[] _chr = [];
    private byte[] _workRam = [];
    private readonly byte[] _chrRegisters = new byte[8];
    private readonly int[] _prgWindowBanks = new int[4];
    private readonly int[] _chrWindowBanks = new int[8];
    private readonly int[] _nametablePages = new int[4];
    private byte _prg16BankRegister;
    private byte _prg8BankRegister;
    private byte _bankingModeRegister;
    private int _prgBankMask;
    private int _chrBankMask;
    private int _mapperNumber;
    private KonamiVrc6Variant _variant;

    private bool _cpuReadAddressSelected;
    private bool _cpuSelectedFromWorkRam;
    private ushort _cpuSelectedAddress;
    private byte _cpuSelectedData;
    private bool _cpuCycleHighRomSelected;
    private bool _cpuCycleWorkRamSelected;
    private ushort _cpuCycleAddress;
    private bool _ppuReadActive;
    private ushort _ppuReadAddress;

    private readonly ulong _powerInputMask;
    private readonly ulong _cpuAddressControlInputMask;
    private readonly ulong _cpuM2InputMask;
    private readonly ulong _cpuRomSelectInputMask;
    private readonly ulong _ppuAddressControlInputMask;

    public KonamiVrc6Cartridge(string componentId) : base(componentId)
    {
        Vcc = AddPin("VCC", PinDirection.Input);
        Gnd = AddPin("GND", PinDirection.Input);
        CpuAddress = new DigitalBus($"{componentId}.CPU.A",
            Enumerable.Range(0, 15).Select(i => AddPin($"CPU.A{i}", PinDirection.Input)).ToArray());
        CpuData = new DigitalBus($"{componentId}.CPU.D",
            Enumerable.Range(0, 8).Select(i => AddPin($"CPU.D{i}", PinDirection.Bidirectional)).ToArray());
        CpuReadWrite = AddPin("CPU.RW", PinDirection.Input);
        CpuM2 = AddPin("CPU.M2", PinDirection.Input);
        CpuRomSelectBar = AddPin("CPU.ROMSEL_BAR", PinDirection.Input);
        PpuAddress = new DigitalBus($"{componentId}.PPU.A",
            Enumerable.Range(0, 14).Select(i => AddPin($"PPU.A{i}", PinDirection.Input)).ToArray());
        PpuData = new DigitalBus($"{componentId}.PPU.D",
            Enumerable.Range(0, 8).Select(i => AddPin($"PPU.D{i}", PinDirection.Bidirectional)).ToArray());
        PpuReadBar = AddPin("PPU.RD_BAR", PinDirection.Input);
        PpuWriteBar = AddPin("PPU.WR_BAR", PinDirection.Input);
        CiramChipEnableBar = AddPin("CIRAM.CE_BAR", PinDirection.Output);
        CiramA10 = AddPin("CIRAM.A10", PinDirection.Output);
        IrqBar = AddPin("IRQ_BAR", PinDirection.Output);

        Irq = new KonamiVrcIrqCounter();
        Audio = new KonamiVrc6Audio();

        _powerInputMask = Vcc.InputChangeMask | Gnd.InputChangeMask;
        _cpuAddressControlInputMask = CpuAddress.InputChangeMask | CpuReadWrite.InputChangeMask;
        _cpuM2InputMask = CpuM2.InputChangeMask;
        _cpuRomSelectInputMask = CpuRomSelectBar.InputChangeMask;
        _ppuAddressControlInputMask = PpuAddress.InputChangeMask |
            PpuReadBar.InputChangeMask | PpuWriteBar.InputChangeMask;

        CpuData.SetOwnerWakeEnabled(false);
        PpuData.SetOwnerWakeEnabled(false);
        ApplyResetState();
    }

    public int MapperNumber => _mapperNumber;
    public DigitalPin Vcc { get; }
    public DigitalPin Gnd { get; }
    public DigitalBus CpuAddress { get; }
    public DigitalBus CpuData { get; }
    public DigitalPin CpuReadWrite { get; }
    public DigitalPin CpuM2 { get; }
    public DigitalPin CpuRomSelectBar { get; }
    public DigitalBus PpuAddress { get; }
    public DigitalBus PpuData { get; }
    public DigitalPin PpuReadBar { get; }
    public DigitalPin PpuWriteBar { get; }
    public DigitalPin CiramChipEnableBar { get; }
    public DigitalPin CiramA10 { get; }
    public DigitalPin IrqBar { get; }

    public bool IsInserted { get; private set; }
    public KonamiVrc6Variant Variant => _variant;
    public byte Prg16BankRegister => _prg16BankRegister;
    public byte Prg8BankRegister => _prg8BankRegister;
    public byte BankingModeRegister => _bankingModeRegister;
    public IReadOnlyList<byte> ChrBankRegisters => _chrRegisters;
    public IReadOnlyList<int> PrgWindowBanks => _prgWindowBanks;
    public IReadOnlyList<int> ChrWindowBanks => _chrWindowBanks;
    public IReadOnlyList<int> NametablePages => _nametablePages;
    public bool NametablesUseChrRom => (_bankingModeRegister & 0x10) != 0;
    public bool WorkRamEnabled => (_bankingModeRegister & 0x80) != 0 && _workRam.Length != 0;
    public int WorkRamSizeBytes => _workRam.Length;
    public KonamiVrcIrqCounter Irq { get; }
    public KonamiVrc6Audio Audio { get; }

    public ulong MapperWriteCount { get; private set; }
    public ushort LastMapperWriteAddress { get; private set; }
    public ushort LastTranslatedMapperWriteAddress { get; private set; }
    public byte LastMapperWriteData { get; private set; }
    public ulong CpuReadCount { get; private set; }
    public ushort LastCpuReadAddress { get; private set; }
    public byte LastCpuReadData { get; private set; }
    public ulong WorkRamReadCount { get; private set; }
    public ulong WorkRamWriteCount { get; private set; }
    public ulong PpuReadCount { get; private set; }
    public ulong PpuWriteCount { get; private set; }
    public ulong ChrNametableReadCount { get; private set; }

    public void LoadImage(VirtualHardwareNesRomImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.MapperNumber is not (24 or 26))
            throw new NotSupportedException($"Mapper {image.MapperNumber} is not VRC6-family hardware modeled by this package.");
        if (image.Mirroring == VirtualHardwareNesMirroring.FourScreen)
            throw new NotSupportedException("VRC6 four-screen/external nametable-RAM boards require distinct physical cartridge RAM topology.");
        if (image.ChrRom.Length == 0)
            throw new NotSupportedException("Commercial VRC6 boards modeled here use CHR ROM; CHR-RAM-only hardware requires distinct physical verification.");
        if (image.TotalChrRamSizeBytes != 0 && image.HasExplicitRamSizes)
            throw new NotSupportedException("Mixed VRC6 CHR ROM/RAM boards are not modeled by this package.");

        if (image.PrgRom.Length < 4 * PrgBankSize || image.PrgRom.Length % PrgBankSize != 0)
            throw new ArgumentException("VRC6 PRG ROM must contain at least four whole 8 KiB banks.", nameof(image));
        var prgBanks = image.PrgRom.Length / PrgBankSize;
        if (prgBanks > MaximumPrgBanks || !IsPowerOfTwo(prgBanks))
            throw new NotSupportedException($"VRC6 PRG ROM must expose a power-of-two count of at most {MaximumPrgBanks} 8 KiB banks.");

        if (image.ChrRom.Length < 8 * ChrBankSize || image.ChrRom.Length % ChrBankSize != 0)
            throw new ArgumentException("VRC6 CHR ROM must contain at least eight whole 1 KiB banks.", nameof(image));
        var chrBanks = image.ChrRom.Length / ChrBankSize;
        if (chrBanks > MaximumChrBanks || !IsPowerOfTwo(chrBanks))
            throw new NotSupportedException($"VRC6 CHR ROM must expose a power-of-two count of at most {MaximumChrBanks} 1 KiB banks on the commercial package wiring.");

        var workRamSize = ResolveWorkRamSize(image);
        if (workRamSize != 0 && workRamSize != WorkRamWindowSize)
            throw new NotSupportedException("The VRC6 package currently supports the physically common single 8 KiB work-RAM window.");

        _mapperNumber = image.MapperNumber;
        _variant = image.MapperNumber == 24 ? KonamiVrc6Variant.Vrc6A : KonamiVrc6Variant.Vrc6B;
        _prg = image.PrgRom.ToArray();
        _chr = image.ChrRom.ToArray();
        _workRam = workRamSize == 0 ? [] : new byte[workRamSize];
        _prgBankMask = prgBanks - 1;
        _chrBankMask = chrBanks - 1;
        IsInserted = true;
        ApplyResetState();
    }

    public void Eject()
    {
        IsInserted = false;
        _mapperNumber = 0;
        _prg = [];
        _chr = [];
        _workRam = [];
        _prgBankMask = 0;
        _chrBankMask = 0;
        ApplyResetState();
    }

    public byte InspectWorkRamByte(int offset)
    {
        if ((uint)offset >= (uint)_workRam.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        return _workRam[offset];
    }

    public void ResetDiagnostics()
    {
        MapperWriteCount = 0;
        LastMapperWriteAddress = 0;
        LastTranslatedMapperWriteAddress = 0;
        LastMapperWriteData = 0;
        CpuReadCount = 0;
        LastCpuReadAddress = 0;
        LastCpuReadData = 0;
        WorkRamReadCount = 0;
        WorkRamWriteCount = 0;
        PpuReadCount = 0;
        PpuWriteCount = 0;
        ChrNametableReadCount = 0;
    }

    private void ApplyResetState()
    {
        _prg16BankRegister = 0;
        _prg8BankRegister = 0;
        _bankingModeRegister = 0;
        Array.Clear(_chrRegisters);
        Array.Clear(_prgWindowBanks);
        Array.Clear(_chrWindowBanks);
        Array.Clear(_nametablePages);

        if (_prg.Length != 0)
        {
            var prgBanks = _prg.Length / PrgBankSize;
            _prgWindowBanks[0] = 0;
            _prgWindowBanks[1] = Math.Min(1, prgBanks - 1);
            _prgWindowBanks[2] = 0;
            _prgWindowBanks[3] = prgBanks - 1;
        }
        if (_chr.Length != 0)
        {
            var chrBanks = _chr.Length / ChrBankSize;
            for (var slot = 0; slot < 8; slot++)
            {
                _chrRegisters[slot] = (byte)slot;
                _chrWindowBanks[slot] = Math.Min(slot, chrBanks - 1);
            }
        }
        RefreshPpuBanking();

        _cpuReadAddressSelected = false;
        _cpuSelectedFromWorkRam = false;
        _cpuSelectedAddress = 0;
        _cpuSelectedData = 0;
        _cpuCycleHighRomSelected = false;
        _cpuCycleWorkRamSelected = false;
        _cpuCycleAddress = 0;
        _ppuReadActive = false;
        _ppuReadAddress = 0;
        ResetDiagnostics();
        Irq.Reset();
        Audio.Reset();
        ReleaseOutputs();
    }

    protected override void OnInputChanges(ulong changedInputMask)
    {
        if (changedInputMask == 0) return;
        var powerChanged = (changedInputMask & _powerInputMask) != 0;
        var cpuAddressOrControlChanged = (changedInputMask & _cpuAddressControlInputMask) != 0;
        var cpuM2Changed = (changedInputMask & _cpuM2InputMask) != 0;
        var cpuRomSelectChanged = (changedInputMask & _cpuRomSelectInputMask) != 0;
        var ppuAddressOrControlChanged = (changedInputMask & _ppuAddressControlInputMask) != 0;

        if (!powerChanged && !cpuAddressOrControlChanged && !cpuM2Changed &&
            !cpuRomSelectChanged && !ppuAddressOrControlChanged)
            return;

        if (!IsPowered())
        {
            _cpuReadAddressSelected = false;
            _cpuCycleHighRomSelected = false;
            _cpuCycleWorkRamSelected = false;
            _ppuReadActive = false;
            ReleaseOutputs();
            return;
        }

        if (powerChanged) RefreshIrqPhysical();
        if (powerChanged || ppuAddressOrControlChanged) ProcessPpuPort();

        if (!powerChanged && cpuM2Changed && CpuM2.SampledLevel == DigitalLevel.Low)
        {
            ClockCpuCycle();
            CompleteCpuTransaction();
        }

        if (powerChanged || cpuAddressOrControlChanged || cpuRomSelectChanged || cpuM2Changed)
            UpdateCpuPort();
    }

    private bool IsPowered() => IsInserted
        && Vcc.SampledLevel == DigitalLevel.High
        && Gnd.SampledLevel == DigitalLevel.Low;

    private void UpdateCpuPort()
    {
        if (!CpuAddress.TrySample(out var rawAddress))
        {
            _cpuReadAddressSelected = false;
            _cpuCycleHighRomSelected = false;
            _cpuCycleWorkRamSelected = false;
            CpuData.Release();
            return;
        }

        var connectorAddress = (ushort)(rawAddress & 0x7FFF);
        var m2High = CpuM2.SampledLevel == DigitalLevel.High;
        var highRomSelected = m2High && CpuRomSelectBar.SampledLevel == DigitalLevel.Low;
        var workRamSelected = m2High
            && CpuRomSelectBar.SampledLevel == DigitalLevel.High
            && WorkRamEnabled
            && (connectorAddress & 0x6000) == 0x6000;

        _cpuCycleHighRomSelected = highRomSelected;
        _cpuCycleWorkRamSelected = workRamSelected;
        _cpuCycleAddress = highRomSelected ? (ushort)(0x8000 | connectorAddress) : connectorAddress;

        CpuData.Release();
        _cpuReadAddressSelected = false;
        _cpuSelectedFromWorkRam = false;
        if (CpuReadWrite.SampledLevel != DigitalLevel.High) return;

        if (highRomSelected)
        {
            SelectCpuRead(_cpuCycleAddress, ReadPrg(_cpuCycleAddress));
            return;
        }

        if (workRamSelected)
        {
            _cpuSelectedFromWorkRam = true;
            SelectCpuRead(connectorAddress, ReadWorkRam(connectorAddress));
        }
    }

    private void SelectCpuRead(ushort address, byte value)
    {
        _cpuReadAddressSelected = true;
        _cpuSelectedAddress = address;
        _cpuSelectedData = value;
        CpuData.Drive(value);
    }

    private void CompleteCpuTransaction()
    {
        if (CpuReadWrite.SampledLevel == DigitalLevel.High)
        {
            if (!_cpuReadAddressSelected) return;
            CpuReadCount++;
            LastCpuReadAddress = _cpuSelectedAddress;
            LastCpuReadData = _cpuSelectedData;
            if (_cpuSelectedFromWorkRam) WorkRamReadCount++;
            return;
        }

        if (!CpuData.TrySample(out var rawData)) return;
        var value = (byte)rawData;
        if (_cpuCycleHighRomSelected)
        {
            WriteMapper(_cpuCycleAddress, value);
            return;
        }

        if (_cpuCycleWorkRamSelected)
            WriteWorkRam(_cpuCycleAddress, value);
    }

    private void ProcessPpuPort()
    {
        if (!PpuAddress.TrySample(out var rawAddress))
        {
            CiramChipEnableBar.Drive(DigitalLevel.High);
            CiramA10.Drive(DigitalLevel.Unknown);
            PpuData.Release();
            _ppuReadActive = false;
            return;
        }

        var address = (ushort)(rawAddress & 0x3FFF);
        DriveCiramOutputs(address);
        var readSelected = PpuReadBar.SampledLevel == DigitalLevel.Low && IsCartridgePpuReadSelected(address);
        if (!readSelected)
        {
            PpuData.Release();
            _ppuReadActive = false;
            return;
        }

        var newRead = !_ppuReadActive || _ppuReadAddress != address;
        PpuData.Drive(ReadPpu(address));
        if (newRead)
        {
            PpuReadCount++;
            if (address >= 0x2000) ChrNametableReadCount++;
        }
        _ppuReadAddress = address;
        _ppuReadActive = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DriveCiramOutputs(ushort address)
    {
        var nametableAddress = (address & 0x2000) != 0;
        CiramChipEnableBar.Drive(nametableAddress && !NametablesUseChrRom ? DigitalLevel.Low : DigitalLevel.High);
        CiramA10.Drive(EvaluateCiramA10(address));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private DigitalLevel EvaluateCiramA10(ushort address)
    {
        if (NametablesUseChrRom || (address & 0x2000) == 0) return DigitalLevel.Unknown;
        var page = _nametablePages[(address >> 10) & 0x03] & 1;
        return page != 0 ? DigitalLevel.High : DigitalLevel.Low;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsCartridgePpuReadSelected(ushort address) => address < 0x2000
        || (NametablesUseChrRom && address < 0x3F00 && (address & 0x2000) != 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadPrg(ushort address)
    {
        var slot = (address - 0x8000) >> 13;
        var bank = _prgWindowBanks[slot];
        return _prg[(bank * PrgBankSize) + (address & 0x1FFF)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadWorkRam(ushort address) => _workRam[address & 0x1FFF];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadPpu(ushort address)
    {
        if (address < 0x2000)
        {
            var slot = address >> 10;
            var bank = _chrWindowBanks[slot];
            return _chr[(bank * ChrBankSize) + (address & 0x03FF)];
        }

        var nametable = (address >> 10) & 0x03;
        var bankPage = _nametablePages[nametable] & _chrBankMask;
        return _chr[(bankPage * ChrBankSize) + (address & 0x03FF)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteWorkRam(ushort address, byte value)
    {
        _workRam[address & 0x1FFF] = value;
        WorkRamWriteCount++;
    }

    private void WriteMapper(ushort address, byte value)
    {
        MapperWriteCount++;
        LastMapperWriteAddress = address;
        LastMapperWriteData = value;

        var translated = TranslateMapperAddress(address);
        LastTranslatedMapperWriteAddress = translated;
        var register = (ushort)(translated & 0xF003);

        if (register is >= 0x8000 and <= 0x8003)
        {
            _prg16BankRegister = (byte)(value & 0x0F);
            RefreshPrgWindows();
            return;
        }

        if (register is >= 0x9000 and <= 0x9003 ||
            register is >= 0xA000 and <= 0xA002 ||
            register is >= 0xB000 and <= 0xB002)
        {
            Audio.WriteRegister(register, value);
            return;
        }

        if (register == 0xB003)
        {
            _bankingModeRegister = value;
            RefreshPpuBanking();
            RefreshCiramPhysical();
            return;
        }

        if (register is >= 0xC000 and <= 0xC003)
        {
            _prg8BankRegister = (byte)(value & 0x1F);
            RefreshPrgWindows();
            return;
        }

        if (register is >= 0xD000 and <= 0xD003)
        {
            _chrRegisters[register & 0x03] = value;
            RefreshPpuBanking();
            RefreshCiramPhysical();
            return;
        }

        if (register is >= 0xE000 and <= 0xE003)
        {
            _chrRegisters[4 + (register & 0x03)] = value;
            RefreshPpuBanking();
            RefreshCiramPhysical();
            return;
        }

        switch (register)
        {
            case 0xF000:
                Irq.SetReloadValue(value);
                break;
            case 0xF001:
                Irq.SetControl(value);
                RefreshIrqPhysical();
                break;
            case 0xF002:
                Irq.Acknowledge();
                RefreshIrqPhysical();
                break;
        }
    }

    private ushort TranslateMapperAddress(ushort address)
    {
        if (_variant != KonamiVrc6Variant.Vrc6B) return address;
        return (ushort)((address & 0xFFFC) | ((address & 0x0001) << 1) | ((address & 0x0002) >> 1));
    }

    private void RefreshPrgWindows()
    {
        if (_prg.Length == 0) return;
        var base16 = ((_prg16BankRegister & 0x0F) << 1) & _prgBankMask;
        _prgWindowBanks[0] = base16;
        _prgWindowBanks[1] = (base16 + 1) & _prgBankMask;
        _prgWindowBanks[2] = _prg8BankRegister & _prgBankMask;
        _prgWindowBanks[3] = _prgBankMask;
    }

    private void RefreshPpuBanking()
    {
        if (_chr.Length == 0) return;

        var mask = (_bankingModeRegister & 0x20) != 0 ? 0xFE : 0xFF;
        var orMask = (_bankingModeRegister & 0x20) != 0 ? 1 : 0;
        switch (_bankingModeRegister & 0x03)
        {
            case 0:
                for (var slot = 0; slot < 8; slot++)
                    _chrWindowBanks[slot] = _chrRegisters[slot] & _chrBankMask;
                break;
            case 1:
                for (var pair = 0; pair < 4; pair++)
                {
                    var even = _chrRegisters[pair] & mask;
                    _chrWindowBanks[pair * 2] = even & _chrBankMask;
                    _chrWindowBanks[(pair * 2) + 1] = (even | orMask) & _chrBankMask;
                }
                break;
            case 2:
            case 3:
                for (var slot = 0; slot < 4; slot++)
                    _chrWindowBanks[slot] = _chrRegisters[slot] & _chrBankMask;
                for (var pair = 0; pair < 2; pair++)
                {
                    var even = _chrRegisters[4 + pair] & mask;
                    _chrWindowBanks[4 + (pair * 2)] = even & _chrBankMask;
                    _chrWindowBanks[5 + (pair * 2)] = (even | orMask) & _chrBankMask;
                }
                break;
        }

        RefreshNametablePages();
    }

    private void RefreshNametablePages()
    {
        if (NametablesUseChrRom)
        {
            switch (_bankingModeRegister & 0x2F)
            {
                case 0x20:
                case 0x27:
                    SetNametablePages(_chrRegisters[6] & 0xFE, (_chrRegisters[6] & 0xFE) | 1,
                        _chrRegisters[7] & 0xFE, (_chrRegisters[7] & 0xFE) | 1);
                    return;
                case 0x23:
                case 0x24:
                    SetNametablePages(_chrRegisters[6] & 0xFE, _chrRegisters[7] & 0xFE,
                        (_chrRegisters[6] & 0xFE) | 1, (_chrRegisters[7] & 0xFE) | 1);
                    return;
                case 0x28:
                case 0x2F:
                    SetNametablePages(_chrRegisters[6] & 0xFE, _chrRegisters[6] & 0xFE,
                        _chrRegisters[7] & 0xFE, _chrRegisters[7] & 0xFE);
                    return;
                case 0x2B:
                case 0x2C:
                    SetNametablePages((_chrRegisters[6] & 0xFE) | 1, (_chrRegisters[7] & 0xFE) | 1,
                        (_chrRegisters[6] & 0xFE) | 1, (_chrRegisters[7] & 0xFE) | 1);
                    return;
            }

            switch (_bankingModeRegister & 0x07)
            {
                case 0:
                case 6:
                case 7:
                    SetNametablePages(_chrRegisters[6], _chrRegisters[6], _chrRegisters[7], _chrRegisters[7]);
                    break;
                case 1:
                case 5:
                    SetNametablePages(_chrRegisters[4], _chrRegisters[5], _chrRegisters[6], _chrRegisters[7]);
                    break;
                default:
                    SetNametablePages(_chrRegisters[6], _chrRegisters[7], _chrRegisters[6], _chrRegisters[7]);
                    break;
            }
            return;
        }

        switch (_bankingModeRegister & 0x2F)
        {
            case 0x20:
            case 0x27:
                SetNametablePages(0, 1, 0, 1);
                return;
            case 0x23:
            case 0x24:
                SetNametablePages(0, 0, 1, 1);
                return;
            case 0x28:
            case 0x2F:
                SetNametablePages(0, 0, 0, 0);
                return;
            case 0x2B:
            case 0x2C:
                SetNametablePages(1, 1, 1, 1);
                return;
        }

        switch (_bankingModeRegister & 0x07)
        {
            case 0:
            case 6:
            case 7:
                SetNametablePages(_chrRegisters[6] & 1, _chrRegisters[6] & 1, _chrRegisters[7] & 1, _chrRegisters[7] & 1);
                break;
            case 1:
            case 5:
                SetNametablePages(_chrRegisters[4] & 1, _chrRegisters[5] & 1, _chrRegisters[6] & 1, _chrRegisters[7] & 1);
                break;
            default:
                SetNametablePages(_chrRegisters[6] & 1, _chrRegisters[7] & 1, _chrRegisters[6] & 1, _chrRegisters[7] & 1);
                break;
        }
    }

    private void SetNametablePages(int a, int b, int c, int d)
    {
        _nametablePages[0] = NametablesUseChrRom ? a & _chrBankMask : a & 1;
        _nametablePages[1] = NametablesUseChrRom ? b & _chrBankMask : b & 1;
        _nametablePages[2] = NametablesUseChrRom ? c & _chrBankMask : c & 1;
        _nametablePages[3] = NametablesUseChrRom ? d & _chrBankMask : d & 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClockCpuCycle()
    {
        var wasAsserted = Irq.Asserted;
        Irq.ClockCpuCycle();
        Audio.ClockCpuCycle();
        if (Irq.Asserted != wasAsserted) RefreshIrqPhysical();
    }

    private void RefreshIrqPhysical()
    {
        if (Irq.Asserted) IrqBar.Drive(DigitalLevel.Low);
        else IrqBar.Release();
    }

    private void RefreshCiramPhysical()
    {
        if (!IsPowered() || !PpuAddress.TrySample(out var rawAddress)) return;
        DriveCiramOutputs((ushort)(rawAddress & 0x3FFF));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadCpuRomCompiled(ushort address)
    {
        var value = ReadPrg(address);
        RecordCpuRead(address, value);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadCpuWorkRamCompiled(ushort address)
    {
        var value = ReadWorkRam(address);
        WorkRamReadCount++;
        RecordCpuRead(address, value);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordCpuRead(ushort address, byte value)
    {
        CpuReadCount++;
        LastCpuReadAddress = address;
        LastCpuReadData = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteCpuHighCompiled(ushort address, byte value) => WriteMapper(address, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteCpuWorkRamCompiled(ushort address, byte value) => WriteWorkRam(address, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ObserveCompiledCpuBusCycle(bool _) => ClockCpuCycle();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadPpuCompiled(ushort address)
    {
        PpuReadCount++;
        if (address >= 0x2000) ChrNametableReadCount++;
        return ReadPpu(address);
    }

    private bool IsCpuWorkRamSelectedCompiled(int address, bool _) => WorkRamEnabled && (address & 0x6000) == 0x6000;
    private bool IsPpuSelectedCompiled(int address, bool writeCycle) => !writeCycle && IsCartridgePpuReadSelected((ushort)(address & 0x3FFF));

    private void ReleaseOutputs()
    {
        CpuData.Release();
        PpuData.Release();
        CiramChipEnableBar.Release();
        CiramA10.Release();
        IrqBar.Release();
    }

    bool ICompiledExternalDevice.ReadyForCompiledExecution => IsInserted;

    IEnumerable<CompiledBusTargetDescriptor> ICompiledBusTargetProvider.GetCompiledBusTargets()
    {
        if (!IsInserted) yield break;

        yield return new CompiledBusTargetDescriptor(
            this,
            CpuAddress.Pins,
            CpuData.Pins,
            new[]
            {
                new CompiledPinCondition(CpuReadWrite, DigitalLevel.High),
                new CompiledPinCondition(CpuRomSelectBar, DigitalLevel.Low)
            },
            new[]
            {
                new CompiledPinCondition(CpuReadWrite, DigitalLevel.Low),
                new CompiledPinCondition(CpuRomSelectBar, DigitalLevel.Low)
            },
            CompiledBusReadPhase.Complete,
            address => ReadCpuRomCompiled((ushort)(0x8000 | address)),
            (address, value) => WriteCpuHighCompiled((ushort)(0x8000 | address), value),
            ObserveCompiledCpuBusCycle,
            writePhase: CompiledBusWritePhase.Complete,
            observeBusCyclePhase: CompiledBusCycleObservationPhase.Complete);

        if (_workRam.Length != 0)
        {
            yield return new CompiledBusTargetDescriptor(
                this,
                CpuAddress.Pins,
                CpuData.Pins,
                new[]
                {
                    new CompiledPinCondition(CpuReadWrite, DigitalLevel.High),
                    new CompiledPinCondition(CpuM2, DigitalLevel.High),
                    new CompiledPinCondition(CpuRomSelectBar, DigitalLevel.High),
                    new CompiledPinCondition(CpuAddress.Pins[14], DigitalLevel.High),
                    new CompiledPinCondition(CpuAddress.Pins[13], DigitalLevel.High)
                },
                new[]
                {
                    new CompiledPinCondition(CpuReadWrite, DigitalLevel.Low),
                    new CompiledPinCondition(CpuM2, DigitalLevel.High),
                    new CompiledPinCondition(CpuRomSelectBar, DigitalLevel.High),
                    new CompiledPinCondition(CpuAddress.Pins[14], DigitalLevel.High),
                    new CompiledPinCondition(CpuAddress.Pins[13], DigitalLevel.High)
                },
                CompiledBusReadPhase.Complete,
                address => ReadCpuWorkRamCompiled((ushort)address),
                (address, value) => WriteCpuWorkRamCompiled((ushort)address, value),
                isSelected: IsCpuWorkRamSelectedCompiled,
                writePhase: CompiledBusWritePhase.Complete);
        }

        yield return new CompiledBusTargetDescriptor(
            this,
            PpuAddress.Pins,
            PpuData.Pins,
            new[] { new CompiledPinCondition(PpuReadBar, DigitalLevel.Low) },
            Array.Empty<CompiledPinCondition>(),
            CompiledBusReadPhase.Complete,
            address => ReadPpuCompiled((ushort)address),
            null,
            isSelected: IsPpuSelectedCompiled);
    }

    bool ICompiledCombinationalComponent.TryEvaluateCompiledOutput(
        DigitalPin output,
        Func<DigitalPin, DigitalLevel> sampleInput,
        out CompiledDriveState drive)
    {
        if (ReferenceEquals(output, CiramChipEnableBar))
        {
            var a13 = sampleInput(PpuAddress.Pins[13]);
            drive = new CompiledDriveState(a13 switch
            {
                DigitalLevel.Low => DigitalLevel.High,
                DigitalLevel.High => NametablesUseChrRom ? DigitalLevel.High : DigitalLevel.Low,
                _ => DigitalLevel.Unknown
            });
            return true;
        }

        if (ReferenceEquals(output, CiramA10))
        {
            var a13 = sampleInput(PpuAddress.Pins[13]);
            if (NametablesUseChrRom || a13 == DigitalLevel.Low)
            {
                drive = new CompiledDriveState(DigitalLevel.Unknown);
                return true;
            }
            if (a13 != DigitalLevel.High)
            {
                drive = new CompiledDriveState(DigitalLevel.Unknown);
                return true;
            }

            var a10 = sampleInput(PpuAddress.Pins[10]);
            var a11 = sampleInput(PpuAddress.Pins[11]);
            if (a10 is not (DigitalLevel.Low or DigitalLevel.High) || a11 is not (DigitalLevel.Low or DigitalLevel.High))
            {
                drive = new CompiledDriveState(DigitalLevel.Unknown);
                return true;
            }
            var index = (a10 == DigitalLevel.High ? 1 : 0) | (a11 == DigitalLevel.High ? 2 : 0);
            drive = new CompiledDriveState((_nametablePages[index] & 1) != 0 ? DigitalLevel.High : DigitalLevel.Low);
            return true;
        }

        if (ReferenceEquals(output, IrqBar))
        {
            drive = new CompiledDriveState(Irq.Asserted ? DigitalLevel.Low : DigitalLevel.HighImpedance);
            return true;
        }

        drive = default;
        return false;
    }

    private static int ResolveWorkRamSize(VirtualHardwareNesRomImage image)
    {
        if (image.HasExplicitRamSizes) return image.TotalPrgRamSizeBytes;
        return image.TotalPrgRamSizeBytes > 0 ? WorkRamWindowSize : 0;
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
}
