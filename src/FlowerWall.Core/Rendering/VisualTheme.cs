using System.Windows.Media;
using FlowerWall.Core;

namespace FlowerWall.Rendering;

/// <summary>
/// 从 <see cref="AppConfig"/> 中提取出来的、渲染层可以直接使用的颜色与几何参数。
///
/// 为什么要有这一层：渲染代码不应到处做字符串解析和范围校验。
/// 配置在装配阶段一次性转换成这里的强类型值，绘制代码只读它。
///
/// 画布尺寸：背景默认铺满整个桌面，因此画布 = 屏幕分辨率；
/// 胶片与声波环仍是固定像素尺寸、居中绘制 —— 因此屏幕越大，留白越多，视觉元素不会变形。
/// </summary>
public sealed class VisualTheme
{
    /// <summary>画布上限，避免极高分辨率下位图过大（4K 以上会被限制）。</summary>
    private const int MaxCanvasSize = 4096;

    public VisualTheme(
        ThemeSettings theme,
        RecordSettings record,
        SpectrumSettings spectrum,
        BackgroundSettings background,
        int screenWidth = 0,
        int screenHeight = 0)
    {
        Accent = ColorUtil.Parse(theme.AccentColor, Color.FromRgb(0xE4, 0x67, 0x9A));
        Highlight = ColorUtil.Parse(theme.HighlightColor, Color.FromRgb(0xFF, 0xF0, 0xF5));
        Disc = ColorUtil.Parse(theme.DiscColor, Color.FromRgb(0x06, 0x08, 0x0B));
        Groove = ColorUtil.Parse(theme.GrooveColor, Color.FromRgb(0x39, 0x43, 0x4F));

        RecordDiameter = Math.Clamp(record.Diameter, 120, 2000);
        LabelDiameter = Math.Clamp(record.LabelDiameter, 40, RecordDiameter / 2);
        SpindleDiameter = Math.Clamp(record.SpindleDiameter, 4, LabelDiameter / 3);
        GrooveCount = Math.Clamp(record.GrooveCount, 0, 400);
        GrooveIrregularity = Math.Clamp(record.GrooveIrregularity, 0f, 1f);
        GrooveOpacity = Math.Clamp(record.GrooveOpacity, 0f, 1f);
        GrooveOuterBoost = Math.Clamp(record.GrooveOuterBoost, 0.2f, 4f);
        SpecularStrength = Math.Clamp(record.SpecularStrength, 0f, 1f);
        WearStrength = Math.Clamp(record.WearStrength, 0f, 1f);
        RimHighlight = Math.Clamp(record.RimHighlight, 0f, 1f);
        CenterTextScale = Math.Clamp(record.CenterTextScale, 0.15f, 1.2f);

        Rpm = Math.Clamp(record.Rpm, 1f, 200f);
        ActiveSpeedFactor = Math.Clamp(record.ActiveSpeedFactor, 0.1f, 5f);
        SilentSpeedFactor = Math.Clamp(record.SilentSpeedFactor, 0f, 5f);
        SpeedSmoothing = Math.Clamp(record.SpeedSmoothing, 0.005f, 1f);
        SpinStopThreshold = Math.Clamp(record.SpinStopThreshold, 0f, 0.5f);

        BandCount = Math.Clamp(spectrum.BandCount, 8, 512);
        BarWidth = Math.Clamp(spectrum.BarWidth, 1f, 40f);
        GapFromRecord = Math.Clamp(spectrum.GapFromRecord, 0f, 200f);
        MaxBarLength = Math.Clamp(spectrum.MaxLength, 2f, 600f);
        MinBarLength = Math.Clamp(spectrum.MinLength, 0.5f, MaxBarLength);
        StartAngleDeg = spectrum.StartAngleDeg;
        GlowStrength = Math.Clamp(spectrum.GlowStrength, 0f, 1f);
        FlowSpeed = Math.Clamp(spectrum.FlowSpeed, -2f, 2f);
        SecondaryPhaseRadians = spectrum.SecondaryPhaseRadians;
        SecondaryAmplitude = Math.Clamp(spectrum.SecondaryAmplitude, 0f, 2f);
        SecondaryOpacity = Math.Clamp(spectrum.SecondaryOpacity, 0f, 1f);
        ReflectionAmplitude = Math.Clamp(spectrum.ReflectionAmplitude, 0f, 2f);
        ReflectionLength = Math.Clamp(spectrum.ReflectionLength, 0f, 300f);
        ReflectionOpacity = Math.Clamp(spectrum.ReflectionOpacity, 0f, 1f);

        BackgroundMode = background.Mode;
        BackgroundColor = ColorUtil.Parse(background.Color, Color.FromRgb(0xD9, 0xE8, 0xD2));
        BackgroundCenterColor = ColorUtil.Parse(background.CenterColor, Color.FromRgb(0xE3, 0xEE, 0xDD));
        BackgroundOpacity = Math.Clamp(background.Opacity, 0f, 1f);
        BackgroundImage = background.Image ?? string.Empty;
        BackgroundFitCover = !string.Equals(background.Fit, "Contain", StringComparison.OrdinalIgnoreCase);
        BackgroundGradientStrength = Math.Clamp(background.GradientStrength, 0f, 1f);

        // 画布 = 屏幕分辨率（背景要铺满整屏）；拿不到屏幕尺寸时退回可视化区域的包围盒。
        var visualizationExtent = (int)Math.Ceiling(
            ((RecordDiameter / 2f) + GapFromRecord + MaxBarLength + ReflectionLength) * 2f + 24f);

        var width = screenWidth > 0 ? screenWidth : visualizationExtent;
        var height = screenHeight > 0 ? screenHeight : visualizationExtent;

        CanvasWidth = Math.Clamp(width, 128, MaxCanvasSize);
        CanvasHeight = Math.Clamp(height, 128, MaxCanvasSize);
    }

