# claudia-pet 开发交接

更新：2026-11-04。本文描述当前工作区，不代表所有修改已经提交或发布。

## 项目定位

Windows 上独立维护的 Claude 桌宠。基于 CrossPet，参考 dsh-pet；来源与许可集中在 NOTICE.md、LICENSE 和素材说明中。关于页只简要介绍来源，不列出功能删改历史。

命名（用户 2026-11-04 定）：仓库名、项目名、软件名都是 `claudia-pet`（GitHub 仓库名同此；程序 `claudia-pet.exe`、安装目录 `Programs\claudia-pet`、插件名、MCP 配置项、窗口和托盘标题）。只有桌宠角色叫 `claudia`。数据目录 `~/.claudia` 和代码内部名称（C# 命名空间 `Claudia`、互斥锁、`claudia.app` / `claudia.data` 虚拟域名、localStorage 键）按用户选择没有改。0.1.0 叫 Claudia（`Programs\Claudia`、MCP 项 `claudia`），接入和撤销时会清掉这两个旧条目，卸载脚本也会删旧目录。

默认名牌为 `claudia`，支持用户改名。当前图标为 Claudia 抱 Clawd 的透明头像，程序与托盘使用同一份 ICO。

## 目录与组成

| 位置 | 职责 |
|---|---|
| `app/src/` | WPF、WebView2 外壳，跟随窗口、接入配置、通知、托盘和设置 |
| `web/` | 人物、气泡、特效、互动、物理驱动与设置页 |
| `plugin/` | Claude Code hooks，把状态、额度和通知写到本机 |
| `characters/claude/` | Claude 娘立绘、台词和角色配置 |
| `icon/` | 抱 Clawd 图标及生成说明 |
| `scripts/`、`tests/` | 构建、安装、卸载和验证 |
| `upstream/` | 原项目许可和版本来源记录 |

运行数据在 `%USERPROFILE%\.claudia`；安装目录为 `%LOCALAPPDATA%\Programs\claudia-pet`。数据放在用户主目录，以避开商店版 Claude 对部分 AppData 写入的重定向。

## 关键行为

- `Pet.Follow.cs`：识别 Claude 主窗口，按物理像素保存相对位置、限制范围和设置层级；不置顶于其他应用。
- `Ensure.cs` / `Mcp.cs`：通过 `--ensure` / `--mcp` 随 Claude 启动；启动路径使用独立进程以免随 Code 子进程树被回收。
- `Store.cs` / `SpriteResources.cs`：设置、角色同步、清单与图片资源。
- `Pet.Physics.cs`：只转发鼠标屏幕坐标、参数、边界及消息代次，接收网页位置并移动 HWND。拖动和飞行时窗口线程很忙，而 WPF 里鼠标输入的优先级低于网页消息和脚本回调，松开消息曾晚到 0.15～0.2 秒，导致甩不动、乱飞、松手有延迟。所以（2026-11-04 修）：轨迹点用 `GetMouseMovePointsEx` 取系统鼠标历史（真实坐标和发生时间），每帧统一发给网页一次；松手由每帧检查物理按键状态判断，不等松开消息；开始拖动前也确认按键确实还按着。`pet-motion.js` 松手时剔掉松手之后的点，再按 dsh-pet 原样取松手前 200ms 的轨迹算速度。
- `dsh-physics.js`：直接摘录的 dsh-pet 物理函数；`pet-motion.js` 用网页帧循环驱动拖动和飞行。不要再混入另一套 C# 物理公式。
- `idle-policy.js` / `Pet.Behavior.cs`：动作池及频率，安静／标准／活泼约为 90／45／20 秒，带 ±20% 浮动。
- `click-sequence.js`：220ms Q 弹后才执行原有点击互动；连点保留摸头次数，新工作或拖动优先。
- `motion-mood.js`：移动期间保存怒气，停稳 800–1200ms 后处理。工作、等待回答及多会话优先；最小化或手动隐藏时延后结算。普通飞行和碰壁不额外加怒气。
- `Pet.Notices.cs` / `TaskNoticePolicy.cs`：通知分类、前台过滤和去重。新版插件按会话保存短事件队列；旧会话兼容状态文件提醒。

## 构建与验证

```powershell
pwsh -NoProfile -File scripts/build.ps1
```

构建脚本使用 PowerShell 7 自带的 Roslyn，引用 .NET Framework 4.8 和下载的 WebView2 SDK。默认生成 `dist/claudia-pet`。

JavaScript 验证需要 Node.js（本机使用 24 系列）。界面和情绪测试还需要可解析的 Playwright 模块和已安装的 Microsoft Edge。

本机 PATH 里没有 `node`，用 Codex 自带的 Node v24.22.1 和它的 Playwright；变量只在当前窗口生效，不改系统配置。该路径随 Codex 更新可能变化，使用前先核实：

```powershell
$nodeDir = "$env:USERPROFILE\.cache\codex-runtimes\codex-primary-runtime\dependencies\node"
$node = "$nodeDir\bin\node.exe"
$env:NODE_PATH = "$nodeDir\node_modules"
& $node scripts/test-physics.cjs
& $node scripts/test-behavior.cjs
& $node scripts/test-ui.cjs
& $node scripts/test-mood.cjs
pwsh -NoProfile -File scripts/build.ps1 -Out build/native-tests -MotionTests
```

`scripts/test-physics.ps1` 直接调用 `node`，PATH 里没有 Node 时会失败，改用上面的写法。

`test-physics.cjs` 可额外接收原版 dsh-pet `shared-core.js` 路径进行数值对照。`-MotionTests` 使用未显示的 WPF 窗口检查松手位置、过期帧过滤及通知策略，不改用户配置。

## 验证边界

- 用户已确认当前抛掷手感；数值对照、浏览器帧循环、交接与情绪场景已做自动检查。
- Windows 系统通知的实际弹出尚未通过真实 Code 会话完整验收；通知受 Windows 设置影响。
- 2026-11-04 用户完全退出并重开 Claude 后验证：Claude 16:38:57 启动，16:39:02 MCP（`claudia-pet.exe --mcp`）以独立进程拉起桌宠；新插件路径同时生效，状态写入 `~/.claudia\state`。Claude 自动更新、右键退出后同一次运行不再出现（`user-quit`）仍未验证。
- 多显示器、不同 DPI 和 Windows 10 尚未完整验收。

## 修改与发布边界

保留现有用户设置、已接受的抛掷手感、来源说明和许可原文。普通修改不需要重做接入；`install.ps1` 不加 `-Connect` 可更新程序而不改 Claude 接入配置。

已清理旧内部命名、其他角色特效、旧层级策略、闲置反馈／皮肤卡片实现、空额度任务及过时注释。额度来自插件文件；没有自动更新器和额度提前恢复庆祝。来源和许可保留。验证与发布状态见 [发布审计](docs/PUBLISHING_AUDIT.md)。2026-11-04 已发布为公开仓库 https://github.com/kirito1207/claudia-pet（远端 `origin`，默认分支 `main`）。整理前的完整开发历史在本地分支 `archive/full-history`，去掉 Claude 署名前的两个提交在 `archive/before-strip-coauthor`，都只留本机，不要推送：以后推送只推 `main`，不要用 `git push --all`。本项目的 `.claude/settings.local.json`（不进 git）放行了 `gh repo create kirito1207/claudia-pet *` 和 `git push -u origin main` 两条命令，并关闭了提交和 PR 的 Claude 署名（用户不希望 Claude 出现在 GitHub 贡献者里）。
