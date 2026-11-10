#!/bin/sh
# applier：把「确认提交」的 yes/no 确认框改成恒返回 true，避免提交被确认框阻塞（K-1456）。
#
# 为什么需要：
#   `ConfirmCommitCommand.Execute` → `_FinaliseCommit` → `CreateStagingViewCommitProcess`
#   → `NotificationsManager.ShowNotificationDialogWithYesConfirmation(...)`；
#   Linux 上该对话框构造/等待路径会阻塞 `Execute` ⇒ 提交偶发卡住（K-1455）。
#
# 修法：`SourceTree.Notifications.NotificationsManager::ShowNotificationDialogWithYesConfirmation`
#   的**两个重载**方法体都替换为 `ldc.i4.1; ret`（恒 true）。
#   代价：不再弹「确认提交？」对话框；提交本身照常完成
#   （K-1456 实测：`Execute` 正常返回、`git log` 出现新提交、3/3）。
#
# 为什么必须 applier：`SourceTree.Api.UI.Wpf.dll` 被重新部署/重装后手工 patch 会丢
#   ⇒ 提交回到偶发阻塞。本 applier 与 loglabel-brush / commit-minreq / seticon-ret 同构、幂等。
#
# 与另三个补丁的关系（四者改同一个 DLL）：
#   顺序必须是 loglabel-brush → commit-minreq → seticon-ret → commit-noconfirm。
#   Program.cs 在 patch 前后都读回前三处指纹，任一缺席则以退出码 5 拒绝。
#
# 用法：
#   APP=/tmp/stpkg/SourceTree/app-3.4.15 sh 40-build/appliers/commit-noconfirm/apply.sh --check
#   APP=/tmp/stpkg/SourceTree/app-3.4.15 sh 40-build/appliers/commit-noconfirm/apply.sh
#
# 幂等判据：两个重载都已是 `ldc.i4.1; ret` ⇒ STATUS=ALREADY_PATCHED，不重写。
# 备份：首次执行会 `cp -a` 到 `<dll>.pre-commit-noconfirm.bak`（不动前三个补丁的备份）。
set -e
ROOT=$(cd "$(dirname "$0")/../../.." && pwd)
APP="${APP:?必须指定 APP=<SourceTree app 目录>（如 APP=/tmp/stpkg/SourceTree/app-3.4.15）}"
FX="${FX:-$HOME/.local/share/ldp/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.11}"
# 构建/运行本补丁程序用系统 SDK dotnet（专用 3.4.15 runtime 不带 SDK）
DOTNET_BIN="${DOTNET_BIN:-$(command -v dotnet || echo /usr/share/dotnet/dotnet)}"
SRC="$APP/SourceTree.Api.UI.Wpf.dll"
BAK="$APP/SourceTree.Api.UI.Wpf.dll.pre-commit-noconfirm.bak"
TOOL_DIR="$ROOT/40-build/appliers/commit-noconfirm"

if [ "$1" = "--check" ]; then
    [ -f "$SRC" ] && echo "OK   $SRC ($(stat -c%s "$SRC") 字节)" || { echo "缺 $SRC"; exit 1; }
    [ -f "$FX/PresentationCore.dll" ] && echo "OK   $FX/PresentationCore.dll ($(stat -c%s "$FX/PresentationCore.dll") 字节)" || { echo "缺 $FX/PresentationCore.dll"; exit 1; }
    [ -f "$TOOL_DIR/Program.cs" ] && echo "OK   $TOOL_DIR/Program.cs" || { echo "缺 $TOOL_DIR/Program.cs"; exit 1; }
    exit 0
fi

[ -f "$SRC" ] || { echo "缺 $SRC"; exit 1; }

cd "$TOOL_DIR"
rm -rf bin obj
"$DOTNET_BIN" build -c Release --nologo 2>&1 | grep -E "error|已用时间|Elapsed" | head -5

DLL="$TOOL_DIR/bin/Release/net10.0/linux-x64/commit-noconfirm.dll"
[ -f "$DLL" ] || DLL="$TOOL_DIR/bin/Release/net10.0/commit-noconfirm.dll"
[ -f "$DLL" ] || { echo "找不到构建产物 commit-noconfirm.dll" >&2; exit 1; }

# 幂等：先对**目标文件本身**判指纹（已 patch 则跳过，不重写）
if "$DOTNET_BIN" "$DLL" "$SRC" "" "$APP" --fx "$FX" --verify 2>/dev/null | grep -q "VERIFY=PATCHED"; then
    echo "已 patch 过（幂等跳过）: $SRC ($(stat -c%s "$SRC") 字节)"
    exit 0
fi

[ -f "$BAK" ] || cp -a "$SRC" "$BAK"
OUT="$SRC.tmp.$$"
if ! "$DOTNET_BIN" "$DLL" "$SRC" "$OUT" "$APP" --fx "$FX"; then
    echo "patch 失败" >&2
    rm -f "$OUT"
    exit 1
fi

if [ -f "$OUT" ]; then
    mv "$OUT" "$SRC"
    echo "已 patch: $SRC ($(stat -c%s "$SRC") 字节)"
else
    echo "已 patch 过（幂等跳过）: $SRC ($(stat -c%s "$SRC") 字节)"
fi
