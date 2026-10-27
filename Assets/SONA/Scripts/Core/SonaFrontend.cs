// SONA Quest port: front-end (frontend.py). Anything that produces Return3D samples.
//
// X-RAY MODE (as in the Python prototype): the scene is fully known, so every
// sampled surface point is emitted, occluded or not. Many points share a
// direction at different ranges (box front, box back, wall behind it), which is
// what lets the wave wash over full geometry. The encoder sorts by onset, so the
// expanding shell is emergent.
using System;
using System.Collections.Generic;

namespace Sona.Core
{
    public static class SonaFrontend
    {
        /// <summary>Grid count along one quad edge: max(2, int(round(length * spm))) with Python's round-half-even.</summary>
        public static int GridCount(double edgeLength, double samplesPerMeter)
        {
            return Math.Max(2, (int)Math.Round(edgeLength * samplesPerMeter, MidpointRounding.ToEven));
        }

        /// <summary>
        /// Grid-sample a planar quad (frontend.sample_quad). Points are appended in the same
        /// (i over C0->C1, j over C0->C3) order as the Python list.
        /// </summary>
        public static void SampleQuad(SurfaceQuad q, double samplesPerMeter, List<Vec3d> output)
        {
            int nu = GridCount((q.C1 - q.C0).Length, samplesPerMeter);
            int nv = GridCount((q.C3 - q.C0).Length, samplesPerMeter);
            for (int i = 0; i < nu; i++)
            {
                double u = (double)i / (nu - 1);
                for (int j = 0; j < nv; j++)
                {
                    double v = (double)j / (nv - 1);
                    double w0 = (1.0 - u) * (1.0 - v);
                    double w1 = u * (1.0 - v);
                    double w2 = u * v;
                    double w3 = (1.0 - u) * v;
                    // Same accumulation order as numpy: ((w0*C0 + w1*C1) + w2*C2) + w3*C3
                    output.Add(w0 * q.C0 + w1 * q.C1 + w2 * q.C2 + w3 * q.C3);
                }
            }
        }

        /// <summary>
        /// Build one Return3D for a world point seen from the listener, or false if it is skipped.
        /// <paramref name="spherical"/> = false is the Python behaviour (cylindrical wave: timing from
        /// horizontal distance); true times the wave by full 3-D distance (step 14 comparison).
        /// </summary>
        public static bool TryMakeReturn(Vec3d p, ListenerPose3D listener, double? maxRange,
                                         Vec3d normal, string sourceId, string material, out Return3D ret,
                                         bool spherical = false)
        {
            Vec3d v = p - listener.Position;
            double r = v.Length;
            double propagationRange = spherical ? r : v.HorizontalLength;
            if (r < 1e-3 || (maxRange.HasValue && propagationRange > maxRange.Value))
            {
                ret = default(Return3D);
                return false;
            }
            double worldAzimuth = Math.Atan2(v.X, v.Z);
            ret = new Return3D
            {
                Azimuth = SonaMath.WrapPi(worldAzimuth - listener.Heading),
                Elevation = Math.Asin(SonaMath.Clamp(v.Y / r, -1.0, 1.0)),
                Range = r,
                PropagationRange = propagationRange,
                WorldPosition = p,
                Normal = normal,
                SourceId = sourceId,
                Material = material,
            };
            return true;
        }

        /// <summary>
        /// frontend.surface_emission: convert every sampled surface point to a Return3D.
        /// Propagation is top-down (unless <paramref name="spherical"/>), so <paramref name="maxRange"/> uses planar distance; true 3-D
        /// range and elevation remain available for loudness and pitch. No visibility test (X-ray).
        /// </summary>
        public static List<Return3D> SurfaceEmission(
            IList<SurfaceQuad> quads,
            ListenerPose3D listener,
            double samplesPerMeter = 5.0,
            double? maxRange = 8.0,
            List<Return3D> output = null,
            bool spherical = false)
        {
            if (!(samplesPerMeter > 0.0))
            {
                throw new ArgumentException("samplesPerMeter must be positive");
            }
            if (maxRange.HasValue && !(maxRange.Value > 0.0))
            {
                throw new ArgumentException("maxRange must be positive or null");
            }
            var returns = output ?? new List<Return3D>();
            returns.Clear();
            var points = new List<Vec3d>(256);
            if (quads != null)
            {
                for (int qi = 0; qi < quads.Count; qi++)
                {
                    SurfaceQuad q = quads[qi];
                    points.Clear();
                    SampleQuad(q, samplesPerMeter, points);
                    for (int k = 0; k < points.Count; k++)
                    {
                        Return3D ret;
                        if (TryMakeReturn(points[k], listener, maxRange, q.Normal, q.SourceId, q.Material, out ret, spherical))
                        {
                            returns.Add(ret);
                        }
                    }
                }
            }
            return returns;
        }

        /// <summary>Largest planar distance among the returns (the sweep radius that covers the whole scene).</summary>
        public static double MaxPropagationRange(IList<Return3D> returns)
        {
            double far = 0.0;
            for (int i = 0; i < returns.Count; i++)
            {
                if (returns[i].PropagationRange > far)
                {
                    far = returns[i].PropagationRange;
                }
            }
            return far;
        }
    }
}
