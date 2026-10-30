#!/usr/bin/env bash
# T2 · WIC 闭环 harness 的**唯一运行入口**：把"环境怎么给"固化成一条命令，
# 免得每次手敲（手敲正是临时手法最容易偷偷留下来的地方）。
#
# 用法：
#   run-harness.sh                     # 严格模式（默认）：**不设 LD_LIBRARY_PATH**
#   run-harness.sh --temp-ole32-alias  # 临时模式：允许 libole32.dll.so 兜底解析 ole32.dll
#   run-harness.sh <模式> <png 路径>    # 额外参数原样透传给 harness
#
# 为什么会有"临时模式"：
#   PC 里的 `[DllImport("ole32.dll")]`（UnsafeNativeMethodsMilCoreApi.cs:1050 CoInitialize /
#   :1054 CoUninitialize，调用点 UnknownBitmapDecoder.cs:27,32）需要被映射到 libwpfwic.so。
#   那一行**已经加好**（src/Microsoft.DotNet.Wpf.Linux/src/shims/Win32ShimResolver.cs 的 WicMappedLibraries），
#   但**要一次 PC 重建才生效**。在 PC 重建之前，为了让 harness 能跑到 WIC 里面，
#   临时把 libwpfwic.so 复制成 `libole32.dll.so`（dlopen 的探测名之一）+ LD_LIBRARY_PATH 命中。
#   ⚠ 这是**部署期兜底**，绕过了 PC 的映射表 —— 不是实现，不许当常态。
#
# 怎么证明"临时手法已经不需要了"（PC 重建后**必须**做这一步）：
#   1) rm -f ../wic-shim/libole32.dll.so bin/Debug/libole32.dll.so
#   2) run-harness.sh                       # 严格模式，不设 LD_LIBRARY_PATH
#   3) 判定标准（两条都要）：
#        · 输出里**没有** `DllNotFoundException` 且消息里**不含** ole32.dll；
#        · `DECODER_BRANCH=CreateDecoderFromFileHandle…`、`CALLS_AFTER=[FileHandle=…]` 照旧。
#      若仍报 `DllNotFoundException: ole32.dll` ⇒ **PC 还没重建 / 映射没生效**。
#      此时**不要**改回临时模式交差（那等于用本地 hack 掩盖真路径），报"映射未生效"即可。
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
shim_dir="$here/../wic-shim"
shim="$shim_dir/libwpfwic.so"

mode="${1:-strict}"
export PATH="$HOME/.dotnet:$PATH"
export DISPLAY="${DISPLAY:-:99}"

[ -f "$shim" ] || { echo "SKIP=WIC shim 不存在：$shim"; echo "SKIP_REASON=先跑 wic-shim/build-wic-shim.sh"; exit 2; }

if [ "$mode" = "--temp-ole32-alias" ]; then
    [ -f "$shim_dir/libole32.dll.so" ] || {
        echo "SKIP=临时 alias 不在：$shim_dir/libole32.dll.so"
        echo "SKIP_REASON=cp -f libwpfwic.so libole32.dll.so（见 REPORT.md §18.5）"; exit 2; }
    export LD_LIBRARY_PATH="$shim_dir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
    echo "MODE=临时（LD_LIBRARY_PATH=$shim_dir：libole32.dll.so 兜底解析 ole32.dll）"
else
    echo "MODE=严格（不设 LD_LIBRARY_PATH：ole32.dll 只能靠 PC 的 WicMappedLibraries 解析）"
    if [ -f "$shim_dir/libole32.dll.so" ]; then
        echo "WARN=临时 alias 仍在：$shim_dir/libole32.dll.so"
        echo "WARN_NOTE=严格模式下它不会被用到，但请按 REPORT.md §18.5 删掉后再验一遍才算数"
    fi
fi

dotnet build "$here/DirectWrite.Linux.WicWriteClosedLoop.csproj" -c Debug -m:1 --nologo -v:m >/dev/null
# ⚠ 两侧必须 dlopen **同一个文件**（否则两张 g_objs 句柄表 ⇒ MILQueryInterface 返 E_HANDLE，
#   症状会伪装成"MIL 补丁没生效"）：
#     · PC 侧 resolver 认 WPF_LINUX_WIC_SHIM（映射 WindowsCodecs.dll / ole32.dll）
#     · MIL 侧 MilExternalHandleBridge 认 MILBRIDGE_WIC_SO（候选①，优先于 dladdr 目录）
#   所以两个 env 一起指到同一个绝对路径。判据由 harness 的 WIC_SHIM_MAPS 实测（=1 才算接线正确）。
exec env WPF_LINUX_WIC=1 "WPF_LINUX_WIC_SHIM=$shim" "MILBRIDGE_WIC_SO=$shim" \
    dotnet "$here/bin/Debug/DirectWrite.Linux.WicWriteClosedLoop.dll" "${@:2}"
