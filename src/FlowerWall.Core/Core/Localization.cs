namespace FlowerWall.Core;

/// <summary>需要本地化的界面文案。新增文案时在这里加一项，并在 <see cref="Localization"/> 里补两种语言。</summary>
public enum TextKey
{
    AppName,
    ToggleShow,
    ToggleHide,
    ReloadConfig,
    OpenConfig,
    ImportBackground,
    ImportBackgroundTitle,
    ImportBackgroundFilter,
    BackgroundImported,
    BackgroundImportFailed,
    ResetBackground,
    BackgroundReset,
    ColorScheme,
    ColorSchemeCustom,
    ColorSchemeChanged,
    Language,
    LanguageAuto,
    LanguageChinese,
    LanguageEnglish,
    LanguageChanged,
    Exit,
    StatusVisibleManual,
    StatusVisibleIdle,
    StatusHidden,
    StatusLine,
    SingleInstanceMessage,
    SingleInstanceTitle,
    FatalErrorTitle,
    ConfigLoadFailed,
    ConfigSaved,
}

/// <summary>
/// 极轻量的双语支持。
///
/// 为什么不用 .NET 的 resx / ResourceManager：整个程序只有二十几条界面文案，
/// 引入资源文件会带来「改文案要开设计器、多出附属程序集」的负担。
/// 这里用一张只读字典表，编译期就能看出缺漏，运行时切换也不需要重启。
///
/// 应用名也走这张表：中文界面显示「花墙」，英文界面显示「FlowerWall」，
/// 这样名字不会和文案各改各的。
/// </summary>
public static class Localization
{
    /// <summary>当前生效的语言（<see cref="AppLanguage.Auto"/> 会被解析成具体语言）。</summary>
    public static AppLanguage Current { get; private set; } = AppLanguage.Auto;

    /// <summary>当前是否使用中文。</summary>
    public static bool IsChinese => Resolve(Current) == AppLanguage.Chinese;

    /// <summary>按当前语言取文案。</summary>
    public static string Text(TextKey key)
        => Table(IsChinese ? AppLanguage.Chinese : AppLanguage.English).TryGetValue(key, out var value)
            ? value
            : key.ToString();

    /// <summary>取带占位符的文案，例如「已隐藏（空闲 {0} 秒）」。</summary>
    public static string Format(TextKey key, params object[] arguments)
        => string.Format(Text(key), arguments);

    /// <summary>
    /// 指定语言是否收录了某条文案。
    ///
    /// 供诊断工具做漏翻译检查：<see cref="Text"/> 在缺失时会退回枚举名，
    /// 而个别词条的译文恰好与枚举名相同（例如 <see cref="TextKey.Exit"/> 的英文就是 "Exit"），
    /// 仅凭返回值无法区分这两种情况，必须能单独查"表里有没有"。
    /// </summary>
    public static bool Has(TextKey key, AppLanguage language)
        => Table(Resolve(language)).ContainsKey(key);

    /// <summary>当前语言的显示名，用于菜单勾选。</summary>
    public static string LanguageDisplayName(AppLanguage language) => language switch
    {
        AppLanguage.Chinese => Text(TextKey.LanguageChinese),
        AppLanguage.English => Text(TextKey.LanguageEnglish),
        _ => IsChinese ? "跟随系统" : "System",
    };

    /// <summary>应用界面语言。可在运行时调用，立即生效。</summary>
    public static void Use(AppLanguage language)
    {
        Current = language;
        Resolve(language);
    }

    /// <summary>把 Auto 解析成具体语言并缓存。中文系统 → 中文，其余 → 英文。</summary>
    private static AppLanguage Resolve(AppLanguage language)
    {
        if (language != AppLanguage.Auto) { return language; }

        var culture = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return culture.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.Chinese
            : AppLanguage.English;
    }

    private static IReadOnlyDictionary<TextKey, string> Table(AppLanguage language)
        => language == AppLanguage.Chinese ? Chinese : English;

