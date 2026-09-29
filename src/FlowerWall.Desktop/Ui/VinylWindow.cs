using System.Drawing;
using System.Windows.Forms;
using FlowerWall.Audio;
using FlowerWall.Interop;
using FlowerWall.Rendering;

namespace FlowerWall.Ui;

/// <summary>
/// 可视化呈现窗口（分层窗口 + per-pixel alpha 上屏）。
///
/// 形态：无边框、无任务栏项、不可激活的顶层窗口，尺寸等于渲染画布。
/// 位置：始终居中于主显示器，只覆盖可视化区域而不是整个屏幕（更小 = 更省带宽）。
/// 交互：完全鼠标穿透（WM_NCHITTEST → HTTRANSPARENT），绝不拦截桌面点击。
/// 层级：显示时压到 z 序底部，位于壁纸之上、普通窗口之下。
///
/// 淡入淡出：改用 UpdateLayeredWindow 的 SourceConstantAlpha。
/// 淡出期间画面内容不变，只重推同一张位图，几乎不消耗 CPU。
///
/// 本类**不自己跑定时器**：帧循环由 <see cref="WallpaperApplication"/> 统一驱动，
/// 这样「是否渲染」「按什么帧率渲染」只有一个决策点。
/// </summary>
internal sealed class VinylWindow : Form
{
    private readonly VisualizerRenderer _renderer;
    private readonly LayeredWindowSurface _surface = new();

    private bool _shown;
    private byte _lastPushedAlpha = byte.MaxValue;

    public VinylWindow(VisualizerRenderer renderer)
    {
        _renderer = renderer;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = false;
        Text = "桌面音频可视化壁纸";
        BackColor = Color.Black;
        AutoScaleMode = AutoScaleMode.None;
        Enabled = false; // 永不接收焦点

        LayoutCanvas();
    }

    /// <summary>画布宽（= 主显示器宽度）。窗口就是一块覆盖整屏的透明画布。</summary>
    public int CanvasWidth => _renderer.CanvasWidth;

    /// <summary>画布高（= 主显示器高度）。</summary>
    public int CanvasHeight => _renderer.CanvasHeight;

    /// <summary>窗口当前是否处于显示状态（未挂起）。</summary>
    public bool IsSurfaceVisible => _shown;

    /// <summary>
    /// 呈现一帧。
    /// </summary>
    /// <param name="frame">频谱数据。</param>
    /// <param name="opacity">目标不透明度 0~1。</param>
    /// <param name="deltaSeconds">距上一帧的时间。</param>
    /// <param name="boost">空闲增强系数。</param>
    /// <param name="glowBoost">光晕强度倍数。</param>
    public void Present(SpectrumFrame frame, float opacity, double deltaSeconds, float boost, float glowBoost)
    {
        var alpha = (byte)Math.Clamp((int)MathF.Round(opacity * 255f), 0, 255);
        if (alpha == 0) { return; }

        EnsureShown();

        // force = true 的场景：窗口刚显示，位图里还是空白内容，必须完整画一帧。
        var force = _lastPushedAlpha == byte.MaxValue;
        var contentChanged = _renderer.Render(frame, deltaSeconds, boost, glowBoost, force);

        if (!contentChanged && alpha == _lastPushedAlpha)
        {
            // 画面与透明度都没变（例如静音且唱片已停转）：整帧跳过。
            return;
        }

        var target = _renderer.Target;
        if (target is null) { return; }

        _surface.Update(target, new Point32 { X = Left, Y = Top }, alpha);
        _lastPushedAlpha = alpha;
    }

    /// <summary>隐藏窗口并释放上屏占用。再次 <see cref="Present"/> 时会自动显示。</summary>
    public void Suspend()
    {
        if (!_shown) { return; }

        _shown = false;
        _lastPushedAlpha = byte.MaxValue;
        Hide();
    }

    /// <summary>显示器参数变化后重新定位。</summary>
    public void Relayout()
    {
        LayoutCanvas();

        if (_shown)
        {
            NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HWND_BOTTOM,
                Left,
                Top,
                Width,
                Height,
                NativeMethods.SWP_NOACTIVATE);
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= NativeMethods.WS_EX_LAYERED
                                  | NativeMethods.WS_EX_TRANSPARENT
                                  | NativeMethods.WS_EX_NOACTIVATE
                                  | NativeMethods.WS_EX_TOOLWINDOW;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        _surface.Handle = Handle;
        _surface.Resize(_renderer.CanvasWidth, _renderer.CanvasHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // 分层窗口由 UpdateLayeredWindow 负责呈现，WinForms 的 WM_PAINT 不参与。
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_NCHITTEST)
        {
            m.Result = new IntPtr(NativeMethods.HTTRANSPARENT);
            return;
        }

        if (m.Msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            m.Result = new IntPtr(NativeMethods.MA_NOACTIVATE);
            return;
        }

        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _surface.Dispose();
        }

        base.Dispose(disposing);
    }

    private void EnsureShown()
    {
        if (_shown) { return; }

        _shown = true;
        Show();

        // 压到 z 序底部：位于壁纸之上、普通窗口之下。
        NativeMethods.SetWindowPos(
            Handle,
            NativeMethods.HWND_BOTTOM,
            Left,
            Top,
            Width,
            Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE);
    }

    /// <summary>
    /// 把窗口铺满主显示器并同步尺寸。
    /// 画布 = 屏幕分辨率，因此这里就是整屏对齐；胶片与声波环由渲染层居中绘制。
    /// </summary>
    private void LayoutCanvas()
    {
        var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, _renderer.CanvasWidth, _renderer.CanvasHeight);

        Width = _renderer.CanvasWidth;
        Height = _renderer.CanvasHeight;
        Left = bounds.Left;
        Top = bounds.Top;
    }
}
