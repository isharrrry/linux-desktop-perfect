// applier：把「确认提交」的 yes/no 确认框改成恒返回 true，避免提交被确认框阻塞（K-1456）。
//
// 为什么需要：
//   `ConfirmCommitCommand.Execute` → `_FinaliseCommit` → `CreateStagingViewCommitProcess`
//   → `NotificationsManager.ShowNotificationDialogWithYesConfirmation(...)`。
//   Linux 上该对话框（`NotificationDialogWindow`）构造/等待路径会阻塞 `Execute`
//   ⇒ 提交偶发卡住（K-1455 记录「不加补丁时偶发」）。
//
// 修法：`SourceTree.Notifications.NotificationsManager::ShowNotificationDialogWithYesConfirmation`
//   的**两个重载**方法体都替换为 `ldc.i4.1; ret`（恒 true）。
//   代价：不再弹「确认提交？」对话框；提交本身照常完成
//   （K-1456 实测：`ConfirmCommitCommand.Execute` 正常返回、`git log` 出现新提交 `3938e39`/`7159dbe`、3/3）。
//
// 为什么必须 applier：`SourceTree.Api.UI.Wpf.dll` 重装/重派生后手工 patch 会丢
//   ⇒ 提交回到偶发阻塞。本 applier 与 loglabel-brush / commit-minreq / seticon-ret 同构、幂等。
//
// 与另三个补丁的关系（四个补丁改同一个 DLL）：
//   顺序必须是 loglabel-brush → commit-minreq → seticon-ret → commit-noconfirm。
//   Program.cs 在 patch 前后都读回前三个补丁的指纹，任一缺席则以退出码 5 拒绝。
//
// 用法：
//   APP=/tmp/stpkg/SourceTree/app-3.4.15 sh 40-build/appliers/commit-noconfirm/apply.sh --check
//   APP=/tmp/stpkg/SourceTree/app-3.4.15 sh 40-build/appliers/commit-noconfirm/apply.sh
//
// 幂等判据：两个重载的方法体都已是 `ldc.i4.1; ret` ⇒ STATUS=ALREADY_PATCHED，不重写。
// 备份：首次执行会 `cp -a` 到 `<dll>.pre-commit-noconfirm.bak`（不动前三个补丁的备份）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class CommitNoConfirm
{
    const string DialogType = "SourceTree.Notifications.NotificationsManager";
    const string DialogMethod = "ShowNotificationDialogWithYesConfirmation";

    static IEnumerable<TypeDefinition> Flat(TypeDefinition t)
    {
        yield return t;
        foreach (var n in t.NestedTypes) foreach (var x in Flat(n)) yield return x;
    }

    static int Main(string[] argv)
    {
        if (argv.Length < 3)
        {
            Console.Error.WriteLine("用法: commit-noconfirm <dll> <out.dll> <appDir> [--fx <PresentationCore 目录>] [--verify]");
            return 2;
        }
        string src = argv[0], outp = argv[1], app = argv[2];
        string fx = Environment.GetEnvironmentVariable("FX")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                    ".local/share/ldp/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.11");
        bool verifyOnly = false;
        for (int i = 3; i < argv.Length; i++)
        {
            if (argv[i] == "--fx" && i + 1 < argv.Length) fx = argv[i + 1];
            if (argv[i] == "--verify") verifyOnly = true;
        }

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(src)));
        resolver.AddSearchDirectory(app);
        resolver.AddSearchDirectory(fx);

        var asm = AssemblyDefinition.ReadAssembly(src, new ReaderParameters { AssemblyResolver = resolver });
        var types = asm.MainModule.Types.SelectMany(Flat).ToList();

        var t = types.FirstOrDefault(x => x.FullName == DialogType);
        if (t == null) { Console.Error.WriteLine($"[CN] 找不到 {DialogType}"); return 3; }

        var targets = t.Methods.Where(m => m.Name == DialogMethod).ToList();
        if (targets.Count == 0) { Console.Error.WriteLine($"[CN] 找不到 {DialogMethod}"); return 3; }
        foreach (var m in targets)
            Console.WriteLine($"[CN] target {m.Name}({string.Join(",", m.Parameters.Select(p => p.ParameterType.Name))}) ins={m.Body?.Instructions.Count ?? -1} ret={m.ReturnType.Name}");

        bool AllPatched(IEnumerable<MethodDefinition> ms) => ms.All(m => m.HasBody
            && m.Body.Instructions.Count == 2
            && m.Body.Instructions[0].OpCode == OpCodes.Ldc_I4_1
            && m.Body.Instructions[1].OpCode == OpCodes.Ret);

        bool looksPatched = AllPatched(targets);

        var brOk = CheckBrush(types);
        var minOk = CheckMinReq(types);
        var siOk = CheckSetIcon(types);
        Console.WriteLine($"[CN] brush={(brOk ? "present" : "absent")} minreq={(minOk ? "present" : "absent")} seticon={(siOk ? "present" : "absent")}");

        if (verifyOnly)
        {
            Console.WriteLine($"[CN] VERIFY={(looksPatched ? "PATCHED" : "CLEAN")} overloads={targets.Count}");
            return looksPatched ? 0 : 1;
        }

        if (!brOk)
        {
            Console.Error.WriteLine("[CN] 基线里没有 loglabel-brush 指纹 ⇒ 先跑 40-build/appliers/loglabel-brush/apply.sh（顺序：brush → minreq → seticon → noconfirm）");
            return 5;
        }
        if (!minOk)
        {
            Console.Error.WriteLine("[CN] 基线里没有 commit-minreq 指纹（IL_000d 不是 ldc.i4.0）⇒ 先跑 40-build/appliers/commit-minreq/apply.sh");
            return 5;
        }
        if (!siOk)
        {
            Console.Error.WriteLine("[CN] 基线里没有 seticon 指纹（SetIcon 不是单条 ret）⇒ 先跑 40-build/appliers/seticon-ret/apply.sh");
            return 5;
        }

        if (looksPatched)
        {
            Console.WriteLine("[CN] STATUS=ALREADY_PATCHED（两个重载都已是 ldc.i4.1; ret，幂等跳过）");
            File.Copy(src, outp, true);
            return 0;
        }

        // 前置形态校验：每个重载都必须有方法体且指令数 > 2，否则是别的版本/结构
        foreach (var m in targets)
        {
            if (!m.HasBody) { Console.Error.WriteLine($"[CN] {m.Name}({m.Parameters.Count}) 没有方法体 ⇒ 拒绝改"); return 4; }
            if (m.Body.Instructions.Count <= 2)
            {
                Console.Error.WriteLine($"[CN] {m.Name}({m.Parameters.Count}) 指令数={m.Body.Instructions.Count}，期望 >2 ⇒ 前置形态不符，拒绝改");
                return 4;
            }
        }

        int n = 0;
        foreach (var m in targets)
        {
            var proc = m.Body.GetILProcessor();
            m.Body.Instructions.Clear();
            m.Body.ExceptionHandlers.Clear();
            m.Body.Variables.Clear();
            proc.Append(proc.Create(OpCodes.Ldc_I4_1));
            proc.Append(proc.Create(OpCodes.Ret));
            n++;
        }

        asm.Write(outp);
        Console.WriteLine($"[CN] STATUS=PATCHED overloads={n} wrote={outp} size={new FileInfo(outp).Length}");

        // 写回后复核：前三个补丁的指纹必须仍在（防顺序错误把它们吃掉）
        var reasm = AssemblyDefinition.ReadAssembly(outp, new ReaderParameters { AssemblyResolver = resolver });
        var reTypes = reasm.MainModule.Types.SelectMany(Flat).ToList();
        bool postBr = CheckBrush(reTypes), postMin = CheckMinReq(reTypes), postSi = CheckSetIcon(reTypes);
        Console.WriteLine($"[CN] post_write brush={(postBr ? "present" : "absent")} minreq={(postMin ? "present" : "absent")} seticon={(postSi ? "present" : "absent")}");
        if (!postBr || !postMin || !postSi)
        {
            Console.Error.WriteLine("[CN] 写回后前三个补丁指纹缺失 ⇒ 产物不可用");
            return 6;
        }
        return 0;
    }

    // 指纹 1：LogLabelControl::.ctor 的 ret 前 5 条是否为 brush 补丁
    static bool CheckBrush(List<TypeDefinition> ts)
    {
        var ll = ts.FirstOrDefault(x => x.Name == "LogLabelControl" && x.Namespace == "SourceTree.Controls");
        if (ll == null) return false;
        foreach (var ctor in ll.Methods.Where(x => x.IsConstructor && !x.IsStatic))
        {
            if (!ctor.HasBody) continue;
            foreach (var ret in ctor.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret))
            {
                var prev = new List<Instruction>();
                for (var p = ret.Previous; p != null && prev.Count < 5; p = p.Previous) prev.Insert(0, p);
                if (prev.Count == 5
                    && prev[0].OpCode == OpCodes.Ldarg_0 && prev[1].OpCode == OpCodes.Ldarg_0
                    && prev[2].OpCode == OpCodes.Call && prev[2].Operand is MethodReference m0 && m0.Name == "get_Background"
                    && prev[3].OpCode == OpCodes.Newobj && prev[3].Operand is MethodReference m1 && m1.DeclaringType.Name == "SolidColorBrush"
                    && prev[4].OpCode == OpCodes.Stfld && prev[4].Operand is FieldReference f0 && f0.Name == "_backgroundBrush")
                    return true;
            }
        }
        return false;
    }

    // 指纹 2：_CheckMinimumRequirementsBeforeEnterCommit 的 IL_000d 是否为 ldc.i4.0
    static bool CheckMinReq(List<TypeDefinition> ts)
    {
        var cf = ts.FirstOrDefault(x => x.Name == "CommitAndFileStatusViewModel");
        if (cf == null) return false;
        var mm = cf.Methods.FirstOrDefault(x => x.Name == "_CheckMinimumRequirementsBeforeEnterCommit");
        if (mm == null || !mm.HasBody) return false;
        var at = mm.Body.Instructions.FirstOrDefault(x => x.Offset == 0x0d);
        return at != null && at.OpCode == OpCodes.Ldc_I4_0;
    }

    // 指纹 3：NotificationDialogWindow::SetIcon 是否为单条 ret
    static bool CheckSetIcon(List<TypeDefinition> ts)
    {
        var win = ts.FirstOrDefault(x => x.FullName == "SourceTree.View.Dialogs.NotificationDialogWindow");
        if (win == null) return false;
        var m = win.Methods.FirstOrDefault(x => x.Name == "SetIcon" && x.Parameters.Count == 1);
        return m != null && m.HasBody && m.Body.Instructions.Count <= 2
               && m.Body.Instructions.FirstOrDefault()?.OpCode == OpCodes.Ret;
    }
}
