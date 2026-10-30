// Licensed to the .NET Foundation under one or more agreements.
//
// M7a · 分组 A：MilUtility_* 几何导出的 Linux 实现（15 个）。
//
// 【一句话】这一组是**真几何**：路径数据解析、矩阵变换、展平、描边外扩、布尔运算、
//   面积、命中测试、弧转贝塞尔、TileBrush 映射、像素缓冲按位拷贝、3D 投影包围盒，
//   全部落到 SkiaSharp + Interop/MilGeometryEngine.cs 里的自写算法，能数值断言。
//
// 【矩阵参数为什么是 double*】
//   上游这些函数的参数类型是 MilMatrix3x2D*（6 个 double：S_11 S_12 S_21 S_22 DX DY）。
//   本工程既有的 MilMatrix3x2D 是 internal，而 C# 不允许 public 方法签名里出现
//   internal 类型（CS0051，InternalsVisibleTo 也救不了），所以这里退化成 double*
//   ——布局逐字节一致，调用方 `(double*)&matrix` 即可，零成本。
//
// 【容差与阈值的口径】
//   rTolerance（Flatten/Widen/Outline/Bounds/Area）：曲线的展平精度。
//     fRelative=true 时按几何包围盒最大边长缩放（WPF ToleranceType.Relative）。
//   rThreshold（HitTest 系列）：**命中距离阈值**，不是展平精度！
//     填充命中 = 落在填充内 或 到边界距离 ≤ 阈值；
//     描边命中 = 落在描边轮廓内 或 到描边轮廓距离 ≤ 阈值。
//
// 【已知偏差（诚实标注）】
//   1. fSkipHollows 被接受但不改变结果。上游用它跳过"不可填充图形"（只出现在
//      region data 里，M1 不产出这类数据）；包围盒与面积对所有图形一视同仁。
//   2. Arc 段一律走 Skia 的 ArcTo（见 Rendering/PathGeometryParser.cs 的存疑注释）。
//   3. Exclude 映射成 SKPathOp.Difference，与 Xor 的区分未经真机比对
//      （与 Rendering/SkiaGeometry.cs 保持同一口径）。

using System;
using SkiaSharp;

namespace WpfGfx.Linux.Interop
{
    public static unsafe partial class MilNative
    {
        // ==================================================================
        //  弧 → 贝塞尔
        // ==================================================================

        /// <summary>
        /// SVG 端点参数化椭圆弧 → 最多 4 段三次贝塞尔（上游 geometry/utils.cpp:503）。
        ///
        /// pPt 必须能容纳 12 个 MilPointD。返回段数：-1 退化成点；0 退化成直线
        /// （此时 pPt[0] 是终点）；1..4 为贝塞尔段数，每段 3 个点
        /// （控制点1、控制点2、终点），共 3×cPieces 个点。
        /// 上游托管调用方 PresentationCore/System/Windows/Media/ArcSegment.cs:73
        /// 正是按 points[3*i]、points[3*i+1]、points[3*i+2] 取用的。
        /// </summary>
        public static void MilUtility_ArcToBezier(
            MilPointD ptStart,
            MilSizeD rRadii,
            double rRotation,
            bool fLargeArc,
            MilSweepDirection fSweepUp,
            MilPointD ptEnd,
            double* pMatrix,
            MilPointD* pPt,
            out int cPieces)
        {
            cPieces = MilGeometryEngine.ArcToBezier(
                ptStart, rRadii, rRotation,
                fLargeArc, fSweepUp == MilSweepDirection.Clockwise, ptEnd,
                pMatrix, pPt);
        }

        // ==================================================================
        //  包围盒
        // ==================================================================

        /// <summary>
        /// 路径的紧包围盒（可带笔 = 描边包围盒）。
        ///
        /// 变换顺序按上游 geometry_api.cpp:535 的契约：
        ///   · pGeometryMatrix 只作用于几何，**不作用于笔**
        ///   · pWorldMatrix 同时作用于笔与几何
        /// 所以实现是：解析 → （有笔时）在几何空间描边外扩 → 乘 pGeometryMatrix → 乘 pWorldMatrix → 取紧包围盒。
        /// </summary>
        public static int MilUtility_PathGeometryBounds(
            MIL_PEN_DATA* pPenData,
            double* pDashArray,
            double* pWorldMatrix,
            MilFillRule fillRule,
            byte* pPathData,
            uint nSize,
            double* pGeometryMatrix,
            double rTolerance,
            bool fRelative,
            bool fSkipHollows,
            MilRectD* pBounds)
        {
            if (pPathData == null || pBounds == null) return HResult.E_INVALIDARG;
            if (nSize < MilGeometryEngine.PathGeometryHeaderSize) return HResult.E_INVALIDARG;

            // 【U1c 真机对拍】fSkipHollows 是真的生效的：上游把这个参数一路传到
            // CShapeBase::GetFillBounds(rect, fFillOnly, pMatrix)，那里按
            // figure.IsFillable() 过滤（shapebase.cpp:1438）。
            // 真机 bounds_hollow_skip_true = (0,0,10,10)（只算可填充图形），
            // 初版忽略该参数给 (0,0,200,200)。
            using SKPath path = MilGeometryEngine.ParsePathData(pPathData, nSize, fillRule, fSkipHollows);
            if (path == null) return HResult.E_INVALIDARG;

            // 【U1c 真机对拍纠正：几何矩阵与笔的先后顺序】
            // 契约是"pGeometryMatrix 只作用于几何、不作用于笔"。上游把几何矩阵交给
            // PathGeometryData 的构造参数（geometry_api.cpp:583），也就是**先变换几何**，
            // 之后才带笔取紧包围盒（:585 的 GetTightBounds(rect, pen, worldMatrix, ...)）——
            // 于是笔的外扩量是**变换后坐标系里的绝对量**，不跟着几何矩阵缩放。
            // 初版反了：先按笔外扩、再乘几何矩阵，外扩量被一起放大。
            // 真机证据 bounds_rect_geom_matrix_scale2：几何矩形 0..100 × 2 = 0..200，
            // 笔宽 2 → (-1,-1,201,101)（真机）vs 初版 (-2,-2,202,102)。
            using SKPath geometrySpace = ApplyMatrices(path, pGeometryMatrix, null, fillRule);

            using SKPath stroked = pPenData != null
                ? MilGeometryEngine.Widen(geometrySpace, pPenData, pDashArray)
                : null;
            SKPath source = stroked ?? geometrySpace;

            using SKPath transformed = ApplyMatrices(source, pWorldMatrix, null, fillRule);

            MilRectD bounds = TightBounds(transformed, rTolerance, fRelative);
            *pBounds = bounds;

            return HResult.S_OK;
        }

