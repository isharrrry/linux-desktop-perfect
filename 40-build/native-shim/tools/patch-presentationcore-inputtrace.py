#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""T1c/输入追踪应用器：给 **WM_CHAR → TextInput 这一腿**加只读插桩（缺省关、有界）。

    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-inputtrace.py --check
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-inputtrace.py          # 无参 = 应用

======================================================================================
【为什么要有它（主控 2026-09-11 派的判据工作）】
  症状：**打字到不了 TextBox**（键到了、字符没了、一个异常都不抛）。
  已钉死的两侧读数：
    · native shim 侧**完全正确**：`[KEY_DIAG]` 显示 WM_KEYDOWN + WM_CHAR 都产出了（'A'/'B'），
      三条 `DROP WM_CHAR` 也都是设计正确的（Ctrl 组合 / Shift 自身 / 释放事件）；
    · `WFP_MSGS … WM_CHAR=0` **不是**"shim 没产"：nativeshim 的 `[msg]` 打在 **dispatch 期**，
      而 WPF 在 **thread-preprocess 期**就把 WM_CHAR 吃掉了（`handled=true` ⇒ 不进 DispatchMessage）
      —— Windows 上同样读数是 0。
    · Ctrl+A 那条路是通的（`focus=True caret=0 selLen=7`）⇒ 坏的是 `WM_CHAR → TextInput` 这一腿。
  头号嫌疑：`HwndSource._eatCharMessages` **长期卡在 true** ⇒ 所有 WM_CHAR 被静默吃掉
  （键到了、字符没了、不抛异常 —— 与症状完全一致）。次嫌疑：Delayed 的
  `RestoreCharMessages`（Normal 优先级）在本移植里**没被调度到**，或 `TranslateChar` 吞了。
  ⇒ 本应用器把那三格**同时**变成可读：卡没卡、restore 跑没跑、哪一步把 handled 置了 true。

【打出来的东西（全部 `WPF_LINUX_INPUT_TRACE=1` 才打；每进程 ≤ 200 行；只打印）】
  1. `OnPreprocessMessage` 的 WM_CHAR 分支**入口**：`msg / hwnd / wParam(十进制码点) / _eatCharMessages / IsInExclusiveMenuMode`
  2. 三个子步骤**各自之后**的 `handled`：`TranslateChar` → `OnMnemonic` → `ProcessTextInputAction`
  3. `HwndKeyboardInputProvider.FilterMessage` 的 WM_KEYDOWN **入口与出口**：`_eatCharMessages` 前后值 + `handled` + `IsRepeatedKeyboardMessage`
  4. `RestoreCharMessages` 被调用时一行（"那条 Normal 优先级的 restore 到底跑没跑"）
  5. HwndSource 自己那条 WM_KEYDOWN：`_eatCharMessages` 置 true 前 / 复位后 各一行
  6. 提供方 FilterMessage 的 WM_CHAR 分支：`IsRepeatedKeyboardMessage` 结果 + `_eatCharMessages`（被它门住的那一格）

【纪律（本工程应用器家族的约定）】
  · 无参 = **真的应用**（写生成物 + csproj 接线）；`--check` 只读（未就位 rc=1、就位 rc=0）；
  · **幂等**：内容一致则不重写、csproj 已有 MARKER 就不再注入；
  · **锚点自检**：每处锚点必须**恰好命中期望次数**，否则**报错退出**（绝不静默产出未打补丁的副本）；
  · 生成物里的 `throw` 条数与上游**逐字相同**（只读插桩 ≠ 改行为）；大括号平衡同检；
  · **接线**：`Compile Remove` 上游 + `Compile Include` 生成物，整块插在 `Sdk.targets` 之前
    （`Remove` 必须落在上游 `Include` 之后才生效）。
