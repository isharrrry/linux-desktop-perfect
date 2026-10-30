// Licensed to the .NET Foundation under one or more agreements.
//
// M7a：新导出需要的**进程内句柄表**。
//
// 【统一口径】所有句柄都是由单调计数器下发的 IntPtr，**永不复用**。
//   这与 Resources/MilChannel.cs 里 MilChannelRegistry 的做法一致，理由也一样：
//   GCHandle/指针槽位复用会导致 ABA（陈旧的句柄误伤新建对象）。上游 Windows 口
//   的句柄是内核对象指针，天然不复用；Linux 口用单调计数模拟这个性质。
//
// 【为什么这些表是 public】
//   MilNative 的新导出方法签名里会出现这些表的下发句柄（IntPtr），
//   测试与将来接进来的托管层需要通过公开入口登记 / 查询（例如把某个 X11 窗口
//   登记成 HWND、把某个 SKTypeface 登记成 DWrite 字体面）。表本身不泄漏内部
//   实现细节，只暴露 Register/Resolve/Count 这类必要操作。
//
// 【各表的角色】
//   MilHwndRegistry         HWND → 窗口身份绑定（MilVisualTarget_/MilContent_ AttachToHwnd）
//   MilConnectionTable      WgxConnection_Create/Disconnect 的连接对象
//   MilDeviceObjectTable    MIL 的 COM 式对象（工厂 / 渲染目标 / 事件代理 / 流 / 反向包装）
//   MilPixelBufferTable     "IWICBitmap 等价物"：必须在调用方持有期间保持存活的位图
//   MilColorContextTable    IWICColorContext 等价物（profile 字节 + 类型 + EXIF 色彩空间）
//   MilFontFaceTable        DWrite 字体面指针 → SKTypeface（MilGlyphRun_GetGlyphOutline）
//   MilRenderTimeTargetTable 渲染时目标（glyph cache / glyph run 的 *AtRenderTime 三件套）
//   MilCompositionEngineState 合成引擎锁 / 分区管理器 / 进程级渲染开关

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using SkiaSharp;

namespace WpfGfx.Linux.Interop
{
    /// <summary>进程内单调句柄分配器。0 恒为非法句柄。</summary>
    internal static class MilHandleSource
    {
        /// <summary>
        /// 起始基数刻意取大值，避免与调用方手写的"假句柄"（测试里的 0x1234 之类）撞车。
        /// 与 MilChannelRegistry.HandleBase 同一数量级但错开，方便日志里区分来源。
        /// </summary>
        private const long Base = 0x2000_0000;

        private static long _next = Base;

        public static IntPtr Next() => (IntPtr)Interlocked.Increment(ref _next);

        public static long PeekNext() => Interlocked.Read(ref _next);
    }

    // ==================================================================
    //  HWND → 窗口身份绑定
    // ==================================================================

    /// <summary>HWND 在原生侧的角色。</summary>
    public enum MilHwndRole
    {
        /// <summary>呈现目标（MilVisualTarget_AttachToHwnd）。一个窗口同时只能有一个。</summary>
        VisualTarget = 0,

        /// <summary>MIL 内容提示（MilContent_AttachToHwnd；Windows 上是 DWM 提示）。</summary>
        Content = 1,
    }

    /// <summary>一条 HWND 绑定记录。</summary>
    public sealed class MilHwndBinding
    {
        /// <summary>HWND 值（Linux 上就是 X11PresentationTarget.NativeHandle / XID）。</summary>
        public IntPtr Hwnd;

        /// <summary>角色。</summary>
        public MilHwndRole Role;

        /// <summary>绑定序号（第几个被登记的窗口），便于测试断言"句柄不复用"。</summary>
        public long Sequence;

        /// <summary>绑定时刻（毫秒时间戳，仅诊断用）。</summary>
        public long AttachedAtTicks;

        public override string ToString() =>
            $"HWND=0x{Hwnd.ToInt64():X} role={Role} seq={Sequence}";
    }

    /// <summary>
    /// HWND → 窗口身份注册表。
    ///
    /// 【当前实现到哪一步（M7a 边界，必须说清楚）】
    ///   本轮只落**身份映射语义**：登记 / 查表 / 解绑 / 幂等 / 冲突拒绝。
    ///   真实的接窗（把这个 HWND 对到 X11Window、把 DUCE 的
    ///   TYPE_HWNDRENDERTARGET 命令路由到 X11PresentationTarget.Present）是
    ///   M2/后续 milestone 的事，本轮不做——本类不含任何 X11 调用。
    ///
    /// 【HRESULT 与上游逐条对齐（WpfGfx/core/uce/vt_api.cpp:24/67）】
    ///   AttachVisualTarget : 已在表里 → E_ACCESSDENIED；否则登记 → S_OK
    ///   DetachVisualTarget : 不在表里 → E_INVALIDARG；否则移除 → S_OK
    ///   Content 的 Attach/Detach：Windows 上只是"给 DWM 发提示"，非 DWM 平台上
    ///   恒 S_OK。这里保持恒 S_OK（幂等），但同样登记 / 移除，让上层能查表。
    /// </summary>
    public static class MilHwndRegistry
    {
        private static readonly ConcurrentDictionary<IntPtr, MilHwndBinding> _bindings =
            new ConcurrentDictionary<IntPtr, MilHwndBinding>();

        private static long _sequence;

        /// <summary>登记一个呈现目标。已存在同角色绑定时返回 false（对应 E_ACCESSDENIED）。</summary>
        public static bool TryAttachVisualTarget(IntPtr hwnd, out MilHwndBinding binding)
        {
            binding = null;
            if (hwnd == IntPtr.Zero) return false;

            var candidate = new MilHwndBinding
            {
                Hwnd = hwnd,
                Role = MilHwndRole.VisualTarget,
                Sequence = Interlocked.Increment(ref _sequence),
                AttachedAtTicks = Environment.TickCount64,
            };

            if (!_bindings.TryAdd(hwnd, candidate))
            {
                // 已经有人占了：把刚分配的序号退回去没意义（序号不做唯一性断言之外的事），
                // 但要让调用方能区分"我加的"和"别人加的"。
                return false;
            }

            binding = candidate;
            return true;
        }

        /// <summary>解绑呈现目标。不存在返回 false（对应 E_INVALIDARG）。</summary>
        public static bool TryDetachVisualTarget(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            if (!_bindings.TryGetValue(hwnd, out MilHwndBinding binding)) return false;
            if (binding.Role != MilHwndRole.VisualTarget) return false;

            return _bindings.TryRemove(hwnd, out _);
        }

        /// <summary>登记 / 更新 MIL 内容提示。恒成功（幂等）。</summary>
        public static bool AttachContent(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;

            _bindings.AddOrUpdate(
                hwnd,
                _ => new MilHwndBinding
                {
                    Hwnd = hwnd,
                    Role = MilHwndRole.Content,
                    Sequence = Interlocked.Increment(ref _sequence),
                    AttachedAtTicks = Environment.TickCount64,
                },
                (_, existing) => existing);

            return true;
        }

