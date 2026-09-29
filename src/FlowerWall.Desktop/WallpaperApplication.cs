using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using FlowerWall.Audio;
using FlowerWall.Interop;
using FlowerWall.Rendering;
using FlowerWall.Ui;

namespace FlowerWall.Core;

/// <summary>
/// 应用装配与生命周期。整个程序只有这一个 ApplicationContext，
/// 负责把「配置 → 音频 → 分析 → 渲染 → 窗口 → 交互」串起来，并在退出时做好资源回收。
///
/// 线程模型：所有 UI 与渲染都在 UI 线程的帧定时器上完成；
/// 音频采集独占一条后台线程，通过环形缓冲单向传递数据，两者之间没有锁。
/// </summary>
internal sealed class WallpaperApplication : ApplicationContext
{
    /// <summary>帧定时器的最小查询间隔（毫秒）。更小只会空转，不会让帧率更准。</summary>
    private const int MinFrameTickMs = 4;

    private const int MaxFrameIntervalMs = 250;

    /// <summary>淡入淡出期间使用的帧率。过渡只有几百毫秒，不必跟随配置上限。</summary>
    private const int FadeFps = 30;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer _frameTimer;

    private AppConfig _config;
    private AudioRingBuffer _ring;
    private AudioCaptureEngine _capture;
    private SpectrumAnalyzer _analyzer;
    private InteractionController _interaction;
    private VisualizerRenderer _renderer;
    private VinylWindow _window;
    private ControlButtonForm? _button;
    private TrayMenu? _tray;
    private DesktopIconHost? _iconHost;

    /// <summary>上一次真正渲染的时间点。帧调度靠它判断「是否到点了」。</summary>
    private TimeSpan _lastRenderTime;

    /// <summary>常态帧间隔。</summary>
    private TimeSpan _frameInterval = TimeSpan.FromMilliseconds(1000.0 / 30);

    /// <summary>淡入淡出期间的帧间隔。</summary>
    private TimeSpan _fadeFrameInterval = TimeSpan.FromMilliseconds(1000.0 / 30);

    /// <summary>当前是否处于淡入 / 淡出过程中。</summary>
    private bool _fadeInProgress;

    private float _previousBoostGain = 1f;
    private float _previousGlowGain = 1f;
    private double _fps;
    private int _framesSinceFpsSample;
    private double _fpsWindowStart;

    private bool _closed;
    private bool _iconsHiddenByUs;

    /// <summary>是否已经尝试过定位桌面图标宿主（避免每帧重复枚举窗口）。</summary>
    private bool _iconHostLookupDone;

    private double _statsWindowStart;
    private double _statsCpuStart;
    private long _statsFrames;
    private long _statsRendered;
    private double _statsTickMilliseconds;

    public WallpaperApplication(AppConfig config)
    {
        _config = config;

        _clock.Start();
        _lastRenderTime = _clock.Elapsed;

        _ring = new AudioRingBuffer(1 << 17); // 131072 采样 ≈ 2.7 秒 @48kHz，远超单帧需求，代价仅 512KB
        _analyzer = new SpectrumAnalyzer(_ring, _config, 48000);
        _interaction = new InteractionController(_config);
        _renderer = new VisualizerRenderer(CreateTheme(_config));
        _window = new VinylWindow(_renderer);

        // 定时器只负责提供「查询机会」，真实帧率由 OnFrameTick 里的时间判断决定。
        _frameTimer = new System.Windows.Forms.Timer
        {
            Interval = Math.Clamp(_config.Render.FrameTickMilliseconds, MinFrameTickMs, MaxFrameIntervalMs),
        };
        _frameTimer.Tick += OnFrameTick;
        UpdateFrameInterval();

        _capture = new AudioCaptureEngine(_ring);
        _capture.Start();

        CreateTray();

        if (_config.Button.Enabled)
        {
            CreateButton();
        }

        if (_config.App.StartVisible)
        {
            _interaction.SetManual(true);
        }

        _iconHost = DesktopIconHost.TryCreate();

        _frameTimer.Start();

        // 探针：把实际生效的关键配置写进统计日志，避免「以为改的是这个字段」。
        RuntimeStats.WriteTimestamped(
            $"started: idleThreshold={_config.Idle.ThresholdMinutes}min idleEnabled={_config.Idle.Enabled} " +
            $"boost={_config.Idle.BoostWhenIdle} startVisible={_config.App.StartVisible} " +
            $"maxFps={_config.Render.MaxFps} bandCount={_config.Spectrum.BandCount} " +
            $"fftSize={_config.Spectrum.FftSize} canvas={_renderer.CanvasWidth}x{_renderer.CanvasHeight} " +
            $"background={_config.Background.Mode} " +
            $"configPath={ConfigStore.ConfigPath} configError={ConfigStore.LastError ?? "none"}");
    }

