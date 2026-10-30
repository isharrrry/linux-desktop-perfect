// Licensed to the .NET Foundation under one or more agreements.
//
// MIL 内置位图效果 → Skia SKImageFilter。
//
// ── 为什么只做 Blur / DropShadow ──────────────────────────────────────────────
//   上游 dotnet/wpf 的 Effect 体系有三条互不相干的通道（逐条查过上游代码）：
//
//   1. `MilCmdShaderEffect`(0x70) + `MilCmdPixelShader`(0x6c)：HLSL 字节码。
//      → C 类，永久不做（docs/unimplemented.md §0）。替代路径是 Skia SKRuntimeEffect，
//        **不是**本文件的事。
//   2. `MilCmdBlurEffect`(0x6e) / `MilCmdDropShadowEffect`(0x6f)：两个**独立顶层命令**，
//      载荷全是 double + MilColorF + 枚举（POD），由
//      PresentationCore/System/Windows/Media/Effects/Generated/{Blur,DropShadow}Effect.cs
//      的 UpdateResource 封送。→ 本文件实现这两个。
//   3. 旧 `BitmapEffect`（Blur/DropShadow/OuterGlow/Bevel/Emboss 的 *BitmapEffect 版）：
//      上游自 .NET 4 起就是**空实现**（Effects/*.cs 全带 [Obsolete]，背后是进程内
//      IMILBitmapEffect COM 对象，压根没有 DUCE 命令）。→ 不存在"实现"这回事。
//
//   渲染层拿到效果句柄的位置是 `MilPushEffect`(0x55) 绘图指令，其载荷布局在上游
//   PresentationCore/System/Windows/Media/Generated/RenderData.cs:1050 实测为 8 字节：
//       hEffect@0(4)  hEffectInput@4(4)
//   两个句柄都过 Commands/MilCommandDispatcher 落到资源表里，本文件按 hEffect 查。
//   hEffectInput 是"把另一张位图当效果输入"，上游只有旧的 BitmapEffect 通道用它；
//   Blur / DropShadowEffect 的 UpdateResource 一律传空，故这里**故意不读**——
//   内容就是 Push/Pop 之间画的东西，这是 WPF Effect 的默认语义。
//
// ── 数值依据（都取自上游 C++，不是估的）──────────────────────────────────────
//   σ = Radius / 3
//       WpfGfx/core/resources/BlurEffect.cpp:351 原注释：
//       "Choosing a standard deviation of 1/3rd the radius is standard for a discrete
//        approximation of the gaussian function."
//   DropShadow 的 (dx, dy)
//       WpfGfx/core/resources/DropShadowEffect.cpp:893-894：
//           offsetX = depth * cos(DegToRad(direction))
//           offsetY = depth * sin(DegToRad(direction))
//       那是 **WPF 世界坐标（Y 轴朝上）**；Skia 的 Y 轴朝下，故 dy 取负。
//       判据是上游 Effects/DropShadowEffect.cs:39-56 的 GetRenderBounds：offsetY >= 0
//       时执行 `topLeft.Y -= offsetY`（往"上"扩包围盒），即 +Y 在上是 WPF 侧的正方向。
//       自检：默认 Direction=315 → dx=+0.707d, dy=+0.707d → 右下角阴影，与 WPF 默认一致。
//       135°（左上）时 dy=-0.707d → 阴影往上，与 315° 严格镜像；EffectTests 里
//       两个方向各钉一个采样点，Y 轴符号一旦写反就会有一侧落空。
//   DropShadow 的模糊半径
//       DropShadowEffect.cpp:135 直接复用 CMilBlurEffectDuce 的核，故同样 σ = BlurRadius/3。
//
// ── 已知简化（诚实标注）───────────────────────────────────────────────────────
//   · KernelType.Box：WPF 是真方框核；Skia 只有高斯（内部用三次方框逼近）。
//     Box 一律退化成高斯，像素观感接近但不等价。
//   · RenderingBias.Performance / Quality：Skia CPU 后端只有一条路径，两者等价处理。
//
// ── TryResolve 的返回值契约（改这个文件必须读懂）───────────────────────────────
//   返回值 = "这个句柄是不是本后端认识的效果"；out filter = null 表示"认得，但它
//   退化成空操作"。两者是**两件独立的事**，调用方必须按矩阵处理：
//
//       supported=false, filter=null  → 不认识（比如将来塞进来的 ShaderEffect），记 NotDrawn
//       supported=true,  filter=null  → 认得且是空操作（Radius=0），压一层裸 Save 顶位，不算未画出
//       supported=true,  filter≠null  → 压一层带滤波器的图层
//
//   早先这里拆成 Create + IsSupported 两个方法，后端先 Create 拿到 null，再调
//   IsSupported 判"是空操作还是不认识"——**同一条指令把资源表查了两遍**，外部
//   EffectResolver 委托也被调用两次（那是个用户回调，重复调用有副作用风险）。
//   合成一个 TryResolve 之后一次查表同时给出两个答案。

