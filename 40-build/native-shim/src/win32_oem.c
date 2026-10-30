// ============================================================================
// win32_oem.c —— **映射表外 DLL 的最小面**（`#35` 波）
//
// 【为什么需要它】`libwpfwin32.so` 原先只被映射到 6 个 DLL 名
//   （`gdi32`/`kernel32`/`ole32`/`user32`/`uxtheme`/`wtsapi32`）。
//   而**第三方 WPF 应用**（实例：HandyControl 示例工程）自己 `[DllImport]` 的还有
//   `shell32`（`ExtractIconEx`）、`ntdll`（`RtlGetVersion`）、`gdiplus`（`GdiplusStartup` …）、
//   `msimg32`、`dwmapi`、以及 `user32` 的 `SetWindowCompositionAttribute`。
//   实测症状（不是"少一个功能"，而是"应用直接被掀掉"）：
//     · `EntryPointNotFoundException: Unable to find an entry point named 'ExtractIconEx'`
//     · `DllNotFoundException: gdiplus.dll`（派生 `TypeInitializationException`）
//   ⇒ 本文件把**这些名字下、调用方真正会碰到的入口**补上，并让解析器（`src/Microsoft.DotNet.Wpf.Linux/src/shims/Win32ShimResolver.cs`）
//     把它们也映射到本 shim。
//
// 【口径（明说，不许沉默扩权）】
//   · 能真做的真做：`RtlGetVersion` 如实报一个 Windows 版本（linux 上**没有真值**，
//     这里报 `10.0.19045` 是**降级声明**，不是"这就是你的系统版本"；调用方只用它做分支）；
//   · 做不到的**如实失败**（返回 NULL/0 + `SetLastError`），**绝不**返回"看起来有效的假句柄"；
//   · **GDI+ 只做到"应用能起来、图像族不装成功"**：`GdiplusStartup` 返回 Ok（只是个标记），
//     而**查询类**返回"成功 + 空结果"（例：这张图没有动画帧 ⇒ 上层优雅降级为静图），
//     **创建/解码类**返回 `InvalidParameter` ⇒ 上层走自己的失败分支。图像真要落地得接 Skia/WIC。
// ============================================================================

#include <stdint.h>
#include <string.h>
#include <stddef.h>
#include <stdio.h>    /* ★TASK-0747：`SHAppBarMessage` 的 `[APPBAR_DIAG]` 自报行（`fprintf`）*/
#include <stdlib.h>   /* ★TASK-0747：上面那条 diag 的 env 门（`getenv`）*/

typedef int BOOL;
typedef uint32_t UINT;
typedef uint32_t DWORD;
typedef void *HANDLE;
typedef void *HMODULE;
typedef intptr_t INT_PTR;
typedef uintptr_t UINT_PTR;

extern void wpf_set_last_error(uint32_t code);
extern void *wpf_x11_display(void);   /* win32_x11.c 提供（若不存在则由链接期报错拦住 —— 宁可红） */
extern void  wpf_x11_workarea(int *x, int *y, int *w, int *h);   /* win32_x11.c 提供：工作区事实（★TASK-0747）*/

#define E_NOTIMPL_CODE 120            /* ERROR_CALL_NOT_IMPLEMENTED */
#define E_INVALIDARG_CODE 87          /* ERROR_INVALID_PARAMETER */

// ── ntdll：RtlGetVersion ────────────────────────────────────────────────────
// 结构 = RTL_OSVERSIONINFOEXW（`dwOSVersionInfoSize` + 4×DWORD + 128×WCHAR + 2×WORD）。
typedef struct {
    DWORD dwOSVersionInfoSize;
    DWORD dwMajorVersion;
    DWORD dwMinorVersion;
    DWORD dwBuildNumber;
    DWORD dwPlatformId;
    uint16_t szCSDVersion[128];
    uint16_t wServicePackMajor;
    uint16_t wServicePackMinor;
} RTL_OSVERSIONINFOEXW;

