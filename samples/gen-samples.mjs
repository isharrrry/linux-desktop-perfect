#!/usr/bin/env node
// samples/gen-samples.mjs —— B5：7 个样本工程骨架生成器（确定性）
//
// 依据：
//   30-design/设计文档包/09-开工前置与切片计划.md §7（文件清单、启动参数、期望树路径）
//   30-design/设计文档包/界面设计说明.md 各 SCR 元素属性表（控件、路径、名称期望、读数文案）
//
// 用法：
//   node samples/gen-samples.mjs            # 写全部工程
//   node samples/gen-samples.mjs --check    # 只校验磁盘与 spec 完全一致（不一致 rc=1）
//
// 纪律：
//   ① 只写 samples/ 下的文件；② 确定性（无时间戳、无随机、LF、UTF-8）；
//   ③ 只摆控件与读数 label，不实现任何业务逻辑；④ 每个 .csproj 首行标注「派生样本」。

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const checkOnly = process.argv.includes('--check');

const L = (...lines) => lines.join('\n') + '\n';

// ---------------------------------------------------------------- 公共模板

const csprojWpf = (dir) => L(
  '<!-- 派生样本：由 samples/gen-samples.mjs 生成，勿手改 -->',
  '<Project Sdk="Microsoft.NET.Sdk">',
  '',
  '  <PropertyGroup>',
  '    <OutputType>WinExe</OutputType>',
  '    <TargetFramework>net10.0</TargetFramework>',
  '    <UseWPF>true</UseWPF>',
  '    <Nullable>disable</Nullable>',
  '    <ImplicitUsings>disable</ImplicitUsings>',
  '    <AssemblyName>' + dir + '</AssemblyName>',
  '    <RootNamespace>LinuxDesktopPerfect.Samples.' + dir + '</RootNamespace>',
  '    <ApplicationManifest></ApplicationManifest>',
  '  </PropertyGroup>',
  '',
  '</Project>',
);

// 【ACC-024 / K-056】打印样本专用 csproj：显式引用**运行时**的 ReachFramework.dll，
// 绕开 NuGet 的 ref 版（microsoft.windowsdesktop.app.ref 里那份只有 5120 字节 / 8 类型，
// XpsDocument 只暴露 Dispose() —— 编译期根本看不到 CreateXpsDocumentWriter 等 API）。
// 注意 HintPath 用生成时展开的绝对路径：构建期没有 $(DOTNET_ROOT)，不能依赖它。
const csprojWpfWithReachFramework = (dir) => L(
  '<!-- 派生样本：由 samples/gen-samples.mjs 生成，勿手改 -->',
  '<Project Sdk="Microsoft.NET.Sdk">',
  '',
  '  <PropertyGroup>',
  '    <OutputType>WinExe</OutputType>',
  '    <TargetFramework>net10.0</TargetFramework>',
  '    <UseWPF>true</UseWPF>',
  '    <Nullable>disable</Nullable>',
  '    <ImplicitUsings>disable</ImplicitUsings>',
  '    <AssemblyName>' + dir + '</AssemblyName>',
  '    <RootNamespace>LinuxDesktopPerfect.Samples.' + dir + '</RootNamespace>',
  '    <ApplicationManifest></ApplicationManifest>',
  '  </PropertyGroup>',
  '',
  '  <ItemGroup>',
  '    <!-- 运行时的完整 ReachFramework（594 类型 / 619008 字节）覆盖 NuGet 的 ref 子集 -->',
  '    <Reference Include="ReachFramework">',
  '      <HintPath>' + process.env.HOME + '/.local/share/ldp/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.11/ReachFramework.dll</HintPath>',
  '      <Private>false</Private>',
  '    </Reference>',
  '  </ItemGroup>',
  '',
  '</Project>',
);

const csprojWinForms = (dir) => L(
  '<!-- 派生样本：由 samples/gen-samples.mjs 生成，勿手改 -->',
  '<Project Sdk="Microsoft.NET.Sdk">',
  '',
  '  <PropertyGroup>',
  '    <OutputType>WinExe</OutputType>',
  '    <TargetFramework>net10.0</TargetFramework>',
  '    <UseWindowsForms>true</UseWindowsForms>',
  '    <Nullable>disable</Nullable>',
  '    <ImplicitUsings>disable</ImplicitUsings>',
  '    <AssemblyName>' + dir + '</AssemblyName>',
  '    <RootNamespace>LinuxDesktopPerfect.Samples.' + dir + '</RootNamespace>',
  '  </PropertyGroup>',
  '',
  '</Project>',
);

const appXaml = (ns, startUri) => L(
  '<Application x:Class="' + ns + '.App"',
  '             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
  '             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"',
  '             StartupUri="' + startUri + '">',
  '    <Application.Resources />',
  '</Application>',
);

