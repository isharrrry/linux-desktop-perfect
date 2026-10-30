#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""T1c 第 2 批 · P6：`TextEditorTyping.OnTextInput`（TextBox 的处理器）只读插桩。

用法
----
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationframework-texteditor-trace.py --check   # 只读
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationframework-texteditor-trace.py           # 生成 + 接线（幂等）
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationframework-texteditor-trace.py --prove   # 「只插入」机械证明（只读）

为什么打这一格（P6，**跨车道一次**，主控授权）
--------------------------------------------
第 1 批实跑：`WM_CHAR` 到了 `OnPreprocessMessage`、`_eatCharMessages` 全 `False`、
`TranslateChar/OnMnemonic` 都 `False`，最后 **`ProcessTextInputAction ⇒ handled=True`** 而文本没变。
⇒ 第 2 批要回答"`TextInput` 有没有被 raise、raise 给了谁"，而 P6 是这条链的**终点那一格**：
**TextBox（`TextEditorTyping.OnTextInput`）到底跑没跑；跑了为什么没插入**。

五个点（**每个点都在"会导致 return 的门"的两侧**，所以"没打印"不再有多种含义）：
    P6a  方法入口：`sender`/文本/`OriginalSource`/`e.Handled`（进来时的值）
    P6b1 `This == null || !_IsEnabled || IsReadOnly || !_IsSourceInScope(OriginalSource)` 那一格
         ⇒ 打**四个条件的各自真假**（"处理器跑了但又立刻 return" = 这一格）
    P6b2 空文本那一格（`composition == null && Text 为空`）
    P6c  `// Consider event handled` + `e.Handled = true;` 之后 ⇒ 说明**过了两道门**
    P6d  `TextEditorTyping.ScheduleInput(...)` 之后 ⇒ **插入已排队**（再往后就是调度/TextContainer）

