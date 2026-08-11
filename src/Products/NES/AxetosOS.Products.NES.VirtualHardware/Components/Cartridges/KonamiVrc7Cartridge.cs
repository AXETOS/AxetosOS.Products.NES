using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

/// <summary>
/// Konami VRC7 / mapper 85 replaceable cartridge hardware. The package owns
/// three switchable 8 KiB PRG outputs, eight 1 KiB CHR outputs, CIRAM routing,
/// SRAM gate, reusable VRC IRQ circuitry and the chip-local six-channel FM unit.
/// Register decode accepts the physical x008/x010 alias used by documented VRC7
/// board wiring while preserving the dedicated $9010/$9030 FM address/data pair.
/// </summary>
public sealed class KonamiVrc7Cartridge : VirtualHardwareComponent, IReplaceableCartridgeHardware,
    ICompiledBusTargetProvider, ICompiledCombinationalComponent, ICompiledStaticCombinationalComponent,
    ICompiledBusAddressCombinationalComponent
{
    private const int PrgBankSize = 8 * 1024;
    private const int ChrBankSize = 1 * 1024;
    private const int WorkRamWindowSize = 8 * 1024;
    private const int MaximumPrgBanks = 64;
    private const int MaximumChrBanks = 256;

    private byte[] _prg = [];
    private byte[] _chr = [];
    private bool _chrRam;
    private byte[] _workRam = [];
    private readonly byte[] _prgBankRegisters = new byte[3];
    private readonly byte[] _chrBankRegisters = new byte[8];
    private readonly int[] _prgWindowBanks = new int[4];
    private readonly int[] _chrWindowBanks = new int[8];
    private int _prgBankMask;
    private int _chrBankMask;
    private byte _controlFlags;

    private bool _cpuReadAddressSelected;
    private bool _cpuSelectedFromWorkRam;
    private ushort _cpuSelectedAddress;
    private byte _cpuSelectedData;
    private bool _cpuCycleHighRomSelected;
    private bool _cpuCycleWorkRamSelected;
    private ushort _cpuCycleAddress;
    private bool _ppuReadActive;
    private ushort _ppuReadAddress;
    private bool _ppuWriteActive;

    private readonly ulong _powerInputMask;
    private readonly ulong _cpuAddressControlInputMask;
    private readonly ulong _cpuM2InputMask;
    private readonly ulong _cpuRomSelectInputMask;
    private readonly ulong _ppuAddressControlInputMask;
    private readonly ulong _ppuDataInputMask;

    public KonamiVrc7Cartridge(string componentId) : base(componentId)
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
        Audio = new KonamiVrc7Audio();

        _powerInputMask = Vcc.InputChangeMask | Gnd.InputChangeMask;
        _cpuAddressControlInputMask = CpuAddress.InputChangeMask | CpuReadWrite.InputChangeMask;
        _cpuM2InputMask = CpuM2.InputChangeMask;
        _cpuRomSelectInputMask = CpuRomSelectBar.InputChangeMask;
        _ppuAddressControlInputMask = PpuAddress.InputChangeMask |
            PpuReadBar.InputChangeMask | PpuWriteBar.InputChangeMask;
        _ppuDataInputMask = PpuData.InputChangeMask;

        CpuData.SetOwnerWakeEnabled(false);
        PpuData.SetOwnerWakeEnabled(false);
        ApplyResetState();
    }

    public int MapperNumber => IsInserted ? 85 : 0;
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
    public IReadOnlyList<byte> PrgBankRegisters => _prgBankRegisters;
    public IReadOnlyList<byte> ChrBankRegisters => _chrBankRegisters;
    public IReadOnlyList<int> PrgWindowBanks => _prgWindowBanks;
    public IReadOnlyList<int> ChrWindowBanks => _chrWindowBanks;
    public bool IsChrRam => _chrRam;
    public int ChrMemorySizeBytes => _chr.Length;
    public byte ControlFlags => _controlFlags;
    public KonamiVrcNametableMode NametableMode => (KonamiVrcNametableMode)(_controlFlags & 0x03);
    public bool AudioMuted => (_controlFlags & 0x40) != 0;
    public bool WorkRamEnabled => (_controlFlags & 0x80) != 0 && _workRam.Length != 0;
    public int WorkRamSizeBytes => _workRam.Length;
    public KonamiVrcIrqCounter Irq { get; }
    public KonamiVrc7Audio Audio { get; }

    public ulong MapperWriteCount { get; private set; }
    public ushort LastMapperWriteAddress { get; private set; }
    public ushort LastNormalizedMapperWriteAddress { get; private set; }
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
        if (image.MapperNumber != 85)
            throw new NotSupportedException($"Mapper {image.MapperNumber} is not VRC7 hardware modeled by this package.");
        if (image.Mirroring == VirtualHardwareNesMirroring.FourScreen)
            throw new NotSupportedException("VRC7 boards modeled here route motherboard CIRAM and do not fit four-screen cartridge nametable RAM.");
        if (image.ChrRom.Length != 0 && image.TotalChrRamSizeBytes != 0 && image.HasExplicitRamSizes)
            throw new NotSupportedException("Mixed VRC7 CHR ROM/RAM topology requires separate physical verification.");

        if (image.PrgRom.Length < 4 * PrgBankSize || image.PrgRom.Length % PrgBankSize != 0)
            throw new ArgumentException("VRC7 PRG ROM must contain at least four whole 8 KiB banks.", nameof(image));
        var prgBanks = image.PrgRom.Length / PrgBankSize;
        if (prgBanks > MaximumPrgBanks || !IsPowerOfTwo(prgBanks))
            throw new NotSupportedException($"VRC7 PRG ROM must expose a power-of-two count of at most {MaximumPrgBanks} 8 KiB banks.");

        var chrRam = image.ChrRom.Length == 0;
        var chrMemorySize = chrRam ? ResolveChrRamSize(image) : image.ChrRom.Length;
        if (chrMemorySize < 8 * ChrBankSize || chrMemorySize % ChrBankSize != 0)
            throw new ArgumentException("VRC7 CHR memory must contain at least eight whole 1 KiB banks.", nameof(image));
        var chrBanks = chrMemorySize / ChrBankSize;
        if (chrBanks > MaximumChrBanks || !IsPowerOfTwo(chrBanks))
            throw new NotSupportedException($"VRC7 CHR memory must expose a power-of-two count of at most {MaximumChrBanks} 1 KiB banks.");

        var workRamSize = ResolveWorkRamSize(image);
        if (workRamSize != 0 && workRamSize != WorkRamWindowSize)
            throw new NotSupportedException("The VRC7 package currently supports the common single 8 KiB SRAM window.");

        _prg = image.PrgRom.ToArray();
        _chrRam = chrRam;
        _chr = chrRam ? new byte[chrMemorySize] : image.ChrRom.ToArray();
        _workRam = workRamSize == 0 ? [] : new byte[workRamSize];
        _prgBankMask = prgBanks - 1;
        _chrBankMask = chrBanks - 1;
        IsInserted = true;
        ApplyResetState();
        RefreshPpuDataWakeState();
    }

    public void Eject()
    {
        IsInserted = false;
        _prg = [];
        _chr = [];
        _chrRam = false;
        _workRam = [];
        _prgBankMask = 0;
        _chrBankMask = 0;
        ApplyResetState();
        RefreshPpuDataWakeState();
    }

    public byte InspectWorkRamByte(int offset)
    {
        if ((uint)offset >= (uint)_workRam.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        return _workRam[offset];
    }

    public byte InspectChrByte(int offset)
    {
        if ((uint)offset >= (uint)_chr.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        return _chr[offset];
    }

    public void ResetDiagnostics()
    {
        MapperWriteCount = 0;
        LastMapperWriteAddress = 0;
        LastNormalizedMapperWriteAddress = 0;
        LastMapperWriteData = 0;
        CpuReadCount = 0;
        LastCpuReadAddress = 0;
        LastCpuReadData = 0;
        WorkRamReadCount = 0;
        WorkRamWriteCount = 0;
        PpuReadCount = 0;
        PpuWriteCount = 0;
    }

    private void ApplyResetState()
    {
        Array.Clear(_prgBankRegisters);
        Array.Clear(_chrBankRegisters);
        Array.Clear(_prgWindowBanks);
        Array.Clear(_chrWindowBanks);
        _controlFlags = 0;

        if (_prg.Length != 0)
        {
            _prgWindowBanks[0] = 0;
            _prgWindowBanks[1] = 0;
            _prgWindowBanks[2] = 0;
            _prgWindowBanks[3] = _prgBankMask;
        }
        if (_chr.Length != 0)
        {
            for (var slot = 0; slot < 8; slot++) _chrWindowBanks[slot] = 0;
        }

        _cpuReadAddressSelected = false;
        _cpuSelectedFromWorkRam = false;
        _cpuSelectedAddress = 0;
        _cpuSelectedData = 0;
        _cpuCycleHighRomSelected = false;
        _cpuCycleWorkRamSelected = false;
        _cpuCycleAddress = 0;
        _ppuReadActive = false;
        _ppuReadAddress = 0;
        _ppuWriteActive = false;
        ResetDiagnostics();
        Irq.Reset();
        Audio.Reset();
        RefreshCiramPhysical();
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
        var ppuDataChanged = (changedInputMask & _ppuDataInputMask) != 0;
        var ppuDataCanMatter = ppuDataChanged && _chrRam && PpuWriteBar.SampledLevel == DigitalLevel.Low;

        if (!powerChanged && !cpuAddressOrControlChanged && !cpuM2Changed &&
            !cpuRomSelectChanged && !ppuAddressOrControlChanged && !ppuDataCanMatter)
            return;

        if (!IsPowered())
        {
            _cpuReadAddressSelected = false;
            _cpuCycleHighRomSelected = false;
            _cpuCycleWorkRamSelected = false;
            _ppuReadActive = false;
            _ppuWriteActive = false;
            ReleaseOutputs();
            RefreshPpuDataWakeState();
            return;
        }

        if (powerChanged)
        {
            RefreshIrqPhysical();
            RefreshCiramPhysical();
        }
        if (powerChanged || ppuAddressOrControlChanged) RefreshPpuDataWakeState();
        if (powerChanged || ppuAddressOrControlChanged || ppuDataCanMatter) ProcessPpuPort();

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
            RecordCpuRead(_cpuSelectedAddress, _cpuSelectedData);
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
        if (_cpuCycleWorkRamSelected) WriteWorkRam(_cpuCycleAddress, value);
    }

    private void ProcessPpuPort()
    {
        if (!PpuAddress.TrySample(out var rawAddress))
        {
            CiramChipEnableBar.Drive(DigitalLevel.High);
            CiramA10.Drive(DigitalLevel.Unknown);
            PpuData.Release();
            _ppuReadActive = false;
            _ppuWriteActive = false;
            return;
        }

        var address = (ushort)(rawAddress & 0x3FFF);
        DriveCiramOutputs(address);
        var readSelected = address < 0x2000 && PpuReadBar.SampledLevel == DigitalLevel.Low;
        var writeSelected = address < 0x2000 && _chrRam && PpuWriteBar.SampledLevel == DigitalLevel.Low;

        if (readSelected)
        {
            var newRead = !_ppuReadActive || _ppuReadAddress != address;
            PpuData.Drive(ReadChr(address));
            if (newRead) PpuReadCount++;
            _ppuReadAddress = address;
        }
        else
        {
            PpuData.Release();
            if (writeSelected && !_ppuWriteActive && PpuData.TrySample(out var rawData))
                WriteChr(address, (byte)rawData);
        }

        _ppuReadActive = readSelected;
        _ppuWriteActive = writeSelected;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DriveCiramOutputs(ushort address)
    {
        CiramChipEnableBar.Drive((address & 0x2000) != 0 ? DigitalLevel.Low : DigitalLevel.High);
        CiramA10.Drive(EvaluateCiramA10(address));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private DigitalLevel EvaluateCiramA10(ushort address)
    {
        if ((address & 0x2000) == 0) return DigitalLevel.Unknown;
        return NametableMode switch
        {
            KonamiVrcNametableMode.Vertical => (address & 0x0400) != 0 ? DigitalLevel.High : DigitalLevel.Low,
            KonamiVrcNametableMode.Horizontal => (address & 0x0800) != 0 ? DigitalLevel.High : DigitalLevel.Low,
            KonamiVrcNametableMode.SingleScreenPage0 => DigitalLevel.Low,
            KonamiVrcNametableMode.SingleScreenPage1 => DigitalLevel.High,
            _ => DigitalLevel.Unknown
        };
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
    private void WriteChr(ushort address, byte value)
    {
        if (!_chrRam) return;
        var slot = address >> 10;
        var bank = _chrWindowBanks[slot];
        _chr[(bank * ChrBankSize) + (address & 0x03FF)] = value;
        PpuWriteCount++;
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
        var register = NormalizeRegisterAddress(address);
        LastNormalizedMapperWriteAddress = register;

        switch (register)
        {
            case 0x8000:
                _prgBankRegisters[0] = (byte)(value & 0x3F);
                RefreshPrgWindows();
                return;
            case 0x8008:
                _prgBankRegisters[1] = (byte)(value & 0x3F);
                RefreshPrgWindows();
                return;
            case 0x9000:
                _prgBankRegisters[2] = (byte)(value & 0x3F);
                RefreshPrgWindows();
                return;
            case 0x9010:
            case 0x9030:
                Audio.WritePort(register, value);
                return;
            case 0xA000: SetChrRegister(0, value); return;
            case 0xA008: SetChrRegister(1, value); return;
            case 0xB000: SetChrRegister(2, value); return;
            case 0xB008: SetChrRegister(3, value); return;
            case 0xC000: SetChrRegister(4, value); return;
            case 0xC008: SetChrRegister(5, value); return;
            case 0xD000: SetChrRegister(6, value); return;
            case 0xD008: SetChrRegister(7, value); return;
            case 0xE000:
                _controlFlags = value;
                Audio.SetMuted(AudioMuted);
                RefreshCiramPhysical();
                return;
            case 0xE008:
                Irq.SetReloadValue(value);
                return;
            case 0xF000:
                Irq.SetControl(value);
                RefreshIrqPhysical();
                return;
            case 0xF008:
                Irq.Acknowledge();
                RefreshIrqPhysical();
                return;
        }
    }

    public static ushort NormalizeRegisterAddress(ushort address)
    {
        if ((address & 0x0010) != 0 && (address & 0xF010) != 0x9010)
        {
            address |= 0x0008;
            address &= 0xFFEF;
        }
        return (ushort)(address & 0xF038);
    }

    private void SetChrRegister(int slot, byte value)
    {
        _chrBankRegisters[slot] = value;
        _chrWindowBanks[slot] = value & _chrBankMask;
    }

    private void RefreshPrgWindows()
    {
        _prgWindowBanks[0] = _prgBankRegisters[0] & _prgBankMask;
        _prgWindowBanks[1] = _prgBankRegisters[1] & _prgBankMask;
        _prgWindowBanks[2] = _prgBankRegisters[2] & _prgBankMask;
        _prgWindowBanks[3] = _prgBankMask;
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
        if (!PpuAddress.TrySample(out var rawAddress)) return;
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
        return ReadChr(address);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WritePpuCompiled(ushort address, byte value) => WriteChr(address, value);

    private void RefreshPpuDataWakeState()
    {
        var enabled = IsPowered() && _chrRam && PpuWriteBar.SampledLevel == DigitalLevel.Low;
        PpuData.SetOwnerWakeEnabled(enabled);
    }

    private bool IsWorkRamSelectedCompiled(int address, bool _) => WorkRamEnabled && (address & 0x6000) == 0x6000;

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
                isSelected: IsWorkRamSelectedCompiled,
                writePhase: CompiledBusWritePhase.Complete);
        }

        Action<int, byte>? compiledPpuWrite = _chrRam
            ? (address, value) => WritePpuCompiled((ushort)address, value)
            : null;
        yield return new CompiledBusTargetDescriptor(
            this,
            PpuAddress.Pins,
            PpuData.Pins,
            new[]
            {
                new CompiledPinCondition(PpuAddress.Pins[13], DigitalLevel.Low),
                new CompiledPinCondition(PpuReadBar, DigitalLevel.Low)
            },
            _chrRam
                ? new[]
                {
                    new CompiledPinCondition(PpuAddress.Pins[13], DigitalLevel.Low),
                    new CompiledPinCondition(PpuWriteBar, DigitalLevel.Low)
                }
                : Array.Empty<CompiledPinCondition>(),
            CompiledBusReadPhase.Complete,
            address => ReadPpuCompiled((ushort)address),
            compiledPpuWrite);
    }

    bool ICompiledBusAddressCombinationalComponent.TryEvaluateCompiledBusAddressOutput(
        DigitalPin output,
        uint address,
        bool readCycle,
        out CompiledDriveState drive)
    {
        var ppuAddress = (ushort)(address & 0x3FFF);
        if (ReferenceEquals(output, CiramChipEnableBar))
        {
            drive = new CompiledDriveState((ppuAddress & 0x2000) != 0 ? DigitalLevel.Low : DigitalLevel.High);
            return true;
        }
        if (ReferenceEquals(output, CiramA10))
        {
            drive = new CompiledDriveState(EvaluateCiramA10(ppuAddress));
            return true;
        }
        drive = default;
        return false;
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
            drive = NametableMode switch
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

    private static int ResolveChrRamSize(VirtualHardwareNesRomImage image)
    {
        if (image.TotalChrRamSizeBytes > 0) return image.TotalChrRamSizeBytes;
        if (!image.HasExplicitRamSizes) return 8 * 1024;
        throw new NotSupportedException("VRC7 image has no CHR ROM and declares no CHR RAM.");
    }

    private static int ResolveWorkRamSize(VirtualHardwareNesRomImage image)
    {
        if (image.HasExplicitRamSizes) return image.TotalPrgRamSizeBytes;
        return image.TotalPrgRamSizeBytes > 0 ? WorkRamWindowSize : 0;
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
}
