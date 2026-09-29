using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FlowerWall.Rendering;

/// <summary>
/// 黑胶唱片绘制。
///
/// 实现取向：盘面（渐变底 + 纹路 + 反光 + 磨损）**预光栅化成一张位图**，每帧只做一次带旋转的拷贝；
/// 只有中心文字与轴孔每帧绘制，因为它们不随盘旋转（文字要始终正立可读）。
/// 早期版本把纹路留在保留模式几何里，WPF 每帧重画几十条描边，单这一步就吃掉 14ms/帧。
///
/// 真实感来自五层叠加，顺序即绘制顺序：
///   1. 偏移的径向渐变底 —— 模拟盘面弧度与来自左上方的打光；
///   2. 密纹沟槽 —— 间距带谐波扰动（真实纹路并不等距），且**外圈更亮**（反光更强）；
///   3. 径向反光带 —— 打光在沟槽上形成的一条亮带，是黑胶最显眼的特征；
///   4. 磨损 —— 细划痕、边缘磨白、污渍；
///   5. 边缘暗角与高光 —— 让盘体有厚度感。
///
/// 踩过的坑：第一版把纹路颜色设成 #1B222A（与 #07090C 的盘面对比度只有 2.5:1），
/// 并且把外圈纹路做成渐暗，结果在真实屏幕上整个盘面就是一片黑。
/// 结论：暗色材质的细节必须靠**对比度**而不是靠"画了"，并且要在 100% 缩放下确认。
/// </summary>
internal sealed class VinylRenderer
{
    /// <summary>音量持续低于阈值多久后完全停转（秒）。</summary>
    private const double StopAfterSilentSeconds = 1.2;

    /// <summary>低于此转速视为已停转（弧度/秒）。</summary>
    private const double StoppedSpeedThreshold = 0.02;

    /// <summary>纹路间距扰动中高频成分的权重。</summary>
    private const double GrooveJitterRatio = 0.22;

    /// <summary>中心文字离圆心的距离（相对标签半径）。</summary>
    private const double CenterTextOffsetRatio = 0.42;

    private readonly VisualTheme _theme;

    /// <summary>预渲染的静态盘面位图（渐变底 + 纹路 + 反光 + 磨损 + 暗角 + 高光）。</summary>
    private readonly ImageSource? _discSprite;

    private readonly Typeface _scriptTypeface;

    /// <summary>已构建的中心文字；内容只在星期变化时重建。</summary>
    private FormattedText? _centerText;
    private FormattedText? _centerTextGlow;
    private string _centerTextCache = string.Empty;

    /// <summary>当前旋转角度（弧度）。</summary>
    private double _angle;

    /// <summary>当前转速（弧度/秒）。</summary>
    private double _speed;

    /// <summary>音量低于阈值持续的时间（秒）。</summary>
    private double _silentSeconds;

    public VinylRenderer(VisualTheme theme)
    {
        _theme = theme;
        _discSprite = BuildDiscSprite(theme);

        // 花体英文：优先 Segoe Script（Win7+ 自带），逐级回退，
        // 保证在缺少花体字体的机器上仍能正常显示而不是变成方块。
        _scriptTypeface = new Typeface(
            new FontFamily("Segoe Script, Brush Script MT, Edwardian Script ITC, Lucida Handwriting, Segoe UI"),
            FontStyles.Italic,
            FontWeights.Normal,
            FontStretches.Normal);
    }

    /// <summary>当前旋转角度（弧度）。</summary>
    public double Angle => _angle;

    /// <summary>当前转速（弧度/秒）。</summary>
    public double Speed => _speed;

    /// <summary>唱片是否还在转。</summary>
    public bool IsSpinning => _speed > StoppedSpeedThreshold;

    /// <summary>
    /// 推进动画状态。
    /// </summary>
    /// <param name="deltaSeconds">距上一帧的时间。</param>
    /// <param name="level">当前音量（0~1），用于决定起转 / 停转。</param>
    /// <returns>本帧角度是否真的有变化（用于跳过无意义的整帧重绘）。</returns>
    public bool Advance(double deltaSeconds, float level)
    {
        if (deltaSeconds <= 0) { return false; }

        var spinning = level >= _theme.SpinStopThreshold;
        _silentSeconds = spinning ? 0 : _silentSeconds + deltaSeconds;

        // 有声音时按「基准转速 × 活动系数」转；静音后按惯性滑行到更慢的速度。
        var baseRadiansPerSecond = _theme.Rpm * Math.PI * 2.0 / 60.0;
        var factor = spinning ? _theme.ActiveSpeedFactor : _theme.SilentSpeedFactor;
        var target = baseRadiansPerSecond * factor;

        // 静音一小段时间后完全停转：省电，也符合真实唱机手感。
        if (!spinning && _silentSeconds > StopAfterSilentSeconds)
        {
            target = 0;
        }

        _speed += (target - _speed) * _theme.SpeedSmoothing;

        if (target == 0 && _speed < 0.01)
        {
            _speed = 0;
        }

        // 返回值必须表达「本帧角度是否变化」而不是「唱片是否在转」，
        // 否则停转后会逐帧返回 true，白白重绘上百帧。
        var moved = _speed > 0;

        _angle += _speed * deltaSeconds;
        if (_angle > Math.PI * 2)
        {
            _angle -= Math.PI * 2;
        }

        return moved;
    }

