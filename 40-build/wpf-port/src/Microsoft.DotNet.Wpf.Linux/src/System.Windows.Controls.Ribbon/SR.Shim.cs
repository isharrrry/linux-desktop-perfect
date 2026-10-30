// SR 资源属性的 Linux 补位（RIBBON_IN_FRAMEWORK）
//
// 上游用内部 ResxGenerator 从 Resources/Strings.resx 生成强类型 SR 属性；本仓的 Linux 构建链
// 没有那个生成器，于是 `SR.InvalidKeyTipOffset` 之类的**属性**缺失（CS0117）。这里按 resx 的
// data 名生成同一组属性：值取自资源（缺失时回落到键名，不抛），`Format` 用 string.Format。
// 口径：这些是**异常/无障碍文案**，不是渲染路径；回落到键名不会伪造语义。
using System;
using System.Resources;

namespace Microsoft.Windows.Controls
{
    internal partial class SR
    {
        private static readonly ResourceManager s_rm =
            new ResourceManager("System.Windows.Controls.Ribbon.Resources.Strings", typeof(SR).Assembly);

        // 上游生成的 SR.Designer.cs 里有这个静态属性（Common SR.cs 的 GetResourceString 直接调它）
        internal static ResourceManager ResourceManager => s_rm;

        internal static string Get(string key)
        {
            try { return s_rm.GetString(key) ?? key; } catch { return key; }
        }

        internal static string Name1 => Get("Name1");
        internal static string Color1 => Get("Color1");
        internal static string Bitmap1 => Get("Bitmap1");
        internal static string Icon1 => Get("Icon1");
        internal static string ElementNotKeyTipScope => Get("ElementNotKeyTipScope");
        internal static string InRibbonGallery_ScrollDownButtonAutomationName => Get("InRibbonGallery_ScrollDownButtonAutomationName");
        internal static string InRibbonGallery_ScrollUpButtonAutomationName => Get("InRibbonGallery_ScrollUpButtonAutomationName");
        internal static string InvalidApplicationMenuOrItemContainer => Get("InvalidApplicationMenuOrItemContainer");
        internal static string InvalidCtorParameterNoInfinityForStarSize => Get("InvalidCtorParameterNoInfinityForStarSize");
        internal static string InvalidCtorParameterNoNaN => Get("InvalidCtorParameterNoNaN");
        internal static string InvalidCtorParameterUnknownRibbonControlLengthUnitType => Get("InvalidCtorParameterUnknownRibbonControlLengthUnitType");
        internal static string InvalidKeyTipOffset => Get("InvalidKeyTipOffset");
        internal static string InvalidMenuButtonOrItemContainer => Get("InvalidMenuButtonOrItemContainer");
        internal static string QATKeyTipCharacters => Get("QATKeyTipCharacters");
        internal static string ResizeParametersNotValid => Get("ResizeParametersNotValid");
        internal static string RibbonContextMenu_AddGalleryToQAT => Get("RibbonContextMenu_AddGalleryToQAT");
        internal static string RibbonContextMenu_AddToQAT => Get("RibbonContextMenu_AddToQAT");
        internal static string RibbonContextMenu_MaximizeTheRibbon => Get("RibbonContextMenu_MaximizeTheRibbon");
        internal static string RibbonContextMenu_MinimizeTheRibbon => Get("RibbonContextMenu_MinimizeTheRibbon");
        internal static string RibbonContextMenu_RemoveFromQAT => Get("RibbonContextMenu_RemoveFromQAT");
        internal static string RibbonContextMenu_ShowQATAbove => Get("RibbonContextMenu_ShowQATAbove");
        internal static string RibbonContextMenu_ShowQATBelow => Get("RibbonContextMenu_ShowQATBelow");
        internal static string RibbonGallery_AllFilter => Get("RibbonGallery_AllFilter");
        internal static string RibbonGroupsPanel_InvalidRegistrationParameter => Get("RibbonGroupsPanel_InvalidRegistrationParameter");
        internal static string RibbonQuickAccessToolBar_OverflowButtonToolTip => Get("RibbonQuickAccessToolBar_OverflowButtonToolTip");
        internal static string RibbonWindow_ExtendUnShownGlass => Get("RibbonWindow_ExtendUnShownGlass");
        internal static string Ribbon_ContextualTabHeadersSourceInvalid => Get("Ribbon_ContextualTabHeadersSourceInvalid");
        internal static string SetFocusFailed => Get("SetFocusFailed");
        internal static string UIA_OperationCannotBePerformed => Get("UIA_OperationCannotBePerformed");
        internal static string VirtualizedElement => Get("VirtualizedElement");
    }
}