        /// <summary>移除 MIL 内容提示。恒成功（幂等，不存在也算成功）。</summary>
        public static bool DetachContent(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;

            if (_bindings.TryGetValue(hwnd, out MilHwndBinding binding) &&
                binding.Role == MilHwndRole.Content)
            {
                _bindings.TryRemove(hwnd, out _);
            }
            return true;
        }

        /// <summary>查表。</summary>
        public static MilHwndBinding Resolve(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;
            _bindings.TryGetValue(hwnd, out MilHwndBinding binding);
            return binding;
        }

        public static bool IsAttached(IntPtr hwnd) => Resolve(hwnd) != null;

        public static int Count => _bindings.Count;

        /// <summary>当前所有绑定（快照，测试与诊断用）。</summary>
        public static IReadOnlyCollection<MilHwndBinding> Bindings => new List<MilHwndBinding>(_bindings.Values);

        /// <summary>清空（只给测试用：xunit 并行下不要依赖进程级残留）。</summary>
        public static void Reset()
        {
            _bindings.Clear();
            Interlocked.Exchange(ref _sequence, 0);
        }
    }

    // ==================================================================
    //  WgxConnection
    // ==================================================================

    /// <summary>一条 MIL 连接（WgxConnection_Create 的产物）。</summary>
    public sealed class MilConnectionObject
    {
        public IntPtr Handle;

        /// <summary>requestSynchronousTransport：上游映射到 MilMarshalType::SameThread。</summary>
        public bool RequestSynchronousTransport;

        public ChannelMarshalType MarshalType =>
            RequestSynchronousTransport
                ? ChannelMarshalType.ChannelMarshalTypeSameThread
                : ChannelMarshalType.ChannelMarshalTypeCrossThread;

        /// <summary>已通过 SyncFlush 强制刷新的次数（诊断）。</summary>
        public int SyncFlushCount;

        public override string ToString() =>
            $"MilConnection(0x{Handle.ToInt64():X} marshal={MarshalType})";
    }

    /// <summary>连接注册表。句柄单调下发、不复用。</summary>
    public static class MilConnectionTable
    {
        private static readonly ConcurrentDictionary<IntPtr, MilConnectionObject> _connections =
            new ConcurrentDictionary<IntPtr, MilConnectionObject>();

        public static MilConnectionObject Create(bool requestSynchronousTransport)
        {
            var connection = new MilConnectionObject
            {
                Handle = MilHandleSource.Next(),
                RequestSynchronousTransport = requestSynchronousTransport,
            };
            _connections[connection.Handle] = connection;
            return connection;
        }

        public static MilConnectionObject Resolve(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return null;
            _connections.TryGetValue(handle, out MilConnectionObject c);
            return c;
        }

        public static bool Destroy(IntPtr handle) =>
            handle != IntPtr.Zero && _connections.TryRemove(handle, out _);

        public static int Count => _connections.Count;

        public static void Reset() => _connections.Clear();
    }

    // ==================================================================
    //  MIL 设备对象（COM 式引用计数）
    // ==================================================================

    /// <summary>MIL 设备对象的种类。</summary>
    public enum MilDeviceObjectKind
    {
        Unknown = 0,
        Factory = 1,
        BitmapRenderTarget = 2,
        SwDoubleBufferedBitmap = 3,
        CwicWrapperBitmap = 4,
        EventProxy = 5,
        Stream = 6,
        InteropDeviceBitmap = 7,
        MediaPlayer = 8,

        /// <summary>
        /// 可被当作 **IWICBitmapSource** 查询的像素缓冲（M7c 轨道 B）。
        /// 目前只有一种来源：`MILSwDoubleBufferedBitmapGetBackBuffer` 交出去的**后台缓冲**。
        /// 见 <see cref="MilBackBufferSourceTable"/> 的说明。
        /// </summary>
        WicBitmapSource = 9,
    }

    /// <summary>
    /// 标记：这个负载**不归设备对象所有**，析构设备对象时不得释放它。
    /// <see cref="MilDeviceObjectTable.DisposePayload"/> 对它显式跳过 —— 这样
    /// "忘了加 case 就静默不释放"不会被将来的人当成 bug 顺手改成释放。
    /// </summary>
    public interface IMilNonOwningPayload { }

    /// <summary>
    /// 后台缓冲设备对象的负载：**指向**双缓冲状态的一张"接口视图"，
    /// 不拥有 <see cref="MilDoubleBufferedState"/>（它归 swdbb 主设备对象所有）。
    ///
    /// 【为什么需要它】`MilQueryInterface(token, IID_IWICBitmapSource)` 成功后会 AddRef
    ///   这个设备对象；PC 侧 `BitmapSourceSafeMILHandle.Dispose`/`SafeMILHandle` 的 Release
    ///   把它减到 0 ⇒ 设备对象被摘除。若这里挂的是 `MilDoubleBufferedState`，
    ///   `DisposePayload` 会把**仍在使用中**的两张 SKBitmap 释放掉（use-after-free）。
    ///   挂视图则什么也不释放，同时保留 state 链接（将来补 WIC GetSize/GetPixelFormat 要用）。
    /// </summary>
    public sealed class MilBackBufferView : IMilNonOwningPayload
    {
        public readonly MilDoubleBufferedState State;

        /// <summary>像素缓冲令牌（= `MILSwDoubleBufferedBitmapGetBackBuffer` 交出去的那个句柄）。</summary>
        public readonly IntPtr Token;

        public MilBackBufferView(MilDoubleBufferedState state, IntPtr token)
        {
            State = state;
            Token = token;
        }

        public override string ToString() => $"back-buffer view token=0x{Token.ToInt64():X}";
    }

    /// <summary>
    /// 离屏渲染目标（`MILRenderTargetBitmap.GetBitmap` 交出的位图句柄）的设备对象负载：
    /// 与 <see cref="MilBackBufferView"/> 同理，是**指向**渲染目标状态的一张"接口视图"，
    /// **不拥有**它 —— 那张 `SKBitmap` 归创建它的 RTB 主设备对象
    /// （`MilDeviceObjectKind.BitmapRenderTarget` 的 `MilRenderTargetState`）所有。
    ///
    /// 【为什么需要它】上游 `RenderTargetBitmap.FinalizeCreation`（`RenderTargetBitmap.cs:256`）
    ///   把 `GetBitmap` 的产物赋给 `WicSourceHandle`，紧接着
    ///   `BitmapSource.set_WicSourceHandle`（`BitmapSource.cs:584`）对该句柄调
    ///   `MILQueryInterface(handle, IID_IWICBitmapSource, out _)`。这个句柄此前只在
    ///   `MilPixelBufferTable` 里 —— MIL 设备对象表与 WIC 所有者都认不出它 ⇒ `E_HANDLE`。
    ///   挂视图后由 <see cref="MilBackBufferSourceTable"/> 把它别名到一个 `WicBitmapSource`
    ///   设备对象 ⇒ QI 走 MIL 自己那张账放行（不落进外部句柄路径，避免两边账本单边）。
    ///   引用计数归零时 `DisposePayload` 对 `IMilNonOwningPayload` 显式跳过 ⇒ 不会误释放
    ///   仍在用的位图。
    /// </summary>
    public sealed class MilRenderTargetView : IMilNonOwningPayload
    {
        public readonly MilRenderTargetState Target;

