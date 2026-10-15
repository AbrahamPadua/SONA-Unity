// SONA Quest port: the sweep made visible. In the dark world this is the only view of the room:
// each surface point flashes gold when the wave reaches it (brighter = louder in the mix, floor
// points dimmer), then glows cyan and fades out; a ring on the floor shows the wavefront.
// The sweep controller hides it in Challenge mode and during blackout.
using System.Collections.Generic;
using Sona.Core;
using UnityEngine;

namespace Sona
{
    [AddComponentMenu("SONA/SONA Wave Visualizer")]
    public sealed class SonaWaveVisualizer : MonoBehaviour
    {
        [Tooltip("Fallback vertex-colour material if the SONA/Glow shader is unavailable (SONA Wave FX).")]
        public Material effectMaterial;

        [Tooltip("Size of each glowing point (m).")]
        public float dotSize = 0.08f;
        [Tooltip("Width of the wavefront ring on the floor (m).")]
        public float ringThickness = 0.05f;
        [Range(16, 256)] public int ringSegments = 128;
        public float ringHeight = 0.01f;
        public Color litColor = new Color(1f, 0.86f, 0.29f);
        public Color pastColor = new Color(0.33f, 0.86f, 0.91f);
        public Color ringColor = new Color(0.49f, 0.96f, 1f);
        [Tooltip("How long a point flashes gold when the wave reaches it (s, at least one grain).")]
        [Min(0f)] public float flashSeconds = 0.3f;
        [Tooltip("Brightness of the cyan afterglow relative to the flash.")]
        [Range(0f, 1f)] public float afterglowBrightness = 0.55f;
        [Tooltip("How long a point keeps glowing after its flash (s). 0 = keep the whole cloud until the next sweep.")]
        [Min(0f)] public float afterglowSeconds = 2.5f;
        [Tooltip("Brightness of floor points relative to walls and objects.")]
        [Range(0f, 1f)] public float floorBrightness = 0.6f;
        [Tooltip("Scale brightness by each point's loudness in the sweep (shows the foveation).")]
        public bool brightnessFromLoudness = true;

        ParticleSystem _ps;
        ParticleSystem.Particle[] _particles = new ParticleSystem.Particle[0];
        LineRenderer _ring;
        Vector3[] _ringPoints;
        Vector3[] _positions = new Vector3[0];
        float[] _onsets = new float[0];
        float[] _weights = new float[0];
        int _count;
        float _lastOnset;
        EncodingParams _params;
        Vector3 _center;
        double _startDsp = -1;
        float _grainDuration = 0.14f;
        float _cycleSeconds;
        bool _visible = true;
        bool _spherical;

        public bool Visible { get { return _visible; } }

        void Awake()
        {
            Material dot = SonaGlow.Dot;
            Material line = SonaGlow.Line;
            if (dot == null || line == null)
            {
                Material fallback = effectMaterial != null ? effectMaterial : SonaMaterialUtil.FallbackEffect();
                if (dot == null) dot = fallback;
                if (line == null) line = fallback;
            }

            var psGo = new GameObject("Wave Points");
            psGo.transform.SetParent(transform, false);
            _ps = psGo.AddComponent<ParticleSystem>();
            _ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystem.MainModule main = _ps.main;
            main.loop = false;
            main.playOnAwake = false;
            main.maxParticles = 60000;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startSpeed = 0f;
            main.startLifetime = 1e6f;
            main.gravityModifier = 0f;
            ParticleSystem.EmissionModule emission = _ps.emission;
            emission.enabled = false;
            ParticleSystem.ShapeModule shape = _ps.shape;
            shape.enabled = false;
            var pr = psGo.GetComponent<ParticleSystemRenderer>();
            pr.renderMode = ParticleSystemRenderMode.Billboard;
            pr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            pr.receiveShadows = false;
            if (dot != null) pr.sharedMaterial = dot;
            _ps.Play();

            var ringGo = new GameObject("Wave Ring");
            ringGo.transform.SetParent(transform, false);
            _ring = ringGo.AddComponent<LineRenderer>();
            _ring.useWorldSpace = true;
            _ring.loop = true;
            _ring.widthMultiplier = ringThickness;
            _ring.positionCount = ringSegments;
            _ring.startColor = ringColor;
            _ring.endColor = ringColor;
            _ring.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _ring.receiveShadows = false;
            _ring.alignment = LineAlignment.TransformZ;   // lie flat on the floor
            ringGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            if (line != null) _ring.sharedMaterial = line;
            _ring.enabled = false;
            _ringPoints = new Vector3[ringSegments];
        }

        public void SetVisible(bool visible)
        {
            _visible = visible;
            if (!visible) HideAll();
        }

