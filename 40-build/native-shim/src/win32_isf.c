// ── 【清算 / K-204】ISF 压缩：按替代格式设计落地（zlib 后端）────────────────────
//
// 【设计依据】`40-build/替代格式设计.md` 第一节。
//   ISF（Ink Serialized Format）的压缩段原本用微软私有算法，**没有公开规范、本仓无样本可比对**。
//   设计文档的结论：**保留 ISF 外层容器、压缩段换 zlib**，因为 `.isf` 只在本地「存→读」，
//   **不需要与 Windows 互认**。
//
// 【算法标记】`pnAlgoByte` 用 `0x02` 表示"zlib 压缩"（`0x01` 预留给未压缩）。
//   读的时候按标记分派；标记未知 ⇒ 返回明确错误码，**不猜**。
//
// 【上游签名】（`40-build/upstream/wpf/src/.../UnsafeNativeMethodsPenimc.cs:375-485`）
//     int  IsfCompressPropertyData(byte[] in, uint cbIn, ref byte algo, ref uint pcbOut, byte[] out);
//     int  IsfCompressPacketData(IntPtr hCompress, int[] in, uint cInCount, ref byte algo, ref uint pcbOut, byte[] out);
//     int  IsfDecompressPacketData(IntPtr hCompress, byte[] comp, ref uint pcbComp, uint cInCount, int[] out, ref byte algo);
//     IntPtr IsfLoadCompressor(byte[] in, ref uint pcbIn);
//     void IsfReleaseCompressor(IntPtr hCompress);
#include "win32_internal.h"
#include <stdlib.h>
#include <string.h>
#include <zlib.h>

#define WPF_ISF_ALGO_ZLIB     0x02
#define WPF_ISF_OK            0
#define WPF_ISF_ERR           1

// ── IsfLoadCompressor：返回一个"压缩器可用了"的句柄 ─────────────────────────
//   真实的 Windows 实现会解析传入的头部字节并分配状态机。我们只需要一个
//   **非 NULL 的句柄**表示"zlib 可用" —— 这是真实现（zlib 确实可用）。
void *IsfLoadCompressor(const uint8_t *pbInput, uint32_t *pcbInput)
{
    (void)pbInput;
    if (pcbInput) *pcbInput = 0;   // 我们没有头部信息要回填
    return (void *)(intptr_t)0x1F01;   // 非 NULL 句柄
}

void IsfReleaseCompressor(void *hCompress) { (void)hCompress; }

// 内部：把一段字节用 zlib 压缩到调用方缓冲
static int32_t wpf_isf_deflate(const uint8_t *in, uint32_t cbIn, uint8_t *out, uint32_t *pcbOut)
{
    if (!in || !out || !pcbOut) return WPF_ISF_ERR;
    uLongf destLen = (uLongf)*pcbOut;
    int rc = compress2((Bytef *)out, &destLen, (const Bytef *)in, (uLong)cbIn, 6 /* 压缩级别 */);
    if (rc != Z_OK) return WPF_ISF_ERR;
    *pcbOut = (uint32_t)destLen;
    return WPF_ISF_OK;
}

// 内部：解压
static int32_t wpf_isf_inflate(const uint8_t *in, uint32_t cbIn, uint8_t *out, uint32_t cbOut)
{
    if (!in || !out) return WPF_ISF_ERR;
    uLongf destLen = (uLongf)cbOut;
    int rc = uncompress((Bytef *)out, &destLen, (const Bytef *)in, (uLong)cbIn);
    return (rc == Z_OK) ? WPF_ISF_OK : WPF_ISF_ERR;
}

// ── IsfCompressPropertyData：压缩属性字节流（无压缩器句柄）──────────────────
int32_t IsfCompressPropertyData(const uint8_t *pbInput, uint32_t cbInput,
                                uint8_t *pnAlgoByte, uint32_t *pcbOutput, uint8_t *pbOutput)
{
    if (!pnAlgoByte || !pcbOutput) return WPF_ISF_ERR;
    int32_t rc = wpf_isf_deflate(pbInput, cbInput, pbOutput, pcbOutput);
    if (rc == WPF_ISF_OK) *pnAlgoByte = WPF_ISF_ALGO_ZLIB;
    return rc;
}

