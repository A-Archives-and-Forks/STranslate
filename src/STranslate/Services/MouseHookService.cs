using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace STranslate.Services;

/// <summary>
/// 在专用消息线程中监听全局鼠标拖动。
/// </summary>
/// <param name="logger">日志记录器。</param>
public sealed class MouseHookService(ILogger<MouseHookService> logger) : IMouseHookService
{
    private static readonly TimeSpan ThreadTransitionTimeout = TimeSpan.FromSeconds(2);
    private readonly Lock _stateLock = new();
    private readonly Lock _eventDispatchLock = new();
    private Task _eventDispatchTask = Task.CompletedTask;
    private Thread? _hookThread;
    private ManualResetEventSlim? _startupCompleted;
    private UnhookWindowsHookExSafeHandle? _hookHandle;
    private HOOKPROC? _hookProc;
    private MouseDragDetector? _dragDetector;
    private HCURSOR _iBeamCursor;
    private uint _hookThreadId;
    private bool _startupSucceeded;
    private bool _disposed;

    /// <summary>
    /// 鼠标左键开始操作时触发，用于清理上一次未完成的交互。
    /// </summary>
    public event EventHandler<Point>? SelectionStarted;

    /// <summary>
    /// 检测到可能的文本拖动选择时触发。
    /// </summary>
    public event EventHandler<MouseDragCompletedEventArgs>? SelectionCompleted;

    /// <summary>
    /// 获取 Hook 线程是否正在运行。
    /// </summary>
    public bool IsRunning
    {
        get
        {
            lock (_stateLock)
                return _hookThread is { IsAlive: true } && _startupSucceeded;
        }
    }

