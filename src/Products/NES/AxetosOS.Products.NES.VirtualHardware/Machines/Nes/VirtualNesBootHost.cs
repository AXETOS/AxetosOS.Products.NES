using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;
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
    private readonly RegionalNesVirtualMachine _machine = new();
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
        _machine.InsertRom(image, sourceName, regionSelection, palCicVariant);
        AttachOutputPins();
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
