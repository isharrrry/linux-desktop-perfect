// LINUX 补丁：上游快照里没有 `PublicKeys` 类（Windows 侧由构建脚本生成，Linux 上没有那一步）。
// 这里给出等价常量 —— 值逐字抄自上游 `src/Microsoft.DotNet.Wpf/src/Shared/RefAssemblyAttrs.cs`
// 的 `BuildInfo.WCP_PUBLIC_KEY_STRING` 与 `DEVDIV_PUBLIC_KEY_STRING`（公钥，不是私钥）。
// 用途：System.Windows.Primitives 的 AssemblyInfo.cs 用它写 InternalsVisibleTo。
internal static class PublicKeys
{
    // MicrosoftShared 公钥（PublicKeyToken=31bf3856ad364e35）
    internal const string MicrosoftShared =
        "0024000004800000940000000602000000240000525341310004000001000100b5fc90e7027f67871e773a8fde8938c81dd402ba65b9201d60593e96c492651e889cc13f1415ebb53fac1131ae0bd333c5ee6021672d9718ea31a8aebd0da0072f25d87dba6fc90ffd598ed4da35e44c398c454307e8e33b8426143daec9f596836f97c8f74750e5975c64e2189f45def46b2a2b1247adc3652bf5c308055da9";

    // ECMA 公钥（PublicKeyToken=b77a5c561934e089）
    internal const string Ecma = "00000000000000000400000000000000";
}