    /// <summary>把唱片绘制到指定中心。</summary>
    public void Draw(DrawingContext context, double centerX, double centerY)
    {
        // 1) 边缘光晕：浅色背景上让唱片"浮"起来。
        if (_theme.RimHighlight > 0f)
        {
            DrawHalo(context, centerX, centerY);
        }

        // 盘面、轴孔与中心文字都在同一个旋转变换里 ——
        // 也就是文字真的"印"在唱片上，会随盘一起转动（因此转到下半圈时是倒的，
        // 这是用户明确选择的观感：更像真实黑胶标签）。
        context.PushTransform(new RotateTransform(_angle * 180.0 / Math.PI, centerX, centerY));

        if (_discSprite is not null)
        {
            var radius = _theme.RecordRadius;
            context.DrawImage(
                _discSprite,
                new Rect(centerX - radius, centerY - radius, radius * 2, radius * 2));
        }

        DrawSpindle(context, centerX, centerY);
        DrawCenterText(context, centerX, centerY);

        context.Pop();
    }

    /// <summary>只绘制边缘光晕。仅用于性能诊断。</summary>
    public void DrawHalo(DrawingContext context, double centerX, double centerY)
    {
        if (_theme.RimHighlight <= 0f) { return; }

        var radius = _theme.RecordRadius * 1.045;
        var haloBrush = Freeze(new SolidColorBrush(
            ColorUtil.ScaleAlpha(_theme.Accent, 0.14f * _theme.RimHighlight)));

        context.DrawEllipse(haloBrush, null, new Point(centerX, centerY), radius, radius);
    }

    /// <summary>只绘制盘面位图（含旋转）。仅用于性能诊断。</summary>
    public void DrawDisc(DrawingContext context, double centerX, double centerY)
    {
        if (_discSprite is null) { return; }

        var radius = _theme.RecordRadius;

        context.PushTransform(new RotateTransform(_angle * 180.0 / Math.PI, centerX, centerY));
        context.DrawImage(
            _discSprite,
            new Rect(centerX - radius, centerY - radius, radius * 2, radius * 2));
        context.Pop();
    }

    /// <summary>只绘制轴孔与中心文字。仅用于性能诊断。</summary>
    public void DrawCenterLayerOnly(DrawingContext context, double centerX, double centerY)
    {
        DrawSpindle(context, centerX, centerY);
        DrawCenterText(context, centerX, centerY);
    }

    private void DrawSpindle(DrawingContext context, double centerX, double centerY)
    {
        var spindleRadius = _theme.SpindleDiameter / 2f;

        var spindleBrush = Freeze(new SolidColorBrush(_theme.Disc));
        var spindlePen = new Pen(
            Freeze(new SolidColorBrush(ColorUtil.ScaleAlpha(_theme.Accent, 0.45f))),
            1.2);
        spindlePen.Freeze();

        context.DrawEllipse(spindleBrush, spindlePen, new Point(centerX, centerY), spindleRadius, spindleRadius);
    }

    /// <summary>
    /// 中心文字：只显示星期，用花体英文，中英界面下都一样。
    ///
    /// 位置在轴孔下方一点，内容不随界面语言变化 —— 这是刻意的：
    /// 唱片标签上的字是装饰，不该跟着 UI 语言跳来跳去。
    /// </summary>
    private void DrawCenterText(DrawingContext context, double centerX, double centerY)
    {
        EnsureCenterText(out var glow, out var text);
        if (glow is null || text is null) { return; }

        var y = centerY + (_theme.LabelRadius * CenterTextOffsetRatio);
        var x = centerX - (text.Width / 2);

        // 先画一层柔光（同一句话用半透明色略微放大偏移），让花体看起来是印上去的而不是贴上去的。
        context.DrawText(glow, new Point(x, y + 1.5));
        context.DrawText(text, new Point(x, y));
    }