using System;
using SkiaSharp;
using WpfGfx.Linux.Contracts;
using WpfGfx.Linux.Resources;

namespace WpfGfx.Linux.Rendering
{
    internal static class SkiaEffect
    {
        /// <summary>
        /// 效果句柄 → SKImageFilter，一次查表同时给出"认不认得"和"滤波长什么样"。
        /// </summary>
        /// <param name="provider">资源提供者。</param>
        /// <param name="handle">MilPushEffect 载荷里的 hEffect。</param>
        /// <param name="filter">
        /// 滤镜实例；null 表示"认得这个效果但它退化成空操作"。调用方负责 Dispose 非 null 的实例。
        /// </param>
        /// <returns>true = 本后端认识这个句柄；false = 压根不认识。</returns>
        public static bool TryResolve(
            MilResourceProvider provider, MilResourceHandle handle, out SKImageFilter filter)
        {
            filter = null;
            if (provider == null || handle.IsNull) return false;

            // 扩展点优先：外部（将来接 ShaderEffect 的组）可以注册自己的效果工厂，
            // 不必改本目录一行。与 BitmapResolver / GlyphRunRenderer 同一范式。
            SKImageFilter external = provider.LookupEffect(handle);
            if (external != null)
            {
                filter = external;
                return true;
            }

            switch (provider.Lookup(handle))
            {
                case MilBlurEffect blur:
                    filter = CreateBlur(blur);
                    return true;

                case MilDropShadowEffect shadow:
                    filter = CreateDropShadow(shadow);
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>σ 由半径换算：σ = radius / 3（依据见文件头）。半径为 0 → null（等同无效果）。</summary>
        private static SKImageFilter CreateBlur(MilBlurEffect blur)
        {
            float sigma = RadiusToSigma(blur.Radius);
            if (sigma <= 0f) return null;
            return SKImageFilter.CreateBlur(sigma, sigma);
        }

        private static SKImageFilter CreateDropShadow(MilDropShadowEffect shadow)
        {
            SKColor color = SkiaColor.WithOpacity(
                SkiaColor.FromMilColorF(shadow.Color), (float)shadow.Opacity);

            // 全透明的阴影画出来是 0 贡献，与"无效果"逐像素等价 → 直接退化，
            // 省掉一整层 SaveLayer。（MilDropShadowEffect.Color 的默认值是
            // default(MilColorF)，即全透明黑，走的就是这条分支。）
            if (color.Alpha == 0) return null;

            float sigma = RadiusToSigma(shadow.BlurRadius);
            double depth = shadow.ShadowDepth;

            // 既无偏移又无模糊 → 阴影正好落在内容背后且形状一致。对**不透明**内容
            // 它本来就看不出来，但对半透明内容，阴影色会透过内容显出来，与"无效果"
            // 逐像素不一致。这里按空操作处理，让"退化为无效果"对所有内容都成立。
            if (sigma <= 0f && Math.Abs(depth) < 1e-6) return null;

            double radians = shadow.Direction * Math.PI / 180.0;
            float dx = (float)(Math.Cos(radians) * depth);
            float dy = (float)(-Math.Sin(radians) * depth);   // WPF 的 +Y 朝上，Skia 朝下

            // CreateDropShadow 输出的是「阴影 + 原内容」，正是 WPF DropShadowEffect 的语义
            // （阴影落在内容后面，内容本身仍然可见）。只要阴影用 CreateDropShadowOnly。
            return SKImageFilter.CreateDropShadow(dx, dy, sigma, sigma, color);
        }

        /// <summary>
        /// 半径 → 高斯标准差。上游 BlurEffect.cpp:351：σ = radius / 3。
        /// 上游对半径取 max(r, 0)，负半径在这里同样落到 0 → 无效果。
        /// </summary>
        private static float RadiusToSigma(double radius)
        {
            if (!(radius > 0.0)) return 0f;      // 顺手处理 NaN
            return (float)(radius / 3.0);
        }
    }
}
