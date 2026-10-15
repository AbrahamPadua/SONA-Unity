// SONA Quest port: core math. Pure C#, no UnityEngine dependency, double precision
// so the core can be parity-tested against the original Python/numpy pipeline.
//
// Frame convention (Unity): +X = right, +Y = up, +Z = forward.
// The Python prototype used +X = right, +Y = forward, +Z = up; a Python point
// (x, y, z) is the Unity point (x, z, y).
using System;

namespace Sona.Core
{
    [Serializable]
    public struct Vec3d
    {
        public double X;
        public double Y;
        public double Z;

        public Vec3d(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static readonly Vec3d Zero = new Vec3d(0.0, 0.0, 0.0);

        public static Vec3d operator +(Vec3d a, Vec3d b) { return new Vec3d(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static Vec3d operator -(Vec3d a, Vec3d b) { return new Vec3d(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static Vec3d operator -(Vec3d a) { return new Vec3d(-a.X, -a.Y, -a.Z); }
        public static Vec3d operator *(Vec3d a, double s) { return new Vec3d(a.X * s, a.Y * s, a.Z * s); }
        public static Vec3d operator *(double s, Vec3d a) { return new Vec3d(a.X * s, a.Y * s, a.Z * s); }

        public static double Dot(Vec3d a, Vec3d b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }

        public static Vec3d Cross(Vec3d a, Vec3d b)
        {
            return new Vec3d(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }

        /// <summary>Euclidean length (numpy.linalg.norm equivalent).</summary>
        public double Length { get { return Math.Sqrt(X * X + Y * Y + Z * Z); } }

        /// <summary>Length of the horizontal (XZ) component: the planar distance that drives the sweep.</summary>
        public double HorizontalLength { get { return SonaMath.Hypot(X, Z); } }

        public Vec3d Normalized
        {
            get
            {
                double m = Length;
                return m > 1e-12 ? new Vec3d(X / m, Y / m, Z / m) : Zero;
            }
        }

        public bool IsFinite
        {
            get { return SonaMath.IsFinite(X) && SonaMath.IsFinite(Y) && SonaMath.IsFinite(Z); }
        }

        public override string ToString()
        {
            return string.Format("({0:0.###}, {1:0.###}, {2:0.###})", X, Y, Z);
        }
    }

    /// <summary>Unit quaternion (x, y, z, w) using the same convention as UnityEngine.Quaternion.</summary>
    [Serializable]
    public struct QuatD
    {
        public double X;
        public double Y;
        public double Z;
        public double W;

        public QuatD(double x, double y, double z, double w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public static readonly QuatD Identity = new QuatD(0.0, 0.0, 0.0, 1.0);

        /// <summary>Rotation about +Y (yaw), clockwise seen from above, like Quaternion.Euler(0, deg, 0).</summary>
        public static QuatD FromYawDegrees(double degrees)
        {
            double half = 0.5 * degrees * Math.PI / 180.0;
            return new QuatD(0.0, Math.Sin(half), 0.0, Math.Cos(half));
        }

        public static QuatD AxisAngle(Vec3d axis, double degrees)
        {
            Vec3d n = axis.Normalized;
            double half = 0.5 * degrees * SonaMath.Deg2Rad;
            double s = Math.Sin(half);
            return new QuatD(n.X * s, n.Y * s, n.Z * s, Math.Cos(half));
        }

        /// <summary>Same convention as UnityEngine.Quaternion.Euler: z, then x, then y (q = qy * qx * qz).</summary>
        public static QuatD Euler(double xDeg, double yDeg, double zDeg)
        {
            return AxisAngle(new Vec3d(0, 1, 0), yDeg) * AxisAngle(new Vec3d(1, 0, 0), xDeg) * AxisAngle(new Vec3d(0, 0, 1), zDeg);
        }

        public static QuatD operator *(QuatD a, QuatD b)
        {
            return new QuatD(
                a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y + a.Y * b.W + a.Z * b.X - a.X * b.Z,
                a.W * b.Z + a.Z * b.W + a.X * b.Y - a.Y * b.X,
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
        }

        /// <summary>q * v (same result as UnityEngine.Quaternion * Vector3 for a unit quaternion).</summary>
        public Vec3d Rotate(Vec3d v)
        {
            var u = new Vec3d(X, Y, Z);
            Vec3d t = 2.0 * Vec3d.Cross(u, v);
            return v + W * t + Vec3d.Cross(u, t);
        }

        /// <summary>Inverse(q) * v: world direction expressed in this rotation's local frame.</summary>
        public Vec3d InverseRotate(Vec3d v)
        {
            return new QuatD(-X, -Y, -Z, W).Rotate(v);
        }
    }

    public static class SonaMath
    {
        public const double TwoPi = 2.0 * Math.PI;
        public const double Deg2Rad = Math.PI / 180.0;
        public const double Rad2Deg = 180.0 / Math.PI;

        public static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        public static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        public static double Clamp01(double v)
        {
            return Clamp(v, 0.0, 1.0);
        }

        /// <summary>Python's float modulo: the result takes the sign of the divisor.</summary>
        public static double FloorMod(double x, double m)
        {
            double r = x % m;
            if (r != 0.0 && ((m < 0.0) != (r < 0.0)))
            {
                r += m;
            }
            return r;
        }

        /// <summary>Wrap an angle to [-pi, pi) exactly like the Python prototype.</summary>
        public static double WrapPi(double angle)
        {
            return FloorMod(angle + Math.PI, TwoPi) - Math.PI;
        }

        public static double Hypot(double a, double b)
        {
            return Math.Sqrt(a * a + b * b);
        }

        /// <summary>Clockwise-from-forward azimuth of a direction (+ = right), Python atan2(x, forward).</summary>
        public static double Azimuth(Vec3d direction)
        {
            return Math.Atan2(direction.X, direction.Z);
        }

        public static double DbToGain(double db)
        {
            return Math.Pow(10.0, db / 20.0);
        }
    }
}
