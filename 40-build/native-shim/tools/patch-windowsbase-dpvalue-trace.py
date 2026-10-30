#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""T1c 第 5 批 · W1…W4：**读路径为什么不解析 deferred**（WindowsBase `DependencyObject`，只读插桩）。

用法
----
    python3 src/WpfGfx.Linux.Native/tools/patch-windowsbase-dpvalue-trace.py --check   # 只读
    python3 src/WpfGfx.Linux.Native/tools/patch-windowsbase-dpvalue-trace.py           # 生成 + 接线（幂等）
    python3 src/WpfGfx.Linux.Native/tools/patch-windowsbase-dpvalue-trace.py --prove   # 「只插入」证明（只读）

**为什么打这一批**（第 4 批实跑：命中⑥格）
------------------------------------------
```
Q5c **Changed 已 raise**（订阅者被调）        Q6b 即将 RaiseEvent(TextChangedEvent)（:1352 那道门没挡）
Q7b **已 SetCurrentDeferredValue(TextProperty, DeferredTextReference#…)** ×2（容器 len=1 "A" / len=2 "AB"）
Q7c 出口 resetText=False ×3                   Q8a/Q8b = **0 行**（`DeferredTextReference.GetValue` 一次都没被调）
```
T3 的口径：样例读了 **18 次** `_tb.Text`，而 `Q8` 一次没触发 ⇒ **读路径从不解析那个 deferred 引用**，
读者一直拿到旧的 local 值。上游把这条读路径钉在：

    DependencyObject.cs:156  `GetValue(dp)` → `GetValueEntry(…, RequestFlags.FullyResolved).Value`
    DependencyObject.cs:295  `GetEffectiveValue(entryIndex, dp, requests)`
        :301  `effectiveEntry = entry.GetFlattenedEntry(requests)`
        :303  **提前返回**：`(requests & (DeferredReferences|RawEntry)) != 0 || !effectiveEntry.IsDeferredReference`
        :308  `if (!entry.HasModifiers)` → `:313 if (!entry.HasExpressionMarker)` → `:321/:322 真正解析` → `:333 entry.Value = value` → `:336 return entry`
        :340  else（HasModifiers）→ `:372 reference == null ⇒ 提前返回` ／ `:375 解析` → `:399 return effectiveEntry`
    DependencyObject.cs:616  `SetValueCommon(…)`：`:646 bool isDeferredReference = false;` `:677 isDeferredReference = (value is DeferredReference);` `:776 newEntry.Value = value;`

⇒ 三种可能：**(A)** `effectiveEntry.IsDeferredReference == false`（**读到的 effectiveEntry 没有 deferred 标志
⇒ 本格不解析**；⚠️ 这**不**等于"写侧没留住标记" —— 写侧到底有没有写过 deferred，须看**写侧**读数 `W4a`/`W6b`）；
**(B)** 读时 `requests` 带了 `DeferredReferences`/`RawEntry`（**调用方主动要"原始 deferred"**，看 W3a 的调用栈）；
**(C)** `entry.HasModifiers`/`HasExpressionMarker` 为真（走了别的分支，看 W3c/W3d）。

插桩（**全部是纯插入**）
------------------------
    W0 插桩类本体（放进 `DependencyObject.Linux.cs`，同 namespace）        W1 `GetValue` 入口（dp.Name/OwnerType）
    W2 `GetEffectiveValue` 入口（requests / IsDeferredReference / HasModifiers / HasExpressionMarker / entry.Value 类型）
    W3a 提前返回（`:305`）**并判 A/B/C**；若是 B ⇒ 打**调用栈前 4 帧**（找出是谁要"原始 deferred"）
    W3b 真解析（`:336`）｜W3c 修改值分支 reference==null 提前返回（`:372`）｜W3d 修改值分支真解析（`:399`）
    W4a `SetValueCommon` 存值之后（`:776`：value 类型 / isDeferredReference / 存进 newEntry 的值）
    W4b `SetValueCommon` 尾部（`:822` 之前：`UpdateEffectiveValue` 之后 newEntry 的最终状态）

**三条硬约束**
1. **过滤**：只在 `dp.Name == "Text"` 时打（`GetValue`/`GetEffectiveValue` 是全进程最热的 DP 路径，不过滤会把 200 行吃光）。
2. **入口探针在早退门之前**（W1 在 `VerifyAccess` 之后立刻；W2 在 `:303` 那道提前返回之前）。
3. **探针不读任何 DP**（读 DP 会自己触发解析 = 观测者效应）；所有字段读取都在 helper 内部 try/catch 里做，
   **调用点只传已有的局部变量/结构体副本**（不在调用点做数组下标或字段访问 ⇒ 不会在调用点抛异常）。

