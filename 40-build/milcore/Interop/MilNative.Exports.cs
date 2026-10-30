// Licensed to the .NET Foundation under one or more agreements.
//
// M7a：**导出覆盖清单** —— 托管侧需要的全部 108 个 DllImport(DllImport.MilCore) 导出名，
// 以及每一个的当前实现深度。
//
// 【清单怎么来的（可复现）】
//   扫描集合 = PresentationCore in-tree 8 个含 DllImport.MilCore 的文件
//     UnsafeNativeMethodsMilCoreApi.cs(44 条) / Composition.cs(8) /
//     SafeNativeMethodsMilCoreApi.cs(3) / MILUtilities.cs(2) /
//     MediaContextNotificationWindow.cs(2) / HwndTarget.cs(2) /
//     StreamAsIStream.cs(1) / EventProxy.cs(1)                       = 63 条属性
//   ∪ Common/Graphics（exports.cs 19 + wgx_exports.cs 28）             = 47 条属性
//   合计 **110 条属性**，去掉 EntryPoint 重名后 **108 个导出名**（106 个 C# 方法名）。
//   扫描方式：先剥注释（跨行属性块里有 /*HRESULT*/ 这类块注释，不剥会漏 40+ 条），
//   再用跨行正则解析属性 + extern 声明，取 EntryPoint ?? 方法名。
//
//   ⚠️ 与 docs/U2-PresentationCore-scan.md §3.4 的差异：U2 报告写的是
//   「104 条属性 / 102 个导出名 / 缺口 95」。复核后确认 Common/Graphics 是 47 条
//   而不是 41 条（漏了 6 条），因此正确数字是 **110 / 108**，缺口是
//   **94 个导出名**（108 − 14 个既有导出名）。按方法名算是 90 个（106 − 16）。
//   本轮按**导出名**补齐 94 个（多出的 MilChannel_CloseBatch / MilChannel_CommitChannel
//   是既有功能的别名，只为让导出表完整）。
//
// 【ExportDepth 的含义】
//   Real     —— 真实现：有真实数据面/几何运算/状态机，能用数值或像素断言（见 MilExportTests）
//   Identity —— 身份映射：登记 / 查表 / 解绑 / 幂等 + 错误码保真；真实接窗或真实
//               native 回调留给后续 milestone（每条在源文件里有"当前实现到哪一步"的注释）
//   State    —— 纯状态/同步：进程级开关的存取、可重入锁、单调计数器
//   NotImpl  —— E_NOTIMPL，并登记到 docs/unimplemented.md 的建议条目
//
//   注：MilResource_SendCommandMedia 仍是"从不接"的既有导出（M1 决策 4：不接 MediaPlayer）。
//   MilResource_SendCommandBitmapSource 已从 NotImpl **升为 Real**（M7c/#24：接进既有的
//   `MilCmdBitmapSource`(0x0c) 命令路径，见 MilNative.cs 的注释），因此不再登记为缺口。
//
//   【T1/M7c 更新 · 2026-09-10】MilChannel_SetNotificationWindow 原先是第 3 个 M1 存量
//   E_NOTIMPL，现由 T1 落地为**真实现**（把 (hwnd, message) 登记到通道，幂等/可查询/可解绑，
//   见 Interop/MilNative.NotificationWindow.cs 与 docs/U2-M7c-report.md §3.4），
//   实现深度 NotImpl → **State**。于是：
//       Real 59 · Identity 9 · **State 12** · NotImpl **28**（合计仍 108）。
//   ⚠️ 改这一条**必须重建 build/MilBridge 的 wpfgfx_cor3.so** 才会在运行时生效。
//
//   【T1/M7c2 更新 · 2026-09-10】再新增 **1 个不在上游清单里的导出**：
//   `MilFontFace_RegisterFromFile`（跨运行时字体面登记，Real 档）。
//   于是总计 **109**：Real **60** · Identity 9 · State 12 · NotImpl 28。
//   它同样必须由 build/MilBridge 的 AOT 包装导出（生成器 gen-exports.py 的
//   EXTRA_EXPORTS 列表），否则托管侧 NativeLibrary.GetExport 找不到。

using System;
using System.Collections.Generic;
using System.Reflection;

namespace WpfGfx.Linux.Interop
{
    /// <summary>一个 MIL 导出的当前实现深度。</summary>
    public enum ExportDepth
    {
        /// <summary>真实现（有真实语义，可数值/像素断言）。</summary>
        Real = 0,

        /// <summary>身份映射（登记/查表/解绑/幂等/错误码；真实接窗或回调待后续 milestone）。</summary>
        Identity = 1,

