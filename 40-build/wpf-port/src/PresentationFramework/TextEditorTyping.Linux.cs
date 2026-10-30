// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-presentationframework-texteditor-trace.py **生成**，不要手改。
//
// 内容 = 上游 `upstream/wpf/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Documents/TextEditorTyping.cs` 逐字复制 + 13 处 T1c **只读插桩**（第 2 批 P6）。
// 每次运行该脚本都会从上游重读重生成；锚点找不到 / 命中数不符时**报错退出**，不会静默产出未打补丁的副本。
//
// 为什么打：**打字到不了 TextBox**。第 1 批证明字符到了 OnPreprocessMessage 且没被
//   `_eatCharMessages` 吞掉，停在 `ProcessTextInputAction ⇒ handled=True` 而文本未变。
//   P6 是这条链的终点那一格：**TextBox（TextEditorTyping.OnTextInput）到底跑没跑、跑了为什么没插入**。
//   五个点分布在两道 `return` 门的两侧 ⇒ "没打印"不再有多种含义。
//   开关与 PC 侧同一套（WPF_LINUX_INPUT_TRACE / WPF_LINUX_MSGFLOW_TRACE），缺省关、≤120 行、只打印。
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using MS.Internal;
using MS.Internal.Interop;
using System.Collections; // ArrayList
using System.Runtime.InteropServices;

using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Controls; // ScrollChangedEventArgs
using System.Windows.Interop;
using MS.Win32;
using MS.Internal.Documents;
using MS.Internal.Commands; // CommandHelpers

//
// Description: Text editing service for controls.
//

namespace System.Windows.Documents
{
    /// <summary>
    /// Subcomponent of TextEditor class - Support for Typing
    /// </summary>
// =====================================================================================
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
        return "\"" + v + "\"";
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
        try { return (t.Text == null) ? "(null)" : "\"" + t.Text + "\""; }
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
        Emit("P6d 已 ScheduleInput 排队插入 text=" + ((text == null) ? "(null)" : "\"" + text + "\"")
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
            return "len=" + t.Length + " text=\"" + head + "\"";
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
            return "选区[" + sel.Start.Offset + "," + sel.End.Offset + "]=\""
                 + ((sel.Text == null) ? "(null)" : sel.Text) + "\"";
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
        EmitQ("Q3 TextInputItem.Do() 入口 text=\"" + text + "\" UiScope=" + ui
              + " " + StateOf(editor));
    }

    /// <summary>Q3b **命中 `UiScope == null` 早退** ⇒ 输入被静默丢弃。</summary>
    internal static void Q3BailUiScope(object editor, string text)
    {
        if (!s_enabled) return;
        EmitQ("Q3b **命中早退：UiScope == null** ⇒ text=\"" + text + "\" 被**静默丢弃**"
              + "（编辑器已从 UiScope 摘下）" + " " + StateOf(editor));
    }

    /// <summary>Q3c `DoTextInput` 正常返回（写没写进去看 Q4d）。</summary>
    internal static void Q3Done(object editor, string text)
    {
        if (!s_enabled) return;
        EmitQ("Q3c TextInputItem.Do() 正常返回 text=\"" + text + "\" " + DocOf(editor));
    }

    /// <summary>Q4a `DoTextInput` 入口。</summary>
    internal static void Q4Entry(object editor, string textData)
    {
        if (!s_enabled) return;
        EmitQ("Q4a DoTextInput 入口 textData=\"" + textData + "\" " + SelOf(editor)
              + " " + DocOf(editor) + " " + StateOf(editor));
    }

    /// <summary>Q4b `_FilterText` 之后（长度 0 ⇒ 下一行就 return ⇒ **静默无效果**）。</summary>
    internal static void Q4Filtered(object editor, string raw, string filtered)
    {
        if (!s_enabled) return;
        EmitQ("Q4b _FilterText 之后 原始长度=" + ((raw == null) ? -1 : raw.Length)
              + " 过滤后长度=" + ((filtered == null) ? -1 : filtered.Length)
              + " 过滤后=\"" + filtered + "\""
              + ((filtered != null && filtered.Length == 0) ? " ⇒ **长度 0，下一行就 return（静默无效果）**" : "")
              + " " + DocOf(editor));
    }

    /// <summary>Q4c **真正写进容器之前**（`This.SetSelectedText(...)` 那一行之前）。</summary>
    internal static void Q4BeforeWrite(object editor, string filtered)
    {
        if (!s_enabled) return;
        EmitQ("Q4c 即将 SetSelectedText(filteredText=\"" + filtered + "\") 之前 "
              + SelOf(editor) + " " + DocOf(editor));
    }

