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
| 版本 | `0.4.0`（2026-11-10 发布，用户定跳过 0.3.1；0.3.0 为 2026-11-07；0.2.0 为 2026-11-04 用户定，和改名前叫 Claudia 的 0.1.0 区分）。`VERSION` 和 `plugin/.claude-plugin/plugin.json` 同步 |

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
