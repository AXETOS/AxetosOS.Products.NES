using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

/// <summary>
/// Standalone mapper-0 cartridge board. PRG and CHR devices react only to the
/// normalized cartridge connector pins; no CPU, PPU, or motherboard calls are used.
/// </summary>
public sealed class NromCartridge : VirtualHardwareComponent, ICompiledBusTargetProvider, ICompiledCombinationalComponent, ICompiledExternalDevice
{
    private byte[] _prg = [];
    private byte[] _chr = [];
    private bool _chrRam;
    private VirtualHardwareNesMirroring _mirroring;
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

    public NromCartridge(string componentId) : base(componentId)
    {
        Vcc = AddPin("VCC", PinDirection.Input);
        Gnd = AddPin("GND", PinDirection.Input);
        CpuAddress = new DigitalBus($"{componentId}.CPU.A", Enumerable.Range(0, 16).Select(i => AddPin($"CPU.A{i}", PinDirection.Input)).ToArray());
        CpuData = new DigitalBus($"{componentId}.CPU.D", Enumerable.Range(0, 8).Select(i => AddPin($"CPU.D{i}", PinDirection.Bidirectional)).ToArray());
        // CPU D0-D7 still arrive at the cartridge connector like every other
        // motherboard signal. Mapper 0 simply ignores them internally because
        // it has no CPU-write register.
        CpuReadWrite = AddPin("CPU.RW", PinDirection.Input);
        CpuM2 = AddPin("CPU.M2", PinDirection.Input, DigitalInputActivation.RisingEdge);
        PpuAddressData = new DigitalBus($"{componentId}.PPU.AD", Enumerable.Range(0, 8).Select(i => AddPin($"PPU.AD{i}", PinDirection.Bidirectional)).ToArray());
        // PPU AD0-D7 are ordinary connector pins. Whether a transition matters
        // is decided by ALE,/RD,/WR and the cartridge's own CHR circuitry.
        PpuHighAddress = new DigitalBus($"{componentId}.PPU.AH", Enumerable.Range(8, 6).Select(i => AddPin($"PPU.A{i}", PinDirection.Input)).ToArray());
        PpuAle = AddPin("PPU.ALE", PinDirection.Input);
        PpuReadBar = AddPin("PPU.RD_BAR", PinDirection.Input);
        PpuWriteBar = AddPin("PPU.WR_BAR", PinDirection.Input);
        CiramChipEnableBar = AddPin("CIRAM.CE_BAR", PinDirection.Output);
        CiramA10 = AddPin("CIRAM.A10", PinDirection.Output);
        IrqBar = AddPin("IRQ_BAR", PinDirection.Output);

        _powerInputMask = Vcc.InputChangeMask | Gnd.InputChangeMask;
        _cpuAddressControlInputMask = CpuAddress.InputChangeMask
            | CpuReadWrite.InputChangeMask;
        _cpuM2InputMask = CpuM2.InputChangeMask;
        _ppuControlInputMask = PpuHighAddress.InputChangeMask
            | PpuAle.InputChangeMask
            | PpuReadBar.InputChangeMask
            | PpuWriteBar.InputChangeMask;
        _ppuAddressDataInputMask = PpuAddressData.InputChangeMask;

        // Mapper 0 has no CPU-side write register, so CPU D0-D7 never activate
        // internal cartridge logic. They still retain their connector levels.
        CpuData.SetOwnerWakeEnabled(false);
        PpuAddressData.SetOwnerWakeEnabled(false);
    
        InitializePackageState();
    }

