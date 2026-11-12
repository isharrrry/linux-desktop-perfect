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

    static int Main(string[] argv)
    {
        if (argv.Length < 1) { Console.Error.WriteLine("用法: <appDir> [--check]"); return 2; }
        var app = argv[0];
        var check = argv.Length > 1 && argv[1] == "--check";
        var log = new List<string>();
        int rc = 0;
        rc |= FixVsmef(Path.Combine(app, "Sourcetree.Composition.VSMef.Net48.dll"), check, log);
        rc |= FixApi(Path.Combine(app, "SourceTree.Api.dll"), check, log);
        rc |= FixExe(Path.Combine(app, "SourceTree.exe"), check, log);
        rc |= FixOnboard(Path.Combine(app, "SourceTree.exe"), check, log);
        rc |= FixGitLookup(Path.Combine(app, "SourceTree.Dvcs.Git.dll"),
                           Path.Combine(app, "SourceTree.Api.Dvcs.dll"), check, log);
        rc |= FixGitVersionRegex(Path.Combine(app, "SourceTree.Dvcs.Git.dll"), check, log);
        rc |= ReplaceStrings(Path.Combine(app, "SourceTree.Dvcs.Hg.dll"), "Mercurial", "get_ExeName",
            new Dictionary<string, string> { { "hg.exe", "hg" } }, check, log, "hg-exename");
        foreach (var l in log) Console.WriteLine(l);
        Console.WriteLine(rc == 0 ? "STATUS=OK" : "STATUS=FAIL");
        return rc;
    }
}
