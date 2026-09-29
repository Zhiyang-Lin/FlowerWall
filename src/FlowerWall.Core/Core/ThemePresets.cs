namespace FlowerWall.Core;

/// <summary>
/// 一套配色的颜色值。字段名与配置文件里的名字一致，便于对照排查。
/// </summary>
public sealed record ThemePresetColors(
    string Accent,
    string Highlight,
    string Background,
    string BackgroundCenter,
    string Groove);

/// <summary>
/// 预置主题方案。
///
/// 为什么做预置而不是只留手改色号（两者其实并存）：
/// 单独调一个色号很难判断"整体搭不搭"，而配色是整体观感问题。
/// 预置方案是几套已经配平过的组合，一键切换即可；
/// 想微调其中某个颜色，仍然可以直接改 settings.json 里的 `theme.*`。
/// </summary>
public static class ThemePresets
{
    /// <summary>预设名称。空字符串表示"自定义"（用户手改过色号，与任何预设都不完全一致）。</summary>
    public const string CustomName = "Custom";

    private static readonly Dictionary<string, ThemePresetColors> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        // 默认方案：柔和抹茶绿 + 樱粉。绿色刻意压低黄味、提高一点饱和度。
        ["Matcha"] = new(
            Accent: "#E4679A",
            Highlight: "#FFF0F5",
            Background: "#A9C69A",
            BackgroundCenter: "#B5CFA6",
            Groove: "#39434F"),

        // 深墨绿 + 薄荷：最沉静，适合长时间盯着。
        ["Forest"] = new(
            Accent: "#5FD6A8",
            Highlight: "#EAFFF6",
            Background: "#20463A",
            BackgroundCenter: "#2A5445",
            Groove: "#4A6A5E"),

        // 近白 + 玫红：最干净，适合浅色桌面。
        ["Paper"] = new(
            Accent: "#D6336C",
            Highlight: "#FFE3EC",
            Background: "#F4F1EA",
            BackgroundCenter: "#FBFAF6",
            Groove: "#59606B"),

        // 暗紫 + 紫罗兰：夜间观感最安静。
        ["Midnight"] = new(
            Accent: "#A78BFA",
            Highlight: "#EFE9FF",
            Background: "#211C2E",
            BackgroundCenter: "#2B2439",
            Groove: "#544A6B"),

        // 樱粉底 + 亮粉：和最初那版接近，但现在底是粉、主色更亮。
        ["Sakura"] = new(
            Accent: "#E85D9B",
            Highlight: "#FFF2F7",
            Background: "#F2D3DE",
            BackgroundCenter: "#F8E2EA",
            Groove: "#5A4550"),
    };

    /// <summary>全部预置方案的名称（用于菜单）。</summary>
    public static IReadOnlyCollection<string> Names => Table.Keys;

    /// <summary>取某个预置方案的颜色。不存在时返回 null。</summary>
    public static ThemePresetColors? Find(string? name)
        => !string.IsNullOrEmpty(name) && Table.TryGetValue(name, out var colors) ? colors : null;

    /// <summary>
    /// 把预置方案应用到配置上。同时写入方案名，便于菜单显示当前选中项。
    /// </summary>
    /// <returns>方案是否存在并已应用。</returns>
    public static bool Apply(AppConfig config, string name)
    {
        if (!Table.TryGetValue(name, out var colors)) { return false; }

        config.Theme.AccentColor = colors.Accent;
        config.Theme.HighlightColor = colors.Highlight;
        config.Theme.GrooveColor = colors.Groove;

        // 有自定义背景图时不覆盖图片，只更新色号（图片优先级本来就更高）。
        config.Background.Color = colors.Background;
        config.Background.CenterColor = colors.BackgroundCenter;

        config.Theme.Preset = name;
        return true;
    }

    /// <summary>
    /// 判断当前配置是否与某个预置方案一致。
    /// 用户手改过任一色号就会全部不匹配，菜单因此显示为「自定义」。
    /// </summary>
    public static bool Matches(AppConfig config, string name)
    {
        if (!Table.TryGetValue(name, out var colors)) { return false; }

        return string.Equals(config.Theme.AccentColor, colors.Accent, StringComparison.OrdinalIgnoreCase)
               && string.Equals(config.Theme.HighlightColor, colors.Highlight, StringComparison.OrdinalIgnoreCase)
               && string.Equals(config.Background.Color, colors.Background, StringComparison.OrdinalIgnoreCase)
               && string.Equals(config.Background.CenterColor, colors.BackgroundCenter, StringComparison.OrdinalIgnoreCase)
               && string.Equals(config.Theme.GrooveColor, colors.Groove, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>当前配置命中的预设名；都不命中则返回 <see cref="CustomName"/>。</summary>
    public static string ResolveCurrent(AppConfig config)
        => Table.Keys.FirstOrDefault(name => Matches(config, name)) ?? CustomName;
}
