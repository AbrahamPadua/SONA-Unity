// SONA Quest port: surface quads of Unity's Cube and Quad primitives, shared by SonaSurface (runtime)
// and the parity tests, so the tested code is the code that runs on the Quest.
using System;
using System.Collections.Generic;

namespace Sona.Core
{
    public static class SonaPrimitives
    {
        // Footprint order of scene._prism_quads: near-left, near-right, far-right, far-left (local XZ).
        static readonly double[,] s_foot = { { -0.5, -0.5 }, { 0.5, -0.5 }, { 0.5, 0.5 }, { -0.5, 0.5 } };

        /// <summary>
        /// Six faces of the local unit cube [-0.5, 0.5]^3, in scene.py's prism order (bottom, top, near,
        /// right, far, left) so the sampled grids match the Python boxes and panels point for point.
        /// </summary>
        public static void AppendBoxQuads(List<SurfaceQuad> quads, Func<Vec3d, Vec3d> toWorld,
                                          Vec3d right, Vec3d up, Vec3d forward, string id, string tag)
        {
            var b = new Vec3d[4];
            var t = new Vec3d[4];
            for (int k = 0; k < 4; k++)
            {
                b[k] = toWorld(new Vec3d(s_foot[k, 0], -0.5, s_foot[k, 1]));
                t[k] = toWorld(new Vec3d(s_foot[k, 0], 0.5, s_foot[k, 1]));
            }
            quads.Add(new SurfaceQuad(b[0], b[3], b[2], b[1], -up, id, "bottom", tag));
            quads.Add(new SurfaceQuad(t[0], t[1], t[2], t[3], up, id, "top", tag));
            quads.Add(new SurfaceQuad(b[0], b[1], t[1], t[0], -forward, id, "near", tag));
            quads.Add(new SurfaceQuad(b[1], b[2], t[2], t[1], right, id, "right", tag));
            quads.Add(new SurfaceQuad(b[2], b[3], t[3], t[2], forward, id, "far", tag));
            quads.Add(new SurfaceQuad(b[3], b[0], t[0], t[3], -right, id, "left", tag));
        }

        /// <summary>Unity's Quad primitive: unit square in local XY, visible face pointing local -Z.</summary>
        public static void AppendQuadQuad(List<SurfaceQuad> quads, Func<Vec3d, Vec3d> toWorld, Vec3d frontNormal,
                                          string id, string tag)
        {
            quads.Add(new SurfaceQuad(
                toWorld(new Vec3d(-0.5, -0.5, 0.0)),
                toWorld(new Vec3d(0.5, -0.5, 0.0)),
                toWorld(new Vec3d(0.5, 0.5, 0.0)),
                toWorld(new Vec3d(-0.5, 0.5, 0.0)),
                frontNormal, id, "quad", tag));
        }
    }

    public enum PrimitiveShape { Quad, Box }

    /// <summary>One primitive the room builder instantiates (local to the listener-spawn transform).</summary>
    public struct PrimitivePlacement
    {
        public string Name;
        public PrimitiveShape Shape;
        public Vec3d LocalPosition;
        public Vec3d EulerDegrees;   // Unity Euler angles (x, y, z)
        public Vec3d Scale;
        public string Tag;           // "floor" or null
        public string SourceId;
        public bool IsObject;        // box/panel (true) or room surface (false)

        public Vec3d ToLocal(Vec3d unitPoint)
        {
            var scaled = new Vec3d(unitPoint.X * Scale.X, unitPoint.Y * Scale.Y, unitPoint.Z * Scale.Z);
            return LocalPosition + QuatD.Euler(EulerDegrees.X, EulerDegrees.Y, EulerDegrees.Z).Rotate(scaled);
        }
    }
}
