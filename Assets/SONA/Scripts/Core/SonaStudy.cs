// SONA Quest port: stimuli for the Proposal 1.2 study (does the learned delay cue fuse with DRR?).
//
// One box waveform is rendered once and reused, because re-encoding the box at each distance would also
// change its duration and level. Each interval is a click at Lead, then the box at Lead + onset:
//     s * (a_d * Dry + a_r * Wet),   a_d / a_r = 10^(DRR/20)
// Wet is Dry convolved with frozen, exponentially decaying noise (unit energy, independent per ear), so the
// kernel's DRR is exactly the requested one. s scales the box to TargetRms and then applies the interval's
// roving gain, which the click shares. The plan with sources: AudioSweep/SONA Proposal 1.2 implementation plan.md.
using System;
using System.Collections.Generic;

namespace Sona.Core
{
    [Serializable]
    public sealed class StudyParams
    {
        public int SampleRate = 44100;
        public double SweepDuration = 1.2;      // s: the delay at Far
        public double Far = 5.0;                // m
        public double FoveationExponent = 0.5;  // delay ~ distance^p; the pilot tunes p to match the two cues
        public double ReferenceDistance = 2.0;  // m: the middle of the standards (near face of the box)
        public double StandardJitter = 0.175;   // ln units either side of the reference (Negen et al. 2023)
        public double DrrAtReference = 5.0;     // dB
        public double MinDrrDb = -10.0;
        public double MaxDrrDb = 20.0;
        public double MinDistance = 0.5;        // m: nearest delay distance
        public double T60 = 0.5;                // s
        public double TailGap = 0.010;          // s: past the 6 ms direct window of Bronkhorst & Houtgast (1999)
        public double Lead = 0.05;              // s of silence before the click
        public double Gap = 0.3;                // s of silence after the longest box
        public double TargetRms = 0.05;         // box RMS over BoxFrames, before roving
        public double RoveDb = 5.0;             // each interval's gain is uniform in +-RoveDb
        public double ClickAmplitude = 0.5;
        public double EarHeight = 1.2;          // m, seated
        public ulong NoiseSeed = 1;

        public EncodingParams Encoding()
        {
            return new EncodingParams { SweepDuration = SweepDuration, Far = Far, FoveationExponent = FoveationExponent };
        }

        /// <summary>SONA's delay: when the wavefront reaches a box whose near face is this far away.</summary>
        public double DelaySeconds(double distance) { return SonaEncoder.FoveatedOnset(distance, Encoding()); }

        /// <summary>Diffuse-field model: direct sound falls 6.02 dB per doubling, reverberation stays constant.</summary>
        public double DrrDb(double distance) { return DrrAtReference - 20.0 * Math.Log10(distance / ReferenceDistance); }
    }

    /// <summary>One interval of a trial: what to play, then (after Render) what was played.</summary>
    public struct StudyInterval
    {
        public double DelayDistance;  // m; NaN when there is no click (the onset is jittered instead)
        public double DrrDistance;    // m; NaN when dry
        public double Onset;          // s from the click to the box
        public double DrrDb;          // +infinity = dry (no room)
        public bool Click;
        public double GainDb;

        public long ClickSample;      // frames from the interval start; -1 without a click
        public long BoxSample;
        public double SignalDrrDb;    // DRR of the rendered box, which differs a little from the kernel's
        public double BoxRms;         // measured from the output, roving included
        public double Peak;
    }

    /// <summary>A design cell: the odd interval moves by sign * delta * (KDelay, KDrr) in ln distance.</summary>
    public struct StudyCondition
    {
        public string Name;
        public double KDelay;
        public double KDrr;
        public bool Room;    // false: dry, isolated delay
        public bool Click;   // false: no click and a jittered onset, isolated DRR

