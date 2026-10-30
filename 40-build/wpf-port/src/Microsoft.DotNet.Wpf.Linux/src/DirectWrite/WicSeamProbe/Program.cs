// T2 · WIC seam 探针：FileStream.SafeFileHandle 是不是"真 fd"？
// 上游路径（主控钉死）：BitmapDecoder.cs:1102 GetSeekableStream → :1111 `stream is FileStream`
//   → :1116 `!filestream.IsAsync` → :1118 `safeFilehandle = filestream.SafeFileHandle`
//   → WICImagingFactory.CreateDecoderFromFileHandle(hFile, ...)
// 本探针就验"那个 hFile 在 Linux 上是什么"。
using System;
using System.IO;
using Microsoft.Win32.SafeHandles;

class Program
{
    static int Main(string[] args)
    {
        string path = args.Length > 0 ? args[0]
            : (Environment.GetEnvironmentVariable("WPF_PROBE_PNG")
               ?? throw new InvalidOperationException("死根已清：未设 WPF_PROBE_PNG（本件不再内嵌退役树路径）"));
        Console.WriteLine("FILE=" + path);
        Console.WriteLine("EXISTS=" + File.Exists(path));

        using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Console.WriteLine("STREAM_TYPE=" + fs.GetType().FullName);
        Console.WriteLine("CAN_SEEK=" + fs.CanSeek);
        Console.WriteLine("IS_ASYNC=" + fs.IsAsync);          // 上游 :1116 的分支条件
        Console.WriteLine("LENGTH=" + fs.Length);

        SafeFileHandle handle = fs.SafeFileHandle;
        Console.WriteLine("SAFEHANDLE_TYPE=" + handle.GetType().FullName);
        long value = handle.DangerousGetHandle().ToInt64();
        Console.WriteLine("HANDLE_VALUE=" + value);

        // ① readlink /proc/self/fd/<值>：若显示字体/图像路径 ⇒ 就是真 fd
        string link = $"/proc/self/fd/{value}";
        try
        {
            string target = File.ResolveLinkTarget(link, returnFinalTarget: true)?.FullName
                            ?? new FileInfo(link).LinkTarget;
            Console.WriteLine("READLINK=" + target);
            Console.WriteLine("READLINK_MATCHES_FILE=" + string.Equals(target, path, StringComparison.Ordinal));
        }
        catch (Exception e) { Console.WriteLine("READLINK=FAIL " + e.GetType().Name + ": " + e.Message); }

        // ② pread(偏移 0, 8 字节)：PNG 应得 89 50 4E 47 0D 0A 1A 0A
        var head = new byte[8];
        int read = RandomAccess.Read(handle, head, 0);
        Console.WriteLine($"PREAD_READ={read} BYTES={Convert.ToHexString(head)}");
        bool png = head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47;
        Console.WriteLine("IS_PNG_MAGIC=" + png);

        // ③ seek + 再读：证明 shim 里可以任意 pread（不需要维护流位置）
        var tail = new byte[8];
        int read2 = RandomAccess.Read(handle, tail, fs.Length - 8);
        Console.WriteLine($"PREAD_TAIL_READ={read2} BYTES={Convert.ToHexString(tail)}");
        var mid = new byte[4];
        int read3 = RandomAccess.Read(handle, mid, 16);
        Console.WriteLine($"PREAD_MID_READ={read3} BYTES={Convert.ToHexString(mid)}");

        // ④ 同一个 fd 号在 /proc/self/fd 里存在（Linux 语义）
        Console.WriteLine("PROC_FD_EXISTS=" + File.Exists(link));

        Console.WriteLine("SEAM_OK=" + (png && read == 8));
        return png && read == 8 ? 0 : 1;
    }
}
