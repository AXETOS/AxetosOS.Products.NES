using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

/// <summary>
/// Nintendo MMC5 / mapper 5 replaceable cartridge hardware. The package owns PRG/CHR
/// banking, protected PRG RAM, 1 KiB ExRAM, per-nametable CIRAM/ExRAM/fill selection,
/// extended attributes, vertical split fetch substitution, scanline IRQ detection,
/// multiplier and chip-local pulse/PCM expansion audio. All PPU-integrated behavior is
/// derived from the package-visible CPU/PPU buses; the motherboard remains unaware of MMC5.
/// </summary>
public sealed class NintendoMmc5Cartridge : VirtualHardwareComponent, IReplaceableCartridgeHardware,
    ICompiledBusTargetProvider, ICompiledCombinationalComponent, ICompiledBusAddressCombinationalComponent
{
    private const int PrgBankSize = 8 * 1024;
    private const int ChrBankSize = 1024;
    private const int ExRamSize = 1024;
    private const int MaximumPrgBanks = 128;
    private const int MaximumChrBanks = 1024;
    private const int MaximumWorkRamBytes = 128 * 1024;

    private enum PpuDriveKind : byte
    {
        None,
        Ciram,
        Chr,
        ExRam,
        Fill,
        Empty,
        ExtendedAttribute,
        ExtendedChr,
        SplitNametable,
        SplitAttribute,
        SplitChr
    }

    private enum CurrentReadOverride : byte
    {
        None,
        ExtendedAttribute,
        ExtendedChr,
        SplitNametable,
        SplitAttribute,
        SplitChr
    }

    private readonly record struct PpuSource(PpuDriveKind Kind, int CiramPage, int MemoryAddress)
    {
        public bool CartridgeDrives => Kind is not (PpuDriveKind.None or PpuDriveKind.Ciram);
    }

    private readonly record struct PrgSource(bool Selected, bool UsesRam, int Bank, int MemoryAddress);

    private byte[] _prg = [];
    private byte[] _chr = [];
    private bool _chrRam;
    private byte[] _workRam = [];
    private readonly byte[] _exRam = new byte[ExRamSize];
    private int _prgBankCount;
    private int _chrBankCount;

    private byte _prgMode;
    private byte _chrMode;
    private byte _prgRamProtect1;
    private byte _prgRamProtect2;
    private byte _exRamMode;
    private byte _nametableMapping;
    private byte _fillTile;
    private byte _fillColor;
    private readonly byte[] _prgRegisters = new byte[5];
    private readonly ushort[] _chrRegisters = new ushort[12];
    private byte _chrUpperBits;
    private bool _lastChrSetB;
    private bool _activeChrSetA;
    private byte _ppuControl;

    private bool _splitEnabled;
    private bool _splitRightSide;
    private byte _splitDelimiterTile;
    private byte _splitScroll;
    private byte _splitBank;
    private bool _splitInRegion;
    private int _splitVerticalScroll;
    private int _splitTileAddress;
    private int _tileNumber;

    private byte _irqTarget;
    private bool _irqEnabled;
    private byte _scanlineCounter;
    private bool _irqPending;
    private bool _irqLineAsserted;
    private bool _needInFrame;
    private bool _ppuInFrame;
    private byte _ppuIdleCounter;
    private ushort _lastPpuReadAddress;
    private byte _ntReadCounter;

    private ushort _exAttrLastNametableFetch;
    private byte _exAttrFetchCounter;
    private ushort _exAttrSelectedChrBank;
    private CurrentReadOverride _currentReadOverride;

    private byte _multiplierValue1;
    private byte _multiplierValue2;

    private bool _cpuReadAddressSelected;
    private ushort _cpuSelectedAddress;
    private byte _cpuSelectedData;
    private bool _cpuSelectedWorkRam;
    private bool _cpuSelectedLowRegister;
    private ushort _cpuCycleAddress;
    private bool _cpuCycleHigh;
    private bool _cpuCycleLow;
    private bool _ppuReadActive;
    private ushort _ppuReadAddress;
    private bool _ppuWriteActive;

    private readonly ulong _powerInputMask;
    private readonly ulong _cpuAddressControlInputMask;
    private readonly ulong _cpuM2InputMask;
    private readonly ulong _cpuRomSelectInputMask;
    private readonly ulong _ppuAddressControlInputMask;
    private readonly ulong _ppuDataInputMask;

    private readonly int[] _prgWindowBanks = new int[4];
    private readonly bool[] _prgWindowRam = new bool[4];

    public NintendoMmc5Cartridge(string componentId) : base(componentId)
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

        Audio = new NintendoMmc5Audio();

        _powerInputMask = Vcc.InputChangeMask | Gnd.InputChangeMask;
        _cpuAddressControlInputMask = CpuAddress.InputChangeMask | CpuReadWrite.InputChangeMask;
        _cpuM2InputMask = CpuM2.InputChangeMask;
        _cpuRomSelectInputMask = CpuRomSelectBar.InputChangeMask;
        _ppuAddressControlInputMask = PpuAddress.InputChangeMask | PpuReadBar.InputChangeMask | PpuWriteBar.InputChangeMask;
        _ppuDataInputMask = PpuData.InputChangeMask;

        CpuData.SetOwnerWakeEnabled(false);
        PpuData.SetOwnerWakeEnabled(false);
        ApplyResetState();
    }

    public int MapperNumber => IsInserted ? 5 : 0;
    public bool IsInserted { get; private set; }
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

    public NintendoMmc5Audio Audio { get; }
    public byte PrgMode => _prgMode;
    public byte ChrMode => _chrMode;
    public byte ExRamMode => _exRamMode;
    public byte NametableMapping => _nametableMapping;
    public byte FillTile => _fillTile;
    public byte FillColor => _fillColor;
    public IReadOnlyList<byte> PrgBankRegisters => _prgRegisters;
    public IReadOnlyList<ushort> ChrBankRegisters => _chrRegisters;
    public byte ChrUpperBits => _chrUpperBits;
    public bool ActiveChrSetA => _activeChrSetA;
    public IReadOnlyList<int> PrgWindowBanks => _prgWindowBanks;
    public IReadOnlyList<bool> PrgWindowUsesRam => _prgWindowRam;
    public bool IsChrRam => _chrRam;
    public int ChrMemorySizeBytes => _chr.Length;
    public int WorkRamSizeBytes => _workRam.Length;
    public bool PrgRamWriteEnabled => _prgRamProtect1 == 0x02 && _prgRamProtect2 == 0x01;
    public bool PpuInFrame => _ppuInFrame;
    public byte ScanlineCounter => _scanlineCounter;
    public byte IrqTarget => _irqTarget;
    public bool IrqEnabled => _irqEnabled;
    public bool IrqPending => _irqPending;
    public bool IrqAsserted => (_irqPending && _irqEnabled) || Audio.PcmIrqPending;
    public bool SplitEnabled => _splitEnabled;
    public bool SplitRightSide => _splitRightSide;
    public byte SplitDelimiterTile => _splitDelimiterTile;
    public byte SplitScroll => _splitScroll;
    public byte SplitBank => _splitBank;
    public ushort MultiplierResult => (ushort)(_multiplierValue1 * _multiplierValue2);

    public ulong MapperWriteCount { get; private set; }
    public ushort LastMapperWriteAddress { get; private set; }
    public byte LastMapperWriteData { get; private set; }
    public ulong CpuReadCount { get; private set; }
    public ushort LastCpuReadAddress { get; private set; }
    public byte LastCpuReadData { get; private set; }
    public ulong LowRegisterReadCount { get; private set; }
    public ulong WorkRamReadCount { get; private set; }
    public ulong WorkRamWriteCount { get; private set; }
    public ulong ExRamCpuReadCount { get; private set; }
    public ulong ExRamCpuWriteCount { get; private set; }
    public ulong PpuReadCount { get; private set; }
    public ulong PpuWriteCount { get; private set; }
    public ulong PpuBusReadObservationCount { get; private set; }
    public ulong ChrReadCount { get; private set; }
    public ulong ChrSetAReadCount { get; private set; }
    public ulong ChrSetBReadCount { get; private set; }
    public ulong ChrSetSwitchCount { get; private set; }
    public ulong ExRamPpuReadCount { get; private set; }
    public ulong ExRamPpuWriteCount { get; private set; }
    public ulong FillReadCount { get; private set; }
    public ulong ExtendedAttributeReadCount { get; private set; }
    public ulong ExtendedChrReadCount { get; private set; }
    public ulong VerticalSplitReadCount { get; private set; }
    public ulong CpuCycleClockCount { get; private set; }
    public ulong IrqAssertCount { get; private set; }

    public void LoadImage(VirtualHardwareNesRomImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.MapperNumber != 5)
            throw new NotSupportedException($"Mapper {image.MapperNumber} is not MMC5 hardware modeled by this package.");
        if (image.Mirroring == VirtualHardwareNesMirroring.FourScreen)
            throw new NotSupportedException("MMC5 supplies its own per-nametable routing and is not modeled with external four-screen nametable RAM.");
        if (image.ChrRom.Length != 0 && image.TotalChrRamSizeBytes != 0 && image.HasExplicitRamSizes)
            throw new NotSupportedException("Mixed MMC5 CHR ROM/RAM topology requires a separately verified physical board.");

        if (image.PrgRom.Length < 4 * PrgBankSize || image.PrgRom.Length % PrgBankSize != 0)
            throw new ArgumentException("MMC5 PRG ROM must contain at least four whole 8 KiB banks.", nameof(image));
        _prgBankCount = image.PrgRom.Length / PrgBankSize;
        if (_prgBankCount > MaximumPrgBanks || !IsPowerOfTwo(_prgBankCount))
            throw new NotSupportedException($"MMC5 PRG ROM must expose a power-of-two count of at most {MaximumPrgBanks} 8 KiB banks.");

        _chrRam = image.ChrRom.Length == 0;
        var chrMemorySize = _chrRam ? ResolveChrRamSize(image) : image.ChrRom.Length;
        if (chrMemorySize < 8 * ChrBankSize || chrMemorySize % ChrBankSize != 0)
            throw new ArgumentException("MMC5 CHR memory must contain at least eight whole 1 KiB banks.", nameof(image));
        _chrBankCount = chrMemorySize / ChrBankSize;
        if (_chrBankCount > MaximumChrBanks || !IsPowerOfTwo(_chrBankCount))
            throw new NotSupportedException($"MMC5 CHR memory must expose a power-of-two count of at most {MaximumChrBanks} 1 KiB banks.");

        var workRamSize = ResolveWorkRamSize(image);
        if (workRamSize < 0 || workRamSize > MaximumWorkRamBytes || (workRamSize % PrgBankSize) != 0)
            throw new NotSupportedException("MMC5 PRG RAM/NVRAM must be an 8 KiB multiple up to 128 KiB.");

        _prg = image.PrgRom.ToArray();
        _chr = _chrRam ? new byte[chrMemorySize] : image.ChrRom.ToArray();
        _workRam = workRamSize == 0 ? [] : new byte[workRamSize];
        Array.Clear(_exRam);
        IsInserted = true;
        ApplyResetState();
        RefreshPpuDataWakeState();
    }

    public void Eject()
    {
        IsInserted = false;
        _prg = [];
        _chr = [];
        _workRam = [];
        _chrRam = false;
        _prgBankCount = 0;
        _chrBankCount = 0;
        Array.Clear(_exRam);
        ApplyResetState();
        RefreshPpuDataWakeState();
    }

    public byte InspectWorkRamByte(int offset)
    {
        if ((uint)offset >= (uint)_workRam.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        return _workRam[offset];
    }

    public byte InspectExRamByte(int offset)
    {
        if ((uint)offset >= ExRamSize) throw new ArgumentOutOfRangeException(nameof(offset));
        return _exRam[offset];
    }

    public byte InspectChrByte(int offset)
    {
        if ((uint)offset >= (uint)_chr.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        return _chr[offset];
    }

    public int ResolveCurrentChrBank(int slot)
    {
        if ((uint)slot >= 8) throw new ArgumentOutOfRangeException(nameof(slot));
        return ResolveNormalChrBank((ushort)(slot * ChrBankSize));
    }

    public void ResetDiagnostics()
    {
        MapperWriteCount = 0;
        LastMapperWriteAddress = 0;
        LastMapperWriteData = 0;
        CpuReadCount = 0;
        LastCpuReadAddress = 0;
        LastCpuReadData = 0;
        LowRegisterReadCount = 0;
        WorkRamReadCount = 0;
        WorkRamWriteCount = 0;
        ExRamCpuReadCount = 0;
        ExRamCpuWriteCount = 0;
        PpuReadCount = 0;
        PpuWriteCount = 0;
        PpuBusReadObservationCount = 0;
        ChrReadCount = 0;
        ChrSetAReadCount = 0;
        ChrSetBReadCount = 0;
        ChrSetSwitchCount = 0;
        ExRamPpuReadCount = 0;
        ExRamPpuWriteCount = 0;
        FillReadCount = 0;
        ExtendedAttributeReadCount = 0;
        ExtendedChrReadCount = 0;
        VerticalSplitReadCount = 0;
        CpuCycleClockCount = 0;
        IrqAssertCount = 0;
    }

    private void ApplyResetState()
    {
        _prgMode = 3;
        _chrMode = 0;
        _prgRamProtect1 = 0;
        _prgRamProtect2 = 0;
        _exRamMode = 0;
        _nametableMapping = 0;
        _fillTile = 0;
        _fillColor = 0;
        Array.Clear(_prgRegisters);
        _prgRegisters[4] = 0xFF;
        Array.Clear(_chrRegisters);
        _chrUpperBits = 0;
        _lastChrSetB = false;
        _activeChrSetA = true;
        _ppuControl = 0;
        _splitEnabled = false;
        _splitRightSide = false;
        _splitDelimiterTile = 0;
        _splitScroll = 0;
        _splitBank = 0;
        _splitInRegion = false;
        _splitVerticalScroll = 0;
        _splitTileAddress = 0;
        _tileNumber = 0;
        _irqTarget = 0;
        _irqEnabled = false;
        _scanlineCounter = 0;
        _irqPending = false;
        _irqLineAsserted = false;
        _needInFrame = false;
        _ppuInFrame = false;
        _ppuIdleCounter = 0;
        _lastPpuReadAddress = 0;
        _ntReadCounter = 0;
        _exAttrLastNametableFetch = 0;
        _exAttrFetchCounter = 0;
        _exAttrSelectedChrBank = 0;
        _currentReadOverride = CurrentReadOverride.None;
        _multiplierValue1 = 0;
        _multiplierValue2 = 0;
        _cpuReadAddressSelected = false;
        _cpuSelectedAddress = 0;
        _cpuSelectedData = 0;
        _cpuSelectedWorkRam = false;
        _cpuSelectedLowRegister = false;
        _cpuCycleAddress = 0;
        _cpuCycleHigh = false;
        _cpuCycleLow = false;
        _ppuReadActive = false;
        _ppuReadAddress = 0;
        _ppuWriteActive = false;
        Audio.Reset();
        ResetDiagnostics();
        RefreshPrgDiagnostics();
        RefreshIrqPhysical();
        RefreshCiramPhysical();
        ReleaseDataOutputs();
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
        var ppuDataCanMatter = ppuDataChanged && PpuWriteBar.SampledLevel == DigitalLevel.Low;

        if (!powerChanged && !cpuAddressOrControlChanged && !cpuM2Changed && !cpuRomSelectChanged &&
            !ppuAddressOrControlChanged && !ppuDataCanMatter)
            return;

        if (!IsPowered())
        {
            _cpuReadAddressSelected = false;
            _cpuCycleHigh = false;
            _cpuCycleLow = false;
            _ppuReadActive = false;
            _ppuWriteActive = false;
            ReleaseOutputs();
            RefreshPpuDataWakeState();
            return;
        }

        if (powerChanged)
        {
            RefreshIrqPhysical();
            RefreshCiramPhysical();
        }
        if (powerChanged || ppuAddressOrControlChanged) RefreshPpuDataWakeState();
        if (powerChanged || ppuAddressOrControlChanged || ppuDataCanMatter) ProcessPpuPort();

        if (!powerChanged && cpuM2Changed && CpuM2.SampledLevel == DigitalLevel.Low)
        {
            ClockCpuCycle();
            CompleteCpuTransaction();
        }

        if (powerChanged || cpuAddressOrControlChanged || cpuRomSelectChanged || cpuM2Changed)
            UpdateCpuPort();
    }

    private bool IsPowered() => IsInserted && Vcc.SampledLevel == DigitalLevel.High && Gnd.SampledLevel == DigitalLevel.Low;

    private void UpdateCpuPort()
    {
        if (!CpuAddress.TrySample(out var rawAddress))
        {
            _cpuReadAddressSelected = false;
            _cpuCycleHigh = false;
            _cpuCycleLow = false;
            CpuData.Release();
            return;
        }

        var connectorAddress = (ushort)(rawAddress & 0x7FFF);
        var m2High = CpuM2.SampledLevel == DigitalLevel.High;
        var romSelected = m2High && CpuRomSelectBar.SampledLevel == DigitalLevel.Low;
        _cpuCycleHigh = romSelected;
        _cpuCycleLow = m2High && !romSelected;
        _cpuCycleAddress = romSelected ? (ushort)(0x8000 | connectorAddress) : connectorAddress;

        CpuData.Release();
        _cpuReadAddressSelected = false;
        _cpuSelectedWorkRam = false;
        _cpuSelectedLowRegister = false;
        if (CpuReadWrite.SampledLevel != DigitalLevel.High || !m2High) return;

        if (romSelected)
        {
            ResetFrameTrackingOnVectorRead(_cpuCycleAddress);
            var source = ResolvePrgSource(_cpuCycleAddress);
            if (!source.Selected) return;
            var value = ReadPrgSource(source);
            SelectCpuRead(_cpuCycleAddress, value, source.UsesRam, lowRegister: false);
            return;
        }

        if (connectorAddress is >= 0x6000 and <= 0x7FFF)
        {
            var source = ResolvePrgSource(connectorAddress);
            if (!source.Selected) return;
            SelectCpuRead(connectorAddress, ReadPrgSource(source), source.UsesRam, lowRegister: false);
            return;
        }

        if (IsLowRegisterReadSelected(connectorAddress))
        {
            SelectCpuRead(connectorAddress, ReadLowRegister(connectorAddress), workRam: false, lowRegister: true);
        }
    }

    private void SelectCpuRead(ushort address, byte value, bool workRam, bool lowRegister)
    {
        _cpuReadAddressSelected = true;
        _cpuSelectedAddress = address;
        _cpuSelectedData = value;
        _cpuSelectedWorkRam = workRam;
        _cpuSelectedLowRegister = lowRegister;
        CpuData.Drive(value);
    }

    private void CompleteCpuTransaction()
    {
        if (CpuReadWrite.SampledLevel == DigitalLevel.High)
        {
            if (!_cpuReadAddressSelected) return;
            if (_cpuSelectedAddress is >= 0x8000 and <= 0xBFFF)
                ObservePcmRead(_cpuSelectedAddress, _cpuSelectedData);
            RecordCpuRead(_cpuSelectedAddress, _cpuSelectedData);
            if (_cpuSelectedWorkRam) WorkRamReadCount++;
            if (_cpuSelectedLowRegister) LowRegisterReadCount++;
            return;
        }

        if (!CpuData.TrySample(out var rawData)) return;
        var value = (byte)rawData;
        if (_cpuCycleHigh)
        {
            WritePrgMemory(_cpuCycleAddress, value);
            return;
        }
        if (!_cpuCycleLow) return;

        if (_cpuCycleAddress == 0x2000)
        {
            ObservePpuControlWrite(value);
            return;
        }
        if (_cpuCycleAddress is >= 0x6000 and <= 0x7FFF)
        {
            WritePrgMemory(_cpuCycleAddress, value);
            return;
        }
        if (_cpuCycleAddress is >= 0x5000 and <= 0x5FFF)
            WriteLowRegister(_cpuCycleAddress, value);
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
        var readSelected = PpuReadBar.SampledLevel == DigitalLevel.Low;
        var writeSelected = PpuWriteBar.SampledLevel == DigitalLevel.Low;
        var newRead = readSelected && (!_ppuReadActive || _ppuReadAddress != address);
        if (newRead) ObservePpuReadBegin(address);

        var source = readSelected ? ResolvePpuReadSource(address) : ResolvePpuWriteSource(address);
        DriveCiramOutputs(source);

        if (readSelected && source.CartridgeDrives)
        {
            PpuData.Drive(ReadPpuSource(source, address));
            if (newRead) PpuReadCount++;
        }
        else
        {
            PpuData.Release();
            if (writeSelected && !_ppuWriteActive && PpuData.TrySample(out var rawData))
                WritePpuSource(source, address, (byte)rawData);
        }

        if (readSelected) _ppuReadAddress = address;
        _ppuReadActive = readSelected;
        _ppuWriteActive = writeSelected;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ObservePpuReadBegin(ushort address)
    {
        PpuBusReadObservationCount++;
        _currentReadOverride = CurrentReadOverride.None;
        var isNtFetch = IsNametableTileFetch(address);
        if (isNtFetch)
        {
            _tileNumber++;
            if (_ppuInFrame)
            {
                LatchChrSetSelection();
            }
            else if (_needInFrame)
            {
                _needInFrame = false;
                _ppuInFrame = true;
                LatchChrSetSelection();
            }
        }

        // MMC5's CHR A/B selection is retained circuitry, not a combinational
        // function of the tile counter. DetectScanlineStart can reset the tile
        // counter after the selection for this fetch has already been latched.
        DetectScanlineStart(address);
        _ppuIdleCounter = 3;
        _lastPpuReadAddress = address;

        if (_exRamMode > 1 || !_ppuInFrame) return;

        if (_splitEnabled)
        {
            var scanline = _tileNumber >= 49 ? _scanlineCounter + 1 : _scanlineCounter;
            _splitVerticalScroll = (scanline + _splitScroll) % 240;
            var column = (_tileNumber + 2) % 50;
            if (address >= 0x2000)
            {
                if (isNtFetch)
                {
                    if (column == 0) _splitInRegion = !_splitRightSide;
                    if (column == _splitDelimiterTile && _tileNumber < 50)
                        _splitInRegion = !_splitInRegion;
                    else if (column > 32)
                        _splitInRegion = false;

                    if (_splitInRegion)
                    {
                        _splitTileAddress = ((_splitVerticalScroll & 0xF8) << 2) | column;
                        _currentReadOverride = CurrentReadOverride.SplitNametable;
                        return;
                    }
                }
                else if (_splitInRegion)
                {
                    _currentReadOverride = CurrentReadOverride.SplitAttribute;
                    return;
                }
            }
            else if (_splitInRegion)
            {
                _currentReadOverride = CurrentReadOverride.SplitChr;
                return;
            }
        }

        if (_exRamMode != 1 || !(_tileNumber < 32 || _tileNumber >= 48)) return;
        if (isNtFetch)
        {
            _exAttrLastNametableFetch = (ushort)(address & 0x03FF);
            _exAttrFetchCounter = 3;
            return;
        }
        if (_exAttrFetchCounter == 0) return;

        _exAttrFetchCounter--;
        if (_exAttrFetchCounter == 2)
        {
            var value = _exRam[_exAttrLastNametableFetch];
            _exAttrSelectedChrBank = (ushort)((value & 0x3F) | (_chrUpperBits << 6));
            _currentReadOverride = CurrentReadOverride.ExtendedAttribute;
        }
        else
        {
            _currentReadOverride = CurrentReadOverride.ExtendedChr;
        }
    }

    private void DetectScanlineStart(ushort address)
    {
        if (_ntReadCounter >= 2)
        {
            if (!_ppuInFrame && !_needInFrame)
            {
                _needInFrame = true;
                _scanlineCounter = 0;
            }
            else
            {
                _scanlineCounter++;
                if (_irqTarget == _scanlineCounter)
                {
                    _irqPending = true;
                    RefreshIrqPhysical();
                }
            }
        }
        else if (address is >= 0x2000 and <= 0x2FFF && _lastPpuReadAddress == address)
        {
            _ntReadCounter++;
            if (_ntReadCounter >= 2) _tileNumber = 0;
        }

        if (_lastPpuReadAddress != address) _ntReadCounter = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsNametableTileFetch(ushort address) =>
        address is >= 0x2000 and <= 0x2FFF && (address & 0x03FF) < 0x03C0;

    private PpuSource ResolvePpuReadSource(ushort address)
    {
        if (address < 0x2000)
        {
            return _currentReadOverride switch
            {
                CurrentReadOverride.SplitChr => new PpuSource(PpuDriveKind.SplitChr, -1,
                    (_splitBank << 12) + (((address & ~0x0007) | (_splitVerticalScroll & 0x07)) & 0x0FFF)),
                CurrentReadOverride.ExtendedChr => new PpuSource(PpuDriveKind.ExtendedChr, -1,
                    (_exAttrSelectedChrBank << 12) + (address & 0x0FFF)),
                _ => new PpuSource(PpuDriveKind.Chr, -1,
                    (ResolveNormalChrBank(address) * ChrBankSize) + (address & 0x03FF))
            };
        }

        if (address is >= 0x3000 and <= 0x3EFF) address -= 0x1000;
        else if (address > 0x2FFF) return new PpuSource(PpuDriveKind.None, -1, 0);

        if (_currentReadOverride == CurrentReadOverride.SplitNametable)
            return new PpuSource(PpuDriveKind.SplitNametable, -1, _splitTileAddress & 0x03FF);
        if (_currentReadOverride == CurrentReadOverride.SplitAttribute)
            return new PpuSource(PpuDriveKind.SplitAttribute, -1, ResolveSplitAttributeAddress());
        if (_currentReadOverride == CurrentReadOverride.ExtendedAttribute)
            return new PpuSource(PpuDriveKind.ExtendedAttribute, -1, _exAttrLastNametableFetch);

        var slot = (address >> 10) & 0x03;
        var mode = (_nametableMapping >> (slot * 2)) & 0x03;
        return mode switch
        {
            0 => new PpuSource(PpuDriveKind.Ciram, 0, 0),
            1 => new PpuSource(PpuDriveKind.Ciram, 1, 0),
            2 when _exRamMode <= 1 => new PpuSource(PpuDriveKind.ExRam, -1, address & 0x03FF),
            2 => new PpuSource(PpuDriveKind.Empty, -1, 0),
            3 => new PpuSource(PpuDriveKind.Fill, -1, address & 0x03FF),
            _ => new PpuSource(PpuDriveKind.None, -1, 0)
        };
    }

    private PpuSource ResolvePpuWriteSource(ushort address)
    {
        if (address < 0x2000)
        {
            if (!_chrRam) return new PpuSource(PpuDriveKind.None, -1, 0);
            return new PpuSource(PpuDriveKind.Chr, -1,
                (ResolveNormalChrBank(address) * ChrBankSize) + (address & 0x03FF));
        }
        if (address is >= 0x3000 and <= 0x3EFF) address -= 0x1000;
        else if (address > 0x2FFF) return new PpuSource(PpuDriveKind.None, -1, 0);

        var slot = (address >> 10) & 0x03;
        var mode = (_nametableMapping >> (slot * 2)) & 0x03;
        return mode switch
        {
            0 => new PpuSource(PpuDriveKind.Ciram, 0, 0),
            1 => new PpuSource(PpuDriveKind.Ciram, 1, 0),
            2 when _exRamMode <= 1 => new PpuSource(PpuDriveKind.ExRam, -1, address & 0x03FF),
            2 => new PpuSource(PpuDriveKind.Empty, -1, 0),
            3 => new PpuSource(PpuDriveKind.Fill, -1, address & 0x03FF),
            _ => new PpuSource(PpuDriveKind.None, -1, 0)
        };
    }

    private int ResolveSplitAttributeAddress()
    {
        return 0x3C0 | ((_splitTileAddress & 0x380) >> 4) | ((_splitTileAddress & 0x1F) >> 2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadPpuSource(PpuSource source, ushort address)
    {
        switch (source.Kind)
        {
            case PpuDriveKind.Chr:
                ChrReadCount++;
                if (_activeChrSetA) ChrSetAReadCount++;
                else ChrSetBReadCount++;
                return ReadChrMemory(source.MemoryAddress);
            case PpuDriveKind.ExRam:
                ExRamPpuReadCount++;
                return _exRam[source.MemoryAddress & 0x03FF];
            case PpuDriveKind.Fill:
                FillReadCount++;
                if ((address & 0x03FF) < 0x03C0) return _fillTile;
                return ReplicatePalette(_fillColor);
            case PpuDriveKind.Empty:
                return 0;
            case PpuDriveKind.ExtendedAttribute:
            {
                ExtendedAttributeReadCount++;
                var palette = (byte)((_exRam[source.MemoryAddress & 0x03FF] >> 6) & 0x03);
                return ReplicatePalette(palette);
            }
            case PpuDriveKind.ExtendedChr:
                ExtendedChrReadCount++;
                return ReadChrMemory(source.MemoryAddress);
            case PpuDriveKind.SplitNametable:
                VerticalSplitReadCount++;
                return _exRam[source.MemoryAddress & 0x03FF];
            case PpuDriveKind.SplitAttribute:
            {
                VerticalSplitReadCount++;
                var shift = ((_splitTileAddress >> 4) & 0x04) | (_splitTileAddress & 0x02);
                var palette = (byte)((_exRam[source.MemoryAddress & 0x03FF] >> shift) & 0x03);
                return ReplicatePalette(palette);
            }
            case PpuDriveKind.SplitChr:
                VerticalSplitReadCount++;
                return ReadChrMemory(source.MemoryAddress);
            default:
                return 0;
        }
    }

    private void WritePpuSource(PpuSource source, ushort address, byte value)
    {
        switch (source.Kind)
        {
            case PpuDriveKind.Chr when _chrRam && address < 0x2000:
                _chr[source.MemoryAddress & (_chr.Length - 1)] = value;
                PpuWriteCount++;
                break;
            case PpuDriveKind.ExRam:
                _exRam[source.MemoryAddress & 0x03FF] = value;
                PpuWriteCount++;
                ExRamPpuWriteCount++;
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadChrMemory(int memoryAddress) => _chr[memoryAddress & (_chr.Length - 1)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ReplicatePalette(byte palette) => (byte)((palette & 0x03) * 0x55);

    private int ResolveNormalChrBank(ushort address)
    {
        var slot = (address >> 10) & 0x07;
        var useA = _activeChrSetA;
        int bank;
        switch (_chrMode)
        {
            case 0:
                bank = (_chrRegisters[useA ? 7 : 11] << 3) + slot;
                break;
            case 1:
                if (useA)
                {
                    var reg = slot < 4 ? 3 : 7;
                    bank = (_chrRegisters[reg] << 2) + (slot & 0x03);
                }
                else
                {
                    bank = (_chrRegisters[11] << 2) + (slot & 0x03);
                }
                break;
            case 2:
                if (useA)
                {
                    var reg = ((slot >> 1) * 2) + 1;
                    bank = (_chrRegisters[reg] << 1) + (slot & 0x01);
                }
                else
                {
                    var reg = (slot & 0x02) == 0 ? 9 : 11;
                    bank = (_chrRegisters[reg] << 1) + (slot & 0x01);
                }
                break;
            default:
                bank = useA ? _chrRegisters[slot] : _chrRegisters[8 + (slot & 0x03)];
                break;
        }
        return bank & (_chrBankCount - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LatchChrSetSelection()
    {
        var largeSprites = (_ppuControl & 0x20) != 0;
        if (!largeSprites) _lastChrSetB = false;

        var next = !largeSprites
            || (_tileNumber is >= 32 and < 48)
            || (!_ppuInFrame && !_lastChrSetB);
        if (next != _activeChrSetA) ChrSetSwitchCount++;
        _activeChrSetA = next;
    }

    private void DriveCiramOutputs(PpuSource source)
    {
        CiramChipEnableBar.Drive(source.CiramPage >= 0 ? DigitalLevel.Low : DigitalLevel.High);
        CiramA10.Drive(source.CiramPage switch
        {
            0 => DigitalLevel.Low,
            1 => DigitalLevel.High,
            _ => DigitalLevel.Unknown
        });
    }

    private void RefreshCiramPhysical()
    {
        if (!IsPowered() || !PpuAddress.TrySample(out var rawAddress)) return;
        var address = (ushort)(rawAddress & 0x3FFF);
        var source = PpuReadBar.SampledLevel == DigitalLevel.Low
            ? ResolvePpuReadSource(address)
            : ResolvePpuWriteSource(address);
        DriveCiramOutputs(source);
    }

    private PrgSource ResolvePrgSource(ushort address)
    {
        if (address is >= 0x6000 and <= 0x7FFF)
            return ResolveRamBankSource(_prgRegisters[0], address);
        if (address < 0x8000) return default;

        var slot = (address - 0x8000) >> 13;
        byte register;
        int groupOffset;
        int groupMask;
        bool forceRom;
        switch (_prgMode)
        {
            case 0:
                register = _prgRegisters[4];
                groupOffset = slot;
                groupMask = ~3;
                forceRom = true;
                break;
            case 1:
                if (slot < 2)
                {
                    register = _prgRegisters[2];
                    groupOffset = slot;
                    groupMask = ~1;
                    forceRom = false;
                }
                else
                {
                    register = _prgRegisters[4];
                    groupOffset = slot - 2;
                    groupMask = ~1;
                    forceRom = true;
                }
                break;
            case 2:
                if (slot < 2)
                {
                    register = _prgRegisters[2];
                    groupOffset = slot;
                    groupMask = ~1;
                    forceRom = false;
                }
                else if (slot == 2)
                {
                    register = _prgRegisters[3];
                    groupOffset = 0;
                    groupMask = ~0;
                    forceRom = false;
                }
                else
                {
                    register = _prgRegisters[4];
                    groupOffset = 0;
                    groupMask = ~0;
                    forceRom = true;
                }
                break;
            default:
                register = _prgRegisters[slot + 1];
                groupOffset = 0;
                groupMask = ~0;
                forceRom = slot == 3;
                break;
        }

        var usesRam = !forceRom && (register & 0x80) == 0;
        var bank = ((register & 0x7F) & groupMask) + groupOffset;
        if (usesRam) return ResolveRamBankSource(bank, address);
        var romBank = bank & (_prgBankCount - 1);
        return new PrgSource(true, false, romBank, (romBank * PrgBankSize) + (address & 0x1FFF));
    }

    private PrgSource ResolveRamBankSource(int selectedBank, ushort address)
    {
        var physicalBank = ResolvePhysicalRamBank(selectedBank);
        if (physicalBank < 0) return default;
        return new PrgSource(true, true, physicalBank,
            (physicalBank * PrgBankSize) + (address & 0x1FFF));
    }

    private int ResolvePhysicalRamBank(int selectedBank)
    {
        var banks = _workRam.Length / PrgBankSize;
        if (banks == 0) return -1;
        selectedBank &= 0x0F;
        return banks switch
        {
            1 => selectedBank <= 3 ? 0 : -1,
            2 => selectedBank <= 3 ? 0 : selectedBank <= 7 ? 1 : -1,
            4 => selectedBank <= 3 ? selectedBank : -1,
            // Explicit 64/128 KiB NES 2.0 boards expose the wider four-bit RAM decode.
            // A 64 KiB physical device mirrors the top address bit; 128 KiB uses all 16 banks.
            _ => selectedBank & (banks - 1)
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadPrgSource(PrgSource source) => source.UsesRam
        ? _workRam[source.MemoryAddress]
        : _prg[source.MemoryAddress];

    private void WritePrgMemory(ushort address, byte value)
    {
        var source = ResolvePrgSource(address);
        if (!source.Selected || !source.UsesRam || !PrgRamWriteEnabled) return;
        _workRam[source.MemoryAddress] = value;
        WorkRamWriteCount++;
    }

    private bool IsLowRegisterReadSelected(ushort address) =>
        address is 0x5010 or 0x5015 or 0x5204 or 0x5205 or 0x5206 ||
        (address is >= 0x5C00 and <= 0x5FFF && _exRamMode >= 2);

    private byte ReadLowRegister(ushort address)
    {
        if (address is >= 0x5C00 and <= 0x5FFF)
        {
            ExRamCpuReadCount++;
            return _exRam[address & 0x03FF];
        }

        switch (address)
        {
            case 0x5010:
            {
                var value = Audio.ReadRegister(address);
                RefreshIrqPhysical();
                return value;
            }
            case 0x5015:
                return Audio.ReadRegister(address);
            case 0x5204:
            {
                var value = (byte)((_irqPending ? 0x80 : 0x00) | (_ppuInFrame ? 0x40 : 0x00));
                _irqPending = false;
                RefreshIrqPhysical();
                return value;
            }
            case 0x5205:
                return (byte)MultiplierResult;
            case 0x5206:
                return (byte)(MultiplierResult >> 8);
            default:
                return 0;
        }
    }

    private void WriteLowRegister(ushort address, byte value)
    {
        if (address is >= 0x5C00 and <= 0x5FFF)
        {
            WriteExRamCpu(address, value);
            return;
        }

        MapperWriteCount++;
        LastMapperWriteAddress = address;
        LastMapperWriteData = value;
        if (address is >= 0x5000 and <= 0x5007 || address is 0x5010 or 0x5011 or 0x5015)
        {
            Audio.WriteRegister(address, value);
            RefreshIrqPhysical();
            return;
        }

        if (address is >= 0x5113 and <= 0x5117)
        {
            _prgRegisters[address - 0x5113] = value;
            RefreshPrgDiagnostics();
            return;
        }
        if (address is >= 0x5120 and <= 0x512B)
        {
            var index = address - 0x5120;
            _chrRegisters[index] = (ushort)(value | (_chrUpperBits << 8));
            _lastChrSetB = ((_ppuControl & 0x20) != 0) && address >= 0x5128;
            LatchChrSetSelection();
            return;
        }

        switch (address)
        {
            case 0x5100: _prgMode = (byte)(value & 0x03); RefreshPrgDiagnostics(); break;
            case 0x5101:
                _chrMode = (byte)(value & 0x03);
                LatchChrSetSelection();
                break;
            case 0x5102: _prgRamProtect1 = (byte)(value & 0x03); break;
            case 0x5103: _prgRamProtect2 = (byte)(value & 0x03); break;
            case 0x5104:
                _exRamMode = (byte)(value & 0x03);
                RefreshPpuDataWakeState();
                RefreshCiramPhysical();
                break;
            case 0x5105: _nametableMapping = value; RefreshCiramPhysical(); break;
            case 0x5106: _fillTile = value; break;
            case 0x5107: _fillColor = (byte)(value & 0x03); break;
            case 0x5130: _chrUpperBits = (byte)(value & 0x03); break;
            case 0x5200:
                _splitEnabled = (value & 0x80) != 0;
                _splitRightSide = (value & 0x40) != 0;
                _splitDelimiterTile = (byte)(value & 0x1F);
                break;
            case 0x5201: _splitScroll = value; break;
            case 0x5202: _splitBank = value; break;
            case 0x5203: _irqTarget = value; break;
            case 0x5204: _irqEnabled = (value & 0x80) != 0; RefreshIrqPhysical(); break;
            case 0x5205: _multiplierValue1 = value; break;
            case 0x5206: _multiplierValue2 = value; break;
        }
    }

    private void WriteExRamCpu(ushort address, byte value)
    {
        if (_exRamMode == 3) return;
        if (_exRamMode <= 1 && !_ppuInFrame) value = 0;
        _exRam[address & 0x03FF] = value;
        ExRamCpuWriteCount++;
    }

    private void ObservePpuControlWrite(byte value)
    {
        var oldLargeSprites = (_ppuControl & 0x20) != 0;
        _ppuControl = value;
        var newLargeSprites = (value & 0x20) != 0;
        if (oldLargeSprites && !newLargeSprites) _lastChrSetB = false;
        LatchChrSetSelection();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ObservePcmRead(ushort address, byte value)
    {
        var pending = Audio.PcmIrqPending;
        Audio.ObserveCpuRead(address, value);
        if (pending != Audio.PcmIrqPending) RefreshIrqPhysical();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClockCpuCycle()
    {
        CpuCycleClockCount++;
        Audio.ClockCpuCycle();
        if (_ppuIdleCounter != 0)
        {
            _ppuIdleCounter--;
            if (_ppuIdleCounter == 0)
            {
                _ppuInFrame = false;
                _splitInRegion = false;
                _currentReadOverride = CurrentReadOverride.None;
                LatchChrSetSelection();
            }
        }
        RefreshIrqPhysical();
    }

    private void RefreshIrqPhysical()
    {
        var asserted = IrqAsserted;
        if (asserted && !_irqLineAsserted) IrqAssertCount++;
        _irqLineAsserted = asserted;
        if (asserted) IrqBar.Drive(DigitalLevel.Low);
        else IrqBar.Release();
    }

    private void ResetFrameTrackingOnVectorRead(ushort address)
    {
        if (address is not (0xFFFA or 0xFFFB)) return;
        _ppuInFrame = false;
        _needInFrame = false;
        _lastPpuReadAddress = 0;
        _scanlineCounter = 0;
        _irqPending = false;
        _splitInRegion = false;
        LatchChrSetSelection();
        RefreshIrqPhysical();
    }

    private void RefreshPrgDiagnostics()
    {
        if (_prgBankCount == 0)
        {
            Array.Clear(_prgWindowBanks);
            Array.Clear(_prgWindowRam);
            return;
        }
        for (var slot = 0; slot < 4; slot++)
        {
            var source = ResolvePrgSource((ushort)(0x8000 + slot * PrgBankSize));
            _prgWindowBanks[slot] = source.Selected ? source.Bank : -1;
            _prgWindowRam[slot] = source.Selected && source.UsesRam;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordCpuRead(ushort address, byte value)
    {
        CpuReadCount++;
        LastCpuReadAddress = address;
        LastCpuReadData = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadCpuHighCompiled(ushort address)
    {
        ResetFrameTrackingOnVectorRead(address);
        var source = ResolvePrgSource(address);
        var value = ReadPrgSource(source);
        if (source.UsesRam) WorkRamReadCount++;
        ObservePcmRead(address, value);
        RecordCpuRead(address, value);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteCpuHighCompiled(ushort address, byte value) => WritePrgMemory(address, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadCpu6000Compiled(ushort address)
    {
        var source = ResolvePrgSource(address);
        var value = ReadPrgSource(source);
        WorkRamReadCount++;
        RecordCpuRead(address, value);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteCpu6000Compiled(ushort address, byte value) => WritePrgMemory(address, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadCpuLowCompiled(ushort address)
    {
        var value = ReadLowRegister(address);
        LowRegisterReadCount++;
        RecordCpuRead(address, value);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteCpuLowCompiled(ushort address, byte value) => WriteLowRegister(address, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ObservePpuControlCompiled(int _, byte value) => ObservePpuControlWrite(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ObserveCompiledCpuBusCycle(bool _) => ClockCpuCycle();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ObservePpuReadCompiled(int address) => ObservePpuReadBegin((ushort)address);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadPpuPatternCompiled(ushort address)
    {
        var source = ResolvePpuReadSource(address);
        PpuReadCount++;
        return ReadPpuSource(source, address);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadPpuNametableCompiled(ushort address)
    {
        var source = ResolvePpuReadSource(address);
        PpuReadCount++;
        return ReadPpuSource(source, address);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WritePpuCompiled(ushort address, byte value) => WritePpuSource(ResolvePpuWriteSource(address), address, value);

    private bool IsHighCpuSelectedCompiled(int address, bool writeCycle)
    {
        var source = ResolvePrgSource((ushort)(0x8000 | address));
        return source.Selected && (!writeCycle || (source.UsesRam && PrgRamWriteEnabled));
    }

    private bool Is6000CpuSelectedCompiled(int address, bool writeCycle)
    {
        var source = ResolvePrgSource((ushort)address);
        return source.Selected && source.UsesRam && (!writeCycle || PrgRamWriteEnabled);
    }

    private bool IsLowCpuSelectedCompiled(int address, bool writeCycle)
    {
        var cpuAddress = (ushort)address;
        if (!writeCycle) return IsLowRegisterReadSelected(cpuAddress);
        if (cpuAddress is >= 0x5C00 and <= 0x5FFF) return _exRamMode != 3;
        return cpuAddress is >= 0x5000 and <= 0x5206;
    }

    private static bool IsPpuControlSnoopSelected(int address, bool writeCycle) => writeCycle && address == 0x2000;

    private bool IsPpuNametableCartridgeSelectedCompiled(int address, bool writeCycle)
    {
        var ppuAddress = (ushort)address;
        return writeCycle
            ? ResolvePpuWriteSource(ppuAddress).Kind == PpuDriveKind.ExRam
            : ResolvePpuReadSource(ppuAddress).CartridgeDrives;
    }

    private void RefreshPpuDataWakeState()
    {
        var enabled = IsPowered() && PpuWriteBar.SampledLevel == DigitalLevel.Low && (_chrRam || _exRamMode <= 1);
        PpuData.SetOwnerWakeEnabled(enabled);
    }

    private void ReleaseDataOutputs()
    {
        CpuData.Release();
        PpuData.Release();
    }

    private void ReleaseOutputs()
    {
        ReleaseDataOutputs();
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
            address => ReadCpuHighCompiled((ushort)(0x8000 | address)),
            (address, value) => WriteCpuHighCompiled((ushort)(0x8000 | address), value),
            ObserveCompiledCpuBusCycle,
            isSelected: IsHighCpuSelectedCompiled,
            writePhase: CompiledBusWritePhase.Complete,
            observeBusCyclePhase: CompiledBusCycleObservationPhase.Complete);

        if (_workRam.Length != 0)
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
                address => ReadCpu6000Compiled((ushort)address),
                (address, value) => WriteCpu6000Compiled((ushort)address, value),
                isSelected: Is6000CpuSelectedCompiled,
                writePhase: CompiledBusWritePhase.Complete);
        }

        yield return new CompiledBusTargetDescriptor(
            this,
            CpuAddress.Pins,
            CpuData.Pins,
            new[]
            {
                new CompiledPinCondition(CpuReadWrite, DigitalLevel.High),
                new CompiledPinCondition(CpuM2, DigitalLevel.High),
                new CompiledPinCondition(CpuRomSelectBar, DigitalLevel.High)
            },
            new[]
            {
                new CompiledPinCondition(CpuReadWrite, DigitalLevel.Low),
                new CompiledPinCondition(CpuM2, DigitalLevel.High),
                new CompiledPinCondition(CpuRomSelectBar, DigitalLevel.High)
            },
            CompiledBusReadPhase.Complete,
            address => ReadCpuLowCompiled((ushort)address),
            (address, value) => WriteCpuLowCompiled((ushort)address, value),
            isSelected: IsLowCpuSelectedCompiled,
            writePhase: CompiledBusWritePhase.Complete);

        yield return new CompiledBusTargetDescriptor(
            this,
            CpuAddress.Pins,
            CpuData.Pins,
            Array.Empty<CompiledPinCondition>(),
            new[]
            {
                new CompiledPinCondition(CpuReadWrite, DigitalLevel.Low),
                new CompiledPinCondition(CpuM2, DigitalLevel.High),
                new CompiledPinCondition(CpuRomSelectBar, DigitalLevel.High)
            },
            CompiledBusReadPhase.Complete,
            null,
            ObservePpuControlCompiled,
            isSelected: IsPpuControlSnoopSelected,
            writePhase: CompiledBusWritePhase.Complete);

        // Read observer: MMC5 scanline/frame/extended-mode circuitry snoops every physical PPU /RD cycle.
        yield return new CompiledBusTargetDescriptor(
            this,
            PpuAddress.Pins,
            PpuData.Pins,
            new[] { new CompiledPinCondition(PpuReadBar, DigitalLevel.Low) },
            Array.Empty<CompiledPinCondition>(),
            CompiledBusReadPhase.Complete,
            null,
            null,
            observeReadBegin: ObservePpuReadCompiled);

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
            address => ReadPpuPatternCompiled((ushort)address),
            _chrRam ? (address, value) => WritePpuCompiled((ushort)address, value) : null);

        yield return new CompiledBusTargetDescriptor(
            this,
            PpuAddress.Pins,
            PpuData.Pins,
            new[]
            {
                new CompiledPinCondition(PpuAddress.Pins[13], DigitalLevel.High),
                new CompiledPinCondition(PpuReadBar, DigitalLevel.Low)
            },
            new[]
            {
                new CompiledPinCondition(PpuAddress.Pins[13], DigitalLevel.High),
                new CompiledPinCondition(PpuWriteBar, DigitalLevel.Low)
            },
            CompiledBusReadPhase.Complete,
            address => ReadPpuNametableCompiled((ushort)address),
            (address, value) => WritePpuCompiled((ushort)address, value),
            isSelected: IsPpuNametableCartridgeSelectedCompiled);
    }

    bool ICompiledBusAddressCombinationalComponent.TryEvaluateCompiledBusAddressOutput(
        DigitalPin output,
        uint address,
        bool readCycle,
        out CompiledDriveState drive)
    {
        var ppuAddress = (ushort)(address & 0x3FFF);
        var source = readCycle ? ResolvePpuReadSource(ppuAddress) : ResolvePpuWriteSource(ppuAddress);
        if (ReferenceEquals(output, CiramChipEnableBar))
        {
            drive = new CompiledDriveState(source.CiramPage >= 0 ? DigitalLevel.Low : DigitalLevel.High);
            return true;
        }
        if (ReferenceEquals(output, CiramA10))
        {
            drive = new CompiledDriveState(source.CiramPage switch
            {
                0 => DigitalLevel.Low,
                1 => DigitalLevel.High,
                _ => DigitalLevel.Unknown
            });
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
        if (ReferenceEquals(output, CiramChipEnableBar) || ReferenceEquals(output, CiramA10))
        {
            ushort address = 0;
            for (var bit = 0; bit < PpuAddress.Width; bit++)
            {
                var level = sampleInput(PpuAddress.Pins[bit]);
                if (level is not (DigitalLevel.Low or DigitalLevel.High))
                {
                    drive = new CompiledDriveState(DigitalLevel.Unknown);
                    return true;
                }
                if (level == DigitalLevel.High) address |= (ushort)(1 << bit);
            }

            var readCycle = sampleInput(PpuReadBar) == DigitalLevel.Low;
            var source = readCycle ? ResolvePpuReadSource(address) : ResolvePpuWriteSource(address);
            if (ReferenceEquals(output, CiramChipEnableBar))
            {
                drive = new CompiledDriveState(source.CiramPage >= 0 ? DigitalLevel.Low : DigitalLevel.High);
                return true;
            }
            drive = new CompiledDriveState(source.CiramPage switch
            {
                0 => DigitalLevel.Low,
                1 => DigitalLevel.High,
                _ => DigitalLevel.Unknown
            });
            return true;
        }

        if (ReferenceEquals(output, IrqBar))
        {
            drive = new CompiledDriveState(IrqAsserted ? DigitalLevel.Low : DigitalLevel.HighImpedance);
            return true;
        }

        drive = default;
        return false;
    }

    private static int ResolveChrRamSize(VirtualHardwareNesRomImage image)
    {
        if (image.TotalChrRamSizeBytes > 0) return image.TotalChrRamSizeBytes;
        if (!image.HasExplicitRamSizes) return 8 * 1024;
        throw new NotSupportedException("MMC5 image has no CHR ROM and declares no CHR RAM.");
    }

    private static int ResolveWorkRamSize(VirtualHardwareNesRomImage image)
    {
        if (image.HasExplicitRamSizes) return image.TotalPrgRamSizeBytes;
        // Legacy iNES does not describe MMC5 board RAM topology reliably. Model the
        // full 64 KiB bank decode used by unclassified legacy boards rather than
        // pretending a single 8 KiB socket is authoritative.
        return 64 * 1024;
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
}