        /// <summary>
        /// 多边形（点数组 + 段类型数组）的紧包围盒。
        /// 段类型字节取 MILCoreSegFlags：SegTypeLine 每段 1 点、SegTypeBezier 每段 3 点。
        /// </summary>
        public static int MilUtility_PolygonBounds(
            double* pWorldMatrix,
            MIL_PEN_DATA* pPenData,
            double* pDashArray,
            MilPointD* pPoints,
            byte* pTypes,
            uint pointCount,
            uint segmentCount,
            double* pGeometryMatrix,
            double rTolerance,
            bool fRelative,
            bool fSkipHollows,
            MilRectD* pBounds)
        {
            if (pPoints == null || pTypes == null || pBounds == null) return HResult.E_INVALIDARG;
            if (pointCount == 0 || segmentCount == 0) return HResult.E_INVALIDARG;

            using SKPath path = BuildPolygonPath(pPoints, pTypes, pointCount, segmentCount);
            if (path == null) return HResult.E_INVALIDARG;

            // 同 PathGeometryBounds：几何矩阵在"加图形"时就作用（geometry_api.cpp:519），
            // 之后才带笔取紧包围盒 —— 笔的外扩量不随几何矩阵缩放。
            using SKPath geometrySpace = ApplyMatrices(path, pGeometryMatrix, null, MilFillRule.EvenOdd);

            using SKPath stroked = pPenData != null
                ? MilGeometryEngine.Widen(geometrySpace, pPenData, pDashArray)
                : null;
            SKPath source = stroked ?? geometrySpace;

            using SKPath transformed = ApplyMatrices(source, pWorldMatrix, null, MilFillRule.EvenOdd);

            *pBounds = TightBounds(transformed, rTolerance, fRelative);
            _ = fSkipHollows;
            return HResult.S_OK;
        }

        // ==================================================================
        //  展平 / 外扩 / 消解 / 布尔
        // ==================================================================

        /// <summary>
        /// 展平成折线（曲线 → 直线段），逐图形回调。
        /// outFillRule 回填输入填充规则（上游 geometry_api.cpp:421 也是原样回填）。
        /// </summary>
        public static int MilUtility_PathGeometryFlatten(
            double* pMatrix,
            MilFillRule fillRule,
            byte* pPathData,
            uint nSize,
            double rTolerance,
            bool fRelative,
            MilAddFigureCallback addFigureCallback,
            out MilFillRule resultFillRule)
        {
            resultFillRule = fillRule;
            if (addFigureCallback == null) return HResult.E_INVALIDARG;
            if (pPathData == null || nSize < MilGeometryEngine.PathGeometryHeaderSize)
                return HResult.E_INVALIDARG;

            using SKPath path = MilGeometryEngine.ParsePathData(pPathData, nSize, fillRule);
            if (path == null) return HResult.E_INVALIDARG;

            using SKPath transformed = ApplyMatrices(path, pMatrix, null, fillRule);

            MilRectD bounds = MilGeometryEngine.Bounds(MilGeometryEngine.Flatten(transformed, 0.1));
            double tolerance = MilGeometryEngine.ResolveTolerance(rTolerance, fRelative, bounds);

            var contours = MilGeometryEngine.Flatten(transformed, tolerance);
            EmitContoursAsLineFigures(contours, addFigureCallback);
            return HResult.S_OK;
        }

        /// <summary>
        /// 按填充规则消解为空心轮廓（上游 Outline：把自交/空心图形解算成规范区域），
        /// 结果按**曲线段**回调（保留贝塞尔，不展平）。
        /// </summary>
        public static int MilUtility_PathGeometryOutline(
            double* pMatrix,
            MilFillRule fillRule,
            byte* pPathData,
            uint nSize,
            double rTolerance,
            bool fRelative,
            MilAddFigureCallback addFigureCallback,
            out MilFillRule outlinedFillRule)
        {
            outlinedFillRule = fillRule;
            if (addFigureCallback == null) return HResult.E_INVALIDARG;
            if (pPathData == null || nSize < MilGeometryEngine.PathGeometryHeaderSize)
                return HResult.E_INVALIDARG;

            using SKPath path = MilGeometryEngine.ParsePathData(pPathData, nSize, fillRule);
            if (path == null) return HResult.E_INVALIDARG;

            using SKPath transformed = ApplyMatrices(path, pMatrix, null, fillRule);

            // 用"几何自身的填充解算"得到规范轮廓（填充规则的语义在这一步落地）。
            using SKPath outlined = MilGeometryEngine.Outline(transformed);
            if (outlined == null) return HResult.E_FAIL;

            outlinedFillRule = FillRuleOf(outlined);
            EmitPathFigures(outlined, addFigureCallback);

            // rTolerance/fRelative：上游用于曲线展平精度；本实现保留曲线，故不参与。
            _ = rTolerance;
            _ = fRelative;
            return HResult.S_OK;
        }

