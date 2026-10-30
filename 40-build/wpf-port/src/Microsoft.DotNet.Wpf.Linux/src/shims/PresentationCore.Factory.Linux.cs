// T2 · 补丁 G —— PresentationCore 的 **Linux 版 Factory**（替换上游 C++/CLI 依赖件）
// =====================================================================================
// 【为什么必须整份替换，而不是打小补丁】
//   上游 `MS/internal/Text/TextInterface/Factory.cs` 是**围绕一个原生 IDWriteFactory
//   指针**写的：初始化时把函数指针取出来（`DWriteLoader.GetDWriteCreateFactoryFunctionPointer`），
//   之后 6 处直接解引用它的 vtable：
//       RegisterFontFileLoader(73) / RegisterFontCollectionLoader(84) / CreateFontFace(214) /
//       GetSystemFontCollection(281) / CreateCustomFontCollection(312) / CreateTextAnalyzer(328)
//   在 Linux 上：
//     · `DWriteLoader.LoadDWrite()` 找不到 dwrite.dll → 抛 DllNotFoundException；
//     · 即便把它改成 no-op，那个函数指针也是 **null** → `delegate*` 调用 = 进程崩溃；
//     · 6 处 `_factory.Value->…` 对镜像指针解引用 = 段错误。
//   这三件事都在**到达** T2 那 47 条托管实现**之前**发生，所以必须让这条路径不再经过原生指针。
//
// 【为什么选"编译期替换"而不是"伪造 libdwrite.so 的 COM vtable"】
//   备选方案是造一个 `libdwrite.so`，导出 `DWriteCreateFactory` 并返回一个假 vtable，
//   每个方法再回调托管代码。不选它的理由（可实测）：
//     ① 需要为 IDWriteFactory / IDWriteFontFace / IDWriteFontCollection / IDWriteTextAnalyzer
//        四个接口**逐方法**实现 vtable 槽位（顺序必须与 dwrite.h 完全一致），
//        任何一处错位都是"调到隔壁方法"级别的静默错误；
//     ② 每次调用都要托管↔原生往返（反向 P/Invoke + 对象生命周期 + HRESULT 映射），
//        出错面比"直接调托管"大一个量级；
//     ③ 我们**已经有**托管实现（Provider）与托管形态的骨架，伪造 vtable 只是把
//        已经解决的问题重新做一遍，还额外引入一个 .so 的构建与部署环节。
//   结论：编译期替换（本文件）在"改动面 / 可控性 / 可测性"三项上全面占优。
//
// 【本文件替代的范围】
//   上游 Factory.cs 的**全部** internal 面（逐个成员保留同名同签名）：
//     Factory.Create / DWriteFactory / CreateFontFile / CreateFontFace ×2 /
//     GetSystemFontCollection / GetFontCollection / CreateTextAnalyzer / IsLocalUri
//   实现全部落在 T2 的 provider（`MS.Internal.Text.TextInterface.Linux`）与 DWF 骨架
//   （`FontFile`/`FontFace`/`FontCollection`/`TextAnalyzer`）上。
//
// 【系统字体目录从哪来】
//   Windows 上"系统字体"= `%windir%\Fonts`。Linux 上没有唯一答案，所以：
//     · 环境变量 `WPF_LINUX_FONT_DIR`（`:` 分隔多个）**优先** —— 这是宿主/测试控制
//       "看得见哪些字体"的唯一开关，也是确定性测试的依据；
//     · 未设置时回落到 XDG/FHS 的标准位置（/usr/share/fonts、/usr/local/share/fonts、
//       $XDG_DATA_HOME/fonts、$HOME/.local/share/fonts、$HOME/.fonts），**递归**扫描
//       （Linux 的字体目录是分层结构）；
//     · 一个都不存在 → **空集合**（并记进 `SystemFontDirectoryDiagnostics`），
//       而不是抛异常或伪造字体。空集合的表现是"找不到任何族"，这是事实，不是错误。
//
// 【本轮未做的分支（明确登记，不静默）】
//   · 非本地 URI（嵌入式/网络字体）走 `IFontSource` → 读字节 → `LinuxFontFile.FromBytes`。
//     这是**尽力而为**：`IFontSource.GetUnmanagedStream()` 在 Linux 上依赖 PC 的
//     `FileMapping`/资源缓存，若那条路失败，异常会**原样抛出**（不做假成功）。
//   · `Factory.DWriteFactory`（原生 IDWriteFactory* 属性）恒返回 **null**：
//     唯一消费方 `TypefaceMap.cs:114` 把它当参数传给 `TextAnalyzer.Itemize`，
//     而 Itemize 是 T2 的 D 档（立刻抛带说明的 PlatformNotSupportedException）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using MS.Internal.Text.TextInterface.Linux;
using MS.Internal.Text.TextInterface.Native;

