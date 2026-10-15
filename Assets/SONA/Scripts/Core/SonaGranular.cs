// SONA Quest port: granular synthesis (spatializer.py GranularBinauralSpatializer).
//
// Every return becomes a soft tonal grain that swells in at its onset and fades out.
// The offline renderer reproduces the Python render() sample-for-sample (same envelope,
// harmonics, ITD/ILD panning, mix gain and peak limiting). The block mixer plays the same
// grains in real time on Unity's audio thread and can re-aim each grain at its own start
// using the live head pose, which keeps the scene world-anchored while the head turns.
using System;
using System.Collections.Generic;

namespace Sona.Core
{
    [Serializable]
    public sealed class GranularParams
    {
        public int SampleRate = 44100;
        public double GrainDuration = 0.14;
        public double Attack = 0.03;
        public double Release = 0.09;
        public double[] Harmonics = { 1.0, 0.4, 0.2 };
        public double[] FloorHarmonics = { 1.0 };
        public double MaxItd = 7e-4;        // s
        public double MaxIldDb = 8.0;
        public double MixGain = 0.025;
        public double PeakLimit = 0.9;

        public void Validate()
        {
            if (SampleRate <= 0) throw new ArgumentException("sample rate must be positive");
            if ((int)(SampleRate * GrainDuration) <= 0) throw new ArgumentException("grain duration must produce at least one sample");
            if (Harmonics == null || Harmonics.Length == 0 || Sum(Harmonics) <= 0) throw new ArgumentException("harmonics must contain positive total energy");
            if (FloorHarmonics == null || FloorHarmonics.Length == 0 || Sum(FloorHarmonics) <= 0) throw new ArgumentException("floor_harmonics must contain positive total energy");
            if (!SonaMath.IsFinite(MixGain) || MixGain <= 0) throw new ArgumentException("mix_gain must be positive and finite");
            if (!SonaMath.IsFinite(PeakLimit) || PeakLimit <= 0 || PeakLimit > 1) throw new ArgumentException("peak_limit must be in (0, 1]");
        }

        internal static double Sum(double[] values)
        {
            double s = 0.0;
            for (int i = 0; i < values.Length; i++) s += values[i];
            return s;
        }

        /// <summary>Python buffer length: int(total_duration * sr) + len(grain) + int(max_itd * sr) + 2.</summary>
        public int BufferFrames(double totalDuration, int grainLength)
        {
            int pad = grainLength + (int)(MaxItd * SampleRate) + 2;
            return (int)(totalDuration * SampleRate) + pad;
        }
    }

    /// <summary>Pre-computed grains (tone * envelope), one per (frequency, floor-profile).</summary>
    public sealed class GrainBank
    {
        public readonly GranularParams Params;
        public readonly int SampleRate;
        public readonly int Length;
        public readonly double[] Envelope;

        readonly Dictionary<long, int> _index = new Dictionary<long, int>();
        readonly List<double[]> _grains = new List<double[]>();
        readonly List<float[]> _grainsF = new List<float[]>();
        readonly object _lock = new object();

        public GrainBank(GranularParams p)
        {
            p.Validate();
            Params = p;
            SampleRate = p.SampleRate;
            int n = (int)(p.SampleRate * p.GrainDuration);
            Length = n;
            var env = new double[n];
            for (int i = 0; i < n; i++) env[i] = 1.0;
            int na = Math.Min(n, (int)(p.SampleRate * p.Attack));
            int nr = Math.Min(n, (int)(p.SampleRate * p.Release));
            for (int i = 0; i < na; i++)
            {
                env[i] *= 0.5 * (1.0 - Math.Cos(Math.PI * i / na));            // soft attack
            }
            for (int i = 0; i < nr; i++)
            {
                env[n - nr + i] *= 0.5 * (1.0 + Math.Cos(Math.PI * i / nr));    // soft release
            }
            Envelope = env;
        }

        static long Key(double freq, bool floor)
        {
            // Python caches by round(freq, 2); frequencies come from a small discrete set.
            return (long)Math.Round(freq * 100.0) * 2 + (floor ? 1 : 0);
        }

