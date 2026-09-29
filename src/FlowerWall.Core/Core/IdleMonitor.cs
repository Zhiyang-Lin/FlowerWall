using FlowerWall.Interop;

namespace FlowerWall.Core;

/// <summary>
/// 键鼠空闲检测。
///
/// 实现选择：轮询 <c>GetLastInputInfo</c> 而不是安装全局键鼠钩子。
/// 理由：全局钩子会影响其他程序、容易被安全软件拦截，而本项目只需要「大致空闲了多久」，
/// 轮询 500ms 的成本可以忽略（一次系统调用，不分配内存）。
///
/// 休眠 / 待机处理：<c>GetLastInputInfo</c> 返回的是 tick 计数，休眠期间不推进，
/// 而 <see cref="Environment.TickCount64"/> 会推进，因此两者之差天然反映「真实离开时长」，
/// 无需额外监听电源事件。
/// </summary>
public sealed class IdleMonitor
{
    /// <summary>
    /// 覆盖空闲时长的环境变量（秒）。仅供诊断工具做确定性测试使用：
    /// 真实空闲时长依赖系统输入状态，无法在无输入设备的会话里构造。
    /// 设置后 <see cref="IdleSeconds"/> 直接返回该值。
    /// </summary>
    public const string FakeIdleVariable = "FLOWERWALL_FAKE_IDLE_SECONDS";

    /// <summary>
    /// 禁用空闲检测的环境变量（设为 1 即生效）。
    /// 用途：在自动化测试与性能测量中固定「不激活」这一侧的状态，
    /// 否则无输入设备的会话会因为系统读数而持续判定为空闲。
    /// </summary>
    public const string DisableIdleVariable = "FLOWERWALL_DISABLE_IDLE";

    /// <summary>
    /// 可信的空闲时长上限（秒）。
    ///
    /// 为什么需要：无人值守的会话、没有输入设备的虚拟机里，
    /// <c>GetLastInputInfo</c> 会回报一个离谱的 tick 差值（实测见过 1800 秒以上）。
    /// 那种读数不代表「用户离开了」，因此一律按 0 处理，宁可漏触发也不能误触发。
    /// 取 24 小时 —— 依赖空闲自动出现壁纸的场景，一天没人碰电脑已属极端。
    /// </summary>
    private const double ImplausibleIdleSeconds = 24 * 60 * 60;

    private readonly int _pollIntervalMs;

    private long _lastPollTimestamp;
    private double _idleSeconds;

    public IdleMonitor(int pollIntervalMs)
    {
        _pollIntervalMs = Math.Clamp(pollIntervalMs, 50, 5000);
    }

    /// <summary>轮询间隔（毫秒）。</summary>
    public int PollIntervalMs => _pollIntervalMs;

    /// <summary>上次输入距现在经过的秒数。未成功读取或读数不可信时为 0。</summary>
    public double IdleSeconds => _idleSeconds;

    /// <summary>距上次轮询是否已经超过轮询间隔。</summary>
    public bool ShouldPoll(long nowTimestampMs)
        => nowTimestampMs - _lastPollTimestamp >= _pollIntervalMs;

    /// <summary>
    /// 刷新空闲时长。应由 UI 定时器按 <see cref="ShouldPoll"/> 的节奏调用。
    /// </summary>
    /// <returns>是否成功读取到空闲时长。</returns>
    public bool Refresh(long nowTimestampMs)
    {
        _lastPollTimestamp = nowTimestampMs;

        // 自动化测量时可强制关闭空闲检测。
        var disabled = Environment.GetEnvironmentVariable(DisableIdleVariable);
        if (!string.IsNullOrEmpty(disabled) && disabled != "0")
        {
            _idleSeconds = 0;
            return true;
        }

        // 诊断工具通过这个环境变量注入确定的空闲时长。
        var fake = Environment.GetEnvironmentVariable(FakeIdleVariable);
        if (!string.IsNullOrEmpty(fake) && double.TryParse(fake, out var fakeSeconds))
        {
            _idleSeconds = Math.Max(0, fakeSeconds);
            return true;
        }

        var info = new LastInputInfo { Size = NativeMethods.LastInputInfoSize };
        if (!NativeMethods.GetLastInputInfo(ref info))
        {
            return false;
        }

        var tick = unchecked((uint)nowTimestampMs);
        var elapsed = tick - info.Time;

        // tick 计数约 49.7 天回绕一次，回绕后差值会变成极大值，这里做一次修正。
        if (elapsed > NativeMethods.TickWrapThresholdMs)
        {
            elapsed = 0;
        }

        var seconds = elapsed / 1000.0;

        // 防御性判断：这个会话可能根本没有输入设备（无人值守 / 无键鼠的虚拟机），
        // 此时 GetLastInputInfo 会回报一个巨大的值，绝不能据此判定「用户离开了」。
        if (seconds > ImplausibleIdleSeconds)
        {
            seconds = 0;
        }

        _idleSeconds = seconds;
        return true;
    }

    /// <summary>强制把空闲时长清零（例如手动唤醒时）。</summary>
    public void Reset() => _idleSeconds = 0;
}
