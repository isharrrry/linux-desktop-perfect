/* probe_describe.c —— 两用：① M7b 的取证 CLI（给一个句柄，print 它在 shim 里是什么）
 *                              ② 自检（--selftest）：造一个 4x3 内存位图 → 描述必须匹配;
 *                                 并做**突变自查**（虚构句柄必须返回 E_INVALIDARG 且写 <unknown>）——
 *                                 若虚构句柄也"看起来正常"，本探针必须红。*/
 *
 * 【这个探针凭什么可信（不必信任作者，可以复算）】
 *   probe_describe libwpfwic.so --selftest
 *   · 自检 1：真造 4x3 内存位图 ⇒ 描述必须含 4x3 / ownedByWic=1 / foreign=0
 *   · 自检 2（突变）：虚构句柄 0x7ffff0 ⇒ **必须** E_INVALIDARG 且写 <unknown>
 *   两向都验：只会通过的检查不是检查。
#include <dlfcn.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
typedef struct { uint32_t d1; uint16_t d2, d3; uint8_t d4[8]; } guid_t;
static guid_t BGRA = {0x6fddc324,0x4e03,0x4bfe,{0xb1,0x85,0x3d,0x77,0x76,0x8d,0xc9,0x0f}};
int main(int argc, char **argv)
{
    if (argc < 3) { printf("用法: probe_describe <libwpfwic.so> <handle|--selftest>\n"); return 2; }
    void *h = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (!h) { printf("SKIP dlopen: %s\n", dlerror()); return 2; }
    int32_t (*desc)(intptr_t, char *, size_t) = dlsym(h, "WicShim_DescribeHandle");
    if (!desc) { printf("SKIP 无 WicShim_DescribeHandle（旧 shim）\n"); return 2; }

    if (strcmp(argv[2], "--selftest") != 0) {
        char buf[512]; intptr_t hh = (intptr_t)strtoull(argv[2], NULL, 0);
        int32_t hr = desc(hh, buf, sizeof buf);
        printf("DESCRIBE h=0x%llx hr=0x%08X\n  %s\n", (unsigned long long)hh, (unsigned)hr, buf);
        return hr == 0 ? 0 : 1;
    }

    int fails = 0;
    int32_t (*mkf)(uint32_t, void **) = dlsym(h, "WICCreateImagingFactory_Proxy");
    int32_t (*mkbm)(void *, uint32_t, uint32_t, guid_t *, uint32_t, uint32_t, unsigned char *, void **) =
        dlsym(h, "IWICImagingFactory_CreateBitmapFromMemory_Proxy");
    void *factory = NULL; mkf(0x01000000u, &factory);
    unsigned char px[4*3*4]; memset(px, 0x11, sizeof px);
    void *bm = NULL;
    int32_t hr = mkbm(factory, 4, 3, &BGRA, 16, sizeof px, px, &bm);
    char buf[512] = {0};
    if (hr != 0 || !bm) { printf("FAIL 建内存位图\n"); return 1; }
    desc((intptr_t)bm, buf, sizeof buf);
    printf("自检 1（真实句柄）：%s\n", buf);
    if (strstr(buf, "4x3") == NULL) { printf("  **FAIL** 描述里没有 4x3\n"); fails++; }
    if (strstr(buf, "ownedByWic=1") == NULL) { printf("  **FAIL** 应 ownedByWic=1\n"); fails++; }
    if (strstr(buf, "foreign=0") == NULL) { printf("  **FAIL** 应 foreign=0\n"); fails++; }

    /* 突变自查：虚构句柄必须"答不上来"，否则本探针是假绿 */
    const intptr_t BOGUS = (intptr_t)0x7FFFF0;
    char buf2[512] = {0};
    int32_t hr2 = desc(BOGUS, buf2, sizeof buf2);
    printf("自检 2（突变：虚构句柄 0x%llx）：hr=0x%08X buf=%s\n", (unsigned long long)BOGUS, (unsigned)hr2, buf2);
    if (hr2 != (int32_t)0x80070057 || strstr(buf2, "<unknown>") == NULL) {
        printf("  **FAIL** 虚构句柄必须 E_INVALIDARG + <unknown>（否则描述不可信）\n"); fails++; }

    printf("%s（fail=%d）\n", fails == 0 ? "DESCRIBE_PROBE=PASS" : "DESCRIBE_PROBE=FAIL", fails);
    return fails == 0 ? 0 : 1;
}