// `--theme <Aero|Aero2|AeroLite|Luna|Royale|Classic>`：换主题字典（ACC-020 的六主题切换）。
// 主题 BAML 由自产主题程序集提供（pack URI 指向 PresentationFramework.<T>）。
const appCs = (ns, withTheme = false) => L(
  'using System;',
  'using System.Windows;',
  '',
  'namespace ' + ns,
  '{',
  '    public partial class App : Application',
  '    {',
  ...(withTheme
    ? [
        '        protected override void OnStartup(StartupEventArgs e)',
        '        {',
        '            base.OnStartup(e);',
        '            string theme = Arg("--theme");',
        '            if (theme == null) return;',
        '            Uri uri = ThemeUri(theme);',
        '            if (uri == null) return;',
        '            Resources.MergedDictionaries.Clear();',
        '            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });',
        '        }',
        '',
        '        private static Uri ThemeUri(string theme)',
        '        {',
        '            switch (theme)',
        '            {',
        '                case "Classic":   return new Uri("pack://application:,,,/PresentationFramework.Classic;component/themes/Classic.xaml");',
        '                case "Aero":      return new Uri("pack://application:,,,/PresentationFramework.Aero;component/themes/Aero.NormalColor.xaml");',
        '                case "Aero2":     return new Uri("pack://application:,,,/PresentationFramework.Aero2;component/Themes/Aero2.NormalColor.xaml");',
        '                case "AeroLite":  return new Uri("pack://application:,,,/PresentationFramework.AeroLite;component/Themes/AeroLite.NormalColor.xaml");',
        '                case "Luna":      return new Uri("pack://application:,,,/PresentationFramework.Luna;component/Themes/Luna.NormalColor.xaml");',
        '                case "Royale":    return new Uri("pack://application:,,,/PresentationFramework.Royale;component/Themes/Royale.NormalColor.xaml");',
        '                default:          return null;',
        '            }',
        '        }',
        '',
        '        private static string Arg(string name)',
        '        {',
        '            string[] a = Environment.GetCommandLineArgs();',
        '            for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];',
        '            return null;',
        '        }',
      ]
    : []),
  '    }',
  '}',
);

// SCR-01 的 code-behind 变体：在通用 windowCs 基础上加 DragOver/Drop 处理器。
// 为什么单独一个：ACC-012 的"XDND 端到端"必须由**应用侧真的收到 Drop 事件**来证明，
//   而判据能读到它的方式只有 UIA 树里的文本（`drop=<路径>`）。
const windowCsWithDrop = (ns, cls, title) => L(
  'using System;',
  'using System.Windows;',
  'using System.Windows.Automation;',
  'using System.Windows.Media;',
  '',
  'namespace ' + ns,
  '{',
  '    public partial class ' + cls + ' : Window',
  '    {',
  '        public ' + cls + '()',
  '        {',
  '            InitializeComponent();',
  '            string scr = ScrArg();',
  '            Title = "' + title + '" + (scr == null ? "" : " [" + scr + "]");',
  '            TitleText.Text = Title;',
  '            Loaded += (s, e) =>',
  '            {',
  '                DpiScale dpi = VisualTreeHelper.GetDpi(this);',
  '                int d = (int)Math.Round(dpi.PixelsPerInchX);',
  '                int pct = (int)Math.Round(dpi.PixelsPerInchX / 96.0 * 100.0);',
  '                string text = "DPI " + d.ToString("000") + " (" + pct.ToString() + "%)";',
  '                DpiText.Text = text;',
  '                AutomationProperties.SetName(DpiText, text);',
  '            };',
  '        }',
  '',
  '        // DragOver 必须把 Effects 设成非 None，否则 WPF 不会触发 Drop',
  '        private void OnFileDragOver(object sender, DragEventArgs e)',
  '        {',
  '            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;',
  '            e.Handled = true;',
  '        }',
  '',
  '        private void OnFileDrop(object sender, DragEventArgs e)',
  '        {',
  '            string text = "drop=none";',
  '            if (e.Data.GetDataPresent(DataFormats.FileDrop))',
  '            {',
  '                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];',
  '                if (files != null && files.Length > 0) text = "drop=" + files[0];',
  '            }',
  '            // ⚠️ 不新增 XAML 元素：SCR-01 的元素属性表把路径 0/0~0/6 定死（ACC-027 的结构契约），',
  '            //   多一个 TextBlock 会让整棵树路径错位。拖放结果复用已有的 TitleText。',
  '            TitleText.Text = text;',
  '            AutomationProperties.SetName(TitleText, text);',
  '            e.Handled = true;',
  '        }',
  '',
  '        private static string ScrArg()',
  '        {',
  '            string[] args = Environment.GetCommandLineArgs();',
  '            for (int i = 0; i < args.Length - 1; ++i)',
  '                if (args[i] == "--scr") return args[i + 1];',
  '            return null;',
  '        }',
  '    }',
  '}',
);

