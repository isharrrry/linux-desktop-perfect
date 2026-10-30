// D-F1 判据 runner（T2 车道，2026-09-14 v1；2026-09-15 v2）—— **只做读数，不做判据**。
//
// 【为什么只做读数】判据（C1/C2/C3 + 两极化牙）只有一份实现，在 `eval-df1-criteria.py`；
//   本程序把"我们这一侧的真实读数"打成机读行，由 python 侧评估 ⇒ 判据口径唯一、可复算。
//
// =====================================================================================
// 【v2 修的两个自抓 bug（主控 2026-09-15 批准"你自己那两个 bug 批准修"）】
//   ① `cr.Width` 测不到（v1 打 `CR_IDX=-`）：v1 喂的是 **2 字合成段** 且 `alwaysCollapsible:false`。
//      shim 的折叠资格闸门 = `if (!HasOverflowed && !_keepState) return this;`（:3142），
//      而本 shim `HasOverflowed => false` **恒假**（:3086）⇒ **永不折叠**，`cr` 自然永远是空的。
//      v2 改为**逐字复现 b34 语料 `F_nbsp_zwsp_w40`**：整段 32 码元、em16、段宽 40、
//      `alwaysCollapsible:true`（= 该用例 `cases.json` 的 `alwaysCollapsible: true`），
//      并取 **`与 ` 那一行**（段落区间 `[14,2)`）—— 取行方式与 harness 逐字同口径
//      （`start += L.Length` 累加，`HbTextLineParity/Program.cs:722`）。
//   ② 牙的常量把"**行宽**"与"**折后宽**"混成一个数（`12.6560` 是**折后宽**，不是行宽）
//      ⇒ v2 分开报：`LINE_W`（原行宽）/ `COL_W`（折后宽）/ `CR_W`（折叠区间宽）。
//
// =====================================================================================
// 【口径来源（逐条可回源）】
//   · 段落调用参数：`src/Linux/build/MilBridge/tests/HbTextLineParity/Program.cs:459-461`
//       `FormatParagraph(text, font, em, width, gt, 1.0f, props, ac, hasModifier, lineHeight,
//                        out consumed, defaultIncrementalTab: 0, modifierOpenIndex:…, …)`
//   · 折叠椭圆：同文件 `:590` = `new HarnessEllipsisProps(Math.Max(1.0, L.Width * 0.5))`
//   · 取行/起点累加：同文件 `:722`（`start += L.Length`）
//   · b34 用例参数（只读真值）：`src/Linux/tests/parity/windows/layout-b34/cases.json` 的 `F_nbsp_zwsp_w40`
//       text 32 码元 / fontSize 16 / maxWidth 40 / fontKey "file" / alwaysCollapsible true
//
// =====================================================================================
// 【三种模式】（同一构建里做完，不需要重编）
//   null  = `plan: null` ⇒ **应用/harness 实际走的那条路**（`#14` 起 shim 在此路径内部自建
//            `allowFallback:true` 的计划，见 shim `:3486-3489`）⇒ **被判对象**
//   fb    = `HbFontPlanner.Build(..., allowFallback: true,  null)` ⇒ **正极**
//   nofb  = `HbFontPlanner.Build(..., allowFallback: false, null)` ⇒ **负极**（= 修前行为）
//
// 【三个段落（同一套代码路径，只换"选哪一行/哪段文本"）】
//   b34      : b34 整段，取含 U+4E0E 的那一行（`[14,2)`）…… **被判段**（D-F1 的主张面）
//   b34line0 : b34 整段，取**第 0 行** `no br`（全拉丁、段落字体自己覆盖）…… **健康正控**
//              （同一 `plan==null` 路径、同一 API ⇒ C1 必须能在此判绿；否则 C1 就是"恒红"）
//   two      : `"与 "` 两字段（v1 的段落，保留作次级对照）
//
// =====================================================================================
// 【字段来源（纪律 18：读数报的是哪一侧，必须一眼看出）】每个读数行后紧跟一行 `# SRC …`
//   LINE_W / WITW       := `TextLine.Width` / `.WidthIncludingTrailingWhitespace`（公开 API；**值由本 shim 计算**）
//   COL_W / COL_LEN     := `TextLine.Collapse(props).Width` / `.Length`（公开 API）
//   CR_IDX/CR_LEN/CR_W  := `.GetTextCollapsedRanges()[0]` 的 `TextSourceCharacterIndex`/`Length`/`Width`（公开 API）
//   ADV_DIP             := `IndexedGlyphRun.GlyphRun.AdvanceWidths[0]`（公开 API；**值由本 shim 的整形路径写入**）
//   RUN_EM              := `IndexedGlyphRun.GlyphRun.FontRenderingEmSize`（公开 API）
//   ADV_FROM_TYPEFACE   := `GlyphTypeface.AdvanceWidths[gid] * RUN_EM`（公开 API；字体 em 归一 advance）
//   GLYPH_COUNT         := `GlyphTypeface.GlyphCount`（公开 API）
//   FACE_URI / GID      := `GlyphRun.GlyphTypeface.FontUri` / `GlyphRun.GlyphIndices[0]`（公开 API）
//   PLAN_FACE           := **本 runner 自己喂进去的**计划里该段的面（不是 API 的说法，是我们自己的输入）
//   ⇒ **以上全部都是"我们这一侧经公开 API 面报出来的量"**；互相独立的只是**代码路径**。
//     真正**外部独立**的量只有 `advance_from_font.py`（自己读字体文件的 cmap/hmtx/maxp，不信任何 API 自述）。
//
// 用法：dotnet FallbackCriteria.dll [--mode=null|fb|nofb|all] [--para=b34,b34line0,two|all]
//                                  [--font=<ttf>] [--em=16] [--width=40] [--strict-probe]
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using WpfLinux.Shims.PresentationCore;

