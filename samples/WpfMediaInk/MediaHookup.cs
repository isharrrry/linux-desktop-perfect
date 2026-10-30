// 【ACC-022 / D-015】把「播放 / 暂停」接到真正的 MediaPlayer 上，并让 `state=` 读数跟着变。
//
// 为什么手写：XAML 由 gen-samples 生成，不该往里塞事件绑定；
//   而且这一屏的属性表要求 8 个元素、路径固定（加/删元素都会让 ACC-027 错位）。
// 为什么走 MILMediaSetRate：读上游 `MediaPlayerState.Play()` 确认
//   `Play() ⇒ PrivateSpeedRatio = SpeedRatio`、`Pause() ⇒ PrivateSpeedRatio = 0`，
//   而 `PrivateSpeedRatio` 的 setter 就是 `MILMedia.SetRate` —— 播放/暂停没有独立导出。
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LinuxDesktopPerfect.Samples.WpfMediaInk
{
    internal static class MediaHookup
    {
        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void Init()
        {
            Diag("Init: 注册类处理器 typeof(MediaWindow)=" + typeof(MediaWindow).AssemblyQualifiedName);
            EventManager.RegisterClassHandler(typeof(MediaWindow), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnLoaded));
        }

        // 诊断**写文件**而不是 stderr：本项目的日志通道被钩子/重定向/管道吞过三次
        //   （读数缺失被误读成"处理器没跑"），文件是唯一不会被吞的通道。
        private static void Diag(string msg)
        {
            try { System.IO.File.AppendAllText("/tmp/ldp-media-hook.log", msg + "\n"); } catch { }
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            Diag("OnLoaded fired: sender=" + (sender == null ? "null" : sender.GetType().FullName));
            if (sender is not MediaWindow w) { Diag("sender 不是 MediaWindow，跳过"); return; }
            try
            {
                var player = new MediaPlayer();
                string media = MediaArg() ?? DefaultMedia();
                if (media != null) player.Open(new Uri(media, UriKind.Absolute));

                Button play = FindByName(w, "播放");
                Button pause = FindByName(w, "暂停");
                TextBlock state = FindState(w);

                if (play != null) play.Click += (s2, e2) => { player.Play(); SetState(state, "PLAYING"); };
                if (pause != null) pause.Click += (s2, e2) => { player.Pause(); SetState(state, "PAUSED"); };

                SetState(state, player.Source == null ? "READY" : "READY");
                Diag("hookup: play=" + (play != null) + " pause=" + (pause != null)
                    + " state=" + (state != null) + " media=" + (media ?? "null"));
            }
            catch (Exception ex)
            {
                Diag("EXCEPTION: " + ex);
            }
        }

        private static string MediaArg()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; ++i)
                if (args[i] == "--media") return args[i + 1];
            return null;
        }

        // 仓库内的测试视频（相对可执行目录向上找，找不到就返回 null —— 不伪造路径）
        private static string DefaultMedia()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 6 && dir != null; ++i)
            {
                string p = System.IO.Path.Combine(dir, "40-build", "media-bridge", "testdata", "red-32x24.mp4");
                if (System.IO.File.Exists(p)) return p;
                dir = System.IO.Path.GetDirectoryName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar));
            }
            return null;
        }

        private static void SetState(TextBlock tb, string state)
        {
            if (tb == null) return;
            tb.Text = "state=" + state;
            System.Windows.Automation.AutomationProperties.SetName(tb, "state=" + state);
        }

        private static TextBlock FindState(DependencyObject root)
        {
            foreach (var c in All(root))
                if (c is TextBlock tb && (tb.Text ?? "").StartsWith("state=", StringComparison.Ordinal)) return tb;
            return null;
        }

        private static Button FindByName(DependencyObject root, string name)
        {
            foreach (var c in All(root))
                if (c is Button b && (System.Windows.Automation.AutomationProperties.GetName(b) == name)) return b;
            return null;
        }

        private static System.Collections.Generic.IEnumerable<DependencyObject> All(DependencyObject root)
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; ++i)
            {
                DependencyObject c = VisualTreeHelper.GetChild(root, i);
                yield return c;
                foreach (var d in All(c)) yield return d;
            }
        }
    }
}
