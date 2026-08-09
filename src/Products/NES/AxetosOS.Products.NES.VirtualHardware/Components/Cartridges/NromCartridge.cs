using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

/// <summary>
/// Standalone mapper-0 cartridge board. PRG and CHR devices react only to the
/// normalized cartridge connector pins; no CPU, PPU, or motherboard calls are used.
/// </summary>
public sealed class NromCartridge : VirtualHardwareComponent, IReplaceableCartridgeHardware, ICompiledBusTargetProvider, ICompiledCombinationalComponent, ICompiledStaticCombinationalComponent
{
    private byte[] _prg = [];
    private byte[] _chr = [];
    private bool _chrRam;
    private VirtualHardwareNesMirroring _mirroring;
    private bool _cpuReadAddressSelected;
    private ushort _cpuSelectedAddress;
    private byte _cpuSelectedData;
    private bool _ppuReadActive;
    private bool _ppuWriteActive;
    private readonly ulong _powerInputMask;
    private readonly ulong _cpuAddressControlInputMask;
    private readonly ulong _cpuM2InputMask;
    private readonly ulong _cpuRomSelectInputMask;
    private readonly ulong _ppuAddressControlInputMask;
    private readonly ulong _ppuDataInputMask;

    public NromCartridge(string componentId) : base(componentId)
    {
        Vcc = AddPin("VCC", PinDirection.Input);
        Gnd = AddPin("GND", PinDirection.Input);
        CpuAddress = new DigitalBus($"{componentId}.CPU.A", Enumerable.Range(0, 15).Select(i => AddPin($"CPU.A{i}", PinDirection.Input)).ToArray());
        CpuData = new DigitalBus($"{componentId}.CPU.D", Enumerable.Range(0, 8).Select(i => AddPin($"CPU.D{i}", PinDirection.Bidirectional)).ToArray());
        // CPU D0-D7 still arrive at the cartridge connector like every other
        // motherboard signal. Mapper 0 simply ignores them internally because
        // it has no CPU-write register.
        CpuReadWrite = AddPin("CPU.RW", PinDirection.Input);
        CpuM2 = AddPin("CPU.M2", PinDirection.Input);
        CpuRomSelectBar = AddPin("CPU.ROMSEL_BAR", PinDirection.Input);
        // The console's 74LS373 has already demultiplexed RP2C0x AD0-AD7
        // before these cartridge-connector pins. A0-A13 are address-only and
        // D0-D7 are the independent bidirectional PPU data bus.
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
        _cpuAddressControlInputMask = CpuAddress.InputChangeMask
            | CpuReadWrite.InputChangeMask;
        _cpuM2InputMask = CpuM2.InputChangeMask;
        _cpuRomSelectInputMask = CpuRomSelectBar.InputChangeMask;
        _ppuAddressControlInputMask = PpuAddress.InputChangeMask
            | PpuReadBar.InputChangeMask
            | PpuWriteBar.InputChangeMask;
        _ppuDataInputMask = PpuData.InputChangeMask;

        // Mapper 0 has no CPU-side write register, so CPU D0-D7 never activate
        // internal cartridge logic. They still retain their connector levels.
        CpuData.SetOwnerWakeEnabled(false);
        PpuData.SetOwnerWakeEnabled(false);
    
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
    public DigitalPin CpuRomSelectBar { get; }
    public DigitalBus PpuAddress { get; }
    public DigitalBus PpuData { get; }
    public DigitalPin PpuReadBar { get; }
    public DigitalPin PpuWriteBar { get; }
    public DigitalPin CiramChipEnableBar { get; }
    public DigitalPin CiramA10 { get; }
    public DigitalPin IrqBar { get; }
    public int MapperNumber => 0;
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
            && _chrRam
            && PpuWriteBar.SampledLevel == DigitalLevel.Low;
        PpuData.SetOwnerWakeEnabled(enabled);
    }