        /// <summary>Index of the grain for this carrier, creating it on first use. Thread-safe.</summary>
        public int GetIndex(double freq, bool floor)
        {
            long key = Key(freq, floor);
            lock (_lock)
            {
                int idx;
                if (_index.TryGetValue(key, out idx)) return idx;
                double[] profile = floor ? Params.FloorHarmonics : Params.Harmonics;
                double total = GranularParams.Sum(profile);
                var tone = new double[Length];
                for (int i = 0; i < Length; i++)
                {
                    double t = i / (double)SampleRate;
                    double s = 0.0;
                    for (int k = 0; k < profile.Length; k++)
                    {
                        s += profile[k] * Math.Sin(2.0 * Math.PI * freq * (k + 1) * t);
                    }
                    tone[i] = (s / total) * Envelope[i];
                }
                var toneF = new float[Length];
                for (int i = 0; i < Length; i++) toneF[i] = (float)tone[i];
                idx = _grains.Count;
                _grains.Add(tone);
                _grainsF.Add(toneF);
                _index[key] = idx;
                return idx;
            }
        }

        public double[] Grain(int index) { lock (_lock) { return _grains[index]; } }
        public float[] GrainF(int index) { lock (_lock) { return _grainsF[index]; } }
        public int Count { get { lock (_lock) { return _grains.Count; } } }

        /// <summary>Snapshot of all float grains (for handing to the audio thread).</summary>
        public float[][] SnapshotF()
        {
            lock (_lock) { return _grainsF.ToArray(); }
        }
    }

    /// <summary>Interaural time/level difference panning exactly as spatializer.py.</summary>
    public static class ItdIld
    {
        public static void Compute(double azimuth, double amplitude, GranularParams p,
                                   out long delayL, out long delayR, out double gainL, out double gainR)
        {
            double s = Math.Sin(azimuth);
            double itd = p.MaxItd * s;
            double ild = p.MaxIldDb * s;
            gainL = amplitude * Math.Pow(10.0, -Math.Max(ild, 0.0) / 20.0);
            gainR = amplitude * Math.Pow(10.0, -Math.Max(-ild, 0.0) / 20.0);
            delayL = (long)(Math.Max(itd, 0.0) * p.SampleRate);
            delayR = (long)(Math.Max(-itd, 0.0) * p.SampleRate);
        }
    }

    /// <summary>One plan event prepared for the granular renderers.</summary>
    public struct GrainEvent
    {
        public long OnsetSample;     // int(onset * sr): sample offset from the sweep start
        public int Grain;            // index into the GrainBank
        public float Amplitude;      // encoder amplitude (before mix gain / normalization)
        public double FrozenAzimuth; // azimuth at ping time (relative to heading then)
        public Vec3d WorldPosition;
    }

    public static class GranularRenderer
    {
        /// <summary>Resolve each plan event's grain and sample onset.</summary>
        public static GrainEvent[] PrepareEvents(IList<ScheduledReturn> plan, GrainBank bank, PitchMapper pitch)
        {
            var events = new GrainEvent[plan.Count];
            for (int i = 0; i < plan.Count; i++)
            {
                ScheduledReturn e = plan[i];
                bool floor;
                double freq = pitch.FrequencyOf(e, out floor);
                events[i] = new GrainEvent
                {
                    OnsetSample = (long)(e.Onset * bank.SampleRate),
                    Grain = bank.GetIndex(freq, floor),
                    Amplitude = (float)e.Amplitude,
                    FrozenAzimuth = e.Azimuth,
                    WorldPosition = e.WorldPosition,
                };
            }
            return events;
        }

        /// <summary>
        /// GranularBinauralSpatializer.render: interleaved stereo float32, identical math to Python.
        /// <paramref name="outputGain"/> receives mix_gain times the peak-limit scale, which the live
        /// mixer reuses so both modes play at the same loudness.
        /// </summary>
        public static float[] RenderOffline(IList<ScheduledReturn> plan, double totalDuration, GrainBank bank,
                                            PitchMapper pitch, out double outputGain, out double rawPeak)
        {
            GranularParams p = bank.Params;
            int frames = p.BufferFrames(totalDuration, bank.Length);
            var buf = new double[frames * 2];
            for (int ei = 0; ei < plan.Count; ei++)
            {
                ScheduledReturn ev = plan[ei];
                bool floor;
                double freq = pitch.FrequencyOf(ev, out floor);
                double[] g = bank.Grain(bank.GetIndex(freq, floor));
                long dl, dr;
                double gl, gr;
                ItdIld.Compute(ev.Azimuth, ev.Amplitude, p, out dl, out dr, out gl, out gr);
                long start = (long)(ev.Onset * p.SampleRate);
                long sl = start + dl;
                long sr = start + dr;
                for (int i = 0; i < g.Length; i++)
                {
                    long fl = sl + i;
                    if (fl >= 0 && fl < frames) buf[fl * 2] += gl * g[i];
                    long fr = sr + i;
                    if (fr >= 0 && fr < frames) buf[fr * 2 + 1] += gr * g[i];
                }
            }
            double peak = 0.0;
            for (int i = 0; i < buf.Length; i++)
            {
                buf[i] *= p.MixGain;
                double a = Math.Abs(buf[i]);
                if (a > peak) peak = a;
            }
            rawPeak = peak;
            double scale = 1.0;
            if (peak > p.PeakLimit)
            {
                scale = p.PeakLimit / peak;
                for (int i = 0; i < buf.Length; i++) buf[i] *= scale;
            }
            outputGain = p.MixGain * scale;
            var result = new float[buf.Length];
            for (int i = 0; i < buf.Length; i++) result[i] = (float)buf[i];
            return result;
        }
    }

