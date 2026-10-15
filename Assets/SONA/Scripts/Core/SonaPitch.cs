// SONA Quest port: pitch mapping (spatializer.py elevation_to_freq / surface_to_freq).
// Non-floor elevation -> pitch, quantized to a major pentatonic scale so overlapping
// grains stay consonant. The floor is a fixed low carrier (E2).
using System;
using System.Collections.Generic;

namespace Sona.Core
{
    public static class SonaPitch
    {
        public static readonly int[] Pentatonic = { 0, 2, 4, 7, 9 };   // major pentatonic (semitones)
        public const double FloorFrequency = 82.4068892282;              // E2
        public const double BaseFrequency = 196.0;                       // G3 at the lowest elevation
        public const double Octaves = 2.0;
        public static readonly double HalfSpan = 45.0 * SonaMath.Deg2Rad;

        /// <summary>Low (looking down) -> low pitch; high -> high pitch. Quantized to pentatonic.</summary>
        public static double ElevationToFreq(double elevation, double baseFreq = BaseFrequency,
                                             double octaves = Octaves, double halfSpan = double.NaN)
        {
            if (double.IsNaN(halfSpan)) halfSpan = HalfSpan;
            double x = SonaMath.Clamp((elevation + halfSpan) / (2.0 * halfSpan), 0.0, 1.0);
            double semis = x * octaves * 12.0;
            // Python divmod(semis, 12.0) for non-negative semis
            double octave = Math.Floor(semis / 12.0);
            double within = SonaMath.FloorMod(semis, 12.0);
            // min(PENTATONIC, key=|d - within|): first minimum wins ties
            int nearest = Pentatonic[0];
            double best = Math.Abs(Pentatonic[0] - within);
            for (int i = 1; i < Pentatonic.Length; i++)
            {
                double d = Math.Abs(Pentatonic[i] - within);
                if (d < best)
                {
                    best = d;
                    nearest = Pentatonic[i];
                }
            }
            return baseFreq * Math.Pow(2.0, (octave * 12.0 + nearest) / 12.0);
        }

        /// <summary>Ordinary surfaces use the elevation melody; the floor is a fixed low carrier.</summary>
        public static double SurfaceToFreq(double elevation, string material, double floorFrequency = FloorFrequency)
        {
            if (SonaMaterials.IsFloor(material))
            {
                if (!SonaMath.IsFinite(floorFrequency) || floorFrequency <= 0.0)
                    throw new ArgumentException("floor frequency must be positive and finite");
                return floorFrequency;
            }
            return ElevationToFreq(elevation);
        }

        /// <summary>Every frequency the default mapping can produce (useful for pre-building grains).</summary>
        public static List<double> AllElevationFrequencies()
        {
            var result = new List<double>();
            int maxOctave = (int)Math.Ceiling(Octaves);
            for (int o = 0; o <= maxOctave; o++)
            {
                for (int i = 0; i < Pentatonic.Length; i++)
                {
                    double semis = o * 12 + Pentatonic[i];
                    if (semis > Octaves * 12.0 + 1e-9) continue;
                    result.Add(BaseFrequency * Math.Pow(2.0, semis / 12.0));
                }
            }
            return result;
        }
    }

    /// <summary>
    /// Chooses each event's carrier. ElevationToPitch = true reproduces the Python prototype.
    /// False gives every non-floor surface one fixed pitch, which isolates spatial (HRTF) cues
    /// for the "HRTF only vs HRTF + elevation->pitch" comparison.
    /// </summary>
    [Serializable]
    public sealed class PitchMapper
    {
        public bool ElevationToPitch = true;
        public double FlatFrequency = 392.0;     // G4, the middle of the elevation melody
        public double FloorFrequency = SonaPitch.FloorFrequency;

        public double FrequencyOf(ScheduledReturn e, out bool isFloor)
        {
            isFloor = SonaMaterials.IsFloor(e.Material);
            if (isFloor) return FloorFrequency;
            return ElevationToPitch ? SonaPitch.ElevationToFreq(e.Elevation) : FlatFrequency;
        }
    }
}