        /// <summary>
        /// 用笔把路径外扩成填充轮廓（Widen = "描边几何"）。
        /// 虚线（pDashArray + pen.DashArraySize，后者是**字节数**）在这里生效。
        ///
        /// 【U1c 真机对拍的纠正】
        ///   1. 笔宽 0 → S_OK + 0 个图形（真机 widen_zero_thickness）。
        ///   2. 笔宽为负按绝对值处理（真机 widen_negative_thickness 与 +2 同形）。
        ///
        /// 【outFillRule 是"上游未定义输出"】geometry_api.cpp:151-217 的 Widen 里
        /// **没有** `*pOutFillRule = ...`，而 Outline(:254)/Flatten(:452)/Combine(:397)
        /// 都有。用哨兵验证过：真机原样回吐调用方栈上的未初始化值。
        /// 本实现给出**确定性**的输入填充规则（比"照抄未初始化内存"更有用），
        /// 对拍时该键按"不可比"处理（见 tools/GeometryOracle 的比较器）。
        /// </summary>
        public static int MilUtility_PathGeometryWiden(
            MIL_PEN_DATA* pPenData,
            double* pDashArray,
            double* pMatrix,
            MilFillRule fillRule,
            byte* pPathData,
            uint nSize,
            double rTolerance,
            bool fRelative,
            MilAddFigureCallback addFigureCallback,
            out MilFillRule widenedFillRule)
        {
            widenedFillRule = fillRule;
            if (addFigureCallback == null) return HResult.E_INVALIDARG;
            if (pPathData == null || nSize < MilGeometryEngine.PathGeometryHeaderSize)
                return HResult.E_INVALIDARG;

            using SKPath path = MilGeometryEngine.ParsePathData(pPathData, nSize, fillRule);
            if (path == null) return HResult.E_INVALIDARG;

            // 【U1c 真机对拍纠正：矩阵先作用于几何，再描边】
            // 契约写明 "pMatrix applied to the geometry but not to the pen"
            //（geometry_api.cpp:154），上游把矩阵交给 PathGeometryData 构造参数、
            // 之后才 WidenToShape —— 于是**虚线相位也落在变换后的坐标系里**。
            // 真机 widen_dash_scale_matrix：几何 0..50 被 ×2 成 0..100、虚线周期仍是
            // 8/4 → 9 段；初版先描边后变换 → 5 段（周期被一起放大）。
            using SKPath geometrySpace = ApplyMatrices(path, pMatrix, null, fillRule);

            using SKPath widened = MilGeometryEngine.Widen(geometrySpace, pPenData, pDashArray);
            if (widened == null) return HResult.E_FAIL;

            EmitPathFigures(widened, addFigureCallback);

            _ = rTolerance;
            _ = fRelative;
            return HResult.S_OK;
        }

        /// <summary>
        /// 两条路径的布尔运算（Union / Intersect / Xor / Exclude）。
        /// 两个矩阵分别作用于各自的几何；结果填充规则由实际结果回填。
        /// </summary>
        public static int MilUtility_PathGeometryCombine(
            double* pMatrix,
            double* pMatrix1,
            MilFillRule fillRule1,
            byte* pPathData1,
            uint nSize1,
            double* pMatrix2,
            MilFillRule fillRule2,
            byte* pPathData2,
            uint nSize2,
            double rTolerance,
            bool fRelative,
            MilAddFigureCallback addFigureCallback,
            MilGeometryCombineMode combineMode,
            out MilFillRule resultFillRule)
        {
            resultFillRule = fillRule1;
            if (addFigureCallback == null) return HResult.E_INVALIDARG;
            if (pPathData1 == null || pPathData2 == null) return HResult.E_INVALIDARG;

            using SKPath a = MilGeometryEngine.ParsePathData(pPathData1, nSize1, fillRule1);
            using SKPath b = MilGeometryEngine.ParsePathData(pPathData2, nSize2, fillRule2);
            if (a == null || b == null) return HResult.E_INVALIDARG;

            using SKPath ta = ApplyMatrices(a, pMatrix1, pMatrix, fillRule1);
            using SKPath tb = ApplyMatrices(b, pMatrix2, pMatrix, fillRule2);

            using SKPath combined = MilGeometryEngine.Combine(ta, tb, combineMode, fillRule1);
            if (combined == null) return HResult.E_FAIL;

            // 【U1c 真机对拍】上游 `*pOutFillRule = combinedShape.GetFillMode();`
            //（geometry_api.cpp:397），而 combinedShape 是新建的 CShape，
            // 默认填充模式恒为 Winding（shape.h:57 `m_eFillMode(MilFillMode::Winding)`）
            // → 真机 22/22 个 combine 用例全部回 1，与输入 fillRule 无关。
            resultFillRule = MilFillRule.Nonzero;
            EmitPathFigures(combined, addFigureCallback);

            _ = rTolerance;
            _ = fRelative;
            return HResult.S_OK;
        }

        // ==================================================================
        //  命中测试 / 面积 / 长度分数
        // ==================================================================

        /// <summary>
        /// 路径命中测试。pPenData 为空 → 测填充；不为空 → 测描边。
        /// pDoesContain 回传 1/0（上游是 BOOL）。
        /// </summary>
        public static int MilUtility_PathGeometryHitTest(
            double* pMatrix,
            MIL_PEN_DATA* pPenData,
            double* pDashArray,
            MilFillRule fillRule,
            byte* pPathData,
            uint nSize,
            double rTolerance,
            bool fRelative,
            MilPointD* pHitPoint,
            out int pDoesContain)
        {
            pDoesContain = 0;
            if (pHitPoint == null) return HResult.E_INVALIDARG;
            if (pPathData == null || nSize < MilGeometryEngine.PathGeometryHeaderSize)
                return HResult.E_INVALIDARG;

            using SKPath path = MilGeometryEngine.ParsePathData(pPathData, nSize, fillRule);
            if (path == null) return HResult.E_INVALIDARG;

            pDoesContain = HitTestPath(path, pMatrix, pPenData, pDashArray, fillRule,
                rTolerance, fRelative, *pHitPoint) ? 1 : 0;
            return HResult.S_OK;
        }

        /// <summary>多边形命中测试（点数组 + 段类型数组）。</summary>
        public static int MilUtility_PolygonHitTest(
            double* pGeometryMatrix,
            MIL_PEN_DATA* pPenData,
            double* pDashArray,
            MilPointD* pPoints,
            byte* pTypes,
            uint cPoints,
            uint cSegments,
            double rTolerance,
            bool fRelative,
            MilPointD* pHitPoint,
            out int pDoesContain)
        {
            pDoesContain = 0;
            if (pPoints == null || pTypes == null || pHitPoint == null) return HResult.E_INVALIDARG;
            if (cPoints == 0 || cSegments == 0) return HResult.E_INVALIDARG;

            using SKPath path = BuildPolygonPath(pPoints, pTypes, cPoints, cSegments);
            if (path == null) return HResult.E_INVALIDARG;

            pDoesContain = HitTestPath(path, pGeometryMatrix, pPenData, pDashArray,
                MilFillRule.EvenOdd, rTolerance, fRelative, *pHitPoint) ? 1 : 0;
            return HResult.S_OK;
        }

