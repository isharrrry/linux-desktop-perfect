// ============================================================================
// win32_gdiplus.c —— `gdiplus.dll` 的实现（`#35` 立最小面；`#41` F 起**图像族真解码**）
//
// 【为什么需要它】第三方 WPF 应用会**自己** `[DllImport("gdiplus.dll")]`（实例：HandyControl
//   的 `Tools/Interop/InteropMethods.cs`）。典型次序：
//     `Gdip..cctor` → `GdiplusStartup`（类初始化就调！）→ `RtlGetVersion` → 各图像查询/解码。
//   缺这个 DLL 的后果不是"少个功能"，而是 `TypeInitializationException ← DllNotFoundException`
//   把应用在**构造窗口**时就掀掉。
//
// 【`#41` F 的口径 —— 只读族**真做**，写族**如实失败**】
//   · **真做**（接到 WIC/Skia 那条唯一的解码链上）：
//       `GdipLoadImageFromFile` / `GdipCreateBitmapFromFile` / `GdipGetImageWidth` / `GdipGetImageHeight` /
//       `GdipGetImagePixelFormat` / `GdipBitmapGetPixel` / `GdipBitmapLockBits` / `GdipBitmapUnlockBits` /
//       `GdipDisposeImage`（真释放）。
//   · **真话式降级**：帧维度/帧数/编解码器枚举 ⇒ `Ok` + 空结果（Linux 后端确实没有动画/编码器表）。
//   · **如实失败**（不返回假句柄、不假装成功）：保存族、`HBITMAP` 互转、属性项、流式解码。
//
//   ⚠️ **不许再写第二份解码**：真解码一律经 `libwpfwic.so` 的导出助手
//      `WpfWic_DecodeFileToBgra()`（Skia C API 只在那一个文件里被调用）。
//      本文件只做"GDI+ 面 → 那条链"的**转发**与句柄记账。
//      以 `dladdr` 定位我们自己的 `.so` 目录去找同目录的 `libwpfwic.so`（即"四个 .so 与 app 同目录"
//      那条已验证的部署布局），再退回 `dlopen("libwpfwic.so")` 与 `WPF_LINUX_WIC_SHIM`。
// ============================================================================

#define _GNU_SOURCE
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <dlfcn.h>
#include <fcntl.h>
#include <unistd.h>

typedef int32_t GpStatus;
enum {
    Ok = 0, GenericError = 1, InvalidParameter = 2, OutOfMemory = 3, ObjectBusy = 4,
    InsufficientBuffer = 5, NotImplemented = 6, Win32Error = 7, WrongState = 8, Aborted = 9,
    FileNotFound = 10, ValueOverflow = 11, AccessDenied = 12, UnknownImageFormat = 13,
    GdiplusNotInitialized = 18,
};

/* GDI+ 的像素格式常量（只用得到的几个；值与 Windows 头一致） */
#define PixelFormat32bppARGB   0x0026200A
#define PixelFormat32bppPARGB  0x000E200B

/* 观测面（env 门控，默认关）：多帧/属性面的每个"拒绝"路径都留一行，避免"错在哪一步"只能靠猜。 */
#define GD_DIAG(...) do { if (getenv("WPF_GDIP_DIAG")) { fprintf(stderr, "[GDIP] " __VA_ARGS__); } } while (0)
static uintptr_t g_token = 0x6764706c;   /* 'gdpl' —— 只是"非空"的标记 */

/* ⚠️ 前向声明：`gd_resolve_decoder()` 里用 `dladdr((void *)&GdiplusStartup, …)` 求**我们自己 .so 的目录**
   （去找同目录的 `libwpfwic.so`）。那个函数定义在本文件后半 ⇒ 不先声明就是 C99 的隐式声明错误
   （`#41` F 实测：`error: 'GdiplusStartup' undeclared`）。*/
GpStatus GdiplusStartup(void **token, const void *input, void *output);

/* ── 我们自己的位图句柄 ────────────────────────────────────────────────────────
   ⚠️ 句柄是**我们**分配的指针；跨面（例如有人把真 GDI+ 的句柄塞进来）不可能命中 magic
   ⇒ 一律 `InvalidParameter`，绝不 deref 未知指针。 */
