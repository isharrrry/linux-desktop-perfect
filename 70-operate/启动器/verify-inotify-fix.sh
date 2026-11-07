#!/bin/bash
# 环境自检（inotify + 启动器）
#
# ⚠️ 历史结论修正：
#   - K-1306 曾认为「inotify 上限导致暂存失败」；K-1333 否定；K-1377 在「有仓库」条件下
#     重新观察到 UI 不反映文件变化 ⇒ inotify 仍是可疑项（但非唯一）。
#   - K-1379 确认白屏的真正原因是缺 WPF_LINUX_ATSPI=0（启动器已内置）。
#
# 用法：bash 70-operate/启动器/verify-inotify-fix.sh
set -u
FX=~/.local/share/ldp/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.11
APP="${ST_APP:-/tmp/stpkg/SourceTree/app-3.4.15}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC_LAUNCHER="$HERE/sourcetree"

echo "=== 步骤 1/4：确保启动器是最新版（含 WPF_LINUX_ATSPI=0，K-1379）==="
if [ ! -f "$SRC_LAUNCHER" ]; then
  echo "  ❌ 找不到源启动器：$SRC_LAUNCHER"; exit 1
fi
if [ ! -x "$APP/sourcetree" ] || ! grep -q "WPF_LINUX_ATSPI=0" "$APP/sourcetree" 2>/dev/null; then
  cp "$SRC_LAUNCHER" "$APP/sourcetree" && chmod +x "$APP/sourcetree"
  echo "  ✅ 已更新：$APP/sourcetree（$(stat -c%s "$APP/sourcetree")B）"
else
  echo "  ✅ 已是最新：$APP/sourcetree"
fi

echo "=== 步骤 2/4：检查 inotify 上限 ==="
LIMIT=$(cat /proc/sys/fs/inotify/max_user_instances 2>/dev/null || echo "?")
USED=0
for pid in $(ls /proc 2>/dev/null | grep -E '^[0-9]+$'); do
  n=$(ls -la /proc/$pid/fd 2>/dev/null | grep -c inotify)
  USED=$((USED + n))
done
echo "  max_user_instances = $LIMIT"
echo "  已用 = $USED"
if [ "$LIMIT" != "?" ] && [ "$LIMIT" -lt 512 ]; then
  echo "  ⚠️ 上限偏低（$LIMIT）。若出现「RepoWatcher ... inotify instances limit」错误，请："
  echo "     sudo sysctl -w fs.inotify.max_user_instances=1024"
  echo "  ⚠️ 免 sudo 缓解：清理重复的 xdg-desktop-portal 进程（实测可释放 15 个，K-1379）"
else
  echo "  ✅ 上限充足"
fi

echo "=== 步骤 3/4：启动 SourceTree 并检查日志 ==="
for p in $(pgrep -x SourceTree 2>/dev/null); do kill -9 "$p" 2>/dev/null; done
sleep 1
setsid timeout 60 env DISPLAY="${DISPLAY:-:303}" "$APP/sourcetree" >/tmp/inotify-fix.log 2>&1 < /dev/null &
sleep 45

WID=$(DISPLAY="${DISPLAY:-:303}" xdotool search --name 'Sourcetree' 2>/dev/null | head -1)
if [ -n "$WID" ]; then
  echo "  ✅ 窗口出现：WID=$WID"
else
  echo "  ❌ 窗口未出现 —— 检查是否缺 WPF_LINUX_ATSPI=0（K-1379）"
fi

if grep -aq "RepoWatcher.*inotify instances limit" /tmp/inotify-fix.log; then
  echo "  ❌ RepoWatcher 报告 inotify 上限错误（需提权或清理进程）"
else
  echo "  ✅ 未发现 RepoWatcher inotify 错误"
fi

echo "=== 步骤 4/4：结果 ==="
echo "  日志：/tmp/inotify-fix.log"
echo "  说明：窗口能出现 ≠ 文件监控可用；要验证文件列表请看 K-1377 的说明。"
