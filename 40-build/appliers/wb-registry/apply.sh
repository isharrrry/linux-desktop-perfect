#!/bin/sh
# applier：修 `WindowsBase` 在 Linux 上因 `Registry.CurrentUser == null` 而 SIGSEGV 的问题。
#
# 为什么需要（K-351/K-360）：
#   `BaseCompatibilityPreferences.SetMatchPackageSignatureMethodToPackagePartDigestMethodFromRegistry()`
#   假设 `Microsoft.Win32.Registry.CurrentUser` 非 null，直接调 `RegistryKey.OpenSubKey`
#   ⇒ Linux 上该属性返回 null ⇒ JIT 的 null check 解引用 null ⇒ SIGSEGV。
#   本 patch 把该方法体清空成单条 `ret`（等价「走默认路径」），验证后日志能从
#   「崩在静态初始化」推进到 `[FONT_FALLBACK]` 之后。
#
# 用法：
#   sh 40-build/appliers/wb-registry/apply.sh --check   # 只检查
#   sh 40-build/appliers/wb-registry/apply.sh           # 注入，产出 *.injected.dll
set -e
ROOT=$(cd "$(dirname "$0")/../../.." && pwd)
FX="${FX:-$HOME/.local/share/ldp/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.11}"
SRC="$FX/WindowsBase.dll"
OUT="${OUT:-$ROOT/40-build/appliers/wb-registry/WindowsBase.injected.dll}"

if [ "$1" = "--check" ]; then
    [ -f "$SRC" ] && echo "OK   WindowsBase: $SRC ($(stat -c%s "$SRC") 字节)" || { echo "缺 $SRC"; exit 1; }
    exit 0
fi

[ -f "$SRC" ] || { echo "缺 $SRC"; exit 1; }
# 如果已经是 patch 过的（1109504 左右），先提示
SZ=$(stat -c%s "$SRC")
if [ "$SZ" -lt 1111000 ]; then
    echo "⚠️  $SRC 可能已是 patch 版（$SZ 字节，原版约 1113088）—— 建议先从备份恢复" >&2
fi

cd "$ROOT/40-build/appliers/wb-registry"
/usr/bin/dotnet build -c Release --nologo >/dev/null 2>&1 || /usr/bin/dotnet build -c Release --nologo
/usr/bin/dotnet bin/Release/net10.0/linux-x64/wb-registry.dll "$SRC" "$OUT"
echo "注入产物：$OUT"
