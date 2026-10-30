// Licensed to the .NET Foundation under one or more agreements.
//
// M7a · 分组 D：离屏位图 / 渲染目标 / 工厂 / D3D 互操作 / WIC 色彩上下文（18 个）。
//
// 【语义落点】
//   MILSwDoubleBufferedBitmap*  → 双缓冲 SKBitmap（前台 + 后台）+ MilPixelBufferTable 令牌
//   MILRenderTargetBitmap*      → 离屏 SKBitmap 渲染目标 + 同上令牌协议
//   MILCreateFactory / MILFactoryCreate* → 引用计数对象表（MilDeviceObjectTable）
//   InteropDeviceBitmap_*       → **E_NOTIMPL**（Linux 无 D3D11 共享纹理；见下）
//   IWICColorContext_*_Proxy    → MilColorContextTable（真实存储 ICC profile 字节）
//
// 【为什么 D3D 互操作是 E_NOTIMPL 而不是硬做】
//   D3DImage 的整条链路（InteropDeviceBitmap + D3D11 纹理共享 + DWM 合成）在 Linux
//   上没有对等物：没有 D3D11，没有共享句柄语义，也没有 DWM。硬返回 S_OK 会让
//   D3DImage 进入"以为有纹理"的状态，之后每一帧都画错——比明确失败更难查。
//   所以统一返回 E_NOTIMPL，并在 docs/unimplemented.md 建议条目里登记。
//
// 【WIC 的边界】
//   位图**解码**由 M1 的 Skia 栈承接；这里只补 MIL 导出的那一小块 WIC 面：
//   CWICWrapperBitmap 是"把 IWICBitmapSource 包一层"的引用计数包装，
//   ColorContext 是 profile 字节容器。两者都落在进程内句柄表上，
//   不 import WindowsCodecs。

using System;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace WpfGfx.Linux.Interop
{
    /// <summary>
    /// 本组用到的 WindowsCodecs / WGX 错误码。
    /// 单独定义而不是加进 HResult.cs：HResult.cs 是既有文件，M7a 的改动边界不允许改它。
    /// 取值来自上游 WpfGfx/include/wgx_error.cs:24-52。
    /// </summary>
    public static class MilErrors
    {
        /// <summary>E_ACCESSDENIED：MilVisualTarget_AttachToHwnd 的"窗口已被占用"码。</summary>
        public const int E_ACCESSDENIED = unchecked((int)0x80070005);

        public const int WINCODEC_ERR_UNSUPPORTEDVERSION = unchecked((int)0x88982F0B);
        public const int WINCODEC_ERR_WRONGSTATE = unchecked((int)0x88982F04);
        public const int WINCODEC_ERR_VALUEOUTOFRANGE = unchecked((int)0x88982F05);
        public const int WINCODEC_ERR_UNSUPPORTEDPIXELFORMAT = unchecked((int)0x88982F80);
        public const int WINCODEC_ERR_INSUFFICIENTBUFFER = unchecked((int)0x88982F8C);

        /// <summary>WGXERR_UNSUPPORTEDVERSION === WINCODEC_ERR_UNSUPPORTEDVERSION。</summary>
        public const int WGXERR_UNSUPPORTEDVERSION = WINCODEC_ERR_UNSUPPORTEDVERSION;

        /// <summary>MilVersionCheck 比对用的 MIL SDK 版本（上游 wgx_sdk_version.h:17）。</summary>
        public const uint MilSdkVersion = 0x200184C0;
    }

    /// <summary>WIC 像素格式 GUID（上游 Common/Graphics/wgx_exports.cs:200）。</summary>
    public static class MilPixelFormats
    {
        public static readonly Guid DontCare =
            new Guid(0x6fddc324, 0x4e03, 0x4bfe, 0xb1, 0x85, 0x3d, 0x77, 0x76, 0x8d, 0xc9, 0x00);
        public static readonly Guid Indexed8bpp =
            new Guid(0x6fddc324, 0x4e03, 0x4bfe, 0xb1, 0x85, 0x3d, 0x77, 0x76, 0x8d, 0xc9, 0x04);
        public static readonly Guid BlackWhite =
            new Guid(0x6fddc324, 0x4e03, 0x4bfe, 0xb1, 0x85, 0x3d, 0x77, 0x76, 0x8d, 0xc9, 0x05);
        public static readonly Guid Gray8bpp =
            new Guid(0x6fddc324, 0x4e03, 0x4bfe, 0xb1, 0x85, 0x3d, 0x77, 0x76, 0x8d, 0xc9, 0x08);
        public static readonly Guid Bgr24 =
            new Guid(0x6fddc324, 0x4e03, 0x4bfe, 0xb1, 0x85, 0x3d, 0x77, 0x76, 0x8d, 0xc9, 0x0c);
        public static readonly Guid Rgb24 =
            new Guid(0x6fddc324, 0x4e03, 0x4bfe, 0xb1, 0x85, 0x3d, 0x77, 0x76, 0x8d, 0xc9, 0x0d);
        public static readonly Guid Bgr32 =
            new Guid(0x6fddc324, 0x4e03, 0x4bfe, 0xb1, 0x85, 0x3d, 0x77, 0x76, 0x8d, 0xc9, 0x0e);
        public static readonly Guid Bgra32 =
            new Guid(0x6fddc324, 0x4e03, 0x4bfe, 0xb1, 0x85, 0x3d, 0x77, 0x76, 0x8d, 0xc9, 0x0f);
        public static readonly Guid Pbgra32 =
            new Guid(0x6fddc324, 0x4e03, 0x4bfe, 0xb1, 0x85, 0x3d, 0x77, 0x76, 0x8d, 0xc9, 0x10);
        public static readonly Guid Gray32Float =
            new Guid(0x6fddc324, 0x4e03, 0x4bfe, 0xb1, 0x85, 0x3d, 0x77, 0x76, 0x8d, 0xc9, 0x11);
        public static readonly Guid Rgba64 =
            new Guid(0x6fddc324, 0x4e03, 0x4bfe, 0xb1, 0x85, 0x3d, 0x77, 0x76, 0x8d, 0xc9, 0x16);
        public static readonly Guid Prgba64 =
            new Guid(0x6fddc324, 0x4e03, 0x4bfe, 0xb1, 0x85, 0x3d, 0x77, 0x76, 0x8d, 0xc9, 0x17);
        public static readonly Guid Rgba128Float =
            new Guid(0x6fddc324, 0x4e03, 0x4bfe, 0xb1, 0x85, 0x3d, 0x77, 0x76, 0x8d, 0xc9, 0x19);
        public static readonly Guid Prgba128Float =
            new Guid(0x6fddc324, 0x4e03, 0x4bfe, 0xb1, 0x85, 0x3d, 0x77, 0x76, 0x8d, 0xc9, 0x1a);
    }
}

