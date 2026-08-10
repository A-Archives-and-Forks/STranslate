using STranslate.Helpers;
using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

using DrawingPoint = System.Drawing.Point;

namespace STranslate.Views;

public partial class MouseHookIconWindow : Window
{
    private const int IconOffset = 10;
    private readonly DispatcherTimer _hideTimer;

    /// <summary>
    /// 用户点击翻译图标时触发。
    /// </summary>
    public event EventHandler? TranslateRequested;

    /// <summary>
    /// 初始化鼠标划词悬浮图标窗口。
    /// </summary>
    public MouseHookIconWindow()
    {
        InitializeComponent();

        _hideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _hideTimer.Tick += (_, _) => HideWindow();

        MouseEnter += (_, _) => _hideTimer.Stop();
        MouseLeave += (_, _) => RestartHideTimer();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new HWND(new WindowInteropHelper(this).Handle);
        var extendedStyle = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLongPtr(
            hwnd,
            WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        PInvoke.SetWindowLongPtr(
            hwnd,
            WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE,
            (nint)(extendedStyle | WINDOW_EX_STYLE.WS_EX_NOACTIVATE));
    }

    /// <summary>
    /// 仅显示图标，不传递文本
    /// </summary>
    /// <param name="point">鼠标的物理屏幕坐标</param>
    public void ShowAt(System.Windows.Point point)
    {
        var physicalPoint = new DrawingPoint((int)Math.Round(point.X), (int)Math.Round(point.Y));
        var monitor = MonitorInfo.GetDisplayMonitors()
            .FirstOrDefault(item => item.Bounds.Contains(point)) ?? MonitorInfo.GetCursorDisplayMonitor();
        var dpi = Win32Helper.GetDpiScaleForPhysicalPoint(physicalPoint.X, physicalPoint.Y);
        var physicalWidth = Math.Max(1, (int)Math.Round(Width * dpi.DpiScaleX));
        var physicalHeight = Math.Max(1, (int)Math.Round(Height * dpi.DpiScaleY));
        var offsetX = (int)Math.Round(IconOffset * dpi.DpiScaleX);
        var offsetY = (int)Math.Round(IconOffset * dpi.DpiScaleY);
        var workArea = new Rectangle(
            (int)Math.Round(monitor.WorkingArea.Left),
            (int)Math.Round(monitor.WorkingArea.Top),
            (int)Math.Round(monitor.WorkingArea.Width),
            (int)Math.Round(monitor.WorkingArea.Height));

        var left = physicalPoint.X + offsetX;
        var top = physicalPoint.Y + offsetY;
        if (left + physicalWidth > workArea.Right)
            left = physicalPoint.X - physicalWidth - offsetX;
        if (top + physicalHeight > workArea.Bottom)
            top = physicalPoint.Y - physicalHeight - offsetY;

        left = Math.Clamp(left, workArea.Left, workArea.Right - physicalWidth);
        top = Math.Clamp(top, workArea.Top, workArea.Bottom - physicalHeight);

        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Left = left / dpi.DpiScaleX;
        Top = top / dpi.DpiScaleY;
        Show();
        Win32Helper.SetWindowPhysicalBounds(this, left, top, physicalWidth, physicalHeight);
        RestartHideTimer();
        StartFadeIn();
    }

    internal void StartFadeIn()
    {
        Opacity = 1;
        BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
            {
                FillBehavior = FillBehavior.Stop
            });
    }

    /// <summary>
    /// 隐藏当前悬浮图标。
    /// </summary>
    public void HideWindow()
    {
        _hideTimer.Stop();
        Hide();
    }

    /// <summary>
    /// 判断物理屏幕坐标是否位于当前图标窗口内。
    /// </summary>
    /// <param name="point">物理屏幕坐标。</param>
    /// <returns>窗口可见且坐标位于窗口内时返回 true。</returns>
    public bool ContainsPhysicalPoint(DrawingPoint point)
    {
        if (!IsVisible)
            return false;

        var hwnd = new HWND(new WindowInteropHelper(this).Handle);
        return PInvoke.GetWindowRect(hwnd, out var bounds) &&
               point.X >= bounds.left &&
               point.X < bounds.right &&
               point.Y >= bounds.top &&
               point.Y < bounds.bottom;
    }

    private void TranslateBtn_Click(object sender, RoutedEventArgs e)
    {
        HideWindow();
        TranslateRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RestartHideTimer()
    {
        _hideTimer.Stop();
        _hideTimer.Start();
    }
}
