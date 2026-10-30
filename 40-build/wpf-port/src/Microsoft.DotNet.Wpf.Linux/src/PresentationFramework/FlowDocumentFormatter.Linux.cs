// ⚠️ 本文件由 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/reapply-patches.py **生成**，不要手改。
//
// 内容 = 上游 `src/Microsoft.DotNet.Wpf/src/PresentationFramework/MS/Internal/documents/FlowDocumentFormatter.cs` 逐字复制 + 3 处 W86A（`TASK-0304`/`TASK-0305`）改动。
// 每次运行该脚本都会从上游重读重生成；needle 找不到 / 命中数不符时**报错退出**
// （不会静默产出未打补丁的副本）。改动逐处见：
//   E1 ／ E2 ／ E3
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
// Description: Bottomless content formatter associated with FlowDocument.
//

using System.Windows;               // Size
using System.Windows.Documents;     // FlowDocument
using MS.Internal.PtsHost;          // FlowDocumentPage
using MS.Internal.PtsHost.UnsafeNativeMethods;  // PTS

namespace MS.Internal.Documents
{
    /// <summary>
    /// Bottomless content formatter associated with FlowDocument.
    /// </summary>
    internal class FlowDocumentFormatter : IFlowDocumentFormatter
    {
        //-------------------------------------------------------------------
        //
        //  Constructors
        //
        //-------------------------------------------------------------------

        #region Constructors

        /// <summary>
        /// Constructor
        /// </summary>
        internal FlowDocumentFormatter(FlowDocument document)
        {
            _document = document;
            _documentPage = new FlowDocumentPage(_document.StructuralCache);
        }

        #endregion Constructors

        //-------------------------------------------------------------------
        //
        //  Internal Methods
        //
        //-------------------------------------------------------------------

        #region Internal Methods

        /// <summary>
        /// Formatts content.
        /// </summary>
        /// <param name="constraint">Constraint size.</param>
        internal void Format(Size constraint)
        {
            Thickness pageMargin;
            Size pageSize;

            // Reentrancy check.
            if (_document.StructuralCache.IsFormattingInProgress)
            {
                throw new InvalidOperationException(SR.FlowDocumentFormattingReentrancy);
            }
            if (_document.StructuralCache.IsContentChangeInProgress)
            {
                throw new InvalidOperationException(SR.TextContainerChangingReentrancyInvalid);
            }

            // Check if we can continue with formatting without nuking incremental udpate info.
            if (_document.StructuralCache.IsFormattedOnce)
            {
                if (!_lastFormatSuccessful)
                {
                    // We cannot resolve update info if last formatting was unsuccessful.
                    _document.StructuralCache.InvalidateFormatCache(true);
                }
                if (!_arrangedAfterFormat && (!_document.StructuralCache.ForceReformat || !_document.StructuralCache.DestroyStructure))
                {
                    // Need to clear update info by running arrange process.
                    // This is necessary, because Format may be called more than once
                    // before Arrange is called. But PTS is not able to merge update info.
                    // To protect against loosing incremental changes delta, need
                    // to arrange the page and create all necessary visuals.
                    _documentPage.Arrange(_documentPage.ContentSize);
                    _documentPage.EnsureValidVisuals();
                }
            }
            _arrangedAfterFormat = false;
            _lastFormatSuccessful = false;
            _isContentFormatValid = false;

            pageSize = ComputePageSize(constraint);
            pageMargin = ComputePageMargin();

            // Disable processing of the queue during blocking operations to prevent unrelated reentrancy.
            using (_document.Dispatcher.DisableProcessing())
            {
                _document.StructuralCache.IsFormattingInProgress = true; // Set reentrancy flag.
                try
                {
                    _document.StructuralCache.BackgroundFormatInfo.ViewportHeight = constraint.Height;
                    // ── `T-A45`（`LINEVIS-ON-SCREEN`）：**把"在屏页"从底流改为有限页** ──────────────
                    //  见生成器内该块的说明；`WPF_LINEVIS_ONSCREEN=0` ⇒ 逐字回上游（反极性腿）。
                    WpfLinuxOnScreenDrive.Format(pageSize, constraint, pageMargin, _documentPage);
                }
                finally
                {
                    _document.StructuralCache.IsFormattingInProgress = false; // Clear reentrancy flag.
                }
            }
            _lastFormatSuccessful = true;
        }

