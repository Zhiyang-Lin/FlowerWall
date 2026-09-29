namespace FlowerWall.Core;

/// <summary>交互控制器对外暴露的状态快照，供托盘菜单与调试输出使用。</summary>
public readonly record struct InteractionStatus(bool Active, bool Manual, double IdleSeconds, float Opacity);

/// <summary>
/// 交互状态机：决定「可视化现在应该显示还是隐藏」。
///
/// 这是本项目唯一做该决策的地方 —— 窗口、按钮、托盘都只读取它的结果，不自行判断。
/// 好处是「唤醒」相关的行为规则集中在一处，后续加新触发源（例如全屏检测、开机自启）只需改这里。
///
/// 状态迁移：
///   空闲达到阈值              → 自动激活
///   手动开关                  → 切换手动激活
///   用户恢复操作 + 未手动激活  → 自动关闭
///   关闭时                    → 淡出后再真正隐藏
/// </summary>
public sealed class InteractionController
{
    private readonly AppConfig _config;
    private readonly IdleMonitor _idleMonitor;

    private bool _manualActive;
    private bool _idleActive;
    private float _opacity;
    private bool _closed;

    public InteractionController(AppConfig config)
    {
        _config = config;
        _idleMonitor = new IdleMonitor(config.Idle.PollIntervalMs);
    }

    /// <summary>当前不透明度（0~1）。</summary>
    public float Opacity => _opacity;

    /// <summary>可视化是否处于激活状态（正在显示或正在淡出）。</summary>
    public bool IsActive => _manualActive || _idleActive;

    /// <summary>是否由用户手动打开（手动打开时不会因恢复操作而自动关闭）。</summary>
    public bool IsManuallyActive => _manualActive;

    /// <summary>当前状态快照。</summary>
    public InteractionStatus Snapshot => new(IsActive, _manualActive, _idleMonitor.IdleSeconds, _opacity);

    /// <summary>空闲检测的轮询间隔（毫秒）。</summary>
    public int IdlePollIntervalMs => _idleMonitor.PollIntervalMs;

    /// <summary>切换手动激活。返回切换后的状态。</summary>
    public bool ToggleManual()
    {
        _manualActive = !_manualActive;
        return _manualActive;
    }

    /// <summary>显式设置手动激活状态。</summary>
    public void SetManual(bool active) => _manualActive = active;

    /// <summary>
    /// 推进一帧。
    /// </summary>
    /// <param name="nowTimestampMs">单调递增的时间戳（毫秒），通常取 Environment.TickCount64。</param>
    /// <param name="deltaSeconds">距上一帧的秒数。</param>
    /// <returns>当前应达到的不透明度 0~1。</returns>
    public float Update(long nowTimestampMs, double deltaSeconds)
    {
        if (!_closed && _idleMonitor.ShouldPoll(nowTimestampMs))
        {
            _idleMonitor.Refresh(nowTimestampMs);

            var idle = _config.Idle.Enabled && _idleMonitor.IdleSeconds >= _config.Idle.ThresholdMinutes * 60.0;

            if (idle)
            {
                _idleActive = true;
            }
            else if (_idleActive)
            {
                // 用户回来了：空闲态结束。手动打开过的情况下，这一次也一并收起来。
                _idleActive = false;
                if (_manualActive) { _manualActive = false; }
            }
        }

        var target = IsActive ? 1f : 0f;

        var duration = Math.Max(1, _config.Render.FadeDurationMs) / 1000.0;
        var step = (float)Math.Clamp(deltaSeconds / duration, 0.0, 1.0);

        if (_opacity < target)
        {
            _opacity = Math.Min(target, _opacity + step);
        }
        else if (_opacity > target)
        {
            _opacity = Math.Max(target, _opacity - step);
        }

        return _opacity;
    }

    /// <summary>
    /// 是否可以彻底停止渲染（已完全淡出）。
    /// 此时窗口应当隐藏、帧循环降频，静默期 CPU 占用趋近于 0。
    /// </summary>
    public bool CanSuspend => !IsActive && _opacity <= 0.001f;

    /// <summary>当前空闲增强系数：空闲时提亮，常态为 1。</summary>
    public float Boost
    {
        get
        {
            var settings = _config.Idle;
            if (!settings.BoostWhenIdle) { return 1f; }

            // 用不透明度做过渡，避免增强在淡入过程中突兀跳变。
            return 1f + ((Math.Max(1f, settings.IdleBoostGain) - 1f) * _opacity);
        }
    }

    /// <summary>关闭控制器，停止空闲检测。</summary>
    public void Close()
    {
        _closed = true;
    }
}
