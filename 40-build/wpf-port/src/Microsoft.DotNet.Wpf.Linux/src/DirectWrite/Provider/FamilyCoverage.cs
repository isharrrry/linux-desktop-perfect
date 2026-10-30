// T2 · 族级按码点覆盖查询（**只读面**）—— 给 PC 侧"补丁 J 短路复合字体后"的回退链用。
//
// 【为什么需要】补丁 J 把非 Windows 的回退从 GlobalUserInterface 的区间映射改成
//   「回退交给 provider 的首个可用族」，但"这个族不覆盖某个码点时要换一个覆盖得上的族"
//   这件事，只有 provider 知道 —— 本文件就是回答它的那一层。
//
// 【只加不放松】全部以**扩展方法**提供 ⇒ 既有类型（LinuxFontFamily / LinuxFontCollection）
//   与既有代码路径**一个字节都没改**；不新增字段、不改行为。
//
// 【确定性 & 缓存】缓存挂在**族实例**上（ConditionalWeakTable ⇒ 随实例回收，不泄漏）。
//   粒度：每个族 × 每个码点一个三态结论。
//   失效条件：不需要 —— LinuxFontCollection 在 Load 之后**不可变**（族只在加载期创建、
//   只有 Dispose 会 Clear），换集合 = 换实例 = 换缓存。字体文件在进程内被换掉这种情形
//   本 provider 其它部分同样不处理（同一假设）。
//
// 【"答不了"必须与"没有"分开】FamilyCoverage.Unknown ≠ NotCovered：
//   · NotCovered = 查过了，族里的可用面都答"没有这个码点"
//   · Unknown    = 查不了（没有族/族里没有可用面/取面时全部失败）
//   —— 静默默认值（把"答不了"当"没有"）是这一族缺陷最典型的形态，这里显式区分。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace MS.Internal.Text.TextInterface.Linux
{
    /// <summary>族对某码点的覆盖结论（三态：能答的两态 + 答不了的一态）。</summary>
    public enum FamilyCoverage
    {
        /// <summary>族里有可用面覆盖该码点。</summary>
        Covered = 0,
        /// <summary>查过了：族里可用面都不覆盖该码点。</summary>
        NotCovered = 1,
        /// <summary>查不了：没有可用面（或取面全部失败）。**不要当成 NotCovered**。</summary>
        Unknown = 2,
    }

    /// <summary>只读诊断计数（这条回退一旦生效会**静默改变某些字符用哪个族**，所以要看得见）。</summary>
    public readonly struct FamilyCoverageStats
    {
        public long Queries { get; }
        public long CacheHits { get; }
        public long Covered { get; }
        public long NotCovered { get; }
        public long Unknown { get; }
        public long ElapsedTicks { get; }
        public FamilyCoverageStats(long q, long hits, long cov, long notCov, long unk, long ticks)
        { Queries = q; CacheHits = hits; Covered = cov; NotCovered = notCov; Unknown = unk; ElapsedTicks = ticks; }
        public override string ToString() =>
            $"queries={Queries} cacheHits={CacheHits} covered={Covered} notCovered={NotCovered} unknown={Unknown} elapsedTicks={ElapsedTicks}";
    }

    public static class FamilyCoverageQuery
    {
        /// <summary>诊断开关（与 provider 其它诊断同源）。</summary>
        public const string DiagnosticsEnv = "WPF_LINUX_FONT_DIAG";

        private static readonly ConditionalWeakTable<LinuxFontFamily, Dictionary<int, FamilyCoverage>> s_cache
            = new ConditionalWeakTable<LinuxFontFamily, Dictionary<int, FamilyCoverage>>();

        private static long s_queries, s_cacheHits, s_covered, s_notCovered, s_unknown, s_elapsedTicks;
        private static int s_traced;

        public static FamilyCoverageStats Stats => new FamilyCoverageStats(
            Interlocked.Read(ref s_queries), Interlocked.Read(ref s_cacheHits),
            Interlocked.Read(ref s_covered), Interlocked.Read(ref s_notCovered),
            Interlocked.Read(ref s_unknown), Interlocked.Read(ref s_elapsedTicks));

        public static void ResetStats()
        {
            Interlocked.Exchange(ref s_queries, 0); Interlocked.Exchange(ref s_cacheHits, 0);
            Interlocked.Exchange(ref s_covered, 0); Interlocked.Exchange(ref s_notCovered, 0);
            Interlocked.Exchange(ref s_unknown, 0); Interlocked.Exchange(ref s_elapsedTicks, 0);
            Interlocked.Exchange(ref s_traced, 0);
        }

        public static FamilyCoverage QueryCoverage(this LinuxFontFamily family, int codePoint)
        {
            Interlocked.Increment(ref s_queries);
            if (family == null || family.Count == 0)
                return Record(codePoint, FamilyCoverage.Unknown, family, "族为空或没有可用面");

            Dictionary<int, FamilyCoverage> per = s_cache.GetValue(family, _ => new Dictionary<int, FamilyCoverage>());
            if (per.TryGetValue(codePoint, out FamilyCoverage cached))
            {
                Interlocked.Increment(ref s_cacheHits);
                Record(codePoint, cached, family, "缓存命中");
                return cached;
            }

            long t0 = Stopwatch.GetTimestamp();
            FamilyCoverage verdict = FamilyCoverage.Unknown;
            int examined = 0;
            foreach (LinuxFont font in family)
            {
                try
                {
                    examined++;
                    if (font.HasCharacter(codePoint)) { verdict = FamilyCoverage.Covered; break; }
                    verdict = FamilyCoverage.NotCovered;      // 至少有一个面明确答"没有"
                }
                catch
                {
                    // 取面失败：不把失败当成"没有"；若无任何面答过，结论保持 Unknown
                }
            }
            Interlocked.Add(ref s_elapsedTicks, Stopwatch.GetTimestamp() - t0);
            if (examined == 0) verdict = FamilyCoverage.Unknown;

            per[codePoint] = verdict;
            Record(codePoint, verdict, family, "首次查询");
            return verdict;
        }

        /// <summary>便捷形态：只关心"覆盖与否"（Unknown 视为 false —— 调用方要区分时用 QueryCoverage）。</summary>
        public static bool Covers(this LinuxFontFamily family, int codePoint)
            => family.QueryCoverage(codePoint) == FamilyCoverage.Covered;

        /// <summary>
        /// 集合级：找**第一个**覆盖该码点的族（按集合的既有族序，确定性）。
        /// 返回三态：Covered / NotCovered（都答过"没有"）/ Unknown（至少一个族答不了，且无人覆盖）。
        /// </summary>
        public static FamilyCoverage QueryCoverage(this LinuxFontCollection collection, int codePoint, out LinuxFontFamily family)
        {
            family = null;
            if (collection == null || collection.FamilyCount == 0)
            {
                Interlocked.Increment(ref s_queries);
                Record(codePoint, FamilyCoverage.Unknown, null, "集合为空");
                return FamilyCoverage.Unknown;
            }

            bool anyUnknown = false;
            for (int i = 0; i < collection.FamilyCount; i++)
            {
                LinuxFontFamily candidate = collection[i];
                FamilyCoverage verdict = candidate.QueryCoverage(codePoint);
                if (verdict == FamilyCoverage.Covered) { family = candidate; return FamilyCoverage.Covered; }
                if (verdict == FamilyCoverage.Unknown) anyUnknown = true;
            }
            return anyUnknown ? FamilyCoverage.Unknown : FamilyCoverage.NotCovered;
        }

        /// <summary>便捷形态：找到了返回 true（Unknown 与 NotCovered 都返回 false）。</summary>
        public static bool TryFindFamilyCovering(this LinuxFontCollection collection, int codePoint, out LinuxFontFamily family)
            => collection.QueryCoverage(codePoint, out family) == FamilyCoverage.Covered;

        private static FamilyCoverage Record(int codePoint, FamilyCoverage verdict, LinuxFontFamily family, string why)
        {
            switch (verdict)
            {
                case FamilyCoverage.Covered:    Interlocked.Increment(ref s_covered); break;
                case FamilyCoverage.NotCovered: Interlocked.Increment(ref s_notCovered); break;
                default:                        Interlocked.Increment(ref s_unknown); break;
            }
            if (Environment.GetEnvironmentVariable(DiagnosticsEnv) == "1" && Interlocked.Increment(ref s_traced) <= 50)
            {
                Console.Error.WriteLine($"[FONT_DIAG] COVERAGE cp=U+{codePoint:X4} verdict={verdict} family={family?.FamilyName ?? "<none>"} ({why})");
            }
            return verdict;
        }
    }
}