    /// <summary>Q4d **写完之后**——本批最关键的一格：文档长度/内容变了没有。</summary>
    internal static void Q4AfterWrite(object editor, string filtered)
    {
        if (!s_enabled) return;
        EmitQ("Q4d SetSelectedText 返回后（**文档到底变了没有**）" + DocOf(editor)
              + " " + SelOf(editor) + "（写的是 \"" + filtered + "\"）");
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
            string[] lines = e.StackTrace.Split('\n');
            int n = (lines.Length < 4) ? lines.Length : 4;
            for (int i = 0; i < n; i++) stack += " | " + lines[i].Trim();
        }
        EmitQ("Q4e **DoTextInput 抛出** " + line + stack + " " + DocOf(editor));
    }
}

    internal static class TextEditorTyping
    {
        //------------------------------------------------------
        //
        //  Class Internal Methods
        //
        //------------------------------------------------------

        #region Class Internal Methods

        /// <summary>
        /// Registes all handlers needed for text editing control functioning.
        /// </summary>
        /// <param name="controlType">
        /// A type of control for which typing component is registered
        /// </param>
        /// <param name="registerEventListeners">
        /// If registerEventListeners is false, caller is responsible for calling OnXXXEvent methods on TextEditor from
        /// UIElement and FrameworkElement virtual overrides (piggy backing on the
        /// UIElement/FrameworkElement class listeners).  If true, TextEditor will register
        /// its own class listener for events it needs.
        ///
        /// This method will always register private command listeners.
        /// </param>
        internal static void _RegisterClassHandlers(Type controlType, bool registerEventListeners)
        {
            if (registerEventListeners)
            {
                EventManager.RegisterClassHandler(controlType, Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown));
                EventManager.RegisterClassHandler(controlType, Keyboard.KeyDownEvent, new KeyEventHandler(OnKeyDown));
                EventManager.RegisterClassHandler(controlType, Keyboard.KeyUpEvent, new KeyEventHandler(OnKeyUp));
                EventManager.RegisterClassHandler(controlType, TextCompositionManager.TextInputEvent, new TextCompositionEventHandler(OnTextInput));
            }

            var onEnterBreak = new ExecutedRoutedEventHandler(OnEnterBreak);
            var onSpace = new ExecutedRoutedEventHandler(OnSpace);
            var onQueryStatusNYI = new CanExecuteRoutedEventHandler(OnQueryStatusNYI);
            var onQueryStatusEnterBreak = new CanExecuteRoutedEventHandler(OnQueryStatusEnterBreak);
            
            EventManager.RegisterClassHandler(controlType, Mouse.MouseMoveEvent, new MouseEventHandler(OnMouseMove), true /* handledEventsToo */);
            EventManager.RegisterClassHandler(controlType, Mouse.MouseLeaveEvent, new MouseEventHandler(OnMouseLeave), true /* handledEventsToo */);

            CommandHelpers.RegisterCommandHandler(controlType, ApplicationCommands.CorrectionList   , new ExecutedRoutedEventHandler(OnCorrectionList)       , new CanExecuteRoutedEventHandler(OnQueryStatusCorrectionList)       , nameof(SR.KeyCorrectionList),   nameof(SR.KeyCorrectionListDisplayString)         );
            CommandHelpers.RegisterCommandHandler(controlType, EditingCommands.ToggleInsert         , new ExecutedRoutedEventHandler(OnToggleInsert)         , onQueryStatusNYI                  , KeyGesture.CreateFromResourceStrings(KeyToggleInsert,     nameof(SR.KeyToggleInsertDisplayString)           ));
            CommandHelpers.RegisterCommandHandler(controlType, EditingCommands.Delete               , new ExecutedRoutedEventHandler(OnDelete)               , onQueryStatusNYI                  , KeyGesture.CreateFromResourceStrings(KeyDelete,           nameof(SR.KeyDeleteDisplayString)                 ));
            CommandHelpers.RegisterCommandHandler(controlType, EditingCommands.DeleteNextWord       , new ExecutedRoutedEventHandler(OnDeleteNextWord)       , onQueryStatusNYI                  , KeyGesture.CreateFromResourceStrings(KeyDeleteNextWord,   nameof(SR.KeyDeleteNextWordDisplayString)         ));
            CommandHelpers.RegisterCommandHandler(controlType, EditingCommands.DeletePreviousWord   , new ExecutedRoutedEventHandler(OnDeletePreviousWord)   , onQueryStatusNYI                  , KeyGesture.CreateFromResourceStrings(KeyDeletePreviousWord, nameof(SR.KeyDeletePreviousWordDisplayString)   ));
            CommandHelpers.RegisterCommandHandler(controlType, EditingCommands.EnterParagraphBreak  , onEnterBreak                                           , onQueryStatusEnterBreak           , KeyGesture.CreateFromResourceStrings(KeyEnterParagraphBreak, nameof(SR.KeyEnterParagraphBreakDisplayString) ));
            CommandHelpers.RegisterCommandHandler(controlType, EditingCommands.EnterLineBreak       , onEnterBreak                                           , onQueryStatusEnterBreak           , KeyGesture.CreateFromResourceStrings(KeyEnterLineBreak,   nameof(SR.KeyEnterLineBreakDisplayString)         ));
            CommandHelpers.RegisterCommandHandler(controlType, EditingCommands.TabForward           , new ExecutedRoutedEventHandler(OnTabForward)           , new CanExecuteRoutedEventHandler(OnQueryStatusTabForward)           , KeyGesture.CreateFromResourceStrings(KeyTabForward,       nameof(SR.KeyTabForwardDisplayString)             ));
            CommandHelpers.RegisterCommandHandler(controlType, EditingCommands.TabBackward          , new ExecutedRoutedEventHandler(OnTabBackward)          , new CanExecuteRoutedEventHandler(OnQueryStatusTabBackward)          , KeyGesture.CreateFromResourceStrings(KeyTabBackward,      nameof(SR.KeyTabBackwardDisplayString)            ));
            CommandHelpers.RegisterCommandHandler(controlType, EditingCommands.Space                , onSpace                                                , onQueryStatusNYI                  , KeyGesture.CreateFromResourceStrings(KeySpace,            nameof(SR.KeySpaceDisplayString)                  ));
            CommandHelpers.RegisterCommandHandler(controlType, EditingCommands.ShiftSpace           , onSpace                                                , onQueryStatusNYI                  , KeyGesture.CreateFromResourceStrings(KeyShiftSpace,       nameof(SR.KeyShiftSpaceDisplayString)             ));

            CommandHelpers.RegisterCommandHandler(controlType, EditingCommands.Backspace            , new ExecutedRoutedEventHandler(OnBackspace)            , onQueryStatusNYI                  , KeyGesture.CreateFromResourceStrings(KeyBackspace,        SR.KeyBackspaceDisplayString),   KeyGesture.CreateFromResourceStrings(KeyShiftBackspace, SR.KeyShiftBackspaceDisplayString) );
        }

        /// <summary>
        /// Add the input language changed event handler and save it
        /// into UIContext data slot.
        /// </summary>
        internal static void _AddInputLanguageChangedEventHandler(TextEditor This)
        {
            TextEditorThreadLocalStore threadLocalStore;

            Invariant.Assert(This._dispatcher == null);
            This._dispatcher = Dispatcher.CurrentDispatcher;
            Invariant.Assert(This._dispatcher != null);

            threadLocalStore = TextEditor._ThreadLocalStore;

            // Only add the input language changed event handler once that safe per UIContext
            if (threadLocalStore.InputLanguageChangeEventHandlerCount == 0)
            {
                // Add input changed event handler into InputLanguageManager
                InputLanguageManager.Current.InputLanguageChanged += new InputLanguageEventHandler(OnInputLanguageChanged);

                // Add the dispatcher shutdown finished event handler to remove InputLanguageChangedEventHandler
                // before dispose the dispatcher.
                Dispatcher.CurrentDispatcher.ShutdownFinished += new EventHandler(OnDispatcherShutdownFinished);
            }

            threadLocalStore.InputLanguageChangeEventHandlerCount++;
        }

        /// <summary>
        /// Remove the input language changed event handler from UIContext data slot.
        /// </summary>
        internal static void _RemoveInputLanguageChangedEventHandler(TextEditor This)
        {
            TextEditorThreadLocalStore threadLocalStore;

            threadLocalStore = TextEditor._ThreadLocalStore;

            // Decrease the input language changed event handler reference count
            threadLocalStore.InputLanguageChangeEventHandlerCount--;

            // Remove the input language changed event handler when nobody reference it
            if (threadLocalStore.InputLanguageChangeEventHandlerCount == 0)
            {
                // Remove InputLanguageEventHandler
                InputLanguageManager.Current.InputLanguageChanged -= new InputLanguageEventHandler(OnInputLanguageChanged);

                // Remove the dispatcher shutdown finished event handler
                Dispatcher.CurrentDispatcher.ShutdownFinished -= new EventHandler(OnDispatcherShutdownFinished);
            }
        }

        /// <summary>
        /// Discards previous typing undo unit, to prevent
        /// from merging it with the subsequent typing.
        /// </summary>
        internal static void _BreakTypingSequence(TextEditor This)
        {
            // Discard typing undo unit
            This._typingUndoUnit = null;
        }

        // Handles any pending input events.
        internal static void _FlushPendingInputItems(TextEditor This)
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

        // Un-hides the mouse cursor.
        internal static void _ShowCursor()
        {
            if (TextEditor._ThreadLocalStore.HideCursor)
            {
                TextEditor._ThreadLocalStore.HideCursor = false;
                SafeNativeMethods.ShowCursor(true);
            }
        }

        // ................................................................
        //
        // Event Handlers
        //
        // ................................................................

        /// <summary>
        /// Removes selected content in a RichTextBox when an IME is jump-starting a composition
        /// over existing content.
        /// </summary>
        /// <remarks>
        /// This is a work around for a messy situation we get into when a composition starts
        /// over a non-empty selection in the RichTextBox (eg, a user selects some content and
        /// then starts typing with an IME).
        /// 
        /// In general, code in TextStore.cs tracks IME composition events with character offsets
        /// and often restores and then "plays back" the changes.  If the character offsets of the
        /// original and played back composition events do not match exactly, the editor enters
        /// an inconsistent state and crashes or worse.
        /// 
        /// There is code in TextStore.OnStartComposition that attempts to ensure character offsets
        /// always match in the case where an IME starts a non-empty composition.  Comments in the
        /// method have details.
        /// 
        /// However, that code only handles the case where the selection is empty (a caret/single
        /// insertion point).  It will in fact cause problems if the composition is non-empty
        /// because the initial selection was non-empty.  In that case, the code in
        /// TextStore.OnStartComposition attempts to convert
        /// elements like LineBreak that are normally invisible to the IME but round-trip as text
        /// like "\r\n". This confuses the before/after playback state because character counts
        /// do not match.
        /// 
        /// The code here is a work around -- if we detect a composition start request with a non-empty
        /// selection, we preemtively remove the selected content before the IME has a chance to do
        /// so.  We can't do that work later because once the IME has started a composition no
        /// reentrant edits are allowed.
        /// 
        /// Modifying the document from PreviewKeyDown is not ideal.  A better long term solution
        /// is to change the way we expose the document to the IME so that reentrancy is not an
        /// issue.  However, we don't have time left in dev10 to do that work.  This solution is
        /// a compromise that avoids serious crashes while typing over selected text with IMEs.
        /// </remarks>
        internal static void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.ImeProcessed)
            {
                return;
            }

            RichTextBox richTextBox = sender as RichTextBox;

            if (richTextBox == null)
            {
                return;
            }

            TextEditor This = richTextBox.TextEditor;

            if (This == null || !This._IsEnabled || This.IsReadOnly || !This._IsSourceInScope(e.OriginalSource))
            {
                return;
            }

            // Ignore repeated events generated when the key is hold down for long time
            if (e.IsRepeat)
            {
                return;
            }

            if (This.TextStore == null || 
                This.TextStore.IsComposing)
            {
                return;
            }

            if (richTextBox.Selection.IsEmpty)
            {
                return;
            }

            This.SetText(This.Selection, String.Empty, InputLanguageManager.Current.CurrentInputLanguage);

            // NB: we do not handle the event.  We want the IME to handle it.
        }

        // KeyDownEvent handler - needed for handling FlowDirection commands on KeyUp
        internal static void OnKeyDown(object sender, KeyEventArgs e)
        {
            TextEditor This = TextEditor._GetTextEditor(sender);

            if (This == null || !This._IsEnabled || (This.IsReadOnly && !This.IsReadOnlyCaretVisible) || !This._IsSourceInScope(e.OriginalSource))
            {
                return;
            }

            // Ignore repeated events generated when the key is hold down for long time
            if (e.IsRepeat)
            {
                return;
            }

            // If UiScope has a ToolTip and it is open, any keyboard/mouse activity should close the tooltip.
            This.CloseToolTip();

            TextEditorThreadLocalStore threadLocalStore = TextEditor._ThreadLocalStore;

            // Clear a flag indicating that Shift key was pressed without any following key
            // This flag is necessary for KeyUp(RightShift/LeftShift) processing.
            threadLocalStore.PureControlShift = false;

            // Shift+Ctrl combination must be executed only when it's "pure" -
            // no mouse dragging/movement, no other key downs involved in a gesture.
            if (This.TextView != null && !This.UiScope.IsMouseCaptured)
            {
                if ((e.Key == Key.RightShift || e.Key == Key.LeftShift) && //
                    (e.KeyboardDevice.Modifiers & ModifierKeys.Control) != 0 && (e.KeyboardDevice.Modifiers & ModifierKeys.Alt) == 0)
                {
                    threadLocalStore.PureControlShift = true; // will be cleared by any other key down
                }
                else if ((e.Key == Key.RightCtrl || e.Key == Key.LeftCtrl) && //
                    (e.KeyboardDevice.Modifiers & ModifierKeys.Shift) != 0 && (e.KeyboardDevice.Modifiers & ModifierKeys.Alt) == 0)
                {
                    threadLocalStore.PureControlShift = true; // will be cleared by any other key down
                }
                else if (e.Key == Key.RightCtrl || e.Key == Key.LeftCtrl)
                {
                    UpdateHyperlinkCursor(This);
                }
            }
        }

        // Handler for KeyUp events
        internal static void OnKeyUp(object sender, KeyEventArgs e)
        {
            TextEditor This = TextEditor._GetTextEditor(sender);

            if (This == null || !This._IsEnabled || (This.IsReadOnly && !This.IsReadOnlyCaretVisible) || !This._IsSourceInScope(e.OriginalSource))
            {
                return;
            }

            // Delegate the work to specific handlers.
            switch (e.Key)
            {
                case Key.RightShift:
                case Key.LeftShift:
                    if (TextEditor._ThreadLocalStore.PureControlShift && (e.KeyboardDevice.Modifiers & ModifierKeys.Alt) == 0)
                    {
                        TextEditorTyping.ScheduleInput(This, new KeyUpInputItem(This, e.Key, e.KeyboardDevice.Modifiers));
                    }
                    break;

                case Key.LeftCtrl:
                case Key.RightCtrl:
                    UpdateHyperlinkCursor(This);
                    break;
            }
        }


        // TextInputEvent handler.
        internal static void OnTextInput(object sender, TextCompositionEventArgs e)
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

            FrameworkTextComposition composition = e.TextComposition as FrameworkTextComposition;

            // Ignore any event with an empty Text property.
            // The public TextCompositionEventArgs ctor allows null Text values.
            // Also it's possible to have non-null ControlText or AltText with String.Empty Text values.
            if (composition == null &&
                (e.Text == null || e.Text.Length == 0))
            {
                // ── T1c P6b2：第二道门（空文本）──
                WpfLinuxPfInputTrace.P6BailEmptyText(e, composition == null,
                                                     (e.Text == null) ? -1 : e.Text.Length);
                return;
            }

            // Consider event handled
            e.Handled = true;

            // ── T1c P6c：两道门都过了 ⇒ 后面该走插入 ──
            WpfLinuxPfInputTrace.P6ConsiderHandled(This, composition, e);

            This.TextView?.ThrottleBackgroundTasksForUserInput();

            // If this event is our Cicero TextStore composition, we always handles through ITextStore::SetText.
            if (composition != null)
            {
                if (composition.Owner == This.TextStore)
                {
                    This.TextStore.UpdateCompositionText(composition);
                }
                else if (composition.Owner == This.ImmComposition)
                {
                    This.ImmComposition.UpdateCompositionText(composition);
                }
            }
            else
            {
                // Input text (with springload formatting if any)
                // We'll delay the event handling, batching it up with other
                // input if layout is too slow to keep up with the input stream.
                KeyboardDevice keyboard = e.Device as KeyboardDevice;
                TextEditorTyping.ScheduleInput(This, new TextInputItem(This, e.Text, /*isInsertKeyToggled:*/keyboard != null ? keyboard.IsKeyToggled(Key.Insert) : false));
                // ── T1c P6d：插入**已排队**（再往后是调度与 TextContainer，不在本批）──
                WpfLinuxPfInputTrace.P6ScheduleInput(This, e.Text, e.Handled);
            }
        }

        #endregion Class Internal Methods

        //------------------------------------------------------
        //
        //  Private Methods
        //
        //------------------------------------------------------

        #region Private Methods

        // ................................................................
        //
        // Command Handlers
        //
        // ................................................................

        /// <summary>
        /// CorrectionList command QueryStatus handler
        /// </summary>
        private static void OnQueryStatusCorrectionList(object target, CanExecuteRoutedEventArgs args)
        {
            TextEditor This = TextEditor._GetTextEditor(target);

            if (This == null)
            {
                return;
            }

            if (This.TextStore != null)
            {
                // Don't do actual reconversion, it just checks if the current selection is reconvertable.
                args.CanExecute = This.TextStore.QueryRangeOrReconvertSelection( /*fDoReconvert:*/ false);
            }
            else
            {
                // If there is no textstore, this command is not enabled.
                args.CanExecute = false;
            }
        }

        /// <summary>
        /// CorrectionList command event handler.
        /// </summary>
        private static void OnCorrectionList(object target, ExecutedRoutedEventArgs args)
        {
            TextEditor This = TextEditor._GetTextEditor(target);

            if (This == null)
            {
                return;
            }

            This.TextStore?.QueryRangeOrReconvertSelection( /*fDoReconvert:*/ true);
        }

        /// <summary>
        /// ToggleInsert command handler
        /// </summary>
        private static void OnToggleInsert(object target, ExecutedRoutedEventArgs args)
        {
            TextEditor This = TextEditor._GetTextEditor(target);

            if (This == null || !This._IsEnabled || This.IsReadOnly)
            {
                return;
            }

            This._OvertypeMode = !This._OvertypeMode;

            // Use Cicero's transitory extension for OverTyping.
            if (TextServicesLoader.ServicesInstalled && (This.TextStore != null))
            {
                TextServicesHost tsfHost = TextServicesHost.Current;
                if (tsfHost != null)
                {
                    if (This._OvertypeMode)
                    {
                        IInputElement element = target as IInputElement;
                        if (element != null)
                        {
                            PresentationSource.AddSourceChangedHandler(element, OnSourceChanged);
                        }
                        
                        TextServicesHost.StartTransitoryExtension(This.TextStore);
                    }
                    else
                    {
                        IInputElement element = target as IInputElement;
                        if (element != null)
                        {
                            PresentationSource.RemoveSourceChangedHandler(element, OnSourceChanged);
                        }
                        
                        TextServicesHost.StopTransitoryExtension(This.TextStore);
                    }
                }
            }
        }

        // This should only be invoked on a text control in Overtype mode being tracked for presentation source 
        // changes. Connecting or disconnecting from a window fires this notification.
        private static void OnSourceChanged(object sender, SourceChangedEventArgs args)
        {
            OnToggleInsert(sender, null);
        }

        // ...........................................................................
        //
        // Delete Characters
        //
        // ...........................................................................

        private static void OnDelete(object sender, ExecutedRoutedEventArgs args)
        {
            TextEditor This = TextEditor._GetTextEditor(sender);

            if (This == null || !This._IsEnabled || This.IsReadOnly || !This._IsSourceInScope(args.Source))
            {
                return;
            }

            TextEditorTyping._FlushPendingInputItems(This);

            // Note, that Delete and Backspace keys behave differently.
            ((TextSelection)This.Selection).ClearSpringloadFormatting();

            // Forget previously suggested horizontal position
            TextEditorSelection._ClearSuggestedX(This);

            using (This.Selection.DeclareChangeBlock())
            {
                ITextPointer position = This.Selection.End;
                if (This.Selection.IsEmpty)
                {
                    ITextPointer deletePosition = position.GetNextInsertionPosition(LogicalDirection.Forward);

                    if (deletePosition == null)
                    {
                        // Nothing to delete.
                        return;
                    }

                    if (TextPointerBase.IsAtRowEnd(deletePosition))
                    {
                        // Backspace and delete are a no-op at row end positions.
                        return;
                    }

                    if (position is TextPointer && !IsAtListItemStart(deletePosition) &&
                        HandleDeleteWhenStructuralBoundaryIsCrossed(This, (TextPointer)position, (TextPointer)deletePosition))
                    {
                        // We are crossing structural boundary and
                        // selection was updated in HandleDeleteWhenStructuralBoundaryIsCrossed.
                        return;
                    }

                    // Selection is empty, extend selection forward to delete the following char.
                    This.Selection.ExtendToNextInsertionPosition(LogicalDirection.Forward);
                }

                // Delete selected text.
                This.Selection.Text = String.Empty;
            }
        }

        private static void OnBackspace(object sender, ExecutedRoutedEventArgs args)
        {
            TextEditor This = TextEditor._GetTextEditor(sender);

            if (This == null || !This._IsEnabled || This.IsReadOnly || !This._IsSourceInScope(args.Source))
            {
                return;
            }

            TextEditorTyping._FlushPendingInputItems(This);

            // Forget previously suggested horizontal position.
            TextEditorSelection._ClearSuggestedX(This);

            using (This.Selection.DeclareChangeBlock())
            {
                ITextPointer position = This.Selection.Start;

                // Note that this is different than the previous insertion position in backward direction,
                // in case of combining characters and surrogates.
                ITextPointer backspacePosition = null;

                // In case when selection is empty we will need to expand
                // it backward. Check first whether we are crossing
                // any structural boundary - to disable the operation
                // in such case.
                if (This.Selection.IsEmpty)
                {
                    // Identify a case for special actions in the beginning of paragraphs or list items

                    if (This.AcceptsRichContent && IsAtListItemStart(position))
                    {
                        // Remove a bullet from this list item.
                        // Note that doing anything more aggressive like unindenting
                        // would make backspace very inconvenient for merging two same-level list items.
                        TextRangeEditLists.ConvertListItemsToParagraphs((TextRange)This.Selection);
                    }
                    else if (This.AcceptsRichContent &&
                             (IsAtListItemChildStart(position, false /* emptyChildOnly */) || IsAtIndentedParagraphOrBlockUIContainerStart(This.Selection.Start)))
                    {
                        // Unindent the list by one level.
                        TextEditorLists.DecreaseIndentation(This);
                    }
                    else
                    {
                        // Find a preceding position.
                        ITextPointer deletePosition = position.GetNextInsertionPosition(LogicalDirection.Backward);

                        if (deletePosition == null)
                        {
                            // Nothing to delete.
                            ((TextSelection)This.Selection).ClearSpringloadFormatting();
                            return;
                        }

                        if (TextPointerBase.IsAtRowEnd(deletePosition))
                        {
                            // Backspace and delete are a no-op at row end positions.
                            ((TextSelection)This.Selection).ClearSpringloadFormatting();
                            return;
                        }

                        if (position is TextPointer &&
                            HandleDeleteWhenStructuralBoundaryIsCrossed(This, (TextPointer)position, (TextPointer)deletePosition))
                        {
                            // We are crossing structural boundary and
                            // selection was updated in HandleDeleteWhenStructuralBoundaryIsCrossed.
                            return;
                        }

                        // Normalize the current position backward.
                        position = position.GetFrozenPointer(LogicalDirection.Backward);

                        // If TextView is valid, we can get the backspace position from TextView and then
                        // delete the content from the backspace position to the current position.
                        // Otherwise, we move the selection to the previous insertion position then delete.
                        if (This.TextView != null &&
                            position.HasValidLayout &&
                            position.GetPointerContext(LogicalDirection.Backward) == TextPointerContext.Text)
                        {
                            // Get the backspace caret unit position from TextView that support surrogate
                            // and all internal characters
                            backspacePosition = This.TextView.GetBackspaceCaretUnitPosition(position);
                            Invariant.Assert(backspacePosition != null);

                            // bug 1733868
                            // backspacePosition should always be less than position.
                            // But backspacing before '\n' (no preceding '\r') exposes
                            // this bug.
                            if (backspacePosition.CompareTo(position) == 0)
                            {
                                // As of 6/30/2006 we're too close to ship to fix
                                // this bug cleanly.  Ideally, we would stop referencing
                                // the position at the end-of-line (which mil text does not
                                // consider a valid position), and instead reference the start
                                // of the next line (flipping the original position's gravity).
                                //
                                // As a work-around, take the previous insertion position,
                                // ignoring glyph level backspace positions.
                                //
                                This.Selection.ExtendToNextInsertionPosition(LogicalDirection.Backward);
                                backspacePosition = null;
                            }
                            // If there is no text preceding the backspacePosition, extend to the next
                            // insertion position to make sure we cleanup any empty Inlines left
                            // after the delete.  We don't want a non-empty selection if there is
                            // bordering text, because we might normalize outside of a run of combining
                            // marks otherwise.
                            else if (backspacePosition.GetPointerContext(LogicalDirection.Backward) != TextPointerContext.Text)
                            {
                                This.Selection.Select(This.Selection.End, backspacePosition);
                                backspacePosition = null;
                            }
                        }
                        else
                        {
                            // Selection is empty, extend it backward to include the preceeding char.
                            This.Selection.ExtendToNextInsertionPosition(LogicalDirection.Backward);
                        }
                    }
                }

                // Save current formatting properties for springload formatting before backspace
                // Note, that Delete and Backspace keys behave differently: it's by design.
                if (This.AcceptsRichContent)
                {
                    ((TextSelection)This.Selection).ClearSpringloadFormatting();
                    ((TextSelection)This.Selection).SpringloadCurrentFormatting();
                }

                // If backspace position is available from TextView, we can delete it directly
                // without the normalization. Because we already normalized the backspace position.
                if (backspacePosition != null)
                {
                    Invariant.Assert(backspacePosition.CompareTo(position) < 0);
                    // Delete the content from the backspace to the current position
                    backspacePosition.DeleteContentToPosition(position);
                }
                else
                {
                    // Delete selected text
                    This.Selection.Text = String.Empty;
                    position = This.Selection.Start;
                }

                // Set the caret position with the Backward direction,
                // because we want to appear close to previous character.
                // However, we do not allow to stop at end of line.
                // We alow to stop next to space - to be consistent with typing behavior.
                This.Selection.SetCaretToPosition(position, LogicalDirection.Backward, /*allowStopAtLineEnd:*/false, /*allowStopNearSpace:*/true);
            }
        }

        // Helper for OnDelete/OnBackspace, handles special case scenarios for delete when table or BlockUIContainer boundaries are crossed.
        // Returns true if passed positions were in this category and appropriate editing action was taken for handling delete operation.
        // Otherwise, returns false.
        private static bool HandleDeleteWhenStructuralBoundaryIsCrossed(TextEditor This, TextPointer position, TextPointer deletePosition)
        {
            if (!TextRangeEditTables.IsTableStructureCrossed(position, deletePosition) &&
                !IsBlockUIContainerBoundaryCrossed(position, deletePosition) &&
                !TextPointerBase.IsAtRowEnd(position))
            {
                return false;
            }

            LogicalDirection directionOfDelete = position.CompareTo(deletePosition) < 0 ? LogicalDirection.Forward : LogicalDirection.Backward;

            Block paragraphOrBlockUIContainerToDelete = position.ParagraphOrBlockUIContainer;

            // Check if an empty paragraph or BlockUIContainer needs to be deleted.
            if (paragraphOrBlockUIContainerToDelete != null)
            {
                if (directionOfDelete == LogicalDirection.Forward)
                {
                    // We check for next/previous block here, to avoid deletion of an empty paragraph when a list/table boundary is crossed.
                    // Note however, we dont treat sections as structural boundaries. So this check does not let us delete a last empty
                    // paragraph in a section. Investigate the section case more...

                    if (paragraphOrBlockUIContainerToDelete.NextBlock != null &&
                        paragraphOrBlockUIContainerToDelete is Paragraph && Paragraph.HasNoTextContent((Paragraph)paragraphOrBlockUIContainerToDelete) || // empty paragraph
                        paragraphOrBlockUIContainerToDelete is BlockUIContainer && paragraphOrBlockUIContainerToDelete.IsEmpty) // empty BlockUIContainer
                    {
                        paragraphOrBlockUIContainerToDelete.RepositionWithContent(null);
                    }
                }
                else
                {
                    if (paragraphOrBlockUIContainerToDelete.PreviousBlock != null &&
                        paragraphOrBlockUIContainerToDelete is Paragraph && Paragraph.HasNoTextContent((Paragraph)paragraphOrBlockUIContainerToDelete) || // empty paragraph
                        paragraphOrBlockUIContainerToDelete is BlockUIContainer && paragraphOrBlockUIContainerToDelete.IsEmpty) // empty BlockUIContainer
                    {
                        paragraphOrBlockUIContainerToDelete.RepositionWithContent(null);
                    }
                }
            }

            // Set caret position.
            This.Selection.SetCaretToPosition(deletePosition, directionOfDelete, /*allowStopAtLineEnd:*/false, /*allowStopNearSpace:*/true);

            if (directionOfDelete == LogicalDirection.Backward)
            {
                // Clear springload formatting in case of backspace
                ((TextSelection)This.Selection).ClearSpringloadFormatting();
            }

            return true;
        }

        // Tests if the position is at the beginning of indented paragraph -
        // to allow Backspace to decrease indentation
        private static bool IsAtIndentedParagraphOrBlockUIContainerStart(ITextPointer position)
        {
            if ((position is TextPointer) && TextPointerBase.IsAtParagraphOrBlockUIContainerStart(position))
            {
                Block paragraphOrBlockUIContainer = ((TextPointer)position).ParagraphOrBlockUIContainer;
                if (paragraphOrBlockUIContainer != null)
                {
                    FlowDirection flowDirection = paragraphOrBlockUIContainer.FlowDirection;
                    Thickness margin = paragraphOrBlockUIContainer.Margin;

                    return
                        flowDirection == FlowDirection.LeftToRight && margin.Left > 0 ||
                        flowDirection == FlowDirection.RightToLeft && margin.Right > 0 ||
                        (paragraphOrBlockUIContainer is Paragraph && ((Paragraph)paragraphOrBlockUIContainer).TextIndent > 0);
                }
            }

            return false;
        }

        // Tests if the position is at the beginning of some list item -
        // to allow Backspace to delete the bullet.
        private static bool IsAtListItemStart(ITextPointer position)
        {
            // Check for empty ListItem case
            if (typeof(ListItem).IsAssignableFrom(position.ParentType) &&
                position.GetPointerContext(LogicalDirection.Backward) == TextPointerContext.ElementStart &&
                position.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.ElementEnd)
            {
                return true;
            }

            while (position.GetPointerContext(LogicalDirection.Backward) == TextPointerContext.ElementStart)
            {
                Type parentType = position.ParentType;
                if (TextSchema.IsBlock(parentType))
                {
                    if (TextSchema.IsParagraphOrBlockUIContainer(parentType))
                    {
                        position = position.GetNextContextPosition(LogicalDirection.Backward);
                        if (position.GetPointerContext(LogicalDirection.Backward) == TextPointerContext.ElementStart &&
                            typeof(ListItem).IsAssignableFrom(position.ParentType))
                        {
                            return true;
                        }
                    }
                    return false;
                }
                position = position.GetNextContextPosition(LogicalDirection.Backward);
            }
            return false;
        }

        // Tests if a position is at the start of a Block
        // within a ListItem.
        //
        // position must be normalized at an insertion point.
        private static bool IsAtListItemChildStart(ITextPointer position, bool emptyChildOnly)
        {
            if (position.GetPointerContext(LogicalDirection.Backward) != TextPointerContext.ElementStart)
            {
                return false;
            }

            if (emptyChildOnly &&
                position.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.ElementEnd)
            {
                return false;
            }

            ITextPointer navigator = position.CreatePointer();

            // Cross inline opening tags.
            while (navigator.GetPointerContext(LogicalDirection.Backward) == TextPointerContext.ElementStart &&
                   typeof(Inline).IsAssignableFrom(navigator.ParentType))
            {
                navigator.MoveToElementEdge(ElementEdge.BeforeStart);
            }

            // Check if navigator is at the start of a block.
            if (!(navigator.GetPointerContext(LogicalDirection.Backward) == TextPointerContext.ElementStart &&
                TextSchema.IsParagraphOrBlockUIContainer(navigator.ParentType)))
            {
                return false;
            }

            // Move just past the block.
            navigator.MoveToElementEdge(ElementEdge.BeforeStart);
            return typeof(ListItem).IsAssignableFrom(navigator.ParentType);
        }

        // Tests if position1 and position2 cross a BlockUIContainer boundary.
        private static bool IsBlockUIContainerBoundaryCrossed(TextPointer position1, TextPointer position2)
        {
            return
                (position1.Parent is BlockUIContainer || position2.Parent is BlockUIContainer) &&
                position1.Parent != position2.Parent;
        }

        // ...........................................................................
        //
        // Delete Words
        //
        // ...........................................................................

        private static void OnDeleteNextWord(object sender, ExecutedRoutedEventArgs args)
        {
            TextEditor This = TextEditor._GetTextEditor(sender);

            if (This == null || !This._IsEnabled || This.IsReadOnly)
            {
                return;
            }

            if (This.Selection.IsTableCellRange)
            {
                return;
            }

            TextEditorTyping._FlushPendingInputItems(This);

            ITextPointer wordBoundary = This.Selection.End.CreatePointer();

            // When selection is not empty the command deletes selected content
            // without extending it to the word bopundary. For empty selection
            // the command deletes a content from caret position to
            // nearest word boundary in a given direction
            if (This.Selection.IsEmpty)
            {
                TextPointerBase.MoveToNextWordBoundary(wordBoundary, LogicalDirection.Forward);
            }

            if (TextRangeEditTables.IsTableStructureCrossed(This.Selection.Start, wordBoundary))
            {
                return;
            }

            ITextRange textRange = new TextRange(This.Selection.Start, wordBoundary);

            // When a range is TableCellRange we do not want to make deletions
            if (textRange.IsTableCellRange)
            {
                return;
            }

            if (!textRange.IsEmpty)
            {
                using (This.Selection.DeclareChangeBlock())
                {
                    // Note asymetry with Backspace: we do not load springload formatting here
                    if (This.AcceptsRichContent)
                    {
                        ((TextSelection)This.Selection).ClearSpringloadFormatting();
                    }

                    This.Selection.Select(textRange.Start, textRange.End);

                    // Delete selected text
                    This.Selection.Text = String.Empty;
                }
            }
        }

        private static void OnDeletePreviousWord(object sender, ExecutedRoutedEventArgs args)
        {
            TextEditor This = TextEditor._GetTextEditor(sender);

            if (This == null || !This._IsEnabled || This.IsReadOnly)
            {
                return;
            }

            if (This.Selection.IsTableCellRange)
            {
                //  Add code for clearing table cell range contents
                return;
            }

            TextEditorTyping._FlushPendingInputItems(This);

            ITextPointer wordBoundary = This.Selection.Start.CreatePointer();

            // When selection is not empty the command deletes selected content
            // without extending it to the word bopundary. For empty selection
            // the command deletes a content from caret position to
            // nearest word boundary in a given direction
            if (This.Selection.IsEmpty)
            {
                TextPointerBase.MoveToNextWordBoundary(wordBoundary, LogicalDirection.Backward);
            }

            // When the movement to word boundary crosses table structure, ignore the command
            if (TextRangeEditTables.IsTableStructureCrossed(wordBoundary, This.Selection.Start))
            {
                return;
            }

            // Build a range from a start of a word preceding start of selection, ending at the end of whole selection
            // This range is supposed to be deleted by the operation.
            ITextRange textRange = new TextRange(wordBoundary, This.Selection.End);

            // When a range is TableCellRange we do not want to make deletions
            if (textRange.IsTableCellRange)
            {
                return;
            }

            if (!textRange.IsEmpty)
            {
                using (This.Selection.DeclareChangeBlock())
                {
                    // Note asymetry with Backspace: we DO load springload formatting here
                    if (This.AcceptsRichContent)
                    {
                        ((TextSelection)This.Selection).ClearSpringloadFormatting();
                        This.Selection.Select(textRange.Start, textRange.End);
                        ((TextSelection)This.Selection).SpringloadCurrentFormatting();
                    }
                    else
                    {
                        This.Selection.Select(textRange.Start, textRange.End);
                    }

                    // Delete selected text
                    This.Selection.Text = String.Empty;
                }
            }
        }

        // ...........................................................................
        //
        // Enter Breaks
        //
        // ...........................................................................

        /// <summary>
        /// EnterParagraphBreak/EnterLineBreak command QueryStatus handler
        /// </summary>
        private static void OnQueryStatusEnterBreak(object sender, CanExecuteRoutedEventArgs args)
        {
            TextEditor This = TextEditor._GetTextEditor(sender);

            if (This == null || !This._IsEnabled || This.IsReadOnly)
            {
                args.ContinueRouting = true;
                return;
            }

            if (This.Selection.IsTableCellRange || !This.AcceptsReturn)
            {
                args.ContinueRouting = true;
                return;
            }

            args.CanExecute = true;
        }

        // EnterParagraphBreak/EnterLineBreak command handler
        private static void OnEnterBreak(object sender, ExecutedRoutedEventArgs args)
        {
            TextEditor This = TextEditor._GetTextEditor(sender);

            if (This == null || !This._IsEnabled || This.IsReadOnly)
            {
                return;
            }

            if (This.Selection.IsTableCellRange || !This.AcceptsReturn || !This.UiScope.IsKeyboardFocused)
            {
                return;
            }

            TextEditorTyping._FlushPendingInputItems(This);

            // We do not merge Enter typing with other typing - for better undo structuring
            using (This.Selection.DeclareChangeBlock())
            {
                // Flag to indicate if selection was changed. It may be unaffected in following cases:
                // 1. In plain text case, Environment.NewLine can not fit in MaxLength
                // 2. In rich text case, we cannot split a hyperlink ancestor to insert a paragraph break
                bool wasSelectionChanged;

                if (This.AcceptsRichContent && This.Selection.Start is TextPointer)
                {
                    // Paragraph insertion for the case of rich text
                    wasSelectionChanged = HandleEnterBreakForRichText(This, args.Command);
                }
                else
                {
                    // Newline insertion for plain text
                    wasSelectionChanged = HandleEnterBreakForPlainText(This);
                }

                // Update caret and clear SuggestedX only when selection has changed.
                if (wasSelectionChanged)
                {
                    // Position the caret.
                    This.Selection.SetCaretToPosition(This.Selection.End, LogicalDirection.Forward, /*allowStopAtLineEnd:*/false, /*allowStopNearSpace:*/false);

                    // Forget previously suggested horizontal position
                    TextEditorSelection._ClearSuggestedX(This);
                }
            }
        }

        // Helper for OnEnterBreak for rich text case
        private static bool HandleEnterBreakForRichText(TextEditor This, ICommand command)
        {
            bool wasSelectionChanged = true;

            // Save current inline settings to continue on the next paragraph
            ((TextSelection)This.Selection).SpringloadCurrentFormatting();

            if (!This.Selection.IsEmpty)
            {
                // Delete selected content
                This.Selection.Text = String.Empty;
            }

            if (HandleEnterBreakWhenStructuralBoundaryIsCrossed(This, command))
            {
                // We are crossing structural boundary and
                // selection was updated if HandleEnterBreakWhenStructuralBoundaryIsCrossed returned true
            }
            else
            {
                TextPointer newEnd = ((TextSelection)This.Selection).End;

                if (command == EditingCommands.EnterParagraphBreak)
                {
                    if (newEnd.HasNonMergeableInlineAncestor && !TextPointerBase.IsPositionAtNonMergeableInlineBoundary(newEnd))
                    {
                        // Selection end is in the middle of a hyperlink element, enter is a no-op.
                        wasSelectionChanged = false;
                    }
                    else
                    {
                        newEnd = TextRangeEdit.InsertParagraphBreak(newEnd, /*moveIntoSecondParagraph*/true);
                    }
                }
                else if (command == EditingCommands.EnterLineBreak)
                {
                    newEnd = newEnd.InsertLineBreak();
                }

                if (wasSelectionChanged)
                {
                    This.Selection.Select(newEnd, newEnd);
                }
            }

            return wasSelectionChanged;
        }

        // Helper for OnEnterBreak for plain text case
        private static bool HandleEnterBreakForPlainText(TextEditor This)
        {
            bool wasSelectionChanged = true;

            // Filter Environment.NewLine based on TextBox.MaxLength
            string filteredText = This._FilterText(Environment.NewLine, This.Selection);

            if (filteredText != String.Empty)
            {
                This.Selection.Text = Environment.NewLine;
            }
            else
            {
                // Do not update selection if Environment.NewLine can not fit in.
                wasSelectionChanged = false;
            }

            return wasSelectionChanged;
        }

        // Helper for rich text OnEnterBreak case, handles special cases when a
        // structural boundary such as listitem, table, blockuicontainer is crossed.
        private static bool HandleEnterBreakWhenStructuralBoundaryIsCrossed(TextEditor This, ICommand command)
        {
            Invariant.Assert(This.Selection.Start is TextPointer);
            TextPointer position = (TextPointer)This.Selection.Start;

            bool structuralBoundaryIsCrossed = true;

            if (TextPointerBase.IsAtRowEnd(position))
            {
                // For both ParagraphBreak and LineBreak commands, insert a new row after the current one
                TextRange range = ((TextSelection)This.Selection).InsertRows(+1);
                This.Selection.SetCaretToPosition(range.Start, LogicalDirection.Forward, /*allowStopAtLineEnd:*/false, /*allowStopNearSpace:*/false);
            }
            else if (This.Selection.IsEmpty &&
                     (TextPointerBase.IsInEmptyListItem(position) || IsAtListItemChildStart(position, true /* emptyChildOnly */)) &&
                     command == EditingCommands.EnterParagraphBreak)
            {
                // Unindent the list by one level.
                TextEditorLists.DecreaseIndentation(This);
            }
            else if (TextPointerBase.IsBeforeFirstTable(position) ||
                TextPointerBase.IsAtBlockUIContainerStart(position))
            {
                // Calling EnsureInsertionPosition has the effect of inserting a paragraph BEFORE the table or BlockUIContainer/Table.
                // In this case, we do not want to move selection end to the paragraph just created.

                TextRangeEditTables.EnsureInsertionPosition(position);
            }
            else if (TextPointerBase.IsAtBlockUIContainerEnd(position))
            {
                // Calling EnsureInsertionPosition has the effect of inserting a paragraph AFTER the BlockUIContainer.
                // Update selection end to position in the following paragraph.

                TextPointer newEnd = TextRangeEditTables.EnsureInsertionPosition(position);
                This.Selection.Select(newEnd, newEnd);
            }
            else
            {
                structuralBoundaryIsCrossed = false;
            }

            return structuralBoundaryIsCrossed;
        }

        // ...........................................................................
        //
        // Flow Direction
        //
        // ...........................................................................

        /// <summary>
        /// LeftToRightFlowDirection command event handler.
        /// </summary>
        private static void OnFlowDirectionCommand(TextEditor This, Key key)
        {
            //  Detect appropriateness of FlowDirection command

            using (This.Selection.DeclareChangeBlock())
            {
                if (key == Key.LeftShift)
                {
                    if (This.AcceptsRichContent && (This.Selection is TextSelection))
                    {
                        // NOTE: We do not call OnApplyProperty to avoid recursion for FlushPendingInput
                        ((TextSelection)This.Selection).ApplyPropertyValue(FlowDocument.FlowDirectionProperty, FlowDirection.LeftToRight, /*applyToParagraphs*/true);
                    }
                    else
                    {
                        Invariant.Assert(This.UiScope != null);
                        UIElementPropertyUndoUnit.Add(This.TextContainer, This.UiScope, FrameworkElement.FlowDirectionProperty, FlowDirection.LeftToRight);
                        This.UiScope.SetValue(FrameworkElement.FlowDirectionProperty, FlowDirection.LeftToRight);
                    }
                }
                else
                {
                    Invariant.Assert(key == Key.RightShift);

                    if (This.AcceptsRichContent && (This.Selection is TextSelection))
                    {
                        // NOTE: We do not call OnApplyProperty to avoid recursion for FlushPendingInput
                        ((TextSelection)This.Selection).ApplyPropertyValue(FlowDocument.FlowDirectionProperty, FlowDirection.RightToLeft, /*applyToParagraphs*/true);
                    }
                    else
                    {
                        Invariant.Assert(This.UiScope != null);
                        UIElementPropertyUndoUnit.Add(This.TextContainer, This.UiScope, FrameworkElement.FlowDirectionProperty, FlowDirection.RightToLeft);
                        This.UiScope.SetValue(FrameworkElement.FlowDirectionProperty, FlowDirection.RightToLeft);
                    }
                }
                ((TextSelection)This.Selection).UpdateCaretState(CaretScrollMethod.Simple);
            }
        }

        // ...........................................................................
        //
        // In some controls, Space and Shift+Space keys are mapped to
        // scroll down and scroll up commands respectively.
        // In TextEditor, we handle them as text input.
        // Using the command system allows controls to override the existing default behavior.
        // ...........................................................................

        // Space, Shift+Space handler
        private static void OnSpace(object sender, ExecutedRoutedEventArgs e)
        {
            TextEditor This = TextEditor._GetTextEditor(sender);

            if (This == null || !This._IsEnabled || This.IsReadOnly || !This._IsSourceInScope(e.OriginalSource))
            {
                return;
            }

            // If this event is our Cicero TextStore composition, we always handles through ITextStore::SetText.
            if (This.TextStore != null && This.TextStore.IsComposing)
            {
                return;
            }

            if (This.ImmComposition != null && This.ImmComposition.IsComposition)
            {
                return;
            }

            // Consider event handled
            e.Handled = true;

            This.TextView?.ThrottleBackgroundTasksForUserInput();

            ScheduleInput(This, new TextInputItem(This, " ", /*isInsertKeyToggled:*/!This._OvertypeMode));
        }

        // ...........................................................................
        //
        // Tab and Back-Tab
        //
        // ...........................................................................

        /// <summary>
        /// ForwardTabStop command QueryStatus handler
        /// </summary>
        private static void OnQueryStatusTabForward(object sender, CanExecuteRoutedEventArgs args)
        {
            TextEditor This = TextEditor._GetTextEditor(sender);
            if (This != null && This.AcceptsTab)
            {
                args.CanExecute = true;
            }
            else
            {
                args.ContinueRouting = true;
            }
        }

        /// <summary>
        /// BackwardTabStop command QueryStatus handler
        /// </summary>
        private static void OnQueryStatusTabBackward(object sender, CanExecuteRoutedEventArgs args)
        {
            TextEditor This = TextEditor._GetTextEditor(sender);
            if (This != null && This.AcceptsTab)
            {
                args.CanExecute = true;
            }
            else
            {
                args.ContinueRouting = true;
            }
        }

        // Tab handler.
        private static void OnTabForward(object sender, ExecutedRoutedEventArgs args)
        {
            TextEditor This = TextEditor._GetTextEditor(sender);

            if (This == null || !This._IsEnabled || This.IsReadOnly || !This.UiScope.IsKeyboardFocused)
            {
                return;
            }

            TextEditorTyping._FlushPendingInputItems(This);

            if (HandleTabInTables(This, LogicalDirection.Forward))
            {
                // All done on table level.
                return;
            }

            if (This.AcceptsRichContent && (!This.Selection.IsEmpty || TextPointerBase.IsAtParagraphOrBlockUIContainerStart(This.Selection.Start)) &&
                EditingCommands.IncreaseIndentation.CanExecute(null, (IInputElement)sender))
            {
                // In RichTextBox Tab/Shift+Tab keys work as paragraph/list indentation
                EditingCommands.IncreaseIndentation.Execute(null, (IInputElement)sender);
            }
            else
            {
                // In plain text we treat tab as a characters always
                DoTextInput(This, "\t", /*isInsertKeyToggled:*/!This._OvertypeMode, /*acceptControlCharacters:*/true);
            }
        }

        // Shift+Tab handler.
        private static void OnTabBackward(object sender, ExecutedRoutedEventArgs args)
        {
            TextEditor This = TextEditor._GetTextEditor(sender);

            if (This == null || !This._IsEnabled || This.IsReadOnly || !This.UiScope.IsKeyboardFocused)
            {
                return;
            }

            // Implement paragraph decrease level command
            TextEditorTyping._FlushPendingInputItems(This);

            if (HandleTabInTables(This, LogicalDirection.Backward))
            {
                // All done on table level.
                return;
            }

            if (This.AcceptsRichContent && (!This.Selection.IsEmpty || TextPointerBase.IsAtParagraphOrBlockUIContainerStart(This.Selection.Start)) &&
                EditingCommands.DecreaseIndentation.CanExecute(null, (IInputElement)sender))
            {
                // In RichTextBox Tab/Shift+Tab keys work as paragraph/list indentation
                EditingCommands.DecreaseIndentation.Execute(null, (IInputElement)sender);
            }
            else
            {
                // In plain text we treat tab as a characters always
                DoTextInput(This, "\t", /*isInsertKeyToggled:*/!This._OvertypeMode, /*acceptControlCharacters:*/true);
            }
        }

        // Command handler for Tab and ShiftTab - moves caret between table cells
        // if the selection is within a table. Otherwise does nothing and returns false.
        private static bool HandleTabInTables(TextEditor This, LogicalDirection direction)
        {
            if (!This.AcceptsRichContent)
            {
                return false;
            }

            if (This.Selection.IsTableCellRange)
            {
                // When table cell range is selected, Tab simply collapses
                // a selection to a content of a first cell
                This.Selection.SetCaretToPosition(This.Selection.Start, LogicalDirection.Backward, /*allowStopAtLineEnd:*/false, /*allowStopNearSpace:*/false);
                return true;
            }

            if (This.Selection.IsEmpty && TextPointerBase.IsAtRowEnd(This.Selection.End))
            {
                // From the end of row we go to the first cell of a next row
                TableCell cell = null;
                TableRow row = ((TextPointer)This.Selection.End).Parent as TableRow;
                Invariant.Assert(row != null);
                TableRowGroup body = row.RowGroup;
                int rowIndex = body.Rows.IndexOf(row);

                if (direction == LogicalDirection.Forward)
                {
                    if (rowIndex + 1 < body.Rows.Count)
                    {
                        cell = body.Rows[rowIndex + 1].Cells[0];
                    }
                }
                else
                {
                    if (rowIndex > 0)
                    {
                        cell = body.Rows[rowIndex - 1].Cells[body.Rows[rowIndex - 1].Cells.Count - 1];
                    }
                }

                if (cell != null)
                {
                    This.Selection.Select(cell.ContentStart, cell.ContentEnd);
                }
                return true;
            }

            // Check if selection is within a table
            TextElement parent = ((TextPointer)This.Selection.Start).Parent as TextElement;
            while (parent != null && !(parent is TableCell))
            {
                parent = parent.Parent as TextElement;
            }
            if (parent is TableCell)
            {
                TableCell cell = (TableCell)parent;
                TableRow row = cell.Row;
                TableRowGroup body = row.RowGroup;

                int cellIndex = row.Cells.IndexOf(cell);
                int rowIndex = body.Rows.IndexOf(row);

                if (direction == LogicalDirection.Forward)
                {
                    if (cellIndex + 1 < row.Cells.Count)
                    {
                        cell = row.Cells[cellIndex + 1];
                    }
                    else if (rowIndex + 1 < body.Rows.Count)
                    {
                        cell = body.Rows[rowIndex + 1].Cells[0];
                    }
                    else
                    {
                        //  Add code for inserting new table row - at the end of a table
                    }
                }
                else
                {
                    if (cellIndex > 0)
                    {
                        cell = row.Cells[cellIndex - 1];
                    }
                    else if (rowIndex > 0)
                    {
                        cell = body.Rows[rowIndex - 1].Cells[body.Rows[rowIndex - 1].Cells.Count - 1];
                    }
                    else
                    {
                        //  Add code for inserting new table row - at the end of a table
                    }
                }

                Invariant.Assert(cell != null);
                This.Selection.Select(cell.ContentStart, cell.ContentEnd);
                return true;
            }

            return false;
        }

        // ......................................................
        //
        //  Handling Text Input
        //
        // ......................................................

        /// <summary>
        /// This is a single method used to insert user input characters.
        /// It takes care of typing undo, springload formatting, overtype mode etc.
        /// </summary>
        /// <param name="This"></param>
        /// <param name="textData">
        /// Text to insert.
        /// </param>
        /// <param name="isInsertKeyToggled">
        /// Reflects a state of Insert key at the moment of textData input.
        /// </param>
        /// <param name="acceptControlCharacters">
        /// True indicates that control characters like '\t' or '\r' etc. can be inserted.
        /// False means that all control characters are filtered out.
        /// </param>
        private static void DoTextInput(TextEditor This, string textData, bool isInsertKeyToggled, bool acceptControlCharacters)
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

            // Remove control characters. Note that this is not included into _FilterText,
            // because we want such kind of filtering only for real input,
            // not for copy/paste.
            if (!acceptControlCharacters)
            {
                for (int i = 0; i < textData.Length; i++)
                {
                    if (Char.IsControl(textData[i]))
                    {
                        textData = textData.Remove(i--, 1);  // decrement i to compensate for character removal
                    }
                }
            }

            string filteredText = This._FilterText(textData, This.Selection);

            // ── T1c Q4b：过滤后的文本（长度 0 ⇒ 下一行就 return ⇒ 静默无效果）──
            WpfLinuxPfInputTrace.Q4Filtered(This, textData, filteredText);

            if (filteredText.Length == 0)
            {
                return;
            }

            TextEditorTyping.OpenTypingUndoUnit(This);

            UndoCloseAction closeAction = UndoCloseAction.Rollback;

            try
            {
                using (This.Selection.DeclareChangeBlock())
                {
                    This.Selection.ApplyTypingHeuristics(This.AllowOvertype && This._OvertypeMode && filteredText != "\t");

                    // ── T1c Q4c：**真正写进容器的调用点之前** ──
                    WpfLinuxPfInputTrace.Q4BeforeWrite(This, filteredText);

                    This.SetSelectedText(filteredText, InputLanguageManager.Current.CurrentInputLanguage);

                    // ── T1c Q4d：写完之后——**文档到底变了没有**（本批最关键的一格）──
                    WpfLinuxPfInputTrace.Q4AfterWrite(This, filteredText);

                    // Create caret position normalized backward to keep formatting of a character just typed
                    ITextPointer caretPosition = This.Selection.End.CreatePointer(LogicalDirection.Backward);

                    // Set selection at the end of input content
                    This.Selection.SetCaretToPosition(caretPosition, LogicalDirection.Backward, /*allowStopAtLineEnd:*/true, /*allowStopNearSpace:*/true);
                    // Note: Using explicit backward orientation we keep formatting with
                    // a previous character during typing.

                    closeAction = UndoCloseAction.Commit;
                }
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

        // Takes state originating with a KeyDownEvent or TextInputEvent and
        // schedules it for eventual handling.
        //
        // Normally we delay handling until a Background priority event fires.
        // This has the effect of batching multiple input events when
        // layout cannot keep up with the input stream.
        //
        // However, if any mouse events are pending, we handle the event
        // immediately, since otherwise we risk the possibility of handling
        // the events out of order.
        private static void ScheduleInput(TextEditor This, InputItem item)
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

        // Returns true if any mouse input event is currently waiting in the
        // win32 message queue for processing.
        // Avalon doesn't keep a separate queue for input events.  Instead
        // it interleaves work items with the win32 input queue.
        private static bool IsMouseInputPending(TextEditor This)
        {
            bool mouseInputPending = false;
            IWin32Window win32Window = PresentationSource.CriticalFromVisual(This.UiScope) as IWin32Window;
            if (win32Window != null)
            {
                IntPtr hwnd = IntPtr.Zero;
                hwnd = win32Window.Handle;

                if (hwnd != (IntPtr)0)
                {
                    System.Windows.Interop.MSG message = new System.Windows.Interop.MSG();
                    mouseInputPending = UnsafeNativeMethods.PeekMessage(ref message, new HandleRef(null, hwnd), WindowMessage.WM_MOUSEFIRST, WindowMessage.WM_MOUSELAST, NativeMethods.PM_NOREMOVE);
                }
            }

            return mouseInputPending;
        }

        // Background priority callback used to process keystrokes.
        private static object BackgroundInputCallback(object This)
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

        /// <summary>
        /// Callback for shutdown finished dispatcher. Before shutdown dispatcher, we should clean
        /// InputLanguageChangedEventHandler.
        /// </summary>
        private static void OnDispatcherShutdownFinished(object sender, EventArgs args)
        {
            // Remove the dispatcher shutdown finished event handler
            Dispatcher.CurrentDispatcher.ShutdownFinished -= new EventHandler(OnDispatcherShutdownFinished);

            // Remove the input language changed event handler
            InputLanguageManager.Current.InputLanguageChanged -= new InputLanguageEventHandler(OnInputLanguageChanged);

            TextEditorThreadLocalStore threadLocalStore = TextEditor._ThreadLocalStore;

            // Clear InputLanguageChangeEventHandler count
            threadLocalStore.InputLanguageChangeEventHandlerCount = 0;
        }

        // InputLanguageChanged handler.
        private static void OnInputLanguageChanged(object sender, InputLanguageEventArgs e)
        {
            TextSelection.OnInputLanguageChanged(e.NewLanguage);
        }

        // Base class for keyboard/text input items.
        // Individual keystroke/text events are batched and handled together
        // when layout cannot keep up with the input stream.
        private abstract class InputItem
        {
            // Ctor.
            internal InputItem(TextEditor textEditor)
            {
                _textEditor = textEditor;
            }

            // Handles the input event.
            internal abstract void Do();

            // The TextEditor instance on which this input item applies.
            private TextEditor _textEditor;

            protected TextEditor TextEditor
            {
                get
                {
                    return _textEditor;
                }
            }
        }

        // Holds state originating from a single TextInputEvent.
        private class TextInputItem : InputItem
        {
            // Ctor.
            internal TextInputItem(TextEditor textEditor, string text, bool isInsertKeyToggled)
                : base (textEditor)
            {
                _text = text;
                _isInsertKeyToggled = isInsertKeyToggled;
            }

            // Inserts event content into the document.
            internal override void Do()
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

            // Text to input.
            private readonly string _text;
            private readonly bool _isInsertKeyToggled;
        }

        // Holds state originating from a single KeyDownEvent.
        private class KeyUpInputItem : InputItem
        {
            // Ctor.
            internal KeyUpInputItem(TextEditor textEditor, Key key, ModifierKeys modifiers)
                : base(textEditor)
            {
                _key = key;
                _modifiers = modifiers;
            }

            // Fires the command associated with a keystroke.
            internal override void Do()
            {
                if (TextEditor.UiScope == null)
                {
                    // We dont want to process the input item if the editor has already been detached from its UiScope.
                    return;
                }

                // Delegate the work to specific handlers.
                switch (_key)
                {
                    case Key.RightShift:
                        // Only support RTL flow direction in case of having the installed
                        // bidi input language.
                        if (TextSelection.IsBidiInputLanguageInstalled())
                        {
                            TextEditorTyping.OnFlowDirectionCommand(TextEditor, _key);
                        }
                        break;
                    case Key.LeftShift:
                        TextEditorTyping.OnFlowDirectionCommand(TextEditor, _key);
                        break;

                    default:
                        Invariant.Assert(false, "Unexpected key value!");
                        break;
                }
            }

            // Key associated with the original event.
            private readonly Key _key;

            // Modifier state when the original event fired.
            private readonly ModifierKeys _modifiers;
        }

        // ----------------------------------------------------------
        //
        // Merge Typing Undo Units
        //
        // ----------------------------------------------------------

        #region Merge Typing Undo Units

        /// <summary>
        /// The helper for typing undo unit merging.
        /// Supposed to be called in the beginning of typing block -
        /// before making any changes.
        /// Assumes that CloseTypingUndoUnit method will be called
        /// after the change is completed.
        /// </summary>
        private static void OpenTypingUndoUnit(TextEditor This)
        {
            UndoManager undoManager = This._GetUndoManager();

            if (undoManager != null && undoManager.IsEnabled)
            {
                if (This._typingUndoUnit != null && undoManager.LastUnit == This._typingUndoUnit && !This._typingUndoUnit.Locked)
                {
                    undoManager.Reopen(This._typingUndoUnit);
                }
                else
                {
                    This._typingUndoUnit = new TextParentUndoUnit(This.Selection);
                    undoManager.Open(This._typingUndoUnit);
                }
            }
        }

        /// <summary>
        /// The helper for typing undo unit megring.
        /// Supposed to be called at the end of typing block -
        /// after all changes are done.
        /// Assumes that OpenTypingUndoUnit method was called
        /// in the beginning of this sequence.
        /// </summary>
        private static void CloseTypingUndoUnit(TextEditor This, UndoCloseAction closeAction)
        {
            UndoManager undoManager = This._GetUndoManager();

            if (undoManager != null && undoManager.IsEnabled)
            {
                if (This._typingUndoUnit != null && undoManager.LastUnit == This._typingUndoUnit && !This._typingUndoUnit.Locked)
                {
                    if (This._typingUndoUnit is TextParentUndoUnit)
                    {
                        ((TextParentUndoUnit)This._typingUndoUnit).RecordRedoSelectionState();
                    }
                    undoManager.Close(This._typingUndoUnit, closeAction);
                }
            }
            else
            {
                This._typingUndoUnit = null;
            }
        }

        /// <summary>
        /// StartInputCorrection command QueryStatus handler
        /// </summary>
        private static void OnQueryStatusNYI(object target, CanExecuteRoutedEventArgs args)
        {
            TextEditor This = TextEditor._GetTextEditor(target);

            if (This == null)
            {
                return;
            }

            args.CanExecute = true;
        }

        #endregion Merge Typing Undo Units

        // MouseMoveEvent listener.
        private static void OnMouseMove(object sender, MouseEventArgs e)
        {
            // Un-vanish the cursor on any mouse move.
            _ShowCursor();
        }

        // MouseMoveEvent listener.
        // We only need this event because of the edge case where
        // moving the mouse from the outermost pixel of the UiScope to
        // another UIElement's real estate doesn't raise a MouseMoveEvent.
        private static void OnMouseLeave(object sender, MouseEventArgs e)
        {
            // Un-vanish the cursor on any mouse leave.
            _ShowCursor();
        }

        // Hides the mouse cursor when the user starts typing.
        private static void HideCursor(TextEditor This)
        {
            if (!TextEditor._ThreadLocalStore.HideCursor &&
                SystemParameters.MouseVanish &&
                This.UiScope.IsMouseOver)
            {
                TextEditor._ThreadLocalStore.HideCursor = true;
                SafeNativeMethods.ShowCursor(false);
            }
        }

        // When the mouse cursor is over a Hyperlink, force a cursor update
        // to display the "hand" cursor appropriately.
        private static void UpdateHyperlinkCursor(TextEditor This)
        {
            if (This.UiScope is RichTextBox && This.TextView != null && This.TextView.IsValid)
            {
                TextPointer pointer = (TextPointer)This.TextView.GetTextPositionFromPoint(Mouse.GetPosition(This.TextView.RenderScope), false);

                if (pointer != null &&
                    pointer.Parent is TextElement &&
                    TextSchema.HasHyperlinkAncestor((TextElement)pointer.Parent))
                {
                    Mouse.UpdateCursor();
                }
            }
        }

        #endregion Private Methods

        private const string KeyBackspace = "Backspace";
        private const string KeyDelete = "Delete";
        private const string KeyDeleteNextWord = "Ctrl+Delete";
        private const string KeyDeletePreviousWord = "Ctrl+Backspace";
        private const string KeyEnterLineBreak = "Shift+Enter";
        private const string KeyEnterParagraphBreak = "Enter";
        private const string KeyShiftBackspace = "Shift+Backspace";
        private const string KeyShiftSpace = "Shift+Space";
        private const string KeySpace = "Space";
        private const string KeyTabBackward = "Shift+Tab";
        private const string KeyTabForward = "Tab";
        private const string KeyToggleInsert = "Insert";
    }
}
