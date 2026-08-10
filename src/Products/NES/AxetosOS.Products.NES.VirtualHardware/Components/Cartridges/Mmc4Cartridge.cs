using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

/// <summary>
/// Mapper 10 / Nintendo MMC4 / FxROM cartridge hardware. MMC4 presents a
/// switchable 16 KiB PRG-ROM window at $8000-$BFFF, the final 16 KiB PRG
/// bank fixed at $C000-$FFFF, and a fixed 8 KiB PRG-RAM window at
/// $6000-$7FFF. Two 4 KiB CHR-ROM pattern-table windows each select between
/// FD/FE bank registers. PPU reads in the MMC4 FD/FE trigger ranges clock the
/// corresponding package latch after the current CHR read. Mirroring is
/// mapper-controlled; there is no IRQ, CHR RAM, or CPU/ROM bus conflict.
/// </summary>
public sealed class Mmc4Cartridge : VirtualHardwareComponent, IReplaceableCartridgeHardware,
    ICompiledBusTargetProvider, ICompiledCombinationalComponent, ICompiledStaticCombinationalComponent
{
    private const int PrgBankSize = 16 * 1024;
    private const int PrgRamSize = 8 * 1024;
    private const int ChrBankSize = 4 * 1024;
    private const byte LatchFd = 0xFD;
    private const byte LatchFe = 0xFE;

    private byte[] _prg = [];
    private byte[] _chr = [];
    private byte[] _prgRam = [];
    private byte _prgBankMask;
    private byte _chrBankMask;
    private byte _prgBankRegister;
    private readonly byte[] _chrBankRegisters = new byte[4];
    private byte _latch0 = LatchFe;
    private byte _latch1 = LatchFe;
    private VirtualHardwareNesMirroring _mirroring;

    private bool _cpuReadAddressSelected;
    private ushort _cpuSelectedAddress;
    private byte _cpuSelectedData;
    private bool _cpuCycleRomSelected;
    private bool _cpuCyclePrgRamSelected;
    private ushort _cpuCycleAddress;
    private bool _ppuReadActive;
    private ushort _ppuReadAddress;

    private readonly ulong _powerInputMask;
    private readonly ulong _cpuAddressControlInputMask;
    private readonly ulong _cpuM2InputMask;
    private readonly ulong _cpuRomSelectInputMask;
    private readonly ulong _ppuAddressControlInputMask;

    public Mmc4Cartridge(string componentId) : base(componentId)
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

    public int MapperNumber => 10;
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
    public int PrgBankCount => _prg.Length / PrgBankSize;
    public int ChrBankCount => _chr.Length / ChrBankSize;
    public int PrgRamSizeBytes => _prgRam.Length;
    public byte PrgBankRegister => _prgBankRegister;
    public IReadOnlyList<byte> ChrBankRegisters => _chrBankRegisters;
    public byte Latch0 => _latch0;
    public byte Latch1 => _latch1;
    public VirtualHardwareNesMirroring Mirroring => _mirroring;
    public int SelectedPrgBank => _prg.Length == 0 ? 0 : _prgBankRegister & _prgBankMask;
    public int SelectedChrBank0 => _chr.Length == 0 ? 0 : ActiveChrRegister0 & _chrBankMask;
    public int SelectedChrBank1 => _chr.Length == 0 ? 0 : ActiveChrRegister1 & _chrBankMask;

    public ulong MapperWriteCount { get; private set; }
    public ulong LatchTriggerCount { get; private set; }
    public ulong Latch0FdTriggerCount { get; private set; }
    public ulong Latch0FeTriggerCount { get; private set; }
    public ulong Latch1FdTriggerCount { get; private set; }
    public ulong Latch1FeTriggerCount { get; private set; }
    public ushort LastMapperWriteAddress { get; private set; }
    public byte LastMapperWriteData { get; private set; }
    public ushort LastLatchTriggerAddress { get; private set; }
    public ulong CpuReadCount { get; private set; }
    public ushort LastCpuReadAddress { get; private set; }
    public byte LastCpuReadData { get; private set; }
    public ulong PpuReadCount { get; private set; }
    public ulong PpuWriteCount { get; private set; }
    public ulong PrgRamWriteCount { get; private set; }

    private byte ActiveChrRegister0 => _latch0 == LatchFd ? _chrBankRegisters[0] : _chrBankRegisters[1];
    private byte ActiveChrRegister1 => _latch1 == LatchFd ? _chrBankRegisters[2] : _chrBankRegisters[3];

    public void LoadImage(VirtualHardwareNesRomImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.MapperNumber != 10)
            throw new NotSupportedException($"Mapper {image.MapperNumber} is not MMC4/FxROM hardware.");
        if (image.SubmapperNumber is > 0)
            throw new NotSupportedException($"Mapper 10 submapper {image.SubmapperNumber} is not defined for Nintendo MMC4/FxROM hardware.");
        if (image.Mirroring == VirtualHardwareNesMirroring.FourScreen)
            throw new NotSupportedException("MMC4/FxROM does not provide four-screen nametable RAM.");
        if (image.PrgRom.Length < 2 * PrgBankSize || image.PrgRom.Length % PrgBankSize != 0)
            throw new ArgumentException("MMC4 PRG ROM must contain at least two whole 16 KiB banks.", nameof(image));
        var prgBanks = image.PrgRom.Length / PrgBankSize;
        if (prgBanks > 16 || !IsPowerOfTwo(prgBanks))
            throw new NotSupportedException("MMC4 exposes a power-of-two PRG-ROM population of at most sixteen 16 KiB banks (256 KiB).");
        if (image.ChrRom.Length < 2 * ChrBankSize || image.ChrRom.Length % ChrBankSize != 0)
            throw new NotSupportedException("MMC4/FxROM requires CHR ROM in whole 4 KiB banks; CHR-RAM boards are distinct hardware.");
        var chrBanks = image.ChrRom.Length / ChrBankSize;
        if (chrBanks > 32 || !IsPowerOfTwo(chrBanks))
            throw new NotSupportedException("MMC4 exposes a power-of-two CHR-ROM population of at most thirty-two 4 KiB banks (128 KiB).");
        var prgRamSize = image.HasExplicitRamSizes ? image.TotalPrgRamSizeBytes : PrgRamSize;
        if (prgRamSize != PrgRamSize)
            throw new NotSupportedException($"MMC4/FxROM requires exactly {PrgRamSize:N0} bytes of PRG RAM/NVRAM; image declares {prgRamSize:N0} bytes.");
        if (image.HasExplicitRamSizes && image.TotalChrRamSizeBytes != 0)
            throw new NotSupportedException("MMC4/FxROM has no CHR RAM.");

        _prg = image.PrgRom.ToArray();
        _chr = image.ChrRom.ToArray();
        _prgRam = new byte[PrgRamSize];
        _prgBankMask = (byte)(prgBanks - 1);
        _chrBankMask = (byte)(chrBanks - 1);
        _mirroring = image.Mirroring;
        IsInserted = true;
        ApplyResetState();
    }

    public void Eject()
    {
        IsInserted = false;
        _prg = [];
        _chr = [];
        _prgRam = [];
        _cpuReadAddressSelected = false;
        _cpuCycleRomSelected = false;
        _cpuCyclePrgRamSelected = false;
        _ppuReadActive = false;
        ReleaseOutputs();
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    private bool IsPowered() =>
        IsInserted && Vcc.SampledLevel == DigitalLevel.High && Gnd.SampledLevel == DigitalLevel.Low;

    private void ApplyResetState()
    {
        _prgBankRegister = 0;
        Array.Clear(_chrBankRegisters);
        // MMC4 has no documented reset pin/state for these latches. AxetosOS
        // uses FE as a deterministic power-on observation while exposing every
        // subsequent transition through the physical PPU address triggers.
        _latch0 = LatchFe;
        _latch1 = LatchFe;
        _cpuReadAddressSelected = false;
        _cpuSelectedAddress = 0;
        _cpuSelectedData = 0;
        _cpuCycleRomSelected = false;
        _cpuCyclePrgRamSelected = false;
        _cpuCycleAddress = 0;
        _ppuReadActive = false;
        _ppuReadAddress = 0;
        MapperWriteCount = 0;
        LatchTriggerCount = 0;
        Latch0FdTriggerCount = 0;
        Latch0FeTriggerCount = 0;
        Latch1FdTriggerCount = 0;
        Latch1FeTriggerCount = 0;
        LastMapperWriteAddress = 0;
        LastMapperWriteData = 0;
        LastLatchTriggerAddress = 0;
        CpuReadCount = 0;
        LastCpuReadAddress = 0;
        LastCpuReadData = 0;
        PpuReadCount = 0;
        PpuWriteCount = 0;
        PrgRamWriteCount = 0;
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
            _cpuCycleRomSelected = false;
            _cpuCyclePrgRamSelected = false;
            _ppuReadActive = false;
            ReleaseOutputs();
            return;
        }

        if (powerChanged || ppuAddressOrControlChanged) ProcessPpuPort();

        if (!powerChanged && cpuM2Changed && CpuM2.SampledLevel == DigitalLevel.Low)
            CompleteCpuTransaction();

        if (powerChanged || cpuAddressOrControlChanged || cpuRomSelectChanged || cpuM2Changed)
            UpdateCpuPort();
    }

    private void UpdateCpuPort()
    {
        if (!CpuAddress.TrySample(out var rawAddress))
        {
            _cpuReadAddressSelected = false;
            _cpuCycleRomSelected = false;
            _cpuCyclePrgRamSelected = false;
            CpuData.Release();
            return;
        }

        var connectorAddress = (ushort)(rawAddress & 0x7FFF);
        var m2High = CpuM2.SampledLevel == DigitalLevel.High;
        var romSelected = m2High && CpuRomSelectBar.SampledLevel == DigitalLevel.Low;
        var prgRamSelected = m2High
            && CpuRomSelectBar.SampledLevel == DigitalLevel.High
            && (connectorAddress & 0x6000) == 0x6000
            && _prgRam.Length != 0;

        _cpuCycleRomSelected = romSelected;
        _cpuCyclePrgRamSelected = prgRamSelected;
        _cpuCycleAddress = romSelected
            ? (ushort)(0x8000 | connectorAddress)
            : connectorAddress;
        _cpuReadAddressSelected = CpuReadWrite.SampledLevel == DigitalLevel.High
            && (romSelected || prgRamSelected);
        if (!_cpuReadAddressSelected)
        {
            CpuData.Release();
            return;
        }

        _cpuSelectedAddress = _cpuCycleAddress;
        _cpuSelectedData = romSelected
            ? ReadPrg(_cpuSelectedAddress)
            : _prgRam[connectorAddress & 0x1FFF];
        CpuData.Drive(_cpuSelectedData);
    }

    private void CompleteCpuTransaction()
    {
        if (CpuReadWrite.SampledLevel == DigitalLevel.High)
        {
            if (!_cpuReadAddressSelected) return;
            CpuReadCount++;
            LastCpuReadAddress = _cpuSelectedAddress;
            LastCpuReadData = _cpuSelectedData;
            return;
        }

        if (!CpuData.TrySample(out var rawData)) return;
        var value = (byte)rawData;
        if (_cpuCyclePrgRamSelected)
        {
            _prgRam[_cpuCycleAddress & 0x1FFF] = value;
            PrgRamWriteCount++;
            return;
        }

        if (_cpuCycleRomSelected) WriteMapperRegister(_cpuCycleAddress, value);
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
        if (newRead)
        {
            PpuReadCount++;
            ApplyLatchTrigger(address);
        }
        _ppuReadAddress = address;
        _ppuReadActive = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DriveCiramOutputs(ushort address)
    {
        CiramChipEnableBar.Drive((address & 0x2000) != 0 ? DigitalLevel.Low : DigitalLevel.High);
        var sourceBit = _mirroring == VirtualHardwareNesMirroring.Horizontal ? 11 : 10;
        CiramA10.Drive((address & (1 << sourceBit)) != 0 ? DigitalLevel.High : DigitalLevel.Low);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RefreshCiramA10Physical()
    {
        if (!IsPowered() || !PpuAddress.TrySample(out var rawAddress)) return;
        var address = (ushort)(rawAddress & 0x3FFF);
        var sourceBit = _mirroring == VirtualHardwareNesMirroring.Horizontal ? 11 : 10;
        CiramA10.Drive((address & (1 << sourceBit)) != 0 ? DigitalLevel.High : DigitalLevel.Low);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadPrg(ushort address)
    {
        var bank = address < 0xC000
            ? (_prgBankRegister & _prgBankMask)
            : ((_prg.Length / PrgBankSize) - 1);
        return _prg[(bank * PrgBankSize) + (address & 0x3FFF)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadChr(ushort address)
    {
        var bank = (address & 0x1000) == 0 ? ActiveChrRegister0 : ActiveChrRegister1;
        return _chr[((bank & _chrBankMask) * ChrBankSize) + (address & 0x0FFF)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteMapperRegister(ushort address, byte value)
    {
        if (address < 0xA000) return;

        switch (address >> 12)
        {
            case 0xA:
                _prgBankRegister = (byte)(value & 0x0F);
                break;
            case 0xB:
                _chrBankRegisters[0] = (byte)(value & 0x1F);
                break;
            case 0xC:
                _chrBankRegisters[1] = (byte)(value & 0x1F);
                break;
            case 0xD:
                _chrBankRegisters[2] = (byte)(value & 0x1F);
                break;
            case 0xE:
                _chrBankRegisters[3] = (byte)(value & 0x1F);
                break;
            case 0xF:
                _mirroring = (value & 0x01) == 0
                    ? VirtualHardwareNesMirroring.Vertical
                    : VirtualHardwareNesMirroring.Horizontal;
                RefreshCiramA10Physical();
                break;
            default:
                return;
        }

        MapperWriteCount++;
        LastMapperWriteAddress = address;
        LastMapperWriteData = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ApplyLatchTrigger(ushort address)
    {
        byte next;
        int latch;
        if (address is >= 0x0FD8 and <= 0x0FDF)
        {
            latch = 0;
            next = LatchFd;
            Latch0FdTriggerCount++;
        }
        else if (address is >= 0x0FE8 and <= 0x0FEF)
        {
            latch = 0;
            next = LatchFe;
            Latch0FeTriggerCount++;
        }
        else if (address is >= 0x1FD8 and <= 0x1FDF)
        {
            latch = 1;
            next = LatchFd;
            Latch1FdTriggerCount++;
        }
        else if (address is >= 0x1FE8 and <= 0x1FEF)
        {
            latch = 1;
            next = LatchFe;
            Latch1FeTriggerCount++;
        }
        else return;

        if (latch == 0) _latch0 = next;
        else _latch1 = next;
        LatchTriggerCount++;
        LastLatchTriggerAddress = address;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadCpuCompiled(ushort address)
    {
        var value = address >= 0x8000
            ? ReadPrg(address)
            : _prgRam[address & 0x1FFF];
        CpuReadCount++;
        LastCpuReadAddress = address;
        LastCpuReadData = value;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteCpuCompiled(ushort address, byte value)
    {
        if (address >= 0x8000)
        {
            WriteMapperRegister(address, value);
            return;
        }

        _prgRam[address & 0x1FFF] = value;
        PrgRamWriteCount++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadPpuCompiled(ushort address)
    {
        var value = ReadChr(address);
        PpuReadCount++;
        ApplyLatchTrigger(address);
        return value;
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
            address => ReadCpuCompiled((ushort)(0x8000 | address)),
            (address, value) => WriteCpuCompiled((ushort)(0x8000 | address), value),
            writePhase: CompiledBusWritePhase.Complete);

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
            address => ReadCpuCompiled((ushort)address),
            (address, value) => WriteCpuCompiled((ushort)address, value),
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

        // MMC4 mirroring is mapper-controlled, so CIRAM A10 is intentionally
        // not exposed as a static foldable route.
        if (ReferenceEquals(output, IrqBar))
        {
            drive = new CompiledDriveState(DigitalLevel.HighImpedance);
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
            var sourceBit = _mirroring == VirtualHardwareNesMirroring.Horizontal ? 11 : 10;
            drive = new CompiledDriveState(sampleInput(PpuAddress.Pins[sourceBit]));
            return true;
        }

        return ((ICompiledStaticCombinationalComponent)this)
            .TryEvaluateCompiledStaticOutput(output, sampleInput, out drive);
    }
}
