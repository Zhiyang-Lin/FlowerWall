using System.Globalization;
using System.Windows.Media;

namespace FlowerWall.Rendering;

/// <summary>
/// 颜色工具。配置里用 #RRGGBB / #AARRGGBB 字符串表达颜色，
/// 这里负责解析成 <see cref="Color"/> 并按需要调整亮度、透明度。
/// </summary>
public static class ColorUtil
{
    /// <summary>
    /// 解析 #RGB / #RRGGBB / #AARRGGBB 形式的颜色。
    /// 解析失败时返回 <paramref name="fallback"/>，不抛异常 —— 配置写错不应导致程序起不来。
    /// </summary>
    public static Color Parse(string? text, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) { return fallback; }

        var value = text.Trim();
        if (value.StartsWith('#')) { value = value[1..]; }

        if (value.Length == 3)
        {
            // #RGB → #RRGGBB
            value = string.Concat(value[0], value[0], value[1], value[1], value[2], value[2]);
        }

        if (value.Length == 6)
        {
            value = "FF" + value;
        }

        if (value.Length != 8)
        {
            return fallback;
        }

        if (!uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
        {
            return fallback;
        }

        // #AARRGGBB：高位是 alpha。
        return Color.FromArgb(
            (byte)((packed >> 24) & 0xFF),
            (byte)((packed >> 16) & 0xFF),
            (byte)((packed >> 8) & 0xFF),
            (byte)(packed & 0xFF));
    }

    /// <summary>按比例缩放 RGB，保持 alpha 不变。大于 1 提亮，小于 1 压暗。</summary>
    public static Color Scale(Color color, float factor)
    {
        static byte Clamp(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);

        return Color.FromArgb(
            color.A,
            Clamp(color.R * factor),
            Clamp(color.G * factor),
            Clamp(color.B * factor));
    }

    /// <summary>替换 alpha 通道。</summary>
    public static Color WithAlpha(Color color, byte alpha)
        => Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>按比例缩放 alpha 通道。</summary>
    public static Color ScaleAlpha(Color color, float factor)
        => Color.FromArgb((byte)Math.Clamp((int)MathF.Round(color.A * factor), 0, 255), color.R, color.G, color.B);
}
