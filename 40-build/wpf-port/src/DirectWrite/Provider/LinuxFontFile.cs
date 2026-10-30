// T2 · Phase 1 —— 字体文件（对应骨架的 MS.Internal.Text.TextInterface.FontFile）
// =====================================================================================
// 【Analyze 的判据来自 SFNT 规范，不是猜的】
//   文件类型由 SFNT 版本标签决定：
//     0x00010000  → TrueType（实测 NotoSans-*.ttf 的首 4 字节就是这个，不是 'true'）
//     'true'      → TrueType（Apple 变体）
//     'typ1'      → Type1（包在 SFNT 里的）
//     'OTTO'      → CFF
//     'ttcf'      → TrueTypeCollection（字体集合，TTC header 里有 numFonts）
//   字形类型同理：CFF 表存在 → CFF，否则 TrueType。
//   这些判据与 DWrite 的 IDWriteFontFile::Analyze 输出一致。
//
// 【numberOfFaces 的来源】
//   TTC：'ttcf' + version + numFonts(offset 8)。
//   非 TTC：恒为 1（一个文件一个面）。
//
// 【GetUriPath】
//   DWrite 的 IDWriteLocalFontFileLoader::GetFilePathFromKey 给的是**本地文件路径**。
//   Linux 侧我们本来就是从路径加载的，所以直接返回加载时用的路径；
//   经字节流加载（嵌入字体）时返回""（空串）—— 明确表示"没有文件路径"，
//   而不是编一个假的 file:// URI 出来。

using System;
using System.IO;

namespace MS.Internal.Text.TextInterface.Linux
{
    /// <summary>一个字体文件（路径 + 分析信息）。</summary>
    public sealed class LinuxFontFile : IDisposable
    {
        private readonly OpenTypeFontData _openType;
        private IntPtr _token;

        internal LinuxFontFile(string sourcePath, int faceIndex, OpenTypeFontData openType)
        {
            SourcePath = sourcePath;
            FaceIndex = faceIndex;
            _openType = openType;
        }

        /// <summary>
        /// 按路径构造（**自己解析文件字节**，不经过 Skia）。
        /// 给 InternalFactory.CreateFontFile 这类"只有 Uri、没有 typeface"的调用方用。
        /// 文件不是字体/面下标越界 → 抛（调用方据此返回 DWRITE_E_FILEFORMAT）。
        /// </summary>
        /// <param name="stripLayout">
        /// 运行期剥离 GSUB/GPOS（T1/M7c5 路线 C）。null=默认（**开**）；false=本次旁路；true=强制剥。
        /// </param>
        public static LinuxFontFile FromPath(string path, int faceIndex = 0)
            => FromPath(path, faceIndex, stripLayout: null);

        /// <summary>按路径构造，**并显式指定是否运行期剥离 GSUB/GPOS**（T1/M7c5）。</summary>
        public static LinuxFontFile FromPath(string path, int faceIndex, bool? stripLayout)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            if (!File.Exists(path)) throw new FileNotFoundException("字体文件不存在：" + path, path);

            byte[] effective = FontLayoutStripping.Apply(File.ReadAllBytes(path), stripLayout, out bool didStrip, out _);
            if (!didStrip) effective = File.ReadAllBytes(path);
            OpenTypeFontData data = OpenTypeFontData.FromSfnt(effective, faceIndex);
            return new LinuxFontFile(path, faceIndex, data);
        }

        /// <summary>
        /// 按字节构造（WPF 的嵌入字体 / 资源字体走这条路：IFontSource → 字节）。
        /// <paramref name="tag"/> 只作诊断标签，**不会**被当成路径。
        /// </summary>
        public static LinuxFontFile FromBytes(byte[] data, int faceIndex = 0, string tag = null)
            => FromBytes(data, faceIndex, tag, stripLayout: null);

