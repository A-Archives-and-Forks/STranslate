using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Controls;

namespace STranslate.Helpers;

/// <summary>用 DWM 整窗预览淡入上浮，真实窗口的位置、背景和布局均保持不变。</summary>
internal sealed class WindowShowAnimation : IDisposable
{
    private readonly Window _window;
    private readonly HwndSource _source;
    private readonly Stopwatch _clock = new();
    private Window? _surface;
    private nint _thumbnail;
    private DispatcherOperation? _prepareOperation;
    private bool _cloaked;
    private bool _started;
    private bool _disposed;
    private Action? _completed;
    private int _surfaceRegionWidth;
    private int _surfaceRegionHeight;
    private double _surfaceRegionDpi;

    internal bool IsActive => !_disposed;
    internal Window? Surface => _surface;

    // 在 SourceInitialized 或再次显示之前调用；此时不依赖 ActualWidth/ActualHeight。
    internal static WindowShowAnimation? TryCreate(Window window)
    {
        try { return new WindowShowAnimation(window); }
        catch (COMException) { return null; }
        catch (Win32Exception) { return null; }
    }

    private WindowShowAnimation(Window window)
    {
        _window = window;
        _source = HwndSource.FromHwnd(Win32Helper.GetWindowHandle(window, ensure: true));
        try
        {
            _cloaked = Win32Helper.SetWindowCloaked(window, cloaked: true);
            if (!_cloaked) throw new COMException("无法准备窗口显示动画");
            _source.AddHook(SourceWndProc);
            _window.IsVisibleChanged += OnVisibilityChanged;
            _window.PreviewKeyDown += OnKeyDown;
            _window.PreviewMouseDown += OnMouseDown;
            _window.Closed += OnClosed;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void Start(Action? completed = null)
    {
        if (_disposed || _started) return;
        _started = true;
        _completed = completed;
        // 等位置、布局和首帧渲染就绪，避免预览捕获空背景。
        _prepareOperation = _window.Dispatcher.InvokeAsync(Prepare, DispatcherPriority.Loaded);
    }

    private void Prepare()
    {
        _prepareOperation = null;
        if (_disposed) return;
        if (!_window.IsVisible)
        {
            Dispose();
            return;
        }
        if (!SystemParameters.ClientAreaAnimation)
        {
            Complete();
            return;
        }
        try
        {
            _surface = new Window
            {
                Style = null,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowActivated = false,
                ShowInTaskbar = false,
                // 不能设置 Owner：过渡层会继承源窗口的 DWM 遮蔽，无法显示。
                // 源窗口在 Cloak 期间不可见，预览层必须保持非激活置顶，直到动画交还真实窗口。
                Topmost = true,
                Background = Brushes.Transparent,
                // 首次 HWND 就建立在源窗口所在屏幕，避免从默认屏幕迁移时触发 DPI/激活消息。
                Left = _window.Left,
                Top = _window.Top,
                Width = Math.Max(1, _window.ActualWidth),
                Height = Math.Max(1, _window.ActualHeight)
            };
            var hwnd = new WindowInteropHelper(_surface).EnsureHandle();
            if (_disposed) return;
            Win32Helper.HideFromAltTab(_surface);
            if (_disposed) return;
            Win32Helper.DisableWindowActivation(_surface);
            if (_disposed) return;
            var source = HwndSource.FromHwnd(hwnd);
            source.AddHook(SurfaceWndProc);
            var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            PInvoke.DwmExtendFrameIntoClientArea(new HWND(hwnd), margins).ThrowOnFailure();
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            PInvoke.DwmRegisterThumbnail(new HWND(hwnd), Win32Helper.GetWindowHandle(_window),
                out _thumbnail).ThrowOnFailure();
            if (!Win32Helper.SetWindowCloaked(_surface, cloaked: true) || !ApplyFrame(0))
            {
                Complete();
                return;
            }
            if (_disposed) return;
            _surface.Show();
            if (_disposed) return;
            Win32Helper.RaiseWindowWithoutActivation(_surface);
            if (_disposed) return;
            Win32Helper.FlushDesktopComposition();
            if (!Win32Helper.SetWindowCloaked(_surface, cloaked: false))
                throw new COMException("无法显示窗口预览");
            _clock.Restart();
            CompositionTarget.Rendering += OnRendering;
        }
        catch (COMException) { Complete(); }
        catch (Win32Exception) { Complete(); }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_disposed) return;
        var elapsed = _clock.Elapsed.TotalMilliseconds;
        if (!SystemParameters.ClientAreaAnimation || !ApplyFrame(elapsed) || elapsed >= 260)
            Complete();
    }

    private void Complete()
    {
        if (_disposed) return;
        var completed = _completed;
        _completed = null;

        // 保留置顶预览层遮挡交接过程：先解除真实窗口遮蔽并完成激活，
        // 最后才关闭预览层，避免原前台窗口在两个动作之间闪回到上层。
        CompositionTarget.Rendering -= OnRendering;
        _clock.Stop();
        if (_cloaked)
        {
            Win32Helper.SetWindowCloaked(_window, cloaked: false);
            _cloaked = false;
        }

        try { completed?.Invoke(); }
        finally { Dispose(); }
    }

    private bool ApplyFrame(double elapsed)
    {
        if (_disposed || _surface is null ||
            !PInvoke.GetWindowRect(Win32Helper.GetWindowHandle(_window), out var bounds) ||
            bounds.Width <= 0 || bounds.Height <= 0 ||
            !PInvoke.DwmQueryThumbnailSourceSize(_thumbnail, out var size).Succeeded ||
            size.Width <= 0 || size.Height <= 0)
            return false;

        var (offset, opacity) = GetFrame(elapsed);
        var offsetPixels = (int)Math.Round(offset * VisualTreeHelper.GetDpi(_window).DpiScaleY);
        try
        {
            _surface.Topmost = true;
            if (_disposed) return false;
            // 只移动过渡层；每帧读取源窗口尺寸以跟随译文高度和显示器 DPI 变化。
            // SetWindowPos 会同步派发消息，可能重入失焦/隐藏逻辑并释放预览层。
            Win32Helper.SetWindowPhysicalBounds(_surface, bounds.left, bounds.top + offsetPixels,
                bounds.Width, bounds.Height, showWindow: false);
            if (_disposed) return false;
            if (!ApplySurfaceRegion(bounds.Width, bounds.Height)) return false;
            Win32Helper.RaiseWindowWithoutActivation(_surface);
            if (_disposed) return false;
        }
        catch (Win32Exception) { return false; }

        var properties = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = PInvoke.DWM_TNP_RECTDESTINATION | PInvoke.DWM_TNP_RECTSOURCE |
                      PInvoke.DWM_TNP_OPACITY | PInvoke.DWM_TNP_VISIBLE | PInvoke.DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = new RECT(0, 0, bounds.Width, bounds.Height),
            rcSource = new RECT(0, 0, size.Width, size.Height),
            opacity = (byte)Math.Round(opacity * 255),
            fVisible = true,
            fSourceClientAreaOnly = false
        };
        return PInvoke.DwmUpdateThumbnailProperties(_thumbnail, properties).Succeeded;
    }