namespace MS.Internal.Text.TextInterface
{
    /// <summary>
    /// DWrite 工厂的 Linux 等价物。**不含任何原生指针解引用**：
    /// 每个成员都直接落到 T2 的托管 provider 上。
    /// </summary>
    internal unsafe class Factory
    {
        /// <summary>DWrite 的 DWRITE_E_FILEFORMAT（上游 Factory.cs:16 同值）。</summary>
        private const int DWRITE_E_FILEFORMAT = unchecked((int)0x88985000L);

        private readonly IFontSourceFactory _fontSourceFactory;
        private readonly IFontSourceCollectionFactory _fontSourceCollectionFactory;
        private readonly object _systemCollectionLock = new object();

        private FontCollection _systemFontCollection;
        private string[] _systemFontDirectories;
        private string _systemFontDirectoryDiagnostics;

        private Factory(FactoryType factoryType, IFontSourceCollectionFactory fontSourceCollectionFactory,
                        IFontSourceFactory fontSourceFactory)
        {
            // factoryType（Shared/Isolated）在 Linux 上没有共享/隔离的差别：
            // 我们不做全局原生对象缓存，两者行为一致。保留参数是为了与上游签名一致。
            _ = factoryType;
            _fontSourceCollectionFactory = fontSourceCollectionFactory;
            _fontSourceFactory = fontSourceFactory;
        }

        /// <summary>上游 Factory.Create 的托管等价物。</summary>
        internal static Factory Create(
            FactoryType factoryType,
            IFontSourceCollectionFactory fontSourceCollectionFactory,
            IFontSourceFactory fontSourceFactory)
        {
            return new Factory(factoryType, fontSourceCollectionFactory, fontSourceFactory);
        }

        /// <summary>
        /// 上游返回原生 IDWriteFactory*。Linux 上没有这个对象 —— 恒返回 null。
        /// 唯一消费方是 TypefaceMap.cs:114（把它传给 D-档的 TextAnalyzer.Itemize），
        /// 而那个方法会立刻抛出带说明的 PlatformNotSupportedException，
        /// 所以 null 不会被解引用。（**不是**"假装有个工厂"：这里如实为空。）
        /// </summary>
        internal IDWriteFactory* DWriteFactory => null;

        // =================================================================================
        //  文件 / 字体面
        // =================================================================================

        /// <summary>
        /// 上游 Factory.CreateFontFile：把字体 URI 变成字体文件句柄。
        /// 本地文件 → 直接解析；其它 → 退回 IFontSource（嵌入/资源字体）。
        /// 异常语义与上游一致：目录 → UnauthorizedAccessException；不是字体 → FileFormatException。
        /// </summary>
        internal FontFile CreateFontFile(Uri filePathUri)
        {
            if (filePathUri == null) throw new ArgumentNullException(nameof(filePathUri));

            if (IsLocalUri(filePathUri))
            {
                string path = filePathUri.LocalPath;

                // 上游注释：给一个"目录"而不是字体文件时，DWrite 的 CreateFontFileReference
                // 会成功，但 Analyze 返回 DWRITE_E_FILEFORMAT，WPF 最终抛 UnauthorizedAccessException
                //（Factory.cs:229-241）。这里直接按同样的语义抛。
                if (Directory.Exists(path))
                    throw new UnauthorizedAccessException("对路径\"" + path + "\"的访问被拒绝（给的是目录而不是字体文件）。");

                if (File.Exists(path))
                {
                    try
                    {
                        return new FontFile(LinuxFontFile.FromPath(path, 0));
                    }
                    catch (Exception e) when (e is InvalidOperationException || e is ArgumentOutOfRangeException)
                    {
                        // 不是字体文件 → 与 DWRITE_E_FILEFORMAT 的分支等价。
                        throw new FileFormatException(filePathUri, e);
                    }
                }
            }

            // 非本地 / 不存在的本地路径：走 WPF 自己的 FontSource（资源字体、pack:// 等）。
            IFontSource fontSource = _fontSourceFactory.Create(filePathUri.AbsoluteUri);
            fontSource.TestFileOpenable();
            byte[] bytes = ReadFontSourceBytes(fontSource);
            return new FontFile(LinuxFontFile.FromBytes(bytes, 0, fontSource.Uri?.AbsoluteUri));
        }

