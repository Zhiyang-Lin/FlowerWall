using FlowerWall.Core;

namespace FlowerWall.Audio;

/// <summary>
/// 频谱分析器：从环形缓冲取最新窗口 → 加窗 FFT → 对数频段映射 → 平滑 → 输出 <see cref="SpectrumFrame"/>。
///
/// 纯粹的数学与数据搬运，不接触 COM、不接触窗口，因此可以单独测试。
/// 每次 <see cref="Analyze"/> 调用都保证零托管分配。
/// </summary>
public sealed class SpectrumAnalyzer
{
    private readonly AudioRingBuffer _ring;
    private readonly Fft _fft;
    private readonly int _sampleRate;

    private readonly float[] _magnitudes;
    private readonly float[] _bandMagnitudes;
    private readonly float[] _scratch;

    private readonly int[] _bandLowBin;
    private readonly int[] _bandHighBin;
    private readonly float[] _bandGain;
    private readonly float[] _gainCurve;

    private readonly float _binHz;
    private readonly float _noiseFloorDb;
    private readonly float _ceilingDb;
    private readonly float _gain;
    private readonly float _attack;
    private readonly float _release;
    private readonly float _peakDecayPerSecond;
    private readonly float _silenceThreshold;

    /// <param name="ring">音频来源。</param>
    /// <param name="config">配置。频段数量等参数在构造时固化，改动需要重建实例。</param>
    /// <param name="sampleRate">采集采样率（Hz）。</param>
    public SpectrumAnalyzer(AudioRingBuffer ring, AppConfig config, int sampleRate)
    {
        _ring = ring;
        _sampleRate = Math.Max(8000, sampleRate);

        var spectrum = config.Spectrum;
        var fftSize = spectrum.FftSize;
        if (fftSize < 128 || (fftSize & (fftSize - 1)) != 0)
        {
            // 配置写错时不抛异常中断程序，退回安全值继续运行。
            fftSize = 1024;
        }

        _fft = new Fft(fftSize);
        _magnitudes = new float[_fft.BinCount];
        _bandMagnitudes = new float[Math.Max(1, spectrum.BandCount)];
        _scratch = new float[fftSize];

        _binHz = (float)_sampleRate / fftSize;
        _noiseFloorDb = spectrum.NoiseFloorDb;
        _ceilingDb = Math.Max(spectrum.NoiseFloorDb + 1f, spectrum.CeilingDb);
        _gain = Math.Max(0.01f, spectrum.Gain);
        _attack = Math.Clamp(spectrum.Attack, 0.01f, 1f);
        _release = Math.Clamp(spectrum.Release, 0.01f, 1f);
        _peakDecayPerSecond = Math.Max(0f, spectrum.PeakDecayPerSecond);
        _silenceThreshold = Math.Max(0.0001f, config.Record.SpinStopThreshold);

        var bandCount = Math.Clamp(spectrum.BandCount, 1, _bandMagnitudes.Length);
        _bandLowBin = new int[bandCount];
        _bandHighBin = new int[bandCount];
        _bandGain = new float[bandCount];
        _gainCurve = BuildGainCurve(bandCount, spectrum);

        Frame = new SpectrumFrame(bandCount);
        BuildBandMap(spectrum, bandCount);

        // 初始化为静音，避免第一帧出现虚假的满格。
        Frame.Reset();
        Frame.SetBandCount(bandCount);
    }

    /// <summary>最新一帧的频谱数据（内容随每次 <see cref="Analyze"/> 更新）。</summary>
    public SpectrumFrame Frame { get; }

    /// <summary>采集采样率。</summary>
    public int SampleRate => _sampleRate;

    /// <summary>
    /// 分析缓冲中最新的一个窗口。
    /// </summary>
    /// <param name="deltaSeconds">距上一帧的时间，用于峰值保持的回落。</param>
    public void Analyze(float deltaSeconds)
    {
        if (deltaSeconds <= 0f) { deltaSeconds = 1f / 60f; }

        var count = _ring.CopyLatest(_scratch);

        var peak = 0f;
        var sumOfSquares = 0f;
        for (var i = 0; i < count; i++)
        {
            var value = _scratch[i];
            var magnitude = MathF.Abs(value);
            if (magnitude > peak) { peak = magnitude; }
            sumOfSquares += value * value;
        }

        var rms = count > 0 ? MathF.Sqrt(sumOfSquares / count) : 0f;
        var silent = peak < _silenceThreshold;

        if (count < _fft.Size)
        {
            // 数据不足一个窗口：只更新电平表，频谱保持上一帧并继续回落。
            DecayBands(deltaSeconds);
            Frame.SetMeters(peak, rms, ToDb(rms), Frame.HasSignal, silent);
            return;
        }

        _fft.Forward(_scratch, _magnitudes);

        var bandCount = Frame.BandCount;
        for (var band = 0; band < bandCount; band++)
        {
            var low = _bandLowBin[band];
            var high = _bandHighBin[band];

            var sum = 0f;
            // 低频段的 bin 很少（对数刻度的必然结果），直接用单 bin 会又抖又容易受谱泄漏影响。
            // 因此对低频做轻度扩展平均，但扩展范围不能超过本频段宽度 ——
            // 否则窄频段会「够到」远处的高频能量，造成明显的串扰。
            var binCount = high - low + 1;
            var expand = binCount < 3 ? binCount : 0;
            var from = Math.Max(1, low - expand);
            var to = Math.Min(_fft.BinCount - 1, high + expand);

            for (var bin = from; bin <= to; bin++)
            {
                sum += _magnitudes[bin];
            }

            _bandMagnitudes[band] = sum / (to - from + 1);
        }

        var levels = Frame.Levels;
        var peaks = Frame.Peaks;
        var peakDecay = Math.Clamp(_peakDecayPerSecond * deltaSeconds, 0f, 1f);

        for (var band = 0; band < bandCount; band++)
        {
            var value = Normalize(_bandMagnitudes[band]) * _bandGain[band] * _gain;
            value = Math.Clamp(value, 0f, 1f);

            var previous = levels[band];
            var coefficient = value > previous ? _attack : _release;
            var smoothed = previous + ((value - previous) * coefficient);
            levels[band] = smoothed;

            peaks[band] = smoothed >= peaks[band]
                ? smoothed
                : Math.Max(0f, peaks[band] - peakDecay);
        }

        Frame.SetMeters(peak, rms, ToDb(rms), true, silent);
    }