#define GD_MAGIC 0x47504C31u             /* 'GPL1' */
typedef struct {
    uint32_t magic;
    int32_t  w, h, stride;
    uint32_t format;
    unsigned char *px;                    /* BGRA32，malloc 出来的 */
    /* ── 多帧（GIF）───────────────────────────────────────────────────────
       `wpath` 是我们自己留的**宽字符路径副本**：切帧时要按 index 重解，而 GDI+ 的
       `SelectActiveFrame` 只给句柄、不给来源 ⇒ 来源必须随句柄走。
       `frame_count` 在打开时探明（WpfWic_FrameCount）；`current_frame` 是当前活动帧。*/
    uint16_t *wpath;
    int32_t  frame_count;
    int32_t  current_frame;
    char    *tmp_path;   /* 流来源：我们自己的临时文件（Dispose 时删）*/
} gd_bmp;

static int gd_valid(void *h) { return h && ((gd_bmp *)h)->magic == GD_MAGIC; }

/* 临时取证（`GD_TRACE=1` 开）：只打印，不改语义。取证结束会移除。 */
static int gd_trace_on(void)
{
    static int cached = -1;
    if (cached < 0) { const char *e = getenv("GD_TRACE"); cached = (e && e[0] == '1'); }
    return cached;
}
#define GD_TRACE(...) do { if (gd_trace_on()) { fprintf(stderr, "[GD_TRACE] " __VA_ARGS__); fputc('\n', stderr); } } while (0)

/* ── 到 WIC/Skia 那条唯一解码链的转发 ─────────────────────────────────────── */
typedef int (*decode_file_fn)(const char *, unsigned char **, int32_t *, int32_t *, int32_t *);
typedef int (*decode_frame_fn)(const char *, int32_t, unsigned char **, int32_t *, int32_t *, int32_t *);
typedef int (*frame_count_fn)(const char *, int32_t *);
typedef int (*frame_delay_fn)(const char *, int32_t, int32_t *);
static decode_file_fn  g_decode_file;
static decode_frame_fn g_decode_frame;    /* 多帧：按 index 解第 N 帧（`#50` 能力的 GDI+ 出口）*/
static frame_count_fn  g_frame_count;     /* 多帧：真实帧数 */
static frame_delay_fn  g_frame_delay;     /* 多帧：第 N 帧显示时长（毫秒）*/
static int g_decode_probe_done;           /* 0=未找过 1=找到 2=没找到 */

static void *try_dlopen(const char *path)
{
    if (!path || !*path) return NULL;
    return dlopen(path, RTLD_NOW | RTLD_LOCAL);
}

static void gd_resolve_decoder(void)
{
    if (g_decode_probe_done) return;
    g_decode_probe_done = 1;              /* 只找一次（失败也不反复 dlopen） */

    void *wic = NULL;
    /* ① 环境变量（显式覆盖；调试/非常规布局用） */
    const char *env = getenv("WPF_LINUX_WIC_SHIM");
    if (env && *env) wic = try_dlopen(env);

    /* ② 与我们自己同目录（**已验证的部署布局**：四个 .so 与 app 同目录） */
    if (!wic) {
        Dl_info info;
        if (dladdr((void *)&GdiplusStartup, &info) && info.dli_fname) {
            char buf[4096];
            const char *slash = strrchr(info.dli_fname, '/');
            if (slash && (size_t)(slash - info.dli_fname) < sizeof buf - 32) {
                size_t n = (size_t)(slash - info.dli_fname);
                memcpy(buf, info.dli_fname, n);
                buf[n] = 0;
                strncat(buf, "/libwpfwic.so", sizeof buf - strlen(buf) - 1);
                wic = try_dlopen(buf);
            }
        }
    }
    /* ③ 默认搜索路径（LD_LIBRARY_PATH / rpath / 系统目录） */
    if (!wic) wic = try_dlopen("libwpfwic.so");
    if (!wic) return;

    void *sym = dlsym(wic, "WpfWic_DecodeFileToBgra");
    if (sym) g_decode_file = (decode_file_fn)sym;
    /* 多帧三件（缺任意一件 ⇒ 该能力**如实缺席**，由调用点报 NotImplemented/降级，不假装）*/
    sym = dlsym(wic, "WpfWic_DecodeFrameToBgra");
    if (sym) g_decode_frame = (decode_frame_fn)sym;
    sym = dlsym(wic, "WpfWic_FrameCount");
    if (sym) g_frame_count = (frame_count_fn)sym;
    sym = dlsym(wic, "WpfWic_FrameDelayMs");
    if (sym) g_frame_delay = (frame_delay_fn)sym;
    /* ⚠️ 故意**不 dlclose**：解码链的生命期跟着进程（关掉会让后续调用悬空）。 */
}

