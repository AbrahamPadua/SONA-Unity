// SONA Quest port: marks Unity geometry as something the sweep can "hear".
// Replaces the Python editor's room/box/panel surface lists: any GameObject with this
// component is sampled every time the sweep is triggered, so objects can move freely.
using System;
using System.Collections.Generic;
using Sona.Core;
using UnityEngine;

namespace Sona
{
    public enum SonaSurfaceShape
    {
        /// <summary>Six faces of the local unit cube (Unity's Cube primitive): boxes, panels, furniture blocks.</summary>
        Box,
        /// <summary>The local unit quad in XY facing -Z (Unity's Quad primitive): floors and walls.</summary>
        Quad,
    }

    [DisallowMultipleComponent]
    [AddComponentMenu("SONA/SONA Surface")]
    public sealed class SonaSurface : MonoBehaviour
    {
        [Tooltip("Box = Unity Cube primitive faces, Quad = Unity Quad primitive.")]
        public SonaSurfaceShape shape = SonaSurfaceShape.Box;

        [Tooltip("Surface tag. \"floor\" gets SONA's fixed low floor tone; leave empty for ordinary surfaces.")]
        public string surfaceTag = "";

        [Tooltip("Optional id for logs; defaults to the GameObject name.")]
        public string sourceId = "";

        static readonly List<SonaSurface> s_active = new List<SonaSurface>();

        /// <summary>Every enabled surface in the loaded scenes.</summary>
        public static List<SonaSurface> Active { get { return s_active; } }

        void OnEnable()
        {
            if (!s_active.Contains(this)) s_active.Add(this);
        }

        void OnDisable()
        {
            s_active.Remove(this);
        }

        public string Id { get { return string.IsNullOrEmpty(sourceId) ? gameObject.name : sourceId; } }

        string TagOrNull { get { return string.IsNullOrEmpty(surfaceTag) ? null : surfaceTag; } }

        /// <summary>Append this surface's quads in world space.</summary>
        public void AppendQuads(List<SurfaceQuad> quads)
        {
            Transform t = transform;
            Func<Vec3d, Vec3d> toWorld = p => t.TransformPoint(p.ToVector3()).ToVec3d();
            if (shape == SonaSurfaceShape.Box)
            {
                SonaPrimitives.AppendBoxQuads(quads, toWorld, t.right.ToVec3d(), t.up.ToVec3d(), t.forward.ToVec3d(), Id, TagOrNull);
            }
            else if (shape == SonaSurfaceShape.Quad)
            {
                SonaPrimitives.AppendQuadQuad(quads, toWorld, (-t.forward).ToVec3d(), Id, TagOrNull);
            }
        }
    }
}
