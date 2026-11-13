// applier：让 SourceTree 3.4.15（.NET Framework 程序）在 Linux + 自产 WindowsDesktop
// 运行时 10.0.11 上能起到主窗口（K-1497）。
//
// 四个根因与修法（全部是「Windows 专有假设在 Linux 上不成立」）：
//  1. Sourcetree.Composition.VSMef.Net48.VSMefCompositionManager.GetAssemblies
//     用 `LastIndexOf('\\')` 切程序集目录；Linux 路径没有反斜杠 → -1 → Substring 抛
//     ArgumentOutOfRangeException，MEF 组合直接失败。修法：把分隔符常量 92('\\') 改为 47('/')。
//  2. 同类型 SaveCompositionCache 在序列化 MEF 缓存时访问 .NET Framework 专有的
//     System.Reflection.Emit.MethodToken（Core 无此类型）→ TypeLoadException。
//     修法：方法体替换为 `return Task.CompletedTask`（缓存是可选加速，跳过只影响启动耗时）。
//  3. SourceTree.Api 的 DiffMergeTool* 构造函数/TryFindApplication 直接读注册表
//     （FindApplicationByRegKey / FindApplicationSearchProgramFiles）→ Linux 上抛
//     PlatformNotSupportedException / 返回 null 后 NRE。修法：把这些调用替换为
//     「弹出参数 + 压 null」（工具路径为空，Linux 上本就没有这些 Windows diff 工具）。
//  4. SourceTree.AppRoot.RegisterBrowserEmulationMode 写 HKCU 注册表 → NRE。
//     修法：方法体替换为单条 ret。
//
// 用法：
//   APP=/tmp/stpkg/SourceTree/app-3.4.15 dotnet run --project . -- $APP --check
//   APP=/tmp/stpkg/SourceTree/app-3.4.15 dotnet run --project . -- $APP
// 幂等：全部已打补丁时输出 ALREADY_PATCHED 并不写文件。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class SourcetreeBoot
{
    static AssemblyDefinition Load(string path)
    {
        var r = new DefaultAssemblyResolver();
        r.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        return AssemblyDefinition.ReadAssembly(path, new ReaderParameters { InMemory = true, AssemblyResolver = r });
    }

    static IEnumerable<TypeDefinition> Types(AssemblyDefinition a) => a.MainModule.GetTypes();

    static bool IsRetOnly(MethodDefinition m) =>
        m.HasBody && m.Body.Instructions.Count == 1 && m.Body.Instructions[0].OpCode == OpCodes.Ret;

    static void ToRet(MethodDefinition m)
    {
        m.Body = new MethodBody(m);
        m.Body.GetILProcessor().Emit(OpCodes.Ret);
    }

    static int FixVsmef(string path, bool check, List<string> log)
    {
        var asm = Load(path);
        var t = Types(asm).FirstOrDefault(x => x.Name == "VSMefCompositionManager");
        if (t == null) { log.Add("vsmef: NO_TYPE"); return 4; }
        int need = 0;

        var ga = t.Methods.FirstOrDefault(x => x.Name == "GetAssemblies");
        var ins = ga?.Body?.Instructions;
        var li = ins?.FirstOrDefault(i => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference mr
                 && mr.Name == "LastIndexOf" && mr.Parameters.Count == 1
                 && mr.Parameters[0].ParameterType.FullName == "System.Char");
        if (li == null) { log.Add("vsmef: NO_LASTINDEXOF"); return 4; }
        var idx = ins.IndexOf(li);
        if (idx < 1) { log.Add("vsmef: BAD_SHAPE"); return 4; }
        var prev = ins[idx - 1];
        int sep = prev.OpCode == OpCodes.Ldc_I4_S ? (sbyte)prev.Operand : -999;
        if (sep == 92) { need++; if (!check) prev.Operand = (sbyte)47; }
        else if (sep != 47) { log.Add("vsmef: UNEXPECTED_SEP " + sep); return 4; }

        var sc = t.Methods.FirstOrDefault(x => x.Name == "SaveCompositionCache");
        if (sc == null || !sc.HasBody) { log.Add("vsmef: NO_SAVECACHE"); return 4; }
        bool scDone = sc.Body.Instructions.Count == 2 && sc.Body.Instructions[0].OpCode == OpCodes.Call
                      && sc.Body.Instructions[1].OpCode == OpCodes.Ret;
        if (!scDone)
        {
            need++;
            if (!check)
            {
                var corelib = asm.MainModule.TypeSystem.CoreLibrary;
                var taskType = new TypeReference("System.Threading.Tasks", "Task", asm.MainModule, corelib);
                var getCompleted = new MethodReference("get_CompletedTask", taskType, taskType) { HasThis = false };
                var body = new MethodBody(sc);
                body.GetILProcessor().Emit(OpCodes.Call, getCompleted);
                body.GetILProcessor().Emit(OpCodes.Ret);
                sc.Body = body;
            }
        }
        if (need == 0) { log.Add("vsmef: ALREADY"); return 0; }
        if (check) { log.Add("vsmef: NEEDS " + need); return 0; }
        asm.Write(path, new WriterParameters { WriteSymbols = false });
        log.Add("vsmef: PATCHED " + need);
        return 0;
    }

    static int FixApi(string path, bool check, List<string> log)
    {
        var asm = Load(path);
        var todo = new List<(MethodDefinition, Instruction)>();
        foreach (var t in Types(asm))
        {
            if (!t.Name.StartsWith("DiffMergeTool")) continue;
            foreach (var m in t.Methods.Where(x => x.HasBody))
                foreach (var i in m.Body.Instructions)
                    if (i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference mr
                        && (mr.Name == "FindApplicationByRegKey" || mr.Name == "FindApplicationSearchProgramFiles"))
                        todo.Add((m, i));
        }
        if (todo.Count == 0) { log.Add("api: ALREADY"); return 0; }
        if (check) { log.Add("api: NEEDS " + todo.Count); return 0; }
        foreach (var (m, i) in todo)
        {
            var mr = (MethodReference)i.Operand;
            int pops = mr.Parameters.Count + (mr.HasThis ? 1 : 0);
            var il = m.Body.GetILProcessor();
            for (int k = 0; k < pops; k++) il.InsertBefore(i, Instruction.Create(OpCodes.Pop));
            il.InsertBefore(i, Instruction.Create(OpCodes.Ldnull));
            il.Remove(i);
        }
        asm.Write(path, new WriterParameters { WriteSymbols = false });
        log.Add("api: PATCHED " + todo.Count);
        return 0;
    }

    static int FixExe(string path, bool check, List<string> log)
    {
        var asm = Load(path);
        var t = Types(asm).FirstOrDefault(x => x.Name == "AppRoot");
        var m = t?.Methods.FirstOrDefault(x => x.Name == "RegisterBrowserEmulationMode");
        if (m == null || !m.HasBody) { log.Add("exe: NO_METHOD"); return 4; }
        if (IsRetOnly(m)) { log.Add("exe: ALREADY"); return 0; }
        if (check) { log.Add("exe: NEEDS"); return 0; }
        ToRet(m);
        asm.Write(path, new WriterParameters { WriteSymbols = false });
        log.Add("exe: PATCHED");
        return 0;
    }

    // 5) 跳过首启向导，直接创建主窗口：
    //    AppRoot.OnStartup 先调 OnboardIfRequired()（里面对话框式跑欢迎向导），随后无条件
    //    创建 MainWindow 并 Show。把 OnboardIfRequired 整体替换为 ret，等价于「已完成引导」，
    //    仍保留 OnStartup 的 MainWindow 创建路径，不改向导类本身。
    static int FixOnboard(string path, bool check, List<string> log)
    {
        var asm = Load(path);
        var t = Types(asm).FirstOrDefault(x => x.Name == "AppRoot");
        var m = t?.Methods.FirstOrDefault(x => x.Name == "OnboardIfRequired");
        if (m == null || !m.HasBody) { log.Add("onboard: NO_METHOD"); return 4; }
        if (IsRetOnly(m)) { log.Add("onboard: ALREADY"); return 0; }
        if (check) { log.Add("onboard: NEEDS"); return 0; }
        ToRet(m);
        asm.Write(path, new WriterParameters { WriteSymbols = false });
        log.Add("onboard: PATCHED");
        return 0;
    }

    // 6) Git 安装探测的 Windows 假设：ExeName = "git.exe"、PATH 搜索用 `where`。
    //    Linux 上命令叫 git、查找命令叫 which。只替换字符串常量，不改控制流。
    //    注：改完实测 `which git` 已能返回 /usr/bin/git 且 `/usr/bin/git --version` 成功（git 2.34.1），
    //    但仍有下游 `FileHelper.GetFileName` 的 `\\` 假设未解（卡点见 AIDash 本轮证据卡）。
    static int FixGitLookup(string gitDll, string apiDvcsDll, bool check, List<string> log)
    {
        int rc = 0;
        rc |= ReplaceStrings(gitDll, "Git", "get_ExeName",
            new Dictionary<string, string> { { "git.exe", "git" } }, check, log, "git-exename");
        rc |= ReplaceStrings(gitDll, "Git", "EnableDefaultSystemInstallation",
            new Dictionary<string, string> { { "git.exe", "git" } }, check, log, "git-search");
        rc |= ReplaceStrings(apiDvcsDll, "AbstractDvcs", "FindInstallationViaPathEnvVar",
            new Dictionary<string, string> { { "where", "which" } }, check, log, "git-which");
        return rc;
    }

    static int ReplaceStrings(string path, string typeName, string methodName,
        Dictionary<string, string> map, bool check, List<string> log, string tag)
    {
        var asm = Load(path);
        var t = Types(asm).FirstOrDefault(x => x.Name == typeName);
        var m = t?.Methods.FirstOrDefault(x => x.Name == methodName);
        if (m == null || !m.HasBody) { log.Add(tag + ": NO_METHOD"); return 4; }
        int need = 0;
        foreach (var ins in m.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Ldstr && map.ContainsKey((string)ins.Operand)) need++;
        }
        if (need == 0) { log.Add(tag + ": ALREADY"); return 0; }
        if (check) { log.Add(tag + ": NEEDS " + need); return 0; }
        foreach (var ins in m.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Ldstr && map.ContainsKey((string)ins.Operand)) ins.Operand = map[(string)ins.Operand];
        }
        var tmp = path + ".tmp";
        asm.Write(tmp, new WriterParameters { WriteSymbols = false });
        asm.Dispose();
        File.Move(tmp, path, true);
        log.Add(tag + ": PATCHED " + need);
        return 0;
    }


    // 7) Git 版本兼容正则的 Windows 假设：`Git::GetVersionString` 返回的是 git 的**完整输出**
    //    （实测 `git version 2.34.1`），而 `CompatibilityVersion` 正则要求以版本号开头，
    //    `GitVersionTwoConfiguration` 还要求 `2.x.y.<windows|msysgit|gvfs|vfs>` 四段。
    //    两边都不满足 ⇒ `GetInstallationConfigurationsFromExe` 的版本过滤结果为空（第 4 关），
    //    `CurrentInstallationConfiguration` 为 null，`AppRoot.DoWindowStartupTasks` NRE。
    //    修法：放宽为 `^[^0-9]*<major>\.<minor>\.<patch>.*`（允许非数字前缀、后缀可选）。
    static int FixGitVersionRegex(string path, bool check, List<string> log)
    {
        var asm = Load(path);
        var targets = Types(asm)
            .Where(t => t.Name.Contains("Version") && t.Name.EndsWith("Configuration"))
            .Select(t => t.Methods.FirstOrDefault(m => m.Name == "get_CompatibilityVersion"))
            .Where(m => m != null && m.HasBody)
            .ToList();
        if (targets.Count == 0) { log.Add("git-verregex: NO_METHOD"); return 4; }
        int need = 0;
        foreach (var m in targets)
        {
            var ld = m.Body.Instructions.FirstOrDefault(i => i.OpCode == OpCodes.Ldstr && ((string)i.Operand).StartsWith("^"));
            if (ld == null) continue;
            var cur = (string)ld.Operand;
            if (cur.StartsWith("^[^0-9]*")) continue;   // 已 patch
            var major = cur.StartsWith("^2") ? "2" : "1";
            var want = "^[^0-9]*" + major + "\\.[0-9]+\\.[0-9]+.*";
            if (cur == want) continue;
            need++;
            if (!check) ld.Operand = want;
        }
        if (need == 0) { log.Add("git-verregex: ALREADY"); return 0; }
        if (check) { log.Add("git-verregex: NEEDS " + need); return 0; }
        var tmp = path + ".tmp";
        asm.Write(tmp, new WriterParameters { WriteSymbols = false });
        asm.Dispose();
        File.Move(tmp, path, true);
        log.Add("git-verregex: PATCHED " + need);
        return 0;
    }


    // 8) 本机无 hg：`Mercurial::DetermineAvailability`（void）置 ret —— 不做 Windows 式 Install、
    //    不建 RepoHandlerHg（`GetHandler().CheckConfig()` 在无 hg 时也会抛）。Git 已有合法配置，
    //    `AppRoot.DoWindowStartupTasks` 不再因「所有 DVCS 的 CurrentInstallationConfiguration 全 null」而 NRE。
    static int FixHgDetect(string path, bool check, List<string> log)
    {
        var asm = Load(path);
        var t = Types(asm).FirstOrDefault(x => x.Name == "Mercurial");
        var m = t?.Methods.FirstOrDefault(x => x.Name == "DetermineAvailability");
        if (m == null || !m.HasBody) { log.Add("hg-detect: NO_METHOD"); return 4; }
        if (IsRetOnly(m)) { log.Add("hg-detect: ALREADY"); return 0; }
        if (check) { log.Add("hg-detect: NEEDS"); return 0; }
        ToRet(m);
        var tmp = path + ".tmp";
        asm.Write(tmp, new WriterParameters { WriteSymbols = false });
        asm.Dispose();
        File.Move(tmp, path, true);
        log.Add("hg-detect: PATCHED");
        return 0;
    }

    // 9) 对某个类型的**全部方法**做 ldstr 替换（不知道具体方法名时用）
    static int ReplaceStringsInType(string path, string typeName, Dictionary<string, string> map,
        bool check, List<string> log, string tag)
    {
        var asm = Load(path);
        var t = Types(asm).FirstOrDefault(x => x.Name == typeName);
        if (t == null) { log.Add(tag + ": NO_TYPE"); return 4; }
        int need = 0;
        foreach (var m in t.Methods)
        {
            if (!m.HasBody) continue;
            foreach (var ins in m.Body.Instructions)
                if (ins.OpCode == OpCodes.Ldstr && map.ContainsKey((string)ins.Operand)) need++;
        }
        if (need == 0) { log.Add(tag + ": ALREADY"); return 0; }
        if (check) { log.Add(tag + ": NEEDS " + need); return 0; }
        foreach (var m in t.Methods)
        {
            if (!m.HasBody) continue;
            foreach (var ins in m.Body.Instructions)
                if (ins.OpCode == OpCodes.Ldstr && map.ContainsKey((string)ins.Operand)) ins.Operand = map[(string)ins.Operand];
        }
        var tmp = path + ".tmp";
        asm.Write(tmp, new WriterParameters { WriteSymbols = false });
        asm.Dispose();
        File.Move(tmp, path, true);
        log.Add(tag + ": PATCHED " + need);
        return 0;
    }

    // 10) 把无参取字符串方法整体替换为 `ldstr <value>; ret`（ExeName 由字段/基类算出时用）
    static int ForceReturnString(string path, string typeName, string methodName, string value,
        bool check, List<string> log, string tag)
    {
        var asm = Load(path);
        var t = Types(asm).FirstOrDefault(x => x.Name == typeName);
        var m = t?.Methods.FirstOrDefault(x => x.Name == methodName);
        if (m == null || !m.HasBody) { log.Add(tag + ": NO_METHOD"); return 4; }
        if (m.Body.Instructions.Count == 2 && m.Body.Instructions[0].OpCode == OpCodes.Ldstr
            && (string)m.Body.Instructions[0].Operand == value) { log.Add(tag + ": ALREADY"); return 0; }
        if (check) { log.Add(tag + ": NEEDS"); return 0; }
        m.Body.Instructions.Clear();
        m.Body.Variables.Clear();
        m.Body.ExceptionHandlers.Clear();
        var il = m.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldstr, value));
        il.Append(il.Create(OpCodes.Ret));
        var tmp = path + ".tmp";
        asm.Write(tmp, new WriterParameters { WriteSymbols = false });
        asm.Dispose();
        File.Move(tmp, path, true);
        log.Add(tag + ": PATCHED");
        return 0;
    }


    // 8) Dragablz 的 DWM 颜色参数：DragablzColors 的 .cctor 调 GetWindowGlassBrush/
    //    GetBalancedWindowGlassBrush，二者都 P/Invoke dwmapi.dll!#127
    //    (DwmGetColorizationParameters)。本仓 shim 只导出 DwmGetColorizationColor，
    //    序号导入直接 EntryPointNotFoundException，把 .cctor 拖成 TypeInitializationException
    //    （XamlParseException: StaticExtension）。这两个 brush 只用于窗口玻璃效果，
    //    在 Linux 上退化为固定色即可：方法体改为 new SolidColorBrush(Color.FromRgb(217,217,217))。
    static int FixDragablzColors(string path, bool check, List<string> log)
    {
        var asm = Load(path);
        var t = Types(asm).FirstOrDefault(x => x.FullName == "Dragablz.DragablzColors");
        if (t == null) { log.Add("dragablz: NO_TYPE"); return 4; }
        var cctor = t.Methods.FirstOrDefault(x => x.Name == ".cctor");
        var fromRgb = cctor?.HasBody == true
            ? cctor.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
                .FirstOrDefault(mr => mr.Name == "FromRgb" && mr.DeclaringType.Name == "Color")
            : null;
        int need = 0;
        foreach (var name in new[] { "GetWindowGlassBrush", "GetBalancedWindowGlassBrush" })
        {
            var m = t.Methods.FirstOrDefault(x => x.Name == name);
            if (m == null || !m.HasBody) { log.Add("dragablz-" + name + ": NO_METHOD"); return 4; }
            bool already = !m.Body.Instructions.Any(i => i.OpCode == OpCodes.Call
                && i.Operand is MethodReference r0 && r0.Name == "DwmGetColorizationParameters");
            if (already) { log.Add("dragablz-" + name + ": ALREADY"); continue; }
            need++;
            if (check) { log.Add("dragablz-" + name + ": NEEDS"); continue; }
            var brushCtor = m.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference)
                .Select(i => (MethodReference)i.Operand)
                .FirstOrDefault(mr => mr.Name == ".ctor" && mr.DeclaringType.Name == "SolidColorBrush");
            if (fromRgb == null || brushCtor == null) { log.Add("dragablz-" + name + ": NO_REF"); return 4; }
            m.Body = new MethodBody(m);
            var il = m.Body.GetILProcessor();
            il.Emit(OpCodes.Ldc_I4, 217);
            il.Emit(OpCodes.Ldc_I4, 217);
            il.Emit(OpCodes.Ldc_I4, 217);
            il.Emit(OpCodes.Call, fromRgb);
            il.Emit(OpCodes.Newobj, brushCtor);
            il.Emit(OpCodes.Ret);
            log.Add("dragablz-" + name + ": PATCHED");
        }
        if (need > 0 && !check)
        {
            var tmp = path + ".tmp";
            asm.Write(tmp, new WriterParameters { WriteSymbols = false });
            File.Move(tmp, path, true);
        }
        return 0;
    }

    // 9) MainWindow_Loaded → SetAppID → WindowsAPICodePack Taskbar SHGetPropertyStoreForWindow：
    //    COM 接口指针封送在 .NET Core/Linux 不支持（MarshalDirectiveException: parameter #3），
    //    抛在 Loaded 事件里直接打崩进程。Linux 没有 Windows 任务栏，整个 SetAppID 置 ret。
    static int RetMethod(string path, string typeName, string methodName, bool check, List<string> log, string label)
    {
        var asm = Load(path);
        var t = Types(asm).FirstOrDefault(x => x.FullName == typeName);
        var m = t?.Methods.FirstOrDefault(x => x.Name == methodName);
        if (m == null || !m.HasBody) { log.Add(label + ": NO_METHOD"); return 4; }
        if (IsRetOnly(m)) { log.Add(label + ": ALREADY"); return 0; }
        if (check) { log.Add(label + ": NEEDS"); return 0; }
        ToRet(m);
        var tmp = path + ".tmp";
        asm.Write(tmp, new WriterParameters { WriteSymbols = false });
        File.Move(tmp, path, true);
        log.Add(label + ": PATCHED");
        return 0;
    }

    // 11) 把某类型下**全部同名重载**的方法体替换为常量返回（kind: ret / null / true）。
    //     用于运行时 PresentationCore.dll 与 app 侧几处「Windows 专有能力在 Linux 上不可用」的收口：
    //       - HwndTarget/NotificationWindowHelper::Dispose → ret（K-1264 类：委托被 GC 的致命错）
    //       - DataFormats::GetDataFormat（Int32/String 两个重载）→ ldnull; ret（占位实现会抛
    //         PlatformNotSupportedException，GongSolutions DragDrop 的 .cctor 因此崩）
    //       - ThemeImage::HandleDPIChanged → ret（打开仓库页即核心转储）
    //       - NotificationsManager::ShowNotificationDialogWithYesConfirmation（2 个重载）→ ldc.i4.1; ret
    //         （System.Drawing.Icon.FromHandle 在 Linux/Core 不支持，确认框构造抛 ArgumentException）
    //     全部已 patch 时输出 `<label>: ALREADY`，--check 只报告不写盘。
    static int SetAllMethodBodies(string path, string typeName, string methodName, string kind,
        bool check, List<string> log, string label)
    {
        var asm = Load(path);
        var t = Types(asm).FirstOrDefault(x => x.FullName == typeName);
        if (t == null) { log.Add(label + ": NO_TYPE"); return 4; }
        var ms = t.Methods.Where(m => m.Name == methodName && m.HasBody).ToList();
        if (ms.Count == 0) { log.Add(label + ": NO_METHOD"); return 4; }
        int need = 0;
        foreach (var m in ms)
        {
            if (IsConstBody(m, kind)) continue;
            need++;
            if (check) continue;
            m.Body.Instructions.Clear();
            m.Body.Variables.Clear();
            m.Body.ExceptionHandlers.Clear();
            var il = m.Body.GetILProcessor();
            if (kind == "null") il.Append(il.Create(OpCodes.Ldnull));
            else if (kind == "true") il.Append(il.Create(OpCodes.Ldc_I4_1));
            il.Append(il.Create(OpCodes.Ret));
        }
        if (need == 0) { log.Add(label + ": ALREADY"); return 0; }
        if (check) { log.Add(label + ": NEEDS " + need); return 0; }
        var tmp = path + ".tmp";
        asm.Write(tmp, new WriterParameters { WriteSymbols = false });
        asm.Dispose();
        File.Move(tmp, path, true);
        log.Add(label + ": PATCHED " + need);
        return 0;
    }

    static bool IsConstBody(MethodDefinition m, string kind)
    {
        var ins = m.Body.Instructions;
        if (kind == "ret") return ins.Count == 1 && ins[0].OpCode == OpCodes.Ret;
        if (kind == "null") return ins.Count == 2 && ins[0].OpCode == OpCodes.Ldnull && ins[1].OpCode == OpCodes.Ret;
        if (kind == "true") return ins.Count == 2 && ins[0].OpCode == OpCodes.Ldc_I4_1 && ins[1].OpCode == OpCodes.Ret;
        return false;
    }


    // 12) SplashScreen：.NET 10 的 WindowsBase 里没有 System.Windows.SplashScreen（K-498），
    //     SourceTree.App.Main 里 `newobj SplashScreen::.ctor` + `callvirt Show` 会让 host 在
    //     TypeLoadException 上直接中止（连 Main 都进不去）。Main 里 splash 只用于启动画面，
    //     Linux 上直接去掉：局部变量类型改 object、newobj → ldnull、Show → pop。
    static int FixSplashScreen(string path, bool check, List<string> log)
    {
        var asm = Load(path);
        int need = 0;
        foreach (var t in Types(asm))
        {
            foreach (var m in t.Methods.Where(x => x.HasBody))
            {
                foreach (var v in m.Body.Variables)
                    if (v.VariableType.FullName == "System.Windows.SplashScreen") need++;
                foreach (var i in m.Body.Instructions)
                {
                    if (i.Operand is MethodReference mr && mr.DeclaringType.Name == "SplashScreen")
                        need++;
                }
            }
        }
        if (need == 0) { log.Add("splash: ALREADY"); return 0; }
        if (check) { log.Add("splash: NEEDS " + need); return 0; }
        foreach (var t in Types(asm))
        {
            foreach (var m in t.Methods.Where(x => x.HasBody))
            {
                foreach (var v in m.Body.Variables)
                    if (v.VariableType.FullName == "System.Windows.SplashScreen")
                        v.VariableType = asm.MainModule.TypeSystem.Object;
                var il = m.Body.GetILProcessor();
                foreach (var i in m.Body.Instructions.ToList())
                {
                    if (!(i.Operand is MethodReference mr) || mr.DeclaringType.Name != "SplashScreen") continue;
                    if (i.OpCode == OpCodes.Newobj)
                    {
                        // ctor 有 1 个参数（实测）：先 pop 掉参数，再压 null（栈效果与 newobj 一致）
                        for (int k = 0; k < mr.Parameters.Count; k++)
                            il.InsertBefore(i, il.Create(OpCodes.Pop));
                        i.OpCode = OpCodes.Ldnull; i.Operand = null;
                    }
                    else if (i.OpCode == OpCodes.Callvirt && mr.Name == "Show")
                    {
                        // Show(Boolean) 实测：消耗 this + 1 个 bool、无返回 → 按参数数全部 pop
                        int pops = mr.Parameters.Count + (mr.HasThis ? 1 : 0);
                        for (int k = 0; k < pops - 1; k++)
                            il.InsertBefore(i, il.Create(OpCodes.Pop));
                        i.OpCode = OpCodes.Pop; i.Operand = null;
                    }
                }
            }
        }
        var tmp = path + ".tmp";
        asm.Write(tmp, new WriterParameters { WriteSymbols = false });
        asm.Dispose();
        File.Move(tmp, path, true);
        log.Add("splash: PATCHED " + need);
        return 0;
    }

    // 14) RepositoryMonitorManager.OnStartUp 用 Observable.FromEventPattern 反射订阅
    //     Application.Activated；解析 System.Runtime.InteropServices.WindowsRuntime.EventRegistrationToken
    //     时从 mscorlib 4.0.0.0 找不到 → TypeLoadException → The application is terminating（K-1503）。
    //     只删 FromEventPattern..stfld 段（保留 add_Deactivated）；OnExit 对订阅字段有 null 检查。
    // RepoProcess 的完成回调竞态：InternalProcessOnExited 里当 _blocking=true 时跳过 WaitForExit，
    // 随后 DoCompletionFunctions → DetermineSuccess 读 Process.ExitCode 抛
    // "Process must exit before requested information can be determined"；
    // 改成无条件 WaitForExit 后，Process 已被 Dispose 的路径又抛 "No process is associated with this object"。
    // 因此把该回调整体置 ret：命令行照常执行（RunThis/Run 同步路径不受影响），只放弃异步完成回调。
    // 降级：提交命令的 Success/ExitCode 不刷新（提交本身成功，git log 可见）。
    // K-1505 → K-1506：不再把 InternalProcessOnExited 整体置 ret（那会让所有依赖完成回调的
    // 调用永久等待 → 启动卡死白屏），改为「保留原回调，整段包 try/catch」：
    //   remove_Exited → if(!_blocking) WaitForExit() → DoCompletionFunctions()
    // 两个已知抛出点（Process.ExitCode 的 "must exit before requested information" 与
    // Process 已 Dispose 的 "No process is associated"）都被 catch 吞掉，回调照常返回，
    // 调用方不再卡死；命令行本身不受影响。
    static int FixRepoProcess(string path, bool check, List<string> log)
    {
        var asm = Load(path);
        var m = Types(asm).FirstOrDefault(x => x.Name == "RepoProcess")?
            .Methods.FirstOrDefault(x => x.Name == "InternalProcessOnExited");
        if (m == null || !m.HasBody) { log.Add("repo-process: NO_METHOD"); return 4; }
        // 旧版降级补丁（ret-only）：无法从 ret 重建原方法，要求先恢复备份再跑
        if (IsRetOnly(m)) { log.Add("repo-process: NEEDS_RESTORE"); return 4; }
        // 已带 try/catch（本版补丁）→ 幂等
        if (m.Body.ExceptionHandlers.Count > 0) { log.Add("repo-process: ALREADY"); return 0; }
        if (check) { log.Add("repo-process: NEEDS"); return 0; }
        var il = m.Body.GetILProcessor();
        var first = m.Body.Instructions.First();
        var handlerStart = il.Create(OpCodes.Pop);
        var handlerEnd = il.Create(OpCodes.Ret);
        il.Append(handlerStart);
        il.Append(handlerEnd);
        m.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            CatchType = asm.MainModule.ImportReference(typeof(Exception)),
            TryStart = first,
            TryEnd = handlerStart,
            HandlerStart = handlerStart,
            HandlerEnd = handlerEnd,
        });
        asm.Write(path, new WriterParameters { WriteSymbols = false });
        log.Add("repo-process: PATCHED");
        return 0;
    }

    static int FixRmStartup(string path, bool check, List<string> log)
    {
        var asm = Load(path);
        var m = Types(asm).FirstOrDefault(x => x.Name == "RepositoryMonitorManager")?
            .Methods.FirstOrDefault(x => x.Name == "OnStartUp");
        if (m == null || !m.HasBody) { log.Add("rm-startup: NO_METHOD"); return 4; }
        var body = m.Body;
        bool hasFromEvent = body.Instructions.Any(i => i.OpCode == OpCodes.Call &&
            (i.Operand?.ToString() ?? "").Contains("FromEventPattern"));
        if (!hasFromEvent) { log.Add("rm-startup: ALREADY"); return 0; }
        if (check) { log.Add("rm-startup: NEEDS"); return 0; }
        var start = body.Instructions.First(i => i.OpCode == OpCodes.Call &&
            (i.Operand?.ToString() ?? "").Contains("Application::get_Current"));
        var end = body.Instructions.FirstOrDefault(i => i.OpCode == OpCodes.Stfld &&
            (i.Operand?.ToString() ?? "").Contains("_applicationActivatedSubscription"));
        if (end == null) { log.Add("rm-startup: NO_END"); return 4; }
        var toRemove = new List<Instruction>();
        bool inRange = false;
        foreach (var i in body.Instructions)
        {
            if (i == start) inRange = true;
            if (inRange) toRemove.Add(i);
            if (i == end) break;
        }
        var il = body.GetILProcessor();
        foreach (var i in toRemove) il.Remove(i);
        body.Variables.Clear();
        var tmp = path + ".tmp";
        asm.Write(tmp, new WriterParameters { WriteSymbols = false });
        asm.Dispose();
        File.Move(tmp, path, true);
        log.Add($"rm-startup: PATCHED removed={toRemove.Count}");
        return 0;
    }

    static int Main(string[] argv)
    {
        if (argv.Length < 1) { Console.Error.WriteLine("用法: <appDir> [--check] [--runtime <runtimeDir>]"); return 2; }
        var app = argv[0];
        var check = argv.Contains("--check");
        string runtime = null;
        var ri = Array.IndexOf(argv, "--runtime");
        if (ri >= 0 && ri + 1 < argv.Length) runtime = argv[ri + 1];
        var log = new List<string>();
        int rc = 0;
        rc |= FixVsmef(Path.Combine(app, "Sourcetree.Composition.VSMef.Net48.dll"), check, log);
        rc |= FixApi(Path.Combine(app, "SourceTree.Api.dll"), check, log);
        rc |= FixExe(Path.Combine(app, "SourceTree.exe"), check, log);
        rc |= FixOnboard(Path.Combine(app, "SourceTree.exe"), check, log);
        rc |= FixSplashScreen(Path.Combine(app, "SourceTree.exe"), check, log);
        // 13) Linux 上启动 pageant（Windows PE）会 Process.Start 抛 Win32Exception(8) Exec format error
        //     并打崩进程；SSH agent 在 Linux 不需要，DefaultSshKeyManager 的 3 个 LaunchSshAgent 置 ret。
        rc |= SetAllMethodBodies(Path.Combine(app, "SourceTree.exe"),
            "SourceTree.Security.DefaultSshKeyManager", "LaunchSshAgent", "ret", check, log, "sshagent");
        rc |= FixGitLookup(Path.Combine(app, "SourceTree.Dvcs.Git.dll"),
                           Path.Combine(app, "SourceTree.Api.Dvcs.dll"), check, log);
        rc |= FixGitVersionRegex(Path.Combine(app, "SourceTree.Dvcs.Git.dll"), check, log);
        rc |= ReplaceStrings(Path.Combine(app, "SourceTree.Dvcs.Hg.dll"), "Mercurial", "get_ExeName",
            new Dictionary<string, string> { { "hg.exe", "hg" } }, check, log, "hg-exename");
        rc |= ReplaceStringsInType(Path.Combine(app, "SourceTree.Dvcs.Hg.dll"), "Mercurial",
            new Dictionary<string, string> { { "hg.exe", "hg" } }, check, log, "hg-all");
        rc |= ForceReturnString(Path.Combine(app, "SourceTree.Dvcs.Hg.dll"), "Mercurial", "get_ExeName", "hg", check, log, "hg-exename-force");
        // 7) Mercurial 的版本不是执行 `hg --version`：Mercurial::GetVersionString 覆写为
        //    GetVersionNumber(path) → ApplicationManager.GetVersionFromFile(path)，读的是 PE 文件
        //    版本资源；Linux 的 shell 包装脚本没有版本资源 ⇒ 返回 null ⇒ 版本兼容判断（
        //    StartsWith("3"/"4")）必然失败。直接让 GetVersionString 返回常量。
        rc |= ForceReturnString(Path.Combine(app, "SourceTree.Dvcs.Hg.dll"), "Mercurial", "GetVersionString", "4.9.1", check, log, "hg-version");
        rc |= ForceReturnString(Path.Combine(app, "SourceTree.Dvcs.Hg.dll"), "Mercurial", "GetVersionNumber", "4.9.1", check, log, "hg-versionnumber");
        rc |= FixDragablzColors(Path.Combine(app, "Dragablz.dll"), check, log);
        rc |= RetMethod(Path.Combine(app, "SourceTree.exe"), "SourceTree.MainWindow", "SetAppID", check, log, "setappid");
        rc |= FixRmStartup(Path.Combine(app, "SourceTree.Api.UI.Wpf.dll"), check, log);
        rc |= FixRepoProcess(Path.Combine(app, "SourceTree.Api.UI.Wpf.dll"), check, log);
        // 4 处「bin 级手改」收口（原来只在机器上改、不在 applier 里，部署不可复现）
        rc |= SetAllMethodBodies(Path.Combine(app, "SourceTree.Api.UI.Wpf.dll"),
            "SourceTree.Notifications.NotificationsManager",
            "ShowNotificationDialogWithYesConfirmation", "true", check, log, "notif-dialog");
        rc |= SetAllMethodBodies(Path.Combine(app, "SourceTree.UI.Theme.Wpf.dll"),
            "SourceTree.UI.Theme.Wpf.Controls.ThemeImage",
            "HandleDPIChanged", "ret", check, log, "theme-dpi");
        if (runtime == null)
        {
            log.Add("runtime: SKIP (未传 --runtime)");
        }
        else
        {
            var pc = Path.Combine(runtime, "PresentationCore.dll");
            rc |= SetAllMethodBodies(pc, "System.Windows.Interop.HwndTarget/NotificationWindowHelper",
                "Dispose", "ret", check, log, "pc-notifwnd");
            rc |= SetAllMethodBodies(pc, "System.Windows.DataFormats",
                "GetDataFormat", "null", check, log, "pc-dataformats");
        }
        foreach (var l in log) Console.WriteLine(l);
        Console.WriteLine(rc == 0 ? "STATUS=OK" : "STATUS=FAIL");
        return rc;
    }
}