/* UTF-16(LE) → UTF-8（GDI+ 的文件名是 WCHAR*）。返回新分配的缓冲或 NULL。 */
static char *utf16_to_utf8(const uint16_t *w)
{
    if (!w) return NULL;
    size_t n = 0;
    while (w[n]) n++;
    char *out = (char *)malloc(n * 4 + 1);
    if (!out) return NULL;
    size_t j = 0;
    for (size_t i = 0; i < n; i++) {
        uint32_t cp = w[i];
        if (cp >= 0xD800 && cp <= 0xDBFF && i + 1 < n && w[i + 1] >= 0xDC00 && w[i + 1] <= 0xDFFF) {
            cp = 0x10000u + ((cp - 0xD800u) << 10) + (w[i + 1] - 0xDC00u);
            i++;
        }
        if (cp < 0x80) out[j++] = (char)cp;
        else if (cp < 0x800) { out[j++] = (char)(0xC0 | (cp >> 6)); out[j++] = (char)(0x80 | (cp & 0x3F)); }
        else if (cp < 0x10000) {
            out[j++] = (char)(0xE0 | (cp >> 12));
            out[j++] = (char)(0x80 | ((cp >> 6) & 0x3F));
            out[j++] = (char)(0x80 | (cp & 0x3F));
        } else {
            out[j++] = (char)(0xF0 | (cp >> 18));
            out[j++] = (char)(0x80 | ((cp >> 12) & 0x3F));
            out[j++] = (char)(0x80 | ((cp >> 6) & 0x3F));
            out[j++] = (char)(0x80 | (cp & 0x3F));
        }
    }
    out[j] = 0;
    return out;
}

/* 把"解码助手"的返回码翻成 GDI+ 的 GpStatus（**逐码对应**，不合并成"反正失败了"） */
static GpStatus map_decode_rc(int rc)
{
    switch (rc) {
        case 0:  return Ok;
        case 1:  return InvalidParameter;
        case 2:  return OutOfMemory;
        case 3:  return FileNotFound;
        case 4:  return UnknownImageFormat;
        default: return GenericError;
    }
}

static GpStatus gd_from_file(const uint16_t *wpath, void **bitmap)
{
    if (bitmap) *bitmap = NULL;
    if (!wpath || !bitmap) return InvalidParameter;
    gd_resolve_decoder();
    if (!g_decode_file) return NotImplemented;   /* 解码链不在 ⇒ **如实**说做不到 */

    char *path = utf16_to_utf8(wpath);
    if (!path) return OutOfMemory;

    unsigned char *px = NULL; int32_t w = 0, h = 0, stride = 0;
    int rc = g_decode_file(path, &px, &w, &h, &stride);
    if (rc != 0) { free(path); return map_decode_rc(rc); }

    /* 探明帧数（多帧图 > 1）。助手不在 ⇒ 当静态图（**具名缺席**，不臆造帧数）。 */
    int32_t frames = 1;
    if (g_frame_count) {
        int32_t n = 0;
        if (g_frame_count(path, &n) == 0 && n > 0) frames = n;
    }

    gd_bmp *b = (gd_bmp *)calloc(1, sizeof *b);
    if (!b) { free(px); free(path); return OutOfMemory; }

    /* 留一份宽字符路径副本：`SelectActiveFrame` 只给句柄、不给来源，来源必须随句柄走。 */
    size_t wn = 0; while (wpath[wn]) wn++;
    uint16_t *wcopy = (uint16_t *)malloc((wn + 1) * sizeof(uint16_t));
    if (!wcopy) { free(px); free(path); free(b); return OutOfMemory; }
    memcpy(wcopy, wpath, (wn + 1) * sizeof(uint16_t));

    b->magic = GD_MAGIC; b->w = w; b->h = h; b->stride = stride;
    b->format = PixelFormat32bppPARGB;    /* Skia 出的是 BGRA（预乘）—— 与 WIC 侧的声明一致 */
    b->px = px;
    b->wpath = wcopy;
    b->frame_count = frames;
    b->current_frame = 0;
    *bitmap = b;
    free(path);
    return Ok;
}

// ── 生命周期 ───────────────────────────────────────────────────────────────
GpStatus GdiplusStartup(void **token, const void *input, void *output)
{
    (void)input;
    if (token) *token = (void *)&g_token;
    if (output) memset(output, 0, 16);       /* StartupOutput（两个函数指针） */
    return Ok;
}
GpStatus GdiplusShutdown(void *token) { (void)token; return Ok; }

// ── 只读族：**真做**（经唯一解码链）────────────────────────────────────────────
GpStatus GdipLoadImageFromFile(const uint16_t *filename, void **image) { return gd_from_file(filename, image); }
GpStatus GdipCreateBitmapFromFile(const uint16_t *filename, void **bitmap) { return gd_from_file(filename, bitmap); }

