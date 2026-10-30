// ⚠️ 本文件由 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/reapply-patches.py **生成**，不要手改。
//
// 内容 = 上游 `src/Microsoft.DotNet.Wpf/src/PresentationFramework/MS/Internal/documents/FlowDocumentView.cs` 逐字复制 + 11 处 W86A（`TASK-0304`/`TASK-0305`）改动。
// 每次运行该脚本都会从上游重读重生成；needle 找不到 / 命中数不符时**报错退出**
// （不会静默产出未打补丁的副本）。改动逐处见：
//   E1 ／ E2 ／ E3 ／ E4 ／ E5 ／ E6 ／ E7 ／ E8 ／ E9 ／ E10 ／ E11
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
// Description: Provides a view port for content of FlowDocument formatted
//              bottomless area.
//

using System.Globalization;                 // CultureInfo（W86A A3 的占位文字）
using System.Windows;                       // Size
using System.Windows.Controls;              // ScrollViewer
using System.Windows.Controls.Primitives;   // IScrollInfo
using System.Windows.Documents;             // FlowDocument
using System.Windows.Media;                 // Visual
using MS.Internal.PtsHost;                  // FlowDocumentPage
using MS.Internal.Text;                     // TextDpi

namespace MS.Internal.Documents
{
    /// <summary>
    /// Provides a view port for content of FlowDocument formatted bottomless area.
    /// </summary>
    internal class FlowDocumentView : FrameworkElement, IScrollInfo, IServiceProvider
    {
        //-------------------------------------------------------------------
        //
        //  Constructors
        //
        //-------------------------------------------------------------------

        #region Constructors

        /// <summary>
        /// Static Constructor
        /// </summary>
        static FlowDocumentView()
        {
        }

        /// <summary>
        /// Constructor
        /// </summary>
        // W86A `A3`：页级占位尺寸（Measure 与 OnRender 共用同一个值）。
        private static readonly Size PtsGapPlaceholderSize = new Size(440.0, 132.0);

        internal FlowDocumentView()
        {
        }

        #endregion Constructors

        //-------------------------------------------------------------------
        //
        //  Protected Methods
        //
        //-------------------------------------------------------------------

        #region Protected Methods

        /// <summary>
        /// Content measurement.
        /// </summary>
        /// <param name="constraint">Constraint size.</param>
        /// <returns>Computed desired size.</returns>
        protected sealed override Size MeasureOverride(Size constraint)
        {
            Size desiredSize = new Size();

            if (_suspendLayout)
            {
                desiredSize = this.DesiredSize;
            }
            else if (Document != null)
            {
                // ── W86A `A3`（`D-G70`）：PTS 不可用 ⇒ **页级具名占位** ──────────────────
                //   ① 已判定过 ⇒ 直接给占位尺寸，**不再进 formatter**（幂等：一次失败只降级一次
                //      ⇒ 不会每次 Measure 都抛一次，也不会重入布局）；
                //   ② 第一次 ⇒ 只接 `PtsUnavailableException` 这**一个具名条件**
                //      （**不是** catch-all：别的异常原样向上传播）。
                if (_ptsUnavailable != null)
                {
                    return PtsGapPlaceholderSize;
                }

                // ⚠️ `EnsureFormatter()` **必须在 try 之内**：PTS 上下文是它**间接**建的
                //   （`_document.BottomlessFormatter` → `FlowDocumentFormatter..ctor`
                //     → `FlowDocumentPage..ctor` → `StructuralCache.Section` → `PtsContext..ctor`
                //     → `PtsCache.AcquireContext`），第一跳根本不在 `Format()` 里。
                //   【本车道实测踩到】W86A 第一版把 try 只罩住 `Format()` ⇒ 异常从
                //   `EnsureFormatter()` 逃出去（实测栈见 W86A 报告 §3），占位一次都没画。
                try
                {
                    // Create bottomless formatter, if necessary.
                    EnsureFormatter();

                    // Format bottomless content.
                    _formatter.Format(constraint);
                }
                catch (PtsUnavailableException ptsGap)
                {
                    _ptsUnavailable = ptsGap;
                    WpfLinuxPtsGapTrace.Report("FlowDocumentView.MeasureOverride", ptsGap);
                    InvalidateVisual();              // ⇒ 走 OnRender 的占位分支（非零墨，不是空白）
                    return PtsGapPlaceholderSize;
                }

                // DesiredSize is set to the calculated size of the page.
                // If hosted by ScrollViewer, desired size is limited to constraint.
                if (_scrollData != null)
                {
                    desiredSize.Width = Math.Min(constraint.Width, _formatter.DocumentPage.Size.Width);
                    desiredSize.Height = Math.Min(constraint.Height, _formatter.DocumentPage.Size.Height);
                }
                else
                {
                    desiredSize = _formatter.DocumentPage.Size;
                }
            }
            return desiredSize;
        }