        /// <summary>纯状态或同步（开关存取、可重入锁、单调计数器）。</summary>
        State = 2,

        /// <summary>E_NOTIMPL（已登记到未实现台账）。</summary>
        NotImpl = 3,
    }

    public static unsafe partial class MilNative
    {
        /// <summary>
        /// 托管侧需要的全部 **109** 个导出名 → 实现深度。
        /// = 上游 108 个 [DllImport(DllImport.MilCore)] 导出名
        ///   + 1 个本工程新增的跨运行时导出（MilFontFace_RegisterFromFile，T1/M7c2）。
        /// 这个表同时是**测试的判据**：MilExportTests 用反射检查每一个名字都能在
        /// <see cref="MilNative"/> 上找到同名 public static 方法。
        /// </summary>
        public static readonly IReadOnlyDictionary<string, ExportDepth> ExportManifest =
            new Dictionary<string, ExportDepth>(StringComparer.Ordinal)
            {
            { "GetNextPerfElementId", ExportDepth.Real },
            { "IWICColorContext_GetExifColorSpace_Proxy", ExportDepth.Real },
            { "IWICColorContext_GetProfileBytes_Proxy", ExportDepth.Real },
            { "IWICColorContext_GetType_Proxy", ExportDepth.Real },
            { "InteropDeviceBitmap_AddDirtyRect", ExportDepth.NotImpl },
            { "InteropDeviceBitmap_Create", ExportDepth.NotImpl },
            { "InteropDeviceBitmap_Detach", ExportDepth.NotImpl },
            { "InteropDeviceBitmap_GetAsSoftwareBitmap", ExportDepth.NotImpl },
            { "MIL3DCalcProjected2DBounds", ExportDepth.Real },
            { "MILAddRef", ExportDepth.Real },
            { "MILCreateEventProxy", ExportDepth.Identity },
            { "MILCreateFactory", ExportDepth.Real },
            { "MILCreateStreamFromStreamDescriptor", ExportDepth.Identity },
            { "MILFactoryCreateBitmapRenderTarget", ExportDepth.Real },
            { "MILFactoryCreateMediaPlayer", ExportDepth.NotImpl },
            { "MILFactoryCreateSWRenderTargetForBitmap", ExportDepth.Real },
            { "MILIStreamWrite", ExportDepth.Real },
            { "MILMediaCanPause", ExportDepth.NotImpl },
            { "MILMediaClose", ExportDepth.NotImpl },
            { "MILMediaGetBufferingProgress", ExportDepth.NotImpl },
            { "MILMediaGetDownloadProgress", ExportDepth.NotImpl },
            { "MILMediaGetMediaLength", ExportDepth.NotImpl },
            { "MILMediaGetNaturalHeight", ExportDepth.NotImpl },
            { "MILMediaGetNaturalWidth", ExportDepth.NotImpl },
            { "MILMediaGetPosition", ExportDepth.NotImpl },
            { "MILMediaHasAudio", ExportDepth.NotImpl },
            { "MILMediaHasVideo", ExportDepth.NotImpl },
            { "MILMediaIsBuffering", ExportDepth.NotImpl },
            { "MILMediaNeedUIFrameUpdate", ExportDepth.NotImpl },
            { "MILMediaOpen", ExportDepth.NotImpl },
            { "MILMediaProcessExitHandler", ExportDepth.NotImpl },
            { "MILMediaSetBalance", ExportDepth.NotImpl },
            { "MILMediaSetIsScrubbingEnabled", ExportDepth.NotImpl },
            { "MILMediaSetPosition", ExportDepth.NotImpl },
            { "MILMediaSetRate", ExportDepth.NotImpl },
            { "MILMediaSetVolume", ExportDepth.NotImpl },
            { "MILMediaShutdown", ExportDepth.NotImpl },
            { "MILMediaStop", ExportDepth.NotImpl },
            { "MILQueryInterface", ExportDepth.Real },
            { "MILRelease", ExportDepth.Real },
            { "MILRenderTargetBitmapClear", ExportDepth.Real },
            { "MILRenderTargetBitmapGetBitmap", ExportDepth.Real },
            { "MILSwDoubleBufferedBitmapAddDirtyRect", ExportDepth.Real },
            { "MILSwDoubleBufferedBitmapCreate", ExportDepth.Real },
            { "MILSwDoubleBufferedBitmapGetBackBuffer", ExportDepth.Real },
            { "MILSwDoubleBufferedBitmapProtectBackBuffer", ExportDepth.Real },
            { "MILUpdateSystemParametersInfo", ExportDepth.State },
            { "MilChannel_AppendCommandData", ExportDepth.Real },
            { "MilChannel_BeginCommand", ExportDepth.Real },
            { "MilChannel_CloseBatch", ExportDepth.Real },
            { "MilChannel_CommitChannel", ExportDepth.Real },
            { "MilChannel_EndCommand", ExportDepth.Real },
            { "MilChannel_GetMarshalType", ExportDepth.Real },
            { "MilChannel_SetNotificationWindow", ExportDepth.State },
            { "MilCompositionEngine_DeinitializePartitionManager", ExportDepth.Real },
            { "MilCompositionEngine_EnterCompositionEngineLock", ExportDepth.State },
            { "MilCompositionEngine_EnterMediaSystemLock", ExportDepth.State },
            { "MilCompositionEngine_ExitCompositionEngineLock", ExportDepth.State },
            { "MilCompositionEngine_ExitMediaSystemLock", ExportDepth.State },
            { "MilCompositionEngine_InitializePartitionManager", ExportDepth.Real },
            { "MilComposition_PeekNextMessage", ExportDepth.Real },
            { "MilComposition_SyncFlush", ExportDepth.Real },
            { "MilComposition_WaitForNextMessage", ExportDepth.Real },
            { "MilConnection_CreateChannel", ExportDepth.Real },
            { "MilConnection_DestroyChannel", ExportDepth.Real },
            // T1/M7c2：**新增导出**（不在上游 108 个 [DllImport] 之内）。
            //   跨运行时字体面登记：托管侧经 C ABI 调它，由 .so 自己的运行时
            //   创建 SKTypeface 并登记进它那份 MilFontFaceTable。
            //   背景见 Interop/MilNative.FontFace.cs 顶部；调用方是 T2 的
            //   FontHandleTable.PathTokenAllocator。
            { "MilFontFace_RegisterFromFile", ExportDepth.Real },
            { "MilContent_AttachToHwnd", ExportDepth.Identity },
            { "MilContent_DetachFromHwnd", ExportDepth.Identity },
            { "MilCreateReversePInvokeWrapper", ExportDepth.Identity },
            { "MilGlyphCache_AppendCommandDataAtRenderTime", ExportDepth.Real },
            { "MilGlyphCache_BeginCommandAtRenderTime", ExportDepth.Real },
            { "MilGlyphCache_EndCommandAtRenderTime", ExportDepth.Real },
            { "MilGlyphRun_GetGlyphOutline", ExportDepth.Real },
            { "MilGlyphRun_ReleasePathGeometryData", ExportDepth.Real },
            { "MilGlyphRun_SetGeometryAtRenderTime", ExportDepth.Real },
            { "MilReleasePInvokePtrBlocking", ExportDepth.State },
            { "MilResource_CreateCWICWrapperBitmap", ExportDepth.Real },
            { "MilResource_CreateOrAddRefOnChannel", ExportDepth.Real },
            { "MilResource_DuplicateHandle", ExportDepth.Real },
            { "MilResource_GetRefCountOnChannel", ExportDepth.Real },
            { "MilResource_ReleaseOnChannel", ExportDepth.Real },
            { "MilResource_SendCommand", ExportDepth.Real },
            { "MilResource_SendCommandBitmapSource", ExportDepth.Real },   // M7c/#24：位图源命令已接线（0x0c）
            { "MilResource_SendCommandMedia", ExportDepth.NotImpl },
            { "MilUtility_ArcToBezier", ExportDepth.Real },
            { "MilUtility_CopyPixelBuffer", ExportDepth.Real },
            { "MilUtility_GeometryGetArea", ExportDepth.Real },
            { "MilUtility_GetPointAtLengthFraction", ExportDepth.Real },
            { "MilUtility_GetTileBrushMapping", ExportDepth.Real },
            { "MilUtility_PathGeometryBounds", ExportDepth.Real },
            { "MilUtility_PathGeometryCombine", ExportDepth.Real },
            { "MilUtility_PathGeometryFlatten", ExportDepth.Real },
            { "MilUtility_PathGeometryHitTest", ExportDepth.Real },
            { "MilUtility_PathGeometryHitTestPathGeometry", ExportDepth.Real },
            { "MilUtility_PathGeometryOutline", ExportDepth.Real },
            { "MilUtility_PathGeometryWiden", ExportDepth.Real },
            { "MilUtility_PolygonBounds", ExportDepth.Real },
            { "MilUtility_PolygonHitTest", ExportDepth.Real },
            { "MilVersionCheck", ExportDepth.Real },
            { "MilVisualTarget_AttachToHwnd", ExportDepth.Identity },
            { "MilVisualTarget_DetachFromHwnd", ExportDepth.Identity },
            { "RenderOptions_EnableHardwareAccelerationInRdp", ExportDepth.State },
            { "RenderOptions_ForceSoftwareRenderingModeForProcess", ExportDepth.State },
            { "RenderOptions_IsSoftwareRenderingForcedForProcess", ExportDepth.State },
            { "WgxConnection_Create", ExportDepth.Identity },
            { "WgxConnection_Disconnect", ExportDepth.Identity },
            { "WgxConnection_SameThreadPresent", ExportDepth.Real },
            { "WgxConnection_ShouldForceSoftwareForGraphicsStreamClient", ExportDepth.State },
            { "WpfGfx_SetDisableBoundsCheckProtection", ExportDepth.State },
            };

