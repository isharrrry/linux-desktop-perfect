// Licensed to the .NET Foundation under one or more agreements.
//
// M7c · 接窗与端到端呈现：把「DUCE 通道」接到「真实 X11 窗口」上。
//
// ── 这条链路原来缺的是哪两环 ───────────────────────────────────────────────
//   M7a 把 `MilVisualTarget_AttachToHwnd(hwnd)` 实现成**纯身份登记**
//   （MilHwndRegistry：登记/查表/解绑/幂等/句柄不复用），`WgxConnection_SameThreadPresent`
//   只做 `channel.Commit()`。两者都**不产生任何像素**。本文件补上：
//     ① HWND（== X11 XID）→ 呈现目标的绑定；
//     ② 提交之后：渲染该通道的根视觉 → `X11PresentationTarget.Present` → `XSync`。
//
// ── 三个设计决定（都有理由，不是顺手写的）──────────────────────────────────
//
// [1] 为什么**不**复用 Win32 shim（libwpfwin32.so）里那条 X 连接
//     托管层（WindowsBase/PresentationCore）的 X 连接归 shim 所有，是 C 侧的
//     `Display*`；要从托管代码用它就得把裸指针搬来搬去、还要和 shim 的 xlock 协调。
//     而 X11 的 XID 是 **server 全局**的：换一条连接照样能对同一个窗口 XPutImage。
//     所以这里用 `X11Display.Open` 开**本进程的第二条**连接，各管各的：
//       · 谁也不需要把 Display* 暴露给对方；
//       · 不用动 M7b 已验证的 shim（它是"消息泵"，不负责呈现）；
//       · 关掉呈现侧连接不会影响 HwndWrapper 的输入/生命周期。
//     代价：多一条 X 连接（一个 socket），以及窗口销毁的顺序要注意——
//     `DetachFromHwnd` 会释放我们的目标，但**不销毁窗口**（见 X11Window.Wrap）。
//
// [2] 为什么绑定是**尽力而为**、而呈现失败必须报错
//     `MilVisualTarget_AttachToHwnd` 的 HRESULT 语义在 M7a 已被 HwndTarget 依赖
//     （E_ACCESSDENIED ⇒ WindowAlreadyHasContent），而且上游语义里 Attach 只管
//     "占用/登记"，不管"窗口系统是否就绪"。所以：
//       · Attach 里 X11 绑定失败**不改变 HRESULT**，但把原因记进 _bindErrors，
//         并且**不静默**——`WgxConnection_SameThreadPresent` 会把它变成失败码；
//       · 反过来，一旦真的到了"该出像素"的时候（有 HWND 目标 + 有根视觉），
//         任何一环失败都返回失败码，而不是"看起来成功但屏幕没变"。
//     这是本项目"不伪造"原则在呈现路径上的具体化：**登记可以宽容，出图必须诚实**。
//
// [3] 为什么渲染用 `MilChannelResourceProvider` + `SkiaRenderBackend`
//     与 `samples/HelloMil` 的驱动方式完全一致（同一个后端、同一个 provider 类）。
//     不另写一条渲染路径，就不会出现"demo 能画、托管层画不出来"这种分叉。

using System;
using System.Collections.Generic;
using SkiaSharp;
using WpfGfx.Linux.Contracts;
using WpfGfx.Linux.Rendering;
using WpfGfx.Linux.Resources;
using WpfGfx.Linux.Windowing;

namespace WpfGfx.Linux.Interop
{
    /// <summary>
    /// HWND（== X11 XID）↔ 呈现目标 的绑定表，以及「渲染 + 呈现」的实现。
    ///
    /// 线程模型：所有方法都在**UI/呈现线程**上调用（DUCE 的 SameThread 通道语义），
    /// 内部仍用一把锁保护表，避免测试与 Dispatcher 线程交错时读到半个状态。
    /// </summary>
    internal static class MilPresentation
    {
        private static readonly object _gate = new object();

        private static readonly Dictionary<IntPtr, X11PresentationTarget> _targets =
            new Dictionary<IntPtr, X11PresentationTarget>();

        /// <summary>HWND → 绑定失败原因（成功时为 null）。用于把"静默不出图"变成可诊断的失败。</summary>
        private static readonly Dictionary<IntPtr, string> _bindErrors =
            new Dictionary<IntPtr, string>();

        private static X11Display _display;

        // ---- 诊断计数（测试断言用；每帧不清零，Reset() 清）----
        private static long _presentCalls;
        private static long _framesPresented;
        private static long _bindAttempts;
        private static long _bindFailures;

        /// <summary>
        /// **按目标呈现**里"被跳过"的目标数（波46 · `D-G54`）：多目标通道上某个目标
        /// 呈现失败而**至少另一个成功**时，通道提交仍返回 S_OK（口径见
        /// `WAVE46-PERTARGET-PRESENT-DESIGN.md` §4-D6）；这里把"确实有目标没画出来"记账，
        /// 使那条口径**不是静默吞掉失败**。
        /// </summary>
        private static long _targetsSkipped;

        internal static long PresentCalls { get { lock (_gate) return _presentCalls; } }
        internal static long FramesPresented { get { lock (_gate) return _framesPresented; } }
        internal static long BindAttempts { get { lock (_gate) return _bindAttempts; } }
        internal static long BindFailures { get { lock (_gate) return _bindFailures; } }
        internal static long TargetsSkipped { get { lock (_gate) return _targetsSkipped; } }

        // ==================================================================
        //  运行期诊断输出（M7c Phase 2 补）
        // ==================================================================
        // 【为什么需要它】Phase 2 跑真应用时的观测手段只有两个：进程退出码、xwd 截屏。
        //   实测踩到的坑正是这个盲区：窗口**映射出来了**（xwininfo 说 640x400 IsViewable），
        //   但截屏是纯白，而应用日志**一行都没有** —— 于是"呈现链路到底有没有被调到"
        //   完全无法回答：可能是（a）WPF 的渲染 pass 根本没跑、（b）跑了但通道里没有
        //   带 HWND 的目标、（c）渲染了但没画上窗口。这三种情况的修法完全不同。
        //
        //   本类里的 MilDiagnostics.Note / 失败分支原本只把原因记在**进程内的列表**里，
        //   测试能断言、真应用看不到。所以这里加一条 env 开关，把它们**原样**打到 stderr：
        //       WPF_LINUX_MIL_TRACE=1 dotnet HelloWpf.dll
        //   默认关闭、开销为零（一次 env 读取 + 一个 bool 判断），不影响验收路径。
        //   台账有预算上限（见 _traceBudget）：渲染 pass 每帧都调，不设上限会淹掉日志。
        private static readonly bool _trace =
            Environment.GetEnvironmentVariable("WPF_LINUX_MIL_TRACE") == "1";
        private static int _traceBudget = 400;
        private static long _traceSeq;

        // 【文件汇（WPF_LINUX_MIL_LOG=<path>）】`dotnet test` 里 stderr 不一定看得到，
        //   而"哪一处 E_HANDLE"这类定位需要**进程内**的证据。给一个显式文件汇：
        //   设了就把同样的行追加进去（带锁，跨线程安全）。默认不开，开销为零。
        private static readonly string _traceFile =
            Environment.GetEnvironmentVariable("WPF_LINUX_MIL_LOG");
        private static readonly object _fileGate = new object();
        private static bool _fileFailed;

        /// <summary>诊断汇是否开启（`WPF_LINUX_MIL_LOG` 指向文件）。预检等重诊断据此开关。</summary>
        internal static bool DiagnosticSinkEnabled => !string.IsNullOrEmpty(_traceFile);

        private static void TraceToFile(string line)
        {
            if (string.IsNullOrEmpty(_traceFile) || _fileFailed) return;
            try
            {
                lock (_fileGate)
                    System.IO.File.AppendAllText(_traceFile, line + "\n");
            }
            catch { _fileFailed = true; }   // 写不进去就放弃，绝不影响呈现
        }

        /// <summary>把一条诊断写到 stderr（仅当 WPF_LINUX_MIL_TRACE=1）与文件汇。</summary>
        internal static void Trace(string message)
        {
            TraceToFile(message);
            if (!_trace) return;
            lock (_gate)
            {
                if (_traceBudget <= 0)
                {
                    if (_traceBudget == 0)
                    {
                        _traceBudget = -1;
                        try
                        {
                            Console.Error.WriteLine(
                                $"[mil] …诊断预算用尽（已打 {_traceSeq} 条），后续不再输出。" +
                                "完整计数见 MilPresentation.* 属性 / MilDiagnostics.Snapshot()");
                            Console.Error.Flush();
                        }
                        catch { /* 输出失败不影响呈现 */ }
                    }
                    return;
                }
                _traceBudget--;
                _traceSeq++;
            }
            try
            {
                Console.Error.WriteLine($"[mil {_traceSeq,4}] {message}");
                Console.Error.Flush();
            }
            catch { /* 输出失败不影响呈现 */ }
        }

        /// <summary>
        /// 呈现台账的节流：**前 5 次 + 之后每 120 次**，但**首个"有内容"的帧永远记录**。
        /// 【为什么必须例外】实测：提交驱动的第一帧可能是空的（`skia 指令 0 条`，
        /// 这一批只创建了资源），真正有内容的帧出现在之后 —— 而它恰好被节流吞掉，
        /// 于是 runner（以及读日志的人）只看到"空帧"，误判成"什么都没画"。
        /// 内容帧本来就少（一次布局一条），全记也不吵。
        /// </summary>
        private static bool _loggedFirstContentFrame;

        private static bool ShouldTracePresent(long n) => n <= 5 || (n % 120) == 0;

        /// <summary>上一次**已打过台账**的呈现尺寸（按窗口）。用于"尺寸变化免采样"。</summary>
        private static readonly Dictionary<IntPtr, (int W, int H)> _lastLoggedSize =
            new Dictionary<IntPtr, (int W, int H)>();