namespace WpfGfx.Linux.Interop
{
    /// <summary>
    /// 双缓冲位图的状态（MILSwDoubleBufferedBitmap* 的作用对象）。
    ///
    /// 【双缓冲的语义】WriteableBitmap 只有一个"后台缓冲"，渲染线程持有一个
    ///   "前台缓冲"；两者在 AcquireBackBuffer/ReleaseBackBuffer 之间交换。
    ///   这里用两张 SKBitmap 表达，并给出 <see cref="Swap"/> 让上层在提交时换。
    /// </summary>
    public sealed class MilDoubleBufferedState : IDisposable
    {
        public int Width;
        public int Height;
        public double DpiX = 96.0;
        public double DpiY = 96.0;
        public Guid PixelFormatGuid;

        /// <summary>前台缓冲（渲染线程读到的那一份）。</summary>
        public SKBitmap Front;

        /// <summary>后台缓冲（调用方写入的那一份）。</summary>
        public SKBitmap Back;

        /// <summary>后台缓冲在 MilPixelBufferTable 里的句柄。</summary>
        public IntPtr BackBufferHandle;

        public int DirtyRectCount;
        public MilInt32Rect? LastDirtyRect;

        /// <summary>
        /// ProtectBackBuffer 之后置位：后台缓冲不再被前台覆盖
        /// （WriteableBitmap.FreezeCore 把它当成普通 BitmapSource 的场景）。
        /// </summary>
        public bool BackBufferProtected;

        /// <summary>ProtectBackBuffer 的调用次数（幂等性可见）。</summary>
        public int ProtectCount;

        /// <summary>前后台交换次数。</summary>
        public int SwapCount;

        /// <summary>后台缓冲的字节数（stride × height）。</summary>
        public uint BackBufferSize => (uint)(Back.RowBytes * Back.Height);

        /// <summary>把后台缓冲提升为前台。Protected 之后不再交换（冻结语义）。</summary>
        public bool Swap()
        {
            if (BackBufferProtected) return false;

            SKBitmap old = Front;
            Front = Back;
            Back = old;
            SwapCount++;
            return true;
        }

        public void Dispose()
        {
            // 【顺序】① 先注销 shim 的外来源登记 → ② 摘别名设备对象 → ③ 摘位图句柄 → ④ 释放 SKBitmap。
            //   ① 必须在 ④ **之前**：将来若真把 `pixels` 借给 shim，shim 手上就是一块会消失的内存；
            //   第一版虽不借像素，这个顺序也不能写反（T2 规格里点名的）。
            if (BackBufferHandle != IntPtr.Zero)
            {
                int unregHr = MilExternalHandleBridge.UnregisterForeignSource(BackBufferHandle);
                if (HResult.Failed(unregHr))
                    MilDiagnostics.Note(
                        $"SwDoubleBufferedBitmap.Dispose: 注销 WIC 外来源未生效（hr=0x{unregHr:x8}）" +
                        "（旧 shim 无此导出 / 从未登记过 —— 都属预期，不影响拆除）");
            }

            // 再把后台缓冲的别名设备对象收掉，然后释放位图：
            // 顺序反了的话，设备对象表里会留下指向已释放 SKBitmap 的条目。
            MilBackBufferSourceTable.Forget(BackBufferHandle);

            // 句柄本身也要从 MilPixelBufferTable 里摘掉（借用登记：只摘句柄，不 Dispose 位图）。
            // 修前这里什么都不做 ⇒ **每个双缓冲位图的生命周期都在位图表里留一条**，
            // 表只增不减。实测见 M7cBackBufferRefCountTests 的高水位断言。
            MilPixelBufferTable.Unregister(BackBufferHandle, force: true);
            BackBufferHandle = IntPtr.Zero;

            Front?.Dispose();
            Back?.Dispose();
            Front = null;
            Back = null;
        }
    }

    public static unsafe partial class MilNative
    {
        // ==================================================================
        //  MILSwDoubleBufferedBitmap_*
        // ==================================================================

