using System.IO;

namespace FlowerWall.Core;

/// <summary>
/// 路径解析。
///
/// 约定：配置与日志放在**可执行文件所在目录**，而不是 AppData。
/// 这样整个项目是「绿色」的：拷贝目录即可迁移，卸载只需删目录。
/// </summary>
public static class AppPaths
{
    private static string? _root;

    /// <summary>项目根目录（exe 所在目录）。</summary>
    public static string RootDirectory
    {
        get
        {
            if (_root is not null) { return _root; }

            var baseDirectory = AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(baseDirectory) && Directory.Exists(baseDirectory))
            {
                _root = baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return _root;
            }

            var processPath = Environment.ProcessPath;
            _root = string.IsNullOrEmpty(processPath)
                ? Directory.GetCurrentDirectory()
                : Path.GetDirectoryName(processPath) ?? Directory.GetCurrentDirectory();

            return _root;
        }
    }

    /// <summary>把相对路径解析为基于 <see cref="RootDirectory"/> 的绝对路径。</summary>
    public static string Resolve(string relativePath)
        => Path.Combine(RootDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// 仓库根目录（含 FlowerWall.sln 的那一层）。
    ///
    /// 用途：诊断工具要把预览图写到仓库根目录，而运行目录是 bin\&lt;Config&gt;\&lt;tfm&gt;\，
    /// 靠固定层数的 .. 很脆弱（换个配置名或 TFM 就错位）。这里改为向上查找解决方案文件。
    /// 找不到时退回 <see cref="RootDirectory"/>。
    /// </summary>
    public static string RepositoryRootDirectory
    {
        get
        {
            const string marker = "FlowerWall.sln";

            var directory = new DirectoryInfo(RootDirectory);

            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, marker)))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            return RootDirectory;
        }
    }
}
