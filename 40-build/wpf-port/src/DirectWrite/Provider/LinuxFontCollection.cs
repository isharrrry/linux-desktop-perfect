// T2 · Phase 1 —— 字体集合（对应骨架的 FontCollection + InternalFactory 的加载侧）
// =====================================================================================
// 【确定性三条（照抄 M1 FontSet.cs 的既有裁决，不另立一套）】
//   1. **只认显式给的目录/文件列表**，绝不回落系统字体、绝不摸 /usr/share/fonts。
//      golden 图的前提是"同一个输入永远出同一个输出"，系统字体参与了就不成立。
//   2. 枚举顺序按文件名 **Ordinal 排序**，不依赖 Directory.EnumerateFiles 的顺序
//      （不同文件系统不保证），因为"谁先被索引"会影响同分匹配的胜出者。
//   3. 坏文件跳过（不抛），但跳过的事实会记进 <see cref="LoadWarnings"/> ——
//      "静默少了一个字体"是必须可查的。
//
// 【与 M1 的差异（刻意的，已量化）】
//   M1 的 FontSet 只索引 <c>(family, isBold, isItalic)</c> 二元桶，
//   本工程索引完整三元组 (weight, width, slant) 并支持 TTC 多面。
//   两者对 WPF FontWeight 枚举的全部取值结论相同（M1ConsistencyTests 断言 30/30）。
//
// 【TTC】
//   同一路径的下标 0,1,2… 逐个尝试直到 FromFile 返回 null。实测
//   NotoSans-*.ttf 在 idx=1 就返回 null（单面文件），所以这套探测对非 TTC 是恒等操作。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SkiaSharp;

namespace MS.Internal.Text.TextInterface.Linux
{
    /// <summary>一个目录/文件列表里的全部字体族与字面。</summary>
    public sealed class LinuxFontCollection : IDisposable
    {
        /// <summary>枚举一个面下标时最多尝试多少个面（TTC 上限保护）。</summary>
        private const int MaxFaceIndexProbe = 64;

        private readonly List<SKTypeface> _typefaces = new List<SKTypeface>();
        private readonly List<string> _fileNames = new List<string>();
        private readonly List<FontFaceEntry> _entries = new List<FontFaceEntry>();
        private readonly List<LinuxFontFamily> _families = new List<LinuxFontFamily>();
        private readonly Dictionary<string, int> _familyIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _warnings = new List<string>();

        /// <summary>构造期的文件清单（FromDirectory/FromDirectories 在 LoadFiles 之前填）。</summary>
        private readonly List<string> PendingFiles = new List<string>();
        private readonly Dictionary<(string Path, int Index), int> _entryLookup = new Dictionary<(string, int), int>();

        private bool _disposed;

        private LinuxFontCollection(string directory)
        {
            Directory = directory;
        }

        /// <summary>集合的来源目录；由文件列表构造时为 null。</summary>
        public string Directory { get; }

        /// <summary>被索引的字体文件名（排序后）。测试用它断言"目录里就这 4 个"。</summary>
        public IReadOnlyList<string> FileNames => _fileNames;

        /// <summary>全部字面（跨族，按 (族名, 字重, 拉伸, 斜体, 路径) 稳定排序）。</summary>
        public IReadOnlyList<FontFaceEntry> Entries => _entries;

        /// <summary>族数量（上游 FontCollection::FamilyCount）。</summary>
        public int FamilyCount => _families.Count;

        /// <summary>族列表（已按族名 Ordinal 排序，保证枚举顺序确定）。</summary>
        public IReadOnlyList<LinuxFontFamily> Families => _families;

        /// <summary>加载过程中跳过的文件/面（坏文件、非字体文件）。空 = 全部成功。</summary>
        public IReadOnlyList<string> LoadWarnings => _warnings;

        /// <summary>上游 FontCollection::default[UINT32]。</summary>
        public LinuxFontFamily this[int familyIndex] => _families[familyIndex];