        /// <summary>
        /// 建一个软件双缓冲位图。pixelFormatGuid 是 in-out：传 DontCare（全 0 GUID）
        /// 时选 Pbgra32 并回填；不认识的 GUID → WINCODEC_ERR_UNSUPPORTEDPIXELFORMAT。
        /// </summary>
        public static int MILSwDoubleBufferedBitmapCreate(
            uint width,
            uint height,
            double dpiX,
            double dpiY,
            ref Guid pixelFormatGuid,
            IntPtr pPalette,
            out IntPtr ppSwDoubleBufferedBitmap)
        {
            ppSwDoubleBufferedBitmap = IntPtr.Zero;

            if (width == 0 || height == 0) return HResult.E_INVALIDARG;
            if (dpiX <= 0 || dpiY <= 0) return HResult.E_INVALIDARG;

            SKColorType colorType;
            SKAlphaType alphaType;

            if (pixelFormatGuid == Guid.Empty || pixelFormatGuid == MilPixelFormats.DontCare)
            {
                pixelFormatGuid = MilPixelFormats.Pbgra32;
                colorType = SKColorType.Bgra8888;
                alphaType = SKAlphaType.Premul;
            }
            else if (pixelFormatGuid == MilPixelFormats.Pbgra32)
            {
                colorType = SKColorType.Bgra8888;
                alphaType = SKAlphaType.Premul;
            }
            else if (pixelFormatGuid == MilPixelFormats.Bgra32)
            {
                colorType = SKColorType.Bgra8888;
                alphaType = SKAlphaType.Unpremul;
            }
            else if (pixelFormatGuid == MilPixelFormats.Bgr32)
            {
                colorType = SKColorType.Bgra8888;
                alphaType = SKAlphaType.Opaque;
            }
            else if (pixelFormatGuid == MilPixelFormats.Rgb24 || pixelFormatGuid == MilPixelFormats.Bgr24)
            {
                colorType = SKColorType.Rgb888x;
                alphaType = SKAlphaType.Opaque;
            }
            else if (pixelFormatGuid == MilPixelFormats.Gray8bpp)
            {
                colorType = SKColorType.Gray8;
                alphaType = SKAlphaType.Opaque;
            }
            else
            {
                // 调色板格式（Indexed*/BlackWhite）需要 colormap，M1 不做；未知 GUID 一律拒绝。
                // 上游这个位置返回 WINCODEC_ERR_UNSUPPORTEDPIXELFORMAT（exports.cpp:Create）。
                _ = pPalette;
                return MilErrors.WINCODEC_ERR_UNSUPPORTEDPIXELFORMAT;
            }

            var info = new SKImageInfo((int)width, (int)height, colorType, alphaType);
            var front = new SKBitmap(info);
            var back = new SKBitmap(info);
            if (front.IsNull || back.IsNull)
            {
                front.Dispose();
                back.Dispose();
                return HResult.E_OUTOFMEMORY;
            }

            var state = new MilDoubleBufferedState
            {
                Width = (int)width,
                Height = (int)height,
                DpiX = dpiX,
                DpiY = dpiY,
                PixelFormatGuid = pixelFormatGuid,
                Front = front,
                Back = back,
            };

            // 后台缓冲登记进位图表（**不接管所有权**：state 仍然持有并负责释放，
            // 表只借用同一实例，因此这里用 RegisterBorrowed 语义——通过 AddRef 计数
            // 与 state.Dispose 的配对在 DeviceObjectTable.Release 里完成）。
            state.BackBufferHandle = MilPixelBufferTable.RegisterBorrowed(back, "SwDoubleBufferedBitmap.Back");

            MilDeviceObject obj = MilDeviceObjectTable.Register(
                MilDeviceObjectKind.SwDoubleBufferedBitmap, state,
                $"swdbb {width}x{height} {pixelFormatGuid}");

            // 后台缓冲**同时**登记成一个可被查询为 IWICBitmapSource 的设备对象，
            // 并把像素缓冲令牌**别名**到它 —— `WriteableBitmap.AcquireBackBuffer` 之后
            // `set_WicSourceHandle` 那句 `MILQueryInterface(backBuffer, IID_IWICBitmapSource)`
            // 修前返回 E_HANDLE，就是因为这个句柄在 MIL 账本里根本不存在。详见
            // MilBackBufferSourceTable 的类注释。
            // 负载是**视图**而非 state 本身：这个设备对象不拥有 SKBitmap，
            // 详见 MilBackBufferView 的类注释（引用计数归零时不得释放双缓冲的位图）。
            MilDeviceObject backObj = MilDeviceObjectTable.Register(
                MilDeviceObjectKind.WicBitmapSource,
                new MilBackBufferView(state, state.BackBufferHandle),
                $"swdbb back buffer {width}x{height}");
            MilBackBufferSourceTable.Alias(state.BackBufferHandle, backObj.Handle);

            // ── T2 交办的接线（派发，不动记账）──────────────────────────────
            // 【为什么】上游 WIC proxy 是"转发 vtable 调用"，我们的 shim 是"表查找" ⇒
            //   被当成 `IWICBitmapSource` 交出去的 MIL 句柄，**proxy 层也得能派发**。
            //   挡住的调用实例（实测）：`BitmapSource.UpdateCachedSettings`
            //   → `IWICBitmapSource_GetPixelFormat_Proxy(后缓冲句柄)` → 修前 E_INVALIDARG
            //   （PC 侧抛 `ArgumentException`，`WriteableBitmap` 就在这一步死）。
            // 【登记的是哪个句柄】= `state.BackBufferHandle`（就是交给 PC 的那个令牌）。
            // 【isOpaque 的近似，照实写】**只认 Bgr32** 这一种不透明格式；
            //   其它格式一律按"非不透明"报（Pbgra32/Bgra32 本来就带 alpha）。这是 T2 规格里
            //   明确的近似，不是漏判 —— 真要按每格式精确判，得先把各格式的 alphaType 语义对齐。
            // 【pixels/rowBytes】恒 NULL/0：第一版**不接借用像素**（只需要元数据）；
            //   真调 CopyPixels 时 shim 会诚实返回 WINCODEC_ERR_UNSUPPORTEDOPERATION。
            // 【取不到导出】只跳过（旧 shim / 没装 WIC）⇒ 行为与接线前逐字一致。
            {
                Guid foreignFormat = pixelFormatGuid;
                bool isOpaque = foreignFormat == MilPixelFormats.Bgr32;

                // ── v2：把**这块位图的像素借给 shim**（原先 pixels/rowBytes 传 NULL/0）──
                // 【为什么必须借而不是拷贝（语义，不是性能）】`WriteableBitmap` 的契约是
                //   "Lock 拿到的指针就是你写进去、渲染能看见的那块内存" ⇒ **拷贝一份给上层会让写穿透失效**，
                //   那是功能坏掉，不是慢一点。
                // 【借用的代价（下一个人改这里必须知道）】
                //   `pixels` 指向的 `back`（`state.Back`）在**注销之前必须一直有效**：
                //     · 别在别处 Realloc / 重建 / 换掉这块 SKBitmap 而不重新登记；
                //     · 拆除顺序必须是 **先 `WicShim_UnregisterForeignSource(handle)`，再释放 SKBitmap**
                //       —— 见 `MilDoubleBufferedState.Dispose()`（那条顺序现在从"格式正确"
                //       升级成"生命周期正确"：反了就是**野内存**）。
                IntPtr pixels = back.GetPixels();
                uint rowBytes = (uint)back.RowBytes;

                int regHr = MilExternalHandleBridge.RegisterForeignSource(
                    state.BackBufferHandle, ref foreignFormat, width, height, isOpaque,
                    pixels, rowBytes);
                if (HResult.Succeeded(regHr))
                    MilDiagnostics.Note(
                        $"MILSwDoubleBufferedBitmapCreate: 后缓冲已登记为 WIC **外来源**（派发用）" +
                        $"handle=0x{(long)state.BackBufferHandle:x} {width}x{height} " +
                        $"format={foreignFormat} isOpaque={(isOpaque ? 1 : 0)}；" +
                        $"借用像素=0x{(long)pixels:x} rowBytes={rowBytes}（**注销前必须有效**）；" +
                        "记账未变（引用计数仍全在 MilDeviceObjectTable）");
                else
                    MilDiagnostics.Note(
                        $"MILSwDoubleBufferedBitmapCreate: WIC 外来源登记未生效（hr=0x{regHr:x8}）⇒ " +
                        "GetPixelFormat 仍会是 E_INVALIDARG（fail-safe，不影响其它行为）");
            }

            ppSwDoubleBufferedBitmap = obj.Handle;
            return HResult.S_OK;
        }

        /// <summary>
        /// 取后台缓冲的位图句柄与字节数。句柄进 MilPixelBufferTable，调用方按
        /// IWICBitmap 的用法读写它（句柄在 Unregister 之前一直有效）。
        /// 未找到对象时两个 out 参数都是 0/空（上游这个导出是 void，不报错）。
        /// </summary>
        public static void MILSwDoubleBufferedBitmapGetBackBuffer(
            IntPtr THIS_PTR,
            out IntPtr pBackBuffer,
            out uint pBackBufferSize)
        {
            pBackBuffer = IntPtr.Zero;
            pBackBufferSize = 0;

            MilDoubleBufferedState state = ResolveDoubleBuffered(THIS_PTR);
            if (state == null) return;

            pBackBuffer = state.BackBufferHandle;
            pBackBufferSize = state.BackBufferSize;
        }

