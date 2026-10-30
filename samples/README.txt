样本工程（由 samples/gen-samples.mjs 生成，勿手改）

七个样本与判据现场的对应关系见 30-design/设计文档包/09-开工前置与切片计划.md §7；
控件、路径与读数文案的唯一来源是 30-design/设计文档包/界面设计说明.md 的元素属性表。

  WpfFirstWindow      SCR-01  窗口/消息/DPI/剪贴板
  WpfTextDemo         SCR-02  字体回退/shaping/行布局/CFF/IME
  WpfImageGallery     SCR-03  WIC/ICC
  WpfControlGallery   SCR-04  DUCE 覆盖/六主题/Ribbon/DPI
  WpfPrintDoc         SCR-05  XPS/CUPS 与流文档
  WpfMediaInk         SCR-06  GStreamer 与 Ink
  WinFormsBasics      SCR-07  WinForms 兼容层与 MSAA 桥

全部工程用 --scr SCR-0x 启动（只改标题，不实现业务逻辑）。
dotnet build 走 samples/Directory.Build.props 注入的自产 targets：先跑 node 40-build/pack/build-pack.mjs（ACC-001/002 读数见 50-verify/读数/ACC-001/）。

