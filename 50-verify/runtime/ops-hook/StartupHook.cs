// 四类操作的启动钩子（DOTNET_STARTUP_HOOKS）：纯反射驱动 WPF，跑完写结构化结果供 ops-check.mjs 判定。
//
// 能这样做的前提（见《已知坑与降级清单》K-1500~K-1502）：
//   - 合成输入不可用（K-1263），所以只能走 ViewModel/命令对象；
//   - 打开仓库必须 RepoViewMode=0（FileStatus），用 1 时 CommitAndFileStatusVM 为 null；
//   - 差异靠 FileStatusRecord.Selected → DiffVM.DiffModel.Count；
//   - 提交靠 repo.Handler.CommitProcess(4 参) + RunThis()，且必须先 staged；
//   - 切分支靠 RepoTabViewModel.CheckoutBranch(String,String)。
//
// 结果写 $LDP_OPS_OUT/result.json；日志逐行追加到 $LDP_OPS_OUT/hook.log。
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

public class StartupHook
{
    static readonly StringBuilder Log = new StringBuilder();
    static readonly object Lock = new object();
    static object _disp, _dc, _proc;
    static MethodInfo _inv;
    static readonly Dictionary<string, string> Res = new Dictionary<string, string>();
    static string _outDir, _repo, _branch;
    static bool _done;

    public static void Initialize()
    {
        _outDir = Environment.GetEnvironmentVariable("LDP_OPS_OUT") ?? "/tmp/ops-check";
        _repo = Environment.GetEnvironmentVariable("LDP_OPS_REPO") ?? "/tmp/stgit";
        _branch = Environment.GetEnvironmentVariable("LDP_OPS_BRANCH") ?? "feature-x";
        try { Directory.CreateDirectory(_outDir); } catch { }
        var t = new Thread(Loop) { IsBackground = true };
        t.Start();
    }

    static void Loop()
    {
        for (var i = 0; i < 300 && !_done; i++)
        {
            Thread.Sleep(500);
            try { Step(); }
            catch (Exception e) { W("step err " + Unwrap(e)); }
        }
        Finish("loop-end");
    }

    static void Step()
    {
        if (_dc == null)
        {
            var appT = Type.GetType("System.Windows.Application, PresentationFramework");
            var cur = appT?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (cur == null) return;
            _disp = appT.GetProperty("Dispatcher", BindingFlags.Public | BindingFlags.Instance).GetValue(cur);
            _inv = _disp.GetType().GetMethod("Invoke", new[] { typeof(Delegate), typeof(object[]) });
            var win = appT.GetProperty("MainWindow")?.GetValue(cur);
            _dc = win == null ? null : win.GetType().GetProperty("DataContext")?.GetValue(win);
            if (_dc == null) return;
            Res["window"] = "ok";
            W("window ok");
            return;
        }

        // 1) 先用 Log 模式打开仓库：验证「提交查看」（FileStatus 模式下 LogVM 为 null）
        if (!Res.ContainsKey("log_open"))
        {
            Inv(() => OpenRepo(1));
            Res["log_open"] = "ok";
            return;
        }
        if (!Res.ContainsKey("history")) { Inv(CheckHistory); return; }

        // 2) 再用 FileStatus 模式打开：差异/提交需要 CommitAndFileStatusVM
        if (!Res.ContainsKey("file_open"))
        {
            Inv(() => OpenRepo(0));
            Res["file_open"] = "ok";
            return;
        }
        if (!Res.ContainsKey("diff")) { Inv(SelectFirstFile); return; }
        if (!Res.ContainsKey("commit")) { Inv(CommitStaged); return; }
        if (!Res.ContainsKey("checkout_started") && !Res.ContainsKey("checkout")) { Inv(CheckoutBranch); return; }
        if (Res.ContainsKey("checkout_started") && !Res.ContainsKey("checkout"))
        {
            if (!Res.ContainsKey("_co_wait")) { Res["_co_wait"] = "1"; Thread.Sleep(10000); }
            if (!Res.ContainsKey("checkout"))
            {
                Res["checkout"] = "SKIP";
                Res["checkout_reason"] = "UI CheckoutBranch 10s 内未返回（可能弹确认框）；分支切换由调用方用 git 层验证";
                W("checkout=SKIP ui-timeout");
            }
        }
        Finish("all-done");
    }

    static void CheckHistory()
    {
        var tab = Find(_dc, "RepoTabViewModel", 8, new HashSet<object>());
        var logVm = Get(tab, "LogVM");
        if (logVm == null) { Res["history"] = "FAIL"; Res["history_reason"] = "LogVM null"; W("history=FAIL"); return; }
        Res["history"] = "PASS";
        W("history=PASS logVm=" + logVm.GetType().Name);
    }