        /// <summary>
        /// 记一条脏矩形。未找到对象时静默返回（上游同样是 void）。
        /// 脏矩形不裁剪到 [0,w)×[0,h)：上游把越界矩形原样记下来交给下游处理。
        /// </summary>
        public static void MILSwDoubleBufferedBitmapAddDirtyRect(
            IntPtr THIS_PTR,
            ref MilInt32Rect dirtyRect)
        {
            MilDoubleBufferedState state = ResolveDoubleBuffered(THIS_PTR);
            if (state == null) return;

            state.DirtyRectCount++;
            state.LastDirtyRect = dirtyRect;
        }

        /// <summary>
        /// 保护后台缓冲：此后不再发生前后台交换（对应
        /// WriteableBitmap.FreezeCore 里"转成普通 BitmapSource"的场景）。幂等。
        /// </summary>
        public static int MILSwDoubleBufferedBitmapProtectBackBuffer(IntPtr THIS_PTR)
        {
            MilDoubleBufferedState state = ResolveDoubleBuffered(THIS_PTR);
            if (state == null) return HResult.E_HANDLE;

            state.BackBufferProtected = true;
            state.ProtectCount++;
            return HResult.S_OK;
        }

        // ==================================================================
        //  MILRenderTargetBitmap*
        // ==================================================================

        /// <summary>
        /// 取渲染目标的位图（IWICBitmap 等价物）。句柄登记在 MilPixelBufferTable，
        /// 调用方拿到后即可读像素；重复调用返回**同一个**句柄（上游也是同一对象）。
        /// </summary>
        public static int MILRenderTargetBitmapGetBitmap(IntPtr THIS_PTR, out IntPtr ppIBitmap)
        {
            ppIBitmap = IntPtr.Zero;

            MilDeviceObject obj = MilDeviceObjectTable.Resolve(THIS_PTR);
            if (obj == null || obj.Kind != MilDeviceObjectKind.BitmapRenderTarget)
                return HResult.E_HANDLE;

            var target = (MilRenderTargetState)obj.Payload;
            if (target == null) return HResult.E_HANDLE;

            if (target.BitmapHandle == IntPtr.Zero && target.Bitmap != null)
            {
                target.BitmapHandle = MilPixelBufferTable.RegisterBorrowed(
                    target.Bitmap, "RenderTargetBitmap");

                // ── WIC 离屏渲染目标面（`PRECOND-WIC-RTB`）──────────────────────
                // 【为什么在这一跳补】上游 `RenderTargetBitmap.FinalizeCreation`
                //   （`RenderTargetBitmap.cs:256`）把本句柄赋给 `WicSourceHandle`，紧接着
                //   `BitmapSource.set_WicSourceHandle`（`BitmapSource.cs:584`）对它调
                //   `MILQueryInterface(handle, IID_IWICBitmapSource, out _)`；随后
                //   `UpdateCachedSettings()` 又会走 WIC 的三个只读 proxy。
                //   句柄此前只在 `MilPixelBufferTable` 里 ⇒ 设备对象表查不到、WIC 所有者不认领
                //   ⇒ `E_HANDLE`。这里照 `MILSwDoubleBufferedBitmapCreate` 的**同一套体例**补两件事：
                //     ① 别名到一个 `WicBitmapSource` 设备对象 ⇒ QI 走 MIL 自己那张账放行
                //        （见 `MilBackBufferSourceTable` 的类注释）；
                //     ② 登记成 shim 的 WIC **外来源** ⇒ `IWICBitmapSource_GetSize/GetPixelFormat/
                //        GetResolution`（以及借了像素的 `CopyPixels`）能在 proxy 层派发 ——
                //        只放行 QI 而不补这条，`UpdateCachedSettings()` 立刻在 proxy 层拿 E_INVALIDARG。
                // 【像素借用】沿用双缓冲那条的语义：`pixels` 指向 `target.Bitmap`，
                //   在 `MilRenderTargetState.Dispose` 注销之前必须一直有效（见那里的拆除顺序）。
                MilDeviceObject wicObj = MilDeviceObjectTable.Register(
                    MilDeviceObjectKind.WicBitmapSource,
                    new MilRenderTargetView(target, target.BitmapHandle),
                    $"rtb bitmap {target.Width}x{target.Height}");
                target.WicSourceDeviceHandle = wicObj.Handle;
                MilBackBufferSourceTable.Alias(target.BitmapHandle, wicObj.Handle);

                Guid foreignFormat = WicGuidForPixelFormat(target.PixelFormat);
                bool isOpaque = IsOpaquePixelFormat(target.PixelFormat);
                IntPtr pixels = target.Bitmap.GetPixels();
                uint rowBytes = (uint)target.Bitmap.RowBytes;

                int regHr = MilExternalHandleBridge.RegisterForeignSource(
                    target.BitmapHandle, ref foreignFormat, (uint)target.Width, (uint)target.Height,
                    isOpaque, pixels, rowBytes);
                if (HResult.Succeeded(regHr))
                    MilDiagnostics.Note(
                        $"MILRenderTargetBitmapGetBitmap: 位图 0x{(long)target.BitmapHandle:x} 已登记为 WIC **外来源**" +
                        $"（{target.Width}x{target.Height} format={foreignFormat} isOpaque={(isOpaque ? 1 : 0)}；" +
                        $"借用像素=0x{(long)pixels:x} rowBytes={rowBytes}（**注销前必须有效**）；" +
                        "记账未变（引用计数仍全在 MilDeviceObjectTable））");
                else
                    MilDiagnostics.Note(
                        $"MILRenderTargetBitmapGetBitmap: WIC 外来源登记未生效（hr=0x{regHr:x8}）⇒ " +
                        "proxy 层仍 E_INVALIDARG（fail-safe；QI 本身不受影响，语义与接线前一致）");
            }

            ppIBitmap = target.BitmapHandle;
            return HResult.S_OK;
        }

        /// <summary>
        /// `MilPixelFormatEnum` → WIC 像素格式 GUID。WIC 的像素格式 GUID 只有**最后一个字节**
        /// 承载格式号（基础 `{6fddc324-4e03-4bfe-b185-3d7776 8dc9 00}`，末字节 = 枚举值）；
        /// 上游 `PixelFormat.GetPixelFormat(Guid)`（`PixelFormat.cs:517-523`）正是按 `guidBytes[15]`
        /// 反查 ⇒ 两个方向互为证据。取值与 <see cref="MilPixelFormats"/> 的常量一致（如 `Pbgra32 = …0x10`）。
        /// </summary>
        private static Guid WicGuidForPixelFormat(MilPixelFormatEnum format) =>
            new Guid(0x6fddc324, 0x4e03, 0x4bfe, 0xb1, 0x85, 0x3d, 0x77, 0x76, 0x8d, 0xc9, (byte)format);

