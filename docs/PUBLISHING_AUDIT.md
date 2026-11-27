# claudia-pet 发布前现状与残留审计

检查日期：2026-11-04。检查对象为当前本地工作区、安装产物及 Git 状态。本次已清理代码残留并更新说明，公开用的 Git 历史已整理为单个初始提交，并已发布到 GitHub。

## 图标与产品名称

| 项目 | 实际结果 |
|---|---|
| `icon/AppIcon.png` | 已是 Claudia 抱 Clawd 的单人透明头像 |
| `icon/icon-source.webp` | 与新头像一致的源素材版本 |
| `icon/AppIcon.ico` | 新图标，包含 16–256 像素的九种尺寸 |
| 本机安装的程序与托盘 ICO | 已替换；安装 ICO 与当前源码一致，已检查编译后 EXE 的图标 |
| 当前生成程序的产品名／文件描述 | `claudia-pet`；文件名为 `claudia-pet.exe`（桌宠角色本身仍叫 `claudia`） |
| 版本 | `0.4.2`（2026-11-16 发布；`0.4.1` 为 2026-11-10 晚；`0.4.0` 为 2026-11-10 上午，用户定跳过 0.3.1；0.3.0 为 2026-11-07；0.2.0 为 2026-11-04 用户定，和改名前叫 Claudia 的 0.1.0 区分）。`VERSION` 和 `plugin/.claude-plugin/plugin.json` 同步 |

当前文件没有继续使用四头像软件图标。旧图标只留在本地备份（`build/`）和本地归档分支里，不在公开历史中。

## 已完成的残留清理

- `CrossFx` 和菜单旧前缀改为 Claudia 命名，调用点同步更新。
- 删除未使用的其他角色泡泡、吃饭、价签特效和姿态映射，保留 Claude 当前动作、金色闪光和绘画特效。
- 删除旧显示层级过渡、前台程序角色路由、全屏／遮挡判断及未调用的置顶辅助代码；保留 Claude 窗口跟随和手动隐藏所需实现。
- 删除空的 `RefreshQuota()`、定时调用、`Pet.Quota.cs` 和未调用的反馈记录工具 `Ledger.cs`。
- 简化 Claude 额度条，删除多供应商额度池选择，以及没有接入当前插件数据的提前恢复庆祝逻辑；保留额度条、文字、重置时间和低额度提醒。
- 删除设置页已无入口的换装命令、皮肤清单和角色卡片图像接口；保留动作素材扫描及头像／展示图的排除规则。
- 删除旧 macOS 应用标识、其他供应商事件别名和过时的跨平台／设计文档注释。
- 简化程序集产品描述和 Company，来源信息保留在关于页与许可说明中。

## 应保留的来源信息

- LICENSE 内的原版权与 MIT 许可。
- NOTICE.md、README 和关于页中简洁的 CrossPet、dsh-pet 来源说明。
- `characters/LICENSE.md` 的原始角色授权；当前只含 Claude 素材的范围说明在 `characters/README.md`。
- `upstream/CrossPet-LICENSE` 和来源版本记录。
- 新图标的角色素材来源、派生说明及授权。

这些信息是项目来源记录，不表示运行程序仍叫 CrossPet。

## Git 与发布状态

- 当前分支为 `main`，远端 `origin` 为公开仓库 https://github.com/kirito1207/claudia-pet。
- 2026-11-04 按用户决定整理了公开历史：`main` 只有一个初始提交「claudia-pet 0.1.0」，文件内容与整理前最后一版完全一致。早期提交的交接文档含本机路径和本机配置细节，因此不公开。
- 整理前的完整开发历史保存在本地分支 `archive/full-history`，只留在本机：推送时只推 `main`，不要用 `git push --all`。
- 初始提交前检查过：已跟踪文件中无构建产物、大文件、密钥、本机用户路径或个人邮箱；提交作者使用 GitHub noreply 邮箱。
- 已确认 `build/`、`dist/`、`.claude/` 没有已跟踪文件，并受忽略规则覆盖。本地旧图标／程序备份位于 `build/`，不要打入发布包。

## 验证记录

清理后通过 C# 编译、真实 WPF 窗口位置交接与过期帧过滤、通知策略测试，以及 JavaScript 物理、行为、界面和情绪场景检查。浏览器测试覆盖额度隔离、设置布局、点击衔接、水平甩出、停稳延迟及工作／等待回答优先级。

