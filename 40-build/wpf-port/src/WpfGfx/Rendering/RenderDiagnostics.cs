// Licensed to the .NET Foundation under one or more agreements.
//
// 渲染诊断计数器。
//
// 存在的理由：渲染层最危险的失败模式不是抛异常，而是**静默画错或画少**——
// 一条不支持的指令如果不留痕，golden 图只会"看起来差不多"，等到端到端才发现。
// 这里把"没画出来"和"画了但语义不完整"分开计数，让未完成的部分是可枚举的。

using System.Collections.Generic;
using WpfGfx.Linux.Contracts;

namespace WpfGfx.Linux.Rendering
{
    internal sealed class RenderDiagnostics
    {
        private readonly Dictionary<MilDrawCommand, long> _notDrawn = new Dictionary<MilDrawCommand, long>();
        private readonly Dictionary<MilDrawCommand, long> _degraded = new Dictionary<MilDrawCommand, long>();

        /// <summary>本轮渲染执行过的指令总数。</summary>
        public long InstructionCount { get; private set; }

        /// <summary>完全没产出绘制的指令（缺资源、类型未支持、功能未实现）。</summary>
        public IReadOnlyDictionary<MilDrawCommand, long> NotDrawn => _notDrawn;

        /// <summary>画了但语义不完整（如 Exclude 映射存疑、arc 转换、忽略 guideline 吸附）。</summary>
        public IReadOnlyDictionary<MilDrawCommand, long> Degraded => _degraded;

        public void Reset()
        {
            InstructionCount = 0;
            _notDrawn.Clear();
            _degraded.Clear();
        }

        public void CountInstruction() => InstructionCount++;

        /// <summary>
        /// 未画指令的**有界类名后缀**，用来直接接在既有台账行后面：
        /// <c>未画种类 = 1 [MilPushOpacityMask×1]</c>。
        ///
        /// 【为什么加它】台账原先只有一个计数（`未画种类 = N`），**类名只能靠另外跑一趟带
        /// `[DRAW_CENSUS]` 的应用**才能推出来；而 census 与这本账**刻意互相独立**（那是有意设计，
        /// 不许为了省事把它们并成一本）。⇒ 把类名直接附在台账行后面，**下一次排查不用再跑一趟**。
        ///
        /// 【格式约定——**只允许追加**】返回串以**空格**开头、以 `]` 结尾；**没有任何未画时返回空串**，
        /// 因此调用方写成 <c>…未画种类 {N}{suffix}</c> 时，**N 的既有前缀逐字不变**。
        /// T3 的判据②是按字符串判 `未画种类 0` 的 ⇒ **前缀必须保持逐字一致**，本方法只负责尾部。
        ///
        /// 【有界】最多 8 类，超出打 `…+N`（台账行不能无限长）。
        /// </summary>
        public string NotDrawnSummary()
        {
            if (_notDrawn.Count == 0) return string.Empty;

            var parts = new List<string>();
            int shown = 0, hidden = 0;
            foreach (KeyValuePair<MilDrawCommand, long> kv in _notDrawn)
            {
                if (shown < MaxSummaryKinds) { parts.Add($"{kv.Key}×{kv.Value}"); shown++; }
                else hidden++;
            }
            string body = string.Join(",", parts) + (hidden > 0 ? $",…+{hidden}" : "");
            return $" [{body}]";
        }

        /// <summary>台账尾部最多列几类（其余折叠成 `…+N`）。</summary>
        internal const int MaxSummaryKinds = 8;

        /// <summary>
        /// 嵌套离屏渲染（VisualBrush 画它的 Visual）期间**暂时闭嘴**：
        /// 这本账是整后端共享的，离屏那一趟的"不支持"若记进来，会污染外层帧的读数
        /// （golden 用例把"未画出="打进输出，多出来的条目会被误当成外层漏画）。
        /// 用法：`using (Diagnostics.Suppress()) { ...离屏渲染... }`。
        /// </summary>
        public IDisposable Suppress() => new SuppressScope(this);

        private sealed class SuppressScope : IDisposable
        {
            private readonly RenderDiagnostics _owner;
            private readonly long _instructionCount;
            private readonly Dictionary<MilDrawCommand, long> _notDrawn;
            private readonly Dictionary<MilDrawCommand, long> _degraded;

            public SuppressScope(RenderDiagnostics owner)
            {
                _owner = owner;
                _instructionCount = owner.InstructionCount;
                _notDrawn = new Dictionary<MilDrawCommand, long>(owner._notDrawn);
                _degraded = new Dictionary<MilDrawCommand, long>(owner._degraded);
            }

            public void Dispose()
            {
                _owner.InstructionCount = _instructionCount;
                Restore(_owner._notDrawn, _notDrawn);
                Restore(_owner._degraded, _degraded);

                static void Restore(Dictionary<MilDrawCommand, long> target, Dictionary<MilDrawCommand, long> saved)
                {
                    target.Clear();
                    foreach (KeyValuePair<MilDrawCommand, long> kv in saved) target[kv.Key] = kv.Value;
                }
            }
        }

        public void RecordNotDrawn(MilDrawCommand command) => Bump(_notDrawn, command);

        public void RecordDegraded(MilDrawCommand command) => Bump(_degraded, command);

        private static void Bump(Dictionary<MilDrawCommand, long> table, MilDrawCommand command)
        {
            table.TryGetValue(command, out long n);
            table[command] = n + 1;
        }
    }
}
