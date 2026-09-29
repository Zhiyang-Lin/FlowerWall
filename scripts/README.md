# 构建脚本说明

| 脚本 | 用途 | 何时用 |
| --- | --- | --- |
| `bootstrap-sdk.ps1` | 安装项目专用 .NET 8 SDK 到 `.tools\dotnet` | 只在新机器上跑一次 |
| `build.ps1` | 构建 / 运行 / 发布 / 诊断 / 清理 | 日常都用它 |
| `download.py` | 用 Python 的 OpenSSL 下载文件 | 被 bootstrap 调用；受限环境下比 Schannel 可靠 |
| `install-dotnet-sdk.py` | 从官方 feed 下载并解压 SDK | 被 bootstrap 调用；不依赖 dotnet-install.ps1 的下载路径 |

## 常用命令

```powershell
# 一次性：准备 SDK
powershell -ExecutionPolicy Bypass -File scripts\bootstrap-sdk.ps1

# 日常
powershell -ExecutionPolicy Bypass -File scripts\build.ps1              # 构建
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 run          # 构建并运行
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 diag         # 跑诊断工具
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 publish      # 自包含单文件
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 publish-fd   # 框架依赖单文件（体积小得多）
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 clean        # 清理 bin\ obj\
```

## 发布需要联网（首次）

`publish` / `publish-fd` 都要指定 `-r win-x64`，这会触发对 `win-x64` 运行时包的还原 ——
这些包首次使用时才从 nuget.org 下载。

**因此离线环境（或沙箱）里发布会失败，报 `NU1301`。** 这不是项目配置问题：
在能访问 nuget.org 的机器上跑一次即可，之后包会留在本地缓存里。
脚本在失败时会打印这段说明，不会让你对着一行 NU1301 猜。

- `publish`：自包含，目标机器不需要装 .NET，代价是体积大（运行时一起打包）。
- `publish-fd`：框架依赖，体积小很多，但目标机器需要装 .NET 8 桌面运行时。

## 两个环境相关的坑（已在脚本里处理）

1. **PowerShell 5.1 按 ANSI 读取无 BOM 的 .ps1**，中文注释会导致脚本解析失败
   （症状是「变量未设置」之类的怪错误，很难联想到编码）。
   因此 `scripts\*.ps1` 全部只写 ASCII（注释用英文）。
   C# 源码不受影响，源文件是 UTF-8，编译器默认按 UTF-8 处理。

2. **受限环境下 Schannel 无法完成 TLS 握手**（`SEC_E_NO_CREDENTIALS`），
   而 Python 自带的 OpenSSL 不受影响。所以下载逻辑优先走 `download.py`。

## 为什么 SDK 装在项目内

`bootstrap-sdk.ps1` 默认装到 `<repo>\.tools\dotnet`，而不是 `%LOCALAPPDATA%`：

- 整个工作区自包含，删目录即卸载；
- 不需要管理员权限，也不污染系统目录；
- `.tools\` 已在 `.gitignore` 中，不会进入版本库。

想装到用户目录就显式传参：

```powershell
scripts\bootstrap-sdk.ps1 -InstallDir "$env:LOCALAPPDATA\Microsoft\dotnet"
```

## 并行构建的注意事项

`build.ps1` 固定使用单节点构建（`-m:1`），并设置了 `MSBUILDDISABLENODEREUSE=1`。
原因：在受限环境（沙箱、受限令牌、只读临时目录）里 MSBuild 的并行工作节点可能起不来，
症状是「Build FAILED 但 0 Errors」这种完全没信息的失败，排查成本很高。
单节点在这个规模的解决方案上只慢几秒，但行为确定。

**注意不要额外设置 `MSBUILDNOINPROCNODE=1`**：它在 `-m:1` 之上会连进程内节点一起禁用，
导致 MSBuild 完全没有可用节点，反而复现同样的静默失败（已实测验证）。
