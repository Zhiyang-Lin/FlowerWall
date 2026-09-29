using System.Drawing;
using System.Windows.Forms;
using FlowerWall.Core;

namespace FlowerWall.Ui;

/// <summary>托盘与悬浮按钮共用的动作集合。</summary>
internal sealed record MenuActions(
    Func<bool> Toggle,
    Action Reload,
    Action<AppLanguage> SetLanguage,
    Action<string> SetColorScheme,
    Func<bool> ImportBackground,
    Func<bool> ResetBackground,
    Action OpenConfig,
    Action Exit);

/// <summary>菜单打开时用于刷新状态文字的取值函数。</summary>
internal sealed record StatusSnapshot(bool Active, bool Manual, double IdleSeconds, float Fps);

/// <summary>
/// 托盘图标与菜单。
///
/// 托盘菜单与悬浮按钮右键菜单共用同一份 <see cref="ContextMenuStrip"/> —— 菜单内容在每次弹出时
/// 动态生成，因此语言、勾选状态、状态文字永远是最新的，不需要在别处手动同步文本。
/// </summary>
internal sealed class TrayMenu : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();

    private MenuActions? _actions;
    private Func<StatusSnapshot>? _statusProvider;
    private Func<AppLanguage>? _languageProvider;
    private Func<string>? _colorSchemeProvider;

    private Icon? _ownedIcon;

    public TrayMenu(IconPalette palette, int petals)
    {
        _menu.ShowImageMargin = false;
        _menu.Opening += (_, _) => BuildItems();

        _ownedIcon = BlossomIconRenderer.CreateIcon(32, palette, petals);

        _icon = new NotifyIcon
        {
            Icon = _ownedIcon,
            Text = Localization.Text(TextKey.AppName),
            Visible = true,
            ContextMenuStrip = _menu,
        };

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                _actions?.Toggle();
            }
        };
    }

    /// <summary>共用菜单。悬浮按钮直接引用它，保证两处入口的菜单完全一致。</summary>
    public ContextMenuStrip Menu => _menu;

    /// <summary>绑定动作与状态来源。必须在显示任何菜单之前调用。</summary>
    public void Bind(
        MenuActions actions,
        Func<StatusSnapshot> statusProvider,
        Func<AppLanguage> languageProvider,
        Func<string> colorSchemeProvider)
    {
        _actions = actions;
        _statusProvider = statusProvider;
        _languageProvider = languageProvider;
        _colorSchemeProvider = colorSchemeProvider;
    }

    /// <summary>
    /// 更新强调色与花瓣数量。配置热重载或切换主题后调用。
    /// 会重建托盘图标 —— 图标是按配色现画的，不重建就看不到新颜色。
    /// </summary>
    public void ApplyAppearance(IconPalette palette, int petals)
    {
        var replacement = BlossomIconRenderer.CreateIcon(32, palette, petals);
        var previous = _ownedIcon;

        _icon.Icon = replacement;
        _ownedIcon = replacement;

        previous?.Dispose();
    }

    /// <summary>
    /// 弹一个气泡提示。用于确认「导入背景图」「切换语言」这类操作真的生效了。
    /// 托盘不可用（例如 Explorer 正在重启）时静默忽略，不影响主功能。
    /// </summary>
    public void Notify(string message)
    {
        try
        {
            _icon.BalloonTipTitle = Localization.Text(TextKey.AppName);
            _icon.BalloonTipText = message;
            _icon.BalloonTipIcon = ToolTipIcon.None;
            _icon.ShowBalloonTip(2500);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // 忽略：提示失败不应影响主流程。
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _ownedIcon?.Dispose();
        _ownedIcon = null;
    }

    /// <summary>每次弹出前重建菜单项，使语言、状态与勾选保持最新。</summary>
    private void BuildItems()
    {
        var actions = _actions;
        if (actions is null) { return; }

        _menu.Items.Clear();

        var status = _statusProvider?.Invoke();
        if (status is not null)
        {
            var state = status.Active
                ? (status.Manual
                    ? Localization.Text(TextKey.StatusVisibleManual)
                    : Localization.Text(TextKey.StatusVisibleIdle))
                : Localization.Text(TextKey.StatusHidden);

            _menu.Items.Add(new ToolStripMenuItem($"{Localization.Text(TextKey.AppName)} · {state}") { Enabled = false });
            _menu.Items.Add(new ToolStripMenuItem(
                Localization.Format(TextKey.StatusLine, status.IdleSeconds, status.Fps)) { Enabled = false });
            _menu.Items.Add(new ToolStripSeparator());
        }

        var toggleItem = new ToolStripMenuItem(
            status?.Active == true ? Localization.Text(TextKey.ToggleHide) : Localization.Text(TextKey.ToggleShow));
        toggleItem.Click += (_, _) => actions.Toggle();

        var backgroundItem = new ToolStripMenuItem(Localization.Text(TextKey.ImportBackground));
        backgroundItem.Click += (_, _) =>
        {
            if (actions.ImportBackground())
            {
                Notify(Localization.Text(TextKey.BackgroundImported));
            }
        };

        // 恢复默认背景：修掉"导入图片后回不去纯色"的缺口。
        var resetBackgroundItem = new ToolStripMenuItem(Localization.Text(TextKey.ResetBackground));
        resetBackgroundItem.Click += (_, _) =>
        {
            if (actions.ResetBackground())
            {
                Notify(Localization.Text(TextKey.BackgroundReset));
            }
        };

        var reloadItem = new ToolStripMenuItem(Localization.Text(TextKey.ReloadConfig));
        reloadItem.Click += (_, _) => actions.Reload();

        var openConfigItem = new ToolStripMenuItem(Localization.Text(TextKey.OpenConfig));
        openConfigItem.Click += (_, _) => actions.OpenConfig();

        _menu.Items.Add(toggleItem);
        _menu.Items.Add(backgroundItem);
        _menu.Items.Add(resetBackgroundItem);
        _menu.Items.Add(BuildContextMenu(actions));
        _menu.Items.Add(BuildLanguageMenu(actions));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(reloadItem);
        _menu.Items.Add(openConfigItem);
        _menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem(Localization.Text(TextKey.Exit));
        exitItem.Click += (_, _) => actions.Exit();
        _menu.Items.Add(exitItem);
    }

    /// <summary>配色方案子菜单：预置几套 + 「自定义」提示，当前项打勾。</summary>
    private ToolStripMenuItem BuildContextMenu(MenuActions actions)
    {
        var current = _colorSchemeProvider?.Invoke() ?? ThemePresets.CustomName;
        var root = new ToolStripMenuItem(Localization.Text(TextKey.ColorScheme));

        foreach (var name in ThemePresets.Names)
        {
            var item = new ToolStripMenuItem(name) { Checked = string.Equals(current, name, StringComparison.OrdinalIgnoreCase) };
            var captured = name;
            item.Click += (_, _) => actions.SetColorScheme(captured);
            root.DropDownItems.Add(item);
        }

        // 用户手改过色号时给出明确提示，避免"明明选了方案却不一样"的困惑。
        if (string.Equals(current, ThemePresets.CustomName, StringComparison.OrdinalIgnoreCase))
        {
            root.DropDownItems.Add(new ToolStripSeparator());
            root.DropDownItems.Add(
                new ToolStripMenuItem(Localization.Text(TextKey.ColorSchemeCustom)) { Enabled = false });
        }

        return root;
    }

    /// <summary>语言子菜单：跟随系统 / 中文 / English，当前项打勾。</summary>
    private ToolStripMenuItem BuildLanguageMenu(MenuActions actions)
    {
        var current = _languageProvider?.Invoke() ?? AppLanguage.Auto;
        var root = new ToolStripMenuItem(Localization.Text(TextKey.Language));

        var options = new[]
        {
            (Language: AppLanguage.Auto, Label: Localization.Text(TextKey.LanguageAuto)),
            (Language: AppLanguage.Chinese, Label: Localization.Text(TextKey.LanguageChinese)),
            (Language: AppLanguage.English, Label: Localization.Text(TextKey.LanguageEnglish)),
        };

        foreach (var (language, label) in options)
        {
            var item = new ToolStripMenuItem(label) { Checked = current == language };
            var captured = language;
            item.Click += (_, _) => actions.SetLanguage(captured);
            root.DropDownItems.Add(item);
        }

        return root;
    }
}
