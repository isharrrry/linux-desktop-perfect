// T2 · Phase 1 —— 共享装置
// =====================================================================================
// 【为什么用 collection fixture 而不是每个类各加载一次】
//   加载 4 份字体 + 解析 13 张表 + 读 3884 个字形，一次约几十毫秒。虽然不贵，
//   但"跨进程哈希一致"这类断言的语义是"从零加载两次也一致"，
//   所以确定性测试**刻意不复用**这个装置（自己新建两套），而其余测试复用它。
//
// 【测试用的字体族与字面】
//   全部来自 src/Linux/build/fonts（SHA256SUMS 锁定），族名 "Noto Sans"。

using System;
using System.Collections.Generic;
using System.Linq;
using Sky = SkiaSharp;
using M1Text = WpfGfx.Linux.Text;
using Xunit;

namespace MS.Internal.Text.TextInterface.Linux.Tests
{
    /// <summary>共享的字体集合 / M1 对照集合。</summary>
    public sealed class FontFixture : IDisposable
    {
        public FontFixture()
        {
            // T1/M7c5：本装置是**原始语料**的对照组 —— 它的全部断言都在比对
            // "provider 拿到的表 vs 磁盘上的原始文件字节"（M1 一致性、Skia/原生表字节对拍…）。
            // 因此显式旁路运行期 GSUB/GPOS 剥离（stripLayout:false），保持它们的语义不变。
            // 剥离后的行为由 FontLayoutStrippingTests 单独取证（它自建集合，不复用本装置）。
            Collection = LinuxFontCollection.FromDirectory(TestLayout.FontDir, recurse: false, stripLayout: false);

            // M1 的对照集合：**同一个目录**，保证两边面对的是同一批文件。
            M1FontSet = M1Text.FontSet.FromDirectory(TestLayout.FontDir);

            Regular = Collection["Noto Sans"]?.GetFirstMatchingFont(400, 5, 0, FontMatchingRule.Distance);
            Bold = Collection["Noto Sans"]?.GetFirstMatchingFont(700, 5, 0, FontMatchingRule.Distance);
            Italic = Collection["Noto Sans"]?.GetFirstMatchingFont(400, 5, 2, FontMatchingRule.Distance);
            BoldItalic = Collection["Noto Sans"]?.GetFirstMatchingFont(700, 5, 2, FontMatchingRule.Distance);

            RegularFace = Regular?.GetFontFace();
        }

        public LinuxFontCollection Collection { get; }

        /// <summary>
        /// M1 的 FontSet 是 **internal**（只对 WpfGfx.Linux.Windowing.Tests 开友元），
        /// 我们靠 csproj 的 Compile Link 把它编进本程序集才拿得到 —— 因此这里的
        /// 属性也必须是 internal（public 属性不能暴露 internal 类型）。
        /// </summary>
        internal M1Text.FontSet M1FontSet { get; }

        public LinuxFont Regular { get; }
        public LinuxFont Bold { get; }
        public LinuxFont Italic { get; }
        public LinuxFont BoldItalic { get; }

        public LinuxFontFace RegularFace { get; }

        public IEnumerable<LinuxFont> AllFaces
        {
            get
            {
                yield return Regular;
                yield return Bold;
                yield return Italic;
                yield return BoldItalic;
            }
        }

        /// <summary>打开一个字体面并保证用完 Release（与上游调用方的 finally 同构）。</summary>
        public T WithFace<T>(LinuxFont font, Func<LinuxFontFace, T> body)
        {
            LinuxFontFace face = font.GetFontFace();
            try
            {
                return body(face);
            }
            finally
            {
                face.Release();
            }
        }

        public void Dispose()
        {
            RegularFace?.Release();
            Collection?.Dispose();
            M1FontSet?.Dispose();
        }
    }

    [CollectionDefinition("Fonts")]
    public class FontCollectionDefinition : ICollectionFixture<FontFixture>
    {
    }
}
