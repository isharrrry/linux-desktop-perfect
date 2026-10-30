#!/usr/bin/env node
// 派生树生成器（applier 骨架）#applier
// 把只读上游快照复制成派生树，并按 09 §4 的补丁项做文本变换；只做确定性文本改写，不产出二进制。
// 用法：
//   node 40-build/appliers/apply.mjs [--src 40-build/upstream/wpf] [--dst 40-build/build/wpf] [--check]
// 补丁项（与 09 §4 对应）：
//   P1 TFM 改写（net10.0-windows* → net10.0）
//   P3 P/Invoke 目标名映射（生成 DllImportTargets.props，交给运行时 resolver）
//   P9 构建排除（redist/、tests/ 不进入派生树）
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '../..');
const argv = process.argv.slice(2);
const arg = (name, def) => {
  const i = argv.indexOf(`--${name}`);
  return i >= 0 && argv[i + 1] ? path.resolve(ROOT, argv[i + 1]) : def;
};
const SRC = arg('src', path.join(ROOT, '40-build/upstream/wpf'));
const DST = arg('dst', path.join(ROOT, '40-build/build/wpf'));
const CHECK = argv.includes('--check');
const SKIP_DIRS = new Set(['redist', 'tests', '.git']);
// `.cs` 也要在列：定点补丁（如 P19 的 STA 降级）落在源码文件上，不在列表里就静默不生效。
const TEXT_EXT = new Set(['.csproj', '.props', '.targets', '.csproj', '.projitems', '.props', '.targets', '.csdef', '.cs']);
const TFM_RE = /net1[01]\.0-windows(?:[\d.]+)?/g;

function walk(dir, base = dir, out = []) {
  for (const ent of fs.readdirSync(dir, { withFileTypes: true })) {
    if (SKIP_DIRS.has(ent.name)) continue;
    const p = path.join(dir, ent.name);
    if (ent.isDirectory()) walk(p, base, out);
    else out.push(path.relative(base, p));
  }
  return out;
}

if (!fs.existsSync(SRC)) {
  console.error(`ERROR 上游快照不存在：${path.relative(ROOT, SRC)}`);
  process.exit(1);
}

const files = walk(SRC).sort();
const rewrites = [];
const extraRewrites = [];
const plan = new Map(); // relPath -> content

