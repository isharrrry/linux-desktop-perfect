// L1 · 纯 3D 光栅器（最小可用面）
//
// 【它补的是哪一半】
//   命令层早已就绪：0x57–0x6b（21 条 3D 资源）与 0x29–0x30（8 条 3D 视觉树）都在
//   `Commands/MilCommandDispatcher.cs` 里解码，状态写进 `MilResource3DTable`
//   （宿主是占位 `MilResource`，解码结果是挂在它上面的 `MilResource3D` 派生对象）。
//   但**此前没有任何渲染者读它**：`WpfGfx/Rendering/` 下没有 3D 文件 ⇒ 3D 视口永远空白
//   （实测症状：hc:CoverFlow 页 colors=1、hc:FlipClock 页 colors=3）。
//
// 【本文件做什么】
//   `MilViewport3DVisual`（相机/Viewport/3D 子节点）→ 递归 `MilVisual3D` 子树 →
//   `MilModel3DGroup`/`MilGeometryModel3D`/`MilMeshGeometry3D`(positions+triangleIndices) →
//   合成 4×4（`MilMatrix4x4F`，即上游 `D3DMATRIX`：16 个 float）→ `PerspectiveCamera` 投影 →
//   按相机空间深度排序 → Skia 填三角。调用方（`SkiaRenderBackend.RenderVisual`）已把
//   `deviceBase · world` 设进 canvas，所以这里按 **DIP 局部坐标** 在 `Viewport` 矩形内绘制。
//
// 【已知偏差（如实登记，不许假装成功）】
//   · **无 Z-buffer**：按三角形质心深度排序（远的先画）⇒ 相交面/凹面可能排序错。
//   · **未做背面剔除**；**未做近平面逐顶点裁剪**（只整体丢弃 w<=0 的三角形）。
//   · **每帧 CPU 投影 + 排序**，三角形数由 `MaxTriangles` 封顶。
//   · **光照**只做 `AmbientLight` + `DirectionalLight` 的 Lambert 近似；`PointLight`/`SpotLight`
//     只把 Color 当环境项（衰减未实现），并计入 reason。
//   · **材质**只取 `DiffuseMaterial`/`EmissiveMaterial` 的 Color；`HBrush` 未实现（计入 `brush-ignored`）。
//   · **旋转只支持 AxisAngle**；`QuaternionRotation3D` 未实现（计入 `unknown-rotation`，不静默失真）。
//   · **纹理映射未实现** —— `Viewport2DVisual3D`/`ContainerUIElement3D`（2D 内容贴 3D 面）属 L2。

using System;
using System.Collections.Generic;
using System.Numerics;
using SkiaSharp;
using WpfGfx.Linux.Commands;
using WpfGfx.Linux.Contracts;
using WpfGfx.Linux.Interop;
using WpfGfx.Linux.Resources;

namespace WpfGfx.Linux.Rendering
{
    /// <summary>L1：把已解码的 3D 场景状态光栅化成 Skia 三角形。</summary>
    internal static class Skia3DRenderer
    {
        private static int _texSeen;
        private static int _texSeen2;
        private static int _uvSeen;
        private const int MaxDepth = 32;
        private const int MaxTriangles = 20000;

        private struct Triangle
        {
            public Vector3 A, B, C;
            public SKColor Color;

            // ---- L2：2D-on-3D 的纹理贴图 ----
            //   `Viewport2DVisual3D` 的 2D 内容在上游被装进一个 `VisualBrush` 再换进材质
            //   （`Viewport2DVisual3D.cs:340 InternalVisualBrush.Visual = newValue` / `:462 SwapInCyclicBrush`），
            //   所以这里只需：材质 HBrush 是 VisualBrush ⇒ 用 provider.VisualImageResolver 拿离屏 SKImage
            //   （2D 侧 SkiaBrush 就是这么做的），再按 MeshGeometry3D.TextureCoordinates 贴到三角上。
            public SKImage Texture;          // null = 纯色填充
            public bool HasTexture;          // 纹理 + UV 都齐才置位
            public SKPoint TUvA, TUvB, TUvC; // **纹理像素坐标**（已按图像尺寸换算，供 DrawVertices 用）
            public float Depth;              // 相机空间深度（越大越远）
        }

        private sealed class LightInfo
        {
            public Vector3 Ambient;
            public readonly List<(Vector3 Dir, Vector3 Color)> Directionals = new();
        }

