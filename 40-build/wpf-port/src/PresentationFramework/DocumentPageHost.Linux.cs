// ⚠️ 本文件由 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/reapply-patches.py **生成**，不要手改。
//
// 内容 = 上游 `src/Microsoft.DotNet.Wpf/src/PresentationFramework/MS/Internal/documents/DocumentPageHost.cs` 逐字复制 + 4 处 W86A（`TASK-0304`/`TASK-0305`）改动。
// 每次运行该脚本都会从上游重读重生成；needle 找不到 / 命中数不符时**报错退出**
// （不会静默产出未打补丁的副本）。改动逐处见：
//   E1 ／ E2 ／ E3 ／ E4
//
// 背景（`D-G70`/`D-G78`）：本移植没有 PTS/原生 LineServices ⇒ 切「富文本」/「流文档」页
// 曾**整进程 `rc=134`**。本件把它变成「**具名、可判、可见的能力边界**」：
//   · native 侧新增 `src/WpfGfx.Linux.Native/src/win32_pts.c`（6 个入口导出、**如实返回非零 LsErr**、
//     打具名台账 `PTS_GAP entry=… seq=… err=-10000`）；
//   · 本文件（托管侧）负责：**拆掉毒池项**（`D-G78` 的根因）＋ **具名能力闩** ＋ **页级可见降级**。
// ⚠️ `Invariant.Assert` **一个都没删、没放宽** —— 目标是让它们**不再被走到**；
//    若仍被走到，断言照旧响亮（那是新缺陷）。
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Description: Provides a view port for a page of content for a DocumentPage.
//

using System.Windows;           // UIElement
using System.Windows.Media;     // Visual

namespace MS.Internal.Documents
{
    /// <summary> 
    /// Provides a view port for a page of content for a DocumentPage.
    /// </summary>
    internal class DocumentPageHost : FrameworkElement
    {
        //-------------------------------------------------------------------
        //
        //  Constructors
        //
        //-------------------------------------------------------------------

        #region Constructors

        /// <summary> 
        /// Create an instance of a DocumentPageHost.
        /// </summary>
        internal DocumentPageHost()
            : base()
        {
        }

        #endregion Constructors

        //-------------------------------------------------------------------
        //
        //  Internal Methods
        //
        //-------------------------------------------------------------------

        #region Internal Methods

        internal static void DisconnectPageVisual(Visual pageVisual)
        {
            // There might be a case where a visual associated with a page was 
            // inserted to a visual tree before. It got removed later, but GC did not
            // destroy its parent yet. To workaround this case always check for the parent
            // of page visual and disconnect it, when necessary.
            Visual currentParent = VisualTreeHelper.GetParent(pageVisual) as Visual;
            if (currentParent != null)
            {
                // ── `T-B11`（`PAGEVIEW-ONSCREEN`）：直接挂载形态下，页视觉的父**就是**宿主 ──────
                //  打开驱动时 `PageVisual` setter 把页视觉**本身**挂到宿主（照 `FlowDocumentView`
                //  范式，不经 `ContainerVisual` 包壳）⇒ 必须先认这一形态，否则下游
                //  `currentParent as ContainerVisual == null` 会误判成"父不是 DocumentPageHost"
                //  而**响亮抛错**（那是一处**新缺陷**，不是本增量要的）。关 ⇒ 逐字回上游。
                DocumentPageHost directHost = currentParent as DocumentPageHost;
                if (directHost != null)
                {
                    directHost.PageVisual = null;
                    return;
                }
                ContainerVisual pageVisualHost = currentParent as ContainerVisual;
                if (pageVisualHost == null)
                    throw new ArgumentException(SR.DocumentPageView_ParentNotDocumentPageHost, nameof(pageVisual));
                DocumentPageHost docPageHost = VisualTreeHelper.GetParent(pageVisualHost) as DocumentPageHost;
                if (docPageHost == null)
                    throw new ArgumentException(SR.DocumentPageView_ParentNotDocumentPageHost, nameof(pageVisual));
                docPageHost.PageVisual = null;
            }
        }

        #endregion Internal Methods

        //-------------------------------------------------------------------
        //
        //  Internal Properties
        //
        //-------------------------------------------------------------------

        #region Internal Properties

