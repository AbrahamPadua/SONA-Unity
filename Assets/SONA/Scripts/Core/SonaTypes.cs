// SONA Quest port: the data contracts between sensing, encoding and rendering.
// Port of returns.py plus the dataclasses in encoder.py.
//
//   geometry --frontend--> Return3D[] --encoder--> ScheduledReturn[] (RenderPlan) --renderer--> audio
//
// The encoder never knows where returns came from (test room, scanned mesh, or the
// Quest depth/scene API later), which is the same seam the Python prototype kept.
using System;
using System.Collections.Generic;

namespace Sona.Core
{
    /// <summary>A planar quad, sampled on a bilinear grid. Edges C0->C1 and C0->C3 set the grid density.</summary>
    public struct SurfaceQuad
    {
        public Vec3d C0;
        public Vec3d C1;
        public Vec3d C2;
        public Vec3d C3;
        public Vec3d Normal;      // outward unit normal (Zero when unknown)
        public string SourceId;   // object id, e.g. "box-1" or "room"
        public string Face;       // e.g. "floor", "left-wall", "near"
        public string Material;   // "floor" selects the fixed low floor tone

        public SurfaceQuad(Vec3d c0, Vec3d c1, Vec3d c2, Vec3d c3, Vec3d normal,
                           string sourceId, string face, string material)
        {
            C0 = c0;
            C1 = c1;
            C2 = c2;
            C3 = c3;
            Normal = normal;
            SourceId = sourceId;
            Face = face;
            Material = material;
        }
    }

    /// <summary>A single pre-sampled surface point (used for arbitrary meshes and future depth sensors).</summary>
    public struct SurfacePoint
    {
        public Vec3d Position;
        public Vec3d Normal;
        public string SourceId;
        public string Material;

        public SurfacePoint(Vec3d position, Vec3d normal, string sourceId, string material)
        {
            Position = position;
            Normal = normal;
            SourceId = sourceId;
            Material = material;
        }
    }

    /// <summary>
    /// One direction-plus-distance sample (returns.py Return).
    /// Azimuth is relative to the listener heading at ping time; elevation is gravity-referenced.
    /// </summary>
    public struct Return3D
    {
        public double Azimuth;           // rad. 0 = straight ahead, + = right
        public double Elevation;         // rad. 0 = level with the ears, + = up
        public double Range;             // true 3-D distance (m)
        public double PropagationRange;  // horizontal distance (m): drives the sweep timing
        public Vec3d WorldPosition;
        public Vec3d Normal;
        public string SourceId;
        public string Material;
    }

    /// <summary>
    /// Where the listener is and which way they face (returns.py ListenerPose).
    /// Heading is yaw in radians, clockwise from world +Z (forward), matching azimuth.
    /// </summary>
    public struct ListenerPose3D
    {
        public Vec3d Position;
        public double Heading;

        public ListenerPose3D(Vec3d position, double heading)
        {
            Position = position;
            Heading = heading;
        }

        /// <summary>Heading from a forward vector; falls back to <paramref name="up"/> when looking straight up/down.</summary>
        public static ListenerPose3D FromForward(Vec3d position, Vec3d forward, Vec3d up)
        {
            double heading = 0.0;
            if (SonaMath.Hypot(forward.X, forward.Z) > 1e-6)
            {
                heading = Math.Atan2(forward.X, forward.Z);
            }
            else if (SonaMath.Hypot(up.X, up.Z) > 1e-6)
            {
                // Looking straight down: the top of the head points the way the user faces.
                double sign = forward.Y < 0.0 ? 1.0 : -1.0;
                heading = Math.Atan2(sign * up.X, sign * up.Z);
            }
            return new ListenerPose3D(position, heading);
        }
    }

    /// <summary>encoder.py EncodingParams. Defaults match the Python dataclass.</summary>
    [Serializable]
    public sealed class EncodingParams
    {
        public double SweepDuration = 1.2;                    // s; keep < ~1.5 for echoic memory
        public double Near = 0.5;                             // m; loudness reference/clamp
        public double Far = 5.0;                              // m; planar sweep radius
        public double FoveationExponent = 0.5;                // < 1 expands near space in time
        public double PeripheralDb = 18.0;                    // attenuation at/beyond the field edge
        public double FieldHalfAngle = 90.0 * SonaMath.Deg2Rad;
        public double NearEmphasis = 0.3;                     // 0 = flat loudness; higher = nearer louder

        public EncodingParams Clone()
        {
            return (EncodingParams)MemberwiseClone();
        }
    }

    /// <summary>encoder.py ScheduledReturn: one sound event of the RenderPlan.</summary>
    public struct ScheduledReturn
    {
        public double Onset;             // seconds into the sweep
        public double Azimuth;           // rad (relative to heading at ping time)
        public double Elevation;         // rad
        public double Amplitude;         // linear gain before the renderer's mix gain/limiting
        public double PropagationRange;
        public double Range;
        public Vec3d WorldPosition;
        public string SourceId;
        public string Material;
    }

    public static class SonaMaterials
    {
        public const string Floor = "floor";

        public static bool IsFloor(string material)
        {
            return material != null && string.Equals(material.Trim(), Floor, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Stable sort helper (Python's list.sort is stable; List.Sort is not).</summary>
    public static class StableSort
    {
        public static void ByOnset(List<ScheduledReturn> plan)
        {
            int n = plan.Count;
            var keys = new double[n];
            var idx = new int[n];
            for (int i = 0; i < n; i++)
            {
                keys[i] = plan[i].Onset;
                idx[i] = i;
            }
            Array.Sort(idx, (a, b) =>
            {
                int c = keys[a].CompareTo(keys[b]);
                return c != 0 ? c : a.CompareTo(b);
            });
            var copy = plan.ToArray();
            for (int i = 0; i < n; i++)
            {
                plan[i] = copy[idx[i]];
            }
        }
    }
}
