using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlowerWall.Audio;
using FlowerWall.Core;

namespace FlowerWall.Rendering;

/// <summary>
/// 可视化渲染器：把一帧频谱数据画成「背景 + 黑胶唱片 + 环绕声波环」，并输出一张 per-pixel alpha 位图。
///
/// 关键设计：
///   - 画布是 <see cref="RenderTargetBitmap"/>，格式 Pbgra32（预乘 alpha 的 32 位 BGRA），
///     与 UpdateLayeredWindow 期望的 DIB 布局完全一致，可以直接整块拷贝上屏。
///   - 画布尺寸 = 屏幕分辨率（背景要铺满整屏）；胶片与声波环固定像素尺寸、居中绘制，
///     因此屏幕越大留白越多，元素本身不会变形或被拉伸。
///   - 不持有窗口、不读音频设备，只接受 <see cref="SpectrumFrame"/>，便于单独测试。
///   - 静态资源（盘面位图、背景图、画刷）都在构造时准备好，每帧只做绘制与拷贝。
/// </summary>
public sealed class VisualizerRenderer : IDisposable
{
    /// <summary>位图渲染 DPI。固定 96，使 1 个绘制单位 = 1 个物理像素（进程已声明 PerMonitorV2）。</summary>
    private const double BitmapDpi = 96.0;

    /// <summary>判定「画面静止」的音量阈值。</summary>
    private const float SilenceThreshold = 0.01f;

    private readonly VisualTheme _theme;
    private readonly VinylRenderer _vinyl;
    private readonly WaveRingRenderer _wave;
    private readonly DrawingVisual _scene = new();

    private readonly Brush? _backgroundBrush;
    private readonly BitmapSource? _backgroundImage;

    private RenderTargetBitmap? _target;
    private double _timeSeconds;
    private bool _disposed;

    public VisualizerRenderer(VisualTheme theme)
    {
        _theme = theme;
        _vinyl = new VinylRenderer(theme);
        _wave = new WaveRingRenderer(theme);

        _backgroundBrush = BuildBackgroundBrush(theme);
        _backgroundImage = LoadBackgroundImage(theme);
    }

    /// <summary>画布宽。</summary>
    public int CanvasWidth => _theme.CanvasWidth;

    /// <summary>画布高。</summary>
    public int CanvasHeight => _theme.CanvasHeight;

    /// <summary>当前渲染目标位图。首次 <see cref="Render"/> 后可用。</summary>
    public RenderTargetBitmap? Target => _target;

    /// <summary>位图行距（字节）。</summary>
    public int Stride => _target is null ? 0 : _target.PixelWidth * 4;

    /// <summary>当前强调色，供悬浮按钮与托盘图标取色。</summary>
    public Color Accent => _theme.Accent;

    /// <summary>高光色。</summary>
    public Color Highlight => _theme.Highlight;

    /// <summary>胶片当前是否还在旋转。</summary>
    public bool IsSpinning => _vinyl.IsSpinning;

    /// <summary>背景图片是否加载成功。</summary>
    public bool HasBackgroundImage => _backgroundImage is not null;

    /// <summary>
    /// 渲染一帧。
    /// </summary>
    /// <param name="frame">频谱数据。</param>
    /// <param name="deltaSeconds">距上一帧的时间。</param>
    /// <param name="boost">空闲增强系数（1 为常态）。</param>
    /// <param name="glowBoost">光晕强度倍数。</param>
    /// <param name="force">true 时忽略「画面静止」判断，强制重绘。</param>
    /// <returns>true 表示位图内容有更新；false 表示画面静止，调用方可以跳过上屏。</returns>
    public bool Render(SpectrumFrame frame, double deltaSeconds, float boost, float glowBoost = 1f, bool force = false)
    {
        if (_disposed) { return false; }

        EnsureTarget();

        var spinning = _vinyl.Advance(deltaSeconds, frame.Peak);
        var flowing = Math.Abs(_theme.FlowSpeed) > 0.0001f;

        // 画面静止（唱片停转、声波环不流动、频谱全零）时跳过重绘，空闲 CPU 可以降到接近 0。
        // 注意：声波环在流动时不能跳过，否则动画会冻结。
        if (!force && !spinning && !flowing && IsSilent(frame))
        {
            return false;
        }

        _timeSeconds += deltaSeconds;

        var centerX = _theme.CenterX;
        var centerY = _theme.CenterY;

        using (var context = _scene.RenderOpen())
        {
            context.DrawRectangle(
                Brushes.Transparent,
                null,
                new Rect(0, 0, _theme.CanvasWidth, _theme.CanvasHeight));

            DrawBackground(context);
            _vinyl.Draw(context, centerX, centerY);
            _wave.Draw(context, centerX, centerY, frame, boost, glowBoost, _timeSeconds);
        }

        var target = _target!;
        target.Clear();
        target.Render(_scene);

        return true;
    }