        /// <summary>
        /// 已打过"按目标呈现"台账的 HWND（波46 · `D-G54`）。
        /// 每个窗口**只打一行**：这一行的用途是"把呈现目标这个选择变成读数"，
        /// 而它是**每进程静态**的（通道/目标不会中途改变），所以不需要也不许按帧打 ——
        /// 按帧打会淹掉日志（本仓反复踩过"仪器把读数吃掉"）。
        /// </summary>
        private static readonly HashSet<IntPtr> _loggedTargetPick = new HashSet<IntPtr>();

        /// <summary>
        /// 呈现结果台账。
        ///
        /// 【为什么"尺寸变化"必须**免采样**】
        ///   本台账默认是抽样的（`n <= 5 || n % 120 == 0`）。抽样的代价在 M2 那轮已经吃过一次：
        ///   首个**有内容**的帧被吞掉，于是"窗口全白"被误判成渲染没跑。
        ///   尺寸变化是**同性质的关键状态转换**（"内容跟着新尺寸重渲了"是债务 #3 的判据本身），
        ///   而它可能只发生一两次、刚好落在采样空档里 —— 抽样会让观测者得出"没重渲"的错误结论。
        ///   所以：首帧（有内容）+ **每次尺寸变化**都无条件打一行。
        /// </summary>
        private static void TracePresentResult(long n, MilChannel channel, IntPtr hwnd, int w, int h,
                                               long drawn, long notDrawn, string notDrawnSummary)
        {
            bool content = drawn > 0;

            bool sizeChanged;
            lock (_gate)
            {
                sizeChanged = !_lastLoggedSize.TryGetValue(hwnd, out (int W, int H) last) ||
                              last.W != w || last.H != h;
                if (sizeChanged) _lastLoggedSize[hwnd] = (w, h);
            }

            if (content && !_loggedFirstContentFrame)
            {
                _loggedFirstContentFrame = true;
                Trace($"  ★ 首个**有内容**的帧（第 {n} 次呈现）：通道 {channel.Id} → HWND 0x{(long)hwnd:x} " +
                      $"{w}x{h}（skia 指令 {drawn} 条，未画种类 {notDrawn}{notDrawnSummary}）" +
                      $"  累计帧数 = {FramesPresented}");
                return;
            }
            if (sizeChanged)
            {
                Trace($"  ★ 呈现尺寸变化（免采样）：通道 {channel.Id} → HWND 0x{(long)hwnd:x} " +
                      $"已呈现 {w}x{h}（skia 指令 {drawn} 条，未画种类 {notDrawn}{notDrawnSummary}）" +
                      $"  累计帧数 = {FramesPresented}");
                return;
            }
            if (!ShouldTracePresent(n)) return;
            Trace($"  通道 {channel.Id} → HWND 0x{(long)hwnd:x} 已呈现 {w}x{h}" +
                  $"（skia 指令 {drawn} 条，未画种类 {notDrawn}{notDrawnSummary}）" +
                  $"  累计帧数 = {FramesPresented}");
        }

        // ==================================================================
        //  通道计数器探针（M7c Phase 2 的诊断仪器）
        // ==================================================================
        // 【它回答的问题】真应用里"窗口出来了但没像素"有两种完全不同的原因：
        //   (H1) WPF 的渲染 pass **根本没跑** —— 命令一条都没提交到通道；
        //   (H2) 渲染 pass 跑了、命令也提交了，只是**没有人把它呈现到 X 窗口**。
        //   两者的修法天差地别（H1 要查资源回调/调度，H2 只要在提交点接上呈现），
        //   而外部观测（截屏、退出码、消息台账）**都区分不了**：两种情况下窗口都是白的。
        //
        // 【为什么能这么读】`MilChannel` 的计数器（`CommittedCommands` / `NotImplCommands`
        //   / `FailedCommands`）就在本程序集里（`WpfGfx.Linux.Resources`），同程序集直接读，
        //   不需要 T1 的反射诊断面。句柄从 `MilChannelRegistry.HandleBase`(0x1000_0000)
        //   **单调 +1 下发、永不复用**（见 Resources/MilChannel.cs 的说明），
        //   所以按序号探测就能枚举出存活通道 —— 只读，不改变任何行为。
        //
        // 【为什么用定时器】真应用里没有任何一处会回头打日志：Attach 之后就再没有
        //   我们的代码被调到。所以起一个 2s 的只读快照，把"命令在不在流动"变成时序证据
        //   （计数器在涨 ⇒ pass 在跑；一直不动 ⇒ pass 没跑）。
        private static System.Threading.Timer _counterTimer;

        private static void StartChannelCounterProbe()
        {
            if (!_trace || _counterTimer != null) return;
            try
            {
                // 250ms：诊断台账要能追上"内容提交完 → 截图"这个窗口（原来 2s 会错过）。
                _counterTimer = new System.Threading.Timer(
                    _ => DumpChannelCounters(), null, 2000, 250);
            }
            catch { /* 起不来就退化成"只有台账"，不影响呈现 */ }
        }

        // 视觉树形状：`子=0 且 内容=null` 说明这棵树**根本没接上内容**
        // （问题在更上游：HwndSource.RootVisual / Window 的内容装配）；
        // 而 `子>0` 却没有绘制指令，说明**渲染 pass 没跑**（问题在渲染调度）。
        // 这两个结论的修法完全不同，所以把形状打出来。
        private static string DescribeVisualTree(MilVisual v, int depth, int maxDepth)
        {
            if (v == null) return "null";
            var sb = new System.Text.StringBuilder();
            sb.Append($"{{子={v.Children.Count} 内容={(v.Content == null ? "无" : "有")} 不透明={v.Opacity:0.##}");
            if (depth < maxDepth)
            {
                for (int i = 0; i < v.Children.Count && i < 8; i++)
                    sb.Append(' ').Append(DescribeVisualTree(v.Children[i], depth + 1, maxDepth));
                if (v.Children.Count > 8) sb.Append($" …还有{v.Children.Count - 8}个");
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static void DumpChannelCounters()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                for (long i = 1; i <= 16; i++)
                {
                    MilChannel ch = MilChannelRegistry.Resolve((IntPtr)(0x1000_0000 + i));
                    if (ch == null) continue;
                    sb.Append($"  通道#{i}: committed={ch.CommittedCommands}")
                      .Append($" notimpl={ch.NotImplCommands}")
                      .Append($" failed={ch.FailedCommands}")
                      .Append($" short={ch.ShortCommands}")
                      .Append($" pending={ch.PendingCommandCount}")
                      .Append($" batchBytes={ch.BatchByteCount}");

                    // 资源表内容 + 根视觉 + 窗口目标：这三样决定"该不该有像素"。
                    // MilTarget.NativeWindow != 0 表示 MilCmdHwndTargetCreate 真的执行过；
                    // Root != null 表示 TargetSetRoot 执行过。
                    try
                    {
                        var kinds = new System.Collections.Generic.Dictionary<string, int>();
                        IntPtr targetHwnd = IntPtr.Zero;
                        int entries = 0;
                        foreach (System.Collections.Generic.KeyValuePair<uint, MilResourceEntry> kv in ch.Resources.Entries)
                        {
                            entries++;
                            MilResource res = kv.Value?.Resource;
                            if (res == null) continue;
                            string n = res.GetType().Name;
                            kinds[n] = kinds.TryGetValue(n, out int c) ? c + 1 : 1;
                            if (res is MilTarget mt && mt.NativeWindow != IntPtr.Zero) targetHwnd = mt.NativeWindow;
                        }
                        var root = (ch as IMilChannel)?.Root;
                        sb.Append($" 资源={entries} root={(root == null ? "无" : "有")}")
                          .Append($" 窗口目标=0x{(long)targetHwnd:x}");
                        if (root != null) sb.Append($" 视觉树={DescribeVisualTree(root, 0, 3)}");
                        foreach (System.Collections.Generic.KeyValuePair<string, int> k in kinds)
                            sb.Append($" [{k.Key}×{k.Value}]");
                    }
                    catch (Exception ex)
                    {
                        sb.Append($" （资源表读取失败：{ex.GetType().Name}）");
                    }
                    sb.Append('\n');
                }
                Trace(sb.Length == 0 ? "通道计数快照：无存活通道" : "通道计数快照：\n" + sb);

                // 【注意】这里**不再**驱动呈现。真正的触发器已经接进
                // `MilNative.MilConnection_CommitChannel`（提交之后呈现，
                // 对应 Windows 上原生 MilCore 渲染线程那一步），
                // 所以诊断探针那个 250ms 替身已按主控要求移除。
            }
            catch { /* 诊断永不抛 */ }
        }