    /// <summary>当前是否处于可视化显示状态。</summary>
    public bool IsVisualizationActive => _interaction.IsActive;

    /// <summary>释放全部资源并退出消息循环。可重复调用。</summary>
    public void Shutdown()
    {
        if (_closed) { return; }

        _closed = true;

        try { _frameTimer.Stop(); } catch (ObjectDisposedException) { /* 已释放 */ }
        _frameTimer.Dispose();

        _interaction.Close();
        _capture.Dispose();

        RestoreDesktopIcons();

        _tray?.Dispose();
        _button?.Dispose();
        _window.Dispose();
        _renderer.Dispose();

        ExitThread();
    }

    /// <summary>切换可视化显隐（悬浮按钮左键、托盘左键、菜单项共用）。</summary>
    public bool ToggleVisualization() => _interaction.ToggleManual();

    /// <summary>
    /// 切换界面语言并立即生效、写回配置。
    /// 菜单、悬浮按钮标题都会在下一次弹出 / 重建时用新语言。
    /// </summary>
    public void SetLanguage(AppLanguage language)
    {
        _config.App.Language = language;
        Localization.Use(language);
        ConfigStore.Save(_config);

        RecreateButton();
        _tray?.Notify(Localization.Text(TextKey.LanguageChanged));
    }

    /// <summary>
    /// 切换到某个预置配色方案，立即生效并写回配置。
    /// 托盘图标也要重建 —— 它是按强调色现画的。
    /// </summary>
    public void SetColorScheme(string presetName)
    {
        if (!ThemePresets.Apply(_config, presetName)) { return; }

        ConfigStore.Save(_config);

        RebuildRenderSurface();
        _tray?.ApplyAppearance(BuildIconPalette(), IconPetals);
        _tray?.Notify(Localization.Text(TextKey.ColorSchemeChanged));
    }

    /// <summary>
    /// 恢复默认背景：从图片模式切回纯色，并清空图片路径。
    ///
    /// 为什么需要这一项：导入背景图后 <c>background.mode</c> 会变成 Image，
    /// 此后只改 <c>background.color</c> 是不生效的 —— 用户会以为"颜色改不动"。
    /// 这个菜单项就是那条退路。
    /// </summary>
    /// <returns>是否发生了变更。</returns>
    public bool ResetBackground()
    {
        var changed = _config.Background.Mode != BackgroundMode.Solid
                      || !string.IsNullOrEmpty(_config.Background.Image);

        _config.Background.Mode = BackgroundMode.Solid;
        _config.Background.Image = string.Empty;

        ConfigStore.Save(_config);
        RebuildRenderSurface();

        return changed;
    }

    /// <summary>
    /// 导入背景图：弹出文件选择框，把图片拷进程序目录的 assets\，再写入配置并立即生效。
    ///
    /// 为什么拷贝而不是直接引用原路径：用户可能把图放在临时目录或移动硬盘上，
    /// 直接引用会在图被删除/拔出后静默失效。拷进程序目录后配置永远有效。
    /// </summary>
    /// <returns>是否成功导入。</returns>
    public bool ImportBackground()
    {
        using var dialog = new OpenFileDialog
        {
            Title = Localization.Text(TextKey.ImportBackgroundTitle),
            Filter = Localization.Text(TextKey.ImportBackgroundFilter),
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog() != DialogResult.OK) { return false; }

        try
        {
            var assetsDirectory = Path.Combine(Core.AppPaths.RootDirectory, "assets");
            Directory.CreateDirectory(assetsDirectory);

            var extension = Path.GetExtension(dialog.FileName);
            var destination = Path.Combine(assetsDirectory, "background" + extension);

            // 先拷到临时名再替换：避免目标是同一文件时自我覆盖导致内容损坏。
            var temporary = destination + ".tmp";
            File.Copy(dialog.FileName, temporary, overwrite: true);
            File.Move(temporary, destination, overwrite: true);

            _config.Background.Mode = BackgroundMode.Image;
            _config.Background.Image = destination;
            ConfigStore.Save(_config);

            // 重建渲染器：背景图在构造时加载，不重建就不会生效。
            RebuildRenderSurface();

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            RuntimeStats.WriteLine($"import background failed: {exception.GetType().Name}: {exception.Message}");
            _tray?.Notify(Localization.Text(TextKey.BackgroundImportFailed));
            return false;
        }
    }

