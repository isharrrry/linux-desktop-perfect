// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

// 改动9：PBT 的程序集解析器。
//
// 官方 PathAssemblyResolver 的规矩是"名字同 且 公钥 token 完全同"（版本允许 找到的 >= 请求的）。
// 第三方 WPF NuGet 包的 windows 资产按官方 WindowsDesktop 身份请求:
//     System.Xaml            5.0.0.0 / b77a5c561934e089
//     PresentationFramework  5.0.0.0 / 31bf3856ad364e35
// 而本仓自产件是 4.0.0.1 / 31bf3856ad364e35（System.Xaml 的 token 都不同）
// ⇒ PathAssemblyResolver 一律拒绝 ⇒ MarkupCompilePass1 抛
//    MC1000: Could not find assembly 'System.Xaml, Version=5.0.0.0, ...'
//
// 本类先按官方规矩解析；解析不到时退回"只按简单名找"（同名多份时优先公钥 token 相符、
// 其次版本最近的候选）。这样 XAML 编译器可以**直接认自产真件**，
// 不需要"身份改版件"（那会把编译期与运行期的身份劈成两套，运行期全黑）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace MS.Internal.Markup
{
    internal sealed class NameFallbackResolver : MetadataAssemblyResolver
    {
        private sealed class Candidate
        {
            internal string Path;
            internal Version Version;
            internal string PublicKeyToken;   // 小写十六进制；无签名件为 null
        }

        private readonly PathAssemblyResolver _strict;
        private readonly Dictionary<string, List<Candidate>> _bySimpleName =
            new Dictionary<string, List<Candidate>>(StringComparer.OrdinalIgnoreCase);

        internal NameFallbackResolver(IEnumerable<string> assemblyPaths)
        {
            if (assemblyPaths == null)
            {
                throw new ArgumentNullException(nameof(assemblyPaths));
            }

            List<string> paths = new List<string>();
            foreach (string p in assemblyPaths)
            {
                if (string.IsNullOrEmpty(p))
                {
                    continue;
                }

                paths.Add(p);

                string simpleName = System.IO.Path.GetFileNameWithoutExtension(p);
                if (string.IsNullOrEmpty(simpleName))
                {
                    continue;
                }

                Candidate candidate = new Candidate { Path = p };
                try
                {
                    AssemblyName an = AssemblyName.GetAssemblyName(p);
                    candidate.Version = an.Version;
                    byte[] token = an.GetPublicKeyToken();
                    if (token != null && token.Length > 0)
                    {
                        candidate.PublicKeyToken = ToHex(token);
                    }
                }
                catch (Exception)
                {
                    // 读不出身份的文件（不是托管程序集等）仍按简单名登记，但不参与 token 优先选择
                }

                List<Candidate> list;
                if (!_bySimpleName.TryGetValue(simpleName, out list))
                {
                    list = new List<Candidate>();
                    _bySimpleName[simpleName] = list;
                }

                list.Add(candidate);
            }

            _strict = new PathAssemblyResolver(paths);
        }

        public override Assembly Resolve(MetadataLoadContext context, AssemblyName assemblyName)
        {
            if (assemblyName == null)
            {
                throw new ArgumentNullException(nameof(assemblyName));
            }

            Assembly resolved = null;
            try
            {
                resolved = _strict.Resolve(context, assemblyName);
            }
            catch (FileNotFoundException)
            {
                resolved = null;
            }

            if (resolved != null || assemblyName.Name == null)
            {
                return resolved;
            }

            List<Candidate> candidates;
            if (!_bySimpleName.TryGetValue(assemblyName.Name, out candidates) || candidates.Count == 0)
            {
                return null;
            }

            Candidate pick = null;
            byte[] wantToken = assemblyName.GetPublicKeyToken();
            if (wantToken != null && wantToken.Length > 0)
            {
                string want = ToHex(wantToken);
                foreach (Candidate c in candidates)
                {
                    if (string.Equals(c.PublicKeyToken, want, StringComparison.OrdinalIgnoreCase))
                    {
                        pick = c;
                        break;
                    }
                }
            }

            if (pick == null && assemblyName.Version != null)
            {
                foreach (Candidate c in candidates)
                {
                    if (c.Version != null && c.Version >= assemblyName.Version &&
                        (pick == null || c.Version < pick.Version))
                    {
                        pick = c;
                    }
                }
            }

            if (pick == null)
            {
                pick = candidates[0];
            }

            return context.LoadFromAssemblyPath(pick.Path);
        }

        private static string ToHex(byte[] bytes)
        {
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
            {
                sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }
    }
}