GpStatus GdipGetImageWidth(void *image, uint32_t *width)
{
    if (!width) return InvalidParameter;
    if (!gd_valid(image)) { *width = 0; return InvalidParameter; }
    *width = (uint32_t)((gd_bmp *)image)->w;
    return Ok;
}
GpStatus GdipGetImageHeight(void *image, uint32_t *height)
{
    if (!height) return InvalidParameter;
    if (!gd_valid(image)) { *height = 0; return InvalidParameter; }
    *height = (uint32_t)((gd_bmp *)image)->h;
    return Ok;
}
GpStatus GdipGetImagePixelFormat(void *image, int32_t *format)
{
    if (!format) return InvalidParameter;
    if (!gd_valid(image)) { *format = 0; return InvalidParameter; }
    *format = (int32_t)((gd_bmp *)image)->format;
    return Ok;
}
GpStatus GdipBitmapGetPixel(void *bitmap, int32_t x, int32_t y, uint32_t *color)
{
    if (!color) return InvalidParameter;
    if (!gd_valid(bitmap)) { *color = 0; return InvalidParameter; }
    gd_bmp *b = (gd_bmp *)bitmap;
    if (x < 0 || y < 0 || x >= b->w || y >= b->h) { *color = 0; return InvalidParameter; }
    const unsigned char *p = b->px + (size_t)y * (size_t)b->stride + (size_t)x * 4;
    /* 内存里是 BGRA；GDI+ 的 ARGB 是 0xAARRGGBB */
    *color = ((uint32_t)p[3] << 24) | ((uint32_t)p[2] << 16) | ((uint32_t)p[1] << 8) | (uint32_t)p[0];
    return Ok;
}

/* BitmapData 的前 6 个字段（GDI+ 头里的顺序）；我们把 Scan0/Stride/尺寸/格式填真值 */
typedef struct { int32_t width, height, stride, pixelFormat; unsigned char *scan0; void *reserved; } gd_bitmapdata;

GpStatus GdipBitmapLockBits(void *bitmap, void *rect, uint32_t flags, int32_t format, gd_bitmapdata *data)
{
    (void)rect; (void)flags;
    if (!data) return InvalidParameter;
    if (!gd_valid(bitmap)) { memset(data, 0, sizeof *data); return InvalidParameter; }
    gd_bmp *b = (gd_bmp *)bitmap;
    if (format && format != PixelFormat32bppPARGB && format != PixelFormat32bppARGB) {
        memset(data, 0, sizeof *data);
        GD_DIAG("LockBits refuse: format=0x%x\n", format);
        return InvalidParameter;              /* 我们只有 32bpp BGRA ⇒ 别的格式**如实拒绝** */
    }
    data->width = b->w; data->height = b->h; data->stride = b->stride;
    data->pixelFormat = (int32_t)b->format; data->scan0 = b->px; data->reserved = NULL;
    return Ok;
}
GpStatus GdipBitmapUnlockBits(void *bitmap, gd_bitmapdata *data)
{
    (void)data;
    return gd_valid(bitmap) ? Ok : InvalidParameter;
}

GpStatus GdipDisposeImage(void *image)
{
    if (!gd_valid(image)) return Ok;          /* 与 Windows 一致：不是我们的句柄就什么都不做 */
    gd_bmp *b = (gd_bmp *)image;
    b->magic = 0;                             /* 先失效再释放（重复 Dispose 不会 double free） */
    free(b->px);
    free(b->wpath);
    if (b->tmp_path) unlink(b->tmp_path);
    free(b->tmp_path);
    free(b);
    return Ok;
}

