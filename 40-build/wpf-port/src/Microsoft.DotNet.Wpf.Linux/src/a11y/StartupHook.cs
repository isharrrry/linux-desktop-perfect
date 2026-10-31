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
        // 【ACC-028 / K-067】抓首次机会异常，打印 TargetSite + 完整栈。
        //   比普通栈更准：TargetSite 直接指出"哪个方法没被找到"。
        //   只对 EntryPointNotFoundException 生效，避免刷屏。
        try
        {
            AppDomain.CurrentDomain.FirstChanceException += (s3, e3) =>
            {
                if (e3.Exception is not EntryPointNotFoundException) return;
                var ex = e3.Exception;
                Console.Error.WriteLine("[FCE] " + ex.GetType().Name + ": " + ex.Message);
                Console.Error.WriteLine("[FCE] TargetSite = " +
                    (ex.TargetSite != null ? (ex.TargetSite.DeclaringType?.FullName ?? "?") + "." + ex.TargetSite.Name : "(null)"));
                Console.Error.WriteLine("[FCE] Stack = " + ex.StackTrace);
                // 【K-069】检查接口映射：PaintEventArgs 有没有把 IDeviceContext.GetHdc 映射上
                try
                {
                    var tPe = Type.GetType("System.Windows.Forms.PaintEventArgs, System.Windows.Forms");
                    var tIdc = Type.GetType("System.Drawing.IDeviceContext, System.Drawing.Common");
                    if (tPe != null && tIdc != null)
                    {
                        Console.Error.WriteLine("[FCE] PaintEventArgs=" + tPe.Assembly.FullName);
                        Console.Error.WriteLine("[FCE] IDeviceContext=" + tIdc.Assembly.FullName);
                        var im = tPe.GetInterfaceMap(tIdc);
                        Console.Error.WriteLine("[FCE] InterfaceMap 长度=" + im.InterfaceMethods.Length);
                        for (int i = 0; i < im.InterfaceMethods.Length; i++)
                            Console.Error.WriteLine("[FCE]   " + im.InterfaceMethods[i].Name + " -> " +
                                (im.TargetMethods[i]?.Name ?? "(null)"));
                    }
                    else Console.Error.WriteLine("[FCE] 找不到类型 pe=" + (tPe != null) + " idc=" + (tIdc != null));
                }
                catch (Exception e4) { Console.Error.WriteLine("[FCE] GetInterfaceMap 失败：" + e4.GetType().Name + ": " + e4.Message); }
                Console.Error.Flush();
            };
        }
        catch (Exception ex) { Console.Error.WriteLine("[FCE] 安装失败：" + ex.GetType().Name); }

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
