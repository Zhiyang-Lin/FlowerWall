using System.Diagnostics;
using System.IO;
using System.Text;

namespace FlowerWall.Core;

/// <summary>
/// 轻量运行期统计，只在设置了 <c>FLOWERWALL_STATS=1</c> 时启用。
///
/// 为什么需要它：这是个没有控制台的 GUI 程序，而「空闲时 CPU 为什么不为 0」
/// 这类问题必须靠真实运行数据才能定位。启用后每秒把一行统计写入
/// <c>logs\stats.log</c>：帧数、实际帧率、采集线程唤醒次数、音频设备状态等。
///
/// 关闭时开销为零（只有一个 bool 判断），因此可以长期留在代码里。
/// </summary>
public static class RuntimeStats
{
    /// <summary>启用统计的环境变量名。</summary>
    public const string EnvironmentVariable = "FLOWERWALL_STATS";

    private static readonly bool Enabled = IsEnabledByEnvironment();
    private static readonly object Gate = new();

    private static string? _logPath;

    /// <summary>统计是否启用。</summary>
    public static bool IsEnabled => Enabled;

    /// <summary>采集线程被唤醒的次数。</summary>
    public static long CaptureWakeups;

    /// <summary>采集线程实际读到音频包的次数。</summary>
    public static long CapturePackets;

    /// <summary>等待事件返回 WAIT_TIMEOUT 的次数（用于判断事件是否真的在工作）。</summary>
    public static long CaptureTimeouts;

    /// <summary>记录一行统计。未启用时是空操作。</summary>
    public static void WriteLine(string message)
    {
        if (!Enabled) { return; }

        try
        {
            lock (Gate)
            {
                _logPath ??= PrepareLogPath();
                File.AppendAllText(_logPath, message + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 统计失败绝不能影响主流程。
        }
    }

    /// <summary>记录一行带时间戳的统计。</summary>
    public static void WriteTimestamped(string message)
        => WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");

    private static bool IsEnabledByEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return !string.IsNullOrEmpty(value) && value != "0";
    }

    private static string PrepareLogPath()
    {
        var directory = Path.Combine(AppPaths.RootDirectory, "logs");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "stats.log");
    }

    /// <summary>当前进程的 CPU 时间（秒），供统计输出使用。</summary>
    public static double ProcessCpuSeconds
    {
        get
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                return process.TotalProcessorTime.TotalSeconds;
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
            {
                return 0;
            }
        }
    }
}
