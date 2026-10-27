// SONA Quest port: HRTF renderer. Plays clustered grains from their real world positions through
// Unity AudioSources spatialized by the project's spatializer plugin (Meta XR Audio), so the
// binaural cues, including elevation and front/back, come from a measured HRTF and update with
// every head movement.
//
// Each voice owns one AudioClip. A voice plays one timeline per sweep (several clusters merged,
// silence between them) and is moved to the next cluster's position as soon as the previous one
// has finished. Allocation guarantees a voice never needs two scheduled plays at once.
using System.Collections.Generic;
using Sona.Core;
using UnityEngine;

namespace Sona
{
    [AddComponentMenu("SONA/SONA HRTF Renderer")]
    public sealed class SonaHrtfRenderer : MonoBehaviour
    {
        [Tooltip("AudioSources in the pool. Keep below the Meta XR Audio voice limit (64 by default) and Unity's Max Real Voices.")]
        [Range(8, 64)] public int voiceCount = 56;

        [Tooltip("Extra gain to level-match the HRTF path with the stereo path (calibrate by ear or meter).")]
        [Range(-24f, 24f)] public float gainDb = 0f;

        [Tooltip("Time reserved to write a voice's clip before its next play starts.")]
        public float uploadLeadSeconds = 0.06f;

        [Tooltip("Clips are written only this far ahead of their start time.")]
        public float uploadHorizonSeconds = 0.3f;

        sealed class Voice
        {
            public AudioSource Source;
            public AudioClip Clip;
            public int ClipSamples;
            public HrtfVoicePlay Current;
            public readonly Queue<HrtfVoicePlay> Pending = new Queue<HrtfVoicePlay>();
        }

        Voice[] _voices;
        HrtfVoiceAllocator _allocator;
        int _sampleRate;
        float _volume = 1f;

        public int DroppedClusters { get; private set; }
        public int VoiceCount { get { return _voices != null ? _voices.Length : 0; } }

        public float Volume { set { _volume = Mathf.Clamp01(value); } }

        void Awake()
        {
            _sampleRate = AudioSettings.outputSampleRate;
            _voices = new Voice[voiceCount];
            for (int i = 0; i < voiceCount; i++)
            {
                var go = new GameObject("SONA HRTF Voice " + i);
                go.transform.SetParent(transform, false);
                var src = go.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.loop = false;
                src.spatialBlend = 1f;
                src.spatialize = true;            // rendered by the project's spatializer plugin (Meta XR Audio)
                src.dopplerLevel = 0f;
                src.spread = 0f;
                src.priority = 32;
                // SONA's encoder already sets loudness from distance (near emphasis), so disable
                // Unity's distance roll-off with a flat custom curve.
                src.rolloffMode = AudioRolloffMode.Custom;
                src.SetCustomCurve(AudioSourceCurveType.CustomRolloff, AnimationCurve.Constant(0f, 1f, 1f));
                src.minDistance = 1f;
                src.maxDistance = 1000f;
                _voices[i] = new Voice { Source = src };
            }
            _allocator = new HrtfVoiceAllocator(voiceCount);
        }

        /// <summary>Schedule a sweep's clusters starting at <paramref name="startDsp"/>.</summary>
        public void Play(int sweepId, HrtfClusterResult clusters, double startDsp, bool stopCurrent)
        {
            if (stopCurrent) Stop();
            int dropped;
            List<HrtfVoicePlay> plays = _allocator.Allocate(clusters, sweepId, startDsp, 0.01, uploadLeadSeconds, out dropped);
            DroppedClusters = dropped;
            for (int i = 0; i < plays.Count; i++)
            {
                _voices[plays[i].Voice].Pending.Enqueue(plays[i]);
            }
            Pump();
        }

        /// <summary>Remove a sweep that has not started yet (repeat switched off); the playing one finishes.</summary>
        public void CancelSweep(int sweepId)
        {
            if (_voices == null) return;
            double now = AudioSettings.dspTime;
            for (int i = 0; i < _voices.Length; i++)
            {
                Voice v = _voices[i];
                if (v.Pending.Count > 0)
                {
                    var keep = new Queue<HrtfVoicePlay>();
                    foreach (HrtfVoicePlay p in v.Pending)
                    {
                        if (p.SweepId != sweepId) keep.Enqueue(p);
                    }
                    v.Pending.Clear();
                    foreach (HrtfVoicePlay p in keep) v.Pending.Enqueue(p);
                }
                if (v.Current != null && v.Current.SweepId == sweepId && v.Current.StartDsp > now)
                {
                    v.Source.Stop();
                    v.Current = null;
                }
                double busyUntil = v.Current != null ? v.Current.EndDsp : double.NegativeInfinity;
                foreach (HrtfVoicePlay p in v.Pending) busyUntil = System.Math.Max(busyUntil, p.EndDsp);
                _allocator.SetEnd(i, busyUntil);
            }
        }

        public void Stop()
        {
            if (_voices == null) return;
            for (int i = 0; i < _voices.Length; i++)
            {
                Voice v = _voices[i];
                v.Pending.Clear();
                v.Current = null;
                if (v.Source.isPlaying) v.Source.Stop();
            }
            _allocator.Reset();
        }

        void Update()
        {
            Pump();
        }

        void Pump()
        {
            if (_voices == null) return;
            double now = AudioSettings.dspTime;
            float gain = Mathf.Pow(10f, gainDb / 20f) * _volume;
            for (int i = 0; i < _voices.Length; i++)
            {
                Voice v = _voices[i];
                // Finished?
                if (v.Current != null && now >= v.Current.EndDsp)
                {
                    v.Current = null;
                }
                // Upload and schedule the next timeline once the voice is idle and the play is near
                // (spreads clip uploads over the sweep instead of one burst at the trigger).
                if (v.Current == null && v.Pending.Count > 0 && v.Pending.Peek().StartDsp - now < uploadHorizonSeconds)
                {
                    HrtfVoicePlay play = v.Pending.Dequeue();
                    if (play.EndDsp <= now) continue;   // entirely in the past; skip it
                    Upload(v, play, gain);
                    v.Current = play;
                }
                // Follow the timeline: sit at the current cluster, or the next one during gaps.
                if (v.Current != null)
                {
                    List<HrtfSegment> segs = v.Current.Segments;
                    for (int s = 0; s < segs.Count; s++)
                    {
                        if (segs[s].EndDsp > now)
                        {
                            v.Source.transform.position = segs[s].Position.ToVector3();
                            break;
                        }
                    }
                }
            }
        }

        void Upload(Voice v, HrtfVoicePlay play, float gain)
        {
            int n = play.Samples.Length;
            if (v.Clip == null || v.ClipSamples < n)
            {
                if (v.Clip != null) Destroy(v.Clip);
                int size = Mathf.Max(n, (int)(_sampleRate * 1.5f));
                v.Clip = AudioClip.Create("SONA HRTF " + v.Source.name, size, 1, _sampleRate, false);
                v.ClipSamples = size;
                v.Source.clip = v.Clip;
            }
            float[] data = play.Samples;   // each play is uploaded once, so scale in place (no garbage)
            if (!Mathf.Approximately(gain, 1f))
            {
                for (int i = 0; i < n; i++) data[i] *= gain;
            }
            v.Clip.SetData(data, 0);
            if (play.Segments.Count > 0) v.Source.transform.position = play.Segments[0].Position.ToVector3();
            v.Source.PlayScheduled(play.StartDsp);
            v.Source.SetScheduledEndTime(play.EndDsp);
        }
    }
}
