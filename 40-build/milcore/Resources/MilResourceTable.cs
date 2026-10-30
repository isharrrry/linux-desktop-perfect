// Licensed to the .NET Foundation under one or more agreements.
//
// 通道内的资源句柄表。
//
// 语义对齐上游 wpfgfx 的 CResourceTable / HMilResource：
//   - 句柄 0 永远是 Null，不指向任何资源
//   - CreateOrAddRefOnChannel：句柄为 Null 时分配新句柄（RefCount=1），
//     否则对既有句柄 AddRef（RefCount++）
//   - ReleaseOnChannel：RefCount--；降到 0 时从表里摘除，deleted=1
//   - DuplicateHandle：同一 partition 内跨通道共享同一个 MilResource 实例，
//     目标通道拿到自己的句柄与自己的 RefCount

using System;
using System.Collections.Generic;
using WpfGfx.Linux.Contracts;
using WpfGfx.Linux.Interop;

namespace WpfGfx.Linux.Resources
{
    internal sealed class MilResourceEntry
    {
        public MilResource Resource;
        public uint RefCount;
    }

    internal sealed class MilResourceTable : IMilResourceTable
    {
        private readonly Dictionary<uint, MilResourceEntry> _entries = new Dictionary<uint, MilResourceEntry>();
        private readonly Stack<uint> _freeHandles = new Stack<uint>();
        private uint _nextHandle = 1;

        /// <summary>当前活跃句柄数（不含 Null）。</summary>
        public int Count => _entries.Count;

        /// <summary>已分配过的句柄总数（含已释放）——用于测试句柄回收。</summary>
        public uint HighWaterMark => _nextHandle - 1;

        public bool IsEmpty => _entries.Count == 0;

        /// <summary>
        /// CreateOrAddRef：handle 为 Null 时新建；否则 AddRef。
        /// 返回实际使用的句柄。
        /// </summary>
        public DUCE.ResourceHandle CreateOrAddRef(DUCE.ResourceType type, ref DUCE.ResourceHandle handle)
        {
            if (handle.IsNull)
            {
                MilResource resource = MilResourceFactory.Create(type);
                resource.LastCommand = MilCmd.MilCmdChannelCreateResource;
                uint h = AllocHandle();
                _entries[h] = new MilResourceEntry { Resource = resource, RefCount = 1 };
                handle = new DUCE.ResourceHandle(h);
                BindHandle(resource, handle);
            }
            else
            {
                MilResourceEntry entry = LookupEntry(handle);
                if (entry == null)
                {
                    // 上游会把它当无效句柄处理；这里宽容地重建一个，避免整条命令流崩掉。
                    MilResource resource = MilResourceFactory.Create(type);
                    _entries[(uint)handle] = new MilResourceEntry { Resource = resource, RefCount = 1 };
                    BindHandle(resource, handle);
                    return handle;
                }
                entry.RefCount++;
            }
            return handle;
        }

        /// <summary>Release：RefCount--，归零则摘除。deleted 表示是否已从表里移除。</summary>
        public bool Release(DUCE.ResourceHandle handle, out bool deleted)
        {
            deleted = false;
            if (handle.IsNull) return false;

            uint h = (uint)handle;
            if (!_entries.TryGetValue(h, out MilResourceEntry entry)) return false;

            if (entry.RefCount > 0) entry.RefCount--;

            if (entry.RefCount == 0)
            {
                _entries.Remove(h);
                _freeHandles.Push(h);
                deleted = true;
            }
            return true;
        }

        /// <summary>DuplicateHandle：把源通道的同一个 MilResource 实例挂到本表的新句柄上。</summary>
        public DUCE.ResourceHandle Duplicate(MilResource shared, ref DUCE.ResourceHandle duplicate)
        {
            if (shared == null) return DUCE.ResourceHandle.Null;

            if (!duplicate.IsNull && _entries.ContainsKey((uint)duplicate))
            {
                _entries[(uint)duplicate].RefCount++;
                return duplicate;
            }

            uint h = AllocHandle();
            _entries[h] = new MilResourceEntry { Resource = shared, RefCount = 1 };
            duplicate = new DUCE.ResourceHandle(h);
            return duplicate;
        }

