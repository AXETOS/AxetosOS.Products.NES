using System.Runtime.CompilerServices;
using System.Diagnostics;
using AxetosOS.Audio.Windows;
using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Machines.Nes;
using AxetosOS.Rendering.Abstractions;
using AxetosOS.Rendering.Windows;

const int ScreenWidth = 256;
const int ScreenHeight = 240;
const int AudioSampleRate = 44_100;

string? romArgument = null;
var boardSelection = NesRegionSelection.NtscJapan;
var palCic = PalCicVariant.PalA3195;
var profileSimulation = false;
var ppuSplitTrace = false;
for (var index = 0; index < args.Length; index++)
{
    if (args[index].Equals("--ppu-split-trace", StringComparison.OrdinalIgnoreCase))
    {
        ppuSplitTrace = true;
        continue;
    }

    if (args[index].Equals("--profile", StringComparison.OrdinalIgnoreCase))
    {
        profileSimulation = true;
        continue;
    }

    if (args[index].Equals("--board", StringComparison.OrdinalIgnoreCase))
    {
        if (++index >= args.Length || !TryParseBoard(args[index], out boardSelection, out palCic))
        {
            Console.Error.WriteLine("Board must be famicom, ntsc, pal-a, pal-b or auto.");
            return 2;
        }
        continue;
    }

    if (romArgument is not null)
    {
        Console.Error.WriteLine("Usage: DesktopHost [rom-path] [--board famicom|ntsc|pal-a|pal-b|auto] [--profile] [--ppu-split-trace]");
        return 2;
    }

    romArgument = args[index];
}

var selectedRomPath = romArgument ?? NativeFileDialog.OpenFile(
    "Open NES cartridge image",
    "NES cartridge images (*.nes)|*.nes|All files (*.*)|*.*",
    defaultExtension: "nes");

if (string.IsNullOrWhiteSpace(selectedRomPath)) return 0;
var romPath = Path.GetFullPath(selectedRomPath);
if (!File.Exists(romPath))
{
    Console.Error.WriteLine($"ROM file not found: {romPath}");
    return 3;
}

var host = new VirtualNesBootHost();
VirtualHardwareNesRomImage image;
try
{
    image = host.LoadRom(romPath, boardSelection, palCic);
}
catch (NotSupportedException exception)
{
    NativeMessageDialog.ShowError("Unsupported virtual cartridge hardware", exception.Message);
    Console.Error.WriteLine(exception.Message);
    return 4;
}

if (host.Machine.ActiveMotherboard == ActiveNesMotherboard.PalNes)
{
    Console.Error.WriteLine("PAL desktop presentation will be enabled after the PAL boot diagnostics path is exposed.");
    return 5;
}

using var presenter = new Win32FramePresenter(
    $"AxetosOS Virtual NES — {Path.GetFileNameWithoutExtension(romPath)}",
    ScreenWidth * 3,
    ScreenHeight * 3);
var surface = new FrameSurface(ScreenWidth, ScreenHeight);
var videoSink = new NativeFrameVideoSink();
host.VideoSink = videoSink;

var masterClockHz = host.Machine.ActiveMotherboard switch
{
    ActiveNesMotherboard.Famicom or ActiveNesMotherboard.NtscNes => 21_477_272.0,
    _ => 21_477_272.0
};
using var audio = new Win32WaveOutAudioSink(AudioSampleRate);
var audioSink = new NativePcmAudioSink(masterClockHz, AudioSampleRate);
host.AudioSink = audioSink;
audio.Start();

presenter.KeyStateChanged += (key, pressed) =>
{
    if (key == NativeKey.Escape && pressed) presenter.Close();
};