    private void EnsureCenterText(out FormattedText? glow, out FormattedText? text)
    {
        var weekday = DateTime.Now.DayOfWeek.ToString();
        if (weekday == _centerTextCache && _centerText is not null && _centerTextGlow is not null)
        {
            glow = _centerTextGlow;
            text = _centerText;
            return;
        }

        _centerTextCache = weekday;

        var fontSize = Math.Max(11.0, _theme.LabelRadius * _theme.CenterTextScale);
        var softBrush = Freeze(new SolidColorBrush(ColorUtil.ScaleAlpha(_theme.Accent, 0.35f)));
        var solidBrush = Freeze(new SolidColorBrush(_theme.Accent));

        _centerTextGlow = new FormattedText(
            weekday,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _scriptTypeface,
            fontSize,
            softBrush,
            96.0);

        _centerText = new FormattedText(
            weekday,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _scriptTypeface,
            fontSize,
            solidBrush,
            96.0);

        glow = _centerTextGlow;
        text = _centerText;
    }

    // ------------------------------------------------------------------ 盘面预渲染

    /// <summary>
    /// 预渲染整张盘面。只做一次，之后每帧只做一次带旋转的拷贝。
    /// </summary>
    private static ImageSource? BuildDiscSprite(VisualTheme theme)
    {
        var diameter = theme.RecordDiameter;
        if (diameter <= 0) { return null; }

        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            var center = diameter / 2.0;
            var radius = center;

            DrawDiscBase(context, theme, center, radius);

            // 暗角必须在纹路之前、反光之前：它负责把最外圈压暗形成"厚度"。
            // 早期版本把暗角放在最后，结果它把外圈的纹路一起盖掉了 ——
            // 而外圈恰恰是反光最强、最该看清沟槽的地方。
            DrawVignette(context, center, radius);

            DrawGrooves(context, theme, center, radius);
            DrawSpecular(context, theme, center, radius);
            DrawWear(context, theme, center, radius);
            DrawLabelShape(context, theme, center);
            DrawRim(context, theme, center, radius);
        }

        var bitmap = new RenderTargetBitmap(diameter, diameter, 96.0, 96.0, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        return bitmap;
    }

