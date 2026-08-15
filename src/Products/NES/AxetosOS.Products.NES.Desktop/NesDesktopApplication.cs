using System.Collections.Concurrent;
using System.Diagnostics;
using AxetosOS.Products.NES.Host.Windows;
using AxetosOS.Products.NES.VirtualHardware.Components.Nes;

namespace AxetosOS.Products.NES.Desktop;

internal sealed class NesDesktopApplication : IDisposable
{
    public const int AudioSampleRate = 44_100;
    private const int ScreenWidth = 256;
    private const int ScreenHeight = 240;
    private const int MasterCyclesPerBatch = 16_384;
    private const int AudioTransferBufferSize = 4_096;
    private const int StatusBarHeight = 24;

    private readonly Win32FramePresenter _presenter;
    private readonly Win32WaveOutAudioSink _audioDevice;
    private readonly FrameSurface _surface = new(ScreenWidth, ScreenHeight);
    private readonly float[] _audioTransfer = new float[AudioTransferBufferSize];
    private readonly ConcurrentQueue<NesShellCommand> _commands = new();
    private readonly ConcurrentQueue<string> _loadProgress = new();
    private readonly Stopwatch _runtimeClock = Stopwatch.StartNew();
    private NesDesktopSession? _session;
    private NesDesktopQuickSaveState? _quickSave;
    private bool _paused;
    private ulong _lastPresentedFrame = ulong.MaxValue;
    private ulong _fpsSampleFrame;
    private TimeSpan _fpsSampleTime;
    private double _displayedFps;
    private string? _transientStatus;
    private TimeSpan _transientStatusUntil;
    private TimeSpan _lastStatusUpdate;
    private bool _disposed;