host.PowerAndReleaseReset();
var activeSimulator = host.Machine.ActiveMotherboard switch
{
    ActiveNesMotherboard.Famicom => host.Machine.Famicom.Simulator,
    ActiveNesMotherboard.NtscNes => host.Machine.NtscNes.Simulator,
    ActiveNesMotherboard.PalNes => host.Machine.PalNes.Simulator,
    _ => throw new InvalidOperationException("No active motherboard simulator.")
};
if (profileSimulation) activeSimulator.SetProfilingEnabled(true);
Rp2C02? tracedPpu = null;
if (ppuSplitTrace)
{
    tracedPpu = host.Machine.ActiveMotherboard switch
    {
        ActiveNesMotherboard.Famicom => host.Machine.Famicom.Ppu,
        ActiveNesMotherboard.NtscNes => host.Machine.NtscNes.Ppu,
        _ => null
    };
    tracedPpu?.SplitTraceOutput.SetCaptureEnabled(true);
}
var initial = host.Snapshot();
Console.WriteLine("AxetosOS Products / NES — virtual hardware desktop host");
Console.WriteLine($"ROM:        {romPath}");
Console.WriteLine($"Mapper:     {image.MapperNumber}");
Console.WriteLine($"Board:      {initial.Motherboard}");
Console.WriteLine($"PRG ROM:    {image.PrgRomSizeBytes:N0} bytes");
Console.WriteLine($"CHR:        {(image.ChrRomSizeBytes == 0 ? "8 KiB CHR RAM" : $"{image.ChrRomSizeBytes:N0} bytes ROM")}");
Console.WriteLine("Execution:   physical virtual-hardware buses and master clock only");
Console.WriteLine("Video:       RP2C02 color output -> AxetosOS native framebuffer presenter");
Console.WriteLine($"Audio:       RP2A03 DAC output -> AxetosOS native PCM output ({AudioSampleRate:N0} Hz mono)");
Console.WriteLine("Controls:    Esc=Exit (controller hardware adapter is the next input milestone)");
Console.WriteLine("Kernel:      chip-owned activation direct propagation (no signal queue)");
if (profileSimulation) Console.WriteLine("Profiler:    enabled; component timing sampled 1/256; results every 5 seconds");
if (ppuSplitTrace) Console.WriteLine("PPU trace:   sprite-zero and $2002/$2005/$2006 split-screen events enabled");

const int MasterCyclesPerVideoBatch = 16_384;
const int AudioTransferBufferSize = 4_096;
var audioTransfer = new float[AudioTransferBufferSize];
var timer = Stopwatch.StartNew();
var lastPresentedFrame = ulong.MaxValue;
var lastTitleUpdate = TimeSpan.Zero;
var lastFpsSampleTime = TimeSpan.Zero;
var lastFpsSampleFrame = initial.PpuFrames;
var displayedFps = 0.0;
var minimumFps = double.PositiveInfinity;
var maximumFps = 0.0;
var averageFps = 0.0;
var fpsStatisticsStarted = false;
var fpsStatisticsStartTime = TimeSpan.Zero;
var fpsStatisticsStartFrame = initial.PpuFrames;
var haltReported = false;
var lastInstructionProgress = initial.CpuInstructions;
var lastInstructionProgressFrame = initial.PpuFrames;
var stallReportedForState = string.Empty;
var lastProfileReport = TimeSpan.Zero;

