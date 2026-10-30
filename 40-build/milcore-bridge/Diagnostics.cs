// T1 · MilBridge —— 桥接层专用诊断导出（**不属于 MIL ABI**，只给闭环测试用）。
//
// 为什么需要它：闭环测试要求断言 "channel.CommittedCommands 增加"。
// 但 `MilChannel` / `MilChannelRegistry` 是 WpfGfx.Linux 的 **internal** 类型，
// 而 AOT .so 里有自己的一套运行时与状态，跨进程读不到；托管测试进程即使用反射
// 也只能看到它自己那份 WpfGfx.Linux 副本，不是 .so 里跑的那份。
//
// 因此把这条观测放进 .so **内部**，用反射读同镜像里的 MilChannelRegistry。
// 反射依赖 <IlcGenerateCompleteTypeMetadata>true</IlcGenerateCompleteTypeMetadata>。
// 取不到时返回 E_NOTIMPL，测试侧据此标记 "该断言不可用"（不静默通过）。

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using WpfGfx.Linux.Interop;

namespace MilBridge
{
    /// <summary>桥接层诊断面（非 MIL ABI）。</summary>
    public static unsafe class Diagnostics
    {
        private const int S_OK = 0;
        private const int E_NOTIMPL = unchecked((int)0x80004001);
        private const int E_HANDLE = unchecked((int)0x80070006);
        private const int E_INVALIDARG = unchecked((int)0x80070057);

        /// <summary>计数器选择子，与 <see cref="MilChannelCounter"/> 对应。</summary>
        public enum MilChannelCounter
        {
            CommittedCommands = 0,
            NotImplCommands = 1,
            FailedCommands = 2,
            ShortCommands = 3,
            PendingCommandCount = 4,
            BatchByteCount = 5,
        }

        private static Type s_registryType;
        private static MethodInfo s_resolve;
        private static bool s_probed;
        private static int s_probeCode = -1;

        private static bool Probe()
        {
            if (s_probed) return s_registryType != null && s_resolve != null;
            s_probed = true;
            try
            {
                Assembly asm = typeof(MilNative).Assembly;
                s_registryType = asm.GetType("WpfGfx.Linux.Resources.MilChannelRegistry", throwOnError: false);
                if (s_registryType == null) { s_probeCode = 1; return false; }
                s_resolve = s_registryType.GetMethod(
                    "Resolve", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(IntPtr) }, null);
                if (s_resolve == null) { s_probeCode = 2; return false; }
                s_probeCode = 0;
                return true;
            }
            catch (Exception ex)
            {
                s_probeCode = 100 + ex.GetType().Name.GetHashCode() % 100;
                return false;
            }
        }

        /// <summary>
        /// 反射探针细节码：0=可用；1=找不到 MilChannelRegistry 类型；
        /// 2=找不到 Resolve 方法；100+=异常（100+异常类型哈希%100）。给报告定位用。
        /// </summary>
        [UnmanagedCallersOnly(EntryPoint = "MilBridge_Diag_ProbeCode")]
        public static int ProbeCode()
        {
            Probe();
            return s_probeCode;
        }

        /// <summary>读一个通道计数器。返回 E_NOTIMPL 表示本镜像里反射不可用。</summary>
        [UnmanagedCallersOnly(EntryPoint = "MilBridge_Diag_GetChannelCounter")]
        public static int GetChannelCounter(void* channelHandle, int which, long* value)
        {
            if (value == null) return E_INVALIDARG;
            *value = -1;
            if (!Probe()) return E_NOTIMPL;

            try
            {
                object channel = s_resolve.Invoke(null, new object[] { (IntPtr)channelHandle });
                if (channel == null) return E_HANDLE;

                string name = which switch
                {
                    0 => "CommittedCommands",
                    1 => "NotImplCommands",
                    2 => "FailedCommands",
                    3 => "ShortCommands",
                    4 => "PendingCommandCount",
                    5 => "BatchByteCount",
                    _ => null,
                };
                if (name == null) return E_INVALIDARG;

                Type t = channel.GetType();
                FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (f != null) { *value = Convert.ToInt64(f.GetValue(channel)); return S_OK; }

                PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (p != null) { *value = Convert.ToInt64(p.GetValue(channel)); return S_OK; }

                return E_NOTIMPL;
            }
            catch
            {
                return E_NOTIMPL;
            }
        }

        /// <summary>本镜像里反射诊断是否可用（1/0）。给测试侧区分"不可用"与"值为 0"。</summary>
        [UnmanagedCallersOnly(EntryPoint = "MilBridge_Diag_Available")]
        public static int Available() => Probe() ? 1 : 0;

        /// <summary>
        /// 返回 <c>MilNative.MissingExports()</c> 的条目数 —— 0 表示 108 个导出名在
        /// .so 内部的 WpfGfx.Linux 里都能找到同名 public static 方法。
        /// 这条同时验证了 AOT 裁剪没有把 MilNative 的方法裁掉。
        /// </summary>
        [UnmanagedCallersOnly(EntryPoint = "MilBridge_Diag_MissingExportCount")]
        public static int MissingExportCount() => MilNative.MissingExports().Count;