        /// <summary>
        /// 两条路径的交集细节（上游 System.Windows.Media.Geometry.FillContainsWithDetail）。
        ///
        /// 【U1c 真机对拍纠正：极性与"完全相同"两处】
        /// 原生写出去的是 MilPathsRelation::Enum，与 IntersectionDetail **数值一一对应**
        /// （wgx_render_types_generated.h:75 `Unknown=0, Disjoint=1, IsContained=2,
        /// Contains=3, Overlap=4`，全树没有任何映射代码）。判定视角是**参数 1**：
        ///   1 (Empty)         两形状无交集（原生 Disjoint）
        ///   2 (FullyInside)   **path1 ⊆ path2**（IsContained；CRelation::GetResult 先查 m_fInside[0]）
        ///   3 (FullyContains) **path1 ⊇ path2**（Contains）
        ///   4 (Intersects)    部分相交；(Boolean.cpp:1220 的早退) **完全相同也回 4**
        /// 注意枚举的 XML 文档是按托管 API 的"target=this=参数1"写的，所以
        /// "target 完全装下 hit geometry" 对应 3。本工程初版把 2/3 判反了，
        /// 且把"两个差集都为空"（完全相同）判成 FullyInside，真机给 4。
        /// 真机证据：hitpath_small_inside_big=(大,小)→3、
        /// hitpath_big_contains_small=(小,大)→2、hitpath_identical→4、
        /// hitpath_edge_touch→4、hitpath_disjoint→1。
        /// </summary>
        public static int MilUtility_PathGeometryHitTestPathGeometry(
            double* pMatrix1,
            MilFillRule fillRule1,
            byte* pPathData1,
            uint nSize1,
            double* pMatrix2,
            MilFillRule fillRule2,
            byte* pPathData2,
            uint nSize2,
            double rTolerance,
            bool fRelative,
            MilIntersectionDetail* pDetail)
        {
            if (pDetail == null) return HResult.E_INVALIDARG;
            if (pPathData1 == null || pPathData2 == null) return HResult.E_INVALIDARG;

            *pDetail = MilIntersectionDetail.NotCalculated;

            using SKPath a = MilGeometryEngine.ParsePathData(pPathData1, nSize1, fillRule1);
            using SKPath b = MilGeometryEngine.ParsePathData(pPathData2, nSize2, fillRule2);
            if (a == null || b == null) return HResult.E_INVALIDARG;

            using SKPath ta = ApplyMatrices(a, pMatrix1, null, fillRule1);
            using SKPath tb = ApplyMatrices(b, pMatrix2, null, fillRule2);

            using SKPath intersection = MilGeometryEngine.Combine(ta, tb, MilGeometryCombineMode.Intersect, fillRule1);
            if (intersection == null) return HResult.E_FAIL;

            if (IsEmptyPath(intersection))
            {
                *pDetail = MilIntersectionDetail.Empty;      // 原生 Disjoint = 1
                return HResult.S_OK;
            }

            // path1 减 path2：为空说明 path1 整个在 path2 里。
            using SKPath onlyA = MilGeometryEngine.Combine(ta, tb, MilGeometryCombineMode.Exclude, fillRule1);
            if (onlyA == null) return HResult.E_FAIL;
            bool aInsideB = IsEmptyPath(onlyA);

            // path2 减 path1：为空说明 path2 整个在 path1 里。
            using SKPath onlyB = MilGeometryEngine.Combine(tb, ta, MilGeometryCombineMode.Exclude, fillRule2);
            if (onlyB == null) return HResult.E_FAIL;
            bool bInsideA = IsEmptyPath(onlyB);

            if (aInsideB && bInsideA)
            {
                // 两个差集都为空 = 形状相同。原生没有"相等"这个关系，
                // 重合边界会被判成 Overlap（真机 hitpath_identical = 4）。
                *pDetail = MilIntersectionDetail.Intersects;
            }
            else if (bInsideA)
            {
                *pDetail = MilIntersectionDetail.FullyContains;   // path1 ⊇ path2 → 3
            }
            else if (aInsideB)
            {
                *pDetail = MilIntersectionDetail.FullyInside;     // path1 ⊆ path2 → 2
            }
            else
            {
                *pDetail = MilIntersectionDetail.Intersects;      // 部分相交 → 4
            }

            _ = rTolerance;
            _ = fRelative;
            return HResult.S_OK;
        }

        /// <summary>填充区域面积（梯形分解，支持 EvenOdd / Nonzero，自交图形也对）。</summary>
        public static int MilUtility_GeometryGetArea(
            MilFillRule fillRule,
            byte* pPathData,
            uint nSize,
            double* pMatrix,
            double rTolerance,
            bool fRelative,
            double* pArea)
        {
            if (pArea == null) return HResult.E_INVALIDARG;
            if (pPathData == null || nSize < MilGeometryEngine.PathGeometryHeaderSize)
                return HResult.E_INVALIDARG;

            *pArea = 0.0;

            using SKPath path = MilGeometryEngine.ParsePathData(pPathData, nSize, fillRule);
            if (path == null) return HResult.E_INVALIDARG;

            using SKPath transformed = ApplyMatrices(path, pMatrix, null, fillRule);

            MilRectD bounds = MilGeometryEngine.Bounds(MilGeometryEngine.Flatten(transformed, 0.1));
            double tolerance = MilGeometryEngine.ResolveTolerance(rTolerance, fRelative, bounds);

            *pArea = MilGeometryEngine.Area(MilGeometryEngine.Flatten(transformed, tolerance), fillRule);
            return HResult.S_OK;
        }

        /// <summary>
        /// 按弧长比例取路径上的点与切线方向（rFraction ∈ [0,1]）。
        /// 用 SKPathMeasure：Length 是路径总长，GetPositionAndTangent 给点与单位切线。
        /// 空路径（长度 0）返回 E_INVALIDARG，点与切线都是 (0,0)。
        /// </summary>
        public static int MilUtility_GetPointAtLengthFraction(
            double* pMatrix,
            MilFillRule fillRule,
            byte* pPathData,
            uint nSize,
            double rFraction,
            out MilPointD pt,
            out MilPointD vecTangent)
        {
            pt = new MilPointD(0, 0);
            vecTangent = new MilPointD(0, 0);

            if (pPathData == null || nSize < MilGeometryEngine.PathGeometryHeaderSize)
                return HResult.E_INVALIDARG;

            using SKPath path = MilGeometryEngine.ParsePathData(pPathData, nSize, fillRule);
            if (path == null) return HResult.E_INVALIDARG;

            using SKPath transformed = ApplyMatrices(path, pMatrix, null, fillRule);

            using var measure = new SKPathMeasure(transformed, false, 1f);
            float length = measure.Length;
            if (!(length > 0f)) return HResult.E_INVALIDARG;

            float distance = (float)(Math.Clamp(rFraction, 0.0, 1.0) * length);
            if (!measure.GetPositionAndTangent(distance, out SKPoint position, out SKPoint tangent))
                return HResult.E_FAIL;

            pt = new MilPointD(position.X, position.Y);
            vecTangent = new MilPointD(tangent.X, tangent.Y);
            return HResult.S_OK;
        }

