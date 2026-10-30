// Licensed to the .NET Foundation under one or more agreements.
//
// HRESULT 常量。MIL 导出函数全部返回 HRESULT（int）。

namespace WpfGfx.Linux.Interop
{
    public static class HResult
    {
        public const int S_OK = 0;
        public const int S_FALSE = 1;

        public const int E_NOTIMPL = unchecked((int)0x80004001);
        public const int E_NOINTERFACE = unchecked((int)0x80004002);
        public const int E_FAIL = unchecked((int)0x80004005);
        public const int E_UNEXPECTED = unchecked((int)0x8000FFFF);
        public const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
        public const int E_INVALIDARG = unchecked((int)0x80070057);
        public const int E_HANDLE = unchecked((int)0x80070006);

        /// <summary>MIL 专用：通道/分区已失效。</summary>
        public const int MILERR_PARTITION_ZOMBIE = unchecked((int)0x8898000A);

        public static bool Succeeded(int hr) => hr >= 0;
        public static bool Failed(int hr) => hr < 0;

        public static string Name(int hr) => hr switch
        {
            S_OK => "S_OK",
            S_FALSE => "S_FALSE",
            E_NOTIMPL => "E_NOTIMPL",
            E_NOINTERFACE => "E_NOINTERFACE",
            E_FAIL => "E_FAIL",
            E_UNEXPECTED => "E_UNEXPECTED",
            E_OUTOFMEMORY => "E_OUTOFMEMORY",
            E_INVALIDARG => "E_INVALIDARG",
            E_HANDLE => "E_HANDLE",
            _ => $"0x{hr:X8}",
        };
    }
}