        /// <summary>Start showing a sweep. Positions/onsets are copied, so the plan can be reused.</summary>
        public void Show(IList<ScheduledReturn> plan, EncodingParams p, Vector3 center, double startDsp,
                         float grainDuration, float cycleSeconds, bool spherical = false)
        {
            _spherical = spherical;
            _count = plan.Count;
            if (_positions.Length < _count)
            {
                _positions = new Vector3[_count];
                _onsets = new float[_count];
                _weights = new float[_count];
                _particles = new ParticleSystem.Particle[_count];
            }
            double maxAmp = 0.0;
            for (int i = 0; i < _count; i++)
            {
                if (plan[i].Amplitude > maxAmp) maxAmp = plan[i].Amplitude;
            }
            _lastOnset = 0f;
            for (int i = 0; i < _count; i++)
            {
                ScheduledReturn s = plan[i];
                _positions[i] = s.WorldPosition.ToVector3();
                _onsets[i] = (float)s.Onset;
                if (_onsets[i] > _lastOnset) _lastOnset = _onsets[i];
                float w = 1f;
                if (brightnessFromLoudness && maxAmp > 0.0)
                {
                    w = 0.3f + 0.7f * Mathf.Sqrt((float)(s.Amplitude / maxAmp));
                }
                if (SonaMaterials.IsFloor(s.Material)) w *= floorBrightness;
                _weights[i] = w;
            }
            _params = p.Clone();
            _center = center;
            _startDsp = startDsp;
            _grainDuration = grainDuration;
            _cycleSeconds = cycleSeconds;
        }

        /// <summary>Correct the start time once the audio thread reports when the sweep really began.</summary>
        public void SetStartDsp(double startDsp)
        {
            if (_startDsp >= 0) _startDsp = startDsp;
        }

        public void Clear()
        {
            _startDsp = -1;
            _count = 0;
            HideAll();
        }

        void HideAll()
        {
            if (_ps != null) _ps.SetParticles(_particles, 0);
            if (_ring != null) _ring.enabled = false;
        }

        void LateUpdate()
        {
            if (!_visible || _startDsp < 0 || _params == null) return;
            float elapsed = (float)(AudioSettings.dspTime - _startDsp);
            if (elapsed < 0f)
            {
                HideAll();
                return;
            }

            float grain = Mathf.Max(_grainDuration, 1e-4f);
            float flash = Mathf.Max(grain, flashSeconds);
            // Pull each dot a little toward the eye so surfaces drawn in Create room do not cut it in half.
            Camera eye = Camera.main;
            bool pull = eye != null;
            Vector3 eyePos = pull ? eye.transform.position : Vector3.zero;
            float pullDistance = dotSize * 0.5f;
            int shown = 0;
            for (int i = 0; i < _count; i++)
            {
                float age = elapsed - _onsets[i];
                if (age < 0f) continue;
                Color c;
                float size = dotSize;
                float w = _weights[i];
                if (age < flash)
                {
                    float a = 1f - age / flash;
                    c = Color.Lerp(pastColor * afterglowBrightness, litColor, a) * w;
                    size = dotSize * (1f + 0.35f * a);
                }
                else
                {
                    float fade = 1f;
                    if (afterglowSeconds > 0f)
                    {
                        fade = 1f - (age - flash) / afterglowSeconds;
                        if (fade <= 0f) continue;
                    }
                    c = pastColor * (afterglowBrightness * w * fade);
                }
                c.a = 1f;
                ParticleSystem.Particle pt = _particles[shown];
                Vector3 p = _positions[i];
                if (pull)
                {
                    Vector3 toEye = eyePos - p;
                    float d = toEye.magnitude;
                    if (d > pullDistance * 2f) p += toEye * (pullDistance / d);
                }
                pt.position = p;
                pt.startSize = size;
                pt.startColor = c;
                pt.remainingLifetime = 1e5f;
                pt.startLifetime = 1e6f;
                pt.velocity = Vector3.zero;
                _particles[shown++] = pt;
            }
            _ps.SetParticles(_particles, shown);

            bool ringOn = elapsed <= (float)_params.SweepDuration + grain;
            _ring.enabled = ringOn;
            if (ringOn)
            {
                float radius = (float)SonaEncoder.PropagationRadius(elapsed, _params);
                float y = transform.position.y + ringHeight;   // this object sits on the floor
                if (_spherical)
                {
                    // Where the spherical shell meets the floor.
                    float h = _center.y - y;
                    radius = Mathf.Sqrt(Mathf.Max(0f, radius * radius - h * h));
                }
                for (int s = 0; s < ringSegments; s++)
                {
                    float a = s * Mathf.PI * 2f / ringSegments;
                    _ringPoints[s] = new Vector3(_center.x + radius * Mathf.Sin(a), y, _center.z + radius * Mathf.Cos(a));
                }
                _ring.positionCount = ringSegments;
                _ring.SetPositions(_ringPoints);
            }

            if (afterglowSeconds > 0f)
            {
                if (elapsed > _lastOnset + flash + afterglowSeconds && !ringOn)
                {
                    // Everything has faded back to black.
                    HideAll();
                    _startDsp = -1;
                }
            }
            else if (_cycleSeconds > 0f && elapsed > _cycleSeconds + 1.0f)
            {
                // Keep the final cloud up but stop updating.
                _startDsp = -1;
            }
        }
    }
}
