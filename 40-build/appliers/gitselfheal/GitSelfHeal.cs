using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace LdpFix
{
    // PM-0128: 打开仓库时惰性自愈。
    // 纯反射，不静态依赖任何 SourceTree 类型；健康机器上（handler 不是 RepoHandlerNone）
    // 打完 [GHEAL] before=… 就直接返回，零副作用。
    public static class GitSelfHeal
    {
        private const string NoneHandler = "SourceTree.Dvcs.None.Repo.RepoHandlerNone";
        private static int s_attempts;
        private static void Log(string s) { Console.Error.WriteLine("[GHEAL] " + s); }

        private static object Prop(object o, string name)
        {
            if (o == null) return null;
            var p = o.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return p == null ? null : p.GetValue(o);
        }

        private static MethodInfo Method(Type t, string name, Type[] args)
        {
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            if (args == null) return t.GetMethods(F).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == 0);
            return t.GetMethod(name, F, null, args, null);
        }

        public static void TryFix(object panelVm, object node)
        {
            if (s_attempts >= 3) return;
            try
            {
                object repo = Prop(node, "Repo");
                if (repo == null) { Log("EX repo=null"); return; }

                object handler = Prop(repo, "Handler");
                string handlerName = handler == null ? "null" : handler.GetType().FullName;
                Log("before=" + handlerName);
                if (handler != null && handlerName != NoneHandler) return;   // 健康机器：不动

                s_attempts++;

                object mgr = Prop(panelVm, "DvcsManager");
                if (mgr == null) { Log("EX DvcsManager=null"); return; }
                Type mgrType = mgr.GetType();

                Type repoTypeEnum = null;
                foreach (var m in mgrType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.Name != "EnableSupport") continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType.IsEnum) { repoTypeEnum = ps[0].ParameterType; break; }
                }
                if (repoTypeEnum == null) { Log("EX RepoType enum not found"); return; }
                object git = Enum.ToObject(repoTypeEnum, 0);   // SourceTree.Model.RepoType.Git = 0

                var enable = Method(mgrType, "EnableSupport", new[] { repoTypeEnum });
                if (enable == null) { Log("EX EnableSupport(RepoType) not found"); return; }
                var sw = Stopwatch.StartNew();
                enable.Invoke(mgr, new[] { git });
                Log("detect_ms=" + sw.ElapsedMilliseconds);

                var getHandler = Method(mgrType, "GetRepositoryHandler", new[] { repoTypeEnum });
                if (getHandler == null) { Log("EX GetRepositoryHandler(RepoType) not found"); return; }
                object gitHandler = getHandler.Invoke(mgr, new[] { git });
                Log("after=" + (gitHandler == null ? "null" : gitHandler.GetType().FullName));
                if (gitHandler == null) return;

                string path = (string)Prop(repo, "Path");
                bool ok = Validate(gitHandler, path);
                Log("validate=" + ok + " path=" + path);
                if (!ok) return;

                var reset = Method(repo.GetType(), "ResetHandler", null);
                if (reset == null)
                {
                    foreach (var m in repo.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                        if (m.Name == "ResetHandler" && m.GetParameters().Length == 1) { reset = m; break; }
                }
                if (reset == null) { Log("EX ResetHandler not found"); return; }
                reset.Invoke(repo, new[] { gitHandler });

                object fin = Prop(repo, "Handler");
                Log("reset_ok final=" + (fin == null ? "null" : fin.GetType().FullName));
            }
            catch (Exception e)
            {
                Log("EX " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static bool Validate(object handler, string path)
        {
            Type t = handler.GetType();
            var iface = t.GetInterface("SourceTree.Repositories.IConfigurationSupport");
            MethodInfo m = iface == null ? null : iface.GetMethod("ValidateWorkingDirectory");
            if (m == null)
                m = t.GetMethod("ValidateWorkingDirectory", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null) { Log("EX ValidateWorkingDirectory not found"); return false; }
            object r = m.Invoke(handler, new object[] { path });
            return r is bool b && b;
        }
    }
}
