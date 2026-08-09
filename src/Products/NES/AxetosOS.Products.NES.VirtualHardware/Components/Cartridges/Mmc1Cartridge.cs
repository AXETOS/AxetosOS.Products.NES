using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

public enum Mmc1PpuMappingKind
{
    Chr,
    Ciram,
    Unmapped
}

public enum Mmc1RegisterOperation
{
    Reset,
    Control,
    ChrBank0,
    ChrBank1,
    PrgBank
}

public readonly record struct Mmc1PpuMappingDiagnostic(
    ushort PpuAddress,
    Mmc1PpuMappingKind Kind,
    int PhysicalAddress,
    int Bank4K,
    int CiramPage,
    byte Control,
    byte ChrBank0,
    byte ChrBank1,
    byte PrgBank);

public readonly record struct Mmc1ChrReadTraceEvent(
    ulong PpuReadCount,
    ushort PpuAddress,
    int PhysicalAddress,
    int Bank4K,
    byte Data,
    byte Control,
    byte ChrBank0,
    byte ChrBank1,
    byte PrgBank);

public readonly record struct Mmc1RegisterTraceEvent(
    ulong MapperWriteCount,
    ulong PpuReadCountAtCommit,
    ulong PpuWriteCountAtCommit,
    ushort Address,
    byte Data,
    Mmc1RegisterOperation Operation,
    byte Control,
    byte ChrBank0,
    byte ChrBank1,
    byte PrgBank,
    byte ShiftRegister);

