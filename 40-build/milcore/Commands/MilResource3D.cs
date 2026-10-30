// Licensed to the .NET Foundation under one or more agreements.
//
// 3D 资源（命令字 0x57–0x6b，21 条）与 3D 视觉树（0x29–0x30，8 条）的解码结果模型。
//
// 【为什么这个文件在 Commands/ 而不是 Resources/】
//   Resources/MilResourceTable.cs 的工厂对 3D 类型一律返回占位 MilOpaqueResource，
//   Resources/ 不在本次可改范围内（另一个 agent 在改 MilChannel.cs）。
//   于是解码结果挂在一张**以资源实例为弱键**的附加表上：
//     · 不改 Resources/ 一行
//     · 资源被 Release 后表项随之回收，不泄漏
//     · 句柄复用时会拿到新的 MilResource 实例，不会读到上一任的残留状态
//
// 【这 29 条做到哪一步】
//   字段 100% 解码 + 状态落地。**不做 3D 光栅化**：M1 没有 3D 场景图/渲染后端，
//   这里的数据目前没有消费者。将来 3D 后端接入时直接读这张表。
//   分类依据见 docs/unimplemented.md §0。

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WpfGfx.Linux.Contracts;
using WpfGfx.Linux.Interop;

namespace WpfGfx.Linux.Commands
{
    /// <summary>
    /// 4×4 单精度矩阵，对应上游 D3DMATRIX（wgx_core_types.cs:963）。
    /// 64 字节 = 16 × float。名字带 D3D，实际就是纯数值矩阵，不含任何 COM/D3D 引用。
    /// 布局与上游一致：_11.._44 按行连续排列。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct MilMatrix4x4F : IEquatable<MilMatrix4x4F>
    {
        public float M11, M12, M13, M14;
        public float M21, M22, M23, M24;
        public float M31, M32, M33, M34;
        public float M41, M42, M43, M44;

        public MilMatrix4x4F(
            float m11, float m12, float m13, float m14,
            float m21, float m22, float m23, float m24,
            float m31, float m32, float m33, float m34,
            float m41, float m42, float m43, float m44)
        {
            M11 = m11; M12 = m12; M13 = m13; M14 = m14;
            M21 = m21; M22 = m22; M23 = m23; M24 = m24;
            M31 = m31; M32 = m32; M33 = m33; M34 = m34;
            M41 = m41; M42 = m42; M43 = m43; M44 = m44;
        }

        public static MilMatrix4x4F Identity => new MilMatrix4x4F(
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1);

        public bool Equals(MilMatrix4x4F o) =>
            M11 == o.M11 && M12 == o.M12 && M13 == o.M13 && M14 == o.M14 &&
            M21 == o.M21 && M22 == o.M22 && M23 == o.M23 && M24 == o.M24 &&
            M31 == o.M31 && M32 == o.M32 && M33 == o.M33 && M34 == o.M34 &&
            M41 == o.M41 && M42 == o.M42 && M43 == o.M43 && M44 == o.M44;

        public override bool Equals(object obj) => obj is MilMatrix4x4F m && Equals(m);
        public override int GetHashCode()
        {
            var hc = new HashCode();
            hc.Add(M11); hc.Add(M12); hc.Add(M13); hc.Add(M14);
            hc.Add(M21); hc.Add(M22); hc.Add(M23); hc.Add(M24);
            hc.Add(M31); hc.Add(M32); hc.Add(M33); hc.Add(M34);
            hc.Add(M41); hc.Add(M42); hc.Add(M43); hc.Add(M44);
            return hc.ToHashCode();
        }
        public override string ToString() =>
            $"[{M11} {M12} {M13} {M14} / {M21} {M22} {M23} {M24} / " +
            $"{M31} {M32} {M33} {M34} / {M41} {M42} {M43} {M44}]";
    }

    /// <summary>3D 资源解码结果的公共基类。只标记最近一次写入的命令字。</summary>
    internal abstract class MilResource3D
    {
        public MilCmd LastCommand;
    }

    // ---------------------------- 旋转 ----------------------------

