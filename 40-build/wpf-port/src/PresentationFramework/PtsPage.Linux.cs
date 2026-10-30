// ⚠️ 本文件由 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/reapply-patches.py **生成**，不要手改。
//
// 内容 = 上游 `src/Microsoft.DotNet.Wpf/src/PresentationFramework/MS/Internal/PtsHost/PtsPage.cs` 逐字复制 + 7 处 W86A（`TASK-0304`/`TASK-0305`）改动。
// 每次运行该脚本都会从上游重读重生成；needle 找不到 / 命中数不符时**报错退出**
// （不会静默产出未打补丁的副本）。改动逐处见：
//   E1 ／ E2 ／ E3 ／ E4 ／ E5 ／ E6 ／ E7
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
// Description: Wrapper for PTS page. 
//

using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Documents;
using MS.Internal.Text;
using System.Windows.Threading;
using MS.Internal.Documents;
using MS.Internal.PtsHost.UnsafeNativeMethods;

namespace MS.Internal.PtsHost
{
    // ----------------------------------------------------------------------
    // Wrapper for PTS page object.
    // ----------------------------------------------------------------------
    internal class PtsPage : IDisposable
    {
        //-------------------------------------------------------------------
        //
        //  Constructors
        //
        //-------------------------------------------------------------------

        #region Constructors

        // ------------------------------------------------------------------
        // Constructor.
        //
        //      section - PTS section: root of the NameTable
        // ------------------------------------------------------------------
        internal PtsPage(Section section) : this()
        {
            _section = section;
        }

        // ------------------------------------------------------------------
        // Constructor.
        // ------------------------------------------------------------------
        private PtsPage()
        {
            _ptsPage = IntPtr.Zero;
        }

        // ------------------------------------------------------------------
        // Finalizer.
        // ------------------------------------------------------------------
        ~PtsPage()
        {
            Dispose(false);
        }

        // ------------------------------------------------------------------
        // Dispose unmanaged resources.
        // ------------------------------------------------------------------
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion Constructors

        //-------------------------------------------------------------------
        //
        //  Internal Methods
        //
        //-------------------------------------------------------------------

        #region Internal Methods

        //-------------------------------------------------------------------
        // Prepare for incremental update process. If update is not possible
        // make sure that structural cache is in clean state.
        //
        // Following logic is used for to prepare for reformat/update:
        // a) if _ptsPage == NULL,  full format + clear NameTable(from DTRs) + clear DTRs
        // b) if NameTable == NULL, full format + clear DTRs (covered by a))
        // c) if ForceReformat,     full format + clear DTRs + clear NameTable
        // e) otherwise,            update
        //
        // Allow incremental update if there is a page previously created
        // and ForceReformat flag is not set.
        // Before formatting needs to make sure that following is done:
        // 1) If ForceReformat is true, clear entire NameTable.
        // 2) If there is existing DTR and update is not possible,
        //    invalidate NameTable from the firts position
        //    stored in DTR list, then clear DTR list.
        //
        // Returns: 'true' if can do incremental update.
        //-------------------------------------------------------------------
        internal bool PrepareForBottomlessUpdate()
        {
            bool canUpdate = !IsEmpty;

            if(!_section.CanUpdate)
            {
                // Main text segment is null for section, no update possible.
                canUpdate = false;
            }
            else if (_section.StructuralCache != null)
            {
                // No update is possible when ForceReformat flag is set.
                // Clear update information and clear entire NameTable.
                if (_section.StructuralCache.ForceReformat)
                {
                    canUpdate = false;
                    _section.StructuralCache.ClearUpdateInfo(true);
                }
                else if (_section.StructuralCache.DtrList != null)
                {
                    // If there is DRT list and the page cannot be updated, 
                    // invalidate entire NameTable starting from the position 
                    // of the first DTR.
                    // Then clear update info.
                    if (!canUpdate)
                    {
                        _section.InvalidateStructure();
                        _section.StructuralCache.ClearUpdateInfo(false);
                    }
                }
                // else the NameTable is in a valid state.
            }

            return canUpdate;
        }

        //-------------------------------------------------------------------
        // Prepare for incremental update process of a finite page. Finite 
        // page incremental is always done by PTS as full format with change
        // delta exposed through queries.
        // Allow incremental update if there is a page previously created.
        // Before formatting needs to make sure that following is done:
        // a) If ForceReformat is true, clear entire NameTable (reformat needs to
        //    start from the first page).
        // b) If there is existing DTR, invalidate NameTable from the firts position
        //    stored in DTR list, then clear DTR list.
        // NOTE: If ForceReformat is false and DTR list is null, it means that 
        //       existing NameTable is in a valid state, and format can be done using
        //       cached portion of the NameTable.
        //
        //      breakRecord - PageBreakRecord describing start position of the page.
        //
        // Returns: 'true' if can do incremental update.
        //-------------------------------------------------------------------
        internal bool PrepareForFiniteUpdate(PageBreakRecord breakRecord)
        {
            bool canUpdate = !IsEmpty;
#if DEBUG
            Debug.Assert(!canUpdate || _section.CanUpdate);
#endif
            if (_section.StructuralCache != null)
            {
                // No update is possible when ForceReformat flag is set.
                // Clear update information and clear entire NameTable.
                if (_section.StructuralCache.ForceReformat)
                {
                    canUpdate = false;
                    Debug.Assert(breakRecord == null || !_section.StructuralCache.DestroyStructure, "Cannot format from dirty break record unless StructuralCache.DestroyStructure is not set.");
                 
                    _section.InvalidateStructure();
                    // Update structural cache info. The DestroyStructureCache parameter is set to true if
                    // the name table is not preserved. If the name table is to be preserved, e.g. for highlight
                    // changed, we do not clear structure cache
                    _section.StructuralCache.ClearUpdateInfo(/*destroy structure cache:*/ _section.StructuralCache.DestroyStructure);
                } 
                // If there is DRT list, invalidate entire NameTable starting from the 
                // position of the first DTR.
                // Then clear update info, if incremental update is not possible.
                else if (_section.StructuralCache.DtrList != null)
                {
                    _section.InvalidateStructure();
                    if (!canUpdate)
                    {
                        _section.StructuralCache.ClearUpdateInfo(false);
                    }
                }
                // The NameTable is in a valid state, but cannot do incremental update, because
                // there is no DTR stored anymore.
                else
                {
                    canUpdate = false;
                    _section.StructuralCache.ClearUpdateInfo(false);
                }
            }
            return canUpdate;
        }

        //-------------------------------------------------------------------
        // Hit tests to the correct IInputElement within the page that the 
        // mouse is over.
        //
        //      p - Mouse coordinates relative to the page.
        //
        // Returns: IInputElement that has been hit.
        //-------------------------------------------------------------------
        internal IInputElement InputHitTest(Point p)
        {
            IInputElement ie = null;
            if (!IsEmpty)
            {
                PTS.FSPOINT pt = TextDpi.ToTextPoint(p);
                ie = InputHitTestPage(pt);
            }
            return ie;
        }

        //-------------------------------------------------------------------
        // Returns rectangles for a specified element.
        //
        //      e - ContentElement for which rectangles are to be returned
        //      start - int representing start offset of e
        //      length - int representing number of positions occupied by e
        //
        // Returns: ArrayList of rectangles. If element is not found or if 
        // there is nothing in this page, returns empty ArrayList
        //-------------------------------------------------------------------
        internal List<Rect> GetRectangles(ContentElement e, int start, int length)
        {
            List<Rect> rectangles = new List<Rect>();
            if (!IsEmpty)
            {
                rectangles = GetRectanglesInPage(e, start, length);
            }
            return rectangles;
        }

        //-------------------------------------------------------------------
        // Callback for background layout / update
        //-------------------------------------------------------------------
        private static DispatcherOperationCallback BackgroundUpdateCallback = new DispatcherOperationCallback(PtsPage.BackgroundFormatStatic);

        //-------------------------------------------------------------------
        // Static function, just proxies over to our real function
        //-------------------------------------------------------------------
        private static object BackgroundFormatStatic(object arg)
        {
            Invariant.Assert(arg is PtsPage);
            ((PtsPage)arg).BackgroundFormat();
            return null;
        }

