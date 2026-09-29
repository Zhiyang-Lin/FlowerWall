namespace FlowerWall.Audio;

/// <summary>
/// 定长 FFT（迭代基 2，就地运算），把实数时域采样变换为幅度谱。
///
/// 为什么自己写：整条音频链路只用到「实数输入 → 幅度谱」这一种变换，
/// 引入 FFT 库的收益不足以抵消依赖成本。这里约 150 行，且位反序表与旋转因子表都只算一次。
///
/// 内存约定：所有工作缓冲在构造时分配，<see cref="Forward"/> 运行期零分配。
/// 线程约定：实例不是线程安全的，但可以安全地从一个线程反复调用（本项目固定在采集线程使用）。
/// </summary>
public sealed class Fft
{
    private readonly int _size;
    private readonly int _levels;
    private readonly int[] _reversal;
    private readonly float[] _cosTable;
    private readonly float[] _sinTable;
    private readonly float[] _imaginary;
    private readonly float[] _real;
    private readonly float[] _window;

    /// <param name="size">窗口大小，必须是 2 的幂。</param>
    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
        {
            throw new ArgumentException($"FFT 窗口大小必须是 2 的幂，实际为 {size}。", nameof(size));
        }

        _size = size;
        _levels = (int)Math.Round(Math.Log2(size));
        _imaginary = new float[size];
        _real = new float[size];
        _window = CreateHannWindow(size);

        _reversal = new int[size];
        for (var i = 0; i < size; i++)
        {
            _reversal[i] = ReverseBits(i, _levels);
        }

        // 旋转因子，角度为 -2πk/N。
        var half = size >> 1;
        _cosTable = new float[half];
        _sinTable = new float[half];
        for (var i = 0; i < half; i++)
        {
            var angle = -2.0 * Math.PI * i / size;
            _cosTable[i] = (float)Math.Cos(angle);
            _sinTable[i] = (float)Math.Sin(angle);
        }
    }

    /// <summary>窗口大小。</summary>
    public int Size => _size;

    /// <summary>非冗余频谱点数（size / 2）。</summary>
    public int BinCount => _size >> 1;

    /// <summary>
    /// 对时域实数采样加汉宁窗后做 FFT，输出各频点幅度。
    /// </summary>
    /// <param name="samples">长度必须等于 <see cref="Size"/> 的时域采样（不会被修改）。</param>
    /// <param name="magnitudes">输出缓冲，长度至少为 <see cref="BinCount"/>。满幅正弦约为 1.0。</param>
    public void Forward(ReadOnlySpan<float> samples, Span<float> magnitudes)
    {
        if (samples.Length != _size)
        {
            throw new ArgumentException($"输入长度必须是 {_size}，实际为 {samples.Length}。", nameof(samples));
        }

        if (magnitudes.Length < BinCount)
        {
            throw new ArgumentException($"输出长度至少为 {BinCount}，实际为 {magnitudes.Length}。", nameof(magnitudes));
        }

        var real = _real;

        // 1) 加窗 + 位反序，直接写入工作缓冲；虚部清零。
        for (var i = 0; i < _size; i++)
        {
            real[_reversal[i]] = samples[i] * _window[i];
        }

        Array.Clear(_imaginary);

        // 2) 蝶形运算（就地复数 FFT）。
        for (var level = 1; level <= _levels; level++)
        {
            var halfSize = 1 << (level - 1);
            var tableStep = _size >> level;

            for (var block = 0; block < _size; block += halfSize << 1)
            {
                for (var k = 0; k < halfSize; k++)
                {
                    var evenIndex = block + k;
                    var oddIndex = evenIndex + halfSize;
                    var tableIndex = k * tableStep;

                    var cos = _cosTable[tableIndex];
                    var sin = _sinTable[tableIndex];

                    var oddReal = real[oddIndex];
                    var oddImag = _imaginary[oddIndex];

                    var rotatedReal = (oddReal * cos) - (oddImag * sin);
                    var rotatedImag = (oddReal * sin) + (oddImag * cos);

                    var evenReal = real[evenIndex];
                    var evenImag = _imaginary[evenIndex];

                    real[evenIndex] = evenReal + rotatedReal;
                    _imaginary[evenIndex] = evenImag + rotatedImag;

                    real[oddIndex] = evenReal - rotatedReal;
                    _imaginary[oddIndex] = evenImag - rotatedImag;
                }
            }
        }

        // 3) 取模。归一化用 2/Σw 而不是 2/N，这样加了汉宁窗之后幅度依旧准确
        //    （等价于补偿窗的相干增益 0.5）。满幅正弦 → 约 1.0。
        var windowSum = 0f;
        for (var i = 0; i < _size; i++)
        {
            windowSum += _window[i];
        }

        var scale = windowSum > 0f ? 2f / windowSum : 2f / _size;
        for (var bin = 0; bin < BinCount; bin++)
        {
            var re = real[bin];
            var im = _imaginary[bin];
            magnitudes[bin] = MathF.Sqrt((re * re) + (im * im)) * scale;
        }
    }

    /// <summary>预计算汉宁窗系数表。内部已使用；公开出来便于调用方复用同一套窗函数。</summary>
    public static float[] CreateHannWindow(int size)
    {
        var window = new float[size];
        for (var i = 0; i < size; i++)
        {
            window[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (size - 1)));
        }

        return window;
    }

    private static int ReverseBits(int value, int bits)
    {
        var result = 0;
        for (var i = 0; i < bits; i++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }

        return result;
    }
}