    /// <summary>0x57 MilCmdAxisAngleRotation3D。</summary>
    internal sealed class MilAxisAngleRotation3D : MilResource3D
    {
        public double Angle;
        public MilPoint3F Axis;
        public DUCE.ResourceHandle HAxisAnimations;
        public DUCE.ResourceHandle HAngleAnimations;
    }

    /// <summary>0x58 MilCmdQuaternionRotation3D。</summary>
    internal sealed class MilQuaternionRotation3D : MilResource3D
    {
        public MilQuaternionF Quaternion;
        public DUCE.ResourceHandle HQuaternionAnimations;
    }

    // ---------------------------- 相机 ----------------------------

    /// <summary>0x59 MilCmdPerspectiveCamera。</summary>
    internal sealed class MilPerspectiveCamera : MilResource3D
    {
        public double NearPlaneDistance;
        public double FarPlaneDistance;
        public double FieldOfView;
        public MilPoint3F Position;
        public DUCE.ResourceHandle HTransform;
        public MilPoint3F LookDirection;
        public MilPoint3F UpDirection;
        public DUCE.ResourceHandle HNearPlaneDistanceAnimations;
        public DUCE.ResourceHandle HFarPlaneDistanceAnimations;
        public DUCE.ResourceHandle HPositionAnimations;
        public DUCE.ResourceHandle HLookDirectionAnimations;
        public DUCE.ResourceHandle HUpDirectionAnimations;
        public DUCE.ResourceHandle HFieldOfViewAnimations;
    }

    /// <summary>0x5a MilCmdOrthographicCamera。</summary>
    internal sealed class MilOrthographicCamera : MilResource3D
    {
        public double NearPlaneDistance;
        public double FarPlaneDistance;
        public double Width;
        public MilPoint3F Position;
        public DUCE.ResourceHandle HTransform;
        public MilPoint3F LookDirection;
        public MilPoint3F UpDirection;
        public DUCE.ResourceHandle HNearPlaneDistanceAnimations;
        public DUCE.ResourceHandle HFarPlaneDistanceAnimations;
        public DUCE.ResourceHandle HPositionAnimations;
        public DUCE.ResourceHandle HLookDirectionAnimations;
        public DUCE.ResourceHandle HUpDirectionAnimations;
        public DUCE.ResourceHandle HWidthAnimations;
    }

    /// <summary>0x5b MilCmdMatrixCamera。</summary>
    internal sealed class MilMatrixCamera : MilResource3D
    {
        public MilMatrix4x4F ViewMatrix;
        public MilMatrix4x4F ProjectionMatrix;
        public DUCE.ResourceHandle HTransform;
    }

    // ---------------------------- 模型 / 网格 ----------------------------

    /// <summary>0x5c MilCmdModel3DGroup（变长：ChildrenSize = 4×N 句柄）。</summary>
    internal sealed class MilModel3DGroup : MilResource3D
    {
        public DUCE.ResourceHandle HTransform;
        public readonly List<DUCE.ResourceHandle> Children = new List<DUCE.ResourceHandle>();
    }

    /// <summary>
    /// 0x62 MilCmdMeshGeometry3D（变长四段）。元素宽度来自
    /// Media3D/Generated/MeshGeometry3D.cs:230-283 的实测：
    /// Positions 12B / Normals 12B / TextureCoordinates 16B / TriangleIndices 4B。
    /// </summary>
    internal sealed class MilMeshGeometry3D : MilResource3D
    {
        public readonly List<MilPoint3F> Positions = new List<MilPoint3F>();
        public readonly List<MilPoint3F> Normals = new List<MilPoint3F>();
        public readonly List<MilPoint> TextureCoordinates = new List<MilPoint>();
        public readonly List<int> TriangleIndices = new List<int>();
    }

    /// <summary>0x61 MilCmdGeometryModel3D。</summary>
    internal sealed class MilGeometryModel3D : MilResource3D
    {
        public DUCE.ResourceHandle HTransform;
        public DUCE.ResourceHandle HGeometry;
        public DUCE.ResourceHandle HMaterial;
        public DUCE.ResourceHandle HBackMaterial;
    }

