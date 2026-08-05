using System.Diagnostics;
using AxetosOS.Audio.Windows;
using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
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
for (var index = 0; index < args.Length; index++)
{
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
        Console.Error.WriteLine("Usage: DesktopHost [rom-path] [--board famicom|ntsc|pal-a|pal-b|auto] [--profile]");
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
Console.WriteLine($"Kernel:      {(activeSimulator.UsesStrictEventKernel ? "strict indexed event queue" : "compatibility polling")}");
if (!activeSimulator.UsesStrictEventKernel)
    Console.WriteLine($"Legacy:      {string.Join(", ", activeSimulator.LegacyPollingComponents)}");
if (profileSimulation) Console.WriteLine("Profiler:    enabled; component timing sampled 1/256; results every 5 seconds");

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
var lastInstructionProgress = host.Snapshot().CpuInstructions;
var lastInstructionProgressFrame = host.Snapshot().PpuFrames;
var stallReportedForState = string.Empty;
var lastProfileReport = TimeSpan.Zero;

while (presenter.IsOpen)
{
    presenter.PumpEvents();
    if (!presenter.IsOpen) break;

    host.AdvanceMasterCycles(MasterCyclesPerVideoBatch);

    if (videoSink.CompletedFrame != lastPresentedFrame)
    {
        videoSink.Pixels.AsSpan().CopyTo(surface.PixelSpan);
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
if (profileSimulation) PrintProfile(activeSimulator.GetProfileSnapshot());
return 0;

static void PrintProfile(AxetosOS.Products.NES.VirtualHardware.Simulation.VirtualHardwareSimulationProfile profile)
{
    Console.WriteLine($"PROFILE: board={profile.BoardId}; settles={profile.SettleCalls:N0}; passes={profile.PropagationPasses:N0}; avg={profile.AveragePassesPerSettle:F2}; max={profile.MaximumPassesPerSettle}; settle={profile.TotalSettleTime.TotalSeconds:F2}s; nets={profile.NetResolutionTime.TotalSeconds:F2}s");
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
    public uint[] Pixels { get; } = new uint[Width * Height];
    public ulong CompletedFrame { get; private set; }

    public void AcceptPixel(ulong frame, int x, int y, byte colorCode, byte emphasis)
    {
        if ((uint)x >= Width || (uint)y >= Height) return;
        Pixels[(y * Width) + x] = ApplyEmphasis(Palette[colorCode & 0x3F], emphasis);
        if (x == Width - 1 && y == Height - 1) CompletedFrame = frame;
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

sealed class NativePcmAudioSink(double masterClockHz, int sampleRate) : IVirtualNesScheduledAudioSink
{
    private readonly Queue<float> _samples = new();
    private readonly double _masterCyclesPerSample = masterClockHz / sampleRate;
    private double _nextSampleCycle;

    public ulong NextRequiredMasterCycle => (ulong)Math.Ceiling(_nextSampleCycle);

    public void AcceptSample(ulong masterCycle, byte dacLevel)
    {
        if (masterCycle < _nextSampleCycle) return;
        _nextSampleCycle += _masterCyclesPerSample;
        var normalized = (dacLevel / 127.5f) - 1.0f;
        _samples.Enqueue(Math.Clamp(normalized, -1.0f, 1.0f));
    }

    public int Drain(float[] destination)
    {
        var count = Math.Min(destination.Length, _samples.Count);
        for (var index = 0; index < count; index++) destination[index] = _samples.Dequeue();
        return count;
    }
}
