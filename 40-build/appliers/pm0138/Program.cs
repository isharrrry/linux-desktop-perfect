using Mono.Cecil;
using Mono.Cecil.Cil;

// pm0138-patcher <in.dll> <out.dll>
// 在 WorkingCopySplitFileListContainerView 的 .ctor 末尾插入：
//   Pm0138.Hook.Apply(this);
// 真正设 MinHeight/RowDefinition.MinHeight 的逻辑在 Pm0138Fix.dll（C#，可 jitverify）。
internal static class P
{
    private const string TypeName = "SourceTree.View.Container.WorkingCopySplitFileListContainerView";
    private const string HookAsm = "Pm0138Fix";
    private const string HookNs = "Pm0138";
    private const string HookType = "Hook";
    private const string HookMethod = "Apply";

    private static int Main(string[] a)
    {
        if (a.Length < 2) { Console.WriteLine("usage: pm0138-patcher <in.dll> <out.dll>"); return 2; }
        var asm = AssemblyDefinition.ReadAssembly(a[0]);
        var mod = asm.MainModule;
        var type = AllTypes(mod.Types).FirstOrDefault(t => t.FullName == TypeName);
        if (type == null) { Console.WriteLine("TYPE_MISSING " + TypeName); return 3; }
        var ctor = type.Methods.FirstOrDefault(m => m.Name == ".ctor" && m.Parameters.Count == 0);
        if (ctor == null) { Console.WriteLine("CTOR_MISSING"); return 4; }

        var asmRef = mod.AssemblyReferences.FirstOrDefault(r => r.Name == HookAsm)
                     ?? new AssemblyNameReference(HookAsm, new Version(1, 0, 0, 0));
        if (!mod.AssemblyReferences.Contains(asmRef)) mod.AssemblyReferences.Add(asmRef);

        var hookTypeRef = new TypeReference(HookNs, HookType, mod, asmRef);
        var apply = new MethodReference(HookMethod, mod.TypeSystem.Void, hookTypeRef) { HasThis = false };
        apply.Parameters.Add(new ParameterDefinition("view", ParameterAttributes.None, mod.TypeSystem.Object));

        var il = ctor.Body.Instructions;
        int insertAt = il.Count - 1;
        while (insertAt > 0 && il[insertAt].OpCode != OpCodes.Ret) insertAt--;
        Console.WriteLine("BEFORE il=" + il.Count + " insertAt=" + insertAt + " op=" + il[insertAt].OpCode);

        il.Insert(insertAt++, Instruction.Create(OpCodes.Ldarg_0));
        il.Insert(insertAt++, Instruction.Create(OpCodes.Call, apply));

        asm.Write(a[1]);

        var chkAsm = AssemblyDefinition.ReadAssembly(a[1]);
        var chkMod = chkAsm.MainModule;
        var chk = AllTypes(chkMod.Types).First(t => t.FullName == TypeName)
            .Methods.First(m => m.Name == ".ctor" && m.Parameters.Count == 0);
        int hookCalls = chk.Body.Instructions.Count(i => i.OpCode == OpCodes.Call
            && i.Operand is MethodReference mr && mr.Name == HookMethod);
        bool refOk = chkMod.AssemblyReferences.Any(r => r.Name == HookAsm);
        Console.WriteLine("AFTER il=" + chk.Body.Instructions.Count + " hook_calls=" + hookCalls + " asmref=" + refOk);
        return hookCalls == 1 && refOk ? 0 : 7;
    }

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (var t in types)
        {
            yield return t;
            foreach (var n in AllTypes(t.NestedTypes)) yield return n;
        }
    }
}