    private NesDesktopApplication()
    {
        _surface.PixelSpan.Fill(0xFF000000u);

        _presenter = new Win32FramePresenter(
            "AxetosOS NES",
            ScreenWidth * 3,
            (ScreenHeight * 3) + StatusBarHeight);
        _presenter.SetApplicationMenu(BuildMenu());
        _presenter.SetStatusBar("Starting AxetosOS NES...");
        _presenter.KeyChanged += OnKeyChanged;
        _presenter.CommandInvoked += OnMenuCommand;
        _presenter.PresentApplicationMessage(
            "Starting AxetosOS NES",
            "Loading, please wait...");

        _audioDevice = new Win32WaveOutAudioSink(AudioSampleRate);
        _audioDevice.Start();
        _presenter.SetStatusBar("Ready — Ctrl+O Load ROM | F11 Fullscreen");
    }

    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The AxetosOS NES desktop shell requires Windows.");
            return 1;
        }

        if (args.Length > 1)
        {
            Console.Error.WriteLine("Usage: AxetosOS.Products.NES.Desktop [rom-or-state-path]");
            return 2;
        }

        using var application = new NesDesktopApplication();
        if (args.Length == 1)
        {
            var path = args[0];
            var loaded = string.Equals(
                Path.GetExtension(path),
                NesPersistentStateStore.FileExtension,
                StringComparison.OrdinalIgnoreCase)
                ? application.TryLoadPersistentState(path)
                : application.TryLoadRom(path);
            if (!loaded) return 3;
        }

        application.RunLoop();
        return 0;
    }

    private void RunLoop()
    {
        while (_presenter.IsOpen)
        {
            _presenter.PumpEvents();
            if (!_presenter.IsOpen) break;

            ProcessCommands();
            if (!_presenter.IsOpen) break;

            if (_session is not null && !_paused)
            {
                _session.AdvanceMasterCycles(MasterCyclesPerBatch);
                DrainAudio();
                PresentCompletedFrame();
                _session.PaceToHardwareClock();
            }
            else
            {
                _presenter.Present(_surface, ScalingMode.IntegerNearest);
                Thread.Sleep(8);
            }

            UpdateStatusBar();
        }
    }

    private void ProcessCommands()
    {
        while (_commands.TryDequeue(out var command))
        {
            switch (command)
            {
                case NesShellCommand.OpenRom:
                    OpenRom();
                    break;
                case NesShellCommand.SaveState:
                    SavePersistentState();
                    break;
                case NesShellCommand.LoadState:
                    LoadPersistentState();
                    break;
                case NesShellCommand.QuickSave:
                    QuickSave();
                    break;
                case NesShellCommand.QuickLoad:
                    QuickLoad();
                    break;
                case NesShellCommand.Reset:
                    Reset();
                    break;
                case NesShellCommand.TogglePause:
                    TogglePause();
                    break;
                case NesShellCommand.ToggleFullscreen:
                    _presenter.ToggleFullscreen();
                    UpdateStatusBar(force: true);
                    break;
                case NesShellCommand.LeaveFullscreen:
                    if (_presenter.IsFullscreen)
                    {
                        _presenter.ExitFullscreen();
                        UpdateStatusBar(force: true);
                    }
                    break;
                case NesShellCommand.Exit:
                    _presenter.Close();
                    break;
            }
        }
    }

    private void OpenRom()
    {
        var selected = NativeFileDialog.OpenFile(
            "Open NES cartridge image",
            "NES cartridge images (*.nes)|*.nes|All files (*.*)|*.*",
            defaultExtension: "nes");
        if (!string.IsNullOrWhiteSpace(selected))
        {
            TryLoadRom(selected);
        }
    }

    private bool TryLoadRom(string romPath)
    {
        var previousPaused = _paused;
        _paused = true;

        while (_loadProgress.TryDequeue(out _))
        {
        }

        var romFileName = Path.GetFileName(romPath);
        var currentProgress = "Preparing ROM...";
        PresentRomLoading(romFileName, currentProgress);

        var loadTask = NesDesktopSession.LoadAsync(romPath, progress => _loadProgress.Enqueue(progress));
        while (!loadTask.IsCompleted && _presenter.IsOpen)
        {
            _presenter.PumpEvents();
            while (_loadProgress.TryDequeue(out var progress))
            {
                currentProgress = progress;
            }

            PresentRomLoading(romFileName, currentProgress);
            Thread.Sleep(16);
        }

        if (!_presenter.IsOpen)
        {
            return false;
        }

        while (_loadProgress.TryDequeue(out var finalProgress))
        {
            currentProgress = finalProgress;
        }
        PresentRomLoading(romFileName, currentProgress);

        NesDesktopSession loadedSession;
        try
        {
            loadedSession = loadTask.GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or InvalidOperationException)
        {
            RestoreAfterFailedLoad(previousPaused);
            NativeMessageDialog.ShowError("Could not load NES ROM", exception.Message);
            return false;
        }

        ActivateSession(loadedSession, restoredState: false);
        return true;
    }

    private void SavePersistentState()
    {
        var session = _session;
        if (session is null)
        {
            SetTransientStatus("Save State ignored — no ROM is loaded.");
            return;
        }

        var savedAt = DateTimeOffset.Now;
        string saveDirectory;
        try
        {
            saveDirectory = NesPersistentStateStore.DefaultSaveDirectory;
            Directory.CreateDirectory(saveDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            NativeMessageDialog.ShowError("Could not prepare NES save-state folder", exception.Message);
            return;
        }

        var selected = NativeFileDialog.SaveFile(
            "Save NES state",
            $"AxetosOS NES save states (*{NesPersistentStateStore.FileExtension})|*{NesPersistentStateStore.FileExtension}|All files (*.*)|*.*",
            initialDirectory: saveDirectory,
            defaultExtension: NesPersistentStateStore.FileExtension.TrimStart('.'),
            defaultFileName: NesPersistentStateStore.CreateDefaultFileName(session.RomName, savedAt));
        if (string.IsNullOrWhiteSpace(selected)) return;

        var previousPaused = _paused;
        _paused = true;
        PresentStateOperation(
            "Saving Game State",
            Path.GetFileName(selected),
            session.RomName,
            "Capturing complete NES machine state...");

        var started = Stopwatch.GetTimestamp();
        try
        {
            var state = NesPersistentStateStore.Create(session, savedAt);
            PresentStateOperation(
                "Saving Game State",
                Path.GetFileName(selected),
                session.RomName,
                "Writing verified save-state file...");
            NesPersistentStateStore.Save(selected, state);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            NotSupportedException or
            InvalidOperationException)
        {
            _paused = previousPaused;
            if (!_paused) session.RestartPacing();
            _transientStatus = null;
            _presenter.Present(_surface, ScalingMode.IntegerNearest);
            NativeMessageDialog.ShowError("Could not save NES state", exception.Message);
            UpdateStatusBar(force: true);
            return;
        }

        _paused = previousPaused;
        if (!_paused) session.RestartPacing();
        _presenter.Present(_surface, ScalingMode.IntegerNearest);
        var elapsed = Stopwatch.GetElapsedTime(started);
        SetTransientStatus(
            $"Saved state — {Path.GetFileName(selected)} ({elapsed.TotalMilliseconds:F0} ms).",
            TimeSpan.FromSeconds(5));
    }

    private void LoadPersistentState()
    {
        string? initialDirectory = null;
        try
        {
            initialDirectory = NesPersistentStateStore.DefaultSaveDirectory;
            Directory.CreateDirectory(initialDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            NativeMessageDialog.ShowError("Could not prepare NES save-state folder", exception.Message);
        }

        var selected = NativeFileDialog.OpenFile(
            "Load NES state",
            $"AxetosOS NES save states (*{NesPersistentStateStore.FileExtension})|*{NesPersistentStateStore.FileExtension}|All files (*.*)|*.*",
            initialDirectory: initialDirectory,
            defaultExtension: NesPersistentStateStore.FileExtension.TrimStart('.'));
        if (!string.IsNullOrWhiteSpace(selected))
        {
            TryLoadPersistentState(selected);
        }
    }

    private bool TryLoadPersistentState(string statePath)
    {
        var previousPaused = _paused;
        _paused = true;
        var stateFileName = Path.GetFileName(statePath);
        PresentStateOperation("Loading Saved Game", stateFileName, null, "Reading and verifying save-state file...");

        NesPersistentStateFile saved;
        try
        {
            saved = NesPersistentStateStore.Load(statePath);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            NotSupportedException or
            InvalidOperationException)
        {
            RestoreAfterFailedLoad(previousPaused);
            NativeMessageDialog.ShowError("Could not load NES state", exception.Message);
            return false;
        }

        // If the exact ROM is already running, restore directly into that machine.
        // Otherwise the save-state file is an entry point: resolve the ROM, compile
        // a fresh machine, then restore the persisted physical state into it.
        if (_session is not null &&
            _session.RomSha256.AsSpan().SequenceEqual(saved.RomSha256) &&
            _session.RegionSelection == saved.RegionSelection)
        {
            if (!ValidateSavedRomMetadata(_session, saved, out var metadataError))
            {
                RestoreAfterFailedLoad(previousPaused);
                NativeMessageDialog.ShowError("Could not load NES state", metadataError);
                return false;
            }

            PresentStateOperation("Loading Saved Game", stateFileName, saved.RomFileName, "Restoring saved NES machine state...");
            var rollback = _session.CaptureQuickSaveState();
            try
            {
                _session.RestorePersistentState(new NesDesktopPersistentMachineState(
                    saved.MachineState,
                    saved.FramebufferState,
                    saved.AudioState));
            }
            catch (Exception exception) when (
                exception is IOException or
                InvalidDataException or
                NotSupportedException or
                InvalidOperationException)
            {
                try
                {
                    _session.RestoreQuickSaveState(rollback);
                }
                catch
                {
                    // Preserve the original restore error; this fallback exists only
                    // to keep the currently running game intact when possible.
                }
                RestoreAfterFailedLoad(previousPaused);
                NativeMessageDialog.ShowError("Could not restore NES state", exception.Message);
                return false;
            }

            ActivateSession(_session, restoredState: true);
            SetTransientStatus(
                $"Loaded state — {saved.RomFileName} — saved {saved.SavedAtUtc.ToLocalTime():g}.",
                TimeSpan.FromSeconds(5));
            return true;
        }

        PresentStateOperation("Loading Saved Game", stateFileName, saved.RomFileName, "Locating matching ROM image...");
        var romPath = ResolveRomForSavedState(saved, statePath);
        if (string.IsNullOrWhiteSpace(romPath))
        {
            RestoreAfterFailedLoad(previousPaused);
            SetTransientStatus("Load State cancelled — matching ROM was not selected.");
            return false;
        }

        while (_loadProgress.TryDequeue(out _))
        {
        }

        var currentProgress = "Preparing ROM...";
        PresentStateOperation("Loading Saved Game", stateFileName, saved.RomFileName, currentProgress);
        var loadTask = NesDesktopSession.LoadAsync(
            romPath,
            progress => _loadProgress.Enqueue(progress),
            saved.RegionSelection);
        while (!loadTask.IsCompleted && _presenter.IsOpen)
        {
            _presenter.PumpEvents();
            while (_loadProgress.TryDequeue(out var progress))
            {
                currentProgress = progress;
            }

            PresentStateOperation("Loading Saved Game", stateFileName, saved.RomFileName, currentProgress);
            Thread.Sleep(16);
        }

        if (!_presenter.IsOpen) return false;

        NesDesktopSession loadedSession;
        try
        {
            loadedSession = loadTask.GetAwaiter().GetResult();
            if (!loadedSession.RomSha256.AsSpan().SequenceEqual(saved.RomSha256))
                throw new InvalidDataException("The ROM loaded for this state does not match the saved ROM SHA-256 identity.");
            if (!ValidateSavedRomMetadata(loadedSession, saved, out var metadataError))
                throw new InvalidDataException(metadataError);

            PresentStateOperation("Loading Saved Game", stateFileName, saved.RomFileName, "Restoring saved NES machine state...");
            loadedSession.RestorePersistentState(new NesDesktopPersistentMachineState(
                saved.MachineState,
                saved.FramebufferState,
                saved.AudioState));
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            NotSupportedException or
            InvalidOperationException)
        {
            RestoreAfterFailedLoad(previousPaused);
            NativeMessageDialog.ShowError("Could not load NES state", exception.Message);
            return false;
        }

        ActivateSession(loadedSession, restoredState: true);
        SetTransientStatus(
            $"Loaded state — {saved.RomFileName} — saved {saved.SavedAtUtc.ToLocalTime():g}.",
            TimeSpan.FromSeconds(5));
        return true;
    }

    private string? ResolveRomForSavedState(NesPersistentStateFile saved, string statePath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(saved.OriginalRomPath)) candidates.Add(saved.OriginalRomPath);

        var stateDirectory = Path.GetDirectoryName(Path.GetFullPath(statePath));
        var safeRomFileName = Path.GetFileName(saved.RomFileName);
        if (!string.IsNullOrWhiteSpace(stateDirectory) && !string.IsNullOrWhiteSpace(safeRomFileName))
        {
            candidates.Add(Path.Combine(stateDirectory, safeRomFileName));
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (SafeRomHashMatches(candidate, saved.RomSha256)) return candidate;
        }

        while (_presenter.IsOpen)
        {
            var selected = NativeFileDialog.OpenFile(
                $"Locate ROM for {safeRomFileName}",
                "NES cartridge images (*.nes)|*.nes|All files (*.*)|*.*",
                defaultExtension: "nes");
            if (string.IsNullOrWhiteSpace(selected)) return null;
            if (SafeRomHashMatches(selected, saved.RomSha256)) return selected;

            NativeMessageDialog.ShowError(
                "ROM does not match saved game",
                $"The selected ROM is not the exact cartridge image used by this save state.\n\n" +
                $"Required ROM: {safeRomFileName}\n" +
                "Please select the matching ROM, or cancel the dialog.");
        }

        return null;
    }

    private static bool SafeRomHashMatches(string path, byte[] expectedSha256)
    {
        try
        {
            return NesPersistentStateStore.HashMatches(path, expectedSha256);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool ValidateSavedRomMetadata(
        NesDesktopSession session,
        NesPersistentStateFile saved,
        out string error)
    {
        if (session.Image.MapperNumber != saved.MapperNumber)
        {
            error = $"The save requires mapper {saved.MapperNumber}, but the loaded ROM reports mapper {session.Image.MapperNumber}.";
            return false;
        }

        if (session.Image.SubmapperNumber != saved.SubmapperNumber)
        {
            error = $"The save requires submapper {saved.SubmapperNumber?.ToString() ?? "none"}, " +
                    $"but the loaded ROM reports {session.Image.SubmapperNumber?.ToString() ?? "none"}.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private void ActivateSession(NesDesktopSession session, bool restoredState)
    {
        _session = session;
        _quickSave = null;
        if (!restoredState) _surface.PixelSpan.Fill(0xFF000000u);
        _lastPresentedFrame = ulong.MaxValue;
        _fpsSampleFrame = session.Framebuffer.CompletedFrame == ulong.MaxValue
            ? 0
            : session.Framebuffer.CompletedFrame;
        _fpsSampleTime = _runtimeClock.Elapsed;
        _displayedFps = 0;
        _paused = false;
        _transientStatus = null;
        _presenter.SetTitle($"AxetosOS NES — {session.RomName}");
        session.RestartPacing();

        if (restoredState)
        {
            if (session.Framebuffer.CompletedFrame == ulong.MaxValue)
            {
                _surface.PixelSpan.Fill(0xFF000000u);
                _presenter.Present(_surface, ScalingMode.IntegerNearest);
            }
            else
            {
                PresentCompletedFrame();
            }
        }

        UpdateStatusBar(force: true);
    }

    private void RestoreAfterFailedLoad(bool previousPaused)
    {
        _paused = previousPaused;
        _transientStatus = null;
        if (_session is not null && !_paused) _session.RestartPacing();
        _presenter.Present(_surface, ScalingMode.IntegerNearest);
        UpdateStatusBar(force: true);
    }

    private void PresentStateOperation(string title, string stateFileName, string? romFileName, string progress)
    {
        var waitDots = 1 + ((int)(_runtimeClock.Elapsed.TotalMilliseconds / 350) % 3);
        var waitText = $"Please wait{new string('.', waitDots)}";
        var romLine = string.IsNullOrWhiteSpace(romFileName) ? string.Empty : $"\nROM: {Path.GetFileName(romFileName)}";

        _transientStatus = $"{title} — {progress}";
        _transientStatusUntil = _runtimeClock.Elapsed + TimeSpan.FromMinutes(1);
        UpdateStatusBar(force: true);
        _presenter.PresentApplicationMessage(
            title,
            $"{stateFileName}{romLine}\n\n{progress}\n{waitText}");
    }

    private void PresentRomLoading(string romFileName, string progress)
    {
        var waitDots = 1 + ((int)(_runtimeClock.Elapsed.TotalMilliseconds / 350) % 3);
        var waitText = $"Please wait{new string('.', waitDots)}";

        _transientStatus = $"Loading {romFileName} — {progress}";
        _transientStatusUntil = _runtimeClock.Elapsed + TimeSpan.FromMinutes(1);
        UpdateStatusBar(force: true);
        _presenter.PresentApplicationMessage(
            "Loading ROM",
            $"{romFileName}\n\n{progress}\n{waitText}");
    }

    private void Reset()
    {
        if (_session is null)
        {
            SetTransientStatus("Reset ignored — no ROM is loaded.");
            return;
        }

        _session.ResetHardware();
        _paused = false;
        _lastPresentedFrame = ulong.MaxValue;
        SetTransientStatus("NES reset line asserted and released.");
    }

    private void TogglePause()
    {
        if (_session is null)
        {
            SetTransientStatus("Pause ignored — no ROM is loaded.");
            return;
        }

        _paused = !_paused;
        if (!_paused)
        {
            _session.RestartPacing();
        }
        SetTransientStatus(_paused ? "Paused." : "Resumed.");
    }

    private void QuickSave()
    {
        if (_session is null)
        {
            SetTransientStatus("Quick Save ignored — no ROM is loaded.");
            return;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            _quickSave = _session.CaptureQuickSaveState();
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            NativeMessageDialog.ShowError("Could not create NES quick save", exception.Message);
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        var frame = _session.Framebuffer.CompletedFrame;
        var frameText = frame == ulong.MaxValue ? "before first complete frame" : $"frame {frame:N0}";
        SetTransientStatus($"Quick Save stored — {frameText} ({elapsed.TotalMilliseconds:F0} ms).", TimeSpan.FromSeconds(4));
    }

    private void QuickLoad()
    {
        if (_session is null)
        {
            SetTransientStatus("Quick Load ignored — no ROM is loaded.");
            return;
        }
        var quickSave = _quickSave;
        if (quickSave is null)
        {
            SetTransientStatus("Quick Load ignored — press F5 to create a quick save first.", TimeSpan.FromSeconds(4));
            return;
        }

        var remainPaused = _paused;
        _paused = true;
        var started = Stopwatch.GetTimestamp();
        try
        {
            _session.RestoreQuickSaveState(quickSave);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            _paused = remainPaused;
            NativeMessageDialog.ShowError("Could not restore NES quick save", exception.Message);
            return;
        }

        _lastPresentedFrame = ulong.MaxValue;
        _fpsSampleFrame = _session.Framebuffer.CompletedFrame == ulong.MaxValue
            ? 0
            : _session.Framebuffer.CompletedFrame;
        _fpsSampleTime = _runtimeClock.Elapsed;
        _displayedFps = 0;
        PresentCompletedFrame();
        _paused = remainPaused;
        if (!_paused) _session.RestartPacing();

        var elapsed = Stopwatch.GetElapsedTime(started);
        var frame = _session.Framebuffer.CompletedFrame;
        var frameText = frame == ulong.MaxValue ? "saved point" : $"frame {frame:N0}";
        SetTransientStatus($"Quick Load restored — {frameText} ({elapsed.TotalMilliseconds:F0} ms).", TimeSpan.FromSeconds(4));
    }

    private void PresentCompletedFrame()
    {
        if (_session is null || _session.Framebuffer.CompletedFrame == ulong.MaxValue) return;
        if (_session.Framebuffer.CompletedFrame == _lastPresentedFrame) return;

        _session.Framebuffer.CompletedPixels.Span.CopyTo(_surface.PixelSpan);
        _presenter.Present(_surface, ScalingMode.IntegerNearest);
        _lastPresentedFrame = _session.Framebuffer.CompletedFrame;
    }

    private void DrainAudio()
    {
        if (_session is null) return;
        int drained;
        while ((drained = _session.Audio.Drain(_audioTransfer)) > 0)
        {
            _audioDevice.Submit(_audioTransfer.AsSpan(0, drained));
        }
    }

    private void UpdateStatusBar(bool force = false)
    {
        var now = _runtimeClock.Elapsed;
        if (!force && now - _lastStatusUpdate < TimeSpan.FromMilliseconds(250)) return;
        _lastStatusUpdate = now;

        if (_transientStatus is not null && now < _transientStatusUntil)
        {
            _presenter.SetStatusBar(_transientStatus);
            return;
        }
        _transientStatus = null;

        if (_session is null)
        {
            _presenter.SetStatusBar("Ready — Ctrl+O Load ROM | F11 Fullscreen");
            return;
        }

        UpdateFps(now);
        var state = _paused ? "Paused" : "Running";
        _presenter.SetStatusBar(
            $"{state} | {_session.RomName} | Mapper {_session.Image.MapperNumber} | {_session.MotherboardName} | {_displayedFps:F1} FPS");
    }

    private void UpdateFps(TimeSpan now)
    {
        if (_session is null || _session.Framebuffer.CompletedFrame == ulong.MaxValue) return;
        if (_fpsSampleTime == TimeSpan.Zero)
        {
            _fpsSampleTime = now;
            _fpsSampleFrame = _session.Framebuffer.CompletedFrame;
            return;
        }

        var elapsed = now - _fpsSampleTime;
        if (elapsed < TimeSpan.FromSeconds(1)) return;

        var frame = _session.Framebuffer.CompletedFrame;
        _displayedFps = frame >= _fpsSampleFrame
            ? (frame - _fpsSampleFrame) / elapsed.TotalSeconds
            : 0;
        _fpsSampleFrame = frame;
        _fpsSampleTime = now;
    }

    private void SetTransientStatus(string message, TimeSpan? duration = null)
    {
        _transientStatus = message;
        _transientStatusUntil = _runtimeClock.Elapsed + (duration ?? TimeSpan.FromSeconds(3));
        UpdateStatusBar(force: true);
    }

    private void OnKeyChanged(NativeKeyEvent input)
    {
        if (TryMapController1Key(input.Key, out var button))
        {
            _session?.SetControllerButton(button, input.Pressed);
            return;
        }

        if (!input.Pressed || input.IsRepeat) return;

        if (input.Key == NativeKey.Escape)
        {
            _commands.Enqueue(NesShellCommand.LeaveFullscreen);
            return;
        }

        if (input.Key == NativeKey.F11)
        {
            _commands.Enqueue(NesShellCommand.ToggleFullscreen);
            return;
        }

        if (input.Key == NativeKey.F5)
        {
            _commands.Enqueue(NesShellCommand.QuickSave);
            return;
        }

        if (input.Key == NativeKey.F7)
        {
            _commands.Enqueue(NesShellCommand.QuickLoad);
            return;
        }

        if (input.Key == NativeKey.Space)
        {
            _commands.Enqueue(NesShellCommand.TogglePause);
            return;
        }

        if ((input.Modifiers & NativeKeyModifiers.Control) == 0) return;

        if (input.Key == NativeKey.O)
        {
            _commands.Enqueue(NesShellCommand.OpenRom);
        }
        else if (input.Key == NativeKey.R)
        {
            _commands.Enqueue(NesShellCommand.Reset);
        }
    }

    private void OnMenuCommand(int commandId)
    {
        var command = commandId switch
        {
            NesShellCommandIds.OpenRom => NesShellCommand.OpenRom,
            NesShellCommandIds.SaveState => NesShellCommand.SaveState,
            NesShellCommandIds.LoadState => NesShellCommand.LoadState,
            NesShellCommandIds.QuickSave => NesShellCommand.QuickSave,
            NesShellCommandIds.QuickLoad => NesShellCommand.QuickLoad,
            NesShellCommandIds.Exit => NesShellCommand.Exit,
            NesShellCommandIds.Reset => NesShellCommand.Reset,
            NesShellCommandIds.TogglePause => NesShellCommand.TogglePause,
            NesShellCommandIds.ToggleFullscreen => NesShellCommand.ToggleFullscreen,
            _ => (NesShellCommand?)null
        };

        if (command.HasValue)
        {
            _commands.Enqueue(command.Value);
        }
    }

    private static IReadOnlyList<NativeApplicationMenuGroup> BuildMenu() =>
    [
        new NativeApplicationMenuGroup(
            "&File",
            [
                new NativeApplicationMenuItem(NesShellCommandIds.OpenRom, "&Open ROM...\tCtrl+O"),
                NativeApplicationMenuItem.Separator(),
                new NativeApplicationMenuItem(NesShellCommandIds.SaveState, "&Save State..."),
                new NativeApplicationMenuItem(NesShellCommandIds.LoadState, "&Load State..."),
                NativeApplicationMenuItem.Separator(),
                new NativeApplicationMenuItem(NesShellCommandIds.Exit, "E&xit")
            ]),
        new NativeApplicationMenuGroup(
            "&Emulation",
            [
                new NativeApplicationMenuItem(NesShellCommandIds.QuickSave, "Quick &Save\tF5"),
                new NativeApplicationMenuItem(NesShellCommandIds.QuickLoad, "Quick &Load\tF7"),
                NativeApplicationMenuItem.Separator(),
                new NativeApplicationMenuItem(NesShellCommandIds.Reset, "&Reset\tCtrl+R"),
                new NativeApplicationMenuItem(NesShellCommandIds.TogglePause, "&Pause / Resume\tSpace")
            ]),
        new NativeApplicationMenuGroup(
            "&View",
            [
                new NativeApplicationMenuItem(NesShellCommandIds.ToggleFullscreen, "&Fullscreen\tF11")
            ])
    ];

    private static bool TryMapController1Key(NativeKey key, out NesControllerButton button)
    {
        button = key switch
        {
            NativeKey.Up => NesControllerButton.Up,
            NativeKey.Down => NesControllerButton.Down,
            NativeKey.Left => NesControllerButton.Left,
            NativeKey.Right => NesControllerButton.Right,
            NativeKey.Z => NesControllerButton.A,
            NativeKey.X => NesControllerButton.B,
            NativeKey.Enter => NesControllerButton.Start,
            NativeKey.RightShift => NesControllerButton.Select,
            _ => (NesControllerButton)(-1)
        };
        return Enum.IsDefined(button);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _audioDevice.Dispose();
        _presenter.Dispose();
    }
}
