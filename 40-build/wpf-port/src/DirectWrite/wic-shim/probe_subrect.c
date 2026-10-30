/* probe_subrect.c —— D-d 修法 A 的牙齿：任意子矩形 CopyPixels
 * 【凭什么可信（不必信任作者，可复算）】
 *   模式刻意用**每像素互不相同**的字节 ⇒ 任何行偏移/列偏移/宽度算术错误都会改变字节内容。
 *   突变自查：把源偏移里的 prc->x 与 prc->y 对调 ⇒ 用例 1/2 必须红（本文件末尾给出复算命令）。*/
#include <dlfcn.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
typedef struct { uint32_t d1; uint16_t d2, d3; uint8_t d4[8]; } guid_t;
typedef struct { int32_t x, y, w, h; } wic_rect;
static guid_t PBGRA = {0x6fddc324,0x4e03,0x4bfe,{0xb1,0x85,0x3d,0x77,0x76,0x8d,0xc9,0x10}};
#define W 8
#define H 4
#define STRIDE (W*4)
static int fails;
static int chk(const char *n, int ok, const char *x){ printf("  %-52s %s%s%s\n", n, ok?"PASS":"**FAIL**", x?"  ":"", x?x:""); if(!ok) fails++; return ok; }
int main(int argc, char **argv)
{
    void *h = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (!h) { printf("SKIP dlopen\n"); return 2; }
    int32_t (*reg)(intptr_t,const guid_t*,uint32_t,uint32_t,int32_t,const void*,uint32_t)=dlsym(h,"WicShim_RegisterForeignSource");
    int32_t (*unreg)(intptr_t)=dlsym(h,"WicShim_UnregisterForeignSource");
    int32_t (*copy)(void*,wic_rect*,uint32_t,uint32_t,void*)=dlsym(h,"IWICBitmapSource_CopyPixels_Proxy");
    if(!reg||!unreg||!copy){printf("SKIP 导出缺失\n");return 2;}
    unsigned char px[W*H*4];
    for (int i=0;i<W*H;i++){ px[i*4+0]=(unsigned char)(0x10+i); px[i*4+1]=(unsigned char)(0x40+i); px[i*4+2]=(unsigned char)(0x80+i); px[i*4+3]=0xFF; }
    const intptr_t MIL=(intptr_t)0x20000031;
    reg(MIL,&PBGRA,W,H,0,px,STRIDE);
    const int32_t E_INVALIDARG=(int32_t)0x80070057;

    /* 1. D-d 那一格：1x1 探针必须 S_OK 且等于首像素 */
    unsigned char b1[4]={0}; wic_rect r1={0,0,1,1};
    int32_t hr=copy((void*)MIL,&r1,4,4,b1);
    int ok1 = hr==0 && memcmp(b1,px,4)==0;
    chk("1. CopyPixels(0,0,1,1) ⇒ S_OK 且首像素逐字节相同", ok1, NULL);

    /* 2. 一般子矩形 (2,1,3x2)，stride 用自己的 20（大于 3*4，验证行距语义）*/
    unsigned char b2[3*2*4+8]; memset(b2,0xEE,sizeof b2); wic_rect r2={2,1,3,2};
    hr=copy((void*)MIL,&r2,20,sizeof b2,b2);
    int ok2 = hr==0;
    for (int j=0;j<2 && ok2;j++) for (int x=0;x<3;x++){
        const unsigned char *want = px + (size_t)(1+j)*STRIDE + (size_t)(2+x)*4;
        if (memcmp(b2 + (size_t)j*20 + (size_t)x*4, want, 4)!=0) ok2=0; }
    chk("2. CopyPixels(2,1,3x2) ⇒ S_OK 且区域逐字节相同（含自定义 stride）", ok2, NULL);

    /* 3. 非法矩形必须被拒（越界 / 负 / 零宽）*/
    unsigned char b3[W*H*4]; wic_rect r3={6,3,5,5};
    hr=copy((void*)MIL,&r3,STRIDE,sizeof b3,b3);
    chk("3a. 越界 (6,3,5x5) ⇒ E_INVALIDARG", hr==E_INVALIDARG, NULL);
    wic_rect r4={-1,0,2,2}; hr=copy((void*)MIL,&r4,STRIDE,sizeof b3,b3);
    chk("3b. 负偏移 (-1,0,2x2) ⇒ E_INVALIDARG", hr==E_INVALIDARG, NULL);
    wic_rect r5={0,0,0,0}; unsigned char b5[W*H*4];
    hr=copy((void*)MIL,&r5,STRIDE,sizeof b5,b5);
    chk("3c. (0,0,0,0) ⇒ 整图语义（S_OK 且全图相同）", hr==0 && memcmp(b5,px,sizeof px)==0, NULL);

    /* 4. 缓冲区不足仍须拒绝 */
    wic_rect r6={0,0,4,4}; unsigned char b6[16];
    hr=copy((void*)MIL,&r6,16,15,b6);
    chk("4. 缓冲区不足 ⇒ E_INVALIDARG（语义不变）", hr==E_INVALIDARG, NULL);

    unreg(MIL);
    printf("%s（fail=%d）\n", fails==0?"SUBRECT_PROBE=PASS":"SUBRECT_PROBE=FAIL", fails);
    return fails==0?0:1;
}
