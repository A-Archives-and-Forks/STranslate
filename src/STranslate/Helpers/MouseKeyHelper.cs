using Gma.System.MouseKeyHook;
using System.Runtime.InteropServices;

namespace STranslate.Helpers;

public class MouseKeyHelper
{
    private static IKeyboardMouseEvents? _mouseHook;
    private static bool _isMouseListening;
    private static string _oldText = string.Empty;
    private static Func<int> _getSelectedTextFetchTimeoutMs = () => 500;
    private static bool _isIBeamAtStart;

    /// <summary>
    /// 是否在划词结束时自动执行复制取词操作
    /// </summary>
    public static bool IsAutomaticCopy { get; set; } = true;

    /// <summary>
    /// 鼠标划词文本选择事件 (自动取词模式下触发)
    /// </summary>
    public static event Action<string>? MouseTextSelected;

    /// <summary>
    /// 鼠标划词结束事件 (非自动取词模式下触发，仅传递位置)
    /// </summary>
    public static event Action<System.Drawing.Point>? MousePointSelected;

    /// <summary>
    /// 启动鼠标划词监听
    /// </summary>
    /// <param name="getSelectedTextFetchTimeoutMs">获取当前取词等待上限的方法，单位：毫秒。</param>
    public static async Task StartMouseTextSelectionAsync(Func<int>? getSelectedTextFetchTimeoutMs = null)
    {
        if (getSelectedTextFetchTimeoutMs != null)
        {
            _getSelectedTextFetchTimeoutMs = getSelectedTextFetchTimeoutMs;
        }

        if (_isMouseListening) return;

        _mouseHook = Hook.GlobalEvents();
        _mouseHook.MouseDragStarted += OnDragStarted;
        _mouseHook.MouseDragFinished += OnDragFinished;

        _isMouseListening = true;

        // 等待钩子启动
        await Task.Delay(100);
    }

    /// <summary>
    /// 停止鼠标划词监听
    /// </summary>
    public static void StopMouseTextSelection()
    {
        if (!_isMouseListening) return;

        _isMouseListening = false;

        if (_mouseHook != null)
        {
            _mouseHook.MouseDragStarted -= OnDragStarted;
            _mouseHook.MouseDragFinished -= OnDragFinished;
            _mouseHook.Dispose();
            _mouseHook = null;
        }

        _getSelectedTextFetchTimeoutMs = () => 500;
    }

    /// <summary>
    /// 切换鼠标划词监听状态
    /// </summary>
    public static async Task ToggleMouseTextSelection()
    {
        if (_isMouseListening)
        {
            StopMouseTextSelection();
        }
        else
        {
            await StartMouseTextSelectionAsync();
        }
    }

    /// <summary>
    /// 获取鼠标划词监听状态
    /// </summary>
    public static bool IsMouseTextSelectionListening => _isMouseListening;

    private static void OnDragStarted(object? sender, System.Windows.Forms.MouseEventArgs e)
    {
        _isIBeamAtStart = IsIBeamCursor();
        // 只有自动模式才需要在开始时记录旧文本
        if (IsAutomaticCopy)
            _oldText = ClipboardHelper.GetText() ?? string.Empty;
    }

    private static void OnDragFinished(object? sender, System.Windows.Forms.MouseEventArgs e)
    {
        if (e.Button == System.Windows.Forms.MouseButtons.Left)
        {
            if (IsAutomaticCopy)
            {
                // 异步处理文本获取和事件触发
                _ = Task.Run(async () =>
                {
                    // 异步获取选中文本
                    var selectedText = await ClipboardHelper.GetSelectedTextAsync(Math.Max(1, _getSelectedTextFetchTimeoutMs()));
                    if (!string.IsNullOrEmpty(selectedText) && selectedText != _oldText)
                    {
                        MouseTextSelected?.Invoke(selectedText);
                    }
                });
            }
            else
            {
                // ★★★ 核心修改：图标模式下，增加光标形状检测 ★★★
                // 只有当光标是 "I-Beam" (文本输入/选择状) 时，才认为是选中文本
                // 这样可以过滤掉桌面选文件、拖拽窗口等操作
                if (IsIBeamCursor() || _isIBeamAtStart)
                {
                    MousePointSelected?.Invoke(e.Location);
                }
            }
        }
    }

    // --- ↓↓↓ 新增：光标检测逻辑 ↓↓↓ ---

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public System.Drawing.Point ptScreenPos;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    private const int IDC_IBEAM = 32513; // 系统标准的“工”字形文本选择光标 ID

    /// <summary>
    /// 判断当前鼠标光标是否为文本选择状态 (I-Beam)
    /// </summary>
    private static bool IsIBeamCursor()
    {
        try
        {
            var ci = new CURSORINFO();
            ci.cbSize = Marshal.SizeOf(ci);
            
            if (GetCursorInfo(ref ci))
            {
                // 获取系统标准的 I-Beam 光标句柄
                var hIBeam = LoadCursor(IntPtr.Zero, IDC_IBEAM);
                
                // 比较当前光标句柄是否等于系统 I-Beam 句柄
                // 注意：某些自定义主题或个别软件(如Word)可能使用自定义光标，这可能会导致漏判，
                // 但这是过滤桌面/文件选择最安全、无副作用的方法。
                return ci.hCursor == hIBeam;
            }
        }
        catch (Exception)
        {
            // 容错处理
        }
        return false;
    }
}
