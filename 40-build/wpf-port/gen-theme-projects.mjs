#!/usr/bin/env node
// 生成 5 个主题程序集的 Linux 工程（Aero / Aero2 / AeroLite / Luna / Royale）
//
// 为什么用生成器而不是手写 5 份 csproj：主题工程的价值 90% 在同一条「XAML 标记编译链」上
// （补丁块 A/B/E/F 的接线与自检），手写 5 份必然漂移 —— 其中一份漏了 `WpfMarkupCompile.Linux.targets`
// 就会"构建成功但 BAML 是空的"。这里以可复算的模板固定这条链，只让**主题名与源文件清单**变化。
//
// 用法：
//   node 40-build/wpf-port/gen-theme-projects.mjs            # 生成/更新 5 个工程
//   node 40-build/wpf-port/gen-theme-projects.mjs --check    # 只比对，不改文件（不一致即非零退出）

import fs from 'node:fs';
import path from 'node:path';

const ROOT = path.resolve(path.dirname(new URL(import.meta.url).pathname), '../..');
const SRC = path.join(ROOT, '40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src');
const UPSTREAM = path.join(ROOT, '40-build/build/wpf/src/Microsoft.DotNet.Wpf/src');
const THEMES = ['Aero', 'Aero2', 'AeroLite', 'Luna', 'Royale'];
const TEMPLATE = path.join(SRC, 'PresentationFramework.Classic/PresentationFramework.Classic.Linux.csproj');

const check = process.argv.includes('--check');
const selfBuilt = '$(WpfLinuxSelfBuiltConfiguration)';

const listFiles = (dir, ext) => {
  const out = [];
  const walk = (d) => {
    if (!fs.existsSync(d)) return;
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      const p = path.join(d, e.name);
      if (e.isDirectory()) {
        if (e.name === 'ref' || e.name === 'obj' || e.name === 'bin') continue;
        walk(p);
      } else if (e.name.endsWith(ext)) {
        out.push(p);
      }
    }
  };
  walk(dir);
  return out.sort();
};

// 相对上游根的文件清单（Compile/Page 都用 $(UpstreamWpfRoot) 前缀，与 Classic 同风格）
const upstreamRel = (abs) => abs.slice(UPSTREAM.length + 1).split(path.sep).join('/');

const template = fs.readFileSync(TEMPLATE, 'utf8');
const patchA = template.indexOf('  <!-- ==== WPF-on-Linux 补丁块 A');
if (patchA < 0) {
  console.error('gen-theme-projects: Classic 模板里找不到「补丁块 A」标记 —— 模板结构变了，不做静默猜测（LDP-E-THEME-001）');
  process.exit(1);
}
// 头段必须截在 Classic 的 **Compile 项组之前** —— 否则模板里 Classic 的源文件清单会被带进每个主题
// （实测：Aero 工程里跑出 ClassicBorderDecorator.cs 的 CS2001）。
const compileMarker = '  <ItemGroup>\n    <Compile Include="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/Shared/RefAssemblyAttrs.cs" />';
const compileAt = template.indexOf(compileMarker);
if (compileAt < 0) {
  console.error('gen-theme-projects: Classic 模板里找不到 Compile 项组起点（LDP-E-THEME-005）');
  process.exit(1);
}
const headRaw = template.slice(0, compileAt);
const tailRaw = template.slice(patchA);

// 路径前缀：Classic 模板里 Compile/Page 都写 `$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/...`
// （UpstreamWpfRoot = 派生树根 `40-build/build/wpf`）。少写这一层就是实测踩过的
// `BG1002 File '.../40-build/build/wpf/Themes/...' cannot be found`。
const UP = 'src/Microsoft.DotNet.Wpf/src/';
// Compile 清单**以上游 csproj 为准**（手写清单必然漂移：实测 Luna 编 KnownTypeHelper 需要
// Themes/Shared/BulletChrome.cs，而 Aero 目录自带同名文件 —— 猜子集就会 CS0234/MC3074）。
const THEME_REL = (theme) => UP + 'Themes/PresentationFramework.' + theme + '/';

const parseCompileItems = (theme) => {
  const proj = path.join(UPSTREAM, 'Themes', `PresentationFramework.${theme}`, `PresentationFramework.${theme}.csproj`);
  const raw = fs.readFileSync(proj, 'utf8');
  const out = [];
  const skipped = [];
  for (const m of raw.matchAll(/<Compile Include="([^"]+)"/g)) {
    let v = m[1].replace(/\\/g, '/');
    if (v.includes('$(WpfSharedDir)')) { out.push(UP + 'Shared/' + v.slice(v.indexOf(')') + 2)); continue; }
    if (v.includes('$(WpfSourceDir)')) { out.push(UP + v.slice(v.indexOf(')') + 2)); continue; }
    if (v.includes('$(') || v.startsWith('..') || v.includes('PresentationFramework.')) { skipped.push(m[1]); continue; }
    out.push(THEME_REL(theme) + v);
  }
  return { out: [...new Set(out)].sort(), skipped };
};

