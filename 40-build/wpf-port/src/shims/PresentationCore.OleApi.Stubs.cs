// PresentationCore · Linux OLE 公开 API 占位（M4，主控裁决：最小诚实 stub）
// =====================================================================================
// 为什么有这个文件
// ----------------
// 上游的 OLE 剪贴板 / DragDrop 栈（8 个文件，1880 行）深度依赖 WinForms 私有包
// `System.Private.Windows.Core`（`System.Private.Windows.Ole` 的泛型宿主）。
// 实测两条硬事实（见 src/Linux/build/excludes/PresentationCore.txt B 类、docs/U2-PresentationCore-prep.md §2.2）：
//   1) 该包在可用 NuGet 镜像上 **NU1101 不存在**，离线无法补齐；
//   2) 其泛型宿主（Composition<,,> / ClipboardCore<T> / …）的形态只存在于另一个仓库，
//      凭调用点手写 = SplashScreen 式「能编译但语义错」。
// 故这 8 个文件被剔除；剔除的代价是 4 个公开类型 + 3 个内部类型被挖掉，表现为 12 条
// 编译错误（GlobalUsings 的 4 个 global alias 失效 + DragDrop.cs 用 DataObject）。
//
// 本文件的定位（**必须如实理解**）
// ------------------------------
//   * 它是**编译期占位**，让 PresentationCore 的公开 API 面保持完整、让上述 12 条错误归零；
//   * 运行期一律抛 `PlatformNotSupportedException`（消息固定指向 X11 selection/Xdnd 重写），
//     **绝不返回空集合/默认值假装成功** —— 那是"能编译但行为错"，本项目明确拒绝
//     （SplashScreen 教训，src/Linux/build/excludes/WindowsBase.txt）；
//   * 唯一「真实现」的部分是纯数据结构：`DataFormat.Name/Id`、`DataFormats` 的格式名字符串。
//     其中格式名字面量来自 WinForms `DataFormatNames`（**另一个仓库**），离线无 oracle，
//     故在此显式标注「值未离线核实」—— 它们目前不产生任何行为（所有使用者都会抛异常）。
//
// 恢复条件（二选一，路线级决定）：
//   ① 路线乙（推荐）：按 X11 selection / Xdnd 语义重写这四个公开类型，取代本文件；
//   ② 路线甲：联网取得 System.Private.Windows.Core 等价面或 CsWin32 真实生成，
//      重建 OLE 泛型宿主后恢复那 8 个文件。
// 关联条目：handoff U13（剪贴板与拖放）。
using System;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Media.Imaging;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace System.Windows
{
    /// <summary>
    /// [真实现] 数据格式（名 + 数字 id）—— 纯数据，无平台依赖。
    /// 上游实现在被剔除的 System/Windows/DataFormat.cs；公开契约见 ref/PresentationCore.cs。
    /// </summary>
    public sealed class DataFormat
    {
        private readonly string _name;
        private readonly int _id;

        public DataFormat(string name, int id)
        {
            _name = name ?? throw new ArgumentNullException(nameof(name));
            _id = id;
        }

        public string Name => _name;
        public int Id => _id;

        public override string ToString() => _name;
    }

    /// <summary>
    /// OLE 剪贴板/DnD 未实现的统一出口。
    /// 消息固定，便于用户与日志一眼定位到替代路线。
    /// </summary>
    internal static class OleNotSupported
    {
        internal const string Message =
            "Linux 侧剪贴板/DnD 需 X11 selection/Xdnd 实现，见 handoff U13" +
            "（占位类型由 src/Microsoft.DotNet.Wpf.Linux/src/shims/PresentationCore.OleApi.Stubs.cs 提供，运行期不可用）";

        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        internal static Exception Throw(string member) =>
            throw new PlatformNotSupportedException(member + "：" + Message);

        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        internal static T Throw<T>(string member) =>
            throw new PlatformNotSupportedException(member + "：" + Message);
    }

    /// <summary>
    /// [部分真实现] 预定义剪贴板格式名。
    /// 23 个 <c>public static readonly string</c> 是 upstream 的公开契约
    /// （ref/PresentationCore.cs 逐项列出）—— 见本文件头部关于**值未离线核实**的说明。
    /// 两个 <c>GetDataFormat</c> 依赖 OLE 格式注册表（id 分配），故抛异常。
    /// </summary>
    public static class DataFormats
    {
        // 值取自 WinForms DataFormatNames（System.Private.Windows.Core，离线不可得）：
        // 下列字面量是 Windows 剪贴板格式的标准名，但**未在本环境核实**（标注诚实性）。
        public static readonly string Text = "Text";
        public static readonly string UnicodeText = "UnicodeText";
        public static readonly string Dib = "DeviceIndependentBitmap";
        public static readonly string Bitmap = "Bitmap";
        public static readonly string EnhancedMetafile = "EnhancedMetafile";
        public static readonly string MetafilePicture = "MetaFilePict";
        public static readonly string SymbolicLink = "SymbolicLink";
        public static readonly string Dif = "DataInterchangeFormat";
        public static readonly string Tiff = "TaggedImageFileFormat";
        public static readonly string OemText = "OEMText";
        public static readonly string Palette = "Palette";
        public static readonly string PenData = "PenData";
        public static readonly string Riff = "RiffAudio";
        public static readonly string WaveAudio = "WaveAudio";
        public static readonly string FileDrop = "FileDrop";
        public static readonly string Locale = "Locale";
        public static readonly string Html = "HTML Format";
        public static readonly string Rtf = "Rich Text Format";
        public static readonly string CommaSeparatedValue = "Csv";
        public static readonly string StringFormat = "System.String";
        public static readonly string Serializable = "WindowsForms10PersistentObject";
        public static readonly string Xaml = "Xaml";
        public static readonly string XamlPackage = "XamlPackage";

        /// <summary>[PNSE] 依赖 OLE 格式注册表的 id 分配。</summary>
        public static DataFormat GetDataFormat(int id) => OleNotSupported.Throw<DataFormat>(nameof(GetDataFormat));

        /// <summary>[PNSE] 依赖 OLE 格式注册表的 id 分配。</summary>
        public static DataFormat GetDataFormat(string format) => OleNotSupported.Throw<DataFormat>(nameof(GetDataFormat));
    }

    /// <summary>
    /// [PNSE] 数据对象（剪贴板/拖放的数据载体）。
    /// 公开契约照 ref/PresentationCore.cs；三个事件字段是 <c>RoutedEvent</c>，用 <c>null</c> 占位
    /// —— 注意：**读它们会得到 null，这属于占位语义**，但任何真实数据操作都会抛异常，
    /// 不会出现"静默成功"。
    /// </summary>
    public sealed class DataObject :
        ComTypes.IDataObject,
        IDataObject,
        ITypedDataObject
    {
        public static readonly RoutedEvent CopyingEvent;
        public static readonly RoutedEvent PastingEvent;
        public static readonly RoutedEvent SettingDataEvent;

        public DataObject() => OleNotSupported.Throw(nameof(DataObject));
        public DataObject(object data) => OleNotSupported.Throw(nameof(DataObject));
        public DataObject(string format, object data) => OleNotSupported.Throw(nameof(DataObject));
        public DataObject(string format, object data, bool autoConvert) => OleNotSupported.Throw(nameof(DataObject));
        public DataObject(Type format, object data) => OleNotSupported.Throw(nameof(DataObject));

        /// <summary>DragDrop.cs 内部构造用（上游从 COM IDataObject 包装）。</summary>
        internal DataObject(ComTypes.IDataObject data) => OleNotSupported.Throw(nameof(DataObject));

        // ---- System.Windows.IDataObject ----
        public object GetData(string format) => OleNotSupported.Throw<object>(nameof(GetData));
        public object GetData(string format, bool autoConvert) => OleNotSupported.Throw<object>(nameof(GetData));
        public object GetData(Type format) => OleNotSupported.Throw<object>(nameof(GetData));
        public bool GetDataPresent(string format) => OleNotSupported.Throw<bool>(nameof(GetDataPresent));
        public bool GetDataPresent(string format, bool autoConvert) => OleNotSupported.Throw<bool>(nameof(GetDataPresent));
        public bool GetDataPresent(Type format) => OleNotSupported.Throw<bool>(nameof(GetDataPresent));
        public string[] GetFormats() => OleNotSupported.Throw<string[]>(nameof(GetFormats));
        public string[] GetFormats(bool autoConvert) => OleNotSupported.Throw<string[]>(nameof(GetFormats));
        public void SetData(object data) => OleNotSupported.Throw(nameof(SetData));
        public void SetData(string format, object data) => OleNotSupported.Throw(nameof(SetData));
        public void SetData(string format, object data, bool autoConvert) => OleNotSupported.Throw(nameof(SetData));
        public void SetData(Type format, object data) => OleNotSupported.Throw(nameof(SetData));

        // ---- System.Windows.ITypedDataObject ----
        public bool TryGetData<T>(out T data) { data = default; OleNotSupported.Throw(nameof(TryGetData)); return false; }
        public bool TryGetData<T>(string format, out T data) { data = default; OleNotSupported.Throw(nameof(TryGetData)); return false; }
        public bool TryGetData<T>(string format, bool autoConvert, out T data) { data = default; OleNotSupported.Throw(nameof(TryGetData)); return false; }
        public bool TryGetData<T>(string format, Func<Reflection.Metadata.TypeName, Type> resolver, bool autoConvert, out T data)
        { data = default; OleNotSupported.Throw(nameof(TryGetData)); return false; }

        // ---- 富数据便捷 API（上游公开面）----
        public bool ContainsAudio() => OleNotSupported.Throw<bool>(nameof(ContainsAudio));
        public bool ContainsFileDropList() => OleNotSupported.Throw<bool>(nameof(ContainsFileDropList));
        public bool ContainsImage() => OleNotSupported.Throw<bool>(nameof(ContainsImage));
        public bool ContainsText() => OleNotSupported.Throw<bool>(nameof(ContainsText));
        public bool ContainsText(TextDataFormat format) => OleNotSupported.Throw<bool>(nameof(ContainsText));
        public Stream GetAudioStream() => OleNotSupported.Throw<Stream>(nameof(GetAudioStream));
        public StringCollection GetFileDropList() => OleNotSupported.Throw<StringCollection>(nameof(GetFileDropList));
        public BitmapSource GetImage() => OleNotSupported.Throw<BitmapSource>(nameof(GetImage));
        public string GetText() => OleNotSupported.Throw<string>(nameof(GetText));
        public string GetText(TextDataFormat format) => OleNotSupported.Throw<string>(nameof(GetText));
        public void SetAudio(byte[] audioBytes) => OleNotSupported.Throw(nameof(SetAudio));
        public void SetAudio(Stream audioStream) => OleNotSupported.Throw(nameof(SetAudio));
        public void SetFileDropList(StringCollection fileDropList) => OleNotSupported.Throw(nameof(SetFileDropList));
        public void SetImage(BitmapSource image) => OleNotSupported.Throw(nameof(SetImage));
        public void SetText(string textData) => OleNotSupported.Throw(nameof(SetText));
        public void SetText(string textData, TextDataFormat format) => OleNotSupported.Throw(nameof(SetText));
        public void SetDataAsJson<T>(T data) => OleNotSupported.Throw(nameof(SetDataAsJson));
        public void SetDataAsJson<T>(string format, T data) => OleNotSupported.Throw(nameof(SetDataAsJson));

        // ---- 事件挂接（上游公开面；依赖 RoutedEvent，占位实现直接抛）----
        public static void AddCopyingHandler(DependencyObject element, DataObjectCopyingEventHandler handler) => OleNotSupported.Throw(nameof(AddCopyingHandler));
        public static void AddPastingHandler(DependencyObject element, DataObjectPastingEventHandler handler) => OleNotSupported.Throw(nameof(AddPastingHandler));
        public static void AddSettingDataHandler(DependencyObject element, DataObjectSettingDataEventHandler handler) => OleNotSupported.Throw(nameof(AddSettingDataHandler));
        public static void RemoveCopyingHandler(DependencyObject element, DataObjectCopyingEventHandler handler) => OleNotSupported.Throw(nameof(RemoveCopyingHandler));
        public static void RemovePastingHandler(DependencyObject element, DataObjectPastingEventHandler handler) => OleNotSupported.Throw(nameof(RemovePastingHandler));
        public static void RemoveSettingDataHandler(DependencyObject element, DataObjectSettingDataEventHandler handler) => OleNotSupported.Throw(nameof(RemoveSettingDataHandler));

        // ---- System.Runtime.InteropServices.ComTypes.IDataObject（Windows COM 互操作面）----
        // DragDrop.cs 会把 DataObject 转型成 IComDataObject 传给原生层，故必须实现该接口。
        int ComTypes.IDataObject.DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink pAdvSink, out int pdwConnection)
        { pdwConnection = 0; OleNotSupported.Throw("IDataObject.DAdvise"); return 0; }
        void ComTypes.IDataObject.DUnadvise(int dwConnection) => OleNotSupported.Throw("IDataObject.DUnadvise");
        int ComTypes.IDataObject.EnumDAdvise(out IEnumSTATDATA enumAdvise)
        { enumAdvise = null; OleNotSupported.Throw("IDataObject.EnumDAdvise"); return 0; }
        IEnumFORMATETC ComTypes.IDataObject.EnumFormatEtc(DATADIR dwDirection) => OleNotSupported.Throw<IEnumFORMATETC>("IDataObject.EnumFormatEtc");
        int ComTypes.IDataObject.GetCanonicalFormatEtc(ref FORMATETC pformatetcIn, out FORMATETC pformatetcOut)
        { pformatetcOut = default; OleNotSupported.Throw("IDataObject.GetCanonicalFormatEtc"); return 0; }
        void ComTypes.IDataObject.GetData(ref FORMATETC formatetc, out STGMEDIUM medium)
        { medium = default; OleNotSupported.Throw("IDataObject.GetData"); }
        void ComTypes.IDataObject.GetDataHere(ref FORMATETC formatetc, ref STGMEDIUM medium) => OleNotSupported.Throw("IDataObject.GetDataHere");
        int ComTypes.IDataObject.QueryGetData(ref FORMATETC formatetc) => OleNotSupported.Throw<int>("IDataObject.QueryGetData");
        void ComTypes.IDataObject.SetData(ref FORMATETC pFormatetcIn, ref STGMEDIUM pmedium, bool fRelease) => OleNotSupported.Throw("IDataObject.SetData");
    }

    /// <summary>
    /// [PNSE] 剪贴板静态 API。公开契约照 ref/PresentationCore.cs（26 个成员）；
    /// Linux 侧将由 X11 selection 实现取代。
    /// </summary>
    public static class Clipboard
    {
        public static void Clear() => OleNotSupported.Throw(nameof(Clear));
        public static void Flush() => OleNotSupported.Throw(nameof(Flush));
        public static bool ContainsAudio() => OleNotSupported.Throw<bool>(nameof(ContainsAudio));
        public static bool ContainsData(string format) => OleNotSupported.Throw<bool>(nameof(ContainsData));
        public static bool ContainsFileDropList() => OleNotSupported.Throw<bool>(nameof(ContainsFileDropList));
        public static bool ContainsImage() => OleNotSupported.Throw<bool>(nameof(ContainsImage));
        public static bool ContainsText() => OleNotSupported.Throw<bool>(nameof(ContainsText));
        public static bool ContainsText(TextDataFormat format) => OleNotSupported.Throw<bool>(nameof(ContainsText));
        public static Stream GetAudioStream() => OleNotSupported.Throw<Stream>(nameof(GetAudioStream));
        public static object GetData(string format) => OleNotSupported.Throw<object>(nameof(GetData));
        public static IDataObject GetDataObject() => OleNotSupported.Throw<IDataObject>(nameof(GetDataObject));
        public static StringCollection GetFileDropList() => OleNotSupported.Throw<StringCollection>(nameof(GetFileDropList));
        public static BitmapSource GetImage() => OleNotSupported.Throw<BitmapSource>(nameof(GetImage));
        public static string GetText() => OleNotSupported.Throw<string>(nameof(GetText));
        public static string GetText(TextDataFormat format) => OleNotSupported.Throw<string>(nameof(GetText));
        public static bool IsCurrent(IDataObject data) => OleNotSupported.Throw<bool>(nameof(IsCurrent));
        public static void SetAudio(byte[] audioBytes) => OleNotSupported.Throw(nameof(SetAudio));
        public static void SetAudio(Stream audioStream) => OleNotSupported.Throw(nameof(SetAudio));
        public static void SetData(string format, object data) => OleNotSupported.Throw(nameof(SetData));
        public static void SetDataObject(object data) => OleNotSupported.Throw(nameof(SetDataObject));
        public static void SetDataObject(object data, bool copy) => OleNotSupported.Throw(nameof(SetDataObject));
        public static void SetFileDropList(StringCollection fileDropList) => OleNotSupported.Throw(nameof(SetFileDropList));
        public static void SetImage(BitmapSource image) => OleNotSupported.Throw(nameof(SetImage));
        public static void SetText(string text) => OleNotSupported.Throw(nameof(SetText));
        public static void SetText(string text, TextDataFormat format) => OleNotSupported.Throw(nameof(SetText));
        public static void SetDataAsJson<T>(string format, T data) => OleNotSupported.Throw(nameof(SetDataAsJson));
        public static bool TryGetData<T>(string format, out T data) { data = default; OleNotSupported.Throw(nameof(TryGetData)); return false; }
        public static bool TryGetData<T>(string format, Func<Reflection.Metadata.TypeName, Type> resolver, out T data)
        { data = default; OleNotSupported.Throw(nameof(TryGetData)); return false; }
    }

    /// <summary>
    /// [PNSE] 依赖 WinForms 私有包 System.Private.Windows.Ole 的宿主类型群。
    /// 这些类型由被剔除的 8 个文件使用（GlobalUsings.cs 的 4 个 global alias 直接指向
    /// Composition&lt;,,&gt; / ClipboardCore&lt;T&gt; / DragDropHelper&lt;,&gt; / DataFormatsCore&lt;T&gt;），
    /// 因此必须存在才能让别名的声明成立；这里只提供**元数（arity）**与空壳，不伪造成员。
    /// </summary>
}

