using System;
using System.Linq;
using Mono.Cecil;

class Redir
{
    static AssemblyNameReference GetOrAddRef(ModuleDefinition m, AssemblyDefinition src)
    {
        var e = m.AssemblyReferences.FirstOrDefault(r => r.Name == src.Name.Name);
        if (e != null) return e;
        var r = new AssemblyNameReference(src.Name.Name, src.Name.Version) {
            Culture = src.Name.Culture,
            PublicKeyToken = src.Name.PublicKeyToken != null && src.Name.PublicKeyToken.Length > 0
                ? (byte[])src.Name.PublicKeyToken.Clone() : null,
            Attributes = 0, HashAlgorithm = 0,
        };
        m.AssemblyReferences.Add(r);
        return r;
    }

    static int Main(string[] argv)
    {
        if (argv.Length < 4) { Console.Error.WriteLine("用法: redir <SDC.dll> <GdiPlus.dll> <Core.dll> <out.dll>"); return 2; }
        var sdc = AssemblyDefinition.ReadAssembly(argv[0]);
        var gp  = AssemblyDefinition.ReadAssembly(argv[1]);
        var core= AssemblyDefinition.ReadAssembly(argv[2]);
        var outPath = argv[3];
        var mod = sdc.MainModule;

        // ① 加 AssemblyRef（公钥 token 用 ECMA 的 b77a5c561934e089）
        var MS = new byte[] { 0xb7,0x7a,0x5c,0x56,0x19,0x34,0xe0,0x89 };
        AssemblyNameReference coreRef = GetOrAddRef(mod, core);
        if (coreRef.PublicKeyToken == null || coreRef.PublicKeyToken.Length == 0) coreRef.PublicKeyToken = (byte[])MS.Clone();
        AssemblyNameReference gpRef = GetOrAddRef(mod, gp);
        gpRef.PublicKeyToken = (byte[])MS.Clone();
        gpRef.Attributes = 0;
        Console.WriteLine($"[1] refs: core={coreRef.Name} gp={gpRef.Name}");

        // ② 找占位类型、官方类型
        var phHdc = mod.GetType("System.Drawing.LdpPlaceholders.GpHdcStub");
        var phAgp = mod.GetType("System.Drawing.LdpPlaceholders.GpApplyGraphicsStub");
        var hdcDef  = core.MainModule.GetType("Windows.Win32.Graphics.Gdi.HDC");
        var agpDef  = gp.MainModule.GetType("System.Drawing.ApplyGraphicsProperties");
        var igciDef = gp.MainModule.GetType("System.Drawing.IGraphicsContextInfo");
        var igraphicsDef = gp.MainModule.GetType("System.Drawing.IGraphics");
        if (phHdc == null || phAgp == null || hdcDef == null || agpDef == null || igciDef == null) {
            Console.Error.WriteLine($"缺类型: phHdc={phHdc!=null} phAgp={phAgp!=null} hdc={hdcDef!=null} agp={agpDef!=null} igci={igciDef!=null}");
            return 3;
        }
        Console.WriteLine($"[2] 占位类型都在，官方类型都拿到");

        // ③ 把占位类型的 **名字/命名空间/scope** 改成官方类型 —— 这是"重定向"的核心
        //    注意：直接改 TypeDefinition 的名字会破坏源码语义，但这里我们**不再编译**，只写元数据。
        //    做法：给 phHdc 改 Namespace/Name，并把它的 Module 设为... 不行（TypeDefinition 必须在本模块）。
        //    ⇒ 正确做法：**不 TypeDefinition 改名**，而是把所有**引用它的 TypeRef** 换成官方 TypeRef。
        //    但模块内的 TypeSpec 里用的是 TypeDefinition 的直接引用 ⇒ 需要替换 GenericInstanceType 的 GenericArguments。
        Console.WriteLine("[3] 采用「替换泛型实参的 TypeReference」方式");

        var officialHdcRef = mod.ImportReference(hdcDef);
        var officialAgpRef = mod.ImportReference(agpDef);

        int replaced = 0;
        // 遍历所有方法的签名（返回类型 + 参数），把 GenericInstanceType 里指向占位类型的实参替换掉
        void FixSig(TypeReference t)
        {
            if (t is GenericInstanceType git) {
                for (int i = 0; i < git.GenericArguments.Count; ++i) {
                    var ga = git.GenericArguments[i];
                    if (ga.FullName == phHdc.FullName) { git.GenericArguments[i] = officialHdcRef; replaced++; }
                    else if (ga.FullName == phAgp.FullName) { git.GenericArguments[i] = officialAgpRef; replaced++; }
                }
            }
        }
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods) {
                FixSig(m.ReturnType);
                foreach (var p in m.Parameters) FixSig(p.ParameterType);
            }
        Console.WriteLine($"[4] 泛型实参已替换 {replaced} 处");

