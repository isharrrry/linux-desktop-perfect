// 【D-015 第十三步】把 FlowDocument 的段落文本**预先缓存**进 FlowDocument.Tag。
//
// 为什么在这里做（而不是在 LdpA11y 里）：
//   `Block` 上的属性在 AT-SPI dump 的时机读取会触发**同步排版**，而移植版在无渲染线程的
//   dump 路径上等不到排版完成 ⇒ 样本卡住（实测三点：只读 Blocks.Count 不卡、foreach 卡、
//   FirstBlock/NextBlock 链也卡）。所以改由**应用侧**在 Loaded（布局已完成）里取一次文本、
//   存进 `FlowDocument.Tag`；LdpA11y 只读 Tag，不在投影路径上碰排版。
//
// 用「类处理器」而不是构造函数：XAML 生成的 partial 类不该再插构造函数代码。
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Runtime.CompilerServices;

namespace LinuxDesktopPerfect.Samples.WpfTextDemo
{
    internal static class DocCache
    {
        // 【为什么需要这一行】静态类的静态构造**只在类被使用时**才执行；没人引用 DocCache，
        //   类处理器就永远不会注册（实测症状：`doc.Tag` 一直是 null）。
        //   `[ModuleInitializer]` 在模块加载时强制跑一次 ⇒ 注册一定发生。
        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void Init() { RuntimeHelpers.RunClassConstructor(typeof(DocCache).TypeHandle); }

        static DocCache()
        {
            EventManager.RegisterClassHandler(typeof(TextWindow), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnLoaded));
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is not TextWindow w) return;
                foreach (var host in Hosts(w))
                {
                    FlowDocument doc = host.Document;
                    if (doc is null) continue;
                    var acc = new List<string>();
                    foreach (Block blk in doc.Blocks)
                    {
                        string t = new TextRange(blk.ContentStart, blk.ContentEnd).Text;
                        string kind = blk is System.Windows.Documents.List ? "L|" : "P|";
                        acc.Add(kind + (t ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim());
                    }
                    doc.Tag = acc.ToArray();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("doc-cache: " + ex.Message);
            }
        }

        private static IEnumerable<FlowDocumentScrollViewer> Hosts(DependencyObject root)
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; ++i)
            {
                DependencyObject c = VisualTreeHelper.GetChild(root, i);
                if (c is FlowDocumentScrollViewer sv) yield return sv;
                foreach (var d in Hosts(c)) yield return d;
            }
        }
    }
}