        // ==================================================================
        //  TileBrush 映射
        // ==================================================================

        /// <summary>
        /// TileBrush 的 Viewport/Viewbox 绝对化 + Content→World 矩阵（无 HRESULT，void）。
        /// viewport/viewbox 是 in-out：进来是用户指定值，出去是绝对单位值。
        /// brushIsEmpty 非 0 时 contentToShape 无意义（与上游一致）。
        /// </summary>
        public static void MilUtility_GetTileBrushMapping(
            D3DMATRIX* transform,
            D3DMATRIX* relativeTransform,
            MilStretch stretch,
            MilAlignmentX alignmentX,
            MilAlignmentY alignmentY,
            MilBrushMappingMode viewPortUnits,
            MilBrushMappingMode viewBoxUnits,
            MilPointAndSizeD* shapeFillBounds,
            MilPointAndSizeD* contentBounds,
            ref MilPointAndSizeD viewport,
            ref MilPointAndSizeD viewbox,
            out D3DMATRIX contentToShape,
            out int brushIsEmpty)
        {
            MilGeometryEngine.GetTileBrushMapping(
                transform, relativeTransform, stretch, alignmentX, alignmentY,
                viewPortUnits, viewBoxUnits, shapeFillBounds, contentBounds,
                ref viewport, ref viewbox, out contentToShape, out brushIsEmpty);
        }

        // ==================================================================
        //  像素缓冲按位拷贝（上游 WpfGfx/core/common/exports.cpp:291 的逐行移植）
        // ==================================================================

        /// <summary>
        /// 把输入缓冲的 height 行、每行 copyWidthInBits 位，按位拷到输出缓冲。
        /// 支持子字节像素格式（位偏移 0..7）。校验顺序与失败码逐条对齐上游：
        ///   height/copyWidth 为 0 → 直接 S_OK（什么都不做）
        ///   位偏移 > 7 / stride 太小 / buffer 太小 → E_INVALIDARG
        /// 上游用 PreserveSig=false 声明（失败会抛异常），托管适配层需要把
        /// 这里的 HRESULT 转成异常——这一点写在报告里，避免接线时漏掉。
        /// </summary>
        public static int MilUtility_CopyPixelBuffer(
            byte* pOutputBuffer,
            uint outputBufferSize,
            uint outputBufferStride,
            uint outputBufferOffsetInBits,
            byte* pInputBuffer,
            uint inputBufferSize,
            uint inputBufferStride,
            uint inputBufferOffsetInBits,
            uint height,
            uint copyWidthInBits)
        {
            if (height == 0 || copyWidthInBits == 0) return HResult.S_OK;   // nothing to do

            if (pOutputBuffer == null || pInputBuffer == null) return HResult.E_INVALIDARG;
            if (outputBufferOffsetInBits > 7 || inputBufferOffsetInBits > 7) return HResult.E_INVALIDARG;

            // 输出侧
            if (!TryAddUInt(outputBufferOffsetInBits, copyWidthInBits, out uint minOutStrideBits))
                return HResult.E_INVALIDARG;
            uint minOutStride = BitsToBytes(minOutStrideBits);
            if (outputBufferStride < minOutStride) return HResult.E_INVALIDARG;

            if (!TryMulUInt(outputBufferStride, height - 1, out uint minOutSize)) return HResult.E_INVALIDARG;
            if (!TryAddUInt(minOutSize, minOutStride, out minOutSize)) return HResult.E_INVALIDARG;
            if (outputBufferSize < minOutSize) return HResult.E_INVALIDARG;

            // 输入侧
            if (!TryAddUInt(inputBufferOffsetInBits, copyWidthInBits, out uint minInStrideBits))
                return HResult.E_INVALIDARG;
            uint minInStride = BitsToBytes(minInStrideBits);
            if (inputBufferStride < minInStride) return HResult.E_INVALIDARG;

            if (!TryMulUInt(inputBufferStride, height - 1, out uint minInSize)) return HResult.E_INVALIDARG;
            if (!TryAddUInt(minInSize, minInStride, out minInSize)) return HResult.E_INVALIDARG;
            if (inputBufferSize < minInSize) return HResult.E_INVALIDARG;

            if (outputBufferOffsetInBits != inputBufferOffsetInBits)
            {
                CopyUnalignedPixelBuffer(
                    pOutputBuffer, outputBufferStride, outputBufferOffsetInBits,
                    pInputBuffer, inputBufferStride, inputBufferOffsetInBits,
                    height, copyWidthInBits);
                return HResult.S_OK;
            }

            // 两侧位偏移相同：按上游 common/exports.cpp:398 起的三段式逐行拷贝。
            byte* src = pInputBuffer;
            byte* dst = pOutputBuffer;
            if (inputBufferOffsetInBits == 0 && copyWidthInBits % 8 == 0)
            {
                if (minInStride == inputBufferStride && inputBufferStride == outputBufferStride)
                {
                    // 快路径：整行整块，一次大拷贝
                    Buffer.MemoryCopy(src, dst, (long)inputBufferStride * height, (long)inputBufferStride * height);
                }
                else
                {
                    for (uint i = 0; i < height; i++)
                    {
                        Buffer.MemoryCopy(src, dst, minInStride, minInStride);
                        dst += outputBufferStride;
                        src += inputBufferStride;
                    }
                }
            }
            else if (minInStride == 1)
            {
                // 首字节就是末字节：双重掩码，只改该字节里被覆盖的位。
                byte mask = GetOffsetMask(inputBufferOffsetInBits, copyWidthInBits);
                for (uint i = 0; i < height; i++)
                {
                    dst[0] = (byte)((dst[0] & ~mask) | (src[0] & mask));
                    dst += outputBufferStride;
                    src += inputBufferStride;
                }
            }
            else
            {
                bool firstByteIsWhole = inputBufferOffsetInBits == 0;
                bool finalByteIsWhole = minInStrideBits % 8 == 0;
                uint wholeBytesPerRow = minInStride
                    - (finalByteIsWhole ? 0u : 1u)
                    - (firstByteIsWhole ? 0u : 1u);
                uint finalByteOffset = minInStride - 1;

                for (uint i = 0; i < height; i++)
                {
                    if (!firstByteIsWhole)
                    {
                        byte mask = GetOffsetMask(inputBufferOffsetInBits, 8 - inputBufferOffsetInBits);
                        dst[0] = (byte)((dst[0] & ~mask) | (src[0] & mask));
                    }

                    if (wholeBytesPerRow > 0)
                    {
                        uint firstByteOffset = firstByteIsWhole ? 0u : 1u;
                        Buffer.MemoryCopy(src + firstByteOffset, dst + firstByteOffset,
                            wholeBytesPerRow, wholeBytesPerRow);
                    }

                    if (!finalByteIsWhole)
                    {
                        byte mask = GetOffsetMask(0, minInStrideBits % 8);
                        dst[finalByteOffset] = (byte)((dst[finalByteOffset] & ~mask) | (src[finalByteOffset] & mask));
                    }

                    dst += outputBufferStride;
                    src += inputBufferStride;
                }
            }

            return HResult.S_OK;
        }

