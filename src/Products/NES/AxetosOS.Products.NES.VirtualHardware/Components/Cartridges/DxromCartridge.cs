using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

/// <summary>
/// Mapper-206 / DxROM replaceable cartridge hardware built around the Namco
/// 108/118 or Tengen MIMIC-1 banking family. The package exposes only the
/// banking facilities that exist on this predecessor of MMC3: two switchable
/// 8 KiB PRG windows followed by two fixed-last windows, two 2 KiB plus four
/// 1 KiB CHR windows, and hard-wired nametable routing. There is no MMC3 PRG
/// mode, CHR inversion, IRQ counter, mapper-controlled mirroring, or standard
/// work-RAM control register.
/// </summary>
public sealed class DxromCartridge : VirtualHardwareComponent, IReplaceableCartridgeHardware,
    ICompiledBusTargetProvider, ICompiledCombinationalComponent, ICompiledStaticCombinationalComponent
{
    private const int PrgBankSize = 8 * 1024;
    private const int ChrBankSize = 1 * 1024;
    private const int OptionalPrgRamSize = 8 * 1024;
    private const int FourScreenRamChipSize = 8 * 1024;

    private byte[] _prg = [];
    private byte[] _chr = [];
    private byte[] _prgRam = [];
    private byte[] _fourScreenRam = [];
    private readonly byte[] _bankRegisters = new byte[8];
    private readonly int[] _prgWindowBases = new int[4];
    private readonly int[] _chrWindowBases = new int[8];

    private bool _unbanked32KPrg;
    private VirtualHardwareNesMirroring _mirroring;
    private byte _bankSelect;
    private byte _prgBankMask;
    private byte _chrBankMask;

    private bool _cpuReadAddressSelected;
    private ushort _cpuSelectedAddress;
    private byte _cpuSelectedData;
    private bool _cpuCycleRomSelected;
    private bool _cpuCyclePrgRamSelected;
    private ushort _cpuCycleLogicalAddress;
    private bool _ppuReadActive;
    private bool _ppuWriteActive;

    private readonly ulong _powerInputMask;
    private readonly ulong _cpuAddressControlInputMask;
    private readonly ulong _cpuM2InputMask;
    private readonly ulong _cpuRomSelectInputMask;
    private readonly ulong _ppuAddressControlInputMask;
    private readonly ulong _ppuDataInputMask;

    public DxromCartridge(string componentId) : base(componentId)
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

        CpuData.SetOwnerWakeEnabled(false);
        PpuData.SetOwnerWakeEnabled(false);
        ApplyResetState();
    }

    public int MapperNumber => 206;
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
    public bool Unbanked32KPrg => _unbanked32KPrg;
    public bool HasFourScreenRam => _fourScreenRam.Length != 0;
    public VirtualHardwareNesMirroring Mirroring => _mirroring;
    public byte BankSelectRegister => _bankSelect;
    public IReadOnlyList<byte> BankRegisters => _bankRegisters;
    public int PrgRamSizeBytes => _prgRam.Length;
    public int PrgBankCount => _prg.Length / PrgBankSize;
    public int ChrBankCount => _chr.Length / ChrBankSize;
    public int SelectedPrgBank0 => _prg.Length == 0 ? 0 : _prgWindowBases[0] / PrgBankSize;
    public int SelectedPrgBank1 => _prg.Length == 0 ? 0 : _prgWindowBases[1] / PrgBankSize;
    public int FixedPrgBank0 => _prg.Length == 0 ? 0 : _prgWindowBases[2] / PrgBankSize;
    public int FixedPrgBank1 => _prg.Length == 0 ? 0 : _prgWindowBases[3] / PrgBankSize;

    public ulong MapperWriteCount { get; private set; }
    public ulong IgnoredMapperWriteCount { get; private set; }
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
        if (image.MapperNumber != 206)
            throw new NotSupportedException($"Mapper {image.MapperNumber} is not DxROM/Namco-108-family hardware.");

        ResolveVariant(image);

        if (image.PrgRom.Length < 4 * PrgBankSize || image.PrgRom.Length % PrgBankSize != 0)
            throw new ArgumentException("DxROM PRG ROM must contain at least four whole 8 KiB banks.", nameof(image));
        var prgBankCount = image.PrgRom.Length / PrgBankSize;
        if (prgBankCount > 16)
            throw new NotSupportedException("Mapper 206 exposes at most sixteen 8 KiB PRG-ROM banks (128 KiB).");
        if (!IsPowerOfTwo(prgBankCount))
            throw new NotSupportedException("DxROM PRG ROM must expose a power-of-two bank count so package outputs map directly to ROM address pins.");
        if (_unbanked32KPrg && prgBankCount != 4)
            throw new NotSupportedException("Mapper 206 submapper 1 represents 3407/3417/3451 boards with one directly-wired 32 KiB PRG ROM.");

        if (image.ChrRom.Length == 0)
            throw new NotSupportedException("Standard Mapper-206 DxROM/Namco-108-family boards require CHR ROM; CHR-RAM variants need distinct cartridge hardware.");
        if (image.ChrRom.Length % ChrBankSize != 0)
            throw new ArgumentException("DxROM CHR ROM must contain whole 1 KiB banks.", nameof(image));
        var chrBankCount = image.ChrRom.Length / ChrBankSize;
        if (chrBankCount > 64)
            throw new NotSupportedException("Mapper 206 exposes at most sixty-four 1 KiB CHR-ROM banks (64 KiB).");
        if (!IsPowerOfTwo(chrBankCount))
            throw new NotSupportedException("DxROM CHR ROM must expose a power-of-two bank count so package outputs map directly to ROM address pins.");
        if (image.HasExplicitRamSizes && image.TotalChrRamSizeBytes != 0)
            throw new NotSupportedException("Mapper 206 does not combine CHR ROM with CHR RAM on standard DxROM/Namco-108-family boards.");

        var prgRamSize = ResolveOptionalPrgRamSize(image);
        if (prgRamSize is not (0 or OptionalPrgRamSize))
            throw new NotSupportedException($"Mapper 206 supports no standard PRG RAM; the known MIMIC-1 prototype exception uses exactly {OptionalPrgRamSize:N0} bytes.");

        _prg = image.PrgRom.ToArray();
        _chr = image.ChrRom.ToArray();
        _prgRam = prgRamSize == 0 ? [] : new byte[OptionalPrgRamSize];
        _fourScreenRam = image.Mirroring == VirtualHardwareNesMirroring.FourScreen
            ? new byte[FourScreenRamChipSize]
            : [];
        _mirroring = image.Mirroring;
        _prgBankMask = (byte)(prgBankCount - 1);
        _chrBankMask = (byte)(chrBankCount - 1);
        IsInserted = true;
        ApplyResetState();
        RefreshPpuDataWakeState();
    }

    public void Eject()
    {
        IsInserted = false;
        _prg = [];
        _chr = [];
        _prgRam = [];
        _fourScreenRam = [];
        _cpuReadAddressSelected = false;
        _cpuCycleRomSelected = false;
        _cpuCyclePrgRamSelected = false;
        _ppuReadActive = false;
        _ppuWriteActive = false;
        RefreshPpuDataWakeState();
        ReleaseOutputs();
    }

    private void ResolveVariant(VirtualHardwareNesRomImage image)
    {
        _unbanked32KPrg = false;
        if (image.HeaderFormat == VirtualHardwareNesHeaderFormat.INes || image.SubmapperNumber is null)
            return;

        switch (image.SubmapperNumber.Value)
        {
            case 0:
                break;
            case 1:
                _unbanked32KPrg = true;
                break;
            default:
                throw new NotSupportedException($"Mapper 206 submapper {image.SubmapperNumber} is not defined for this DxROM/Namco-108-family package.");
        }
    }

    private static int ResolveOptionalPrgRamSize(VirtualHardwareNesRomImage image)
    {
        if (image.HasExplicitRamSizes)
            return image.TotalPrgRamSizeBytes;

        // Legacy iNES commonly infers an 8 KiB PRG-RAM field even for boards
        // that physically contain none. Only the battery indication is useful
        // for the known MIMIC-1 prototype exception.
        return image.HasBatteryBackedMemory ? OptionalPrgRamSize : 0;
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    private bool IsPowered() =>
        IsInserted && Vcc.SampledLevel == DigitalLevel.High && Gnd.SampledLevel == DigitalLevel.Low;

    private void ApplyResetState()
    {
        _bankSelect = 0;
        Array.Clear(_bankRegisters);
        _cpuReadAddressSelected = false;
        _cpuSelectedAddress = 0;
        _cpuSelectedData = 0;
        _cpuCycleRomSelected = false;
        _cpuCyclePrgRamSelected = false;
        _cpuCycleLogicalAddress = 0;
        _ppuReadActive = false;
        _ppuWriteActive = false;
        MapperWriteCount = 0;
        IgnoredMapperWriteCount = 0;
        LastMapperWriteAddress = 0;
        LastMapperWriteData = 0;
        CpuReadCount = 0;
        LastCpuReadAddress = 0;
        LastCpuReadData = 0;
        PpuReadCount = 0;
        PpuWriteCount = 0;
        RefreshDecodedBanks();
        ReleaseOutputs();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RefreshDecodedBanks()
    {
        if (_prg.Length != 0)
        {
            if (_unbanked32KPrg)
            {
                _prgWindowBases[0] = 0;
                _prgWindowBases[1] = PrgBankSize;
                _prgWindowBases[2] = 2 * PrgBankSize;
                _prgWindowBases[3] = 3 * PrgBankSize;
            }
            else
            {
                var last = (_prg.Length / PrgBankSize) - 1;
                var secondLast = last - 1;
                _prgWindowBases[0] = (_bankRegisters[6] & _prgBankMask) * PrgBankSize;
                _prgWindowBases[1] = (_bankRegisters[7] & _prgBankMask) * PrgBankSize;
                _prgWindowBases[2] = secondLast * PrgBankSize;
                _prgWindowBases[3] = last * PrgBankSize;
            }
        }
        else Array.Clear(_prgWindowBases);

        if (_chr.Length == 0)
        {
            Array.Clear(_chrWindowBases);
            return;
        }

        var r0 = _bankRegisters[0] & 0x3E & _chrBankMask;
        var r1 = _bankRegisters[1] & 0x3E & _chrBankMask;
        SetChrWindow(0, r0); SetChrWindow(1, r0 + 1);
        SetChrWindow(2, r1); SetChrWindow(3, r1 + 1);
        SetChrWindow(4, _bankRegisters[2] & 0x3F);
        SetChrWindow(5, _bankRegisters[3] & 0x3F);
        SetChrWindow(6, _bankRegisters[4] & 0x3F);
        SetChrWindow(7, _bankRegisters[5] & 0x3F);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetChrWindow(int window, int bank) =>
        _chrWindowBases[window] = (bank & _chrBankMask) * ChrBankSize;

    private void RefreshPpuDataWakeState()
    {
        var writable = _fourScreenRam.Length != 0;
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
            _cpuCyclePrgRamSelected = false;
            _ppuReadActive = false;
            _ppuWriteActive = false;
            ReleaseOutputs();
            RefreshPpuDataWakeState();
            return;
        }

        if (powerChanged || ppuAddressOrControlChanged) RefreshPpuDataWakeState();
        var ppuDataCanMatter = ppuDataChanged && _fourScreenRam.Length != 0
            && PpuWriteBar.SampledLevel == DigitalLevel.Low;
        if (powerChanged || ppuAddressOrControlChanged || ppuDataCanMatter)
            ProcessPpuPort();

        if (!powerChanged && cpuM2Changed && CpuM2.SampledLevel == DigitalLevel.Low)
            CompleteCpuTransaction();

        if (powerChanged || cpuAddressOrControlChanged || cpuRomSelectChanged || cpuM2Changed)
            UpdateCpuOutput();
    }

    private void UpdateCpuOutput()
    {
        if (!CpuAddress.TrySample(out var rawAddress))
        {
            _cpuReadAddressSelected = false;
            _cpuCycleRomSelected = false;
            _cpuCyclePrgRamSelected = false;
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
        _cpuCyclePrgRamSelected = ramSelected;
        _cpuCycleLogicalAddress = romSelected ? (ushort)(0x8000 | address) : address;

        _cpuReadAddressSelected = CpuReadWrite.SampledLevel == DigitalLevel.High
            && (romSelected || ramSelected);
        if (!_cpuReadAddressSelected)
        {
            CpuData.Release();
            return;
        }

        _cpuSelectedAddress = _cpuCycleLogicalAddress;
        _cpuSelectedData = romSelected
            ? ReadPrg(_cpuSelectedAddress)
            : _prgRam[address & 0x1FFF];
        CpuData.Drive(_cpuSelectedData);
    }

    private void CompleteCpuTransaction()
    {
        var writeCycle = CpuReadWrite.SampledLevel == DigitalLevel.Low;
        if (!writeCycle)
        {
            if (!_cpuReadAddressSelected) return;
            CpuReadCount++;
            LastCpuReadAddress = _cpuSelectedAddress;
            LastCpuReadData = _cpuSelectedData;
            return;
        }

        if (!CpuData.TrySample(out var rawData)) return;
        var data = (byte)rawData;
        if (_cpuCyclePrgRamSelected)
        {
            _prgRam[_cpuCycleLogicalAddress & 0x1FFF] = data;
            return;
        }
        if (_cpuCycleRomSelected)
            WriteMapperRegister(_cpuCycleLogicalAddress, data);
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
            if (readSelected)
            {
                PpuData.Drive(ReadChr(address));
                if (!_ppuReadActive) PpuReadCount++;
            }
            else PpuData.Release();
        }
        else if (_fourScreenRam.Length != 0 && address < 0x3F00)
        {
            readSelected = PpuReadBar.SampledLevel == DigitalLevel.Low;
            writeSelected = PpuWriteBar.SampledLevel == DigitalLevel.Low;
            var index = (address - 0x2000) & 0x1FFF;
            if (readSelected)
            {
                PpuData.Drive(_fourScreenRam[index]);
                if (!_ppuReadActive) PpuReadCount++;
            }
            else if (writeSelected && PpuData.TrySample(out var data))
            {
                _fourScreenRam[index] = (byte)data;
                if (!_ppuWriteActive) PpuWriteCount++;
                PpuData.Release();
            }
            else PpuData.Release();
        }
        else PpuData.Release();

        _ppuReadActive = readSelected;
        _ppuWriteActive = writeSelected;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DriveCiramOutputs(ushort address)
    {
        if (_fourScreenRam.Length != 0)
        {
            CiramChipEnableBar.Drive(DigitalLevel.High);
            CiramA10.Drive(DigitalLevel.Low);
            return;
        }

        CiramChipEnableBar.Drive((address & 0x2000) != 0 ? DigitalLevel.Low : DigitalLevel.High);
        var sourceBit = _mirroring == VirtualHardwareNesMirroring.Horizontal ? 11 : 10;
        CiramA10.Drive((address & (1 << sourceBit)) != 0 ? DigitalLevel.High : DigitalLevel.Low);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadPrg(ushort address)
    {
        var window = (address >> 13) & 0x03;
        return _prg[_prgWindowBases[window] + (address & 0x1FFF)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadChr(ushort address)
    {
        var window = (address >> 10) & 0x07;
        return _chr[_chrWindowBases[window] + (address & 0x03FF)];
    }

    private void WriteMapperRegister(ushort address, byte value)
    {
        switch (address & 0xE001)
        {
            case 0x8000:
                MapperWriteCount++;
                LastMapperWriteAddress = address;
                LastMapperWriteData = value;
                _bankSelect = (byte)(value & 0x07);
                break;
            case 0x8001:
            {
                MapperWriteCount++;
                LastMapperWriteAddress = address;
                LastMapperWriteData = value;
                var register = _bankSelect & 0x07;
                var latched = register switch
                {
                    <= 1 => (byte)(value & 0x3E), // only CHR bank outputs 5..1 exist
                    >= 6 => (byte)(value & 0x0F), // only PRG bank outputs 3..0 exist
                    _ => (byte)(value & 0x3F)
                };
                _bankRegisters[register] = latched;
                RefreshDecodedBanks();
                break;
            }
            default:
                IgnoredMapperWriteCount++;
                break;
        }
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
        if (address >= 0x8000) WriteMapperRegister(address, value);
        else if (_prgRam.Length != 0) _prgRam[address & 0x1FFF] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadPpuCompiled(ushort address)
    {
        PpuReadCount++;
        if (address < 0x2000) return ReadChr(address);
        return _fourScreenRam[(address - 0x2000) & 0x1FFF];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WritePpuCompiled(ushort address, byte value)
    {
        if (_fourScreenRam.Length == 0 || address < 0x2000 || address >= 0x3F00) return;
        _fourScreenRam[(address - 0x2000) & 0x1FFF] = value;
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

        if (_fourScreenRam.Length != 0)
        {
            yield return new CompiledBusTargetDescriptor(
                this,
                PpuAddress.Pins,
                PpuData.Pins,
                new[] { new CompiledPinCondition(PpuAddress.Pins[13], DigitalLevel.High) },
                new[] { new CompiledPinCondition(PpuAddress.Pins[13], DigitalLevel.High) },
                CompiledBusReadPhase.Complete,
                address => ReadPpuCompiled((ushort)address),
                (address, value) => WritePpuCompiled((ushort)address, value),
                isSelected: (address, _) => address < 0x3F00);
        }
    }

    bool ICompiledStaticCombinationalComponent.TryEvaluateCompiledStaticOutput(
        DigitalPin output,
        Func<DigitalPin, DigitalLevel> sampleInput,
        out CompiledDriveState drive)
    {
        if (ReferenceEquals(output, CiramChipEnableBar))
        {
            if (_fourScreenRam.Length != 0)
            {
                drive = new CompiledDriveState(DigitalLevel.High);
                return true;
            }
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
            if (_fourScreenRam.Length != 0)
            {
                drive = new CompiledDriveState(DigitalLevel.Low);
                return true;
            }
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
        ((ICompiledStaticCombinationalComponent)this)
            .TryEvaluateCompiledStaticOutput(output, sampleInput, out drive);
}
