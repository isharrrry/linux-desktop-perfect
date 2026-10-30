// ABI 自检：把 dwlinux.h 的结构布局打印出来，并把「编译期断言」再确认一遍。
//
// 编译期 _Static_assert 已经保证不成立就编不过；这个可执行文件是给判据与报告留的
// 可读证据（与 50-verify/text/shaping.mjs 的读数一起看）。
//
// 退出码：0 = 全部断言成立；1 = 有断言失败。

#include "../src/dwlinux.h"

#include <stddef.h>
#include <stdio.h>
#include <string.h>

static int g_fail = 0;

#define CHECK(cond, what)                                                                  \
    do {                                                                                   \
        if (!(cond)) {                                                                     \
            printf("  !! %s\n", what);                                                     \
            g_fail = 1;                                                                    \
        } else {                                                                           \
            printf("  ok %s\n", what);                                                     \
        }                                                                                  \
    } while (0)

_Static_assert(sizeof(DwpPoint) == 12, "DwpPoint 必须是 3 个 4 字节字段（float x, float y, int kind）");
_Static_assert(offsetof(DwpPoint, x) == 0, "DwpPoint.x 必须在 0");
_Static_assert(offsetof(DwpPoint, y) == 4, "DwpPoint.y 必须在 4");
_Static_assert(offsetof(DwpPoint, kind) == 8, "DwpPoint.kind 必须在 8");
_Static_assert(offsetof(DwpMatch, index) == 256 + 1024, "DwpMatch.index 必须在 family+file 之后");
_Static_assert(DWP_E_INVALIDARG == -1, "DWP_E_INVALIDARG 固定 -1（SAFE-013）");
_Static_assert(DWP_E_FC_UNAVAILABLE == -403, "错误码与 03 §3.4.1 一致");
_Static_assert(DWP_E_OUTLINE_EMPTY == -412, "错误码与 03 §3.4.5 一致");

int main(void)
{
    printf("dwlinux ABI 自检\n");
    printf("  sizeof(DwpPoint)=%zu  sizeof(DwpMatch)=%zu\n", sizeof(DwpPoint), sizeof(DwpMatch));
    printf("  offsetof(DwpPoint,kind)=%zu  offsetof(DwpMatch,index)=%zu\n",
           offsetof(DwpPoint, kind), offsetof(DwpMatch, index));
    CHECK(sizeof(DwpPoint) == 12, "DwpPoint 12 字节");
    CHECK(sizeof(DwpMatch) == 256 + 1024 + 4, "DwpMatch = family256 + file1024 + index4");
    CHECK(strcmp(dwp_error(DWP_OK), "ok") == 0, "dwp_error(0) = ok");
    CHECK(strstr(dwp_error(DWP_E_OUTLINE_EMPTY), "LDP-E-TEXT-412") != NULL, "错误码文案带 LDP-E-TEXT-412");
    CHECK(strstr(dwp_version(), "libdwlinux") != NULL, "版本串可读");
    printf("  %s\n", g_fail ? "ABI 自检：FAIL" : "ABI 自检：PASS");
    return g_fail ? 1 : 0;
}
