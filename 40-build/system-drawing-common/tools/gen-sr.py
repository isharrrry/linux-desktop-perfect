#!/usr/bin/env python3
"""从 src/Resources/Strings.resx 生成 SR 类（corefx 用它自己的构建系统生成，我们这份精简构建里没有）。

用法：python3 tools/gen-sr.py
产物：src/CommonInterop/SR.Generated.cs
"""
import xml.etree.ElementTree as ET
import io, os

RESX = 'src/Resources/Strings.resx'
OUT = 'src/CommonInterop/SR.Generated.cs'

names = [d.get('name') for d in ET.parse(RESX).getroot().findall('data') if d.get('name')]
lines = [
    '// 由 40-build/system-drawing-common/tools/gen-sr.py 从 src/Resources/Strings.resx 生成。',
    '// corefx 用它的构建系统生成这份代码（GenerateResourcesSource），我们这份精简构建里没有，',
    '// 所以自己生成一份：只为让 `SR.Xxx` / `SR.Format(...)` 编译通过（文案走资源文件，不在此处）。',
    'using System.Globalization;',
    '',
    'namespace System.Drawing',
    '{',
    '    internal static class SR',
    '    {',
]
for n in names:
    lines.append(f'        internal static string {n.replace(".", "_").replace("-", "_")} => null;')
lines += [
    '',
    '        internal static string Format(string format, params object[] args)',
    '            => format == null ? null : string.Format(CultureInfo.InvariantCulture, format, args);',
    '        internal static string GetString(string name) => null;',
    '    }',
    '}',
    '',
]
os.makedirs(os.path.dirname(OUT), exist_ok=True)
io.open(OUT, 'w', encoding='utf-8').write('\n'.join(lines))
print(f'已生成 {OUT}（{len(names)} 个资源项）')
