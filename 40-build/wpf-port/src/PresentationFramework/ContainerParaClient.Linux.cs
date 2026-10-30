// ⚠️ 本文件由 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/reapply-patches.py **生成**，不要手改。
//
// 内容 = 上游 `src/Microsoft.DotNet.Wpf/src/PresentationFramework/MS/Internal/PtsHost/ContainerParaClient.cs` 逐字复制 + 2 处 W86A（`TASK-0304`/`TASK-0305`）改动。
// 每次运行该脚本都会从上游重读重生成；needle 找不到 / 命中数不符时**报错退出**
// （不会静默产出未打补丁的副本）。改动逐处见：
//   E1 ／ E2
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
// Description: ContainerParaClient is responsible for handling display
//              related data of paragraph containers.
//


using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Documents;
using MS.Internal.Documents;
using MS.Internal.Text;

using MS.Internal.PtsHost.UnsafeNativeMethods;

namespace MS.Internal.PtsHost
{
    /// <summary>
    /// ContainerParaClient is responsible for handling display related data 
    /// of paragraph containers. 
    /// </summary>
    internal class ContainerParaClient : BaseParaClient
    {
        /// <summary>
        /// Constructor. 
        /// </summary>
        /// <param name="paragraph">
        /// Paragraph associated with this object.
        /// </param>
        internal ContainerParaClient(ContainerParagraph paragraph) : base(paragraph)
        {
        }

        /// <summary>
        /// Arrange paragraph.
        /// </summary>
        protected override void OnArrange()
        {
            base.OnArrange();

            // Query paragraph details
            PTS.FSSUBTRACKDETAILS subtrackDetails;
            PTS.Validate(PTS.FsQuerySubtrackDetails(PtsContext.Context, _paraHandle, out subtrackDetails));

            // Adjust rectangle and offset to take into account MBPs
            MbpInfo mbp = MbpInfo.FromElement(Paragraph.Element, Paragraph.StructuralCache.TextFormatterHost.PixelsPerDip);

            if(ParentFlowDirection != PageFlowDirection)
            {
                mbp.MirrorMargin();
            }

            _rect.u += mbp.MarginLeft;
            _rect.du -= mbp.MarginLeft + mbp.MarginRight;

            _rect.du = Math.Max(TextDpi.ToTextDpi(TextDpi.MinWidth), _rect.du); 
            _rect.dv = Math.Max(TextDpi.ToTextDpi(TextDpi.MinWidth), _rect.dv); 

            uint fswdirSubtrack = PTS.FlowDirectionToFswdir(_flowDirection);

            // There is possibility to get empty track.
            if (subtrackDetails.cParas != 0)
            {
                // Get list of paragraphs
                PTS.FSPARADESCRIPTION [] arrayParaDesc;
                PtsHelper.ParaListFromSubtrack(PtsContext, _paraHandle, ref subtrackDetails, out arrayParaDesc);

                PtsHelper.ArrangeParaList(PtsContext, subtrackDetails.fsrc, arrayParaDesc, fswdirSubtrack);
            }
        }

        /// <summary>
        /// Hit tests to the correct IInputElement within the paragraph
        /// that the mouse is over.
        /// </summary>
        /// <param name="pt">
        /// Point which gives location of Hit test
        /// </param>
        internal override IInputElement InputHitTest(PTS.FSPOINT pt)
        {
            IInputElement ie = null;

            // Query paragraph details
            PTS.FSSUBTRACKDETAILS subtrackDetails;
            PTS.Validate(PTS.FsQuerySubtrackDetails(PtsContext.Context, _paraHandle, out subtrackDetails));

            // Hittest subtrack content.

            // There might be possibility to get empty sub-track, skip the sub-track
            // in such case.
            if (subtrackDetails.cParas != 0)
            {
                // Get list of paragraphs
                PTS.FSPARADESCRIPTION [] arrayParaDesc;
                PtsHelper.ParaListFromSubtrack(PtsContext, _paraHandle, ref subtrackDetails, out arrayParaDesc);

                // Render list of paragraphs
                ie = PtsHelper.InputHitTestParaList(PtsContext, pt, ref subtrackDetails.fsrc, arrayParaDesc);
            }

            // If nothing is hit, return the owner of the paragraph.
            if (ie == null && _rect.Contains(pt))
            {
                ie = Paragraph.Element as IInputElement;
            }

            return ie;
        }

