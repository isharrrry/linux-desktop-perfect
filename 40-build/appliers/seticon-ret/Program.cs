// applier：把「确认提交」对话框的 SetIcon 改成空实现，避免 Icon.FromHandle 在 Linux 崩溃（K-1432）。
//
// 为什么需要：
//   点「提交」后 `ConfirmCommitCommand.Execute` → `_FinaliseCommit` →
//   `CreateStagingViewCommitProcess` → `NotificationsManager.ShowNotificationDialogWithYesConfirmation`
//   → `NotificationDialogWindow..ctor` → `SetIcon(VistaTaskDialogIcon)`
//   → `DialogIconUtil.GetDialogIcon` → `System.Drawing.Icon.FromHandle(IntPtr)`，
//   而后者在 Linux 上抛 `System.ArgumentException: handle` ⇒ 提交在「确认提交」对话框
//   构造处中断，`git log` 不产生新提交（K-1431）。
//
// 修法：把 `SourceTree.View.Dialogs.NotificationDialogWindow::SetIcon` 的方法体
//   整体替换为单条 `ret`（void 方法，栈无需平衡）⇒ 对话框不再设图标，
//   但确认流程本身照常走，提交能真正完成（K-1432 实测 `git log` 产生新提交）。
//   代价：确认对话框不显示图标（功能不受影响）。
//
// 为什么必须 applier：`SourceTree.Api.UI.Wpf.dll` 一旦被重新部署/重装，手工 patch 会丢
//   ⇒ 提交再次崩在 Icon.FromHandle。本 applier 与 loglabel-brush / commit-minreq 同构、幂等。
//
// 与另两个补丁的关系（三者改同一个 DLL）：
//   应用顺序必须是 loglabel-brush → commit-minreq → seticon-ret。
//   本程序在 patch 前后都读回前两个补丁的指纹，缺席则以退出码 5 拒绝，防止在错基线上产出不可用 DLL。
//
// 用法：
//   APP=<app 目录> sh 40-build/appliers/seticon-ret/apply.sh --check
//   APP=<app 目录> sh 40-build/appliers/seticon-ret/apply.sh
//
// 幂等判据：`SetIcon` 方法体已是单条 `ret` ⇒ STATUS=ALREADY_PATCHED，不重写。
// 前置形态校验：改前要求方法体指令数 > 1（否则说明是别的版本/已改坏），不符则以退出码 4 拒绝。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class SetIconRet
{
    static IEnumerable<TypeDefinition> Flat(TypeDefinition t)
    {
        yield return t;
        foreach (var n in t.NestedTypes) foreach (var x in Flat(n)) yield return x;
    }

    static int Main(string[] argv)
    {
        if (argv.Length < 3)
        {
            Console.Error.WriteLine("用法: seticon-ret <dll> <out.dll> <appDir> [--fx <PresentationCore 目录>] [--verify]");
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
        var mod = asm.MainModule;
        var types = mod.Types.SelectMany(Flat).ToList();

        var t = types.FirstOrDefault(x => x.FullName == "SourceTree.View.Dialogs.NotificationDialogWindow");
        if (t == null) { Console.Error.WriteLine("[SI] 找不到 SourceTree.View.Dialogs.NotificationDialogWindow"); return 3; }
        var m = t.Methods.FirstOrDefault(x => x.Name == "SetIcon" && x.Parameters.Count == 1);
        if (m == null) { Console.Error.WriteLine("[SI] 找不到 SetIcon(VistaTaskDialogIcon)"); return 3; }
        if (!m.HasBody) { Console.Error.WriteLine("[SI] SetIcon 没有方法体"); return 3; }

        var il = m.Body.Instructions;
        var first = il.FirstOrDefault();
        bool looksPatched = il.Count <= 2 && first != null && first.OpCode == OpCodes.Ret;
        Console.WriteLine($"[SI] type={t.FullName} method={m.Name} ret={m.ReturnType.Name} ins={il.Count} first={first}");

        bool brushOk = CheckBrush(types);
        bool minreqOk = CheckMinReq(types);
        Console.WriteLine($"[SI] brush={(brushOk ? "present" : "absent")} minreq={(minreqOk ? "present" : "absent")}");

        if (verifyOnly)
        {
            Console.WriteLine($"[SI] VERIFY={(looksPatched ? "PATCHED" : "CLEAN")}");
            foreach (var ins in il.Take(6)) Console.WriteLine($"[SI] IL {ins}");
            return looksPatched ? 0 : 1;
        }

        if (!brushOk)
        {
            Console.Error.WriteLine("[SI] 基线里没有 loglabel-brush 指纹 ⇒ 先跑 40-build/appliers/loglabel-brush/apply.sh（顺序：brush → minreq → seticon）");
            return 5;
        }
        if (!minreqOk)
        {
            Console.Error.WriteLine("[SI] 基线里没有 commit-minreq 指纹（IL_000d 不是 ldc.i4.0）⇒ 先跑 40-build/appliers/commit-minreq/apply.sh（顺序：brush → minreq → seticon）");
            return 5;
        }

        if (looksPatched)
        {
            Console.WriteLine("[SI] STATUS=ALREADY_PATCHED（SetIcon 已是单条 ret，幂等跳过）");
            File.Copy(src, outp, true);
            return 0;
        }

        // 前置形态校验：方法体必须有多条指令（K-1431 记录为 30 条），否则是别的版本/结构
        if (il.Count <= 1)
        {
            Console.Error.WriteLine($"[SI] SetIcon 指令数={il.Count}，期望 >1 ⇒ 前置形态不符，拒绝改");
            return 4;
        }

        var proc = m.Body.GetILProcessor();
        m.Body.Instructions.Clear();
        m.Body.ExceptionHandlers.Clear();
        m.Body.Variables.Clear();
        proc.Append(proc.Create(OpCodes.Ret));

        asm.Write(outp);
        Console.WriteLine($"[SI] STATUS=PATCHED ins=1 wrote={outp} size={new FileInfo(outp).Length}");

        // 写回后复核：前两个补丁的指纹必须仍在（防顺序错误把它们吃掉）
        var reasm = AssemblyDefinition.ReadAssembly(outp, new ReaderParameters { AssemblyResolver = resolver });
        var reTypes = reasm.MainModule.Types.SelectMany(Flat).ToList();
        bool postBrush = CheckBrush(reTypes), postMin = CheckMinReq(reTypes);
        Console.WriteLine($"[SI] post_write brush={(postBrush ? "present" : "absent")} minreq={(postMin ? "present" : "absent")}");
        if (!postBrush || !postMin)
        {
            Console.Error.WriteLine("[SI] 写回后前两个补丁指纹缺失 ⇒ 产物不可用");
            return 6;
        }
        return 0;

        // 指纹 1：LogLabelControl::.ctor 的 ret 前 5 条是否为 brush 补丁
        bool CheckBrush(List<TypeDefinition> ts)
        {
            var ll = ts.FirstOrDefault(x => x.Name == "LogLabelControl" && x.Namespace == "SourceTree.Controls");
            if (ll == null) return false;
            foreach (var ctor in ll.Methods.Where(x => x.IsConstructor && !x.IsStatic))
            {
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
        bool CheckMinReq(List<TypeDefinition> ts)
        {
            var cf = ts.FirstOrDefault(x => x.Name == "CommitAndFileStatusViewModel");
            if (cf == null) return false;
            var mm = cf.Methods.FirstOrDefault(x => x.Name == "_CheckMinimumRequirementsBeforeEnterCommit");
            if (mm == null || !mm.HasBody) return false;
            var at = mm.Body.Instructions.FirstOrDefault(x => x.Offset == 0x0d);
            return at != null && at.OpCode == OpCodes.Ldc_I4_0;
        }
    }
}