        /// <summary>
        /// Content arrangement.
        /// </summary>
        /// <param name="arrangeSize">Size that element should use to arrange itself and its children.</param>
        protected sealed override Size ArrangeOverride(Size arrangeSize)
        {
            Rect viewport = Rect.Empty;
            Vector offset;
            bool invalidateScrollInfo = false;
            Size safeArrangeSize = arrangeSize;

            // W86A `A3`：PTS 不可用 ⇒ 只摆占位（`_formatter.DocumentPage` 无效，**一个字段都不许碰**）
            if (_ptsUnavailable != null)
            {
                return arrangeSize;
            }

            if (!_suspendLayout)
            {
                // Convert to TextDpi and convert back to double, to make sure that we are not
                // getting rounding errors later.
                TextDpi.SnapToTextDpi(ref safeArrangeSize);

                if (Document != null)
                {
                    // Create bottomless formatter, if necessary.
                    // W86A `A3`：`ArrangeOverride` 与 `MeasureOverride` **同一条第一跳**
                    // （同样的 `EnsureFormatter()`）⇒ 也必须接住，否则在"先摆后量"的顺序下照样逃逸。
                    try
                    {
                        EnsureFormatter();
                    }
                    catch (PtsUnavailableException ptsGap)
                    {
                        _ptsUnavailable = ptsGap;
                        WpfLinuxPtsGapTrace.Report("FlowDocumentView.ArrangeOverride", ptsGap);
                        InvalidateVisual();
                        return arrangeSize;
                    }

                    // Arrange bottomless content.
                    if (_scrollData != null)
                    {
                        if (!DoubleUtil.AreClose(_scrollData.Viewport, safeArrangeSize))
                        {
                            _scrollData.Viewport = safeArrangeSize;
                            invalidateScrollInfo = true;
                        }

                        if (!DoubleUtil.AreClose(_scrollData.Extent, _formatter.DocumentPage.Size))
                        {
                            _scrollData.Extent = _formatter.DocumentPage.Size;
                            invalidateScrollInfo = true;
                            // DocumentPage Size is calculated by converting double to int and then back to double.
                            // This conversion may produce rounding errors and force us to show scrollbars in cases
                            // when extent is within 1px from viewport. To workaround this issue, snap extent to viewport
                            // if we are within 1px range.
                            if (Math.Abs(_scrollData.ExtentWidth - _scrollData.ViewportWidth) < 1)
                            {
                                _scrollData.ExtentWidth = _scrollData.ViewportWidth;
                            }
                            if (Math.Abs(_scrollData.ExtentHeight - _scrollData.ViewportHeight) < 1)
                            {
                                _scrollData.ExtentHeight = _scrollData.ViewportHeight;
                            }
                        }
                        offset = new Vector(
                            Math.Max(0, Math.Min(_scrollData.ExtentWidth - _scrollData.ViewportWidth, _scrollData.HorizontalOffset)),
                            Math.Max(0, Math.Min(_scrollData.ExtentHeight - _scrollData.ViewportHeight, _scrollData.VerticalOffset)));
                        if (!DoubleUtil.AreClose(offset, _scrollData.Offset))
                        {
                            _scrollData.Offset = offset;
                            invalidateScrollInfo = true;
                        }
                        if (invalidateScrollInfo && _scrollData.ScrollOwner != null)
                        {
                            _scrollData.ScrollOwner.InvalidateScrollInfo();
                        }
                        viewport = new Rect(_scrollData.HorizontalOffset, _scrollData.VerticalOffset, safeArrangeSize.Width, safeArrangeSize.Height);
                    }
                    // ── `T-A41`（`HOSTED-FSVIEW-VIEWPORT-DRIVE`）：**本视图发起视口驱动** ──────────────
                    //  病情（现取）：`FlowDocumentFormatter.Arrange:130` 之后的整条视口链
                    //  （页轨枚举 → 宿主段 → 附属对象 → 子页轨 → 容器 → 内容段
                    //   `UpdateViewportSimpleLines:3359`）里，**内容段的行视图从未被造出**
                    //  （`[FSQLL] cLines=1` 恒 0）。本视图是**唯一**把 viewport 交给 formatter 的收口。
                    //  驱动 ＝ **视口 ∪ 整页**（空/非有限 ⇒ 直接用整页），**只动这一个入参**：
                    //    · 不碰 native 几何；· 不改 `fsupdinf`／`fUpdateInfoForLinesPresent`；
                    //    · 不删不放宽任何 `Invariant.Assert`。
                    //  `WPF_FSVIEW_VIEWPORT_DRIVE=0` ⇒ 逐字回上游行为（反极性腿；缺省＝开）。
                    Rect fsviewViewport = WpfLinuxFsViewDrive.Effective(viewport, safeArrangeSize, _formatter, _pageVisual);
                    WpfLinuxFsViewDrive.Report("ArrangeOverride", Document != null, _suspendLayout,
                                               _scrollData != null, safeArrangeSize, viewport, fsviewViewport,
                                               _formatter, _pageVisual);
                    try
                    {
                        _formatter.Arrange(safeArrangeSize, fsviewViewport);
                    }
                    catch (System.Exception fsviewEx)
                    {
                        WpfLinuxFsViewDrive.ReportException("ArrangeOverride.Arrange", fsviewEx);
                        throw;
                    }

                    // Connect to visual tree.
                    if (_pageVisual != _formatter.DocumentPage.Visual)
                    {
                        _textView?.OnPageConnected();
                        if (_pageVisual != null)
                        {
                            RemoveVisualChild(_pageVisual);
                        }
                        _pageVisual = (PageVisual)_formatter.DocumentPage.Visual;
                        AddVisualChild(_pageVisual);
                        // `T-B11`：让"画得出的那条链"与 `DPV`／`DPH` 有**同口径**读数（只读，不改语义）
                        WpfLinuxPageViewProbe.ReportFdv(this, _pageVisual);
                    }

                    // Set appropriate content offset
                    if (_scrollData != null)
                    {
                        _pageVisual.Offset = new Vector(-_scrollData.HorizontalOffset, -_scrollData.VerticalOffset);
                    }

                    // DocumentPage.Visual is always returned in LeftToRight FlowDirection. 
                    // Hence, if the the current FlowDirection is RightToLeft, 
                    // mirroring transform need to be applied to the content.
                    PtsHelper.UpdateMirroringTransform(FlowDirection, FlowDirection.LeftToRight, _pageVisual, safeArrangeSize.Width);
                }
                else
                {
                    if (_pageVisual != null)
                    {
                        _textView?.OnPageDisconnected();
                        RemoveVisualChild(_pageVisual);
                        _pageVisual = null;
                    }
                    // Arrange bottomless content.
                    if (_scrollData != null)
                    {
                        if (!DoubleUtil.AreClose(_scrollData.Viewport, safeArrangeSize))
                        {
                            _scrollData.Viewport = safeArrangeSize;
                            invalidateScrollInfo = true;
                        }
                        if (!DoubleUtil.AreClose(_scrollData.Extent, new Size()))
                        {
                            _scrollData.Extent = new Size();
                            invalidateScrollInfo = true;
                        }
                        if (!DoubleUtil.AreClose(_scrollData.Offset, new Vector()))
                        {
                            _scrollData.Offset = new Vector();
                            invalidateScrollInfo = true;
                        }
                        if (invalidateScrollInfo && _scrollData.ScrollOwner != null)
                        {
                            _scrollData.ScrollOwner.InvalidateScrollInfo();
                        }
                    }
                }
            }
            return arrangeSize;
        }

