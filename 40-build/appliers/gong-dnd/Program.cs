// gongfix —— patch GongSolutions.Wpf.DragDrop.dll 的 .cctor，让 DataFormats.GetDataFormat 不抛异常（K-1131 修复）
using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class P {
  static void Main(string[] a) {
    var asm = AssemblyDefinition.ReadAssembly(a[0]);
    var t = asm.MainModule.Types.SelectMany(Flat).First(x => x.Name == "DragDrop" && x.Namespace == "GongSolutions.Wpf.DragDrop");
    var cc = t.Methods.First(m => m.Name == ".cctor");
    var ins = cc.Body.Instructions;
    int patched = 0;
    for (int i = 0; i < ins.Count; i++) {
      var op = ins[i];
      if (op.OpCode == OpCodes.Call && op.Operand is MethodReference mr && mr.Name == "GetDataFormat") {
        var il = cc.Body.GetILProcessor();
        // call → pop（丢弃 ldstr 的字符串），然后压 null
        op.OpCode = OpCodes.Pop;
        op.Operand = null;
        il.InsertAfter(op, il.Create(OpCodes.Ldnull));
        patched++;
        Console.WriteLine($"patched index {i}: call GetDataFormat -> pop + ldnull");
      }
    }
    if (patched == 0) { Console.WriteLine("未找到 GetDataFormat 调用"); return; }
    asm.Write(a[1]);
    Console.WriteLine($"已写出 {a[1]}（{patched} 处）");
  }
  static System.Collections.Generic.IEnumerable<TypeDefinition> Flat(TypeDefinition t) {
    yield return t;
    foreach (var n in t.NestedTypes) foreach (var x in Flat(n)) yield return x;
  }
}