    /// <summary>
    /// 启动全局鼠标监听。
    /// </summary>
    /// <returns>Hook 是否成功安装。</returns>
    public bool Start()
    {
        ManualResetEventSlim startupCompleted;

        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_hookThread is { IsAlive: true })
            {
                startupCompleted = _startupCompleted!;
            }
            else
            {
                _startupSucceeded = false;
                _startupCompleted?.Dispose();
                startupCompleted = new ManualResetEventSlim();
                _startupCompleted = startupCompleted;
                _hookThread = new Thread(RunHookThread)
                {
                    IsBackground = true,
                    Name = "STranslate.MouseHook"
                };
                _hookThread.Start();
            }
        }

        if (!startupCompleted.Wait(ThreadTransitionTimeout))
        {
            logger.LogError("Timed out while starting the global mouse hook thread.");
            Stop();
            return false;
        }

        lock (_stateLock)
            return _startupSucceeded;
    }

    /// <summary>
    /// 停止全局鼠标监听。
    /// </summary>
    public void Stop()
    {
        Thread? hookThread;
        uint hookThreadId;

        lock (_stateLock)
        {
            hookThread = _hookThread;
            hookThreadId = _hookThreadId;
        }

        if (hookThread is null)
            return;

        if (hookThreadId != 0)
            PInvoke.PostThreadMessage(hookThreadId, PInvoke.WM_QUIT, default, default);

        if (hookThread != Thread.CurrentThread && !hookThread.Join(ThreadTransitionTimeout))
            logger.LogWarning("Timed out while stopping the global mouse hook thread.");

        lock (_stateLock)
        {
            if (_hookThread == hookThread && !hookThread.IsAlive)
            {
                _hookThread = null;
                _hookThreadId = 0;
                _startupSucceeded = false;
            }
        }
    }

    private void RunHookThread()
    {
        try
        {
            _hookThreadId = PInvoke.GetCurrentThreadId();
            PInvoke.PeekMessage(out _, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);

            var horizontalThreshold = Math.Max(1, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXDRAG));
            var verticalThreshold = Math.Max(1, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYDRAG));
            _dragDetector = new MouseDragDetector(horizontalThreshold, verticalThreshold);
            _iBeamCursor = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_IBEAM);
            _hookProc = HookCallback;

            using var process = Process.GetCurrentProcess();
            using var module = process.MainModule;
            var moduleHandle = PInvoke.GetModuleHandle(module?.ModuleName);
            _hookHandle = PInvoke.SetWindowsHookEx(
                WINDOWS_HOOK_ID.WH_MOUSE_LL,
                _hookProc,
                moduleHandle,
                0);

            _startupSucceeded = !_hookHandle.IsInvalid;
            if (!_startupSucceeded)
            {
                logger.LogError("Failed to install global mouse hook. Error code: {ErrorCode}", Marshal.GetLastWin32Error());
                return;
            }

            _startupCompleted?.Set();
            logger.LogInformation("Global mouse hook started on dedicated thread {ThreadId}.", _hookThreadId);
            while (PInvoke.GetMessage(out var message, HWND.Null, 0, 0).Value > 0)
            {
                PInvoke.TranslateMessage(message);
                PInvoke.DispatchMessage(message);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Global mouse hook thread crashed.");
        }
        finally
        {
            _startupCompleted?.Set();
            _hookHandle?.Dispose();
            _hookHandle = null;
            _hookProc = null;
            _dragDetector = null;
            _iBeamCursor = HCURSOR.Null;

            lock (_stateLock)
            {
                _startupSucceeded = false;
                _hookThreadId = 0;
            }

            logger.LogInformation("Global mouse hook stopped.");
        }
    }

    private LRESULT HookCallback(int nCode, WPARAM wParam, LPARAM lParam)
    {
        if (nCode < 0 || _dragDetector is null)
            return PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        var point = new Point(data.pt.X, data.pt.Y);
        var message = (uint)wParam.Value;

        if (message == PInvoke.WM_LBUTTONDOWN)
        {
            _dragDetector.OnLeftButtonDown(point, IsIBeamCursor());
            QueueEvent(() => SelectionStarted?.Invoke(this, point));
        }
        else if (message == PInvoke.WM_MOUSEMOVE)
        {
            if (_dragDetector.IsTracking)
                _dragDetector.OnMouseMove(point, IsIBeamCursor());
        }
        else if (message == PInvoke.WM_LBUTTONUP &&
                 _dragDetector.TryComplete(point, IsIBeamCursor(), out var completedPoint))
        {
            var args = new MouseDragCompletedEventArgs(completedPoint);
            QueueEvent(() => SelectionCompleted?.Invoke(this, args));
        }

        return PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);
    }

    private bool IsIBeamCursor()
    {
        var cursorInfo = new CURSORINFO { cbSize = (uint)Marshal.SizeOf<CURSORINFO>() };
        return PInvoke.GetCursorInfo(ref cursorInfo) && cursorInfo.hCursor == _iBeamCursor;
    }

    private void QueueEvent(Action callback)
    {
        lock (_eventDispatchLock)
        {
            // Hook 回调不能等待业务逻辑；串行续接可在立即返回的同时保持按下/抬起事件顺序。
            _eventDispatchTask = _eventDispatchTask.ContinueWith(
                _ => InvokeEventHandler(callback),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    private void InvokeEventHandler(Action callback)
    {
        try
        {
            callback();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error in global mouse hook event handler.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        Stop();
        try
        {
            _eventDispatchTask.Wait(ThreadTransitionTimeout);
        }
        catch (AggregateException ex)
        {
            logger.LogDebug(ex, "Ignored error while draining global mouse hook events.");
        }

        lock (_stateLock)
        {
            if (_hookThread is not { IsAlive: true })
            {
                _startupCompleted?.Dispose();
                _startupCompleted = null;
            }
        }
    }
}

/// <summary>
/// 描述一次全局鼠标文本拖动操作。
/// </summary>
/// <param name="ScreenPoint">拖动结束时的物理屏幕坐标。</param>
public sealed record MouseDragCompletedEventArgs(Point ScreenPoint);

internal sealed class MouseDragDetector(int horizontalThreshold, int verticalThreshold)
{
    private Point _startPoint;
    private bool _isLeftButtonDown;
    private bool _isDragging;
    private bool _hasSeenIBeam;

    internal bool IsTracking => _isLeftButtonDown;

    internal void OnLeftButtonDown(Point point, bool isIBeam)
    {
        _startPoint = point;
        _isLeftButtonDown = true;
        _isDragging = false;
        _hasSeenIBeam = isIBeam;
    }

    internal void OnMouseMove(Point point, bool isIBeam)
    {
        if (!_isLeftButtonDown)
            return;

        _hasSeenIBeam |= isIBeam;
        if (!_isDragging)
        {
            _isDragging = Math.Abs(point.X - _startPoint.X) >= horizontalThreshold ||
                          Math.Abs(point.Y - _startPoint.Y) >= verticalThreshold;
        }
    }

    internal bool TryComplete(Point point, bool isIBeam, out Point completedPoint)
    {
        completedPoint = point;
        var isTextDrag = _isLeftButtonDown && _isDragging && (_hasSeenIBeam || isIBeam);
        Reset();
        return isTextDrag;
    }

    internal void Reset()
    {
        _isLeftButtonDown = false;
        _isDragging = false;
        _hasSeenIBeam = false;
    }
}

/// <summary>
/// 定义全局鼠标拖动监听的生命周期与事件。
/// </summary>
public interface IMouseHookService : IDisposable
{
    /// <summary>
    /// 鼠标左键开始操作时触发。
    /// </summary>
    event EventHandler<Point>? SelectionStarted;

    /// <summary>
    /// 检测到可能的文本拖动选择时触发。
    /// </summary>
    event EventHandler<MouseDragCompletedEventArgs>? SelectionCompleted;

    /// <summary>
    /// 获取 Hook 是否正在运行。
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// 启动 Hook。
    /// </summary>
    /// <returns>是否启动成功。</returns>
    bool Start();

    /// <summary>
    /// 停止 Hook。
    /// </summary>
    void Stop();
}
