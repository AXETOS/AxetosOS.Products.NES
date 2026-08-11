using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

/// <summary>
/// Namcot/Namco 163 replaceable cartridge ASIC for iNES/NES 2.0 Mapper 19.
/// The package owns three switchable 8 KiB PRG outputs, twelve 1 KiB PPU bank
/// registers that can independently select CHR ROM or either CIRAM page,
/// four 2 KiB work-RAM protection gates, the 15-bit CPU-cycle IRQ counter and
/// chip-local 128-byte multiplexed wavetable audio circuitry.
/// </summary>
public sealed class Namco163Cartridge : VirtualHardwareComponent, IReplaceableCartridgeHardware,
    ICompiledBusTargetProvider, ICompiledCombinationalComponent, ICompiledBusAddressCombinationalComponent
{
    private const int PrgBankSize = 8 * 1024;
    private const int ChrBankSize = 1024;
    private const int WorkRamSize = 8 * 1024;
    private const int WorkRamBlockSize = 2 * 1024;
    private const int MaximumPrgBanks = 64;
    private const int MaximumChrBanks = 256;

    private enum CpuReadSource : byte
    {
        None,
        PrgRom,
        WorkRam,
        LowRegister
    }

    private byte[] _prg = [];
    private byte[] _chr = [];
    private byte[] _workRam = [];
    private readonly byte[] _prgRegisters = new byte[3];
    private readonly byte[] _ppuBankRegisters = new byte[12];
    private readonly int[] _prgWindowBanks = new int[4];
    private int _prgBankMask;
    private int _chrBankMask;
    private byte _writeProtectRegister;
    private bool _lowChrCiramDisable;
    private bool _highChrCiramDisable;
    private ushort _irqCounter;
    private bool _irqAsserted;

    private bool _cpuCycleHighRomSelected;
    private bool _cpuCycleWorkRamSelected;
    private bool _cpuCycleLowRegisterSelected;
    private ushort _cpuCycleAddress;
    private CpuReadSource _cpuReadSource;
    private ushort _cpuSelectedAddress;
    private byte _cpuSelectedData;
    private bool _ppuReadActive;
    private ushort _ppuReadAddress;
    private bool _compiledPendingAudioReadCompletion;

    private readonly ulong _powerInputMask;
    private readonly ulong _cpuAddressControlInputMask;
    private readonly ulong _cpuM2InputMask;
    private readonly ulong _cpuRomSelectInputMask;
    private readonly ulong _ppuAddressControlInputMask;

    public Namco163Cartridge(string componentId) : base(componentId)
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

        Audio = new Namco163Audio();

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

    public int MapperNumber => IsInserted ? 19 : 0;
    public bool IsInserted { get; private set; }
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

    public Namco163Audio Audio { get; }
    public IReadOnlyList<byte> PrgBankRegisters => _prgRegisters;
    public IReadOnlyList<byte> PpuBankRegisters => _ppuBankRegisters;
    public IReadOnlyList<int> PrgWindowBanks => _prgWindowBanks;
    public byte WriteProtectRegister => _writeProtectRegister;
    public bool LowChrCiramDisabled => _lowChrCiramDisable;
    public bool HighChrCiramDisabled => _highChrCiramDisable;
    public int WorkRamSizeBytes => _workRam.Length;
    public ushort IrqCounter => (ushort)(_irqCounter & 0x7FFF);
    public bool IrqEnabled => (_irqCounter & 0x8000) != 0;
    public bool IrqAsserted => _irqAsserted;

    public ulong MapperWriteCount { get; private set; }
    public ushort LastMapperWriteAddress { get; private set; }
    public byte LastMapperWriteData { get; private set; }
    public ulong CpuCycleClockCount { get; private set; }
    public ulong IrqClockCount { get; private set; }
    public ulong IrqAssertCount { get; private set; }
    public ulong CpuReadCount { get; private set; }
    public ushort LastCpuReadAddress { get; private set; }
    public byte LastCpuReadData { get; private set; }
    public ulong WorkRamReadCount { get; private set; }
    public ulong WorkRamWriteCount { get; private set; }
    public ulong BlockedWorkRamWriteCount { get; private set; }
    public ulong LowRegisterReadCount { get; private set; }
    public ulong PpuReadCount { get; private set; }
    public ulong PpuWriteCount { get; private set; }
    public ulong ChrReadCount { get; private set; }
    public ulong ChrNametableReadCount { get; private set; }

    public void LoadImage(VirtualHardwareNesRomImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.MapperNumber != 19)
            throw new NotSupportedException($"Mapper {image.MapperNumber} is not Namco 163 hardware modeled by this package.");
        if (image.Mirroring == VirtualHardwareNesMirroring.FourScreen)
            throw new NotSupportedException("Namco 163 owns nametable routing through its twelve PPU bank registers; four-screen cartridge RAM requires distinct board topology.");
        if (image.ChrRom.Length == 0)
            throw new NotSupportedException("Namco 163 boards modeled here require banked CHR ROM.");
        if (image.TotalChrRamSizeBytes != 0 && image.HasExplicitRamSizes)
            throw new NotSupportedException("Mixed Namco 163 CHR ROM/RAM boards require distinct physical verification.");

        if (image.PrgRom.Length < 4 * PrgBankSize || image.PrgRom.Length % PrgBankSize != 0)
            throw new ArgumentException("Namco 163 PRG ROM must contain at least four whole 8 KiB banks.", nameof(image));
        var prgBanks = image.PrgRom.Length / PrgBankSize;
        if (prgBanks > MaximumPrgBanks || !IsPowerOfTwo(prgBanks))
            throw new NotSupportedException($"Namco 163 PRG ROM must expose a power-of-two count of at most {MaximumPrgBanks} 8 KiB banks.");

        if (image.ChrRom.Length < 8 * ChrBankSize || image.ChrRom.Length % ChrBankSize != 0)
            throw new ArgumentException("Namco 163 CHR ROM must contain at least eight whole 1 KiB banks.", nameof(image));
        var chrBanks = image.ChrRom.Length / ChrBankSize;
        if (chrBanks > MaximumChrBanks || !IsPowerOfTwo(chrBanks))
            throw new NotSupportedException($"Namco 163 CHR ROM must expose a power-of-two count of at most {MaximumChrBanks} 1 KiB banks.");

        var workRamSize = ResolveWorkRamSize(image);
        if (workRamSize is not (0 or WorkRamSize))
            throw new NotSupportedException("Namco 163 supports the physically common single 8 KiB PRG RAM/NVRAM array split into four 2 KiB protection blocks.");

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
        LastMapperWriteData = 0;
        CpuCycleClockCount = 0;
        IrqClockCount = 0;
        IrqAssertCount = 0;
        CpuReadCount = 0;
        LastCpuReadAddress = 0;
        LastCpuReadData = 0;
        WorkRamReadCount = 0;
        WorkRamWriteCount = 0;
        BlockedWorkRamWriteCount = 0;
        LowRegisterReadCount = 0;
        PpuReadCount = 0;
        PpuWriteCount = 0;
        ChrReadCount = 0;
        ChrNametableReadCount = 0;
    }

    private void ApplyResetState()
    {
        Array.Clear(_prgRegisters);
        Array.Clear(_ppuBankRegisters);
        Array.Clear(_prgWindowBanks);
        _writeProtectRegister = 0;
        _lowChrCiramDisable = false;
        _highChrCiramDisable = false;
        _irqCounter = 0;
        _irqAsserted = false;

        if (_prg.Length != 0)
        {
            _prgWindowBanks[0] = 0;
            _prgWindowBanks[1] = 0;
            _prgWindowBanks[2] = 0;
            _prgWindowBanks[3] = _prgBankMask;
        }
        for (var slot = 0; slot < 12; slot++)
            _ppuBankRegisters[slot] = slot < 8 ? (byte)slot : (byte)(0xE0 | (slot & 1));

        _cpuCycleHighRomSelected = false;
        _cpuCycleWorkRamSelected = false;
        _cpuCycleLowRegisterSelected = false;
        _cpuCycleAddress = 0;
        _cpuReadSource = CpuReadSource.None;
        _cpuSelectedAddress = 0;
        _cpuSelectedData = 0;
        _ppuReadActive = false;
        _ppuReadAddress = 0;
        _compiledPendingAudioReadCompletion = false;
        Audio.Reset();
        ResetDiagnostics();
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
            _cpuReadSource = CpuReadSource.None;
            _cpuCycleHighRomSelected = false;
            _cpuCycleWorkRamSelected = false;
            _cpuCycleLowRegisterSelected = false;
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
            _cpuReadSource = CpuReadSource.None;
            _cpuCycleHighRomSelected = false;
            _cpuCycleWorkRamSelected = false;
            _cpuCycleLowRegisterSelected = false;
            CpuData.Release();
            return;
        }

        var address = (ushort)(rawAddress & 0x7FFF);
        var m2High = CpuM2.SampledLevel == DigitalLevel.High;
        var highRomSelected = m2High && CpuRomSelectBar.SampledLevel == DigitalLevel.Low;
        var workRamSelected = m2High && CpuRomSelectBar.SampledLevel == DigitalLevel.High
            && _workRam.Length != 0 && (address & 0x6000) == 0x6000;
        var lowRegisterSelected = m2High && CpuRomSelectBar.SampledLevel == DigitalLevel.High
            && IsLowRegisterAddress(address);

        _cpuCycleHighRomSelected = highRomSelected;
        _cpuCycleWorkRamSelected = workRamSelected;
        _cpuCycleLowRegisterSelected = lowRegisterSelected;
        _cpuCycleAddress = highRomSelected ? (ushort)(0x8000 | address) : address;

        CpuData.Release();
        _cpuReadSource = CpuReadSource.None;
        if (CpuReadWrite.SampledLevel != DigitalLevel.High) return;

        if (highRomSelected)
        {
            SelectCpuRead(_cpuCycleAddress, ReadPrg(_cpuCycleAddress), CpuReadSource.PrgRom);
            return;
        }

        if (workRamSelected)
        {
            SelectCpuRead(address, ReadWorkRam(address), CpuReadSource.WorkRam);
            return;
        }

        if (lowRegisterSelected)
            SelectCpuRead(address, PeekLowRegister(address), CpuReadSource.LowRegister);
    }

    private void SelectCpuRead(ushort address, byte value, CpuReadSource source)
    {
        _cpuReadSource = source;
        _cpuSelectedAddress = address;
        _cpuSelectedData = value;
        CpuData.Drive(value);
    }

    private void CompleteCpuTransaction()
    {
        if (CpuReadWrite.SampledLevel == DigitalLevel.High)
        {
            if (_cpuReadSource == CpuReadSource.None) return;
            CpuReadCount++;
            LastCpuReadAddress = _cpuSelectedAddress;
            LastCpuReadData = _cpuSelectedData;
            if (_cpuReadSource == CpuReadSource.WorkRam) WorkRamReadCount++;
            else if (_cpuReadSource == CpuReadSource.LowRegister)
            {
                LowRegisterReadCount++;
                if ((_cpuSelectedAddress & 0xF800) == 0x4800) Audio.CompletePeekedRead();
            }
            return;
        }

        if (!CpuData.TrySample(out var rawData)) return;
        var value = (byte)rawData;
        if (_cpuCycleHighRomSelected)
        {
            WriteMapper(_cpuCycleAddress, value);
            return;
        }
        if (_cpuCycleLowRegisterSelected)
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
        var source = ResolvePpuSource(address);
        DriveCiramOutputs(source);

        var readSelected = PpuReadBar.SampledLevel == DigitalLevel.Low && source.UsesChrRom;
        if (!readSelected)
        {
            PpuData.Release();
            _ppuReadActive = false;
            return;
        }

        var newRead = !_ppuReadActive || _ppuReadAddress != address;
        PpuData.Drive(ReadPpuChr(address, source.Bank));
        if (newRead)
        {
            PpuReadCount++;
            ChrReadCount++;
            if (address >= 0x2000) ChrNametableReadCount++;
        }
        _ppuReadAddress = address;
        _ppuReadActive = true;
    }

    private readonly record struct PpuSource(bool UsesChrRom, int Bank, int CiramPage);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PpuSource ResolvePpuSource(ushort address)
    {
        if (address >= 0x3F00) return new PpuSource(false, 0, -1);

        var slot = address < 0x2000 ? address >> 10 : 8 + ((address >> 10) & 0x03);
        var value = _ppuBankRegisters[slot];
        var canMapCiram = slot switch
        {
            < 4 => !_lowChrCiramDisable,
            < 8 => !_highChrCiramDisable,
            _ => true
        };
        if (canMapCiram && value >= 0xE0)
            return new PpuSource(false, 0, value & 1);
        return new PpuSource(true, value & _chrBankMask, -1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DriveCiramOutputs(PpuSource source)
    {
        if (source.CiramPage >= 0)
        {
            CiramChipEnableBar.Drive(DigitalLevel.Low);
            CiramA10.Drive(source.CiramPage != 0 ? DigitalLevel.High : DigitalLevel.Low);
            return;
        }
        CiramChipEnableBar.Drive(DigitalLevel.High);
        CiramA10.Drive(DigitalLevel.Unknown);
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
    private byte ReadPpuChr(ushort address, int bank) => _chr[(bank * ChrBankSize) + (address & 0x03FF)];

    private byte PeekLowRegister(ushort address) => (address & 0xF800) switch
    {
        0x4800 => Audio.PeekData(),
        0x5000 => (byte)_irqCounter,
        0x5800 => (byte)(_irqCounter >> 8),
        _ => 0
    };

    private byte ReadLowRegisterCompiled(ushort address)
    {
        LowRegisterReadCount++;
        var audioDataRead = (address & 0xF800) == 0x4800;
        var value = PeekLowRegister(address);
        if (audioDataRead) _compiledPendingAudioReadCompletion = true;
        RecordCpuRead(address, value);
        return value;
    }

    private void WriteWorkRam(ushort address, byte value)
    {
        if (!IsWorkRamWriteEnabled(address))
        {
            BlockedWorkRamWriteCount++;
            return;
        }
        _workRam[address & 0x1FFF] = value;
        WorkRamWriteCount++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsWorkRamWriteEnabled(ushort address)
    {
        if (_workRam.Length == 0 || (_writeProtectRegister & 0x40) == 0) return false;
        var block = (address >> 11) & 0x03;
        return (_writeProtectRegister & (1 << block)) == 0;
    }

    private void WriteMapper(ushort address, byte value)
    {
        MapperWriteCount++;
        LastMapperWriteAddress = address;
        LastMapperWriteData = value;

        switch (address & 0xF800)
        {
            case 0x4800:
                Audio.WriteData(value);
                return;
            case 0x5000:
                _irqCounter = (ushort)((_irqCounter & 0xFF00) | value);
                ClearIrq();
                return;
            case 0x5800:
                _irqCounter = (ushort)((_irqCounter & 0x00FF) | (value << 8));
                ClearIrq();
                return;
            case 0x8000:
            case 0x8800:
            case 0x9000:
            case 0x9800:
            {
                var slot = (address - 0x8000) >> 11;
                _ppuBankRegisters[slot] = value;
                RefreshCiramPhysical();
                return;
            }
            case 0xA000:
            case 0xA800:
            case 0xB000:
            case 0xB800:
            {
                var slot = 4 + ((address - 0xA000) >> 11);
                _ppuBankRegisters[slot] = value;
                RefreshCiramPhysical();
                return;
            }
            case 0xC000:
            case 0xC800:
            case 0xD000:
            case 0xD800:
            {
                var slot = 8 + ((address - 0xC000) >> 11);
                _ppuBankRegisters[slot] = value;
                RefreshCiramPhysical();
                return;
            }
            case 0xE000:
                _prgRegisters[0] = (byte)(value & 0x3F);
                _prgWindowBanks[0] = _prgRegisters[0] & _prgBankMask;
                Audio.SetSoundDisabled((value & 0x40) != 0);
                return;
            case 0xE800:
                _prgRegisters[1] = (byte)(value & 0x3F);
                _prgWindowBanks[1] = _prgRegisters[1] & _prgBankMask;
                _lowChrCiramDisable = (value & 0x40) != 0;
                _highChrCiramDisable = (value & 0x80) != 0;
                RefreshCiramPhysical();
                return;
            case 0xF000:
                _prgRegisters[2] = (byte)(value & 0x3F);
                _prgWindowBanks[2] = _prgRegisters[2] & _prgBankMask;
                return;
            case 0xF800:
                _writeProtectRegister = value;
                Audio.SetAddressRegister(value);
                return;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClockCpuCycle()
    {
        CpuCycleClockCount++;
        if (IrqEnabled && IrqCounter != 0x7FFF)
        {
            _irqCounter++;
            IrqClockCount++;
            if (IrqCounter == 0x7FFF)
            {
                _irqAsserted = true;
                IrqAssertCount++;
                RefreshIrqPhysical();
            }
        }
        Audio.ClockCpuCycle();
    }

    private void ClearIrq()
    {
        if (!_irqAsserted) return;
        _irqAsserted = false;
        RefreshIrqPhysical();
    }

    private void RefreshIrqPhysical()
    {
        if (_irqAsserted) IrqBar.Drive(DigitalLevel.Low);
        else IrqBar.Release();
    }

    private void RefreshCiramPhysical()
    {
        if (!IsPowered() || !PpuAddress.TrySample(out var rawAddress)) return;
        DriveCiramOutputs(ResolvePpuSource((ushort)(rawAddress & 0x3FFF)));
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
    internal void WriteCpuMapperCompiled(ushort address, byte value) => WriteMapper(address, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteCpuWorkRamCompiled(ushort address, byte value) => WriteWorkRam(address, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ObserveCompiledCpuBusCycle(bool _)
    {
        ClockCpuCycle();
        if (!_compiledPendingAudioReadCompletion) return;
        _compiledPendingAudioReadCompletion = false;
        Audio.CompletePeekedRead();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadPpuChrCompiled(ushort address)
    {
        PpuReadCount++;
        ChrReadCount++;
        if (address >= 0x2000) ChrNametableReadCount++;
        var slot = address < 0x2000 ? address >> 10 : 8 + ((address >> 10) & 0x03);
        var bank = _ppuBankRegisters[slot] & _chrBankMask;
        return ReadPpuChr(address, bank);
    }

    private bool IsLowRegisterSelectedCompiled(int address, bool _) => IsLowRegisterAddress((ushort)address);
    private bool IsWorkRamSelectedCompiled(int address, bool _) => _workRam.Length != 0 && (address & 0x6000) == 0x6000;
    private bool IsPpuChrSelectedCompiled(int address, bool writeCycle) => !writeCycle && ResolvePpuSource((ushort)address).UsesChrRom;

    private static bool IsLowRegisterAddress(ushort address) => (address & 0xF800) is 0x4800 or 0x5000 or 0x5800;

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
            (address, value) => WriteCpuMapperCompiled((ushort)(0x8000 | address), value),
            ObserveCompiledCpuBusCycle,
            writePhase: CompiledBusWritePhase.Complete,
            observeBusCyclePhase: CompiledBusCycleObservationPhase.Complete);

        yield return new CompiledBusTargetDescriptor(
            this,
            CpuAddress.Pins,
            CpuData.Pins,
            new[]
            {
                new CompiledPinCondition(CpuReadWrite, DigitalLevel.High),
                new CompiledPinCondition(CpuM2, DigitalLevel.High),
                new CompiledPinCondition(CpuRomSelectBar, DigitalLevel.High)
            },
            new[]
            {
                new CompiledPinCondition(CpuReadWrite, DigitalLevel.Low),
                new CompiledPinCondition(CpuM2, DigitalLevel.High),
                new CompiledPinCondition(CpuRomSelectBar, DigitalLevel.High)
            },
            CompiledBusReadPhase.Complete,
            address => ReadLowRegisterCompiled((ushort)address),
            (address, value) => WriteCpuMapperCompiled((ushort)address, value),
            isSelected: IsLowRegisterSelectedCompiled,
            writePhase: CompiledBusWritePhase.Complete);

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
                isSelected: IsWorkRamSelectedCompiled,
                writePhase: CompiledBusWritePhase.Complete);
        }

        yield return new CompiledBusTargetDescriptor(
            this,
            PpuAddress.Pins,
            PpuData.Pins,
            new[] { new CompiledPinCondition(PpuReadBar, DigitalLevel.Low) },
            Array.Empty<CompiledPinCondition>(),
            CompiledBusReadPhase.Complete,
            address => ReadPpuChrCompiled((ushort)address),
            null,
            isSelected: IsPpuChrSelectedCompiled);
    }

    bool ICompiledBusAddressCombinationalComponent.TryEvaluateCompiledBusAddressOutput(
        DigitalPin output,
        uint address,
        bool readCycle,
        out CompiledDriveState drive)
    {
        var source = ResolvePpuSource((ushort)(address & 0x3FFF));
        if (ReferenceEquals(output, CiramChipEnableBar))
        {
            drive = new CompiledDriveState(source.CiramPage >= 0 ? DigitalLevel.Low : DigitalLevel.High);
            return true;
        }
        if (ReferenceEquals(output, CiramA10))
        {
            drive = new CompiledDriveState(source.CiramPage switch
            {
                0 => DigitalLevel.Low,
                1 => DigitalLevel.High,
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
        if (ReferenceEquals(output, CiramChipEnableBar) || ReferenceEquals(output, CiramA10))
        {
            ushort address = 0;
            for (var bit = 0; bit < PpuAddress.Width; bit++)
            {
                var level = sampleInput(PpuAddress.Pins[bit]);
                if (level is not (DigitalLevel.Low or DigitalLevel.High))
                {
                    drive = new CompiledDriveState(DigitalLevel.Unknown);
                    return true;
                }
                if (level == DigitalLevel.High) address |= (ushort)(1 << bit);
            }
            var source = ResolvePpuSource(address);
            if (ReferenceEquals(output, CiramChipEnableBar))
            {
                drive = new CompiledDriveState(source.CiramPage >= 0 ? DigitalLevel.Low : DigitalLevel.High);
                return true;
            }
            drive = new CompiledDriveState(source.CiramPage switch
            {
                0 => DigitalLevel.Low,
                1 => DigitalLevel.High,
                _ => DigitalLevel.Unknown
            });
            return true;
        }

        if (ReferenceEquals(output, IrqBar))
        {
            drive = new CompiledDriveState(_irqAsserted ? DigitalLevel.Low : DigitalLevel.HighImpedance);
            return true;
        }

        drive = default;
        return false;
    }

    private static int ResolveWorkRamSize(VirtualHardwareNesRomImage image)
    {
        if (image.HasExplicitRamSizes) return image.TotalPrgRamSizeBytes;
        return image.TotalPrgRamSizeBytes > 0 || image.HasBatteryBackedMemory ? WorkRamSize : 0;
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
}
