#!/usr/bin/env bash
# 从 zip 一键重建可启动的 SourceTree 部署（K-1497 / K-1500 / K-1502 的收口）。
#
# 做的事（全部幂等，任一失败即非零退出）：
#   1. 解压 app zip 到 <appDir> 的父目录（zip 内路径为 SourceTree/app-3.4.15/…）
#   2. 跑 applier：app 侧 13 处 + 运行时 PresentationCore.dll 2 处
#   3. 重建 shim 并部署 libwpfwin32.so（先备份）
#   4. 生成 ~/bin/{which,git,hg} 启动前置包装（RepoProcess 读竞态 + hg 版本）
#
# 用法:
#   bash 40-build/appliers/sourcetree-boot/rebuild.sh [--zip <zip>] [--app <appDir>] [--runtime <rtDir>] [--no-shim]
# 默认:
#   --zip     ~/.dsh/attachments/v1/files/9e/9e7d0cae…/SourceTree.zip
#   --app     /tmp/stpkg/SourceTree/app-3.4.15
#   --runtime ~/.local/share/ldp/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.11
set -uo pipefail

ZIP=""; APP=""; RT=""; SKIP_SHIM=0
while [ $# -gt 0 ]; do
  case "$1" in
    --zip)      ZIP="$2"; shift 2 ;;
    --app)      APP="$2"; shift 2 ;;
    --runtime)  RT="$2"; shift 2 ;;
    --no-shim)  SKIP_SHIM=1; shift ;;
    -h|--help)  sed -n '2,16p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "未知参数: $1" >&2; exit 2 ;;
  esac
done

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
DEFAULT_ZIP="$HOME/.dsh/attachments/v1/files/9e/9e7d0cae37b9f5ddf22d0985c47a034d6c198a44114521bf7d36e2e6cf4f82da/SourceTree.zip"
ZIP="${ZIP:-$DEFAULT_ZIP}"
APP="${APP:-/tmp/stpkg/SourceTree/app-3.4.15}"
RT="${RT:-$HOME/.local/share/ldp/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.11}"

say() { printf '\n== %s\n' "$*"; }
die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

[ -f "$ZIP" ] || die "找不到 zip: $ZIP"
[ -f "$RT/PresentationCore.dll" ] || die "运行时目录不对（缺 PresentationCore.dll）: $RT"
[ -f "$ROOT/40-build/appliers/sourcetree-boot/apply.sh" ] || die "找不到 applier: $ROOT/40-build/appliers/sourcetree-boot/apply.sh"

ts=$(date +%Y%m%d-%H%M%S)
UNZIP_TO="$(dirname "$(dirname "$APP")")"

say "1/6 解压 app → $UNZIP_TO"
mkdir -p "$UNZIP_TO"
unzip -q -o "$ZIP" -d "$UNZIP_TO" || die "解压失败"
if [ ! -f "$APP/SourceTree.exe" ]; then
  # 兼容 zip 根不含顶层目录前缀的布局
  unzip -q -o "$ZIP" -d "$(dirname "$APP")" || die "解压失败（备用布局）"
fi
[ -f "$APP/SourceTree.exe" ] || die "解压后缺 SourceTree.exe: $APP"
# zip 来自 Windows，解压后 .exe 没有执行位；SourceTree 会 Process.Start 调 tools/ 下的 exe
# （putty/pageant.exe、7z.exe 等），缺执行位会抛 Win32Exception(13) 并打崩进程。
find "$APP" -name '*.exe' -exec chmod +x {} + 2>/dev/null || true
echo "   SourceTree.exe $(stat -c%s "$APP/SourceTree.exe") B，dll $(ls "$APP"/*.dll | wc -l) 个，exe 已加执行位"

say "2/6 放置 SourceTree.runtimeconfig.json（zip 里没有；缺它 host 会报 libhostpolicy.so 缺失）"
cp "$ROOT/40-build/appliers/sourcetree-boot/SourceTree.runtimeconfig.json" "$APP/SourceTree.runtimeconfig.json" \
  || die "复制 runtimeconfig.json 失败"
echo "   已写 $APP/SourceTree.runtimeconfig.json"

