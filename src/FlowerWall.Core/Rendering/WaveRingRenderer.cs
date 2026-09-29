using System.Windows;
using System.Windows.Media;
using FlowerWall.Audio;

namespace FlowerWall.Rendering;

/// <summary>
/// 环绕胶片的分立短条（圆形放射状频谱）。
///
/// 与之前的「连续波浪」相比，这里每个频段是一根**独立的圆角短条**，
/// 条与条之间留出清楚的间隙 —— 观感是"梳齿"而不是"一片水幕"。
/// 低音冲上来时是局部几根明显变长，而不是整圈水面鼓起。
///
/// 这一版保留了几轮实测换来的经验：
///   - 条要**细**且不透明度高（0.8~1.0）：早期用半透明粗条，相邻条半透明叠加会糊成一片；
///   - 条长随音量增加，但**条的粗细保持恒定**：变粗是"粘黏感"的主因；
///   - 峰值用一根更亮的短帽标出，给出节奏层次而不增加面积。
///
/// 性能：所有缓冲与画刷在构造或参数变化时创建，<see cref="Draw"/> 运行期零托管分配。
/// </summary>
internal sealed class WaveRingRenderer
{
    /// <summary>采样点上限。</summary>
    private const int MaxPoints = 240;

    /// <summary>颜色量化级数，用于缓存画刷与画笔。</summary>
    private const int ColorLevels = 14;

    private readonly VisualTheme _theme;

    /// <summary>每个采样点的角度与单位向量，只算一次。</summary>
    private readonly double[] _cos;
    private readonly double[] _sin;

    private readonly Pen[] _barPens = new Pen[ColorLevels + 1];
    private readonly Pen[] _secondaryPens = new Pen[ColorLevels + 1];
    private readonly Pen[] _reflectionPens = new Pen[ColorLevels + 1];
    private readonly Brush[] _peakBrushes = new Brush[ColorLevels + 1];

    private SolidColorBrush? _glowBrush;
    private float _paletteBoost = float.NaN;
    private float _paletteGlow = float.NaN;

    public WaveRingRenderer(VisualTheme theme)
    {
        _theme = theme;

        var count = Math.Clamp(theme.BandCount, 8, MaxPoints);
        Points = count;

        _cos = new double[count];
        _sin = new double[count];

        var angle = theme.StartAngleDeg * Math.PI / 180.0;
        var step = Math.PI * 2 / count;

        for (var i = 0; i < count; i++)
        {
            _cos[i] = Math.Cos(angle);
            _sin[i] = Math.Sin(angle);
            angle += step;
        }
    }

    /// <summary>实际参与绘制的条数。</summary>
    public int Points { get; }

    /// <summary>
    /// 绘制频谱。
    /// </summary>
    /// <param name="context">目标画布上下文。</param>
    /// <param name="centerX">胶片中心 X。</param>
    /// <param name="centerY">胶片中心 Y。</param>
    /// <param name="frame">频谱数据。</param>
    /// <param name="boost">空闲增强系数，1 表示常态。</param>
    /// <param name="glowBoost">光晕强度倍数。</param>
    /// <param name="timeSeconds">累计时间。梳齿模式下不用于流动，仅保留接口一致。</param>
    public void Draw(
        DrawingContext context,
        double centerX,
        double centerY,
        SpectrumFrame frame,
        float boost,
        float glowBoost,
        double timeSeconds)
    {
        _ = timeSeconds;

        var bandCount = Math.Min(frame.BandCount, _theme.BandCount);
        if (bandCount <= 0) { return; }

        EnsurePalette(boost, glowBoost);

        var levels = frame.Levels;
        var peaks = frame.Peaks;
        var baseRadius = _theme.BarStartRadius;
        var amplitude = _theme.MaxBarLength - _theme.MinBarLength;
        var origin = new Point(centerX, centerY);

        // 1) 整环柔光：细一点，只做氛围，不能盖过短条本身。
        if (_theme.GlowStrength > 0f && _glowBrush is not null)
        {
            var glowRadius = baseRadius + (_theme.MaxBarLength * 0.5f);
            var glowPen = new Pen(_glowBrush, _theme.MaxBarLength * 0.26);
            glowPen.Freeze();
            context.DrawEllipse(null, glowPen, origin, glowRadius, glowRadius);
        }

        // 2) 主条 + 副条 + 外侧倒影。
        //
        // 绘制顺序：倒影 → 副条 → 主条，保证主条压在最上层，不会被副条糊住。
        var barCount = Math.Min(bandCount, Points);

        if (_theme.ReflectionOpacity > 0.01f && _theme.ReflectionLength > 1f)
        {
            DrawBars(context, centerX, centerY, levels, barCount, baseRadius, amplitude,
                lengthScale: 1f, offset: 0f, _reflectionPens, drawPeaks: false);
        }

        if (_theme.SecondaryOpacity > 0.01f)
        {
            DrawBars(context, centerX, centerY, levels, barCount, baseRadius,
                amplitude * _theme.SecondaryAmplitude,
                lengthScale: 1f,
                offset: _theme.SecondaryPhaseRadians,
                _secondaryPens,
                drawPeaks: false);
        }

        DrawBars(context, centerX, centerY, levels, barCount, baseRadius, amplitude,
            lengthScale: 1f, offset: 0f, _barPens, drawPeaks: true, peaksOverride: peaks);
    }

