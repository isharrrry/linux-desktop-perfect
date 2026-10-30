// WPF-on-Linux · M7b · Linux 版的 MS.Utility.EventTrace（替换上游 Shared/MS/Utility/Trace.cs）
//
// ── 为什么必须换掉上游那 136 行 ───────────────────────────────────────────
//   上游 `EventTrace` 的静态构造函数是：
//
//       static EventTrace()
//       {
//           Guid providerGuid = new Guid("E13B77A8-14B6-11DE-8069-001B212B5009");
//           if (Environment.OSVersion.Version.Major < 6 ||      // ← ①
//               IsClassicETWRegistryEnabled())                  // ← ②
//               EventProvider = new ClassicTraceProvider();
//           else
//               EventProvider = new ManifestTraceProvider();
//           EventProvider.Register(providerGuid);
//       }
//
//   在 Linux 上这两条**同时**出问题：
//     ① `Environment.OSVersion` 在 Unix 上返回的是**内核版本**（本机 6.x），
//        所以 `Major < 6` 为假，短路失效，于是必然走到 ②；
//     ② `IsClassicETWRegistryEnabled()` 读
//        `HKEY_CURRENT_USER\Software\Microsoft\Avalon.Graphics\ClassicETW`，
//        而 `Microsoft.Win32.Registry` 在 Unix 上直接抛 PlatformNotSupportedException；
//     ③ 就算绕过 ②，`ManifestTraceProvider.Register` 会 P/Invoke `advapi32.dll`
//        的 EventRegister —— Linux 上同样是 DllNotFoundException。
//
//   **实测症状（本轮真踩）**：`EventTrace.IsEnabled` 是 Dispatcher /
//   DependencyObject / UIElement / LayoutManager 等 24 个文件、506 处调用的
//   必经之路。于是：
//     · `Dispatcher.BeginInvoke` → TypeInitializationException；
//     · 更致命的是 `HwndWrapper` 的**终结器**里也会走到它 →
//       终结器线程上的未处理异常 → **整个测试宿主进程崩溃**（不是用例失败，是崩溃）。
//   也就是说：不修这个，WindowsBase 在 Linux 上根本不是一个可用的程序集。
//
// ── 换成了什么（以及为什么这是"诚实"的而不是"假装"的）──────────────
//   **ETW 是 Windows 专有的事件追踪机制，Linux 上没有对应物。**
//   所以正确的替代不是"用 X11 做点什么"，而是"明确地什么都不做"：
//   一个 `NullTraceProvider`：`IsEnabled` 恒为 false，`Register`/`EventWrite`
//   是 no-op。语义上它**精确**等于"这台机器上没有 ETW 会话订阅了这些 provider"，
//   而上游代码在任何没有订阅者的机器上走的就是这条路径（`_enabled == false`
//   → 全部 `TraceEvent` 调用被 `IsEnabled` 挡掉）。也就是说：
//   行为与「Windows 上没人监听 ETW」完全一致，没有伪造任何东西。
//
//   不选 `ManifestTraceProvider` 的理由：它 Register 时会 P/Invoke advapi32.dll。
//   不选「让 Registry 返回 0」的理由：那只是把 ② 绕过去，③ 还在。
//
// ── 与上游的一致性 ────────────────────────────────────────────────────────
//   除静态构造函数与 `IsEnabled` 里多出的一个 null 检查外，本文件**逐行照抄**
//   上游 Trace.cs（含 7 个 `EasyTraceEvent` 重载与 `LayoutSource` 枚举），
//   这样任何上游新增的调用点都能继续编译。
//   `Keyword` / `Level` / `Event` 枚举由 `Shared/Tracing/managed/wpf-etw.cs`
//   提供（该文件仍在编译集里，未改动），所以这里不重复定义。
//
// ── 接线方式 ──────────────────────────────────────────────────────────────
//   `src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/reapply-patches.py` 注入：
//       <Compile Remove="$(UpstreamWpfRoot)…/Shared/MS/Utility/Trace.cs" />
//       <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/shims/WindowsBase.EventTrace.Shim.cs" />
//   两者必须成对出现（少一个就是重复定义或类型缺失），所以放在同一个补丁里。

using System;

namespace MS.Utility
{
    internal static partial class EventTrace
    {
        internal static readonly TraceProvider EventProvider;