    /// <summary>释放位图。换显示器或改配置后重建时调用。</summary>
    public void Dispose()
    {
        if (_disposed) { return; }

        _disposed = true;
        _target = null;
    }

    /// <summary>
    /// 绘制背景：铺满整个画布（= 整个桌面）。
    /// 纯色模式下带一点点径向过渡，因为全屏大面积纯色会显得很平。
    /// </summary>
    private void DrawBackground(DrawingContext context)
    {
        if (_theme.BackgroundMode == BackgroundMode.None || _theme.BackgroundOpacity <= 0.001f)
        {
            return;
        }

        var bounds = new Rect(0, 0, _theme.CanvasWidth, _theme.CanvasHeight);

        if (_theme.BackgroundMode == BackgroundMode.Image && _backgroundImage is not null)
        {
            context.PushOpacity(_theme.BackgroundOpacity);
            DrawImageFitted(context, bounds);
            context.Pop();
            return;
        }

        // 纯色 / 极淡径向过渡。图片模式下若图片加载失败也会走到这里，等同安静地退回纯色。
        if (_backgroundBrush is not null)
        {
            context.PushOpacity(_theme.BackgroundOpacity);
            context.DrawRectangle(_backgroundBrush, null, bounds);
            context.Pop();
        }
    }

    private void DrawImageFitted(DrawingContext context, Rect bounds)
    {
        var image = _backgroundImage!;
        var imageAspect = (double)image.PixelWidth / Math.Max(1, image.PixelHeight);
        var boxAspect = bounds.Width / Math.Max(1, bounds.Height);

        double width;
        double height;

        if (_theme.BackgroundFitCover)
        {
            // Cover：按短边铺满整屏，超出的部分被窗口裁掉。
            if (imageAspect > boxAspect)
            {
                height = bounds.Height;
                width = bounds.Height * imageAspect;
            }
            else
            {
                width = bounds.Width;
                height = bounds.Width / imageAspect;
            }
        }
        else
        {
            // Contain：整张图完整显示，留出空白。
            if (imageAspect > boxAspect)
            {
                width = bounds.Width;
                height = bounds.Width / imageAspect;
            }
            else
            {
                height = bounds.Height;
                width = bounds.Height * imageAspect;
            }
        }

        var x = bounds.X + ((bounds.Width - width) / 2);
        var y = bounds.Y + ((bounds.Height - height) / 2);

        context.DrawImage(image, new Rect(x, y, width, height));
    }

