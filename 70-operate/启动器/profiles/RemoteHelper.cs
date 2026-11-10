// 远程操作（fetch/push/pull）的程序化骨架 —— 对应《程序化操作清单》第 ⑥ 条（K-1466 / K-1467）。
// 与 Helper.cs 同级：把 RemoteInjected.Run(window) 插到 MainWindow_Loaded 开头即可。
// 前置：① Dvcss 配置 + cmd\git.exe（K-1413/K-1415）② AddOrActivate ③ ActivatedTab；
//       fetch 还需要先用 CustomActionsPatch.cs 打补丁旁路「自定义动作」对话框。
using System;
using System.Collections;
using System.IO;
using System.Reflection;

public static class RemoteInjected {
    static readonly BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.FlattenHierarchy;
    static object _tab;
    static int _tick;

    static void Log(string s) { try { File.AppendAllText("/tmp/remote/remote.log", s + "\n"); } catch { } }
    static string N(object o) => o == null ? "null" : o.GetType().FullName;

    static object Prop(object o, string name) {
        try { var p = o?.GetType().GetProperty(name, F); return p == null ? null : p.GetValue(o, null); } catch { return null; }
    }
    static void PropSet(object o, string name, object v) {
        try { o?.GetType().GetProperty(name, F)?.SetValue(o, v, null); }
        catch (Exception e) { Log($"[RM] set {name} EX {e.GetType().Name} :: {e.InnerException?.Message ?? e.Message}"); }
    }
    static object Call(object o, string name, params object[] a) {
        try { var m = o?.GetType().GetMethod(name, F); return m == null ? null : m.Invoke(o, a); }
        catch (Exception e) { Log($"[RM] call {name} EX {e.GetType().Name} :: {e.InnerException?.Message ?? e.Message}"); return null; }
    }
    static int CollCount(object c) => c is ICollection col ? col.Count : -1;

    // ⑥-2 push：RemoteNameList / SelectedRemoteIndex / Branches(LocalBranch,RemoteBranch,Push)
    public static void FillPush(object vm) {
        var names = Prop(vm, "RemoteNameList");
        names?.GetType().GetMethod("Add")?.Invoke(names, new object[] { "origin" });
        PropSet(vm, "SelectedRemoteIndex", 0);
        var branches = Prop(vm, "Branches");
        if (branches != null) {
            var et = branches.GetType().GetGenericArguments()[0];
            var e = Activator.CreateInstance(et);
            et.GetProperty("LocalBranch")?.SetValue(e, "master", null);
            et.GetProperty("RemoteBranch")?.SetValue(e, "master", null);
            et.GetProperty("Push")?.SetValue(e, true, null);
            branches.GetType().GetMethod("Add")?.Invoke(branches, new[] { e });
        }
        Log($"[RM] fillPush names={CollCount(names)} idx={Prop(vm, "SelectedRemoteIndex")} branches={CollCount(branches)}");
    }

    // ⑥-3 pull：RemoteList / SelectedRemote / RemoteBranchList / SelectedRemoteBranch
    public static void FillPull(object vm) {
        var rl = Prop(vm, "RemoteList");
        rl?.GetType().GetMethod("Add")?.Invoke(rl, new object[] { "origin" });
        PropSet(vm, "SelectedRemote", "origin");
        var bl = Prop(vm, "RemoteBranchList");
        bl?.GetType().GetMethod("Add")?.Invoke(bl, new object[] { "master" });
        PropSet(vm, "SelectedRemoteBranch", "master");
        Log($"[RM] fillPull rl={CollCount(rl)} bl={CollCount(bl)} sel={Prop(vm, "SelectedRemote")}/{Prop(vm, "SelectedRemoteBranch")}");
    }

    // 三步：宿主方法 → 下一 tick 取 DialogContainer 里的 DialogVM → 填集合 → Confirm
    // REMOTE_MODE = fetch | push | pull
    public static void OnTick(object tab) {
        _tab = tab;
        var mode = Environment.GetEnvironmentVariable("REMOTE_MODE") ?? "fetch";
        _tick++;
        Log($"[RM] tick{_tick} mode={mode}");
        if (_tick == 3) {
            var host = mode == "pull" ? "Pull" : mode == "push" ? "Push" : "Fetch";
            Log($"[RM] call {host}()");
            Call(tab, host);
            Log($"[RM] {host}() returned");
        } else if (_tick == 4 && mode != "fetch") {
            var container = Prop(tab, "DialogContainer");
            var vm = Prop(container, mode == "push" ? "PushDialogVM" : "PullDialogVM")
                     ?? Prop(container, mode == "push" ? "GitPushDialogViewModel" : "GitPullDialogViewModel");
            Log($"[RM] dialogVM={N(vm)}");
            if (vm != null) {
                if (mode == "push") FillPush(vm); else FillPull(vm);
                Call(vm, "Confirm");
                Log("[RM] Confirm() called");
            }
        }
    }
}
