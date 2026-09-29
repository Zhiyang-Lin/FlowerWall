using System.Runtime.InteropServices;

namespace FlowerWall.Interop.Audio;

/// <summary>
/// WASAPI 常量。
/// 参考：https://learn.microsoft.com/windows/win32/coreaudio/audclnt-streamflags-xxx-constants
/// </summary>
internal static class AudioConstants
{
    // --- EDataFlow ---
    public const int EDataFlowRender = 0;
    public const int EDataFlowCapture = 1;
    public const int EDataFlowAll = 2;

    // --- ERole ---
    public const int ERoleConsole = 0;
    public const int ERoleMultimedia = 1;
    public const int ERoleCommunications = 2;

    // --- AUDCLNT_SHAREMODE ---
    public const int AUDCLNT_SHAREMODE_SHARED = 0;
    public const int AUDCLNT_SHAREMODE_EXCLUSIVE = 1;

    // --- AUDCLNT_STREAMFLAGS_xxx ---
    public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    public const uint AUDCLNT_STREAMFLAGS_NOPERSIST = 0x00080000;

    // --- 设备状态 ---
    public const int DEVICE_STATE_ACTIVE = 0x00000001;

    // --- WAVE_FORMAT ---
    public const ushort WAVE_FORMAT_PCM = 0x0001;
    public const ushort WAVE_FORMAT_IEEE_FLOAT = 0x0003;
    public const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

    // --- HRESULT / 等待返回值 ---
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    public const int AUDCLNT_E_SERVICE_NOT_RUNNING = unchecked((int)0x88890010);

    public const int WAIT_OBJECT_0 = 0;
    public const uint WAIT_TIMEOUT = 0x00000102;

    /// <summary>KSDATAFORMAT_SUBTYPE_IEEE_FLOAT：WAVEFORMATEXTENSIBLE 中的浮点标识。</summary>
    public static readonly Guid SubFormatIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");

    /// <summary>KSDATAFORMAT_SUBTYPE_PCM：WAVEFORMATEXTENSIBLE 中的整形 PCM 标识。</summary>
    public static readonly Guid SubFormatPcm = new("00000001-0000-0010-8000-00AA00389B71");
}

/// <summary>WAVEFORMATEX 结构。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormatEx
{
    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSec;
    public uint AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort ExtraSize;

    public readonly bool IsFloat => FormatTag == AudioConstants.WAVE_FORMAT_IEEE_FLOAT;

    public readonly bool IsPcm => FormatTag == AudioConstants.WAVE_FORMAT_PCM;
}

/// <summary>
/// WAVEFORMATEXTENSIBLE 结构。
/// 前 18 字节与 WAVEFORMATEX 完全一致，因此可以直接从同一个指针读出来。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormatExtensible
{
    public WaveFormatEx Format;
    public ushort ValidBitsPerSample;
    public uint ChannelMask;
    public Guid SubFormat;
}

[Flags]
internal enum ClsCtx : uint
{
    InprocServer = 0x1,
    InprocHandler = 0x2,
    LocalServer = 0x4,
    RemoteServer = 0x10,
    All = InprocServer | InprocHandler | LocalServer | RemoteServer,
}

/// <summary>IMMDeviceEnumerator：枚举音频端点。</summary>
[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IntPtr client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IntPtr client);
}

/// <summary>IMMDevice：单个音频端点。</summary>
[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid interfaceId, ClsCtx classContext, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);

    [PreserveSig]
    int OpenPropertyStore(int access, out IntPtr properties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

    [PreserveSig]
    int GetState(out int state);
}

/// <summary>
/// IAudioClient：核心音频客户端。
///
/// 方法顺序必须与 Windows SDK 的 audioclient.h 中虚表顺序完全一致 —— 顺序错误会直接崩溃。
/// 这里只声明了本项目用得到的几个方法；为保证虚表槽位正确，
/// 位置在它们之前的无关方法也一并按顺序声明。
/// </summary>
[ComImport]
[Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig]
    int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);

    [PreserveSig]
    int GetBufferSize(out uint bufferFrameCount);

    [PreserveSig]
    int GetStreamLatency(out long latency);

    [PreserveSig]
    int GetCurrentPadding(out uint paddingFrameCount);

    [PreserveSig]
    int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);

    [PreserveSig]
    int GetMixFormat(out IntPtr deviceFormat);

    [PreserveSig]
    int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);

    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int SetEventHandle(IntPtr eventHandle);

    [PreserveSig]
    int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

/// <summary>IAudioCaptureClient：读取采集到的音频包。</summary>
[ComImport]
[Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(out IntPtr data, out uint frameCount, out uint flags, out ulong devicePosition, out ulong qpcPosition);

    [PreserveSig]
    int ReleaseBuffer(uint frameCount);

    [PreserveSig]
    int GetNextPacketSize(out uint frameCount);
}

/// <summary>核心音频相关的原生方法声明。</summary>
internal static class NativeAudioMethods
{
    [DllImport("ole32.dll")]
    public static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    public static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, IntPtr name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    public static extern void Sleep(uint milliseconds);
}

/// <summary>COM 初始化常量。</summary>
internal static class ComInit
{
    /// <summary>COINIT_MULTITHREADED：采集线程必须用 MTA，否则跨线程调用会走消息泵。</summary>
    public const uint Multithreaded = 0x0;

    public const int RpcEChangedMode = unchecked((int)0x80010106);
    public const int SFalse = 1;
}
