# claudia-pet Linux 版

Windows 版的外壳（`app/`，WPF + WebView2）换成了 Python + Qt（PySide6 + Qt WebEngine）。Qt WebEngine 和 WebView2 一样是 Chromium 内核，网页、透明视频在两边表现一致（2026-11-27 从 GTK + WebKitGTK 换过来：WebKitGTK 经 GStreamer 解透明 VP9 时好时坏、连着换视频会整页卡死，CPU 也高四五倍）。网页（`web/`）、立绘（`characters/`）和 Claude Code 插件（`plugin/`）两边共用，数据目录也是同一个 `~/.claudia`。

用过的系统：Ubuntu 26.04（GNOME 50，Wayland，GTK 版时期）、Debian 13（Openbox，X11）。需要 glibc 2.34 以上（Ubuntu 22.04、Debian 12 及更新）。

## 安装

先装系统的运行库（大多数桌面系统已经有一部分）：

```bash
sudo apt install python3-gi python3-venv libxcb-cursor0 libxcb-icccm4 libxcb-keysyms1 libxkbcommon-x11-0 libnss3 libnotify-bin gir1.2-notify-0.7
```

界面库 PySide6 不用自己装：`install.sh` 第一次运行时用 pip 把它装进单独的运行环境 `~/.local/share/claudia-pet-runtime`（下载几百 MB，装好约 690 MB）。以后重装、升级 claudia-pet 不会重新下载；卸载时一起删掉。

在仓库根目录执行：

```bash
sh linux/install.sh
```

程序装在 `~/.local/share/claudia-pet`，命令是 `~/.local/bin/claudia-pet`，应用菜单里也会出现「claudia-pet」。不需要 root，也不会加开机自启。加 `--connect` 可以同时接入 Claude Code 插件和 Claude 桌面端。

在 GNOME 桌面上还会装一个 GNOME Shell 扩展，让她能待在 Claude 窗口里（见下文「跟着 Claude 窗口」）。装完要**注销电脑、重新登录一次**才生效（是系统的登录，Claude 账号不受影响）。不想要的话加 `--no-extension`。

不安装、直接从仓库运行也可以（要先装过一次，用的是同一个运行环境）：

```bash
./linux/claudia-pet
```

卸载（加 `--keep-data` 保留 `~/.claudia`）：

```bash
sh ~/.local/share/claudia-pet/linux/uninstall.sh
```

## 接入 Claude

和 Windows 版一样，在右键「设置…」→「接入 Claude」里点「接入」：

- **Claude Code 插件**：改 `~/.claude/settings.json` 的 `env.CLAUDE_CODE_PLUGIN_DIRS`（Linux 上多个目录用冒号隔开）。新开的 Code 会话生效。
- **Claude 桌面端（MCP）**：改 `~/.config/Claude/claude_desktop_config.json`。完全退出 Claude 再打开后生效。

- **GNOME 扩展（跟着 Claude 窗口）**：只在 GNOME 桌面上有这一项，见下文。

改之前都会先备份，备份名是 `原文件名.bak-claudia-时间`。命令行也能接入：`claudia-pet --connect all`。

## 跟着 Claude 窗口

Wayland 下普通程序看不到别的窗口在哪，桌宠自己找不到 Claude。所以配了一个 GNOME Shell 扩展（`gnome-extension/`），它只做一件事：盯着 Claude 桌面端的主窗口，把位置、大小、是否最小化、在不在当前工作区，以及叠在它上面的窗口范围，通过 D-Bus（`io.github.kirito1207.ClaudiaPet`）告诉桌宠。它不改 Claude，不动任何窗口，也不拦截输入。

- 扩展装在 `~/.local/share/gnome-shell/extensions/claudia-pet@kirito1207.github.io`。装好、更新、撤销后，都要注销电脑、重新登录一次才生效（Wayland 下 GNOME Shell 只在登录时加载扩展）。
- 扩展生效后，在「设置…」→「位置与多开」里把她改成待在「Claude 窗口里」。位置和 Windows 版一样按「离 Claude 窗口右下角多远」记。
- Claude 最小化、切到别的工作区时，她跟着藏起来。**她站的地方被别的窗口盖住时，她整只藏起来**：Wayland 下没法只被挡住一部分，这一点和 Windows 版不一样。
- 扩展没装、没启用、找不到 Claude 的窗口，或者 GNOME 升级后不认这个扩展时，她自动待在桌面上，不会消失。
- 扩展声明支持 GNOME 50。GNOME 升到新的大版本后，要在 `metadata.json` 里加上新版本号（必要时改代码）。
- 认 Claude 窗口的办法：先看它属于哪个应用（`com.anthropic.Claude.desktop`），再看窗口类名（`com.anthropic.Claude`），最后看进程的可执行文件名（`claude-desktop`）。

