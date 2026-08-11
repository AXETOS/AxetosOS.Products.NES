using System.Runtime.CompilerServices;
using System.Diagnostics;
using AxetosOS.Audio.Windows;
using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;
using AxetosOS.Products.NES.VirtualHardware.Components.Nes;
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
(ulong Start, ulong End)? cartridgeVideoTrace = null;
var rawHardwareRuntime = false;
var compiledLabRuntime = false;
var uncappedRuntime = false;
ulong? stopFrame = null;
for (var index = 0; index < args.Length; index++)
{
    if (args[index].Equals("--ppu-split-trace", StringComparison.OrdinalIgnoreCase))
    {
        ppuSplitTrace = true;
        continue;
    }

    if (args[index].Equals("--cartridge-video-trace", StringComparison.OrdinalIgnoreCase))
    {
        if (++index >= args.Length || !TryParseFrameRange(args[index], out var traceStart, out var traceEnd))
        {
            Console.Error.WriteLine("--cartridge-video-trace requires START:END PPU frames, for example 1450:1850.");
            return 2;
        }
        cartridgeVideoTrace = (traceStart, traceEnd);
        continue;
    }

    if (args[index].Equals("--profile", StringComparison.OrdinalIgnoreCase))
    {
        profileSimulation = true;
        continue;
    }

    if (args[index].Equals("--raw-hardware", StringComparison.OrdinalIgnoreCase)
        || args[index].Equals("--reference-runtime", StringComparison.OrdinalIgnoreCase))
    {
        rawHardwareRuntime = true;
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

    if (args[index].Equals("--stop-frame", StringComparison.OrdinalIgnoreCase))
    {
        if (++index >= args.Length || !ulong.TryParse(args[index], out var parsedStopFrame) || parsedStopFrame == 0)
        {
            Console.Error.WriteLine("--stop-frame requires a positive PPU frame number.");
            return 2;
        }
        stopFrame = parsedStopFrame;
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
        Console.Error.WriteLine("Usage: DesktopHost [rom-path] [--board famicom|ntsc|pal-a|pal-b|auto] [--profile] [--raw-hardware|--reference-runtime|--compiled-lab] [--uncapped] [--stop-frame N] [--ppu-split-trace] [--cartridge-video-trace START:END]");
        return 2;
    }

    romArgument = args[index];
}

if (cartridgeVideoTrace is { } requestedTrace && !stopFrame.HasValue && requestedTrace.End < ulong.MaxValue)
    stopFrame = requestedTrace.End + 1;

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

if (rawHardwareRuntime && compiledLabRuntime)
{
    Console.Error.WriteLine("--raw-hardware/--reference-runtime and --compiled-lab are mutually exclusive.");
    return 2;
}

using var presenter = new Win32FramePresenter(
    $"AxetosOS Virtual NES — Loading {Path.GetFileName(romPath)}",
    ScreenWidth * 3,
    ScreenHeight * 3);
var surface = new FrameSurface(ScreenWidth, ScreenHeight);
VirtualNesBootHost? controllerHost = null;

presenter.KeyStateChanged += (key, pressed) =>
{
    if (key == NativeKey.Escape)
    {
        if (pressed) presenter.Close();
        return;
    }

    if (controllerHost is not null && TryMapController1Key(key, out var button))
        controllerHost.Machine.SetControllerButton(0, button, pressed);
};

// Show the native window before parsing, assembling and compiling the cartridge.
// Loading is performed on a worker so the Win32 message pump stays responsive
// even when startup compilation takes several seconds on a complex cartridge.
var loadingTimer = Stopwatch.StartNew();
RenderLoadingScreen(surface, TimeSpan.Zero);
presenter.Present(surface, ScalingMode.IntegerNearest);
presenter.PumpEvents();

var loadTask = Task.Run(() =>
{
    var loadingHost = new VirtualNesBootHost
    {
        AutomaticCompiledExecutionEnabled = !rawHardwareRuntime
    };
    if (compiledLabRuntime) loadingHost.Machine.SetCompiledLabExecutionEnabled(true);
    var loadingImage = loadingHost.LoadRom(romPath, boardSelection, palCic);
    return (Host: loadingHost, Image: loadingImage);
});

while (!loadTask.IsCompleted && presenter.IsOpen)
{
    presenter.PumpEvents();
    if (!presenter.IsOpen) break;

    RenderLoadingScreen(surface, loadingTimer.Elapsed);
    presenter.Present(surface, ScalingMode.IntegerNearest);
    Thread.Sleep(16);
}

// Closing the loading window cancels startup from the user's point of view.
// The worker is a ThreadPool background task and does not own native resources.
if (!presenter.IsOpen) return 0;

VirtualNesBootHost host;
VirtualHardwareNesRomImage image;
try
{
    var loaded = loadTask.GetAwaiter().GetResult();
    host = loaded.Host;
    image = loaded.Image;
    controllerHost = host;
}
catch (NotSupportedException exception)
{
    NativeMessageDialog.ShowError("Unsupported virtual cartridge hardware", exception.Message);
    Console.Error.WriteLine(exception.Message);
    return 4;
}

var startupLoadTime = loadingTimer.Elapsed;
presenter.SetTitle($"AxetosOS Virtual NES — {Path.GetFileNameWithoutExtension(romPath)}");

var rawHardwareRuntimeActive = rawHardwareRuntime
    && host.Machine.ActiveMotherboard == ActiveNesMotherboard.Famicom;
if (rawHardwareRuntimeActive)
{
    host.Machine.SetCompiledLabExecutionEnabled(false);
    host.Machine.Famicom.SetCompiledPhysicalMachineEnabled(false);
}

if (host.Machine.ActiveMotherboard == ActiveNesMotherboard.PalNes)
{
    Console.Error.WriteLine("PAL desktop presentation will be enabled after the PAL boot diagnostics path is exposed.");
    return 5;
}

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
var physicalAudioPlayback = !uncappedRuntime;
if (physicalAudioPlayback) audio.Start();

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
if (ppuSplitTrace || cartridgeVideoTrace.HasValue)
{
    tracedPpu = host.Machine.ActiveMotherboard switch
    {
        ActiveNesMotherboard.Famicom => host.Machine.Famicom.Ppu,
        ActiveNesMotherboard.NtscNes => host.Machine.NtscNes.Ppu,
        _ => null
    };
    if (ppuSplitTrace || cartridgeVideoTrace.HasValue) tracedPpu?.SplitTraceOutput.SetCaptureEnabled(true);
}

CartridgeVideoTraceCollector? cartridgeTraceCollector = null;
if (cartridgeVideoTrace is { } traceRange)
{
    if (tracedPpu is null || host.Machine.Slot.Cartridge is not Mmc1Cartridge traceMmc1)
    {
        Console.Error.WriteLine("--cartridge-video-trace currently requires an RP2C02 board with an MMC1 cartridge.");
        return 6;
    }

    var tracedCpu = host.Machine.ActiveMotherboard switch
    {
        ActiveNesMotherboard.Famicom => host.Machine.Famicom.Cpu,
        ActiveNesMotherboard.NtscNes => host.Machine.NtscNes.Cpu,
        _ => null
    };
    if (tracedCpu is null)
    {
        Console.Error.WriteLine("--cartridge-video-trace requires an RP2A0x CPU paired with the traced RP2C02 board.");
        return 6;
    }

    cartridgeTraceCollector = new CartridgeVideoTraceCollector(traceMmc1, tracedPpu, tracedCpu, traceRange.Start, traceRange.End);
}
var initial = host.Snapshot();
Console.WriteLine("AxetosOS Products / NES — virtual hardware desktop host");
Console.WriteLine($"ROM:        {romPath}");
Console.WriteLine($"Startup:    ROM parse + physical assembly + compilation {startupLoadTime.TotalMilliseconds:N0} ms");
Console.WriteLine($"Mapper:     {image.MapperNumber}");
Console.WriteLine($"Board:      {initial.Motherboard}");
Console.WriteLine($"PRG ROM:    {image.PrgRomSizeBytes:N0} bytes");
Console.WriteLine($"CHR:        {(image.ChrRomSizeBytes == 0 ? $"{image.TotalChrRamSizeBytes:N0} bytes RAM/NVRAM" : $"{image.ChrRomSizeBytes:N0} bytes ROM")}");
Console.WriteLine($"Header:     {image.HeaderFormat}{(image.SubmapperNumber.HasValue ? $"; submapper={image.SubmapperNumber.Value}" : string.Empty)}; RAM sizes={(image.HasExplicitRamSizes ? "explicit" : "legacy/inferred")}");
Console.WriteLine($"Cart RAM:   PRG-RAM={Math.Max(0, image.PrgRamSizeBytes):N0}; PRG-NVRAM={Math.Max(0, image.PrgNvRamSizeBytes):N0}; CHR-RAM={Math.Max(0, image.ChrRamSizeBytes):N0}; CHR-NVRAM={Math.Max(0, image.ChrNvRamSizeBytes):N0} bytes");
var compiledFamicom = host.Machine.ActiveMotherboard == ActiveNesMotherboard.Famicom
    && host.Machine.Famicom.CompiledPhysicalMachineEnabled;
var compiledLab = host.Machine.ActiveMotherboard == ActiveNesMotherboard.Famicom
    && host.Machine.Famicom.CompiledLabMotherboardEnabled;
Console.WriteLine(compiledLab
    ? "Execution:   startup-compiled physical Famicom motherboard + replaceable cartridge unit"
    : compiledFamicom
        ? "Execution:   startup-compiled fused physical Famicom/NROM machine"
        : "Execution:   diagnostic raw physical virtual-hardware buses and master clock only");
Console.WriteLine("Video:       RP2C02 color output -> AxetosOS native framebuffer presenter");
Console.WriteLine($"Audio:       RP2A03 DAC output -> AxetosOS native PCM output ({AudioSampleRate:N0} Hz mono)");
Console.WriteLine("Controls:    Arrows=D-pad, Z=A, X=B, Enter=Start, Right Shift=Select, Esc=Exit");
Console.WriteLine(compiledLab
    ? $"Kernel:      whole-circuit compiler ({host.Machine.Famicom.CompiledLabRuntimeUnitCount} runtime units; {host.Machine.Famicom.CompiledLabInternalComponentCount} fixed-board components; {host.Machine.Famicom.CompiledLabFoldedInternalTraceCount} internal traces folded; {host.Machine.Famicom.CompiledLabBoundaryTraceCount} cartridge-boundary traces)"
    : compiledFamicom
        ? $"Kernel:      fused compiled circuit ({host.Machine.Famicom.CompiledRuntimeUnitCount} runtime unit; {host.Machine.Famicom.CompiledFoldedPhysicalTraceCount} fixed traces folded; no signal queue)"
        : "Kernel:      chip-owned pin-gated direct propagation (no signal queue)");
if (compiledLab)
{
    Console.WriteLine("Compiler:    component hardware facets + physical topology only; zero board/product semantics");
    Console.WriteLine("Boundary:    mapper + ROM remain replaceable external hardware");
    if (compiledLabRuntime) Console.WriteLine("Compiler mode: generic whole-circuit compiler explicitly forced for A/B comparison");
}
if (rawHardwareRuntimeActive) Console.WriteLine("Diagnostic:  raw per-trace physical runtime explicitly forced");
var realTimePacing = !uncappedRuntime && !profileSimulation;
Console.WriteLine(realTimePacing
    ? $"Timing:      hardware real-time ({masterClockHz:N0} Hz master clock)"
    : "Timing:      uncapped host throughput");
if (profileSimulation) Console.WriteLine("Profiler:    enabled; component/net/internal-IC timing sampled 1/256; host timing exact; results every 5 seconds");
if (ppuSplitTrace) Console.WriteLine("PPU trace:   sprite-zero and $2002/$2005/$2006 split-screen events enabled");
if (cartridgeVideoTrace is { } activeTrace)
{
    Console.WriteLine($"Cart trace:  MMC1 exact CHR-read + PPUSTATUS + sprite-zero provenance for frames {activeTrace.Start:N0}-{activeTrace.End:N0}");
    Console.WriteLine("Trace timing: precision drain every 12 master clocks (one CPU bus-cycle period) while capture is active");
}
if (uncappedRuntime)
{
    Console.WriteLine("Host output:  uncapped benchmark decouples real-time audio playback and limits physical presentation to 60 Hz");
    Console.WriteLine("Benchmark:    PPU/APU generation, framebuffer conversion and PCM resampling remain active; only real-time device backpressure is removed");
}
if (stopFrame.HasValue) Console.WriteLine($"Stop target: PPU frame {stopFrame.Value:N0}");

const int MasterCyclesPerVideoBatch = 16_384;
const int MasterCyclesPerPrecisionTraceBatch = 12;
const int AudioTransferBufferSize = 4_096;
var audioTransfer = new float[AudioTransferBufferSize];
var timer = Stopwatch.StartNew();
ulong pacedMasterCycles = 0;
var lastPresentedFrame = ulong.MaxValue;
var lastPhysicalPresentationTime = TimeSpan.MinValue;
var uncappedPresentationInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60);
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

    cartridgeTraceCollector?.UpdateFetchCapture(host.Snapshot().PpuFrames);
    var masterCyclesThisBatch = cartridgeTraceCollector?.RequiresFineTiming == true
        ? MasterCyclesPerPrecisionTraceBatch
        : MasterCyclesPerVideoBatch;
    profileStarted = profileSimulation ? Stopwatch.GetTimestamp() : 0;
    host.AdvanceMasterCycles(masterCyclesThisBatch);
    cartridgeTraceCollector?.ObserveTimingSample();
    cartridgeTraceCollector?.DrainCapturedSignals();
    pacedMasterCycles += (ulong)masterCyclesThisBatch;
    var reachedStopFrame = stopFrame.HasValue && host.Snapshot().PpuFrames >= stopFrame.Value;
    if (profileSimulation)
    {
        profileSimulationTicks += Stopwatch.GetTimestamp() - profileStarted;
        profileSimulationBatches++;
    }

    tracedPpu?.SplitTraceOutput.Drain(trace =>
    {
        cartridgeTraceCollector?.OnPpuSplitTrace(trace);
        if (ppuSplitTrace)
        {
            Console.WriteLine(
                $"PPU SPLIT: frame={trace.Frame:N0}; scanline={trace.Scanline}; dot={trace.Dot}; " +
                $"op={trace.Operation}; value=${trace.Value:X2}; v=${trace.VramAddress:X4}; " +
                $"t=${trace.TemporaryAddress:X4}; x={trace.FineX}; w={(trace.WriteToggle ? 1 : 0)}");
        }
    });

    if (videoSink.CompletedFrame != lastPresentedFrame)
    {
        cartridgeTraceCollector?.OnCompletedFrame(videoSink.CompletedFrame, videoSink.CompletedPixels.Span);

        // In throughput mode the virtual machine must be allowed to outrun the
        // physical display. Keep the native window alive at a normal refresh
        // cadence, but do not make every emulated frame wait on presentation.
        // This is host policy only; the RP2C02 still generates every pixel and
        // the framebuffer sink still consumes every sample.
        var presentationNow = timer.Elapsed;
        var presentPhysicalFrame = !uncappedRuntime
            || lastPhysicalPresentationTime == TimeSpan.MinValue
            || presentationNow - lastPhysicalPresentationTime >= uncappedPresentationInterval;
        if (presentPhysicalFrame)
        {
            profileStarted = profileSimulation ? Stopwatch.GetTimestamp() : 0;
            videoSink.CompletedPixels.Span.CopyTo(surface.PixelSpan);
            presenter.Present(surface, ScalingMode.IntegerNearest);
            lastPhysicalPresentationTime = presentationNow;
            if (profileSimulation)
            {
                profileVideoPresentationTicks += Stopwatch.GetTimestamp() - profileStarted;
                profilePresentedFrames++;
            }
        }
        lastPresentedFrame = videoSink.CompletedFrame;
    }

    profileStarted = profileSimulation ? Stopwatch.GetTimestamp() : 0;
    int drained;
    while ((drained = audioSink.Drain(audioTransfer)) > 0)
    {
        // A physical audio device necessarily consumes samples in real time.
        // Submitting faster-than-real-time PCM here would turn --uncapped into
        // an audio-device benchmark and eventually apply backpressure. Keep the
        // complete APU + DAC + PCM-resampling workload active, but discard the
        // final host samples in throughput mode.
        if (physicalAudioPlayback)
            audio.Submit(audioTransfer.AsSpan(0, drained));
        if (profileSimulation) profileAudioSamples += (ulong)drained;
    }
    if (profileSimulation) profileAudioTransferTicks += Stopwatch.GetTimestamp() - profileStarted;

    if (reachedStopFrame) break;

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
            $"CPU {diagnostics.CpuInstructions:N0}{waitText} | " +
            (physicalAudioPlayback ? $"Audio {audio.BufferedMilliseconds:F0} ms" : "Audio benchmark-discard"));

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

    // Backpressure is correct for normal real-time playback, but must never
    // throttle an explicit uncapped throughput run.
    if (physicalAudioPlayback && audio.BufferedMilliseconds > 120) Thread.Sleep(1);
}