        /// <summary>按字节构造，**并显式指定是否运行期剥离 GSUB/GPOS**。</summary>
        public static LinuxFontFile FromBytes(byte[] data, int faceIndex, string tag, bool? stripLayout)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            byte[] effective = FontLayoutStripping.Apply(data, stripLayout, out bool didStrip, out _);
            if (!didStrip) effective = data;
            OpenTypeFontData parsed = OpenTypeFontData.FromSfnt(effective, faceIndex);
            return new LinuxFontFile(null, faceIndex, parsed);
        }

        /// <summary>文件路径；来自字节流时为 null。</summary>
        public string SourcePath { get; }

        /// <summary>本文件在我们这份字体集合里的面下标。</summary>
        public int FaceIndex { get; }

        /// <summary>文件类型（按 SFNT 版本标签）。</summary>
        public FontFileKind FileKind => ClassifyFileKind(_openType?.SfntVersion ?? 0);

        /// <summary>字形类型（CFF 表存在则为 CFF）。</summary>
        public FontFaceKind FaceKind => _openType != null && _openType.TryGet(TableTags.Cff, out _)
            ? FontFaceKind.CFF
            : FontFaceKind.TrueType;

        /// <summary>集合里的面数：TTC 取 header 的 numFonts，否则 1。</summary>
        public int NumberOfFaces => Math.Max(1, _openType?.FaceCount ?? 1);

        /// <summary>
        /// DWrite 的 <c>FontFile.Analyze</c>。
        /// 返回 true 表示三个 out 参数有效；文件不可读/不是字体 → false（不抛）。
        /// </summary>
        public bool Analyze(out FontFileAnalysis analysis)
        {
            if (_openType == null)
            {
                analysis = default;
                return false;
            }

            analysis = new FontFileAnalysis(FileKind, FaceKind, NumberOfFaces);
            return true;
        }

        /// <summary>
        /// <c>FontFile.GetUriPath</c>：本地路径。字节流加载时返回空串。
        /// 上游语义是"本地文件路径"（IDWriteLocalFontFileLoader），不是 URI 字符串。
        /// </summary>
        public string GetUriPath() => SourcePath ?? string.Empty;

        /// <summary>
        /// 直接从文件字节分析（不经过 Skia）—— 供 <c>InternalFactory.CreateFontFile</c>
        /// 的接线使用：那条路只有 Uri，没有 typeface。
        /// </summary>
        public static bool AnalyzeFile(string path, int faceIndex, out FontFileAnalysis analysis, out OpenTypeFontData data)
        {
            analysis = default;
            data = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

            try
            {
                data = OpenTypeFontData.FromSfnt(File.ReadAllBytes(path), faceIndex);
            }
            catch (Exception)
            {
                // 不是字体文件 / 面下标越界 → 与 DWrite 的 DWRITE_E_FILEFORMAT 等价：返回 false。
                data = null;
                return false;
            }

            analysis = new FontFileAnalysis(ClassifyFileKind(data.SfntVersion), 
                                            data.TryGet(TableTags.Cff, out _) ? FontFaceKind.CFF : FontFaceKind.TrueType,
                                            Math.Max(1, data.FaceCount));
            return true;
        }

        private static FontFileKind ClassifyFileKind(uint sfntVersion)
        {
            if (sfntVersion == TableTags.SfntTrueType || sfntVersion == TableTags.SfntTrue) return FontFileKind.TrueType;
            if (sfntVersion == TableTags.SfntOtto) return FontFileKind.CFF;
            if (sfntVersion == TableTags.SfntTtcf) return FontFileKind.TrueTypeCollection;
            if (sfntVersion == TableTags.SfntTyp1) return FontFileKind.Type1PFB;
            return FontFileKind.Unknown;
        }

        /// <summary>
        /// 稳定的 IntPtr 令牌：骨架的 <c>FontFile.DWriteFontFileNoAddRef</c> 用它。
        /// 与 <see cref="LinuxFontFace.Token"/> 同一套句柄表（见 FontHandleTable 的文件登记节）。
        /// </summary>
        public IntPtr Token
        {
            get
            {
                if (_token == IntPtr.Zero) _token = FontHandleTable.RegisterFile(this);
                return _token;
            }
        }

        /// <summary>骨架的 FontFile 是 IDisposable（上游 ~FontFile）；这里无原生资源。</summary>
        public void Dispose()
        {
            if (_token != IntPtr.Zero)
            {
                FontHandleTable.UnregisterFile(_token);
                _token = IntPtr.Zero;
            }
        }
    }
}
