#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""T1c 第 4 批 · Q5…Q9：`TextContainer.Changed` → `TextBox.Text` DP 的推值链（只读插桩）。

用法
----
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationframework-textbox-textdp-trace.py --check   # 只读
    python3 src/WpfGfx.Linux.native/tools/…                                                              # （见上）
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationframework-textbox-textdp-trace.py           # 生成 + 接线（幂等）
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationframework-textbox-textdp-trace.py --prove   # 「只插入」证明（只读）

**命题在第 3 批后被改写**（务必先读这段）
----------------------------------------
第 3 批实跑（`pf=f3bc1aad879422a7`）：

    Q0i **立即执行支路**（AcceptsRichContent=False）⇒ 不走 Background 投递
    Q4d SetSelectedText 返回后 **len=1 text="A"**      ← **容器真的被改了**
    Q4f 出口 UndoCloseAction=Commit len=1 text="A"
    Q3c TextInputItem.Do() 正常返回 len=2 text="AB"
    （无 Q4e 无异常、无 Q3b、过滤后长度≠0）

并且 T3 用**注入后帧**看到 TextBox 里就是 `AB` 加光标。⇒ **命题改写**：

    键入**到了**、**容器改了**、**屏幕也画出来了**；
    **本批只问**：`TextContainer.Changed` 有没有 raise、有没有推到 `TextBox` 的 `Text` DP。
    ⚠️「`Text` DP（与 `TextChanged`）是否陈旧」**本批不判**：第 3 批那批 `changes=0`/`'seed-文本'`
    经复核全是**注入之前**的读数 ⇒ 当时**无信息**（不是"陈旧"，也不是"回归"）；
    该问题只能由**写后读数**判（见 T1c 报告 §34/§35）。
    **不是**"打字到不了 TextBox"。

这条链（上游原文）：
    TextBoxBase.cs:1435   `_textContainer.Changed += new TextContainerChangedEventHandler(OnTextContainerChanged);`  ← 订阅
    TextContainer.cs:375  `ChangedHandler(this, changes);`                        ← **Changed 真正 raise 的那一点**
    TextBoxBase.cs:1348   `OnTextContainerChanged`（→ `:1395 OnTextChanged(...)` → `RaiseEvent(TextChangedEvent)`）
    TextBox.cs:1194       `override OnTextContainerChanged`：`:1206` 守卫 → `:1214` `DeferredTextReference` → `:1216 SetCurrentDeferredValue(TextProperty, dtr)`
    DeferredTextReference.cs:41 `GetValue` → `TextRangeBase.GetTextInternal(...)`  ← **DP 读 `.Text` 时解析出的字符串**

本批插桩（**只插入**；4 个生成物，tracer 类放在 `TextContainer.Linux.cs` 里，其余三个文件**全限定**引用）：
    Q9a/Q9b `TextContainer.BeginChange(bool)` / `EndChange(bool)`：**变更块嵌套计数**的配对（不归零 = Changed 被永久推迟）
    Q5      `EndChange` 入口计数 + **raise 之前**（`ChangedHandler==null?` / `skipEvents` / changes 条数）+ **raise 之后**
    Q6      `TextBoxBase.OnTextContainerChanged`：入口（**在早退门之前**）/ 早退门命中 / 走到 `OnTextChanged` / 出口
    Q7      `TextBox.OnTextContainerChanged`：入口（`_isInsideTextContentChange` / `_changeEventNestingCount` / `_newTextValue` 类型）
            + **是否真的走到 `:1216 SetCurrentDeferredValue`** + finally 出口（`resetText` 等）
    Q8      `DeferredTextReference.GetValue`：入口（容器 + `Parent` 类型）+ **取到的字符串**

