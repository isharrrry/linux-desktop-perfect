// ─────────────────────────────────────────────────────────────────────────────
// **生成物 —— 不要手改**（本仓纪律：生成物由应用器重生成）。
//   应用器：src/WpfGfx.Linux.Native/tools/patch-presentationcore-mimetype.py（PC 补丁 Q）
//   上游：  upstream/wpf/src/Microsoft.DotNet.Wpf/src/Shared/MS/Internal/MimeTypeMapper.cs
//   内容：  上游逐字 + 3 处锚点替换（内置表合并 / 调用点 / UrlMon 方法 → 内置表方法）
//   重生成：python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-mimetype.py
//   自检：  python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-mimetype.py --check
//   原因：  UrlMon 的 FindMimeFromData 要 marshal COM 接口指针，Linux 上封送阶段即抛
//           MarshalDirectiveException ⇒ XAML 里按 pack:// URI 引用图片的 app 一律死在这里。
// ─────────────────────────────────────────────────────────────────────────────
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.


using System.Collections;
using System.Globalization;
using System.IO;
#if PRESENTATION_CORE
using MS.Internal.PresentationCore;  // for BindUriHelper.UriToString
#else
#error Class is being used from an unknown assembly.
#endif

namespace MS.Internal
{
    internal static class MimeTypeMapper
    {

        internal static ContentType GetMimeTypeFromUri(Uri uriSource)
        {
            ContentType mimeType = ContentType.Empty;

            if (uriSource != null)
            {
                Uri uri = uriSource;
                if (!uri.IsAbsoluteUri)
                {
                      uri = new Uri("http://foo/bar/");
                      uri = new Uri(uri, uriSource);
                }
               
                string completeExt = GetFileExtension(uri);

                lock (((ICollection)_fileExtensionToMimeType).SyncRoot)
                {
                    // initialize for the first time
                    if (_fileExtensionToMimeType.Count == 0)
                    {

                        // Adding the known mime types to the hash table.

                        _fileExtensionToMimeType.Add(XamlExtension, XamlMime);
                        _fileExtensionToMimeType.Add(BamlExtension, BamlMime);
                        _fileExtensionToMimeType.Add(JpgExtension, JpgMime);
                        _fileExtensionToMimeType.Add(XbapExtension, XbapMime);

                        // ── Linux 补丁 Q：Windows 上其余扩展名的 MIME 由 UrlMon 从注册表读出；
                        //    Linux 无 urlmon ⇒ 合并内置表（口径与边界见表定义处注释）。
                        foreach (var pair in GetBuiltInExtensionTable())
                        {
                            _fileExtensionToMimeType.Add(pair.Key, pair.Value);
                        }

                    }


                    if (!_fileExtensionToMimeType.TryGetValue(completeExt, out mimeType))
                    {
                        //
                        // Linux 补丁 Q：上游在这里调 UrlMon 问注册表（表里没有的扩展名）。
                        //   Linux 上那条路走不通 —— `FindMimeFromData` 的第 1 参是 COM 接口
                        //   指针，.NET 在**封送阶段**就抛 MarshalDirectiveException（见文件头）。
                        //   改为查内置表；表里没有的扩展名返回 OctetMime（与 Windows 上
                        //   "未知类型"同口径，引证 WpfWebRequestHelper.cs:288-297）。
                        //
                        mimeType = GetMimeTypeFromBuiltInTable(completeExt);

                        if (mimeType != ContentType.Empty)
                        {
                            _fileExtensionToMimeType.Add(completeExt, mimeType);
                        }

                    }
                }
            }

            return mimeType;
        }

        //
        // Linux 补丁 Q：内置扩展名 → MIME 表（替代 UrlMon / 注册表查询）。
        //
        //   上游这里调的是 urlmon.dll 的 FindMimeFromData；它在 Linux 上不可达（COM 指针封送）。
        //   本方法只做纯托管查表：命中即返回，未命中返回 OctetMime。
        //
        private static ContentType GetMimeTypeFromBuiltInTable(string extension)
        {
            ContentType mimeType;

            if (!String.IsNullOrEmpty(extension)
                && GetBuiltInExtensionTable().TryGetValue(extension, out mimeType))
            {
                return mimeType;
            }

            return OctetMime;
        }

        // Linux 补丁 Q：内置表本体。键的口径 = `GetFileExtension` 的返回（**小写、不含点**）。
        //   取值 = Windows 上 UrlMon/注册表对该扩展名的常见返回。**只收图片**（+ 上游的 IconMime）。
        //
        //   ⚠️ **必须惰性构造，不许写成字段初始化器** —— 这条是实测踩出来的：
        //     本类的常量（`IconMime`/`OctetMime`/`XamlMime`…）**声明在文件靠后的类尾**，
        //     而 C# 的静态字段初始化器**按文本顺序**执行 ⇒ 字段级的表在构造时引用到的
        //     `IconMime` 还是 **null**，于是 "ico" 映到 null ⇒
        //     `ResourcePart.GetContentTypeCore()` 里那句 `.ToString()` 抛
        //     **NullReferenceException**。实测触发者：HandyControl demo 主窗口的
        //     `Icon="/HandyControlDemo;component/Resources/Img/icon.ico"`（`.ico` 必踩）。
        //     惰性构造把求值推到**首次调用**，那时全部静态字段已就绪（上游
        //     `_fileExtensionToMimeType` 自己也是这个风格）。
        private static Dictionary<string, ContentType> _builtInExtensionToMimeType;