    public void LoadImage(VirtualHardwareNesRomImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.MapperNumber != 0) throw new NotSupportedException($"Mapper {image.MapperNumber} is not NROM.");
        if (image.PrgRom.Length is not (16 * 1024) and not (32 * 1024))
            throw new ArgumentException("NROM PRG must be 16 KiB or 32 KiB.", nameof(image));
        _prg = image.PrgRom.ToArray();
        _chrRam = image.ChrRom.Length == 0;
        _chr = _chrRam ? new byte[8 * 1024] : image.ChrRom.ToArray();
        if (_chr.Length != 8 * 1024) throw new ArgumentException("NROM CHR must be 8 KiB or absent for CHR RAM.", nameof(image));
        _mirroring = image.Mirroring;
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
        _cpuSelectedAddress = 0;
        _cpuSelectedData = 0;
        _ppuReadActive = false;
        _ppuWriteActive = false;
        RefreshPpuDataWakeState();
        ReleaseOutputs();
    }

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
    public bool IsChrRam => _chrRam;
    internal VirtualHardwareNesMirroring CompiledMirroring => _mirroring;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal byte ReadCpuCompiled(ushort address)
    {
        var index = _prg.Length == 16 * 1024
            ? address & 0x3FFF
            : address & 0x7FFF;
        var value = _prg[index];
        CpuReadCount++;
        LastCpuReadAddress = address;
        LastCpuReadData = value;
        return value;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal byte ReadPpuCompiled(ushort address)
    {
        PpuReadCount++;
        return _chr[address & 0x1FFF];
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void WritePpuCompiled(ushort address, byte value)
    {
        if (!_chrRam) return;
        _chr[address & 0x1FFF] = value;
        PpuWriteCount++;
    }

    public bool IsInserted { get; private set; }
    public ulong CpuReadCount { get; private set; }
    public ushort LastCpuReadAddress { get; private set; }
    public byte LastCpuReadData { get; private set; }
    public ulong PpuReadCount { get; private set; }
    public ulong PpuWriteCount { get; private set; }

    private void RefreshPpuDataWakeState()
    {
        var enabled = IsInserted
            && Vcc.SampledLevel == DigitalLevel.High
            && Gnd.SampledLevel == DigitalLevel.Low
            && (PpuAle.SampledLevel == DigitalLevel.High
                || (_chrRam && PpuWriteBar.SampledLevel == DigitalLevel.Low));
        PpuAddressData.SetOwnerWakeEnabled(enabled);
    }

    private void InitializePackageState() => ApplyResetState();
    private void ApplyResetState()
    {
        _ppuLowAddressLatch = 0;
        _cpuReadAddressSelected = false;
        _cpuSelectedAddress = 0;
        _cpuSelectedData = 0;
        _ppuReadActive = false;
        _ppuWriteActive = false;
        RefreshPpuDataWakeState();
        ReleaseOutputs();
    }

    protected override void OnInputChanges(ulong changedInputMask)
    {
        if (changedInputMask == 0) return;

        // All connector pins have already received their physical levels.  The
        // cartridge itself decides whether a transition can affect mapper-0.
        // CPU D0-D7 never feed an NROM register.  PPU AD0-D7 only feed the
        // low-address latch while ALE is high or CHR RAM during an active write.
        var powerChanged = (changedInputMask & _powerInputMask) != 0;
        var cpuAddressOrControlChanged = (changedInputMask & _cpuAddressControlInputMask) != 0;
        var cpuM2Changed = (changedInputMask & _cpuM2InputMask) != 0;
        var ppuControlChanged = (changedInputMask & _ppuControlInputMask) != 0;
        var ppuDataChanged = (changedInputMask & _ppuAddressDataInputMask) != 0;

        if (!powerChanged
            && !cpuAddressOrControlChanged
            && !cpuM2Changed
            && !ppuControlChanged
            && !ppuDataChanged)
        {
            // This is normally mapper-0 CPU data traffic.  The pins are current;
            // there is simply no internal circuit connected to them.
            return;
        }

        if (!IsInserted || Vcc.SampledLevel != DigitalLevel.High || Gnd.SampledLevel != DigitalLevel.Low)
        {
            // Ordinary bus traffic while already inactive costs only the checks
            // above.  Power/insertion transitions still release every output.
            if (!powerChanged && !IsInserted) return;
            _cpuReadAddressSelected = false;
            _cpuSelectedAddress = 0;
            _cpuSelectedData = 0;
            _ppuReadActive = false;
            _ppuWriteActive = false;
            ReleaseOutputs();
            RefreshPpuDataWakeState();
            return;
        }

        if (powerChanged || ppuControlChanged) RefreshPpuDataWakeState();

        var ppuDataCanMatter = ppuDataChanged
            && (PpuAle.SampledLevel == DigitalLevel.High
                || (_chrRam && PpuWriteBar.SampledLevel == DigitalLevel.Low));
        if (powerChanged || ppuControlChanged || ppuDataCanMatter)
            ProcessPpuPort();

        if (powerChanged || cpuAddressOrControlChanged)
            UpdateCpuPrgOutput();

        if (!powerChanged && cpuM2Changed)
            CountCpuReadTransaction();
    }

    private void ProcessPpuPort()
    {
        if (PpuAle.SampledLevel == DigitalLevel.High)
        {
            // During ALE the cartridge must not own AD0-AD7; the PPU is placing
            // the low address byte on the multiplexed bus.
            PpuAddressData.Release();
            _ppuReadActive = false;
            _ppuWriteActive = false;
            if (PpuAddressData.TrySample(out var low))
                _ppuLowAddressLatch = (byte)low;
        }

        var ppuAddressKnown = PpuHighAddress.TrySample(out var high);
        var ppuAddress = (ushort)(((high & 0x3F) << 8) | _ppuLowAddressLatch);
        var ppuReadSelected = false;
        var ppuWriteSelected = false;

        if (ppuAddressKnown)
        {
            var nametable = (ppuAddress & 0x2000) != 0;
            CiramChipEnableBar.Drive(nametable ? DigitalLevel.Low : DigitalLevel.High);
            var a10SourceBit = _mirroring switch
            {
                VirtualHardwareNesMirroring.Vertical => 10,
                VirtualHardwareNesMirroring.Horizontal => 11,
                _ => 10
            };
            CiramA10.Drive((ppuAddress & (1 << a10SourceBit)) == 0 ? DigitalLevel.Low : DigitalLevel.High);

            if (PpuAle.SampledLevel != DigitalLevel.High && ppuAddress < 0x2000)
            {
                ppuReadSelected = PpuReadBar.SampledLevel == DigitalLevel.Low;
                ppuWriteSelected = _chrRam && PpuWriteBar.SampledLevel == DigitalLevel.Low;

                if (ppuReadSelected)
                {
                    PpuAddressData.Drive(_chr[ppuAddress & 0x1FFF]);
                    if (!_ppuReadActive) PpuReadCount++;
                }
                else
                {
                    PpuAddressData.Release();
                    if (ppuWriteSelected && PpuAddressData.TrySample(out var data) && !_ppuWriteActive)
                    {
                        _chr[ppuAddress & 0x1FFF] = (byte)data;
                        PpuWriteCount++;
                    }
                }
            }
            else if (PpuAle.SampledLevel != DigitalLevel.High)
            {
                PpuAddressData.Release();
            }
        }
        else
        {
            CiramChipEnableBar.Drive(DigitalLevel.High);
            CiramA10.Drive(DigitalLevel.Unknown);
            if (PpuAle.SampledLevel != DigitalLevel.High) PpuAddressData.Release();
        }

        _ppuReadActive = ppuReadSelected;
        _ppuWriteActive = ppuWriteSelected;
    }

    private void UpdateCpuPrgOutput()
    {
        var cpuAddressKnown = CpuAddress.TrySample(out var cpuAddress);
        _cpuReadAddressSelected = cpuAddressKnown
            && cpuAddress >= 0x8000
            && CpuReadWrite.SampledLevel == DigitalLevel.High;

        if (!_cpuReadAddressSelected)
        {
            CpuData.Release();
            return;
        }

        _cpuSelectedAddress = (ushort)cpuAddress;
        var index = _prg.Length == 16 * 1024
            ? (int)(cpuAddress & 0x3FFF)
            : (int)(cpuAddress & 0x7FFF);
        _cpuSelectedData = _prg[index];
        CpuData.Drive(_cpuSelectedData);
    }

    private void CountCpuReadTransaction()
    {
        // Mapper 0 has no M2-falling-edge work. The pin still receives Low,
        // but only a real Low->High edge qualifies a CPU cartridge read.
        if (!_cpuReadAddressSelected || CpuM2.SampledLevel != DigitalLevel.High) return;
        CpuReadCount++;
        LastCpuReadAddress = _cpuSelectedAddress;
        LastCpuReadData = _cpuSelectedData;
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
            Array.Empty<CompiledPinCondition>(),
            CompiledBusReadPhase.Complete,
            address => ReadCpuCompiled((ushort)address),
            null);

        var ppuAddressPins = new DigitalPin[PpuAddressData.Width + PpuHighAddress.Width];
        for (var bit = 0; bit < PpuAddressData.Width; bit++)
            ppuAddressPins[bit] = PpuAddressData.Pins[bit];
        for (var bit = 0; bit < PpuHighAddress.Width; bit++)
            ppuAddressPins[bit + PpuAddressData.Width] = PpuHighAddress.Pins[bit];

        Action<int, byte>? compiledPpuWrite = _chrRam
            ? (address, value) => WritePpuCompiled((ushort)address, value)
            : null;
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
            compiledPpuWrite);
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
            var sourceBit = _mirroring == VirtualHardwareNesMirroring.Horizontal ? 3 : 2;
            drive = new CompiledDriveState(sampleInput(PpuHighAddress.Pins[sourceBit]));
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
