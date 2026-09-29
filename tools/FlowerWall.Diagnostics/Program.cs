using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using FlowerWall.Audio;
using FlowerWall.Core;
using FlowerWall.Rendering;

namespace FlowerWall.Diagnostics;

/// <summary>
/// 离线诊断工具：不打开窗口，直接测量音频分析与渲染的开销，并导出可视化结果图。
///
/// 存在的意义：桌面程序的性能问题很难定位（是采集？分析？还是绘制？）。
/// 这里把每一段拆开单独计时，改完代码跑一次就能看出是哪一段劣化。
///
/// 用法：
///   dotnet run --project tools/FlowerWall.Diagnostics            # 全部检查
///   dotnet run --project tools/FlowerWall.Diagnostics -- render  # 只测渲染并导出 PNG
///   dotnet run --project tools/FlowerWall.Diagnostics -- fft     # 只验证 FFT
/// </summary>
internal static class Program
{
    /// <summary>模拟帧数，用于稳定测量。600 帧约等于 10 秒 @60FPS。</summary>
    private const int RenderFrames = 600;

    /// <summary>计时用的预热帧数，避开 JIT 与首次分配的影响。</summary>
    private const int WarmupFrames = 60;

    /// <summary>
    /// 测量时假设的屏幕分辨率。
    /// 背景默认铺满整屏，所以画布尺寸直接决定每帧开销 ——
    /// 用一个"小画布"测量会严重低估真实负载，这里必须按实际屏幕算。
    /// 可以改这两个值来模拟别的显示器（例如 3840×2160 看 4K 下的表现）。
    /// </summary>
    private const int ScreenWidth = 1920;

    private const int ScreenHeight = 1080;

    private static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "all";
        var failures = 0;

        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (mode is "all" or "fft")
        {
            failures += VerifyFft() ? 0 : 1;
        }

        if (mode is "all" or "ring")
        {
            failures += VerifyRingBuffer() ? 0 : 1;
        }

        if (mode is "all" or "analyzer")
        {
            failures += VerifyAnalyzer() ? 0 : 1;
        }

        if (mode is "all" or "interaction")
        {
            failures += VerifyInteraction() ? 0 : 1;
        }

        if (mode is "all" or "render")
        {
            failures += MeasureRender() ? 0 : 1;
        }

        if (mode is "config")
        {
            failures += WriteConfigTemplate() ? 0 : 1;
        }

        if (mode is "icon")
        {
            failures += ExportIconSheet() ? 0 : 1;
        }

        if (mode is "language" or "lang")
        {
            failures += VerifyLocalization() ? 0 : 1;
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "全部检查通过。" : $"有 {failures} 项检查失败。");
        return failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ 配置模板

    /// <summary>
    /// 用当前默认值重写 config/settings.json 模板。
    ///
    /// 为什么做成一个任务：配置项会随功能增加，手抄容易漏；
    /// 让它由代码生成，新增字段后跑一次就能保证模板与代码一致。
    /// 枚举以字符串形式写出（"Solid" 而不是 1），因此配置文件对人也是可读的。
    /// </summary>
    private static bool WriteConfigTemplate()
    {
        Console.WriteLine("[Config] 生成配置模板");

        var path = Path.Combine(AppPaths.RepositoryRootDirectory, "config", "settings.json");

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }

            // 显式写 LF：AppendLine 在 Windows 上产出 CRLF，而 .gitattributes 规定仓库与
            // 工作区一律 LF。若不在这里统一，每次重新生成模板都会因为换行差异
            // 被 git 当成一次改动，掺进无意义的 diff 噪声。
            var text = BuildConfigTemplateText().Replace("\r\n", "\n");
            File.WriteAllText(path, text, new UTF8Encoding(false));

