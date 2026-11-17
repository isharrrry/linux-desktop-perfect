using System;
using System.Linq;
using System.Reflection;
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

        // 【PM-0133】shim 的 WaitForMultipleObjectsEx 是假桩：
        //   `native-shim/src/win32_misc.c:867` 无条件 `return WAIT_TIMEOUT`（最多睡 50ms），
        //   而 WPF 的 `NonPumpingSynchronizationContext` / `DispatcherSynchronizationContext`
        //   会把 CLR 的阻塞等待转交给它；CLR 拿到「立即返回且非失败」的结果就当等待结束
        //   ⇒ 线程实际没拿到 `ReaderWriterLockSlim` 的锁，随后 Exit*Lock 抛
        //   `SynchronizationLockException: The read lock is being released without being held.`
        //   （PM-0133，unhandled → abort）。
        //   Linux 上 WaitHandle 是 CLR WaitSubsystem 的托管句柄，C shim 无法真等待，
        //   所以修法只能是托管真等待：两处 Wait 重写都不许再落 Win32 桩。
        //   IL 形状（已用 pm0133waitfix + 真 DLL A/B 验过：改前 59ms/抛异常，改后 304ms/ok）：
        //     NonPumpingSynchronizationContext::Wait
        //         ldarg.0; ldarg.1; ldarg.2; ldarg.3; call SynchronizationContext::Wait(...); ret
        //     DispatcherSynchronizationContext::Wait
        //         ldarg.1; ldarg.2; ldarg.3; call SynchronizationContext::WaitHelper(...); ret
        int waitPatched = 0;
        var npcWait = FindNpcWait(mod);
        if (npcWait != null)
        {
            RewriteWait(npcWait, ScMethod(mod, "Wait", hasThis: true), hasThis: true);
            Console.WriteLine($"  patched: {npcWait.DeclaringType.FullName}::{npcWait.Name} -> SynchronizationContext::Wait");
            waitPatched++;
        }
        var dscWait = FindDscWait(mod);
        if (dscWait != null)
        {
            RewriteWait(dscWait, ScMethod(mod, "WaitHelper", hasThis: false), hasThis: false);
            Console.WriteLine($"  patched: {dscWait.DeclaringType.FullName}::{dscWait.Name} -> SynchronizationContext::WaitHelper");
            waitPatched++;
        }
        if (waitPatched != 2)
        {
            Console.Error.WriteLine($"PM-0133 等待修复只命中 {waitPatched}/2 处，拒绝写出");
            return 5;
        }
        if (patched == 0) { Console.Error.WriteLine("没有可打补丁的方法"); return 4; }

        asm.Write(argv[1]);
        Console.WriteLine($"OK 写出 {argv[1]}");

        // 产出件形状自检：重建 WindowsBase 后，这两行必须看不到 WaitForMultipleObjectsEx。
        // 任一变成 `call UnsafeNativeMethods::WaitForMultipleObjectsEx` 就是回归。
        var check = AssemblyDefinition.ReadAssembly(argv[1]).MainModule;
        Console.WriteLine("VERIFY NonPumpingSynchronizationContext::Wait il=[" + IlOf(FindNpcWait(check)) + "]");
        Console.WriteLine("VERIFY DispatcherSynchronizationContext::Wait il=[" + IlOf(FindDscWait(check)) + "]");
        return 0;
    }

    // ---- PM-0133 helpers ----

    // System.Runtime 作用域下的 SynchronizationContext 方法引用。
    // 【平台 21:47 实测修正】原实现手搓 `new TypeReference(..., mod, mod.TypeSystem.CoreLibrary)`
    //   会把作用域写成 **System.Runtime 门面**，JIT 时报
    //   `TypeLoadException: Could not load type 'System.Threading.SynchronizationContext'
    //    from assembly 'System.Runtime'`（注入件在 get_ReadLock 处直接抛）。
    //   改为 `ImportReference(反射得到的 MethodInfo)`：Cecil 会写到真正的实现程序集
    //   （System.Private.CoreLib），实测可用。
    static MethodReference ScMethod(ModuleDefinition mod, string name, bool hasThis)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Instance | BindingFlags.Static;
        MethodInfo mi = typeof(System.Threading.SynchronizationContext).GetMethod(
            name, flags, null,
            new[] { typeof(IntPtr[]), typeof(bool), typeof(int) }, null);
        if (mi == null)
            throw new InvalidOperationException("找不到 SynchronizationContext." + name);
        return mod.ImportReference(mi);
    }

    static void RewriteWait(MethodDefinition m, MethodReference target, bool hasThis)
    {
        var il = m.Body.GetILProcessor();
        m.Body.Instructions.Clear();
        m.Body.Variables.Clear();
        m.Body.ExceptionHandlers.Clear();
        m.Body.MaxStackSize = 5;
        m.Body.InitLocals = false;
        if (hasThis) il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldarg_1));
        il.Append(il.Create(OpCodes.Ldarg_2));
        il.Append(il.Create(OpCodes.Ldarg_3));
        il.Append(il.Create(OpCodes.Call, target));
        il.Append(il.Create(OpCodes.Ret));
    }

    static MethodDefinition FindNpcWait(ModuleDefinition mod)
    {
        var rw = mod.GetType("MS.Internal.ReaderWriterLockWrapper");
        var npc = rw == null ? null : rw.NestedTypes.FirstOrDefault(x => x.Name == "NonPumpingSynchronizationContext");
        return npc == null ? null : npc.Methods.FirstOrDefault(x => x.Name == "Wait" && x.Parameters.Count == 3);
    }

    static MethodDefinition FindDscWait(ModuleDefinition mod)
    {
        var dsc = mod.GetType("System.Windows.Threading.DispatcherSynchronizationContext");
        return dsc == null ? null : dsc.Methods.FirstOrDefault(x => x.Name == "Wait" && x.Parameters.Count == 3);
    }

    static string IlOf(MethodDefinition m)
    {
        if (m == null) return "MISSING";
        return string.Join(" ; ", m.Body.Instructions.Select(i =>
            i.OpCode + (i.Operand is MethodReference mr ? " " + mr.DeclaringType.Name + "::" + mr.Name : "")));
    }
}
