// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-presentationcore-compositefont.py **生成**，不要手改。
//
// 内容 = 上游 `upstream/wpf/src/Microsoft.DotNet.Wpf/src/PresentationCore/MS/internal/FontCache/FamilyCollection.cs`
//        逐字复制 + 9 处修改（7 处 M7d 补丁 J：非 Windows 上短路系统复合字体 + 把回退交给 provider；
//        2 处 T1c 方案 A：按码点覆盖感知的回退包装 + 它的两个新类）。
// 每次运行该脚本都会从上游重读重生成；锚点找不到 / 计数不符时**报错退出**，不会静默产出未打补丁的副本。
//
// 为什么要打：
//   `GlobalUserInterface.CompositeFont` 是 4 个系统复合字体里唯一根为 <FontFamilyCollection> 的
//   （4 个 OS 段），`CompositeFontParser.ParseFontFamilyCollectionElement` 按 `OS` 挑段，
//   本移植上 DeviceFamily/IsWindows* 一律 false ⇒ 一段都挑不中 ⇒ Fail(...)
//   ⇒ 消息要 OSVersionHelper.GetOsVersion() ⇒ **抛** "Could not detect OS!"。
//   入口是"任何找不到的字体名"（`FontFamily="Arial"` 这种最常见写法），与字体装没装无关。
// 修法语义：**不伪造 OS 版本**（OS 用来挑平台专属族链表，里面是 Windows 的字体名 ⇒ 谎报 = 造假换安静），
//   而是：① 非 Windows 上系统复合字体一律不加载（短路，连 LoadXml 都不执行）；
//          ② "系统回退族"（#GLOBAL USER INTERFACE，Typeface 的 FallbackFontFamily）改由
//             **provider 的首个可用族**回答 —— 不做 ② 的话 Typeface.cs:786 会解引用 null 抛 NRE
//             （实测见 src/Linux/build/MilBridge/T1-report.md 的 M7d 段；这条链上"诚实失败"被
//              CachedTypeface.cs:42 的 Invariant.Assert 堵死）。
//          ③④ 枚举循环 null 守卫 + FamilyCount 同步（否则尾部 null / Debug.Assert 失败）；
//          ⑤ FindFamily 返回类型 → IFontFamily（② 要返回 PhysicalFontFamily）。
// Windows 分支逐字不变（只在方法/属性最前面加非 Windows 判断，或改私有方法的返回类型）。
//
// 机械证据：生成物里的 `throw` 条数与上游**逐字相同**（脚本每次断言）⇒ 是短路 + 代偿，不是把异常换地方冒。
//
// ⚠️ T1c（方案 A）实测结论：新增的覆盖感知回退（`HbCoverageFallback` / `HbCoverageFallbackFamily`）
//    **当前不在 CJK 的活路上** —— 包装族被创建（`Wrap` 有自报）但它的按区间选族钩子
//    `IFontFamily.GetMapTargetFamilyNameAndScale` **一次都没被问到**（`WrapperMapCalls=0`），
//    因为喂 `TypefaceMap` 的 `GetShapeableText` 在本移植里**没有托管调用者**（LS 已被托管 shim 取代），
//    而 shim 是"一段一个字体文件"（无覆盖回退）。⇒ **不要**把本文件当成"CJK 已修好"。
//    真正的修法在 `src/Microsoft.DotNet.Wpf.Linux/src/shims/PresentationCore.HbTextLine.cs` 的字体解析那一层（别的车道）。
//
// ↓↓↓ 以下为上游原文（仅 9 处标记：7 处 M7d 补丁 J + 2 处 T1c 方案 A）↓↓↓
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
//
// Description: FamilyCollection font cache element class is responsible for
// storing the mapping between a folder and font families in it.
//
//

using System.Collections;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;    // for XmlLanguage
using System.Windows.Media;

using MS.Win32;
using MS.Internal.FontFace;
using MS.Internal.Shaping;

namespace MS.Internal.FontCache
{
    /// <summary>
    /// T1c · 方案 A 的**适配器**：把一个物理族包装成"**按字符区间**做覆盖感知回退"的族。
    ///
    /// 【为什么是包装，而不是在 LookupFamily 里挑族】
    ///   `FamilyCollection.LookupFamily` 的入参**只有族名**（`string familyName`），**看不到该 run 的字符**
    ///   ⇒ 在那一层拿不到"当前族不覆盖哪个码点"。而上游**复合字体协议**里有一处正好带字符区间：
    ///   `IFontFamily.GetMapTargetFamilyNameAndScale(CharacterBufferRange, …)` ——
    ///   `MS.Internal.Shaping.TypefaceMap.MapByFontFamily` 用它决定"这一段字符交给哪个族"，
    ///   再把返回的族名交给 `FamilyCollection.LookupFamily` 解析。
    ///   补丁 J 短路掉的正是**唯一**实现它的上游类型（`CompositeFontFamily`/`CachedCompositeFamily`）
    ///   ⇒ 本类把那件事按**码点覆盖**重做一遍：这就是"按码点/按 run 选面"，且**不动任何行创建代码**
    ///   （`TypographyGate` / `CheckFastPathNominalGlyphs` / LineServices 一个字节都不碰）。
    ///
    /// 【拉丁为什么不会退步】
    ///   除 `GetMapTargetFamilyNameAndScale` 外**全部逐字转发**给今天那个族（度量/基线/行距/字形面/
    ///   名字/Typeface 集合都来自它）⇒ 拉丁的整形、度量、断行仍在**同一条代码路径**上；
    ///   覆盖率判定的粒度是**码点**（区间边界 = 族边界），绝不按段落整体换族。
    /// </summary>
    internal sealed class HbCoverageFallbackFamily : IFontFamily
    {
        private readonly IFontFamily _inner;
        private readonly string     _familyName;      // 本族名（provider 那边的名字；保证能被本集合解析回来）
        private readonly MS.Internal.Text.TextInterface.FontCollection _collection;
        private readonly MS.Internal.Text.TextInterface.Linux.LinuxFontFamily _linuxFamily;

        internal HbCoverageFallbackFamily(
            IFontFamily inner,
            string      familyName,
            MS.Internal.Text.TextInterface.FontCollection collection,
            MS.Internal.Text.TextInterface.Linux.LinuxFontFamily linuxFamily)
        {
            _inner       = inner;
            _familyName  = familyName;
            _collection  = collection;
            _linuxFamily = linuxFamily;
        }

        internal string FamilyName { get { return _familyName; } }
        internal IFontFamily Inner { get { return _inner; } }

        // ── 以下 8 个成员**逐字转发**（一个字节都不改）──
        System.Collections.Generic.IDictionary<XmlLanguage, string> IFontFamily.Names
        {
            get { return ((IFontFamily)_inner).Names; }
        }

        double IFontFamily.Baseline(double emSize, double toReal, double pixelsPerDip, TextFormattingMode textFormattingMode)
        {
            return ((IFontFamily)_inner).Baseline(emSize, toReal, pixelsPerDip, textFormattingMode);
        }

        double IFontFamily.BaselineDesign
        {
            get { return ((IFontFamily)_inner).BaselineDesign; }
        }

        double IFontFamily.LineSpacing(double emSize, double toReal, double pixelsPerDip, TextFormattingMode textFormattingMode)
        {
            return ((IFontFamily)_inner).LineSpacing(emSize, toReal, pixelsPerDip, textFormattingMode);
        }

        double IFontFamily.LineSpacingDesign
        {
            get { return ((IFontFamily)_inner).LineSpacingDesign; }
        }

        ITypefaceMetrics IFontFamily.GetTypefaceMetrics(FontStyle style, FontWeight weight, FontStretch stretch)
        {
            return ((IFontFamily)_inner).GetTypefaceMetrics(style, weight, stretch);
        }

        IDeviceFont IFontFamily.GetDeviceFont(FontStyle style, FontWeight weight, FontStretch stretch)
        {
            return ((IFontFamily)_inner).GetDeviceFont(style, weight, stretch);
        }

        System.Collections.Generic.ICollection<Typeface> IFontFamily.GetTypefaces(FontFamilyIdentifier familyIdentifier)
        {
            return ((IFontFamily)_inner).GetTypefaces(familyIdentifier);
        }

        // ── 唯一被改写的行为：按字符区间决定"这一段交给哪个族" ──
        bool IFontFamily.GetMapTargetFamilyNameAndScale(
            System.Windows.Media.TextFormatting.CharacterBufferRange unicodeString,
            CultureInfo          culture,
            CultureInfo          digitCulture,
            double               defaultSizeInEm,
            out int              cchAdvance,
            out string           targetFamilyName,
            out double           scaleInEm
            )
        {
            // 缺省 = **原样**：交给本族、整段、scale 用调用方给的（= `PhysicalFontFamily` 的语义）。
            // 任何"答不了/查不出/找不到"都落在这里 ⇒ 与今天**逐字同路**：
            //   本族名 ⇒ 机制把整段交给本族 ⇒ `MapByFontFaceFamily`（`firstValidFamily` 记账、
            //   `nextValid`、`MapUnresolvedCharacters` 与不包装时完全相同）。
            cchAdvance       = unicodeString.Length;
            targetFamilyName = _familyName;
            scaleInEm        = defaultSizeInEm;

            HbCoverageFallback.DropStaleAnswer();

            int length = unicodeString.Length;
            HbCoverageFallback.NoteMapCall(length);        // "机制到底有没有问到我"（有界诊断）
            if (length <= 0 || _linuxFamily == null || _collection == null)
            {
                return true;   // 必须 true：返回 false 会让 TypefaceMap 把本类当 `PhysicalFontFamily` 去 cast
            }

            // 1) 找"首个**未被本族覆盖**的码点"（组合符/连接符跟随前一个字符 ⇒ 不单独判，口径见报告）
            int firstUncovered = -1;
            int sizeofChar = 0;
            for (int i = Classification.AdvanceWhile(unicodeString, ItemClass.JoinerClass); i < length; i += sizeofChar)
            {
                int originalChar = Classification.UnicodeScalar(
                    new System.Windows.Media.TextFormatting.CharacterBufferRange(unicodeString, i, length - i),
                    out sizeofChar);

                if (Classification.IsJoiner(originalChar) || Classification.IsCombining(originalChar))
                {
                    continue;
                }

                HbCoverageFallback.Verdict verdict = HbCoverageFallback.QueryFamily(_linuxFamily, originalChar);

                if (verdict == HbCoverageFallback.Verdict.Unknown)
                {
                    // **不确定就交回**：不能把"答不了"当"没有"（本项目在 pid==0 那次立过这条规矩）
                    HbCoverageFallback.NoteUnknownTreatedAsReturn(originalChar);
                    return true;
                }

                if (verdict == HbCoverageFallback.Verdict.NotCovered)
                {
                    firstUncovered = i;
                    break;
                }
            }

            if (firstUncovered < 0)
            {
                HbCoverageFallback.NoteAllCovered(length);
                return true;                       // 整段都被本族覆盖 ⇒ 原样（**拉丁走这条**）
            }

            if (firstUncovered > 0)
            {
                // 首个未覆盖码点**不在区间开头** ⇒ 只把"本族覆盖的那段前缀"交出去（与今天逐字一致）；
                // 剩下的机制会**再问一次**（`TypefaceMap.MapByFontFamily` 的循环）—— 那时它就是区间开头。
                cchAdvance       = firstUncovered;
                targetFamilyName = _familyName;
                scaleInEm        = defaultSizeInEm;
                HbCoverageFallback.SetPendingAnswer(_familyName);
                HbCoverageFallback.NotePrefixSplit(firstUncovered);
                return true;
            }

            // 2) 区间开头就是不覆盖的码点 ⇒ 向 provider（→ 直读字体文件 cmap）要一个**覆盖它的族**
            int uncoveredCodePoint = Classification.UnicodeScalar(
                new System.Windows.Media.TextFormatting.CharacterBufferRange(unicodeString, 0, length),
                out sizeofChar);

            string covering;
            HbCoverageFallback.Verdict found =
                HbCoverageFallback.FindFamilyCovering(_collection, uncoveredCodePoint, out covering);

            if (found != HbCoverageFallback.Verdict.Covered || string.IsNullOrEmpty(covering))
            {
                HbCoverageFallback.NoteFailed(uncoveredCodePoint, found);
                return true;                       // 答不了 ⇒ **保持今天的行为**
            }

            // 3) 只把"从该码点起、连续由该族覆盖"的前缀交出去（**按码点**，绝不按段落整体换族）
            int cch = sizeofChar;
            for (int i = sizeofChar; i < length; )
            {
                int size2;
                int c2 = Classification.UnicodeScalar(
                    new System.Windows.Media.TextFormatting.CharacterBufferRange(unicodeString, i, length - i),
                    out size2);

                if (!Classification.IsJoiner(c2) && !Classification.IsCombining(c2)
                    && HbCoverageFallback.QueryFamilyByName(_collection, covering, c2)
                       != HbCoverageFallback.Verdict.Covered)
                {
                    break;                         // 该族不覆盖 ⇒ 在此断开（区间边界 = 族边界）
                }

                i += size2;
                cch = i;
            }

            cchAdvance       = cch;
            targetFamilyName = covering;
            scaleInEm        = defaultSizeInEm;
            HbCoverageFallback.SetPendingAnswer(covering);
            HbCoverageFallback.NoteApplied(uncoveredCodePoint, covering, cch, length);
            return true;
        }
    }