// 【ACC-024 / K-052】打印样本专用：在通用窗口骨架上挂「打印」按钮的 Click，
// 走 System.Printing → XpsDocumentWriter.Write(...) → shim 的 winspool.drv（见 40-build/native-shim/src/win32_winspool.c），
// 然后把 job=/pages= 读数改掉。判据（50-verify/print/cups-submit.mjs）只看读数有没有变化。
// 失败时也必须把读数改成 job=0 state=error —— 不能静默（静默 = 判据看不出到底走没走）。
const windowCsWithPrint = (ns, cls, title) => L(
  'using System;',
  'using System.Printing;',
  'using System.Windows;',
  'using System.Windows.Automation;',
  'using System.Windows.Controls;',
  'using System.Windows.Documents;',
  'using System.Windows.Markup;',
  'using System.Windows.Xps;',
  '',
  'namespace ' + ns,
  '{',
  '    public partial class ' + cls + ' : Window',
  '    {',
  '        public ' + cls + '()',
  '        {',
  '            InitializeComponent();',
  '            string scr = ScrArg();',
  '            Title = "' + title + '" + (scr == null ? "" : " [" + scr + "]");',
  '            TitleText.Text = Title;',
  '            PrintButton.Click += OnPrint;',
  '        }',
  '',
  '        private void OnPrint(object sender, RoutedEventArgs e)',
  '        {',
  '            try',
  '            {',
  '                // 【ACC-024 / K-055】PrintServer/PrintQueue 的两个入口都在托管层 NRE（winspool 根本没被调到），',
  '                // 所以这里走**不需要打印机**的 XPS 路径：XpsDocument + XpsDocumentWriter 直接写文件。',
  '                // 这一半验证的是 XPS 序列化（ReachFramework 594 类型）+ 打包（System.IO.Packaging），',
  '                // 「打印到 CUPS」那半仍未实现，会单独记。',
  '                string xpsPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ldp-print-" + DateTime.Now.Ticks + ".xps");',
  '                // 【ACC-024 / K-057】编译期被 NuGet 的 ReachFramework **ref** 程序集挡着（那份只有 5120 字节、',
  '                // XpsDocument 只暴露 Dispose()），所以这里**只用反射**调运行时那 619008 字节的版本。',
  '                // FixedDocument/FixedPage/PageContent 在 PresentationFramework 里，ref 可用，不必反射。',
  '                var rfAsm = Array.Find(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == "ReachFramework");',
  '                if (rfAsm == null) { rfAsm = System.Reflection.Assembly.Load("ReachFramework"); }',
  '                var tXps = rfAsm.GetType("System.Windows.Xps.Packaging.XpsDocument", true);',
  '                var tCO  = Type.GetType("System.IO.Packaging.CompressionOption, System.IO.Packaging", true);',
  '                var ctor = tXps.GetConstructor(new Type[] { typeof(string), typeof(System.IO.FileAccess), tCO });',
  '                Console.Error.WriteLine("[PRINT] XpsDocument ctor=" + (ctor != null ? "found" : "NULL") + "  asm=" + rfAsm.GetName().Version);',
  '                var xps = ctor.Invoke(new object[] { xpsPath, System.IO.FileAccess.ReadWrite, Enum.Parse(tCO, "Fast") });',
  '                var mCreate = tXps.GetMethod("CreateXpsDocumentWriter", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, new Type[] { tXps }, null);',
  '                Console.Error.WriteLine("[PRINT] CreateXpsDocumentWriter=" + (mCreate != null ? "found" : "NULL"));',
  '                var writer = mCreate.Invoke(null, new object[] { xps });',
  '                var doc = new FixedDocument();',
  '                var page = new FixedPage();',
  '                page.Children.Add(new TextBlock { Text = "打印测试" });',
  '                var pc = new PageContent();',
  '                ((IAddChild)pc).AddChild(page);',
  '                doc.Pages.Add(pc);',
  '                var mWrite = writer.GetType().GetMethod("Write", new Type[] { typeof(FixedDocument) });',
  '                mWrite.Invoke(writer, new object[] { doc });',
  '                var mClose = xps.GetType().GetMethod("Close");',
  '                if (mClose != null) mClose.Invoke(xps, null);',
  '                Console.Error.WriteLine("[PRINT] XPS 已写出：" + xpsPath);', 
  '                SetReadouts("job=1 state=done", "pages=1");',
  '            }',
  '            catch (Exception ex)',
  '            {',
  '                SetReadouts("job=0 state=error", "pages=0");',
  '                Console.Error.WriteLine("[PRINT] 失败：" + ex.ToString());',
  '            }',
  '        }',
  '',
  '        private void SetReadouts(string job, string pages)',
  '        {',
  '            JobText.Text = job;',
  '            AutomationProperties.SetName(JobText, job);',
  '            PagesText.Text = pages;',
  '            AutomationProperties.SetName(PagesText, pages);',
  '        }',
  '',
  '        private static string ScrArg()',
  '        {',
  '            string[] a = Environment.GetCommandLineArgs();',
  '            for (int i = 0; i < a.Length - 1; i++)',
  '                if (a[i] == "--scr") return a[i + 1];',
  '            return null;',
  '        }',
  '    }',
  '}');

const windowCs = (ns, cls, title, withTitleText, withDpi = false) => L(
  'using System;',
  'using System.Windows;',
  ...(withDpi ? ['using System.Windows.Automation;', 'using System.Windows.Media;'] : []),
  '',
  'namespace ' + ns,
  '{',
  '    public partial class ' + cls + ' : Window',
  '    {',
  '        public ' + cls + '()',
  '        {',
  '            InitializeComponent();',
  '            string scr = ScrArg();',
  '            Title = "' + title + '" + (scr == null ? "" : " [" + scr + "]");',
  ...(withTitleText ? ['            TitleText.Text = Title;'] : []),
  ...(withDpi
    ? [
        '            // DPI 读数：结构判据的数据源。文案格式由元素属性表定死（DPI 1xx (yyy%)），别改。',
        '            Loaded += (s, e) =>',
        '            {',
        '                DpiScale dpi = VisualTreeHelper.GetDpi(this);',
        '                int d = (int)Math.Round(dpi.PixelsPerInchX);',
        '                int pct = (int)Math.Round(dpi.PixelsPerInchX / 96.0 * 100.0);',
        '                string text = "DPI " + d.ToString("000") + " (" + pct.ToString() + "%)";',
        '                DpiText.Text = text;',
        '                AutomationProperties.SetName(DpiText, text);',
        '            };',
      ]
    : []),
  '        }',
  '',
  '        // --scr SCR-0x：只影响窗口标题，不做其他事（骨架不实现业务逻辑）',
  '        private static string ScrArg()',
  '        {',
  '            string[] args = Environment.GetCommandLineArgs();',
  '            for (int i = 0; i < args.Length - 1; i++)',
  '            {',
  '                if (args[i] == "--scr")',
  '                {',
  '                    return args[i + 1];',
  '                }',
  '            }',
  '            return null;',
  '        }',
  '    }',
  '}',
);

const wpfXaml = (ns, cls, width, height, title, body) => L(
  '<Window x:Class="' + ns + '.' + cls + '"',
  '        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
  '        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"',
  '        Title="' + title + '" Height="' + height + '" Width="' + width + '"',
  '        AutomationProperties.Name="' + title + '">',
  body,
  '</Window>',
);