        /// <summary>清单里所有导出名（108 个，字典序遍历）。</summary>
        public static IReadOnlyCollection<string> ExportNames
        {
            get
            {
                var list = new List<string>(ExportManifest.Keys);
                list.Sort(StringComparer.Ordinal);
                return list;
            }
        }

        /// <summary>实现深度为 E_NOTIMPL 的导出名（未实现台账的建议来源，字典序）。</summary>
        public static IReadOnlyCollection<string> NotImplExportNames
        {
            get
            {
                var list = new List<string>();
                foreach (KeyValuePair<string, ExportDepth> pair in ExportManifest)
                {
                    if (pair.Value == ExportDepth.NotImpl) list.Add(pair.Key);
                }
                list.Sort(StringComparer.Ordinal);
                return list;
            }
        }

        /// <summary>清单里没有对应 public static 方法的导出名（应当恒为空）。</summary>
        public static IReadOnlyCollection<string> MissingExports()
        {
            var missing = new List<string>();
            Type type = typeof(MilNative);

            foreach (string name in ExportManifest.Keys)
            {
                bool found = false;
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (string.Equals(method.Name, name, StringComparison.Ordinal))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found) missing.Add(name);
            }

            missing.Sort(StringComparer.Ordinal);
            return missing;
        }

        /// <summary>清单里深度为 Real 的导出数（报告用）。</summary>
        public static int RealExportCount
        {
            get
            {
                int n = 0;
                foreach (ExportDepth depth in ExportManifest.Values)
                    if (depth == ExportDepth.Real) n++;
                return n;
            }
        }

