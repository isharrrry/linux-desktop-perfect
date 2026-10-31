// 【ACC-028】`Consts` 的最小桩。
//   用途：`[Editor("System.Drawing.Design.ImageEditor, " + Consts.AssemblySystem_Drawing_Design, typeof(UITypeEditor))]`
//   —— 只是**特性里的字符串**，运行期由设计器去解析。corefx 里这个类来自它的共享构建
//   （`Configurations.props` 一组 props 生成），我们这份精简构建里没有，补一个常量即可。
using System;

namespace System.Drawing
{
    internal static class Consts
    {
        /// <summary>设计器程序集的**显示名**（特性字符串里用，不需要强名称）。</summary>
        internal const string AssemblySystem_Drawing_Design = "System.Drawing.Design";
    }
}
