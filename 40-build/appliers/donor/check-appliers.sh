#!/usr/bin/env bash
# 债务 #13：**应用器审计**（只读）—— "被登记了但其实没生效"的闸门。
#   rc=0 全绿；rc=1 至少一条 miss（红）。机读行：APPLIER_AUDIT …／APPLIER_AUDIT_SUMMARY …
# 用法：
#   bash src/Linux/build/check-appliers.sh              # 真实树 + src/Linux/build/integration-wave.sh + 登记清单
#   bash src/Linux/build/check-appliers.sh --with-check # 额外跑每个应用器自己的 --check（只读，慢一些）
set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec python3 "$HERE/MilBridge/tools/applier-audit.py" --root "$HERE/../../.." "$@"
