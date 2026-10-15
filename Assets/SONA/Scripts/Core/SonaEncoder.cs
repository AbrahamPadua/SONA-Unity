// SONA Quest port: the encoder (encoder.py). The part of the project that is actually new,
// kept deliberately pure: Return3D[] in, RenderPlan (ScheduledReturn[]) out. No audio, no HRTF,
// no hardware, so it survives every front-end and renderer swap unchanged.
//
// Two knobs carry the core ideas:
//   - depth -> time, foveated:  onset = duration * norm(range) ** foveation_exponent
//     An exponent below 1 (0.5 = sqrt) spends more of the sweep on near space.
//   - amplitude: mild near-emphasis plus a raised-cosine peripheral roll-off.
using System;
using System.Collections.Generic;

namespace Sona.Core
{
    public static class SonaEncoder
    {
        static void ValidatePropagation(EncodingParams p)
        {
            if (!(p.Far > 0.0)) throw new ArgumentException("far must be positive");
            if (!(p.SweepDuration > 0.0)) throw new ArgumentException("sweep_duration must be positive");
            if (!(p.FoveationExponent > 0.0)) throw new ArgumentException("foveation_exponent must be positive");
        }

        /// <summary>Map planar distance to sweep time, normalized from the origin to far.</summary>
        public static double FoveatedOnset(double propagationRange, EncodingParams p)
        {
            ValidatePropagation(p);
            double x = SonaMath.Clamp(propagationRange / p.Far, 0.0, 1.0);
            return p.SweepDuration * Math.Pow(x, p.FoveationExponent);
        }

        /// <summary>Inverse of FoveatedOnset: radius of the visual ring at a given time.</summary>
        public static double PropagationRadius(double timeSeconds, EncodingParams p)
        {
            ValidatePropagation(p);
            double t = SonaMath.Clamp(timeSeconds / p.SweepDuration, 0.0, 1.0);
            return p.Far * Math.Pow(t, 1.0 / p.FoveationExponent);
        }

        /// <summary>
        /// Smooth listener-relative gain: 1.0 straight ahead, raised-cosine down to
        /// PeripheralDb attenuation at FieldHalfAngle, and that floor behind the listener.
        /// </summary>
        public static double AngularFoveationGain(double azimuth, EncodingParams p)
        {
            if (!SonaMath.IsFinite(p.FieldHalfAngle) || p.FieldHalfAngle <= 0.0)
                throw new ArgumentException("field_half_angle must be positive and finite");
            if (!SonaMath.IsFinite(p.PeripheralDb) || p.PeripheralDb < 0.0)
                throw new ArgumentException("peripheral_db must be non-negative and finite");
            if (!SonaMath.IsFinite(azimuth))
                throw new ArgumentException("azimuth must be finite");

            double wrapped = SonaMath.WrapPi(azimuth);
            double normalized = SonaMath.Clamp(Math.Abs(wrapped) / p.FieldHalfAngle, 0.0, 1.0);
            double c = Math.Cos(0.5 * Math.PI * normalized);
            double focus = c * c;
            double edgeGain = Math.Pow(10.0, -p.PeripheralDb / 20.0);
            return edgeGain + (1.0 - edgeGain) * focus;
        }

        public static double Amplitude(double range, double azimuth, EncodingParams p)
        {
            if (!(p.Near > 0.0)) throw new ArgumentException("near must be positive");
            double peripheral = AngularFoveationGain(azimuth, p);
            double nearBoost = Math.Pow(p.Near / Math.Max(range, p.Near), p.NearEmphasis);
            return peripheral * nearBoost;
        }

        /// <summary>encoder.encode: schedule every return, then sort near-first (stable), which is the wave.</summary>
        public static List<ScheduledReturn> Encode(IList<Return3D> returns, EncodingParams p, List<ScheduledReturn> output = null)
        {
            ValidatePropagation(p);
            var plan = output ?? new List<ScheduledReturn>(returns.Count);
            plan.Clear();
            for (int i = 0; i < returns.Count; i++)
            {
                Return3D ret = returns[i];
                double propagationRange = ret.PropagationRange;
                if (propagationRange < 0.0 || propagationRange > p.Far)
                {
                    continue;
                }
                plan.Add(new ScheduledReturn
                {
                    Onset = FoveatedOnset(propagationRange, p),
                    Azimuth = ret.Azimuth,
                    Elevation = ret.Elevation,
                    Amplitude = Amplitude(ret.Range, ret.Azimuth, p),
                    PropagationRange = propagationRange,
                    Range = ret.Range,
                    WorldPosition = ret.WorldPosition,
                    SourceId = ret.SourceId,
                    Material = ret.Material,
                });
            }
            StableSort.ByOnset(plan);   // near-first, far-last: the wave
            return plan;
        }
    }
}
