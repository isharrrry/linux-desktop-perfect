/* libdwlinux —— text-stack 的公开 ABI（SYS-4，MOD-31/32/33/35）
 *
 * 设计依据：30-design/设计文档包/03-软件设计说明.md §3.4、04 §2.3
 * 契约：IF-001~IF-006（DirectWrite 字体面）的 Linux 落点；判据 ACC-013~ACC-016。
 *
 * 纪律：
 *   ①本文件是 ABI 的唯一声明处，改它必须同跑 tests/text_abi.c 与 ACC-013~016；
 *   ②所有入口先做空值与长度校验（SAFE-013），失败返回负错误码，不静默；
 *   ③错误码与 03 §3.4 一致（LDP-E-TEXT-403~412），文案由 dwp_error() 给。
 */
#ifndef DWPLINUX_H
#define DWPLINUX_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* 错误码（负值；0 = 成功） */
#define DWP_OK              0
#define DWP_E_FC_UNAVAILABLE  (-403) /* fontconfig 不可用（03 §3.4.1） */
#define DWP_E_NO_MATCH        (-404) /* 无匹配字体 */
#define DWP_E_SHAPE_FAILED    (-405) /* shaping 失败 */
#define DWP_E_CLUSTER_RANGE   (-406) /* cluster 越界 */
#define DWP_E_LINE_FAILED     (-407) /* 行布局失败（断行器不可用） */
#define DWP_E_FACE_FAILED     (-411) /* 字体解析失败 */
#define DWP_E_OUTLINE_EMPTY   (-412) /* 轮廓为空 */
#define DWP_E_INVALIDARG      (-1)   /* 空指针/容量不足（SAFE-013） */

/* 匹配结果：族名、字体文件、face index（MOD-31） */
typedef struct DwpMatch {
    char family[256];
    char file[1024];
    int  index;
} DwpMatch;

/* 轮廓点（MOD-35）：kind 0=起点 1=线段 2=三次曲线控制点 */
typedef struct DwpPoint {
    float x;
    float y;
    int   kind;
} DwpPoint;

/* 版本串（判据读数用；不含时间戳，保证可复算） */
const char *dwp_version(void);

/* 加载 dlopen 依赖；返回 0 或负错误码 */
int dwp_init(void);

/* 错误码 → 文案（不返回 NULL） */
const char *dwp_error(int code);

/* 字体枚举（MOD-31）：返回族数；负值为错误码 */
int dwp_font_count(void);
/* 第 index 个族名写入 buf（含结尾 0）；返回 0 或负错误码 */
int dwp_font_family(int index, char *buf, int cap);

/* 族名匹配（MOD-31）：回退链首项写入 out；返回 0 或负错误码 */
int dwp_match(const char *family, DwpMatch *out);

/* shaping（MOD-32）：返回字形数；glyphs/advances/clusters 可为 NULL（只取计数）。
 * size 为像素em；cap 为三个数组的容量。 */
int dwp_shape(const char *family, const char *text, int size,
              uint32_t *glyphs, int32_t *advances, uint32_t *clusters, int cap);

/* 断行（MOD-33）：UAX#14 机会点的 UTF-8 字节偏移写入 byte_offsets；
 * 返回断点数（含末尾）；负值为错误码。 */
int dwp_line_breaks(const char *text, int *byte_offsets, int cap);

/* 轮廓（MOD-35）：取码位 cp 的字形轮廓点；返回点数；负值为错误码。 */
int dwp_outline(const char *family, uint32_t cp, int size, DwpPoint *pts, int cap);

#ifdef __cplusplus
}
#endif

#endif /* DWPLINUX_H */
