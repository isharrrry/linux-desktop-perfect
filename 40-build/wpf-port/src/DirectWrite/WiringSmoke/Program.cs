// T2 · Phase 2 —— 端到端冒烟：**通过接线后的骨架**（DirectWriteForwarder）取字体数据
// =====================================================================================
// 【验证什么】
//   本工程反射访问 `MS.Internal.Text.TextInterface.{FontCollection, FontFamily, Font, FontFace, FontFile}`
//   —— 也就是接线后的那几个类型 —— 并把取到的值打印成 `KEY=VALUE`。
//   它证明的是"接线真的在工作"，而不是"接线编译过了"：
//     · 度量走的是 OS/2 + hhea + post 的真值（含 T2 修掉的 LineSpacing 整数除法）；
//     · 表字节走的是 OpenType 表直读，与原始 .ttf 文件逐字节比对；
//     · 族/字面/匹配/枚举走的是 provider 的托管列表。
//
// 【为什么用反射而不是编译期引用】
//   这些类型是 `internal`（上游靠 [InternalsVisibleTo("PresentationCore")] 暴露）。
//   反射让本工程不依赖 IVT，也避免把 DWF 的 internal 面固化进测试。
//   代价是代码啰嗦一点 —— 换来的是一份"外部视角"的验证。
//
// 【刻意不做的事】
//   不验指针参数的成员（GetArrayOfGlyphIndices / GetDesignGlyphMetrics 等吃 `ushort*`）：
//   `MethodInfo.Invoke` 无法传指针。它们的正确性由 T2 的 82 条断言在 provider 层覆盖，
//   这里只验"接线把调用转过去了"（TryGetFontTable 就够了 —— 它也是 A 档成员）。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;

namespace MS.Internal.Text.TextInterface.Linux.WiringSmoke
{
    public static class Program
    {
        private const string TextInterface = "MS.Internal.Text.TextInterface";

        public static int Main(string[] args)
        {
            // 引用形态改成 HintPath（照 src/Linux/samples/HelloWpf 的 9 条）之后，WindowsBase 会进 deps.json
            // ⇒ 不再需要预加载 / Resolving 兜底。这里只报身份，出问题时一眼看出命中的是哪一份。
            try
            {
                Console.WriteLine("LOADED WindowsBase=" + typeof(System.Windows.DependencyObject).Assembly.GetName().FullName);
            }
            catch (Exception e)
            {
                Console.WriteLine("LOADED WindowsBase=<" + e.GetType().Name + ">");
            }

            string fontDir = null;
            string testFamily = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--font-dir" && i + 1 < args.Length) fontDir = args[++i];
                else if (args[i] == "--family" && i + 1 < args.Length) testFamily = args[++i];
            }

            fontDir ??= ResolveFontDirectory();

            // provider 侧的类型化集合：只用来取"当前测的是哪个字体文件"（DWF 侧不暴露路径）
            using var linuxCollection = LinuxFontCollection.FromDirectory(fontDir);
            string firstFacePath = linuxCollection.Families.Count > 0 && linuxCollection.Families[0].Faces.Count > 0
                ? linuxCollection.Families[0].Faces[0].FilePath
                : null;

            string boldFontPath = firstFacePath;
            if (linuxCollection.Families.Count > 0)
            {
                var fam0 = linuxCollection.Families[0];
                LinuxFont boldFace = fam0.GetFirstMatchingFont(700, 5, 0, FontMatchingRule.Distance);
                if (boldFace?.FaceEntry?.FilePath != null) boldFontPath = boldFace.FaceEntry.FilePath;
            }

