# 花墙 FlowerWall

[中文说明](README.md) ｜ [English](README.en.md)

Windows 桌面音频可视化壁纸：**黑胶唱片旋转 + 环绕频谱**，监听系统正在播放的声音实时呈现，
背景默认铺满整个桌面。

- 技术栈：C# 12 / .NET 8，WinForms（窗口与消息循环）+ WPF（可视化绘制），**零第三方依赖**
- 音频来源：WASAPI Loopback（系统回环采集，不会产生回声，也不需要虚拟声卡）
- 呈现方式：整屏透明分层窗口（`UpdateLayeredWindow` per-pixel alpha），位于壁纸之上、桌面图标之下
- 唤醒方式：点击屏幕边缘的梅花图标 / 键鼠空闲自动淡入（默认 5 分钟）
- 双语界面：中文「花墙」与 English「FlowerWall」可随时切换
- 默认配色：抹茶绿底（`#A9C69A`）+ 樱粉主色（`#E4679A`），另预置 5 套方案可一键切换
- 设计目标：代码可长期维护、产物小、内存与 CPU 占用低

![预览](docs/images/preview.png)

> 上图由诊断工具生成（`scripts\build.ps1 diag`），是可复现的渲染结果，不是截图。
> 其他预览：[`docs/images/vinyl-closeup.png`](docs/images/vinyl-closeup.png)（胶片 2 倍放大，看纹路与磨损）、
> [`docs/images/icon-sheet.png`](docs/images/icon-sheet.png)（梅花图标在各尺寸下的对照）。

---

## 快速开始

### 1. 安装 .NET 8 SDK（只需一次）

```powershell
powershell -ExecutionPolicy Bypass -File scripts\bootstrap-sdk.ps1
```

装到项目内 `.tools\dotnet`，不需要管理员权限、不污染系统目录。脚本幂等，已装则直接跳过。

### 2. 运行

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 run
```

### 3. 发布单文件

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 publish      # 自包含，目标机无需装 .NET
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 publish-fd   # 框架依赖，体积小很多
```

> 首次发布需要联网：`-r win-x64` 会触发对 win-x64 运行时包的还原。
> 离线环境会报 `NU1301`，在能访问 nuget.org 的机器上跑一次即可（之后走本地缓存）。

其他任务（`build` / `diag` / `config` / `clean`）与注意事项见 [`scripts/README.md`](scripts/README.md)。

---

## 使用方式

| 操作 | 效果 |
| --- | --- |
| 左键单击屏幕边缘的梅花 | 立即唤醒 / 关闭可视化 |
| 拖动梅花 | 移动到任意位置，松手后靠近边缘会吸附 |
| 鼠标悬停梅花 | 放大、提亮并出现外发光 |
| 右键梅花 / 托盘图标 | 菜单：显示、导入背景图、恢复默认背景、配色方案、语言、重新加载配置、打开配置、退出 |
| 键鼠连续 5 分钟无操作 | 自动淡入可视化并进入增强模式 |
| 键鼠恢复操作 | 自动淡出（若此前是手动点亮则保持显示） |

可视化显示期间桌面图标会自动隐藏，隐藏或淡出后立即恢复。程序退出（含异常退出）一定会恢复。

### 配色方案

右键菜单「配色方案」里有 5 套预置方案，点一下整套切换（背景色 + 主色 + 高光 + 纹路色一起换）：

| 方案 | 底色 | 主色 |
| --- | --- | --- |
| `Matcha`（默认） | 抹茶绿 `#A9C69A` | 樱粉 `#E4679A` |
| `Forest` | 深墨绿 `#20463A` | 薄荷绿 `#5FD6A8` |
| `Paper` | 近白 `#F4F1EA` | 玫红 `#D6336C` |
| `Midnight` | 暗紫 `#211C2E` | 紫罗兰 `#A78BFA` |
| `Sakura` | 樱粉底 `#F2D3DE` | 亮粉 `#E85D9B` |

想微调其中某个颜色，直接改 `settings.json` 里的 `theme.*` / `background.*` 即可 ——
手改后菜单会显示为「自定义（手改色号）」，不会被覆盖。

### 背景

- **导入背景图**：右键菜单选图，自动拷进程序目录的 `assets\` 并写配置。
- **恢复默认背景**：导入图片后想回到纯色，点这一项即可。
  （只改 `background.color` 是不生效的 —— 图片模式下颜色会被忽略，这是最容易踩的坑。）

### 双语

界面语言由 `app.language` 决定：`Auto`（跟随系统）/ `Chinese` / `English`。
右键菜单里的「语言 / Language」可以随时切换并写回配置，**立即生效、不需要重启**。

注意：胶片中心显示的是**星期**（花体英文，如 `Tuesday`），随胶片一起旋转，
刻意不跟随界面语言 —— 唱片标签上的字属于装饰。

---

## 配置

配置文件：`config/settings.json`。菜单「重新加载配置」可热重载，**无需重启**。
文件顶部带一段调参速查注释，支持 `//` 与 `/* */`。

