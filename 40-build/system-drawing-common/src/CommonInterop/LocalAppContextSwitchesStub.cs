// 【ACC-028】`LocalAppContextSwitches.GetCachedSwitchValue` 的最小桩。
//   corefx 里这个方法在 `src/Common/src/CoreLib/System/LocalAppContextSwitches.Common.cs`，
//   用 `AppContext.TryGetSwitch` 做三态缓存（0=未知 / 1=真 / -1=假）。
//   Linux 上这些开关都取默认值（false）即可，不需要真的读 AppContext。
using System.Runtime.CompilerServices;

namespace System
{
    internal static partial class LocalAppContextSwitches
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool GetCachedSwitchValue(string switchName, ref int cachedSwitchValue)
        {
            // 与 corefx 语义一致：负=假、正=真、零=未知。我们不做 AppContext 查询，一律返回假。
            if (cachedSwitchValue > 0) return true;
            return false;
        }
    }
}
