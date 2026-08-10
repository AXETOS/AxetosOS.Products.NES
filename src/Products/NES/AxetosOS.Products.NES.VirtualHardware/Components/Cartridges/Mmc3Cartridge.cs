using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

/// <summary>
/// Mapper-4/MMC3-family replaceable cartridge hardware. PRG/CHR banking,
/// mirroring, work-RAM protection and the filtered PPU-A12 IRQ counter are all
/// package-local behavior. The motherboard and whole-circuit compiler see only
/// connector pins and product-agnostic hardware facets.
/// </summary>
public sealed class Mmc3Cartridge : VirtualHardwareComponent, IReplaceableCartridgeHardware,
    ICompiledBusTargetProvider, ICompiledCombinationalComponent, ICompiledStaticCombinationalComponent
{
    private const int PrgBankSize = 8 * 1024;
    private const int ChrBankSize = 1 * 1024;
    private const int StandardPrgRamSize = 8 * 1024;
    private const int Mmc6PrgRamSize = 1 * 1024;
    private const int StandardChrRamSize = 8 * 1024;
    // Nintendo TxROM four-screen boards fit an 8 KiB SRAM but only decode the
    // lower 4 KiB nametable address space; preserve the physical chip capacity.
    private const int FourScreenRamChipSize = 8 * 1024;
    private const int A12FilterM2Falls = 3;

    private byte[] _prg = [];
    private byte[] _chr = [];
    private byte[] _prgRam = [];
    private byte[] _fourScreenRam = [];
    private readonly byte[] _bankRegisters = new byte[8];
    private readonly int[] _prgWindowBases = new int[4];
    private readonly int[] _chrWindowBases = new int[8];

    private bool _chrRam;
    private bool _mmc6;
    private bool _hardwiredMirroring;
    private bool _oldIrqRevision;
    private VirtualHardwareNesMirroring _mirroring;
    private byte _bankSelect;
    private byte _ramProtect;
    private byte _prgBankMask;
    private byte _chrBankMask;

    private byte _irqLatch;
    private byte _irqCounter;
    private bool _irqReloadPending;
    private bool _irqEnabled;
    private bool _irqAsserted;
    private bool _ppuA12High;
    private int _ppuA12LowM2Falls;

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

    public Mmc3Cartridge(string componentId) : base(componentId)
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

        // CPU/PPU data are sampled only at package-defined transaction points.
        // Pins still retain all physically delivered levels, but routine data
        // transitions need not wake the package unless writable PPU RAM is active.
        CpuData.SetOwnerWakeEnabled(false);
        PpuData.SetOwnerWakeEnabled(false);
        ApplyResetState();
    }

    public int MapperNumber => 4;
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
    public bool IsMmc6 => _mmc6;
    public bool IsChrRam => _chrRam;
    public bool HasFourScreenRam => _fourScreenRam.Length != 0;
    public bool HardwiredMirroring => _hardwiredMirroring;
    public bool OldIrqRevision => _oldIrqRevision;
    public VirtualHardwareNesMirroring Mirroring => _mirroring;
    public byte BankSelectRegister => _bankSelect;
    public IReadOnlyList<byte> BankRegisters => _bankRegisters;
    public byte PrgRamProtectRegister => _ramProtect;
    public int PrgRamSizeBytes => _prgRam.Length;
    public byte IrqLatch => _irqLatch;
    public byte IrqCounter => _irqCounter;
    public bool IrqReloadPending => _irqReloadPending;
    public bool IrqEnabled => _irqEnabled;
    public bool IrqAsserted => _irqAsserted;
    public ulong QualifiedA12RiseCount { get; private set; }
    public ulong IrqAssertCount { get; private set; }
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
        if (image.MapperNumber != 4)
            throw new NotSupportedException($"Mapper {image.MapperNumber} is not MMC3/MMC6.");
        if (image.PrgRom.Length < 4 * PrgBankSize || image.PrgRom.Length % PrgBankSize != 0)
            throw new ArgumentException("MMC3 PRG ROM must contain at least four whole 8 KiB banks.", nameof(image));

        var prgBankCount = image.PrgRom.Length / PrgBankSize;
        if (prgBankCount > 64)
            throw new NotSupportedException("Standard MMC3 exposes at most 64 8 KiB PRG-ROM banks (512 KiB).");
        if (!IsPowerOfTwo(prgBankCount))
            throw new NotSupportedException("MMC3 PRG ROM must expose a power-of-two bank count so package outputs map directly to ROM address pins.");

        ResolveVariant(image);

        if (image.ChrRom.Length != 0)
        {
            if (image.ChrRom.Length % ChrBankSize != 0)
                throw new ArgumentException("MMC3 CHR ROM must contain whole 1 KiB banks.", nameof(image));
            var chrBankCount = image.ChrRom.Length / ChrBankSize;
            if (chrBankCount > 256 || !IsPowerOfTwo(chrBankCount))
                throw new NotSupportedException("Standard MMC3 CHR ROM must expose a power-of-two count of at most 256 1 KiB banks (256 KiB).");
            if (image.HasExplicitRamSizes && image.TotalChrRamSizeBytes != 0)
                throw new NotSupportedException("Mixed CHR-ROM/CHR-RAM boards use different mapper hardware (for example Mapper 119/TQROM).");
            _chrRam = false;
            _chr = image.ChrRom.ToArray();
        }
        else
        {
            var chrRamSize = ResolveChrRamSize(image);
            if (chrRamSize != StandardChrRamSize)
                throw new NotSupportedException($"Mapper-4 CHR-RAM boards currently require one {StandardChrRamSize:N0}-byte RAM chip; image declares {chrRamSize:N0} bytes.");
            _chrRam = true;
            _chr = new byte[StandardChrRamSize];
        }

        var prgRamSize = ResolvePrgRamSize(image);
        if (_mmc6)
        {
            if (prgRamSize is not (0 or Mmc6PrgRamSize))
                throw new NotSupportedException($"MMC6 contains 1 KiB PRG RAM; image declares {prgRamSize:N0} bytes.");
            _prgRam = new byte[Mmc6PrgRamSize];
        }
        else
        {
            if (prgRamSize is not (0 or StandardPrgRamSize))
                throw new NotSupportedException($"Standard MMC3 boards support zero or {StandardPrgRamSize:N0} bytes of PRG RAM; image declares {prgRamSize:N0} bytes.");
            _prgRam = prgRamSize == 0 ? [] : new byte[StandardPrgRamSize];
        }

        _fourScreenRam = image.Mirroring == VirtualHardwareNesMirroring.FourScreen
            ? new byte[FourScreenRamChipSize]
            : [];
        _mirroring = image.Mirroring;
        _prg = image.PrgRom.ToArray();
        _prgBankMask = (byte)(prgBankCount - 1);
        _chrBankMask = (byte)((_chr.Length / ChrBankSize) - 1);
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
        SetIrqAsserted(false);
        RefreshPpuDataWakeState();
        ReleaseOutputs();
    }

    private void ResolveVariant(VirtualHardwareNesRomImage image)
    {
        _mmc6 = false;
        _hardwiredMirroring = false;
        _oldIrqRevision = false;

        if (image.HeaderFormat == VirtualHardwareNesHeaderFormat.INes || image.SubmapperNumber is null)
            return;

        switch (image.SubmapperNumber.Value)
        {
            case 0: // Sharp MMC3
                break;
            case 1: // MMC6
                _mmc6 = true;
                break;
            case 2: // MMC3C with hard-wired mirroring
                _hardwiredMirroring = true;
                break;
            case 4: // NEC/old IRQ behavior
                _oldIrqRevision = true;
                break;
            case 3:
                throw new NotSupportedException("Mapper 4 submapper 3 uses the Acclaim MC-ACC IRQ circuit and requires a distinct cartridge package implementation.");
            case 5:
                throw new NotSupportedException("Mapper 4 submapper 5 includes a T9552 scrambling device and requires additional cartridge hardware.");
            default:
                throw new NotSupportedException($"Mapper 4 submapper {image.SubmapperNumber} is not implemented by this MMC3-family package.");
        }
    }

    private static int ResolvePrgRamSize(VirtualHardwareNesRomImage image)
    {
        if (image.PrgRamSizeBytes >= 0 || image.PrgNvRamSizeBytes >= 0)
            return checked(Math.Max(0, image.PrgRamSizeBytes) + Math.Max(0, image.PrgNvRamSizeBytes));
        // Legacy iNES mapper 4 historically implies an optional 8 KiB work-RAM
        // footprint. NES 2.0 images can explicitly declare zero.
        return image.HasExplicitRamSizes ? 0 : StandardPrgRamSize;
    }

    private static int ResolveChrRamSize(VirtualHardwareNesRomImage image)
    {
        if (image.ChrRamSizeBytes >= 0 || image.ChrNvRamSizeBytes >= 0)
            return checked(Math.Max(0, image.ChrRamSizeBytes) + Math.Max(0, image.ChrNvRamSizeBytes));
        return StandardChrRamSize;
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    private bool IsPowered() =>
        IsInserted && Vcc.SampledLevel == DigitalLevel.High && Gnd.SampledLevel == DigitalLevel.Low;

    private void ApplyResetState()
    {
        _bankSelect = 0;
        Array.Clear(_bankRegisters);
        _ramProtect = 0;
        _irqLatch = 0;
        _irqCounter = 0;
        _irqReloadPending = false;
        _irqEnabled = false;
        _ppuA12High = false;
        _ppuA12LowM2Falls = A12FilterM2Falls;
        _cpuReadAddressSelected = false;
        _cpuSelectedAddress = 0;
        _cpuSelectedData = 0;
        _cpuCycleRomSelected = false;
        _cpuCyclePrgRamSelected = false;
        _cpuCycleLogicalAddress = 0;
        _ppuReadActive = false;
        _ppuWriteActive = false;
        QualifiedA12RiseCount = 0;
        IrqAssertCount = 0;
        MapperWriteCount = 0;
        LastMapperWriteAddress = 0;
        LastMapperWriteData = 0;
        CpuReadCount = 0;
        LastCpuReadAddress = 0;
        LastCpuReadData = 0;
        PpuReadCount = 0;
        PpuWriteCount = 0;
        RefreshDecodedBanks();
        SetIrqAsserted(false);
        ReleaseOutputsExceptIrq();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RefreshDecodedBanks()
    {
        if (_prg.Length != 0)
        {
            var last = (_prg.Length / PrgBankSize) - 1;
            var secondLast = last - 1;
            var r6 = _bankRegisters[6] & 0x3F & _prgBankMask;
            var r7 = _bankRegisters[7] & 0x3F & _prgBankMask;
            var prgMode = (_bankSelect & 0x40) != 0;

            _prgWindowBases[0] = (prgMode ? secondLast : r6) * PrgBankSize;
            _prgWindowBases[1] = r7 * PrgBankSize;
            _prgWindowBases[2] = (prgMode ? r6 : secondLast) * PrgBankSize;
            _prgWindowBases[3] = last * PrgBankSize;
        }
        else Array.Clear(_prgWindowBases);

        if (_chr.Length == 0)
        {
            Array.Clear(_chrWindowBases);
            return;
        }

        var r0 = _bankRegisters[0] & 0xFE & _chrBankMask;
        var r1 = _bankRegisters[1] & 0xFE & _chrBankMask;
        var r2 = _bankRegisters[2] & _chrBankMask;
        var r3 = _bankRegisters[3] & _chrBankMask;
        var r4 = _bankRegisters[4] & _chrBankMask;
        var r5 = _bankRegisters[5] & _chrBankMask;
        var chrMode = (_bankSelect & 0x80) != 0;

        if (!chrMode)
        {
            SetChrWindow(0, r0); SetChrWindow(1, r0 + 1);
            SetChrWindow(2, r1); SetChrWindow(3, r1 + 1);
            SetChrWindow(4, r2); SetChrWindow(5, r3);
            SetChrWindow(6, r4); SetChrWindow(7, r5);
        }
        else
        {
            SetChrWindow(0, r2); SetChrWindow(1, r3);
            SetChrWindow(2, r4); SetChrWindow(3, r5);
            SetChrWindow(4, r0); SetChrWindow(5, r0 + 1);
            SetChrWindow(6, r1); SetChrWindow(7, r1 + 1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetChrWindow(int window, int bank) =>
        _chrWindowBases[window] = (bank & _chrBankMask) * ChrBankSize;

    private void RefreshPpuDataWakeState()
    {
        var writable = _chrRam || _fourScreenRam.Length != 0;
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
            SetIrqAsserted(false);
            ReleaseOutputsExceptIrq();
            RefreshPpuDataWakeState();
            return;
        }

        if (powerChanged || ppuAddressOrControlChanged) RefreshPpuDataWakeState();
        var ppuDataCanMatter = ppuDataChanged && (_chrRam || _fourScreenRam.Length != 0)
            && PpuWriteBar.SampledLevel == DigitalLevel.Low;
        if (powerChanged || ppuAddressOrControlChanged || ppuDataCanMatter)
            ProcessPpuPort();

        if (!powerChanged && cpuM2Changed && CpuM2.SampledLevel == DigitalLevel.Low)
        {
            ObserveM2FallingEdge();
            CompleteCpuTransaction();
        }

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
        var ramWindow = m2High && CpuRomSelectBar.SampledLevel == DigitalLevel.High
            && (address & 0x6000) == 0x6000;
        var writeCycle = CpuReadWrite.SampledLevel == DigitalLevel.Low;
        var ramSelected = ramWindow && IsPrgRamSelected(address, writeCycle);

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
            : ReadPrgRam(address);
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
            WritePrgRam(_cpuCycleLogicalAddress, data);
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
        ObservePpuAddress(address);
        DriveCiramOutputs(address);

        var readSelected = false;
        var writeSelected = false;
        if (address < 0x2000)
        {
            readSelected = PpuReadBar.SampledLevel == DigitalLevel.Low;
            writeSelected = _chrRam && PpuWriteBar.SampledLevel == DigitalLevel.Low;
            if (readSelected)
            {
                PpuData.Drive(ReadChr(address));
                if (!_ppuReadActive) PpuReadCount++;
            }
            else if (writeSelected && PpuData.TrySample(out var data))
            {
                WriteChr(address, (byte)data);
                if (!_ppuWriteActive) PpuWriteCount++;
                PpuData.Release();
            }
            else PpuData.Release();
        }
        else if (_fourScreenRam.Length != 0 && address < 0x3F00)
        {
            readSelected = PpuReadBar.SampledLevel == DigitalLevel.Low;
            writeSelected = PpuWriteBar.SampledLevel == DigitalLevel.Low;
            var index = (address - 0x2000) & 0x0FFF;
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

    private void RefreshCiramOutputsFromPins()
    {
        if (PpuAddress.TrySample(out var raw)) DriveCiramOutputs((ushort)(raw & 0x3FFF));
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteChr(ushort address, byte value)
    {
        if (!_chrRam) return;
        var window = (address >> 10) & 0x07;
        _chr[_chrWindowBases[window] + (address & 0x03FF)] = value;
    }

    private bool AnyMmc6ReadBankEnabled => (_ramProtect & 0xA0) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsPrgRamSelected(ushort address, bool writeCycle)
    {
        if (_prgRam.Length == 0) return false;
        if (!_mmc6)
        {
            if ((address & 0x6000) != 0x6000 || (_ramProtect & 0x80) == 0) return false;
            return !writeCycle || (_ramProtect & 0x40) == 0;
        }

        if ((address & 0x7000) != 0x7000 || (_bankSelect & 0x20) == 0) return false;
        if (!AnyMmc6ReadBankEnabled) return false;
        if (!writeCycle) return true; // disabled half is still driven as zero when the other half is readable
        var highHalf = (address & 0x0200) != 0;
        var readEnable = highHalf ? (_ramProtect & 0x80) != 0 : (_ramProtect & 0x20) != 0;
        var writeEnable = highHalf ? (_ramProtect & 0x40) != 0 : (_ramProtect & 0x10) != 0;
        return readEnable && writeEnable;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadPrgRam(ushort address)
    {
        if (!_mmc6) return _prgRam[address & 0x1FFF];
        var highHalf = (address & 0x0200) != 0;
        var readEnable = highHalf ? (_ramProtect & 0x80) != 0 : (_ramProtect & 0x20) != 0;
        if (!readEnable) return 0;
        return _prgRam[address & 0x03FF];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WritePrgRam(ushort address, byte value)
    {
        if (_prgRam.Length == 0 || !IsPrgRamSelected(address, writeCycle: true)) return;
        _prgRam[_mmc6 ? address & 0x03FF : address & 0x1FFF] = value;
    }

    private void WriteMapperRegister(ushort address, byte value)
    {
        MapperWriteCount++;
        LastMapperWriteAddress = address;
        LastMapperWriteData = value;

        switch (address & 0xE001)
        {
            case 0x8000:
                _bankSelect = value;
                if (_mmc6 && (_bankSelect & 0x20) == 0) _ramProtect = 0;
                RefreshDecodedBanks();
                break;
            case 0x8001:
            {
                var register = _bankSelect & 0x07;
                if (register <= 1) value &= 0xFE;
                else if (register >= 6) value &= 0x3F;
                _bankRegisters[register] = value;
                RefreshDecodedBanks();
                break;
            }
            case 0xA000:
                if (_fourScreenRam.Length == 0 && !_hardwiredMirroring)
                {
                    _mirroring = (value & 0x01) == 0
                        ? VirtualHardwareNesMirroring.Vertical
                        : VirtualHardwareNesMirroring.Horizontal;
                    RefreshCiramOutputsFromPins();
                }
                break;
            case 0xA001:
                if (!_mmc6 || (_bankSelect & 0x20) != 0) _ramProtect = value;
                break;
            case 0xC000:
                _irqLatch = value;
                break;
            case 0xC001:
                _irqCounter = 0;
                _irqReloadPending = true;
                break;
            case 0xE000:
                _irqEnabled = false;
                SetIrqAsserted(false);
                break;
            case 0xE001:
                _irqEnabled = true;
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ObservePpuAddress(ushort address)
    {
        var high = (address & 0x1000) != 0;
        if (high == _ppuA12High) return;

        if (high)
        {
            if (_ppuA12LowM2Falls >= A12FilterM2Falls)
                ClockIrqCounter();
            _ppuA12High = true;
            _ppuA12LowM2Falls = 0;
        }
        else
        {
            _ppuA12High = false;
            _ppuA12LowM2Falls = 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ObserveM2FallingEdge()
    {
        if (_ppuA12High || _ppuA12LowM2Falls >= A12FilterM2Falls) return;
        _ppuA12LowM2Falls++;
    }

    private void ClockIrqCounter()
    {
        QualifiedA12RiseCount++;
        var before = _irqCounter;
        if (_irqCounter == 0 || _irqReloadPending)
        {
            _irqCounter = _irqLatch;
            _irqReloadPending = false;
        }
        else _irqCounter--;

        var fire = _oldIrqRevision
            ? before != 0 && _irqCounter == 0
            : _irqCounter == 0;
        if (fire && _irqEnabled) SetIrqAsserted(true);
    }

    private void SetIrqAsserted(bool asserted)
    {
        if (_irqAsserted == asserted)
        {
            if (asserted) IrqBar.Drive(DigitalLevel.Low);
            else IrqBar.Release();
            return;
        }
        _irqAsserted = asserted;
        if (asserted)
        {
            IrqAssertCount++;
            IrqBar.Drive(DigitalLevel.Low);
        }
        else IrqBar.Release();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadCpuCompiled(ushort address)
    {
        var value = address >= 0x8000 ? ReadPrg(address) : ReadPrgRam(address);
        CpuReadCount++;
        LastCpuReadAddress = address;
        LastCpuReadData = value;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteCpuCompiled(ushort address, byte value)
    {
        if (address >= 0x8000) WriteMapperRegister(address, value);
        else WritePrgRam(address, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ObserveCompiledCpuBusCycle(bool _) => ObserveM2FallingEdge();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ObserveCompiledPpuReadBegin(int address) => ObservePpuAddress((ushort)address);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ObserveCompiledPpuWrite(int address, byte _) => ObservePpuAddress((ushort)address);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadPpuCompiled(ushort address)
    {
        PpuReadCount++;
        if (address < 0x2000) return ReadChr(address);
        return _fourScreenRam[(address - 0x2000) & 0x0FFF];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WritePpuCompiled(ushort address, byte value)
    {
        if (address < 0x2000)
        {
            if (!_chrRam) return;
            WriteChr(address, value);
        }
        else if (_fourScreenRam.Length != 0 && address < 0x3F00)
            _fourScreenRam[(address - 0x2000) & 0x0FFF] = value;
        else return;
        PpuWriteCount++;
    }

    private void ReleaseOutputsExceptIrq()
    {
        CpuData.Release();
        PpuData.Release();
        CiramChipEnableBar.Release();
        CiramA10.Release();
    }

    private void ReleaseOutputs()
    {
        ReleaseOutputsExceptIrq();
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
            ObserveCompiledCpuBusCycle,
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
                ObserveCompiledCpuBusCycle,
                (address, write) => IsPrgRamSelected((ushort)address, write),
                CompiledBusWritePhase.Complete);
        }

        Action<int, byte>? chrWrite = _chrRam
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
            _chrRam
                ? new[]
                {
                    new CompiledPinCondition(PpuAddress.Pins[13], DigitalLevel.Low),
                    new CompiledPinCondition(PpuWriteBar, DigitalLevel.Low)
                }
                : Array.Empty<CompiledPinCondition>(),
            CompiledBusReadPhase.Complete,
            address => ReadPpuCompiled((ushort)address),
            chrWrite);

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

        // Address-only observer. It does not drive read data; the compiler keeps
        // it as a physical bus observer so package circuitry can react to PPU
        // A12 edges even when another chip (for example nametable RAM) owns D0-D7.
        yield return new CompiledBusTargetDescriptor(
            this,
            PpuAddress.Pins,
            PpuData.Pins,
            Array.Empty<CompiledPinCondition>(),
            Array.Empty<CompiledPinCondition>(),
            CompiledBusReadPhase.Complete,
            null,
            ObserveCompiledPpuWrite,
            observeReadBegin: ObserveCompiledPpuReadBegin);
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

        if (ReferenceEquals(output, CiramA10) && _hardwiredMirroring && _fourScreenRam.Length == 0)
        {
            var sourceBit = _mirroring == VirtualHardwareNesMirroring.Horizontal ? 11 : 10;
            drive = new CompiledDriveState(sampleInput(PpuAddress.Pins[sourceBit]));
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
        if (((ICompiledStaticCombinationalComponent)this).TryEvaluateCompiledStaticOutput(output, sampleInput, out drive))
            return true;

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
            drive = new CompiledDriveState(_irqAsserted ? DigitalLevel.Low : DigitalLevel.HighImpedance);
            return true;
        }

        drive = default;
        return false;
    }
}
