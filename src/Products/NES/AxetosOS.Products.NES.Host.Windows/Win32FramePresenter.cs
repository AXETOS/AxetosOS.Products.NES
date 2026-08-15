using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AxetosOS.Products.NES.Host.Windows;

public sealed class Win32FramePresenter : IFramePresenter
{
    private const int CwUseDefault = unchecked((int)0x80000000);
    private const int GwlStyle = -16;
    private const uint CsOwnDc = 0x0020;
    private const uint WsOverlappedWindow = 0x00CF0000;
    private const uint WsVisible = 0x10000000;
    private const uint PmRemove = 0x0001;
    private const uint WmDestroy = 0x0002;
    private const uint WmEraseBackground = 0x0014;
    private const uint WmClose = 0x0010;
    private const uint WmCommand = 0x0111;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint MfString = 0x0000;
    private const uint MfPopup = 0x0010;
    private const uint MfSeparator = 0x0800;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint SwpFrameChanged = 0x0020;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int VkControl = 0x11;
    private const int VkShift = 0x10;
    private const int VkMenu = 0x12;
    private const int ColorBtnFace = 15;
    private const int ColorBtnText = 18;
    private const int Transparent = 1;
    private const int BlackBrush = 4;
    private const int DefaultGuiFont = 17;
    private const uint WhiteColorRef = 0x00FFFFFF;
    private const uint DtCenter = 0x0001;
    private const uint DtVCenter = 0x0004;
    private const uint DtWordBreak = 0x0010;
    private const uint DtSingleLine = 0x0020;
    private const uint DtEndEllipsis = 0x00008000;
    private const uint DtNoPrefix = 0x00000800;
    private const uint DibRgbColors = 0;
    private const uint Srccopy = 0x00CC0020;
    private const int DefaultStatusBarHeight = 24;
    private static readonly nint HgdiError = new(-1);
    private const int ColorOnColor = 3;

    private readonly string _className = $"AxetosOS.FramePresenter.{Guid.NewGuid():N}";
    private readonly WndProc _windowProcedure;
    private readonly nint _instance;
    private nint _window;
    private nint _menu;
    private nint _backBufferDc;
    private nint _backBufferBitmap;
    private nint _backBufferPreviousBitmap;
    private int _backBufferWidth;
    private int _backBufferHeight;
    private string _statusText = string.Empty;
    private bool _statusBarVisible;
    private bool _fullscreen;
    private nint _windowedStyle;
    private WindowPlacement _windowedPlacement;
    private bool _hasWindowedPlacement;
    private bool _disposed;

