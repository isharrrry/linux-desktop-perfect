using Mono.Cecil;
using Mono.Cecil.Cil;

// pm0148-patcher <in.dll> <out.dll> <typeFullName> <methodName> [insert|replace]
// 在指定方法入口插 ret（或把首指令替换为 ret），用于停掉 GongSolutions 拖源入口。
internal static class P
{
    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (var t in types)
        {
            yield return t;
            foreach (var n in AllTypes(t.NestedTypes)) yield return n;
        }
    }

    private static int Main(string[] a)
    {
        if (a.Length < 4)
        {
            Console.WriteLine("usage: pm0148-patcher <in.dll> <out.dll> <typeFullName> <methodName> [insert|replace]");
            return 2;
        }
        string inDll = a[0], outDll = a[1], typeName = a[2], methodName = a[3];
        string mode = a.Length > 4 ? a[4] : "insert";

        var asm = AssemblyDefinition.ReadAssembly(inDll);
        var mod = asm.MainModule;
        var type = AllTypes(mod.Types).FirstOrDefault(t => t.FullName == typeName);
        if (type == null) { Console.WriteLine("TYPE_MISSING " + typeName); return 3; }
        var methods = type.Methods.Where(m => m.Name == methodName).ToList();
        if (methods.Count != 1) { Console.WriteLine("METHOD_COUNT " + methods.Count); return 4; }
        var m = methods[0];
        if (!m.HasBody) { Console.WriteLine("NO_BODY"); return 5; }

        int before = m.Body.Instructions.Count;
        Console.WriteLine("BEFORE first=" + m.Body.Instructions[0].OpCode + " il=" + before
            + " handlers=" + m.Body.ExceptionHandlers.Count + " maxstack=" + m.Body.MaxStackSize);

        if (mode == "replace")
        {
            m.Body.Instructions[0] = Instruction.Create(OpCodes.Ret);
        }
        else
        {
            m.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Ret));
        }

        // 写出去后重新读回校验，防止写坏了自欺
        asm.Write(outDll);
        var chkAsm = AssemblyDefinition.ReadAssembly(outDll);
        var chkType = AllTypes(chkAsm.MainModule.Types).First(t => t.FullName == typeName);
        var chk = chkType.Methods.First(x => x.Name == methodName);
        Console.WriteLine("AFTER  first=" + chk.Body.Instructions[0].OpCode
            + " second=" + (chk.Body.Instructions.Count > 1 ? chk.Body.Instructions[1].OpCode.ToString() : "-")
            + " il=" + chk.Body.Instructions.Count
            + " handlers=" + chk.Body.ExceptionHandlers.Count);
        return chk.Body.Instructions[0].OpCode == OpCodes.Ret ? 0 : 6;
    }
}
