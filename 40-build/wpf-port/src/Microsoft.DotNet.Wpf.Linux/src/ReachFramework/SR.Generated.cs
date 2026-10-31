// 由 40-build/wpf-port/tools/gen-sr.py 从 ReachFramework/Resources/Strings.resx 生成。
// 上游用他们的构建系统生成（GenerateResourcesSource），我们的精简构建里没有，所以自己生成。
// 少了它，本程序集里 400+ 处 SR.Xxx 全是 CS0117/CS0122。
using System.Globalization;

namespace System.Windows.Xps
{
    internal static class SR
    {
        internal static string Converter_ConvertFromNotSupported => null;
        internal static string Converter_ConvertToNotSupported => null;
        internal static string MustBeOfType => null;
        internal static string PTProvider_InvalidDevModeBytes => null;
        internal static string PTProvider_VerifyAccess => null;
        internal static string ReachPackaging_AlreadyHasRootSequenceOrDocument => null;
        internal static string ReachPackaging_AlreadyHasStartingPart => null;
        internal static string ReachPackaging_AlreadyHasThumbnail => null;
        internal static string ReachPackaging_CannotModifyReadOnlyContainer => null;
        internal static string ReachPackaging_CommitCountPolicyLessThan1 => null;
        internal static string ReachPackaging_DependantsNotCommitted => null;
        internal static string ReachPackaging_DocumentWasClosed => null;
        internal static string ReachPackaging_InvalidContentType => null;
        internal static string ReachPackaging_InvalidDocUri => null;
        internal static string ReachPackaging_InvalidStartingPart => null;
        internal static string ReachPackaging_InvalidType => null;
        internal static string ReachPackaging_MoreThanOneDocStructure => null;
        internal static string ReachPackaging_MoreThanOneMetaDataParts => null;
        internal static string ReachPackaging_MoreThanOnePrintTicketPart => null;
        internal static string ReachPackaging_MoreThanOneSigDefParts => null;
        internal static string ReachPackaging_MoreThanOneSigOriginParts => null;
        internal static string ReachPackaging_MoreThanOneStartingParts => null;
        internal static string ReachPackaging_MoreThanOneStoryFragment => null;
        internal static string ReachPackaging_MoreThanOneThumbnailPart => null;
        internal static string ReachPackaging_NotAFixedDocument => null;
        internal static string ReachPackaging_NotAFixedDocumentSequence => null;
        internal static string ReachPackaging_NotAFixedPage => null;
        internal static string ReachPackaging_NotAPrintTicket => null;
        internal static string ReachPackaging_NotOpenForReading => null;
        internal static string ReachPackaging_NotSignatureDefinitionElement => null;
        internal static string ReachPackaging_NotValidSignatureDefinitionAttribute => null;
        internal static string ReachPackaging_NotValidSignatureDefinitionElement => null;
        internal static string ReachPackaging_ObfucatedFontNeedGuid => null;
        internal static string ReachPackaging_OnlyWriters => null;
        internal static string ReachPackaging_OpenDocOrElementAlreadyCalled => null;
        internal static string ReachPackaging_PackageUriNull => null;
        internal static string ReachPackaging_PanelOrSequenceAlreadyOpen => null;
        internal static string ReachPackaging_PartFromDifferentContainer => null;
        internal static string ReachPackaging_PartNotFound => null;
        internal static string ReachPackaging_PrintTicketAlreadyCommitted => null;
        internal static string ReachPackaging_SequenceCntMustBe1 => null;
        internal static string ReachPackaging_ServiceTypeAlreadyAdded => null;
        internal static string ReachPackaging_SigningDoesNotMeetPolicy => null;
        internal static string ReachPackaging_SpotIDRequiredAttribute => null;
        internal static string ReachPackaging_UnsupportedImageType => null;
        internal static string ReachPackaging_UnsupportedThumbnailImageType => null;
        internal static string ReachSerializationAsync_NoNgcType => null;
        internal static string ReachSerializationAsync_NoType => null;
        internal static string ReachSerialization_CannotReleaseXmlWriter => null;
        internal static string ReachSerialization_CycleDetectedInSerialization => null;
        internal static string ReachSerialization_ExpectedFixedDocument => null;
        internal static string ReachSerialization_ExpectedFixedDocumentSequence => null;
        internal static string ReachSerialization_ExpectedFixedPage => null;
        internal static string ReachSerialization_FixedDocumentInDocument => null;
        internal static string ReachSerialization_FixedDocumentInPage => null;
        internal static string ReachSerialization_FixedPageInPage => null;
        internal static string ReachSerialization_MustHaveSerializationManager => null;
        internal static string ReachSerialization_NoFixedDocuments => null;
        internal static string ReachSerialization_NoFixedPages => null;
        internal static string ReachSerialization_NoFixedPageWriter => null;
        internal static string ReachSerialization_NoFontService => null;
        internal static string ReachSerialization_NoImageService => null;
        internal static string ReachSerialization_NoSerializer => null;
        internal static string ReachSerialization_NotSupported => null;
        internal static string ReachSerialization_NotXpsSerializationManagerAsync => null;
        internal static string ReachSerialization_TargetNotPrintTicket => null;
        internal static string ReachSerialization_UnableToInstantiateSerializer => null;
        internal static string ReachSerialization_WrongPackagingPolicy => null;
        internal static string ReachSerialization_WrongPropertyTypeForFixedDocument => null;
        internal static string ReachSerialization_WrongPropertyTypeForFixedPage => null;
        internal static string ReachSerialization_WrongPropertyTypeForPageContent => null;
        internal static string XpsSerializerFactory_DisplayName => null;
        internal static string XpsSerializerFactory_ManufacturerName => null;
        internal static string XpsSerializerFactory_ManufacturerWebsite => null;
        internal static string XpsSerializerFactory_WriterIsClosed => null;

        internal static string Format(string format, params object[] args)
            => format == null ? null : string.Format(CultureInfo.InvariantCulture, format, args);
        internal static string GetString(string name) => null;
    }
}
