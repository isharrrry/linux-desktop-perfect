// ⚠️ 手写件（**不是** `reapply-patches.py` 生成的 `*.Linux.cs`），可直接修改。
//
// 目的：`D-G206` —— 本移植没有完整的 PTS（分页排版引擎），native 侧 `win32_pts.c` 大量入口如实返回
//   `WPF_PTS_ERR_NOT_IMPLEMENTED`（`-10000` 等）。上游 `PTS.Validate()` 收到非零码就抛
//   `PtsException("Page formatting engine did not complete formatting operation. Error code: '-10000'.")`，
//   该异常冒泡到 UI 线程 ⇒ FlowDocument 类控件（ScrollViewer / RichTextBox / PageViewer / Reader /
//   DocumentPaginator）**整个不可用**。
//
// 策略（"托管侧降级"）：在**栈自有的调用层**把这类"引擎没实现"的失败降级为"排版不精确/回退底流"，
//   而不是让异常打死 UI。**零假值**：
//     · `WPF_PTS_STRICT=1` ⇒ 保持旧行为（异常照抛），用于反极性对照；
//     · 只吞 **PTS 引擎自身**的失败（按类型名 + 文案精确判定），其它任何异常**照旧抛**
//       —— 不掩盖真 bug，也不放宽任何断言。
//
// 为什么按"类型名 + 文案"而不是 `catch (PtsException)`：上游把 `PtsException` 声明成
//   `private class`（`src/Microsoft.DotNet.Wpf/src/PresentationFramework/MS/Internal/PtsHost/Pts.cs:299`），
//   `PTS+PtsException` 是嵌套私有类型，外部**无法**在 catch 子句里引用它。
using System;
using System.Collections.Generic;

namespace MS.Internal.PtsHost
{
    /// <summary>PTS 引擎能力缺口的<strong>托管侧降级</strong>判定与一次性记账（见 `D-G206`）。</summary>
    internal static class PtsDegrade
    {
        // 上游 `SR.PTSError` / `SR.FormatRestrictionsExceeded` 的文案前缀（`SR.g.cs` 现取）：
        //   "Page formatting engine did not complete formatting operation. Error code: '{0}'."
        //   "Page dimensions exceed maximum allowed value of 3.579 million pixels."
        private const string PtseErrorPrefix = "Page formatting engine did not complete formatting operation";
        private const string RestrictionsPrefix = "Page dimensions exceed maximum allowed value";
        private const string InvalidHandleMarker = "unmanaged object handle";   // PTS.InvalidHandle()

        private static readonly bool s_strict = ReadFlag("WPF_PTS_STRICT");
        private static readonly bool s_diag = ReadFlag("WPF_PTS_DIAG");

        // 一次性记账：同一个 `where` 只打一行（`WPF_PTS_DIAG=1` ⇒ 每次都打）。
        private static readonly HashSet<string> s_reported = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> s_unsupportedLogged = new HashSet<string>(StringComparer.Ordinal);

        // ── `WPF_PTS_TRAP=1`：**首次抛出**时抓完整调用栈（仅诊断；默认关） ───────────────────────
        //   为什么用 `FirstChanceException`：此时异常还没被展开，`Environment.StackTrace` 能拿到
        //   **真业务帧**（`DispatcherUnhandledException` 里已经看不到业务帧了）。
        //   注意：**不读 `ex.StackTrace`**（本栈读它会抛 `BadImageFormatException`，那是 `D-G205`）。
        private static bool s_trapHooked;

        private static void HookTrapOnce()
        {
            if (s_trapHooked || !ReadFlag("WPF_PTS_TRAP"))
            {
                return;
            }

            s_trapHooked = true;
            try
            {
                AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
                {
                    try
                    {
                        if (!IsPtsGap(e.Exception))
                        {
                            return;
                        }

                        Console.Error.WriteLine("[PTS-TRAP] first-chance " + e.Exception.GetType().Name
                            + ": " + OneLine(e.Exception.Message));
                        Console.Error.WriteLine("[PTS-TRAP] stack>>>");
                        Console.Error.WriteLine(Environment.StackTrace);
                        Console.Error.WriteLine("[PTS-TRAP] <<<stack");
                    }
                    catch
                    {
                    }
                };
            }
            catch
            {
            }
        }

        /// <summary>`WPF_PTS_STRICT=1` ⇒ 旧行为（异常照抛）。</summary>
        internal static bool StrictMode
        {
            get { return s_strict; }
        }

        /// <summary>诊断用：让调用方（或程序启动处）显式挂上 first-chance trap（`WPF_PTS_TRAP=1` 时才生效）。</summary>
        internal static void EnsureTrapHook()
        {
            HookTrapOnce();
        }

