// Licensed to the .NET Foundation under one or more agreements.
//
// DUCE 顶层命令分发器（契约 IMilCommandDispatcher 的实现）。
//
// 输入：一条完整命令的字节（命令头 + 载荷）。
// 输出：把字段落到通道的**资源图**上（Resources/MilResources.cs），返回 HRESULT。
//
// 【三条硬规则】
//   1. 命令长度不足固定部分 → E_INVALIDARG（宁可报错，不读越界字节）
//   2. 句柄不在表里      → E_HANDLE
//   3. 句柄指向的资源类型与命令不符 → E_INVALIDARG
//
// M1 返回 E_NOTIMPL 的命令在 MilCommandLayout.s_notImpl，清单见 docs/unimplemented.md。
//
// 118 条的精确拆分（已实现 / 未实现 / 哨兵）由
// CommandCoverageTests.全部命令的去向必须无重叠无遗漏 钉死——
// 这里不重复维护一份会腐烂的数字。

using System;
using System.Collections.Generic;
using WpfGfx.Linux.Contracts;
using WpfGfx.Linux.Interop;
using WpfGfx.Linux.Resources;
using S = WpfGfx.Linux.Commands.MilCommandStructs;

namespace WpfGfx.Linux.Commands
{
    internal sealed class MilCommandDispatcher : IMilCommandDispatcher
    {
        public int Dispatch(ReadOnlySpan<byte> command, IMilChannel channel)
        {
            if (channel is not MilChannel ch) return HResult.E_INVALIDARG;
            if (command.Length < MilCommandDecoder.MinCommandSize) return HResult.E_INVALIDARG;

            MilCmd cmd = MilCommandDecoder.ReadType(command);

            // D3D / 3D / 媒体：M1 不做
            if (MilCommandLayout.IsNotImplemented(cmd)) return HResult.E_NOTIMPL;

            if (cmd == MilCmd.MilCmdInvalid) return HResult.E_INVALIDARG;

            int fixedSize = MilCommandLayout.FixedSize(cmd);
            if (fixedSize < 0) return HResult.E_NOTIMPL;     // 未知命令字
            if (command.Length < fixedSize) return HResult.E_INVALIDARG;

            try
            {
                return DispatchCore(cmd, command, ch);
            }
            catch (ArgumentException)
            {
                return HResult.E_INVALIDARG;
            }
            catch (InvalidOperationException)
            {
                return HResult.E_INVALIDARG;
            }
        }

        public bool IsImplemented(MilCmd cmd) =>
            !MilCommandLayout.IsNotImplemented(cmd) && MilCommandLayout.FixedSize(cmd) > 0;

        // ==================================================================
        //  分发主体
        // ==================================================================

        private static int DispatchCore(MilCmd cmd, ReadOnlySpan<byte> c, MilChannel ch)
        {
            switch (cmd)
            {
                // ---------------- 分区级（无 Handle） ----------------
                case MilCmd.MilCmdPartitionRegisterForNotifications:
                    ch.Partition.RegisterForNotifications =
                        MilCommandDecoder.ReadFixed<S.MILCMD_PARTITION_REGISTERFORNOTIFICATIONS>(c).Enable != 0;
                    return HResult.S_OK;

                case MilCmd.MilCmdChannelRequestTier:
                    ch.Partition.ReturnCommonMinimum =
                        MilCommandDecoder.ReadFixed<S.MILCMD_CHANNEL_REQUESTTIER>(c).ReturnCommonMinimum != 0;
                    return HResult.S_OK;

                case MilCmd.MilCmdPartitionSetVBlankSyncMode:
                    ch.Partition.VBlankSyncMode =
                        MilCommandDecoder.ReadFixed<S.MILCMD_PARTITION_SETVBLANKSYNCMODE>(c).Enable != 0;
                    return HResult.S_OK;

                case MilCmd.MilCmdPartitionNotifyPresent:
                    ch.Partition.LastPresentFrameTime =
                        MilCommandDecoder.ReadFixed<S.MILCMD_PARTITION_NOTIFYPRESENT>(c).FrameTime;
                    return HResult.S_OK;

                case MilCmd.MilCmdPartitionNotifyPolicyChangeForNonInteractiveMode:
                    ch.Partition.RenderEvenWhenNoDisplayDevices =
                        MilCommandDecoder.ReadFixed<S.MILCMD_PARTITION_NOTIFYPOLICYCHANGEFORNONINTERACTIVEMODE>(c)
                            .ShouldRenderEvenWhenNoDisplayDevicesAreAvailable != 0;
                    return HResult.S_OK;

                // ---------------- 传输 / 资源（命令头 8 字节） ----------------
                case MilCmd.MilCmdTransportSyncFlush:
                    return HResult.S_OK;                       // 本实现里批次即同步点

                case MilCmd.MilCmdTransportDestroyResourcesOnChannel:
                    ch.Resources.Clear();
                    return HResult.S_OK;

                case MilCmd.MilCmdChannelCreateResource:
                    // 资源本体由 MilResource_CreateOrAddRefOnChannel 建，这里只是流里的标记。
                    return HResult.S_OK;

                case MilCmd.MilCmdChannelDeleteResource:
                    ch.Resources.Release(MilCommandDecoder.ReadHandle(c), out _);
                    return HResult.S_OK;

                case MilCmd.MilCmdChannelDuplicateHandle:
                    return HResult.S_OK;

                case MilCmd.MilCmdValidateStructureOrder:
                    return HResult.S_OK;                       // 调试用断言，无状态

                case MilCmd.MilCmdInvalid:
                    return HResult.E_INVALIDARG;

                // ---------------- 位图源 (0x0c, 0x0d) ----------------
                // 资源表里 TYPE_BITMAPSOURCE 是占位 MilOpaqueResource，没有对应子类，
                // 所以按 DUCE.ResourceType 解析（Require3D），位图状态挂在该实例上。
                case MilCmd.MilCmdBitmapSource:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_BITMAPSOURCE, out MilResource res);
                    if (HResult.Failed(hr)) return hr;

                    S.MILCMD_BITMAP_SOURCE s = MilCommandDecoder.ReadFixed<S.MILCMD_BITMAP_SOURCE>(c);
                    return MilBitmapSourceTable.ProcessSource(res, s.BitmapToken);
                }

                case MilCmd.MilCmdBitmapInvalidate:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_BITMAPSOURCE, out MilResource res);
                    if (HResult.Failed(hr)) return hr;