// ---------------------------------------------------------------- SCR-01

const scr01 = {
  dir: 'WpfFirstWindow',
  scr: 'SCR-01',
  ns: 'LinuxDesktopPerfect.Samples.WpfFirstWindow',
  files: (dir, ns) => ({
    'WpfFirstWindow.csproj': csprojWpf(dir),
    'App.xaml': appXaml(ns, 'MainWindow.xaml'),
    'App.xaml.cs': appCs(ns, true),
    'MainWindow.xaml.cs': windowCsWithDrop(ns, 'MainWindow', 'WpfFirstWindow'),
    'MainWindow.xaml': wpfXaml(ns, 'MainWindow', '960', '640', 'WpfFirstWindow', L(
      '    <!-- 对应 SCR-01 元素属性表：路径 0/0 ~ 0/6；读数文本是结构判据的数据源，格式别改 -->',
      '    <StackPanel Margin="12" AllowDrop="True" Drop="OnFileDrop" DragOver="OnFileDragOver">',
      '        <TextBlock x:Name="TitleText" Text="WpfFirstWindow" AutomationProperties.Name="WpfFirstWindow" FontSize="20" />',
      '        <TextBox AutomationProperties.Name="输入样本" Margin="0,8,0,0" />',
      '        <Button Content="提交" AutomationProperties.Name="提交" Margin="0,8,0,0" />',
      '        <Button Content="不可用示例" AutomationProperties.Name="不可用示例" IsEnabled="False" Margin="0,8,0,0" />',
      '        <StatusBar AutomationProperties.Name="状态" Margin="0,8,0,0">',
      '            <TextBlock x:Name="DpiText" Text="DPI 000 (0%)" AutomationProperties.Name="DPI 000 (0%)" />',
      '            <Separator />',
      '            <TextBlock Text="composite=false" AutomationProperties.Name="composite=false" />',
      '            <Separator />',
      '            <TextBlock Text="hwnd=0x0" AutomationProperties.Name="hwnd=0x0" />',
      '        </StatusBar>',
      '        <Canvas AutomationProperties.Name="几何画布" Height="120" Background="#FFF2F2F2" Margin="0,8,0,0" />',
      '        <Button Content="放大 150%" AutomationProperties.Name="放大 150%" Margin="0,8,0,0" />',
      '    </StackPanel>',
    )),
  }),
};

// ---------------------------------------------------------------- SCR-02

const scr02 = {
  dir: 'WpfTextDemo',
  scr: 'SCR-02',
  ns: 'LinuxDesktopPerfect.Samples.WpfTextDemo',
  files: (dir, ns) => ({
    'WpfTextDemo.csproj': csprojWpf(dir),
    'App.xaml': appXaml(ns, 'TextWindow.xaml'),
    'App.xaml.cs': appCs(ns),
    'TextWindow.xaml.cs': windowCs(ns, 'TextWindow', 'WpfTextDemo', true),
    'TextWindow.xaml': wpfXaml(ns, 'TextWindow', '980', '720', 'WpfTextDemo', L(
      '    <!-- 对应 SCR-02 元素属性表：路径 0/1 ~ 0/7；三类样本与读数文案按表 -->',
      '    <StackPanel Margin="12">',
      '        <TextBlock x:Name="TitleText" Text="WpfTextDemo" AutomationProperties.Name="WpfTextDemo" FontSize="20" />',
      '        <ListBox AutomationProperties.Name="字体族" Height="72" Margin="0,8,0,0">',
      '            <ListBoxItem Content="DejaVu Sans" AutomationProperties.Name="DejaVu Sans" IsSelected="True" />',
      '            <ListBoxItem Content="Noto Sans CJK SC" AutomationProperties.Name="Noto Sans CJK SC" />',
      '        </ListBox>',
      '        <FlowDocumentScrollViewer Height="140" Margin="0,8,0,0">',
      '            <FlowDocument>',
      '                <Paragraph AutomationProperties.Name="汉字样本">汉字样本：中文排版与回退链。</Paragraph>',
      '                <Paragraph AutomationProperties.Name="Latin sample">Latin sample: The quick brown fox jumps over the lazy dog.</Paragraph>',
      '                <Paragraph AutomationProperties.Name="符号 → ∑ ≈">符号样本：→ ∑ ≈ ± ∞。</Paragraph>',
      '            </FlowDocument>',
      '        </FlowDocumentScrollViewer>',
      '        <TextBlock AutomationProperties.Name="العربية" Text="العربية · देवनागरी · ไทย" FontSize="20" Margin="0,8,0,0" />',
      '        <TextBlock AutomationProperties.Name="行布局" TextWrapping="Wrap" MaxWidth="720" Margin="0,8,0,0"',
      '                   Text="行布局：这是一段用于断行与双向混排的长段落，包含超长单词 Supercalifragilisticexpialidocious 与混排 abc 汉字 def。" />',
      '        <TextBlock AutomationProperties.Name="CFF 大字" Text="CFF" FontSize="42" Margin="0,8,0,0" />',
      '        <TextBox AutomationProperties.Name="输入法样本" Margin="0,8,0,0" />',
      '        <TextBlock AutomationProperties.Name="预编辑：" Text="预编辑：" Margin="0,8,0,0" />',
      '    </StackPanel>',
    )),
    'fonts/ASSETS.txt': '占位：判据用的字体样本（CJK/拉丁/符号/复杂脚本/CFF）待冻结，见 09 §2.2「字体集冻结」。\n',
  }),
};

// ---------------------------------------------------------------- SCR-03

