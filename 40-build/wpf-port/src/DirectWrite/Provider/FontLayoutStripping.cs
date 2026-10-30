// T1 · M7c5（路线 C）—— **运行期**布局表剥离的开关与落地。
// =====================================================================================
// 【要解决什么】
//   只有默认 UI 字体（`src/Linux/build/fonts-ui/UI-NoLayout.ttf`，离线用 `src/Linux/build/gen-ui-font.py` 剥好）
//   能过 WPF 的快路径闸门 2；**应用显式指定的任何真实字体都会被拒** → 回落 LineServices
//   → 那套原生引擎在本仓库里**不存在**（没有 C++、没有二进制，见 T1/M7c4 决策报告）
//   → 文本测量/绘制异常退出。
//
//   本类把"离线剥离"变成**运行期能力**：任何字体在加载时按需剥掉 `GSUB`/`GPOS`，
//   于是任何字体都能过闸门 2，解除"只有默认 UI 字体能出字"这条产品级限制。
//
// 【为什么剥离是诚实的，不是骗闸门】
//   快路径**本来就是名义字形语义**：不做 kerning、不做连字、不做 locl 替换。
//   剥掉 GSUB/GPOS 之后字体**确实**不再带这些特性（掩码 0 是测出来的事实），
//   而字形 id / 轮廓 / 步进 / cmap / 全部度量**逐项不变**
//   （`FontLayoutStrippingTests` 对 3884 个字形步进 + 65536 个 BMP 码点逐项断言）。
//   也就是说：剥离后的渲染结果 == 快路径会画出来的东西，**一像素都不差**。
//
// 【默认值：开】—— 论证见 docs/…… 与本文件 §默认值 注释（也在 T1 报告里）
//   本工程**没有 shaper**（HarfBuzz 路线是 M7c4 建议的路线 B 第 2 步，尚未做），
//   所以 GSUB/GPOS 在任何路径下都不会被应用。此时：
//     · 剥 → 过闸门 2 → 名义字形出字（正确，只是没 kerning/连字）
//     · 不剥 → 闸门 2 拒 → 进 FullTextLine → `LoCreateContext` 等 26 个原生符号缺失 → 崩
//   即"剥"严格优于"不剥"；唯一的代价是 **字体元数据保真度**：
//   `TryGetFontTable("GSUB"/"GPOS")` 会返回 false，WPF 的 `FontCapabilities` 之类会报"无特性"。
//   在没有任何消费者能使用这些特性的前提下，这个代价小于"文本直接崩"。
//   想要元数据保真的调用方可以 `WPF_LINUX_STRIP_LAYOUT=0`，或用**逐次旁路**参数。
//
// 【逐次旁路（硬要求）】
//   既有断言测的是**原始文件**的掩码（Noto=21 / DejaVu=23），必须还能测到。
//   所以每个加载入口都有 `stripLayout` 参数：
//     null  → 用本类的默认（env / DefaultEnabled）
//     false → 本次加载**不剥**，字节与行为与改动前逐字节一致
//     true  → 本次加载强制剥（无视 env）
//   探针（`Probe`）走的就是 `false` —— 它是"原始语料"的诊断，语义上就该测原始字节，
//   因此 T2 已提交的 `artifacts/probe-summary.txt` / `probe-digest.txt` **不受影响**。
// =====================================================================================

using System;
using System.Threading;

namespace MS.Internal.Text.TextInterface.Linux
{
    /// <summary>运行期 GSUB/GPOS 剥离的开关、统计与落地。</summary>
    public static class FontLayoutStripping
    {
        /// <summary>环境变量：`1`/`true`/`on`/`yes` 开，`0`/`false`/`off`/`no` 关。</summary>
        public const string EnvVar = "WPF_LINUX_STRIP_LAYOUT";

        /// <summary>要剥的表 —— **只有这两个**，与 `src/Linux/build/gen-ui-font.py` 的 `DEFAULT_STRIP` 一致。</summary>
        public static readonly string[] TablesToStrip = { "GSUB", "GPOS" };

        /// <summary>
        /// 编译期默认值：**开**（理由见文件头 §默认值）。
        /// </summary>
        public const bool DefaultEnabled = true;

        private static int s_envResolved;      // 0 = 未解析，1 = 已解析
        private static bool s_envValue;

        /// <summary>测试钩子：非 null 时覆盖 env 与 <see cref="DefaultEnabled"/>。</summary>
        public static bool? Override { get; set; }