const results = [];
for (const theme of THEMES) {
  const themeDir = path.join(UPSTREAM, 'Themes', `PresentationFramework.${theme}`);
  if (!fs.existsSync(themeDir)) {
    console.error(`gen-theme-projects: 缺上游主题目录 ${themeDir}（LDP-E-THEME-002）`);
    process.exit(1);
  }
  const xaml = listFiles(themeDir, '.xaml').map((f) => UP + upstreamRel(f));
  const parsed = parseCompileItems(theme);
  const cs = parsed.out;
  if (!cs.length || !xaml.length) {
    console.error(`gen-theme-projects: ${theme} 的源文件清单为空（cs=${cs.length} xaml=${xaml.length}）（LDP-E-THEME-003）`);
    process.exit(1);
  }

  // 头段：换 AssemblyName / DefineConstants / Classic 字样
  let head = headRaw
    .split('PresentationFramework.Classic').join(`PresentationFramework.${theme}`)
    .split('THEME_CLASSIC').join(`THEME_${theme.toUpperCase()}`);

  if (!cs.length) {
    console.error(`gen-theme-projects: ${theme} 从上游 csproj 解析出的 Compile 清单为空（LDP-E-THEME-006）`);
    process.exit(1);
  }
  const compileLines = cs
    .map((f) => `    <Compile Include="$(UpstreamWpfRoot)${f}" />`)
    .join('\n');
  const pageLines = xaml
    .map((f) => {
      const base = path.basename(f);
      return `    <Page Include="$(UpstreamWpfRoot)${f}">\n      <Link>Themes\\${base}</Link>\n      <Generator>MSBuild:Compile</Generator>\n      <XamlRuntime>Wpf</XamlRuntime>\n      <SubType>Designer</SubType>\n    </Page>`;
    })
    .join('\n');

  // 尾段（补丁块 A/B/E/F）：把 Classic 名字与 Page 清单替换掉
  const classicPageBlock = /  <ItemGroup>\n    <Page Include="\$\(UpstreamWpfRoot\)[^"]*">[\s\S]*?<\/ItemGroup>/;
  if (!classicPageBlock.test(tailRaw)) {
    console.error('gen-theme-projects: Classic 模板里找不到 Page 项块（LDP-E-THEME-004）');
    process.exit(1);
  }
  let tail = tailRaw
    .split('PresentationFramework.Classic').join(`PresentationFramework.${theme}`)
    .split('WpfLinuxClassic').join(`WpfLinux${theme}`)
    .split('_WpfClassicIth')
    .join(`_Wpf${theme}Ith`)
    .split('_WpfLinuxClassicBaml')
    .join(`_WpfLinux${theme}Baml`)
    .split('Classic.xaml').join(path.basename(xaml[0]))
    .replace(classicPageBlock, `  <ItemGroup>\n${pageLines}\n  </ItemGroup>`);

  const proj = `${head}  <ItemGroup>\n${compileLines}\n  </ItemGroup>\n  <!-- 自产程序集身份：与框架门面同一套 10.0.0.0 / FileVersion（ACC-004） -->\n  <ItemGroup>\n    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/shims/LinuxAssemblyIdentity.cs" />\n  </ItemGroup>\n${tail}`;

  const dir = path.join(SRC, `PresentationFramework.${theme}`);
  const file = path.join(dir, `PresentationFramework.${theme}.Linux.csproj`);
  const exists = fs.existsSync(file);
  const same = exists && fs.readFileSync(file, 'utf8') === proj;
  if (!same) {
    if (check) {
      console.error(`gen-theme-projects --check: ${path.relative(ROOT, file)} 与模板生成结果不一致`);
      process.exitCode = 1;
    } else {
      fs.mkdirSync(dir, { recursive: true });
      fs.writeFileSync(file, proj);
    }
  }
  results.push({ theme, cs: cs.length, xaml: xaml.length, skipped: parsed.skipped.length, file: path.relative(ROOT, file), changed: !same });
  // 未使用的模板变量保持显式（避免后续改动误删 selfBuilt 语义）
  void selfBuilt;
}

if (check && process.exitCode) process.exit(process.exitCode);
console.log(`gen-theme-projects: ${results.length} 个主题工程（${results.map((r) => `${r.theme} cs=${r.cs} xaml=${r.xaml} 跳过=${r.skipped}${r.changed ? ' 已更新' : ' 无变化'}`).join('；')}）`);
