using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// applier：给 SourceTree.Api.UI.Wpf 的 LogLabelControl 补 Brush 初始化（K-1427 / K-1428）
//
// 为什么需要（K-1427）：
//   History 面板首次渲染时，`LogLabelControl.OnRender` 先读字段 `_backgroundBrush`（`IL_0009 ldfld`）
//   再 `callvirt get_Color()`（`IL_000f`），而给它赋值的 `stfld _backgroundBrush` 在 `IL_0030`
//   ⇒ 首帧读到 null ⇒ NullReferenceException（栈顶 `LogLabelControl.OnRender` ← `UIElement.Arrange`
//   ← `ContentPresenter.ArrangeOverride`）⇒ 切到 History 视图即崩、面板出不来。
//
//   全 DLL 扫描的既有事实：`_backgroundBrush` 只有 `OnRender` 自身 1 处 `stfld`；
//   `_borderPen` / `_backgroundGreyBrush` 全程序集 0 处 `stfld`；`UpdateBrushes()` 是空方法
//   （`IL_0000: ret`）而 `.ctor` 恰恰调它 ⇒ brushes 从未初始化（上游 bug，或依赖 Windows 上才走的路径）。
//
// 修法：在 `LogLabelControl::.ctor` 的**每个** `ret` 之前插入 5 条指令
//   ldarg.0; ldarg.0; call get_Background(); newobj SolidColorBrush(Color); stfld _backgroundBrush
//   （栈平衡：this → this → Color → Brush → 消费 this+Brush，与原方法返回值无关）
//
// 幂等：逐 `ret` 比对「紧邻 5 条指令」是否已是指纹序列；全部已插则输出 ALREADY_PATCHED 并原样退出。
//
// 用法：loglabel-brush <SourceTree.Api.UI.Wpf.dll> <out.dll> <appDir> [--fx <PresentationCore 目录>]
//   退出码：0 成功（或已 patch）；2 参数错；3 找不到类型/字段/方法；4 前置形态不符（拒绝生成非法 IL）
class LogLabelBrush
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
            Console.Error.WriteLine("用法: loglabel-brush <dll> <out.dll> <appDir> [--fx <PresentationCore 目录>]");
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

        var ll = mod.Types.SelectMany(Flat).FirstOrDefault(x => x.Name == "LogLabelControl" && x.Namespace == "SourceTree.Controls");
        if (ll == null) { Console.Error.WriteLine("[LLB] 找不到 SourceTree.Controls.LogLabelControl"); return 3; }

        var fld = ll.Fields.FirstOrDefault(x => x.Name == "_backgroundBrush");
        if (fld == null) { Console.Error.WriteLine("[LLB] 找不到字段 _backgroundBrush"); return 3; }

        var getBg = ll.Methods.FirstOrDefault(x => x.Name == "get_Background" && x.Parameters.Count == 0);
        if (getBg == null) { Console.Error.WriteLine("[LLB] 找不到 get_Background()"); return 3; }

        // 前置形态校验：get_Background 必须返回 System.Windows.Media.Color（否则 newobj SolidColorBrush(Color) 非法）
        if (getBg.ReturnType.Name != "Color")
        {
            Console.Error.WriteLine($"[LLB] get_Background 返回 {getBg.ReturnType.FullName}，不是 Color ⇒ 拒绝生成非法 IL");
            return 4;
        }

        var pc = AssemblyDefinition.ReadAssembly(Path.Combine(fx, "PresentationCore.dll"), new ReaderParameters { AssemblyResolver = resolver });
        var scb = pc.MainModule.Types.SelectMany(Flat).FirstOrDefault(x => x.Name == "SolidColorBrush" && x.Namespace == "System.Windows.Media");
        if (scb == null) { Console.Error.WriteLine("[LLB] PresentationCore 里找不到 System.Windows.Media.SolidColorBrush"); return 3; }
        var scbCtor = scb.Methods.FirstOrDefault(x => x.IsConstructor && x.Parameters.Count == 1 && x.Parameters[0].ParameterType.Name == "Color");
        if (scbCtor == null) { Console.Error.WriteLine("[LLB] 找不到 SolidColorBrush(Color) 构造函数"); return 3; }

        var fldR = mod.ImportReference(fld);
        var getBgR = mod.ImportReference(getBg);
        var scbCtorR = mod.ImportReference(scbCtor);

        // 只 patch 实例构造：静态构造（.cctor）没有 this，ldarg.0 会生成非法 IL（K-1427 也只处理实例 ctor）
        var ctors = ll.Methods.Where(x => x.IsConstructor && !x.IsStatic).ToList();
        if (ctors.Count == 0) { Console.Error.WriteLine("[LLB] LogLabelControl 没有构造函数"); return 3; }

        Console.WriteLine($"[LLB] type={ll.FullName} ctors={ctors.Count} field={fld.Name} getBg={getBg.ReturnType.Name}");

        if (verifyOnly)
        {
            int total = 0, hit = 0;
            foreach (var ctor in ctors)
                foreach (var ret in ctor.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret))
                {
                    total++;
                    if (HasFingerprintBefore(ret)) hit++;
                }
            Console.WriteLine($"[LLB] VERIFY={(hit == total && total > 0 ? "PATCHED" : "CLEAN")} rets={total} fingerprinted={hit}");
            // 打印实例 ctor 尾部 6 条（含指纹 5 条），作为"指令确实在"的机读证据
            var c0 = ctors.FirstOrDefault(x => !x.IsStatic);
            if (c0 != null)
            {
                var tail = c0.Body.Instructions.Reverse().Take(6).Reverse().ToList();
                foreach (var ins in tail)
                    Console.WriteLine($"[LLB] IL {ins}");
            }
            return 0;
        }

        int patchedIns = 0, patchedRets = 0, alreadyRets = 0;

        foreach (var ctor in ctors)
        {
            var body = ctor.Body;
            var rets = body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();
            var il = body.GetILProcessor();
            bool anyInserted = false;

            foreach (var ret in rets)
            {
                if (HasFingerprintBefore(ret)) { alreadyRets++; continue; }

                il.InsertBefore(ret, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(ret, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(ret, il.Create(OpCodes.Call, getBgR));
                il.InsertBefore(ret, il.Create(OpCodes.Newobj, scbCtorR));
                il.InsertBefore(ret, il.Create(OpCodes.Stfld, fldR));
                patchedRets++; patchedIns += 5; anyInserted = true;
            }
            Console.WriteLine($"[LLB] ctor={ctor.Name} rets={rets.Count} inserted={(anyInserted ? "yes" : "no")}");
        }

        if (patchedIns == 0)
        {
            Console.WriteLine($"[LLB] STATUS=ALREADY_PATCHED already_rets={alreadyRets}");
            return 0;
        }

        asm.Write(outp);
        Console.WriteLine($"[LLB] STATUS=PATCHED patched_rets={patchedRets} patched_ins={patchedIns} already_rets={alreadyRets}");
        Console.WriteLine($"[LLB] wrote={outp} size={new FileInfo(outp).Length}");
        return 0;

        // 指纹：ret 前 5 条是否恰为 ldarg.0 / ldarg.0 / call get_Background / newobj SolidColorBrush(Color) / stfld _backgroundBrush
        bool HasFingerprintBefore(Instruction ret)
        {
            var prev = new List<Instruction>();
            for (var p = ret.Previous; p != null && prev.Count < 5; p = p.Previous) prev.Insert(0, p);
            if (prev.Count < 5) return false;
            return prev[0].OpCode == OpCodes.Ldarg_0
                && prev[1].OpCode == OpCodes.Ldarg_0
                && prev[2].OpCode == OpCodes.Call
                && prev[2].Operand is MethodReference m1 && m1.Name == "get_Background"
                && prev[3].OpCode == OpCodes.Newobj
                && prev[3].Operand is MethodReference m2 && m2.Name == ".ctor" && m2.DeclaringType.Name == "SolidColorBrush"
                && prev[4].OpCode == OpCodes.Stfld
                && prev[4].Operand is FieldReference f1 && f1.Name == "_backgroundBrush";
        }
    }
}