    /// <summary>把线性幅度映射到 0~1 的显示值：低于门限归零，达到上限满格。</summary>
    private float Normalize(float magnitude)
    {
        var db = ToDb(magnitude);
        if (db <= _noiseFloorDb) { return 0f; }

        var normalized = (db - _noiseFloorDb) / (_ceilingDb - _noiseFloorDb);
        return Math.Clamp(normalized, 0f, 1f);
    }

    private static float ToDb(float linear)
    {
        if (linear <= 1e-6f) { return -100f; }
        return 20f * MathF.Log10(linear);
    }

    /// <summary>没有新数据时让频谱平滑回落，而不是直接跳到 0。</summary>
    private void DecayBands(float deltaSeconds)
    {
        var bandCount = Frame.BandCount;
        var levels = Frame.Levels;
        var peaks = Frame.Peaks;
        var release = Math.Clamp(_release * 0.5f, 0.01f, 1f);
        var peakDecay = Math.Clamp(_peakDecayPerSecond * deltaSeconds, 0f, 1f);

        for (var band = 0; band < bandCount; band++)
        {
            levels[band] = Math.Max(0f, levels[band] * (1f - release));
            peaks[band] = Math.Max(levels[band], peaks[band] - peakDecay);
        }
    }

    /// <summary>
    /// 建立「频段索引 → FFT bin 区间」的映射。
    /// 采用对数刻度（贴近人耳感知），并不留空隙地覆盖 [MinFrequency, MaxFrequency]。
    /// </summary>
    private void BuildBandMap(SpectrumSettings spectrum, int bandCount)
    {
        var minFrequency = Math.Max(20f, spectrum.MinFrequency);
        var maxFrequency = Math.Max(minFrequency * 1.5f, spectrum.MaxFrequency);
        var nyquist = _sampleRate * 0.5f;
        maxFrequency = Math.Min(maxFrequency, nyquist * 0.98f);

        var logMin = MathF.Log(minFrequency);
        var logMax = MathF.Log(maxFrequency);
        var maxBin = _fft.BinCount - 1;

        for (var band = 0; band < bandCount; band++)
        {
            var lower = FrequencyAt(band, bandCount, logMin, logMax);
            var upper = FrequencyAt(band + 1, bandCount, logMin, logMax);

            // ceil/floor 让相邻频段共享边界 bin，避免出现空档导致某些频率永远不显示。
            var lowBin = (int)MathF.Ceiling(lower / _binHz);
            var highBin = (int)MathF.Floor(upper / _binHz);

            lowBin = Math.Clamp(lowBin, 1, maxBin);
            highBin = Math.Clamp(highBin, lowBin, maxBin);

            _bandLowBin[band] = lowBin;
            _bandHighBin[band] = highBin;
            _bandGain[band] = _gainCurve[band];
        }
    }

    private static float FrequencyAt(int band, int bandCount, float logMin, float logMax)
        => MathF.Exp(logMin + ((logMax - logMin) * band / bandCount));

    /// <summary>生成各频段的固定增益曲线：低频抬升、高频收敛，中间平滑过渡，不存在突变。</summary>
    private static float[] BuildGainCurve(int bandCount, SpectrumSettings spectrum)
    {
        var curve = new float[bandCount];
        var low = Math.Max(0f, spectrum.LowGain);
        var mid = Math.Max(0f, spectrum.MidGain);
        var high = Math.Max(0f, spectrum.HighGain);

        for (var band = 0; band < bandCount; band++)
        {
            var position = bandCount > 1 ? (float)band / (bandCount - 1) : 0f;

            curve[band] = position < 0.33f
                ? Lerp(low, mid, position / 0.33f)
                : Lerp(mid, high, (position - 0.33f) / 0.67f);
        }

        return curve;
    }

    private static float Lerp(float from, float to, float amount)
        => from + ((to - from) * Math.Clamp(amount, 0f, 1f));
}