当前头像与托盘 ICO 保持 Claudia 抱 Clawd 的版本。构建产物为 `dist/claudia-pet`。本机更新通过不带 `-Connect` 的安装流程完成，保留用户设置及接入配置。真实 Windows 通知、多显示器／DPI、完整 Claude 生命周期的验证边界仍见 HANDOFF。

2026-11-04 提交前复测（Node v24.22.1）：`test-physics`、`test-behavior`、`test-mood`、`test-ui` 四项 Node 测试，正式版编译，以及 `-MotionTests` 的通知策略和 WPF 位置交接测试均通过。改名为 claudia-pet 后再次全部通过；更新关于页后重跑界面测试通过，并已重新安装到本机。

2026-11-04 已发布：用 `gh repo create` 新建公开仓库 `kirito1207/claudia-pet`，只推送了 `main`。推送后核对远端：公开、默认分支 `main`、只有 `main` 一个分支、只有初始提交「claudia-pet 0.1.0」（与本地一致）、127 个文件；README、NOTICE、LICENSE、`characters/LICENSE.md`、`icon/README.md`、`upstream/CrossPet-LICENSE` 均在。`archive/full-history` 未推送。之后又推送了一次只改交接文档和本审计的更新。

同日按用户要求去掉了提交说明里的 `Co-Authored-By: Claude …` 署名行（GitHub 曾因此把 Claude 官方账号列为贡献者）：两个提交只改说明、不改文件，用带保护的强制推送覆盖远端，此时仓库 0 fork、0 star。核对后远端贡献者只有 `kirito1207`，文件仍为 127 个。改写前的版本备份在本地分支 `archive/before-strip-coauthor`，未推送；本项目 `.claude/settings.local.json` 已关闭提交和 PR 署名。

同日稍晚发现：贡献者接口只有 `kirito1207`，但仓库首页右侧栏仍显示 `claude`。原因是改写前的两个提交（`af6e614`、`de92ba8`，带 `Co-Authored-By: Claude …`）已推送过，强制推送后不在任何分支上，但仍能按提交号在 GitHub 上打开。处理：仓库 0 star、0 fork、没有 Issue 和 PR，用户在 GitHub 设置里自己删除了仓库，再用 `gh repo create` 以同名、同简介、公开新建，只推送 `main`（当时为 `6ec4917`）。核对后：远端只有 `main`，与本地一致；首页贡献者只有 `kirito1207`；两个旧提交按提交号查询返回 HTTP 422（找不到）。

2026-11-04 发布 0.2.0：版本号提交为 `02f0973`「claudia-pet 0.2.0」，构建脚本从此把 WebView2 包里的 `LICENSE.txt`、`NOTICE.txt` 复制成程序目录下的 `WebView2-LICENSE.txt`、`WebView2-NOTICE.txt`（该包许可允许二进制再分发，须附版权声明和免责声明）。发布前全部测试、WPF 窗口测试和构建都通过；zip 内 98 个文件，扫过无本机路径、邮箱或密钥。用 `gh release create` 建了正式 Release `v0.2.0`（标签指向 `02f0973`，非草稿、非预发布），附件 `claudia-pet-0.2.0-win.zip`（7,065,380 字节，SHA-256 `F4D47926C58677F0C1C53C30C907F8F0CB309B0F6A2FA0184EE29003175A18EC`）。发布后下载回附件比对，校验值一致。未用 zip 里的程序单独启动验证。

2026-11-07 发布 0.3.0：工作树分支 `claude/kind-blackburn-304e7f` 上的 8 个提交（光晕改脚本驱动、精灵图后台解码、撞头第一帧、Codex 审查修复、点击区域上报与移窗去重、窗口切换防闪、中间帧 / 动画衔接 / 火气 / 额度显示、版本号）按用户要求快进推送到 `main`（`818c2f8..02132ad`），只推了 `main`。提交前扫过改动和新增文件：无本机用户路径、邮箱或密钥（Codex 报告里一处本机路径改成了相对说明）；提交作者为 GitHub noreply 邮箱，没有 Claude 署名行。发布前 7 个网页测试、`-MotionTests`（通知策略、点击区域缩放、WPF 位置交接）和正式构建都通过。zip 内 174 个文件（不含构建标记 `.claudia-pet-build-output`），扫过无本机路径、邮箱或密钥。用 `gh release create` 建了正式 Release `v0.3.0`（标签指向 `02132ad`，非草稿、非预发布），附件 `claudia-pet-0.3.0-win.zip`（162,825,130 字节，SHA-256 `5D02F4B0BB22CE5BCD3F0C1D60842D9A55D77AC17ED3B08FA3F40E2590465645`）。发布后核对：下载回附件校验值一致，`releases/latest` 为 `v0.3.0`，远端只有 `main` 一个分支，贡献者只有 `kirito1207`。动画素材占了包的大部分（0.2.0 为 7 MB）。未用 zip 里的程序单独启动验证；本机安装的是同一份 `dist`。