## 和 Windows 版不一样的地方

| 功能 | Linux 版 |
|---|---|
| 待在 Claude 窗口里 | 要 GNOME Shell 扩展（见上文）；被别的窗口盖住时整只藏起来。没有扩展时设置里这个选项是灰的 |
| 窗口 | 走 XWayland（`QT_QPA_PLATFORM=xcb`）：Wayland 下普通窗口不能自己摆位置、置顶。窗口不归窗口管理器管（override-redirect），GNOME 不会把它推回屏幕里，挪动也不用等它，所以头顶的透明区能伸出屏幕顶，碰边按人物本体算 |
| 打开时 | 窗口一出来就在原位，但外形（SHAPE）是空的、看不见；等人物立绘画好再显示，不会先看到名牌或图片的占位框。藏起来也是把外形设成空 |
| Claude 随登录自启 | 那时交给 MCP 的环境里可能还没有 `DISPLAY`：桌宠从 systemd 用户会话补上，连不上图形界面会在 30 秒内重试 |
| 点击穿透 | X11 SHAPE 扩展的输入区域，只在人物、气泡、名牌、额度上收鼠标 |
| 隐藏快捷键 | Wayland 下普通程序抢不到全局按键。在设置里选了快捷键后，会往 GNOME「自定义快捷键」里加一条，按下时执行 `claudia-pet --toggle`。默认不加 |
| 托盘 | Qt 的系统托盘（GNOME 上要有 AppIndicator 扩展才显示，Ubuntu 自带；KDE 等原生支持）。单击、中键点托盘图标是叫她出来 |
| 通知 | libnotify（系统通知） |
| 「仅在 Claude 不在前台时通知」 | GNOME 扩展报告 Claude 主窗口在前台时不发通知；没有焦点数据时照常通知 |
| 拖动轨迹 | 每帧（16 毫秒）读一次鼠标位置交给网页（Windows 版读系统鼠标历史） |

## 命令行

```text
claudia-pet                     打开（已经开着就让她回到默认位置）
claudia-pet --toggle            藏起来 / 再执行一次出来
claudia-pet --show | --quit     叫出来 / 退出
claudia-pet --connect [plugin|mcp|gnome|all]、--disconnect [...]
```

日志在 `~/.claudia/linux.log`。设置环境变量 `CLAUDIA_DEBUG=1` 后，日志会写得更详细，控制 socket 也会接受调试指令（`eval <js>`、`py <代码>`）。

## 代码

| 文件 | 对应 Windows 版 |
|---|---|
| `claudia/main.py` | `Program.cs`：参数、单实例、启动 |
| `claudia/pet.py` | `Pet*.cs`：窗口、鼠标、拖动和飞行、走路、大小、菜单、设置、多开、通知 |
| `claudia/web.py` | WebView2 那部分 + `SpriteResources.cs`（Qt WebEngine）：`claudia://app/`、`claudia://data/` 两个地址，以及 `chrome.webview.postMessage` 垫片 |
| `claudia/store.py` | `Store.cs`、`SpritePath.cs` |
| `claudia/claude.py` | `Pet.Claude.cs`、`Ensure.cs`、`Mcp.cs` |
| `claudia/system.py` | 托盘、通知、快捷键、控制 socket、锁屏 |
| `claudia/xshape.py` | 点击穿透；没画好、藏起来时把窗口外形设成空 |
| `claudia/xwin.py` | 前台窗口是否全屏（全屏时让开）、直接问 X 服务器鼠标位置和按键 |
| `claudia/claude_window.py` | `Pet.Follow.cs` 里找 Claude 窗口那部分：收 GNOME 扩展报来的状态 |
| `gnome-extension/` | GNOME Shell 扩展：盯着 Claude 的窗口，在 D-Bus 上广播 |
| `claudia/actions.py` | `Pet.Menu.cs` 里的动作分组和中文名（两边要一起改） |