        /// <summary>
        /// Returns visual child at specified index. FlowDocumentView has just one child.
        /// </summary>
        protected override Visual GetVisualChild(int index)
        {
            if (index != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, SR.Visual_ArgumentOutOfRange);
            }
            return _pageVisual;
        }

        /// <summary>
        /// W86A `A3`（`D-G70`）：PTS 不可用时的**页级具名占位** —— 洋红矩形 + 黑边框 + 三行自述文字。
        ///
        /// 【判据读的就是这里的三件事】① **非零墨**：洋红像素数 &gt; 0（"空白"与"已降级"因此**可机读区分**）；
        /// ② **具名**：页面上直接写着"不支持"与缺口入口名（人眼可读，不必翻日志）；
        /// ③ **零墨差**：`_ptsUnavailable == null`（也就是**所有正常页**）时本方法**立即返回**
        ///    ⇒ 正常页的像素**一个都不变**（这是"零回归"能被机器证明的那一半）。
        ///
        /// ⚠️ 这不是"渲染出了文档"：它画的是"**我渲染不出来，原因是这个**"。
        /// </summary>
        protected override void OnRender(DrawingContext drawingContext)
        {
            if (_ptsUnavailable == null) return;      // 正常路径：零墨差（不许改变任何正常页）
            if (drawingContext == null) return;

            Rect box = new Rect(0, 0, PtsGapPlaceholderSize.Width, PtsGapPlaceholderSize.Height);
            // 洋红（`Brushes.Magenta` = #FFFF00FF）是本移植里"能力缺口占位"的保留色。
            drawingContext.DrawRectangle(Brushes.Magenta, new Pen(Brushes.Black, 2.0), box);

            string[] lines = new string[]
            {
                "此页不支持：PTS / 原生 LineServices 未实现（D-G70 / D-G78）",
                "NOT SUPPORTED on this port: PTS is not implemented.",
                "entry=" + (_ptsUnavailable.Entry ?? "unknown")
                    + "  err=" + _ptsUnavailable.ErrorCode.ToString(CultureInfo.InvariantCulture),
            };
            double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            double y = 8;
            for (int i = 0; i < lines.Length; i++)
            {
                FormattedText ft = new FormattedText(
                    lines[i],
                    CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    new Typeface("DejaVu Sans"),
                    i == 0 ? 14.0 : 12.0,
                    Brushes.Black,
                    dip);
                ft.MaxTextWidth = Math.Max(1.0, PtsGapPlaceholderSize.Width - 20.0);
                drawingContext.DrawText(ft, new Point(10.0, y));
                y += ft.Height + 4.0;
            }
        }

