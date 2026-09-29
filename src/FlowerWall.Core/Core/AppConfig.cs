namespace FlowerWall.Core;

/// <summary>界面语言。</summary>
public enum AppLanguage
{
    /// <summary>跟随系统（中文环境用中文，其余用英文）。</summary>
    Auto,

    /// <summary>简体中文。</summary>
    Chinese,

    /// <summary>English.</summary>
    English,
}

/// <summary>背景模式。</summary>
public enum BackgroundMode
{
    /// <summary>不绘制背景，只有胶片与声波环。</summary>
    None,

    /// <summary>纯色 / 极淡的径向过渡。</summary>
    Solid,

    /// <summary>图片（本地文件或网络地址）。</summary>
    Image,
}

/// <summary>
/// 应用的全部可调参数。所有视觉与行为参数都必须落在这里，绘制代码里不允许出现魔法数字。
/// 字段通过 <see cref="ConfigStore"/> 与 config/settings.json 双向绑定。
/// </summary>
public sealed class AppConfig
{
    /// <summary>配置结构版本号，用于将来做迁移。</summary>
    public int Version { get; set; } = 2;

    public AppSettings App { get; set; } = new();

    public IdleSettings Idle { get; set; } = new();

    public ThemeSettings Theme { get; set; } = new();

    public BackgroundSettings Background { get; set; } = new();

    public RecordSettings Record { get; set; } = new();

    public SpectrumSettings Spectrum { get; set; } = new();

    public RenderSettings Render { get; set; } = new();

    public ButtonSettings Button { get; set; } = new();
}

/// <summary>进程级行为。</summary>
public sealed class AppSettings
{
    /// <summary>只允许一个实例运行。</summary>
    public bool SingleInstance { get; set; } = true;

    /// <summary>启动后立即进入可视化，而不是等待空闲或点击。</summary>
    public bool StartVisible { get; set; }

    /// <summary>是否创建托盘图标。</summary>
    public bool TrayIcon { get; set; } = true;

    /// <summary>界面语言。</summary>
    public AppLanguage Language { get; set; } = AppLanguage.Auto;
}

/// <summary>空闲唤醒机制。</summary>
public sealed class IdleSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>键鼠连续无操作的阈值（分钟），超过后自动唤醒。</summary>
    public double ThresholdMinutes { get; set; } = 5.0;

    /// <summary>空闲时是否增强表现（声波环幅度与光晕强度）。</summary>
    public bool BoostWhenIdle { get; set; } = true;

    /// <summary>空闲增强时的幅度倍数。</summary>
    public float IdleBoostGain { get; set; } = 1.35f;

    /// <summary>空闲增强时的光晕强度倍数。</summary>
    public float IdleBoostGlow { get; set; } = 1.5f;

    /// <summary>空闲检测轮询间隔（毫秒）。500 是灵敏度与开销之间合适的折中。</summary>
    public int PollIntervalMs { get; set; } = 500;
}

/// <summary>
/// 主题配色。颜色使用 #RRGGBB 或 #AARRGGBB。
/// 默认是樱花粉一套：饱和樱粉主色 + 近白高光，配黑色胶片形成强对比。
/// </summary>
public sealed class ThemeSettings
{
    /// <summary>
    /// 当前选用的预置方案名（Matcha / Forest / Paper / Midnight / Sakura）。
    /// 仅用于菜单显示与回读，实际生效的是下面几个具体色号 ——
    /// 因此手改色号不会被这个字段覆盖；改完菜单会显示为「自定义」。
    /// </summary>
    public string Preset { get; set; } = "Matcha";

    /// <summary>主强调色：声波环、倒影、中心文字、图标。</summary>
    public string AccentColor { get; set; } = "#E4679A";

    /// <summary>高光色：声波环波峰与文字高亮，比主色更亮。</summary>
    public string HighlightColor { get; set; } = "#FFF0F5";

    /// <summary>胶片本体色（保持黑色）。</summary>
    public string DiscColor { get; set; } = "#06080B";

