// Licensed to the .NET Foundation under one or more agreements.
//
// 位图源（命令字 0x0c MilCmdBitmapSource / 0x0d MilCmdBitmapInvalidate）的 Linux 侧实现。
//
// 【为什么这个文件在 Commands/】
//   与 MilResource3D.cs 同样的理由：Resources/MilResourceTable.cs 的工厂对
//   TYPE_BITMAPSOURCE 返回占位 MilOpaqueResource，而 Resources/ 下有别的 agent
//   正在改 MilChannel.cs。位图状态挂在一张**以资源实例为弱键**的附加表上：
//     · 不改 Resources/ 一行
//     · 资源被 Release 后表项随之回收，不泄漏
//     · 句柄复用时会拿到新的 MilResource 实例，不会读到上一任的残留位图
//
// 【Linux 侧对 IWICBitmapSource 的等价替代】
//   上游 MILCMD_BITMAP_SOURCE 的第 3 个字段是 IWICBitmapSource*（COM 接口指针），
//   靠「发送前 AddRef → 接收方接手该引用」在同一进程内传递。Linux 上没有 WIC，
//   且本实现的命令流是纯字节流，进程地址没有意义。这里用 **SKBitmap 作 WIC 的等价物**，
//   并把同样 8 字节的列改作位图令牌，传递语义与上游的引用一一对应。
//   详见 MilCommandStructs.cs 里 MILCMD_BITMAP_SOURCE 的注释。
//
// 【不做的事】
//   不做解码/缩放/格式转换的运行时行为（上游 C 类依赖 WIC 的那部分）。
//   SKBitmap 由调用方按最终尺寸与格式准备好；本层只负责登记、传递与失效。

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SkiaSharp;
using WpfGfx.Linux.Contracts;
using WpfGfx.Linux.Interop;
using WpfGfx.Linux.Rendering;
using WpfGfx.Linux.Resources;

namespace WpfGfx.Linux.Commands
{
    /// <summary>挂在一个 TYPE_BITMAPSOURCE 资源实例上的位图状态。</summary>
    internal sealed class MilBitmapSourceState
    {
        /// <summary>当前绑定的位图。未绑定（上游 m_pIBitmap == NULL）时为 null。</summary>
        public SKBitmap Bitmap { get; set; }

        /// <summary>收到的 AddDirtyRect 次数（含不带脏矩形的「整图失效」）。</summary>
        public int DirtyRectCount { get; set; }

        /// <summary>最近一次脏矩形；从未带矩形时为空。</summary>
        public MilRectI? LastDirtyRect { get; set; }

        /// <summary>每次 ProcessSource 成功替换位图时 +1，供渲染层判断缓存是否需要重建。</summary>
        public int Version { get; set; }

        /// <summary>用新位图替换旧的，并释放旧位图（对应上游 ReplaceInterface + Release）。</summary>
        public void Replace(SKBitmap bitmap)
        {
            SKBitmap old = Bitmap;
            Bitmap = bitmap;
            Version++;
            old?.Dispose();
        }
    }

    /// <summary>
    /// 位图令牌登记表 + 资源附加表 + 渲染层接线处。
    /// </summary>
    internal static class MilBitmapSourceTable
    {
        // ---- 令牌登记表（进程内全局） ----

        private static readonly object s_gate = new object();
        private static readonly Dictionary<ulong, SKBitmap> s_pending = new Dictionary<ulong, SKBitmap>();
        private static ulong s_nextToken = 1;   // 0 恒为非法令牌

        /// <summary>登记一张位图并领取令牌。**所有权随之转移给登记表**，调用方不要再 Dispose。</summary>
        /// <returns>合法令牌（&gt;0）；位图为空或尺寸非正时返回 0（拒绝登记）。</returns>
        public static ulong Register(SKBitmap bitmap)
        {
            if (!IsUsable(bitmap)) return 0;

            lock (s_gate)
            {
                ulong token = s_nextToken++;
                s_pending[token] = bitmap;
                return token;
            }
        }

        /// <summary>
        /// 取出并注销令牌对应的位图，**所有权随之转移给调用方**（调用方负责 Dispose）。
        /// 对应上游「该引用在传输期间保活、随后交给从端位图资源」的语义。
        /// </summary>
        public static bool TryTake(ulong token, out SKBitmap bitmap)
        {
            bitmap = null;
            if (token == 0) return false;

            lock (s_gate)
            {
                if (!s_pending.TryGetValue(token, out SKBitmap bmp)) return false;
                s_pending.Remove(token);
                bitmap = bmp;
                return true;
            }
        }