"""

import argparse
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
PC_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "PresentationCore")
CSPROJ = os.path.join(PC_DIR, "PresentationCore.Linux.csproj")

UP_REL_HS = ("src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/InterOp/HwndSource.cs")
UP_REL_HK = ("src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/InterOp/"
             "HwndKeyboardInputProvider.cs")
UPSTREAM_HS = os.path.join(ROOT, UP_REL_HS)
UPSTREAM_HK = os.path.join(ROOT, UP_REL_HK)
GEN_HS = os.path.join(PC_DIR, "HwndSource.Linux.cs")
GEN_HK = os.path.join(PC_DIR, "HwndKeyboardInputProvider.Linux.cs")

MARKER_BEGIN = ("  <!-- ==== WPF-on-Linux T1c：输入链只读插桩（WM_CHAR→TextInput）"
                "（由 src/Linux/tools/patch-presentationcore-inputtrace.py 注入）==== -->")
MARKER_END = "  <!-- ==== WPF-on-Linux T1c 输入插桩 结束 ==== -->"

# =====================================================================================
#  插桩类本体（放进 HwndSource.Linux.cs，命名空间内；提供方文件引用同一个类）
# =====================================================================================
TRACE_CLASS = '''    /// <summary>
    /// T1c · 输入链的**只读**插桩（缺省关）：`WPF_LINUX_INPUT_TRACE=1` 才打，每进程最多
    /// <see cref="MaxLines"/> 行。**只打印**：所有调用点都在"原语句之后"读一眼，不改任何控制流、
    /// 不改任何返回值、不碰 `RenderDiagnostics`。
    ///
    /// 【它要回答的三格】① `_eatCharMessages` 是不是**卡在 true**（⇒ WM_CHAR 被静默吃掉）；
    /// ② 那条 Normal 优先级的 `RestoreCharMessages` **跑没跑**；③ `TranslateChar/OnMnemonic/
    /// ProcessTextInputAction` 里**哪一步**把 `handled` 置成了 true。
    /// </summary>
    internal static class WpfLinuxInputTrace
    {
        /// <summary>开关名。**未设/空白 ⇒ 关**（纯函数 <see cref="IsOn"/>）。</summary>
        internal const string EnvVar = "WPF_LINUX_INPUT_TRACE";

        /// <summary>有界：每进程最多打这么多行（输入路径高频，必须有界）。</summary>
        private const int MaxLines = 200;

        private static readonly bool s_enabled = IsOn(Environment.GetEnvironmentVariable(EnvVar));

        /// <summary>**尝试**打印的次数（用于封顶）。</summary>
        private static int s_attempts;
        private static int s_budgetNotice;   // L12：触顶只报一次

        /// <summary>**实际打出去**的行数 —— <see cref="LineCount"/> 报的就是它。</summary>
        private static int s_emitted;

        internal static bool Enabled { get { return s_enabled; } }

        /// <summary>
        /// **实际打出去的行数**（探针/复验可读；缺省关时恒 0）。
        /// ⚠️ 这里踩过一次：早先它记的是"调用次数"，于是在连打 250 次后报 **261**，
        ///    而真正写出去的是 200 行（封顶本身没错，**是读数会骗人** ⇒ 探针把它抓了出来）。
        ///    现在它只在实际写行时自增 ⇒ 读数 == 输出行数 ≤ MaxLines。
        /// </summary>
        internal static int LineCount { get { return s_emitted; } }

        /// <summary>尝试次数（诊断用：> MaxLines 说明发生过封顶）。</summary>
        internal static int AttemptCount { get { return s_attempts; } }

        /// <summary>未设/空白 ⇒ **false**；"1"/"true"/"on"/"yes"（不分大小写）⇒ true；其余 ⇒ false。</summary>
        internal static bool IsOn(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            string v = value.Trim();
            if (v == "1") return true;
            if (string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(v, "on", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void Emit(string message)
        {
            if (!s_enabled) return;
            if (System.Threading.Interlocked.Increment(ref s_attempts) > MaxLines)   // 封顶：最多尝试 MaxLines 次
            {
                if (System.Threading.Interlocked.Exchange(ref s_budgetNotice, 1) == 0)
                {
                    try
                    {
                        System.Console.Error.WriteLine("[INPUT_TRACE] **预算用尽**（MaxLines=" + MaxLines
                            + "）⇒ 之后不再打印；**「没打」≠「没发生」**：请调大额度或缩小过滤范围");
                        System.Console.Error.Flush();
                    }
                    catch (System.Exception) { }
                }
                return;
            }

            try
            {
                Console.Error.WriteLine("[INPUT_TRACE] " + message);
                Console.Error.Flush();
                System.Threading.Interlocked.Increment(ref s_emitted);                        // **只在实际写出去时**自增
            }
            catch (Exception)
            {
            }
        }

        private static string Hex(IntPtr v) { return "0x" + v.ToInt64().ToString("x"); }

        /// <summary>
        /// ⓪ **所有早退门之前**（即 `if (!HasFocusWithin() && !IsInExclusiveMenuMode) return;` **之前**）。
        /// 只对四个 char 消息打（否则每条消息都打会把 200 行预算炸掉）。
        /// `HasFocusWithin()` **惰性**求值：把 sink 传进来、只在插桩开着时才问 ⇒ 关掉时零额外代价、语义不变。
        /// 为什么非要这一格：`OnPreprocessMessage` 在 switch **之前**就有早退门，所以后面那条
        /// "WM_CHAR 入口"探针的**没打印**有三种含义 ——
        ///   (a) 消息根本没到 OnPreprocessMessage；(b) 到了、被焦点门挡回去了；(c) 到了、没进 switch 的 char 分支。
        /// 有了这一格：(a) 与 (b/c) 立刻分开（本格命中而入口格不命中 ⇒ 就是被焦点门挡的，实锤）。
        /// </summary>
        internal static void PreprocessEarlyGate(int msg, IntPtr hwnd, IntPtr wParam,
                                                 bool eatCharMessages, bool menuMode, IKeyboardInputSink sink)
        {
            if (!s_enabled) return;
            bool isChar = (msg == 0x0102 || msg == 0x0106 || msg == 0x0103 || msg == 0x0107);
            if (!isChar) return;                     // WM_CHAR / WM_SYSCHAR / WM_DEADCHAR / WM_SYSDEADCHAR
            bool hasFocus = false;
            try { hasFocus = (sink != null) && sink.HasFocusWithin(); } catch (System.Exception) { }
            Emit("PreprocessMessage **早退门之前** msg=0x" + msg.ToString("x") + " hwnd=" + Hex(hwnd)
                 + " wParam=" + wParam.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture)
                 + " HasFocusWithin=" + hasFocus + " IsInExclusiveMenuMode=" + menuMode
                 + " _eatCharMessages=" + eatCharMessages);
        }

        /// <summary>① WM_CHAR 分支**入口（过焦点门后）**（注意：在 `if(!_eatCharMessages)` **之外**读，才看得到"卡住"）。</summary>
        internal static void PreprocessCharEntry(IntPtr hwnd, int msg, IntPtr wParam, bool eatCharMessages, bool menuMode)
        {
            Emit("PreprocessMessage WM_CHAR 入口(**过焦点门后**) hwnd=" + Hex(hwnd) + " msg=0x" + msg.ToString("x")
                 + " wParam=" + wParam.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture)
                 + " _eatCharMessages=" + eatCharMessages + " IsInExclusiveMenuMode=" + menuMode);
        }

        /// <summary>② 每个子步骤**之后**的 handled（哪一步把它置 true 就打到那一步）。</summary>
        internal static void PreprocessCharStep(string step, bool handled)
        {
            Emit("PreprocessMessage WM_CHAR 步骤 " + step + " ⇒ handled=" + handled);
        }

        /// <summary>①' HwndSource 自己那条 WM_KEYDOWN：置 true 前 / 复位后。</summary>
        internal static void SourceKeyDown(bool eatCharMessages, bool handled, string stage)
        {
            Emit("HwndSource WM_KEYDOWN " + stage + " _eatCharMessages=" + eatCharMessages + " handled=" + handled);
        }

        /// <summary>④ `RestoreCharMessages` 被调用（"那条 Normal 优先级的 restore 到底跑没跑"）。</summary>
        internal static void RestoreCharMessagesCalled()
        {
            Emit("RestoreCharMessages 被调用（_eatCharMessages 将被置 false）");
        }

        /// <summary>③ 提供方 FilterMessage 的 WM_KEYDOWN 入口/出口。</summary>
        internal static void ProviderKeyDown(string stage, bool eatCharMessages, bool handled, bool repeated)
        {
            Emit("FilterMessage WM_KEYDOWN " + stage + " _eatCharMessages=" + eatCharMessages
                 + " handled=" + handled + " IsRepeatedKeyboardMessage=" + repeated);
        }

        /// <summary>③' 提供方 FilterMessage 的 WM_KEYDOWN 复位后（`_eatCharMessages` 是否被清掉）。</summary>
        internal static void ProviderKeyDownReset(bool eatCharMessages, bool handled)
        {
            Emit("FilterMessage WM_KEYDOWN 复位后 _eatCharMessages=" + eatCharMessages + " handled=" + handled);
        }

        // ══════════════════════════════════════════════════════════════════════════════════
        //  第 2 批（P1…P5）：`ProcessTextInputAction` **之后**发生了什么
        //
        //  第 1 批的实跑读数已把范围钉死：char 到了 OnPreprocessMessage、`_eatCharMessages`
        //  **所有描点都是 False**、三步都没置 handled，最后 **ProcessTextInputAction ⇒ handled=True**
        //  而 TextBox 文本没变 ⇒ "置位/吞掉/复位"那套判据**不成立**（已在报告里撤回）。
        //  第 2 批只回答一件事：**`TextInput` 到底有没有被 raise、raise 给了谁**。
        //
        //  预算**独立**于第 1 批（第 1 批那 200 行吃不到它）：Text 类报告**必打**，
        //  其它类报告只采样前 20 行（鼠标每帧都来，否则会把预算吃光 —— 上一轮的教训）。
        //  只对 Text 类打印的地方在注释里写明"只对 Text"。
        //  ⚠️ 本类在 `System.Windows.Interop`；**其它命名空间的文件必须全限定**
        //     （`System.Windows.Interop.WpfLinuxInputTrace.…`），否则编译不过。
        // ══════════════════════════════════════════════════════════════════════════════════
        private const int MaxB2Lines = 200;        // 第 2 批硬上限
        private const int MaxB2OtherLines = 20;    // 非 Text 类报告采样行数

        private static int s_b2Lines;
        private static int s_b2OtherLines;
        private static int s_b2TextSeen;
        private static int s_b2OtherSeen;
        private static int s_b2OtherNotice;

        /// <summary>第 2 批实际输出行数（读数 == 输出行数）。</summary>
        internal static int B2LineCount { get { return s_b2Lines; } }

        private static void EmitB2(string message)
        {
            if (!s_enabled) return;
            if (System.Threading.Interlocked.Increment(ref s_b2Lines) > MaxB2Lines) return;
            try
            {
                System.Console.Error.WriteLine("[INPUT_TRACE] " + message);
                System.Console.Error.Flush();
            }
            catch (System.Exception)
            {
                System.Threading.Interlocked.Decrement(ref s_b2Lines);
            }
        }

        private static string Chr2(int c)
        {
            if (c < 0x20 || c > 0x10FFFF) return "";
            return " ch=U+" + c.ToString("x4");
        }

        private static string TypeOf(object o)
        {
            if (o == null) return "(null)";
            try
            {
                return o.GetType().Name + "#"
                     + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o).ToString("x");
            }
            catch (System.Exception) { return "(读不出来)"; }
        }

        /// <summary>`InputReport` 的读数：类型名 + `Type`（Text / Mouse / Keyboard…）。</summary>
        private static string ReportOf(object inputReport)
        {
            System.Windows.Input.InputReport r = inputReport as System.Windows.Input.InputReport;
            if (r == null) return "(null)";
            try { return r.GetType().Name + "/Type=" + r.Type; }
            catch (System.Exception) { return r.GetType().Name + "/Type=(读不出来)"; }
        }

        private static bool B2IsTextReport(object inputReport)
        {
            System.Windows.Input.InputReport r = inputReport as System.Windows.Input.InputReport;
            if (r == null) return false;
            try { return r.Type == System.Windows.Input.InputType.Text; }
            catch (System.Exception) { return false; }
        }

        /// <summary>预算闸：Text 类必打；其它类采样 MaxB2OtherLines 行（封顶时打一行通知，只打一次）。</summary>
        private static bool B2Allow(object inputReport)
        {
            if (B2IsTextReport(inputReport))
            {
                System.Threading.Interlocked.Increment(ref s_b2TextSeen);
                return true;
            }
            System.Threading.Interlocked.Increment(ref s_b2OtherSeen);
            if (System.Threading.Interlocked.Increment(ref s_b2OtherLines) <= MaxB2OtherLines) return true;
            if (System.Threading.Interlocked.Exchange(ref s_b2OtherNotice, 1) == 0)
            {
                EmitB2("第 2 批：非 Text 报告明细已封顶(" + MaxB2OtherLines + " 行) ⇒ 之后**只打 Text 类**"
                       + "（累计已见 Text " + s_b2TextSeen + " 条 / 非 Text " + s_b2OtherSeen + " 条）");
            }
            return false;
        }

        /// <summary>P1 `InputProviderSite.ReportInput`：报告类型 + 返回的 handled + `_inputManager` 有没有。</summary>
        internal static void B2ReportInput(object inputReport, bool handled, object inputManager)
        {
            if (!s_enabled) return;
            if (!B2Allow(inputReport)) return;
            EmitB2("P1 InputProviderSite.ReportInput 报告=" + ReportOf(inputReport)
                   + " ⇒ 返回 handled=" + handled
                   + " _inputManager=" + (inputManager == null ? "(null)" : "有"));
        }

        /// <summary>P2 `InputManager.ProcessInput` 入口：报告类型 + RoutedEvent + Source + **焦点在谁身上**。</summary>
        internal static void B2ProcessInputEntry(object input, object keyboardDevice)
        {
            if (!s_enabled) return;
            object report = null;
            string routedEvent = "(非 InputReportEventArgs)";
            string source = "(n/a)";
            System.Windows.Input.InputReportEventArgs ire =
                input as System.Windows.Input.InputReportEventArgs;
            if (ire != null)
            {
                report = ire.Report;
                routedEvent = (ire.RoutedEvent == null) ? "(null)" : ire.RoutedEvent.Name;
                source = TypeOf(ire.Source);
            }
            if (!B2Allow(report)) return;

            string focused = "(null)";
            System.Windows.Input.KeyboardDevice dev =
                keyboardDevice as System.Windows.Input.KeyboardDevice;
            if (dev != null) focused = TypeOf(dev.FocusedElement);

            EmitB2("P2 InputManager.ProcessInput 入口 报告=" + ReportOf(report)
                   + " RoutedEvent=" + routedEvent + " Source=" + source
                   + " KeyboardDevice.FocusedElement=" + focused);
        }

        /// <summary>P2' `InputManager.ProcessInput` 返回（**只对 Text 类**打，非 Text 在入口已采样过）。</summary>
        internal static void B2ProcessInputResult(object input, bool handled)
        {
            if (!s_enabled) return;
            System.Windows.Input.InputReportEventArgs ire =
                input as System.Windows.Input.InputReportEventArgs;
            object report = (ire == null) ? null : ire.Report;
            if (!B2IsTextReport(report)) return;
            EmitB2("P2' ProcessInput 返回 handled=" + handled + " 报告=" + ReportOf(report));
        }

        /// <summary>
        /// P3a `TextCompositionManager` 的 "Raw to StartComposition" 那段**条件之前**：
        /// 报告类型 + RoutedEvent（**条件要求 `Type=Text` 且 `RoutedEvent=InputReport`**）。
        /// **只对 Text 类**打（这段每帧都会被别的报告经过）。
        /// </summary>
        internal static void B2RawReport(object input)
        {
            if (!s_enabled) return;
            System.Windows.Input.InputReportEventArgs ire =
                input as System.Windows.Input.InputReportEventArgs;
            object report = (ire == null) ? null : ire.Report;
            if (!B2IsTextReport(report)) return;
            string routedEvent = (ire.RoutedEvent == null) ? "(null)" : ire.RoutedEvent.Name;
            EmitB2("P3a TextCompositionManager 到达 Raw 段 报告=" + ReportOf(report)
                   + " RoutedEvent=" + routedEvent + " Source=" + TypeOf(ire.Source)
                   + "（条件= Type==Text && RoutedEvent==InputReport）");
        }

        /// <summary>P3b **进了** `Raw → StartComposition` 分支体（码点 + 三个标志 + 目标元素）。</summary>
        internal static void B2RawBranch(object rawTextInput, object processArgs)
        {
            if (!s_enabled) return;
            // **实参惰性**：调用点只传 `e`；`e.StagingItem.Input.Source` 这条链在这里取（try/catch 包住）
            object stagingSource = "(取不到)";
            try
            {
                System.Windows.Input.ProcessInputEventArgs pia =
                    processArgs as System.Windows.Input.ProcessInputEventArgs;
                if (pia != null && pia.StagingItem != null && pia.StagingItem.Input != null)
                    stagingSource = pia.StagingItem.Input.Source;
            }
            catch (System.Exception) { stagingSource = "(读 Source 抛)"; }
            System.Windows.Input.RawTextInputReport t =
                rawTextInput as System.Windows.Input.RawTextInputReport;
            if (t == null)
            {
                EmitB2("P3b 进了 Raw→StartComposition 分支，但 Report 不是 RawTextInputReport："
                       + TypeOf(rawTextInput));
                return;
            }
            EmitB2("P3b 进了 Raw→StartComposition 分支 码点=" + ((int)t.CharacterCode)
                   + Chr2((int)t.CharacterCode)
                   + " IsControlCharacter=" + t.IsControlCharacter
                   + " IsDeadCharacter=" + t.IsDeadCharacter
                   + " IsSystemCharacter=" + t.IsSystemCharacter
                   + " StagingItem.Source=" + TypeOf(stagingSource));
        }

        /// <summary>P4a 正常字符分支新建出来的 `TextComposition`：文本 + **目标元素（Source）**。</summary>
        internal static void B2CompositionCreated(object composition)
        {
            if (!s_enabled) return;
            System.Windows.Input.TextComposition c =
                composition as System.Windows.Input.TextComposition;
            if (c == null) { EmitB2("P4a 新建的 composition 为 null"); return; }
            string text = "(读不出来)";
            try { text = "\"" + c.Text + "\""; } catch (System.Exception) { }
            string src = "(读不出来)";
            try { src = TypeOf(c.Source); } catch (System.Exception) { }
            EmitB2("P4a 新建 TextComposition Text=" + text + " 目标(Source)=" + src);
        }

        /// <summary>P4b `UnsafeStartComposition` 的**返回值**（handled）。</summary>
        internal static void B2StartComposition(string stage, bool returned, object composition)
        {
            if (!s_enabled) return;
            System.Windows.Input.TextComposition c =
                composition as System.Windows.Input.TextComposition;
            string text = "(null)";
            try { if (c != null) text = "\"" + c.Text + "\""; } catch (System.Exception) { }
            EmitB2("P4b UnsafeStartComposition(" + stage + ") 返回 handled=" + returned + " Text=" + text);
        }

        /// <summary>
        /// P5 **`RaiseEvent` 之前**：事件名 + **目标元素（eventSource）** + `Source`。
        /// `TextInput`/`PreviewTextInput` **必打**；其它事件只采样（每个输入事件都会经过这里）。
        /// </summary>
        internal static void B2RaiseInput(object input, object eventSource)
        {
            if (!s_enabled) return;
            System.Windows.Input.InputEventArgs ie = input as System.Windows.Input.InputEventArgs;
            string ev = (ie == null || ie.RoutedEvent == null) ? "(null)" : ie.RoutedEvent.Name;
            bool isTextEvent = (ev == "TextInput" || ev == "PreviewTextInput");
            if (!isTextEvent)
            {
                if (System.Threading.Interlocked.Increment(ref s_b2OtherLines) > MaxB2OtherLines) return;
            }
            EmitB2("P5 RaiseEvent 之前 事件=" + ev + " 目标(eventSource)=" + TypeOf(eventSource)
                   + " Source=" + ((ie == null) ? "(null)" : TypeOf(ie.Source))
                   + (isTextEvent ? "" : "（非文本事件·采样）"));
        }

        /// <summary>P5' `RaiseEvent` **之后**的 `Handled`（**只对文本事件**打）——"raise 了有没有人接"。</summary>
        internal static void B2RaiseInputResult(object input, bool handled)
        {
            if (!s_enabled) return;
            System.Windows.Input.InputEventArgs ie = input as System.Windows.Input.InputEventArgs;
            string ev = (ie == null || ie.RoutedEvent == null) ? "(null)" : ie.RoutedEvent.Name;
            if (ev != "TextInput" && ev != "PreviewTextInput") return;
            EmitB2("P5' RaiseEvent 之后 事件=" + ev + " Handled=" + handled);
        }

        /// <summary>⑥ 提供方 FilterMessage 的 WM_CHAR 分支（重复消息判定 + 被 `_eatCharMessages` 门住的那一格）。</summary>
        internal static void ProviderChar(string stage, bool repeated, bool eatCharMessages, bool handled)
        {
            Emit("FilterMessage WM_CHAR " + stage + " IsRepeatedKeyboardMessage=" + repeated
                 + " _eatCharMessages=" + eatCharMessages + " handled=" + handled);
        }
    }

'''

# =====================================================================================
#  HwndSource.cs 的 6 处锚点
# =====================================================================================
HS_CLASS_ANCHOR = ('    public class HwndSource : PresentationSource, IDisposable, IWin32Window, '
                   'IKeyboardInputSink\n')

# ⓪ 早退门（`HasFocusWithin`）**之前**的锚点：逐字含上面三行注释，保证命中唯一（`HasFocusWithin` 全文件 1 次）。
HS_EARLYGATE_ANCHOR = '''            // Mnemonics are broadcast to all branches of the window tree; even
            // those that don't have focus.  BUT! at least someone under this
            // top-level window must have focus.
            if (!((IKeyboardInputSink)this).HasFocusWithin() && !IsInExclusiveMenuMode)
'''
HS_CHAR_ANCHOR = '''            case WindowMessage.WM_CHAR:
            case WindowMessage.WM_SYSCHAR:
            case WindowMessage.WM_DEADCHAR:
            case WindowMessage.WM_SYSDEADCHAR:
                // MITIGATION: HANDLED_KEYDOWN_STILL_GENERATES_CHARS
                if(!_eatCharMessages)
                {
'''

HS_TRANSLATE_ANCHOR = ('                    msgdata.handled = ((IKeyboardInputSink)this)'
                       '.TranslateChar(ref msgdata.msg, modifierKeys);\n')

HS_MNEMONIC_ANCHOR = ('                        msgdata.handled = ((IKeyboardInputSink)this)'
                      '.OnMnemonic(ref msgdata.msg, modifierKeys);\n')

HS_TEXTAction_ANCHOR = ('                        _keyboard.ProcessTextInputAction(msgdata.msg.hwnd, '
                        '(WindowMessage)msgdata.msg.message,\n'
                        '                                                               msgdata.msg.wParam,'
                        ' msgdata.msg.lParam, ref msgdata.handled);\n')

HS_KEYDOWN_SET_ANCHOR = '''                _eatCharMessages = true;
                DispatcherOperation restoreCharMessages = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new DispatcherOperationCallback(RestoreCharMessages), null);
'''

HS_KEYDOWN_RESET_ANCHOR = '''                    _eatCharMessages = false;
                    restoreCharMessages.Abort();
                }
'''

HS_RESTORE_ANCHOR = '''        internal static object RestoreCharMessages(object unused)
        {
            _eatCharMessages = false;
'''

# =====================================================================================
#  HwndKeyboardInputProvider.cs 的 3 处锚点
# =====================================================================================
HK_KEYDOWN_ENTRY_ANCHOR = '''                    if(_source.IsRepeatedKeyboardMessage(hwnd, (int)message, wParam, lParam))
                    {
                        break;
                    }

                    // We will use the current time before generating KeyDown events so we can filter
                    // the later posted WM_CHAR.
                    int currentTime = 0;
'''

HK_KEYDOWN_RESET_ANCHOR = '''                    if(!handled)
                    {
                        // MITIGATION: HANDLED_KEYDOWN_STILL_GENERATES_CHARS
                        // We did not handle the WM_KEYDOWN, so it is OK to process WM_CHAR messages.
                        // We can also abort the pending restore operation since we don't need it.
                        HwndSource._eatCharMessages = false;
                        restoreCharMessages.Abort();
                    }
'''

HK_CHAR_ANCHOR = '''                    if(_source.IsRepeatedKeyboardMessage(hwnd, (int)message, wParam, lParam))
                    {
                        break;
                    }

                    // MITIGATION: HANDLED_KEYDOWN_STILL_GENERATES_CHARS
                    if(HwndSource._eatCharMessages)
                    {
                        break;
                    }

                    ProcessTextInputAction(hwnd, message, wParam, lParam, ref handled);
'''

EDITS_HS = [
    ("H1 插桩类本体", HS_CLASS_ANCHOR, TRACE_CLASS + HS_CLASS_ANCHOR),
    ("H0 早退门之前（三态可分的那一格）", HS_EARLYGATE_ANCHOR,
     '            // ── T1c 只读插桩：**在所有早退门之前**（没到 / 到了被焦点门挡 / 到了没进 switch —— 三态可分）──\n'
     '            WpfLinuxInputTrace.PreprocessEarlyGate(msgdata.msg.message, msgdata.msg.hwnd, msgdata.msg.wParam,\n'
     '                                                   _eatCharMessages, IsInExclusiveMenuMode, (IKeyboardInputSink)this);\n\n'
     + HS_EARLYGATE_ANCHOR),
    ("H2 WM_CHAR 入口（过焦点门后）", HS_CHAR_ANCHOR, HS_CHAR_ANCHOR.replace(
        '''                // MITIGATION: HANDLED_KEYDOWN_STILL_GENERATES_CHARS
                if(!_eatCharMessages)''',
        '''                // ── T1c 只读插桩：**在 if 之外**读，才看得到"卡住" ──
                WpfLinuxInputTrace.PreprocessCharEntry(msgdata.msg.hwnd, msgdata.msg.message, msgdata.msg.wParam,
                                                       _eatCharMessages, IsInExclusiveMenuMode);

                // MITIGATION: HANDLED_KEYDOWN_STILL_GENERATES_CHARS
                if(!_eatCharMessages)''')),
    ("H3 TranslateChar 之后", HS_TRANSLATE_ANCHOR,
     HS_TRANSLATE_ANCHOR + '                    WpfLinuxInputTrace.PreprocessCharStep("TranslateChar", msgdata.handled);\n'),
    ("H4 OnMnemonic 之后", HS_MNEMONIC_ANCHOR,
     HS_MNEMONIC_ANCHOR + '                        WpfLinuxInputTrace.PreprocessCharStep("OnMnemonic", msgdata.handled);\n'),
    ("H5 ProcessTextInputAction 之后", HS_TEXTAction_ANCHOR,
     HS_TEXTAction_ANCHOR + '                        WpfLinuxInputTrace.PreprocessCharStep("ProcessTextInputAction", msgdata.handled);\n'),
    ("H6 KEYDOWN 置 true", HS_KEYDOWN_SET_ANCHOR,
     '                WpfLinuxInputTrace.SourceKeyDown(_eatCharMessages, msgdata.handled, "置true前");\n'
     + HS_KEYDOWN_SET_ANCHOR),
    ("H7 KEYDOWN 复位", HS_KEYDOWN_RESET_ANCHOR,
     HS_KEYDOWN_RESET_ANCHOR.replace('''                    _eatCharMessages = false;
                    restoreCharMessages.Abort();''',
     '''                    _eatCharMessages = false;
                    restoreCharMessages.Abort();
                    WpfLinuxInputTrace.SourceKeyDown(_eatCharMessages, msgdata.handled, "复位后");''')),
    ("H8 RestoreCharMessages", HS_RESTORE_ANCHOR, HS_RESTORE_ANCHOR + '            WpfLinuxInputTrace.RestoreCharMessagesCalled();\n'),
]

EDITS_HK = [
    ("K1 KEYDOWN 入口", HK_KEYDOWN_ENTRY_ANCHOR, HK_KEYDOWN_ENTRY_ANCHOR.replace(
        '''                    // We will use the current time before generating KeyDown events so we can filter''',
        '''                    // ── T1c 只读插桩：入口（重复判定已过）──
                    WpfLinuxInputTrace.ProviderKeyDown("入口", HwndSource._eatCharMessages, handled, false);

                    // We will use the current time before generating KeyDown events so we can filter''')),
    ("K2 KEYDOWN 复位后", HK_KEYDOWN_RESET_ANCHOR, HK_KEYDOWN_RESET_ANCHOR + '''                    WpfLinuxInputTrace.ProviderKeyDownReset(HwndSource._eatCharMessages, handled);
'''),
    ("K3 WM_CHAR 分支", HK_CHAR_ANCHOR, HK_CHAR_ANCHOR.replace(
        '''                    // MITIGATION: HANDLED_KEYDOWN_STILL_GENERATES_CHARS
                    if(HwndSource._eatCharMessages)
                    {
                        break;
                    }''',
        '''                    WpfLinuxInputTrace.ProviderChar("入口", false, HwndSource._eatCharMessages, handled);

                    // MITIGATION: HANDLED_KEYDOWN_STILL_GENERATES_CHARS
                    if(HwndSource._eatCharMessages)
                    {
                        WpfLinuxInputTrace.ProviderChar("被 _eatCharMessages 门住 ⇒ 丢弃", false, true, handled);
                        break;
                    }''').replace(
        '''                    ProcessTextInputAction(hwnd, message, wParam, lParam, ref handled);''',
        '''                    ProcessTextInputAction(hwnd, message, wParam, lParam, ref handled);
                    WpfLinuxInputTrace.ProviderChar("ProcessTextInputAction 之后", false, false, handled);''')),
]

REQUIRED = [
    ("插桩类存在", "internal static class WpfLinuxInputTrace"),
    ("开关名", 'internal const string EnvVar = "WPF_LINUX_INPUT_TRACE";'),
    ("缺省关（纯函数首行）", "if (string.IsNullOrWhiteSpace(value)) return false;"),
    ("有界 200 行", "private const int MaxLines = 200;"),
    ("读数 == 实际输出行数（LineCount 只在实际写行时自增）",
     "System.Threading.Interlocked.Increment(ref s_emitted);                        // **只在实际写出去时**自增"),
    ("⓪ 早退门之前（三态可分）", "WpfLinuxInputTrace.PreprocessEarlyGate("),
    ("① WM_CHAR 入口（if 之外，过焦点门后）", "WpfLinuxInputTrace.PreprocessCharEntry("),
    ("② TranslateChar 步骤", 'WpfLinuxInputTrace.PreprocessCharStep("TranslateChar"'),
    ("② OnMnemonic 步骤", 'WpfLinuxInputTrace.PreprocessCharStep("OnMnemonic"'),
    ("② ProcessTextInputAction 步骤", 'WpfLinuxInputTrace.PreprocessCharStep("ProcessTextInputAction"'),
    ("③ 提供方 KEYDOWN 入口", 'WpfLinuxInputTrace.ProviderKeyDown("入口"'),
    ("③ 提供方 KEYDOWN 复位后", "WpfLinuxInputTrace.ProviderKeyDownReset("),
    ("④ RestoreCharMessages", "WpfLinuxInputTrace.RestoreCharMessagesCalled();"),
    ("⑤ HwndSource KEYDOWN 置 true 前", '"置true前"'),
    ("⑤ HwndSource KEYDOWN 复位后", '"复位后"'),
    ("⑥ 提供方 WM_CHAR 门住那一格", "被 _eatCharMessages 门住 ⇒ 丢弃"),
    # ── 第 2 批（P1…P5）的 helper **定义**必须都在 HwndSource.Linux.cs 里 ----
    #    （调用点在 InputProviderSite / InputManager / TextCompositionManager 三个**别**的生成物里，
    #      由各自的 applier 负责；本 applier 只保证"类里确实有这些方法"）
    ("第 2 批独立预算（第 1 批 200 行吃不到它）", "private const int MaxB2Lines = 200;"),
    ("第 2 批：Text 类必打 / 其它类采样", "private const int MaxB2OtherLines = 20;"),
    ("第 2 批 P1 ReportInput", "internal static void B2ReportInput(object inputReport, bool handled, object inputManager)"),
    ("第 2 批 P2 ProcessInput 入口（含 FocusedElement）", "internal static void B2ProcessInputEntry(object input, object keyboardDevice)"),
    ("第 2 批 P2' ProcessInput 返回", "internal static void B2ProcessInputResult(object input, bool handled)"),
    ("第 2 批 P3a Raw 段条件之前", "internal static void B2RawReport(object input)"),
    ("第 2 批 P3b 分支体（码点+三标志；**实参惰性**：传 e，链在 helper 内取）", "internal static void B2RawBranch(object rawTextInput, object processArgs)"),
    ("第 2 批 P4a 新建 composition", "internal static void B2CompositionCreated(object composition)"),
    ("第 2 批 P4b StartComposition 返回", "internal static void B2StartComposition(string stage, bool returned, object composition)"),
    ("第 2 批 P5 RaiseEvent 之前（目标元素）", "internal static void B2RaiseInput(object input, object eventSource)"),
    ("第 2 批 P5' RaiseEvent 之后", "internal static void B2RaiseInputResult(object input, bool handled)"),
    ("跨命名空间必须全限定（否则编译不过）",
     "**其它命名空间的文件必须全限定**"),
]

HEADER = """// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-presentationcore-inputtrace.py **生成**，不要手改。
//
// 内容 = 上游 `upstream/wpf/{rel}` 逐字复制 + {n} 处 T1c **只读插桩**（输入链：WM_CHAR → TextInput）。
// 每次运行该脚本都会从上游重读重生成；锚点找不到 / 命中数不符时**报错退出**，不会静默产出未打补丁的副本。
//
// 为什么打：**打字到不了 TextBox**。头号嫌疑是 `HwndSource._eatCharMessages` 卡在 true
//   （WM_CHAR 被静默吃掉：键到了、字符没了、不抛异常），次嫌疑是 Normal 优先级的
//   `RestoreCharMessages` 没被调度到、或 `TranslateChar` 吞了。
//   插桩**缺省关**（`WPF_LINUX_INPUT_TRACE` 未设 ⇒ 一行不打）、**有界**（≤200 行/进程）、
//   **只打印**（所有调用点都在原语句之后读一眼，不改控制流、不改返回值）。
"""


def _count(haystack, needle):
    return haystack.count(needle)


def _gen_one(upstream_path, rel, edits, check_only, label):
    if not os.path.exists(upstream_path):
        print(f"[失败] 找不到上游 {upstream_path}")
        return None, 1
    with open(upstream_path, encoding="utf-8-sig") as f:
        text = f.read()

    bad = False
    for name, anchor, _ in edits:
        n = _count(text, anchor)
        print(f"[锚点] {label} {name}：上游出现 {n} 次（要求 1）")
        if n != 1:
            bad = True
    if bad:
        print(f"[失败] {label} 锚点缺失或重复 —— 上游这段改过了？**不做任何静默降级**。")
        return None, 1

    out = text
    for _, anchor, repl in edits:
        out = out.replace(anchor, repl, 1)

    up_throws, out_throws = _count(text, "throw "), _count(out, "throw ")
    if up_throws != out_throws:
        print(f"[失败] {label} `throw` 条数变了：上游 {up_throws} → 生成物 {out_throws}")
        return None, 1

    # 结构自检：**大括号盈亏**必须不变（插桩类本身带大括号 ⇒ 不能比"绝对条数"）
    if (_count(text, "{") - _count(text, "}")) != (_count(out, "{") - _count(out, "}")):
        print(f"[失败] {label} 大括号盈亏变化（只读插桩不该改结构）："
              f"上游 {_count(text, '{')}/{_count(text, '}')} → 生成物 {_count(out, '{')}/{_count(out, '}')}")
        return None, 1
    print(f"[断言] {label} 大括号盈亏一致：{_count(out, '{')} / {_count(out, '}')}")

    print(f"[断言] {label} `throw` {up_throws}=={out_throws}；行数 {len(text.splitlines())} → "
          f"{len(out.splitlines())}（+{len(out.splitlines()) - len(text.splitlines())}）")
    return HEADER.replace("{rel}", rel).replace("{n}", str(len(edits))) + out, 0


def generate(check_only):
    gen_hs, rc = _gen_one(UPSTREAM_HS, UP_REL_HS, EDITS_HS, check_only, "HwndSource")
    if rc:
        return rc
    gen_hk, rc = _gen_one(UPSTREAM_HK, UP_REL_HK, EDITS_HK, check_only, "HwndKeyboardInputProvider")
    if rc:
        return rc

    # ---- 结构断言：两个生成物合起来必须含全部插桩点 ----
    both = gen_hs + gen_hk
    for name, needle in REQUIRED:
        if needle not in both:
            print(f"[失败] 生成物缺少结构断言：{name}")
            return 1
    print("[断言] 结构断言 %d/%d 全中（含「缺省关」与「有界」）" % (len(REQUIRED), len(REQUIRED)))

    outputs = [(GEN_HS, gen_hs), (GEN_HK, gen_hk)]
    up_to_date = True
    for path, content in outputs:
        cur = None
        if os.path.exists(path):
            with open(path, encoding="utf-8") as f:
                cur = f.read()
        if cur != content:
            up_to_date = False
            if not check_only:
                with open(path, "w", encoding="utf-8") as f:
                    f.write(content)
                print(f"[生成] {os.path.relpath(path, ROOT)}：已从上游重生成（只读插桩）")
        else:
            print(f"[生成] {os.path.relpath(path, ROOT)}：内容已是最新（未重写）")

    # ---- csproj 接线 ----
    with open(CSPROJ, encoding="utf-8-sig") as f:
        csproj = f.read()

    if MARKER_BEGIN in csproj:
        print("[接线] csproj 已就位（幂等，不改）")
    elif check_only:
        print("[接线] csproj **未接线** —— 需要运行一次不带 --check 的本脚本")
        return 1
    else:
        anchor = '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'
        if _count(csproj, anchor) != 1:
            print(f"[失败] csproj 里 Sdk.targets 锚点出现 {_count(csproj, anchor)} 次（要求 1）")
            return 1
        block = (MARKER_BEGIN + "\n"
                 "  <ItemGroup>\n"
                 f'    <Compile Remove="$(UpstreamWpfRoot){UP_REL_HS}" />\n'
                 f'    <Compile Remove="$(UpstreamWpfRoot){UP_REL_HK}" />\n'
                 '    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/HwndSource.Linux.cs" />\n'
                 '    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/HwndKeyboardInputProvider.Linux.cs" />\n'
                 "  </ItemGroup>\n"
                 + MARKER_END + "\n")
        csproj = csproj.replace(anchor, block + anchor, 1)
        with open(CSPROJ, "w", encoding="utf-8") as f:
            f.write(csproj)
        print(f"[接线] 已注入 4 行到 {os.path.relpath(CSPROJ, ROOT)}（Remove 落在上游 Include 之后）")

    if check_only:
        print("[检查] " + ("两个生成物都已就位且与上游同步" if up_to_date
                           else "生成物缺失/与上游不同步（需要重新生成）"))
        return 0 if up_to_date else 1

    print("\n下一步（**不要在这里重建 PC**；编到 /tmp 做闸门）：")
    print("  dotnet msbuild src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/PresentationCore.Linux.csproj -m:1 --nologo \\")
    print("      -getItem:Compile -p:Configuration=Debug | grep -iE 'HwndSource|HwndKeyboardInputProvider'")
    print("  # 期望只剩两个 .Linux.cs（上游那两条被 Remove 掉）")
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="只检查，不修改任何文件")
    ap.add_argument("--apply", action="store_true", help="显式表示要写盘（默认行为，等价）")
    args = ap.parse_args()
    return generate(args.check)


if __name__ == "__main__":
    sys.exit(main())
