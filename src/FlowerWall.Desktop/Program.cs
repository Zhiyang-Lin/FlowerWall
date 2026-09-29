using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using FlowerWall.Core;

namespace FlowerWall;

/// <summary>
/// 进程入口。
///
/// 职责只有四件事：单实例、DPI、异常兜底、把控制权交给 <see cref="WallpaperApplication"/>。
/// 具体功能装配一律不写在这里。
/// </summary>
internal static class Program
{
    /// <summary>单实例互斥体名。带 Local\ 前缀表示仅当前登录会话内唯一。</summary>
    private const string SingleInstanceMutexName = @"Local\FlowerWall.SingleInstance";

    /// <summary>设置该环境变量（任意值）可让可视化直接显示，便于开发调试。</summary>
    private const string ForceVisibleVariable = "FLOWERWALL_FORCE_VISIBLE";

    private static Mutex? _singleInstanceMutex;
    private static WallpaperApplication? _application;

    [STAThread]
    private static void Main()
    {
        var config = ConfigStore.Load();

        // 先按配置确定界面语言，之后所有文案（包括下面的报错框）都用它。
        Localization.Use(config.App.Language);

        if (config.App.SingleInstance && !TryAcquireSingleInstance())
        {
            MessageBox.Show(
                Localization.Text(TextKey.SingleInstanceMessage),
                Localization.Text(TextKey.SingleInstanceTitle),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 清单里已声明 PerMonitorV2，这里做一次保险，避免通过其他方式启动时丢 DPI 设置。
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        }
        catch (Exception exception) when (exception is InvalidOperationException or EntryPointNotFoundException)
        {
            // 老系统不支持该 API，忽略即可。
        }

        if (Environment.GetEnvironmentVariable(ForceVisibleVariable) is { Length: > 0 })
        {
            config.App.StartVisible = true;
        }

        // 最后一道防线：无论如何退出，都要把桌面图标恢复回来。
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _application?.Shutdown();
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        Application.ThreadException += OnThreadException;

        try
        {
            _application = new WallpaperApplication(config);
            Application.Run(_application);
        }
        catch (Exception exception)
        {
            ReportFatal(exception);
        }
        finally
        {
            _application?.Shutdown();
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
        }
    }

    private static bool TryAcquireSingleInstance()
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);

        if (!createdNew)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        return createdNew;
    }

    private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
    {
        // UI 线程异常：记下来并恢复桌面图标，但尽量不让程序直接崩掉。
        ReportFatal(e.Exception, recoverable: true);
        _application?.Shutdown();
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            ReportFatal(exception);
        }

        _application?.Shutdown();
    }

    private static void ReportFatal(Exception exception, bool recoverable = false)
    {
        try
        {
            var directory = Path.Combine(AppPaths.RootDirectory, "logs");
            Directory.CreateDirectory(directory);

            var file = Path.Combine(directory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(
                file,
                $"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                $"版本：{typeof(Program).Assembly.GetName().Version}{Environment.NewLine}" +
                $"可恢复：{recoverable}{Environment.NewLine}{Environment.NewLine}" +
                exception.ToString());
        }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
        {
            // 连日志都写不进去时，只能放弃记录，不能让异常处理本身再抛异常。
        }

        if (recoverable) { return; }

        MessageBox.Show(
            exception.Message + Environment.NewLine + Environment.NewLine + "logs",
            Localization.Text(TextKey.FatalErrorTitle),
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
