// T2 · Phase 1 —— 字体面（对应骨架的 MS.Internal.Text.TextInterface.FontFace）
// =====================================================================================
// 【这个类型承担什么】
//   骨架里 FontFace 的 11 个 [PNSE] 成员全部在这里有真实现：
//     Type / Index / SimulationFlags / IsSymbolFont / Metrics / GlyphCount /
//     GetFileZero / GetDesignGlyphMetrics / GetDisplayGlyphMetrics /
//     GetArrayOfGlyphIndices / TryGetFontTable / ReadFontEmbeddingRights
//
// 【数据来源的分工（这是本文件最重要的一段）】
//   · 度量、字形索引、步进、轮廓盒（设计单位）→ **OpenType 表直读**（权威，DWrite 同源；
//     也是"不随 Skia 版本漂移"的那份）；
//   · 光栅化相关的量（advance 的浮点值、墨迹盒、合成粗斜体）→ Skia；
//   · 两者的差异由测试逐项断言（Tests/SkiaParityTests.cs），任何不一致都会红。
//
// 【GetDisplayGlyphMetrics 的取整语义（我们自己定的口径，写死在这里）】
//   DWrite 的 GetGdiCompatibleGlyphMetrics 返回的仍是**设计单位**，只是把值
//   "吸附到物理像素网格"上：即先算 px = du * emSize * pixelsPerDip / upem，
//   取整，再换算回设计单位 du' = round(px) * upem / (emSize * pixelsPerDip)。
//   于是 du' * emSize/upem*pixelsPerDip 恒为整数（Display/GDI 模式的全部意义）。
//   useDisplayNatural=true（Ideal 模式）时不做吸附，线性缩放。
//   DWrite 到底对哪几个字段做吸附是**未公开细节**，我们统一对全部字段做同样的吸附，
//   并由测试断言"像素整数"不变式成立。这条口径变更时只需要改 SnapToPixel 一处。
//
// 【合成粗体/斜体】
//   FontSimulations.Bold → SKFont.Embolden = true（Skia 的合成加粗，**只影响绘制**，
//   不影响度量）；Oblique → SKFont.SkewX = -0.25（DWrite 的合成倾斜角是 14° 左右）。
//   这两条都是近似：真 DWrite 的模拟粗体会让 advance 略微变宽，而 Skia 的 Embolden 不会。
//   → 已登记为"够用但不等价"的条目（REPORT.md §降级清单）。

using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;

namespace MS.Internal.Text.TextInterface.Linux
{
    /// <summary>一个字体面：度量 + 字形索引 + 字形度量 + 字体表访问。</summary>
    public sealed class LinuxFontFace : IDisposable
    {
        /// <summary>DWrite FontSimulations.Bold = 0x0001。</summary>
        public const int SimulationBold = 0x0001;

        /// <summary>DWrite FontSimulations.Oblique = 0x0002。</summary>
        public const int SimulationOblique = 0x0002;

        private readonly bool _ownsTypeface;
        private readonly object _sync = new object();

        private OpenTypeFontData _openType;
        private FontMetricsData _metrics;
        private LinuxFontFile _file;
        private IntPtr _token;
        private int _refCount = 1;
        private bool _disposed;

        private LinuxFontFace(SKTypeface typeface, string sourcePath, int faceIndex, int simulationFlags, bool ownsTypeface, string tag = null)
        {
            Typeface = typeface ?? throw new ArgumentNullException(nameof(typeface));
            SourcePath = sourcePath;
            FaceIndex = faceIndex;
            SimulationFlags = simulationFlags;
            _ownsTypeface = ownsTypeface;
            Tag = tag;
        }

        /// <summary>底层的 Skia 字体面。所有权归本对象（Dispose 时按 <c>ownsTypeface</c> 决定是否释放）。</summary>
        public SKTypeface Typeface { get; }

        /// <summary>
        /// 字体文件路径。**从字节流加载时为 null**（调用方给的名字放在 <see cref="Tag"/> 里）——
        /// 因为 GetUriPath 的语义是"本地文件路径"，不能拿一个诊断标签冒充路径。
        /// </summary>
        public string SourcePath { get; }

        /// <summary>诊断标签（字节流加载时用；文件加载时为 null）。只用于日志/告警，不参与任何查找。</summary>
        public string Tag { get; }

