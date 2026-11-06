// 【K-648】AT-SPI 控件树导出器（C# 版，替代 dump_tree.py）
// 依赖：只调 gdbus 命令行，无第三方 NuGet。
// 用法：
//   dump-tree [--json] [--wait N] [--bus <addr>]
// 退出码：0=找到且≥10 控件 / 1=控件不足 / 3=未找到 / 4=gdbus 不可用
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace DumpTree;

internal static class Program
{
    private static string _busAddr = "";
    private const string Registry = "org.a11y.atspi.Registry";
    private const string Root = "/org/a11y/atspi/accessible/root";

    private static int Main(string[] argv)
    {
        var args = new List<string>(argv);
        bool asJson = args.Remove("--json");
        int waitSec = 0;
        int wi = args.IndexOf("--wait");
        if (wi >= 0 && wi + 1 < args.Count)
        {
            int.TryParse(args[wi + 1], out waitSec);
            args.RemoveRange(wi, 2);
        }
        int bi = args.IndexOf("--bus");
        if (bi >= 0 && bi + 1 < args.Count)
        {
            _busAddr = args[bi + 1];
            args.RemoveRange(bi, 2);
        }
        if (string.IsNullOrEmpty(_busAddr))
        {
            _busAddr = DiscoverBus() ?? "";
            if (_busAddr.Length == 0)
            {
                Console.Error.WriteLine("ERROR: 找不到 AT-SPI bus（请设 AT_SPI_BUS_ADDRESS 或用 --bus）");
                return 4;
            }
        }

        if (!HasGdbus())
        {
            Console.Error.WriteLine("ERROR: gdbus 不可用");
            return 4;
        }

        var deadline = DateTime.UtcNow.AddSeconds(waitSec);
        DesktopSnapshot? snap = null;
        while (true)
        {
            snap = TakeSnapshot();
            if (snap is { Found: true } || DateTime.UtcNow >= deadline) break;
            Thread.Sleep(1000);
        }

        if (asJson) PrintJson(snap);
        else PrintHuman(snap);

        if (snap is not { Found: true }) return 3;
        return snap.Controls.Count >= 10 ? 0 : 1;
    }

