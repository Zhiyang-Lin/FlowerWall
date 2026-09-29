using System.Runtime.InteropServices;
using FlowerWall.Core;
using FlowerWall.Interop.Audio;

namespace FlowerWall.Audio;

/// <summary>采集引擎状态，供 UI 显示与自动重连判断。</summary>
public enum AudioCaptureState
{
    /// <summary>尚未启动。</summary>
    Stopped,

    /// <summary>正在建立到音频设备的连接。</summary>
    Connecting,

    /// <summary>采集进行中。</summary>
    Running,

    /// <summary>设备不可用（无播放设备 / 被占用），正在按退避策略重试。</summary>
    DeviceUnavailable,
}

/// <summary>
/// WASAPI 回环采集引擎。
///
/// 职责边界：
///   - 在专用后台线程上完成 COM 初始化、设备连接、循环读取；
///   - 把音频混为单声道后写入 <see cref="AudioRingBuffer"/>；
///   - 处理设备切换 / 失效后的自动重连。
/// 不负责频谱计算，也不接触任何 UI。
///
/// 线程模型：MTA。所有 COM 对象都在采集线程上创建与释放，绝不跨线程传递。
/// </summary>
public sealed class AudioCaptureEngine : IDisposable
{
    private const int MaxRetryDelayMs = 5000;
    private const int InitialRetryDelayMs = 1000;
    private const int FallbackPollIntervalMs = 10;

    /// <summary>
    /// 事件等待的超时（毫秒）。
    /// 部分驱动在静音时不会触发事件，超时用于兜底重新检查一次是否有数据。
    /// 取 500ms 而不是更短：静音期间每秒只唤醒约 2 次，CPU 占用可以忽略，
    /// 而有声音时事件会立刻唤醒，延迟不受这个值影响。
    /// </summary>
    private const uint EventWaitTimeoutMs = 500;

    /// <summary>AUDCLNT_BUFFERFLAGS_SILENT：数据指针内容无意义，按静音处理。</summary>
    private const uint BufferFlagSilent = 0x2;

    private readonly AudioRingBuffer _ring;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stopSignal = new(false);

    /// <summary>中转缓冲：原生内存 → 托管内存。容量只增不减，运行期不产生新分配。</summary>
    private float[] _interleaveBuffer = new float[1 << 14];

    private volatile bool _running;
    private volatile int _state = (int)AudioCaptureState.Stopped;
    private volatile int _sampleRate = 48000;
    private volatile int _channels = 2;

    private IntPtr _wakeEvent = IntPtr.Zero;
    private IAudioClient? _audioClient;
    private IAudioCaptureClient? _captureClient;
    private IntPtr _mixFormat = IntPtr.Zero;

    public AudioCaptureEngine(AudioRingBuffer ring)
    {
        _ring = ring;
        _thread = new Thread(CaptureThreadMain)
        {
            Name = "audio-capture",
            IsBackground = true,
            // 略高于普通线程：音频读取晚一拍就会造成频谱抖动。
            Priority = ThreadPriority.AboveNormal,
        };
    }

    /// <summary>当前状态。</summary>
    public AudioCaptureState State => (AudioCaptureState)_state;

    /// <summary>当前采集采样率（未连接时为 48000）。</summary>
    public int SampleRate => _sampleRate;

    /// <summary>当前声道数。</summary>
    public int Channels => _channels;

    /// <summary>启动采集线程。重复调用无副作用。</summary>
    public void Start()
    {
        if (_running) { return; }

        _running = true;
        _stopSignal.Reset();

        try
        {
            _thread.Start();
        }
        catch (ThreadStateException)
        {
            // 线程已启动过，忽略。
        }
    }