        /// <summary>像素缓冲令牌（= `MILRenderTargetBitmap.GetBitmap` 交出去的那个句柄）。</summary>
        public readonly IntPtr Token;

        public MilRenderTargetView(MilRenderTargetState target, IntPtr token)
        {
            Target = target;
            Token = token;
        }

        public override string ToString() => $"render-target view token=0x{Token.ToInt64():X}";
    }

    /// <summary>
    /// CWIC wrapper 设备对象的负载：**引用计数归零时回收物化出来的位图**（#24）。
    /// 与 `MilBackBufferView` 相反：那个是"借来的视图、不拥有负载"，这个是**拥有**的
    /// （位图是本工程为了包装 WIC 源而物化出来的）。
    /// </summary>
    public sealed class MilCwicWrapper : IDisposable
    {
        public IntPtr WrapperHandle;
        public void Dispose() => MilCwicWrapperTable.Release(WrapperHandle);
    }

    /// <summary>一个 MIL 设备对象。RefCount 语义与 COM 一致（0 → 销毁）。</summary>
    public sealed class MilDeviceObject
    {
        public IntPtr Handle;
        public MilDeviceObjectKind Kind;
        public uint RefCount = 1;

        /// <summary>负载：渲染目标持 SKBitmap，流持 MemoryStream，等等。</summary>
        public object Payload;

        /// <summary>任意诊断字段（例如 SDK 版本、尺寸）。</summary>
        public string Note;

        public override string ToString() =>
            $"{Kind}(0x{Handle.ToInt64():X} refs={RefCount})";
    }

    /// <summary>MIL 设备对象注册表：MILAddRef / MILRelease / MILQueryInterface 的作用域。</summary>
    public static class MilDeviceObjectTable
    {
        private static readonly ConcurrentDictionary<IntPtr, MilDeviceObject> _objects =
            new ConcurrentDictionary<IntPtr, MilDeviceObject>();

        private static readonly object _gate = new object();

        /// <summary>登记一个对象，RefCount = 1，返回其句柄。</summary>
        public static MilDeviceObject Register(MilDeviceObjectKind kind, object payload = null, string note = null)
        {
            var obj = new MilDeviceObject
            {
                Handle = MilHandleSource.Next(),
                Kind = kind,
                Payload = payload,
                Note = note,
            };
            _objects[obj.Handle] = obj;
            return obj;
        }

        public static MilDeviceObject Resolve(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return null;
            _objects.TryGetValue(handle, out MilDeviceObject obj);
            return obj;
        }

        /// <summary>AddRef。对象不存在返回 0（上游对空指针不会走到这里，见 MILAddRef 的注释）。</summary>
        public static uint AddRef(IntPtr handle)
        {
            lock (_gate)
            {
                MilDeviceObject obj = Resolve(handle);
                if (obj == null) return 0;
                obj.RefCount++;
                return obj.RefCount;
            }
        }

        /// <summary>
        /// Release。返回剩余引用数；对象不存在返回 -1（调用方据此返回 E_HANDLE）。
        /// 归零时从表里摘除并释放负载（SKBitmap / MemoryStream 等）。
        /// </summary>
        public static long Release(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return -1;

            lock (_gate)
            {
                MilDeviceObject obj = Resolve(handle);
                if (obj == null) return -1;

                if (obj.RefCount > 0) obj.RefCount--;

                if (obj.RefCount == 0)
                {
                    _objects.TryRemove(handle, out _);
                    DisposePayload(obj.Payload);
                    return 0;
                }
                return obj.RefCount;
            }
        }

        private static void DisposePayload(object payload)
        {
            switch (payload)
            {
                // 借用视图（见 MilBackBufferView）：**显式**不释放任何东西。
                case IMilNonOwningPayload _: break;
                case SKBitmap bitmap: bitmap.Dispose(); break;
                case MemoryStream stream: stream.Dispose(); break;
                case MilDoubleBufferedState state: state.Dispose(); break;
                case MilStreamObject stream: stream.Dispose(); break;
                // CWIC wrapper：**拥有**物化出来的位图 ⇒ 归零时连位图一起回收（#24 的"回基线"）。
                case MilCwicWrapper cwic: cwic.Dispose(); break;
                case MilRenderTargetState target: target.Dispose(); break;
            }
        }

        public static int Count => _objects.Count;

        /// <summary>清空并释放全部负载（测试用）。</summary>
        public static void Reset()
        {
            lock (_gate)
            {
                foreach (MilDeviceObject obj in _objects.Values) DisposePayload(obj.Payload);
                _objects.Clear();
            }
        }
    }

    // ==================================================================
    //  位图缓冲（IWICBitmap 等价物）
    // ==================================================================

    /// <summary>
    /// "调用方持有期间必须存活"的位图登记表。
    ///
    /// 【与 Commands/MilBitmapSource.cs 的 MilBitmapSourceTable 的分工】
    ///   MilBitmapSourceTable：**一次性移交**。令牌被 TryTake 取走后即失效，
    ///     对应上游"发送方 AddRef、接收方接手该引用"的命令流语义。
    ///   MilPixelBufferTable（本表）：**句柄保持有效**。调用方（例如
    ///     BitmapSource/DoubleBufferedBitmap）拿到句柄后可以反复读写同一张位图，
    ///     直到显式 Unregister 或引用计数归零。上游这些位置上放的是 IWICBitmap
    ///     COM 对象，生命周期由引用计数管理。
    ///   两者混用会出错，所以刻意做成两张表。
    /// </summary>
    public static class MilPixelBufferTable
    {
        private sealed class Entry
        {
            public SKBitmap Bitmap;
            public uint RefCount = 1;
            public string Note;

            /// <summary>借用（所有权在别处）：Unregister 时只摘句柄，不 Dispose。</summary>
            public bool Borrowed;
        }

        private static readonly ConcurrentDictionary<IntPtr, Entry> _buffers =
            new ConcurrentDictionary<IntPtr, Entry>();

        /// <summary>登记一张位图并领取句柄。**所有权转移给本表**。</summary>
        public static IntPtr Register(SKBitmap bitmap, string note = null)
        {
            if (bitmap == null || bitmap.IsNull) return IntPtr.Zero;

            IntPtr handle = MilHandleSource.Next();
            _buffers[handle] = new Entry { Bitmap = bitmap, Note = note };
            return handle;
        }

        /// <summary>
        /// 登记一张**借用**的位图（所有权仍在别处，本表只发句柄、并在注销时
        /// 不做 Dispose）。用于"位图对象自己有主人"的场景：双缓冲位图的缓冲、
        /// 渲染目标的位图、CWIC 包装。Unregister 时一律只摘句柄。
        /// </summary>
        public static IntPtr RegisterBorrowed(SKBitmap bitmap, string note = null)
        {
            if (bitmap == null || bitmap.IsNull) return IntPtr.Zero;

            IntPtr handle = MilHandleSource.Next();
            _buffers[handle] = new Entry { Bitmap = bitmap, Note = note, Borrowed = true };
            return handle;
        }