// ── 多帧（GIF）：帧维数 / 帧列表 / 帧数 / 选帧 / 帧延时 ───────────────────────
//
// 口径：多帧能力**转发到 WIC shim 的旁路助手**（`WpfWic_FrameCount/FrameDelayMs/
//   DecodeFrameToBgra`），本文件不写第二份解码。助手缺任一件 ⇒ 该入口**如实**报
//   `NotImplemented`（不是"返回空结果"）—— 因为"多帧"这件事此时确实做不到。
//
// GUID 的内存布局：GDI+ 的 GUID 在内存里是 Windows 结构（前 3 段小端）⇒ 逐字节写死，
//   避免端序踩坑。`{b96b3cb0-…}`=Gif、`{b96b3caa-…}`=MemoryBmp（HandyControl 两个都认）、
//   `{6aedbd6d-…}`=FrameDimensionTime（**HandyControl 的 `GifFrameDimension.Time` 就是它**）。
static const unsigned char GD_FMT_GIF[16]    = {0xb0,0x3c,0x6b,0xb9,0x28,0x07,0xd3,0x11,0x9d,0x7b,0x00,0x00,0xf8,0x1e,0xf3,0x2e};
static const unsigned char GD_FMT_MEMBMP[16] = {0xaa,0x3c,0x6b,0xb9,0x28,0x07,0xd3,0x11,0x9d,0x7b,0x00,0x00,0xf8,0x1e,0xf3,0x2e};
static const unsigned char GD_FMT_FDTIME[16] = {0x6d,0xbd,0xed,0x6a,0xb5,0x3f,0x8a,0x41,0x83,0xa6,0x7f,0x45,0x22,0x9d,0xc8,0x72};
static const unsigned char GD_FMT_PNG[16]    = {0xaf,0x3c,0x6b,0xb9,0x28,0x07,0xd3,0x11,0x9d,0x7b,0x00,0x00,0xf8,0x1e,0xf3,0x2e};
static const unsigned char GD_FMT_JPEG[16]   = {0xae,0x3c,0x6b,0xb9,0x28,0x07,0xd3,0x11,0x9d,0x7b,0x00,0x00,0xf8,0x1e,0xf3,0x2e};
static const unsigned char GD_FMT_BMP[16]    = {0xab,0x3c,0x6b,0xb9,0x28,0x07,0xd3,0x11,0x9d,0x7b,0x00,0x00,0xf8,0x1e,0xf3,0x2e};

#define GD_PROP_FRAME_DELAY 0x5100            /* PropertyTagFrameDelay */
#define GD_PROPITEM_SIZE    24                /* {int id; int len; short type; pad; void* value;}（x64）*/

static int gd_guid_is(const void *guid, const unsigned char *want)
{ return guid && memcmp(guid, want, 16) == 0; }

/* 按扩展名判 ImageFormat；多帧 ⇒ 必是 GIF（本栈只支持 GIF 多帧）。判不出就报 MemoryBmp
   （与 GDI+ 对"内存里的图"的语义一致）——**不**解析文件头，那会变成第二份解码。 */
static const unsigned char *gd_format_of(const gd_bmp *b)
{
    if (b->frame_count > 1) return GD_FMT_GIF;
    if (b->wpath) {
        size_t n = 0; while (b->wpath[n]) n++;
        if (n >= 4) {
            const uint16_t *e = b->wpath + n - 4;
            uint16_t a = (uint16_t)(e[0] | 0x20), c = (uint16_t)(e[1] | 0x20), d = (uint16_t)(e[2] | 0x20);
            if (e[3] == '.' && a == 'g' && c == 'i' && d == 'f') return GD_FMT_GIF;
            if (e[3] == '.' && a == 'p' && c == 'n' && d == 'g') return GD_FMT_PNG;
            if (e[3] == '.' && a == 'b' && c == 'm' && d == 'p') return GD_FMT_BMP;
            if (n >= 5 && e[-1] == '.' && a == 'j' && c == 'p' && d == 'g') return GD_FMT_JPEG;
        }
    }
    return GD_FMT_MEMBMP;
}

GpStatus GdipImageGetFrameDimensionsCount(void *image, int32_t *count)
{
    if (!count) return InvalidParameter;
    GD_TRACE("GetFrameDimensionsCount image=%p valid=%d", image, gd_valid(image));
    if (!gd_valid(image)) { *count = 0; GD_DIAG("FrameDimensionsCount: not-ready -> 0\n"); return Ok; }
    /* 多帧 ⇒ 1 个维度（时间）；静态图保持旧语义 0（"没有帧维度"是真话）。 */
    *count = (((gd_bmp *)image)->frame_count > 1) ? 1 : 0;
    GD_DIAG("FrameDimensionsCount -> %d (frames=%d)\n", *count, ((gd_bmp *)image)->frame_count);
    return Ok;
}

GpStatus GdipImageGetFrameDimensionsList(void *image, void *buffer, int32_t count)
{
    if (!gd_valid(image)) return Ok;
    GD_TRACE("GetFrameDimensionsList image=%p valid=%d frames=%d count=%d buf=%p", image, gd_valid(image), gd_valid(image) ? ((gd_bmp *)image)->frame_count : -1, count, buffer);
    if (((gd_bmp *)image)->frame_count <= 1) return Ok;   /* 静态图：无维度可列（旧语义） */
    if (!buffer || count < 1) { GD_DIAG("FrameDimensionsList refuse buffer=%p count=%d\n", buffer, count); return InvalidParameter; }
    memcpy(buffer, GD_FMT_FDTIME, 16);
    GD_DIAG("FrameDimensionsList -> Time\n");
    return Ok;
}