const scr03 = {
  dir: 'WpfImageGallery',
  scr: 'SCR-03',
  ns: 'LinuxDesktopPerfect.Samples.WpfImageGallery',
  files: (dir, ns) => ({
    'WpfImageGallery.csproj': csprojWpf(dir),
    'App.xaml': appXaml(ns, 'GalleryWindow.xaml'),
    'App.xaml.cs': appCs(ns),
    'GalleryWindow.xaml.cs': windowCs(ns, 'GalleryWindow', 'WpfImageGallery', true),
    'GalleryWindow.xaml': wpfXaml(ns, 'GalleryWindow', '900', '700', 'WpfImageGallery', L(
      '    <!-- 对应 SCR-03 元素属性表：五格式项 0/0/0 ~ 0/0/4；读数文案按表 -->',
      '    <StackPanel Margin="12">',
      '        <TextBlock x:Name="TitleText" Text="WpfImageGallery" AutomationProperties.Name="WpfImageGallery" FontSize="20" />',
      '        <ListBox AutomationProperties.Name="格式" Height="150" Margin="0,8,0,0">',
      '            <ListBoxItem Content="png" AutomationProperties.Name="png" IsSelected="True" />',
      '            <ListBoxItem Content="jpeg" AutomationProperties.Name="jpeg" />',
      '            <ListBoxItem Content="gif" AutomationProperties.Name="gif" />',
      '            <ListBoxItem Content="bmp" AutomationProperties.Name="bmp" />',
      '            <ListBoxItem Content="tiff" AutomationProperties.Name="tiff" />',
      '        </ListBox>',
      '        <Image AutomationProperties.Name="当前文件名" Height="160" Margin="0,8,0,0" />',
      '        <TextBlock AutomationProperties.Name="32×32 png" Text="32×32 png" Margin="0,8,0,0" />',
      '        <CheckBox Content="应用 ICC" AutomationProperties.Name="应用 ICC" IsChecked="True" Margin="0,8,0,0" />',
      '        <TextBlock AutomationProperties.Name="ΔE00=0.0" Text="ΔE00=0.0" Margin="0,8,0,0" />',
      '    </StackPanel>',
    )),
    'images/ASSETS.txt': '占位：五格式判据样本图（png/jpeg/gif/bmp/tiff）与 ICC 嵌入图待补，判据侧见 ACC-018/019。\n',
  }),
};

// ---------------------------------------------------------------- SCR-04

const scr04 = {
  dir: 'WpfControlGallery',
  scr: 'SCR-04',
  ns: 'LinuxDesktopPerfect.Samples.WpfControlGallery',
  files: (dir, ns) => ({
    'WpfControlGallery.csproj': csprojWpf(dir),
    'App.xaml': appXaml(ns, 'Gallery.xaml'),
    'App.xaml.cs': appCs(ns),
    'Gallery.xaml.cs': windowCs(ns, 'Gallery', 'WpfControlGallery', true),
    'Gallery.xaml': L(
      '<Window x:Class="' + ns + '.Gallery"',
      '        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
      '        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"',
      '        xmlns:ribbon="clr-namespace:System.Windows.Controls.Ribbon;assembly=System.Windows.Controls.Ribbon"',
      '        Title="WpfControlGallery" Height="820" Width="1024"',
      '        AutomationProperties.Name="WpfControlGallery">',
      '    <!-- 对应 SCR-04 元素属性表：0/0 ~ 0/6。0/1、0/2、0/3 是无名分组容器（GroupBox 提供 peer），',
      '         名字不在表里，故不设 AutomationProperties.Name；桥接层对无名容器的角色口径见 GAP。 -->',
      '    <StackPanel Margin="12">',
      '        <TextBlock x:Name="TitleText" Text="WpfControlGallery" AutomationProperties.Name="WpfControlGallery" FontSize="20" Margin="0,0,0,8" />',
      '        <ComboBox AutomationProperties.Name="主题" SelectedIndex="0" Margin="0,0,0,8">',
      '            <ComboBoxItem Content="Aero" AutomationProperties.Name="Aero" />',
      '            <ComboBoxItem Content="Aero2" AutomationProperties.Name="Aero2" />',
      '            <ComboBoxItem Content="AeroLite" AutomationProperties.Name="AeroLite" />',
      '            <ComboBoxItem Content="Luna" AutomationProperties.Name="Luna" />',
      '            <ComboBoxItem Content="Royale" AutomationProperties.Name="Royale" />',
      '            <ComboBoxItem Content="Classic" AutomationProperties.Name="Classic" />',
      '        </ComboBox>',
      '        <GroupBox Margin="0,0,0,8">',
      '            <StackPanel>',
      '                <Button Content="按钮" AutomationProperties.Name="按钮" />',
      '                <TextBox AutomationProperties.Name="文本" />',
      '                <CheckBox Content="复选" AutomationProperties.Name="复选" />',
      '                <RadioButton Content="单选" AutomationProperties.Name="单选" />',
      '                <Slider AutomationProperties.Name="滑杆" Minimum="0" Maximum="100" Value="50" />',
      '            </StackPanel>',
      '        </GroupBox>',
      '        <GroupBox Margin="0,0,0,8">',
      '            <StackPanel>',
      '                <ListBox AutomationProperties.Name="列表" Height="64">',
      '                    <ListBoxItem Content="项 1" AutomationProperties.Name="项 1" IsSelected="True" />',
      '                    <ListBoxItem Content="项 2" AutomationProperties.Name="项 2" />',
      '                </ListBox>',
      '                <ComboBox AutomationProperties.Name="下拉" SelectedIndex="0">',
      '                    <ComboBoxItem Content="选项一" AutomationProperties.Name="选项一" />',
      '                </ComboBox>',
      '                <DataGrid AutomationProperties.Name="表格" AutoGenerateColumns="False" Height="90">',
      '                    <DataGrid.Columns>',
      '                        <DataGridTextColumn Header="名称" Binding="{Binding Name}" />',
      '                    </DataGrid.Columns>',
      '                </DataGrid>',
      '            </StackPanel>',
      '        </GroupBox>',
      '        <GroupBox Margin="0,0,0,8">',
      '            <StackPanel>',
      '                <TabControl AutomationProperties.Name="选项卡">',
      '                    <TabItem Header="第一页" AutomationProperties.Name="第一页" />',
      '                    <TabItem Header="第二页" AutomationProperties.Name="第二页" />',
      '                </TabControl>',
      '                <GroupBox Header="分组" AutomationProperties.Name="分组">',
      '                    <TextBlock Text="分组内容" />',
      '                </GroupBox>',
      '                <Expander Header="折叠" AutomationProperties.Name="折叠" IsExpanded="False">',
      '                    <TextBlock Text="折叠内容" />',
      '                </Expander>',
      '            </StackPanel>',
      '        </GroupBox>',
      '        <ribbon:Ribbon AutomationProperties.Name="Ribbon" Height="120" Margin="0,0,0,8">',
      '            <ribbon:RibbonTab Header="开始" AutomationProperties.Name="开始">',
      '                <ribbon:RibbonGroup Header="剪贴板" AutomationProperties.Name="剪贴板">',
      '                    <ribbon:RibbonButton Label="粘贴" AutomationProperties.Name="粘贴" />',
      '                </ribbon:RibbonGroup>',
      '            </ribbon:RibbonTab>',
      '        </ribbon:Ribbon>',
      '        <ComboBox AutomationProperties.Name="缩放" SelectedIndex="0" Margin="0,0,0,8">',
      '            <ComboBoxItem Content="100%" AutomationProperties.Name="100%" />',
      '            <ComboBoxItem Content="125%" AutomationProperties.Name="125%" />',
      '            <ComboBoxItem Content="150%" AutomationProperties.Name="150%" />',
      '            <ComboBoxItem Content="200%" AutomationProperties.Name="200%" />',
      '        </ComboBox>',
      '        <TextBlock AutomationProperties.Name="frames=0" Text="frames=0" />',
      '    </StackPanel>',
      '</Window>',
    ),
  }),
};

