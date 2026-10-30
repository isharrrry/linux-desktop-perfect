/* PNG 解码探针：直接验证 libwpfwic 里的 ldp_png_decode（不经过 WIC 的接口层）。
 * 用法：ldp-png-probe <file.png>  → 打印尺寸/是否含 alpha/首像素。 */
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include "png_decode.h"

int main(int argc, char **argv)
{
    if (argc < 2) { fprintf(stderr, "用法：ldp-png-probe <file.png>\n"); return 1; }
    FILE *f = fopen(argv[1], "rb");
    if (!f) { fprintf(stderr, "打不开 %s\n", argv[1]); return 2; }
    fseek(f, 0, SEEK_END);
    long n = ftell(f);
    fseek(f, 0, SEEK_SET);
    uint8_t *buf = (uint8_t *)malloc((size_t)n);
    if (!buf || fread(buf, 1, (size_t)n, f) != (size_t)n) { fprintf(stderr, "读取失败\n"); fclose(f); return 3; }
    fclose(f);
    ldp_png_image img;
    char err[256] = { 0 };
    int32_t hr = ldp_png_decode(buf, (size_t)n, &img, err, sizeof(err));
    free(buf);
    if (hr != 0) { printf("{\"ok\":false,\"hr\":%d,\"error\":\"%s\"}\n", hr, err); return 4; }
    const uint8_t *p0 = img.bgra;
    printf("{\"ok\":true,\"width\":%u,\"height\":%u,\"has_alpha\":%s,\"px0\":[%u,%u,%u,%u]}\n",
           img.width, img.height, img.has_alpha ? "true" : "false", p0[0], p0[1], p0[2], p0[3]);
    ldp_png_free(&img);
    return 0;
}
