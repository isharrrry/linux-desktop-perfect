// 远程操作 ⑥-1 的探针前置：旁路 RepoTabViewModel.Fetch/Pull/Push 的「自定义动作」对话框。
// 用法：
//   dotnet ca-patch.dll <SourceTree.Api.UI.Wpf.dll>            # 打补丁（幂等）
//   dotnet ca-patch.dll <SourceTree.Api.UI.Wpf.dll> --verify   # 只校验
// 输出：PATCHED <method> / ALREADY <method> / MISS / NO_PUSHDIALOG / VM_NOT_FOUND。
//
// 三个必踩的坑（K-1466）：
//   1) DialogVM.Confirm 是 Family(protected) ⇒ 跨方法 callvirt 抛 MethodAccessException，需改 public（位掩码下述）；
//   2) Cecil asm.Write 必须写新文件再 File.Move(..., true)：直接写回同一路径会被截成 0 字节；
//   3) ReaderParameters 必须带 AssemblyResolver 并 AddSearchDirectory(dll 目录)，否则解析基类失败。
using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class CustomActionsPatch {
    static int Main(string[] a) {
        if (a.Length == 0) { Console.Error.WriteLine("usage: ca-patch <dll> [--verify]"); return 2; }
        var dll = a[0];
        var verifyOnly = a.Contains("--verify");
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(System.IO.Path.GetDirectoryName(dll));
        var asm = AssemblyDefinition.ReadAssembly(dll, new ReaderParameters { AssemblyResolver = resolver });
        var tab = asm.MainModule.Types.First(x => x.Name == "RepoTabViewModel");

        // 每个宿主方法要确认的 DialogVM 类型（fetch/pull/push 各有 Git/Hg 两套）
        var plan = new (string host, string[] vms)[] {
            ("Fetch", new[] { "SourceTree.ViewModel.Git.GitFetchDialogViewModel" }),
            ("Pull",  new[] { "SourceTree.ViewModel.Git.GitPullDialogViewModel", "SourceTree.ViewModel.Hg.HgPullDialogViewModel" }),
            ("Push",  new[] { "SourceTree.ViewModel.Git.GitPushDialogViewModel", "SourceTree.ViewModel.Hg.HgPushDialogViewModel" }),
        };

        var patched = 0;
        foreach (var (host, vms) in plan) {
            var m = tab.Methods.FirstOrDefault(x => x.Name == host && x.HasBody);
            if (m == null) { Console.WriteLine($"MISS {host}"); continue; }
            var il = m.Body.GetILProcessor();
            var pushDialog = m.Body.Instructions.FirstOrDefault(i => i.OpCode == OpCodes.Callvirt &&
                (i.Operand as MethodReference)?.Name == "PushDialog");
            if (pushDialog == null) { Console.WriteLine($"NO_PUSHDIALOG {host}"); continue; }
            // 幂等：PushDialog 之后已经是 ldloc.0 + isinst 说明打过
            if (pushDialog.Next?.OpCode == OpCodes.Ldloc_0 && pushDialog.Next.Next?.OpCode == OpCodes.Isinst) {
                Console.WriteLine($"ALREADY {host}"); continue;
            }
            if (verifyOnly) { Console.WriteLine($"CLEAN {host} pushAt={pushDialog.Offset:X4}"); continue; }
            foreach (var vmName in vms) {
                var vm = asm.MainModule.Types.FirstOrDefault(x => x.FullName == vmName);
                if (vm == null) { Console.WriteLine($"VM_NOT_FOUND {vmName}"); continue; }
                var confirm = vm.Methods.FirstOrDefault(x => x.Name == "Confirm" && x.Parameters.Count == 0);
                if (confirm == null) { Console.WriteLine($"NO_CONFIRM {vmName}"); continue; }
                // 坑 1：Family(protected) → public
                if (!confirm.IsPublic)
                    confirm.Attributes = (confirm.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public;
                var skip = Instruction.Create(OpCodes.Nop);
                var anchor = pushDialog.Next;
                il.InsertBefore(anchor, Instruction.Create(OpCodes.Ldloc_0));
                il.InsertBefore(anchor, Instruction.Create(OpCodes.Isinst, vm));
                il.InsertBefore(anchor, Instruction.Create(OpCodes.Brfalse, skip));
                il.InsertBefore(anchor, Instruction.Create(OpCodes.Ldloc_0));
                il.InsertBefore(anchor, Instruction.Create(OpCodes.Castclass, vm));
                il.InsertBefore(anchor, Instruction.Create(OpCodes.Callvirt, confirm));
                il.InsertBefore(anchor, skip);
            }
            patched++;
            Console.WriteLine($"PATCHED {host} pushAt={pushDialog.Offset:X4} vms={vms.Length}");
        }

        if (!verifyOnly && patched > 0) {
            // 坑 2：写 .new 再 Move，避免 0 字节
            var tmp = dll + ".new";
            asm.Write(tmp);
            System.IO.File.Move(tmp, dll, true);
            Console.WriteLine($"WROTE {dll} patched={patched}");
        }
        return patched > 0 || verifyOnly ? 0 : 1;
    }
}
