/* WIC 探针：dlopen libwpfwic.so，验证工厂入口能过、解码入口明确返回 E_NOTIMPL。
 * 用途：ACC-018/019 的读数要能区分「so 不存在」「工厂能建但解码没实现」「解码可用」三种状态。 */
#define _GNU_SOURCE
#include <dlfcn.h>
#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>

typedef int32_t (*fn_create)(uint32_t, void **);
typedef int32_t (*fn_decoder)(void *, void *, void *, void *, void **);

int main(int argc, char **argv)
{
    const char *path = argc > 1 ? argv[1] : "40-build/wic-dwrite/bin/libwpfwic.so";
    void *h = dlopen(path, RTLD_NOW | RTLD_LOCAL);
    if (!h) { printf("{\"loaded\":false,\"error\":\"%s\"}\n", dlerror()); return 1; }
    fn_create create = (fn_create)dlsym(h, "WICCreateImagingFactory_Proxy");
    fn_decoder dec = (fn_decoder)dlsym(h, "IWICImagingFactory_CreateDecoderFromStream_Proxy");
    if (!create) { printf("{\"loaded\":true,\"factory_entry\":false}\n"); return 2; }
    void *factory = NULL;
    int32_t hr_create = create(0x0230, &factory);
    int32_t hr_decode = -1;
    if (dec) hr_decode = dec(factory, NULL, NULL, NULL, NULL);
    printf("{\"loaded\":true,\"factory_entry\":true,\"hr_create\":%d,\"factory_nonnull\":%s,\"decoder_entry\":%s,\"hr_decode\":%d}\n",
           hr_create, factory ? "true" : "false", dec ? "true" : "false", hr_decode);
    dlclose(h);
    return 0;
}