    public static class GranularGain
    {
        [ThreadStatic] static double[] s_scratch;

        /// <summary>
        /// The mix_gain x peak-limit scale that RenderOffline would apply, computed without keeping the
        /// rendered buffer (reuses a per-thread scratch buffer, so repeated sweeps make no garbage).
        /// </summary>
        public static double Measure(IList<ScheduledReturn> plan, double totalDuration, GrainBank bank, PitchMapper pitch)
        {
            GranularParams p = bank.Params;
            int frames = p.BufferFrames(totalDuration, bank.Length);
            if (s_scratch == null || s_scratch.Length < frames * 2) s_scratch = new double[frames * 2];
            double[] buf = s_scratch;
            Array.Clear(buf, 0, frames * 2);
            for (int ei = 0; ei < plan.Count; ei++)
            {
                ScheduledReturn ev = plan[ei];
                bool floor;
                double freq = pitch.FrequencyOf(ev, out floor);
                double[] g = bank.Grain(bank.GetIndex(freq, floor));
                long dl, dr;
                double gl, gr;
                ItdIld.Compute(ev.Azimuth, ev.Amplitude, p, out dl, out dr, out gl, out gr);
                long start = (long)(ev.Onset * p.SampleRate);
                long sl = start + dl;
                long sr = start + dr;
                for (int i = 0; i < g.Length; i++)
                {
                    long fl = sl + i;
                    if (fl >= 0 && fl < frames) buf[fl * 2] += gl * g[i];
                    long fr = sr + i;
                    if (fr >= 0 && fr < frames) buf[fr * 2 + 1] += gr * g[i];
                }
            }
            double peak = 0.0;
            for (int i = 0; i < frames * 2; i++)
            {
                double a = Math.Abs(buf[i] * p.MixGain);
                if (a > peak) peak = a;
            }
            double scale = peak > p.PeakLimit ? p.PeakLimit / peak : 1.0;
            return p.MixGain * scale;
        }
    }

    /// <summary>Immutable head pose handed from the main thread to the audio thread.</summary>
    public sealed class HeadPoseSnapshot
    {
        public readonly Vec3d Position;
        public readonly QuatD Rotation;

        public HeadPoseSnapshot(Vec3d position, QuatD rotation)
        {
            Position = position;
            Rotation = rotation;
        }

        /// <summary>Head-relative azimuth of a world point (+ = right ear side).</summary>
        public double AzimuthOf(Vec3d worldPoint)
        {
            Vec3d local = Rotation.InverseRotate(worldPoint - Position);
            return Math.Atan2(local.X, local.Z);
        }
    }

    /// <summary>One sweep queued for the real-time mixer. Built off the audio thread, read-only afterwards.</summary>
    public sealed class MixSweep
    {
        public int Id;
        public GrainEvent[] Events;     // sorted by OnsetSample
        public float[][] Grains;        // grain table referenced by Events[i].Grain
        public GranularParams Params;
        public float OutputGain;        // mix_gain * peak-limit scale (from the offline render)
        public long CycleFrames;        // Python buffer length: the gapless loop period
        public bool LiveHeadTracking;   // true: aim each grain with the head pose at its start

        // Audio-thread state
        internal long StartFrame = -1;
        internal int Next;
        internal bool Started;
    }

    /// <summary>
    /// Real-time granular mixer. Render() runs on the audio thread and never allocates.
    /// With LiveHeadTracking off it reproduces RenderOffline exactly (to float precision).
    /// </summary>
    public sealed class StereoBlockMixer
    {
        struct Voice
        {
            public float[] Grain;
            public long StartL;
            public long StartR;
            public float GainL;
            public float GainR;
        }

        enum CommandKind { Play, PlayAfterCurrent, Stop, CancelQueued }

        struct Command
        {
            public CommandKind Kind;
            public MixSweep Sweep;
            public double StartDsp;
        }

