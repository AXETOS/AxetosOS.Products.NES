using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

/// <summary>
/// Mapper-3/CNROM-compatible replaceable cartridge hardware. The cartridge owns
/// a fixed 32 KiB PRG ROM, an end-of-M2 CHR-bank latch, banked 8 KiB CHR-ROM
/// window, fixed nametable wiring and optional board-local CPU/ROM bus-conflict
/// behavior. Motherboards and the generic hardware compiler see only connector
/// pins plus product-agnostic compilable hardware facets.
/// </summary>
public sealed class CnromCartridge : VirtualHardwareComponent, IReplaceableCartridgeHardware,
    ICompiledBusTargetProvider, ICompiledCombinationalComponent, ICompiledStaticCombinationalComponent
{
    private const int PrgSize = 32 * 1024;
    private const int ChrBankSize = 8 * 1024;
    private const int MaximumChrSize = 32 * 1024;

    private byte[] _prg = [];
    private byte[] _chr = [];
    private VirtualHardwareNesMirroring _mirroring;
    private byte _bankRegister;
    private byte _bankSelectMask;
    private int _selectedChrBankBase;
    private bool _busConflictsEnabled;

    private bool _cpuReadAddressSelected;
    private ushort _cpuSelectedAddress;
    private byte _cpuSelectedData;
    private bool _cpuWriteCycleSelected;
    private ushort _cpuWriteAddress;
    private bool _ppuReadActive;

    private readonly ulong _powerInputMask;
    private readonly ulong _cpuAddressControlInputMask;
    private readonly ulong _cpuM2InputMask;
    private readonly ulong _cpuRomSelectInputMask;
    private readonly ulong _ppuAddressControlInputMask;

    public CnromCartridge(string componentId) : base(componentId)
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

        // CPU data reaches only the cartridge bank latch and is sampled at the
        // end of an M2-qualified write. CHR is ROM, so PPU data is never an
        // internal input. Connector pins still retain every delivered level.
        CpuData.SetOwnerWakeEnabled(false);
        PpuData.SetOwnerWakeEnabled(false);
        ApplyResetState();
    }

    public int MapperNumber => 3;
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
    public byte BankRegister => _bankRegister;
    public int SelectedChrBank => _chr.Length == 0 ? 0 : _selectedChrBankBase / ChrBankSize;
    public int ChrBankCount => _chr.Length / ChrBankSize;
    public int ChrRomSizeBytes => _chr.Length;
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
        if (image.MapperNumber != 3)
            throw new NotSupportedException($"Mapper {image.MapperNumber} is not CNROM.");
        if (image.PrgRom.Length != PrgSize)
            throw new ArgumentException("Standard CNROM requires one fixed 32 KiB PRG ROM.", nameof(image));
        if (image.ChrRom.Length < ChrBankSize || image.ChrRom.Length > MaximumChrSize ||
            image.ChrRom.Length % ChrBankSize != 0)
            throw new ArgumentException("CNROM CHR ROM must contain one to four whole 8 KiB banks.", nameof(image));

        var chrBankCount = image.ChrRom.Length / ChrBankSize;
        if ((chrBankCount & (chrBankCount - 1)) != 0)
            throw new NotSupportedException(
                "CNROM CHR ROM must expose a power-of-two number of 8 KiB banks so latch outputs map directly to ROM address pins.");
        if (image.Mirroring == VirtualHardwareNesMirroring.FourScreen)
            throw new NotSupportedException("CNROM four-screen boards require additional cartridge nametable RAM hardware.");
        if (image.HasBatteryBackedMemory)
            throw new NotSupportedException("Standard CNROM hardware has no battery-backed cartridge memory.");
        if (image.HasExplicitRamSizes && image.TotalPrgRamSizeBytes != 0)
            throw new NotSupportedException("Standard CNROM hardware has no PRG RAM/NVRAM chip.");
        if (image.HasExplicitRamSizes && image.TotalChrRamSizeBytes != 0)
            throw new NotSupportedException("Standard CNROM uses CHR ROM rather than CHR RAM/NVRAM.");

        _busConflictsEnabled = ResolveBusConflictBehavior(image);
        _bankSelectMask = (byte)(chrBankCount - 1);
        _prg = image.PrgRom.ToArray();
        _chr = image.ChrRom.ToArray();
        _mirroring = image.Mirroring;
        IsInserted = true;
        ApplyResetState();
    }

    public void Eject()
    {
        IsInserted = false;
        _prg = [];
        _chr = [];
        _cpuReadAddressSelected = false;
        _cpuSelectedAddress = 0;
        _cpuSelectedData = 0;
        _cpuWriteCycleSelected = false;
        _cpuWriteAddress = 0;
        _ppuReadActive = false;
        ReleaseOutputs();
    }

    private static bool ResolveBusConflictBehavior(VirtualHardwareNesRomImage image) => image.SubmapperNumber switch
    {
        null => true,
        0 => true,
        1 => false,
        2 => true,
        _ => throw new NotSupportedException(
            $"Mapper 3 submapper {image.SubmapperNumber} is not defined by the current CNROM cartridge hardware.")
    };

    private bool IsPowered() =>
        IsInserted && Vcc.SampledLevel == DigitalLevel.High && Gnd.SampledLevel == DigitalLevel.Low;

    private void ApplyResetState()
    {
        _bankRegister = 0;
        RefreshDecodedChrBankBase();
        _cpuReadAddressSelected = false;
        _cpuSelectedAddress = 0;
        _cpuSelectedData = 0;
        _cpuWriteCycleSelected = false;
        _cpuWriteAddress = 0;
        _ppuReadActive = false;
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
        ReleaseOutputs();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RefreshDecodedChrBankBase()
    {
        _selectedChrBankBase = _chr.Length == 0
            ? 0
            : (_bankRegister & _bankSelectMask) * ChrBankSize;
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
            if (!powerChanged && !IsInserted) return;
            _cpuReadAddressSelected = false;
            _cpuWriteCycleSelected = false;
            _ppuReadActive = false;
            ReleaseOutputs();
            return;
        }

        if (powerChanged || ppuAddressOrControlChanged)
            ProcessPpuPort();

        // The falling M2 connector edge clocks the bank latch while address,
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
        if (readSelected)
        {
            PpuData.Drive(ReadChr(address));
            if (!_ppuReadActive) PpuReadCount++;
        }
        else
        {
            // CHR ROM never accepts PPU writes; every write is simply ignored by
            // this cartridge circuit while the connector still sees the bus.
            PpuData.Release();
        }

        _ppuReadActive = readSelected;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DriveCiramOutputs(ushort ppuAddress)
    {
        CiramChipEnableBar.Drive((ppuAddress & 0x2000) != 0 ? DigitalLevel.Low : DigitalLevel.High);
        var sourceBit = _mirroring == VirtualHardwareNesMirroring.Horizontal ? 11 : 10;
        CiramA10.Drive((ppuAddress & (1 << sourceBit)) != 0 ? DigitalLevel.High : DigitalLevel.Low);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadPrg(ushort address) => _prg[address & 0x7FFF];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadChr(ushort address) => _chr[_selectedChrBankBase + (address & 0x1FFF)];

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
        // Only latch outputs physically wired to populated CHR address pins can
        // influence the ROM; the mask follows fitted ROM capacity.
        _bankRegister = (byte)(effectiveData & _bankSelectMask);
        RefreshDecodedChrBankBase();
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