// P16 等「定点文本补丁」：file（相对快照根）→ [{ patch, find, replace, why }]
// 纪律：只做确定性文本替换；每项必须有判据读数（见 09 §4 补丁项 16 与 40-build/build-matrix.json）。
const POINT_PATCHES = [
  {
    patch: 'P16',
    file: 'eng/WpfArcadeSdk/tools/Pbt.props',
    find: `<PbtTfm Condition="'$(MSBuildRuntimeType)'=='Core'">net6.0</PbtTfm>`,
    replace: `<PbtTfm Condition="'$(MSBuildRuntimeType)'=='Core'">net10.0</PbtTfm>`,
    why: 'PBT 工程的 TargetFrameworks 是 net472;net10.0，而 Pbt.props 写死 net6.0 → NETSDK1005（补丁项 16）',
  },
  {
    patch: 'P19',
    file: 'src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/Input/Stylus/Common/DynamicRendererThreadManager.cs',
    find: `                inkingThread.SetApartmentState(ApartmentState.STA);`,
    replace: `                // 【WPF-on-Linux 补丁 P19】STA 是 COM 的线程单元语义，.NET 在非 Windows 上
                //   \`Thread.SetApartmentState\` 直接抛 \`PlatformNotSupportedException: COM Interop is not
                //   supported on this platform.\`。上游这里只是"让墨迹线程是 STA"，Linux 上没有 COM
                //   可隔离 ⇒ 降级为**不改单元状态**（线程照常起，Dispatcher 照常跑），并打一行诊断。
                //   实测触发链：InkCanvas 可见 → WispStylusPlugInCollection.UpdateState →
                //   DynamicRenderer.CreateRealTimeVisuals → 本函数 ⇒ 整个样本进程启动即崩（D-013）。
                try
                {
                    inkingThread.SetApartmentState(ApartmentState.STA);
                }
                catch (PlatformNotSupportedException)
                {
                    System.Diagnostics.Debug.WriteLine("WPF-on-Linux: 墨迹线程的 STA 单元不可用（无 COM），按 MTA 继续。");
                }`,
    why: 'InkCanvas 的实时墨迹线程要求 STA（COM）→ Linux 上 PlatformNotSupportedException 直接崩（补丁项 19，D-013）',
  },
  {
    patch: 'P20',
    file: 'src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/InterOp/HwndSource.cs',
    find: `                HwndTarget hwndTarget = _hwndTarget;
                if (hwndTarget != null)
                {
                    result = hwndTarget.HandleMessage((WindowMessage)msg, wParam, lParam);`,
    replace: `                HwndTarget hwndTarget = _hwndTarget;
                // 【WPF-on-Linux 补丁 P20】把 \`WM_DROPFILES\` 转成应用的 \`Drop\` 事件。
                //   背景：WPF 的拖放走 OLE（RegisterDragDrop + IDropTarget），Linux 上没有 OLE；
                //   而本项目的 shim 已把 XDND 拖放翻译成 \`WM_DROPFILES\` + HDROP（见 D-011）。
                //   上游 PresentationCore 里**没有任何 WM_DROPFILES 处理**（grep 实证），所以这里补上：
                //   读 HDROP 的文件列表 → 构造 DataObject(FileDrop) → 在 RootVisual 上触发 DropEvent。
                //   只对"没有 OLE 拖放目标"的情况生效（Linux 恒真；Windows 上 RegisterDragDrop 会先接手）。
                if (msg == 0x0233 /* WM_DROPFILES */ && wParam != IntPtr.Zero)
                {
                    Console.Error.WriteLine("[P20] WM_DROPFILES wParam=0x" + wParam.ToString("x"));
                    Console.Error.Flush();
                    try
                    {
                        string[] paths = WpfLinuxDroppedFiles(wParam);
                        if (paths != null && paths.Length > 0 && hwndTarget != null && hwndTarget.RootVisual != null)
                        {
                            DataObject data = new DataObject(DataFormats.FileDrop, paths);
                            DragEventArgs dropArgs = new DragEventArgs(
                                data, DragDropKeyStates.None, DragDropEffects.Copy,
                                hwndTarget.RootVisual, new Point(0, 0));
                            dropArgs.RoutedEvent = DragDrop.DropEvent;
                            hwndTarget.RootVisual.RaiseEvent(dropArgs);
                            handled = true;
                        }
                    }
                    catch (Exception)
                    {
                        // 拖放是"锦上添花"：任何异常都不能把窗口消息循环打崩
                    }
                }
                if (hwndTarget != null)
                {
                    result = hwndTarget.HandleMessage((WindowMessage)msg, wParam, lParam);`,
    why: 'WPF 拖放走 OLE（Linux 无），而 shim 已把 XDND 翻成 WM_DROPFILES + HDROP → 补上这条转换（补丁项 20，D-011）',
  },
  {
    patch: 'P20b',
    file: 'src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/InterOp/HwndSource.cs',
    find: `        private IntPtr HwndTargetFilterMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)`,
    replace: `        /// <summary>
        /// 【WPF-on-Linux 补丁 P20b】从 \`WM_DROPFILES\` 的 HDROP 里读出文件路径列表。
        /// HDROP 由本项目的 win32 shim 构造（X DnD 的 uri-list 翻译而来），
        /// 通过 \`DragQueryFileW\`（iFile = 0xFFFFFFFF 取个数）与 \`DragFinish\` 访问 —— 这三个
        /// 入口在 libwpfwin32.so 里实现，且已加入映射表（kernel32/user32 组）。
        /// </summary>
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern uint WpfLinuxDragQueryFileW(IntPtr hDrop, uint iFile, char[] lpszFile, uint cch);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void WpfLinuxDragFinish(IntPtr hDrop);

        private static string[] WpfLinuxDroppedFiles(IntPtr hDrop)
        {
            try
            {
                int count = (int)WpfLinuxDragQueryFileW(hDrop, 0xFFFFFFFFu, null, 0);
                if (count <= 0) return null;
                string[] paths = new string[count];
                for (int i = 0; i < count; ++i)
                {
                    int len = (int)WpfLinuxDragQueryFileW(hDrop, (uint)i, null, 0);
                    if (len <= 0) { paths[i] = string.Empty; continue; }
                    char[] buf = new char[len + 1];
                    WpfLinuxDragQueryFileW(hDrop, (uint)i, buf, (uint)buf.Length);
                    paths[i] = new string(buf, 0, len);
                }
                return paths;
            }
            finally
            {
                WpfLinuxDragFinish(hDrop);
            }
        }

        private IntPtr HwndTargetFilterMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)`,
    why: 'P20 的辅助方法：从 HDROP 读文件列表（补丁项 20b，D-011）',
  },
];