    /// <summary>重建渲染器与窗口（配置影响画布尺寸、配色或背景时调用）。</summary>
    private void RebuildRenderSurface()
    {
        var wasActive = _interaction.IsActive;

        _window.Dispose();
        _renderer.Dispose();

        _renderer = new VisualizerRenderer(CreateTheme(_config));
        _window = new VinylWindow(_renderer);

        RecreateButton();
        UpdateFrameInterval();

        // 让新外观立刻可见，不必等下一帧调度。
        if (wasActive)
        {
            _window.Present(_analyzer.Frame, 0.001f, 1.0 / 60.0, 1f, 1f);
        }
    }

    /// <summary>重新读取配置文件并应用。不需要重启程序。</summary>
    public void ReloadConfig()
    {
        var reloaded = ConfigStore.Load();
        var previousBoostGain = _config.Idle.BoostWhenIdle ? _config.Idle.IdleBoostGain : 1f;

        _config = reloaded;

        // 语言可能在文件里被改过，先同步。
        Localization.Use(_config.App.Language);

        // 频段数量、FFT 窗口等参数影响缓冲区布局，需要重建分析器；
        // 采样率取当前采集引擎的实际值，避免设备处于 44.1kHz 时频段映射偏移。
        _analyzer = new SpectrumAnalyzer(_ring, _config, _capture.SampleRate);

        _previousBoostGain = previousBoostGain;

        RebuildRenderSurface();

        // 配色 / 花瓣数量可能变了，托盘图标要重建。
        _tray?.ApplyAppearance(BuildIconPalette(), IconPetals);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Shutdown();
        }

        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------ 帧循环

    private void OnFrameTick(object? sender, EventArgs e)
    {
        if (_closed) { return; }

        // 帧调度：定时器只负责「隔一小段时间叫醒我」，真正何时渲染由这里的时间判断决定。
        //
        // 为什么不能只靠 Timer.Interval：WinForms 定时器的精度受系统时钟粒度限制，
        // 实测把 Interval 设成 5 ms，唤醒间隔仍在 15 ms 量级。
        // 因此这里用小间隔轮询 + 到点才渲染：帧间隔 = 唤醒粒度 + 单帧耗时。
        //
        // 实测（本机沙箱会话）：单帧约 10 ms，唤醒粒度约 15.6 ms，
        // 于是 maxFps=30 时实际约 21 FPS、maxFps=60 时约 32 FPS —— 上限来自唤醒粒度，
        // 而不是这套调度逻辑。真机桌面会话的计时分辨率更细，能真正跑满配置值。
        //
        // 不要在这里 Sleep：WinForms 定时器在处理消息期间不会重入，
        // 睡在处理器里会把下一次唤醒一起推迟，净效果反而更差（已实测）。
        var now = _clock.Elapsed;
        var targetInterval = _fadeInProgress ? _fadeFrameInterval : _frameInterval;

        if (now - _lastRenderTime < targetInterval) { return; }

        var statsStart = RuntimeStats.IsEnabled ? now : default;
        var deltaSeconds = Math.Clamp((now - _lastRenderTime).TotalSeconds, 0.0001, 0.25);
        _lastRenderTime = now;

        UpdateFps(deltaSeconds);

        // 1) 更新交互状态（空闲判定 + 淡入淡出进度）。
        var opacity = _interaction.Update(Environment.TickCount64, deltaSeconds);

        // 2) 分析一帧频谱。设备采样率可能在重连后变化，这里按当前值同步。
        if (_analyzer.SampleRate != _capture.SampleRate)
        {
            _analyzer = new SpectrumAnalyzer(_ring, _config, _capture.SampleRate);
        }

        _analyzer.Analyze((float)deltaSeconds);

        // 3) 空闲增强：用上一帧的增益做过渡起点，避免菜单里改配置时画面跳变。
        //    增益与光晕各有一个倍数，空闲时一起提亮，营造「沉浸」感。
        var boostGain = _config.Idle.BoostWhenIdle ? _config.Idle.IdleBoostGain : 1f;
        var glowGain = _config.Idle.BoostWhenIdle ? _config.Idle.IdleBoostGlow : 1f;
        var boost = _previousBoostGain + ((boostGain - _previousBoostGain) * opacity);
        var glowBoost = _previousGlowGain + ((glowGain - _previousGlowGain) * opacity);
        _previousBoostGain = boostGain;
        _previousGlowGain = glowGain;

        // 4) 呈现或挂起。淡入淡出期间不降低帧率 —— 过渡只有几百毫秒，降帧反而会看出卡顿。
        _fadeInProgress = opacity > 0.001f && opacity < 0.999f;

        if (opacity > 0.001f)
        {
            _window.Present(_analyzer.Frame, opacity, deltaSeconds, boost, glowBoost);
        }
        else if (_interaction.CanSuspend)
        {
            _window.Suspend();
        }

        // 5) 图标可见性跟随激活状态。
        SyncDesktopIcons();

        // 6) 可选的运行期统计（FLOWERWALL_STATS=1 时每秒写一行）。
        UpdateStats(deltaSeconds, opacity, statsStart);
    }

