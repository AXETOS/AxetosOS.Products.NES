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
for (var index = 0; index < args.Length; index++)
{
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
        Console.Error.WriteLine("Usage: DesktopHost [rom-path] [--board famicom|ntsc|pal-a|pal-b|auto]");
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

const int MasterCyclesPerVideoBatch = 2_048;
const int AudioTransferBufferSize = 4_096;
var audioTransfer = new float[AudioTransferBufferSize];
var timer = Stopwatch.StartNew();
var lastPresentedFrame = ulong.MaxValue;
var lastTitleUpdate = TimeSpan.Zero;

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
        presenter.SetTitle(
            $"{Path.GetFileNameWithoutExtension(romPath)} | {diagnostics.Motherboard} | " +
            $"Frame {diagnostics.PpuFrames:N0} | PC ${diagnostics.ProgramCounter:X4} | " +
            $"CPU {diagnostics.CpuInstructions:N0} | Audio {audio.BufferedMilliseconds:F0} ms");
        lastTitleUpdate = now;
    }

    if (audio.BufferedMilliseconds > 120) Thread.Sleep(1);
}

var final = host.Snapshot();
Console.WriteLine($"Stopped:     master={final.MasterCycles:N0}, instructions={final.CpuInstructions:N0}, frames={final.PpuFrames:N0}");
Console.WriteLine($"Boot checks: reset-vector={final.ResetVectorObserved}, opcode={final.FirstOpcodeObserved}, vblank={final.FirstVblankObserved}, nmi={final.FirstNmiObserved}");
return 0;

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

sealed class NativePcmAudioSink(double masterClockHz, int sampleRate) : IVirtualNesAudioSink
{
    private readonly Queue<float> _samples = new();
    private readonly double _masterCyclesPerSample = masterClockHz / sampleRate;
    private double _nextSampleCycle;

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
