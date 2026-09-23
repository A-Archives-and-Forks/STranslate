using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using STranslate.Helpers;

namespace STranslate.Tests;

public class WindowShowAnimationTests
{
    [Fact]
    public void PreviewOnEachMonitorStaysNonActivatingAndDoesNotHideTheSource()
    {
        RunOnSta(() =>
        {
            var previousDpiContext = SetThreadDpiAwarenessContext(new nint(-4)); // PER_MONITOR_AWARE_V2
            try
            {
                foreach (var monitor in MonitorInfo.GetDisplayMonitors())
                {
                    var window = CreateWindow();
                    try
                    {
                        window.Show();
                        Win32Helper.SetWindowPhysicalBounds(window,
                            (int)monitor.WorkingArea.Left + 60, (int)monitor.WorkingArea.Top + 60, 400, 250);
                        window.Hide();
                        using var animation = WindowShowAnimation.TryCreate(window);
                        Assert.NotNull(animation);
                        window.Deactivated += (_, _) => window.Visibility = Visibility.Collapsed;
                        window.Show();
                        animation.Start();
                        PumpUntil(() => animation.Surface?.IsVisible == true);
                        var hwnd = new WindowInteropHelper(animation.Surface!).Handle;
                        Assert.Equal(monitor.Name, MonitorInfo.GetNearestDisplayMonitor(hwnd).Name);
                        Assert.NotEqual(0, GetWindowLong(hwnd, -20) & 0x08000000); // WS_EX_NOACTIVATE
                        PumpUntil(() => !animation.IsActive);
                        Assert.True(window.IsVisible);
                        Assert.False(IsCloaked(window));
                    }
                    finally { window.Close(); }
                }
            }
            finally { if (previousDpiContext != 0) SetThreadDpiAwarenessContext(previousDpiContext); }
        });
    }

