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
            _ = THIS_PTR;
            _ = src;
            return HResult.E_NOTIMPL;
        }

        /// <summary>停止播放。E_NOTIMPL。</summary>
        public static int MILMediaStop(IntPtr THIS_PTR)
        {
            _ = THIS_PTR;
            return HResult.E_NOTIMPL;
        }

        /// <summary>关闭媒体。E_NOTIMPL。</summary>
        public static int MILMediaClose(IntPtr THIS_PTR)
        {
            _ = THIS_PTR;
            return HResult.E_NOTIMPL;
        }

        /// <summary>取播放位置（100ns）。E_NOTIMPL，pllTime 置 0。</summary>
        public static int MILMediaGetPosition(IntPtr THIS_PTR, ref long pllTime)
        {
            _ = THIS_PTR;
            pllTime = 0;
            return HResult.E_NOTIMPL;
        }

        /// <summary>设置播放位置。E_NOTIMPL。</summary>
        public static int MILMediaSetPosition(IntPtr THIS_PTR, long llTime)
        {
            _ = THIS_PTR;
            _ = llTime;
            return HResult.E_NOTIMPL;
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
            _ = THIS_PTR;
            pIsBuffering = false;
            return HResult.E_NOTIMPL;
        }

        /// <summary>是否可暂停。E_NOTIMPL，pCanPause 置 false。</summary>
        public static int MILMediaCanPause(IntPtr THIS_PTR, ref bool pCanPause)
        {
            _ = THIS_PTR;
            pCanPause = false;
            return HResult.E_NOTIMPL;
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
            _ = THIS_PTR;
            _ = dblRate;
            return HResult.E_NOTIMPL;
        }

        /// <summary>是否有视频轨。E_NOTIMPL，pfHasVideo 置 false（没有媒体 = 没有视频轨）。</summary>
        public static int MILMediaHasVideo(IntPtr THIS_PTR, ref bool pfHasVideo)
        {
            _ = THIS_PTR;
            pfHasVideo = false;
            return HResult.E_NOTIMPL;
        }

        /// <summary>是否有音频轨。E_NOTIMPL，pfHasAudio 置 false。</summary>
        public static int MILMediaHasAudio(IntPtr THIS_PTR, ref bool pfHasAudio)
        {
            _ = THIS_PTR;
            pfHasAudio = false;
            return HResult.E_NOTIMPL;
        }

        /// <summary>视频自然高度。E_NOTIMPL，puiHeight 置 0。</summary>
        public static int MILMediaGetNaturalHeight(IntPtr THIS_PTR, ref uint puiHeight)
        {
            _ = THIS_PTR;
            puiHeight = 0;
            return HResult.E_NOTIMPL;
        }

        /// <summary>视频自然宽度。E_NOTIMPL，puiWidth 置 0。</summary>
        public static int MILMediaGetNaturalWidth(IntPtr THIS_PTR, ref uint puiWidth)
        {
            _ = THIS_PTR;
            puiWidth = 0;
            return HResult.E_NOTIMPL;
        }

        /// <summary>媒体总时长（100ns）。E_NOTIMPL，pllLength 置 0。</summary>
        public static int MILMediaGetMediaLength(IntPtr THIS_PTR, ref long pllLength)
        {
            _ = THIS_PTR;
            pllLength = 0;
            return HResult.E_NOTIMPL;
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
    }
}
