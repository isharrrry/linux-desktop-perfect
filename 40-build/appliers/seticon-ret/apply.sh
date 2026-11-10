#!/bin/sh
# applier：把「确认提交」对话框的 SetIcon 改成空实现，避免 Icon.FromHandle 在 Linux 崩溃（K-1432）。
#
# 为什么需要：
#   点「提交」后 `ConfirmCommitCommand.Execute` → `_FinaliseCommit` →
#   `CreateStagingViewCommitProcess` → `NotificationsManager.ShowNotificationDialogWithYesConfirmation`
#   → `NotificationDialogWindow..ctor` → `SetIcon(VistaTaskDialogIcon)`
#   → `DialogIconUtil.GetDialogIcon` → `System.Drawing.Icon.FromHandle(IntPtr)`，
#   后者在 Linux 上抛 `System.ArgumentException: handle` ⇒ 提交在「确认提交」对话框
#   构造处中断（K-1431）。
#
# 修法：`NotificationDialogWindow::SetIcon` 方法体整体替换为单条 `ret`。
#   代价：确认对话框不显示图标；确认流程本身照常，提交能真正完成
#   （K-1432 实测：`/tmp/stgit` HEAD 61df0d9 → 8d0de09 "P1 test commit"、`git status` 变空、
#    `ArgumentException` 计数 0、进程 rc=124 正常终止）。
#
# 为什么必须 applier：`SourceTree.Api.UI.Wpf.dll` 被重新部署/重装后手工 patch 会丢
#   ⇒ 提交再次崩在 Icon.FromHandle。本 applier 与 loglabel-brush / commit-minreq 同构、幂等。
#
# 与另两个补丁的关系（三者改同一个 DLL）：
#   顺序必须是 loglabel-brush → commit-minreq → seticon-ret。
#   Program.cs 在 patch 前后都读回前两个补丁的指纹，任一缺席则以退出码 5 拒绝。
#
# 用法：
#   APP=/tmp/stpkg/SourceTree/app-3.4.15 sh 40-build/appliers/seticon-ret/apply.sh --check
#   APP=/tmp/stpkg/SourceTree/app-3.4.15 sh 40-build/appliers/seticon-ret/apply.sh
#
# 幂等判据：Program.cs 看 SetIcon 方法体是否已是单条 ret；是 ⇒ STATUS=ALREADY_PATCHED，不重写。
# 备份：首次执行会 `cp -a` 到 `<dll>.pre-seticon-ret.bak`（不动前两个补丁的备份）。
set -e
ROOT=$(cd "$(dirname "$0")/../../.." && pwd)
APP="${APP:?必须指定 APP=<SourceTree app 目录>（如 APP=/tmp/stpkg/SourceTree/app-3.4.15）}"
FX="${FX:-$HOME/.local/share/ldp/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.11}"
DOTNET_BIN="${DOTNET_BIN:-$(command -v dotnet || echo /usr/share/dotnet/dotnet)}"
SRC="$APP/SourceTree.Api.UI.Wpf.dll"
BAK="$APP/SourceTree.Api.UI.Wpf.dll.pre-seticon-ret.bak"
TOOL_DIR="$ROOT/40-build/appliers/seticon-ret"

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

DLL="$TOOL_DIR/bin/Release/net10.0/linux-x64/seticon-ret.dll"
[ -f "$DLL" ] || DLL="$TOOL_DIR/bin/Release/net10.0/seticon-ret.dll"
[ -f "$DLL" ] || { echo "找不到构建产物 seticon-ret.dll" >&2; exit 1; }

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
