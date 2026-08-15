using System.Diagnostics;
using System.Security.Cryptography;
using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using AxetosOS.Products.NES.VirtualHardware.Components.Nes;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Machines.Nes;

namespace AxetosOS.Products.NES.Desktop;

internal sealed class NesDesktopSession
{
    private const int ResetMasterCycles = 32;
    private readonly Stopwatch _pacingTimer = new();
    private ulong _pacedMasterCycles;

    private NesDesktopSession(
        string romPath,
        VirtualNesBootHost host,
        VirtualHardwareNesRomImage image,
        byte[] romSha256,
        NesFramebufferOutput framebuffer,
        NesPcmOutput audio,
        double masterClockHz)
    {
        RomPath = romPath;
        Host = host;
        Image = image;
        RomSha256 = romSha256;
        Framebuffer = framebuffer;
        Audio = audio;
        MasterClockHz = masterClockHz;
        RestartPacing();
    }

    public string RomPath { get; }
    public string RomName => Path.GetFileNameWithoutExtension(RomPath);
    public VirtualNesBootHost Host { get; }
    public VirtualHardwareNesRomImage Image { get; }
    public byte[] RomSha256 { get; }
    public NesFramebufferOutput Framebuffer { get; }
    public NesPcmOutput Audio { get; }
    public double MasterClockHz { get; }

    public string MotherboardName => Host.Machine.ActiveMotherboard switch
    {
        ActiveNesMotherboard.Famicom => "Famicom / NTSC-J",
        ActiveNesMotherboard.NtscNes => "NES / NTSC-U",
        ActiveNesMotherboard.PalNes => "NES / PAL",
        _ => "No motherboard"
    };

    public NesRegionSelection RegionSelection => Host.Machine.ActiveMotherboard switch
    {
        ActiveNesMotherboard.Famicom => NesRegionSelection.NtscJapan,
        ActiveNesMotherboard.NtscNes => NesRegionSelection.NtscNorthAmerica,
        ActiveNesMotherboard.PalNes => NesRegionSelection.Pal,
        _ => throw new InvalidOperationException("No NES motherboard is active.")
    };

    public static Task<NesDesktopSession> LoadAsync(
        string romPath,
        Action<string>? reportProgress = null,
        NesRegionSelection regionSelection = NesRegionSelection.Auto) =>
        Task.Run(() => Load(romPath, reportProgress, regionSelection));

    public void AdvanceMasterCycles(int cycles)
    {
        Host.AdvanceMasterCycles(cycles);
        _pacedMasterCycles += (ulong)cycles;
    }

    public void PaceToHardwareClock()
    {
        var targetSeconds = _pacedMasterCycles / MasterClockHz;
        while (true)
        {
            var remaining = targetSeconds - _pacingTimer.Elapsed.TotalSeconds;
            if (remaining <= 0) return;
            if (remaining > 0.002)
            {
                Thread.Sleep(1);
            }
            else
            {
                Thread.SpinWait(64);
            }
        }
    }

    public void RestartPacing()
    {
        _pacedMasterCycles = 0;
        _pacingTimer.Restart();
    }

    public void ResetHardware()
    {
        switch (Host.Machine.ActiveMotherboard)
        {
            case ActiveNesMotherboard.Famicom:
                Host.Machine.Famicom.AssertReset();
                break;
            case ActiveNesMotherboard.NtscNes:
                Host.Machine.NtscNes.AssertReset();
                break;
            case ActiveNesMotherboard.PalNes:
                Host.Machine.PalNes.AssertReset();
                break;
            default:
                throw new InvalidOperationException("No NES motherboard is active.");
        }

        Host.AdvanceMasterCycles(ResetMasterCycles);
        Host.Machine.ReleaseReset();
        Audio.Clear();
        RestartPacing();
    }

    public void SetControllerButton(NesControllerButton button, bool pressed) =>
        Host.Machine.SetControllerButton(0, button, pressed);

    public NesDesktopQuickSaveState CaptureQuickSaveState() => new(
        Host.CaptureState(),
        Framebuffer.CaptureState(),
        Audio.CaptureState());

    public void RestoreQuickSaveState(NesDesktopQuickSaveState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Host.RestoreState(state.MachineState);
        Framebuffer.RestoreState(state.FramebufferState);
        Audio.RestoreState(state.AudioState);
        RestartPacing();
    }

    public NesDesktopPersistentMachineState CapturePersistentState() => new(
        Host.CapturePortableState(),
        Framebuffer.CaptureState(),
        Audio.CaptureState());

    public void RestorePersistentState(NesDesktopPersistentMachineState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Host.RestorePortableState(state.MachineState);
        Framebuffer.RestoreState(state.FramebufferState);
        Audio.RestoreState(state.AudioState);
        RestartPacing();
    }

    private static NesDesktopSession Load(
        string romPath,
        Action<string>? reportProgress,
        NesRegionSelection regionSelection)
    {
        var fullPath = Path.GetFullPath(romPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("NES ROM image was not found.", fullPath);
        }

        reportProgress?.Invoke("Reading ROM image...");
        var image = VirtualHardwareNesRomReader.ReadFile(fullPath);
        byte[] romSha256;
        using (var romStream = File.OpenRead(fullPath))
        {
            romSha256 = SHA256.HashData(romStream);
        }

        var host = new VirtualNesBootHost
        {
            AutomaticCompiledExecutionEnabled = true
        };

        reportProgress?.Invoke("Selecting NES hardware region...");
        var resolvedRegion = NesHardwareRegionResolver.Resolve(
            image,
            Path.GetFileName(fullPath),
            regionSelection);

        reportProgress?.Invoke($"Compiling {DescribeRegion(resolvedRegion.Region)} hardware for cartridge...");
        host.LoadRom(image, Path.GetFileName(fullPath), regionSelection, PalCicVariant.PalA3195);

        reportProgress?.Invoke("Connecting video and audio output...");
        var framebuffer = new NesFramebufferOutput();
        host.VideoSink = framebuffer;

        var masterClockHz = host.Machine.ActiveMotherboard switch
        {
            ActiveNesMotherboard.Famicom => (double)FamicomMotherboard.MasterClockHertz,
            ActiveNesMotherboard.NtscNes => NtscNesMotherboard.MasterClockHertz,
            ActiveNesMotherboard.PalNes => PalNesMotherboard.MasterClockHertz,
            _ => throw new InvalidOperationException("No NES motherboard is active after ROM loading.")
        };
        var audio = new NesPcmOutput(masterClockHz, NesDesktopApplication.AudioSampleRate);
        host.AudioSink = audio;

        reportProgress?.Invoke("Powering NES and releasing reset...");
        host.PowerAndReleaseReset();
        reportProgress?.Invoke("Starting emulation...");

        return new NesDesktopSession(fullPath, host, image, romSha256, framebuffer, audio, masterClockHz);
    }

    private static string DescribeRegion(NesHardwareRegion region) => region switch
    {
        NesHardwareRegion.NtscJapan => "Famicom / NTSC-J",
        NesHardwareRegion.NtscNorthAmerica => "NES / NTSC-U",
        NesHardwareRegion.Pal => "NES / PAL",
        _ => region.ToString()
    };
}

internal sealed record NesDesktopQuickSaveState(
    VirtualNesMachineState MachineState,
    NesFramebufferState FramebufferState,
    NesPcmState AudioState);

internal sealed record NesDesktopPersistentMachineState(
    byte[] MachineState,
    NesFramebufferState FramebufferState,
    NesPcmState AudioState);