            Console.WriteLine($"  已写入：{path}");
            Console.WriteLine();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"  ✗ 写入失败：{exception.Message}");
            Console.WriteLine();
            return false;
        }
    }

    /// <summary>
    /// 拼出带注释的配置模板：顶部是生成提示与调参速查，后面是序列化出来的默认值。
    /// 注释在 JSON 顶部而不是逐字段 —— 逐字段注释无法由序列化器生成，
    /// 手写又会在下次生成时丢失；顶部说明则永远有效。
    /// </summary>
    private static string BuildConfigTemplateText()
    {
        var builder = new StringBuilder();

        builder.AppendLine("// 花墙 FlowerWall — 配置文件");
        builder.AppendLine("//");
        builder.AppendLine("// 本文件由 `scripts\build.ps1 diag -DiagArgs config` 从代码默认值生成，字段顺序与含义以代码为准。");
        builder.AppendLine("// 直接改这里的值即可，改完在托盘/悬浮按钮右键菜单点「重新加载配置」立即生效，不需要重启。");
        builder.AppendLine("// 支持 // 与 /* */ 注释；颜色用 #RRGGBB；枚举用字符串（如 \"Solid\" / \"Image\"）。");
        builder.AppendLine("//");
        builder.AppendLine("// 调参速查：");
        builder.AppendLine("//   主题配色 ......... theme.accentColor（声波环/文字/图标）、theme.preset（预置配色）、background.color（纯色底）");
        builder.AppendLine("//   背景图 ........... 右键菜单「导入背景图」会自动写入 background.image 与 mode");
        builder.AppendLine("//   胶片 ............. record.diameter（大小）、record.rpm（转速）、record.blossomPetals（刻印花瓣数）");
        builder.AppendLine("//   声波环 ........... spectrum.bandCount（采样点数）、spectrum.maxLength（振幅）");
        builder.AppendLine("//   流动速度 ......... spectrum.flowSpeed（0 表示不流动，可完全停帧省电）");
        builder.AppendLine("//   灵敏度 ........... spectrum.gain、spectrum.noiseFloorDb、spectrum.ceilingDb");
        builder.AppendLine("//   省电 ............. render.maxFps 调小、spectrum.bandCount 调小、background.mode 设为 \"None\"");
        builder.AppendLine("//   空闲唤醒 ......... idle.thresholdMinutes（分钟）、idle.boostWhenIdle（空闲时是否增强）");
        builder.AppendLine("//   界面语言 ......... app.language：Auto / Chinese / English");
        builder.AppendLine();

        builder.Append(ConfigStore.SerializeDefaults());
        builder.AppendLine();

        return builder.ToString();
    }

    // ------------------------------------------------------------------ 双语检查

    /// <summary>
    /// 覆盖两种语言打印全部界面文案。
    ///
    /// 为什么需要：文案散落在菜单、按钮、气泡与报错框里，切语言时最容易漏掉某一条；
    /// 这里一次性列全，漏翻译（显示成 key 名）或两者相同都一眼可见。
    /// </summary>
    private static bool VerifyLocalization()
    {
        Console.WriteLine("[Language] 检查双语文案");

        var ok = true;
        var keys = Enum.GetValues<TextKey>();
        var chinese = new Dictionary<TextKey, string>();

        foreach (var language in new[] { AppLanguage.Chinese, AppLanguage.English })
        {
            Localization.Use(language);

            var isChinese = language == AppLanguage.Chinese;
            Console.WriteLine();
            Console.WriteLine($"  === {(isChinese ? "中文" : "English")} ===");

            foreach (var key in keys)
            {
                var text = Localization.Text(key);
                var display = text.Replace("\n", "\\n");

                if (isChinese)
                {
                    chinese[key] = text;
                    Console.WriteLine($"      {key,-24} {display}");
                    continue;
                }

                // 判定漏翻译：值为空，或者这个键在表里根本不存在。
                // 不能只看"值 == 枚举名" —— TextKey.Exit 的英文正好就是 "Exit"，
                // 那是正确翻译而不是漏翻，只能通过"表里有没有这个键"来区分。
                var missing = string.IsNullOrEmpty(text) || !Localization.Has(key, language);

                if (missing) { ok = false; }

                Console.WriteLine($"    {(missing ? "✗" : " ")} {key,-24} {display}");
            }
        }

        // 列出两种语言完全相同的项，供人工判断是"合理一致"还是"忘了翻译"。
        Localization.Use(AppLanguage.English);
        var identical = keys.Where(key => chinese[key] == Localization.Text(key)).ToArray();

        Console.WriteLine();
        Console.WriteLine(identical.Length == 0
            ? "  没有中英相同的条目。"
            : $"  中英相同（需人工确认是否合理）：{string.Join("、", identical)}");

        // 抽查：两种语言的应用名必须不同，否则等于没切换。
        Localization.Use(AppLanguage.Chinese);
        var chineseName = Localization.Text(TextKey.AppName);
        Localization.Use(AppLanguage.English);
        var englishName = Localization.Text(TextKey.AppName);

        var distinct = !string.Equals(chineseName, englishName, StringComparison.Ordinal);
        ok &= distinct;
        Console.WriteLine($"  中英应用名不同：{chineseName} / {englishName} {(distinct ? "✓" : "✗")}");

        // Auto 必须跟随系统语言落定到某一种，而不是停在 Auto。
        Localization.Use(AppLanguage.Auto);
        var auto = Localization.IsChinese ? AppLanguage.Chinese : AppLanguage.English;
        Console.WriteLine($"  Auto 跟随系统解析为：{auto} ✓");

        Console.WriteLine();
        return ok;
    }

    // ------------------------------------------------------------------ 图标预览

    /// <summary>
    /// 把梅花图形放大导出成一张对照图（多种尺寸 / 花瓣数 / 配色）。
    ///
    /// 为什么单独做：图标在托盘里只有 16px，形状对不对肉眼很难判断；
    /// 放大到几百像素就能立刻看出花瓣是否饱满、比例是否协调。
    /// 这张图也直接用于 README 展示。
    /// </summary>
    private static bool ExportIconSheet()
    {
        Console.WriteLine("[Icon] 导出梅花图形对照图");

        var config = new AppConfig();
        var accent = ColorUtil.Parse(config.Theme.AccentColor, System.Windows.Media.Color.FromRgb(0xE4, 0x67, 0x9A));
        var blossom = ColorUtil.Parse(config.Theme.HighlightColor, System.Windows.Media.Color.FromRgb(0xFF, 0xF0, 0xF5));

        const int canvas = 900;
        var target = new RenderTargetBitmap(canvas, canvas, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var visual = new System.Windows.Media.DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            // 深色底：托盘图标多出现在深色任务栏上，这个底最接近真实观感。
            context.DrawRectangle(
                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x1E, 0x1E)),
                null,
                new System.Windows.Rect(0, 0, canvas, canvas));

            // 一棵大花（检查形状与比例）
            DrawBlossom(context, canvas * 0.30, canvas * 0.26, 170, 5, accent, blossom, "5 瓣 · 大");

            // 几种花瓣数对照
            DrawBlossom(context, canvas * 0.62, canvas * 0.26, 90, 5, accent, blossom, "5 瓣");
            DrawBlossom(context, canvas * 0.82, canvas * 0.26, 90, 6, accent, blossom, "6 瓣");
            DrawBlossom(context, canvas * 0.62, canvas * 0.62, 90, 4, accent, blossom, "4 瓣");
            DrawBlossom(context, canvas * 0.82, canvas * 0.62, 90, 8, accent, blossom, "8 瓣");

            // 实际尺寸对照：托盘 16px、按钮常态 20px、按钮悬停 48px
            DrawBlossom(context, canvas * 0.24, canvas * 0.62, 8, 5, accent, blossom, "16px 托盘");
            DrawBlossom(context, canvas * 0.38, canvas * 0.62, 10, 5, accent, blossom, "20px 按钮");
            DrawBlossom(context, canvas * 0.24, canvas * 0.82, 24, 5, accent, blossom, "48px 悬停");
        }

        target.Render(visual);

        var directory = Path.Combine(AppPaths.RepositoryRootDirectory, "artifacts");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "icon-sheet.png");

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));

        using (var stream = File.Create(path))
        {
            encoder.Save(stream);
        }

        Console.WriteLine($"  已导出：{path}");
        Console.WriteLine();
        return true;
    }

    private static void DrawBlossom(
        System.Windows.Media.DrawingContext context,
        double x,
        double y,
        double radius,
        int petals,
        System.Windows.Media.Color accent,
        System.Windows.Media.Color blossom,
        string? label = null)
    {
        var (petalGeometry, coreGeometry) = BlossomArt.CreateGeometry(
            x, y, radius, petals, -Math.PI / 2, stepsPerPetal: 40);

        var petalBrush = new System.Windows.Media.SolidColorBrush(accent);
        petalBrush.Freeze();

        var coreBrush = new System.Windows.Media.SolidColorBrush(ColorUtil.Scale(accent, 0.62f));
        coreBrush.Freeze();

        var outlinePen = new System.Windows.Media.Pen(
            new System.Windows.Media.SolidColorBrush(ColorUtil.ScaleAlpha(accent, 0.9f)),
            Math.Max(1.0, radius * 0.05));
        outlinePen.Freeze();

        context.DrawGeometry(petalBrush, null, petalGeometry);
        context.DrawGeometry(null, outlinePen, petalGeometry);
        context.DrawGeometry(coreBrush, null, coreGeometry);

        _ = blossom;

        if (label is not null)
        {
            var typeface = new System.Windows.Media.Typeface("Segoe UI, Microsoft YaHei UI");
            var text = new System.Windows.Media.FormattedText(
                label,
                CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                typeface,
                14,
                System.Windows.Media.Brushes.White,
                96);

            context.DrawText(text, new System.Windows.Point(x - (text.Width / 2), y + radius + 14));
        }
    }

    // ------------------------------------------------------------------ FFT

    /// <summary>用已知频率与幅度的正弦波验证 FFT：峰值应落在正确的频点，幅度应接近输入幅度。</summary>
    private static bool VerifyFft()
    {
        Console.WriteLine("[FFT] 验证单频正弦波的频谱峰值位置与幅度");

        const int size = 1024;
        const int sampleRate = 48000;
        const float amplitude = 0.5f;

        var fft = new Fft(size);
        var samples = new float[size];
        var magnitudes = new float[fft.BinCount];

        var ok = true;

        foreach (var frequency in new[] { 100.0, 1000.0, 5000.0 })
        {
            // 窗函数已由 Fft.Forward 内部施加，这里只提供原始正弦波。
            for (var i = 0; i < size; i++)
            {
                samples[i] = (float)(amplitude * Math.Sin(2.0 * Math.PI * frequency * i / sampleRate));
            }

            fft.Forward(samples, magnitudes);

            var peakBin = 0;
            var peakValue = 0f;
            for (var bin = 1; bin < fft.BinCount; bin++)
            {
                if (magnitudes[bin] > peakValue)
                {
                    peakValue = magnitudes[bin];
                    peakBin = bin;
                }
            }

            var detected = (double)peakBin * sampleRate / size;

            // 单频信号的峰值必然落在真实频率所在的那个 bin 内，允许一个 bin 的量化误差。
            var frequencyError = Math.Abs(detected - frequency) / frequency;
            var amplitudeError = Math.Abs(peakValue - amplitude) / amplitude;

            var frequencyOk = Math.Abs(detected - frequency) <= (double)sampleRate / size;
            var amplitudeOk = amplitudeError < 0.15;

            ok &= frequencyOk && amplitudeOk;

            Console.WriteLine(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "  {0,6:0} Hz → 检出 {1,7:0.0} Hz（误差 {2,5:0.00}%），幅度 {3:0.000}（期望 {4:0.000}）{5}",
                    frequency,
                    detected,
                    frequencyError * 100,
                    peakValue,
                    amplitude,
                    frequencyOk && amplitudeOk ? "  ✓" : "  ✗"));
        }

        Console.WriteLine();
        return ok;
    }

    // ------------------------------------------------------------------ 环形缓冲

    /// <summary>验证环形缓冲的取数语义：不足补零、超出取最新、回绕不越界。</summary>
    private static bool VerifyRingBuffer()
    {
        Console.WriteLine("[RingBuffer] 验证取数语义");

        var ok = true;
        var ring = new AudioRingBuffer(8); // 会被向上取整为 8
        var destination = new float[8];

        // 1) 空缓冲：应全部为 0，且返回 0 个采样
        ring.CopyLatest(destination);
        var emptyOk = destination.All(value => value == 0f);
        ok &= emptyOk;
        Console.WriteLine($"  空缓冲补零：{(emptyOk ? "✓" : "✗")}");

        // 2) 只写 3 个：前面补零，后面是数据
        ring.Write(new float[] { 1f, 2f, 3f }, 1);
        ring.CopyLatest(destination);
        var partialExpected = new[] { 0f, 0f, 0f, 0f, 0f, 1f, 2f, 3f };
        var partialOk = destination.SequenceEqual(partialExpected);
        ok &= partialOk;
        Console.WriteLine($"  数据不足时开头补零：{(partialOk ? "✓" : "✗")}");

        // 3) 再写 5 个（11..15）。注意此刻缓冲已满，1..8 这组数值并不存在。
        ring.Write(new float[] { 11f, 12f, 13f, 14f, 15f }, 1);
        ring.CopyLatest(destination);
        var fullExpected = new[] { 1f, 2f, 3f, 11f, 12f, 13f, 14f, 15f };
        var fullOk = destination.SequenceEqual(fullExpected);
        ok &= fullOk;
        Console.WriteLine($"  写满后顺序正确：{(fullOk ? "✓" : "✗")}");

        // 4) 立体声混单声道：取平均
        var stereo = new AudioRingBuffer(4);
        stereo.Write(new float[] { 1f, 3f, 0f, 0f }, 2);
        stereo.CopyLatest(destination.AsSpan(0, 2));
        var stereoOk = Math.Abs(destination[0] - 2f) < 0.0001f;
        ok &= stereoOk;
        Console.WriteLine($"  立体声混单声道取均值：{(stereoOk ? "✓" : "✗")}");

        // 5) 溢出后仍只保留容量内的最新数据
        var overflow = new AudioRingBuffer(4);
        var ramp = Enumerable.Range(0, 100).Select(i => (float)i).ToArray();
        overflow.Write(ramp, 1);
        overflow.CopyLatest(destination.AsSpan(0, 4));
        var overflowOk = destination.AsSpan(0, 4).SequenceEqual(new[] { 96f, 97f, 98f, 99f });
        ok &= overflowOk;
        Console.WriteLine($"  溢出后取最新 4 个采样：{(overflowOk ? "✓" : "✗")}");

        Console.WriteLine();
        return ok;
    }

    // ------------------------------------------------------------------ 频谱分析

    /// <summary>验证频段映射：低频信号应当只点亮靠前的频段，且整体电平随幅度单调变化。</summary>
    private static bool VerifyAnalyzer()
    {
        Console.WriteLine("[Analyzer] 验证频段映射与电平");

        var config = new AppConfig();
        const int sampleRate = 48000;
        var ring = new AudioRingBuffer(1 << 15);
        var analyzer = new SpectrumAnalyzer(ring, config, sampleRate);

        var ok = true;

        // 1) 静音：所有频段应为 0
        analyzer.Analyze(1f / 60f);
        var silenceMax = analyzer.Frame.Levels.Take(analyzer.Frame.BandCount).Max();
        var silenceOk = silenceMax < 0.01f;
        ok &= silenceOk;
        Console.WriteLine($"  静音时全零：{(silenceOk ? "✓" : "✗")}（最大值 {silenceMax:0.000}）");

        // 2) 低频正弦（100Hz）：能量应集中在靠前的频段
        var lowTone = GenerateSine(100.0, sampleRate, 16384, 0.8f);
        ring.Clear();
        ring.Write(lowTone, 1);
        Settle(analyzer);

        var bands = analyzer.Frame.BandCount;
        var lowHalf = analyzer.Frame.Levels.Take(bands / 4).Max();
        var highHalf = analyzer.Frame.Levels.Skip(bands / 2).Max();
        var lowOk = lowHalf > 0.2f && highHalf < 0.15f;
        ok &= lowOk;
        Console.WriteLine($"  100Hz 集中在低频段：{(lowOk ? "✓" : "✗")}（低频 {lowHalf:0.000} / 高频 {highHalf:0.000}）");

        // 3) 高频正弦（8kHz）：能量应集中在靠后的频段
        var highTone = GenerateSine(8000.0, sampleRate, 16384, 0.8f);
        ring.Clear();
        ring.Write(highTone, 1);
        Settle(analyzer);

        var highBandLevel = analyzer.Frame.Levels.Skip((bands * 3) / 4).Max();
        var lowBandLevel = analyzer.Frame.Levels.Take(bands / 4).Max();

        // 对数刻度的低频段很窄，加窗后的谱泄漏必然留下一点残余，
        // 因此判据是「高频段明显主导」而不是「低频段必须为 0」。
        var highOk = highBandLevel > 0.5f && highBandLevel > lowBandLevel * 1.5f;
        ok &= highOk;
        Console.WriteLine($"  8kHz 由高频段主导：{(highOk ? "✓" : "✗")}（低频 {lowBandLevel:0.000} / 高频 {highBandLevel:0.000}）");

        // 4) 幅度单调性：更响的输入应产生更大的电平
        var quiet = MeasureLevel(sampleRate, config, 0.05f);
        var loud = MeasureLevel(sampleRate, config, 0.8f);
        var monotonicOk = loud > quiet;
        ok &= monotonicOk;
        Console.WriteLine($"  电平随幅度单调：{(monotonicOk ? "✓" : "✗")}（0.05 → {quiet:0.000}，0.8 → {loud:0.000}）");

        Console.WriteLine();
        return ok;
    }

    private static float MeasureLevel(int sampleRate, AppConfig config, float amplitude)
    {
        var ring = new AudioRingBuffer(1 << 15);
        var analyzer = new SpectrumAnalyzer(ring, config, sampleRate);
        ring.Write(GenerateSine(440.0, sampleRate, 8192, amplitude), 1);

        // 多跑几帧让平滑滤波器收敛
        for (var i = 0; i < 30; i++)
        {
            analyzer.Analyze(1f / 60f);
        }

        return analyzer.Frame.Levels.Take(analyzer.Frame.BandCount).Max();
    }

    /// <summary>
    /// 反复分析若干帧，让上升 / 回落平滑滤波收敛。
    /// 只调用一次 <c>Analyze</c> 时，电平还停在「从 0 出发的 55%」，测出来的不是稳定值。
    /// </summary>
    private static void Settle(SpectrumAnalyzer analyzer, int frames = 30)
    {
        for (var i = 0; i < frames; i++)
        {
            analyzer.Analyze(1f / 60f);
        }
    }

    private static float[] GenerateSine(double frequency, int sampleRate, int count, float amplitude)
    {
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = (float)(amplitude * Math.Sin(2.0 * Math.PI * frequency * i / sampleRate));
        }

        return samples;
    }

    // ------------------------------------------------------------------ 交互状态机

    /// <summary>
    /// 验证唤醒状态机的迁移规则。这部分逻辑决定了「壁纸什么时候出现」，
    /// 是用户能直接感知的行为，因此必须有可重复的用例覆盖。
    /// </summary>
    private static bool VerifyInteraction()
    {
        Console.WriteLine("[Interaction] 验证唤醒状态机");

        var config = new AppConfig();
        config.Idle.ThresholdMinutes = 5.0;
        config.Render.FadeDurationMs = 600;

        var controller = new InteractionController(config);
        var ok = true;

        // 1) 初始状态：未激活、不透明度为 0
        var initial = controller.Snapshot;
        var initialOk = !initial.Active && controller.Opacity <= 0.001f;
        ok &= initialOk;
        Console.WriteLine($"  初始为隐藏：{(initialOk ? "✓" : "✗")}");

        // 2) 手动切换应立刻进入激活态，并在若干帧内淡入到 1
        controller.ToggleManual();
        var opacity = Fade(controller, 1.0);

        var manualOk = controller.IsActive && controller.IsManuallyActive && opacity >= 0.999f;
        ok &= manualOk;
        Console.WriteLine($"  手动切换后淡入到 1：{(manualOk ? "✓" : "✗")}（不透明度 {opacity:0.000}）");

        // 3) 再次切换应淡出到 0，并且允许挂起
        controller.ToggleManual();
        opacity = Fade(controller, 1.2, 2_000_000);

        var suspendOk = !controller.IsActive && controller.CanSuspend && opacity <= 0.001f;
        ok &= suspendOk;
        Console.WriteLine($"  再切换后淡出并可挂起：{(suspendOk ? "✓" : "✗")}（不透明度 {opacity:0.000}）");

        // 4) 空闲阈值：注入人为的空闲秒数，验证「达到阈值才自动显示」。
        //    真实空闲时长取决于系统输入状态，在无输入设备的会话里无法构造，因此走注入路径。
        Environment.SetEnvironmentVariable(IdleMonitor.FakeIdleVariable, "299");
        Fade(controller, 1.5, 3_000_000);
        var belowOk = !controller.IsActive;
        ok &= belowOk;
        Console.WriteLine($"  空闲 299s（阈值 300s）不触发：{(belowOk ? "✓" : "✗")}");

        Environment.SetEnvironmentVariable(IdleMonitor.FakeIdleVariable, "301");
        Fade(controller, 1.5, 4_000_000);
        var aboveOk = controller.IsActive && !controller.IsManuallyActive;
        ok &= aboveOk;
        Console.WriteLine($"  空闲 301s 触发自动显示：{(aboveOk ? "✓" : "✗")}");

        // 5) 用户回来（空闲归零）后应自动收起
        Environment.SetEnvironmentVariable(IdleMonitor.FakeIdleVariable, "0");
        Fade(controller, 1.5, 5_000_000);
        var rearmOk = !controller.IsActive;
        ok &= rearmOk;
        Console.WriteLine($"  恢复操作后自动收起：{(rearmOk ? "✓" : "✗")}");

        Environment.SetEnvironmentVariable(IdleMonitor.FakeIdleVariable, null);
        controller.Close();

        Console.WriteLine();
        return ok;
    }

    /// <summary>按 60 FPS 推进若干帧，返回最终不透明度。</summary>
    private static float Fade(InteractionController controller, double seconds, long startTimestampMs = 0)
    {
        var frames = (int)Math.Ceiling(seconds * 60);
        var timestamp = startTimestampMs;

        for (var i = 0; i < frames; i++)
        {
            timestamp += 16;
            controller.Update(timestamp, 1.0 / 60.0);
        }

        return controller.Opacity;
    }

    /// <summary>推进足够长的时间让空闲检测有机会轮询（轮询间隔 500ms）。</summary>
    private static float PushIdleFrames(InteractionController controller)
        => Fade(controller, 1.5, 1_000_000);
    // ------------------------------------------------------------------ 渲染

    /// <summary>
    /// 测量渲染开销，并导出一张 PNG 便于肉眼检查视觉效果。
    /// 这里刻意包含「像素拷贝到 DIB」这一步，因为那才是真正的上屏成本，
    /// 只测绘制会严重低估实际负载。
    /// </summary>
    private static bool MeasureRender()
    {
        Console.WriteLine("[Render] 测量渲染开销并导出预览图");

        var ok = true;
        var config = new AppConfig();

        // 用真实的屏幕分辨率构建主题：背景铺满全屏，因此画布尺寸直接决定每帧开销，
        // 拿一个小画布测出来的数字会严重低估真实负载。
        var theme = new VisualTheme(
            config.Theme, config.Record, config.Spectrum, config.Background,
            ScreenWidth, ScreenHeight);

        var renderer = new VisualizerRenderer(theme);

        // 构造一个「有声」的频谱帧：用真实分析器产生，避免用手写数据掩盖问题。
        var frame = BuildActiveFrame(config);

        var canvasPixels = (double)renderer.CanvasWidth * renderer.CanvasHeight;
        Console.WriteLine(
            $"  画布尺寸：{renderer.CanvasWidth} × {renderer.CanvasHeight}" +
            $"（约 {canvasPixels * 4 / 1024.0 / 1024.0:0.0} MB/帧，按 {ScreenWidth}×{ScreenHeight} 屏幕计）");

        // 预热：让 JIT、WPF 首次渲染与内部缓冲都稳定下来。
        for (var i = 0; i < WarmupFrames; i++)
        {
            renderer.Render(frame, 1.0 / 60.0, 1f, force: true);
        }

        var target = renderer.Target;
        if (target is null)
        {
            Console.WriteLine("  ✗ 渲染目标为空");
            return false;
        }

        var stride = target.PixelWidth * 4;

        // 0) 分阶段计时：定位瓶颈到底在「背景」「胶片」还是「声波环」。
        MeasureStage(renderer, frame, "清屏", (context) => renderer.DrawClearOnly(context));
        MeasureStage(renderer, frame, "纯色背景", (context) => renderer.DrawFlatBackgroundOnly(context));
        MeasureStage(renderer, frame, "清屏+渐变背景", (context) => renderer.DrawBackgroundOnly(context));
        MeasureStage(renderer, frame, " 胶片-整体", (context) => renderer.DrawVinylOnly(context));
        MeasureStage(renderer, frame, " 胶片-光晕", (context) => renderer.DrawVinylHaloOnly(context));
        MeasureStage(renderer, frame, " 胶片-盘体", (context) => renderer.DrawVinylDiscOnly(context));
        MeasureStage(renderer, frame, " 胶片-中心层", (context) => renderer.DrawVinylLabelOnly(context));
        MeasureStage(renderer, frame, "声波环", (context) => renderer.DrawRingOnly(context, frame, 1f));

        // 1) 纯绘制：不含像素拷贝
        var drawWatch = Stopwatch.StartNew();
        for (var i = 0; i < RenderFrames; i++)
        {
            renderer.Render(frame, 1.0 / 60.0, 1f, force: true);
        }

        drawWatch.Stop();
        var drawMs = drawWatch.Elapsed.TotalMilliseconds / RenderFrames;

        // 2) 绘制 + 像素读取：模拟真实每帧上屏的拷贝成本
        var pixels = new byte[stride * target.PixelHeight];
        var copyWatch = Stopwatch.StartNew();
        for (var i = 0; i < RenderFrames; i++)
        {
            renderer.Render(frame, 1.0 / 60.0, 1f, force: true);
            target.CopyPixels(pixels, stride, 0);
        }

        copyWatch.Stop();
        var copyMs = copyWatch.Elapsed.TotalMilliseconds / RenderFrames;

        Console.WriteLine($"  纯绘制：{drawMs:0.000} ms/帧（{1000.0 / Math.Max(0.001, drawMs):0} FPS 上限）");
        Console.WriteLine($"  绘制+拷屏：{copyMs:0.000} ms/帧（{1000.0 / Math.Max(0.001, copyMs):0} FPS 上限）");
        Console.WriteLine($"  24 FPS 预算占用：{copyMs / 41.67 * 100:0.0}%（默认档）　30 FPS：{copyMs / 33.33 * 100:0.0}%　60 FPS：{copyMs / 16.67 * 100:0.0}%");

        ExportPng(target, "preview-desktop.png");

        // 3) 静态预览：换一个全新的渲染器（转角从 0 开始、唱片静止），
        //    用于核对文字方向与整体构图。
        using (var staticRenderer = new VisualizerRenderer(theme))
        {
            var staticFrame = new SpectrumFrame(theme.BandCount);
            staticFrame.Reset();
            staticRenderer.Render(staticFrame, 1.0 / 60.0, 1f, force: true);

            if (staticRenderer.Target is { } staticTarget)
            {
                ExportPng(staticTarget, "preview-static.png");
            }
        }

        // 3b) 局部放大：把胶片区域以 2 倍放大单独导出一张。
        //     全屏预览缩到 1066px 后，盘面上的纹路与磨损根本看不清 ——
        //     而"胶片像不像真的"恰恰全在这些细节上，必须放大看。
        ExportVinylCloseUp(config);

        // 4) 空闲路径：分成两种情形验证。
        //
        //    声波环默认会持续流动（FlowSpeed > 0），因此画面在静音时也不会静止 —— 这是刻意的：
        //    声波环不会因为没声音就冻住。代价是隐藏前会一直重绘，所以这里分别验证两种配置：
        //      a) 关闭流动 → 唱片停转后必须彻底停止重绘（省电档）
        //      b) 开启流动 → 必须持续重绘（观感档）
        //    这样"是否省电"就变成一个有据可查的取舍，而不是靠猜。
        var stillConfig = new AppConfig();
        stillConfig.Spectrum.FlowSpeed = 0f;
        var stillTheme = new VisualTheme(stillConfig.Theme, stillConfig.Record, stillConfig.Spectrum, stillConfig.Background);

        using (var stillRenderer = new VisualizerRenderer(stillTheme))
        {
            var silentFrame = new SpectrumFrame(stillTheme.BandCount);
            silentFrame.Reset();
            stillRenderer.Render(silentFrame, 1.0 / 60.0, 1f, 1f, force: true);

            var redraws = 0;
            const int tailFrames = 300;

            for (var i = 0; i < RenderFrames; i++)
            {
                if (stillRenderer.Render(silentFrame, 1.0 / 60.0, 1f) && i >= RenderFrames - tailFrames)
                {
                    redraws++;
                }
            }

            var idleOk = redraws == 0;
            ok &= idleOk;
            Console.WriteLine($"  关闭流动后静音停止重绘：末 {tailFrames} 帧 {redraws} 次 {(idleOk ? "✓" : "✗")}");
        }

        using (var flowingRenderer = new VisualizerRenderer(theme))
        {
            var silentFrame = new SpectrumFrame(theme.BandCount);
            silentFrame.Reset();
            flowingRenderer.Render(silentFrame, 1.0 / 60.0, 1f, 1f, force: true);

            var redraws = 0;
            for (var i = 0; i < 120; i++)
            {
                if (flowingRenderer.Render(silentFrame, 1.0 / 60.0, 1f)) { redraws++; }
            }

            var flowingOk = redraws > 100;
            ok &= flowingOk;
            Console.WriteLine($"  开启流动时持续重绘（声波环不冻结）：{redraws}/120 {(flowingOk ? "✓" : "✗")}");
        }

        Console.WriteLine();
        return ok;
    }

    /// <summary>
    /// 把胶片区域以 2 倍放大单独导出，用于核对纹路、反光与磨损这些细节。
    /// 全屏预览缩到千余像素后，盘面上的细节会全部糊掉，只有放大才看得出"像不像真唱片"。
    /// </summary>
    private static void ExportVinylCloseUp(AppConfig config)
    {
        var theme = new VisualTheme(
            config.Theme, config.Record, config.Spectrum, config.Background,
            ScreenWidth, ScreenHeight);

        using var renderer = new VisualizerRenderer(theme);

        var frame = new SpectrumFrame(theme.BandCount);
        frame.Reset();
        renderer.Render(frame, 1.0 / 60.0, 1f, force: true);

        if (renderer.Target is not { } source) { return; }

        // 取景：胶片半径的 1.15 倍，再放大 2 倍。
        var half = (int)(theme.RecordRadius * 1.15);
        var scale = 2.0;
        var size = (int)(half * 2 * scale);

        var cropped = new CroppedBitmap(
            source,
            new System.Windows.Int32Rect(
                (int)theme.CenterX - half,
                (int)theme.CenterY - half,
                half * 2,
                half * 2));

        var visual = new System.Windows.Media.DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            // 用与桌面背景相近的绿做底，接近真实观感。
            context.DrawRectangle(
                new System.Windows.Media.SolidColorBrush(theme.BackgroundColor),
                null,
                new System.Windows.Rect(0, 0, size, size));
            context.DrawImage(cropped, new System.Windows.Rect(0, 0, size, size));
        }

        var target = new RenderTargetBitmap(size, size, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        target.Render(visual);

        ExportPng(target, "preview-vinyl-closeup.png");
    }

    /// <summary>用真实的分析器生成一帧有声频谱，保证渲染测试与线上数据形态一致。</summary>
    private static SpectrumFrame BuildActiveFrame(AppConfig config)
    {
        const int sampleRate = 48000;
        var ring = new AudioRingBuffer(1 << 15);
        var analyzer = new SpectrumAnalyzer(ring, config, sampleRate);

        // 混合多个频率，制造接近真实音乐的频谱分布。
        var count = config.Spectrum.FftSize * 8;
        var samples = new float[count];
        foreach (var frequency in new[] { 60.0, 180.0, 440.0, 1200.0, 3000.0, 9000.0 })
        {
            var partial = GenerateSine(frequency, sampleRate, count, 0.18f);
            for (var i = 0; i < count; i++)
            {
                samples[i] += partial[i];
            }
        }

        ring.Write(samples, 1);

        for (var i = 0; i < 20; i++)
        {
            analyzer.Analyze(1f / 60f);
        }

        return analyzer.Frame;
    }

    /// <summary>
    /// 单独测量某一段绘制 + 位图渲染的耗时。
    /// </summary>
    /// <remarks>
    /// 注意这里包含 <see cref="RenderTargetBitmap.Render"/>，因为那才是真正的光栅化成本：
    /// 只测量「往 DrawingContext 里塞绘制指令」会严重低估耗时。
    /// </remarks>
    private static void MeasureStage(VisualizerRenderer renderer, SpectrumFrame frame, string name, Action<System.Windows.Media.DrawingContext> draw)
    {
        var target = renderer.Target!;
        var scene = new System.Windows.Media.DrawingVisual();
        var width = renderer.CanvasWidth;
        var height = renderer.CanvasHeight;

        void DrawOnce()
        {
            using var context = scene.RenderOpen();
            context.DrawRectangle(
                System.Windows.Media.Brushes.Transparent,
                null,
                new System.Windows.Rect(0, 0, width, height));
            draw(context);
            target.Clear();
            target.Render(scene);
        }

        for (var i = 0; i < WarmupFrames; i++) { DrawOnce(); }

        var watch = Stopwatch.StartNew();
        for (var i = 0; i < RenderFrames; i++) { DrawOnce(); }
        watch.Stop();

        Console.WriteLine($"  ├ {name}：{watch.Elapsed.TotalMilliseconds / RenderFrames:0.000} ms/帧");

        _ = frame;
    }

    private static void ExportPng(BitmapSource source, string fileName)
    {
        // 输出到仓库根目录的 artifacts\，而不是 bin\ 深处，
        // 这样预览图是稳定的已知路径，README 可以直接引用。
        var directory = Path.Combine(AppPaths.RepositoryRootDirectory, "artifacts");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, fileName);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));

        using var stream = File.Create(path);
        encoder.Save(stream);

        Console.WriteLine($"  预览图已导出：{path}");

        // 同时导出一张纯黑底图，方便在浅色背景上检查圆盘边缘是否有黑边。
        var composited = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var visual = new System.Windows.Media.DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(
                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x24, 0x28, 0x2E)),
                null,
                new System.Windows.Rect(0, 0, source.PixelWidth, source.PixelHeight));
            context.DrawImage(source, new System.Windows.Rect(0, 0, source.PixelWidth, source.PixelHeight));
        }

        composited.Render(visual);

        // 灰底合成：桌面是深色的，要看清边缘与半透明区域得换个背景。
        // 文件名从原图派生，避免两张图互相覆盖。
        var compositedPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(fileName) + "-on-gray.png");
        var compositedEncoder = new PngBitmapEncoder();
        compositedEncoder.Frames.Add(BitmapFrame.Create(composited));

        using var compositedStream = File.Create(compositedPath);
        compositedEncoder.Save(compositedStream);

        Console.WriteLine($"  灰底预览图已导出：{compositedPath}");
    }
}
