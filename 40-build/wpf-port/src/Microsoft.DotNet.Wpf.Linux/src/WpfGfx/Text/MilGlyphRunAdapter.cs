// MilGlyphRun（Resources/ 里的 MIL 资源对象）→ GlyphRunRequest 的适配。
//
// 【一个必须说清楚的坑：MilGlyphRun 不带字体身份】
//   上游 MILCMD_GLYPHRUN_CREATE 里只有 `ulong PIDWriteFont` —— 一个指向 Windows
//   DirectWrite 字体对象的**裸指针**。在 Linux 上这个值没有任何意义，解引用就是
//   段错误。而字形 id 本身又是**字体相关**的：同一段文字在两份不同字体里的
//   glyph id 完全不同。
//
//   所以这里定的对接契约是：
//     · 渲染器构造期绑定一个「默认字体」（本轮是打包的 Noto Sans）
//     · 提供 TextRenderer.MilFontResolver 钩子，让将来接进来的托管层（T9）能按
//       自己的 Typeface 映射把 PIDWriteFont 翻译成 TextFontDescription
//     · 在 T9 接进来之前，所有 MilGlyphRun 都用默认字体渲染
//   这不是"以后再说"，而是**M1 阶段唯一可行的做法**：FontFamily 的解析在托管层
//   （PresentationCore 的 Typeface / FontFamily），不在 MIL 协议里。
//
// 【flags 的取值来自上游】
//   wgx_core_types.cs:339 的 MilGlyphRun 枚举：Sideways = 0x1，HasOffsets = 0x10。

using System;
using SkiaSharp;
using WpfGfx.Linux.Resources;

namespace WpfGfx.Linux.Text
{
    internal static class MilGlyphRunAdapter
    {
        /// <summary>MilGlyphRun 枚举：竖排。</summary>
        public const ushort FlagSideways = 0x0001;

        /// <summary>MilGlyphRun 枚举：带逐字形偏移数组。</summary>
        public const ushort FlagHasOffsets = 0x0010;

        public static GlyphRunRequest ToRequest(
            MilGlyphRun run, TextFontDescription font, float fallbackSize)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));

            // MuSize 是 WPF 的渲染 em size。解码没解出来时是 0，退回渲染器配的兜底字号，
            // 否则会画出一条高度为 0 的线——golden 图里根本看不出来。
            float size = run.MuSize > 0f ? run.MuSize : fallbackSize;

            var request = new GlyphRunRequest
            {
                Font = font,
                FontSize = size,
                BaselineOrigin = new SKPoint(run.Origin.X, run.Origin.Y),
                Sideways = (run.Flags & FlagSideways) != 0,
                GlyphIndices = run.GlyphIndices ?? Array.Empty<ushort>(),
            };

            // 显式 advance：Commands/ 解出来就有，没解出来就是空数组。
            if (run.AdvanceWidths != null && run.AdvanceWidths.Length > 0)
                request.AdvanceWidths = run.AdvanceWidths;

            // 偏移数组只在 HasOffsets 置位时有效——没置位时那块内存是未定义内容。
            if ((run.Flags & FlagHasOffsets) != 0 &&
                run.GlyphOffsets != null && run.GlyphOffsets.Length > 0)
            {
                var offsets = new SKPoint[run.GlyphOffsets.Length];
                for (int i = 0; i < offsets.Length; i++)
                    offsets[i] = new SKPoint((float)run.GlyphOffsets[i].X, (float)run.GlyphOffsets[i].Y);
                request.GlyphOffsets = offsets;
            }

            return request;
        }
    }
}
