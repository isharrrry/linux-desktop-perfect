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
        string dump = Environment.GetEnvironmentVariable("LDP_A11Y_DUMP");
        if (string.IsNullOrEmpty(dump)) return;
        string scr = Environment.GetEnvironmentVariable("LDP_A11Y_SCREEN") ?? "";
        Ldp.A11y.TreeDumper.Run(dump, scr);
    }
}
