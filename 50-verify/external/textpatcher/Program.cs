// 【K-752】通用文本替换工具（替代 patch-*.py 的核心逻辑）
// 用法：textpatcher --src <源文件> --out <输出文件> --edits <edits.txt>
// edits.txt 格式：每行 "anchor\trepl"（Tab 分隔），或者用 --data 选项
using System; using System.IO; using System.Text; using System.Collections.Generic;

class P {
    static int Main(string[] a) {
        string src = null, outp = null, editsFile = null, headerFile = null;
        for (int i = 0; i < a.Length; i++) {
            if (a[i] == "--src" && i+1 < a.Length) src = a[++i];
            else if (a[i] == "--out" && i+1 < a.Length) outp = a[++i];
            else if (a[i] == "--edits" && i+1 < a.Length) editsFile = a[++i];
            else if (a[i] == "--header" && i+1 < a.Length) headerFile = a[++i];
        }
        if (src == null || outp == null || editsFile == null) {
            Console.Error.WriteLine("用法: textpatcher --src <源> --out <输出> --edits <edits.txt>");
            return 2;
        }
        var text = File.ReadAllText(src, Encoding.UTF8);
        var edits = LoadEdits(editsFile);
        Console.WriteLine($"[输入] {src}（{text.Length} 字符）");
        Console.WriteLine($"[替换] {edits.Count} 条");
        var outp_text = text;
        int applied = 0;
        foreach (var (name, anchor, repl) in edits) {
            int n = CountOccurrences(outp_text, anchor);
            Console.WriteLine($"  [{name}] 锚点出现 {n} 次");
            if (n != 1) { Console.Error.WriteLine($"  [失败] 锚点 {name} 出现 {n} 次（要求 1）"); return 1; }
            outp_text = ReplaceFirst(outp_text, anchor, repl);
            ++applied;
        }
        // 断言
        int throwBefore = CountOccurrences(text, "throw ");
        int throwAfter = CountOccurrences(outp_text, "throw ");
        if (throwBefore != throwAfter) { Console.Error.WriteLine($"[失败] throw 条数变了：{throwBefore} → {throwAfter}"); return 1; }
        Console.WriteLine($"[断言] throw {throwBefore}=={throwAfter}");
        if (headerFile != null) {
            var header = File.ReadAllText(headerFile, Encoding.UTF8);
            outp_text = header + outp_text;
            Console.WriteLine($"[头部] 插入 {header.Length} 字符（{headerFile}）");
        }
        File.WriteAllText(outp, outp_text, Encoding.UTF8);
        Console.WriteLine($"[写出] {outp}（{outp_text.Length} 字符，{applied} 条替换）");
        return 0;
    }
    static List<(string name, string anchor, string repl)> LoadEdits(string path) {
        var list = new List<(string, string, string)>();
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        foreach (var line in lines) {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
            var parts = line.Split('\t');
            if (parts.Length >= 3) list.Add((parts[0], parts[1].Replace("\\n", "\n"), parts[2].Replace("\\n", "\n")));
        }
        return list;
    }
    static int CountOccurrences(string text, string needle) {
        if (string.IsNullOrEmpty(needle)) return 0;
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0) { ++count; idx += needle.Length; }
        return count;
    }
    static string ReplaceFirst(string text, string oldValue, string newValue) {
        int idx = text.IndexOf(oldValue, StringComparison.Ordinal);
        if (idx < 0) return text;
        return text.Substring(0, idx) + newValue + text.Substring(idx + oldValue.Length);
    }
}
