// SONA figure helper (editor only). In Play mode, press F9 to trigger one sweep at half speed and save
// a burst of frames while the wave crosses the room. If the file Logs/SonaWaveCapture/AUTO_CAPTURE
// exists when Play starts, the burst runs by itself once (after the panel settles) and the file is deleted.
// Frames go to <project>/Logs/SonaWaveCapture/<timestamp>/frame_###_<seconds>s.jpg
// Safe to delete this file when the figure is done; it never ships in a player build.
#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Sona
{
    public sealed class SonaFrameBurst : MonoBehaviour
    {
        public float framesPerSecond = 20f;
        public float captureWaveSpeed = 0.5f;
        public float extraSeconds = 0.8f;
        public float autoDelaySeconds = 4f;
        public int jpgQuality = 92;

        bool _busy;

        static string CaptureRoot
        {
            get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "SonaWaveCapture")); }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (FindFirstObjectByType<SonaFrameBurst>() != null) return;
            var go = new GameObject("SONA Frame Burst (editor only)");
            DontDestroyOnLoad(go);
            go.AddComponent<SonaFrameBurst>();
        }

        IEnumerator Start()
        {
            string flag = Path.Combine(CaptureRoot, "AUTO_CAPTURE");
            if (!File.Exists(flag)) yield break;
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < autoDelaySeconds) yield return null;
            try { File.Delete(flag); } catch (IOException) { }
            if (!_busy) yield return StartCoroutine(Burst());
        }

        void Update()
        {
            if (_busy) return;
            bool pressed;
#if ENABLE_INPUT_SYSTEM
            pressed = Keyboard.current != null && Keyboard.current.f9Key.wasPressedThisFrame;
#else
            pressed = Input.GetKeyDown(KeyCode.F9);
#endif
            if (pressed) StartCoroutine(Burst());
        }

        IEnumerator Burst()
        {
            _busy = true;
            SonaSweepController sweep = FindFirstObjectByType<SonaSweepController>();
            if (sweep == null)
            {
                Debug.LogWarning("SONA Frame Burst: no SonaSweepController in the scene.");
                _busy = false;
                yield break;
            }

            string dir = Path.Combine(CaptureRoot, System.DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(dir);

            bool panelWasVisible = sweep.settingsPanel != null && sweep.settingsPanel.Visible;
            if (sweep.settingsPanel != null) sweep.settingsPanel.SetVisible(false);
            if (sweep.hud != null) sweep.hud.SetVisible(false);

            float previousSpeed = sweep.waveSpeed;
            sweep.SetWaveSpeed(captureWaveSpeed);
            sweep.Trigger();

            float duration = sweep.baseSweepDuration / Mathf.Max(0.01f, sweep.waveSpeed) + sweep.scheduleLead + extraSeconds;
            float step = 1f / Mathf.Max(1f, framesPerSecond);
            float t0 = Time.realtimeSinceStartup;
            int count = 0;
            while (Time.realtimeSinceStartup - t0 < duration)
            {
                yield return new WaitForEndOfFrame();
                float t = Time.realtimeSinceStartup - t0;
                Texture2D tex = ScreenCapture.CaptureScreenshotAsTexture();
                File.WriteAllBytes(Path.Combine(dir, string.Format("frame_{0:000}_{1:0.00}s.jpg", count, t)),
                    tex.EncodeToJPG(jpgQuality));
                Destroy(tex);
                count++;
                float next = t0 + count * step;
                while (Time.realtimeSinceStartup < next) yield return null;
            }

            sweep.SetWaveSpeed(previousSpeed);
            if (sweep.hud != null) sweep.hud.SetVisible(true);
            if (sweep.settingsPanel != null && panelWasVisible) sweep.settingsPanel.SetVisible(true);
            File.WriteAllText(Path.Combine(dir, "DONE.txt"), count + " frames");
            Debug.Log("SONA Frame Burst: saved " + count + " frames to " + dir);
            _busy = false;
        }
    }
}
#endif