    /// <summary>
    /// 构建背景画刷。
    ///
    /// 默认返回**纯色**，这是实测后的结论：全屏（1920×1080）铺一层径向渐变要 11.7ms/帧，
    /// 而纯色只要 2.1ms —— 差价 5 倍以上，对"占用尽可能小"的目标来说不值得。
    /// 想加一点中心到边缘的层次，把 <c>background.gradientStrength</c> 调大于 0 即可，
    /// 那时会换成一段平缓的线性渐变（仍比径向渐变便宜）。
    /// </summary>
    private static Brush? BuildBackgroundBrush(VisualTheme theme)
    {
        if (theme.BackgroundMode == BackgroundMode.Image) { return null; }

        var baseColor = theme.BackgroundColor;
        var centerColor = theme.BackgroundCenterColor;
        var strength = theme.BackgroundGradientStrength;

        // 强度为 0（或中心色与主色相同）时直接用纯色。
        if (strength <= 0.001f || centerColor == baseColor)
        {
            var flat = new SolidColorBrush(baseColor);
            flat.Freeze();
            return flat;
        }

        // 需要层次时用一段从左上到右下的线性渐变：比径向渐变便宜，观感也够用。
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.0, 0.0),
            EndPoint = new Point(0.0, 1.0),
        };

        brush.GradientStops.Add(new GradientStop(centerColor, 0.0));
        brush.GradientStops.Add(new GradientStop(baseColor, Math.Clamp(strength, 0.05f, 1f)));
        brush.GradientStops.Add(new GradientStop(baseColor, 1.0));
        brush.Freeze();

        return brush;
    }

    /// <summary>
    /// 加载背景图片。支持本地文件与 http(s) 地址。
    /// 加载失败既不抛异常也不影响其他功能 —— 背景退回纯色即可，
    /// 一张图路径写错不该让整个壁纸起不来。
    /// </summary>
    private static BitmapSource? LoadBackgroundImage(VisualTheme theme)
    {
        if (theme.BackgroundMode != BackgroundMode.Image) { return null; }

        var path = theme.BackgroundImage;
        if (string.IsNullOrWhiteSpace(path)) { return null; }

        try
        {
            var uri = Uri.TryCreate(path, UriKind.Absolute, out var absolute) && !absolute.IsFile
                ? absolute
                : new Uri(Path.GetFullPath(path));

            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = uri;
            image.CacheOption = BitmapCacheOption.OnLoad;   // 立即读完，避免长期占用文件
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            image.Freeze();

            return image;
        }
        catch (Exception exception) when (exception is IOException
                                          or NotSupportedException
                                          or UriFormatException
                                          or ArgumentException
                                          or System.Net.WebException)
        {
            RuntimeStats.WriteLine($"background image failed to load: {path} ({exception.GetType().Name})");
            return null;
        }
    }

    private void EnsureTarget()
    {
        if (_target is not null) { return; }

        _target = new RenderTargetBitmap(
            _theme.CanvasWidth,
            _theme.CanvasHeight,
            BitmapDpi,
            BitmapDpi,
            PixelFormats.Pbgra32);
    }

    /// <summary>判断频谱是否已经全静音（所有条都接近 0）。</summary>
    private static bool IsSilent(SpectrumFrame frame)
    {
        if (frame.Peak > SilenceThreshold) { return false; }

        var levels = frame.Levels;
        for (var i = 0; i < frame.BandCount; i++)
        {
            if (levels[i] > SilenceThreshold) { return false; }
        }

        return true;
    }

    // ------------------------------------------------------------------ 诊断辅助
    // 下面几个方法把绘制拆成独立阶段，供诊断工具分别计时。
    // 整帧耗时无法定位瓶颈（背景？盘体？还是声波环？），拆开才能判断优化往哪做。

    /// <summary>只绘制清屏与背景。仅用于性能诊断。</summary>
    public void DrawBackgroundOnly(DrawingContext context)
    {
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, _theme.CanvasWidth, _theme.CanvasHeight));
        DrawBackground(context);
    }

    /// <summary>只清屏，不画背景。仅用于性能诊断 —— 用来把清屏成本从背景成本里分离出来。</summary>
    public void DrawClearOnly(DrawingContext context)
        => context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, _theme.CanvasWidth, _theme.CanvasHeight));

    /// <summary>把背景画成完全平坦的纯色。仅用于性能诊断 —— 用来对比渐变与纯色的成本差。</summary>
    public void DrawFlatBackgroundOnly(DrawingContext context)
    {
        context.DrawRectangle(
            new SolidColorBrush(_theme.BackgroundColor),
            null,
            new Rect(0, 0, _theme.CanvasWidth, _theme.CanvasHeight));
    }

    /// <summary>画布中心 X（胶片与声波环的圆心）。</summary>
    public double CenterX => _theme.CenterX;

    /// <summary>画布中心 Y。</summary>
    public double CenterY => _theme.CenterY;

    /// <summary>胶片边缘到声波环最大外缘的半径。诊断工具用它决定放大取景范围。</summary>
    public double VisualRadius => _theme.BarMaxRadius + _theme.ReflectionLength;

    /// <summary>只绘制胶片（含边缘光晕）。仅用于性能诊断与局部放大预览。</summary>
    public void DrawVinylOnly(DrawingContext context)
        => _vinyl.Draw(context, _theme.CenterX, _theme.CenterY);

    /// <summary>只绘制胶片边缘光晕。仅用于性能诊断。</summary>
    public void DrawVinylHaloOnly(DrawingContext context)
        => _vinyl.DrawHalo(context, _theme.CenterX, _theme.CenterY);

    /// <summary>只绘制盘面位图。仅用于性能诊断。</summary>
    public void DrawVinylDiscOnly(DrawingContext context)
        => _vinyl.DrawDisc(context, _theme.CenterX, _theme.CenterY);

    /// <summary>只绘制轴孔与中心文字（不随盘旋转的部分）。仅用于性能诊断。</summary>
    public void DrawVinylLabelOnly(DrawingContext context)
        => _vinyl.DrawCenterLayerOnly(context, _theme.CenterX, _theme.CenterY);

    /// <summary>只绘制声波环。仅用于性能诊断。</summary>
    public void DrawRingOnly(DrawingContext context, SpectrumFrame frame, float boost)
        => _wave.Draw(context, _theme.CenterX, _theme.CenterY, frame, boost, 1f, _timeSeconds);
}
