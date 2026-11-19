#!/bin/sh
# 给当前用户安装 claudia-pet macOS 版（不需要 sudo）。
#
#   sh macos/install.sh [--connect] [--no-start]
#
# 程序装到 ~/.local/share/claudia-pet（Python 运行环境也在里面的 venv），命令 ~/.local/bin/claudia-pet，
# 「应用程序」里有 ~/Applications/claudia-pet.app（启动台、聚焦搜索都能找到；要 Xcode 命令行工具来编启动器）。
# 设置、日志和立绘在 ~/.claudia，重装不会丢。
# --connect 同时接入 Claude Code 插件和 Claude 桌面端（MCP）；不加的话之后在右键「设置…」→「接入 Claude」里接入。
# 不加开机自启：接入后由 Claude 把她叫出来。
# 第一次安装要联网下载 PyObjC。网络不好可以换镜像：PIP_INDEX_URL=https://pypi.tuna.tsinghua.edu.cn/simple sh macos/install.sh
set -eu

[ "$(uname -s)" = Darwin ] || { echo "这是 macOS 版的安装脚本（Linux 请用 linux/install.sh）" >&2; exit 1; }

SRC=$(cd "$(dirname "$0")/.." && pwd)
DST="$HOME/.local/share/claudia-pet"
BIN="$HOME/.local/bin"
APP="$HOME/Applications/claudia-pet.app"
CONNECT=0
START=1
for a in "$@"; do
  case "$a" in
    --connect) CONNECT=1 ;;
    --no-start) START=0 ;;
    *) echo "不认识的参数：$a" >&2; exit 2 ;;
  esac
done

# ---- 仓库要完整：macos 文件夹放在 claudia-pet 仓库的根目录下 ----
for f in web/index.html characters plugin/.claude-plugin/plugin.json VERSION icon/AppIcon.png macos/claudia/main.py; do
  if [ ! -e "$SRC/$f" ]; then
    echo "没找到 $SRC/$f" >&2
    echo "请把 macos 文件夹放进 claudia-pet 仓库的根目录（和 linux、web、characters 并排），再运行 sh macos/install.sh" >&2
    exit 1
  fi
done

# ---- Python 3.9 以上（用来建 venv；Xcode 命令行工具、python.org、Homebrew 装的都行） ----
# 没装 Xcode 命令行工具时，/usr/bin/python3 只是个空壳，一运行就弹「安装开发者工具」的窗口：
# 先找 Homebrew、python.org 装的；/usr/bin/python3 只在装了命令行工具时才试
HAVE_CLT=0
if xcode-select -p >/dev/null 2>&1; then HAVE_CLT=1; fi
py_ok() { [ -x "$1" ] && "$1" -c 'import sys, venv; sys.exit(sys.version_info < (3, 9))' >/dev/null 2>&1; }
PY=${PYTHON:-}
if [ -z "$PY" ]; then
  ON_PATH=$(command -v python3 2>/dev/null || true)
  for c in /opt/homebrew/bin/python3 /usr/local/bin/python3 /Library/Frameworks/Python.framework/Versions/Current/bin/python3 \
           "$ON_PATH" /usr/bin/python3; do
    [ -n "$c" ] || continue
    if [ "$c" = /usr/bin/python3 ] && [ "$HAVE_CLT" = 0 ]; then continue; fi
    if py_ok "$c"; then PY=$c; break; fi
  done
fi
if [ -z "$PY" ]; then
  echo "需要 Python 3.9 以上。最简单的办法是装 Xcode 命令行工具：" >&2
  echo "  xcode-select --install" >&2
  echo "装好后再运行一次 sh macos/install.sh" >&2
  exit 1
fi
echo "用 $PY（$("$PY" -c 'import platform; print(platform.python_version())')）"

# ---- 先停掉正在跑的桌宠 ----
# 用 SIGTERM（她会好好退出，也不会记成「你从菜单退出过」）。Claude 桌面端带起来的 --mcp 那个不动：
# 停了它 Claude 会报「claudia-pet 停止运行」，要重开 Claude 才回来
pets() { ps -axww -o pid=,command= | grep -F "$DST/macos/claudia/main.py" | grep -v -e ' --mcp' -e grep | awk '{print $1}'; }
PIDS=$(pets || true)
if [ -n "$PIDS" ]; then
  echo "先让正在跑的 claudia-pet 退出…"
  kill $PIDS 2>/dev/null || true
  sleep 2
  PIDS=$(pets || true)
  if [ -n "$PIDS" ]; then kill -9 $PIDS 2>/dev/null || true; fi
fi

# ---- 复制 ----
mkdir -p "$DST" "$BIN"
TMP="$DST.new"
rm -rf "$TMP"
mkdir -p "$TMP/macos" "$TMP/icon"
echo "复制文件…"
cp -Rp "$SRC/web" "$SRC/characters" "$SRC/plugin" "$TMP/"
cp -p "$SRC/VERSION" "$SRC/LICENSE" "$SRC/NOTICE.md" "$TMP/" 2>/dev/null || cp -p "$SRC/VERSION" "$TMP/"
cp -p "$SRC/icon/AppIcon.png" "$TMP/icon/"
cp -Rp "$SRC/macos/claudia" "$TMP/macos/"
cp "$SRC/macos/claudia-pet" "$SRC/macos/uninstall.sh" "$TMP/macos/"
find "$TMP" -name __pycache__ -type d -prune -exec rm -rf {} +
find "$TMP" -name .DS_Store -type f -delete
chmod +x "$TMP/macos/claudia-pet" "$TMP/macos/uninstall.sh"
xattr -dr com.apple.quarantine "$TMP" 2>/dev/null || true   # 从网上下的压缩包解出来的文件带着隔离标记
# venv 里写死了自己的路径（$DST/venv）：挪进新目录、换完名字还是同一个路径，可以接着用
if [ -d "$DST/venv" ]; then mv "$DST/venv" "$TMP/venv"; fi
rm -rf "$DST.old"
if [ -d "$DST" ]; then mv "$DST" "$DST.old"; fi
mv "$TMP" "$DST"
rm -rf "$DST.old"