    // ---------------------------- 光照 ----------------------------

    /// <summary>0x5d MilCmdAmbientLight。</summary>
    internal sealed class MilAmbientLight : MilResource3D
    {
        public MilColorF Color;
        public DUCE.ResourceHandle HTransform;
        public DUCE.ResourceHandle HColorAnimations;
    }

    /// <summary>0x5e MilCmdDirectionalLight。</summary>
    internal sealed class MilDirectionalLight : MilResource3D
    {
        public MilColorF Color;
        public MilPoint3F Direction;
        public DUCE.ResourceHandle HTransform;
        public DUCE.ResourceHandle HColorAnimations;
        public DUCE.ResourceHandle HDirectionAnimations;
    }

    /// <summary>0x5f MilCmdPointLight。</summary>
    internal sealed class MilPointLight : MilResource3D
    {
        public MilColorF Color;
        public double Range;
        public double ConstantAttenuation;
        public double LinearAttenuation;
        public double QuadraticAttenuation;
        public MilPoint3F Position;
        public DUCE.ResourceHandle HTransform;
        public DUCE.ResourceHandle HColorAnimations;
        public DUCE.ResourceHandle HPositionAnimations;
        public DUCE.ResourceHandle HRangeAnimations;
        public DUCE.ResourceHandle HConstantAttenuationAnimations;
        public DUCE.ResourceHandle HLinearAttenuationAnimations;
        public DUCE.ResourceHandle HQuadraticAttenuationAnimations;
    }

    /// <summary>0x60 MilCmdSpotLight。</summary>
    internal sealed class MilSpotLight : MilResource3D
    {
        public MilColorF Color;
        public double Range;
        public double ConstantAttenuation;
        public double LinearAttenuation;
        public double QuadraticAttenuation;
        public double OuterConeAngle;
        public double InnerConeAngle;
        public MilPoint3F Position;
        public MilPoint3F Direction;
        public DUCE.ResourceHandle HTransform;
        public DUCE.ResourceHandle HColorAnimations;
        public DUCE.ResourceHandle HPositionAnimations;
        public DUCE.ResourceHandle HRangeAnimations;
        public DUCE.ResourceHandle HConstantAttenuationAnimations;
        public DUCE.ResourceHandle HLinearAttenuationAnimations;
        public DUCE.ResourceHandle HQuadraticAttenuationAnimations;
        public DUCE.ResourceHandle HDirectionAnimations;
        public DUCE.ResourceHandle HOuterConeAngleAnimations;
        public DUCE.ResourceHandle HInnerConeAngleAnimations;
    }

    // ---------------------------- 材质 ----------------------------

    /// <summary>0x63 MilCmdMaterialGroup（变长：ChildrenSize = 4×N 句柄）。</summary>
    internal sealed class MilMaterialGroup : MilResource3D
    {
        public readonly List<DUCE.ResourceHandle> Children = new List<DUCE.ResourceHandle>();
    }

    /// <summary>0x64 MilCmdDiffuseMaterial。</summary>
    internal sealed class MilDiffuseMaterial : MilResource3D
    {
        public MilColorF Color;
        public MilColorF AmbientColor;
        public DUCE.ResourceHandle HBrush;
    }

    /// <summary>0x65 MilCmdSpecularMaterial。</summary>
    internal sealed class MilSpecularMaterial : MilResource3D
    {
        public MilColorF Color;
        public double SpecularPower;
        public DUCE.ResourceHandle HBrush;
    }

    /// <summary>0x66 MilCmdEmissiveMaterial。</summary>
    internal sealed class MilEmissiveMaterial : MilResource3D
    {
        public MilColorF Color;
        public DUCE.ResourceHandle HBrush;
    }

    // ---------------------------- 变换 ----------------------------

    /// <summary>0x67 MilCmdTransform3DGroup（变长：ChildrenSize = 4×N 句柄）。</summary>
    internal sealed class MilTransform3DGroup : MilResource3D
    {
        public readonly List<DUCE.ResourceHandle> Children = new List<DUCE.ResourceHandle>();
    }

