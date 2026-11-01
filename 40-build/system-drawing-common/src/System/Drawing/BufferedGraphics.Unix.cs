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
using System.Drawing.Imaging;
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

        // 【K-332】**暂不实现** `RenderInternal`（K-331 的尝试导致段错误）。
        //   试过的实现：
        //     `using (Graphics g = Graphics.FromHdc(refTargetDC.Handle)) Render(g);`
        //   ⇒ **进程直接 core dump**（日志只有 1 行就 SIGSEGV）。
        //   原因（推测）：`refTargetDC.Handle` 在 Unix 上不是有效的 `GpGraphics*`
        //   （它是 Win32 HDC 语义的句柄，可能为 0 或别的指针），`FromHdc` 拿它去解引用就崩。
        //   **⇒ 这条路需要先查清 `refTargetDC.Handle` 在 Unix 上的实际值，才能安全实现。**
        //   在此之前保持抛异常（WinForms 会捕获，走 `DEFPAINT` 兜底，至少画面可见）。
        // 【K-341】真正的实现 —— WinForms 双缓冲合成（K-330/K-338 定位的最后一环）。
        //   背景：`Control.PaintBackground` 走 `BufferedGraphicsContext.Allocate(IntPtr, Rectangle)`
        //   ⇒ `_targetGraphics = null` ⇒ `Render()` 只能调本方法（传的是伪 HDC `0x1`）。
        //   原实现是字面 throw ⇒ 每个控件每次绘制抛 `NotImplementedException`（实测 4 个）。
        //
        //   实现方式：**不再尝试 `Graphics.FromHdc(0x1)`**（那会解引用地址 1 ⇒ SIGSEGV，K-332 实测），
        //   而是用 `LockBits` 拿 `membmp` 的像素，经 shim 的 `WpfLinuxWin32_DrawImage`
        //   用 `XPutImage` **一次贴到屏幕**（K-340 已实现该 API）。
        [DllImport("USER32.dll", EntryPoint = "WpfLinuxWin32_DrawImage")]
        private static extern int NativeDrawImage(IntPtr hdc, int x, int y, int w, int h,
                                                  IntPtr bgra, int stride);

        // 判断位图是否有"实质内容"：抽样扫一遍，只要有一个像素的 A 通道非 0 就算有。
        //   为什么抽样：640x460 = 29 万像素，全扫不慢但没必要；每 7x7 取一个点已经足够判断
        //   "这是不是一张空图"（`BufferedGraphics` 的成员位图如果没被画过就是全 0）。
        private static unsafe bool BitmapHasContent(BitmapData data, int w, int h)
        {
            byte* p0 = (byte *)data.Scan0;
            for (int y = 0; y < h; y += 7) {
                byte *row = p0 + y * data.Stride;
                for (int x = 0; x < w; x += 7) {
                    if (row[x * 4 + 3] != 0) return true;    // A != 0
                }
            }
            return false;
        }

        private void RenderInternal(HandleRef refTargetDC)
        {
            if (refTargetDC.Handle == IntPtr.Zero || membmp == null)
                throw new NotImplementedException();
            if (Environment.GetEnvironmentVariable("LDP_BG_TRACE") == "1") {
                try { Console.Error.WriteLine("[BG] RenderInternal hdc=0x" + refTargetDC.Handle.ToString("x")
                        + " size=" + membmp.Width + "x" + membmp.Height); } catch { }
            }
            int w = membmp.Width, h = membmp.Height;
            if (w <= 0 || h <= 0) return;
            BitmapData data = null;
            try {
                data = membmp.LockBits(new Rectangle(0, 0, w, h),
                                       ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                if (data == null || data.Scan0 == IntPtr.Zero) throw new NotImplementedException();
                // 【K-358】**先判断 `membmp` 是否有内容** ——
                //   实测（K-355）：不判断就直接贴，会把 `DEFPAINT` 兜底画的控件背景+标题**覆盖成空白**
                //   （截图从原版的 7046 字节掉到 921 字节）。那是"擦掉别人画的东西"。
                //   ⇒ 只在 `membmp` **确实有非零像素**时才贴；全零（全透明/未初始化）就跳过，
                //     让 `DEFPAINT` 的兜底内容保留。
                if (!BitmapHasContent(data, w, h)) {
                    if (Environment.GetEnvironmentVariable("LDP_BG_TRACE") == "1") {
                        try { Console.Error.WriteLine("[BG] membmp 全零，跳过贴图（保留 DEFPAINT 兜底）"); } catch { }
                    }
                    return;
                }
                NativeDrawImage(refTargetDC.Handle, 0, 0, w, h, data.Scan0, data.Stride);
            } finally {
                if (data != null) membmp.UnlockBits(data);
            }
        }
    }
}
