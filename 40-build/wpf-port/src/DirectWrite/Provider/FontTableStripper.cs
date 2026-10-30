// T2 · Phase 3 —— SFNT 表剥离器（用于生成"能走名义字形快路径"的字体候选）
// =====================================================================================
// 【为什么需要它：闸门 2 的物理约束】
//   WPF 的 `Typeface.CheckFastPathNominalGlyphs`（Typeface.cs:520-562）在"全是 fast-text 字符"
//   时会检查 `TypographyAvailabilities`：只要字体对 fast-text 字形范围带着
//   `{ccmp,rlig,liga,clig,calt,kern,mark,mkmk}` 中任意一个（或 `locl`），就**拒掉快路径**，
//   回落到 LineServices 的复杂路径。
//   实测（本工程 LayoutFeatureReader）：Noto Sans / DejaVu Sans / Liberation Sans
//   **全都**在 `latn` 上带 `kern`（还有 liga/ccmp/mark/mkmk）→ 快路径对它们天然不可用。
//
//   而 fast path 的语义本来就是**名义字形**：不做 kerning、不做连字、不做 locl 替换。
//   所以"把 GSUB/GPOS 从字体里去掉"并不会让渲染比快路径**更**差 ——
//   它恰恰等于快路径会画出来的东西。这就使"剥离"成为一个**诚实**的候选：
//   字体确实没有那些特性了（不是骗闸门），渲染结果与快路径一致，
//   而且字形轮廓/度量/cmap 一个字节都没动。
//
// 【这个工具做什么】
//   读入一份 SFNT，按表目录**重建**一份只保留指定表的字体：
//     · 重算每张表的 checksum（OpenType 规范：uint32 累加，含 4 字节对齐的补零）
//     · 重算 head.checkSumAdjustment = 0xB1B0AFBA - 全文件 checksum
//     · 表目录按 tag 升序（规范要求），数据 4 字节对齐
//   输出是**新字节数组**，不改输入、不写仓库资产（调用方决定落到哪里）。
//
// 【不做的事】不合并/不重排 glyph id、不碰 glyf/loca/hmtx/cmap —— 所以
//   剥离前后所有字形的 id、轮廓、步进、度量完全一致（本工程的测试会逐项对比）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MS.Internal.Text.TextInterface.Linux
{
    /// <summary>SFNT 表剥离。</summary>
    public static class FontTableStripper
    {
        /// <summary>剥离失败的原因码（<see cref="TryStripTables"/> 的 <c>reason</c>）。</summary>
        public static class FailureReason
        {
            /// <summary>成功（不是失败）。</summary>
            public const string None = "ok";
            /// <summary>输入为 null。</summary>
            public const string NullInput = "null-input";
            /// <summary>短于 SFNT 头（12 字节）。</summary>
            public const string TooShort = "too-short";
            /// <summary>TTC（字体集合）：本轮只处理单面 SFNT。</summary>
            public const string TrueTypeCollection = "ttc-unsupported";
            /// <summary>表目录越界 / 表数据越界 —— 损坏文件。</summary>
            public const string CorruptDirectory = "corrupt-table-directory";
            /// <summary>没有 head 表 ⇒ 不是可用的 TrueType/OpenType 字体。</summary>
            public const string NoHeadTable = "no-head-table";
            /// <summary>**一个要剥的表都没找到** —— 原样返回，不算成功。</summary>
            public const string NoLayoutTables = "no-layout-tables";
        }

        /// <summary>
        /// **不抛异常**的剥离：给运行期加载路径用。
        ///
        /// 契约（T1/M7c5 要求 ⑤）：任何失败都
        ///   <c>result = sfnt</c>（**原字节**）、<c>stripResult = false</c>、<c>reason</c> = 具体原因，
        /// **绝不返回半个字体、绝不静默成功**。调用方拿到 false 就用原字节继续。
        ///
        /// 与 <see cref="StripTables"/> 的关系：成功时两者产出**逐字节相同**的字节
        /// （<c>TryStripTables</c> 成功路径直接委托给它）；失败时前者抛、后者返回原字节。
        /// </summary>
        public static bool TryStripTables(byte[] sfnt, out byte[] result, out string reason,
                                          params string[] tablesToRemove)
        {
            result = sfnt;
            reason = FailureReason.None;

            if (sfnt == null) { reason = FailureReason.NullInput; return false; }
            if (sfnt.Length < 12) { reason = FailureReason.TooShort; return false; }

            // ---- 先自己探一遍目录，把"损坏"和"没有可剥的表"区分开 ----
            // （不能只靠 catch：StripTables 对"没什么可剥"不抛，会安静地重建一份等价字体）
            if (OpenTypeFontData.U32(sfnt, 0) == TableTags.SfntTtcf)
            {
                reason = FailureReason.TrueTypeCollection;
                return false;
            }

            int numTables = OpenTypeFontData.U16(sfnt, 4);
            bool sawHead = false;
            int removable = 0;
            for (int i = 0; i < numTables; i++)
            {
                int rec = 12 + i * 16;
                if (rec + 16 > sfnt.Length) { reason = FailureReason.CorruptDirectory; return false; }

                string tag = TableTags.ToString(OpenTypeFontData.U32(sfnt, rec));
                int offset = (int)OpenTypeFontData.U32(sfnt, rec + 8);
                int length = (int)OpenTypeFontData.U32(sfnt, rec + 12);
                if (offset < 0 || length < 0 || (long)offset + length > sfnt.Length)
                {
                    reason = FailureReason.CorruptDirectory;
                    return false;
                }

                if (tag == "head") sawHead = true;
                if (tablesToRemove != null && Array.IndexOf(tablesToRemove, tag) >= 0) removable++;
            }

            if (!sawHead) { reason = FailureReason.NoHeadTable; return false; }
            if (removable == 0) { reason = FailureReason.NoLayoutTables; return false; }

            try
            {
                result = StripTables(sfnt, tablesToRemove);
                return true;
            }
            catch (Exception ex)
            {
                // 兜底：任何没预料到的失败都退回原字节并把异常消息带出去
                result = sfnt;
                reason = FailureReason.CorruptDirectory + ": " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 生成一份去掉 <paramref name="tablesToRemove"/> 的新字体字节。
        /// 输入不是可解析的 SFNT（或 TTC）→ 抛。
        /// </summary>
        public static byte[] StripTables(byte[] sfnt, params string[] tablesToRemove)
        {
            if (sfnt == null) throw new ArgumentNullException(nameof(sfnt));
            if (sfnt.Length < 12) throw new InvalidOperationException("字体数据太短");

            uint version = OpenTypeFontData.U32(sfnt, 0);
            if (version == TableTags.SfntTtcf)
                throw new NotSupportedException("剥离器只处理单面 SFNT（TTC 需要逐面重建，本轮不做）");

            var remove = new HashSet<string>(tablesToRemove ?? Array.Empty<string>(), StringComparer.Ordinal);

            // ---- 读原目录 ----
            int numTables = OpenTypeFontData.U16(sfnt, 4);
            var kept = new List<(string Tag, byte[] Data)>();
            for (int i = 0; i < numTables; i++)
            {
                int rec = 12 + i * 16;
                if (rec + 16 > sfnt.Length) throw new InvalidOperationException("表目录越界");

                string tag = TableTags.ToString(OpenTypeFontData.U32(sfnt, rec));
                int offset = (int)OpenTypeFontData.U32(sfnt, rec + 8);
                int length = (int)OpenTypeFontData.U32(sfnt, rec + 12);
                if (offset + length > sfnt.Length) throw new InvalidOperationException($"表 {tag} 越界");

                if (remove.Contains(tag)) continue;

                var data = new byte[length];
                Array.Copy(sfnt, offset, data, 0, length);
                kept.Add((tag, data));
            }

            if (!kept.Any(t => t.Tag == "head")) throw new InvalidOperationException("缺少 head 表：不是可用的 TrueType 字体");

            // ---- 重排目录（按 tag 升序）----
            kept.Sort((a, b) => string.CompareOrdinal(a.Tag, b.Tag));

            int count = kept.Count;
            int directorySize = 12 + count * 16;
            int total = directorySize;
            var offsets = new int[count];
            for (int i = 0; i < count; i++)
            {
                offsets[i] = total;
                total += Align4(kept[i].Data.Length);
            }

            var output = new byte[total];

            // ---- 头部 ----
            WriteU32(output, 0, version);
            WriteU16(output, 4, (ushort)count);
            int entrySelector = 0;
            for (int p = 1; p <= count; p <<= 1) entrySelector++;
            entrySelector--;                                  // floor(log2(count))
            if (entrySelector < 0) entrySelector = 0;
            int searchRange = 16 * (1 << entrySelector);
            WriteU16(output, 6, (ushort)searchRange);
            WriteU16(output, 8, (ushort)entrySelector);
            WriteU16(output, 10, (ushort)(count * 16 - searchRange));

            // ---- 表数据 ----
            for (int i = 0; i < count; i++)
            {
                Array.Copy(kept[i].Data, 0, output, offsets[i], kept[i].Data.Length);
            }

            // ---- 目录记录（checksum 里 head 的 checkSumAdjustment 先当 0）----
            int headOffset = -1;
            for (int i = 0; i < count; i++)
            {
                int rec = 12 + i * 16;
                WriteU32(output, rec, TableTags.Make(kept[i].Tag));
                WriteU32(output, rec + 8, (uint)offsets[i]);
                WriteU32(output, rec + 12, (uint)kept[i].Data.Length);

                if (kept[i].Tag == "head") headOffset = offsets[i];

                // head 表的 checksum 规范上要求把 checkSumAdjustment 当作 0 来算
                int checksum = (int)Checksum(output, offsets[i], kept[i].Data.Length, zeroAt: kept[i].Tag == "head" ? 8 : -1);
                WriteU32(output, rec + 4, (uint)checksum);
            }

            // ---- head.checkSumAdjustment：整份文件的 checksum 归到 0xB1B0AFBA ----
            if (headOffset > 0)
            {
                WriteU32(output, headOffset + 8, 0);
                int fileChecksum = (int)Checksum(output, 0, output.Length, zeroAt: -1);
                uint adjustment = unchecked((uint)(0xB1B0AFBAL - fileChecksum));
                WriteU32(output, headOffset + 8, adjustment);
            }

            return output;
        }

        /// <summary>一份字体里有哪些表（报告/断言用）。</summary>
        public static string[] TableTagsOf(byte[] sfnt)
        {
            int numTables = OpenTypeFontData.U16(sfnt, 4);
            var tags = new List<string>();
            for (int i = 0; i < numTables; i++)
            {
                int rec = 12 + i * 16;
                if (rec + 16 > sfnt.Length) break;
                tags.Add(TableTags.ToString(OpenTypeFontData.U32(sfnt, rec)));
            }

            tags.Sort(StringComparer.Ordinal);
            return tags.ToArray();
        }

        /// <summary>把 <paramref name="sfnt"/> 写到 <paramref name="path"/> 并返回落盘字节数。</summary>
        public static int WriteFile(byte[] sfnt, string path)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllBytes(path, sfnt);
            return sfnt.Length;
        }

        // ---------------------------------------------------------------------------------

        private static int Align4(int value) => (value + 3) & ~3;

        private static long Checksum(byte[] data, int offset, int length, int zeroAt)
        {
            long sum = 0;
            int end = offset + Align4(length);            // 尾部补零参与累加（规范如此）

            for (int i = offset; i < end; i += 4)
            {
                uint word = 0;
                for (int b = 0; b < 4; b++)
                {
                    int index = i + b;
                    uint value = 0;
                    if (index < offset + length && index < data.Length) value = data[index];
                    if (zeroAt >= 0 && index >= offset + zeroAt && index < offset + zeroAt + 4) value = 0;
                    word = (word << 8) | value;
                }

                sum += word;
            }

            return sum & 0xFFFFFFFFL;
        }

        private static void WriteU16(byte[] b, int o, ushort v)
        {
            b[o] = (byte)(v >> 8);
            b[o + 1] = (byte)(v & 0xFF);
        }

        private static void WriteU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24);
            b[o + 1] = (byte)((v >> 16) & 0xFF);
            b[o + 2] = (byte)((v >> 8) & 0xFF);
            b[o + 3] = (byte)(v & 0xFF);
        }
    }
}
