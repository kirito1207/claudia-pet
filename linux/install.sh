#!/bin/sh
# 给当前用户安装 claudia-pet Linux 版（不需要 root）。
#
#   sh linux/install.sh [--connect] [--no-extension] [--no-start]
#
# 程序装到 ~/.local/share/claudia-pet，命令 ~/.local/bin/claudia-pet，应用菜单里有「claudia-pet」。
# 设置、日志和立绘在 ~/.claudia，重装不会丢。
# --connect 同时接入 Claude Code 插件和 Claude 桌面端（MCP）；不加的话之后在右键「设置…」→「接入 Claude」里接入。
# GNOME 桌面上还会装一个 GNOME Shell 扩展（让她能待在 Claude 窗口里），注销电脑、重新登录一次后生效；
# --no-extension 不装它（她就只待在桌面上）。
# 不加开机自启：接入后由 Claude 把她叫出来。
set -eu

SRC=$(cd "$(dirname "$0")/.." && pwd)
DST="${XDG_DATA_HOME:-$HOME/.local/share}/claudia-pet"
BIN="$HOME/.local/bin"
APPS="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
CONNECT=0
START=1
EXTENSION=1
for a in "$@"; do
  case "$a" in
    --connect) CONNECT=1 ;;
    --no-start) START=0 ;;
    --no-extension) EXTENSION=0 ;;
    *) echo "不认识的参数：$a" >&2; exit 2 ;;
  esac
done

# ---- 依赖 ----
# 界面用 Qt（PySide6 + Qt WebEngine，和 Windows 版一样是 Chromium 内核；2026-11-27 从 WebKitGTK 换过来）。
# PySide6 用 pip 装进单独的运行环境（约 670 MB，只第一次装时下载，重装程序不再下载）；GLib、D-Bus、通知仍用系统的 python3-gi。
RUNTIME="${XDG_DATA_HOME:-$HOME/.local/share}/claudia-pet-runtime"
missing=""
LDCONFIG=$(command -v ldconfig || echo /sbin/ldconfig)   # 普通用户的 PATH 里常常没有 /sbin
python3 -I -c 'import gi; from gi.repository import GLib, Gio' 2>/dev/null || missing="$missing python3-gi"
python3 -c 'import venv, ensurepip' 2>/dev/null || missing="$missing python3-venv"
for lib in libxcb-cursor.so.0:libxcb-cursor0 libxcb-icccm.so.4:libxcb-icccm4 libxcb-keysyms.so.1:libxcb-keysyms1 \
           libxkbcommon-x11.so.0:libxkbcommon-x11-0 libnss3.so:libnss3; do
  "$LDCONFIG" -p 2>/dev/null | grep -q "${lib%%:*}" || missing="$missing ${lib#*:}"
done
if [ -n "$missing" ]; then
  echo "缺少运行库，请先装：" >&2
  echo "  sudo apt install$missing libnotify-bin gir1.2-notify-0.7" >&2
  exit 1
fi
if ! "$RUNTIME/bin/python3" -I -c 'import PySide6.QtWebEngineWidgets, gi' 2>/dev/null; then
  echo "第一次安装：下载界面库 PySide6（约 670 MB 解压后），装到 $RUNTIME ……"
  rm -rf "$RUNTIME"
  python3 -m venv --system-site-packages "$RUNTIME"
  "$RUNTIME/bin/pip" install --disable-pip-version-check -q "PySide6>=6.6,<7" || { echo "PySide6 没装上（网络？）。" >&2; rm -rf "$RUNTIME"; exit 1; }
  "$RUNTIME/bin/python3" -I -c 'import PySide6.QtWebEngineWidgets, gi' || { echo "PySide6 装好了却用不了。" >&2; exit 1; }
fi

# ---- 复制 ----
mkdir -p "$(dirname "$DST")" "$BIN" "$APPS"
exec 9>"$DST.install.lock"
if ! flock -n 9; then
  echo "已有一个 claudia-pet 安装任务在运行。" >&2
  exit 1
fi
OLD="$DST.old"
for dir in "$DST" "$OLD"; do
  if [ -L "$dir" ] || { [ -e "$dir" ] && [ ! -x "$dir/linux/claudia-pet" ]; }; then
    echo "安装路径不是 claudia-pet 安装目录：$dir" >&2
    exit 1
  fi