    /// <summary>胶片纹路色。必须明显亮于盘面色，否则在真实屏幕上根本看不见纹理。</summary>
    public string GrooveColor { get; set; } = "#39434F";
}

/// <summary>
/// 背景。默认铺满整个桌面 —— 画布尺寸等于屏幕分辨率，
/// 中心算在屏幕中心，胶片与声波环仍保持固定像素尺寸居中。
/// </summary>
public sealed class BackgroundSettings
{
    public BackgroundMode Mode { get; set; } = BackgroundMode.Solid;

    /// <summary>
    /// 背景主色。默认抹茶绿 —— 刻意比早期版本更深、黄味更少：
    /// #D9E8D2 那种高明度浅绿在满屏时会显得发白发黄，像"褪色的绿"而不是抹茶。
    /// </summary>
    public string Color { get; set; } = "#A9C69A";

    /// <summary>背景中心色。与主色差别很小，仅提供一点点层次。</summary>
    public string CenterColor { get; set; } = "#B5CFA6";

    /// <summary>整体不透明度（0~1）。调到 0.5 左右能透出后面的桌面壁纸。</summary>
    public float Opacity { get; set; } = 1.0f;

    /// <summary>图片路径（Image 模式）。支持本地绝对路径或 http(s) 地址。</summary>
    public string Image { get; set; } = string.Empty;

    /// <summary>图片填充方式：Cover（裁切填满）或 Contain（完整显示）。</summary>
    public string Fit { get; set; } = "Cover";

    /// <summary>
    /// 中心到边缘的渐变强度（0~1）。**默认 0 = 完全平坦的纯色**。
    ///
    /// 为什么不默认开渐变：全屏铺一层径向渐变实测要 11.7ms/帧，纯色只要 2.1ms。
    /// 差价 5 倍以上，而"中心略亮"这种层次在满屏绿色上其实很难察觉。
    /// 想要层次就调到 0.15~0.3，此时会换成一段平缓的线性渐变。
    /// </summary>
    public float GradientStrength { get; set; }
}

/// <summary>黑胶唱片外观与运动。</summary>
public sealed class RecordSettings
{
    /// <summary>唱片直径（像素，主显示器物理像素）。</summary>
    public int Diameter { get; set; } = 560;

    /// <summary>中心标签直径（像素）。</summary>
    public int LabelDiameter { get; set; } = 250;

    /// <summary>中心轴孔直径（像素）。</summary>
    public int SpindleDiameter { get; set; } = 16;

    /// <summary>
    /// 纹路圈数。
    ///
    /// 取值受**可见性**约束，不是越多越好：纹路区宽度约为 (唱片半径 − 标签半径)，
    /// 默认尺寸下约 148px。圈数 150 时间距不到 1px，抗锯齿会把它们糊成一片均匀的深灰 ——
    /// 这正是"盘面看着一片黑、毫无纹理"的真正原因。
    /// 72 圈对应约 2px 间距，每一条都清晰可辨。想更密要先加大唱片直径。
    /// </summary>
    public int GrooveCount { get; set; } = 72;

    /// <summary>
    /// 纹路间距的不规则程度（0~1）。
    /// 真实唱片的纹路并非严格等距，0.35 左右最自然；设 0 会变成完美等距，一眼看出是程序画的。
    /// </summary>
    public float GrooveIrregularity { get; set; } = 0.35f;

    /// <summary>纹路整体不透明度（0~1）。调低盘面更"干净"，调高更"旧"。</summary>
    public float GrooveOpacity { get; set; } = 0.95f;

    /// <summary>
    /// 靠外圈的纹路相对内圈的亮度倍数。
    /// 真实唱片外圈反光更强，因此通常大于 1 —— 早期版本做成渐暗，结果整盘发黑看不见纹理。
    /// </summary>
    public float GrooveOuterBoost { get; set; } = 1.55f;

    /// <summary>径向反光强度（0~1）。模拟打光下沟槽的一条亮带，是黑胶最显眼的特征。</summary>
    public float SpecularStrength { get; set; } = 0.5f;