    // --- 颜色 ---
    public Color Accent { get; }

    public Color Highlight { get; }

    public Color Disc { get; }

    public Color Groove { get; }

    // --- 几何 ---
    public int RecordDiameter { get; }

    public int LabelDiameter { get; }

    public int SpindleDiameter { get; }

    public int GrooveCount { get; }

    public float GrooveIrregularity { get; }

    public float GrooveOpacity { get; }

    public float GrooveOuterBoost { get; }

    public float SpecularStrength { get; }

    public float WearStrength { get; }

    public float RimHighlight { get; }

    public float CenterTextScale { get; }

    // --- 唱机运动 ---
    public float Rpm { get; }

    public float ActiveSpeedFactor { get; }

    public float SilentSpeedFactor { get; }

    public float SpeedSmoothing { get; }

    public float SpinStopThreshold { get; }

    // --- 声波环 ---
    public int BandCount { get; }

    /// <summary>短条宽度（像素）。</summary>
    public float BarWidth { get; }

    public float GapFromRecord { get; }

    public float MaxBarLength { get; }

    public float MinBarLength { get; }

    public float StartAngleDeg { get; }

    public float GlowStrength { get; }

    public float FlowSpeed { get; }

    public float SecondaryPhaseRadians { get; }

    public float SecondaryAmplitude { get; }

    public float SecondaryOpacity { get; }

    public float ReflectionAmplitude { get; }

    public float ReflectionLength { get; }

    public float ReflectionOpacity { get; }

    // --- 背景 ---
    public BackgroundMode BackgroundMode { get; }

    public Color BackgroundColor { get; }

    public Color BackgroundCenterColor { get; }

    public float BackgroundOpacity { get; }

    public string BackgroundImage { get; }

    public bool BackgroundFitCover { get; }

    public float BackgroundGradientStrength { get; }

    /// <summary>画布宽（= 屏幕宽）。</summary>
    public int CanvasWidth { get; }

    /// <summary>画布高（= 屏幕高）。</summary>
    public int CanvasHeight { get; }

    /// <summary>画布中心 X。胶片与声波环都以此为中心。</summary>
    public float CenterX => CanvasWidth / 2f;

    /// <summary>画布中心 Y。</summary>
    public float CenterY => CanvasHeight / 2f;

    /// <summary>胶片半径。</summary>
    public float RecordRadius => RecordDiameter / 2f;

    /// <summary>胶片标签半径。</summary>
    public float LabelRadius => LabelDiameter / 2f;

    /// <summary>声波环基线半径（胶片边缘外侧）。</summary>
    public float BarStartRadius => RecordRadius + GapFromRecord;

    /// <summary>声波环最大外缘半径。</summary>
    public float BarMaxRadius => BarStartRadius + MaxBarLength;
}
