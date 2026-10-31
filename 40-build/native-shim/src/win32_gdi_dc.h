// 【ACC-028 / K-144】真 HDC —— 见 win32_gdi_dc.c 的说明。
// 对外**只用不透明指针** + 绘制函数，避免调用方 include `win32_internal.h`
//（`win32_abi.h` 与 `win32_internal.h` 对 `DefWindowProcW` 的声明不同，同时包含会冲突）。
#ifndef WPF_GDI_DC_H
#define WPF_GDI_DC_H

#include "win32_abi.h"

typedef struct wpf_hdc wpf_hdc;        // 不透明：定义在 .c 里

wpf_hdc *wpf_hdc_get(HWND hwnd);       // 拿/建窗口 HDC；失败 NULL
wpf_hdc *wpf_hdc_compat(HDC src);      // 离屏 HDC
wpf_hdc *wpf_hdc_of(HDC dc);           // HDC → 结构；不是我们的返回 NULL
void     wpf_hdc_release(wpf_hdc *h);  // XFlush（不清缓存）
void     wpf_hdc_destroy(wpf_hdc *h);
void     wpf_hdc_destroy_all(void);

// ── 绘制（都接受 HDC，内部转结构；不是我们的 HDC 就静默 no-op）────────────────
int      wpf_draw_fill_rect(HDC dc, const WPF_RECT *rc, uint32_t colorref);
int      wpf_draw_frame_rect(HDC dc, const WPF_RECT *rc, uint32_t colorref);
int      wpf_draw_line(HDC dc, int32_t x1, int32_t y1, int32_t x2, int32_t y2, uint32_t colorref);
int      wpf_draw_text(HDC dc, int32_t x, int32_t y, const uint16_t *s, int32_t len);
int      wpf_draw_text_bg(HDC dc, const WPF_RECT *rc);   // OPAQUE 背景填充
int      wpf_draw_set_text_color(HDC dc, uint32_t colorref);   // 返回 1=成功
int      wpf_draw_set_bk_color(HDC dc, uint32_t colorref);
int      wpf_draw_set_bk_mode(HDC dc, int mode);
int      wpf_draw_flush(HDC dc);
int      wpf_draw_select_object(HDC dc, HGDIOBJ obj, int kind);   // 1=brush 2=pen 3=font
int      wpf_gdi_kind(HGDIOBJ o);

uint32_t wpf_colorref_to_x(uint32_t colorref);                 // 0x00BBGGRR → 0xRRGGBB
int      wpf_utf16_to_utf8(const uint16_t *s, int32_t len, char *out, int outsz);

// GDI 对象（brush/pen）—— 只需记住颜色
typedef struct wpf_gdi_obj_opaque wpf_gdi_obj_opaque;
HGDIOBJ  wpf_gdi_new_brush(uint32_t colorref);
HGDIOBJ  wpf_gdi_new_pen(uint32_t colorref, int32_t width, int style);
uint32_t wpf_gdi_color(HGDIOBJ o, uint32_t dflt);

#endif