    /// <summary>停止采集并等待线程退出。</summary>
    public void Stop()
    {
        if (!_running) { return; }

        _running = false;
        _stopSignal.Set();

        if (_thread.IsAlive && Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        _stopSignal.Dispose();
    }

    // ------------------------------------------------------------------ 采集线程

    private void CaptureThreadMain()
    {
        // 采集线程必须是 MTA，否则跨线程调用会走消息泵，行为不可预期。
        var hr = NativeAudioMethods.CoInitializeEx(IntPtr.Zero, ComInit.Multithreaded);
        var comInitialized = hr >= 0 || hr == ComInit.SFalse || hr == ComInit.RpcEChangedMode;

        try
        {
            var retryDelay = InitialRetryDelayMs;

            while (_running)
            {
                if (!TryBringUp())
                {
                    _state = (int)AudioCaptureState.DeviceUnavailable;
                    TeardownClients();

                    if (!SleepInterruptible(retryDelay)) { break; }
                    retryDelay = Math.Min(MaxRetryDelayMs, retryDelay * 2);
                    continue;
                }

                retryDelay = InitialRetryDelayMs;
                _state = (int)AudioCaptureState.Running;

                var deviceInvalidated = CaptureLoop();

                // 采集循环退出：要么被要求停止，要么设备失效需要重连。
                TeardownClients();

                if (!_running) { break; }

                if (deviceInvalidated)
                {
                    _state = (int)AudioCaptureState.DeviceUnavailable;
                    if (!SleepInterruptible(InitialRetryDelayMs)) { break; }
                }
            }
        }
        finally
        {
            TearDown();
            _state = (int)AudioCaptureState.Stopped;

            if (comInitialized)
            {
                NativeAudioMethods.CoUninitialize();
            }
        }
    }

    /// <summary>读取循环。</summary>
    /// <returns>true 表示设备失效（需要重连），false 表示正常停止。</returns>
    private bool CaptureLoop()
    {
        var captureClient = _captureClient!;
        var evented = _wakeEvent != IntPtr.Zero;

        while (_running)
        {
            if (evented)
            {
                // 事件驱动：有数据时立刻唤醒；无音频时线程完全休眠，CPU 占用为 0。
                var wait = NativeAudioMethods.WaitForSingleObject(_wakeEvent, EventWaitTimeoutMs);

                if (RuntimeStats.IsEnabled)
                {
                    Interlocked.Increment(ref RuntimeStats.CaptureWakeups);
                    if (wait == AudioConstants.WAIT_TIMEOUT)
                    {
                        Interlocked.Increment(ref RuntimeStats.CaptureTimeouts);
                    }
                }

                if (wait != AudioConstants.WAIT_OBJECT_0 && wait != AudioConstants.WAIT_TIMEOUT)
                {
                    return true;
                }
            }
            else
            {
                // 少数驱动不支持事件回调，退化为定时轮询。
                NativeAudioMethods.Sleep(FallbackPollIntervalMs);
            }

            if (!DrainPackets(captureClient)) { return true; }
        }

        return false;
    }

    /// <summary>把当前所有待读的数据包搬进环形缓冲。</summary>
    /// <returns>设备仍然有效返回 true；失效返回 false。</returns>
    private bool DrainPackets(IAudioCaptureClient captureClient)
    {
        var channels = _channels;

        while (true)
        {
            var hr = captureClient.GetNextPacketSize(out var framesAvailable);
            if (hr < 0) { return false; }
            if (framesAvailable == 0) { return true; }

            hr = captureClient.GetBuffer(out var data, out var frames, out var flags, out _, out _);
            if (hr < 0) { return false; }

            if (frames == 0)
            {
                captureClient.ReleaseBuffer(0);
                continue;
            }

            var sampleCount = checked((int)(frames * channels));
            EnsureInterleaveBuffer(sampleCount);

            if ((flags & BufferFlagSilent) != 0 || data == IntPtr.Zero)
            {
                _interleaveBuffer.AsSpan(0, sampleCount).Clear();
            }
            else
            {
                Marshal.Copy(data, _interleaveBuffer, 0, sampleCount);
            }

            _ring.Write(_interleaveBuffer.AsSpan(0, sampleCount), channels);

            if (RuntimeStats.IsEnabled)
            {
                Interlocked.Increment(ref RuntimeStats.CapturePackets);
            }

            captureClient.ReleaseBuffer(frames);
        }
    }

    // ------------------------------------------------------------------ 设备连接

    private bool TryBringUp()
    {
        _state = (int)AudioCaptureState.Connecting;

        try
        {
            // 通过 CLSID 实例化 MMDeviceEnumerator 并按接口取用：
            // 直接对 ComImport 类做接口转换在编译期不被允许，这样写既简单又符合 COM 惯例。
            var clsid = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
            if (clsid is null) { return false; }

            if (Activator.CreateInstance(clsid) is not IMMDeviceEnumerator enumerator) { return false; }

            var hr = enumerator.GetDefaultAudioEndpoint(AudioConstants.EDataFlowRender, AudioConstants.ERoleMultimedia, out var device);
            if (hr < 0 || device is null) { return false; }

            var audioClientId = typeof(IAudioClient).GUID;
            hr = device.Activate(ref audioClientId, ClsCtx.All, IntPtr.Zero, out var clientObject);
            if (hr < 0 || clientObject is not IAudioClient audioClient) { return false; }

            _audioClient = audioClient;

            // 回环采集使用渲染端点的混音格式。
            hr = audioClient.GetMixFormat(out _mixFormat);
            if (hr < 0 || _mixFormat == IntPtr.Zero) { return false; }

            if (!TryReadFormat(_mixFormat, out var sampleRate, out var channels)) { return false; }

            _sampleRate = sampleRate;
            _channels = channels;

            _wakeEvent = NativeAudioMethods.CreateEventW(IntPtr.Zero, false, false, IntPtr.Zero);

            var flags = AudioConstants.AUDCLNT_STREAMFLAGS_LOOPBACK | AudioConstants.AUDCLNT_STREAMFLAGS_NOPERSIST;
            var evented = _wakeEvent != IntPtr.Zero;
            if (evented) { flags |= AudioConstants.AUDCLNT_STREAMFLAGS_EVENTCALLBACK; }

            // 回环采集必须使用共享模式；缓冲时长传 0 表示由音频引擎决定。
            hr = audioClient.Initialize(AudioConstants.AUDCLNT_SHAREMODE_SHARED, flags, 0, 0, _mixFormat, IntPtr.Zero);

            if (hr < 0 && evented)
            {
                // 驱动不支持事件回调，退化为定时轮询后重试一次。
                evented = false;
                NativeAudioMethods.CloseHandle(_wakeEvent);
                _wakeEvent = IntPtr.Zero;

                flags = AudioConstants.AUDCLNT_STREAMFLAGS_LOOPBACK | AudioConstants.AUDCLNT_STREAMFLAGS_NOPERSIST;
                hr = audioClient.Initialize(AudioConstants.AUDCLNT_SHAREMODE_SHARED, flags, 0, 0, _mixFormat, IntPtr.Zero);
            }

            if (hr < 0) { return false; }

            if (evented && audioClient.SetEventHandle(_wakeEvent) < 0)
            {
                evented = false;
                NativeAudioMethods.CloseHandle(_wakeEvent);
                _wakeEvent = IntPtr.Zero;
            }

            hr = audioClient.GetBufferSize(out var bufferFrames);
            if (hr < 0 || bufferFrames == 0) { return false; }

            EnsureInterleaveBuffer(checked((int)bufferFrames * channels));

            var captureId = typeof(IAudioCaptureClient).GUID;
            hr = audioClient.GetService(ref captureId, out var captureObject);
            if (hr < 0 || captureObject is not IAudioCaptureClient captureClient) { return false; }

            _captureClient = captureClient;

            if (audioClient.Start() < 0) { return false; }

            return true;
        }
        catch (Exception)
        {
            // COM 激活失败、设备被拔出等情况：交给外层退避重试，不让后台线程崩掉。
            return false;
        }
    }

    /// <summary>
    /// 读取混音格式的采样率与声道数，并确认采样位深受支持。
    /// </summary>
    /// <remarks>
    /// 需要注意 WAVEFORMATEXTENSIBLE：它的 FormatTag 固定是 0xFFFE，
    /// 真正的采样类型在 SubFormat GUID 里。只看 FormatTag 会把所有环绕声 / 多声道格式误判掉。
    /// </remarks>
    private static bool TryReadFormat(IntPtr format, out int sampleRate, out int channels)
    {
        sampleRate = 0;
        channels = 0;

        var waveFormat = Marshal.PtrToStructure<WaveFormatEx>(format);
        if (waveFormat.Channels == 0 || waveFormat.SamplesPerSec == 0) { return false; }

        var isFloat = waveFormat.IsFloat;

        if (waveFormat.FormatTag == AudioConstants.WAVE_FORMAT_EXTENSIBLE && waveFormat.ExtraSize >= 22)
        {
            var extensible = Marshal.PtrToStructure<WaveFormatExtensible>(format);
            isFloat = extensible.SubFormat == AudioConstants.SubFormatIeeeFloat;
        }

        // 只接受 32 位浮点：采集缓冲按 float 解释，其他格式必须先转换，否则会写出垃圾数据。
        // Windows 的回环混音格式始终是 32 位浮点，因此这里不牺牲兼容性。
        if (!isFloat || waveFormat.BitsPerSample != 32)
        {
            return false;
        }

        sampleRate = (int)waveFormat.SamplesPerSec;
        channels = waveFormat.Channels;
        return true;
    }

    /// <summary>
    /// 确保托管中转缓冲至少能容纳 <paramref name="sampleCount"/> 个采样。
    /// 这个缓冲只用于「原生内存 → 托管内存」的中转，之后立刻写进环形缓冲，不长期持有数据。
    /// </summary>
    private void EnsureInterleaveBuffer(int sampleCount)
    {
        if (_interleaveBuffer.Length >= sampleCount) { return; }

        // 预留余量：驱动偶尔会给出比 GetBufferSize 更大的数据包。
        var size = 1;
        while (size < sampleCount) { size <<= 1; }
        _interleaveBuffer = new float[size];
    }

    private void TeardownClients()
    {
        try
        {
            _audioClient?.Stop();
        }
        catch (Exception)
        {
            // 设备已失效时 Stop 可能失败，忽略。
        }

        if (_captureClient is not null && Marshal.IsComObject(_captureClient))
        {
            Marshal.ReleaseComObject(_captureClient);
        }

        _captureClient = null;

        if (_audioClient is not null && Marshal.IsComObject(_audioClient))
        {
            Marshal.ReleaseComObject(_audioClient);
        }

        _audioClient = null;

        if (_mixFormat != IntPtr.Zero)
        {
            NativeAudioMethods.CoTaskMemFree(_mixFormat);
            _mixFormat = IntPtr.Zero;
        }

        if (_wakeEvent != IntPtr.Zero)
        {
            NativeAudioMethods.CloseHandle(_wakeEvent);
            _wakeEvent = IntPtr.Zero;
        }

        // 设备切换后旧数据没有意义，清空避免误显示。
        _ring.Clear();
    }

    private void TearDown() => TeardownClients();

    /// <summary>可被打断的等待。<returns>true 表示应继续运行，false 表示已请求停止。</returns>
    private bool SleepInterruptible(int milliseconds) => !_stopSignal.Wait(milliseconds);
}