/// <summary>
/// MMC1 cartridge hardware unit. Bank selection, serial register loading,
/// nametable wiring and ROM/RAM decoding are entirely cartridge-local. The
/// motherboard sees only physical connector pins and does not know a mapper is
/// present.
/// </summary>
public sealed class Mmc1Cartridge : VirtualHardwareComponent, IReplaceableCartridgeHardware,
    ICompiledBusTargetProvider, ICompiledCombinationalComponent
{
    private const int PrgBankSize = 16 * 1024;
    private const int ChrBankSize = 4 * 1024;
    private const int StandardPrgRamWindowSize = 8 * 1024;
    private const int LegacyChrRamSize = 8 * 1024;
    private const ulong MapperWriteHashOffset = 14_695_981_039_346_656_037UL;
    private const ulong MapperWriteHashPrime = 1_099_511_628_211UL;

    private byte[] _prg = [];
    private byte[] _chr = [];
    private byte[] _prgRam = [];
    private bool _chrRam;
    private byte _shiftRegister = 0x10;
    private byte _control = 0x0C;
    private byte _chrBank0;
    private byte _chrBank1;
    private byte _prgBank;
    private bool _cpuReadAddressSelected;
    private ushort _cpuSelectedAddress;
    private byte _cpuSelectedData;
    private bool _ppuReadActive;
    private bool _ppuWriteActive;
    private bool _previousCpuCycleWasWrite;
    private bool _suppressCurrentSerialDataWrite;
    private bool _cpuCycleRomSelected;
    private bool _cpuCyclePrgRamSelected;
    private ushort _cpuCycleLogicalAddress;
    private ulong _mapperWriteHash = MapperWriteHashOffset;
    private readonly ulong _powerInputMask;
    private readonly ulong _cpuAddressControlInputMask;
    private readonly ulong _cpuM2InputMask;
    private readonly ulong _cpuRomSelectInputMask;
    private readonly ulong _ppuAddressControlInputMask;
    private readonly ulong _ppuDataInputMask;

    public Mmc1Cartridge(string componentId) : base(componentId)
    {
        Vcc = AddPin("VCC", PinDirection.Input);
        Gnd = AddPin("GND", PinDirection.Input);
        CpuAddress = new DigitalBus($"{componentId}.CPU.A",
            Enumerable.Range(0, 15).Select(i => AddPin($"CPU.A{i}", PinDirection.Input)).ToArray());
        CpuData = new DigitalBus($"{componentId}.CPU.D",
            Enumerable.Range(0, 8).Select(i => AddPin($"CPU.D{i}", PinDirection.Bidirectional)).ToArray());
        CpuReadWrite = AddPin("CPU.RW", PinDirection.Input);
        // CPU writes are captured at the end of the M2-qualified bus cycle.  The
        // RP2A0x package publishes its M2 rising edge and next-cycle bus outputs
        // as one atomic package change-set, so sampling on that rising edge would
        // observe the newly-started cycle rather than the cycle that just ended.
        // The falling edge is a real cartridge connector edge and occurs while
        // address, R/W and CPU data still represent the active transaction.
        CpuM2 = AddPin("CPU.M2", PinDirection.Input);
        CpuRomSelectBar = AddPin("CPU.ROMSEL_BAR", PinDirection.Input);
        // The console motherboard has already demultiplexed RP2C0x AD0-AD7
        // through its 74LS373 before the cartridge connector. MMC1 therefore
        // receives ordinary PPU A0-A13 address pins plus a separate D0-D7 bus.
        PpuAddress = new DigitalBus($"{componentId}.PPU.A",
            Enumerable.Range(0, 14).Select(i => AddPin($"PPU.A{i}", PinDirection.Input)).ToArray());
        PpuData = new DigitalBus($"{componentId}.PPU.D",
            Enumerable.Range(0, 8).Select(i => AddPin($"PPU.D{i}", PinDirection.Bidirectional)).ToArray());
        PpuReadBar = AddPin("PPU.RD_BAR", PinDirection.Input);
        PpuWriteBar = AddPin("PPU.WR_BAR", PinDirection.Input);
        CiramChipEnableBar = AddPin("CIRAM.CE_BAR", PinDirection.Output);
        CiramA10 = AddPin("CIRAM.A10", PinDirection.Output);
        IrqBar = AddPin("IRQ_BAR", PinDirection.Output);
        RegisterTraceOutput = new BufferedOutputPin<Mmc1RegisterTraceEvent>(
            $"{componentId}.REGISTER_TRACE");
        ChrReadTraceOutput = new BufferedOutputPin<Mmc1ChrReadTraceEvent>(
            $"{componentId}.CHR_READ_TRACE");

        _powerInputMask = Vcc.InputChangeMask | Gnd.InputChangeMask;
        _cpuAddressControlInputMask = CpuAddress.InputChangeMask | CpuReadWrite.InputChangeMask;
        _cpuM2InputMask = CpuM2.InputChangeMask;
        _cpuRomSelectInputMask = CpuRomSelectBar.InputChangeMask;
        _ppuAddressControlInputMask = PpuAddress.InputChangeMask |
            PpuReadBar.InputChangeMask | PpuWriteBar.InputChangeMask;
        _ppuDataInputMask = PpuData.InputChangeMask;

        // Data pins always retain delivered levels, but MMC1 consumes CPU data
        // only on an M2-qualified write and PPU data only during CHR-RAM writes.
        CpuData.SetOwnerWakeEnabled(false);
        PpuData.SetOwnerWakeEnabled(false);
        ApplyResetState();
    }

    public int MapperNumber => 1;
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
    public bool IsChrRam => _chrRam;
    public int PrgRamSizeBytes => _prgRam.Length;
    public int ChrRamSizeBytes => _chrRam ? _chr.Length : 0;
    public bool PrgRamEnabled => _prgRam.Length != 0 && (_prgBank & 0x10) == 0;
    public byte ControlRegister => _control;
    public byte ChrBank0Register => _chrBank0;
    public byte ChrBank1Register => _chrBank1;
    public byte PrgBankRegister => _prgBank;
    public byte SerialShiftRegister => _shiftRegister;
    public ulong CpuReadCount { get; private set; }
    public ushort LastCpuReadAddress { get; private set; }
    public byte LastCpuReadData { get; private set; }
    public ulong PpuReadCount { get; private set; }
    public ulong PpuWriteCount { get; private set; }
    public ulong MapperWriteCount { get; private set; }
    public ulong MapperResetWriteCount { get; private set; }
    public ulong MapperCommitCount { get; private set; }
    public ulong IgnoredConsecutiveMapperWriteCount { get; private set; }
    public ushort LastMapperWriteAddress { get; private set; }
    public byte LastMapperWriteData { get; private set; }
    public ulong MapperWriteHash => _mapperWriteHash;
    public BufferedOutputPin<Mmc1RegisterTraceEvent> RegisterTraceOutput { get; }
    public BufferedOutputPin<Mmc1ChrReadTraceEvent> ChrReadTraceOutput { get; }

    /// <summary>
    /// Inspects the cartridge-local PPU address translation without mutating
    /// hardware state. This is a laboratory diagnostic surface only: the
    /// motherboard and compiler never consume it.
    /// </summary>
    public Mmc1PpuMappingDiagnostic InspectPpuMapping(ushort ppuAddress)
    {
        ppuAddress &= 0x3FFF;
        if (ppuAddress < 0x2000)
        {
            var physical = ChrIndex(ppuAddress);
            return new Mmc1PpuMappingDiagnostic(
                ppuAddress,
                Mmc1PpuMappingKind.Chr,
                physical,
                physical / ChrBankSize,
                -1,
                _control,
                _chrBank0,
                _chrBank1,
                _prgBank);
        }

        if (ppuAddress < 0x3F00)
        {
            var mode = _control & 0x03;
            var ciramA10 = mode switch
            {
                0 => 0,
                1 => 1,
                2 => (ppuAddress >> 10) & 1,
                _ => (ppuAddress >> 11) & 1
            };
            var physical = (ppuAddress & 0x03FF) | (ciramA10 << 10);
            return new Mmc1PpuMappingDiagnostic(
                ppuAddress,
                Mmc1PpuMappingKind.Ciram,
                physical,
                -1,
                ciramA10,
                _control,
                _chrBank0,
                _chrBank1,
                _prgBank);
        }

        return new Mmc1PpuMappingDiagnostic(
            ppuAddress, Mmc1PpuMappingKind.Unmapped, -1, -1, -1,
            _control, _chrBank0, _chrBank1, _prgBank);
    }

    public void LoadImage(VirtualHardwareNesRomImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.MapperNumber != 1) throw new NotSupportedException($"Mapper {image.MapperNumber} is not MMC1.");
        if (image.PrgRom.Length < 2 * PrgBankSize || image.PrgRom.Length % PrgBankSize != 0)
            throw new ArgumentException("MMC1 PRG ROM must contain whole 16 KiB banks and at least 32 KiB.", nameof(image));
        if (image.PrgRom.Length / PrgBankSize > 16)
            throw new NotSupportedException("This MMC1 hardware model currently supports the standard 256 KiB PRG address range.");
        if (image.ChrRom.Length != 0 && image.ChrRom.Length % ChrBankSize != 0)
            throw new ArgumentException("MMC1 CHR ROM must contain whole 4 KiB banks.", nameof(image));
        if (image.Mirroring == VirtualHardwareNesMirroring.FourScreen)
            throw new NotSupportedException("MMC1 four-screen cartridge boards require additional cartridge nametable RAM hardware.");

        _prg = image.PrgRom.ToArray();

        var prgRamSize = ResolvePrgRamSize(image);
        if (prgRamSize is not (0 or StandardPrgRamWindowSize))
            throw new NotSupportedException(
                $"This MMC1 cartridge declares {prgRamSize:N0} bytes of PRG RAM/NVRAM. " +
                "The current cartridge hardware supports either no PRG RAM chip or one 8 KiB RAM chip; " +
                "other SxROM RAM wiring will be added as distinct cartridge hardware.");
        _prgRam = prgRamSize == 0 ? [] : new byte[StandardPrgRamWindowSize];

        _chrRam = image.ChrRom.Length == 0;
        if (_chrRam)
        {
            var chrRamSize = ResolveChrRamSize(image);
            if (chrRamSize == 0)
                throw new NotSupportedException(
                    "This MMC1 cartridge has no CHR ROM and its NES 2.0 header declares no CHR RAM/NVRAM hardware.");
            if (chrRamSize % ChrBankSize != 0)
                throw new NotSupportedException("MMC1 CHR RAM/NVRAM must be composed of whole 4 KiB banks.");
            _chr = new byte[chrRamSize];
        }
        else
        {
            _chr = image.ChrRom.ToArray();
        }

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
        _cpuReadAddressSelected = false;
        _ppuReadActive = false;
        _ppuWriteActive = false;
        _previousCpuCycleWasWrite = false;
        _suppressCurrentSerialDataWrite = false;
        _cpuCycleRomSelected = false;
        _cpuCyclePrgRamSelected = false;
        _cpuCycleLogicalAddress = 0;
        IgnoredConsecutiveMapperWriteCount = 0;
        RefreshPpuDataWakeState();
        ReleaseOutputs();
    }

    private void ApplyResetState()
    {
        _shiftRegister = 0x10;
        _control = 0x0C;
        _chrBank0 = 0;
        _chrBank1 = 0;
        _prgBank = 0;
        _cpuReadAddressSelected = false;
        _cpuSelectedAddress = 0;
        _cpuSelectedData = 0;
        _ppuReadActive = false;
        _ppuWriteActive = false;
        _previousCpuCycleWasWrite = false;
        _suppressCurrentSerialDataWrite = false;
        _cpuCycleRomSelected = false;
        _cpuCyclePrgRamSelected = false;
        _cpuCycleLogicalAddress = 0;
        MapperWriteCount = 0;
        MapperResetWriteCount = 0;
        MapperCommitCount = 0;
        IgnoredConsecutiveMapperWriteCount = 0;
        LastMapperWriteAddress = 0;
        LastMapperWriteData = 0;
        _mapperWriteHash = MapperWriteHashOffset;
        RefreshPpuDataWakeState();
        ReleaseOutputs();
    }

    private static int ResolvePrgRamSize(VirtualHardwareNesRomImage image)
    {
        if (image.PrgRamSizeBytes >= 0 || image.PrgNvRamSizeBytes >= 0)
            return checked(Math.Max(0, image.PrgRamSizeBytes) + Math.Max(0, image.PrgNvRamSizeBytes));

        // Directly constructed legacy test images predate explicit RAM metadata.
        // Preserve the historical MMC1 8 KiB compatibility assumption only for
        // those unknown legacy descriptions; NES 2.0 zero means physically absent.
        return image.HasExplicitRamSizes ? 0 : StandardPrgRamWindowSize;
    }

    private static int ResolveChrRamSize(VirtualHardwareNesRomImage image)
    {
        if (image.ChrRamSizeBytes >= 0 || image.ChrNvRamSizeBytes >= 0)
            return checked(Math.Max(0, image.ChrRamSizeBytes) + Math.Max(0, image.ChrNvRamSizeBytes));
        return image.HasExplicitRamSizes ? 0 : LegacyChrRamSize;
    }

    private bool IsPowered() =>
        IsInserted && Vcc.SampledLevel == DigitalLevel.High && Gnd.SampledLevel == DigitalLevel.Low;

    private void RefreshPpuDataWakeState()
    {
        var enabled = IsPowered()
            && _chrRam
            && PpuWriteBar.SampledLevel == DigitalLevel.Low;
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

        if (!powerChanged && !cpuAddressOrControlChanged && !cpuM2Changed && !cpuRomSelectChanged && !ppuAddressOrControlChanged && !ppuDataChanged)
            return;

        if (!IsPowered())
        {
            if (!powerChanged && !IsInserted) return;
            _cpuReadAddressSelected = false;
            _ppuReadActive = false;
            _ppuWriteActive = false;
            ReleaseOutputs();
            RefreshPpuDataWakeState();
            return;
        }

        if (powerChanged || ppuAddressOrControlChanged) RefreshPpuDataWakeState();
        var ppuDataCanMatter = ppuDataChanged &&
            _chrRam && PpuWriteBar.SampledLevel == DigitalLevel.Low;
        if (powerChanged || ppuAddressOrControlChanged || ppuDataCanMatter)
            ProcessPpuPort();

        // Sample the bus transaction before recomputing output selection for
        // the now-inactive M2 phase. This preserves the /ROMSEL/WRAM select
        // that was latched throughout the preceding high phase.
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
        var prgRamSelected = m2High
            && CpuRomSelectBar.SampledLevel == DigitalLevel.High
            && (address & 0x6000) == 0x6000
            && PrgRamEnabled;

        // A15 is not present on the NES cartridge connector. During an active
        // /ROMSEL window its value is implicit (logical $8000-$FFFF); during a
        // PRG-RAM window A13/A14 + M2 + inactive /ROMSEL identify $6000-$7FFF.
        _cpuCycleRomSelected = romSelected;
        _cpuCyclePrgRamSelected = prgRamSelected;
        _cpuCycleLogicalAddress = romSelected ? (ushort)(0x8000 | address) : address;

        _cpuReadAddressSelected = CpuReadWrite.SampledLevel == DigitalLevel.High
            && (romSelected || prgRamSelected);
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
        var suppressSerialDataWrite = writeCycle && _previousCpuCycleWasWrite;
        _previousCpuCycleWasWrite = writeCycle;

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
            if (PrgRamEnabled) _prgRam[_cpuCycleLogicalAddress & 0x1FFF] = data;
            return;
        }

        if (_cpuCycleRomSelected)
            WriteMapperRegister(_cpuCycleLogicalAddress, data, suppressSerialDataWrite);
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
            writeSelected = _chrRam && PpuWriteBar.SampledLevel == DigitalLevel.Low;
            if (readSelected)
            {
                PpuData.Drive(ReadChr(address));
                if (!_ppuReadActive) PpuReadCount++;
            }
            else
            {
                PpuData.Release();
                if (writeSelected && PpuData.TrySample(out var data) && !_ppuWriteActive)
                {
                    WriteChr(address, (byte)data);
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
        CiramA10.Drive(CiramA10Level(ppuAddress));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private DigitalLevel CiramA10Level(ushort ppuAddress) => (_control & 0x03) switch
    {
        0 => DigitalLevel.Low,
        1 => DigitalLevel.High,
        2 => (ppuAddress & 0x0400) != 0 ? DigitalLevel.High : DigitalLevel.Low,
        _ => (ppuAddress & 0x0800) != 0 ? DigitalLevel.High : DigitalLevel.Low
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadPrg(ushort address)
    {
        var bankCount = _prg.Length / PrgBankSize;
        var mode = (_control >> 2) & 0x03;
        int bank;
        int offset;
        if (mode <= 1)
        {
            var pairCount = Math.Max(1, bankCount / 2);
            var pair = ((_prgBank & 0x0E) >> 1) % pairCount;
            offset = (pair * 2 * PrgBankSize) + (address & 0x7FFF);
        }
        else
        {
            var selected = (_prgBank & 0x0F) % bankCount;
            bank = mode == 2
                ? (address < 0xC000 ? 0 : selected)
                : (address < 0xC000 ? selected : bankCount - 1);
            offset = (bank * PrgBankSize) + (address & 0x3FFF);
        }
        return _prg[offset % _prg.Length];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ChrIndex(ushort address)
    {
        var bankCount = Math.Max(1, _chr.Length / ChrBankSize);
        if ((_control & 0x10) == 0)
        {
            var bank = (_chrBank0 & 0x1E) % bankCount;
            if ((bank & 1) != 0) bank--;
            return ((bank * ChrBankSize) + (address & 0x1FFF)) % _chr.Length;
        }

        var selected = address < 0x1000 ? _chrBank0 : _chrBank1;
        var selectedBank = selected % bankCount;
        return (selectedBank * ChrBankSize) + (address & 0x0FFF);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadChr(ushort address) => _chr[ChrIndex(address)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteChr(ushort address, byte value)
    {
        if (_chrRam) _chr[ChrIndex(address)] = value;
    }

    private void WriteMapperRegister(ushort address, byte value, bool suppressSerialDataWrite = false)
    {
        RecordMapperWrite(address, value);

        // MMC1's serial input ignores D0 writes on CPU write cycles that
        // immediately follow another write cycle. This is package-internal
        // M2/RW state and is what prevents a 6502 read-modify-write instruction
        // from shifting twice. Bit 7 reset is asynchronous to that suppression
        // and must always be honored, including on the second RMW write.
        if ((value & 0x80) != 0)
        {
            MapperResetWriteCount++;
            _shiftRegister = 0x10;
            _control |= 0x0C;
            RefreshMapperControlledPpuOutputs();
            NotifyRegisterDiagnostic(address, value, Mmc1RegisterOperation.Reset);
            return;
        }

        if (suppressSerialDataWrite)
        {
            IgnoredConsecutiveMapperWriteCount++;
            return;
        }

        var complete = (_shiftRegister & 0x01) != 0;
        _shiftRegister >>= 1;
        _shiftRegister |= (byte)((value & 0x01) << 4);
        if (!complete) return;

        var registerValue = (byte)(_shiftRegister & 0x1F);
        Mmc1RegisterOperation operation;
        if (address <= 0x9FFF)
        {
            _control = registerValue;
            operation = Mmc1RegisterOperation.Control;
        }
        else if (address <= 0xBFFF)
        {
            _chrBank0 = registerValue;
            operation = Mmc1RegisterOperation.ChrBank0;
        }
        else if (address <= 0xDFFF)
        {
            _chrBank1 = registerValue;
            operation = Mmc1RegisterOperation.ChrBank1;
        }
        else
        {
            _prgBank = registerValue;
            operation = Mmc1RegisterOperation.PrgBank;
        }
        MapperCommitCount++;
        _shiftRegister = 0x10;
        RefreshMapperControlledPpuOutputs();
        NotifyRegisterDiagnostic(address, value, operation);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void NotifyRegisterDiagnostic(ushort address, byte data, Mmc1RegisterOperation operation)
    {
        if (!RegisterTraceOutput.CaptureEnabled) return;
        RegisterTraceOutput.Drive(new Mmc1RegisterTraceEvent(
            MapperWriteCount,
            PpuReadCount,
            PpuWriteCount,
            address,
            data,
            operation,
            _control,
            _chrBank0,
            _chrBank1,
            _prgBank,
            _shiftRegister));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RefreshMapperControlledPpuOutputs()
    {
        if (!IsPowered() || !PpuAddress.TrySample(out var rawAddress)) return;

        var address = (ushort)(rawAddress & 0x3FFF);
        DriveCiramOutputs(address);

        // MMC1 CHR bank outputs feed the cartridge CHR device continuously.
        // If the register commits while /RD is already active, the physical
        // CHR address changes on the same data window without inventing a new
        // PPU read transaction.
        if (_ppuReadActive &&
            PpuReadBar.SampledLevel == DigitalLevel.Low &&
            address < 0x2000)
        {
            PpuData.Drive(ReadChr(address));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordMapperWrite(ushort address, byte value)
    {
        MapperWriteCount++;
        LastMapperWriteAddress = address;
        LastMapperWriteData = value;

        // FNV-1a over the physical mapper write stream. This is diagnostic
        // state only: it lets reference and compiled execution prove that they
        // delivered the same address/data transaction sequence at a fixed frame.
        _mapperWriteHash ^= (byte)address;
        _mapperWriteHash *= MapperWriteHashPrime;
        _mapperWriteHash ^= (byte)(address >> 8);
        _mapperWriteHash *= MapperWriteHashPrime;
        _mapperWriteHash ^= value;
        _mapperWriteHash *= MapperWriteHashPrime;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadCpuCompiled(ushort address)
    {
        var value = address >= 0x8000
            ? ReadPrg(address)
            : PrgRamEnabled
                ? _prgRam[address & 0x1FFF]
                : (byte)0;
        CpuReadCount++;
        LastCpuReadAddress = address;
        LastCpuReadData = value;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteCpuCompiled(ushort address, byte value)
    {
        if (address < 0x8000)
        {
            if (PrgRamEnabled) _prgRam[address & 0x1FFF] = value;
        }
        else
        {
            WriteMapperRegister(address, value, _suppressCurrentSerialDataWrite);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ObserveCompiledCpuBusCycle(bool writeCycle)
    {
        _suppressCurrentSerialDataWrite = writeCycle && _previousCpuCycleWasWrite;
        _previousCpuCycleWasWrite = writeCycle;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadPpuCompiled(ushort address)
    {
        address &= 0x1FFF;
        var physicalAddress = ChrIndex(address);
        var data = _chr[physicalAddress];

        if (ChrReadTraceOutput.CaptureEnabled)
        {
            ChrReadTraceOutput.Drive(new Mmc1ChrReadTraceEvent(
                PpuReadCount,
                address,
                physicalAddress,
                physicalAddress / ChrBankSize,
                data,
                _control,
                _chrBank0,
                _chrBank1,
                _prgBank));
        }

        return data;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ObserveCompiledPpuReadBegin(int _) => PpuReadCount++;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WritePpuCompiled(ushort address, byte value)
    {
        if (!_chrRam) return;
        WriteChr(address, value);
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
                (_, _) => PrgRamEnabled,
                CompiledBusWritePhase.Complete);
        }

        Action<int, byte>? ppuWrite = _chrRam ? (address, value) => WritePpuCompiled((ushort)address, value) : null;
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
            ppuWrite,
            observeReadBegin: ObserveCompiledPpuReadBegin);
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
            var mode = _control & 0x03;
            if (mode == 0) drive = new CompiledDriveState(DigitalLevel.Low);
            else if (mode == 1) drive = new CompiledDriveState(DigitalLevel.High);
            else drive = new CompiledDriveState(sampleInput(PpuAddress.Pins[mode == 2 ? 10 : 11]));
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