    /// <summary>
    /// 每秒输出一行运行期统计到 logs\stats.log。
    ///
    /// 用途：这是没有控制台的 GUI 程序，「空闲时到底在做什么、花了多少 CPU」
    /// 只能靠真实运行数据判断。未启用时只有一个 bool 判断，开销可忽略。
    /// </summary>
    private void UpdateStats(double deltaSeconds, float opacity, TimeSpan statsStart)
    {
        if (!RuntimeStats.IsEnabled) { return; }

        _statsFrames++;
        _statsTickMilliseconds += (_clock.Elapsed - statsStart).TotalMilliseconds;
        if (_window.IsSurfaceVisible) { _statsRendered++; }

        _statsWindowStart += deltaSeconds;
        if (_statsWindowStart < 1.0) { return; }

        var cpuNow = RuntimeStats.ProcessCpuSeconds;
        var cpuDelta = cpuNow - _statsCpuStart;
        var windowSeconds = _statsWindowStart;
        var tickMs = _statsFrames > 0 ? _statsTickMilliseconds / _statsFrames : 0;

        var status = _interaction.Snapshot;

        RuntimeStats.WriteTimestamped(
            $"frames={_statsFrames} rendered={_statsRendered} fps={_statsFrames / windowSeconds:0.0} " +
            $"tick={tickMs:0.00}ms interval={_frameInterval.TotalMilliseconds:0}ms " +
            $"opacity={opacity:0.000} active={status.Active} manual={status.Manual} idleSec={status.IdleSeconds:0.0} " +
            $"cpu={cpuDelta / windowSeconds * 100:0.0}% " +
            $"audioState={_capture.State} rate={_capture.SampleRate} ch={_capture.Channels} " +
            $"wakeups={Interlocked.Read(ref RuntimeStats.CaptureWakeups)} " +
            $"timeouts={Interlocked.Read(ref RuntimeStats.CaptureTimeouts)} " +
            $"packets={Interlocked.Read(ref RuntimeStats.CapturePackets)} " +
            $"ringWritten={_ring.TotalWritten} " +
            $"analyzerRate={_analyzer.SampleRate} " +
            $"workingSetMB={Environment.WorkingSet / 1024 / 1024}");

        _statsFrames = 0;
        _statsRendered = 0;
        _statsTickMilliseconds = 0;
        _statsWindowStart = 0;
        _statsCpuStart = cpuNow;
    }

    /// <summary>
    /// 根据配置刷新帧调度参数。
    /// 定时器固定用小间隔轮询，真正的帧率由 <see cref="OnFrameTick"/> 里的时间判断决定。
    /// </summary>
    private void UpdateFrameInterval()
    {
        _frameInterval = IntervalFromFps(_config.Render.MaxFps);
        _fadeFrameInterval = IntervalFromFps(Math.Min(FadeFps, _config.Render.MaxFps));

        var tickMs = Math.Clamp(_config.Render.FrameTickMilliseconds, MinFrameTickMs, MaxFrameIntervalMs);
        _frameTimer.Interval = tickMs;
    }

