using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;
using AxetosOS.Products.NES.VirtualHardware.Components.Nes;
using AxetosOS.Products.NES.VirtualHardware.Loading;

namespace AxetosOS.Products.NES.VirtualHardware.Machines.Nes;

public sealed record VirtualNesBootDiagnostics(
    ActiveNesMotherboard Motherboard,
    int Mapper,
    int PrgRomBytes,
    int ChrRomBytes,
    ulong MasterCycles,
    ulong CpuInstructions,
    ushort ProgramCounter,
    byte CurrentOpcode,
    bool CpuHalted,
    ushort HaltOpcodeAddress,
    string CpuCycleState,
    ushort CpuBusAddress,
    bool CpuBusIsRead,
    bool CpuDataBusKnown,
    byte CpuDataBusValue,
    string CpuM2Level,
    ulong CartridgeCpuReads,
    ushort LastCartridgeCpuReadAddress,
    byte LastCartridgeCpuReadData,
    ulong CartridgePpuReads,
    ulong PpuFrames,
    ulong PpuVramReads,
    ulong NmiEdges,
    bool ResetVectorObserved,
    bool FirstOpcodeObserved,
    bool FirstVblankObserved,
    bool FirstNmiObserved);

public interface IVirtualNesVideoSink
{
    void AcceptPixel(ulong frame, int x, int y, byte colorCode, byte emphasis);

    void AcceptPixels(ReadOnlySpan<RicohVideoPixelSample> samples)
    {
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = samples[index];
            AcceptPixel(sample.Frame, sample.X, sample.Y, sample.ColorCode, sample.Emphasis);
        }
    }
}

public interface IVirtualNesAudioSink
{
    void AcceptLevelChange(ulong masterCycle, byte dacLevel);

    void ResetTimeline(ulong masterCycle, byte dacLevel) => AcceptLevelChange(masterCycle, dacLevel);

    void AcceptLevelChanges(ReadOnlySpan<RicohAudioDacSample> samples)
    {
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = samples[index];
            AcceptLevelChange(sample.MasterClock, sample.DacLevel);
        }
    }

    void CompleteThrough(ulong masterCycle, byte dacLevel);
}

public sealed class VirtualNesFrameBuffer : IVirtualNesVideoSink
{
    private readonly byte[] _colors = new byte[256 * 240];
    private readonly byte[] _emphasis = new byte[256 * 240];

    public ReadOnlyMemory<byte> Colors => _colors;
    public ReadOnlyMemory<byte> Emphasis => _emphasis;
    public ulong CompletedFrame { get; private set; }
    public ulong WrittenPixelCount { get; private set; }

    public void AcceptPixel(ulong frame, int x, int y, byte colorCode, byte emphasis)
    {
        if ((uint)x >= 256 || (uint)y >= 240) return;
        var index = (y * 256) + x;
        _colors[index] = colorCode;
        _emphasis[index] = emphasis;
        CompletedFrame = frame;
        WrittenPixelCount++;
    }

    public void AcceptPixels(ReadOnlySpan<RicohVideoPixelSample> samples)
    {
        for (var sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
        {
            var sample = samples[sampleIndex];
            if ((uint)sample.X >= 256 || (uint)sample.Y >= 240) continue;
            var pixelIndex = (sample.Y * 256) + sample.X;
            _colors[pixelIndex] = sample.ColorCode;
            _emphasis[pixelIndex] = sample.Emphasis;
            CompletedFrame = sample.Frame;
            WrittenPixelCount++;
        }
    }
}

public sealed class VirtualNesPcmBuffer : IVirtualNesAudioSink
{
    private readonly List<byte> _samples = [];
    private byte _currentLevel;

    public IReadOnlyList<byte> Samples => _samples;

    public void AcceptLevelChange(ulong masterCycle, byte dacLevel) => _currentLevel = dacLevel;

    public void AcceptLevelChanges(ReadOnlySpan<RicohAudioDacSample> samples)
    {
        if (samples.Length != 0) _currentLevel = samples[^1].DacLevel;
    }

