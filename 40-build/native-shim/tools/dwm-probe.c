/* DWM 探测探针：直接调 libwpfwin32.so 的 DwmIsCompositionEnabled，输出单行 JSON。
 * 判据用它验「有/无合成器」两极下的实现读数（不经样本，避免样本静态文本的假读数）。 */
#include <stdint.h>
#include <stdio.h>

typedef int32_t (*fn_dwm)(int32_t *enabled);
extern int32_t DwmIsCompositionEnabled(int32_t *enabled);

int main(void)
{
    int32_t enabled = -1;
    int32_t hr = DwmIsCompositionEnabled(&enabled);
    printf("{\"hr\":%d,\"enabled\":%s}\n", hr, enabled ? "true" : "false");
    return 0;
}