    private static readonly Dictionary<TextKey, string> Chinese = new()
    {
        [TextKey.AppName] = "花墙",
        [TextKey.ToggleShow] = "显示壁纸",
        [TextKey.ToggleHide] = "隐藏壁纸",
        [TextKey.ReloadConfig] = "重新加载配置",
        [TextKey.OpenConfig] = "打开配置文件位置",
        [TextKey.ImportBackground] = "导入背景图…",
        [TextKey.ImportBackgroundTitle] = "选择一张背景图",
        [TextKey.ImportBackgroundFilter] = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件|*.*",
        [TextKey.BackgroundImported] = "背景图已导入并生效",
        [TextKey.BackgroundImportFailed] = "背景图导入失败",
        [TextKey.ResetBackground] = "恢复默认背景",
        [TextKey.BackgroundReset] = "已恢复默认背景",
        [TextKey.ColorScheme] = "配色方案",
        [TextKey.ColorSchemeCustom] = "自定义（手改色号）",
        [TextKey.ColorSchemeChanged] = "配色已切换",
        [TextKey.Language] = "语言 / Language",
        [TextKey.LanguageAuto] = "跟随系统",
        [TextKey.LanguageChinese] = "中文",
        [TextKey.LanguageEnglish] = "English",
        [TextKey.LanguageChanged] = "界面语言已切换",
        [TextKey.Exit] = "退出",
        [TextKey.StatusVisibleManual] = "已显示（手动）",
        [TextKey.StatusVisibleIdle] = "已显示（空闲触发）",
        [TextKey.StatusHidden] = "已隐藏",
        [TextKey.StatusLine] = "空闲 {0:0} 秒　帧率 {1:0} FPS",
        [TextKey.SingleInstanceMessage] = "花墙已经在运行了。\n请在悬浮按钮或托盘图标上右键退出后再启动新的实例。",
        [TextKey.SingleInstanceTitle] = "花墙",
        [TextKey.FatalErrorTitle] = "花墙遇到错误",
        [TextKey.ConfigLoadFailed] = "配置文件解析失败，已使用默认值。详情见 logs 目录。",
        [TextKey.ConfigSaved] = "配置已保存",
    };

    private static readonly Dictionary<TextKey, string> English = new()
    {
        [TextKey.AppName] = "FlowerWall",
        [TextKey.ToggleShow] = "Show wallpaper",
        [TextKey.ToggleHide] = "Hide wallpaper",
        [TextKey.ReloadConfig] = "Reload config",
        [TextKey.OpenConfig] = "Open config folder",
        [TextKey.ImportBackground] = "Import background…",
        [TextKey.ImportBackgroundTitle] = "Choose a background image",
        [TextKey.ImportBackgroundFilter] = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*",
        [TextKey.BackgroundImported] = "Background image imported and applied",
        [TextKey.BackgroundImportFailed] = "Failed to import background image",
        [TextKey.ResetBackground] = "Reset to default background",
        [TextKey.BackgroundReset] = "Default background restored",
        [TextKey.ColorScheme] = "Color scheme",
        [TextKey.ColorSchemeCustom] = "Custom (edited colors)",
        [TextKey.ColorSchemeChanged] = "Color scheme changed",
        [TextKey.Language] = "Language / 语言",
        [TextKey.LanguageAuto] = "Follow system",
        [TextKey.LanguageChinese] = "中文",
        [TextKey.LanguageEnglish] = "English",
        [TextKey.LanguageChanged] = "Interface language changed",
        [TextKey.Exit] = "Exit",
        [TextKey.StatusVisibleManual] = "visible (manual)",
        [TextKey.StatusVisibleIdle] = "visible (idle)",
        [TextKey.StatusHidden] = "hidden",
        [TextKey.StatusLine] = "idle {0:0}s   {1:0} FPS",
        [TextKey.SingleInstanceMessage] = "FlowerWall is already running.\nRight-click the floating button or tray icon and choose Exit first.",
        [TextKey.SingleInstanceTitle] = "FlowerWall",
        [TextKey.FatalErrorTitle] = "FlowerWall encountered an error",
        [TextKey.ConfigLoadFailed] = "Config could not be parsed; defaults are in use. See the logs folder.",
        [TextKey.ConfigSaved] = "Config saved",
    };
}
