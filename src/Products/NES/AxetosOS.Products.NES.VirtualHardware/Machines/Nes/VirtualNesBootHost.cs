using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
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
    private ulong _lastCpuReadCount;
    private ulong _lastVideoFrame = ulong.MaxValue;
    private int _lastVideoScanline = int.MinValue;
    private int _lastVideoDot = int.MinValue;

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
        for (var index = 0; index < cycles; index++)
        {
            _machine.AdvanceMasterCycles(1);
            _masterCycles++;
            ObserveSelectedHardware();
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

    private void ObserveSelectedHardware()
    {
        var (cpu, ppu) = GetActiveProcessors();
        var cartridge = _machine.Slot.Cartridge;
        var cpuReads = cartridge?.CpuReadCount ?? 0;
        if (cpuReads > _lastCpuReadCount)
        {
            if (cpuReads >= 2) _resetVectorObserved = true;
            _lastCpuReadCount = cpuReads;
        }
        if (cpu.CompletedInstructionCount > 0) _firstOpcodeObserved = true;
        if (ppu.Vblank) _firstVblankObserved = true;
        if (ppu.NmiFallingEdgeCount > 0) _firstNmiObserved = true;

        if (ppu.Scanline is >= 0 and < 240 && ppu.Dot is >= 1 and <= 256 &&
            (ppu.Frame != _lastVideoFrame || ppu.Scanline != _lastVideoScanline || ppu.Dot != _lastVideoDot))
        {
            // The RP2C02 raster now advances once per four console master clocks.
            // ObserveSelectedHardware still runs every master clock, so suppress the
            // three repeated observations of the same physical output pixel.
            _lastVideoFrame = ppu.Frame;
            _lastVideoScanline = ppu.Scanline;
            _lastVideoDot = ppu.Dot;

            var x = ppu.Dot - 1;
            var color = ppu.OutputColorCode;
            VideoSink?.AcceptPixel(ppu.Frame, x, ppu.Scanline, color, ppu.ColorEmphasis);
            if (color != 0) _nonUniversalPixels++;
        }

        AudioSink?.AcceptSample(_masterCycles, cpu.AudioDacLevel);
        _audioSamples++;
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
        _nonUniversalPixels = 0;
        _audioSamples = 0;
        _resetVectorObserved = false;
        _firstOpcodeObserved = false;
        _firstVblankObserved = false;
        _firstNmiObserved = false;
        _lastCpuReadCount = 0;
        _lastVideoFrame = ulong.MaxValue;
        _lastVideoScanline = int.MinValue;
        _lastVideoDot = int.MinValue;
    }
}