for (const rel of files) {
  const abs = path.join(SRC, rel);
  const ext = path.extname(rel).toLowerCase();
  const buf = fs.readFileSync(abs);
  if (!TEXT_EXT.has(ext)) continue;
  let text = buf.toString('utf8');
  let touched = false;

  for (const pp of POINT_PATCHES) {
    if (rel.split(path.sep).join('/') !== pp.file) continue;
    if (!text.includes(pp.find)) {
      console.error(`ERROR ${pp.patch} 定点补丁未命中：${pp.file}（上游可能已修，请复核后删掉该项）`);
      process.exit(1);
    }
    text = text.split(pp.find).join(pp.replace);
    extraRewrites.push({ file: pp.file, patch: pp.patch, why: pp.why });
    touched = true;
  }

  if (!TFM_RE.test(text)) {
    if (touched) plan.set(rel, text);
    continue;
  }
  TFM_RE.lastIndex = 0;
  const next = text.replace(TFM_RE, 'net10.0');
  if (next !== text) {
    const n = (text.match(TFM_RE) || []).length;
    rewrites.push({ file: rel, patch: 'P1', from: 'net1x.0-windows*', to: 'net10.0', occurrences: n });
    plan.set(rel, next);
  } else if (touched) {
    plan.set(rel, text);
  }
}

const dllTargets = {
  generated_by: '40-build/appliers/apply.mjs',
  note: 'P3：托管层 DllImport 的库名 → Linux .so 的映射；运行时由 resolver 读取，构建期只落表。',
  mapping: [
    { from: 'user32.dll', to: 'libwpfwin32.so' },
    { from: 'gdi32.dll', to: 'libwpfwin32.so' },
    { from: 'kernel32.dll', to: 'libwpfwin32.so' },
    { from: 'gdiplus.dll', to: 'libwpfwin32.so' },
    { from: 'PresentationNative_cor3.dll', to: 'libwpfwin32.so' },
    { from: 'wpfgfx_cor3.dll', to: 'libwpfgfx_linux.so' },
    { from: 'WindowsCodecs.dll', to: 'libwpfwic.so' },
    { from: 'dwrite.dll', to: 'libdwlinux.so' },
  ],
};

if (CHECK) {
  if (!fs.existsSync(DST)) {
    console.error(`ERROR 派生树不存在：${path.relative(ROOT, DST)}（跑一次不带 --check）`);
    process.exit(1);
  }
  const problems = [];
  for (const [rel, content] of plan) {
    const abs = path.join(DST, rel);
    if (!fs.existsSync(abs)) problems.push(`缺文件 ${rel}`);
    else if (fs.readFileSync(abs, 'utf8') !== content) problems.push(`未按补丁改写 ${rel}`);
  }
  const report = path.join(DST, 'patch-report.json');
  if (!fs.existsSync(report)) problems.push('缺 patch-report.json');
  for (const p of problems) console.error(`ERROR ${p}`);
  console.log(`apply --check: 计划改写 ${rewrites.length} 个文件，问题 ${problems.length}`);
  process.exit(problems.length ? 1 : 0);
}

fs.rmSync(DST, { recursive: true, force: true });
let copied = 0;
for (const rel of files) {
  const to = path.join(DST, rel);
  fs.mkdirSync(path.dirname(to), { recursive: true });
  if (plan.has(rel)) fs.writeFileSync(to, plan.get(rel), 'utf8');
  else fs.copyFileSync(path.join(SRC, rel), to);
  copied += 1;
}
// P4：源覆盖——dnceng 私有源在本机不可达（实测 Resource temporarily unavailable），派生树统一走 nuget.org
fs.writeFileSync(
  path.join(DST, 'NuGet.config'),
  '<?xml version="1.0" encoding="utf-8"?>\n<configuration>\n  <!-- 由 apply.mjs 生成（P4）：dnceng 源不可达，统一走 nuget.org -->\n  <packageSources>\n    <clear />\n    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />\n  </packageSources>\n</configuration>\n',
  'utf8',
);