    /// <summary>
    /// 画一圈短条。
    /// </summary>
    /// <param name="levels">频段电平。</param>
    /// <param name="barCount">条数。</param>
    /// <param name="baseRadius">所有条的起始半径。</param>
    /// <param name="amplitude">满格时的长度。</param>
    /// <param name="lengthScale">长度倍数（副条用）。</param>
    /// <param name="offset">相位偏移（弧度，副条用）。</param>
    /// <param name="pens">按量级分桶的画笔。</param>
    /// <param name="drawPeaks">是否绘制峰值亮帽。</param>
    /// <param name="peaksOverride">峰值数组。</param>
    private void DrawBars(
        DrawingContext context,
        double centerX,
        double centerY,
        float[] levels,
        int barCount,
        float baseRadius,
        float amplitude,
        float lengthScale,
        double offset,
        Pen[] pens,
        bool drawPeaks,
        float[]? peaksOverride = null)
    {
        var step = Math.PI * 2 / Points;
        var phaseShift = offset / step;

        for (var i = 0; i < barCount; i++)
        {
            // 相位偏移用小数采样位置实现，避免副条与主条完全重合。
            var sourceIndex = i + phaseShift;
            var lower = (int)Math.Floor(sourceIndex);
            var fraction = sourceIndex - lower;

            var indexA = ((lower % barCount) + barCount) % barCount;
            var indexB = ((lower + 1) % barCount + barCount) % barCount;

            var level = (float)((levels[indexA] * (1.0 - fraction)) + (levels[indexB] * fraction));
            level = Math.Clamp(level, 0f, 1f);

            var length = _theme.MinBarLength + (level * amplitude * lengthScale);

            var cos = _cos[i];
            var sin = _sin[i];

            var inner = new Point(centerX + (cos * baseRadius), centerY + (sin * baseRadius));
            var outer = new Point(
                centerX + (cos * (baseRadius + length)),
                centerY + (sin * (baseRadius + length)));

            context.DrawLine(pens[LevelIndex(level)], inner, outer);

            if (!drawPeaks || peaksOverride is null) { continue; }

            var peak = Math.Clamp(peaksOverride[indexA], 0f, 1f);
            if (peak <= level + 0.06f) { continue; }

            // 峰值：在最高处点一个短帽，比条本身更亮，给出节奏层次。
            var peakRadius = baseRadius + _theme.MinBarLength + (peak * amplitude * lengthScale);
            var capStart = new Point(
                centerX + (cos * (peakRadius - _theme.BarWidth * 0.9)),
                centerY + (sin * (peakRadius - _theme.BarWidth * 0.9)));
            var capEnd = new Point(
                centerX + (cos * peakRadius),
                centerY + (sin * peakRadius));

            context.DrawLine(new Pen(_peakBrushes[ColorLevels], _theme.BarWidth * 1.15), capStart, capEnd);
        }
    }

    private static int LevelIndex(float level)
        => Math.Clamp((int)MathF.Round(level * ColorLevels), 0, ColorLevels);

    /// <summary>重建画刷与画笔缓存。仅在增强参数变化时执行。</summary>
    private void EnsurePalette(float boost, float glowBoost)
    {
        if (Math.Abs(boost - _paletteBoost) < 0.001f && Math.Abs(glowBoost - _paletteGlow) < 0.001f) { return; }

        _paletteBoost = boost;
        _paletteGlow = glowBoost;

        // 空闲增强让短条更亮，但封顶 1.35 倍 —— 再高就失去"梳齿"的清爽感。
        var clampedBoost = Math.Clamp(boost, 0.5f, 1.35f);
        var clampedGlow = Math.Clamp(glowBoost, 0.5f, 3f);
        var barWidth = _theme.BarWidth;

        for (var i = 0; i <= ColorLevels; i++)
        {
            var t = (float)i / ColorLevels;

            // 接近实心（0.8~1.0）：半透明短条相互叠加会糊成一片，那正是"粘黏感"的来源。
            var depth = 0.84f + (0.16f * t);
            var alpha = Math.Min(1f, (0.8f + (0.2f * t)) * clampedBoost);

            var color = ColorUtil.ScaleAlpha(ColorUtil.Scale(_theme.Accent, depth), alpha);

            _barPens[i] = CreatePen(color, barWidth);

            _secondaryPens[i] = CreatePen(
                ColorUtil.ScaleAlpha(color, _theme.SecondaryOpacity),
                barWidth * 0.92f);

            _reflectionPens[i] = CreatePen(
                ColorUtil.ScaleAlpha(color, _theme.ReflectionOpacity),
                barWidth * 1.5f);

            var peakBrush = new SolidColorBrush(
                ColorUtil.ScaleAlpha(ColorUtil.Scale(_theme.Highlight, 1.0f), Math.Min(1f, alpha * 0.95f)));
            peakBrush.Freeze();
            _peakBrushes[i] = peakBrush;
        }

        _glowBrush = Freeze(new SolidColorBrush(
            ColorUtil.ScaleAlpha(_theme.Accent, 0.04f * _theme.GlowStrength * clampedGlow)));
    }

    private static Pen CreatePen(Color color, double width)
    {
        var pen = new Pen(Freeze(new SolidColorBrush(color)), Math.Max(0.5, width))
        {
            // 圆头让短条看起来是"圆角胶囊"而不是生硬的线段。
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };

        pen.Freeze();
        return pen;
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
