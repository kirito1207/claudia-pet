#!/bin/sh
# 卸载 claudia-pet Linux 版：撤销接入（插件、桌面端 MCP、GNOME 扩展）和隐藏快捷键，删除程序；加 --keep-data 保留 ~/.claudia（设置、日志、立绘）。
set -eu

DST="${XDG_DATA_HOME:-$HOME/.local/share}/claudia-pet"
BIN="$HOME/.local/bin"
APPS="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
KEEP=0
for a in "$@"; do
  case "$a" in
    --keep-data) KEEP=1 ;;
    *) echo "不认识的参数：$a" >&2; exit 2 ;;
  esac
done

EXE="$DST/linux/claudia-pet"
if [ -x "$EXE" ]; then
  "$EXE" --quit >/dev/null 2>&1 || true
  "$EXE" --disconnect all || true
  # 撤掉 GNOME 里加的自定义快捷键
  python3 -I -c "import sys; sys.path.insert(0, '$DST/linux'); from claudia import system; system.set_hotkey('off')" 2>/dev/null || true
fi
sleep 1
pkill -f " -I $DST/linux/claudia/main.py" 2>/dev/null || true

rm -f "$APPS/claudia-pet.desktop"
[ -L "$BIN/claudia-pet" ] && rm -f "$BIN/claudia-pet"
rm -rf "$DST"
echo "已删除 $DST"
RUNTIME="${XDG_DATA_HOME:-$HOME/.local/share}/claudia-pet-runtime"
if [ -d "$RUNTIME" ]; then rm -rf "$RUNTIME"; echo "已删除界面库运行环境 $RUNTIME"; fi
if [ "$KEEP" = 0 ]; then
  rm -rf "$HOME/.claudia"
  echo "已删除 ~/.claudia"
fi
