// audit-geometry.cs —— 从运行日志提取控件树几何，与入库证据比对（K-1090 的 C# 版）
// 用法: audit-geometry <st*.log> <proof-uia-tree-geometry.json>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

class P {
  static int Main(string[] a) {
    if (a.Length < 2) { Console.WriteLine("用法: audit-geometry <log> <json>"); return 2; }
    var lines = File.ReadAllLines(a[0]);
    var fromLog = new List<(string type, string w, string h)>();
    for (int i = 0; i < lines.Length; i++) {
      if (lines[i].Trim() == "[LOOP] child-type=" && i + 5 < lines.Length) {
        var t = lines[i + 1].Trim();
        var w = (i + 3 < lines.Length && lines[i + 2].Trim() == "[GEO] W=") ? lines[i + 3].Trim() : "";
        var h = (i + 5 < lines.Length && lines[i + 4].Trim() == "[GEO] H=") ? lines[i + 5].Trim() : "";
        if (t.Length > 0) fromLog.Add((t, w, h));
      }
    }
    using var doc = JsonDocument.Parse(File.ReadAllText(a[1]));
    var fromJson = doc.RootElement.GetProperty("geometry").EnumerateArray()
      .Select(e => (type: e.GetProperty("type").GetString(), w: e.GetProperty("w").GetRawText(), h: e.GetProperty("h").GetRawText()))
      .ToList();
    Console.WriteLine($"日志: {fromLog.Count} 条 / 证据: {fromJson.Count} 条");
    if (fromLog.Count != fromJson.Count) { Console.WriteLine("verdict=FAIL (数量不一致)"); return 1; }
    for (int i = 0; i < fromLog.Count; i++) {
      if (fromLog[i].type != fromJson[i].type) {
        Console.WriteLine($"verdict=FAIL (第 {i} 条类型不同: 日志={fromLog[i].type} 证据={fromJson[i].type})");
        return 1;
      }
    }
    Console.WriteLine("类型序列一致: True");
    Console.WriteLine("verdict=PASS");
    return 0;
  }
}