        /// <summary>3D 盒投影到 2D 的屏幕包围盒（上游 WpfGfx/core/common/exports.cpp:26）。</summary>
        public static int MIL3DCalcProjected2DBounds(
            D3DMATRIX* pFullTransform3D,
            MILRect3D* pboxBounds,
            out MilRectF prcDestRect)
        {
            prcDestRect = default;

            if (pFullTransform3D == null || pboxBounds == null) return HResult.E_INVALIDARG;

            D3DMATRIX m = *pFullTransform3D;
            MILRect3D box = *pboxBounds;

            float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;

            for (int i = 0; i < 8; i++)
            {
                float x = box.X + ((i & 1) != 0 ? box.LengthX : 0f);
                float y = box.Y + ((i & 2) != 0 ? box.LengthY : 0f);
                float z = box.Z + ((i & 4) != 0 ? box.LengthZ : 0f);

                // WPF 的 D3DMATRIX 用行向量约定（见 MILUtilities.ConvertToD3DMATRIX：
                // _41/_42 放的是二维仿射的 OffsetX/OffsetY），故 p' = p · M。
                float px = x * m.M11 + y * m.M21 + z * m.M31 + m.M41;
                float py = x * m.M12 + y * m.M22 + z * m.M32 + m.M42;
                float pw = x * m.M14 + y * m.M24 + z * m.M34 + m.M44;

                if (pw != 0f && pw != 1f)
                {
                    px /= pw;
                    py /= pw;
                }

                if (px < minX) minX = px;
                if (py < minY) minY = py;
                if (px > maxX) maxX = px;
                if (py > maxY) maxY = py;
            }

            prcDestRect = new MilRectF(minX, minY, maxX, maxY);
            return HResult.S_OK;
        }

        // ==================================================================
        //  内部工具
        // ==================================================================

        /// <summary>依次施加"几何矩阵 → 世界矩阵"。两个都为 null 时返回原路径的副本。</summary>
        private static SKPath ApplyMatrices(SKPath path, double* geometryMatrix, double* worldMatrix, MilFillRule fillRule)
        {
            SKMatrix geometry = MilGeometryEngine.ToSkMatrix(geometryMatrix);
            SKMatrix world = MilGeometryEngine.ToSkMatrix(worldMatrix);

            var result = new SKPath { FillType = MilGeometryEngine.FillTypeOf(fillRule) };
            path.Transform(geometry, result);

            if (!world.IsIdentity)
            {
                var second = new SKPath { FillType = MilGeometryEngine.FillTypeOf(fillRule) };
                result.Transform(world, second);
                result.Dispose();
                return second;
            }

            return result;
        }

