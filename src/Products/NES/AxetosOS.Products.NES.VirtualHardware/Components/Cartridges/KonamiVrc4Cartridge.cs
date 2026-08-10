using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

public enum KonamiVrc4Variant : byte
{
    Vrc4A,
    Vrc4B,
    Vrc4C,
    Vrc4D,
    Vrc4E,
    Vrc4F,
    LegacyMapper21,
    LegacyMapper23,
    LegacyMapper25
}

public enum KonamiVrcNametableMode : byte
{
    Vertical = 0,
    Horizontal = 1,
    SingleScreenPage0 = 2,
    SingleScreenPage1 = 3
}

/// <summary>
/// Konami VRC4-family replaceable cartridge hardware for iNES mappers
/// 21/23/25. The package owns its physically variant A0/A1 register decode,
/// two switchable 8 KiB PRG outputs with the VRC4 PRG swap path, eight 1 KiB
/// CHR outputs assembled from independent low/high nibbles, CIRAM routing,
/// optional work RAM and the reusable Konami VRC IRQ divider/counter.
/// Legacy iNES images have no submapper wiring metadata, so only the register
/// address-line decode uses the documented VRC4 compatibility ORing; NES 2.0
/// submappers select one exact VRC4 package wiring.
/// </summary>
public sealed class KonamiVrc4Cartridge : VirtualHardwareComponent, IReplaceableCartridgeHardware,
    ICompiledBusTargetProvider, ICompiledCombinationalComponent, ICompiledStaticCombinationalComponent
{
    private const int PrgBankSize = 8 * 1024;
    private const int ChrBankSize = 1 * 1024;
    private const int WorkRamWindowSize = 8 * 1024;
    private const int MaximumPrgBanks = 32;
    private const int MaximumChrBanks = 512;

    private byte[] _prg = [];
    private byte[] _chr = [];
    private byte[] _workRam = [];
    private readonly byte[] _prgBankRegisters = new byte[2];
    private readonly byte[] _chrLowRegisters = new byte[8];
    private readonly byte[] _chrHighRegisters = new byte[8];
    private readonly int[] _prgWindowBanks = new int[4];
    private readonly int[] _chrWindowBanks = new int[8];
    private int _prgBankMask;
    private int _chrBankMask;
    private bool _prgMode;
    private KonamiVrcNametableMode _nametableMode;
    private KonamiVrcNametableMode _powerOnNametableMode;
    private int _mapperNumber;
    private KonamiVrc4Variant _variant;

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

    public KonamiVrc4Cartridge(string componentId) : base(componentId)
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
    public KonamiVrc4Variant Variant => _variant;
    public bool UsesLegacyAddressDecode => _variant is KonamiVrc4Variant.LegacyMapper21 or KonamiVrc4Variant.LegacyMapper23 or KonamiVrc4Variant.LegacyMapper25;
    public IReadOnlyList<byte> PrgBankRegisters => _prgBankRegisters;
    public IReadOnlyList<byte> ChrLowRegisters => _chrLowRegisters;
    public IReadOnlyList<byte> ChrHighRegisters => _chrHighRegisters;
    public IReadOnlyList<int> PrgWindowBanks => _prgWindowBanks;
    public IReadOnlyList<int> ChrWindowBanks => _chrWindowBanks;
    public bool PrgMode => _prgMode;
    public KonamiVrcNametableMode NametableMode => _nametableMode;
    public int WorkRamSizeBytes => _workRam.Length;
    public KonamiVrcIrqCounter Irq { get; }

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

    public void LoadImage(VirtualHardwareNesRomImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.MapperNumber is not (21 or 23 or 25))
            throw new NotSupportedException($"Mapper {image.MapperNumber} is not VRC4-family hardware modeled by this package.");
        if (image.Mirroring == VirtualHardwareNesMirroring.FourScreen)
            throw new NotSupportedException("Konami VRC4 boards modeled here route CIRAM through the mapper and do not use four-screen cartridge nametable RAM.");
        if (image.ChrRom.Length == 0)
            throw new NotSupportedException("VRC4-family boards modeled here use banked CHR ROM; CHR-RAM-only variants require distinct physical verification.");
        if (image.TotalChrRamSizeBytes != 0 && image.HasExplicitRamSizes)
            throw new NotSupportedException("Mixed CHR ROM/RAM VRC4 boards are not modeled by this package.");

        if (image.PrgRom.Length < 4 * PrgBankSize || image.PrgRom.Length % PrgBankSize != 0)
            throw new ArgumentException("VRC4 PRG ROM must contain at least four whole 8 KiB banks.", nameof(image));
        var prgBanks = image.PrgRom.Length / PrgBankSize;
        if (prgBanks > MaximumPrgBanks || !IsPowerOfTwo(prgBanks))
            throw new NotSupportedException($"VRC4 PRG ROM must expose a power-of-two count of at most {MaximumPrgBanks} 8 KiB banks.");

        if (image.ChrRom.Length < 8 * ChrBankSize || image.ChrRom.Length % ChrBankSize != 0)
            throw new ArgumentException("VRC4 CHR ROM must contain at least eight whole 1 KiB banks.", nameof(image));
        var chrBanks = image.ChrRom.Length / ChrBankSize;
        if (chrBanks > MaximumChrBanks || !IsPowerOfTwo(chrBanks))
            throw new NotSupportedException($"VRC4 CHR ROM must expose a power-of-two count of at most {MaximumChrBanks} 1 KiB banks.");

        var workRamSize = ResolveWorkRamSize(image);
        if (workRamSize != 0 && workRamSize != WorkRamWindowSize)
            throw new NotSupportedException("The VRC4 package currently supports the physically common single 8 KiB work-RAM window.");

        _mapperNumber = image.MapperNumber;
        _variant = ResolveVariant(image);
        _prg = image.PrgRom.ToArray();
        _chr = image.ChrRom.ToArray();
        _workRam = workRamSize == 0 ? [] : new byte[workRamSize];
        _prgBankMask = prgBanks - 1;
        _chrBankMask = chrBanks - 1;
        _powerOnNametableMode = image.Mirroring == VirtualHardwareNesMirroring.Vertical
            ? KonamiVrcNametableMode.Vertical
            : KonamiVrcNametableMode.Horizontal;
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

    public ushort GetChrRegister(int slot)
    {
        if ((uint)slot >= 8u) throw new ArgumentOutOfRangeException(nameof(slot));
        return (ushort)(_chrLowRegisters[slot] | (_chrHighRegisters[slot] << 4));
    }

    public ushort TranslateMapperAddress(ushort address)
    {
        var (a0, a1) = ResolveRegisterSelectBits(address);
        return (ushort)((address & 0xFF00) | (a1 << 1) | a0);
    }

    private void ApplyResetState()
    {
        Array.Clear(_prgBankRegisters);
        Array.Clear(_chrLowRegisters);
        Array.Clear(_chrHighRegisters);
        Array.Clear(_prgWindowBanks);
        Array.Clear(_chrWindowBanks);

        if (IsInserted)
        {
            var prgBanks = _prg.Length / PrgBankSize;
            _prgBankRegisters[0] = 0;
            _prgBankRegisters[1] = 1;
            _prgWindowBanks[0] = 0;
            _prgWindowBanks[1] = Math.Min(1, prgBanks - 1);
            _prgWindowBanks[2] = Math.Max(0, prgBanks - 2);
            _prgWindowBanks[3] = Math.Max(0, prgBanks - 1);

            var chrBanks = _chr.Length / ChrBankSize;
            for (var slot = 0; slot < 8; slot++)
            {
                _chrLowRegisters[slot] = (byte)(slot & 0x0F);
                _chrHighRegisters[slot] = 0;
                _chrWindowBanks[slot] = Math.Min(slot, chrBanks - 1);
            }
        }

        _prgMode = false;
        _nametableMode = _powerOnNametableMode;
        _cpuReadAddressSelected = false;
        _cpuSelectedFromWorkRam = false;
        _cpuSelectedAddress = 0;
        _cpuSelectedData = 0;
        _cpuCycleHighRomSelected = false;
        _cpuCycleWorkRamSelected = false;
        _cpuCycleAddress = 0;
        _ppuReadActive = false;
        _ppuReadAddress = 0;
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
        Irq.Reset();
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
            && _workRam.Length != 0
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
        var readSelected = address < 0x2000 && PpuReadBar.SampledLevel == DigitalLevel.Low;
        if (!readSelected)
        {
            PpuData.Release();
            _ppuReadActive = false;
            return;
        }

        var newRead = !_ppuReadActive || _ppuReadAddress != address;
        PpuData.Drive(ReadChr(address));
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
        KonamiVrcNametableMode.Vertical => (address & 0x0400) != 0 ? DigitalLevel.High : DigitalLevel.Low,
        KonamiVrcNametableMode.Horizontal => (address & 0x0800) != 0 ? DigitalLevel.High : DigitalLevel.Low,
        KonamiVrcNametableMode.SingleScreenPage0 => DigitalLevel.Low,
        KonamiVrcNametableMode.SingleScreenPage1 => DigitalLevel.High,
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
        var slot = (address - 0x8000) >> 13;
        var bank = _prgWindowBanks[slot];
        return _prg[(bank * PrgBankSize) + (address & 0x1FFF)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadWorkRam(ushort address) => _workRam[address & 0x1FFF];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadChr(ushort address)
    {
        var slot = address >> 10;
        var bank = _chrWindowBanks[slot];
        return _chr[(bank * ChrBankSize) + (address & 0x03FF)];
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
        var register = translated & 0xF003;

        if (register is >= 0x8000 and <= 0x8003)
        {
            _prgBankRegisters[0] = (byte)(value & 0x1F);
            RefreshPrgWindows();
            return;
        }

        if (register is >= 0x9000 and <= 0x9001)
        {
            _nametableMode = (KonamiVrcNametableMode)(value & 0x03);
            RefreshCiramA10Physical();
            return;
        }

        if (register is >= 0x9002 and <= 0x9003)
        {
            _prgMode = (value & 0x02) != 0;
            RefreshPrgWindows();
            return;
        }

        if (register is >= 0xA000 and <= 0xA003)
        {
            _prgBankRegisters[1] = (byte)(value & 0x1F);
            RefreshPrgWindows();
            return;
        }

        if (register is >= 0xB000 and <= 0xE003)
        {
            var registerPair = ((register >> 12) - 0x0B) * 2;
            var slot = registerPair + ((register >> 1) & 0x01);
            if ((register & 0x01) == 0)
                _chrLowRegisters[slot] = (byte)(value & 0x0F);
            else
                _chrHighRegisters[slot] = (byte)(value & 0x1F);
            RefreshChrWindow(slot);
            return;
        }

        switch (register)
        {
            case 0xF000:
                Irq.SetReloadNibble(value, highNibble: false);
                break;
            case 0xF001:
                Irq.SetReloadNibble(value, highNibble: true);
                break;
            case 0xF002:
                Irq.SetControl(value);
                RefreshIrqPhysical();
                break;
            case 0xF003:
                Irq.Acknowledge();
                RefreshIrqPhysical();
                break;
        }
    }

    private void RefreshPrgWindows()
    {
        var fixedSecondLast = Math.Max(0, (_prg.Length / PrgBankSize) - 2);
        var fixedLast = Math.Max(0, (_prg.Length / PrgBankSize) - 1);
        var bank0 = _prgBankRegisters[0] & _prgBankMask;
        var bank1 = _prgBankRegisters[1] & _prgBankMask;
        if (!_prgMode)
        {
            _prgWindowBanks[0] = bank0;
            _prgWindowBanks[1] = bank1;
            _prgWindowBanks[2] = fixedSecondLast;
        }
        else
        {
            _prgWindowBanks[0] = fixedSecondLast;
            _prgWindowBanks[1] = bank1;
            _prgWindowBanks[2] = bank0;
        }
        _prgWindowBanks[3] = fixedLast;
    }

    private void RefreshChrWindow(int slot)
    {
        var page = _chrLowRegisters[slot] | (_chrHighRegisters[slot] << 4);
        _chrWindowBanks[slot] = page & _chrBankMask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClockCpuCycle()
    {
        var wasAsserted = Irq.Asserted;
        Irq.ClockCpuCycle();
        if (Irq.Asserted != wasAsserted) RefreshIrqPhysical();
    }

    private void RefreshIrqPhysical()
    {
        if (Irq.Asserted) IrqBar.Drive(DigitalLevel.Low);
        else IrqBar.Release();
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
                writePhase: CompiledBusWritePhase.Complete);
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
                KonamiVrcNametableMode.Vertical => new CompiledDriveState(sampleInput(PpuAddress.Pins[10])),
                KonamiVrcNametableMode.Horizontal => new CompiledDriveState(sampleInput(PpuAddress.Pins[11])),
                KonamiVrcNametableMode.SingleScreenPage0 => new CompiledDriveState(DigitalLevel.Low),
                KonamiVrcNametableMode.SingleScreenPage1 => new CompiledDriveState(DigitalLevel.High),
                _ => new CompiledDriveState(DigitalLevel.Unknown)
            };
            return true;
        }

        if (ReferenceEquals(output, IrqBar))
        {
            drive = new CompiledDriveState(Irq.Asserted ? DigitalLevel.Low : DigitalLevel.HighImpedance);
            return true;
        }

        return ((ICompiledStaticCombinationalComponent)this)
            .TryEvaluateCompiledStaticOutput(output, sampleInput, out drive);
    }

    private (int A0, int A1) ResolveRegisterSelectBits(ushort address)
    {
        return _variant switch
        {
            KonamiVrc4Variant.Vrc4A => ((address >> 1) & 1, (address >> 2) & 1),
            KonamiVrc4Variant.Vrc4B => ((address >> 1) & 1, address & 1),
            KonamiVrc4Variant.Vrc4C => ((address >> 6) & 1, (address >> 7) & 1),
            KonamiVrc4Variant.Vrc4D => ((address >> 3) & 1, (address >> 2) & 1),
            KonamiVrc4Variant.Vrc4E => ((address >> 2) & 1, (address >> 3) & 1),
            KonamiVrc4Variant.Vrc4F => (address & 1, (address >> 1) & 1),
            KonamiVrc4Variant.LegacyMapper21 => (((address >> 1) | (address >> 6)) & 1, ((address >> 2) | (address >> 7)) & 1),
            KonamiVrc4Variant.LegacyMapper23 => ((address | (address >> 2)) & 1, ((address >> 1) | (address >> 3)) & 1),
            KonamiVrc4Variant.LegacyMapper25 => (((address >> 1) | (address >> 3)) & 1, (address | (address >> 2)) & 1),
            _ => (0, 0)
        };
    }

    private static KonamiVrc4Variant ResolveVariant(VirtualHardwareNesRomImage image)
    {
        if (image.HeaderFormat == VirtualHardwareNesHeaderFormat.INes || image.SubmapperNumber is null or 0)
        {
            return image.MapperNumber switch
            {
                21 => KonamiVrc4Variant.LegacyMapper21,
                23 => KonamiVrc4Variant.LegacyMapper23,
                25 => KonamiVrc4Variant.LegacyMapper25,
                _ => throw new NotSupportedException($"Mapper {image.MapperNumber} is not a VRC4 mapper number.")
            };
        }

        return (image.MapperNumber, image.SubmapperNumber.Value) switch
        {
            (21, 1) => KonamiVrc4Variant.Vrc4A,
            (21, 2) => KonamiVrc4Variant.Vrc4C,
            (23, 1) => KonamiVrc4Variant.Vrc4F,
            (23, 2) => KonamiVrc4Variant.Vrc4E,
            (25, 1) => KonamiVrc4Variant.Vrc4B,
            (25, 2) => KonamiVrc4Variant.Vrc4D,
            (23, 3) => throw new NotSupportedException("Mapper 23 submapper 3 identifies VRC2b hardware, not the VRC4 package modeled here."),
            (25, 3) => throw new NotSupportedException("Mapper 25 submapper 3 identifies VRC2c hardware, not the VRC4 package modeled here."),
            _ => throw new NotSupportedException($"Mapper {image.MapperNumber} submapper {image.SubmapperNumber} is not a defined VRC4 package wiring.")
        };
    }

    private static int ResolveWorkRamSize(VirtualHardwareNesRomImage image)
    {
        if (image.HasExplicitRamSizes) return image.TotalPrgRamSizeBytes;
        // Legacy iNES has only the conventional 8 KiB PRG-RAM field. Preserve
        // that compatibility assumption at cartridge composition time; NES 2.0
        // zero remains physically absent.
        return image.TotalPrgRamSizeBytes > 0 ? WorkRamWindowSize : 0;
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
}
