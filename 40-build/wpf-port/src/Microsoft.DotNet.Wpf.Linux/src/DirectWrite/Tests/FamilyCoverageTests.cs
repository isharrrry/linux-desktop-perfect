// T2 · 族级覆盖查询的验收（只读面）：三态区分、确定性、缓存可观测、既有集合的答案。
using Xunit;
using Xunit.Abstractions;
using MS.Internal.Text.TextInterface.Linux;

namespace MS.Internal.Text.TextInterface.Linux.Tests
{
    public class FamilyCoverageTests
    {
        public FamilyCoverageTests(ITestOutputHelper output) => Output = output;
        private ITestOutputHelper Output { get; }

        private const int LATIN_A = 0x0041;      // 'A'
        private const int CJK_ZHONG = 0x4E2D;    // '中'

        [Fact]
        public void Family_Covers_Latin_But_Not_CJK()
        {
            using LinuxFontCollection c = LinuxFontCollection.FromDirectory(TestLayout.FontDir, recurse: true, stripLayout: false);
            LinuxFontFamily noto = c["Noto Sans"];
            Assert.NotNull(noto);
            Output.WriteLine($"Noto Sans: 'A' => {noto.QueryCoverage(LATIN_A)}；U+4E2D => {noto.QueryCoverage(CJK_ZHONG)}");
            Assert.True(noto.Covers(LATIN_A));
            // src/Linux/build/fonts 的 Noto Sans 无 CJK ⇒ 必须是**查过了的"没有"**，而不是"答不了"
            Assert.Equal(FamilyCoverage.NotCovered, noto.QueryCoverage(CJK_ZHONG));
        }

        [Fact]
        public void Collection_Finds_CoveringFamily_Deterministically()
        {
            using LinuxFontCollection c = LinuxFontCollection.FromDirectory(TestLayout.FontDir, recurse: true, stripLayout: false);
            Assert.True(c.TryFindFamilyCovering(LATIN_A, out LinuxFontFamily fam));
            Assert.Equal("Noto Sans", fam.FamilyName);
            // 同问两次必须同答（缓存与首次计算一致）
            Assert.True(c.TryFindFamilyCovering(LATIN_A, out LinuxFontFamily fam2));
            Assert.Equal(fam.FamilyName, fam2.FamilyName);
        }

        [Fact]
        public void EmptyCollection_Answers_Unknown_Not_NotCovered()
        {
            string missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "no-fonts-" + System.Guid.NewGuid().ToString("N"));
            using LinuxFontCollection empty = LinuxFontCollection.FromDirectory(missing, recurse: true, stripLayout: false);
            Assert.Equal(0, empty.FamilyCount);
            FamilyCoverage verdict = empty.QueryCoverage(LATIN_A, out LinuxFontFamily fam);
            Output.WriteLine($"空集合：verdict={verdict} family={(fam == null ? "<null>" : fam.FamilyName)}");
            // **"答不了" 必须与 "没有" 分开** —— 这正是本 API 存在的理由
            Assert.Equal(FamilyCoverage.Unknown, verdict);
            Assert.Null(fam);
            Assert.False(empty.TryFindFamilyCovering(LATIN_A, out _));
        }

        [Fact]
        public void Stats_Distinguish_Miss_Then_Hit()
        {
            using LinuxFontCollection c = LinuxFontCollection.FromDirectory(TestLayout.FontDir, recurse: true, stripLayout: false);
            LinuxFontFamily noto = c["Noto Sans"];
            FamilyCoverageQuery.ResetStats();
            noto.QueryCoverage(0x005A);      // 'Z' 首次 → miss
            FamilyCoverageStats mid = FamilyCoverageQuery.Stats;
            noto.QueryCoverage(0x005A);      // 再来一次 → hit
            FamilyCoverageStats end = FamilyCoverageQuery.Stats;
            Output.WriteLine($"first={mid}  then={end}");
            Assert.True(mid.CacheHits == 0, "首次不应命中缓存");
            Assert.True(end.CacheHits >= 1, "第二次应命中缓存（缓存可观测）");
            Assert.True(end.Queries >= 2);
            Assert.True(end.ElapsedTicks > 0, "总耗时应被记录");
        }
    }
}