    /// <summary>
    /// T1c · 方案 A 的**查覆盖 + 计数**（**只做这两件事**）。
    ///
    /// 【覆盖查询的降级链】a. provider 面（**反射**探测；它们是**扩展方法** ⇒ 在 `FamilyCoverageQuery`
    ///   静态类上）→ b. **直读字体文件的 cmap**（"读 cmap"是**读字体**，与"读 PC 的选择"是两件事）。
    ///   两档都答不了 ⇒ `Unknown` ⇒ 调用方**保持今天的行为**并计数（"不确定就交回"）。
    ///
    /// 【缓存】粒度 = **码点**；key 带**集合身份**（`ConditionalWeakTable` 挂在集合实例上 ⇒
    ///   集合不可变则缓存有效，换集合 = 换实例 = 换缓存）。上限 4096 条，满了**整体清空**。
    ///
    /// 【计数器】**缺省关**（`WPF_LINUX_COVERAGE_DIAG` 未设时一次都不加、不分配、不输出），
    ///   独立成类，**绝不碰 `RenderDiagnostics`**，**不进任何 runner 判据**。
    ///
    /// 【开关与**未设时的行为**（本项目栽过"设计说默认开、实现却默认关"）】
    ///   `WPF_LINUX_COVERAGE_FALLBACK`：**未设/空 ⇒ 开**；`0`/`false`/`off`/`no` ⇒ 关。
    ///   `WPF_LINUX_COVERAGE_DIAG`    ：**未设/空 ⇒ 关**；`1`/`true`/`on`/`yes` ⇒ 开。
    ///   两条语义都是**纯函数**（`ParseBehavior`/`ParseDiag`），可被断言直接钉住。
    /// </summary>
    internal static class HbCoverageFallback
    {
        internal const string BehaviorEnv = "WPF_LINUX_COVERAGE_FALLBACK";
        internal const string DiagEnv     = "WPF_LINUX_COVERAGE_DIAG";
        internal const string DumpEnv     = "WPF_LINUX_COVERAGE_DUMP";   // 原始读数落盘（进程被 SIGTERM 也看得见）

        private const int MaxCachedCodePoints = 4096;
        private const int MaxTraceLines       = 40;
        private const int TopCodePoints       = 16;
        private const int MaxCmapFiles        = 64;

        internal enum Verdict
        {
            Unknown    = 0,   // **答不了**（不是"没有"）
            NotCovered = 1,
            Covered    = 2,
        }

        // ── 开关（静态只读；语义由纯函数给定）──
        private static readonly bool s_enabled = ParseBehavior(Environment.GetEnvironmentVariable(BehaviorEnv));
        private static readonly bool s_diag    = ParseDiag(Environment.GetEnvironmentVariable(DiagEnv));

        internal static bool Enabled { get { return s_enabled; } }
        internal static bool Diag    { get { return s_diag; } }

