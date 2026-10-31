// Licensed to the .NET Foundation under one or more agreements.
// See the LICENSE file in the project root for more information.
//
// System.Drawing.SystemIcons.cs
//
// Authors:
//   Dennis Hayes (dennish@Raytek.com)
//   Andreas Nahr (ClassDevelopment@A-SoftTech.com)
//   Sebastien Pouliot  <sebastien@ximian.com>
//
// (C) 2002 Ximian, Inc
// Copyright (C) 2004, 2006 Novell, Inc (http://www.novell.com)
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

namespace System.Drawing
{
    public static class SystemIcons
    {

        static Icon[] icons;
        private const int Application_Winlogo = 0;
        private const int Asterisk_Information = 1;
        private const int Error_Hand = 2;
        private const int Exclamation_Warning = 3;
        private const int Question_ = 4;
        private const int Shield_ = 5;

        // 【ACC-028】corefx 3.1 的 Unix 版这里引用了 `placeholder.ico` / `Mono.ico`，
        //   而**这两个文件在 corefx 仓库里根本不存在**（是从 Mono 抄代码时的遗留引用）⇒
        //   `SystemIcons..cctor` 抛 `FileNotFoundException: Resource 'Mono.ico' was not found`。
        //   本仓改为**内嵌一个 16x16 的空白 ICO**（下面这串 base64 由 tools/gen-blank-ico.py 生成），
        //   保证 6 个索引都有非 null 的 Icon（调用方在异常路径上，null 会二次崩）。
        private const string BlankIcoBase64 = "AAABAAEAEBAAAAEACABoBQAAFgAAACgAAAAQAAAAIAAAAAEACAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACApUAAwOUAAUFlgAAAKsAAACtAAAAswAICJYACAiXAAkJmAADA7EACwuWAAwMmwAODpoAEhKdABUVnwA2NC4AOTcxADw6NAA9OzYAPTw2AB8fogBAPjkAIyOkAEREQABHRkAAR0dBAEhJRABISkUASUtIAEZJewBKTXkAU1dVAFRYVgBVWFYAVVlXAFpaVQBWWlgAV1pZAFdbWQBaXFgAWFxZAFhcWgBbXVoAWV1bAFtfXQBbYF0AXmFgAF9jYQBgY2IAQUHgAGBkYgBFReUAZGhmAGVoZgBkaGgAZGhpAGVpZwBmaWcARkbpAGVpaQBFRe0AbW5oAGpubABrbmwAam9rAG1xbwBwcm0AbXJvAHF1cwBydnQAc3d1AHN4dAB2eHMAdHh2AHp/fAB8f34AfIB+AH2CfwB+gn8AfoKAAH6DgAB/g4EAgIOCAH+EgQCCh4QAhomHAIaJiACHiokAh4uJAImNiwCLjowAjZGOAI6SkACPkpAAjZOPAI+TkQCRlJIAlJiWAJaYlwCJic4Al5qYAJebmQCbnZwAnqCfAJyingCbm9UAnZ3WAKetqQCtsq0Ara3cALO6tQC1u7YAtLu4ALW8uQC2vboAvcK9AL7FwQDJz8sAzNLNAM7T0QDP1NIA0tnTANPa1QDV3NcA197ZAOLk5ADk5uYAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAfyAkJCQkJCQkJCQkJCQifzBKU05QUFBPT09PTU1QUCRFWEEkJCQkJCQkJCQkOVwyV10tdXx8cxhIe3x8eiRkRGdaQ258bxUbFyN2fHQ1ZVZ/T0tobBMcHx8aGXNrSVhmf0lVVEIREh8fFQ89XlFMf38+XEB8fA8gHxB5fEdZRn9/L2EkenwqODgneXssYD5/fyZbQSYrNXFwNCgpP18uf39iIiQkJThycjQhJCQkUn9/f39/f384cnI0f39/f39/f2kUCAcKNnJyNAYHCBZtf38BBAUFCTd4dzQFBQUDDH9/ADM8PDo7fn00PDw8MQt/f2MNAgICHjQ0HQICAg5qf4ABAAAAAAAAAAAAAAAAAAAAAAAAgAAAAIABAACAAQAAgAEAAIABAACAAQAA/D8AAIABAACAAQAAgAEAAIABAAA=";

        private static Icon CreateBlankIcon()
        {
            byte[] raw = Convert.FromBase64String(BlankIcoBase64);
            using (var ms = new System.IO.MemoryStream(raw))
            {
                return new Icon(ms);
            }
        }

        static SystemIcons()
        {
            // we minimize the # of icons to load since most of them are duplicates
            icons = new Icon[6];
            for (int i = 0; i < icons.Length; i++) icons[i] = CreateBlankIcon();
        }

        // note: same as WinLogo (for Mono)
        public static Icon Application
        {
            get { return icons[Application_Winlogo]; }
        }

        // note: same as Information
        public static Icon Asterisk
        {
            get { return icons[Asterisk_Information]; }
        }

        // note: same as Hand
        public static Icon Error
        {
            get { return icons[Error_Hand]; }
        }

        // same as Warning
        public static Icon Exclamation
        {
            get { return icons[Exclamation_Warning]; }
        }

        // note: same as Error
        public static Icon Hand
        {
            get { return icons[Error_Hand]; }
        }

        // note: same as Asterisk
        public static Icon Information
        {
            get { return icons[Asterisk_Information]; }
        }

        public static Icon Question
        {
            get { return icons[Question_]; }
        }

        // note: same as Exclamation
        public static Icon Warning
        {
            get { return icons[Exclamation_Warning]; }
        }

        // note: same as Application (for Mono)
        public static Icon WinLogo
        {
            get { return icons[Application_Winlogo]; }
        }

        public static Icon Shield
        {
            get { return icons[Shield_]; }
        }

        /// <summary>
        /// .NET 10 的 API：按 <see cref="StockIconId"/> 取系统图标。
        /// 本后端没有 Windows 的 SHGetStockIconInfo，按语义折到手头这 8 个静态图标上；
        /// **永远不返回 null**（调用方在异常处理路径上，再 NRE 会把进程带崩）。
        /// </summary>
        public static Icon GetStockIcon(StockIconId stockIcon, int size)
        {
            switch (stockIcon)
            {
                case StockIconId.Info:        return Information;
                case StockIconId.Warning:     return Warning;
                case StockIconId.Error:       return Error;
                case StockIconId.Shield:      return Shield;
                case StockIconId.Help:        return Question;
                case StockIconId.Find:        return Question;
                default:                      return Application;
            }
        }
    }
}