done
if [ -d "$OLD" ] && [ ! -d "$DST" ]; then mv "$OLD" "$DST"; fi
TMP=$(mktemp -d "$DST.new.XXXXXXXX")
promoted=0
cleanup() {
  if [ "$promoted" = 1 ] && [ -d "$OLD" ]; then mv "$DST" "$TMP"; fi
  if [ ! -e "$DST" ] && [ -d "$OLD" ]; then mv "$OLD" "$DST"; fi
  if [ -d "$TMP" ]; then rm -rf -- "$TMP"; fi
}
trap cleanup EXIT
trap 'exit 1' HUP INT TERM
# 物理参数表三端共用（web/physics.json）：缺项、范围不对就不装，免得装出去拖动不扔、Q 弹不压
python3 -I - "$SRC/web/physics.json" <<'PY' || { echo "web/physics.json 缺项或范围不对，没有安装" >&2; exit 1; }
import json, sys
t = json.load(open(sys.argv[1], encoding="utf-8"))
for k in ("throwPower", "gravity", "restitution", "groundFriction", "elasticity"):
    r = t[k]
    assert r["min"] <= r["default"] <= r["max"] and r["step"] > 0, k
PY
mkdir -p "$TMP/linux" "$TMP/icon"
cp -rp "$SRC/web" "$SRC/characters" "$SRC/plugin" "$TMP/"
cp -p "$SRC/VERSION" "$SRC/LICENSE" "$SRC/NOTICE.md" "$TMP/"
cp -p "$SRC/icon/AppIcon.png" "$TMP/icon/"
cp -rp "$SRC/linux/claudia" "$TMP/linux/"
cp -rp "$SRC/linux/gnome-extension" "$TMP/linux/"
cp "$SRC/linux/claudia-pet" "$SRC/linux/uninstall.sh" "$TMP/linux/"
find "$TMP" -name __pycache__ -type d -prune -exec rm -rf {} +
chmod +x "$TMP/linux/claudia-pet" "$TMP/linux/uninstall.sh"

# 只请桌宠退出。MCP 使用同一程序入口，但不持有桌宠的实例锁。
if [ -x "$DST/linux/claudia-pet" ]; then "$DST/linux/claudia-pet" --quit >/dev/null 2>&1 || true; fi
python3 -I - "${CLAUDIA_DATA_DIR:-$HOME/.claudia}/run" <<'PY'
import fcntl
import sys
import time
from pathlib import Path

run = Path(sys.argv[1])

def busy(lock):
    try:
        with lock.open("rb") as file:
            fcntl.flock(file, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except FileNotFoundError:
        return False
    except BlockingIOError:
        return True
    return False

deadline = time.monotonic() + 12
while any(busy(lock) for lock in run.glob("*.lock")):
    if time.monotonic() >= deadline:
        sys.exit("桌宠还在运行，安装目录未替换。")
    time.sleep(.15)
PY
if [ -d "$OLD" ]; then rm -rf -- "$OLD"; fi
if [ -d "$DST" ]; then mv "$DST" "$OLD"; fi
mv "$TMP" "$DST"
promoted=1
ln -sf "$DST/linux/claudia-pet" "$BIN/claudia-pet"

cat > "$APPS/claudia-pet.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=claudia-pet
Comment=Claude 娘桌宠（非官方同人项目）
Exec=$BIN/claudia-pet
Icon=$DST/icon/AppIcon.png
Terminal=false
Categories=Utility;
StartupWMClass=claudia-pet
EOF
if [ ! -x "$DST/linux/claudia-pet" ] || [ ! -f "$DST/web/index.html" ]; then
  echo "安装文件不完整。" >&2
  exit 1
fi
promoted=0
if [ -d "$OLD" ]; then rm -rf -- "$OLD"; fi
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$APPS" 2>/dev/null || true
echo "已安装到 $DST"

case ":$PATH:" in
  *":$BIN:"*) ;;
  *) echo "提示：$BIN 不在 PATH 里，命令行里用完整路径 $BIN/claudia-pet" ;;
esac

if [ "$CONNECT" = 1 ]; then
  "$BIN/claudia-pet" --connect plugin
  "$BIN/claudia-pet" --connect mcp
fi
case "${XDG_CURRENT_DESKTOP:-}" in
  *GNOME*|*gnome*)
    if [ "$EXTENSION" = 1 ]; then
      "$BIN/claudia-pet" --connect gnome
      echo "提示：跟着 Claude 窗口要用的 GNOME 扩展已经装好，注销电脑、重新登录一次后生效（不是 Claude 账号）。"
    fi ;;
esac
if [ "$START" = 1 ]; then
  setsid "$BIN/claudia-pet" 9>&- >/dev/null 2>&1 < /dev/null &
  echo "claudia-pet 已经打开。"
fi
