// 【ACC-028】`System.Drawing.Design.UITypeEditor` 的**最小桩**。
//   为什么需要：`Bitmap.Unix.cs` / `Icon.Unix.cs` / `Image.Unix.cs` 等在 `[Editor(...)]` 特性里
//   引用 `typeof(System.Drawing.Design.UITypeEditor)`；而这个类型在 **另一个程序集**
//   `System.Drawing.Design.dll` 里，那个程序集**反过来引用 `System.Drawing.Common`**
//   ⇒ 在这里引用它会形成程序集环。
//   做法：在本程序集内提供一个**同名同命名空间**的最小声明，只为让 `typeof(...)` 编译通过。
//   运行时真正的编辑器（`BitmapEditor` 等）来自 `System.Drawing.Design`，与这个桩不冲突
//   （`[Editor("System.Drawing.Design.BitmapEditor, System.Drawing.Design", typeof(UITypeEditor))]`，
//   字符串指向真类型，`typeof(UITypeEditor)` 只是特性参数的类型约束）。
using System;
using System.Drawing;

namespace System.Drawing.Design
{
    /// <summary>设计器编辑器基类的最小桩（见文件头注释）。</summary>
    public class UITypeEditor
    {
        public virtual object EditValue(System.ComponentModel.ITypeDescriptorContext context, object provider, object value) => value;
        public virtual UITypeEditorEditStyle GetEditStyle(System.ComponentModel.ITypeDescriptorContext context) => UITypeEditorEditStyle.None;
        public virtual void PaintValue(PaintValueEventArgs e) { }
        public virtual bool GetPaintValueSupported(System.ComponentModel.ITypeDescriptorContext context) => false;
        public virtual bool IsDropDownResizable => false;
    }

    public enum UITypeEditorEditStyle { None = 0, Modal = 1, DropDown = 2 }

    public class PaintValueEventArgs : EventArgs
    {
        public PaintValueEventArgs(System.ComponentModel.ITypeDescriptorContext context, object value, Graphics graphics, Rectangle bounds)
        { Context = context; Value = value; Graphics = graphics; Bounds = bounds; }
        public System.ComponentModel.ITypeDescriptorContext Context { get; }
        public object Value { get; }
        public Graphics Graphics { get; }
        public Rectangle Bounds { get; }
    }
}
