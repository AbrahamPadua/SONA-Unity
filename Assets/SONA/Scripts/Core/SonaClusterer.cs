// SONA Quest port: grain clustering + voice allocation for HRTF rendering (new; no Python equivalent).
//
// A sweep holds thousands of grains (about 2,300 in the Open Demo room) but the Meta XR
// Audio spatializer renders at most 64 HRTF voices. Grains that start in the same short
// time slot and arrive from the same direction sector are summed into one mono "cluster"
// (keeping every member's own pitch, amplitude and timing) and played from the members'
// amplitude-weighted centroid. If the predicted voice count still exceeds the budget, the
// slot is widened until it fits. Clusters are then packed onto voices so each voice plays
// a single timeline per sweep and only moves between clusters.
using System;
using System.Collections.Generic;

namespace Sona.Core
{
    [Serializable]
    public sealed class ClusterParams
    {
        public double SlotSeconds = 0.06;
        public double AzimuthBinDegrees = 30.0;
        public double ElevationLowDegrees = -20.0;   // three elevation bands: below, level, above
        public double ElevationHighDegrees = 20.0;
        public int VoiceBudget = 56;                 // Meta XR Audio's default global voice limit is 64
        public int MaxWidenSteps = 4;
        public double WidenFactor = 1.5;
        public double GuardSeconds = 0.01;           // silence kept between two clusters on one voice
    }

    public sealed class HrtfCluster
    {
        public double Onset;        // seconds from sweep start
        public long OnsetSample;
        public float[] Samples;     // mono, already scaled by the output gain
        public Vec3d Position;      // world position to play from
        public int Members;
    }

    public sealed class HrtfClusterResult
    {
        public readonly List<HrtfCluster> Clusters = new List<HrtfCluster>();
        public int SampleRate;
        public double SlotSecondsUsed;
        public int PredictedMaxVoices;
    }

    /// <summary>A position change inside one voice's timeline.</summary>
    public struct HrtfSegment
    {
        public double StartDsp;
        public double EndDsp;
        public Vec3d Position;
    }

    /// <summary>One PlayScheduled on one voice: a mono timeline holding one or more clusters.</summary>
    public sealed class HrtfVoicePlay
    {
        public int SweepId;
        public int Voice;
        public double StartDsp;
        public double EndDsp;
        public float[] Samples;
        public readonly List<HrtfSegment> Segments = new List<HrtfSegment>();
    }

    public static class SonaClusterer
    {
        struct Member
        {
            public int PlanIndex;
            public long OnsetSample;
        }

        sealed class Bin
        {
            public readonly List<Member> Members = new List<Member>();
            public long FirstOnsetSample = long.MaxValue;
            public long LastOnsetSample = long.MinValue;
        }