        /// <summary>
        /// 该像素格式是否不透明。与 <see cref="MILFactoryCreateBitmapRenderTarget"/> 里
        /// `alphaType` 的判定同源（不透明的那几种），供 shim 的 `isOpaque` 入参用。
        /// </summary>
        private static bool IsOpaquePixelFormat(MilPixelFormatEnum format) =>
            format is MilPixelFormatEnum.Rgb24 or MilPixelFormatEnum.Bgr24 or
                      MilPixelFormatEnum.Bgr32 or MilPixelFormatEnum.Gray8;

        /// <summary>把渲染目标清成透明黑（SKColors.Transparent），并复位脏矩形记录。</summary>
        public static int MILRenderTargetBitmapClear(IntPtr THIS_PTR)
        {
            MilDeviceObject obj = MilDeviceObjectTable.Resolve(THIS_PTR);
            if (obj == null || obj.Kind != MilDeviceObjectKind.BitmapRenderTarget)
                return HResult.E_HANDLE;

            var target = (MilRenderTargetState)obj.Payload;
            if (target?.Bitmap == null) return HResult.E_HANDLE;

            target.Bitmap.Erase(SKColors.Transparent);
            target.ClearCount++;
            target.DirtyRectCount = 0;
            target.LastDirtyRect = null;
            return HResult.S_OK;
        }

        // ==================================================================
        //  工厂
        // ==================================================================

        /// <summary>
        /// 建 MIL 工厂。SDKVersion 必须等于 MIL_SDK_VERSION（0x200184C0），
        /// 否则返回 WGXERR_UNSUPPORTEDVERSION——与上游 apifunc.cpp:38 一致。
        /// </summary>
        public static int MILCreateFactory(out IntPtr ppIFactory, uint SDKVersion)
        {
            ppIFactory = IntPtr.Zero;

            if (SDKVersion != MilErrors.MilSdkVersion)
                return MilErrors.WGXERR_UNSUPPORTEDVERSION;

            MilDeviceObject factory = MilDeviceObjectTable.Register(
                MilDeviceObjectKind.Factory, null, $"factory sdk=0x{SDKVersion:X8}");

            ppIFactory = factory.Handle;
            return HResult.S_OK;
        }

        /// <summary>
        /// 建一个离屏位图渲染目标。Linux 上恒为软件渲染目标（MIL_RT_HARDWARE_ONLY
        /// 无法满足 → E_INVALIDARG；MIL_RT_NULL 建一个不落像素的空目标，行为等价，
        /// 这里按软件目标处理并记录）。
        /// </summary>
        public static int MILFactoryCreateBitmapRenderTarget(
            IntPtr THIS_PTR,
            uint width,
            uint height,
            MilPixelFormatEnum pixelFormatEnum,
            float dpiX,
            float dpiY,
            MILRTInitializationFlags dwFlags,
            out IntPtr ppIRenderTargetBitmap)
        {
            ppIRenderTargetBitmap = IntPtr.Zero;

            MilDeviceObject factory = MilDeviceObjectTable.Resolve(THIS_PTR);
            if (factory == null || factory.Kind != MilDeviceObjectKind.Factory)
                return HResult.E_HANDLE;

            if (width == 0 || height == 0) return HResult.E_INVALIDARG;
            if (dpiX <= 0f || dpiY <= 0f) return HResult.E_INVALIDARG;

            if ((dwFlags & MILRTInitializationFlags.MIL_RT_TYPE_MASK) ==
                MILRTInitializationFlags.MIL_RT_HARDWARE_ONLY)
            {
                // 本后端没有硬件渲染目标（handoff 决策 4：M1 只做软件渲染）。
                return HResult.E_INVALIDARG;
            }

            SKColorType colorType = pixelFormatEnum switch
            {
                MilPixelFormatEnum.Rgb24 => SKColorType.Rgb888x,
                MilPixelFormatEnum.Bgr24 => SKColorType.Rgb888x,
                MilPixelFormatEnum.Bgr32 => SKColorType.Bgra8888,
                MilPixelFormatEnum.Gray8 => SKColorType.Gray8,
                MilPixelFormatEnum.Bgra32 => SKColorType.Bgra8888,
                _ => SKColorType.Bgra8888,        // Default/Extended/Pbgra32 都落到 Bgra8888
            };

            SKAlphaType alphaType = pixelFormatEnum switch
            {
                MilPixelFormatEnum.Rgb24 or MilPixelFormatEnum.Bgr24 or
                MilPixelFormatEnum.Bgr32 or MilPixelFormatEnum.Gray8 => SKAlphaType.Opaque,
                _ => SKAlphaType.Premul,
            };

            var bitmap = new SKBitmap(new SKImageInfo((int)width, (int)height, colorType, alphaType));
            if (bitmap.IsNull)
            {
                bitmap.Dispose();
                return HResult.E_OUTOFMEMORY;
            }

            var state = new MilRenderTargetState
            {
                Bitmap = bitmap,
                Width = (int)width,
                Height = (int)height,
                DpiX = dpiX,
                DpiY = dpiY,
                PixelFormat = pixelFormatEnum,
                Flags = dwFlags,
            };

            MilDeviceObject obj = MilDeviceObjectTable.Register(
                MilDeviceObjectKind.BitmapRenderTarget, state,
                $"rt {width}x{height} {pixelFormatEnum} flags={dwFlags}");

            ppIRenderTargetBitmap = obj.Handle;
            return HResult.S_OK;
        }