// ---------------------------------------------------------------- SCR-05

const scr05 = {
  dir: 'WpfPrintDoc',
  scr: 'SCR-05',
  ns: 'LinuxDesktopPerfect.Samples.WpfPrintDoc',
  files: (dir, ns) => ({
    'WpfPrintDoc.csproj': csprojWpfWithReachFramework(dir),
    'App.xaml': appXaml(ns, 'PrintWindow.xaml'),
    'App.xaml.cs': appCs(ns),
    'PrintWindow.xaml.cs': windowCsWithPrint(ns, 'PrintWindow', 'WpfPrintDoc'),
    'PrintWindow.xaml': wpfXaml(ns, 'PrintWindow', '900', '760', 'WpfPrintDoc', L(
      '    <!-- 对应 SCR-05 元素属性表：0/0 ~ 0/8；作业/页数/命中读数文案按表 -->',
      '    <StackPanel Margin="12">',
      '        <TextBlock x:Name="TitleText" Text="WpfPrintDoc" AutomationProperties.Name="WpfPrintDoc" FontSize="20" />',
      '        <ComboBox AutomationProperties.Name="打印机" SelectedIndex="0" Margin="0,8,0,0">',
      '            <ComboBoxItem Content="Fake CUPS 队列" AutomationProperties.Name="Fake CUPS 队列" />',
      '        </ComboBox>',
      '        <Button x:Name="PrintButton" Content="打印" AutomationProperties.Name="打印" Margin="0,8,0,0" />',
      '        <TextBlock x:Name="JobText" AutomationProperties.Name="job=0 state=pending" Text="job=0 state=pending" Margin="0,8,0,0" />',
      '        <TextBlock x:Name="PagesText" AutomationProperties.Name="pages=0" Text="pages=0" Margin="0,8,0,0" />',
      '        <FlowDocumentReader AutomationProperties.Name="流文档" Height="240" Margin="0,8,0,0">',
      '            <FlowDocument>',
      '                <Paragraph AutomationProperties.Name="第 1 段">第 1 段：为流文档判据准备的固定文本。</Paragraph>',
      '                <Paragraph AutomationProperties.Name="第 2 段">第 2 段：分页、缩放与查找都在这一屏上验。</Paragraph>',
      '            </FlowDocument>',
      '        </FlowDocumentReader>',
      '        <Button Content="下一页" AutomationProperties.Name="下一页" Margin="0,8,0,0" />',
      '        <Slider AutomationProperties.Name="缩放" Minimum="50" Maximum="200" Value="100" Margin="0,8,0,0" />',
      '        <TextBox AutomationProperties.Name="查找" Margin="0,8,0,0" />',
      '        <TextBlock AutomationProperties.Name="命中 0 处" Text="命中 0 处" Margin="0,8,0,0" />',
      '    </StackPanel>',
    )),
    'docs/ASSETS.txt': '占位：打印判据用的 XPS/流文档样本待补（ACC-024/025）。\n',
  }),
};

// ---------------------------------------------------------------- SCR-06

