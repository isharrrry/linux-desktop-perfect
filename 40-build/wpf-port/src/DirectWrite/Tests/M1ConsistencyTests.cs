// T2 · Phase 1 —— 与 M1（src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Text/）的一致性
// =====================================================================================
// 【这一组在防什么】
//   现在仓库里有两套"字体栈"：
//     · M1 的 src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Text/（FontSet / GlyphRunLayout / GlyphRunRequest）—— 绘制侧
//     · 本工程的 Provider —— 托管层（PresentationCore）要接的那一套
//   如果两边对同一个 (字体, 字符串) 给出**不同的字形 id 或不同的 advance**，
//   那么绘制出来的文字与布局算出来的位置就对不上（字距累积错位、甚至画错字）。
//   所以这一组逐项断言"两边同源"。
//
// 【怎么保证验证的是"M1 的真身"而不是拷贝】
//   Tests.csproj 用 <Compile Include="$(RepoRoot)src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Text/*.cs" Link="M1Link/...">
//   把那 4 个源文件**编进本程序集**：
//     · 不写 src/（只读，符合目录边界）；
//     · 编的是同一份源码 → src/ 一改，下次构建这里就跟着变，不会"验证的是旧拷贝"。
//   另外 M1LinkRevision 测试会把 4 个源文件的 SHA-256 打进测试输出，
//   这样报告里可以写明"验证的是哪一版"。
//
// 【FontSet 的接口限制】
//   M1 的 FontSet.TryResolve 只吐 SKTypeface，不吐文件路径。所以"选中的是同一份文件"
//   这一条要用**表字节的哈希**来证（不同字面的 glyf/head 内容不同）。
//
// 【两条匹配规则的差异是量化过的】
//   WPF 的 FontWeight 枚举值一共 10 个（100/200/300/400/500/600/700/800/900/950），
//   在这 10 × 3 种 style = 30 个组合上，我们的 Distance 规则与 M1 的粗体桶规则结论
//   **完全相同**（下面逐值断言）。差异只出现在 551..599 这 49 个**非枚举**整数上
//   （Distance 选 Bold、BoldBucket 选 Regular；分界点 550 恰好是 400/700 的中点）。
//   这条差异被显式断言，所以将来谁改了任一侧的规则都会立刻红。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sky = SkiaSharp;
using M1Text = WpfGfx.Linux.Text;
using Xunit;
using Xunit.Abstractions;

namespace MS.Internal.Text.TextInterface.Linux.Tests
{
    [Collection("Fonts")]
    public class M1ConsistencyTests
    {
        private readonly FontFixture _fixture;
        private readonly ITestOutputHelper _output;

