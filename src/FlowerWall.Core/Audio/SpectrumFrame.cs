namespace FlowerWall.Audio;

/// <summary>
/// 声波条的增益分区：频率落在 [StartFraction, EndFraction) 区间内的频段乘上对应增益。
/// 用来补偿「低频能量天然比高频大」带来的视觉失衡。当前由配置里的三段增益实现，
/// 保留此类型便于后续改成任意多段。
/// </summary>
internal readonly record struct SpectrumBand(float StartFraction, float EndFraction, float Gain);

/// <summary>
/// 一帧可供绘制的频谱数据。
///
/// 设计要点：数组在构造时按最大规模分配，运行期只改内容不换数组 ——
/// 渲染线程每帧读取它，全程零分配，避免 GC 抖动。
/// 这个类型不持有窗口或音频句柄，是纯粹的「数据快照」。
/// </summary>
public sealed class SpectrumFrame
{
    /// <summary>本帧有效的声波条数量。</summary>
    public int BandCount { get; private set; }

    /// <summary>当前值（0~1）。</summary>
    public float[] Levels { get; }

    /// <summary>峰值保持（0~1）。</summary>
    public float[] Peaks { get; }

    /// <summary>本帧取样窗口的峰值绝对值（0~1）。</summary>
    public float Peak { get; private set; }

    /// <summary>本帧取样窗口的均方根（0~1）。</summary>
    public float Rms { get; private set; }

    /// <summary>均方根对应的 dBFS，最小 -100。</summary>
    public float RmsDb { get; private set; }

    /// <summary>是否已经收到过有效音频数据。</summary>
    public bool HasSignal { get; private set; }

    /// <summary>自上次有信号以来经过的帧数，用于静音省电判断。</summary>
    public int SilentFrames { get; private set; }

    public SpectrumFrame(int maxBands)
    {
        Levels = new float[Math.Max(1, maxBands)];
        Peaks = new float[Math.Max(1, maxBands)];
        BandCount = Levels.Length;
    }

    /// <summary>由分析器在每帧末尾调用。外部不要调用。</summary>
    internal void SetBandCount(int bandCount)
    {
        BandCount = Math.Clamp(bandCount, 0, Levels.Length);
    }

    /// <summary>由分析器在每帧末尾调用。外部不要调用。</summary>
    internal void SetMeters(float peak, float rms, float rmsDb, bool hasSignal, bool silent)
    {
        Peak = peak;
        Rms = rms;
        RmsDb = rmsDb;
        HasSignal = hasSignal;
        SilentFrames = silent ? SilentFrames + 1 : 0;
    }

    /// <summary>
    /// 重置为静音状态（设备切换或长时间无信号时调用）。
    /// 只清空数值与电平表，不改变 <see cref="BandCount"/> —— 频段数量是缓冲布局，不是数据。
    /// </summary>
    public void Reset()
    {
        Array.Clear(Levels);
        Array.Clear(Peaks);
        Peak = 0f;
        Rms = 0f;
        RmsDb = -100f;
        HasSignal = false;
        SilentFrames = 0;
    }
}
