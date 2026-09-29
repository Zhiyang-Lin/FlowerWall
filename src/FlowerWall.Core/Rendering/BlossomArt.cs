using System.Windows;
using System.Windows.Media;

namespace FlowerWall.Rendering;

/// <summary>
/// 梅花 / 樱花图案的参数化绘制。
///
/// 为什么用代码画而不是放图片：图标要在 16px（托盘）、20px（悬浮按钮常态）、
/// 48px（按钮悬停）、以及盘面刻印（上百像素）四种尺度下都清晰。
/// 位图缩放必然在某一端发虚，而矢量几何在每个尺度都锐利；
/// 而且颜色能跟着主题走，换配色不用重做素材。
///
/// 花瓣形状：以花瓣轴为基准的泪滴形（尖端朝圆心、外缘圆润）。
/// 参数是「过冲量 + 半宽比」，都能直观调形，比硬编码一串坐标更好维护。
/// </summary>
public static class BlossomArt
{
    /// <summary>
    /// 花瓣外缘的圆润程度。1.0 表示花瓣尖端正好落在给定半径上。
    /// </summary>
    private const double PetalBulge = 1.0;

    /// <summary>
    /// 花瓣起始半径占花瓣长度的比例。
    ///
    /// 这是让花瓣"分得开"的关键：如果每片花瓣都从圆心出发，它们必然在中心连成一片实心色块，
    /// 看起来像多边形而不是花。让花瓣从内半径起步，就能留出真实的间隙与花心。
    /// 取值需略小于花蕊半径，两者才会平滑衔接。
    /// </summary>
    private const double PetalStartRatio = 0.28;

    /// <summary>
    /// 花瓣半宽相对（外半径 − 内半径）的比例。
    /// 上限由花瓣数量决定：相邻花瓣角间距为 2π/n，半宽超过该角度对应的弦长就会重叠。
    /// 0.42 在 5 瓣与 8 瓣下都不会互相挤压。
    /// </summary>
    private const double PetalWidthRatio = 0.42;

    /// <summary>花瓣横向轮廓的形状指数。1.0 是标准正弦轮廓。</summary>
    private const double PetalSpreadPower = 1.0;

    /// <summary>花蕊（中心圆点）相对花瓣长度的比例。</summary>
    private const double CenterRatio = 0.3;

    /// <summary>
    /// 生成一片花瓣的轮廓点（已按给定方向旋转）。
    /// </summary>
    /// <param name="centerX">花心 X。</param>
    /// <param name="centerY">花心 Y。</param>
    /// <param name="length">花瓣长度（像素）。</param>
    /// <param name="angleRadians">花瓣指向（弧度）。</param>
    /// <param name="steps">轮廓采样点数。</param>
    private static Point[] PetalOutline(double centerX, double centerY, double length, double angleRadians, int steps)
    {
        var points = new Point[steps];
        var inner = length * PetalStartRatio;
        var band = length - inner;
        var halfWidth = band * PetalWidthRatio;
        var cos = Math.Cos(angleRadians);
        var sin = Math.Sin(angleRadians);

        for (var i = 0; i < steps; i++)
        {
            // 参数：前半段 0→1 画花瓣的一侧，后半段 1→0 沿另一侧回到起点，于是轮廓闭合。
            // side 的符号是关键 —— 两侧必须朝相反方向偏移，
            // 否则两半都偏到同一侧，花瓣会弯成月牙（远看像风车叶子而不是花瓣）。
            var half = steps / 2;
            var forward = i < half;
            var t = forward
                ? (double)i / half
                : 2.0 - ((double)i / half);
            var side = forward ? 1.0 : -1.0;

            // 轴向：从内半径起步到外半径，花瓣因此不会挤在圆心连成一片。
            var axis = inner + (band * PetalBulge * t);

            // 横向：sin(πt) 两端收拢到 0、中段最宽，形成圆润的花瓣边缘。
            var spread = side * halfWidth * Math.Pow(Math.Sin(Math.PI * t), PetalSpreadPower);

            points[i] = new Point(
                centerX + (axis * cos) - (spread * sin),
                centerY + (axis * sin) + (spread * cos));
        }

        return points;
    }

