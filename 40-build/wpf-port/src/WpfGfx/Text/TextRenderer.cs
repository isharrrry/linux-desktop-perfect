// 文本渲染门面：FontSet + 默认字体 + SKFont 缓存，并挂到 T4 的扩展点上。
//
// 【为什么把挂载做成静态方法而不是让调用方自己 new 委托】
//   MilResourceProvider.GlyphRunRenderer 的签名是
//   `Func<SKCanvas, object, SKPaint, bool>`，其中 object 是 MilGlyphRun。
//   让调用方自己写 lambda 就意味着他们要自己处理"object 不是 MilGlyphRun"、
//   "字号为 0"、"字体解析失败"这些分支。收进这里，规则只有一份。
//
// 【SKFont 为什么要缓存】
//   SKFont 是一个 native 对象，每次绘制都 new 会给 GC 和 native 堆都压上不必要的
//   负担（一个 60fps 的窗口每秒就是 60 次）。按 (typeface.Handle, size) 缓存，
//   TextRenderer 自己 Dispose 时统一释放。

using System;
using System.Collections.Generic;
using SkiaSharp;
using WpfGfx.Linux.Rendering;
using WpfGfx.Linux.Resources;

namespace WpfGfx.Linux.Text
{
    /// <summary>文本渲染器。线程不安全（SKFont 缓存与 scratch paint 都没有加锁）。</summary>
    internal sealed class TextRenderer : IDisposable
    {
        private readonly FontSet _fonts;
        private readonly TextFontDescription _defaultFont;
        private readonly float _fallbackSize;

        private readonly Dictionary<(nint TypefaceHandle, float Size), SKFont> _fontCache =
            new Dictionary<(nint, float), SKFont>();

        /// <summary>度量专用的 paint。绘制时用调用方传进来的 paint，保证度量与绘制口径一致。</summary>
        private readonly SKPaint _measurePaint = new SKPaint { IsAntialias = true };

        private bool _disposed;

        /// <param name="fonts">字体集合（所有权转移给 TextRenderer，Dispose 时一起释放）。</param>
        /// <param name="defaultFont">MilGlyphRun 没带字体身份时使用的字体。</param>
        /// <param name="fallbackSize">MuSize 为 0 时的兜底字号。</param>
        public TextRenderer(FontSet fonts, TextFontDescription defaultFont, float fallbackSize)
        {
            _fonts = fonts ?? throw new ArgumentNullException(nameof(fonts));
            _defaultFont = defaultFont;
            _fallbackSize = fallbackSize > 0f ? fallbackSize : 12f;
        }

        /// <summary>
        /// MilGlyphRun → 字体的自定义解析钩子。
        /// 用于将来托管层接进来后，把 MILCMD_GLYPHRUN_CREATE 里的 PIDWriteFont
        /// 翻译成真正的字体。未设置时用构造期的 <c>defaultFont</c>。
        /// </summary>
        public Func<MilGlyphRun, TextFontDescription> MilFontResolver { get; set; }

        /// <summary>
        /// **债务 #14（面由 run 决定）的 seam**：给定一个 glyph run，返回"它 shaping 时实际用的那份面"。
        /// 由宿主（本工程 = `MilPresentation.EnsureGlyphRenderer`）接上：它读 `run.PIDWriteFont`
        /// 并去 `MilFontFaceTable.TryResolve` 查。**返回 null ⇒ 回落今天的族名路径**（不破坏现状）。
        /// ⚠️ 与 `MilFontResolver`（返回**描述**）并列而不是替换它：描述路径仍是兜底。
        /// </summary>
        public Func<MilGlyphRun, SKTypeface> MilFaceResolver { get; set; }

        public FontSet Fonts => _fonts;

        /// <summary>测量一段文本占的长度与各字形位置。解析不到字体时返回空度量。</summary>
        public GlyphRunMetrics Measure(GlyphRunRequest request)
        {
            ThrowIfDisposed();
            if (request == null) throw new ArgumentNullException(nameof(request));

            GlyphFaceCensus.NoteEntry("TextRenderer.Measure");
            if (!TryGetFont(request, out SKFont font)) return default;
            return GlyphRunLayout.Measure(request, font, _measurePaint);
        }