        public static SKBitmap Resolve(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return null;
            _buffers.TryGetValue(handle, out Entry entry);
            return entry?.Bitmap;
        }

        public static uint AddRef(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return 0;
            if (!_buffers.TryGetValue(handle, out Entry entry)) return 0;
            entry.RefCount++;
            return entry.RefCount;
        }

        /// <summary>注销句柄并释放位图。返回 false 表示句柄不存在（陈旧句柄）。</summary>
        public static bool Unregister(IntPtr handle, bool force = false)
        {
            if (handle == IntPtr.Zero) return false;
            if (!_buffers.TryGetValue(handle, out Entry entry)) return false;

            if (!force && entry.RefCount > 1)
            {
                entry.RefCount--;
                return true;
            }

            if (!_buffers.TryRemove(handle, out entry)) return false;
            if (!entry.Borrowed) entry.Bitmap?.Dispose();
            return true;
        }

        public static int Count => _buffers.Count;

        public static IReadOnlyCollection<IntPtr> Handles => new List<IntPtr>(_buffers.Keys);

        public static void Reset()
        {
            foreach (IntPtr key in new List<IntPtr>(_buffers.Keys))
            {
                if (_buffers.TryRemove(key, out Entry entry) && !entry.Borrowed) entry.Bitmap?.Dispose();
            }
        }
    }

    // ==================================================================
    //  颜色上下文（IWICColorContext 等价物）
    // ==================================================================

    /// <summary>一个颜色上下文：ICC profile 字节 + 类型 + EXIF 色彩空间。</summary>
    public sealed class MilColorContext
    {
        public MilWicColorContextType Type = MilWicColorContextType.WICColorContextUninitialized;
        public byte[] ProfileBytes = Array.Empty<byte>();
        public uint ExifColorSpace;

        public override string ToString() =>
            $"ColorContext({Type} profile={ProfileBytes.Length}B exif={ExifColorSpace})";
    }

    /// <summary>IWICColorContext_*_Proxy 的作用域。</summary>
    public static class MilColorContextTable
    {
        private static readonly ConcurrentDictionary<IntPtr, MilColorContext> _contexts =
            new ConcurrentDictionary<IntPtr, MilColorContext>();

        public static IntPtr Register(MilColorContext context)
        {
            if (context == null) return IntPtr.Zero;
            IntPtr handle = MilHandleSource.Next();
            _contexts[handle] = context;
            return handle;
        }

        public static MilColorContext Resolve(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return null;
            _contexts.TryGetValue(handle, out MilColorContext ctx);
            return ctx;
        }

        public static bool Unregister(IntPtr handle) =>
            handle != IntPtr.Zero && _contexts.TryRemove(handle, out _);

        public static int Count => _contexts.Count;

        public static void Reset() => _contexts.Clear();
    }

    // ==================================================================
    //  字体面（DWrite 字体面指针 → SKTypeface）
    // ==================================================================

    /// <summary>
    /// DWrite 字体面指针 → SKTypeface 的映射表。
    ///
    /// 【为什么需要它】MilGlyphRun_GetGlyphOutline 的第一个参数在上游是
    ///   IDWriteFontFace*（MilGlyphRun.cs 里传的是 DWriteFontFaceAddRef 的裸指针）。
    ///   在 Linux 上这个指针没有意义，也不该解引用。这里的做法与 Text/ 的
    ///   MilGlyphRunAdapter 完全一致：**由上层显式登记映射**，原生层只按句柄查表。
    ///   未登记时用 <see cref="DefaultTypeface"/>（M1 的打包字体），
    ///   一个都没配就返回 E_HANDLE——不猜。
    ///
    /// 【跨运行时边界（T1/M7c2 实测发现的坑）】
    ///   本表是**进程内静态状态**。T1 之后 MIL 跑在 NativeAOT 共享库
    ///   （`wpfgfx_cor3.so`）里 —— **那是另一个 .NET 运行时**，静态状态与托管侧
    ///   各一份。所以托管侧直接调 `Register` 只会写进一个 .so 永远看不见的副本。
    ///   能跨过去的只有 **C ABI**：托管侧调 `MilFontFace_RegisterFromFile`
    ///   （.so 内部导出，见 MilNative.FontFace.cs），由 .so 自己的运行时登记。
    /// </summary>
    public static class MilFontFaceTable
    {
        /// <summary>来源键 `(path, faceIndex, simFlags)` → 句柄（同 key 幂等，见 RegisterFromFile）。</summary>
        private static readonly ConcurrentDictionary<string, IntPtr> _bySource =
            new ConcurrentDictionary<string, IntPtr>();

        private static readonly ConcurrentDictionary<IntPtr, SKTypeface> _faces =
            new ConcurrentDictionary<IntPtr, SKTypeface>();

        /// <summary>句柄 → 字体模拟标志（DWRITE_FONT_SIMULATIONS）。仅非 0 时入表。</summary>
        private static readonly ConcurrentDictionary<IntPtr, int> _simFlags =
            new ConcurrentDictionary<IntPtr, int>();

        /// <summary>未登记字体面时使用的兜底字体（骨架字体，不参与绘制）。</summary>
        public static SKTypeface DefaultTypeface { get; set; }

        /// <summary>字体模拟标志：加粗（= DWRITE_FONT_SIMULATIONS_BOLD）。</summary>
        public const int SimulationBold = 0x1;

        /// <summary>字体模拟标志：倾斜（= DWRITE_FONT_SIMULATIONS_OBLIQUE）。</summary>
        public const int SimulationOblique = 0x2;

        /// <summary>登记一个"字体面指针"（可以是任意非 0 标记值）。**不接管所有权**。</summary>
        public static IntPtr Register(SKTypeface typeface) => Register(typeface, 0);

        /// <summary>
        /// 登记字体面并记下模拟标志。
        /// <paramref name="simFlags"/> 语义 = DWrite 的 `DWRITE_FONT_SIMULATIONS`：
        ///   0 = 不模拟，1 = Bold，2 = Oblique，3 = 两者。
        /// 高位的未知位**不报错也不生效**，原样记下来由 <see cref="TryGetSimFlags"/> 可查。
        /// </summary>
        public static IntPtr Register(SKTypeface typeface, int simFlags)
        {
            if (typeface == null) return IntPtr.Zero;
            IntPtr handle = MilHandleSource.Next();
            _faces[handle] = typeface;
            if (simFlags != 0) _simFlags[handle] = simFlags;
            return handle;
        }