    // ── bus 发现 ────────────────────────────────────────────────────────────
    private static string? DiscoverBus()
    {
        var env = Environment.GetEnvironmentVariable("AT_SPI_BUS_ADDRESS");
        if (!string.IsNullOrEmpty(env)) return env;

        var display = Environment.GetEnvironmentVariable("DISPLAY") ?? "";
        var m = Regex.Match(display, @":(\d+)");
        if (m.Success)
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? "";
            foreach (var cand in new[] {
                $"{home}/.cache/at-spi/bus_{m.Groups[1].Value}",
                $"{home}/.cache/at-spi/bus"
            })
                if (File.Exists(cand)) return $"unix:path={cand}";
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(xdg) && m.Success)
        {
            var p = $"{xdg}/at-spi/bus_{m.Groups[1].Value}";
            if (File.Exists(p)) return $"unix:path={p}";
        }
        return null;
    }

    private static bool HasGdbus()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("gdbus", "--help")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            p?.WaitForExit(3000);
            return p is { ExitCode: 0 or 1 };
        }
        catch { return false; }
    }

    // ── gdbus 调用 ─────────────────────────────────────────────────────────
    private static string Call(string dest, string path, string iface, string method, params string[] extra)
    {
        var sb = new StringBuilder();
        sb.Append("call --address \"").Append(_busAddr).Append("\" --dest ").Append(dest)
          .Append(" --object-path ").Append(path).Append(" --method ").Append(iface).Append('.').Append(method);
        foreach (var e in extra) sb.Append(' ').Append(e);
        try
        {
            using var p = Process.Start(new ProcessStartInfo("gdbus", sb.ToString())
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (p is null) return "";
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return output.Trim();
        }
        catch { return ""; }
    }

    // GVariant 文本 → 简单提取
    private static string ExtractString(string gv)
    {
        var m = Regex.Match(gv, @"'([^']*)'");
        return m.Success ? m.Groups[1].Value : "";
    }
    private static int ExtractInt(string gv)
    {
        var m = Regex.Match(gv, @"(-?\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    // ── 快照 ────────────────────────────────────────────────────────────────
    private sealed record Node(int Depth, string Bus, string Path, string Name, string Role, int ChildCount);
    private sealed record DesktopApp(int Index, string Name, string Role, int Children);
    private sealed class DesktopSnapshot
    {
        public int DesktopChildCount { get; set; }
        public List<DesktopApp> Apps { get; } = new();
        public bool Found { get; set; }
        public List<Node> Controls { get; } = new();
    }

    private static DesktopSnapshot TakeSnapshot()
    {
        var snap = new DesktopSnapshot();
        var kids = Call(Registry, Root, "org.a11y.atspi.Accessible", "GetChildren");
        var refs = ParseRefs(kids);
        snap.DesktopChildCount = refs.Count;

        for (int i = 0; i < refs.Count; i++)
        {
            var (bus, path) = refs[i];
            var name = ExtractString(Call(bus, path, "org.a11y.atspi.Accessible", "GetName"));
            var role = ExtractString(Call(bus, path, "org.a11y.atspi.Accessible", "GetRoleName"));
            var cnt = ExtractInt(Call(bus, path, "org.freedesktop.DBus.Properties", "Get",
                "org.a11y.atspi.Accessible", "ChildCount"));
            if (name.Length == 0)
                name = ExtractString(Call(bus, path, "org.freedesktop.DBus.Properties", "Get",
                    "org.a11y.atspi.Accessible", "Name"));
            snap.Apps.Add(new DesktopApp(i, name, role, cnt));

            if (name.Equals("SourceTree", StringComparison.OrdinalIgnoreCase) && !snap.Found)
            {
                snap.Found = true;
                Walk(bus, path, 0, snap.Controls, new HashSet<string>());
            }
        }
        return snap;
    }

    private static List<(string bus, string path)> ParseRefs(string gv)
    {
        var list = new List<(string, string)>();
        foreach (Match m in Regex.Matches(gv, @"\('([^']+)',\s*objectpath\s+'([^']+)'\)"))
            list.Add((m.Groups[1].Value, m.Groups[2].Value));
        foreach (Match m in Regex.Matches(gv, @"\('([^']+)',\s*'([^']+)'\)"))
            if (!list.Any(x => x.Item1 == m.Groups[1].Value && x.Item2 == m.Groups[2].Value))
                list.Add((m.Groups[1].Value, m.Groups[2].Value));
        return list;
    }

    private static void Walk(string bus, string path, int depth, List<Node> acc, HashSet<string> seen,
                             int maxDepth = 8, int maxChildren = 40)
    {
        if (depth > maxDepth) return;
        if (!seen.Add(bus + path)) return;

        var name = ExtractString(Call(bus, path, "org.a11y.atspi.Accessible", "GetName"));
        var role = ExtractString(Call(bus, path, "org.a11y.atspi.Accessible", "GetRoleName"));
        var cnt = ExtractInt(Call(bus, path, "org.freedesktop.DBus.Properties", "Get",
            "org.a11y.atspi.Accessible", "ChildCount"));
        if (name.Length == 0)
            name = ExtractString(Call(bus, path, "org.freedesktop.DBus.Properties", "Get",
                "org.a11y.atspi.Accessible", "Name"));

        acc.Add(new Node(depth, bus, path, name, role, cnt));
        if (cnt <= 0) return;

        var kids = ParseRefs(Call(bus, path, "org.a11y.atspi.Accessible", "GetChildren"));
        foreach (var (b, p) in kids.Take(maxChildren))
            Walk(b, p, depth + 1, acc, seen, maxDepth, maxChildren);
    }

    // ── 输出 ────────────────────────────────────────────────────────────────
    private static void PrintHuman(DesktopSnapshot snap)
    {
        Console.WriteLine($"桌面子项数: {snap.DesktopChildCount}");
        foreach (var a in snap.Apps)
            Console.WriteLine($"  [{a.Index}] '{a.Name}' role={a.Role} children={a.Children}");
        if (!snap.Found)
        {
            Console.WriteLine("\n❌ 未找到 SourceTree");
            return;
        }
        var named = snap.Controls.Count(c => c.Name.Length > 0);
        Console.WriteLine($"\n✅ 找到 SourceTree，枚举了 {snap.Controls.Count} 个控件（{named} 个有 name）");
        foreach (var c in snap.Controls)
            Console.WriteLine($"{new string(' ', c.Depth * 2)}[{c.Role}] '{c.Name}' (childCount={c.ChildCount})");
    }

    private static void PrintJson(DesktopSnapshot snap)
    {
        var obj = new
        {
            desktop_child_count = snap.DesktopChildCount,
            apps = snap.Apps.Select(a => new { index = a.Index, name = a.Name, role = a.Role, children = a.Children }),
            found_sourcetree = snap.Found,
            controls = snap.Controls.Select(c => new
            {
                depth = c.Depth, bus = c.Bus, path = c.Path, name = c.Name, role = c.Role, child_count = c.ChildCount
            }),
        };
        Console.WriteLine(JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
    }
}
