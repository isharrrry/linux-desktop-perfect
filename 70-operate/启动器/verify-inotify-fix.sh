#!/bin/bash
# inotify 提权后的验证清单
#
# ⚠️ 本脚本基于 K-1306/K-1307 的假设（「inotify 上限导致暂存失败」），
#    该假设已被 K-1333/K-1343 **否定**：
#    - K-1333：RepoWatcher 零报错（inotify 已用 124/128）时，UnstagedFileList.Count 仍为 0
#    - K-1343：最终根因是 Tabs.Count = 0（SourceTree 从没打开仓库）
#    - ⇒ 正确结论见 70-operate/已知限制汇总.md 的 L-20.4：暂存不可达
#
# 本脚本保留作为历史记录 / 环境自检（检查 inotify 上限本身仍有意义），
# 但**不要**再认为「跑完它暂存就能用」。
#
# 用法：bash 70-operate/启动器/verify-inotify-fix.sh
set -u
FX=~/.local/share/ldp/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.11
APP=/tmp/stpkg/SourceTree/app-3.4.15
LIMIT=$(cat /proc/sys/fs/inotify/max_user_instances 2>/dev/null)

echo "=== 步骤 1/4：检查 inotify 上限 ==="
echo "  max_user_instances = $LIMIT"
if [ "${LIMIT:-0}" -lt 512 ]; then
  echo "  ❌ 仍为 $LIMIT（未提权或未生效）"
  echo "  请先执行："
  echo "    sudo sysctl -w fs.inotify.max_user_instances=1024"
  echo "    # 持久化："
  echo "    echo 'fs.inotify.max_user_instances=1024' | sudo tee /etc/sysctl.d/99-sourcetree.conf"
  echo "    sudo sysctl --system"
  exit 1
fi
echo "  ✅ 已提升到 $LIMIT"

echo "=== 步骤 2/4：当前已用实例数 ==="
USED=$(for pid in $(ls /proc | grep -E '^[0-9]+$'); do ls -la /proc/$pid/fd 2>/dev/null | grep -c inotify; done 2>/dev/null | awk '{s+=$1} END {print s}')
echo "  已用 $USED / $LIMIT"

echo "=== 步骤 3/4：启动 SourceTree 并检查 RepoWatcher ==="
for p in $(pgrep -x SourceTree 2>/dev/null); do kill -9 "$p" 2>/dev/null; done
sleep 1
[ -x "$APP/sourcetree" ] || { cp 70-operate/启动器/sourcetree "$APP/sourcetree" 2>/dev/null; chmod +x "$APP/sourcetree" 2>/dev/null; }
setsid timeout 60 env DISPLAY=:303 "$APP/sourcetree" >/tmp/inotify-fix.log 2>&1 < /dev/null &
sleep 45
if grep -aq "RepoWatcher.*inotify instances limit" /tmp/inotify-fix.log; then
  echo "  ❌ 仍有 inotify 上限错误（见 /tmp/inotify-fix.log）"
  exit 2
fi
echo "  ✅ 无 inotify 上限错误"

echo "=== 步骤 4/4：检查 UnstagedFileList.Count（需 ST_REFLECT 版 DLL）==="
if [ -f /tmp/ST.cnt.dll ]; then
  echo "  提示：用 /tmp/ST.cnt.dll 替换 SourceTree.exe 后再跑，可看到 Count 值"
  echo "  （本脚本不自动替换，避免影响当前部署）"
else
  echo "  提示：/tmp/ST.cnt.dll 不存在，无法检查 Count"
fi

echo
echo "=== 全部检查完成 ==="
