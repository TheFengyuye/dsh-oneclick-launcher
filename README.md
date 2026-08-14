# dsh-oneclick-launcher · DeepSeek Harness 一键启动器

双击即可启动 [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) Web GUI 的 Windows 小工具，**完全不需要打开终端**。

首次运行时，如果检测到本机还没装 DeepSeek Harness，会引导你**一键自动安装**（通过 npm 安装到用户目录，无需管理员权限），装完自动启动并打开浏览器。

## 特性

- 🖱️ **一键启动**：双击 exe → 后台静默启动 `dsh web` → 自动打开浏览器进入 GUI，全程无黑窗口
- 📦 **首次自动安装**：自动检测 Node.js 与 `@deepseek-ai/dsh`，未安装时提供一键安装（含安装进度显示）
- 🧭 **自动发现**：按 配置 > 内置安装目录 > npm 全局 > npx 缓存 > exe 目录 的顺序自动定位 dsh
- 🔧 **托盘常驻**：关闭窗口不会退出服务（收进托盘），托盘菜单可「打开页面 / 安装或更新 dsh / 停止并退出」
- 🔁 **单实例**：重复双击只会重新打开页面，不会重复启动服务
- 📝 **日志**：运行日志写入 exe 同目录的 `launcher.log`，服务日志写入 `dsh-server.log`

## 使用方法

### 前置要求

- Windows 10/11
- 安装 [Node.js](https://nodejs.org/zh-cn/download) 20 或更高版本（当前 LTS 即可）

### 方式一：直接用编译好的 exe

下载仓库里的 `DeepSeek Harness 一键启动.exe`，双击即可：

1. 首次运行：若本机已装 dsh，直接启动；若未安装，点击「一键安装 dsh」（下载约 500+ 个依赖包，一般几分钟，视网络而定），完成后自动启动
2. 之后每次双击：服务在运行就直接打开浏览器；没在运行就自动拉起再打开浏览器
3. 想关掉服务：托盘图标右键 →「停止并退出」

> 小贴士：exe 未做代码签名，首次运行 SmartScreen 可能提示"未知发布者"，点「更多信息 → 仍要运行」即可。

### 方式二：从源码构建

需要 Windows 自带的 .NET Framework 4.x（csc.exe，无需额外安装）：

```powershell
pwsh -File build.ps1
```

构建产物为仓库根目录的 `DeepSeek Harness 一键启动.exe`。

## 配置（可选）

启动器窗口右上角（或托盘右键）有「**设置**」按钮，可以用文件夹选择器直接指定三个目录并保存，不用手写配置文件：

| 设置项 | 说明 | 留空时默认 |
|---|---|---|
| 工作目录 | dsh 运行与工作所在的文件夹 | exe 所在目录 |
| dsh 数据目录 | 会话/配置数据存放处（即 DSH_HOME） | `%USERPROFILE%\.dsh` |
| 一键安装目录 | 首次自动安装 dsh 的文件夹 | `%LOCALAPPDATA%\DeepSeekHarness` |

保存后如果服务正在运行会自动用新配置重启。

**配置是持久的，配一次下次直接用**：设置保存到用户级共享位置
`%LOCALAPPDATA%\DeepSeekHarnessLauncher\launcher.config.txt`，
无论双击哪个 exe（哪怕把 exe 复制到别的文件夹）都会自动读取同一份配置，不需要重新配置。

也可以手动编辑配置文件（exe 同目录放一个 `launcher.config.txt` 作为便携基础层，用户级共享配置优先级更高；全部可省略，省略则自动检测）：

```ini
# Node.js 可执行文件路径 (留空自动检测)
NodeExe=C:\Program Files\nodejs\node.exe
# dsh 入口 bin.js (留空自动检测)
DshBin=...
# Web GUI 地址与端口
Url=http://127.0.0.1:3080
Port=3080
# dsh 数据目录 (留空用 %USERPROFILE%\.dsh)
DshHome=C:\Users\cai\.dsh
# 服务工作目录 (留空用 exe 所在目录)
WorkDir=E:\deepseek harness
# 一键安装时的安装目录 (留空用 %LOCALAPPDATA%\DeepSeekHarness)
InstallDir=...
```

完整示例见 [launcher.config.example.txt](launcher.config.example.txt)。

## 常见问题

| 问题 | 解决 |
|---|---|
| 提示"未检测到 Node.js" | 先安装 [Node.js](https://nodejs.org/zh-cn/download)，窗口里也有「下载 Node.js」按钮 |
| 双击后浏览器打不开 / 一直"正在启动" | 看 exe 同目录的 `dsh-server.log`，常见是端口 3080 被占用，可改配置里的 `Port` |
| SmartScreen 提示 | exe 未签名，点「更多信息 → 仍要运行」 |
| 想换个图标 | 见下方"图标说明"，或把 `icon-source/meme.png` 换成你自己的图再重新构建 |

## 目录结构

```text
dsh-oneclick-launcher/
├─ Launcher.cs                  # 启动器源码 (C#, .NET Framework WinForms)
├─ build.ps1                    # 一键构建脚本
├─ make-icon-from-image.ps1     # 图片转多尺寸 ico 脚本
├─ make-icon.ps1                # 内置鲸鱼图标生成脚本 (无梗图时的后备)
├─ launcher.ico                 # 构建好的图标
├─ launcher.config.example.txt  # 配置示例
├─ icon-source/                 # 图标素材 (见 NOTICE)
├─ LICENSE / NOTICE
└─ DeepSeek Harness 一键启动.exe  # 构建产物
```

## 图标说明

默认图标使用"**你这吃白饭的蓝色大肥鱼**"梗图（DeepSeek 吉祥物娘化的蓝发鲸鱼女仆角色），原图来自 [YunYueSama/codex-deepseek-pet](https://github.com/YunYueSama/codex-deepseek-pet) 仓库的 `assets/你这吃白饭的蓝色大肥鱼.png`。素材版权归原作者所有，**不随本仓库代码的 MIT 许可分发**，个人使用没问题，商用或再分发请先获得原作者授权（详见 [NOTICE](NOTICE)）。

如果不想附带该素材，删除 `icon-source/` 目录后用 `build.ps1` 重新构建即可，会回退到内置的鲸鱼图标。

## 许可

代码部分使用 [MIT License](LICENSE)。图标素材的版权见 [NOTICE](NOTICE)。