// [降级] Linux 上没有 Windows 版本真值 ⇒ 报 10.0.19045（Win10 22H2）。
//   为什么这样"不撒谎"：调用方（WPF/第三方库）只用它做**分支判断**；报一个受支持的版本
//   比让它拿到 0.0.0 而走进未定义分支安全。**这条降级必须留在册**。
int32_t RtlGetVersion(RTL_OSVERSIONINFOEXW *v)
{
    if (!v) return (int32_t)0xC000000D;          /* STATUS_INVALID_PARAMETER */
    // ⚠️ **必须尊重调用方声明的结构大小**（`#35` 实测踩出来的）：
    //   不同的托管声明对同一结构算出的大小**不一样**（`szCSDVersion` 按 ANSI 的 `ByValTStr(128)`
    //   只有 128 字节 ⇒ 整个结构 148 字节；按 Unicode 才是 280）。**写满"我们以为的 280"会越界踩坏
    //   调用方的栈/堆** —— 症状是应用**静默黑屏、无异常**（呈现链根本没跑到）。
    //   ⇒ 只写前 20 字节的固定字段，并**原样回填**调用方声明的大小；其余字段按声明长度**只清不写**。
    DWORD declared = v->dwOSVersionInfoSize;
    if (declared < 5u * sizeof(DWORD)) declared = 5u * sizeof(DWORD);   /* 小于固定头 ⇒ 按固定头算 */

    v->dwOSVersionInfoSize = declared;
    v->dwMajorVersion = 10;
    v->dwMinorVersion = 0;
    v->dwBuildNumber  = 19045;                   /* Win10 22H2：Linux 上没有 Windows 版本真值，这是**降级声明** */
    v->dwPlatformId   = 2;                       /* VER_PLATFORM_WIN32_NT */

    // `szCSDVersion[128]`（WCHAR）只有调用方声明到它时才清 —— 不越界。
    if (declared >= 5u * sizeof(DWORD) + 256u) memset(v->szCSDVersion, 0, 256);
    return 0;                                    /* STATUS_SUCCESS */
}
int32_t RtlGetVersionA(RTL_OSVERSIONINFOEXW *v) { return RtlGetVersion(v); }

// ── user32：SetWindowCompositionAttribute ──────────────────────────────────
// 上游用途：模糊/亚克力（Win10 起）。Linux 上 X11 没有等价物（合成器相关）。
//   HandyControl 的用法是"失败就跳过"，而**缺符号**会让整个应用被掀掉 ⇒ 这里如实失败。
BOOL SetWindowCompositionAttribute(void *hwnd, void *data)
{
    (void)hwnd; (void)data; wpf_set_last_error(E_NOTIMPL_CODE); return 0;
}

// ── msimg32：AlphaBlend ────────────────────────────────────────────────────
// 上游用途：GDI 的 alpha 混合。我们的 gdi32 面只做到"占位"，混合**不做**（如实返回 0）。
BOOL AlphaBlend(void *hdcDst, int32_t xd, int32_t yd, int32_t wd, int32_t hd, void *hdcSrc,
                int32_t xs, int32_t ys, int32_t ws, int32_t hs, void *bf)
{
    (void)hdcDst; (void)xd; (void)yd; (void)wd; (void)hd; (void)hdcSrc;
    (void)xs; (void)ys; (void)ws; (void)hs; (void)bf;
    wpf_set_last_error(E_NOTIMPL_CODE); return 0;
}
BOOL TransparentBlt(void *a, int32_t b, int32_t c, int32_t d, int32_t e, void *f,
                    int32_t g, int32_t h, int32_t i, int32_t j, UINT k)
{
    (void)a;(void)b;(void)c;(void)d;(void)e;(void)f;(void)g;(void)h;(void)i;(void)j;(void)k;
    wpf_set_last_error(E_NOTIMPL_CODE); return 0;
}