        /// <summary>
        /// 上游 FontCollection::default[String]：按族名取族。
        /// **找不到返回 null**（上游 DWrite 的 FindFamilyName 走 false 分支时 WPF 会走兜底逻辑），
        /// 不回落系统字体、不造一个空族。
        /// </summary>
        public LinuxFontFamily this[string familyName]
        {
            get
            {
                if (string.IsNullOrEmpty(familyName)) return null;
                return _familyIndex.TryGetValue(familyName, out int index) ? _families[index] : null;
            }
        }

        /// <summary>上游 FontCollection::FindFamilyName。</summary>
        public bool FindFamilyName(string familyName, out int index)
        {
            index = 0;
            if (string.IsNullOrEmpty(familyName)) return false;
            return _familyIndex.TryGetValue(familyName, out index);
        }

        // ---------------------------------------------------------------------------------
        //  加载
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// 从一个目录加载全部 .ttf/.otf/.ttc。目录不存在或为空 → 得到一个空集合（不抛）。
        /// <paramref name="recurse"/>=true 时递归子目录（Linux 的系统字体目录就是
        /// <c>/usr/share/fonts/{truetype,opentype,...}</c> 这种分层结构）。
        /// 递归时按**完整路径** Ordinal 排序，保证枚举顺序与文件系统无关。
        /// </summary>
        /// <param name="stripLayout">
        /// 运行期剥离 GSUB/GPOS（T1/M7c5 路线 C）。null=默认（**开**）；false=本次旁路（测原始文件的断言走这条）；
        /// true=强制剥。语义详见 <see cref="FontLayoutStripping"/>。
        /// </param>
        // ⚠️ 2 参签名是预编译程序集（PC/DWF）绑定的那个，**必须逐字保持**；
        //    剥离参数走新重载（可选参数会改元数据签名 → MissingMethodException）。
        // 默认值必须留着：源码兼容（调用方写 FromDirectory(dir)）；元数据签名仍是 (string,bool)。
        public static LinuxFontCollection FromDirectory(string directory, bool recurse = false)
            => FromDirectory(directory, recurse, stripLayout: null);

        /// <summary>从目录加载，**并显式指定是否运行期剥离 GSUB/GPOS**（T1/M7c5）。</summary>
        public static LinuxFontCollection FromDirectory(string directory, bool recurse, bool? stripLayout)
        {
            var collection = new LinuxFontCollection(directory);

            foreach (string file in EnumerateFontFiles(directory, recurse))
                collection.PendingFiles.Add(file);

            collection.LoadFiles(collection.PendingFiles, stripLayout);
            collection.Build();
            return collection;
        }

        /// <summary>
        /// 从**多个**目录加载并合并（系统字体集合在 Linux 上通常是
        /// /usr/share/fonts + /usr/local/share/fonts + 用户目录）。
        /// 同一路径只算一次；整体按完整路径 Ordinal 排序。
        /// </summary>
        public static LinuxFontCollection FromDirectories(IEnumerable<string> directories, bool recurse = true)
            => FromDirectories(directories, recurse, stripLayout: null);

        /// <summary>从多个目录加载，**并显式指定是否运行期剥离 GSUB/GPOS**。</summary>
        public static LinuxFontCollection FromDirectories(IEnumerable<string> directories, bool recurse,
                                                          bool? stripLayout)
        {
            var collection = new LinuxFontCollection(null);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            if (directories != null)
            {
                foreach (string directory in directories)
                {
                    foreach (string file in EnumerateFontFiles(directory, recurse))
                        if (seen.Add(file)) collection.PendingFiles.Add(file);
                }
            }

            collection.PendingFiles.Sort(StringComparer.Ordinal);
            collection.LoadFiles(collection.PendingFiles, stripLayout);
            collection.Build();
            return collection;
        }

