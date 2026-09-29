using System.Runtime.InteropServices;

namespace FlowerWall.Interop;

/// <summary>POINT。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Point32
{
    public int X;
    public int Y;
}

/// <summary>SIZE。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Size32
{
    public int Cx;
    public int Cy;
}

/// <summary>BLENDFUNCTION，UpdateLayeredWindow 使用。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct BlendFunction
{
    public byte BlendOp;
    public byte BlendFlags;
    public byte SourceConstantAlpha;
    public byte AlphaFormat;
}

/// <summary>LASTINPUTINFO，空闲检测使用。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct LastInputInfo
{
    public uint Size;
    public uint Time;
}

/// <summary>
/// Win32 声明集合。
///
/// 约定：这个文件只放 P/Invoke 声明和常量，不写任何业务逻辑。
/// 业务判断放在 Interop/ 下对应的宿主类里（例如 <see cref="DesktopIconHost"/>）。
///
/// 可见性说明：这里有少量声明是 public 的，因为桌面程序需要在窗口过程里直接使用
/// （窗口样式常量、SetWindowPos、UpdateLayeredWindow 及其 BLENDFUNCTION）。
/// 其余（DIB、DWM、空闲检测）保持 internal，只通过 Core 内部的封装类使用。
///
/// 维护要求：只放**真正用到**的声明。未使用的 P/Invoke 不占运行期开销，但会让
/// "这里到底用了哪些系统能力"变得难以判断，也让裁剪分析失去意义。
/// </summary>
public static class NativeMethods
{
    // ------------------------------------------------------------------ 窗口消息

    public const int WM_NCHITTEST = 0x0084;
    public const int WM_MOUSEACTIVATE = 0x0021;

    public const int HTTRANSPARENT = -1;
    public const int MA_NOACTIVATE = 3;

    // ------------------------------------------------------------------ 窗口样式

    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    // ------------------------------------------------------------------ SetWindowPos

    public static readonly IntPtr HWND_BOTTOM = new(1);
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;

    // ------------------------------------------------------------------ ShowWindow

    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;

    // ------------------------------------------------------------------ Layered window

    public const byte AC_SRC_OVER = 0x00;
    public const byte AC_SRC_ALPHA = 0x01;
    public const int ULW_ALPHA = 0x00000002;

    // ------------------------------------------------------------------ DWM

    public const int DWMWA_CLOAK = 14;

    // ------------------------------------------------------------------ 空闲检测

    /// <summary>GetLastInputInfo 要求的结构大小（8 字节：uint + uint）。</summary>
    public const uint LastInputInfoSize = 8;

    /// <summary>tick 计数回绕阈值：回绕后差值会超过它。</summary>
    public const uint TickWrapThresholdMs = 0x80000000;

    // ------------------------------------------------------------------ P/Invoke

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr FindWindowW([MarshalAs(UnmanagedType.LPWStr)] string? className, [MarshalAs(UnmanagedType.LPWStr)] string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr childAfter, [MarshalAs(UnmanagedType.LPWStr)] string? className, [MarshalAs(UnmanagedType.LPWStr)] string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    public delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateLayeredWindow(
        IntPtr window,
        IntPtr destinationDc,
        ref Point32 destinationPoint,
        ref Size32 size,
        IntPtr sourceDc,
        ref Point32 sourcePoint,
        int colorKey,
        ref BlendFunction blend,
        int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
