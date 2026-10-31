// 【ACC-028】手写程序集特性。
//   为什么不用 csproj 的 <AssemblyVersion>：本工程设了 <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
//   （corefx 的源码里有自己的 AssemblyInfo 风格），那个属性依赖自动生成器，所以**不会**写进 dll。
//   实测：只设 <AssemblyVersion> 时产物仍是 version=0.0.0.0，
//   而 System.Windows.Forms 的 AssemblyRef 写的是 Version=10.0.0.0 ⇒ FileNotFoundException。
//   ⇒ 必须在这里显式声明。
using System.Reflection;

[assembly: AssemblyVersion("10.0.0.0")]
[assembly: AssemblyFileVersion("10.0.0.0")]
[assembly: AssemblyInformationalVersion("10.0.0")]