        /// <summary>
        /// Returns ArrayList of rectangles for the given ContentElement e if 
        /// e lies in this client. Otherwise returns empty list. 
        /// </summary>
        /// <param name="e">
        /// ContentElement for which rectangles are needed
        /// </param>
        /// <param name="start">
        /// Int representing start offset of e.
        /// </param>
        /// <param name="length">
        /// int representing number of positions occupied by e.
        /// </param>
        internal override List<Rect> GetRectangles(ContentElement e, int start, int length)
        {
            List<Rect> rectangles = new List<Rect>();

            if (this.Paragraph.Element as ContentElement == e)
            {
                // We have found the element. Return rectangles for this paragraph.
                GetRectanglesForParagraphElement(out rectangles);
            }
            else
            {
                // Element not found as Paragraph.Element. Look inside

                // Query paragraph details
                PTS.FSSUBTRACKDETAILS subtrackDetails;
                PTS.Validate(PTS.FsQuerySubtrackDetails(PtsContext.Context, _paraHandle, out subtrackDetails));

                // There might be possibility to get empty sub-track, skip the sub-track
                // in such case.
                if (subtrackDetails.cParas != 0)
                {
                    // Get list of paragraphs
                    // No changes to offset, since there are no subpages generated, only lists of paragraphs
                    PTS.FSPARADESCRIPTION[] arrayParaDesc;
                    PtsHelper.ParaListFromSubtrack(PtsContext, _paraHandle, ref subtrackDetails, out arrayParaDesc);

                    // Render list of paragraphs
                    rectangles = PtsHelper.GetRectanglesInParaList(PtsContext, e, start, length, arrayParaDesc);
                }
                else
                {
                    rectangles = new List<Rect>();
                }
            }

            // Rectangles must be non-null
            Invariant.Assert(rectangles != null);    
            return rectangles;
        }

        /// <summary>
        /// Validate visual node associated with paragraph.
        /// </summary>
        /// <param name="fskupdInherited">
        /// Inherited update info
        /// </param>
        internal override void ValidateVisual(PTS.FSKUPDATE fskupdInherited)
        {
            WpfLinuxChainProbe.Hit("CON.ValidateVisual", "parah=" + WpfLinuxChainProbe.Hx(_paraHandle) + " inh=" + (int)fskupdInherited);
            // Query paragraph details
            PTS.FSSUBTRACKDETAILS subtrackDetails;
            PTS.Validate(PTS.FsQuerySubtrackDetails(PtsContext.Context, _paraHandle, out subtrackDetails));

            // Draw border and background info.

            // Adjust rectangle and offset to take into account MBPs
            MbpInfo mbp = MbpInfo.FromElement(Paragraph.Element, Paragraph.StructuralCache.TextFormatterHost.PixelsPerDip);

            if(ThisFlowDirection != PageFlowDirection)
            {
                mbp.MirrorBP();
            }

            Brush backgroundBrush = (Brush)Paragraph.Element.GetValue(TextElement.BackgroundProperty);
            _visual.DrawBackgroundAndBorder(backgroundBrush, mbp.BorderBrush, mbp.Border, _rect.FromTextDpi(), IsFirstChunk, IsLastChunk);

            // There might be possibility to get empty sub-track, skip the sub-track in such case.
            if (subtrackDetails.cParas != 0)
            {
                // Get list of paragraphs
                PTS.FSPARADESCRIPTION [] arrayParaDesc;
                PtsHelper.ParaListFromSubtrack(PtsContext, _paraHandle, ref subtrackDetails, out arrayParaDesc);

                // Render list of paragraphs
                PtsHelper.UpdateParaListVisuals(PtsContext, _visual.Children, fskupdInherited, arrayParaDesc);
            }
            else
            {
                _visual.Children.Clear();
            }
        }

