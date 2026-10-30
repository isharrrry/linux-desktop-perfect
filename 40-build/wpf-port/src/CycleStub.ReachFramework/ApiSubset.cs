// CycleStub.ReachFramework：ReachFramework 侧的**最小 API 替身**（编译期专用）。
//
// 为什么需要：见本工程 .csproj 头部注释 —— 上游用它打破
//   System.Printing ⇄ ReachFramework 的编译期循环，而 CycleBreakers 目录
//   在本仓库的上游快照里**不存在**，该工程不可移植。
//
// 形状来源（逐项抄自上游真源码，非凭记忆）：
//   PrintTicket                ← ReachFramework/PrintConfig/PrtTicket_Public_Simple.cs:240
//   PrintCapabilities          ← ReachFramework/PrintConfig/PrtCap_Public_Simple.cs:179
//   PrintTicketScope           ← ReachFramework/PrintConfig/PTManager.cs:160
//   ValidationResult           ← ReachFramework/PrintConfig/PTManager.cs:49
//   PackagingProgressEventArgs ← ReachFramework/Packaging/XpsInterleavingPolicy.cs:102
//
// 覆盖范围：System.Printing/ref/System.Printing.cs（唯一的 System.Printing 托管面）
//   对这些类型**只做类型引用**（实测：无任何成员访问），故此处只需类型声明。
//   若将来有消费方需要成员，编译器会立刻报出来 —— 到那时按同一规则补（并注明真源码出处）。

using System;
using System.Runtime.CompilerServices;

// 真源码：ReachFramework/AlphaFlattener/Utility.cs:14
//   [assembly: InternalsVisibleTo("System.Printing, PublicKey=" + BuildInfo.WCP_PUBLIC_KEY_STRING)]
// 用途：System.Printing 的 internals 面要用到 RF 的 internal 类型
//       （System.Printing/ref/System.Printing.internals.cs:69 → IXpsOMPackageWriter）。
// 公钥串 = Shared/RefAssemblyAttrs.cs:31 的 WCP_PUBLIC_KEY_STRING（公开常量，与 src/Linux/build/keys/WcpPublicKey.snk 逐字节相同）。
// 这里内联而不再编入 RefAssemblyAttrs.cs：避免替身与消费方源码里的 Microsoft.Internal.BuildInfo 同名冲突（CS0436）。
[assembly: InternalsVisibleTo("System.Printing, PublicKey=0024000004800000940000000602000000240000525341310004000001000100b5fc90e7027f67871e773a8fde8938c81dd402ba65b9201d60593e96c492651e889cc13f1415ebb53fac1131ae0bd333c5ee6021672d9718ea31a8aebd0da0072f25d87dba6fc90ffd598ed4da35e44c398c454307e8e33b8426143daec9f596836f97c8f74750e5975c64e2189f45def46b2a2b1247adc3652bf5c308055da9")]

namespace System.Printing
{
    /// <summary>真类型：ReachFramework/PrintConfig/PrtTicket_Public_Simple.cs:240（sealed, INotifyPropertyChanged）。</summary>
    public sealed class PrintTicket
    {
    }

    /// <summary>真类型：ReachFramework/PrintConfig/PrtCap_Public_Simple.cs:179（sealed）。</summary>
    public sealed class PrintCapabilities
    {
    }

    /// <summary>真类型：ReachFramework/PrintConfig/PTManager.cs:160（取值与顺序照抄）。</summary>
    public enum PrintTicketScope
    {
        PageScope = 0,
        DocumentScope = 1,
        JobScope = 2,
    }

    /// <summary>真类型：ReachFramework/PrintConfig/PTManager.cs:49（struct，构造 internal）。</summary>
    public struct ValidationResult
    {
    }
}

namespace System.Windows.Xps.Serialization
{
    /// <summary>
    /// 真源码：ReachFramework/Serialization/manager/XpsSerializationManagerAsync.cs:789（4 个取值照抄）。
    /// 消费方：PresentationFramework 的 System/Windows/Documents/Serialization/SerializerWriterEventHandlers.cs
    ///        （WritingPrintTicketRequiredEventArgs.CurrentPrintTicketLevel）。
    /// </summary>
    public enum PrintTicketLevel
    {
        None = 0,
        FixedDocumentSequencePrintTicket = 1,
        FixedDocumentPrintTicket = 2,
        FixedPagePrintTicket = 3,
    }
}

namespace System.Windows.Xps.Packaging
{
    /// <summary>真类型：ReachFramework/Packaging/XpsInterleavingPolicy.cs:102。</summary>
    public class PackagingProgressEventArgs : EventArgs
    {
    }

    /// <summary>
    /// 真源码：ReachFramework/Packaging/XpsDocument.cs:37
    ///   public class XpsDocument : XpsPartBase, INode, IDisposable
    /// 消费方：System.Printing/ref/System.Printing.internals.cs:84（XpsDocumentWriter 的 internal ctor 形参）。
    /// 替身只保留类型身份（消费方不访问成员）；基类省略（XpsPartBase/INode 是 RF 内部类型）。
    /// </summary>
    public class XpsDocument : IDisposable
    {
        public void Dispose() { }
    }
}

namespace System.Windows.Xps.Serialization.RCW
{
    /// <summary>
    /// 真源码：ReachFramework/Serialization/RCW/IXpsOMPackageWriter.cs:23（internal interface）。
    /// 消费方：System.Printing/ref/System.Printing.internals.cs:69（PrintQueue.XpsOMPackageWriter setter）。
    /// 可见性照抄 internal —— 靠上面的 IVT（真源码 ReachFramework/AlphaFlattener/Utility.cs:14）对
    /// System.Printing 开放，与上游同一机制。
    /// </summary>
    internal interface IXpsOMPackageWriter
    {
    }
}
