// 字体描述：家族名 + 字重 + 倾斜。
//
// 【为什么不用 SkiaSharp 的 SKFontStyle】
//   SKFontStyle 是 Skia 的类型，而 GlyphRun 的字体身份来自 WPF 的字体解析结果
//   （Typeface.FamilyName / FontWeight / FontStyle）。中间隔一层我们自己的结构，
//   Text/ 就不会被绑死在某一个 Skia 版本上——SkiaSharp 3.x 把 SKFontStyle 的
//   构造签名改过，4.x 又动过一次，直接透传等于把升级风险引进来。
//
// 【字只用 build/fonts 里打包的那几份】
//   FontSet 只从指定目录加载，找不到就返回 false，**绝不回落系统字体**。
//   这是 handoff §6「确定性保证」第 1 条：golden 图一旦允许系统字体参与，
//   容器里 /usr/share/fonts 的有无就会改变字形轮廓，测试必然随机飘。

using System;
using SkiaSharp;

namespace WpfGfx.Linux.Text
{
    /// <summary>一次文本绘制请求里的字体身份。</summary>
    internal readonly struct TextFontDescription : IEquatable<TextFontDescription>
    {
        public const int NormalWeight = 400;
        public const int BoldWeight = 700;

        public TextFontDescription(string familyName, int weight, SKFontStyleSlant slant)
        {
            FamilyName = familyName ?? string.Empty;
            Weight = weight;
            Slant = slant;
        }

        public string FamilyName { get; }

        /// <summary>WPF 的 FontWeight 语义：400=Regular，700=Bold。</summary>
        public int Weight { get; }

        public SKFontStyleSlant Slant { get; }

        public bool IsBold => Weight >= 600;

        public bool IsItalic => Slant != SKFontStyleSlant.Upright;

        public static TextFontDescription Regular(string family) =>
            new TextFontDescription(family, NormalWeight, SKFontStyleSlant.Upright);

        public static TextFontDescription Bold(string family) =>
            new TextFontDescription(family, BoldWeight, SKFontStyleSlant.Upright);

        public static TextFontDescription Italic(string family) =>
            new TextFontDescription(family, NormalWeight, SKFontStyleSlant.Italic);

        public static TextFontDescription BoldItalic(string family) =>
            new TextFontDescription(family, BoldWeight, SKFontStyleSlant.Italic);

        public bool Equals(TextFontDescription other) =>
            string.Equals(FamilyName, other.FamilyName, StringComparison.OrdinalIgnoreCase)
            && Weight == other.Weight
            && Slant == other.Slant;

        public override bool Equals(object obj) => obj is TextFontDescription d && Equals(d);

        public override int GetHashCode() => HashCode.Combine(
            FamilyName == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(FamilyName),
            Weight, Slant);

        public override string ToString() => $"{FamilyName} {Weight} {Slant}";
    }
}
