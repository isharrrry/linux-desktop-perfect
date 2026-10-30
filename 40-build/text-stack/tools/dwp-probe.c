/* dwp-probe —— text-stack 的判据探针（输出单行 JSON，供 50-verify 判据消费）
 *
 * 用法：
 *   dwp-probe --version
 *   dwp-probe --fonts
 *   dwp-probe --match "DejaVu Sans"
 *   dwp-probe --shape "DejaVu Sans" "Hello" [size]
 *   dwp-probe --breaks "Hello world"
 *   dwp-probe --outline "DejaVu Sans" 72 [size]
 *
 * 退出码：0 = 成功；2 = 调用失败（stderr 给错误码与文案，不静默返回空）。
 */

#include "../src/dwlinux.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int fail(int rc)
{
    fprintf(stderr, "ERROR %d %s\n", rc, dwp_error(rc));
    return 2;
}

int main(int argc, char **argv)
{
    if (argc < 2) {
        fprintf(stderr, "用法：dwp-probe --version|--fonts|--match|--shape|--breaks|--outline\n");
        return 2;
    }
    int rc = dwp_init();
    if (rc < 0) return fail(rc);
    const char *mode = argv[1];

    if (strcmp(mode, "--version") == 0) {
        printf("{\"version\":\"%s\"}\n", dwp_version());
        return 0;
    }
    if (strcmp(mode, "--fonts") == 0) {
        int n = dwp_font_count();
        if (n < 0) return fail(n);
        printf("{\"count\":%d,\"families\":[", n);
        char buf[256];
        int shown = 0;
        for (int i = 0; i < n && shown < 4096; i++) {
            if (dwp_font_family(i, buf, (int)sizeof(buf)) == 0) {
                printf("%s\"%s\"", shown ? "," : "", buf);
                shown++;
            }
        }
        printf("]}\n");
        return 0;
    }
    if (strcmp(mode, "--match") == 0) {
        if (argc < 3) return fail(DWP_E_INVALIDARG);
        DwpMatch m;
        rc = dwp_match(argv[2], &m);
        if (rc < 0) return fail(rc);
        printf("{\"family\":\"%s\",\"file\":\"%s\",\"index\":%d}\n", m.family, m.file, m.index);
        return 0;
    }
    if (strcmp(mode, "--shape") == 0) {
        if (argc < 4) return fail(DWP_E_INVALIDARG);
        int size = argc >= 5 ? atoi(argv[4]) : 16;
        int n = dwp_shape(argv[2], argv[3], size, NULL, NULL, NULL, 0);
        if (n < 0) return fail(n);
        uint32_t *glyphs = (uint32_t *)calloc((size_t)(n > 0 ? n : 1), sizeof(uint32_t));
        int32_t *advances = (int32_t *)calloc((size_t)(n > 0 ? n : 1), sizeof(int32_t));
        uint32_t *clusters = (uint32_t *)calloc((size_t)(n > 0 ? n : 1), sizeof(uint32_t));
        if (!glyphs || !advances || !clusters) return fail(DWP_E_SHAPE_FAILED);
        n = dwp_shape(argv[2], argv[3], size, glyphs, advances, clusters, n);
        if (n < 0) return fail(n);
        printf("{\"size\":%d,\"glyphs\":[", size);
        for (int i = 0; i < n; i++) printf("%s%u", i ? "," : "", glyphs[i]);
        printf("],\"advances\":[");
        for (int i = 0; i < n; i++) printf("%s%d", i ? "," : "", advances[i]);
        printf("],\"clusters\":[");
        for (int i = 0; i < n; i++) printf("%s%u", i ? "," : "", clusters[i]);
        printf("]}\n");
        free(glyphs);
        free(advances);
        free(clusters);
        return 0;
    }
    if (strcmp(mode, "--fallback") == 0) {
        const char *family = argc >= 3 ? argv[2] : "";
        const char *text = argc >= 4 ? argv[3] : "";
        char buf[8192];
        int n = dwp_fallback(family, text, buf, (int)sizeof(buf));
        if (n < 0) return fail(n);
        printf("{\"count\":%d,\"families\":[", n);
        const char *p2 = buf;
        for (int i = 0; i < n; i++) {
            const char *nl = strchr(p2, '\n');
            int len = nl ? (int)(nl - p2) : (int)strlen(p2);
            printf("%s\"%.*s\"", i ? "," : "", len, p2);
            if (!nl) break;
            p2 = nl + 1;
        }
        printf("]}\n");
        return 0;
    }
    if (strcmp(mode, "--bidi") == 0) {
        if (argc < 3) return fail(DWP_E_INVALIDARG);
        int n = dwp_bidi_levels(argv[2], NULL, 0);
        if (n < 0) return fail(n);
        int *levels = (int *)calloc((size_t)(n > 0 ? n : 1), sizeof(int));
        if (!levels) return fail(DWP_E_LINE_FAILED);
        n = dwp_bidi_levels(argv[2], levels, n);
        if (n < 0) return fail(n);
        printf("{\"count\":%d,\"levels\":[", n);
        for (int i = 0; i < n; i++) printf("%s%d", i ? "," : "", levels[i]);
        printf("]}\n");
        free(levels);
        return 0;
    }
    if (strcmp(mode, "--breaks") == 0) {
        if (argc < 3) return fail(DWP_E_INVALIDARG);
        int n = dwp_line_breaks(argv[2], NULL, 0);
        if (n < 0) return fail(n);
        int *offs = (int *)calloc((size_t)(n > 0 ? n : 1), sizeof(int));
        if (!offs) return fail(DWP_E_LINE_FAILED);
        n = dwp_line_breaks(argv[2], offs, n);
        if (n < 0) return fail(n);
        printf("{\"count\":%d,\"byte_offsets\":[", n);
        for (int i = 0; i < n; i++) printf("%s%d", i ? "," : "", offs[i]);
        printf("]}\n");
        free(offs);
        return 0;
    }
    if (strcmp(mode, "--outline") == 0) {
        if (argc < 4) return fail(DWP_E_INVALIDARG);
        unsigned cp = (unsigned)strtoul(argv[3], NULL, 10);
        int size = argc >= 5 ? atoi(argv[4]) : 32;
        int n = dwp_outline(argv[2], cp, size, NULL, 0);
        if (n < 0) return fail(n);
        DwpPoint *pts = (DwpPoint *)calloc((size_t)(n > 0 ? n : 1), sizeof(DwpPoint));
        if (!pts) return fail(DWP_E_OUTLINE_EMPTY);
        n = dwp_outline(argv[2], cp, size, pts, n);
        if (n < 0) return fail(n);
        printf("{\"codepoint\":%u,\"count\":%d,\"points\":[", cp, n);
        for (int i = 0; i < n; i++) {
            printf("%s{\"x\":%.3f,\"y\":%.3f,\"kind\":%d}", i ? "," : "", pts[i].x, pts[i].y, pts[i].kind);
        }
        printf("]}\n");
        free(pts);
        return 0;
    }
    fprintf(stderr, "未知模式：%s\n", mode);
    return 2;
}