程序集边界：本文件在 **PresentationFramework**，看不到 PresentationCore 的 internal
⇒ 自带一个极小的 `WpfLinuxPfInputTrace`（**不复用** `WpfLinuxInputTrace`）。
开关与 PC 侧**同一套**：`WPF_LINUX_INPUT_TRACE`（严格：1/true/on/yes）或
`WPF_LINUX_MSGFLOW_TRACE`（与原生侧同义：非空且非字面 `0`）；前缀同样是 `[INPUT_TRACE]`。
"""

import argparse
import hashlib
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))

PF_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "PresentationFramework")
CSPROJ = os.path.join(PF_DIR, "PresentationFramework.Linux.csproj")

UP_REL = ("src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Documents/"
          "TextEditorTyping.cs")
GEN = os.path.join(PF_DIR, "TextEditorTyping.Linux.cs")

# --------------------------------------------------------------------------------------
#  插桩类本体（插到 `internal static class TextEditorTyping` **之前**，同一 namespace）
# --------------------------------------------------------------------------------------
TRACE_CLASS = '''// =====================================================================================
//  T1c 只读插桩（第 2 批 · P6）：PF 侧"TextBox 的处理器到底跑没跑"
//
//  为什么自带一个类：本文件编进 **PresentationFramework**，看不到 PresentationCore 的
//  internal 成员 ⇒ 不能复用 `WpfLinuxInputTrace`（跨程序集）。所以这里是**极小**的独立实现。
//  开关与 PC 侧同一套：WPF_LINUX_INPUT_TRACE（严格 1/true/on/yes）或
//  WPF_LINUX_MSGFLOW_TRACE（与原生侧同义：非空且非字面 0）。缺省关 / ≤120 行 / 只打印。
//  前缀同样是 [INPUT_TRACE] ⇒ 一条 grep 抓全链路；行内标 P6a/P6b1/P6b2/P6c/P6d。
// =====================================================================================
internal static class WpfLinuxPfInputTrace
{
    private const string EnvInputTrace = "WPF_LINUX_INPUT_TRACE";
    private const string EnvMsgFlow = "WPF_LINUX_MSGFLOW_TRACE";
    private const int MaxLines = 120;

    private static readonly bool s_enabled;
    private static readonly string s_via;
    private static int s_lines;
        private static int s_budgetNotice;   // L12：触顶只报一次

    static WpfLinuxPfInputTrace()
    {
        string a = null, b = null;
        try { a = System.Environment.GetEnvironmentVariable(EnvInputTrace); } catch (System.Exception) { }
        try { b = System.Environment.GetEnvironmentVariable(EnvMsgFlow); } catch (System.Exception) { }
        s_enabled = IsOn(a) || IsOnNativeSwitch(b);
        s_via = IsOn(a) ? EnvInputTrace : (IsOnNativeSwitch(b) ? EnvMsgFlow : "(都未设)");
        if (s_enabled)
        {
            Emit("via=" + s_via + " pid=" + System.Environment.ProcessId
                 + " —— PF 侧 P6 读数（TextBox 的 TextEditorTyping.OnTextInput）"
                 + " 原始读数 " + EnvInputTrace + "=" + Raw(a) + " " + EnvMsgFlow + "=" + Raw(b));
        }
    }

    /// <summary>未设/空白 ⇒ false；1/true/on/yes（不分大小写）⇒ true；其余 ⇒ false（与 PC 侧一致）。</summary>
    internal static bool IsOn(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string v = value.Trim();
        if (v == "1") return true;
        if (string.Equals(v, "true", System.StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(v, "on", System.StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(v, "yes", System.StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>与原生 `win32_msg.c` 的同名开关**同义**：非空且非字面 `0` ⇒ 开。</summary>
    internal static bool IsOnNativeSwitch(string value)
    {
        if (value == null) return false;
        string v = value.Trim();
        if (v.Length == 0) return false;
        return v != "0";
    }

    private static string Raw(string value)
    {
        if (value == null) return "(null)";
        string v = value.Trim();
        if (v.Length == 0) return "(空)";
        if (v.Length > 12) v = v.Substring(0, 12) + "…";
        return "\\"" + v + "\\"";
    }

    internal static int LineCount { get { return s_lines; } }

    private static void Emit(string message)
    {
        if (!s_enabled) return;
        if (System.Threading.Interlocked.Increment(ref s_lines) > MaxLines)
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
            System.Console.Error.WriteLine("[INPUT_TRACE] " + message);
            System.Console.Error.Flush();
        }
        catch (System.Exception)
        {
            System.Threading.Interlocked.Decrement(ref s_lines);
        }
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

    private static string TextOf(object args)
    {
        System.Windows.Input.TextCompositionEventArgs t =
            args as System.Windows.Input.TextCompositionEventArgs;
        if (t == null) return "(非 TextCompositionEventArgs)";
        try { return (t.Text == null) ? "(null)" : "\\"" + t.Text + "\\""; }
        catch (System.Exception) { return "(读 Text 抛异常)"; }
    }

    /// <summary>P6a 方法入口：谁调、文本是什么、OriginalSource 是谁、进来时 Handled 是什么。</summary>
    internal static void P6Entry(object sender, object e)
    {
        if (!s_enabled) return;
        System.Windows.RoutedEventArgs re = e as System.Windows.RoutedEventArgs;
        string original = (re == null) ? "(?)" : TypeOf(re.OriginalSource);
        string handled = (re == null) ? "(?)" : re.Handled.ToString();
        string comp = "(?)";
        System.Windows.Input.TextCompositionEventArgs t =
            e as System.Windows.Input.TextCompositionEventArgs;
        if (t != null)
        {
            try { comp = TypeOf(t.TextComposition); } catch (System.Exception) { comp = "(读不出来)"; }
        }
        Emit("P6a TextEditorTyping.OnTextInput 入口 sender=" + TypeOf(sender)
             + " Text=" + TextOf(e) + " OriginalSource=" + original
             + " TextComposition=" + comp + " 进来时 Handled=" + handled);
    }

    /// <summary>P6b1 第一道门（处理器跑了但立刻 return）：**四个条件各自真假**。</summary>
    internal static void P6BailScope(object thisEditor, bool thisNull, bool enabled, bool readOnly,
                                     bool inScope, object originalSource)
    {
        if (!s_enabled) return;
        Emit("P6b1 **第一道门就 return** This=" + TypeOf(thisEditor)
             + " This==null:" + thisNull + " IsEnabled:" + enabled + " IsReadOnly:" + readOnly
             + " IsSourceInScope(OriginalSource):" + inScope
             + " OriginalSource=" + TypeOf(originalSource)
             + "（任一项为假都会 return；全真却打到这里 ⇒ 表达式求值有问题）");
    }

    /// <summary>P6b2 第二道门（空文本）。</summary>
    internal static void P6BailEmptyText(object e, bool compositionNull, int textLen)
    {
        if (!s_enabled) return;
        Emit("P6b2 **第二道门就 return**（空文本）composition==null:" + compositionNull
             + " Text=" + TextOf(e) + " Text.Length=" + textLen);
    }

    /// <summary>P6c 过了两道门、且已把 `e.Handled = true` ⇒ 后面该走插入。</summary>
    internal static void P6ConsiderHandled(object thisEditor, object composition, object e)
    {
        if (!s_enabled) return;
        System.Windows.RoutedEventArgs re = e as System.Windows.RoutedEventArgs;
        Emit("P6c 已过两道门 ⇒ e.Handled=true（现在 Handled="
             + ((re == null) ? "(?)" : re.Handled.ToString()) + "）This=" + TypeOf(thisEditor)
             + " composition=" + TypeOf(composition)
             + " ⇒ 走 " + ((composition == null) ? "ScheduleInput（普通输入）" : "TextStore/ImmComposition 分支"));
    }

    /// <summary>P6d 插入**已排队**（再往后是调度与 TextContainer，不在本批）。</summary>
    internal static void P6ScheduleInput(object thisEditor, string text, bool handled)
    {
        if (!s_enabled) return;
        Emit("P6d 已 ScheduleInput 排队插入 text=" + ((text == null) ? "(null)" : "\\"" + text + "\\"")
             + " This=" + TypeOf(thisEditor) + " Handled=" + handled
             + " ⇒ 事件确实被 TextBox 处理并排了插入");
    }

    // ══════════════════════════════════════════════════════════════════════════════════
    //  第 3 批（Q1…Q4）：插入**排队之后**到底跑没跑、跑到了哪一步
    //
    //  第 2 批实测（六格命中"己"）：P6c/P6d 都绿（`ScheduleInput` 已排队插入 text="A"），
    //  而样例读回 `text='seed-文本' changes=0` ⇒ 问题在**排队之后**。
    //  读上游 `TextEditorTyping.ScheduleInput:1569-1591` 得知：普通输入（非 rich content、
    //  没有 pending 鼠标）把插入**排在一个 `DispatcherPriority.Background` 的
    //  `DispatcherOperation`** 上（`BackgroundInputCallback`）⇒ 这一批就钉那条路：
    //    Q1 `BackgroundInputCallback` 入口/出口   —— 那个 Background 操作**到底跑了没有**
    //    Q2 `_FlushPendingInputItems` 入口/出口/每条 item —— 我们的 TextInputItem 在不在队列里
    //    Q3 `TextInputItem.Do()` 入口/早退/返回   —— `UiScope == null` 那条静默丢弃
    //    Q4 `DoTextInput` 全程（入口/过滤后/写之前/**写之后**/出口/异常）—— **文档到底变了没有**
    //
    //  预算**独立**（MaxQLines），与 P6 那 120 行不互相挤。
    //  只打印；唯一"非纯插入"的一处已明写：Q4 把整个方法体包了一层 try/catch，
    //  但 **catch 里记录后原样 `throw;`** ⇒ 异常类型、栈信息、控制流都不变（见应用器说明）。
    // ══════════════════════════════════════════════════════════════════════════════════
    private const int MaxQLines = 200;
    private static int s_qLines;

    /// <summary>第 3 批实际输出行数。</summary>
    internal static int QLineCount { get { return s_qLines; } }

    private static void EmitQ(string message)
    {
        if (!s_enabled) return;
        if (System.Threading.Interlocked.Increment(ref s_qLines) > MaxQLines) return;
        try
        {
            System.Console.Error.WriteLine("[INPUT_TRACE] " + message);
            System.Console.Error.Flush();
        }
        catch (System.Exception)
        {
            System.Threading.Interlocked.Decrement(ref s_qLines);
        }
    }

    /// <summary>文档读数：**长度 + 前 40 字符**（回答"到底写进去了没有"）。</summary>
    private static string DocOf(object editor)
    {
        TextEditor te = editor as TextEditor;
        if (te == null) return "(非 TextEditor)";
        try
        {
            ITextContainer c = te.TextContainer;
            if (c == null) return "(TextContainer=null)";
            string t = new TextRange(c.Start, c.End).Text;
            if (t == null) return "(Text=null)";
            string head = (t.Length > 40) ? (t.Substring(0, 40) + "…") : t;
            return "len=" + t.Length + " text=\\"" + head + "\\"";
        }
        catch (System.Exception ex) { return "(读文档抛 " + ex.GetType().Name + ")"; }
    }

    /// <summary>选区读数（Start/End 偏移 + 选中的文本）。</summary>
    private static string SelOf(object editor)
    {
        TextEditor te = editor as TextEditor;
        if (te == null) return "(非 TextEditor)";
        try
        {
            ITextSelection sel = te.Selection;
            if (sel == null) return "(Selection=null)";
            return "选区[" + sel.Start.Offset + "," + sel.End.Offset + "]=\\""
                 + ((sel.Text == null) ? "(null)" : sel.Text) + "\\"";
        }
        catch (System.Exception ex) { return "(读选区抛 " + ex.GetType().Name + ")"; }
    }

    /// <summary>编辑器状态（把"哪一道门会挡"一次性打全）。</summary>
    private static string StateOf(object editor)
    {
        TextEditor te = editor as TextEditor;
        if (te == null) return "(非 TextEditor)";
        try
        {
            return "UiScope=" + TypeOf(te.UiScope)
                 + " IsEnabled=" + te._IsEnabled + " IsReadOnly=" + te.IsReadOnly
                 + " AcceptsRichContent=" + te.AcceptsRichContent;
        }
        catch (System.Exception ex) { return "(读状态抛 " + ex.GetType().Name + ")"; }
    }

    private static int PendingCount(object store)
    {
        TextEditorThreadLocalStore s = store as TextEditorThreadLocalStore;
        if (s == null) return -1;
        try { return (s.PendingInputItems == null) ? -1 : s.PendingInputItems.Count; }
        catch (System.Exception) { return -2; }
    }

    /// <summary>Q0a `ScheduleInput` 入口（插入进入调度器）。</summary>
    internal static void Q0Entry(object This, object item)
    {
        if (!s_enabled) return;
        TextEditor te = This as TextEditor;
        string rich = "(?)";
        try { if (te != null) rich = te.AcceptsRichContent.ToString(); } catch (System.Exception) { rich = "(读不出来)"; }
        EmitQ("Q0a ScheduleInput 入口 item=" + TypeOf(item) + " This=" + TypeOf(This)
              + " AcceptsRichContent=" + rich);
    }

    /// <summary>Q0i 走了**立即执行**支路（`!AcceptsRichContent || IsMouseInputPending`）。</summary>
    internal static void Q0Immediate(object This, object item)
    {
        if (!s_enabled) return;
        EmitQ("Q0i **立即执行支路**（`!AcceptsRichContent || IsMouseInputPending`）item=" + TypeOf(item)
              + " ⇒ 不经过 Background 投递，应当紧接着看到 Q3/Q4");
    }

    /// <summary>
    /// Q0b **决定性那一格**：`threadLocalStore.PendingInputItems == null` 的真假。
    /// `False` ⇒ 上游那条 `if` **不成立** ⇒ **从不投递** `BackgroundInputCallback`，
    /// item 只是 `Add` 进列表躺着（这一格直接判"有没有投递出去"）。
    /// </summary>
    internal static void Q0PendingBefore(object This, object store)
    {
        if (!s_enabled) return;
        int n = PendingCount(store);
        EmitQ("Q0b PendingInputItems == null ? "
              + ((n < 0) ? "**True** ⇒ 下面应当紧接着出现 Q0c（已投递）"
                         : ("**False**（count=" + n + "）⇒ **if 不成立 ⇒ 从不投递**，"
                            + "item 只 Add 就躺着（永远不会有 BackgroundInputCallback）")));
    }

    /// <summary>Q0c **已投递**（`BeginInvoke(DispatcherPriority.Background, BackgroundInputCallback)` 调过了）。</summary>
    internal static void Q0Posted(object This, object store)
    {
        if (!s_enabled) return;
        EmitQ("Q0c **已投递** Background 操作（PendingInputItems 现在 count=" + PendingCount(store) + "）");
    }

    /// <summary>Q0d `Add(item)` 之后的条数（与 Q0b 合起来判"投没投 / 排没排"）。</summary>
    internal static void Q0Added(object This, object store, object item)
    {
        if (!s_enabled) return;
        EmitQ("Q0d Add 之后 item=" + TypeOf(item) + " count=" + PendingCount(store));
    }

    /// <summary>Q1 `BackgroundInputCallback` 入口：那个 **Background 操作真的跑了**。</summary>
    internal static void Q1Entry(object This, object store)
    {
        if (!s_enabled) return;
        EmitQ("Q1 BackgroundInputCallback **入口**（Background 优先级的操作被执行了）"
              + " This=" + TypeOf(This) + " PendingInputItems.Count=" + PendingCount(store));
    }

    /// <summary>Q1' 回调出口（条目已清空）。</summary>
    internal static void Q1Exit(object This, object store)
    {
        if (!s_enabled) return;
        EmitQ("Q1' BackgroundInputCallback 出口（PendingInputItems 已置 null）"
              + " This=" + TypeOf(This) + " PendingInputItems=" + PendingCount(store));
    }

    private static int s_q2Count = -1;

    /// <summary>Q2 `_FlushPendingInputItems` 入口（**所有调用者都经过这里**）。</summary>
    internal static void Q2Entry(object This, object store)
    {
        if (!s_enabled) return;
        s_q2Count = PendingCount(store);
        EmitQ("Q2 _FlushPendingInputItems 入口 This=" + TypeOf(This)
              + " PendingInputItems.Count=" + s_q2Count);
    }

    /// <summary>Q2b 循环里第 i 条 item 的类型（**实参惰性**：调用点只传列表本身，下标与守卫都在这里）。</summary>
    internal static void Q2Item(int index, object list)
    {
        if (!s_enabled) return;
        object item = null;
        bool ok = false;
        try
        {
            System.Collections.ArrayList a = list as System.Collections.ArrayList;
            if (a != null && index >= 0 && index < a.Count) { item = a[index]; ok = true; }
        }
        catch (System.Exception) { }
        EmitQ("Q2b flush 第 " + index + " 条 item=" + (ok ? TypeOf(item) : "越界/不可用"));
    }

    /// <summary>Q2' flush 出口（处理了多少条）。</summary>
    internal static void Q2Exit(object This)
    {
        if (!s_enabled) return;
        EmitQ("Q2' _FlushPendingInputItems 出口 This=" + TypeOf(This)
              + " 入口时的条数=" + s_q2Count + "（每条 item 都会有一条 Q2b/Q3 读数）");
    }

    /// <summary>Q3 `TextInputItem.Do()` 入口。</summary>
    internal static void Q3Do(object editor, string text)
    {
        if (!s_enabled) return;
        // `UiScope` 是属性访问 ⇒ **在这里取**（调用点只传 editor 本身）
        string ui = "(?)";
        try { TextEditor te = editor as TextEditor; if (te != null) ui = TypeOf(te.UiScope); }
        catch (System.Exception) { ui = "(读 UiScope 抛)"; }
        EmitQ("Q3 TextInputItem.Do() 入口 text=\\"" + text + "\\" UiScope=" + ui
              + " " + StateOf(editor));
    }

    /// <summary>Q3b **命中 `UiScope == null` 早退** ⇒ 输入被静默丢弃。</summary>
    internal static void Q3BailUiScope(object editor, string text)
    {
        if (!s_enabled) return;
        EmitQ("Q3b **命中早退：UiScope == null** ⇒ text=\\"" + text + "\\" 被**静默丢弃**"
              + "（编辑器已从 UiScope 摘下）" + " " + StateOf(editor));
    }

    /// <summary>Q3c `DoTextInput` 正常返回（写没写进去看 Q4d）。</summary>
    internal static void Q3Done(object editor, string text)
    {
        if (!s_enabled) return;
        EmitQ("Q3c TextInputItem.Do() 正常返回 text=\\"" + text + "\\" " + DocOf(editor));
    }

    /// <summary>Q4a `DoTextInput` 入口。</summary>
    internal static void Q4Entry(object editor, string textData)
    {
        if (!s_enabled) return;
        EmitQ("Q4a DoTextInput 入口 textData=\\"" + textData + "\\" " + SelOf(editor)
              + " " + DocOf(editor) + " " + StateOf(editor));
    }

    /// <summary>Q4b `_FilterText` 之后（长度 0 ⇒ 下一行就 return ⇒ **静默无效果**）。</summary>
    internal static void Q4Filtered(object editor, string raw, string filtered)
    {
        if (!s_enabled) return;
        EmitQ("Q4b _FilterText 之后 原始长度=" + ((raw == null) ? -1 : raw.Length)
              + " 过滤后长度=" + ((filtered == null) ? -1 : filtered.Length)
              + " 过滤后=\\"" + filtered + "\\""
              + ((filtered != null && filtered.Length == 0) ? " ⇒ **长度 0，下一行就 return（静默无效果）**" : "")
              + " " + DocOf(editor));
    }

    /// <summary>Q4c **真正写进容器之前**（`This.SetSelectedText(...)` 那一行之前）。</summary>
    internal static void Q4BeforeWrite(object editor, string filtered)
    {
        if (!s_enabled) return;
        EmitQ("Q4c 即将 SetSelectedText(filteredText=\\"" + filtered + "\\") 之前 "
              + SelOf(editor) + " " + DocOf(editor));
    }

    /// <summary>Q4d **写完之后**——本批最关键的一格：文档长度/内容变了没有。</summary>
    internal static void Q4AfterWrite(object editor, string filtered)
    {
        if (!s_enabled) return;
        EmitQ("Q4d SetSelectedText 返回后（**文档到底变了没有**）" + DocOf(editor)
              + " " + SelOf(editor) + "（写的是 \\"" + filtered + "\\"）");
    }

    /// <summary>Q4f 出口：`closeAction` = Rollback 会把刚写进去的又撤掉。</summary>
    internal static void Q4Exit(object editor, object closeAction)
    {
        if (!s_enabled) return;
        EmitQ("Q4f DoTextInput 出口 UndoCloseAction=" + ((closeAction == null) ? "(?)" : closeAction.ToString())
              + "（Rollback ⇒ 上面写进去的会被撤掉）" + DocOf(editor));
    }

    /// <summary>Q4e **捕获异常**（记录下来后调用方原样 `throw;` ⇒ 语义不变）。</summary>
    internal static void Q4Exception(object editor, object ex)
    {
        if (!s_enabled) return;
        System.Exception e = ex as System.Exception;
        string line = (e == null) ? TypeOf(ex)
                                  : (e.GetType().FullName + ": " + e.Message);
        string stack = "";
        if (e != null && e.StackTrace != null)
        {
            string[] lines = e.StackTrace.Split('\\n');
            int n = (lines.Length < 4) ? lines.Length : 4;
            for (int i = 0; i < n; i++) stack += " | " + lines[i].Trim();
        }
        EmitQ("Q4e **DoTextInput 抛出** " + line + stack + " " + DocOf(editor));
    }
}

'''

# --------------------------------------------------------------------------------------
#  五处锚点（每处要求**上游恰好命中 1 次**）
# --------------------------------------------------------------------------------------
PF_CLASS_ANCHOR = '    internal static class TextEditorTyping\n'

PF_ENTRY_ANCHOR = '''        internal static void OnTextInput(object sender, TextCompositionEventArgs e)
        {
            TextEditor This = TextEditor._GetTextEditor(sender);

            if (This == null || !This._IsEnabled || This.IsReadOnly || !This._IsSourceInScope(e.OriginalSource))
            {
                return;
            }
'''

PF_EMPTY_ANCHOR = '''            if (composition == null &&
                (e.Text == null || e.Text.Length == 0))
            {
                return;
            }
'''

PF_HANDLED_ANCHOR = '''            // Consider event handled
            e.Handled = true;

            This.TextView?.ThrottleBackgroundTasksForUserInput();

            // If this event is our Cicero TextStore composition, we always handles through ITextStore::SetText.
'''

PF_SCHEDULE_ANCHOR = '''                KeyboardDevice keyboard = e.Device as KeyboardDevice;
                TextEditorTyping.ScheduleInput(This, new TextInputItem(This, e.Text, /*isInsertKeyToggled:*/keyboard != null ? keyboard.IsKeyToggled(Key.Insert) : false));
            }
        }
'''

EDITS = [
    ("W0 插桩类本体", PF_CLASS_ANCHOR, TRACE_CLASS + PF_CLASS_ANCHOR),
    ("P6a 入口 + P6b1 第一道门", PF_ENTRY_ANCHOR, '''        internal static void OnTextInput(object sender, TextCompositionEventArgs e)
        {
            // ── T1c 第 2 批 P6a（只读插桩）：处理器**有没有被调到** ──
            WpfLinuxPfInputTrace.P6Entry(sender, e);

            TextEditor This = TextEditor._GetTextEditor(sender);

            if (This == null || !This._IsEnabled || This.IsReadOnly || !This._IsSourceInScope(e.OriginalSource))
            {
                // ── T1c P6b1：**四个条件各自真假**（"跑了但立刻 return"就是这一格）──
                WpfLinuxPfInputTrace.P6BailScope(This, This == null,
                                                 This != null && This._IsEnabled,
                                                 This != null && This.IsReadOnly,
                                                 This != null && This._IsSourceInScope(e.OriginalSource),
                                                 e.OriginalSource);
                return;
            }
'''),
    ("P6b2 第二道门（空文本）", PF_EMPTY_ANCHOR, '''            if (composition == null &&
                (e.Text == null || e.Text.Length == 0))
            {
                // ── T1c P6b2：第二道门（空文本）──
                WpfLinuxPfInputTrace.P6BailEmptyText(e, composition == null,
                                                     (e.Text == null) ? -1 : e.Text.Length);
                return;
            }
'''),
    ("P6c 过门 + e.Handled=true", PF_HANDLED_ANCHOR, '''            // Consider event handled
            e.Handled = true;

            // ── T1c P6c：两道门都过了 ⇒ 后面该走插入 ──
            WpfLinuxPfInputTrace.P6ConsiderHandled(This, composition, e);

            This.TextView?.ThrottleBackgroundTasksForUserInput();

            // If this event is our Cicero TextStore composition, we always handles through ITextStore::SetText.
'''),
    ("P6d 插入已排队", PF_SCHEDULE_ANCHOR, '''                KeyboardDevice keyboard = e.Device as KeyboardDevice;
                TextEditorTyping.ScheduleInput(This, new TextInputItem(This, e.Text, /*isInsertKeyToggled:*/keyboard != null ? keyboard.IsKeyToggled(Key.Insert) : false));
                // ── T1c P6d：插入**已排队**（再往后是调度与 TextContainer，不在本批）──
                WpfLinuxPfInputTrace.P6ScheduleInput(This, e.Text, e.Handled);
            }
        }
'''),
    ('''Q0 ScheduleInput 入口/分支/投递/Add''',
     '''        private static void ScheduleInput(TextEditor This, InputItem item)
        {
            if (!This.AcceptsRichContent || IsMouseInputPending(This))
            {
                // We have to do the work now, or we'll get out of synch.
                TextEditorTyping._FlushPendingInputItems(This);
                item.Do();
            }
            else
            {
                TextEditorThreadLocalStore threadLocalStore;

                threadLocalStore = TextEditor._ThreadLocalStore;

                if (threadLocalStore.PendingInputItems == null)
                {
                    threadLocalStore.PendingInputItems = new ArrayList(1);
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new DispatcherOperationCallback(BackgroundInputCallback), This);
                }

                threadLocalStore.PendingInputItems.Add(item);
            }
        }
''',
     '''        private static void ScheduleInput(TextEditor This, InputItem item)
        {
            // ── T1c Q0a（只读插桩）：插入**进入 ScheduleInput**（item 类型 + AcceptsRichContent）──
            WpfLinuxPfInputTrace.Q0Entry(This, item);

            if (!This.AcceptsRichContent || IsMouseInputPending(This))
            {
                // ── T1c Q0i：走了**立即执行**支路（不走 Background 投递）──
                WpfLinuxPfInputTrace.Q0Immediate(This, item);
                // We have to do the work now, or we'll get out of synch.
                TextEditorTyping._FlushPendingInputItems(This);
                item.Do();
            }
            else
            {
                TextEditorThreadLocalStore threadLocalStore;

                threadLocalStore = TextEditor._ThreadLocalStore;

                // ── T1c Q0b（**决定性那一格**）：`PendingInputItems == null` 的真假 ──
                //     false ⇒ 下面那条 if **不成立** ⇒ **从不投递**，item 只 Add 就躺着（永远不会有 BackgroundInputCallback）
                //     true  ⇒ 应当紧接着看到 Q0c（已投递）
                WpfLinuxPfInputTrace.Q0PendingBefore(This, threadLocalStore);

                if (threadLocalStore.PendingInputItems == null)
                {
                    threadLocalStore.PendingInputItems = new ArrayList(1);
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new DispatcherOperationCallback(BackgroundInputCallback), This);
                    // ── T1c Q0c：**已投递**（这一行出现 ⇒ BeginInvoke 确实调了）──
                    WpfLinuxPfInputTrace.Q0Posted(This, threadLocalStore);
                }

                threadLocalStore.PendingInputItems.Add(item);

                // ── T1c Q0d：Add 之后的条数（与 Q0b 一起就能判"投没投/排没排"）──
                WpfLinuxPfInputTrace.Q0Added(This, threadLocalStore, item);
            }
        }
'''),
    ('''Q1 BackgroundInputCallback 入口/出口''',
     '''        private static object BackgroundInputCallback(object This)
        {
            TextEditorThreadLocalStore threadLocalStore = TextEditor._ThreadLocalStore;

            Invariant.Assert(This is TextEditor);
            Invariant.Assert(threadLocalStore.PendingInputItems != null);

            try
            {
                TextEditorTyping._FlushPendingInputItems((TextEditor)This);
            }
            finally
            {
                threadLocalStore.PendingInputItems = null;
            }

            return null;
        }
''',
     '''        private static object BackgroundInputCallback(object This)
        {
            TextEditorThreadLocalStore threadLocalStore = TextEditor._ThreadLocalStore;

            // ── T1c 第 3 批 Q1（只读插桩）：**Background 优先级的操作真的被调到了** ──
            //    ⚠ 故意放在两条 `Invariant.Assert` **之前**：万一断言 FailFast（补丁 N 会打印原文），
            //      这一行也必须已经出去，否则"没看到 assert"与"回调没跑"分不开。
            WpfLinuxPfInputTrace.Q1Entry(This, threadLocalStore);

            Invariant.Assert(This is TextEditor);
            Invariant.Assert(threadLocalStore.PendingInputItems != null);

            try
            {
                TextEditorTyping._FlushPendingInputItems((TextEditor)This);
            }
            finally
            {
                threadLocalStore.PendingInputItems = null;
            }

            // ── T1c Q1'：Background 回调出口（条目已置 null）──
            WpfLinuxPfInputTrace.Q1Exit(This, threadLocalStore);

            return null;
        }
'''),
    ('''Q2 _FlushPendingInputItems 入口/每条/出口''',
     '''        internal static void _FlushPendingInputItems(TextEditor This)
        {
            TextEditorThreadLocalStore threadLocalStore;

            This.TextView?.ThrottleBackgroundTasksForUserInput();

            threadLocalStore = TextEditor._ThreadLocalStore;

            if (threadLocalStore.PendingInputItems != null)
            {
                try
                {
                    for (int i = 0; i < threadLocalStore.PendingInputItems.Count; i++)
                    {
                        ((InputItem)threadLocalStore.PendingInputItems[i]).Do();

                        // After the first dequeue, clear the bit that tracks if
                        // any events are handled after ctl+shift (change flow direction keyboard hotkey).
                        threadLocalStore.PureControlShift = false;
                    }
                }
                finally
                {
                    threadLocalStore.PendingInputItems.Clear();
                }
            }

            // Clear the bit that tracks if any events are handled after
            // ctl+shift (change flow direction keyboard hotkey) one last
            // time, in case the queue was empty.
            //
            // Because we only call this method in preparation for handling
            // a Command, we want this bit cleared.
            threadLocalStore.PureControlShift = false;
        }
''',
     '''        internal static void _FlushPendingInputItems(TextEditor This)
        {
            TextEditorThreadLocalStore threadLocalStore;

            This.TextView?.ThrottleBackgroundTasksForUserInput();

            threadLocalStore = TextEditor._ThreadLocalStore;

            // ── T1c Q2：flush 入口（**所有调用者都经过这里**）──
            WpfLinuxPfInputTrace.Q2Entry(This, threadLocalStore);

            if (threadLocalStore.PendingInputItems != null)
            {
                try
                {
                    for (int i = 0; i < threadLocalStore.PendingInputItems.Count; i++)
                    {
                        // ── T1c Q2b：这一条是什么（我们的 TextInputItem 在不在队列里）──
                        WpfLinuxPfInputTrace.Q2Item(i, threadLocalStore.PendingInputItems);

                        ((InputItem)threadLocalStore.PendingInputItems[i]).Do();

                        // After the first dequeue, clear the bit that tracks if
                        // any events are handled after ctl+shift (change flow direction keyboard hotkey).
                        threadLocalStore.PureControlShift = false;
                    }
                }
                finally
                {
                    threadLocalStore.PendingInputItems.Clear();
                }
            }

            // ── T1c Q2'：flush 出口 ──
            WpfLinuxPfInputTrace.Q2Exit(This);

            // Clear the bit that tracks if any events are handled after
            // ctl+shift (change flow direction keyboard hotkey) one last
            // time, in case the queue was empty.
            //
            // Because we only call this method in preparation for handling
            // a Command, we want this bit cleared.
            threadLocalStore.PureControlShift = false;
        }
'''),
    ('''Q3 TextInputItem.Do 入口/早退/返回''',
     '''            internal override void Do()
            {
                if (TextEditor.UiScope == null)
                {
                    // We dont want to process the input item if the editor has already been detached from its UiScope.
                    return;
                }

                DoTextInput(TextEditor, _text, _isInsertKeyToggled, /*acceptControlCharacters:*/false);
            }
''',
     '''            internal override void Do()
            {
                // ── T1c 第 3 批 Q3（只读插桩）：item **被 Do 了** ──
                WpfLinuxPfInputTrace.Q3Do(TextEditor, _text);

                if (TextEditor.UiScope == null)
                {
                    // ── T1c Q3b：**命中早退** ⇒ 输入被静默丢弃（编辑器已从 UiScope 摘下）──
                    WpfLinuxPfInputTrace.Q3BailUiScope(TextEditor, _text);
                    // We dont want to process the input item if the editor has already been detached from its UiScope.
                    return;
                }

                DoTextInput(TextEditor, _text, _isInsertKeyToggled, /*acceptControlCharacters:*/false);

                // ── T1c Q3c：DoTextInput 正常返回（写没写进去看 Q4d）──
                WpfLinuxPfInputTrace.Q3Done(TextEditor, _text);
            }
'''),
    ('''Q4a/Q4 方法体 try 包裹 + 入口''',
     '''        private static void DoTextInput(TextEditor This, string textData, bool isInsertKeyToggled, bool acceptControlCharacters)
        {
            // Hide the mouse cursor on user input.
            HideCursor(This);

''',
     '''        private static void DoTextInput(TextEditor This, string textData, bool isInsertKeyToggled, bool acceptControlCharacters)
        {
            // ── T1c 第 3 批 Q4（只读插桩）：整个方法体包一层 try/catch ──
            //   **语义不变**：catch 里只记录，然后原样 `throw;`（异常类型与栈信息都保留）。
            //   为什么要包：这条路上任何异常（例：InputLanguageManager.Current 为 null 时的 NRE）
            //   都会变成"静默无效果"，必须让它留下读数。
            try
            {
            // ── T1c Q4a：入口读数（文本 / 选区 / 文档长度 / 编辑器状态）──
            WpfLinuxPfInputTrace.Q4Entry(This, textData);

            // Hide the mouse cursor on user input.
            HideCursor(This);

'''),
    ('''Q4b _FilterText 之后''',
     '''            string filteredText = This._FilterText(textData, This.Selection);
            if (filteredText.Length == 0)
            {
                return;
            }

''',
     '''            string filteredText = This._FilterText(textData, This.Selection);

            // ── T1c Q4b：过滤后的文本（长度 0 ⇒ 下一行就 return ⇒ 静默无效果）──
            WpfLinuxPfInputTrace.Q4Filtered(This, textData, filteredText);

            if (filteredText.Length == 0)
            {
                return;
            }

'''),
    ('''Q4c/Q4d 写入之前/之后''',
     '            try\n            {\n                using (This.Selection.DeclareChangeBlock())\n                {\n                    This.Selection.ApplyTypingHeuristics(This.AllowOvertype && This._OvertypeMode && filteredText != "\\t");\n\n                    This.SetSelectedText(filteredText, InputLanguageManager.Current.CurrentInputLanguage);\n\n',
     '            try\n            {\n                using (This.Selection.DeclareChangeBlock())\n                {\n                    This.Selection.ApplyTypingHeuristics(This.AllowOvertype && This._OvertypeMode && filteredText != "\\t");\n\n                    // ── T1c Q4c：**真正写进容器的调用点之前** ──\n                    WpfLinuxPfInputTrace.Q4BeforeWrite(This, filteredText);\n\n                    This.SetSelectedText(filteredText, InputLanguageManager.Current.CurrentInputLanguage);\n\n                    // ── T1c Q4d：写完之后——**文档到底变了没有**（本批最关键的一格）──\n                    WpfLinuxPfInputTrace.Q4AfterWrite(This, filteredText);\n\n'),
    ('''Q4e/Q4f 异常捕获 + 出口''',
     '''                }
            }
            finally
            {
                TextEditorTyping.CloseTypingUndoUnit(This, closeAction);
            }
        }
''',
     '''                }
            }
            finally
            {
                TextEditorTyping.CloseTypingUndoUnit(This, closeAction);
            }

            // ── T1c Q4f：出口（UndoCloseAction=Rollback ⇒ 刚写进去的会被撤掉）──
            WpfLinuxPfInputTrace.Q4Exit(This, closeAction);
            }
            catch (System.Exception __t1cEx)
            {
                // ── T1c Q4e：**捕获异常**（记录后原样 throw ⇒ 语义不变）──
                WpfLinuxPfInputTrace.Q4Exception(This, __t1cEx);
                throw;
            }
        }
'''),
]

REQUIRED = [
    ("插桩类存在", "internal static class WpfLinuxPfInputTrace"),
    ("开关名①", 'private const string EnvInputTrace = "WPF_LINUX_INPUT_TRACE";'),
    ("开关名②", 'private const string EnvMsgFlow = "WPF_LINUX_MSGFLOW_TRACE";'),
    ("与原生同名开关同义", 'return v != "0";'),
    ("缺省关", "if (string.IsNullOrWhiteSpace(value)) return false;"),
    ("有界 120 行", "private const int MaxLines = 120;"),
    ("读数自带出处 + 原始值", 'Emit("via=" + s_via'),
    ("输出前缀与 PC 侧一致（一条 grep 抓全链路）", '"[INPUT_TRACE] "'),
    ("P6a 入口", "WpfLinuxPfInputTrace.P6Entry(sender, e);"),
    ("P6b1 第一道门四个条件", "WpfLinuxPfInputTrace.P6BailScope(This, This == null,"),
    ("P6b2 第二道门", "WpfLinuxPfInputTrace.P6BailEmptyText(e, composition == null,"),
    ("P6c 过门", "WpfLinuxPfInputTrace.P6ConsiderHandled(This, composition, e);"),
    ("P6d 插入已排队", "WpfLinuxPfInputTrace.P6ScheduleInput(This, e.Text, e.Handled);"),
    ("上游 ScheduleInput 那条语句逐字未动",
     "TextEditorTyping.ScheduleInput(This, new TextInputItem(This, e.Text, /*isInsertKeyToggled:*/keyboard != null ? keyboard.IsKeyToggled(Key.Insert) : false));"),
    # ── 第 3 批（Q1…Q4） ──
    ("第 3 批独立预算", "private const int MaxQLines = 200;"),
    ("Q0a ScheduleInput 入口", "WpfLinuxPfInputTrace.Q0Entry(This, item);"),
    ("Q0i 立即执行支路", "WpfLinuxPfInputTrace.Q0Immediate(This, item);"),
    ("Q0b **PendingInputItems == null 的真假**（决定性那一格）",
     "WpfLinuxPfInputTrace.Q0PendingBefore(This, threadLocalStore);"),
    ("Q0c 已投递 Background 操作", "WpfLinuxPfInputTrace.Q0Posted(This, threadLocalStore);"),
    ("Q0d Add 之后 count", "WpfLinuxPfInputTrace.Q0Added(This, threadLocalStore, item);"),
    ("Q1 入口探针在两条 Invariant.Assert **之前**",
     "            WpfLinuxPfInputTrace.Q1Entry(This, threadLocalStore);\n\n            Invariant.Assert(This is TextEditor);"),
    ("Q1 Background 回调入口", "WpfLinuxPfInputTrace.Q1Entry(This, threadLocalStore);"),
    ("Q1' Background 回调出口", "WpfLinuxPfInputTrace.Q1Exit(This, threadLocalStore);"),
    ("Q2 flush 入口", "WpfLinuxPfInputTrace.Q2Entry(This, threadLocalStore);"),
    ("Q2b 第 i 条 item（**实参惰性**：传列表，下标在 helper 内）", "WpfLinuxPfInputTrace.Q2Item(i, threadLocalStore.PendingInputItems);"),
    ("Q2' flush 出口", "WpfLinuxPfInputTrace.Q2Exit(This);"),
    ("Q3 item 被 Do（**实参惰性**：UiScope 在 helper 内取）", "WpfLinuxPfInputTrace.Q3Do(TextEditor, _text);"),
    ("Q3b UiScope==null 早退", "WpfLinuxPfInputTrace.Q3BailUiScope(TextEditor, _text);"),
    ("Q3c Do 正常返回", "WpfLinuxPfInputTrace.Q3Done(TextEditor, _text);"),
    ("Q4a DoTextInput 入口", "WpfLinuxPfInputTrace.Q4Entry(This, textData);"),
    ("Q4b _FilterText 之后", "WpfLinuxPfInputTrace.Q4Filtered(This, textData, filteredText);"),
    ("Q4c 写入之前", "WpfLinuxPfInputTrace.Q4BeforeWrite(This, filteredText);"),
    ("Q4d 写入之后（文档变了没有）", "WpfLinuxPfInputTrace.Q4AfterWrite(This, filteredText);"),
    ("Q4e 异常捕获（记录后 rethrow）", "WpfLinuxPfInputTrace.Q4Exception(This, __t1cEx);"),
    ("Q4 的 catch 里是 `throw;`（不是 throw ex）",
     "                WpfLinuxPfInputTrace.Q4Exception(This, __t1cEx);\n                throw;"),
    ("Q4f 出口", "WpfLinuxPfInputTrace.Q4Exit(This, closeAction);"),
    ("上游 SetSelectedText 那条语句逐字未动",
     "This.SetSelectedText(filteredText, InputLanguageManager.Current.CurrentInputLanguage);"),
]

HEADER = """// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-presentationframework-texteditor-trace.py **生成**，不要手改。
//
// 内容 = 上游 `upstream/wpf/{rel}` 逐字复制 + {n} 处 T1c **只读插桩**（第 2 批 P6）。
// 每次运行该脚本都会从上游重读重生成；锚点找不到 / 命中数不符时**报错退出**，不会静默产出未打补丁的副本。
//
// 为什么打：**打字到不了 TextBox**。第 1 批证明字符到了 OnPreprocessMessage 且没被
//   `_eatCharMessages` 吞掉，停在 `ProcessTextInputAction ⇒ handled=True` 而文本未变。
//   P6 是这条链的终点那一格：**TextBox（TextEditorTyping.OnTextInput）到底跑没跑、跑了为什么没插入**。
//   五个点分布在两道 `return` 门的两侧 ⇒ "没打印"不再有多种含义。
//   开关与 PC 侧同一套（WPF_LINUX_INPUT_TRACE / WPF_LINUX_MSGFLOW_TRACE），缺省关、≤120 行、只打印。
"""

MARKER_BEGIN = ("  <!-- ==== T1c 输入链读数（第 2 批）：PF 侧 TextEditorTyping.OnTextInput"
                "（patch-presentationframework-texteditor-trace.py 注入）==== -->")
MARKER_END = "  <!-- ==== T1c 输入链读数（第 2 批）TextEditorTyping 结束 ==== -->"


def _count(h, n):
    return h.count(n)


def _sha(t):
    return hashlib.sha256(t.encode("utf-8")).hexdigest()


def _header():
    return HEADER.replace("{rel}", UP_REL).replace("{n}", str(len(EDITS)))


def _build(check_only=False):
    up = os.path.join(ROOT, UP_REL)
    if not os.path.exists(up):
        print(f"[失败] 找不到上游 {up}")
        return None, 1
    with open(up, encoding="utf-8-sig") as f:
        text = f.read()

    bad = False
    for name, anchor, _ in EDITS:
        n = _count(text, anchor)
        print(f"[锚点] TextEditorTyping {name}：上游出现 {n} 次（要求 1）")
        if n != 1:
            bad = True
    if bad:
        print("[失败] 锚点缺失或重复 —— 上游这段改过了？**不做任何静默降级**。")
        return None, 1

    out = text
    for _, anchor, repl in EDITS:
        out = out.replace(anchor, repl, 1)

    up_throws, out_throws = _count(text, "throw "), _count(out, "throw ")
    # 第 3 批 Q4e 有**一处故意**的 `throw;`：catch 里记录后**原样重抛**（异常类型/栈/控制流都不变）。
    # 所以这里不是"放水"，而是把那一处**点名**：多一处或少一处都报错。
    INTENTIONAL_RETHROW = 1
    if out_throws != up_throws + INTENTIONAL_RETHROW:
        print(f"[失败] `throw` 条数不符：上游 {up_throws} + 故意 rethrow {INTENTIONAL_RETHROW} "
              f"≠ 生成物 {out_throws}")
        return None, 1
    if _count(out, "                throw;\n") != INTENTIONAL_RETHROW:
        print("[失败] 预期的那一处 `throw;`（Q4e 的 rethrow）不在位")
        return None, 1
    print(f"[断言] `throw` 上游 {up_throws} + **1 处故意 rethrow（Q4e：记录后原样重抛，语义不变）** "
          f"== 生成物 {out_throws}")
    if (_count(text, "{") - _count(text, "}")) != (_count(out, "{") - _count(out, "}")):
        print("[失败] 大括号盈亏变化（只读插桩不该改结构）："
              f"上游 {_count(text, '{')}/{_count(text, '}')} → 生成物 {_count(out, '{')}/{_count(out, '}')}")
        return None, 1
    print(f"[断言] 大括号盈亏一致：{_count(out, '{')} / {_count(out, '}')}")
    print(f"[断言] `throw` 上游 {up_throws} → 生成物 {out_throws}（其中 1 处是 Q4e 的故意 rethrow）；"
          f"行数 {len(text.splitlines())} → {len(out.splitlines())}"
          f"（+{len(out.splitlines()) - len(text.splitlines())}）")

    full = _header() + out
    for name, needle in REQUIRED:
        if needle not in full:
            print(f"[失败] 生成物缺少结构断言：{name}")
            return None, 1
    print(f"[断言] 结构断言 {len(REQUIRED)}/{len(REQUIRED)} 全中")
    return full, 0


def prove():
    up = os.path.join(ROOT, UP_REL)
    with open(up, encoding="utf-8-sig") as f:
        text = f.read()
    body = None
    where = None
    if os.path.exists(GEN):
        with open(GEN, encoding="utf-8") as f:
            raw = f.read()
        head = _header()
        if raw.startswith(head):
            body = raw[len(head):]
            where = f"落盘生成物 {os.path.relpath(GEN, ROOT)}"
        else:
            print("[注意] 落盘生成物的**文件头与当前脚本不一致**（上一版脚本产的、还没重放）"
                  "=> 本证明退化为**内存**证明，不对落盘件背书。")

    if body is None:
        full, rc = _build(check_only=True)
        if rc:
            return 1
        body = full[len(_header()):]
        where = "**内存**（生成物尚未产出/已过时；重放后再跑一次才是对落盘件取的真值）"

    cur = body
    for name, anchor, repl in reversed(EDITS):
        n = _count(cur, repl)
        if n != 1:
            print(f"[失败] 逆向回代 {name}：`repl` 命中 {n} 次（要求 1）⇒ 生成物被手改过？")
            return 1
        cur = cur.replace(repl, anchor, 1)
    if cur != text:
        print("[失败] 摘掉插桩后与上游**不一致** ⇒ 这不是「只插入」，别信。")
        return 1
    print(f"[① 只插入] 取自 {where}")
    print(f"[① 只插入] 摘掉插桩后与上游**逐字节相同** ✓ sha256={_sha(cur)}")
    print(f"[① 只插入] 上游 sha256={_sha(text)}（两条相同即证明：控制流/返回值一个字节没动）")
    return 0


def generate(check_only):
    full, rc = _build(check_only=check_only)
    if rc:
        return rc

    up_to_date = True
    cur = None
    if os.path.exists(GEN):
        with open(GEN, encoding="utf-8") as f:
            cur = f.read()
    if cur != full:
        up_to_date = False
        if not check_only:
            with open(GEN, "w", encoding="utf-8") as f:
                f.write(full)
            print(f"[生成] {os.path.relpath(GEN, ROOT)}：已从上游重生成（只读插桩）sha256={_sha(full)}")
    else:
        print(f"[生成] {os.path.relpath(GEN, ROOT)}：内容已是最新（未重写）sha256={_sha(full)}")

    if not os.path.exists(CSPROJ):
        print(f"[失败] 找不到 {os.path.relpath(CSPROJ, ROOT)}")
        return 1
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
                 f'    <Compile Remove="$(UpstreamWpfRoot){UP_REL}" />\n'
                 '    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/TextEditorTyping.Linux.cs" />\n'
                 "  </ItemGroup>\n"
                 + MARKER_END + "\n")
        csproj = csproj.replace(anchor, block + anchor, 1)
        with open(CSPROJ, "w", encoding="utf-8") as f:
            f.write(csproj)
        print(f"[接线] 已注入 2 行到 {os.path.relpath(CSPROJ, ROOT)}（Remove 落在上游 Include 之后）")

    print("[注意] `src/Linux/build/port-lib.py PresentationFramework` 会整份重写 csproj ⇒ 本块会被抹掉；"
          "重写后必须重跑本脚本（或登记进 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/reapply-patches.py）。"
          "接线丢失**不会报编译错**，只会一行都不打 ⇒ 别把空输出读成结论。")

    if check_only:
        print("[检查] " + ("生成物与接线都已就位且与上游同步" if up_to_date
                           else "生成物缺失/与上游不同步（需要重新生成）"))
        return 0 if up_to_date else 1
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="只检查，不修改任何文件")
    ap.add_argument("--prove", action="store_true", help="「只插入」机械证明（只读）")
    args = ap.parse_args()
    if args.prove:
        return prove()
    return generate(args.check)


if __name__ == "__main__":
    sys.exit(main())