        readonly Voice[] _voices;
        int _voiceCount;
        readonly MixSweep[] _sweeps = new MixSweep[8];
        int _sweepCount;
        readonly Command[] _commands = new Command[16];
        int _commandCount;
        readonly object _commandLock = new object();
        readonly object _stateLock = new object();
        readonly int _sampleRate;

        long _clock;                       // frames rendered so far
        MixSweep _lastScheduled;           // most recent sweep given a start frame
        MixSweep _lastStarted;             // most recent sweep whose first frame has been rendered
        int _publishedId = -1;
        double _publishedStartDsp;
        bool _publishedActive;
        int _droppedVoices;

        public StereoBlockMixer(int sampleRate, int maxVoices = 8192)
        {
            _sampleRate = sampleRate;
            _voices = new Voice[maxVoices];
        }

        public int SampleRate { get { return _sampleRate; } }

        /// <summary>Head pose used for live aiming; replace the reference from the main thread each frame.</summary>
        public volatile HeadPoseSnapshot HeadPose;

        /// <summary>Linear master volume (0..1).</summary>
        public volatile float MasterVolume = 1.0f;

        public void Play(MixSweep sweep, double startDsp)
        {
            Enqueue(new Command { Kind = CommandKind.Play, Sweep = sweep, StartDsp = startDsp });
        }

        /// <summary>Start exactly when the current sweep's cycle ends (gapless repeat, as in the Python player).</summary>
        public void PlayAfterCurrent(MixSweep sweep)
        {
            Enqueue(new Command { Kind = CommandKind.PlayAfterCurrent, Sweep = sweep });
        }

        public void Stop()
        {
            Enqueue(new Command { Kind = CommandKind.Stop });
        }

        /// <summary>Drop sweeps that are scheduled but have not started (repeat switched off); the current one finishes.</summary>
        public void CancelQueued()
        {
            Enqueue(new Command { Kind = CommandKind.CancelQueued });
        }

        void Enqueue(Command c)
        {
            lock (_commandLock)
            {
                if (c.Kind == CommandKind.Play || c.Kind == CommandKind.Stop)
                {
                    _commandCount = 0;   // a restart supersedes anything still pending
                }
                if (_commandCount < _commands.Length)
                {
                    _commands[_commandCount++] = c;
                }
            }
        }

        /// <summary>Latest sweep that has actually started (its first frame was rendered), for keeping visuals in sync.</summary>
        public bool TryGetCurrent(out int sweepId, out double startDsp, out bool active)
        {
            lock (_stateLock)
            {
                sweepId = _publishedId;
                startDsp = _publishedStartDsp;
                active = _publishedActive;
                return _publishedId >= 0;
            }
        }

        public int DroppedVoices { get { lock (_stateLock) { return _droppedVoices; } } }

        void ApplyCommands(double blockDsp)
        {
            lock (_commandLock)
            {
                for (int i = 0; i < _commandCount; i++)
                {
                    Command c = _commands[i];
                    switch (c.Kind)
                    {
                        case CommandKind.Stop:
                            _sweepCount = 0;
                            _voiceCount = 0;
                            _lastScheduled = null;
                            _lastStarted = null;
                            break;
                        case CommandKind.CancelQueued:
                        {
                            int keep = 0;
                            for (int s = 0; s < _sweepCount; s++)
                            {
                                if (_sweeps[s].Started) _sweeps[keep++] = _sweeps[s];
                            }
                            for (int s = keep; s < _sweepCount; s++) _sweeps[s] = null;
                            _sweepCount = keep;
                            _lastScheduled = _lastStarted;
                            break;
                        }
                        case CommandKind.Play:
                        {
                            _sweepCount = 0;
                            _voiceCount = 0;
                            _lastStarted = null;
                            long offset = (long)Math.Round((c.StartDsp - blockDsp) * _sampleRate);
                            Schedule(c.Sweep, _clock + Math.Max(0L, offset), blockDsp);
                            break;
                        }
                        case CommandKind.PlayAfterCurrent:
                        {
                            long start = _clock;
                            if (_lastScheduled != null && _lastScheduled.StartFrame >= 0)
                            {
                                start = Math.Max(_clock, _lastScheduled.StartFrame + _lastScheduled.CycleFrames);
                            }
                            Schedule(c.Sweep, start, blockDsp);
                            break;
                        }
                    }
                }
                _commandCount = 0;
            }
        }

        void Schedule(MixSweep sweep, long startFrame, double blockDsp)
        {
            if (sweep == null) return;
            sweep.StartFrame = startFrame;
            sweep.Next = 0;
            sweep.Started = false;
            if (_sweepCount < _sweeps.Length)
            {
                _sweeps[_sweepCount++] = sweep;
            }
            _lastScheduled = sweep;
        }