        private static Dictionary<string, ContentType> GetBuiltInExtensionTable()
        {
            if (_builtInExtensionToMimeType == null)
            {
                var table = new Dictionary<string, ContentType>(System.StringComparer.Ordinal);

                table.Add("png",  new ContentType("image/png"));
                table.Add("gif",  new ContentType("image/gif"));
                table.Add("ico",  IconMime);
                table.Add("cur",  IconMime);
                table.Add("bmp",  new ContentType("image/bmp"));
                table.Add("dib",  new ContentType("image/bmp"));
                table.Add("jpeg", new ContentType("image/jpeg"));
                table.Add("jpe",  new ContentType("image/jpeg"));
                table.Add("jfif", new ContentType("image/jpeg"));
                table.Add("tif",  new ContentType("image/tiff"));
                table.Add("tiff", new ContentType("image/tiff"));
                table.Add("wdp",  new ContentType("image/vnd.ms-photo"));
                table.Add("hdp",  new ContentType("image/vnd.ms-photo"));
                table.Add("webp", new ContentType("image/webp"));

                _builtInExtensionToMimeType = table;
            }

            return _builtInExtensionToMimeType;
        }

        private static string GetDocument(Uri uri)
        {
            string docstring;

            if (uri.IsFile)
            {
                // LocalPath will un-escape characters, convert a file:///
                //  URI back into a local file system path.  It will also
                //  drop any post-pended characters.  (rogerch)
                //
                // "file:///c:/Program%20Files/foo.xmf#bar.jpg"
                //          becomes
                // "C:\Program Files\foo.xmf"
                docstring = uri.LocalPath;
            }
            else
            {
                // When not using the file scheme, escaped characters need
                //  to stay there and we rely on the Uri class to take care
                //  of figuring out what's going on.
                docstring = uri.GetLeftPart(UriPartial.Path);
            }

            return docstring;
        }

        internal static string GetFileExtension(Uri uri)
        {
            string docString = GetDocument(uri);
            string extensionWithDot = Path.GetExtension(docString);
            string extension = String.Empty;

            if (!String.IsNullOrEmpty(extensionWithDot))
            {
                extension = extensionWithDot.Substring(1).ToLower(CultureInfo.InvariantCulture);
            }
            
            return extension;
        }

        internal static bool IsHTMLMime(ContentType contentType)
        {
            return (HtmlMime.AreTypeAndSubTypeEqual(contentType)
                || HtmMime.AreTypeAndSubTypeEqual(contentType));
        }

        // The initial size of the hashtable mapps to the initial Known mimetypes.
        // If more known mimetypes are added later, please change this number also 
        // for better perf.
        private static readonly Dictionary<string, ContentType> _fileExtensionToMimeType = new Dictionary<string, ContentType>(4);

        // Unspported MIME type
        internal static readonly ContentType OctetMime = new ContentType("application/octet-stream");
        internal static readonly ContentType TextPlainMime = new ContentType("text/plain");

        // Known file extensions
        internal const string XamlExtension      = "xaml";
        internal const string BamlExtension      = "baml";
        internal const string XbapExtension      = "xbap";
        internal const string JpgExtension       = "jpg";

        // Supported MIME types:
        internal static readonly ContentType XamlMime = new ContentType("application/xaml+xml");
        internal static readonly ContentType BamlMime = new ContentType("application/baml+xml");
        internal static readonly ContentType JpgMime = new ContentType("image/jpg");
        internal static readonly ContentType IconMime = new ContentType("image/x-icon");

        internal static readonly ContentType FixedDocumentSequenceMime = new ContentType("application/vnd.ms-package.xps-fixeddocumentsequence+xml");
        internal static readonly ContentType FixedDocumentMime = new ContentType("application/vnd.ms-package.xps-fixeddocument+xml");
        internal static readonly ContentType FixedPageMime = new ContentType("application/vnd.ms-package.xps-fixedpage+xml");
        internal static readonly ContentType ResourceDictionaryMime = new ContentType("application/vnd.ms-package.xps-resourcedictionary+xml");

        internal static readonly ContentType HtmlMime = new ContentType("text/html");
        internal static readonly ContentType HtmMime = new ContentType("text/htm");
        internal static readonly ContentType XbapMime = new ContentType("application/x-ms-xbap");
    }
}