        /// <summary>绘制一段文本。返回 false 表示"没画"（字体解析失败或字形序列为空）。</summary>
        public bool Draw(SKCanvas canvas, GlyphRunRequest request, SKPaint paint)
        {
            ThrowIfDisposed();
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (paint == null) throw new ArgumentNullException(nameof(paint));

            GlyphFaceCensus.NoteEntry("TextRenderer.Draw");
            if (!TryGetFont(request, null, out SKFont font))
            {
                // 【census 的关键一条】解析失败**也要被记成一条可读事实**。
                //   我上一版把 census 挂在 `DrawResource` 里、且用 `_fonts.TryResolve` 自己解析一次 ——
                //   **解析失败时它会静默不记**（"探针恰好在它最该报的场景下变哑巴"），
                //   而且那条路与真正绘制用的 `TryGetFont` **不是同一条**（T2b 只读读代码抓到的）。
                //   现在改成：在**真正绘制的那条路**上、`TryGetFont` 之后记；失败记 `NoteUnresolved`。
                GlyphFaceCensus.NoteUnresolved(request);
                return false;
            }

            GlyphFaceCensus.NoteRun(font.Typeface, request.GlyphIndices);   // 只读：面 + 已解码的 id
            return GlyphRunPainter.Draw(canvas, request, font, paint);
        }

        /// <summary>
        /// 直接绘制一个 MIL 资源对象。这是挂到 <c>MilResourceProvider.GlyphRunRenderer</c>
        /// 上的那个委托的实现体。
        /// </summary>
        public bool DrawResource(SKCanvas canvas, object resource, SKPaint paint)
        {
            GlyphFaceCensus.NoteEntry("TextRenderer.DrawResource");
            if (resource is not MilGlyphRun run) return false;

            TextFontDescription font = MilFontResolver != null
                ? MilFontResolver(run)
                : _defaultFont;

            // ★ 债务 #14：**面由 run 决定** —— 先问 `MilFaceResolver`（宿主用 `run.PIDWriteFont`
            //   去 `MilFontFaceTable` 查）；拿不到就回落今天的族名路径。三种情况**分开计数**：
            //     · HitByHandle      = 按句柄解析成功
            //     · FallbackToFamily = **没有句柄**（PC 还没接线）⇒ 走族名
            //     · HandleUnresolved = **有句柄但解析失败**（注册与 run 不同步）—— 与上一条是**两种故障**
            SKTypeface preferred = null;
            if (MilFaceResolver != null)
            {
                try { preferred = MilFaceResolver(run); }
                catch { preferred = null; }
            }

            if (preferred != null) GlyphFaceCensus.NoteFaceHitByHandle();
            else if (run.PIDWriteFont != 0) GlyphFaceCensus.NoteFaceHandleUnresolved();
            else GlyphFaceCensus.NoteFaceFallbackToFamily();

            // census **不在这里**：这里再解析一次 `_fonts.TryResolve` 会开出"第二条解析路"，
            // 解析失败时还会静默（见 `Draw` 里那段注释）。真正的记录点在 `Draw` 内、`TryGetFont` 之后。

            GlyphRunRequest request = MilGlyphRunAdapter.ToRequest(run, font, _fallbackSize);
            if (preferred != null && TryGetFont(request, preferred, out SKFont faceFont))
            {
                GlyphFaceCensus.NoteRun(preferred, request.GlyphIndices);
                return GlyphRunPainter.Draw(canvas, request, faceFont, paint);
            }
            // 回落：既有路径（内部会记 `NoteRun` 与"解析不到面"）
            return Draw(canvas, request, paint);
        }

        /// <summary>包装成 T4 扩展点要求的委托签名。</summary>
        public Func<SKCanvas, object, SKPaint, bool> AsGlyphRunRenderer() => DrawResource;

        /// <summary>
        /// 挂到渲染层的资源提供者上。这是 T6 与 T4 的**唯一**接线处，
        /// 不修改 Rendering/ 下任何文件（handoff §7 目录边界）。
        /// </summary>
        public static void AttachTo(MilResourceProvider provider, TextRenderer renderer)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));

            GlyphFaceCensus.NoteEntry("TextRenderer.AttachTo（被挂到 provider 上）");
            provider.GlyphRunRenderer = renderer.DrawResource;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (SKFont font in _fontCache.Values) font.Dispose();
            _fontCache.Clear();
            _measurePaint.Dispose();
            _fonts.Dispose();
        }

        private bool TryGetFont(GlyphRunRequest request, out SKFont font)
            => TryGetFont(request, null, out font);

        /// <summary>
        /// 取字体：<paramref name="preferredFace"/> 非空时**优先用它**（债务 #14：面由 run 决定），
        /// 否则走今天的族名解析（`FontSet.TryResolve`）。**解析失败返回 false、不抛**。
        /// </summary>
        private bool TryGetFont(GlyphRunRequest request, SKTypeface preferredFace, out SKFont font)
        {
            SKTypeface typeface = preferredFace;
            if (typeface == null &&
                (!_fonts.TryResolve(request.Font, out typeface) || typeface == null))
            {
                font = null;
                return false;
            }

            var key = (typeface.Handle, request.FontSize);
            if (_fontCache.TryGetValue(key, out font) && font != null) return true;

            font = new SKFont(typeface, request.FontSize);
            _fontCache[key] = font;
            return true;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TextRenderer));
        }
    }
}