GpStatus GdipImageGetFrameCount(void *image, void *dimensionId, int32_t *count)
{
    if (!count) return InvalidParameter;
    GD_TRACE("GetFrameCount image=%p valid=%d", image, gd_valid(image));
    if (!gd_valid(image)) { *count = 1; return Ok; }
    gd_bmp *b = (gd_bmp *)image;
    /* 时间维度 ⇒ 真实帧数；其它维度（Page/Resolution）本栈只有一维 ⇒ 1（真话）。 */
    *count = (gd_guid_is(dimensionId, GD_FMT_FDTIME) && b->frame_count > 0) ? b->frame_count : 1;
    GD_DIAG("FrameCount -> %d (isTime=%d)\n", *count, gd_guid_is(dimensionId, GD_FMT_FDTIME));
    return Ok;
}

GpStatus GdipImageSelectActiveFrame(void *image, void *dimensionId, int32_t frameIndex)
{
    if (!gd_valid(image)) return Ok;
    gd_bmp *b = (gd_bmp *)image;
    if (b->frame_count <= 1) { GD_DIAG("SelectActiveFrame refuse: static frames=%d\n", b->frame_count); return InvalidParameter; }
    if (!gd_guid_is(dimensionId, GD_FMT_FDTIME)) { GD_DIAG("SelectActiveFrame refuse: not-Time\n"); return InvalidParameter; }
    if (frameIndex < 0 || frameIndex >= b->frame_count) { GD_DIAG("SelectActiveFrame refuse: index=%d frames=%d\n", frameIndex, b->frame_count); return InvalidParameter; }
    if (frameIndex == b->current_frame) return Ok;             /* 幂等：同帧不重解 */
    if (!g_decode_frame || !b->wpath) return NotImplemented;   /* 助手不在 ⇒ 如实说做不到 */

    char *path = utf16_to_utf8(b->wpath);
    if (!path) return OutOfMemory;
    unsigned char *px = NULL; int32_t w = 0, h = 0, stride = 0;
    int rc = g_decode_frame(path, frameIndex, &px, &w, &h, &stride);
    free(path);
    if (rc != 0) return map_decode_rc(rc);                     /* 失败 ⇒ 旧帧**保持不动**（不留半个帧）*/

    free(b->px);
    b->px = px; b->w = w; b->h = h; b->stride = stride;
    b->current_frame = frameIndex;
    return Ok;
}

GpStatus GdipGetImageRawFormat(void *image, void *format)
{
    if (!format) return InvalidParameter;
    if (!gd_valid(image)) return InvalidParameter;
    memcpy(format, gd_format_of((gd_bmp *)image), 16);
    return Ok;
}

/* 帧延时（`PropertyTagFrameDelay`）：HandyControl 的 `GifImageInfo` 构造时就读它 ⇒ 必须真给。
   单位是**厘秒**（GIF 原生单位；hc 的 50ms tick 累加 5 与之配套），值数组紧随结构之后，
   结构的 `value`（偏移 16）指向它 —— 与 GDI+ 的 `PropertyItem` 内存形制一致。 */
GpStatus GdipGetPropertyItemSize(void *image, int32_t propid, int32_t *size)
{
    if (!size) return InvalidParameter;
    *size = 0;
    if (!gd_valid(image)) return InvalidParameter;
    gd_bmp *b = (gd_bmp *)image;
    if (propid != GD_PROP_FRAME_DELAY) { GD_DIAG("PropertyItemSize refuse: propid=0x%x\n", propid); return InvalidParameter; }
    if (b->frame_count <= 1) { GD_DIAG("PropertyItemSize refuse: static frames=%d\n", b->frame_count); return InvalidParameter; }
    *size = GD_PROPITEM_SIZE + 4 * b->frame_count;
    return Ok;
}