        // ---- 字形渲染器（TextRenderer）：由呈现层按需建并自持 --------------------
        // 【为什么是呈现层持有】`GlyphRenderer` 原设计是"宿主挂进来、宿主释放"。
        //   但真应用里**没有别的宿主**：托管层（PresentationCore/PF）不引用
        //   `WpfGfx.Linux.Text`，T9 的 PIDWriteFont→Typeface 映射也没接上。
        //   而通道里 `MilGlyphRun×2` 早就有了、`MilDrawGlyphRun` 却因没人挂被记进
        //   NotDrawn(1) —— 典型的"数据齐了、只差渲染器"。所以呈现层自己建**一个**
        //   实例（作用域就是这个呈现层，`Reset()` 时释放；不是给别的层共用的隐式单例）。
        //
        // 【为什么字体必须与 WPF 侧同一份】**glyph id 是字体相关的**：
        //   换一份字体，同一串 id 会画成完全不同的字（见 MilGlyphRunAdapter 顶部注释）。
        //   所以目录/字族的取值口径与 shim 的 UI 字体保持一致：
        //     WPF_LINUX_TEXT_FONT_DIR → WPF_LINUX_FONT_DIR → 系统目录
        //     WPF_LINUX_TEXT_FONT_FAMILY → WPF_LINUX_UI_FONT → "Noto Sans"
        private static void EnsureGlyphRenderer()
        {
            if (GlyphRenderer != null) return;
            lock (_gate)
            {
                if (GlyphRenderer != null) return;
                try
                {
                    string dir = FirstExistingDirectory(
                        Environment.GetEnvironmentVariable("WPF_LINUX_TEXT_FONT_DIR"),
                        Environment.GetEnvironmentVariable("WPF_LINUX_FONT_DIR"),
                        "/usr/share/fonts", "/usr/local/share/fonts");
                    if (dir == null)
                    {
                        Trace("字形渲染器：找不到任何字体目录 ⇒ 文字仍会被记为 NotDrawn");
                        return;
                    }

                    // 【P0 收尾轮 · B 步】默认字族不再硬编码猜一个名字，而是**问 SPI**：
                    //   `SPI_GETNONCLIENTMETRICS.lfMessageFont.lfFaceName` —— WPF 的 message font
                    //   就是从同一个源来的（真机 oracle：SPI = HKCU\...\MessageFont blob
                    //   = `SystemFonts.MessageFontFamily.Source`，三处逐字相同），
                    //   shim 侧的实现见 `win32_misc.c` 的 `WPF_DEFAULT_UI_FONT` / `WPF_LINUX_UI_FONT`。
                    //   ⇒ 渲染器请求的族与 WPF 实际用的族**同源**，而不是两个独立的猜测。
                    //   （旧行为：硬编码 "Noto Sans" —— 系统里没有真正的 Noto Sans 族，
                    //     `fc-match` 会命中 `Noto Sans CJK SC` ⇒ 字形 id 与光栅化的面不是同一个。）
                    string family = Environment.GetEnvironmentVariable("WPF_LINUX_TEXT_FONT_FAMILY");
                    string familySource = "env:WPF_LINUX_TEXT_FONT_FAMILY";
                    if (string.IsNullOrEmpty(family))
                    {
                        family = Win32UiFont.TryGetMessageFontFamily();   // 内部已含 WPF_LINUX_UI_FONT
                        familySource = $"SPI(SPI_GETNONCLIENTMETRICS)：{Win32UiFont.LastDetail}";
                    }
                    if (string.IsNullOrEmpty(family))
                    {
                        family = "DejaVu Sans";
                        familySource = "兜底常量（SPI 与 env 都没拿到）";
                    }

                    Text.FontSet fonts = Text.FontSet.FromDirectory(dir);
                    var description = Text.TextFontDescription.Regular(family);
                    GlyphRenderer = new Text.TextRenderer(fonts, description, fallbackSize: 12f);

                    // 【为什么要记录"**实际解析到**哪个面"】族名与面不是一回事：
                    //   实测（主控 P0）`/usr/share/fonts` 里没有真正的 `Noto Sans` 族，
                    //   字体配置会把它匹配到 `Noto Sans CJK SC` —— 于是"请求的字族"与
                    //   "拿到的面"不是同一个，而字形 id 来自另一个面 ⇒ 整段文字不画。
                    string resolved = "<未解析>";
                    try
                    {
                        if (fonts.TryResolve(description, out SkiaSharp.SKTypeface face) && face != null)
                            resolved = face.FamilyName;
                    }
                    catch { /* 诊断永不抛 */ }

                    // 【显式给了目录、却没给字族时：字族以**那个目录自己的面**为准】
                    //   为什么必须有这一步（实测退步）：档②只设 `WPF_LINUX_TEXT_FONT_DIR=build/fonts-ui`
                    //   时，字族来源是 SPI 的 `DejaVu Sans`，而那个目录里只有 `UI-NoLayout.ttf`
                    //   （Noto 系）⇒ `TryResolve("DejaVu Sans")` 失败 ⇒ **文字又不画了**。
                    //   规则：**请求的族在选定集合里解析不到 ⇒ 用该集合里的第一个面**，
                    //   并且**大声说出来**（免采样 NOTE）—— 因为"请求的族与拿到的面不一致"
                    //   正是 P0 那条不变量的违例形态，不能静默兜底。
                    if (resolved == "<未解析>")
                    {
                        string fromSet = FirstFamilyInDirectory(dir);
                        if (fromSet != null)
                        {
                            family = fromSet;
                            familySource += $" → 集合内兜底面（请求的族在 {dir} 里解析不到）";
                            var retry = Text.TextFontDescription.Regular(family);
                            if (fonts.TryResolve(retry, out SkiaSharp.SKTypeface f2) && f2 != null)
                            {
                                description = retry;
                                GlyphRenderer = new Text.TextRenderer(fonts, description, fallbackSize: 12f);
                                resolved = f2.FamilyName;
                            }
                            MilDiagnostics.Note(
                                $"⚠ 字形渲染器：请求的字族在 {dir} 里不存在 ⇒ 退回该目录内的面「{fromSet}」。" +
                                "（这是**兜底**，不是对齐：真修法是让 glyph run 自己带面，见报告 §4.21.3 的 C 步）");
                        }
                    }

                    // 句柄重建后要重新记一次"实际面"
                    if (resolved == "<未解析>")
                    {
                        try
                        {
                            if (fonts.TryResolve(description, out SkiaSharp.SKTypeface f3) && f3 != null)
                                resolved = f3.FamilyName;
                        }
                        catch { /* 诊断永不抛 */ }
                    }

                    _glyphFaceInfo = $"目录={dir} 请求字族={family}（来源：{familySource}）" +
                                     $" 实际面={resolved} 候选文件={fonts.FileNames.Count}";
                    Trace($"字形渲染器已挂上：{_glyphFaceInfo}");

                    // ── 债务 #14：**面由 run 决定**（本工程这一半）────────────────────────
                    //   宿主把 run 的 `PIDWriteFont` 翻成"实际那份面"：PC 侧接好后会把**它实际用的面**
                    //   登记进 `MilFontFaceTable`（`MilFontFace_RegisterFromFile`），并把返回的句柄
                    //   写进 run 的 `PIDWriteFont`；这里优先按句柄解析，**解析不到就返回 null**
                    //   ⇒ 渲染器回落今天的族名路径（现状不变）。
                    //
                    //   ⚠️ `PIDWriteFont` 是 **ulong**、`MilFontFaceTable` 的键是 **IntPtr**：
                    //      上游语义是"Windows 上的 DWrite 字体指针"，本工程把它**重新定义为句柄**
                    //      （契约见报告 §4.36.3）。转换前做**范围校验**：为 0、或超出 IntPtr 范围
                    //      ⇒ 当"没有句柄"（不让截断后的值去撞别的句柄）。
                    GlyphRenderer.MilFaceResolver = run =>
                    {
                        if (run == null || run.PIDWriteFont == 0) return null;
                        if (run.PIDWriteFont > (ulong)long.MaxValue) return null;
                        var faceHandle = new IntPtr((long)run.PIDWriteFont);
                        // ⚠️ 必须用 **TryResolveExact**：`TryResolve` 在未登记时会回落 `DefaultTypeface`
                        //    并返回 true ⇒ 会把"pid 一个都不认识"伪装成"全部按句柄命中"（本轮实测踩到）。
                        return MilFontFaceTable.TryResolveExact(faceHandle, out SkiaSharp.SKTypeface face)
                            ? face : null;
                    };

                    // 【字形 id 探针：**已撤回**】我加过一版"给 TextRenderer.MilFontResolver 挂探针"来读 CJK 的 glyph id，
                    //   但它**改变了渲染**（同一份代码：装探针后 `未画种类 0→1`，即有整整一类指令不再被画）
                    //   ⇒ 按"观测手段不许扰动被测对象"的纪律**撤掉**，不拿它的读数下结论。
                    //   要读 CJK 的 glyph id，应当用**只读**计数（渲染路径里加 counters，属 T2b 车道），
                    //   或者用 `WPF_LINUX_MIL_LOG` 里既有的资源台账逐 run 看，而不是替换渲染器的解析器。

                    if (resolved != "<未解析>" && !resolved.StartsWith(family, StringComparison.Ordinal))
                    {
                        MilDiagnostics.Note(
                            $"⚠ 字形渲染器用的**不是请求的那个面**：请求「{family}」⇒ 拿到「{resolved}」。" +
                            "若 WPF 侧用的是另一个面，glyph id 会对不上（症状：文字整段不画，`未画种类` 涨）。");
                    }
                }
                catch (Exception ex)
                {
                    MilDiagnostics.Note($"创建字形渲染器失败：{ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 取某个目录（**递归**）里第一个字体文件的族名。给"显式给了目录但没给字族"的兜底用。
        /// 与 `Text/FontSet.FromDirectory` 用同一套枚举选项与排序，保证"哪个面算第一个"两处一致。
        /// </summary>
        private static string FirstFamilyInDirectory(string directory)
        {
            try
            {
                var options = new System.IO.EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                };
                var files = new List<string>();
                files.AddRange(System.IO.Directory.EnumerateFiles(directory, "*.ttf", options));
                files.AddRange(System.IO.Directory.EnumerateFiles(directory, "*.otf", options));
                files.AddRange(System.IO.Directory.EnumerateFiles(directory, "*.ttc", options));
                files.Sort(StringComparer.Ordinal);

                foreach (string path in files)
                {
                    try
                    {
                        // [W8 / D-F1c 内存半边] 兜底扫目录只为取 FamilyName —— 但 `FromFile` **每调一次 mmap 一整份**。
                        //   改走按路径共享的 `SKData`（本行 `using` 释放的是 typeface，共享后备由缓存持有 ⇒ 不重复映射）。
                        using SkiaSharp.SKTypeface face = SkiaFontFileCache.CreateFace(path, 0);
                        if (face != null && !string.IsNullOrEmpty(face.FamilyName)) return face.FamilyName;
                    }
                    catch { /* 坏文件跳过，与 FontSet 同一口径 */ }
                }
            }
            catch { /* 兜底失败就不要兜底 */ }
            return null;
        }

        private static string FirstExistingDirectory(params string[] candidates)
        {
            foreach (string c in candidates)
                if (!string.IsNullOrEmpty(c) && System.IO.Directory.Exists(c)) return c;
            return null;
        }

        /// <summary>本进程的 X11 连接（懒建，呈现侧独有）。</summary>
        internal static X11Display Display
        {
            get
            {
                lock (_gate)
                {
                    if (_display == null)
                        _display = X11Display.Open(null);   // null ⇒ 读 $DISPLAY
                    return _display;
                }
            }
        }

        /// <summary>
        /// 为一个**已存在**的 XID 建/取呈现目标。幂等：同一个 HWND 只会有一个目标。
        /// 失败时返回 false 并把原因写进 <paramref name="error"/>（同时记进 _bindErrors）。
        /// </summary>
        internal static bool TryBind(IntPtr hwnd, out string error)
        {
            error = null;
            if (hwnd == IntPtr.Zero) { error = "HWND 为空"; return false; }

            lock (_gate)
            {
                _bindAttempts++;
                if (_targets.ContainsKey(hwnd)) return true;   // 幂等

                try
                {
                    // 关键：包装**既有**窗口，而不是新建一个。hwnd 就是 XID（M7b 实证）。
                    X11PresentationTarget t = X11PresentationTarget.WrapExisting(Display, (ulong)hwnd.ToInt64());
                    _targets[hwnd] = t;
                    _bindErrors.Remove(hwnd);
                    // 轨道 C：为**本连接**订阅 Expose/结构事件 —— 这是"真接窗"的后半段：
                    // 没有它，窗口被 resize/expose 之后没人知道该重画（债务 #3）。
                    try { t.EnableEventNotifications(); }
                    catch (Exception ex) { MilDiagnostics.Note($"订阅呈现事件失败（HWND 0x{(long)hwnd:x}）：{ex.GetType().Name}: {ex.Message}"); }
                    Trace($"AttachToHwnd: HWND 0x{(long)hwnd:x} → 呈现目标已绑定" +
                          $"（窗口 {t.Width}x{t.Height}，own={t.OwnsWindow}）");
                    StartChannelCounterProbe();   // 诊断：把"命令在不在流动"变成时序证据
                    return true;
                }
                catch (Exception ex)
                {
                    error = $"{ex.GetType().Name}: {ex.Message}";
                    _bindErrors[hwnd] = error;
                    _bindFailures++;
                    Trace($"AttachToHwnd: HWND 0x{(long)hwnd:x} **绑定失败**：{error}");
                    return false;
                }
            }
        }

        // ==================================================================
        //  XEvent → 呈现（轨道 C：反向派发 + resize 跟随）
        // ==================================================================
        // 【债务 #3 的正解】`docs/ARCHITECTURE.md §8` 写着"`Resize` 之后需要上层重渲 ——
        //   这是使用契约的一部分，不是实现细节"。本轮把这条**契约收进呈现层**：
        //   X11 的 resize/expose 是 server 侧动作，窗口自己不会重画；呈现层既然订阅了
        //   这两类事件，就在事件到达时**用当前通道重新渲染并呈现**。
        //   —— 对上层（WPF 的 HwndTarget）来说，"窗口变大后内容自动跟着"不再需要它记得做什么。
        //
        // 【为什么不做成独立线程】托管层的 resize 本来就有一条完整链路
        //   （ConfigureNotify → shim → WM_SIZE → HwndTarget.UpdateWindowSettings → 渲染 pass
        //     → commit → 我们这里的 PresentChannel），所以在**呈现入口**顺手把事件抽干即可，
        //   不需要额外线程、也不引入跨线程竞态。没有新命令到达的纯 expose（被遮挡后重现）
        //   同样会在下一次呈现时被补上。
        /// <summary>上次呈现时**看到的 X 窗口尺寸**（用来识别"真的发生了 resize"）。</summary>
        private static readonly Dictionary<IntPtr, (int W, int H)> _lastXSize =
            new Dictionary<IntPtr, (int, int)>();

        private static readonly Dictionary<IntPtr, MilChannel> _channelByHwnd =
            new Dictionary<IntPtr, MilChannel>();   // 最近一次呈现用过的通道（重渲时要用）

        private static long _resizeEvents, _exposeEvents, _closedEvents;

        /// <summary>X 侧 resize 事件计数（**不节流**：关键状态转换要免采样）。</summary>
        internal static long ResizeEvents { get { lock (_gate) return _resizeEvents; } }
        /// <summary>X 侧 expose 事件计数（不节流）。</summary>
        internal static long ExposeEvents { get { lock (_gate) return _exposeEvents; } }
        /// <summary>窗口被销毁（Closed）事件计数（不节流）。</summary>
        internal static long ClosedEvents { get { lock (_gate) return _closedEvents; } }

        /// <summary>
        /// 抽干所有已绑定窗口的 X 事件，并把 resize/expose/closed 变成实际动作。
        /// 返回本次处理的"需要重画"的窗口数（0 = 没有）。
        /// </summary>
        internal static int PumpWindowEvents()
        {
            if (_pumping) return 0;                 // 防重入：重渲会再次进 PresentChannel
            _pumping = true;
            try
            {
                IntPtr[] hwnds;
                lock (_gate) hwnds = new List<IntPtr>(_targets.Keys).ToArray();

                int repaint = 0;
                foreach (IntPtr hwnd in hwnds)
                {
                    if (!TryGetTarget(hwnd, out X11PresentationTarget target)) continue;

                    bool needRepaint = false;
                    int guard = 0;
                    while (guard++ < 64 && target.Window.TryNextEvent(out WindowEvent ev))
                    {
                        // 【必须按 WindowId 归属】X11 的事件队列是**连接级**的：
                        //   `XNextEvent` 会把整条连接上**任何窗口**的事件取出来。所以
                        //   "在 A 窗口上抽事件"完全可能抽到 B 窗口的事件 —— 实测踩过两回：
                        //   ① `DestroyNotify`：上一个测试销毁窗口留下的事件被当成**当前**窗口的
                        //      Closed，于是把当前目标误判成"窗口没了"并解绑（紧接着就是对
                        //      已释放对象的访问）。
                        //   ② 【缺陷 A / `D-G54`】弹窗（`0x200008`）的 `ConfigureNotify` 是在
                        //      **主窗口**（`0x200004`）那次抽事件里被取走的，而旧代码按"抽到事件的
                        //      窗口"记账 ⇒ 弹窗的 `413x274` 被记成主窗口的新尺寸，呈现层于是
                        //      "按新尺寸重渲"用**弹窗**的尺寸重画了**主窗口**。现场逐字
                        //      （`$HOME/hc-fo3-mil.log:7885-7886`）：
                        //        `NOTE X11 Resize 生效：HWND 0x200004 800x600 → 413x274（按新尺寸重渲）`
                        //        `★ 呈现尺寸变化（免采样）：通道 2 → HWND 0x200004 已呈现 413x274`
                        //   ⇒ `ev.WindowId` 是**唯一**的归属依据，它决定这些事算在**哪扇窗**头上：
                        //     尺寸缓存（`ApplyOwnConfigureNotify`）、解绑（`TryUnbind`）、日志里的 HWND。
                        //   不属于本窗口的事件**不就地丢弃**：主人也在绑定表里就转交给**它**处理
                        //   （抽事件的人只是"替邻居收信"）；主人没绑定 ⇒ 本层看不见那扇窗，丢。
                        //   「重画」这件事本身是**整条连接一趟**的：下面那个重渲块会把所有绑定了
                        //   通道的窗口都重渲一遍，所以 `needRepaint` 只表示"这趟有事件、要重画"。
                        IntPtr owner = hwnd;
                        X11PresentationTarget ownerTarget = target;
                        if (ev.WindowId != 0 && ev.WindowId != (ulong)hwnd.ToInt64())
                        {
                            owner = (IntPtr)(long)ev.WindowId;
                            if (!TryGetTarget(owner, out ownerTarget)) continue;
                        }

                        switch (ev.Kind)
                        {
                            case WindowEventKind.Resized:
                                // ★ 缓存尺寸只许记在**事件的主人**身上（缺陷 A 的判定点）。
                                //   方法内部还会再核一次 XID：归属不符的事件改不动任何窗口的尺寸。
                                ownerTarget.Window.ApplyOwnConfigureNotify(ev);
                                lock (_gate) _resizeEvents++;
                                needRepaint = true;
                                MilDiagnostics.Note(
                                    $"X11 Resize：HWND 0x{(long)owner:x} → {ev.Width}x{ev.Height}（将按新尺寸重渲）");
                                break;
                            case WindowEventKind.Exposed:
                                lock (_gate) _exposeEvents++;
                                needRepaint = true;
                                break;
                            case WindowEventKind.Closed:
                                lock (_gate) _closedEvents++;
                                MilDiagnostics.Note($"X11 Closed：HWND 0x{(long)owner:x} 已销毁 ⇒ 解绑呈现目标");
                                // 窗口没了：解绑（不销毁任何东西，窗口已经不在了）。
                                // **处理完必须立刻跳出事件循环** —— `target` 已被 Dispose，
                                // 再调 `target.Window.TryNextEvent` 就是对已释放对象下手。
                                TryUnbind(owner);
                                lock (_gate) _channelByHwnd.Remove(owner);
                                goto nextWindow;
                            default:
                                break;   // 其余事件（键鼠等）归 shim 的输入链路，本层不看
                        }
                    }

                    if (needRepaint) repaint++;
                    nextWindow: ;
                }

                // 需要重画的窗口：用它**最近一次呈现用的通道**重新渲染 + 呈现。
                // ⚠️ 去重：`PresentChannel` 现在是**按目标**呈现的（一个通道挂多扇窗时一次全画），
                //   所以"两扇窗属于同一个通道"时**一趟只需调一次**（旧实现在这种情况下会把同一
                //   通道的"第一个目标"重画两遍）。单窗口场景只有一个 hwnd ⇒ 与旧行为逐字相同。
                if (repaint > 0)
                {
                    var done = new HashSet<MilChannel>();
                    foreach (IntPtr hwnd in hwnds)
                    {
                        if (!TryGetTarget(hwnd, out _)) continue;
                        MilChannel ch;
                        lock (_gate) _channelByHwnd.TryGetValue(hwnd, out ch);
                        if (ch == null) continue;
                        if (!done.Add(ch)) continue;
                        PresentChannel(ch);
                    }
                }
                return repaint;
            }
            finally { _pumping = false; }
        }

        private static bool _pumping;

        /// <summary>解绑。返回是否真的解绑了一个目标（幂等解绑返回 false）。</summary>
        internal static bool TryUnbind(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            lock (_gate)
            {
                bool removed = _targets.Remove(hwnd, out X11PresentationTarget target);
                _bindErrors.Remove(hwnd);
                _channelByHwnd.Remove(hwnd);
                _lastXSize.Remove(hwnd);
                if (removed)
                {
                    // 只释放我们的 GC —— X11Window.Wrap 建的目标**不拥有**窗口，
                    // 因此这里不会把 WPF 的窗口拆掉（拆窗归 Win32 shim 的 DestroyWindow）。
                    try { target.Dispose(); } catch { /* 解绑阶段的清理失败不改变语义 */ }
                }
                return removed;
            }
        }

        internal static bool HasTarget(IntPtr hwnd)
        {
            lock (_gate) return _targets.ContainsKey(hwnd);
        }

        internal static string BindError(IntPtr hwnd)
        {
            lock (_gate) return _bindErrors.TryGetValue(hwnd, out string e) ? e : null;
        }

        /// <summary>取目标（成功时 target 非空）。</summary>
        internal static bool TryGetTarget(IntPtr hwnd, out X11PresentationTarget target)
        {
            lock (_gate) return _targets.TryGetValue(hwnd, out target);
        }

        internal static int TargetCount { get { lock (_gate) return _targets.Count; } }

        // ==================================================================
        //  通道 → 窗口
        // ==================================================================

        /// <summary>
        /// 列出该通道资源表里**全部**呈现目标（`MilTarget.NativeWindow != 0` 的那些）。
        ///
        /// 【为什么不再是"取第一个"（波46 · `D-G54` 判定点之一）】
        ///   一个通道上**可以挂多个窗口目标**。hc 组合框下拉弹窗那一趟里，通道 2 同时持有
        ///   主窗口目标（`NativeWindow` = `0x200004`）与弹窗目标（`0x200008`）。旧实现
        ///   `TryResolveTarget` 遍历资源表、遇到**第一个** `NativeWindow != 0` 的 target 就
        ///   `return true` ⇒ 一个通道**只可能**有一扇窗进入呈现名单，弹窗的 HWND 从头到尾
        ///   没被呈现过一次（现场读数：`grep -c '已呈现.*0x200008'` = **0**）。
        ///   上游 Windows 的 MilCore 是**每个 `HwndTarget` 自己一棵根、各自呈现**；"每通道
        ///   一个目标"是本移植的结构性偏差，这里把它改回**按目标**。
        ///
        /// 返回空列表 = 这个通道**没有绑定窗口**（离屏/纯通道用法）——呈现流程按"无事可做"
        /// 处理，保持与 M7a 完全一致的行为（只 Commit）。
        ///
        /// 【顺序为什么必须**按句柄升序**排】`channel.Resources.Entries` 是 `Dictionary`
        ///   （`Resources/MilResourceTable.cs:28`），且句柄会被**回收复用**（`_freeHandles` 弹栈）
        ///   ⇒ 枚举序**既不等于句柄序、也不保证跨运行稳定**。不排序的直接后果是**呈现顺序**
        ///   与台账里的"第 i/N 个"每趟都可能不同 ⇒ 读数不可复算（违反本项目"结论必须可复算"）。
        ///   排序键取**句柄**还有一个好处：句柄小 = 先建的目标（主窗先于弹窗）
        ///   ⇒ 多目标时的"第 1 个"自然退化成旧实现语义里的那个"第一个"。
        /// </summary>
        internal static List<MilTarget> CollectTargets(MilChannel channel)
        {
            var result = new List<MilTarget>();
            if (channel == null) return result;

            // Entries 是 live 字典视图；先快照键，再逐个查，避免枚举期间被改。
            List<uint> keys;
            try
            {
                keys = new List<uint>(channel.Resources.Count);
                foreach (KeyValuePair<uint, MilResourceEntry> kv in channel.Resources.Entries)
                    keys.Add(kv.Key);
            }
            catch (InvalidOperationException)
            {
                // 并发修改：这一帧拿不到完整快照就当作"没有目标"，下一帧再来。
                return result;
            }

            keys.Sort();                                  // ★ 确定性（见上）
            for (int i = 0; i < keys.Count; i++)
            {
                if (!(channel.Resources.Lookup(new DUCE.ResourceHandle(keys[i])) is MilTarget t)) continue;
                if (t.NativeWindow == IntPtr.Zero) continue;
                result.Add(t);
            }
            return result;
        }

        // ==================================================================
        //  渲染 + 呈现
        // ==================================================================

        /// <summary>
        /// 渲染并呈现一个通道。返回 HRESULT。
        ///
        /// 【什么时候"什么都不做"是**正确**的】
        ///   通道没有绑定窗口（离屏通道）→ S_OK，不发一帧。这不是"静默失败"：
        ///   确实没有窗口可画。反之，**有窗口**却渲不出来/画不上去 → 返回失败码。
        /// </summary>
        // ── 根句柄活投影诊断（`#34` 波；env 门控 WPFGFX_ROOTDIAG=1，默认关 ⇒ 行为零变化）──
        //   ⚠️ 变量名**故意不带 `WPF_LINUX_` 前缀**：门禁的 default 档会清空全部 `WPF_LINUX_*`，
        //      实测若用 `WPF_LINUX_MIL_ROOTDIAG` 会让门禁的 env 组装炸成 `env: "-u": 没有那个文件或目录`、
        //      app 以 exit=127 死掉（= 仪器把被测对象弄死了，不是产品读数）。
        // 【它回答的问题】"换 Content 之后整帧变空白"到底是
        //   (A) 新子树挂在**别的父句柄**下（从 target.Root 走不到）⇒ 孤立节点数 > 0；
        //   (B) 还是**根句柄本身失效**（Resolve 不到）⇒ 从根可达数 = 0/1。
        //   两条的修法不同，必须分开量。
        private static readonly bool _rootDiag =
            Environment.GetEnvironmentVariable("WPFGFX_ROOTDIAG") == "1";
        private static long _rootDiagBudget = 80;

        private static void DumpRootDiag(MilChannel channel, MilTarget target, MilVisual liveRoot)
        {
            if (!_rootDiag) return;
            if (System.Threading.Interlocked.Decrement(ref _rootDiagBudget) < 0) return;
            try
            {
                MilVisual snap = (channel as IMilChannel).Root;
                int snapChildren = snap == null ? -1 : snap.Children.Count;
                int liveChildren = liveRoot == null ? -1 : liveRoot.Children.Count;

                var all = new System.Collections.Generic.List<MilVisualNode>();
                foreach (var kv in channel.Resources.Entries)
                    if (kv.Value.Resource is MilVisualResource vr) all.Add(vr.Visual);

                var reach = new System.Collections.Generic.HashSet<uint>();
                var queue = new System.Collections.Generic.Queue<DUCE.ResourceHandle>();
                queue.Enqueue(target.Root);
                while (queue.Count > 0)
                {
                    DUCE.ResourceHandle h = queue.Dequeue();
                    if (h.IsNull || !reach.Add(h.Value)) continue;
                    MilVisualNode n = channel.GetVisual(h);
                    if (n == null) continue;
                    foreach (DUCE.ResourceHandle c in n.Children) queue.Enqueue(c);
                }

                int orphans = 0;
                var orphanInfo = new System.Text.StringBuilder();
                foreach (MilVisualNode v in all)
                {
                    if (reach.Contains(v.Handle.Value)) continue;
                    orphans++;
                    if (orphanInfo.Length > 600) continue;
                    // T-B19 只读：孤立子树的规模与"带内容"节点数（判"页视觉是否没被投影"）。
                    int sub = 0, subContent = 0;
                    var st = new System.Collections.Generic.Stack<DUCE.ResourceHandle>();
                    st.Push(v.Handle);
                    var seen = new System.Collections.Generic.HashSet<uint>();
                    while (st.Count > 0)
                    {
                        DUCE.ResourceHandle hh = st.Pop();
                        if (hh.IsNull || !seen.Add(hh.Value)) continue;
                        sub++;
                        MilVisualNode nn = channel.GetVisual(hh);
                        if (nn == null) continue;
                        if (!nn.Content.IsNull) subContent++;
                        foreach (DUCE.ResourceHandle c in nn.Children) st.Push(c);
                    }
                    orphanInfo.Append($" [孤立 h=0x{v.Handle.Value:x} 子树={sub} 带内容={subContent}]");
                }

                Trace($"ROOTDIAG 通道{channel.Id} target.Root=0x{target.Root.Value:x}"
                      + $" 快照子={snapChildren} 活投影子={liveChildren}"
                      + $" 镜像visual={all.Count} 从根可达={reach.Count} 孤立={orphans}"
                      + $" 资源={channel.Resources.Count}{orphanInfo}");
            }
            catch (Exception ex)
            {
                Trace("ROOTDIAG 失败 " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static int PresentChannel(MilChannel channel)
        {
            if (channel == null) return HResult.E_HANDLE;

            // 轨道 C：进呈现前先把 X 事件抽干（resize/expose ⇒ 需要重画）。
            // 放在这里而不是开线程：上层本来就有 ConfigureNotify→WM_SIZE→渲染 pass→commit 这条链路，
            // 呈现入口是它唯一的汇聚点。
            if (!_pumping) PumpWindowEvents();

            long callNo;
            lock (_gate) callNo = ++_presentCalls;
            if (ShouldTracePresent(callNo))
                Trace($"WgxConnection_SameThreadPresent 第 {callNo} 次：通道 {channel.Id}");

            List<MilTarget> targets = CollectTargets(channel);
            if (targets.Count == 0)
            {
                // 离屏通道：无窗口可画。**这条分支原来是静默的**，也正是 Phase 2 里
                // "窗口出来了但截屏纯白、日志为空"最难排除的一种可能，所以单独打一条。
                if (ShouldTracePresent(callNo))
                    Trace($"  通道 {channel.Id} 无带 HWND 的目标（离屏通道）→ 不呈现（S_OK）");
                return HResult.S_OK;
            }

            // ★ 按**目标**逐个呈现（波46 · `D-G54`）：一个通道挂多扇窗时，**每扇窗都要**被画到。
            //   旧实现只呈现"第一个"目标 ⇒ 后建的那扇窗（弹窗）永远是空的。
            //
            //   返回码口径（`WAVE46-PERTARGET-PRESENT-DESIGN.md` §4-D6，**不是"第一个失败码胜出"**）：
            //     · 单目标通道 ⇒ `done==0` 时把那个失败码带出去 ⇒ 与旧实现**逐字等价**；
            //     · 多目标通道 ⇒ **全部失败才带出失败**（`done > 0` 一律 `S_OK`）。
            //   为什么必须这样：`exports.cs:376` 是 `HRESULT.Check(MilConnection_CommitChannel(…))`
            //   ⇒ Commit 返回失败码会让 **managed 侧抛异常**。而"窗口已销毁、目标资源还没 Release"
            //   是**正常时序**（`PumpWindowEvents` 的 `Closed` 分支会 `TryUnbind`）⇒ 若按"第一个失败
            //   胜出"，弹窗一关、主通道**每次 Commit 都会 E_FAIL**，真应用可能就死在这条异常上。
            //   被跳过的目标不静默：`MilDiagnostics.Note` 已留原因，这里再计一个 `TargetsSkipped`。
            int hr = HResult.S_OK;
            int done = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                int one = PresentTarget(channel, targets[i], targets.Count == 1, callNo, i, targets.Count);
                if (HResult.Succeeded(one)) done++;
                else if (HResult.Succeeded(hr)) hr = one;
            }
            if (done == 0 && HResult.Failed(hr)) return hr;
            if (HResult.Failed(hr))
            {
                lock (_gate) _targetsSkipped++;
                Trace($"  ⚠ 按目标呈现：通道 {channel.Id} 有 {targets.Count - done}/{targets.Count} 个目标未呈现" +
                      $"（首个失败码 hr=0x{hr:x8}）—— 因为**至少有一个**目标成功，本次通道提交仍返回 S_OK" +
                      "（口径见 WAVE46-PERTARGET-PRESENT-DESIGN.md §4-D6）");
            }
            return HResult.S_OK;
        }

        /// <summary>
        /// 把一个 target **呈现到它自己的 HWND**：用它自己的根（`MilTarget.Root`）、
        /// 它自己的窗口几何（`WindowRect` / `Width`、`Height`）。
        ///
        /// 【三条语义边界，都在这里逐字保留】
        ///   · 目标 HWND 没绑上呈现目标 ⇒ **E_FAIL**（不伪造成功），原因进 `MilDiagnostics`；
        ///   · 该目标没有可用的根 ⇒ **S_OK** + 诊断（与"渲染失败"不同：确实没有内容可画）；
        ///   · 有根但渲染/Present 失败 ⇒ **E_FAIL**。
        /// </summary>
        private static int PresentTarget(MilChannel channel, MilTarget target, bool singleTargetInChannel,
                                         long callNo, int index, int targetCount)
        {
            IntPtr hwnd = target.NativeWindow;

            if (!HasTarget(hwnd))
            {
                // 有窗口目标但没能绑上呈现目标。把原因原样带出去（不伪造成功）。
                string why = BindError(hwnd) ?? "未调用 MilVisualTarget_AttachToHwnd，或该 HWND 不在绑定表里";
                MilDiagnostics.Note($"WgxConnection_SameThreadPresent: HWND 0x{(long)hwnd:x} 未绑定呈现目标：{why}");
                return HResult.E_FAIL;
            }

            // ── 根视觉：**本目标自己的根**（波46 · `D-G54` 判定点之二）────────────────
            //   旧实现取 `IMilChannel.Root` —— 那是**通道级单槽**（`MilChannel.SetRootFromHandle`
            //   每次 TargetSetRoot 都覆盖它）。一个通道挂两扇窗时，后 SetRoot 的那扇（弹窗）
            //   会把先前的（主窗口）**覆盖掉** ⇒ 通道级单槽在结构上表达不了"每窗一棵根"。
            //   上游 Windows 的 MilCore 正是**每个 HwndTarget 自己一棵根**，所以这里改成：
            //     ① `singleTargetInChannel`（退化路径）时**先**取通道快照 —— 与旧实现逐字同序，
            //        单窗口应用的行为一字不变；
            //     ② 然后（任何情况）用**本目标**的 `target.Root` 现投一次；非空即采用。
            //   ⚠️ 投影**只许在本通道上做**：`target.Root` 是"在本通道上执行的那条
            //      `MilCmdTargetSetRoot` 给的句柄"，句柄的命名空间是**每通道**的。
            //      `DuplicateHandle` 会让两个通道的表指向**同一个 `MilTarget` 实例**（所以
            //      `t.Root` 在两边都可见），而 out-of-band 通道 3 里根本没有那棵子树 ⇒
            //      在通道 3 上投影要么得到 null、要么（句柄号恰好撞上它自己表里的某个 visual）
            //      把**别人的树**画进主窗口。`IsTargetRooted` 就是这条边界的判据：
            //      只有"该目标的根**是在本通道上**设定的"才允许在本通道投影。
            string rootSource;
            MilVisual root = singleTargetInChannel ? (channel as IMilChannel).Root : null;
            rootSource = root == null ? "无" : "通道快照（单目标兼容）";
            if (!channel.IsTargetRooted(target))
            {
                if (root != null)
                {
                    // 单目标退化路径 + 标记缺失（例如测试直接给 `MilTarget.Root` 赋值）：
                    // 保留旧的"快照兜底"语义，不因为新标记而少画一帧。
                    rootSource = "通道快照（单目标兼容·未标记）";
                }
            }
            else
            {
                // ★ 呈现代替"读一次快照"：**在呈现这一刻重新投影**。
                //   【为什么必须重投影 —— M7c 收尾轮实测】
                //   `channel.Root` 是 `MilChannel.SetRootFromHandle()`（Resources/MilChannel.cs:374）
                //   在 **TargetSetRoot 那一刻**做的**快照**：`VisualProjection.Project(channel, handle)`。
                //   而 TargetSetRoot 发生在 `HwndTarget.CreateUCEResources` 里 —— **远早于**任何渲染，
                //   那时内容根还没有子节点 ⇒ 快照**永远是空的**。
                //   实测后果：通道里明明已经有了完整内容
                //     `committed=65 资源=44 [MilVisualResource×14][MilGlyphRun×2][MilRenderDataResource×7]
                //      [MilLinearGradientBrush×2][MilSolidColorBrush×3][MilPen×3][MilEllipseGeometry×2]…`
                //   而快照仍是 `视觉树={子=0 内容=无}` ⇒ 就算有人调呈现，画出来的也是一张空帧（白屏）。
                //   ⇒ 呈现路径必须**现取现投**。投影是纯读（`VisualProjection.Project` 只读资源表），
                //     与 UI 线程的提交之间由通道自己的锁保护；投影失败（句柄被回收等）就退回快照，
                //     不改变"呈现失败要报错"的既有语义。
                try
                {
                    MilVisual live = VisualProjection.Project(channel, target.Root);
                    if (live != null) { root = live; rootSource = "目标自己的根（本次重投影）"; }
                }
                catch (Exception ex)
                {
                    MilDiagnostics.Note($"WgxConnection_SameThreadPresent: 呈现时重投影根视觉失败，退回快照：{ex.GetType().Name}: {ex.Message}");
                }
            }
            if (root == null)
            {
                // 还没有 TargetSetRoot：没有可画的内容。这与"渲染失败"不同。
                // 文案在单目标退化路径上**逐字不变**（既有判据/测试按它匹配）。
                MilDiagnostics.Note(targetCount == 1
                    ? $"WgxConnection_SameThreadPresent: 通道 {channel.Id} 无根视觉（未 TargetSetRoot）"
                    : $"WgxConnection_SameThreadPresent: 通道 {channel.Id} → HWND 0x{(long)hwnd:x} 无根视觉（本目标未 SetRoot 或根子树不属本通道）");
                return HResult.S_OK;
            }
            DumpRootDiag(channel, target, root);

            if (!TryGetTarget(hwnd, out X11PresentationTarget presentation))
                return HResult.E_FAIL;

            lock (_gate) _channelByHwnd[hwnd] = channel;   // 供 resize/expose 重渲使用

            // 尺寸：优先用 TargetUpdateWindowSettings 的 WindowRect（WPF 会发），
            // 否则用 target 的 Width/Height（MilCmdHwndTargetCreate 带的）。
            // ★ 逐目标取（波46 · D-G54）：这三样都是**本目标自己**的字段。
            //   ⚠️ 陷阱（设计稿 §4-D5）：弹窗目标是**先按 1×1 建窗**、`SetRoot` 就在此时、
            //      **之后**才 `SetWindowPos`/`MAP` 到 413×274 ⇒ 该目标的 `Width/Height`
            //      可能是 **1×1**；而"X 尺寸优先"的条件是"自上次呈现以来**变了**"，
            //      首次呈现 `haveLast == false` **不触发**。⇒ 尺寸**来源**必须打进台账，
            //      否则"拿 1×1 渲了一帧"会被读成"修法没生效"（假阴性）。
            int width = (int)target.Width;
            int height = (int)target.Height;
            string sizeSource = "HwndTargetCreate";
            if (target.WindowRect.Right > target.WindowRect.Left && target.WindowRect.Bottom > target.WindowRect.Top)
            {
                width = target.WindowRect.Right - target.WindowRect.Left;
                height = target.WindowRect.Bottom - target.WindowRect.Top;
                sizeSource = "WindowRect";
            }

            // ★ 轨道 C · 债务 #3：**X 窗口自己的尺寸优先于通道里记的尺寸**。
            //   判据是"**自上次呈现以来 X 尺寸变了**"，而不是"两者不相等" —— 这条区分很要紧：
            //     · 真应用启动时，shim 先按 CW_USEDEFAULT 建成 800×600，WPF 随后 SetWindowPos
            //       到 640×400（再按 DPI 缩放）。**两者不相等是常态**，若一律以 X 为准，
            //       第一帧会按 800×600 去画 WPF 按 640×400 排好的内容（多出一圈底色）。
            //     · 而"X 尺寸**变化了**"只可能来自一次真实 resize（用户拖边框 / SetWindowPos /
            //       外部客户端）—— 那时就该按新尺寸重画，否则多出来的那块永远是 X 的窗口底色。
            //   配合上面的 `PumpWindowEvents()`：ConfigureNotify 到达 → 尺寸更新 → 在本函数里生效。
            int xw = presentation.Width, xh = presentation.Height;
            if (xw > 0 && xh > 0)
            {
                bool haveLast;
                (int lw, int lh) last;
                lock (_gate) haveLast = _lastXSize.TryGetValue(hwnd, out last);
                if (haveLast && (last.lw != xw || last.lh != xh))
                {
                    MilDiagnostics.Note(
                        $"X11 Resize 生效：HWND 0x{(long)hwnd:x} {last.lw}x{last.lh} → {xw}x{xh}（按新尺寸重渲）");
                    width = xw; height = xh;
                    sizeSource = "X11（已变化）";
                }
                lock (_gate) _lastXSize[hwnd] = (xw, xh);
            }

            if (width <= 0 || height <= 0) return HResult.E_INVALIDARG;

            // ▸ 按目标选择台账（**每个 HWND 一行**，不是每帧一行 ⇒ 不刷屏、也不用抽样）。
            //   它是"这一帧到底呈现给谁"的**直接读数**：旧实现在这个位置是推理
            //   （"`TryResolveTarget` 取第一个"），现在是量出来的。W46C 报告 §7.3 点名要的
            //   就是这一行（当时只能靠读 `MilPresentation.cs` 的代码来解释 `0x200004`）。
            //   三个候选尺寸都打出来（设计稿 §4-D5）：`HwndTargetCreate` / `WindowRect` / `X11`
            //   —— 1×1 陷阱是否真被踩到，`尺寸来源=` 这一格一眼可判。
            bool firstPick;
            lock (_gate) firstPick = _loggedTargetPick.Add(hwnd);
            if (firstPick)
                Trace($"  ▸ 按目标呈现：通道 {channel.Id} → HWND 0x{(long)hwnd:x}" +
                      $"（本通道 {targetCount} 个目标里的第 {index + 1} 个）" +
                      $" target.Root=0x{target.Root.Value:x} 根来源={rootSource}" +
                      $" 尺寸={width}x{height} 尺寸来源={sizeSource}" +
                      $"（候选 HwndTargetCreate={target.Width}x{target.Height}" +
                      $" WindowRect={target.WindowRect.Right - target.WindowRect.Left}x{target.WindowRect.Bottom - target.WindowRect.Top}" +
                      $" X11={xw}x{xh}）" +
                      $" 清屏色=({target.ClearColor.R:F2},{target.ClearColor.G:F2},{target.ClearColor.B:F2},{target.ClearColor.A:F2})");

            // ================================================================
            //  【TASK-0210 / `D-G98`】呈现层**不是接窗路径上的几何主人**
            // ================================================================
            //  本桥只**包装**别人的窗口 —— 全仓唯一的创建点是
            //  `X11PresentationTarget.WrapExisting`（本文件 `:561`）⇒ 真应用里
            //  `OwnsWindow == false`：这扇窗的几何归 `HwndWrapper → Win32 shim → WM`，
            //  **不归呈现层**。呈现层做的是"往那个窗口里画"，所以它只需要**读**尺寸。
            //
            //  【旧行为为什么是缺陷（`D-G98`，现场逐字段读数）】WM 把窗**真还原**成
            //  `800x600` 之后，桥在 **`+85…99 ms`** 用一条裸 `ConfigureWindow` 把它改回
            //  `1280x1024`。证据链（两份独立仪器）：
            //   · 协议级台账（syscall 层）：`configure(0xc00004, mask=0xc, 1280x1024)`，
            //     调用链 `_XSend ← _XReply ← XSync ← wpfgfx_cor3.so+0x131b36`
            //     `(X11Native.XSync) ← +0x1339f5 (X11Window.Resize) ← +0x16fa4a (本方法)`；
            //   · 桥自带诊断（`WPF_LINUX_MIL_LOG`）读到了那一帧的**全部输入**：
            //     `目标尺寸=1280x1024 来源=WindowRect  X11=800x600 X11缓存=800x600
            //      haveLast=True last=800x600  Owns=False
            //      HwndTargetCreate=800x600 WindowRect=1280x1024`。
            //  ⇒ 机制 = **"X 尺寸变化"只被消费一次**（`_lastXSize` 在每次呈现里都无条件
            //    刷新）：采纳 X 尺寸的那一帧（上面 `:1052`，正是本文件既有的"债务 #3"修法）
            //    过后，`last == xw` ⇒ 那条分支不再触发 ⇒ `width/height` 落回 MIL 侧**过期**的
            //    `WindowRect`（仍是最大化几何）⇒ 与 X 缓存（`800x600`）不等 ⇒ 这一行
            //    **把过期几何顶回 X**。窗口于是永远回不到 `800x600`。
            //
            //  【修法】接窗路径上**只读尺寸、不写几何**：按 server（X）报的尺寸渲染，
            //  几何同步由窗口主人负责 —— 它是**唯一**知道 WM/工作区规则（标题栏、
            //  frame extents、最大化策略）的一侧。⚠️"真正的尺寸变化仍然到得了 X"不受影响：
            //  它走的是 shim 的 `SetWindowPos`/`MoveWindow → wpf_x11_move_resize`，
            //  **从来不是**这条路（本方法只在"MIL 与 X 不一致"时才动手，而那正是打架）。
            //  自己建的窗（M1 / HelloMil 路径，`OwnsWindow == true`）**逐字不变**。
            if (presentation.OwnsWindow)
            {
                if (presentation.Width != width || presentation.Height != height)
                    presentation.Resize(width, height);
            }
            else if (presentation.Width > 0 && presentation.Height > 0 &&
                     (presentation.Width != width || presentation.Height != height))
            {
                MilDiagnostics.Note(
                    $"[GEOWRITE-SUPPRESSED] HWND 0x{(long)hwnd:x} 接窗路径**不回写 X 几何**：" +
                    $"MIL 侧 {width}x{height}（来源={sizeSource}）≠ X 侧 " +
                    $"{presentation.Width}x{presentation.Height} ⇒ 以 X 为准渲染" +
                    "（几何归窗口主人 HwndWrapper→shim→WM）");
                width = presentation.Width;
                height = presentation.Height;
                sizeSource = "X11（接窗路径：server 为准，呈现层不回写几何）";
            }

            SKImage frame;
            // ★ 本帧统计必须**渲染完立刻抓成本地量**（设计稿 §9-G4）：`DrawnCommands` /
            //   `NotDrawnCommands` / `NotDrawnSummary` 是**进程级静态**（`RenderChannel` 里赋值），
            //   多目标呈现时它们只描述**最后一个**渲染过的目标 ⇒ 不能当"本目标的指令数"用，
            //   更不能在渲染**前**打。这里抓本地量并显式传给 `TracePresentResult`。
            long drawn, notDrawn;
            string notDrawnSummary;
            try
            {
                frame = RenderChannel(channel, root, width, height, target.ClearColor);
                drawn = DrawnCommands; notDrawn = NotDrawnCommands; notDrawnSummary = NotDrawnSummary;
                MilPresentProbe.OnPresent(callNo, hwnd, width, height, drawn, notDrawn, root, channel);
            }
            catch (Exception ex)
            {
                MilDiagnostics.Note($"WgxConnection_SameThreadPresent: 渲染失败：{ex.GetType().Name}: {ex.Message}");
                return HResult.E_FAIL;
            }

            // ▸ 本目标自己的指令数（渲染**之后**打；这是"两棵不同的树各画各的窗"的机器判据：
            //   两扇窗的 `skia 指令` 若**相同**，就要怀疑根被共用了）。
            if (firstPick)
                Trace($"  ▸ 本目标指令数：HWND 0x{(long)hwnd:x} 通道 {channel.Id}" +
                      $" 尺寸={width}x{height} skia 指令 {drawn} 条 未画种类 {notDrawn}{notDrawnSummary}" +
                      $" root=0x{target.Root.Value:x}");

            using (frame)
            {
                try
                {
                    presentation.Present(frame);
                    presentation.Sync();     // xwd 截屏要的是"server 上真的有这一帧"
                }
                catch (Exception ex)
                {
                    MilDiagnostics.Note($"WgxConnection_SameThreadPresent: Present 失败：{ex.GetType().Name}: {ex.Message}");
                    return HResult.E_FAIL;
                }
            }

            lock (_gate) _framesPresented++;
            // 用**本目标刚抓的**统计量，不读进程级静态（设计稿 §9-G4）。
            TracePresentResult(callNo, channel, hwnd, width, height, drawn, notDrawn, notDrawnSummary);
            return HResult.S_OK;
        }

        /// <summary>
        /// 把根视觉渲染成不可变 SKImage。**与 HelloMil 用的是同一条渲染路径**
        /// （MilChannelResourceProvider + SkiaRenderBackend + RenderContext）。
        /// </summary>
        internal static SKImage RenderChannel(MilChannel channel, MilVisual root,
                                              int width, int height, MilColorF clearColor)
        {
            var provider = new MilChannelResourceProvider(channel);

            // 两个扩展点必须挂上，否则"渲染成功但少了东西"——这是本工程既有的范式：
            // 字形（T6 的 TextRenderer）与位图源（0x0c/0x0d 的 MilBitmapSourceTable）
            // 都是以委托形式挂到 provider 上的。挂不上时后端会**记账**（NotDrawn），
            // 而不是静默丢弃，所以这里挂的是"能挂的都挂上"。
            // 字形钩子需要一个 **TextRenderer 实例**（它持有字体资源，得由宿主拥有并
            // 负责释放），所以呈现路径上不新建、不做隐式全局单例。宿主可通过
            // `MilPresentation.GlyphRenderer` 挂进来（与 RenderContext/MilResourceProvider
            // 的"委托扩展点"是同一范式）。
            // 没挂上时后端会把 MilDrawGlyphRun 记进 NotDrawn —— 这是刻意的：
            // **"少画了东西"必须可观测**，而不是静默丢指令（NotDrawnCommands 就是对它的断言）。
            EnsureGlyphRenderer();
            Text.GlyphFaceCensus.NoteEntry("MilPresentation.RenderChannel（呈现路径）");
            if (GlyphRenderer != null)
                Text.TextRenderer.AttachTo(provider, GlyphRenderer);
            else
                Text.GlyphFaceCensus.NoteEntry("MilPresentation：GlyphRenderer == null（没建出来）");
            Commands.MilBitmapSourceTable.AttachTo(provider);

            var context = new RenderContext
            {
                Width = width,
                Height = height,
                Dpi = RenderContext.FixedDpi,
                ClearColor = ToSKColor(clearColor),
                Antialias = true,
                FontDirectory = FontDirectoryProbe(),
            };

            var backend = new SkiaRenderBackend(provider);

            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using SKSurface surface = SKSurface.Create(info);
            if (surface == null)
                throw new InvalidOperationException("SKSurface.Create 失败（Skia CPU 后端不可用）");

            backend.RenderVisualTree(root, surface.Canvas, context);

            NotDrawnCommands = backend.Diagnostics.NotDrawn.Count;
            // 与 NotDrawnCommands **同刻取、同一本账**：尾部类名后缀（空态为空串 ⇒ 前缀逐字不变）。
            // 只加不改：这三处格式串的既有字符一个都没动，只在 `）` 前追加本串。
            NotDrawnSummary = backend.Diagnostics.NotDrawnSummary();
            DrawnCommands = backend.Diagnostics.InstructionCount;

            // ★ P0（收尾轮）：字形指令被丢时**必须说清为什么**。
            //   修前唯一的信号是台账里一个 `未画种类 1` —— 画面其余部分完全正常，
            //   肉眼看着"像没问题"，而真应用默认配置（不设任何字体 env）走的正是这条路。
            //   这里在**第一次**发生时就免采样说清：选了哪个目录/字族、**实际解析到哪个面**、
            //   以及 MIL 侧有没有被 WPF 登记过字体面（这是"面能不能由 run 决定"的前提）。
            ReportGlyphDrop(backend.Diagnostics.NotDrawn);

            // 只读 census 汇总（缺省关；仅供 (乙) 归因，不进任何 runner 判据）
            Text.GlyphFaceCensus.Report($"帧 {width}x{height}");

            return surface.Snapshot();
        }

        /// <summary>
        /// 字形渲染钩子（可选）。宿主（例如 T2 的文本路径或 samples）持有一个
        /// <see cref="Text.TextRenderer"/> 时挂进来；不挂则 MilDrawGlyphRun 被记为
        /// NotDrawn（可观测，不静默）。
        /// </summary>
        internal static Text.TextRenderer GlyphRenderer { get; set; }

        /// <summary>渲染诊断：最近一帧里后端"没画"的**种类数**（0 = 全画了）。</summary>
        internal static int NotDrawnCommands { get; private set; }

        /// <summary>
        /// `未画种类` 的**尾部类名后缀**（形如 ` [MilPushOpacityMask×1]`，空台账为 `""`）。
        /// 与 <see cref="NotDrawnCommands"/> 同刻从**同一本** `Diagnostics.NotDrawn` 取。
        /// **只追加**：格式串里 `未画种类 {N}` 仍是逐字不动的子串 ⇒ 判据②的字符串匹配不受影响。
        /// 目的：台账行自己带类名，下次排查不必再跑一趟带 `[DRAW_CENSUS]` 的应用。
        /// </summary>
        internal static string NotDrawnSummary { get; private set; } = string.Empty;

        // ---- 字形被丢时的"第一现场"诊断（P0，默认零开销）----------------------
        private static long _glyphDropReported;
        private static long _glyphIdProbeReported;
        private static string _glyphFaceInfo = "字形渲染器：尚未创建";

        /// <summary>
        /// 一次说清"字形为什么没画出来"。**免采样**（关键状态转换不许被节流吞掉），
        /// 但同一进程只报一次（信息是静态的：面与目录不会中途变）。
        /// </summary>
        private static void ReportGlyphDrop(System.Collections.Generic.IReadOnlyDictionary<MilDrawCommand, long> notDrawn)
        {
            long glyphRuns = 0;
            foreach (System.Collections.Generic.KeyValuePair<MilDrawCommand, long> kv in notDrawn)
            {
                if (kv.Key == MilDrawCommand.MilDrawGlyphRun) glyphRuns += kv.Value;
            }
            if (glyphRuns <= 0) return;
            if (System.Threading.Interlocked.Exchange(ref _glyphDropReported, 1) == 1) return;

            MilDiagnostics.Note(
                $"★★ 字形指令**没有画出来**（MilDrawGlyphRun×{glyphRuns}）。" +
                $"诊断：{_glyphFaceInfo}；MIL 侧字体面登记数={MilFontFaceTable.Count}。" +
                "根因方向：glyph id 是**字体相关**的，光栅化必须用 WPF 用的同一份**面**；" +
                "而本移植的 MilGlyphRun 不带字体身份（MILCMD_GLYPHRUN_CREATE 里的 PIDWriteFont " +
                "被解码后丢弃）⇒ 渲染器只能按**字族名**猜，猜错就整段不画。" +
                "方案见 docs/U2-M7c-report.md §4.22。");
        }

        /// <summary>渲染诊断：最近一帧画了多少条指令。</summary>
        internal static long DrawnCommands { get; private set; }

        private static SKColor ToSKColor(MilColorF c) => new SKColor(
            Clamp(c.R), Clamp(c.G), Clamp(c.B), Clamp(c.A));

        private static byte Clamp(float v) =>
            v <= 0f ? (byte)0 : v >= 1f ? (byte)255 : (byte)(v * 255f + 0.5f);

        /// <summary>
        /// 字体目录：与 HelloMil 一样找 `<repo>/build/fonts`。
        /// 托管层的文本走 DirectWriteForwarder 骨架 + M1 文本栈，这里只是让
        /// RenderContext 带上正确的字体根（找不到就给 null，渲染层会自行回退）。
        /// </summary>
        private static string FontDirectoryProbe()
        {
            try
            {
                var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
                for (int depth = 0; dir != null && depth < 12; depth++, dir = dir.Parent)
                {
                    string candidate = System.IO.Path.Combine(dir.FullName, "build", "fonts");
                    if (System.IO.Directory.Exists(candidate)) return candidate;
                }
            }
            catch { /* 探测失败不该让呈现失败 */ }
            return null;
        }

        /// <summary>测试/新进程用：清空绑定表与计数。**不会**销毁任何窗口。</summary>
        internal static void Reset()
        {
            lock (_gate)
            {
                foreach (X11PresentationTarget t in _targets.Values)
                {
                    try { t.Dispose(); } catch { }
                }
                _targets.Clear();
                _bindErrors.Clear();
                _lastXSize.Clear();
                _lastLoggedSize.Clear();
                _loggedTargetPick.Clear();
                _channelByHwnd.Clear();
                // 自持的字形渲染器一并释放（它持有 SKTypeface/SKFont 缓存）
                try { GlyphRenderer?.Dispose(); } catch { /* 清理失败不改变语义 */ }
                GlyphRenderer = null;
                _presentCalls = 0;
                _framesPresented = 0;
                _bindAttempts = 0;
                _bindFailures = 0;
                _targetsSkipped = 0;
                NotDrawnCommands = 0;
                DrawnCommands = 0;
                // _display 保持打开：X 连接是进程级资源，反复开关没有好处。
            }
        }
    }

    /// <summary>
    /// 呈现路径的**诊断缓冲区**。
    ///
    /// 【为什么需要它】HRESULT 只能告诉调用方"失败了"，告诉不了"为什么"。
    ///   而托管层的 `HwndTarget` 拿到失败码只会抛一个笼统的异常。把最后 N 条原因
    ///   留在这里，测试可以直接断言、报告可以直接引用，避免"MILERR 无法定位"。
    /// </summary>
    internal static class MilDiagnostics
    {
        private const int Capacity = 64;
        private static readonly Queue<string> _notes = new Queue<string>();
        private static readonly object _gate = new object();

        internal static void Note(string message)
        {
            lock (_gate)
            {
                _notes.Enqueue(message);
                while (_notes.Count > Capacity) _notes.Dequeue();
            }
            // 同样的内容也进 stderr 台账（WPF_LINUX_MIL_TRACE=1 时）。
            // 原来这些原因只存在**进程内**：测试能断言，真应用（Phase 2 的 HelloWpf）
            // 看不见 —— 这正是"窗口出来了、截屏纯白、日志为空"卡了一轮的原因。
            MilPresentation.Trace("NOTE " + message);
        }

        internal static string[] Snapshot() { lock (_gate) return _notes.ToArray(); }

        internal static string Last() { lock (_gate) return _notes.Count == 0 ? null : _notes.ToArray()[^1]; }

        internal static void Reset() { lock (_gate) _notes.Clear(); }
    }
}