    /// <summary>
    /// 生成整朵花的几何：<paramref name="petals"/> 片花瓣 + 中心花蕊。
    /// </summary>
    /// <param name="centerX">花心 X。</param>
    /// <param name="centerY">花心 Y。</param>
    /// <param name="radius">花的总半径（到花瓣最外缘）。</param>
    /// <param name="petals">花瓣数量。</param>
    /// <param name="rotationRadians">整体旋转角。</param>
    /// <param name="stepsPerPetal">每片花瓣的轮廓采样点数，越大越圆滑。</param>
    public static (Geometry Petals, Geometry Core) CreateGeometry(
        double centerX,
        double centerY,
        double radius,
        int petals,
        double rotationRadians,
        int stepsPerPetal = 24)
    {
        petals = Math.Clamp(petals, 3, 12);

        // 花瓣长度要按过冲量折算，否则实际半径会比请求的大。
        var petalLength = radius / PetalBulge;
        // 每片花瓣都从花心出发，因此五片在圆心必然互相重叠。
        // 默认的 EvenOdd 填充规则会把重叠区挖成洞（花瓣看起来是镂空的），
        // 必须用 Nonzero —— 重叠区仍然算作"内部"。
        var petalGeometry = new StreamGeometry { FillRule = FillRule.Nonzero };

        using (var context = petalGeometry.Open())
        {
            for (var petal = 0; petal < petals; petal++)
            {
                var angle = rotationRadians + (Math.PI * 2 * petal / petals);
                var outline = PetalOutline(centerX, centerY, petalLength, angle, stepsPerPetal);

                context.BeginFigure(outline[0], isFilled: true, isClosed: true);
                context.PolyLineTo(outline[1..], isStroked: true, isSmoothJoin: true);
            }
        }

        petalGeometry.Freeze();

        var coreRadius = petalLength * CenterRatio;
        var coreGeometry = new EllipseGeometry(new Point(centerX, centerY), coreRadius, coreRadius);
        coreGeometry.Freeze();

        return (petalGeometry, coreGeometry);
    }

    /// <summary>
    /// 生成单朵花瓣合并成一个 Geometry（用于只需要花瓣轮廓的场景，例如描边）。
    /// </summary>
    public static Geometry CreatePetalsOnly(
        double centerX,
        double centerY,
        double radius,
        int petals,
        double rotationRadians,
        int stepsPerPetal = 24)
        => CreateGeometry(centerX, centerY, radius, petals, rotationRadians, stepsPerPetal).Petals;

    /// <summary>
    /// 花瓣轮廓的通用数据接口，供 WinForms / GDI+ 侧（托盘图标、悬浮按钮）复用同一套形状。
    ///
    /// 返回「每片花瓣一个点数组」，坐标是整数数组：偶数下标为 X，奇数下标为 Y。
    /// 之所以不直接返回 <see cref="Point"/> 数组，是为了让 GDI+ 侧不必依赖 WPF 类型。
    /// </summary>
    /// <param name="centerX">花心 X。</param>
    /// <param name="centerY">花心 Y。</param>
    /// <param name="radius">花的总半径。</param>
    /// <param name="petals">花瓣数量。</param>
    /// <param name="rotationRadians">整体旋转角。</param>
    /// <param name="stepsPerPetal">每片花瓣的采样点数。</param>
    /// <param name="ringScale">花瓣长度相对总半径的缩放，用于画更小的内层花瓣。</param>
    public static int[][] CreatePetalPolygons(
        float centerX,
        float centerY,
        float radius,
        int petals,
        double rotationRadians,
        int stepsPerPetal = 20,
        float ringScale = 1f)
    {
        petals = Math.Clamp(petals, 3, 12);
        var petalLength = radius * ringScale / PetalBulge;
        var result = new int[petals][];

        for (var petal = 0; petal < petals; petal++)
        {
            var angle = rotationRadians + (Math.PI * 2 * petal / petals);
            var outline = PetalOutline(centerX, centerY, petalLength, angle, stepsPerPetal);
            var flat = new int[outline.Length * 2];

            for (var i = 0; i < outline.Length; i++)
            {
                flat[(i * 2) + 0] = (int)Math.Round(outline[i].X);
                flat[(i * 2) + 1] = (int)Math.Round(outline[i].Y);
            }

            result[petal] = flat;
        }

        return result;
    }
}