        /// <summary>未设/空白 ⇒ **true（开）**；"0"/"false"/"off"/"no"（不分大小写）⇒ false；其余 ⇒ true。</summary>
        internal static bool ParseBehavior(string value)
        {
            if (string.IsNullOrEmpty(value)) return true;
            string v = value.Trim();
            if (v.Length == 0) return true;
            if (v == "0") return false;
            if (string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(v, "off",   StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(v, "no",    StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        /// <summary>未设/空白 ⇒ **false（关）**；"1"/"true"/"on"/"yes"（不分大小写）⇒ true；其余 ⇒ false。</summary>
        internal static bool ParseDiag(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            string v = value.Trim();
            if (v == "1") return true;
            if (string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(v, "on",   StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(v, "yes",  StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // =================================================================================
        //  包装入口（LookupFamily 的出口调用它）
        // =================================================================================

        /// <summary>把一个刚解析出来的物理族包成覆盖感知族。**任何不确定 ⇒ 原样返回**（今天的族）。</summary>
        internal static IFontFamily Wrap(IFontFamily inner,
                                         MS.Internal.Text.TextInterface.FontCollection collection,
                                         MS.Internal.Text.TextInterface.FontFamily providerFamily)
        {
            if (s_diag)
            {
                ++s_wrapCalls;
                ReportOnce();                              // 首次自报**开关语义 + 探测原文**（有界：只 1 行）
            }

            if (inner == null) return null;
            if (!s_enabled) return inner;                  // 关掉开关 ⇒ **连包装都不做**（A/B 的"关"档）

            PhysicalFontFamily physical = inner as PhysicalFontFamily;
            if (physical == null) return inner;            // 只包物理族

            MS.Internal.Text.TextInterface.Linux.LinuxFontFamily linuxFamily = null;

            try
            {
                if (providerFamily != null) linuxFamily = providerFamily.LinuxFamily;
            }
            catch (Exception)
            {
            }

            if (linuxFamily == null)
            {
                if (s_diag) { ++s_noProviderFamily; }
                return inner;                              // 拿不到 provider 族 ⇒ 查不了覆盖 ⇒ 原样（今天的行为）
            }

            string name = linuxFamily.FamilyName;

            if (TryConsumePendingAnswer(name))
            {
                return inner;                              // 这次解析是**我们自己刚给出的答案** ⇒ 原样（防递归）
            }

            if (s_diag) RegisterExitDumpOnce();

            return new HbCoverageFallbackFamily(inner, name, collection, linuxFamily);
        }

        // =================================================================================
        //  覆盖查询
        // =================================================================================

        /// <summary>该族是否覆盖该码点（三态）。</summary>
        internal static Verdict QueryFamily(MS.Internal.Text.TextInterface.Linux.LinuxFontFamily family, int codePoint)
        {
            if (family == null) return Verdict.Unknown;
            if (codePoint < 0 || codePoint > 0x10FFFF) return Verdict.Unknown;   // 非法码点：答不了，不当"没有"

            if (s_diag) { ++s_coverageProbe; }

            PerCollection cache = GetCache(family.FontsCollection);
            string familyName = family.FamilyName;

            if (cache != null)
            {
                System.Collections.Generic.Dictionary<int, Verdict> perFamily;
                if (cache.FamilyCoverage.TryGetValue(familyName, out perFamily))
                {
                    Verdict cached;
                    if (perFamily.TryGetValue(codePoint, out cached))
                    {
                        if (s_diag) { ++s_cacheHit; }
                        return cached;
                    }
                }
                if (s_diag) { ++s_cacheMiss; }
            }

            EnsureProbe();

            Verdict viaProvider = Verdict.Unknown;

            if (s_queryOnFamily != null || s_coversOnFamily != null)
            {
                viaProvider = InvokeFamilyQuery(family, codePoint);
                if (s_diag) { ++s_providerQueries; }
            }

            Verdict verdict = viaProvider;

            if (verdict == Verdict.Unknown)
            {
                // 降级链 b：**直接读字体文件的 cmap**
                Verdict viaCmap = QueryFamilyByCmap(family, codePoint);
                if (s_diag) { ++s_cmapQueries; }
                if (viaCmap != Verdict.Unknown) verdict = viaCmap;

                // 交叉自检（只在诊断开着时做）：两条链结论不同 ⇒ 计数（"仪器自己会不会撒谎"的检查）
                if (s_diag && viaCmap != Verdict.Unknown && viaProvider != Verdict.Unknown && viaCmap != viaProvider)
                {
                    ++s_cmapDisagrees;
                }
            }

            if (cache != null && !string.IsNullOrEmpty(familyName))
            {
                System.Collections.Generic.Dictionary<int, Verdict> perFamily;
                if (!cache.FamilyCoverage.TryGetValue(familyName, out perFamily))
                {
                    perFamily = new System.Collections.Generic.Dictionary<int, Verdict>();
                    cache.FamilyCoverage[familyName] = perFamily;
                }

                if (!perFamily.ContainsKey(codePoint)) NoteCachedEntry();
                perFamily[codePoint] = verdict;
            }

            return verdict;
        }

        /// <summary>按族名查覆盖（"前缀延伸"时判同一族是否继续覆盖）。</summary>
        internal static Verdict QueryFamilyByName(
            MS.Internal.Text.TextInterface.FontCollection collection, string familyName, int codePoint)
        {
            MS.Internal.Text.TextInterface.Linux.LinuxFontCollection linuxCollection =
                collection == null ? null : collection.LinuxCollection;

            if (linuxCollection == null || string.IsNullOrEmpty(familyName)) return Verdict.Unknown;

            MS.Internal.Text.TextInterface.Linux.LinuxFontFamily family = linuxCollection[familyName];
            if (family == null) return Verdict.Unknown;

            return QueryFamily(family, codePoint);
        }

        /// <summary>在集合里找**第一个**覆盖该码点的族（provider 集合级面优先；否则逐族问）。</summary>
        internal static Verdict FindFamilyCovering(
            MS.Internal.Text.TextInterface.FontCollection collection, int codePoint, out string familyName)
        {
            familyName = null;

            MS.Internal.Text.TextInterface.Linux.LinuxFontCollection linuxCollection =
                collection == null ? null : collection.LinuxCollection;

            if (linuxCollection == null)
            {
                if (s_diag) { ++s_noProviderCollection; }
                return Verdict.Unknown;
            }

            if (codePoint < 0 || codePoint > 0x10FFFF) return Verdict.Unknown;

            PerCollection cache = GetCache(linuxCollection);

            Verdict verdict = Verdict.Unknown;
            string found = null;

            if (cache != null)
            {
                string cachedName;
                if (cache.CoveringFamily.TryGetValue(codePoint, out cachedName))
                {
                    if (s_diag) { ++s_cacheHit; }
                    familyName = cachedName;
                    return Verdict.Covered;
                }

                Verdict cachedVerdict;
                if (cache.CoveringVerdict.TryGetValue(codePoint, out cachedVerdict))
                {
                    if (s_diag) { ++s_cacheHit; }
                    return cachedVerdict;
                }

                if (s_diag) { ++s_cacheMiss; }
            }

            EnsureProbe();

            // ① provider 的**集合级**面（三态语义由 provider 定义：全答"没有" ⇒ NotCovered，有"答不了" ⇒ Unknown）
            if (s_queryOnCollection != null)
            {
                try
                {
                    object[] args = new object[] { linuxCollection, codePoint, null };
                    object result = s_queryOnCollection.Invoke(null, args);
                    if (result != null)
                    {
                        string name = result.ToString();     // 按**枚举名**读，对枚举重排免疫
                        familyName = null;

                        if (name == "Covered")
                        {
                            MS.Internal.Text.TextInterface.Linux.LinuxFontFamily family =
                                args[2] as MS.Internal.Text.TextInterface.Linux.LinuxFontFamily;
                            if (family != null) { verdict = Verdict.Covered; found = family.FamilyName; }
                        }
                        else if (name == "NotCovered")
                        {
                            verdict = Verdict.NotCovered;
                        }
                        else
                        {
                            verdict = Verdict.Unknown;
                        }

                        if (s_diag) { ++s_providerCollectionFace; }
                    }
                }
                catch (Exception e)
                {
                    verdict = Verdict.Unknown;
                    if (s_diag) { s_providerCollectionFaceError = e.GetType().Name; }
                }
            }

            if (verdict != Verdict.Covered && s_queryOnCollection == null)
            {
                // ② 逐族问（"遍历 Families + QueryCoverage/Covers(cp)"，反射面最小、语义最直白）
                try
                {
                    System.Collections.Generic.IReadOnlyList<
                        MS.Internal.Text.TextInterface.Linux.LinuxFontFamily> families = linuxCollection.Families;

                    bool anyAnswered = false;
                    bool anyUnknown = false;

                    for (int i = 0; i < families.Count; i++)
                    {
                        MS.Internal.Text.TextInterface.Linux.LinuxFontFamily family = families[i];
                        if (family == null) continue;

                        Verdict v = QueryFamily(family, codePoint);
                        if (v == Verdict.Covered) { verdict = Verdict.Covered; found = family.FamilyName; break; }
                        if (v == Verdict.NotCovered) anyAnswered = true;
                        if (v == Verdict.Unknown) anyUnknown = true;
                    }

                    if (verdict != Verdict.Covered)
                    {
                        verdict = anyUnknown ? Verdict.Unknown
                                : (anyAnswered ? Verdict.NotCovered : Verdict.Unknown);
                    }

                    if (s_diag) { ++s_providerLoopFace; }
                }
                catch (Exception)
                {
                    verdict = Verdict.Unknown;
                }
            }

            if (verdict == Verdict.Covered) familyName = found;

            if (cache != null)
            {
                if (verdict == Verdict.Covered) cache.CoveringFamily[codePoint] = found;
                else cache.CoveringVerdict[codePoint] = verdict;

                NoteCachedEntry();
            }

            return verdict;
        }

        // =================================================================================
        //  provider 能力探测（一次，结果缓存；读数是"探测到了什么、按什么签名"）
        // =================================================================================

        private static bool s_probeDone;
        private static System.Reflection.MethodInfo s_queryOnFamily;      // static FamilyCoverage QueryCoverage(LinuxFontFamily, int)
        private static System.Reflection.MethodInfo s_coversOnFamily;     // static bool Covers(LinuxFontFamily, int)
        private static System.Reflection.MethodInfo s_queryOnCollection;  // static FamilyCoverage QueryCoverage(LinuxFontCollection, int, out LinuxFontFamily)
        private static System.Reflection.MethodInfo s_findCovering;       // static bool TryFindFamilyCovering(LinuxFontCollection, int, out LinuxFontFamily)
        private static string s_probeReading = "未探测";
        private static string s_providerCollectionFaceError;

        internal static string ProbeReading { get { EnsureProbe(); return s_probeReading; } }

        private static void EnsureProbe()
        {
            if (s_probeDone) return;
            s_probeDone = true;

            try
            {
                System.Type familyType = typeof(MS.Internal.Text.TextInterface.Linux.LinuxFontFamily);
                System.Type collectionType = typeof(MS.Internal.Text.TextInterface.Linux.LinuxFontCollection);
                System.Reflection.Assembly asm = familyType.Assembly;

                // ⚠️ provider 的覆盖查询是**扩展方法** ⇒ 反射里它们出现在**静态类** `FamilyCoverageQuery` 上，
                //    **不是** `LinuxFontFamily`/`LinuxFontCollection` 上的实例方法。按实例方法探测会**永远找不到**
                //    ⇒ 错误地判定"provider 没有能力" ⇒ 永远走 cmap 降级链，而**所有读数都是绿的**（最坏的假绿）。
                //    这里把两种探测的**结论都记进读数**。
                System.Type query = asm.GetType("MS.Internal.Text.TextInterface.Linux.FamilyCoverageQuery", false);

                System.Reflection.MethodInfo instanceCovers = familyType.GetMethod(
                    "Covers", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                    null, new System.Type[] { typeof(int) }, null);

                if (query == null)
                {
                    s_probeReading = "providerFace=**缺**（程序集 " + asm.GetName().Name
                        + " 里没有 MS.Internal.Text.TextInterface.Linux.FamilyCoverageQuery）"
                        + "；typeof(LinuxFontFamily).GetMethod(Covers)=" + (instanceCovers == null ? "无" : "有")
                        + " ⇒ **降级链 b：直读字体文件 cmap**";
                    return;
                }

                s_queryOnFamily = FindStatic(query, "QueryCoverage", null,
                    new System.Type[] { familyType, typeof(int) });

                s_coversOnFamily = FindStatic(query, "Covers", typeof(bool),
                    new System.Type[] { familyType, typeof(int) });

                s_queryOnCollection = FindStatic(query, "QueryCoverage", null,
                    new System.Type[] { collectionType, typeof(int), familyType.MakeByRefType() });

                s_findCovering = FindStatic(query, "TryFindFamilyCovering", typeof(bool),
                    new System.Type[] { collectionType, typeof(int), familyType.MakeByRefType() });

                s_probeReading = "providerFace=" + query.FullName + "@" + asm.GetName().Name
                    + "；QueryCoverage(LinuxFontFamily,int)->FamilyCoverage=" + (s_queryOnFamily != null ? "有" : "无")
                    + "；Covers(LinuxFontFamily,int)->bool=" + (s_coversOnFamily != null ? "有" : "无")
                    + "；QueryCoverage(LinuxFontCollection,int,out LinuxFontFamily)->FamilyCoverage="
                    + (s_queryOnCollection != null ? "有" : "无")
                    + "；TryFindFamilyCovering(LinuxFontCollection,int,out LinuxFontFamily)->bool="
                    + (s_findCovering != null ? "有" : "无（只报，不调：集合级 QueryCoverage 语义更全）")
                    + "；typeof(LinuxFontFamily).GetMethod(Covers)="
                    + (instanceCovers == null ? "无（它是扩展方法，本就在静态类上）" : "有")
                    + "；决策="
                    + (s_queryOnCollection != null
                        ? "集合级 QueryCoverage（三态：Unknown 不当成没有）"
                        : (s_queryOnFamily != null
                            ? "逐个族 QueryCoverage（三态）"
                            : (s_coversOnFamily != null
                                ? "逐个族 Covers（两态；provider 的 Unknown 会退化成 NotCovered，已计数）"
                                : "无 provider 面 ⇒ 降级链 b：直读字体文件 cmap")));
            }
            catch (Exception e)
            {
                s_probeReading = "providerFace=探测抛 " + e.GetType().Name + ": " + e.Message
                    + " ⇒ 降级链 b：直读字体文件 cmap";
            }
        }

        private static System.Reflection.MethodInfo FindStatic(
            System.Type type, string name, System.Type returnType, System.Type[] parameterTypes)
        {
            try
            {
                System.Reflection.MethodInfo[] methods = type.GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

                for (int i = 0; i < methods.Length; i++)
                {
                    System.Reflection.MethodInfo m = methods[i];
                    if (m.Name != name) continue;
                    if (returnType != null && m.ReturnType != returnType) continue;

                    System.Reflection.ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != parameterTypes.Length) continue;

                    bool match = true;
                    for (int j = 0; j < ps.Length; j++)
                    {
                        System.Type want = parameterTypes[j];
                        System.Type got = ps[j].ParameterType;

                        if (want.IsByRef)
                        {
                            if (!got.IsByRef || got.GetElementType() != want.GetElementType()) { match = false; break; }
                        }
                        else if (got != want) { match = false; break; }
                    }

                    if (match) return m;
                }
            }
            catch (Exception)
            {
            }

            return null;
        }

        /// <summary>调 provider 的三态查询；`Covers`（两态）是退路。任何失败/答不了 ⇒ Unknown。</summary>
        private static Verdict InvokeFamilyQuery(MS.Internal.Text.TextInterface.Linux.LinuxFontFamily family, int codePoint)
        {
            try
            {
                if (s_queryOnFamily != null)
                {
                    object result = s_queryOnFamily.Invoke(null, new object[] { family, codePoint });
                    if (result == null) return Verdict.Unknown;

                    string name = result.ToString();     // 按**枚举名**读，不按数值
                    if (name == "Covered")    return Verdict.Covered;
                    if (name == "NotCovered") return Verdict.NotCovered;
                    return Verdict.Unknown;
                }

                if (s_coversOnFamily != null)
                {
                    object result = s_coversOnFamily.Invoke(null, new object[] { family, codePoint });
                    if (result is bool) return (bool)result ? Verdict.Covered : Verdict.NotCovered;
                }
            }
            catch (Exception)
            {
                if (s_diag) { ++s_providerInvokeErrors; }
            }

            return Verdict.Unknown;
        }

        // =================================================================================
        //  降级链 b：直读字体文件的 cmap（**读字体**，与"读 PC 的选择"是两件事）
        // =================================================================================

        private sealed class Cmap
        {
            internal int Format;
            internal int[] Starts;
            internal int[] Ends;
            internal int[] Deltas;
            internal int[] RangeOffsets;
            internal int RangeOffsetBase;
            internal int[] StartGlyphs;
            internal byte[] Data;

            internal bool Covers(int codePoint)
            {
                if (Starts == null || Starts.Length == 0) return false;

                int lo = 0, hi = Starts.Length - 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    if (codePoint < Starts[mid]) hi = mid - 1;
                    else if (codePoint > Ends[mid]) lo = mid + 1;
                    else
                    {
                        if (Format == 12)
                        {
                            long glyph = (long)StartGlyphs[mid] + (codePoint - Starts[mid]);
                            return glyph != 0;
                        }
                        return GlyphId(codePoint, mid) != 0;
                    }
                }

                return false;
            }

            private int GlyphId(int codePoint, int segment)
            {
                if (Deltas == null || Data == null) return 0;

                int delta = Deltas[segment];

                if (RangeOffsets[segment] == 0)
                {
                    return (codePoint + delta) & 0xFFFF;
                }

                int index = RangeOffsetBase + segment * 2 + RangeOffsets[segment] + (codePoint - Starts[segment]) * 2;
                if (index < 0 || index + 2 > Data.Length) return 0;

                int glyph = (Data[index] << 8) | Data[index + 1];
                if (glyph == 0) return 0;

                return (glyph + delta) & 0xFFFF;
            }
        }

        private static readonly System.Collections.Generic.Dictionary<string, Cmap> s_cmapCache =
            new System.Collections.Generic.Dictionary<string, Cmap>(StringComparer.Ordinal);

        private static Verdict QueryFamilyByCmap(
            MS.Internal.Text.TextInterface.Linux.LinuxFontFamily family, int codePoint)
        {
            System.Collections.Generic.IReadOnlyList<
                MS.Internal.Text.TextInterface.Linux.FontFaceEntry> faces = family.Faces;

            if (faces == null || faces.Count == 0) return Verdict.Unknown;

            bool anyAnswered = false;

            for (int i = 0; i < faces.Count; i++)
            {
                MS.Internal.Text.TextInterface.Linux.FontFaceEntry face = faces[i];
                if (face == null || string.IsNullOrEmpty(face.FilePath)) continue;

                Cmap cmap = GetCmap(face.FilePath, face.FaceIndex);
                if (cmap == null) continue;      // 这面读不了 ⇒ 答不了（**不当"没有"**）

                anyAnswered = true;
                if (cmap.Covers(codePoint)) return Verdict.Covered;
            }

            return anyAnswered ? Verdict.NotCovered : Verdict.Unknown;
        }

        private static Cmap GetCmap(string path, int faceIndex)
        {
            string key = path + "|" + faceIndex.ToString(CultureInfo.InvariantCulture);

            Cmap cached;
            if (s_cmapCache.TryGetValue(key, out cached))
            {
                if (s_diag) { ++s_cacheHit; }
                return cached;
            }

            if (s_diag) { ++s_cacheMiss; }

            if (s_cmapCache.Count >= MaxCmapFiles)
            {
                s_cmapCache.Clear();      // 满了**整体清空**（与码点缓存同款策略）
                if (s_diag) { ++s_cacheCleared; }
            }

            Cmap cmap = null;

            try
            {
                byte[] data = File.ReadAllBytes(path);
                cmap = ParseCmap(data, faceIndex);
                if (s_diag) { ++s_cmapFilesRead; }
            }
            catch (Exception)
            {
                cmap = null;
            }

            s_cmapCache[key] = cmap;
            return cmap;
        }

        /// <summary>最小 SFNT/TTC cmap 读取（format 4 / format 12）。读不了 ⇒ null（**不抛**）。</summary>
        private static Cmap ParseCmap(byte[] data, int faceIndex)
        {
            if (data == null || data.Length < 12) return null;

            int sfnt = 0;

            if (data[0] == (byte)'t' && data[1] == (byte)'t' && data[2] == (byte)'c' && data[3] == (byte)'f')
            {
                int numFonts = ReadU32(data, 8);
                if (numFonts <= 0) return null;
                if (faceIndex < 0 || faceIndex >= numFonts) faceIndex = 0;
                sfnt = ReadU32(data, 12 + 4 * faceIndex);
                if (sfnt <= 0 || sfnt + 12 > data.Length) return null;
            }

            bool sfntVersion = data[sfnt] == 0 && data[sfnt + 1] == 1 && data[sfnt + 2] == 0 && data[sfnt + 3] == 0;
            bool otto = data[sfnt] == (byte)'O' && data[sfnt + 1] == (byte)'T'
                     && data[sfnt + 2] == (byte)'T' && data[sfnt + 3] == (byte)'O';
            if (!sfntVersion && !otto) return null;

            int numTables = ReadU16(data, sfnt + 4);
            int cmapOffset = -1;

            for (int i = 0; i < numTables; i++)
            {
                int rec = sfnt + 12 + 16 * i;
                if (rec + 16 > data.Length) break;

                if (data[rec] == (byte)'c' && data[rec + 1] == (byte)'m'
                    && data[rec + 2] == (byte)'a' && data[rec + 3] == (byte)'p')
                {
                    cmapOffset = ReadU32(data, rec + 8);
                    break;
                }
            }

            if (cmapOffset <= 0 || cmapOffset + 4 > data.Length) return null;

            int subTables = ReadU16(data, cmapOffset + 2);
            int bestFormat12 = -1, bestFormat4 = -1, bestRank12 = -1, bestRank4 = -1;

            for (int i = 0; i < subTables; i++)
            {
                int rec = cmapOffset + 4 + 8 * i;
                if (rec + 8 > data.Length) break;

                int platform = ReadU16(data, rec);
                int encoding = ReadU16(data, rec + 2);
                int sub = cmapOffset + ReadU32(data, rec + 4);
                if (sub <= 0 || sub + 2 > data.Length) continue;

                int format = ReadU16(data, sub);

                // 子表优选序（与 Skia/常见实现同向：完整 Unicode 优先）：
                //   (3,10) > (0,4)/(0,6) > (3,1) > (0,3)/(0,0..2)
                if (format == 12)
                {
                    int rank = (platform == 3 && encoding == 10) ? 4
                             : (platform == 0 && (encoding == 4 || encoding == 6)) ? 3
                             : (platform == 3 && encoding == 1) ? 2
                             : (platform == 0) ? 1 : 0;
                    if (rank > bestRank12) { bestRank12 = rank; bestFormat12 = sub; }
                }
                else if (format == 4)
                {
                    int rank = (platform == 3 && encoding == 1) ? 4
                             : (platform == 0 && encoding == 3) ? 3
                             : (platform == 0) ? 2
                             : (platform == 3) ? 1 : 0;
                    if (rank > bestRank4) { bestRank4 = rank; bestFormat4 = sub; }
                }
            }

            if (bestFormat12 >= 0) return ParseCmapFormat12(data, bestFormat12);
            if (bestFormat4 >= 0)  return ParseCmapFormat4(data, bestFormat4);

            return null;
        }

        private static Cmap ParseCmapFormat12(byte[] data, int offset)
        {
            if (offset + 16 > data.Length) return null;

            int groups = ReadU32(data, offset + 12);
            if (groups <= 0 || groups > 0x100000) return null;
            if (offset + 16 + groups * 12 > data.Length) return null;

            int[] starts = new int[groups];
            int[] ends = new int[groups];
            int[] glyphs = new int[groups];

            for (int i = 0; i < groups; i++)
            {
                int g = offset + 16 + 12 * i;
                starts[i] = ReadU32(data, g);
                ends[i]   = ReadU32(data, g + 4);
                glyphs[i] = ReadU32(data, g + 8);
            }

            Cmap cmap = new Cmap();
            cmap.Format = 12;
            cmap.Starts = starts;
            cmap.Ends = ends;
            cmap.StartGlyphs = glyphs;
            cmap.Data = data;
            return cmap;
        }

        private static Cmap ParseCmapFormat4(byte[] data, int offset)
        {
            if (offset + 14 > data.Length) return null;

            int length = ReadU16(data, offset + 2);
            int segCountX2 = ReadU16(data, offset + 6);
            int segCount = segCountX2 / 2;

            if (segCount <= 0 || segCount > 0x8000) return null;
            if (offset + length > data.Length) return null;
            if (offset + 16 + segCount * 8 > data.Length) return null;

            int endBase = offset + 14;
            int startBase = endBase + segCountX2 + 2;
            int deltaBase = startBase + segCountX2;
            int rangeBase = deltaBase + segCountX2;

            int[] starts = new int[segCount];
            int[] ends = new int[segCount];
            int[] deltas = new int[segCount];
            int[] ranges = new int[segCount];

            for (int i = 0; i < segCount; i++)
            {
                ends[i]   = ReadU16(data, endBase + i * 2);
                starts[i] = ReadU16(data, startBase + i * 2);
                deltas[i] = (short)ReadU16(data, deltaBase + i * 2);
                ranges[i] = ReadU16(data, rangeBase + i * 2);
            }

            Cmap cmap = new Cmap();
            cmap.Format = 4;
            cmap.Starts = starts;
            cmap.Ends = ends;
            cmap.Deltas = deltas;
            cmap.RangeOffsets = ranges;
            cmap.RangeOffsetBase = rangeBase;
            cmap.Data = data;
            return cmap;
        }

        private static int ReadU16(byte[] data, int index)
        {
            return (data[index] << 8) | data[index + 1];
        }

        private static int ReadU32(byte[] data, int index)
        {
            return (data[index] << 24) | (data[index + 1] << 16) | (data[index + 2] << 8) | data[index + 3];
        }

        // =================================================================================
        //  按码点缓存（key 带**集合身份**）+ "pending answer"（防递归）+ 计数器
        // =================================================================================

        private sealed class PerCollection
        {
            internal readonly System.Collections.Generic.Dictionary<
                string, System.Collections.Generic.Dictionary<int, Verdict>> FamilyCoverage =
                new System.Collections.Generic.Dictionary<
                    string, System.Collections.Generic.Dictionary<int, Verdict>>(StringComparer.OrdinalIgnoreCase);

            internal readonly System.Collections.Generic.Dictionary<int, string> CoveringFamily =
                new System.Collections.Generic.Dictionary<int, string>();

            internal readonly System.Collections.Generic.Dictionary<int, Verdict> CoveringVerdict =
                new System.Collections.Generic.Dictionary<int, Verdict>();
        }

        private static System.Runtime.CompilerServices.ConditionalWeakTable<
            MS.Internal.Text.TextInterface.Linux.LinuxFontCollection, PerCollection> s_perCollection =
            new System.Runtime.CompilerServices.ConditionalWeakTable<
                MS.Internal.Text.TextInterface.Linux.LinuxFontCollection, PerCollection>();

        private static int s_cachedEntries;

        private static PerCollection GetCache(MS.Internal.Text.TextInterface.Linux.LinuxFontCollection collection)
        {
            if (collection == null) return null;

            PerCollection cache;
            if (!s_perCollection.TryGetValue(collection, out cache))
            {
                cache = new PerCollection();
                s_perCollection.Add(collection, cache);
            }

            return cache;
        }

        private static void NoteCachedEntry()
        {
            if (++s_cachedEntries > MaxCachedCodePoints)
            {
                // 满了 ⇒ **整体清空**（新表按需重建；集合身份随 key 一起作废）
                s_perCollection = new System.Runtime.CompilerServices.ConditionalWeakTable<
                    MS.Internal.Text.TextInterface.Linux.LinuxFontCollection, PerCollection>();
                s_cachedEntries = 0;
                if (s_diag) { ++s_cacheCleared; }
            }
        }

        // ---- "这次解析是我自己刚给出的答案"（一次性；防 LookupFamily 递归）----
        [ThreadStatic] private static string t_pendingAnswer;

        internal static void SetPendingAnswer(string familyName)
        {
            t_pendingAnswer = familyName;
        }

        internal static void DropStaleAnswer()
        {
            if (t_pendingAnswer != null)
            {
                t_pendingAnswer = null;
                if (s_diag) { ++s_answerNotResolved; }
            }
        }

        private static bool TryConsumePendingAnswer(string familyName)
        {
            if (t_pendingAnswer == null || string.IsNullOrEmpty(familyName)) return false;

            string pending = t_pendingAnswer;
            if (!string.Equals(pending.Trim(), familyName.Trim(), StringComparison.OrdinalIgnoreCase)) return false;

            t_pendingAnswer = null;
            return true;
        }

        // ---- 计数器（**缺省关**：Diag 为假时一次都不加）----
        private static int s_coverageProbe;              // CoverageProbe
        private static int s_cacheHit;                   // CacheHit
        private static int s_cacheMiss;                  // CacheMiss
        private static int s_cacheCleared;               // CacheCleared            （extra）
        private static int s_applied;                    // FallbackApplied
        private static int s_failed;                     // FallbackFailed
        private static int s_capabilityMissing;          // ProviderCapabilityMissing
        private static int s_unknownTreatedAsReturn;     // ProviderUnknownTreatedAsReturn （extra）
        private static int s_noProviderCollection;       // NoProviderCollection    （extra）
        private static int s_noProviderFamily;           // NoProviderFamily        （extra）
        private static int s_providerQueries;            // ProviderQueries         （extra）
        private static int s_providerCollectionFace;     // ProviderCollectionFace  （extra）
        private static int s_providerLoopFace;           // ProviderLoopFace        （extra）
        private static int s_providerInvokeErrors;       // ProviderInvokeErrors    （extra）
        private static int s_cmapQueries;                // CmapQueries             （extra）
        private static int s_cmapFilesRead;              // CmapFilesRead           （extra）
        private static int s_cmapDisagrees;              // CmapDisagreesWithProvider（extra）
        private static int s_prefixSplit;                // PrefixSplit             （extra）
        private static int s_answerNotResolved;          // AnswerNotResolved       （extra）
        // 注：`s_providerCollectionFaceError` 的声明在**探测那一节**（只此一处，重复声明会 CS0102）

        private static readonly System.Collections.Generic.Dictionary<string, int> s_targets =
            new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);

        private static readonly System.Collections.Generic.Dictionary<int, int> s_appliedCodePoints =
            new System.Collections.Generic.Dictionary<int, int>();

        private static bool s_exitDumpRegistered;
        private static bool s_reported;
        private static int s_wrapCalls;                  // WrapCalls（extra：LookupFamily 出口被问了几次）
        private static int s_mapCalls;                   // WrapperMapCalls（extra：机制问了包装族几次）
        private static int s_allCovered;                  // AllCoveredRanges（extra）

        /// <summary>
        /// 首次包装时**自报**（仪器的自我声明）：开关的**未设语义**、探测到的 provider 能力**原文**。
        /// 只打一行（有界）⇒ 不会污染读数；`WPF_LINUX_COVERAGE_DIAG` 未设时**什么都不做**。
        /// </summary>
        private static void ReportOnce()
        {
            if (s_reported) return;
            s_reported = true;

            try
            {
                Console.Error.WriteLine(
                    "[COVERAGE_FALLBACK] 自报: " + BehaviorEnv + "="
                    + (Environment.GetEnvironmentVariable(BehaviorEnv) ?? "<未设>")
                    + " ⇒ Enabled=" + (s_enabled ? "true（**未设时就是开**）" : "false（关：连包装都不做 ⇒ 行为=接线前）")
                    + " ; " + DiagEnv + "="
                    + (Environment.GetEnvironmentVariable(DiagEnv) ?? "<未设>")
                    + " ⇒ Diag=" + (s_diag ? "true" : "false（**缺省关**）")
                    + " ; Probe: " + ProbeReading);

                Dump();     // 第一行自报就把原始读数落盘（含"未设时的行为"）
            }
            catch (Exception)
            {
            }
        }

        internal static void NoteMapCall(int rangeLength)
        {
            if (!s_diag) return;
            ++s_mapCalls;
            if (s_mapCalls <= 8)
            {
                Trace("MapCall#" + s_mapCalls + " rangeLen="
                      + rangeLength.ToString(CultureInfo.InvariantCulture));
            }
        }

        internal static void NoteAllCovered(int rangeLength)
        {
            if (!s_diag) return;
            ++s_allCovered;
            if (s_allCovered <= 8)
            {
                Trace("AllCovered rangeLen=" + rangeLength.ToString(CultureInfo.InvariantCulture)
                      + "（本族覆盖整段 ⇒ 原样交给本族）");
            }
        }

        internal static void NoteUnknownTreatedAsReturn(int codePoint)
        {
            if (!s_diag) return;
            ++s_unknownTreatedAsReturn;
            if (s_unknownTreatedAsReturn <= 8)
            {
                Trace("UnknownTreatedAsReturn cp=U+" + codePoint.ToString("X4")
                      + "（**答不了 ⇒ 交回今天的族**，不当成\"没有\"）");
            }
        }

        internal static void NotePrefixSplit(int length)
        {
            if (!s_diag) return;
            ++s_prefixSplit;
            if (s_prefixSplit <= 8)
            {
                Trace("PrefixSplit cch=" + length.ToString(CultureInfo.InvariantCulture)
                      + "（首个未覆盖码点不在区间开头 ⇒ 只交出前缀）");
            }
        }

        internal static void NoteFailed(int codePoint, Verdict verdict)
        {
            if (!s_diag) return;
            ++s_failed;
            if (verdict == Verdict.Unknown) ++s_capabilityMissing;
            Trace("FallbackFailed cp=U+" + codePoint.ToString("X4") + " verdict=" + verdict.ToString());
        }

        internal static void NoteApplied(int codePoint, string target, int cch, int rangeLength)
        {
            if (!s_diag) return;
            ++s_applied;

            int n;
            s_targets.TryGetValue(target, out n);
            s_targets[target] = n + 1;

            s_appliedCodePoints.TryGetValue(codePoint, out n);
            s_appliedCodePoints[codePoint] = n + 1;

            Trace("FallbackApplied cp=U+" + codePoint.ToString("X4") + " target=" + target
                  + " cch=" + cch.ToString(CultureInfo.InvariantCulture)
                  + " rangeLen=" + rangeLength.ToString(CultureInfo.InvariantCulture));
        }

        private static void Trace(string what)
        {
            if (!s_diag) return;
            if (s_traceLines >= MaxTraceLines) return;

            ++s_traceLines;
            string line = "[COVERAGE_FALLBACK] " + what + " | " + ReadingsOneLine();

            try { Console.Error.WriteLine(line); } catch (Exception) { }

            Dump();
        }

        /// <summary>
        /// 原始读数**落盘**（`WPF_LINUX_COVERAGE_DUMP=<路径>`；未设 ⇒ 不落盘、不分配）。
        /// 为什么需要：应用是被 `SIGTERM` 收尾的，`ProcessExit` 汇总未必有机会跑
        /// （实测：本装置下没跑）⇒ "读数只在退出时打"会变成"永远看不到读数"。
        /// 每次事件 + 首次自报都刷新一遍 ⇒ 被 SIGTERM 也看得见（与 shim 的 DUMP 同款做法）。
        /// </summary>
        private static void Dump()
        {
            string path = Environment.GetEnvironmentVariable(DumpEnv);
            if (string.IsNullOrEmpty(path)) return;

            try { File.WriteAllText(path, Readings()); } catch (Exception) { }
        }

        private static int s_traceLines;

        private static string ReadingsOneLine()
        {
            return "CoverageProbe=" + s_coverageProbe
                 + " CacheHit=" + s_cacheHit
                 + " CacheMiss=" + s_cacheMiss
                 + " CacheCleared=" + s_cacheCleared
                 + " FallbackApplied=" + s_applied
                 + " FallbackTarget(不同族数)=" + s_targets.Count
                 + " FallbackFailed=" + s_failed
                 + " ProviderCapabilityMissing=" + s_capabilityMissing
                 + " ProviderUnknownTreatedAsReturn=" + s_unknownTreatedAsReturn
                 + " ProviderQueries=" + s_providerQueries
                 + " ProviderCollectionFace=" + s_providerCollectionFace
                 + " ProviderLoopFace=" + s_providerLoopFace
                 + " ProviderInvokeErrors=" + s_providerInvokeErrors
                 + " ProviderCollectionFaceError=" + (s_providerCollectionFaceError ?? "-")
                 + " NoProviderCollection=" + s_noProviderCollection
                 + " NoProviderFamily=" + s_noProviderFamily
                 + " CmapQueries=" + s_cmapQueries
                 + " CmapFilesRead=" + s_cmapFilesRead
                 + " CmapDisagreesWithProvider=" + s_cmapDisagrees
                 + " PrefixSplit=" + s_prefixSplit
                 + " AnswerNotResolved=" + s_answerNotResolved
                 + " WrapCalls=" + s_wrapCalls
                 + " WrapperMapCalls=" + s_mapCalls
                 + " AllCoveredRanges=" + s_allCovered;
        }

        /// <summary>完整读数（开关语义 + 七个计数器 + 探测原文 + 目标族 + 码点直方图前 N）。</summary>
        internal static string Readings()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();

            sb.Append("[COVERAGE_FALLBACK] 开关: ").Append(BehaviorEnv).Append('=')
              .Append(Environment.GetEnvironmentVariable(BehaviorEnv) ?? "<未设>")
              .Append(" ⇒ 本次 Enabled=")
              .Append(s_enabled ? "true（**未设时就是开**）" : "false（关：连包装都不做 ⇒ 行为=接线前）")
              .Append(" ; ").Append(DiagEnv).Append('=')
              .Append(Environment.GetEnvironmentVariable(DiagEnv) ?? "<未设>")
              .Append(" ⇒ Diag=").Append(s_diag ? "true" : "false（**缺省关**）").Append('\n');

            sb.Append("[COVERAGE_FALLBACK] 计数: ").Append(ReadingsOneLine()).Append('\n');
            sb.Append("[COVERAGE_FALLBACK] Probe: ").Append(ProbeReading).Append('\n');

            sb.Append("[COVERAGE_FALLBACK] FallbackTarget: ");
            if (s_targets.Count == 0) sb.Append("<无>");

            foreach (System.Collections.Generic.KeyValuePair<string, int> pair in s_targets)
            {
                sb.Append('"').Append(pair.Key).Append("\"=").Append(pair.Value).Append(' ');
            }

            sb.Append('\n');
            sb.Append("[COVERAGE_FALLBACK] FallbackAppliedCodePoints(top")
              .Append(TopCodePoints).Append("): ");

            System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<int, int>> histogram =
                new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<int, int>>(s_appliedCodePoints);

            histogram.Sort(delegate (System.Collections.Generic.KeyValuePair<int, int> a,
                                     System.Collections.Generic.KeyValuePair<int, int> b)
            {
                int c = b.Value.CompareTo(a.Value);
                return c != 0 ? c : a.Key.CompareTo(b.Key);
            });

            if (histogram.Count == 0) sb.Append("<无>");

            for (int i = 0; i < histogram.Count && i < TopCodePoints; i++)
            {
                sb.Append("U+").Append(histogram[i].Key.ToString("X4")).Append('=')
                  .Append(histogram[i].Value).Append(' ');
            }

            sb.Append('\n');
            return sb.ToString();
        }

        /// <summary>诊断开着时登记进程退出汇总（**缺省关**时什么都不登记）。</summary>
        internal static void RegisterExitDumpOnce()
        {
            if (!s_diag || s_exitDumpRegistered) return;
            s_exitDumpRegistered = true;

            try
            {
                AppDomain.CurrentDomain.ProcessExit += delegate (object sender, EventArgs e)
                {
                    try { Console.Error.WriteLine(Readings()); } catch (Exception) { }
                    Dump();
                };
            }
            catch (Exception)
            {
            }
        }
    }
    /// <summary>
    /// FamilyCollection font cache element class is responsible for
    /// storing the mapping between a folder and font families in it
    /// </summary>
    internal class FamilyCollection
    {
        //------------------------------------------------------
        //
        //  Private Fields
        //
        //------------------------------------------------------

        #region Private Fields

        private Text.TextInterface.FontCollection _fontCollection;
        private Uri                               _folderUri;
        private List<CompositeFontFamily>         _userCompositeFonts;
        private static object                     _staticLock = new object();

        #endregion Private Fields

        internal static string SxSFontsResourcePrefix { get; } = $"/{Path.GetFileNameWithoutExtension(ExternDll.PresentationCore)};component/fonts/";

        private static List<CompositeFontFamily> GetCompositeFontList(FontSourceCollection fontSourceCollection)
        {
            List<CompositeFontFamily> compositeFonts = new List<CompositeFontFamily>();

            foreach (FontSource fontSource in fontSourceCollection)
            {
                if (fontSource.IsComposite)
                {
                    CompositeFontInfo fontInfo = CompositeFontParser.LoadXml(fontSource.GetStream());
                    CompositeFontFamily compositeFamily = new CompositeFontFamily(fontInfo);
                    compositeFonts.Add(compositeFamily);
                }
            }

            return compositeFonts;
        }

        private bool UseSystemFonts
        {
            get
            {
                return (_fontCollection == DWriteFactory.SystemFontCollection);
            }
        }

        private IList<CompositeFontFamily> UserCompositeFonts
        {
            get
            {
                if (_userCompositeFonts == null)
                {
                    _userCompositeFonts = GetCompositeFontList(new FontSourceCollection(_folderUri, true));
                }
                return _userCompositeFonts;
            }
        }

        private static class LegacyArabicFonts
        {
            private static bool              _usePrivateFontCollectionIsInitialized = false;
            private static object            _staticLock = new object();
            private static bool              _usePrivateFontCollectionForLegacyArabicFonts;
            private static readonly string[] _legacyArabicFonts;
            private static Text.TextInterface.FontCollection _legacyArabicFontCollection;


            static LegacyArabicFonts()
            {
                _legacyArabicFonts = new string[] { "Traditional Arabic",
                                                    "Andalus",
                                                    "Simplified Arabic",
                                                    "Simplified Arabic Fixed" };
            }

            internal static Text.TextInterface.FontCollection LegacyArabicFontCollection
            {
                get
                {
                    if (_legacyArabicFontCollection == null)
                    {
                        lock (_staticLock)
                        {
                            if (_legacyArabicFontCollection == null)
                            {
                                Uri criticalSxSFontsLocation = new Uri(FamilyCollection.SxSFontsResourcePrefix);
                                _legacyArabicFontCollection = DWriteFactory.GetFontCollectionFromFolder(criticalSxSFontsLocation);
                            }
                        }
                    }
                    return _legacyArabicFontCollection;
                }
            }

            /// <summary>
            /// Checks if a given family name is one of the legacy Arabic fonts.
            /// </summary>
            /// <param name="familyName">The family name without any face info.</param>
            internal static bool IsLegacyArabicFont(string familyName)
            {
                for (int i = 0; i < _legacyArabicFonts.Length; ++i)
                {
                    if (string.Equals(familyName, _legacyArabicFonts[i], StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            }

            /// <summary>
            /// We will use the private font collection to load some Arabic fonts
            /// only on OSes lower than Win7, since the fonts on these OSes are legacy fonts
            /// and the shaping engines that DWrite uses does not handle them properly.
            /// </summary>
            internal static bool UsePrivateFontCollectionForLegacyArabicFonts
            {
                get
                {
                    if (!_usePrivateFontCollectionIsInitialized)
                    {
                        lock (_staticLock)
                        {
                            if (!_usePrivateFontCollectionIsInitialized)
                            {
                                try
                                {
                                    OperatingSystem osInfo = Environment.OSVersion;
                                    // The version of Win7 is 6.1
                                    _usePrivateFontCollectionForLegacyArabicFonts = (osInfo.Version.Major < 6)
                                                                                 || (osInfo.Version.Major == 6
                                                                                     &&
                                                                                     osInfo.Version.Minor == 0);
                                }
                                //Environment.OSVersion was unable to obtain the system version.
                                //-or- 
                                //The obtained platform identifier is not a member of PlatformID.
                                catch (InvalidOperationException)
                                {
                                    // We do not want to bubble this exception up to the user since the user
                                    // has nothing to do about it.
                                    // Instead we will silently fallback to using the private fonts collection 
                                    // so that we guarantee that the text shows properly.
                                    _usePrivateFontCollectionForLegacyArabicFonts = true;
                                }

                                _usePrivateFontCollectionIsInitialized = true;
                            }
                        }
                    }
                    return _usePrivateFontCollectionForLegacyArabicFonts;
                }
            }
        }

        /// <summary>
        /// This class encapsulates the 4 system composite fonts.
        /// </summary>
        /// <remarks>
        /// This class has direct knowledge about the 4 composite fonts that ship with WPF.
        /// </remarks>
        private static class SystemCompositeFonts
        {
            /// The number of the system composite fonts that ship with WPF is 4.
            internal const int NumOfSystemCompositeFonts = 4;

            private static object                _systemCompositeFontsLock = new object();
            private static readonly string[]     _systemCompositeFontsNames;
            private static readonly string[]     _systemCompositeFontsFileNames;
            private static CompositeFontFamily[] _systemCompositeFonts;

            static SystemCompositeFonts()
            {
                _systemCompositeFontsNames     = new string[] { "Global User Interface", "Global Monospace", "Global Sans Serif", "Global Serif" };
                _systemCompositeFontsFileNames = new string[] { "GlobalUserInterface", "GlobalMonospace", "GlobalSansSerif", "GlobalSerif" };
                _systemCompositeFonts = new CompositeFontFamily[NumOfSystemCompositeFonts];
            }

            /// <summary>
            /// Returns the composite font to be used to fallback to a different font
            /// if one of the legacy Arabic fonts is specifed.
            /// </summary>
            internal static CompositeFontFamily GetFallbackFontForArabicLegacyFonts()
            {
                return GetCompositeFontFamilyAtIndex(1);
            }

            /// <summary>
            /// This method returns the composite font family (or null if not found) given its name
            /// </summary>
            internal static IFontFamily FindFamily(string familyName)
            {
                // ── WPF-on-Linux M7d 补丁 J ⑤：返回类型 CompositeFontFamily → IFontFamily ──
                // 非 Windows 上"系统复合字体"不再由 CompositeFontFamily 填充，而是由
                // **provider 的首个可用族**代偿（见 FamilyCollection.LookupFamily 里的 ② 段），
                // 那是个 PhysicalFontFamily ⇒ 返回类型必须是 IFontFamily。
                // 唯一调用点：FamilyCollection.LookupFamily（本文件 :340）。Windows 语义逐字不变。
                int index = GetIndexOfFamily(familyName);
                if (index >= 0)
                {
                    return GetCompositeFontFamilyAtIndex(index);
                }
                return null;
            }

            /// <summary>
            /// This method returns the composite font with the given index after
            /// lazily allocating it if it has not been already allocated.
            /// </summary>
            internal static CompositeFontFamily GetCompositeFontFamilyAtIndex(int index)
            {
                // ── WPF-on-Linux M7d 补丁 J ①：短路点（由 src/Linux/tools/patch-presentationcore-compositefont.py 插入）──
                // 4 个 Global*.CompositeFont 是 **Windows 平台的族链表资源**：它们干的是
                // "某个字符落到哪个字体"的**回退映射**。本移植里这件事**已由 provider 承担**
                // （src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/FaceSelector.cs + LinuxFontCollection.cs：
                // 族内选面 / 缺字回退）⇒ 上游这一层在 Linux 上是冗余的第二套回退，不是缺失能力。
                //
                // 不短路的话：LoadXml → CompositeFontParser.ParseFontFamilyCollectionElement 按 `OS`
                // 属性挑平台专属族链表（GlobalUserInterface 是 4 个里唯一 <FontFamilyCollection> 根的），
                // 一段都挑不中 ⇒ Fail(...) ⇒ 而 Fail 要 OSVersionHelper.GetOsVersion() ⇒ 本移植上必抛
                // "OSVersionHelper.GetOsVersion Could not detect OS!"。
                // 影响面：`new FontFamily("Arial")`（XAML 里最常见的写法）全线不可用。
                //
                // ❌ 禁做：伪造 OS 版本。`OS` 用来挑**平台专属族链表**（里面是 Windows 装的字体名
                //    Segoe UI / Microsoft YaHei…），谎报 = 以造假换安静且掩盖平台差异。这里一个字节都不碰。
                //
                // ✅ 短路：非 Windows 上这一层一致地表达"没有系统复合字体"。判断在最前面 ⇒
                //    连 `new FontSource(...)` / `LoadXml(...)` 都不会被执行（是短路，不是接住异常）。
                //    调用点 3 个：FindFamily（:340，下面 ② 接管"系统回退族"名字）、
                //    GetFallbackFontForArabicLegacyFonts（:420，返回 null = LookupFamily 的"找不到"语义）、
                //    GetFontFamilies 枚举（:628，见本文件第 ③ 处 null 守卫与第 ④ 处计数）。
                if (!OperatingSystem.IsWindows())
                {
                    return null;
                }

                if (_systemCompositeFonts[index] == null)
                {
                    lock (_systemCompositeFontsLock)
                    {
                        if (_systemCompositeFonts[index] == null)
                        {
                            FontSource fontSource = new FontSource(new Uri(Path.Combine(FamilyCollection.SxSFontsResourcePrefix, _systemCompositeFontsFileNames[index] + Util.CompositeFontExtension), UriKind.RelativeOrAbsolute),
                                                                   isComposite:true,
                                                                   isInternalCompositeFont:true);

                            CompositeFontInfo fontInfo = CompositeFontParser.LoadXml(fontSource.GetStream());
                            _systemCompositeFonts[index] = new CompositeFontFamily(fontInfo);
                        }
                    }
                }
                return _systemCompositeFonts[index];
            }

            /// <summary>
            /// M7d 补丁 J ②：familyName 是否是 4 个系统复合字体之一（只看名字，不看是否加载成功）。
            /// </summary>
            internal static bool IsSystemCompositeFontName(string familyName)
            {
                return GetIndexOfFamily(familyName) >= 0;
            }

            /// <summary>
            /// This method returns the index of the system composite font in _systemCompositeFontsNames.
            /// </summary>
            private static int GetIndexOfFamily(string familyName)
            {
                for (int i = 0; i < _systemCompositeFontsNames.Length; ++i)
                {
                    if (string.Equals(_systemCompositeFontsNames[i], familyName, StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }
                return -1;
            }
        }

        //------------------------------------------------------
        //
        //  Constructors
        //
        //------------------------------------------------------

        #region Constructors
        /// <summary>
        /// Creates a font family collection cache element from a canonical font family reference.
        /// </summary>
        /// <param name="folderUri">Absolute Uri of a folder</param>
        /// <param name="fontCollection">Collection of fonts loaded from the folderUri location</param>
        private FamilyCollection(Uri folderUri, MS.Internal.Text.TextInterface.FontCollection fontCollection)
        {
            _folderUri = folderUri;
            _fontCollection = fontCollection;
        }

        /// <summary>
        /// Creates a font family collection cache element from a canonical font family reference.
        /// </summary>
        /// <param name="folderUri">Absolute Uri of a folder</param>
        internal static FamilyCollection FromUri(Uri folderUri)
        {
            return new FamilyCollection(folderUri, DWriteFactory.GetFontCollectionFromFolder(folderUri));
        }

        /// <summary>
        /// Creates a font family collection cache element from a canonical font family reference.
        /// </summary>
        /// <param name="folderUri">Absolute Uri to the Windows Fonts folder or a file in the Windows Fonts folder.</param>
        internal static FamilyCollection FromWindowsFonts(Uri folderUri)
        {
            return new FamilyCollection(folderUri, DWriteFactory.SystemFontCollection);
        }

        #endregion Constructors


        //------------------------------------------------------
        //
        //  Internal Methods
        //
        //------------------------------------------------------

        #region Internal methods

        /// <summary>
        /// This method looks up a certain family in this collection given its name.
        /// If the name was for a specific font face then this method will return its
        /// style, weight and stretch information.
        /// </summary>
        /// <param name="familyName">The name of the family to look for.</param>
        /// <param name="fontStyle">The style if the font face in case family name contained style info.</param>
        /// <param name="fontWeight">The weight if the font face in case family name contained style info.</param>
        /// <param name="fontStretch">The stretch if the font face in case family name contained style info.</param>
        /// <returns>The font family if found.</returns>
        internal IFontFamily LookupFamily(
            string familyName,
            ref FontStyle fontStyle,
            ref FontWeight fontWeight,
            ref FontStretch fontStretch
            )
        {
            if (familyName == null || familyName.Length == 0)
                return null;

            familyName = familyName.Trim();

            // If we are referencing fonts from the system fonts, then it is cheap to lookup the 4 composite fonts
            // that ship with WPF. Also, it happens often that familyName is "Global User Interface".
            // So in this case we preceed looking into SystemComposite Fonts.
            if (UseSystemFonts)
            {
                IFontFamily compositeFamily = SystemCompositeFonts.FindFamily(familyName);
                if (compositeFamily != null)
                {
                    return compositeFamily;
                }

                // ── WPF-on-Linux M7d 补丁 J ②：把"系统回退族"这一层交给 provider ──
                // 名字命中 4 个系统复合字体（`FindFamily` 就是按名字查的）但没拿到族 ⇒ 非 Windows 上
                // 就是 ① 的短路。此时**不能留 null**：`#GLOBAL USER INTERFACE` 是 WPF "找不到字体时的
                // 兜底入口"（`FontFamily.FontFamilyGlobalUI`，`Typeface` 4 参构造把它当 FallbackFontFamily，
                // Typeface.cs:755），`Typeface.ConstructCachedTypeface` 在 `firstFontFamily == null` 之后
                // **直接解引用**（Typeface.cs:786）⇒ 留 null 就是把异常从 OSVersionHelper 搬成 NRE。
                //
                // 为什么不是"诚实失败"：`CachedTypeface` 的构造断言
                // `firstFontFamily != null && typefaceMetrics != null`（CachedTypeface.cs:42），
                // Debug 构建里传 null 会炸进程 ⇒ 这条链上没有"返回 null 的诚实失败"这个选项。
                //
                // 所以按上游语义补上回退：Windows 上这一步落到 GlobalUI 的字体映射（最差也是 Arial）；
                // 本移植上落到 **provider 的默认族**（`DefaultFontFamily.SelectFamilyName`，真实字体，
                // 不是造的族、不是空的族；理由与实测见 GetProviderFallbackFamily）。
                // ⏪ `t114`／P1-W38：**降级判定诊断行**（判据 C4/C5 的可判面）。
                //   三格固定字段：`requested=`（请求族名）/ `fallback=`（yes|no，二值）/ `resolved=`（选中族名或 none）。
                //   口径：本条闸**只**在"名字命中 4 个系统复合字体 ∨ 名字是空字体族的规范族名"时才走到 ⇒
                //   这条诊断在"存在族"的路径上**必然**打出 fallback=no（**常开**，不靠环境变量）。
                //   ⚠️ 空字体族的规范名经 `CanonicalFontFamilyReference.Create(null, "#ARIAL")` 归一后
                //      **就是 "ARIAL"**（`#` 是"无 location"前缀，分族名时被剥掉）⇒ 与 `FamilyName` 比较
                //      用**精确** `Ordinal`（大小写按 `CanonicalFontFamilyReference` 的转义规则）。
                //   既有的 `[FONT_DIAG] providerFallback:`（`WPF_LINUX_FONT_DIAG` 支）保持原样。
                string _fbRequested = familyName;
                bool _fbNullFontFamily = string.Equals(familyName, "ARIAL", StringComparison.Ordinal);
                bool _fbCompositeName = SystemCompositeFonts.IsSystemCompositeFontName(familyName);
                if (!_fbCompositeName && !_fbNullFontFamily)
                {
                    EmitFontFallbackDiag(_fbRequested, "no",
                        _fontCollection[familyName] == null ? "none" : familyName);
                }
                // ⏪ `t114`／P1-W38：**闸门放宽** —— `ARIAL`（空字体族的规范族名）原先**过不了**这道名字闸
                //   （闸内只有 4 个系统复合字体名）⇒ 落到 `_fontCollection["ARIAL"]` ⇒ null ⇒
                //   `FontFamily.cs:336` 的 1 参 `Invariant.Assert` ⇒ `Environment.FailFast`（**不可捕获**）
                //   ⇒ 整进程 `rc=134`。现在把**空字体族名**也纳入这道回退 ⇒ 它拿到 provider 默认族
                //   （真实字体，不是造的族、不是空族）⇒ 该断言不再达成。
                //   ⚠️ **不是**"永远降级"：本闸只覆盖"4 个系统复合字体名 ∨ 空字体族名"；
                //   **存在族**根本不进这里（走下面的集合查找）⇒ "存在族被误降级"不可能发生。
                if (!OperatingSystem.IsWindows() && (_fbCompositeName || _fbNullFontFamily))
                {
                    IFontFamily providerFallback = GetProviderFallbackFamily();
                    if (providerFallback != null)
                    {
                        /* ⏪ `t114`／P1-W38：把"**落到谁**"写进降级判定行（判据 C4 的 `resolved=`）。
                           走 `_fontCollection` 查那次回退族名；查不到就如实 none（不编名）。 */
                        EmitFontFallbackDiag(_fbRequested, "yes",
                            (_fontCollection != null && _fontCollection[_fbRequested] != null)
                                ? _fbRequested : "none");
                        return providerFallback;
                    }
                }
            }

            Text.TextInterface.FontFamily fontFamilyDWrite = _fontCollection[familyName];

            // A font family was not found in DWrite's font collection.
            if (fontFamilyDWrite == null)
            {
                // Having user defined composite fonts is not very common. So we defer looking into them to looking DWrite 
                // (which is opposite to what we do for system fonts).
                if (!UseSystemFonts)
                {
                    // The family name was not found in DWrite's font collection. It may possibly be the name of a composite font
                    // since DWrite does not recognize composite fonts.
                    CompositeFontFamily compositeFamily = LookUpUserCompositeFamily(familyName);
                    if (compositeFamily != null)
                    {
                        return compositeFamily;
                    }
                }

                // The family name cannot be found. This may possibly be because the family name contains styling info.
                // For example, "Arial Bold"
                // We will strip off the styling info (one word at a time from the end) and try to find the family name.
                int indexOfSpace = -1;
                string originalFamilyName = familyName;

                // Start removing off strings from the end hoping they are
                // style info so as to get down to the family name.
                do
                {
                    indexOfSpace = familyName.LastIndexOf(' ');
                    if (indexOfSpace < 0)
                    {
                        break;
                    }
                    else
                    {
                        // store the stripped off style names to look for the specific face later.
                        familyName = familyName.Substring(0, indexOfSpace);
                    }

                    fontFamilyDWrite = _fontCollection[familyName];
                } while (fontFamilyDWrite == null);


                if (fontFamilyDWrite == null)
                {
                    return null;
                }

                // If there was styling information.
                if (familyName.Length != originalFamilyName.Length)
                {
                    // To obtain the face name, we remove the family name and the next char (A space) from the original family name.
                    int faceNameIndex = familyName.Length + 1;
                    ReadOnlySpan<char> faceName = originalFamilyName.AsSpan(faceNameIndex);
                    Text.TextInterface.Font font = GetFontFromFamily(fontFamilyDWrite, faceName);

                    if (font != null)
                    {
                        fontStyle = new FontStyle((int)font.Style);
                        fontWeight = new FontWeight((int)font.Weight);
                        fontStretch = new FontStretch((int)font.Stretch);
                    }
                }
            }

            if (UseSystemFonts
                && LegacyArabicFonts.UsePrivateFontCollectionForLegacyArabicFonts
                // familyName will hold the family name without any face info.
                && LegacyArabicFonts.IsLegacyArabicFont(familyName))
            {
                fontFamilyDWrite = LegacyArabicFonts.LegacyArabicFontCollection[familyName];
                if (fontFamilyDWrite == null)
                {
                    return SystemCompositeFonts.GetFallbackFontForArabicLegacyFonts();
                }
            }

            // ── WPF-on-Linux T1c 方案 A：按码点覆盖感知的回退（由 src/Linux/tools/patch-presentationcore-compositefont.py 插入）──
            // ⚠️ 为什么这一处也要包：`LookupFamily` 的入参**只有族名**，看不到该 run 的字符 ⇒
            //    "当前族不覆盖该 run 的码点"只能在**字符区间**那一层判（`IFontFamily.
            //    GetMapTargetFamilyNameAndScale`，上游**复合字体协议**的钩子；补丁 J 短路掉的
            //    正是唯一实现它的上游类型）。⇒ 把"今天那个族"包一层：
            //      覆盖 ⇒ 答本族名 ⇒ 机制随即走与今天**逐字相同**的 `MapByFontFaceFamily`；
            //      不覆盖 ⇒ 答一个**覆盖该码点**的族名（按码点/按 run 选面，不按段落整体换族）。
            //    关掉开关（WPF_LINUX_COVERAGE_FALLBACK=0）或任何不确定 ⇒ 原样返回今天的族。
            return HbCoverageFallback.Wrap(new PhysicalFontFamily(fontFamilyDWrite), _fontCollection, fontFamilyDWrite);
        }

        /// <summary>
        /// ⏪ `t114`／P1-W38：**降级判定诊断行**（常开；判据 C4/C5 的机器可读面）。
        /// 三格固定字段：`requested=`（请求族名）/ `fallback=`（yes|no，二值）/ `resolved=`（选中族名或 none）。
        /// **实例方法**：要查 `_fontCollection` 才能回答"这个名字在集合里找不找得到"。
        /// 只打 stderr，不改任何返回值语义。
        /// </summary>
        private void EmitFontFallbackDiag(string requested, string fallback, string resolved)
        {
            Console.Error.WriteLine("[FONT_FALLBACK] requested=" + requested
                + " fallback=" + fallback + " resolved=" + resolved);
        }

        /// <summary>
        /// M7d 补丁 J ②：非 Windows 上"系统复合字体"这一层不存在，**把"默认族"交给 provider**。
        ///
        /// 用 T2 的公开 API `DefaultFontFamily.SelectFamilyName(LinuxFontCollection)`
        /// （偏好序 → fontconfig sans-serif → 族名序，见 Provider/DefaultFontFamily.cs），
        /// **不是** `_fontCollection[0]` —— 实测漂移（主控 ① 要求 2，两种目录配置）：
        ///     _fontCollection[0]      : 只 src/Linux/build/fonts → `Noto Sans`；+ /usr/share/fonts → `AR PL UKai CN`（楷体）
        ///     SelectFamilyName        : 两种配置**都** `Noto Sans`（reason=preferred-list）
        /// 默认 UI 字体不该随"扫到了哪些目录"变（Windows 的默认 UI 字体是固定族 Segoe UI）。
        ///
        /// 诚实回退（不许静默退回 [0]）：`Select` 内部已保证返回值**来自本集合**（它按 collection.Entries 取候选），
        /// 但这里仍然再验一次；取不到 / 抛异常 / 空集合都**记数并打诊断**（`WPF_LINUX_FONT_DIAG=1` ⇒ stderr
        /// `[FONT_DIAG] providerFallback: ...`，前缀与 T2 的诊断同款），读数也可从
        /// `ProviderFallbackDiagnostics` 反射读取 —— 免得"选了但没选到"变成悄悄用第 0 个。
        /// 为什么必须返回非 null：见 LookupFamily 里 ② 段的说明（null 会在 Typeface.cs:786 变成 NRE）。
        /// </summary>
        private IFontFamily GetProviderFallbackFamily()
        {
            if (_fontCollection == null)
            {
                ++s_providerFallbackMisses;
                ProviderFallbackDiag("字体集合为 null ⇒ 默认族不可用");
                return null;
            }

            if (_fontCollection.FamilyCount == 0)
            {
                ++s_providerFallbackMisses;
                ProviderFallbackDiag("集合里一个族都没有 ⇒ 默认族不可用（任何字体调用都没有意义）");
                return null;
            }

            try
            {
                string name = MS.Internal.Text.TextInterface.Linux.DefaultFontFamily.SelectFamilyName(
                    _fontCollection.LinuxCollection);

                MS.Internal.Text.TextInterface.FontFamily selected =
                    string.IsNullOrEmpty(name) ? null : _fontCollection[name];

                if (selected != null)
                {
                    ++s_providerFallbackHits;
                    ProviderFallbackDiag("默认族 = \"" + name + "\"");
                    return HbCoverageFallback.Wrap(new PhysicalFontFamily(selected), _fontCollection, selected);
                }

                ++s_providerFallbackMisses;
                ProviderFallbackDiag("SelectFamilyName 选了 \"" + name + "\" 但集合里取不到"
                                     + " ⇒ 退到集合第 0 个（**已记数**，不是静默）");
            }
            catch (Exception e)
            {
                ++s_providerFallbackMisses;
                ProviderFallbackDiag("DefaultFontFamily 抛 " + e.GetType().Name + " ⇒ 退到集合第 0 个（**已记数**）");
            }

            return HbCoverageFallback.Wrap(new PhysicalFontFamily(_fontCollection[0]), _fontCollection, _fontCollection[0]);
        }

        // ── M7d 补丁 J ② 的计数器与诊断读数（主控 ① 要求 1：任何回退都要有读数）──
        private static int s_providerFallbackHits;
        private static int s_providerFallbackMisses;

        /// <summary>诊断读数（探针/复验可反射读取）。</summary>
        internal static string ProviderFallbackDiagnostics
        {
            get
            {
                return "providerFallbackHits=" + s_providerFallbackHits
                     + " providerFallbackMisses=" + s_providerFallbackMisses;
            }
        }

        private static void ProviderFallbackDiag(string message)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WPF_LINUX_FONT_DIAG"))) return;

            Console.Error.WriteLine("[FONT_DIAG] providerFallback: " + message
                + " (hits=" + s_providerFallbackHits + " misses=" + s_providerFallbackMisses + ")");
        }

        private CompositeFontFamily LookUpUserCompositeFamily(string familyName)
        {
            if (UserCompositeFonts != null)
            {
                foreach (CompositeFontFamily compositeFamily in UserCompositeFonts)
                {
                    foreach (KeyValuePair<XmlLanguage, string> localizedFamilyName in compositeFamily.FamilyNames)
                    {
                        if (string.Equals(localizedFamilyName.Value, familyName, StringComparison.OrdinalIgnoreCase))
                        {
                            return compositeFamily;
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Given a DWrite font family, look into it for the given face.
        /// </summary>
        /// <param name="fontFamily">The font family to look in.</param>
        /// <param name="faceName">The face to look for.</param>
        /// <returns>The font face if found and null if nothing was found.</returns>
        private static Text.TextInterface.Font GetFontFromFamily(Text.TextInterface.FontFamily fontFamily, ReadOnlySpan<char> faceName)
        {
            // The search that DWrite supports is a linear search.
            // Look at every font face.
            foreach (Text.TextInterface.Font font in fontFamily)
            {
                // and at every locale name this font face has.
                foreach (KeyValuePair<CultureInfo, string> name in font.FaceNames)
                {
                    if (faceName.Equals(name.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        return font;
                    }
                }
            }

            // This dictionary is used to store the faces (indexed by their names).
            // This dictionary will be used in case the exact string "faceName" was not found,
            // thus we will start again removing words (separated by ' ') from its end and looking
            // for the resulting faceName in that dictionary. So this dictionary is 
            // used to speed the search.
            Dictionary<string, Text.TextInterface.Font> faces = new Dictionary<string, Text.TextInterface.Font>(StringComparer.OrdinalIgnoreCase);

            //We could have merged this loop with the one above. However this will degrade the performance 
            //of the scenario where the user entered  a correct face name (which is the common scenario).
            //Thus we adopt a pay for play approach, meaning that only whenever the face name does not
            //exactly correspond to an actual face name we will incure this overhead.
            foreach (Text.TextInterface.Font font in fontFamily)
            {
                foreach (KeyValuePair<CultureInfo, string> name in font.FaceNames)
                {
                    faces.TryAdd(name.Value, font);
                }
            }

            // An exact match was not found and so we will start looking for the best match.
            Text.TextInterface.Font matchingFont = null;
            int indexOfSpace = faceName.LastIndexOf(' ');
            Dictionary<string, Text.TextInterface.Font>.AlternateLookup<ReadOnlySpan<char>> alternateLookup = faces.GetAlternateLookup<ReadOnlySpan<char>>();

            while (indexOfSpace > 0)
            {
                faceName = faceName.Slice(0, indexOfSpace);
                if (alternateLookup.TryGetValue(faceName, out matchingFont))
                {
                    return matchingFont;
                }

                indexOfSpace = faceName.LastIndexOf(' ');
            }

            // No match was found.
            return null;
        }

        private struct FamilyEnumerator : IEnumerator<Text.TextInterface.FontFamily>, IEnumerable<Text.TextInterface.FontFamily>
        {
            private uint _familyCount;
            private Text.TextInterface.FontCollection _fontCollection;
            private bool _firstEnumeration;
            private uint _currentFamily;

            internal FamilyEnumerator(Text.TextInterface.FontCollection fontCollection)
            {
                _fontCollection = fontCollection;
                _currentFamily = 0;
                _firstEnumeration = true;
                _familyCount = fontCollection.FamilyCount;
}

            #region IEnumerator<Text.TextInterface.FontFamily> Members

            public bool MoveNext()
            {
                if (_firstEnumeration)
                {
                    _firstEnumeration = false;
                }
                else
                {
                    ++_currentFamily;
                }
                if (_currentFamily >= _familyCount)
                {
                    // prevent cycling
                    _currentFamily = _familyCount;
                    return false;
                }
                return true;
            }

            Text.TextInterface.FontFamily IEnumerator<Text.TextInterface.FontFamily>.Current
            {
                get
                {
                    if (_currentFamily < 0 || _currentFamily >= _familyCount)
                    {
                        throw new InvalidOperationException();
                    }

                    return _fontCollection[_currentFamily];
                }
            }

            #endregion

            #region IEnumerator Members

            object IEnumerator.Current
            {
                get
                {
                    return ((IEnumerator<Text.TextInterface.FontFamily>)this).Current;
                }
            }

            public void Reset()
            {
                _currentFamily = 0;
                _firstEnumeration = true;
            }

            #endregion


            #region IDisposable Members

            public void Dispose() { }

            #endregion

            #region IEnumerable<Text.TextInterface.FontFamily> Members

            IEnumerator<Text.TextInterface.FontFamily> IEnumerable<Text.TextInterface.FontFamily>.GetEnumerator()
            {
                return this as IEnumerator<Text.TextInterface.FontFamily>;
            }

            #endregion

            #region IEnumerable Members

            IEnumerator IEnumerable.GetEnumerator()
            {
                return ((IEnumerable<Text.TextInterface.FontFamily>)this).GetEnumerator();
            }

            #endregion
        }

        private IEnumerable<Text.TextInterface.FontFamily> GetPhysicalFontFamilies()
        {
            return new FamilyEnumerator(this._fontCollection);
        }

        internal FontFamily[] GetFontFamilies(Uri fontFamilyBaseUri, string fontFamilyLocationReference)
        {
            FontFamily[] fontFamilyList = new FontFamily[FamilyCount];
            int i = 0;
            foreach (MS.Internal.Text.TextInterface.FontFamily family in GetPhysicalFontFamilies())
            {
                string fontFamilyReference = Util.ConvertFamilyNameAndLocationToFontFamilyReference(
                    family.OrdinalName,
                    fontFamilyLocationReference
                    );

                string friendlyName = Util.ConvertFontFamilyReferenceToFriendlyName(fontFamilyReference);

                fontFamilyList[i++] = new FontFamily(fontFamilyBaseUri, friendlyName);
            }

            FontFamily fontFamily;
            if (UseSystemFonts)
            {
                for (int j = 0; j < SystemCompositeFonts.NumOfSystemCompositeFonts; ++j)
                {
                    // ── WPF-on-Linux M7d 补丁 J ③：null 守卫 ──
                    // 非 Windows 上 GetCompositeFontFamilyAtIndex 一律返回 null，而上游这里**直接**把
                    // 返回值交给 CreateFontFamily（:652），后者第一行 `(IFontFamily)compositeFontFamily`
                    // 紧跟 `.Names`，**没有 null 守卫**（上游断言调用方给的是真族）⇒ 不改这里就是 NRE。
                    // `Fonts.SystemFontFamilies` 在 App/Window 静态构造链上（同补丁 I 的 Util..cctor 那条
                    // 经验）⇒ 这条 NRE 会是"任何窗口都建不出来"级别。
                    CompositeFontFamily systemCompositeFont = SystemCompositeFonts.GetCompositeFontFamilyAtIndex(j);
                    if (systemCompositeFont == null)
                    {
                        continue;
                    }

                    fontFamily = CreateFontFamily(systemCompositeFont, fontFamilyBaseUri, fontFamilyLocationReference);
                    if (fontFamily != null)
                    {
                        fontFamilyList[i++] = fontFamily;
                    }
                }
            }
            else
            {
                foreach (CompositeFontFamily compositeFontFamily in UserCompositeFonts)
                {
                    fontFamily = CreateFontFamily(compositeFontFamily, fontFamilyBaseUri, fontFamilyLocationReference);
                    if (fontFamily != null)
                    {
                        fontFamilyList[i++] = fontFamily;
                    }
                }
            }

            Debug.Assert(i == FamilyCount);

            return fontFamilyList;
        }

        private FontFamily CreateFontFamily(CompositeFontFamily compositeFontFamily, Uri fontFamilyBaseUri, string fontFamilyLocationReference)
        {
            IFontFamily fontFamily = (IFontFamily)compositeFontFamily;
            IEnumerator<string> familyNames = fontFamily.Names.Values.GetEnumerator();
            if (familyNames.MoveNext())
            {
                string ordinalName = familyNames.Current;
                string fontFamilyReference = Util.ConvertFamilyNameAndLocationToFontFamilyReference(
                ordinalName,
                fontFamilyLocationReference
                );

                string friendlyName = Util.ConvertFontFamilyReferenceToFriendlyName(fontFamilyReference);

                return new FontFamily(fontFamilyBaseUri, friendlyName);
            }
            return null;
        }

        internal uint FamilyCount
        {
            get
            {
                // ── WPF-on-Linux M7d 补丁 J ④：计数与枚举一致 ──
                // 非 Windows 上枚举侧一个复合字体都不产出（见本文件 ① 与 ③），计数侧必须一致：
                // 否则 FamilyCount 比实际枚举**多 4** ⇒
                //   · GetFontFamilies（:609）用 FamilyCount 预分配 FontFamily[] ⇒ 尾部 4 个 null 元素
                //     （Fonts.SystemFontFamilies 的消费者会 NRE）；
                //   · 上游紧跟着的 Debug.Assert(i == FamilyCount)（:647）直接失败。
                //   注：② 的 provider 回退**不进**枚举表（它只回答"系统回退族"这一个名字），
                //   所以这里减 4 仍然与枚举严格一致。Windows 分支逐字不变。
                if (!OperatingSystem.IsWindows() && UseSystemFonts)
                {
                    return _fontCollection.FamilyCount;
                }

                return _fontCollection.FamilyCount + (UseSystemFonts ? SystemCompositeFonts.NumOfSystemCompositeFonts : checked((uint)UserCompositeFonts.Count));
            }
        }

        #endregion Internal methods
    }
}