        /// <summary>枚举目录下的字体文件（可选递归）。目录不存在 → 空序列，不抛。</summary>
        private static IEnumerable<string> EnumerateFontFiles(string directory, bool recurse)
        {
            if (string.IsNullOrEmpty(directory) || !System.IO.Directory.Exists(directory))
                yield break;

            var options = recurse
                ? System.IO.SearchOption.AllDirectories
                : System.IO.SearchOption.TopDirectoryOnly;

            var files = new List<string>();
            foreach (string pattern in new[] { "*.ttf", "*.otf", "*.ttc" })
            {
                try
                {
                    files.AddRange(System.IO.Directory.EnumerateFiles(directory, pattern, options));
                }
                catch (Exception)
                {
                    // 权限不足的子目录：跳过（记在 LoadWarnings 里由调用方决定要不要报）
                }
            }

            files.Sort(StringComparer.Ordinal);
            foreach (string file in files) yield return file;
        }

        /// <summary>从显式文件列表加载（顺序不影响结果：内部按路径 Ordinal 排序）。</summary>
        public static LinuxFontCollection FromFiles(IEnumerable<string> paths)
            => FromFiles(paths, stripLayout: null);

        /// <summary>同上，显式给剥离开关。</summary>

        /// <summary>从显式文件列表加载，**并显式指定是否运行期剥离 GSUB/GPOS**。</summary>
        public static LinuxFontCollection FromFiles(IEnumerable<string> paths, bool? stripLayout)
        {
            var collection = new LinuxFontCollection(null);

            var files = new List<string>(paths ?? Array.Empty<string>());
            files.Sort(StringComparer.Ordinal);

            collection.LoadFiles(files, stripLayout);
            collection.Build();
            return collection;
        }

        /// <summary>
        /// 从内存字节加载单个字体面（WPF 的嵌入字体 / 自定义 IFontSource 走这条路）。
        /// 返回的集合只有一个族/一个字面，所有权归调用方（Dispose 时释放 typeface）。
        /// </summary>
        public static LinuxFontCollection FromBytes(byte[] data, int faceIndex = 0, string tag = null)
            => FromBytes(data, faceIndex, tag, stripLayout: null);

        /// <summary>从内存字节加载，**并显式指定是否运行期剥离 GSUB/GPOS**。</summary>
        public static LinuxFontCollection FromBytes(byte[] data, int faceIndex, string tag, bool? stripLayout)
        {
            var collection = new LinuxFontCollection(null);
            try
            {
                LinuxFontFace face = LinuxFontFace.FromBytes(data, faceIndex, 0, tag, stripLayout);

                // path 传 null：FontFaceEntry.FilePath == null 表示"没有文件"，
                // 摘要/日志里会显示成 "<bytes>"（而不是把 tag 伪装成路径）。
                collection.AdoptFace(face.Typeface, null, faceIndex);
            }
            catch (Exception ex)
            {
                collection._warnings.Add($"{tag ?? "<bytes>"}: {ex.GetType().Name}: {ex.Message}");
            }

            collection.Build();
            return collection;
        }

        private void LoadFiles(List<string> files, bool? stripLayout = null)
        {
            foreach (string path in files)
            {
                var loaded = new List<(SKTypeface Face, int Index)>();

                // 剥离**每个文件只做一次**（面下标探测会对同一文件试多次，不能每次重读重剥）。
                byte[] strippedBytes = null;
                if (WantsStripping(stripLayout))
                {
                    try
                    {
                        strippedBytes = FontLayoutStripping.Apply(File.ReadAllBytes(path), stripLayout,
                                                                  out bool didStrip, out _);
                        if (!didStrip) strippedBytes = null;   // 没剥成功 → 走原来的 FromFile
                    }
                    catch (Exception ex)
                    {
                        _warnings.Add($"{Path.GetFileName(path)}: 剥离前读文件失败 {ex.GetType().Name}: {ex.Message}");
                        strippedBytes = null;
                    }
                }

                for (int index = 0; index < MaxFaceIndexProbe; index++)
                {
                    SKTypeface face = null;
                    try
                    {
                        face = strippedBytes != null
                            ? FromStrippedBytes(strippedBytes, index)
                            // 窗口 9：**每路径共享一份 SKData**（不再 `FromFile` ⇒ 不再每面 mmap 整份文件）
                            : SkiaFontDataCache.OpenFace(path, index);
                    }
                    catch (Exception ex)
                    {
                        _warnings.Add($"{Path.GetFileName(path)}[face {index}]: {ex.GetType().Name}: {ex.Message}");
                    }

                    if (face == null) break;
                    loaded.Add((face, index));
                }

                if (loaded.Count == 0)
                {
                    _warnings.Add($"{Path.GetFileName(path)}: Skia 无法加载（已跳过）");
                    continue;
                }

                _fileNames.Add(Path.GetFileName(path));
                foreach ((SKTypeface face, int index) in loaded)
                    AdoptFace(face, path, index);
            }
        }