        #endregion Protected Methods

        //-------------------------------------------------------------------
        //
        //  Protected Properties
        //
        //-------------------------------------------------------------------

        #region Protected Properties

        /// <summary>
        /// Returns number of children. FlowDocumentView has just one child.
        /// </summary>        
        protected override int VisualChildrenCount
        {
            get
            {
                return _pageVisual == null ? 0 : 1;
            }
        }

        #endregion Protected Properties

        //-------------------------------------------------------------------
        //
        //  Internal Methods
        //
        //-------------------------------------------------------------------

        #region Internal Methods

        /// <summary>
        /// Suspends page layout.
        /// </summary>
        internal void SuspendLayout()
        {
            _suspendLayout = true;
            _pageVisual?.Opacity = 0.5;
        }

        /// <summary>
        /// Resumes page layout.
        /// </summary>
        internal void ResumeLayout()
        {
            _suspendLayout = false;
            _pageVisual?.Opacity = 1.0;
            InvalidateMeasure();
        }

        #endregion Internal Methods

        //-------------------------------------------------------------------
        //
        //  Internal Properties
        //
        //-------------------------------------------------------------------

        #region Internal Properties

        /// <summary>
        /// BreakRecordTable.
        /// </summary>
        internal FlowDocument Document
        {
            get
            {
                return _document;
            }
            set
            {
                if (_formatter != null)
                {
                    HandleFormatterSuspended(_formatter, EventArgs.Empty);
                }
                _suspendLayout = false;
                _textView = null;
                _document = value;
                InvalidateMeasure();
                InvalidateVisual(); //ensure re-rendering
            }
        }

        /// <summary>
        /// DocumentPage.
        /// </summary>
        internal FlowDocumentPage DocumentPage
        {
            get
            {
                if (_document != null)
                {
                    // W86A `A3`：这是**第三个**会撞 PTS 的入口（`DocumentPageTextView..ctor`
                    // 走它：`_page = owner.DocumentPage`），而它的第一跳同样是 `EnsureFormatter()`。
                    // 接住并**返回 null**（那个 ctor 只做 `if (_page is IServiceProvider)` ⇒ null 安全）
                    // —— 目的是"**别让具名异常从这里逃出去**"，而不是"假装有页面"。
                    if (_ptsUnavailable != null) return null;
                    try
                    {
                        EnsureFormatter();
                        return _formatter.DocumentPage;
                    }
                    catch (PtsUnavailableException ptsGap)
                    {
                        _ptsUnavailable = ptsGap;
                        WpfLinuxPtsGapTrace.Report("FlowDocumentView.DocumentPage", ptsGap);
                        InvalidateVisual();
                        return null;
                    }
                }
                return null;
            }
        }

