/* mscms 面探针（ACC-019）：用 libwpfwic 导出的 mscms 入口做一次真实的色彩转换。
 * 口径：输入/输出都用同一份 ICC（sRGB）→ 转换结果应与输入**逐字节相同**（恒等变换）；
 *   若引擎没接上，要么 OpenColorProfileW 返回 NULL、要么 TranslateColors 返回 0 —— 都如实报出。 */
#define _GNU_SOURCE
#include <dlfcn.h>
#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

typedef void *(*fn_open)(void *, const uint16_t *, uint32_t, uint32_t, uint32_t);
typedef void *(*fn_multi)(void **, uint32_t, uint32_t *, uint32_t, uint32_t, uint32_t);
typedef int (*fn_translate)(void *, void *, uint32_t, uint32_t, void *, uint32_t);
typedef int (*fn_deltransform)(void *);
typedef int (*fn_close)(void *);

int main(int argc, char **argv)
{
    const char *icc_path = argc > 1 ? argv[1] : "40-build/wic-dwrite/testdata/srgb.icc";
    FILE *f = fopen(icc_path, "rb");
    if (!f) { printf("{\"ok\":false,\"error\":\"没有 ICC 文件（先用 Python 的 ImageCms 导一份）\"}\n"); return 1; }
    fseek(f, 0, SEEK_END); long n = ftell(f); fseek(f, 0, SEEK_SET);
    uint8_t *icc = malloc((size_t)n);
    if (fread(icc, 1, (size_t)n, f) != (size_t)n) { fclose(f); return 2; }
    fclose(f);

    void *h = dlopen("40-build/wic-dwrite/bin/libwpfwic.so", RTLD_NOW);
    if (!h) h = dlopen("bin/libwpfwic.so", RTLD_NOW);
    if (!h) { printf("{\"ok\":false,\"error\":\"so 打不开\"}\n"); return 3; }
    fn_open openp = (fn_open)dlsym(h, "OpenColorProfileW");
    fn_multi multi = (fn_multi)dlsym(h, "CreateMultiProfileTransform");
    fn_translate tr = (fn_translate)dlsym(h, "TranslateColors");
    fn_deltransform del = (fn_deltransform)dlsym(h, "DeleteColorTransform");
    fn_close closep = (fn_close)dlsym(h, "CloseColorProfile");
    if (!openp || !multi || !tr || !del || !closep) { printf("{\"ok\":false,\"error\":\"缺导出\"}\n"); return 4; }

    struct { const void *bytes; uint32_t len; } blob = { icc, (uint32_t)n };
    void *p1 = openp(&blob, NULL, 0, 0, 0);
    void *p2 = openp(&blob, NULL, 0, 0, 0);
    if (!p1 || !p2) { printf("{\"ok\":false,\"error\":\"OpenColorProfileW 失败（lcms2 未接上？）\"}\n"); return 5; }
    void *profs[2] = { p1, p2 };
    uint32_t intents[1] = { 0 };
    void *tf = multi(profs, 2, intents, 1, 0, 0);
    if (!tf) { printf("{\"ok\":false,\"error\":\"CreateMultiProfileTransform 失败\"}\n"); return 6; }

    uint8_t in[6] = { 200, 100, 50, 10, 20, 30 };
    uint8_t out[6] = { 0 };
    int ok = tr(tf, in, 2, 1, out, 1);
    int same = memcmp(in, out, 6) == 0;
    printf("{\"ok\":%s,\"translate_ret\":%d,\"identity\":%s,\"out\":[%u,%u,%u,%u,%u,%u]}\n",
           (ok && same) ? "true" : "false", ok, same ? "true" : "false",
           out[0], out[1], out[2], out[3], out[4], out[5]);
    del(tf); closep(p1); closep(p2);
    free(icc);
    return (ok && same) ? 0 : 7;
}