2026-11-10 发布 0.4.0（用户定：不发 0.3.1，直接 0.4.0）：0.3.0 之后的提交（作者 82 段新动画、托盘双击、去掉「允许的动作」、右键菜单宽度和闪烁修复、随机动作精简与合并进问候 / 元旦、菜单重排）加上版本号提交 `1c6f3da`「claudia-pet 0.4.0」，只推了 `main`。提交前扫过改动：无本机用户路径、邮箱或密钥，提交作者为 GitHub noreply 邮箱，没有 Claude 署名行；工作区里另一个工具留下的未跟踪交接文件没有提交。发布前 7 个网页测试、`-MotionTests` 和正式构建都通过。zip 照 0.3.0 打包（顶层文件夹 `claudia-pet/`，带 WebView2 许可，不含构建标记），233 个文件（146 张动画精灵图），扫过无本机路径、邮箱或密钥；这次另外把 zip 解压到临时目录单独启动过一次，日志为「启动 0.4.0」、网页正常加载，之后换回安装版。用 `gh release create` 建了正式 Release `v0.4.0`（标签指向 `1c6f3da`，非草稿、非预发布；`--target` 要写完整哈希，短哈希会被拒），附件 `claudia-pet-0.4.0-win.zip`（290,089,043 字节，SHA-256 `5A1E20D1314F94FBAEB1AFBB76C17ECB48EC18AE6C5CAC5E58F02BDB34746836`）。发布后核对：下载回附件校验值一致，`releases/latest` 为 `v0.4.0`，远端只有 `main`，贡献者只有 `kirito1207`。发布说明写了「升级先删旧文件夹内容再解压」：这一版删了动画和静态图，直接覆盖会留下旧文件。

2026-11-10 晚发布 0.4.1（用户定）：0.4.0 之后的三个提交（`4078321` 窗口跟随改成 Claude 窗口一挪就摆、`bb20960` 桌面模式和多开及设置里的「多开」页、`26728d0` 新加的可以一起送走而不关第一只）加上版本号提交 `5873fc0`「claudia-pet 0.4.1」（同时在 README 里补了「待在哪和多开」），只推了 `main`。提交前扫过这一版的改动：无本机用户路径、邮箱或密钥，提交作者为 GitHub noreply 邮箱，没有 Claude 署名行；工作区里另一个工具留下的未跟踪交接文件没有提交。发布前 7 个网页测试、`-MotionTests` 和正式构建都通过；这些测试覆盖不到跟随、桌面模式和多开，那三样由用户在本机桌面上实测确认（多显示器没有测）。zip 照 0.4.0 打包（顶层文件夹 `claudia-pet/`，带 WebView2 许可，不含构建标记），233 个文件（146 张动画精灵图），扫过文本文件无本机路径、邮箱；这次没有另外解压到临时目录单独启动，本机安装的是同一份 `dist`。用 `gh release create` 建了正式 Release `v0.4.1`（标签指向 `5873fc002436dcad1acd840b26256b029c326706`，非草稿、非预发布），附件 `claudia-pet-0.4.1-win.zip`（290,092,207 字节，SHA-256 `E6FD96C2DA2FA9F7AA7DE7CE7BACC4323FE8D821B47078D35FADD2F02E928227`）。发布后核对：GitHub 给附件算的 SHA-256 和本地一致（没有再下载回来校验），`releases/latest` 为 `v0.4.1`，远端只有 `main`，贡献者只有 `kirito1207`。这一版没有删素材，从 0.4.0 升级可以直接覆盖。

