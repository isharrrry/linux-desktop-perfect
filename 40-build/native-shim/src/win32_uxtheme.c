// 【ACC-028 / K-114】UXTHEME 最小桩集。
// WinForms 启动时会问"当前应用是否启用视觉样式"，Windows 上走 uxtheme.dll：
//   IsAppThemed / IsThemeActive → BOOL
//   OpenThemeData / CloseThemeData → HTHEME 句柄
//   GetTheme* → 主题属性（颜色/尺寸/字体）
// Linux 上没有该服务 ⇒ 全部按"无主题"返回（FALSE / 默认值），
// 让 WinForms 走它的经典（非 themed）绘制路径 —— 那条路不依赖 HDC 主题服务。
#include "win32_internal.h"

#ifndef FALSE
#define FALSE 0
#endif

// 句柄：用一个非 NULL 的哨兵值即可，CloseThemeData 忽略它
#define WPF_FAKE_HTHEME ((void *)(uintptr_t)0x7A7A)

BOOL  IsAppThemed(void)   { return FALSE; }
BOOL  IsCompositionActive(void) { return FALSE; }
BOOL  IsThemeDialogTextureEnabled(void *hwnd) { (void)hwnd; return FALSE; }

void *OpenThemeData(void *hwnd, const wchar_t *classList)
{
    (void)hwnd; (void)classList;
    return WPF_FAKE_HTHEME;
}
void *OpenThemeDataForDpi(void *hwnd, const wchar_t *classList, unsigned dpi)
{
    (void)hwnd; (void)classList; (void)dpi;
    return WPF_FAKE_HTHEME;
}
int   CloseThemeData(void *htheme) { (void)htheme; return 0; }

BOOL  IsThemePartDefined(void *htheme, int part, int state)
{
    (void)htheme; (void)part; (void)state;
    return FALSE;   // 没有主题部件 ⇒ 调用方走自绘
}

int   GetThemeBool(void *htheme, int part, int state, int prop, BOOL *out)
{
    (void)htheme; (void)part; (void)state; (void)prop;
    if (out) *out = FALSE;
    return 0;
}
int   GetThemeInt(void *htheme, int part, int state, int prop, int *out)
{
    (void)htheme; (void)part; (void)state; (void)prop;
    if (out) *out = 0;
    return 0;
}
int   GetThemeColor(void *htheme, int part, int state, int prop, uint32_t *out)
{
    (void)htheme; (void)part; (void)state; (void)prop;
    if (out) *out = 0xFFFFFFFFu;   // CLR_INVALID，调用方会用默认色
    return 0;
}
int   GetThemeMetric(void *htheme, int part, int state, int prop, int *out)
{
    (void)htheme; (void)part; (void)state; (void)prop;
    if (out) *out = 0;
    return 0;
}
int   GetThemePartSize(void *htheme, void *hdc, int part, int state, const void *rect, int eSize, void *outSize)
{
    (void)htheme; (void)hdc; (void)part; (void)state; (void)rect; (void)eSize; (void)outSize;
    return 1;   // S_OK 但尺寸不变（调用方回退到自算）
}
int   GetThemeSysColor(void *htheme, int iColorId)
{
    (void)htheme; (void)iColorId;
    return 0;
}
int   GetThemeSysSize(void *htheme, int iSizeId)
{
    (void)htheme; (void)iSizeId;
    return 0;
}
int   DrawThemeBackground(void *htheme, void *hdc, int part, int state, const void *rect, const void *clip)
{
    (void)htheme; (void)hdc; (void)part; (void)state; (void)rect; (void)clip;
    return 0;   // 不画，调用方自绘
}
int   DrawThemeTextEx(void *htheme, void *hdc, int part, int state, const wchar_t *text,
                      int cch, uint32_t flags, void *rect, const void *opts)
{
    (void)htheme; (void)hdc; (void)part; (void)state; (void)text; (void)cch;
    (void)flags; (void)rect; (void)opts;
    return 0;
}
