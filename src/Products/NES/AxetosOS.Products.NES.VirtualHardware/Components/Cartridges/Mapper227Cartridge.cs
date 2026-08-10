using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

/// <summary>
/// Mapper-227 address-latch multicart hardware. CPU writes do not latch data:
/// the completed write address itself selects outer/inner PRG wiring, PRG mode,
/// nametable mirroring and optional solder-pad ROM addressing. The cartridge
/// also owns one unbanked 8 KiB CHR-RAM chip. Motherboards and the generic
/// compiler see only connector pins plus product-agnostic compilable facets.
/// </summary>
public sealed class Mapper227Cartridge : VirtualHardwareComponent, IReplaceableCartridgeHardware,
    ICompiledBusTargetProvider, ICompiledCombinationalComponent, ICompiledStaticCombinationalComponent
{
    private const int PrgBankSize = 16 * 1024;
    private const int MaximumPrgSize = 1024 * 1024;
    private const int StandardChrRamSize = 8 * 1024;

    private byte[] _prg = [];
    private byte[] _chrRam = [];
    private ushort _addressLatch;
    private int _prgBankMask;
    private int _lowerPrgBank;
    private int _upperPrgBank;
    private bool _protectChrInNromModes;
    private bool _solderPadReadSupported;
    private bool _submapper2InnerZeroBehavior;

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

    public Mapper227Cartridge(string componentId, byte solderPadValue = 0) : base(componentId)
    {
        SolderPadValue = (byte)(solderPadValue & 0x0F);

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

        // Mapper 227 clocks CPU address lines, not CPU data, at the end of the
        // selected write. PPU data is consumed only by writable CHR-RAM cycles.
        CpuData.SetOwnerWakeEnabled(false);
        PpuData.SetOwnerWakeEnabled(false);
        ApplyResetState();
    }

    public int MapperNumber => 227;
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
    public ushort AddressLatch => _addressLatch;
    public int PrgBankCount => _prg.Length / PrgBankSize;
    public int PrgRomSizeBytes => _prg.Length;
    public int ChrRamSizeBytes => _chrRam.Length;
    public int LowerPrgBank => _lowerPrgBank;
    public int UpperPrgBank => _upperPrgBank;
    public byte SolderPadValue { get; }
    public bool NromMode => (_addressLatch & 0x0080) != 0;
    public bool SFlag => (_addressLatch & 0x0001) != 0;
    public bool LFlag => (_addressLatch & 0x0200) != 0;
    public bool SolderPadReadSupported => _solderPadReadSupported;
    public bool SolderPadReadActive => _solderPadReadSupported && (_addressLatch & 0x0400) != 0;
    public bool ChrRamWriteProtected => _protectChrInNromModes && NromMode;
    public VirtualHardwareNesMirroring Mirroring =>
        (_addressLatch & 0x0002) != 0
            ? VirtualHardwareNesMirroring.Horizontal
            : VirtualHardwareNesMirroring.Vertical;

    public string PrgMode => NromMode
        ? (SFlag ? "NROM-256" : "NROM-128")
        : (LFlag ? "UNROM-fixed-7" : "UNROM-fixed-0");

    public ulong MapperWriteCount { get; private set; }
    public ushort LastMapperWriteAddress { get; private set; }
    public byte LastMapperWriteData { get; private set; }
    public ulong CpuReadCount { get; private set; }
    public ushort LastCpuReadAddress { get; private set; }
    public byte LastCpuReadData { get; private set; }
    public ulong PpuReadCount { get; private set; }
    public ulong PpuWriteCount { get; private set; }
    public ulong ProtectedChrWriteCount { get; private set; }

    public void LoadImage(VirtualHardwareNesRomImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.MapperNumber != 227)
            throw new NotSupportedException($"Mapper {image.MapperNumber} is not Mapper 227.");

        if (image.PrgRom.Length < 2 * PrgBankSize || image.PrgRom.Length > MaximumPrgSize ||
            image.PrgRom.Length % PrgBankSize != 0)
            throw new ArgumentException(
                "Mapper 227 PRG ROM must contain two to sixty-four whole 16 KiB banks (maximum 1 MiB).", nameof(image));

        var prgBankCount = image.PrgRom.Length / PrgBankSize;
        if ((prgBankCount & (prgBankCount - 1)) != 0)
            throw new NotSupportedException(
                "Mapper 227 PRG ROM must expose a power-of-two number of 16 KiB banks so unconnected ROM address pins are modeled directly.");

        if (image.ChrRom.Length != 0)
            throw new NotSupportedException("Standard Mapper 227 multicart hardware uses unbanked CHR RAM, not CHR ROM.");
        if (image.Mirroring == VirtualHardwareNesMirroring.FourScreen)
            throw new NotSupportedException("Mapper 227 drives H/V CIRAM wiring from its address latch; four-screen hardware is distinct.");
        if (image.HasBatteryBackedMemory)
            throw new NotSupportedException(
                "Battery-backed Mapper 227 Chinese-RPG boards add WRAM and omit the multicart UNROM-like modes; that distinct board variant is not approximated here.");
        if (image.HasExplicitRamSizes && image.TotalPrgRamSizeBytes != 0)
            throw new NotSupportedException(
                "Standard Mapper 227 multicart hardware has no PRG RAM/NVRAM chip.");
        if (image.HasExplicitRamSizes && image.ChrNvRamSizeBytes != 0)
            throw new NotSupportedException("Standard Mapper 227 uses volatile CHR RAM rather than CHR NVRAM.");

        var chrRamSize = image.HasExplicitRamSizes ? image.TotalChrRamSizeBytes : StandardChrRamSize;
        if (chrRamSize != StandardChrRamSize)
            throw new NotSupportedException(
                $"Standard Mapper 227 requires one {StandardChrRamSize:N0}-byte CHR RAM chip; the image declares {chrRamSize:N0} bytes.");

        ResolveBoardVariant(image.SubmapperNumber);

        _prgBankMask = prgBankCount - 1;
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
        _prgBankMask = 0;
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

    private void ResolveBoardVariant(int? submapper)
    {
        switch (submapper)
        {
            case null:
                // Legacy iNES mapper-227 images historically describe the
                // multicart PCB: NROM modes protect CHR-RAM and the address-latch
                // m output may route PRG A3-A0 from the board's four solder pads.
                // NES 2.0 expresses that same feature set explicitly as
                // submapper 1.
                _protectChrInNromModes = true;
                _solderPadReadSupported = true;
                _submapper2InnerZeroBehavior = false;
                break;
            case 0:
                _protectChrInNromModes = false;
                _solderPadReadSupported = false;
                _submapper2InnerZeroBehavior = false;
                break;
            case 1:
                _protectChrInNromModes = true;
                _solderPadReadSupported = true;
                _submapper2InnerZeroBehavior = false;
                break;
            case 2:
                _protectChrInNromModes = true;
                _solderPadReadSupported = false;
                _submapper2InnerZeroBehavior = true;
                break;
            default:
                throw new NotSupportedException(
                    $"Mapper 227 submapper {submapper} is not defined by the current address-latch multicart hardware model.");
        }
    }

    private bool IsPowered() =>
        IsInserted && Vcc.SampledLevel == DigitalLevel.High && Gnd.SampledLevel == DigitalLevel.Low;

    private void ApplyResetState()
    {
        // The physical address latch powers up cleared. Both CPU halves therefore
        // expose 16 KiB bank zero until the menu performs its first mapper write.
        _addressLatch = 0;
        RefreshDecodedPrgBanks();
        _cpuReadAddressSelected = false;
        _cpuSelectedAddress = 0;
        _cpuSelectedData = 0;
        _cpuWriteCycleSelected = false;
        _cpuWriteAddress = 0;
        _ppuReadActive = false;
        _ppuWriteActive = false;
        MapperWriteCount = 0;
        LastMapperWriteAddress = 0;
        LastMapperWriteData = 0;
        CpuReadCount = 0;
        LastCpuReadAddress = 0;
        LastCpuReadData = 0;
        PpuReadCount = 0;
        PpuWriteCount = 0;
        ProtectedChrWriteCount = 0;
        RefreshPpuDataWakeState();
        ReleaseOutputs();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int DecodePrgBankSeed() =>
        ((_addressLatch >> 2) & 0x1F) | ((_addressLatch & 0x0100) >> 3);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RefreshDecodedPrgBanks()
    {
        if (_prg.Length == 0)
        {
            _lowerPrgBank = 0;
            _upperPrgBank = 0;
            return;
        }

        var prgBank = DecodePrgBankSeed();
        int lower;
        int upper;

        if (NromMode)
        {
            if (SFlag)
            {
                lower = prgBank & 0x3E;
                upper = lower + 1;
            }
            else
            {
                lower = prgBank;
                upper = prgBank;
            }
        }
        else
        {
            lower = SFlag ? prgBank & 0x3E : prgBank;
            if (LFlag)
            {
                upper = prgBank | 0x07;
            }
            else
            {
                // Submapper 2 forces outer A18..A17 low whenever the fixed
                // inner bank is zero while leaving A19 unaffected.
                upper = _submapper2InnerZeroBehavior
                    ? prgBank & 0x20
                    : prgBank & 0x38;
            }
        }

        _lowerPrgBank = lower & _prgBankMask;
        _upperPrgBank = upper & _prgBankMask;
    }

    private void RefreshPpuDataWakeState()
    {
        var enabled = IsPowered() &&
            PpuWriteBar.SampledLevel == DigitalLevel.Low &&
            !ChrRamWriteProtected;
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
        var ppuDataCanMatter = ppuDataChanged &&
            PpuWriteBar.SampledLevel == DigitalLevel.Low &&
            !ChrRamWriteProtected;
        if (powerChanged || ppuAddressOrControlChanged || ppuDataCanMatter)
            ProcessPpuPort();

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

        if (_cpuWriteCycleSelected)
        {
            var data = (byte)0;
            if (CpuData.TrySample(out var rawData)) data = (byte)rawData;
            LatchMapperAddress(_cpuWriteAddress, data);
        }

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
            _ppuWriteActive = false;
            return;
        }

        var address = (ushort)(rawAddress & 0x3FFF);
        DriveCiramOutputs(address);

        var readSelected = false;
        var writeSelected = false;
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
                if (writeSelected && !_ppuWriteActive)
                {
                    if (ChrRamWriteProtected)
                    {
                        ProtectedChrWriteCount++;
                    }
                    else if (PpuData.TrySample(out var data))
                    {
                        _chrRam[address & 0x1FFF] = (byte)data;
                        PpuWriteCount++;
                    }
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
        var sourceBit = Mirroring == VirtualHardwareNesMirroring.Horizontal ? 11 : 10;
        CiramA10.Drive((ppuAddress & (1 << sourceBit)) != 0 ? DigitalLevel.High : DigitalLevel.Low);
    }

    private void RefreshCiramOutputFromCurrentAddress()
    {
        if (!IsPowered()) return;
        if (PpuAddress.TrySample(out var rawAddress))
        {
            DriveCiramOutputs((ushort)(rawAddress & 0x3FFF));
        }
        else
        {
            CiramA10.Drive(DigitalLevel.Unknown);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadPrg(ushort address)
    {
        var bank = address < 0xC000 ? _lowerPrgBank : _upperPrgBank;
        var offset = address & 0x3FFF;
        if (SolderPadReadActive)
            offset = (offset & 0x3FF0) | SolderPadValue;
        return _prg[bank * PrgBankSize + offset];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LatchMapperAddress(ushort address, byte cpuData)
    {
        MapperWriteCount++;
        LastMapperWriteAddress = address;
        LastMapperWriteData = cpuData;

        // Address lines A0-A10 are the physically significant latched signals.
        // CPU data does not participate in Mapper-227 bank selection.
        _addressLatch = (ushort)(address & 0x07FF);
        RefreshDecodedPrgBanks();
        RefreshPpuDataWakeState();
        RefreshCiramOutputFromCurrentAddress();
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
    internal void WriteCpuCompiled(ushort address, byte value) => LatchMapperAddress(address, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadPpuCompiled(ushort address)
    {
        PpuReadCount++;
        return _chrRam[address & 0x1FFF];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WritePpuCompiled(ushort address, byte value)
    {
        if (ChrRamWriteProtected)
        {
            ProtectedChrWriteCount++;
            return;
        }

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

        // Mapper 227 shares the normal cartridge ROM window. Reads use the
        // currently decoded PRG wiring; writes clock the address latch and ignore
        // CPU data. Both are represented through the same physical connector.
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

        // CIRAM A10 depends on the live mapper address latch and must not be
        // folded as a state-independent topology fact.
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
            var sourceBit = Mirroring == VirtualHardwareNesMirroring.Horizontal ? 11 : 10;
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
}