        #endregion Internal Properties

        //-------------------------------------------------------------------
        //
        //  Private Methods
        //
        //-------------------------------------------------------------------

        #region Private Methods

        /// <summary>
        /// Ensures valid instance of FlowDocumentFormatter.
        /// </summary>
        private void EnsureFormatter()
        {
            Invariant.Assert(_document != null);
            if (_formatter == null)
            {
                _formatter = _document.BottomlessFormatter;
                _formatter.ContentInvalidated += new EventHandler(HandleContentInvalidated);
                _formatter.Suspended += new EventHandler(HandleFormatterSuspended);
            }
            Invariant.Assert(_formatter == _document.BottomlessFormatter);
        }

        /// <summary>
        /// Responds to content invalidation.
        /// </summary>
        private void HandleContentInvalidated(object sender, EventArgs e)
        {
            Invariant.Assert(sender == _formatter);
            InvalidateMeasure();
            InvalidateVisual(); //ensure re-rendering
        }

        /// <summary>
        /// Responds to formatter suspention.
        /// </summary>
        private void HandleFormatterSuspended(object sender, EventArgs e)
        {
            Invariant.Assert(sender == _formatter);

            // Disconnect formatter.
            _formatter.ContentInvalidated -= new EventHandler(HandleContentInvalidated);
            _formatter.Suspended -= new EventHandler(HandleFormatterSuspended);
            _formatter = null;

            // Disconnect any content associated with the formatter.
            if (_pageVisual != null && !_suspendLayout)
            {
                _textView?.OnPageDisconnected();
                RemoveVisualChild(_pageVisual);
                _pageVisual = null;
            }
        }

        #endregion Private Methods

        //-------------------------------------------------------------------
        //
        //  Private Fields
        //
        //-------------------------------------------------------------------

        #region Private Fields

        private FlowDocument _document;             // Hosted FlowDocument
        private PageVisual _pageVisual;             // Visual representing the content
        private FlowDocumentFormatter _formatter;   // Bottomless formatter associated with FlowDocument
        private ScrollData _scrollData;             // IScrollInfo related data, if hosted by ScrollViewer
        private DocumentPageTextView _textView;     // TextView associated with this element.
        private bool _suspendLayout;                // Layout of the page is suspended.

        // W86A `A3`：null = PTS 路径正常（**正常路径的唯一取值**）；非 null = 本视图已降级为具名占位。
        // ⚠️ 它是**视图级**的幂等闩（与 `PtsCache` 的进程级闩各管一段）：进程级闩保证"不再进 native"，
        //    视图级闩保证"不再进 formatter、只画出占位一次"。
        private PtsUnavailableException _ptsUnavailable;

        #endregion Private Fields

        //-------------------------------------------------------------------
        //
        //  IScrollInfo Members
        //
        //-------------------------------------------------------------------

        #region IScrollInfo Members

        /// <summary>
        /// <see cref="IScrollInfo.LineUp"/>
        /// </summary>
        void IScrollInfo.LineUp()
        {
            _scrollData?.LineUp(this);
        }

        /// <summary>
        /// <see cref="IScrollInfo.LineDown"/>
        /// </summary>
        void IScrollInfo.LineDown()
        {
            _scrollData?.LineDown(this);
        }

        /// <summary>
        /// <see cref="IScrollInfo.LineLeft"/>
        /// </summary>
        void IScrollInfo.LineLeft()
        {
            _scrollData?.LineLeft(this);
        }

        /// <summary>
        /// <see cref="IScrollInfo.LineRight"/>
        /// </summary>
        void IScrollInfo.LineRight()
        {
            _scrollData?.LineRight(this);
        }

        /// <summary>
        /// <see cref="IScrollInfo.PageUp"/>
        /// </summary>
        void IScrollInfo.PageUp()
        {
            _scrollData?.PageUp(this);
        }

        /// <summary>
        /// <see cref="IScrollInfo.PageDown"/>
        /// </summary>
        void IScrollInfo.PageDown()
        {
            _scrollData?.PageDown(this);
        }

        /// <summary>
        /// <see cref="IScrollInfo.PageLeft"/>
        /// </summary>
        void IScrollInfo.PageLeft()
        {
            _scrollData?.PageLeft(this);
        }

        /// <summary>
        /// <see cref="IScrollInfo.PageRight"/>
        /// </summary>
        void IScrollInfo.PageRight()
        {
            _scrollData?.PageRight(this);
        }

