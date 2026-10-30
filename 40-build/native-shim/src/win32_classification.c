// WPF-on-Linux · M7c #U · `MILGetClassificationTables` 的落点
// ============================================================================
//
// 【它挡的是什么】`PresentationCore/MS/internal/Classification.cs:201-212` 的静态构造
//   调它；而 `Typeface.CheckFastPathNominalGlyphs` → `Classification.GetUnicodeClassUTF16`
//   → `CharAttributeOf(...).Flags` 是**任何文本测量**的必经之路
//   （实测调用栈：`TextBlock.MeasureOverride → Line.Format → SimpleRun.CreateSimpleTextRun
//     → Typeface.CheckFastPathNominalGlyphs`）。少了它 ⇒ `TypeEntryPointNotFoundException`
//   ⇒ 布局在 render pass 里抛出 ⇒ 未处理异常（实测退出码 134）。
//
// 【为什么叫"真实现"而不是"造个空表糊过去"】
//   托管侧要的是**真数据**，不是能跑就行：
//     · `ItemClass` 决定 `IsCombining`/`IsJoiner`（字体回退、字素切分）；
//     · `Flags` 决定"这一串字符能不能走快速路径"（`CharacterComplex`/`CharacterFastText`/
//       `CharacterIdeo`）以及"要不要做 bidi 分析"（`CharacterRTL`）、
//       "这一串是不是空格"（`CharacterSpace`，LineServices 回调读它）；
//     · `Script` 参与 `GetScript(ch)==GetScript(baseChar)` 的字体回退判断与 `isDigit/isLatin`；
//     · `BiDi` 参与 `SimpleTextLine` 的空白判定与 bidi 分析。
//   所以本文件的数据是**从真实 UCD 推出的**（见 src/Linux/tools/gen-unicode-tables.py 的文件头：
//   数据源是本机 Python 3 `unicodedata`，UCD 13.0.0 的 category/bidirectional/combining/
//   mirrored/name）。**这不是手写常量，也不是占位。**

#include "win32_internal.h"

// 生成物（k_wpf_uni_planes / k_wpf_char_attr / k_wpf_mirror_planes）由
// src/win32_unicode_tables.c 定义，声明在 win32_internal.h 里。
// 生成命令：python3 src/WpfGfx.Linux.Native/tools/gen-unicode-tables.py

// ══════════════════════════════════════════════════════════════════════════
//  1. Mirroring 表 —— **降级（恒等映射）**，理由必须写清楚
// ══════════════════════════════════════════════════════════════════════════
// 【为什么是降级】两个独立的事实：
//   ① **托管侧没有消费者**：`Classification._mirroredCharTable` 只在静态构造里被赋值，
//      整棵树 grep 零命中（`grep -rn "CombiningMarksClassification\|_mirroredCharTable"`）⇒
//      它的内容不影响任何托管行为；
//   ② **本地拿不到 BidiMirroring 数据**：Python 的 `unicodedata` 只有 `mirrored()`（是不是
//      镜像字符），**没有"镜像成哪个字符"**；本机也没有 UCD 文本文件（`/usr/share/unicode`
//      不存在，`BidiMirroring.txt` 全盘无）。
//   所以这里给一张**恒等表**（每个码点镜像到自身），并把它登记为"降级"而不是"实现"。
//   **不做名字启发式**（"LEFT x" ↔ "RIGHT x"）：那样得到的数据是猜的，而这个工程的口径是
//   "宁可降级，不猜"。将来若原生 LineServices 真的需要镜像，再按 UCD 的 BidiMirroring.txt
//   生成——结构已经摆好，替换的只是这张表。
//
// 【表的形状】照 `UnicodeClasses` 的读法（两级 + 小整数压缩），只是叶子元素换成"镜像字符"。
//   恒等 ⇒ 叶子元素就是码点低 8 位；但为了**结构真实**（将来能原地替换成真数据），
//   这里仍然按两级表摆出来，而不是一张 `cp==cp` 的函数。
// ══════════════════════════════════════════════════════════════════════════
//  2. CombiningMarksClassification —— **合成（结构有效、语义最小）**
// ══════════════════════════════════════════════════════════════════════════
// 【为什么也是"最小"】同一件事：托管侧**零消费者**（`_combiningMarksClassification`
//   只在静态构造里被赋值）。原生 LineServices 才用它做"基字符 + 组合标记"的合并查表。
//   本工程当前没有 LineServices（可操作 42／实现口径 42 条 `Fs*`/`Lo*` 缺口就是它；旧「111 条」已证 TOOL-UNSOUND、见 D-G70 更正行），所以：
//     · 三张表的指针都指向**真实存在的零长度数组**（不是 NULL）；
//     · 计数一律 0 —— 表示"没有合并条目"，任何按计数遍历的读者都不会读到越界内存；
//     · 登记为**合成**：结构对、语义空。
//   这与"返回失败"不同：一个空表是**真话**（我们确实没有合并数据），
//   而 NULL 会让读者崩在解引用上 —— 那不是"不伪造"，那是制造 bug。
static const uint16_t k_combining_empty[1] = { 0 };