        /// <summary>本次加载是否要走"读字节 → 剥"这条路。</summary>
        private static bool WantsStripping(bool? stripLayout)
            => stripLayout == true || (stripLayout == null && FontLayoutStripping.Enabled);

        /// <summary>从剥离后的字节建 SKTypeface（SKData 的生命周期由 SkiaSharp 自己持有，与 FromBytes 一致）。</summary>
        private static SKTypeface FromStrippedBytes(byte[] stripped, int faceIndex)
        {
            using SKData data = SKData.CreateCopy(stripped);
            return SKTypeface.FromData(data, faceIndex);
        }

        private void AdoptFace(SKTypeface face, string path, int faceIndex)
        {
            _typefaces.Add(face);

            OpenTypeFontData ot = null;
            try
            {
                ot = OpenTypeFontData.FromTypeface(face);
            }
            catch (Exception ex)
            {
                _warnings.Add($"{Path.GetFileName(path ?? "<bytes>")}[face {faceIndex}]: 表解析失败 {ex.Message}");
            }

            string subFamily = ot?.GetNameString(2) ?? string.Empty;
            string postScript = ot?.GetNameString(6) ?? string.Empty;

            var entry = new FontFaceEntry(
                path,
                faceIndex,
                face.FamilyName ?? string.Empty,
                subFamily,
                postScript,
                face.FontWeight,
                face.FontWidth,
                SlantOf(face.FontSlant));

            _entryLookup[(path ?? string.Empty, faceIndex)] = _entries.Count;
            _entries.Add(entry);
        }

        private static int SlantOf(SKFontStyleSlant slant)
        {
            switch (slant)
            {
                case SKFontStyleSlant.Italic: return 2;
                case SKFontStyleSlant.Oblique: return 1;
                default: return 0;
            }
        }

