// CycleStub.PresentationUI：MS.Internal.Documents.FindToolBar 的最小替身。
//
// 结构性理由与运行期语义见本工程 .csproj 头部注释（上游 CycleBreakers 目录在本快照缺失
// → PresentationUI-PresentationFramework-impl-cycle.csproj 不可移植；FindToolBar 是带 BAML 的控件）。
//
// 成员逐项来自真源码 PresentationUI/MS/Internal/Documents/FindToolBar.xaml.cs：
//   :27  ctor                 public FindToolBar()
//   :45  SearchText           public string SearchText { get; }            （真类只有 getter）
//   :57  SearchUp             public bool   SearchUp   { get; set; }
//   :77  MatchCase            public bool   MatchCase  { get; set; }
//   :87  MatchWholeWord       public bool   MatchWholeWord { get; set; }
//   :95  MatchDiacritic       public bool   MatchDiacritic { get; set; }
//   :104 MatchKashida         public bool   MatchKashida   { get; set; }
//   :113 MatchAlefHamza       public bool   MatchAlefHamza { get; set; }
//   :123 DocumentLoaded       public bool   DocumentLoaded { get; set; }
//   :139 FindEnabled          public bool   FindEnabled { get; }
//   :159 FindClicked          public event EventHandler FindClicked;
//   :174 GoToTextBox()        public void GoToTextBox()
//
// PresentationFramework 实测访问的成员（MS/Internal/documents/DocumentViewerHelper.cs:51-103、
// FlowDocument*Viewer/SinglePageViewer/FlowDocumentReader 的按键处理）：
//   SetResourceReference / FindClicked(+=,-=) / DocumentLoaded(set) / GoToTextBox() /
//   SearchUp(get) / MatchCase / MatchWholeWord / MatchDiacritic / MatchKashida / MatchAlefHamza / SearchText(get)
// —— 全部覆盖。
//
// 基类：真类为 System.Windows.Controls.ToolBar（FindToolBar.xaml 根元素 `<ToolBar>`），
// 属 PresentationFramework；替身若继承它会形成「PF → 本替身 → 真 PF」的编译期循环，
// 故取 System.Windows.UIElement（PresentationCore）并自声明 PF 需要的 SetResourceReference。
// 这是本替身与真类**唯一的形状偏离**，已在 .csproj 与文档中标注。

using System;
using System.Runtime.CompilerServices;

// 真源码：PresentationUI/OtherAssemblyAttrs.cs:7
//   [assembly: InternalsVisibleTo(BuildInfo.PresentationFramework)]
// FindToolBar 是 internal 类型，PresentationFramework 靠这条 IVT 访问它（PF 用 WCP 公钥签名 → 匹配）。
[assembly: InternalsVisibleTo("PresentationFramework, PublicKey=0024000004800000940000000602000000240000525341310004000001000100b5fc90e7027f67871e773a8fde8938c81dd402ba65b9201d60593e96c492651e889cc13f1415ebb53fac1131ae0bd333c5ee6021672d9718ea31a8aebd0da0072f25d87dba6fc90ffd598ed4da35e44c398c454307e8e33b8426143daec9f596836f97c8f74750e5975c64e2189f45def46b2a2b1247adc3652bf5c308055da9")]

namespace MS.Internal.Documents
{
    internal partial class FindToolBar : System.Windows.UIElement
    {
        public FindToolBar()
        {
        }

        // 真源码：SearchText 只有 getter（读 FindTextBox.Text）
        public string SearchText { get { return string.Empty; } }

        public bool SearchUp { get; set; }
        public bool MatchCase { get; set; }
        public bool MatchWholeWord { get; set; }
        public bool MatchDiacritic { get; set; }
        public bool MatchKashida { get; set; }
        public bool MatchAlefHamza { get; set; }
        public bool DocumentLoaded { get; set; }
        public bool FindEnabled { get { return DocumentLoaded; } }

        public event EventHandler FindClicked;

        public void GoToTextBox()
        {
        }

        /// <summary>
        /// 真类从 System.Windows.FrameworkElement 继承此成员（本替身不继承 PF，故显式声明同名同签名）。
        /// PF 调用点：MS/Internal/documents/DocumentViewerHelper.cs:51。
        /// </summary>
        public void SetResourceReference(System.Windows.DependencyProperty dp, object name)
        {
        }
    }
}

namespace System.Windows.Documents
{
    /// <summary>
    /// 真源码：PresentationUI/PresentationUIStyleResources.cs:10（public static class，空类，仅供
    /// ComponentResourceKey 取 typeof）。消费方：PF 的 FlowDocumentReader/DocumentViewerHelper/
    /// StickyNote/FlowDocumentScrollViewer/SinglePageViewer（共 8 处 `typeof(PresentationUIStyleResources)`）。
    /// </summary>
    public static class PresentationUIStyleResources
    {
    }
}

namespace MS.Internal.Documents.Application
{
    /// <summary>
    /// 真源码：PresentationUI/MS/Internal/Documents/Application/DocumentApplicationState.cs:11
    /// （[Serializable] internal struct，4 个 ctor 参数 + 4 个属性照抄）。
    /// 消费方：PresentationFramework/MS/Internal/documents/Application/DocumentApplicationJournalEntry.cs。
    /// </summary>
    [Serializable]
    internal struct DocumentApplicationState
    {
        public DocumentApplicationState(double zoom, double horizontalOffset, double verticalOffset, int maxPagesAcross)
        {
            _zoom = zoom;
            _horizontalOffset = horizontalOffset;
            _verticalOffset = verticalOffset;
            _maxPagesAcross = maxPagesAcross;
        }

        public double Zoom { get { return _zoom; } }
        public double HorizontalOffset { get { return _horizontalOffset; } }
        public double VerticalOffset { get { return _verticalOffset; } }
        public int MaxPagesAcross { get { return _maxPagesAcross; } }

        private double _zoom;
        private double _horizontalOffset;
        private double _verticalOffset;
        private int _maxPagesAcross;
    }
}

namespace MS.Internal.Documents
{
    /// <summary>
    /// 真源码：PresentationUI/MS/Internal/Documents/DocumentApplicationDocumentViewer.cs:26
    ///   internal sealed class DocumentApplicationDocumentViewer : DocumentViewer（DocumentViewer 属 PF）
    /// 消费方：PF 只用 `as` / `is` / typeof() + 两个成员
    ///         （DocumentApplicationJournalEntry.cs:66-90、DocumentGridContextMenu.cs:30,73）。
    /// 替身不继承 PF 的 DocumentViewer（会形成编译期循环），基类取 System.Windows.UIElement；
    /// PF 未对它做 DocumentViewer 方向的转换或成员访问，故不影响编译与类型判断。
    /// </summary>
    internal sealed class DocumentApplicationDocumentViewer : System.Windows.UIElement
    {
        // 真源码 DocumentApplicationDocumentViewer.cs:150（类型在 MS.Internal.Documents.Application 下，故此处限定）
        public Application.DocumentApplicationState StoredDocumentApplicationState { get; set; }
        public void SetUIToStoredState() { }
    }
}