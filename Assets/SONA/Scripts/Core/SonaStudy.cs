// SONA Quest port: stimuli for the Proposal 1.2 and 1.3 studies (does the learned sweep cue combine with DRR?).
//
// One box waveform is rendered once and reused, because re-encoding the box at each distance would also
// change its duration and level. Each interval is a click at Lead, then the box at Lead + onset:
//     s * (a_d * Dry + a_r * Wet),   a_d / a_r = 10^(DRR/20)
// Wet is Dry convolved with frozen, exponentially decaying noise (unit energy, independent per ear), so the
// kernel's DRR is exactly the requested one. s scales the box to TargetRms and then applies the interval's
// roving gain, which the click shares. The sweep matches Explore's default room: a 5 x 6 m floor in front of the
// listener, with the wave reaching its far corner (6.5 m) at the end of the sweep. With Ground on, intervals with a
// click also play that floor's low sweep from the click on, at its level relative to the dry box in the app's own mix.
// Moves are unit vectors in (x_S, x_R) = (ln delay distance, ln DRR distance) relative to 2 m, so a size is the length
// of the move. Proposal 1.3's frozen bank (StudyBank) replaces the staircases in the main study; the staircases stay for
// the pilot. The plan with sources: AudioSweep/SONA Proposal 1.2 implementation plan.md; the 1.3 Protocol tab.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Sona.Core
{
    [Serializable]
    public sealed class StudyParams
    {
        public int SampleRate = 48000;          // the headset's rate (ProjectSettings > Audio); the app passes the real one
        public double SweepDuration = 1.2;      // s: the delay at Far
        public double FloorWidth = 5.0;         // m: Explore's default room, listener at the middle of its near edge
        public double FloorDepth = 6.0;         // m
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
        public bool Ground;                     // also play SONA's ground sound in intervals with a click

        // Proposal 1.3. Sizes and k* are placeholders until the pilot sets them; then they are frozen for the study.
        public double OpposingRatio = 2.0;                       // k*: pilot geometric mean of T_R / T_S; opposing uses k*/2, k*, 2k*
        public double[] SweepAxisSizes = { 0.2, 0.4, 0.6 };     // ln units along the direction
        public double[] DrrAxisSizes = { 0.5, 1.0, 1.5 };
        public double[] PairedSizes = { 0.3, 0.6, 0.9 };        // matching and all three opposing ratios, so they compare at equal size
        public double[] BankReferences = { -0.10, 0.0, 0.10 };  // ln offsets of the standard from ReferenceDistance
        public int BankRepeats = 2;
        public ulong BankSeed = 13;                               // the bank's frozen gains
        public int CheckTargets = 12;                             // distance checks per cue
        public int PairsPerCue = 30;                              // pilot "which is farther?" pairs
        public double PairStep = 0.6931;                          // ln ratio inside a pilot pair; placeholder: a factor of 2

        /// <summary>Where the wave ends: the floor's far corner (6.5 m), as Explore sizes its sweep to the farthest point.</summary>
        public double Far { get { return SonaMath.Hypot(FloorWidth / 2.0, FloorDepth); } }

        public EncodingParams Encoding()
        {
            // Near is Explore's (SonaSweepController.near), so the ground and the box keep Explore's loudness.
            return new EncodingParams { SweepDuration = SweepDuration, Far = Far, FoveationExponent = FoveationExponent, Near = 0.4 };
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
        public bool Ground;           // the ground sound from the click on; only with a click, or it would mark the onset

        public long ClickSample;      // frames from the interval start; -1 without a click
        public long BoxSample;
        public double SignalDrrDb;    // DRR of the rendered box, which differs a little from the kernel's
        public double BoxRms;         // measured from the output, roving included
        public double Peak;
    }

    /// <summary>A design cell: the odd interval moves by sign * delta * (KDelay, KDrr), a unit vector, in ln distance.</summary>
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
            new StudyCondition { Name = "congruent", KDelay = Math.Sqrt(0.5), KDrr = Math.Sqrt(0.5), Room = true, Click = true },
            new StudyCondition { Name = "opposite", KDelay = Math.Sqrt(0.5), KDrr = -Math.Sqrt(0.5), Room = true, Click = true },
        };
    }

    public sealed class StudyStimulus
    {
        public readonly StudyParams Params;
        public readonly double[] Dry;        // interleaved stereo box, from its first grain
        public readonly double[] Wet;        // Dry convolved with the tail, gap included: BoxFrames frames
        public readonly double[][] Tail;     // per ear: unit-energy decaying noise
        public readonly double[] Click;      // mono
        public readonly double[] Floor;      // interleaved stereo ground sound, from the start of the sweep (the click)
        public readonly int DryFrames, BoxFrames, LeadFrames, IntervalFrames;
        public readonly double DryEnergy, WetEnergy, CrossEnergy;
        public readonly double FloorScale;   // brings the ground sound to its app level relative to the dry box at TargetRms

        public StudyStimulus(StudyParams p)
        {
            if (p.ReferenceDistance * Math.Exp(p.StandardJitter) + 1.0 > p.Far)
                throw new ArgumentException("the 1 m box must fit inside the sweep at the farthest standard");
            Params = p;
            int sr = p.SampleRate;
            var bank = new GrainBank(new GranularParams { SampleRate = sr });
            Dry = RenderBox(p, bank);
            Floor = RenderFloor(p, bank);
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
            FloorScale = p.TargetRms * Math.Sqrt(2.0 * BoxFrames / DryEnergy);
            if (LeadFrames + Floor.Length / 2 > IntervalFrames) throw new ArgumentException("the ground sound must fit the interval");
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
            iv.BoxRms = Math.Sqrt(sum / Wet.Length);   // the box alone, before the ground sound joins it

            if (iv.Ground)
            {
                long f = o + LeadFrames * 2L;
                for (int i = 0; i < Floor.Length; i++) output[f + i] += (float)(g * FloorScale * Floor[i]);
            }
            for (long i = o; i < o + IntervalFrames * 2L; i++) peak = Math.Max(peak, Math.Abs(output[i]));
            iv.Peak = peak;
        }

        static ListenerPose3D Listener(StudyParams p) { return new ListenerPose3D(new Vec3d(0.0, p.EarHeight, 0.0), 0.0); }

        // SONA's grains and panning before its mix gain and peak limit, so the box and the ground keep their relative level.
        static double[] Mix(List<ScheduledReturn> plan, GrainBank bank, double sweepDuration)
        {
            int frames = bank.Params.BufferFrames(sweepDuration, bank.Length);
            var buf = new double[frames * 2];
            GranularRenderer.MixInto(plan, bank, new PitchMapper(), buf, frames);
            return buf;
        }

        // A 1 m box on the floor, straight ahead, near face at the reference distance, through SONA's own pipeline.
        static double[] RenderBox(StudyParams p, GrainBank bank)
        {
            var quads = new List<SurfaceQuad>();
            double near = p.ReferenceDistance;
            SonaPrimitives.AppendBoxQuads(quads, q => new Vec3d(q.X, q.Y + 0.5, q.Z + near + 0.5),
                                          new Vec3d(1, 0, 0), new Vec3d(0, 1, 0), new Vec3d(0, 0, 1), "box", null);
            EncodingParams ep = p.Encoding();
            List<ScheduledReturn> plan = SonaEncoder.Encode(SonaFrontend.SurfaceEmission(quads, Listener(p), 5.0, p.Far), ep);
            if (plan.Count == 0) throw new ArgumentException("the box produced no returns");
            double[] full = Mix(plan, bank, ep.SweepDuration);
            long first = (long)(plan[0].Onset * p.SampleRate);
            long last = full.Length / 2 - 1;
            while (last > first && full[last * 2] == 0.0 && full[last * 2 + 1] == 0.0) last--;
            var box = new double[(last - first + 1) * 2];
            Array.Copy(full, first * 2, box, 0, box.Length);
            return box;
        }

        // SONA's ground sound: the floor of Explore's default room in front of the listener, sounding the low floor tone as
        // the wave crosses it. No occlusion by the box, like the rest of SONA (X-ray).
        static double[] RenderFloor(StudyParams p, GrainBank bank)
        {
            double x = p.FloorWidth / 2.0, z = p.FloorDepth;
            var floor = new SurfaceQuad(new Vec3d(-x, 0.0, 0.0), new Vec3d(x, 0.0, 0.0), new Vec3d(x, 0.0, z), new Vec3d(-x, 0.0, z),
                                        new Vec3d(0, 1, 0), "floor", "floor", SonaMaterials.Floor);
            EncodingParams ep = p.Encoding();
            List<ScheduledReturn> plan = SonaEncoder.Encode(SonaFrontend.SurfaceEmission(new[] { floor }, Listener(p), 5.0, p.Far), ep);
            return Mix(plan, bank, ep.SweepDuration);
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
                    Ground = p.Ground && c.Click,
                };
            }
            return intervals;
        }

        /// <summary>
        /// Training: <paramref name="targets"/> distances evenly spaced in ln distance from MinDistance to Far, each played
        /// once with delay alone (dry) and once with delay and DRR together, in random order (2 * targets trials).
        /// </summary>
        public static StudyInterval[] TrainingSchedule(StudyParams p, int targets, StudyRandom rng)
        {
            if (targets < 1) throw new ArgumentOutOfRangeException("targets", "need at least one target");
            var trials = new StudyInterval[2 * targets];
            for (int i = 0; i < trials.Length; i++)
            {
                double d = Target(p.MinDistance, p.Far, targets, i / 2);
                bool paired = i % 2 == 1;
                trials[i] = new StudyInterval
                {
                    Click = true,
                    DelayDistance = d,
                    Onset = p.DelaySeconds(d),
                    DrrDistance = paired ? d : double.NaN,
                    DrrDb = paired ? p.DrrDb(d) : double.PositiveInfinity,
                    GainDb = rng.Uniform(-p.RoveDb, p.RoveDb),
                    Ground = p.Ground,
                };
            }
            Shuffle(trials, rng);
            return trials;
        }

        /// <summary>
        /// Proposal 1.3's distance checks (no feedback): CheckTargets distances spaced like training, each once with the
        /// sweep cue alone and once with DRR alone, shuffled.
        /// </summary>
        public static StudyInterval[] DistanceChecks(StudyParams p, StudyRandom rng)
        {
            var trials = new StudyInterval[2 * p.CheckTargets];
            for (int i = 0; i < trials.Length; i++)
                trials[i] = Isolated(p, Target(p.MinDistance, p.Far, p.CheckTargets, i / 2), i % 2 == 0, rng);
            Shuffle(trials, rng);
            return trials;
        }

        /// <summary>
        /// The pilot's "which is farther?" pairs: PairsPerCue per cue, PairStep apart in ln distance, the nearer one spaced
        /// evenly so the farther one stays on the floor line, the farther one first in half of each cue's pairs. Shuffled.
        /// </summary>
        public static StudyInterval[][] Pairs(StudyParams p, StudyRandom rng)
        {
            var pairs = new StudyInterval[2 * p.PairsPerCue][];
            for (int i = 0; i < pairs.Length; i++)
            {
                bool sweep = i % 2 == 0;
                double near = Target(p.MinDistance, p.Far * Math.Exp(-p.PairStep), p.PairsPerCue, i / 2);
                StudyInterval a = Isolated(p, near, sweep, rng), b = Isolated(p, near * Math.Exp(p.PairStep), sweep, rng);
                pairs[i] = i / 2 % 2 == 0 ? new[] { b, a } : new[] { a, b };
            }
            Shuffle(pairs, rng);
            return pairs;
        }

        /// <summary>Which of a pilot pair's two intervals is farther by the cue it carries.</summary>
        public static int Farther(StudyInterval[] pair)
        {
            return pair[0].Click ? (pair[0].DelayDistance > pair[1].DelayDistance ? 0 : 1) : (pair[0].DrrDistance > pair[1].DrrDistance ? 0 : 1);
        }

        // One cue at distance d. The sweep cue keeps the click and floor tone with DRR held at the reference; DRR alone has
        // neither and a random onset, so timing says nothing.
        static StudyInterval Isolated(StudyParams p, double d, bool sweep, StudyRandom rng)
        {
            double dDrr = sweep ? p.ReferenceDistance : d;
            return new StudyInterval
            {
                Click = sweep,
                Ground = sweep,
                DelayDistance = sweep ? d : double.NaN,
                Onset = sweep ? p.DelaySeconds(d) : rng.Uniform(p.DelaySeconds(p.MinDistance), p.SweepDuration),
                DrrDistance = dDrr,
                DrrDb = p.DrrDb(dDrr),
                GainDb = rng.Uniform(-p.RoveDb, p.RoveDb),
            };
        }

        // Target i of n, evenly spaced in ln distance between lo and hi (cell centres).
        static double Target(double lo, double hi, int n, int i) { return lo * Math.Exp((i + 0.5) / n * Math.Log(hi / lo)); }

        internal static void Shuffle<T>(T[] a, StudyRandom rng)
        {
            for (int i = a.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                T t = a[i];
                a[i] = a[j];
                a[j] = t;
            }
        }
    }

    /// <summary>One direction of the bank: a unit vector (US, UR) in (x_S, x_R) and its sizes.</summary>
    public struct BankDirection
    {
        public string Name;
        public double US, UR;
        public double K;          // opposing ratio; 0 for the axes and matching
        public double[] Sizes;
    }

    /// <summary>One trial of Proposal 1.3's frozen bank.</summary>
    public sealed class BankTrial
    {
        public int Index;                 // position in the frozen bank, before the visit's shuffle
        public string Direction;
        public double US, UR, K, Size;
        public int Sign, Odd, Repeat;
        public double Reference;          // ln offset of the standard from ReferenceDistance
        public bool Catch;
        public StudyInterval[] Intervals; // three; copy before rendering, so the bank stays as built
    }

    public static class StudyBank
    {
        /// <summary>Sweep axis, DRR axis, matching and the three opposing ratios k*/2, k*, 2k*.</summary>
        public static BankDirection[] Directions(StudyParams p)
        {
            double h = Math.Sqrt(0.5);
            var dirs = new List<BankDirection>
            {
                new BankDirection { Name = "sweep-axis", US = 1.0, UR = 0.0, Sizes = p.SweepAxisSizes },
                new BankDirection { Name = "drr-axis", US = 0.0, UR = 1.0, Sizes = p.DrrAxisSizes },
                new BankDirection { Name = "matching", US = h, UR = h, Sizes = p.PairedSizes },
            };
            foreach (double k in new[] { p.OpposingRatio / 2.0, p.OpposingRatio, 2.0 * p.OpposingRatio })
            {
                double n = Math.Sqrt(1.0 + k * k);
                dirs.Add(new BankDirection { Name = "opposing-" + k.ToString("0.00", CultureInfo.InvariantCulture), US = 1.0 / n, UR = -k / n, K = k, Sizes = p.PairedSizes });
            }
            return dirs.ToArray();
        }

        /// <summary>
        /// The frozen bank: direction x size x sign x odd position x repeat, with the references rotated so every cell and
        /// every odd position hears each reference equally often (not crossed). Then the catch trials: both axes, both signs,
        /// every odd position, at the largest move the sounds allow from the middle reference. Gains come from BankSeed.
        /// Throws if any trial would leave the sweep or the DRR range, so a pilot setting that does not fit is caught at once.
        /// </summary>
        public static BankTrial[] Build(StudyParams p)
        {
            var rng = new StudyRandom(p.BankSeed);
            var trials = new List<BankTrial>();
            BankDirection[] dirs = Directions(p);
            int cell = 0, refs = p.BankReferences.Length;
            foreach (BankDirection d in dirs)
                foreach (double size in d.Sizes)
                    foreach (int sign in new[] { 1, -1 })
                    {
                        for (int rep = 0; rep < p.BankRepeats; rep++)
                            for (int odd = 0; odd < 3; odd++)
                                trials.Add(Make(p, d, size, sign, odd, rep, p.BankReferences[(odd + rep + cell) % refs], false, trials.Count, rng));
                        cell++;
                    }
            double ln10 = Math.Log(10.0);
            for (int axis = 0; axis < 2; axis++)
                foreach (int sign in new[] { 1, -1 })
                {
                    double size = axis == 0
                        ? (sign > 0 ? Math.Log(p.Far / p.ReferenceDistance) : Math.Log(p.ReferenceDistance / p.MinDistance))
                        : (sign > 0 ? p.DrrAtReference - p.MinDrrDb : p.MaxDrrDb - p.DrrAtReference) / 20.0 * ln10;
                    for (int odd = 0; odd < 3; odd++) trials.Add(Make(p, dirs[axis], size, sign, odd, 0, 0.0, true, trials.Count, rng));
                }
            return trials.ToArray();
        }

        /// <summary>The bank in one visit's order.</summary>
        public static BankTrial[] Shuffled(BankTrial[] bank, ulong seed)
        {
            var order = (BankTrial[])bank.Clone();
            StudyTrials.Shuffle(order, new StudyRandom(seed));
            return order;
        }

        static BankTrial Make(StudyParams p, BankDirection d, double size, int sign, int odd, int rep, double reference, bool isCatch,
                              int index, StudyRandom rng)
        {
            var t = new BankTrial
            {
                Index = index, Direction = isCatch ? "catch-" + d.Name : d.Name, US = d.US, UR = d.UR, K = d.K, Size = size,
                Sign = sign, Odd = odd, Repeat = rep, Reference = reference, Catch = isCatch, Intervals = new StudyInterval[3],
            };
            double standard = p.ReferenceDistance * Math.Exp(reference);
            const double tol = 1e-9;
            for (int j = 0; j < 3; j++)
            {
                double move = j == odd ? sign * size : 0.0;
                double dS = standard * Math.Exp(move * d.US), dR = standard * Math.Exp(move * d.UR), drr = p.DrrDb(dR);
                if (dS < p.MinDistance * (1.0 - tol) || dS > p.Far * (1.0 + tol) || drr < p.MinDrrDb - tol || drr > p.MaxDrrDb + tol)
                    throw new ArgumentException(string.Format(CultureInfo.InvariantCulture,
                        "bank: {0} at size {1:0.###} ({2}) from reference {3:+0.00;-0.00;0.00} leaves the sweep or the DRR range",
                        t.Direction, size, sign > 0 ? "+" : "-", reference));
                t.Intervals[j] = new StudyInterval
                {
                    Click = true, Ground = true, DelayDistance = dS, Onset = p.DelaySeconds(dS), DrrDistance = dR, DrrDb = drr,
                    GainDb = rng.Uniform(-p.RoveDb, p.RoveDb),
                };
            }
            return t;
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