开关：`WPF_LINUX_INPUT_TRACE`（严格）或 `WPF_LINUX_MSGFLOW_TRACE`（与原生侧同名同义）；缺省关 / ≤200 行 / 只打印。
"""

import argparse
import hashlib
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))

WB_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "WindowsBase")
CSPROJ = os.path.join(WB_DIR, "WindowsBase.Linux.csproj")
UP_REL = "src/Microsoft.DotNet.Wpf/src/WindowsBase/System/Windows/DependencyObject.cs"
GEN = os.path.join(WB_DIR, "DependencyObject.Linux.cs")

TRACE_CLASS = '''// =====================================================================================
//  T1c 第 5 批（W1…W4）：**读路径为什么不解析 deferred**（`DependencyObject`）
//
//  背景：第 4 批证明 `TextContainer.Changed → SetCurrentDeferredValue(TextProperty, dtr)` 全绿，
//  而 `DeferredTextReference.GetValue` **一次都没被调**（样例读了 18 次 `.Text`）⇒
//  读路径从不解析那个 deferred 引用。本批只问：**是 A（写侧没留住标记）/ B（读方要原始 deferred）/ C（走了别的分支）。**
//
//  开关：WPF_LINUX_INPUT_TRACE（严格）或 WPF_LINUX_MSGFLOW_TRACE（与原生同名同义）。缺省关 / ≤200 行 / 只打印。
//  ⚠️ 只对 `dp.Name == "Text"` 打（这条路径是全进程最热的 DP 路径）。
//  ⚠️ 本类**不读任何 DP**；字段读取全部在 try/catch 里，调用点只传局部变量/结构体副本。
// =====================================================================================
internal static class WpfLinuxDpValueTrace
{
    private const string EnvInputTrace = "WPF_LINUX_INPUT_TRACE";
    private const string EnvMsgFlow = "WPF_LINUX_MSGFLOW_TRACE";
    private const int MaxLines = 2000;      // 总上限（原 200 会被 W7 那种高频辅助格吃光 ⇒ L12）
    private const int MaxW7Lines = 16;      // W7（flatten 辅助格）**独立小额度**：采样足够

    private static readonly bool s_enabled;
    private static readonly string s_via;
    private static int s_lines;
    private static int s_w7Lines;
    private static int s_w7Notice;
    private static int s_seq;                    // 第 8 批：调用序号（判『输入之后』）
    private static readonly int s_t0 = System.Environment.TickCount;   // 进程内相对时刻

    static WpfLinuxDpValueTrace()
    {
        string a = null, b = null;
        try { a = System.Environment.GetEnvironmentVariable(EnvInputTrace); } catch (System.Exception) { }
        try { b = System.Environment.GetEnvironmentVariable(EnvMsgFlow); } catch (System.Exception) { }
        s_enabled = IsOn(a) || IsOnNativeSwitch(b);
        s_via = IsOn(a) ? EnvInputTrace : (IsOnNativeSwitch(b) ? EnvMsgFlow : "(都未设)");
        if (s_enabled)
        {
            Emit("via=" + s_via + " pid=" + System.Environment.ProcessId
                 + " —— WB 侧 W1…W4 读数（DependencyObject 的 DP 读路径，只跟 dp.Name == \\"Text\\"）");
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

    private static int s_budgetNotice;

    /// <summary>
    /// **触顶必须看得见**（L12）：额度用尽时打一行，否则"没打"与"没发生"长得一模一样 ——
    /// 第 6 批就是这样把输入时刻的关键读数全丢掉的（W*=199/200）。
    /// </summary>
    private static void NoticeBudget(string what, int used)
    {
        if (System.Threading.Interlocked.Exchange(ref s_budgetNotice, 1) != 0) return;
        try
        {
            System.Console.Error.WriteLine("[INPUT_TRACE] **预算用尽**（" + what + "，已打 " + used
                + " 行）⇒ 之后不再打印；**这回「没打」不等于「没发生」**：请调大额度或缩小过滤范围");
            System.Console.Error.Flush();
        }
        catch (System.Exception) { }
    }

    private static void Emit(string message)
    {
        if (!s_enabled) return;
        if (System.Threading.Interlocked.Increment(ref s_lines) > MaxLines)
        {
            NoticeBudget("总上限 MaxLines=" + MaxLines, MaxLines);
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

    private static string ValOf(object v)
    {
        if (v == null) return "(null)";
        try
        {
            string s = v as string;
            if (s != null)
            {
                string head = (s.Length > 24) ? (s.Substring(0, 24) + "…") : s;
                return "string(len=" + s.Length + ")" + ' ' + '"' + head + '"';
            }
            return v.GetType().Name;
        }
        catch (System.Exception) { return "(读值抛异常)"; }
    }

    /// <summary>只看 `Text`（这条路径太热，不过滤会把预算吃光）。</summary>
    private static int s_textPropIndex = -1;   // TextProperty 的 GlobalIndex（W7 用它把自己**收窄到 Text**）

    private static bool IsTextDp(object dp)
    {
        DependencyProperty p = dp as DependencyProperty;
        if (p == null) return false;
        try
        {
            if (p.Name != "Text") return false;
            // `EffectiveValueEntry._propertyIndex = (short) dp.GlobalIndex`（上游构造里就是这么赋的）
            // ⇒ 记下来，`GetFlattenedEntry` 那边虽然没有 dp 参数，也能用条目的 PropertyIndex 收窄到 Text。
            s_textPropIndex = p.GlobalIndex;
            return true;
        }
        catch (System.Exception) { return false; }
    }

    private static string DpOf(object dp)
    {
        DependencyProperty p = dp as DependencyProperty;
        if (p == null) return "(非 DependencyProperty)";
        try
        {
            return '"' + p.Name + '"' + " OwnerType=" + ((p.OwnerType == null) ? "(null)" : p.OwnerType.Name);
        }
        catch (System.Exception) { return "(读 dp 抛异常)"; }
    }

    private static void DumpStack(string tag)
    {
        try
        {
            System.Diagnostics.StackTrace st = new System.Diagnostics.StackTrace(1, true);
            int n = st.FrameCount;
            if (n > 4) n = 4;
            for (int i = 0; i < n; i++)
            {
                System.Diagnostics.StackFrame f = st.GetFrame(i);
                System.Reflection.MethodBase m = (f == null) ? null : f.GetMethod();
                string name = (m == null) ? "(?)"
                             : ((m.DeclaringType == null) ? m.Name : (m.DeclaringType.Name + "." + m.Name));
                string file = "?";
                try
                {
                    file = (f == null || f.GetFileName() == null)
                         ? "(无行号)"
                         : (System.IO.Path.GetFileName(f.GetFileName()) + ":" + f.GetFileLineNumber());
                }
                catch (System.Exception) { file = "(读行号抛异常)"; }
                Emit(tag + " #" + i + " " + name + " @" + file);
            }
        }
        catch (System.Exception) { }
    }


    // ── 第 7 批 W8/W9：索引从哪来（只对 Text 的 targetIndex 打；LookupEntry 很热）──────────
    /// <summary>W8 `LookupEntry(targetIndex)` 入口。</summary>
    internal static void W8LookupEntry(object target, int targetIndex)
    {
        if (!s_enabled) return;
        if (s_textPropIndex < 0 || targetIndex != s_textPropIndex) return;
        int seq = System.Threading.Interlocked.Increment(ref s_seq);
        Emit("W8 LookupEntry(Text 的 GlobalIndex=" + targetIndex + ") 入口 #" + seq
             + " (+" + (System.Environment.TickCount - s_t0) + "ms)"
             + " EffectiveValuesCount=" + CountOf(target) + " target=" + TypeOf(target));
    }

    /// <summary>W8' `LookupEntry` 的**四条出口**各打一行（索引是怎么算出来的）。</summary>
    internal static void W8LookupResult(object target, int targetIndex, string how, uint index, bool found)
    {
        if (!s_enabled) return;
        if (s_textPropIndex < 0 || targetIndex != s_textPropIndex) return;
        // **实参惰性**：调用点只传 `iPv/iLo`（uint）与 `true/false`，不出现 `new EntryIndex(...)`
        Emit("W8' LookupEntry **出口[" + how + "]** → Index=" + index + " Found=" + found
             + " target=" + TypeOf(target));
    }

    /// <summary>W9 `CheckEntryIndex(entryIndex, targetIndex)` 入口（持索引者的**重查**）。</summary>
    internal static void W9CheckEntryIndex(object target, EntryIndex entryIndex, int targetIndex)
    {
        if (!s_enabled) return;
        if (s_textPropIndex < 0 || targetIndex != s_textPropIndex) return;
        string i = "(?)";
        try { i = "Index=" + entryIndex.Index + " Found=" + entryIndex.Found; } catch (System.Exception) { }
        Emit("W9 CheckEntryIndex 入口 传入 " + i + " targetIndex=" + targetIndex + " target=" + TypeOf(target));
    }

    /// <summary>W9' `CheckEntryIndex` 的两条出口：**沿用旧索引** / **重新查找**。</summary>
    internal static void W9CheckResult(object target, int targetIndex, string how, EntryIndex result)
    {
        if (!s_enabled) return;
        if (s_textPropIndex < 0 || targetIndex != s_textPropIndex) return;
        string r = "(?)";
        try { r = "Index=" + result.Index + " Found=" + result.Found; } catch (System.Exception) { }
        Emit("W9' CheckEntryIndex **出口[" + how + "]** → " + r + " target=" + TypeOf(target));
    }


    // ── 第 8 批 W10：`GetFlattenedEntry` 里**取了哪个 modifier 值**（9 个赋值点各一行）──────
    /// <summary>
    /// `flatten` 组装 flattened entry 时**采用了哪个值**（CoercedValue / AnimatedValue / ExpressionValue / BaseValue /
    /// expressionValue），并把 `ModifiedValue` 的四个字段**并排**打出来 —— 直接判"flatten 有没有把 deferred 取出来"。
    /// **实参惰性**：调用点只传 `this`（源条目）与局部 `modifiedValue`。
    /// </summary>
    internal static void W10Pick(string which, object entry, object modifiedValue)
    {
        if (!s_enabled) return;
        EffectiveValueEntry e = (EffectiveValueEntry)entry;
        int pi = -1;
        try { pi = e.PropertyIndex; } catch (System.Exception) { }
        if (!(s_textPropIndex >= 0 && pi == s_textPropIndex)) return;      // **只对 Text 条目打**
        string val = "(?)", flags = "(?)", modDump = "(?)";
        try { val = ValOf(e.Value); } catch (System.Exception) { }
        try
        {
            flags = "IsCoerced=" + e.IsCoerced + " IsAnimated=" + e.IsAnimated
                  + " IsExpression=" + e.IsExpression + " IsCoercedWithCurrentValue=" + e.IsCoercedWithCurrentValue
                  + " IsDeferredReference=" + e.IsDeferredReference;
        }
        catch (System.Exception) { }
        try
        {
            ModifiedValue mv = modifiedValue as ModifiedValue;
            modDump = (mv == null) ? "(null)"
                    : ("Base=" + ValOf(mv.BaseValue) + " Coerced=" + ValOf(mv.CoercedValue)
                       + " Expression=" + ValOf(mv.ExpressionValue) + " Animated=" + ValOf(mv.AnimatedValue));
        }
        catch (System.Exception ex) { modDump = "(读 ModifiedValue 抛 " + ex.GetType().Name + ")"; }
        Emit("W10 flatten **取值[" + which + "]** 采用的值=" + val + "｜" + flags + "｜ModifiedValue: " + modDump);
    }

    /// <summary>看 `EffectiveValuesCount`（**只读**；取不到就返回 -1）。</summary>
    private static long CountOf(object target)
    {
        try
        {
            DependencyObject dobj = target as DependencyObject;
            if (dobj != null) return (long)dobj.EffectiveValuesCount;
        }
        catch (System.Exception) { }
        return -1;
    }

    /// <summary>W1 `GetValue(dp)` 入口（返回值的类型改在 W3 的四个出口打：那里能同时判路径，且保持纯插入）。</summary>
    internal static void W1GetValue(object target, object dp)
    {
        if (!s_enabled || !IsTextDp(dp)) return;
        // **惰性**：`LookupEntry` 在这里调（调用点只传 this/dp）；它是纯查找（只读、无副作用），
        // 且 `_effectiveValues` 为 null/空时上游自己就返回 `EntryIndex(0, found:false)` ⇒ 不会抛。
        // 读侧**用的就是这个索引**（上游 :166-170 `GetValueEntry(LookupEntry(dp.GlobalIndex), …)`）
        // ⇒ 这一行是判"读侧是否用了陈旧索引"的关键读数。
        string idx = "(取不到)";
        try
        {
            DependencyObject dobj = target as DependencyObject;
            DependencyProperty p = dp as DependencyProperty;
            if (dobj != null && p != null)
            {
                EntryIndex ei = dobj.LookupEntry(p.GlobalIndex);
                idx = "Index=" + ei.Index + " Found=" + ei.Found;
            }
        }
        catch (System.Exception ex) { idx = "(LookupEntry 抛 " + ex.GetType().Name + ")"; }
        Emit("W1 GetValue(…) 读 " + DpOf(dp) + " target=" + TypeOf(target)
             + " ⇒ **读侧 LookupEntry 得到 " + idx + "**");
    }

    /// <summary>W2 `GetEffectiveValue` 入口：把 A/B/C 三格需要的状态一次打全。</summary>
    internal static void W2Entry(object target, object dp, RequestFlags requests,
                                 EffectiveValueEntry entry, EffectiveValueEntry effectiveEntry,
                                 EntryIndex entryIndex)
    {
        if (!s_enabled || !IsTextDp(dp)) return;
        string inIdx = "(?)";
        try { inIdx = "Index=" + entryIndex.Index + " Found=" + entryIndex.Found; } catch (System.Exception) { }
        string isDeferred = "(?)", hasMod = "(?)", hasExpr = "(?)", val = "(?)", flat = "(?)";
        try { isDeferred = effectiveEntry.IsDeferredReference.ToString(); } catch (System.Exception) { }
        try { hasMod = entry.HasModifiers.ToString(); } catch (System.Exception) { }
        try { hasExpr = entry.HasExpressionMarker.ToString(); } catch (System.Exception) { }
        try { val = ValOf(entry.Value); } catch (System.Exception) { }
        try { flat = ValOf(effectiveEntry.Value); } catch (System.Exception) { }
        Emit("W2 GetEffectiveValue 入口 **读路径传入的 entryIndex: " + inIdx + "** " + DpOf(dp) + " requests=" + requests
             + " effectiveEntry.IsDeferredReference=" + isDeferred
             + " entry.HasModifiers=" + hasMod + " entry.HasExpressionMarker=" + hasExpr
             + " entry.Value=" + val + " effectiveEntry.Value=" + flat + " " + TypeOf(target));
        // ── 第 8 批：**原始槽 vs ModifiedValue vs 算出的 effectiveEntry** 并排 ──
        //    `entry` 就是调用点的 `_effectiveValues[entryIndex.Index]`（上游 :300 逐字），所以这里不需要再传数组
        //    （**实参惰性**：调用点一个字段都不取）。
        string slotFlags = "(?)", modDump = "(无修饰)";
        try
        {
            slotFlags = "IsDeferredReference=" + entry.IsDeferredReference
                      + " IsCoerced=" + entry.IsCoerced
                      + " IsAnimated=" + entry.IsAnimated
                      + " IsExpression=" + entry.IsExpression
                      + " IsCoercedWithCurrentValue=" + entry.IsCoercedWithCurrentValue
                      + "（`_source` 是 private 且无访问器 ⇒ 打它的派生标志）";
        }
        catch (System.Exception) { }
        try
        {
            if (entry.HasModifiers)
            {
                ModifiedValue mv = entry.ModifiedValue;
                modDump = (mv == null) ? "(ModifiedValue=null)"
                         : ("BaseValue=" + ValOf(mv.BaseValue)
                            + " CoercedValue=" + ValOf(mv.CoercedValue)
                            + " ExpressionValue=" + ValOf(mv.ExpressionValue)
                            + " AnimatedValue=" + ValOf(mv.AnimatedValue));
            }
        }
        catch (System.Exception ex) { modDump = "(读 ModifiedValue 抛 " + ex.GetType().Name + ")"; }
        Emit("W2' **原始槽** " + slotFlags + "｜**ModifiedValue** " + modDump);
        Emit("W2'' **算出的 effectiveEntry** Value=" + flat + " IsDeferredReference=" + isDeferred
             + "（与上行并排 ⇒ 一眼看出标记是在槽里就有、还是 flatten 之后才丢的）");
    }

    /// <summary>W3a **提前返回（不解析）**：直接判 A / B（B 再打调用栈前 4 帧）。</summary>
    internal static void W3EarlyReturn(object target, object dp, RequestFlags requests,
                                       EffectiveValueEntry effectiveEntry, EntryIndex entryIndex)
    {
        if (!s_enabled || !IsTextDp(dp)) return;
        string inIdx = "(?)";
        try { inIdx = "Index=" + entryIndex.Index + " Found=" + entryIndex.Found; } catch (System.Exception) { }
        bool byRequests = (requests & (RequestFlags.DeferredReferences | RequestFlags.RawEntry)) != 0;
        string isDeferred = "(?)", val = "(?)";
        try { isDeferred = effectiveEntry.IsDeferredReference.ToString(); } catch (System.Exception) { }
        try { val = ValOf(effectiveEntry.Value); } catch (System.Exception) { }
        Emit("W3a **提前返回 effectiveEntry（不解析）** 用的 entryIndex: " + inIdx + " " + DpOf(dp) + " 原因="
             + (byRequests ? "**B：requests 带了 DeferredReferences/RawEntry**"
                           : "**A：effectiveEntry.IsDeferredReference == false ⇒ 本格不解析**（写侧有没有 deferred 须看写侧那行 W6b 有效值槽.IsDeferredReference=…）")
             + " requests=" + requests + " IsDeferredReference=" + isDeferred
             + " 返回的值=" + val);
        if (byRequests) DumpStack("W3a-B 谁要的\\"原始 deferred\\"（前 4 帧）");
    }

    /// <summary>W3b **真解析了**（`!HasModifiers &amp;&amp; !HasExpressionMarker` 经典路径）。</summary>
    internal static void W3ResolvedLocal(object target, object dp, EffectiveValueEntry entry)
    {
        if (!s_enabled || !IsTextDp(dp)) return;
        string val = "(?)";
        try { val = ValOf(entry.Value); } catch (System.Exception) { }
        Emit("W3b **真解析了（经典路径）** " + DpOf(dp) + " entry.Value=" + val
             + " ⇒ deferred 被展开、DP 拿到新值（这条出现 ⇒ 再看 PF 侧 Q8 哨兵）");
    }

    /// <summary>W3c 修改值分支里 `reference == null` ⇒ 提前返回（C 的一种）。</summary>
    internal static void W3BailModifiedNull(object target, object dp, EffectiveValueEntry entry, EffectiveValueEntry effectiveEntry)
    {
        if (!s_enabled || !IsTextDp(dp)) return;
        string hasMod = "(?)", isExpr = "(?)", isCoerced = "(?)", val = "(?)";
        try { hasMod = entry.HasModifiers.ToString(); } catch (System.Exception) { }
        try { isExpr = entry.IsExpression.ToString(); } catch (System.Exception) { }
        try { isCoerced = entry.IsCoerced.ToString(); } catch (System.Exception) { }
        try { val = ValOf(effectiveEntry.Value); } catch (System.Exception) { }
        Emit("W3c 修改值分支 **reference==null ⇒ 提前返回（不解析）** " + DpOf(dp)
             + " HasModifiers=" + hasMod + " IsExpression=" + isExpr + " IsCoerced=" + isCoerced
             + " 返回的值=" + val);
    }

    /// <summary>W3d 修改值分支里**真解析**（expression / coerced 值里带的 deferred）。</summary>
    internal static void W3ResolvedModified(object target, object dp, EffectiveValueEntry entry,
                                            EffectiveValueEntry effectiveEntry, bool referenceFromExpression)
    {
        if (!s_enabled || !IsTextDp(dp)) return;
        string val = "(?)", modKind = "(?)";
        try { val = ValOf(effectiveEntry.Value); } catch (System.Exception) { }
        try
        {
            modKind = "HasModifiers=" + entry.HasModifiers + " IsExpression=" + entry.IsExpression
                    + " IsCoerced=" + entry.IsCoerced + " IsCoercedWithCurrentValue=" + entry.IsCoercedWithCurrentValue;
        }
        catch (System.Exception) { }
        Emit("W3d **真解析了（HasModifiers 分支）** " + DpOf(dp) + " referenceFromExpression=" + referenceFromExpression
             + " " + modKind + " 返回的值=" + val);
    }

    /// <summary>W4a `SetValueCommon` **存值之后**：写侧有没有把 deferred 标记留住（A 的直接证据）。</summary>
    internal static void W4StoreLocal(object target, object dp, object value, bool isDeferredReference,
                                      EffectiveValueEntry newEntry, bool newValueHasExpressionMarker,
                                      EntryIndex entryIndex)
    {
        if (!s_enabled || !IsTextDp(dp)) return;
        string stored = "(?)", storedDeferred = "(?)";
        long idx = -1;
        try { idx = (long)entryIndex.Index; } catch (System.Exception) { }
        try { stored = ValOf(newEntry.Value); } catch (System.Exception) { }
        try { storedDeferred = newEntry.IsDeferredReference.ToString(); } catch (System.Exception) { }
        Emit("W4a SetValueCommon **newEntry.Value = value** " + Tgt(target, dp, idx)
             + " " + DpOf(dp)
             + " 写入的 value=" + ValOf(value) + " isDeferredReference=" + isDeferredReference
             + " newValueHasExpressionMarker=" + newValueHasExpressionMarker
             + " ⇒ newEntry.Value=" + stored + " newEntry.IsDeferredReference=" + storedDeferred);
    }


    /// <summary>
    /// W4/W5/W6 共用的**目标身份**读数：把 `target=` 放在**行首**。
    /// 第 5 批的 W4 把 target 放在**行尾**，实战里很容易被截断 ⇒ 无法与读侧 W1 的 `target=` 对齐。
    /// </summary>
    private static string Tgt(object target, object dp, long entryIndex)   // long：容纳 uint 实参与 -1 哨兵
    {
        string gi = "(?)";
        try
        {
            DependencyProperty p = dp as DependencyProperty;
            if (p != null) gi = p.GlobalIndex.ToString();
        }
        catch (System.Exception) { }
        // ⚠️ `-1` 是**本 helper 的哨兵**（该调用点压根没有 entryIndex，例如 W5 的八个写入口），
        //    **不是**运行时值：`EntryIndex.Index` 是掩掉 bit31 的 uint，永远不可能是 -1。
        //    第一版直接打 `entryIndex=-1` ⇒ 看起来像"实测索引是 -1"，会把人带沟里（G 类假读数）。
        return "target=" + TypeOf(target) + " dp.GlobalIndex=" + gi
             + " entryIndex=" + ((entryIndex < 0) ? "(该点没有索引)" : entryIndex.ToString());
    }


    /// <summary>
    /// W7 `EffectiveValueEntry.GetFlattenedEntry` 的**三个 return 分支**各打一行
    /// —— 确认"flatten 有没有把 deferred 标记吞掉"。
    /// ⚠️ **有界**：这个方法对**每个 DP 读**都会走，且没有 `dp` 参数 ⇒ 不能用 dp.Name 过滤；
    ///    这里用 `IsDeferredReference || requests 带了 DeferredReferences/RawEntry` 过滤（非 deferred 静默）。
    /// </summary>
    internal static void W7Flatten(string branch, object entry, RequestFlags requests)
    {
        if (!s_enabled) return;
        EffectiveValueEntry e = (EffectiveValueEntry)entry;
        bool isDef = false, hasMod = false, hasExpr = false;
        try { isDef = e.IsDeferredReference; } catch (System.Exception) { }
        try { hasMod = e.HasModifiers; } catch (System.Exception) { }
        try { hasExpr = e.HasExpressionMarker; } catch (System.Exception) { }
        bool byRequests = (requests & (RequestFlags.DeferredReferences | RequestFlags.RawEntry)) != 0;
        // **收窄到 Text**：`GetFlattenedEntry` 没有 dp 参数，但条目自带 `PropertyIndex`
        // （== (short)dp.GlobalIndex）；Text 的 GlobalIndex 由 `IsTextDp` 第一次碰到 TextProperty 时记下。
        // 这样启动期的 `DeferredResourceReference`（资源/样式/模板）就不再吃 W7 的额度。
        int pi = -1;
        try { pi = e.PropertyIndex; } catch (System.Exception) { }
        bool isTextEntry = (s_textPropIndex >= 0 && pi == s_textPropIndex);
        if (!isTextEntry && !byRequests) return;
        // **独立小额度**：W7 是辅助格；`IsDeferredReference` 在启动期很常见
        // （`DeferredResourceReference` = 资源/样式/模板，SystemResources.cs:1700）⇒ 144 行是"过滤生效但太宽"，
        // 不是"过滤没生效"。独立封顶后才不会把 W5/W6/W4 的额度吃光（L12）。
        if (System.Threading.Interlocked.Increment(ref s_w7Lines) > MaxW7Lines)
        {
            if (System.Threading.Interlocked.Exchange(ref s_w7Notice, 1) == 0)
            {
                Emit("W7 **已达独立额度 " + MaxW7Lines + " 行** ⇒ 之后不再打 W7（其余 W 格不受影响；"
                     + "**「没打」≠「没发生」**）");
            }
            return;
        }
        string val = "(?)";
        try { val = ValOf(e.Value); } catch (System.Exception) { }
        Emit("W7 GetFlattenedEntry → **" + branch + "** IsDeferredReference=" + isDef
             + " HasModifiers=" + hasMod + " HasExpressionMarker=" + hasExpr
             + " Value=" + val + " requests=" + requests
             + (byRequests ? "（requests 带了 DeferredReferences/RawEntry）" : ""));
    }

    /// <summary>W5 写"Text"的**每一个入口**（每次必打）+ **调用栈前 4 帧**（谁写的）。</summary>
    internal static void W5WriteEntry(object target, object dp, object value, string entry)
    {
        if (!s_enabled || !IsTextDp(dp)) return;
        Emit("W5 **写入口 " + entry + "** 写入的 value=" + ValOf(value) + " " + Tgt(target, dp, -1));
        DumpStack("W5-> 谁写的（前 4 帧）");
    }

    /// <summary>W6a `UpdateEffectiveValue` **之前**：operationType + newEntry/oldEntry + 当前有效值槽。</summary>
    internal static void W6BeforeUpdate(object target, object dp, object operationType,
                                        EffectiveValueEntry newEntry, EffectiveValueEntry oldEntry,
                                        EntryIndex entryIndex, EffectiveValueEntry[] slots)
    {
        if (!s_enabled || !IsTextDp(dp)) return;
        string nv = "(?)", ov = "(?)", sv = "(?)", sd = "(?)";
        try { nv = ValOf(newEntry.Value); } catch (System.Exception) { }
        try { ov = ValOf(oldEntry.Value); } catch (System.Exception) { }
        // ⚠️ 槽的读取（下标 + null + 越界）**都在这里**，调用点只传 `entryIndex` 与数组本身：
        //    静态初始化期 `_effectiveValues` 可能是 null、`entryIndex` 可能无效 ⇒ **一律当作"取不到"，绝不抛**。
        long idx = -1;
        try { idx = (long)entryIndex.Index; } catch (System.Exception) { }
        try
        {
            if (slots != null && idx >= 0 && idx < (long)slots.Length)
            {
                sv = ValOf(slots[idx].Value);
                sd = slots[idx].IsDeferredReference.ToString();
            }
            else
            {
                sv = "越界/不可用";
                sd = "越界/不可用";
            }
        }
        catch (System.Exception ex) { sv = "(读槽抛 " + ex.GetType().Name + ")"; sd = "(?)"; }
        Emit("W6a UpdateEffectiveValue **之前** operationType=" + operationType
             + " newEntry.Value=" + nv + " oldEntry.Value=" + ov
             + " 有效值槽.Value=" + sv + " 有效值槽.IsDeferredReference=" + sd
             + " " + Tgt(target, dp, idx));
    }

    /// <summary>
    /// W6b `UpdateEffectiveValue` **之后**：有效值槽到底被写成了什么（判据 c 的直接读数）。
    /// ⚠️ **下标越界检查在 helper 内**（调用点不做下标访问 ⇒ 调用点不会抛异常、控制流不变）。
    /// </summary>
    internal static void W6AfterUpdate(object target, object dp, object operationType,
                                       EffectiveValueEntry newEntry, EntryIndex entryIndex, EffectiveValueEntry[] slots)
    {
        if (!s_enabled || !IsTextDp(dp)) return;
        string nv = "(?)", sv = "(?)", sd = "(?)";
        long idx = -1;
        try { idx = (long)entryIndex.Index; } catch (System.Exception) { }
        try { nv = ValOf(newEntry.Value); } catch (System.Exception) { }
        try
        {
            // entryIndex.Index 是 uint ⇒ 这里用 long 承接并对 long 做越界判断
            if (slots != null && idx >= 0 && idx < (long)slots.Length)
            {
                sv = ValOf(slots[idx].Value);
                sd = slots[idx].IsDeferredReference.ToString();
            }
            else
            {
                sv = "(下标越界或数组为 null ⇒ 不读)";
                sd = "(?)";
            }
        }
        catch (System.Exception ex) { sv = "(读槽抛 " + ex.GetType().Name + ")"; }
        Emit("W6b UpdateEffectiveValue **之后** operationType=" + operationType
             + " 有效值槽.Value=" + sv + " 有效值槽.IsDeferredReference=" + sd
             + " newEntry.Value=" + nv + " " + Tgt(target, dp, idx));
    }
}

'''

WB_CLASS_ANCHOR = '    public class DependencyObject : DispatcherObject\n'

# ── 【W48D · `D-G56`】插桩类的插入点**必须**在「本类型的 doc 注释 ＋ 属性块」**之前** ─────────
#   为什么换锚：原锚就是下面那行类声明，而 `EDITS` 的语义是「把 `TRACE_CLASS` 文本拼在**锚行前面**」
#   ⇒ 插桩横幅恰好落在**属性块与类声明之间** ⇒ `[TypeDescriptionProvider]`/`[NameScopeProperty]`
#   两条属性的**归属变成插桩类**，`DependencyObject` 自己的属性数掉到 **0** ⇒ BAML 页面的 `NameScope`
#   挂不到页面根上 ⇒ `Storyboard.TargetName` 解析失败 ⇒ 点「工具」页第 2 项即**未处理异常 + abort**
#   （现场读数：`[NS] ATTRCOUNT DependencyObject=0`、`scope=null`、`FindName(PathDemo)=null`）。
#   现锚 = 本类型 **doc 注释的首两行**（上游实测出现 **1** 次）⇒ 插入后「doc ＋ 属性块 ＋ 类声明」
#   与上游**逐字紧贴**（上游原本就是这个形状），且插桩类仍落在同一 `namespace System.Windows` 内。
#   ⚠️ 为什么不改成「追加到文件末尾 ＋ 前向声明」：本批与 `patch-windowsbase-entry-flatten-trace.py`
#      的调用点都写**非全限定**名 `WpfLinuxDpValueTrace.…`（同 namespace 同程序集）⇒ 挪到 namespace
#      之外就得动调用点或加别名（改动面更大、且要新建一处"第二份声明"）；换锚是**零语义改动**，
#      且 `--prove` 仍能机械证明「只插入」。
WB_TYPE_HEAD_ANCHOR = ('    /// <summary>\n'
                       '    ///     DependencyObject is an object that participates in the property dependency system\n')

W1_ANCHOR = '''        public object GetValue(DependencyProperty dp)
        {
            // Do not allow foreign threads access.
'''

W2_ANCHOR = '''            EffectiveValueEntry entry = _effectiveValues[entryIndex.Index];
            EffectiveValueEntry effectiveEntry = entry.GetFlattenedEntry(requests);

            if (((requests & (RequestFlags.DeferredReferences | RequestFlags.RawEntry)) != 0) || !effectiveEntry.IsDeferredReference)
            {
                return effectiveEntry;
            }
'''

W3B_ANCHOR = '''                    _effectiveValues[entryIndex.Index] = entry;
                    return entry;
                }
            }
            else
'''

W3C_ANCHOR = '''                Debug.Assert(reference != null, "the only modified values that can have deferredreferences are (a) expression, (b) coerced control value");
                if (reference == null)
                {
                    return effectiveEntry;
                }
'''

W3D_ANCHOR = '''                _effectiveValues[entryIndex.Index] = entry;

                effectiveEntry.Value = value;
            }
            return effectiveEntry;
        }
'''

W4A_ANCHOR = '''                if (newExpr == null)
                {
                    // simple local value set
                    newEntry.HasExpressionMarker = newValueHasExpressionMarker;
                    newEntry.Value = value;
                }
'''

W4B_ANCHOR = '''            UpdateEffectiveValue(
                entryIndex,
                dp,
                metadata,
                oldEntry,
                ref newEntry,
                coerceWithDeferredReference,
                coerceWithCurrentValue,
                operationType);
'''


# ── 第 6 批 W5：写 "Text" 的每个入口（每次必打 + 栈前 4 帧；探针都在 `this.VerifyAccess();` 之后、
#    各早退门之前；只对 dp.Name=="Text" 打 ⇒ 不会刷屏）──────────────────────────────────
W5_SETVALUE_ANCHOR = '''        public void SetValue(DependencyProperty dp, object value)
        {
            // Do not allow foreign threads access.
            // (This is a noop if this object is not assigned to a Dispatcher.)
            //
            this.VerifyAccess();
'''
W5_SETCURRENT_ANCHOR = '''        public void SetCurrentValue(DependencyProperty dp, object value)
        {
            // Do not allow foreign threads access.
            // (This is a noop if this object is not assigned to a Dispatcher.)
            this.VerifyAccess();
'''
W5_SETVALUEINT_ANCHOR = '''        internal void SetValueInternal(DependencyProperty dp, object value)
        {
            // Do not allow foreign threads access.
            // (This is a noop if this object is not assigned to a Dispatcher.)
            //
            this.VerifyAccess();
'''
W5_SETCURRENTINT_ANCHOR = '''        internal void SetCurrentValueInternal(DependencyProperty dp, object value)
        {
            // Do not allow foreign threads access.
            // (This is a noop if this object is not assigned to a Dispatcher.)
            //
            this.VerifyAccess();
'''
W5_SETDEFERRED_ANCHOR = '''        internal void SetDeferredValue(DependencyProperty dp, DeferredReference deferredReference)
        {
            // Cache the metadata object this method needed to get anyway.
'''
W5_SETVALUEKEY_ANCHOR = '''        public void SetValue(DependencyPropertyKey key, object value)
        {
            // Do not allow foreign threads access.
            // (This is a noop if this object is not assigned to a Dispatcher.)
            //
            this.VerifyAccess();
'''
W5_COERCE_ANCHOR = '''        public void CoerceValue(DependencyProperty dp)
        {
            // Do not allow foreign threads access.
            // (This is a noop if this object is not assigned to a Dispatcher.)
            //
            this.VerifyAccess();
'''
W5_COMMON_ANCHOR = '''        private void SetValueCommon(
            DependencyProperty  dp,
            object              value,
            PropertyMetadata    metadata,
            bool                coerceWithDeferredReference,
            bool                coerceWithCurrentValue,
            OperationType       operationType,
            bool                isInternal)
        {
            if (IsSealed)
'''

_W5_PROBE = '            // ── T1c 第 6 批 W5（只读插桩）：写 "Text" 的入口 + 调用栈（只对 Text 打）──\n'

W5_EDITS = [
    ("W5a SetValue(dp,value) 入口", W5_SETVALUE_ANCHOR,
     W5_SETVALUE_ANCHOR + _W5_PROBE + '            WpfLinuxDpValueTrace.W5WriteEntry(this, dp, value, "SetValue(dp,value)");\n'),
    ("W5b SetCurrentValue 入口", W5_SETCURRENT_ANCHOR,
     W5_SETCURRENT_ANCHOR + _W5_PROBE + '            WpfLinuxDpValueTrace.W5WriteEntry(this, dp, value, "SetCurrentValue");\n'),
    ("W5c SetValueInternal 入口", W5_SETVALUEINT_ANCHOR,
     W5_SETVALUEINT_ANCHOR + _W5_PROBE + '            WpfLinuxDpValueTrace.W5WriteEntry(this, dp, value, "SetValueInternal");\n'),
    ("W5d SetCurrentValueInternal 入口", W5_SETCURRENTINT_ANCHOR,
     W5_SETCURRENTINT_ANCHOR + _W5_PROBE + '            WpfLinuxDpValueTrace.W5WriteEntry(this, dp, value, "SetCurrentValueInternal");\n'),
    ("W5e SetDeferredValue 入口", W5_SETDEFERRED_ANCHOR,
     W5_SETDEFERRED_ANCHOR + _W5_PROBE + '            WpfLinuxDpValueTrace.W5WriteEntry(this, dp, deferredReference, "SetDeferredValue");\n'),
    ("W5f SetValue(key,value) 入口", W5_SETVALUEKEY_ANCHOR,
     W5_SETVALUEKEY_ANCHOR + _W5_PROBE + '            WpfLinuxDpValueTrace.W5WriteEntry(this, key.DependencyProperty, value, "SetValue(key,value)");\n'),
    ("W5g CoerceValue 入口", W5_COERCE_ANCHOR,
     W5_COERCE_ANCHOR + _W5_PROBE + '            WpfLinuxDpValueTrace.W5WriteEntry(this, dp, (object)null, "CoerceValue");\n'),
    ("W5h SetValueCommon 入口", W5_COMMON_ANCHOR,
     W5_COMMON_ANCHOR.replace("        {\n            if (IsSealed)",
                              "        {\n" + _W5_PROBE + '            WpfLinuxDpValueTrace.W5WriteEntry(this, dp, value, "SetValueCommon");\n\n            if (IsSealed)')),
]


# ── 第 7 批 W8/W9：LookupEntry / CheckEntryIndex 的锚点（各要求上游恰好 1 次）──
W8_SIG_ANCHOR = '''        internal EntryIndex LookupEntry(int targetIndex)
        {
            int checkIndex;
'''
W8_ZERO_ANCHOR = '''            if (iHi <= 0)
            {
                return new EntryIndex(0, found: false);
            }
'''
W8_BIN_ANCHOR = '''                if (targetIndex == checkIndex)
                {
                    return new EntryIndex(iPv);
                }
'''
W8_LIN_ANCHOR = '''                if (checkIndex == targetIndex)
                {
                    return new EntryIndex(iLo);
                }
'''
W8_NF_ANCHOR = '''            return new EntryIndex(iLo, found: false);
        }
'''
W9_SIG_ANCHOR = '''        private EntryIndex CheckEntryIndex(EntryIndex entryIndex, int targetIndex)
        {
            uint effectiveValuesCount = EffectiveValuesCount;
'''
W9_REUSE_ANCHOR = '''                if (entry.PropertyIndex == targetIndex)
                {
                    return new EntryIndex(entryIndex.Index);
                }
'''
W9_REQUERY_ANCHOR = '''            return LookupEntry(targetIndex);
        }
'''

W89_EDITS = [
    ("W8 LookupEntry 入口", W8_SIG_ANCHOR,
     W8_SIG_ANCHOR + '            WpfLinuxDpValueTrace.W8LookupEntry(this, targetIndex);\n'),
    ("W8' 出口[iHi<=0]", W8_ZERO_ANCHOR,
     W8_ZERO_ANCHOR.replace("                return new EntryIndex(0, found: false);",
                            "                WpfLinuxDpValueTrace.W8LookupResult(this, targetIndex, \"iHi<=0（表为空）\", 0u, false);\n"
                            "                return new EntryIndex(0, found: false);")),
    ("W8' 出口[二分命中]", W8_BIN_ANCHOR,
     W8_BIN_ANCHOR.replace("                    return new EntryIndex(iPv);",
                           "                    WpfLinuxDpValueTrace.W8LookupResult(this, targetIndex, \"二分命中\", iPv, true);\n"
                           "                    return new EntryIndex(iPv);")),
    ("W8' 出口[线性命中]", W8_LIN_ANCHOR,
     W8_LIN_ANCHOR.replace("                    return new EntryIndex(iLo);",
                           "                    WpfLinuxDpValueTrace.W8LookupResult(this, targetIndex, \"线性命中\", iLo, true);\n"
                           "                    return new EntryIndex(iLo);")),
    ("W8' 出口[未找到⇒插入位]", W8_NF_ANCHOR,
     W8_NF_ANCHOR.replace("            return new EntryIndex(iLo, found: false);",
                          "            WpfLinuxDpValueTrace.W8LookupResult(this, targetIndex, \"未找到⇒插入位\", iLo, false);\n"
                          "            return new EntryIndex(iLo, found: false);")),
    ("W9 CheckEntryIndex 入口", W9_SIG_ANCHOR,
     W9_SIG_ANCHOR + '            WpfLinuxDpValueTrace.W9CheckEntryIndex(this, entryIndex, targetIndex);\n'),
    ("W9' 出口[沿用旧索引]", W9_REUSE_ANCHOR,
     W9_REUSE_ANCHOR.replace("                    return new EntryIndex(entryIndex.Index);",
                             "                    WpfLinuxDpValueTrace.W9CheckResult(this, targetIndex, \"沿用旧索引（槽仍属于该 DP）\", entryIndex);\n"
                             "                    return new EntryIndex(entryIndex.Index);")),
]

EDITS = [
    ("W0 插桩类本体（**插在 doc 注释之前** ⇒ 属性块＋类声明保持紧贴，`D-G56`）",
     WB_TYPE_HEAD_ANCHOR, TRACE_CLASS + WB_TYPE_HEAD_ANCHOR),
    ("W1 GetValue 入口", W1_ANCHOR, '''        public object GetValue(DependencyProperty dp)
        {
            // ── T1c 第 5 批 W1（只读插桩）：入口（**在 VerifyAccess/ThrowIfNull 之前**，只对 dp.Name=="Text" 打）──
            WpfLinuxDpValueTrace.W1GetValue(this, dp);

            // Do not allow foreign threads access.
'''),
    ("W2 GetEffectiveValue 入口 + W3a 提前返回", W2_ANCHOR, '''            EffectiveValueEntry entry = _effectiveValues[entryIndex.Index];
            EffectiveValueEntry effectiveEntry = entry.GetFlattenedEntry(requests);

            // ── T1c W2（只读插桩）：A/B/C 三格需要的状态一次打全（**在下面那道提前返回之前**）──
            WpfLinuxDpValueTrace.W2Entry(this, dp, requests, entry, effectiveEntry, entryIndex);

            if (((requests & (RequestFlags.DeferredReferences | RequestFlags.RawEntry)) != 0) || !effectiveEntry.IsDeferredReference)
            {
                // ── T1c W3a：**提前返回（不解析）** ⇒ 判 A（标记没留住）还是 B（readers 要原始 deferred）──
                WpfLinuxDpValueTrace.W3EarlyReturn(this, dp, requests, effectiveEntry, entryIndex);
                return effectiveEntry;
            }
'''),
    ("W3b 经典路径真解析", W3B_ANCHOR, '''                    _effectiveValues[entryIndex.Index] = entry;
                    // ── T1c W3b：**真解析了**（经典路径）──
                    WpfLinuxDpValueTrace.W3ResolvedLocal(this, dp, entry);
                    return entry;
                }
            }
            else
'''),
    ("W3c 修改值分支 reference==null 提前返回", W3C_ANCHOR, '''                Debug.Assert(reference != null, "the only modified values that can have deferredreferences are (a) expression, (b) coerced control value");
                if (reference == null)
                {
                    // ── T1c W3c：修改值分支里 reference==null ⇒ 提前返回（不解析）──
                    WpfLinuxDpValueTrace.W3BailModifiedNull(this, dp, entry, effectiveEntry);
                    return effectiveEntry;
                }
'''),
    ("W3d 修改值分支真解析（**在 else 作用域内**）", W3D_ANCHOR, '''                _effectiveValues[entryIndex.Index] = entry;

                effectiveEntry.Value = value;
                // ── T1c W3d：修改值分支里**真解析**（expression / coerced 值里带的 deferred）──
                //    ⚠️ 探针必须在**这个 else 块之内**：`referenceFromExpression` 是在块内声明的
                //       （上游 :348-350）⇒ 放到块外就是 CS0103（第 5 批实测：被集成波的构建步骤抓住）。
                WpfLinuxDpValueTrace.W3ResolvedModified(this, dp, entry, effectiveEntry, referenceFromExpression);
            }
            return effectiveEntry;
        }
'''),
    ("W4a 存值之后", W4A_ANCHOR, '''                if (newExpr == null)
                {
                    // simple local value set
                    newEntry.HasExpressionMarker = newValueHasExpressionMarker;
                    newEntry.Value = value;
                    // ── T1c W4a（只读插桩）：**写侧有没有把 deferred 标记留住**（A 的直接证据）──
                    WpfLinuxDpValueTrace.W4StoreLocal(this, dp, value, isDeferredReference, newEntry, newValueHasExpressionMarker, entryIndex);
                }
'''),
    ("W6a+W6b 包住 UpdateEffectiveValue", W4B_ANCHOR, '''            // ── T1c 第 6 批 W6a（只读插桩）：有效值槽**进 UpdateEffectiveValue 之前** ──
            // ⚠️ 实参**惰性**：只传原件（`entryIndex` / 数组），下标与 null/越界判断都在 helper 内。
            //    第一版在这里写 `_effectiveValues[entryIndex.Index]` ⇒ 探针**关着也会求值** ⇒
            //    静态初始化期（Transform..cctor）`_effectiveValues` 还是 null ⇒ **NRE**（第 6 批运行期崩因）。
            WpfLinuxDpValueTrace.W6BeforeUpdate(this, dp, operationType, newEntry, oldEntry, entryIndex, _effectiveValues);

            UpdateEffectiveValue(
                entryIndex,
                dp,
                metadata,
                oldEntry,
                ref newEntry,
                coerceWithDeferredReference,
                coerceWithCurrentValue,
                operationType);

            // ── T1c W6b：**之后**有效值槽被写成了什么（判据 c 的直接读数）──
            //    ⚠️ 下标越界检查在 helper 内（调用点不做下标访问 ⇒ 调用点不抛异常、控制流不变）
            WpfLinuxDpValueTrace.W6AfterUpdate(this, dp, operationType, newEntry, entryIndex, _effectiveValues);
'''),
] + W5_EDITS + W89_EDITS

# ── 【W48D · `D-G56`】防复发判据（跟 `--check` 一起跑；两极化各自实测过，见 `W48D-report.md`）─────
#   两条**独立**的判据，各自都能单独把「修前那种形状」判红：
#     ① 「属性行与类声明之间不得出现插入横幅」——文件级：**任何**一行形如 `[Attr]` 的属性行，
#        其**下一个非空行**都不许是插桩横幅（`// ====…` 或 `//  T1c …`）。
#        修前：`:52 [NameScopeProperty…]` 的下一非空行是 `:53 // =====` ⇒ **红**。
#     ② 「`DependencyObject` 的**紧贴**属性行数 ≥ `WB_ATTR_MIN`」——类级：自类声明向上数，
#        连续的 `[Attr]` 行条数。修前 = **0**（属性全被横幅顶走了）⇒ **红**；修后 = **2**。
#   ⚠️ 为什么两条都要：① 抓的是「**任何人**又把横幅插进某个属性块中间」（不限于本批锚点）；
#      ② 抓的是「本类型的属性**真的**回到了类身上」（`≥2` 是**内容级**读数，不是形状读数）。
#      仿冒方式不同 ⇒ 单靠一条都会有盲区（例如把横幅插到 doc 注释之前但把属性行搬走 ⇒ ① 绿 ② 红）。
ATTR_LINE_RE = re.compile(r'^\s*\[[^\]]*\]\s*$')
BANNER_LINE_RE = re.compile(r'^\s*//\s*(T1c\b|=+\s*$)')
WB_ATTR_MIN = 2


def guard_wb(full):
    """返回 (失败说明列表, 紧贴属性行数或 None)。空列表 = 两条判据都过。"""
    fails = []
    lines = full.splitlines()
    for i, ln in enumerate(lines):
        if not ATTR_LINE_RE.match(ln):
            continue
        j = i + 1
        while j < len(lines) and lines[j].strip() == "":
            j += 1
        if j < len(lines) and BANNER_LINE_RE.match(lines[j]):
            fails.append("判据① 属性行与类声明之间出现插入横幅：生成件 :%d %s ⇒ 下一非空行 :%d 是 %s"
                         % (i + 1, repr(ln.strip()[:48]), j + 1, repr(lines[j].strip()[:32])))
    n = None
    if WB_CLASS_ANCHOR.rstrip("\n") not in lines:
        fails.append("判据② 生成件里找不到类声明行：%s" % repr(WB_CLASS_ANCHOR.rstrip("\n")))
    else:
        k = lines.index(WB_CLASS_ANCHOR.rstrip("\n")) - 1
        n = 0
        while k >= 0 and ATTR_LINE_RE.match(lines[k]):
            n += 1
            k -= 1
        if n < WB_ATTR_MIN:
            fails.append("判据② `DependencyObject` 的紧贴属性行 = %d < %d（属性块被顶走了？）"
                         % (n, WB_ATTR_MIN))
    return fails, n


REQUIRED = [
    ("插桩类存在", "internal static class WpfLinuxDpValueTrace"),
    ("开关名①", 'private const string EnvInputTrace = "WPF_LINUX_INPUT_TRACE";'),
    ("开关名②", 'private const string EnvMsgFlow = "WPF_LINUX_MSGFLOW_TRACE";'),
    ("与原生同名开关同义", 'return v != "0";'),
    ("有界：总上限 2000（原 200 会被 W7 吃光 ⇒ L12）", "private const int MaxLines = 2000;"),
    ("W7 独立小额度 16", "private const int MaxW7Lines = 16;"),
    ("触顶要看得见（打一行）", "private static void NoticeBudget(string what, int used)"),
    ("触顶文案用全角引号（C# 字符串里不能有裸 \"）", "「没打」不等于「没发生」"),
    ("只看 Text（防刷屏）+ 顺手记下 TextProperty 的 GlobalIndex", 'if (p.Name != "Text") return false;'),
    ("不读任何 DP（只读 entry/结构体）", "本类**不读任何 DP**"),
    ("W1 入口", "WpfLinuxDpValueTrace.W1GetValue(this, dp);"),
    ("W2 入口", "WpfLinuxDpValueTrace.W2Entry(this, dp, requests, entry, effectiveEntry, entryIndex);"),
    ("W2 在提前返回之前", "WpfLinuxDpValueTrace.W2Entry(this, dp, requests, entry, effectiveEntry, entryIndex);\n\n            if (((requests & (RequestFlags.DeferredReferences | RequestFlags.RawEntry)) != 0)"),
    ("W3a 提前返回 + A/B 判定", "WpfLinuxDpValueTrace.W3EarlyReturn(this, dp, requests, effectiveEntry, entryIndex);"),
    ("W3a-B 打调用栈（谁要原始 deferred）", "DumpStack("),
    ("W3b 真解析", "WpfLinuxDpValueTrace.W3ResolvedLocal(this, dp, entry);"),
    ("W3c 修改值分支提前返回", "WpfLinuxDpValueTrace.W3BailModifiedNull(this, dp, entry, effectiveEntry);"),
    ("W3d 修改值分支真解析", "WpfLinuxDpValueTrace.W3ResolvedModified(this, dp, entry, effectiveEntry, referenceFromExpression);"),
    ("W4a 存值之后（写侧留没留住标记 + **对象身份**；实参惰性）",
     "WpfLinuxDpValueTrace.W4StoreLocal(this, dp, value, isDeferredReference, newEntry, newValueHasExpressionMarker, entryIndex);"),
    ("W4a 的 target 在**行首**（与读侧 W1 的 target= 可对齐；entryIndex 在 helper 内取）", 'Emit("W4a SetValueCommon **newEntry.Value = value** " + Tgt(target, dp, idx)'),
    ("W6a 进 UpdateEffectiveValue 之前（**实参惰性**：传 entryIndex + 数组，下标在 helper 内）",
     "WpfLinuxDpValueTrace.W6BeforeUpdate(this, dp, operationType, newEntry, oldEntry, entryIndex, _effectiveValues);"),
    ("W6a 的槽读取在 helper 内（null + 越界都当作「取不到」，不抛）",
     'sv = "越界/不可用";'),
    ("W6b 出 UpdateEffectiveValue 之后（有效值槽；实参惰性）",
     "WpfLinuxDpValueTrace.W6AfterUpdate(this, dp, operationType, newEntry, entryIndex, _effectiveValues);"),
    ("W6b 的越界检查在 helper 内（调用点不做下标；`EntryIndex.Index` 在 helper 里转 long）",
     "if (slots != null && idx >= 0 && idx < (long)slots.Length)"),
    ("W5 写入口共 8 处（SetValue/SetCurrentValue/SetValueInternal/SetCurrentValueInternal/SetDeferredValue/SetValue(key)/CoerceValue/SetValueCommon）",
     'WpfLinuxDpValueTrace.W5WriteEntry(this, dp, value, "SetValueCommon");'),
    ("W5 打调用栈（谁写的）", 'DumpStack("W5-> 谁写的（前 4 帧）")'),
    ("W7 helper 定义（供 EffectiveValueEntry 用；**有界过滤**）",
     "internal static void W7Flatten(string branch, object entry, RequestFlags requests)"),
    ("W7 的过滤：**收窄到 Text**（用 PropertyIndex）+ requests",
     "if (!isTextEntry && !byRequests) return;"),
    ("Text 的 GlobalIndex 在 IsTextDp 里记下（PropertyIndex == (short)dp.GlobalIndex）", "s_textPropIndex = p.GlobalIndex;"),
    ("W7 自己的触顶通知（只打一次）", "W7 **已达独立额度 "),
    ("W1 打读侧 LookupEntry 得到的索引（**惰性**：在 helper 内调）", "dobj.LookupEntry(p.GlobalIndex)"),
    ("W2 打**读路径传入的 entryIndex**", "**读路径传入的 entryIndex: "),
    ("W2' 原始槽标志（并排）", "**原始槽** "),
    ("W2' ModifiedValue 四个字段", "BaseValue="),
    ("W2'' 算出的 effectiveEntry（并排）", "**算出的 effectiveEntry** Value="),
    ("W8 带**调用序号 + 相对时刻**（判『输入之后』）", '入口 #" + seq'),
    ("W10 flatten 取了哪个值（帮助判 flatten 选择）", 'W10 flatten **取值[" + which + "]**'),
    ("W3a 打 A 格用的 entryIndex", "用的 entryIndex: "),
    ("W8 LookupEntry 入口", "WpfLinuxDpValueTrace.W8LookupEntry(this, targetIndex);"),
    ("W8' 四条出口（**实参惰性**：uint/bool，不用 new）", 'W8LookupResult(this, targetIndex, "未找到⇒插入位", iLo, false)'),
    ("W9' 出口只留「沿用」一条（重查由紧随的 W8 读数体现）", 'W9CheckResult(this, targetIndex, "沿用旧索引（槽仍属于该 DP）", entryIndex)'),
    ("W9 CheckEntryIndex 入口", "WpfLinuxDpValueTrace.W9CheckEntryIndex(this, entryIndex, targetIndex);"),
    ("W9' 出口[沿用旧索引]（实参惰性：传结构体）", 'W9CheckResult(this, targetIndex, "沿用旧索引（槽仍属于该 DP）", entryIndex)'),
    ("上游提前返回那条判定逐字未动",
     "if (((requests & (RequestFlags.DeferredReferences | RequestFlags.RawEntry)) != 0) || !effectiveEntry.IsDeferredReference)"),
    ("上游 FullyResolved 那条语句逐字未动", "RequestFlags.FullyResolved).Value;"),
]

HEADER = """// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-windowsbase-dpvalue-trace.py **生成**，不要手改。
//
// 内容 = 上游 `upstream/wpf/{rel}` 逐字复制 + {n} 处 T1c **只读插桩**（第 5 批 W1…W4）。
// 每次运行该脚本都会从上游重读重生成；锚点找不到 / 命中数不符时**报错退出**，不会静默产出未打补丁的副本。
//
// 为什么打：第 4 批证明 `TextContainer.Changed → SetCurrentDeferredValue(TextProperty, dtr)` 全绿，
//   而 `DeferredTextReference.GetValue` **一次都没被调**（样例读了 18 次 `.Text`）⇒ 读路径从不解析 deferred。
//   本批只问：A（`effectiveEntry.IsDeferredReference == false`，写侧没留住标记）/ B（`requests` 带了
//   `DeferredReferences`/`RawEntry`）/ C（走了 `HasModifiers`/`HasExpressionMarker` 分支）。
//   只对 `dp.Name == "Text"` 打（这条路径是全进程最热的 DP 路径）；入口探针在各早退门之前；
//   探针**不读任何 DP**（读 DP 会自己触发解析 = 观测者效应）。
//   开关与其它批同一套（WPF_LINUX_INPUT_TRACE / WPF_LINUX_MSGFLOW_TRACE），缺省关、≤200 行、只打印。
"""

MARKER_BEGIN = ("  <!-- ==== T1c 第 5 批：DP 读路径为什么不解析 deferred"
                "（patch-windowsbase-dpvalue-trace.py 注入）==== -->")
MARKER_END = "  <!-- ==== T1c 第 5 批 结束 ==== -->"


def _count(h, n):
    return h.count(n)


def _sha(t):
    return hashlib.sha256(t.encode("utf-8")).hexdigest()


def _header():
    return HEADER.replace("{rel}", UP_REL).replace("{n}", str(len(EDITS)))


def _build():
    up = os.path.join(ROOT, UP_REL)
    if not os.path.exists(up):
        print(f"[失败] 找不到上游 {up}")
        return None, 1
    with open(up, encoding="utf-8-sig") as f:
        text = f.read()

    bad = False
    for name, anchor, _ in EDITS:
        n = _count(text, anchor)
        print(f"[锚点] DependencyObject {name}：上游出现 {n} 次（要求 1）")
        if n != 1:
            bad = True
    if bad:
        print("[失败] 锚点缺失或重复 —— 上游这段改过了？**不做任何静默降级**。")
        return None, 1

    out = text
    for _, anchor, repl in EDITS:
        out = out.replace(anchor, repl, 1)

    up_throws, out_throws = _count(text, "throw "), _count(out, "throw ")
    if up_throws != out_throws:
        print(f"[失败] `throw` 条数变了：上游 {up_throws} → 生成物 {out_throws}")
        return None, 1
    if (_count(text, "{") - _count(text, "}")) != (_count(out, "{") - _count(out, "}")):
        print(f"[失败] 大括号盈亏变化（只读插桩不该改结构）："
              f"上游 {_count(text, '{')}/{_count(text, '}')} → 生成物 {_count(out, '{')}/{_count(out, '}')}")
        return None, 1
    print(f"[断言] 大括号盈亏一致：{_count(out, '{')} / {_count(out, '}')}")
    print(f"[断言] `throw` {up_throws}=={out_throws}；行数 {len(text.splitlines())} → "
          f"{len(out.splitlines())}（+{len(out.splitlines()) - len(text.splitlines())}）")

    full = _header() + out
    for name, needle in REQUIRED:
        if needle not in full:
            print(f"[失败] 生成物缺少结构断言：{name}")
            return None, 1
    print(f"[断言] 结构断言 {len(REQUIRED)}/{len(REQUIRED)} 全中")

    # 【W48D · `D-G56`】防复发判据（跟着 `--check` 一起跑）
    gfails, gn = guard_wb(full)
    for m in gfails:
        print(f"[失败] `D-G56` 防复发判据：{m}")
    if gfails:
        print("[失败] 属性块被插桩横幅打断 ⇒ 属性会挂到插桩类上（`D-G56`，会 abort 应用）。**不做任何静默降级**。")
        return None, 1
    print(f"[断言] `D-G56` 防复发判据：① 属性块与类声明之间无插入横幅 ✓；"
          f"② `DependencyObject` 紧贴属性行 = {gn}（要求 ≥ {WB_ATTR_MIN}）✓")
    return full, 0


def prove():
    with open(os.path.join(ROOT, UP_REL), encoding="utf-8-sig") as f:
        text = f.read()
    body, where = None, None
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
        full, rc = _build()
        if rc:
            return 1
        body = full[len(_header()):]
        where = "**内存**（生成物尚未产出/已过时）"

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
    print(f"[① 只插入] 逆序回代后与上游**逐字节相同** ✓ sha256={_sha(cur)}（== 上游 {_sha(text)}）")
    return 0


def generate(check_only):
    full, rc = _build()
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
        if check_only and not up_to_date:
            print("[检查] 生成物过时（需要重新生成）⇒ rc=1")
            return 1
        return 0
    if check_only:
        print("[接线] csproj **未接线** —— 需要运行一次不带 --check 的本脚本")
        return 1

    anchor = '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'
    if _count(csproj, anchor) != 1:
        print(f"[失败] csproj 里 Sdk.targets 锚点出现 {_count(csproj, anchor)} 次（要求 1）")
        return 1
    block = (MARKER_BEGIN + "\n"
             "  <ItemGroup>\n"
             f'    <Compile Remove="$(UpstreamWpfRoot){UP_REL}" />\n'
             '    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/DependencyObject.Linux.cs" />\n'
             "  </ItemGroup>\n"
             + MARKER_END + "\n")
    csproj = csproj.replace(anchor, block + anchor, 1)
    with open(CSPROJ, "w", encoding="utf-8") as f:
        f.write(csproj)
    print(f"[接线] 已注入 2 行到 {os.path.relpath(CSPROJ, ROOT)}")

    print("[注意] `src/Linux/build/port-lib.py WindowsBase` 会**整份重写** csproj ⇒ 本块会被抹掉；"
          "重写后必须重跑本脚本。接线丢失**不会报编译错**，只会一行都不打 ⇒ 别把空输出读成结论。")
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
