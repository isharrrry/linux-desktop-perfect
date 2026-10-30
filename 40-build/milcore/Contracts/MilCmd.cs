// 自动生成，勿手改。
// 来源：dotnet/wpf :: Common/Graphics/wgx_core_types.cs
// 重新生成：python3 build/gen-contracts.py
namespace WpfGfx.Linux.Contracts;

/// <summary>DUCE 顶层命令：资源创建/视觉树/目标管理。由 mil-core 组实现（共 118 条）</summary>
internal enum MilCmd : byte
{
    /// <summary>0x00</summary>
    MilCmdInvalid = 0x00,
    /// <summary>0x01</summary>
    MilCmdTransportSyncFlush = 0x01,
    /// <summary>0x02</summary>
    MilCmdTransportDestroyResourcesOnChannel = 0x02,
    /// <summary>0x03</summary>
    MilCmdPartitionRegisterForNotifications = 0x03,
    /// <summary>0x04</summary>
    MilCmdChannelRequestTier = 0x04,
    /// <summary>0x05</summary>
    MilCmdPartitionSetVBlankSyncMode = 0x05,
    /// <summary>0x06</summary>
    MilCmdPartitionNotifyPresent = 0x06,
    /// <summary>0x07</summary>
    MilCmdChannelCreateResource = 0x07,
    /// <summary>0x08</summary>
    MilCmdChannelDeleteResource = 0x08,
    /// <summary>0x09</summary>
    MilCmdChannelDuplicateHandle = 0x09,
    /// <summary>0x0a</summary>
    MilCmdD3DImage = 0x0a,
    /// <summary>0x0b</summary>
    MilCmdD3DImagePresent = 0x0b,
    /// <summary>0x0c</summary>
    MilCmdBitmapSource = 0x0c,
    /// <summary>0x0d</summary>
    MilCmdBitmapInvalidate = 0x0d,
    /// <summary>0x0e</summary>
    MilCmdDoubleResource = 0x0e,
    /// <summary>0x0f</summary>
    MilCmdColorResource = 0x0f,
    /// <summary>0x10</summary>
    MilCmdPointResource = 0x10,
    /// <summary>0x11</summary>
    MilCmdRectResource = 0x11,
    /// <summary>0x12</summary>
    MilCmdSizeResource = 0x12,
    /// <summary>0x13</summary>
    MilCmdMatrixResource = 0x13,
    /// <summary>0x14</summary>
    MilCmdPoint3DResource = 0x14,
    /// <summary>0x15</summary>
    MilCmdVector3DResource = 0x15,
    /// <summary>0x16</summary>
    MilCmdQuaternionResource = 0x16,
    /// <summary>0x17</summary>
    MilCmdMediaPlayer = 0x17,
    /// <summary>0x18</summary>
    MilCmdRenderData = 0x18,
    /// <summary>0x19</summary>
    MilCmdEtwEventResource = 0x19,
    /// <summary>0x1a</summary>
    MilCmdVisualCreate = 0x1a,
    /// <summary>0x1b</summary>
    MilCmdVisualSetOffset = 0x1b,
    /// <summary>0x1c</summary>
    MilCmdVisualSetTransform = 0x1c,
    /// <summary>0x1d</summary>
    MilCmdVisualSetEffect = 0x1d,
    /// <summary>0x1e</summary>
    MilCmdVisualSetCacheMode = 0x1e,
    /// <summary>0x1f</summary>
    MilCmdVisualSetClip = 0x1f,
    /// <summary>0x20</summary>
    MilCmdVisualSetAlpha = 0x20,
    /// <summary>0x21</summary>
    MilCmdVisualSetRenderOptions = 0x21,
    /// <summary>0x22</summary>
    MilCmdVisualSetContent = 0x22,
    /// <summary>0x23</summary>
    MilCmdVisualSetAlphaMask = 0x23,
    /// <summary>0x24</summary>
    MilCmdVisualRemoveAllChildren = 0x24,
    /// <summary>0x25</summary>
    MilCmdVisualRemoveChild = 0x25,
    /// <summary>0x26</summary>
    MilCmdVisualInsertChildAt = 0x26,
    /// <summary>0x27</summary>
    MilCmdVisualSetGuidelineCollection = 0x27,
    /// <summary>0x28</summary>
    MilCmdVisualSetScrollableAreaClip = 0x28,
    /// <summary>0x29</summary>
    MilCmdViewport3DVisualSetCamera = 0x29,
    /// <summary>0x2a</summary>
    MilCmdViewport3DVisualSetViewport = 0x2a,
    /// <summary>0x2b</summary>
    MilCmdViewport3DVisualSet3DChild = 0x2b,
    /// <summary>0x2c</summary>
    MilCmdVisual3DSetContent = 0x2c,
    /// <summary>0x2d</summary>
    MilCmdVisual3DSetTransform = 0x2d,
    /// <summary>0x2e</summary>
    MilCmdVisual3DRemoveAllChildren = 0x2e,
    /// <summary>0x2f</summary>
    MilCmdVisual3DRemoveChild = 0x2f,
    /// <summary>0x30</summary>
    MilCmdVisual3DInsertChildAt = 0x30,
    /// <summary>0x31</summary>
    MilCmdHwndTargetCreate = 0x31,
    /// <summary>0x32</summary>
    MilCmdHwndTargetSuppressLayered = 0x32,
    /// <summary>0x33</summary>
    MilCmdTargetUpdateWindowSettings = 0x33,
    /// <summary>0x34</summary>
    MilCmdGenericTargetCreate = 0x34,
    /// <summary>0x35</summary>
    MilCmdTargetSetRoot = 0x35,
    /// <summary>0x36</summary>
    MilCmdTargetSetClearColor = 0x36,
    /// <summary>0x37</summary>
    MilCmdTargetInvalidate = 0x37,
    /// <summary>0x38</summary>
    MilCmdTargetSetFlags = 0x38,
    /// <summary>0x39</summary>
    MilCmdHwndTargetDpiChanged = 0x39,
    /// <summary>0x3a</summary>
    MilCmdGlyphRunCreate = 0x3a,
    /// <summary>0x3b</summary>
    MilCmdDoubleBufferedBitmap = 0x3b,
    /// <summary>0x3c</summary>
    MilCmdDoubleBufferedBitmapCopyForward = 0x3c,
    /// <summary>0x3d</summary>
    MilCmdPartitionNotifyPolicyChangeForNonInteractiveMode = 0x3d,
    /// <summary>0x57</summary>
    MilCmdAxisAngleRotation3D = 0x57,
    /// <summary>0x58</summary>
    MilCmdQuaternionRotation3D = 0x58,
    /// <summary>0x59</summary>
    MilCmdPerspectiveCamera = 0x59,
    /// <summary>0x5a</summary>
    MilCmdOrthographicCamera = 0x5a,
    /// <summary>0x5b</summary>
    MilCmdMatrixCamera = 0x5b,
    /// <summary>0x5c</summary>
    MilCmdModel3DGroup = 0x5c,
    /// <summary>0x5d</summary>
    MilCmdAmbientLight = 0x5d,
    /// <summary>0x5e</summary>
    MilCmdDirectionalLight = 0x5e,
    /// <summary>0x5f</summary>
    MilCmdPointLight = 0x5f,
    /// <summary>0x60</summary>
    MilCmdSpotLight = 0x60,
    /// <summary>0x61</summary>
    MilCmdGeometryModel3D = 0x61,
    /// <summary>0x62</summary>
    MilCmdMeshGeometry3D = 0x62,
    /// <summary>0x63</summary>
    MilCmdMaterialGroup = 0x63,
    /// <summary>0x64</summary>
    MilCmdDiffuseMaterial = 0x64,
    /// <summary>0x65</summary>
    MilCmdSpecularMaterial = 0x65,
    /// <summary>0x66</summary>
    MilCmdEmissiveMaterial = 0x66,
    /// <summary>0x67</summary>
    MilCmdTransform3DGroup = 0x67,
    /// <summary>0x68</summary>
    MilCmdTranslateTransform3D = 0x68,
    /// <summary>0x69</summary>
    MilCmdScaleTransform3D = 0x69,
    /// <summary>0x6a</summary>
    MilCmdRotateTransform3D = 0x6a,
    /// <summary>0x6b</summary>
    MilCmdMatrixTransform3D = 0x6b,
    /// <summary>0x6c</summary>
    MilCmdPixelShader = 0x6c,
    /// <summary>0x6d</summary>
    MilCmdImplicitInputBrush = 0x6d,
    /// <summary>0x6e</summary>
    MilCmdBlurEffect = 0x6e,
    /// <summary>0x6f</summary>
    MilCmdDropShadowEffect = 0x6f,
    /// <summary>0x70</summary>
    MilCmdShaderEffect = 0x70,
    /// <summary>0x71</summary>
    MilCmdDrawingImage = 0x71,
    /// <summary>0x72</summary>
    MilCmdTransformGroup = 0x72,
    /// <summary>0x73</summary>
    MilCmdTranslateTransform = 0x73,
    /// <summary>0x74</summary>
    MilCmdScaleTransform = 0x74,
    /// <summary>0x75</summary>
    MilCmdSkewTransform = 0x75,
    /// <summary>0x76</summary>
    MilCmdRotateTransform = 0x76,
    /// <summary>0x77</summary>
    MilCmdMatrixTransform = 0x77,
    /// <summary>0x78</summary>
    MilCmdLineGeometry = 0x78,
    /// <summary>0x79</summary>
    MilCmdRectangleGeometry = 0x79,
    /// <summary>0x7a</summary>
    MilCmdEllipseGeometry = 0x7a,
    /// <summary>0x7b</summary>
    MilCmdGeometryGroup = 0x7b,
    /// <summary>0x7c</summary>
    MilCmdCombinedGeometry = 0x7c,
    /// <summary>0x7d</summary>
    MilCmdPathGeometry = 0x7d,
    /// <summary>0x7e</summary>
    MilCmdSolidColorBrush = 0x7e,
    /// <summary>0x7f</summary>
    MilCmdLinearGradientBrush = 0x7f,
    /// <summary>0x80</summary>
    MilCmdRadialGradientBrush = 0x80,
    /// <summary>0x81</summary>
    MilCmdImageBrush = 0x81,
    /// <summary>0x82</summary>
    MilCmdDrawingBrush = 0x82,
    /// <summary>0x83</summary>
    MilCmdVisualBrush = 0x83,
    /// <summary>0x84</summary>
    MilCmdBitmapCacheBrush = 0x84,
    /// <summary>0x85</summary>
    MilCmdDashStyle = 0x85,
    /// <summary>0x86</summary>
    MilCmdPen = 0x86,
    /// <summary>0x87</summary>
    MilCmdGeometryDrawing = 0x87,
    /// <summary>0x88</summary>
    MilCmdGlyphRunDrawing = 0x88,
    /// <summary>0x89</summary>
    MilCmdImageDrawing = 0x89,
    /// <summary>0x8a</summary>
    MilCmdVideoDrawing = 0x8a,
    /// <summary>0x8b</summary>
    MilCmdDrawingGroup = 0x8b,
    /// <summary>0x8c</summary>
    MilCmdGuidelineSet = 0x8c,
    /// <summary>0x8d</summary>
    MilCmdBitmapCache = 0x8d,
    /// <summary>0x8e</summary>
    MilCmdValidateStructureOrder = 0x8e,
}
