// Licensed to the .NET Foundation under one or more agreements.
// See the LICENSE file in the project root for more information.
//
// Copyright (C) 2005 Novell, Inc (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
// Authors:
//
//   Jordi Mas i Hernadez <jordimash@gmail.com>
//
//

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;

namespace System.Drawing
{
    public sealed partial class BufferedGraphics
    {
        private Rectangle size;
        private Bitmap membmp = null;
        private Graphics source = null;

        internal BufferedGraphics(Graphics targetGraphics, IntPtr targetDc, Rectangle targetRectangle)
        {
            _targetGraphics = targetGraphics;
            _targetDC = targetDc;
            size = targetRectangle;
            membmp = new Bitmap(size.Width, size.Height);
        }

        public Graphics Graphics
        {
            get
            {
                if (source == null && membmp != null)
                {
                    source = Graphics.FromImage(membmp);
                }

                return source;
            }
        }

        public void Dispose()
        {
            if (membmp != null)
            {
                membmp.Dispose();
                membmp = null;
            }

            if (source != null)
            {
                source.Dispose();
                source = null;
            }

            _targetGraphics = null;
        }

        public void Render(Graphics target)
        {
            if (target == null)
                return;

            target.DrawImage(membmp, size);
        }

        // 【K-330】实现 `RenderInternal` —— WinForms 的双缓冲合成路径。
        //   背景：`Control.PaintBackground` 走 `BufferedGraphics`，最终调
        //   `RenderInternal(HandleRef refTargetDC)`。Unix 后端原来是**字面 throw**，
        //   于是每个控件每次绘制都抛 `NotImplementedException`（实测 4 个），
        //   **最终合成那一步被丢掉** ⇒ 控件看起来是"死的"。
        //
        //   实现方式：复用同文件里**已经可用**的 `Render(Graphics target)`
        //   （它做 `target.DrawImage(membmp, size)`）。
        //   而 `Graphics.FromHdc(hdc)` 在本仓是可用的 —— 子代理实测
        //   `GetHdc()` 返回的 `GpGraphics*` 能被 `FromHdc` 消费并画出真像素。
        private void RenderInternal(HandleRef refTargetDC)
        {
            if (refTargetDC.Handle == IntPtr.Zero)
                return;
            using (Graphics g = Graphics.FromHdc(refTargetDC.Handle))
                Render(g);
        }
    }
}
