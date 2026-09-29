using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using FlowerWall.Interop;

namespace FlowerWall.Ui;

/// <summary>
/// 透明分层窗口的底层封装：负责 DIB 创建、像素搬运和 UpdateLayeredWindow 上屏。
///
/// 为什么不用 WinForms 的 TransparencyKey / SetLayeredWindowAttributes：
///   两者都只能做「全透明色键」，边缘会出现锯齿与黑边。
///   UpdateLayeredWindow + per-pixel alpha 才能让黑胶圆盘的边缘真正平滑。
///
/// 生命周期：<see cref="Update"/> 之前必须先 <see cref="Resize"/>；<see cref="Dispose"/> 释放 GDI 与 DIB 资源。
/// </summary>
internal sealed class LayeredWindowSurface : IDisposable
{
    private IntPtr _memoryDc;
    private IntPtr _dibHandle;
    private IntPtr _dibBits;
    private int _width;
    private int _height;
    private bool _disposed;

    /// <summary>窗口句柄。</summary>
    public IntPtr Handle { get; set; }

    /// <summary>当前位图宽度。未初始化时为 0。</summary>
    public int Width => _width;

    /// <summary>当前位图高度。未初始化时为 0。</summary>
    public int Height => _height;

    /// <summary>
    /// 创建（或重建）指定尺寸的 32 位 DIB 与兼容 DC。
    /// </summary>
    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) { throw new ArgumentOutOfRangeException(nameof(width)); }
        if (width == _width && height == _height && _dibBits != IntPtr.Zero) { return; }

        ReleaseBitmap();

        var bitmapInfo = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height, // 负高度 = 自上而下，与 WPF 位图行序一致
                Planes = 1,
                BitCount = 32,
                Compression = 0, // BI_RGB
            },
        };

        _dibHandle = Gdi32.CreateDIBSection(IntPtr.Zero, ref bitmapInfo, 0, out _dibBits, IntPtr.Zero, 0);
        if (_dibHandle == IntPtr.Zero || _dibBits == IntPtr.Zero)
        {
            throw new InvalidOperationException("创建 32 位 DIB 失败。");
        }

        _memoryDc = Gdi32.CreateCompatibleDC(IntPtr.Zero);
        if (_memoryDc == IntPtr.Zero)
        {
            ReleaseBitmap();
            throw new InvalidOperationException("创建兼容 DC 失败。");
        }

        Gdi32.SelectObject(_memoryDc, _dibHandle);

        _width = width;
        _height = height;
    }

    /// <summary>
    /// 把 WPF 位图的像素拷贝到 DIB 并上屏。
    /// </summary>
    /// <param name="source">Pbgra32 源位图。尺寸必须与 <see cref="Resize"/> 传入的一致。</param>
    /// <param name="destination">窗口在屏幕上的位置（左上角）。</param>
    /// <param name="opacity">整体不透明度 0~255，用于淡入淡出。</param>
    public void Update(BitmapSource source, Point32 destination, byte opacity)
    {
        if (_disposed || _dibBits == IntPtr.Zero || Handle == IntPtr.Zero) { return; }
        if (source.PixelWidth != _width || source.PixelHeight != _height) { return; }

        var stride = source.PixelWidth * 4;
        var byteCount = stride * source.PixelHeight;

        // 直接把像素写入 DIB 内存，避免中间数组分配。
        source.CopyPixels(new Int32Rect(0, 0, source.PixelWidth, source.PixelHeight), _dibBits, byteCount, stride);

        PushToScreen(source.PixelWidth, source.PixelHeight, destination, opacity);
    }

    /// <summary>
    /// 把 GDI+ 位图的像素拷贝到 DIB 并上屏。
    /// 要求位图使用 32bppPArgb（预乘 alpha），否则上屏会出现半透明区域发白。
    /// </summary>
    public void Update(Bitmap source, Point32 destination, byte opacity)
    {
        if (_disposed || _dibBits == IntPtr.Zero || Handle == IntPtr.Zero) { return; }
        if (source.Width != _width || source.Height != _height) { return; }
        if (source.PixelFormat != PixelFormat.Format32bppPArgb && source.PixelFormat != PixelFormat.Format32bppArgb) { return; }

        var data = source.LockBits(
            new Rectangle(0, 0, source.Width, source.Height),
            ImageLockMode.ReadOnly,
            source.PixelFormat);

        try
        {
            var rowBytes = source.Width * 4;

            unsafe
            {
                var target = (byte*)_dibBits;
                var sourcePointer = (byte*)data.Scan0;

                if (data.Stride == rowBytes)
                {
                    Buffer.MemoryCopy(sourcePointer, target, (long)rowBytes * source.Height, (long)rowBytes * source.Height);
                }
                else
                {
                    // 行间有填充时逐行拷贝。
                    for (var row = 0; row < source.Height; row++)
                    {
                        Buffer.MemoryCopy(
                            sourcePointer + (row * data.Stride),
                            target + (row * rowBytes),
                            rowBytes,
                            rowBytes);
                    }
                }
            }
        }
        finally
        {
            source.UnlockBits(data);
        }

        PushToScreen(source.Width, source.Height, destination, opacity);
    }

    private void PushToScreen(int width, int height, Point32 destination, byte opacity)
    {
        var size = new Size32 { Cx = width, Cy = height };
        var sourcePoint = new Point32 { X = 0, Y = 0 };

        // SourceConstantAlpha 控制整体透明度；AC_SRC_ALPHA 启用 per-pixel alpha。
        var blend = new BlendFunction
        {
            BlendOp = NativeMethods.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = opacity,
            AlphaFormat = NativeMethods.AC_SRC_ALPHA,
        };

        // 失败通常是窗口尚未创建或已销毁，属于可恢复情况，静默跳过本帧。
        NativeMethods.UpdateLayeredWindow(Handle, IntPtr.Zero, ref destination, ref size, _memoryDc, ref sourcePoint, 0, ref blend, NativeMethods.ULW_ALPHA);
    }

    public void Dispose()
    {
        if (_disposed) { return; }

        _disposed = true;
        ReleaseBitmap();
    }

    private void ReleaseBitmap()
    {
        if (_memoryDc != IntPtr.Zero)
        {
            Gdi32.DeleteDC(_memoryDc);
            _memoryDc = IntPtr.Zero;
        }

        if (_dibHandle != IntPtr.Zero)
        {
            Gdi32.DeleteObject(_dibHandle);
            _dibHandle = IntPtr.Zero;
        }

        _dibBits = IntPtr.Zero;
        _width = 0;
        _height = 0;
    }

    // ------------------------------------------------------------------ GDI 与 DIB 声明

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RgbQuad
    {
        public byte Blue;
        public byte Green;
        public byte Red;
        public byte Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public RgbQuad Colors;
    }

    private static class Gdi32
    {
        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateDIBSection(IntPtr deviceContext, ref BitmapInfo bitmapInfo, uint usage, out IntPtr bits, IntPtr section, uint offset);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteDC(IntPtr deviceContext);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr gdiObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr gdiObject);
    }
}