    /// <summary>0x68 MilCmdTranslateTransform3D。</summary>
    internal sealed class MilTranslateTransform3D : MilResource3D
    {
        public double OffsetX, OffsetY, OffsetZ;
        public DUCE.ResourceHandle HOffsetXAnimations;
        public DUCE.ResourceHandle HOffsetYAnimations;
        public DUCE.ResourceHandle HOffsetZAnimations;
    }

    /// <summary>0x69 MilCmdScaleTransform3D。</summary>
    internal sealed class MilScaleTransform3D : MilResource3D
    {
        public double ScaleX, ScaleY, ScaleZ;
        public double CenterX, CenterY, CenterZ;
        public DUCE.ResourceHandle HScaleXAnimations;
        public DUCE.ResourceHandle HScaleYAnimations;
        public DUCE.ResourceHandle HScaleZAnimations;
        public DUCE.ResourceHandle HCenterXAnimations;
        public DUCE.ResourceHandle HCenterYAnimations;
        public DUCE.ResourceHandle HCenterZAnimations;
    }

    /// <summary>0x6a MilCmdRotateTransform3D。</summary>
    internal sealed class MilRotateTransform3D : MilResource3D
    {
        public double CenterX, CenterY, CenterZ;
        public DUCE.ResourceHandle HCenterXAnimations;
        public DUCE.ResourceHandle HCenterYAnimations;
        public DUCE.ResourceHandle HCenterZAnimations;
        public DUCE.ResourceHandle HRotation;
    }

    /// <summary>0x6b MilCmdMatrixTransform3D。</summary>
    internal sealed class MilMatrixTransform3D : MilResource3D
    {
        public MilMatrix4x4F Matrix;
    }

    // ---------------------------- 3D 视觉树节点 (0x29–0x30) ----------------------------

    /// <summary>
    /// `TYPE_VIEWPORT3DVISUAL`(40) 节点的解码结果，由 0x29 / 0x2a / 0x2b 三条命令写入。
    /// 三个字段互相独立，各自只被对应的一条命令改写。
    /// </summary>
    internal sealed class MilViewport3DVisual : MilResource3D
    {
        public DUCE.ResourceHandle HCamera;
        public MilRect Viewport;
        public DUCE.ResourceHandle HChild;
    }

    /// <summary>
    /// `TYPE_VISUAL3D`(41) 节点的解码结果，由 0x2c–0x30 五条命令写入。
    /// `Children` 是真的在增删（0x2e/0x2f/0x30），不是记账式的空操作。
    /// </summary>
    internal sealed class MilVisual3D : MilResource3D
    {
        public DUCE.ResourceHandle HContent;
        public DUCE.ResourceHandle HTransform;
        public readonly List<DUCE.ResourceHandle> Children = new List<DUCE.ResourceHandle>();
    }

    /// <summary>
    /// 3D 解码结果的附加表：MilResource 实例（弱键）→ 解码结果。
    /// </summary>
    internal static class MilResource3DTable
    {
        private static readonly ConditionalWeakTable<Resources.MilResource, MilResource3D> s_attached = new();

        /// <summary>取已附加的解码结果；类型不符（说明类型校验漏了）返回 null。</summary>
        public static T Lookup<T>(Resources.MilResource owner) where T : MilResource3D
        {
            if (owner == null) return null;
            s_attached.TryGetValue(owner, out MilResource3D value);
            return value as T;
        }

        /// <summary>取已附加的解码结果，没有则按 kind 新建并附加。</summary>
        public static T GetOrAttach<T>(Resources.MilResource owner, MilCmd cmd)
            where T : MilResource3D, new()
        {
            if (s_attached.TryGetValue(owner, out MilResource3D existing) && existing is T typed)
            {
                typed.LastCommand = cmd;
                return typed;
            }

            var created = new T { LastCommand = cmd };
            s_attached.Remove(owner);
            s_attached.Add(owner, created);
            return created;
        }
    }
}