新增配置项后，用下面这条命令让模板与代码默认值重新同步：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 diag -DiagArgs config
```

| 想改什么 | 改哪个字段 |
| --- | --- |
| 整套配色 | 右键菜单「配色方案」，或改 `theme.preset` |
| 背景色 | `background.color`（默认抹茶绿 `#A9C69A`） |
| 背景层次 | `background.gradientStrength`（**默认 0 = 平坦纯色**，调大才有渐变，会明显增加开销） |
| 背景图 / 恢复纯色 | 右键菜单「导入背景图」/「恢复默认背景」 |
| 主题色号（细调） | `theme.accentColor`、`theme.highlightColor`、`theme.grooveColor` |
| 胶片大小 / 转速 | `record.diameter` / `record.rpm` |
| 胶片纹路 | `record.grooveCount`、`record.grooveOpacity`、`record.grooveOuterBoost` |
| 胶片反光 / 磨损 | `record.specularStrength` / `record.wearStrength` |
| 中心文字大小 | `record.centerTextScale` |
| 频谱条数 / 宽度 / 振幅 | `spectrum.bandCount` / `spectrum.barWidth` / `spectrum.maxLength` |
| 外侧倒影 | `spectrum.reflectionLength`、`spectrum.reflectionOpacity` |
| 声波灵敏度 | `spectrum.gain`、`spectrum.noiseFloorDb`、`spectrum.ceilingDb` |
| 帧率（省电） | `render.maxFps`（默认 24） |
| 空闲唤醒 | `idle.thresholdMinutes`、`idle.boostWhenIdle` |
| 界面语言 | `app.language` |

> 关于省电：频谱是**分立短条**，不依赖时间流动，因此静音且胶片停转后会**完全停止重绘**。

---

## 性能基线

以下数字是**本机实测**，可复现：`scripts\build.ps1 diag -DiagArgs render` 给出渲染分段耗时，
把 `FLOWERWALL_STATS=1` 打开后运行程序，每秒会往 `logs\stats.log` 写一行运行期统计。

背景铺满全屏后画布 = 屏幕分辨率，因此每帧开销与分辨率成正比（下面按 1920×1080 计）。
下面的数字有 ±1 ms 级别的波动（每次运行会有差异），看趋势即可：

| 阶段 | 耗时 |
| --- | --- |
| 清屏 | 0.9 ms/帧 |
| 纯色背景 | 2.4 ms/帧 |
| 胶片（光晕 + 盘面 + 中心层） | 11.6 ms/帧 |
| 声波环（主条 + 副条 + 倒影 + 柔光） | 6.4 ms/帧 |
| 纯绘制 | 18.1 ms/帧（55 FPS 上限） |
| **整帧（含像素拷屏）** | **约 21.6 ms/帧（46 FPS 上限）** |

默认 `render.maxFps` 是 24，上表整帧耗时占 24 FPS 预算约 **52%** —— 留出的余量用于
空闲增强、导入大背景图等情况。

运行期：

| 状态 | CPU | 内存 |
| --- | --- | --- |
| 隐藏（等待唤醒） | **0.0%** | 约 66 MB |
| 显示（正在可视化） | 约 40–60%（单核口径） | 约 110 MB |
| 显示但静音且胶片停转 | **0.0%** | — |

几点说明：

- 隐藏时窗口 `Hide()` 且完全不做渲染，CPU 真的为 0（统计日志可验证 `rendered=0 / cpu=0.0%`）。
- 显示时每帧约 20 ms 全部来自 CPU 软件光栅化。真机上 WPF 会走 GPU，实际占用通常更低。
- **全屏背景是主要成本来源**：把 `background.mode` 设为 `"None"` 可省下约 3 ms/帧。
- **`maxFps` 是上限而不是保证值**：实际帧率 ≈ 1 /（系统计时粒度 + 单帧耗时）。
  本机沙箱计时粒度较粗，实测 24 目标跑出约 20 FPS；真机桌面会话能更接近配置值。

产物体积（Release、框架依赖）：

| 文件 | 大小 |
| --- | --- |
| `FlowerWall.exe` | 约 149 KB |
| `FlowerWall.dll` | 约 46 KB |
| `FlowerWall.Core.dll` | 约 139 KB |
| 整个输出目录 | 约 338 KB |

---

## 排查与调参

程序没有控制台，因此需要观察运行状态时用统计日志：