        /// <summary>
        /// <see cref="IScrollInfo.MouseWheelUp"/>
        /// </summary>
        void IScrollInfo.MouseWheelUp()
        {
            _scrollData?.MouseWheelUp(this);
        }

        /// <summary>
        /// <see cref="IScrollInfo.MouseWheelDown"/>
        /// </summary>
        void IScrollInfo.MouseWheelDown()
        {
            _scrollData?.MouseWheelDown(this);
        }

        /// <summary>
        /// <see cref="IScrollInfo.MouseWheelLeft"/>
        /// </summary>
        void IScrollInfo.MouseWheelLeft()
        {
            _scrollData?.MouseWheelLeft(this);
        }

        /// <summary>
        /// <see cref="IScrollInfo.MouseWheelRight"/>
        /// </summary>
        void IScrollInfo.MouseWheelRight()
        {
            _scrollData?.MouseWheelRight(this);
        }

        /// <summary>
        /// <see cref="IScrollInfo.SetHorizontalOffset"/>
        /// </summary>
        void IScrollInfo.SetHorizontalOffset(double offset)
        {
            _scrollData?.SetHorizontalOffset(this, offset);
        }

        /// <summary>
        /// <see cref="IScrollInfo.SetVerticalOffset"/>
        /// </summary>
        void IScrollInfo.SetVerticalOffset(double offset)
        {
            _scrollData?.SetVerticalOffset(this, offset);
        }

        /// <summary>
        /// <see cref="IScrollInfo.MakeVisible"/>
        /// </summary>
        Rect IScrollInfo.MakeVisible(Visual visual, Rect rectangle)
        {
            if (_scrollData == null)
            {
                rectangle = Rect.Empty;
            }
            else
            {
                rectangle = _scrollData.MakeVisible(this, visual, rectangle);
            }

            return rectangle;
        }

        /// <summary>
        /// <see cref="IScrollInfo.CanVerticallyScroll"/>
        /// </summary>
        bool IScrollInfo.CanVerticallyScroll
        {
            get
            {
                return (_scrollData != null) ? _scrollData.CanVerticallyScroll : false;
            }
            set
            {
                _scrollData?.CanVerticallyScroll = value;
            }
        }

        /// <summary>
        /// <see cref="IScrollInfo.CanHorizontallyScroll"/>
        /// </summary>
        bool IScrollInfo.CanHorizontallyScroll
        {
            get
            {
                return (_scrollData != null) ? _scrollData.CanHorizontallyScroll : false;
            }
            set
            {
                _scrollData?.CanHorizontallyScroll = value;
            }
        }

        /// <summary>
        /// <see cref="IScrollInfo.ExtentWidth"/>
        /// </summary>
        double IScrollInfo.ExtentWidth
        {
            get
            {
                return (_scrollData != null) ? _scrollData.ExtentWidth : 0;
            }
        }

        /// <summary>
        /// <see cref="IScrollInfo.ExtentHeight"/>
        /// </summary>
        double IScrollInfo.ExtentHeight
        {
            get
            {
                return (_scrollData != null) ? _scrollData.ExtentHeight : 0;
            }
        }

        /// <summary>
        /// <see cref="IScrollInfo.ViewportWidth"/>
        /// </summary>
        double IScrollInfo.ViewportWidth
        {
            get
            {
                return (_scrollData != null) ? _scrollData.ViewportWidth : 0;
            }
        }

        /// <summary>
        /// <see cref="IScrollInfo.ViewportHeight"/>
        /// </summary>
        double IScrollInfo.ViewportHeight
        {
            get
            {
                return (_scrollData != null) ? _scrollData.ViewportHeight : 0;
            }
        }

        /// <summary>
        /// <see cref="IScrollInfo.HorizontalOffset"/>
        /// </summary>
        double IScrollInfo.HorizontalOffset
        {
            get
            {
                return (_scrollData != null) ? _scrollData.HorizontalOffset : 0;
            }
        }

        /// <summary>
        /// <see cref="IScrollInfo.VerticalOffset"/>
        /// </summary>
        double IScrollInfo.VerticalOffset
        {
            get
            {
                return (_scrollData != null) ? _scrollData.VerticalOffset : 0;
            }
        }