        /// <summary>
        /// Updates the viewport for this para
        /// </summary>
        /// <param name="viewport">
        /// Fsrect with viewport info
        /// </param>
        internal override void UpdateViewport(ref PTS.FSRECT viewport)
        {
            WpfLinuxChainProbe.Hit("CON.UpdateViewport", "parah=" + WpfLinuxChainProbe.Hx(_paraHandle) + " vp=" + viewport.u + "," + viewport.v + "," + viewport.du + "," + viewport.dv);
            // Query paragraph details
            PTS.FSSUBTRACKDETAILS subtrackDetails;
            PTS.Validate(PTS.FsQuerySubtrackDetails(PtsContext.Context, _paraHandle, out subtrackDetails));

            // There might be possibility to get empty sub-track, skip the sub-track in such case.
            if (subtrackDetails.cParas != 0)
            {
                // Get list of paragraphs
                PTS.FSPARADESCRIPTION [] arrayParaDesc;
                PtsHelper.ParaListFromSubtrack(PtsContext, _paraHandle, ref subtrackDetails, out arrayParaDesc);

                // Render list of paragraphs
                PtsHelper.UpdateViewportParaList(PtsContext, arrayParaDesc, ref viewport);
            }
        }

        /// <summary>
        /// Create and return paragraph result representing this paragraph.
        /// </summary>
        internal override ParagraphResult CreateParagraphResult()
        {
            /*
            // Query paragraph details
            PTS.FSSUBTRACKDETAILS subtrackDetails;
            PTS.Validate(PTS.FsQuerySubtrackDetails(PtsContext.Context, _paraHandle, out subtrackDetails));

            // If there is just one paragraph, do not create container. Return just this paragraph.
            if (subtrackDetails.cParas == 1)
            {
                // Get list of paragraphs
                PTS.FSPARADESCRIPTION [] arrayParaDesc;
                PtsHelper.ParaListFromSubtrack(_paraHandle, ref subtrackDetails, out arrayParaDesc);

                BaseParaClient paraClient = PtsContext.HandleToObject(arrayParaDesc[0].pfsparaclient) as BaseParaClient;
                PTS.ValidateHandle(paraClient);
                return paraClient.CreateParagraphResult();
            }
            */
            return new ContainerParagraphResult(this);
        }

        /// <summary>
        /// Return TextContentRange for the content of the paragraph.
        /// </summary>
        internal override TextContentRange GetTextContentRange()
        {
            TextElement elementOwner = this.Paragraph.Element as TextElement;
            TextContentRange textContentRange;
            BaseParaClient paraClient;
            PTS.FSSUBTRACKDETAILS subtrackDetails;
            PTS.FSPARADESCRIPTION[] arrayParaDesc;
            
            Invariant.Assert(elementOwner != null, "Expecting TextElement as owner of ContainerParagraph.");

            // Query paragraph details
            PTS.Validate(PTS.FsQuerySubtrackDetails(PtsContext.Context, _paraHandle, out subtrackDetails));

            // If container is empty, return range for the entire element.
            // If the beginning and the end of content of the paragraph is 
            // part of this ParaClient, return range for the entire element.
            // Otherwise combine ranges from all nested paragraphs.
            if (subtrackDetails.cParas == 0 || (_isFirstChunk && _isLastChunk))
            {
                textContentRange = TextContainerHelper.GetTextContentRangeForTextElement(elementOwner);
            }
            else
            {
                PtsHelper.ParaListFromSubtrack(PtsContext, _paraHandle, ref subtrackDetails, out arrayParaDesc);

                // Merge TextContentRanges for all paragraphs
                textContentRange = new TextContentRange();
                for (int i = 0; i < arrayParaDesc.Length; i++)
                {
                    paraClient = Paragraph.StructuralCache.PtsContext.HandleToObject(arrayParaDesc[i].pfsparaclient) as BaseParaClient;
                    PTS.ValidateHandle(paraClient);
                    textContentRange.Merge(paraClient.GetTextContentRange());
                }

                // If the first paragraph is the first paragraph in the container and it is the first chunk, 
                // include start position of this element.
                if (_isFirstChunk)
                {
                    textContentRange.Merge(TextContainerHelper.GetTextContentRangeForTextElementEdge(
                        elementOwner, ElementEdge.BeforeStart));
                }

                // If the last paragraph is the last paragraph in the container and it is the last chunk, 
                // include end position of this element.
                if (_isLastChunk)
                {
                    textContentRange.Merge(TextContainerHelper.GetTextContentRangeForTextElementEdge(
                        elementOwner, ElementEdge.AfterEnd));
                }
            }

            Invariant.Assert(textContentRange != null);
            return textContentRange;
        }

