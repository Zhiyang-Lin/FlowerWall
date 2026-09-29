# 架构设计说明

> 本文记录项目的关键技术决策与理由。改动架构前请先读这里，改动后请同步更新。

## 1. 运行时形态

单进程、零第三方 NuGet 依赖。

```
FlowerWall.exe
   ├─ UI 线程        WinForms 消息循环
   │    ├─ VinylWindow        铺满主显示器的分层窗口（画布 = 屏幕分辨率）
   │    ├─ ControlButtonForm  屏幕边缘的悬浮梅花
   │    └─ TrayMenu           托盘图标与菜单
   ├─ Capture 线程   WASAPI 回环采集 → 环形缓冲（事件驱动，静音时休眠）
   └─ 帧定时器      5 ms 唤醒一次，到点才渲染（见第 7 节）
```

三个工程：

| 工程 | 职责 | 为什么单独拆出来 |
| --- | --- | --- |
| `FlowerWall.Core` | 采集、分析、互操作、绘制 | 不依赖窗口，因此可以在无界面环境测性能与跑回归 |
| `FlowerWall.Desktop` | 入口、窗口、按钮、托盘 | 只负责「显示状态」与「收集操作」 |
| `FlowerWall.Diagnostics` | 离线测量与预览图导出 | 让性能与正确性可以被重复验证 |

## 2. 为什么是「顶层透明分层窗口」而不是「挂进 WorkerW」

Windows 没有提供壁纸绘制 API。常见的两种做法：

| 方案 | 做法 | 问题 |
| --- | --- | --- |
| A. WorkerW 挂载 | 给 `Progman` 发 `0x052C` 逼 Explorer 生成 `WorkerW`，把窗口 `SetParent` 进去 | per-pixel alpha 要求 `UpdateLayeredWindow`，而它对子窗口的支持在各版本上行为不一致；Explorer 重启 / 切换壁纸后需要重新挂载；多出一套重挂载逻辑 |
| B. 顶层分层窗口 | `WS_EX_LAYERED` 顶层窗口 + `UpdateLayeredWindow`，显示时压到 `HWND_BOTTOM` | 严格来说不在壁纸层，而是在壁纸之上、图标之下 |

**本项目选择 B**，理由：

1. **正确性优先**：`UpdateLayeredWindow` 的 per-pixel alpha 在顶层窗口上行为完全确定，黑胶圆盘边缘不会出现黑边或方框。
2. **效果等价**：可视化激活时桌面图标本来就被隐藏（见第 4 节），此时壁纸层与桌面层的视觉差异不存在。
3. **开销可控**：不需要为兼容 Explorer 重启而写重挂载逻辑，代码更少、更稳。
4. **窗口铺满主显示器**：背景（纯色 / 渐变 / 用户导入的图片）要铺满整屏，
   画布因此等于屏幕分辨率（1920×1080 → 约 7.9 MB/帧）。
   胶片与声波环仍按固定像素尺寸居中绘制 —— 屏幕越大留白越多，视觉元素不会变形。

窗口样式：
- `WS_EX_LAYERED`：per-pixel alpha 的前提。
- `WS_EX_TRANSPARENT`：鼠标事件穿透，绝不挡住桌面和图标操作。
- `WS_EX_NOACTIVATE`：永不抢焦点，不打断当前正在打字的程序。
- `WS_EX_TOOLWINDOW`：不出现在 Alt+Tab 与任务栏。

用 `SetWindowPos(..., HWND_BOTTOM, ...)` 压到 z 序底部，避免浮在其他窗口之上。

## 3. 音频采集：为什么不用 NAudio

- NAudio 是优秀的库，但会把 WASAPI 的 COM 细节包起来，同时引入额外依赖和体积。
- 本项目对音频的需求非常窄：**系统回环（loopback）采集 + 单路混音 + 频谱**。
- 因此自写一层最小 WASAPI 互操作（`src/FlowerWall.Core/Interop/Audio/`，约 400 行），换来：
  - 零 NuGet 依赖 → 无还原、无版本冲突、发布产物更小；
  - 完全掌控缓冲与线程模型，便于把 CPU 占用压到最低；
  - 后续要做「每声道独立可视化」时可直接扩展 `IAudioCaptureClient` 的读取逻辑。

