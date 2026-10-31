// 由 40-build/system-drawing-common/tools/gen-sr.py 从 src/Resources/Strings.resx 生成。
// corefx 用它的构建系统生成这份代码（GenerateResourcesSource），我们这份精简构建里没有，
// 所以自己生成一份：只为让 `SR.Xxx` / `SR.Format(...)` 编译通过（文案走资源文件，不在此处）。
using System.Globalization;

namespace System.Drawing
{
    internal static class SR
    {
        internal static string CantTellPrinterName => null;
        internal static string CantChangeImmutableObjects => null;
        internal static string CantMakeIconTransparent => null;
        internal static string ColorNotSystemColor => null;
        internal static string DotNET_ComponentType => null;
        internal static string GdiplusAborted => null;
        internal static string GdiplusAccessDenied => null;
        internal static string GdiplusCannotCreateGraphicsFromIndexedPixelFormat => null;
        internal static string GdiplusCannotSetPixelFromIndexedPixelFormat => null;
        internal static string GdiplusDestPointsInvalidParallelogram => null;
        internal static string GdiplusDestPointsInvalidLength => null;
        internal static string GdiplusFileNotFound => null;
        internal static string GdiplusFontFamilyNotFound => null;
        internal static string GdiplusFontStyleNotFound => null;
        internal static string GdiplusGenericError => null;
        internal static string GdiplusInsufficientBuffer => null;
        internal static string GdiplusInvalidParameter => null;
        internal static string GdiplusInvalidRectangle => null;
        internal static string GdiplusInvalidSize => null;
        internal static string GdiplusOutOfMemory => null;
        internal static string GdiplusNotImplemented => null;
        internal static string GdiplusNotInitialized => null;
        internal static string GdiplusNotTrueTypeFont => null;
        internal static string GdiplusNotTrueTypeFont_NoName => null;
        internal static string GdiplusObjectBusy => null;
        internal static string GdiplusOverflow => null;
        internal static string GdiplusPropertyNotFoundError => null;
        internal static string GdiplusPropertyNotSupportedError => null;
        internal static string GdiplusUnknown => null;
        internal static string GdiplusUnknownImageFormat => null;
        internal static string GdiplusUnsupportedGdiplusVersion => null;
        internal static string GdiplusWrongState => null;
        internal static string GlobalAssemblyCache => null;
        internal static string GraphicsBufferCurrentlyBusy => null;
        internal static string GraphicsBufferQueryFail => null;
        internal static string IconInvalidMaskLength => null;
        internal static string ToolboxItemLocked => null;
        internal static string ToolboxItemInvalidPropertyType => null;
        internal static string ToolboxItemValueNotSerializable => null;
        internal static string ToolboxItemInvalidKey => null;
        internal static string IllegalState => null;
        internal static string InterpolationColorsColorBlendNotSet => null;
        internal static string InterpolationColorsCommon => null;
        internal static string InterpolationColorsInvalidColorBlendObject => null;
        internal static string InterpolationColorsInvalidStartPosition => null;
        internal static string InterpolationColorsInvalidEndPosition => null;
        internal static string InterpolationColorsLength => null;
        internal static string InterpolationColorsLengthsDiffer => null;
        internal static string InvalidArgumentValue => null;
        internal static string InvalidBoundArgument => null;
        internal static string InvalidClassName => null;
        internal static string InvalidColor => null;
        internal static string InvalidDashPattern => null;
        internal static string InvalidEx2BoundArgument => null;
        internal static string InvalidFrame => null;
        internal static string InvalidGDIHandle => null;
        internal static string InvalidImage => null;
        internal static string InvalidLowBoundArgumentEx => null;
        internal static string InvalidPermissionLevel => null;
        internal static string InvalidPermissionState => null;
        internal static string InvalidPictureType => null;
        internal static string InvalidPrinterException_InvalidPrinter => null;
        internal static string InvalidPrinterException_NoDefaultPrinter => null;
        internal static string InvalidPrinterHandle => null;
        internal static string ValidRangeX => null;
        internal static string ValidRangeY => null;
        internal static string NativeHandle0 => null;
        internal static string NoDefaultPrinter => null;
        internal static string NotImplemented => null;
        internal static string PDOCbeginPrintDescr => null;
        internal static string PDOCdocumentNameDescr => null;
        internal static string PDOCdocumentPageSettingsDescr => null;
        internal static string PDOCendPrintDescr => null;
        internal static string PDOCoriginAtMarginsDescr => null;
        internal static string PDOCprintControllerDescr => null;
        internal static string PDOCprintPageDescr => null;
        internal static string PDOCprinterSettingsDescr => null;
        internal static string PDOCqueryPageSettingsDescr => null;
        internal static string PlatformNotSupported_Drawing => null;
        internal static string PrintDocumentDesc => null;
        internal static string PrintingPermissionBadXml => null;
        internal static string PrintingPermissionAttributeInvalidPermissionLevel => null;
        internal static string PropertyValueInvalidEntry => null;
        internal static string PSizeNotCustom => null;
        internal static string ResourceNotFound => null;
        internal static string TargetNotPrintingPermission => null;
        internal static string TextParseFailedFormat => null;
        internal static string TriStateCompareError => null;
        internal static string toStringIcon => null;
        internal static string toStringNone => null;
        internal static string DCTypeInvalid => null;
        internal static string InvalidEnumArgument => null;
        internal static string ConvertInvalidPrimitive => null;
        internal static string BlendObjectMustHaveTwoElements => null;
        internal static string BlendObjectFirstElementInvalid => null;
        internal static string BlendObjectLastElementInvalid => null;
        internal static string DllNotFoundExceptionMessage => null;
        internal static string EntryPointNotFoundExceptionMessage => null;

        internal static string Format(string format, params object[] args)
            => format == null ? null : string.Format(CultureInfo.InvariantCulture, format, args);
        internal static string GetString(string name) => null;
    }
}