        /// <summary>
        /// 从字体文件登记一个字体面（**跨运行时导出 MilFontFace_RegisterFromFile 的落点**）。
        ///
        /// 任何失败都返回 <see cref="IntPtr.Zero"/>，**不抛异常**（调用方在 C ABI 另一侧，
        /// 异常穿不过去也没意义）：路径为空 / 文件不存在 / 不是可解析的字体 /
        /// faceIndex 越界 / Skia 抛错，一律 0。
        ///
        /// 【M7c3】每条失败路径都会把**具体原因**记进
        /// <see cref="MilFontFaceDiagnostics"/>（含异常类型与消息）——
        /// 这样"返回 0"不再是一个黑盒。
        /// </summary>
        public static IntPtr RegisterFromFile(string path, int faceIndex, int simFlags)
        {
            if (string.IsNullOrEmpty(path))
            {
                MilFontFaceDiagnostics.ReportFailure(MilFontFaceDiagnostics.Failure.EmptyPath, "path 为空");
                return IntPtr.Zero;
            }
            if (faceIndex < 0)
            {
                MilFontFaceDiagnostics.ReportFailure(MilFontFaceDiagnostics.Failure.NegativeFaceIndex,
                    $"faceIndex={faceIndex}");
                return IntPtr.Zero;
            }

            SKTypeface typeface;
            try
            {
                if (!System.IO.File.Exists(path))
                {
                    MilFontFaceDiagnostics.ReportFailure(MilFontFaceDiagnostics.Failure.FileNotFound, path);
                    return IntPtr.Zero;
                }
                // [W8 / D-F1c 内存半边] **每规范路径一份共享 `SKData`（一份整文件映射）+ 逐面 `FromData`**。
                //   今天这里是 `SKTypeface.FromFile(path, faceIndex)` —— 实测**每面 mmap 一整份文件**
                //   （10 面 ttc：段数 10、Σ虚拟 185.9 MB；改后 1 段、18.6 MB；逐面指纹 10/10 相同）。
                typeface = SkiaFontFileCache.CreateFace(path, faceIndex);
            }
            catch (Exception ex)
            {
                // Skia 对损坏文件会抛；C ABI 契约要求"失败返回 0"，不是抛出去。
                // ⚠️ 这里也是 **DllNotFoundException: libSkiaSharp** 的落点 ——
                //    M7c3 之前它被无声吞掉，T2 只看到 nativeAllocations=0。
                MilFontFaceDiagnostics.ReportException(ex);
                return IntPtr.Zero;
            }

            if (typeface == null)
            {
                MilFontFaceDiagnostics.ReportFailure(MilFontFaceDiagnostics.Failure.TypefaceLoadFailed,
                    $"SkiaFontFileCache.CreateFace 返回 null：path={path} faceIndex={faceIndex}" +
                    "（共享 SKData 建不出 / 该 index 没有面 / 文件不是字体）");
                return IntPtr.Zero;
            }

            // 【债务 #14 的前提：同一份面必须拿到**同一个句柄**】
            //   方案 A（PC 把"实际用的面"登记一次、把句柄写进 `PIDWriteFont`）依赖"句柄稳定"：
            //   若 PC 因任何原因**重复注册**同一份面而拿到新句柄，那么**先前那批 run 里的旧句柄**
            //   就会解析成"另一份面"（或解析失败）—— 这是"给字段换语义"这一类缺陷的经典触发方式。
            //   所以这里做**同 key 幂等**：`(path, faceIndex, simFlags)` 相同 ⇒ 复用既有句柄。
            //   ⚠️ 实测过"修前不幂等"（两次调用两个句柄），测试见 `MilFontFaceTableIdempotencyTests`。
            string key = $"{path}\u0000{faceIndex}\u0000{simFlags}";
            if (_bySource.TryGetValue(key, out IntPtr existing) && _faces.ContainsKey(existing))
            {
                typeface.Dispose();                     // 新加载的那份不再需要（避免泄漏）
                MilFontFaceDiagnostics.ReportSuccess();
                return existing;
            }

            IntPtr handle = Register(typeface, simFlags);
            if (handle == IntPtr.Zero)
            {
                MilFontFaceDiagnostics.ReportFailure(MilFontFaceDiagnostics.Failure.TypefaceLoadFailed,
                    "Register 返回 0");
                return IntPtr.Zero;
            }

            _bySource[key] = handle;

            // 【只读诊断】把"这份面来自哪个文件 / 第几个 face"与"覆盖不覆盖 U+4E2D"记进 census。
            //   缺省关（`WPF_LINUX_GLYPH_FACE_CENSUS != 1` 时两函数立即返回）⇒ 调用次数与行为不受影响。
            //   为什么非记不可：走"按句柄"那条路的面**不经过 `FontSet`**，所以此前一直显示 `file=<未知>`，
            //   无法判定"渲染器用的这两份面是不是 PC 真正用的那两份、是不是 CJK 面"。
            Text.GlyphFaceCensus.NoteFaceSource(typeface, path, faceIndex);
            Text.GlyphFaceCensus.NoteFaceCoverage(typeface);

            MilFontFaceDiagnostics.ReportSuccess();
            return handle;
        }

        /// <summary>查询该句柄的模拟标志（未登记句柄 → false；已登记无模拟 → true + 0）。</summary>
        public static bool TryGetSimFlags(IntPtr handle, out int simFlags)
        {
            simFlags = 0;
            if (handle == IntPtr.Zero) return false;
            if (!_faces.ContainsKey(handle)) return false;
            if (_simFlags.TryGetValue(handle, out int v)) simFlags = v;
            return true;
        }

        public static bool TryResolve(IntPtr handle, out SKTypeface typeface)
        {
            typeface = null;
            if (handle == IntPtr.Zero) return false;

            if (_faces.TryGetValue(handle, out typeface) && typeface != null) return true;

            typeface = DefaultTypeface;
            return typeface != null;
        }

        /// <summary>
        /// **精确**查询：只认"这个句柄确实登记过"，**不做 `DefaultTypeface` 回落**。
        ///
        /// 【为什么必须有它 —— 本轮我自己的假命中】`TryResolve` 在句柄不认识时会回落
        ///   `DefaultTypeface` **并返回 true**（这是它既有的契约，给轮廓路径用的）。
        ///   我第一版 `MilFaceResolver` 直接用了 `TryResolve` ⇒ **"PC 填的 pid 我们一个都不认识"
        ///   这件事被伪装成了"48 个 run 全部按句柄命中"**（在 `DefaultTypeface != null` 的桥上）。
        ///   ⇒ 债务 #14 的判据**必须**用这个不带回落的版本：命中 = 真登记过。
        ///   `TryResolve`（带回落）保持原样不动，它的既有消费者不受影响。
        /// </summary>
        public static bool TryResolveExact(IntPtr handle, out SKTypeface typeface)
        {
            typeface = null;
            if (handle == IntPtr.Zero) return false;
            return _faces.TryGetValue(handle, out typeface) && typeface != null;
        }

        public static int Count => _faces.Count;

        public static void Reset()
        {
            _faces.Clear();
            _simFlags.Clear();
            DefaultTypeface = null;
        }
    }

    // ==================================================================
    //  渲染时目标（*_AtRenderTime 三件套）
    // ==================================================================

