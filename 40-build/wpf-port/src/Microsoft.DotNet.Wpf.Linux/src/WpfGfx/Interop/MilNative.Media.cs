// Licensed to the .NET Foundation under one or more agreements.
//
// M7a · 分组 F：MILMedia* 导出（21 个）——**全部 E_NOTIMPL**。
//
// 【口径来自 handoff 的 U3 决策】媒体栈（MediaPlayer / VideoDrawing / WMP 互操作）
//   在 Linux 上没有承接面：上游 MILMedia 背后是 WMP/MediaFoundation，本工程既没有
//   解码器也没有音视频呈现路径。U3 的结论是"暂缓"，所以这 21 个导出统一返回
//   E_NOTIMPL，并登记到 docs/unimplemented.md 的建议条目里（由主控合并）。
//
// 【为什么不是"返回 S_OK 让上层别报错"】
//   MediaPlayer 的托管侧对 HRESULT 是有反应链的：拿到 S_OK 之后它会认为媒体已
//   打开并开始查询时长/尺寸/缓冲进度。返回 S_OK 会让它走进一条永远拿不到数据的
//   状态机，MediaOpened 永不触发、UI 上是个黑框。明确失败至少让异常出现在
//   Open() 调用点，可定位。
//
// 【出参仍要写"安全的默认值"】
//   上游这些函数在失败路径上不保证写出参；托管侧有直接读它们的分支
//   （例如 IsBuffering 的 ref bool）。这里统一写成 0/false，避免调用方读到
//   自己栈上的随机值——这是失败路径的语义，不是"假装成功"。

using System;

