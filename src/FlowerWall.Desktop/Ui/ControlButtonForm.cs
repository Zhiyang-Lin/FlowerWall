using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using FlowerWall.Core;
using FlowerWall.Interop;

namespace FlowerWall.Ui;

/// <summary>
/// 桌面悬浮小圆点。
///
/// 行为：
///   - 常态是一个半透明小圆点，鼠标悬停时平滑放大并显示图标；
///   - 左键单击 → 请求切换可视化；按住拖动 → 移动，松手后吸附到最近的屏幕边缘；
///   - 右键 → 弹出功能菜单。
///
/// 实现要点：与壁纸窗口共用 <see cref="LayeredWindowSurface"/>，因此同样是 per-pixel alpha，
/// 圆形边缘不会出现方框或黑边。窗口带 WS_EX_NOACTIVATE，点击它不会抢走当前程序的焦点。
/// </summary>
internal sealed class ControlButtonForm : Form
{
    /// <summary>拖动判定阈值（像素）。超过即视为拖动而不是点击。</summary>
    private const int DragThreshold = 4;

    /// <summary>悬停动画的时长（毫秒）。</summary>
    private const double HoverAnimationMs = 140.0;

    /// <summary>边缘吸附的判定距离（像素）。</summary>
    private const int SnapDistance = 80;

    private readonly ButtonSettings _settings;
    private readonly LayeredWindowSurface _surface = new();
    private readonly System.Windows.Forms.Timer _animationTimer;

    /// <summary>按钮自己的空菜单。被外部的共用菜单替换后不再由本类释放。</summary>
    private ContextMenuStrip _defaultMenu = new();

    private ContextMenuStrip? _sharedMenu;
    private Color _accent = Color.FromArgb(0xE4, 0x67, 0x9A);
    private int _diameter;
    private int _petals = 5;
    private double _hoverProgress;
    private int _targetHover;

    private bool _dragging;
    private bool _dragCandidate;
    private Point32 _dragOrigin;
    private Point32 _position;

    public ControlButtonForm(ButtonSettings settings, Color accent, int petals)
    {
        _settings = settings;
        _accent = accent;
        _petals = Math.Clamp(petals, 3, 12);
        _diameter = Math.Max(settings.Diameter, settings.HoverDiameter);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true; // 悬浮控件必须浮在普通窗口之上
        Text = Core.Localization.Text(Core.TextKey.AppName);
        BackColor = Color.Black;
        AutoScaleMode = AutoScaleMode.None;
        Cursor = Cursors.Hand;

        _animationTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _animationTimer.Tick += OnAnimationTick;

        RestorePosition();
        ApplyBounds();
    }

    /// <summary>左键单击（非拖动）时触发。</summary>
    public event EventHandler? ToggleRequested;

    /// <summary>拖动结束、位置确定后触发。只有这时才值得把位置写回配置。</summary>
    public event EventHandler? PositionChanged;

    /// <summary>
    /// 与托盘共用的右键菜单。设置后，右键悬浮按钮会直接弹出这一份，
    /// 保证两个入口的菜单内容与状态完全一致。
    /// </summary>
    public ContextMenuStrip? SharedMenu
    {
        get => _sharedMenu;
        set => _sharedMenu = value;
    }

    /// <summary>更新强调色（配置热重载时使用）。</summary>
    public void SetAccent(Color accent)
    {
        _accent = accent;
        Render();
    }

    /// <summary>把当前位置写回配置，供下次启动恢复。</summary>
    public void PersistPosition()
    {
        _settings.Position.X = _position.X;
        _settings.Position.Y = _position.Y;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= NativeMethods.WS_EX_LAYERED
                                  | NativeMethods.WS_EX_NOACTIVATE
                                  | NativeMethods.WS_EX_TOOLWINDOW;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        _surface.Handle = Handle;
        _surface.Resize(_diameter, _diameter);
        Render();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // 由 UpdateLayeredWindow 呈现。
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        StartHoverAnimation(1);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        StartHoverAnimation(0);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button == MouseButtons.Right)
        {
            if (_sharedMenu is not null)
            {
                _sharedMenu.Show(Cursor.Position);
            }

            return;
        }

