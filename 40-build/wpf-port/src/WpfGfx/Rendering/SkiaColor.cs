// Licensed to the .NET Foundation under one or more agreements.
//
// MIL 颜色 → Skia 颜色。
//
// 【关键：MilColorF 是 scRGB，不是 sRGB/255】
//   上游 PresentationCore/System/Windows/Media/Composition.cs:13
//       color.r = c.ScR; color.g = c.ScG; color.b = c.ScB; color.a = c.ScA;
//   也就是说命令流里躺着的是**线性光 scRGB 浮点**，直接 ×255 当 sRGB 用会让中间调
//   整体偏亮（0.5 的 scRGB 其实是 sRGB 188 而不是 128）。
//
//   转换函数逐行对着上游 Color.cs:1050 的 ScRgbTosRgb 抄，包括 +0.5 的取整偏置——
//   golden image 依赖这个取整，自己"顺手改成 MidpointRounding"就会让基准图全红。
//
//   alpha 例外：WPF 的 alpha 不做伽马校正（Color.cs:93 是 a*255），故只乘 255。

using System;
using SkiaSharp;
using WpfGfx.Linux.Interop;

namespace WpfGfx.Linux.Rendering
{
    internal static class SkiaColor
    {
        /// <summary>scRGB 分量 → sRGB 字节。逐行对应上游 Color.ScRgbTosRgb。</summary>
        public static byte ScRgbToSrgbByte(float val)
        {
            if (!(val > 0.0f))                       // 顺手处理 NaN
                return 0;
            if (val <= 0.0031308f)
                return (byte)((255.0f * val * 12.92f) + 0.5f);
            if (val < 1.0f)
                return (byte)((255.0f * ((1.055f * (float)Math.Pow(val, 1.0 / 2.4)) - 0.055f)) + 0.5f);
            return 255;
        }

        /// <summary>sRGB 字节 → scRGB 浮点。上游 Color.sRgbToScRgb 的逆。</summary>
        public static float SrgbByteToScRgb(byte bval)
        {
            float val = bval / 255.0f;
            if (!(val > 0.0f)) return 0.0f;
            if (val <= 0.04045f) return val / 12.92f;
            if (val < 1.0f) return (float)Math.Pow((val + 0.055f) / 1.055f, 2.4);
            return 1.0f;
        }

        public static SKColor FromMilColorF(MilColorF c) => new SKColor(
            ScRgbToSrgbByte(c.R),
            ScRgbToSrgbByte(c.G),
            ScRgbToSrgbByte(c.B),
            AlphaToByte(c.A));

        /// <summary>alpha 是线性量，不做伽马校正。</summary>
        public static byte AlphaToByte(float a)
        {
            if (!(a > 0.0f)) return 0;
            if (a >= 1.0f) return 255;
            return (byte)((a * 255.0f) + 0.5f);
        }

        /// <summary>把画刷/节点的不透明度乘进颜色（WPF 也是预乘到 brush 上再画）。</summary>
        public static SKColor WithOpacity(SKColor color, float opacity)
        {
            if (opacity >= 1.0f) return color;
            if (opacity <= 0.0f) return color.WithAlpha(0);
            return color.WithAlpha((byte)Math.Round(color.Alpha * opacity));
        }

        /// <summary>测试与工具用：sRGB 字节 → MilColorF（走 scRGB，与上游封送一致）。</summary>
        public static MilColorF MakeScRgb(byte r, byte g, byte b, byte a) => new MilColorF(
            SrgbByteToScRgb(r),
            SrgbByteToScRgb(g),
            SrgbByteToScRgb(b),
            a / 255.0f);
    }
}