        /// <summary>
        /// <see cref="IScrollInfo.ScrollOwner"/>
        /// </summary>
        ScrollViewer IScrollInfo.ScrollOwner
        {
            get
            {
                return _scrollData?.ScrollOwner;
            }

            set
            {
                if (_scrollData == null)
                {
                    // Create cached scroll info.
                    _scrollData = new ScrollData();
                }
                _scrollData.SetScrollOwner(this, value);
            }
        }

        #endregion IScrollInfo Members

        //-------------------------------------------------------------------
        //
        //  IServiceProvider Members
        //
        //-------------------------------------------------------------------

        #region IServiceProvider Members

        /// <summary>
        /// Gets the service object of the specified type.
        /// </summary>
        /// <param name="serviceType">
        /// An object that specifies the type of service object to get.
        /// </param>
        /// <returns>
        /// A service object of type serviceType. A null reference if there is no 
        /// service object of type serviceType.
        /// </returns>
        object IServiceProvider.GetService(Type serviceType)
        {
            object service = null;

            if (serviceType == typeof(ITextView))
            {
                if (_textView == null && _document != null)
                {
                    _textView = new DocumentPageTextView(this, _document.StructuralCache.TextContainer);
                }
                service = _textView;
            }
            else if (serviceType == typeof(ITextContainer))
            {
                if (Document != null)
                {
                    service = Document.StructuralCache.TextContainer as TextContainer;
                }
            }

            return service;
        }

