// applier：让 DirectWrite.Linux.Provider 的 `FontTableStripper.TryStripTables` 直接返回 false。
//
// 为什么需要（K-386/K-387/K-388/K-389）：
//   Ink 样本在 Linux 上因 `memset(0, 140TB)` 崩溃。追查到：
//     GC bump-pointer 分配器（libcoreclr）← FontTableStripper::StripTables ← FontLayoutStripping::Apply
//   `StripTables` 在算字体表大小时出错，`new byte[140TB]` ⇒ memset 越界。
//
//   而 `TryStripTables` 的设计恰好是安全的：
//     IL_0000 ldarg.1 ; IL_0001 ldarg.0 ; IL_0002 stind.ref   ⇒ ***dest = source（先赋值！）**
//     所以任何 `return false` 的分支都保证 `*dest` 是原字体数据。
//   调用方 `FontLayoutStripping::Apply` 在 false 时只做 `Interlocked.Increment(计数器)` + 设 out 参数，
//   然后返回 `dest`（= source）—— **完全安全**。
//
// 本补丁：在 `IL_0002`（stind.ref）之后插入 `*error = "skipped-by-patch"; return false;`
using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class P {
    static int Main(string[] a) {
        if (a.Length < 2) { Console.Error.WriteLine("用法: dw-fontstrip <DirectWrite.Linux.Provider.dll> <out.dll>"); return 2; }
        var asm = AssemblyDefinition.ReadAssembly(a[0]);
        int patched = 0;
        foreach (var t in asm.MainModule.GetTypes()) {
            if (!t.Name.Contains("FontTableStripper")) continue;
            foreach (var m in t.Methods) {
                if (m.Name != "TryStripTables" || !m.HasBody) continue;
                var il = m.Body.Instructions;
                // 找 IL_0002 的 stind.ref（第一次 stind.ref，即 *dest = source）
                var firstStind = il.FirstOrDefault(x => x.OpCode == OpCodes.Stind_Ref);
                if (firstStind == null) { Console.Error.WriteLine("没找到 stind.ref"); return 1; }
                var ip = m.Body.GetILProcessor();
                // 在 stind.ref 之后插入：*error = "skipped-by-patch"; return false;
                var after = firstStind;
                var ins = new[] {
                    Instruction.Create(OpCodes.Ldarg_2),                       // error
                    Instruction.Create(OpCodes.Ldstr, "skipped-by-patch"),
                    Instruction.Create(OpCodes.Stind_Ref),                     // *error = "..."
                    Instruction.Create(OpCodes.Ldc_I4_0),                      // false
                    Instruction.Create(OpCodes.Ret),
                };
                foreach (var i in ins) { ip.InsertAfter(after, i); after = i; }
                Console.WriteLine($"patched: {t.FullName}::{m.Name}  (在 IL_0002 stind.ref 后插入 *error + return false)");
                ++patched;
            }
        }
        if (patched == 0) { Console.Error.WriteLine("没打上补丁"); return 1; }
        asm.Write(a[1]);
        Console.WriteLine($"OK 写出 {a[1]}");
        return 0;
    }
}
