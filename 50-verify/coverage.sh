#!/usr/bin/env bash
# 覆盖率闸门登记（coverage）：本仓没有行覆盖率采集工具链（cobertura 产物 0 个），
# 这里登记的是「被 50-verify/gates.sh 与 50-verify/units/ 的机读断言校验的包范围」。
# 口径声明：kit 的 gated_line_ratio 在本仓语义是「被校验包的代码行 / 全部本仓源码行」，
# 不是行覆盖率。不要把它读成 coverage %。
#
# 包前缀（单引号 + 星号；kit 只认 [A-Za-z0-9_.] 组成的包名前缀）：
PKGS=(
  'PresentationFramework*' 'PresentationCore*' 'WindowsBase*' 'WpfGfx*'
  'milcore*' 'DirectWrite*' 'CycleStub*' 'ReachFramework*'
  'SystemPrinting*' 'a11y*' 'System*' 'MilBridge*' 'system*'
  'Provider*' 'PresentationBuildTasks*' 'UIAutomation*' 'Probe*'
  'WicClosedLoop*' 'WicWriteClosedLoop*' 'WicSeamProbe*'
  'FallbackCriteria*' 'FontEntryClosedLoop*' 'WiringSmoke*'
)

set -euo pipefail

[ ${#PKGS[@]} -gt 0 ] || { echo "coverage: 前缀清单为空" >&2; exit 1; }
dup=$(printf '%s\n' "${PKGS[@]}" | sort | uniq -d)
[ -z "$dup" ] || { echo "coverage: 前缀重复：$dup" >&2; exit 1; }
# 禁止把未入库/未被任何闸门校验的目录塞进来（如 upstream）：它们是分数工程，不是覆盖
case "${PKGS[*]}" in
  *upstream*) echo "coverage: 拒绝 upstream（未入库、未被任何闸门校验）" >&2; exit 1 ;;
esac

echo "coverage: 登记 ${#PKGS[@]} 个被校验包前缀（行覆盖口径见 kit gated_line_ratio 说明）"