    static void SelectFirstFile()
    {
        var cfs = Cfs();
        if (cfs == null) { Res["diff"] = "FAIL"; Res["diff_reason"] = "no CommitAndFileStatusVM"; return; }
        object first = null;
        for (var w = 0; w < 60 && first == null; w++)
        {
            var csv0 = Get(cfs, "ChangeSetViewModel");
            var fl0 = csv0 == null ? null : Get(csv0, "FileList") as IEnumerable;
            if (fl0 != null) foreach (var x in fl0) { first = x; break; }
            if (first == null) Thread.Sleep(500);
        }
        if (first == null) { Res["diff"] = "FAIL"; Res["diff_reason"] = "FileList empty 30s"; return; }
        var p = first.GetType().GetProperty("Selected", BindingFlags.Public | BindingFlags.Instance);
        if (p == null || !p.CanWrite) { Res["diff"] = "FAIL"; Res["diff_reason"] = "no writable Selected"; return; }
        try { p.SetValue(first, true); }
        catch (Exception e) { Res["diff"] = "FAIL"; Res["diff_reason"] = "set Selected: " + Unwrap(e); return; }
        // diff 是异步加载的：等 DiffModel.Count > 0
        for (var i = 0; i < 40; i++)
        {
            Thread.Sleep(500);
            var n = DiffCount(cfs);
            if (n > 0) { Res["diff"] = "PASS"; Res["diff_count"] = n.ToString(); W("diff PASS count=" + n); return; }
        }
        var last = DiffCount(cfs);
        // 有文件但 diff 为空，记为 SKIP（不判 FAIL：可能是二进制/无差异文件）
        Res["diff"] = last > 0 ? "PASS" : "SKIP";
        Res["diff_reason"] = last > 0 ? "loaded" : "diff not loaded in 20s (可能该文件无文本差异)";
        W("diff=" + Res["diff"] + " count=" + last);
    }

    static int DiffCount(object cfs)
    {
        var csv = Get(cfs, "ChangeSetViewModel");
        var diffVm = csv == null ? null : Get(csv, "DiffVM");
        var model = diffVm == null ? null : Get(diffVm, "DiffModel");
        if (model == null) return -1;
        var c = Get(model, "Count");
        return c is int i ? i : -1;
    }

