// T2 · Phase 1 —— provider 的形态与生命周期（接线前必须先钉死的那些事）
// =====================================================================================
// 【为什么生命周期也要测】
//   上游 PresentationCore 在 7 个 finally 里对 FontFace 调 Release
//   （GlyphTypeface.cs:93,967,1079,1103,1275,1562,1703），而 Font::GetFontFace()
//   每次都返回 **+1 引用**的面（Font.cpp 的缓存命中 → AddRef）。
//   如果我们的 Release 语义不对，结果不是"泄漏一点内存"，而是
//   **use-after-free**：某处 Release 之后另一处还在用同一个面。
//   在托管侧它的表现是 ObjectDisposedException 或者"偶尔画出 .notdef"，
//   属于最难查的一类问题。所以这里逐条断言引用计数语义。
//
// 【句柄表为什么也要测】
//   GlyphTypeface.cs:1264 把 DWriteFontFaceAddRef 的 IntPtr 交给 MIL
//   （MilGlyphRun_GetGlyphOutline），MIL 侧要能反查回 SKTypeface。
//   所以令牌必须：同一面稳定不变、释放后不再可解析、能安装外部分配钩子。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sky = SkiaSharp;
using Xunit;

namespace MS.Internal.Text.TextInterface.Linux.Tests
{
    [Collection("Fonts")]
    public class ProviderShapeTests
    {
        private readonly FontFixture _fixture;

        public ProviderShapeTests(FontFixture fixture) => _fixture = fixture;

        [Fact]
        public void Collection_LoadsExactlyThePackagedFonts()
        {
            Assert.Equal(1, _fixture.Collection.FamilyCount);          // 只有 "Noto Sans" 一个族
            Assert.Equal(4, _fixture.Collection.Entries.Count);
            Assert.Equal(4, _fixture.Collection.TypefaceCount);
            Assert.Equal(4, _fixture.Collection.FileNames.Count);
            Assert.Empty(_fixture.Collection.LoadWarnings);

            Assert.Equal(new[] { "NotoSans-Bold.ttf", "NotoSans-BoldItalic.ttf", "NotoSans-Italic.ttf", "NotoSans-Regular.ttf" },
                _fixture.Collection.FileNames.OrderBy(n => n, StringComparer.Ordinal).ToArray());

            // 枚举顺序是排序后的（不是文件系统顺序）—— 这是确定性的组成部分。
            var expectedOrder = _fixture.Collection.FileNames.OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(expectedOrder, _fixture.Collection.FileNames.ToArray());
        }

