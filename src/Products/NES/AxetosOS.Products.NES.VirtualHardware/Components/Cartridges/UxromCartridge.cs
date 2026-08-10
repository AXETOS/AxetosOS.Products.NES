using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

/// <summary>
/// Mapper-2/UxROM-compatible replaceable cartridge hardware. The cartridge owns
/// its PRG bank latch, fixed-last-bank decode, CHR RAM, fixed nametable wiring
/// and optional board-local CPU/ROM bus-conflict behavior. The motherboard and
/// generic hardware compiler see only connector pins and generic hardware facets.
/// </summary>
public sealed class UxromCartridge : VirtualHardwareComponent, IReplaceableCartridgeHardware,
    ICompiledBusTargetProvider, ICompiledCombinationalComponent, ICompiledStaticCombinationalComponent
{
    private const int PrgBankSize = 16 * 1024;
    private const int StandardChrRamSize = 8 * 1024;

    private byte[] _prg = [];
    private byte[] _chrRam = [];
    private VirtualHardwareNesMirroring _mirroring;
    private byte _bankRegister;
    private byte _bankSelectMask;
    private int _switchableBankBase;
    private int _fixedBankBase;
    private bool _busConflictsEnabled;

    private bool _cpuReadAddressSelected;
    private ushort _cpuSelectedAddress;
    private byte _cpuSelectedData;
    private bool _cpuWriteCycleSelected;
    private ushort _cpuWriteAddress;
    private bool _ppuReadActive;
    private bool _ppuWriteActive;

    private readonly ulong _powerInputMask;
    private readonly ulong _cpuAddressControlInputMask;
    private readonly ulong _cpuM2InputMask;
    private readonly ulong _cpuRomSelectInputMask;
    private readonly ulong _ppuAddressControlInputMask;
    private readonly ulong _ppuDataInputMask;

    public UxromCartridge(string componentId) : base(componentId)
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
        _ppuDataInputMask = PpuData.InputChangeMask;

        // CPU data is sampled by the bank latch only at the end of an
        // M2-qualified write. PPU data is consumed only by active CHR-RAM writes.
        // The connector pins still retain every delivered level while owner wake
        // remains disabled for transitions that cannot clock internal hardware.
        CpuData.SetOwnerWakeEnabled(false);
        PpuData.SetOwnerWakeEnabled(false);
        ApplyResetState();
    }

    public int MapperNumber => 2;
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
    public bool IsChrRam => true;
    public int ChrRamSizeBytes => _chrRam.Length;
    public byte BankRegister => _bankRegister;
    public int SelectedPrgBank => _prg.Length == 0 ? 0 : _switchableBankBase / PrgBankSize;
    public int FixedPrgBank => _prg.Length == 0 ? 0 : _fixedBankBase / PrgBankSize;
    public bool BusConflictsEnabled => _busConflictsEnabled;
    public ulong MapperWriteCount { get; private set; }
    public ulong BusConflictModifiedWriteCount { get; private set; }
    public ushort LastMapperWriteAddress { get; private set; }
    public byte LastMapperWriteData { get; private set; }
    public byte LastEffectiveMapperWriteData { get; private set; }

    public ulong CpuReadCount { get; private set; }
    public ushort LastCpuReadAddress { get; private set; }
    public byte LastCpuReadData { get; private set; }
    public ulong PpuReadCount { get; private set; }
    public ulong PpuWriteCount { get; private set; }

    public void LoadImage(VirtualHardwareNesRomImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.MapperNumber != 2)
            throw new NotSupportedException($"Mapper {image.MapperNumber} is not UxROM.");
        if (image.PrgRom.Length < 2 * PrgBankSize || image.PrgRom.Length % PrgBankSize != 0)
            throw new ArgumentException("UxROM PRG ROM must contain whole 16 KiB banks and at least 32 KiB.", nameof(image));
        var prgBankCount = image.PrgRom.Length / PrgBankSize;
        if (prgBankCount > 256)
            throw new NotSupportedException("UxROM bank-select wiring supports at most 256 16 KiB PRG banks.");
        if ((prgBankCount & (prgBankCount - 1)) != 0)
            throw new NotSupportedException("UxROM PRG ROM must expose a power-of-two number of 16 KiB banks so bank-select lines map directly to ROM address pins.");
        if (image.ChrRom.Length != 0)
            throw new NotSupportedException("UxROM cartridge hardware uses CHR RAM; CHR-ROM mapper-2 variants require distinct board hardware.");
        if (image.Mirroring == VirtualHardwareNesMirroring.FourScreen)
            throw new NotSupportedException("UxROM four-screen boards require additional cartridge nametable RAM hardware.");
        if (image.HasBatteryBackedMemory)
            throw new NotSupportedException("Standard UxROM hardware has no battery-backed cartridge memory.");
        if (image.HasExplicitRamSizes && image.TotalPrgRamSizeBytes != 0)
            throw new NotSupportedException("Standard UxROM hardware has no PRG RAM/NVRAM chip.");
        if (image.HasExplicitRamSizes && image.ChrNvRamSizeBytes > 0)
            throw new NotSupportedException("Standard UxROM uses volatile CHR RAM rather than CHR NVRAM.");

        var chrRamSize = image.HasExplicitRamSizes ? image.TotalChrRamSizeBytes : StandardChrRamSize;
        if (chrRamSize != StandardChrRamSize)
            throw new NotSupportedException(
                $"Standard UxROM requires one {StandardChrRamSize:N0}-byte CHR RAM chip; the image declares {chrRamSize:N0} bytes.");

        _busConflictsEnabled = ResolveBusConflictBehavior(image);
        _bankSelectMask = (byte)(prgBankCount - 1);
        _prg = image.PrgRom.ToArray();
        _chrRam = new byte[StandardChrRamSize];
        _mirroring = image.Mirroring;
        IsInserted = true;
        ApplyResetState();
        RefreshPpuDataWakeState();
    }

    public void Eject()
    {
        IsInserted = false;
        _prg = [];
        _chrRam = [];
        _cpuReadAddressSelected = false;
        _cpuSelectedAddress = 0;
        _cpuSelectedData = 0;
        _cpuWriteCycleSelected = false;
        _cpuWriteAddress = 0;
        _ppuReadActive = false;
        _ppuWriteActive = false;
        RefreshPpuDataWakeState();
        ReleaseOutputs();
    }

    private static bool ResolveBusConflictBehavior(VirtualHardwareNesRomImage image) => image.SubmapperNumber switch
    {
        null => true,
        0 => true,
        1 => false,
        2 => true,
        _ => throw new NotSupportedException(
            $"Mapper 2 submapper {image.SubmapperNumber} is not defined by the current UxROM cartridge hardware.")
    };

    private bool IsPowered() =>
        IsInserted && Vcc.SampledLevel == DigitalLevel.High && Gnd.SampledLevel == DigitalLevel.Low;

    private void ApplyResetState()
    {
        _bankRegister = 0;
        RefreshDecodedBankBases();
        _cpuReadAddressSelected = false;
        _cpuSelectedAddress = 0;
        _cpuSelectedData = 0;
        _cpuWriteCycleSelected = false;
        _cpuWriteAddress = 0;
        _ppuReadActive = false;
        _ppuWriteActive = false;
        MapperWriteCount = 0;
        BusConflictModifiedWriteCount = 0;
        LastMapperWriteAddress = 0;
        LastMapperWriteData = 0;
        LastEffectiveMapperWriteData = 0;
        RefreshPpuDataWakeState();
        ReleaseOutputs();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RefreshDecodedBankBases()
    {
        if (_prg.Length == 0)
        {
            _switchableBankBase = 0;
            _fixedBankBase = 0;
            return;
        }

        var bankCount = _prg.Length / PrgBankSize;
        _switchableBankBase = (_bankRegister & _bankSelectMask) * PrgBankSize;
        _fixedBankBase = (bankCount - 1) * PrgBankSize;
    }

    private void RefreshPpuDataWakeState()
    {
        var enabled = IsPowered() && PpuWriteBar.SampledLevel == DigitalLevel.Low;
        PpuData.SetOwnerWakeEnabled(enabled);
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

        if (!powerChanged && !cpuAddressOrControlChanged && !cpuM2Changed &&
            !cpuRomSelectChanged && !ppuAddressOrControlChanged && !ppuDataChanged)
            return;

        if (!IsPowered())
        {
            if (!powerChanged && !IsInserted) return;
            _cpuReadAddressSelected = false;
            _cpuWriteCycleSelected = false;
            _ppuReadActive = false;
            _ppuWriteActive = false;
            ReleaseOutputs();
            RefreshPpuDataWakeState();
            return;
        }

        if (powerChanged || ppuAddressOrControlChanged) RefreshPpuDataWakeState();
        var ppuDataCanMatter = ppuDataChanged && PpuWriteBar.SampledLevel == DigitalLevel.Low;
        if (powerChanged || ppuAddressOrControlChanged || ppuDataCanMatter)
            ProcessPpuPort();

        // The falling M2 connector edge completes the preceding CPU bus window
        // while address, R/W and CPU data still represent that transaction.
        if (!powerChanged && cpuM2Changed && CpuM2.SampledLevel == DigitalLevel.Low)
            CompleteCpuTransaction();

        if (powerChanged || cpuAddressOrControlChanged || cpuRomSelectChanged || cpuM2Changed)
            UpdateCpuPort();
    }

    private void UpdateCpuPort()
    {
        if (!CpuAddress.TrySample(out var rawAddress) || CpuRomSelectBar.SampledLevel != DigitalLevel.Low)
        {
            _cpuReadAddressSelected = false;
            if (CpuM2.SampledLevel == DigitalLevel.High) _cpuWriteCycleSelected = false;
            CpuData.Release();
            return;
        }

        var logicalAddress = (ushort)(0x8000 | rawAddress);
        if (CpuReadWrite.SampledLevel == DigitalLevel.High)
        {
            _cpuWriteCycleSelected = false;
            _cpuReadAddressSelected = true;
            _cpuSelectedAddress = logicalAddress;
            _cpuSelectedData = ReadPrg(logicalAddress);
            CpuData.Drive(_cpuSelectedData);
            return;
        }

        _cpuReadAddressSelected = false;
        CpuData.Release();
        if (CpuM2.SampledLevel == DigitalLevel.High)
        {
            _cpuWriteCycleSelected = true;
            _cpuWriteAddress = logicalAddress;
        }
    }

    private void CompleteCpuTransaction()
    {
        if (_cpuReadAddressSelected)
        {
            CpuReadCount++;
            LastCpuReadAddress = _cpuSelectedAddress;
            LastCpuReadData = _cpuSelectedData;
        }

        if (_cpuWriteCycleSelected && CpuData.TrySample(out var rawData))
            WriteMapperRegister(_cpuWriteAddress, (byte)rawData);

        _cpuWriteCycleSelected = false;
    }

    private void ProcessPpuPort()
    {
        var addressKnown = PpuAddress.TrySample(out var rawAddress);
        var readSelected = false;
        var writeSelected = false;

        if (!addressKnown)
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

        if (address < 0x2000)
        {
            readSelected = PpuReadBar.SampledLevel == DigitalLevel.Low;
            writeSelected = PpuWriteBar.SampledLevel == DigitalLevel.Low;
            if (readSelected)
            {
                PpuData.Drive(_chrRam[address & 0x1FFF]);
                if (!_ppuReadActive) PpuReadCount++;
            }
            else
            {
                PpuData.Release();
                if (writeSelected && PpuData.TrySample(out var data) && !_ppuWriteActive)
                {
                    _chrRam[address & 0x1FFF] = (byte)data;
                    PpuWriteCount++;
                }
            }
        }
        else
        {
            PpuData.Release();
        }

        _ppuReadActive = readSelected;
        _ppuWriteActive = writeSelected;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DriveCiramOutputs(ushort ppuAddress)
    {
        CiramChipEnableBar.Drive((ppuAddress & 0x2000) != 0 ? DigitalLevel.Low : DigitalLevel.High);
        var sourceBit = _mirroring == VirtualHardwareNesMirroring.Horizontal ? 11 : 10;
        CiramA10.Drive((ppuAddress & (1 << sourceBit)) != 0 ? DigitalLevel.High : DigitalLevel.Low);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadPrg(ushort address)
    {
        var baseAddress = address < 0xC000 ? _switchableBankBase : _fixedBankBase;
        return _prg[baseAddress + (address & 0x3FFF)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteMapperRegister(ushort address, byte cpuData)
    {
        var effectiveData = cpuData;
        if (_busConflictsEnabled)
        {
            effectiveData = (byte)(cpuData & ReadPrg(address));
            if (effectiveData != cpuData) BusConflictModifiedWriteCount++;
        }

        MapperWriteCount++;
        LastMapperWriteAddress = address;
        LastMapperWriteData = cpuData;
        LastEffectiveMapperWriteData = effectiveData;
        // Only physically connected latch outputs can reach PRG address pins.
        // The mask is derived once from the fitted power-of-two ROM capacity.
        _bankRegister = (byte)(effectiveData & _bankSelectMask);
        RefreshDecodedBankBases();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadCpuCompiled(ushort address)
    {
        var value = ReadPrg(address);
        CpuReadCount++;
        LastCpuReadAddress = address;
        LastCpuReadData = value;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteCpuCompiled(ushort address, byte value) => WriteMapperRegister(address, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadPpuCompiled(ushort address)
    {
        PpuReadCount++;
        return _chrRam[address & 0x1FFF];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WritePpuCompiled(ushort address, byte value)
    {
        _chrRam[address & 0x1FFF] = value;
        PpuWriteCount++;
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
            PpuAddress.Pins,
            PpuData.Pins,
            new[]
            {
                new CompiledPinCondition(PpuAddress.Pins[13], DigitalLevel.Low),
                new CompiledPinCondition(PpuReadBar, DigitalLevel.Low)
            },
            new[]
            {
                new CompiledPinCondition(PpuAddress.Pins[13], DigitalLevel.Low),
                new CompiledPinCondition(PpuWriteBar, DigitalLevel.Low)
            },
            CompiledBusReadPhase.Complete,
            address => ReadPpuCompiled((ushort)address),
            (address, value) => WritePpuCompiled((ushort)address, value));
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

        if (ReferenceEquals(output, CiramA10))
        {
            var sourceBit = _mirroring == VirtualHardwareNesMirroring.Horizontal ? 11 : 10;
            drive = new CompiledDriveState(sampleInput(PpuAddress.Pins[sourceBit]));
            return true;
        }

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
        out CompiledDriveState drive) =>
        ((ICompiledStaticCombinationalComponent)this).TryEvaluateCompiledStaticOutput(output, sampleInput, out drive);
}
