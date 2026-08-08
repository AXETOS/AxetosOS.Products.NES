using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

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
    private const int PrgRamSize = 8 * 1024;

    private byte[] _prg = [];
    private byte[] _chr = [];
    private readonly byte[] _prgRam = new byte[PrgRamSize];
    private bool _chrRam;
    private byte _shiftRegister = 0x10;
    private byte _control = 0x0C;
    private byte _chrBank0;
    private byte _chrBank1;
    private byte _prgBank;
    private byte _ppuLowAddressLatch;
    private bool _cpuReadAddressSelected;
    private ushort _cpuSelectedAddress;
    private byte _cpuSelectedData;
    private bool _ppuReadActive;
    private bool _ppuWriteActive;
    private readonly ulong _powerInputMask;
    private readonly ulong _cpuAddressControlInputMask;
    private readonly ulong _cpuM2InputMask;
    private readonly ulong _ppuControlInputMask;
    private readonly ulong _ppuAddressDataInputMask;

    public Mmc1Cartridge(string componentId) : base(componentId)
    {
        Vcc = AddPin("VCC", PinDirection.Input);
        Gnd = AddPin("GND", PinDirection.Input);
        CpuAddress = new DigitalBus($"{componentId}.CPU.A",
            Enumerable.Range(0, 16).Select(i => AddPin($"CPU.A{i}", PinDirection.Input)).ToArray());
        CpuData = new DigitalBus($"{componentId}.CPU.D",
            Enumerable.Range(0, 8).Select(i => AddPin($"CPU.D{i}", PinDirection.Bidirectional)).ToArray());
        CpuReadWrite = AddPin("CPU.RW", PinDirection.Input);
        // CPU writes are captured at the end of the M2-qualified bus cycle.  The
        // RP2A0x package publishes its M2 rising edge and next-cycle bus outputs
        // as one atomic package change-set, so sampling on that rising edge would
        // observe the newly-started cycle rather than the cycle that just ended.
        // The falling edge is a real cartridge connector edge and occurs while
        // address, R/W and CPU data still represent the active transaction.
        CpuM2 = AddPin("CPU.M2", PinDirection.Input, DigitalInputActivation.FallingEdge);
        PpuAddressData = new DigitalBus($"{componentId}.PPU.AD",
            Enumerable.Range(0, 8).Select(i => AddPin($"PPU.AD{i}", PinDirection.Bidirectional)).ToArray());
        PpuHighAddress = new DigitalBus($"{componentId}.PPU.AH",
            Enumerable.Range(8, 6).Select(i => AddPin($"PPU.A{i}", PinDirection.Input)).ToArray());
        PpuAle = AddPin("PPU.ALE", PinDirection.Input);
        PpuReadBar = AddPin("PPU.RD_BAR", PinDirection.Input);
        PpuWriteBar = AddPin("PPU.WR_BAR", PinDirection.Input);
        CiramChipEnableBar = AddPin("CIRAM.CE_BAR", PinDirection.Output);
        CiramA10 = AddPin("CIRAM.A10", PinDirection.Output);
        IrqBar = AddPin("IRQ_BAR", PinDirection.Output);

        _powerInputMask = Vcc.InputChangeMask | Gnd.InputChangeMask;
        _cpuAddressControlInputMask = CpuAddress.InputChangeMask | CpuReadWrite.InputChangeMask;
        _cpuM2InputMask = CpuM2.InputChangeMask;
        _ppuControlInputMask = PpuHighAddress.InputChangeMask | PpuAle.InputChangeMask |
            PpuReadBar.InputChangeMask | PpuWriteBar.InputChangeMask;
        _ppuAddressDataInputMask = PpuAddressData.InputChangeMask;

        // Data pins always retain delivered levels, but MMC1 consumes CPU data
        // only on an M2-qualified write and PPU data only during ALE/CHR-RAM write.
        CpuData.SetOwnerWakeEnabled(false);
        PpuAddressData.SetOwnerWakeEnabled(false);
        ApplyResetState();
    }

    public int MapperNumber => 1;
    public DigitalPin Vcc { get; }
    public DigitalPin Gnd { get; }
    public DigitalBus CpuAddress { get; }
    public DigitalBus CpuData { get; }
    public DigitalPin CpuReadWrite { get; }
    public DigitalPin CpuM2 { get; }
    public DigitalBus PpuAddressData { get; }
    public DigitalBus PpuHighAddress { get; }
    public DigitalPin PpuAle { get; }
    public DigitalPin PpuReadBar { get; }
    public DigitalPin PpuWriteBar { get; }
    public DigitalPin CiramChipEnableBar { get; }
    public DigitalPin CiramA10 { get; }
    public DigitalPin IrqBar { get; }
    public bool IsInserted { get; private set; }
    public bool IsChrRam => _chrRam;
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
        _chrRam = image.ChrRom.Length == 0;
        _chr = _chrRam ? new byte[8 * 1024] : image.ChrRom.ToArray();
        Array.Clear(_prgRam);
        IsInserted = true;
        ApplyResetState();
        RefreshPpuDataWakeState();
    }

    public void Eject()
    {
        IsInserted = false;
        _prg = [];
        _chr = [];
        _cpuReadAddressSelected = false;
        _ppuReadActive = false;
        _ppuWriteActive = false;
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
        _ppuLowAddressLatch = 0;
        _cpuReadAddressSelected = false;
        _cpuSelectedAddress = 0;
        _cpuSelectedData = 0;
        _ppuReadActive = false;
        _ppuWriteActive = false;
        RefreshPpuDataWakeState();
        ReleaseOutputs();
    }

    private bool IsPowered() =>
        IsInserted && Vcc.SampledLevel == DigitalLevel.High && Gnd.SampledLevel == DigitalLevel.Low;

    private void RefreshPpuDataWakeState()
    {
        var enabled = IsPowered() &&
            (PpuAle.SampledLevel == DigitalLevel.High || (_chrRam && PpuWriteBar.SampledLevel == DigitalLevel.Low));
        PpuAddressData.SetOwnerWakeEnabled(enabled);
    }

    protected override void OnInputChanges(ulong changedInputMask)
    {
        if (changedInputMask == 0) return;
        var powerChanged = (changedInputMask & _powerInputMask) != 0;
        var cpuAddressOrControlChanged = (changedInputMask & _cpuAddressControlInputMask) != 0;
        var cpuM2Changed = (changedInputMask & _cpuM2InputMask) != 0;
        var ppuControlChanged = (changedInputMask & _ppuControlInputMask) != 0;
        var ppuDataChanged = (changedInputMask & _ppuAddressDataInputMask) != 0;

        if (!powerChanged && !cpuAddressOrControlChanged && !cpuM2Changed && !ppuControlChanged && !ppuDataChanged)
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

        if (powerChanged || ppuControlChanged) RefreshPpuDataWakeState();
        var ppuDataCanMatter = ppuDataChanged &&
            (PpuAle.SampledLevel == DigitalLevel.High || (_chrRam && PpuWriteBar.SampledLevel == DigitalLevel.Low));
        if (powerChanged || ppuControlChanged || ppuDataCanMatter)
            ProcessPpuPort();

        if (powerChanged || cpuAddressOrControlChanged)
            UpdateCpuOutput();

        // CpuM2 is package-owned falling-edge activated.  At this edge the
        // CPU bus still carries the transaction being completed, so both ROM
        // reads and MMC1/PRG-RAM writes are sampled from the physical pins.
        if (!powerChanged && cpuM2Changed && CpuM2.SampledLevel == DigitalLevel.Low)
            CompleteCpuTransaction();
    }

    private void UpdateCpuOutput()
    {
        if (!CpuAddress.TrySample(out var rawAddress))
        {
            _cpuReadAddressSelected = false;
            CpuData.Release();
            return;
        }

        var address = (ushort)rawAddress;
        _cpuReadAddressSelected = CpuReadWrite.SampledLevel == DigitalLevel.High && address >= 0x6000;
        if (!_cpuReadAddressSelected)
        {
            CpuData.Release();
            return;
        }

        _cpuSelectedAddress = address;
        _cpuSelectedData = address >= 0x8000
            ? ReadPrg(address)
            : _prgRam[address & 0x1FFF];
        CpuData.Drive(_cpuSelectedData);
    }

    private void CompleteCpuTransaction()
    {
        if (!CpuAddress.TrySample(out var rawAddress)) return;
        var address = (ushort)rawAddress;
        if (CpuReadWrite.SampledLevel == DigitalLevel.High)
        {
            if (!_cpuReadAddressSelected) return;
            CpuReadCount++;
            LastCpuReadAddress = _cpuSelectedAddress;
            LastCpuReadData = _cpuSelectedData;
            return;
        }

        if (address < 0x6000 || !CpuData.TrySample(out var rawData)) return;
        var data = (byte)rawData;
        if (address < 0x8000)
        {
            _prgRam[address & 0x1FFF] = data;
            return;
        }

        WriteMapperRegister(address, data);
    }

    private void ProcessPpuPort()
    {
        if (PpuAle.SampledLevel == DigitalLevel.High)
        {
            _ppuReadActive = false;
            _ppuWriteActive = false;
            if (PpuAddressData.TrySample(out var low)) _ppuLowAddressLatch = (byte)low;
        }

        if (!PpuHighAddress.TrySample(out var high))
        {
            CiramChipEnableBar.Drive(DigitalLevel.High);
            CiramA10.Drive(DigitalLevel.Unknown);
            if (PpuAle.SampledLevel != DigitalLevel.High) PpuAddressData.Release();
            return;
        }

        var address = (ushort)(((high & 0x3F) << 8) | _ppuLowAddressLatch);
        DriveCiramOutputs(address);
        var readSelected = false;
        var writeSelected = false;

        if (PpuAle.SampledLevel != DigitalLevel.High && address < 0x2000)
        {
            readSelected = PpuReadBar.SampledLevel == DigitalLevel.Low;
            writeSelected = _chrRam && PpuWriteBar.SampledLevel == DigitalLevel.Low;
            if (readSelected)
            {
                PpuAddressData.Drive(ReadChr(address));
                if (!_ppuReadActive) PpuReadCount++;
            }
            else
            {
                PpuAddressData.Release();
                if (writeSelected && PpuAddressData.TrySample(out var data) && !_ppuWriteActive)
                {
                    WriteChr(address, (byte)data);
                    PpuWriteCount++;
                }
            }
        }
        else if (PpuAle.SampledLevel != DigitalLevel.High)
        {
            PpuAddressData.Release();
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

    private void WriteMapperRegister(ushort address, byte value)
    {
        MapperWriteCount++;
        if ((value & 0x80) != 0)
        {
            _shiftRegister = 0x10;
            _control |= 0x0C;
            return;
        }

        var complete = (_shiftRegister & 0x01) != 0;
        _shiftRegister >>= 1;
        _shiftRegister |= (byte)((value & 0x01) << 4);
        if (!complete) return;

        var registerValue = (byte)(_shiftRegister & 0x1F);
        if (address <= 0x9FFF) _control = registerValue;
        else if (address <= 0xBFFF) _chrBank0 = registerValue;
        else if (address <= 0xDFFF) _chrBank1 = registerValue;
        else _prgBank = registerValue;
        _shiftRegister = 0x10;
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
        if (address < 0x8000) _prgRam[address & 0x1FFF] = value;
        else WriteMapperRegister(address, value);
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
        if (!_chrRam) return;
        WriteChr(address, value);
        PpuWriteCount++;
    }

    private void ReleaseOutputs()
    {
        CpuData.Release();
        PpuAddressData.Release();
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
                new CompiledPinCondition(CpuAddress.Pins[15], DigitalLevel.High)
            },
            new[]
            {
                new CompiledPinCondition(CpuReadWrite, DigitalLevel.Low),
                new CompiledPinCondition(CpuAddress.Pins[15], DigitalLevel.High)
            },
            CompiledBusReadPhase.Complete,
            address => ReadCpuCompiled((ushort)address),
            (address, value) => WriteCpuCompiled((ushort)address, value));

        yield return new CompiledBusTargetDescriptor(
            this,
            CpuAddress.Pins,
            CpuData.Pins,
            new[]
            {
                new CompiledPinCondition(CpuReadWrite, DigitalLevel.High),
                new CompiledPinCondition(CpuAddress.Pins[15], DigitalLevel.Low),
                new CompiledPinCondition(CpuAddress.Pins[14], DigitalLevel.High),
                new CompiledPinCondition(CpuAddress.Pins[13], DigitalLevel.High)
            },
            new[]
            {
                new CompiledPinCondition(CpuReadWrite, DigitalLevel.Low),
                new CompiledPinCondition(CpuAddress.Pins[15], DigitalLevel.Low),
                new CompiledPinCondition(CpuAddress.Pins[14], DigitalLevel.High),
                new CompiledPinCondition(CpuAddress.Pins[13], DigitalLevel.High)
            },
            CompiledBusReadPhase.Complete,
            address => ReadCpuCompiled((ushort)address),
            (address, value) => WriteCpuCompiled((ushort)address, value));

        var ppuAddressPins = new DigitalPin[PpuAddressData.Width + PpuHighAddress.Width];
        for (var bit = 0; bit < PpuAddressData.Width; bit++) ppuAddressPins[bit] = PpuAddressData.Pins[bit];
        for (var bit = 0; bit < PpuHighAddress.Width; bit++) ppuAddressPins[bit + PpuAddressData.Width] = PpuHighAddress.Pins[bit];
        Action<int, byte>? ppuWrite = _chrRam ? (address, value) => WritePpuCompiled((ushort)address, value) : null;
        yield return new CompiledBusTargetDescriptor(
            this,
            ppuAddressPins,
            PpuAddressData.Pins,
            new[]
            {
                new CompiledPinCondition(PpuHighAddress.Pins[5], DigitalLevel.Low),
                new CompiledPinCondition(PpuAle, DigitalLevel.Low),
                new CompiledPinCondition(PpuReadBar, DigitalLevel.Low)
            },
            _chrRam
                ? new[]
                {
                    new CompiledPinCondition(PpuHighAddress.Pins[5], DigitalLevel.Low),
                    new CompiledPinCondition(PpuAle, DigitalLevel.Low),
                    new CompiledPinCondition(PpuWriteBar, DigitalLevel.Low)
                }
                : Array.Empty<CompiledPinCondition>(),
            CompiledBusReadPhase.Complete,
            address => ReadPpuCompiled((ushort)address),
            ppuWrite);
    }

    bool ICompiledCombinationalComponent.TryEvaluateCompiledOutput(
        DigitalPin output,
        Func<DigitalPin, DigitalLevel> sampleInput,
        out CompiledDriveState drive)
    {
        if (ReferenceEquals(output, CiramChipEnableBar))
        {
            drive = new CompiledDriveState(sampleInput(PpuHighAddress.Pins[5]) switch
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
            else drive = new CompiledDriveState(sampleInput(PpuHighAddress.Pins[mode == 2 ? 2 : 3]));
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
