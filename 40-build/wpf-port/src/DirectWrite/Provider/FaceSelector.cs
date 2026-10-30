// T2 · Phase 1 —— 字面匹配（GetFirstMatchingFont / FontCollection[name] 的核心）
// =====================================================================================
// 【为什么这件事必须显式定义，不能"随手挑一个"】
//   打包字体里只有 Regular/Bold/Italic/BoldItalic 四面。上层（WPF 的 Typeface）
//   会用任意 FontWeight 值来要字体：FontWeight.FromOpenTypeWeight(usWeightClass) 可以是
//   1..999 的任何整数，而不只是枚举里的 10 个值。匹配规则一旦含糊，
//   就会出现"同一个控件在两次运行里选中不同字面"这种不可复现的现象。
//
// 【两条规则，都实现，可切换】
//   Distance（默认）：在 (weight 距离, stretch 距离, style 距离) 上做**字典序最小**，
//                     平局取更轻的字重，再平局取更小的面下标（确定性）。
//                     这是 DWrite 风格的口径：先看字重像不像，再看拉伸，最后看斜体。
//   BoldBucket：M1 Text/ 的现有口径（TextFontDescription.IsBold == weight >= 600，
//               斜体按 slant != Upright）。选它是为了与 FontSet.TryResolve **逐值一致**。
//
// 【两条规则的差异是量化过的，不是"差不多"】
//   在只有 400/700 两个字重的族上：
//     · 对 WPF FontWeight 枚举的**全部 10 个值**（100/200/300/400/500/600/700/800/900/950）
//       × 3 种 style，两条规则**结论完全相同**（30/30，见 M1ConsistencyTests）；
//     · 差异只出现在 551..599 这 49 个**非枚举**整数上：Distance 选 Bold，
//       BoldBucket 选 Regular（分界点 550 恰好是 400/700 的中点，取轻者）。
//   这一点被测试显式断言（差异集合必须恰好是 551..599），所以将来谁改了规则都会红。

using System;
using System.Collections.Generic;

namespace MS.Internal.Text.TextInterface.Linux
{
    /// <summary>一个可匹配的字面（字体文件里的一个面）。</summary>
    public sealed class FontFaceEntry
    {
        public FontFaceEntry(
            string filePath, int faceIndex, string familyName, string subFamilyName,
            string postScriptName, int weight, int width, int slant)
        {
            FilePath = filePath;
            FaceIndex = faceIndex;
            FamilyName = familyName;
            SubFamilyName = subFamilyName;
            PostScriptName = postScriptName;
            Weight = weight;
            Width = width;
            Slant = slant;
        }

        /// <summary>字体文件路径。</summary>
        public string FilePath { get; }

        /// <summary>TTC 内的面下标（非 TTC 为 0）。</summary>
        public int FaceIndex { get; }

        /// <summary>族名（与 WPF 的 Typeface.FamilyName 同源：Skia 读出来的族名）。</summary>
        public string FamilyName { get; }

        /// <summary>子族名（Regular / Bold / Italic / Bold Italic）。</summary>
        public string SubFamilyName { get; }

        /// <summary>PostScript 名（字体文件里的唯一名字）。</summary>
        public string PostScriptName { get; }

        /// <summary>usWeightClass 语义：1..999（Regular=400，Bold=700）。</summary>
        public int Weight { get; }

        /// <summary>usWidthClass 语义：1..9（Normal=5）。</summary>
        public int Width { get; }

        /// <summary>FontStyle 语义：0=Normal，1=Oblique，2=Italic。</summary>
        public int Slant { get; }

        /// <summary>粗体桶口径下的"是否粗体"（与 M1 TextFontDescription.IsBold 一致）。</summary>
        public bool IsBoldBucket => Weight >= BoldBucketThreshold;

        /// <summary>粗体桶口径下的"是否斜体"。</summary>
        public bool IsItalicBucket => Slant != 0;

        /// <summary>M1 的粗体分界（TextFontDescription.IsBold =&gt; Weight &gt;= 600）。</summary>
        public const int BoldBucketThreshold = 600;

        public override string ToString() =>
            $"{FamilyName} w={Weight} wd={Width} s={Slant} ({SubFamilyName}) face={FaceIndex}";
    }

    /// <summary>字面匹配。</summary>
    public static class FaceSelector
    {
        /// <summary>
        /// 在 <paramref name="faces"/> 里挑最匹配 (weight, stretch, style) 的一个，返回下标；空集合返回 -1。
        /// 结果只取决于输入（无随机、无字典序依赖）：同分时取更小下标。
        /// </summary>
        public static int SelectBest(
            IReadOnlyList<FontFaceEntry> faces, int weight, int stretch, int style, FontMatchingRule rule)
        {
            if (faces == null || faces.Count == 0) return -1;

            if (rule == FontMatchingRule.BoldBucket) return SelectByBoldBucket(faces, weight, stretch, style);

            int best = -1;
            long bestWeight = long.MaxValue, bestStretch = long.MaxValue, bestStyle = long.MaxValue;
            int bestTie = int.MaxValue;

            for (int i = 0; i < faces.Count; i++)
            {
                FontFaceEntry face = faces[i];

                long weightDistance = Math.Abs((long)face.Weight - weight);
                long stretchDistance = Math.Abs((long)face.Width - stretch);
                long styleDistance = StyleDistance(face.Slant, style);

                bool better;
                if (best < 0)
                {
                    better = true;
                }
                else
                {
                    // 平局取**更轻**的字重：与 M1 的 600 分界在 550 这个中点上一致（见文件头）。
                    better =
                        weightDistance < bestWeight ||
                        (weightDistance == bestWeight && stretchDistance < bestStretch) ||
                        (weightDistance == bestWeight && stretchDistance == bestStretch && styleDistance < bestStyle) ||
                        (weightDistance == bestWeight && stretchDistance == bestStretch && styleDistance == bestStyle &&
                         (face.Weight < faces[best].Weight || (face.Weight == faces[best].Weight && i < bestTie)));
                }

                if (better)
                {
                    best = i;
                    bestTie = i;
                    bestWeight = weightDistance;
                    bestStretch = stretchDistance;
                    bestStyle = styleDistance;
                }
            }

            return best;
        }

        private static int SelectByBoldBucket(
            IReadOnlyList<FontFaceEntry> faces, int weight, int stretch, int style)
        {
            bool wantBold = weight >= FontFaceEntry.BoldBucketThreshold;
            bool wantItalic = style != 0;

            int best = -1;
            long bestScore = long.MaxValue;

            for (int i = 0; i < faces.Count; i++)
            {
                FontFaceEntry face = faces[i];

                // 粗/斜是硬条件；拉伸与字重用于排序。
                if (face.IsBoldBucket != wantBold || face.IsItalicBucket != wantItalic) continue;

                long score = Math.Abs((long)face.Weight - weight) * 100 + Math.Abs((long)face.Width - stretch);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }

            // 桶内一个都没有（例如族里只有 Medium 一种）→ 退回距离规则，绝不返回"找不到"。
            return best >= 0 ? best : SelectBest(faces, weight, stretch, style, FontMatchingRule.Distance);
        }

        /// <summary>样式距离：相同 0；Oblique↔Italic 视为近邻 1；正体↔斜体 2。</summary>
        private static long StyleDistance(int candidateSlant, int requestedSlant)
        {
            if (candidateSlant == requestedSlant) return 0;

            bool candidateItalic = candidateSlant != 0;
            bool requestedItalic = requestedSlant != 0;
            return candidateItalic == requestedItalic ? 1 : 2;
        }
    }
}
