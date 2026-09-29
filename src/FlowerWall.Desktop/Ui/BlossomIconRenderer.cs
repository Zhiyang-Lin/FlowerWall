using System.Drawing;
using System.Drawing.Drawing2D;
using FlowerWall.Rendering;

namespace FlowerWall.Ui;

/// <summary>图标配色。避免各处硬编码颜色，换主题时只改一处。</summary>
internal readonly record struct IconPalette(Color Petal, Color Core, Color Outline)
{
    /// <summary>从主题色派生一套图标配色。</summary>
    public static IconPalette FromAccent(Color accent)
    {
        return new IconPalette(
            Petal: accent,
            Core: Darken(accent, 0.35f),
            Outline: Lighten(accent, 0.65f));
    }

    private static Color Lighten(Color color, float amount) => Color.FromArgb(
        color.A,
        (int)Math.Clamp(color.R + ((255 - color.R) * amount), 0, 255),
        (int)Math.Clamp(color.G + ((255 - color.G) * amount), 0, 255),
        (int)Math.Clamp(color.B + ((255 - color.B) * amount), 0, 255));

    private static Color Darken(Color color, float amount) => Color.FromArgb(
        color.A,
        (int)Math.Clamp(color.R * (1 - amount), 0, 255),
        (int)Math.Clamp(color.G * (1 - amount), 0, 255),
        (int)Math.Clamp(color.B * (1 - amount), 0, 255));
}

/// <summary>
/// 用 GDI+ 绘制梅花 / 樱花图标。
///
/// 形状算法与 WPF 侧共用 <see cref="BlossomArt"/>，因此托盘图标、悬浮按钮、盘面刻印
/// 是同一个花形，不会出现三种"花"。
///
/// 关于清晰度：所有尺寸都在目标尺寸上直接绘制（不是先画大图再缩放），
/// 且笔画宽度随尺寸换算，因此 16px 的托盘图标不会发虚 —— 位图缩放才会。
/// </summary>
internal static class BlossomIconRenderer
{
    /// <summary>
    /// 把花画在指定矩形的中心。
    /// </summary>
    /// <param name="graphics">目标画布。</param>
    /// <param name="bounds">图标的正方形区域。</param>
    /// <param name="palette">配色。</param>
    /// <param name="petals">花瓣数量。</param>
    /// <param name="rotationRadians">整体旋转角。</param>
    /// <param name="outlineWidth">描边宽度（像素）。0 表示不描边。</param>
    public static void Draw(
        Graphics graphics,
        RectangleF bounds,
        IconPalette palette,
        int petals = 5,
        double rotationRadians = -Math.PI / 2,
        float outlineWidth = 0f)
    {
        var centerX = bounds.X + (bounds.Width / 2f);
        var centerY = bounds.Y + (bounds.Height / 2f);

        // 留一小圈边距，避免描边被裁掉。
        var radius = (Math.Min(bounds.Width, bounds.Height) / 2f) * 0.88f;
        if (radius < 1.5f) { return; }

        var previousMode = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        try
        {
            var polygons = BlossomArt.CreatePetalPolygons(
                centerX, centerY, radius, petals, rotationRadians, stepsPerPetal: 22);

            using var petalBrush = new SolidBrush(palette.Petal);

            foreach (var flat in polygons)
            {
                var points = ToPoints(flat);
                graphics.FillPolygon(petalBrush, points);

                if (outlineWidth > 0f)
                {
                    using var pen = new Pen(palette.Outline, outlineWidth)
                    {
                        LineJoin = LineJoin.Round,
                    };
                    graphics.DrawPolygon(pen, points);
                }
            }

            // 花蕊：中心一个小圆，用更深的粉色，让花心不空。
            var coreRadius = radius * 0.22f;
            using var coreBrush = new SolidBrush(palette.Core);
            graphics.FillEllipse(
                coreBrush,
                centerX - coreRadius,
                centerY - coreRadius,
                coreRadius * 2,
                coreRadius * 2);
        }
        finally
        {
            graphics.SmoothingMode = previousMode;
        }
    }

    /// <summary>
    /// 生成一个 <see cref="Icon"/>。尺寸建议 32×32 —— Windows 会自己缩到托盘需要的 16×16，
    /// 从 32 缩小比直接画 16 更干净（保留了抗锯齿的层次）。
    /// </summary>
    /// <param name="size">位图边长。</param>
    /// <param name="palette">配色。</param>
    /// <param name="petals">花瓣数量。</param>
    public static Icon CreateIcon(int size, IconPalette palette, int petals = 5)
    {
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            Draw(graphics, new RectangleF(0, 0, size, size), palette, petals, -Math.PI / 2);
        }

        var handle = bitmap.GetHicon();

        try
        {
            // Icon.FromHandle 不接管句柄所有权，克隆后必须销毁原句柄，否则每次换主题都漏一个 GDI 对象。
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static PointF[] ToPoints(int[] flat)
    {
        var points = new PointF[flat.Length / 2];

        for (var i = 0; i < points.Length; i++)
        {
            points[i] = new PointF(flat[i * 2], flat[(i * 2) + 1]);
        }

        return points;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
