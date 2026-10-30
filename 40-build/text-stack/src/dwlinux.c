/* libdwlinux —— text-stack 实现（SYS-4：MOD-31 枚举 / MOD-32 shaping / MOD-33 断行 / MOD-35 轮廓）
 *
 * 设计依据：30-design/设计文档包/03-软件设计说明.md §3.4（LDP-E-TEXT-403~412）
 * 判据：ACC-013~ACC-016（50-verify/text 下的 .mjs，双路径对拍：本库 vs hb-oracle.py）
 *
 * 加载策略：本机发行版只装运行时库（libharfbuzz.so.0/libfreetype.so.6/libfontconfig.so.1/
 *   libicui18n.so.70），没有 *.so 开发符号链接；因此用编译期头（40-build/lock/headers 取件）
 *   拿类型、用 dlopen 在运行期绑定实现，二者结合既不猜布局也不要求 dev 包。
 *
 * 纪律：
 *   ①所有入口先判空与长度（SAFE-013），失败返回负错误码，不静默；
 *   ②错误码用 03 §3.4 的 LDP-E-TEXT-4xx，文案由 dwp_error() 给；
 *   ③不放全局锁之外的可变状态：初始化幂等（dwp_init 可重复调用）。
 */

#include "dwlinux.h"

#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <fontconfig/fontconfig.h>
#include <ft2build.h>
#include FT_FREETYPE_H
#include FT_OUTLINE_H
#include <hb-ft.h>
#include <hb.h>
#include <unicode/ubrk.h>

#define DWP_VERSION_STR "libdwlinux 10.0.11 (fontconfig+freetype+harfbuzz+icu via dlopen)"

/* ---------------------------------------------------------------- dlopen 表 */

typedef UBreakIterator *(*fn_ubrk_open)(UBreakIteratorType, const char *, const UChar *, int32_t, UErrorCode *);
typedef int32_t (*fn_ubrk_next)(UBreakIterator *);
typedef void (*fn_ubrk_close)(UBreakIterator *);
typedef void (*fn_u_strFromUTF8)(UChar *, int32_t, int32_t *, const char *, int32_t, UErrorCode *);

static struct {
    void *h_fc, *h_ft, *h_hb, *h_i18n, *h_uc;
    /* fontconfig */
    FcBool (*FcInit)(void);
    FcPattern *(*FcNameParse)(const FcChar8 *);
    FcConfig *(*FcConfigGetCurrent)(void);
    void (*FcConfigSubstitute)(FcConfig *, FcPattern *, FcMatchKind);
    void (*FcDefaultSubstitute)(FcPattern *);
    FcPattern *(*FcFontMatch)(FcConfig *, FcPattern *, FcResult *);
    FcResult (*FcPatternGetString)(const FcPattern *, const char *, int, FcChar8 **);
    FcResult (*FcPatternGetInteger)(const FcPattern *, const char *, int, int *);
    void (*FcPatternDestroy)(FcPattern *);
    FcFontSet *(*FcFontList)(FcConfig *, FcPattern *, FcObjectSet *);
    FcFontSet *(*FcFontSort)(FcConfig *, FcPattern *, FcBool, FcCharSet **, FcResult *);
    FcPattern *(*FcPatternCreate)(void);
    FcBool (*FcPatternAddString)(FcPattern *, const char *, const FcChar8 *);
    void (*FcCharSetDestroy)(FcCharSet *);
    void (*FcFontSetDestroy)(FcFontSet *);
    /* freetype */
    FT_Error (*FT_Init_FreeType)(FT_Library *);
    FT_Error (*FT_New_Face)(FT_Library, const char *, FT_Long, FT_Face *);
    FT_Error (*FT_Set_Pixel_Sizes)(FT_Face, FT_UInt, FT_UInt);
    FT_Error (*FT_Load_Glyph)(FT_Face, FT_UInt, FT_Int32);
    FT_UInt (*FT_Get_Char_Index)(FT_Face, FT_ULong);
    FT_Error (*FT_Outline_Decompose)(FT_Outline *, const FT_Outline_Funcs *, void *);
    FT_Error (*FT_Done_Face)(FT_Face);
    void (*FT_Done_FreeType)(FT_Library);
    /* harfbuzz */
    hb_font_t *(*hb_ft_font_create_referenced)(FT_Face);
    hb_buffer_t *(*hb_buffer_create)(void);
    void (*hb_buffer_destroy)(hb_buffer_t *);
    void (*hb_buffer_add_utf8)(hb_buffer_t *, const char *, int, unsigned, int);
    void (*hb_buffer_guess_segment_properties)(hb_buffer_t *);
    void (*hb_shape)(hb_font_t *, hb_buffer_t *, const hb_feature_t *, unsigned);
    hb_glyph_info_t *(*hb_buffer_get_glyph_infos)(hb_buffer_t *, unsigned *);
    hb_glyph_position_t *(*hb_buffer_get_glyph_positions)(hb_buffer_t *, unsigned *);
    void (*hb_font_destroy)(hb_font_t *);
    /* icu */
    fn_ubrk_open ubrk_open;
    fn_ubrk_next ubrk_next;
    fn_ubrk_close ubrk_close;
    fn_u_strFromUTF8 u_strFromUTF8;
} g;