        /// <summary>上游 Factory.CreateFontFace(Uri, uint) —— 等价于 faceIndex=0、无模拟标志。</summary>
        internal FontFace CreateFontFace(Uri filePathUri, uint faceIndex)
        {
            return CreateFontFace(filePathUri, faceIndex, FontSimulations.None);
        }

        /// <summary>
        /// 上游 Factory.CreateFontFace(Uri, uint, FontSimulations)。
        /// Linux 侧：文件 → LinuxFontFace.FromFile（含 TTC 面下标与合成粗/斜体标志）。
        /// </summary>
        internal FontFace CreateFontFace(Uri filePathUri, uint faceIndex, FontSimulations fontSimulationFlags)
        {
            if (filePathUri == null) throw new ArgumentNullException(nameof(filePathUri));

            if (IsLocalUri(filePathUri))
            {
                string path = filePathUri.LocalPath;

                if (Directory.Exists(path))
                    throw new UnauthorizedAccessException("对路径\"" + path + "\"的访问被拒绝（给的是目录而不是字体文件）。");

                if (File.Exists(path))
                {
                    try
                    {
                        LinuxFontFace linuxFace = LinuxFontFace.FromFile(path, (int)faceIndex, (int)fontSimulationFlags);

                        // ── M7d 追加 1：Uri 形态的"诚实失败"（**不伪造空面**）──
                        // 上游 GlyphTypeface.Initialize（GlyphTypeface.cs:136-150）拿到面之后必定会走：
                        //     fontCollection = DWriteFactory.GetFontCollectionFromFile(uri)   // 父目录 → 集合
                        //     _font          = fontCollection.GetFontFromFontFace(face)
                        //     _fontFace      = new FontFaceLayoutInfo(_font)                  // ← null 时 NRE
                        // 本移植上这两步**对不上**：面是 `LinuxFontFace.FromFile` 单独造的，集合是
                        // `LinuxFontCollection.FromDirectory` 另造的，而 provider 的反查按
                        // **SKTypeface 引用相等**匹配（src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/LinuxFontCollection.cs:443
                        // `ReferenceEquals(font.Typeface, fontFace.Typeface)`）⇒ 必然 null
                        // ⇒ NRE 会晚到 `FontFaceLayoutInfo.IntMap`（实测栈见 T1 报告"连锁 2"）才爆，
                        //   而 `new GlyphTypeface(new Uri(path))` 是最自然的公开入口。
                        //
                        // 这里**提前问 provider 同一个问题**（能力探测：既不是猜，也不是造空面）：
                        // 这个面能回到它所属的 Font 吗？不能 ⇒ 诚实返回 **null** ⇒ 触发上游**既有**守卫
                        // `if (fontFaceDWrite == null) throw new FileFormatException(typefaceSource)`
                        // （GlyphTypeface.cs:140-143）⇒ 调用方拿到 FileFormatException 而不是 NRE。
                        // 判据是实测的（探针 S7）：FromDirectory(parent).GetFontFromFontFace(FromFile(path)) = null。
                        // 若将来 provider 这条反查能对上（例如共享 SKTypeface 缓存），本守卫自动放行，
                        // 这里一个字都不用改。
                        if (!CanRoundTripFace(filePathUri, linuxFace))
                        {
                            return null;
                        }

                        return new FontFace(linuxFace);
                    }
                    catch (Exception e) when (e is InvalidOperationException || e is ArgumentOutOfRangeException)
                    {
                        throw new FileFormatException(filePathUri, e);
                    }
                }
            }

            // 非本地：资源字体 → 字节 → 面（与 CreateFontFile 同一条兜底路）
            IFontSource fontSource = _fontSourceFactory.Create(filePathUri.AbsoluteUri);
            fontSource.TestFileOpenable();
            byte[] bytes = ReadFontSourceBytes(fontSource);

            try
            {
                return new FontFace(LinuxFontFace.FromBytes(bytes, (int)faceIndex, (int)fontSimulationFlags,
                                                            fontSource.Uri?.AbsoluteUri));
            }
            catch (Exception e) when (e is InvalidOperationException || e is ArgumentOutOfRangeException)
            {
                throw new FileFormatException(filePathUri, e);
            }
        }

        // =================================================================================
        //  集合
        // =================================================================================

