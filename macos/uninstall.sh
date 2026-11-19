#!/bin/sh
# 卸载 claudia-pet macOS 版：撤销两项接入，删除程序和 ~/Applications/claudia-pet.app；
# 加 --keep-data 保留 ~/.claudia（设置、日志、立绘）。
set -eu

DST="$HOME/.local/share/claudia-pet"
BIN="$HOME/.local/bin"
APP="$HOME/Applications/claudia-pet.app"
KEEP=0
for a in "$@"; do
  case "$a" in
    --keep-data) KEEP=1 ;;
    *) echo "不认识的参数：$a" >&2; exit 2 ;;
  esac
done

EXE="$DST/macos/claudia-pet"
if [ -x "$EXE" ]; then
  "$EXE" --quit >/dev/null 2>&1 || true
  "$EXE" --disconnect all || true
fi
sleep 1
# 还剩下的（多开的那几只）也停掉；Claude 桌面端带起来的 --mcp 不动，重开 Claude 后它就没了
PIDS=$(ps -axww -o pid=,command= | grep -F "$DST/macos/claudia/main.py" | grep -v -e ' --mcp' -e grep | awk '{print $1}' || true)
if [ -n "$PIDS" ]; then kill $PIDS 2>/dev/null || true; sleep 1; fi

rm -rf "$APP"
if [ -L "$BIN/claudia-pet" ]; then rm -f "$BIN/claudia-pet"; fi
rm -rf "$DST"
echo "已删除 $DST 和 $APP"
if [ "$KEEP" = 0 ]; then
  rm -rf "$HOME/.claudia"
  echo "已删除 ~/.claudia"
fi
