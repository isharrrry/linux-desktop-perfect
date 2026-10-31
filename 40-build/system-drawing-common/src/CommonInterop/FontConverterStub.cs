// 【ACC-028】`FontConverter` 的最小桩。
//   为什么需要：`Font.cs` 的 `[TypeConverter(typeof(FontConverter))]` 引用了它，
//   而真类型在 `System.Drawing.Design.dll` 里 —— 那个程序集反过来引用 `System.Drawing.Common`，
//   在这里引用会成环。桩只负责让特性编译通过；实际转换行为由设计器程序集提供。
namespace System.Drawing
{
    public partial class FontConverter : System.ComponentModel.TypeConverter
    {
        public override bool CanConvertFrom(System.ComponentModel.ITypeDescriptorContext context, System.Type sourceType)
            => sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
        public override object ConvertFrom(System.ComponentModel.ITypeDescriptorContext context,
                                            System.Globalization.CultureInfo culture, object value)
            => value is string s
                ? new Font(s, 12f, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point)
                : base.ConvertFrom(context, culture, value);
    }
}

namespace System.Drawing
{
    public partial class FontConverter
    {
        /// <summary>字体名字转换器（真实现在 System.Drawing.Design，这里只为特性/引用编译通过）。</summary>
        public sealed class FontNameConverter : System.ComponentModel.TypeConverter { }
        /// <summary>字体单位转换器（同上）。</summary>
        public sealed class FontUnitConverter : System.ComponentModel.TypeConverter { }
    }
}

namespace System.Drawing
{
    /// <summary>`[TypeConverter(typeof(IconConverter))]` 的桩（真实现在 System.Drawing.Design）。</summary>
    public class IconConverter : System.ComponentModel.TypeConverter { }
    /// <summary>`[TypeConverter(typeof(ImageConverter))]` 的桩（同上）。</summary>
    public class ImageConverter : System.ComponentModel.TypeConverter { }
}
