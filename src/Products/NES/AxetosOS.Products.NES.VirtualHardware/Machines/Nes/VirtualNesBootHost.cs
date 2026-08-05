using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;
using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
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
    ulong NonUniversalPixels,
    ulong AudioSamples,
    bool ResetVectorObserved,
    bool FirstOpcodeObserved,
    bool FirstVblankObserved,
    bool FirstNmiObserved);

public interface IVirtualNesVideoSink
{
    void AcceptPixel(ulong frame, int x, int y, byte colorCode, byte emphasis);
}

public interface IVirtualNesAudioSink
{
    void AcceptSample(ulong masterCycle, byte dacLevel);
}

/// <summary>
/// Optional host contract for sinks that consume samples at scheduled master
/// cycles. It avoids invoking a resampler on every virtual master tick.
/// </summary>
public interface IVirtualNesScheduledAudioSink : IVirtualNesAudioSink
{
    ulong NextRequiredMasterCycle { get; }
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
}

public sealed class VirtualNesPcmBuffer : IVirtualNesAudioSink
{
    private readonly List<byte> _samples = [];
    public IReadOnlyList<byte> Samples => _samples;
    public void AcceptSample(ulong masterCycle, byte dacLevel) => _samples.Add(dacLevel);
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
    private ulong _nonUniversalPixels;
    private ulong _audioSamples;
    private bool _resetVectorObserved;
    private bool _firstOpcodeObserved;
    private bool _firstVblankObserved;
    private bool _firstNmiObserved;
    private Rp2A03? _activeCpu;
    private Rp2C02? _activePpu;
    private NromCartridge? _activeCartridge;

    public RegionalNesVirtualMachine Machine => _machine;
    public IVirtualNesVideoSink? VideoSink { get; set; }
    public IVirtualNesAudioSink? AudioSink { get; set; }

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
        CacheSelectedHardware();
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
        EnsureSelectedHardwareCached();

        switch (_machine.ActiveMotherboard)
        {
            case ActiveNesMotherboard.Famicom:
                AdvanceFamicom(cycles);
                break;
            case ActiveNesMotherboard.NtscNes:
                AdvanceNtsc(cycles);
                break;
            case ActiveNesMotherboard.PalNes:
                _machine.PalNes.AdvanceMasterCycles(cycles);
                _masterCycles += (ulong)cycles;
                break;
            default:
                throw new InvalidOperationException("No motherboard is selected.");
        }
    }

    private void AdvanceFamicom(int cycles)
    {
        var board = _machine.Famicom;
        var cpu = _activeCpu!;
        var ppu = _activePpu!;
        var cartridge = _activeCartridge;
        for (var index = 0; index < cycles; index++)
        {
            board.AdvanceMasterHalfCycle();
            board.AdvanceMasterHalfCycle();
            _masterCycles++;
            ObserveSelectedHardware(cpu, ppu, cartridge);
        }
    }

    private void AdvanceNtsc(int cycles)
    {
        var board = _machine.NtscNes;
        var cpu = _activeCpu!;
        var ppu = _activePpu!;
        var cartridge = _activeCartridge;
        for (var index = 0; index < cycles; index++)
        {
            board.AdvanceMasterHalfCycle();
            board.AdvanceMasterHalfCycle();
            _masterCycles++;
            ObserveSelectedHardware(cpu, ppu, cartridge);
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
        EnsureSelectedHardwareCached();
        var cpu = _activeCpu!;
        var ppu = _activePpu!;
        var cartridge = _activeCartridge;
        var dataBusKnown = cpu.TryInspectDataBus(out var dataBusValue);
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
            _nonUniversalPixels,
            _audioSamples,
            _resetVectorObserved,
            _firstOpcodeObserved,
            _firstVblankObserved,
            _firstNmiObserved);
    }

    private void ObserveSelectedHardware(Rp2A03 cpu, Rp2C02 ppu, NromCartridge? cartridge)
    {
        if (!_resetVectorObserved)
        {
            var cpuReads = cartridge?.CpuReadCount ?? 0;
            if (cpuReads >= 2) _resetVectorObserved = true;
        }
        if (!_firstOpcodeObserved && cpu.CompletedInstructionCount > 0) _firstOpcodeObserved = true;
        if (!_firstVblankObserved && ppu.Vblank) _firstVblankObserved = true;
        if (!_firstNmiObserved && ppu.NmiFallingEdgeCount > 0) _firstNmiObserved = true;

        var videoSink = VideoSink;
        if (videoSink is not null && ppu.Scanline is >= 0 and < 240 && ppu.Dot is >= 1 and <= 256)
        {
            var color = ppu.OutputColorCode;
            videoSink.AcceptPixel(ppu.Frame, ppu.Dot - 1, ppu.Scanline, color, ppu.ColorEmphasis);
            if (color != 0) _nonUniversalPixels++;
        }

        var audioSink = AudioSink;
        if (audioSink is not null &&
            (audioSink is not IVirtualNesScheduledAudioSink scheduled ||
             _masterCycles >= scheduled.NextRequiredMasterCycle))
        {
            audioSink.AcceptSample(_masterCycles, cpu.AudioDacLevel);
            _audioSamples++;
        }
    }

    private void CacheSelectedHardware()
    {
        _activeCartridge = _machine.Slot.Cartridge;
        switch (_machine.ActiveMotherboard)
        {
            case ActiveNesMotherboard.Famicom:
                _activeCpu = _machine.Famicom.Cpu;
                _activePpu = _machine.Famicom.Ppu;
                break;
            case ActiveNesMotherboard.NtscNes:
                _activeCpu = _machine.NtscNes.Cpu;
                _activePpu = _machine.NtscNes.Ppu;
                break;
            case ActiveNesMotherboard.PalNes:
                _activeCpu = null;
                _activePpu = null;
                break;
            default:
                _activeCpu = null;
                _activePpu = null;
                break;
        }
    }

    private void EnsureSelectedHardwareCached()
    {
        if (_machine.ActiveMotherboard == ActiveNesMotherboard.PalNes) return;
        if (_activeCpu is null || _activePpu is null) CacheSelectedHardware();
    }

    private (Rp2A03 Cpu, Rp2C02 Ppu) GetActiveProcessors()
    {
        EnsureSelectedHardwareCached();
        return (_activeCpu ?? throw new InvalidOperationException("Use PAL processor access through the PAL-specific diagnostics path."),
            _activePpu ?? throw new InvalidOperationException("No motherboard is selected."));
    }

    private void ResetDiagnostics()
    {
        _masterCycles = 0;
        _nonUniversalPixels = 0;
        _audioSamples = 0;
        _resetVectorObserved = false;
        _firstOpcodeObserved = false;
        _firstVblankObserved = false;
        _firstNmiObserved = false;
    }
}
