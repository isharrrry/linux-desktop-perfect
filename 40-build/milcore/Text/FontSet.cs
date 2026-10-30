// 字体集合：从**一个目录**里加载全部字体文件，按 (家族名, 字重, 倾斜) 索引。
//
// 【为什么只认一个目录】
//   golden image 的整个前提是"同一个输入永远出同一个输出"。一旦允许 FontSet 在
//   打包字体之外去摸系统字体，那么容器里装没装 fonts-noto、装的是哪个版本，都会
//   改变字形轮廓——测试会在不同机器上给出不同结果，而且这种 flaky 极难查。
//   所以这里的设计是**封闭的**：目录里有什么就只有什么，查不到就 false。
//
// 【索引为什么不靠文件名】
//   NotoSans-Bold.ttf → ("Noto Sans", Bold) 这种拆法只对 Noto 这一个命名约定有效。
//   真正可靠的来源是字体自己的 name 表：SKTypeface.FamilyName / FontWeight /
//   FontSlant。已实测 build/fonts 下四个文件都能被 Skia 正确读出
//   FamilyName="Noto Sans"、Weight=400/700、Slant=Upright/Italic。
//   文件名只用来排序（保证枚举顺序确定），不参与解析。
//
// 【查找为什么先精确后近似】
//   精确匹配失败时（比如调用方把字重写成 500），退化到"家族名 + 是否粗体 + 是否斜体"
//   三元组，让 400/500/600 都落到同一份 Regular/Bold 上。这不是要"猜"，而是
//   打包字体里本来就没有 9 个字重，硬要精确匹配只会让上层什么都画不出来。

using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using WpfGfx.Linux.Interop;

namespace WpfGfx.Linux.Text
{
    /// <summary>
    /// 一组字体文件。同一个实例对同一个 <see cref="TextFontDescription"/> 永远返回
    /// 同一个 <see cref="SKTypeface"/> 实例（缓存），因此重复测量/绘制不会重新解析字体。
    /// </summary>
    internal sealed class FontSet : IDisposable
    {
        private readonly List<SKTypeface> _typefaces = new List<SKTypeface>();

        /// <summary>目录里按文件名排序后的字体文件名（不含路径）。</summary>
        private readonly List<string> _fileNames = new List<string>();

        private readonly Dictionary<TextFontDescription, SKTypeface> _exact =
            new Dictionary<TextFontDescription, SKTypeface>();

        private readonly Dictionary<(string Family, bool Bold, bool Italic), SKTypeface> _approximate =
            new Dictionary<(string, bool, bool), SKTypeface>();

        private bool _disposed;

        private FontSet()
        {
        }

        /// <summary>目录（不存在或为空时得到一个空集合，不抛）。</summary>
        public static FontSet FromDirectory(string directory)
        {
            var set = new FontSet();

            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return set;

            // 排序：Directory.EnumerateFiles 的顺序在不同文件系统上不保证，
            // 而"哪一份先被索引"会影响近似匹配的胜出者。排序后行为才是确定的。
            //
            // 【P0 收尾轮 · A 步：必须**递归**】
            //   修前只扫顶层 ⇒ `/usr/share/fonts` 顶层**一个字体文件都没有**
            //   （都在 `truetype/<vendor>/`、`opentype/…` 下）⇒ 集合恒为空 ⇒
            //   `TryResolve` 永远失败 ⇒ 真应用默认配置（不设任何字体 env）**文字整段不画**，
            //   而唯一信号只是台账里一行 `未画种类 1`（画面其余部分完全正常，肉眼看着像没问题）。
            //   实测：`目录=/usr/share/fonts … 候选文件=0`。
            //   `IgnoreInaccessible = true`：系统字体目录里有 X11/encodings 之类的子目录，
            //   权限/软链异常不该让整份字体集合建不起来（那正是"默认配置什么都不画"的形态）。
            //   排序仍在**递归结果**上做一次，语义（索引顺序确定）保持不变。
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            };
            var files = new List<string>();
            files.AddRange(Directory.EnumerateFiles(directory, "*.ttf", options));
            files.AddRange(Directory.EnumerateFiles(directory, "*.otf", options));
            files.AddRange(Directory.EnumerateFiles(directory, "*.ttc", options));
            files.Sort(StringComparer.Ordinal);

            foreach (string path in files)
            {
                SKTypeface face;
                try
                {
                    // [W8 / D-F1c 内存半边] 走按路径共享的 `SKData`（否则每个字体文件各留一份整文件映射）。
                    face = SkiaFontFileCache.CreateFace(path, 0);
                }
                catch (Exception)
                {
                    continue; // 坏文件跳过，不让一份坏字体毁掉整个集合
                }

                if (face == null) continue;

                set._typefaces.Add(face);
                set._fileNames.Add(Path.GetFileName(path));

                // census（缺省关）：把"面 → 文件/faceIndex"记下来（SkiaSharp 的 SKTypeface 不暴露路径）。
                GlyphFaceCensus.NoteFaceSource(face, Path.GetFileName(path), 0);

                // 顺带量一条与 (乙) 直接相关的事实：**同一个文件里还有没有我们没加载的 face**
                //   （TTC/TTF 集合字体：本加载器只取 index 0）。只读、只计数。
                if (GlyphFaceCensus.Enabled)
                {
                    try
                    {
                        // [W8 / D-F1c] 同上：census 的"第二面"也从共享后备建，避免瞬时多一份整文件映射。
                        using SKTypeface second = SkiaFontFileCache.CreateFace(path, 1);
                        if (second != null)
                        {
                            GlyphFaceCensus.NoteFaceSource(second, Path.GetFileName(path), 1);
                        }
                    }
                    catch { /* 不是集合字体/读取失败：忽略 */ }
                }

                var desc = new TextFontDescription(face.FamilyName, face.FontWeight, face.FontSlant);
                set._exact[desc] = face;

                var key = (face.FamilyName, face.IsBold, face.IsItalic);
                if (!set._approximate.ContainsKey(key)) set._approximate[key] = face;
            }

            return set;
        }

        /// <summary>目录里的字体文件名（排序后）。</summary>
        public IReadOnlyList<string> FileNames => _fileNames;

        public bool IsEmpty => _typefaces.Count == 0;

        /// <summary>
        /// 解析字体。返回的 <see cref="SKTypeface"/> **归 FontSet 所有**，调用方不要 Dispose。
        /// 找不到返回 false —— 不会回落到系统字体或 Skia 的默认字体。
        /// </summary>
        public bool TryResolve(TextFontDescription description, out SKTypeface typeface)
        {
            ThrowIfDisposed();

            if (_exact.TryGetValue(description, out typeface) && typeface != null) return true;

            // 近似：家族名相同 + 粗/斜一致即可。
            if (_approximate.TryGetValue((description.FamilyName, description.IsBold, description.IsItalic),
                    out typeface) && typeface != null)
            {
                return true;
            }

            typeface = null;
            return false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (SKTypeface face in _typefaces) face.Dispose();
            _typefaces.Clear();
            _exact.Clear();
            _approximate.Clear();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FontSet));
        }
    }
}