        public static readonly StudyCondition[] All =
        {
            new StudyCondition { Name = "delay-only", KDelay = 1, KDrr = 0, Room = false, Click = true },
            new StudyCondition { Name = "drr-only", KDelay = 0, KDrr = 1, Room = true, Click = false },
            new StudyCondition { Name = "delay-axis", KDelay = 1, KDrr = 0, Room = true, Click = true },
            new StudyCondition { Name = "drr-axis", KDelay = 0, KDrr = 1, Room = true, Click = true },
            new StudyCondition { Name = "congruent", KDelay = 1, KDrr = 1, Room = true, Click = true },
            new StudyCondition { Name = "opposite", KDelay = 1, KDrr = -1, Room = true, Click = true },
        };
    }

    public sealed class StudyStimulus
    {
        public readonly StudyParams Params;
        public readonly double[] Dry;        // interleaved stereo box, from its first grain
        public readonly double[] Wet;        // Dry convolved with the tail, gap included: BoxFrames frames
        public readonly double[][] Tail;     // per ear: unit-energy decaying noise
        public readonly double[] Click;      // mono
        public readonly int DryFrames, BoxFrames, LeadFrames, IntervalFrames;
        public readonly double DryEnergy, WetEnergy, CrossEnergy;

        public StudyStimulus(StudyParams p)
        {
            if (p.ReferenceDistance * Math.Exp(p.StandardJitter) + 1.0 > p.Far)
                throw new ArgumentException("the 1 m box must fit inside the sweep at the farthest standard");
            Params = p;
            int sr = p.SampleRate;
            Dry = RenderBox(p);
            DryFrames = Dry.Length / 2;
            int gap = (int)Math.Round(p.TailGap * sr);
            int tail = (int)Math.Ceiling(p.T60 * sr);
            var rng = new StudyRandom(p.NoiseSeed);
            Tail = new[] { DecayingNoise(tail, p.T60, sr, rng), DecayingNoise(tail, p.T60, sr, rng) };
            BoxFrames = DryFrames + gap + tail - 1;
            Wet = new double[BoxFrames * 2];
            // shortcut: direct convolution, about 0.6 G multiply-adds once per session; switch to FFT if Quest startup feels slow.
            for (int ch = 0; ch < 2; ch++)
            {
                double[] n = Tail[ch];
                for (int i = 0; i < DryFrames; i++)
                {
                    double x = Dry[i * 2 + ch];
                    if (x == 0.0) continue;
                    int o = (i + gap) * 2 + ch;
                    for (int k = 0; k < tail; k++) Wet[o + k * 2] += x * n[k];
                }
            }
            for (int i = 0; i < Wet.Length; i++)
            {
                double d = i < Dry.Length ? Dry[i] : 0.0;
                DryEnergy += d * d;
                WetEnergy += Wet[i] * Wet[i];
                CrossEnergy += d * Wet[i];
            }
            Click = MakeClick(sr);
            LeadFrames = (int)Math.Round(p.Lead * sr);
            IntervalFrames = LeadFrames + (int)Math.Ceiling(p.SweepDuration * sr) + BoxFrames + (int)Math.Round(p.Gap * sr);
        }

        /// <summary>Three (or any number of) intervals back to back, each IntervalFrames long, as interleaved stereo.</summary>
        public float[] RenderTrial(StudyInterval[] intervals)
        {
            var output = new float[(long)intervals.Length * IntervalFrames * 2];
            for (int i = 0; i < intervals.Length; i++) Render(ref intervals[i], output, i * IntervalFrames);
            return output;
        }