    /// <summary>盘面底色：偏移的径向渐变，模拟弧度与左上方的打光。</summary>
    private static void DrawDiscBase(DrawingContext context, VisualTheme theme, double center, double radius)
    {
        var gradient = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.38, 0.34),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.68,
            RadiusY = 0.68,
        };
        gradient.GradientStops.Add(new GradientStop(ColorUtil.Scale(theme.Disc, 2.6f), 0.0));
        gradient.GradientStops.Add(new GradientStop(ColorUtil.Scale(theme.Disc, 1.7f), 0.30));
        gradient.GradientStops.Add(new GradientStop(ColorUtil.Scale(theme.Disc, 1.15f), 0.68));
        gradient.GradientStops.Add(new GradientStop(theme.Disc, 1.0));
        gradient.Freeze();

        context.DrawEllipse(gradient, null, new Point(center, center), radius, radius);
    }

    /// <summary>
    /// 密纹沟槽。三处细节决定"像不像"：
    /// 1) 间距做低频幂曲线（外圈略密）+ 两个不可通约谐波抖动（打破等距感）；
    /// 2) 亮度**随半径上升**（外圈反光更强），这是最容易被做反的一点；
    /// 3) 线宽随半径收窄，模拟沟槽密度上升后的视觉压缩。
    /// </summary>
    private static void DrawGrooves(DrawingContext context, VisualTheme theme, double center, double radius)
    {
        if (theme.GrooveCount <= 0 || theme.GrooveOpacity <= 0f) { return; }

        var inner = theme.LabelRadius * 1.05;
        var outer = radius * 0.99;
        if (outer <= inner) { return; }

        var span = outer - inner;
        var step = span / theme.GrooveCount;
        var bob = theme.GrooveIrregularity * step * GrooveJitterRatio;

        for (var i = 0; i < theme.GrooveCount; i++)
        {
            var t = (double)i / theme.GrooveCount;

            var position = Math.Pow(t, 0.93) * span;
            var jitter = (Math.Sin(i * 2.399) * 0.6) + (Math.Sin(i * 0.771) * 0.4);
            var r = inner + position + (jitter * bob);

            // 外圈更亮（OuterBoost），并且整体透明度控制在较高水平 ——
            // 暗色材质上"看得见"靠的是对比度，不是把参数写好。
            var brightness = 1f + ((theme.GrooveOuterBoost - 1f) * (float)t);
            var alpha = theme.GrooveOpacity * (0.84f + (0.16f * (float)t));

            var color = ColorUtil.ScaleAlpha(ColorUtil.Scale(theme.Groove, brightness), alpha);

            // 线宽：内圈粗一点（0.46 倍步距），外圈细（0.30 倍）。
            // 上限 1.6px 是经验值 —— 再粗就会与邻纹粘连成块。
            var thickness = Math.Min(1.6, Math.Max(0.9, step * (0.46 - (0.16 * t))));

            var pen = new Pen(Freeze(new SolidColorBrush(color)), thickness);
            pen.Freeze();

            context.DrawEllipse(null, pen, new Point(center, center), r, r);
        }
    }

    /// <summary>
    /// 盘面高光：模拟打光在沟槽上形成的两道亮带。
    ///
    /// 实现要点（踩过的坑）：<see cref="RadialGradientBrush"/> 默认是
    /// <c>RelativeToBoundingBox</c> 坐标系 —— 它的 Center/GradientOrigin 是 0~1 的**相对比例**，
    /// 之前按绝对像素传（例如 700,540）会被当成"7 倍包围盒"的位置，
    /// 结果渐变中心跑到画面外，留下一块花瓣状色斑。
    /// 因此这里统一在 560×560 的**盘面本地坐标系**里用相对值表达，
    /// 高光位置也就不会随屏幕分辨率漂移。
    /// </summary>
    private static void DrawSpecular(DrawingContext context, VisualTheme theme, double center, double radius)
    {
        if (theme.SpecularStrength <= 0.01f) { return; }

        var inner = theme.LabelRadius * 1.05;
        var outer = radius * 0.99;
        var bandRadius = (inner + (outer - inner) * 0.55) / radius;   // 换算成半径比例

        // 两道高光分别落在左上与右下：这是黑胶在单一光源下的典型反光形态。
        var highlight = ColorUtil.ScaleAlpha(
            ColorUtil.Scale(theme.Groove, 1.7f),
            0.34f * theme.SpecularStrength);

        DrawHighlightBlob(context, center, radius, bandRadius, -2.35, highlight);
        DrawHighlightBlob(context, center, radius, bandRadius, 0.79, ColorUtil.ScaleAlpha(highlight, 0.55f));
    }

    private static void DrawHighlightBlob(
        DrawingContext context,
        double center,
        double radius,
        double bandRadiusRatio,
        double direction,
        Color color)
    {
        // 高光椭圆在盘面本地坐标系里的中心（0~1 相对包围盒）。
        var localCenter = new Point(
            0.5 + (Math.Cos(direction) * bandRadiusRatio * 0.5),
            0.5 + (Math.Sin(direction) * bandRadiusRatio * 0.5));

        var brush = new RadialGradientBrush
        {
            Center = localCenter,
            GradientOrigin = localCenter,
            // 沿半径方向拉长、沿切向压扁，得到一条弧形亮带而不是圆斑。
            RadiusX = 0.26,
            RadiusY = 0.13,
        };

        brush.GradientStops.Add(new GradientStop(color, 0.0));
        brush.GradientStops.Add(new GradientStop(ColorUtil.WithAlpha(color, 0), 1.0));
        brush.Freeze();

        context.DrawEllipse(brush, null, new Point(center, center), radius, radius);
    }

    /// <summary>
    /// 磨损：细划痕、边缘磨白、污渍。纯黑圆盘太"干净"反而不像实物。
    /// 强度由 <see cref="VisualTheme.WearStrength"/> 统一控制，设 0 就是崭新唱片。
    /// </summary>
    private static void DrawWear(DrawingContext context, VisualTheme theme, double center, double radius)
    {
        if (theme.WearStrength <= 0.01f) { return; }

        var inner = theme.LabelRadius * 1.08;
        var outer = radius * 0.97;
        var span = outer - inner;
        var wear = theme.WearStrength;

        // --- 细划痕：用比沟槽更亮的颜色，才会在暗盘面上真的被看见 ---
        var scratchPen = new Pen(
            Freeze(new SolidColorBrush(ColorUtil.ScaleAlpha(theme.Groove, 0.85f * wear))),
            0.9);
        scratchPen.Freeze();

        var origin = new Point(center, center);

        // 位置用黄金比例与黄金角生成：分布均匀，又不会出现可见的规律。
        for (var i = 0; i < 22; i++)
        {
            var t = (i * 0.6180339887) % 1.0;
            var r = inner + (span * t);

            var startAngle = (i * 137.5) % 360.0;
            var sweep = 8.0 + ((i * 7) % 34);

            context.DrawGeometry(null, scratchPen, CreateArcGeometry(origin, r, startAngle, sweep));
        }

        // --- 边缘磨白：唱片最外圈是搬运时最容易磨到的位置 ---
        var edgeBrush = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        edgeBrush.GradientStops.Add(new GradientStop(ColorUtil.WithAlpha(theme.Groove, 0), 0.88));
        edgeBrush.GradientStops.Add(new GradientStop(
            ColorUtil.ScaleAlpha(ColorUtil.Scale(theme.Groove, 1.3f), 0.5f * wear), 0.985));
        edgeBrush.GradientStops.Add(new GradientStop(ColorUtil.WithAlpha(theme.Groove, 0), 1.0));
        edgeBrush.Freeze();

        context.DrawEllipse(edgeBrush, null, origin, radius, radius);

        // --- 污渍：几处极淡的暗斑，打破"完美同心圆"的机械感 ---
        var smudgeBrush = new SolidColorBrush(ColorUtil.WithAlpha(Colors.Black, (byte)(26 * wear)));
        smudgeBrush.Freeze();

        for (var i = 0; i < 7; i++)
        {
            var t = ((i * 0.3819660113) + 0.11) % 1.0;
            var r = inner + (span * t);
            var angle = (i * 2.399) + 0.7;

            var cx = center + (Math.Cos(angle) * r);
            var cy = center + (Math.Sin(angle) * r);
            var blob = span * (0.06 + ((i % 3) * 0.02));

            context.DrawEllipse(smudgeBrush, null, new Point(cx, cy), blob, blob * 0.72);
        }
    }

    private static Geometry CreateArcGeometry(Point center, double radius, double startDegrees, double sweepDegrees)
    {
        var geometry = new StreamGeometry();

        using (var context = geometry.Open())
        {
            context.BeginFigure(
                PointOnCircle(center, radius, startDegrees),
                isFilled: false,
                isClosed: false);

            context.ArcTo(
                PointOnCircle(center, radius, startDegrees + sweepDegrees),
                new Size(radius, radius),
                0,
                sweepDegrees > 180,
                SweepDirection.Clockwise,
                isStroked: true,
                isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180.0;
        return new Point(center.X + (radius * Math.Cos(radians)), center.Y + (radius * Math.Sin(radians)));
    }

    /// <summary>中心标签：比盘面略亮的一圈，加两道强调色细环。</summary>
    private static void DrawLabelShape(DrawingContext context, VisualTheme theme, double center)
    {
        var labelRadius = theme.LabelRadius;
        var origin = new Point(center, center);

        var labelBrush = new SolidColorBrush(ColorUtil.Scale(theme.Disc, 2.2f));
        labelBrush.Freeze();
        context.DrawEllipse(labelBrush, null, origin, labelRadius, labelRadius);

        var ringPen = new Pen(
            Freeze(new SolidColorBrush(ColorUtil.ScaleAlpha(theme.Accent, 0.7f))),
            Math.Max(1.0, labelRadius * 0.022));
        ringPen.Freeze();
        context.DrawEllipse(null, ringPen, origin, labelRadius * 0.95, labelRadius * 0.95);

        var innerPen = new Pen(
            Freeze(new SolidColorBrush(ColorUtil.ScaleAlpha(theme.Accent, 0.32f))),
            Math.Max(0.8, labelRadius * 0.012));
        innerPen.Freeze();
        context.DrawEllipse(null, innerPen, origin, labelRadius * 0.76, labelRadius * 0.76);
    }

    /// <summary>边缘暗角：外圈压暗形成厚度感。注意它画在纹路之前，否则会把外圈沟槽一起盖掉。</summary>
    private static void DrawVignette(DrawingContext context, double center, double radius)
    {
        var vignette = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        vignette.GradientStops.Add(new GradientStop(ColorUtil.WithAlpha(Colors.Black, 0), 0.82));
        vignette.GradientStops.Add(new GradientStop(ColorUtil.WithAlpha(Colors.Black, 90), 0.96));
        vignette.GradientStops.Add(new GradientStop(ColorUtil.WithAlpha(Colors.Black, 140), 1.0));
        vignette.Freeze();

        context.DrawEllipse(vignette, null, new Point(center, center), radius, radius);
    }

    private static void DrawRim(DrawingContext context, VisualTheme theme, double center, double radius)
    {
        if (theme.RimHighlight <= 0f) { return; }

        var rimPen = new Pen(
            new SolidColorBrush(ColorUtil.ScaleAlpha(theme.Accent, 0.3f * theme.RimHighlight)),
            Math.Max(1.0, radius * 0.014));
        rimPen.Freeze();

        context.DrawEllipse(null, rimPen, new Point(center, center), radius * 0.993, radius * 0.993);
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