        /// <summary>
        /// Root of visual subtree hosted by this DocumentPageHost.
        /// </summary>
        internal Visual PageVisual
        {
            get
            {
                return _pageVisual;
            }
            set
            {
                ContainerVisual pageVisualHost;
                WpfLinuxPageViewProbe.ReportHostSetSeq(this, _pageVisual, value);
                // ── `T-B11`（`PAGEVIEW-ONSCREEN`）：照 `FlowDocumentView` 范式挂载页视觉 ──────────
                //  `FlowDocumentView` 那条**可用**接线是"宿主**自己**把页视觉对象 `AddVisualChild`
                //  进自己的视觉树 ＋ 显式定位"（`FlowDocumentView.Linux.cs:247-254`），**不**经内层
                //  元素的隐式 `Measure/Arrange`。上游 `DocumentPageHost` 则是把页视觉塞进一个
                //  **新建的 `ContainerVisual` 包壳**、再把包壳 `AddVisualChild`（`:95-98`）。
                //  两种形态在**本移植的成帧面上不等价**：现取（腿 `b11visit`）渲染遍历**确已**走到
                //  `DPV → DPH → 页视觉`（`[PAGEVIEW] site=DPV.GetVisualChild` ＝ `DPH.GetVisualChild`
                //  ＝ 246 次、尺寸/可见性全对），却**一像素也上不了屏** ⇒ 断点在"页视觉子树在本移植的
                //  成帧面上没被合成"。本块把挂载改成与 `FlowDocumentView` **同形**。
                //  ⚠️ **不删**上游任何一步（关闸时逐字保留包壳形态）；**不删／不放宽**任何断言。
                //  零假值／默认关：`WPF_PAGEVIEW_ONSCREEN=1` 才启用；未设 ⇒ **逐字回上游**（反极性腿）。
                if (_pageVisual != null)
                {
                    if (WpfLinuxPageViewProbe.Enabled)
                    {
                        this.RemoveVisualChild(_pageVisual);
                    }
                    else
                    {
                        pageVisualHost = VisualTreeHelper.GetParent(_pageVisual) as ContainerVisual;
                        Invariant.Assert(pageVisualHost != null);
                        pageVisualHost.Children.Clear();
                        this.RemoveVisualChild(pageVisualHost);
                    }
                }
                _pageVisual = value;
                if (_pageVisual != null)
                {
                    if (WpfLinuxPageViewProbe.Enabled)
                    {
                        this.AddVisualChild(_pageVisual);
                        WpfLinuxPageViewProbe.ReportAttach(this, _pageVisual, true);
                    }
                    else
                    {
                        pageVisualHost = new ContainerVisual();
                        this.AddVisualChild(pageVisualHost);
                        pageVisualHost.Children.Add(_pageVisual);
                        pageVisualHost.SetValue(FlowDirectionProperty, FlowDirection.LeftToRight);
                        WpfLinuxPageViewProbe.ReportAttach(this, _pageVisual, false);
                    }
                }
            }
        }

        /// <summary>
        /// Internal cached offset.
        /// </summary>
        internal Point CachedOffset;

        #endregion Internal Properties

