#!/usr/bin/env bash
# 派生构建单一入口 #构建入口
# 用法：40-build/build.sh [项目相对路径] [--restore-only]
# 五个必需属性见 40-build/appliers/build-flags.json（少一个就会走 Windows 专属工具链）
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TREE="${LDP_DERIVED_TREE:-$ROOT/40-build/build/wpf}"
PROJ="${1:-src/Microsoft.DotNet.Wpf/src/WindowsBase/WindowsBase.csproj}"
MODE="${2:-build}"
FLAGS=(-p:UsingToolXliff=false -p:RunApiCompat=false -p:RunRefApiCompat=false -p:RunNetFrameworkApiCompat=false -p:PerlCommand=/usr/bin/perl)
[ -d "$TREE" ] || { echo "ERROR 派生树不存在：$TREE（先 node 40-build/appliers/apply.mjs）" >&2; exit 1; }
cd "$TREE"
dotnet restore "$PROJ" --disable-parallel "${FLAGS[@]}"
[ "$MODE" = "--restore-only" ] && exit 0
dotnet build "$PROJ" -c Release --no-restore "${FLAGS[@]}"
