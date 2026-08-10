using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

public enum SunsoftFme7NametableMode : byte
{
    Vertical = 0,
    Horizontal = 1,
    SingleScreenPage0 = 2,
    SingleScreenPage1 = 3
}

/// <summary>
/// Mapper-69 Sunsoft FME-7/5A/5B-family replaceable cartridge hardware. The
/// cartridge package owns its command/data register interface, four banked PRG
/// windows, eight CHR bank outputs, banked ROM/RAM $6000 window, CIRAM routing,
/// CPU-cycle IRQ down-counter and the Sunsoft-5B-compatible PSG register block.
/// Mapper 69 metadata does not identify the exact FME-7/5A/5B ASIC revision, so
/// the externally compatible family circuitry is represented without filename,
/// hash or motherboard-side board inference.
/// </summary>
public sealed class SunsoftFme7Cartridge : VirtualHardwareComponent, IReplaceableCartridgeHardware,
    ICompiledBusTargetProvider, ICompiledCombinationalComponent, ICompiledStaticCombinationalComponent
{
    private const int PrgBankSize = 8 * 1024;
    private const int ChrBankSize = 1 * 1024;
    private const int WramBankSize = 8 * 1024;
    private const int MaximumPrgBanks = 64;
    private const int MaximumChrBanks = 256;
    private const int MaximumWramBanks = 64;

    private byte[] _prg = [];
    private byte[] _chr = [];
    private byte[] _wram = [];
    private readonly byte[] _prgBankRegisters = new byte[3];
    private readonly byte[] _chrBankRegisters = new byte[8];
    private readonly int[] _prgWindowBanks = new int[4];
    private readonly int[] _chrWindowBanks = new int[8];
    private int _prgBankMask;
    private int _chrBankMask;
    private int _wramBankMask;
    private byte _commandRegister;
    private byte _prg6000Control;
    private SunsoftFme7NametableMode _nametableMode;
    private SunsoftFme7NametableMode _powerOnNametableMode;

    private ushort _irqCounter;
    private bool _irqCounterEnabled;
    private bool _irqOutputEnabled;
    private bool _irqAsserted;

    private bool _cpuReadAddressSelected;
    private bool _cpuSelectedFromWram;
    private bool _cpuSelectedFrom6000Rom;
    private ushort _cpuSelectedAddress;
    private byte _cpuSelectedData;
    private bool _cpuCycleHighRomSelected;
    private bool _cpuCycle6000Window;
    private ushort _cpuCycleAddress;
    private bool _ppuReadActive;
    private ushort _ppuReadAddress;

    private readonly ulong _powerInputMask;
    private readonly ulong _cpuAddressControlInputMask;
    private readonly ulong _cpuM2InputMask;
    private readonly ulong _cpuRomSelectInputMask;
    private readonly ulong _ppuAddressControlInputMask;

    public SunsoftFme7Cartridge(string componentId) : base(componentId)
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
        Psg = new Sunsoft5bPsg();

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

    public int MapperNumber => 69;
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
    public byte CommandRegister => _commandRegister;
    public byte Prg6000ControlRegister => _prg6000Control;
    public bool Prg6000RamSelected => (_prg6000Control & 0x40) != 0;
    public bool Prg6000RamEnabled => Prg6000RamSelected && (_prg6000Control & 0x80) != 0 && _wram.Length != 0;
    public bool Prg6000RomSelected => !Prg6000RamSelected;
    public int Prg6000Bank => (_prg6000Control & 0x3F) & _prgBankMask;
    public int WramBank => _wram.Length == 0 ? 0 : (_prg6000Control & 0x3F) & _wramBankMask;
    public int WramSizeBytes => _wram.Length;
    public IReadOnlyList<byte> PrgBankRegisters => _prgBankRegisters;
    public IReadOnlyList<byte> ChrBankRegisters => _chrBankRegisters;
    public IReadOnlyList<int> PrgWindowBanks => _prgWindowBanks;
    public IReadOnlyList<int> ChrWindowBanks => _chrWindowBanks;
    public int FixedPrgBank => _prgWindowBanks[3];
    public SunsoftFme7NametableMode NametableMode => _nametableMode;
    public ushort IrqCounter => _irqCounter;
    public bool IrqCounterEnabled => _irqCounterEnabled;
    public bool IrqOutputEnabled => _irqOutputEnabled;
    public bool IrqAsserted => _irqAsserted;
    public Sunsoft5bPsg Psg { get; }

    public ulong MapperWriteCount { get; private set; }
    public ulong CoreRegisterWriteCount { get; private set; }
    public ushort LastMapperWriteAddress { get; private set; }
    public byte LastMapperWriteData { get; private set; }
    public ulong CpuReadCount { get; private set; }
    public ushort LastCpuReadAddress { get; private set; }
    public byte LastCpuReadData { get; private set; }
    public ulong Prg6000RomReadCount { get; private set; }
    public ulong WramReadCount { get; private set; }
    public ulong WramWriteCount { get; private set; }
    public ulong PpuReadCount { get; private set; }
    public ulong PpuWriteCount { get; private set; }
    public ulong CpuCycleClockCount { get; private set; }
    public ulong IrqClockCount { get; private set; }
    public ulong IrqAssertCount { get; private set; }

    public void LoadImage(VirtualHardwareNesRomImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.MapperNumber != 69)
            throw new NotSupportedException($"Mapper {image.MapperNumber} is not Sunsoft FME-7-family hardware.");
        if (image.Mirroring == VirtualHardwareNesMirroring.FourScreen)
            throw new NotSupportedException("Mapper 69 FME-7-family boards route CIRAM through the mapper and do not use four-screen cartridge nametable RAM.");
        if (image.ChrRom.Length == 0)
            throw new NotSupportedException("FME-7-family Mapper 69 boards modeled here use banked CHR ROM; CHR-RAM-only boards require distinct physical verification.");
        if (image.TotalChrRamSizeBytes != 0 && image.HasExplicitRamSizes)
            throw new NotSupportedException("Mixed CHR ROM/RAM Mapper 69 boards are not FME-7-family hardware modeled here.");

        if (image.PrgRom.Length < 4 * PrgBankSize || image.PrgRom.Length % PrgBankSize != 0)
            throw new ArgumentException("FME-7-family PRG ROM must contain at least four whole 8 KiB banks.", nameof(image));
        var prgBanks = image.PrgRom.Length / PrgBankSize;
        if (prgBanks > MaximumPrgBanks || !IsPowerOfTwo(prgBanks))
            throw new NotSupportedException($"FME-7-family PRG ROM must expose a power-of-two count of at most {MaximumPrgBanks} 8 KiB banks.");

        if (image.ChrRom.Length < 8 * ChrBankSize || image.ChrRom.Length % ChrBankSize != 0)
            throw new ArgumentException("FME-7-family CHR ROM must contain at least eight whole 1 KiB banks.", nameof(image));
        var chrBanks = image.ChrRom.Length / ChrBankSize;
        if (chrBanks > MaximumChrBanks || !IsPowerOfTwo(chrBanks))
            throw new NotSupportedException($"FME-7-family CHR ROM must expose a power-of-two count of at most {MaximumChrBanks} 1 KiB banks.");

        var wramSize = ResolveWramSize(image);
        if (wramSize != 0)
        {
            if (wramSize % WramBankSize != 0)
                throw new NotSupportedException("FME-7-family work RAM must be wired in whole 8 KiB banks.");
            var wramBanks = wramSize / WramBankSize;
            if (wramBanks > MaximumWramBanks || !IsPowerOfTwo(wramBanks))
                throw new NotSupportedException($"FME-7-family work RAM must expose a power-of-two count of at most {MaximumWramBanks} 8 KiB banks.");
        }

        _prg = image.PrgRom.ToArray();
        _chr = image.ChrRom.ToArray();
        _wram = wramSize == 0 ? [] : new byte[wramSize];
        _prgBankMask = prgBanks - 1;
        _chrBankMask = chrBanks - 1;
        _wramBankMask = _wram.Length == 0 ? 0 : (_wram.Length / WramBankSize) - 1;
        _powerOnNametableMode = image.Mirroring == VirtualHardwareNesMirroring.Vertical
            ? SunsoftFme7NametableMode.Vertical
            : SunsoftFme7NametableMode.Horizontal;
        IsInserted = true;
        ApplyResetState();
    }

    public void Eject()
    {
        IsInserted = false;
        _prg = [];
        _chr = [];
        _wram = [];
        _prgBankMask = 0;
        _chrBankMask = 0;
        _wramBankMask = 0;
        ApplyResetState();
    }

    public byte InspectWramByte(int offset)
    {
        if ((uint)offset >= (uint)_wram.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        return _wram[offset];
    }

    private void ApplyResetState()
    {
        Array.Clear(_prgBankRegisters);
        Array.Clear(_chrBankRegisters);
        Array.Clear(_prgWindowBanks);
        Array.Clear(_chrWindowBanks);
        if (IsInserted)
        {
            var prgBanks = _prg.Length / PrgBankSize;
            for (var slot = 0; slot < 3; slot++)
            {
                _prgBankRegisters[slot] = (byte)slot;
                _prgWindowBanks[slot] = Math.Min(slot, prgBanks - 1);
            }
            _prgWindowBanks[3] = Math.Max(0, prgBanks - 1);

            var chrBanks = _chr.Length / ChrBankSize;
            for (var slot = 0; slot < 8; slot++)
            {
                _chrBankRegisters[slot] = (byte)slot;
                _chrWindowBanks[slot] = Math.Min(slot, chrBanks - 1);
            }
        }

        _commandRegister = 0;
        _prg6000Control = 0;
        _nametableMode = _powerOnNametableMode;
        _irqCounter = 0;
        _irqCounterEnabled = false;
        _irqOutputEnabled = false;
        _irqAsserted = false;
        _cpuReadAddressSelected = false;
        _cpuSelectedFromWram = false;
        _cpuSelectedFrom6000Rom = false;
        _cpuSelectedAddress = 0;
        _cpuSelectedData = 0;
        _cpuCycleHighRomSelected = false;
        _cpuCycle6000Window = false;
        _cpuCycleAddress = 0;
        _ppuReadActive = false;
        _ppuReadAddress = 0;
        MapperWriteCount = 0;
        CoreRegisterWriteCount = 0;
        LastMapperWriteAddress = 0;
        LastMapperWriteData = 0;
        CpuReadCount = 0;
        LastCpuReadAddress = 0;
        LastCpuReadData = 0;
        Prg6000RomReadCount = 0;
        WramReadCount = 0;
        WramWriteCount = 0;
        PpuReadCount = 0;
        PpuWriteCount = 0;
        CpuCycleClockCount = 0;
        IrqClockCount = 0;
        IrqAssertCount = 0;
        Psg.Reset();
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
            _cpuCycle6000Window = false;
            _ppuReadActive = false;
            ReleaseOutputs();
            return;
        }

        if (powerChanged) SetIrqAsserted(_irqAsserted);
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
            _cpuCycle6000Window = false;
            CpuData.Release();
            return;
        }

        var connectorAddress = (ushort)(rawAddress & 0x7FFF);
        var m2High = CpuM2.SampledLevel == DigitalLevel.High;
        var highRomSelected = m2High && CpuRomSelectBar.SampledLevel == DigitalLevel.Low;
        var window6000 = m2High
            && CpuRomSelectBar.SampledLevel == DigitalLevel.High
            && (connectorAddress & 0x6000) == 0x6000;

        _cpuCycleHighRomSelected = highRomSelected;
        _cpuCycle6000Window = window6000;
        _cpuCycleAddress = highRomSelected ? (ushort)(0x8000 | connectorAddress) : connectorAddress;

        CpuData.Release();
        _cpuReadAddressSelected = false;
        _cpuSelectedFromWram = false;
        _cpuSelectedFrom6000Rom = false;
        if (CpuReadWrite.SampledLevel != DigitalLevel.High) return;

        if (highRomSelected)
        {
            SelectCpuRead(_cpuCycleAddress, ReadPrgHigh(_cpuCycleAddress));
            return;
        }

        if (!window6000) return;
        if (Prg6000RomSelected)
        {
            _cpuSelectedFrom6000Rom = true;
            SelectCpuRead(connectorAddress, ReadPrg6000Rom(connectorAddress));
            return;
        }

        if (Prg6000RamEnabled)
        {
            _cpuSelectedFromWram = true;
            SelectCpuRead(connectorAddress, ReadWram(connectorAddress));
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
            if (_cpuSelectedFromWram) WramReadCount++;
            if (_cpuSelectedFrom6000Rom) Prg6000RomReadCount++;
            return;
        }

        if (!CpuData.TrySample(out var rawData)) return;
        var value = (byte)rawData;
        if (_cpuCycleHighRomSelected)
        {
            WriteCpuHigh(_cpuCycleAddress, value);
            return;
        }

        if (_cpuCycle6000Window && Prg6000RamEnabled)
            WriteWram(_cpuCycleAddress, value);
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
        SunsoftFme7NametableMode.Vertical => (address & 0x0400) != 0 ? DigitalLevel.High : DigitalLevel.Low,
        SunsoftFme7NametableMode.Horizontal => (address & 0x0800) != 0 ? DigitalLevel.High : DigitalLevel.Low,
        SunsoftFme7NametableMode.SingleScreenPage0 => DigitalLevel.Low,
        SunsoftFme7NametableMode.SingleScreenPage1 => DigitalLevel.High,
        _ => DigitalLevel.Unknown
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RefreshCiramA10Physical()
    {
        if (!IsPowered() || !PpuAddress.TrySample(out var rawAddress)) return;
        CiramA10.Drive(EvaluateCiramA10((ushort)(rawAddress & 0x3FFF)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadPrgHigh(ushort address)
    {
        var slot = (address - 0x8000) >> 13;
        var bank = _prgWindowBanks[slot];
        return _prg[(bank * PrgBankSize) + (address & 0x1FFF)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadPrg6000Rom(ushort address) =>
        _prg[(Prg6000Bank * PrgBankSize) + (address & 0x1FFF)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadWram(ushort address) =>
        _wram[(WramBank * WramBankSize) + (address & 0x1FFF)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadChr(ushort address)
    {
        var slot = address >> 10;
        var bank = _chrWindowBanks[slot];
        return _chr[(bank * ChrBankSize) + (address & 0x03FF)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteWram(ushort address, byte value)
    {
        _wram[(WramBank * WramBankSize) + (address & 0x1FFF)] = value;
        WramWriteCount++;
    }

    private void WriteCpuHigh(ushort address, byte value)
    {
        MapperWriteCount++;
        LastMapperWriteAddress = address;
        LastMapperWriteData = value;

        switch (address & 0xE000)
        {
            case 0x8000:
                _commandRegister = (byte)(value & 0x0F);
                CoreRegisterWriteCount++;
                break;
            case 0xA000:
                WriteCommandData(value);
                CoreRegisterWriteCount++;
                break;
            case 0xC000:
                Psg.WriteRegisterSelect(value);
                break;
            case 0xE000:
                Psg.WriteRegisterData(value);
                break;
        }
    }

    private void WriteCommandData(byte value)
    {
        if (_commandRegister <= 0x07)
        {
            var slot = _commandRegister;
            _chrBankRegisters[slot] = value;
            _chrWindowBanks[slot] = value & _chrBankMask;
            return;
        }

        switch (_commandRegister)
        {
            case 0x08:
                _prg6000Control = value;
                break;
            case 0x09:
            case 0x0A:
            case 0x0B:
            {
                var slot = _commandRegister - 0x09;
                _prgBankRegisters[slot] = (byte)(value & 0x3F);
                _prgWindowBanks[slot] = _prgBankRegisters[slot] & _prgBankMask;
                break;
            }
            case 0x0C:
                _nametableMode = (SunsoftFme7NametableMode)(value & 0x03);
                RefreshCiramA10Physical();
                break;
            case 0x0D:
                _irqCounterEnabled = (value & 0x80) != 0;
                _irqOutputEnabled = (value & 0x01) != 0;
                SetIrqAsserted(false);
                break;
            case 0x0E:
                _irqCounter = (ushort)((_irqCounter & 0xFF00) | value);
                break;
            case 0x0F:
                _irqCounter = (ushort)((_irqCounter & 0x00FF) | (value << 8));
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClockCpuCycle()
    {
        CpuCycleClockCount++;
        Psg.ClockCpuCycle();
        if (!_irqCounterEnabled) return;

        IrqClockCount++;
        _irqCounter = unchecked((ushort)(_irqCounter - 1));
        if (_irqCounter == ushort.MaxValue && _irqOutputEnabled)
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
        var value = ReadPrgHigh(address);
        RecordCpuRead(address, value);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadCpu6000Compiled(ushort address)
    {
        byte value;
        if (Prg6000RomSelected)
        {
            value = ReadPrg6000Rom(address);
            Prg6000RomReadCount++;
        }
        else
        {
            value = ReadWram(address);
            WramReadCount++;
        }
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
    internal void WriteCpuHighCompiled(ushort address, byte value) => WriteCpuHigh(address, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteCpu6000Compiled(ushort address, byte value) => WriteWram(address, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsCpu6000SelectedCompiled(int _, bool write)
    {
        if (Prg6000RomSelected) return !write;
        return Prg6000RamEnabled;
    }

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
            address => ReadCpu6000Compiled((ushort)address),
            (address, value) => WriteCpu6000Compiled((ushort)address, value),
            isSelected: IsCpu6000SelectedCompiled,
            writePhase: CompiledBusWritePhase.Complete);

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
                SunsoftFme7NametableMode.Vertical => new CompiledDriveState(sampleInput(PpuAddress.Pins[10])),
                SunsoftFme7NametableMode.Horizontal => new CompiledDriveState(sampleInput(PpuAddress.Pins[11])),
                SunsoftFme7NametableMode.SingleScreenPage0 => new CompiledDriveState(DigitalLevel.Low),
                SunsoftFme7NametableMode.SingleScreenPage1 => new CompiledDriveState(DigitalLevel.High),
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

    private static int ResolveWramSize(VirtualHardwareNesRomImage image)
    {
        if (image.HasExplicitRamSizes) return image.TotalPrgRamSizeBytes;
        return image.HasBatteryBackedMemory ? WramBankSize : 0;
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
}