        /// <summary>
        /// Returns a new colleciton of ParagraphResults for the contained paragraphs.
        /// </summary>
        internal ReadOnlyCollection<ParagraphResult> GetChildrenParagraphResults(out bool hasTextContent)
        {
#if TEXTPANELLAYOUTDEBUG
            TextPanelDebug.IncrementCounter("ContainerPara.GetParagraphs", TextPanelDebug.Category.TextView);
#endif
            // Query paragraph details
            PTS.FSSUBTRACKDETAILS subtrackDetails;
            PTS.Validate(PTS.FsQuerySubtrackDetails(PtsContext.Context, _paraHandle, out subtrackDetails));

            // hasTextContent is set to true if any of the children paragraphs has text content, not just attached objects
            hasTextContent = false;

            if (subtrackDetails.cParas == 0) 
            {
                return ReadOnlyCollection<ParagraphResult>.Empty;
            }

            // Get list of paragraphs
            PTS.FSPARADESCRIPTION [] arrayParaDesc;
            PtsHelper.ParaListFromSubtrack(PtsContext, _paraHandle, ref subtrackDetails, out arrayParaDesc);

            List<ParagraphResult> paragraphResults = new List<ParagraphResult>(arrayParaDesc.Length);
            for (int i = 0; i < arrayParaDesc.Length; i++)
            {
                BaseParaClient paraClient = PtsContext.HandleToObject(arrayParaDesc[i].pfsparaclient) as BaseParaClient;
                PTS.ValidateHandle(paraClient);
                ParagraphResult paragraphResult = paraClient.CreateParagraphResult();
                if (paragraphResult.HasTextContent)
                {
                    hasTextContent = true;
                }
                paragraphResults.Add(paragraphResult);
            }
            return new ReadOnlyCollection<ParagraphResult>(paragraphResults);
        }

        /// <summary>
        /// Update information about first/last chunk.
        /// </summary>
        /// <param name="isFirstChunk">
        /// True if para is first chunk of paginated content
        /// </param>
        /// <param name="isLastChunk">
        /// True if para is last chunk of paginated content
        /// </param>
        internal void SetChunkInfo(bool isFirstChunk, bool isLastChunk)
        {
            _isFirstChunk = isFirstChunk;
            _isLastChunk = isLastChunk;
        }

        /// <summary>
        /// Returns baseline for first text line
        /// </summary>
        internal override int GetFirstTextLineBaseline()
        {
            PTS.FSSUBTRACKDETAILS subtrackDetails;
            PTS.Validate(PTS.FsQuerySubtrackDetails(PtsContext.Context, _paraHandle, out subtrackDetails));

            if (subtrackDetails.cParas == 0) 
            {
                return _rect.v;
            }

            // Get list of paragraphs
            PTS.FSPARADESCRIPTION [] arrayParaDesc;
            PtsHelper.ParaListFromSubtrack(PtsContext, _paraHandle, ref subtrackDetails, out arrayParaDesc);

            BaseParaClient paraClient = PtsContext.HandleToObject(arrayParaDesc[0].pfsparaclient) as BaseParaClient;
            PTS.ValidateHandle(paraClient);

            return paraClient.GetFirstTextLineBaseline();
        }

        /// <summary>
        /// Returns tight bounding path geometry.
        /// </summary>
        internal Geometry GetTightBoundingGeometryFromTextPositions(ITextPointer startPosition, ITextPointer endPosition, Rect visibleRect)
        {
            bool hasTextContent;
            ReadOnlyCollection<ParagraphResult> paragraphs = GetChildrenParagraphResults(out hasTextContent);
            Invariant.Assert(paragraphs != null, "Paragraph collection is null.");

            if (paragraphs.Count > 0)
            {
                return (TextDocumentView.GetTightBoundingGeometryFromTextPositionsHelper(paragraphs, startPosition, endPosition, TextDpi.FromTextDpi(_dvrTopSpace), visibleRect));
            }

            return null;
        }

        /// <summary>
        /// Is this the first chunk of paginated content.
        /// </summary>
        internal override bool IsFirstChunk 
        { 
            get 
            { 
                return _isFirstChunk; 
            } 
        }
        private bool _isFirstChunk;

        /// <summary>
        /// Is this the last chunk of paginated content.
        /// </summary>
        internal override bool IsLastChunk 
        { 
            get 
            { 
                return _isLastChunk; 
            } 
        }
        private bool _isLastChunk;
    }
}