        /// <summary>这个异常是不是「PTS 引擎没实现/没排完」这一类（而不是别的真 bug）。</summary>
        internal static bool IsPtsGap(Exception ex)
        {
            if (ex == null)
            {
                return false;
            }

            // `PTS+PtsException`（嵌套私有类）⇒ 只能看名字。
            if (string.Equals(ex.GetType().Name, "PtsException", StringComparison.Ordinal))
            {
                return true;
            }

            string m = ex.Message;
            if (string.IsNullOrEmpty(m))
            {
                return false;
            }

            if (m.IndexOf(PtseErrorPrefix, StringComparison.Ordinal) >= 0
                || m.IndexOf(RestrictionsPrefix, StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            // ── `D-G206` 第二处致命点 ──────────────────────────────────────────────
            //  `PTS.InvalidHandle()`（由 `PTS.ValidateHandle` 调用，位于分页主链
            //    `FsCreatePageFinite → PtsCache.InitGenericInfo → PtsHost.GetFirstPara` 上）抛的是
            //  **普通 `Exception`**，消息为：
            //    "No object associated with unmanaged object handle or type mismatch."
            //  它不是 `PtsException`、也不带 `Sr.PTSError` 前缀 ⇒ 修前 `ShouldSwallow` 返回 false
            //  ⇒ 调用方（`FlowDocumentPaginator.FormatPage`）重抛 ⇒ 该异常在
            //  `ExceptionWrapper.InternalRealCall`（Dispatcher 回调）里 ⇒ **`Environment.FailFast` 直接 abort**。
            //  表现：`FlowDocumentPageViewer` 建窗后 abort（纯 core dump），而 `RichTextBox`/`ScrollViewer` 正常
            //  （后两者不走 `FormatFinite` 的这条链）。
            //  ⇒ 这里按"PTS 缺口"处理（降级吞掉），与 `-10000` 同族：本栈没有完整 PTS，
            //    handle 校验必然失败，属于**诚实缺口**而不是真 bug。
            if (m.IndexOf(InvalidHandleMarker, StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// 判定"这条异常是否可以吞掉（降级）"。
        /// 返回 <c>true</c> ⇒ 调用方**不要**再抛（已降级并记账）；
        /// 返回 <c>false</c> ⇒ 调用方**必须原样重抛**（非 PTS 缺口，或 `WPF_PTS_STRICT=1`）。
        /// </summary>
        internal static bool ShouldSwallow(Exception ex, string where)
        {
            HookTrapOnce();
            // 【确证】证明这个判定点确实在崩溃路径上（env 门控，一次性）
            if (Environment.GetEnvironmentVariable("WPF_PTS_CALLTRACE") == "1")
            {
                Console.Error.WriteLine("[PTS-CALL] ShouldSwallow where=" + (where ?? "?")
                    + " type=" + (ex == null ? "<null>" : ex.GetType().Name)
                    + " isGap=" + (IsPtsGap(ex) ? 1 : 0));
            }
            if (!IsPtsGap(ex))
            {
                return false;
            }

            if (s_strict)
            {
                return false;
            }

            Report(where, ex);
            return true;
        }

        /// <summary>记账一行（默认同 `where` 只打一次；`WPF_PTS_DIAG=1` 每次都打）。</summary>
        internal static void Report(string where, Exception ex)
        {
            try
            {
                if (!s_diag && !s_reported.Add(where ?? "?"))
                {
                    return;
                }

                if (ex != null && ex.Message != null
                    && ex.Message.IndexOf(InvalidHandleMarker, StringComparison.Ordinal) >= 0
                    && s_unsupportedLogged.Add(where ?? "?"))
                {
                    Console.Error.WriteLine("[PTS-UNSUPPORTED] 分页查看器（FlowDocumentPageViewer/Reader）不可用："
                        + "本栈 PTS 分页引擎未实现（handle 校验失败）；已降级、不崩溃。where=" + (where ?? "?"));
                }

                Console.Error.WriteLine("[PTS-DEGRADED] where=" + (where ?? "?")
                    + " type=" + ex.GetType().Name
                    + " msg=" + OneLine(ex.Message)
                    + (s_diag ? " (WPF_PTS_DIAG=1)" : " (仅首次打印)"));
            }
            catch
            {
                // 记账失败绝不影响排版主流程。
            }
        }

        private static string OneLine(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }
            return s.Replace('\r', ' ').Replace('\n', ' ');
        }

        private static bool ReadFlag(string name)
        {
            try
            {
                string v = Environment.GetEnvironmentVariable(name);
                return !string.IsNullOrEmpty(v) && v != "0";
            }
            catch
            {
                return false;
            }
        }
    }
}