static int g_init_rc = 0; /* 0=未初始化；1=就绪；负=错误码 */

static void *open_lib(const char *const *names, void **out)
{
    for (int i = 0; names[i]; i++) {
        void *h = dlopen(names[i], RTLD_NOW | RTLD_GLOBAL);
        if (h) {
            *out = h;
            return h;
        }
    }
    return NULL;
}

static void *sym(void *h, const char *name, const char *alt)
{
    void *p = h ? dlsym(h, name) : NULL;
    if (!p && h && alt) p = dlsym(h, alt);
    return p;
}

const char *dwp_version(void) { return DWP_VERSION_STR; }

const char *dwp_error(int code)
{
    switch (code) {
    case DWP_OK: return "ok";
    case DWP_E_FC_UNAVAILABLE: return "LDP-E-TEXT-404 fontconfig 不可用";
    case DWP_E_NO_MATCH: return "LDP-E-TEXT-403 无匹配字体";
    case DWP_E_SHAPE_FAILED: return "LDP-E-TEXT-405 shaping 失败";
    case DWP_E_CLUSTER_RANGE: return "LDP-E-TEXT-406 cluster 越界";
    case DWP_E_LINE_FAILED: return "LDP-E-TEXT-407 断行器不可用";
    case DWP_E_FACE_FAILED: return "LDP-E-TEXT-411 字体解析失败";
    case DWP_E_OUTLINE_EMPTY: return "LDP-E-TEXT-412 轮廓为空";
    case DWP_E_INVALIDARG: return "E_INVALIDARG 空指针或容量不足（SAFE-013）";
    default: return "E_FAIL 未知错误";
    }
}

