// T2 · 系统兜底族（default family）出口 —— provider 侧。
//
// 【为什么需要】按名字建 FontFamily 在 Linux 上会去加载系统复合字体，兜底链（#GLOBAL USER INTERFACE
//   → #ARIAL）两边落空 ⇒ NRE / 断言炸。主控裁定：对齐 WPF 语义 ——"找不到族就落到系统兜底族"，
//   永远不因为"某字体没装"而崩。兜底族由本出口给出。
//
// 【为什么不能取 collection[0]】那只反映"扫到了什么/枚举顺序如何"：
//   只给 src/Linux/build/fonts → Noto Sans（巧合）；加上 /usr/share/fonts → AR PL UKai CN（楷体，拉丁差）。
//   ⇒ 兜底族必须**有原则、确定、可观测**。
//
// 【选择次序】每档都写明依据，**没有任何一档依赖目录/文件枚举顺序**：
//   ① 写死的偏好序：Noto Sans → DejaVu Sans → Liberation Sans。
//      优先于 fontconfig 是**刻意**的：对齐 WPF（Windows 默认 UI 字体是固定族）+ 跨目录配置稳定
//      （实测 fontconfig 优先会让默认族随扫描目录漂：Noto Sans vs Noto Sans CJK SC）。
//   ② fontconfig 的 sans-serif（系统自己的"默认无衬线"概念，走 fc-match）：偏好族都没有时才用。
//      **先探测 fc-match 是否存在**，不存在/超时/无输出 ⇒ 跳过这一档（不伪造"查过 fontconfig"）。
//   ③ 最后才按**族名 Ordinal 升序**取第一个。
//   ④ 候选为空 ⇒ **明确抛错**（绝不返回 null 族），报文点明该查哪些目录。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace MS.Internal.Text.TextInterface.Linux
{
    /// <summary>系统兜底族的选定结果：族名 + 依据（依据本身也是可观测面）。</summary>
    public sealed class DefaultFamilyChoice
    {
        public string FamilyName { get; }
        /// <summary>fontconfig-sans-serif / preferred-list / name-order</summary>
        public string Reason { get; }
        /// <summary>人读补充说明（诊断与排查用）。</summary>
        public string Detail { get; }

        public DefaultFamilyChoice(string familyName, string reason, string detail)
        {
            FamilyName = familyName; Reason = reason; Detail = detail;
        }

        public override string ToString() => FamilyName + " (" + Reason + ")";
    }

    public static class DefaultFontFamily
    {
        /// <summary>诊断开关：=1 时把"选了哪个族、依据是什么"打到 stderr（默认静默）。</summary>
        public const string DiagnosticsEnv = "WPF_LINUX_FONT_DIAG";

        internal const string DiagPrefix = "[FONT_DIAG]";

        /// <summary>退档偏好序（依据见文件头 ②）。不随机器/目录变化。</summary>
        private static readonly string[] PreferredFamilies =
        {
            "Noto Sans",
            "DejaVu Sans",
            "Liberation Sans",
        };

        /// <summary>空字体集报文：明确失败 + 点明检查哪两个来源。</summary>
        internal const string EmptySetMessage =
            "字体集为空：没有任何可用字体，无法给出系统兜底族。" +
            "检查 src/Linux/build/fonts 是否存在且可读、WPF_LINUX_TEXT_FONT_DIR 指向的目录是否正确。";

        /// <summary>
        /// 选定系统兜底族。**确定性**：同机同目录集永远同一结果（不依赖枚举/扫描顺序）。
        /// 字体集为空 ⇒ 抛 InvalidOperationException（不返回 null 族）。
        /// </summary>
        public static DefaultFamilyChoice Select(LinuxFontCollection collection)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            List<string> names = collection.Entries
                .Select(e => e.FamilyName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            if (names.Count == 0) throw new InvalidOperationException(EmptySetMessage);

            // ① 写死的偏好序优先 —— **这是刻意的**：
            //   (a) 与 WPF 对齐：Windows 的默认 UI 字体是一个**固定族**（Segoe UI），不是"系统字体配置说什么就是什么"；
            //   (b) **跨目录配置稳定**：实测过 fontconfig 优先时，只给 src/Linux/build/fonts 得 Noto Sans、
            //       加上 /usr/share/fonts 得 Noto Sans CJK SC —— 默认族会随"扫到哪些目录"变，
            //       等于把主控点名的病灶（默认族跟着环境漂）换个形式留着。
            foreach (string preferred in PreferredFamilies)
            {
                if (collection.FindFamilyName(preferred, out _))
                {
                    return Report(new DefaultFamilyChoice(preferred, "preferred-list",
                        "偏好序命中 " + preferred +
                        "（fontconfig sans-serif = " + (QueryFontconfigSansSerif() ?? "<不可用>") + "，仅作第二档）"),
                        collection);
                }
            }

            // ② fontconfig 的 sans-serif：偏好族一个都没有时，用系统自己的"默认无衬线"
            string fcFamily = QueryFontconfigSansSerif();
            if (fcFamily != null && collection.FindFamilyName(fcFamily, out _))
            {
                return Report(new DefaultFamilyChoice(fcFamily, "fontconfig-sans-serif",
                    "偏好序全部未命中；fc-match sans-serif = " + fcFamily + "（已在本集合中）"), collection);
            }

            return Report(new DefaultFamilyChoice(names[0], "name-order",
                "fontconfig 与偏好序都未命中；按族名 Ordinal 升序取第一个（共 " + names.Count + " 个族）"),
                collection);
        }

        /// <summary>便捷重载：只要族名。空集同样抛错。</summary>
        public static string SelectFamilyName(LinuxFontCollection collection) => Select(collection).FamilyName;

        private static DefaultFamilyChoice Report(DefaultFamilyChoice choice, LinuxFontCollection collection)
        {
            if (Environment.GetEnvironmentVariable(DiagnosticsEnv) == "1")
            {
                Console.Error.WriteLine(DiagPrefix + " DEFAULT_FAMILY=" + choice.FamilyName +
                    " REASON=" + choice.Reason + " FAMILIES=" + collection.FamilyCount +
                    " FACES=" + collection.Entries.Count + " :: " + choice.Detail);
            }
            return choice;
        }

        /// <summary>问 fontconfig 要"默认无衬线族"。探测优先：不可用 ⇒ null（跳过该档，不伪造）。</summary>
        private static string QueryFontconfigSansSerif()
        {
            try
            {
                var psi = new ProcessStartInfo("fc-match")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("--format=%{family}");
                psi.ArgumentList.Add("sans-serif");

                using Process p = Process.Start(psi);
                if (p == null) return null;
                string stdout = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(2000)) { try { p.Kill(); } catch { } return null; }
                if (p.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout)) return null;

                string first = stdout.Split(',')[0].Trim();   // fc-match 可能给别名回退链
                return string.IsNullOrEmpty(first) ? null : first;
            }
            catch
            {
                return null;
            }
        }
    }
}