        /// <summary>
        /// Build clusters for one sweep. <paramref name="listener"/> is the ping pose: sectors are centred on
        /// the heading at ping time (so straight ahead is the middle of a sector, not an edge) and fixed in
        /// the world, so a head turn during the sweep cannot reshuffle them.
        /// </summary>
        public static HrtfClusterResult Build(IList<ScheduledReturn> plan, GrainEvent[] events, GrainBank bank,
                                              ListenerPose3D listener, double outputGain, ClusterParams cp)
        {
            int sr = bank.SampleRate;
            long guard = (long)(cp.GuardSeconds * sr);
            double slot = Math.Max(1e-3, cp.SlotSeconds);
            Dictionary<long, Bin> bins = null;
            int predicted = 0;
            for (int attempt = 0; attempt <= cp.MaxWidenSteps; attempt++)
            {
                bins = Assign(plan, events, listener, slot, cp);
                predicted = PredictMaxConcurrency(bins, bank.Length + guard);
                if (predicted <= cp.VoiceBudget) break;
                if (attempt < cp.MaxWidenSteps) slot *= cp.WidenFactor;
            }

            var result = new HrtfClusterResult { SampleRate = sr, SlotSecondsUsed = slot, PredictedMaxVoices = predicted };
            foreach (Bin bin in bins.Values)
            {
                int length = (int)(bin.LastOnsetSample - bin.FirstOnsetSample) + bank.Length;
                var samples = new float[length];
                double wx = 0.0, wy = 0.0, wz = 0.0, wsum = 0.0;
                for (int m = 0; m < bin.Members.Count; m++)
                {
                    Member mem = bin.Members[m];
                    GrainEvent ev = events[mem.PlanIndex];
                    float[] g = bank.GrainF(ev.Grain);
                    int offset = (int)(mem.OnsetSample - bin.FirstOnsetSample);
                    float gain = (float)(ev.Amplitude * outputGain);
                    for (int i = 0; i < g.Length; i++)
                    {
                        samples[offset + i] += gain * g[i];
                    }
                    double a = Math.Max(1e-9, ev.Amplitude);
                    wx += a * ev.WorldPosition.X;
                    wy += a * ev.WorldPosition.Y;
                    wz += a * ev.WorldPosition.Z;
                    wsum += a;
                }
                result.Clusters.Add(new HrtfCluster
                {
                    Onset = bin.FirstOnsetSample / (double)sr,
                    OnsetSample = bin.FirstOnsetSample,
                    Samples = samples,
                    Position = new Vec3d(wx / wsum, wy / wsum, wz / wsum),
                    Members = bin.Members.Count,
                });
            }
            result.Clusters.Sort((a, b) => a.OnsetSample.CompareTo(b.OnsetSample));
            return result;
        }

        static Dictionary<long, Bin> Assign(IList<ScheduledReturn> plan, GrainEvent[] events, ListenerPose3D listener,
                                            double slot, ClusterParams cp)
        {
            var bins = new Dictionary<long, Bin>();
            double width = Math.Max(1.0, cp.AzimuthBinDegrees);
            int azBins = Math.Max(1, (int)Math.Round(360.0 / width));
            for (int i = 0; i < plan.Count; i++)
            {
                ScheduledReturn e = plan[i];
                Vec3d d = e.WorldPosition - listener.Position;
                double r = Math.Max(1e-9, d.Length);
                double rel = SonaMath.WrapPi(Math.Atan2(d.X, d.Z) - listener.Heading) * SonaMath.Rad2Deg;
                double elDeg = Math.Asin(SonaMath.Clamp(d.Y / r, -1.0, 1.0)) * SonaMath.Rad2Deg;
                // Sector 0 is centred straight ahead.
                int azBin = (int)Math.Floor(SonaMath.FloorMod(rel + 0.5 * width, 360.0) / width);
                if (azBin >= azBins) azBin = azBins - 1;
                int elBand = elDeg < cp.ElevationLowDegrees ? 0 : (elDeg <= cp.ElevationHighDegrees ? 1 : 2);
                long slotIndex = (long)Math.Floor(e.Onset / slot);
                long key = (slotIndex * 1024 + azBin) * 4 + elBand;
                Bin bin;
                if (!bins.TryGetValue(key, out bin))
                {
                    bin = new Bin();
                    bins.Add(key, bin);
                }
                long onsetSample = events[i].OnsetSample;
                bin.Members.Add(new Member { PlanIndex = i, OnsetSample = onsetSample });
                if (onsetSample < bin.FirstOnsetSample) bin.FirstOnsetSample = onsetSample;
                if (onsetSample > bin.LastOnsetSample) bin.LastOnsetSample = onsetSample;
            }
            return bins;
        }

        static int PredictMaxConcurrency(Dictionary<long, Bin> bins, long tailSamples)
        {
            var edges = new List<KeyValuePair<long, int>>(bins.Count * 2);
            foreach (Bin b in bins.Values)
            {
                edges.Add(new KeyValuePair<long, int>(b.FirstOnsetSample, +1));
                edges.Add(new KeyValuePair<long, int>(b.LastOnsetSample + tailSamples, -1));
            }
            edges.Sort((a, b) =>
            {
                int c = a.Key.CompareTo(b.Key);
                return c != 0 ? c : a.Value.CompareTo(b.Value);   // ends before starts at the same sample
            });
            int cur = 0, max = 0;
            for (int i = 0; i < edges.Count; i++)
            {
                cur += edges[i].Value;
                if (cur > max) max = cur;
            }
            return max;
        }
    }