int dwp_init(void)
{
    if (g_init_rc != 0) return g_init_rc > 0 ? 0 : g_init_rc;

    static const char *const fc_names[] = { "libfontconfig.so.1", "libfontconfig.so", NULL };
    static const char *const ft_names[] = { "libfreetype.so.6", "libfreetype.so", NULL };
    static const char *const hb_names[] = { "libharfbuzz.so.0", "libharfbuzz.so", NULL };
    static const char *const i18n_names[] = { "libicui18n.so.70", "libicui18n.so", NULL };
    static const char *const uc_names[] = { "libicuuc.so.70", "libicuuc.so", NULL };

    if (!open_lib(fc_names, &g.h_fc) || !open_lib(ft_names, &g.h_ft) || !open_lib(hb_names, &g.h_hb) ||
        !open_lib(i18n_names, &g.h_i18n) || !open_lib(uc_names, &g.h_uc)) {
        g_init_rc = DWP_E_FC_UNAVAILABLE;
        return g_init_rc;
    }

#define BIND(handle, field, name, alt)                                                     \
    do {                                                                                   \
        *(void **)(&g.field) = sym(handle, name, alt);                                     \
        if (!g.field) {                                                                    \
            g_init_rc = DWP_E_LINE_FAILED;                                                 \
            return g_init_rc;                                                              \
        }                                                                                  \
    } while (0)

    BIND(g.h_fc, FcInit, "FcInit", NULL);
    BIND(g.h_fc, FcNameParse, "FcNameParse", NULL);
    BIND(g.h_fc, FcConfigGetCurrent, "FcConfigGetCurrent", NULL);
    BIND(g.h_fc, FcConfigSubstitute, "FcConfigSubstitute", NULL);
    BIND(g.h_fc, FcDefaultSubstitute, "FcDefaultSubstitute", NULL);
    BIND(g.h_fc, FcFontMatch, "FcFontMatch", NULL);
    BIND(g.h_fc, FcPatternGetString, "FcPatternGetString", NULL);
    BIND(g.h_fc, FcPatternGetInteger, "FcPatternGetInteger", NULL);
    BIND(g.h_fc, FcPatternDestroy, "FcPatternDestroy", NULL);
    BIND(g.h_fc, FcFontList, "FcFontList", NULL);
    BIND(g.h_fc, FcFontSort, "FcFontSort", NULL);
    BIND(g.h_fc, FcPatternCreate, "FcPatternCreate", NULL);
    BIND(g.h_fc, FcPatternAddString, "FcPatternAddString", NULL);
    BIND(g.h_fc, FcCharSetDestroy, "FcCharSetDestroy", NULL);
    BIND(g.h_fc, FcFontSetDestroy, "FcFontSetDestroy", NULL);

    BIND(g.h_ft, FT_Init_FreeType, "FT_Init_FreeType", NULL);
    BIND(g.h_ft, FT_New_Face, "FT_New_Face", NULL);
    BIND(g.h_ft, FT_Set_Pixel_Sizes, "FT_Set_Pixel_Sizes", NULL);
    BIND(g.h_ft, FT_Load_Glyph, "FT_Load_Glyph", NULL);
    BIND(g.h_ft, FT_Get_Char_Index, "FT_Get_Char_Index", NULL);
    BIND(g.h_ft, FT_Outline_Decompose, "FT_Outline_Decompose", NULL);
    BIND(g.h_ft, FT_Done_Face, "FT_Done_Face", NULL);
    BIND(g.h_ft, FT_Done_FreeType, "FT_Done_FreeType", NULL);

    BIND(g.h_hb, hb_ft_font_create_referenced, "hb_ft_font_create_referenced", NULL);
    BIND(g.h_hb, hb_buffer_create, "hb_buffer_create", NULL);
    BIND(g.h_hb, hb_buffer_destroy, "hb_buffer_destroy", NULL);
    BIND(g.h_hb, hb_buffer_add_utf8, "hb_buffer_add_utf8", NULL);
    BIND(g.h_hb, hb_buffer_guess_segment_properties, "hb_buffer_guess_segment_properties", NULL);
    BIND(g.h_hb, hb_shape, "hb_shape", NULL);
    BIND(g.h_hb, hb_buffer_get_glyph_infos, "hb_buffer_get_glyph_infos", NULL);
    BIND(g.h_hb, hb_buffer_get_glyph_positions, "hb_buffer_get_glyph_positions", NULL);
    BIND(g.h_hb, hb_font_destroy, "hb_font_destroy", NULL);

    /* ICU：符号带版本后缀（Ubuntu 22.04 = _70）；两个名字都试，失败按 407 报 */
    g.ubrk_open = (fn_ubrk_open)sym(g.h_i18n, "ubrk_open_70", "ubrk_open");
    g.ubrk_next = (fn_ubrk_next)sym(g.h_i18n, "ubrk_next_70", "ubrk_next");
    g.ubrk_close = (fn_ubrk_close)sym(g.h_i18n, "ubrk_close_70", "ubrk_close");
    g.u_strFromUTF8 = (fn_u_strFromUTF8)sym(g.h_uc, "u_strFromUTF8_70", "u_strFromUTF8");
    if (!g.ubrk_open || !g.ubrk_next || !g.ubrk_close || !g.u_strFromUTF8) {
        g_init_rc = DWP_E_LINE_FAILED;
        return g_init_rc;
    }
#undef BIND

    if (!g.FcInit()) {
        g_init_rc = DWP_E_FC_UNAVAILABLE;
        return g_init_rc;
    }
    g_init_rc = 1;
    return 0;
}

/* ---------------------------------------------------------------- MOD-31 字体枚举 */

int dwp_font_count(void)
{
    int rc = dwp_init();
    if (rc < 0) return rc;
    FcPattern *pat = g.FcNameParse((const FcChar8 *)":");
    if (!pat) return DWP_E_FC_UNAVAILABLE;
    FcFontSet *set = g.FcFontList(g.FcConfigGetCurrent(), pat, NULL);
    g.FcPatternDestroy(pat);
    if (!set) return DWP_E_FC_UNAVAILABLE;
    int n = set->nfont;
    g.FcFontSetDestroy(set);
    return n;
}