        #region VisualChildren
        /// <summary>
        ///   Derived class must implement to support Visual children. The method must return
        ///    the child at the specified index. Index must be between 0 and GetVisualChildrenCount-1.
        ///
        ///    By default a Visual does not have any children.
        ///
        ///  Remark: 
        ///       During this virtual call it is not valid to modify the Visual tree. 
        /// </summary>
        protected override Visual GetVisualChild(int index)
        {
            WpfLinuxPageViewProbe.ReportVisit("DPH.GetVisualChild", this, _pageVisual, index);
            WpfLinuxPageViewProbe.ReportRenderSub(this, _pageVisual);
            if (index != 0 || _pageVisual == null)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, SR.Visual_ArgumentOutOfRange);
            }
            // T-B11：直接挂载形态下页视觉的父**就是**宿主 ⇒ 子必须是页视觉**本身**
            // （与 `FlowDocumentView` 的 `GetVisualChild` 返回被 `AddVisualChild` 的同一对象**同源**）。
            if (WpfLinuxPageViewProbe.Enabled)
            {
                return _pageVisual;
            }
            return VisualTreeHelper.GetParent(_pageVisual) as Visual;
        }

        /// <summary>
        ///  Derived classes override this property to enable the Visual code to enumerate 
        ///  the Visual children. Derived classes need to return the number of children
        ///  from this method.
        ///
        ///    By default a Visual does not have any children.
        ///
        ///  Remark: During this virtual method the Visual tree must not be modified.
        /// </summary>        
        protected override int VisualChildrenCount
        {
            get { return _pageVisual != null ? 1 : 0; }
        }

        #endregion VisualChildren

        private Visual _pageVisual;
    }

    /// <summary>
    /// `T-B11`（`PAGEVIEW-ONSCREEN`）：分页视觉宿主（`DocumentPageView`＋`DocumentPageHost`）的
    /// **只读台账** ＋ **「上屏」驱动闸**。照 `FlowDocumentView` 那条**可用**接线（把页视觉对象本身
    /// `AddVisualChild` 到本控件 ＋ 显式定位）做最小对齐；见本块在 `reapply-patches.py` 内的说明。
    ///
    /// ⚠️ 它**只**做两件事：① 只读打印（身份／尺寸／子树包围盒／父链）；② 在 `WPF_PAGEVIEW_ONSCREEN`
    /// `=1`（**默认关**）下把"页视觉 → 宿主"的挂载／定位按 `FlowDocumentView` 的同形做法补上。
    /// **不删上游任何一步**、**不删不放宽任何 `Invariant.Assert`**、**不碰 native 几何**。
    /// </summary>
    internal static class WpfLinuxPageViewProbe
    {
        private const int TraceMax = 600;
        private static int _trace;
        // ⏪ `T-B13`：渲染遍历读数（`DPV.GetVisualChild`／`DPH.GetVisualChild`，每趟渲染数百行）
        //   **单独记账**（不再吃 `TraceMax` 的共享预算），且**按元素**各记一份 —— 否则先出现的
        //   宿主会把全局额度吃光，把**后面的**宿主（tab3 的 `ReaderPageViewer` 那一份）整片吞掉
        //   （`T-B13` 第一次现取即栽在这里）。
        private const int VisitPerSelfMax = 200;
        private static readonly System.Collections.Generic.Dictionary<int, int> _visitPerSelf
            = new System.Collections.Generic.Dictionary<int, int>();
        private static int _enabled = -1;

        internal static bool Enabled
        {
            get
            {
                if (_enabled < 0)
                {
                    string s = null;
                    try { s = System.Environment.GetEnvironmentVariable("WPF_PAGEVIEW_ONSCREEN"); }
                    catch (System.Exception) { s = null; }
                    // ⚠️ `T-B11` **默认关**（只有显式 "1" 才开）：本条接线已被现取**证伪**
                    //   （开/关两腿 `tab2` 帧逐字节相同、具名色同为 0，见载体报告 §4）⇒ 不默认启用，
                    //   不把"零效果"的改动当成"修好了"。保持可复现（`=1` 即开）以备后续复核。
                    _enabled = (s == "1") ? 1 : 0;
                }
                return _enabled == 1;
            }
        }

        internal static bool ProbeOn
        {
            get
            {
                string s = null;
                try { s = System.Environment.GetEnvironmentVariable("WPF_PAGEVIEW_PROBE"); }
                catch (System.Exception) { s = null; }
                return (s != "0");
            }
        }

        private static void Emit(string line)
        {
            try { System.Console.Error.WriteLine(line); System.Console.Error.Flush(); }
            catch (System.Exception) { }
        }

        internal static string Id(object o)
        {
            if (o == null) { return "null"; }
            int h;
            try { h = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
            catch (System.Exception) { return "NA"; }
            return "0x" + h.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static string N(double v)
        {
            return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static string S(Size sz)
        {
            return N(sz.Width) + "x" + N(sz.Height);
        }

        internal static string Bounds(Visual v)
        {
            if (v == null) { return "null"; }
            try
            {
                Rect r = VisualTreeHelper.GetDescendantBounds(v);
                if (r.IsEmpty) { return "empty"; }
                return N(r.X) + "," + N(r.Y) + "," + N(r.Width) + "," + N(r.Height);
            }
            catch (System.Exception) { return "NA"; }
        }

        internal static int Kids(Visual v)
        {
            if (v == null) { return -1; }
            try { return VisualTreeHelper.GetChildrenCount(v); }
            catch (System.Exception) { return -2; }
        }

        internal static string ParentId(Visual v)
        {
            if (v == null) { return "null"; }
            try { return Id(VisualTreeHelper.GetParent(v)); }
            catch (System.Exception) { return "NA"; }
        }

        internal static string Pt(UIElement e, UIElement relativeTo)
        {
            if (e == null || relativeTo == null) { return "null"; }
            try
            {
                Point p = e.TranslatePoint(new Point(0, 0), relativeTo);
                return N(p.X) + "," + N(p.Y);
            }
            catch (System.Exception) { return "NA"; }
        }

        internal static string Off(Visual v)
        {
            if (v == null) { return "null"; }
            try
            {
                Vector o = VisualTreeHelper.GetOffset(v);
                return N(o.X) + "," + N(o.Y);
            }
            catch (System.Exception) { return "NA"; }
        }

        /// <summary>
        /// 只读：**页视觉的第一层子（＝ PTS 页的 `ContainerVisual`）自身的读数** —— 子数/包围盒/自身
        /// 绘制内容。它把"页视觉**壳**已建"与"页视觉**里真的有内容视觉**"分开：若 `kids=0 ∧ content=empty`，
        /// 那"上不了屏"的成因在**壳之内空**（在分页器造页那一段），而**不在** `DocumentPageView`／
        /// `DocumentPageHost` 的挂载线上。
        /// </summary>
        internal static string ChildInfo(Visual v)
        {
            if (v == null) { return "null"; }
            try
            {
                int n = VisualTreeHelper.GetChildrenCount(v);
                if (n == 0) { return "nokids"; }
                Visual c = VisualTreeHelper.GetChild(v, 0) as Visual;
                return "c=" + Id(c) + ",kids=" + Kids(c) + ",bounds=" + Bounds(c) + ",content=" + Content(c);
            }
            catch (System.Exception) { return "NA"; }
        }

        private static void SubRec(Visual v, int depth, int lvl, System.Text.StringBuilder sb, ref int budget)
        {
            if (v == null || lvl > depth || budget <= 0) { return; }
            budget--;
            int k;
            try { k = VisualTreeHelper.GetChildrenCount(v); } catch (System.Exception) { k = -2; }
            sb.Append("L").Append(lvl).Append(':').Append(Id(v)).Append(",k=").Append(k)
              .Append(",b=").Append(Bounds(v)).Append(",c=").Append(Content(v)).Append(" | ");
            for (int i = 0; i < k && i < 6; i++)
            {
                Visual c;
                try { c = VisualTreeHelper.GetChild(v, i) as Visual; } catch (System.Exception) { c = null; }
                SubRec(c, depth, lvl + 1, sb, ref budget);
            }
        }

        /// <summary>只读：页视觉子树**逐层**读数（层号／身份／子数／包围盒／自身绘制内容），有界。</summary>
        internal static string Sub(Visual v, int depth)
        {
            if (v == null) { return "null"; }
            var sb = new System.Text.StringBuilder();
            int budget = 24;
            try { SubRec(v, depth, 0, sb, ref budget); }
            catch (System.Exception) { return "NA"; }
            return sb.ToString();
        }

        // ── `T-B17`：**逐层"类型 + 自身绘制"**读数 ────────────────────────────────
        //  【为什么必须补这一格】`Sub` 只打 `k`（子数）／`b`（包围盒）／`c`（内容包围盒），
        //   而 `VisualTreeHelper.GetContentBounds` 对 `ContainerVisual` 返回的是**子树并集**
        //   ⇒ 一个"空壳容器"与一个"真有绘制的叶子"在 `Sub` 里**长得一模一样**。
        //   本节把两者的差**做成读数**：类型名 ＋（若是 `DrawingVisual`）`Drawing` 的类型。
        //   `draw=null` ⇒ 该叶子**没有绘制指令**（断点在更上游）；`draw=DrawingGroup/…` ⇒
        //   绘制指令在，断点在**呈现**那一跳。缺省常开、只读、有界。
        private static string DrawOf(Visual v)
        {
            try
            {
                DrawingVisual dv = v as DrawingVisual;
                if (dv == null) { return "-"; }
                Drawing d = dv.Drawing;
                return (d == null) ? "none" : d.GetType().Name;
            }
            catch (System.Exception) { return "NA"; }
        }

        private static void TypeRec(Visual v, int depth, int lvl, System.Text.StringBuilder sb, ref int budget)
        {
            if (v == null || lvl > depth || budget <= 0) { return; }
            budget--;
            int k;
            try { k = VisualTreeHelper.GetChildrenCount(v); } catch (System.Exception) { k = -2; }
            string tn;
            try { tn = (v == null) ? "null" : v.GetType().Name; } catch (System.Exception) { tn = "NA"; }
            sb.Append("T").Append(lvl).Append(':').Append(tn).Append('@').Append(Id(v))
              .Append(",k=").Append(k).Append(",draw=").Append(DrawOf(v)).Append(" | ");
            for (int i = 0; i < k && i < 8; i++)
            {
                Visual c;
                try { c = VisualTreeHelper.GetChild(v, i) as Visual; } catch (System.Exception) { c = null; }
                TypeRec(c, depth, lvl + 1, sb, ref budget);
            }
        }

        internal static string Types(Visual v, int depth)
        {
            if (!TypesOn) { return "(off)"; }
            if (v == null) { return "null"; }
            var sb = new System.Text.StringBuilder();
            int budget = 40;
            try { TypeRec(v, depth, 0, sb, ref budget); }
            catch (System.Exception) { return "NA"; }
            return sb.ToString();
        }

        /// <summary>
        /// `T-B17`（纪律 41「仪器不得扰动被测对象」）：`Types` 会**枚举**视觉树的深层
        /// （`VisualTreeHelper.GetChild`），而这本身会催熟视觉树 ⇒ 本节**缺省关**
        /// （`WPF_PAGEVIEW_TYPES=1` 才开）。**现取**：开/关两腿 `tab1`／`tab3` 帧**逐字节相同**
        /// （`c22457cf663453dd`／`71a93980be1f49a6`）⇒ 本探针在**本形态**下**测得**不扰动；
        /// 缺省关仍是**保守选择**（别的形态未证）。
        /// </summary>
        private static int _typesOn = -1;
        private static bool TypesOn
        {
            get
            {
                if (_typesOn < 0)
                {
                    string s = null;
                    try { s = System.Environment.GetEnvironmentVariable("WPF_PAGEVIEW_TYPES"); }
                    catch (System.Exception) { s = null; }
                    _typesOn = (s == "1") ? 1 : 0;
                }
                return _typesOn == 1;
            }
        }

        // ── `T-B17`（`PRECOND-TAB3-EHANDLE-CALLSITE`）：`RenderTargetBitmap` 快照建不出来 ⇒ 降级 ──
        //  【现取抛点（`T-B17` 机器证，`FirstChanceException` 抓的栈）】`E_HANDLE` 的调用点是
        //    `BitmapSource.set_WicSourceHandle`（上游 `BitmapSource.cs:579` 的
        //    `HRESULT.Check(MILUnknown.QueryInterface(value, IID_IWICBitmapSource, out _))`），
        //    由 `RenderTargetBitmap.FinalizeCreation()`（`RenderTargetBitmap.cs:256`）触发。
        //    触发链 ＝ `SinglePageViewer.HandleAllBreakRecordsInvalidated`
        //    → `DocumentPageView.DuplicateVisual()` → `DuplicatePageVisual()`（`new RenderTargetBitmap`）。
        //  【语义】`_pageVisualClone` 只是"重分页期间先显示上一张快照"的**可选优化**；上游本块**已经**
        //    把"渲染目标建不出来"当作可降级（那条 `OverflowException` 的注释逐字就是
        //    "render target creation not possible"）⇒ 本移植补同一支。
        //  【零假值】`WPF_DPV_RTB_FALLBACK=0` ⇒ **整块不发生**（`throw;` 照旧 ⇒ 反极性腿）。
        private static int _rtbFallback = -1;
        internal static bool RtbFallback
        {
            get
            {
                if (_rtbFallback < 0)
                {
                    string s = null;
                    try { s = System.Environment.GetEnvironmentVariable("WPF_DPV_RTB_FALLBACK"); }
                    catch (System.Exception) { s = null; }
                    // ⚠️ `T-B17` 现取：**缺省关**（只有显式 "1" 才开）。理由（与 `T-B11`/`T-B13`/`T-B16` 同体例）：
                    //   ① 开腿**消除**了 `E_HANDLE`（症状成对 1→0，伴随 `[DPV] site=RtbFallback` 大声记账）——
                    //      这是**真的**；但 ② **帧面判据不成立**：开腿 `tab3` 帧退化为**同腿 `tab1` 的帧**
                    //      （`c22457cf663453dd`，与 `tab1` **逐字节**同），而**缺省/关腿**的 `tab3` 帧带
                    //      "页边框"（`71a93980be1f49a6`，两次独立复现）⇒ 开腿**不再出现**那个页边框 ⇒
                    //      读作"**没再排帧**"（上一 tab 的帧）而不是"页真的上屏"。⇒ 不默认启用。
                    //    `WPF_DPV_RTB_FALLBACK=1` 即开（可复现）。
                    _rtbFallback = (s == "1") ? 1 : 0;
                }
                return _rtbFallback == 1;
            }
        }

        /// <summary>`T-B17`：降级发生时**大声**打一行（点名抛点／类型／HRESULT），不静默吞。</summary>
        internal static void ReportRtbFallback(System.Exception ex)
        {
            try
            {
                string hr = "NA";
                try { hr = "0x" + ex.HResult.ToString("x8", System.Globalization.CultureInfo.InvariantCulture); }
                catch (System.Exception) { hr = "NA"; }
                System.Console.Error.WriteLine("[DPV] site=RtbFallback outcome=degrade-to-live-visual type="
                    + ex.GetType().Name + " hr=" + hr
                    + " cause=BitmapSource.set_WicSourceHandle(MILUnknown.QueryInterface E_HANDLE)"
                    + " NOINFO=dpv-rtb-fallback-readonly");
                System.Console.Error.Flush();
            }
            catch (System.Exception) { }
        }

        // ── `T-B17`：`ArrangeOverride` 末尾**可撤**的 `InvalidateVisual`（把"宿主进场后**没再排帧**"做成成对读数）──
        //  【为什么需要它】`T-B17` 现取：闸开腿（主题字典在场、页宿主被接出）在**缺省**下 `tab3`
        //    帧只有"页边框"（`71a93980be1f49a6`）；把 `RenderTargetBitmap` 那条未处理异常
        //    **降级掉**之后，`tab3` 帧**反而不动**（＝上一 tab 的帧）⇒ **原异常路径正是那一次重绘的
        //    触发者**。本闸把"宿主接上之后**主动**排一帧"做成可证伪的候选：`WPF_DPV_INVALIDATE=1` 才开。
        //  **有界**（每元素 ≤3 次），缺省**关**（不设＝逐字回上游）。
        private static int _invalidateOn = -1;
        private static readonly System.Collections.Generic.Dictionary<int, int> _invalidatePerSelf
            = new System.Collections.Generic.Dictionary<int, int>();
        private static bool InvalidateOn
        {
            get
            {
                if (_invalidateOn < 0)
                {
                    string s = null;
                    try { s = System.Environment.GetEnvironmentVariable("WPF_DPV_INVALIDATE"); }
                    catch (System.Exception) { s = null; }
                    _invalidateOn = (s == "1") ? 1 : 0;
                }
                return _invalidateOn == 1;
            }
        }

        internal static void ReportArrangeEndInvalidate(FrameworkElement view)
        {
            if (!InvalidateOn || view == null) { return; }
            int k;
            try { k = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(view); }
            catch (System.Exception) { return; }
            int n;
            _invalidatePerSelf.TryGetValue(k, out n);
            if (n >= 3) { return; }
            _invalidatePerSelf[k] = n + 1;
            // ⚠️ 必须**离开布局趟**再失效：`ArrangeOverride` 内直接 `InvalidateVisual()` 现取**无效**
            //   （`inv1` 腿帧与不失效**逐字节相同**）⇒ 改用 `Dispatcher.BeginInvoke(Render)`。
            try
            {
                view.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render,
                    new System.Action(view.InvalidateVisual));
            }
            catch (System.Exception) { }
            Emit("[DPV] site=InvalidateVisual id=" + Id(view) + " n=" + (n + 1)
                 + " via=dispatcher-begininvoke-render NOINFO=dpv-invalidate-readonly");
        }

        private static bool Gate(string tag)
        {
            if (!ProbeOn) { return false; }
            if (_trace >= TraceMax)
            {
                if (_trace == TraceMax)
                {
                    _trace++;
                    Emit("[PAGEVIEW] trace=suppressed-after-" + TraceMax + "lines tag=" + tag);
                }
                return false;
            }
            _trace++;
            return true;
        }

        internal static void ReportMeasure(FrameworkElement view, Size available, object paginator, object page)
        {
            if (!Gate("DPV.Measure")) { return; }
            Emit("[DPV] site=MeasureOverride id=" + Id(view) + " avail=" + S(available)
                 + " pag=" + Id(paginator) + " page=" + Id(page)
                 + " desired=" + S(view.DesiredSize) + " render=" + S(view.RenderSize)
                 + " NOINFO=dpv-measure-readonly");
        }

        internal static void ReportArrange(FrameworkElement view, Size finalSize, object page, Visual pageVisual, FrameworkElement host)
        {
            if (!Gate("DPV.Arrange")) { return; }
            Emit("[DPV] site=ArrangeOverride id=" + Id(view) + " final=" + S(finalSize)
                 + " page=" + Id(page) + " pv=" + Id(pageVisual)
                 + " pvKids=" + Kids(pageVisual) + " pvBounds=" + Bounds(pageVisual)
                 + " host=" + Id(host) + " hostKids=" + Kids(host)
                 + " viewPS=" + PS(view) + " pvPS=" + PS(pageVisual)
                 + " NOINFO=dpv-arrange-readonly");
        }

        internal static void ReportArranged(FrameworkElement view, FrameworkElement host, object page)
        {
            if (!Gate("DPV.Arranged")) { return; }
            Emit("[DPV] site=HostArranged view=" + Id(view)
                 + " host=" + Id(host)
                 + " hostRender=" + ((host == null) ? "null" : S(host.RenderSize))
                 + " hostAt=" + Pt(host, view)
                 + " hostOff=" + Off(host) + " hostXf=" + Xf(host)
                 + " hostKids=" + Kids(host)
                 + " page=" + Id(page)
                 + " NOINFO=dpv-hostarranged-readonly");
        }

        internal static void ReportArrangeEnd(FrameworkElement view, Size finalSize, FrameworkElement host, object page)
        {
            if (!Gate("DPV.ArrangeEnd")) { return; }
            Emit("[DPV] site=ArrangeEnd id=" + Id(view) + " final=" + S(finalSize)
                 + " render=" + S(view.RenderSize)
                 + " host=" + Id(host)
                 + " hostRender=" + ((host == null) ? "null" : S(host.RenderSize))
                 + " page=" + Id(page)
                 + " NOINFO=dpv-arrangeend-readonly");
        }

        // ── `T-B19`：`PageVisual` setter 的**每一次调用**逐条（独立 env，不共享 `Gate` 的 trace 预算）──
        //  【要回答的问题】`tab3` 稳态呈现里页子树**不在** milcore 树里；`[DPH] site=Attach` 只在
        //    `Gate`（TraceMax=600）里各 1 条 ⇒ "setter 被调了几次、旧值被清掉后有没有再挂回来"
        //    没有直读面。本节把它变成**逐条**读数。只读；`WPF_DPH_SET_PROBE=1` 才开（缺省零输出）。
        private static int _setProbeOn = -1;
        internal static bool SetProbeOn2
        {
            get
            {
                if (_setProbeOn < 0)
                {
                    string s = null;
                    try { s = System.Environment.GetEnvironmentVariable("WPF_DPH_SET_PROBE"); }
                    catch (System.Exception) { s = null; }
                    _setProbeOn = (s == "1") ? 1 : 0;
                }
                return _setProbeOn == 1;
            }
        }

        private static long _setSeqNo;

        internal static void ReportHostSetSeq(Visual host, Visual oldVal, Visual newVal)
        {
            if (!SetProbeOn2) { return; }
            try
            {
                string shell = (oldVal == null) ? "null" : ParentId(oldVal);
                Emit("[DPHSET] seq=" + (++_setSeqNo) + " host=" + Id(host)
                     + " old=" + Id(oldVal) + " new=" + Id(newVal)
                     + " same=" + (ReferenceEquals(oldVal, newVal) ? 1 : 0)
                     + " oldParent=" + shell
                     + " oldKids=" + Kids(oldVal) + " oldBounds=" + Bounds(oldVal)
                     + " newKids=" + Kids(newVal) + " newBounds=" + Bounds(newVal)
                     + " oldPS=" + PS(oldVal) + " newPS=" + PS(newVal)
                     + " NOINFO=dphsetseq-readonly");
            }
            catch (System.Exception) { }
        }

        // ── `T-B19`：把 `T-B12` 的"在屏页被搬空"修复挪到**渲染遍历入口**（见 `DPV_VISIT_REPL` 的说明）──
        //  `DocumentPageView.GetVisualChild` 是渲染/命中遍历枚举页宿主的**唯一出口**；在那里把
        //  "本页自己的段落/浮动视觉"接回本页（只在容器**确已空**时动手；闸沿用 `WPF_PAGEPAGE_REDRIVE`）。
        //  只读之外**只做托管侧换父**；任何失败**不重抛**（它是可选修复路径，不许盖掉页面自身显示）。
        internal static void RedrivePageVisuals(object page)
        {
            if (page == null) { return; }
            try
            {
                MS.Internal.PtsHost.FlowDocumentPage fdp = page as MS.Internal.PtsHost.FlowDocumentPage;
                if (fdp == null) { return; }
                fdp.RedrivePageVisualsOnly();
            }
            catch (System.Exception) { }
        }

        internal static void ReportHostSet(Visual host, Visual pageVisual, Visual newValue)
        {
            if (!Gate("DPH.Set")) { return; }
            Emit("[DPH] site=PageVisual.set host=" + Id(host)
                 + " newValue=" + Id(newValue) + " pv=" + Id(pageVisual)
                 + " pvParent=" + ParentId(pageVisual)
                 + " pvKids=" + Kids(pageVisual) + " pvBounds=" + Bounds(pageVisual)
                 + " NOINFO=dph-pageset-readonly");
        }

        internal static string Content(Visual v)
        {
            if (v == null) { return "null"; }
            try
            {
                Rect r = VisualTreeHelper.GetContentBounds(v);
                if (r.IsEmpty) { return "empty"; }
                return N(r.X) + "," + N(r.Y) + "," + N(r.Width) + "," + N(r.Height);
            }
            catch (System.Exception) { return "NA"; }
        }

        /// <summary>
        /// 只读：**该视觉是否连在某个 `PresentationSource`（＝成帧根）上**。`null` ＝ 它所在的视觉树
        /// **没有**接到任何呈现源（也就是说它**不进成帧**——不论树内尺寸算得多对）。
        /// </summary>
        internal static string PS(Visual v)
        {
            if (v == null) { return "null"; }
            try
            {
                PresentationSource ps = PresentationSource.FromVisual(v);
                return Id(ps);
            }
            catch (System.Exception) { return "NA"; }
        }

        /// <summary>只读：到视觉树根的**父链深度**（`-1` ＝ 走不动/异常）。</summary>
        internal static int Depth(Visual v)
        {
            if (v == null) { return -1; }
            try
            {
                int d = 0;
                Visual cur = v;
                while (cur != null)
                {
                    cur = VisualTreeHelper.GetParent(cur) as Visual;
                    d++;
                    if (d > 4096) { break; }
                }
                return d;
            }
            catch (System.Exception) { return -2; }
        }

        /// <summary>
        /// 只读：**该视觉到呈现源根视觉的完整变换矩阵**（`M11,M12,M21,M22,OX,OY`）。
        /// 它把"树里尺寸算得对不对"与"**画到屏上落在哪、多大**"两件事分开：矩阵若把页面映射到
        /// 零面积/界外/非有限值，那"内容已建"与"上不了屏"就同时成立且**可证**。
        /// </summary>
        internal static string Xf(Visual v)
        {
            if (v == null) { return "null"; }
            try
            {
                PresentationSource ps = PresentationSource.FromVisual(v);
                if (ps == null) { return "nops"; }
                Visual root = ps.RootVisual;
                if (root == null) { return "noroot"; }
                GeneralTransform gt = v.TransformToAncestor(root);
                if (gt == null) { return "noxf"; }
                Rect rb = gt.TransformBounds(new Rect(0, 0, 1, 1));
                return N(rb.X) + "," + N(rb.Y) + "," + N(rb.Width) + "," + N(rb.Height);
            }
            catch (System.Exception) { return "NA"; }
        }

        internal static void ReportAttach(Visual host, Visual pageVisual, bool direct)
        {
            if (!Gate("DPH.Attach")) { return; }
            Emit("[DPH] site=Attach host=" + Id(host) + " pv=" + Id(pageVisual)
                 + " mode=" + (direct ? "direct" : "container")
                 + " pvParent=" + ParentId(pageVisual)
                 + " pvContent=" + Content(pageVisual)
                 + " pvKids=" + Kids(pageVisual)
                 + " pvBound=" + Bounds(pageVisual)
                 + " hostPS=" + PS(host) + " pvPS=" + PS(pageVisual) + " pvDepth=" + Depth(pageVisual)
                 + " hostOff=" + Off(host) + " pvXf=" + Xf(pageVisual)
                 + " pvChild=" + ChildInfo(pageVisual)
                 + " SUB=" + Sub(pageVisual, 3)
                 + " TYPES=" + Types(pageVisual, 5)
                 + " NOINFO=dph-attach-readonly");
        }

        /// <summary>
        /// 只读：`FlowDocumentView`（**画得出**的那条链）的同位对照 —— 页视觉挂在谁身上、
        /// 有没有连到呈现源、子树深度多少。使"两条链的差"有**同口径**读数。
        /// </summary>
        internal static void ReportFdv(FrameworkElement view, Visual pageVisual)
        {
            if (!Gate("FDV.Attach")) { return; }
            Emit("[FDV] site=Attach view=" + Id(view) + " pv=" + Id(pageVisual)
                 + " pvParent=" + ParentId(pageVisual)
                 + " viewPS=" + PS(view) + " pvPS=" + PS(pageVisual) + " pvDepth=" + Depth(pageVisual)
                 + " pvContent=" + Content(pageVisual) + " pvKids=" + Kids(pageVisual)
                 + " pvXf=" + Xf(pageVisual)
                 + " pvChild=" + ChildInfo(pageVisual)
                 + " SUB=" + Sub(pageVisual, 3)
                 + " TYPES=" + Types(pageVisual, 5)
                 + " NOINFO=fdv-attach-readonly");
        }

        /// <summary>
        /// 只读：**渲染遍历（render walk）到底走到了哪一格**。`GetVisualChild` 只由视觉枚举方调用
        /// （渲染／命中测试／变换）⇒ "DPV 有没有被走到"、"DPV→DPH 有没有被走到"这两问因此**有直读面**。
        /// </summary>
        private static int _subOnce;

        /// <summary>只读：**渲染遍历期间**页视觉子树的前若干次逐层读数（确认"壳之内是否真空"）。</summary>
        internal static void ReportRenderSub(FrameworkElement self, Visual pageVisual)
        {
            if (ProbeOn == false) { return; }
            if (_subOnce >= 3) { return; }
            _subOnce++;
            Emit("[DPH] site=RenderSub self=" + Id(self) + " pv=" + Id(pageVisual)
                 + " render=" + S(self.RenderSize)
                 + " SUB=" + Sub(pageVisual, 3)
                 + " NOINFO=dph-rendersub-readonly");
        }

        internal static void ReportPaginator(FrameworkElement view, object paginator)
        {
            if (!Gate("DPV.SetPaginator")) { return; }
            Emit("[DPV] site=SetPaginator id=" + Id(view) + " pag=" + Id(paginator)
                 + " NOINFO=dpv-setpaginator-readonly");
        }

        internal static void ReportCtor(FrameworkElement view)
        {
            if (!Gate("DPV.Ctor")) { return; }
            Emit("[DPV] site=Ctor id=" + Id(view) + " NOINFO=dpv-ctor-readonly");
        }

        /// <summary>
        /// `T-B13`：`DocumentViewerBase.GetPageViewsCollection` 的**只读**读数 —— 直接回答
        /// "这个查看器**有没有页宿主**（`DocumentPageView`）"（＝ tab3 断点的**判决面**）。
        /// </summary>
        internal static void ReportPageViews(object viewer, int n)
        {
            if (!ProbeOn) { return; }
            Emit("[DVBI] site=GetPageViews self=" + Id(viewer) + " selfType=" + TypeName(viewer)
                 + " n=" + n.ToString(System.Globalization.CultureInfo.InvariantCulture)
                 + " NOINFO=dvbi-readonly");
        }

        internal static string TypeName(object o)
        {
            if (o == null) { return "null"; }
            try { return o.GetType().Name; }
            catch (System.Exception) { return "NA"; }
        }

        internal static void ReportVisit(string site, FrameworkElement self, Visual child, int index)
        {
            // `T-B13`：渲染遍历读数**按元素各记一份**（不再吃 `TraceMax` 的共享额度）
            if (!ProbeOn) { return; }
            int vkey;
            try { vkey = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(self); }
            catch (System.Exception) { vkey = 0; }
            int vc;
            _visitPerSelf.TryGetValue(vkey, out vc);
            if (vc >= VisitPerSelfMax) { return; }
            _visitPerSelf[vkey] = vc + 1;
            string childRender = "NA";
            try
            {
                FrameworkElement fe = child as FrameworkElement;
                if (fe != null) { childRender = S(fe.RenderSize); }
                else if (child != null) { childRender = "vis"; }
                else { childRender = "null"; }
            }
            catch (System.Exception) { childRender = "NA"; }
            Emit("[PAGEVIEW] site=" + site + " self=" + Id(self) + " idx=" + index
                 + " selfRender=" + S(self.RenderSize) + " selfVis=" + (self.Visibility == Visibility.Visible ? 1 : 0)
                 + " child=" + Id(child) + " childRender=" + childRender
                 + " NOINFO=renderwalk-readonly");
        }
    }
}



