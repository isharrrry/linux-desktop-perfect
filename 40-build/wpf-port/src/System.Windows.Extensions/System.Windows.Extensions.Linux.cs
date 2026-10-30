// ─────────────────────────────────────────────────────────────────────────────
// System.Windows.Extensions 的 **Linux 原生替身**（`#34` 波）
//
// 【为什么】（实测栈，第三方应用 HandyControl 示例工程）
//   App.InitializeComponent() → XamlReader.LoadBaml → XamlAccessLevel.AssemblyAccessTo
//   ⇒ PlatformNotSupportedException: System.Windows.Extensions types are not supported on this platform.
//   上游 `PresentationFramework/System/Windows/Markup/XamlReader.cs:1096-1104` 的分支是
//     if (internalTypeHelper != null) { accessLevel = XamlAccessLevel.AssemblyAccessTo(asm); … }
//   —— 只要程序集里生成了 `GeneratedInternalTypeHelper`（正常构建的 WPF 程序集都会），
//     装载 BAML 就必然走这一句；而包里该类型在非 Windows 上是**故意抛异常**的桩。
//
// 【口径】`.NET Core / Linux 上没有 CAS、没有部分信任` ⇒ access level 只是一个**标记**：
//   它记录"这段 BAML 属于哪个程序集 / 哪个类型"，随后由 `WpfXamlLoader` 传给 XamlObjectWriter
//   用于访问非公开成员。**没有安全判定**要做，所以这里的实现只保存这两个字段、**不抛**。
//   这与本仓既有降级口径一致（如实降级并登记，不伪造安全语义）。参见同目录 PORT-CHANGES.md。
//
// 【覆盖范围】只实现 WPF 在 Linux 上会用到的那三个类型族（包里共 6 个类型）：
//   · System.Xaml.Permissions.XamlAccessLevel     ← 本文件的核心（挡住 BAML 装载）
//   · System.Media.SystemSound / SystemSounds     ← 应用的提示音（HandyControl 会调 SystemSounds.Asterisk.Play）
//   · System.Media.SoundPlayer                    ← 同上族，Linux 上无系统音效，如实降级为"不播"
//   **不提供** `X509Certificate2UI` / `X509SelectionFlag`（Linux 上没有证书选择 UI；
//   谁用它谁在**编译期**就报缺类型，比运行期抛异常更早、更清楚）。
// ─────────────────────────────────────────────────────────────────────────────

using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;

namespace System.Xaml.Permissions
{
    /// <summary>XAML 装载时的"访问级别"标记。Linux 版不抛异常（见文件头）。</summary>
    public class XamlAccessLevel
    {
        private readonly AssemblyName _assemblyName;
        private readonly string _typeName;

        private XamlAccessLevel(AssemblyName assemblyName, string typeName)
        {
            _assemblyName = assemblyName;
            _typeName = typeName;
        }

        /// <summary>公开面与官方包一致：目标程序集名。</summary>
        public AssemblyName AssemblyAccessToAssemblyName => _assemblyName;

        /// <summary>公开面与官方包一致：目标类型的非限定名。</summary>
        public string PrivateAccessToTypeName => _typeName;

        public static XamlAccessLevel AssemblyAccessTo(Assembly assembly)
            => new XamlAccessLevel(assembly?.GetName(), null);

        public static XamlAccessLevel AssemblyAccessTo(AssemblyName assemblyName)
            => new XamlAccessLevel(assemblyName, null);

        public static XamlAccessLevel PrivateAccessTo(string assemblyQualifiedTypeName)
            => new XamlAccessLevel(null, GetUnqualifiedTypeName(assemblyQualifiedTypeName));

        public static XamlAccessLevel PrivateAccessTo(Type type)
            => new XamlAccessLevel(type?.Assembly?.GetName(), type?.Name);

        internal Assembly Assembly => _assemblyName == null ? null : Assembly.Load(_assemblyName);

        internal string TypeName => _typeName;

        private static string GetUnqualifiedTypeName(string assemblyQualifiedTypeName)
        {
            if (string.IsNullOrEmpty(assemblyQualifiedTypeName)) return assemblyQualifiedTypeName;
            int comma = assemblyQualifiedTypeName.IndexOf(',');
            string full = comma < 0 ? assemblyQualifiedTypeName : assemblyQualifiedTypeName.Substring(0, comma);
            int dot = full.LastIndexOf('.');
            return dot < 0 ? full : full.Substring(dot + 1);
        }
    }
}

namespace System.Media
{
    /// <summary>系统提示音。Linux 后端**不播**（没有等价的系统音效通道），如实降级。</summary>
    public class SystemSound
    {
        internal SystemSound(string name) { Name = name; }

        /// <summary>该声音的名字（诊断用；官方包没有这个成员，加它是为了可观测）。</summary>
        public string Name { get; }

        /// <summary>不播。**不做假动作**：没有 XBell/音频通道就不产生声音，也不抛异常打断应用。</summary>
        public void Play()
        {
            Diagnostics.SoundAttempts++;
        }
    }

