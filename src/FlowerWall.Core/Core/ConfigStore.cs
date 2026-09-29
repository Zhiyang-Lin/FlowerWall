using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace FlowerWall.Core;

/// <summary>
/// 配置读写。
///
/// 约定：
///   - 首次运行自动生成带默认值的 settings.json；
///   - 解析失败**不抛异常**，退回默认值并在 <see cref="LastError"/> 里留下原因，
///     这样「配置写错」不会让程序起不来；
///   - 使用源生成序列化（见 <see cref="AppConfigJsonContext"/>），启动更快、也不怕裁剪；
///   - 允许在 JSON 里写 // 与 /* */ 注释（.NET 8 的源生成器不支持 ReadCommentHandling，
///     因此由 <see cref="StripComments"/> 在反序列化前自行剥离）。
/// </summary>
public static class ConfigStore
{
    /// <summary>相对于项目根目录的默认配置位置。</summary>
    public const string RelativePath = "config/settings.json";

    /// <summary>最近一次读取或写入的错误信息。null 表示没有错误。</summary>
    public static string? LastError { get; private set; }

    /// <summary>配置文件绝对路径。</summary>
    public static string ConfigPath => AppPaths.Resolve(RelativePath);

    /// <summary>
    /// 读取配置。文件不存在时创建默认配置并返回默认值。
    /// 解析失败时退回默认值并记录 <see cref="LastError"/>。
    /// </summary>
    public static AppConfig Load()
    {
        LastError = null;

        var path = ConfigPath;
        if (!File.Exists(path))
        {
            // 刻意不在这里写文件：让「源配置」保持比运行目录的副本更新，
            // 构建时的 PreserveNewest 才会把新增配置项同步过去。
            // 需要落盘时由菜单「打开配置文件位置」或用户改动触发 Save。
            return new AppConfig();
        }

        try
        {
            var text = File.ReadAllText(path);

            // 允许注释：先剥离，再交给源生成器反序列化。
            text = StripComments(text);

            var config = JsonSerializer.Deserialize(text, AppConfigJsonContext.Default.AppConfig);
            return config ?? new AppConfig();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            LastError = $"读取配置失败，已使用默认值：{exception.Message}";
            return new AppConfig();
        }
    }

    /// <summary>
    /// 写回配置。失败时记录错误但不抛异常 —— 配置写不进去不应影响程序运行。
    /// </summary>
    /// <returns>是否写入成功。</returns>
    public static bool Save(AppConfig config)
    {
        var path = ConfigPath;

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 先写临时文件再替换：避免写入过程中崩溃导致配置损坏。
            var temporary = path + ".tmp";
            using (var stream = File.Create(temporary))
            {
                JsonSerializer.Serialize(stream, config, AppConfigJsonContext.Default.AppConfig);
            }

            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LastError = $"保存配置失败：{exception.Message}";
            return false;
        }
    }

    /// <summary>用资源管理器打开配置文件所在目录并选中它；文件不存在则先落盘一份默认配置。</summary>
    public static void OpenInShell()
    {
        var path = ConfigPath;
        if (!File.Exists(path))
        {
            Save(new AppConfig());
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LastError = $"打开配置目录失败：{exception.Message}";
        }
    }

    /// <summary>
    /// 把默认值序列化成 JSON 文本。
    ///
    /// 用途：让 config/settings.json 这个「配置模板」能由代码生成 ——
    /// 新增配置项后跑一次诊断工具的 <c>config</c> 任务即可同步，
    /// 不必手抄一遍（漏字段是很常见的坑，而且很难发现）。
    /// </summary>
    public static string SerializeDefaults()
        => JsonSerializer.Serialize(new AppConfig(), AppConfigJsonContext.Default.AppConfig);

    /// <summary>
    /// 剥离 JSON 中的 // 与 /* */ 注释，字符串字面量内部的注释符号会被保留。
    /// 用状态机而不是正则，避免误伤 URL 之类的文本。
    /// </summary>
    private static string StripComments(string text)
    {
        var builder = new StringBuilder(text.Length);
        var inString = false;
        var escaped = false;

        for (var i = 0; i < text.Length; i++)
        {
            var current = text[i];

            if (inString)
            {
                builder.Append(current);

                if (escaped)
                {
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (current == '"')
            {
                inString = true;
                builder.Append(current);
                continue;
            }

            if (current == '/' && i + 1 < text.Length)
            {
                var next = text[i + 1];

                if (next == '/')
                {
                    // 行注释：跳到行尾，保留换行以维持行号。
                    i += 2;
                    while (i < text.Length && text[i] != '\n') { i++; }
                    builder.Append('\n');
                    continue;
                }

                if (next == '*')
                {
                    // 块注释：跳到结束标记。
                    i += 2;
                    while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/')) { i++; }
                    i++; // 跳过结束的 '/'
                    continue;
                }
            }

            builder.Append(current);
        }

        return builder.ToString();
    }
}