    public void CompleteThrough(ulong masterCycle, byte dacLevel)
    {
        _currentLevel = dacLevel;
        _samples.Add(_currentLevel);
    }
}

/// <summary>
/// Host-side clock and presentation harness. It never executes CPU instructions,
/// reads cartridge memory, renders tiles/sprites, or synthesizes APU channels.
/// It only loads a ROM into cartridge hardware, toggles board pins/clocks, and
/// observes the selected package outputs.
/// </summary>
public sealed class VirtualNesBootHost
{
    private const int PortableStateFormatVersion = 1;
    private const int MaximumPortableStateBytes = 64 * 1024 * 1024;
    private static readonly byte[] PortableStateMagic = "AXNESST1"u8.ToArray();
    private readonly RegionalNesVirtualMachine _machine = new();
    private readonly Guid _stateOwnerId = Guid.NewGuid();
    private ulong _cartridgeGeneration;
    private ulong _masterCycles;
    private bool _resetVectorObserved;
    private bool _firstOpcodeObserved;
    private bool _firstVblankObserved;
    private bool _firstNmiObserved;
    private bool _bootChecksComplete;
    private Rp2A03? _observedCpu;
    private Rp2C02? _observedPpu;
    private IVirtualNesVideoSink? _videoSink;
    private IVirtualNesAudioSink? _audioSink;
    private readonly RicohVideoPixelSample[] _videoTransfer = new RicohVideoPixelSample[4096];
    private readonly RicohAudioDacSample[] _audioTransfer = new RicohAudioDacSample[1024];

    public RegionalNesVirtualMachine Machine => _machine;

    /// <summary>
    /// Production hosts compile the assembled Famicom machine before power-on.
    /// If a specialized compiled runtime was already selected by topology (for
    /// example the existing NROM fused runtime), it is preserved. Otherwise the
    /// product-agnostic whole-circuit compiler is enabled and the replaceable
    /// cartridge remains a separate external runtime unit. Diagnostic callers
    /// may disable this before loading a ROM to exercise the raw pin/net path.
    /// </summary>
    public bool AutomaticCompiledExecutionEnabled { get; set; } = true;

    public IVirtualNesVideoSink? VideoSink
    {
        get => _videoSink;
        set
        {
            _videoSink = value;
            _observedPpu?.VideoOutput.SetCaptureEnabled(value is not null);
        }
    }

    public IVirtualNesAudioSink? AudioSink
    {
        get => _audioSink;
        set
        {
            _audioSink = value;
            if (_observedCpu is null) return;

            _observedCpu.AudioDacOutput.SetCaptureEnabled(value is not null);
            if (value is not null)
            {
                value.AcceptLevelChange(
                    _masterCycles,
                    _observedCpu.AudioDacOutput.CurrentValue.DacLevel);
            }
        }
    }

    public VirtualHardwareNesRomImage LoadRom(
        string path,
        NesRegionSelection regionSelection = NesRegionSelection.Auto,
        PalCicVariant palCicVariant = PalCicVariant.PalA3195)
    {
        var image = VirtualHardwareNesRomReader.ReadFile(path);
        LoadRom(image, Path.GetFileName(path), regionSelection, palCicVariant);
        return image;
    }

    public void LoadRom(
        VirtualHardwareNesRomImage image,
        string? sourceName = null,
        NesRegionSelection regionSelection = NesRegionSelection.Auto,
        PalCicVariant palCicVariant = PalCicVariant.PalA3195)
    {
        ResetDiagnostics();
        _cartridgeGeneration++;
        _machine.InsertRom(image, sourceName, regionSelection, palCicVariant);
        PrepareAutomaticCompiledExecution();
        AttachOutputPins();
    }

    private void PrepareAutomaticCompiledExecution()
    {
        if (!AutomaticCompiledExecutionEnabled) return;
        if (_machine.ActiveMotherboard != ActiveNesMotherboard.Famicom) return;

        // RecompileTopology() already installs any topology-specific compiled
        // runtime that can be proven for this assembled machine. Keep that fast
        // path. If no such runtime exists, compile the fixed motherboard now and
        // bind the inserted cartridge through its generic physical facets.
        if (_machine.Famicom.CompiledPhysicalMachineEnabled
            || _machine.Famicom.CompiledLabMotherboardEnabled)
            return;

        _machine.Famicom.SetCompiledLabMotherboardEnabled(true);
    }

    public void PowerAndReleaseReset(int resetMasterCycles = 32)
    {
        if (resetMasterCycles < 1) throw new ArgumentOutOfRangeException(nameof(resetMasterCycles));
        _machine.PowerOn();
        AdvanceMasterCycles(resetMasterCycles);
        _machine.ReleaseReset();
    }

