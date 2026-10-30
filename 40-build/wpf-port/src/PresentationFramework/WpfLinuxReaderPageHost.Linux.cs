// ⚠️ 本文件由 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/reapply-patches.py **生成**，不要手改。
//
// T-B13（`PRECOND-TAB3-PAGE-HOST`）：`FlowDocumentReader` 内部页宿主的接线 ＋ 只读台账（[READERHOST]）。
// 断点、件:行与依据见生成器内同名块。`WPF_READER_PAGEHOST=0` ⇒ 整块不发生（反极性腿）。

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;

namespace MS.Internal.Documents
{
    internal static class WpfLinuxReaderPageHost
    {
        private static int _enabled = -1;
        private static Style _fallback;

        internal static bool Enabled
        {
            get
            {
                if (_enabled < 0)
                {
                    string s = null;
                    try { s = Environment.GetEnvironmentVariable("WPF_READER_PAGEHOST"); }
                    catch (Exception) { s = null; }
                    // ⚠️ `T-B13` **默认关**（只有显式 "1" 才开）：本条接线**已现取**它把页宿主
                    //   **接出来了**（`[DVBI] selfType=ReaderPageViewer n=1`），但**帧面不绿**
                    //   （`tab3` 色数／具名色不变）且**新增一条 `COMException E_HANDLE`** ⇒ 不默认启用，
                    //   不把"接出来了却仍不上屏"当成"修好了"。保持可复现（`=1` 即开）以备后续复核。
                    _enabled = (s == "1") ? 1 : 0;
                }
                return _enabled == 1;
            }
        }

        /// <summary>
        /// 上游行为 ＝ 无条件 `SetResourceReference(StyleProperty, key)`。本移植里该键（`PUIPageViewStyleKey`）
        /// 在 PresentationUI 替身中**不存在** ⇒ 先**只读**查一次：查到 ⇒ 逐字回上游；查不到 ⇒ 补等价模板。
        /// </summary>
        internal static void ApplyPageViewerStyle(FrameworkElement viewer, ResourceKey key)
        {
            if (viewer == null) { return; }
            if (!Enabled)
            {
                viewer.SetResourceReference(FrameworkElement.StyleProperty, key);
                return;
            }
            object found = null;
            try { found = viewer.TryFindResource(key); }
            catch (Exception) { found = null; }
            if (found is Style)
            {
                viewer.SetResourceReference(FrameworkElement.StyleProperty, key);
                Emit("[READERHOST] site=ApplyViewerStyle viewerType=" + TypeOf(viewer)
                     + " key=PUIPageViewStyleKey found=1 fallback=0 viewer=" + Id(viewer));
                return;
            }
            // ② 该键缺失 ⇒ **先**用"控件类型隐式样式"（＝"上游在 Windows 上本来会拿到的那份等价物"，
            //    只是由 app 提供；查到就用它，等价于**不给 `Style` 留本地值** ⇒ 隐式查找照旧生效）。
            Style implicitStyle = null;
            try { implicitStyle = viewer.TryFindResource(typeof(FlowDocumentPageViewer)) as Style; }
            catch (Exception) { implicitStyle = null; }
            if (implicitStyle != null)
            {
                viewer.Style = implicitStyle;
                Emit("[READERHOST] site=ApplyViewerStyle viewerType=" + TypeOf(viewer)
                     + " key=PUIPageViewStyleKey found=0 fallback=implicit targetType="
                     + (implicitStyle.TargetType == null ? "null" : implicitStyle.TargetType.Name)
                     + " viewer=" + Id(viewer));
                return;
            }
            Style fb = Fallback;
            viewer.Style = fb;
            Emit("[READERHOST] site=ApplyViewerStyle viewerType=" + TypeOf(viewer)
                 + " key=PUIPageViewStyleKey found=0 fallback=equiv targetType="
                 + (fb.TargetType == null ? "null" : fb.TargetType.Name)
                 + " viewer=" + Id(viewer));
        }

        /// <summary>
        /// 与上游 `PresentationUI/Themes/Generic.xaml:8675-8699` 的 `PUIPageViewStyleKey` **等价**的
        /// `FlowDocumentPageViewer` 样式：`AdornerDecorator(ClipToBounds)` → `Border` →
        /// `DocumentPageView(IsMasterPage=True, ClipToBounds)`。**只**在该资源键解析为空时使用。
        /// </summary>
        private static Style Fallback
        {
            get
            {
                if (_fallback == null)
                {
                    FrameworkElementFactory dpv = new FrameworkElementFactory(typeof(DocumentPageView));
                    dpv.SetValue(UIElement.ClipToBoundsProperty, true);
                    dpv.SetValue(DocumentViewerBase.IsMasterPageProperty, true);
                    FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
                    border.AppendChild(dpv);
                    FrameworkElementFactory adorner = new FrameworkElementFactory(typeof(AdornerDecorator));
                    adorner.SetValue(UIElement.ClipToBoundsProperty, true);
                    adorner.AppendChild(border);
                    ControlTemplate tmpl = new ControlTemplate(typeof(FlowDocumentPageViewer));
                    tmpl.VisualTree = adorner;
                    Style st = new Style(typeof(FlowDocumentPageViewer));
                    st.Setters.Add(new Setter(Control.TemplateProperty, tmpl));
                    _fallback = st;
                }
                return _fallback;
            }
        }

        /// <summary>`T-B13`：只读台账 —— 「接上新查看器」那一步的现场（查看器／内容宿主各是什么）。</summary>
        internal static void ReportAttached(FrameworkElement viewer, object contentHost)
        {
            if (!Enabled) { return; }
            Emit("[READERHOST] site=Attach viewerType=" + TypeOf(viewer) + " viewer=" + Id(viewer)
                 + " contentHostType=" + TypeOf(contentHost));
        }

        private static string TypeOf(object o)
        {
            if (o == null) { return "null"; }
            try { return o.GetType().Name; }
            catch (Exception) { return "NA"; }
        }

        internal static string Id(object o)
        {
            if (o == null) { return "null"; }
            try
            {
                return "0x" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o)
                    .ToString("x", System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception) { return "NA"; }
        }

        private static void Emit(string line)
        {
            try { Console.Error.WriteLine(line); Console.Error.Flush(); }
            catch (Exception) { }
        }
    }
}