        private uint AllocHandle()
        {
            if (_freeHandles.Count > 0) return _freeHandles.Pop();
            return _nextHandle++;
        }

        /// <summary>把分配到的句柄回填给需要自知的 Visual 资源。</summary>
        private static void BindHandle(MilResource resource, DUCE.ResourceHandle handle)
        {
            if (resource is MilVisualResource v) v.Visual.Handle = handle;
        }

        public MilResourceEntry LookupEntry(DUCE.ResourceHandle handle)
        {
            if (handle.IsNull) return null;
            _entries.TryGetValue((uint)handle, out MilResourceEntry e);
            return e;
        }

        public MilResource Lookup(DUCE.ResourceHandle handle) => LookupEntry(handle)?.Resource;

        public T Lookup<T>(DUCE.ResourceHandle handle) where T : MilResource => Lookup(handle) as T;

        public uint GetRefCount(DUCE.ResourceHandle handle) => LookupEntry(handle)?.RefCount ?? 0;

        /// <summary>整表清空（DestroyChannel 时用）。</summary>
        public void Clear()
        {
            _entries.Clear();
            _freeHandles.Clear();
            _nextHandle = 1;
        }

        public IEnumerable<KeyValuePair<uint, MilResourceEntry>> Entries => _entries;

        // ==================================================================
        //  契约 IMilResourceTable 实现（Contracts/Interfaces.cs，只读）
        //
        //  契约用 MilResourceType / MilResourceHandle，本表内部用 DUCE.ResourceType /
        //  DUCE.ResourceHandle——两者取值与宽度逐项一致（Duce.cs 与 Contracts/
        //  MilResourceType.cs 同源），这里只做零成本转换，不改变语义。
        // ==================================================================

        MilResourceHandle IMilResourceTable.Create(MilResourceType type, out object resource)
        {
            DUCE.ResourceHandle h = DUCE.ResourceHandle.Null;
            CreateOrAddRef((DUCE.ResourceType)type, ref h);
            resource = Lookup(h);
            return new MilResourceHandle((uint)h);
        }

        object IMilResourceTable.Lookup(MilResourceHandle handle) =>
            Lookup(new DUCE.ResourceHandle(handle.Value));

        int IMilResourceTable.AddRef(MilResourceHandle handle)
        {
            DUCE.ResourceHandle h = new DUCE.ResourceHandle(handle.Value);
            MilResourceEntry entry = LookupEntry(h);
            if (entry == null) return 0;
            entry.RefCount++;
            return (int)entry.RefCount;
        }

        int IMilResourceTable.Release(MilResourceHandle handle, out bool deleted)
        {
            Release(new DUCE.ResourceHandle(handle.Value), out deleted);
            return (int)GetRefCount(new DUCE.ResourceHandle(handle.Value));
        }

        void IMilResourceTable.DestroyAll() => Clear();
    }