while (presenter.IsOpen)
{
    presenter.PumpEvents();
    if (!presenter.IsOpen) break;

    host.AdvanceMasterCycles(MasterCyclesPerVideoBatch);

    tracedPpu?.SplitTraceOutput.Drain(trace => Console.WriteLine(
        $"PPU SPLIT: frame={trace.Frame:N0}; scanline={trace.Scanline}; dot={trace.Dot}; " +
        $"op={trace.Operation}; value=${trace.Value:X2}; v=${trace.VramAddress:X4}; " +
        $"t=${trace.TemporaryAddress:X4}; x={trace.FineX}; w={(trace.WriteToggle ? 1 : 0)}"));

    if (videoSink.CompletedFrame != lastPresentedFrame)
    {
        videoSink.CompletedPixels.Span.CopyTo(surface.PixelSpan);
        presenter.Present(surface, ScalingMode.IntegerNearest);
        lastPresentedFrame = videoSink.CompletedFrame;
    }

    int drained;
    while ((drained = audioSink.Drain(audioTransfer)) > 0)
        audio.Submit(audioTransfer.AsSpan(0, drained));

    var now = timer.Elapsed;
    if (now - lastTitleUpdate >= TimeSpan.FromMilliseconds(500))
    {
        var diagnostics = host.Snapshot();
        if (!fpsStatisticsStarted && now >= TimeSpan.FromSeconds(2))
        {
            fpsStatisticsStarted = true;
            fpsStatisticsStartTime = now;
            fpsStatisticsStartFrame = diagnostics.PpuFrames;
            lastFpsSampleTime = now;
            lastFpsSampleFrame = diagnostics.PpuFrames;
        }

        var fpsElapsed = now - lastFpsSampleTime;
        if (fpsStatisticsStarted && fpsElapsed >= TimeSpan.FromSeconds(1))
        {
            var completedFrames = diagnostics.PpuFrames - lastFpsSampleFrame;
            displayedFps = completedFrames / fpsElapsed.TotalSeconds;
            minimumFps = Math.Min(minimumFps, displayedFps);
            maximumFps = Math.Max(maximumFps, displayedFps);
            var statisticsElapsed = now - fpsStatisticsStartTime;
            averageFps = statisticsElapsed.TotalSeconds > 0
                ? (diagnostics.PpuFrames - fpsStatisticsStartFrame) / statisticsElapsed.TotalSeconds
                : 0.0;
            lastFpsSampleFrame = diagnostics.PpuFrames;
            lastFpsSampleTime = now;
        }

        if (diagnostics.CpuInstructions != lastInstructionProgress)
        {
            lastInstructionProgress = diagnostics.CpuInstructions;
            lastInstructionProgressFrame = diagnostics.PpuFrames;
            stallReportedForState = string.Empty;
        }

        var stalled = !diagnostics.CpuHalted &&
            diagnostics.PpuFrames >= lastInstructionProgressFrame + 2;
        var dataText = diagnostics.CpuDataBusKnown ? $"${diagnostics.CpuDataBusValue:X2}" : "??";
        var waitText = stalled
            ? $" | WAIT {diagnostics.CpuCycleState} A${diagnostics.CpuBusAddress:X4} D{dataText} M2={diagnostics.CpuM2Level}"
            : string.Empty;

        presenter.SetTitle(
            $"{Path.GetFileNameWithoutExtension(romPath)} | {diagnostics.Motherboard} | " +
            $"FPS C {displayedFps:F1} | Min {(double.IsPositiveInfinity(minimumFps) ? 0.0 : minimumFps):F1} | " +
            $"Max {maximumFps:F1} | Avg {averageFps:F1} ({averageFps / 60.0988 * 100.0:F0}%) | " +
            $"Frame {diagnostics.PpuFrames:N0} | PC ${diagnostics.ProgramCounter:X4} | " +
            $"OP ${diagnostics.CurrentOpcode:X2}{(diagnostics.CpuHalted ? " HALT" : string.Empty)} | " +
            $"CPU {diagnostics.CpuInstructions:N0}{waitText} | Audio {audio.BufferedMilliseconds:F0} ms");

        if (stalled)
        {
            var stallKey = $"{diagnostics.CpuCycleState}:{diagnostics.CpuBusAddress:X4}:{dataText}:{diagnostics.CpuM2Level}";
            if (!string.Equals(stallReportedForState, stallKey, StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"CPU STALL: state={diagnostics.CpuCycleState}; PC=${diagnostics.ProgramCounter:X4}; " +
                    $"opcode=${diagnostics.CurrentOpcode:X2}; bus={(diagnostics.CpuBusIsRead ? "READ" : "WRITE")} " +
                    $"${diagnostics.CpuBusAddress:X4}; data={dataText}; M2={diagnostics.CpuM2Level}; " +
                    $"last cartridge read=${diagnostics.LastCartridgeCpuReadAddress:X4} -> ${diagnostics.LastCartridgeCpuReadData:X2}; " +
                    $"instructions={diagnostics.CpuInstructions:N0}; frame={diagnostics.PpuFrames:N0}");
                stallReportedForState = stallKey;
            }
        }

        if (diagnostics.CpuHalted && !haltReported)
        {
            Console.Error.WriteLine(
                $"CPU HALT: opcode=${diagnostics.CurrentOpcode:X2} at ${diagnostics.HaltOpcodeAddress:X4}; " +
                $"last cartridge read=${diagnostics.LastCartridgeCpuReadAddress:X4} -> ${diagnostics.LastCartridgeCpuReadData:X2}; " +
                $"instructions={diagnostics.CpuInstructions:N0}, frame={diagnostics.PpuFrames:N0}");
            haltReported = true;
        }
        lastTitleUpdate = now;
    }

    if (profileSimulation && now - lastProfileReport >= TimeSpan.FromSeconds(5))
    {
        PrintProfile(activeSimulator.GetProfileSnapshot());
        lastProfileReport = now;
    }

    if (audio.BufferedMilliseconds > 120) Thread.Sleep(1);
}