        /// <summary>
        /// 用一个既有位图建软件渲染目标（对应上游
        /// MILFactoryCreateSWRenderTargetForBitmap：把 IWICBitmap 包成一个可渲染的面）。
        /// pIBitmap 必须是 MilPixelBufferTable 里登记的句柄；未知句柄 → E_HANDLE。
        /// **不复制像素**：渲染目标直接写进那张位图（与上游"目标就是同一块内存"一致）。
        /// </summary>
        public static int MILFactoryCreateSWRenderTargetForBitmap(
            IntPtr THIS_PTR,
            IntPtr pIBitmap,
            out IntPtr ppIRenderTargetBitmap)
        {
            ppIRenderTargetBitmap = IntPtr.Zero;

            MilDeviceObject factory = MilDeviceObjectTable.Resolve(THIS_PTR);
            if (factory == null || factory.Kind != MilDeviceObjectKind.Factory)
                return HResult.E_HANDLE;

            SKBitmap bitmap = MilPixelBufferTable.Resolve(pIBitmap);
            if (bitmap == null || bitmap.IsNull) return HResult.E_HANDLE;

            var state = new MilRenderTargetState
            {
                Bitmap = bitmap,
                Width = bitmap.Width,
                Height = bitmap.Height,
                DpiX = 96f,
                DpiY = 96f,
                PixelFormat = MilPixelFormatEnum.Default,
                Flags = MILRTInitializationFlags.MIL_RT_SOFTWARE_ONLY,
                OwnsBitmap = false,     // 位图归 MilPixelBufferTable 所有
            };

            MilDeviceObject obj = MilDeviceObjectTable.Register(
                MilDeviceObjectKind.BitmapRenderTarget, state,
                $"rt-for-bitmap {bitmap.Width}x{bitmap.Height}");

            ppIRenderTargetBitmap = obj.Handle;
            return HResult.S_OK;
        }

        /// <summary>
        /// 建媒体播放器。**E_NOTIMPL**：handoff 的 U3 口径把整个 MILMedia* 面
        /// 定为暂缓，MILFactoryCreateMediaPlayer 是这条链的入口，一并延后。
        /// </summary>
        public static int MILFactoryCreateMediaPlayer(
            IntPtr THIS_PTR,
            IntPtr pEventProxy,
            bool canOpenAllMedia,
            out IntPtr ppMedia)
        {
            ppMedia = IntPtr.Zero;
            _ = THIS_PTR;
            _ = pEventProxy;
            _ = canOpenAllMedia;
            return HResult.E_NOTIMPL;
        }

        // ==================================================================
        //  InteropDeviceBitmap_*（D3D11 互操作：Linux 无对等物）
        // ==================================================================

        /// <summary>D3D11 共享纹理的互操作位图，Linux 上无实现 → E_NOTIMPL。</summary>
        public static int InteropDeviceBitmap_Create(
            IntPtr d3dResource,
            double dpiX,
            double dpiY,
            uint version,
            IntPtr pfnCallback,
            bool isSoftwareFallbackEnabled,
            out IntPtr ppInteropDeviceBitmap,
            out uint pixelWidth,
            out uint pixelHeight)
        {
            ppInteropDeviceBitmap = IntPtr.Zero;
            pixelWidth = 0;
            pixelHeight = 0;

            if (d3dResource == IntPtr.Zero) return HResult.E_INVALIDARG;
            if (dpiX <= 0 || dpiY <= 0) return HResult.E_INVALIDARG;

            _ = version;
            _ = pfnCallback;
            _ = isSoftwareFallbackEnabled;
            return HResult.E_NOTIMPL;
        }

        /// <summary>void 导出：没有实现也就没有资源可解绑，幂等空操作。</summary>
        public static void InteropDeviceBitmap_Detach(IntPtr pInteropDeviceBitmap)
        {
            // 上游这里是 ReleaseInterface；Linux 上从未创建过这类对象。
            _ = pInteropDeviceBitmap;
        }

        /// <summary>D3D11 互操作位图的脏矩形 → E_NOTIMPL。</summary>
        public static int InteropDeviceBitmap_AddDirtyRect(
            int x, int y, int w, int h, IntPtr pInteropDeviceBitmap)
        {
            _ = x;
            _ = y;
            _ = w;
            _ = h;
            _ = pInteropDeviceBitmap;
            return HResult.E_NOTIMPL;
        }

        /// <summary>把互操作位图降级成软件位图 → E_NOTIMPL（没有源对象）。</summary>
        public static int InteropDeviceBitmap_GetAsSoftwareBitmap(
            IntPtr pInteropDeviceBitmap,
            out IntPtr pIWICBitmapSource)
        {
            pIWICBitmapSource = IntPtr.Zero;
            _ = pInteropDeviceBitmap;
            return HResult.E_NOTIMPL;
        }

        // ==================================================================
        //  CWICWrapperBitmap
        // ==================================================================

        /// <summary>
        /// 把一个 IWICBitmapSource 包成 CWICWrapperBitmap。
        /// Linux 侧：pIWICBitmapSource 是 MilPixelBufferTable 的句柄，包装结果是**新的**
        /// 句柄（引用计数 +1，同一张位图），与上游"包一层 COM 对象"的生命周期语义对应。
        /// 未登记的句柄 → E_HANDLE（上游 IFCNULL 也是 E_HANDLE）。
        /// </summary>
        private static int _cwicWrapMissReported;