2026-11-16 发布 0.4.2（用户定）：0.4.1 之后的提交 `0c01bbf`（交接文档）、`5fedb5d` 会话没报认得的额度时清空额度、`872a817` 圣诞加入装点圣诞树 v2 并改四句台词、`f2dc3d0` 运行方式（跟着 Claude / 独立运行，含 README），加上版本号提交 `9fc0e3a`「claudia-pet 0.4.2」，只推了 `main`。推送前扫过 `origin/main..HEAD` 的改动：无本机用户路径、邮箱或密钥，提交作者为 GitHub noreply 邮箱，没有 Claude 署名行；工作区里另一个工具留下的未跟踪交接文件没有提交。发布前 7 个网页测试、`-MotionTests` 和正式构建都通过；运行方式另在单独数据目录里做了桌面实测（五个场景，见 HANDOFF），独立运行时关掉 Claude 那一步没有实测。zip 照 0.4.1 打包（顶层 `claudia-pet/`，带 WebView2 许可，不含构建标记），234 个文件（147 张动画精灵图），扫过文本文件无本机路径、邮箱。第一次 `gh release create` 在上传附件时中断，只留下没有附件、没有标签的草稿；随后用 `gh release upload` 把附件传到这份草稿、再 `gh release edit --draft=false --latest` 发布，没有另建重复的 Release。Release `v0.4.2`（标签指向 `9fc0e3a45f23e7133471a31ae453bacff7f9c157`，非草稿、非预发布），附件 `claudia-pet-0.4.2-win.zip`（292,069,259 字节，SHA-256 `284E6F2F0704AAB0DECE85A75C4355D39D087C4E3F9C6D4A316DAAC43CCF0D86`）。发布后核对：GitHub 给附件算的 SHA-256 和本地一致（没有再下载回来校验），`releases/latest` 为 `v0.4.2`，远端只有 `main`，贡献者只有 `kirito1207`。没有删素材，从 0.4.1 升级可以直接覆盖。

2026-11-28 发布 0.5.1（用户定版本号，跳过 0.5.0）：0.4.2 之后 53 个提交，合并同类是 9 项：Linux 版（后改为 PySide6 + Qt WebEngine）、一批新动画并改成只播透明 WebM（删掉精灵图）、多开重写（每只平级、负责人交接）、桌面模式全屏让开和鼠标穿透、设置页六页和首次默认独立运行、Q 弹和点击手感（含碰边、点醒）、拖拽松手修复（Windows 用鼠标钩子取松手时刻，Linux 按最后一次还按着时收尾）、Windows 换成 DWM 合成窗口，以及检查更新（默认开，一天最多一次问 GitHub releases/latest，只提醒不下载；设置页和 README 的「不联网」文案相应改了）。另有 `fec9d4a` 检查更新和版本号提交 `3d5f962`「Bump to 0.5.1; stop tracking the local handoff notes」（`HANDOFF.md` 从此不再由 Git 跟踪，本地保留）。推送只推了 `main`，由用户在本机推送；推送前提交作者为 GitHub noreply 邮箱，没有 Claude 署名行。发布前 7 个网页测试、`-MotionTests` 和正式构建在 Windows 上都通过，Linux 单元测试通过；Linux 拖拽松手、多开由用户实机确认。zip 照 0.4.2 打包（顶层 `claudia-pet/`，带 WebView2 许可，不含构建标记），这次在 Linux 一侧用 Python 从同一份 `dist/claudia-pet` 打包，221 个文件，`testzip` 通过、包内 `VERSION` 为 0.5.1，扫过文本文件无本机路径、邮箱；没有另外解压单独启动。Linux 不另外打包：说明里让 Linux 用户下载 Release 自带的 Source code，再运行 `linux/install.sh`。`gh release create --draft` 建草稿并上传附件后，本机暂时连不上 api.github.com，由用户在浏览器里核对并发布。Release `v0.5.1`（标签指向 `3d5f962676eef9c0a0ce7938a5c006bb2cf07bc4`，非草稿、非预发布），附件 `claudia-pet-0.5.1-win.zip`（261,586,166 字节，SHA-256 `410A65073B41A1CD5F66CA772F9680A23861E351B60E157BD2682F2D4030307B`）。发布后核对：GitHub 给附件算的 SHA-256 和本地一致（没有再下载回来校验），`releases/latest` 为 `v0.5.1`。远端分支除 `main` 外还有 `codex/pr6-animation-and-characters`、`linux-port`；贡献者为 `kirito1207` 和 `littlepec`（合并 PR #7 带来的）。这一版删了精灵图和一批静态图，说明里写了升级先删旧文件夹内容再解压；0.4.2 及更早留在数据目录的内置角色副本会按发布包指纹自动挪到备份。
