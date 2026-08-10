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

var host = new VirtualNesBootHost
{
    AutomaticCompiledExecutionEnabled = !rawHardwareRuntime
};
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
var physicalAudioPlayback = !uncappedRuntime;
if (physicalAudioPlayback) audio.Start();

presenter.KeyStateChanged += (key, pressed) =>
{
    if (key == NativeKey.Escape)
    {
        if (pressed) presenter.Close();
        return;
    }

    if (TryMapController1Key(key, out var button))
        host.Machine.SetControllerButton(0, button, pressed);
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