        // ④ 找 Graphics 的 4 个方法，加 Overrides + Interfaces
        var graphics = mod.GetType("System.Drawing.Graphics");
        var mGetHdcCtx = graphics.Methods.FirstOrDefault(m => m.Name == "GetHdcContext");
        var mGetPointer = graphics.Methods.FirstOrDefault(m => m.Name == "GetPointerGpGraphics");
        var mGetHdc2 = graphics.Methods.FirstOrDefault(m => m.Name == "GetHdcCtx");
        var mRelHdc = graphics.Methods.FirstOrDefault(m => m.Name == "ReleaseHdcCtx");
        Console.WriteLine($"[5] 方法: GetHdcContext={mGetHdcCtx!=null} GetPointer={mGetPointer!=null} GetHdcCtx={mGetHdc2!=null} ReleaseHdcCtx={mRelHdc!=null}");

        // ⑤ GetHdcCtx 的**直接返回类型**也要替换（它不是泛型实参，是独立 TypeReference）
        if (mGetHdc2 != null && mGetHdc2.ReturnType.FullName == phHdc.FullName) {
            mGetHdc2.ReturnType = officialHdcRef;
            Console.WriteLine("[5b] GetHdcCtx 返回类型已换成官方 HDC");
        }
        // GetHdcContext 的参数类型也要换（GpApplyGraphicsStub -> ApplyGraphicsProperties）
        if (mGetHdcCtx != null && mGetHdcCtx.Parameters.Count > 0
            && mGetHdcCtx.Parameters[0].ParameterType.FullName == phAgp.FullName) {
            mGetHdcCtx.Parameters[0].ParameterType = officialAgpRef;
            Console.WriteLine("[5c] GetHdcContext 参数类型已换成官方 ApplyGraphicsProperties");
        }

        // ⑥ 加 InterfaceImpl：声明 IGraphics（自动带 3 个父接口）
        var igraphicsRef = mod.ImportReference(igraphicsDef);
        var igciRef = mod.ImportReference(igciDef);
        graphics.Interfaces.Add(new InterfaceImplementation(igraphicsRef));
        Console.WriteLine("[7] +InterfaceImpl IGraphics");

        // ⑦ 加 Overrides —— 4 个方法绑到 4 个接口方法
        //   ⚠️ `IPointer<T>` 的 T 是 **GpGraphics**（K-300 教训），不是 IntPtr
        var iPointerDef = core.MainModule.GetType("Windows.Win32.Foundation.IPointer`1");
        var gpgraphicsDef = core.MainModule.GetType("Windows.Win32.Graphics.GdiPlus.GpGraphics");
        var iHdcCtxDef = core.MainModule.GetType("Windows.Win32.Graphics.Gdi.IHdcContext");

        // (a) IGraphicsContextInfo.GetHdc(ApplyGraphicsProperties, bool) -> ValueTuple<HDC,int>
        var ifaceGetHdc = igciDef.Methods.First(m => m.Name == "GetHdc");
        var ifaceGetHdcRef = mod.ImportReference(ifaceGetHdc);
        mGetHdcCtx.Overrides.Add(ifaceGetHdcRef);

        // (b) IPointer<GpGraphics>.get_Pointer -> IntPtr
        var iPointerInst = new GenericInstanceType(mod.ImportReference(iPointerDef));
        iPointerInst.GenericArguments.Add(mod.ImportReference(gpgraphicsDef));
        var getPointerRef = new MethodReference("get_Pointer", mod.TypeSystem.IntPtr, iPointerInst) { HasThis = true };
        mGetPointer.Overrides.Add(getPointerRef);

        // (c)(d) IHdcContext.GetHdc / ReleaseHdc
        var iHdcCtxRef = mod.ImportReference(iHdcCtxDef);
        var hdcGetRef = new MethodReference("GetHdc", officialHdcRef, iHdcCtxRef) { HasThis = true };
        var hdcRelRef = new MethodReference("ReleaseHdc", mod.TypeSystem.Void, iHdcCtxRef) { HasThis = true };
        mGetHdc2.Overrides.Add(hdcGetRef);
        mRelHdc.Overrides.Add(hdcRelRef);
        Console.WriteLine("[8] +Overrides x4");

        // ⑦b 【K-309】接口实现方法必须是 virtual（源码里写的是 internal ⇒ 非 virtual）
        //   `Final`（sealed）必须 —— 否则 CLR 可能要求子类可见性检查；
        //   `NewSlot` 确保是新槽而不是覆盖基类方法。
        foreach (var mm in new[]{mGetHdcCtx, mGetPointer, mGetHdc2, mRelHdc}) {
            if (mm == null) continue;
            mm.Attributes |= MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot;
            mm.Attributes &= ~MethodAttributes.Abstract;
        }
        Console.WriteLine("[7b] 4 个方法已加 Virtual|Final|NewSlot");

        // ⑧ 校验
        Console.WriteLine($"[9] Interfaces = {string.Join(" | ", graphics.Interfaces.Select(i => i.InterfaceType.FullName))}");
        foreach (var (nm, mm) in new[]{("GetHdcContext",mGetHdcCtx),("GetPointer",mGetPointer),("GetHdcCtx",mGetHdc2),("ReleaseHdcCtx",mRelHdc)})
            Console.WriteLine($"[9] {nm}: ret={mm.ReturnType.FullName} overrides={mm.Overrides.Count}");

        sdc.Write(outPath);
        Console.WriteLine("[10] 写出: " + outPath);
        return 0;
    }
}