// ── dwmapi：DWM 合成 ───────────────────────────────────────────────────────
// 【语义对齐（`#35` 波实测订正）】原先把这几个一律返回 `NOT_SUPPORTED`（"没有 DWM"）——
//   **那是错的口径**：Windows 上"**有 DWM 但合成被关闭**"是**合法且常见**的状态，此时
//   · `DwmIsCompositionEnabled` 返回 **S_OK** 且 `*pfEnabled = FALSE`；
//   · `DwmExtendFrameIntoClientArea` / `DwmSetWindowAttribute` 返回 **S_OK**（什么都不做）。
//   WPF 的 `WindowChromeWorker` 按这些 HRESULT 选分支：一律 `NOT_SUPPORTED` 会让它走进一条
//   **不画客户区**的路（实测：第三方应用开得出窗口、却整屏全黑）。
//   ⇒ 改成"**有 DWM、合成关闭**"这一组语义（这才是 Linux 上的真话：X11 有合成器、但没有 DWM 玻璃）。
typedef struct { int32_t cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight; } MARGINS;
int32_t DwmExtendFrameIntoClientArea(void *hwnd, const MARGINS *m) { (void)hwnd; (void)m; return 0; }  /* S_OK（无效果）*/
int32_t DwmSetWindowAttribute(void *hwnd, DWORD attr, const void *val, DWORD cb)
{ (void)hwnd; (void)attr; (void)val; (void)cb; return 0; }                                            /* S_OK（忽略）*/
int32_t DwmEnableBlurBehindWindow(void *hwnd, const void *bb) { (void)hwnd; (void)bb; return 0; }      /* S_OK（无效果）*/
int32_t DwmIsCompositionEnabled(BOOL *enabled) { if (enabled) *enabled = 0; return 0; }               /* S_OK + 关闭 */
// `DwmGetWindowAttribute` 不同：调用方要**真值**（扩展边框/圆角），编不出来 ⇒ 如实失败。
int32_t DwmGetWindowAttribute(void *hwnd, DWORD attr, void *val, DWORD cb)
{ (void)hwnd; (void)attr; (void)val; (void)cb; return (int32_t)0x80004001; }                          /* E_NOTIMPL */

