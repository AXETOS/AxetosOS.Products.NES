using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

public enum BandaiFcgVariant
{
    Compatibility,
    Fcg12,
    Lz93D50
}

public enum BandaiFcgNametableMode : byte
{
    Vertical = 0,
    Horizontal = 1,
    SingleScreenPage0 = 2,
    SingleScreenPage1 = 3
}

/// <summary>
/// Mapper 16 / Bandai FCG replaceable cartridge hardware. FCG-1/2 and
/// LZ93D50 use the same 16 KiB PRG + eight 1 KiB CHR banking shape, but their
/// register decode and IRQ counter circuitry differ. NES 2.0 submapper 4
/// selects FCG-1/2 ($6000-$7FFF registers, direct counter writes); submapper 5
/// selects LZ93D50 ($8000-$FFFF registers, latched counter, optional 24C02
/// EEPROM). Legacy/unspecified submapper 0 responds in both physical register
/// ranges for compatibility while preserving the corresponding IRQ semantics.
/// </summary>
public sealed class BandaiFcgCartridge : VirtualHardwareComponent, IReplaceableCartridgeHardware,
    ICompiledBusTargetProvider, ICompiledCombinationalComponent, ICompiledStaticCombinationalComponent
{
    private const int PrgBankSize = 16 * 1024;
    private const int ChrBankSize = 1024;
    private const int EepromSize = 256;

    private byte[] _prg = [];
    private byte[] _chr = [];
    private byte _prgBankMask;
    private byte _chrBankMask;
    private byte _prgBankRegister;
    private readonly byte[] _chrBankRegisters = new byte[8];
    private BandaiFcgNametableMode _nametableMode;
    private BandaiFcgVariant _variant;

    private ushort _irqLatch;
    private ushort _irqCounter;
    private bool _irqEnabled;
    private bool _irqAsserted;

    private Bandai24C02Eeprom? _eeprom;
    private byte _eepromControl;

    private bool _cpuReadAddressSelected;
    private bool _cpuReadEepromSelected;
    private ushort _cpuSelectedAddress;
    private byte _cpuSelectedData;
    private bool _cpuCycleHighRomSelected;
    private bool _cpuCycleLowRegisterSelected;
    private ushort _cpuCycleAddress;
    private bool _ppuReadActive;
    private ushort _ppuReadAddress;

    private readonly ulong _powerInputMask;
    private readonly ulong _cpuAddressControlInputMask;
    private readonly ulong _cpuM2InputMask;
    private readonly ulong _cpuRomSelectInputMask;
    private readonly ulong _ppuAddressControlInputMask;

    public BandaiFcgCartridge(string componentId) : base(componentId)
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

        _powerInputMask = Vcc.InputChangeMask | Gnd.InputChangeMask;
        _cpuAddressControlInputMask = CpuAddress.InputChangeMask | CpuReadWrite.InputChangeMask;
        _cpuM2InputMask = CpuM2.InputChangeMask;
        _cpuRomSelectInputMask = CpuRomSelectBar.InputChangeMask;
        _ppuAddressControlInputMask = PpuAddress.InputChangeMask |
            PpuReadBar.InputChangeMask | PpuWriteBar.InputChangeMask;

        CpuData.SetOwnerWakeEnabled(false);
        PpuData.SetOwnerWakeEnabled(false);
        ApplyResetState(resetEepromProtocol: true);
    }

    public int MapperNumber => 16;
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
    public BandaiFcgVariant Variant => _variant;
    public int PrgBankCount => _prg.Length / PrgBankSize;
    public int ChrBankCount => _chr.Length / ChrBankSize;
    public byte PrgBankRegister => _prgBankRegister;
    public IReadOnlyList<byte> ChrBankRegisters => _chrBankRegisters;
    public int SelectedPrgBank => _prg.Length == 0 ? 0 : _prgBankRegister & _prgBankMask;
    public int FixedPrgBank => Math.Max(0, PrgBankCount - 1);
    public BandaiFcgNametableMode NametableMode => _nametableMode;
    public ushort IrqLatch => _irqLatch;
    public ushort IrqCounter => _irqCounter;
    public bool IrqEnabled => _irqEnabled;
    public bool IrqAsserted => _irqAsserted;
    public int EepromSizeBytes => _eeprom is null ? 0 : EepromSize;
    public byte EepromControl => _eepromControl;
    public bool EepromDataOutHigh => _eeprom?.DataLineHigh ?? true;

    public ulong MapperWriteCount { get; private set; }
    public ulong IrqClockCount { get; private set; }
    public ulong IrqAssertCount { get; private set; }
    public ulong EepromControlWriteCount { get; private set; }
    public ulong EepromReadCount => _eeprom?.ReadCount ?? 0;
    public ulong EepromWriteCount => _eeprom?.WriteCount ?? 0;
    public ushort LastMapperWriteAddress { get; private set; }
    public byte LastMapperWriteData { get; private set; }
    public ulong CpuReadCount { get; private set; }
    public ushort LastCpuReadAddress { get; private set; }
    public byte LastCpuReadData { get; private set; }
    public ulong PpuReadCount { get; private set; }
    public ulong PpuWriteCount { get; private set; }

    public void LoadImage(VirtualHardwareNesRomImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.MapperNumber != 16)
            throw new NotSupportedException($"Mapper {image.MapperNumber} is not Bandai FCG hardware.");
        if (image.Mirroring == VirtualHardwareNesMirroring.FourScreen)
            throw new NotSupportedException("Bandai FCG mapper 16 does not provide four-screen nametable RAM.");
        if (image.PrgRom.Length < 2 * PrgBankSize || image.PrgRom.Length % PrgBankSize != 0)
            throw new ArgumentException("Bandai FCG PRG ROM must contain at least two whole 16 KiB banks.", nameof(image));
        var prgBanks = image.PrgRom.Length / PrgBankSize;
        if (prgBanks > 16 || !IsPowerOfTwo(prgBanks))
            throw new NotSupportedException("Bandai FCG exposes a power-of-two PRG population of at most sixteen 16 KiB banks (256 KiB).");
        if (image.ChrRom.Length == 0 || image.ChrRom.Length % ChrBankSize != 0)
            throw new NotSupportedException("Mapper 16 FCG-1/2 and LZ93D50 boards require banked CHR ROM; CHR-RAM Bandai boards use distinct mapper numbers.");
        var chrBanks = image.ChrRom.Length / ChrBankSize;
        if (chrBanks > 256 || !IsPowerOfTwo(chrBanks))
            throw new NotSupportedException("Bandai FCG exposes a power-of-two CHR population of at most 256 one KiB banks (256 KiB).");
        if (image.HasExplicitRamSizes && image.TotalChrRamSizeBytes != 0)
            throw new NotSupportedException("Mapper 16 FCG-1/2 and LZ93D50 boards use CHR ROM rather than CHR RAM/NVRAM.");

        _variant = ResolveVariant(image.SubmapperNumber);
        var eepromSize = ResolveEepromSize(image, _variant);

        _prg = image.PrgRom.ToArray();
        _chr = image.ChrRom.ToArray();
        _prgBankMask = (byte)(prgBanks - 1);
        _chrBankMask = (byte)(chrBanks - 1);
        _eeprom = eepromSize == 0 ? null : new Bandai24C02Eeprom();
        _nametableMode = image.Mirroring == VirtualHardwareNesMirroring.Vertical
            ? BandaiFcgNametableMode.Vertical
            : BandaiFcgNametableMode.Horizontal;
        IsInserted = true;
        ApplyResetState(resetEepromProtocol: true);
    }

    public void Eject()
    {
        IsInserted = false;
        _prg = [];
        _chr = [];
        _eeprom = null;
        _cpuReadAddressSelected = false;
        _cpuReadEepromSelected = false;
        _cpuCycleHighRomSelected = false;
        _cpuCycleLowRegisterSelected = false;
        _ppuReadActive = false;
        ReleaseOutputs();
    }

    public byte InspectEepromByte(byte address)
    {
        if (_eeprom is null) throw new InvalidOperationException("This Bandai FCG cartridge has no 24C02 EEPROM fitted.");
        return _eeprom.Inspect(address);
    }

    private static BandaiFcgVariant ResolveVariant(int? submapper) => submapper switch
    {
        null or 0 => BandaiFcgVariant.Compatibility,
        4 => BandaiFcgVariant.Fcg12,
        5 => BandaiFcgVariant.Lz93D50,
        1 => throw new NotSupportedException("Mapper 16 submapper 1 is deprecated 128-byte X24C01 hardware; use mapper 159 for that distinct board."),
        2 => throw new NotSupportedException("Mapper 16 submapper 2 is deprecated Datach hardware; use mapper 157 for the Datach main unit."),
        3 => throw new NotSupportedException("Mapper 16 submapper 3 is deprecated WRAM/CHR-RAM hardware; use mapper 153 for that distinct board."),
        _ => throw new NotSupportedException($"Mapper 16 submapper {submapper} is not defined for supported Bandai FCG hardware.")
    };

    private static int ResolveEepromSize(VirtualHardwareNesRomImage image, BandaiFcgVariant variant)
    {
        if (variant == BandaiFcgVariant.Fcg12)
        {
            if (image.HasExplicitRamSizes && image.TotalPrgRamSizeBytes != 0)
                throw new NotSupportedException("FCG-1/2 submapper 4 has no PRG RAM/NVRAM or serial EEPROM.");
            if (image.HasBatteryBackedMemory)
                throw new NotSupportedException("FCG-1/2 submapper 4 has no battery-backed cartridge memory.");
            return 0;
        }

        if (image.HasExplicitRamSizes)
        {
            if (image.PrgRamSizeBytes > 0)
                throw new NotSupportedException("LZ93D50 mapper 16 uses serial EEPROM rather than volatile PRG RAM.");
            var nvram = Math.Max(0, image.PrgNvRamSizeBytes);
            if (nvram is not (0 or EepromSize))
                throw new NotSupportedException($"LZ93D50 mapper 16 supports no EEPROM or exactly {EepromSize} bytes of 24C02 EEPROM; image declares {nvram:N0} bytes.");
            return nvram;
        }

        // Legacy iNES cannot express the 256-byte serial EEPROM capacity. The
        // battery flag is the only board-level evidence available.
        return image.HasBatteryBackedMemory ? EepromSize : 0;
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    private bool IsPowered() =>
        IsInserted && Vcc.SampledLevel == DigitalLevel.High && Gnd.SampledLevel == DigitalLevel.Low;

    private bool LowRegisterWindowEnabled => _variant is BandaiFcgVariant.Compatibility or BandaiFcgVariant.Fcg12;
    private bool HighRegisterWindowEnabled => _variant is BandaiFcgVariant.Compatibility or BandaiFcgVariant.Lz93D50;
    private bool EepromPortEnabled => _eeprom is not null && _variant is not BandaiFcgVariant.Fcg12;

    private void ApplyResetState(bool resetEepromProtocol)
    {
        _prgBankRegister = 0;
        Array.Clear(_chrBankRegisters);
        _irqLatch = 0;
        _irqCounter = 0;
        _irqEnabled = false;
        _irqAsserted = false;
        _eepromControl = 0x80;
        if (resetEepromProtocol) _eeprom?.ResetProtocol();
        _cpuReadAddressSelected = false;
        _cpuReadEepromSelected = false;
        _cpuSelectedAddress = 0;
        _cpuSelectedData = 0;
        _cpuCycleHighRomSelected = false;
        _cpuCycleLowRegisterSelected = false;
        _cpuCycleAddress = 0;
        _ppuReadActive = false;
        _ppuReadAddress = 0;
        MapperWriteCount = 0;
        IrqClockCount = 0;
        IrqAssertCount = 0;
        EepromControlWriteCount = 0;
        LastMapperWriteAddress = 0;
        LastMapperWriteData = 0;
        CpuReadCount = 0;
        LastCpuReadAddress = 0;
        LastCpuReadData = 0;
        PpuReadCount = 0;
        PpuWriteCount = 0;
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
            _cpuReadEepromSelected = false;
            _cpuCycleHighRomSelected = false;
            _cpuCycleLowRegisterSelected = false;
            _ppuReadActive = false;
            ReleaseOutputs();
            return;
        }

        if (powerChanged) SetIrqAsserted(_irqAsserted);
        if (powerChanged || ppuAddressOrControlChanged) ProcessPpuPort();

        if (!powerChanged && cpuM2Changed && CpuM2.SampledLevel == DigitalLevel.Low)
        {
            // The cycle counter clocks from M2 independently of chip-select.
            // Clock before completing this cycle's write so a write that enables
            // or reloads the IRQ takes effect starting on the following cycle.
            ClockIrqCounter();
            CompleteCpuTransaction();
        }

        if (powerChanged || cpuAddressOrControlChanged || cpuRomSelectChanged || cpuM2Changed)
            UpdateCpuPort();
    }

    private void UpdateCpuPort()
    {
        if (!CpuAddress.TrySample(out var rawAddress))
        {
            _cpuReadAddressSelected = false;
            _cpuReadEepromSelected = false;
            _cpuCycleHighRomSelected = false;
            _cpuCycleLowRegisterSelected = false;
            CpuData.Release();
            return;
        }

        var connectorAddress = (ushort)(rawAddress & 0x7FFF);
        var m2High = CpuM2.SampledLevel == DigitalLevel.High;
        var highRomSelected = m2High && CpuRomSelectBar.SampledLevel == DigitalLevel.Low;
        var lowWindowSelected = m2High
            && CpuRomSelectBar.SampledLevel == DigitalLevel.High
            && (connectorAddress & 0x6000) == 0x6000;

        _cpuCycleHighRomSelected = highRomSelected;
        _cpuCycleLowRegisterSelected = lowWindowSelected && LowRegisterWindowEnabled;
        _cpuCycleAddress = highRomSelected
            ? (ushort)(0x8000 | connectorAddress)
            : connectorAddress;

        var read = CpuReadWrite.SampledLevel == DigitalLevel.High;
        _cpuReadAddressSelected = read && highRomSelected;
        _cpuReadEepromSelected = read && lowWindowSelected && EepromPortEnabled;

        CpuData.Release();
        if (_cpuReadAddressSelected)
        {
            _cpuSelectedAddress = _cpuCycleAddress;
            _cpuSelectedData = ReadPrg(_cpuSelectedAddress);
            CpuData.Drive(_cpuSelectedData);
            return;
        }

        if (_cpuReadEepromSelected)
        {
            _cpuSelectedAddress = connectorAddress;
            _cpuSelectedData = EepromDataOutHigh ? (byte)0x10 : (byte)0x00;
            CpuData.Pins[4].Drive(EepromDataOutHigh ? DigitalLevel.High : DigitalLevel.Low);
        }
    }

    private void CompleteCpuTransaction()
    {
        if (CpuReadWrite.SampledLevel == DigitalLevel.High)
        {
            if (!_cpuReadAddressSelected && !_cpuReadEepromSelected) return;
            CpuReadCount++;
            LastCpuReadAddress = _cpuSelectedAddress;
            LastCpuReadData = _cpuSelectedData;
            return;
        }

        if (!CpuData.TrySample(out var rawData)) return;
        var value = (byte)rawData;
        if (_cpuCycleLowRegisterSelected)
        {
            WriteMapperRegister(_cpuCycleAddress, value, BandaiFcgVariant.Fcg12);
            return;
        }

        if (_cpuCycleHighRomSelected && HighRegisterWindowEnabled)
            WriteMapperRegister(_cpuCycleAddress, value, BandaiFcgVariant.Lz93D50);
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
        var readSelected = address < 0x2000 && PpuReadBar.SampledLevel == DigitalLevel.Low;
        if (!readSelected)
        {
            PpuData.Release();
            _ppuReadActive = false;
            return;
        }

        var newRead = !_ppuReadActive || _ppuReadAddress != address;
        var value = ReadChr(address);
        PpuData.Drive(value);
        if (newRead) PpuReadCount++;
        _ppuReadAddress = address;
        _ppuReadActive = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DriveCiramOutputs(ushort address)
    {
        CiramChipEnableBar.Drive((address & 0x2000) != 0 ? DigitalLevel.Low : DigitalLevel.High);
        CiramA10.Drive(EvaluateCiramA10(address));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private DigitalLevel EvaluateCiramA10(ushort address) => _nametableMode switch
    {
        BandaiFcgNametableMode.Vertical => (address & 0x0400) != 0 ? DigitalLevel.High : DigitalLevel.Low,
        BandaiFcgNametableMode.Horizontal => (address & 0x0800) != 0 ? DigitalLevel.High : DigitalLevel.Low,
        BandaiFcgNametableMode.SingleScreenPage0 => DigitalLevel.Low,
        BandaiFcgNametableMode.SingleScreenPage1 => DigitalLevel.High,
        _ => DigitalLevel.Unknown
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RefreshCiramA10Physical()
    {
        if (!IsPowered() || !PpuAddress.TrySample(out var rawAddress)) return;
        CiramA10.Drive(EvaluateCiramA10((ushort)(rawAddress & 0x3FFF)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadPrg(ushort address)
    {
        var bank = address < 0xC000 ? SelectedPrgBank : FixedPrgBank;
        return _prg[(bank * PrgBankSize) + (address & 0x3FFF)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadChr(ushort address)
    {
        var slot = (address >> 10) & 0x07;
        var bank = _chrBankRegisters[slot] & _chrBankMask;
        return _chr[(bank * ChrBankSize) + (address & 0x03FF)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteMapperRegister(ushort address, byte value, BandaiFcgVariant semantics)
    {
        var register = address & 0x0F;
        if (register > 0x0D) return;

        switch (register)
        {
            case <= 0x07:
                _chrBankRegisters[register] = value;
                break;
            case 0x08:
                _prgBankRegister = (byte)(value & 0x0F);
                break;
            case 0x09:
                _nametableMode = (BandaiFcgNametableMode)(value & 0x03);
                RefreshCiramA10Physical();
                break;
            case 0x0A:
                SetIrqAsserted(false);
                if (semantics == BandaiFcgVariant.Lz93D50 && (value & 0x01) != 0)
                    _irqCounter = _irqLatch;
                _irqEnabled = (value & 0x01) != 0;
                break;
            case 0x0B:
                if (semantics == BandaiFcgVariant.Lz93D50)
                    _irqLatch = (ushort)((_irqLatch & 0xFF00) | value);
                else
                    _irqCounter = (ushort)((_irqCounter & 0xFF00) | value);
                break;
            case 0x0C:
                if (semantics == BandaiFcgVariant.Lz93D50)
                    _irqLatch = (ushort)((_irqLatch & 0x00FF) | (value << 8));
                else
                    _irqCounter = (ushort)((_irqCounter & 0x00FF) | (value << 8));
                break;
            case 0x0D:
                if (semantics == BandaiFcgVariant.Lz93D50)
                {
                    _eepromControl = value;
                    EepromControlWriteCount++;
                    _eeprom?.ApplyControl(value);
                }
                break;
        }

        MapperWriteCount++;
        LastMapperWriteAddress = address;
        LastMapperWriteData = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClockIrqCounter()
    {
        if (!_irqEnabled) return;
        IrqClockCount++;
        if (_irqCounter > 0) _irqCounter--;
        if (_irqCounter != 0) return;
        _irqEnabled = false;
        _irqCounter = 0xFFFF;
        SetIrqAsserted(true);
    }

    private void SetIrqAsserted(bool asserted)
    {
        if (_irqAsserted != asserted)
        {
            _irqAsserted = asserted;
            if (asserted) IrqAssertCount++;
        }

        if (asserted) IrqBar.Drive(DigitalLevel.Low);
        else IrqBar.Release();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadCpuRomCompiled(ushort address)
    {
        var value = ReadPrg(address);
        CpuReadCount++;
        LastCpuReadAddress = address;
        LastCpuReadData = value;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteCpuHighCompiled(ushort address, byte value)
    {
        if (HighRegisterWindowEnabled)
            WriteMapperRegister(address, value, BandaiFcgVariant.Lz93D50);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteCpuLowCompiled(ushort address, byte value)
    {
        if (LowRegisterWindowEnabled)
            WriteMapperRegister(address, value, BandaiFcgVariant.Fcg12);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadEepromCompiled(ushort address)
    {
        var value = EepromDataOutHigh ? (byte)1 : (byte)0;
        CpuReadCount++;
        LastCpuReadAddress = address;
        LastCpuReadData = EepromDataOutHigh ? (byte)0x10 : (byte)0;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ObserveCompiledCpuBusCycle(bool _) => ClockIrqCounter();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadPpuCompiled(ushort address)
    {
        PpuReadCount++;
        return ReadChr(address);
    }

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
            HighRegisterWindowEnabled
                ? new[]
                {
                    new CompiledPinCondition(CpuReadWrite, DigitalLevel.Low),
                    new CompiledPinCondition(CpuRomSelectBar, DigitalLevel.Low)
                }
                : Array.Empty<CompiledPinCondition>(),
            CompiledBusReadPhase.Complete,
            address => ReadCpuRomCompiled((ushort)(0x8000 | address)),
            HighRegisterWindowEnabled
                ? (address, value) => WriteCpuHighCompiled((ushort)(0x8000 | address), value)
                : null,
            ObserveCompiledCpuBusCycle,
            writePhase: CompiledBusWritePhase.Complete);

        if (LowRegisterWindowEnabled)
        {
            yield return new CompiledBusTargetDescriptor(
                this,
                CpuAddress.Pins,
                CpuData.Pins,
                Array.Empty<CompiledPinCondition>(),
                new[]
                {
                    new CompiledPinCondition(CpuReadWrite, DigitalLevel.Low),
                    new CompiledPinCondition(CpuM2, DigitalLevel.High),
                    new CompiledPinCondition(CpuRomSelectBar, DigitalLevel.High),
                    new CompiledPinCondition(CpuAddress.Pins[14], DigitalLevel.High),
                    new CompiledPinCondition(CpuAddress.Pins[13], DigitalLevel.High)
                },
                CompiledBusReadPhase.Complete,
                null,
                (address, value) => WriteCpuLowCompiled((ushort)address, value),
                writePhase: CompiledBusWritePhase.Complete);
        }

        if (EepromPortEnabled)
        {
            yield return new CompiledBusTargetDescriptor(
                this,
                CpuAddress.Pins,
                new[] { CpuData.Pins[4] },
                new[]
                {
                    new CompiledPinCondition(CpuReadWrite, DigitalLevel.High),
                    new CompiledPinCondition(CpuM2, DigitalLevel.High),
                    new CompiledPinCondition(CpuRomSelectBar, DigitalLevel.High),
                    new CompiledPinCondition(CpuAddress.Pins[14], DigitalLevel.High),
                    new CompiledPinCondition(CpuAddress.Pins[13], DigitalLevel.High)
                },
                Array.Empty<CompiledPinCondition>(),
                CompiledBusReadPhase.Complete,
                address => ReadEepromCompiled((ushort)address),
                null);
        }

        yield return new CompiledBusTargetDescriptor(
            this,
            PpuAddress.Pins,
            PpuData.Pins,
            new[]
            {
                new CompiledPinCondition(PpuAddress.Pins[13], DigitalLevel.Low),
                new CompiledPinCondition(PpuReadBar, DigitalLevel.Low)
            },
            Array.Empty<CompiledPinCondition>(),
            CompiledBusReadPhase.Complete,
            address => ReadPpuCompiled((ushort)address),
            null);
    }

    bool ICompiledStaticCombinationalComponent.TryEvaluateCompiledStaticOutput(
        DigitalPin output,
        Func<DigitalPin, DigitalLevel> sampleInput,
        out CompiledDriveState drive)
    {
        if (ReferenceEquals(output, CiramChipEnableBar))
        {
            drive = new CompiledDriveState(sampleInput(PpuAddress.Pins[13]) switch
            {
                DigitalLevel.Low => DigitalLevel.High,
                DigitalLevel.High => DigitalLevel.Low,
                _ => DigitalLevel.Unknown
            });
            return true;
        }

        drive = default;
        return false;
    }

    bool ICompiledCombinationalComponent.TryEvaluateCompiledOutput(
        DigitalPin output,
        Func<DigitalPin, DigitalLevel> sampleInput,
        out CompiledDriveState drive)
    {
        if (ReferenceEquals(output, CiramA10))
        {
            drive = _nametableMode switch
            {
                BandaiFcgNametableMode.Vertical => new CompiledDriveState(sampleInput(PpuAddress.Pins[10])),
                BandaiFcgNametableMode.Horizontal => new CompiledDriveState(sampleInput(PpuAddress.Pins[11])),
                BandaiFcgNametableMode.SingleScreenPage0 => new CompiledDriveState(DigitalLevel.Low),
                BandaiFcgNametableMode.SingleScreenPage1 => new CompiledDriveState(DigitalLevel.High),
                _ => new CompiledDriveState(DigitalLevel.Unknown)
            };
            return true;
        }

        if (ReferenceEquals(output, IrqBar))
        {
            drive = new CompiledDriveState(_irqAsserted ? DigitalLevel.Low : DigitalLevel.HighImpedance);
            return true;
        }

        return ((ICompiledStaticCombinationalComponent)this)
            .TryEvaluateCompiledStaticOutput(output, sampleInput, out drive);
    }

    /// <summary>
    /// Board-local 24C02 serial EEPROM. The ASIC exposes the I²C clock/data
    /// lines through register $x00D; the EEPROM itself remains a separate
    /// storage device electrically, represented here as the cartridge's
    /// internal board-local component rather than as CPU-addressable RAM.
    /// </summary>
    private sealed class Bandai24C02Eeprom
    {
        private enum ProtocolState
        {
            Idle,
            ReceiveControl,
            ReceiveAddress,
            ReceiveData,
            SendData,
            WaitMasterAck
        }

        private readonly byte[] _memory = Enumerable.Repeat((byte)0xFF, EepromSize).ToArray();
        private ProtocolState _state;
        private ProtocolState _afterAckState;
        private bool _scl;
        private bool _masterSda = true;
        private bool _driveLow;
        private bool _ackHolding;
        private bool _ackClockSeen;
        private bool _sendByteFinishPending;
        private bool _masterAckContinue;
        private byte _shift;
        private int _bitCount;
        private byte _address;
        private byte _sendByte;
        private int _sendBit;

        public bool DataLineHigh => !_driveLow;
        public ulong ReadCount { get; private set; }
        public ulong WriteCount { get; private set; }

        public byte Inspect(byte address) => _memory[address];

        public void ResetProtocol()
        {
            _state = ProtocolState.Idle;
            _afterAckState = ProtocolState.Idle;
            _scl = false;
            _masterSda = true;
            _driveLow = false;
            _ackHolding = false;
            _ackClockSeen = false;
            _sendByteFinishPending = false;
            _masterAckContinue = false;
            _shift = 0;
            _bitCount = 0;
            _sendByte = 0;
            _sendBit = 0;
            ReadCount = 0;
            WriteCount = 0;
        }

        public void ApplyControl(byte value)
        {
            var nextScl = (value & 0x20) != 0;
            var directionRead = (value & 0x80) != 0;
            var nextMasterSda = directionRead || (value & 0x40) != 0;

            if (_scl && nextScl)
            {
                if (_masterSda && !nextMasterSda)
                {
                    Start();
                    _masterSda = nextMasterSda;
                    return;
                }
                if (!_masterSda && nextMasterSda)
                {
                    Stop();
                    _masterSda = nextMasterSda;
                    return;
                }
            }

            if (!_scl && nextScl) OnRisingEdge(nextMasterSda);
            else if (_scl && !nextScl) OnFallingEdge();

            _scl = nextScl;
            _masterSda = nextMasterSda;
        }

        private void Start()
        {
            _state = ProtocolState.ReceiveControl;
            _afterAckState = ProtocolState.Idle;
            _driveLow = false;
            _ackHolding = false;
            _ackClockSeen = false;
            _sendByteFinishPending = false;
            _shift = 0;
            _bitCount = 0;
        }

        private void Stop()
        {
            _state = ProtocolState.Idle;
            _driveLow = false;
            _ackHolding = false;
            _ackClockSeen = false;
            _sendByteFinishPending = false;
            _bitCount = 0;
        }

        private void OnRisingEdge(bool masterSda)
        {
            if (_ackHolding)
            {
                _ackClockSeen = true;
                return;
            }

            switch (_state)
            {
                case ProtocolState.ReceiveControl:
                case ProtocolState.ReceiveAddress:
                case ProtocolState.ReceiveData:
                    _shift = (byte)((_shift << 1) | (masterSda ? 1 : 0));
                    _bitCount++;
                    if (_bitCount == 8) ReceiveByte(_shift);
                    break;

                case ProtocolState.SendData:
                    _sendBit++;
                    if (_sendBit == 8) _sendByteFinishPending = true;
                    break;

                case ProtocolState.WaitMasterAck:
                    _masterAckContinue = !masterSda;
                    break;
            }
        }

        private void OnFallingEdge()
        {
            if (_ackHolding)
            {
                if (!_ackClockSeen) return;
                _ackHolding = false;
                _ackClockSeen = false;
                _driveLow = false;
                _state = _afterAckState;
                if (_state == ProtocolState.SendData) BeginSendByte();
                return;
            }

            if (_state == ProtocolState.SendData)
            {
                if (_sendByteFinishPending)
                {
                    _sendByteFinishPending = false;
                    _driveLow = false;
                    _state = ProtocolState.WaitMasterAck;
                    return;
                }
                DriveCurrentSendBit();
                return;
            }

            if (_state == ProtocolState.WaitMasterAck)
            {
                if (_masterAckContinue)
                {
                    _address++;
                    _state = ProtocolState.SendData;
                    BeginSendByte();
                }
                else
                {
                    _state = ProtocolState.Idle;
                    _driveLow = false;
                }
                _masterAckContinue = false;
            }
        }

        private void ReceiveByte(byte value)
        {
            _shift = 0;
            _bitCount = 0;

            switch (_state)
            {
                case ProtocolState.ReceiveControl:
                    if ((value & 0xFE) != 0xA0)
                    {
                        _state = ProtocolState.Idle;
                        _driveLow = false;
                        return;
                    }
                    BeginAck((value & 0x01) != 0 ? ProtocolState.SendData : ProtocolState.ReceiveAddress);
                    break;

                case ProtocolState.ReceiveAddress:
                    _address = value;
                    BeginAck(ProtocolState.ReceiveData);
                    break;

                case ProtocolState.ReceiveData:
                    _memory[_address] = value;
                    _address++;
                    WriteCount++;
                    BeginAck(ProtocolState.ReceiveData);
                    break;
            }
        }

        private void BeginAck(ProtocolState afterAck)
        {
            _afterAckState = afterAck;
            _ackHolding = true;
            _ackClockSeen = false;
            _driveLow = true;
        }

        private void BeginSendByte()
        {
            _sendByte = _memory[_address];
            ReadCount++;
            _sendBit = 0;
            _sendByteFinishPending = false;
            DriveCurrentSendBit();
        }

        private void DriveCurrentSendBit()
        {
            var mask = 0x80 >> _sendBit;
            _driveLow = (_sendByte & mask) == 0;
        }
    }
}