int dwp_font_family(int index, char *buf, int cap)
{
    int rc = dwp_init();
    if (rc < 0) return rc;
    if (!buf || cap <= 0 || index < 0) return DWP_E_INVALIDARG;
    FcPattern *pat = g.FcNameParse((const FcChar8 *)":");
    if (!pat) return DWP_E_FC_UNAVAILABLE;
    FcFontSet *set = g.FcFontList(g.FcConfigGetCurrent(), pat, NULL);
    g.FcPatternDestroy(pat);
    if (!set) return DWP_E_FC_UNAVAILABLE;
    int out = DWP_E_NO_MATCH;
    if (index < set->nfont) {
        FcChar8 *fam = NULL;
        if (g.FcPatternGetString(set->fonts[index], FC_FAMILY, 0, &fam) == FcResultMatch && fam) {
            snprintf(buf, (size_t)cap, "%s", (const char *)fam);
            out = 0;
        }
    }
    g.FcFontSetDestroy(set);
    return out;
}

int dwp_match(const char *family, DwpMatch *out)
{
    int rc = dwp_init();
    if (rc < 0) return rc;
    if (!family || !out) return DWP_E_INVALIDARG;
    memset(out, 0, sizeof(*out));
    FcPattern *pat = g.FcNameParse((const FcChar8 *)family);
    if (!pat) return DWP_E_FC_UNAVAILABLE;
    g.FcConfigSubstitute(g.FcConfigGetCurrent(), pat, FcMatchPattern);
    g.FcDefaultSubstitute(pat);
    FcResult result = FcResultNoMatch;
    FcPattern *match = g.FcFontMatch(g.FcConfigGetCurrent(), pat, &result);
    g.FcPatternDestroy(pat);
    if (!match) return DWP_E_NO_MATCH;

    FcChar8 *file = NULL, *fam = NULL;
    int idx = 0;
    int ok = g.FcPatternGetString(match, FC_FILE, 0, &file) == FcResultMatch;
    g.FcPatternGetInteger(match, FC_INDEX, 0, &idx);
    if (g.FcPatternGetString(match, FC_FAMILY, 0, &fam) != FcResultMatch) fam = NULL;
    if (ok && file) {
        snprintf(out->file, sizeof(out->file), "%s", (const char *)file);
        snprintf(out->family, sizeof(out->family), "%s", fam ? (const char *)fam : family);
        out->index = idx;
    }
    g.FcPatternDestroy(match);
    return ok && file ? 0 : DWP_E_NO_MATCH;
}

/* ---------------------------------------------------------------- 面加载（MOD-31/32 共用） */

typedef struct {
    FT_Library lib;
    FT_Face face;
} dwp_face;

static int face_open(const char *family, int size, dwp_face *fh)
{
    DwpMatch m;
    int rc = dwp_match(family, &m);
    if (rc < 0) return rc;
    if (g.FT_Init_FreeType(&fh->lib) != 0) return DWP_E_FACE_FAILED;
    if (g.FT_New_Face(fh->lib, m.file, m.index, &fh->face) != 0) {
        g.FT_Done_FreeType(fh->lib);
        fh->lib = NULL;
        return DWP_E_FACE_FAILED;
    }
    g.FT_Set_Pixel_Sizes(fh->face, 0, (FT_UInt)(size > 0 ? size : 16));
    return 0;
}

static void face_close(dwp_face *fh)
{
    if (fh->face) g.FT_Done_Face(fh->face);
    if (fh->lib) g.FT_Done_FreeType(fh->lib);
    fh->face = NULL;
    fh->lib = NULL;
}

/* ---------------------------------------------------------------- MOD-32 shaping */

