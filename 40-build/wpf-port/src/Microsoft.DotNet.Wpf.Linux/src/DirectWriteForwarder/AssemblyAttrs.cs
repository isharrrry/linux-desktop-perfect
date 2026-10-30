// DirectWriteForwarder · Linux 托管骨架 —— 程序集级特性
//
// 等价于上游 DirectWriteForwarder/OtherAssemblyAttrs.cpp 的这一条：
//   [assembly:InternalsVisibleTo("PresentationCore, PublicKey=<WCP>")]
// 下面这条 IVT 的**公钥字面量由脚本从上游该文件机械提取**（不是手抄），
// 已核对与 src/Linux/build/keys/WcpPublicKey.snk 逐字节相同（本工程公开签名用同一把公钥）。
//
// AssemblyVersion/FileVersion/InformationalVersion **不在此声明**：由
// src/Microsoft.DotNet.Wpf.Linux/src/shims/LinuxAssemblyIdentity.cs 统一提供（主控约定 4.0.0.1，重复声明会 CS0579）。

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("PresentationCore, PublicKey=0024000004800000940000000602000000240000525341310004000001000100b5fc90e7027f67871e773a8fde8938c81dd402ba65b9201d60593e96c492651e889cc13f1415ebb53fac1131ae0bd333c5ee6021672d9718ea31a8aebd0da0072f25d87dba6fc90ffd598ed4da35e44c398c454307e8e33b8426143daec9f596836f97c8f74750e5975c64e2189f45def46b2a2b1247adc3652bf5c308055da9")]
