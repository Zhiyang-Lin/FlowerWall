# 上传到 GitHub 操作指南

> 本文是**操作步骤**，不是自动化脚本。请在你自己确认后手动执行 ——
> 我不会替你上传任何东西。

---

## 第 0 步：先看清将上传什么

仓库我已经在本地初始化好了（`git init` + 首次 `add`），但**没有提交、也没有配置远程仓库**。

```powershell
cd <仓库所在目录>          # 就是你现在打开的这个项目文件夹
git status
```

**应该看到**：57 个待提交文件，总计约 2.2 MB。构成：

| 类型 | 数量 |
| --- | --- |
| `.cs` 源码 | 31 |
| `.md` 文档 | 8 |
| `.png` 展示图 | 3 |
| `.csproj` 工程文件 | 3 |
| `.ps1` / `.py` 脚本 | 2 / 2 |
| 其余（`LICENSE`、`.sln`、`.props`、配置等） | 8 |

**不应该看到** `.tools/`、`bin/`、`obj/`、`dist/`、`logs/`、`artifacts/` —— 它们已在 `.gitignore` 里。

自查这两条命令，输出为空就说明干净：

```powershell
git status --porcelain --untracked-files=all | Where-Object { $_ -notmatch '^A ' }   # 有没有漏网的未跟踪文件
git status --porcelain --ignored=matching                                          # 看看被忽略的到底是哪些
```

如果看到 `.tools/` 出现在列表里，**先停下来**：那里面有 700MB 的 SDK，传上去会非常痛苦。
检查 `.gitignore` 第一行是否是 `.tools/`。

---

## 第 1 步：本地提交

```powershell
git commit -m "初始提交：花墙 FlowerWall —— 桌面音频可视化壁纸"
```

提交人信息会使用你机器上已配置的 `user.name` / `user.email`。

> **提交前请先决定用哪个邮箱。**
> 提交者的邮箱会**永久公开**在 GitHub 的提交记录里，任何人都能看到，
> 而且之后改成私有仓库也不会从历史中消失。
> 如果不希望公开真实邮箱，有两个办法：
>
> 1. 用 GitHub 提供的匿名邮箱：打开 <https://github.com/settings/emails>，
>    勾选 **Keep my email addresses private**，页面上会给一个
>    `<数字ID>+<用户名>@users.noreply.github.com` 形式的地址，用它替换下面的邮箱。
> 2. 只对这个仓库设置（不影响你其他项目）：
>
> ```powershell
> git config user.name "你的名字"
> git config user.email "你的匿名邮箱"
> ```
>
> 想看当前配的是什么：`git config user.name; git config user.email`

---

## 第 2 步：在 GitHub 上建仓库

1. 打开 <https://github.com/new>
2. **Repository name** 填 `FlowerWall`（想用别的名字也行，不影响代码）
3. **Description** 建议填：

   ```
   Windows 桌面音频可视化壁纸：黑胶唱片 + 环绕频谱，监听系统声音实时呈现 | audio-reactive desktop wallpaper
   ```

4. 可见性：**Public**（公开，别人才能下载）
5. **重要**：下面三个初始化选项**全部不要勾选**
   - ❌ Add a README file
   - ❌ Add .gitignore
   - ❌ Choose a license

   因为本地已经有这些文件了，勾了会导致第一次推送冲突（报 `rejected - fetch first`）。

6. 点 **Create repository**

---

## 第 3 步：关联远程并推送

建好后 GitHub 会显示一段命令。用 **HTTPS** 那一段（更省事），把 `<你的用户名>` 替换掉：

```powershell
git remote add origin https://github.com/<你的用户名>/FlowerWall.git
git branch -M main
git push -u origin main
```

推送时会弹窗要求登录 GitHub。**注意**：GitHub 从 2021 年起不接受账号密码，
需要 **Personal Access Token**：

1. 打开 <https://github.com/settings/tokens>
2. **Generate new token** → **Generate new token (classic)**
3. Note 随便填（如 `FlowerWall push`），Expiration 选 90 天
4. 勾选 **`repo`**（这一项就够）
5. 生成后**立刻复制**那串 `ghp_...`（离开页面就再也看不到）
6. 推送弹窗里：用户名填 GitHub 用户名，**密码粘贴那个 token**

推送成功的标志是输出里有 `* [new branch] main -> main`。

---

## 第 4 步：核对仓库页面

刷新 `https://github.com/<你的用户名>/FlowerWall`，应该看到：

- 首页渲染出 **README.md**，顶部那张预览图能正常显示
- 文件列表里有 `src/`、`tools/`、`config/`、`scripts/`、`docs/`、`LICENSE`
- 顶部有一个 **MIT license** 标签
- 语言统计显示 **100% C#**

**如果预览图显示不出来**：说明 `docs/images/` 没传上去。检查：