    [Fact]
    public void ActivatingWindowWithHideOnDeactivationSurvivesThePreview()
    {
        RunOnSta(() =>
        {
            var window = CreateWindow();
            window.ShowActivated = true;
            window.Opacity = 1;
            var losses = 0;
            try
            {
                window.Show();
                PumpFor(TimeSpan.FromMilliseconds(60));
                window.Hide();
                using var animation = WindowShowAnimation.TryCreate(window);
                Assert.NotNull(animation);
                window.Deactivated += (_, _) => { losses++; window.Visibility = Visibility.Collapsed; };
                window.Show();
                window.Activate();
                animation.Start();
                PumpUntil(() => !animation.IsActive);
                Assert.True(window.IsVisible, $"预览导致主窗口隐藏，失焦次数 {losses}");
                Assert.False(IsCloaked(window));
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationInsideNativePositionUpdateDoesNotContinueUsingDisposedSurface(bool useNativeHook)
    {
        RunOnSta(() =>
        {
            var window = CreateWindow();
            try
            {
                window.Show();
                using var animation = WindowShowAnimation.TryCreate(window);
                Assert.NotNull(animation);
                animation.Start();
                if (useNativeHook)
                {
                    // 模拟主窗口失焦隐藏在 SetWindowPos 的同步消息中重入。
                    EventHandler? hook = null;
                    hook = (_, _) =>
                    {
                        if (animation.Surface is not { } surface) return;
                        HwndSource.FromHwnd(new WindowInteropHelper(surface).Handle).AddHook(
                            (nint hwnd, int msg, nint w, nint l, ref bool handled) =>
                            {
                                if (msg == 0x0046) window.Visibility = Visibility.Collapsed;
                                return 0;
                            });
                        CompositionTarget.Rendering -= hook;
                    };
                    CompositionTarget.Rendering += hook;
                    try { PumpUntil(() => !animation.IsActive); }
                    finally { CompositionTarget.Rendering -= hook; }
                }
                else
                {
                    PumpUntil(() => animation.Surface?.IsVisible == true);
                    animation.Surface!.LocationChanged += (_, _) => window.Visibility = Visibility.Collapsed;
                    PumpUntil(() => !animation.IsActive);
                }
                Assert.False(window.IsVisible);
                Assert.False(IsCloaked(window));
                Assert.Null(animation.Surface);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void FrameUsesRequestedRiseOvershootFadeAndSettleTimings()
    {
        var start = WindowShowAnimation.GetFrame(0);
        var overshoot = WindowShowAnimation.GetFrame(180);
        var settled = WindowShowAnimation.GetFrame(260);

        Assert.Equal(18, start.Offset, precision: 3);
        Assert.Equal(0, start.Opacity, precision: 3);
        Assert.Equal(-3, overshoot.Offset, precision: 3);
        Assert.InRange(overshoot.Opacity, 0.99, 1);
        Assert.Equal(0, settled.Offset, precision: 3);
        Assert.Equal(1, settled.Opacity, precision: 3);
    }

    [Fact]
    public void FirstShowIsCloakedBeforeLoadedAndCompletesWithoutChangingLayout()
    {
        RunOnSta(() =>
        {
            var window = CreateWindow();
            WindowShowAnimation? animation = null;
            var cloakedAtLoad = false;
            window.SourceInitialized += (_, _) => animation = WindowShowAnimation.TryCreate(window);
            window.Loaded += (_, _) => cloakedAtLoad = IsCloaked(window);
            try
            {
                window.Show();
                Assert.NotNull(animation);
                Assert.True(cloakedAtLoad);
                Assert.True(IsCloaked(window));
                var bounds = GetBounds(window);
                var completed = 0;
                animation.Start(() =>
                {
                    Assert.False(IsCloaked(window));
                    // 激活回调执行时预览层仍在遮挡交接空档，回调结束后才释放。
                    Assert.NotNull(animation.Surface);
                    completed++;
                });
                PumpUntil(() => animation.Surface?.IsVisible == true);
                Assert.False(IsCloaked(animation.Surface!));
                PumpUntil(() => !animation.IsActive);
                Assert.False(IsCloaked(window));
                Assert.True(window.IsVisible);
                Assert.Equal(bounds, GetBounds(window));
                Assert.Null(animation.Surface);
                Assert.Equal(1, completed);
            }
            finally
            {
                animation?.Dispose();
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedStartKeepsOneSurfaceAndPreservesPositionAndTopmostBindings(bool topmost)
    {
        RunOnSta(() =>
        {
            var window = CreateWindow();
            var source = new Placement { Left = 160, Top = 180, Topmost = topmost };
            var leftBinding = Bind(window, Window.LeftProperty, source, nameof(Placement.Left));
            var topBinding = Bind(window, Window.TopProperty, source, nameof(Placement.Top));
            var topmostBinding = Bind(window, Window.TopmostProperty, source, nameof(Placement.Topmost));
            try
            {
                window.Show();
                window.Hide();
                var originalBounds = GetBounds(window);
                var root = (UIElement)window.Content;
                var transform = root.RenderTransform;
                using var animation = WindowShowAnimation.TryCreate(window);
                Assert.NotNull(animation);
                window.Show();
                animation.Start();
                animation.Start();
                PumpUntil(() => animation.Surface?.IsVisible == true);
                var surface = animation.Surface!;
                animation.Start();
                Assert.True(animation.IsActive);
                Assert.Same(surface, animation.Surface);
                Assert.False(surface.ShowActivated);
                Assert.False(surface.ShowInTaskbar);
                Assert.True(surface.Topmost);
                Assert.False(IsCloaked(surface));
                Assert.True(IsCloaked(window));
                Assert.Equal(originalBounds, GetBounds(window));
                Assert.Same(transform, root.RenderTransform);

                // 翻译结果在动画中到达，预览应跟随新高度，不改变源窗口的位置。
                window.Height += 60;
                window.UpdateLayout();
                PumpUntil(() => GetBounds(surface).Height == GetBounds(window).Height);
                Assert.Equal(originalBounds.Left, GetBounds(window).Left);
                Assert.Equal(originalBounds.Top, GetBounds(window).Top);
                PumpUntil(() => !animation.IsActive);
                Assert.False(IsCloaked(window));
                Assert.Same(leftBinding, BindingOperations.GetBindingBase(window, Window.LeftProperty));
                Assert.Same(topBinding, BindingOperations.GetBindingBase(window, Window.TopProperty));
                Assert.Same(topmostBinding, BindingOperations.GetBindingBase(window, Window.TopmostProperty));
                Assert.Equal(160, source.Left);
                Assert.Equal(180, source.Top);
                Assert.Equal(topmost, source.Topmost);
                Assert.Null(animation.Surface);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HidingCancelsPendingOrRunningAnimationWithoutReopeningWindow(bool running)
    {
        RunOnSta(() =>
        {
            var window = CreateWindow();
            try
            {
                window.Show();
                window.Hide();
                using var animation = WindowShowAnimation.TryCreate(window);
                Assert.NotNull(animation);
                Assert.True(IsCloaked(window));
                window.Show();
                var completed = false;
                animation.Start(() => completed = true);
                if (running) PumpUntil(() => animation.Surface?.IsVisible == true);
                window.Visibility = Visibility.Collapsed;
                Assert.False(animation.IsActive);
                Assert.False(IsCloaked(window));
                PumpFor(TimeSpan.FromMilliseconds(320));
                Assert.False(window.IsVisible);
                Assert.False(IsCloaked(window));
                Assert.Null(animation.Surface);
                Assert.False(completed);

                // 快速再次唤出，上一轮清理不应影响新的遮蔽。
                using var next = WindowShowAnimation.TryCreate(window);
                Assert.NotNull(next);
                animation.Dispose();
                Assert.True(IsCloaked(window));
                window.Show();
                next.Start();
                PumpUntil(() => !next.IsActive);
                Assert.True(window.IsVisible);
                Assert.False(IsCloaked(window));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void KeyboardInputImmediatelyReleasesTheSourceWithoutSwallowingInput()
    {
        RunOnSta(() =>
        {
            var window = CreateWindow();
            try
            {
                window.Show();
                using var animation = WindowShowAnimation.TryCreate(window);
                Assert.NotNull(animation);
                animation.Start();
                PumpUntil(() => animation.Surface?.IsVisible == true);
                var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window),
                    Environment.TickCount, Key.A) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                window.RaiseEvent(key);
                Assert.False(key.Handled);
                Assert.False(animation.IsActive);
                Assert.False(IsCloaked(window));
                Assert.True(window.IsVisible);
                Assert.Null(animation.Surface);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosingCancelsPendingOrRunningAnimation(bool running)
    {
        RunOnSta(() =>
        {
            var window = CreateWindow();
            window.Show();
            using var animation = WindowShowAnimation.TryCreate(window);
            Assert.NotNull(animation);
            animation.Start();
            if (running) PumpUntil(() => animation.Surface?.IsVisible == true);
            window.Close();
            Assert.False(animation.IsActive);
            PumpFor(TimeSpan.FromMilliseconds(320));
        });
    }

    private static Window CreateWindow() => new()
    {
        Style = null,
        Width = 320,
        Height = 200,
        Left = 160,
        Top = 180,
        Opacity = 0,
        ShowActivated = false,
        ShowInTaskbar = false,
        Content = new Border { Background = Brushes.White }
    };

    private static Binding Bind(Window window, DependencyProperty property, object source, string path)
    {
        var binding = new Binding(path) { Source = source, Mode = BindingMode.TwoWay };
        window.SetBinding(property, binding);
        return binding;
    }

    private static NativeRect GetBounds(Window window)
    {
        Assert.True(GetWindowRect(new WindowInteropHelper(window).Handle, out var bounds));
        return bounds;
    }

    private static bool IsCloaked(Window window)
    {
        Assert.Equal(0, DwmGetWindowAttribute(new WindowInteropHelper(window).Handle,
            14, out var cloaked, sizeof(int)));
        return (cloaked & 1) != 0;
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline) PumpFor(TimeSpan.FromMilliseconds(5));
        Assert.True(condition(), "等待窗口显示动画超时");
    }

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }

    private static readonly Lazy<Dispatcher> TestDispatcher = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    });

    private static void RunOnSta(Action action) => TestDispatcher.Value.Invoke(action);

    public sealed class Placement
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public bool Topmost { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Height => Bottom - Top;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect bounds);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);
}
