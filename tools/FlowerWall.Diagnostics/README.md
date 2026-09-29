# FlowerWall.Diagnostics

离线诊断工具。**不启动窗口**，直接测量音频链路与渲染开销，并把可视化结果导出成 PNG。

## 为什么需要它

桌面程序的性能问题很难定位：是采集卡了？分析慢了？还是绘制太重了？
这个工具把每一段单独计时，改完参数跑一次就能看出是哪一段变化了多少。
同时它在无图形界面的环境里也能验证 FFT / 频段映射是否正确 ——
这就是逻辑层（`FlowerWall.Core`）不依赖窗口的原因。

## 用法

```powershell
# 全部检查（默认）
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 diag

# 只跑某一项
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 diag -DiagArgs render
```

- `-DiagArgs` 可省略，省略即跑全部。
- 可以一次传多个模式：`-DiagArgs fft,ring`。

模式名：`all`（默认）、`fft`、`ring`、`analyzer`、`interaction`、`render`、`icon`、`config`、`language`。

退出码 0 表示全部通过，非 0 表示有检查失败 —— 可以直接接进 CI。

> **注意**：不要用 `dotnet run --project ... -- render` 这种方式调用。
> `dotnet run` 在混入 MSBuild 单节点开关（`-m:1`）后参数分隔会失效，
> 工具会把开关当成模式名 —— 匹配不到任何用例，**却仍然返回成功**。
> `build.ps1` 因此改为「先构建、再直接启动产物程序集」，从根上避免这种假通过。

## 检查内容

| 分组 | 验证什么 | 判据 |
| --- | --- | --- |
| `fft` | 单频正弦的峰值频点与幅度 | 检出频率与真实频率相差不超过一个 bin；幅度误差 < 15% |
| `ring` | 环形缓冲取数语义 | 不足补零、写满顺序、立体声混单声道、溢出后取最新 |
| `analyzer` | 频段映射与电平 | 静音全零、100Hz 落低频段、8kHz 由高频段主导、电平随幅度单调 |
| `interaction` | 唤醒状态机 | 空闲阈值触发、手动开关优先、淡入淡出进度与打断 |
| `render` | 渲染开销与预览图 | 分段计时、整帧耗时、静音后停止重绘 |
| `icon` | 梅花图标 | 导出对照图供目视检查 |
| `config` | 配置模板 | 从代码默认值重新生成 `config\settings.json` |
| `language` | 双语文案完整性 | 每个 `TextKey` 在中英两边都有非空且不同的取值 |

## 输出

预览图写到仓库根目录的 `artifacts\`（已在 `.gitignore` 中，正式展示图放在 `docs\images\`）：

| 文件 | 内容 |
| --- | --- |
| `preview-desktop.png` | 原图（透明背景），用于检查边缘是否有黑边 |
| `preview-desktop-on-gray.png` | 灰底合成图，最接近实际桌面观感 |
| `preview-static.png` | 唱片转角为 0、频谱静音的静态图，用于核对文字方向与构图 |
| `preview-static-on-gray.png` | 上者的灰底合成版 |
| `preview-vinyl-closeup.png` | 胶片 2 倍放大图 —— 全屏预览缩到千余像素后纹理细节会糊掉 |
| `preview-vinyl-closeup-on-gray.png` | 上者的灰底合成版 |
| `icon-sheet.png` | 梅花图标的尺寸与配色对照图 |

## 性能基线

本机实测（1920×1080 画布 / 128 声波条 / 2048 点 FFT）：

```
├ 清屏：0.9 ms/帧
├ 纯色背景：2.4 ms/帧
├  胶片-整体：11.6 ms/帧（光晕 1.9 + 盘体 9.5 + 中心层 1.7）
├ 声波环：6.4 ms/帧
纯绘制：18.1 ms/帧（55 FPS 上限）
绘制+拷屏：21.6 ms/帧（46 FPS 上限）
24 FPS 预算占用：51.9%（默认档）
关闭流动后静音停止重绘：末 300 帧 0 次 ✓
```

数字随屏幕分辨率与 `record.diameter` 变化，改完参数重跑一次即可对比。

## 一个已经踩过的坑

唱片本体最初用 `DrawingImage` 保留几何，结果每帧都被重新光栅化几十条同心纹路，
单这一步就占 14.6 ms/帧（远超 60 FPS 预算）。改成构造时预光栅化成 `RenderTargetBitmap`
之后大幅下降；后续又把盘体与中心层两张位图合并成一张盘面精灵，开销再降一个数量级。
**结论：静态且复杂的图形一定要预先光栅化，能合并的图层要合并。**