        //-------------------------------------------------------------------
        // Does the work of background format - For now, this is simply an invalidate measure call.
        // text.
        //-------------------------------------------------------------------
        private void BackgroundFormat()
        {
            FlowDocument formattingOwner = _section.StructuralCache.FormattingOwner; 

            if (formattingOwner.Formatter is FlowDocumentFormatter)
            {
                _section.StructuralCache.BackgroundFormatInfo.BackgroundFormat(formattingOwner.BottomlessFormatter, false /* ignoreThrottle */);
            }
        }

        //-------------------------------------------------------------------
        // Defers remaining text formatting to background - treated as if new text
        //-------------------------------------------------------------------
        private void DeferFormattingToBackground()
        {
            int cpLast = _section.StructuralCache.BackgroundFormatInfo.CPInterrupted;
            int cpTextContainer = _section.StructuralCache.BackgroundFormatInfo.CchAllText;
                                                                                                                          
            DirtyTextRange dtr = new DirtyTextRange(cpLast, cpTextContainer - cpLast, cpTextContainer - cpLast);
            _section.StructuralCache.AddDirtyTextRange(dtr);

            _backgroundFormatOperation = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, BackgroundUpdateCallback, this);
        }

        // ------------------------------------------------------------------
        // Create bottomless page.
        // ------------------------------------------------------------------
        internal void CreateBottomlessPage()
        {
            OnBeforeFormatPage(false, false);

            if (TracePageFormatting.IsEnabled)
            {
                TracePageFormatting.Trace(
                    TraceEventType.Start,
                    TracePageFormatting.FormatPage,
                    PageContext,
                    PtsContext);
            }

            PTS.FSFMTRBL formattingResult;
            IntPtr ptsPage;
            int fserr = PTS.FsCreatePageBottomless(PtsContext.Context, _section.Handle, out formattingResult, out ptsPage);
            if (fserr != PTS.fserrNone)
            {
                // Formatting failed and ptsPage may be set to a partially formatted page. Set value to IntPtr.Zero
                _ptsPage = IntPtr.Zero;
                PTS.ValidateAndTrace(fserr, PtsContext);
            }
            else
            {
                // Formatting succeeded. Set page value
                _ptsPage = ptsPage;
            }

            if (TracePageFormatting.IsEnabled)
            {
                TracePageFormatting.Trace(
                    TraceEventType.Stop,
                    TracePageFormatting.FormatPage,
                    PageContext,
                    PtsContext);
            }

            OnAfterFormatPage(true, false);

            if(formattingResult == PTS.FSFMTRBL.fmtrblInterrupted)
            {
                DeferFormattingToBackground();
            }
        }

        // ------------------------------------------------------------------
        // Update bottomless page.
        // ------------------------------------------------------------------
        internal void UpdateBottomlessPage()
        {
            if (IsEmpty)
            {
                return;
            }

            OnBeforeFormatPage(false, true);

            if (TracePageFormatting.IsEnabled)
            {
                TracePageFormatting.Trace(
                    TraceEventType.Start,
                    TracePageFormatting.FormatPage,
                    PageContext,
                    PtsContext);
            }

            PTS.FSFMTRBL formattingResult;
            int fserr = PTS.FsUpdateBottomlessPage(PtsContext.Context, _ptsPage, _section.Handle, out formattingResult);
            if (fserr != PTS.fserrNone)
            {
                // Do inplace cleanup.
                DestroyPage();

                // Generic error handling.
                PTS.ValidateAndTrace(fserr, PtsContext);
            }

            if (TracePageFormatting.IsEnabled)
            {
                TracePageFormatting.Trace(
                    TraceEventType.Stop,
                    TracePageFormatting.FormatPage,
                    PageContext,
                    PtsContext);
            }

            OnAfterFormatPage(true, true);

            if(formattingResult == PTS.FSFMTRBL.fmtrblInterrupted)
            {
                DeferFormattingToBackground();
            }
        }

        // ------------------------------------------------------------------
        // Create finite page.
        // ------------------------------------------------------------------
        internal void CreateFinitePage(PageBreakRecord breakRecord)
        {
            OnBeforeFormatPage(true, false);

            if (TracePageFormatting.IsEnabled)
            {
                TracePageFormatting.Trace(
                    TraceEventType.Start,
                    TracePageFormatting.FormatPage,
                    PageContext,
                    PtsContext);
            }

            // Retrieve PTS break record
            IntPtr brIn = (breakRecord != null) ? breakRecord.BreakRecord : IntPtr.Zero;

            // Create finite page and update layout size information
            PTS.FSFMTR formattingResult;
            IntPtr brOut;
            IntPtr ptsPage;
            int fserr = PTS.FsCreatePageFinite(PtsContext.Context, brIn, _section.Handle, out formattingResult, out ptsPage, out brOut);
            if (fserr != PTS.fserrNone)
            {
                // Formatting failed and ptsPage may be set to a partially formatted page. Set value to IntPtr.Zero
                _ptsPage = IntPtr.Zero;
                brOut = IntPtr.Zero;
                PTS.ValidateAndTrace(fserr, PtsContext);
            }
            else
            {
                _ptsPage = ptsPage;
            }
            if (brOut != IntPtr.Zero)
            {
                StructuralCache structuralCache = _section.StructuralCache;
                if (structuralCache != null)
                {
                    _breakRecord = new PageBreakRecord(PtsContext, brOut, (breakRecord != null) ? breakRecord.PageNumber + 1 : 1);
                }
            }

            if (TracePageFormatting.IsEnabled)
            {
                TracePageFormatting.Trace(
                    TraceEventType.Stop,
                    TracePageFormatting.FormatPage,
                    PageContext,
                    PtsContext);
            }

            OnAfterFormatPage(true, false);
        }

        // ------------------------------------------------------------------
        // Update finite page.
        // ------------------------------------------------------------------
        internal void UpdateFinitePage(PageBreakRecord breakRecord)
        {
            if (IsEmpty)
            {
                return;
            }

            OnBeforeFormatPage(true, true);

            if (TracePageFormatting.IsEnabled)
            {
                TracePageFormatting.Trace(
                    TraceEventType.Start,
                    TracePageFormatting.FormatPage,
                    PageContext,
                    PtsContext);
            }

            // Retrieve PTS break record
            IntPtr brIn = (breakRecord != null) ? breakRecord.BreakRecord : IntPtr.Zero;

            // Create finite page and update layout size information
            PTS.FSFMTR formattingResult;
            IntPtr brOut;
            int fserr = PTS.FsUpdateFinitePage(PtsContext.Context, _ptsPage, brIn,
                                               _section.Handle, out formattingResult, out brOut);

            if (fserr != PTS.fserrNone)
            {
                // Do inplace cleanup.
                DestroyPage();

                // Generic error handling.
                PTS.ValidateAndTrace(fserr, PtsContext);
            }
            if (brOut != IntPtr.Zero)
            {
                StructuralCache structuralCache = _section.StructuralCache;
                if (structuralCache != null)
                {
                    _breakRecord = new PageBreakRecord(PtsContext, brOut, (breakRecord != null) ? breakRecord.PageNumber + 1 : 1);
                }
            }

            if (TracePageFormatting.IsEnabled)
            {
                TracePageFormatting.Trace(
                    TraceEventType.Stop,
                    TracePageFormatting.FormatPage,
                    PageContext,
                    PtsContext);
            }

            OnAfterFormatPage(true, true);
        }

