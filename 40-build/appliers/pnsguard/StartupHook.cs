// PM-0141 · 最外层未处理异常策略（DOTNET_STARTUP_HOOKS 注入，不改 app dll）。
// 【口径（大肥鱼 11-19 定）】只截 PlatformNotSupportedException：
//   弹可见提示 + e.Handled = true，进程继续；其它异常一律不碰，照原样终止。
//   桩继续抛 PNSE 的既有决策不变（不返回空 DataObject 假装成功）。
using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

public static class StartupHook
    {
        private static readonly string LogPath =
            Environment.GetEnvironmentVariable("LDP_PNSGUARD_LOG")
            ?? "/home/links-dev/deploy-artifacts/pnsguard.log";
        private static int _prompting;

        private static void Log(string s)
        {
            try
            {
                File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + "\n");
            }
            catch { }
        }

        public static void Initialize()
        {
            try
            {
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                    Log("[PNSG] AppDomain 未处理: " + e.ExceptionObject?.GetType().Name);

                Dispatcher.CurrentDispatcher.UnhandledException += OnDispatcherException;
                Log("[PNSG] installed dispatcher=" + Dispatcher.CurrentDispatcher.GetHashCode());
            }
            catch (Exception ex)
            {
                Log("[PNSG] install FAILED: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            if (!(e.Exception is PlatformNotSupportedException pns))
            {
                Log("[PNSG] pass-through " + e.Exception?.GetType().Name);
                return;    // 不碰，让进程照原样终止
            }

            e.Handled = true;
            Log("[PNSG] caught PNSE: " + pns.Message);

            if (System.Threading.Interlocked.Exchange(ref _prompting, 1) != 0) return;
            try
            {
                MessageBox.Show(
                    "该操作在当前平台（Linux 移植版）尚未实现，已安全取消，应用会继续运行。\n\n" +
                    "详细信息：" + pns.Message,
                    "功能暂不可用",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex2)
            {
                Log("[PNSG] prompt FAILED: " + ex2.GetType().Name + ": " + ex2.Message);
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _prompting, 0);
            }
        }
    }