        public static int MilResource_CreateCWICWrapperBitmap(
            IntPtr pIWICBitmapSource,
            out IntPtr pCWICWrapperBitmap)
        {
            pCWICWrapperBitmap = IntPtr.Zero;

            SKBitmap bitmap = MilPixelBufferTable.Resolve(pIWICBitmapSource);
            if (bitmap != null)
            {
                MilPixelBufferTable.AddRef(pIWICBitmapSource);
                pCWICWrapperBitmap = MilPixelBufferTable.RegisterBorrowed(bitmap, "CWICWrapperBitmap");
                return HResult.S_OK;
            }

            // ── #24：WIC 所有者（libwpfwic.so）下发的句柄 ⇒ **物化**一份我们持有的位图 ──
            // 【为什么这条分支必须存在】上游 `BitmapSource.get_DUCECompatiblePtr()`
            //   （`BitmapSource.cs:872`）调 `CreateCWICWrapperBitmap(pIWICSource, …)`，
            //   而"由 WIC 解码/转换来的 BitmapSource"的 `WicSourceHandle` 在 **shim 的表**里，
            //   不在 MIL 的 `MilPixelBufferTable` 里 ⇒ 修前一律 E_HANDLE
            //   ⇒ 上层 `HRESULT.Check` 抛 `COMException(0x80070006)`（主控实测栈）。
            // 【为什么这里**拷**、而 v2（WriteableBitmap 后缓冲）**借**】两处相反是有意的：
            //   `BitmapSource` 在 MIL 侧是**只读**语义（渲染只读它）⇒ 拷一份不改变任何可观察行为；
            //   而 `WriteableBitmap` 的后缓冲是**读写**、`Lock` 拿到的指针必须就是渲染读的那块内存
            //   ⇒ 那一处必须借用（见本文件 v2 的注释）。**下一个人别把这里的"拷"当成疏漏。**
            if (MilExternalHandleBridge.IsConnected && MilExternalHandleBridge.OwnsHandleProbe(pIWICBitmapSource))
            {
                if (TryMaterializeWicSource(pIWICBitmapSource, out SKBitmap materialized, out string why))
                {
                    IntPtr pixelBuffer = MilPixelBufferTable.RegisterBorrowed(
                        materialized, "CWICWrapperBitmap(materialized from WIC)");
                    MilDeviceObject wrapper = MilDeviceObjectTable.Register(
                        MilDeviceObjectKind.CwicWrapperBitmap, null,
                        $"cwic {materialized.Width}x{materialized.Height} from-wic");
                    // 负载持有"归零时要回收的物化位图"（`DisposePayload` → `MilCwicWrapper.Dispose`
                    // → `MilCwicWrapperTable.Release`）。句柄要等 Register 之后才知道，所以这里后置赋值。
                    wrapper.Payload = new MilCwicWrapper { WrapperHandle = wrapper.Handle };
                    MilCwicWrapperTable.Register(wrapper.Handle, materialized, pixelBuffer);

                    MilDiagnostics.Note(
                        $"MilResource_CreateCWICWrapperBitmap: WIC 句柄 0x{(long)pIWICBitmapSource:x} **物化**成功" +
                        $"（{materialized.Width}x{materialized.Height} {materialized.ColorType}）⇒ wrapper=0x{(long)wrapper.Handle:x}；" +
                        $"在册={MilCwicWrapperTable.InFlight}");
                    pCWICWrapperBitmap = wrapper.Handle;
                    return HResult.S_OK;
                }

                if (System.Threading.Interlocked.Exchange(ref _cwicWrapMissReported, 1) == 0)
                    MilDiagnostics.Note(
                        $"MilResource_CreateCWICWrapperBitmap: 句柄 0x{(long)pIWICBitmapSource:x} 是 WIC 的，" +
                        $"但物化失败：{why} ⇒ E_HANDLE（fail-safe，语义与修前一致）");
                return HResult.E_HANDLE;
            }

            if (bitmap == null)
            {
                // 【为什么这里要"说清楚是哪种句柄"】修前只回一个 E_HANDLE，症状是上层
                //   `BitmapSource.get_DUCECompatiblePtr()` 抛 COMException(0x80070006)，
                //   而"句柄到底属于谁"完全不可见 —— 只能靠猜（本项目因此误判过）。
                //   这里把三张表的态度一次说清：MIL 像素缓冲 / MIL 设备对象 / WIC 所有者。
                //   免采样（只报一次），失败语义**一点不变**（仍然 E_HANDLE）。
                if (System.Threading.Interlocked.Exchange(ref _cwicWrapMissReported, 1) == 0)
                {
                    MilDeviceObject dev = MilDeviceObjectTable.Resolve(pIWICBitmapSource);
                    bool wicOwned = MilExternalHandleBridge.IsConnected &&
                                    MilExternalHandleBridge.OwnsHandleProbe(pIWICBitmapSource);
                    MilDiagnostics.Note(
                        $"MilResource_CreateCWICWrapperBitmap: 句柄 0x{(long)pIWICBitmapSource:x} 不在 " +
                        $"MilPixelBufferTable 里 ⇒ E_HANDLE。" +
                        $"设备对象={(dev == null ? "无" : dev.Kind.ToString())}；" +
                        $"WIC 所有者认领={(wicOwned ? "是" : "否")}；WIC 桥接={(MilExternalHandleBridge.IsConnected ? "已接上" : "未接上")}");
                }
                return HResult.E_HANDLE;
            }

            pCWICWrapperBitmap = IntPtr.Zero;
            return HResult.E_HANDLE;   // 不可达（上面已 return），保留以维持"每条路径都有返回值"的形状
        }

        /// <summary>
        /// 把 WIC 源的像素**拉进一张我们持有的 `SKBitmap`**（#24 的物化）。
        /// v1 只覆盖 `Pbgra32` / `Bgra32` / `Bgr32`（与 `WriteableBitmap` 同一格式集合）；
        /// **其余格式一律诚实失败**（返回 false + 原因），由调用方退回 `E_HANDLE` ——
        /// 不猜、不近似、不编数据。
        /// </summary>
        private static bool TryMaterializeWicSource(IntPtr source, out SKBitmap bitmap, out string reason)
        {
            bitmap = null;
            reason = null;

            if (!MilExternalHandleBridge.BitmapSourceReadAvailable)
            {
                reason = "shim 没有 GetSize/GetPixelFormat/CopyPixels 三个只读 proxy 导出";
                return false;
            }
            if (!MilExternalHandleBridge.TryGetWicSize(source, out uint width, out uint height))
            {
                reason = "IWICBitmapSource_GetSize 失败/不可用";
                return false;
            }
            if (!MilExternalHandleBridge.TryGetWicPixelFormat(source, out Guid format))
            {
                reason = "IWICBitmapSource_GetPixelFormat 失败/不可用";
                return false;
            }

            SKColorType colorType;
            SKAlphaType alphaType;
            if (format == MilPixelFormats.Pbgra32) { colorType = SKColorType.Bgra8888; alphaType = SKAlphaType.Premul; }
            else if (format == MilPixelFormats.Bgra32) { colorType = SKColorType.Bgra8888; alphaType = SKAlphaType.Unpremul; }
            else if (format == MilPixelFormats.Bgr32) { colorType = SKColorType.Bgra8888; alphaType = SKAlphaType.Opaque; }
            else
            {
                reason = $"暂不支持该 WIC 像素格式（{format}）—— v1 只覆盖 Pbgra32/Bgra32/Bgr32";
                return false;
            }

            var info = new SKImageInfo((int)width, (int)height, colorType, alphaType);
            var bmp = new SKBitmap(info);
            if (bmp.IsNull || bmp.GetPixels() == IntPtr.Zero)
            {
                bmp.Dispose();
                reason = "SKBitmap 分配失败";
                return false;
            }

            uint stride = (uint)bmp.RowBytes;
            uint bufferSize = stride * height;
            if (!MilExternalHandleBridge.TryCopyWicPixels(source, stride, bufferSize, bmp.GetPixels()))
            {
                bmp.Dispose();
                reason = "IWICBitmapSource_CopyPixels 失败（外来源的借用像素未接时它会诚实地返回 UNSUPPORTEDOPERATION）";
                return false;
            }

            bitmap = bmp;

            // D-d 判别用（缺省关 `WPF_LINUX_CWIC_TRACE=1`）：把"我们以为的尺寸"与来源逐项打出来。
            // 【为什么要打这四样】"96×96 的 Image 画出空白"有两种完全不同的成因：
            //   ① 源**本来就**是 1×1（样例/降级路径的输入问题）⇒ 不是物化路径的 bug；
            //   ② `GetSize` 报错/我们读错 ⇒ 是物化路径的 bug。
            //   这两者的证据形态不同，所以把「shim 说多大」「我们建成多大」「CopyPixels 结果」「句柄归属」并排打。
            if (Environment.GetEnvironmentVariable("WPF_LINUX_CWIC_TRACE") == "1")
            {
                bool owned = MilExternalHandleBridge.OwnsHandleProbe(source);
                bool size2Ok = MilExternalHandleBridge.TryGetWicSize(source, out uint w2, out uint h2);
                // 【字段名更正（主控点名）】这里打的是 `TryMaterializeWicSource` **由 `GetSize` 得到的**尺寸，
                //   **不是** `WICShim_RegisterForeignSource` 的入参 —— 原名 `登记传入=` 会让人读错，已改名。
                // 【D-d 取证】`WicShim_DescribeHandle` 是**进程内**问"这个句柄到底是什么"的唯一出口
                //   （独立进程的 `probe_describe` 看不到本进程的 shim 表 —— 实测过）。只读、只在本开关下调用。
                string describe = MilExternalHandleBridge.DescribeHandle(source) ?? "<不可用>";
                Console.Error.WriteLine(
                    $"[cwic-trace] source=0x{(long)source:x} ownedByWic={owned} " +
                    $"materialize尺寸={width}x{height} foreignSources={MilExternalHandleBridge.ForeignSourceCount()} " +
                    $"shimGetSize={width}x{height}（第二次={w2}x{h2} ok={size2Ok}） format={format} " +
                    $"→ SKBitmap={bmp.Width}x{bmp.Height} colorType={colorType} alphaType={alphaType} " +
                    $"stride={bmp.RowBytes} bufferSize={bufferSize} copyPixels=S_OK\n" +
                    $"[cwic-trace]   describe(source)= {describe}");
                Console.Error.Flush();
            }
            return true;
        }