// 防止本仓 40-build/Directory.Packages.props 泄漏进派生构建（上游不用中央包管理）
if (!fs.existsSync(path.join(SRC, 'Directory.Packages.props'))) {
  fs.writeFileSync(
    path.join(DST, 'Directory.Packages.props'),
    '<Project>\n  <!-- 由 apply.mjs 生成：挡掉上层 40-build/Directory.Packages.props 的中央包管理（上游不含此文件） -->\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n</Project>\n',
    'utf8',
  );
}

fs.writeFileSync(path.join(DST, 'DllImportTargets.props'), `<Project>\n  <!-- 由 apply.mjs 生成（P3）：库名 → .so 映射，resolver 在运行期读取 -->\n  <ItemGroup>\n${dllTargets.mapping
  .map((m) => `    <DllImportTarget Include="${m.from}" LinuxLibrary="${m.to}" />`)
  .join('\n')}\n  </ItemGroup>\n</Project>\n`, 'utf8');

const snap = path.join(SRC, 'SNAPSHOT.json');
const snapshot = fs.existsSync(snap) ? JSON.parse(fs.readFileSync(snap, 'utf8')) : null;
fs.writeFileSync(
  path.join(DST, 'patch-report.json'),
  JSON.stringify(
    {
      generated_by: '40-build/appliers/apply.mjs',
      source: path.relative(ROOT, SRC),
      source_commit: snapshot ? snapshot.commit : null,
      generated_at_note: '确定性：同输入同输出；不含时间戳',
      skipped_dirs: [...SKIP_DIRS],
      copied_files: copied,
      patches: [
        { id: 'P1', desc: 'TFM 改写 net1x.0-windows* → net10.0', files: rewrites },
        { id: 'P16', desc: 'PBT 接线（PbtTfm net6.0 → net10.0）', files: extraRewrites.filter((r) => r.patch === 'P16') },
        { id: 'P3', desc: 'DllImport 库名 → .so 映射', files: ['DllImportTargets.props'], mapping: dllTargets.mapping },
        { id: 'P4', desc: '源覆盖（dnceng → nuget.org）', files: ['NuGet.config'] },
        { id: 'P9', desc: '构建排除', skipped: [...SKIP_DIRS] },
      ],
      not_yet: ['P2 程序集身份', 'P4 平台常量/注册表替换', 'P5 资源与主题', 'P6 PBT 定位', 'P7 pack 组装', 'P8 兼容层开关', 'P10 必抛件替换', 'P17 SDK 名归一', 'P18 摘除原生工程引用'],
    },
    null,
    2,
  ) + '\n',
  'utf8',
);

console.log(`apply: 复制 ${copied} 个文件 → ${path.relative(ROOT, DST)}；P1 改写 ${rewrites.length} 个文件；P16 定点 ${extraRewrites.length} 处；P3 映射 ${dllTargets.mapping.length} 条`);

// ── K-002 的自动化：重建派生树会清掉 `artifacts/`（含 PresentationBuildTasks）────────
// 【为什么在这里做】上游 csproj 的 `_LdpCheckPbtLocator` 一旦找不到 PBT 就**直接报错**
//   （LDP-E-PACK-210），而 `apply` 是"重建派生树"的动作 ⇒ 每次 apply 之后样本就编不了。
//   这个坑已经人工处理过三次（K-002），34 s × 3 全是白花；所以在这里自动补回：
//   只在 PBT 缺失时才跑（存在时不碰，保证 apply 的幂等/无副作用）。
//   `--check` 模式不写盘（那是"只比对不落盘"的语义），也不重建。
if (!CHECK) {
  const pbt = path.join(DST, 'artifacts/bin/PresentationBuildTasks/Release/net10.0/PresentationBuildTasks.dll');
  if (!fs.existsSync(pbt)) {
    console.log('apply: PBT 缺失（K-002，apply 重建派生树会清掉 artifacts/）→ 自动重建 PresentationBuildTasks …');
    const { spawnSync } = await import('node:child_process');
    const r = spawnSync('bash', ['40-build/build.sh',
      'src/Microsoft.DotNet.Wpf/src/PresentationBuildTasks/PresentationBuildTasks.csproj'],
      { stdio: 'inherit', cwd: ROOT });
    if (r.status !== 0) {
      console.log('apply: ⚠️ 自动重建 PBT 失败（后续 dotnet build 会报 LDP-E-PACK-210，请手动跑上面那条命令）');
    } else {
      console.log('apply: PBT 已重建');
    }
  }
}