        /// <summary>紧包围盒。rTolerance/fRelative 只影响曲线展平的细腻程度（对包围盒是收敛量）。</summary>
        private static MilRectD TightBounds(SKPath path, double rTolerance, bool fRelative)
        {
            if (!path.GetTightBounds(out SKRect rect)) return MilRectD.Empty;
            _ = rTolerance;
            _ = fRelative;
            return new MilRectD(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }

        private static MilFillRule FillRuleOf(SKPath path) =>
            path.FillType == SKPathFillType.Winding ? MilFillRule.Nonzero : MilFillRule.EvenOdd;

        /// <summary>路径是否描述空区域（无墨迹）。</summary>
        private static bool IsEmptyPath(SKPath path)
        {
            if (path == null) return true;
            if (path.IsEmpty) return true;

            SKRect bounds = path.Bounds;
            return bounds.Width <= 0f || bounds.Height <= 0f;
        }

        /// <summary>
        /// 点数组 + 段类型数组 → SKPath。
        /// pTypes 的取值是 MILCoreSegFlags：SegTypeLine=1（每段 1 点）、
        /// SegTypeBezier=2（每段 3 点）；可带 SegIsAGap（抬笔）与 SegSmoothJoin。
        /// </summary>
        private static SKPath BuildPolygonPath(MilPointD* pPoints, byte* pTypes, uint pointCount, uint segmentCount)
        {
            var path = new SKPath();
            uint pointIndex = 0;

            // pPoints[0] 是起点
            path.MoveTo((float)pPoints[0].X, (float)pPoints[0].Y);
            pointIndex = 1;

            for (uint s = 0; s < segmentCount; s++)
            {
                byte type = pTypes[s];
                bool isGap = (type & (byte)MilCoreSegFlags.SegIsAGap) != 0;
                byte kind = (byte)(type & (byte)MilCoreSegFlags.SegTypeMask);

                if (kind == (byte)MilCoreSegFlags.SegTypeBezier)
                {
                    if (pointIndex + 2 >= pointCount && pointIndex + 3 > pointCount)
                    {
                        path.Dispose();
                        return null;
                    }
                    var c1 = new SKPoint((float)pPoints[pointIndex].X, (float)pPoints[pointIndex].Y);
                    var c2 = new SKPoint((float)pPoints[pointIndex + 1].X, (float)pPoints[pointIndex + 1].Y);
                    var end = new SKPoint((float)pPoints[pointIndex + 2].X, (float)pPoints[pointIndex + 2].Y);
                    if (isGap) path.MoveTo(end);
                    else path.CubicTo(c1, c2, end);
                    pointIndex += 3;
                }
                else
                {
                    if (pointIndex >= pointCount)
                    {
                        path.Dispose();
                        return null;
                    }
                    var end = new SKPoint((float)pPoints[pointIndex].X, (float)pPoints[pointIndex].Y);
                    if (isGap) path.MoveTo(end);
                    else path.LineTo(end);
                    pointIndex += 1;
                }
            }

            return path;
        }

        /// <summary>展平后的轮廓 → 只含直线段的图形回调。</summary>
        private static void EmitContoursAsLineFigures(
            System.Collections.Generic.List<MilContour> contours, MilAddFigureCallback callback)
        {
            foreach (MilContour contour in contours)
            {
                int count = contour.Count;
                if (count < 2) continue;

                var points = new MilPointF[count];
                for (int i = 0; i < count; i++)
                    points[i] = new MilPointF((float)contour.Points[i].X, (float)contour.Points[i].Y);

                int segmentCount = count - 1;
                var types = new byte[segmentCount];
                for (int i = 0; i < segmentCount; i++) types[i] = (byte)MilCoreSegFlags.SegTypeLine;

                fixed (MilPointF* pPoints = points)
                fixed (byte* pTypes = types)
                {
                    // 展平结果一律是"可填充图形"：能进到这里的轮廓都来自 MIL_PATHGEOMETRY，
                    // 那份数据结构里的图形本来就是填充/描边都要用的几何。IsClosed 照实回传。
                    callback(isFilled: true, isClosed: contour.IsClosed,
                             pPoints, (uint)count, pTypes, (uint)segmentCount);
                }
            }
        }

        /// <summary>
        /// 保留曲线的图形回调（Outline / Widen / Combine 用）。
        ///
        /// 【U1c 真机对拍纠正：闭合图形的编码】
        /// 上游发射闭合图形时把"回到起点"当成一条**普通段**：
        /// 点表末尾就是起点、类型表里**没有** SegClosed(0x10) 位，闭合语义只由回调的
        /// isClosed 参数表达。真机证据（三条导出、9 个用例）：
        ///   combine_overlap_intersect → 5 点 / 4 个 Line 类型（win）vs 初版 4 点 / 末位 17
        ///   widen_open_line_pen2      → 5 点 / types=[1,1,1,1]（win）vs [1,1,1,17]
        ///   combine_overlap_union     → 9 点 / 8 个 Line 类型（win）vs 末位 17
        /// 所以这里：不盖 SegClosed 位；闭合且末点没回到起点时，补一个普通 Line 段到起点。
        /// </summary>
        private static void EmitPathFigures(SKPath path, MilAddFigureCallback callback)
        {
            System.Collections.Generic.List<MilFigureData> figures =
                MilGeometryEngine.CollectFigures(path, stampSegClosedOnLast: false, out _);

            foreach (MilFigureData figure in figures)
            {
                if (figure.Types.Count == 0) continue;

                // 闭合轮廓的收口段：上游是显式一段，本实现补出来。
                if (figure.IsClosed && !EndsAtStart(figure))
                {
                    figure.Types.Add((byte)MilCoreSegFlags.SegTypeLine);
                    figure.Points.Add(figure.StartPoint);
                }

                int pointCount = figure.Points.Count + 1;   // 含起点
                var points = new MilPointF[pointCount];
                points[0] = figure.StartPoint;
                for (int i = 0; i < figure.Points.Count; i++) points[i + 1] = figure.Points[i];

                byte[] types = figure.Types.ToArray();

                fixed (MilPointF* pPoints = points)
                fixed (byte* pTypes = types)
                {
                    callback(figure.IsFilled, figure.IsClosed, pPoints, (uint)pointCount, pTypes, (uint)types.Length);
                }
            }
        }

        /// <summary>
        /// 命中测试的"近边界"判定，逐条对齐上游：
        ///   1. 阈值先绝对化（CShapeBase::GetAbsoluteTolerance, shapebase.cpp:1561）：
        ///        fRelative  → max(rThreshold, FUZZ_DOUBLE) * 松包围盒最大边长
        ///        绝对       → max(rThreshold, 松包围盒最大边长 * FUZZ_DOUBLE)
        ///      FUZZ_DOUBLE = 1e-12。**没有"容差 0 就取 0.1"这回事**（初版自作主张的兜底）。
        ///   2. 平方阈值下限 SQ_LENGTH_FUZZ = 1e-4（FigureTask.cpp:119-123）：
        ///      即无论阈值多小，0.01 以内的点都算命中。
        ///   3. 比较是**严格小于**（FigureTask.cpp:209 与 :252 都是 `&lt;`），
        ///      恰好等于阈值的点不算命中。真机 hit_relative_tol：距离 5.0、阈值 5.0 → 不命中，
        ///      初版用 `&lt;=` 给成命中。
        ///   4. 先判"近边界"（命中），再判绕数（figure.IsFillable 的图形才参与）。
        /// </summary>
        /// <summary>图形最后一个点是否就是起点（闭合轮廓已经"自带收口"）。</summary>
        private static bool EndsAtStart(MilFigureData figure)
        {
            if (figure.Points.Count == 0) return true;
            MilPointF last = figure.Points[figure.Points.Count - 1];
            return last.X == figure.StartPoint.X && last.Y == figure.StartPoint.Y;
        }

        private static bool HitTestPath(
            SKPath path, double* matrix, MIL_PEN_DATA* pPenData, double* pDashArray,
            MilFillRule fillRule, double threshold, bool relative, MilPointD hitPoint)
        {
            using SKPath transformed = matrix == null
                ? ClonePath(path, fillRule)
                : ApplyMatrices(path, matrix, null, fillRule);

            SKPath target = transformed;
            SKPath stroked = null;
            try
            {
                if (pPenData != null)
                {
                    stroked = MilGeometryEngine.Widen(transformed, pPenData, pDashArray);
                    if (stroked != null) target = stroked;
                }

                var contours = MilGeometryEngine.Flatten(target, 0.1);
                MilRectD bounds = MilGeometryEngine.Bounds(contours);

                const double FuzzDouble = 1e-12;
                double extent = Math.Max(bounds.Width, bounds.Height);
                double absolute = relative
                    ? Math.Max(threshold, FuzzDouble) * extent
                    : Math.Max(threshold, extent * FuzzDouble);

                // SQ_LENGTH_FUZZ = (1e-2)^2
                double near = Math.Max(absolute, 1e-2);

                if (MilGeometryEngine.Contains(contours, hitPoint.X, hitPoint.Y, fillRule)) return true;

                double d = DistanceToContours(contours, hitPoint);
                return d < near;
            }
            finally
            {
                stroked?.Dispose();
            }
        }

        private static SKPath ClonePath(SKPath path, MilFillRule fillRule)
        {
            var clone = new SKPath { FillType = MilGeometryEngine.FillTypeOf(fillRule) };
            clone.AddPath(path);
            return clone;
        }

        private static double DistanceToContours(
            System.Collections.Generic.List<MilContour> contours, MilPointD p)
        {
            double best = double.PositiveInfinity;
            foreach (MilContour c in contours)
            {
                for (int i = 0; i < c.Points.Count; i++)
                {
                    MilPointD a = c.Points[i];
                    MilPointD b = c.Points[(i + 1) % c.Points.Count];
                    double d = PointSegmentDistance(p, a, b);
                    if (d < best) best = d;
                }
            }
            return best;
        }

        private static double PointSegmentDistance(MilPointD p, MilPointD a, MilPointD b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len2 = dx * dx + dy * dy;
            if (len2 <= 1e-18)
                return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));

