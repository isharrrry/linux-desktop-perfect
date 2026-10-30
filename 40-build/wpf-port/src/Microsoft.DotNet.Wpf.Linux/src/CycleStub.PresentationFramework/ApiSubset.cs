// CycleStub.PresentationFramework：PresentationFramework 侧的**最小 API 替身**（编译期专用）。
//
// 为什么需要：见本工程 .csproj 头部注释 —— 上游用 CycleBreakers 目录下的
//   PresentationFramework-System.Printing-api-cycle.csproj
//   PresentationFramework-ReachFramework-impl-cycle.csproj
// 提供 PF 侧类型以打破编译期循环，而该目录**在本仓库的上游快照里不存在** → 不可移植。
//
// 本文件的每个成员都来自上游真源码签名（出处逐条写在注释里），**没有任何凭记忆的形状**。
// 覆盖范围 = 两个消费方的实际需求：
//   · System.Printing/ref/System.Printing.cs：FixedPage / FixedDocument / FixedDocumentSequence /
//     DocumentPaginator(PC,不需要) / PageRange / PageRangeSelection / Serialization.*（均**只做类型引用**）
//   · ReachFramework（285 文件）：FixedPage.Width/.Height/.Language/.Background、
//     FixedPage.GetNavigateUri(static)、FixedDocument.Pages/.PrintTicket/.Uri、
//     FixedDocumentSequence.References/.PrintTicket、Serialization 抽象基类（真源码直接编译，见 csproj）
// 成员不足时编译器会立刻报出来（不静默）；新增成员一律注明真源码出处。
//
// ⚠ 保真度由「ReachFramework pass 2」保证：真 PresentationFramework.dll 产出后会把 RF 再编一遍，
//   编译器逐条验证 RF 对本替身的使用在真 PF 上依然成立（成员/继承差异 → 编译错误，不会留到运行期）。

using System;
using System.Collections;

namespace System.Windows
{
    /// <summary>
    /// 真源码：PresentationFramework/System/Windows/FrameworkElement.cs（public class FrameworkElement : UIElement, ...）。
    /// 替身只声明 RF 实测用到的成员（ReachSerializationUtils.cs:884 `fe.Name`）；
    /// RF 里还有大量 `is FrameworkElement` / `as FrameworkElement` 分支 —— 那些靠**继承链保真**（见下），
    /// 不靠成员：FixedPage/DocumentReference 在替身里就派生自本类型。
    /// </summary>
    public class FrameworkElement : System.Windows.UIElement
    {
        public string Name { get { return null; } set { } }
        public ResourceDictionary Resources { get { return null; } set { } }
        // 真源码 FrameworkElement.cs：public bool IsInitialized { get; } / public DependencyObject TemplatedParent { get; }
        public bool IsInitialized { get { return true; } }
        public System.Windows.DependencyObject TemplatedParent { get { return null; } }
        // 真源码 FrameworkElement.cs：public static readonly DependencyProperty LanguageProperty
        public static readonly System.Windows.DependencyProperty LanguageProperty = null;
    }

    /// <summary>真源码：PresentationFramework/System/Windows/FrameworkContentElement.cs（: ContentElement）。</summary>
    public class FrameworkContentElement : System.Windows.ContentElement
    {
        public string Name { get { return null; } set { } }
        public ResourceDictionary Resources { get { return null; } set { } }
        public bool IsInitialized { get { return true; } }
        // 真源码 FrameworkContentElement.cs：public static readonly DependencyProperty LanguageProperty
        // 消费方：ReachFramework/Serialization/manager/ReachIDocumentPaginatorSerializer.cs:109
        public static readonly System.Windows.DependencyProperty LanguageProperty = null;
    }

    /// <summary>真源码：PresentationFramework/System/Windows/ResourceDictionary.cs。RF 里只做 typeof() 比较，故空声明。</summary>
    public class ResourceDictionary : System.Collections.IEnumerable
    {
        // 消费方：ReachFramework/Serialization/VisualTreeFlattener.cs:773（foreach over page resources）
        public System.Collections.IEnumerator GetEnumerator() { return null; }
    }

    /// <summary>真源码：PresentationFramework/System/Windows/IFrameworkInputElement.cs（接口）。RF：NGCSerializer.cs:39 `o as IFrameworkInputElement`。</summary>
    public interface IFrameworkInputElement
    {
        // 真源码 IFrameworkInputElement.cs：string Name { get; set; }
        // 消费方：ReachFramework/Serialization/manager/NGCSerializer.cs:42
        string Name { get; set; }
    }
}

