// SONA Quest port: the Python-faithful renderer (spatializer.py granular grains + ITD/ILD panning),
// mixed in real time on Unity's audio thread.
//
// Two flavours, chosen per sweep by the controller:
//   - Frozen: azimuths fixed at ping time. Sample-identical to the Python render (head-locked).
//   - Live head: each grain is aimed with the head pose at the moment it starts, so the scene
//     stays anchored to the world while the head turns during the sweep.
using Sona.Core;
using UnityEngine;

namespace Sona
{
    [RequireComponent(typeof(AudioSource))]
    [AddComponentMenu("SONA/SONA Stereo Renderer")]
    public sealed class SonaStereoRenderer : MonoBehaviour
    {
        StereoBlockMixer _mixer;
        AudioSource _source;
        int _sampleRate;

        public int SampleRate { get { return _sampleRate; } }

        public float Volume
        {
            set { if (_mixer != null) _mixer.MasterVolume = Mathf.Clamp01(value); }
        }

        void Awake()
        {
            _sampleRate = AudioSettings.outputSampleRate;
            _mixer = new StereoBlockMixer(_sampleRate);
            _source = GetComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.loop = true;
            _source.spatialBlend = 0f;   // 2D: SONA does its own panning here
            _source.spatialize = false;  // keep the HRTF spatializer out of this path
            _source.dopplerLevel = 0f;
            _source.priority = 0;        // never virtualize the main renderer
            _source.volume = 1f;
            // A silent stereo loop keeps the source "playing" so OnAudioFilterRead runs as a generator.
            _source.clip = AudioClip.Create("SONA silence", _sampleRate, 2, _sampleRate, false);
        }

        void OnEnable()
        {
            if (_source != null && !_source.isPlaying) _source.Play();
        }

        void OnDisable()
        {
            if (_mixer != null) _mixer.Stop();
        }

        public void Play(MixSweep sweep, double startDsp)
        {
            if (_source != null && !_source.isPlaying) _source.Play();
            _mixer.Play(sweep, startDsp);
        }

        /// <summary>Gapless repeat: starts on the sample after the current cycle (Python repeat semantics).</summary>
        public void PlayAfterCurrent(MixSweep sweep)
        {
            _mixer.PlayAfterCurrent(sweep);
        }

        public void Stop()
        {
            if (_mixer != null) _mixer.Stop();
        }

        /// <summary>Drop a queued repeat cycle that has not started; the current sweep finishes.</summary>
        public void CancelQueued()
        {
            if (_mixer != null) _mixer.CancelQueued();
        }

        public void SetHeadPose(Vector3 position, Quaternion rotation)
        {
            if (_mixer != null) _mixer.HeadPose = new HeadPoseSnapshot(position.ToVec3d(), rotation.ToQuatD());
        }

        /// <summary>Sweep id and DSP start time of the sweep currently scheduled/playing.</summary>
        public bool TryGetCurrent(out int sweepId, out double startDsp, out bool active)
        {
            if (_mixer == null)
            {
                sweepId = -1;
                startDsp = 0;
                active = false;
                return false;
            }
            return _mixer.TryGetCurrent(out sweepId, out startDsp, out active);
        }

        public int DroppedVoices { get { return _mixer != null ? _mixer.DroppedVoices : 0; } }

        void OnAudioFilterRead(float[] data, int channels)
        {
            if (_mixer == null) return;
            _mixer.Render(data, channels, AudioSettings.dspTime, true);
        }
    }
}