        /// <summary>丢弃令牌（发送失败时补做清理，对应上游失败分支的 ReleaseInterface）。</summary>
        public static void Discard(ulong token)
        {
            if (token == 0) return;

            lock (s_gate)
            {
                if (s_pending.TryGetValue(token, out SKBitmap bmp))
                {
                    s_pending.Remove(token);
                    bmp?.Dispose();
                }
            }
        }

        /// <summary>尚未被取走的位图数（测试用）。</summary>
        public static int PendingCount
        {
            get { lock (s_gate) { return s_pending.Count; } }
        }

        private static bool IsUsable(SKBitmap bitmap) =>
            bitmap != null && bitmap.Width > 0 && bitmap.Height > 0 && !bitmap.IsNull;

        // ---- 资源附加表 ----

        private static readonly ConditionalWeakTable<MilResource, MilBitmapSourceState> s_attached = new();

        /// <summary>取资源上已附加的位图状态；没有则 null。</summary>
        public static MilBitmapSourceState LookupState(MilResource owner)
        {
            if (owner == null) return null;
            s_attached.TryGetValue(owner, out MilBitmapSourceState state);
            return state;
        }

        private static MilBitmapSourceState GetOrAttach(MilResource owner) =>
            s_attached.GetValue(owner, _ => new MilBitmapSourceState());

        /// <summary>取资源当前绑定的位图；未绑定返回 null。</summary>
        public static SKBitmap LookupBitmap(MilResource owner) => LookupState(owner)?.Bitmap;

        // ---- 命令语义 ----

        /// <summary>
        /// 0x0c MilCmdBitmapSource。对应上游 CMilSlaveBitmap::ProcessSource
        /// （WpfGfx/core/resources/bitmapres.cpp:58）。
        /// </summary>
        public static int ProcessSource(MilResource resource, ulong token)
        {
            if (resource == null) return HResult.E_INVALIDARG;

            // 【HRESULT 必须与上游一致：E_HANDLE，不是 E_INVALIDARG】
            //   我们是**接收侧**（CMilSlaveBitmap::ProcessSource），上游的空对象校验是
            //     IFCNULL(pCWICWrapperBitmap)   —— bitmapres.cpp:64
            //   而 IFCNULL 展开是 CHECKPTRHRGOTO(Cleanup, obj, E_HANDLE)
            //     （WpfGfx/shared/util/UtilLib/instrumentationapi.h:915）
            //   即上游拿不到有效位图对象时返回 **E_HANDLE**。
            //   E_INVALIDARG 属于**发送侧** MilResource_SendCommandBitmapSource 的
            //   CHECKPTRARG(pIBitmapSource)（apifunc.cpp:728）——那是 apifunc 层，不是本层。
            //   前一个 agent 把两侧混为一谈，写成了 E_INVALIDARG，此处纠正为 E_HANDLE。
            //
            // 本实现：令牌 0 或不在登记表里，等价于上游拿不到有效对象。
            if (!TryTake(token, out SKBitmap bitmap) || !IsUsable(bitmap))
            {
                bitmap?.Dispose();      // 对应上游 Cleanup 里的 ReleaseInterface
                return HResult.E_HANDLE;
            }

            GetOrAttach(resource).Replace(bitmap);
            return HResult.S_OK;
        }

        /// <summary>
        /// 0x0d MilCmdBitmapInvalidate。对应上游 CMilSlaveBitmap::ProcessInvalidate
        /// （bitmapres.cpp:80）：只有在**已绑定位图**时才 AddDirtyRect；
        /// 没绑定位图时上游仍然返回 S_OK（只是 NotifyOnChanged），这里保持一致。
        /// </summary>
        public static int ProcessInvalidate(MilResource resource, bool useDirtyRect, MilRectI dirtyRect)
        {
            if (resource == null) return HResult.E_INVALIDARG;

            MilBitmapSourceState state = LookupState(resource);
            if (state?.Bitmap == null) return HResult.S_OK;   // 上游 m_pIBitmap 为空：静默成功

            state.DirtyRectCount++;
            state.LastDirtyRect = useDirtyRect ? dirtyRect : (MilRectI?)null;
            return HResult.S_OK;
        }

        // ---- 渲染层接线 ----

        /// <summary>
        /// 挂到渲染层的资源提供者上。这是位图与 T4 渲染后端的**唯一**接线处，
        /// 不修改 Rendering/ 下任何文件（与 Text/TextRenderer.AttachTo 同一套路）。
        /// </summary>
        public static void AttachTo(MilResourceProvider provider)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));

            provider.BitmapResolver = handle =>
            {
                if (handle.IsNull) return null;
                object resource = provider.Lookup(handle);
                return LookupBitmap(resource as MilResource);
            };
        }
    }
}