        /// <summary>字体集合（TTC）内的下标；非 TTC 恒为 0。</summary>
        public int FaceIndex { get; }

        /// <summary>DWrite FontSimulations 位标志。</summary>
        public int SimulationFlags { get; }

        // ---------------------------------------------------------------------------------
        //  构造
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// 从字体文件加载一个面。<paramref name="faceIndex"/> 用于 TTC；普通 TTF/OTF 传 0。
        /// 文件打不开/不是字体 → 抛异常（**不回落系统字体、不返回 null**）。
        ///
        /// <paramref name="stripLayout"/>：运行期剥离 GSUB/GPOS（T1/M7c5 路线 C）。
        ///   null = 用 <see cref="FontLayoutStripping.Enabled"/> 的默认（**默认开**）；
        ///   false = 本次旁路（测"原始文件掩码"的断言走这条）；
        ///   true = 本次强制剥。
        /// **不需要剥时本方法逐字节等同于改动前**（Skia 仍走 FromFile，也不额外读文件）。
        /// </summary>
        // ⚠️ 这个 3 参重载的签名**必须逐字保持**：`DirectWriteForwarder.dll` 与
        //    `PresentationCore.dll` 是**预编译产物**（本轮不重建），它们绑定的就是
        //    `FromFile(string, int, int)`。C# 的"可选参数"是编译期糖 ——
        //    把 `bool? stripLayout = null` 加到同一个方法上会**改变元数据签名**，
        //    预编译程序集立刻 MissingMethodException（WiringSmoke 实测踩到过）。
        //    所以剥离参数一律走**新增重载**，绝不改既有签名。
        public static LinuxFontFace FromFile(string path, int faceIndex = 0, int simulationFlags = 0)
            => FromFile(path, faceIndex, simulationFlags, stripLayout: null);

        /// <summary>
        /// 从字体文件加载一个面，**并显式指定是否运行期剥离 GSUB/GPOS**（T1/M7c5 路线 C）。
        /// </summary>
        public static LinuxFontFace FromFile(string path, int faceIndex, int simulationFlags,
                                             bool? stripLayout)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            if (!File.Exists(path)) throw new FileNotFoundException("字体文件不存在：" + path, path);

            if (WantsStripping(stripLayout))
            {
                byte[] effective = FontLayoutStripping.Apply(File.ReadAllBytes(path), stripLayout,
                                                             out bool didStrip, out _);
                if (didStrip)
                {
                    using SKData skData = SKData.CreateCopy(effective);
                    SKTypeface strippedFace = SKTypeface.FromData(skData, faceIndex);
                    if (strippedFace == null)
                        throw new InvalidOperationException(
                            $"Skia 无法从**剥离后**的字节加载字体面（path={path} faceIndex={faceIndex}）；" +
                            "这是剥离器的缺陷，不是字体问题 —— 用 WPF_LINUX_STRIP_LAYOUT=0 可临时绕过");

                    // SourcePath 仍是原文件路径：跨运行时令牌桥（MilFontFace_RegisterFromFile）
                    // 让 MIL 侧按路径加载 —— 那边只用它取**字形轮廓**，与 GSUB/GPOS 无关。
                    return new LinuxFontFace(strippedFace, path, faceIndex, simulationFlags, ownsTypeface: true);
                }
            }

            // 窗口 9：**每路径共享一份 SKData** + 逐面 `FromData`（面号语义不变；只把"每面一份整文件映射"改成"每文件一份"）
            SKTypeface typeface = SkiaFontDataCache.OpenFace(path, faceIndex);
            if (typeface == null)
                throw new InvalidOperationException($"Skia 无法加载字体面（path={path} faceIndex={faceIndex}）");

            return new LinuxFontFace(typeface, path, faceIndex, simulationFlags, ownsTypeface: true);
        }

        /// <summary>本次加载是否要走"读字节 → 剥"这条路（决定要不要付读文件的代价）。</summary>
        private static bool WantsStripping(bool? stripLayout)
            => stripLayout == true || (stripLayout == null && FontLayoutStripping.Enabled);

        /// <summary>
        /// 从内存里的字体字节加载（WPF 的嵌入字体/自定义 IFontSource 走这条路）。
        /// 所有权：字节被复制进 SKData，调用方可以立刻丢掉原数组。
        /// <paramref name="stripLayout"/> 语义见 <see cref="FromFile"/>。
        /// </summary>
        // 同上：4 参签名是预编译程序集绑定的那个，保持不动，剥离参数走新重载。
        public static LinuxFontFace FromBytes(byte[] data, int faceIndex = 0, int simulationFlags = 0,
                                              string tag = null)
            => FromBytes(data, faceIndex, simulationFlags, tag, stripLayout: null);