namespace System.Private.Windows.Ole
{
    /// <summary>[空壳] WinForms 私有包的数据格式宿主（元数 1）。</summary>
    internal sealed class DataFormatsCore<TFormat>
    {
        private DataFormatsCore() { }
    }

    /// <summary>[空壳] WinForms 私有包的拖放宿主（元数 2）。</summary>
    internal sealed class DragDropHelper<TOleServices, TFormat>
    {
        private DragDropHelper() { }
    }

    /// <summary>[空壳] WinForms 私有包的剪贴板宿主（元数 1）。</summary>
    internal sealed class ClipboardCore<TOleServices>
    {
        private ClipboardCore() { }
    }

    /// <summary>[空壳] WinForms 私有包的数据对象组合宿主（元数 3）。</summary>
    internal sealed class Composition<TOleServices, TSerializer, TFormat>
    {
        private Composition() { }
    }
}

namespace System.Windows.Ole
{
    /// <summary>
    /// [空壳] 上游由 System/Windows/Ole/WpfOleServices.cs 定义（已剔除）：
    /// 它是 OLE 服务提供者，同时充当 System.Private.Windows.Ole 泛型宿主的类型参数。
    /// 这里只保留类型本身（GlobalUsings 的 alias 需要它）。
    /// </summary>
    internal sealed class WpfOleServices
    {
        private WpfOleServices() { }
    }
}

namespace System.Windows.Nrbf
{
    /// <summary>
    /// [空壳] 上游由 System/Windows/Nrbf/WpfNrbfSerializer.cs 定义（已剔除）：
    /// Composition&lt;,,&gt; 的序列化器类型参数。NRBF 序列化随 OLE 栈一并暂缓。
    /// </summary>
    internal sealed class WpfNrbfSerializer
    {
        private WpfNrbfSerializer() { }
    }
}