    private void InitializePackageState() => ApplyResetState();
    private void ApplyResetState()
    {
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
        // CPU D0-D7 never feed an NROM register. PPU D0-D7 only feed CHR
        // RAM during an active write; PPU A0-A13 arrive separately through the
        // motherboard/cartridge connector.
        var powerChanged = (changedInputMask & _powerInputMask) != 0;
        var cpuAddressOrControlChanged = (changedInputMask & _cpuAddressControlInputMask) != 0;
        var cpuM2Changed = (changedInputMask & _cpuM2InputMask) != 0;
        var cpuRomSelectChanged = (changedInputMask & _cpuRomSelectInputMask) != 0;
        var ppuAddressOrControlChanged = (changedInputMask & _ppuAddressControlInputMask) != 0;
        var ppuDataChanged = (changedInputMask & _ppuDataInputMask) != 0;

        if (!powerChanged
            && !cpuAddressOrControlChanged
            && !cpuM2Changed
            && !cpuRomSelectChanged
            && !ppuAddressOrControlChanged
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

        if (powerChanged || ppuAddressOrControlChanged) RefreshPpuDataWakeState();

        var ppuDataCanMatter = ppuDataChanged
            && _chrRam
            && PpuWriteBar.SampledLevel == DigitalLevel.Low;
        if (powerChanged || ppuAddressOrControlChanged || ppuDataCanMatter)
            ProcessPpuPort();

        if (!powerChanged && cpuM2Changed && CpuM2.SampledLevel == DigitalLevel.Low)
            CountCpuReadTransaction();

        if (powerChanged || cpuAddressOrControlChanged || cpuRomSelectChanged || cpuM2Changed)
            UpdateCpuPrgOutput();
    }

    private void ProcessPpuPort()
    {
        var ppuAddressKnown = PpuAddress.TrySample(out var rawPpuAddress);
        var ppuAddress = (ushort)(rawPpuAddress & 0x3FFF);
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

            if (ppuAddress < 0x2000)
            {
                ppuReadSelected = PpuReadBar.SampledLevel == DigitalLevel.Low;
                ppuWriteSelected = _chrRam && PpuWriteBar.SampledLevel == DigitalLevel.Low;

                if (ppuReadSelected)
                {
                    PpuData.Drive(_chr[ppuAddress & 0x1FFF]);
                    if (!_ppuReadActive) PpuReadCount++;
                }
                else
                {
                    PpuData.Release();
                    if (ppuWriteSelected && PpuData.TrySample(out var data) && !_ppuWriteActive)
                    {
                        _chr[ppuAddress & 0x1FFF] = (byte)data;
                        PpuWriteCount++;
                    }
                }
            }
            else
            {
                PpuData.Release();
            }
        }
        else
        {
            CiramChipEnableBar.Drive(DigitalLevel.High);
            CiramA10.Drive(DigitalLevel.Unknown);
            PpuData.Release();
        }

        _ppuReadActive = ppuReadSelected;
        _ppuWriteActive = ppuWriteSelected;
    }

    private void UpdateCpuPrgOutput()
    {
        var cpuAddressKnown = CpuAddress.TrySample(out var cpuAddress);
        _cpuReadAddressSelected = cpuAddressKnown
            && CpuRomSelectBar.SampledLevel == DigitalLevel.Low
            && CpuReadWrite.SampledLevel == DigitalLevel.High;

        if (!_cpuReadAddressSelected)
        {
            CpuData.Release();
            return;
        }

        _cpuSelectedAddress = (ushort)(0x8000 | cpuAddress);
        var index = _prg.Length == 16 * 1024
            ? (int)(cpuAddress & 0x3FFF)
            : (int)(cpuAddress & 0x7FFF);
        _cpuSelectedData = _prg[index];
        CpuData.Drive(_cpuSelectedData);
    }

    private void CountCpuReadTransaction()
    {
        // Count the transaction at the end of the M2-qualified window. /ROMSEL
        // and the 15 cartridge address pins described the read while M2 was high.
        if (!_cpuReadAddressSelected || CpuM2.SampledLevel != DigitalLevel.Low) return;
        CpuReadCount++;
        LastCpuReadAddress = _cpuSelectedAddress;
        LastCpuReadData = _cpuSelectedData;
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
            Array.Empty<CompiledPinCondition>(),
            CompiledBusReadPhase.Complete,
            address => ReadCpuCompiled((ushort)(0x8000 | address)),
            null);

        Action<int, byte>? compiledPpuWrite = _chrRam
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
            compiledPpuWrite);
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


}