```powershell
$env:FLOWERWALL_STATS = '1'          # 每秒往 logs\stats.log 写一行：帧率、CPU、音频状态、空闲时长
$env:FLOWERWALL_DISABLE_IDLE = '1'   # 临时关掉空闲唤醒，便于测量
$env:FLOWERWALL_FORCE_VISIBLE = '1'  # 启动即显示，便于测量
```

日志里 `rendered=0` 表示当前没有渲染（隐藏态），`cpu=0.0%` 表示确实没有消耗。

视觉问题先跑诊断导图核对 —— 全屏预览缩到千余像素后细节会糊掉，所以胶片另有 2 倍放大图：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 diag                      # 全部检查 + 导出全部预览图
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 diag -DiagArgs render     # 只看渲染分段耗时
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 diag -DiagArgs icon       # 只看梅花图标对照图
```

可在一次调用里传多个模式：`-DiagArgs fft,ring`。

---

## 项目结构

```
仓库根目录（= 你打开的这个项目文件夹；本地文件夹叫什么都可以，
            GitHub 上的仓库名建议填 FlowerWall）
├─ src/
│  ├─ FlowerWall.Core/          逻辑库：可被桌面程序、诊断工具、单元测试共用
│  │  ├─ Audio/                 回环采集、环形缓冲、FFT、频段分析
│  │  ├─ Core/                  配置模型、配置读写、双语、配色方案、空闲检测、交互状态机
│  │  ├─ Interop/               Win32 与 WASAPI 声明（纯声明，无业务）
│  │  └─ Rendering/             背景、胶片、声波环、梅花图形的纯绘制逻辑
│  └─ FlowerWall.Desktop/       桌面程序：进程入口、窗口、悬浮梅花、托盘
├─ tools/FlowerWall.Diagnostics/ 离线诊断：性能测量、预览图导出、配置模板生成
├─ config/settings.json         用户配置
├─ artifacts/                   诊断工具生成的预览图（不入库）
├─ docs/                        架构说明、展示图、需求归档、上传指南
├─ scripts/                     构建脚本（说明见 scripts/README.md）
├─ REQUIREMENTS.md              需求与验收标准
├─ README.md / README.en.md     中文 / 英文说明
└─ LICENSE                      MIT
```

> 工程与程序集全部以 `FlowerWall.` 开头，与仓库名一致；
> 唯一的例外是本地的外层文件夹名（比如 `Desktop_Wallpaper`），它**不属于仓库内容**，
> 想去掉这个不一致，把文件夹改名即可，不影响任何构建或推送。

为什么拆成三个工程：逻辑层不依赖任何窗口，因此可以在无界面环境下测量性能与回归验证；
桌面程序只负责「把状态显示出来」和「收集用户操作」。

---

## 开发约定

- **零第三方依赖**：新增 NuGet 包前先确认是否真有必要，并在 `docs/design.md` 记录理由。
- **配置优先**：所有视觉与行为参数都进 `AppConfig`，绘制代码里不写魔法数字。
- **渲染与数据分离**：`Rendering/` 只接受 `SpectrumFrame`，不读音频、不碰窗口。
- **静态图形要预光栅化**：环形 / 复杂的静态几何留在每帧场景图里会被反复光栅化，实测差 3 倍以上。
- **暗色材质的细节靠对比度**：参数"写对了"不等于"看得见"。改完必须看 2 倍放大预览
  （`preview-vinyl-closeup.png`），全屏缩图里看不出纹理是否真的存在。
- **每帧零分配**：渲染与频谱分析路径上禁止 `new`（使用构造时预分配的缓冲）。
- **互操作层只放声明**：业务逻辑一律不写进 `Interop/`。
- **改完跑诊断**：`scripts\build.ps1 diag`，性能与正确性一起过一遍。

## 许可证

[MIT](LICENSE)

---

## 项目文档

| 文档 | 内容 |
| --- | --- |
| [docs/design.md](docs/design.md) | 架构决策记录：为什么用整屏分层窗口、为什么不用 NAudio、帧调度与踩过的性能坑 |
| [REQUIREMENTS.md](REQUIREMENTS.md) | 需求与验收标准、已知限制 |
| [docs/github-guide.md](docs/github-guide.md) | 上传到 GitHub / 发布 Release 的操作步骤 |
| [docs/requirements-original.md](docs/requirements-original.md) | 最初的需求清单与逐条完成情况 |
| [scripts/README.md](scripts/README.md) | 构建脚本说明与环境相关的注意事项 |
| [tools/FlowerWall.Diagnostics/README.md](tools/FlowerWall.Diagnostics/README.md) | 诊断工具用法与性能基线 |
| [README.en.md](README.en.md) | English documentation |