GpStatus GdipGetPropertyItem(void *image, int32_t propid, int32_t size, void *buffer)
{
    if (!buffer) return InvalidParameter;
    if (!gd_valid(image)) return InvalidParameter;
    gd_bmp *b = (gd_bmp *)image;
    if (propid != GD_PROP_FRAME_DELAY) { GD_DIAG("PropertyItem refuse: propid=0x%x\n", propid); return InvalidParameter; }
    if (b->frame_count <= 1) { GD_DIAG("PropertyItem refuse: static\n"); return InvalidParameter; }
    if (size < GD_PROPITEM_SIZE + 4 * b->frame_count) { GD_DIAG("PropertyItem refuse: size=%d need=%d\n", size, GD_PROPITEM_SIZE + 4 * b->frame_count); return InsufficientBuffer; }

    unsigned char *p = (unsigned char *)buffer;
    memset(p, 0, (size_t)size);
    int32_t len = 4 * b->frame_count;
    memcpy(p + 0, &propid, 4);            /* id   */
    memcpy(p + 4, &len, 4);               /* len  */
    short type = 4;                       /* PropertyTagTypeLong */
    memcpy(p + 8, &type, 2);              /* type */
    unsigned char *val = p + GD_PROPITEM_SIZE;
    void *valptr = val;                   /* value 指针 = 同 buffer 内的数组起点 */
    memcpy(p + 16, &valptr, sizeof(void *));

    /* 逐帧延时：**拿不到就填 0**（hc 对 0 的语义是"这一帧不额外等"，见 GifImageInfo 的注释），
       但整个属性项**仍然成功** —— 因为"帧延时缺失"是合法的 GIF 情形，不是失败。 */
    int32_t *out = (int32_t *)val;
    for (int32_t i = 0; i < b->frame_count; i++) {
        int32_t ms = 0;
        if (g_frame_delay && b->wpath) {
            char *path = utf16_to_utf8(b->wpath);
            if (path) {
                if (g_frame_delay(path, i, &ms) != 0) ms = 0;
                free(path);
            }
        }
        out[i] = (ms > 0) ? (ms / 10) : 0;   /* 毫秒 → 厘秒（无损：wic 侧就是 cs*10）*/
    }
    return Ok;
}
GpStatus GdipGetImageEncodersSize(int32_t *numEncoders, int32_t *size)
{ if (numEncoders) *numEncoders = 0; if (size) *size = 0; return Ok; }
GpStatus GdipGetImageDecodersSize(int32_t *numDecoders, int32_t *size)
{ if (numDecoders) *numDecoders = 0; if (size) *size = 0; return Ok; }
GpStatus GdipGetImageEncoders(int32_t numEncoders, int32_t size, void *encoders)
{ (void)numEncoders; (void)size; (void)encoders; return Ok; }
GpStatus GdipGetImageDecoders(int32_t numDecoders, int32_t size, void *decoders)
{ (void)numDecoders; (void)size; (void)decoders; return Ok; }
GpStatus GdipImageForceValidation(void *image) { (void)image; return Ok; }

// ── 如实失败族（**不给假句柄、不假装成功**）────────────────────────────────────
/* ── 流式解码（`hc:GifImage` 的 pack 资源路径必经）──────────────────────────────
   托管侧的 `GPStream` 是一个实现了 COM `IStream` 的托管对象；P/Invoke 传进来的就是这个
   **接口指针**（x64 SysV：`*(void ***)stream` 就是 vtable）。
   做法：`IStream::Read` 把字节全部读出来 → 落**临时文件** → 复用上面那条文件解码链
   （`gd_from_file`）。这样**不新增 WIC 出口**、也不写第二份解码（本文件头注的纪律）。

   ⚠️ 只按槽位调用，不 deref 任何未知结构（除 vtable 本身）；`stream == NULL` 直接拒绝。 */
typedef struct {                       /* IStream vtable 前缀（只用到前 6 槽） */
    int32_t  (*QueryInterface)(void *, const void *, void **);
    uint32_t (*AddRef)(void *);
    uint32_t (*Release)(void *);
    int32_t  (*Read)(void *, void *, uint32_t, uint32_t *);
    int32_t  (*Write)(void *, const void *, uint32_t, uint32_t *);
    int32_t  (*Seek)(void *, int64_t, uint32_t, uint64_t *);
} gd_istream_vtbl;

#define GD_STREAM_SEEK_SET 0u
#define GD_READ_BLOCK      65536u

static char *utf8_to_utf16_alloc(const char *u8)   /* 反函数：给 gd_from_file 用 */
{
    if (!u8) return NULL;
    size_t n = strlen(u8);
    uint16_t *out = (uint16_t *)malloc((n + 1) * sizeof(uint16_t));
    if (!out) return NULL;
    /* 简化：本机路径均为 BMP 平面的 ASCII/UTF-8；非 ASCII 时按码位截断（临时文件路径是我们自己生成的
       `/tmp/wpfgdi-XXXXXX`，全 ASCII ⇒ 这条实现足够且可证伪：非 ASCII 输入会被显式拒绝）。 */
    for (size_t i = 0; i < n; i++) {
        if ((unsigned char)u8[i] >= 0x80) { free(out); return NULL; }
        out[i] = (uint16_t)(unsigned char)u8[i];
    }
    out[n] = 0;
    return (char *)out;
}