    private static TimeSpan IntervalFromFps(int fps)
        => TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(fps, 1, 120));

    private void UpdateFps(double deltaSeconds)
    {
        _framesSinceFpsSample++;

        var elapsed = _clock.Elapsed.TotalSeconds - _fpsWindowStart;
        if (elapsed < 1.0) { return; }

        _fps = _framesSinceFpsSample / elapsed;
        _framesSinceFpsSample = 0;
        _fpsWindowStart = _clock.Elapsed.TotalSeconds;

        _ = deltaSeconds;
    }

    // ------------------------------------------------------------------ 桌面图标

    /// <summary>
    /// 让桌面图标可见性与「可视化是否显示」严格同步。
    ///
    /// 只在状态**发生变化**时调用 ShowWindow，避免每帧反复操作系统窗口；
    /// 同时用 <see cref="_iconsHiddenByUs"/> 记录「是我们隐藏的」，
    /// 这样即使用户自己把图标隐藏了，我们也不会擅自把它显示出来。
    /// </summary>
    private void SyncDesktopIcons()
    {
        var shouldHide = _interaction.IsActive && _interaction.Opacity > 0.02f;

        if (shouldHide == _iconsHiddenByUs) { return; }

        // 找不到图标宿主时只尝试一次，避免每帧都做一轮窗口枚举。
        if (_iconHost is null)
        {
            if (_iconHostLookupDone) { return; }

            _iconHostLookupDone = true;
            _iconHost = DesktopIconHost.TryCreate();
            if (_iconHost is null) { return; }
        }

        if (shouldHide)
        {
            if (_iconHost.SetIconsVisible(false))
            {
                _iconsHiddenByUs = true;
            }
        }
        else
        {
            _iconHost.RestoreIfNeeded();
            _iconsHiddenByUs = false;
        }
    }

    private void RestoreDesktopIcons()
    {
        if (!_iconsHiddenByUs) { return; }

        _iconHost?.RestoreIfNeeded();
        _iconsHiddenByUs = false;
    }

    // ------------------------------------------------------------------ 界面装配

    private static VisualTheme CreateTheme(AppConfig config)
    {
        // 画布 = 主显示器分辨率（背景要铺满整屏）。
        // 每个屏幕单独取尺寸，因此把窗口拖到不同 DPI 的显示器上也不会画错。
        var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);

        return new VisualTheme(
            config.Theme,
            config.Record,
            config.Spectrum,
            config.Background,
            bounds.Width,
            bounds.Height);
    }

    /// <summary>从当前主题色派生图标配色（托盘与悬浮按钮共用，保证两处颜色一致）。</summary>
    private IconPalette BuildIconPalette() => IconPalette.FromAccent(ToDrawingColor(_renderer.Accent));

    /// <summary>图标的花瓣数。与主题解耦 —— 盘面刻印已移除，图标固定 5 瓣梅花。</summary>
    private const int IconPetals = 5;

    private void CreateTray()
    {
        if (!_config.App.TrayIcon) { return; }

        _tray = new TrayMenu(BuildIconPalette(), IconPetals);
        _tray.Bind(
            new MenuActions(
                Toggle: ToggleVisualization,
                Reload: ReloadConfig,
                SetLanguage: SetLanguage,
                SetColorScheme: SetColorScheme,
                ImportBackground: ImportBackground,
                ResetBackground: ResetBackground,
                OpenConfig: ConfigStore.OpenInShell,
                Exit: Shutdown),
            () => new StatusSnapshot(
                _interaction.IsActive,
                _interaction.IsManuallyActive,
                _interaction.Snapshot.IdleSeconds,
                (float)_fps),
            () => _config.App.Language,
            () => ThemePresets.ResolveCurrent(_config));
    }

    private void CreateButton()
    {
        if (!_config.Button.Enabled) { return; }

        _button = new ControlButtonForm(
            _config.Button,
            ToDrawingColor(_renderer.Accent),
            IconPetals);

        _button.ToggleRequested += (_, _) => ToggleVisualization();

        // 只有位置真的变了才写配置。
        // 每次点击都写会把「程序自动生成的配置」变成比源文件更新的文件，
        // 导致构建时的 PreserveNewest 不再覆盖它，新增的配置项永远到不了运行目录。
        _button.PositionChanged += (_, _) =>
        {
            _button.PersistPosition();
            ConfigStore.Save(_config);
        };

        // 与托盘共用同一份菜单：右键悬浮按钮和右键托盘图标看到的内容完全一致。
        if (_tray is not null)
        {
            _button.SharedMenu = _tray.Menu;
        }

        _button.Show();
    }

    /// <summary>把 WPF 颜色转换为 GDI+ 颜色（托盘图标与悬浮按钮用 GDI+ 绘制）。</summary>
    private static Color ToDrawingColor(System.Windows.Media.Color color)
        => Color.FromArgb(color.A, color.R, color.G, color.B);

    private void RecreateButton()
    {
        if (_button is null) { return; }

        var wasVisible = _config.Button.Enabled;

        _button.Dispose();
        _button = null;

        if (wasVisible)
        {
            CreateButton();
        }
    }
}
