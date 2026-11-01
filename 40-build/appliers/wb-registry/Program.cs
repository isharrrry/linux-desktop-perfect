using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class FixWb
{
    static int Main(string[] argv)
    {
        if (argv.Length < 2) { Console.Error.WriteLine("用法: fixwb <WindowsBase.dll> <out.dll>"); return 2; }
        var asm = AssemblyDefinition.ReadAssembly(argv[0]);
        var mod = asm.MainModule;
        var t = mod.GetType("System.Windows.BaseCompatibilityPreferences");
        if (t == null) { Console.Error.WriteLine("找不到 BaseCompatibilityPreferences"); return 3; }

        // 【K-351】这个方法的真因：
        //   `Registry.CurrentUser` 在 Linux 上返回 null，而方法体直接对它调 `OpenSubKey`
        //   ⇒ JIT 的 null check 解引用 null ⇒ SIGSEGV。
        //   它的作用只是"从注册表读一个兼容性开关"，读不到就用默认值即可
        //   ⇒ 把方法体改成单条 `ret`（等价于"走默认路径"）。
        int patched = 0;
        foreach (var name in new[] {
            "SetMatchPackageSignatureMethodToPackagePartDigestMethodFromRegistry",
            "SetHandleDispatcherRequestProcessingFailureFromRegistry" })   // 另一个同类方法（如果有）
        {
            var m = t.Methods.FirstOrDefault(x => x.Name == name);
            if (m == null) continue;
            if (!m.HasBody) continue;
            var il = m.Body.GetILProcessor();
            m.Body.Instructions.Clear();
            m.Body.Variables.Clear();
            m.Body.ExceptionHandlers.Clear();
            il.Append(il.Create(OpCodes.Ret));
            Console.WriteLine($"  patched: {name}");
            patched++;
        }
        if (patched == 0) { Console.Error.WriteLine("没有可打补丁的方法"); return 4; }

        asm.Write(argv[1]);
        Console.WriteLine($"OK 写出 {argv[1]}");
        return 0;
    }
}