namespace System.Windows
{
    /// <summary>
    /// 真源码：PresentationFramework/System/Windows/LogicalTreeHelper.cs。
    /// 消费方：ReachFramework/Serialization/manager/ReachSerializationUtils.cs:857
    ///   LogicalTreeHelper.GetChildren(dependencyObject)
    /// </summary>
    public static class LogicalTreeHelper
    {
        public static System.Collections.IEnumerable GetChildren(System.Windows.DependencyObject dependencyObject) { return null; }
    }
}

namespace System.Windows.Markup
{
    /// <summary>
    /// 真源码：PresentationFramework/System/Windows/Markup/ParserContext.cs。
    /// 消费方：ReachFramework/Packaging/XpsDocument.cs:607-612（对象初始化器设置 BaseUri）。
    /// </summary>
    public class ParserContext
    {
        public Uri BaseUri { get { return null; } set { } }
    }

    /// <summary>
    /// 真源码：PresentationFramework/System/Windows/Markup/XamlReader.cs。
    /// 消费方：ReachFramework/Packaging/XpsDocument.cs:622
    ///   XamlReader.Load(stream, parserContext, useRestrictiveXamlReader: true)
    /// </summary>
    public static class XamlReader
    {
        public static object Load(System.IO.Stream stream, ParserContext parserContext, bool useRestrictiveXamlReader) { return null; }
    }
}

namespace System.Windows.Controls
{
    /// <summary>真源码：PresentationFramework/System/Windows/Controls/UIElementCollection.cs。RF 只把它当字段/参数类型（ReachUIElementCollectionSerializerAsync）。</summary>
    public class UIElementCollection : System.Collections.IEnumerable
    {
        // 消费方：ReachFramework/Serialization/manager/ReachSerializationUtils.cs:784（foreach over Children）
        public System.Collections.IEnumerator GetEnumerator() { return null; }
    }

    /// <summary>真源码：PresentationFramework/System/Windows/Controls/PageRanges.cs:17（4 个取值照抄）。</summary>
    public enum PageRangeSelection
    {
        AllPages,
        UserPages,
        CurrentPage,
        SelectedPages,
    }

    /// <summary>真源码：PresentationFramework/System/Windows/Controls/PageRanges.cs:41（struct + 两个 ctor + PageFrom/PageTo）。</summary>
    public struct PageRange
    {
        public PageRange(int page)
        {
            _pageFrom = page;
            _pageTo = page;
        }

        public PageRange(int pageFrom, int pageTo)
        {
            _pageFrom = pageFrom;
            _pageTo = pageTo;
        }

        public int PageFrom { get { return _pageFrom; } set { _pageFrom = value; } }
        public int PageTo { get { return _pageTo; } set { _pageTo = value; } }

        private int _pageFrom;
        private int _pageTo;
    }
}

namespace System.Windows.Documents
{
    /// <summary>占位集合类型：真源码 PresentationFramework/System/Windows/Documents/FixedDocument.cs（FixedDocument.Pages 的元素类型）。</summary>
    public class PageContentCollection : IEnumerable
    {
        public IEnumerator GetEnumerator() { return null; }
    }

    /// <summary>占位集合类型：真源码 PresentationFramework/System/Windows/Documents/DocumentReferenceCollection.cs（FixedDocumentSequence.References 的元素类型）。</summary>
    public class DocumentReferenceCollection : IEnumerable
    {
        public IEnumerator GetEnumerator() { return null; }
    }

    /// <summary>
    /// 真源码：PresentationFramework/System/Windows/Documents/FixedPage.cs
    ///   public sealed class FixedPage : FrameworkElement, IAddChild, IUriContext
    /// 替身基类取 System.Windows.UIElement（PresentationCore）以避免「替身 → 真 PF」的第二次循环依赖；
    /// 真类经 FrameworkElement:UIElement 继承，运行期成员解析沿真继承链，语义一致。
    /// </summary>
    public class FixedPage : System.Windows.FrameworkElement
    {
        // FrameWorkElement 成员（真源码 PresentationFramework/System/Windows/FrameworkElement.cs）
        public double Width { get { return 0; } set { } }
        public double Height { get { return 0; } set { } }
        public System.Windows.Markup.XmlLanguage Language { get { return null; } set { } }

        // FixedPage 成员（真源码 FixedPage.cs）
        public System.Windows.Media.Brush Background { get { return null; } set { } }
        // 真源码 FixedPage.cs：public UIElementCollection Children { get; }
        // 消费方：ReachFramework/Serialization/manager/ReachSerializationUtils.cs:784
        public System.Windows.Controls.UIElementCollection Children { get { return null; } }
        // 注：真 FixedPage 还有 Children(UIElementCollection) / Resources(ResourceDictionary)，
        // 两者都是 PresentationFramework 自有类型，替身内不可达；实测 RF/System.Printing 都不使用它们
        // （RF 里的 page.Children / page.Visual 中 page 是 PresentationCore 的 DocumentPage），故不声明。

