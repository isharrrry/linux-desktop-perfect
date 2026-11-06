using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class P {
  static void Main(string[] a) {
    var asm = AssemblyDefinition.ReadAssembly(a[0]);
    var t = asm.MainModule.Types.First(x => x.FullName == "SourceTree.MainWindow");
    var m = t.Methods.First(x => x.Name == "OnContentRendered");
    var pf = AssemblyDefinition.ReadAssembly(a[2]);
    var pc = AssemblyDefinition.ReadAssembly(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(a[2]), "PresentationCore.dll"));
    var cc = pf.MainModule.Types.SelectMany(Flat).First(x => x.Name == "ContentControl" && x.Namespace == "System.Windows.Controls");
    var getContentRef = asm.MainModule.ImportReference(cc.Methods.First(x => x.Name == "get_Content"));
    var uiElement = pc.MainModule.Types.SelectMany(Flat).First(x => x.Name == "UIElement" && x.Namespace == "System.Windows");
    var uiElementRef = asm.MainModule.ImportReference(uiElement);
    var vth = pc.MainModule.Types.SelectMany(Flat).First(x => x.Name == "VisualTreeHelper");
    var getCountRef = asm.MainModule.ImportReference(vth.Methods.First(x => x.Name == "GetChildrenCount"));
    var getChildRef = asm.MainModule.ImportReference(vth.Methods.First(x => x.Name == "GetChild" && x.Parameters.Count == 2));
    var uap = pc.MainModule.Types.SelectMany(Flat).FirstOrDefault(x => x.Name == "UIElementAutomationPeer")
           ?? pf.MainModule.Types.SelectMany(Flat).First(x => x.Name == "UIElementAutomationPeer");
    var createPeerRef = asm.MainModule.ImportReference(uap.Methods.First(x => x.Name == "CreatePeerForElement"));
    var peerType = pc.MainModule.Types.SelectMany(Flat).First(x => x.Name == "AutomationPeer" && x.Namespace == "System.Windows.Automation.Peers");
    var peerTypeRef = asm.MainModule.ImportReference(peerType);
    var getPeerNameRef = asm.MainModule.ImportReference(peerType.Methods.First(x => x.Name == "GetName"));
    var getPeerTypeRef2 = asm.MainModule.ImportReference(peerType.Methods.First(x => x.Name == "GetAutomationControlType"));
    var wlO = asm.MainModule.ImportReference(typeof(Console).GetMethod("WriteLine", new[]{typeof(object)}));
    var wlS = asm.MainModule.ImportReference(typeof(Console).GetMethod("WriteLine", new[]{typeof(string)}));
    var objGetType = asm.MainModule.ImportReference(typeof(object).GetMethod("GetType"));
    var typeName = asm.MainModule.ImportReference(typeof(Type).GetProperty("FullName").GetGetMethod());
    var dv = new TypeDefinition("UiaProbe", "DumpHelper", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, asm.MainModule.TypeSystem.Object);
    asm.MainModule.Types.Add(dv);
    var dump = new MethodDefinition("Dump", MethodAttributes.Public | MethodAttributes.Static, asm.MainModule.TypeSystem.Void);
    dump.Parameters.Add(new ParameterDefinition(uiElementRef));
    dv.Methods.Add(dump);
    var il = dump.Body.GetILProcessor();
    dump.Body.InitLocals = true;
    var vCount = new VariableDefinition(asm.MainModule.TypeSystem.Int32); dump.Body.Variables.Add(vCount);
    var vI = new VariableDefinition(asm.MainModule.TypeSystem.Int32); dump.Body.Variables.Add(vI);
    var vChild = new VariableDefinition(uiElementRef); dump.Body.Variables.Add(vChild);
    var vPeer = new VariableDefinition(peerTypeRef); dump.Body.Variables.Add(vPeer);
    void A(Instruction i) => il.Append(i);
    var bodyStart = il.Create(OpCodes.Ldstr, "[LOOP] i=");
    var incrStart = il.Create(OpCodes.Ldloc, vI);
    var condStart = il.Create(OpCodes.Ldloc, vI);
    A(il.Create(OpCodes.Ldarg_0));
    A(il.Create(OpCodes.Call, getCountRef));
    A(il.Create(OpCodes.Stloc, vCount));
    A(il.Create(OpCodes.Ldstr, "[DUMP] count="));
    A(il.Create(OpCodes.Call, wlS));
    A(il.Create(OpCodes.Ldloc, vCount));
    A(il.Create(OpCodes.Box, asm.MainModule.TypeSystem.Int32));
    A(il.Create(OpCodes.Call, wlO));
    A(il.Create(OpCodes.Ldc_I4_0));
    A(il.Create(OpCodes.Stloc, vI));
    A(il.Create(OpCodes.Br, condStart));
    /* body */
    A(bodyStart);
    A(il.Create(OpCodes.Call, wlS));
    A(il.Create(OpCodes.Ldloc, vI));
    A(il.Create(OpCodes.Box, asm.MainModule.TypeSystem.Int32));
    A(il.Create(OpCodes.Call, wlO));
    A(il.Create(OpCodes.Ldarg_0));
    A(il.Create(OpCodes.Ldloc, vI));
    A(il.Create(OpCodes.Call, getChildRef));
    A(il.Create(OpCodes.Isinst, uiElementRef));
    A(il.Create(OpCodes.Stloc, vChild));
    A(il.Create(OpCodes.Ldloc, vChild));
    A(il.Create(OpCodes.Brfalse, incrStart));
    A(il.Create(OpCodes.Ldstr, "[LOOP] child-type="));
    A(il.Create(OpCodes.Call, wlS));
    A(il.Create(OpCodes.Ldloc, vChild));
    A(il.Create(OpCodes.Callvirt, objGetType));
    A(il.Create(OpCodes.Callvirt, typeName));
    A(il.Create(OpCodes.Call, wlS));
    A(il.Create(OpCodes.Ldloc, vChild));
    A(il.Create(OpCodes.Call, createPeerRef));
    A(il.Create(OpCodes.Stloc, vPeer));
    A(il.Create(OpCodes.Ldloc, vPeer));
    A(il.Create(OpCodes.Brfalse, incrStart));
    A(il.Create(OpCodes.Ldstr, "[LOOP] peer="));
    A(il.Create(OpCodes.Call, wlS));
    A(il.Create(OpCodes.Ldloc, vPeer));
    A(il.Create(OpCodes.Call, wlO));
    /* incr */
    A(incrStart);
    A(il.Create(OpCodes.Ldc_I4_1));
    A(il.Create(OpCodes.Add));
    A(il.Create(OpCodes.Stloc, vI));
    /* cond */
    A(condStart);
    A(il.Create(OpCodes.Ldloc, vCount));
    A(il.Create(OpCodes.Blt, bodyStart));
    A(il.Create(OpCodes.Ret));
    /* 调用 */
    var il2 = m.Body.GetILProcessor();
    var first2 = m.Body.Instructions[0];
    il2.InsertBefore(first2, il2.Create(OpCodes.Ldstr, "[CP] ENTER"));
    il2.InsertBefore(first2, il2.Create(OpCodes.Call, wlS));
    il2.InsertBefore(first2, il2.Create(OpCodes.Ldarg_0));
    il2.InsertBefore(first2, il2.Create(OpCodes.Callvirt, getContentRef));
    il2.InsertBefore(first2, il2.Create(OpCodes.Castclass, uiElementRef));
    il2.InsertBefore(first2, il2.Create(OpCodes.Call, dump));
    il2.InsertBefore(first2, il2.Create(OpCodes.Ldstr, "[CP] EXIT"));
    il2.InsertBefore(first2, il2.Create(OpCodes.Call, wlS));
    asm.Write(a[1]);
    Console.WriteLine("patched -> " + a[1]);
  }
  static System.Collections.Generic.IEnumerable<TypeDefinition> Flat(TypeDefinition t) {
    yield return t;
    foreach (var n in t.NestedTypes) foreach (var x in Flat(n)) yield return x;
  }
}
