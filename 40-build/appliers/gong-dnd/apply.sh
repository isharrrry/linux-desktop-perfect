#!/bin/sh
# applier：修 GongSolutions.Wpf.DragDrop..cctor 在 Linux 上抛 PlatformNotSupportedException 的问题。
#
# 为什么需要（K-1131/K-1132/K-1135）：
#   DragDrop..cctor 里调 DataFormats.GetDataFormat("GongSolutions.Wpf.DragDrop")，
#   Linux 侧该 API 由 src/Microsoft.DotNet.Wpf.Linux/src/shims/PresentationCore.OleApi.Stubs.cs
#   提供占位实现 ⇒ 抛 PlatformNotSupportedException ⇒ XamlParseException ⇒ RepoTabPanel 加载失败。
#   本 patch 把那一条 `call GetDataFormat` 改成 `pop` + `ldnull`（字段变 null，不抛异常）。
#
# 为什么不能只靠 SourceTree.exe 的注入（K-1135 的依赖）：
#   AddOrActivate 在 patch 前会崩（K-1131）；patch 后成功 ⇒ 提交列表填充 16 个提交（37801B 截图）。
#
# 用法：
#   sh 40-build/appliers/gong-dnd/apply.sh --check   # 只检查
#   sh 40-build/appliers/gong-dnd/apply.sh           # 注入，原地替换（先备份 .orig）
set -e
ROOT=$(cd "$(dirname "$0")/../../.." && pwd)
APP="${APP:-/tmp/stpkg/SourceTree/app-3.4.15}"
SRC="$APP/GongSolutions.Wpf.DragDrop.dll"
BAK="$APP/GongSolutions.Wpf.DragDrop.dll.orig"
TOOL_DIR="$ROOT/40-build/appliers/gong-dnd"

if [ "$1" = "--check" ]; then
    [ -f "$SRC" ] && echo "OK   GongSolutions.Wpf.DragDrop: $SRC ($(stat -c%s "$SRC") 字节)" || { echo "缺 $SRC"; exit 1; }
    exit 0
fi

[ -f "$SRC" ] || { echo "缺 $SRC"; exit 1; }

# 已经 patch 过则跳过（幂等）
if grep -qa "gongfix" "$SRC" 2>/dev/null; then
    echo "已 patch 过（跳过）"
    exit 0
fi

[ -f "$BAK" ] || cp "$SRC" "$BAK"

cd "$TOOL_DIR"
rm -rf bin obj
/usr/share/dotnet/dotnet build -c Release 2>&1 | grep -E "error|已用时间" | head -3
/usr/share/dotnet/dotnet bin/Release/net10.0/linux-x64/gong-dnd.dll "$BAK" "$SRC.tmp" || {
    echo "patch 失败" >&2; rm -f "$SRC.tmp"; exit 1
}
mv "$SRC.tmp" "$SRC"
echo "已 patch: $SRC ($(stat -c%s "$SRC") 字节)"