        /// <summary>
        /// Arranges content.
        /// </summary>
        /// <param name="arrangeSize">Size that element should use to arrange itself and its children.</param>
        /// <param name="viewport">Viewport for visible content.</param>
        internal void Arrange(Size arrangeSize, Rect viewport)
        {
            WpfLinuxChainProbe.Hit("FDF.Arrange", "size=" + WpfLinuxChainProbe.N(arrangeSize.Width) + "x" + WpfLinuxChainProbe.N(arrangeSize.Height) + " vp=" + WpfLinuxChainProbe.N(viewport.X) + "," + WpfLinuxChainProbe.N(viewport.Y) + "," + WpfLinuxChainProbe.N(viewport.Width) + "," + WpfLinuxChainProbe.N(viewport.Height));
            Invariant.Assert(_document.StructuralCache.DtrList == null || _document.StructuralCache.DtrList.Length == 0 ||
                             (_document.StructuralCache.DtrList.Length == 1 && _document.StructuralCache.BackgroundFormatInfo.DoesFinalDTRCoverRestOfText));

            // Arrange the content and create visual tree.
            _documentPage.Arrange(arrangeSize);
            _documentPage.EnsureValidVisuals();
            _arrangedAfterFormat = true;

            // Render content only for the current viewport.
            if (viewport.IsEmpty)
            {
                viewport = new Rect(0, 0, arrangeSize.Width, _document.StructuralCache.BackgroundFormatInfo.ViewportHeight);
            }
            PTS.FSRECT fsrectViewport = new PTS.FSRECT(viewport);
            _documentPage.UpdateViewport(ref fsrectViewport, true);

            _isContentFormatValid = true;
        }

        #endregion Internal Methods

        //-------------------------------------------------------------------
        //
        //  Internal Properties
        //
        //-------------------------------------------------------------------

        #region Internal Properties

        /// <summary>
        /// DocumentPage representing formatted content.
        /// </summary>
        internal FlowDocumentPage DocumentPage
        {
            get
            {
                return _documentPage;
            }
        }

        #endregion Internal Properties

        //-------------------------------------------------------------------
        //
        //  Internal Events
        //
        //-------------------------------------------------------------------

        #region Internal Events

        /// <summary>
        /// Fired when content has been invalidated.
        /// </summary>
        internal event EventHandler ContentInvalidated;

        /// <summary>
        /// Fired when formatter has been suspended.
        /// </summary>
        internal event EventHandler Suspended;

        #endregion Internal Events

        //-------------------------------------------------------------------
        //
        //  Private Methods
        //
        //-------------------------------------------------------------------

        #region Private Methods

        /// <summary>
        /// Compute size for the page.
        /// </summary>
        private Size ComputePageSize(Size constraint)
        {
            double max, min;
            Size pageSize = new Size(_document.PageWidth, double.PositiveInfinity);
            if (double.IsNaN(pageSize.Width))
            {
                pageSize.Width = constraint.Width;
                max = _document.MaxPageWidth;
                if (pageSize.Width > max)
                {
                    pageSize.Width = max;
                }
                min = _document.MinPageWidth;
                if (pageSize.Width < min)
                {
                    pageSize.Width = min;
                }
            }
            // If the width is Double.PositiveInfinity, crop it to predefined value.
            if (double.IsPositiveInfinity(pageSize.Width))
            {
                pageSize.Width = _defaultWidth;
            }
            return pageSize;
        }