        #endregion IServiceProvider Members
    }

    /// <summary>
    /// W86A `T-A41`（`HOSTED-FSVIEW-VIEWPORT-DRIVE`）：**视口驱动 ＋ 只读台账**（`D0` 的同趟托管侧面）。
    ///
    /// 【它是"读数"不是"调参"】`Effective()` 只对**本视图自己交给 formatter 的那一个入参**赋值
    /// （视口＝可见区 ∪ 整页计算尺寸）；它**不接触**任何 native 真值（几何／更新信息／断言都不动）⇒
    /// 下游若仍不造行视图，那是**下游的事实**，不是这里"调"出来的。`Enabled` 读 `WPF_FSVIEW_VIEWPORT_DRIVE`
    /// （缺省开，显式 `0` 关）⇒ 反极性腿可在**同一产物**上把驱动点整个撤掉（逐字回上游入参）。
    /// 【台账】`[FSVIEW]` 每趟 arrange 一行：`doc`／`suspend`／`scroll`／`arrange`／`viewport`／`handed`／`page`
    /// ＋ 尾部具名 `NOINFO=` —— 使 `A40 §6` 的 `NOINFO-FSVIEW-ARRANGE-TRIGGER`（"`ArrangeOverride`
    /// 到底有没有被布局系统调到"）有**直读**面。台账上限 `TraceMax` 行（超出只打一条 `suppressed`）。
    /// </summary>
    internal static class WpfLinuxFsViewDrive
    {
        private const int TraceMax = 400;
        private static int _traceCount;
        private static int _enabled = -1;                 // -1＝未读；0＝关；1＝开

        internal static bool Enabled
        {
            get
            {
                if (_enabled < 0)
                {
                    string s = null;
                    try { s = System.Environment.GetEnvironmentVariable("WPF_FSVIEW_VIEWPORT_DRIVE"); }
                    catch (System.Exception) { s = null; }
                    _enabled = (s == "0") ? 0 : 1;         // 缺省＝开；**只有**显式 "0" 才关
                }
                return _enabled == 1;
            }
        }

        private static bool IsFinite(Rect r)
        {
            return !double.IsNaN(r.X) && !double.IsInfinity(r.X)
                && !double.IsNaN(r.Y) && !double.IsInfinity(r.Y)
                && !double.IsNaN(r.Width) && !double.IsInfinity(r.Width)
                && !double.IsNaN(r.Height) && !double.IsInfinity(r.Height);
        }

        /// <summary>
        /// 交给 formatter 的视口：关 ⇒ **原样**返回上游算出的 `viewport`；开 ⇒ `可见区 ∪ 整页 ∪ 已实现视觉子树`，
        /// 且空/非有限时用后两者（`arrangeSize`／`DocumentPage.Size`／`GetDescendantBounds(pageVisual)`）。
        /// **不得**在这里改任何其它变量（本方法只读这 4 个入参）。
        ///
        /// 【为什么含"已实现视觉子树"这一项】现取（`T-A41` 三腿）：本页的 `DocumentPage.Size` 只有
        /// `39.81x39.17` DIP，**远小于**可见视口 ⇒ 只写"整页"这一项时 `handed == viewport`（驱动成空转）。
        /// 而本侧 native 声明的附属对象盒（`Figure` `(30000,20000,42000,15000)` 文本 dpi ⇒ `9600x6400` DIP）
        /// **远在**该页盒之外 —— 它们**已经**被实现在 `_pageVisual` 子树里（`Figure` 的背景 `GhostWhite`
        /// 现取 `29637 px` 即其证）⇒ 该项是**从已实现视觉树里读出来的真实范围**（不是常量、不是伪造几何）。
        /// </summary>
        internal static Rect Effective(Rect viewport, Size arrangeSize, FlowDocumentFormatter formatter, Visual pageVisual)
        {
            if (!Enabled)
            {
                return viewport;
            }

            Rect pageRect = new Rect(0, 0, arrangeSize.Width, arrangeSize.Height);
            try
            {
                Size cs = formatter.DocumentPage.Size;      // 只读：本页"计算尺寸"
                Rect csRect = new Rect(0, 0, cs.Width, cs.Height);
                if (!csRect.IsEmpty && IsFinite(csRect))
                {
                    pageRect = Rect.Union(pageRect, csRect);
                }
            }
            catch (System.Exception)
            {
                // 读不到尺寸**不许**改变行为（退回 arrangeSize 一档；不影响下面的判据）。
            }

            Rect visRect = VisualBounds(pageVisual);
            if (!visRect.IsEmpty && IsFinite(visRect))
            {
                pageRect = Rect.Union(pageRect, visRect);
            }

            if (viewport.IsEmpty || !IsFinite(viewport) || !IsFinite(pageRect))
            {
                return pageRect;
            }
            Rect union = Rect.Union(viewport, pageRect);
            return (union.IsEmpty || !IsFinite(union)) ? pageRect : union;
        }

        /// <summary>
        /// 只读：已实现视觉子树的范围（`VisualTreeHelper.GetDescendantBounds`）。读不到 ⇒ `Rect.Empty`
        /// （**绝不当 0/当整页** —— 拿不到就是拿不到）。
        /// </summary>
        internal static Rect VisualBounds(Visual pageVisual)
        {
            try
            {
                if (pageVisual == null) { return Rect.Empty; }
                return VisualTreeHelper.GetDescendantBounds(pageVisual);
            }
            catch (System.Exception)
            {
                return Rect.Empty;
            }
        }

        private static string N(double v)
        {
            return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string R(Rect r)
        {
            if (r.IsEmpty) { return "empty"; }
            return N(r.X) + "," + N(r.Y) + "," + N(r.Width) + "," + N(r.Height);
        }

        private static void Emit(string line)
        {
            try
            {
                System.Console.Error.WriteLine(line);
                System.Console.Error.Flush();
            }
            catch (System.Exception)
            {
                // 打印失败不许改变行为（例如 stderr 已关闭）。
            }
        }

        internal static void Report(string site, bool hasDocument, bool suspendLayout, bool hasScrollData,
                                    Size arrangeSize, Rect viewport, Rect handed, FlowDocumentFormatter formatter,
                                    Visual pageVisual)
        {
            if (_traceCount >= TraceMax)
            {
                if (_traceCount == TraceMax)
                {
                    _traceCount++;
                    Emit("[FSVIEW] site=" + site + " trace=suppressed-after-" + TraceMax + "lines");
                }
                return;
            }
            _traceCount++;
            string pageSize = "NA";
            try
            {
                Size cs = formatter.DocumentPage.Size;
                pageSize = N(cs.Width) + "x" + N(cs.Height);
            }
            catch (System.Exception)
            {
                pageSize = "NA";
            }
            Emit("[FSVIEW] site=" + site
                 + " doc=" + (hasDocument ? 1 : 0)
                 + " suspend=" + (suspendLayout ? 1 : 0)
                 + " scroll=" + (hasScrollData ? 1 : 0)
                 + " drive=" + (Enabled ? "on" : "off")
                 + " arrange=" + N(arrangeSize.Width) + "x" + N(arrangeSize.Height)
                 + " viewport=" + R(viewport)
                 + " handed=" + R(handed)
                 + " page=" + pageSize
                 + " visbounds=" + R(VisualBounds(pageVisual))
                 + " NOINFO=fsview-window(managed-side-readonly-ledger)");
        }

        internal static void ReportException(string site, System.Exception e)
        {
            Emit("[FSVIEW] site=" + site + " outcome=exception type="
                 + ((e == null) ? "null" : e.GetType().FullName));
        }
    }
}