        //-------------------------------------------------------------------
        // Arrange top level PTS page.
        //-------------------------------------------------------------------
        internal void ArrangePage()
        {
            if (IsEmpty)
            {
                return;
            }

            _section.UpdateSegmentLastFormatPositions();

            // Get page details
            PTS.FSPAGEDETAILS pageDetails;
            PTS.Validate(PTS.FsQueryPageDetails(PtsContext.Context, _ptsPage, out pageDetails));


            // Arrange page content. Page content may be simple or complex -
            // depending of set of features used in the content of the page.
            // (1) simple page (contains only one track)
            // (2) complex page (contains header, page body (list of sections), footnotes and footer)
            if (PTS.ToBoolean(pageDetails.fSimple))
            {
                // (1) simple page (contains only one track)
                // Exceptions don't need to pop, as the top level arrange context will be nulled out if thrown.
                _section.StructuralCache.CurrentArrangeContext.PushNewPageData(_pageContextOfThisPage, pageDetails.u.simple.trackdescr.fsrc, _finitePage);

                PtsHelper.ArrangeTrack(PtsContext, ref pageDetails.u.simple.trackdescr, PTS.FlowDirectionToFswdir(_section.StructuralCache.PageFlowDirection));

                _section.StructuralCache.CurrentArrangeContext.PopPageData();
            }
            else
            {
                // (2) complex page (contains header, page body (list of sections), footnotes and footer)
                //     NOTE: only page body (list of sections is currently supported).
                //ErrorHandler.Assert(!PTS.ToBoolean(pageDetails.u.complex.fTopBottomHeaderFooter), ErrorHandler.NotSupportedHeadersFooters);
                //ErrorHandler.Assert(!PTS.ToBoolean(pageDetails.u.complex.fJustified), ErrorHandler.NotSupportedVerticalJustify);
                ErrorHandler.Assert(pageDetails.u.complex.cFootnoteColumns == 0, ErrorHandler.NotSupportedFootnotes);

                // cSections == 0, means that page body content is empty.
                if (pageDetails.u.complex.cSections != 0)
                {
                    // Retrieve description for each section.
                    PTS.FSSECTIONDESCRIPTION[] arraySectionDesc;
                    PtsHelper.SectionListFromPage(PtsContext, _ptsPage, ref pageDetails, out arraySectionDesc);

                    // Arrange each section
                    for (int index = 0; index < arraySectionDesc.Length; index++)
                    {
                        ArrangeSection(ref arraySectionDesc[index]);
                    }
                }
            }
}