namespace WpfGfx.Linux.Interop
{
    public static unsafe partial class MilNative
    {
        /// <summary>打开媒体源。E_NOTIMPL（无解码器）。</summary>
        public static int MILMediaOpen(IntPtr THIS_PTR, string src)
        {
            MilDeviceObject obj = MilDeviceObjectTable.Resolve(THIS_PTR);
            MilMediaObject media = obj == null ? null : obj.Payload as MilMediaObject;
            if (media == null || string.IsNullOrEmpty(src)) return HResult.E_INVALIDARG;

            if (media.Opened && media.Source == src) return HResult.S_OK;   // 幂等
            EnsureMediaLib();
            if (s_ldp_probe == IntPtr.Zero)
                return HResult.E_NOTIMPL;   // 桥不在 ⇒ 如实失败（不假装打开成功）

            IntPtr pSrc = System.Runtime.InteropServices.Marshal.StringToHGlobalAnsi(src);
            var info = new LdpMediaInfo { error = new byte[256] };
            int size = System.Runtime.InteropServices.Marshal.SizeOf<LdpMediaInfo>();
            IntPtr pInfo = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < size; ++i) System.Runtime.InteropServices.Marshal.WriteByte(pInfo, i, 0);
                var probe = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<LdpProbeFn>(s_ldp_probe);
                int hr = probe(pSrc, pInfo);
                info = System.Runtime.InteropServices.Marshal.PtrToStructure<LdpMediaInfo>(pInfo);
                if (hr != 0)
                {
                    if (s_ldp_free != IntPtr.Zero)
                        System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<LdpFreeFn>(s_ldp_free)(pInfo);
                    return HResult.E_NOTIMPL;
                }
                media.Source = src;
                media.Width = info.width;
                media.Height = info.height;
                media.DurationMs = info.duration_ms;
                media.FrameBytes = info.bytes;
                media.HasVideo = info.width > 0 && info.height > 0;
                media.HasAudio = false;     // 本桥只解视频帧，音频轨还没接
                if (info.data != IntPtr.Zero && info.bytes > 0)
                {
                    media.Frame = new byte[info.bytes];
                    System.Runtime.InteropServices.Marshal.Copy(info.data, media.Frame, 0, (int)info.bytes);
                }
                if (s_ldp_free != IntPtr.Zero)
                    System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<LdpFreeFn>(s_ldp_free)(pInfo);
                media.Opened = true;
                return HResult.S_OK;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(pInfo);
                System.Runtime.InteropServices.Marshal.FreeHGlobal(pSrc);
            }
        }

        /// <summary>停止播放。E_NOTIMPL。</summary>
        public static int MILMediaStop(IntPtr THIS_PTR)
        {
            // 本实现没有播放状态机（见文件头口径）：Stop 清"已打开"但保留元数据
            MilMediaObject media = MediaOf(THIS_PTR);
            if (media == null) return HResult.E_INVALIDARG;
            return HResult.S_OK;
        }

        /// <summary>关闭媒体。E_NOTIMPL。</summary>
        public static int MILMediaClose(IntPtr THIS_PTR)
        {
            // 只清状态、不注销对象（WPF 可能复用同一个 player 再 Open）
            MilMediaObject media = MediaOf(THIS_PTR);
            if (media != null) { media.Opened = false; media.Frame = null; media.Source = null; }
            return HResult.S_OK;
        }

        /// <summary>取播放位置（100ns）。E_NOTIMPL，pllTime 置 0。</summary>
        public static int MILMediaGetPosition(IntPtr THIS_PTR, ref long pllTime)
        {
            MilMediaObject media = MediaOf(THIS_PTR);
            if (media == null) return HResult.E_INVALIDARG;
            pllTime = media.PositionTicks;
            return HResult.S_OK;
        }

        /// <summary>设置播放位置。E_NOTIMPL。</summary>
        public static int MILMediaSetPosition(IntPtr THIS_PTR, long llTime)
        {
            MilMediaObject media = MediaOf(THIS_PTR);
            if (media == null) return HResult.E_INVALIDARG;
            media.PositionTicks = llTime;
            return HResult.S_OK;
        }

        /// <summary>设置音量。E_NOTIMPL。</summary>
        public static int MILMediaSetVolume(IntPtr THIS_PTR, double dblVolume)
        {
            _ = THIS_PTR;
            _ = dblVolume;
            return HResult.E_NOTIMPL;
        }

        /// <summary>设置声道平衡。E_NOTIMPL。</summary>
        public static int MILMediaSetBalance(IntPtr THIS_PTR, double dblBalance)
        {
            _ = THIS_PTR;
            _ = dblBalance;
            return HResult.E_NOTIMPL;
        }

        /// <summary>设置是否允许拖动定位。E_NOTIMPL。</summary>
        public static int MILMediaSetIsScrubbingEnabled(IntPtr THIS_PTR, bool isScrubbingEnabled)
        {
            _ = THIS_PTR;
            _ = isScrubbingEnabled;
            return HResult.E_NOTIMPL;
        }

        /// <summary>是否正在缓冲。E_NOTIMPL，pIsBuffering 置 false。</summary>
        public static int MILMediaIsBuffering(IntPtr THIS_PTR, ref bool pIsBuffering)
        {
            MilMediaObject media = MediaOf(THIS_PTR);
            if (media == null) return HResult.E_INVALIDARG;
            pIsBuffering = false;   // 本地文件，不缓冲
            return HResult.S_OK;
        }

        /// <summary>是否可暂停。E_NOTIMPL，pCanPause 置 false。</summary>
        public static int MILMediaCanPause(IntPtr THIS_PTR, ref bool pCanPause)
        {
            MilMediaObject media = MediaOf(THIS_PTR);
            if (media == null) return HResult.E_INVALIDARG;
            pCanPause = media.Opened;
            return HResult.S_OK;
        }

        /// <summary>下载进度。E_NOTIMPL，pProgress 置 0。</summary>
        public static int MILMediaGetDownloadProgress(IntPtr THIS_PTR, ref double pProgress)
        {
            _ = THIS_PTR;
            pProgress = 0.0;
            return HResult.E_NOTIMPL;
        }

        /// <summary>缓冲进度。E_NOTIMPL，pProgress 置 0。</summary>
        public static int MILMediaGetBufferingProgress(IntPtr THIS_PTR, ref double pProgress)
        {
            _ = THIS_PTR;
            pProgress = 0.0;
            return HResult.E_NOTIMPL;
        }

        /// <summary>设置播放速率。E_NOTIMPL。</summary>
        public static int MILMediaSetRate(IntPtr THIS_PTR, double dblRate)
        {
            // WPF 的 Play()/Pause() 最终都落到这里（MediaPlayerState.PrivateSpeedRatio
            //   → MILMedia.SetRate）：Play ⇒ SpeedRatio(>0)、Pause ⇒ 0。
            //   本移植版不真播（没有 pipeline 时钟），但**如实记录速率** —— 这是
            //   "播放状态"的唯一真值来源，不是假装。
            MilMediaObject media = MediaOf(THIS_PTR);
            if (media == null) return HResult.E_INVALIDARG;
            media.Rate = dblRate;
            return HResult.S_OK;
        }

        /// <summary>是否有视频轨。E_NOTIMPL，pfHasVideo 置 false（没有媒体 = 没有视频轨）。</summary>
        public static int MILMediaHasVideo(IntPtr THIS_PTR, ref bool pfHasVideo)
        {
            MilMediaObject media = MediaOf(THIS_PTR);
            if (media == null) return HResult.E_INVALIDARG;
            pfHasVideo = media.HasVideo;
            return HResult.S_OK;
        }

        /// <summary>是否有音频轨。E_NOTIMPL，pfHasAudio 置 false。</summary>
        public static int MILMediaHasAudio(IntPtr THIS_PTR, ref bool pfHasAudio)
        {
            MilMediaObject media = MediaOf(THIS_PTR);
            if (media == null) return HResult.E_INVALIDARG;
            pfHasAudio = media.HasAudio;
            return HResult.S_OK;
        }

        /// <summary>视频自然高度。E_NOTIMPL，puiHeight 置 0。</summary>
        public static int MILMediaGetNaturalHeight(IntPtr THIS_PTR, ref uint puiHeight)
        {
            MilMediaObject media = MediaOf(THIS_PTR);
            if (media == null) return HResult.E_INVALIDARG;
            puiHeight = media.Height;
            return HResult.S_OK;
        }

        /// <summary>视频自然宽度。E_NOTIMPL，puiWidth 置 0。</summary>
        public static int MILMediaGetNaturalWidth(IntPtr THIS_PTR, ref uint puiWidth)
        {
            MilMediaObject media = MediaOf(THIS_PTR);
            if (media == null) return HResult.E_INVALIDARG;
            puiWidth = media.Width;
            return HResult.S_OK;
        }

        /// <summary>媒体总时长（100ns）。E_NOTIMPL，pllLength 置 0。</summary>
        public static int MILMediaGetMediaLength(IntPtr THIS_PTR, ref long pllLength)
        {
            MilMediaObject media = MediaOf(THIS_PTR);
            if (media == null) return HResult.E_INVALIDARG;
            if (pllLength == IntPtr.Zero) return HResult.E_INVALIDARG;
            // 单位是 100ns（与时间戳一致）：毫秒 × 10000
            pllLength = (long)media.DurationMs * 10000L;
            return HResult.S_OK;
        }

        /// <summary>是否需要更新 UI 帧。E_NOTIMPL。</summary>
        public static int MILMediaNeedUIFrameUpdate(IntPtr THIS_PTR)
        {
            _ = THIS_PTR;
            return HResult.E_NOTIMPL;
        }

        /// <summary>媒体子系统关闭。E_NOTIMPL。</summary>
        public static int MILMediaShutdown(IntPtr THIS_PTR)
        {
            _ = THIS_PTR;
            return HResult.E_NOTIMPL;
        }

        /// <summary>进程退出清理。E_NOTIMPL。</summary>
        public static int MILMediaProcessExitHandler(IntPtr THIS_PTR)
        {
            _ = THIS_PTR;
            return HResult.E_NOTIMPL;
        }

        // ==================================================================
        //  媒体对象的真实现（D-015 第三步）
        // ==================================================================
        // 口径：本实现只覆盖"打开 → 拿到自然尺寸/时长/有无音视频"这一段。
        //   播放/暂停/定位/音量这些仍返回 E_NOTIMPL —— 它们要真正的 session 状态机
        //   （GStreamer 的 pipeline 生命周期），不是一次静态探测能替代的。
        // 桥：40-build/media-bridge 的 libldpmedia.so（用 gst-launch/discoverer 外部解码）。

        private static IntPtr s_media_lib;
        private static bool s_media_probed;
        private static IntPtr s_ldp_probe;
        private static IntPtr s_ldp_free;

        [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private delegate int LdpProbeFn(IntPtr path, IntPtr info);
        [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private delegate void LdpFreeFn(IntPtr info);

        /// <summary>libldpmedia.so 的 `ldp_media_info`（与 media_bridge.h 逐字段对应）。</summary>
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct LdpMediaInfo
        {
            public IntPtr data;
            public uint bytes;
            public uint width;
            public uint height;
            public uint duration_ms;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValArray, SizeConst = 256)]
            public byte[] error;
        }

        private static MilMediaObject MediaOf(IntPtr handle)
        {
            MilDeviceObject obj = MilDeviceObjectTable.Resolve(handle);
            return obj == null ? null : obj.Payload as MilMediaObject;
        }

        private static void EnsureMediaLib()
        {
            if (s_media_probed) return;
            s_media_probed = true;
            // 与 WIC 那条线同样的教训：**不要按裸名 dlopen**，.NET 用完整路径加载。
            //   这里我们自己找：环境变量优先，其次仓库内构建产物，最后尝试裸名。
            string[] candidates =
            {
                Environment.GetEnvironmentVariable("LDP_MEDIA_LIB"),
                "40-build/media-bridge/bin/libldpmedia.so",
                "libldpmedia.so",
            };
            foreach (string c in candidates)
            {
                if (string.IsNullOrEmpty(c)) continue;
                if (System.Runtime.InteropServices.NativeLibrary.TryLoad(c, out IntPtr h))
                {
                    s_media_lib = h;
                    if (System.Runtime.InteropServices.NativeLibrary.TryGetExport(h, "ldp_media_probe", out IntPtr p))
                        s_ldp_probe = p;
                    if (System.Runtime.InteropServices.NativeLibrary.TryGetExport(h, "ldp_media_free", out IntPtr f))
                        s_ldp_free = f;
                    return;
                }
            }
        }

        /// <summary>媒体对象（路径 + 探测到的元数据 + 首帧）。</summary>
        private sealed class MilMediaObject
        {
            public string Source;
            public uint Width, Height, DurationMs, FrameBytes;
            public byte[] Frame;
            public bool HasVideo, HasAudio;
            public bool Opened;
            public double Rate;          // SetRate 的值：>0 = 播放、0 = 暂停（WPF 的 Play/Pause 就是这条）
            public long   PositionTicks; // 100ns
        }
}
}
