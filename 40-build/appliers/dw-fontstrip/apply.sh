#!/bin/sh
# applier：让 `DirectWrite.Linux.Provider.FontTableStripper.TryStripTables` 直接返回 false。
# 原因见 Program.cs 头部注释（K-386～K-389）。
set -e
ROOT=$(cd "$(dirname "$0")/../../.." && pwd)
FX="${FX:-$HOME/.local/share/ldp/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.11}"
SRC="$FX/DirectWrite.Linux.Provider.dll"
OUT="${OUT:-$ROOT/40-build/appliers/dw-fontstrip/DirectWrite.Linux.Provider.injected.dll}"

if [ "$1" = "--check" ]; then
    [ -f "$SRC" ] && echo "OK   DirectWrite.Linux.Provider: $SRC ($(stat -c%s "$SRC") 字节)" || { echo "缺 $SRC"; exit 1; }
    exit 0
fi
[ -f "$SRC" ] || { echo "缺 $SRC"; exit 1; }
cd "$ROOT/40-build/appliers/dw-fontstrip"
/usr/bin/dotnet build -c Release --nologo >/dev/null 2>&1 || /usr/bin/dotnet build -c Release --nologo
/usr/bin/dotnet bin/Release/net10.0/linux-x64/dw-fontstrip.dll "$SRC" "$OUT"
echo "注入产物：$OUT"
