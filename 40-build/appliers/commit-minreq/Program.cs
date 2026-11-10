using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// applier：跳过「提交」前的用户信息对话框（K-1430 / K-1412）
//
// 为什么需要（K-1404 → K-1412 → K-1430）：
//   点「提交」会走 `ConfirmCommitCommand.Execute` → `ConfirmCommit` →
//   `CommitAndFileStatusViewModel._CheckMinimumRequirementsBeforeEnterCommit` →
//   `AccountManager.GetDefaultUserInformation` → `WindowsIdentity.GetCurrent()`，
//   而 Linux 上 `WindowsIdentity` 抛 `PlatformNotSupportedException: Windows Principal
//   functionality is not supported on this platform.` ⇒ 进程 abort，提交永远做不成。
//
//   该方法的结构（IL 前 7 条）：
//     IL_0002 call   get_DefaultAuthorDesc
//     IL_0007 callvirt String::IsNullOrEmpty
//     IL_000c stloc.0
//     IL_000d ldloc.0
//     IL_000e brfalse IL_00ce     ; 非空 ⇒ 后面 ldc.i4.1 返回 true
//     ...
//     IL_00ce ldc.i4.1
//     IL_00cf ret
//   ⇒ 把 `IL_000d ldloc.0` 换成 `ldc.i4.0`（两条都是 1 字节、无操作数）⇒ brfalse 恒跳
//   `IL_00ce: ldc.i4.1` ⇒ 方法恒返回 true，整段「弹用户信息对话框」的路径被跳过，
//   `WindowsIdentity.GetCurrent()` 不再被调用。指令数与栈平衡都不变（K-1305 口径最稳）。
//
// 实测（K-1430）：补丁后 `ConfirmCommitCommand.Execute` 真实产生提交
//   （`/tmp/stgit` HEAD 9a74d4a → 61df0d9 "P1 test commit"、`git status` 变空、
//    `PlatformNotSupportedException` 计数 0、界面 History 顶部出现该提交）。
//
// 为什么要 applier（不能只手工 patch）：`SourceTree.Api.UI.Wpf.dll` 若被重新部署/重装，
//   手工 patch 会丢 ⇒ 提交再次崩溃。本 applier 与 loglabel-brush / gong-dnd / wb-registry 同构、幂等。
//
// 与 loglabel-brush 的关系（两者改同一个 DLL）：
//   顺序为「先 loglabel-brush 后 commit-minreq」。本程序在 patch 前/后都会读回 brush 的指纹
//   （`LogLabelControl::.ctor` 的 ret 前 5 条）并打印 `[MR] brush=present|absent`，
//   缺席时以退出码 5 拒绝（避免在错基线上打出不可用的 DLL）。
//
// 用法：commit-minreq <SourceTree.Api.UI.Wpf.dll> <out.dll> <appDir> [--fx <PresentationCore 目录>] [--verify]
//   退出码：0 成功（或已 patch）；2 参数错；3 找不到类型/方法；4 前置形态不符（拒绝生成非法 IL）；
//           5 与 loglabel-brush 共存校验失败（brush 指纹缺席）
class CommitMinReq
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
            Console.Error.WriteLine("用法: commit-minreq <dll> <out.dll> <appDir> [--fx <PresentationCore 目录>] [--verify]");
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

        var t = types.FirstOrDefault(x => x.Name == "CommitAndFileStatusViewModel");
        if (t == null) { Console.Error.WriteLine("[MR] 找不到 CommitAndFileStatusViewModel"); return 3; }

        var m = t.Methods.FirstOrDefault(x => x.Name == "_CheckMinimumRequirementsBeforeEnterCommit");
        if (m == null) { Console.Error.WriteLine("[MR] 找不到 _CheckMinimumRequirementsBeforeEnterCommit"); return 3; }
        if (!m.HasBody) { Console.Error.WriteLine("[MR] 该方法没有方法体"); return 3; }

        var il = m.Body.Instructions;
        Console.WriteLine($"[MR] type={t.FullName} method={m.Name} ret={m.ReturnType.Name} ins={il.Count}");

        // 定位目标指令：期望是第一条 ldloc.0（K-1412/K-1430 记录为 IL_000d）
        int idx = -1;
        for (int i = 0; i < il.Count; i++)
        {
            if (il[i].OpCode == OpCodes.Ldloc_0) { idx = i; break; }
        }

        // 幂等判定：若第一条 ldloc.0 已是 ldc.i4.0（即整条链上第一条「非 ldloc.0 的 int 常量」），
        // 则按指纹判 PATCHED：IL_000d 位置上的指令已是 ldc.i4.0
        var at000d = il.FirstOrDefault(x => x.Offset == 0x0d);
        bool looksPatched = at000d != null && at000d.OpCode == OpCodes.Ldc_I4_0;

        // 共存校验：loglabel-brush 的指纹（LogLabelControl::.ctor 的 ret 前 5 条）
        string brushState = CheckBrush(types) ? "present" : "absent";
        Console.WriteLine($"[MR] brush={brushState}");

        if (verifyOnly)
        {
            Console.WriteLine($"[MR] VERIFY={(looksPatched ? "PATCHED" : "CLEAN")} target={(at000d == null ? "absent" : at000d.ToString())}");
            foreach (var ins in il.Take(8))
                Console.WriteLine($"[MR] IL {ins}");
            return looksPatched ? 0 : 1;
        }

        if (brushState == "absent")
        {
            Console.Error.WriteLine("[MR] 基线里没有 loglabel-brush 指纹 ⇒ 请先跑 40-build/appliers/loglabel-brush/apply.sh（顺序：brush → minreq）");
            return 5;
        }

        if (looksPatched)
        {
            Console.WriteLine("[MR] STATUS=ALREADY_PATCHED（IL_000d 已是 ldc.i4.0，幂等跳过）");
            foreach (var ins in il.Take(8))
                Console.WriteLine($"[MR] IL {ins}");
            return 0;
        }

        // 前置形态校验：必须是 IL_000d ldloc.0 + IL_000e brfalse，
        // 否则说明是别的版本/别的结构，拒绝改（避免把方法改坏）
        if (idx < 0) { Console.Error.WriteLine("[MR] 找不到 ldloc.0 ⇒ 前置形态不符"); return 4; }
        var target = il[idx];
        if (target.Offset != 0x0d)
        {
            Console.Error.WriteLine($"[MR] 第一条 ldloc.0 在 IL_{target.Offset:x4}，期望 IL_000d ⇒ 前置形态不符");
            return 4;
        }
        var next = target.Next;
        if (next == null || next.OpCode != OpCodes.Brfalse && next.OpCode != OpCodes.Brfalse_S)
        {
            Console.Error.WriteLine($"[MR] IL_000d 的下一条不是 brfalse（实为 {next}）⇒ 前置形态不符");
            return 4;
        }

        Console.WriteLine($"[MR] before: [{idx}] IL_{target.Offset:x4} {target.OpCode} {target.Operand}");
        // 替换：ldloc.0 → ldc.i4.0（两者都是 1 字节、无操作数 ⇒ 指令数与偏移不变）
        target.OpCode = OpCodes.Ldc_I4_0;
        target.Operand = null;
        Console.WriteLine($"[MR] after : [{idx}] IL_{target.Offset:x4} {target.OpCode} {target.Operand}");

        asm.Write(outp);
        Console.WriteLine($"[MR] STATUS=PATCHED wrote={outp} size={new FileInfo(outp).Length}");

        // 写回后复核：brush 指纹仍在（防顺序错误把 brush 补丁吃掉）
        var reasm = AssemblyDefinition.ReadAssembly(outp, new ReaderParameters { AssemblyResolver = resolver });
        var reTypes = reasm.MainModule.Types.SelectMany(Flat).ToList();
        Console.WriteLine($"[MR] post_write brush={(CheckBrush(reTypes) ? "present" : "absent")}");
        return 0;

        // 指纹：LogLabelControl::.ctor 的 ret 前 5 条是否为 brush 补丁
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
                    if (prev.Count < 5) continue;
                    if (prev[0].OpCode == OpCodes.Ldarg_0 && prev[1].OpCode == OpCodes.Ldarg_0
                        && prev[2].OpCode == OpCodes.Call && prev[2].Operand is MethodReference m1 && m1.Name == "get_Background"
                        && prev[3].OpCode == OpCodes.Newobj && prev[3].Operand is MethodReference m2 && m2.Name == ".ctor" && m2.DeclaringType.Name == "SolidColorBrush"
                        && prev[4].OpCode == OpCodes.Stfld && prev[4].Operand is FieldReference f1 && f1.Name == "_backgroundBrush")
                        return true;
                }
            }
            return false;
        }
    }
}