        /// <summary>
        /// 一次性清空全部进程内状态（句柄表 / 通道后向队列 / 进程级开关）。
        /// **只给测试用**：xunit 默认并行跑测试类，跨类共享的进程级表会让断言互相干扰。
        /// </summary>
        public static void ResetProcessStateForTests()
        {
            // 债务 #1 的长期仪器（缺省关）：报"上一个用例留下了几个通道"。
            // **只读**：这里只快照 + 打印，`MilChannelRegistry` 一如既往**刻意不清**。
            Resources.MilChannelRegistry.ReportLeaks("ResetProcessStateForTests");

            MilHwndRegistry.Reset();
            MilConnectionTable.Reset();
            MilDeviceObjectTable.Reset();
            // M7c 轨道 B：后台缓冲令牌 → 设备对象的**别名表**也要清，否则"上一个测试留下的
            //   别名"会让下一个测试的配平断言看到假的稳态（清表顺序：别名先于设备对象，
            //   因为别名指向设备对象句柄）。
            MilBackBufferSourceTable.Reset();
            MilDeviceObjectTable.Reset();
            MilPixelBufferTable.Reset();
            MilColorContextTable.Reset();
            MilFontFaceTable.Reset();
            MilRenderTimeTargetTable.Reset();
            MilReversePInvokeTable.Reset();
            MilChannelBackChannel.Reset();
            // 【刻意不清 MilChannelNotificationRegistry】理由与上面不清 MilChannelRegistry 相同：
            //   它的键就是**通道句柄**，而通道句柄是跨测试类共享的全局量。
            //   在这里清会把别的测试类正在用的通道登记一起抹掉（xunit 默认并行跑测试类）。
            //   需要干净基线的测试请直接调 MilChannelNotificationRegistry.Reset()，
            //   或把断言收敛到自己创建的那个通道句柄上。
            MilPathGeometryDataTable.Reset();
            MilCompositionEngineState.Reset();
            // M7c：呈现绑定表 + 诊断缓冲。**不销毁任何 X11 窗口**——
            // 绑定的是"包装既有窗口"的目标，Dispose 只释放 GC（见 X11Window.Wrap）。
            MilPresentation.Reset();
            MilDiagnostics.Reset();
        }
    }
}
