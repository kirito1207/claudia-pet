# claudia-pet 开发交接

更新：2026-11-05。本文描述当前工作区。0.2.0 已发版；动画动作这一批已提交、推送到 main，**还没发版**（等补帧做完再发 0.3.0）。下一步见文末「下一步（2026-11-05 交接）」。

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
- `tumble.js`：被扔出去碰边时演不演、演哪套（概率 n×50%÷7 封顶 50%，一次扔出最多一次）。碰顶边撞头，碰左右边撞头 / 摔倒 / 摇晃各 1/3，碰底边 6 成摔倒 4 成摇晃；撞头在撞上时换图，其余停稳后才演；每个动作先演开场再接循环动作（同 CrossPet 的 m-jolt 写法）；演完才结算情绪，演过就不再晕乎乎；干活时只闪 0.8 秒，等回答时只说一句，多会话不触发。
- `Pet.Notices.cs` / `TaskNoticePolicy.cs`：通知分类、前台过滤和去重。新版插件按会话保存短事件队列；旧会话兼容状态文件提醒。

## 动画动作（claude-pet 动作包）

2026-11-05 实现，已装到本机，已提交到 main（未发版）：
- 20 个姿态（待机、思考、写东西、敲命令、整理记忆、睡觉、开心、吃惊、被摸头、不耐烦、再见、哼歌、伸懒腰、打哈欠和六个节日）改播 hunmer 的 claude-pet 动作包精灵图，文件原样放在 `characters/claude/anim/`（24 张，约 45 MB），来源和每张的用途见其中的 `SOURCE.md`。作者已在 hunmer/aboutme-pets#1 书面授权（CC BY-NC-SA 4.0，署名 hunmer）。
- `Store.Manifest` 把 `anim\` 里的文件交给网页（`sheets`），`SpritePath` 多认 `/characters/<角色>/anim/<图>` 这一种地址。
- 播放在 `index.html` 的 `anim` / `playSheet` / `sheetTick`：两块 canvas 交叉淡入，按人物高、脚底、中线和静态立绘对齐（作者每个动作第 1 帧人物在 640×360 帧里的 x 206–444、y 40–321）。配置在 `character.json` 的 `animations`（sheets / from / to / loop / hold / full）。
- 被替换的 22 张静态立绘已移出项目，本机备份在 `build/旧立绘备份-已被动画替换/`（不进 git，附哈希和说明），只留 `idle.webp`、`idle-blink.webp`：程序靠待机图识别角色，减少动画、读不到动画时用它兜底；省电模式下只有还有静态图的待机 / 累了退回静态。原来的「动画动作」开关已去掉（网页里 `animOn` 还在，外壳不再发）。
- 测试：`scripts/test-anim.cjs`。
- 没有动画的姿态和哪些要重画，见 `docs/立绘比例统一清单.md`。本地试验版在 `build/aboutme-src`、`build/aboutme-test`（不进 git）。

- 同日又加了动作包里的新动作（共 41 张、约 78 MB）：拖着她时盖一层「被拎起来」（`dragSheet`，松手回到原来的常态动画，被甩出火气后不盖）；闲置随机的发呆小动作 / 魔法 / 偷吃零食 / Claude 梗四组（设置「日常 → 允许的动作」里按组开关，`idle-policy.js` 的 `fidget`、`magic`、`snack`、`claude`）；偷吃时点她接被抓包（`snack_caught`）；余额按剩得最少的窗口分五档，换档时演对应钱袋（`quotaTier`）。这些只有动画、没有静态图；右键菜单「动作 → 小动作 / 余额」可以预览。新动作的台词是 Claude 起草的，用户还没审。

- 同日再加：动作包里其余的闲置内容（才艺、玩耍、吃喝、时令共 37 个，加上动物环绕、蝴蝶蜜蜂）和走路（共 81 张、约 153 MB）。
  - 时令：`animations[].when` 按月份、几点、公历日期、农历日期（`Intl` 的 chinese 历，闰月不算）筛选；`weight` 调当天专属动作的权重。
  - 闲置抽签改成先在组里平均抽一组，再在组里按权重抽，动画小动作多了也不挤掉哼歌这些。
  - 走路：`startWalk` / `walkEnded` / `stopWalk`（网页）+ `Pet.Walk.cs`（外壳逐帧平移窗口，只走水平、不出 Claude 窗口，碰边或走够就停并记位置；被拖、隐藏、换状态都停）。动画进入 `cycle` 步伐循环才开始挪；奔跑只朝左，不镜像。这一步自动测试测不到外壳，要在桌面上看。

- 衔接审查（2026-11-05）见 `docs/动作衔接审查.md`：加了离场动作 `animations[].exit` / `leaveVia`（睡觉被点醒或要干活时先演醒来，偷吃没被抓先收起零食），不耐烦演满整段，落叶只用第二版（现在共 80 张），螃蟹步从第 8 帧起步；走路收尾用 `outro`。

- 20 张静态立绘已按动画比例重画并换进项目（2026-11-05，用的是第二轮；第一轮腿太长，备份在 `build/第一轮重画-偏瘦高/`）：原图在 `D:\claudia图codex生成`（编号_720.png 原图、_0.png 压缩图），`prepare-sprites.py --only` + 表 `STANDING` 处理，每张单独缩放到和待机图一样高（帽子不算）；旧图备份在 `build/旧立绘备份-比例统一前/`。对照表在 `docs/prompts/立绘重画-统一比例/提示词.md` 的生成记录。

- 节日（2026-11-05）：国庆删除（立绘、台词、特效、菜单、`festivalOf` 里的日期都去掉了）。修了节日只说台词不演动画：`timeGreet` 原来只认静态立绘，节日换成动画后就不演了，改成有动画也算。作者以后生成的放烟花（元旦）、吃饺子 / 收红包 / 写福字（春节，和舞狮头随机轮换）要加，记在 `docs/立绘比例统一清单.md` 第六节。

## 下一步（2026-11-05 交接）

用户定的顺序：**先做衔接用的补帧图，做完再发 0.3.0**。

1. **补帧（静态图按顺序翻页播放）**
   - 现在同一姿态有多张图时，程序是每 ~8 秒随机换一张（`index.html` 里「生动一点」那段 `setInterval`），不连贯。要改成可配置的顺序快切（例如 `character.json` 里给静态姿态写帧顺序和每张停多久，0.3–0.6 秒），这部分还没写。
   - 优先级（详见 `docs/立绘比例统一清单.md` 第五节、`docs/动作衔接审查.md`）：
     - 高：碰边一套（撞头加「捂头揉头」、趴地 / 摔倒加「撑起上身」、爬起加「快站直、拍裙子」作为接回待机动画的过渡）；生气一套（红温加跺脚冒气 1–2 张、赌气加「偷偷回头瞄」、原谅你加「放下书露出笑脸」）。
     - 中：会停很久的听你说、看书、累了、有事问你、查资料各补 1–2 张小变化（点头、翻页、小哈欠、歪头）。
     - 不补：问候、节日、得意、道歉、哎呀、抱礼物；忙碌左右已有两张交替。
   - 碰边那几张和红温、有事问你、道歉、派活、得意、抱礼物、抱 Clawd 还是旧比例；补帧时按新比例画（提示词照 `docs/prompts/立绘重画-统一比例/提示词.md` 的「通用部分」，比例段以改正后的为准：裙摆下只露一小截白袜，约鞋高一半，不要瘦高）。补帧的提示词文档还没写。
   - 用户在 Codex 里生图，放到 `D:\claudia图codex生成`（`编号_720.png` 原图、`_0.png` 压缩图，名字是自动编号）。Claude 按修改时间认新一批、按画面认姿态，用 `scripts/prepare-sprites.py --only` 处理（站立的加进表 `STANDING`，坐 / 趴的用 `SPRITES` 的系数），旧图先备份到 `build/`。用户说过认图后直接处理，不用再确认对照表。
2. **发 0.3.0**：补帧做完后。版本号改 `VERSION` 和 `plugin/.claude-plugin/plugin.json`，Release zip 照 0.2.0 的做法（带 WebView2 许可），发版记录写进本文和 `docs/PUBLISHING_AUDIT.md`。动画约 150 MB，zip 会比 0.2.0 大很多。
3. **等作者更新**（hunmer/aboutme-pets，现在停在 `3ba083b`）：放烟花（元旦，替换静态图）、吃饺子 / 收红包 / 写福字（春节，和舞狮头随机轮换），见 `docs/立绘比例统一清单.md` 第六节。新动作同样适用书面授权。
4. 还没确认的：走路速度、中秋画面中途人物左移是否接受、Claude 起草的新动作台词（用户未审）。

本机留着、不进 git 的：`build/旧立绘备份-已被动画替换/`、`build/旧立绘备份-比例统一前/`、`build/第一轮重画-偏瘦高/`（都有说明和哈希）、`build/aboutme-claude/`（下载的动作包原件和对照图）、试验副本 `build/aboutme-src`（git worktree，detached，用完可 `git worktree remove build/aboutme-src`）和 `build/aboutme-test`。

## 碰边动作（撞头 / 摇晃 / 摔倒 / 趴地 / 爬起）

2026-11-04 实现，随 0.2.0 发布：
- 立绘由 `scripts/prepare-sprites.py` 从 Codex 原图（本机 `D:\claudia图codex生成`）生成到 `characters/claude/`。
- 碰边判断在 `web/tumble.js`，播放和衔接在 `index.html` 的 `tumble*` 函数，`pet-motion.js` 报出碰到哪条边。
- 设置开关 `tumbleActions` 默认打开，右键「动作 → 互动」里能单独预览。

台词已由用户审定写进 `character.json`；被晃出火气（或在赌气）时改说 `…Angry` 生气版台词，爬起来再发火。特效在 `effects.js`：撞头时头顶转星星（`bonkStars`），摔倒、站不稳、趴地着地时扬一次灰（`landingDust`）。

还差：
- 实际甩动的手感（速度下限 200 像素/秒、各段时长）等用户实测后再调。

完整设计见本地文件 `build/new-sprites/交接-新动作.md`（不进 git），提示词和附图已提交在 `docs/prompts/`。

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
& $node scripts/test-anim.cjs
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

已清理旧内部命名、其他角色特效、旧层级策略、闲置反馈／皮肤卡片实现、空额度任务及过时注释。额度来自插件文件；没有自动更新器和额度提前恢复庆祝。来源和许可保留。验证与发布状态见 [发布审计](docs/PUBLISHING_AUDIT.md)。2026-11-04 已发布为公开仓库 https://github.com/kirito1207/claudia-pet（远端 `origin`，默认分支 `main`）。同日删库重建过一次：最初推上去的两个带 Claude 署名的提交改写后仍留在 GitHub、首页贡献者一直显示 claude，用户自己删了仓库，再用同名、同简介新建公开仓库，只推 `main`；核对后首页贡献者只有 kirito1207，旧提交 `af6e614`、`de92ba8` 已查不到。2026-11-04 发布了 0.2.0（GitHub Release `v0.2.0`，附解压即用的 `claudia-pet-0.2.0-win.zip`，详见发布审计）；以后发版：改 `VERSION` 和 `plugin/.claude-plugin/plugin.json`，构建后把 `dist/claudia-pet` 打成 zip 附上。整理前的完整开发历史在本地分支 `archive/full-history`，去掉 Claude 署名前的两个提交在 `archive/before-strip-coauthor`，都只留本机，不要推送：以后推送只推 `main`，不要用 `git push --all`。本项目的 `.claude/settings.local.json`（不进 git）放行了 `gh repo create kirito1207/claudia-pet *` 和 `git push -u origin main` 两条命令，并关闭了提交和 PR 的 Claude 署名（用户不希望 Claude 出现在 GitHub 贡献者里）。