        /// <summary>本次进程的有效默认（env 优先，其次 <see cref="DefaultEnabled"/>）。</summary>
        public static bool Enabled => Override ?? ResolveEnv();

        private static bool ResolveEnv()
        {
            if (Volatile.Read(ref s_envResolved) == 0)
            {
                s_envValue = ParseEnv(Environment.GetEnvironmentVariable(EnvVar));
                Volatile.Write(ref s_envResolved, 1);
            }
            return s_envValue;
        }

        /// <summary>环境变量解析（也是公开的，便于测试直接验证口径）。</summary>
        public static bool ParseEnv(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return DefaultEnabled;
            switch (value.Trim().ToLowerInvariant())
            {
                case "1": case "true": case "on": case "yes": return true;
                case "0": case "false": case "off": case "no": return false;
                default: return DefaultEnabled;    // 认不出来就按默认（不因为打错字就改行为）
            }
        }

        // ---------------------------------------------------------------------------------
        //  统计（诊断 / 断言用）
        // ---------------------------------------------------------------------------------

        private static long s_stripped;
        private static long s_notStripped;
        private static long s_bypassed;
        private static long s_failed;
        private static string s_lastReason = "(never)";

        /// <summary>真的剥成功了几次。</summary>
        public static long StrippedCount => Interlocked.Read(ref s_stripped);
        /// <summary>开关开着、但字体本来就没有 GSUB/GPOS（原字节返回）的次数。</summary>
        public static long NoLayoutTablesCount => Interlocked.Read(ref s_notStripped);
        /// <summary>调用方显式旁路（stripLayout=false）的次数。</summary>
        public static long BypassedCount => Interlocked.Read(ref s_bypassed);
        /// <summary>剥失败（非 sfnt / 目录损坏 / 无 head）因而退回原字节的次数。</summary>
        public static long FailedCount => Interlocked.Read(ref s_failed);
        /// <summary>最近一次的处置说明。</summary>
        public static string LastReason => Volatile.Read(ref s_lastReason);

        /// <summary>清统计（测试用）。</summary>
        public static void ResetCounters()
        {
            Interlocked.Exchange(ref s_stripped, 0);
            Interlocked.Exchange(ref s_notStripped, 0);
            Interlocked.Exchange(ref s_bypassed, 0);
            Interlocked.Exchange(ref s_failed, 0);
            Volatile.Write(ref s_lastReason, "(never)");
        }

        /// <summary>测试用：清掉 env 解析缓存（改完环境变量再调）。</summary>
        public static void ResetEnvCache() => Volatile.Write(ref s_envResolved, 0);

        // ---------------------------------------------------------------------------------
        //  落地
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// 按 <paramref name="stripLayout"/> 决定是否剥离。
        /// 返回**总是可用**的字体字节：剥成功是新字节，任何其他情况都是**入参原字节**。
        /// </summary>
        /// <param name="original">原始字体字节。</param>
        /// <param name="stripLayout">null=用默认；false=本次旁路；true=本次强制剥。</param>
        /// <param name="stripped">是否真的发生了剥离。</param>
        /// <param name="reason">处置说明（统计与诊断用）。</param>
        public static byte[] Apply(byte[] original, bool? stripLayout, out bool stripped, out string reason)
        {
            stripped = false;

            if (original == null) { reason = "null-input"; return original; }

            if (stripLayout == false)
            {
                Interlocked.Increment(ref s_bypassed);
                reason = "bypassed";
                Volatile.Write(ref s_lastReason, reason);
                return original;
            }

            if (stripLayout == null && !Enabled)
            {
                Interlocked.Increment(ref s_bypassed);
                reason = "disabled";
                Volatile.Write(ref s_lastReason, reason);
                return original;
            }

            bool ok = FontTableStripper.TryStripTables(original, out byte[] result, out string failReason,
                                                       TablesToStrip);
            if (ok)
            {
                stripped = true;
                Interlocked.Increment(ref s_stripped);
                reason = "stripped(GSUB,GPOS)";
            }
            else if (failReason == FontTableStripper.FailureReason.NoLayoutTables)
            {
                // 开关开着，但这份字体本来就没有布局表 —— **正常情况**，不是失败。
                Interlocked.Increment(ref s_notStripped);
                reason = failReason;
            }
            else
            {
                // 非 sfnt / TTC / 目录损坏 / 无 head：**明确退回原字节并记录**，不产出半个字体。
                Interlocked.Increment(ref s_failed);
                reason = failReason;
            }

            Volatile.Write(ref s_lastReason, reason);
            return result;
        }
    }
}