    /// <summary>磨损强度（0~1）：细划痕、边缘磨白、污渍的总开关。</summary>
    public float WearStrength { get; set; } = 0.6f;

    /// <summary>基准转速（转/分）。33.3 为标准 LP 转速。</summary>
    public float Rpm { get; set; } = 33.3f;

    /// <summary>有声音时的转速倍数。</summary>
    public float ActiveSpeedFactor { get; set; } = 1.6f;

    /// <summary>无声音时的转速倍数（略慢，像唱机惯性滑行）。</summary>
    public float SilentSpeedFactor { get; set; } = 0.45f;

    /// <summary>转速变化的平滑系数（0~1，越大越跟手）。</summary>
    public float SpeedSmoothing { get; set; } = 0.08f;

    /// <summary>低于此音量视为静音（0~1）。</summary>
    public float SpinStopThreshold { get; set; } = 0.02f;

    /// <summary>唱片边缘高光强度（0~1）。</summary>
    public float RimHighlight { get; set; } = 0.4f;

    /// <summary>
    /// 盘面中心文字的字号（相对标签半径的倍数）。
    /// 内容是星期（花体英文），不随界面语言变化。
    /// </summary>
    public float CenterTextScale { get; set; } = 0.5f;
}

/// <summary>环绕胶片的声波环。</summary>
public sealed class SpectrumSettings
{
    /// <summary>参与绘制的频段数量（采样点数）。越多越细腻，绘制成本线性上升。</summary>
    public int BandCount { get; set; } = 128;

    /// <summary>条的宽度（像素）。短条模式下它就是"梳齿"的粗细。</summary>
    public float BarWidth { get; set; } = 4.5f;

    /// <summary>声波环基线半径与胶片边缘的间距（像素）。</summary>
    public float GapFromRecord { get; set; } = 12.0f;

    /// <summary>声波环的最大振幅（像素）。</summary>
    public float MaxLength { get; set; } = 110.0f;

    /// <summary>声波环的最小振幅（像素），保证静音时也有一圈细条。</summary>
    public float MinLength { get; set; } = 4.0f;

    /// <summary>环绕起始角度（度）。-90 表示从正上方开始。</summary>
    public float StartAngleDeg { get; set; } = -90.0f;

    /// <summary>音频最低频率（Hz）。</summary>
    public float MinFrequency { get; set; } = 30.0f;

    /// <summary>音频最高频率（Hz）。</summary>
    public float MaxFrequency { get; set; } = 16000.0f;

    /// <summary>整体灵敏度倍数。</summary>
    public float Gain { get; set; } = 1.0f;

    /// <summary>噪声门限（dBFS）。低于该值的信号按 0 处理，避免底噪把声波环撑起来。</summary>
    public float NoiseFloorDb { get; set; } = -62.0f;

    /// <summary>映射上限（dBFS）。达到该值即为满格，调小会更灵敏。</summary>
    public float CeilingDb { get; set; } = -3.0f;

    /// <summary>低频段增益。</summary>
    public float LowGain { get; set; } = 1.2f;

    /// <summary>中频段增益。</summary>
    public float MidGain { get; set; } = 1.0f;

    /// <summary>高频段增益。</summary>
    public float HighGain { get; set; } = 0.8f;

    /// <summary>上升平滑系数（0~1，越大越快）。</summary>
    public float Attack { get; set; } = 0.5f;

    /// <summary>回落平滑系数（0~1，越大越快）。</summary>
    public float Release { get; set; } = 0.1f;

    /// <summary>峰值保持的回落速度（每秒，0~1）。</summary>
    public float PeakDecayPerSecond { get; set; } = 0.9f;

    /// <summary>主声波环的流动速度（圈/秒）。设 0 则不流动。</summary>
    public float FlowSpeed { get; set; } = 0.035f;

    /// <summary>副声波环相对主波的相位差（弧度）。约 π/2 让两层错开成波峰波谷。</summary>
    public float SecondaryPhaseRadians { get; set; } = 1.5708f;