    /// <summary>ResourceType → 具体资源类的工厂。</summary>
    internal static class MilResourceFactory
    {
        public static MilResource Create(DUCE.ResourceType type)
        {
            switch (type)
            {
                case DUCE.ResourceType.TYPE_VISUAL: return new MilVisualResource();
                case DUCE.ResourceType.TYPE_RENDERDATA: return new MilRenderDataResource();

                case DUCE.ResourceType.TYPE_DOUBLERESOURCE: return new MilDoubleResource();
                case DUCE.ResourceType.TYPE_COLORRESOURCE: return new MilColorResource();
                case DUCE.ResourceType.TYPE_POINTRESOURCE: return new MilPointResource();
                case DUCE.ResourceType.TYPE_RECTRESOURCE: return new MilRectResource();
                case DUCE.ResourceType.TYPE_SIZERESOURCE: return new MilSizeResource();
                case DUCE.ResourceType.TYPE_MATRIXRESOURCE: return new MilMatrixResource();
                case DUCE.ResourceType.TYPE_POINT3DRESOURCE: return new MilPoint3DResource();
                case DUCE.ResourceType.TYPE_VECTOR3DRESOURCE: return new MilVector3DResource();
                case DUCE.ResourceType.TYPE_QUATERNIONRESOURCE: return new MilQuaternionResource();
                case DUCE.ResourceType.TYPE_ETWEVENTRESOURCE: return new MilEtwEventResource();

                case DUCE.ResourceType.TYPE_HWNDRENDERTARGET: return new MilHwndTarget();
                case DUCE.ResourceType.TYPE_GENERICRENDERTARGET: return new MilGenericTarget();

                case DUCE.ResourceType.TYPE_TRANSLATETRANSFORM: return new MilTranslateTransform();
                case DUCE.ResourceType.TYPE_SCALETRANSFORM: return new MilScaleTransform();
                case DUCE.ResourceType.TYPE_SKEWTRANSFORM: return new MilSkewTransform();
                case DUCE.ResourceType.TYPE_ROTATETRANSFORM: return new MilRotateTransform();
                case DUCE.ResourceType.TYPE_MATRIXTRANSFORM: return new MilMatrixTransform();
                case DUCE.ResourceType.TYPE_TRANSFORMGROUP: return new MilTransformGroup();

                case DUCE.ResourceType.TYPE_SOLIDCOLORBRUSH: return new MilSolidColorBrush();
                case DUCE.ResourceType.TYPE_LINEARGRADIENTBRUSH: return new MilLinearGradientBrush();
                case DUCE.ResourceType.TYPE_RADIALGRADIENTBRUSH: return new MilRadialGradientBrush();
                case DUCE.ResourceType.TYPE_IMAGEBRUSH: return new MilImageBrush();
                case DUCE.ResourceType.TYPE_DRAWINGBRUSH: return new MilDrawingBrush();
                case DUCE.ResourceType.TYPE_VISUALBRUSH: return new MilVisualBrush();
                case DUCE.ResourceType.TYPE_BITMAPCACHEBRUSH: return new MilBitmapCacheBrush();
                case DUCE.ResourceType.TYPE_IMPLICITINPUTBRUSH: return new MilImplicitInputBrush();

                case DUCE.ResourceType.TYPE_LINEGEOMETRY: return new MilLineGeometry();
                case DUCE.ResourceType.TYPE_RECTANGLEGEOMETRY: return new MilRectangleGeometry();
                case DUCE.ResourceType.TYPE_ELLIPSEGEOMETRY: return new MilEllipseGeometry();
                case DUCE.ResourceType.TYPE_GEOMETRYGROUP: return new MilGeometryGroup();
                case DUCE.ResourceType.TYPE_COMBINEDGEOMETRY: return new MilCombinedGeometry();
                case DUCE.ResourceType.TYPE_PATHGEOMETRY: return new MilPathGeometry();

                case DUCE.ResourceType.TYPE_DASHSTYLE: return new MilDashStyle();
                case DUCE.ResourceType.TYPE_PEN: return new MilPen();

                case DUCE.ResourceType.TYPE_GEOMETRYDRAWING: return new MilGeometryDrawing();
                case DUCE.ResourceType.TYPE_GLYPHRUNDRAWING: return new MilGlyphRunDrawing();
                case DUCE.ResourceType.TYPE_IMAGEDRAWING: return new MilImageDrawing();
                case DUCE.ResourceType.TYPE_VIDEODRAWING: return new MilVideoDrawing();
                case DUCE.ResourceType.TYPE_DRAWINGGROUP: return new MilDrawingGroup();
                case DUCE.ResourceType.TYPE_DRAWINGIMAGE: return new MilDrawingImage();

                case DUCE.ResourceType.TYPE_BLUREFFECT: return new MilBlurEffect();
                case DUCE.ResourceType.TYPE_DROPSHADOWEFFECT: return new MilDropShadowEffect();
                case DUCE.ResourceType.TYPE_BITMAPCACHE: return new MilBitmapCache();
                case DUCE.ResourceType.TYPE_GUIDELINESET: return new MilGuidelineSet();

                case DUCE.ResourceType.TYPE_GLYPHRUN: return new MilGlyphRun();

                default:
                    // 3D / D3D / 媒体 等 M1 不建模的类型：占位，解码器会返回 E_NOTIMPL。
                    return new MilOpaqueResource(type);
            }
        }
    }
}