            try
            {
                Assembly dwf = Assembly.Load("DirectWriteForwarder");
                Console.WriteLine("ASSEMBLY=" + dwf.GetName().Name + " " + dwf.GetName().Version);

                // ---------------- FontCollection.FromDirectory ----------------
                Type collectionType = dwf.GetType(TextInterface + ".FontCollection", throwOnError: true);
                object collection = collectionType
                    .GetMethod("FromDirectory", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                    .Invoke(null, new object[] { fontDir });

                Console.WriteLine("COLLECTION_FAMILYCOUNT=" + Get<uint>(collection, "FamilyCount"));

                // ---------------- FontCollection[familyName] ----------------
                PropertyInfo stringIndexer = collectionType.GetProperty(
                    "Item", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    null, null, new[] { typeof(string) }, null);
                // 不再硬编码族名：取集合里的**第一个**族（这样任意字体目录都能测）。
                // DWF 的 FontCollection 有 uint 索引器（上游 FontCollection.h 的 default[UINT32]）。
                object family = collectionType
                    .GetProperty("Item", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                                 null, null, new[] { typeof(uint) }, null)
                    .GetValue(collection, new object[] { 0u });
                Console.WriteLine("FAMILY_NAME=" + Get<string>(family, "OrdinalName"));
                Console.WriteLine("FAMILY_FOUND=" + (family != null));   // 现在表示"集合非空且取到了第一个族"
                Console.WriteLine("FAMILY_MISSING_IS_NULL=" + (stringIndexer.GetValue(collection, new object[] { "Arial" }) == null));

                Type familyType = family.GetType();
                Console.WriteLine("FAMILY_ORDINALNAME=" + Get<string>(family, "OrdinalName"));
                Console.WriteLine("FAMILY_FACECOUNT=" + Get<uint>(family, "Count"));
                Console.WriteLine("FAMILY_ISPHYSICAL=" + Get<bool>(family, "IsPhysical"));
                Console.WriteLine("FAMILY_ISCOMPOSITE=" + Get<bool>(family, "IsComposite"));

                // ---------------- FontFamily.Metrics（含 LineSpacing 的整数除法修正）----------------
                object metrics = GetProp(family, "Metrics");
                Console.WriteLine("FAMILY_METRICS=" + DescribeMetrics(metrics));

                object displayMetrics = familyType
                    .GetMethod("DisplayMetrics", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .Invoke(family, new object[] { 12f, 1.25f });
                Console.WriteLine("FAMILY_DISPLAYMETRICS=" + DescribeMetrics(displayMetrics));

                // ---------------- FontFamily.GetFirstMatchingFont(Bold, Normal, Normal) ----------------
                Type weightType = dwf.GetType(TextInterface + ".FontWeight", throwOnError: true);
                Type stretchType = dwf.GetType(TextInterface + ".FontStretch", throwOnError: true);
                Type styleType = dwf.GetType(TextInterface + ".FontStyle", throwOnError: true);

                object boldWeight = Enum.Parse(weightType, "Bold");
                object normalStretch = Enum.Parse(stretchType, "Normal");
                object normalStyle = Enum.Parse(styleType, "Normal");

                object boldFont = familyType
                    .GetMethod("GetFirstMatchingFont", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .Invoke(family, new[] { boldWeight, normalStretch, normalStyle });

                Type fontWeightType = boldFont.GetType();
                Console.WriteLine("BOLD_WEIGHT=" + Get<object>(boldFont, "Weight"));
                Console.WriteLine("BOLD_STYLE=" + Get<object>(boldFont, "Style"));
                Console.WriteLine("BOLD_STRETCH=" + Get<object>(boldFont, "Stretch"));
                Console.WriteLine("BOLD_ISSYMBOL=" + Get<bool>(boldFont, "IsSymbolFont"));
                Console.WriteLine("BOLD_SIMULATIONS=" + Get<object>(boldFont, "SimulationFlags"));
                Console.WriteLine("BOLD_VERSION=" + Num(Get<double>(boldFont, "Version")));
                Console.WriteLine("BOLD_HASCHAR_A=" + GetMethod(boldFont, "HasCharacter").Invoke(boldFont, new object[] { (uint)'A' }));
                Console.WriteLine("BOLD_HASCHAR_CJK=" + GetMethod(boldFont, "HasCharacter").Invoke(boldFont, new object[] { 0x4E2Du }));
                Console.WriteLine("BOLD_METRICS=" + DescribeMetrics(GetProp(boldFont, "Metrics")));

                // ---------------- Font.GetFontFace() → FontFace 的 A 档成员 ----------------
                object face = GetMethod(boldFont, "GetFontFace").Invoke(boldFont, Array.Empty<object>());
                Type faceType = face.GetType();

                Console.WriteLine("FACE_TYPE=" + Get<object>(face, "Type"));
                Console.WriteLine("FACE_INDEX=" + Get<object>(face, "Index"));
                Console.WriteLine("FACE_GLYPHCOUNT=" + Get<object>(face, "GlyphCount"));
                Console.WriteLine("FACE_ISSYMBOL=" + Get<bool>(face, "IsSymbolFont"));
                Console.WriteLine("FACE_METRICS=" + DescribeMetrics(GetProp(face, "Metrics")));

                var fsTypeArgs = new object[] { null };
                bool rightsOk = (bool)GetMethod(face, "ReadFontEmbeddingRights").Invoke(face, fsTypeArgs);
                Console.WriteLine("FACE_READEMBEDDING=" + rightsOk + " FSTYPE=0x" + ((ushort)fsTypeArgs[0]).ToString("X4"));

                // TryGetFontTable：与原始 .ttf 字节逐字节比对（跨程序集的真值验证）
                object headTag = Enum.Parse(dwf.GetType(TextInterface + ".OpenTypeTableTag", true), "FontHeader");
                var tableArgs = new object[] { headTag, null };
                bool hasTable = (bool)GetMethod(face, "TryGetFontTable").Invoke(face, tableArgs);
                byte[] headFromSkeleton = tableArgs[1] as byte[];
                byte[] headFromFile = SliceHeadTable(boldFontPath);

                Console.WriteLine("FACE_HEADTABLE_OK=" + hasTable);
                Console.WriteLine("FACE_HEADTABLE_LEN=" + (headFromSkeleton?.Length ?? -1));
                Console.WriteLine("FACE_HEADTABLE_SHA_MATCH=" +
                    (headFromSkeleton != null && headFromFile != null && Sha(headFromSkeleton) == Sha(headFromFile)));

                // ---------------- FontFile（GetFileZero → Analyze / GetUriPath）----------------
                object file = GetMethod(face, "GetFileZero").Invoke(face, Array.Empty<object>());
                Type fileType = file.GetType();
                Console.WriteLine("FILE_URIPATH_BASENAME=" + Path.GetFileName(GetMethod(file, "GetUriPath").Invoke(file, Array.Empty<object>()) as string));

                // ---------------- Font.GetFaceNames / GetInformationalStrings（C 档，顺手验）----------------
                object faceNames = GetProp(boldFont, "FaceNames");
                Console.WriteLine("BOLD_FACENAMES_COUNT=" + Get<int>(faceNames, "Count"));

                object versionId = Enum.Parse(dwf.GetType(TextInterface + ".InformationalStringID", true), "VersionStrings");
                var infoArgs = new object[] { versionId, null };
                bool hasInfo = (bool)GetMethod(boldFont, "GetInformationalStrings").Invoke(boldFont, infoArgs);
                Console.WriteLine("BOLD_VERSIONSTRINGS_OK=" + hasInfo);

                // ---------------- 令牌（DWriteFontFaceAddRef / DWriteFontAddRef）----------------
                IntPtr faceToken = (IntPtr)GetProp(face, "DWriteFontFaceAddRef");
                Console.WriteLine("FACE_TOKEN_NONZERO=" + (faceToken != IntPtr.Zero));
                Console.WriteLine("FACE_TOKEN_RESOLVES=" + FontHandleTable.TryResolve(faceToken, out _));

                IntPtr fontToken = (IntPtr)GetProp(boldFont, "DWriteFontAddRef");
                Console.WriteLine("FONT_TOKEN_NONZERO=" + (fontToken != IntPtr.Zero));
                Console.WriteLine("FONT_TOKEN_RESOLVES=" + FontHandleTable.TryResolve(fontToken, out _));

                // ---------------- 释放（上游语义：Release 只递减引用）----------------
                GetMethod(face, "Release").Invoke(face, Array.Empty<object>());

                // ==================================================================
                //  补丁 G 验收入口：PC 的 Linux 版 Factory（原生闸门已移除）
                // ==================================================================
                Assembly pc = Assembly.Load("PresentationCore");
                Type factoryType = pc.GetType(TextInterface + ".Factory", throwOnError: true);

                // Factory.Create(FactoryType.Shared, IFontSourceCollectionFactory, IFontSourceFactory)
                // 本地字体路径不需要这两个工厂 → 传 null。
                object factoryTypeEnum = Enum.Parse(dwf.GetType(TextInterface + ".FactoryType", true), "Shared");
                object factory = factoryType
                    .GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                    .Invoke(null, new object[] { factoryTypeEnum, null, null });
                Console.WriteLine("FACTORY_CREATED=" + (factory != null));

                // 原生工厂指针：Linux 上如实为 null（唯一消费方是 D 档的 Itemize）
                // 指针类型经反射返回 System.Reflection.Pointer 包装（或 null）。
                // 这里只断言"是不是 null" —— 我们的 Linux Factory 恒返回 null
                //（无需 unsafe/Unbox，也就避免为了取证引入不安全代码）。
                object nativeFactory = GetProp(factory, "DWriteFactory");
                bool nativeIsNull;
                unsafe
                {
                    // 指针返回值经反射会包成 System.Reflection.Pointer（即使是空指针）。
                    nativeIsNull = nativeFactory == null
                        || System.Reflection.Pointer.Unbox(nativeFactory) == IntPtr.Zero.ToPointer();
                }

                Console.WriteLine("FACTORY_NATIVE_PTR_IS_NULL=" + nativeIsNull);

                // GetSystemFontCollection → 我们的托管集合（字体目录由 WPF_LINUX_FONT_DIR 指定）
                object systemCollection = GetMethod(factory, "GetSystemFontCollection").Invoke(factory, Array.Empty<object>());
                Console.WriteLine("FACTORY_SYSTEM_FAMILYCOUNT=" + Get<uint>(systemCollection, "FamilyCount"));
                Console.WriteLine("FACTORY_SYSTEM_DIRS=" + Get<string>(factory, "SystemFontDirectoryDiagnostics"));

                // CreateFontFace(Uri, uint, FontSimulations) —— 走真文件
                string regularPath = firstFacePath;
                object simsNone = Enum.Parse(dwf.GetType(TextInterface + ".FontSimulations", true), "None");
                object createdFace = factoryType
                    .GetMethod("CreateFontFace", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                               null, new[] { typeof(Uri), typeof(uint), dwf.GetType(TextInterface + ".FontSimulations", true) }, null)
                    .Invoke(factory, new object[] { new Uri(regularPath), 0u, simsNone });
                Console.WriteLine("FACTORY_CREATED_FACE_GLYPHS=" + Get<object>(createdFace, "GlyphCount"));
                Console.WriteLine("FACTORY_CREATED_FACE_METRICS=" + DescribeMetrics(GetProp(createdFace, "Metrics")));
                GetMethod(createdFace, "Release").Invoke(createdFace, Array.Empty<object>());

                // CreateFontFile(Uri) → Analyze / GetUriPath
                object createdFile = GetMethod(factory, "CreateFontFile").Invoke(factory, new object[] { new Uri(regularPath) });
                var createdAnalyzeArgs = new object[] { null, null, null, null };
                bool createdAnalyzeOk = (bool)GetMethod(createdFile, "Analyze").Invoke(createdFile, createdAnalyzeArgs);
                Console.WriteLine("FACTORY_CREATED_FILE_ANALYZE=" + createdAnalyzeOk
                    + " FACES=" + createdAnalyzeArgs[2]
                    + " FILEKIND=" + createdAnalyzeArgs[0]);
                Console.WriteLine("FACTORY_CREATED_FILE_BASENAME=" +
                    Path.GetFileName(GetMethod(createdFile, "GetUriPath").Invoke(createdFile, Array.Empty<object>()) as string));
                Console.WriteLine("FONT_FILE_UNDER_TEST=" + Path.GetFileName(boldFontPath));

                // CreateTextAnalyzer：非 null，但方法属 D 档 → 调一次必须抛**带说明的** PNSE
                object analyzer = GetMethod(factory, "CreateTextAnalyzer").Invoke(factory, Array.Empty<object>());
                Console.WriteLine("FACTORY_ANALYZER_NONNULL=" + (analyzer != null));

                // D 档成员的行为：必须抛**带说明的** PlatformNotSupportedException
                // （不是崩溃、不是静默返回空）。
                //
                // 为什么不用 TextAnalyzer.GetGlyphs 来演示：它吃 `ushort*`，
                // 而 `MethodInfo.Invoke` **无法传指针**（T2 报告 §9.3 里登记过这条限制）。
                // 所以这里挑一个同样属 D 档、但签名无指针的成员来证明"降级是响亮的"：
                // FontFileStream.GetFileSize(out ulong)。
                Type streamType = dwf.GetType(TextInterface + ".FontFileStream", throwOnError: true);
                object stream = Activator.CreateInstance(streamType,
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    null, new object[] { null }, null);
                string dTierFailure = "<未抛>";
                string dTierMessage = "";
                try
                {
                    GetMethod(stream, "GetFileSize").Invoke(stream, new object[] { null });
                }
                catch (TargetInvocationException tie)
                {
                    dTierFailure = tie.InnerException?.GetType().Name ?? "Unknown";
                    dTierMessage = tie.InnerException?.Message ?? "";
                }

                Console.WriteLine("DTIER_FONTFILESTREAM_GETFILESIZE_THROWS=" + dTierFailure);
                Console.WriteLine("DTIER_MESSAGE_HAS_GUIDANCE=" +
                    (dTierMessage.Contains("PNSE-INVENTORY") || dTierMessage.Contains("D 档") ||
                     dTierMessage.Contains("Provider")));
                Console.WriteLine("DTIER_ANALYZER_NONNULL=" + (analyzer != null));

                // ==================================================================
                //  令牌桥：钩子是否装上、是否被调用（补丁 G 的伴生件）
                // ==================================================================
                Type bridgeType = pc.GetType("WpfLinux.Shims.PresentationCore.FontFaceBridge", throwOnError: false);
                Console.WriteLine("BRIDGE_TYPE_FOUND=" + (bridgeType != null));
                Console.WriteLine("BRIDGE_STATUS=" + GetStatic<object>(bridgeType, "Status"));
                Console.WriteLine("BRIDGE_DIAGNOSTICS=" + GetStatic<string>(bridgeType, "Diagnostics"));
                Console.WriteLine("BRIDGE_ALLOCATOR_CALLS_BEFORE=" + GetStatic<int>(bridgeType, "AllocatorCalls"));

                // 触发一次真实分配：Font.DWriteFontAddRef → LinuxFontFace.Token → FontHandleTable
                IntPtr fontToken2 = (IntPtr)GetProp(boldFont, "DWriteFontAddRef");
                Console.WriteLine("BRIDGE_ALLOCATOR_CALLS_AFTER=" + GetStatic<int>(bridgeType, "AllocatorCalls"));
                Console.WriteLine("BRIDGE_TOKEN_NONZERO=" + (fontToken2 != IntPtr.Zero));
                Console.WriteLine("BRIDGE_TOKEN_RESOLVES=" + FontHandleTable.TryResolve(fontToken2, out _));
                Console.WriteLine("BRIDGE_STATUS_AFTER=" + GetStatic<object>(bridgeType, "Status"));
                Console.WriteLine("BRIDGE_NATIVE_ALLOCATIONS=" + GetStatic<int>(bridgeType, "NativeAllocations"));

                // ==================================================================
                //  闸门 2：TypographyAvailabilities（PC 自己算，我们读它的结果）
                // ==================================================================
                // FontFaceLayoutInfo 由 Font 构造（GlyphTypeface.cs:98 就是这么做的），
                // 它的 ComputeTypographyAvailabilities 会去读 GSUB/GPOS ——
                // 而"读表"这一步正是走我们 provider 的 FontFace.TryGetFontTable。
                // 所以这里拿到的是**PC 的真实计算结果**，不是我们重算的近似值。
                // 先核对"给 PC 的那三张布局表"到底是什么（长度 + 前 4 字节），
                // 因为 PC 的 ComputeTypographyAvailabilities 只会读这三张表。
                Type tagEnumType = dwf.GetType(TextInterface + ".OpenTypeTableTag", throwOnError: true);
                foreach (string tagName in new[] { "TTO_GSUB", "TTO_GPOS", "TTO_GDEF" })
                {
                    object tagValue = Enum.Parse(tagEnumType, tagName);
                    var parms = new object[] { tagValue, null };
                    bool ok = (bool)GetMethod(face, "TryGetFontTable").Invoke(face, parms);
                    byte[] bytes = parms[1] as byte[];
                    string head = bytes == null || bytes.Length < 4 ? "-" : "0x" + ((uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3])).ToString("X8", CultureInfo.InvariantCulture);
                    Console.WriteLine($"HANDOFF {tagName}=0x{Convert.ToUInt32(tagValue, CultureInfo.InvariantCulture):X8} ok={ok} len={(bytes?.Length ?? -1)} first4={head}");
                }

                Type layoutInfoType = pc.GetType("MS.Internal.FontCache.FontFaceLayoutInfo", throwOnError: true);
                string layoutFailure = null;
                try
                {
                    // ⚠ 这条路径可能因为**环境**（WindowsBase 装配失败 → FileLoadException）
                    //   或**字体**（PC 解析越界 → FileFormatException，而它的类型住在 WindowsBase 里）
                    //   而失败。失败要如实报成 UNAVAILABLE + 原因，不能让整个取证程序挂掉 ——
                    //   前面那 30 项事实（度量/表/令牌/Factory）与它无关。
                    object layoutInfo = Activator.CreateInstance(layoutInfoType,
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                        null, new object[] { boldFont }, null);

                    object typography = GetProp(layoutInfo, "TypographyAvailabilities");
                    long mask = Convert.ToInt64(typography, CultureInfo.InvariantCulture);
                    Console.WriteLine("TYPOGRAPHY_MASK=" + mask);
                    Console.WriteLine("TYPOGRAPHY_MASK_HEX=0x" + mask.ToString("X", CultureInfo.InvariantCulture));
                    Console.WriteLine("TYPOGRAPHY_BITS=" + DescribeTypographyBits(mask));
                }
                catch (Exception e)
                {
                    Exception inner = e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
                    layoutFailure = inner.GetType().Name + ": " + inner.Message;
                    Console.WriteLine("TYPOGRAPHY_MASK=UNAVAILABLE " + Flatten(layoutFailure));
                }

                // 字体里命中 WPF RequiredTypographyFeatures / locl 的**原始引用**（我们的读数）
                // 独立读**磁盘上的字体文件字节**（不经过任何反射对象）—— 证据更硬
                var rawFont = OpenTypeFontData.FromSfnt(File.ReadAllBytes(boldFontPath));
                var suspects = LayoutFeatureReader.FastTextSuspects(rawFont);
                Console.WriteLine("LAYOUT_SCRIPTS=" + string.Join(",", LayoutFeatureReader.ReadAll(rawFont).Select(r => r.ScriptTag).Distinct().OrderBy(x => x, StringComparer.Ordinal)));
                Console.WriteLine("LAYOUT_HANI_REFS=" + LayoutFeatureReader.ReadAll(rawFont).Count(r => r.ScriptTag == "hani"));
                Console.WriteLine("LAYOUT_SUSPECT_TAGS=" + string.Join(",", suspects.Select(r => r.FeatureTag).Distinct().OrderBy(x => x, StringComparer.Ordinal)));
                Console.WriteLine("LAYOUT_SUSPECT_COUNT=" + suspects.Count);
                Console.WriteLine("LAYOUT_SUSPECT_SAMPLE=" + string.Join(" | ", suspects.Take(6).Select(r => r.ToString())));

                // 闸门 1 的输入：PC 的字符分类给 "Hello WPF" 算出什么样的 charFastTextCheck
                // （同样可能因环境失败 → 如实报 UNAVAILABLE，不连坐前面的取证）
                try
                {
                    Type classificationType = pc.GetType("MS.Internal.Classification", throwOnError: true)
                        ?? dwf.GetType("MS.Internal.Classification", throwOnError: true);
                    object charAttr = classificationType
                        .GetMethod("CharAttributeOf", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                        .Invoke(null, new object[] { classificationType
                            .GetMethod("GetUnicodeClassUTF16", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                            .Invoke(null, new object[] { 'H' }) });
                    byte flags = (byte)charAttr.GetType().GetField("Flags", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(charAttr);
                    Console.WriteLine("CHAR_FASTTEXT_FLAGS_H=0x" + flags.ToString("X2", CultureInfo.InvariantCulture));
                    Console.WriteLine("CHAR_FASTTEXT_BIT_SET=" + ((flags & 0x20) != 0 ? "True" : "False"));
                }
                catch (Exception e)
                {
                    Exception inner = e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
                    Console.WriteLine("CHAR_FASTTEXT_BIT_SET=UNAVAILABLE " + Flatten(inner.GetType().Name + ": " + inner.Message));
                }

                // ==================================================================
                //  T1/M7c5 路线 C：运行期 GSUB/GPOS 剥离的**真实 PC 路径**取证
                // ==================================================================
                //  provider 的行为由环境变量 WPF_LINUX_STRIP_LAYOUT 决定；
                //  本段只**报告**它跑了哪种模式，以及在那种模式下 PC 算出来的东西。
                Console.WriteLine("STRIP_LAYOUT_ENV=" + (Environment.GetEnvironmentVariable("WPF_LINUX_STRIP_LAYOUT") ?? "<unset>"));
                Console.WriteLine("STRIP_LAYOUT_DEFAULT_ENABLED=" + FontLayoutStripping.DefaultEnabled);
                Console.WriteLine("STRIP_LAYOUT_LAST_REASON=" + FontLayoutStripping.LastReason);
                Console.WriteLine("STRIP_LAYOUT_STRIPPED=" + FontLayoutStripping.StrippedCount);
                Console.WriteLine("STRIP_LAYOUT_NO_LAYOUT_TABLES=" + FontLayoutStripping.NoLayoutTablesCount);
                Console.WriteLine("STRIP_LAYOUT_BYPASSED=" + FontLayoutStripping.BypassedCount);
                Console.WriteLine("STRIP_LAYOUT_FAILED=" + FontLayoutStripping.FailedCount);

                // 闸门 2 的**判定结果**（不是掩码，是"快路径到底放不放行"）：
                // 调 PC 自己的 Typeface.CheckFastPathNominalGlyphs —— 与 SimpleTextLine.cs:1665 同一入口。
                try
                {
                    // 家族名可由 --family 指定；默认用 provider 集合里的第一个族
                    // （这样同一个探针能测 src/Linux/build/fonts 与 /usr/share/fonts/.../dejavu 两种语料）。
                    string probeFamily = testFamily;
                    if (string.IsNullOrEmpty(probeFamily) && linuxCollection.Families.Count > 0)
                        probeFamily = linuxCollection.Families[0].FamilyName;
                    Console.WriteLine("FASTPATH_FAMILY=" + probeFamily);
                    Console.WriteLine("FASTPATH_CHECK=" + CheckFastPathThroughPc(pc, dwf, probeFamily, boldFont != null));
                }
                catch (Exception e)
                {
                    Exception inner = e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
                    Console.WriteLine("FASTPATH_CHECK=UNAVAILABLE " + Flatten(inner.GetType().Name + ": " + inner.Message));
                }

                // ==================================================================
                //  WIC 侦察（T2 · 2026-09-10）：今天在真实 PC 代码路径上"用到会怎样"
                // ==================================================================
                try
                {
                    Type wicFactoryType = pc.GetType("MS.Win32.PresentationCore.UnsafeNativeMethods+WICImagingFactory", throwOnError: false)
                        ?? pc.GetType("System.Windows.Media.UnsafeNativeMethodsMilCoreApi+WICImagingFactory", throwOnError: false);
                    Console.WriteLine("WIC_PROXY_TYPE_FOUND=" + (wicFactoryType != null));
                    if (wicFactoryType != null)
                    {
                        MethodInfo createFromFilename = wicFactoryType.GetMethod("CreateDecoderFromFilename",
                            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                        Console.WriteLine("WIC_CREATE_FROM_FILENAME_FOUND=" + (createFromFilename != null));

                        var wicArgs = new object[] { IntPtr.Zero, "probe.png", IntPtr.Zero, 0u, 3u, IntPtr.Zero };
                        try
                        {
                            object hr = createFromFilename.Invoke(null, wicArgs);
                            Console.WriteLine("WIC_CALL_RESULT=HRESULT 0x" + Convert.ToInt32(hr, CultureInfo.InvariantCulture).ToString("X8", CultureInfo.InvariantCulture));
                        }
                        catch (TargetInvocationException tie)
                        {
                            Exception inner = tie.InnerException ?? tie;
                            Console.WriteLine("WIC_CALL_THROWS=" + inner.GetType().Name + ": " + Flatten(inner.Message));
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine("WIC_CALL_THROWS=" + e.GetType().Name + ": " + Flatten(e.Message));
                }

                Console.WriteLine("SMOKE_OK=True");
                return 0;
            }
            catch (TargetInvocationException ex)
            {
                Console.Error.WriteLine("SMOKE_FAILED: " + (ex.InnerException ?? ex));
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("SMOKE_FAILED: " + ex);
                return 1;
            }
        }

        // ---------------------------------------------------------------------------------

        private static string DescribeMetrics(object metrics)
        {
            if (metrics == null) return "<null>";
            Type t = metrics.GetType();
            object Field(string name) => t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(metrics);
            object Prop(string name) => t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(metrics);

            return $"upem={Field("DesignUnitsPerEm")} asc={Field("Ascent")} desc={Field("Descent")} gap={Field("LineGap")} "
                 + $"cap={Field("CapHeight")} xh={Field("XHeight")} ul=({Field("UnderlinePosition")},{Field("UnderlineThickness")}) "
                 + $"st=({Field("StrikethroughPosition")},{Field("StrikethroughThickness")}) "
                 + $"baseline={Num((double)Prop("Baseline"))} linespacing={Num((double)Prop("LineSpacing"))}";
        }

        private static T Get<T>(object target, string member)
        {
            object value = GetProp(target, member);
            return value == null ? default : (T)value;
        }

        /// <summary>把 TypographyAvailabilities 的位展开成可读文本（位值取自 FontFaceLayoutInfo.cs:1017-1055）。</summary>
        private static string DescribeTypographyBits(long mask)
        {
            var parts = new List<string>();
            if ((mask & 1) != 0) parts.Add("Available(1)");
            if ((mask & 2) != 0) parts.Add("IdeoTypographyAvailable(2)");
            if ((mask & 4) != 0) parts.Add("FastTextTypographyAvailable(4)");
            if ((mask & 8) != 0) parts.Add("FastTextMajorLanguageLocalizedFormAvailable(8)");
            if ((mask & 16) != 0) parts.Add("FastTextExtraLanguageLocalizedFormAvailable(16)");
            return parts.Count == 0 ? "None(0)" : string.Join("|", parts);
        }

        /// <summary>
        /// 走 **PC 自己的** `Typeface.CheckFastPathNominalGlyphs`（Typeface.cs:355）。
        /// 这是闸门 2 的**最终判定**：掩码只是它的输入之一。
        /// 返回 "True"/"False"，失败时抛（由调用方转成 UNAVAILABLE + 原因）。
        /// </summary>
        private static string CheckFastPathThroughPc(Assembly pc, Assembly dwf, string familyName, bool haveFont)
        {
            // FontFamily(string) → Typeface(family, style, weight, stretch)
            Type familyType = pc.GetType("System.Windows.Media.FontFamily", throwOnError: true);
            object family = Activator.CreateInstance(familyType, new object[] { familyName });

            Type styleType = pc.GetType("System.Windows.FontStyle", throwOnError: true);
            Type weightType = pc.GetType("System.Windows.FontWeight", throwOnError: true);
            Type stretchType = pc.GetType("System.Windows.FontStretch", throwOnError: true);
            // ⚠️ 这些常量住在**复数**静态类上（FontStyles/FontWeights/FontStretches），
            //    不是 FontStyle/FontWeight/FontStretch 自身（后者没有 Normal 成员 —— 实测）。
            object style = GetStatic<object>(pc.GetType("System.Windows.FontStyles", throwOnError: true), "Normal");
            object weight = GetStatic<object>(pc.GetType("System.Windows.FontWeights", throwOnError: true), "Bold");
            object stretch = GetStatic<object>(pc.GetType("System.Windows.FontStretches", throwOnError: true), "Normal");

            Type typefaceType = pc.GetType("System.Windows.Media.Typeface", throwOnError: true);
            // 用 GetConstructor 显式取 4 参构造（Activator 的 binder 形态在上面已经踩过一次坑）
            ConstructorInfo ctor = typefaceType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { familyType, styleType, weightType, stretchType }, null);
            if (ctor == null) return "UNAVAILABLE typeface-ctor-not-found";
            object typeface = ctor.Invoke(new[] { family, style, weight, stretch });

            // 先确认这个 Typeface 真的解析到了字体面（否则 False 是"没字体"，不是"闸门拒绝"）
            // 有两个同名重载（public bool TryGetGlyphTypeface(out GlyphTypeface) 与
            // internal GlyphTypeface TryGetGlyphTypeface()）⇒ GetMethod 会 Ambiguous。
            // 这里只要"解析到没有"，用无参那个（internal）更直接。
            MethodInfo tryGet = typefaceType.GetMethod("TryGetGlyphTypeface",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            if (tryGet != null)
            {
                object gt = tryGet.Invoke(typeface, null);
                if (gt == null) return "False(no-glyph-typeface:" + familyName + ")";
            }
            else
            {
                return "UNAVAILABLE trygetglyphtypeface-not-found";
            }

            // CharacterBufferRange(string, int, int)
            const string probeText = "Hello WPF on Linux";
            Type rangeType = pc.GetType("System.Windows.Media.TextFormatting.CharacterBufferRange", throwOnError: true);
            object range = Activator.CreateInstance(rangeType, new object[] { probeText, 0, probeText.Length });

            Type modeType = pc.GetType("System.Windows.Media.TextFormattingMode", throwOnError: true);
            object idealMode = Enum.Parse(modeType, "Ideal");

            MethodInfo check = typefaceType.GetMethod("CheckFastPathNominalGlyphs",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (check == null) return "UNAVAILABLE method-not-found";

            var args = new object[]
            {
                range,              // CharacterBufferRange
                12.0,               // emSize
                1.0f,               // pixelsPerDip（Ideal 模式下不参与取整）
                1.0,                // scalingFactor
                10000.0,            // widthMax（足够宽，不因宽度截断）
                false,              // keepAWord
                false,              // numberSubstitution
                CultureInfo.InvariantCulture,   // cultureInfo
                idealMode,          // TextFormattingMode
                false,              // isSideways
                false,              // breakOnTabs
                0,                  // out stringLengthFit
            };
            object result = check.Invoke(typeface, args);
            return ((bool)result ? "True" : "False") + " stringLengthFit=" + args[11];
        }

        private static string Flatten(string value) =>
            string.IsNullOrEmpty(value) ? "" : value.Replace('\n', ' ').Replace('\r', ' ').Trim();

        private static T GetStatic<T>(Type type, string member)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            PropertyInfo property = type.GetProperty(member, flags);
            if (property != null)
            {
                object value = property.GetValue(null);
                return value == null ? default : (T)value;
            }

            // WPF 的 FontStyle.Normal / FontWeights.Bold / FontStretches.Normal 是
            // **static readonly 字段**，不是属性 —— 只查属性会 MissingMemberException（实测）。
            FieldInfo field = type.GetField(member, flags);
            if (field != null)
            {
                object value = field.GetValue(null);
                return value == null ? default : (T)value;
            }

            throw new MissingMemberException(type.FullName, member);
        }

        private static object GetProp(object target, string member)
        {
            PropertyInfo property = target.GetType().GetProperty(
                member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null) throw new MissingMemberException(target.GetType().FullName, member);
            return property.GetValue(target);
        }

        private static MethodInfo GetMethod(object target, string name) =>
            target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(target.GetType().FullName, name);

        /// <summary>测试侧独立切出 head 表（不经过 provider / 骨架的任何代码）。</summary>
        private static byte[] SliceHeadTable(string path)
        {
            byte[] sfnt = File.ReadAllBytes(path);
            int numTables = (sfnt[4] << 8) | sfnt[5];
            for (int i = 0; i < numTables; i++)
            {
                int rec = 12 + i * 16;
                uint tag = ((uint)sfnt[rec] << 24) | ((uint)sfnt[rec + 1] << 16) | ((uint)sfnt[rec + 2] << 8) | sfnt[rec + 3];
                if (tag != 0x68656164u) continue;   // 'head'

                int offset = (sfnt[rec + 8] << 24) | (sfnt[rec + 9] << 16) | (sfnt[rec + 10] << 8) | sfnt[rec + 11];
                int length = (sfnt[rec + 12] << 24) | (sfnt[rec + 13] << 16) | (sfnt[rec + 14] << 8) | sfnt[rec + 15];

                var bytes = new byte[length];
                Array.Copy(sfnt, offset, bytes, 0, length);
                return bytes;
            }

            return null;
        }

        private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        private static string Num(double value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        private static string ResolveFontDirectory()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "src", "Linux", "build", "fonts");
                if (File.Exists(Path.Combine(candidate, "SHA256SUMS"))) return candidate;
                if (dir.Name == "fonts" && File.Exists(Path.Combine(dir.FullName, "SHA256SUMS"))) return dir.FullName;
                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException("找不到 src/Linux/build/fonts/SHA256SUMS");
        }
    }
}