    public void AdvanceMasterCycles(int cycles)
    {
        if (cycles < 0) throw new ArgumentOutOfRangeException(nameof(cycles));
        if (cycles == 0) return;

        _machine.AdvanceMasterCycles(cycles);
        _masterCycles += (ulong)cycles;

        if (_videoSink is not null && _observedPpu is not null)
        {
            int drained;
            while ((drained = _observedPpu.VideoOutput.Drain(_videoTransfer)) != 0)
                _videoSink.AcceptPixels(_videoTransfer.AsSpan(0, drained));
        }

        if (_audioSink is not null && _observedCpu is not null)
        {
            int drained;
            while ((drained = _observedCpu.AudioDacOutput.Drain(_audioTransfer)) != 0)
                _audioSink.AcceptLevelChanges(_audioTransfer.AsSpan(0, drained));
            _audioSink.CompleteThrough(
                _masterCycles,
                _observedCpu.AudioDacOutput.CurrentValue.DacLevel);
        }
    }

    public VirtualNesMachineState CaptureState()
    {
        if (!_machine.IsPowered || _machine.Slot.InsertedImage is null)
            throw new InvalidOperationException("Power on a loaded NES before capturing machine state.");

        return new VirtualNesMachineState(
            _stateOwnerId,
            _cartridgeGeneration,
            _machine.ActiveMotherboard,
            _machine.Slot.InsertedImage.MapperNumber,
            _masterCycles,
            _resetVectorObserved,
            _firstOpcodeObserved,
            _firstVblankObserved,
            _firstNmiObserved,
            _bootChecksComplete,
            InMemoryHardwareState.Capture(_machine));
    }

    public void RestoreState(VirtualNesMachineState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.OwnerId != _stateOwnerId)
            throw new InvalidOperationException("This NES machine state belongs to a different host instance.");
        if (state.CartridgeGeneration != _cartridgeGeneration)
            throw new InvalidOperationException("This NES machine state belongs to a cartridge that is no longer loaded.");
        if (_machine.Slot.InsertedImage is null ||
            state.MapperNumber != _machine.Slot.InsertedImage.MapperNumber ||
            state.Motherboard != _machine.ActiveMotherboard)
            throw new InvalidOperationException("The loaded NES hardware does not match this machine state.");

        var controller1 = _machine.InspectControllerButtons(0);
        var controller2 = _machine.InspectControllerButtons(1);

        state.Hardware.Restore();
        RestoreControllerContacts(0, controller1);
        RestoreControllerContacts(1, controller2);

        _masterCycles = state.MasterCycles;
        _resetVectorObserved = state.ResetVectorObserved;
        _firstOpcodeObserved = state.FirstOpcodeObserved;
        _firstVblankObserved = state.FirstVblankObserved;
        _firstNmiObserved = state.FirstNmiObserved;
        _bootChecksComplete = state.BootChecksComplete;

        if (_audioSink is not null)
        {
            _audioSink.ResetTimeline(_masterCycles, GetCurrentAudioDacLevel());
        }
    }