// ── shell32：只补"调用方真的会碰"的三个 + ExtractIconEx 的别名已在 win32_misc.c ──
typedef struct { void *hIcon; int32_t iIcon; DWORD dwAttributes; uint16_t szDisplayName[260]; uint16_t szTypeName[80]; } SHFILEINFOW;
UINT_PTR SHGetFileInfoW(const uint16_t *path, DWORD attrs, SHFILEINFOW *fi, UINT cb, UINT flags)
{ (void)path; (void)attrs; (void)fi; (void)cb; (void)flags; wpf_set_last_error(E_NOTIMPL_CODE); return 0; }
UINT_PTR SHGetFileInfoA(const char *path, DWORD attrs, SHFILEINFOW *fi, UINT cb, UINT flags)
{ (void)path; (void)attrs; (void)fi; (void)cb; (void)flags; wpf_set_last_error(E_NOTIMPL_CODE); return 0; }
HANDLE ShellExecuteW(void *hwnd, const uint16_t *op, const uint16_t *file, const uint16_t *params,
                     const uint16_t *dir, int32_t show)
{ (void)hwnd;(void)op;(void)file;(void)params;(void)dir;(void)show; wpf_set_last_error(E_NOTIMPL_CODE); return (HANDLE)(intptr_t)32; }
int32_t SHCreateItemFromParsingName(const uint16_t *path, void *bc, const void *iid, void **out)
{ (void)path; (void)bc; (void)iid; if (out) *out = NULL; return (int32_t)0x80004005; }   /* E_FAIL */
// ── ★`TASK-0747`（`D-G124` 的 `F-A` 最小修法）· shell32：补 `SHAppBarMessage` ─────
// 【为什么必须补这一个符号】上游 HandyControl 的 `Window.WmGetMinMaxInfo`
//   （声明 `Tools/Interop/InteropMethods.cs:485-486`；调用 `Controls/Window/Window.cs:340-341`，
//    另一处 `Tools/Helper/WindowHelper.cs:142`）**只**在"**首次 map 之后**那一拍
//   `WM_GETMINMAXINFO`"里调 `SHAppBarMessage(4 = ABM_GETTASKBARPOS, …)`
//   （本 shim 的交付点 = `win32_core.c` 里带 `after-map` 字串那一处）；本 shim
//   **从未导出**该符号 ⇒ 每趟启动抛 `EntryPointNotFoundException`，被 demo 自己的
//   `DispatcherUnhandledException` 接住（进程不死，但**每趟启动自报 242 B**）。
//   补上这一个符号即消除该异常（**唯一的**缺件就是它）。
//
// 【结构体（ABI 逐字对齐上游）】`InteropValues.APPBARDATA`（`Tools/Interop/InteropValues.cs:1008-1017`）
//   ＝ `{ int cbSize; IntPtr hWnd; uint uCallbackMessage; uint uEdge; RECT rc; int lParam; }`，
//   其中 `RECT`（同文件 `:228-233`）＝四个 `int` ⇒ x86-64 下 **48 字节**。
//   ⚠️ 本文件刻意**不** include `win32_abi.h`（该头与本文件顶部的局部 typedef 相冲），
//   故按本文件既有体例（`SHFILEINFOW`）**局部**声明，并用 `_Static_assert` 把偏移钉死 ——
//   ABI 错一个字节，这里**编译期就红**，不留给运行期。
#define ABM_GETTASKBARPOS 4
typedef struct { int32_t left, top, right, bottom; } SH_RECT;
typedef struct {
    int32_t  cbSize;            /*  0 */
    uint32_t _pad_cbSize;       /*  4  x86-64：后面的 `void *` 需 8 字节对齐 */
    void    *hWnd;              /*  8  IntPtr */
    uint32_t uCallbackMessage;  /* 16 */
    uint32_t uEdge;             /* 20 */
    SH_RECT  rc;                /* 24  四个 int32 */
    int32_t  lParam;            /* 40 */
    uint32_t _pad_lParam;       /* 44 */
} SH_APPBARDATA;                /* 48 */
_Static_assert(sizeof(SH_APPBARDATA) == 48, "APPBARDATA 必须 48 字节（上游 InteropValues.APPBARDATA）");
_Static_assert(offsetof(SH_APPBARDATA, hWnd)             ==  8, "APPBARDATA.hWnd @8");
_Static_assert(offsetof(SH_APPBARDATA, uCallbackMessage) == 16, "APPBARDATA.uCallbackMessage @16");
_Static_assert(offsetof(SH_APPBARDATA, uEdge)            == 20, "APPBARDATA.uEdge @20");
_Static_assert(offsetof(SH_APPBARDATA, rc)               == 24, "APPBARDATA.rc @24");
_Static_assert(offsetof(SH_APPBARDATA, lParam)           == 40, "APPBARDATA.lParam @40");

// 【仪器】只有显式开 `WPF_LINUX_APPBAR_DIAG` 才打（与 `WPF_LINUX_CREATE_DIAG` 同体例），
//   且**自带次数上限**。为什么要**被调方自报**：本缺陷的判据是"启动期该异常不得出现"，
//   而"符号被查到、并且真的被调到了"这件事在托管侧**没有**读数 —— 异常消失既可能是
//   "补上了"，也可能是"这一拍根本没走到" ⇒ 必须有一次自报把这两者分开（否则判据是假的）。
static void wpf_appbar_diag(int msg, int cbSize_in, int x, int y, int w, int h, int ret)
{
    const char *e = getenv("WPF_LINUX_APPBAR_DIAG");
    if (!e || !*e || *e == '0') return;
    static int n = 0;
    if (n++ >= 8) return;
    fprintf(stderr, "[APPBAR_DIAG] msg=%d cbSize_in=%d rc_work=%d,%d %dx%d return=%d\n",
            msg, cbSize_in, x, y, w, h, ret);
    fflush(stderr);
}