// ── IsfCompressPacketData：压缩墨迹点（int 数组 → 字节流）────────────────────
//   `cInCount` 是 **int 的个数**（每点 4 字节）。
int32_t IsfCompressPacketData(void *hCompress, const int32_t *pbInput, uint32_t cInCount,
                              uint8_t *pnAlgoByte, uint32_t *pcbOutput, uint8_t *pbOutput)
{
    (void)hCompress;
    if (!pbInput || !pnAlgoByte || !pcbOutput) return WPF_ISF_ERR;
    int32_t rc = wpf_isf_deflate((const uint8_t *)pbInput, cInCount * sizeof(int32_t),
                                 pbOutput, pcbOutput);
    if (rc == WPF_ISF_OK) *pnAlgoByte = WPF_ISF_ALGO_ZLIB;
    return rc;
}

// ── IsfDecompressPacketData：解压回墨迹点 ──────────────────────────────────
//   `cInCount` 是期望解出的 **int 个数**（输出缓冲大小 = cInCount * 4）。
int32_t IsfDecompressPacketData(void *hCompress, const uint8_t *pbCompressed,
                                uint32_t *pcbCompressed, uint32_t cInCount,
                                int32_t *pbOutput, uint8_t *pnAlgoData)
{
    (void)hCompress;
    if (!pbCompressed || !pcbCompressed || !pbOutput || !pnAlgoData) return WPF_ISF_ERR;
    // 【替代格式的关键】按标记分派：只有 0x02(zlib) 和 0x01(未压缩) 认；其余如实失败。
    if (*pnAlgoData == WPF_ISF_ALGO_ZLIB) {
        return wpf_isf_inflate(pbCompressed, *pcbCompressed,
                               (uint8_t *)pbOutput, cInCount * sizeof(int32_t));
    }
    if (*pnAlgoData == 0x01) {   // 未压缩：直接拷贝
        uint32_t need = cInCount * sizeof(int32_t);
        if (*pcbCompressed < need) return WPF_ISF_ERR;
        memcpy(pbOutput, pbCompressed, need);
        return WPF_ISF_OK;
    }
    return WPF_ISF_ERR;   // 未知算法 ⇒ 不猜
}

// ── IsfDecompressPropertyData：解压属性字节流 ────────────────────────────────
//   上游：`int IsfDecompressPropertyData(byte[] comp, uint cbComp, ref uint pcbOutput,
//                                       byte[] output, ref byte algo)`
//   与 `IsfDecompressPacketData` 的区别：这个的输出大小由调用方给（`pcbOutput` 进/出），
//   单位是**字节**而不是 int 个数。
int32_t IsfDecompressPropertyData(const uint8_t *pbCompressed, uint32_t cbCompressed,
                                  uint32_t *pcbOutput, uint8_t *pbOutput, uint8_t *pnAlgoByte)
{
    if (!pbCompressed || !pcbOutput || !pbOutput || !pnAlgoByte) return WPF_ISF_ERR;
    if (*pnAlgoByte == WPF_ISF_ALGO_ZLIB) {
        uint32_t cap = *pcbOutput;
        uLongf destLen = (uLongf)cap;
        int rc = uncompress((Bytef *)pbOutput, &destLen, (const Bytef *)pbCompressed, (uLong)cbCompressed);
        if (rc != Z_OK) return WPF_ISF_ERR;
        *pcbOutput = (uint32_t)destLen;
        return WPF_ISF_OK;
    }
    if (*pnAlgoByte == 0x01) {   // 未压缩
        if (cbCompressed > *pcbOutput) return WPF_ISF_ERR;
        memcpy(pbOutput, pbCompressed, cbCompressed);
        *pcbOutput = cbCompressed;
        return WPF_ISF_OK;
    }
    return WPF_ISF_ERR;   // 未知算法 ⇒ 不猜
}