    /// <summary>
    /// Packs clusters onto a fixed pool of voices. A voice can only hold one scheduled play at a time,
    /// so a new play may start only after the previous one ended plus an upload lead (time for the
    /// main thread to write the clip). Within a sweep, consecutive clusters on one voice are merged
    /// into one timeline, which needs only a short guard gap between them.
    /// </summary>
    public sealed class HrtfVoiceAllocator
    {
        readonly double[] _endDsp;       // end of the last scheduled play per voice
        public readonly int VoiceCount;

        public HrtfVoiceAllocator(int voices)
        {
            VoiceCount = voices;
            _endDsp = new double[voices];
            Reset();
        }

        public void Reset()
        {
            for (int i = 0; i < _endDsp.Length; i++) _endDsp[i] = double.NegativeInfinity;
        }

        /// <summary>Overwrite a voice's busy-until time (after cancelling queued plays).</summary>
        public void SetEnd(int voice, double endDsp)
        {
            _endDsp[voice] = endDsp;
        }

        public List<HrtfVoicePlay> Allocate(HrtfClusterResult clusters, int sweepId, double sweepStartDsp, double guardSeconds,
                                            double uploadLeadSeconds, out int dropped)
        {
            dropped = 0;
            int sr = clusters.SampleRate;
            var plays = new HrtfVoicePlay[VoiceCount];
            var lastClusterEnd = new double[VoiceCount];
            var pieces = new List<KeyValuePair<long, HrtfCluster>>[VoiceCount];
            for (int c = 0; c < clusters.Clusters.Count; c++)
            {
                HrtfCluster cl = clusters.Clusters[c];
                double start = sweepStartDsp + cl.OnsetSample / (double)sr;
                double end = start + cl.Samples.Length / (double)sr;
                int best = -1;
                double bestFreeAt = double.PositiveInfinity;
                for (int v = 0; v < VoiceCount; v++)
                {
                    double freeAt = plays[v] != null ? lastClusterEnd[v] + guardSeconds : _endDsp[v] + uploadLeadSeconds;
                    if (freeAt <= start && freeAt < bestFreeAt)
                    {
                        best = v;
                        bestFreeAt = freeAt;   // least recently used: longest silent gap before the move
                    }
                }
                if (best < 0)
                {
                    dropped++;
                    continue;
                }
                if (plays[best] == null)
                {
                    plays[best] = new HrtfVoicePlay { SweepId = sweepId, Voice = best, StartDsp = start };
                    pieces[best] = new List<KeyValuePair<long, HrtfCluster>>();
                }
                pieces[best].Add(new KeyValuePair<long, HrtfCluster>(cl.OnsetSample, cl));
                plays[best].Segments.Add(new HrtfSegment { StartDsp = start, EndDsp = end, Position = cl.Position });
                plays[best].EndDsp = end;
                lastClusterEnd[best] = end;
            }

            var result = new List<HrtfVoicePlay>();
            for (int v = 0; v < VoiceCount; v++)
            {
                HrtfVoicePlay play = plays[v];
                if (play == null) continue;
                long first = pieces[v][0].Key;
                long last = 0;
                foreach (var kv in pieces[v]) last = Math.Max(last, kv.Key + kv.Value.Samples.Length);
                var samples = new float[(int)(last - first)];
                foreach (var kv in pieces[v])
                {
                    int offset = (int)(kv.Key - first);
                    float[] src = kv.Value.Samples;
                    for (int i = 0; i < src.Length; i++) samples[offset + i] += src[i];
                }
                play.Samples = samples;
                play.EndDsp = play.StartDsp + samples.Length / (double)sr;
                _endDsp[v] = play.EndDsp;
                result.Add(play);
            }
            result.Sort((a, b) => a.StartDsp.CompareTo(b.StartDsp));
            return result;
        }
    }
}
