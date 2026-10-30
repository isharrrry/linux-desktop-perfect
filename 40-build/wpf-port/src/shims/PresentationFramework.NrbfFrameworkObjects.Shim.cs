// WPF-on-Linux 兼容层：System.Private.Windows.Core 的 NRBF「框架对象」读写（语义化降级实现）。
//
// ── 为什么需要 ──────────────────────────────────────────────────────────
// 上游 PresentationFramework/MS/Internal/DataStreams.cs:13 有
//     using System.Private.Windows.BinaryFormat;
// 该命名空间来自 WinForms 私有包 System.Private.Windows.Core（上游用
// MicrosoftPrivateWinFormsReference 引入）。本工程实测：
//     dotnet restore 引用 System.Private.Windows.Core → NU1101（镜像 huaweicloud 无此 ID）
// 离线不可获得 → 只能以源码 shim 提供调用点所需的两个成员。
//
// ── 需要哪两个成员（**从调用点反推，不是凭记忆**）──────────────────────
//   DataStreams.cs:126  success = BinaryFormatWriter.TryWriteFrameworkObject(byteStream, currentValue);
//   DataStreams.cs:254  NrbfDecoder.Decode(dataStream, leaveOpen: true).TryGetFrameworkObject(out object val);
// 即：
//   · static bool BinaryFormatWriter.TryWriteFrameworkObject(Stream, object)
//   · static bool SerializationRecord.TryGetFrameworkObject(out object)   ← 扩展方法
//     ⚠ 实测修正（M5 第二轮）：该扩展**由 NuGet 包 System.Formats.Nrbf 9.0.0 自己提供**
//       （SerializationRecordExtensions.TryGetFrameworkObject），本 shim 再声明一份会 CS0121 二义性
//       → 已删除，改用包自身的实现。
//
// ── 为什么「恒返回 false」是**语义正确**而不是伪造 ─────────────────────
// 上游对这两个返回值有明确的、写在同文件里的降级分支：
//   · 写：DataStreams.cs:134  if (!success) { this.Formatter.Serialize(byteStream, currentValue); }
//         —— 注释与代码都是「NRBF 写不了这个类型 → 退回 BinaryFormatter」；
//   · 读：DataStreams.cs:257  if (newValue == null) { newValue = this.Formatter.Deserialize(dataStream); }
// 也就是说 false / null 正是上游为「本类型不在 NRBF 支持面内」预留的路径。
// 我们的 shim 声明「本平台没有 NRBF 框架对象写入器」→ 上游自动走它自己的退化路径，
// 不改变任何调用点的控制流，也不伪造任何数据。
// （与 WindowsBase 的 PInvoke.DwmIsCompositionEnabled 恒返回「无 DWM」同一处置口径。）
//
// ── 已知的运行期后果（诚实记录，不在 M5 范围）──────────────────────────
// 退化路径落到 BinaryFormatter —— .NET 9+ 已在运行期弃用/禁用该序列化器
// （类型仍在，调用会抛 PlatformNotSupportedException，除非显式开启
//  EnableUnsafeBinaryFormatterSerialization）。即：**journal（导航日志）状态的
//  保存/恢复在 Linux 上目前不可用**。受影响路径：Frame/NavigationService 的
//  journal（XBAP 式导航），HelloWpf 不经过它。
// 恢复条件：实现 MS-NRBF 的 framework-object 写入器（WinForms 私有实现约数百行，
//  规范见 MS-NRBF；离线无法复核形状与语义），或改用自定义 journal 序列化格式。
//
// 未被本 shim 覆盖的：System.Private.Windows.Core 的其它类型（本工程实测不需要——
// 全项目仅 DataStreams.cs 一个文件引用该命名空间）。

using System.Formats.Nrbf;
using System.IO;

namespace System.Private.Windows.BinaryFormat
{
    /// <summary>NRBF framework-object 写入器：Linux 上无实现，恒返回 false（调用点回退）。</summary>
    internal static class BinaryFormatWriter
    {
        internal static bool TryWriteFrameworkObject(Stream stream, object value) => false;
    }

    /// <summary>
    /// 上游 DataStreams.cs 里 `ex.IsCriticalException()` 来自 WinForms 私有包
    /// System.Private.Windows.Core 的扩展方法；本 shim **直接委托给上游自己的实现**
    /// Shared/MS/Internal/CriticalExceptions.cs:19（编入 WindowsBase，PF 有 IVT 可访问），
    /// 不复制那份「哪些异常算致命」的判断逻辑。
    /// </summary>
    internal static class WpfLinuxExceptionExtensions
    {
        internal static bool IsCriticalException(this System.Exception ex)
            => MS.Internal.CriticalExceptions.IsCriticalException(ex);
    }
}
