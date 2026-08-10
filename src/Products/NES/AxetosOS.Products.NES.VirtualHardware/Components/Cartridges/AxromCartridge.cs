using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

/// <summary>
/// Mapper-7/AxROM-compatible replaceable cartridge hardware. The cartridge owns
/// its 32 KiB PRG bank latch, mapper-selectable single-screen CIRAM A10 output,
/// 8 KiB CHR RAM and optional board-local CPU/ROM bus-conflict behavior. The
/// motherboard and generic compiler see only connector pins and hardware facets.
/// </summary>
public sealed class AxromCartridge : VirtualHardwareComponent, IReplaceableCartridgeHardware,
    ICompiledBusTargetProvider, ICompiledCombinationalComponent, ICompiledStaticCombinationalComponent
{
    private const int PrgBankSize = 32 * 1024;
    private const int MaximumPrgSize = 256 * 1024;
    private const int StandardChrRamSize = 8 * 1024;

    private byte[] _prg = [];
    private byte[] _chrRam = [];
    private byte _bankRegister;
    private byte _bankSelectMask;
    private int _selectedPrgBankBase;
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

    public AxromCartridge(string componentId) : base(componentId)
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

        // CPU data is sampled by the board-local latch only at the end of an
        // M2-qualified write. PPU data is consumed only by active CHR-RAM writes.
        CpuData.SetOwnerWakeEnabled(false);
        PpuData.SetOwnerWakeEnabled(false);
        ApplyResetState();
    }

    public int MapperNumber => 7;
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
    public int SelectedPrgBank => _prg.Length == 0 ? 0 : _selectedPrgBankBase / PrgBankSize;
    public int SelectedNametablePage => (_bankRegister >> 4) & 0x01;
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
        if (image.MapperNumber != 7)
            throw new NotSupportedException($"Mapper {image.MapperNumber} is not AxROM.");
        if (image.PrgRom.Length < PrgBankSize || image.PrgRom.Length > MaximumPrgSize ||
            image.PrgRom.Length % PrgBankSize != 0)
            throw new ArgumentException(
                "AxROM PRG ROM must contain one to eight whole 32 KiB banks (maximum 256 KiB).", nameof(image));

        var prgBankCount = image.PrgRom.Length / PrgBankSize;
        if ((prgBankCount & (prgBankCount - 1)) != 0)
            throw new NotSupportedException(
                "AxROM PRG ROM must expose a power-of-two number of 32 KiB banks so latch outputs map directly to ROM address pins.");
        if (image.ChrRom.Length != 0)
            throw new NotSupportedException("AxROM cartridge hardware uses CHR RAM; CHR-ROM mapper-7 variants require distinct board hardware.");
        if (image.Mirroring == VirtualHardwareNesMirroring.FourScreen)
            throw new NotSupportedException("AxROM uses mapper-selected single-screen CIRAM; four-screen boards require distinct cartridge hardware.");
        if (image.HasBatteryBackedMemory)
            throw new NotSupportedException("Standard AxROM hardware has no battery-backed cartridge memory.");
        if (image.HasExplicitRamSizes && image.TotalPrgRamSizeBytes != 0)
            throw new NotSupportedException("Standard AxROM hardware has no PRG RAM/NVRAM chip.");
        if (image.HasExplicitRamSizes && image.ChrNvRamSizeBytes > 0)
            throw new NotSupportedException("Standard AxROM uses volatile CHR RAM rather than CHR NVRAM.");

        var chrRamSize = image.HasExplicitRamSizes ? image.TotalChrRamSizeBytes : StandardChrRamSize;
        if (chrRamSize != StandardChrRamSize)
            throw new NotSupportedException(
                $"Standard AxROM requires one {StandardChrRamSize:N0}-byte CHR RAM chip; the image declares {chrRamSize:N0} bytes.");

        _busConflictsEnabled = ResolveBusConflictBehavior(image);
        _bankSelectMask = (byte)(prgBankCount - 1);
        _prg = image.PrgRom.ToArray();
        _chrRam = new byte[StandardChrRamSize];
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
        // Mapper 7's legacy/default convention is no bus conflicts because ANROM
        // games exist that depend on ROM being disabled during writes.
        null => false,
        0 => false,
        1 => false,
        2 => true,
        _ => throw new NotSupportedException(
            $"Mapper 7 submapper {image.SubmapperNumber} is not defined by the current AxROM cartridge hardware.")
    };

    private bool IsPowered() =>
        IsInserted && Vcc.SampledLevel == DigitalLevel.High && Gnd.SampledLevel == DigitalLevel.Low;

    private void ApplyResetState()
    {
        _bankRegister = 0;
        RefreshDecodedBankBase();
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
        CpuReadCount = 0;
        LastCpuReadAddress = 0;
        LastCpuReadData = 0;
        PpuReadCount = 0;
        PpuWriteCount = 0;
        RefreshPpuDataWakeState();
        ReleaseOutputs();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RefreshDecodedBankBase()
    {
        _selectedPrgBankBase = _prg.Length == 0
            ? 0
            : (_bankRegister & _bankSelectMask) * PrgBankSize;
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

        // The falling M2 connector edge clocks the board latch while address,
        // R/W and CPU data still represent the transaction that just completed.
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

        // CIRAM A10 is driven directly by the mapper latch, independent of the
        // current PPU address. /CIRAM-CE remains the A13-derived connector signal.
        DriveCiramA10();
        if (!addressKnown)
        {
            CiramChipEnableBar.Drive(DigitalLevel.High);
            PpuData.Release();
            _ppuReadActive = false;
            _ppuWriteActive = false;
            return;
        }

        var address = (ushort)(rawAddress & 0x3FFF);
        CiramChipEnableBar.Drive((address & 0x2000) != 0 ? DigitalLevel.Low : DigitalLevel.High);

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
    private void DriveCiramA10() =>
        CiramA10.Drive(SelectedNametablePage == 0 ? DigitalLevel.Low : DigitalLevel.High);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadPrg(ushort address) =>
        _prg[_selectedPrgBankBase + (address & 0x7FFF)];

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

        // AxROM physically connects latch outputs D0..D2 to PRG bank address
        // lines and D4 to CIRAM A10. Unpopulated PRG address lines are masked by
        // the fitted ROM capacity rather than modulo-normalized in software.
        _bankRegister = (byte)(effectiveData & (0x10 | _bankSelectMask));
        RefreshDecodedBankBase();
        if (IsPowered()) DriveCiramA10();
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
        // /CIRAM-CE is a pure connector-level function of PPU A13. CIRAM A10
        // is intentionally excluded because it follows the live AxROM latch.
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
            drive = new CompiledDriveState(SelectedNametablePage == 0 ? DigitalLevel.Low : DigitalLevel.High);
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
}
