// 【ACC-024】手写程序集特性。
//   为什么必须：本工程的 `GenerateAssemblyInfo=false`，csproj 里的 <AssemblyVersion> 不会生效
//   （同一个坑在 System.Drawing.Common 上踩过，见 K-022）。
//   而 `System.Printing` 的**程序集身份**必须与运行时那份一致：
//       真 System.Printing.dll:  name=System.Printing ver=**10.0.0.0** token=31bf3856ad364e35
//       本 api-cycle 产物（修前）: name=System.Printing ver=**0.0.0.0** token=31bf3856ad364e35
//   版本号不一致 ⇒ MSBuild 报 MSB3277（同一依赖程序集的不同版本冲突）⇒ ReachFramework 里
//   `SerializerWriter` 的 `PrintTicket` 参数被解析成**另一份**的 `PrintTicket`
//   ⇒ 几十个 CS0534/CS0115（全是签名对不上，见 K-034）。
using System.Reflection;

[assembly: AssemblyVersion("10.0.0.0")]
[assembly: AssemblyFileVersion("10.0.0.0")]
[assembly: AssemblyInformationalVersion("10.0.0")]