var final = host.Snapshot();
var finalAverageFps = fpsStatisticsStarted && timer.Elapsed > fpsStatisticsStartTime
    ? (final.PpuFrames - fpsStatisticsStartFrame) / (timer.Elapsed - fpsStatisticsStartTime).TotalSeconds
    : timer.Elapsed.TotalSeconds > 0 ? final.PpuFrames / timer.Elapsed.TotalSeconds : 0.0;
Console.WriteLine(
    $"Stopped:     master={final.MasterCycles:N0}, instructions={final.CpuInstructions:N0}, frames={final.PpuFrames:N0}, " +
    $"current={displayedFps:F2}, min={(double.IsPositiveInfinity(minimumFps) ? 0.0 : minimumFps):F2}, " +
    $"max={maximumFps:F2}, average={finalAverageFps:F2} FPS");
Console.WriteLine($"Boot checks: reset-vector={final.ResetVectorObserved}, opcode={final.FirstOpcodeObserved}, vblank={final.FirstVblankObserved}, nmi={final.FirstNmiObserved}");
if (profileSimulation)
{
    PrintPerformanceCounters(activeSimulator.GetPerformanceCounters(), final.MasterCycles, final.CpuInstructions, final.PpuFrames);
    PrintProfile(activeSimulator.GetProfileSnapshot());
}
return 0;

static void PrintPerformanceCounters(
    AxetosOS.Products.NES.VirtualHardware.Simulation.VirtualHardwarePerformanceCounters counters,
    ulong masterCycles,
    ulong cpuInstructions,
    ulong ppuFrames)
{
    var ppuDots = masterCycles / 4;
    var changedNetPercent = counters.NetResolutionAttempts == 0
        ? 0.0
        : counters.NetLevelChanges * 100.0 / counters.NetResolutionAttempts;

    Console.WriteLine("Profile counters:");
    Console.WriteLine($"  clocks: master={masterCycles:N0}; ppu-dots={ppuDots:N0}; cpu-instructions={cpuInstructions:N0}; frames={ppuFrames:N0}");
    Console.WriteLine($"  kernel: compatibility-settles={counters.SettleCalls:N0}; direct-events={counters.StrictEvents:N0}; topology-compiles={counters.TopologyCompilations:N0}; clock-edges={counters.CompiledClockSourceDispatches:N0}");
    Console.WriteLine($"  components: evaluations={counters.ComponentEvaluations:N0}");
    Console.WriteLine($"  nets: resolutions={counters.NetResolutionAttempts:N0}; level-changes={counters.NetLevelChanges:N0} ({changedNetPercent:F1}%); pin-deliveries={counters.PinSampleDeliveries:N0}; receiver-deliveries={counters.ReceiverDeliveries:N0}");
}

