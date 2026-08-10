using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

public enum Mapper34BoardVariant
{
    Bnrom,
    Nina001
}

/// <summary>
/// Mapper-34 replaceable cartridge hardware. Mapper 34 historically assigns one
/// iNES number to two unrelated physical board families, so this package resolves
/// and then models exactly one fitted circuit: BNROM/I-IM or AVE NINA-001/002.
/// The generic hardware compiler sees only connector pins and product-agnostic
/// compiled facets; it contains no mapper-34 or board-selection semantics.
/// </summary>
public sealed class Mapper34Cartridge : VirtualHardwareComponent, IReplaceableCartridgeHardware,
    ICompiledBusTargetProvider, ICompiledCombinationalComponent, ICompiledStaticCombinationalComponent
{
    private const int Prg32K = 32 * 1024;
    private const int Chr8K = 8 * 1024;
    private const int Chr4K = 4 * 1024;
    private const int PrgRam8K = 8 * 1024;

    private byte[] _prg = [];
    private byte[] _chrRom = [];
    private byte[] _chrRam = [];
    private byte[] _prgRam = [];
    private VirtualHardwareNesMirroring _mirroring;
    private Mapper34BoardVariant _variant;

    private byte _bnromBankRegister;
    private byte _bnromBankMask;
    private int _bnromPrgBase;

    private byte _ninaPrgRegister;
    private byte _ninaChr0Register;
    private byte _ninaChr1Register;
    private byte _ninaPrgMask;
    private byte _ninaChrMask;
    private int _ninaPrgBase;
    private int _ninaChr0Base;
    private int _ninaChr1Base;

    private bool _cpuReadAddressSelected;
    private ushort _cpuSelectedAddress;
    private byte _cpuSelectedData;
    private bool _cpuCycleRomSelected;
    private bool _cpuCycleRamSelected;
    private ushort _cpuCycleAddress;
    private bool _ppuReadActive;
    private bool _ppuWriteActive;

    private readonly ulong _powerInputMask;
    private readonly ulong _cpuAddressControlInputMask;
    private readonly ulong _cpuM2InputMask;
    private readonly ulong _cpuRomSelectInputMask;
    private readonly ulong _ppuAddressControlInputMask;
    private readonly ulong _ppuDataInputMask;

    public Mapper34Cartridge(string componentId) : base(componentId)
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

        // CPU data is sampled only when a selected write completes. PPU data is
        // an internal input only on the BNROM CHR-RAM variant while /WR is low.
        CpuData.SetOwnerWakeEnabled(false);
        PpuData.SetOwnerWakeEnabled(false);
        ApplyResetState();
    }

    public int MapperNumber => 34;
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
    public Mapper34BoardVariant BoardVariant => _variant;
    public VirtualHardwareNesMirroring Mirroring => _mirroring;
    public int PrgRomSizeBytes => _prg.Length;
    public int ChrRomSizeBytes => _chrRom.Length;
    public int ChrRamSizeBytes => _chrRam.Length;
    public int PrgRamSizeBytes => _prgRam.Length;
    public bool BusConflictsEnabled => _variant == Mapper34BoardVariant.Bnrom;

    public byte BnromBankRegister => _bnromBankRegister;
    public int SelectedBnromPrgBank => _prg.Length == 0 ? 0 : _bnromPrgBase / Prg32K;
    public int BnromPrgBankCount => _prg.Length / Prg32K;

    public byte NinaPrgRegister => _ninaPrgRegister;
    public byte NinaChr0Register => _ninaChr0Register;
    public byte NinaChr1Register => _ninaChr1Register;
    public int SelectedNinaPrgBank => _prg.Length == 0 ? 0 : _ninaPrgBase / Prg32K;
    public int SelectedNinaChrBank0 => _chrRom.Length == 0 ? 0 : _ninaChr0Base / Chr4K;
    public int SelectedNinaChrBank1 => _chrRom.Length == 0 ? 0 : _ninaChr1Base / Chr4K;
    public int NinaPrgBankCount => _prg.Length / Prg32K;
    public int NinaChrBankCount => _chrRom.Length / Chr4K;

    public ulong MapperWriteCount { get; private set; }
    public ulong BusConflictModifiedWriteCount { get; private set; }
    public ulong PrgRamWriteCount { get; private set; }
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
        if (image.MapperNumber != 34)
            throw new NotSupportedException($"Mapper {image.MapperNumber} is not Mapper 34 / BNROM / NINA-001.");

        _variant = ResolveVariant(image);
        _mirroring = image.Mirroring;
        if (_mirroring == VirtualHardwareNesMirroring.FourScreen)
            throw new NotSupportedException("Mapper-34 BNROM and NINA-001 boards use fixed H/V CIRAM wiring and have no standard four-screen nametable RAM.");

        if (_variant == Mapper34BoardVariant.Bnrom)
            LoadBnrom(image);
        else
            LoadNina001(image);

        IsInserted = true;
        ApplyResetState();
        RefreshPpuDataWakeState();
    }

    public void Eject()
    {
        IsInserted = false;
        _prg = [];
        _chrRom = [];
        _chrRam = [];
        _prgRam = [];
        _cpuReadAddressSelected = false;
        _cpuCycleRomSelected = false;
        _cpuCycleRamSelected = false;
        _ppuReadActive = false;
        _ppuWriteActive = false;
        ReleaseOutputs();
        RefreshPpuDataWakeState();
    }

    private static Mapper34BoardVariant ResolveVariant(VirtualHardwareNesRomImage image)
    {
        if (image.SubmapperNumber == 1) return Mapper34BoardVariant.Nina001;
        if (image.SubmapperNumber == 2) return Mapper34BoardVariant.Bnrom;
        if (image.SubmapperNumber is int submapper && submapper != 0)
            throw new NotSupportedException($"Mapper 34 submapper {submapper} is not a defined BNROM/NINA-001 board variant.");
        return image.ChrRom.Length > Chr8K ? Mapper34BoardVariant.Nina001 : Mapper34BoardVariant.Bnrom;
    }

    private void LoadBnrom(VirtualHardwareNesRomImage image)
    {
        if (image.PrgRom.Length < Prg32K || image.PrgRom.Length > 4 * Prg32K || image.PrgRom.Length % Prg32K != 0)
            throw new ArgumentException("BNROM PRG ROM must contain one to four whole 32 KiB banks.", nameof(image));
        var prgBanks = image.PrgRom.Length / Prg32K;
        if (!IsPowerOfTwo(prgBanks))
            throw new NotSupportedException("BNROM PRG ROM must expose a power-of-two number of 32 KiB banks so fitted latch outputs map directly to ROM address pins.");

        if (image.ChrRom.Length != 0 && image.ChrRom.Length != Chr8K)
            throw new ArgumentException("BNROM supports either one fixed 8 KiB CHR-ROM or one 8 KiB CHR-RAM device.", nameof(image));

        var explicitChrRam = image.HasExplicitRamSizes ? image.TotalChrRamSizeBytes : 0;
        if (image.ChrRom.Length == 0)
        {
            if (image.HasExplicitRamSizes && explicitChrRam != Chr8K)
                throw new NotSupportedException("BNROM without CHR ROM requires exactly 8 KiB of CHR RAM/NVRAM.");
        }
        else if (image.HasExplicitRamSizes && explicitChrRam != 0)
            throw new NotSupportedException("BNROM cannot fit CHR ROM and CHR RAM simultaneously in this physical board model.");

        var prgRamSize = image.HasExplicitRamSizes ? image.TotalPrgRamSizeBytes : 0;
        if (prgRamSize != 0 && prgRamSize != PrgRam8K)
            throw new NotSupportedException("BNROM supports no standard PRG RAM; the documented extended board case is one 8 KiB PRG-RAM device.");

        _prg = image.PrgRom.ToArray();
        _chrRom = image.ChrRom.ToArray();
        _chrRam = image.ChrRom.Length == 0 ? new byte[Chr8K] : [];
        _prgRam = prgRamSize == 0 ? [] : new byte[PrgRam8K];
        _bnromBankMask = (byte)(prgBanks - 1);
        _ninaPrgMask = 0;
        _ninaChrMask = 0;
    }

    private void LoadNina001(VirtualHardwareNesRomImage image)
    {
        if (image.PrgRom.Length < Prg32K || image.PrgRom.Length > 2 * Prg32K || image.PrgRom.Length % Prg32K != 0)
            throw new ArgumentException("NINA-001/002 PRG ROM must contain one or two whole 32 KiB banks.", nameof(image));
        var prgBanks = image.PrgRom.Length / Prg32K;
        if (!IsPowerOfTwo(prgBanks))
            throw new NotSupportedException("NINA-001/002 PRG ROM must expose a power-of-two number of 32 KiB banks.");

        if (image.ChrRom.Length < Chr8K || image.ChrRom.Length > 16 * Chr4K || image.ChrRom.Length % Chr4K != 0)
            throw new ArgumentException("NINA-001/002 CHR ROM must contain two to sixteen whole 4 KiB banks.", nameof(image));
        var chrBanks = image.ChrRom.Length / Chr4K;
        if (!IsPowerOfTwo(chrBanks))
            throw new NotSupportedException("NINA-001/002 CHR ROM must expose a power-of-two number of 4 KiB banks.");

        if (image.HasBatteryBackedMemory || (image.HasExplicitRamSizes && image.PrgNvRamSizeBytes > 0))
            throw new NotSupportedException("NINA-001/002 uses volatile 8 KiB PRG RAM rather than battery-backed NVRAM.");
        if (image.HasExplicitRamSizes && image.PrgRamSizeBytes != PrgRam8K)
            throw new NotSupportedException("NINA-001/002 requires exactly 8 KiB of volatile PRG RAM.");
        if (image.HasExplicitRamSizes && image.TotalChrRamSizeBytes != 0)
            throw new NotSupportedException("NINA-001/002 uses CHR ROM rather than CHR RAM/NVRAM.");

        _prg = image.PrgRom.ToArray();
        _chrRom = image.ChrRom.ToArray();
        _chrRam = [];
        _prgRam = new byte[PrgRam8K];
        _bnromBankMask = 0;
        _ninaPrgMask = (byte)(prgBanks - 1);
        _ninaChrMask = (byte)(chrBanks - 1);
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    private bool IsPowered() =>
        IsInserted && Vcc.SampledLevel == DigitalLevel.High && Gnd.SampledLevel == DigitalLevel.Low;

    private void ApplyResetState()
    {
        _bnromBankRegister = 0;
        _ninaPrgRegister = 0;
        _ninaChr0Register = 0;
        _ninaChr1Register = 0;
        RefreshDecodedBanks();
        _cpuReadAddressSelected = false;
        _cpuSelectedAddress = 0;
        _cpuSelectedData = 0;
        _cpuCycleRomSelected = false;
        _cpuCycleRamSelected = false;
        _cpuCycleAddress = 0;
        _ppuReadActive = false;
        _ppuWriteActive = false;
        MapperWriteCount = 0;
        BusConflictModifiedWriteCount = 0;
        PrgRamWriteCount = 0;
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
    private void RefreshDecodedBanks()
    {
        _bnromPrgBase = _prg.Length == 0 ? 0 : (_bnromBankRegister & _bnromBankMask) * Prg32K;
        _ninaPrgBase = _prg.Length == 0 ? 0 : (_ninaPrgRegister & _ninaPrgMask) * Prg32K;
        _ninaChr0Base = _chrRom.Length == 0 ? 0 : (_ninaChr0Register & _ninaChrMask) * Chr4K;
        _ninaChr1Base = _chrRom.Length == 0 ? 0 : (_ninaChr1Register & _ninaChrMask) * Chr4K;
    }

    private void RefreshPpuDataWakeState()
    {
        var writable = _variant == Mapper34BoardVariant.Bnrom && _chrRam.Length != 0;
        PpuData.SetOwnerWakeEnabled(IsPowered() && writable && PpuWriteBar.SampledLevel == DigitalLevel.Low);
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
            _cpuCycleRomSelected = false;
            _cpuCycleRamSelected = false;
            _ppuReadActive = false;
            _ppuWriteActive = false;
            ReleaseOutputs();
            RefreshPpuDataWakeState();
            return;
        }

        if (powerChanged || ppuAddressOrControlChanged) RefreshPpuDataWakeState();
        var ppuDataCanMatter = ppuDataChanged && _variant == Mapper34BoardVariant.Bnrom && _chrRam.Length != 0
            && PpuWriteBar.SampledLevel == DigitalLevel.Low;
        if (powerChanged || ppuAddressOrControlChanged || ppuDataCanMatter)
            ProcessPpuPort();

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
            _cpuCycleRamSelected = false;
            CpuData.Release();
            return;
        }

        var address = (ushort)(rawAddress & 0x7FFF);
        var m2High = CpuM2.SampledLevel == DigitalLevel.High;
        var romSelected = m2High && CpuRomSelectBar.SampledLevel == DigitalLevel.Low;
        var ramSelected = m2High && _prgRam.Length != 0
            && CpuRomSelectBar.SampledLevel == DigitalLevel.High
            && (address & 0x6000) == 0x6000;

        _cpuCycleRomSelected = romSelected;
        _cpuCycleRamSelected = ramSelected;
        _cpuCycleAddress = romSelected ? (ushort)(0x8000 | address) : address;

        _cpuReadAddressSelected = CpuReadWrite.SampledLevel == DigitalLevel.High && (romSelected || ramSelected);
        if (!_cpuReadAddressSelected)
        {
            CpuData.Release();
            return;
        }

        _cpuSelectedAddress = _cpuCycleAddress;
        _cpuSelectedData = romSelected ? ReadPrg(_cpuSelectedAddress) : _prgRam[address & 0x1FFF];
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
        var data = (byte)rawData;

        if (_cpuCycleRamSelected)
        {
            WritePrgRam(_cpuCycleAddress, data);
            return;
        }

        if (_cpuCycleRomSelected && _variant == Mapper34BoardVariant.Bnrom)
            WriteBnromRegister(_cpuCycleAddress, data);
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
        var writeSelected = address < 0x2000 && _chrRam.Length != 0 && PpuWriteBar.SampledLevel == DigitalLevel.Low;
        if (readSelected)
        {
            PpuData.Drive(ReadChr(address));
            if (!_ppuReadActive) PpuReadCount++;
        }
        else if (writeSelected && PpuData.TrySample(out var data))
        {
            _chrRam[address & 0x1FFF] = (byte)data;
            if (!_ppuWriteActive) PpuWriteCount++;
            PpuData.Release();
        }
        else PpuData.Release();

        _ppuReadActive = readSelected;
        _ppuWriteActive = writeSelected;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DriveCiramOutputs(ushort address)
    {
        CiramChipEnableBar.Drive((address & 0x2000) != 0 ? DigitalLevel.Low : DigitalLevel.High);
        var sourceBit = _mirroring == VirtualHardwareNesMirroring.Horizontal ? 11 : 10;
        CiramA10.Drive((address & (1 << sourceBit)) != 0 ? DigitalLevel.High : DigitalLevel.Low);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadPrg(ushort address)
    {
        var baseAddress = _variant == Mapper34BoardVariant.Bnrom ? _bnromPrgBase : _ninaPrgBase;
        return _prg[baseAddress + (address & 0x7FFF)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadChr(ushort address)
    {
        if (_variant == Mapper34BoardVariant.Bnrom)
        {
            if (_chrRam.Length != 0) return _chrRam[address & 0x1FFF];
            return _chrRom[address & 0x1FFF];
        }

        var baseAddress = (address & 0x1000) == 0 ? _ninaChr0Base : _ninaChr1Base;
        return _chrRom[baseAddress + (address & 0x0FFF)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteBnromRegister(ushort address, byte cpuData)
    {
        var effective = (byte)(cpuData & ReadPrg(address));
        if (effective != cpuData) BusConflictModifiedWriteCount++;
        MapperWriteCount++;
        LastMapperWriteAddress = address;
        LastMapperWriteData = cpuData;
        LastEffectiveMapperWriteData = effective;
        _bnromBankRegister = (byte)(effective & _bnromBankMask);
        RefreshDecodedBanks();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WritePrgRam(ushort address, byte value)
    {
        _prgRam[address & 0x1FFF] = value;
        PrgRamWriteCount++;
        if (_variant != Mapper34BoardVariant.Nina001) return;

        switch (address)
        {
            case 0x7FFD:
                _ninaPrgRegister = (byte)(value & _ninaPrgMask);
                break;
            case 0x7FFE:
                _ninaChr0Register = (byte)(value & _ninaChrMask);
                break;
            case 0x7FFF:
                _ninaChr1Register = (byte)(value & _ninaChrMask);
                break;
            default:
                return;
        }

        MapperWriteCount++;
        LastMapperWriteAddress = address;
        LastMapperWriteData = value;
        LastEffectiveMapperWriteData = value;
        RefreshDecodedBanks();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadCpuCompiled(ushort address)
    {
        var value = address >= 0x8000 ? ReadPrg(address) : _prgRam[address & 0x1FFF];
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
            if (_variant == Mapper34BoardVariant.Bnrom) WriteBnromRegister(address, value);
        }
        else if (_prgRam.Length != 0) WritePrgRam(address, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadPpuCompiled(ushort address)
    {
        PpuReadCount++;
        return ReadChr(address);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WritePpuCompiled(ushort address, byte value)
    {
        if (_chrRam.Length == 0) return;
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

        IReadOnlyList<CompiledPinCondition> romWriteConditions = _variant == Mapper34BoardVariant.Bnrom
            ? new[]
            {
                new CompiledPinCondition(CpuReadWrite, DigitalLevel.Low),
                new CompiledPinCondition(CpuRomSelectBar, DigitalLevel.Low)
            }
            : Array.Empty<CompiledPinCondition>();
        Action<int, byte>? romWrite = _variant == Mapper34BoardVariant.Bnrom
            ? (address, value) => WriteCpuCompiled((ushort)(0x8000 | address), value)
            : null;

        yield return new CompiledBusTargetDescriptor(
            this,
            CpuAddress.Pins,
            CpuData.Pins,
            new[]
            {
                new CompiledPinCondition(CpuReadWrite, DigitalLevel.High),
                new CompiledPinCondition(CpuRomSelectBar, DigitalLevel.Low)
            },
            romWriteConditions,
            CompiledBusReadPhase.Complete,
            address => ReadCpuCompiled((ushort)(0x8000 | address)),
            romWrite,
            writePhase: CompiledBusWritePhase.Complete);

        if (_prgRam.Length != 0)
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
                address => ReadCpuCompiled((ushort)address),
                (address, value) => WriteCpuCompiled((ushort)address, value),
                writePhase: CompiledBusWritePhase.Complete);
        }

        Action<int, byte>? ppuWrite = _chrRam.Length != 0
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
            _chrRam.Length != 0
                ? new[]
                {
                    new CompiledPinCondition(PpuAddress.Pins[13], DigitalLevel.Low),
                    new CompiledPinCondition(PpuWriteBar, DigitalLevel.Low)
                }
                : Array.Empty<CompiledPinCondition>(),
            CompiledBusReadPhase.Complete,
            address => ReadPpuCompiled((ushort)address),
            ppuWrite);
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
