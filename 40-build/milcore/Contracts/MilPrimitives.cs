using System.Runtime.InteropServices;

namespace WpfGfx.Linux.Contracts;

/// <summary>
/// MIL 资源句柄。与上游 DUCE.ResourceHandle 同构：0 表示空句柄。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct MilResourceHandle(uint handle)
{
    public static readonly MilResourceHandle Null = new(0);

    private readonly uint _handle = handle;

    public uint Value => _handle;
    public bool IsNull => _handle == 0;

    public static explicit operator uint(MilResourceHandle h) => h._handle;
    public static explicit operator MilResourceHandle(uint v) => new(v);

    public bool Equals(MilResourceHandle other) => _handle == other._handle;
    public override bool Equals(object? obj) => obj is MilResourceHandle h && Equals(h);
    public override int GetHashCode() => _handle.GetHashCode();
    public override string ToString() => $"0x{_handle:x8}";
}

/// <summary>
/// 通道封送模式。与上游 ChannelMarshalType 一致。
/// </summary>
internal enum MilChannelMarshalType : uint
{
    Invalid = 0x0,
    SameThread = 0x1,
    CrossThread = 0x2,
}

/// <summary>
/// HRESULT 常量。MIL 导出函数一律返回 HRESULT。
/// </summary>
internal static class MilHResult
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
    public const int E_HANDLE = unchecked((int)0x80070006);

    /// <summary>WPF 惯用：把 Win32 错误码转成 HRESULT。</summary>
    public static int FromWin32(int win32) =>
        win32 == 0 ? S_OK : unchecked((int)0x80070000) | (win32 & 0xFFFF);

    public static bool Succeeded(int hr) => hr >= 0;
    public static bool Failed(int hr) => hr < 0;
}