static void PrintProfile(AxetosOS.Products.NES.VirtualHardware.Simulation.VirtualHardwareSimulationProfile profile)
{
    Console.WriteLine($"PROFILE: board={profile.BoardId}; compatibility-settles={profile.SettleCalls:N0}; direct-events={profile.PropagationEvents:N0}; component timing sampled below");
    foreach (var component in profile.Components.OrderByDescending(static item => item.EvaluationTime).Take(8))
    {
        Console.WriteLine($"PROFILE COMPONENT: {component.ComponentId}; evaluations={component.EvaluationCount:N0}; time={component.EvaluationTime.TotalSeconds:F2}s");
    }
}

static bool TryParseBoard(string value, out NesRegionSelection selection, out PalCicVariant palCic)
{
    palCic = PalCicVariant.PalA3195;
    selection = value.ToLowerInvariant() switch
    {
        "auto" => NesRegionSelection.Auto,
        "famicom" or "japan" => NesRegionSelection.NtscJapan,
        "ntsc" or "usa" => NesRegionSelection.NtscNorthAmerica,
        "pal-a" => NesRegionSelection.Pal,
        "pal-b" => NesRegionSelection.Pal,
        _ => (NesRegionSelection)(-1)
    };
    if (value.Equals("pal-b", StringComparison.OrdinalIgnoreCase)) palCic = PalCicVariant.PalB3197;
    return Enum.IsDefined(selection);
}

sealed class NativeFrameVideoSink : IVirtualNesVideoSink
{
    private const int Width = 256;
    private const int Height = 240;
    private static readonly uint[] Palette = BuildPalette();
    private static readonly uint[] EmphasizedPalette = BuildEmphasizedPalette();
    private uint[] _renderPixels = new uint[Width * Height];
    private uint[] _completedPixels = new uint[Width * Height];

    /// <summary>
    /// Immutable-for-the-current-frame presentation surface. The PPU always
    /// writes into a separate render buffer, and the two buffers swap only
    /// after pixel 255 of scanline 239 has been received.
    /// </summary>
    public ReadOnlyMemory<uint> CompletedPixels => _completedPixels;
    public ulong CompletedFrame { get; private set; } = ulong.MaxValue;

    public void AcceptPixel(ulong frame, int x, int y, byte colorCode, byte emphasis)
    {
        if ((uint)x >= Width || (uint)y >= Height) return;
        _renderPixels[(y * Width) + x] = EmphasizedPalette[((emphasis & 0x07) << 6) | (colorCode & 0x3F)];
        PublishFrameIfComplete(frame, x, y);
    }