        // IUriContext 成员（真源码 FixedPage.cs：public Uri NavigateUri { get; set; }）
        public Uri NavigateUri { get { return null; } set { } }

        // 真源码 FixedPage.cs：public static Uri GetNavigateUri(UIElement element)
        public static Uri GetNavigateUri(System.Windows.UIElement element) { return null; }
    }

    /// <summary>
    /// 真源码：PresentationFramework/System/Windows/Documents/DocumentReference.cs:24
    ///   public sealed class DocumentReference : FrameworkElement, IUriContext
    /// 消费方：ReachFramework/Serialization/manager/NGCSerializer.cs:591 → dre.GetDocument(false)。
    /// （替身基类取 System.Windows.UIElement，理由同 FixedPage。）
    /// </summary>
    public sealed class DocumentReference : System.Windows.FrameworkElement
    {
        // 真源码 DocumentReference.cs:65：public FixedDocument GetDocument(bool forceReload)
        // （实测消费方 ReachDocumentReferenceSerializer.cs:49 需要隐式转成 FixedDocument，
        //   故返回类型必须是 FixedDocument —— 第一版写成 IDocumentPaginatorSource 被编译器当场纠正）
        public FixedDocument GetDocument(bool forceReload) { return null; }
    }

    /// <summary>
    /// 真源码：PresentationFramework/System/Windows/Documents/PageContent.cs
    ///   public class PageContent : FrameworkElement
    /// 消费方：ReachSerializationUtils.cs:1225 `((PageContent)page).GetPageRoot(false) as FixedPage`；
    ///         XpsSerializationManager.cs:307 `typeof(PageContent).IsAssignableFrom(...)`。
    /// </summary>
    public class PageContent : System.Windows.FrameworkElement
    {
        // 真源码 PageContent.cs：public FixedPage GetPageRoot(bool forceReload)
        public FixedPage GetPageRoot(bool forceReload) { return null; }
    }

    /// <summary>
    /// 真源码：PresentationFramework/System/Windows/Documents/Hyperlink.cs
    ///   public class Hyperlink : Span（Span : Inline : TextElement : FrameworkContentElement）
    /// 消费方：ReachSerializationUtils.cs:897-899 `element is Hyperlink` / `((Hyperlink)element).NavigateUri`。
    /// 替身直接派生自 FrameworkContentElement（中间层 Inline/TextElement/Span 未声明）。
    /// </summary>
    public class Hyperlink : System.Windows.FrameworkContentElement
    {
        public Uri NavigateUri { get { return null; } set { } }
    }

    /// <summary>
    /// 真源码：PresentationFramework/System/Windows/Documents/FixedDocument.cs
    ///   public class FixedDocument : FrameworkContentElement, ...
    /// （替身基类取 System.Windows.ContentElement，理由同上）
    /// </summary>
    public class FixedDocument : System.Windows.FrameworkContentElement, IDocumentPaginatorSource
    {
        // 真源码 FixedDocument.cs 实现 IDocumentPaginatorSource：
        //   public DocumentPaginator DocumentPaginator { get; }
        // 消费方：ReachFramework/Serialization/manager/NGCSerializer.cs:594 需要隐式转成 IDocumentPaginatorSource
        public DocumentPaginator DocumentPaginator { get { return null; } }

        // 真源码 FixedDocument.cs：public PageContentCollection Pages { get; }
        public PageContentCollection Pages { get { return null; } }

        // 真源码 FixedDocument.cs：public PrintTicket PrintTicket { get; set; }
        public System.Printing.PrintTicket PrintTicket { get { return null; } set { } }

        // 真源码 FixedDocument.cs：public Uri Uri { get; set; }（IUriContext）
        public Uri Uri { get { return null; } set { } }
    }

    /// <summary>
    /// 真源码：PresentationFramework/System/Windows/Documents/DocumentSequence.cs
    ///   public class FixedDocumentSequence : FrameworkContentElement, ...
    /// </summary>
    public class FixedDocumentSequence : System.Windows.FrameworkContentElement
    {
        // 真源码 DocumentSequence.cs：public DocumentReferenceCollection References { get; }
        public DocumentReferenceCollection References { get { return null; } }

        // 真源码 DocumentSequence.cs：public PrintTicket PrintTicket { get; set; }
        public System.Printing.PrintTicket PrintTicket { get { return null; } set { } }

        // 真源码 DocumentSequence.cs：public Uri Uri { get; set; }（IUriContext）
        public Uri Uri { get { return null; } set { } }
    }
}
