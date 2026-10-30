/* probe_refcount.c —— libwpfwic.so 的**引用计数配平**实测（shim 级闭环，不需要 PC/X/harness）
 *
 * 为什么需要它：PC 的 `SafeMILHandle.ReleaseHandle()` 走 MilCore 的 `MILRelease`
 * （SafeMILHandle.cs:61-63），**不经过本 shim**。T1 的 MILQueryInterface 放行时必须
 * `WicShim_AddRef`，并且 MILRelease 必须转发 `WicShim_Release`——两边账本配不上，
 * 本 shim 的句柄表（上限 256）就单边漏，解到第 257 张图直接创建失败。
 * 本探针给出 shim 侧那一半的可执行判据。
 *
 * 判据：① 真实 PNG 解码对象走一遍 obj_drop ⇒ 活句柄数回落到基线；
 *       ② 500 次 AddRef×3 + Release×3 ⇒ 活句柄数不变、**高水位不涨**；
 *       ③ 未登记句柄 AddRef/Release 返 0 且不崩。
 * Skia 缺失导致解不出图时为 **SKIP(exit 2)**，不报 PASS——不假绿。 */
#include <dlfcn.h>
#include <fcntl.h>
#include <stdint.h>
#include <stdio.h>
#include <unistd.h>

typedef int32_t (*pfn_factory)(uint32_t, void **);
typedef int32_t (*pfn_selftest)(const char *, int32_t *, int32_t *, uint32_t *);
typedef int32_t (*pfn_h1)(intptr_t);
typedef int32_t (*pfn_h0)(void);

int main(int argc, char **argv)
{
    if (argc < 3) { printf("用法: probe_refcount <libwpfwic.so> <png>\n"); return 2; }
    void *h = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (!h) { printf("SKIP: dlopen 失败: %s\n", dlerror()); return 2; }

    pfn_factory   factory  = (pfn_factory)  dlsym(h, "WICCreateImagingFactory_Proxy");
    pfn_selftest  selftest = (pfn_selftest) dlsym(h, "WicShim_SelfTest");
    pfn_h1        addref   = (pfn_h1)       dlsym(h, "WicShim_AddRef");
    pfn_h1        release  = (pfn_h1)       dlsym(h, "WicShim_Release");
    pfn_h0        count    = (pfn_h0)       dlsym(h, "WicShim_HandleCount");
    pfn_h0        peak     = (pfn_h0)       dlsym(h, "WicShim_PeakHandleCount");
    if (!factory || !selftest || !addref || !release || !count || !peak) {
        printf("SKIP: 导出缺失（旧版 shim？）\n"); return 2;
    }

    int32_t c0 = count(), p0 = peak();
    printf("BASELINE live=%d peak=%d\n", c0, p0);
    int fails = 0;

    /* ① 真实解码对象：SelfTest 内部 obj_new → decode_pixels → obj_drop */
    int32_t w = 0, hh = 0; uint32_t px = 0;
    int32_t hr = selftest(argv[2], &w, &hh, &px);
    if (hr != 0) { printf("SKIP: 解不出图 hr=0x%08X（Skia 缺失或 PNG 不可读）\n", (unsigned)hr); return 2; }
    printf("SELFTEST ok %dx%d px=%08X  live=%d\n", w, hh, px, count());
    if (count() != c0) { printf("FAIL: 真实解码对象没回落基线 live=%d 期望=%d\n", count(), c0); fails++; }
    else printf("OK① 真实解码对象释放后活句柄数回落到基线\n");

    /* ② 工厂句柄：500 次 AddRef×3 + Release×3 */
    void *p = NULL;
    hr = factory(0x01000000u, &p);
    if (hr != 0 || !p) { printf("FAIL: 建工厂 hr=0x%08X\n", (unsigned)hr); return 1; }
    intptr_t fh = (intptr_t)p;
    if (count() != c0 + 1) { printf("FAIL: 建工厂后 live=%d 期望=%d\n", count(), c0 + 1); fails++; }
    int32_t p1 = peak();
    for (int i = 0; i < 500; i++) {
        if (addref(fh) != 2) { printf("FAIL: 第%d次 AddRef 计数错\n", i); fails++; break; }
        if (addref(fh) != 3) { printf("FAIL: 第%d次 AddRef#2 计数错\n", i); fails++; break; }
        if (release(fh) != 2) { printf("FAIL: 第%d次 Release 计数错\n", i); fails++; break; }
        if (release(fh) != 1) { printf("FAIL: 第%d次 Release#2 计数错\n", i); fails++; break; }
    }
    if (count() != c0 + 1) { printf("FAIL: 500 轮后 live=%d 期望=%d（表在漏）\n", count(), c0 + 1); fails++; }
    else printf("OK② 500 轮 AddRef×2+Release×2 后 live=%d（未变），peak=%d\n", count(), peak());
    if (peak() != p1) { printf("FAIL: 高水位从 %d 涨到 %d（回收是花架子）\n", p1, peak()); fails++; }
    else printf("OK②' 高水位未涨（=%d）\n", peak());

    /* ③ 未登记句柄 */
    if (addref(9999) != 0 || release(9999) != 0) { printf("FAIL: 未登记句柄未返 0\n"); fails++; }
    else if (count() != c0 + 1) { printf("FAIL: 未登记句柄调用改变了表大小\n"); fails++; }
    else printf("OK③ 未登记句柄 AddRef/Release 返 0 且表不变\n");

    /* 收尾：Release 到 0 ⇒ 回收 */
    if (release(fh) != 0) { printf("FAIL: 工厂句柄 Release 未归零\n"); fails++; }
    if (count() != c0) { printf("FAIL: 收尾 live=%d 期望=%d\n", count(), c0); fails++; }
    else printf("OK④ 引用归零后活句柄数回落到基线（peak=%d 保留高水位）\n", peak());

    printf("%s\n", fails == 0 ? "REFCNT_BALANCE=PASS" : "REFCNT_BALANCE=FAIL");
    return fails == 0 ? 0 : 1;
}
