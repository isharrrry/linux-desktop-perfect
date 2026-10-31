#!/usr/bin/env python3
"""输出一个**真实可解析**的 16x16 ICO 的 base64（供 SystemIcons.Unix.cs 内嵌）。

为什么需要：corefx 3.1 的 `SystemIcons.Unix.cs` 引用 `placeholder.ico` / `Mono.ico`，
但 corefx 仓库里**没有**这两个文件 ⇒ `SystemIcons..cctor` 抛 `FileNotFoundException`。

⚠️ 早期版本本脚本**自己合成** ICO（40 字节 BITMAPINFOHEADER + 像素 + AND mask），
但 `Icon.InitFromStreamWithSize` 拒绝它（`ArgumentException: Parameter 'stream'`）——
自己拼的字节流没有通过 System.Drawing 的严格校验。
现改为直接读一个**系统里真实存在的 .ico** 并转 base64（本仓用的是
`/usr/share/transmission/web/images/favicon.ico`，1406 字节）。
"""
import base64, sys
src = sys.argv[1] if len(sys.argv) > 1 else '/usr/share/transmission/web/images/favicon.ico'
print(base64.b64encode(open(src, 'rb').read()).decode())