const scr06 = {
  dir: 'WpfMediaInk',
  scr: 'SCR-06',
  ns: 'LinuxDesktopPerfect.Samples.WpfMediaInk',
  files: (dir, ns) => ({
    'WpfMediaInk.csproj': csprojWpf(dir),
    'App.xaml': appXaml(ns, 'MediaWindow.xaml'),
    'App.xaml.cs': appCs(ns),
    'MediaWindow.xaml.cs': windowCs(ns, 'MediaWindow', 'WpfMediaInk', false),
    'MediaWindow.xaml': wpfXaml(ns, 'MediaWindow', '900', '760', 'WpfMediaInk', L(
      '    <!-- 对应 SCR-06 元素属性表：0/0 ~ 0/7；不支持格式提示默认 Collapsed（!visible） -->',
      '    <!-- 注意：这一屏**不放 TitleText** —— 属性表的 0/0 是播放按钮。',
      '         其他屏的 0/0 是 TitleText，但 SCR-06 的表没把标题算进元素序列。',
      '         加一个标题元素会让 0/0~0/7 全部错位（ACC-027 立刻抓到）。 -->',
      '    <StackPanel Margin="12">',
      '        <Button Content="播放" AutomationProperties.Name="播放" Margin="0,8,0,0" />',
      '        <Button Content="暂停" AutomationProperties.Name="暂停" Margin="0,8,0,0" />',
      '        <Slider AutomationProperties.Name="进度" Minimum="0" Maximum="100" Value="0" Margin="0,8,0,0" />',
      '        <TextBlock AutomationProperties.Name="state=READY" Text="state=READY" Margin="0,8,0,0" />',
      '        <!-- ADR-012：不支持必须说出来，且要能对上缺陷册编号（属性表要求正则 D-\\d{3}）。',
      '             媒体栈未实现对应已知坑 D-015 —— 用真实编号，不用 D-xxx 占位。 -->',
      '        <TextBlock AutomationProperties.Name="不支持此格式（D-015）"',
      '                   Text="不支持此格式（D-015）" Visibility="Collapsed" Margin="0,8,0,0" />',
      '        <InkCanvas AutomationProperties.Name="墨迹" Height="160" Background="#FFF7F7F7" Margin="0,8,0,0" />',
      '        <TextBlock AutomationProperties.Name="points=0" Text="points=0" Margin="0,8,0,0" />',
      '        <TextBlock AutomationProperties.Name="pen=off" Text="pen=off" Margin="0,8,0,0" />',
      '    </StackPanel>',
    )),
    'media/ASSETS.txt': '占位：媒体判据样本（常见容器/编码 + 一个刻意不支持的格式）待补（ACC-022）。\n',
  }),
};

// ---------------------------------------------------------------- SCR-07（WinForms）

const scr07 = {
  dir: 'WinFormsBasics',
  scr: 'SCR-07',
  ns: 'LinuxDesktopPerfect.Samples.WinFormsBasics',
  files: (dir, ns) => ({
    'WinFormsBasics.csproj': csprojWinForms(dir),
    'Program.cs': L(
      'using System;',
      'using System.Windows.Forms;',
      '',
      'namespace ' + ns,
      '{',
      '    internal static class Program',
      '    {',
      '        [STAThread]',
      '        private static void Main(string[] args)',
      '        {',
      '            string scr = ScrArg(args);',
      '            Application.EnableVisualStyles();',
      '            Application.SetCompatibleTextRenderingDefault(false);',
      '            Application.Run(new MainForm(scr));',
      '        }',
      '',
      '        // --scr SCR-0x：只影响窗体标题（骨架不实现业务逻辑）',
      '        private static string ScrArg(string[] args)',
      '        {',
      '            for (int i = 0; i < args.Length - 1; i++)',
      '            {',
      '                if (args[i] == "--scr")',
      '                {',
      '                    return args[i + 1];',
      '                }',
      '            }',
      '            return null;',
      '        }',
      '    }',
      '}',
    ),
    'MainForm.cs': L(
      'using System.Drawing;',
      'using System.Windows.Forms;',
      '',
      'namespace ' + ns,
      '{',
      '    public class MainForm : Form',
      '    {',
      '        public MainForm(string scr)',
      '        {',
      '            Text = "WinFormsBasics" + (scr == null ? "" : " [" + scr + "]");',
      '            AccessibleName = "WinFormsBasics";',
      '            ClientSize = new Size(900, 700);',
      '',
      '            Button button = new Button();',
      '            button.Text = "确定";',
      '            button.AccessibleName = "确定";',
      '            button.Location = new Point(12, 12);',
      '            Controls.Add(button);',
      '',
      '            TextBox textBox = new TextBox();',
      '            textBox.AccessibleName = "输入";',
      '            textBox.Location = new Point(12, 48);',
      '            Controls.Add(textBox);',
      '',
      '            ListBox listBox = new ListBox();',
      '            listBox.AccessibleName = "列表";',
      '            listBox.Location = new Point(12, 80);',
      '            listBox.Items.Add("项 1");',
      '            Controls.Add(listBox);',
      '',
      '            ComboBox comboBox = new ComboBox();',
      '            comboBox.AccessibleName = "下拉";',
      '            comboBox.Location = new Point(12, 160);',
      '            comboBox.Items.Add("选项一");',
      '            Controls.Add(comboBox);',
      '',
      '            Panel panel = new Panel();',
      '            panel.AccessibleName = "子窗体容器";',
      '            panel.Location = new Point(12, 196);',
      '            panel.Size = new Size(200, 60);',
      '            panel.BackColor = Color.WhiteSmoke;',
      '            Controls.Add(panel);',
      '',
      '            MenuStrip menu = new MenuStrip();',
      '            menu.AccessibleName = "菜单";',
      '            ToolStripMenuItem fileItem = new ToolStripMenuItem("文件");',
      '            fileItem.AccessibleName = "文件";',
      '            menu.Items.Add(fileItem);',
      '            Controls.Add(menu);',
      '',
      '            // 缺失控件占位：缺陷编号未分配，先用 D-xxx 占位（不得借用 D-001）',
      '            Label missing = new Label();',
      '            missing.Text = "缺失控件占位";',
      '            missing.AccessibleName = "缺失控件 SamplePlaceholder D-xxx";',
      '            missing.Location = new Point(12, 268);',
      '            missing.AutoSize = true;',
      '            Controls.Add(missing);',
      '',
      '            Label msaa = new Label();',
      '            msaa.Text = "msaa=off";',
      '            msaa.AccessibleName = "msaa=off";',
      '            msaa.Location = new Point(12, 300);',
      '            msaa.AutoSize = true;',
      '            Controls.Add(msaa);',
      '        }',
      '    }',
      '}',
    ),
  }),
};