        // EasyTraceEvent
        // Checks the keyword and level before emiting the event
        internal static void EasyTraceEvent(Keyword keywords, Event eventID)
        {
            if (IsEnabled(keywords, Level.Info))
            {
                EventProvider.TraceEvent(eventID, keywords, Level.Info);
            }
        }

        // EasyTraceEvent
        // Checks the keyword and level before emiting the event
        internal static void EasyTraceEvent(Keyword keywords, Level level, Event eventID)
        {
            if (IsEnabled(keywords, level))
            {
                EventProvider.TraceEvent(eventID, keywords, level);
            }
        }

        // EasyTraceEvent
        // Checks the keyword and level before emiting the event
        internal static void EasyTraceEvent<T1>(Keyword keywords, Event eventID, T1 param1)
        {
            if (IsEnabled(keywords, Level.Info))
            {
                EventProvider.TraceEvent(eventID, keywords, Level.Info, param1);
            }
        }

        // EasyTraceEvent
        // Checks the keyword and level before emiting the event
        internal static void EasyTraceEvent<T1>(Keyword keywords, Level level, Event eventID, T1 param1)
        {
            if (IsEnabled(keywords, level))
            {
                EventProvider.TraceEvent(eventID, keywords, level, param1);
            }
        }

        // EasyTraceEvent
        // Checks the keyword and level before emiting the event
        internal static void EasyTraceEvent<T1, T2>(Keyword keywords, Event eventID, T1 param1, T2 param2)
        {
            if (IsEnabled(keywords, Level.Info))
            {
                EventProvider.TraceEvent(eventID, keywords, Level.Info, param1, param2);
            }
        }

        internal static void EasyTraceEvent<T1, T2>(Keyword keywords, Level level, Event eventID, T1 param1, T2 param2)
        {
            if (IsEnabled(keywords, Level.Info))
            {
                EventProvider.TraceEvent(eventID, keywords, Level.Info, param1, param2);
            }
        }

        // EasyTraceEvent
        // Checks the keyword and level before emiting the event
        internal static void EasyTraceEvent<T1, T2, T3>(Keyword keywords, Event eventID, T1 param1, T2 param2, T3 param3)
        {
            if (IsEnabled(keywords, Level.Info))
            {
                EventProvider.TraceEvent(eventID, keywords, Level.Info, param1, param2, param3);
            }
        }

        #region Trace related enumerations

        public enum LayoutSource : byte
        {
            LayoutManager,
            HwndSource_SetLayoutSize,
            HwndSource_WMSIZE
        }

        #endregion

        /// <summary>
        /// Callers use this to check if they should be logging.
        /// </summary>
        internal static bool IsEnabled(Keyword flag, Level level)
        {
            // 比上游多一个 null 检查：本文件的 EventProvider 恒非 null，
            // 但把它写成 null-safe 就不会因为将来替换 Provider 而变成 NRE。
            return EventProvider != null && EventProvider.IsEnabled(flag, level);
        }

        /// <summary>
        /// Internal operations associated with initializing the event provider.
        ///
        /// **Linux 版**：没有 ETW，用 NullTraceProvider。
        /// 这与「Windows 上没有任何 ETW 会话订阅了 WPF provider」是同一条路径。
        /// </summary>
        static EventTrace()
        {
            EventProvider = new NullTraceProvider();
        }
    }

    /// <summary>
    /// Linux 上的 ETW 替代：明确的 no-op。
    ///
    /// `_enabled` 保持 false ⇒ 所有 `IsEnabled(...)` 返回 false ⇒
    /// 上游 506 处 `TraceEvent` 调用点全部被挡在门外，与上游在"无订阅者"
    /// 时的行为逐条一致。**没有任何日志被丢弃**，因为根本不存在日志通道。
    /// </summary>
    internal sealed class NullTraceProvider : TraceProvider
    {
        internal NullTraceProvider()
        {
            _enabled = false;
            _level = 0;
            _keywords = 0;
            _matchAllKeyword = 0;
        }

        internal override void Register(Guid providerGuid)
        {
            // 无 ETW，无注册。保留 _registrationHandle == 0（基类构造已置 0）。
        }

        internal override unsafe uint EventWrite(EventTrace.Event eventID, EventTrace.Keyword keywords,
                                                 EventTrace.Level level, int argc, EventData* argv)
        {
            // 不会到达（调用点都先过 IsEnabled）；返回 0 = ERROR_SUCCESS 的 no-op。
            return 0;
        }
    }
}
