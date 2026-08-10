using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

/// <summary>
/// Mapper-79/NINA-03/NINA-06 replaceable cartridge hardware. The cartridge
/// owns a 32 KiB switchable PRG-ROM window, an 8 KiB switchable CHR-ROM
/// window, the address-decoded $4100-$5FFF control latch, fixed nametable
/// wiring and no CPU/ROM bus conflict. Motherboards and the generic hardware
/// compiler see only connector pins plus product-agnostic compilable facets.
/// </summary>
public sealed class Nina0306Cartridge : VirtualHardwareComponent, IReplaceableCartridgeHardware,
    ICompiledBusTargetProvider, ICompiledCombinationalComponent, ICompiledStaticCombinationalComponent
{
    private const int PrgBankSize = 32 * 1024;
    private const int MaximumPrgSize = 64 * 1024;
    private const int ChrBankSize = 8 * 1024;
    private const int MaximumChrSize = 64 * 1024;

    private byte[] _prg = [];
    private byte[] _chr = [];
    private VirtualHardwareNesMirroring _mirroring;
    private byte _bankRegister;
    private byte _prgBankSelectMask;
    private byte _chrBankSelectMask;
    private int _selectedPrgBankBase;
    private int _selectedChrBankBase;

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

    public Nina0306Cartridge(string componentId) : base(componentId)
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

        // CPU data reaches only the address-decoded cartridge control latch and is
        // sampled at the end of an M2-qualified write. CHR is ROM, so PPU data
        // is never an internal input. Connector pins still retain every delivered level.
        CpuData.SetOwnerWakeEnabled(false);
        PpuData.SetOwnerWakeEnabled(false);
        ApplyResetState();
    }

    public int MapperNumber => 79;
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
    public int SelectedPrgBank => _prg.Length == 0 ? 0 : _selectedPrgBankBase / PrgBankSize;
    public int SelectedChrBank => _chr.Length == 0 ? 0 : _selectedChrBankBase / ChrBankSize;
    public int PrgBankCount => _prg.Length / PrgBankSize;
    public int ChrBankCount => _chr.Length / ChrBankSize;
    public int PrgRomSizeBytes => _prg.Length;
    public int ChrRomSizeBytes => _chr.Length;
    public bool BusConflictsEnabled => false;
    public ulong MapperWriteCount { get; private set; }
    public ushort LastMapperWriteAddress { get; private set; }
    public byte LastMapperWriteData { get; private set; }

    public ulong CpuReadCount { get; private set; }
    public ushort LastCpuReadAddress { get; private set; }
    public byte LastCpuReadData { get; private set; }
    public ulong PpuReadCount { get; private set; }
    public ulong PpuWriteCount { get; private set; }

    public void LoadImage(VirtualHardwareNesRomImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.MapperNumber != 79)
            throw new NotSupportedException($"Mapper {image.MapperNumber} is not NINA-03/06.");

        if (image.PrgRom.Length < PrgBankSize || image.PrgRom.Length > MaximumPrgSize ||
            image.PrgRom.Length % PrgBankSize != 0)
            throw new ArgumentException(
                "NINA-03/06 PRG ROM must contain one or two whole 32 KiB banks.", nameof(image));

        var prgBankCount = image.PrgRom.Length / PrgBankSize;
        if ((prgBankCount & (prgBankCount - 1)) != 0)
            throw new NotSupportedException(
                "NINA-03/06 PRG ROM must expose a power-of-two number of 32 KiB banks so latch outputs map directly to ROM address pins.");

        if (image.ChrRom.Length < ChrBankSize || image.ChrRom.Length > MaximumChrSize ||
            image.ChrRom.Length % ChrBankSize != 0)
            throw new ArgumentException(
                "NINA-03/06 CHR ROM must contain one to eight whole 8 KiB banks.", nameof(image));

        var chrBankCount = image.ChrRom.Length / ChrBankSize;
        if ((chrBankCount & (chrBankCount - 1)) != 0)
            throw new NotSupportedException(
                "NINA-03/06 CHR ROM must expose a power-of-two number of 8 KiB banks so latch outputs map directly to ROM address pins.");

        if (image.Mirroring == VirtualHardwareNesMirroring.FourScreen)
            throw new NotSupportedException(
                "Standard NINA-03/06 boards use fixed horizontal or vertical CIRAM wiring and have no cartridge nametable RAM.");
        if (image.HasBatteryBackedMemory)
            throw new NotSupportedException("Standard NINA-03/06 hardware has no battery-backed cartridge memory.");
        if (image.HasExplicitRamSizes && image.TotalPrgRamSizeBytes != 0)
            throw new NotSupportedException("Standard NINA-03/06 hardware has no PRG RAM/NVRAM chip.");
        if (image.HasExplicitRamSizes && image.TotalChrRamSizeBytes != 0)
            throw new NotSupportedException("Standard NINA-03/06 uses CHR ROM rather than CHR RAM/NVRAM.");

        // NINA-03 and NINA-06 differ in lockout circuitry, not in the mapper-79
        // PRG/CHR/mirroring path exposed on the cartridge connector. No NES 2.0
        // mapper-79 submapper is defined for a different memory-board variant.
        if (image.SubmapperNumber is int submapper && submapper != 0)
            throw new NotSupportedException(
                $"Mapper 79 submapper {submapper} has no defined NINA-03/06 board mapping in this hardware model.");

        _prgBankSelectMask = (byte)(prgBankCount - 1);
        _chrBankSelectMask = (byte)(chrBankCount - 1);
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
        MapperWriteCount = 0;
        LastMapperWriteAddress = 0;
        LastMapperWriteData = 0;
        CpuReadCount = 0;
        LastCpuReadAddress = 0;
        LastCpuReadData = 0;
        PpuReadCount = 0;
        PpuWriteCount = 0;
        ReleaseOutputs();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RefreshDecodedBankBases()
    {
        _selectedPrgBankBase = _prg.Length == 0
            ? 0
            : ((_bankRegister >> 3) & _prgBankSelectMask) * PrgBankSize;
        _selectedChrBankBase = _chr.Length == 0
            ? 0
            : (_bankRegister & _chrBankSelectMask) * ChrBankSize;
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
        if (!CpuAddress.TrySample(out var rawAddress))
        {
            _cpuReadAddressSelected = false;
            if (CpuM2.SampledLevel == DigitalLevel.High) _cpuWriteCycleSelected = false;
            CpuData.Release();
            return;
        }

        var romSelect = CpuRomSelectBar.SampledLevel;
        if (CpuReadWrite.SampledLevel == DigitalLevel.High && romSelect == DigitalLevel.Low)
        {
            _cpuWriteCycleSelected = false;
            _cpuReadAddressSelected = true;
            _cpuSelectedAddress = (ushort)(0x8000 | rawAddress);
            _cpuSelectedData = ReadPrg(_cpuSelectedAddress);
            CpuData.Drive(_cpuSelectedData);
            return;
        }

        _cpuReadAddressSelected = false;
        CpuData.Release();

        // NINA-03/06 decodes 010x xxx1 xxxx xxxx: A15=0, A14=1,
        // A13=0 and A8=1. A15 is represented at the cartridge boundary by
        // /ROMSEL remaining inactive/high for this low-half CPU address.
        if (CpuReadWrite.SampledLevel == DigitalLevel.Low &&
            romSelect == DigitalLevel.High &&
            CpuM2.SampledLevel == DigitalLevel.High &&
            IsControlRegisterAddress((ushort)rawAddress))
        {
            _cpuWriteCycleSelected = true;
            _cpuWriteAddress = (ushort)rawAddress;
            return;
        }

        if (CpuM2.SampledLevel == DigitalLevel.High)
            _cpuWriteCycleSelected = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsControlRegisterAddress(ushort address) =>
        address < 0x8000 && (address & 0x6100) == 0x4100;

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
    private byte ReadPrg(ushort address) => _prg[_selectedPrgBankBase + (address & 0x7FFF)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadChr(ushort address) => _chr[_selectedChrBankBase + (address & 0x1FFF)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteMapperRegister(ushort address, byte cpuData)
    {
        if (!IsControlRegisterAddress(address)) return;

        MapperWriteCount++;
        LastMapperWriteAddress = address;
        LastMapperWriteData = cpuData;

        // The mapper-79 control latch connects D3 to PRG A15 and D0-D2 to
        // CHR A13-A15. Higher CPU data bits are physically unconnected here.
        _bankRegister = (byte)(cpuData & ((_prgBankSelectMask << 3) | _chrBankSelectMask));
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
    internal void WriteControlCompiled(ushort address, byte value) => WriteMapperRegister(address, value);

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

        // PRG ROM drives only the normal cartridge ROM window. Mapper writes
        // happen below $8000 and therefore cannot conflict with PRG ROM.
        yield return new CompiledBusTargetDescriptor(
            this,
            CpuAddress.Pins,
            CpuData.Pins,
            new[]
            {
                new CompiledPinCondition(CpuReadWrite, DigitalLevel.High),
                new CompiledPinCondition(CpuRomSelectBar, DigitalLevel.Low)
            },
            Array.Empty<CompiledPinCondition>(),
            CompiledBusReadPhase.Complete,
            address => ReadCpuCompiled((ushort)(0x8000 | address)),
            null);

        // NINA-03/06 control register address decode: 010x xxx1 xxxx xxxx.
        // Express every decoded package pin as a generic physical condition so
        // the compiler can pre-resolve the route without learning mapper rules.
        yield return new CompiledBusTargetDescriptor(
            this,
            CpuAddress.Pins,
            CpuData.Pins,
            Array.Empty<CompiledPinCondition>(),
            new[]
            {
                new CompiledPinCondition(CpuReadWrite, DigitalLevel.Low),
                new CompiledPinCondition(CpuRomSelectBar, DigitalLevel.High),
                new CompiledPinCondition(CpuAddress.Pins[14], DigitalLevel.High),
                new CompiledPinCondition(CpuAddress.Pins[13], DigitalLevel.Low),
                new CompiledPinCondition(CpuAddress.Pins[8], DigitalLevel.High)
            },
            CompiledBusReadPhase.Complete,
            null,
            (address, value) => WriteControlCompiled((ushort)address, value),
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