GpStatus GdipCreateBitmapFromStream(void *stream, void **bitmap)
{
    if (bitmap) *bitmap = 0;
    if (!stream || !bitmap) return InvalidParameter;

    gd_istream_vtbl *vt = *(gd_istream_vtbl **)stream;
    if (!vt || !vt->Read) return InvalidParameter;
    if (vt->Seek) (void)vt->Seek(stream, 0, GD_STREAM_SEEK_SET, NULL);   /* 回到开头（流可能已被读过） */

    unsigned char *buf = NULL;
    size_t cap = 0, len = 0;
    for (;;) {
        if (len + GD_READ_BLOCK > cap) {
            size_t ncap = cap ? cap * 2 : GD_READ_BLOCK;
            unsigned char *nb = (unsigned char *)realloc(buf, ncap);
            if (!nb) { free(buf); return OutOfMemory; }
            buf = nb; cap = ncap;
        }
        uint32_t got = 0;
        int32_t hr = vt->Read(stream, buf + len, (uint32_t)(cap - len), &got);
        if (hr != 0 || got == 0) break;                                  /* 失败或 EOF ⇒ 结束 */
        len += got;
    }
    if (!buf || len == 0) { free(buf); return GenericError; }            /* 流里没有数据 ⇒ 如实失败 */

    char tmpl[] = "/tmp/wpfgdi-XXXXXX";
    int fd = mkstemp(tmpl);
    if (fd < 0) { free(buf); return GenericError; }
    size_t off = 0;
    while (off < len) {
        ssize_t wr = write(fd, buf + off, len - off);
        if (wr <= 0) { close(fd); unlink(tmpl); free(buf); return GenericError; }
        off += (size_t)wr;
    }
    close(fd);
    free(buf);

    char *wpath = utf8_to_utf16_alloc(tmpl);
    if (!wpath) { unlink(tmpl); return GenericError; }
    GpStatus st = gd_from_file((const uint16_t *)wpath, bitmap);
    free(wpath);
    /* 临时文件**留着**：切帧（SelectActiveFrame）要按路径重解，删了就只能停在首帧。
       由句柄 Dispose 负责删（gd_bmp.tmp_path）。 */
    if (st == Ok && bitmap && *bitmap) ((gd_bmp *)*bitmap)->tmp_path = strdup(tmpl);
    else unlink(tmpl);
    GD_TRACE("CreateBitmapFromStream bytes=%zu -> status=%d", len, (int)st);
    return st;
}   /* 流式解码未接（登记在册） */
GpStatus GdipCreateBitmapFromHBITMAP(void *hbitmap, void *hpalette, void **bitmap)
{ (void)hbitmap; (void)hpalette; if (bitmap) *bitmap = 0; return NotImplemented; }
GpStatus GdipCreateHBITMAPFromBitmap(void *bitmap, void **hbitmap, int32_t background)
{ (void)bitmap; (void)background; if (hbitmap) *hbitmap = 0; return NotImplemented; }
GpStatus GdipCreateBitmapFromScan0(int32_t width, int32_t height, int32_t stride, int32_t format, unsigned char *scan0, void **bitmap)
{
    if (bitmap) *bitmap = NULL;
    if (width <= 0 || height <= 0 || !scan0 || !bitmap) return InvalidParameter;
    if (format && format != PixelFormat32bppPARGB && format != PixelFormat32bppARGB) return InvalidParameter;
    if (stride == 0) stride = width * 4;
    if (stride < width * 4) return InvalidParameter;      /* 行距小于一行 ⇒ **如实拒绝**（不许读越界） */
    size_t need = (size_t)stride * (size_t)height;
    unsigned char *px = (unsigned char *)malloc(need);
    if (!px) return OutOfMemory;
    memcpy(px, scan0, need);
    gd_bmp *b = (gd_bmp *)calloc(1, sizeof *b);
    if (!b) { free(px); return OutOfMemory; }
    b->magic = GD_MAGIC; b->w = width; b->h = height; b->stride = stride;
    b->format = format ? (uint32_t)format : PixelFormat32bppPARGB; b->px = px;
    *bitmap = b;
    return Ok;
}
GpStatus GdipSaveImageToFile(void *image, const uint16_t *filename, void *clsid, void *encoderParams)
{ (void)image; (void)filename; (void)clsid; (void)encoderParams; return NotImplemented; }
GpStatus GdipSaveImageToStream(void *image, void *stream, void *clsid, void *encoderParams)
{ (void)image; (void)stream; (void)clsid; (void)encoderParams; return NotImplemented; }
