// 自动生成，勿手改。
// 来源：dotnet/wpf :: Common/Graphics/wgx_core_types.cs
// 重新生成：python3 build/gen-contracts.py
namespace WpfGfx.Linux.Contracts;

/// <summary>RenderData 内部绘图指令。由 skia-render 组实现——这是渲染的核心（共 25 条）</summary>
internal enum MilDrawCommand : byte
{
    /// <summary>0x3e</summary>
    MilDrawLine = 0x3e,
    /// <summary>0x3f</summary>
    MilDrawLineAnimate = 0x3f,
    /// <summary>0x40</summary>
    MilDrawRectangle = 0x40,
    /// <summary>0x41</summary>
    MilDrawRectangleAnimate = 0x41,
    /// <summary>0x42</summary>
    MilDrawRoundedRectangle = 0x42,
    /// <summary>0x43</summary>
    MilDrawRoundedRectangleAnimate = 0x43,
    /// <summary>0x44</summary>
    MilDrawEllipse = 0x44,
    /// <summary>0x45</summary>
    MilDrawEllipseAnimate = 0x45,
    /// <summary>0x46</summary>
    MilDrawGeometry = 0x46,
    /// <summary>0x47</summary>
    MilDrawImage = 0x47,
    /// <summary>0x48</summary>
    MilDrawImageAnimate = 0x48,
    /// <summary>0x49</summary>
    MilDrawGlyphRun = 0x49,
    /// <summary>0x4a</summary>
    MilDrawDrawing = 0x4a,
    /// <summary>0x4b</summary>
    MilDrawVideo = 0x4b,
    /// <summary>0x4c</summary>
    MilDrawVideoAnimate = 0x4c,
    /// <summary>0x4d</summary>
    MilPushClip = 0x4d,
    /// <summary>0x4e</summary>
    MilPushOpacityMask = 0x4e,
    /// <summary>0x4f</summary>
    MilPushOpacity = 0x4f,
    /// <summary>0x50</summary>
    MilPushOpacityAnimate = 0x50,
    /// <summary>0x51</summary>
    MilPushTransform = 0x51,
    /// <summary>0x52</summary>
    MilPushGuidelineSet = 0x52,
    /// <summary>0x53</summary>
    MilPushGuidelineY1 = 0x53,
    /// <summary>0x54</summary>
    MilPushGuidelineY2 = 0x54,
    /// <summary>0x55</summary>
    MilPushEffect = 0x55,
    /// <summary>0x56</summary>
    MilPop = 0x56,
}