        /// <summary>
        /// 上游 Factory.GetSystemFontCollection。
        /// Linux 侧 = 「配置的字体目录」的并集，**递归**扫描，结果缓存（PC 也缓存一层）。
        /// </summary>
        internal FontCollection GetSystemFontCollection()
        {
            if (_systemFontCollection != null) return _systemFontCollection;

            lock (_systemCollectionLock)
            {
                if (_systemFontCollection != null) return _systemFontCollection;

                _systemFontDirectories = ResolveSystemFontDirectories();
                var existing = new List<string>();
                foreach (string directory in _systemFontDirectories)
                    if (Directory.Exists(directory)) existing.Add(directory);

                LinuxFontCollection collection = LinuxFontCollection.FromDirectories(existing, recurse: true);

                _systemFontDirectoryDiagnostics =
                    "directories=[" + string.Join(",", _systemFontDirectories) + "] " +
                    "existing=[" + string.Join(",", existing) + "] " +
                    "files=" + collection.FileNames.Count + " families=" + collection.FamilyCount +
                    " warnings=" + collection.LoadWarnings.Count;

                _systemFontCollection = new FontCollection(collection);
                return _systemFontCollection;
            }
        }

        /// <summary>系统字体目录的解析结果（诊断/测试用；未调用过 GetSystemFontCollection 时为 null）。</summary>
        internal string[] SystemFontDirectories => _systemFontDirectories;

        /// <summary>系统字体目录的诊断串（含"实际存在哪些、枚举到几个文件/族"）。</summary>
        internal string SystemFontDirectoryDiagnostics => _systemFontDirectoryDiagnostics;

        /// <summary>
        /// 上游 Factory.GetFontCollection(Uri)（自定义/嵌入字体集合）。
        /// 本地目录 → 建一个只含该目录的集合；若该目录就是系统字体目录之一 → 复用系统集合
        /// （对应上游"指向 Windows 字体目录就不再重复枚举"的优化，Factory.cs:80-84）。
        /// </summary>
        internal FontCollection GetFontCollection(Uri uri)
        {
            if (uri == null) throw new ArgumentNullException(nameof(uri));

            if (IsLocalUri(uri))
            {
                string localPath = uri.LocalPath;

                if (IsSystemFontDirectory(localPath)) return GetSystemFontCollection();

                if (Directory.Exists(localPath))
                    return new FontCollection(LinuxFontCollection.FromDirectory(localPath, recurse: true));

                if (File.Exists(localPath))
                {
                    // 上游的 GetFontCollectionFromFile 传进来的是父目录；这里再兜一层，
                    // 保证"直接给一个文件 URI"也能得到一个只含它的集合。
                    string parent = Path.GetDirectoryName(localPath);
                    return new FontCollection(LinuxFontCollection.FromDirectory(parent, recurse: false));
                }
            }

            // 非本地（资源字体集合）：单个字体面的集合
            IFontSource fontSource = _fontSourceFactory.Create(uri.AbsoluteUri);
            fontSource.TestFileOpenable();
            byte[] bytes = ReadFontSourceBytes(fontSource);
            return new FontCollection(LinuxFontCollection.FromBytes(bytes, 0, uri.AbsoluteUri));
        }

        // =================================================================================
        //  文本分析器
        // =================================================================================

        /// <summary>
        /// 上游 Factory.CreateTextAnalyzer。
        /// Linux 侧返回一个**托管 TextAnalyzer 实例**（不发任何原生调用）。
        /// 它的 Itemize/GetGlyphs/GetGlyphPlacements 是 T2 的 D 档，
        /// 调用会抛出带说明的 PlatformNotSupportedException ——
        /// **不是**空引用异常，也不是静默返回空结果。
        /// 消费方（TextFormatterImp.cs:768、FormattedTextSymbols.cs:112）只要求非 null。
        /// </summary>
        internal TextAnalyzer CreateTextAnalyzer() => new TextAnalyzer((IDWriteTextAnalyzer*)IntPtr.Zero);

        // =================================================================================
        //  辅助
        // =================================================================================

        /// <summary>上游 Factory.IsLocalUri。</summary>
        internal static bool IsLocalUri(Uri uri) => InternalFactory.IsLocalUri(uri);