        [Fact]
        public void EmptyDirectory_YieldsEmptyCollection_AndResolvesNothing()
        {
            string empty = Path.Combine(Path.GetTempPath(), "dw-empty-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(empty);
            try
            {
                using var collection = LinuxFontCollection.FromDirectory(empty);
                Assert.Equal(0, collection.FamilyCount);
                Assert.Null(collection["Noto Sans"]);
                Assert.False(collection.FindFamilyName("Noto Sans", out _));
            }
            finally
            {
                Directory.Delete(empty, recursive: true);
            }
        }

        [Fact]
        public void MissingDirectory_DoesNotThrow()
        {
            using var collection = LinuxFontCollection.FromDirectory("/nonexistent/fonts/dir");
            Assert.Equal(0, collection.FamilyCount);
        }

        [Fact]
        public void CorruptFile_IsSkippedAndRecorded()
        {
            string dir = Path.Combine(Path.GetTempPath(), "dw-corrupt-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "broken.ttf"), "不是字体文件");
                File.Copy(TestLayout.FontPath(TestLayout.RegularFile), Path.Combine(dir, "good.ttf"));

                using var collection = LinuxFontCollection.FromDirectory(dir);

                // 好文件照常索引
                Assert.Equal(1, collection.FamilyCount);
                Assert.Equal(1, collection.Entries.Count);

                // 坏文件**被记录**（不是静默丢弃）
                Assert.Single(collection.LoadWarnings);
                Assert.Contains("broken.ttf", collection.LoadWarnings[0]);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void GetFontFace_ReturnsPlusOneReferencePerCall()
        {
            LinuxFont font = _fixture.Regular;

            LinuxFontFace first = font.GetFontFace();      // +1
            int after1 = first.RefCount;
            LinuxFontFace second = font.GetFontFace();     // +1
            int after2 = first.RefCount;

            // 上游 Font.cpp:GetFontFace 的语义：同一个对象、每次调用 +1 引用。
            Assert.Same(first, second);
            Assert.Equal(after1 + 1, after2);

            first.Release();
            Assert.Equal(after1, second.RefCount);

            // 关键：Release 之后对象**仍然可用**（上游的缓存持有它）——
            // 这不是"泄漏"，而是上游语义；真销毁要等集合 Dispose。
            Assert.Equal(1000, second.Metrics.DesignUnitsPerEm);
            Assert.Equal(741, second.DesignAdvance(second.GlyphForCodePoint('H')));

            second.Release();
            Assert.Equal(after1 - 1, second.RefCount);
        }

        [Fact]
        public void Release_NeverGoesNegative()
        {
            LinuxFont font = _fixture.Italic;

            LinuxFontFace face = font.GetFontFace();
            for (int i = 0; i < 5; i++) face.Release();     // 多余的 Release

            Assert.Equal(0, face.RefCount);

            // 归零后依然可用（对象生命周期归 Font/集合，不归引用计数）
            Assert.True(face.GlyphCount > 0);
            Assert.Equal(face.GlyphCount, font.GetFontFace().GlyphCount);
        }

        [Fact]
        public void GetFontFromFontFace_RoundTrips()
        {
            _fixture.WithFace(_fixture.Bold, face =>
            {
                LinuxFont font = _fixture.Collection.GetFontFromFontFace(face);
                Assert.NotNull(font);
                Assert.Equal(700, font.Weight);
                Assert.Equal(0, font.Style);
                return 0;
            });

            // 外来的面（不在集合里）→ null（不猜）
            using var other = LinuxFontFace.FromFile(TestLayout.FontPath(TestLayout.RegularFile));
            Assert.Null(_fixture.Collection.GetFontFromFontFace(other));
        }

        [Fact]
        public void FontList_BehavesLikeUpstream()
        {
            LinuxFontFamily family = _fixture.Collection["Noto Sans"];

            Assert.Equal(4, family.Count);
            Assert.True(family[0].Weight <= family[1].Weight);      // 按 (weight, width, slant) 排序

            var enumerated = new List<LinuxFont>();
            foreach (LinuxFont font in family) enumerated.Add(font);
            Assert.Equal(4, enumerated.Count);

            Assert.Same(_fixture.Collection, family.FontsCollection);

            // GetMatchingFonts：按匹配度排序（最匹配的在最前）
            LinuxFontList matching = family.GetMatchingFonts(700, 5, 2);
            Assert.Equal(4, matching.Count);
            Assert.Equal(700, matching[0].Weight);
            Assert.Equal(2, matching[0].Style);

            Assert.Throws<ArgumentOutOfRangeException>(() => family[99]);
        }

        [Fact]
        public void FamilyLookup_IsCaseInsensitive_AndMissReturnsNull()
        {
            Assert.NotNull(_fixture.Collection["Noto Sans"]);
            Assert.NotNull(_fixture.Collection["noto sans"]);
            Assert.Null(_fixture.Collection["Arial"]);

            Assert.True(_fixture.Collection.FindFamilyName("NOTO SANS", out int index));
            Assert.Equal(0, index);
        }

        [Fact]
        public void HandleTable_TokenIsStableAndResolvable()
        {
            FontHandleTable.Reset();
            try
            {
                _fixture.WithFace(_fixture.Regular, face =>
                {
                    IntPtr token = face.Token;
                    Assert.NotEqual(IntPtr.Zero, token);

                    // 同一个面反复取 → 同一个令牌（MIL 侧可以重复反查）
                    Assert.Equal(token, face.Token);

                    Assert.True(FontHandleTable.TryResolve(token, out Sky.SKTypeface typeface));
                    Assert.Same(face.Typeface, typeface);

                    Assert.True(FontHandleTable.TryResolveFace(token, out LinuxFontFace resolved));
                    Assert.Same(face, resolved);

                    return 0;
                });
            }
            finally
            {
                FontHandleTable.Reset();
            }
        }

        [Fact]
        public void HandleTable_UnregisteredToken_DoesNotResolveAndDoesNotFallBack()
        {
            FontHandleTable.Reset();
            try
            {
                Assert.False(FontHandleTable.TryResolve(new IntPtr(12345), out Sky.SKTypeface none));
                Assert.Null(none);
                Assert.False(FontHandleTable.TryResolve(IntPtr.Zero, out _));
                Assert.False(FontHandleTable.Unregister(new IntPtr(12345)));
            }
            finally
            {
                FontHandleTable.Reset();
            }
        }

        [Fact]
        public void HandleTable_TokenAllocatorHook_IsUsedWhenInstalled()
        {
            // 这就是接线用的机制：PresentationCore 装上 MilFontFaceTable.Register 之后，
            // 骨架发出的令牌与 MIL 能解析的就是同一个（见 WIRING.md §3）。
            FontHandleTable.Reset();
            var allocated = new List<Sky.SKTypeface>();
            try
            {
                FontHandleTable.TokenAllocator = typeface =>
                {
                    allocated.Add(typeface);
                    return new IntPtr(0x5A5A);      // 假装这是 MIL 发的句柄
                };

                _fixture.WithFace(_fixture.Bold, face =>
                {
                    Assert.Equal(new IntPtr(0x5A5A), face.Token);
                    Assert.Single(allocated);
                    Assert.Same(face.Typeface, allocated[0]);
                    return 0;
                });
            }
            finally
            {
                FontHandleTable.TokenAllocator = null;
                FontHandleTable.Reset();
            }
        }

        [Fact]
        public void Font_DWriteFontAddRef_ProducesResolvableToken()
        {
            FontHandleTable.Reset();
            try
            {
                IntPtr token = _fixture.Regular.DWriteFontAddRef;
                Assert.NotEqual(IntPtr.Zero, token);
                Assert.True(FontHandleTable.TryResolve(token, out Sky.SKTypeface typeface));
                Assert.Same(_fixture.Regular.Typeface, typeface);

                // GlyphRun.cs:1876 会把它塞进 pIDWriteFont；MIL 侧（M7a）用它查表画轮廓。
                // 这里直接验证那条链路的前提：令牌能解回 SKTypeface，且该 typeface
                // 能给出真轮廓（MilGlyphRun_GetGlyphOutline 的第一步）。
                using var font = new Sky.SKFont(typeface, 24);
                using Sky.SKPath path = font.GetGlyphPath(typeface.GetGlyph('A'));
                Assert.NotNull(path);
                Assert.True(path.PointCount > 0, "从令牌反查到的 typeface 画不出 'A' 的轮廓");
            }
            finally
            {
                FontHandleTable.Reset();
            }
        }

        [Fact]
        public void SimulationFlags_AreForwardedToSkiaFont()
        {
            using var plain = LinuxFontFace.FromFile(TestLayout.FontPath(TestLayout.RegularFile));
            using var simulated = LinuxFontFace.FromFile(
                TestLayout.FontPath(TestLayout.RegularFile), 0,
                LinuxFontFace.SimulationBold | LinuxFontFace.SimulationOblique);

            Assert.Equal(LinuxFontFace.SimulationBold | LinuxFontFace.SimulationOblique, simulated.SimulationFlags);

            using Sky.SKFont plainFont = plain.CreateFont(24);
            using Sky.SKFont simulatedFont = simulated.CreateFont(24);

            // 合成标志确实落到了 SKFont 上（Embolden/SkewX 可读可写）。
            Assert.False(plainFont.Embolden);
            Assert.True(simulatedFont.Embolden);
            Assert.False(plainFont.SkewX != 0f);
            Assert.Equal(-0.25f, simulatedFont.SkewX, 6);

            // **但度量口径不变**：真 DWrite 的模拟粗体会让 advance 略微变宽，
            // Skia 的 Embolden 只在绘制时描边加粗，不动 hmtx。
            // 这是我们与 Windows 的一处已知差异，已登记为降级条目（REPORT.md 降级清单 #5）。
            Assert.Equal(plain.DesignAdvance(plain.GlyphForCodePoint('H')),
                         simulated.DesignAdvance(simulated.GlyphForCodePoint('H')));
        }

        [Fact]
        public void FromBytes_LoadsFontWithoutAFile()
        {
            byte[] data = File.ReadAllBytes(TestLayout.FontPath(TestLayout.RegularFile));

            using LinuxFontFace face = LinuxFontFace.FromBytes(data, 0, 0, "embedded:test");
            Assert.Equal(1000, face.UnitsPerEm);
            Assert.Equal(3884, face.GlyphCount);
            Assert.Equal(1069, face.Metrics.Ascent);

            // 字节流没有本地路径：SourcePath 为 null，GetUriPath() 返回空串，
            // 诊断标签单独放在 Tag 里 —— 不拿标签冒充路径。
            Assert.Null(face.SourcePath);
            Assert.Equal("embedded:test", face.Tag);
            Assert.Equal(string.Empty, face.GetFileZero().GetUriPath());
        }

        [Fact]
        public void FromBytes_InvalidData_ThrowsInsteadOfReturningFakeFace()
        {
            Assert.ThrowsAny<Exception>(() => LinuxFontFace.FromBytes(new byte[] { 1, 2, 3, 4 }));
        }

        [Fact]
        public void FromFile_MissingFile_Throws()
        {
            Assert.Throws<FileNotFoundException>(() => LinuxFontFace.FromFile("/nonexistent/font.ttf"));
            Assert.Throws<ArgumentNullException>(() => LinuxFontFace.FromFile(null));
        }

        [Fact]
        public void Typeface_ReportsFontFaceKind()
        {
            // 骨架的 FontFace.Type 走 DWriteTypeConverter.Convert(DWRITE_FONT_FACE_TYPE)。
            // 我们的判据是 SFNT 版本标签 + CFF 表（Noto Sans 是 TrueType 轮廓）。
            _fixture.WithFace(_fixture.Regular, face =>
            {
                Assert.Equal(FontFaceKind.TrueType, face.Type);
                Assert.Equal(TableTags.SfntTrueType, face.OpenType.SfntVersion);
                Assert.False(face.OpenType.TryGet(TableTags.Cff, out _));
                return 0;
            });
        }

        [Fact]
        public void OpenTypeData_FromSfnt_RejectsTruncatedData()
        {
            byte[] full = File.ReadAllBytes(TestLayout.FontPath(TestLayout.RegularFile));
            var truncated = new byte[64];
            Array.Copy(full, truncated, truncated.Length);

            // 表目录越界 → 明确抛异常（不是返回半份数据）
            Assert.ThrowsAny<Exception>(() => OpenTypeFontData.FromSfnt(truncated));
            Assert.ThrowsAny<Exception>(() => OpenTypeFontData.FromSfnt(new byte[] { 0, 1, 0, 0 }));

            // TTC 面下标越界也要抛
            Assert.ThrowsAny<ArgumentOutOfRangeException>(() =>
            {
                var ttc = new byte[16];
                ttc[0] = (byte)'t'; ttc[1] = (byte)'t'; ttc[2] = (byte)'c'; ttc[3] = (byte)'f';
                ttc[11] = 1;   // numFonts = 1
                OpenTypeFontData.FromSfnt(ttc, 3);
            });
        }

        [Fact]
        public void TableTags_MakeMatchesDwriteMacro()
        {
            // DWRITE_MAKE_OPENTYPE_TAG(a,b,c,d) = (a<<24)|(b<<16)|(c<<8)|d
            Assert.Equal(0x4F532F32u, TableTags.Make("OS/2"));
            Assert.Equal(0x636D6170u, TableTags.Make("cmap"));
            Assert.Equal(0x47535542u, TableTags.Make("GSUB"));
            Assert.Equal("GSUB", TableTags.ToString(0x47535542u));
            Assert.Throws<ArgumentException>(() => TableTags.Make("abc"));
        }

        [Fact]
        public void ResetFontFaceCache_IsANoOpThatDoesNotBreakAnything()
        {
            // 上游 Font::ResetFontFaceCache 是静态方法，LayoutManager 每次 UpdateLayout
            // 结束都会调（LayoutManager.cs:436）。Linux 侧我们的缓存挂在 Font 实例上，
            // 所以它是"确实没有可清的东西"的空实现 —— 但必须可调用。
            LinuxFont.ResetFontFaceCache();

            _fixture.WithFace(_fixture.Regular, face =>
            {
                Assert.Equal(3884, face.GlyphCount);
                return 0;
            });
        }
    }
}