⚠️ **本批探针不读任何 DP**（不碰 `this.Text` / `GetValue(TextProperty)`）：读 DP 本身会触发 deferred 解析，
会**改变被测对象**（观测者效应）。Q8 是 DP 自己来读时才执行的那一格 ⇒ 安全。
⚠️ 开关与 PF 其它批**同一套**：`WPF_LINUX_INPUT_TRACE`（严格）或 `WPF_LINUX_MSGFLOW_TRACE`（与原生同义）；
前缀同样是 `[INPUT_TRACE]`，行内标 Q5/Q6/Q7/Q8/Q9 ⇒ 一条 grep 抓全链路。缺省关 / 有界 / 只打印。
"""

import argparse
import hashlib
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))

PF_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "PresentationFramework")
CSPROJ = os.path.join(PF_DIR, "PresentationFramework.Linux.csproj")

UP_PREFIX = "src/Microsoft.DotNet.Wpf/src/PresentationFramework/"

# --------------------------------------------------------------------------------------
#  插桩类本体（放进 TextContainer.Linux.cs 的同一 namespace；另三个文件全限定引用）
# --------------------------------------------------------------------------------------
TRACE_CLASS = '''// =====================================================================================
//  T1c 第 4 批（Q5…Q9）：`TextContainer.Changed` → `TextBox.Text` DP 的推值链
//
//  命题（第 3 批实跑后改写）：**键入到了、容器改了、屏幕也画出来了**；
//  **本批只问一件事**：`TextContainer.Changed` 到底 raise 了没有、有没有推到 `TextBox` 的 `Text` DP。
//  ⚠️「DP/`TextChanged` 是否**陈旧**」**本批不判**：那批 `changes=0`/`'seed-文本'` 经复核是**注入之前**的读数
//     （无信息）；须由**写后读数**判（T1c 报告 §34/§35）。
//
//  开关与 PF 其它批同一套：WPF_LINUX_INPUT_TRACE（严格）或 WPF_LINUX_MSGFLOW_TRACE（与原生同义）。
//  缺省关 / 有界（≤200 行）/ 只打印。前缀 [INPUT_TRACE]（一条 grep 抓全链路）。
//  ⚠️ 本类里**不读任何 DependencyProperty**：读 DP 会触发 deferred 解析 ⇒ 改变被测对象。
// =====================================================================================
internal static class WpfLinuxPfTextDpTrace
{
    private const string EnvInputTrace = "WPF_LINUX_INPUT_TRACE";
    private const string EnvMsgFlow = "WPF_LINUX_MSGFLOW_TRACE";
    private const int MaxLines = 200;

    private static readonly bool s_enabled;
    private static readonly string s_via;
    private static int s_lines;
    private static int s_budgetNotice;   // L12：触顶只报一次

    static WpfLinuxPfTextDpTrace()
    {
        string a = null, b = null;
        try { a = System.Environment.GetEnvironmentVariable(EnvInputTrace); } catch (System.Exception) { }
        try { b = System.Environment.GetEnvironmentVariable(EnvMsgFlow); } catch (System.Exception) { }
        s_enabled = IsOn(a) || IsOnNativeSwitch(b);
        s_via = IsOn(a) ? EnvInputTrace : (IsOnNativeSwitch(b) ? EnvMsgFlow : "(都未设)");
        if (s_enabled)
        {
            Emit("via=" + s_via + " pid=" + System.Environment.ProcessId
                 + " —— PF 侧 Q5…Q9 读数（TextContainer.Changed → TextBox.Text DP）");
        }
    }

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
                    System.Console.Error.WriteLine("[INPUT_TRACE] **预算用尽**（PF 第4批 MaxLines=" + MaxLines
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

    /// <summary>容器读数：文本长度 + 前 32 字符（**只读容器，不读 DP**）。</summary>
    private static string DocOf(object container)
    {
        ITextContainer c = container as ITextContainer;
        if (c == null) return "(非 ITextContainer)";
        try
        {
            string t = new TextRange(c.Start, c.End).Text;
            if (t == null) return "(Text=null)";
            string head = (t.Length > 32) ? (t.Substring(0, 32) + "…") : t;
            return "容器 len=" + t.Length + " text=\\"" + head + "\\"";
        }
        catch (System.Exception ex) { return "(读容器抛 " + ex.GetType().Name + ")"; }
    }

    private static string ArgsOf(object e)
    {
        TextContainerChangedEventArgs a = e as TextContainerChangedEventArgs;
        if (a == null) return "(非 TextContainerChangedEventArgs)";
        try
        {
            return "changes=" + a.Changes.Count
                 + " HasContentAddedOrRemoved=" + a.HasContentAddedOrRemoved
                 + " HasLocalPropertyValueChange=" + a.HasLocalPropertyValueChange;
        }
        catch (System.Exception ex) { return "(读 args 抛 " + ex.GetType().Name + ")"; }
    }

    // ── Q9：变更块嵌套计数（配对；不归零 ⇒ Changed 被永久推迟）──────────────────────
    /// <summary>Q9a `TextContainer.BeginChange(bool)` 计数自增之后。</summary>
    internal static void Q9Begin(object container, int level, bool undo)
    {
        if (!s_enabled) return;
        Emit("Q9a BeginChange(undo=" + undo + ") ⇒ _changeBlockLevel=" + level + " " + TypeOf(container));
    }

    /// <summary>Q9b `TextContainer.EndChange(bool)` 计数自减之后。</summary>
    internal static void Q9End(object container, int level, bool skipEvents)
    {
        if (!s_enabled) return;
        Emit("Q9b EndChange(skipEvents=" + skipEvents + ") ⇒ _changeBlockLevel=" + level
             + ((level == 0) ? "（**归零 ⇒ 该 raise Changed**）" : "（**未归零 ⇒ 本次不 raise Changed**）"));
    }

    // ── Q5：Changed 到底 raise 了没有 ──────────────────────────────────────────────
    /// <summary>Q5a `EndChange` 入口（计数还没减）。</summary>
    internal static void Q5Entry(object container, int level, bool skipEvents)
    {
        if (!s_enabled) return;
        Emit("Q5a EndChange 入口 _changeBlockLevel=" + level + " skipEvents=" + skipEvents + " " + TypeOf(container));
    }

    /// <summary>Q5b **raise 之前**：为什么 raise / 为什么不 raise（三件事一次打全）。</summary>
    internal static void Q5BeforeRaise(object container, object changes, object handler, bool skipEvents)
    {
        if (!s_enabled) return;
        Emit("Q5b 即将 raise Changed：ChangedHandler=" + ((handler == null) ? "**null（没人订阅 ⇒ 不会 raise）**" : "有")
             + " skipEvents=" + skipEvents + " " + ArgsOf(changes) + " " + TypeOf(container));
    }

    /// <summary>Q5c **raise 之后**（`ChangedHandler(this, changes)` 调过了 ⇒ 订阅者已被调）。</summary>
    internal static void Q5Raised(object container, object changes)
    {
        if (!s_enabled) return;
        Emit("Q5c **Changed 已 raise**（订阅者被调）" + " " + ArgsOf(changes) + " " + DocOf(container));
    }

    // ── Q6：TextBoxBase 这一层 ────────────────────────────────────────────────────
    /// <summary>Q6a `TextBoxBase.OnTextContainerChanged` 入口（**在早退门之前**）。</summary>
    internal static void Q6Entry(object boxBase, object e, object container)
    {
        if (!s_enabled) return;
        Emit("Q6a TextBoxBase.OnTextContainerChanged 入口 " + TypeOf(boxBase) + " " + ArgsOf(e)
             + "（进入时 " + DocOf(container) + "）");
    }

    /// <summary>Q6 早退门命中：`!HasContentAddedOrRemoved &amp;&amp; !HasLocalPropertyValueChange`。</summary>
    internal static void Q6BailNoContent(object boxBase, object e)
    {
        if (!s_enabled) return;
        Emit("Q6 **早退门命中**（非内容增删且非局部属性变化）⇒ 不会放 TextChanged " + ArgsOf(e));
    }

    /// <summary>Q6b 走到了 `OnTextChanged(...)` 那一行（再往下就是 `RaiseEvent(TextChangedEvent)`）。</summary>
    internal static void Q6BeforeOnTextChanged(object boxBase, object e, object undoAction)
    {
        if (!s_enabled) return;
        // **实参惰性**：调用点只传 `undoAction` 枚举本身，ToString 在这里做
        string ua = "";
        try { ua = (undoAction == null) ? "(null)" : undoAction.ToString(); } catch (System.Exception) { ua = "(读不出来)"; }
        Emit("Q6b 即将 OnTextChanged(...) → RaiseEvent(TextChangedEvent) undoAction=" + ua
             + " " + ArgsOf(e));
    }

    /// <summary>Q6c `OnTextContainerChanged` 出口。</summary>
    internal static void Q6Exit(object boxBase, object e)
    {
        if (!s_enabled) return;
        Emit("Q6c TextBoxBase.OnTextContainerChanged 出口 " + ArgsOf(e));
    }

    // ── Q7：TextBox 这一层（Text DP 的推值）──────────────────────────────────────
    /// <summary>
    /// Q7a `TextBox.OnTextContainerChanged` 入口（守卫 / 嵌套计数 / `_newTextValue` 类型）。
    /// **实参惰性**：容器在这里从 `textBox` 取（调用点不写 `this.TextContainer` 这类属性访问）。
    /// </summary>
    private static object ContainerOf(object textBox)
    {
        try
        {
            // ⚠️ 本 tracer 类在 `System.Windows.Documents` 里，`TextBox` 在 `System.Windows.Controls`
            //    ⇒ **必须全限定**（第一次写漏了，PF 编译 CS0246：未能找到类型"TextBox" —— 被自己的编译闸门抓住）
            //    走 `TextEditor._GetTextEditor(tb)`（同 namespace 的 internal 静态方法）拿容器的 `TextContainer`。
            System.Windows.Controls.TextBox tb = textBox as System.Windows.Controls.TextBox;
            if (tb == null) return null;
            TextEditor te = TextEditor._GetTextEditor(tb);
            if (te != null) return te.TextContainer;
        }
        catch (System.Exception) { }
        return null;
    }

    internal static void Q7Entry(object textBox, object e, bool insideTextContentChange,
                                 int changeEventNestingCount, object newTextValue)
    {
        if (!s_enabled) return;
        Emit("Q7a TextBox.OnTextContainerChanged 入口 " + TypeOf(textBox)
             + " _isInsideTextContentChange=" + insideTextContentChange
             + " _changeEventNestingCount=" + changeEventNestingCount
             + " _newTextValue=" + TypeOf(newTextValue) + " " + ArgsOf(e)
             + "（进入时 " + DocOf(ContainerOf(textBox)) + "）");
    }

    /// <summary>Q7b **真的走到了 `SetCurrentDeferredValue(TextProperty, dtr)`**（DP 被推成 deferred）。</summary>
    internal static void Q7SetDeferred(object textBox, object dtr)
    {
        if (!s_enabled) return;
        Emit("Q7b **已 SetCurrentDeferredValue(TextProperty, " + TypeOf(dtr) + ")** "
             + TypeOf(textBox) + "（此时 " + DocOf(ContainerOf(textBox)) + "）");
    }

    /// <summary>Q7c finally 出口（`resetText` / 嵌套计数 / `_newTextValue` 收尾）。</summary>
    internal static void Q7Exit(object textBox, bool resetText,
                                int changeEventNestingCount, object newTextValue)
    {
        if (!s_enabled) return;
        Emit("Q7c TextBox.OnTextContainerChanged 出口 resetText=" + resetText
             + " _changeEventNestingCount=" + changeEventNestingCount
             + " _newTextValue=" + TypeOf(newTextValue) + " " + DocOf(ContainerOf(textBox)));
    }

    // ── Q8：DP 真来读 `.Text` 时的解析结果 ───────────────────────────────────────
    /// <summary>Q8a `DeferredTextReference.GetValue` 入口（容器 + `Parent` 类型 ⇒ `tb?.…` 会不会是 null）。</summary>
    internal static void Q8Entry(object dtr, object container)
    {
        if (!s_enabled) return;
        string parent = "(?)";
        ITextContainer c = container as ITextContainer;
        try { if (c != null) parent = TypeOf(c.Parent); } catch (System.Exception) { parent = "(读 Parent 抛)"; }
        Emit("Q8a DeferredTextReference.GetValue **DP 来读了** " + TypeOf(dtr)
             + " 容器=" + TypeOf(container) + " Parent=" + parent
             + "（Parent 为 null ⇒ `tb?.OnDeferredTextReferenceResolved` 不会调）");
    }

    /// <summary>Q8b **取到的字符串**（这就是 DP 解析出的 `.Text` 新值）。</summary>
    internal static void Q8Value(object container, string s)
    {
        if (!s_enabled) return;
        Emit("Q8b GetTextInternal 取到 " + ((s == null) ? "(null)" : ("len=" + s.Length + " text=\\"" + s + "\\""))
             + " " + DocOf(container));
    }
}

'''

# --------------------------------------------------------------------------------------
#  四个目标文件：锚点 + 替换（每处锚点要求上游**恰好命中 1 次**）
# --------------------------------------------------------------------------------------
TC_REL = UP_PREFIX + "System/Windows/Documents/TextContainer.cs"
TBB_REL = UP_PREFIX + "System/Windows/Controls/Primitives/TextBoxBase.cs"
TB_REL = UP_PREFIX + "System/Windows/Controls/TextBox.cs"
DTR_REL = UP_PREFIX + "System/Windows/Controls/DeferredTextReference.cs"

TC_CLASS_ANCHOR = '    internal class TextContainer : ITextContainer\n'

TC_BEGIN_ANCHOR = '''            _changeBlockLevel++;

            // We'll raise the Changing event when/if we get an actual
            // change added, inside BeforeAddChange.
'''

TC_END_ANCHOR = '''            Invariant.Assert(_changeBlockLevel > 0, "Unmatched EndChange call!");

            _changeBlockLevel--;

            if (_changeBlockLevel == 0)
'''

TC_PRERAISE_ANCHOR = '''                        changes = _changes;
                        _changes = null;

                        if (this.ChangedHandler != null && !skipEvents)
'''

TC_RAISE_ANCHOR = '                            ChangedHandler(this, changes);\n'

TBB_ENTRY_ANCHOR = '''        internal virtual void OnTextContainerChanged(object sender, TextContainerChangedEventArgs e)
        {
            // If only properties on the text changed, don't fire a content change event.
'''

TBB_BAIL_ANCHOR = '''            if (!e.HasContentAddedOrRemoved && !e.HasLocalPropertyValueChange)
            {
                return;
            }
'''

TBB_ONTEXTCHANGED_ANCHOR = '''            _pendingUndoAction = undoAction;
            try
            {
                OnTextChanged(new TextChangedEventArgs(TextChangedEvent, undoAction, new ReadOnlyCollection<TextChange>(e.Changes.Values)));
'''

TBB_EXIT_ANCHOR = '''            finally
            {
                _pendingUndoAction = UndoAction.None;
            }
'''

TB_ENTRY_ANCHOR = '''        internal override void OnTextContainerChanged(object sender, TextContainerChangedEventArgs e)
        {
            bool resetText = false;
            string newTextValue = null;
'''

TB_SETDEFERRED_ANCHOR = '''                    DeferredTextReference dtr = new DeferredTextReference(this.TextContainer);
                    _newTextValue = dtr;
                    SetCurrentDeferredValue(TextProperty, dtr);
'''

TB_FINALLY_ANCHOR = '''                    _isInsideTextContentChange = false;
                    _newTextValue = DependencyProperty.UnsetValue;
                }
            }

            if (resetText)
'''

DTR_GETVALUE_ANCHOR = '''        internal override object GetValue(BaseValueSourceInternal valueSource)
        {
            string s = TextRangeBase.GetTextInternal(_textContainer.Start, _textContainer.End);
'''

FQ = "System.Windows.Documents.WpfLinuxPfTextDpTrace"

TC_EDITS = [
    ("TC0 插桩类本体", TC_CLASS_ANCHOR, TRACE_CLASS + TC_CLASS_ANCHOR),
    ("Q9a BeginChange 计数自增后", TC_BEGIN_ANCHOR, TC_BEGIN_ANCHOR + '''            // ── T1c 第 4 批 Q9a（只读插桩）：变更块**嵌套计数**（与 Q9b 配对）──
            WpfLinuxPfTextDpTrace.Q9Begin(this, _changeBlockLevel, undo);

'''),
    ("Q5a 入口 + Q9b 计数自减后", TC_END_ANCHOR, '''            Invariant.Assert(_changeBlockLevel > 0, "Unmatched EndChange call!");

            // ── T1c Q5a（只读插桩）：EndChange 入口（计数还没减）──
            WpfLinuxPfTextDpTrace.Q5Entry(this, _changeBlockLevel, skipEvents);

            _changeBlockLevel--;

            // ── T1c Q9b：计数自减后（**不归零 ⇒ 这次不会 raise Changed**）──
            WpfLinuxPfTextDpTrace.Q9End(this, _changeBlockLevel, skipEvents);

            if (_changeBlockLevel == 0)
'''),
    ("Q5b raise 之前", TC_PRERAISE_ANCHOR, '''                        changes = _changes;
                        _changes = null;

                        // ── T1c Q5b（只读插桩）：为什么 raise / 为什么不 raise ──
                        WpfLinuxPfTextDpTrace.Q5BeforeRaise(this, changes, this.ChangedHandler, skipEvents);

                        if (this.ChangedHandler != null && !skipEvents)
'''),
    ("Q5c raise 之后", TC_RAISE_ANCHOR, '''                            WpfLinuxPfTextDpTrace.Q5Raised(this, changes);
                            ChangedHandler(this, changes);
'''),
]

TBB_EDITS = [
    ("Q6a 入口（早退门之前）", TBB_ENTRY_ANCHOR, '''        internal virtual void OnTextContainerChanged(object sender, TextContainerChangedEventArgs e)
        {
            // ── T1c 第 4 批 Q6a（只读插桩）：入口（**在早退门之前** ⇒ 早退也有读数）──
            %s.Q6Entry(this, e, this._textContainer);

            // If only properties on the text changed, don't fire a content change event.
''' % FQ),
    ("Q6 早退门", TBB_BAIL_ANCHOR, '''            if (!e.HasContentAddedOrRemoved && !e.HasLocalPropertyValueChange)
            {
                // ── T1c Q6：早退门命中（不会放 TextChanged）──
                %s.Q6BailNoContent(this, e);
                return;
            }
''' % FQ),
    ("Q6b 走到 OnTextChanged 之前", TBB_ONTEXTCHANGED_ANCHOR, '''            _pendingUndoAction = undoAction;
            try
            {
                // ── T1c Q6b：走到了 RaiseEvent(TextChangedEvent) 之前 ──
                %s.Q6BeforeOnTextChanged(this, e, undoAction);
                OnTextChanged(new TextChangedEventArgs(TextChangedEvent, undoAction, new ReadOnlyCollection<TextChange>(e.Changes.Values)));
''' % FQ),
    ("Q6c 出口", TBB_EXIT_ANCHOR, '''            finally
            {
                _pendingUndoAction = UndoAction.None;
            }

            // ── T1c Q6c：出口 ──
            %s.Q6Exit(this, e);
''' % FQ),
]

TB_EDITS = [
    ("Q7a 入口", TB_ENTRY_ANCHOR, '''        internal override void OnTextContainerChanged(object sender, TextContainerChangedEventArgs e)
        {
            // ── T1c 第 4 批 Q7a（只读插桩）：**不读任何 DP**（读 DP 会触发 deferred 解析、改变被测对象）──
            %s.Q7Entry(this, e, _isInsideTextContentChange, _changeEventNestingCount, _newTextValue);

            bool resetText = false;
            string newTextValue = null;
''' % FQ),
    ("Q7b 到达 SetCurrentDeferredValue", TB_SETDEFERRED_ANCHOR, '''                    DeferredTextReference dtr = new DeferredTextReference(this.TextContainer);
                    _newTextValue = dtr;
                    SetCurrentDeferredValue(TextProperty, dtr);
                    // ── T1c Q7b：**DP 已被推成 deferred** ──
                    %s.Q7SetDeferred(this, dtr);
''' % FQ),
    ("Q7c finally 出口", TB_FINALLY_ANCHOR, '''                    _isInsideTextContentChange = false;
                    _newTextValue = DependencyProperty.UnsetValue;
                }
            }

            // ── T1c Q7c：出口（resetText / 嵌套计数 / _newTextValue 收尾）──
            %s.Q7Exit(this, resetText, _changeEventNestingCount, _newTextValue);

            if (resetText)
''' % FQ),
]

DTR_EDITS = [
    ("Q8a 入口 + Q8b 取到的字符串", DTR_GETVALUE_ANCHOR, '''        internal override object GetValue(BaseValueSourceInternal valueSource)
        {
            // ── T1c 第 4 批 Q8a（只读插桩）：**DP 来读了**（Parent 为 null ⇒ 不会回调 TextBox）──
            %s.Q8Entry(this, _textContainer);

            string s = TextRangeBase.GetTextInternal(_textContainer.Start, _textContainer.End);

            // ── T1c Q8b：**取到的字符串**（这就是 DP 解析出的 .Text 新值）──
            %s.Q8Value(_textContainer, s);
''' % (FQ, FQ)),
]

TARGETS = [
    (TC_REL, "TextContainer.Linux.cs", TC_EDITS),
    (TBB_REL, "TextBoxBase.Linux.cs", TBB_EDITS),
    (TB_REL, "TextBox.Linux.cs", TB_EDITS),
    (DTR_REL, "DeferredTextReference.Linux.cs", DTR_EDITS),
]

REQUIRED = {
    "TextContainer.Linux.cs": [
        ("插桩类存在", "internal static class WpfLinuxPfTextDpTrace"),
        ("开关名①", 'private const string EnvInputTrace = "WPF_LINUX_INPUT_TRACE";'),
        ("开关名②", 'private const string EnvMsgFlow = "WPF_LINUX_MSGFLOW_TRACE";'),
        ("与原生同名开关同义", 'return v != "0";'),
        ("有界 200 行", "private const int MaxLines = 200;"),
        ("不读 DP（只读容器）", "只读容器，不读 DP"),
        ("Q9a BeginChange 计数", "WpfLinuxPfTextDpTrace.Q9Begin(this, _changeBlockLevel, undo);"),
        ("Q5a EndChange 入口计数", "WpfLinuxPfTextDpTrace.Q5Entry(this, _changeBlockLevel, skipEvents);"),
        ("Q9b 计数自减后", "WpfLinuxPfTextDpTrace.Q9End(this, _changeBlockLevel, skipEvents);"),
        ("Q5b raise 之前（ChangedHandler 是否为 null）",
         "WpfLinuxPfTextDpTrace.Q5BeforeRaise(this, changes, this.ChangedHandler, skipEvents);"),
        ("Q5c raise 之后", "WpfLinuxPfTextDpTrace.Q5Raised(this, changes);"),
        ("上游 raise 那条语句逐字未动", "ChangedHandler(this, changes);"),
    ],
    "TextBoxBase.Linux.cs": [
        ("Q6a 入口（早退门之前）", FQ + ".Q6Entry(this, e, this._textContainer);"),
        ("Q6 早退门读数", FQ + ".Q6BailNoContent(this, e);"),
        ("Q6b 走到 OnTextChanged 之前（**实参惰性**：传枚举本身）", FQ + ".Q6BeforeOnTextChanged(this, e, undoAction);"),
        ("Q6c 出口", FQ + ".Q6Exit(this, e);"),
        ("上游订阅语句逐字未动",
         "_textContainer.Changed += new TextContainerChangedEventHandler(OnTextContainerChanged);"),
    ],
    "TextBox.Linux.cs": [
        ("Q7a 入口（**实参惰性**：容器在 helper 内取）", FQ + ".Q7Entry(this, e, _isInsideTextContentChange, _changeEventNestingCount, _newTextValue);"),
        ("Q7b 到达 SetCurrentDeferredValue（实参惰性）", FQ + ".Q7SetDeferred(this, dtr);"),
        ("Q7c 出口（实参惰性）", FQ + ".Q7Exit(this, resetText, _changeEventNestingCount, _newTextValue);"),
        ("上游 SetCurrentDeferredValue 那条语句逐字未动", "SetCurrentDeferredValue(TextProperty, dtr);"),
    ],
    "DeferredTextReference.Linux.cs": [
        ("Q8a 入口", FQ + ".Q8Entry(this, _textContainer);"),
        ("Q8b 取到的字符串", FQ + ".Q8Value(_textContainer, s);"),
        ("上游 GetTextInternal 那条语句逐字未动",
         "string s = TextRangeBase.GetTextInternal(_textContainer.Start, _textContainer.End);"),
    ],
}

HEADER = """// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-presentationframework-textbox-textdp-trace.py **生成**，不要手改。
//
// 内容 = 上游 `upstream/wpf/{rel}` 逐字复制 + {n} 处 T1c **只读插桩**（第 4 批 Q5…Q9）。
// 每次运行该脚本都会从上游重读重生成；锚点找不到 / 命中数不符时**报错退出**，不会静默产出未打补丁的副本。
//
// 为什么打：第 3 批实跑把命题改写成 —— **键入到了、容器改了（len=1 "A"→"AB"）、屏幕也画出来了**，
//   **本批只问**：`TextContainer.Changed` 到底 raise 了没有、有没有推到 `TextBox` 的 `Text` DP。
//   ⚠️「`Text` DP 是否**陈旧**」**本批不判**：那批 `changes=0`/`'seed-文本'` 经复核是**注入之前**的读数
//      （无信息）；须由**写后读数**判（T1c 报告 §34/§35）。
//   Q9 变更块计数配对 → Q5 Changed raise → Q6 TextBoxBase 转发 → Q7 TextBox 推 deferred → Q8 DP 解析出的字符串。
//   ⚠️ 探针**不读任何 DP**（读 DP 会触发 deferred 解析 ⇒ 观测者效应）；只读容器文本。
//   开关与 PF 其它批同一套（WPF_LINUX_INPUT_TRACE / WPF_LINUX_MSGFLOW_TRACE），缺省关、有界、只打印。
"""

MARKER_BEGIN = ("  <!-- ==== T1c 第 4 批：TextContainer.Changed → TextBox.Text DP"
                "（patch-presentationframework-textbox-textdp-trace.py 注入）==== -->")
MARKER_END = "  <!-- ==== T1c 第 4 批 结束 ==== -->"


def _count(h, n):
    return h.count(n)


def _sha(t):
    return hashlib.sha256(t.encode("utf-8")).hexdigest()


def _build_one(rel, gen_name, edits):
    up = os.path.join(ROOT, rel)
    if not os.path.exists(up):
        print(f"[失败] 找不到上游 {up}")
        return None, 1
    with open(up, encoding="utf-8-sig") as f:
        text = f.read()

    bad = False
    for name, anchor, _ in edits:
        n = _count(text, anchor)
        print(f"[锚点] {gen_name} {name}：上游出现 {n} 次（要求 1）")
        if n != 1:
            bad = True
    if bad:
        print(f"[失败] {gen_name} 锚点缺失或重复 —— 上游这段改过了？**不做任何静默降级**。")
        return None, 1

    out = text
    for _, anchor, repl in edits:
        out = out.replace(anchor, repl, 1)

    up_throws, out_throws = _count(text, "throw "), _count(out, "throw ")
    if up_throws != out_throws:
        print(f"[失败] {gen_name}：`throw` 条数变了 {up_throws} → {out_throws}")
        return None, 1
    if (_count(text, "{") - _count(text, "}")) != (_count(out, "{") - _count(out, "}")):
        print(f"[失败] {gen_name}：大括号盈亏变了（只读插桩不该改结构）")
        return None, 1

    full = HEADER.replace("{rel}", rel).replace("{n}", str(len(edits))) + out
    for name, needle in REQUIRED.get(gen_name, []):
        if needle not in full:
            print(f"[失败] {gen_name} 缺少结构断言：{name}")
            return None, 1
    print(f"[断言] {gen_name}：锚点 {len(edits)} 处各 1 次；`throw` {up_throws}=={out_throws}；"
          f"大括号 {_count(out, '{')}/{_count(out, '}')} 盈亏一致；结构断言 "
          f"{len(REQUIRED.get(gen_name, []))}/{len(REQUIRED.get(gen_name, []))} 全中；"
          f"行数 {len(text.splitlines())} → {len(out.splitlines())}（+{len(out.splitlines()) - len(text.splitlines())}）")
    return full, 0


def _build_all():
    built = []
    for rel, gen_name, edits in TARGETS:
        full, rc = _build_one(rel, gen_name, edits)
        if rc:
            return None, 1
        built.append((gen_name, full, edits, rel))
    return built, 0


def prove():
    """「只插入」机械证明：各文件逆序回代后与上游 sha256 **逐字节相同**。"""
    rc_all = 0
    for rel, gen_name, edits in TARGETS:
        up = os.path.join(ROOT, rel)
        gen = os.path.join(PF_DIR, gen_name)
        with open(up, encoding="utf-8-sig") as f:
            text = f.read()
        head = HEADER.replace("{rel}", rel).replace("{n}", str(len(edits)))

        body, where = None, None
        if os.path.exists(gen):
            with open(gen, encoding="utf-8") as f:
                raw = f.read()
            if raw.startswith(head):
                body = raw[len(head):]
                where = f"落盘生成物 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/{gen_name}"
            else:
                print(f"[注意] {gen_name} 的文件头与当前脚本不一致（上一版产的、还没重放）"
                      "⇒ 该文件退化为**内存**证明，不对落盘件背书。")
        if body is None:
            full, rc = _build_one(rel, gen_name, edits)
            if rc:
                rc_all = 1
                continue
            body = full[len(head):]
            where = f"**内存**（{gen_name} 尚未产出/已过时）"

        cur = body
        ok = True
        for name, anchor, repl in reversed(edits):
            n = _count(cur, repl)
            if n != 1:
                print(f"[失败] {gen_name} 逆向回代 {name}：`repl` 命中 {n} 次（要求 1）")
                ok = False
                break
            cur = cur.replace(repl, anchor, 1)
        if not ok or cur != text:
            print(f"[失败] {gen_name} 逆代后与上游**不一致** ⇒ 别信这份生成物。")
            rc_all = 1
            continue
        print(f"[① 只插入] {gen_name}：取自 {where}")
        print(f"[① 只插入] 逆序回代后与上游**逐字节相同** ✓ sha256={_sha(cur)}（== 上游 {_sha(text)}）")
    return rc_all


def generate(check_only):
    built, rc = _build_all()
    if rc:
        return rc

    stale = []
    for gen_name, full, _edits, _rel in built:
        gen = os.path.join(PF_DIR, gen_name)
        cur = None
        if os.path.exists(gen):
            with open(gen, encoding="utf-8") as f:
                cur = f.read()
        if cur != full:
            stale.append(gen_name)
            if not check_only:
                with open(gen, "w", encoding="utf-8") as f:
                    f.write(full)
                print(f"[生成] src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/{gen_name}：已从上游重生成 sha256={_sha(full)}")
        else:
            print(f"[生成] src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/{gen_name}：内容已是最新（未重写）sha256={_sha(full)}")

    if not os.path.exists(CSPROJ):
        print(f"[失败] 找不到 {os.path.relpath(CSPROJ, ROOT)}")
        return 1
    with open(CSPROJ, encoding="utf-8-sig") as f:
        csproj = f.read()

    if MARKER_BEGIN in csproj:
        print("[接线] csproj 已就位（幂等，不改）")
        if check_only and stale:
            print(f"[检查] 生成物过时（需要重新生成）：{', '.join(stale)} ⇒ rc=1")
            return 1
        return 0

    if check_only:
        print("[接线] csproj **未接线** —— 需要运行一次不带 --check 的本脚本")
        return 1

    anchor = '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'
    if _count(csproj, anchor) != 1:
        print(f"[失败] csproj 里 Sdk.targets 锚点出现 {_count(csproj, anchor)} 次（要求 1）")
        return 1
    lines = [MARKER_BEGIN, "  <ItemGroup>"]
    for _rel, gen_name, _edits in TARGETS:
        rel_path = [t[0] for t in TARGETS if t[1] == gen_name][0]
        lines.append(f'    <Compile Remove="$(UpstreamWpfRoot){rel_path}" />')
        lines.append(f'    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/{gen_name}" />')
    lines.append("  </ItemGroup>")
    lines.append(MARKER_END)
    csproj = csproj.replace(anchor, "\n".join(lines) + "\n" + anchor, 1)
    with open(CSPROJ, "w", encoding="utf-8") as f:
        f.write(csproj)
    print(f"[接线] 已注入 {len(TARGETS) * 2} 行到 {os.path.relpath(CSPROJ, ROOT)}")

    print("[注意] `src/Linux/build/port-lib.py PresentationFramework` 会整份重写 csproj ⇒ 本块会被抹掉；"
          "重写后必须重跑本脚本（或登记进 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/reapply-patches.py）。"
          "接线丢失**不会报编译错**，只会一行都不打 ⇒ 别把空输出读成结论。")
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