```powershell
git ls-files docs/images
```

应该列出三张 png。没有的话执行 `git add docs/images && git commit -m "补充展示图" && git push`。

---

## 第 5 步：发一个 Release（让别人「点击即用」）

这一步对应你需求里的「要用 Release 版本，用户可以点击即用」。
源码仓库里不放 exe（体积大且不好维护），单独发 Release 更规范。

**5.1 在本地生成发布包**（需要联网，首次会下载运行时包）

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 publish
```

产物：`dist\FlowerWall.exe`。它是**单文件 + 自包含**的，约 150MB ——
因为要把 .NET 运行时一起打包，这样别人下载后**不需要装任何东西，双击就能跑**。

> 想要小体积可以改用 `publish-fd`（约 340KB），但对方必须先装
> [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)。
> 两者取舍：给普通用户就发自包含版，给开发者可以附上框架依赖版。

**5.2 在 GitHub 上创建 Release**

1. 仓库页右侧 → **Releases** → **Create a new release**
2. **Choose a tag** 填 `v0.1.0`，然后点 **Create new tag**
3. **Release title** 填 `花墙 FlowerWall v0.1.0`
4. **Describe this release** 建议内容：

   ```markdown
   ## 花墙 FlowerWall v0.1.0

   第一个可发布版本：桌面音频可视化壁纸。

   ### 功能
   - 监听系统声音（WASAPI 回环），实时绘制黑胶唱片与环绕频谱
   - 点击屏幕边缘的梅花图标唤醒；键鼠空闲 5 分钟自动淡入
   - 背景铺满整个桌面，5 套配色方案可一键切换
   - 支持导入自定义背景图
   - 中英双语界面

   ### 下载
   - `FlowerWall.exe` —— 单文件自包含，**双击即可运行**，无需安装 .NET

   ### 运行要求
   - Windows 10 1903 或更高版本（64 位）
   ```

5. 把 `dist\FlowerWall.exe` **拖进** "Attach binaries" 区域（150MB 可能要传一会儿）
6. 点 **Publish release**

**5.3 回到主 README 加下载引导**（可选但推荐）

在 `README.md` 的「快速开始」上面加一段，让访客先看到下载入口：

```markdown
## 下载

不想自己编译的话，直接去 [Releases](../../releases) 下载 `FlowerWall.exe`，双击运行即可。
```

改完提交推送：

```powershell
git add README.md
git commit -m "README 增加下载引导"
git push
```

---

## 第 6 步：此后日常维护

改完代码后：

```powershell
git status                      # 看改了哪些文件
git diff                        # 看具体改动
git add -A
git commit -m "说明这次改了什么"
git push
```

**提交信息建议**：一句话说清"为什么改"，而不是"改了什么文件"。
例如 `修复导入背景图后无法恢复纯色背景` 比 `修改 TrayMenu.cs` 有用得多。

---

## 常见问题

| 现象 | 原因与处理 |
| --- | --- |
| `rejected - fetch first` | 建仓库时勾了 README/.gitignore/LICENSE。执行 `git pull --rebase origin main` 合并后重推；或删掉远程仓库重建 |
| 提示 `remote origin already exists` | 已经配过远程。`git remote set-url origin <新地址>` 覆盖即可 |
| 推送卡住不动 | 国内网络问题。可以配代理：`git config --global http.proxy http://127.0.0.1:端口` |
| 上传后发现传了不该传的 | `git rm -r --cached <路径>` 后提交，再把该路径加进 `.gitignore`。注意文件仍留在历史里，敏感信息需要重建仓库 |
| 中文文件名在 GitHub 显示乱码 | 执行 `git config --global core.quotepath false` |
| 根目录的 `background_default.png` 要不要传 | 已在 `.gitignore` 里排除。它像是测试「导入背景图」时留下的文件。如果其实是你想内置的默认背景，告诉我，我把它放进 `config/` 并写成默认值 |

---

## 上传前自查清单

- [ ] `git status` 里没有 `.tools/`、`bin/`、`obj/`、`dist/`、`artifacts/`
- [ ] `README.md` 里的图片路径能显示（`docs/images/preview.png`）
- [ ] `LICENSE` 是 MIT
- [ ] **决定好了提交用哪个邮箱**（见第 1 步的提示，提交邮箱会永久公开）
- [ ] 想在 README 里放上你的 GitHub 用户名或联系方式（可选，现在是空的）
- [ ] 确认没有把个人信息（绝对路径里的 Windows 用户名、日志）提交进去

最后一条可以这样自查 —— 把尖括号里换成你自己的 Windows 用户名：

```powershell
git grep -i -n "<你的Windows用户名>" -- .
git grep -i -n "C:\\\\Users" -- .      # 找写死的绝对路径
```

两条都输出为空即可放心推送。
