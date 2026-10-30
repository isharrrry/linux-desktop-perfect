// T2 · 字体入口闭环（追加 1/2）：公开入口 new GlyphTypeface(Uri) 的 NRE 与 FontFamily(名字) 的 OSVersionHelper 链
// 纪律：每格给证据（异常类型 + 完整栈 + 四字段）；能/不能都如实打印，不把"没崩"当通过。
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private static readonly Assembly Pc = typeof(GlyphTypeface).Assembly;
    private static int _bad;

    // MS.Internal.Text.TextInterface.* 在 **DirectWriteForwarder** 程序集里（不是 PC）
    private static readonly Assembly Dwf = typeof(System.Windows.Media.GlyphTypeface).Assembly.GetReferencedAssemblies()
        .Length > 0 ? Assembly.Load("DirectWriteForwarder") : null;
    private static Type T(string n)
    {
        var t = Pc.GetType(n, false) ?? Dwf?.GetType(n, false);
        if (t == null) throw new InvalidOperationException("找不到类型 " + n);
        return t;
    }
    private static object Stat(string type, string method, params object[] a)
    {
        var t = T(type);
        var m = t.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null,
                            Array.ConvertAll(a, x => x.GetType()), null);
        if (m == null) throw new InvalidOperationException("找不到静态方法 " + type + "." + method);
        return m.Invoke(null, a);
    }
    private static object Inst() => T("MS.Internal.Text.TextInterface.DWriteFactory")
        .GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

    private static void Dump(string label, Exception e)
    {
        string t = e.ToString();
        string first = "";
        foreach (string raw in t.Split('\n')) { string l = raw.Trim(); if (l.StartsWith("at ")) { first = l; break; } }
        Console.WriteLine($"  [{label}] {e.GetType().Name}: {e.Message.Replace('\n',' ')}");
        Console.WriteLine($"  [{label}] FIRST_FRAME={first}");
        Console.WriteLine($"  [{label}] VIA_DISPATCHER_OR_WINDOW={t.Contains("Dispatcher")}");
        Console.WriteLine($"  [{label}] IS_WIC_FRAME={t.Contains("WIC")}");
        Console.WriteLine($"  [{label}] ---- 完整异常 ----\n{t}\n  [{label}] ---- 结束 ----");
    }

    private static int Main(string[] args)
    {
        string repo = FindRepoRoot();
        string fonts = Path.Combine(repo, "src", "Linux", "build", "fonts");
        string fontFile = null;
        if (Directory.Exists(fonts))
        {
            var fs = Directory.GetFiles(fonts, "*.ttf");
            Array.Sort(fs);
            if (fs.Length > 0) fontFile = fs[0];
        }
        Console.WriteLine("REPO=" + repo);
        Console.WriteLine("FONT_FILE=" + (fontFile ?? "<无>"));
        if (fontFile == null) { Console.WriteLine("RESULT=SKIP 没有 src/Linux/build/fonts/*.ttf"); return 2; }

        // ---------- A. 公开入口 new GlyphTypeface(Uri) ----------
        Console.WriteLine("--- A. 公开入口 new GlyphTypeface(Uri) ---");
        try
        {
            var gt = new GlyphTypeface(new Uri(fontFile));
            Console.WriteLine($"  A=PASS 公开入口可用：GlyphCount={gt.GlyphCount} Version={gt.Version} FamilyNames={gt.FamilyNames.Count}");
        }
        catch (Exception e) { Console.WriteLine("  A=FAIL（公开入口不可用）"); Dump("A", e); _bad++; }

        // ---------- A2. 把这条链逐步拆开看谁返回 null ----------
        Console.WriteLine("--- A2. Uri 链逐步拆解（反射进 MS.Internal.Text.TextInterface）---");
        object coll = null, face = null, font = null;
        var uri = new Uri(fontFile);
        try
        {
            coll = Stat("MS.Internal.Text.TextInterface.DWriteFactory", "GetFontCollectionFromFile", uri);
            Console.WriteLine("  GetFontCollectionFromFile → " + (coll == null ? "**null**" : coll.GetType().Name));
        }
        catch (Exception e) { Console.WriteLine("  GetFontCollectionFromFile 抛异常"); Dump("A2-coll", e); }

        try
        {
            var inst = Inst();
            var sims = Enum.ToObject(T("MS.Internal.Text.TextInterface.FontSimulations"), 0);
            var m = inst.GetType().GetMethod("CreateFontFace", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                                             null, new[] { typeof(Uri), typeof(uint), T("MS.Internal.Text.TextInterface.FontSimulations") }, null);
            face = m.Invoke(inst, new object[] { uri, 0u, sims });
            Console.WriteLine("  CreateFontFace(uri) → " + (face == null ? "**null**" : face.GetType().Name));
        }
        catch (Exception e) { Console.WriteLine("  CreateFontFace(uri) 抛异常"); Dump("A2-face", e); }

        if (coll != null && face != null)
        {
            try
            {
                var m = coll.GetType().GetMethod("GetFontFromFontFace", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                font = m.Invoke(coll, new[] { face });
                Console.WriteLine("  GetFontFromFontFace → " + (font == null ? "**null**" : font.GetType().Name));
                if (font != null)
                {
                    var gff = font.GetType().GetMethod("GetFontFace", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    object ff = gff.Invoke(font, null);
                    Console.WriteLine("  Font.GetFontFace() → " + (ff == null ? "**null**（NRE 的直接原因）" : ff.GetType().Name));
                }
            }
            catch (Exception e) { Console.WriteLine("  GetFontFromFontFace/GetFontFace 抛异常"); Dump("A2-font", e); }
        }

        // ---------- B. FontFamily / Typeface 写法矩阵 ----------
        Console.WriteLine("--- B. FontFamily 写法矩阵（每格：走没走到 OSVersionHelper / 能否建出 GlyphTypeface）---");
        // 注：SystemFonts.MessageFontFamily 在 PresentationFramework（本 harness 不引用它；M7b 的 HelloWpf 已实测该写法可用）
        Matrix("FontFamily(\"Noto Sans\") 已安装", () => new FontFamily("Noto Sans"), null);
        Matrix("FontFamily(\"Arial\") 未安装", () => new FontFamily("Arial"), null);
        Matrix("FontFamily(\"file://…#family\") 私有字体", () => new FontFamily(new Uri(fontFile).AbsoluteUri + "#" + FirstFamilyName(fontFile)), null);

        Console.WriteLine("RESULT=" + (_bad == 0 ? "PASS" : $"FAIL({_bad})（A 段=公开入口，B 段见上表）"));
        return _bad == 0 ? 0 : 1;
    }

    private static string FirstFamilyName(string fontFile)
    {
        return Path.GetFileNameWithoutExtension(fontFile).Replace('-', ' ');
    }

    private static void Matrix(string label, Func<FontFamily> make, object _)
    {
        Console.Write($"  [{label}] ");
        try
        {
            var ff = make();
            Console.Write($"FontFamily 建出 ok(Source={ff.Source}) ");
            var tf = new Typeface(ff, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (!tf.TryGetGlyphTypeface(out GlyphTypeface gt)) { Console.WriteLine("→ TryGetGlyphTypeface=false（**没有可用面**）"); return; }
            Console.WriteLine($"→ GlyphTypeface ok：GlyphCount={gt.GlyphCount} Version={gt.Version}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"→ 抛 {e.GetType().Name}: {e.Message.Replace('\n',' ')}");
            Dump(label, e);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null) { if (File.Exists(Path.Combine(dir.FullName, "handoff.md"))) return dir.FullName; dir = dir.Parent; }
        return Directory.GetCurrentDirectory();
    }
}