// 【返回值口径（**如实写：这不是"已实现 AppBar"**）】
//   `ABM_GETTASKBARPOS` 在真 Windows 上查询成功返 **TRUE(1)**；**本移植返回 0**：
//     ① Linux/X11 上没有 AppBar（任务栏注册）这个概念，返 TRUE 等于声称存在一个我们
//        **给不出的**任务栏 —— 与本文件顶部"绝不返回看起来有效的假句柄"是同一条口径；
//     ② **决定性**：唯一现取到的调用方（上游 `Window.cs:340`）把**非 0** 读成"任务栏自动隐藏"，
//        随后改走 `GetMonitorInfo().rcWork` 覆盖 `ptMaxSize/ptMaxPosition`；而本 shim 的
//        `GetMonitorInfoW` 是 `info->rcWork = info->rcMonitor`（＝**整屏**，近似）⇒ 返 TRUE 会把
//        **最大化尺寸从工作区改成整屏**（裸 Xvfb 上已是 `1024→1023` 的一像素位移，有面板的
//        桌面上更大）——**那是另一件事的修法**（`GetMonitorInfoW` 的 `rcWork` 语义缺失，
//        同族**另案**），**不捆进本波**。
//        返 0 ⇒ 调用方不覆盖 ⇒ 本 shim 的默认（`fill_minmaxinfo_defaults`：`ptMaxSize`
//        **已经是工作区**）**原地生效** ⇒ 几何逐字不变（这正是本任务的合格线）。
// 【`rc` 照填】按上游形状填 `rc` ＝ **本移植的工作区事实**（`wpf_x11_workarea()`：EWMH
//   `_NET_WORKAREA` 第一格；无 WM/无该属性 ⇒ 屏幕；**无 X ⇒ 全 0 = "不知道"，不猜**）。
//   ⚠️ 这是**工作区的近似**，**不是**任务栏矩形 —— Linux 上查询不到任务栏几何，本函数
//   **不去编一个**。现取到的调用方**不读** `rc`，但保持形状忠实（换调用方时形状是对的）。
// 【不写的字段】`cbSize`（入参，由调用方填）、`hWnd`/`uCallbackMessage`/`uEdge`/`lParam`
//   （"任务栏句柄／回调消息／停靠边"这类我们给不出的量）**一律不动** —— 写 0 会被读成
//   "合法句柄 0"，比不写更坏。
// 【其余消息】一律 `wpf_set_last_error(E_NOTIMPL_CODE); return 0;`：AppBar 的注册/撤销/定位/
//   状态查询需要**真正的 AppBar 宿主**，本移植没有 ⇒ **如实失败**，不假装成功。
uint32_t SHAppBarMessage(int32_t dwMessage, SH_APPBARDATA *abd)
{
    if (dwMessage == ABM_GETTASKBARPOS && abd) {
        int x = 0, y = 0, w = 0, h = 0;
        wpf_x11_workarea(&x, &y, &w, &h);      /* 无 X ⇒ 全 0（"不知道"，不猜）*/
        abd->rc.left   = x;
        abd->rc.top    = y;
        abd->rc.right  = x + w;
        abd->rc.bottom = y + h;
        wpf_appbar_diag(dwMessage, abd->cbSize, x, y, w, h, 0);
        /* 见上"返回值口径"②：返 0 ＝ "查不到任务栏"。**不是**"沉默地假装成功"：
           既有明确的返回值，也有明确的 last-error。 */
        wpf_set_last_error(E_NOTIMPL_CODE);
        return 0;
    }
    wpf_set_last_error(E_NOTIMPL_CODE);
    return 0;
}
// ── ★TASK-0747 段结束（以下不属于本段）───────────────────────────────────────
