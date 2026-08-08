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
var referenceRuntime = false;
var compiledLabRuntime = false;
var uncappedRuntime = false;
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

    if (args[index].Equals("--reference-runtime", StringComparison.OrdinalIgnoreCase))
    {
        referenceRuntime = true;
        continue;
    }

    if (args[index].Equals("--compiled-lab", StringComparison.OrdinalIgnoreCase))
    {
        compiledLabRuntime = true;
        continue;
    }

    if (args[index].Equals("--uncapped", StringComparison.OrdinalIgnoreCase))
    {
        uncappedRuntime = true;
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
        Console.Error.WriteLine("Usage: DesktopHost [rom-path] [--board famicom|ntsc|pal-a|pal-b|auto] [--profile] [--reference-runtime|--compiled-lab] [--uncapped] [--ppu-split-trace]");
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

if (referenceRuntime && compiledLabRuntime)
{
    Console.Error.WriteLine("--reference-runtime and --compiled-lab are mutually exclusive.");
    return 2;
}

var host = new VirtualNesBootHost();
if (compiledLabRuntime) host.Machine.SetCompiledLabExecutionEnabled(true);
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

var referenceRuntimeActive = referenceRuntime
    && host.Machine.ActiveMotherboard == ActiveNesMotherboard.Famicom;
var compiledLabRuntimeActive = compiledLabRuntime
    && host.Machine.ActiveMotherboard == ActiveNesMotherboard.Famicom;
if (referenceRuntimeActive)
    host.Machine.Famicom.SetCompiledPhysicalMachineEnabled(false);

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
var compiledFamicom = host.Machine.ActiveMotherboard == ActiveNesMotherboard.Famicom
    && host.Machine.Famicom.CompiledPhysicalMachineEnabled;
var compiledLab = host.Machine.ActiveMotherboard == ActiveNesMotherboard.Famicom
    && host.Machine.Famicom.CompiledLabMotherboardEnabled;
Console.WriteLine(compiledLab
    ? "Execution:   whole-circuit compiled motherboard + replaceable cartridge unit"
    : compiledFamicom
        ? "Execution:   startup-compiled fused physical Famicom/NROM machine"
        : "Execution:   physical virtual-hardware buses and master clock only");
Console.WriteLine("Video:       RP2C02 color output -> AxetosOS native framebuffer presenter");
Console.WriteLine($"Audio:       RP2A03 DAC output -> AxetosOS native PCM output ({AudioSampleRate:N0} Hz mono)");
Console.WriteLine("Controls:    Esc=Exit (controller hardware adapter is the next input milestone)");
Console.WriteLine(compiledLab
    ? $"Kernel:      lab whole-circuit compiler ({host.Machine.Famicom.CompiledLabRuntimeUnitCount} runtime units; {host.Machine.Famicom.CompiledLabInternalComponentCount} fixed-board components; {host.Machine.Famicom.CompiledLabFoldedInternalTraceCount} internal traces folded; {host.Machine.Famicom.CompiledLabBoundaryTraceCount} cartridge-boundary traces)"
    : compiledFamicom
        ? $"Kernel:      fused compiled circuit ({host.Machine.Famicom.CompiledRuntimeUnitCount} runtime unit; {host.Machine.Famicom.CompiledFoldedPhysicalTraceCount} fixed traces folded; no signal queue)"
        : "Kernel:      chip-owned pin-gated direct propagation (no signal queue)");
if (compiledLab)
{
    Console.WriteLine("Compiler:    component hardware facets + physical topology only; zero board/product semantics");
    Console.WriteLine("Boundary:    mapper + ROM remain replaceable external hardware");
}
if (referenceRuntimeActive) Console.WriteLine("Reference:   legacy per-trace runtime forced for A/B comparison");
var realTimePacing = !uncappedRuntime && !profileSimulation;
Console.WriteLine(realTimePacing
    ? $"Timing:      hardware real-time ({masterClockHz:N0} Hz master clock)"
    : "Timing:      uncapped host throughput");
if (profileSimulation) Console.WriteLine("Profiler:    enabled; component/net/internal-IC timing sampled 1/256; host timing exact; results every 5 seconds");
if (ppuSplitTrace) Console.WriteLine("PPU trace:   sprite-zero and $2002/$2005/$2006 split-screen events enabled");

const int MasterCyclesPerVideoBatch = 16_384;
const int AudioTransferBufferSize = 4_096;
var audioTransfer = new float[AudioTransferBufferSize];
var timer = Stopwatch.StartNew();
ulong pacedMasterCycles = 0;
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
long profileEventPumpTicks = 0;
long profileSimulationTicks = 0;
long profileVideoPresentationTicks = 0;
long profileAudioTransferTicks = 0;
long profileDiagnosticsTicks = 0;
ulong profileSimulationBatches = 0;
ulong profilePresentedFrames = 0;
ulong profileAudioSamples = 0;

while (presenter.IsOpen)
{
    var profileStarted = profileSimulation ? Stopwatch.GetTimestamp() : 0;
    presenter.PumpEvents();
    if (profileSimulation) profileEventPumpTicks += Stopwatch.GetTimestamp() - profileStarted;
    if (!presenter.IsOpen) break;

    profileStarted = profileSimulation ? Stopwatch.GetTimestamp() : 0;
    host.AdvanceMasterCycles(MasterCyclesPerVideoBatch);
    pacedMasterCycles += MasterCyclesPerVideoBatch;
    if (profileSimulation)
    {
        profileSimulationTicks += Stopwatch.GetTimestamp() - profileStarted;
        profileSimulationBatches++;
    }

    tracedPpu?.SplitTraceOutput.Drain(trace => Console.WriteLine(
        $"PPU SPLIT: frame={trace.Frame:N0}; scanline={trace.Scanline}; dot={trace.Dot}; " +
        $"op={trace.Operation}; value=${trace.Value:X2}; v=${trace.VramAddress:X4}; " +
        $"t=${trace.TemporaryAddress:X4}; x={trace.FineX}; w={(trace.WriteToggle ? 1 : 0)}"));

    if (videoSink.CompletedFrame != lastPresentedFrame)
    {
        profileStarted = profileSimulation ? Stopwatch.GetTimestamp() : 0;
        videoSink.CompletedPixels.Span.CopyTo(surface.PixelSpan);
        presenter.Present(surface, ScalingMode.IntegerNearest);
        if (profileSimulation)
        {
            profileVideoPresentationTicks += Stopwatch.GetTimestamp() - profileStarted;
            profilePresentedFrames++;
        }
        lastPresentedFrame = videoSink.CompletedFrame;
    }

    profileStarted = profileSimulation ? Stopwatch.GetTimestamp() : 0;
    int drained;
    while ((drained = audioSink.Drain(audioTransfer)) > 0)
    {
        audio.Submit(audioTransfer.AsSpan(0, drained));
        if (profileSimulation) profileAudioSamples += (ulong)drained;
    }
    if (profileSimulation) profileAudioTransferTicks += Stopwatch.GetTimestamp() - profileStarted;

    if (realTimePacing)
        PaceToMasterClock(timer, pacedMasterCycles, masterClockHz);

    var now = timer.Elapsed;
    if (now - lastTitleUpdate >= TimeSpan.FromMilliseconds(500))
    {
        var diagnosticsStarted = profileSimulation ? Stopwatch.GetTimestamp() : 0;
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
        if (profileSimulation) profileDiagnosticsTicks += Stopwatch.GetTimestamp() - diagnosticsStarted;
    }

    if (profileSimulation && now - lastProfileReport >= TimeSpan.FromSeconds(5))
    {
        PrintHostProfile(
            now,
            profileEventPumpTicks,
            profileSimulationTicks,
            profileVideoPresentationTicks,
            profileAudioTransferTicks,
            profileDiagnosticsTicks,
            profileSimulationBatches,
            profilePresentedFrames,
            profileAudioSamples);
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
var finalCpu = host.Machine.ActiveMotherboard switch
{
    ActiveNesMotherboard.Famicom => host.Machine.Famicom.Cpu,
    ActiveNesMotherboard.NtscNes => host.Machine.NtscNes.Cpu,
    _ => null
};
if (finalCpu is not null)
{
    Console.WriteLine(
        $"Audio core:  apu-cycles={finalCpu.ApuCpuCycleCount:N0}, dac-events={finalCpu.AudioDacOutput.DriveCount:N0}, " +
        $"dac-level={finalCpu.AudioDacLevel}");
}
if (profileSimulation)
{
    PrintHostProfile(
        timer.Elapsed,
        profileEventPumpTicks,
        profileSimulationTicks,
        profileVideoPresentationTicks,
        profileAudioTransferTicks,
        profileDiagnosticsTicks,
        profileSimulationBatches,
        profilePresentedFrames,
        profileAudioSamples);
    PrintPerformanceCounters(activeSimulator.GetPerformanceCounters(), final.MasterCycles, final.CpuInstructions, final.PpuFrames);
    PrintProfile(activeSimulator.GetProfileSnapshot());
}
return 0;

static void PaceToMasterClock(Stopwatch timer, ulong masterCycles, double masterClockHz)
{
    var targetTicks = masterCycles * (double)Stopwatch.Frequency / masterClockHz;
    var remainingTicks = targetTicks - timer.ElapsedTicks;
    if (remainingTicks <= 0) return;

    // Keep at most roughly one short simulation batch of headroom. Avoid a
    // continuous sub-millisecond spin loop: Windows audio/video presentation
    // benefits more from low host load than from nanosecond-level pacing.
    var remainingMilliseconds = remainingTicks * 1000.0 / Stopwatch.Frequency;
    if (remainingMilliseconds >= 1.5)
        Thread.Sleep(Math.Max(1, (int)Math.Floor(remainingMilliseconds)));
}

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

static void PrintHostProfile(
    TimeSpan wallTime,
    long eventPumpTicks,
    long simulationTicks,
    long videoTicks,
    long audioTicks,
    long diagnosticsTicks,
    ulong simulationBatches,
    ulong presentedFrames,
    ulong audioSamples)
{
    static double Seconds(long ticks) => (double)ticks / Stopwatch.Frequency;
    static double Percent(long ticks, TimeSpan wall) => wall.TotalSeconds <= 0
        ? 0
        : Seconds(ticks) * 100.0 / wall.TotalSeconds;

    Console.WriteLine($"PROFILE HOST: wall={wallTime.TotalSeconds:F2}s; batches={simulationBatches:N0}; presented-frames={presentedFrames:N0}; audio-samples={audioSamples:N0}");
    Console.WriteLine($"PROFILE HOST SECTION: simulation={Seconds(simulationTicks):F2}s ({Percent(simulationTicks, wallTime):F1}% wall)");
    Console.WriteLine($"PROFILE HOST SECTION: video-present={Seconds(videoTicks):F2}s ({Percent(videoTicks, wallTime):F1}% wall)");
    Console.WriteLine($"PROFILE HOST SECTION: audio-transfer={Seconds(audioTicks):F2}s ({Percent(audioTicks, wallTime):F1}% wall)");
    Console.WriteLine($"PROFILE HOST SECTION: event-pump={Seconds(eventPumpTicks):F2}s ({Percent(eventPumpTicks, wallTime):F1}% wall)");
    Console.WriteLine($"PROFILE HOST SECTION: diagnostics/title={Seconds(diagnosticsTicks):F2}s ({Percent(diagnosticsTicks, wallTime):F1}% wall)");
}

static void PrintProfile(AxetosOS.Products.NES.VirtualHardware.Simulation.VirtualHardwareSimulationProfile profile)
{
    var componentTotal = profile.Components.Sum(static item => item.EvaluationTime.TotalSeconds);
    Console.WriteLine(
        $"PROFILE: board={profile.BoardId}; direct-events={profile.PropagationEvents:N0}; " +
        $"net-transport={profile.NetResolutionTime.TotalSeconds:F2}s from {profile.TimedNetResolutionSamples:N0}/{profile.NetResolutionAttempts:N0} samples; " +
        $"estimated-component-total={componentTotal:F2}s");

    foreach (var component in profile.Components
        .Where(static item => item.EvaluationCount != 0)
        .OrderByDescending(static item => item.EvaluationTime)
        .Take(12))
    {
        var share = componentTotal <= 0 ? 0 : component.EvaluationTime.TotalSeconds * 100.0 / componentTotal;
        Console.WriteLine(
            $"PROFILE COMPONENT: {component.ComponentId}; evaluations={component.EvaluationCount:N0}; " +
            $"samples={component.TimedEvaluationCount:N0}; estimated={component.EvaluationTime.TotalSeconds:F2}s; component-share={share:F1}%");
    }

    foreach (var section in profile.Sections
        .OrderByDescending(static item => item.EstimatedTime)
        .Take(16))
    {
        Console.WriteLine(
            $"PROFILE IC SECTION: {section.ComponentId}.{section.Section}; samples={section.SampleCount:N0}; estimated={section.EstimatedTime.TotalSeconds:F2}s");
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