                    S.MILCMD_BITMAP_INVALIDATE s = MilCommandDecoder.ReadFixed<S.MILCMD_BITMAP_INVALIDATE>(c);
                    return MilBitmapSourceTable.ProcessInvalidate(res, s.UseDirtyRect != 0, s.DirtyRect);
                }

                // ---------------- Visual ----------------
                case MilCmd.MilCmdVisualCreate:
                    return Require<MilVisualResource>(ch, c, out _);

                case MilCmd.MilCmdVisualSetOffset:
                {
                    int hr = Require<MilVisualResource>(ch, c, out MilVisualResource v);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_VISUAL_SETOFFSET>(c);
                    v.Visual.OffsetX = s.OffsetX;
                    v.Visual.OffsetY = s.OffsetY;
                    // RTL 取证：**只读**诊断（缺省关 `WPF_LINUX_VISTRANS_TRACE=1`，有界 ≤200 行 + 触顶通知）。
                    //   放在赋值**之后**：本诊断不参与任何赋值，关掉即逐字回到原行为。
                    MilVisualTransformDiag.NoteOffset(ch, s.Handle, s.OffsetX, s.OffsetY);
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdVisualSetTransform:
                {
                    int hr = Require<MilVisualResource>(ch, c, out MilVisualResource v);
                    if (HResult.Failed(hr)) return hr;
                    var ts = MilCommandDecoder.ReadFixed<S.MILCMD_VISUAL_SETTRANSFORM>(c);
                    v.Visual.Transform = ts.HTransform;
                    // RTL 取证：打印 `hTransform` 与**解析后的矩阵**（走投影期同一个 `TransformResolver.Resolve`）
                    //   ⇒ 判"PC 有没有把镜像交给 MIL"（见 MilVisualTransformDiag 头注的三选一判据）。
                    MilVisualTransformDiag.NoteTransform(ch, ts.Handle, ts.HTransform);
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdVisualSetEffect:
                {
                    int hr = Require<MilVisualResource>(ch, c, out MilVisualResource v);
                    if (HResult.Failed(hr)) return hr;
                    var se = MilCommandDecoder.ReadFixed<S.MILCMD_VISUAL_SETEFFECT>(c);
                    v.Visual.Effect = se.HEffect;
                    // 【波70 · `D-G71` / `TASK-0403`】不许静默 no-op：本字段**下游没有任何消费者**
                    //   —— 投影层（`Resources/VisualProjection.cs`）不看它，契约类型 `MilVisual`
                    //   （`Contracts/Interfaces.cs:53-79`）连 `Effect` 字段都没有 ⇒ 挂在视觉上的效果
                    //   在投影那一跳就丢了。这里记一条**具名、有界、只读**的台账，把"收得下但不画"
                    //   变成看得见（形态与 D-G58 的 `ch.NoteShaderStubAccepted` 同族）。
                    //   · 放在赋值**之后**：本台账不参与任何赋值 ⇒ 关掉即逐字回到原行为；
                    //   · 只在**非空句柄**时记：上游用 `SetEffect(handle, Null)` 来**清除**效果
                    //     （`upstream/…/Media/Visual.cs:1445`），那不是"被丢弃的效果"，不入账；
                    //   · **刻意不记** `RenderDiagnostics.RecordNotDrawn`：那个计数器是 `WpfTextDemo`
                    //     验收判据②（`未画种类 0`）的输入，而 `Border.Effect = DropShadowEffect` 正是
                    //     走这条命令 ⇒ 记进去会当场把冻死的判据打红（理由逐条见
                    //     `build/MilBridge/W70D-report.md` §2 与 `MilChannel.NoteVisualEffectIgnored` 头注）。
                    if (!se.HEffect.IsNull) ch.NoteVisualEffectIgnored(se.Handle, se.HEffect);
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdVisualSetCacheMode:
                {
                    int hr = Require<MilVisualResource>(ch, c, out MilVisualResource v);
                    if (HResult.Failed(hr)) return hr;
                    v.Visual.CacheMode = MilCommandDecoder.ReadFixed<S.MILCMD_VISUAL_SETCACHEMODE>(c).HCacheMode;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdVisualSetClip:
                {
                    int hr = Require<MilVisualResource>(ch, c, out MilVisualResource v);
                    if (HResult.Failed(hr)) return hr;
                    v.Visual.Clip = MilCommandDecoder.ReadFixed<S.MILCMD_VISUAL_SETCLIP>(c).HClip;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdVisualSetAlpha:
                {
                    int hr = Require<MilVisualResource>(ch, c, out MilVisualResource v);
                    if (HResult.Failed(hr)) return hr;
                    v.Visual.Alpha = MilCommandDecoder.ReadFixed<S.MILCMD_VISUAL_SETALPHA>(c).Alpha;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdVisualSetRenderOptions:
                {
                    int hr = Require<MilVisualResource>(ch, c, out MilVisualResource v);
                    if (HResult.Failed(hr)) return hr;
                    v.Visual.RenderOptions =
                        MilCommandDecoder.ReadFixed<S.MILCMD_VISUAL_SETRENDEROPTIONS>(c).RenderOptions;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdVisualSetContent:
                {
                    int hr = Require<MilVisualResource>(ch, c, out MilVisualResource v);
                    if (HResult.Failed(hr)) return hr;
                    v.Visual.Content = MilCommandDecoder.ReadFixed<S.MILCMD_VISUAL_SETCONTENT>(c).HContent;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdVisualSetAlphaMask:
                {
                    int hr = Require<MilVisualResource>(ch, c, out MilVisualResource v);
                    if (HResult.Failed(hr)) return hr;
                    v.Visual.AlphaMask = MilCommandDecoder.ReadFixed<S.MILCMD_VISUAL_SETALPHAMASK>(c).HAlphaMask;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdVisualRemoveAllChildren:
                {
                    int hr = Require<MilVisualResource>(ch, c, out MilVisualResource v);
                    if (HResult.Failed(hr)) return hr;
                    v.Visual.Children.Clear();
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdVisualRemoveChild:
                {
                    int hr = Require<MilVisualResource>(ch, c, out MilVisualResource v);
                    if (HResult.Failed(hr)) return hr;
                    v.Visual.Children.Remove(
                        MilCommandDecoder.ReadFixed<S.MILCMD_VISUAL_REMOVECHILD>(c).HChild);
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdVisualInsertChildAt:
                {
                    int hr = Require<MilVisualResource>(ch, c, out MilVisualResource v);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_VISUAL_INSERTCHILDAT>(c);
                    int index = (int)Math.Min((uint)s.Index, (uint)v.Visual.Children.Count);
                    v.Visual.Children.Insert(index, s.HChild);
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdVisualSetGuidelineCollection:
                {
                    int hr = Require<MilVisualResource>(ch, c, out MilVisualResource v);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_VISUAL_SETGUIDELINECOLLECTION>(c);
                    int bytes = (s.CountX + s.CountY) * 4;
                    hr = RequirePayload(c, 16, bytes);
                    if (HResult.Failed(hr)) return hr;
                    v.Visual.GuidelinesX = MilCommandDecoder.ReadFloats(c, 16, s.CountX * 4);
                    v.Visual.GuidelinesY = MilCommandDecoder.ReadFloats(c, 16 + s.CountX * 4, s.CountY * 4);
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdVisualSetScrollableAreaClip:
                {
                    int hr = Require<MilVisualResource>(ch, c, out MilVisualResource v);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_VISUAL_SETSCROLLABLEAREACLIP>(c);
                    v.Visual.ScrollableAreaClip = s.Clip;
                    v.Visual.ScrollableAreaClipEnabled = s.IsEnabled != 0;
                    return HResult.S_OK;
                }

                // ---------------- 呈现目标 ----------------
                case MilCmd.MilCmdHwndTargetCreate:
                {
                    int hr = Require<MilHwndTarget>(ch, c, out MilHwndTarget t);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_HWNDTARGET_CREATE>(c);
                    t.NativeWindow = new IntPtr((long)s.Hwnd);
                    t.SectionHandle = new IntPtr((long)s.HSection);
                    t.MasterDevice = s.MasterDevice;
                    t.Width = s.Width;
                    t.Height = s.Height;
                    t.ClearColor = s.ClearColor;
                    t.Flags = s.Flags;
                    t.Bitmap = s.HBitmap;
                    t.Stride = s.Stride;
                    t.PixelFormat = s.EPixelFormat;
                    t.DpiAwarenessContext = s.DpiAwarenessContext;
                    t.DpiX = s.DpiX;
                    t.DpiY = s.DpiY;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdHwndTargetSuppressLayered:
                {
                    int hr = Require<MilHwndTarget>(ch, c, out MilHwndTarget t);
                    if (HResult.Failed(hr)) return hr;
                    t.SuppressLayered =
                        MilCommandDecoder.ReadFixed<S.MILCMD_HWNDTARGET_SUPPRESSLAYERED>(c).Suppress != 0;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdTargetUpdateWindowSettings:
                {
                    int hr = Require<MilTarget>(ch, c, out MilTarget t);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_TARGET_UPDATEWINDOWSETTINGS>(c);
                    t.WindowRect = s.WindowRect;
                    t.WindowLayerType = s.WindowLayerType;
                    t.TransparencyMode = s.TransparencyMode;
                    t.ConstantAlpha = s.ConstantAlpha;
                    t.IsChild = s.IsChild != 0;
                    t.IsRTL = s.IsRTL != 0;
                    t.RenderingEnabled = s.RenderingEnabled != 0;
                    t.ColorKey = s.ColorKey;
                    t.DisableCookie = s.DisableCookie;
                    t.GdiBlt = s.GdiBlt;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdGenericTargetCreate:
                {
                    int hr = Require<MilGenericTarget>(ch, c, out MilGenericTarget t);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_GENERICTARGET_CREATE>(c);
                    t.NativeWindow = new IntPtr((long)s.Hwnd);
                    t.RenderTargetPointer = s.PRenderTarget;
                    t.Width = s.Width;
                    t.Height = s.Height;
                    t.Dummy = s.Dummy;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdTargetSetRoot:
                {
                    int hr = Require<MilTarget>(ch, c, out MilTarget t);
                    if (HResult.Failed(hr)) return hr;
                    DUCE.ResourceHandle root =
                        MilCommandDecoder.ReadFixed<S.MILCMD_TARGET_SETROOT>(c).HRoot;
                    if (!root.IsNull && ch.GetVisual(root) == null) return HResult.E_INVALIDARG;
                    t.Root = root;
                    // 波46 · `D-G54` 判定点之二：**记下"这个目标的根属于本通道"**。
                    //   句柄的命名空间是**每通道**的，而 `DuplicateHandle` 会让多个通道的
                    //   资源表指向**同一个 `MilTarget` 实例**（⇒ `t.Root` 在两边都可见）。
                    //   所以"本目标有根"不等于"本通道有能力投影这棵树"；呈现侧要靠这一笔
                    //   判断能不能在**本通道**上投影（见 `MilChannel.MarkTargetRooted`、
                    //   `Interop/MilPresentation.PresentTarget` 的"只许在根句柄有效的通道上投影"）。
                    ch.MarkTargetRooted(t);
                    // 同步到契约 IMilChannel.Root，供渲染层取用
                    ch.SetRootFromHandle(root);
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdTargetSetClearColor:
                {
                    int hr = Require<MilTarget>(ch, c, out MilTarget t);
                    if (HResult.Failed(hr)) return hr;
                    t.ClearColor = MilCommandDecoder.ReadFixed<S.MILCMD_TARGET_SETCLEARCOLOR>(c).ClearColor;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdTargetInvalidate:
                {
                    int hr = Require<MilTarget>(ch, c, out MilTarget t);
                    if (HResult.Failed(hr)) return hr;
                    t.InvalidRect = MilCommandDecoder.ReadFixed<S.MILCMD_TARGET_INVALIDATE>(c).Rc;
                    t.InvalidateCount++;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdTargetSetFlags:
                {
                    int hr = Require<MilTarget>(ch, c, out MilTarget t);
                    if (HResult.Failed(hr)) return hr;
                    t.Flags = MilCommandDecoder.ReadFixed<S.MILCMD_TARGET_SETFLAGS>(c).Flags;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdHwndTargetDpiChanged:
                {
                    int hr = Require<MilHwndTarget>(ch, c, out MilHwndTarget t);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_HWNDTARGET_DPICHANGED>(c);
                    t.DpiX = s.DpiX;
                    t.DpiY = s.DpiY;
                    return HResult.S_OK;
                }

                // ---------------- 标量资源 ----------------
                case MilCmd.MilCmdDoubleResource:
                {
                    int hr = Require<MilDoubleResource>(ch, c, out MilDoubleResource r);
                    if (HResult.Failed(hr)) return hr;
                    r.Value = MilCommandDecoder.ReadFixed<S.MILCMD_DOUBLERESOURCE>(c).Value;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdColorResource:
                {
                    int hr = Require<MilColorResource>(ch, c, out MilColorResource r);
                    if (HResult.Failed(hr)) return hr;
                    r.Value = MilCommandDecoder.ReadFixed<S.MILCMD_COLORRESOURCE>(c).Value;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdPointResource:
                {
                    int hr = Require<MilPointResource>(ch, c, out MilPointResource r);
                    if (HResult.Failed(hr)) return hr;
                    r.Value = MilCommandDecoder.ReadFixed<S.MILCMD_POINTRESOURCE>(c).Value;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdRectResource:
                {
                    int hr = Require<MilRectResource>(ch, c, out MilRectResource r);
                    if (HResult.Failed(hr)) return hr;
                    r.Value = MilCommandDecoder.ReadFixed<S.MILCMD_RECTRESOURCE>(c).Value;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdSizeResource:
                {
                    int hr = Require<MilSizeResource>(ch, c, out MilSizeResource r);
                    if (HResult.Failed(hr)) return hr;
                    r.Value = MilCommandDecoder.ReadFixed<S.MILCMD_SIZERESOURCE>(c).Value;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdMatrixResource:
                {
                    int hr = Require<MilMatrixResource>(ch, c, out MilMatrixResource r);
                    if (HResult.Failed(hr)) return hr;
                    r.Value = MilCommandDecoder.ReadFixed<S.MILCMD_MATRIXRESOURCE>(c).Value;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdPoint3DResource:
                {
                    int hr = Require<MilPoint3DResource>(ch, c, out MilPoint3DResource r);
                    if (HResult.Failed(hr)) return hr;
                    r.Value = MilCommandDecoder.ReadFixed<S.MILCMD_POINT3DRESOURCE>(c).Value;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdVector3DResource:
                {
                    int hr = Require<MilVector3DResource>(ch, c, out MilVector3DResource r);
                    if (HResult.Failed(hr)) return hr;
                    r.Value = MilCommandDecoder.ReadFixed<S.MILCMD_VECTOR3DRESOURCE>(c).Value;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdQuaternionResource:
                {
                    int hr = Require<MilQuaternionResource>(ch, c, out MilQuaternionResource r);
                    if (HResult.Failed(hr)) return hr;
                    r.Value = MilCommandDecoder.ReadFixed<S.MILCMD_QUATERNIONRESOURCE>(c).Value;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdEtwEventResource:
                {
                    int hr = Require<MilEtwEventResource>(ch, c, out MilEtwEventResource r);
                    if (HResult.Failed(hr)) return hr;
                    r.Id = MilCommandDecoder.ReadFixed<S.MILCMD_ETWEVENTRESOURCE>(c).Id;
                    return HResult.S_OK;
                }

                // ---------------- RenderData（只解外层） ----------------
                case MilCmd.MilCmdRenderData:
                {
                    int hr = Require<MilRenderDataResource>(ch, c, out MilRenderDataResource r);
                    if (HResult.Failed(hr)) return hr;
                    uint cb = MilCommandDecoder.ReadFixed<S.MILCMD_RENDERDATA>(c).CbData;
                    hr = RequirePayload(c, 12, (int)cb);
                    if (HResult.Failed(hr)) return hr;
                    r.Data = c.Slice(12, (int)cb).ToArray();
                    // 内层指令流：只切记录框并尽力填 MilDrawInstruction，不绘图。
                    r.RenderData = RenderDataDecoder.Decode(r.Data);
                    return HResult.S_OK;
                }

                // ---------------- 变换 ----------------
                case MilCmd.MilCmdTranslateTransform:
                {
                    int hr = Require<MilTranslateTransform>(ch, c, out MilTranslateTransform t);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_TRANSLATETRANSFORM>(c);
                    t.X = s.X;
                    t.Y = s.Y;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdScaleTransform:
                {
                    int hr = Require<MilScaleTransform>(ch, c, out MilScaleTransform t);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_SCALETRANSFORM>(c);
                    t.ScaleX = s.ScaleX;
                    t.ScaleY = s.ScaleY;
                    t.CenterX = s.CenterX;
                    t.CenterY = s.CenterY;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdSkewTransform:
                {
                    int hr = Require<MilSkewTransform>(ch, c, out MilSkewTransform t);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_SKEWTRANSFORM>(c);
                    t.AngleX = s.AngleX;
                    t.AngleY = s.AngleY;
                    t.CenterX = s.CenterX;
                    t.CenterY = s.CenterY;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdRotateTransform:
                {
                    int hr = Require<MilRotateTransform>(ch, c, out MilRotateTransform t);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_ROTATETRANSFORM>(c);
                    t.Angle = s.Angle;
                    t.CenterX = s.CenterX;
                    t.CenterY = s.CenterY;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdMatrixTransform:
                {
                    int hr = Require<MilMatrixTransform>(ch, c, out MilMatrixTransform t);
                    if (HResult.Failed(hr)) return hr;
                    t.Matrix = MilCommandDecoder.ReadFixed<S.MILCMD_MATRIXTRANSFORM>(c).Matrix;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdTransformGroup:
                {
                    int hr = Require<MilTransformGroup>(ch, c, out MilTransformGroup t);
                    if (HResult.Failed(hr)) return hr;
                    uint size = MilCommandDecoder.ReadFixed<S.MILCMD_TRANSFORMGROUP>(c).ChildrenSize;
                    hr = RequirePayload(c, 12, (int)size);
                    if (HResult.Failed(hr)) return hr;
                    t.Children.Clear();
                    t.Children.AddRange(MilCommandDecoder.ReadHandles(c, 12, (int)size));
                    return HResult.S_OK;
                }

                // ---------------- 几何 ----------------
                case MilCmd.MilCmdLineGeometry:
                {
                    int hr = Require<MilLineGeometry>(ch, c, out MilLineGeometry g);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_LINEGEOMETRY>(c);
                    g.StartPoint = s.StartPoint;
                    g.EndPoint = s.EndPoint;
                    g.Transform = s.HTransform;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdRectangleGeometry:
                {
                    int hr = Require<MilRectangleGeometry>(ch, c, out MilRectangleGeometry g);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_RECTANGLEGEOMETRY>(c);
                    g.RadiusX = s.RadiusX;
                    g.RadiusY = s.RadiusY;
                    g.Rect = s.Rect;
                    g.Transform = s.HTransform;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdEllipseGeometry:
                {
                    int hr = Require<MilEllipseGeometry>(ch, c, out MilEllipseGeometry g);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_ELLIPSEGEOMETRY>(c);
                    g.RadiusX = s.RadiusX;
                    g.RadiusY = s.RadiusY;
                    g.Center = s.Center;
                    g.Transform = s.HTransform;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdGeometryGroup:
                {
                    int hr = Require<MilGeometryGroup>(ch, c, out MilGeometryGroup g);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_GEOMETRYGROUP>(c);
                    hr = RequirePayload(c, 20, (int)s.ChildrenSize);
                    if (HResult.Failed(hr)) return hr;
                    g.FillRule = s.FillRule;
                    g.Transform = s.HTransform;
                    g.Children.Clear();
                    g.Children.AddRange(MilCommandDecoder.ReadHandles(c, 20, (int)s.ChildrenSize));
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdCombinedGeometry:
                {
                    int hr = Require<MilCombinedGeometry>(ch, c, out MilCombinedGeometry g);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_COMBINEDGEOMETRY>(c);
                    g.GeometryCombineMode = s.GeometryCombineMode;
                    g.Transform = s.HTransform;
                    g.Geometry1 = s.HGeometry1;
                    g.Geometry2 = s.HGeometry2;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdPathGeometry:
                {
                    int hr = Require<MilPathGeometry>(ch, c, out MilPathGeometry g);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_PATHGEOMETRY>(c);
                    hr = RequirePayload(c, 20, (int)s.FiguresSize);
                    if (HResult.Failed(hr)) return hr;
                    g.FillRule = s.FillRule;
                    g.Transform = s.HTransform;
                    // 序列化几何体（MIL_PATHGEOMETRY + MIL_PATHFIGURE + 段序列）原样保存，
                    // 由渲染层解释——本层不猜段布局。
                    g.SerializedData = c.Slice(20, (int)s.FiguresSize).ToArray();
                    return HResult.S_OK;
                }

                // ---------------- 画刷 ----------------
                case MilCmd.MilCmdImplicitInputBrush:
                {
                    int hr = Require<MilImplicitInputBrush>(ch, c, out MilImplicitInputBrush b);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_IMPLICITINPUTBRUSH>(c);
                    b.Opacity = s.Opacity;
                    b.Transform = s.HTransform;
                    b.RelativeTransform = s.HRelativeTransform;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdSolidColorBrush:
                {
                    int hr = Require<MilSolidColorBrush>(ch, c, out MilSolidColorBrush b);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_SOLIDCOLORBRUSH>(c);
                    b.Opacity = s.Opacity;
                    b.Color = s.Color;
                    b.Transform = s.HTransform;
                    b.RelativeTransform = s.HRelativeTransform;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdLinearGradientBrush:
                {
                    int hr = Require<MilLinearGradientBrush>(ch, c, out MilLinearGradientBrush b);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_LINEARGRADIENTBRUSH>(c);
                    hr = RequirePayload(c, 84, (int)s.GradientStopsSize);
                    if (HResult.Failed(hr)) return hr;
                    b.Opacity = s.Opacity;
                    b.StartPoint = s.StartPoint;
                    b.EndPoint = s.EndPoint;
                    b.ColorInterpolationMode = s.ColorInterpolationMode;
                    b.MappingMode = s.MappingMode;
                    b.SpreadMethod = s.SpreadMethod;
                    b.Transform = s.HTransform;
                    b.RelativeTransform = s.HRelativeTransform;
                    b.GradientStops.Clear();
                    b.GradientStops.AddRange(
                        MilCommandDecoder.ReadGradientStops(c, 84, (int)s.GradientStopsSize));
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdRadialGradientBrush:
                {
                    int hr = Require<MilRadialGradientBrush>(ch, c, out MilRadialGradientBrush b);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_RADIALGRADIENTBRUSH>(c);
                    hr = RequirePayload(c, 108, (int)s.GradientStopsSize);
                    if (HResult.Failed(hr)) return hr;
                    b.Opacity = s.Opacity;
                    b.Center = s.Center;
                    b.GradientOrigin = s.GradientOrigin;
                    b.RadiusX = s.RadiusX;
                    b.RadiusY = s.RadiusY;
                    b.ColorInterpolationMode = s.ColorInterpolationMode;
                    b.MappingMode = s.MappingMode;
                    b.SpreadMethod = s.SpreadMethod;
                    b.Transform = s.HTransform;
                    b.RelativeTransform = s.HRelativeTransform;
                    b.GradientStops.Clear();
                    b.GradientStops.AddRange(
                        MilCommandDecoder.ReadGradientStops(c, 108, (int)s.GradientStopsSize));
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdImageBrush:
                {
                    int hr = Require<MilImageBrush>(ch, c, out MilImageBrush b);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_IMAGEBRUSH>(c);
                    SetTileBrush(b, s.Opacity, s.Viewport, s.Viewbox,
                        s.CacheInvalidationThresholdMinimum, s.CacheInvalidationThresholdMaximum,
                        s.ViewportUnits, s.ViewboxUnits, s.Stretch, s.TileMode,
                        s.AlignmentX, s.AlignmentY, s.CachingHint, s.HTransform, s.HRelativeTransform);
                    b.ImageSource = s.HImageSource;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdDrawingBrush:
                {
                    int hr = Require<MilDrawingBrush>(ch, c, out MilDrawingBrush b);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_DRAWINGBRUSH>(c);
                    SetTileBrush(b, s.Opacity, s.Viewport, s.Viewbox,
                        s.CacheInvalidationThresholdMinimum, s.CacheInvalidationThresholdMaximum,
                        s.ViewportUnits, s.ViewboxUnits, s.Stretch, s.TileMode,
                        s.AlignmentX, s.AlignmentY, s.CachingHint, s.HTransform, s.HRelativeTransform);
                    b.Drawing = s.HDrawing;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdVisualBrush:
                {
                    int hr = Require<MilVisualBrush>(ch, c, out MilVisualBrush b);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_VISUALBRUSH>(c);
                    SetTileBrush(b, s.Opacity, s.Viewport, s.Viewbox,
                        s.CacheInvalidationThresholdMinimum, s.CacheInvalidationThresholdMaximum,
                        s.ViewportUnits, s.ViewboxUnits, s.Stretch, s.TileMode,
                        s.AlignmentX, s.AlignmentY, s.CachingHint, s.HTransform, s.HRelativeTransform);
                    b.Visual = s.HVisual;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdBitmapCacheBrush:
                {
                    int hr = Require<MilBitmapCacheBrush>(ch, c, out MilBitmapCacheBrush b);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_BITMAPCACHEBRUSH>(c);
                    b.Opacity = s.Opacity;
                    b.Transform = s.HTransform;
                    b.RelativeTransform = s.HRelativeTransform;
                    b.BitmapCache = s.HBitmapCache;
                    b.InternalTarget = s.HInternalTarget;
                    return HResult.S_OK;
                }

                // ---------------- 效果 ----------------
                case MilCmd.MilCmdBlurEffect:
                {
                    int hr = Require<MilBlurEffect>(ch, c, out MilBlurEffect e);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_BLUREFFECT>(c);
                    e.Radius = s.Radius;
                    e.KernelType = s.KernelType;
                    e.RenderingBias = s.RenderingBias;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdDropShadowEffect:
                {
                    int hr = Require<MilDropShadowEffect>(ch, c, out MilDropShadowEffect e);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_DROPSHADOWEFFECT>(c);
                    e.ShadowDepth = s.ShadowDepth;
                    e.Color = s.Color;
                    e.Direction = s.Direction;
                    e.Opacity = s.Opacity;
                    e.BlurRadius = s.BlurRadius;
                    e.RenderingBias = s.RenderingBias;
                    return HResult.S_OK;
                }

                // ---------------- 效果：HLSL / ShaderEffect 通道（0x6c / 0x70）----------------
                // 【波62 · D-G58】这两条原先在 `MilCommandLayout.s_notImpl` 里 ⇒ `Dispatch` 入口的
                //   `IsNotImplemented` 短路直接返回 E_NOTIMPL ⇒ 上游 `HRESULT.Check(Channel.Commit())`
                //   （`exports.cs:376` ← `MediaContext.cs:2151`）抛出 ⇒ 「工具」页 #2 `Effects`
                //   **一打开就未处理异常**（真应用 hc demo：栈顶 `MediaContext.CommitChannel`）。
                //
                // 【本实现只做"接受"，**不做"渲染"**】——这句话必须被读准：
                //   · 0x6c PixelShader：读出声明的字节码长度，把**整块**当作"已接受的资源"
                //     （句柄本来就在资源表里，是 MilOpaqueResource），**不编译、不执行**；
                //   · 0x70 ShaderEffect：按**恒等效果**——渲染侧无论走 `MilPushEffect`(0x55) 还是
                //     `VisualSetEffect`(0x10) 都查不出可用的 SKImageFilter ⇒ 内容**照原样画**、
                //     效果**不施加**（`SkiaEffect.TryResolve` 的 `supported=false` 分支 ＋
                //     `SkiaRenderBackend` 的裸 `Save` 顶位），且该绘制指令被记 `NotDrawn`。
                //   ⇒ 观感 = "**没有效果**"，**不是** "效果生效"。将来真要接效果，路径是
                //     Shader → Skia `SKRuntimeEffect`（docs/unimplemented.md §0 的同一句话）。
                //
                // 【不许静默 no-op（纪律 47 族）】两条都走 `ch.NoteShaderStubAccepted(...)`：
                //   具名（命令 id ＋ 句柄 ＋ 字节数）、有界（每 id 每进程限 N 行）的只读台账。
                case MilCmd.MilCmdPixelShader:
                {
                    // 固定头 20；尾部长度 = 通道自己的长度账（= 上游 AppendCommandData 的字节数）。
                    int payload = c.Length - MilCommandLayout.FixedSize(cmd);
                    ch.NoteShaderStubAccepted(cmd, MilCommandDecoder.ReadHandle(c), payload,
                                              ReadDeclaredUInt32(c, 12));   // PixelShaderBytecodeSize
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdShaderEffect:
                {
                    // 固定头 80；尾部 = 头里 8 个 *Size 之和（偏移 48/52/56/60/64/68/72/76）。
                    int payload = c.Length - MilCommandLayout.FixedSize(cmd);
                    int declared = 0;
                    for (int o = 48; o <= 76; o += 4)
                    {
                        int v = ReadDeclaredUInt32(c, o);
                        if (v < 0) { declared = -1; break; }   // 头被截断 ⇒ 记 -1（不可读），**不抛**
                        declared += v;
                    }
                    ch.NoteShaderStubAccepted(cmd, MilCommandDecoder.ReadHandle(c), payload, declared);
                    return HResult.S_OK;
                }

                // ---------------- Pen / DashStyle ----------------
                case MilCmd.MilCmdDashStyle:
                {
                    int hr = Require<MilDashStyle>(ch, c, out MilDashStyle d);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_DASHSTYLE>(c);
                    hr = RequirePayload(c, 24, (int)s.DashesSize);
                    if (HResult.Failed(hr)) return hr;
                    d.Offset = s.Offset;
                    d.Dashes.Clear();
                    d.Dashes.AddRange(MilCommandDecoder.ReadDoubles(c, 24, (int)s.DashesSize));
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdPen:
                {
                    int hr = Require<MilPen>(ch, c, out MilPen p);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_PEN>(c);
                    p.Thickness = s.Thickness;
                    p.MiterLimit = s.MiterLimit;
                    p.Brush = s.HBrush;
                    p.DashStyle = s.HDashStyle;
                    p.StartLineCap = s.StartLineCap;
                    p.EndLineCap = s.EndLineCap;
                    p.DashCap = s.DashCap;
                    p.LineJoin = s.LineJoin;
                    return HResult.S_OK;
                }

                // ---------------- Drawing ----------------
                case MilCmd.MilCmdDrawingImage:
                {
                    int hr = Require<MilDrawingImage>(ch, c, out MilDrawingImage d);
                    if (HResult.Failed(hr)) return hr;
                    d.Drawing = MilCommandDecoder.ReadFixed<S.MILCMD_DRAWINGIMAGE>(c).HDrawing;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdGeometryDrawing:
                {
                    int hr = Require<MilGeometryDrawing>(ch, c, out MilGeometryDrawing d);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_GEOMETRYDRAWING>(c);
                    d.Brush = s.HBrush;
                    d.Pen = s.HPen;
                    d.Geometry = s.HGeometry;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdGlyphRunDrawing:
                {
                    int hr = Require<MilGlyphRunDrawing>(ch, c, out MilGlyphRunDrawing d);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_GLYPHRUNDRAWING>(c);
                    d.GlyphRun = s.HGlyphRun;
                    d.ForegroundBrush = s.HForegroundBrush;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdImageDrawing:
                {
                    int hr = Require<MilImageDrawing>(ch, c, out MilImageDrawing d);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_IMAGEDRAWING>(c);
                    d.Rect = s.Rect;
                    d.ImageSource = s.HImageSource;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdVideoDrawing:
                {
                    int hr = Require<MilVideoDrawing>(ch, c, out MilVideoDrawing d);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_VIDEODRAWING>(c);
                    d.Rect = s.Rect;
                    d.Player = s.HPlayer;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdDrawingGroup:
                {
                    int hr = Require<MilDrawingGroup>(ch, c, out MilDrawingGroup d);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_DRAWINGGROUP>(c);
                    hr = RequirePayload(c, 52, (int)s.ChildrenSize);
                    if (HResult.Failed(hr)) return hr;
                    d.Opacity = s.Opacity;
                    d.ClipGeometry = s.HClipGeometry;
                    d.OpacityMask = s.HOpacityMask;
                    d.Transform = s.HTransform;
                    d.GuidelineSet = s.HGuidelineSet;
                    d.EdgeMode = s.EdgeMode;
                    d.BitmapScalingMode = s.BitmapScalingMode;
                    d.ClearTypeHint = s.ClearTypeHint;
                    d.Children.Clear();
                    d.Children.AddRange(MilCommandDecoder.ReadHandles(c, 52, (int)s.ChildrenSize));
                    return HResult.S_OK;
                }

                // ---------------- 其它 ----------------
                case MilCmd.MilCmdGuidelineSet:
                {
                    int hr = Require<MilGuidelineSet>(ch, c, out MilGuidelineSet g);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_GUIDELINESET>(c);
                    hr = RequirePayload(c, 20, (int)(s.GuidelinesXSize + s.GuidelinesYSize));
                    if (HResult.Failed(hr)) return hr;
                    g.IsDynamic = s.IsDynamic != 0;
                    g.GuidelinesX.Clear();
                    g.GuidelinesX.AddRange(MilCommandDecoder.ReadDoubles(c, 20, (int)s.GuidelinesXSize));
                    g.GuidelinesY.Clear();
                    g.GuidelinesY.AddRange(
                        MilCommandDecoder.ReadDoubles(c, 20 + (int)s.GuidelinesXSize, (int)s.GuidelinesYSize));
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdBitmapCache:
                {
                    int hr = Require<MilBitmapCache>(ch, c, out MilBitmapCache b);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_BITMAPCACHE>(c);
                    b.RenderAtScale = s.RenderAtScale;
                    b.SnapsToDevicePixels = s.SnapsToDevicePixels != 0;
                    b.EnableClearType = s.EnableClearType != 0;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdGlyphRunCreate:
                {
                    int hr = Require<MilGlyphRun>(ch, c, out MilGlyphRun g);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_GLYPHRUN_CREATE>(c);
                    int bytes = s.GlyphCount * 2;
                    hr = RequirePayload(c, 76, bytes);
                    if (HResult.Failed(hr)) return hr;
                    g.Flags = s.GlyphRunFlags;
                    g.Origin = s.Origin;
                    g.MuSize = s.MuSize;
                    g.ManagedBounds = s.ManagedBounds;
                    g.BidiLevel = s.BidiLevel;
                    g.DWriteTextMeasuringMethod = s.DWriteTextMeasuringMethod;
                    g.PIDWriteFont = s.PIDWriteFont;   // T2b：搬线格 [FieldOffset(8)] 的字面标识（纯搬运，无逻辑）
                    g.GlyphIndices = MilCommandDecoder.ReadUInt16Array(c, 76, s.GlyphCount);
                    // 偏移/步进数组需要字体度量，M1 留空（见 docs/unimplemented.md）
                    return HResult.S_OK;
                }

                // ---------------- 3D 视觉树 (0x29–0x30) ----------------
                //
                // 载荷全是 POD（ResourceHandle / MilRect / UInt32），与下面 0x57–0x6b
                // 那 21 条同构，因此按同一把尺子判成 A 类。上一轮曾把它们判成 B
                // （"需要 Resources/ 的节点模型"）——但 0x57–0x6b 面对的是同一个处境，
                // 既然那边用弱键附加表解掉了，这边就没有理由区别对待。
                //
                // 状态挂在 MilResource3DTable 的 MilViewport3DVisual / MilVisual3D 上。
                // Visual3D 的子节点列表是真的在增删，不是记账式空操作。
                // 依旧不改 Resources/ 一行。

                case MilCmd.MilCmdViewport3DVisualSetCamera:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_VIEWPORT3DVISUAL, out MilResource owner);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_VIEWPORT3DVISUAL_SETCAMERA>(c);
                    MilResource3DTable.GetOrAttach<MilViewport3DVisual>(owner, cmd).HCamera = s.HCamera;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdViewport3DVisualSetViewport:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_VIEWPORT3DVISUAL, out MilResource owner);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_VIEWPORT3DVISUAL_SETVIEWPORT>(c);
                    MilResource3DTable.GetOrAttach<MilViewport3DVisual>(owner, cmd).Viewport = s.Viewport;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdViewport3DVisualSet3DChild:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_VIEWPORT3DVISUAL, out MilResource owner);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_VIEWPORT3DVISUAL_SET3DCHILD>(c);
                    MilResource3DTable.GetOrAttach<MilViewport3DVisual>(owner, cmd).HChild = s.HChild;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdVisual3DSetContent:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_VISUAL3D, out MilResource owner);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_VISUAL3D_SETCONTENT>(c);
                    MilResource3DTable.GetOrAttach<MilVisual3D>(owner, cmd).HContent = s.HContent;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdVisual3DSetTransform:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_VISUAL3D, out MilResource owner);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_VISUAL3D_SETTRANSFORM>(c);
                    MilResource3DTable.GetOrAttach<MilVisual3D>(owner, cmd).HTransform = s.HTransform;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdVisual3DRemoveAllChildren:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_VISUAL3D, out MilResource owner);
                    if (HResult.Failed(hr)) return hr;
                    MilResource3DTable.GetOrAttach<MilVisual3D>(owner, cmd).Children.Clear();
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdVisual3DRemoveChild:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_VISUAL3D, out MilResource owner);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_VISUAL3D_REMOVECHILD>(c);
                    MilResource3DTable.GetOrAttach<MilVisual3D>(owner, cmd).Children.Remove(s.HChild);
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdVisual3DInsertChildAt:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_VISUAL3D, out MilResource owner);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_VISUAL3D_INSERTCHILDAT>(c);
                    var r = MilResource3DTable.GetOrAttach<MilVisual3D>(owner, cmd);
                    // 与 2D 的 MilCmdVisualInsertChildAt 同一套约定：下标越界钳到末尾，不报错。
                    int index = (int)Math.Min(s.Index, (uint)r.Children.Count);
                    r.Children.Insert(index, s.HChild);
                    return HResult.S_OK;
                }

                // ---------------- 3D 资源 (0x57–0x6b) ----------------
                //
                // 这 21 条是纯 POD 属性写入：字段只有 double / MilPoint3F /
                // MilQuaternionF / MilColorF / ResourceHandle / MilMatrix4x4F，
                // 没有 COM、没有 HANDLE、没有 HLSL。分类依据见 docs/unimplemented.md §0。
                //
                // 状态落在 MilResource3DTable（以资源实例为弱键的附加表）。
                // **不做 3D 光栅化**——M1 没有 3D 后端，这里的数据尚无消费者。

                case MilCmd.MilCmdAxisAngleRotation3D:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_AXISANGLEROTATION3D, out MilResource owner);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_AXISANGLEROTATION3D>(c);
                    var r = MilResource3DTable.GetOrAttach<MilAxisAngleRotation3D>(owner, cmd);
                    r.Angle = s.Angle;
                    r.Axis = s.Axis;
                    r.HAxisAnimations = s.HAxisAnimations;
                    r.HAngleAnimations = s.HAngleAnimations;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdQuaternionRotation3D:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_QUATERNIONROTATION3D, out MilResource owner);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_QUATERNIONROTATION3D>(c);
                    var r = MilResource3DTable.GetOrAttach<MilQuaternionRotation3D>(owner, cmd);
                    r.Quaternion = s.Quaternion;
                    r.HQuaternionAnimations = s.HQuaternionAnimations;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdPerspectiveCamera:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_PERSPECTIVECAMERA, out MilResource owner);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_PERSPECTIVECAMERA>(c);
                    var r = MilResource3DTable.GetOrAttach<MilPerspectiveCamera>(owner, cmd);
                    r.NearPlaneDistance = s.NearPlaneDistance;
                    r.FarPlaneDistance = s.FarPlaneDistance;
                    r.FieldOfView = s.FieldOfView;
                    r.Position = s.Position;
                    r.HTransform = s.HTransform;
                    r.LookDirection = s.LookDirection;
                    r.UpDirection = s.UpDirection;
                    r.HNearPlaneDistanceAnimations = s.HNearPlaneDistanceAnimations;
                    r.HFarPlaneDistanceAnimations = s.HFarPlaneDistanceAnimations;
                    r.HPositionAnimations = s.HPositionAnimations;
                    r.HLookDirectionAnimations = s.HLookDirectionAnimations;
                    r.HUpDirectionAnimations = s.HUpDirectionAnimations;
                    r.HFieldOfViewAnimations = s.HFieldOfViewAnimations;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdOrthographicCamera:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_ORTHOGRAPHICCAMERA, out MilResource owner);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_ORTHOGRAPHICCAMERA>(c);
                    var r = MilResource3DTable.GetOrAttach<MilOrthographicCamera>(owner, cmd);
                    r.NearPlaneDistance = s.NearPlaneDistance;
                    r.FarPlaneDistance = s.FarPlaneDistance;
                    r.Width = s.Width;
                    r.Position = s.Position;
                    r.HTransform = s.HTransform;
                    r.LookDirection = s.LookDirection;
                    r.UpDirection = s.UpDirection;
                    r.HNearPlaneDistanceAnimations = s.HNearPlaneDistanceAnimations;
                    r.HFarPlaneDistanceAnimations = s.HFarPlaneDistanceAnimations;
                    r.HPositionAnimations = s.HPositionAnimations;
                    r.HLookDirectionAnimations = s.HLookDirectionAnimations;
                    r.HUpDirectionAnimations = s.HUpDirectionAnimations;
                    r.HWidthAnimations = s.HWidthAnimations;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdMatrixCamera:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_MATRIXCAMERA, out MilResource owner);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_MATRIXCAMERA>(c);
                    var r = MilResource3DTable.GetOrAttach<MilMatrixCamera>(owner, cmd);
                    r.ViewMatrix = s.ViewMatrix;
                    r.ProjectionMatrix = s.ProjectionMatrix;
                    r.HTransform = s.HTransform;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdModel3DGroup:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_MODEL3DGROUP, out MilResource owner);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_MODEL3DGROUP>(c);
                    hr = RequirePayload(c, 16, (int)s.ChildrenSize);
                    if (HResult.Failed(hr)) return hr;
                    if (s.ChildrenSize % 4 != 0) return HResult.E_INVALIDARG;
                    var r = MilResource3DTable.GetOrAttach<MilModel3DGroup>(owner, cmd);
                    r.HTransform = s.HTransform;
                    r.Children.Clear();
                    r.Children.AddRange(MilCommandDecoder.ReadHandles(c, 16, (int)s.ChildrenSize));
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdAmbientLight:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_AMBIENTLIGHT, out MilResource owner);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_AMBIENTLIGHT>(c);
                    var r = MilResource3DTable.GetOrAttach<MilAmbientLight>(owner, cmd);
                    r.Color = s.Color;
                    r.HTransform = s.HTransform;
                    r.HColorAnimations = s.HColorAnimations;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdDirectionalLight:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_DIRECTIONALLIGHT, out MilResource owner);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_DIRECTIONALLIGHT>(c);
                    var r = MilResource3DTable.GetOrAttach<MilDirectionalLight>(owner, cmd);
                    r.Color = s.Color;
                    r.Direction = s.Direction;
                    r.HTransform = s.HTransform;
                    r.HColorAnimations = s.HColorAnimations;
                    r.HDirectionAnimations = s.HDirectionAnimations;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdPointLight:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_POINTLIGHT, out MilResource owner);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_POINTLIGHT>(c);
                    var r = MilResource3DTable.GetOrAttach<MilPointLight>(owner, cmd);
                    r.Color = s.Color;
                    r.Range = s.Range;
                    r.ConstantAttenuation = s.ConstantAttenuation;
                    r.LinearAttenuation = s.LinearAttenuation;
                    r.QuadraticAttenuation = s.QuadraticAttenuation;
                    r.Position = s.Position;
                    r.HTransform = s.HTransform;
                    r.HColorAnimations = s.HColorAnimations;
                    r.HPositionAnimations = s.HPositionAnimations;
                    r.HRangeAnimations = s.HRangeAnimations;
                    r.HConstantAttenuationAnimations = s.HConstantAttenuationAnimations;
                    r.HLinearAttenuationAnimations = s.HLinearAttenuationAnimations;
                    r.HQuadraticAttenuationAnimations = s.HQuadraticAttenuationAnimations;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdSpotLight:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_SPOTLIGHT, out MilResource owner);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_SPOTLIGHT>(c);
                    var r = MilResource3DTable.GetOrAttach<MilSpotLight>(owner, cmd);
                    r.Color = s.Color;
                    r.Range = s.Range;
                    r.ConstantAttenuation = s.ConstantAttenuation;
                    r.LinearAttenuation = s.LinearAttenuation;
                    r.QuadraticAttenuation = s.QuadraticAttenuation;
                    r.OuterConeAngle = s.OuterConeAngle;
                    r.InnerConeAngle = s.InnerConeAngle;
                    r.Position = s.Position;
                    r.Direction = s.Direction;
                    r.HTransform = s.HTransform;
                    r.HColorAnimations = s.HColorAnimations;
                    r.HPositionAnimations = s.HPositionAnimations;
                    r.HRangeAnimations = s.HRangeAnimations;
                    r.HConstantAttenuationAnimations = s.HConstantAttenuationAnimations;
                    r.HLinearAttenuationAnimations = s.HLinearAttenuationAnimations;
                    r.HQuadraticAttenuationAnimations = s.HQuadraticAttenuationAnimations;
                    r.HDirectionAnimations = s.HDirectionAnimations;
                    r.HOuterConeAngleAnimations = s.HOuterConeAngleAnimations;
                    r.HInnerConeAngleAnimations = s.HInnerConeAngleAnimations;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdGeometryModel3D:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_GEOMETRYMODEL3D, out MilResource owner);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_GEOMETRYMODEL3D>(c);
                    var r = MilResource3DTable.GetOrAttach<MilGeometryModel3D>(owner, cmd);
                    r.HTransform = s.HTransform;
                    r.HGeometry = s.HGeometry;
                    r.HMaterial = s.HMaterial;
                    r.HBackMaterial = s.HBackMaterial;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdMeshGeometry3D:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_MESHGEOMETRY3D, out MilResource owner);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_MESHGEOMETRY3D>(c);

                    // 四段的元素宽度来自 Media3D/Generated/MeshGeometry3D.cs:230-283：
                    //   12 / 12 / 16 / 4。不是 4 的倍数说明流坏了，宁可报错也不静默截断。
                    if (s.PositionsSize % 12 != 0) return HResult.E_INVALIDARG;
                    if (s.NormalsSize % 12 != 0) return HResult.E_INVALIDARG;
                    if (s.TextureCoordinatesSize % 16 != 0) return HResult.E_INVALIDARG;
                    if (s.TriangleIndicesSize % 4 != 0) return HResult.E_INVALIDARG;

                    int pSize = (int)s.PositionsSize, nSize = (int)s.NormalsSize;
                    int tSize = (int)s.TextureCoordinatesSize, iSize = (int)s.TriangleIndicesSize;

                    // 用 long 累加：四段各接近 int.MaxValue 时 int 会溢出成负数绕过校验。
                    long total = (long)pSize + nSize + tSize + iSize;
                    if (total < 0 || total > c.Length - 24) return HResult.E_INVALIDARG;

                    int oP = 24;
                    int oN = oP + pSize;
                    int oT = oN + nSize;
                    int oI = oT + tSize;

                    var r = MilResource3DTable.GetOrAttach<MilMeshGeometry3D>(owner, cmd);
                    r.Positions.Clear();
                    r.Positions.AddRange(MilCommandDecoder.ReadPoint3Fs(c, oP, pSize));
                    r.Normals.Clear();
                    r.Normals.AddRange(MilCommandDecoder.ReadPoint3Fs(c, oN, nSize));
                    r.TextureCoordinates.Clear();
                    r.TextureCoordinates.AddRange(MilCommandDecoder.ReadPoints(c, oT, tSize));
                    r.TriangleIndices.Clear();
                    r.TriangleIndices.AddRange(MilCommandDecoder.ReadInt32s(c, oI, iSize));
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdMaterialGroup:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_MATERIALGROUP, out MilResource owner);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_MATERIALGROUP>(c);
                    hr = RequirePayload(c, 12, (int)s.ChildrenSize);
                    if (HResult.Failed(hr)) return hr;
                    if (s.ChildrenSize % 4 != 0) return HResult.E_INVALIDARG;
                    var r = MilResource3DTable.GetOrAttach<MilMaterialGroup>(owner, cmd);
                    r.Children.Clear();
                    r.Children.AddRange(MilCommandDecoder.ReadHandles(c, 12, (int)s.ChildrenSize));
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdDiffuseMaterial:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_DIFFUSEMATERIAL, out MilResource owner);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_DIFFUSEMATERIAL>(c);
                    var r = MilResource3DTable.GetOrAttach<MilDiffuseMaterial>(owner, cmd);
                    r.Color = s.Color;
                    r.AmbientColor = s.AmbientColor;
                    r.HBrush = s.HBrush;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdSpecularMaterial:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_SPECULARMATERIAL, out MilResource owner);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_SPECULARMATERIAL>(c);
                    var r = MilResource3DTable.GetOrAttach<MilSpecularMaterial>(owner, cmd);
                    r.Color = s.Color;
                    r.SpecularPower = s.SpecularPower;
                    r.HBrush = s.HBrush;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdEmissiveMaterial:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_EMISSIVEMATERIAL, out MilResource owner);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_EMISSIVEMATERIAL>(c);
                    var r = MilResource3DTable.GetOrAttach<MilEmissiveMaterial>(owner, cmd);
                    r.Color = s.Color;
                    r.HBrush = s.HBrush;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdTransform3DGroup:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_TRANSFORM3DGROUP, out MilResource owner);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_TRANSFORM3DGROUP>(c);
                    hr = RequirePayload(c, 12, (int)s.ChildrenSize);
                    if (HResult.Failed(hr)) return hr;
                    if (s.ChildrenSize % 4 != 0) return HResult.E_INVALIDARG;
                    var r = MilResource3DTable.GetOrAttach<MilTransform3DGroup>(owner, cmd);
                    r.Children.Clear();
                    r.Children.AddRange(MilCommandDecoder.ReadHandles(c, 12, (int)s.ChildrenSize));
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdTranslateTransform3D:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_TRANSLATETRANSFORM3D, out MilResource owner);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_TRANSLATETRANSFORM3D>(c);
                    var r = MilResource3DTable.GetOrAttach<MilTranslateTransform3D>(owner, cmd);
                    r.OffsetX = s.OffsetX;
                    r.OffsetY = s.OffsetY;
                    r.OffsetZ = s.OffsetZ;
                    r.HOffsetXAnimations = s.HOffsetXAnimations;
                    r.HOffsetYAnimations = s.HOffsetYAnimations;
                    r.HOffsetZAnimations = s.HOffsetZAnimations;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdScaleTransform3D:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_SCALETRANSFORM3D, out MilResource owner);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_SCALETRANSFORM3D>(c);
                    var r = MilResource3DTable.GetOrAttach<MilScaleTransform3D>(owner, cmd);
                    r.ScaleX = s.ScaleX;
                    r.ScaleY = s.ScaleY;
                    r.ScaleZ = s.ScaleZ;
                    r.CenterX = s.CenterX;
                    r.CenterY = s.CenterY;
                    r.CenterZ = s.CenterZ;
                    r.HScaleXAnimations = s.HScaleXAnimations;
                    r.HScaleYAnimations = s.HScaleYAnimations;
                    r.HScaleZAnimations = s.HScaleZAnimations;
                    r.HCenterXAnimations = s.HCenterXAnimations;
                    r.HCenterYAnimations = s.HCenterYAnimations;
                    r.HCenterZAnimations = s.HCenterZAnimations;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdRotateTransform3D:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_ROTATETRANSFORM3D, out MilResource owner);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_ROTATETRANSFORM3D>(c);
                    var r = MilResource3DTable.GetOrAttach<MilRotateTransform3D>(owner, cmd);
                    r.CenterX = s.CenterX;
                    r.CenterY = s.CenterY;
                    r.CenterZ = s.CenterZ;
                    r.HCenterXAnimations = s.HCenterXAnimations;
                    r.HCenterYAnimations = s.HCenterYAnimations;
                    r.HCenterZAnimations = s.HCenterZAnimations;
                    r.HRotation = s.HRotation;
                    return HResult.S_OK;
                }

                case MilCmd.MilCmdMatrixTransform3D:
                {
                    int hr = Require3D(ch, c, DUCE.ResourceType.TYPE_MATRIXTRANSFORM3D, out MilResource owner);
                    if (HResult.Failed(hr)) return hr;
                    var s = MilCommandDecoder.ReadFixed<S.MILCMD_MATRIXTRANSFORM3D>(c);
                    var r = MilResource3DTable.GetOrAttach<MilMatrixTransform3D>(owner, cmd);
                    r.Matrix = s.Matrix;
                    return HResult.S_OK;
                }

                default:
                    return HResult.E_NOTIMPL;
            }
        }

        // ==================================================================
        //  辅助
        // ==================================================================

        /// <summary>
        /// 读命令头里偏移 <paramref name="offset"/> 处的 UInt32（0x6c/0x70 的 *Size 字段全在固定头内）。
        /// 越界返回 -1 ⇒ 调用方按"声明长度不可读"记，**绝不抛**：
        /// 一条只读诊断不允许把应用打崩（宁可少一个数字，不可多一次 abort）。
        /// </summary>
        private static int ReadDeclaredUInt32(ReadOnlySpan<byte> c, int offset) =>
            offset + 4 <= c.Length
                ? (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(c.Slice(offset, 4))
                : -1;

        /// <summary>
        /// 取命令的目标资源：句柄为空 → E_INVALIDARG；不在表内 → E_HANDLE；
        /// 类型不符 → E_INVALIDARG。
        /// </summary>
        private static int Require<T>(MilChannel ch, ReadOnlySpan<byte> c, out T resource)
            where T : MilResource
        {
            resource = null;
            DUCE.ResourceHandle h = MilCommandDecoder.ReadHandle(c);
            if (h.IsNull) return HResult.E_INVALIDARG;

            MilResource r = ch.Resources.Lookup(h);
            if (r == null) return HResult.E_HANDLE;
            if (r is not T typed) return HResult.E_INVALIDARG;

            resource = typed;
            return HResult.S_OK;
        }

        /// <summary>
        /// 3D 资源（0x57–0x6b）与 3D 视觉树（0x29–0x30）的目标资源解析。
        ///
        /// 与 Require&lt;T&gt; 的差别：资源表里 3D 类型都是占位 MilOpaqueResource，
        /// 没有各自的子类，所以这里按 DUCE.ResourceType 判定而不是按 CLR 类型。
        /// 返回值是"宿主资源"，解码结果再经 MilResource3DTable 挂到它上面。
        /// </summary>
        private static int Require3D(MilChannel ch, ReadOnlySpan<byte> c, DUCE.ResourceType type,
                                     out MilResource resource)
        {
            resource = null;
            DUCE.ResourceHandle h = MilCommandDecoder.ReadHandle(c);
            if (h.IsNull) return HResult.E_INVALIDARG;

            MilResource r = ch.Resources.Lookup(h);
            if (r == null) return HResult.E_HANDLE;
            if (r.Type != type) return HResult.E_INVALIDARG;

            resource = r;
            return HResult.S_OK;
        }

        /// <summary>变长载荷越界保护：offset + 长度不得超出命令。</summary>
        private static int RequirePayload(ReadOnlySpan<byte> c, int offset, int byteCount)
        {
            if (byteCount < 0) return HResult.E_INVALIDARG;
            if (offset + byteCount > c.Length) return HResult.E_INVALIDARG;
            return HResult.S_OK;
        }

        private static void SetTileBrush(
            MilTileBrush b, double opacity, MilRect viewport, MilRect viewbox,
            double minThresh, double maxThresh,
            MilBrushMappingMode viewportUnits, MilBrushMappingMode viewboxUnits,
            MilStretch stretch, MilTileMode tileMode,
            MilAlignmentX alignX, MilAlignmentY alignY, MilCachingHint cachingHint,
            DUCE.ResourceHandle transform, DUCE.ResourceHandle relativeTransform)
        {
            b.Opacity = opacity;
            b.Viewport = viewport;
            b.Viewbox = viewbox;
            b.CacheInvalidationThresholdMinimum = minThresh;
            b.CacheInvalidationThresholdMaximum = maxThresh;
            b.ViewportUnits = viewportUnits;
            b.ViewboxUnits = viewboxUnits;
            b.Stretch = stretch;
            b.TileMode = tileMode;
            b.AlignmentX = alignX;
            b.AlignmentY = alignY;
            b.CachingHint = cachingHint;
            b.Transform = transform;
            b.RelativeTransform = relativeTransform;
        }
    }
}