        /// <summary>
        /// M7d 追加 1 的能力探测（`CreateFontFace(Uri)` 用它决定"诚实失败"还是"交出这个面"）。
        ///
        /// 判据 = 上游 `GlyphTypeface.Initialize` 那条链上"面 → 所属 Font"的反查能不能对上：
        ///     DWriteFactory.GetFontCollectionFromFile(uri).GetFontFromFontFace(face)
        /// 这里直接用**同一个** `DWriteFactory.GetFontCollectionFromFile`（PC 内部方法，本文件就编在 PC 里）
        /// 拿到集合，再问 provider 同一个问题。DWF 的 `FontCollection.GetFontFromFontFace`
        /// （src/Microsoft.DotNet.Wpf.Linux/src/DirectWriteForwarder/ManagedSurface.cs:883）本体就是
        ///     `font == null ? null : new Font(font)`，其中 `font = _linuxCollection.GetFontFromFontFace(fontFace.LinuxFace)`
        /// ⇒ 用 `collection.LinuxCollection.GetFontFromFontFace(linuxFace)` 判等**完全等价**，
        ///   而且不必为一个探测去造/释放 `FontFace` 包装（避免所有权问题）。
        ///
        /// false ⇒ 这条链拿不到 `Font` ⇒ `CreateFontFace` 返回 null ⇒ 上游守卫抛 FileFormatException。
        /// 探测本身抛异常（NotWired / provider 报错）同样算 false：**不能保证能对上就诚实失败**。
        /// </summary>
        private static bool CanRoundTripFace(Uri filePathUri, LinuxFontFace linuxFace)
        {
            try
            {
                MS.Internal.Text.TextInterface.FontCollection collection =
                    MS.Internal.FontCache.DWriteFactory.GetFontCollectionFromFile(filePathUri);

                if (collection == null || collection.LinuxCollection == null) return false;

                return collection.LinuxCollection.GetFontFromFontFace(linuxFace) != null;
            }
            catch
            {
                return false;
            }
        }

        private static byte[] ReadFontSourceBytes(IFontSource fontSource)
        {
            using UnmanagedMemoryStream stream = fontSource.GetUnmanagedStream();
            if (stream == null)
                throw new FileFormatException(fontSource.Uri, new InvalidOperationException("IFontSource.GetUnmanagedStream() 返回 null"));

            long length = stream.Length;
            if (length <= 0 || length > int.MaxValue)
                throw new FileFormatException(fontSource.Uri, new InvalidOperationException("字体字节流长度非法：" + length));

            var bytes = new byte[length];
            stream.Position = 0;
            int read = 0;
            while (read < bytes.Length)
            {
                int n = stream.Read(bytes, read, bytes.Length - read);
                if (n <= 0) break;
                read += n;
            }

            if (read != bytes.Length)
                throw new FileFormatException(fontSource.Uri, new InvalidOperationException("字体字节流长度非法：" + length));

            return bytes;
        }

        /// <summary>
        /// 系统字体目录：`WPF_LINUX_FONT_DIR`（`:` 分隔）优先；否则 FHS/XDG 标准位置。
        /// 顺序即优先级，且**保留不存在的路径**（诊断里能看出"配了但不存在"）。
        /// </summary>
        private static string[] ResolveSystemFontDirectories()
        {
            string configured = Environment.GetEnvironmentVariable("WPF_LINUX_FONT_DIR");
            if (!string.IsNullOrEmpty(configured))
            {
                var list = new List<string>();
                foreach (string part in configured.Split(new[] { ':', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    list.Add(part.Trim());
                if (list.Count > 0) return list.ToArray();
            }

            var candidates = new List<string>();
            string xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            string home = Environment.GetEnvironmentVariable("HOME");

            if (!string.IsNullOrEmpty(xdgDataHome)) candidates.Add(Path.Combine(xdgDataHome, "fonts"));
            candidates.Add("/usr/share/fonts");
            candidates.Add("/usr/local/share/fonts");
            if (!string.IsNullOrEmpty(home))
            {
                candidates.Add(Path.Combine(home, ".local", "share", "fonts"));
                candidates.Add(Path.Combine(home, ".fonts"));
            }

            return candidates.ToArray();
        }

        private bool IsSystemFontDirectory(string localPath)
        {
            if (string.IsNullOrEmpty(localPath) || _systemFontDirectories == null) return false;

            string normalized = localPath.TrimEnd(Path.DirectorySeparatorChar);
            foreach (string directory in _systemFontDirectories)
            {
                if (string.IsNullOrEmpty(directory)) continue;
                if (string.Equals(directory.TrimEnd(Path.DirectorySeparatorChar), normalized, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
    }
}
