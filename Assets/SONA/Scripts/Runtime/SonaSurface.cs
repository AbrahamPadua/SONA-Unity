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
        /// <summary>Area-weighted samples of the MeshFilter's mesh (imported or scanned geometry). Needs Read/Write enabled.</summary>
        Mesh,
    }

    [DisallowMultipleComponent]
    [AddComponentMenu("SONA/SONA Surface")]
    public sealed class SonaSurface : MonoBehaviour
    {
        [Tooltip("Box = Unity Cube primitive faces, Quad = Unity Quad primitive, Mesh = sample the MeshFilter mesh.")]
        public SonaSurfaceShape shape = SonaSurfaceShape.Box;

        [Tooltip("Surface tag. \"floor\" gets SONA's fixed low floor tone; leave empty for ordinary surfaces.")]
        public string surfaceTag = "";

        [Tooltip("Optional id for logs; defaults to the GameObject name.")]
        public string sourceId = "";

        [Tooltip("Mesh shape only: seed for the (repeatable) random surface samples.")]
        public int meshSeed = 1234;

        static readonly List<SonaSurface> s_active = new List<SonaSurface>();

        /// <summary>Every enabled surface in the loaded scenes.</summary>
        public static List<SonaSurface> Active { get { return s_active; } }

        Vector3[] _meshPoints;
        Vector3[] _meshNormals;
        float _meshSpm = -1f;
        Vector3 _meshScale;
        bool _warnedUnreadable;

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

        /// <summary>Append this surface's quads in world space (Box and Quad shapes).</summary>
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

        /// <summary>Append pre-sampled world points (Mesh shape). Density matches the quad grid: about spm^2 per m^2.</summary>
        public void AppendPoints(List<SurfacePoint> points, float samplesPerMeter)
        {
            if (shape != SonaSurfaceShape.Mesh) return;
            if (!EnsureMeshSamples(samplesPerMeter)) return;
            Transform t = transform;
            string id = Id;
            string tag = TagOrNull;
            for (int i = 0; i < _meshPoints.Length; i++)
            {
                points.Add(new SurfacePoint(
                    t.TransformPoint(_meshPoints[i]).ToVec3d(),
                    t.TransformDirection(_meshNormals[i]).ToVec3d(),
                    id, tag));
            }
        }

        bool EnsureMeshSamples(float spm)
        {
            Vector3 scale = transform.lossyScale;
            if (_meshPoints != null && Mathf.Approximately(_meshSpm, spm) && scale == _meshScale) return true;
            var mf = GetComponent<MeshFilter>();
            UnityEngine.Mesh mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null) return false;
            if (!mesh.isReadable)
            {
                if (!_warnedUnreadable)
                {
                    Debug.LogWarning("[SONA] Mesh '" + mesh.name + "' on " + name +
                                     " is not readable. Enable Read/Write in its import settings to sonify it.", this);
                    _warnedUnreadable = true;
                }
                return false;
            }
            Vector3[] verts = mesh.vertices;
            int[] tris = mesh.triangles;
            int triCount = tris.Length / 3;
            if (triCount == 0) return false;
            var cumulative = new double[triCount];
            double total = 0.0;
            Matrix4x4 m = transform.localToWorldMatrix;
            for (int i = 0; i < triCount; i++)
            {
                Vector3 a = m.MultiplyPoint3x4(verts[tris[3 * i]]);
                Vector3 b = m.MultiplyPoint3x4(verts[tris[3 * i + 1]]);
                Vector3 c = m.MultiplyPoint3x4(verts[tris[3 * i + 2]]);
                total += 0.5 * Vector3.Cross(b - a, c - a).magnitude;
                cumulative[i] = total;
            }
            int count = Mathf.Clamp(Mathf.RoundToInt((float)(total * spm * spm)), 1, 50000);
            var rng = new System.Random(meshSeed);
            _meshPoints = new Vector3[count];
            _meshNormals = new Vector3[count];
            for (int s = 0; s < count; s++)
            {
                double pick = rng.NextDouble() * total;
                int lo = System.Array.BinarySearch(cumulative, pick);
                if (lo < 0) lo = ~lo;
                if (lo >= triCount) lo = triCount - 1;
                Vector3 a = verts[tris[3 * lo]];
                Vector3 b = verts[tris[3 * lo + 1]];
                Vector3 c = verts[tris[3 * lo + 2]];
                float r1 = (float)rng.NextDouble();
                float r2 = (float)rng.NextDouble();
                if (r1 + r2 > 1f)
                {
                    r1 = 1f - r1;
                    r2 = 1f - r2;
                }
                _meshPoints[s] = a + r1 * (b - a) + r2 * (c - a);
                _meshNormals[s] = Vector3.Cross(b - a, c - a).normalized;
            }
            _meshSpm = spm;
            _meshScale = scale;
            return true;
        }
    }
}