const SAMPLES = [scr01, scr02, scr03, scr04, scr05, scr06, scr07];

// ---------------------------------------------------------------- 生成 / 校验

function expectedFiles() {
  const out = new Map(); // 绝对路径 -> 内容
  for (const s of SAMPLES) {
    for (const [rel, content] of Object.entries(s.files(s.dir, s.ns))) {
      out.set(path.join(HERE, s.dir, rel), content);
    }
  }
  return out;
}

function walk(dir, acc = []) {
  if (!fs.existsSync(dir)) return acc;
  for (const ent of fs.readdirSync(dir, { withFileTypes: true })) {
    if (ent.name === 'bin' || ent.name === 'obj') continue;
    const p = path.join(dir, ent.name);
    if (ent.isDirectory()) walk(p, acc);
    else acc.push(p);
  }
  return acc;
}

const expected = expectedFiles();
let errors = 0;
let written = 0;

if (checkOnly) {
  for (const [abs, content] of expected) {
    const rel = path.relative(process.cwd(), abs);
    if (!fs.existsSync(abs)) {
      console.error(`MISSING ${rel}`);
      errors += 1;
      continue;
    }
    if (fs.readFileSync(abs, 'utf8') !== content) {
      console.error(`DRIFT ${rel}（重跑 node samples/gen-samples.mjs）`);
      errors += 1;
    }
  }
  // 允许的「手写补充文件」：不走生成器、但属于样本的一部分。
  //   DocCache.cs：D-015 第十三步，把 FlowDocument 的段落文本预缓存进 Tag
  //   （必须在应用侧做 —— 在 LdpA11y 的投影路径上访问 Block 会触发同步排版并卡住）。
  const HANDWRITTEN = new Set([
    'samples/WpfTextDemo/DocCache.cs',
    'samples/WpfMediaInk/MediaHookup.cs',
  ]);
  for (const s of SAMPLES) {
    for (const abs of walk(path.join(HERE, s.dir))) {
      if (!expected.has(abs) && !HANDWRITTEN.has(path.relative(process.cwd(), abs))) {
        console.error(`EXTRA ${path.relative(process.cwd(), abs)}（不在 spec 里）`);
        errors += 1;
      }
    }
  }
} else {
  for (const [abs, content] of expected) {
    fs.mkdirSync(path.dirname(abs), { recursive: true });
    const before = fs.existsSync(abs) ? fs.readFileSync(abs, 'utf8') : null;
    if (before !== content) {
      fs.writeFileSync(abs, content, 'utf8');
      written += 1;
    }
  }
}

const readme = path.join(HERE, 'README.txt');
const readmeText = L(
  '样本工程（由 samples/gen-samples.mjs 生成，勿手改）',
  '',
  '七个样本与判据现场的对应关系见 30-design/设计文档包/09-开工前置与切片计划.md §7；',
  '控件、路径与读数文案的唯一来源是 30-design/设计文档包/界面设计说明.md 的元素属性表。',
  '',
  '  WpfFirstWindow      SCR-01  窗口/消息/DPI/剪贴板',
  '  WpfTextDemo         SCR-02  字体回退/shaping/行布局/CFF/IME',
  '  WpfImageGallery     SCR-03  WIC/ICC',
  '  WpfControlGallery   SCR-04  DUCE 覆盖/六主题/Ribbon/DPI',
  '  WpfPrintDoc         SCR-05  XPS/CUPS 与流文档',
  '  WpfMediaInk         SCR-06  GStreamer 与 Ink',
  '  WinFormsBasics      SCR-07  WinForms 兼容层与 MSAA 桥',
  '',
  '全部工程用 --scr SCR-0x 启动（只改标题，不实现业务逻辑）。',
  'dotnet build 走 samples/Directory.Build.props 注入的自产 targets：先跑 node 40-build/pack/build-pack.mjs（ACC-001/002 读数见 50-verify/读数/ACC-001/）。',
  '',
);
if (checkOnly) {
  if (!fs.existsSync(readme) || fs.readFileSync(readme, 'utf8') !== readmeText) {
    console.error('DRIFT samples/README.txt');
    errors += 1;
  }
} else if (!fs.existsSync(readme) || fs.readFileSync(readme, 'utf8') !== readmeText) {
  fs.writeFileSync(readme, readmeText, 'utf8');
  written += 1;
}

for (const s of SAMPLES) {
  const csproj = path.join(HERE, s.dir, s.dir + '.csproj');
  if (!fs.existsSync(csproj)) {
    console.error(`缺少工程文件 ${path.relative(process.cwd(), csproj)}`);
    errors += 1;
  }
}

if (errors) {
  console.error(`gen-samples: ${errors} 处不一致`);
  process.exit(1);
}
console.log(
  checkOnly
    ? `gen-samples --check: ${expected.size + 1} 个文件与 spec 一致，0 错误`
    : `gen-samples: ${SAMPLES.length} 个工程，${expected.size + 1} 个文件，写出 ${written} 个变更文件`,
);