        /// <summary>
        /// Compute margin for the page.
        /// </summary>
        private Thickness ComputePageMargin()
        {
            double lineHeight = MS.Internal.Text.DynamicPropertyReader.GetLineHeightValue(_document);
            Thickness pageMargin = _document.PagePadding;

            // If Padding value is 'Auto', treat it as 1*LineHeight.
            if (double.IsNaN(pageMargin.Left))
            {
                pageMargin.Left = lineHeight;
            }
            if (double.IsNaN(pageMargin.Top))
            {
                pageMargin.Top = lineHeight;
            }
            if (double.IsNaN(pageMargin.Right))
            {
                pageMargin.Right = lineHeight;
            }
            if (double.IsNaN(pageMargin.Bottom))
            {
                pageMargin.Bottom = lineHeight;
            }
            return pageMargin;
        }

        #endregion Private Methods

        //-------------------------------------------------------------------
        //
        //  Private Fields
        //
        //-------------------------------------------------------------------

        #region Private Fields

        /// <summary>
        /// FlowDocument associated with the paginator.
        /// </summary>
        private readonly FlowDocument _document;

        /// <summary>
        /// DocumentPage representing formatted content.
        /// </summary>
        private FlowDocumentPage _documentPage;

        /// <summary>
        /// Whether Arrange was called after formatting.
        /// </summary>
        private bool _arrangedAfterFormat;

        /// <summary>
        /// Whether last formatting was succesful.
        /// </summary>
        private bool _lastFormatSuccessful;

        /// <summary>
        /// Width used when no width is specified.
        /// </summary>
        private const double _defaultWidth = 500.0;

        /// <summary>
        /// Whether the current format for the content is valid
        /// </summary>
        private bool _isContentFormatValid = false;

        #endregion Private Fields

        //-------------------------------------------------------------------
        //
        //  IFlowDocumentFormatter Members
        //
        //-------------------------------------------------------------------

        #region IFlowDocumentFormatter Members

