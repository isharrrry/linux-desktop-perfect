// DOTNET_STARTUP_HOOKS 入口。
//
// 约定（.NET 硬性）：**全局命名空间**下必须有一个 `public static class StartupHook`
// 且带 `public static void Initialize()`；名字或可见性不对就是
// 「Could not resolve type 'StartupHook' in assembly …」（实测踩过）。
//
// 这里只做一件事：读 LDP_A11Y_DUMP，若设了就起后台线程等主窗口出现再导出结构树。
// 注意：**不要**在本文件里直接引用 WPF 类型 —— 钩子在 Main 之前跑，WPF 还没初始化；
// 真正的遍历在 TreeDumper 里（它在 Application.Current 可用后才被调用）。
using System;

public static class StartupHook
{
    public static void Initialize()
    {
        // 【ACC-028】**无条件**先给 WinForms 家族补装 DllImport resolver。
        //   为什么放在最前面：`System.Windows.Forms.dll` 是 Windows 原版，它没有我们的
        //   `[ModuleInitializer]`，而 `SetDllImportResolver` 必须在它第一次 P/Invoke 之前装。
        //   放在 `LDP_A11Y_DUMP` 判断之前，是为了让"只跑 WinForms、不要 UIA 树"的场景也生效。
        try { Ldp.A11y.Win32ShimCoverage.Install(); } catch (Exception ex) {
            Console.Error.WriteLine("[SHIM-COVER] Install 异常：" + ex.GetType().Name); Console.Error.Flush();
        }

        string dump = Environment.GetEnvironmentVariable("LDP_A11Y_DUMP");
        if (string.IsNullOrEmpty(dump)) return;
        string scr = Environment.GetEnvironmentVariable("LDP_A11Y_SCREEN") ?? "";
        Ldp.A11y.TreeDumper.Run(dump, scr);
    }
}