        /// <summary>渲染一个 3D 视口；失败时 <paramref name="reason"/> 给如实原因（机读）。</summary>
        public static bool Render(MilResourceProvider provider, MilResourceHandle viewportHandle,
                                  SKCanvas canvas, out string reason)
        {
            reason = null;
            if (canvas == null) { reason = "no-canvas"; return false; }
            if (provider is not MilChannelResourceProvider channelProvider) { reason = "no-channel"; return false; }

            MilChannel ch = channelProvider.Channel;
            MilViewport3DVisual viewport = State<MilViewport3DVisual>(ch, new DUCE.ResourceHandle(viewportHandle.Value));
            if (viewport == null) { reason = "no-viewport-state"; return false; }

            MilPerspectiveCamera camera = State<MilPerspectiveCamera>(ch, viewport.HCamera);
            if (camera == null) { reason = "no-perspective-camera"; return false; }

            MilRect vpr = viewport.Viewport;
            if (!(vpr.Width > 0.5) || !(vpr.Height > 0.5)) { reason = "empty-viewport"; return false; }

            var lights = new LightInfo();
            var tris = new List<Triangle>();
            Collect(ch, viewport.HChild, Matrix4x4.Identity, lights, tris, 0, ref reason, provider);
            if (tris.Count == 0) { reason ??= "no-triangles"; return false; }

            // ---- 相机 ----
            Vector3 eye = ToVector(camera.Position);
            Vector3 look = ToVector(camera.LookDirection);
            if (look.LengthSquared() < 1e-9f) look = new Vector3(0f, 0f, -1f);
            Vector3 up = ToVector(camera.UpDirection);
            if (up.LengthSquared() < 1e-9f) up = new Vector3(0f, 1f, 0f);

            float near = (float)camera.NearPlaneDistance;
            if (!(near > 0f)) near = 0.125f;
            float far = (float)camera.FarPlaneDistance;
            if (!(far > near)) far = near + 1000f;

            float aspect = (float)(vpr.Width / vpr.Height);
            if (!(aspect > 0f)) aspect = 1f;

            // WPF 的 PerspectiveCamera.FieldOfView 是**水平**视场角（度）⇒ 先换算成垂直 FOV。
            double fovX = camera.FieldOfView;
            if (!(fovX > 0.5) || fovX >= 179.5) fovX = 45.0;
            float fovY = 2f * MathF.Atan(MathF.Tan((float)(fovX * Math.PI / 360.0)) / aspect);
            if (!(fovY > 1e-3f) || fovY >= MathF.PI - 1e-3f) fovY = 0.7853982f;

            Matrix4x4 view = Matrix4x4.CreateLookAt(eye, eye + look, up);
            Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(fovY, aspect, near, far);
            Matrix4x4 viewProj = view * proj;          // 行向量约定：v · view · proj

            // ---- 投影 + 排序 + 绘制 ----
            var visible = new List<Triangle>(tris.Count);
            for (int i = 0; i < tris.Count; i++)
            {
                Triangle t = tris[i];
                Vector4 ca = Vector4.Transform(new Vector4(t.A, 1f), viewProj);
                Vector4 cb = Vector4.Transform(new Vector4(t.B, 1f), viewProj);
                Vector4 cc = Vector4.Transform(new Vector4(t.C, 1f), viewProj);
                if (ca.W <= 1e-4f || cb.W <= 1e-4f || cc.W <= 1e-4f) continue;   // 近平面整体剔除

                t.Depth = (ca.W + cb.W + cc.W) / 3f;
                t.A = ToViewport(ca, vpr);
                t.B = ToViewport(cb, vpr);
                t.C = ToViewport(cc, vpr);
                visible.Add(t);
            }

            if (visible.Count == 0) { reason = "all-clipped"; return false; }

            visible.Sort(static (x, y) => y.Depth.CompareTo(x.Depth));   // 远的先画

            if (Environment.GetEnvironmentVariable("WPFGFX_M3D_DIAG") == "1")
            {
                Console.Error.WriteLine($"[M3D-DIAG] vpr=({vpr.X},{vpr.Y},{vpr.Width},{vpr.Height}) tris={tris.Count} visible={visible.Count}");
                Triangle t0 = visible[0];
                Console.Error.WriteLine($"[M3D-DIAG] tri0=({t0.A.X:F1},{t0.A.Y:F1}) ({t0.B.X:F1},{t0.B.Y:F1}) ({t0.C.X:F1},{t0.C.Y:F1}) color=#{t0.Color.Red:X2}{t0.Color.Green:X2}{t0.Color.Blue:X2} depth={t0.Depth:F2}");
                Console.Error.WriteLine($"[M3D-DIAG] clipLocal={canvas.LocalClipBounds} clipDevice={canvas.DeviceClipBounds} matrixScale={canvas.TotalMatrix.ScaleX:F3}");
            }

            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            using var path = new SKPath();
            // 纹理 shader 按图像缓存（同一页里多张三角常共用一张图；逐三角新建会慢且易漏 Dispose）。
            Dictionary<IntPtr, (SKImage Image, SKShader Shader)> shaderCache = new();
            int textured = 0, texturedFails = 0;
            try
            {
                for (int i = 0; i < visible.Count; i++)
                {
                    Triangle t = visible[i];

                    if (t.HasTexture && t.Texture != null)
                    {
                        // ---- L2：纹理三角 ----
                        //   一次 DrawVertices 只画这一个三角：`texs` 用**像素坐标**、shader 用 Identity，
                        //   于是 Skia 直接按顶点纹理坐标采样 —— 免去"解 UV→屏幕仿射矩阵"那一步。
                        //   ⚠️ 已知偏差：Skia 的顶点纹理是**仿射插值**，透视正确需要切分/细分；
                        //   本层先做"能用 + 视觉可接受"，不假装透视正确（见文件头 L2 说明）。
                        IntPtr key = t.Texture.Handle;
                        if (!shaderCache.TryGetValue(key, out var entry))
                        {
                            SKShader sh = SKShader.CreateImage(t.Texture, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
                            entry = (t.Texture, sh);
                            shaderCache[key] = entry;
                        }
                        using var texPaint = new SKPaint { IsAntialias = true, Shader = entry.Shader };
                        texPaint.Color = t.Color;   // 顶点色（已含光照）⇒ 用 Modulate 与纹理相乘
                        using SKVertices verts = SKVertices.CreateCopy(
                            SKVertexMode.Triangles,
                            new[] { new SKPoint(t.A.X, t.A.Y), new SKPoint(t.B.X, t.B.Y), new SKPoint(t.C.X, t.C.Y) },
                            new[] { t.TUvA, t.TUvB, t.TUvC },
                            null);
                        if (verts == null) { texturedFails++; }
                        else { canvas.DrawVertices(verts, SKBlendMode.Modulate, texPaint); textured++; }
                        continue;
                    }

                    paint.Color = t.Color;
                    path.Reset();
                    path.MoveTo(t.A.X, t.A.Y);
                    path.LineTo(t.B.X, t.B.Y);
                    path.LineTo(t.C.X, t.C.Y);
                    path.Close();
                    canvas.DrawPath(path, paint);
                }
            }
            finally
            {
                foreach (var kv in shaderCache) kv.Value.Shader.Dispose();
            }

            if (Environment.GetEnvironmentVariable("WPFGFX_M3D_DIAG") == "1")
                Console.Error.WriteLine($"[M3D-DIAG] textured={textured} texturedFails={texturedFails} shaders={shaderCache.Count}");

            return true;
        }

        private static Vector3 ToViewport(Vector4 clip, MilRect vpr)
        {
            float invW = 1f / clip.W;
            float ndcX = clip.X * invW;
            float ndcY = clip.Y * invW;
            float x = (float)vpr.X + (ndcX * 0.5f + 0.5f) * (float)vpr.Width;
            float y = (float)vpr.Y + (0.5f - ndcY * 0.5f) * (float)vpr.Height;   // NDC +Y 朝上 ⇒ 屏幕翻转
            return new Vector3(x, y, 0f);
        }

        /// <summary>句柄 → 3D 解码状态（宿主 MilResource → 附加表）。</summary>
        private static T State<T>(MilChannel ch, DUCE.ResourceHandle handle) where T : MilResource3D
        {
            if (handle.IsNull) return null;
            MilResource r = ch.Resources.Lookup(handle);
            return r == null ? null : MilResource3DTable.Lookup<T>(r);
        }

        private static bool Exists(MilChannel ch, DUCE.ResourceHandle handle)
            => !handle.IsNull && ch.Resources.Lookup(handle) != null;

        private static void Collect(MilChannel ch, DUCE.ResourceHandle handle, Matrix4x4 world,
                                    LightInfo lights, List<Triangle> tris, int depth, ref string reason,
                                    MilResourceProvider provider)
        {
            if (handle.IsNull || depth > MaxDepth || tris.Count >= MaxTriangles) return;

            if (State<MilVisual3D>(ch, handle) is { } visual)
            {
                Matrix4x4 w = world * ResolveTransform(ch, visual.HTransform, ref reason, 0);
                CollectContent(ch, visual.HContent, w, lights, tris, depth, ref reason, provider);
                for (int i = 0; i < visual.Children.Count; i++)
                {
                    Collect(ch, visual.Children[i], w, lights, tris, depth + 1, ref reason, provider);
                }
                return;
            }

            CollectContent(ch, handle, world, lights, tris, depth, ref reason, provider);
        }

        private static void CollectContent(MilChannel ch, DUCE.ResourceHandle handle, Matrix4x4 world,
                                           LightInfo lights, List<Triangle> tris, int depth, ref string reason,
                                           MilResourceProvider provider)
        {
            if (handle.IsNull || depth > MaxDepth || tris.Count >= MaxTriangles) return;

            if (State<MilModel3DGroup>(ch, handle) is { } group)
            {
                Matrix4x4 w = world * ResolveTransform(ch, group.HTransform, ref reason, 0);
                for (int i = 0; i < group.Children.Count; i++)
                {
                    CollectContent(ch, group.Children[i], w, lights, tris, depth + 1, ref reason, provider);
                }
                return;
            }

            if (State<MilGeometryModel3D>(ch, handle) is { } model)
            {
                Matrix4x4 w = world * ResolveTransform(ch, model.HTransform, ref reason, 0);
                MilMeshGeometry3D mesh = State<MilMeshGeometry3D>(ch, model.HGeometry);
                if (mesh == null) { reason ??= "no-mesh"; return; }

                Vector3 color = ResolveMaterialColor(ch, model.HMaterial, ref reason);
                // ---- L2：材质若是 VisualBrush（`Viewport2DVisual3D` 的 2D 内容就是这么装进去的）
                //      就取它的离屏位图当纹理 ⇒ 三角按 TextureCoordinates 贴图。 ----
                SKImage texture = ResolveMaterialTexture(ch, model.HMaterial, provider, ref reason);
                AddTriangles(mesh, w, color, lights, texture, tris);
                return;
            }

            if (State<MilAmbientLight>(ch, handle) is { } ambient)
            {
                lights.Ambient += new Vector3(ambient.Color.R, ambient.Color.G, ambient.Color.B);
                return;
            }

            if (State<MilDirectionalLight>(ch, handle) is { } directional)
            {
                Vector3 dir = NormalizeOr(ToVector(directional.Direction), new Vector3(0f, 0f, -1f));
                lights.Directionals.Add((dir, new Vector3(directional.Color.R, directional.Color.G, directional.Color.B)));
                return;
            }

            if (State<MilPointLight>(ch, handle) is { } point)
            {
                lights.Ambient += new Vector3(point.Color.R, point.Color.G, point.Color.B) * 0.25f;
                reason ??= "point-light-approx";
                return;
            }

            if (State<MilSpotLight>(ch, handle) is { } spot)
            {
                lights.Ambient += new Vector3(spot.Color.R, spot.Color.G, spot.Color.B) * 0.25f;
                reason ??= "spot-light-approx";
                return;
            }

            if (Exists(ch, handle)) reason ??= "unknown-3d-content";
        }

        private static void AddTriangles(MilMeshGeometry3D mesh, Matrix4x4 world, Vector3 color,
                                         LightInfo lights, SKImage texture, List<Triangle> tris)
        {
            List<int> idx = mesh.TriangleIndices;
            List<MilPoint3F> pos = mesh.Positions;
            if (idx.Count < 3 || pos.Count < 3) return;

            // UV 只有在"每个顶点都有一份"时才能用（WPF 允许 TextureCoordinates 为空或与 Positions 等长）。
            List<MilPoint> uvs = mesh.TextureCoordinates;
            // ⚠ WPF 两种形态都合法：UV 与 Positions 等长（每顶点一份）**或**与 TriangleIndices 等长
            //   （每索引一份）。实测 `hc:CoverFlow` 的 mesh 是 **pos=4 / uv=6 / idx=6** ⇒ 必须支持按索引取。
            bool uvByVertex = texture != null && uvs.Count == pos.Count;
            bool uvByIndex = texture != null && uvs.Count == idx.Count;
            bool uvOk = uvByVertex || uvByIndex;
            if (Environment.GetEnvironmentVariable("WPFGFX_M3D_DIAG") == "1" && _uvSeen < 6)
            {
                _uvSeen++;
                string _l2 = $"[M3D-UV] pos={pos.Count} uv={uvs.Count} idx={idx.Count} tex={(texture == null ? "NULL" : "ok")} uvOk={uvOk}";
                Console.Error.WriteLine(_l2);
                try { System.IO.File.AppendAllText("/tmp/m3d-uv.log", _l2 + "\n"); } catch { }
            }

            for (int i = 0; i + 2 < idx.Count; i += 3)
            {
                int i0 = idx[i], i1 = idx[i + 1], i2 = idx[i + 2];
                if ((uint)i0 >= (uint)pos.Count || (uint)i1 >= (uint)pos.Count || (uint)i2 >= (uint)pos.Count) continue;

                Vector3 a = Vector3.Transform(ToVector(pos[i0]), world);
                Vector3 b = Vector3.Transform(ToVector(pos[i1]), world);
                Vector3 c = Vector3.Transform(ToVector(pos[i2]), world);

                Vector3 n = NormalizeOr(Vector3.Cross(b - a, c - a), new Vector3(0f, 0f, 1f));
                var tri = new Triangle { A = a, B = b, C = c, Color = Shade(color, n, lights) };
                if (uvOk)
                {
                    // `TextureCoordinates` 是归一化 UV（原点左上，与 Skia 一致）⇒ 乘图像尺寸换成像素坐标，
                    // 这样 DrawVertices 的 texs 与 `SKShader.CreateImage`(默认矩阵) 直接对齐。
                    tri.Texture = texture;
                    tri.HasTexture = true;
                    int ui0 = uvByVertex ? i0 : i, ui1 = uvByVertex ? i1 : i + 1, ui2 = uvByVertex ? i2 : i + 2;
                    if (ui2 < uvs.Count)
                    {
                        tri.TUvA = new SKPoint((float)(uvs[ui0].X * texture.Width), (float)(uvs[ui0].Y * texture.Height));
                        tri.TUvB = new SKPoint((float)(uvs[ui1].X * texture.Width), (float)(uvs[ui1].Y * texture.Height));
                        tri.TUvC = new SKPoint((float)(uvs[ui2].X * texture.Width), (float)(uvs[ui2].Y * texture.Height));
                    }
                    else { tri.HasTexture = false; }
                }
                tris.Add(tri);
                if (tris.Count >= MaxTriangles) return;
            }
        }

        /// <summary>最小光照：环境项 + 方向光 Lambert（双面，避免整片全黑）。</summary>
        private static SKColor Shade(Vector3 baseColor, Vector3 normal, LightInfo lights)
        {
            Vector3 lit = lights.Ambient;
            for (int i = 0; i < lights.Directionals.Count; i++)
            {
                (Vector3 dir, Vector3 col) = lights.Directionals[i];
                float d = Vector3.Dot(normal, -dir);
                if (d < 0f) d = -d;
                lit += col * d;
            }
            if (lit.LengthSquared() < 1e-6f) lit = Vector3.One;   // 无灯 ⇒ 按本征色平涂（不装黑）

            float r = Math.Clamp(baseColor.X * lit.X, 0f, 1f);
            float g = Math.Clamp(baseColor.Y * lit.Y, 0f, 1f);
            float b = Math.Clamp(baseColor.Z * lit.Z, 0f, 1f);
            return new SKColor((byte)(r * 255f + 0.5f), (byte)(g * 255f + 0.5f), (byte)(b * 255f + 0.5f), 255);
        }

        /// <summary>
        /// L2：材质 → 纹理（`Viewport2DVisual3D` 的 2D 内容路径）。
        /// 上游把 2D 视觉装进 `VisualBrush` 换进材质（`Viewport2DVisual3D.cs:340/:462`），
        /// 所以这里只需把该 VisualBrush 的 `Visual` 交给 provider 的视觉扩展点拿离屏位图
        /// （与 2D 侧 `SkiaBrush` 的 VisualBrush 链路同源，不另写一套渲染）。
        /// 返回 null = 该材质没有可用纹理（调用方退回纯色）。
        /// </summary>
        private static SKImage ResolveMaterialTexture(MilChannel ch, DUCE.ResourceHandle hMaterial,
                                                      MilResourceProvider provider, ref string reason)
        {
            if (provider?.VisualImageResolver == null || hMaterial.IsNull) return null;
            if (State<MilDiffuseMaterial>(ch, hMaterial) is not { } diffuse || diffuse.HBrush.IsNull) return null;

            object brush = ch.Resources.Lookup(diffuse.HBrush);
            if (Environment.GetEnvironmentVariable("WPFGFX_M3D_DIAG") == "1" && _texSeen < 12)
            {
                _texSeen++;
                string _t = brush?.GetType().Name ?? "<null>";
                string _vis = brush is MilVisualBrush _vb ? (_vb.Visual.IsNull ? "vis=null" : $"vis=0x{_vb.Visual.Value:x}") : "-";
                string _line = $"[M3D-TEX] #{_texSeen} hBrush=0x{diffuse.HBrush.Value:x} type={_t} {_vis} resolver={(provider?.VisualImageResolver != null ? "yes" : "NO")}";
                Console.Error.WriteLine(_line);
                try { System.IO.File.AppendAllText("/tmp/m3d-tex.log", _line + "\n"); } catch { }
            }
            switch (brush)
            {
                case MilVisualBrush vb when !vb.Visual.IsNull:
                    // 走 2D 侧的**正规入口**（含递归防护），不要裸调 provider.VisualImageResolver：
                    // 两者的深度/环闸语义必须一致，否则 3D→2D 的正常嵌套会被判成自引用。
                    SKImage img = SkiaBrush.TryResolveVisualImage(provider, new MilResourceHandle((uint)vb.Visual.Value));
                    if (Environment.GetEnvironmentVariable("WPFGFX_M3D_DIAG") == "1" && _texSeen2 < 6)
                    {
                        _texSeen2++;
                        string _l = $"[M3D-TEX2] vis=0x{vb.Visual.Value:x} img={(img == null ? "NULL" : $"{img.Width}x{img.Height}")}";
                        Console.Error.WriteLine(_l);
                        try { System.IO.File.AppendAllText("/tmp/m3d-tex2.log", _l + "\n"); } catch { }
                    }
                    if (img != null) return img;
                    reason ??= "visual-brush-no-image";
                    return null;

                case MilImageBrush ib when !ib.ImageSource.IsNull:
                    SKBitmap bmp = provider.LookupBitmap(new MilResourceHandle((uint)ib.ImageSource.Value));
                    if (bmp == null) { reason ??= "image-brush-no-bitmap"; return null; }
                    return SKImage.FromBitmap(bmp);

                default:
                    return null;   // 纯色/渐变等仍走 ResolveMaterialColor
            }
        }

        private static Vector3 ResolveMaterialColor(MilChannel ch, DUCE.ResourceHandle handle, ref string reason)
        {
            if (Environment.GetEnvironmentVariable("WPFGFX_M3D_DIAG") == "1")
            {
                MilResource host = handle.IsNull ? null : ch.Resources.Lookup(handle);
                Console.Error.WriteLine($"[M3D-DIAG] material handle=0x{handle.Value:x} null={handle.IsNull} host={host?.GetType().Name ?? "<none>"} hostType={host?.Type}");
            }

            if (State<MilDiffuseMaterial>(ch, handle) is { } diffuse)
            {
                // ⚠ 实测（探针 + 诊断）：`new DiffuseMaterial(Brushes.Red)` 落到命令层时
                //   `Color` 字段仍是**默认白 (1,1,1,1)**，真色在 `HBrush` 指向的画刷里
                //   （hostType=TYPE_DIFFUSEMATERIAL / HBrush 非空 / Color=白 ⇒ 三角被画成白色）。
                //   所以**必须先解析画刷**，再退回 Color 字段。
                if (!diffuse.HBrush.IsNull)
                {
                    if (ch.Resources.Lookup(diffuse.HBrush) is MilSolidColorBrush solid)
                    {
                        MilColorF sc = solid.Color;
                        float sa = (float)Math.Clamp(solid.Opacity, 0.0, 1.0);
                        return new Vector3(sc.R * sa, sc.G * sa, sc.B * sa);
                    }
                    reason ??= "brush-ignored";
                }
                return new Vector3(diffuse.Color.R, diffuse.Color.G, diffuse.Color.B);
            }

            if (State<MilMaterialGroup>(ch, handle) is { } group)
            {
                if (Environment.GetEnvironmentVariable("WPFGFX_M3D_DIAG") == "1")
                    Console.Error.WriteLine($"[M3D-DIAG] material=group children={group.Children.Count}");
                for (int i = 0; i < group.Children.Count; i++)
                {
                    Vector3 c = ResolveMaterialColor(ch, group.Children[i], ref reason);
                    if (c.LengthSquared() > 0f) return c;
                }
                reason ??= "empty-material-group";
                return Vector3.One;
            }

            if (State<MilEmissiveMaterial>(ch, handle) is { } emissive)
            {
                return new Vector3(emissive.Color.R, emissive.Color.G, emissive.Color.B);
            }

            if (State<MilSpecularMaterial>(ch, handle) is { } specular)
            {
                reason ??= "specular-as-diffuse";
                return new Vector3(specular.Color.R, specular.Color.G, specular.Color.B);
            }

            reason ??= handle.IsNull ? "no-material" : "unknown-material";
            return Vector3.One;
        }

        /// <summary>Transform3D 句柄 → 4×4；类型不认时按单位阵并记账（不静默失真）。</summary>
        private static Matrix4x4 ResolveTransform(MilChannel ch, DUCE.ResourceHandle handle,
                                                  ref string reason, int depth)
        {
            if (handle.IsNull || depth > 16) return Matrix4x4.Identity;

            if (State<MilMatrixTransform3D>(ch, handle) is { } m) return FromMil(m.Matrix);

            if (State<MilTranslateTransform3D>(ch, handle) is { } t)
                return Matrix4x4.CreateTranslation((float)t.OffsetX, (float)t.OffsetY, (float)t.OffsetZ);

            if (State<MilScaleTransform3D>(ch, handle) is { } s)
            {
                Vector3 center = new((float)s.CenterX, (float)s.CenterY, (float)s.CenterZ);
                Matrix4x4 scale = Matrix4x4.CreateScale((float)s.ScaleX, (float)s.ScaleY, (float)s.ScaleZ);
                return Matrix4x4.CreateTranslation(-center) * scale * Matrix4x4.CreateTranslation(center);
            }

            if (State<MilTransform3DGroup>(ch, handle) is { } group)
            {
                Matrix4x4 acc = Matrix4x4.Identity;
                for (int i = 0; i < group.Children.Count; i++)
                {
                    acc *= ResolveTransform(ch, group.Children[i], ref reason, depth + 1);
                }
                return acc;
            }

            if (State<MilRotateTransform3D>(ch, handle) is { } rot)
            {
                Vector3 center = new((float)rot.CenterX, (float)rot.CenterY, (float)rot.CenterZ);
                Matrix4x4 r = Matrix4x4.Identity;
                if (State<MilAxisAngleRotation3D>(ch, rot.HRotation) is { } axis)
                {
                    r = Matrix4x4.CreateFromAxisAngle(
                        NormalizeOr(ToVector(axis.Axis), new Vector3(0f, 0f, 1f)),
                        (float)(axis.Angle * Math.PI / 180.0));
                }
                else
                {
                    // QuaternionRotation3D 未实现（L1 范围外）：如实记账，不假装转了
                    reason ??= "unknown-rotation";
                }
                return Matrix4x4.CreateTranslation(-center) * r * Matrix4x4.CreateTranslation(center);
            }

            if (Exists(ch, handle)) reason ??= "unknown-transform";
            return Matrix4x4.Identity;
        }

        private static Matrix4x4 FromMil(MilMatrix4x4F m) => new(
            m.M11, m.M12, m.M13, m.M14,
            m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34,
            m.M41, m.M42, m.M43, m.M44);

        private static Vector3 ToVector(MilPoint3F p) => new(p.X, p.Y, p.Z);

        private static Vector3 NormalizeOr(Vector3 v, Vector3 fallback)
            => v.LengthSquared() > 1e-9f ? Vector3.Normalize(v) : fallback;
    }
}