        /// <summary>Add one interval to <paramref name="output"/> at <paramref name="frameOffset"/> and fill its record.</summary>
        public void Render(ref StudyInterval iv, float[] output, int frameOffset)
        {
            StudyParams p = Params;
            bool dry = double.IsPositiveInfinity(iv.DrrDb);
            double ad = dry ? 1.0 : Math.Pow(10.0, iv.DrrDb / 20.0);
            double ar = dry ? 0.0 : 1.0;
            double energy = ad * ad * DryEnergy + ar * ar * WetEnergy + 2.0 * ad * ar * CrossEnergy;
            double g = Math.Pow(10.0, iv.GainDb / 20.0);
            double s = g * p.TargetRms * Math.Sqrt(2.0 * BoxFrames / energy);
            iv.ClickSample = iv.Click ? LeadFrames : -1;
            iv.BoxSample = LeadFrames + (long)Math.Round(iv.Onset * p.SampleRate);
            if (!(iv.Onset >= 0.0) || iv.BoxSample + BoxFrames > IntervalFrames)
                throw new ArgumentOutOfRangeException("iv", "onset must be between 0 and the sweep duration");

            long o = (long)frameOffset * 2;
            if (iv.Click)
            {
                for (int i = 0; i < Click.Length; i++)
                {
                    float v = (float)(g * p.ClickAmplitude * Click[i]);
                    output[o + (LeadFrames + i) * 2] += v;
                    output[o + (LeadFrames + i) * 2 + 1] += v;
                }
            }
            long b = o + iv.BoxSample * 2;
            for (int i = 0; i < Wet.Length; i++)
                output[b + i] += (float)(s * (ad * (i < Dry.Length ? Dry[i] : 0.0) + ar * Wet[i]));

            iv.SignalDrrDb = dry ? double.PositiveInfinity : 10.0 * Math.Log10(ad * ad * DryEnergy / (ar * ar * WetEnergy));
            double sum = 0.0, peak = 0.0;
            for (int i = 0; i < Wet.Length; i++) sum += (double)output[b + i] * output[b + i];
            for (long i = o; i < o + IntervalFrames * 2L; i++) peak = Math.Max(peak, Math.Abs(output[i]));
            iv.BoxRms = Math.Sqrt(sum / Wet.Length);
            iv.Peak = peak;
        }

        // A 1 m box on the floor, straight ahead, near face at the reference distance, through SONA's own pipeline.
        static double[] RenderBox(StudyParams p)
        {
            var quads = new List<SurfaceQuad>();
            double near = p.ReferenceDistance;
            SonaPrimitives.AppendBoxQuads(quads, q => new Vec3d(q.X, q.Y + 0.5, q.Z + near + 0.5),
                                          new Vec3d(1, 0, 0), new Vec3d(0, 1, 0), new Vec3d(0, 0, 1), "box", null);
            var listener = new ListenerPose3D(new Vec3d(0.0, p.EarHeight, 0.0), 0.0);
            EncodingParams ep = p.Encoding();
            List<ScheduledReturn> plan = SonaEncoder.Encode(SonaFrontend.SurfaceEmission(quads, listener, 5.0, p.Far), ep);
            if (plan.Count == 0) throw new ArgumentException("the box produced no returns");
            double gain, rawPeak;
            float[] full = GranularRenderer.RenderOffline(plan, ep.SweepDuration, new GrainBank(new GranularParams { SampleRate = p.SampleRate }),
                                                          new PitchMapper(), out gain, out rawPeak);
            long first = (long)(plan[0].Onset * p.SampleRate);
            long last = full.Length / 2 - 1;
            while (last > first && full[last * 2] == 0f && full[last * 2 + 1] == 0f) last--;
            var box = new double[(last - first + 1) * 2];
            for (long i = 0; i < box.Length; i++) box[i] = full[first * 2 + i];
            return box;
        }

        static double[] DecayingNoise(int frames, double t60, int sr, StudyRandom rng)
        {
            var n = new double[frames];
            double energy = 0.0;
            for (int k = 0; k < frames; k++)
            {
                n[k] = rng.NextGaussian() * Math.Exp(-3.0 * Math.Log(10.0) * k / (t60 * sr));   // -60 dB at T60
                energy += n[k] * n[k];
            }
            double scale = 1.0 / Math.Sqrt(energy);
            for (int k = 0; k < frames; k++) n[k] *= scale;
            return n;
        }

        // Negen et al. (2023, p. 607): 5 ms of 2 kHz, first half period at 0.6, decaying to e^-10 from 1.5 periods on.
        static double[] MakeClick(int sr)
        {
            const double f = 2000.0, length = 0.005;
            var c = new double[(int)Math.Round(length * sr)];
            for (int i = 0; i < c.Length; i++)
            {
                double t = i / (double)sr;
                double v = Math.Sin(2.0 * Math.PI * f * t);
                if (t < 0.5 / f) v *= 0.6;
                if (t > 1.5 / f) v *= Math.Exp(-10.0 * (t - 1.5 / f) / (length - 1.5 / f));
                c[i] = v;
            }
            return c;
        }
    }

