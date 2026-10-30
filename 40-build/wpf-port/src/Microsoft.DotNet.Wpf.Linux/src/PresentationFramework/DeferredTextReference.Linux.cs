// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-presentationframework-textbox-textdp-trace.py **生成**，不要手改。
//
// 内容 = 上游 `upstream/wpf/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/DeferredTextReference.cs` 逐字复制 + 1 处 T1c **只读插桩**（第 4 批 Q5…Q9）。
// 每次运行该脚本都会从上游重读重生成；锚点找不到 / 命中数不符时**报错退出**，不会静默产出未打补丁的副本。
//
// 为什么打：第 3 批实跑把命题改写成 —— **键入到了、容器改了（len=1 "A"→"AB"）、屏幕也画出来了**，
//   **本批只问**：`TextContainer.Changed` 到底 raise 了没有、有没有推到 `TextBox` 的 `Text` DP。
//   ⚠️「`Text` DP 是否**陈旧**」**本批不判**：那批 `changes=0`/`'seed-文本'` 经复核是**注入之前**的读数
//      （无信息）；须由**写后读数**判（T1c 报告 §34/§35）。
//   Q9 变更块计数配对 → Q5 Changed raise → Q6 TextBoxBase 转发 → Q7 TextBox 推 deferred → Q8 DP 解析出的字符串。
//   ⚠️ 探针**不读任何 DP**（读 DP 会触发 deferred 解析 ⇒ 观测者效应）；只读容器文本。
//   开关与 PF 其它批同一套（WPF_LINUX_INPUT_TRACE / WPF_LINUX_MSGFLOW_TRACE），缺省关、有界、只打印。
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Windows.Documents;

//
// Description: Proxy object passed to the property system to delay load
//              TextProperty values.
//

namespace System.Windows.Controls
{
    // Proxy object passed to the property system to delay load TextProperty
    // values.
    internal class DeferredTextReference : DeferredReference
    {
        //------------------------------------------------------
        //
        //  Constructors
        //
        //------------------------------------------------------

        #region Constructors

        internal DeferredTextReference(ITextContainer textContainer)
        {
            _textContainer = textContainer;
        }

        #endregion Constructors

        //------------------------------------------------------
        //
        //  Internal Methods
        //
        //------------------------------------------------------

        #region Internal Methods

        // Does the real work to calculate the current TextProperty value.
        internal override object GetValue(BaseValueSourceInternal valueSource)
        {
            // ── T1c 第 4 批 Q8a（只读插桩）：**DP 来读了**（Parent 为 null ⇒ 不会回调 TextBox）──
            System.Windows.Documents.WpfLinuxPfTextDpTrace.Q8Entry(this, _textContainer);

            string s = TextRangeBase.GetTextInternal(_textContainer.Start, _textContainer.End);

            // ── T1c Q8b：**取到的字符串**（这就是 DP 解析出的 .Text 新值）──
            System.Windows.Documents.WpfLinuxPfTextDpTrace.Q8Value(_textContainer, s);

            TextBox tb = _textContainer.Parent as TextBox;
            tb?.OnDeferredTextReferenceResolved(this, s);

            return s;
        }

        // Gets the type of the value it represents
        internal override Type GetValueType()
        {
            return typeof(string);
        }

        #endregion Internal Methods

        //------------------------------------------------------
        //
        //  Private Fields
        //
        //------------------------------------------------------

        #region Private Fields

        // TextContainer mapped to this object.
        private readonly ITextContainer _textContainer;

        #endregion Private Fields
     }
}
