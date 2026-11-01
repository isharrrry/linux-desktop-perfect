#!/bin/sh
# applier：把「Windows Forms 自绘」所需的元数据注入到自产 System.Drawing.Common。
#
# 为什么需要它（K-249～K-342 的完整调查结论）：
#   WinForms 的 `Control.PaintBackColor` 走 `DeviceContextHdcScope`，要求 `Graphics` 实现
#   `System.Drawing.IGraphicsContextInfo`（GdiPlus 程序集里的 internal 接口）。
#   而 .NET **不允许给已有类型追加接口**，且该接口 `internal` 无法在编译期引用。
#   ⇒ 唯一可行途径：**在源码里写占位实现（编译器生成正确签名 blob），再用 Cecil
#      把占位类型的 TypeRef 重定向到官方类型**（签名 blob 结构不变，运行时能接受）。
#
# 用法：
#   sh 40-build/appliers/sdc-inject/apply.sh            # 注入到构建产物
#   sh 40-build/appliers/sdc-inject/apply.sh --check    # 只检查环境
#
# 前置：先跑 `dotnet build SystemDrawingCommon.Linux.csproj -c Release`
set -e

ROOT=$(cd "$(dirname "$0")/../../.." && pwd)
FX="${FX:-$HOME/.local/share/ldp/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.11}"
SDC="$ROOT/40-build/system-drawing-common/bin/Release/linux-x64/System.Drawing.Common.dll"
OUT="${OUT:-$ROOT/40-build/system-drawing-common/bin/Release/linux-x64/System.Drawing.Common.injected.dll}"

if [ "$1" = "--check" ]; then
    [ -f "$SDC" ] && echo "OK   源 SDC: $SDC ($(stat -c%s "$SDC") 字节)" || { echo "缺 $SDC"; exit 1; }
    [ -f "$FX/System.Private.Windows.GdiPlus.dll" ] && echo "OK   GdiPlus" || { echo "缺 GdiPlus"; exit 1; }
    [ -f "$FX/System.Private.Windows.Core.dll" ] && echo "OK   Core" || { echo "缺 Core"; exit 1; }
    exit 0
fi

[ -f "$SDC" ] || { echo "先构建 SDC：cd $ROOT/40-build/system-drawing-common && dotnet build -c Release"; exit 1; }

cd "$ROOT/40-build/appliers/sdc-inject"
/usr/bin/dotnet build -c Release --nologo >/dev/null 2>&1 || dotnet build -c Release --nologo
/usr/bin/dotnet bin/Release/net10.0/linux-x64/sdc-inject.dll "$SDC" "$FX/System.Private.Windows.GdiPlus.dll" "$FX/System.Private.Windows.Core.dll" "$OUT"
echo "注入产物：$OUT"
