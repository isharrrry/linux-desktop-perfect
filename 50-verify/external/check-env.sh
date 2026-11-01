#!/bin/sh
# 测试前环境检查：列出 $FX 里所有「会被我们 patch 的」dll 的字节数，
# 与「原版 / 我们的版本」对照。**每次跑样本前先跑这个**，避免 K-409/K-413 那种
# 「两次测试结果不一致」的问题（根本原因是 $FX 里的 dll 状态不同）。
FX="${FX:-$HOME/.local/share/ldp/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.11}"
printf "%-32s %10s  %s\n" "文件" "字节" "判定"
check() {
    f="$1"; orig="$2"; ours="$3"; name="$4"
    s=$(stat -c%s "$FX/$f" 2>/dev/null || echo 0)
    if [ "$s" = "0" ]; then j="缺失"; elif [ "$s" = "$orig" ]; then j="原版"; elif [ -n "$ours" ] && [ "$s" = "$ours" ]; then j="我们的"; else j="未知(!)"; fi
    printf "%-32s %10s  %s\n" "$f" "$s" "$j"
}
check "WindowsBase.dll"             1113088 1109504
check "System.Drawing.Common.dll"   392192  393728
check "DirectWrite.Linux.Provider.dll" ""  103424
check "SkiaSharp.dll"               437808 ""
check "System.Private.Windows.GdiPlus.dll" 427816 ""
check "libwpfwic.so"                ""      81512
check "libwpfwin32.so"              ""      ""
