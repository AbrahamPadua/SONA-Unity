// SONA Quest port: the sweep controller (replaces app.py's EcholocationEditor + render_sweep).
//
// On trigger it reads the real head pose, samples every SonaSurface, encodes, renders on a worker
// thread, and schedules the result on the chosen renderer:
//
//   head pose + SonaSurfaces --frontend--> Return3D[] --encoder--> RenderPlan --renderer--> ears
//
// Timing (wave) uses horizontal distance from the head; direction uses the full 3-D vector;
// pitch uses gravity-referenced elevation, exactly as in the Python prototype.
//
// Modes (menu): Explore (listen) and Challenge (find a hidden box, guess with the laser).
//
// View: the world is pitch black. The sweep is drawn as glowing points in Explore (never in Challenge).
// The HUD shows a status line, the latest message and a hint for the next step.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Sona.Core;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Sona
{
    public enum SonaRenderMode
    {
        /// <summary>Python grains + ITD/ILD, each grain aimed with the live head pose (world-anchored).</summary>
        StereoLiveHead = 0,
        /// <summary>Clustered grains from their world positions through the HRTF spatializer (Meta XR Audio).</summary>
        Hrtf = 2,   // keeps the value saved in scenes from before the frozen-Python renderer was removed
    }

    public enum SonaMode
    {
        Explore,
        Challenge,
    }

    [DefaultExecutionOrder(100)]
    [AddComponentMenu("SONA/SONA Sweep Controller")]
    public sealed class SonaSweepController : MonoBehaviour
    {
        [Header("Scene")]
        [Tooltip("Headset camera (OVRCameraRig > TrackingSpace > CenterEyeAnchor). Defaults to Camera.main.")]
        public Transform head;
        public SonaRoomBuilder room;

        [Header("Renderers and UI")]
        public SonaStereoRenderer stereoRenderer;
        public SonaHrtfRenderer hrtfRenderer;
        public SonaWaveVisualizer visualizer;
        public SonaHud hud;
        public SonaPointer pointer;
        public SonaSettingsPanel settingsPanel;

        [Header("Playback")]
        public SonaRenderMode renderMode = SonaRenderMode.StereoLiveHead;
        [Range(0f, 1f)] public float volume = 0.6f;
        [Range(0.5f, 2f)] public float waveSpeed = 1f;
        public bool repeat;
        [Tooltip("Python behaviour: non-floor elevation -> pentatonic pitch. Off = one fixed pitch (isolates HRTF cues).")]
        public bool elevationToPitch = true;
        public bool readControllerInput = true;

        [Header("Sampling (frontend.py)")]
        [Min(0.5f)] public float samplesPerMeter = 5f;
        [Tooltip("On: sweep radius = farthest sampled point (the Python editor's farthest room corner). Off: fixed radius below.")]
        public bool farFromSceneExtent = true;
        [Min(0.5f)] public float fixedFar = 5f;
        [Tooltip("Off (Python): cylindrical wave, timing from horizontal distance. On: spherical wave, timing from 3-D distance.")]
        public bool sphericalWave;

        [Header("Encoder (encoder.py)")]
        public float baseSweepDuration = 1.2f;
        public float near = 0.4f;
        public float foveationExponent = 0.5f;
        public float peripheralDb = 18f;
        public float fieldHalfAngleDegrees = 90f;
        public float nearEmphasis = 0.3f;

        [Header("Grains (spatializer.py)")]
        public float grainDuration = 0.14f;
        public float attack = 0.03f;
        public float release = 0.09f;
        public float maxItdMs = 0.7f;
        public float maxIldDb = 8f;
        public float mixGain = 0.025f;
        [Range(0.05f, 1f)] public float peakLimit = 0.9f;

        [Header("HRTF clustering")]
        public ClusterParams clustering = new ClusterParams();

        [Header("Timing")]
        [Tooltip("Delay between the trigger press and the first sound.")]
        public float scheduleLead = 0.1f;
        [Tooltip("Repeat: compute the next cycle this long before the current one ends (from the head pose at that moment).")]
        public float repeatPrepareLead = 0.4f;

        [Header("Challenge log")]
        [Tooltip("Append every challenge guess to a CSV in Application.persistentDataPath.")]
        public bool logChallengeTrials = true;

        [Header("View")]
        [Tooltip("Pitch black: no sky and no lit room. You see the sweep (outside Challenge), the menu and the " +
                 "status text. Off = lit room.")]
        public bool darkWorld = true;
        [Tooltip("Draw each sweep as glowing points in Explore. Never shown in Challenge.")]
        public bool showSweepVisuals = true;
        [Tooltip("Add researcher numbers to the HUD: renderer, returns, compute time, HRTF voices.")]
        public bool showDiagnostics;

        const float MessageSeconds = 4f;

        sealed class SweepInput
        {
            public int Id;
            public List<SurfaceQuad> Quads;
            public ListenerPose3D Pose;
            public double SamplesPerMeter;
            public bool FarFromExtent;
            public double FixedFar;
            public bool Spherical;
            public EncodingParams Encoding;
            public GrainBank Bank;
            public PitchMapper Pitch;
            public SonaRenderMode Mode;
            public ClusterParams Clusters;
        }

        sealed class SweepResult
        {
            public int Id;
            public SonaRenderMode Mode;
            public List<ScheduledReturn> Plan;
            public EncodingParams Encoding;
            public ListenerPose3D Pose;
            public MixSweep Mix;
            public HrtfClusterResult Hrtf;
            public double CycleSeconds;
            public int ReturnCount;
            public double ComputeMs;
            public double StartDsp;
            public bool Spherical;
            public string Error;
        }

        SonaInput _input;
        GrainBank _bank;
        string _bankKey;
        Task<SweepResult> _job;
        int _jobId = -1;
        bool _jobIsRepeat;
        int _nextId = 1;
        SweepResult _current;
        SweepResult _next;
        bool _blackout;
        string _message;
        float _messageAt;
        string _result;
        float _hudTimer;
        float _startTime;
        readonly System.Random _rng = new System.Random();
        readonly Dictionary<Camera, KeyValuePair<CameraClearFlags, Color>> _savedCameras =
            new Dictionary<Camera, KeyValuePair<CameraClearFlags, Color>>();

        SonaMode _mode = SonaMode.Explore;
        int _trial;
        int _sweepsThisTrial;
        bool _guessedThisTrial;
        Transform _guessMarker;
        string _logPath;
        // Trial numbers restart at every launch, so each log row also names the launch it came from.
        readonly string _session = DateTime.Now.ToString("s", CultureInfo.InvariantCulture);

        public SonaMode Mode { get { return _mode; } }

        /// <summary>True when sweeps are drawn right now (visuals on, not Challenge, not blackout).</summary>
        public bool SweepVisualsShown
        {
            get { return showSweepVisuals && !_blackout && _mode != SonaMode.Challenge; }
        }

        /// <summary>Shown on the HUD for a few seconds, then the hint comes back.</summary>
        string Message
        {
            set
            {
                _message = value;
                _messageAt = Time.unscaledTime;
            }
        }

        void Awake()
        {
            if (head == null && Camera.main != null) head = Camera.main.transform;
            if (stereoRenderer == null) stereoRenderer = GetComponentInChildren<SonaStereoRenderer>();
            if (hrtfRenderer == null) hrtfRenderer = GetComponentInChildren<SonaHrtfRenderer>();
            if (visualizer == null) visualizer = GetComponentInChildren<SonaWaveVisualizer>();
            if (hud == null) hud = GetComponentInChildren<SonaHud>();
            if (room == null) room = GetComponentInChildren<SonaRoomBuilder>();
            if (pointer == null) pointer = GetComponentInChildren<SonaPointer>();
            if (settingsPanel == null) settingsPanel = GetComponentInChildren<SonaSettingsPanel>();
            CreateMissingHelpers();
        }

        /// <summary>Scenes made before the panel existed still get a pointer, a panel and the desktop fallback.</summary>
        void CreateMissingHelpers()
        {
            Transform rigRoot = head != null ? head.root : null;
            if (pointer == null)
            {
                var go = new GameObject("SONA Pointer");
                go.transform.SetParent(transform, false);
                pointer = go.AddComponent<SonaPointer>();
            }
            if (pointer.rayOrigin == null && rigRoot != null) pointer.rayOrigin = SonaUnity.FindDeep(rigRoot, "RightHandAnchor");
            if (pointer.viewCamera == null && head != null) pointer.viewCamera = head.GetComponent<Camera>();
            if (settingsPanel == null)
            {
                var go = new GameObject("SONA Control Bar");
                go.transform.SetParent(transform, false);
                settingsPanel = go.AddComponent<SonaSettingsPanel>();
            }
            if (settingsPanel.controller == null) settingsPanel.controller = this;
            if (settingsPanel.head == null) settingsPanel.head = head;
            if (room != null && room.unlitMaterial == null) room.unlitMaterial = settingsPanel.uiMaterial;
            if (rigRoot != null && rigRoot != transform.root && rigRoot.GetComponent<SonaDesktopRig>() == null)
            {
                rigRoot.gameObject.AddComponent<SonaDesktopRig>();
            }
        }

        void Start()
        {
            ApplyView();
        }

        void OnEnable()
        {
            if (readControllerInput) _input = new SonaInput();
            if (room != null) room.RoomChanged += OnRoomChanged;
            _startTime = Time.time;
        }

        void OnDisable()
        {
            if (_input != null)
            {
                _input.Dispose();
                _input = null;
            }
            if (room != null) room.RoomChanged -= OnRoomChanged;
            StopPlayback();
        }

        void OnRoomChanged()
        {
            // Python: editing the scene stops an active sweep and the repeat loop.
            StopPlayback();
        }

        // ------------------------------------------------------------------ sound actions

        /// <summary>Restart: stop whatever is playing and send a new sweep from the current head pose.</summary>
        [ContextMenu("Trigger Sweep")]
        public void Trigger()
        {
            StopPlayback();
            StartJob(false);
        }

        public void StopPlayback()
        {
            _jobId = -1;   // ignore any result still being computed
            if (stereoRenderer != null) stereoRenderer.Stop();
            if (hrtfRenderer != null) hrtfRenderer.Stop();
            if (visualizer != null) visualizer.Clear();
            _current = null;
            _next = null;
        }

        public void SetRepeat(bool on)
        {
            repeat = on;
            if (!on && _next != null)
            {
                // Python: disabling repeat lets the current cycle finish and starts no other.
                if (_next.Mode == SonaRenderMode.Hrtf) hrtfRenderer.CancelSweep(_next.Id);
                else stereoRenderer.CancelQueued();
                _next = null;
            }
            if (!on && _job != null && _jobIsRepeat) _jobId = -1;   // drop a next cycle still being computed
            Message = on ? "Repeat ON: sweeps re-ping from your current pose" : "Repeat OFF: the current sweep finishes normally";
        }

        public void SetWaveSpeed(float speed)
        {
            waveSpeed = Mathf.Clamp(Mathf.Round(speed * 4f) / 4f, 0.5f, 2f);
            Message = string.Format("Wave speed {0:0.00}x ({1:0.00} s sweep), applies to the next sweep",
                                     waveSpeed, baseSweepDuration / waveSpeed);
        }

        /// <summary>Stereo (live head) and HRTF take turns.</summary>
        public void CycleRenderMode()
        {
            renderMode = renderMode == SonaRenderMode.Hrtf ? SonaRenderMode.StereoLiveHead : SonaRenderMode.Hrtf;
            bool wasRepeating = repeat && _current != null;
            StopPlayback();
            Message = "Renderer: " + ModeLabel(renderMode);
            if (wasRepeating) StartJob(false);
        }

        public void TogglePitch()
        {
            elevationToPitch = !elevationToPitch;
            Message = elevationToPitch ? "Pitch follows height (Python)" : "Fixed pitch: height comes from the HRTF only";
        }

        // ------------------------------------------------------------------ room actions

        public void SetWalls(WallMode walls)
        {
            if (room == null) return;
            room.SetWalls(walls);
            Message = "Walls: " + SonaRoomBuilder.WallsLabel(walls);
        }

        public void SetRoomSize(float width, float depth)
        {
            if (room == null) return;
            room.SetSize(width, depth);
            Message = "Room: " + room.RoomLabel;
        }

        public void ResetObjects()
        {
            if (room == null) return;
            if (_mode == SonaMode.Challenge) SetMode(SonaMode.Explore);
            room.ResetObjects();
            Message = "Box 1 and Panel 1 restored";
        }

        // ------------------------------------------------------------------ modes

        public void SetMode(SonaMode mode)
        {
            if (mode == _mode) return;
            _mode = mode;
            if (mode == SonaMode.Challenge)
            {
                NewChallengeBox();
            }
            else
            {
                if (room != null) room.EndChallenge();
                HideGuessMarker();
                _result = null;
                Message = "Explore: sweep and listen";
            }
            ApplyView();
        }

        /// <summary>Python "Random Box": a hidden 1 x 1 x 1 m box at least 1.5 m away.</summary>
        public void NewChallengeBox()
        {
            if (room == null || head == null) return;
            if (_mode != SonaMode.Challenge)
            {
                SetMode(SonaMode.Challenge);   // calls back into NewChallengeBox
                return;
            }
            string error;
            if (room.NewChallengeBox(head.position, _rng, out error))
            {
                _trial++;
                _sweepsThisTrial = 0;
                _guessedThisTrial = false;
                _result = null;
                HideGuessMarker();
                // The next step is sweep and guess, and an open menu would hold the trigger.
                if (settingsPanel != null) settingsPanel.Close();
                Message = string.Format("Trial {0}: a new box is hidden", _trial);
            }
            else
            {
                Message = "Random box failed: " + error;
            }
        }

        public void ToggleReveal()
        {
            if (room == null || !room.ChallengeActive)
            {
                Message = "No hidden box (choose Challenge mode)";
                return;
            }
            room.SetChallengeRevealed(!room.ChallengeRevealed);
            Vector3? box = room.MysteryBoxPosition;
            if (room.ChallengeRevealed && box.HasValue && head != null)
            {
                Vector3 d = box.Value - head.position;
                double az = SonaChallenge.Bearing(box.Value.ToVec3d(), HeadPose());
                Message = string.Format("Box: {0:0.0} m away, {1:0}° {2}",
                                         new Vector2(d.x, d.z).magnitude, Math.Abs(az), az >= 0 ? "right" : "left");
            }
            else
            {
                Message = room.ChallengeRevealed ? "Box revealed" : "Box hidden";
            }
        }

        /// <summary>Hide everything, including the menu, the laser and the status text (blind trials). Menu ends it.</summary>
        public void SetBlackout(bool on)
        {
            _blackout = on;
            ApplyView();
            if (hud != null) hud.SetVisible(!on);
            if (pointer != null) pointer.Visible = !on;
            if (settingsPanel != null && on) settingsPanel.Close();
            if (_guessMarker != null) _guessMarker.gameObject.SetActive(!on && _guessMarker.gameObject.activeSelf);
            Message = on ? "Blackout on" : "Blackout off";
        }

        /// <summary>The Menu button: ends blackout (and shows the menu), otherwise opens or closes the menu.</summary>
        void PressMenu()
        {
            if (_blackout)
            {
                SetBlackout(false);
                if (settingsPanel != null) settingsPanel.Open();
                return;
            }
            if (settingsPanel != null) settingsPanel.Toggle();
        }

        /// <summary>Pitch black (default) or a lit room for sighted demos.</summary>
        public void SetDarkWorld(bool dark)
        {
            darkWorld = dark;
            ApplyView();
            Message = dark ? "Dark: only the sweep is visible" : "Lit: the room is visible (for sighted demos)";
        }

        public void ToggleSweepVisuals()
        {
            showSweepVisuals = !showSweepVisuals;
            ApplyView();
            if (!showSweepVisuals) Message = "Sweep visuals off: sound only";
            else if (_mode == SonaMode.Challenge) Message = "Sweep visuals on (they stay hidden in Challenge)";
            else Message = "Sweep visuals on";
        }

        /// <summary>Room look, sweep visuals and camera background for the current mode.</summary>
        void ApplyView()
        {
            if (room != null)
            {
                room.SetVisible(!_blackout);
                room.SetView(darkWorld ? SonaRoomView.Hidden : SonaRoomView.Lit);
            }
            if (visualizer != null) visualizer.SetVisible(SweepVisualsShown);
            ApplyCameraBackground(darkWorld || _blackout);
        }

        /// <summary>Black background on every rig camera (no skybox), or put back what they had.</summary>
        void ApplyCameraBackground(bool black)
        {
            if (head == null) return;
            var cameras = new List<Camera>();
            head.root.GetComponentsInChildren(true, cameras);
            Camera own = head.GetComponent<Camera>();
            if (own != null && !cameras.Contains(own)) cameras.Add(own);
            foreach (Camera cam in cameras)
            {
                if (black)
                {
                    if (!_savedCameras.ContainsKey(cam))
                    {
                        _savedCameras[cam] = new KeyValuePair<CameraClearFlags, Color>(cam.clearFlags, cam.backgroundColor);
                    }
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = Color.black;
                }
                else
                {
                    KeyValuePair<CameraClearFlags, Color> saved;
                    if (_savedCameras.TryGetValue(cam, out saved))
                    {
                        cam.clearFlags = saved.Key;
                        cam.backgroundColor = saved.Value;
                    }
                }
            }
            if (!black) _savedCameras.Clear();
        }

        ListenerPose3D HeadPose()
        {
            return ListenerPose3D.FromForward(head.position.ToVec3d(), head.forward.ToVec3d(), head.up.ToVec3d());
        }

        // ------------------------------------------------------------------ frame loop

        void Update()
        {
            if (head == null && Camera.main != null) head = Camera.main.transform;
            bool clickUsed = false;
            if (_input != null)
            {
                _input.Poll();
                bool xr = UnityEngine.XR.XRSettings.isDeviceActive;
                if (pointer != null) pointer.Tick(_input, xr);
                if (_input.Menu) PressMenu();
                if (settingsPanel != null)
                {
                    clickUsed = settingsPanel.Tick(pointer, _input.SelectDown, _input.SelectHeld, _input.SelectUp);
                }
                if (_input.Sweep) Trigger();
                HandlePointer(clickUsed);
            }
            PollJob();
            SyncWithRenderers();
            MaybePrepareRepeat();
            UpdateHud();
        }

        void LateUpdate()
        {
            if (head != null && stereoRenderer != null) stereoRenderer.SetHeadPose(head.position, head.rotation);
            if (stereoRenderer != null) stereoRenderer.Volume = volume;
            if (hrtfRenderer != null) hrtfRenderer.Volume = volume;
        }

        /// <summary>
        /// Challenge: the trigger places a guess on the floor, then starts the next box. A trigger press that
        /// does nothing says why on the HUD.
        /// </summary>
        void HandlePointer(bool clickUsedByPanel)
        {
            if (pointer == null || room == null || !_input.SelectDown || clickUsedByPanel) return;
            string menu = UnityEngine.XR.XRSettings.isDeviceActive ? "Menu" : "Tab";
            // While the menu is open the trigger only works on the menu, so a missed button never becomes a guess.
            if (settingsPanel != null && settingsPanel.Visible)
            {
                Message = "Point at a menu button, or press " + menu + " to close the menu";
                return;
            }
            if (_mode != SonaMode.Challenge)
            {
                Message = "The trigger guesses in Challenge: press " + menu + ", then Trial > Next box";
                return;
            }
            if (_guessedThisTrial)
            {
                NewChallengeBox();
                return;
            }
            // Where the ray meets the floor plane, if that spot is actually visible (not behind a wall).
            Vector3 planePoint = Vector3.zero;
            bool onFloor = !pointer.HitPanel && pointer.RayToPlane(room.FloorY, out planePoint);
            if (onFloor && pointer.HasHit && !pointer.HitIsFloor)
            {
                float toPlane = Vector3.Distance(pointer.CurrentRay.origin, planePoint);
                float toHit = Vector3.Distance(pointer.CurrentRay.origin, pointer.HitPoint);
                if (toHit < toPlane - 0.01f) onFloor = false;
            }
            if (!onFloor)
            {
                Message = "Point at the floor where you think the box is";
                return;
            }
            Guess(planePoint);
        }

        void Guess(Vector3 floorPoint)
        {
            Vector3? boxPos = room.MysteryBoxPosition;
            if (!boxPos.HasValue || head == null) return;
            Vector3 box = boxPos.Value;
            ChallengeScore s = SonaChallenge.Score(box.ToVec3d(), floorPoint.ToVec3d(), HeadPose());
            ShowGuessMarker(floorPoint);
            room.SetChallengeRevealed(true);
            _result = string.Format("Off by {0:0.00} m: your guess was {1:0}° {2} and {3:0.00} m too {4}",
                                    s.Error, Math.Abs(s.BearingError), s.BearingError >= 0 ? "right" : "left",
                                    Math.Abs(s.GuessDistance - s.BoxDistance), s.GuessDistance >= s.BoxDistance ? "far" : "near");
            Message = null;
            if (logChallengeTrials && !_guessedThisTrial) LogTrial(box, floorPoint, s);
            _guessedThisTrial = true;
        }

        void ShowGuessMarker(Vector3 p)
        {
            if (_guessMarker == null)
            {
                GameObject m = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                m.name = "SONA Guess";
                Destroy(m.GetComponent<Collider>());
                m.transform.localScale = new Vector3(0.25f, 0.005f, 0.25f);
                Material mat = pointer != null && pointer.reticleMaterial != null ? pointer.reticleMaterial : SonaMaterialUtil.FallbackUnlit();
                var r = m.GetComponent<Renderer>();
                if (mat != null) r.sharedMaterial = mat;
                var mpb = new MaterialPropertyBlock();
                mpb.SetColor("_BaseColor", new Color(0.3f, 1f, 0.45f));
                mpb.SetColor("_Color", new Color(0.3f, 1f, 0.45f));
                r.SetPropertyBlock(mpb);
                _guessMarker = m.transform;
            }
            _guessMarker.position = new Vector3(p.x, room.FloorY + 0.01f, p.z);
            _guessMarker.gameObject.SetActive(!_blackout);
        }

        void HideGuessMarker()
        {
            if (_guessMarker != null) _guessMarker.gameObject.SetActive(false);
        }

        void LogTrial(Vector3 box, Vector3 guess, ChallengeScore s)
        {
            try
            {
                // v2 adds the session column; a new name keeps it from being appended under the old header.
                if (_logPath == null) _logPath = Path.Combine(Application.persistentDataPath, "sona_challenge_log_v2.csv");
                bool newFile = !File.Exists(_logPath);
                var inv = CultureInfo.InvariantCulture;
                var sb = new StringBuilder();
                if (newFile)
                {
                    sb.AppendLine("session,time,trial,renderer,pitch,wave_speed,wave_shape,walls,room_w,room_d,room_h,sweeps," +
                                  "box_x,box_z,guess_x,guess_z,error_m,box_dist_m,guess_dist_m,box_bearing_deg,guess_bearing_deg,bearing_error_deg");
                }
                sb.AppendLine(string.Join(",", new[]
                {
                    _session, DateTime.Now.ToString("s", inv), _trial.ToString(inv), renderMode.ToString(),
                    elevationToPitch ? "elevation" : "fixed", waveSpeed.ToString("0.00", inv), sphericalWave ? "spherical" : "cylindrical",
                    room.walls.ToString(), room.width.ToString("0.0", inv), room.depth.ToString("0.0", inv), room.height.ToString("0.0", inv),
                    _sweepsThisTrial.ToString(inv),
                    box.x.ToString("0.000", inv), box.z.ToString("0.000", inv), guess.x.ToString("0.000", inv), guess.z.ToString("0.000", inv),
                    s.Error.ToString("0.000", inv), s.BoxDistance.ToString("0.000", inv), s.GuessDistance.ToString("0.000", inv),
                    s.BoxBearing.ToString("0.0", inv), s.GuessBearing.ToString("0.0", inv), s.BearingError.ToString("0.0", inv),
                }));
                File.AppendAllText(_logPath, sb.ToString());
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SONA] Could not write the challenge log: " + e.Message);
                Message = "Could not save this guess: " + e.Message;
            }
        }

        // ------------------------------------------------------------------ sweep computation

        void StartJob(bool isRepeat)
        {
            SweepInput input = GatherInput();
            if (input == null) return;
            _jobId = input.Id;
            _jobIsRepeat = isRepeat;
            _job = Task.Run(() => Compute(input));
        }

        SweepInput GatherInput()
        {
            if (head == null)
            {
                Message = "No head camera found (assign Head on the SONA Sweep Controller)";
                return null;
            }
            var quads = new List<SurfaceQuad>(64);
            List<SonaSurface> surfaces = SonaSurface.Active;
            for (int i = 0; i < surfaces.Count; i++)
            {
                surfaces[i].AppendQuads(quads);
            }
            EnsureBank();
            var pitch = new PitchMapper { ElevationToPitch = elevationToPitch };
            var encoding = new EncodingParams
            {
                SweepDuration = baseSweepDuration / Mathf.Clamp(waveSpeed, 0.5f, 2f),
                Near = near,
                Far = fixedFar,
                FoveationExponent = foveationExponent,
                PeripheralDb = peripheralDb,
                FieldHalfAngle = fieldHalfAngleDegrees * SonaMath.Deg2Rad,
                NearEmphasis = nearEmphasis,
            };
            var clusterCopy = new ClusterParams
            {
                SlotSeconds = clustering.SlotSeconds,
                AzimuthBinDegrees = clustering.AzimuthBinDegrees,
                ElevationLowDegrees = clustering.ElevationLowDegrees,
                ElevationHighDegrees = clustering.ElevationHighDegrees,
                VoiceBudget = hrtfRenderer != null && hrtfRenderer.VoiceCount > 0 ? Mathf.Min(clustering.VoiceBudget, hrtfRenderer.VoiceCount) : clustering.VoiceBudget,
                MaxWidenSteps = clustering.MaxWidenSteps,
                WidenFactor = clustering.WidenFactor,
                GuardSeconds = clustering.GuardSeconds,
            };
            return new SweepInput
            {
                Id = _nextId++,
                Quads = quads,
                Pose = HeadPose(),
                SamplesPerMeter = samplesPerMeter,
                FarFromExtent = farFromSceneExtent,
                FixedFar = fixedFar,
                Spherical = sphericalWave,
                Encoding = encoding,
                Bank = _bank,
                Pitch = pitch,
                Mode = renderMode,
                Clusters = clusterCopy,
            };
        }

        void EnsureBank()
        {
            int sr = stereoRenderer != null ? stereoRenderer.SampleRate : AudioSettings.outputSampleRate;
            if (sr <= 0) sr = AudioSettings.outputSampleRate;
            string key = string.Join("|", new[]
            {
                sr.ToString(), grainDuration.ToString("R"), attack.ToString("R"), release.ToString("R"),
                maxItdMs.ToString("R"), maxIldDb.ToString("R"), mixGain.ToString("R"), peakLimit.ToString("R"),
            });
            if (_bank != null && key == _bankKey) return;
            var gp = new GranularParams
            {
                SampleRate = sr,
                GrainDuration = grainDuration,
                Attack = attack,
                Release = release,
                MaxItd = maxItdMs / 1000.0,
                MaxIldDb = maxIldDb,
                MixGain = mixGain,
                PeakLimit = peakLimit,
            };
            _bank = new GrainBank(gp);
            _bankKey = key;
            // Pre-build every carrier so the first sweep does not pay for it.
            foreach (double f in SonaPitch.AllElevationFrequencies()) _bank.GetIndex(f, false);
            _bank.GetIndex(SonaPitch.FloorFrequency, true);
            _bank.GetIndex(new PitchMapper().FlatFrequency, false);
        }

        /// <summary>Worker thread: pure core only (no Unity API).</summary>
        static SweepResult Compute(SweepInput input)
        {
            var watch = Stopwatch.StartNew();
            var r = new SweepResult { Id = input.Id, Mode = input.Mode, Pose = input.Pose, Spherical = input.Spherical };
            try
            {
                double? maxRange = input.FarFromExtent ? (double?)null : input.FixedFar;
                List<Return3D> returns = SonaFrontend.SurfaceEmission(input.Quads, input.Pose, input.SamplesPerMeter, maxRange,
                                                                      null, input.Spherical);
                EncodingParams ep = input.Encoding;
                ep.Far = input.FarFromExtent ? Math.Max(SonaFrontend.MaxPropagationRange(returns), 1e-6) : input.FixedFar;
                List<ScheduledReturn> plan = SonaEncoder.Encode(returns, ep);
                GrainEvent[] events = GranularRenderer.PrepareEvents(plan, input.Bank, input.Pitch);
                double outputGain = GranularGain.Measure(plan, ep.SweepDuration, input.Bank, input.Pitch);
                int cycleFrames = input.Bank.Params.BufferFrames(ep.SweepDuration, input.Bank.Length);
                r.CycleSeconds = cycleFrames / (double)input.Bank.SampleRate;
                if (input.Mode == SonaRenderMode.Hrtf)
                {
                    r.Hrtf = SonaClusterer.Build(plan, events, input.Bank, input.Pose, outputGain, input.Clusters);
                }
                else
                {
                    r.Mix = new MixSweep
                    {
                        Id = input.Id,
                        Events = events,
                        Grains = input.Bank.SnapshotF(),
                        Params = input.Bank.Params,
                        OutputGain = (float)outputGain,
                        CycleFrames = cycleFrames,
                        LiveHeadTracking = input.Mode == SonaRenderMode.StereoLiveHead,
                    };
                }
                r.Plan = plan;
                r.Encoding = ep;
                r.ReturnCount = returns.Count;
            }
            catch (Exception e)
            {
                r.Error = e.Message;
            }
            r.ComputeMs = watch.Elapsed.TotalMilliseconds;
            return r;
        }

        void PollJob()
        {
            if (_job == null || !_job.IsCompleted) return;
            Task<SweepResult> task = _job;
            _job = null;
            if (task.IsFaulted)
            {
                Message = "Sweep failed: " + task.Exception.GetBaseException().Message;
                Debug.LogException(task.Exception.GetBaseException(), this);
                return;
            }
            SweepResult r = task.Result;
            if (r.Id != _jobId) return;   // superseded by a newer trigger or a stop
            if (r.Error != null)
            {
                Message = "Sweep failed: " + r.Error;
                Debug.LogError("[SONA] " + r.Error, this);
                return;
            }
            Dispatch(r, _jobIsRepeat);
        }

        void Dispatch(SweepResult r, bool isRepeat)
        {
            double now = AudioSettings.dspTime;
            if (isRepeat && _current != null && _current.Mode == r.Mode)
            {
                r.StartDsp = _current.StartDsp + _current.CycleSeconds;
                if (r.Mode == SonaRenderMode.Hrtf)
                {
                    r.StartDsp = Math.Max(r.StartDsp, now + scheduleLead);
                    hrtfRenderer.Play(r.Id, r.Hrtf, r.StartDsp, false);
                }
                else
                {
                    stereoRenderer.PlayAfterCurrent(r.Mix);   // gapless, sample-exact
                }
                _next = r;
                if (_mode == SonaMode.Challenge) _sweepsThisTrial++;
                return;
            }

            r.StartDsp = now + scheduleLead;
            if (r.Mode == SonaRenderMode.Hrtf)
            {
                if (stereoRenderer != null) stereoRenderer.Stop();
                hrtfRenderer.Play(r.Id, r.Hrtf, r.StartDsp, true);
            }
            else
            {
                if (hrtfRenderer != null) hrtfRenderer.Stop();
                stereoRenderer.Play(r.Mix, r.StartDsp);
            }
            _current = r;
            _next = null;
            if (_mode == SonaMode.Challenge) _sweepsThisTrial++;
            ShowVisual(r);
        }

        void ShowVisual(SweepResult r)
        {
            if (visualizer == null || r.Plan == null) return;
            visualizer.Show(r.Plan, r.Encoding, r.Pose.Position.ToVector3(), r.StartDsp, grainDuration, (float)r.CycleSeconds,
                            r.Spherical);
        }

        void SyncWithRenderers()
        {
            double now = AudioSettings.dspTime;
            if (stereoRenderer != null && _current != null && _current.Mode != SonaRenderMode.Hrtf)
            {
                int id;
                double start;
                bool active;
                if (stereoRenderer.TryGetCurrent(out id, out start, out active))
                {
                    if (id == _current.Id && Math.Abs(start - _current.StartDsp) > 1e-4)
                    {
                        _current.StartDsp = start;
                        if (visualizer != null) visualizer.SetStartDsp(start);
                    }
                    else if (_next != null && id == _next.Id)
                    {
                        _next.StartDsp = start;
                        Promote();
                    }
                }
            }
            if (_next != null && _next.Mode == SonaRenderMode.Hrtf && now >= _next.StartDsp)
            {
                Promote();
            }
        }

        void Promote()
        {
            _current = _next;
            _next = null;
            ShowVisual(_current);
        }

        void MaybePrepareRepeat()
        {
            if (!repeat || _current == null || _next != null || _job != null) return;
            double cycleEnd = _current.StartDsp + _current.CycleSeconds;
            if (cycleEnd - AudioSettings.dspTime < repeatPrepareLead)
            {
                StartJob(true);
            }
        }

        // ------------------------------------------------------------------ status

        static string ModeLabel(SonaRenderMode m)
        {
            switch (m)
            {
                case SonaRenderMode.StereoLiveHead: return "Stereo, live head (world-anchored)";
                default: return "HRTF (Meta XR Audio)";
            }
        }

        /// <summary>What to do next, named with the buttons of the device in use.</summary>
        string Hint(bool xr)
        {
            string a = xr ? "A" : "Space", trigger = xr ? "Trigger" : "Click", menu = xr ? "Menu" : "Tab";
            if (settingsPanel != null && settingsPanel.Visible) return trigger + ": choose    " + menu + ": close the menu";
            if (_mode != SonaMode.Challenge) return a + ": sweep    " + menu + ": menu";
            if (_guessedThisTrial) return trigger + ": next box    " + a + ": sweep again";
            return a + ": sweep    " + trigger + " at the floor: guess    " + menu + ": menu";
        }

        /// <summary>Status line, result or latest message, and the hint; researcher numbers only when asked for.</summary>
        void UpdateHud()
        {
            _hudTimer -= Time.unscaledDeltaTime;
            if (_hudTimer > 0f) return;
            _hudTimer = 0.2f;

            bool xr = UnityEngine.XR.XRSettings.isDeviceActive;
            if (head != null && room != null && Time.time - _startTime > 3f && xr && head.position.y - room.transform.position.y < 0.5f)
            {
                Message = "Head is below 0.5 m: set OVRManager Tracking Origin Type = Floor Level";
            }

            var sb = new StringBuilder(256);
            if (_mode == SonaMode.Challenge)
            {
                sb.AppendFormat("<b>Challenge  ·  trial {0}  ·  {1} sweep{2}</b>", _trial, _sweepsThisTrial, _sweepsThisTrial == 1 ? "" : "s");
            }
            else
            {
                sb.Append("<b>Explore</b>");
            }
            if (repeat) sb.Append("  ·  repeat on");
            sb.Append('\n');
            if (_guessedThisTrial && _result != null) sb.Append("<color=#ffd27a>").Append(_result).Append("</color>\n");
            if (!string.IsNullOrEmpty(_message) && Time.unscaledTime - _messageAt < MessageSeconds) sb.Append(_message).Append('\n');
            sb.Append("<color=#9fb3c8>").Append(Hint(xr)).Append("</color>");

            if (showDiagnostics)
            {
                sb.Append("\n<color=#7f8a99>").Append(ModeLabel(renderMode));
                sb.AppendFormat("  |  speed {0:0.00}x  |  pitch {1}  |  {2} wave", waveSpeed, elevationToPitch ? "height" : "fixed",
                                sphericalWave ? "spherical" : "cylindrical");
                if (room != null) sb.Append('\n').Append(room.RoomLabel);
                SweepResult shown = _current;
                if (shown != null && shown.Plan != null)
                {
                    sb.AppendFormat("\n{0} returns  |  far {1:0.0} m  |  {2:0} ms", shown.ReturnCount, shown.Encoding.Far, shown.ComputeMs);
                    if (shown.Mode == SonaRenderMode.Hrtf && shown.Hrtf != null && hrtfRenderer != null)
                    {
                        sb.AppendFormat("  |  {0} clusters, {1} voices, {2} dropped",
                                        shown.Hrtf.Clusters.Count, shown.Hrtf.PredictedMaxVoices, hrtfRenderer.DroppedClusters);
                    }
                    else if (stereoRenderer != null)
                    {
                        sb.AppendFormat("  |  {0} grains dropped", stereoRenderer.DroppedVoices);
                    }
                }
                sb.Append("</color>");
            }
            if (hud != null) hud.SetText(sb.ToString());
        }
    }
}