    public static class StudyTrials
    {
        /// <summary>The largest delta that keeps the odd interval inside the sweep and the DRR range for every standard.</summary>
        public static double MaxDelta(StudyParams p, StudyCondition c, int sign)
        {
            double lnRef = Math.Log(p.ReferenceDistance);
            double lo = lnRef - p.StandardJitter, hi = lnRef + p.StandardJitter;
            double max = double.PositiveInfinity;
            if (c.Click) max = Math.Min(max, Headroom(sign * c.KDelay, lo, hi, Math.Log(p.MinDistance), Math.Log(p.Far)));
            if (c.Room)
            {
                double ln10 = Math.Log(10.0);
                max = Math.Min(max, Headroom(sign * c.KDrr, lo, hi,
                                             lnRef + (p.DrrAtReference - p.MaxDrrDb) / 20.0 * ln10,
                                             lnRef + (p.DrrAtReference - p.MinDrrDb) / 20.0 * ln10));
            }
            return max;
        }

        // How far ln(distance) can move along slope k before a standard in [lo, hi] leaves [min, max].
        static double Headroom(double k, double lo, double hi, double min, double max)
        {
            if (k > 0.0) return (max - hi) / k;
            if (k < 0.0) return (lo - min) / -k;
            return double.PositiveInfinity;
        }

        /// <summary>Three intervals sharing one random standard; interval <paramref name="odd"/> moves by sign * delta.</summary>
        public static StudyInterval[] Plan(StudyParams p, StudyCondition c, double delta, int sign, StudyRandom rng, out int odd)
        {
            if (!(delta >= 0.0) || delta > MaxDelta(p, c, sign) + 1e-12)
                throw new ArgumentOutOfRangeException("delta", "delta must be between 0 and MaxDelta");
            double standard = p.ReferenceDistance * Math.Exp(rng.Uniform(-p.StandardJitter, p.StandardJitter));
            odd = rng.Next(3);
            var intervals = new StudyInterval[3];
            for (int j = 0; j < 3; j++)
            {
                double shift = j == odd ? sign * delta : 0.0;
                double dDelay = standard * Math.Exp(shift * c.KDelay);
                double dDrr = standard * Math.Exp(shift * c.KDrr);
                intervals[j] = new StudyInterval
                {
                    Click = c.Click,
                    DelayDistance = c.Click ? dDelay : double.NaN,
                    Onset = c.Click ? p.DelaySeconds(dDelay) : rng.Uniform(p.DelaySeconds(p.MinDistance), p.SweepDuration),
                    DrrDistance = c.Room ? dDrr : double.NaN,
                    DrrDb = c.Room ? p.DrrDb(dDrr) : double.PositiveInfinity,
                    GainDb = rng.Uniform(-p.RoveDb, p.RoveDb),
                };
            }
            return intervals;
        }
    }

    /// <summary>SplitMix64, so the frozen noise and trial plans are identical in the parity harness and on the Quest.</summary>
    public sealed class StudyRandom
    {
        ulong _state;

        public StudyRandom(ulong seed) { _state = seed; }

        public ulong NextUlong()
        {
            ulong z = (_state += 0x9E3779B97F4A7C15UL);
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public double NextDouble() { return (NextUlong() >> 11) * (1.0 / 9007199254740992.0); }   // [0, 1)
        public double Uniform(double a, double b) { return a + (b - a) * NextDouble(); }
        public int Next(int n) { return (int)(NextDouble() * n); }

        public double NextGaussian()
        {
            double u = 1.0 - NextDouble();   // (0, 1], so the log is finite
            return Math.Sqrt(-2.0 * Math.Log(u)) * Math.Cos(2.0 * Math.PI * NextDouble());
        }
    }
}