关键技术点：
- `IAudioClient` 以 `AUDCLNT_STREAMFLAGS_LOOPBACK` 开启共享模式采集渲染端点 —— 这是「监听系统声音」的官方途径，且不会把声音回放出来造成回声。
- 采集格式是混音格式（通常是 32-bit float，48 kHz，2ch）。用 `AUDCLNT_STREAMFLAGS_EVENTCALLBACK` + `SetEventHandle`，采集线程阻塞在 `WaitForSingleObject` 上，**无音频时 CPU 占用为 0，绝不空转**。
- 设备被拔出/切换/禁用时返回 `AUDCLNT_E_DEVICE_INVALIDATED`，采集线程进入重连循环（指数退避，上限 5 秒）。

## 4. 桌面图标隐藏

激活期间隐藏图标（用户要求），停用时精确恢复。实现见 `src/FlowerWall.Core/Interop/DesktopIconHost.cs`：

- 通过 `Progman → SHELLDLL_DefView → SysListView32` 找到图标宿主窗口。
- 用 `ShowWindow(SW_HIDE/SW_SHOW)` 切换 —— 这是 Explorer 自身响应「查看 → 显示桌面图标」所用的机制。
- **恢复契约**：进程退出（含异常退出）时一定恢复图标，用 `AppDomain.ProcessExit` + `Application.ThreadException` + 顶层 `try/finally` 三重保障。
  「一定恢复」是刻意设计：配置里没有开关可以关掉它 —— 让用户的桌面图标消失是比「多显示了图标」严重得多的问题。
- 若目标窗口不支持 `ShowWindow`（返回状态与预期不符），自动降级为 `DwmSetWindowAttribute(DWMWA_CLOAK)` 隐藏。

## 5. 状态机：唤醒与淡入淡出

```
                 ┌──────────────── 手动开关(点击小圆点) ────────────────┐
                 │                                                      ▼
  [Sleeping] ──空闲 ≥ IdleMinutes──▶ [Waking] ──淡入 600ms──▶ [Active]
      ▲                                                            │
      └────────────── 手动关闭 / 空闲被打断(且未手动激活) ◀──────────┘
```

- 空闲判定：`GetLastInputInfo` 轮询（500 ms 一次），**不做全局键鼠钩子** —— 钩子会影响其他程序且更容易被杀软拦截，轮询成本可忽略。
- 淡入淡出：分层窗口的 `SourceConstantAlpha` 从 0 → 255 渐变，**不需要重绘位图**，几乎零成本。
- 空闲态「增强」：频谱增益 ×`IdleBoostGain`，光晕强度与唱片亮度同步提升。
- 完全淡出到 0 后立即 `Hide()` 窗口并**暂停渲染定时器**，静默期 CPU 为 0。

## 6. 渲染：分层窗口的预算

- 位图尺寸 = 可视化画布 = 屏幕分辨率（1920×1080 → 约 7.9 MB/帧），窗口隐藏时**不渲染、不分配**。
- 每帧：`Graphics`/WPF 绘制 → `RenderTargetBitmap` → 拷进 DIB → `UpdateLayeredWindow`。
- 唱片与声波环的参数见 `VisualTheme`，全部来自配置。

### 6.0 实测基线（1920×1080 / 128 声波条 / 2048 点 FFT）

```
├ 清屏：0.9 ms/帧
├ 纯色背景：2.4 ms/帧
├  胶片-整体：11.6 ms/帧（光晕 1.9 + 盘体 9.5 + 中心层 1.7）
├ 声波环：6.4 ms/帧
纯绘制：18.1 ms/帧（55 FPS 上限）
绘制+拷屏：21.6 ms/帧（46 FPS 上限）
24 FPS 预算占用：51.9%（默认档）
```

数字随屏幕分辨率与 `record.diameter` 变化，用 `scripts\build.ps1 diag -DiagArgs render` 重测。

### 6.1 性能上踩过的两个坑（都是实测发现的）

**坑一：静态几何不能留在每帧的场景图里。**
唱片本体最初用 `DrawingImage` 保留几何，结果 WPF 每帧都把几十条同心纹路重新光栅化，
单这一步 14.6 ms/帧（超过 60 FPS 预算）。改成构造时预光栅化成 `RenderTargetBitmap` 后大幅下降。
后续又发现盘体与中心层各占一张大位图、每帧要合成两次；合并成一张盘面精灵后，
同样画面的这一步开销降到原来的十分之一量级。
**结论：静态且复杂的图形一定要预先光栅化；能合并的图层要合并。**