int dwp_shape(const char *family, const char *text, int size,
              uint32_t *glyphs, int32_t *advances, uint32_t *clusters, int cap)
{
    int rc = dwp_init();
    if (rc < 0) return rc;
    if (!family || !text) return DWP_E_INVALIDARG;
    if ((glyphs || advances || clusters) && cap <= 0) return DWP_E_INVALIDARG;

    dwp_face fh = { NULL, NULL };
    rc = face_open(family, size, &fh);
    if (rc < 0) return rc;

    hb_font_t *font = g.hb_ft_font_create_referenced(fh.face);
    if (!font) {
        face_close(&fh);
        return DWP_E_SHAPE_FAILED;
    }
    hb_buffer_t *buf = g.hb_buffer_create();
    if (!buf) {
        g.hb_font_destroy(font);
        face_close(&fh);
        return DWP_E_SHAPE_FAILED;
    }
    int len = (int)strlen(text);
    g.hb_buffer_add_utf8(buf, text, len, 0, len);
    g.hb_buffer_guess_segment_properties(buf);
    g.hb_shape(font, buf, NULL, 0);

    unsigned n = 0;
    hb_glyph_info_t *info = g.hb_buffer_get_glyph_infos(buf, &n);
    hb_glyph_position_t *pos = g.hb_buffer_get_glyph_positions(buf, &n);
    int out = (int)n;
    if ((glyphs || advances || clusters) && (int)n > cap) {
        /* 容量不足：按 SAFE-013 报错，不截断写越界 */
        out = DWP_E_INVALIDARG;
    } else if (info && pos) {
        for (unsigned i = 0; i < n; i++) {
            if (glyphs) glyphs[i] = info[i].codepoint;
            if (advances) advances[i] = pos[i].x_advance;
            if (clusters) clusters[i] = info[i].cluster;
        }
    } else {
        out = DWP_E_SHAPE_FAILED;
    }
    g.hb_buffer_destroy(buf);
    g.hb_font_destroy(font);
    face_close(&fh);
    return out;
}

int dwp_fallback(const char *family, const char *text, char *buf, int cap)
{
    int rc = dwp_init();
    if (rc < 0) return rc;
    if (!buf || cap <= 0) return DWP_E_INVALIDARG;

    FcPattern *pat = NULL;
    if (family && family[0]) {
        pat = g.FcNameParse((const FcChar8 *)family);
    } else {
        pat = g.FcPatternCreate();
        if (pat) {
            g.FcPatternAddString(pat, FC_FAMILY, (const FcChar8 *)"sans");
        }
    }
    if (!pat) return DWP_E_FC_UNAVAILABLE;
    g.FcConfigSubstitute(g.FcConfigGetCurrent(), pat, FcMatchPattern);
    g.FcDefaultSubstitute(pat);
    FcResult result = FcResultMatch;
    FcCharSet *charset = NULL;
    FcFontSet *set = g.FcFontSort(g.FcConfigGetCurrent(), pat, FcTrue, &charset, &result);
    g.FcPatternDestroy(pat);
    if (!set) return DWP_E_NO_MATCH;

    int written = 0;
    size_t used = 0;
    char last[256] = { 0 };
    for (int i = 0; i < set->nfont && used + 1 < (size_t)cap; i++) {
        FcChar8 *fam = NULL;
        if (g.FcPatternGetString(set->fonts[i], FC_FAMILY, 0, &fam) != FcResultMatch || !fam) continue;
        if (strncmp(last, (const char *)fam, sizeof(last)) == 0) continue;
        snprintf(last, sizeof(last), "%s", (const char *)fam);
        int n = snprintf(buf + used, (size_t)cap - used, "%s\n", (const char *)fam);
        if (n <= 0) break;
        used += (size_t)n;
        written++;
    }
    g.FcFontSetDestroy(set);
    if (charset) g.FcCharSetDestroy(charset);
    return written;
}

/* ---------------------------------------------------------------- MOD-33 双向 */

typedef struct UBiDi UBiDi;
enum { UBIDI_DEFAULT_LTR = 0xfe };
static UBiDi *(*ubidi_openSized)(int32_t, int32_t, UErrorCode *);
static void (*ubidi_close)(UBiDi *);
static void (*ubidi_setPara)(UBiDi *, const UChar *, int32_t, uint8_t, uint8_t *, UErrorCode *);
static const uint8_t *(*ubidi_getLevels)(UBiDi *, UErrorCode *);
static int32_t (*ubidi_getLength)(UBiDi *);