    private bool ApplySurfaceRegion(int width, int height)
    {
        var dpi = VisualTreeHelper.GetDpi(_window).DpiScaleX;
        if (_surfaceRegionWidth == width && _surfaceRegionHeight == height &&
            Math.Abs(_surfaceRegionDpi - dpi) < 0.01)
            return true;

        var radius = Math.Clamp((int)Math.Round(8 * dpi), 1, Math.Min(width, height) / 2);
        var region = PInvoke.CreateRoundRectRgn(0, 0, width, height, radius * 2, radius * 2);
        if (region.IsNull) return false;
        if (PInvoke.SetWindowRgn((HWND)(IntPtr)new WindowInteropHelper(_surface!).Handle, region, true) == 0)
        {
            PInvoke.DeleteObject((HGDIOBJ)region);
            return false;
        }

        _surfaceRegionWidth = width;
        _surfaceRegionHeight = height;
        _surfaceRegionDpi = dpi;
        return true;
    }

    internal static (double Offset, double Opacity) GetFrame(double elapsedMilliseconds)
    {
        var rise = Math.Clamp(elapsedMilliseconds / 180, 0, 1);
        var settle = Math.Clamp((elapsedMilliseconds - 180) / 80, 0, 1);
        var fade = Math.Clamp(elapsedMilliseconds / 140, 0, 1);
        // 三次缓出到最高点，平滑起止地回落，交界处速度均为零。
        var offset = elapsedMilliseconds <= 180
            ? -3 + 21 * Math.Pow(1 - rise, 3)
            : -3 + 3 * settle * settle * (3 - 2 * settle);
        return (offset, 1 - Math.Pow(1 - fade, 3));
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_window.IsVisible) Dispose();
    }

    private void OnKeyDown(object sender, KeyEventArgs e) => Complete();
    private void OnMouseDown(object sender, MouseButtonEventArgs e) => Complete();
    private void OnClosed(object? sender, EventArgs e) => Dispose();

    private nint SourceWndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0231) Complete(); // WM_ENTERSIZEMOVE
        return 0;
    }

    private nint SurfaceWndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021) // WM_MOUSEACTIVATE
        {
            handled = true;
            return 3; // MA_NOACTIVATE
        }
        if (message is 0x0201 or 0x0204 or 0x0207) // 点击后交还真实窗口，不在窗口过程内销毁 HWND。
            _window.Dispatcher.BeginInvoke(Complete);
        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _completed = null;
        _prepareOperation?.Abort();
        _prepareOperation = null;
        CompositionTarget.Rendering -= OnRendering;
        _clock.Stop();
        _window.IsVisibleChanged -= OnVisibilityChanged;
        _window.PreviewKeyDown -= OnKeyDown;
        _window.PreviewMouseDown -= OnMouseDown;
        _window.Closed -= OnClosed;
        _source.RemoveHook(SourceWndProc);
        // 只解除遮蔽，绝不调用 Show，隐藏和退出路径不会被动画重新打开。
        if (_cloaked) Win32Helper.SetWindowCloaked(_window, cloaked: false);
        _cloaked = false;
        if (_thumbnail != 0) PInvoke.DwmUnregisterThumbnail(_thumbnail);
        _thumbnail = 0;
        _surface?.Close();
        _surface = null;
    }
}