        /// <summary>从内存字节加载一个面，**并显式指定是否运行期剥离 GSUB/GPOS**。</summary>
        public static LinuxFontFace FromBytes(byte[] data, int faceIndex, int simulationFlags,
                                              string tag, bool? stripLayout)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            byte[] effective = FontLayoutStripping.Apply(data, stripLayout, out bool didStrip, out _);
            if (!didStrip) effective = data;

            using SKData skData = SKData.CreateCopy(effective);
            SKTypeface typeface = SKTypeface.FromData(skData, faceIndex);
            if (typeface == null)
                throw new InvalidOperationException($"Skia 无法从字节流加载字体面（length={data.Length} faceIndex={faceIndex}）");

            // SourcePath 保持 null：字节流没有本地路径，GetUriPath() 会如实返回空串。
            return new LinuxFontFace(typeface, sourcePath: null, faceIndex, simulationFlags, ownsTypeface: true, tag: tag);
        }

        /// <summary>
        /// 包装一个已存在的 SKTypeface。**不接管所有权**（ownsTypeface=false）；
        /// 用于 FontSet 之类已经持有 typeface 的调用方，避免双重释放。
        /// </summary>
        public static LinuxFontFace Wrap(SKTypeface typeface, string sourcePath = null, int faceIndex = 0, int simulationFlags = 0) =>
            new LinuxFontFace(typeface, sourcePath, faceIndex, simulationFlags, ownsTypeface: false);

        // ---------------------------------------------------------------------------------
        //  来自 OpenType 表的数据
        // ---------------------------------------------------------------------------------

        /// <summary>OpenType 表的直读视图（延迟构造，线程安全）。</summary>
        public OpenTypeFontData OpenType
        {
            get
            {
                ThrowIfDisposed();
                if (_openType != null) return _openType;

                lock (_sync)
                {
                    return _openType ??= OpenTypeFontData.FromTypeface(Typeface);
                }
            }
        }

        /// <summary>head.unitsPerEm。</summary>
        public int UnitsPerEm => OpenType.UnitsPerEm;

        /// <summary>maxp.numGlyphs（DWrite 的 GetGlyphCount 返回 UINT16）。</summary>
        public int GlyphCount => OpenType.NumGlyphs;

        /// <summary>字体面的种类：'OTTO' / 有 CFF 表 → CFF，否则 TrueType。</summary>
        public FontFaceKind Type => OpenType.SfntVersion == TableTags.SfntOtto || OpenType.TryGet(TableTags.Cff, out _)
            ? FontFaceKind.CFF
            : OpenType.SfntVersion == TableTags.SfntTtcf ? FontFaceKind.TrueTypeCollection : FontFaceKind.TrueType;

        /// <summary>
        /// IsSymbolFont：存在 cmap(3,0) 符号子表。
        /// 注：DWrite 的判据相同（MS 规范：symbol 字体 = cmap 里有 platform 3 / encoding 0）。
        /// </summary>
        public bool IsSymbolFont => OpenType.HasSymbolCmap;

        /// <summary>
        /// 度量（设计单位，真值）。通路 = OpenType 表；Skia 通路只用于 CapHeight/XHeight
        /// 的二级回落，且结果被 MetricsFactory.Compare 逐字段比对。
        /// </summary>
        public FontMetricsData Metrics
        {
            get
            {
                ThrowIfDisposed();
                if (_metrics != null) return _metrics;

                lock (_sync)
                {
                    if (_metrics != null) return _metrics;

                    SkiaMetricsSnapshot skia = MetricsFactory.AtUpem(Typeface);
                    _metrics = MetricsFactory.FromOpenType(OpenType, skia, Typeface);
                    return _metrics;
                }
            }
        }

        /// <summary>
        /// DWrite 的 <c>FontFace.GetGdiCompatibleMetrics(emSize, pixelsPerDip, identity)</c>：
        /// 把全部度量吸附到像素网格（见文件头）。
        /// </summary>
        public FontMetricsData DisplayMetrics(double emSize, double pixelsPerDip)
        {
            FontMetricsData design = Metrics;
            return MetricsFactory.ToDisplay(design, emSize, pixelsPerDip);
        }

        // ---------------------------------------------------------------------------------
        //  字形索引
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// 码点 → 字形（.notdef = 0）。symbol 字体按 DWrite 规则先试 +0xF000 偏移。
        /// 权威通路是 cmap 表；这里走 Skia 的 GetGlyph（更快），
        /// 两者对整个 BMP 的一致性由 OpenTypeOracleTests 断言。
        /// </summary>
        public ushort GlyphForCodePoint(int codePoint)
        {
            ThrowIfDisposed();
            if (codePoint < 0 || codePoint > 0x10FFFF) return 0;

            using SKFont font = CreateFont(UnitsPerEm);

            if (IsSymbolFont && codePoint >= 0x20 && codePoint <= 0xFF)
            {
                ushort symbolGlyph = font.GetGlyph(codePoint | 0xF000);
                if (symbolGlyph != 0) return symbolGlyph;
            }

            return font.GetGlyph(codePoint);
        }

        /// <summary>DWrite 的 <c>FontFace.GetGlyphIndices</c>：码点数组 → 字形数组（一对一，无 shaping）。</summary>
        public void GetArrayOfGlyphIndices(ReadOnlySpan<int> codePoints, Span<ushort> glyphIndices)
        {
            ThrowIfDisposed();
            if (glyphIndices.Length < codePoints.Length) throw new ArgumentException("glyphIndices 容量不足", nameof(glyphIndices));

            if (IsSymbolFont)
            {
                for (int i = 0; i < codePoints.Length; i++) glyphIndices[i] = GlyphForCodePoint(codePoints[i]);
                return;
            }

            using SKFont font = CreateFont(UnitsPerEm);
            font.GetGlyphs(codePoints, glyphIndices);
        }

        // ---------------------------------------------------------------------------------
        //  字形度量
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// DWrite 的 <c>GetDesignGlyphMetrics</c>（设计单位）。
        ///   lsb = hmtx.leftSideBearing；advanceWidth = hmtx.advanceWidth；
        ///   rsb = advanceWidth - lsb - inkWidth（inkWidth = glyf 的 xMax-xMin，空轮廓为 0）；
        ///   垂直方向：有 vhea+vmtx 就用真值，否则按 DWrite 的合成规则
        ///   （advanceHeight = ascent - descent，tsb = (advanceHeight - inkHeight)/2，voy = yMax + tsb）。
        /// </summary>
        public void GetDesignGlyphMetrics(ReadOnlySpan<ushort> glyphIndices, Span<GlyphMetricsData> glyphMetrics)
        {
            ThrowIfDisposed();
            if (glyphMetrics.Length < glyphIndices.Length) throw new ArgumentException("glyphMetrics 容量不足", nameof(glyphMetrics));

            OpenTypeFontData ot = OpenType;
            FontMetricsData metrics = Metrics;
            bool hasVertical = ot.HasVerticalMetrics;
            using SKFont skiaFont = CreateFont(UnitsPerEm);   /* CFF/位图字形墨迹盒：渲染器同一份面 */

            for (int i = 0; i < glyphIndices.Length; i++)
            {
                ushort glyph = glyphIndices[i];
                glyphMetrics[i] = DesignMetricsOf(ot, metrics, glyph, hasVertical, skiaFont);
            }
        }

        /// <summary>单个字形的设计度量（GetDesignGlyphMetrics 的内核，公开以便测试逐字形比对）。</summary>
        public GlyphMetricsData DesignMetricsOf(ushort glyph)
        {
            ThrowIfDisposed();
            OpenTypeFontData ot = OpenType;
            using SKFont skiaFont = CreateFont(UnitsPerEm);   /* CFF/位图字形墨迹盒：渲染器同一份面 */
            return DesignMetricsOf(ot, Metrics, glyph, ot.HasVerticalMetrics, skiaFont);
        }

        /// <summary>
        /// CFF（Type2 charstring）/ 位图字形的逐字形墨迹盒 —— 走**渲染器同一份面**（Skia）。
        /// ⚠ SkiaSharp 2.88.9 **没有** SKFont.GetGlyphBounds（实测 CS1061）⇒ 用 GetGlyphPath().Bounds。
        ///   · CreateFont(UnitsPerEm) ⇒ bounds 已是设计单位；Skia y 轴向下 ⇒ yMin=-Bottom, yMax=-Top；
        ///   · 空轮廓/无轮廓 ⇒ false（**不编数据**）。
        /// </summary>
        private static bool TryGetSkiaGlyphBounds(SKFont skiaFont, ushort glyph,
                                                  out short xMin, out short yMin, out short xMax, out short yMax)
        {
            xMin = yMin = xMax = yMax = 0;
            if (skiaFont == null) return false;
            using SKPath glyphPath = skiaFont.GetGlyphPath(glyph);
            if (glyphPath == null || glyphPath.PointCount == 0) return false;
            SKRect b = glyphPath.Bounds;
            xMin = (short)Math.Clamp((int)Math.Round(b.Left), short.MinValue, short.MaxValue);
            xMax = (short)Math.Clamp((int)Math.Round(b.Right), short.MinValue, short.MaxValue);
            yMin = (short)Math.Clamp((int)Math.Round(-b.Bottom), short.MinValue, short.MaxValue);
            yMax = (short)Math.Clamp((int)Math.Round(-b.Top), short.MinValue, short.MaxValue);
            return xMax > xMin || yMax > yMin;
        }

        private static GlyphMetricsData DesignMetricsOf(OpenTypeFontData ot, FontMetricsData metrics, ushort glyph, bool hasVertical, SKFont skiaFont)
        {
            var result = new GlyphMetricsData();

            int advance = ot.AdvanceWidth(glyph);
            int lsb = ot.LeftSideBearing(glyph);
            bool hasBounds = ot.TryGetGlyphBounds(glyph, out short xMin, out short yMin, out short xMax, out short yMax);
            if (!hasBounds)   /* CFF / 位图字形：无 glyf 表或该字形不在其中 */
                hasBounds = TryGetSkiaGlyphBounds(skiaFont, glyph, out xMin, out yMin, out xMax, out yMax);

            int inkWidth = hasBounds ? xMax - xMin : 0;
            int inkHeight = hasBounds ? yMax - yMin : 0;

            result.AdvanceWidth = (uint)Math.Max(0, advance);
            result.LeftSideBearing = lsb;
            result.RightSideBearing = advance - lsb - inkWidth;

            int advanceHeight;
            if (hasVertical)
            {
                advanceHeight = ot.AdvanceHeight(glyph);
                int tsb = ot.TopSideBearing(glyph, advanceHeight);
                if (tsb == int.MinValue) tsb = (advanceHeight - inkHeight) / 2;

                result.AdvanceHeight = (uint)Math.Max(0, advanceHeight);
                result.TopSideBearing = tsb;
                result.BottomSideBearing = advanceHeight - tsb - inkHeight;
                result.VerticalOriginY = (hasBounds ? yMax : 0) + tsb;
            }
            else
            {
                // DWrite 对没有 vmtx 的字体的合成规则（行盒高度、垂直居中、原点在 yMax + tsb）。
                advanceHeight = metrics.Ascent - metrics.Descent;
                int tsb = (advanceHeight - inkHeight) / 2;

                result.AdvanceHeight = (uint)Math.Max(0, advanceHeight);
                result.TopSideBearing = tsb;
                result.BottomSideBearing = advanceHeight - tsb - inkHeight;
                result.VerticalOriginY = (hasBounds ? yMax : 0) + tsb;
            }

            return result;
        }

        /// <summary>
        /// DWrite 的 <c>GetDisplayGlyphMetrics</c>：设计单位 → 吸附到像素网格（见文件头）。
        /// <paramref name="useDisplayNatural"/> = true 表示 Ideal 模式的线性度量（不吸附）。
        /// </summary>
        public void GetDisplayGlyphMetrics(
            ReadOnlySpan<ushort> glyphIndices,
            Span<GlyphMetricsData> glyphMetrics,
            double emSize,
            bool useDisplayNatural,
            bool isSideways,
            double pixelsPerDip)
        {
            ThrowIfDisposed();
            if (glyphMetrics.Length < glyphIndices.Length) throw new ArgumentException("glyphMetrics 容量不足", nameof(glyphMetrics));

            OpenTypeFontData ot = OpenType;
            using SKFont skiaFont = CreateFont(UnitsPerEm);   /* CFF/位图字形墨迹盒：渲染器同一份面 */
            FontMetricsData metrics = Metrics;
            bool hasVertical = ot.HasVerticalMetrics;
            double upem = ot.UnitsPerEm;

            for (int i = 0; i < glyphIndices.Length; i++)
            {
                GlyphMetricsData design = DesignMetricsOf(ot, metrics, glyphIndices[i], hasVertical, skiaFont);

                // 竖排时用垂直度量当"前进量"（DWrite 的 isSideways 语义）。
                if (isSideways && design.AdvanceHeight > 0)
                {
                    design.AdvanceWidth = design.AdvanceHeight;
                    design.LeftSideBearing = design.TopSideBearing;
                    design.RightSideBearing = design.BottomSideBearing;
                }

                if (useDisplayNatural)
                {
                    glyphMetrics[i] = design;    // 线性：设计单位原样（缩放由调用方按 emSize/upem 做）
                    continue;
                }

                double scale = emSize * pixelsPerDip / upem;    // 设计单位 → 物理像素
                if (scale <= 0)
                {
                    glyphMetrics[i] = design;
                    continue;
                }

                glyphMetrics[i] = new GlyphMetricsData
                {
                    AdvanceWidth = (uint)Math.Max(0, SnapToPixel((int)design.AdvanceWidth, scale)),
                    LeftSideBearing = SnapToPixel(design.LeftSideBearing, scale),
                    RightSideBearing = SnapToPixel(design.RightSideBearing, scale),
                    TopSideBearing = SnapToPixel(design.TopSideBearing, scale),
                    AdvanceHeight = (uint)Math.Max(0, SnapToPixel((int)design.AdvanceHeight, scale)),
                    BottomSideBearing = SnapToPixel(design.BottomSideBearing, scale),
                    VerticalOriginY = SnapToPixel(design.VerticalOriginY, scale),
                };
            }
        }

        /// <summary>把一个设计单位的值吸附到物理像素网格上，再换算回设计单位（结果使 px 为整数）。</summary>
        public static int SnapToPixel(int designUnits, double scale) =>
            (int)Math.Round(Math.Round(designUnits * scale) / scale, MidpointRounding.AwayFromZero);

        // ---------------------------------------------------------------------------------
        //  步进（advance）
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// 设计单位步进（hmtx）。这是"advance 的真值"，Skia 在 size=upem 时给出的
        /// 浮点值取整后必须等于它（SkiaParityTests 对全字体所有字形断言）。
        /// </summary>
        public int DesignAdvance(ushort glyph) => OpenType.AdvanceWidth(glyph);

        /// <summary>批量设计单位步进。</summary>
        public void GetDesignAdvances(ReadOnlySpan<ushort> glyphIndices, Span<int> advances)
        {
            ThrowIfDisposed();
            if (advances.Length < glyphIndices.Length) throw new ArgumentException("advances 容量不足", nameof(advances));

            OpenTypeFontData ot = OpenType;
            for (int i = 0; i < glyphIndices.Length; i++) advances[i] = ot.AdvanceWidth(glyphIndices[i]);
        }

        // ---------------------------------------------------------------------------------
        //  字体表
        // ---------------------------------------------------------------------------------

        /// <summary>DWrite 的 <c>TryGetFontTable</c>：表存在返回 true 并给出字节副本。</summary>
        public bool TryGetFontTable(uint openTypeTableTag, out byte[] tableData)
        {
            ThrowIfDisposed();
            bool exists = OpenType.TryGet(openTypeTableTag, out tableData);
            if (!exists) tableData = null;
            return exists;
        }

        /// <summary>
        /// DWrite 的 <c>ReadFontEmbeddingRights</c>：OS/2 偏移 8 处的 fsType（大端 uint16）。
        /// 与上游 FontFace.cpp:228-258 逐行同构（含"表长度不足则返回 false"）。
        /// </summary>
        public bool ReadFontEmbeddingRights(out ushort fsType)
        {
            ThrowIfDisposed();
            fsType = 0;

            if (!OpenType.TryGet(TableTags.OS2, out byte[] os2) || os2 == null) return false;

            const int OffsetOs2FsType = 8;
            if (os2.Length < OffsetOs2FsType + 2) return false;      // 上游判据是 +1，但读两个字节才能真正取值

            fsType = OpenTypeFontData.U16(os2, OffsetOs2FsType);
            return true;
        }

        // ---------------------------------------------------------------------------------
        //  字体文件
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// DWrite 的 <c>FontFace.GetFileZero</c>：字体面的第 0 个文件。
        /// **同一个面反复调用返回同一个对象** —— 因为骨架会拿它的令牌当
        /// <c>IDWriteFontFile*</c> 传递（Factory.cs:162/212），对象身份必须稳定，
        /// 否则令牌会随调用漂移。
        /// </summary>
        public LinuxFontFile GetFileZero()
        {
            ThrowIfDisposed();
            return _file ??= new LinuxFontFile(SourcePath, FaceIndex, OpenType);
        }

        // ---------------------------------------------------------------------------------
        //  SKFont / 句柄 / 生命周期
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// 造一个 SKFont。合成粗斜体（<see cref="SimulationFlags"/>）在这里生效：
        /// Bold → Embolden，Oblique → SkewX。**只影响绘制，不影响度量**（见文件头）。
        /// </summary>
        public SKFont CreateFont(double size)
        {
            ThrowIfDisposed();
            var font = new SKFont(Typeface, (float)size)
            {
                LinearMetrics = true,
                Subpixel = true,
                Hinting = SKFontHinting.None,
            };

            if ((SimulationFlags & SimulationBold) != 0) font.Embolden = true;
            if ((SimulationFlags & SimulationOblique) != 0) font.SkewX = -0.25f;   // 约 14° 的合成倾斜

            return font;
        }

        /// <summary>
        /// 稳定的 IntPtr 令牌：DWrite 的 <c>DWriteFontFaceAddRef</c> 的 Linux 等价物。
        /// MIL 层的 MilGlyphRun_GetGlyphOutline 需要它来反查 SKTypeface（见 WIRING.md §3）。
        /// </summary>
        public IntPtr Token
        {
            get
            {
                ThrowIfDisposed();
                if (_token == IntPtr.Zero) _token = FontHandleTable.Register(this);
                return _token;
            }
        }

        /// <summary>DWrite 的 AddRef（引用计数；Linux 侧只是计数，用于对齐生命周期语义）。</summary>
        public void AddRef() => System.Threading.Interlocked.Increment(ref _refCount);

        /// <summary>
        /// DWrite 的 Release / 骨架的 <c>FontFace.Release</c>。当前引用计数（测试用）。
        /// </summary>
        public int RefCount => System.Threading.Volatile.Read(ref _refCount);

        /// <summary>
        /// DWrite 的 Release（骨架的 FontFace.Release）。
        ///
        /// 【为什么这里**不**销毁对象】
        ///   上游 <c>Font::GetFontFace()</c> 每次返回的都是 **+1 引用**的 FontFace
        ///   （Font.cpp:GetFontFace 在缓存命中时 AddRef、未命中时 AddRef 后入缓存），
        ///   调用方在 finally 里 Release 掉自己那一份 —— 而对象本身由缓存持有。
        ///   本工程照抄这个语义（LinuxFont.GetFontFace 每次 AddRef），因此 Release
        ///   只能是"减掉调用方那一份引用"，不能顺手把对象销毁：
        ///   GlyphTypeface.cs 在 7 个不同的 finally 里调 Release，其中任何一次都不代表
        ///   "这个字体面再也用不到了"。
        ///   底层 SKTypeface 的生命周期归 LinuxFontCollection 所有；本对象是托管包装，
        ///   真正的资源释放走 <see cref="Dispose"/>（由集合或测试显式调用）。
        /// </summary>
        public void Release()
        {
            // 计数下限 0：多余的 Release 不会把计数压成负数（可被测试检出"Release 次数不对"）。
            int current;
            do
            {
                current = System.Threading.Volatile.Read(ref _refCount);
                if (current <= 0) return;
            }
            while (System.Threading.Interlocked.CompareExchange(ref _refCount, current - 1, current) != current);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_token != IntPtr.Zero)
            {
                FontHandleTable.Unregister(_token);
                _token = IntPtr.Zero;
            }

            if (_file != null)
            {
                FontHandleTable.UnregisterFile(_file.Token);
                _file = null;
            }

            if (_ownsTypeface) Typeface.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LinuxFontFace));
        }

        public override string ToString() =>
            $"LinuxFontFace({Typeface.FamilyName} faceIndex={FaceIndex} sim=0x{SimulationFlags:X} " +
            $"path={SourcePath ?? "<bytes>"} glyphs={GlyphCount})";
    }
}