    public void AcceptPixels(ReadOnlySpan<RicohVideoPixelSample> samples)
    {
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = samples[index];
            if ((uint)sample.X >= Width || (uint)sample.Y >= Height) continue;
            _renderPixels[(sample.Y * Width) + sample.X] =
                EmphasizedPalette[((sample.Emphasis & 0x07) << 6) | (sample.ColorCode & 0x3F)];
            PublishFrameIfComplete(sample.Frame, sample.X, sample.Y);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PublishFrameIfComplete(ulong frame, int x, int y)
    {
        if (x != Width - 1 || y != Height - 1 || frame == CompletedFrame) return;

        // Publish a complete NES frame atomically. The frame guard also makes
        // this robust against duplicate observations of the final PPU pixel.
        (_renderPixels, _completedPixels) = (_completedPixels, _renderPixels);
        CompletedFrame = frame;
    }

    private static uint[] BuildPalette()
    {
        // Presentation-only RGB approximation. Rendering decisions remain in the RP2C02.
        int[] rgb =
        [
            0x666666,0x002A88,0x1412A7,0x3B00A4,0x5C007E,0x6E0040,0x6C0600,0x561D00,
            0x333500,0x0B4800,0x005200,0x004F08,0x00404D,0x000000,0x000000,0x000000,
            0xADADAD,0x155FD9,0x4240FF,0x7527FE,0xA01ACC,0xB71E7B,0xB53120,0x994E00,
            0x6B6D00,0x388700,0x0C9300,0x008F32,0x007C8D,0x000000,0x000000,0x000000,
            0xFFFEFF,0x64B0FF,0x9290FF,0xC676FF,0xF36AFF,0xFE6ECC,0xFE8170,0xEA9E22,
            0xBCBE00,0x88D800,0x5CE430,0x45E082,0x48CDDE,0x4F4F4F,0x000000,0x000000,
            0xFFFEFF,0xC0DFFF,0xD3D2FF,0xE8C8FF,0xFBC2FF,0xFEC4EA,0xFECCC5,0xF7D8A5,
            0xE4E594,0xCFEF96,0xBDF4AB,0xB3F3CC,0xB5EBF2,0xB8B8B8,0x000000,0x000000
        ];
        return rgb.Select(value => 0xFF000000u | (uint)value).ToArray();
    }

    private static uint[] BuildEmphasizedPalette()
    {
        var values = new uint[8 * 64];
        for (var emphasis = 0; emphasis < 8; emphasis++)
        {
            for (var color = 0; color < 64; color++)
                values[(emphasis << 6) | color] = ApplyEmphasis(Palette[color], (byte)emphasis);
        }
        return values;
    }

    private static uint ApplyEmphasis(uint argb, byte emphasis)
    {
        if ((emphasis & 0x07) == 0) return argb;
        var r = (int)((argb >> 16) & 0xFF);
        var g = (int)((argb >> 8) & 0xFF);
        var b = (int)(argb & 0xFF);
        if ((emphasis & 0x01) != 0) { g = g * 3 / 4; b = b * 3 / 4; }
        if ((emphasis & 0x02) != 0) { r = r * 3 / 4; b = b * 3 / 4; }
        if ((emphasis & 0x04) != 0) { r = r * 3 / 4; g = g * 3 / 4; }
        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }
}

sealed class NativePcmAudioSink(double masterClockHz, int sampleRate) : IVirtualNesAudioSink
{
    private readonly Queue<float> _samples = new();
    private readonly double _masterCyclesPerSample = masterClockHz / sampleRate;
    private double _nextSampleCycle;
    private byte _currentDacLevel;

    public void AcceptLevelChange(ulong masterCycle, byte dacLevel)
    {
        FillBefore(masterCycle);
        _currentDacLevel = dacLevel;
    }

    public void AcceptLevelChanges(ReadOnlySpan<RicohAudioDacSample> samples)
    {
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = samples[index];
            FillBefore(sample.MasterClock);
            _currentDacLevel = sample.DacLevel;
        }
    }

    public void CompleteThrough(ulong masterCycle, byte dacLevel)
    {
        _currentDacLevel = dacLevel;
        while (_nextSampleCycle <= masterCycle)
        {
            EnqueueCurrentLevel();
            _nextSampleCycle += _masterCyclesPerSample;
        }
    }

    public int Drain(float[] destination)
    {
        var count = Math.Min(destination.Length, _samples.Count);
        for (var index = 0; index < count; index++) destination[index] = _samples.Dequeue();
        return count;
    }

    private void FillBefore(ulong masterCycle)
    {
        while (_nextSampleCycle < masterCycle)
        {
            EnqueueCurrentLevel();
            _nextSampleCycle += _masterCyclesPerSample;
        }
    }

    private void EnqueueCurrentLevel()
    {
        var normalized = (_currentDacLevel / 127.5f) - 1.0f;
        _samples.Enqueue(Math.Clamp(normalized, -1.0f, 1.0f));
    }
}
