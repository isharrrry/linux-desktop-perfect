#!/bin/sh
# applier：SourceTree 在 Linux 上的启动补丁（K-1497）。
# 用法: sh apply.sh <appDir> [--check]
set -eu
APP_DIR=${1:?用法: sh apply.sh <appDir> [--check]}
HERE=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
exec "${DOTNET:-dotnet}" run --project "$HERE" -- "$APP_DIR" "${2:-}"