        /// <summary>
        /// Responds to change affecting entire content of associated FlowDocument.
        /// </summary>
        /// <param name="affectsLayout">Whether change affects layout.</param>
        void IFlowDocumentFormatter.OnContentInvalidated(bool affectsLayout)
        {
            // If change happens before we've been arranged, we need to do a full reformat
            if (affectsLayout)
            {
                if (!_arrangedAfterFormat)
                {
                    _document.StructuralCache.InvalidateFormatCache(true);
                }
                _isContentFormatValid = false;
            }

            if (ContentInvalidated != null)
            {
                ContentInvalidated(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Responds to change affecting entire content of associated FlowDocument.
        /// </summary>
        /// <param name="affectsLayout">Whether change affects layout.</param>
        /// <param name="start">Start of the affected content range.</param>
        /// <param name="end">End of the affected content range.</param>
        void IFlowDocumentFormatter.OnContentInvalidated(bool affectsLayout, ITextPointer start, ITextPointer end)
        {
            ((IFlowDocumentFormatter)this).OnContentInvalidated(affectsLayout);
        }

        /// <summary>
        /// Suspend formatting.
        /// </summary>
        void IFlowDocumentFormatter.Suspend()
        {
            if (Suspended != null)
            {
                Suspended(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Is layout data in a valid state.
        /// </summary>
        bool IFlowDocumentFormatter.IsLayoutDataValid
        {
            get
            {
                // Layout is clean only when the page is calculated and it
                // is in the clean state - there are no pending changes that affect layout.
                //
                // Hittest can be called with invalid arrange. This happens in
                // following situation:
                //   Something is causing to call InvalidateTree and eventually 
                //   InvalidateAllProperties will be called in such case. In responce
                //   to that following properties are invalidated:
                //   * ClipToBounds - invalidates arrange
                //   * IsEnabled - calls MouseDevice.Synchronize and it will eventually
                //     do hittesting.
                //
                // OR
                //   TextContainer sends Changing event, which invalidates measure,
                //   but we have not yet received a matching Changed event.
                //
                // So, it is possible to receive hittesting request on dirty layout.
                bool layoutValid = _documentPage != null &&
                    _document.StructuralCache.IsFormattedOnce &&
                    !_document.StructuralCache.ForceReformat &&
                    _isContentFormatValid &&
                    !_document.StructuralCache.IsContentChangeInProgress &&
                    !_document.StructuralCache.IsFormattingInProgress;

                return layoutValid;
            }
        }

        #endregion IFlowDocumentFormatter Members
    }

    /// <summary>
    /// `T-A45`（`LINEVIS-ON-SCREEN`）：**在屏页＝有限页**驱动（本移植的底流窗**排不出**附属对象内容，
    /// 见 `reapply-patches.py` 内该块的说明）。零假值：显式 `WPF_LINEVIS_ONSCREEN=0` ⇒ 逐字回上游。
    /// </summary>
    internal static class WpfLinuxOnScreenDrive
    {
        private const double MinimumPageHeight = 2000.0;

        internal static void Format(Size pageSize, Size constraint, Thickness pageMargin, FlowDocumentPage page)
        {
            if (!WpfLinuxChainProbe.EnvOn("WPF_LINEVIS_ONSCREEN"))
            {
                FormatBottomlessDegradable(pageSize, pageMargin, page, "ONS.FormatBottomless(upstream-leg)");
                return;
            }

            Size finiteSize = pageSize;
            double height = constraint.Height;
            if (double.IsNaN(height) || double.IsInfinity(height) || height < MinimumPageHeight)
            {
                height = MinimumPageHeight;
            }
            finiteSize.Height = height;

            WpfLinuxChainProbe.Hit("ONS.FormatFinite",
                "w=" + WpfLinuxChainProbe.N(finiteSize.Width) + " h=" + WpfLinuxChainProbe.N(finiteSize.Height));

            // ── `D-G206`：**托管侧降级** ────────────────────────────────────────────────
            //  本移植没有完整 PTS（native `win32_pts.c` 大量入口如实返回 `WPF_PTS_ERR_NOT_IMPLEMENTED`），
            //  `FormatFinite`（有限页）这条路会撞上未实现的入口 ⇒ 上游 `PTS.Validate()` 抛 `PtsException`。
            //  修前该异常冒泡到 UI 线程 ⇒ FlowDocument 类控件**整体不可用**（五种形态全炸）。
            //  这里降级为「**回退底流**（bottomless）」—— 底流是本移植"能出内容"的形态（滚动/编辑场景够用），
            //  只是分页不精确。零假值：`WPF_PTS_STRICT=1` ⇒ 原样重抛（反极性对照）；
            //  非 PTS 缺口的异常 ⇒ 同样原样重抛（不掩盖真 bug）。
            try
            {
                page.FormatFinite(finiteSize, pageMargin, null);
            }
            catch (Exception ex)
            {
                if (!PtsDegrade.ShouldSwallow(ex, "ONS.FormatFinite"))
                {
                    throw;
                }

                FormatBottomlessDegradable(pageSize, pageMargin, page, "ONS.FormatBottomless(fallback-after-Finite)");
            }
        }

        /// <summary>
        /// 底流格式化，带 `D-G206` 降级：PTS 缺口 ⇒ 吞掉（已记账）；`WPF_PTS_STRICT=1` 或非 PTS 异常 ⇒ 重抛。
        /// </summary>
        private static void FormatBottomlessDegradable(
            Size pageSize, Thickness pageMargin, FlowDocumentPage page, string where)
        {
            try
            {
                page.FormatBottomless(pageSize, pageMargin);
            }
            catch (Exception ex)
            {
                if (!PtsDegrade.ShouldSwallow(ex, where))
                {
                    throw;
                }
            }
        }
    }
}