static void bind_bidi(void)
{
    if (ubidi_openSized) return;
    ubidi_openSized = (UBiDi * (*)(int32_t, int32_t, UErrorCode *))sym(g.h_uc, "ubidi_openSized_70", "ubidi_openSized");
    ubidi_close = (void (*)(UBiDi *))sym(g.h_uc, "ubidi_close_70", "ubidi_close");
    ubidi_setPara = (void (*)(UBiDi *, const UChar *, int32_t, uint8_t, uint8_t *, UErrorCode *))sym(g.h_uc, "ubidi_setPara_70", "ubidi_setPara");
    ubidi_getLevels = (const uint8_t *(*)(UBiDi *, UErrorCode *))sym(g.h_uc, "ubidi_getLevels_70", "ubidi_getLevels");
    ubidi_getLength = (int32_t (*)(UBiDi *))sym(g.h_uc, "ubidi_getLength_70", "ubidi_getLength");
}

int dwp_bidi_levels(const char *text, int *levels, int cap)
{
    int rc = dwp_init();
    if (rc < 0) return rc;
    if (!text) return DWP_E_INVALIDARG;
    bind_bidi();
    if (!ubidi_openSized || !ubidi_setPara || !ubidi_getLevels || !ubidi_getLength) return DWP_E_LINE_FAILED;

    int len = (int)strlen(text);
    int u16cap = len * 2 + 16;
    UChar *u16 = (UChar *)calloc((size_t)u16cap, sizeof(UChar));
    if (!u16) return DWP_E_LINE_FAILED;
    UErrorCode err = U_ZERO_ERROR;
    int32_t n16 = 0;
    g.u_strFromUTF8(u16, u16cap, &n16, text, len, &err);
    if (U_FAILURE(err)) {
        free(u16);
        return DWP_E_LINE_FAILED;
    }
    UErrorCode uerr = U_ZERO_ERROR;
    UBiDi *bidi = ubidi_openSized(n16, 0, &uerr);
    if (!bidi || U_FAILURE(uerr)) {
        if (bidi) ubidi_close(bidi);
        free(u16);
        return DWP_E_LINE_FAILED;
    }
    ubidi_setPara(bidi, u16, n16, (uint8_t)UBIDI_DEFAULT_LTR, NULL, &uerr);
    if (U_FAILURE(uerr)) {
        ubidi_close(bidi);
        free(u16);
        return DWP_E_LINE_FAILED;
    }
    const uint8_t *lv = ubidi_getLevels(bidi, &uerr);
    int32_t n = ubidi_getLength(bidi);
    int out = (int)n;
    if (U_FAILURE(uerr) || !lv) {
        ubidi_close(bidi);
        free(u16);
        return DWP_E_LINE_FAILED;
    }
    if (levels && cap < (int)n) out = DWP_E_INVALIDARG;
    else if (levels) for (int32_t i = 0; i < n; i++) levels[i] = (int)lv[i];
    ubidi_close(bidi);
    free(u16);
    return out;
}

/* ---------------------------------------------------------------- MOD-33 断行 */

int dwp_line_breaks(const char *text, int *byte_offsets, int cap)
{
    int rc = dwp_init();
    if (rc < 0) return rc;
    if (!text) return DWP_E_INVALIDARG;
    if (!byte_offsets && cap > 0) return DWP_E_INVALIDARG;

    int len = (int)strlen(text);
    int u16cap = len * 2 + 16;
    UChar *u16 = (UChar *)calloc((size_t)u16cap, sizeof(UChar));
    if (!u16) return DWP_E_LINE_FAILED;
    UErrorCode err = U_ZERO_ERROR;
    int32_t n16 = 0;
    g.u_strFromUTF8(u16, u16cap, &n16, text, len, &err);
    if (U_FAILURE(err)) {
        free(u16);
        return DWP_E_LINE_FAILED;
    }
    UErrorCode uerr = U_ZERO_ERROR;
    UBreakIterator *brk = g.ubrk_open(UBRK_LINE, NULL, u16, n16, &uerr);
    if (!brk || U_FAILURE(uerr)) {
        if (brk) g.ubrk_close(brk);
        free(u16);
        return DWP_E_LINE_FAILED;
    }

    int count = 0;
    int overflow = 0;
    for (;;) {
        int32_t p = g.ubrk_next(brk);
        if (p == UBRK_DONE) break;
        /* UTF-16 偏移 → UTF-8 字节偏移（判据侧统一用字节偏移比较） */
        int32_t bytes = 0;
        for (int32_t i = 0; i < p && i < n16; i++) {
            uint32_t c = (uint32_t)u16[i];
            if (c >= 0xD800 && c <= 0xDBFF && i + 1 < n16) {
                bytes += 4;
                i++;
            } else if (c < 0x80) bytes += 1;
            else if (c < 0x800) bytes += 2;
            else bytes += 3;
        }
        if (byte_offsets) {
            if (count < cap) byte_offsets[count] = bytes;
            else overflow = 1;
        }
        count++;
    }
    g.ubrk_close(brk);
    free(u16);
    if (overflow) return DWP_E_INVALIDARG;
    return count;
}

