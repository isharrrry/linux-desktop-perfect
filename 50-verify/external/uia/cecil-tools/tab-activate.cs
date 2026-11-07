using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class P {
  static void Main(string[] a) {
    var asm = AssemblyDefinition.ReadAssembly(a[0]);
    var mw = asm.MainModule.Types.First(x => x.FullName == "SourceTree.MainWindow");
    var m = mw.Methods.First(x => x.Name == "MainWindow_Loaded");
    var pf = AssemblyDefinition.ReadAssembly(a[2]);
    var pc = AssemblyDefinition.ReadAssembly(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(a[2]), "PresentationCore.dll"));
    var api = AssemblyDefinition.ReadAssembly(System.IO.Path.Combine(a[3], "SourceTree.Api.UI.Wpf.dll"));
    var cc = pf.MainModule.Types.SelectMany(Flat).First(x => x.Name == "ContentControl" && x.Namespace == "System.Windows.Controls");
    var getContentRef = asm.MainModule.ImportReference(cc.Methods.First(x => x.Name == "get_Content"));
    var fe = pf.MainModule.Types.SelectMany(Flat).First(x => x.Name == "FrameworkElement" && x.Namespace == "System.Windows");
    var feRef = asm.MainModule.ImportReference(fe);
    var getDC = asm.MainModule.ImportReference(fe.Methods.First(x => x.Name == "get_DataContext"));
    var setDC = asm.MainModule.ImportReference(fe.Methods.First(x => x.Name == "set_DataContext"));
    var uiElement = pc.MainModule.Types.SelectMany(Flat).First(x => x.Name == "UIElement" && x.Namespace == "System.Windows");
    var uiElementRef = asm.MainModule.ImportReference(uiElement);
    var vth = pc.MainModule.Types.SelectMany(Flat).First(x => x.Name == "VisualTreeHelper");
    var getCountRef = asm.MainModule.ImportReference(vth.Methods.First(x => x.Name == "GetChildrenCount"));
    var getChildRef = asm.MainModule.ImportReference(vth.Methods.First(x => x.Name == "GetChild" && x.Parameters.Count == 2));
    var objGetType = asm.MainModule.ImportReference(typeof(object).GetMethod("GetType"));
    var typeFull = asm.MainModule.ImportReference(typeof(Type).GetProperty("FullName").GetGetMethod());
    var strEq = asm.MainModule.ImportReference(typeof(string).GetMethod("Equals", new[]{typeof(string), typeof(string)}));
    var wlS = asm.MainModule.ImportReference(typeof(Console).GetMethod("WriteLine", new[]{typeof(string)}));
    var wlD = asm.MainModule.ImportReference(typeof(Console).GetMethod("WriteLine", new[]{typeof(double)}));
    var mwVm = asm.MainModule.Types.SelectMany(Flat).First(x => x.Name == "MainWindowViewModel");
    var mwVmRef = asm.MainModule.ImportReference(mwVm);
    var getTabContainer = asm.MainModule.ImportReference(mwVm.Properties.First(x => x.Name == "RepositoryTabContainer").GetMethod);
    var tabIface = api.MainModule.Types.SelectMany(Flat).First(x => x.Name == "IRepositoryTabContainerViewModel");
    var tabIfaceRef = asm.MainModule.ImportReference(tabIface);
    var reopenRef = asm.MainModule.ImportReference(tabIface.Methods.First(x => x.Name == "ReopenTabs"));
    var addOrActivateMethod = tabIface.Methods.First(x => x.Name == "AddOrActivate");
    var addOrActivateRef = asm.MainModule.ImportReference(addOrActivateMethod);
    var getSelectedRepoRef = asm.MainModule.ImportReference(tabIface.Methods.First(x => x.Name == "get_SelectedRepository"));
    /* （菜单展开已移除 —— K-1139 决定不做）*/

    var getTabsRef = asm.MainModule.ImportReference(tabIface.Methods.First(x => x.Name == "get_Tabs"));
    var wlObj = asm.MainModule.ImportReference(typeof(Console).GetMethod("WriteLine", new[]{typeof(object)}));
    var collCountRef = asm.MainModule.ImportReference(typeof(System.Collections.Generic.ICollection<>).Assembly.GetType("System.Collections.Generic.ICollection`1") == null ? typeof(object).GetMethod("ToString") : typeof(object).GetMethod("ToString"));
    var repoViewModeRef = asm.MainModule.ImportReference(addOrActivateMethod.Parameters[1].ParameterType);
    var sv = pf.MainModule.Types.SelectMany(Flat).First(x => x.Name == "ToggleButton" && x.Namespace == "System.Windows.Controls.Primitives");
    var svRef = asm.MainModule.ImportReference(sv);
    var nulBoolDef = typeof(bool?).Assembly.GetType("System.Nullable`1").MakeGenericType(typeof(bool));
    var nulBoolCtor = asm.MainModule.ImportReference(nulBoolDef.GetConstructor(new[]{typeof(bool)}));
    var setCheckedRef = asm.MainModule.ImportReference(sv.Properties.First(x => x.Name == "IsChecked").SetMethod);
    var getCheckedRef = asm.MainModule.ImportReference(sv.Properties.First(x => x.Name == "IsChecked").GetMethod);
    var nullableBool = asm.MainModule.ImportReference(typeof(bool?).GetGenericTypeDefinition().MakeGenericType(typeof(bool)));
    var nulBoolType = asm.MainModule.Types.SelectMany(Flat).FirstOrDefault(x => x.Name == "Nullable`1");
    

    var dv = new TypeDefinition("UiaProbe", "ScrollFix", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, asm.MainModule.TypeSystem.Object);
    asm.MainModule.Types.Add(dv);
    var dF = new FieldDefinition("_d", FieldAttributes.Public | FieldAttributes.Static, asm.MainModule.TypeSystem.Int32);
    dv.Fields.Add(dF);
    var doneF = new FieldDefinition("_done", FieldAttributes.Public | FieldAttributes.Static, asm.MainModule.TypeSystem.Boolean);
    dv.Fields.Add(doneF);
    var scrolledF = new FieldDefinition("_scrolled", FieldAttributes.Public | FieldAttributes.Static, asm.MainModule.TypeSystem.Boolean);
    dv.Fields.Add(scrolledF);
    /* FixTab */
    var fixTab = new MethodDefinition("FixTab", MethodAttributes.Public | MethodAttributes.Static, asm.MainModule.TypeSystem.Void);
    fixTab.Parameters.Add(new ParameterDefinition(uiElementRef));
    fixTab.Parameters.Add(new ParameterDefinition(tabIfaceRef));
    dv.Methods.Add(fixTab);
    var ilf = fixTab.Body.GetILProcessor();
    fixTab.Body.InitLocals = true;
    var fCount = new VariableDefinition(asm.MainModule.TypeSystem.Int32); fixTab.Body.Variables.Add(fCount);
    var fI = new VariableDefinition(asm.MainModule.TypeSystem.Int32); fixTab.Body.Variables.Add(fI);
    var fChild = new VariableDefinition(uiElementRef); fixTab.Body.Variables.Add(fChild);
    var fFe = new VariableDefinition(feRef); fixTab.Body.Variables.Add(fFe);
    void F(Instruction i) => ilf.Append(i);
    var fret = ilf.Create(OpCodes.Ret);
    var fcond = ilf.Create(OpCodes.Ldloc, fI);
    var fincr = ilf.Create(OpCodes.Ldloc, fI);
    var fbody = ilf.Create(OpCodes.Ldarg_0);
    F(ilf.Create(OpCodes.Ldsfld, doneF)); F(ilf.Create(OpCodes.Brtrue, fret));
    F(ilf.Create(OpCodes.Ldsfld, dF)); F(ilf.Create(OpCodes.Ldc_I4, 12)); F(ilf.Create(OpCodes.Bge, fret));
    F(ilf.Create(OpCodes.Ldsfld, dF)); F(ilf.Create(OpCodes.Ldc_I4_1)); F(ilf.Create(OpCodes.Add)); F(ilf.Create(OpCodes.Stsfld, dF));
    F(ilf.Create(OpCodes.Ldarg_0)); F(ilf.Create(OpCodes.Call, getCountRef)); F(ilf.Create(OpCodes.Stloc, fCount));
    F(ilf.Create(OpCodes.Ldc_I4_0)); F(ilf.Create(OpCodes.Stloc, fI));
    F(ilf.Create(OpCodes.Br, fcond));
    F(fbody);
    F(ilf.Create(OpCodes.Ldloc, fI)); F(ilf.Create(OpCodes.Call, getChildRef));
    F(ilf.Create(OpCodes.Isinst, uiElementRef)); F(ilf.Create(OpCodes.Stloc, fChild));
    F(ilf.Create(OpCodes.Ldloc, fChild)); F(ilf.Create(OpCodes.Brfalse, fincr));
    F(ilf.Create(OpCodes.Ldloc, fChild)); F(ilf.Create(OpCodes.Isinst, feRef)); F(ilf.Create(OpCodes.Stloc, fFe));
    F(ilf.Create(OpCodes.Ldloc, fFe)); F(ilf.Create(OpCodes.Brfalse, fincr));
    F(ilf.Create(OpCodes.Ldloc, fFe)); F(ilf.Create(OpCodes.Callvirt, objGetType)); F(ilf.Create(OpCodes.Callvirt, typeFull));
    F(ilf.Create(OpCodes.Ldstr, "SourceTree.View.Repo.RepoTabPanel"));
    F(ilf.Create(OpCodes.Call, strEq));
    var fNot = ilf.Create(OpCodes.Nop);
    F(ilf.Create(OpCodes.Brfalse, fNot));
    F(ilf.Create(OpCodes.Ldloc, fFe)); F(ilf.Create(OpCodes.Ldarg_1)); F(ilf.Create(OpCodes.Callvirt, setDC));
    F(ilf.Create(OpCodes.Ldc_I4_1)); F(ilf.Create(OpCodes.Stsfld, doneF));
    F(fNot);
    F(ilf.Create(OpCodes.Ldloc, fChild)); F(ilf.Create(OpCodes.Ldarg_1)); F(ilf.Create(OpCodes.Call, fixTab));
    F(fincr);
    F(ilf.Create(OpCodes.Ldc_I4_1)); F(ilf.Create(OpCodes.Add)); F(ilf.Create(OpCodes.Stloc, fI));
    F(fcond);
    F(ilf.Create(OpCodes.Ldloc, fCount)); F(ilf.Create(OpCodes.Blt, fbody));
    F(ilf.Create(OpCodes.Ldsfld, dF)); F(ilf.Create(OpCodes.Ldc_I4_1)); F(ilf.Create(OpCodes.Sub)); F(ilf.Create(OpCodes.Stsfld, dF));
    F(fret);

    /* OpenMenu(UIElement) —— K-1235：递归找第一个 MenuItem 并 IsSubmenuOpen=true。
       照 FixTab 的成熟 IL 模式（K-1140 验证过），不走 helper（K-1205/K-1209 证明会崩）。 */
    var miType = pf.MainModule.Types.SelectMany(Flat).FirstOrDefault(x => x.Name == "MenuItem" && x.Namespace == "System.Windows.Controls");
    MethodDefinition openMenu = null;
    if (miType == null) { Console.WriteLine("MenuItem 类型未找到"); }
    else {
      var miRef = asm.MainModule.ImportReference(miType);
      var setOpenRef = asm.MainModule.ImportReference(miType.Properties.First(x => x.Name == "IsSubmenuOpen").SetMethod);
      var openedF = new FieldDefinition("_opened", FieldAttributes.Public | FieldAttributes.Static, asm.MainModule.TypeSystem.Boolean);
      dv.Fields.Add(openedF);
      var odF = new FieldDefinition("_od", FieldAttributes.Public | FieldAttributes.Static, asm.MainModule.TypeSystem.Int32);
      dv.Fields.Add(odF);
      openMenu = new MethodDefinition("OpenMenu", MethodAttributes.Public | MethodAttributes.Static, asm.MainModule.TypeSystem.Void);
      openMenu.Parameters.Add(new ParameterDefinition(uiElementRef));
      dv.Methods.Add(openMenu);
      var ilo = openMenu.Body.GetILProcessor();
      openMenu.Body.InitLocals = true;
      var oCount = new VariableDefinition(asm.MainModule.TypeSystem.Int32); openMenu.Body.Variables.Add(oCount);
      var oI = new VariableDefinition(asm.MainModule.TypeSystem.Int32); openMenu.Body.Variables.Add(oI);
      var oChild = new VariableDefinition(uiElementRef); openMenu.Body.Variables.Add(oChild);
      var oMi = new VariableDefinition(miRef); openMenu.Body.Variables.Add(oMi);
      void O(Instruction i) => ilo.Append(i);
      var oret = ilo.Create(OpCodes.Ret);
      var ocond = ilo.Create(OpCodes.Ldloc, oI);
      var oincr = ilo.Create(OpCodes.Ldloc, oI);
      var obody = ilo.Create(OpCodes.Ldarg_0);
      var skipSet = ilo.Create(OpCodes.Nop);
      O(ilo.Create(OpCodes.Ldsfld, openedF)); O(ilo.Create(OpCodes.Brtrue, oret));
      O(ilo.Create(OpCodes.Ldsfld, odF)); O(ilo.Create(OpCodes.Ldc_I4, 12)); O(ilo.Create(OpCodes.Bge, oret));
      O(ilo.Create(OpCodes.Ldsfld, odF)); O(ilo.Create(OpCodes.Ldc_I4_1)); O(ilo.Create(OpCodes.Add)); O(ilo.Create(OpCodes.Stsfld, odF));
      O(ilo.Create(OpCodes.Ldarg_0)); O(ilo.Create(OpCodes.Isinst, miRef)); O(ilo.Create(OpCodes.Stloc, oMi));
      O(ilo.Create(OpCodes.Ldloc, oMi)); O(ilo.Create(OpCodes.Brfalse, skipSet));
      O(ilo.Create(OpCodes.Ldloc, oMi)); O(ilo.Create(OpCodes.Ldc_I4_1)); O(ilo.Create(OpCodes.Callvirt, setOpenRef));
      O(ilo.Create(OpCodes.Ldc_I4_1)); O(ilo.Create(OpCodes.Stsfld, openedF));
      O(ilo.Create(OpCodes.Ldstr, "[MENUIL] IsSubmenuOpen=true")); O(ilo.Create(OpCodes.Call, wlS));
      O(ilo.Create(OpCodes.Ret));
      O(skipSet);
      O(ilo.Create(OpCodes.Ldarg_0)); O(ilo.Create(OpCodes.Call, getCountRef)); O(ilo.Create(OpCodes.Stloc, oCount));
      O(ilo.Create(OpCodes.Ldc_I4_0)); O(ilo.Create(OpCodes.Stloc, oI));
      O(ilo.Create(OpCodes.Br, ocond));
      O(obody);
      O(ilo.Create(OpCodes.Ldloc, oI)); O(ilo.Create(OpCodes.Call, getChildRef));
      O(ilo.Create(OpCodes.Isinst, uiElementRef)); O(ilo.Create(OpCodes.Stloc, oChild));
      O(ilo.Create(OpCodes.Ldloc, oChild)); O(ilo.Create(OpCodes.Brfalse, oincr));
      O(ilo.Create(OpCodes.Ldloc, oChild)); O(ilo.Create(OpCodes.Call, openMenu));
      O(oincr);
      O(ilo.Create(OpCodes.Ldc_I4_1)); O(ilo.Create(OpCodes.Add)); O(ilo.Create(OpCodes.Stloc, oI));
      O(ocond);
      O(ilo.Create(OpCodes.Ldloc, oCount)); O(ilo.Create(OpCodes.Blt, obody));
      O(ilo.Create(OpCodes.Ldsfld, odF)); O(ilo.Create(OpCodes.Ldc_I4_1)); O(ilo.Create(OpCodes.Sub)); O(ilo.Create(OpCodes.Stsfld, odF));
      O(oret);
    }

    /* DoScroll(UIElement) —— 找第一个 ScrollViewer，滚动到一半 */
    var doScroll = new MethodDefinition("DoScroll", MethodAttributes.Public | MethodAttributes.Static, asm.MainModule.TypeSystem.Void);
    doScroll.Parameters.Add(new ParameterDefinition(uiElementRef));
    dv.Methods.Add(doScroll);
    var ils = doScroll.Body.GetILProcessor();
    doScroll.Body.InitLocals = true;
    var sCount = new VariableDefinition(asm.MainModule.TypeSystem.Int32); doScroll.Body.Variables.Add(sCount);
    var sI = new VariableDefinition(asm.MainModule.TypeSystem.Int32); doScroll.Body.Variables.Add(sI);
    var sChild = new VariableDefinition(uiElementRef); doScroll.Body.Variables.Add(sChild);
    var sSv = new VariableDefinition(svRef); doScroll.Body.Variables.Add(sSv);
    void S(Instruction i) => ils.Append(i);
    var sret = ils.Create(OpCodes.Ret);
    var scond = ils.Create(OpCodes.Ldloc, sI);
    var sincr = ils.Create(OpCodes.Ldloc, sI);
    var sbody = ils.Create(OpCodes.Ldarg_0);
    S(ils.Create(OpCodes.Ldsfld, scrolledF)); S(ils.Create(OpCodes.Brtrue, sret));
    S(ils.Create(OpCodes.Ldsfld, dF)); S(ils.Create(OpCodes.Ldc_I4, 14)); S(ils.Create(OpCodes.Bge, sret));
    S(ils.Create(OpCodes.Ldsfld, dF)); S(ils.Create(OpCodes.Ldc_I4_1)); S(ils.Create(OpCodes.Add)); S(ils.Create(OpCodes.Stsfld, dF));
    S(ils.Create(OpCodes.Ldarg_0)); S(ils.Create(OpCodes.Call, getCountRef)); S(ils.Create(OpCodes.Stloc, sCount));
    S(ils.Create(OpCodes.Ldc_I4_0)); S(ils.Create(OpCodes.Stloc, sI));
    S(ils.Create(OpCodes.Br, scond));
    S(sbody);
    S(ils.Create(OpCodes.Ldloc, sI)); S(ils.Create(OpCodes.Call, getChildRef));
    S(ils.Create(OpCodes.Isinst, uiElementRef)); S(ils.Create(OpCodes.Stloc, sChild));
    S(ils.Create(OpCodes.Ldloc, sChild)); S(ils.Create(OpCodes.Brfalse, sincr));
    S(ils.Create(OpCodes.Ldloc, sChild)); S(ils.Create(OpCodes.Isinst, svRef)); S(ils.Create(OpCodes.Stloc, sSv));
    S(ils.Create(OpCodes.Ldloc, sSv));
    var sNotSv = ils.Create(OpCodes.Nop);
    S(ils.Create(OpCodes.Brfalse, sNotSv));
    /* ToggleButton: 打印 IsChecked 并切换 */
    S(ils.Create(OpCodes.Ldstr, "[TGL] found ToggleButton")); S(ils.Create(OpCodes.Call, wlS));
    S(ils.Create(OpCodes.Ldloc, sSv)); S(ils.Create(OpCodes.Ldc_I4_1));
    S(ils.Create(OpCodes.Newobj, nulBoolCtor));
    S(ils.Create(OpCodes.Callvirt, setCheckedRef));
    S(ils.Create(OpCodes.Ldstr, "[TGL] IsChecked = true")); S(ils.Create(OpCodes.Call, wlS));
    S(ils.Create(OpCodes.Ldc_I4_1)); S(ils.Create(OpCodes.Stsfld, scrolledF));
    S(ils.Create(OpCodes.Ret));
    S(sNotSv);
    S(ils.Create(OpCodes.Ldloc, sChild)); S(ils.Create(OpCodes.Call, doScroll));
    S(sincr);
    S(ils.Create(OpCodes.Ldc_I4_1)); S(ils.Create(OpCodes.Add)); S(ils.Create(OpCodes.Stloc, sI));
    S(scond);
    S(ils.Create(OpCodes.Ldloc, sCount)); S(ils.Create(OpCodes.Blt, sbody));
    S(ils.Create(OpCodes.Ldsfld, dF)); S(ils.Create(OpCodes.Ldc_I4_1)); S(ils.Create(OpCodes.Sub)); S(ils.Create(OpCodes.Stsfld, dF));
    S(sret);

    /* 主入口 */
    var vTabVm = new VariableDefinition(tabIfaceRef); m.Body.Variables.Add(vTabVm);
    m.Body.InitLocals = true;
    var il2 = m.Body.GetILProcessor();
    var first2 = m.Body.Instructions[0];
    void Push(params Instruction[] xs) { foreach (var x in xs) il2.InsertBefore(first2, x); }
    Push(il2.Create(OpCodes.Ldc_I4_0)); Push(il2.Create(OpCodes.Stsfld, dF));
    Push(il2.Create(OpCodes.Ldc_I4_0)); Push(il2.Create(OpCodes.Stsfld, doneF));
    Push(il2.Create(OpCodes.Ldc_I4_0)); Push(il2.Create(OpCodes.Stsfld, scrolledF));
    Push(il2.Create(OpCodes.Ldstr, "[SCR] enter")); Push(il2.Create(OpCodes.Call, wlS));
    Push(il2.Create(OpCodes.Ldarg_0)); Push(il2.Create(OpCodes.Callvirt, getDC));
    Push(il2.Create(OpCodes.Castclass, mwVmRef)); Push(il2.Create(OpCodes.Callvirt, getTabContainer));
    Push(il2.Create(OpCodes.Stloc, vTabVm));
    Push(il2.Create(OpCodes.Ldarg_0)); Push(il2.Create(OpCodes.Callvirt, getContentRef));
    Push(il2.Create(OpCodes.Castclass, uiElementRef)); Push(il2.Create(OpCodes.Ldloc, vTabVm));
    Push(il2.Create(OpCodes.Call, fixTab));
    Push(il2.Create(OpCodes.Ldstr, "[SCR] tab-fix done")); Push(il2.Create(OpCodes.Call, wlS));
    Push(il2.Create(OpCodes.Ldloc, vTabVm)); Push(il2.Create(OpCodes.Callvirt, reopenRef));
    Push(il2.Create(OpCodes.Ldstr, "[SCR] reopen done")); Push(il2.Create(OpCodes.Call, wlS));
    /* K-1134：诊断 —— 打印 SelectedRepository 和 Tabs 数量 */
    Push(il2.Create(OpCodes.Ldstr, "[DIAG] SelectedRepository:"));
    Push(il2.Create(OpCodes.Call, wlS));
    Push(il2.Create(OpCodes.Ldloc, vTabVm));
    Push(il2.Create(OpCodes.Callvirt, getSelectedRepoRef));
    Push(il2.Create(OpCodes.Call, wlObj));
    Push(il2.Create(OpCodes.Ldstr, "[DIAG] Tabs:"));
    Push(il2.Create(OpCodes.Call, wlS));
    Push(il2.Create(OpCodes.Ldloc, vTabVm));
    Push(il2.Create(OpCodes.Callvirt, getTabsRef));
    Push(il2.Create(OpCodes.Call, wlObj));
    /* K-1135：Gong 已 patch（K-1132），重试 AddOrActivate */
    Push(il2.Create(OpCodes.Ldloc, vTabVm));
    Push(il2.Create(OpCodes.Ldstr, "/tmp/stgit"));
    Push(il2.Create(OpCodes.Ldc_I4_0));
    Push(il2.Create(OpCodes.Ldstr, "stgit"));
    Push(il2.Create(OpCodes.Callvirt, addOrActivateRef));
    Push(il2.Create(OpCodes.Ldstr, "[DIAG] AddOrActivate OK")); Push(il2.Create(OpCodes.Call, wlS));
    Push(il2.Create(OpCodes.Ldstr, "[DIAG] SelectedRepository after:"));
    Push(il2.Create(OpCodes.Call, wlS));
    Push(il2.Create(OpCodes.Ldloc, vTabVm));
    Push(il2.Create(OpCodes.Callvirt, getSelectedRepoRef));
    Push(il2.Create(OpCodes.Call, wlObj));
    /* （菜单展开已移除）*/
    Push(il2.Create(OpCodes.Ldarg_0)); Push(il2.Create(OpCodes.Callvirt, getContentRef));
    Push(il2.Create(OpCodes.Castclass, uiElementRef)); Push(il2.Create(OpCodes.Call, doScroll));
    /* K-1235：展开第一个菜单（纯 IL） */
    if (openMenu != null) {
      Push(il2.Create(OpCodes.Ldarg_0)); Push(il2.Create(OpCodes.Callvirt, getContentRef));
      Push(il2.Create(OpCodes.Castclass, uiElementRef)); Push(il2.Create(OpCodes.Call, openMenu));
    }
    Push(il2.Create(OpCodes.Ldstr, "[SCR] done")); Push(il2.Create(OpCodes.Call, wlS));
    asm.Write(a[1]);
    Console.WriteLine("patched -> " + a[1]);
  }
  static System.Collections.Generic.IEnumerable<TypeDefinition> Flat(TypeDefinition t) {
    yield return t;
    foreach (var n in t.NestedTypes) foreach (var x in Flat(n)) yield return x;
  }
}
