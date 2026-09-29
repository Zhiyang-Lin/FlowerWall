namespace FlowerWall.Interop;

/// <summary>
/// 桌面图标宿主的显示 / 隐藏控制。
///
/// 背景：桌面图标由 Explorer 的 SysListView32 承载，其窗口层级通常为
///   Progman
///     └─ SHELLDLL_DefView
///          └─ SysListView32   ← 真正的图标宿主
/// 在部分 Windows 版本上 SHELLDLL_DefView 会被挂到 WorkerW 下，因此需要兼容两种层级。
///
/// 实现选用 ShowWindow —— 这正是 Explorer 自己响应「查看 → 显示桌面图标」的机制。
/// 少数版本的 Explorer 使用 DWM 隐藏而非 ShowWindow，因此 <see cref="SetIconsVisible"/>
/// 会读回可见性做校验，不一致时自动降级为 DWMWA_CLOAK。
///
/// 重要契约：只要调用过隐藏，就必须保证恢复。见 <see cref="RestoreIfNeeded"/>。
/// </summary>
public sealed class DesktopIconHost
{
    private IntPtr _iconList;
    private bool _hiddenByUs;
    private bool _usedCloak;

    private DesktopIconHost(IntPtr iconList)
    {
        _iconList = iconList;
    }

    /// <summary>图标当前是否可见。找不到宿主时返回 true（视为无需处理）。</summary>
    public bool IconsVisible => _iconList == IntPtr.Zero || NativeMethods.IsWindowVisible(_iconList);

    /// <summary>
    /// 定位桌面图标宿主窗口。
    /// </summary>
    /// <returns>成功返回实例；找不到（例如桌面被第三方 shell 接管）返回 null。</returns>
    public static DesktopIconHost? TryCreate()
    {
        var host = FindIconList();
        return host == IntPtr.Zero ? null : new DesktopIconHost(host);
    }

    /// <summary>
    /// 隐藏或显示桌面图标。
    /// </summary>
    /// <param name="visible">true 显示，false 隐藏。</param>
    /// <returns>操作后状态是否符合预期。</returns>
    public bool SetIconsVisible(bool visible)
    {
        if (_iconList == IntPtr.Zero || !NativeMethods.IsWindow(_iconList))
        {
            // 宿主已失效（Explorer 重启过），尝试重新定位。
            _iconList = FindIconList();
            if (_iconList == IntPtr.Zero) { return false; }
        }

        // 已经处于目标状态：不要重复操作，否则会打乱其他程序的假设。
        if (NativeMethods.IsWindowVisible(_iconList) == visible)
        {
            if (visible) { _hiddenByUs = false; }
            return true;
        }

        if (_usedCloak)
        {
            SetCloaked(!visible);
        }
        else
        {
            NativeMethods.ShowWindow(_iconList, visible ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);
        }

        if (NativeMethods.IsWindowVisible(_iconList) == visible)
        {
            _hiddenByUs = !visible;
            return true;
        }

        // ShowWindow 无效：改用 DWM cloak 隐藏。
        if (!visible)
        {
            SetCloaked(true);
            _usedCloak = true;

            var cloaked = !NativeMethods.IsWindowVisible(_iconList);
            _hiddenByUs = cloaked;
            return cloaked;
        }

        return false;
    }

    /// <summary>
    /// 如果图标是被本程序隐藏的，则恢复显示。
    /// 必须在退出路径（含异常退出）上调用，避免用户桌面图标消失。
    /// </summary>
    public void RestoreIfNeeded()
    {
        if (!_hiddenByUs) { return; }

        SetIconsVisible(true);
        _usedCloak = false;
    }

    private void SetCloaked(bool cloaked)
    {
        if (_iconList == IntPtr.Zero) { return; }

        var value = cloaked ? 1 : 0;
        var hr = NativeMethods.DwmSetWindowAttribute(_iconList, NativeMethods.DWMWA_CLOAK, ref value, sizeof(int));

        if (hr < 0)
        {
            // 个别系统上需要作用于 DefView 父窗口。
            var parent = NativeMethods.FindWindowExW(IntPtr.Zero, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (parent != IntPtr.Zero)
            {
                NativeMethods.DwmSetWindowAttribute(parent, NativeMethods.DWMWA_CLOAK, ref value, sizeof(int));
            }
        }
    }

    /// <summary>
    /// 按 Progman → SHELLDLL_DefView → SysListView32 的层级查找图标宿主，
    /// 并兼容 DefView 被挂在 WorkerW 下的情况。
    /// </summary>
    private static IntPtr FindIconList()
    {
        var progman = NativeMethods.FindWindowW("Progman", null);
        var defView = IntPtr.Zero;

        if (progman != IntPtr.Zero)
        {
            defView = NativeMethods.FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        }

        if (defView == IntPtr.Zero)
        {
            // 枚举顶层窗口，寻找任意一个挂着 SHELLDLL_DefView 的 WorkerW。
            var found = IntPtr.Zero;

            NativeMethods.EnumWindows(
                (window, _) =>
                {
                    var candidate = NativeMethods.FindWindowExW(window, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (candidate == IntPtr.Zero) { return true; }

                    found = candidate;
                    return false;
                },
                IntPtr.Zero);

            defView = found;
        }

        if (defView == IntPtr.Zero) { return IntPtr.Zero; }

        var listView = NativeMethods.FindWindowExW(defView, IntPtr.Zero, "SysListView32", null);

        // 少数环境下图标直接由 DefView 承载。
        return listView != IntPtr.Zero ? listView : defView;
    }
}