    public Win32FramePresenter(string title, int clientWidth, int clientHeight)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The Win32 frame presenter requires Windows.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clientWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clientHeight);

        _windowProcedure = WindowProcedure;
        _instance = GetModuleHandle(null);

        var windowClass = new WndClass
        {
            Style = CsOwnDc,
            WindowProcedure = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
            Instance = _instance,
            Cursor = LoadCursor(0, new nint(32512)),
            Background = GetStockObject(BlackBrush),
            ClassName = _className
        };

        if (RegisterClass(ref windowClass) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register the AxetosOS window class.");
        }

        _window = CreateWindowEx(
            0,
            _className,
            title,
            WsOverlappedWindow | WsVisible,
            CwUseDefault,
            CwUseDefault,
            clientWidth + 16,
            clientHeight + 39,
            0,
            0,
            _instance,
            0);

        if (_window == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the AxetosOS presentation window.");
        }

        ClientWidth = clientWidth;
        ClientHeight = clientHeight;
    }

    public bool IsOpen => _window != 0 && !_disposed;
    public bool IsFullscreen => _fullscreen;
    public int ClientWidth { get; private set; }
    public int ClientHeight { get; private set; }
    public int PresentationHeight => Math.Max(0, ClientHeight - (_statusBarVisible && !_fullscreen ? DefaultStatusBarHeight : 0));

    /// <summary>
    /// Compatibility input event retained for existing native frame hosts.
    /// </summary>
    public event Action<NativeKey, bool>? KeyStateChanged;

    /// <summary>
    /// Rich native input event for application shells that need global shortcuts.
    /// </summary>
    public event Action<NativeKeyEvent>? KeyChanged;

    public event Action<int>? CommandInvoked;

    public void SetTitle(string title)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (_window == 0) return;
        if (!SetWindowText(_window, title))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not update the AxetosOS window title.");
        }
    }

    public void SetApplicationMenu(IReadOnlyList<NativeApplicationMenuGroup> groups)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(groups);

        var replacement = CreateNativeMenu(groups);
        var previous = _menu;
        _menu = replacement;

        if (_window != 0 && !_fullscreen)
        {
            if (!SetMenu(_window, _menu))
            {
                _menu = previous;
                DestroyMenu(replacement);
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not attach the AxetosOS application menu.");
            }
            DrawMenuBar(_window);
        }

        if (previous != 0)
        {
            DestroyMenu(previous);
        }
    }

    public void SetStatusBar(string? text, bool visible = true)
    {
        ThrowIfDisposed();
        _statusText = text ?? string.Empty;
        _statusBarVisible = visible;
    }

    public void ToggleFullscreen()
    {
        if (_fullscreen)
        {
            ExitFullscreen();
        }
        else
        {
            EnterFullscreen();
        }
    }

    public void EnterFullscreen()
    {
        ThrowIfDisposed();
        if (_window == 0 || _fullscreen) return;

        var placement = WindowPlacement.Create();
        if (!GetWindowPlacement(_window, ref placement))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not capture the AxetosOS window placement.");
        }

        var monitor = MonitorFromWindow(_window, MonitorDefaultToNearest);
        var monitorInfo = MonitorInfo.Create();
        if (monitor == 0 || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not determine the fullscreen monitor bounds.");
        }

        _windowedPlacement = placement;
        _hasWindowedPlacement = true;
        _windowedStyle = GetWindowLongPtrSafe(_window, GwlStyle);

        if (_menu != 0)
        {
            SetMenu(_window, 0);
        }

        var fullscreenStyle = new nint(_windowedStyle.ToInt64() & ~(long)WsOverlappedWindow);
        SetWindowLongPtrSafe(_window, GwlStyle, fullscreenStyle);

        var bounds = monitorInfo.Monitor;
        if (!SetWindowPos(
                _window,
                0,
                bounds.Left,
                bounds.Top,
                bounds.Right - bounds.Left,
                bounds.Bottom - bounds.Top,
                SwpNoOwnerZOrder | SwpFrameChanged))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enter fullscreen presentation.");
        }

        _fullscreen = true;
        UpdateClientSize();
    }

    public void ExitFullscreen()
    {
        ThrowIfDisposed();
        if (_window == 0 || !_fullscreen) return;

        SetWindowLongPtrSafe(_window, GwlStyle, _windowedStyle);
        if (_hasWindowedPlacement && !SetWindowPlacement(_window, ref _windowedPlacement))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not restore the AxetosOS window placement.");
        }

        if (_menu != 0 && !SetMenu(_window, _menu))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not restore the AxetosOS application menu.");
        }

        if (!SetWindowPos(
                _window,
                0,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoOwnerZOrder | SwpFrameChanged))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not restore windowed presentation.");
        }

        DrawMenuBar(_window);
        _fullscreen = false;
        UpdateClientSize();
    }

    public void Close()
    {
        if (_disposed || _window == 0)
        {
            return;
        }

        PostMessage(_window, WmClose, 0, 0);
    }

    public void PumpEvents()
    {
        ThrowIfDisposed();
        while (PeekMessage(out var message, 0, 0, 0, PmRemove))
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }

        UpdateClientSize();
    }

    public void Present(FrameSurface surface, ScalingMode scalingMode = ScalingMode.IntegerNearest)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(surface);

        if (!IsOpen || ClientWidth <= 0 || ClientHeight <= 0)
        {
            return;
        }

        if (surface.PixelFormat != PixelFormat.Bgra32)
        {
            throw new NotSupportedException($"Pixel format {surface.PixelFormat} is not supported by this presenter.");
        }

        var deviceContext = GetDC(_window);
        if (deviceContext == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not acquire the presentation device context.");
        }

        try
        {
            EnsureBackBuffer(deviceContext);

            var clientRect = new Rect(0, 0, ClientWidth, ClientHeight);
            FillRect(_backBufferDc, ref clientRect, GetStockObject(BlackBrush));

            var presentationHeight = PresentationHeight;
            if (presentationHeight > 0)
            {
                var viewport = PresentationViewport.Calculate(
                    surface.Width,
                    surface.Height,
                    ClientWidth,
                    presentationHeight,
                    scalingMode);

                var bitmapInfo = BitmapInfo.Create(surface.Width, surface.Height);
                var handle = GCHandle.Alloc(surface.Pixels, GCHandleType.Pinned);
                try
                {
                    SetStretchBltMode(_backBufferDc, ColorOnColor);
                    var result = StretchDIBits(
                        _backBufferDc,
                        viewport.X,
                        viewport.Y,
                        viewport.Width,
                        viewport.Height,
                        0,
                        0,
                        surface.Width,
                        surface.Height,
                        handle.AddrOfPinnedObject(),
                        ref bitmapInfo,
                        DibRgbColors,
                        Srccopy);

                    if (result == 0)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "The framebuffer could not be composed.");
                    }
                }
                finally
                {
                    handle.Free();
                }
            }

            if (_statusBarVisible && !_fullscreen)
            {
                DrawStatusBar(presentationHeight);
            }

            if (!BitBlt(deviceContext, 0, 0, ClientWidth, ClientHeight, _backBufferDc, 0, 0, Srccopy))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The composed framebuffer could not be presented.");
            }
        }
        finally
        {
            ReleaseDC(_window, deviceContext);
        }
    }

    /// <summary>
    /// Presents host/application text without writing into the source framebuffer.
    /// This is intended for loading, startup, and other shell-owned transient views.
    /// </summary>
    public void PresentApplicationMessage(string title, string? detail = null)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        UpdateClientSize();
        if (!IsOpen || ClientWidth <= 0 || ClientHeight <= 0)
        {
            return;
        }

        var deviceContext = GetDC(_window);
        if (deviceContext == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not acquire the presentation device context.");
        }

        try
        {
            EnsureBackBuffer(deviceContext);

            var clientRect = new Rect(0, 0, ClientWidth, ClientHeight);
            FillRect(_backBufferDc, ref clientRect, GetStockObject(BlackBrush));

            var presentationHeight = PresentationHeight;
            DrawApplicationMessage(title, detail, presentationHeight);

            if (_statusBarVisible && !_fullscreen)
            {
                DrawStatusBar(presentationHeight);
            }

            if (!BitBlt(deviceContext, 0, 0, ClientWidth, ClientHeight, _backBufferDc, 0, 0, Srccopy))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The application message could not be presented.");
            }
        }
        finally
        {
            ReleaseDC(_window, deviceContext);
        }
    }

    private void DrawApplicationMessage(string title, string? detail, int presentationHeight)
    {
        if (presentationHeight <= 0) return;

        var previousFont = SelectObject(_backBufferDc, GetStockObject(DefaultGuiFont));
        try
        {
            SetBkMode(_backBufferDc, Transparent);
            SetTextColor(_backBufferDc, WhiteColorRef);

            var centerY = presentationHeight / 2;
            var titleRect = new Rect(24, Math.Max(0, centerY - 48), Math.Max(24, ClientWidth - 24), Math.Min(presentationHeight, centerY - 8));
            DrawText(
                _backBufferDc,
                title,
                title.Length,
                ref titleRect,
                DtCenter | DtVCenter | DtSingleLine | DtEndEllipsis | DtNoPrefix);

            if (!string.IsNullOrWhiteSpace(detail))
            {
                var detailRect = new Rect(32, Math.Max(0, centerY), Math.Max(32, ClientWidth - 32), Math.Min(presentationHeight, centerY + 112));
                DrawText(
                    _backBufferDc,
                    detail,
                    detail.Length,
                    ref detailRect,
                    DtCenter | DtWordBreak | DtNoPrefix);
            }
        }
        finally
        {
            if (previousFont != 0 && previousFont != HgdiError)
            {
                SelectObject(_backBufferDc, previousFont);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseBackBuffer();
        if (_window != 0)
        {
            if (_menu != 0) SetMenu(_window, 0);
            DestroyWindow(_window);
            _window = 0;
        }

        if (_menu != 0)
        {
            DestroyMenu(_menu);
            _menu = 0;
        }

        UnregisterClass(_className, _instance);
        GC.SuppressFinalize(this);
    }

    private nint WindowProcedure(nint window, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WmClose:
                DestroyWindow(window);
                return 0;
            case WmDestroy:
                _window = 0;
                return 0;
            case WmEraseBackground:
                // Keep the native client area black before the first composed frame.
                // This avoids the default white window flash during shell startup.
                if (wParam != 0 && GetClientRect(window, out var eraseRect))
                {
                    FillRect(wParam, ref eraseRect, GetStockObject(BlackBrush));
                }
                return 1;
            case WmCommand:
                CommandInvoked?.Invoke(unchecked((int)(wParam.ToInt64() & 0xFFFF)));
                return 0;
            case WmKeyDown:
                PublishKeyChange((int)wParam, pressed: true, lParam);
                return 0;
            case WmKeyUp:
                PublishKeyChange((int)wParam, pressed: false, lParam);
                return 0;
            case WmSysKeyDown:
                PublishKeyChange((int)wParam, pressed: true, lParam);
                return DefWindowProc(window, message, wParam, lParam);
            case WmSysKeyUp:
                PublishKeyChange((int)wParam, pressed: false, lParam);
                return DefWindowProc(window, message, wParam, lParam);
            default:
                return DefWindowProc(window, message, wParam, lParam);
        }
    }

    private void PublishKeyChange(int virtualKey, bool pressed, nint lParam)
    {
        var key = MapVirtualKey(virtualKey);
        var modifiers = NativeKeyModifiers.None;
        if (GetKeyState(VkControl) < 0) modifiers |= NativeKeyModifiers.Control;
        if (GetKeyState(VkShift) < 0) modifiers |= NativeKeyModifiers.Shift;
        if (GetKeyState(VkMenu) < 0) modifiers |= NativeKeyModifiers.Alt;

        var isRepeat = pressed && (lParam.ToInt64() & (1L << 30)) != 0;
        KeyChanged?.Invoke(new NativeKeyEvent(key, pressed, modifiers, isRepeat));
        KeyStateChanged?.Invoke(key, pressed);
    }

    private void DrawStatusBar(int presentationHeight)
    {
        var statusRect = new Rect(0, presentationHeight, ClientWidth, ClientHeight);
        FillRect(_backBufferDc, ref statusRect, GetSysColorBrush(ColorBtnFace));

        if (_statusText.Length == 0) return;

        var textRect = new Rect(8, presentationHeight, Math.Max(8, ClientWidth - 8), ClientHeight);
        SetBkMode(_backBufferDc, Transparent);
        SetTextColor(_backBufferDc, GetSysColor(ColorBtnText));
        DrawText(
            _backBufferDc,
            _statusText,
            _statusText.Length,
            ref textRect,
            DtSingleLine | DtVCenter | DtEndEllipsis | DtNoPrefix);
    }

    private static nint CreateNativeMenu(IReadOnlyList<NativeApplicationMenuGroup> groups)
    {
        var root = CreateMenu();
        if (root == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the AxetosOS application menu.");
        }

        try
        {
            foreach (var group in groups)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(group.Text);
                var popup = CreatePopupMenu();
                if (popup == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create an AxetosOS popup menu.");
                }

                foreach (var item in group.Items)
                {
                    var success = item.IsSeparator
                        ? AppendMenu(popup, MfSeparator, 0, null)
                        : AppendMenu(popup, MfString, unchecked((nuint)item.CommandId), item.Text);
                    if (!success)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not append an AxetosOS menu item.");
                    }
                }

                if (!AppendMenu(root, MfPopup, unchecked((nuint)popup), group.Text))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not append an AxetosOS menu group.");
                }
            }

            return root;
        }
        catch
        {
            DestroyMenu(root);
            throw;
        }
    }

    private void EnsureBackBuffer(nint targetDeviceContext)
    {
        if (_backBufferDc != 0 && _backBufferWidth == ClientWidth && _backBufferHeight == ClientHeight)
        {
            return;
        }

        ReleaseBackBuffer();

        _backBufferDc = CreateCompatibleDC(targetDeviceContext);
        if (_backBufferDc == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the AxetosOS presentation back-buffer context.");
        }

        _backBufferBitmap = CreateCompatibleBitmap(targetDeviceContext, ClientWidth, ClientHeight);
        if (_backBufferBitmap == 0)
        {
            ReleaseBackBuffer();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the AxetosOS presentation back-buffer bitmap.");
        }

        _backBufferPreviousBitmap = SelectObject(_backBufferDc, _backBufferBitmap);
        if (_backBufferPreviousBitmap == 0 || _backBufferPreviousBitmap == HgdiError)
        {
            ReleaseBackBuffer();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not select the AxetosOS presentation back buffer.");
        }

        _backBufferWidth = ClientWidth;
        _backBufferHeight = ClientHeight;
    }

    private void ReleaseBackBuffer()
    {
        if (_backBufferDc != 0 && _backBufferPreviousBitmap != 0 && _backBufferPreviousBitmap != HgdiError)
        {
            SelectObject(_backBufferDc, _backBufferPreviousBitmap);
        }

        if (_backBufferBitmap != 0)
        {
            DeleteObject(_backBufferBitmap);
        }

        if (_backBufferDc != 0)
        {
            DeleteDC(_backBufferDc);
        }

        _backBufferDc = 0;
        _backBufferBitmap = 0;
        _backBufferPreviousBitmap = 0;
        _backBufferWidth = 0;
        _backBufferHeight = 0;
    }

    private static NativeKey MapVirtualKey(int virtualKey) => virtualKey switch
    {
        0x25 => NativeKey.Left,
        0x26 => NativeKey.Up,
        0x27 => NativeKey.Right,
        0x28 => NativeKey.Down,
        0x5A => NativeKey.Z,
        0x58 => NativeKey.X,
        0x4F => NativeKey.O,
        0x52 => NativeKey.R,
        0x0D => NativeKey.Enter,
        0xA1 => NativeKey.RightShift,
        0x10 => NativeKey.RightShift,
        0x1B => NativeKey.Escape,
        0x20 => NativeKey.Space,
        0x74 => NativeKey.F5,
        0x76 => NativeKey.F7,
        0x7A => NativeKey.F11,
        _ => NativeKey.Unknown
    };

    private void UpdateClientSize()
    {
        if (_window != 0 && GetClientRect(_window, out var rectangle))
        {
            ClientWidth = Math.Max(0, rectangle.Right - rectangle.Left);
            ClientHeight = Math.Max(0, rectangle.Bottom - rectangle.Top);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static nint GetWindowLongPtrSafe(nint window, int index) => IntPtr.Size == 8
        ? GetWindowLongPtr64(window, index)
        : new nint(GetWindowLong32(window, index));

    private static nint SetWindowLongPtrSafe(nint window, int index, nint value) => IntPtr.Size == 8
        ? SetWindowLongPtr64(window, index, value)
        : new nint(SetWindowLong32(window, index, value.ToInt32()));

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint Style;
        public nint WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Value;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Point Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public Rect(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public uint Length;
        public uint Flags;
        public uint ShowCommand;
        public Point MinimumPosition;
        public Point MaximumPosition;
        public Rect NormalPosition;

        public static WindowPlacement Create() => new()
        {
            Length = (uint)Marshal.SizeOf<WindowPlacement>()
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;

        public static MonitorInfo Create() => new()
        {
            Size = (uint)Marshal.SizeOf<MonitorInfo>()
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Color;

        public static BitmapInfo Create(int width, int height) => new()
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = 0,
                SizeImage = (uint)checked(width * height * 4)
            }
        };
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProc(nint window, uint message, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", EntryPoint = "RegisterClassW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern ushort RegisterClass(ref WndClass windowClass);

    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool UnregisterClass(string className, nint instance);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateWindowEx(uint extendedStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", ExactSpelling = true)]
    private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", EntryPoint = "SetWindowTextW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowText(nint window, string text);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW", ExactSpelling = true)]
    private static extern bool PeekMessage(out Message message, nint window, uint minimum, uint maximum, uint removeMessage);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW", ExactSpelling = true)]
    private static extern nint DispatchMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint window, out Rect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint deviceContext);

    [DllImport("user32.dll")]
    private static extern int FillRect(nint deviceContext, ref Rect rectangle, nint brush);

    [DllImport("user32.dll")]
    private static extern nint GetSysColorBrush(int index);

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);

    [DllImport("user32.dll", EntryPoint = "DrawTextW", CharSet = CharSet.Unicode)]
    private static extern int DrawText(nint deviceContext, string text, int count, ref Rect rectangle, uint format);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMenu(nint window, nint menu);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawMenuBar(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateMenu();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(nint menu, uint flags, nuint item, string? text);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(nint window, ref WindowPlacement placement);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(nint window, [In] ref WindowPlacement placement);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint window, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(nint window, int index, int value);

    [DllImport("gdi32.dll")]
    private static extern nint GetStockObject(int objectIndex);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(nint deviceContext, int mode);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(nint deviceContext, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(nint deviceContext, uint color);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateCompatibleDC(nint deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateCompatibleBitmap(nint deviceContext, int width, int height);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint SelectObject(nint deviceContext, nint graphicsObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint graphicsObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        nint destinationDeviceContext,
        int x,
        int y,
        int width,
        int height,
        nint sourceDeviceContext,
        int sourceX,
        int sourceY,
        uint rasterOperation);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int StretchDIBits(nint deviceContext, int xDestination, int yDestination,
        int destinationWidth, int destinationHeight, int xSource, int ySource, int sourceWidth,
        int sourceHeight, nint bits, ref BitmapInfo bitmapInfo, uint usage, uint rasterOperation);

    [DllImport("user32.dll")]
    private static extern nint LoadCursor(nint instance, nint cursorName);
}