/* ---------------------------------------------------------------- MOD-35 CFF/TrueType 轮廓 */

typedef struct {
    DwpPoint *pts;
    int cap;
    int n;
    int overflow;
    double last_x, last_y;
} outline_ctx;

static void push_point(outline_ctx *c, double x, double y, int kind)
{
    if (c->pts && c->n < c->cap) {
        c->pts[c->n].x = (float)x;
        c->pts[c->n].y = (float)y;
        c->pts[c->n].kind = kind;
    } else if (c->pts) {
        c->overflow = 1;
    }
    c->n++;
    c->last_x = x;
    c->last_y = y;
}

static int cb_move(const FT_Vector *to, void *u)
{
    outline_ctx *c = (outline_ctx *)u;
    push_point(c, to->x / 64.0, to->y / 64.0, 0);
    return 0;
}
static int cb_line(const FT_Vector *to, void *u)
{
    outline_ctx *c = (outline_ctx *)u;
    push_point(c, to->x / 64.0, to->y / 64.0, 1);
    return 0;
}
static int cb_conic(const FT_Vector *ctrl, const FT_Vector *to, void *u)
{
    outline_ctx *c = (outline_ctx *)u;
    push_point(c, ctrl->x / 64.0, ctrl->y / 64.0, 2);
    push_point(c, to->x / 64.0, to->y / 64.0, 1);
    return 0;
}
static int cb_cubic(const FT_Vector *c1, const FT_Vector *c2, const FT_Vector *to, void *u)
{
    outline_ctx *c = (outline_ctx *)u;
    push_point(c, c1->x / 64.0, c1->y / 64.0, 2);
    push_point(c, c2->x / 64.0, c2->y / 64.0, 2);
    push_point(c, to->x / 64.0, to->y / 64.0, 1);
    return 0;
}

int dwp_outline(const char *family, uint32_t cp, int size, DwpPoint *pts, int cap)
{
    int rc = dwp_init();
    if (rc < 0) return rc;
    if (!family) return DWP_E_INVALIDARG;
    if (!pts && cap > 0) return DWP_E_INVALIDARG;

    dwp_face fh = { NULL, NULL };
    rc = face_open(family, size, &fh);
    if (rc < 0) return rc;
    FT_UInt gid = g.FT_Get_Char_Index(fh.face, cp);
    if (gid == 0) {
        face_close(&fh);
        return DWP_E_OUTLINE_EMPTY;
    }
    if (g.FT_Load_Glyph(fh.face, gid, FT_LOAD_NO_SCALE | FT_LOAD_NO_HINTING) != 0) {
        face_close(&fh);
        return DWP_E_FACE_FAILED;
    }
    FT_Outline *ol = NULL;
    /* 取 glyph slot 里的轮廓：FT_Face 是公开结构，head 之后字段布局由 FT_FREETYPE_H 定义，
       故直接走 face->glyph->outline，不猜偏移。 */
    ol = &fh.face->glyph->outline;
    if (ol->n_points <= 0) {
        face_close(&fh);
        return DWP_E_OUTLINE_EMPTY;
    }
    outline_ctx ctx = { pts, cap, 0, 0, 0, 0 };
    FT_Outline_Funcs funcs;
    memset(&funcs, 0, sizeof(funcs));
    funcs.move_to = cb_move;
    funcs.line_to = cb_line;
    funcs.conic_to = cb_conic;
    funcs.cubic_to = cb_cubic;
    int n = g.FT_Outline_Decompose(ol, &funcs, &ctx);
    face_close(&fh);
    if (n != 0) return DWP_E_OUTLINE_EMPTY;
    if (ctx.overflow) return DWP_E_INVALIDARG;
    return ctx.n;
}