    /// <summary>
    /// 渲染时目标：一个可在渲染线程上累积命令的批次缓冲。
    ///
    /// 上游 MilGlyphCache_* / MilGlyphRun_SetGeometryAtRenderTime 的第一个参数是
    /// **原生从端对象指针**（CMilSlaveGlyphCache* 等），渲染线程直接往它身上写命令。
    /// Linux 侧没有"原生从端对象"，这里用登记句柄代替，并把命令按
    /// Begin → Append* → End 的状态机累积进 <see cref="Commands"/>；
    /// 上层（渲染线程）随后取出执行。状态机的错误码与 MilChannel 的批次机一致，
    /// 便于两边共用一套心智模型。
    /// </summary>
    public sealed class MilRenderTimeTarget
    {
        public IntPtr Handle;
        public MilDeviceObjectKind KindHint = MilDeviceObjectKind.Unknown;

        /// <summary>已闭合的命令（每次 EndCommand 追加一条）。</summary>
        public readonly List<byte[]> Commands = new List<byte[]>();

        private readonly List<byte> _open = new List<byte>();
        private bool _isOpen;
        private int _openExtra;
        private int _appended;

        public bool IsCommandOpen => _isOpen;

        public int PendingBytes => _open.Count;

        public int Begin(ReadOnlySpan<byte> data, uint cbExtra)
        {
            if (_isOpen) return HResult.E_UNEXPECTED;
            if (data.Length == 0) return HResult.E_INVALIDARG;

            _open.Clear();
            _open.AddRange(data.ToArray());
            _openExtra = (int)cbExtra;
            _appended = 0;
            _isOpen = true;
            return HResult.S_OK;
        }

        public int Append(ReadOnlySpan<byte> data)
        {
            if (!_isOpen) return HResult.E_UNEXPECTED;
            if (data.Length == 0) return HResult.E_INVALIDARG;
            if (_appended + data.Length > _openExtra) return HResult.E_INVALIDARG;

            _open.AddRange(data.ToArray());
            _appended += data.Length;
            return HResult.S_OK;
        }

        public int End()
        {
            if (!_isOpen) return HResult.E_UNEXPECTED;

            Commands.Add(_open.ToArray());
            _open.Clear();
            _isOpen = false;
            _openExtra = 0;
            _appended = 0;
            return HResult.S_OK;
        }

        /// <summary>直接投递一条完整命令（MilGlyphRun_SetGeometryAtRenderTime 用）。</summary>
        public int Send(ReadOnlySpan<byte> command)
        {
            if (_isOpen) return HResult.E_UNEXPECTED;
            if (command.Length == 0) return HResult.E_INVALIDARG;

            Commands.Add(command.ToArray());
            return HResult.S_OK;
        }
    }

    /// <summary>渲染时目标注册表。</summary>
    public static class MilRenderTimeTargetTable
    {
        private static readonly ConcurrentDictionary<IntPtr, MilRenderTimeTarget> _targets =
            new ConcurrentDictionary<IntPtr, MilRenderTimeTarget>();

        public static MilRenderTimeTarget Register(MilDeviceObjectKind kindHint = MilDeviceObjectKind.Unknown)
        {
            var target = new MilRenderTimeTarget
            {
                Handle = MilHandleSource.Next(),
                KindHint = kindHint,
            };
            _targets[target.Handle] = target;
            return target;
        }

        public static MilRenderTimeTarget Resolve(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return null;
            _targets.TryGetValue(handle, out MilRenderTimeTarget target);
            return target;
        }

        public static bool Unregister(IntPtr handle) =>
            handle != IntPtr.Zero && _targets.TryRemove(handle, out _);

        public static int Count => _targets.Count;

        public static void Reset() => _targets.Clear();
    }

    // ==================================================================
    //  反向 P/Invoke 包装
    // ==================================================================

    /// <summary>AOT iStream 写入句柄：MilCreateStreamFromStreamDescriptor 的产物。</summary>
    public sealed class MilStreamObject : IDisposable
    {
        /// <summary>
        /// **本工程持有**的描述符副本的地址（不是调用方传进来的那个地址！）。
        ///
        /// 上游 `exports.cpp:873` 的 `new CManagedStreamWrapper(*pSD)` 就是按值拷贝；
        /// 回调一律收到 `&m_sd`（wrapper 自己那份）。理由与实测见
        /// `MilNative.MILCreateStreamFromStreamDescriptor` 的注释。
        /// </summary>
        public IntPtr Descriptor;

        /// <summary>这份副本是否由本对象 malloc（true 才需要 free）。</summary>
        public bool DescriptorOwned;

        /// <summary>
        /// 上游 `StreamDescriptor.pfnWrite`（**托管委托**，由 CLR 在跨边界时转成调用 thunk）。
        ///
        /// 【为什么在 Create 时就把它读出来存着，而不是每次写的时候再去读描述符】
        ///   `MILCreateStreamFromStreamDescriptor(ref StreamDescriptor pSD, …)` 的 `ref` 在
        ///   原生侧拿到的是**该结构的一份临时本地副本**的地址 —— 那个地址只在这次调用期间有效。
        ///   而**函数指针本身**在托管委托存活期间一直有效（CLR 的 delegate→thunk 只在委托被回收时释放，
        ///   上游 `StreamAsIStream` 把结构体当字段持有、并在 Dispose 里才 Free GCHandle，
        ///   所以指针的寿命由它就够了）。⇒ Create 时取值、之后一直用值。
        ///
        /// 【偏移 32 怎么来的】上游 `StreamDescriptor`（StreamAsIStream.cs:14-59）14 个委托字段的顺序：
        ///   Dispose(0) Read(8) Seek(16) Stat(24) **Write(32)** CopyTo(40) SetSize(48) Commit(56)
        ///   Revert(64) LockRegion(72) UnlockRegion(80) Clone(88) CanWrite(96) CanSeek(104)，之后是
        ///   GCHandle(112) ⇒ 共 120。**不手抄**：`M7cMilStreamTests` 会去解析上游源码、机械核对这个顺序与偏移。
        /// </summary>
        public IntPtr WriteCallback;

        /// <summary>写进去的字节（内存流）。上游这里是 IStream + 一组回调委托。</summary>
        public readonly MemoryStream Data = new MemoryStream();

        public long BytesWritten;

        /// <summary>**真正交付给调用方**（经 pfnWrite）的字节数 —— 与 BytesWritten（台账）分开计。</summary>
        public long BytesDelivered;