cartridgeTraceCollector?.Finish();
var final = host.Snapshot();
var finalAverageFps = fpsStatisticsStarted && timer.Elapsed > fpsStatisticsStartTime
    ? (final.PpuFrames - fpsStatisticsStartFrame) / (timer.Elapsed - fpsStatisticsStartTime).TotalSeconds
    : timer.Elapsed.TotalSeconds > 0 ? final.PpuFrames / timer.Elapsed.TotalSeconds : 0.0;
Console.WriteLine(
    $"Stopped:     master={final.MasterCycles:N0}, instructions={final.CpuInstructions:N0}, frames={final.PpuFrames:N0}, " +
    $"current={displayedFps:F2}, min={(double.IsPositiveInfinity(minimumFps) ? 0.0 : minimumFps):F2}, " +
    $"max={maximumFps:F2}, average={finalAverageFps:F2} FPS");
if (uncappedRuntime)
{
    const double NtscFramesPerSecond = 60.0988;
    Console.WriteLine(
        $"Throughput:  {finalAverageFps / NtscFramesPerSecond:F2}x NTSC real-time; " +
        $"headroom={(finalAverageFps / NtscFramesPerSecond - 1.0) * 100.0:F1}%");
}
Console.WriteLine($"Boot checks: reset-vector={final.ResetVectorObserved}, opcode={final.FirstOpcodeObserved}, vblank={final.FirstVblankObserved}, nmi={final.FirstNmiObserved}");
Console.WriteLine($"State:       PC=${final.ProgramCounter:X4}, opcode=${final.CurrentOpcode:X2}, CPU-state={final.CpuCycleState}, bus=${final.CpuBusAddress:X4}");
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
var finalControllers = host.Machine.ActiveMotherboard switch
{
    ActiveNesMotherboard.Famicom => (host.Machine.Famicom.Controller1, host.Machine.Famicom.Controller2),
    ActiveNesMotherboard.NtscNes => (host.Machine.NtscNes.Controller1, host.Machine.NtscNes.Controller2),
    ActiveNesMotherboard.PalNes => (host.Machine.PalNes.Controller1, host.Machine.PalNes.Controller2),
    _ => ((NesStandardController?)null, (NesStandardController?)null)
};
if (finalControllers.Item1 is not null && finalControllers.Item2 is not null)
{
    Console.WriteLine(
        $"Controllers: P1=${host.Machine.InspectControllerButtons(0):X2} latch={finalControllers.Item1.LatchCount:N0} shift={finalControllers.Item1.ShiftCount:N0}; " +
        $"P2=${host.Machine.InspectControllerButtons(1):X2} latch={finalControllers.Item2.LatchCount:N0} shift={finalControllers.Item2.ShiftCount:N0}");
}
var finalPpu = host.Machine.ActiveMotherboard switch
{
    ActiveNesMotherboard.Famicom => host.Machine.Famicom.Ppu,
    ActiveNesMotherboard.NtscNes => host.Machine.NtscNes.Ppu,
    _ => null
};
if (finalPpu is not null)
{
    var ppuState = finalPpu.InspectDiagnosticState();
    var ciramHash = host.Machine.ActiveMotherboard switch
    {
        ActiveNesMotherboard.Famicom => host.Machine.Famicom.Ciram.InspectStateHash(),
        ActiveNesMotherboard.NtscNes => host.Machine.NtscNes.Ciram.InspectStateHash(),
        _ => 0UL
    };
    Console.WriteLine(
        $"PPU core:    raster={ppuState.Frame:N0}:{ppuState.Scanline}:{ppuState.Dot}, ctrl=${ppuState.Control:X2}, mask=${ppuState.Mask:X2}, " +
        $"v=${ppuState.VramAddress:X4}, t=${ppuState.TemporaryAddress:X4}, x={ppuState.FineX}, w={(ppuState.WriteToggle ? 1 : 0)}, oam=${ppuState.OamAddress:X2}");
    Console.WriteLine(
        $"PPU pipe:    state=${ppuState.PipelineStateHash:X16}, palette=${ppuState.PaletteRamHash:X16}, oam=${ppuState.PrimaryOamHash:X16}, " +
        $"ciram=${ciramHash:X16}, frame={videoSink.CompletedFrame:N0}:${videoSink.CompletedFrameHash:X16}");
    Console.WriteLine(
        $"PPU fetch:   reads={ppuState.CompletedVramReads:N0}, writes={ppuState.CompletedVramWrites:N0}, nt={ppuState.BackgroundNametableFetches:N0}, " +
        $"attr={ppuState.BackgroundAttributeFetches:N0}, pattern={ppuState.BackgroundPatternFetches:N0}, dummy-nt={ppuState.DummyNametableFetches:N0}, sprite-pattern={ppuState.SpritePatternFetches:N0}");
    Console.WriteLine(
        $"PPU CPU-IO:  delayed-v-commits={finalPpu.DelayedVramAddressCommitCount:N0}, delayed-$2007-increments={finalPpu.DelayedPpudataIncrementCount:N0}");
}
if (host.Machine.Slot.Cartridge is Mmc1Cartridge finalMmc1)
{
    Console.WriteLine(
        $"MMC1 core:   control=${finalMmc1.ControlRegister:X2}, chr0=${finalMmc1.ChrBank0Register:X2}, " +
        $"chr1=${finalMmc1.ChrBank1Register:X2}, prg=${finalMmc1.PrgBankRegister:X2}, " +
        $"prg-ram={finalMmc1.PrgRamSizeBytes:N0} enabled={finalMmc1.PrgRamEnabled}, " +
        $"mapper-writes={finalMmc1.MapperWriteCount:N0}, commits={finalMmc1.MapperCommitCount:N0}, resets={finalMmc1.MapperResetWriteCount:N0}, " +
        $"ignored-consecutive={finalMmc1.IgnoredConsecutiveMapperWriteCount:N0}, hash=${finalMmc1.MapperWriteHash:X16}, " +
        $"last=${finalMmc1.LastMapperWriteAddress:X4}:${finalMmc1.LastMapperWriteData:X2}, ppu-reads={finalMmc1.PpuReadCount:N0}");
}
if (host.Machine.Slot.Cartridge is UxromCartridge finalUxrom)
{
    Console.WriteLine(
        $"UxROM core:  bank=${finalUxrom.BankRegister:X2} selected={finalUxrom.SelectedPrgBank:N0} fixed={finalUxrom.FixedPrgBank:N0}, " +
        $"chr-ram={finalUxrom.ChrRamSizeBytes:N0}, bus-conflicts={finalUxrom.BusConflictsEnabled}, " +
        $"mapper-writes={finalUxrom.MapperWriteCount:N0}, conflict-modified={finalUxrom.BusConflictModifiedWriteCount:N0}, " +
        $"last=${finalUxrom.LastMapperWriteAddress:X4}:${finalUxrom.LastMapperWriteData:X2}->${finalUxrom.LastEffectiveMapperWriteData:X2}, " +
        $"ppu-reads={finalUxrom.PpuReadCount:N0}, ppu-writes={finalUxrom.PpuWriteCount:N0}");
}
if (host.Machine.Slot.Cartridge is CnromCartridge finalCnrom)
{
    Console.WriteLine(
        $"CNROM core:  bank=${finalCnrom.BankRegister:X2} selected={finalCnrom.SelectedChrBank:N0}/{finalCnrom.ChrBankCount:N0}, " +
        $"chr-rom={finalCnrom.ChrRomSizeBytes:N0}, bus-conflicts={finalCnrom.BusConflictsEnabled}, " +
        $"mapper-writes={finalCnrom.MapperWriteCount:N0}, conflict-modified={finalCnrom.BusConflictModifiedWriteCount:N0}, " +
        $"last=${finalCnrom.LastMapperWriteAddress:X4}:${finalCnrom.LastMapperWriteData:X2}->${finalCnrom.LastEffectiveMapperWriteData:X2}, " +
        $"ppu-reads={finalCnrom.PpuReadCount:N0}");
}
if (host.Machine.Slot.Cartridge is Mmc3Cartridge finalMmc3)
{
    Console.WriteLine(
        $"MMC3 core:   select=${finalMmc3.BankSelectRegister:X2}, " +
        $"r0=${finalMmc3.BankRegisters[0]:X2}, r1=${finalMmc3.BankRegisters[1]:X2}, " +
        $"r2=${finalMmc3.BankRegisters[2]:X2}, r3=${finalMmc3.BankRegisters[3]:X2}, " +
        $"r4=${finalMmc3.BankRegisters[4]:X2}, r5=${finalMmc3.BankRegisters[5]:X2}, " +
        $"r6=${finalMmc3.BankRegisters[6]:X2}, r7=${finalMmc3.BankRegisters[7]:X2}, " +
        $"mirroring={finalMmc3.Mirroring}, prg-ram={finalMmc3.PrgRamSizeBytes:N0}, mmc6={finalMmc3.IsMmc6}");
    Console.WriteLine(
        $"MMC3 IRQ:    latch=${finalMmc3.IrqLatch:X2}, counter=${finalMmc3.IrqCounter:X2}, reload={finalMmc3.IrqReloadPending}, " +
        $"enabled={finalMmc3.IrqEnabled}, asserted={finalMmc3.IrqAsserted}, a12-clocks={finalMmc3.QualifiedA12RiseCount:N0}, " +
        $"irq-asserts={finalMmc3.IrqAssertCount:N0}, mapper-writes={finalMmc3.MapperWriteCount:N0}, " +
        $"last=${finalMmc3.LastMapperWriteAddress:X4}:${finalMmc3.LastMapperWriteData:X2}, " +
        $"ppu-reads={finalMmc3.PpuReadCount:N0}, ppu-writes={finalMmc3.PpuWriteCount:N0}");
}
if (host.Machine.Slot.Cartridge is AxromCartridge finalAxrom)
{
    Console.WriteLine(
        $"AxROM core:  bank=${finalAxrom.BankRegister:X2} selected={finalAxrom.SelectedPrgBank:N0}, nametable={finalAxrom.SelectedNametablePage}, " +
        $"chr-ram={finalAxrom.ChrRamSizeBytes:N0}, bus-conflicts={finalAxrom.BusConflictsEnabled}, " +
        $"mapper-writes={finalAxrom.MapperWriteCount:N0}, conflict-modified={finalAxrom.BusConflictModifiedWriteCount:N0}, " +
        $"last=${finalAxrom.LastMapperWriteAddress:X4}:${finalAxrom.LastMapperWriteData:X2}->${finalAxrom.LastEffectiveMapperWriteData:X2}, " +
        $"ppu-reads={finalAxrom.PpuReadCount:N0}, ppu-writes={finalAxrom.PpuWriteCount:N0}");
}
if (host.Machine.Slot.Cartridge is Mmc2Cartridge finalMmc2)
{
    Console.WriteLine(
        $"MMC2 core:   prg=${finalMmc2.PrgBankRegister:X2} selected={finalMmc2.SelectedPrgBank}/{finalMmc2.PrgBankCount}, " +
        $"chrFD0=${finalMmc2.ChrBankRegisters[0]:X2}, chrFE0=${finalMmc2.ChrBankRegisters[1]:X2}, " +
        $"chrFD1=${finalMmc2.ChrBankRegisters[2]:X2}, chrFE1=${finalMmc2.ChrBankRegisters[3]:X2}, " +
        $"latch0=${finalMmc2.Latch0:X2} selected={finalMmc2.SelectedChrBank0}/{finalMmc2.ChrBankCount}, " +
        $"latch1=${finalMmc2.Latch1:X2} selected={finalMmc2.SelectedChrBank1}/{finalMmc2.ChrBankCount}, mirroring={finalMmc2.Mirroring}");
    Console.WriteLine(
        $"MMC2 latch:  triggers={finalMmc2.LatchTriggerCount:N0} " +
        $"0FD={finalMmc2.Latch0FdTriggerCount:N0} 0FE={finalMmc2.Latch0FeTriggerCount:N0} " +
        $"1FD={finalMmc2.Latch1FdTriggerCount:N0} 1FE={finalMmc2.Latch1FeTriggerCount:N0}, " +
        $"last-trigger=${finalMmc2.LastLatchTriggerAddress:X4}, mapper-writes={finalMmc2.MapperWriteCount:N0}, " +
        $"last=${finalMmc2.LastMapperWriteAddress:X4}:${finalMmc2.LastMapperWriteData:X2}, " +
        $"cpu-reads={finalMmc2.CpuReadCount:N0}, ppu-reads={finalMmc2.PpuReadCount:N0}");
}
if (host.Machine.Slot.Cartridge is Mmc4Cartridge finalMmc4)
{
    Console.WriteLine(
        $"MMC4 core:   prg=${finalMmc4.PrgBankRegister:X2} selected={finalMmc4.SelectedPrgBank}/{finalMmc4.PrgBankCount}, " +
        $"chrFD0=${finalMmc4.ChrBankRegisters[0]:X2}, chrFE0=${finalMmc4.ChrBankRegisters[1]:X2}, " +
        $"chrFD1=${finalMmc4.ChrBankRegisters[2]:X2}, chrFE1=${finalMmc4.ChrBankRegisters[3]:X2}, " +
        $"latch0=${finalMmc4.Latch0:X2} selected={finalMmc4.SelectedChrBank0}/{finalMmc4.ChrBankCount}, " +
        $"latch1=${finalMmc4.Latch1:X2} selected={finalMmc4.SelectedChrBank1}/{finalMmc4.ChrBankCount}, " +
        $"mirroring={finalMmc4.Mirroring}, prg-ram={finalMmc4.PrgRamSizeBytes:N0}");
    Console.WriteLine(
        $"MMC4 latch:  triggers={finalMmc4.LatchTriggerCount:N0} " +
        $"0FD={finalMmc4.Latch0FdTriggerCount:N0} 0FE={finalMmc4.Latch0FeTriggerCount:N0} " +
        $"1FD={finalMmc4.Latch1FdTriggerCount:N0} 1FE={finalMmc4.Latch1FeTriggerCount:N0}, " +
        $"last-trigger=${finalMmc4.LastLatchTriggerAddress:X4}, mapper-writes={finalMmc4.MapperWriteCount:N0}, " +
        $"ram-writes={finalMmc4.PrgRamWriteCount:N0}, last=${finalMmc4.LastMapperWriteAddress:X4}:${finalMmc4.LastMapperWriteData:X2}, " +
        $"cpu-reads={finalMmc4.CpuReadCount:N0}, ppu-reads={finalMmc4.PpuReadCount:N0}");
}
if (host.Machine.Slot.Cartridge is ColorDreamsCartridge finalColorDreams)
{
    Console.WriteLine(
        $"Color Dreams: bank=${finalColorDreams.BankRegister:X2}, prg={finalColorDreams.SelectedPrgBank}/{finalColorDreams.PrgBankCount}, " +
        $"chr={finalColorDreams.SelectedChrBank}/{finalColorDreams.ChrBankCount}, bus-conflicts={finalColorDreams.BusConflictsEnabled}, " +
        $"mapper-writes={finalColorDreams.MapperWriteCount:N0}, conflict-modified={finalColorDreams.BusConflictModifiedWriteCount:N0}, " +
        $"last=${finalColorDreams.LastMapperWriteAddress:X4}:${finalColorDreams.LastMapperWriteData:X2}->${finalColorDreams.LastEffectiveMapperWriteData:X2}, " +
        $"ppu-reads={finalColorDreams.PpuReadCount:N0}");
}
if (host.Machine.Slot.Cartridge is BandaiFcgCartridge finalBandai)
{
    Console.WriteLine(
        $"Bandai FCG:  variant={finalBandai.Variant}, prg=${finalBandai.PrgBankRegister:X2} selected={finalBandai.SelectedPrgBank}/{finalBandai.PrgBankCount} fixed={finalBandai.FixedPrgBank}, " +
        $"chr=[{string.Join(",", finalBandai.ChrBankRegisters.Select(value => $"${value:X2}"))}], mirroring={finalBandai.NametableMode}, " +
        $"mapper-writes={finalBandai.MapperWriteCount:N0}, last=${finalBandai.LastMapperWriteAddress:X4}:${finalBandai.LastMapperWriteData:X2}, " +
        $"cpu-reads={finalBandai.CpuReadCount:N0}, ppu-reads={finalBandai.PpuReadCount:N0}");
    Console.WriteLine(
        $"Bandai IRQ:  latch=${finalBandai.IrqLatch:X4}, counter=${finalBandai.IrqCounter:X4}, enabled={finalBandai.IrqEnabled}, asserted={finalBandai.IrqAsserted}, " +
        $"clocks={finalBandai.IrqClockCount:N0}, irq-asserts={finalBandai.IrqAssertCount:N0}, " +
        $"eeprom={finalBandai.EepromSizeBytes:N0}, eeprom-control-writes={finalBandai.EepromControlWriteCount:N0}, " +
        $"eeprom-reads={finalBandai.EepromReadCount:N0}, eeprom-writes={finalBandai.EepromWriteCount:N0}");
}
if (host.Machine.Slot.Cartridge is JalecoSs88006Cartridge finalJaleco)
{
    Console.WriteLine(
        $"Jaleco SS88006: prg-reg=[{string.Join(",", finalJaleco.PrgBankRegisters.Select(value => $"${value:X2}"))}], " +
        $"prg-map=[{string.Join(",", finalJaleco.PrgWindowBanks)}], chr-reg=[{string.Join(",", finalJaleco.ChrBankRegisters.Select(value => $"${value:X2}"))}], " +
        $"mirroring={finalJaleco.NametableMode}, wram={finalJaleco.WramSizeBytes:N0}, protect=${finalJaleco.WramProtectRegister:X2}, " +
        $"mapper-writes={finalJaleco.MapperWriteCount:N0}, last=${finalJaleco.LastMapperWriteAddress:X4}:${finalJaleco.LastMapperWriteData:X2}, " +
        $"cpu-reads={finalJaleco.CpuReadCount:N0}, wram-reads={finalJaleco.WramReadCount:N0}, wram-writes={finalJaleco.WramWriteCount:N0}, ppu-reads={finalJaleco.PpuReadCount:N0}");
    Console.WriteLine(
        $"Jaleco IRQ:  latch=${finalJaleco.IrqLatch:X4}, counter=${finalJaleco.IrqCounter:X4}, enabled={finalJaleco.IrqEnabled}, " +
        $"mode=${finalJaleco.IrqMode:X2}, mask=${finalJaleco.IrqCounterMask:X4}, asserted={finalJaleco.IrqAsserted}, " +
        $"clocks={finalJaleco.IrqClockCount:N0}, irq-asserts={finalJaleco.IrqAssertCount:N0}, " +
        $"sample-control=${finalJaleco.SampleControlRegister:X2}, sample-triggers={finalJaleco.SampleTriggerCount:N0}, last-sample={finalJaleco.LastSampleIndex}");
}
if (host.Machine.Slot.Cartridge is SunsoftFme7Cartridge finalSunsoft)
{
    Console.WriteLine(
        $"Sunsoft FME-7: cmd=${finalSunsoft.CommandRegister:X2}, prg6000=${finalSunsoft.Prg6000ControlRegister:X2} " +
        $"{(finalSunsoft.Prg6000RomSelected ? $"ROM[{finalSunsoft.Prg6000Bank}]" : finalSunsoft.Prg6000RamEnabled ? $"RAM[{finalSunsoft.WramBank}]" : "OPEN")}, " +
        $"prg-reg=[{string.Join(",", finalSunsoft.PrgBankRegisters.Select(value => $"${value:X2}"))}], " +
        $"prg-map=[{string.Join(",", finalSunsoft.PrgWindowBanks)}], chr-reg=[{string.Join(",", finalSunsoft.ChrBankRegisters.Select(value => $"${value:X2}"))}], " +
        $"mirroring={finalSunsoft.NametableMode}, wram={finalSunsoft.WramSizeBytes:N0}, mapper-writes={finalSunsoft.MapperWriteCount:N0}, core-writes={finalSunsoft.CoreRegisterWriteCount:N0}, " +
        $"last=${finalSunsoft.LastMapperWriteAddress:X4}:${finalSunsoft.LastMapperWriteData:X2}, cpu-reads={finalSunsoft.CpuReadCount:N0}, " +
        $"rom6000-reads={finalSunsoft.Prg6000RomReadCount:N0}, wram-reads={finalSunsoft.WramReadCount:N0}, wram-writes={finalSunsoft.WramWriteCount:N0}, ppu-reads={finalSunsoft.PpuReadCount:N0}");
    Console.WriteLine(
        $"Sunsoft IRQ:   counter=${finalSunsoft.IrqCounter:X4}, counter-enable={finalSunsoft.IrqCounterEnabled}, output-enable={finalSunsoft.IrqOutputEnabled}, " +
        $"asserted={finalSunsoft.IrqAsserted}, cpu-clocks={finalSunsoft.CpuCycleClockCount:N0}, irq-clocks={finalSunsoft.IrqClockCount:N0}, irq-asserts={finalSunsoft.IrqAssertCount:N0}");
    Console.WriteLine(
        $"Sunsoft 5B:    select=${finalSunsoft.Psg.SelectedRegister:X2}, write-lock={finalSunsoft.Psg.DataWritesDisabled}, " +
        $"select-writes={finalSunsoft.Psg.RegisterSelectWriteCount:N0}, data-writes={finalSunsoft.Psg.RegisterDataWriteCount:N0}, ignored-data={finalSunsoft.Psg.IgnoredDataWriteCount:N0}, " +
        $"tone=[${finalSunsoft.Psg.TonePeriodA:X3},${finalSunsoft.Psg.TonePeriodB:X3},${finalSunsoft.Psg.TonePeriodC:X3}], noise=${finalSunsoft.Psg.NoisePeriod:X2}, " +
        $"envelope=${finalSunsoft.Psg.EnvelopePeriod:X4}/${finalSunsoft.Psg.EnvelopeShape:X1}, dac={finalSunsoft.Psg.MixedDacLevel}, " +
        $"psg-clocks={finalSunsoft.Psg.CpuClockCount:N0}, generator-ticks={finalSunsoft.Psg.GeneratorTickCount:N0}, " +
        $"tone-flips={finalSunsoft.Psg.ToneFlipCount:N0}, noise-shifts={finalSunsoft.Psg.NoiseShiftCount:N0}, " +
        $"envelope-steps={finalSunsoft.Psg.EnvelopeStepCount:N0}, output-edges={finalSunsoft.Psg.OutputEdgeCount:N0}");
}
if (host.Machine.Slot.Cartridge is KonamiVrc4Cartridge finalVrc4)
{
    Console.WriteLine(
        $"Konami VRC4:   mapper={finalVrc4.MapperNumber}, variant={finalVrc4.Variant}, legacy-decode={finalVrc4.UsesLegacyAddressDecode}, " +
        $"prg-reg=[{string.Join(",", finalVrc4.PrgBankRegisters.Select(value => $"${value:X2}"))}], prg-mode={finalVrc4.PrgMode}, " +
        $"prg-map=[{string.Join(",", finalVrc4.PrgWindowBanks)}], chr=[{string.Join(",", Enumerable.Range(0, 8).Select(finalVrc4.GetChrRegister).Select(value => $"${value:X3}"))}], " +
        $"mirroring={finalVrc4.NametableMode}, wram={finalVrc4.WorkRamSizeBytes:N0}, mapper-writes={finalVrc4.MapperWriteCount:N0}, " +
        $"last=${finalVrc4.LastMapperWriteAddress:X4}->${finalVrc4.LastTranslatedMapperWriteAddress:X4}:${finalVrc4.LastMapperWriteData:X2}, " +
        $"cpu-reads={finalVrc4.CpuReadCount:N0}, wram-reads={finalVrc4.WorkRamReadCount:N0}, wram-writes={finalVrc4.WorkRamWriteCount:N0}, ppu-reads={finalVrc4.PpuReadCount:N0}");
    Console.WriteLine(
        $"Konami IRQ:    reload=${finalVrc4.Irq.ReloadValue:X2}, counter=${finalVrc4.Irq.Counter:X2}, prescaler={finalVrc4.Irq.Prescaler}, " +
        $"enabled={finalVrc4.Irq.Enabled}, enable-after-ack={finalVrc4.Irq.EnabledAfterAcknowledge}, cycle-mode={finalVrc4.Irq.CycleMode}, asserted={finalVrc4.Irq.Asserted}, " +
        $"cpu-clocks={finalVrc4.Irq.CpuClockCount:N0}, counter-clocks={finalVrc4.Irq.CounterClockCount:N0}, irq-asserts={finalVrc4.Irq.AssertCount:N0}");
}
if (host.Machine.Slot.Cartridge is KonamiVrc6Cartridge finalVrc6)
{
    Console.WriteLine(
        $"Konami VRC6:   mapper={finalVrc6.MapperNumber}, variant={finalVrc6.Variant}, prg16=${finalVrc6.Prg16BankRegister:X2}, prg8=${finalVrc6.Prg8BankRegister:X2}, " +
        $"prg-map=[{string.Join(",", finalVrc6.PrgWindowBanks)}], chr=[{string.Join(",", finalVrc6.ChrBankRegisters.Select(value => $"${value:X2}"))}], " +
        $"chr-map=[{string.Join(",", finalVrc6.ChrWindowBanks)}], mode=${finalVrc6.BankingModeRegister:X2}, " +
        $"nt-source={(finalVrc6.NametablesUseChrRom ? "CHR" : "CIRAM")}, nt-map=[{string.Join(",", finalVrc6.NametablePages)}], " +
        $"wram={finalVrc6.WorkRamSizeBytes:N0}/{finalVrc6.WorkRamEnabled}, mapper-writes={finalVrc6.MapperWriteCount:N0}, " +
        $"last=${finalVrc6.LastMapperWriteAddress:X4}->${finalVrc6.LastTranslatedMapperWriteAddress:X4}:${finalVrc6.LastMapperWriteData:X2}, " +
        $"cpu-reads={finalVrc6.CpuReadCount:N0}, wram-reads={finalVrc6.WorkRamReadCount:N0}, wram-writes={finalVrc6.WorkRamWriteCount:N0}, " +
        $"ppu-reads={finalVrc6.PpuReadCount:N0}, chr-nt-reads={finalVrc6.ChrNametableReadCount:N0}");
    Console.WriteLine(
        $"Konami IRQ:    reload=${finalVrc6.Irq.ReloadValue:X2}, counter=${finalVrc6.Irq.Counter:X2}, prescaler={finalVrc6.Irq.Prescaler}, " +
        $"enabled={finalVrc6.Irq.Enabled}, enable-after-ack={finalVrc6.Irq.EnabledAfterAcknowledge}, cycle-mode={finalVrc6.Irq.CycleMode}, asserted={finalVrc6.Irq.Asserted}, " +
        $"cpu-clocks={finalVrc6.Irq.CpuClockCount:N0}, counter-clocks={finalVrc6.Irq.CounterClockCount:N0}, irq-asserts={finalVrc6.Irq.AssertCount:N0}");
    Console.WriteLine(
        $"Konami VRC6 audio: control=${finalVrc6.Audio.ControlRegister:X2}, halted={finalVrc6.Audio.Halted}, shift={finalVrc6.Audio.FrequencyShift}, dac={finalVrc6.Audio.MixedDacLevel}, " +
        $"cpu-clocks={finalVrc6.Audio.CpuClockCount:N0}, writes={finalVrc6.Audio.RegisterWriteCount:N0}, output-edges={finalVrc6.Audio.OutputEdgeCount:N0}, " +
        $"p1=${finalVrc6.Audio.Pulse1.Frequency:X3}/{finalVrc6.Audio.Pulse1.Volume:X1}/d{finalVrc6.Audio.Pulse1.DutyCycle}/s{finalVrc6.Audio.Pulse1.Step}, " +
        $"p2=${finalVrc6.Audio.Pulse2.Frequency:X3}/{finalVrc6.Audio.Pulse2.Volume:X1}/d{finalVrc6.Audio.Pulse2.DutyCycle}/s{finalVrc6.Audio.Pulse2.Step}, " +
        $"saw=${finalVrc6.Audio.Saw.Frequency:X3}/r{finalVrc6.Audio.Saw.AccumulatorRate:X2}/a{finalVrc6.Audio.Saw.Accumulator:X2}/s{finalVrc6.Audio.Saw.Step}");
}
if (host.Machine.Slot.Cartridge is Namco163Cartridge finalNamco163)
{
    Console.WriteLine(
        $"Namco 163:      prg-reg=[{string.Join(",", finalNamco163.PrgBankRegisters.Select(value => $"${value:X2}"))}], " +
        $"prg-map=[{string.Join(",", finalNamco163.PrgWindowBanks)}], ppu=[{string.Join(",", finalNamco163.PpuBankRegisters.Select(value => $"${value:X2}"))}], " +
        $"chr-ciram-disable={finalNamco163.LowChrCiramDisabled}/{finalNamco163.HighChrCiramDisabled}, protect=${finalNamco163.WriteProtectRegister:X2}, " +
        $"ram={finalNamco163.WorkRamSizeBytes:N0}, mapper-writes={finalNamco163.MapperWriteCount:N0}, last=${finalNamco163.LastMapperWriteAddress:X4}:${finalNamco163.LastMapperWriteData:X2}, " +
        $"cpu-reads={finalNamco163.CpuReadCount:N0}, low-reg-reads={finalNamco163.LowRegisterReadCount:N0}, ram-reads={finalNamco163.WorkRamReadCount:N0}, " +
        $"ram-writes={finalNamco163.WorkRamWriteCount:N0}, blocked-ram-writes={finalNamco163.BlockedWorkRamWriteCount:N0}, ppu-reads={finalNamco163.PpuReadCount:N0}, chr-reads={finalNamco163.ChrReadCount:N0}");
    Console.WriteLine(
        $"Namco IRQ:      counter=${finalNamco163.IrqCounter:X4}, enabled={finalNamco163.IrqEnabled}, asserted={finalNamco163.IrqAsserted}, " +
        $"cpu-clocks={finalNamco163.CpuCycleClockCount:N0}, irq-clocks={finalNamco163.IrqClockCount:N0}, irq-asserts={finalNamco163.IrqAssertCount:N0}");
    Console.WriteLine(
        $"Namco 163 audio: addr=${finalNamco163.Audio.RamAddress:X2}, auto-inc={finalNamco163.Audio.AutoIncrement}, disabled={finalNamco163.Audio.SoundDisabled}, " +
        $"channels={finalNamco163.Audio.ActiveChannelCount}, current={finalNamco163.Audio.CurrentChannel}, divider={finalNamco163.Audio.Divider}, dac={finalNamco163.Audio.SerialDacLevel}, " +
        $"cpu-clocks={finalNamco163.Audio.CpuClockCount:N0}, channel-updates={finalNamco163.Audio.ChannelUpdateCount:N0}, " +
        $"addr-writes={finalNamco163.Audio.RamAddressWriteCount:N0}, data-reads={finalNamco163.Audio.RamDataReadCount:N0}, data-writes={finalNamco163.Audio.RamDataWriteCount:N0}, " +
        $"auto-increments={finalNamco163.Audio.AutoIncrementCount:N0}, output-edges={finalNamco163.Audio.OutputEdgeCount:N0}, " +
        $"sample={finalNamco163.Audio.LastWaveSample:X1}, volume={finalNamco163.Audio.LastVolume:X1}");
}
if (host.Machine.Slot.Cartridge is Mapper34Cartridge finalMapper34)
{
    if (finalMapper34.BoardVariant == Mapper34BoardVariant.Bnrom)
    {
        var chrDescription = finalMapper34.ChrRamSizeBytes != 0
            ? $"RAM {finalMapper34.ChrRamSizeBytes:N0}"
            : $"ROM {finalMapper34.ChrRomSizeBytes:N0}";
        Console.WriteLine(
            $"Mapper 34:    variant=BNROM, bank=${finalMapper34.BnromBankRegister:X2}, " +
            $"prg={finalMapper34.SelectedBnromPrgBank}/{finalMapper34.BnromPrgBankCount}, chr={chrDescription}, " +
            $"prg-ram={finalMapper34.PrgRamSizeBytes:N0}, bus-conflicts={finalMapper34.BusConflictsEnabled}, " +
            $"mapper-writes={finalMapper34.MapperWriteCount:N0}, conflict-modified={finalMapper34.BusConflictModifiedWriteCount:N0}, " +
            $"last=${finalMapper34.LastMapperWriteAddress:X4}:${finalMapper34.LastMapperWriteData:X2}->${finalMapper34.LastEffectiveMapperWriteData:X2}, " +
            $"cpu-reads={finalMapper34.CpuReadCount:N0}, ppu-reads={finalMapper34.PpuReadCount:N0}, ppu-writes={finalMapper34.PpuWriteCount:N0}");
    }
    else
    {
        Console.WriteLine(
            $"Mapper 34:    variant=NINA-001/002, prg=${finalMapper34.NinaPrgRegister:X2} selected={finalMapper34.SelectedNinaPrgBank}/{finalMapper34.NinaPrgBankCount}, " +
            $"chr0=${finalMapper34.NinaChr0Register:X2} selected={finalMapper34.SelectedNinaChrBank0}/{finalMapper34.NinaChrBankCount}, " +
            $"chr1=${finalMapper34.NinaChr1Register:X2} selected={finalMapper34.SelectedNinaChrBank1}/{finalMapper34.NinaChrBankCount}, " +
            $"prg-ram={finalMapper34.PrgRamSizeBytes:N0}, mapper-writes={finalMapper34.MapperWriteCount:N0}, ram-writes={finalMapper34.PrgRamWriteCount:N0}, " +
            $"last=${finalMapper34.LastMapperWriteAddress:X4}:${finalMapper34.LastMapperWriteData:X2}, " +
            $"cpu-reads={finalMapper34.CpuReadCount:N0}, ppu-reads={finalMapper34.PpuReadCount:N0}");
    }
}
if (host.Machine.Slot.Cartridge is GxromCartridge finalGxrom)
{
    Console.WriteLine(
        $"GxROM core:  bank=${finalGxrom.BankRegister:X2}, prg={finalGxrom.SelectedPrgBank}/{finalGxrom.PrgBankCount}, " +
        $"chr={finalGxrom.SelectedChrBank}/{finalGxrom.ChrBankCount}, bus-conflicts={finalGxrom.BusConflictsEnabled}, " +
        $"mapper-writes={finalGxrom.MapperWriteCount:N0}, conflict-modified={finalGxrom.BusConflictModifiedWriteCount:N0}, " +
        $"last=${finalGxrom.LastMapperWriteAddress:X4}:${finalGxrom.LastMapperWriteData:X2}->${finalGxrom.LastEffectiveMapperWriteData:X2}, " +
        $"ppu-reads={finalGxrom.PpuReadCount:N0}");
}
if (host.Machine.Slot.Cartridge is CamericaCartridge finalCamerica)
{
    Console.WriteLine(
        $"Camerica core: bank=${finalCamerica.BankRegister:X2} selected={finalCamerica.SelectedPrgBank}/{finalCamerica.PrgBankCount} fixed={finalCamerica.FixedPrgBank}, " +
        $"single-screen={finalCamerica.MapperControlledSingleScreen} nametable={finalCamerica.SelectedNametablePage}, " +
        $"prg-writes={finalCamerica.PrgBankWriteCount:N0}, mirror-writes={finalCamerica.MirroringWriteCount:N0}, " +
        $"cic-stun={finalCamerica.CicStunLatch} cic-writes={finalCamerica.CicStunWriteCount:N0}, mapper-writes={finalCamerica.MapperWriteCount:N0}, " +
        $"last=${finalCamerica.LastMapperWriteAddress:X4}:${finalCamerica.LastMapperWriteData:X2}, " +
        $"ppu-reads={finalCamerica.PpuReadCount:N0}, ppu-writes={finalCamerica.PpuWriteCount:N0}");
}
if (host.Machine.Slot.Cartridge is Nina0306Cartridge finalNina)
{
    Console.WriteLine(
        $"NINA-03/06:  bank=${finalNina.BankRegister:X2}, prg={finalNina.SelectedPrgBank}/{finalNina.PrgBankCount}, " +
        $"chr={finalNina.SelectedChrBank}/{finalNina.ChrBankCount}, bus-conflicts={finalNina.BusConflictsEnabled}, " +
        $"mapper-writes={finalNina.MapperWriteCount:N0}, last=${finalNina.LastMapperWriteAddress:X4}:${finalNina.LastMapperWriteData:X2}, " +
        $"ppu-reads={finalNina.PpuReadCount:N0}");
}
if (host.Machine.Slot.Cartridge is DxromCartridge finalDxrom)
{
    Console.WriteLine(
        $"DxROM core:   select=${finalDxrom.BankSelectRegister:X2}, " +
        $"r0=${finalDxrom.BankRegisters[0]:X2}, r1=${finalDxrom.BankRegisters[1]:X2}, " +
        $"r2=${finalDxrom.BankRegisters[2]:X2}, r3=${finalDxrom.BankRegisters[3]:X2}, " +
        $"r4=${finalDxrom.BankRegisters[4]:X2}, r5=${finalDxrom.BankRegisters[5]:X2}, " +
        $"r6=${finalDxrom.BankRegisters[6]:X2}, r7=${finalDxrom.BankRegisters[7]:X2}, " +
        $"prg={finalDxrom.SelectedPrgBank0}/{finalDxrom.SelectedPrgBank1}/{finalDxrom.FixedPrgBank0}/{finalDxrom.FixedPrgBank1}, " +
        $"mirroring={finalDxrom.Mirroring}, unbanked32={finalDxrom.Unbanked32KPrg}, four-screen={finalDxrom.HasFourScreenRam}, prg-ram={finalDxrom.PrgRamSizeBytes:N0}, " +
        $"mapper-writes={finalDxrom.MapperWriteCount:N0}, ignored={finalDxrom.IgnoredMapperWriteCount:N0}, " +
        $"last=${finalDxrom.LastMapperWriteAddress:X4}:${finalDxrom.LastMapperWriteData:X2}, " +
        $"cpu-reads={finalDxrom.CpuReadCount:N0}, ppu-reads={finalDxrom.PpuReadCount:N0}, ppu-writes={finalDxrom.PpuWriteCount:N0}");
}
if (host.Machine.Slot.Cartridge is Mapper227Cartridge finalMapper227)
{
    Console.WriteLine(
        $"Mapper 227:   latch=${finalMapper227.AddressLatch:X3}, mode={finalMapper227.PrgMode}, " +
        $"prg={finalMapper227.LowerPrgBank}/{finalMapper227.UpperPrgBank} of {finalMapper227.PrgBankCount}, " +
        $"mirroring={finalMapper227.Mirroring}, chr-protect={finalMapper227.ChrRamWriteProtected}, " +
        $"solder-mux={finalMapper227.SolderPadReadActive}/{finalMapper227.SolderPadReadSupported} pad=${finalMapper227.SolderPadValue:X1}, " +
        $"mapper-writes={finalMapper227.MapperWriteCount:N0}, last=${finalMapper227.LastMapperWriteAddress:X4}:${finalMapper227.LastMapperWriteData:X2}, " +
        $"cpu-reads={finalMapper227.CpuReadCount:N0}, ppu-reads={finalMapper227.PpuReadCount:N0}, " +
        $"ppu-writes={finalMapper227.PpuWriteCount:N0}, protected-chr-writes={finalMapper227.ProtectedChrWriteCount:N0}");
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

static bool TryMapController1Key(NativeKey key, out NesControllerButton button)
{
    // Keep the host adapter independent of the native presenter enum's naming
    // convention. The rendering layer reports a physical key; this host maps it
    // to an external controller contact and nothing below this layer knows that
    // a keyboard exists.
    var name = key.ToString();
    button = name switch
    {
        "Z" or "KeyZ" => NesControllerButton.A,
        "X" or "KeyX" => NesControllerButton.B,
        "Enter" or "Return" => NesControllerButton.Start,
        "RightShift" or "RShift" => NesControllerButton.Select,
        "Up" or "ArrowUp" or "UpArrow" => NesControllerButton.Up,
        "Down" or "ArrowDown" or "DownArrow" => NesControllerButton.Down,
        "Left" or "ArrowLeft" or "LeftArrow" => NesControllerButton.Left,
        "Right" or "ArrowRight" or "RightArrow" => NesControllerButton.Right,
        _ => (NesControllerButton)(-1)
    };
    return (int)button >= 0;
}

static void RenderLoadingScreen(FrameSurface surface, TimeSpan elapsed)
{
    const int width = 256;
    const uint background = 0xFF0B0B12u;
    const uint panel = 0xFF171724u;
    const uint border = 0xFF5C5C72u;
    const uint text = 0xFFF2F2F7u;
    const uint dim = 0xFF3C3C50u;
    const uint active = 0xFFB8B8C8u;

    var pixels = surface.PixelSpan;
    pixels.Fill(background);

    DrawLoadingRect(pixels, width, 24, 42, 208, 156, panel);
    DrawLoadingRect(pixels, width, 24, 42, 208, 2, border);
    DrawLoadingRect(pixels, width, 24, 196, 208, 2, border);
    DrawLoadingRect(pixels, width, 24, 42, 2, 156, border);
    DrawLoadingRect(pixels, width, 230, 42, 2, 156, border);

    DrawLoadingTextCentered(pixels, width, "AXETOSOS", 67, 4, text);
    DrawLoadingTextCentered(pixels, width, "LOADING ROM", 124, 2, text);

    var activeDot = (int)(elapsed.TotalMilliseconds / 125.0) & 7;
    const int dotSize = 6;
    const int dotGap = 4;
    const int dotCount = 8;
    var dotsWidth = (dotCount * dotSize) + ((dotCount - 1) * dotGap);
    var dotsX = (width - dotsWidth) / 2;
    for (var index = 0; index < dotCount; index++)
    {
        DrawLoadingRect(
            pixels,
            width,
            dotsX + (index * (dotSize + dotGap)),
            154,
            dotSize,
            dotSize,
            index == activeDot ? active : dim);
    }

    DrawLoadingTextCentered(pixels, width, "PLEASE WAIT", 178, 1, border);
}

static void DrawLoadingTextCentered(
    Span<uint> pixels,
    int surfaceWidth,
    string value,
    int y,
    int scale,
    uint color)
{
    var textWidth = (((value.Length * 6) - 1) * scale);
    DrawLoadingText(pixels, surfaceWidth, value, (surfaceWidth - textWidth) / 2, y, scale, color);
}

static void DrawLoadingText(
    Span<uint> pixels,
    int surfaceWidth,
    string value,
    int x,
    int y,
    int scale,
    uint color)
{
    for (var characterIndex = 0; characterIndex < value.Length; characterIndex++)
    {
        var glyph = GetLoadingGlyph(char.ToUpperInvariant(value[characterIndex]));
        if (glyph.Length == 0) continue;

        var glyphX = x + (characterIndex * 6 * scale);
        for (var row = 0; row < 7; row++)
        {
            for (var column = 0; column < 5; column++)
            {
                if (glyph[(row * 5) + column] != '#') continue;
                DrawLoadingRect(
                    pixels,
                    surfaceWidth,
                    glyphX + (column * scale),
                    y + (row * scale),
                    scale,
                    scale,
                    color);
            }
        }
    }
}

static void DrawLoadingRect(
    Span<uint> pixels,
    int surfaceWidth,
    int x,
    int y,
    int width,
    int height,
    uint color)
{
    const int surfaceHeight = 240;
    if (width <= 0 || height <= 0) return;

    var left = Math.Max(0, x);
    var top = Math.Max(0, y);
    var right = Math.Min(surfaceWidth, x + width);
    var bottom = Math.Min(surfaceHeight, y + height);
    for (var row = top; row < bottom; row++)
        pixels.Slice((row * surfaceWidth) + left, right - left).Fill(color);
}

static string GetLoadingGlyph(char value) => value switch
{
    'A' => ".###." + "#...#" + "#...#" + "#####" + "#...#" + "#...#" + "#...#",
    'D' => "####." + "#...#" + "#...#" + "#...#" + "#...#" + "#...#" + "####.",
    'E' => "#####" + "#...." + "#...." + "####." + "#...." + "#...." + "#####",
    'G' => ".###." + "#...#" + "#...." + "#.###" + "#...#" + "#...#" + ".###.",
    'I' => "#####" + "..#.." + "..#.." + "..#.." + "..#.." + "..#.." + "#####",
    'L' => "#...." + "#...." + "#...." + "#...." + "#...." + "#...." + "#####",
    'M' => "#...#" + "##.##" + "#.#.#" + "#.#.#" + "#...#" + "#...#" + "#...#",
    'N' => "#...#" + "##..#" + "##..#" + "#.#.#" + "#..##" + "#..##" + "#...#",
    'O' => ".###." + "#...#" + "#...#" + "#...#" + "#...#" + "#...#" + ".###.",
    'P' => "####." + "#...#" + "#...#" + "####." + "#...." + "#...." + "#....",
    'R' => "####." + "#...#" + "#...#" + "####." + "#.#.." + "#..#." + "#...#",
    'S' => ".####" + "#...." + "#...." + ".###." + "....#" + "....#" + "####.",
    'T' => "#####" + "..#.." + "..#.." + "..#.." + "..#.." + "..#.." + "..#..",
    'W' => "#...#" + "#...#" + "#...#" + "#.#.#" + "#.#.#" + "##.##" + "#...#",
    'X' => "#...#" + "#...#" + ".#.#." + "..#.." + ".#.#." + "#...#" + "#...#",
    ' ' => "....." + "....." + "....." + "....." + "....." + "....." + ".....",
    _ => string.Empty
};

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

static bool TryParseFrameRange(string value, out ulong start, out ulong end)
{
    start = 0;
    end = 0;
    var separator = value.IndexOf(':');
    if (separator <= 0 || separator == value.Length - 1) return false;
    return ulong.TryParse(value.AsSpan(0, separator), out start)
        && ulong.TryParse(value.AsSpan(separator + 1), out end)
        && start > 0
        && end >= start;
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
    public ulong CompletedFrameHash => HashPixels(_completedPixels);

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

    private static ulong HashPixels(ReadOnlySpan<uint> pixels)
    {
        const ulong offset = 14_695_981_039_346_656_037UL;
        const ulong prime = 1_099_511_628_211UL;
        var hash = offset;
        foreach (var pixel in pixels)
        {
            hash ^= (byte)pixel; hash *= prime;
            hash ^= (byte)(pixel >> 8); hash *= prime;
            hash ^= (byte)(pixel >> 16); hash *= prime;
            hash ^= (byte)(pixel >> 24); hash *= prime;
        }
        return hash;
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
