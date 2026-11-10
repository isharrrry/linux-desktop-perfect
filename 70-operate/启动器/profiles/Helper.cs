// 程序化操作 SourceTree 的最小骨架（对应《程序化操作清单》①~⑤）
// 用法：把 Run(window) 插到 MainWindow_Loaded 开头（Cecil 注入器见 40-build/appliers/loglabel-brush/）
// 环境：GV_HOME=<home>（默认 /tmp/fakegit），仓库路径见 RepoPath
// 依赖：$APP 目录下需有本 Helper 的 DLL；日志走 AppendAllText 即时落盘（进程被杀也能看到）

using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;

namespace StHelper
{
    public static class InjectedHelper
    {
        const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static object _rtc;
        static object _tab;
        static readonly string RepoPath = "/tmp/stgit";
        static readonly string Home = Environment.GetEnvironmentVariable("GV_HOME") ?? "/tmp/fakegit";
        static readonly string LogPath = "/tmp/sthelper.log";
        static DispatcherTimer _timer;
        static int _tick;

        public static void Run(Window window)
        {
            Log("start");
            try
            {
                var vm = window.DataContext;

                // ① Dvcss 配置注入（K-1413/K-1415）
                var dvcsMgr = Prop(vm, "DvcssManager");
                var dvcs = dvcsMgr.GetType().GetMethod("GetDvcs").Invoke(dvcsMgr, new object[] { 0 });
                var cfgMgr = Prop(vm, "ConfigurationManager");
                var verCfg = Activator.CreateInstance(
                    Type.GetType("SourceTree.Dvcss.GitVersionOneConfiguration, SourceTree.Dvcs.Git"),
                    new object[] { cfgMgr });
                var instCfg = Activator.CreateInstance(
                    Type.GetType("SourceTree.Model.Dvcss.DvcsInstallationConfiguration, SourceTree.Api.Dvcs"),
                    new object[] { Home, "2.30.0", verCfg });
                Field(dvcs.GetType(), "<CurrentInstallationConfiguration>k__BackingField").SetValue(dvcs, instCfg);
                Log("GetHandler.notnull=" + (dvcs.GetType().GetMethod("GetHandler").Invoke(dvcs, null) != null ? 1 : 0));

                // ② 打开仓库（K-1373）
                _rtc = Prop(vm, "RepositoryTabContainer");
                var add = _rtc.GetType().GetMethod("AddOrActivate");
                add.Invoke(_rtc, new object[] { RepoPath, Enum.ToObject(add.GetParameters()[1].ParameterType, 0), null });
                Log("AddOrActivate done");

                // ③ 启用监控（K-1445）：不调则 watcher 不注册、列表不自动刷新
                var mm = Field(vm.GetType(), "<RepositoryMonitorManager>k__BackingField")?.GetValue(vm)
                      ?? Prop(vm, "RepositoryMonitorManager");
                mm.GetType().GetMethod("ActivatedTab").Invoke(mm, new object[] { RepoPath });
                mm.GetType().GetMethod("RefreshFileSystemMonitoring").Invoke(mm, null);
                mm.GetType().GetMethod("SetFileWatcherStatus").Invoke(mm, new object[] { RepoPath, true });
                var mon = Prop(mm, "RepoMonitor");
                mon?.GetType().GetMethod("Resume")?.Invoke(mon, null);
                Log("watcher enabled");

                // ⑤ 读取放 timer（UI 线程），不要 Thread.Sleep（K-1420）
                _timer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background,
                    (s, e) => OnTick(), window.Dispatcher);
                _timer.Start();
                Log("timer started");
            }
            catch (Exception ex) { Log("FATAL " + ex.GetType().Name + " :: " + ex.Message); }
        }

        static void OnTick()
        {
            _tick++;
            try
            {
                _tab = _tab ?? Prop(_rtc, "SelectedRepoTabViewModel");

                // ④ + ⑤：切到 Log 视图后再拉数据
                if (_tick == 2)
                {
                    var nav = _tab.GetType().GetMethod("NavigateToView");
                    nav.Invoke(_tab, new object[] { Enum.ToObject(nav.GetParameters()[0].ParameterType, 1), null });
                    Log("NavigateToView(Log) done");
                    var logVm = Prop(_tab, "LogVM");
                    logVm.GetType().GetMethod("LoadNextLogBatchFrom").Invoke(logVm, new object[] { 0, 500 });
                    Log("LoadNextLogBatchFrom(0,500) invoked");
                }

                var cfs = Field(_tab.GetType(), "_commitAndFileStatusVm")?.GetValue(_tab);
                var staged = Count(cfs, "StagedFileList");
                var unstaged = Count(cfs, "UnstagedFileList");
                var logRows = Count(Prop(_tab, "LogVM"), "LogRows");
                Log("tick" + _tick + " staged=" + staged + " unstaged=" + unstaged + " logRows=" + logRows);
            }
            catch (Exception ex) { Log("tick" + _tick + " EX " + ex.GetType().Name + " :: " + ex.Message); }
        }

        static object Prop(object o, string name)
            => o?.GetType().GetProperty(name, F)?.GetValue(o);

        static FieldInfo Field(Type t, string name) { for (var x = t; x != null; x = x.BaseType) { var f = x.GetField(name, F); if (f != null) return f; } return null; }

        static int Count(object o, string prop)
        {
            var c = Prop(o, prop) as ICollection;
            return c?.Count ?? -1;
        }

        static void Log(string m)
        {
            try { File.AppendAllText(LogPath, "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + m + Environment.NewLine); } catch { }
        }
    }
}