# System.ComponentModel.Composition 4.0.0.0：zip 里没有，SourceTree.App.OnStartup 会 FileNotFound。
# 按优先级找本地已有副本（本机实测可用的是 icd-run 那份），再退回 NuGet 缓存。
COMP=""
for c in "$ROOT/40-build/appliers/sourcetree-boot/System.ComponentModel.Composition.dll" \
         "$HOME/icd-run-213945/app/System.ComponentModel.Composition.dll" \
         "$(ls -1 "$HOME"/.nuget/packages/system.componentmodel.composition/*/lib/net10.0/System.ComponentModel.Composition.dll 2>/dev/null | tail -1)"; do
  if [ -n "$c" ] && [ -f "$c" ]; then COMP="$c"; break; fi
done
[ -n "$COMP" ] || die "找不到 System.ComponentModel.Composition.dll（放在 applier 目录或 icd-run 或 NuGet 缓存里任一即可）"
cp "$COMP" "$APP/System.ComponentModel.Composition.dll" || die "复制 composition dll 失败"
echo "   System.ComponentModel.Composition ← $COMP ($(stat -c%s "$COMP") B)"

say "3/6 应用 applier（app + runtime）"
if [ -f "$RT/PresentationCore.dll" ] && [ ! -f "$RT/PresentationCore.dll.bak-$ts" ]; then
  cp "$RT/PresentationCore.dll" "$RT/PresentationCore.dll.bak-$ts"
  echo "   已备份 PresentationCore.dll → .bak-$ts"
fi
sh "$ROOT/40-build/appliers/sourcetree-boot/apply.sh" "$APP" --runtime "$RT" || die "applier 失败"

say "4/6 重建 shim 并部署 libwpfwin32.so"
if [ "$SKIP_SHIM" -eq 0 ]; then
  if ! bash "$ROOT/40-build/native-shim/build-shim.sh" >/tmp/rebuild-shim.log 2>&1; then
    tail -20 /tmp/rebuild-shim.log >&2
    die "build-shim 失败（完整日志 /tmp/rebuild-shim.log）"
  fi
  SO="$ROOT/40-build/native-shim/bin/libwpfwin32.so"
  [ -f "$SO" ] || die "缺产物: $SO"
  if [ -f "$RT/libwpfwin32.so" ] && [ ! -f "$RT/libwpfwin32.so.bak-$ts" ]; then
    cp "$RT/libwpfwin32.so" "$RT/libwpfwin32.so.bak-$ts"
    echo "   已备份 libwpfwin32.so → .bak-$ts"
  fi
  cp "$SO" "$RT/libwpfwin32.so"
  echo "   已部署 $(stat -c%s "$SO") B → $RT/libwpfwin32.so"
else
  echo "   --no-shim：跳过"
fi

say "5/6 生成 ~/bin 启动前置包装（which/git/hg）"
mkdir -p "$HOME/bin"
cat > "$HOME/bin/which" <<'EOS'
#!/bin/sh
# RepoProcess 读子进程输出有竞态：进程退出过快会抛
# InvalidOperationException: Cannot process request because the process has exited
sleep 0.6
if [ "$1" = "git" ]; then echo "$HOME/bin/git"; exit 0; fi
if [ "$1" = "hg" ]; then echo "$HOME/bin/hg"; exit 0; fi
/usr/bin/which "$@"
sleep 0.6
EOS
cat > "$HOME/bin/git" <<'EOS'
#!/bin/sh
# 同上：给自己一点存活时间，避免调用方读 stdout 时进程已退
sleep 0.4
/usr/bin/git "$@"
rc=$?
sleep 0.4
exit $rc
EOS
cat > "$HOME/bin/hg" <<'EOS'
#!/bin/sh
# Mercurial 兼容判断是 version.StartsWith("3"/"4")，故裸输出主版本号
echo "4.9.1"
EOS
chmod +x "$HOME/bin/which" "$HOME/bin/git" "$HOME/bin/hg"
echo "   已写 $HOME/bin/{which,git,hg}"

say "6/6 完成"
cat <<EOM
启动：
  PATH=\$HOME/bin:\$PATH DISPLAY=:0 bash $ROOT/70-operate/启动器/sourcetree --profile rdp
取证（import 在本环境不可用，必须 windowmap + xwd）：
  xdotool windowmap <win> && xwd -id <win> -out /tmp/x.xwd && convert /tmp/x.xwd /tmp/x.png
本次备份（可还原）：
  $RT/PresentationCore.dll.bak-$ts
  $RT/libwpfwin32.so.bak-$ts
EOM