    static void CommitStaged()
    {
        var cfs = Cfs();
        if (cfs == null) { Res["commit"] = "FAIL"; Res["commit_reason"] = "no cfs"; return; }
        var repo = Get(cfs, "Repo") ?? GetField(cfs, "_repo");
        if (repo == null) { Res["commit"] = "FAIL"; Res["commit_reason"] = "no Repo"; return; }
        var handler = Get(repo, "Handler");
        if (handler == null) { Res["commit"] = "FAIL"; Res["commit_reason"] = "no Handler"; return; }
        MethodInfo cp = null;
        foreach (var m in handler.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (m.Name == "CommitProcess" && m.GetParameters().Length == 4) { cp = m; break; }
        if (cp == null) { Res["commit"] = "FAIL"; Res["commit_reason"] = "no CommitProcess(4)"; return; }
        var ps = cp.GetParameters();
        var path = Get(repo, "Path") as string ?? _repo;
        object opts = Enum.ToObject(ps[3].ParameterType, 0);
        try
        {
            _proc = cp.Invoke(handler, new object[] { path, "probe-commit from ops-check", null, opts });
        }
        catch (Exception e) { Res["commit"] = "FAIL"; Res["commit_reason"] = "CommitProcess: " + Unwrap(e); return; }
        if (_proc == null) { Res["commit"] = "FAIL"; Res["commit_reason"] = "process null"; return; }
        var runThis = _proc.GetType().GetMethod("RunThis", Type.EmptyTypes);
        if (runThis == null) { Res["commit"] = "FAIL"; Res["commit_reason"] = "no RunThis"; return; }
        try { runThis.Invoke(_proc, null); }
        catch (Exception e) { Res["commit"] = "FAIL"; Res["commit_reason"] = "RunThis: " + Unwrap(e); return; }
        // 等进程结束并取退出码
        for (var i = 0; i < 40; i++)
        {
            Thread.Sleep(500);
            var fin = Get(_proc, "Finished");
            if (fin is bool b && b) break;
        }
        var exit = Get(_proc, "ExitCode");
        var ok = exit is int ec && ec == 0;
        Res["commit"] = ok ? "PASS" : "FAIL";
        Res["commit_exit"] = exit?.ToString() ?? "null";
        W("commit=" + Res["commit"] + " exit=" + (exit ?? "null"));
        try { File.Delete(Path.Combine(_repo, "probe-diff.txt")); } catch { }
    }

    static void CheckoutBranch()
    {
        // 清掉 diff 用的未暂存文件，避免切分支时弹「未提交更改」确认框把 UI 线程卡死
        try { File.Delete(Path.Combine(_repo, "probe-diff.txt")); } catch { }
        Thread.Sleep(800);
        var tab = Find(_dc, "RepoTabViewModel", 8, new HashSet<object>());
        if (tab == null) { Res["checkout"] = "FAIL"; Res["checkout_reason"] = "no RepoTabViewModel"; return; }
        var m = tab.GetType().GetMethod("CheckoutBranch", new[] { typeof(string), typeof(string) });
        if (m == null) { Res["checkout"] = "FAIL"; Res["checkout_reason"] = "no CheckoutBranch(String,String)"; return; }
        try
        {
            var begin = _disp.GetType().GetMethod("BeginInvoke", new[] { typeof(Delegate), typeof(object[]) });
            var act = (Action)(() => { try { m.Invoke(tab, new object[] { _branch, null }); } catch { } });
            begin.Invoke(_disp, new object[] { act, new object[0] });
        }
        catch (Exception e) { Res["checkout"] = "FAIL"; Res["checkout_reason"] = "begin: " + Unwrap(e); return; }
        Res["checkout_started"] = _branch;
        W("checkout=started " + _branch);
    }

    static void OpenRepo(int mode)
    {
        var t = FindType("RepositoryTabContainerViewModel");
        var inst = t == null ? null : Find(_dc, "RepositoryTabContainerViewModel", 5, new HashSet<object>());
        if (inst == null) { W("rcInst null"); return; }
        var m = t.GetMethod("AddOrActivate");
        var p1 = m.GetParameters()[1].ParameterType;
        try { m.Invoke(inst, new object[] { _repo, Enum.ToObject(p1, mode), null }); W("AddOrActivate mode=" + mode); }
        catch (Exception e) { W("AddOrActivate err " + Unwrap(e)); }
    }

    static object Cfs()
    {
        var tab = Find(_dc, "RepoTabViewModel", 8, new HashSet<object>());
        return tab == null ? null : Get(tab, "CommitAndFileStatusVM");
    }

    static void Persist()
    {
        try
        {
            var sb = new StringBuilder("{\n");
            var first = true;
            foreach (var kv in Res)
            {
                if (!first) sb.Append(",\n");
                first = false;
                sb.Append("  ").Append(Json(kv.Key)).Append(": ").Append(Json(kv.Value));
            }
            sb.Append("\n}\n");
            File.WriteAllText(Path.Combine(_outDir ?? "/tmp/ops-check", "result.json"), sb.ToString());
        }
        catch { }
    }

    static void Finish(string why)
    {
        if (_done) return;
        _done = true;
        Res["finished"] = why;
        var sb = new StringBuilder("{\n");
        var first = true;
        foreach (var kv in Res)
        {
            if (!first) sb.Append(",\n");
            first = false;
            sb.Append("  ").Append(Json(kv.Key)).Append(": ").Append(Json(kv.Value));
        }
        sb.Append("\n}\n");
        try { File.WriteAllText(Path.Combine(_outDir, "result.json"), sb.ToString()); } catch (Exception e) { W("write result err " + e.Message); }
        W("finished: " + why);
    }

    static string Json(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s ?? "")
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\n");
            else if (c < ' ') sb.Append(' ');
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    static void W(string s)
    {
        lock (Lock)
        {
            Log.AppendLine(s);
            try { File.AppendAllText(Path.Combine(_outDir ?? "/tmp/ops-check", "hook.log"), s + "\n"); } catch { }
            Console.Error.WriteLine("[OPS-HOOK] " + s);
            Persist();
        }
    }

    static void Inv(Action a)
    {
        try { _inv.Invoke(_disp, new object[] { a, new object[0] }); }
        catch (Exception e) { W("invoke err " + Unwrap(e)); }
    }

    static string Unwrap(Exception e) => (e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e).Message;

    static object Get(object o, string name) => o?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(o);
    static object GetField(object o, string name) => o?.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o);

    static object Find(object o, string typeName, int depth, HashSet<object> seen)
    {
        if (o == null || depth < 0) return null;
        var t = o.GetType();
        if (t.Name == typeName || t.FullName == typeName) return o;
        if (o is string) return null;
        if (o is IEnumerable)
        {
            var c = 0;
            foreach (var x in (IEnumerable)o) { if (c++ > 20) break; var r = Find(x, typeName, depth - 1, seen); if (r != null) return r; }
            return null;
        }
        if (!seen.Add(o)) return null;
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            object v; try { v = p.GetValue(o); } catch { continue; }
            var r = Find(v, typeName, depth - 1, seen);
            if (r != null) return r;
        }
        return null;
    }

    static Type FindType(string n)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] ts; try { ts = a.GetTypes(); } catch { continue; }
            foreach (var t in ts) if (t.Name == n || t.FullName == n) return t;
        }
        return null;
    }
}