        /// <summary>
        /// 释放描述符副本（`MilDeviceObjectTable.Release` 走到引用计数 0 时调用）。
        /// 顺序：先清空回调指针（此后任何一次误用都变成"没有可交付对象"而不是野指针调用），
        /// 再 free 内存。
        /// </summary>
        public void Dispose()
        {
            WriteCallback = IntPtr.Zero;
            if (DescriptorOwned && Descriptor != IntPtr.Zero)
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(Descriptor);
            }
            Descriptor = IntPtr.Zero;
            DescriptorOwned = false;
        }
    }

    /// <summary>
    /// CWIC wrapper（**物化**出来的位图）的在册登记（#24）。
    ///
    /// 【为什么要有这张表】物化路径（WIC 句柄 → 我们自己持有的 `SKBitmap`）最怕的是**泄漏**，
    ///   而泄漏**不会以崩溃的形式暴露** —— 所以必须有"在册几条"的可读读数，
    ///   才能给出"登记 N 次 → 释放 N 次 → 回基线"的证据。
    ///
    /// 【它不参与引用计数】引用计数仍在 `MilDeviceObjectTable`（wrapper 是 `CwicWrapperBitmap`
    ///   设备对象）；本表只记"哪些 wrapper 还活着"以及它物化用的位图句柄，供拆除时回收。
    /// </summary>
    public static class MilCwicWrapperTable
    {
        private sealed class Entry
        {
            public SKBitmap Bitmap;
            public IntPtr PixelBufferHandle;
        }

        private static readonly ConcurrentDictionary<IntPtr, Entry> _entries =
            new ConcurrentDictionary<IntPtr, Entry>();

        /// <summary>登记一次物化（wrapper 句柄 → 位图）。</summary>
        public static void Register(IntPtr wrapperHandle, SKBitmap bitmap, IntPtr pixelBufferHandle)
        {
            _entries[wrapperHandle] = new Entry { Bitmap = bitmap, PixelBufferHandle = pixelBufferHandle };
            Interlocked.Increment(ref _totalRegistrations);
        }

        /// <summary>取物化用的位图（诊断/断言用）。</summary>
        public static SKBitmap BitmapOf(IntPtr wrapperHandle)
            => _entries.TryGetValue(wrapperHandle, out Entry e) ? e.Bitmap : null;

        /// <summary>
        /// 回收一条（**释放物化位图**）。返回 false 表示这个句柄不在册（重复回收/未知句柄）。
        /// </summary>
        public static bool Release(IntPtr wrapperHandle)
        {
            if (!_entries.TryRemove(wrapperHandle, out Entry e)) return false;

            if (e.PixelBufferHandle != IntPtr.Zero) MilPixelBufferTable.Unregister(e.PixelBufferHandle, force: true);
            try { e.Bitmap?.Dispose(); } catch { /* 拆除期失败不改语义 */ }
            Interlocked.Increment(ref _totalReleases);
            return true;
        }

        private static long _totalRegistrations;
        private static long _totalReleases;

        /// <summary>当前在册条数（回基线判据）。</summary>
        public static int InFlight => _entries.Count;

        /// <summary>累计登记次数。</summary>
        public static long TotalRegistrations => Interlocked.Read(ref _totalRegistrations);

        /// <summary>累计回收次数。</summary>
        public static long TotalReleases => Interlocked.Read(ref _totalReleases);

        public static void Reset()
        {
            foreach (IntPtr key in new List<IntPtr>(_entries.Keys)) Release(key);
            Interlocked.Exchange(ref _totalRegistrations, 0);
            Interlocked.Exchange(ref _totalReleases, 0);
        }
    }

    /// <summary>
    /// 后台缓冲句柄 → MIL 设备对象 的**别名表**（M7c 轨道 B）。
    ///
    /// 【要解决的问题】`WriteableBitmap.AcquireBackBuffer` 拿到的是
    ///   `MILSwDoubleBufferedBitmapGetBackBuffer` 交出的**像素缓冲令牌**
    ///   （`MilPixelBufferTable` 下发），随后 `set_WicSourceHandle`（BitmapSource.cs:584）
    ///   对它调 `MILQueryInterface(handle, IID_IWICBitmapSource)` —— 修前：
    ///   它既不在 `MilDeviceObjectTable`、也不被 WIC shim 认领 ⇒ **E_HANDLE**（实测栈就是这样）。
    ///
    /// 【为什么用别名而不是换句柄】那个令牌**已经被别处引用**：`MilPixelBufferTable` 用它取回
    ///   `SKBitmap`（WIC 生产面、Skia 渲染面都用），换值会把它们全打断。所以保留令牌，
    ///   额外把它**别名**到一个 MIL 设备对象上：
    ///     · `MILQueryInterface` 认这个令牌 ⇒ 答 `IID_IUnknown` / `IID_IWICBitmapSource`；
    ///     · **AddRef/Release 走 MIL 设备对象表原有的那条账**（与主控的约束一致：
    ///       不能让 MIL 自己创建的句柄落进 `WicShim_*` 那条外部句柄路径，否则两边账本单边）。
    ///
    /// 【与外部句柄路径的关系】互不干扰：`MilExternalHandleBridge` 只在
    ///   `MilDeviceObjectTable.Resolve` **查不到**且所有者认领时才接手；
    ///   这里查得到（走别名）⇒ 外部那条路径一行未改。
    /// </summary>
    public static class MilBackBufferSourceTable
    {
        private static readonly ConcurrentDictionary<IntPtr, IntPtr> _alias =
            new ConcurrentDictionary<IntPtr, IntPtr>();

        /// <summary>把像素缓冲令牌别名为一个 MIL 设备对象。重复登记幂等（返回既有别名）。</summary>
        public static IntPtr Alias(IntPtr token, IntPtr deviceHandle)
        {
            _alias[token] = deviceHandle;
            return deviceHandle;
        }

        /// <summary>令牌 → MIL 设备对象句柄；没有别名返回 Zero。</summary>
        public static IntPtr ResolveDevice(IntPtr token)
            => _alias.TryGetValue(token, out IntPtr dev) ? dev : IntPtr.Zero;

        /// <summary>摘除别名（设备对象被释放时调用）。</summary>
        public static void Remove(IntPtr token) => _alias.TryRemove(token, out _);

        /// <summary>
        /// 主对象（swdbb）析构时调用：摘别名，并把**仍未被 QI 过**的别名设备对象一并释放掉。
        /// 不这样做的话，凡是没有走过 QI 的双缓冲位图都会在设备对象表里留一条悬挂条目
        /// （表只增不减 = 泄漏），这正是 T1 的 G6/G6b 要抓的那类问题。
        /// 如果 PC 还持有 QI 得到的引用，这里释放的只是"主对象那一根"，
        /// 后续那次 Release 会按正常语义拿到 E_HANDLE（对象已随主对象一起消失）。
        /// </summary>
        public static void Forget(IntPtr token)
        {
            if (token == IntPtr.Zero) return;
            if (!_alias.TryGetValue(token, out IntPtr dev)) return;

            if (MilDeviceObjectTable.Resolve(dev) != null)
            {
                // 这一根是"主对象持有的那根"，与调用方 QI 出来的那根是两回事：
                // 还有别人的引用（例如 PC 手上那次 QI 尚未释放）时**不摘别名**，
                // 让那次迟到的 Release 仍能正常把它减到 0（COM 语义：对象随最后一根引用而亡）。
                long remaining = MilDeviceObjectTable.Release(dev);
                if (remaining > 0) return;
            }
            _alias.TryRemove(token, out _);
        }

        public static int Count => _alias.Count;

        public static void Reset() => _alias.Clear();
    }

    /// <summary>反向 P/Invoke 包装注册表。</summary>
    public static class MilReversePInvokeTable
    {
        private static readonly ConcurrentDictionary<IntPtr, int> _wrappers = new ConcurrentDictionary<IntPtr, int>();

        /// <summary>登记一个函数指针，返回包装句柄（Linux 上二者同值：两侧都是托管）。</summary>
        public static IntPtr Create(IntPtr pFcn)
        {
            if (pFcn == IntPtr.Zero) return IntPtr.Zero;

            _wrappers.AddOrUpdate(pFcn, 1, (_, n) => n + 1);
            return pFcn;
        }

        /// <summary>释放一个包装。未知句柄返回 false。</summary>
        public static bool Release(IntPtr wrapper)
        {
            if (wrapper == IntPtr.Zero) return false;

            while (true)
            {
                if (!_wrappers.TryGetValue(wrapper, out int n)) return false;
                if (n <= 1) return _wrappers.TryRemove(wrapper, out _);
                if (_wrappers.TryUpdate(wrapper, n - 1, n)) return true;
            }
        }

        public static int Count => _wrappers.Count;

        public static void Reset() => _wrappers.Clear();
    }

    // ==================================================================
    //  合成引擎状态：锁 / 分区管理器 / 进程级开关
    // ==================================================================

    /// <summary>
    /// 合成引擎锁。
    ///
    /// 【上游语义】MilCompositionEngine_EnterCompositionEngineLock 就是
    ///   <c>g_csCompositionEngine.Enter()</c>——一个 Win32 CRITICAL_SECTION，
    ///   **可重入**（同一线程 Enter 两次要 Leave 两次）。所以这里不能用
    ///   SemaphoreSlim（不可重入），要用 Monitor（可重入），并自己数重入深度，
    ///   好在测试里断言"重入后深度为 2、退出后回到 1"。
    ///
    /// 【为什么不需要跨进程/跨 native】本工程的"原生层"就是本程序集，
    ///   锁只在这个进程里起作用。
    /// </summary>
    public static class MilCompositionEngineState
    {
        private static readonly object _compositionLock = new object();
        private static readonly object _mediaSystemLock = new object();

        private static int _compositionDepth;
        private static int _mediaSystemDepth;
        private static int _compositionOwner;

        private static readonly object _initGate = new object();
        private static bool _partitionManagerInitialized;
        private static int _partitionManagerPriority;
        private static int _initializeCount;
        private static int _deinitializeCount;

        private static int _forceSoftwareRendering;
        private static int _hardwareAccelerationInRdp;
        private static int _disableBoundsCheckProtection;

        private static long _perfElementId;

        // ---------------- 合成引擎锁 ----------------

        public static void EnterCompositionEngineLock()
        {
            Monitor.Enter(_compositionLock);
            _compositionDepth++;
            _compositionOwner = Environment.CurrentManagedThreadId;
        }

        public static void ExitCompositionEngineLock()
        {
            if (_compositionDepth <= 0)
            {
                // 上游对未持有的 CRITICAL_SECTION 调 Leave 是未定义行为；
                // 这里明确抛异常，让调用方的配对错误立刻暴露，而不是悄悄留下坏状态。
                throw new InvalidOperationException(
                    "MilCompositionEngine_ExitCompositionEngineLock 被调用，但当前线程未持有合成引擎锁");
            }

            _compositionDepth--;
            Monitor.Exit(_compositionLock);
        }

        public static int CompositionLockDepth => _compositionDepth;

        public static bool IsCompositionLockHeld => _compositionDepth > 0;

        /// <summary>当前持锁线程 id（未持锁时为 0）。</summary>
        public static int CompositionLockOwner => _compositionDepth > 0 ? _compositionOwner : 0;

        // ---------------- 媒体系统锁 ----------------

        public static void EnterMediaSystemLock()
        {
            Monitor.Enter(_mediaSystemLock);
            _mediaSystemDepth++;
        }

        public static void ExitMediaSystemLock()
        {
            if (_mediaSystemDepth <= 0)
            {
                throw new InvalidOperationException(
                    "MilCompositionEngine_ExitMediaSystemLock 被调用，但当前线程未持有媒体系统锁");
            }

            _mediaSystemDepth--;
            Monitor.Exit(_mediaSystemLock);
        }

        public static int MediaSystemLockDepth => _mediaSystemDepth;

        // ---------------- 分区管理器 ----------------

        /// <summary>
        /// 上游 EnsurePartitionManager(nPriority)：建调度器与一组工作线程。
        /// Linux/M1 的合成是同步的（无独立合成线程），所以这里只做"管理器已就绪"的
        /// 登记 + 优先级记录，不创建线程。返回 S_OK（重复初始化幂等）。
        /// </summary>
        public static int InitializePartitionManager(int priority)
        {
            lock (_initGate)
            {
                _partitionManagerInitialized = true;
                _partitionManagerPriority = priority;
                _initializeCount++;
                return HResult.S_OK;
            }
        }

        /// <summary>上游 ReleasePartitionManager() + 恒返回 S_OK（apifunc.cpp:110）。</summary>
        public static int DeinitializePartitionManager()
        {
            lock (_initGate)
            {
                _partitionManagerInitialized = false;
                _deinitializeCount++;
                return HResult.S_OK;
            }
        }

        public static bool IsPartitionManagerInitialized => _partitionManagerInitialized;

        public static int PartitionManagerPriority => _partitionManagerPriority;

        public static int InitializeCount => _initializeCount;

        public static int DeinitializeCount => _deinitializeCount;

        // ---------------- 进程级渲染开关 ----------------

        public static bool ForceSoftwareRendering
        {
            get => Volatile.Read(ref _forceSoftwareRendering) != 0;
            set => Volatile.Write(ref _forceSoftwareRendering, value ? 1 : 0);
        }

        public static bool EnableHardwareAccelerationInRdp
        {
            get => Volatile.Read(ref _hardwareAccelerationInRdp) != 0;
            set => Volatile.Write(ref _hardwareAccelerationInRdp, value ? 1 : 0);
        }

        public static bool DisableBoundsCheckProtection
        {
            get => Volatile.Read(ref _disableBoundsCheckProtection) != 0;
            set => Volatile.Write(ref _disableBoundsCheckProtection, value ? 1 : 0);
        }

        /// <summary>GetNextPerfElementId：进程内单调计数器（上游也是单调下发）。</summary>
        public static long NextPerfElementId() => Interlocked.Increment(ref _perfElementId);

        private static int _systemParametersUpdateCount;

        /// <summary>MILUpdateSystemParametersInfo 被调用的次数（Linux 上没有参数可更新）。</summary>
        public static int SystemParametersUpdateCount => _systemParametersUpdateCount;

        public static void NotifySystemParametersUpdate() =>
            Interlocked.Increment(ref _systemParametersUpdateCount);

        /// <summary>测试用：恢复全部进程级状态。</summary>
        public static void Reset()
        {
            ForceSoftwareRendering = false;
            EnableHardwareAccelerationInRdp = false;
            DisableBoundsCheckProtection = false;

            lock (_initGate)
            {
                _partitionManagerInitialized = false;
                _partitionManagerPriority = 0;
                _initializeCount = 0;
                _deinitializeCount = 0;
            }

            Interlocked.Exchange(ref _perfElementId, 0);
            Interlocked.Exchange(ref _systemParametersUpdateCount, 0);
        }
    }
}