// ══════════════════════════════════════════════════════════════════════════
//  3. 导出：MILGetClassificationTables
// ══════════════════════════════════════════════════════════════════════════
// 托管签名（Classification.cs:199）：`static extern void MILGetClassificationTables(out RawClassificationTables ct)`
//   ⇒ C 侧收一个**指向结构体的指针**、返回 void。
void MILGetClassificationTables(WPF_RAW_CLASSIFICATION_TABLES *ct)
{
    if (!ct) return;                     // 托管侧不可能传 NULL；防御性早退，不写坏内存

    ct->unicode_classes      = (void *)k_wpf_uni_planes;
    ct->character_attributes = (void *)k_wpf_char_attr;

    // 镜像表：形状与 UnicodeClasses 相同（17 平面 × 两级），值是"镜像字符"。
    // **降级**：恒等映射（理由见上面第 1 节）。
    ct->mirroring            = (void *)k_wpf_mirror_planes;

    ct->combining_marks.combining_chars_indexes = (void *)k_combining_empty;
    ct->combining_marks.combining_chars_indexes_table_length = 0;
    ct->combining_marks.combining_chars_indexes_table_segment_length = 0;
    ct->combining_marks.combining_mark_indexes = (void *)k_combining_empty;
    ct->combining_marks.combining_mark_indexes_table_length = 0;
    ct->combining_marks.combination_chars = (void *)k_combining_empty;
    ct->combining_marks.combination_chars_base_count = 0;
    ct->combining_marks.combination_chars_mark_count = 0;
}

// ══════════════════════════════════════════════════════════════════════════
//  4. 自检 + 跨边界对照用访问器
// ══════════════════════════════════════════════════════════════════════════
// `WpfLinuxWin32_UnicodeClassOf` 用**与托管侧逐字相同的两级读法**在本 .so 里再查一遍。
// 用途：测试可以拿它与托管 `Classification.GetUnicodeClassUTF16/GetUnicodeClass`
// （反射调用）**逐码点对拍** —— 这比"没抛异常"强得多：它同时验证了
// ① 表真的被托管侧读到了、② 两级/小整数压缩的约定两边一致、③ 数据没错位。
int WpfLinuxWin32_UnicodeClassOf(uint32_t scalar)
{
    if (scalar > 0x10FFFFu) return -1;

    const uintptr_t *plane = k_wpf_uni_planes[((scalar >> 16) & 0xFFu) % 17u];
    uintptr_t pcc = plane[(scalar & 0xFFFFu) >> 8];
    if (pcc < WPF_UNICODE_CLASS_MAX) return (int)pcc;              // 整块同类：值内联
    const uint16_t *leaf = (const uint16_t *)pcc;
    return (int)leaf[scalar & 0xFFu];
}

// 类值 → CharacterAttribute 的 6 个字节（给对拍用；返回 0 = 类值越界）。
int WpfLinuxWin32_CharAttrOf(int unicode_class, uint8_t out[8])
{
    if (!out || unicode_class < 0 || unicode_class >= k_wpf_char_attr_count) return 0;
    const unsigned char *p = (const unsigned char *)&k_wpf_char_attr[unicode_class];
    for (int i = 0; i < 8; i++) out[i] = p[i];
    return 1;
}

int WpfLinuxWin32_ClassificationClassCount(void) { return k_wpf_char_attr_count; }

// 表自检：类值必须 < 472（否则托管侧会把类值当成指针去解引用 —— 这是本文件
// 唯一一种"错得很安静、崩得很难查"的错误，所以每次跑自检都验一遍）。
int WpfLinuxWin32_ClassificationSelfCheck(void)
{
    if (k_wpf_char_attr_count <= 0 || k_wpf_char_attr_count >= WPF_UNICODE_CLASS_MAX) return 0;
    for (int i = 0; i < k_wpf_char_attr_count; i++) {
        if (k_wpf_char_attr[i].script >= 0x40) return 0;          // ScriptID.Max
        if (k_wpf_char_attr[i].item_class >= 0x0C) return 0;      // ItemClass.MaxClass
        if (k_wpf_char_attr[i].bidi >= 21) return 0;              // DirectionClass.ClassMax
    }
    // 平面 0（BMP）必须是个真指针（托管侧 `Invariant.Assert((long)plane0 >= Max)`）
    if ((uintptr_t)k_wpf_uni_planes[0] < WPF_UNICODE_CLASS_MAX) return 0;
    return 1;
}


