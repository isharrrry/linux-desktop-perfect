// 媒体桥（ACC-022 的第一步）：用系统 **GStreamer** 把一个视频文件的**第一帧**解成 BGRA。
//
// 【为什么走命令行而不是链 libgstreamer】
//   本机只装了 GStreamer 的**运行库**与插件，**没有 `gst/gst.h`**（也没有 pkg-config）。
//   GStreamer 的 C API 是"宏 + 结构体 + 大量内联函数"，没有头文件就不可能手工声明。
//   ⇒ 用 `gst-launch-1.0` 作为**外部解码器**：它在 PATH 里、插件齐全、行为可复现。
//   代价：一次解码要 fork 一个进程、写一个临时文件（与 TIFF 借 libtiff 同一个取舍）。
//
// 【诚实边界】只做"打开 + 第一帧"；播放/暂停/进度/音视频同步都还没做。

#define _GNU_SOURCE
#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include "media_bridge.h"

#define E_NOTIMPL_    0x80004001
#define E_INVALIDARG_ 0x80070057

static int run_gst_first_frame(const char *path, const char *out_raw)
{
    char cmd[4096];
    snprintf(cmd, sizeof(cmd),
             "gst-launch-1.0 -q filesrc location='%s' ! decodebin ! videoconvert ! "
             "video/x-raw,format=BGRA ! filesink location='%s' 2>/dev/null",
             path, out_raw);
    return system(cmd) == 0 ? 0 : -1;
}

// 用 gst-discoverer-1.0 取时长与视频尺寸（它输出人类可读的树，这里按关键字解析）。
//   【为什么用 discoverer 而不是自己在 pipeline 里取 caps】
//   pipeline 那一路只能拿到裸字节；尺寸/时长需要 caps 或已经建好的时钟，
//   而 discoverer 一条命令就把这两样都给全了。
static void probe_meta(const char *path, ldp_media_info *out)
{
    char cmd[4096];
    snprintf(cmd, sizeof(cmd), "gst-discoverer-1.0 '%s' 2>/dev/null", path);
    FILE *p = popen(cmd, "r");
    if (!p) return;
    char line[512];
    while (fgets(line, sizeof(line), p)) {
        int h = 0, m = 0, s2 = 0;
        if (strstr(line, "Duration:") && sscanf(strstr(line, "Duration:"), "Duration: %d:%d:%d", &h, &m, &s2) == 3)
            out->duration_ms = (uint32_t)(((h * 60 + m) * 60 + s2) * 1000);
        if (strstr(line, "Width:")) sscanf(strstr(line, "Width:"), "Width: %u", &out->width);
        if (strstr(line, "Height:")) sscanf(strstr(line, "Height:"), "Height: %u", &out->height);
    }
    pclose(p);
}

int32_t ldp_media_probe(const char *path, ldp_media_info *out)
{
    if (!path || !out) return E_INVALIDARG_;
    memset(out, 0, sizeof(*out));
    if (access(path, R_OK) != 0) return E_INVALIDARG_;

    char raw[] = "/tmp/ldp-media-XXXXXX";
    int fd = mkstemp(raw);
    if (fd < 0) return E_NOTIMPL_;
    close(fd);

    if (run_gst_first_frame(path, raw) != 0) {
        unlink(raw);
        snprintf(out->error, sizeof(out->error),
                 "gst-launch-1.0 解码失败（插件缺失或格式不受支持）");
        return E_NOTIMPL_;
    }
    FILE *f = fopen(raw, "rb");
    if (!f) { unlink(raw); return E_NOTIMPL_; }
    static uint8_t buf[1920 * 1080 * 4];
    size_t n = fread(buf, 1, sizeof(buf), f);
    fclose(f);
    unlink(raw);
    if (n == 0) { snprintf(out->error, sizeof(out->error), "解码结果为空（0 字节）"); return E_NOTIMPL_; }
    probe_meta(path, out);   // 尺寸/时长（拿不到就是 0，不假装知道）
    out->bytes = (uint32_t)n;
    out->data = (uint8_t *)malloc(n);
    if (!out->data) return E_NOTIMPL_;
    memcpy(out->data, buf, n);
    return 0;
}

void ldp_media_free(ldp_media_info *info)
{
    if (info && info->data) { free(info->data); info->data = NULL; info->bytes = 0; }
}
