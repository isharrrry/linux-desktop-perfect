#!/bin/sh
# applier：给 SourceTree.Api.UI.Wpf 的 LogLabelControl 补 Brush 初始化。
#
# 为什么需要（K-1427 / K-1428）：
#   History 面板首帧渲染时，`LogLabelControl.OnRender` 先读 `_backgroundBrush`（`IL_0009 ldfld`）
#   后 `callvirt get_Color()`（`IL_000f`），而赋值的 `stfld _backgroundBrush` 在 `IL_0030`
#   ⇒ 首帧读到 null ⇒ NullReferenceException（`LogLabelControl.OnRender` ← `UIElement.Arrange`
#   ← `ContentPresenter.ArrangeOverride`）⇒ 一切到 History 视图就崩、面板出不来。
#   既有事实：`_backgroundBrush` 只有 `OnRender` 自身 1 处 stfld；`_borderPen`/`_backgroundGreyBrush`
#   全程序集 0 处 stfld；`UpdateBrushes()` 是空方法（`IL_0000: ret`）而 `.ctor` 调它 ⇒ brushes 从未初始化。
#
# 修法：在 `LogLabelControl::.ctor` 的每个 `ret` 前插 5 条指令
#   ldarg.0; ldarg.0; call get_Background(); newobj SolidColorBrush(Color); stfld _backgroundBrush
#
# 为什么要 applier（不能只手工 patch）：
#   `40-build/appliers/apply.mjs` 只做文本改写、不产出二进制；`SourceTree.Api.UI.Wpf.dll`
#   若被重新部署/重装，手工 patch 会丢 ⇒ History 面板再次崩。本 applier 与 gong-dnd / wb-registry
#   同构，可重复执行（幂等）。
#
# 用法：
#   APP=/tmp/stpkg/SourceTree/app-3.4.15 sh 40-build/appliers/loglabel-brush/apply.sh --check
#   APP=/tmp/stpkg/SourceTree/app-3.4.15 sh 40-build/appliers/loglabel-brush/apply.sh
#
# 幂等判据：Program.cs 逐 ret 比对「紧邻 5 条指令」指纹；全部命中 ⇒ STATUS=ALREADY_PATCHED，不重写。
# 备份：首次执行会 `cp -a` 到 `<dll>.pre-loglabel-brush.bak`（不占用既有 `.orig`，那份是更早的备份）。
set -e
ROOT=$(cd "$(dirname "$0")/../../.." && pwd)
APP="${APP:?必须指定 APP=<SourceTree app 目录>（如 APP=/tmp/stpkg/SourceTree/app-3.4.15）}"
FX="${FX:-$HOME/.local/share/ldp/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.11}"
DOTNET_BIN="${DOTNET_BIN:-$(command -v dotnet || echo /usr/share/dotnet/dotnet)}"
SRC="$APP/SourceTree.Api.UI.Wpf.dll"
BAK="$APP/SourceTree.Api.UI.Wpf.dll.pre-loglabel-brush.bak"
TOOL_DIR="$ROOT/40-build/appliers/loglabel-brush"

if [ "$1" = "--check" ]; then
    [ -f "$SRC" ] && echo "OK   $SRC ($(stat -c%s "$SRC") 字节)" || { echo "缺 $SRC"; exit 1; }
    [ -f "$FX/PresentationCore.dll" ] && echo "OK   $FX/PresentationCore.dll ($(stat -c%s "$FX/PresentationCore.dll") 字节)" || { echo "缺 $FX/PresentationCore.dll"; exit 1; }
    [ -f "$TOOL_DIR/Program.cs" ] && echo "OK   $TOOL_DIR/Program.cs" || { echo "缺 $TOOL_DIR/Program.cs"; exit 1; }
    exit 0
fi

[ -f "$SRC" ] || { echo "缺 $SRC"; exit 1; }
[ -f "$BAK" ] || cp -a "$SRC" "$BAK"

cd "$TOOL_DIR"
rm -rf bin obj
"$DOTNET_BIN" build -c Release --nologo 2>&1 | grep -E "error|已用时间|Elapsed" | head -5

DLL="$TOOL_DIR/bin/Release/net10.0/linux-x64/loglabel-brush.dll"
[ -f "$DLL" ] || DLL="$TOOL_DIR/bin/Release/net10.0/loglabel-brush.dll"
[ -f "$DLL" ] || { echo "找不到构建产物 loglabel-brush.dll" >&2; exit 1; }

# 幂等：先对**目标文件本身**判指纹（补齐则跳过，不重写）
if "$DOTNET_BIN" "$DLL" "$SRC" "" "$APP" --fx "$FX" --verify 2>/dev/null | grep -q "VERIFY=PATCHED"; then
    echo "已 patch 过（幂等跳过）: $SRC ($(stat -c%s "$SRC") 字节)"
    exit 0
fi

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