        /// <summary>返回 <c>MilNative.ExportManifest</c> 的条目数（应为 108）。</summary>
        [UnmanagedCallersOnly(EntryPoint = "MilBridge_Diag_ManifestCount")]
        public static int ManifestCount() => MilNative.ExportManifest.Count;

        /// <summary>返回 <c>MilNative.NotImplExportNames</c> 的条目数。</summary>
        [UnmanagedCallersOnly(EntryPoint = "MilBridge_Diag_NotImplCount")]
        public static int NotImplCount() => MilNative.NotImplExportNames.Count;

        /// <summary>清空进程内句柄表（测试用；语义同 MilNative.ResetProcessStateForTests）。</summary>
        [UnmanagedCallersOnly(EntryPoint = "MilBridge_Diag_ResetProcessState")]
        public static void ResetProcessState() => MilNative.ResetProcessStateForTests();

        /// <summary>
        /// 外部（WIC）句柄桥的计数与连接状态。写入 6 个 long：
        ///   [0]=probeCount [1]=ownedHits [2]=qiOk [3]=qiNoInterface [4]=externalReleases
        ///   [5]=IS_CONNECTED(0/1)
        /// 用于验证"MIL 侧与 WIC 侧两本账都配平"。
        /// </summary>
        [UnmanagedCallersOnly(EntryPoint = "MilBridge_Diag_WicBridge")]
        public static unsafe int WicBridge(long* values)
        {
            if (values == null) return unchecked((int)0x80070057);
            values[0] = MilExternalHandleBridge.ProbeCount;
            values[1] = MilExternalHandleBridge.OwnedHits;
            values[2] = MilExternalHandleBridge.ExternalQueryInterfaceSucceeded;
            values[3] = MilExternalHandleBridge.ExternalQueryInterfaceNoInterface;
            values[4] = MilExternalHandleBridge.ExternalReleases;
            values[5] = MilExternalHandleBridge.IsConnected ? 1 : 0;
            return 0;
        }

        /// <summary>清掉 WIC 桥的计数（测试用）。</summary>
        [UnmanagedCallersOnly(EntryPoint = "MilBridge_Diag_ResetWicCounters")]
        public static void ResetWicCounters() => MilExternalHandleBridge.ResetCounters();

        /// <summary>存活通道数（= MilNative.ChannelCount，走 public API，不依赖反射）。</summary>
        [UnmanagedCallersOnly(EntryPoint = "MilBridge_Diag_ChannelCount")]
        public static int ChannelCount() => MilNative.ChannelCount;

        // ==================================================================
        //  跨运行时字体面登记：**最近一次失败原因**（M7c3 新增）
        // ==================================================================
        //
        //  M7c3 之前 MilFontFace_RegisterFromFile 把所有异常吞成 0，
        //  "文件不存在" / "Skia 加载不了" / "faceIndex 越界"在调用侧完全一样 ——
        //  T2 实测拿到的就是这种黑盒（registerExport=找到 但 nativeAllocations=0）。
        //  下面三条导出把真因说出来。

        /// <summary>
        /// 最近一次 MilFontFace_RegisterFromFile 的失败码
        /// （0 = 没有失败；其余含义见 <c>MilFontFaceDiagnostics.Failure</c>）。
        /// </summary>
        [UnmanagedCallersOnly(EntryPoint = "MilBridge_Diag_LastFontFaceError")]
        public static int LastFontFaceError() => (int)MilFontFaceDiagnostics.LastFailure;

        private static readonly byte[] s_fontFaceErrorBuffer = new byte[2048];

        /// <summary>
        /// 最近一次失败的详情文本（UTF-8 C 字符串，指向**静态缓冲**，调用方不要 free；
        /// 没有失败记录时返回 null）。典型内容：
        ///   0 → System.DllNotFoundException: DllNotFound_Linux, libSkiaSharp, ...
        /// </summary>
        [UnmanagedCallersOnly(EntryPoint = "MilBridge_Diag_LastFontFaceErrorMessage")]
        public static unsafe byte* LastFontFaceErrorMessage()
        {
            string msg = MilFontFaceDiagnostics.LastMessage;
            if (string.IsNullOrEmpty(msg)) return null;

            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(msg);
            int n = Math.Min(utf8.Length, s_fontFaceErrorBuffer.Length - 1);
            for (int i = 0; i < n; i++) s_fontFaceErrorBuffer[i] = utf8[i];
            s_fontFaceErrorBuffer[n] = 0;
            fixed (byte* p = s_fontFaceErrorBuffer) { return p; }
        }

        /// <summary>清掉字体面失败记录（测试/诊断用）。</summary>
        [UnmanagedCallersOnly(EntryPoint = "MilBridge_Diag_ResetFontFaceError")]
        public static void ResetFontFaceError() => MilFontFaceDiagnostics.Reset();

        /// <summary>
        /// dladdr 自定位到的 .so 目录（UTF-8 C 字符串，指向静态缓冲；失败返回 null）。
        /// 用来判断"镜像知不知道自己在哪" —— libSkiaSharp 就是相对这个目录找的。
        /// </summary>
        [UnmanagedCallersOnly(EntryPoint = "MilBridge_Diag_SelfDirectory")]
        public static unsafe byte* SelfDirectory() => NativeSearchPath.SelfDirectoryPtr();
    }
}
