using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

public enum JalecoSs88006NametableMode : byte
{
    Horizontal = 0,
    Vertical = 1,
    SingleScreenPage0 = 2,
    SingleScreenPage1 = 3
}

/// <summary>
/// Mapper-18/Jaleco SS88006 replaceable cartridge hardware. The ASIC owns its
/// split-nibble PRG/CHR bank registers, work-RAM protection, four-mode CIRAM
/// routing and masked CPU-cycle IRQ down-counter. Optional Jaleco speech/sample
/// hardware is a separate board-local package; this component exposes the
/// SS88006 sample-control output state without synthesizing that external audio.
/// </summary>
public sealed class JalecoSs88006Cartridge : VirtualHardwareComponent, IReplaceableCartridgeHardware,
    ICompiledBusTargetProvider, ICompiledCombinationalComponent, ICompiledStaticCombinationalComponent
{
    private const int PrgBankSize = 8 * 1024;
    private const int ChrBankSize = 1 * 1024;
    private const int StandardWramSize = 8 * 1024;

    private byte[] _prg = [];
    private byte[] _chr = [];
    private byte[] _wram = [];
    private readonly byte[] _prgBankRegisters = new byte[3];
    private readonly byte[] _chrBankRegisters = new byte[8];
    private readonly int[] _prgWindowBanks = new int[4];
    private readonly int[] _chrWindowBanks = new int[8];
    private int _prgBankMask;
    private int _chrBankMask;
    private byte _wramProtect;
    private JalecoSs88006NametableMode _nametableMode;

    private ushort _irqLatch;
    private ushort _irqCounter;
    private bool _irqEnabled;
    private byte _irqMode;
    private bool _irqAsserted;

    private byte _sampleControl;
    private int _lastSampleIndex = -1;

    private bool _cpuReadAddressSelected;
    private ushort _cpuSelectedAddress;
    private byte _cpuSelectedData;
    private bool _cpuCycleHighRomSelected;
    private bool _cpuCycleWramSelected;
    private ushort _cpuCycleAddress;
    private bool _ppuReadActive;
    private ushort _ppuReadAddress;

    private readonly ulong _powerInputMask;
    private readonly ulong _cpuAddressControlInputMask;
    private readonly ulong _cpuM2InputMask;
    private readonly ulong _cpuRomSelectInputMask;
    private readonly ulong _ppuAddressControlInputMask;

    public JalecoSs88006Cartridge(string componentId) : base(componentId)
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
        ApplyResetState();
    }

    public int MapperNumber => 18;
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
    public int FixedPrgBank => _prgWindowBanks[3];
    public int WramSizeBytes => _wram.Length;
    public byte WramProtectRegister => _wramProtect;
    public bool WramReadEnabled => _wram.Length != 0 && (_wramProtect & 0x01) != 0;
    public bool WramWriteEnabled => _wram.Length != 0 && (_wramProtect & 0x03) == 0x03;
    public JalecoSs88006NametableMode NametableMode => _nametableMode;
    public ushort IrqLatch => _irqLatch;
    public ushort IrqCounter => _irqCounter;
    public bool IrqEnabled => _irqEnabled;
    public byte IrqMode => _irqMode;
    public ushort IrqCounterMask => ResolveIrqMask();
    public bool IrqAsserted => _irqAsserted;
    public byte SampleControlRegister => _sampleControl;
    public int LastSampleIndex => _lastSampleIndex;

    public ulong MapperWriteCount { get; private set; }
    public ushort LastMapperWriteAddress { get; private set; }
    public byte LastMapperWriteData { get; private set; }
    public ulong CpuReadCount { get; private set; }
    public ushort LastCpuReadAddress { get; private set; }
    public byte LastCpuReadData { get; private set; }
    public ulong WramReadCount { get; private set; }
    public ulong WramWriteCount { get; private set; }
    public ulong PpuReadCount { get; private set; }
    public ulong PpuWriteCount { get; private set; }
    public ulong IrqClockCount { get; private set; }
    public ulong IrqAssertCount { get; private set; }
    public ulong SampleControlWriteCount { get; private set; }
    public ulong SampleTriggerCount { get; private set; }

    public void LoadImage(VirtualHardwareNesRomImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.MapperNumber != 18)
            throw new NotSupportedException($"Mapper {image.MapperNumber} is not Jaleco SS88006 hardware.");
        if (image.Mirroring == VirtualHardwareNesMirroring.FourScreen)
            throw new NotSupportedException("Mapper 18 SS88006 boards route CIRAM through the ASIC and do not use four-screen cartridge nametable RAM.");
        if (image.ChrRom.Length == 0)
            throw new NotSupportedException("Known SS88006 boards use banked CHR ROM; CHR-RAM-only Mapper 18 images require distinct board verification.");
        if (image.TotalChrRamSizeBytes != 0 && image.HasExplicitRamSizes)
            throw new NotSupportedException("Mixed CHR ROM/RAM Mapper 18 boards are not SS88006 hardware modeled here.");

        if (image.PrgRom.Length < 4 * PrgBankSize || image.PrgRom.Length % PrgBankSize != 0)
            throw new ArgumentException("SS88006 PRG ROM must contain at least four whole 8 KiB banks.", nameof(image));
        var prgBanks = image.PrgRom.Length / PrgBankSize;
        if (prgBanks > 256 || !IsPowerOfTwo(prgBanks))
            throw new NotSupportedException("SS88006 PRG ROM must expose a power-of-two count of at most 256 8 KiB banks.");

        if (image.ChrRom.Length < 8 * ChrBankSize || image.ChrRom.Length % ChrBankSize != 0)
            throw new ArgumentException("SS88006 CHR ROM must contain at least eight whole 1 KiB banks.", nameof(image));
        var chrBanks = image.ChrRom.Length / ChrBankSize;
        if (chrBanks > 256 || !IsPowerOfTwo(chrBanks))
            throw new NotSupportedException("SS88006 CHR ROM must expose a power-of-two count of at most 256 1 KiB banks.");

        var wramSize = ResolveWramSize(image);
        if (wramSize is not (0 or StandardWramSize))
            throw new NotSupportedException($"SS88006 boards support zero or {StandardWramSize:N0} bytes of work RAM; image declares {wramSize:N0} bytes.");

        _prg = image.PrgRom.ToArray();
        _chr = image.ChrRom.ToArray();
        _wram = wramSize == 0 ? [] : new byte[StandardWramSize];
        _prgBankMask = prgBanks - 1;
        _chrBankMask = chrBanks - 1;
        _nametableMode = image.Mirroring == VirtualHardwareNesMirroring.Vertical
            ? JalecoSs88006NametableMode.Vertical
            : JalecoSs88006NametableMode.Horizontal;
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
            _prgWindowBanks[0] = 0;
            _prgWindowBanks[1] = Math.Min(1, prgBanks - 1);
            _prgWindowBanks[2] = Math.Max(0, prgBanks - 2);
            _prgWindowBanks[3] = Math.Max(0, prgBanks - 1);
            var chrBanks = _chr.Length / ChrBankSize;
            for (var slot = 0; slot < 8; slot++)
                _chrWindowBanks[slot] = Math.Min(slot, chrBanks - 1);
        }

        _wramProtect = 0;
        _irqLatch = 0;
        _irqCounter = 0;
        _irqEnabled = false;
        _irqMode = 0;
        _irqAsserted = false;
        _sampleControl = 0;
        _lastSampleIndex = -1;
        _cpuReadAddressSelected = false;
        _cpuSelectedAddress = 0;
        _cpuSelectedData = 0;
        _cpuCycleHighRomSelected = false;
        _cpuCycleWramSelected = false;
        _cpuCycleAddress = 0;
        _ppuReadActive = false;
        _ppuReadAddress = 0;
        MapperWriteCount = 0;
        LastMapperWriteAddress = 0;
        LastMapperWriteData = 0;
        CpuReadCount = 0;
        LastCpuReadAddress = 0;
        LastCpuReadData = 0;
        WramReadCount = 0;
        WramWriteCount = 0;
        PpuReadCount = 0;
        PpuWriteCount = 0;
        IrqClockCount = 0;
        IrqAssertCount = 0;
        SampleControlWriteCount = 0;
        SampleTriggerCount = 0;
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
            _cpuCycleWramSelected = false;
            _ppuReadActive = false;
            ReleaseOutputs();
            return;
        }

        if (powerChanged) SetIrqAsserted(_irqAsserted);
        if (powerChanged || ppuAddressOrControlChanged) ProcessPpuPort();

        if (!powerChanged && cpuM2Changed && CpuM2.SampledLevel == DigitalLevel.Low)
        {
            ClockIrqCounter();
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
            _cpuCycleWramSelected = false;
            CpuData.Release();
            return;
        }

        var connectorAddress = (ushort)(rawAddress & 0x7FFF);
        var m2High = CpuM2.SampledLevel == DigitalLevel.High;
        var highRomSelected = m2High && CpuRomSelectBar.SampledLevel == DigitalLevel.Low;
        var wramWindow = m2High
            && CpuRomSelectBar.SampledLevel == DigitalLevel.High
            && (connectorAddress & 0x6000) == 0x6000
            && _wram.Length != 0;

        _cpuCycleHighRomSelected = highRomSelected;
        _cpuCycleWramSelected = wramWindow;
        _cpuCycleAddress = highRomSelected ? (ushort)(0x8000 | connectorAddress) : connectorAddress;

        CpuData.Release();
        if (CpuReadWrite.SampledLevel != DigitalLevel.High) return;

        if (highRomSelected)
        {
            _cpuReadAddressSelected = true;
            _cpuSelectedAddress = _cpuCycleAddress;
            _cpuSelectedData = ReadPrg(_cpuSelectedAddress);
            CpuData.Drive(_cpuSelectedData);
            return;
        }

        if (wramWindow && WramReadEnabled)
        {
            _cpuReadAddressSelected = true;
            _cpuSelectedAddress = connectorAddress;
            _cpuSelectedData = _wram[connectorAddress & 0x1FFF];
            CpuData.Drive(_cpuSelectedData);
            return;
        }

        _cpuReadAddressSelected = false;
    }

    private void CompleteCpuTransaction()
    {
        if (CpuReadWrite.SampledLevel == DigitalLevel.High)
        {
            if (!_cpuReadAddressSelected) return;
            CpuReadCount++;
            LastCpuReadAddress = _cpuSelectedAddress;
            LastCpuReadData = _cpuSelectedData;
            if (_cpuSelectedAddress < 0x8000) WramReadCount++;
            return;
        }

        if (!CpuData.TrySample(out var rawData)) return;
        var value = (byte)rawData;
        if (_cpuCycleHighRomSelected)
        {
            WriteMapperRegister(_cpuCycleAddress, value);
            return;
        }

        if (_cpuCycleWramSelected && WramWriteEnabled)
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
        JalecoSs88006NametableMode.Horizontal => (address & 0x0800) != 0 ? DigitalLevel.High : DigitalLevel.Low,
        JalecoSs88006NametableMode.Vertical => (address & 0x0400) != 0 ? DigitalLevel.High : DigitalLevel.Low,
        JalecoSs88006NametableMode.SingleScreenPage0 => DigitalLevel.Low,
        JalecoSs88006NametableMode.SingleScreenPage1 => DigitalLevel.High,
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
    private byte ReadChr(ushort address)
    {
        var slot = address >> 10;
        var bank = _chrWindowBanks[slot];
        return _chr[(bank * ChrBankSize) + (address & 0x03FF)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteWram(ushort address, byte value)
    {
        _wram[address & 0x1FFF] = value;
        WramWriteCount++;
    }

    private void WriteMapperRegister(ushort address, byte value)
    {
        var decoded = address & 0x7003;
        var recognized = true;
        switch (decoded)
        {
            case 0x0000: WritePrgNibble(0, high: false, value); break;
            case 0x0001: WritePrgNibble(0, high: true, value); break;
            case 0x0002: WritePrgNibble(1, high: false, value); break;
            case 0x0003: WritePrgNibble(1, high: true, value); break;
            case 0x1000: WritePrgNibble(2, high: false, value); break;
            case 0x1001: WritePrgNibble(2, high: true, value); break;
            case 0x1002:
                _wramProtect = (byte)(value & 0x03);
                break;
            case 0x2000: case 0x2001: case 0x2002: case 0x2003:
            case 0x3000: case 0x3001: case 0x3002: case 0x3003:
            case 0x4000: case 0x4001: case 0x4002: case 0x4003:
            case 0x5000: case 0x5001: case 0x5002: case 0x5003:
                WriteChrDecoded(decoded, value);
                break;
            case 0x6000:
            case 0x6001:
            case 0x6002:
            case 0x6003:
                WriteIrqLatchNibble(decoded & 0x0003, value);
                break;
            case 0x7000:
                _irqCounter = _irqLatch;
                SetIrqAsserted(false);
                break;
            case 0x7001:
                _irqEnabled = (value & 0x01) != 0;
                _irqMode = (byte)(value & 0x0E);
                SetIrqAsserted(false);
                break;
            case 0x7002:
                _nametableMode = (JalecoSs88006NametableMode)(value & 0x03);
                RefreshCiramA10Physical();
                break;
            case 0x7003:
                _sampleControl = value;
                SampleControlWriteCount++;
                if ((value & 0x03) == 0x02)
                {
                    _lastSampleIndex = (value >> 2) & 0x1F;
                    SampleTriggerCount++;
                }
                break;
            default:
                recognized = false;
                break;
        }

        if (!recognized) return;
        MapperWriteCount++;
        LastMapperWriteAddress = address;
        LastMapperWriteData = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WritePrgNibble(int slot, bool high, byte value)
    {
        var shift = high ? 4 : 0;
        var mask = (byte)(0x0F << shift);
        _prgBankRegisters[slot] = (byte)((_prgBankRegisters[slot] & ~mask) | ((value & 0x0F) << shift));
        _prgWindowBanks[slot] = _prgBankRegisters[slot] & _prgBankMask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteChrDecoded(int decoded, byte value)
    {
        var group = ((decoded >> 12) & 0x07) - 2;
        var slot = (group * 2) + ((decoded >> 1) & 0x01);
        var high = (decoded & 0x01) != 0;
        var shift = high ? 4 : 0;
        var mask = (byte)(0x0F << shift);
        _chrBankRegisters[slot] = (byte)((_chrBankRegisters[slot] & ~mask) | ((value & 0x0F) << shift));
        _chrWindowBanks[slot] = _chrBankRegisters[slot] & _chrBankMask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteIrqLatchNibble(int nibble, byte value)
    {
        var shift = nibble * 4;
        _irqLatch = (ushort)((_irqLatch & ~(0x000F << shift)) | ((value & 0x0F) << shift));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort ResolveIrqMask()
    {
        if ((_irqMode & 0x08) != 0) return 0x000F;
        if ((_irqMode & 0x04) != 0) return 0x00FF;
        if ((_irqMode & 0x02) != 0) return 0x0FFF;
        return 0xFFFF;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClockIrqCounter()
    {
        if (!_irqEnabled) return;
        IrqClockCount++;
        var mask = ResolveIrqMask();
        _irqCounter = (ushort)((_irqCounter & ~mask) | ((_irqCounter - 1) & mask));
        if ((_irqCounter & mask) == mask) SetIrqAsserted(true);
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
    internal void WriteCpuHighCompiled(ushort address, byte value) => WriteMapperRegister(address, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadWramCompiled(ushort address)
    {
        var value = _wram[address & 0x1FFF];
        CpuReadCount++;
        WramReadCount++;
        LastCpuReadAddress = address;
        LastCpuReadData = value;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteWramCompiled(ushort address, byte value) => WriteWram(address, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsWramSelectedCompiled(int _, bool write) => write ? WramWriteEnabled : WramReadEnabled;

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
            new[]
            {
                new CompiledPinCondition(CpuReadWrite, DigitalLevel.Low),
                new CompiledPinCondition(CpuRomSelectBar, DigitalLevel.Low)
            },
            CompiledBusReadPhase.Complete,
            address => ReadCpuRomCompiled((ushort)(0x8000 | address)),
            (address, value) => WriteCpuHighCompiled((ushort)(0x8000 | address), value),
            ObserveCompiledCpuBusCycle,
            writePhase: CompiledBusWritePhase.Complete);

        if (_wram.Length != 0)
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
                address => ReadWramCompiled((ushort)address),
                (address, value) => WriteWramCompiled((ushort)address, value),
                isSelected: IsWramSelectedCompiled,
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
                JalecoSs88006NametableMode.Horizontal => new CompiledDriveState(sampleInput(PpuAddress.Pins[11])),
                JalecoSs88006NametableMode.Vertical => new CompiledDriveState(sampleInput(PpuAddress.Pins[10])),
                JalecoSs88006NametableMode.SingleScreenPage0 => new CompiledDriveState(DigitalLevel.Low),
                JalecoSs88006NametableMode.SingleScreenPage1 => new CompiledDriveState(DigitalLevel.High),
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
        return image.HasBatteryBackedMemory ? StandardWramSize : 0;
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
}