# ---- Python 运行环境：venv + PyObjC（AppKit、WebKit 的 Python 接口） ----
VPY="$DST/venv/bin/python3"
if ! "$VPY" -I -c 'import objc, AppKit, WebKit' >/dev/null 2>&1; then
  echo "准备 Python 运行环境（要联网下载 PyObjC，一两分钟）…"
  rm -rf "$DST/venv"
  "$PY" -m venv "$DST/venv"
  "$VPY" -m pip install --disable-pip-version-check -q --upgrade pip >/dev/null 2>&1 || true
  if ! "$VPY" -m pip install --disable-pip-version-check -q pyobjc-core pyobjc-framework-Cocoa pyobjc-framework-WebKit; then
    echo "装 PyObjC 失败。网络不好的话换个镜像再装一次：" >&2
    echo "  PIP_INDEX_URL=https://pypi.tuna.tsinghua.edu.cn/simple sh macos/install.sh" >&2
    exit 1
  fi
  if ! "$VPY" -I -c 'import objc, AppKit, WebKit' >/dev/null 2>&1; then
    echo "PyObjC 装好了但载入不了，下面是出错信息：" >&2
    "$VPY" -I -c 'import objc, AppKit, WebKit' >&2 || true
    exit 1
  fi
fi
ln -sf "$DST/macos/claudia-pet" "$BIN/claudia-pet"

# ---- ~/Applications/claudia-pet.app：一个小壳，双击、启动台、聚焦搜索都能打开 ----
# 壳里的可执行文件是 macos/launcher.c 编出来的通用二进制（shell 脚本当 .app 的可执行文件，Apple 芯片上会走 Rosetta）。
# 要 Xcode 命令行工具里的 cc；没有就不建 .app：命令行和 Claude 叫她出来都不受影响
make_app() {
  if [ "$HAVE_CLT" = 0 ]; then
    echo "提示：没装 Xcode 命令行工具，不建 $APP（装了以后重新运行 install.sh 就有）。命令行用 $BIN/claudia-pet 打开"
    return 1
  fi
  rm -rf "$APP"
  WORK=$(mktemp -d)
  if ! cc -O2 -arch arm64 -arch x86_64 -mmacosx-version-min=11.0 -o "$WORK/claudia-pet" "$SRC/macos/launcher.c" 2>"$WORK/cc.log" \
     && ! cc -O2 -mmacosx-version-min=11.0 -o "$WORK/claudia-pet" "$SRC/macos/launcher.c" 2>>"$WORK/cc.log"; then
    echo "提示：启动器没编出来，不建 $APP：" >&2
    cat "$WORK/cc.log" >&2
    rm -rf "$WORK"
    return 1
  fi
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
  mv "$WORK/claudia-pet" "$APP/Contents/MacOS/claudia-pet"
  rm -rf "$WORK"
  VERSION=$(cat "$DST/VERSION" 2>/dev/null || echo 0)
  cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>claudia-pet</string>
  <key>CFBundleDisplayName</key><string>claudia-pet</string>
  <key>CFBundleIdentifier</key><string>local.claudia-pet</string>
  <key>CFBundleExecutable</key><string>claudia-pet</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSUIElement</key><true/>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
</dict>
</plist>
EOF
  chmod +x "$APP/Contents/MacOS/claudia-pet"
  # 图标：AppIcon.png → AppIcon.icns（系统自带的 sips、iconutil）
  WORK=$(mktemp -d)
  SET="$WORK/AppIcon.iconset"
  mkdir -p "$SET"
  for s in 16 32 128 256 512; do
    sips -z "$s" "$s" "$DST/icon/AppIcon.png" --out "$SET/icon_${s}x${s}.png" >/dev/null 2>&1 || true
    d=$((s * 2))
    sips -z "$d" "$d" "$DST/icon/AppIcon.png" --out "$SET/icon_${s}x${s}@2x.png" >/dev/null 2>&1 || true
  done
  iconutil -c icns "$SET" -o "$APP/Contents/Resources/AppIcon.icns" >/dev/null 2>&1 || echo "提示：图标没做出来，不影响使用"
  rm -rf "$WORK"
  touch "$APP"
}
mkdir -p "$HOME/Applications"
make_app || rm -rf "$APP"
echo "已安装到 $DST"

case ":$PATH:" in
  *":$BIN:"*) ;;
  *) echo "提示：$BIN 不在 PATH 里，命令行里用完整路径 $BIN/claudia-pet" ;;
esac

if [ "$CONNECT" = 1 ]; then
  "$BIN/claudia-pet" --connect all
fi
if [ "$START" = 1 ]; then
  if [ -x "$APP/Contents/MacOS/claudia-pet" ]; then
    open "$APP"
  else
    (nohup "$DST/macos/claudia-pet" </dev/null >/dev/null 2>&1 &)
  fi
  echo "claudia-pet 已经打开（菜单栏右上角也有她的图标）。"
fi
