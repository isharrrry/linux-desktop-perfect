/* media 探针（ACC-022 第一步）：验证 media-bridge 能从视频文件解出第一帧。 */
#include <stdio.h>
#include <string.h>
#include "media_bridge.h"

int main(int argc, char **argv)
{
    if (argc < 2) { printf("{\"ok\":false,\"error\":\"用法：media-probe <video>\"}\n"); return 1; }
    ldp_media_info info;
    memset(&info, 0, sizeof(info));
    int32_t hr = ldp_media_probe(argv[1], &info);
    if (hr != 0) { printf("{\"ok\":false,\"hr\":%d,\"error\":\"%s\"}\n", hr, info.error); return 2; }
    printf("{\"ok\":true,\"bytes\":%u,\"px0\":[%u,%u,%u,%u]}\n",
           info.bytes, info.data[0], info.data[1], info.data[2], info.data[3]);
    ldp_media_free(&info);
    return 0;
}
