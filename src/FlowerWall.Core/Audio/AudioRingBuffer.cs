namespace FlowerWall.Audio;

/// <summary>
/// 单生产者 / 单消费者音频环形缓冲。
///
/// 写入方是采集线程，读取方是渲染帧循环（UI 线程）。读取永远只取「最新的 N 个采样」，
/// 因此不需要处理覆盖竞态：读到旧数据最多造成一帧的轻微偏差，不会越界、不会崩溃。
///
/// 全部存储预分配，运行期零 GC 分配 —— 这是保证长跑不抖动的前提。
/// </summary>
public sealed class AudioRingBuffer
{
    private readonly float[] _samples;
    private readonly int _capacity;
    private readonly int _capacityMask;

    /// <summary>已写入的采样总数（单调递增的 64 位计数）。</summary>
    private long _written;

    /// <param name="capacity">容量，会被向上取整到 2 的幂。</param>
    public AudioRingBuffer(int capacity)
    {
        var size = 1;
        while (size < capacity) { size <<= 1; }

        _capacity = size;
        _capacityMask = size - 1;
        _samples = new float[size];
    }

    /// <summary>实际容量（2 的幂）。</summary>
    public int Capacity => _capacity;

    /// <summary>写入的采样总数。</summary>
    public long TotalWritten => Interlocked.Read(ref _written);

    /// <summary>写入交错采样。仅允许采集线程调用。</summary>
    public void Write(ReadOnlySpan<float> interleaved, int channels)
    {
        if (channels <= 0 || interleaved.Length < channels) { return; }

        var frames = interleaved.Length / channels;
        var writeIndex = (int)(_written & _capacityMask);

        if (channels == 1)
        {
            for (var i = 0; i < frames; i++)
            {
                _samples[writeIndex] = interleaved[i];
                writeIndex = (writeIndex + 1) & _capacityMask;
            }
        }
        else
        {
            // 多声道混为单声道：取算术平均，避免只取左声道导致的偏听。
            var scale = 1f / channels;
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                var offset = frame * channels;
                for (var channel = 0; channel < channels; channel++)
                {
                    sum += interleaved[offset + channel];
                }

                _samples[writeIndex] = sum * scale;
                writeIndex = (writeIndex + 1) & _capacityMask;
            }
        }

        Interlocked.Add(ref _written, frames);
    }

    /// <summary>丢弃历史数据（设备切换后调用，避免把旧音频当作新数据）。</summary>
    public void Clear()
    {
        Array.Clear(_samples);
        Interlocked.Exchange(ref _written, 0);
    }

    /// <summary>
    /// 把「最近 <paramref name="count"/> 个采样」拷贝到 <paramref name="destination"/>。
    /// 数据不足时在开头补零；缓冲已被完全覆盖时自动退化为可用范围内的最新数据。
    /// </summary>
    /// <returns>实际从环形缓冲拷贝的采样数。</returns>
    public int CopyLatest(Span<float> destination)
    {
        var count = destination.Length;
        if (count == 0) { return 0; }

        var written = Interlocked.Read(ref _written);
        var available = (int)Math.Min(written, _capacity);

        if (available < count)
        {
            destination[..count].Clear();
            if (available == 0) { return 0; }

            CopyRange(written - available, available, destination[(count - available)..]);
            return available;
        }

        CopyRange(written - count, count, destination);
        return count;
    }

    private void CopyRange(long startSequence, int count, Span<float> destination)
    {
        for (var i = 0; i < count; i++)
        {
            destination[i] = _samples[(int)((startSequence + i) & _capacityMask)];
        }
    }
}