        if (e.Button != MouseButtons.Left) { return; }

        _dragCandidate = true;
        _dragging = false;

        var cursor = Cursor.Position;
        _dragOrigin = new Point32 { X = cursor.X - _position.X, Y = cursor.Y - _position.Y };
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!_dragCandidate) { return; }

        var cursor = Cursor.Position;
        var offsetX = cursor.X - _dragOrigin.X;
        var offsetY = cursor.Y - _dragOrigin.Y;

        if (!_dragging)
        {
            var moved = Math.Abs(offsetX - _position.X) + Math.Abs(offsetY - _position.Y);
            if (moved < DragThreshold) { return; }

            _dragging = true;
        }

        _position = new Point32 { X = offsetX, Y = offsetY };
        ApplyBounds();
        Render();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (e.Button != MouseButtons.Left) { return; }

        var wasDragging = _dragging;
        _dragCandidate = false;
        _dragging = false;

        if (wasDragging)
        {
            SnapToNearestEdge();
            PersistPosition();
            ApplyBounds();
            Render();
            PositionChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        ToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animationTimer.Stop();
            _animationTimer.Dispose();

            // 共用菜单的所有权属于托盘，这里只释放自己创建的那一份。
            _defaultMenu.Dispose();
            _surface.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>按配置决定初始位置：优先沿用上次拖动的位置，否则放到默认角落。</summary>
    private void RestorePosition()
    {
        var bounds = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);

        if (_settings.RememberPosition && _settings.Position.IsValid)
        {
            _position = ClampToScreen(new Point32 { X = _settings.Position.X, Y = _settings.Position.Y }, bounds);
            return;
        }

        var margin = Math.Max(0, _settings.EdgeMargin);
        var (x, y) = _settings.DefaultCorner?.Trim().ToLowerInvariant() switch
        {
            "topleft" => (bounds.Left + margin, bounds.Top + margin),
            "topright" => (bounds.Right - _diameter - margin, bounds.Top + margin),
            "bottomleft" => (bounds.Left + margin, bounds.Bottom - _diameter - margin),
            _ => (bounds.Right - _diameter - margin, bounds.Bottom - _diameter - margin),
        };

        _position = new Point32 { X = x, Y = y };
    }

    /// <summary>松手后吸附到最近的屏幕边缘，避免按钮落在屏幕中间挡视线。</summary>
    private void SnapToNearestEdge()
    {
        var bounds = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        var margin = Math.Max(0, _settings.EdgeMargin);

        var centerX = _position.X + (_diameter / 2);
        var centerY = _position.Y + (_diameter / 2);

        var leftDistance = centerX - bounds.Left;
        var rightDistance = bounds.Right - centerX;
        var topDistance = centerY - bounds.Top;
        var bottomDistance = bounds.Bottom - centerY;

        // 只有当确实靠近某条边时才吸附，否则保持原位（允许用户把按钮放在顺手的地方）。
        var minimum = Math.Min(Math.Min(leftDistance, rightDistance), Math.Min(topDistance, bottomDistance));
        if (minimum > SnapDistance) { return; }

        if (minimum == leftDistance)
        {
            _position.X = bounds.Left + margin;
        }
        else if (minimum == rightDistance)
        {
            _position.X = bounds.Right - _diameter - margin;
        }
        else if (minimum == topDistance)
        {
            _position.Y = bounds.Top + margin;
        }
        else
        {
            _position.Y = bounds.Bottom - _diameter - margin;
        }

        _position = ClampToScreen(_position, bounds);
    }

    private static Point32 ClampToScreen(Point32 position, Rectangle bounds)
        => new()
        {
            X = Math.Clamp(position.X, bounds.Left, Math.Max(bounds.Left, bounds.Right - 8)),
            Y = Math.Clamp(position.Y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - 8)),
        };

    private void ApplyBounds()
    {
        var x = _position.X - ((_diameter - _settings.Diameter) / 2);
        var y = _position.Y - ((_diameter - _settings.Diameter) / 2);

        NativeMethods.SetWindowPos(
            Handle,
            NativeMethods.HWND_TOPMOST,
            x,
            y,
            _diameter,
            _diameter,
            NativeMethods.SWP_NOACTIVATE);
    }

    private void StartHoverAnimation(int target)
    {
        if (_targetHover == target) { return; }

        _targetHover = target;
        _animationTimer.Start();
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        const double step = 16.0 / HoverAnimationMs;

        if (_hoverProgress < _targetHover)
        {
            _hoverProgress = Math.Min(_targetHover, _hoverProgress + step);
        }
        else if (_hoverProgress > _targetHover)
        {
            _hoverProgress = Math.Max(_targetHover, _hoverProgress - step);
        }

        Render();

        if (Math.Abs(_hoverProgress - _targetHover) < 0.001)
        {
            _animationTimer.Stop();
        }
    }

    /// <summary>
    /// 绘制梅花并上屏。
    ///
    /// 常态是半透明的小花；悬停时放大、提亮、外发光增强，提示可点击。
    /// 花的形状与托盘图标、盘面刻印共用同一套几何，因此三处是同一朵花。
    /// </summary>
    private void Render()
    {
        if (_surface.Handle == IntPtr.Zero) { return; }

        _surface.Resize(_diameter, _diameter);

        using var bitmap = new Bitmap(_diameter, _diameter, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);

        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            var center = _diameter / 2f;
            var normalRadius = Math.Max(2f, _settings.Diameter / 2f);
            var hoverRadius = Math.Max(normalRadius, _settings.HoverDiameter / 2f);
            var radius = normalRadius + ((hoverRadius - normalRadius) * (float)_hoverProgress);

            // 整体不透明度：常态偏透（不打扰桌面），悬停时完全不透明。
            var opacity = (float)(_settings.IdleOpacity + ((1.0 - _settings.IdleOpacity) * _hoverProgress));

            // 外发光：悬停时明显增强，代替"按钮边框"做可点击提示。
            var glowAlpha = (int)Math.Clamp((30 + (80 * _hoverProgress)) * opacity, 0, 255);
            using (var glowPath = new GraphicsPath())
            {
                glowPath.AddEllipse(center - (radius * 1.7f), center - (radius * 1.7f), radius * 3.4f, radius * 3.4f);

                using var glowBrush = new PathGradientBrush(glowPath)
                {
                    CenterColor = Color.FromArgb(glowAlpha, _accent),
                    SurroundColors = [Color.FromArgb(0, _accent)],
                };

                graphics.FillPath(glowBrush, glowPath);
            }

            // 花本身：主色填充；悬停时描边更亮更粗，避免在深色桌面或浅色壁纸上都糊掉。
            var palette = IconPalette.FromAccent(_accent);
            var outlineWidth = Math.Max(1f, radius * (0.10f + (0.06f * (float)_hoverProgress)));

            BlossomIconRenderer.Draw(
                graphics,
                new RectangleF(center - radius, center - radius, radius * 2, radius * 2),
                WithOpacity(palette, opacity),
                _petals,
                rotationRadians: -Math.PI / 2,
                outlineWidth: outlineWidth);
        }

        _surface.Update(bitmap, _position, 255);
    }

    /// <summary>给整套图标配色乘以一个不透明度。</summary>
    private static IconPalette WithOpacity(IconPalette palette, float opacity)
    {
        var alpha = (byte)Math.Clamp((int)Math.Round(255 * opacity), 0, 255);

        return new IconPalette(
            Color.FromArgb(alpha, palette.Petal),
            Color.FromArgb(alpha, palette.Core),
            Color.FromArgb(alpha, palette.Outline));
    }
}