        private void Build()
        {
            // 族分组：按 FamilyName（Skia 口径，与 WPF Typeface.FamilyName 同源）。
            var byFamily = new Dictionary<string, List<FontFaceEntry>>(StringComparer.OrdinalIgnoreCase);
            var familyOrder = new List<string>();

            foreach (FontFaceEntry entry in _entries)
            {
                if (!byFamily.TryGetValue(entry.FamilyName, out List<FontFaceEntry> list))
                {
                    list = new List<FontFaceEntry>();
                    byFamily[entry.FamilyName] = list;
                    familyOrder.Add(entry.FamilyName);
                }

                list.Add(entry);
            }

            // 族名 Ordinal 排序：枚举顺序必须与文件系统的返回顺序无关。
            familyOrder.Sort(StringComparer.Ordinal);

            foreach (string familyName in familyOrder)
            {
                List<FontFaceEntry> faces = byFamily[familyName];

                // 面内排序：(weight, width, slant, path, faceIndex) —— 匹配同分时取小下标才有意义。
                faces.Sort((a, b) =>
                {
                    int c = a.Weight.CompareTo(b.Weight);
                    if (c != 0) return c;
                    c = a.Width.CompareTo(b.Width);
                    if (c != 0) return c;
                    c = a.Slant.CompareTo(b.Slant);
                    if (c != 0) return c;
                    c = string.CompareOrdinal(a.FilePath ?? string.Empty, b.FilePath ?? string.Empty);
                    if (c != 0) return c;
                    return a.FaceIndex.CompareTo(b.FaceIndex);
                });

                // 族的本地化名：取第一个面的 name 表 nameId=1。
                LocalizedStringsData familyNames = null;
                OpenTypeFontData firstOpenType = FindOpenType(faces[0]);
                if (firstOpenType != null)
                {
                    Dictionary<CultureInfo, string> map = firstOpenType.GetNameStrings(1);
                    if (map.Count == 0) map[CultureInfo.InvariantCulture] = familyName;
                    familyNames = new LocalizedStringsData(map);
                }
                else
                {
                    familyNames = new LocalizedStringsData(
                        new[] { CultureInfo.InvariantCulture }, new[] { familyName });
                }

                var family = new LinuxFontFamily(this, familyName, faces, familyNames, OrdinalNameOf(familyName));

                // 把 typeface 绑到每个字面上（所有权仍在集合）。
                foreach (LinuxFont font in family.Items)
                    font.Typeface = FindTypeface(font.FaceEntry);

                _familyIndex[familyName] = _families.Count;
                _families.Add(family);
            }
        }

        private OpenTypeFontData FindOpenType(FontFaceEntry entry)
        {
            try
            {
                return OpenTypeFontData.FromTypeface(FindTypeface(entry));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private SKTypeface FindTypeface(FontFaceEntry entry)
        {
            // _typefaces 与 _entries 是同步 append 的，顺序一致；但为了 TTC/多文件混排时
            // 也能对上，这里用 (path, faceIndex) 查下标。
            if (_entryLookup.TryGetValue((entry.FilePath ?? string.Empty, entry.FaceIndex), out int index)
                && index < _typefaces.Count)
            {
                return _typefaces[index];
            }

            for (int i = 0; i < _typefaces.Count; i++)
                if (_entries[i].FaceIndex == entry.FaceIndex &&
                    string.Equals(_entries[i].FilePath, entry.FilePath, StringComparison.Ordinal))
                    return _typefaces[i];

            throw new InvalidOperationException("字面与 typeface 的对齐关系丢失：" + entry);
        }

        /// <summary>
        /// 上游 FontFamily::OrdinalName：用于稳定排序的名字。
        /// Linux 侧用族名的不变文化形式（FamilyNames 里没有不变文化时就是 Skia 的族名）。
        /// </summary>
        private static string OrdinalNameOf(string familyName) => familyName ?? string.Empty;

        /// <summary>
        /// 上游 FontCollection::GetFontFromFontFace：从一个字体面反查它所属的 Font。
        /// 找不到返回 null（上游此处会走 GetMatchingFonts 兜底，我们不猜）。
        /// </summary>
        public LinuxFont GetFontFromFontFace(LinuxFontFace fontFace)
        {
            if (fontFace == null) return null;

            foreach (LinuxFontFamily family in _families)
            {
                foreach (LinuxFont font in family.Items)
                {
                    if (ReferenceEquals(font.Typeface, fontFace.Typeface) && font.FaceEntry.FaceIndex == fontFace.FaceIndex)
                        return font;
                }
            }

            return null;
        }

        /// <summary>当前持有的 typeface 数量（测试断言"没有重复加载/没有泄漏"）。</summary>
        public int TypefaceCount => _typefaces.Count;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (SKTypeface typeface in _typefaces) typeface.Dispose();
            _typefaces.Clear();
            _entries.Clear();
            _families.Clear();
            _familyIndex.Clear();
            _entryLookup.Clear();
        }

        public override string ToString() =>
            $"LinuxFontCollection(families={FamilyCount}, faces={_entries.Count}, files={_fileNames.Count})";
    }
}
