using SkiaSharp;
using WpfGfx.Linux.Interop;

namespace WpfGfx.Linux.Contracts;

/// <summary>
/// 命令分发器：把解码后的 DUCE 命令作用到资源表与视觉树上。
/// <para>由 mil-core 组实现。skia-render 组只读本接口，不得实现。</para>
/// </summary>
internal interface IMilCommandDispatcher
{
    /// <summary>分发一条顶层命令（MilCmd，118 条）。命令体含命令头与载荷。</summary>
    int Dispatch(ReadOnlySpan<byte> command, IMilChannel channel);

    /// <summary>该命令是否已实现。未实现的命令返回 E_NOTIMPL 并登记。</summary>
    bool IsImplemented(MilCmd cmd);
}

/// <summary>
/// MIL 通道。承载批处理缓冲与资源句柄表。
/// <para>由 mil-core 组实现。</para>
/// </summary>
internal interface IMilChannel
{
    nint Handle { get; }
    MilChannelMarshalType MarshalType { get; }

    void BeginCommand(ReadOnlySpan<byte> header, uint cbExtra);
    void AppendCommandData(ReadOnlySpan<byte> data);
    void EndCommand();
    int Commit();

    IMilResourceTable Resources { get; }
    MilVisual? Root { get; set; }
}

/// <summary>
/// 资源句柄表：MIL 资源的创建 / 引用计数 / 释放。
/// <para>由 mil-core 组实现。</para>
/// </summary>
internal interface IMilResourceTable
{
    MilResourceHandle Create(MilResourceType type, out object resource);
    object? Lookup(MilResourceHandle handle);
    int AddRef(MilResourceHandle handle);
    int Release(MilResourceHandle handle, out bool deleted);
    void DestroyAll();
}

/// <summary>
/// 视觉树节点。渲染层遍历它产出绘制指令。
/// </summary>
internal sealed class MilVisual
{
    public MilResourceHandle Handle { get; init; }

    /// <summary>相对父节点的偏移。</summary>
    public SKPoint Offset { get; set; }

    /// <summary>本地变换矩阵。</summary>
    public SKMatrix Transform { get; set; } = SKMatrix.Identity;

    /// <summary>不透明度 [0,1]。</summary>
    public double Opacity { get; set; } = 1.0;

    /// <summary>裁剪矩形（空表示不裁剪）。</summary>
    public SKRect? Clip { get; set; }

    /// <summary>本节点的绘制内容（RenderData 解码结果）。null 表示无内容。</summary>
    public IMilRenderData? Content { get; set; }

    public List<MilVisual> Children { get; } = new();

    /// <summary>内容资源句柄（投影时记录；脏区判定用。0 = 无内容）。</summary>
    public uint ContentHandle { get; init; }

    /// <summary>
    /// 投影时刻的**绘制维**版本戳：内容资源 + 内容指令里引用的画刷/画笔/几何 + 遮罩/效果
    /// 的 `MilResource.Version` 加权和。
    /// <para>
    /// 脏区判定把它与**几何维**（offset/transform/clip/alpha 这些投影后的**值**）分开比较：
    /// 只有绘制维变（例如 GIF 换了 `ImageBrush.Source`）⇒ 几何没动 ⇒ 只需重画该视觉自己的
    /// 包围盒；几何维一变（位移/缩放/裁剪）⇒ 保守整窗（避免旧位置残影与新位置被裁）。
    /// </para>
    /// ⚠ 只是比较用的数值，不参与任何协议/线格对齐。
    /// </summary>
    public long SrcPaintStamp { get; init; }

    /// <summary>累积的世界矩阵（父矩阵 × 本地变换 × 偏移）。由渲染层在遍历时计算。</summary>
    public SKMatrix WorldTransform { get; set; } = SKMatrix.Identity;

    /// <summary>
    /// 上游 RenderOptions（含 BitmapScalingMode / EdgeMode / CompositingMode 等）。
    /// <para>【T2b 新增】此前 `MilVisualNode.RenderOptions` 在投影到契约层时被**丢掉**，
    /// 于是 `BitmapScalingMode` 永远到不了画刷层（渲染层 grep 该名字 0 命中）。
    /// 默认 = <see cref="MilRenderOptions.Default"/> = 全零 = <c>BitmapScalingMode.Unspecified</c>
    /// （真机实测 `Unspecified ≡ Linear`）。</para>
    /// <para>⚠ 本类型是**纯托管类**（不是线格结构体），加字段不影响与上游逐
    /// <c>FieldOffset</c> 对齐的命令体布局 —— 已用 verify-cmd-layout.py 核过。</para>
    /// </summary>
    public MilRenderOptions RenderOptions { get; set; } = MilRenderOptions.Default;