    public static class SystemSounds
    {
        public static SystemSound Asterisk => Get("Asterisk");
        public static SystemSound Beep => Get("Beep");
        public static SystemSound Exclamation => Get("Exclamation");
        public static SystemSound Hand => Get("Hand");
        public static SystemSound Question => Get("Question");

        private static readonly SystemSound s_asterisk = new SystemSound("Asterisk");

        private static SystemSound Get(string name) => name == "Asterisk" ? s_asterisk : new SystemSound(name);
    }

    /// <summary>`SoundPlayer`：Linux 上同样是"不播"，但保留与官方包同名的公开面。</summary>
    /// <remarks>
    /// ⚠️ **`#38`：公开面必须补齐到"上层真的用到的那些成员"** —— `#36` 只写了最小面，
    /// 接线后 `PresentationFramework` 报 7 条 `CS1061/CS1503`（`Dispose`/`IsLoadCompleted`/
    /// `LoadCompleted`/`Stream`/`new SoundPlayer(Stream)`），逐条来自
    /// `upstream/.../Controls/SoundPlayerAction.cs`（`:40` `m_player?.Dispose()`、`:141/:219`
    /// `m_player.IsLoadCompleted`、`:203` `new SoundPlayer((Stream)…)`、`:207` `m_player.Stream = …`、
    /// `:209` `m_player.LoadCompleted += …`、`:210` `m_player.LoadAsync()`、`:143/:234` `m_player.Play()`）。
    /// ⚠️ `IsLoadCompleted` **必须在 `LoadAsync()` 之后为 true**：上游在那之后有一句
    /// `Debug.Assert(m_player.IsLoadCompleted)`，而本仓的权威件目前是 **Debug** 构建 ⇒ 断言**是活的**。
    /// </remarks>
    public class SoundPlayer : IDisposable
    {
        private Stream _stream;

        public SoundPlayer() { }
        public SoundPlayer(string soundLocation) { SoundLocation = soundLocation; }
        public SoundPlayer(Stream stream) { Stream = stream; }

        public string SoundLocation { get; set; }

        /// <summary>官方包里的同名属性；设置即视为"已装好"（我们不真的缓冲）。</summary>
        public Stream Stream
        {
            get => _stream;
            set { _stream = value; IsLoadCompleted = true; }
        }

        /// <summary>"缓冲完成"——Linux 上我们从不做 IO ⇒ 直接为 true（见 `#38` 注释）。</summary>
        public bool IsLoadCompleted { get; private set; }

        /// <summary>官方语义是异步加载完成时触发；我们同步触发一次（不假造失败）。</summary>
        public event AsyncCompletedEventHandler LoadCompleted;

        public void Load()
        {
            IsLoadCompleted = true;
            LoadCompleted?.Invoke(this, new AsyncCompletedEventArgs(null, false, null));
        }

        public void LoadAsync() => Load();

        public void Play() { Diagnostics.SoundAttempts++; }
        public void PlayAsync() => Play();
        public void PlaySync() => Play();
        public void PlayLooping() => Play();
        public void Stop() { }

        /// <summary>官方包继承 `Component` ⇒ 有 `Dispose`；上层 `SoundPlayerAction.Dispose()` 会调它。</summary>
        public void Dispose()
        {
            _stream?.Dispose();
            _stream = null;
        }
    }

    internal static class Diagnostics
    {
        /// <summary>被请求播放的次数（可观测：进程内计数；读取见 PORT-CHANGES.md）。</summary>
        internal static int SoundAttempts;
    }
}

namespace System.Security.Cryptography.X509Certificates
{
        /// <summary>`#38`：`X509Certificate2UI` / `X509SelectionFlag` —— **官方包也提供它们**。
        /// 排除包的编译资产后，`WindowsBase` 的 `PackageDigitalSignatureManager.PromptForSigningCertificate`
        /// 就找不到这两个类型（实测 4 条 `CS0103`）⇒ 必须由替身补齐。
        /// ⚠️ 口径：证书选择**是一个真对话框**，Linux 上没有等价物 ⇒ **如实抛**
        /// `PlatformNotSupportedException`（这不是"谎话"，是"这东西在 Linux 上不存在"）。
        /// 它只在"用户交互式给包签名"这条路上被调用，正常路径不会碰到。</summary>
        public enum X509SelectionFlag
        {
            SingleSelection = 0,
            MultiSelection = 1,
        }

        public static class X509Certificate2UI
        {
            public static X509Certificate2Collection SelectFromCollection(
                X509Certificate2Collection certificates, string title, string message, X509SelectionFlag selectionFlag)
                => SelectFromCollection(certificates, title, message, selectionFlag, IntPtr.Zero);

            public static X509Certificate2Collection SelectFromCollection(
                X509Certificate2Collection certificates, string title, string message,
                X509SelectionFlag selectionFlag, IntPtr hwndOwner)
                => throw new PlatformNotSupportedException(
                    "X509Certificate2UI（证书选择对话框）在 Linux 上没有等价物 —— 本替身**如实失败**，" +
                    "不假装成功。若要给包签名，请用非交互路径（PackageDigitalSignatureManager 的显式证书重载）。");
        }
}