        public M1ConsistencyTests(FontFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        /// <summary>WPF FontWeight 枚举的全部不同取值（含 950）。</summary>
        private static readonly int[] WpfWeights = { 100, 200, 300, 400, 500, 600, 700, 800, 900, 950 };

        private static Sky.SKFontStyleSlant SlantOf(int style) => style switch
        {
            1 => Sky.SKFontStyleSlant.Oblique,
            2 => Sky.SKFontStyleSlant.Italic,
            _ => Sky.SKFontStyleSlant.Upright,
        };

        [Fact]
        public void M1LinkRevision_IsReported()
        {
            // 把"验证的是哪一版 M1 源码"钉在测试输出里（报告要引用它）。
            foreach (string file in TestLayout.M1LinkedFiles)
            {
                string path = Path.Combine(TestLayout.M1TextDir, file);
                Assert.True(File.Exists(path), "M1 源文件缺失：" + path);
                _output.WriteLine($"{file}  sha256={TestLayout.Sha256File(path)}");
            }

            // 顺带证明"链接进来的确实是 M1 的类型"（不是我们另写了一份同名类）。
            Assert.Equal("WpfGfx.Linux.Text", typeof(M1Text.FontSet).Namespace);
            Assert.Equal("WpfGfx.Linux.Text", typeof(M1Text.GlyphRunLayout).Namespace);
            Assert.False(typeof(M1Text.FontSet).IsPublic, "M1 的类型应当是 internal（我们靠 Compile Link 才编得进来）");
        }

        [Fact]
        public void M1_FontSet_ResolvesSameFileForEveryStyle()
        {
            foreach (LinuxFont font in _fixture.AllFaces)
            {
                var description = new M1Text.TextFontDescription(
                    font.FamilyName, font.Weight, SlantOf(font.Style));

                Assert.True(_fixture.M1FontSet.TryResolve(description, out Sky.SKTypeface m1Face),
                    $"M1 FontSet 解析不到 {description}");

                _fixture.WithFace(font, face =>
                {
                    // 身份一致
                    Assert.Equal(face.Typeface.FamilyName, m1Face.FamilyName);
                    Assert.Equal(face.Typeface.UnitsPerEm, m1Face.UnitsPerEm);
                    Assert.Equal(face.Typeface.GlyphCount, m1Face.GlyphCount);
                    Assert.Equal(face.Typeface.IsBold, m1Face.IsBold);
                    Assert.Equal(face.Typeface.IsItalic, m1Face.IsItalic);

                    // "同一个文件"用表字节哈希证明（M1 不吐路径）。
                    Assert.True(m1Face.TryGetTableData(TableTags.Head, out byte[] m1Head));
                    Assert.True(face.TryGetFontTable(TableTags.Head, out byte[] myHead));
                    Assert.Equal(Probe.ProbeDigest.Sha256(m1Head), Probe.ProbeDigest.Sha256(myHead));

                    return 0;
                });
            }
        }

        [Fact]
        public void M1_MatchingRule_AgreesOnAllWpfWeights()
        {
            LinuxFontFamily family = _fixture.Collection["Noto Sans"];
            int agreed = 0;

            foreach (int weight in WpfWeights)
            {
                foreach (int style in new[] { 0, 1, 2 })
                {
                    // M1 的口径：TextFontDescription(家族名, 字重, 斜体)
                    var description = new M1Text.TextFontDescription(family.FamilyName, weight, SlantOf(style));
                    Assert.True(_fixture.M1FontSet.TryResolve(description, out Sky.SKTypeface m1Face));

                    LinuxFont mine = family.GetFirstMatchingFont(weight, 5, style, FontMatchingRule.Distance);

                    // M1 只吐 typeface → 用 IsBold/IsItalic + 字形数 判定"是不是同一个字面"
                    Assert.Equal(m1Face.IsBold, mine.Weight >= 600);
                    Assert.Equal(m1Face.IsItalic, mine.Style != 0);

                    // 更强的判据：表字节哈希（同族里 Regular/Italic 的字形数不同，
                    // 但 head 表都相同 —— 用 hmtx 的哈希才是文件级指纹）。
                    Assert.True(m1Face.TryGetTableData(TableTags.Hmtx, out byte[] m1Hmtx));
                    _fixture.WithFace(mine, face =>
                    {
                        Assert.True(face.TryGetFontTable(TableTags.Hmtx, out byte[] myHmtx));
                        Assert.Equal(Probe.ProbeDigest.Sha256(m1Hmtx), Probe.ProbeDigest.Sha256(myHmtx));
                        return 0;
                    });

                    agreed++;
                }
            }

            Assert.Equal(30, agreed);
            _output.WriteLine($"Distance 规则与 M1 粗体桶规则在 {agreed}/30 个 (字重 × style) 组合上结论相同");
        }

        [Fact]
        public void BoldBucketRule_AgreesWithM1OnAllIntegerWeights()
        {
            // 用 BoldBucket 规则时，应当对 1..1000 的**每一个**整数都与 M1 一致 ——
            // 这才是"与 M1 逐值一致"的完整证明（不止枚举值）。
            LinuxFontFamily family = _fixture.Collection["Noto Sans"];

            for (int weight = 1; weight <= 1000; weight++)
            {
                var description = new M1Text.TextFontDescription(family.FamilyName, weight, Sky.SKFontStyleSlant.Upright);
                Assert.True(_fixture.M1FontSet.TryResolve(description, out Sky.SKTypeface m1Face));

                LinuxFont mine = family.GetFirstMatchingFont(weight, 5, 0, FontMatchingRule.BoldBucket);
                Assert.Equal(m1Face.IsBold, mine.Weight >= 600);
            }
        }

        [Fact]
        public void DistanceRule_DivergesFromM1OnlyInDocumentedBand()
        {
            // 差异集合必须**恰好**是 551..599：这是把"两条规则不同"这件事量化的断言。
            LinuxFontFamily family = _fixture.Collection["Noto Sans"];
            var divergent = new List<int>();

            for (int weight = 1; weight <= 1000; weight++)
            {
                var description = new M1Text.TextFontDescription(family.FamilyName, weight, Sky.SKFontStyleSlant.Upright);
                Assert.True(_fixture.M1FontSet.TryResolve(description, out Sky.SKTypeface m1Face));

                LinuxFont mine = family.GetFirstMatchingFont(weight, 5, 0, FontMatchingRule.Distance);
                bool mineBold = mine.Weight >= 600;
                if (mineBold != m1Face.IsBold) divergent.Add(weight);
            }

            Assert.Equal(49, divergent.Count);
            Assert.Equal(551, divergent.First());
            Assert.Equal(599, divergent.Last());
            _output.WriteLine($"已登记的差异区间：{divergent.First()}..{divergent.Last()}（{divergent.Count} 个非枚举值）");
        }

        [Fact]
        public void M1_GlyphIndices_MatchOurMapping()
        {
            var corpus = new[]
            {
                "Hello WPF on Linux", "AVATAR To Wa", "iiiiiiiiii WWWWWWWWWW",
                "\u00e9\u00e8\u00ea\u00eb", "\u03b1\u03b2\u03b3\u03b4", "\u041f\u0440\u0438\u0432\u0435\u0442",
                "\U00010780\U00010781", "A\U0001F600x", "e\u0301",
                "The quick brown fox jumps over the lazy dog 0123456789",
            };

            var description = new M1Text.TextFontDescription(
                "Noto Sans", 400, Sky.SKFontStyleSlant.Upright);
            Assert.True(_fixture.M1FontSet.TryResolve(description, out Sky.SKTypeface m1Face));

            foreach (string text in corpus)
            {
                // M1 的入口：GlyphRunRequest.FromText（内部就是 typeface.GetGlyphs(text)）
                M1Text.GlyphRunRequest request = M1Text.GlyphRunRequest.FromText(
                    text, m1Face, description, 12f, Sky.SKPoint.Empty);

                ShapedGlyphs mine = GlyphMapper.MapString(_fixture.RegularFace, text);

                Assert.Equal(request.GlyphIndices, mine.GlyphIndices);
            }
        }

        [Fact]
        public void M1_FontSet_DoesNotResolveUnpackagedFamily_LikeOurs()
        {
            // 两边都必须"查不到就是查不到"，绝不回落系统字体 ——
            // 这是 golden 图可复现的前提（M1 FontSet.cs:1-19 的裁决，我们照抄）。
            Assert.False(_fixture.M1FontSet.TryResolve(
                new M1Text.TextFontDescription("Definitely Not A Packaged Family", 400, Sky.SKFontStyleSlant.Upright),
                out _));

            Assert.Null(_fixture.Collection["Definitely Not A Packaged Family"]);
            Assert.False(_fixture.Collection.FindFamilyName("Definitely Not A Packaged Family", out _));
        }

        [Fact]
        public void M1_And_Provider_SeeTheSameFourFiles()
        {
            Assert.Equal(4, _fixture.M1FontSet.FileNames.Count);
            Assert.Equal(4, _fixture.Collection.FileNames.Count);

            Assert.Equal(
                _fixture.M1FontSet.FileNames.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
                _fixture.Collection.FileNames.OrderBy(n => n, StringComparer.Ordinal).ToArray());

            Assert.Empty(_fixture.Collection.LoadWarnings);
            Assert.False(_fixture.M1FontSet.IsEmpty);
        }

        /// <summary>
        /// M1 的排版内核 GlyphRunLayout.Measure 接受我们算出来的 advance，
        /// 并给出与我们一致的总长与位置。
        /// 这是"MIL 按托管层给的度量摆放字形"这条链路的等价验证：
        /// GlyphRunRequest 的 AdvanceWidths 就是 WPF 传下来的那份。
        /// </summary>
        [Fact]
        public void M1_GlyphRunLayout_AcceptsOurAdvances()
        {
            var description = new M1Text.TextFontDescription("Noto Sans", 400, Sky.SKFontStyleSlant.Upright);
            Assert.True(_fixture.M1FontSet.TryResolve(description, out Sky.SKTypeface m1Face));

            const string text = "Hello WPF on Linux";
            const float size = 16f;
            var origin = new Sky.SKPoint(10f, 40f);

            ShapedGlyphs shaped = GlyphMapper.MapString(_fixture.RegularFace, text);

            // 我们的 advance：上游口径是 DIP × scalingFactor(300)，而 M1/MIL 用的是**像素**，
            // 所以这里按 scalingFactor=1（即 DIP）取，供绘制侧直接使用。
            GlyphPlacementData placement = GlyphPositioner.Place(
                _fixture.RegularFace, shaped.GlyphIndices, size, scalingFactor: 1.0,
                isSideways: false, useDisplayNatural: true);

            var request = new M1Text.GlyphRunRequest
            {
                Font = description,
                FontSize = size,
                BaselineOrigin = origin,
                GlyphIndices = shaped.GlyphIndices,
                AdvanceWidths = placement.Advances.Select(a => (float)a).ToArray(),
            };

            using var paint = new Sky.SKPaint { IsAntialias = true };
            using var font = new Sky.SKFont(m1Face, size) { LinearMetrics = true, Hinting = Sky.SKFontHinting.None };

            M1Text.GlyphRunMetrics metrics = M1Text.GlyphRunLayout.Measure(request, font, paint);

            // 1) 没有字形被当成越界（说明我们给的 id 对这份字体是合法的）
            Assert.Equal(0, metrics.OutOfRangeGlyphs);

            // 2) 总长 == 我们算的 advance 之和
            Assert.Equal(placement.Advances.Sum(), (int)metrics.TotalAdvance);

            // 3) 位置就是"基线起点 + 前缀和"（M1 的公式），我们按同一公式核对一遍
            float cursor = origin.X;
            for (int i = 0; i < shaped.GlyphIndices.Length; i++)
            {
                Assert.Equal(cursor, metrics.Positions[i].X, 4);
                Assert.Equal(origin.Y, metrics.Positions[i].Y, 4);
                cursor += placement.Advances[i];
            }

            Assert.Equal(cursor, origin.X + metrics.TotalAdvance, 4);
        }

        /// <summary>
        /// M1 在没有显式 advance 时会自己问字体（SKFont.GetGlyphWidths）。
        /// 那条兜底路径算出来的宽度必须等于我们的设计步进 × size/upem ——
        /// 否则"MIL 自己兜底"与"托管层给度量"会画出两种字距。
        /// </summary>
        [Fact]
        public void M1_FontDerivedAdvances_MatchOurDesignAdvances()
        {
            var description = new M1Text.TextFontDescription("Noto Sans", 400, Sky.SKFontStyleSlant.Upright);
            Assert.True(_fixture.M1FontSet.TryResolve(description, out Sky.SKTypeface m1Face));

            const string text = "AVATAR To Wa";
            const float size = 13.333f;

            M1Text.GlyphRunRequest request = M1Text.GlyphRunRequest.FromText(
                text, m1Face, description, size, Sky.SKPoint.Empty);
            // AdvanceWidths 留 null → M1 走字体度量兜底（GlyphRunLayout.cs:117-122）

            using var paint = new Sky.SKPaint();
            using var font = new Sky.SKFont(m1Face, size) { LinearMetrics = true, Hinting = Sky.SKFontHinting.None };
            M1Text.GlyphRunMetrics m1Metrics = M1Text.GlyphRunLayout.Measure(request, font, paint);

            ShapedGlyphs shaped = GlyphMapper.MapString(_fixture.RegularFace, text);
            Assert.Equal(request.GlyphIndices, shaped.GlyphIndices);

            for (int i = 0; i < shaped.GlyphIndices.Length; i++)
            {
                double expected = _fixture.RegularFace.DesignAdvance(shaped.GlyphIndices[i]) * size / 1000.0;

                // SKFont 的宽度是 float 且带定点误差；容差 0.05 px 足够区分"算错了"与"精度差"。
                Assert.True(Math.Abs(m1Metrics.Advances[i] - expected) < 0.05,
                    $"字形 {i}: M1={m1Metrics.Advances[i]} 我们的设计步进换算={expected}");
            }
        }

        /// <summary>
        /// 度量口径一致：M1 绘制时把基线放在 ascent 之下；我们的 FontMetrics 给出的
        /// ascent 必须能支撑同样的排版（否则两套栈的行高不同）。
        /// M1 的 TextRenderer 不暴露度量，所以这里用 M1 实际使用的 SKFont 度量作对照。
        /// </summary>
        [Fact]
        public void OurMetrics_MatchTheSkiaMetricsM1UsesForRendering()
        {
            var description = new M1Text.TextFontDescription("Noto Sans", 400, Sky.SKFontStyleSlant.Upright);
            Assert.True(_fixture.M1FontSet.TryResolve(description, out Sky.SKTypeface m1Face));

            const float size = 24f;
            using var m1Font = new Sky.SKFont(m1Face, size);   // M1 TextRenderer 就是这么建的
            Sky.SKFontMetrics skia = m1Font.Metrics;

            FontMetricsData mine = _fixture.RegularFace.Metrics;
            double scale = size / mine.DesignUnitsPerEm;

            Assert.Equal(mine.Ascent * scale, -skia.Ascent, 3);
            Assert.Equal(mine.Descent * scale, skia.Descent, 3);
            Assert.Equal(mine.LineGap * scale, skia.Leading, 3);

            // 行高（Baseline/LineSpacing 的公式产物）也必须一致
            Assert.Equal(mine.LineSpacing * size, (-skia.Ascent + skia.Descent + skia.Leading), 3);
        }
    }
}