internal static class Program
{
    private sealed class EllipsisProps : TextCollapsingProperties
    {
        private readonly double _w;
        internal EllipsisProps(double w) { _w = w; }
        public override double Width => _w;
        public override TextRun Symbol => null;
        public override TextCollapsingStyle Style => TextCollapsingStyle.TrailingCharacter;
    }

    /// <summary>一个"段落"= 文本 + 段宽 + 选哪一行（-1 ⇒ 取含探针码点的那一行）。</summary>
    private sealed class ParaSpec
    {
        internal string Name;
        internal string Text;
        internal double Width;
        internal int SelLine;
        internal char Probe;
        internal string Why;
    }

    private static string Repo()
    {
        // 【对照宿主用】`DF1_REPO=<仓库根>` 可显式指定：宿主被输出到仓库外的目录时（如 `-o $HOME/...` 的
        //   **修前对照宿主**），`../../..` 会解析成 `/`（实测 ⇒ `font-missing`）⇒ 必须能显式给。
        string forced = Environment.GetEnvironmentVariable("DF1_REPO");
        if (!string.IsNullOrEmpty(forced)) return System.IO.Path.GetFullPath(forced);
        string d = AppContext.BaseDirectory;
        // …/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/FallbackCriteria/bin/Debug/ ⇒ 仓库根
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(d, "..", "..", "..", "..", ".."));
    }

    /// <summary>b34 语料 F_nbsp_zwsp_w40 的**逐字文本**（32 码元；U+4E0E 在下标 14）。</summary>
    private const string B34Text = "no\u00A0break\u00A0nbsp \u4E0E zero\u200Bwidth\u200Bspace";

    private static int Main(string[] argv)
    {
        string mode = "all", font = null;
        double em = 16.0, width = 40.0;
        bool strictProbe = false, probeOnly = false, census = false;
        string censusCps = null;
        string paraSel = "all";
        var shapeProbes = new List<string>();
        foreach (string a in argv)
        {
            if (a.StartsWith("--mode=", StringComparison.Ordinal)) mode = a.Substring(7);
            else if (a.StartsWith("--para=", StringComparison.Ordinal)) paraSel = a.Substring(7);
            else if (a.StartsWith("--shape-probe=", StringComparison.Ordinal)) shapeProbes.Add(a.Substring(14));
            else if (a.StartsWith("--font=", StringComparison.Ordinal)) font = a.Substring(7);
            else if (a.StartsWith("--em=", StringComparison.Ordinal)) em = double.Parse(a.Substring(5), CultureInfo.InvariantCulture);
            else if (a.StartsWith("--width=", StringComparison.Ordinal)) width = double.Parse(a.Substring(8), CultureInfo.InvariantCulture);
            else if (a == "--strict-probe") strictProbe = true;
            else if (a == "--probe-only") probeOnly = true;   // 只跑定位探针（不打读数行）——给外部脚本串同一份日志用
            else if (a == "--census") census = true;          // 面选择普查（多码点 × 多段落字体；主控 #15 任务 3）
            else if (a.StartsWith("--census-cps=", StringComparison.Ordinal)) censusCps = a.Substring(13);
        }
        font ??= System.IO.Path.Combine(Repo(), "src", "Linux", "build", "fonts", "NotoSans-Regular.ttf");

        var allParas = new List<ParaSpec>
        {
            new ParaSpec { Name = "b34", Text = B34Text, Width = width, SelLine = -1, Probe = '\u4E0E',
                           Why = "被判段：b34 F_nbsp_zwsp_w40 整段，取含 U+4E0E 的那一行 [14,2)" },
            new ParaSpec { Name = "b34line0", Text = B34Text, Width = width, SelLine = 0, Probe = '\0',
                           Why = "健康正控：同一段落的第 0 行 no br（全拉丁、段落字体自己覆盖）" },
            new ParaSpec { Name = "two", Text = "\u4E0E ", Width = width, SelLine = 0, Probe = '\0',
                           Why = "次级对照：v1 的两字段" },
        };
        List<ParaSpec> paras = paraSel == "all"
            ? allParas
            : allParas.Where(p => paraSel.Split(',').Contains(p.Name)).ToList();
        if (paras.Count == 0) { Console.WriteLine("MODE=none PARA=- RESULT=NOINFO reason=no-such-para:" + paraSel); return 3; }

        // 【口径硬要求（主控 2026-09-15 ③）】`WPF_LINUX_FONT_DIR` 设没设**会改读数**
        //   （限制到 4 面 ⇒ 无回退面 ⇒ `9.6000`/`.notdef`；系统目录 ⇒ `16.0000`）⇒ 每个读数行都必须带它的值。
        string fontDirEnv = Environment.GetEnvironmentVariable("WPF_LINUX_FONT_DIR");
        string multiFontEnv = Environment.GetEnvironmentVariable("WPF_LINUX_MULTIFONT");
        Console.WriteLine($"# ENV WPF_LINUX_FONT_DIR={(fontDirEnv ?? "<未设>")}"
                          + $"（{(fontDirEnv == null ? "未设 ⇒ 回退扫描走 /usr/share/fonts 等**系统目录**（371 面 ⇒ 峰值 GB 级）" : "**已设** ⇒ 回退扫描被限制到这些目录")}）"
                          + $"  WPF_LINUX_MULTIFONT={(multiFontEnv ?? "<未设>")}（=0 对 `plan==null` 路径**无效**：shim `:3486-3508` 不看这个开关）");
        Console.WriteLine($"# DF1-RUNNER v2 repo={Repo()}");
        Console.WriteLine($"# font={font} em={em} width={width} paras={string.Join(",", paras.Select(p => p.Name))}");
        if (!System.IO.File.Exists(font)) { Console.WriteLine("MODE=none PARA=- RESULT=NOINFO reason=font-missing"); return 3; }

        GlyphTypeface gt;
        try { gt = GetGlyphTypeface(System.IO.Path.GetDirectoryName(font), "Regular"); }
        catch (Exception e) { Console.WriteLine($"MODE=none PARA=- RESULT=NOINFO reason=glyphtypeface:{e.GetType().Name}:{e.Message}"); return 3; }
        Console.WriteLine($"# glyphTypeface={gt.FaceNames.Values.FirstOrDefault()} fontUri={gt.FontUri}");

        var props = new HbRunProperties(null, 0, null);
        if (census) return RunCensus(font, em, width, props, censusCps);
        string[] modes = mode == "all" ? new[] { "null", "fb", "nofb" } : new[] { mode };
        int rc = 0;
        if (!probeOnly)
            foreach (string m in modes)
                foreach (ParaSpec p in paras)
                    rc = Math.Max(rc, RunOne(m, p, font, em, gt, props, strictProbe));
        if (shapeProbes.Count > 0) RunShapeProbes(shapeProbes);
        return rc;
    }

    /// <summary>
    /// 诊断：**本 shim 自己的 HbShaper**（= 内部装置，仅定位用）在给定 `面#下标` 上把 `与`(U+4E0E) 整成什么字形号。
    /// 【为什么要它】判据 C1② 用**独立读 cmap**（`advance_from_font.py`）判"报出的面覆不覆盖该码点"；
    ///   而 2026-09-15 实测：`NotoSansCJK-Regular.ttc#0` 的 cmap 给 U+4E0E = **9497**，shim 却报 **9498**
    ///   ⇒ 差 1 必须**测**清楚（是 GSUB `locl`（zh-cn）替换，还是面/管线错），**不许推断**。
    /// 输出行以 `#` 开头 ⇒ **不是读数行**，不参与任何判据。
    /// </summary>
    private static void RunShapeProbes(List<string> probes)
    {
        foreach (string spec in probes)
        {
            string path = spec; int faceIdx = 0;
            int h = spec.LastIndexOf('#');
            if (h > 0) { path = spec.Substring(0, h); int.TryParse(spec.Substring(h + 1), out faceIdx); }
            foreach (string lang in new[] { "zh-cn", "en-us" })
            {
                try
                {
                    HbShapedRun r = HbShaper.Shape(path, faceIdx, "\u4E0E", 16.0, lang);
                    string gids = r.Glyphs == null ? "-" : string.Join(".", r.Glyphs.Select(g => g.ToString(CultureInfo.InvariantCulture)));
                    Console.WriteLine($"# PROBE-SHAPE face={San(path)}#{faceIdx} lang={lang} cp=U+4E0E gids={gids} " +
                                      $"adv={r.TotalWidthPx.ToString("F4", CultureInfo.InvariantCulture)} upem={r.Upem} " +
                                      $"（来源=本 shim 自己的 HbShaper（hb_shape P/Invoke）=**内部装置**，仅供定位，不参与判据）");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"# PROBE-SHAPE face={San(path)}#{faceIdx} lang={lang} ERROR={e.GetType().Name}:{San(e.Message)}");
                }
            }
        }
    }

    private static int RunOne(string m, ParaSpec spec, string font, double em,
                              GlyphTypeface gt, TextRunProperties props, bool strictProbe)
    {
        HbFallbackDiag.Reset();
        HbFontPlan plan = null;
        if (m != "null")
        {
            var runs = new List<HbRunFaceInfo>
            {
                new HbRunFaceInfo { Start = 0, Length = spec.Text.Length, Face = new HbFaceRef(font, 0),
                                    Weight = 400, Width = 5, Slant = 0, RunSlot = 0 }
            };
            plan = HbFontPlanner.Build(spec.Text, runs, allowFallback: (m == "fb"), gate: null);
        }
        string tag = $"MODE={m} PARA={spec.Name}";
        List<HbTextLine> lines;
        int consumed;
        try
        {
            lines = HbTextLineFactory.FormatParagraph(spec.Text, font, em, spec.Width, gt, 1.0f, props,
                                                      true,        // alwaysCollapsible ← b34 F 例的 ac=true（:459 第 8 实参）
                                                      false,       // hasModifierScope
                                                      0,           // lineHeight
                                                      out consumed,
                                                      plan: plan,
                                                      defaultIncrementalTab: 0,          // b34 语料口径（case 写死 0）
                                                      modifierOpenIndex: -1, modifierScopeEnd: -1, modifierCloseIndex: -1);
        }
        catch (Exception e)
        {
            Console.WriteLine($"{tag} RESULT=NOINFO reason=format:{e.GetType().Name}:{e.Message}");
            return 3;
        }
        if (lines.Count == 0) { Console.WriteLine($"{tag} RESULT=NOINFO reason=no-lines"); return 3; }

        // ---- 取行：与 harness 同口径（`start += L.Length`）----
        int want = spec.SelLine >= 0 ? spec.SelLine : spec.Text.IndexOf(spec.Probe);
        if (want < 0) { Console.WriteLine($"{tag} RESULT=NOINFO reason=probe-not-in-text"); return 3; }
        int start = 0, sel = -1;
        for (int i = 0; i < lines.Count; ++i)
        {
            if (want >= start && want < start + lines[i].Length) { sel = i; break; }
            start += lines[i].Length;
        }
        if (sel < 0) { Console.WriteLine($"{tag} RESULT=NOINFO reason=no-line-covers-idx{want}（lines={lines.Count}）"); return 3; }
        HbTextLine L = lines[sel];
        int selStart = 0;
        for (int i = 0; i < sel; ++i) selStart += lines[i].Length;
        char selCp = selStart < spec.Text.Length ? spec.Text[selStart] : '\0';

        // ---- 折行（口径 = harness :590：椭圆宽 = max(1.0, 行宽*0.5)，TrailingCharacter）----
        string crIdx = "-", crLen = "-", crW = "-", colW = "-", colLen = "-", colHas = "-", colCon = "-";
        double constraint = Math.Max(1.0, L.Width * 0.5);
        colCon = constraint.ToString("F4", CultureInfo.InvariantCulture);
        try
        {
            TextLine col = L.Collapse(new TextCollapsingProperties[] { new EllipsisProps(constraint) });
            colW = col.Width.ToString("F4", CultureInfo.InvariantCulture);
            colLen = col.Length.ToString(CultureInfo.InvariantCulture);
            colHas = col.HasCollapsed ? "true" : "false";
            IList<TextCollapsedRange> ranges = col.GetTextCollapsedRanges();
            if (ranges != null && ranges.Count > 0)
            {
                crIdx = ranges[0].TextSourceCharacterIndex.ToString(CultureInfo.InvariantCulture);
                crLen = ranges[0].Length.ToString(CultureInfo.InvariantCulture);
                crW = ranges[0].Width.ToString("F4", CultureInfo.InvariantCulture);
            }
        }
        catch (Exception e) { Console.WriteLine($"# {tag} collapse-failed {e.GetType().Name}:{e.Message}"); }

        // ---- 观测面：`GetIndexedGlyphRuns()`（`#14` 起已真实现；STRICT 探针仍保留作"桩探测"）----
        string indexed = "UNIMPLEMENTED";
        string faceUri = "-", gid = "-", advDip = "-", advFromTypeface = "-", runCount = "-";
        string runEm = "-", glyphCount = "-", gidLtCount = "-", tfAdvPresent = "-", tfAdvRaw = "-";
        string planFace = plan == null ? "-" : PlanFaceFor(plan, selStart);
        if (strictProbe) Environment.SetEnvironmentVariable("WPF_LINUX_TEXTLINE_STRICT", "1");
        try
        {
            IEnumerable<IndexedGlyphRun> runsEnum = L.GetIndexedGlyphRuns();
            var list = runsEnum == null ? new List<IndexedGlyphRun>() : runsEnum.ToList();
            runCount = list.Count.ToString(CultureInfo.InvariantCulture);
            if (list.Count == 0)
            {
                indexed = "UNIMPLEMENTED";      // 空序列 ⇒ **不是**"没有字形"，是不给数据
            }
            else
            {
                indexed = "OK";
                IndexedGlyphRun hit = list.FirstOrDefault(r => r.TextSourceCharacterIndex <= selStart &&
                                                               r.TextSourceCharacterIndex + r.TextSourceLength > selStart);
                if (hit == null || hit.GlyphRun == null)
                {
                    indexed = "OK-NORUN";       // 观测面在，但没有 run 覆盖被选码元 ⇒ 不许当"读到了"
                }
                else
                {
                    GlyphRun g = hit.GlyphRun;
                    faceUri = g.GlyphTypeface == null ? "-" : g.GlyphTypeface.FontUri.ToString();
                    if (g.FontRenderingEmSize > 0) runEm = g.FontRenderingEmSize.ToString("F4", CultureInfo.InvariantCulture);
                    if (g.GlyphIndices != null && g.GlyphIndices.Count > 0) gid = g.GlyphIndices[0].ToString(CultureInfo.InvariantCulture);
                    if (g.AdvanceWidths != null && g.AdvanceWidths.Count > 0) advDip = g.AdvanceWidths[0].ToString("F4", CultureInfo.InvariantCulture);
                    if (g.GlyphTypeface != null && g.GlyphIndices != null && g.GlyphIndices.Count > 0)
                    {
                        ushort gi = g.GlyphIndices[0];      // GlyphTypeface.AdvanceWidths 的键是 ushort
                        double emUsed = g.FontRenderingEmSize > 0 ? g.FontRenderingEmSize : em;
                        try
                        {
                            glyphCount = g.GlyphTypeface.GlyphCount.ToString(CultureInfo.InvariantCulture);
                            gidLtCount = (gi < g.GlyphTypeface.GlyphCount) ? "true" : "false";
                        }
                        catch (Exception e2) { glyphCount = "ERROR:" + e2.GetType().Name; }
                        if (g.GlyphTypeface.AdvanceWidths != null && g.GlyphTypeface.AdvanceWidths.ContainsKey(gi))
                        {
                            double raw = g.GlyphTypeface.AdvanceWidths[gi];
                            tfAdvPresent = "true";
                            tfAdvRaw = raw.ToString("F6", CultureInfo.InvariantCulture);
                            advFromTypeface = (raw * emUsed).ToString("F4", CultureInfo.InvariantCulture);
                        }
                        else tfAdvPresent = "false";
                    }
                }
            }
        }
        catch (NotSupportedException e)
        {
            indexed = "UNIMPLEMENTED";
            Console.WriteLine($"# {tag} indexed-probe threw NotSupportedException: {e.Message}");
        }
        catch (Exception e)
        {
            indexed = "ERROR:" + e.GetType().Name + ":" + e.Message.Replace(' ', '_');
        }
        finally
        {
            if (strictProbe) Environment.SetEnvironmentVariable("WPF_LINUX_TEXTLINE_STRICT", null);
        }

        Func<double, string> f4 = v => v.ToString("F4", CultureInfo.InvariantCulture);
        Console.WriteLine(
            $"{tag} RESULT=OK LINES={lines.Count} SEL={sel} SEL_START={selStart} SEL_LEN={L.Length} " +
            $"SEL_TEXT={San(L.LineTextForDiag)} CP=U+{(int)selCp:X4} LINE_W={f4(L.Width)} LINE_WITW={f4(L.WidthIncludingTrailingWhitespace)} " +
            $"LINE_NL={L.NewlineLength} LINE_WS={L.TrailingWhitespaceLength} HAS_OVERFLOWED={(L.HasOverflowed ? "true" : "false")} " +
            $"COL_CONSTRAINT={colCon} COL_W={colW} COL_LEN={colLen} COL_HAS={colHas} CR_IDX={crIdx} CR_LEN={crLen} CR_W={crW} " +
            $"INDEXED={indexed} RUNS={runCount} RUN_EM={runEm} FACE_URI={faceUri} GID={gid} ADV_DIP={advDip} " +
            $"ADV_FROM_TYPEFACE={advFromTypeface} TF_ADV_PRESENT={tfAdvPresent} TF_ADV_RAW={tfAdvRaw} " +
            $"GLYPH_COUNT={glyphCount} GID_LT_COUNT={gidLtCount} PLAN_FACE={San(planFace)} "
            + $"FONTDIR_ENV={San(EnvOr("WPF_LINUX_FONT_DIR", "-"))} MULTIFONT_ENV={San(EnvOr("WPF_LINUX_MULTIFONT", "-"))}");
        Console.WriteLine(
            $"# SRC {tag} LINE_W=TextLine.Width(公开API,值由本shim算) COL_W/CR_W=TextLine.Collapse+GetTextCollapsedRanges(公开API) " +
            $"ADV_DIP=IndexedGlyphRun.GlyphRun.AdvanceWidths[0](公开API,值由本shim写入) " +
            $"ADV_FROM_TYPEFACE=GlyphTypeface.AdvanceWidths[gid]*RUN_EM(公开API) GLYPH_COUNT=GlyphTypeface.GlyphCount(公开API) " +
            $"FACE_URI/GID=GlyphRun.GlyphTypeface.FontUri/GlyphRun.GlyphIndices[0](公开API) PLAN_FACE=本runner自己喂的计划");
        Console.WriteLine($"{tag} DIAG={L.Diagnostics}");
        Console.WriteLine($"{tag} FALLBACK_DIAG={HbFallbackDiag.SummaryFragment()}");
        // 【主控 #15 任务 1 的判据量】`FaceLoads/Evictions/Scans/candidates` 在 **scaffold** 摘要里（含内层 fallback 片段）
        Console.WriteLine($"{tag} SCAFFOLD_DIAG={ScaffoldDiag()}");
        // 【主控窗口 3 任务 3 / T1d 读码结论】`HbFaceCache` 的四个计数**不在** `#if TEXTLINE_SHIM_DIRECT` 内
        //   ⇒ 本宿主（反射形态）可直接读，无需任何 shim 改动。
        // 【窗口 4】新增计数（`ResidentCount`/`ReleaseCalls` 等）用**反射**读 ⇒ 本 runner 不依赖 shim 版本：
        //   有就打印真值，没有就打印 `NA`（**绝不假装读到**）；同时自报"哪些名字找到了"。
        Console.WriteLine($"{tag} FACECACHE_DIAG={FaceCacheDiag()}");   // 该摘要含 faceLoads/faceEvictions/candidates(扫描N次)
        return 0;
    }

    /// <summary>
    /// **面选择普查**（主控 #15 任务 3）：多码点 × 多段落字体，逐格报"选中哪一面"。
    /// 【口径（纪律 18）】每格两个来源**分开标**：
    ///   · `FACE_URI`/`GID` = **公开 API**（`IndexedGlyphRun.GlyphRun`）——修后这才是真面；**修前它是 D-F1b 的错面**（不可用作对照）
    ///   · `SEL_PLAN` = **内部装置**：反射读 `HbTextLine._plan` 后 `HbFontPlan.Describe()`（形如 `[0,2) /path#0 (按码点回退) slot=0`）
    ///     ⇒ **修前/修后都用它做对照**（同一装置、同一口径，apples-to-apples）
    ///   · `API_EQ_PLAN` = 上面两者是否同源（修后应为 true；修前 false ⇒ 正是 D-F1b）
    /// 输出一律以 `#` 开头 ⇒ **不是读数行**，不参与判据；只供 `diff` 逐格比。
    /// </summary>
    private static int RunCensus(string font, double em, double width, TextRunProperties props, string cpsArg)
    {
        var fonts = new List<(string label, string path, string weight)>();
        foreach (string w in new[] { "Regular", "Bold", "Italic", "BoldItalic" })
        {
            string p = System.IO.Path.Combine(Repo(), "src", "Linux", "build", "fonts", "NotoSans-" + w + ".ttf");
            if (System.IO.File.Exists(p)) fonts.Add(("NotoSans-" + w, p, w));
        }
        string ui = System.IO.Path.Combine(Repo(), "src", "Linux", "build", "fonts-ui", "UI-NoLayout.ttf");
        if (System.IO.File.Exists(ui)) fonts.Add(("UI-NoLayout", ui, "Regular"));
        var cps = new List<int>();
        if (!string.IsNullOrEmpty(cpsArg))
            foreach (string t in cpsArg.Split(',')) cps.Add(Convert.ToInt32(t.Replace("U+", "").Replace("0x", ""), 16));
        else
            foreach (int c in new[] { 0x006E, 0x4E0E, 0x6C49, 0x05D0, 0x0627, 0x2192, 0xE000, 0x10FFFD }) cps.Add(c);

        Console.WriteLine("# CENSUS-LEGEND 判据量=SEL_PLAN（**内部装置**：反射读 _plan + Describe()，修前/修后同一口径）；"
                          + "FACE_URI/GID=**公开 API**（修前是 D-F1b 的错面，**不可**用作对照）；API_EQ_PLAN=两者是否同源");
        Console.WriteLine($"# CENSUS-GRID fonts={fonts.Count} cps={cps.Count} em={em} width={width} "
                          + $"WPF_LINUX_FONT_DIR={(Environment.GetEnvironmentVariable("WPF_LINUX_FONT_DIR") ?? "<未设>")}");
        FieldInfo planField = typeof(HbTextLine).GetField("_plan",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        foreach ((string label, string path, string weight) in fonts)
        {
            GlyphTypeface gt;
            try { gt = GetGlyphTypeface(System.IO.Path.GetDirectoryName(path), weight); }
            catch (Exception e)
            {
                Console.WriteLine($"# CENSUS font={label} GT=ERROR:{e.GetType().Name}");
                continue;
            }
            foreach (int cp in cps)
            {
                string text = char.ConvertFromUtf32(cp) + " ";
                string sel = "-", face = "-", gid = "-", adv = "-", lw = "-", eq = "-", why = "";
                try
                {
                    List<HbTextLine> lines = HbTextLineFactory.FormatParagraph(text, path, em, width, gt, 1.0f, props,
                                                        true, false, 0, out int _c, plan: null,
                                                        defaultIncrementalTab: 0,
                                                        modifierOpenIndex: -1, modifierScopeEnd: -1, modifierCloseIndex: -1);
                    if (lines.Count == 0) { why = "no-lines"; }
                    else
                    {
                        HbTextLine L = lines[0];
                        lw = L.Width.ToString("F4", CultureInfo.InvariantCulture);
                        if (planField != null)
                        {
                            object po = planField.GetValue(L);
                            if (po is HbFontPlan pl) sel = San(pl.Describe());
                        }
                        foreach (IndexedGlyphRun r in L.GetIndexedGlyphRuns())
                        {
                            if (r.TextSourceCharacterIndex <= 0 && r.TextSourceCharacterIndex + r.TextSourceLength > 0 && r.GlyphRun != null)
                            {
                                GlyphRun g = r.GlyphRun;
                                face = g.GlyphTypeface == null ? "-" : g.GlyphTypeface.FontUri.ToString();
                                if (g.GlyphIndices != null && g.GlyphIndices.Count > 0) gid = g.GlyphIndices[0].ToString(CultureInfo.InvariantCulture);
                                if (g.AdvanceWidths != null && g.AdvanceWidths.Count > 0) adv = g.AdvanceWidths[0].ToString("F4", CultureInfo.InvariantCulture);
                                break;
                            }
                        }
                        // 同源判定：SEL_PLAN 里的面路径/面号 与 API 报出的 FACE_URI 是否一致（路径为主，.ttc 面号本机取不到 ⇒ 只比路径）
                        if (sel != "-" && face != "-")
                        {
                            string selPath = sel.Contains("#") ? sel.Substring(sel.IndexOf(']') + 1).Trim().Split('#')[0].Trim() : sel;
                            eq = (face.Replace("file://", "").Trim() == selPath.Trim()) ? "true" : "false";
                        }
                    }
                }
                catch (Exception e) { why = e.GetType().Name; }
                Console.WriteLine($"# CENSUS font={label} CP=U+{cp:X4} LINE_W={lw} GID={gid} ADV={adv} "
                                  + $"FACE_URI={San(face)} SEL_PLAN={sel} API_EQ_PLAN={eq}{(why.Length > 0 ? " WHY=" + why : "")}");
            }
        }
        return 0;
    }

    private static readonly string[] FaceCacheFields =
        { "FaceLoads", "FaceLoadFailures", "Evictions", "CoverClears", "ResidentCount", "ReleaseCalls", "MaxFaces",
          "CoverEntries", "s_coverEntries", "s_faces", "MaxCoverEntries", "SegmentFaceResolveCalls",
          "LiveBlobs", "LiveBlobsPeak" };

    /// <summary>把 `HbFaceCache` 的计数**逐名反射**读出来（有则真值、无则 NA）——避免"编译期依赖某个 shim 版本"。</summary>
    private static string FaceCacheDiag()
    {
        // 逐名在**全部类型**里找（有些计数不在 HbFaceCache 里，例如 `SegmentFaceResolveCalls` 在 HbFallbackDiag ⇒ 只搜 HbFaceCache 会 NA）
        Type[] types = typeof(HbTextLine).Assembly.GetTypes();
        Type t = types.FirstOrDefault(x => x.Name == "HbFaceCache");
        if (t == null) return "NA(本形态没有 HbFaceCache)";
        var sb = new System.Text.StringBuilder();
        int got = 0;
        foreach (string name in FaceCacheFields)
        {
            object v = null;
            try
            {
                foreach (Type cand in new[] { t }.Concat(types))
                {
                    FieldInfo fi = cand.GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    if (fi != null) { v = fi.GetValue(null); break; }
                    PropertyInfo pi = cand.GetProperty(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    if (pi != null) { v = pi.GetValue(null); break; }
                }
            }
            catch (Exception e) { v = "ERR:" + e.GetType().Name; }
            if (v != null)
            {
                got++;
                // 容器型字段只报 Count（别把整个字典倒进日志）
                if (v is System.Collections.ICollection coll) v = coll.Count;
                else if (v is System.Collections.IDictionary dict2) v = dict2.Count;
            }
            sb.Append(name.Substring(0, 1).ToLowerInvariant()).Append(name.Substring(1)).Append('=')
              .Append(v == null ? "NA" : v.ToString()).Append(' ');
        }
        sb.Append($"（找到 {got}/{FaceCacheFields.Length} 个字段）");
        return sb.ToString();
    }

    private static MethodInfo s_scaffoldSummary;
    private static string s_scaffoldTypeName;
    /// <summary>`HbTextFallback.SummaryFragment()`（含 faceLoads/faceEvictions/candidates）——它是 **嵌套 internal 类**，
    /// 直接写类型名编不过（CS0122 实测）⇒ 用反射取；取不到就打 `NA`（**绝不静默**）。</summary>
    private static string ScaffoldDiag()
    {
        if (s_scaffoldSummary == null)
        {
            // 【2026-09-15 实测】按类型名 GetType / FirstOrDefault 都取不到（打印 NA）⇒ 改为遍历**全部类型**
            //   找"静态 string SummaryFragment()"（嵌套与顶层都能命中），优先名字带 HbTextFallback 的。
            Type[] all = typeof(HbTextLine).Assembly.GetTypes();
            var cands = all.Select(t => new { t, m = t.GetMethod("SummaryFragment",
                            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public) })
                           .Where(x => x.m != null && x.m.ReturnType == typeof(string) && x.m.GetParameters().Length == 0)
                           .ToList();
            var pick = cands.FirstOrDefault(x => x.t.Name.Contains("HbTextFallback")) ?? cands.FirstOrDefault();
            if (pick != null) { s_scaffoldSummary = pick.m; s_scaffoldTypeName = pick.t.Name; }
        }
        if (s_scaffoldSummary == null) return "NA(本形态下**没有**任何静态 SummaryFragment：`HbTextFallback` 在 `#if TEXTLINE_SHIM_DIRECT`（:3677）里 ⇒ 反射形态编不进来)";
        try { return $"[取到的是 {s_scaffoldTypeName}.SummaryFragment]" + ((string)s_scaffoldSummary.Invoke(null, null) ?? "-"); }
        catch (Exception e) { return "ERR:" + e.GetType().Name; }
    }

    /// <summary>环境变量取值（未设 ⇒ 给定标签）。**口径硬要求**：字体环境会改读数，故必须随行记。</summary>
    private static string EnvOr(string name, string unsetLabel)
        => Environment.GetEnvironmentVariable(name) ?? unsetLabel;

    /// <summary>把任意字符串压成"无空白、可逐字段切分"的形式（码点保留 ==&gt; 如 U+00A0 变 ".00A0"）。</summary>
    private static string San(string s)
    {
        if (string.IsNullOrEmpty(s)) return "-";
        var sb = new System.Text.StringBuilder();
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c) || c == '=') sb.Append('.').Append(((int)c).ToString("X4"));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>我们自己喂进去的计划里，覆盖该段落下标的那一段的面（**我们的输入**，不是 API 的说法）。</summary>
    private static string PlanFaceFor(HbFontPlan plan, int paragraphIndex)
    {
        try
        {
            foreach (HbFontSegment s in plan.Segments)
                if (s.Start <= paragraphIndex && paragraphIndex < s.End) return s.Face.ToString();
            return plan.Segments.Count > 0 ? plan.Segments[0].Face.ToString() : "-";
        }
        catch (Exception e) { return "ERR:" + e.GetType().Name; }
    }

    private static GlyphTypeface GetGlyphTypeface(string fontDir, string weight)
    {
        const string TI = "MS.Internal.Text.TextInterface";
        Assembly dwf = Assembly.Load("DirectWriteForwarder");
        Type colType = dwf.GetType(TI + ".FontCollection", true);
        object col = colType.GetMethod("FromDirectory",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Invoke(null, new object[] { fontDir });
        Type famType = dwf.GetType(TI + ".FontFamily", true);
        PropertyInfo indexer = colType.GetProperty("Item",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, famType, new[] { typeof(uint) }, null);
        object family = indexer.GetValue(col, new object[] { 0u });
        object w = Enum.Parse(dwf.GetType(TI + ".FontWeight", true), weight);
        object stretch = Enum.Parse(dwf.GetType(TI + ".FontStretch", true), "Normal");
        object style = Enum.Parse(dwf.GetType(TI + ".FontStyle", true), "Normal");
        object font = family.GetType()
            .GetMethod("GetFirstMatchingFont", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Invoke(family, new[] { w, stretch, style });
        ConstructorInfo gi = typeof(GlyphTypeface).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            null, new[] { font.GetType() }, null);
        return (GlyphTypeface)gi.Invoke(new[] { font });
    }
}