        /// <summary>
        /// Mix the next block into <paramref name="data"/> (interleaved; channels 0 and 1 receive L/R).
        /// <paramref name="blockDsp"/> is AudioSettings.dspTime for this block.
        /// </summary>
        public void Render(float[] data, int channels, double blockDsp, bool clearFirst)
        {
            int frames = data.Length / channels;
            if (clearFirst) Array.Clear(data, 0, data.Length);
            ApplyCommands(blockDsp);

            long blockStart = _clock;
            long blockEnd = blockStart + frames;
            HeadPoseSnapshot pose = HeadPose;
            int dropped = 0;

            // 0) Publish sweeps whose start falls inside this block.
            for (int s = 0; s < _sweepCount; s++)
            {
                MixSweep sw = _sweeps[s];
                if (!sw.Started && sw.StartFrame < blockEnd)
                {
                    sw.Started = true;
                    _lastStarted = sw;
                    lock (_stateLock)
                    {
                        _publishedId = sw.Id;
                        _publishedStartDsp = blockDsp + (sw.StartFrame - blockStart) / (double)_sampleRate;
                    }
                }
            }

            // 1) Activate grains whose onset falls before the end of this block.
            for (int s = 0; s < _sweepCount; s++)
            {
                MixSweep sw = _sweeps[s];
                GrainEvent[] events = sw.Events;
                while (sw.Next < events.Length && sw.StartFrame + events[sw.Next].OnsetSample < blockEnd)
                {
                    GrainEvent ev = events[sw.Next++];
                    double az = ev.FrozenAzimuth;
                    if (sw.LiveHeadTracking && pose != null)
                    {
                        az = pose.AzimuthOf(ev.WorldPosition);
                    }
                    long dl, dr;
                    double gl, gr;
                    ItdIld.Compute(az, ev.Amplitude, sw.Params, out dl, out dr, out gl, out gr);
                    if (_voiceCount >= _voices.Length)
                    {
                        dropped++;
                        continue;
                    }
                    long start = sw.StartFrame + ev.OnsetSample;
                    _voices[_voiceCount++] = new Voice
                    {
                        Grain = sw.Grains[ev.Grain],
                        StartL = start + dl,
                        StartR = start + dr,
                        GainL = (float)(gl * sw.OutputGain),
                        GainR = (float)(gr * sw.OutputGain),
                    };
                }
            }

            // 2) Mix active voices.
            float master = MasterVolume;
            for (int v = 0; v < _voiceCount; v++)
            {
                Voice voice = _voices[v];
                MixChannel(data, channels, 0, voice.Grain, voice.StartL, voice.GainL * master, blockStart, frames);
                MixChannel(data, channels, 1, voice.Grain, voice.StartR, voice.GainR * master, blockStart, frames);
            }

            // 3) Retire finished voices and sweeps.
            int w = 0;
            for (int v = 0; v < _voiceCount; v++)
            {
                Voice voice = _voices[v];
                long end = Math.Max(voice.StartL, voice.StartR) + voice.Grain.Length;
                if (end > blockEnd) _voices[w++] = voice;
            }
            _voiceCount = w;
            int ws = 0;
            for (int s = 0; s < _sweepCount; s++)
            {
                if (!_sweeps[s].Started || _sweeps[s].Next < _sweeps[s].Events.Length) _sweeps[ws++] = _sweeps[s];
            }
            for (int s = ws; s < _sweepCount; s++) _sweeps[s] = null;
            _sweepCount = ws;

            bool active = _voiceCount > 0 || _sweepCount > 0;
            if (!active && _lastScheduled != null && _lastScheduled.StartFrame + _lastScheduled.CycleFrames > blockEnd)
            {
                active = true;   // still inside the scheduled cycle (silent tail or not started yet)
            }
            _clock = blockEnd;
            lock (_stateLock)
            {
                _publishedActive = active;
                _droppedVoices += dropped;
            }
        }

        static void MixChannel(float[] data, int channels, int channel, float[] grain, long start, float gain,
                               long blockStart, int frames)
        {
            if (channel >= channels) return;
            long from = Math.Max(blockStart, start);
            long to = Math.Min(blockStart + frames, start + grain.Length);
            if (to <= from) return;
            int gi = (int)(from - start);
            int di = (int)(from - blockStart) * channels + channel;
            int count = (int)(to - from);
            for (int i = 0; i < count; i++)
            {
                data[di] += gain * grain[gi + i];
                di += channels;
            }
        }
    }
}
