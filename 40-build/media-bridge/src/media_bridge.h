#ifndef LDP_MEDIA_BRIDGE_H
#define LDP_MEDIA_BRIDGE_H
#include <stdint.h>

typedef struct {
    uint8_t *data;       // 原始 BGRA 帧序列（按 width/height 解释）
    uint32_t bytes;
    uint32_t width;      // 视频自然尺寸（来自 gst-discoverer）
    uint32_t height;
    uint32_t duration_ms;// 时长（毫秒）
    char     error[256];
} ldp_media_info;

// 打开文件并用 GStreamer 解出（至少）第一帧的 BGRA 数据。
// 返回 0 成功；E_NOTIMPL 表示"没解出来"（含 gst-launch 缺失/失败），error 里有原因。
int32_t ldp_media_probe(const char *path, ldp_media_info *out);
void ldp_media_free(ldp_media_info *info);

#endif
