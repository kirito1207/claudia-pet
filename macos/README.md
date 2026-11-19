# claudia-pet macOS 版

照 Linux 版（`linux/`）改的：外壳换成 Python + PyObjC（macOS 自带的 AppKit 和 WKWebView）。网页（`web/`）、立绘（`characters/`）和 Claude Code 插件（`plugin/`）三版共用，数据目录也是同一个 `~/.claudia`。

需要 macOS 11 以上，Intel 和 Apple 芯片都行。有问题看下面「出问题时」。

## 安装

1. 装 Xcode 命令行工具（已经装过会提示 already installed，跳过就行）：

   ```bash
   xcode-select --install
   ```

   它带着 Python 3，安装脚本还要用它里面的 `cc` 编一个小启动器，做出「应用程序」里的 claudia-pet.app。
   不想装的话，有 Homebrew 或 python.org 的 Python 3.9 以上也能装，只是没有 .app，从命令行打开。

2. 在仓库根目录执行：

   ```bash
   sh macos/install.sh
   ```

   第一次会建一个 Python 虚拟环境、联网装 PyObjC（一两分钟）。网络不好可以换镜像：

   ```bash
   PIP_INDEX_URL=https://pypi.tuna.tsinghua.edu.cn/simple sh macos/install.sh
   ```

程序装在 `~/.local/share/claudia-pet`，命令是 `~/.local/bin/claudia-pet`，「应用程序」里也会出现 claudia-pet.app（启动台、聚焦搜索都能打开；Intel 和 Apple 芯片上都是原生运行，不用 Rosetta）。菜单栏右上角有她的图标，点开就是右键菜单，最上面一项是「叫她出来」。不需要 sudo，也不会加开机自启。加 `--connect` 可以同时接入 Claude Code 插件和 Claude 桌面端。

不安装、直接从仓库运行也可以（要自己先把 PyObjC 装进 `python3`：`pip install pyobjc-framework-WebKit`）：

```bash
./macos/claudia-pet
```

卸载（加 `--keep-data` 保留 `~/.claudia`）：

```bash
sh ~/.local/share/claudia-pet/macos/uninstall.sh
```

## 接入 Claude

和 Windows 版一样，在右键「设置…」→「接入 Claude」里点「接入」：

- **Claude Code 插件**：改 `~/.claude/settings.json` 的 `env.CLAUDE_CODE_PLUGIN_DIRS`（多个目录用冒号隔开）。新开的 Code 会话生效。插件会去找 `~/.local/bin/claudia-pet`，和 Linux 版是同一个位置。
- **Claude 桌面端（MCP）**：改 `~/Library/Application Support/Claude/claude_desktop_config.json`。完全退出 Claude（⌘Q）再打开后生效。

改之前都会先备份，备份名是 `原文件名.bak-claudia-时间`。命令行也能接入：`claudia-pet --connect all`。

干活、做完的动作只跟着 Claude Code 走（桌面端里的 Code，或者终端里的 `claude`）；普通对话里她只是陪着。

## 和 Windows 版不一样的地方

| 功能 | macOS 版 |
|---|---|
| 待在 Claude 窗口里 | **还没做**（和 Linux 版一样），都待在桌面上，设置里这个选项是灰的 |
| 窗口 | 不抢焦点的浮动面板（NSPanel），层级在菜单栏、程序坞之上，每个桌面空间、全屏程序上都在；不出现在 ⌘Tab 和程序坞里 |
| 打开时 | 窗口先完全透明，等人物立绘画好再显示 |
| 点击穿透 | 窗口平时整个不收鼠标，每 40 毫秒看一次鼠标在不在人物、气泡、名牌、额度上，在就临时收。不用「辅助功能」权限 |
| 隐藏快捷键 | 默认 ⌃⌥P（Control+Option+P），设置里能换 ⌃⌥H 或关掉。用的是系统的全局快捷键接口（Carbon `RegisterEventHotKey`），不用权限，不改系统设置 |
| 托盘 | 菜单栏右上角的图标 |
| 通知 | 用 `osascript` 发系统通知，显示成「脚本编辑器」发的；第一次可能要到「系统设置 → 通知」里允许 |
| 「仅在 Claude 不在前台时通知」 | 看最前面的程序是不是 Claude 桌面端 |
| 拖动轨迹 | 每帧（16 毫秒）读一次鼠标位置交给网页 |
| 坐标 | 按「点」算（不是物理像素），Retina 屏上大小和普通屏一样。网页报的走路速度乘过 `devicePixelRatio`，外壳除回去 |
| 系统「减弱动态效果」 | 和 Windows 关掉「动画效果」时一样：网页按 `prefers-reduced-motion` 停掉精灵动画，干活、待机都只显示静态立绘（没有静态图的姿态显示待机图）。启动时会在日志里记这个设置开没开 |

## 命令行

```text
claudia-pet                     打开（已经开着就让她回到默认位置）
claudia-pet --toggle            藏起来 / 再执行一次出来
claudia-pet --show | --quit     叫出来 / 退出
claudia-pet --connect [plugin|mcp|all]、--disconnect [...]
```

## 出问题时

- 日志在 `~/.claudia/macos.log`，WebKit 和 Python 的报错在 `~/.claudia/stderr.log`。
- 在终端里带调试开关直接跑，出错会直接打在终端上（控制 socket 也会接受 `eval <js>`、`py <代码>` 调试指令，和 Linux 版一样）：

  ```bash
  CLAUDIA_DEBUG=1 ~/.local/bin/claudia-pet
  ```

  调试模式下 Safari 的「开发」菜单里能检查她的网页（macOS 13.3 以上）。

- 打不开、报缺 PyObjC：重新运行 `sh macos/install.sh`。
- 干活时不换动作：先看日志里「系统「减弱动态效果」」那一行；再看「收到 Claude Code 插件的状态」——没有这一行就是插件没在送状态（检查 `~/.claude/settings.json`，新开 Code 会话）。
- 重启桌宠别用 `pkill -f claudia`：Claude 桌面端带起来的 `claudia-pet --mcp` 也会被杀掉，Claude 会报「claudia-pet 停止运行」。用 `claudia-pet --quit` 再重新打开。

## 代码

| 文件 | 对应 Windows 版 | 对应 Linux 版 |
|---|---|---|
| `claudia/main.py` | `Program.cs` | 同名 |
| `claudia/pet.py` | `Pet*.cs` | 同名（窗口、鼠标那部分重写） |
| `claudia/web.py` | WebView2 那部分 + `SpriteResources.cs` | 同名，WebKitGTK 换成 WKWebView |
| `claudia/ui.py` | — | GLib 定时器、GDK 屏幕坐标、GTK 菜单那些的替代 |
| `claudia/store.py` | `Store.cs`、`SpritePath.cs` | 同名 |
| `claudia/claude.py` | `Pet.Claude.cs`、`Ensure.cs`、`Mcp.cs` | 同名（找 Claude 进程换成 NSWorkspace / ps） |
| `claudia/system.py` | 托盘、通知、快捷键、命名事件 | 同名 |
| `claudia/actions.py`、`notices.py` | `Pet.Menu.cs`、`TaskNoticePolicy.cs` | 原样复制（几版要一起改） |
| `launcher.c` | — | 安装时编成 claudia-pet.app 里的可执行文件（shell 脚本当 .app 的可执行文件，Apple 芯片上会走 Rosetta） |