    /// <summary>副声波环的幅度倍数。</summary>
    public float SecondaryAmplitude { get; set; } = 0.62f;

    /// <summary>
    /// 副声波环的不透明度倍数。
    /// 副波是叠在主波上的第二层，太实会让两层混成一坨（"粘黏感"的来源之一）。
    /// </summary>
    public float SecondaryOpacity { get; set; } = 0.4f;

    /// <summary>外侧倒影声波环的幅度倍数。</summary>
    public float ReflectionAmplitude { get; set; } = 0.75f;

    /// <summary>
    /// 外侧倒影向外延伸的长度（像素）。
    /// 要克制：取 70px 会配出一圈厚色块把声波环本体盖住，取 12px 才只是环外的一点余韵。
    /// </summary>
    public float ReflectionLength { get; set; } = 12.0f;

    /// <summary>外侧倒影的整体不透明度倍数。太高会变成"声波环外面套了一圈"。</summary>
    public float ReflectionOpacity { get; set; } = 0.14f;

    /// <summary>
    /// 整环柔光强度（0~1）。
    /// 它是把一圈粗描边叠在声波环下方做氛围，**代价是会让外圈显得很"厚"**。
    /// 早期取 0.5 时外面那层光比声波环本体还显眼，与"细环线"的取向冲突，因此压到 0.18。
    /// </summary>
    public float GlowStrength { get; set; } = 0.18f;

    /// <summary>FFT 窗口大小，必须是 2 的幂。2048 在 48kHz 下给出约 23Hz 的频率分辨率。</summary>
    public int FftSize { get; set; } = 2048;
}

/// <summary>渲染性能相关。低配机器优先调这里。</summary>
public sealed class RenderSettings
{
    /// <summary>
    /// 可视化可见时的目标帧率上限。
    ///
    /// 默认 24：背景铺满全屏后每帧像素量大幅上升，24 FPS 能在"跟得上"与"省电"之间取得平衡
    /// （唱片旋转与声波环起伏对帧率本就不敏感）。追求丝滑可调到 30 或 60。
    /// 注意这是**上限**而不是保证值 —— 实际帧率还受系统计时粒度与单帧耗时限制。
    /// </summary>
    public int MaxFps { get; set; } = 24;

    /// <summary>
    /// 帧调度用的定时器唤醒间隔（毫秒）。
    /// 它只是「多久检查一次是否该渲染」，真实帧率由帧循环里的时间判断决定。
    /// </summary>
    public int FrameTickMilliseconds { get; set; } = 5;

    /// <summary>淡入 / 淡出时长（毫秒）。</summary>
    public int FadeDurationMs { get; set; } = 600;
}

/// <summary>屏幕边缘的悬浮按钮。</summary>
public sealed class ButtonSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>常态直径（像素）。</summary>
    public int Diameter { get; set; } = 20;

    /// <summary>鼠标悬停时的直径（像素）。</summary>
    public int HoverDiameter { get; set; } = 48;

    /// <summary>距离屏幕边缘的留白（像素）。</summary>
    public int EdgeMargin { get; set; } = 24;

    /// <summary>默认停靠角落：TopLeft / TopRight / BottomLeft / BottomRight。</summary>
    public string DefaultCorner { get; set; } = "BottomRight";

    /// <summary>是否记住上次拖动的位置。</summary>
    public bool RememberPosition { get; set; } = true;

    /// <summary>常态下的整体不透明度（0~1）。</summary>
    public float IdleOpacity { get; set; } = 0.6f;

    /// <summary>上次拖动的落点（屏幕坐标），由程序自动写回。</summary>
    public PositionSettings Position { get; set; } = new();
}

/// <summary>屏幕坐标。</summary>
public sealed class PositionSettings
{
    public int X { get; set; } = -1;

    public int Y { get; set; } = -1;

    /// <summary>是否已有有效坐标。</summary>
    public bool IsValid => X >= 0 && Y >= 0;
}