        //-------------------------------------------------------------------
        // Update viewport top-level
        //-------------------------------------------------------------------
        internal void UpdateViewport(ref PTS.FSRECT viewport)
        {
            WpfLinuxChainProbe.Hit("PTSP.UpdateViewport", "empty=" + (IsEmpty ? 1 : 0) + " vp=" + viewport.u + "," + viewport.v + "," + viewport.du + "," + viewport.dv);
            if (!IsEmpty)
            {
                // Get page details
                PTS.FSPAGEDETAILS pageDetails;
                PTS.Validate(PTS.FsQueryPageDetails(PtsContext.Context, _ptsPage, out pageDetails));

                // Arrange page content. Page content may be simple or complex -
                // depending of set of features used in the content of the page.
                // (1) simple page (contains only one track)
                // (2) complex page (contains header, page body (list of sections), footnotes and footer)
                if (PTS.ToBoolean(pageDetails.fSimple))
                {
                    // (1) simple page (contains only one track)

                    PtsHelper.UpdateViewportTrack(PtsContext, ref pageDetails.u.simple.trackdescr, ref viewport);
                }
                else
                {
                    // (2) complex page (contains header, page body (list of sections), footnotes and footer)
                    //     NOTE: only page body (list of sections is currently supported).
                    //ErrorHandler.Assert(!PTS.ToBoolean(pageDetails.u.complex.fTopBottomHeaderFooter), ErrorHandler.NotSupportedHeadersFooters);
                    //ErrorHandler.Assert(!PTS.ToBoolean(pageDetails.u.complex.fJustified), ErrorHandler.NotSupportedVerticalJustify);
                    ErrorHandler.Assert(pageDetails.u.complex.cFootnoteColumns == 0, ErrorHandler.NotSupportedFootnotes);

                    // cSections == 0, means that page body content is empty.
                    if (pageDetails.u.complex.cSections != 0)
                    {
                        // Retrieve description for each section.
                        PTS.FSSECTIONDESCRIPTION[] arraySectionDesc;
                        PtsHelper.SectionListFromPage(PtsContext, _ptsPage, ref pageDetails, out arraySectionDesc);

                        // Arrange each section
                        for (int index = 0; index < arraySectionDesc.Length; index++)
                        {
                            UpdateViewportSection(ref arraySectionDesc[index], ref viewport);
                        }
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // Clear update info.
        // ------------------------------------------------------------------
        internal void ClearUpdateInfo()
        {
            if (!IsEmpty)
            {
                // Clear any incremental update state acummulated during update process.
                PTS.Validate(PTS.FsClearUpdateInfoInPage(PtsContext.Context, _ptsPage), PtsContext);
            }
        }

        // ------------------------------------------------------------------
        // Get a visual representing the page's content.
        // ------------------------------------------------------------------
        internal ContainerVisual GetPageVisual()
        {
            WpfLinuxChainProbe.Hit("PTSP.GetPageVisual", "empty=" + (IsEmpty ? 1 : 0) + " visual=" + (_visual == null ? 0 : 1));
            if (_visual == null)
            {
                _visual = new ContainerVisual();
            }
            if (!IsEmpty)
            {
                UpdatePageVisuals(_calculatedSize);
            }
            else
            {
                _visual.Children.Clear();
            }
            WpfLinuxPageVisProbe.Report("PTSP.GetPageVisual.ret", "empty=" + (IsEmpty ? 1 : 0), _visual);
            return _visual;
        }

        // ── `T-B12`（`PAGINATED-PAGE-CONTENT-VISUALS`）：**在屏页的"被搬空"修复** ──────────
        //  【现取机制（逐跳）】分页器造页时，同一 `BaseParaClient.Visual` 会被**后来的页**沿用：
        //   `PtsHelper.UpdateParaListVisuals` 的 `fskupdNew` 支**先"从旧父摘除"再 `Insert`**
        //   （现取 `[PAGEVIS] site=PH.UpdParaList.new oldParent=<本页 trackVisual>`）；
        //   而一个 `Visual` **只能有一个父** ⇒ **先造的那一页的 `trackVisual` 被搬空**。
        //   现场：`tab2` 页 `0x93377a` 的 `trackVisual=0x2d22d7e` 在 `PTSP.UpdPageVis.simple`
        //   时 `kids=1`（`L1:0x2948ddd,k=3`，**有内容**），到 `[DPH] site=Attach` 时已成
        //   `L3:k=0,b=empty`（**空**）—— `DocumentPageView` 显示的恰是这一页 ⇒ 帧面"壳内为空"。
        //  【修法（**同源**）】本页**非空 ∧ `fSimple` ∧ `trackVisual` 恰被搬空**时，用**同一条**造视觉
        //   路径（`PtsHelper.RedriveParaListVisuals` ＝ 上游 `fskupdNew` 支的逐段
        //   `从旧父摘除 → Insert → ValidateVisual(New)`）把**本页自己的**段落视觉**接回本页**。
        //   · **不**改任何 native 真值（几何／更新信息／`fskupd` **一律不写**；`fskupdNew` 只是**托管侧入参**）；
        //   · **不**删／**不**放宽任何 `Invariant.Assert`；
        //   · 只在"确已被搬空（`trackVisual.Children.Count == 0`）"时动手 ⇒ 否则**零动作**。
        //  【零假值】`WPF_PAGEPAGE_REDRIVE=0` ⇒ **整块不发生**（逐字回上游行为 ⇒ 反极性腿）。
        //  ⚠️ **不查 native**（页的 PTS 句柄可能已陈旧 ⇒ `FsQuery*` 会抛）：本页在**造视觉那一刻**
        //     把 `trackVisual` 的**子视觉引用**记下来（`_pageContentKeep`），显示时只做**托管侧的换父**。
        //     失败**如实打一行**、不重抛（它是**可选修复**路径 —— 不许它盖掉页面自身的显示，也不许静默）。
        private System.Collections.Generic.List<Visual> _pageContentKeep;
        private System.Collections.Generic.List<Visual> _pageFloatKeep;

        internal void RedrivePageVisualsForDisplay()
        {
            if (!WpfLinuxPageVisDrive.Enabled) { return; }
            if (_pageContentKeep == null && _pageFloatKeep == null) { return; }
            if (_visual == null || IsEmpty) { return; }
            if (_visual.Children.Count != 2) { return; }
            try
            {
                bool did = false;
                ContainerVisual pageContentVisual = _visual.Children[0] as ContainerVisual;
                ContainerVisual floatingVisual = _visual.Children[1] as ContainerVisual;
                if (pageContentVisual != null && pageContentVisual.Children.Count == 1)
                {
                    ContainerVisual trackVisual = pageContentVisual.Children[0] as ContainerVisual;
                    if (trackVisual != null && trackVisual.Children.Count == 0)
                    {
                        WpfLinuxPageVisDrive.ReparentInto(trackVisual, _pageContentKeep);
                        did = true;
                    }
                }
                if (floatingVisual != null && floatingVisual.Children.Count == 0)
                {
                    WpfLinuxPageVisDrive.ReparentInto(floatingVisual, _pageFloatKeep);
                    did = true;
                }
                if (did)
                {
                    WpfLinuxPageVisProbe.Report("PTSP.RedrivePageVisuals",
                        "content=" + ((_pageContentKeep == null) ? 0 : _pageContentKeep.Count)
                        + " float=" + ((_pageFloatKeep == null) ? 0 : _pageFloatKeep.Count), _visual);
                }
            }
            catch (System.Exception e)
            {
                WpfLinuxPageVisProbe.Report("PTSP.RedrivePageVisuals",
                    "outcome=exception type=" + e.GetType().Name, null);
            }
        }

        #endregion Internal Methods

        //-------------------------------------------------------------------
        //
        //  Internal Properties
        //
        //-------------------------------------------------------------------

        #region Internal Properties

        //-------------------------------------------------------------------
        // BreakRecord indicating break position of the page. 
        // 'null' if the page is bottomless or it is the last page
        //-------------------------------------------------------------------
        internal PageBreakRecord BreakRecord { get { return _breakRecord; } }

        //-------------------------------------------------------------------
        // Calculated size of the page.
        //-------------------------------------------------------------------
        internal Size CalculatedSize { get { return _calculatedSize; } }

        //-------------------------------------------------------------------
        // Content size of the page.
        //-------------------------------------------------------------------
        internal Size ContentSize { get { return _contentSize; } }

        //-------------------------------------------------------------------
        // Is it finite page or bottomless?
        //-------------------------------------------------------------------
        internal bool FinitePage { get { return _finitePage; } }

        //-------------------------------------------------------------------
        // Page context
        //-------------------------------------------------------------------
        internal PageContext PageContext { get { return _pageContextOfThisPage; } }

        //-------------------------------------------------------------------
        // Is during incremental update mode?
        //-------------------------------------------------------------------
        internal bool IncrementalUpdate { get { return _incrementalUpdate; } }

        //-------------------------------------------------------------------
        // PTS Host.
        //-------------------------------------------------------------------
        internal PtsContext PtsContext { get { return _section.PtsContext; } }

        //-------------------------------------------------------------------
        // Handle to PTS page.
        //-------------------------------------------------------------------
        internal IntPtr PageHandle { get { return _ptsPage; } }

        //-------------------------------------------------------------------
        // Is being used in a plain text box?
        //-------------------------------------------------------------------
        internal bool UseSizingWorkaroundForTextBox
        {
            get { return _useSizingWorkaroundForTextBox; }
            set { _useSizingWorkaroundForTextBox = value; }
        }

        #endregion Internal Properties

        // ------------------------------------------------------------------
        //
        //  Private Methods
        //
        // ------------------------------------------------------------------

        #region Private Methods

        // ------------------------------------------------------------------
        // Dispose unmanaged resources.
        // ------------------------------------------------------------------
        /// <remarks>
        /// Finalizer needs to follow rules below:
        ///     a) Your Finalize method must tolerate partially constructed instances.
        ///     b) Your Finalize method must consider the consequence of failure.
        ///     c) Your object is callable after Finalization.
        ///     d) Your object is callable during Finalization.
        ///     e) Your Finalizer could be called multiple times.
        ///     f) Your Finalizer runs in a delicate security context.
        /// See: http://blogs.msdn.com/cbrumme/archive/2004/02/20/77460.aspx
        /// </remarks>
        private void Dispose(bool disposing)
        {
            if (!Interlocked.CompareExchange(ref _disposed, true, false))
            {
                // Destroy PTS page.
                // According to following article the entire reachable graph from 
                // a finalizable object is promoted, and it is safe to access its 
                // members if they do not have their own finalizers.
                // Hence it is OK to access _section during finalization.
                // See: http://blogs.msdn.com/cbrumme/archive/2004/02/20/77460.aspx
                if (!IsEmpty)
                {
                    _section.PtsContext.OnPageDisposed(_ptsPage, disposing, true);
                }

                // Cleanup the state.
                _ptsPage = IntPtr.Zero;
                _breakRecord = null;
                _visual = null;
                _backgroundFormatOperation = null;
            }
        }

        // ------------------------------------------------------------------
        // Inlitialize page state before formatting of the page.
        // ------------------------------------------------------------------
        private void OnBeforeFormatPage(bool finitePage, bool incremental)
        {
            // If not in incremental mode a new PTS page is created, hence there is 
            // a need to destroy existing one to avoid unmanaged resources leaking. 
            if (!incremental && !IsEmpty)
            {
                DestroyPage();
            }

            _incrementalUpdate = incremental;
            _finitePage = finitePage;
            _breakRecord = null;
            _pageContextOfThisPage.PageRect = new PTS.FSRECT(new Rect(_section.StructuralCache.CurrentFormatContext.PageSize));

            // Ensure we have no background work pending
            _backgroundFormatOperation?.Abort();

            if (!_finitePage)
            {
                _section.StructuralCache.BackgroundFormatInfo.UpdateBackgroundFormatInfo();
            }
        }

        // ------------------------------------------------------------------
        // Clear state and collect necessary data after formatting of the page.
        // ------------------------------------------------------------------
        private void OnAfterFormatPage(bool setSize, bool incremental)
        {
            // Update page size if necessary
            if (setSize)
            {
                PTS.FSRECT rect = GetRect();
                PTS.FSBBOX bbox = GetBoundingBox();
                // Workaround for PTS bug 860: get max of the page rect and 
                // bounding box of the page.
                if (!FinitePage && PTS.ToBoolean(bbox.fDefined))
                {
                    rect.dv = Math.Max(rect.dv, bbox.fsrc.dv);
                }
                // Set page size
                _calculatedSize.Width  = Math.Max(TextDpi.MinWidth, TextDpi.FromTextDpi(rect.du));
                _calculatedSize.Height = Math.Max(TextDpi.MinWidth, TextDpi.FromTextDpi(rect.dv));
                // Set content size
                if (PTS.ToBoolean(bbox.fDefined))
                {
                    _contentSize.Width = Math.Max(Math.Max(TextDpi.FromTextDpi(bbox.fsrc.du), TextDpi.MinWidth), _calculatedSize.Width);
                    _contentSize.Height = Math.Max(TextDpi.MinWidth, TextDpi.FromTextDpi(bbox.fsrc.dv));
                    // In bottomless pages, page size reported by PTS is 
                    // actually content size (see PTS bug 860 for exceptions).
                    // Take PTS calculated value into account.
                    if (!FinitePage)
                    {
                        _contentSize.Height = Math.Max(_contentSize.Height, _calculatedSize.Height);
                    }
                }
                else
                {
                    _contentSize = _calculatedSize;
                }
            }

            if (!IsEmpty)
            {
                // If page has been just created, notify PtsContext about this fact.
                // PtsContext keeps track of all created pages.
                if (!incremental)
                {
                    PtsContext.OnPageCreated(_ptsPage);
                }
            }

            // Make sure that structural cache is in clean state after formatting
            // is done.
            _section.StructuralCache?.ClearUpdateInfo(false);
        }

        // ------------------------------------------------------------------
        // Retrieve rectangle the page/subpage.
        // ------------------------------------------------------------------
        private PTS.FSRECT GetRect()
        {
            PTS.FSRECT rect;
            // There are 3 cases when calculating page rect:
            // (1) PTS page is not created - return empty rect.
            // (2) PTS page    - use page PTS APIs to get page rectangle.
            // (3) PTS subpage - use subpage PTS APIs to get page rectangle.
            if (IsEmpty)
            {
                // (1) PTS page is not created - empty rect.
                rect = new PTS.FSRECT();
            }
            else
            {
                // (2) PTS page - use page PTS APIs to get page rectangle.
                PTS.FSPAGEDETAILS pageDetails;
                PTS.Validate(PTS.FsQueryPageDetails(PtsContext.Context, _ptsPage, out pageDetails));

                // There are 2 different types of PTS page and calculated rectangle depends on it:
                // (a) simple page (contains only one track) - get rectanglefrom the track.
                // (b) complex page (contains header, page body, footnotes and footer) - get bounding 
                //     box of each segment and union them.
                if (PTS.ToBoolean(pageDetails.fSimple))
                {
                    // (a) simple page (contains only one track) - get rectanglefrom the track.
                    rect = pageDetails.u.simple.trackdescr.fsrc;
                }
                else
                {
                    // (b) complex page (contains header, page body, footnotes and footer) - get rectangle 
                    //     of each segment and union them.
                    //ErrorHandler.Assert(!PTS.ToBoolean(pageDetails.u.complex.fTopBottomHeaderFooter) || this.PresenterCache.CurrentPresenter.IsBottomless, ErrorHandler.NotSupportedHeadersFooters);
                    //ErrorHandler.Assert(!PTS.ToBoolean(pageDetails.u.complex.fJustified) || this.PresenterCache.CurrentPresenter.IsBottomless, ErrorHandler.NotSupportedVerticalJustify);
                    ErrorHandler.Assert(pageDetails.u.complex.cFootnoteColumns == 0, ErrorHandler.NotSupportedFootnotes);

                    // Since header/footer and footnotes are not supported yet, use page body rectangle
                    rect = pageDetails.u.complex.fsrcPageBody;
                }
            }
            return rect;
        }

        // ------------------------------------------------------------------
        // Retrieve bounding box of the page/subpage
        // ------------------------------------------------------------------
        private PTS.FSBBOX GetBoundingBox()
        { 
            PTS.FSBBOX bbox = new PTS.FSBBOX();
            // There are 3 cases when calculating bounding box:
            // (1) PTS page is not created - return empty rect.
            // (2) PTS page - use page PTS APIs to get bounding box.
            // (3) PTS subpage - use subpage PTS APIs to get bounding box.
            if (IsEmpty)
            {
                // (1) PTS page is not created - bbox is not defined
            }
            else
            {
                // (2) PTS page - use page PTS APIs to get bounding box.
                PTS.FSPAGEDETAILS pageDetails;
                PTS.Validate(PTS.FsQueryPageDetails(PtsContext.Context, _ptsPage, out pageDetails));

                // There are 2 different types of PTS page and bounding box calculation depends on it:
                // (a) simple page (contains only one track) - get bounding box from the track.
                // (b) complex page (contains header, page body, footnotes and footer) - get bounding 
                //     box of each segment and union them.
                if (PTS.ToBoolean(pageDetails.fSimple))
                {
                    // (a) simple page (contains only one track) - get bounding box from the track.
                    bbox = pageDetails.u.simple.trackdescr.fsbbox;
                }
                else
                {
                    // (b) complex page (contains header, page body, footnotes and footer) - get bounding 
                    //     box of each segment and union them.
                    //ErrorHandler.Assert(!PTS.ToBoolean(pageDetails.u.complex.fTopBottomHeaderFooter), ErrorHandler.NotSupportedHeadersFooters);
                    //ErrorHandler.Assert(!PTS.ToBoolean(pageDetails.u.complex.fJustified), ErrorHandler.NotSupportedVerticalJustify);
                    ErrorHandler.Assert(pageDetails.u.complex.cFootnoteColumns == 0, ErrorHandler.NotSupportedFootnotes);
                    // Since header/footer and footnotes are not supported yet, use page body bounding box
                    bbox = pageDetails.u.complex.fsbboxPageBody;
                }
            }
            return bbox;
        }

        //-------------------------------------------------------------------
        // Arrange PTS section.
        //-------------------------------------------------------------------
        private void ArrangeSection(ref PTS.FSSECTIONDESCRIPTION sectionDesc)
        {
            // Get section details
            PTS.FSSECTIONDETAILS sectionDetails;
            PTS.Validate(PTS.FsQuerySectionDetails(PtsContext.Context, sectionDesc.pfssection, out sectionDetails));

            // There are 2 types of sections:
            // (1) with page notes - footnotes in section treated as endnotes
            // (2) with column notes - footnotes in section treated as column notes
            if (PTS.ToBoolean(sectionDetails.fFootnotesAsPagenotes))
            {
                // (1) with page notes - footnotes in section treated as endnotes
                ErrorHandler.Assert(sectionDetails.u.withpagenotes.cEndnoteColumns == 0, ErrorHandler.NotSupportedFootnotes);

                // Need to impl. Extended multi-column layout.
                Debug.Assert(sectionDetails.u.withpagenotes.cSegmentDefinedColumnSpanAreas == 0);
                Debug.Assert(sectionDetails.u.withpagenotes.cHeightDefinedColumnSpanAreas == 0);

                // cBasicColumns == 0, means that section content is empty.
                // In such case there is nothing to render.
                if (sectionDetails.u.withpagenotes.cBasicColumns != 0)
                {
                    // Retrieve description for each column.
                    PTS.FSTRACKDESCRIPTION [] arrayColumnDesc;
                    PtsHelper.TrackListFromSection(PtsContext, sectionDesc.pfssection, ref sectionDetails, out arrayColumnDesc);

                    // Arrange each column
                    for (int index = 0; index < arrayColumnDesc.Length; index++)
                    {
                        // Exceptions don't need to pop, as the top level arrange context will be nulled out if thrown.
                        _section.StructuralCache.CurrentArrangeContext.PushNewPageData(_pageContextOfThisPage, arrayColumnDesc[index].fsrc, _finitePage);

                        PtsHelper.ArrangeTrack(PtsContext, ref arrayColumnDesc[index], sectionDetails.u.withpagenotes.fswdir);

                        _section.StructuralCache.CurrentArrangeContext.PopPageData();
                    }
                }
            }
            else
            {
                // (2) with column notes - footnotes in section treated as column notes
                ErrorHandler.Assert(false, ErrorHandler.NotSupportedCompositeColumns);
            }
        }

        //-------------------------------------------------------------------
        // Update the viewport for a section
        //-------------------------------------------------------------------
        private void UpdateViewportSection(ref PTS.FSSECTIONDESCRIPTION sectionDesc, ref PTS.FSRECT viewport)
        {
            // Get section details
            PTS.FSSECTIONDETAILS sectionDetails;
            PTS.Validate(PTS.FsQuerySectionDetails(PtsContext.Context, sectionDesc.pfssection, out sectionDetails));

            // There are 2 types of sections:
            // (1) with page notes - footnotes in section treated as endnotes
            // (2) with column notes - footnotes in section treated as column notes
            if (PTS.ToBoolean(sectionDetails.fFootnotesAsPagenotes))
            {
                // (1) with page notes - footnotes in section treated as endnotes
                ErrorHandler.Assert(sectionDetails.u.withpagenotes.cEndnoteColumns == 0, ErrorHandler.NotSupportedFootnotes);

                //  Need to impl. Extended multi-column layout.
                Debug.Assert(sectionDetails.u.withpagenotes.cSegmentDefinedColumnSpanAreas == 0);
                Debug.Assert(sectionDetails.u.withpagenotes.cHeightDefinedColumnSpanAreas == 0);

                // cBasicColumns == 0, means that section content is empty.
                // In such case there is nothing to render.
                if (sectionDetails.u.withpagenotes.cBasicColumns != 0)
                {
                    // Retrieve description for each column.
                    PTS.FSTRACKDESCRIPTION [] arrayColumnDesc;
                    PtsHelper.TrackListFromSection(PtsContext, sectionDesc.pfssection, ref sectionDetails, out arrayColumnDesc);

                    // Arrange each column
                    for (int index = 0; index < arrayColumnDesc.Length; index++)
                    {
                        PtsHelper.UpdateViewportTrack(PtsContext, ref arrayColumnDesc[index], ref viewport);
                    }
                }
            }
            else
            {
                // (2) with column notes - footnotes in section treated as column notes
                ErrorHandler.Assert(false, ErrorHandler.NotSupportedCompositeColumns);
            }
        }

        //-------------------------------------------------------------------
        // Update PTS page visuals.
        //-------------------------------------------------------------------
        private void UpdatePageVisuals(Size arrangeSize)
        {
            WpfLinuxChainProbe.Hit("PTSP.UpdatePageVisuals", "size=" + WpfLinuxChainProbe.N(arrangeSize.Width) + "x" + WpfLinuxChainProbe.N(arrangeSize.Height));
            Invariant.Assert(!IsEmpty);
            VisualCollection visualChildren;

            // Get page details
            PTS.FSPAGEDETAILS pageDetails;
            PTS.Validate(PTS.FsQueryPageDetails(PtsContext.Context, _ptsPage, out pageDetails));

            // If there is no change, visual information is valid
            if (pageDetails.fskupd == PTS.FSKUPDATE.fskupdNoChange) { return; }
            ErrorHandler.Assert(pageDetails.fskupd != PTS.FSKUPDATE.fskupdShifted, ErrorHandler.UpdateShiftedNotValid);

            ContainerVisual pageContentVisual;
            ContainerVisual floatingElementsVisual;

            if(_visual.Children.Count != 2)
            {
                _visual.Children.Clear();
                _visual.Children.Add(new ContainerVisual());
                _visual.Children.Add(new ContainerVisual());
            }

            pageContentVisual = (ContainerVisual)_visual.Children[0];
            floatingElementsVisual = (ContainerVisual)_visual.Children[1];

            // Page content may be simple or complex -
            // depending of set of features used in the content of the page.
            // (1) simple page (contains only one track)
            // (2) complex page (contains header, page body (list of sections), footnotes and footer)
            if (PTS.ToBoolean(pageDetails.fSimple))
            {
                // (1) simple page (contains only one track)
                //     Each track is represented as a ContainerVisual.
                PTS.FSKUPDATE fskupd = pageDetails.u.simple.trackdescr.fsupdinf.fskupd;
                if (fskupd == PTS.FSKUPDATE.fskupdInherited)
                {
                    fskupd = pageDetails.fskupd;
                }
                visualChildren = pageContentVisual.Children;
                if (fskupd == PTS.FSKUPDATE.fskupdNew)
                {
                    visualChildren.Clear();
                    visualChildren.Add(new ContainerVisual());
                }
                // For complex page SectionVisual is added. So, when morphing
                // complex subpage to simple one, remove SectionVisual.
                else if (visualChildren.Count == 1 && visualChildren[0] is SectionVisual)
                {
                    visualChildren.Clear();
                    visualChildren.Add(new ContainerVisual());
                }
                Debug.Assert(visualChildren.Count == 1 && visualChildren[0] is ContainerVisual);
                ContainerVisual trackVisual = (ContainerVisual)visualChildren[0];
                PtsHelper.UpdateTrackVisuals(PtsContext, trackVisual.Children, pageDetails.fskupd, ref pageDetails.u.simple.trackdescr);
                WpfLinuxPageVisProbe.Report("PTSP.UpdPageVis.simple", "fskupd=" + (int)fskupd + " track=" + WpfLinuxPageVisProbe.Id(trackVisual), trackVisual);
                WpfLinuxPageVisDrive.KeepVisuals(trackVisual, ref _pageContentKeep);
            }
            else
            {
                // (2) complex page (contains header, page body (list of sections), footnotes and footer)
                //     NOTE: only page body (list of sections is currently supported).
                //ErrorHandler.Assert(!PTS.ToBoolean(pageDetails.u.complex.fTopBottomHeaderFooter), ErrorHandler.NotSupportedHeadersFooters);
                //ErrorHandler.Assert(!PTS.ToBoolean(pageDetails.u.complex.fJustified), ErrorHandler.NotSupportedVerticalJustify);
                ErrorHandler.Assert(pageDetails.u.complex.cFootnoteColumns == 0, ErrorHandler.NotSupportedFootnotes);

                // cSections == 0, means that page body content is empty.
                bool emptyPage = (pageDetails.u.complex.cSections == 0);
                if (!emptyPage)
                {
                    // Retrieve description for each section.
                    PTS.FSSECTIONDESCRIPTION [] arraySectionDesc;
                    PtsHelper.SectionListFromPage(PtsContext, _ptsPage, ref pageDetails, out arraySectionDesc);

                    emptyPage = (arraySectionDesc.Length == 0);
                    if (!emptyPage)
                    {
                        ErrorHandler.Assert(arraySectionDesc.Length == 1, ErrorHandler.NotSupportedMultiSection);

                        // For complex subpage SectionVisual is added. So, when morphing
                        // simple subpage to complex one, remove ParagraphVisual.
                        visualChildren = pageContentVisual.Children;
                        if (visualChildren.Count == 0)
                        {
                            visualChildren.Add(new SectionVisual());
                        }
                        else if (!(visualChildren[0] is SectionVisual))
                        {
                            visualChildren.Clear();
                            visualChildren.Add(new SectionVisual());
                        }
                        UpdateSectionVisuals((SectionVisual)visualChildren[0], pageDetails.fskupd, ref arraySectionDesc[0]);
                    }
                }
                if (emptyPage)
                {
                    // There is no content, remove all existing visuals.
                    pageContentVisual.Children.Clear();
                }
            }

            PtsHelper.UpdateFloatingElementVisuals(floatingElementsVisual, _pageContextOfThisPage.FloatingElementList);
            WpfLinuxPageVisDrive.KeepVisuals(floatingElementsVisual, ref _pageFloatKeep);
        }

        //-------------------------------------------------------------------
        // Update PTS section visuals.
        //-------------------------------------------------------------------
        private void UpdateSectionVisuals(
            SectionVisual visual, 
            PTS.FSKUPDATE fskupdInherited, 
            ref PTS.FSSECTIONDESCRIPTION sectionDesc)
        {
            PTS.FSKUPDATE fskupd = sectionDesc.fsupdinf.fskupd;
            if (fskupd == PTS.FSKUPDATE.fskupdInherited)
            {
                fskupd = fskupdInherited;
            }
            ErrorHandler.Assert(fskupd != PTS.FSKUPDATE.fskupdShifted, ErrorHandler.UpdateShiftedNotValid);

            // If there is no change, visual information is valid
            if (fskupd == PTS.FSKUPDATE.fskupdNoChange) { return; }

            bool emptySection;

            // Get section details
            PTS.FSSECTIONDETAILS sectionDetails;
            PTS.Validate(PTS.FsQuerySectionDetails(PtsContext.Context, sectionDesc.pfssection, out sectionDetails));

            // There are 2 types of sections:
            // (1) with page notes - footnotes in section treated as endnotes
            // (2) with column notes - footnotes in section treated as column notes
            if (PTS.ToBoolean(sectionDetails.fFootnotesAsPagenotes))
            {
                // (1) with page notes - footnotes in section treated as endnotes
                ErrorHandler.Assert(sectionDetails.u.withpagenotes.cEndnoteColumns == 0, ErrorHandler.NotSupportedFootnotes);

                //      Need to impl. Extended multi-column layout.
                Debug.Assert(sectionDetails.u.withpagenotes.cSegmentDefinedColumnSpanAreas == 0);
                Debug.Assert(sectionDetails.u.withpagenotes.cHeightDefinedColumnSpanAreas == 0);

                // cBasicColumns == 0, means that section content is empty.
                emptySection = (sectionDetails.u.withpagenotes.cBasicColumns == 0);
                if (!emptySection)
                {
                    // Retrieve description for each column.
                    PTS.FSTRACKDESCRIPTION [] arrayColumnDesc;
                    PtsHelper.TrackListFromSection(PtsContext, sectionDesc.pfssection, ref sectionDetails, out arrayColumnDesc);

                    emptySection = (arrayColumnDesc.Length == 0);
                    if (!emptySection)
                    {
                        // Draw column rules.
                        ColumnPropertiesGroup columnProperties = new ColumnPropertiesGroup(_section.Element);
                        visual.DrawColumnRules(ref arrayColumnDesc, TextDpi.FromTextDpi(sectionDesc.fsrc.v), TextDpi.FromTextDpi(sectionDesc.fsrc.dv), columnProperties);

                        VisualCollection visualChildren = visual.Children;
                        if (fskupd == PTS.FSKUPDATE.fskupdNew)
                        {
                            visualChildren.Clear();
                            for (int index = 0; index < arrayColumnDesc.Length; index++)
                            {
                                visualChildren.Add(new ContainerVisual());
                            }
                        }
                        ErrorHandler.Assert(visualChildren.Count == arrayColumnDesc.Length, ErrorHandler.ColumnVisualCountMismatch);
                        for (int index = 0; index < arrayColumnDesc.Length; index++)
                        {
                            ContainerVisual trackVisual = (ContainerVisual)visualChildren[index];
                            PtsHelper.UpdateTrackVisuals(PtsContext, trackVisual.Children, fskupd, ref arrayColumnDesc[index]);
                        }
                    }
                }
            }
            else
            {
                // (2) with column notes - footnotes in section treated as column notes
                ErrorHandler.Assert(false, ErrorHandler.NotSupportedCompositeColumns);
                emptySection = true;
            }
            if (emptySection)
            {
                // There is no content, remove all existing visuals.
                visual.Children.Clear();
            }
        }

        //-------------------------------------------------------------------
        // Hit tests to the correct IInputElement within the page that the 
        // mouse is over.
        //-------------------------------------------------------------------
        private IInputElement InputHitTestPage(PTS.FSPOINT pt)
        {
            IInputElement ie = null;

            if(_pageContextOfThisPage.FloatingElementList != null)
            {
                for(int index = 0; index < _pageContextOfThisPage.FloatingElementList.Count && ie == null; index++)
                {
                    BaseParaClient floatingElement = _pageContextOfThisPage.FloatingElementList[index];

                    ie = floatingElement.InputHitTest(pt);
                }
            }

            if(ie == null)
            {
                // Get page details
                PTS.FSPAGEDETAILS pageDetails;
                PTS.Validate(PTS.FsQueryPageDetails(PtsContext.Context, _ptsPage, out pageDetails));

                // Hittest page content. Page content may be simple or complex -
                // depending of set of features used in the content of the page.
                // (1) simple page (contains only one track)
                // (2) complex page (contains header, page body (list of sections), footnotes and footer)
                if (PTS.ToBoolean(pageDetails.fSimple))
                {
                    // (1) simple page (contains only one track)
                    if (pageDetails.u.simple.trackdescr.fsrc.Contains(pt))
                    {
                        ie = PtsHelper.InputHitTestTrack(PtsContext, pt, ref pageDetails.u.simple.trackdescr); 
                    }
                }
                else
                {
                    // (2) complex page (contains header, page body (list of sections), footnotes and footer)
                    //     NOTE: only page body (list of sections is currently supported).
                    //ErrorHandler.Assert(!PTS.ToBoolean(pageDetails.u.complex.fTopBottomHeaderFooter), ErrorHandler.NotSupportedHeadersFooters);
                    //ErrorHandler.Assert(!PTS.ToBoolean(pageDetails.u.complex.fJustified), ErrorHandler.NotSupportedVerticalJustify);
                    ErrorHandler.Assert(pageDetails.u.complex.cFootnoteColumns == 0, ErrorHandler.NotSupportedFootnotes);

                    // cSections == 0, means that page body content is empty.
                    // In such case there is nothing to render.
                    if (pageDetails.u.complex.cSections != 0)
                    {
                        // Retrieve description for each section.
                        PTS.FSSECTIONDESCRIPTION [] arraySectionDesc;
                        PtsHelper.SectionListFromPage(PtsContext, _ptsPage, ref pageDetails, out arraySectionDesc);

                        // Hittest each section
                        for (int index = 0; index < arraySectionDesc.Length && ie == null; index++)
                        {
                            if (arraySectionDesc[index].fsrc.Contains(pt))
                            {
                                ie = InputHitTestSection(pt, ref arraySectionDesc[index]);
                            }
                        }
                    }
                }
            }

            return ie;
        }

        //-------------------------------------------------------------------
        // Returns ArrayList of rectangles for the ContentElement e within
        // the page. If element is not found, returns empty ArrayList
        // start: int representing start offset of e
        // length: int representing number of positions occupied by e
        // ------------------------------------------------------------------
        private List<Rect> GetRectanglesInPage(ContentElement e, int start, int length)
        {            
            // Rectangles to be returned
            List<Rect> rectangles = new List<Rect>();
            Invariant.Assert(!IsEmpty);

            // Get page details
            PTS.FSPAGEDETAILS pageDetails;
            PTS.Validate(PTS.FsQueryPageDetails(PtsContext.Context, _ptsPage, out pageDetails));

            // Check for page content - if simple, contains only one track and we call the helper to
            // find the element within that track. If complex, we must traverse sections within the content.
            if (PTS.ToBoolean(pageDetails.fSimple))
            {
                // (1) simple page (contains only one track)
                rectangles = PtsHelper.GetRectanglesInTrack(PtsContext, e, start, length, ref pageDetails.u.simple.trackdescr);
            }
            else
            {
                // (2) complex page (contains header, page body (list of sections), footnotes and footer)
                //     NOTE: only page body (list of sections is currently supported).
                //ErrorHandler.Assert(!PTS.ToBoolean(pageDetails.u.complex.fTopBottomHeaderFooter), ErrorHandler.NotSupportedHeadersFooters);
                //ErrorHandler.Assert(!PTS.ToBoolean(pageDetails.u.complex.fJustified), ErrorHandler.NotSupportedVerticalJustify);
                ErrorHandler.Assert(pageDetails.u.complex.cFootnoteColumns == 0, ErrorHandler.NotSupportedFootnotes);

                // cSections == 0, means that page body content is empty.
                // In such case there is nothing to render.
                if (pageDetails.u.complex.cSections != 0)
                {
                    // Retrieve description for each section.
                    PTS.FSSECTIONDESCRIPTION[] arraySectionDesc;
                    PtsHelper.SectionListFromPage(PtsContext, _ptsPage, ref pageDetails, out arraySectionDesc);

                    // Check each section for element
                    for (int index = 0; index < arraySectionDesc.Length; index++)
                    {
                        rectangles = GetRectanglesInSection(e, start, length, ref arraySectionDesc[index]);
                        
                        // For consistency, helpers cannot return null for rectangles
                        Invariant.Assert(rectangles != null);
                        if (rectangles.Count != 0)
                        {
                            // Found element and rectangles. We will sotp here because the element has been
                            // found and it cannot span more than one section. So we do not need to search
                            // the rest of the sections
                            break;
                        }
                    }
                }
                else
                {
                    // No complex content, return empty list
                    rectangles = new List<Rect>();
                }
            }
            return rectangles;
        }

        //-------------------------------------------------------------------
        // Hit tests to the correct IInputElement within the section that the 
        // mouse is over.
        //-------------------------------------------------------------------
        private IInputElement InputHitTestSection(
            PTS.FSPOINT pt, 
            ref PTS.FSSECTIONDESCRIPTION sectionDesc)
        {
            IInputElement ie = null;

            // Get section details
            PTS.FSSECTIONDETAILS sectionDetails;
            PTS.Validate(PTS.FsQuerySectionDetails(PtsContext.Context, sectionDesc.pfssection, out sectionDetails));

            // There are 2 types of sections:
            // (1) with page notes - footnotes in section treated as endnotes
            // (2) with column notes - footnotes in section treated as column notes
            if (PTS.ToBoolean(sectionDetails.fFootnotesAsPagenotes))
            {
                // (1) with page notes - footnotes in section treated as endnotes
                ErrorHandler.Assert(sectionDetails.u.withpagenotes.cEndnoteColumns == 0, ErrorHandler.NotSupportedFootnotes);

                //  Need to impl. Extended multi-column layout.
                Debug.Assert(sectionDetails.u.withpagenotes.cSegmentDefinedColumnSpanAreas == 0);
                Debug.Assert(sectionDetails.u.withpagenotes.cHeightDefinedColumnSpanAreas == 0);

                // cBasicColumns == 0, means that section content is empty.
                // In such case there is nothing to hit-test.
                if (sectionDetails.u.withpagenotes.cBasicColumns != 0)
                {
                    // Retrieve description for each column.
                    PTS.FSTRACKDESCRIPTION [] arrayColumnDesc;
                    PtsHelper.TrackListFromSection(PtsContext, sectionDesc.pfssection, ref sectionDetails, out arrayColumnDesc);

                    // Hittest each column
                    for (int index = 0; index < arrayColumnDesc.Length; index++)
                    {
                        if (arrayColumnDesc[index].fsrc.Contains(pt))
                        {
                            ie = PtsHelper.InputHitTestTrack(PtsContext, pt, ref arrayColumnDesc[index]);
                            break;
                        }
                    }
                }
            }
            else
            {
                // (2) with column notes - footnotes in section treated as column notes
                ErrorHandler.Assert(false, ErrorHandler.NotSupportedCompositeColumns);
            }

            return ie;
        }

        //-------------------------------------------------------------------
        // Returns ArrayList of rectangles for the ContentElement e in the 
        // section if it is found in the section. Returns empty ArrayList 
        // if section does not contain e. e may span multiple tracks in which
        // case we will have more than one rectangle
        // start: int representing start offset of e
        // length: int representing number of positions occupied by e 
        //-------------------------------------------------------------------
        private List<Rect> GetRectanglesInSection(
            ContentElement e,
            int start,
            int length,
            ref PTS.FSSECTIONDESCRIPTION sectionDesc)
        {
            // Get section details
            PTS.FSSECTIONDETAILS sectionDetails;
            PTS.Validate(PTS.FsQuerySectionDetails(PtsContext.Context, sectionDesc.pfssection, out sectionDetails));

            // Declare ArrayList to be returned
            List<Rect> rectangles = new List<Rect>();

            // There are 2 types of sections:
            // (1) with page notes - footnotes in section treated as endnotes
            // (2) with column notes - footnotes in section treated as column notes
            if (PTS.ToBoolean(sectionDetails.fFootnotesAsPagenotes))
            {
                // (1) with page notes - footnotes in section treated as endnotes
                ErrorHandler.Assert(sectionDetails.u.withpagenotes.cEndnoteColumns == 0, ErrorHandler.NotSupportedFootnotes);

                //      Need to impl. Extended multi-column layout.
                Debug.Assert(sectionDetails.u.withpagenotes.cSegmentDefinedColumnSpanAreas == 0);
                Debug.Assert(sectionDetails.u.withpagenotes.cHeightDefinedColumnSpanAreas == 0);

                // cBasicColumns == 0, means that section content is empty.
                // In such case there is nothing to hit-test.
                if (sectionDetails.u.withpagenotes.cBasicColumns != 0)
                {
                    // Retrieve description for each column.
                    PTS.FSTRACKDESCRIPTION[] arrayColumnDesc;
                    PtsHelper.TrackListFromSection(PtsContext, sectionDesc.pfssection, ref sectionDetails, out arrayColumnDesc);

                    // Check each column for element or part of element - element may span multiple
                    // columns/tracks
                    for (int index = 0; index < arrayColumnDesc.Length; index++)
                    {
                        // See if any rectangles for the element are found in this track
                        List<Rect> trackRectangles = PtsHelper.GetRectanglesInTrack(PtsContext, e, start, length, ref arrayColumnDesc[index]);
                        
                        // For consistency, rectangles collection is never null, only empty
                        Invariant.Assert(trackRectangles != null);
                        if (trackRectangles.Count != 0)
                        {
                            // Add rectangles found in this track to rectangles for element
                            rectangles.AddRange(trackRectangles);
                        }
                    }
                }
            }
            else
            {
                // (2) with column notes - footnotes in section treated as column notes
                ErrorHandler.Assert(false, ErrorHandler.NotSupportedCompositeColumns);
            }

            return rectangles;
        }

        // ------------------------------------------------------------------
        // Destroy page - release unmanaged resources.
        // ------------------------------------------------------------------
        private void DestroyPage()
        {
            if (_ptsPage != IntPtr.Zero)
            {
                PtsContext.OnPageDisposed(_ptsPage, true, false);
                _ptsPage = IntPtr.Zero;
            }
        }

        #endregion Private Methods

        // ------------------------------------------------------------------
        //
        //  Private Properties
        //
        // ------------------------------------------------------------------

        #region Private Properties

        private bool IsEmpty
        {
            get
            {
                return (_ptsPage == IntPtr.Zero); 
            }
        }

        #endregion Private Properties

        // ------------------------------------------------------------------
        //
        //  Private Fields
        //
        // ------------------------------------------------------------------

        #region Private Fields

        // ------------------------------------------------------------------
        // PTS section - root of the NameTable.
        // ------------------------------------------------------------------
        private readonly Section _section;

        //-------------------------------------------------------------------
        // BreakRecord indicating break position of the page.
        //-------------------------------------------------------------------
        private PageBreakRecord _breakRecord;

        //-------------------------------------------------------------------
        // Visual node representing content of the page.
        //-------------------------------------------------------------------
        private ContainerVisual _visual;

        //-------------------------------------------------------------------
        // Handle to pending background format operation
        //-------------------------------------------------------------------
        private DispatcherOperation _backgroundFormatOperation;

        // ------------------------------------------------------------------
        // Calculated size of the page.
        // ------------------------------------------------------------------
        private Size _calculatedSize = new Size();

        // ------------------------------------------------------------------
        // Content size of the page.
        // ------------------------------------------------------------------
        private Size _contentSize = new Size();

        // ------------------------------------------------------------------
        // Context the current page provides for its content.
        // ------------------------------------------------------------------
        private PageContext _pageContextOfThisPage = new PageContext(); 


        // ------------------------------------------------------------------
        // PTS page object.
        // ------------------------------------------------------------------
        private IntPtr _ptsPage;

        // ------------------------------------------------------------------
        // Is it finite page?
        // ------------------------------------------------------------------
        private bool _finitePage;

        //-------------------------------------------------------------------
        // Is during incremental update mode?
        //-------------------------------------------------------------------
        private bool _incrementalUpdate;

        //-------------------------------------------------------------------
        // Is being used in a plain text box?
        //-------------------------------------------------------------------
        internal bool _useSizingWorkaroundForTextBox;

        // ------------------------------------------------------------------
        // Is object already disposed.
        // ------------------------------------------------------------------
        private bool _disposed;

        #endregion Private Fields
    }

    // ----------------------------------------------------------------------
    // A page context object represents the current page or subpage being formatted.
    // It allows for collection of floating elements, and provides sizing information for RTL mirroring.
    // ----------------------------------------------------------------------
    internal class PageContext
    {
        internal PTS.FSRECT PageRect { get { return _pageRect; } set { _pageRect = value; } }

        internal List<BaseParaClient> FloatingElementList { get { return _floatingElementList; } }

        internal void AddFloatingParaClient(BaseParaClient floatingElement)
        {
            if(_floatingElementList == null)
            {
                _floatingElementList = new List<BaseParaClient>();
            }

            if(!_floatingElementList.Contains(floatingElement))
            {
                _floatingElementList.Add(floatingElement);
            }
        }

        internal void RemoveFloatingParaClient(BaseParaClient floatingElement)
        {
            if(_floatingElementList.Contains(floatingElement))
            {
                _floatingElementList.Remove(floatingElement);
            }

            if(_floatingElementList.Count == 0)
            {
                _floatingElementList = null;
            }
        }

        // ------------------------------------------------------------------
        // Floating element list
        // ------------------------------------------------------------------
        private List<BaseParaClient> _floatingElementList;

        // ------------------------------------------------------------------
        // Page rect
        // ------------------------------------------------------------------
        private PTS.FSRECT _pageRect;
    }
}