// ══════════════════════════════════════════════════════════════════════════
//  5. LoGetEscString —— LineServices 的"转义字符表"（**合成 / 惰性**，见下）
// ══════════════════════════════════════════════════════════════════════════
// 【它为什么在**简单路径**上也必须存在】这是 M7c 收尾轮实测的最后一环：
//   修好 DPI（GetDeviceCaps 由 0 → 100）之后，`pixelsPerDip` 不再为 0，
//   `CheckFastPathNominalGlyphs` 终于返回 true，栈**从 FullTextLine 换成了 SimpleTextLine**
//   —— 但紧接着撞在这里：
//     at FormatSettings.FetchTextRun(...)   FormatSettings.cs:237
//       at SimpleRun.Create(...)            SimpleTextLine.cs:1398
//         at SimpleTextLine.Create(...)     SimpleTextLine.cs:135
//   原因是 `FetchTextRun` 要用 `TextStore.PwchParaSeparator`（**把 EOT run 变成 1 个转义字符**），
//   而那个静态字段由 `TextStore` 的**静态构造**填充，静态构造无条件调 `LoGetEscString`
//   （TextStore.cs:73-95）。⇒ 只要走到文本排版，无论简单还是复杂路径，它都必须成功。
//
// 【托管侧要的是什么】`EscStringInfo` 是 **6 个 IntPtr**（指向宽字符的指针），
//   不是 6 个字符 —— 托管把每个当"长度 1 的缓冲区"用：
//     new CharacterBufferRange((char*)TextStore.PwchParaSeparator, 1)
//   所以**必须交回有效指针**（给 NULL 下一次解引用就崩）。
//
// 【档位：合成 / 惰性 —— 必须说清】
//   这些是 **LineServices 内部保留的转义字符**（LS 用它们把"伪 run"编码进文本流：
//   段落分隔、行分隔、对象替换、对象终止、隐藏文本、不换行空格）。
//   本移植**没有 LineServices** ⇒ **无法给出 LS 的真实取值**，所以这里不假装知道：
//     · 取值落在 **Unicode 私用区 U+F000..U+F005**（真实 LS 的转义也取自私用区，
//       选这里的**语义**是"保留给内部使用、绝不与真实文本字符相等"）；
//     · 六个值**互不相同**（`TextStore` 会拿其中 3 个建不同类型的 `ControlRuns`，
//       相同值会让那三类无法区分）；
//     · 在**没有 LS 的移植里这些值是惰性的**：它们只会出现在 LS 路径构造的伪 run 上，
//       而本工程的 LS 路径（110 条 `Fs*`/`Lo*`/`Nl*` 缺口）根本没实现 ⇒
//       不会有任何真实文本与之比较。
//   将来若真的接了 shaping/LineServices，**这张表必须换成 LS 的真值**（登记为待办）。
static uint16_t g_esc[6] = {
    0xF000,  // szParaSeparator
    0xF001,  // szLineSeparator
    0xF002,  // szHidden
    0xF003,  // szNbsp
    0xF004,  // szObjectTerminator
    0xF005,  // szObjectReplacement
};

void LoGetEscString(WPF_ESCSTRING *esc)
{
    if (!esc) return;
    esc->szParaSeparator    = &g_esc[0];
    esc->szLineSeparator    = &g_esc[1];
    esc->szHidden           = &g_esc[2];
    esc->szNbsp             = &g_esc[3];
    esc->szObjectTerminator = &g_esc[4];
    esc->szObjectReplacement= &g_esc[5];
}

// 自检：六个指针必须非空、互不相同、且都落在私用区（PUA）。
int WpfLinuxWin32_EscStringSelfCheck(void)
{
    WPF_ESCSTRING e;
    LoGetEscString(&e);
    void *p[6] = { e.szParaSeparator, e.szLineSeparator, e.szHidden,
                   e.szNbsp, e.szObjectTerminator, e.szObjectReplacement };
    for (int i = 0; i < 6; i++) {
        if (!p[i]) return 0;
        uint16_t c = *(const uint16_t *)p[i];
        if (c < 0xE000 || c > 0xF8FF) return 0;         // 必须在私用区
        for (int j = 0; j < i; j++) if (p[i] == p[j]) return 0;   // 互不相同
    }
    return 1;
}

/* ⏪ `T-A47`／`TASK-0302` 增量 **dated 现值位**（读时 `2026-09-30T20:2x+0800`；**只增不改**）：
   本增量不动本文的"缺口/分类"口径（`tool`／`dead`／`artifact`／`ops`／`impl` 五位全未动；
   `so16 5b7d0ac101673900→1dbea9026dd7d3d7`）。它改的是**失败面**：缺省路径残留的 1 条
   `[HC-UNHANDLED]`（`ArgumentException: Specified Visual is already a child …`）⇒ `=0`。
   落点两处：① 托管生成件 `src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/PtsHelper.Linux.cs` 的
   `UpdateFloatingElementVisuals`（**浮层视觉换父**；生成器 `reapply-patches.py` 产出）；
   ② 本文同族的 `src/WpfGfx.Linux.Native/src/win32_pts.c`（`drive-handles-released(page-destroyed)`
   两处拒因**收窄**）。载体：`src/Linux/build/MilBridge/P1-tail2-hcres-impl-report.md`。 */