        // ==================================================================
        //  IWICColorContext_*_Proxy
        // ==================================================================

        /// <summary>
        /// 取 ICC profile 字节。cbBuffer 小于实际长度时只拷 cbBuffer 字节并返回
        /// WINCODEC_ERR_INSUFFICIENTBUFFER（WIC 的惯例），pcbActual 始终回填实际长度。
        /// </summary>
        public static int IWICColorContext_GetProfileBytes_Proxy(
            IntPtr THIS_PTR,
            uint cbBuffer,
            byte[] pbBuffer,
            out uint pcbActual)
        {
            pcbActual = 0;

            MilColorContext context = MilColorContextTable.Resolve(THIS_PTR);
            if (context == null) return HResult.E_HANDLE;

            byte[] profile = context.ProfileBytes ?? Array.Empty<byte>();
            pcbActual = (uint)profile.Length;

            if (profile.Length == 0) return HResult.S_OK;
            if (pbBuffer == null) return HResult.E_INVALIDARG;

            int toCopy = (int)Math.Min(cbBuffer, (uint)profile.Length);
            Array.Copy(profile, 0, pbBuffer, 0, Math.Min(toCopy, pbBuffer.Length));

            return cbBuffer < profile.Length ? MilErrors.WINCODEC_ERR_INSUFFICIENTBUFFER : HResult.S_OK;
        }

        /// <summary>取颜色上下文的类型。</summary>
        public static int IWICColorContext_GetType_Proxy(
            IntPtr THIS_PTR,
            out MilWicColorContextType pType)
        {
            pType = MilWicColorContextType.WICColorContextUninitialized;

            MilColorContext context = MilColorContextTable.Resolve(THIS_PTR);
            if (context == null) return HResult.E_HANDLE;

            pType = context.Type;
            return HResult.S_OK;
        }

        /// <summary>取 EXIF 色彩空间值（仅当类型是 ExifColorSpace 时有意义，与 WIC 一致）。</summary>
        public static int IWICColorContext_GetExifColorSpace_Proxy(IntPtr THIS_PTR, out uint pValue)
        {
            pValue = 0;

            MilColorContext context = MilColorContextTable.Resolve(THIS_PTR);
            if (context == null) return HResult.E_HANDLE;

            pValue = context.ExifColorSpace;
            return HResult.S_OK;
        }

        // ==================================================================
        //  内部
        // ==================================================================

        private static MilDoubleBufferedState ResolveDoubleBuffered(IntPtr handle)
        {
            MilDeviceObject obj = MilDeviceObjectTable.Resolve(handle);
            if (obj == null || obj.Kind != MilDeviceObjectKind.SwDoubleBufferedBitmap) return null;
            return obj.Payload as MilDoubleBufferedState;
        }
    }

    /// <summary>离屏渲染目标的状态（MILRenderTargetBitmap* / 工厂创建的位图目标）。</summary>
    public sealed class MilRenderTargetState : IDisposable
    {
        public SKBitmap Bitmap;
        public int Width;
        public int Height;
        public float DpiX = 96f;
        public float DpiY = 96f;
        public MilPixelFormatEnum PixelFormat;
        public MILRTInitializationFlags Flags;

        /// <summary>位图是否归本对象所有（工厂按尺寸建的归本对象；包既有位图的不归）。</summary>
        public bool OwnsBitmap = true;

        /// <summary>GetBitmap 发放出去的位图句柄（同一目标只发一个）。</summary>
        public IntPtr BitmapHandle;

        /// <summary>该位图句柄别名到的 `WicBitmapSource` 设备对象句柄（见 `MILRenderTargetBitmapGetBitmap`）。</summary>
        public IntPtr WicSourceDeviceHandle;

        public int ClearCount;
        public int DirtyRectCount;
        public MilInt32Rect? LastDirtyRect;

        public void Dispose()
        {
            // 【顺序（与 `MilDoubleBufferedState.Dispose` 同一口径）】
            //   ① 先注销 shim 的 WIC 外来源登记（shim 手上借的是 `Bitmap` 的像素）→
            //   ② 摘别名设备对象 → ③ 摘像素缓冲句柄 → ④ 释放 SKBitmap。
            //   ① 必须在 ④ **之前**：否则 shim 会继续拿着指向已释放位图的指针（野内存）。
            if (BitmapHandle != IntPtr.Zero)
            {
                int unregHr = MilExternalHandleBridge.UnregisterForeignSource(BitmapHandle);
                if (HResult.Failed(unregHr))
                    MilDiagnostics.Note(
                        $"RenderTargetBitmap.Dispose: 注销 WIC 外来源未生效（hr=0x{unregHr:x8}）" +
                        "（旧 shim 无此导出 / 从未登记过 —— 都属预期，不影响拆除）");

                MilBackBufferSourceTable.Forget(BitmapHandle);
                MilPixelBufferTable.Unregister(BitmapHandle, force: true);
                BitmapHandle = IntPtr.Zero;
            }
            WicSourceDeviceHandle = IntPtr.Zero;

            if (OwnsBitmap) Bitmap?.Dispose();
            Bitmap = null;
        }
    }
}