**坑二：WinForms 定时器的精度不代表你想要的帧率。**
把 `Timer.Interval` 设成 `1000/30` 只能跑出 21 FPS —— 因为系统计时粒度约 15.6 ms，
33 ms 的请求会被推成近两倍。实测把 Interval 设成 5 ms，唤醒间隔仍在 15 ms 量级。
因此帧循环改为「小间隔轮询 + 到点才渲染 + 用实际时间差做动画」，
帧间隔 = 唤醒粒度 + 单帧耗时。
也试过在帧处理器里 `Thread.Sleep` 补齐时间，结果更差：
WinForms 定时器在处理消息期间不重入，睡在处理器里会把下一次唤醒一起推迟。**这条路不要走。**

## 7. 帧循环与状态机

单一决策点：`WallpaperApplication.OnFrameTick` 是唯一决定「这一帧做什么」的地方。

```
定时器唤醒（5ms）
  → 没到目标帧间隔？直接返回（CPU 几乎为 0）
  → InteractionController.Update：空闲判定 + 淡入淡出进度
  → SpectrumAnalyzer.Analyze：从环形缓冲取最新窗口做 FFT
  → 不透明度 > 0：VinylWindow.Present（渲染 + 上屏）
     不透明度 = 0 且已淡出：VinylWindow.Suspend（隐藏窗口）
  → SyncDesktopIcons：图标可见性跟随激活状态
```

状态迁移规则见 `InteractionController`，可用诊断工具验证：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 diag   # 含 [Interaction] 用例
```

## 8. 目录职责

| 路径 | 职责 |
| --- | --- |
| `src/FlowerWall.Desktop/Program.cs` | 进程入口、单实例、DPI、异常与退出兜底 |
| `src/FlowerWall.Desktop/WallpaperApplication.cs` | 组装各模块、生命周期、帧循环、图标联动 |
| `src/FlowerWall.Desktop/Ui/VinylWindow.cs` | 可视化窗口与上屏（画布 = 屏幕分辨率） |
| `src/FlowerWall.Desktop/Ui/LayeredWindowSurface.cs` | 分层窗口底层：DIB 管理 + UpdateLayeredWindow |
| `src/FlowerWall.Desktop/Ui/ControlButtonForm.cs` | 悬浮梅花按钮（悬停动画、拖动、边缘吸附） |
| `src/FlowerWall.Desktop/Ui/BlossomIconRenderer.cs` | 梅花图形与图标调色板（矢量绘制，无位图资源） |
| `src/FlowerWall.Desktop/Ui/TrayMenu.cs` | 托盘图标与菜单（与按钮共用同一份菜单） |
| `src/FlowerWall.Core/Core/AppConfig.cs` | 配置数据模型（全部可调参数都在这里） |
| `src/FlowerWall.Core/Core/ConfigStore.cs` | 配置读写、注释剥离、热重载 |
| `src/FlowerWall.Core/Core/ThemePresets.cs` | 5 套预置配色方案与匹配逻辑 |
| `src/FlowerWall.Core/Core/Localization.cs` | 中英双语文案表（`TextKey` + 字典） |
| `src/FlowerWall.Core/Core/IdleMonitor.cs` | 键鼠空闲检测 |
| `src/FlowerWall.Core/Core/InteractionController.cs` | 唤醒状态机（唯一的显隐决策点） |
| `src/FlowerWall.Core/Core/RuntimeStats.cs` | 可选的运行期统计（`FLOWERWALL_STATS=1`） |
| `src/FlowerWall.Core/Audio/` | 回环采集、环形缓冲、FFT、频段分析 |
| `src/FlowerWall.Core/Interop/Audio/` | WASAPI COM 接口定义（只放声明，不含业务） |
| `src/FlowerWall.Core/Interop/NativeMethods.cs` | Win32 P/Invoke 声明（只放声明，不含业务） |
| `src/FlowerWall.Core/Interop/DesktopIconHost.cs` | 桌面图标宿主的定位与显隐 |
| `src/FlowerWall.Core/Rendering/` | 背景、胶片、声波环、梅花的纯绘制逻辑（不碰窗口和音频） |
| `tools/FlowerWall.Diagnostics/` | 离线测量、回归用例、预览图导出 |

## 9. 已知限制

- 只铺满主显示器，多显示器尚未分别呈现。
- 只接受 32 位浮点的回环混音格式（Windows 的默认情况）；其他格式会拒绝连接并重试，
  而不是冒险把数据按错误类型解释。
- 实测帧率上限受系统计时粒度约束，`maxFps`（默认 24）是目标而非保证值（见第 6.1 节）。
- 桌面图标隐藏依赖 Explorer 的 `SHELLDLL_DefView`；被第三方 shell 接管时会静默跳过（不报错、不影响其他功能）。
