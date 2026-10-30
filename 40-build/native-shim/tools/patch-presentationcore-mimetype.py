#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""PC 补丁 Q：MimeType 内置表（MimeTypeMapper）—— 把 UrlMon/注册表查询换成内置表。

【为什么必须有这个补丁】
上游 `Shared/MS/Internal/MimeTypeMapper.cs` 的 `GetMimeTypeFromUri` 对**表外**扩展名调
`MS.Win32.Compile.UnsafeNativeMethods.FindMimeFromData`（urlmon.dll）。该 P/Invoke 的第 1 参
是 COM 接口指针 `IBindCtx` ⇒ .NET 在 Linux 上**在封送阶段**就抛

    MarshalDirectiveException: Cannot marshal 'parameter #1':
    Invalid managed/unmanaged type combination (Marshaling to and from COM interface pointers isn't supported)

——连 DLL 都不会去找 ⇒ 放任何 `urlmon.dll.so` 之类的原生替身都**修不了**。
触发性极普通：`_fileExtensionToMimeType` 初始化只有 `xaml/baml/jpg/xbap` 四项，所以
**任何在 XAML 里按 `pack://` URI 引用图片（png/gif/ico/…）的真实 app 都会死在这里**。
实例（第一次实测到它的东西）：HandyControl 示例工程主窗口 BAML ——
`Window..ctor` → … → `BitmapFrame.CreateFromUriOrStream` → `ResourcePart.GetContentTypeCore`
→ `MimeTypeMapper.GetMimeTypeFromUri` → `GetMimeTypeFromUrlMon` ⇒ 上述异常（rc=134）。
我们自己的语料没覆盖过它，因为样本应用走的是 `BitmapImage` + **文件路径**（WIC 解码器路径），
从来没有"XAML 里按 URI 引用图片"的形状。

同源先例：**补丁 H**（`src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/SecurityHelper.Linux.cs`）——同一个模式
（上游要 marshal urlmon 的 COM 指针 ⇒ Linux 上换成短路实现）。

【生成物】src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/MimeTypeMapper.Linux.cs
  = 上游逐字 + 3 处锚点替换 + 文件头横幅：
    ① `GetMimeTypeFromUri` 的初始化块：合并内置表（图片格式）；
    ② 调用点 `GetMimeTypeFromUrlMon(uriSource)` → `GetMimeTypeFromBuiltInTable(completeExt)`；
    ③ `UrlMon` 方法整块 → 内置表方法 + 表定义。
  并断言：生成物里**不再出现** `FindMimeFromData` / `UnsafeNativeMethods` / `GetMimeTypeFromUrlMon`，
  `throw` 条数与上游**相等**（这是短路，不是把异常换地方冒）。

【表的口径（明说的边界）】只收①上游四项 ②图片格式。其余扩展名一律 `OctetMime`
（`application/octet-stream`），与 Windows 上"未知类型"同口径 —— 引证
`WpfWebRequestHelper.cs:288-297` 正是拿 `OctetMime`/`TextPlainMime` 当"服务器没配好、
按扩展名再嗅探"的判据。**不在这里猜媒体/字体类型的注册表值**：那是未测量的断言；
将来若某条臂证明需要，再加表项并补读数。

【接线】在 `PresentationCore.Linux.csproj` 的 `Sdk.targets` 锚点行之前插一整块
（`Compile Remove` 上游 + `Compile Include` 生成物）。该 `Remove` 落在上游那条 `Include`
之后 ⇒ 按 MSBuild 文档顺序求值生效。本补丁必须跑在 `port-lib.py PresentationCore`
（它会整份重写 csproj）**之后** —— `integration-wave.sh` 的主循环已经保证这个顺序。

【用法】
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-mimetype.py          # 应用（默认动作）
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-mimetype.py --check   # 0=生成物同步且已接线

【复现（接线求值，不读 XML）】
    dotnet msbuild src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/PresentationCore.Linux.csproj -m:1 --nologo \\
        -getItem:Compile -p:Configuration=Release | grep -i MimeTypeMapper
    # 期望只剩 MimeTypeMapper.Linux.cs（上游那条被 Remove 掉）
"""

import argparse
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))

UPSTREAM_REL = "src/Microsoft.DotNet.Wpf/src/Shared/MS/Internal/MimeTypeMapper.cs"
UPSTREAM = os.path.join(ROOT, UPSTREAM_REL)
PC_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "PresentationCore")
GENERATED = os.path.join(PC_DIR, "MimeTypeMapper.Linux.cs")
CSPROJ = os.path.join(PC_DIR, "PresentationCore.Linux.csproj")

MARKER_BEGIN = "  <!-- ==== T2 · 补丁 Q：MimeType 内置表（MimeTypeMapper） BEGIN ==== -->"
MARKER_END = "  <!-- ==== T2 · 补丁 Q：MimeType 内置表（MimeTypeMapper） END ==== -->"

HEADER = """// ─────────────────────────────────────────────────────────────────────────────
// **生成物 —— 不要手改**（本仓纪律：生成物由应用器重生成）。
//   应用器：src/WpfGfx.Linux.Native/tools/patch-presentationcore-mimetype.py（PC 补丁 Q）
//   上游：  upstream/wpf/src/Microsoft.DotNet.Wpf/src/Shared/MS/Internal/MimeTypeMapper.cs
//   内容：  上游逐字 + 3 处锚点替换（内置表合并 / 调用点 / UrlMon 方法 → 内置表方法）
//   重生成：python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-mimetype.py
//   自检：  python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-mimetype.py --check
//   原因：  UrlMon 的 FindMimeFromData 要 marshal COM 接口指针，Linux 上封送阶段即抛
//           MarshalDirectiveException ⇒ XAML 里按 pack:// URI 引用图片的 app 一律死在这里。
// ─────────────────────────────────────────────────────────────────────────────
"""

# ── ① 初始化块：合并内置表 ────────────────────────────────────────────────────
ANCHOR1 = "                        _fileExtensionToMimeType.Add(XbapExtension, XbapMime);\n"
REPLACEMENT1 = ANCHOR1 + """
                        // ── Linux 补丁 Q：Windows 上其余扩展名的 MIME 由 UrlMon 从注册表读出；
                        //    Linux 无 urlmon ⇒ 合并内置表（口径与边界见表定义处注释）。
                        foreach (var pair in GetBuiltInExtensionTable())
                        {
                            _fileExtensionToMimeType.Add(pair.Key, pair.Value);
                        }
"""

# ── ② 调用点：UrlMon → 内置表 ─────────────────────────────────────────────────
CALLSITE_NEEDLE = "mimeType = GetMimeTypeFromUrlMon(uriSource);"
CALLSITE_PATTERN = re.compile(
    r"(?m)^[ \t]*//[ \t]*\n(?:[ \t]*//[^\n]*\n)*[ \t]*//[ \t]*\n"
    r"[ \t]*mimeType = GetMimeTypeFromUrlMon\(uriSource\);\n"
)
CALLSITE_REPL = """                        //
                        // Linux 补丁 Q：上游在这里调 UrlMon 问注册表（表里没有的扩展名）。
                        //   Linux 上那条路走不通 —— `FindMimeFromData` 的第 1 参是 COM 接口
                        //   指针，.NET 在**封送阶段**就抛 MarshalDirectiveException（见文件头）。
                        //   改为查内置表；表里没有的扩展名返回 OctetMime（与 Windows 上
                        //   "未知类型"同口径，引证 WpfWebRequestHelper.cs:288-297）。
                        //
                        mimeType = GetMimeTypeFromBuiltInTable(completeExt);
"""

# ── ③ UrlMon 方法整块 → 内置表方法 + 表 ──────────────────────────────────────
METHOD_START = (
    "        //\n"
    "        // Call UrlMon API to get MimeType for a given extension.\n"
    "        //\n"
    "        private static ContentType GetMimeTypeFromUrlMon(Uri uriSource)\n"
)
METHOD_END = "        private static string GetDocument(Uri uri)"

NEW_METHOD = '''        //
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

'''

REQUIRED_IN_OUTPUT = [
    ("内置表方法", "private static ContentType GetMimeTypeFromBuiltInTable(string extension)"),
    ("内置表字段", "_builtInExtensionToMimeType"),
    ("合并内置表", "_fileExtensionToMimeType.Add(pair.Key, pair.Value);"),
    ("调用点已换", "mimeType = GetMimeTypeFromBuiltInTable(completeExt);"),
    ("兜底口径", "return OctetMime;"),
    ("图片表项 ico", 'table.Add("ico",  IconMime);'),
    # ── 静态初始化顺序的**结构性**断言（#34 波实测坑，见 NEW_METHOD 里的注释）──
    # 字段声明**必须**以 `;` 结尾 ⇒ 证明表不是字段初始化器（字段级会让 `IconMime` 还是 null）
    ("惰性表 · 字段无初始化器",
     "private static Dictionary<string, ContentType> _builtInExtensionToMimeType;"),
    ("惰性表 · 构造方法存在",
     "private static Dictionary<string, ContentType> GetBuiltInExtensionTable()"),
    ("惰性表 · 查表走方法", "GetBuiltInExtensionTable().TryGetValue(extension, out mimeType)"),
    ("惰性表 · 合并走方法", "foreach (var pair in GetBuiltInExtensionTable())"),
]
FORBIDDEN_IN_OUTPUT = [
    ("UrlMon P/Invoke", "FindMimeFromData"),
    ("UrlMon 声明类型", "UnsafeNativeMethods"),
    ("旧方法名", "GetMimeTypeFromUrlMon"),
    ("COM 封送修饰", "MarshalAs"),
]


def _count(haystack, needle):
    return haystack.count(needle)


def strip_comments(text):
    """去掉 `//` 行注释与 `/* */` 块注释（块注释按行数用换行占位，保住行号）。

    【为什么需要它】禁项检查必须只看**正文**：本补丁的注释要**说明原因**，就必然要写出
    `FindMimeFromData` 这个名字（不写名字反而说不清"替掉的是什么"）。第一版把禁项
    直接扫全文 ⇒ 应用器被**自己的注释**判红（rc=1、生成物缺失）——这正是纪律 70
    "散文里的否定与实测可以背道而驰"的又一次现身：**注释里的字面量不是代码，但断言
    分不清**。⇒ 判据改成"剥掉注释后的正文里为 0"，并把注释里的出现次数**打出来**。
    """
    out = re.sub(r"/\*.*?\*/", lambda m: "\n" * m.group(0).count("\n"), text, flags=re.S)
    return "\n".join(re.sub(r"//.*$", "", line) for line in out.splitlines())


def generate(check_only):
    if not os.path.exists(UPSTREAM):
        print(f"[失败] 找不到上游 {UPSTREAM}")
        return 1
    with open(UPSTREAM, encoding="utf-8-sig") as f:
        text = f.read()

    bad = False

    # ① 初始化块
    n1 = _count(text, ANCHOR1)
    print(f"[锚点] ① 初始化块（Add(XbapExtension…)）：上游出现 {n1} 次（要求 1）")
    bad |= (n1 != 1)

    # ② 调用点（连同它上面那个注释块一起换掉）
    n2 = _count(text, CALLSITE_NEEDLE)
    m2 = list(CALLSITE_PATTERN.finditer(text))
    print(f"[锚点] ② 调用点 + 注释块：调用出现 {n2} 次（要求 1）、注释块匹配 {len(m2)} 次（要求 1）")
    bad |= (n2 != 1 or len(m2) != 1)

    # ③ UrlMon 方法整块
    n3a = _count(text, METHOD_START)
    n3b = _count(text, METHOD_END)
    print(f"[锚点] ③ UrlMon 方法：起始 {n3a} 次（要求 1）、后继锚点 {n3b} 次（要求 1）")
    bad |= (n3a != 1 or n3b != 1)

    if bad:
        print("[失败] 锚点缺失或重复 —— 上游这段改过了？补丁 Q 未应用（**不做任何静默降级**）。")
        return 1

    out = text
    out = out.replace(ANCHOR1, REPLACEMENT1, 1)
    out = CALLSITE_PATTERN.sub(CALLSITE_REPL, out, count=1)
    i = out.index(METHOD_START)
    j = out.index(METHOD_END, i)
    out = out[:i] + NEW_METHOD + out[j:]

    # ---- 结构性断言（生成物自检）----
    for name, needle in REQUIRED_IN_OUTPUT:
        if needle not in out:
            print(f"[失败] 生成物缺少结构断言：{name}（{needle}）")
            return 1
    for name, needle in FORBIDDEN_IN_OUTPUT:
        n_code = _count(strip_comments(out), needle)
        n_all = _count(out, needle)
        if n_code != 0:
            print(f"[失败] 生成物**正文**里仍有禁项：{name}（{needle}）×{n_code}")
            return 1
        if n_all:
            print(f"[断言] 禁项 {name}：正文 0 次、注释 {n_all} 次（解释原因用，允许）")
    print(f"[断言] 禁项 4/4 在**正文**里 0 次（注释里提到不算：注释不是代码）")

    # ---- 机械证据：没有新增/删除任何 throw（**只数正文**：上游注释里那句
    #      "will not throw an exception on failure" 是散文，不是代码 —— 第一版按全文数，
    #      被自己换掉的那段注释判红，实测上游 1 → 生成物 0）----
    up_throw = _count(strip_comments(text), "throw ")
    out_throw = _count(strip_comments(out), "throw ")
    if up_throw != out_throw:
        print(f"[失败] `throw` 条数变了：上游 {up_throw} → 生成物 {out_throw}（补丁 Q 只允许短路与查表）")
        return 1
    print(f"[断言] `throw` 条数（正文）上游 {up_throw} == 生成物 {out_throw}"
          f"（全文含注释：上游 {_count(text, 'throw ')} → 生成物 {_count(out, 'throw ')}，"
          "差值全部来自被替换掉的注释散文）")
    print(f"[断言] 上游 {len(text.splitlines())} 行 → 生成物 {len(out.splitlines())} 行"
          f"（{len(out.splitlines()) - len(text.splitlines()):+d} 行 = 内置表 + 注释）")

    output = "\ufeff" + HEADER + out

    up_to_date = False
    if os.path.exists(GENERATED):
        with open(GENERATED, encoding="utf-8") as f:
            up_to_date = (f.read() == output)

    if check_only:
        print(f"[检查] {os.path.relpath(GENERATED, ROOT)}："
              + ("内容已是最新" if up_to_date else "缺失/与上游不同步（需要重新生成）"))
    elif up_to_date:
        print(f"[生成] {os.path.relpath(GENERATED, ROOT)}：内容已是最新（未重写）")
    else:
        with open(GENERATED, "w", encoding="utf-8") as f:
            f.write(output)
        print(f"[生成] {os.path.relpath(GENERATED, ROOT)}：已从上游重生成（3 处锚点替换）")

    # ---- csproj 接线 ----
    with open(CSPROJ, encoding="utf-8-sig") as f:
        csproj = f.read()

    if MARKER_BEGIN in csproj:
        print("[接线] csproj 已就位（幂等，不改）")
        return 0 if (not check_only or up_to_date) else 1

    if check_only:
        print("[接线] csproj **未接线** —— 需要运行一次不带 --check 的本脚本")
        return 1

    anchor = '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'
    if _count(csproj, anchor) != 1:
        print(f"[失败] csproj 里 Sdk.targets 锚点出现 {_count(csproj, anchor)} 次（要求 1）")
        return 1

    # ① 整块（含 <ItemGroup>）插在锚点行之前；② Remove 落在上游那条 Include 之后 ⇒ 生效
    block = (MARKER_BEGIN + "\n"
             "  <!-- T2 · 补丁 Q：Linux 上 UrlMon 的 FindMimeFromData 要 marshal COM 指针，\n"
             "       封送阶段即抛 MarshalDirectiveException ⇒ 表外扩展名（png/gif/ico/…）一律\n"
             "       走内置表。生成物 = 上游逐字 + 3 处锚点替换。 -->\n"
             "  <ItemGroup>\n"
             f'    <Compile Remove="$(UpstreamWpfRoot){UPSTREAM_REL}" />\n'
             '    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/MimeTypeMapper.Linux.cs" />\n'
             "  </ItemGroup>\n"
             + MARKER_END + "\n")
    csproj = csproj.replace(anchor, block + anchor, 1)
    with open(CSPROJ, "w", encoding="utf-8") as f:
        f.write(csproj)
    print(f"[接线] 已注入 2 行到 {os.path.relpath(CSPROJ, ROOT)}（Remove 之后于上游 Include）")
    print("\n下一步（**不要在这里重建 PC**，留给集成波）：")
    print("  dotnet msbuild src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/PresentationCore.Linux.csproj -m:1 --nologo \\")
    print("      -getItem:Compile -p:Configuration=Release | grep -i MimeTypeMapper")
    print("  # 期望只剩 MimeTypeMapper.Linux.cs（上游那条被 Remove 掉）")
    return 0


def main():
    ap = argparse.ArgumentParser(
        description="PC 补丁 Q：MimeType 内置表（MimeTypeMapper）—— 生成物 + csproj 接线")
    ap.add_argument("--check", action="store_true", help="只检查，不修改任何文件（0=已就位）")
    ap.add_argument("--apply", action="store_true", help="显式表示要写盘（默认行为，等价）")
    args = ap.parse_args()
    return generate(check_only=args.check)


if __name__ == "__main__":
    sys.exit(main())