            double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
            t = Math.Clamp(t, 0.0, 1.0);
            double qx = a.X + t * dx, qy = a.Y + t * dy;
            return Math.Sqrt((p.X - qx) * (p.X - qx) + (p.Y - qy) * (p.Y - qy));
        }

        // ---- 无符号整数运算（复刻上游 UIntAdd/UIntMult 的溢出保护） ----

        private static bool TryAddUInt(uint a, uint b, out uint result)
        {
            ulong sum = (ulong)a + b;
            result = (uint)sum;
            return sum <= uint.MaxValue;
        }

        private static bool TryMulUInt(uint a, uint b, out uint result)
        {
            ulong product = (ulong)a * b;
            result = (uint)product;
            return product <= uint.MaxValue;
        }

        private static uint BitsToBytes(uint bits) => (bits + 7u) / 8u;

        // ---- 按位拷贝（上游 common/exports.cpp:65-266 的移植） ----

        private static byte GetOffsetMask(uint bitOffset, uint bitsToMask)
        {
            byte mask = 0xFF;
            uint maskShift = 8 - bitsToMask;
            mask = (byte)(mask >> (int)maskShift);
            mask <<= (int)(maskShift - bitOffset);
            return mask;
        }

        /// <summary>
        /// 从输入缓冲取"下一个字节"（上游 common/exports.cpp:120 的逐行移植）。
        ///
        /// ⚠️ 上游的结果约定是**靠左对齐**（"The results are left-aligned"），
        /// 而且调用方紧接着会做 <c>nextByte &gt;&gt; outputBufferOffsetInBits</c>——
        /// 两处一起才构成完整的位搬运语义。这里的实现**逐位对齐上游**，
        /// 包括它在位偏移不等时不那么直观的结果（见 MilExportTests 的
        /// CopyPixelBuffer位偏移时只改目标位：上游算出来就是 3，不是 7）。
        /// 移植时不做"看起来更对"的改写，否则与 Windows 侧的行为会分叉。
        /// </summary>
        private static byte GetNextByteFromInputBuffer(
            byte* pInput, uint inputBufferOffsetInBits, uint bitsRemainingToCopy)
        {
            if (bitsRemainingToCopy > 8) bitsRemainingToCopy = 8;

            if (inputBufferOffsetInBits == 0)
            {
                byte onlyMask = GetOffsetMask(0, bitsRemainingToCopy);
                return (byte)(pInput[0] & onlyMask);
            }

            uint bitsFromFirstByte = 8 - inputBufferOffsetInBits;

            byte mask = GetOffsetMask(inputBufferOffsetInBits, bitsFromFirstByte);
            byte nextByte = (byte)(pInput[0] & mask);
            nextByte = (byte)(nextByte << (int)inputBufferOffsetInBits);

            bitsRemainingToCopy -= bitsFromFirstByte;

            if (bitsRemainingToCopy > 0)
            {
                if (bitsRemainingToCopy + bitsFromFirstByte == 8)
                {
                    mask = (byte)~mask;
                }
                else
                {
                    mask = GetOffsetMask(0, bitsRemainingToCopy);
                }
                nextByte |= (byte)((pInput[1] & mask) >> (int)bitsFromFirstByte);
            }

            return nextByte;
        }

        private static void CopyUnalignedPixelBuffer(
            byte* pOutputBuffer, uint outputBufferStride, uint outputBufferOffsetInBits,
            byte* pInputBuffer, uint inputBufferStride, uint inputBufferOffsetInBits,
            uint height, uint copyWidthInBits)
        {
            for (uint row = 0; row < height; row++)
            {
                uint bitsRemaining = copyWidthInBits;
                byte* pIn = pInputBuffer;
                byte* pOut = pOutputBuffer;

                while (bitsRemaining > 0)
                {
                    byte nextByte = GetNextByteFromInputBuffer(pIn, inputBufferOffsetInBits, bitsRemaining);

                    if (bitsRemaining >= 8)
                    {
                        if (outputBufferOffsetInBits == 0)
                        {
                            pOut[0] = nextByte;
                        }
                        else
                        {
                            uint bitsToFirst = 8 - outputBufferOffsetInBits;
                            byte mask = GetOffsetMask(outputBufferOffsetInBits, bitsToFirst);
                            pOut[0] = (byte)((pOut[0] & ~mask) | ((nextByte >> (int)outputBufferOffsetInBits) & mask));
                            pOut[1] = (byte)((pOut[1] & mask) | ((nextByte << (int)bitsToFirst) & ~mask));
                        }
                        bitsRemaining -= 8;
                    }
                    else
                    {
                        uint relativeLastBit = outputBufferOffsetInBits + bitsRemaining;
                        if (relativeLastBit <= 8)
                        {
                            byte mask = GetOffsetMask(outputBufferOffsetInBits, bitsRemaining);
                            pOut[0] = (byte)((pOut[0] & ~mask) | ((nextByte >> (int)outputBufferOffsetInBits) & mask));
                        }
                        else
                        {
                            uint bitsToFirst = 8 - outputBufferOffsetInBits;
                            byte mask = GetOffsetMask(outputBufferOffsetInBits, bitsToFirst);
                            pOut[0] = (byte)((pOut[0] & ~mask) | ((nextByte >> (int)outputBufferOffsetInBits) & mask));

                            mask = GetOffsetMask(0, bitsRemaining - bitsToFirst);
                            pOut[1] = (byte)((pOut[1] & ~mask) | ((nextByte << (int)bitsToFirst) & mask));
                        }
                        bitsRemaining = 0;
                    }

                    pOut++;
                    pIn++;
                }

                pOutputBuffer += outputBufferStride;
                pInputBuffer += inputBufferStride;
            }
        }

    }
}
