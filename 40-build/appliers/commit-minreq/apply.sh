#!/bin/sh
# applier：跳过「提交」前的用户信息对话框，让提交在 Linux 上真正可完成（K-1430 / K-1412）。
#
# 为什么需要：
#   点「提交」会走 `ConfirmCommitCommand.Execute` → `ConfirmCommit` →
#   `CommitAndFileStatusViewModel._CheckMinimumRequirementsBeforeEnterCommit` →
#   `AccountManager.GetDefaultUserInformation` → `WindowsIdentity.GetCurrent()`，
#   Linux 上后者抛 `PlatformNotSupportedException: Windows Principal functionality is not
#   supported on this platform.` ⇒ 进程 abort，提交永远做不成（K-1404）。
#
# 修法：把该方法里 `IL_000d ldloc.0` 换成 `ldc.i4.0`（两条都是 1 字节、无操作数）
#   ⇒ 后续 `IL_000e brfalse IL_00ce` 恒跳 `ldc.i4.1` ⇒ 方法恒返回 true，
#   整段弹用户信息对话框的路径被跳过，`WindowsIdentity.GetCurrent()` 不再被调用。
#   指令数与栈平衡都不变。
#
# 实测（K-1430）：补丁后 `ConfirmCommitCommand.Execute` 真实产生提交
#   （`/tmp/stgit` HEAD 9a74d4a → 61df0d9 "P1 test commit"、`git status` 变空、
#    `PlatformNotSupportedException` 计数 0、History 顶部出现该提交，截图 68527B）。
#
# 为什么要 applier（不能只手工 patch）：
#   `SourceTree.Api.UI.Wpf.dll` 若被重新部署/重装，手工 patch 会丢 ⇒ 提交再次崩。
#   本 applier 与 loglabel-brush / gong-dnd / wb-registry 同构，可重复执行（幂等）。
#
# 与 loglabel-brush 的顺序（两者改同一个 DLL）：
#   先 `loglabel-brush/apply.sh`，后本脚本。Program.cs 会读回 brush 指纹
#   （`LogLabelControl::.ctor` 的 ret 前 5 条）并在缺席时以退出码 5 拒绝，
#   避免在错基线上产出不可用 DLL。
#
# 用法：
#   APP=/tmp/stpkg/SourceTree/app-3.4.15 sh 40-build/appliers/commit-minreq/apply.sh --check
#   APP=/tmp/stpkg/SourceTree/app-3.4.15 sh 40-build/appliers/commit-minreq/apply.sh
#
# 幂等判据：Program.cs 看 `IL_000d` 处是否已是 `ldc.i4.0`；是 ⇒ STATUS=ALREADY_PATCHED，不重写。
# 备份：首次执行会 `cp -a` 到 `<dll>.pre-commit-minreq.bak`（不动 loglabel-brush 的备份）。
set -e
ROOT=$(cd "$(dirname "$0")/../../.." && pwd)
APP="${APP:?必须指定 APP=<SourceTree app 目录>（如 APP=/tmp/stpkg/SourceTree/app-3.4.15）}"
FX="${FX:-$HOME/.local/share/ldp/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.11}"
DOTNET_BIN="${DOTNET_BIN:-$(command -v dotnet || echo /usr/share/dotnet/dotnet)}"
SRC="$APP/SourceTree.Api.UI.Wpf.dll"
BAK="$APP/SourceTree.Api.UI.Wpf.dll.pre-commit-minreq.bak"
TOOL_DIR="$ROOT/40-build/appliers/commit-minreq"

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

DLL="$TOOL_DIR/bin/Release/net10.0/linux-x64/commit-minreq.dll"
[ -f "$DLL" ] || DLL="$TOOL_DIR/bin/Release/net10.0/commit-minreq.dll"
[ -f "$DLL" ] || { echo "找不到构建产物 commit-minreq.dll" >&2; exit 1; }

# 幂等：先对**目标文件本身**判指纹（已 patch 则跳过，不重写）
if "$DOTNET_BIN" "$DLL" "$SRC" "" "$APP" --fx "$FX" --verify 2>/dev/null | grep -q "VERIFY=PATCHED"; then
    echo "已 patch 过（幂等跳过）: $SRC ($(stat -c%s "$SRC") 字节)"
    exit 0
fi

[ -f "$BAK" ] || cp -a "$SRC" "$BAK"
OUT="$SRC.tmp.$$"
"$DOTNET_BIN" "$DLL" "$BAK" "$OUT" "$APP" --fx "$FX" || {
    echo "patch 失败" >&2; rm -f "$OUT"; exit 1
}

if [ -f "$OUT" ]; then
    mv "$OUT" "$SRC"
    echo "已 patch: $SRC ($(stat -c%s "$SRC") 字节)"
else
    echo "已 patch 过（幂等跳过）: $SRC ($(stat -c%s "$SRC") 字节)"
fi