    /// <summary>
    /// 视觉不透明度遮罩（`MilCmdVisualSetAlphaMask` = 0x23 的载荷）。
    /// <para>语义与上游 `UIElement.OpacityMask` 相同：**作用于整个视觉子树**。</para>
    /// <para>【T2b 新增】此前 `MilVisualNode.AlphaMask` 在投影到契约层时被**丢掉**，
    /// 于是 `grep AlphaMask src/`（排除 Commands/Contracts/Resources）= **0 命中** ⇒
    /// 遮罩被**静默忽略**（探针那三个 Border 的 `alpha=0` 遮罩完全不起作用）。
    /// 这是本项目"投影时丢字段"这一族的**第三个**实例（前两个：`PIDWriteFont`、变换资源句柄）。</para>
    /// </summary>
    public MilResourceHandle AlphaMask { get; set; }

    /// <summary>
    /// 非空 ⇒ 该 2D 视觉是 3D 视口（`TYPE_VIEWPORT3DVISUAL`），值为视口资源句柄；
    /// 渲染层据此调用 <c>Skia3DRenderer</c>。纯 2D 视觉为 null（默认）。
    /// </summary>
    public MilResourceHandle? Viewport3D { get; set; }

    /// <summary>
    /// 视觉级效果（由 MilCmdVisualSetEffect 下发；MilVisualNode.Effect 是唯一写入点）。
    /// 句柄为空 = 无效果。此前该字段在投影那一跳就丢了，挂在视觉上的 Effect 从未被画
    /// （D-G71 / W70 的台账 NoteVisualEffectIgnored 记的就是这件事）。
    /// </summary>
    public MilResourceHandle? Effect { get; set; }
}

/// <summary>
/// 一段已解码的 RenderData 绘制指令序列。
/// <para>mil-core 负责解码出结构，skia-render 负责执行。</para>
/// </summary>
internal interface IMilRenderData
{
    IReadOnlyList<MilDrawInstruction> Instructions { get; }
}

/// <summary>
/// 单条绘图指令（对应 MilDrawCommand 25 条）。
/// 载荷以强类型形式携带，避免渲染层再碰字节。
/// </summary>
internal sealed class MilDrawInstruction
{
    public MilDrawCommand Command { get; init; }

    /// <summary>画刷资源句柄（填充/描边用）。</summary>
    public MilResourceHandle Brush { get; init; }

    /// <summary>画笔资源句柄（描边用）。</summary>
    public MilResourceHandle Pen { get; init; }

    /// <summary>几何资源句柄（路径绘制用）。</summary>
    public MilResourceHandle Geometry { get; init; }

    /// <summary>矩形类指令的几何。</summary>
    public SKRect Rect { get; init; }

    /// <summary>圆角矩形。</summary>
    public SKPoint CornerRadius { get; init; }

    /// <summary>线条端点。</summary>
    public SKPoint Point0 { get; init; }
    public SKPoint Point1 { get; init; }

    /// <summary>PushOpacity 的透明度值。</summary>
    public double Opacity { get; init; } = 1.0;

    /// <summary>PushTransform 的矩阵。</summary>
    public SKMatrix Matrix { get; init; } = SKMatrix.Identity;
}

/// <summary>
/// 渲染后端：把视觉树真正画到 Skia 画布上。
/// <para>由 skia-render 组实现。这是渲染的正确性核心。</para>
/// </summary>
internal interface IRenderBackend
{
    void RenderVisualTree(MilVisual root, SKCanvas canvas, RenderContext ctx);

    /// <summary>脏区裁剪版：`dirtyClip` 有值时只清/只画该矩形内的像素（矩形外保留上一帧）。</summary>
    void RenderVisualTree(MilVisual root, SKCanvas canvas, RenderContext ctx, SKRectI? dirtyClip);
    void Invalidate();
}

/// <summary>
/// 呈现目标：窗口或离屏表面。
/// <para>由 window-text 组实现（X11）与 test-harness 组实现（离屏）。</para>
/// </summary>
internal interface IPresentationTarget
{
    nint NativeHandle { get; }
    int Width { get; }
    int Height { get; }

    void Resize(int w, int h);
    void Present(SKImage frame);
}

/// <summary>
/// 渲染上下文。承载确定性渲染所需的全部环境参数。
/// <para>注意：golden image 测试依赖这些值固定，不得在运行期漂移。</para>
/// </summary>
internal sealed class RenderContext
{
    /// <summary>固定 96 DPI，保证 golden 可复现。</summary>
    public const float FixedDpi = 96f;

    public int Width { get; init; }
    public int Height { get; init; }
    public float Dpi { get; init; } = FixedDpi;

    /// <summary>背景色。</summary>
    public SKColor ClearColor { get; init; } = SKColors.White;

    /// <summary>字体目录。测试时必须指向打包字体，禁止用系统字体。</summary>
    public string FontDirectory { get; init; } = "fonts";

    /// <summary>抗锯齿开关。golden 生成与比对必须一致。</summary>
    public bool Antialias { get; init; } = true;
}