    /// <summary>
    /// Captures a versioned cross-process state payload for the currently loaded
    /// physical NES. Unlike <see cref="CaptureState"/>, this payload contains no
    /// live object references and may be persisted by a host application.
    /// ROM bytes are deliberately excluded; the host must identify and reload
    /// the exact cartridge image before restoring the payload.
    /// </summary>
    public byte[] CapturePortableState()
    {
        if (!_machine.IsPowered || _machine.Slot.InsertedImage is null)
            throw new InvalidOperationException("Power on a loaded NES before capturing portable machine state.");

        var hardware = VirtualNesPortableState.Capture(_machine);
        if (hardware.Length > MaximumPortableStateBytes)
            throw new InvalidDataException("NES portable hardware state is too large to capture safely.");

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(PortableStateMagic);
        writer.Write(PortableStateFormatVersion);
        writer.Write((int)_machine.ActiveMotherboard);
        writer.Write(_machine.Slot.InsertedImage.MapperNumber);
        writer.Write(_masterCycles);

        byte flags = 0;
        if (_resetVectorObserved) flags |= 1 << 0;
        if (_firstOpcodeObserved) flags |= 1 << 1;
        if (_firstVblankObserved) flags |= 1 << 2;
        if (_firstNmiObserved) flags |= 1 << 3;
        if (_bootChecksComplete) flags |= 1 << 4;
        writer.Write(flags);

        writer.Write(hardware.Length);
        writer.Write(hardware);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Restores a cross-process state payload into an already loaded and powered
    /// NES whose motherboard and mapper match the machine that created it.
    /// Current external controller contacts remain live rather than being rewound.
    /// </summary>
    public void RestorePortableState(ReadOnlySpan<byte> state)
    {
        if (!_machine.IsPowered || _machine.Slot.InsertedImage is null)
            throw new InvalidOperationException("Load and power the matching NES cartridge before restoring portable machine state.");
        if (state.Length == 0 || state.Length > MaximumPortableStateBytes)
            throw new InvalidDataException("NES portable machine state has an invalid size.");

        using var stream = new MemoryStream(state.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        var magic = reader.ReadBytes(PortableStateMagic.Length);
        if (!magic.AsSpan().SequenceEqual(PortableStateMagic))
            throw new InvalidDataException("The NES portable machine state has an invalid signature.");

        var version = reader.ReadInt32();
        if (version != PortableStateFormatVersion)
        {
            throw new NotSupportedException(
                $"NES portable machine-state format {version} is not supported by this engine (expected {PortableStateFormatVersion}).");
        }

        var motherboardValue = reader.ReadInt32();
        if (!Enum.IsDefined(typeof(ActiveNesMotherboard), motherboardValue))
            throw new InvalidDataException("NES portable machine state contains an invalid motherboard identifier.");
        var motherboard = (ActiveNesMotherboard)motherboardValue;
        var mapper = reader.ReadInt32();
        var masterCycles = reader.ReadUInt64();
        var flags = reader.ReadByte();
        var hardwareLength = reader.ReadInt32();
        if (hardwareLength < 0 || hardwareLength > MaximumPortableStateBytes || hardwareLength > stream.Length - stream.Position)
            throw new InvalidDataException("NES portable machine state contains an invalid hardware payload length.");
        var hardware = reader.ReadBytes(hardwareLength);
        if (hardware.Length != hardwareLength)
            throw new EndOfStreamException("NES portable machine state ended unexpectedly.");
        if (stream.Position != stream.Length)
            throw new InvalidDataException("NES portable machine state contains trailing data.");

        if (motherboard != _machine.ActiveMotherboard || mapper != _machine.Slot.InsertedImage.MapperNumber)
        {
            throw new InvalidOperationException(
                $"The loaded NES hardware does not match this state. Save requires {motherboard}, mapper {mapper}; " +
                $"loaded machine is {_machine.ActiveMotherboard}, mapper {_machine.Slot.InsertedImage.MapperNumber}.");
        }

        var controller1 = _machine.InspectControllerButtons(0);
        var controller2 = _machine.InspectControllerButtons(1);

        VirtualNesPortableState.Restore(_machine, hardware);
        RestoreControllerContacts(0, controller1);
        RestoreControllerContacts(1, controller2);

        _masterCycles = masterCycles;
        _resetVectorObserved = (flags & (1 << 0)) != 0;
        _firstOpcodeObserved = (flags & (1 << 1)) != 0;
        _firstVblankObserved = (flags & (1 << 2)) != 0;
        _firstNmiObserved = (flags & (1 << 3)) != 0;
        _bootChecksComplete = (flags & (1 << 4)) != 0;

        if (_audioSink is not null)
        {
            _audioSink.ResetTimeline(_masterCycles, GetCurrentAudioDacLevel());
        }
    }

    private void RestoreControllerContacts(int port, byte buttons)
    {
        for (var bit = 0; bit < 8; bit++)
        {
            _machine.SetControllerButton(port, (NesControllerButton)bit, (buttons & (1 << bit)) != 0);
        }
    }

    private byte GetCurrentAudioDacLevel() => _machine.ActiveMotherboard switch
    {
        ActiveNesMotherboard.Famicom => _machine.Famicom.Cpu.AudioDacOutput.CurrentValue.DacLevel,
        ActiveNesMotherboard.NtscNes => _machine.NtscNes.Cpu.AudioDacOutput.CurrentValue.DacLevel,
        ActiveNesMotherboard.PalNes => _machine.PalNes.Cpu.AudioDacOutput.CurrentValue.DacLevel,
        _ => 0
    };

    public VirtualNesBootDiagnostics RunUntil(
        Func<VirtualNesBootDiagnostics, bool> stop,
        int maximumMasterCycles)
    {
        ArgumentNullException.ThrowIfNull(stop);
        if (maximumMasterCycles < 1) throw new ArgumentOutOfRangeException(nameof(maximumMasterCycles));
        for (var index = 0; index < maximumMasterCycles; index++)
        {
            AdvanceMasterCycles(1);
            var snapshot = Snapshot();
            if (stop(snapshot)) return snapshot;
        }
        return Snapshot();
    }

    public VirtualNesBootDiagnostics Snapshot()
    {
        var (cpu, ppu) = GetActiveProcessors();
        var cartridge = _machine.Slot.Cartridge;
        var dataBusKnown = cpu.TryInspectDataBus(out var dataBusValue);
        if (!_bootChecksComplete)
        {
            if (!_resetVectorObserved) _resetVectorObserved = (cartridge?.CpuReadCount ?? 0) >= 2;
            if (!_firstOpcodeObserved) _firstOpcodeObserved = cpu.CompletedInstructionCount > 0;
            if (!_firstVblankObserved)
                _firstVblankObserved = ppu.Vblank || ppu.Frame > 0 || ppu.NmiFallingEdgeCount > 0;
            if (!_firstNmiObserved) _firstNmiObserved = ppu.NmiFallingEdgeCount > 0;
            _bootChecksComplete =
                _resetVectorObserved &&
                _firstOpcodeObserved &&
                _firstVblankObserved &&
                _firstNmiObserved;
        }
        return new VirtualNesBootDiagnostics(
            _machine.ActiveMotherboard,
            _machine.Slot.InsertedImage?.MapperNumber ?? -1,
            _machine.Slot.InsertedImage?.PrgRomSizeBytes ?? 0,
            _machine.Slot.InsertedImage?.ChrRomSizeBytes ?? 0,
            _masterCycles,
            cpu.CompletedInstructionCount,
            cpu.ProgramCounter,
            cpu.CurrentOpcode,
            cpu.IsHalted,
            cpu.IsHalted ? (ushort)(cpu.ProgramCounter - 1) : (ushort)0,
            cpu.CurrentCycleState,
            cpu.CurrentBusAddress,
            cpu.CurrentBusIsRead,
            dataBusKnown,
            dataBusValue,
            cpu.CurrentM2Level.ToString(),
            cartridge?.CpuReadCount ?? 0,
            cartridge?.LastCpuReadAddress ?? 0,
            cartridge?.LastCpuReadData ?? 0,
            cartridge?.PpuReadCount ?? 0,
            ppu.Frame,
            ppu.CompletedVramReadCount,
            ppu.NmiFallingEdgeCount,
            _resetVectorObserved,
            _firstOpcodeObserved,
            _firstVblankObserved,
            _firstNmiObserved);
    }

    private void AttachOutputPins()
    {
        _observedCpu?.AudioDacOutput.SetCaptureEnabled(false);
        _observedPpu?.VideoOutput.SetCaptureEnabled(false);

        (_observedCpu, _observedPpu) = GetActiveProcessors();
        _observedCpu.AudioDacOutput.SetCaptureEnabled(_audioSink is not null);
        _observedPpu.VideoOutput.SetCaptureEnabled(_videoSink is not null);

        if (_audioSink is not null)
        {
            _audioSink.AcceptLevelChange(
                _masterCycles,
                _observedCpu.AudioDacOutput.CurrentValue.DacLevel);
        }
    }

    private (Rp2A03 Cpu, Rp2C02 Ppu) GetActiveProcessors()
    {
        return _machine.ActiveMotherboard switch
        {
            ActiveNesMotherboard.Famicom => (_machine.Famicom.Cpu, _machine.Famicom.Ppu),
            ActiveNesMotherboard.NtscNes => (_machine.NtscNes.Cpu, _machine.NtscNes.Ppu),
            ActiveNesMotherboard.PalNes => throw new InvalidOperationException("Use PAL processor access through the PAL-specific diagnostics path."),
            _ => throw new InvalidOperationException("No motherboard is selected.")
        };
    }

    private void ResetDiagnostics()
    {
        _masterCycles = 0;
        _resetVectorObserved = false;
        _firstOpcodeObserved = false;
        _firstVblankObserved = false;
        _firstNmiObserved = false;
        _bootChecksComplete = false;
    }
}
