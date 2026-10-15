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
// Modes (control bar): Explore (listen), Challenge (find a hidden box, guess with the laser)
// and Create room (resize the room, add/drag/rotate/delete boxes and panels).
//
// View: the world is pitch black. The sweep is drawn as glowing points in Explore and Create room
// (never in Challenge); Create room also shows a dim outline of the room so it can be edited.
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
        StereoLiveHead,
        /// <summary>Python grains + ITD/ILD with ping-time azimuths: sample-identical to the Python render.</summary>
        StereoFrozenPython,
        /// <summary>Clustered grains from their world positions through the HRTF spatializer (Meta XR Audio).</summary>
        Hrtf,
    }

    public enum SonaMode
    {
        Explore,
        Challenge,
        CreateRoom,
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
        [Tooltip("Pitch black: no sky and no lit room. You see the sweep (outside Challenge), the control bar and the " +
                 "status text. Create room shows a dim outline of the room so it can be edited. Off = lit room.")]
        public bool darkWorld = true;
        [Tooltip("Draw each sweep as glowing points in Explore and Create room. Never shown in Challenge.")]
        public bool showSweepVisuals = true;

        sealed class SweepInput
        {
            public int Id;
            public List<SurfaceQuad> Quads;
            public List<SurfacePoint> Points;
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
        string _message = "Press A (or Space) to send a sweep";
        float _hudTimer;
        float _startTime;
        readonly System.Random _rng = new System.Random();
        readonly Dictionary<Camera, KeyValuePair<CameraClearFlags, Color>> _savedCameras =
            new Dictionary<Camera, KeyValuePair<CameraClearFlags, Color>>();

        SonaMode _mode = SonaMode.Explore;
        string _selectedId;
        string _dragId;
        Vector3 _dragOffset;
        Vector3 _lastFloorPoint;
        bool _hasFloorPoint;
        int _trial;
        int _sweepsThisTrial;
        bool _guessedThisTrial;
        Transform _guessMarker;
        string _logPath;

        public SonaMode Mode { get { return _mode; } }
        public bool Blackout { get { return _blackout; } }

        /// <summary>True when sweeps are drawn right now (visuals on, not Challenge, not blackout).</summary>
        public bool SweepVisualsShown
        {
            get { return showSweepVisuals && !_blackout && _mode != SonaMode.Challenge; }
        }

        public string PanelHint
        {
            get
            {
                switch (_mode)
                {
                    case SonaMode.Challenge:
                        return "Sweep, then point at the floor and pull the trigger";
                    case SonaMode.CreateRoom:
                        return "Hold the trigger on an object to drag it";
                    default:
                        return "A: sweep.  Hold :: SONA to move the bar";
                }
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
            if (pointer.rayOrigin == null && rigRoot != null) pointer.rayOrigin = FindDeep(rigRoot, "RightHandAnchor");
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

        static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                Transform found = FindDeep(t.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
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
            _message = on ? "Repeat ON: sweeps re-ping from your current pose" : "Repeat OFF: the current sweep finishes normally";
        }

        public void SetWaveSpeed(float speed)
        {
            waveSpeed = Mathf.Clamp(Mathf.Round(speed * 4f) / 4f, 0.5f, 2f);
            _message = string.Format("Wave speed {0:0.00}x ({1:0.00} s sweep), applies to the next sweep",
                                     waveSpeed, baseSweepDuration / waveSpeed);
        }

        /// <summary>0.5x -> 0.75x -> ... -> 2x -> 0.5x.</summary>
        public void CycleWaveSpeed()
        {
            SetWaveSpeed(waveSpeed >= 1.99f ? 0.5f : waveSpeed + 0.25f);
        }

        public void CycleRenderMode()
        {
            renderMode = (SonaRenderMode)(((int)renderMode + 1) % 3);
            bool wasRepeating = repeat && _current != null;
            StopPlayback();
            _message = "Renderer: " + ModeLabel(renderMode);
            if (wasRepeating) StartJob(false);
        }

        public void TogglePitch()
        {
            elevationToPitch = !elevationToPitch;
            _message = elevationToPitch ? "Elevation -> pitch ON (Python)" : "Elevation -> pitch OFF (fixed pitch)";
        }

        public void ToggleSphericalWave()
        {
            sphericalWave = !sphericalWave;
            _message = sphericalWave ? "Spherical wave: timing from 3-D distance" : "Cylindrical wave (Python): timing from horizontal distance";
        }

        // ------------------------------------------------------------------ room actions

        public void SetWalls(WallMode walls)
        {
            if (room == null) return;
            room.SetWalls(walls);
            _message = "Walls: " + SonaRoomBuilder.WallsLabel(walls);
        }

        public void CycleWalls(int direction)
        {
            if (room == null) return;
            int n = 3;
            SetWalls((WallMode)((((int)room.walls + direction) % n + n) % n));
        }

        public void SetRoomSize(float width, float depth)
        {
            if (room == null) return;
            room.SetSize(width, depth);
            _message = "Room: " + room.RoomLabel;
        }

        public void ChangeRoomSize(float dWidth, float dDepth, float dHeight)
        {
            if (room == null) return;
            if (dHeight != 0f) room.SetHeight(room.height + dHeight);
            if (dWidth != 0f || dDepth != 0f) room.SetSize(room.width + dWidth, room.depth + dDepth);
            _message = "Room: " + room.RoomLabel;
        }

        public void ResetObjects()
        {
            if (room == null) return;
            if (_mode == SonaMode.Challenge) SetMode(SonaMode.Explore);
            room.ResetObjects();
            _selectedId = null;
            _message = "Box 1 and Panel 1 restored";
        }

        public void ClearObjects()
        {
            if (room == null) return;
            room.ClearObjects();
            _selectedId = null;
            _message = "All boxes and panels removed";
        }

        public void AddObject(ObjectKind kind)
        {
            if (room == null || head == null) return;
            if (_mode != SonaMode.CreateRoom) SetMode(SonaMode.CreateRoom);
            Vector3 spot;
            if (_hasFloorPoint)
            {
                spot = _lastFloorPoint;
            }
            else
            {
                // scene.py: spawn 1.5 m in front of the listener.
                Vector3 f = Vector3.ProjectOnPlane(head.forward, Vector3.up);
                if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
                spot = head.position + f.normalized * 1.5f;
            }
            string error;
            ObjectSpec o = room.AddObject(kind, spot, out error);
            if (o == null)
            {
                _message = "Could not add: " + error;
                return;
            }
            Select(o.Id);
            _message = o.Name + " added (hold the trigger on it to drag)";
        }

        public void RotateSelected()
        {
            if (room == null || _selectedId == null)
            {
                _message = "Select a box or panel first (trigger on it)";
                return;
            }
            room.RotateObject(_selectedId, 15f);
            _message = "Rotated 15°";
        }

        public void DeleteSelected()
        {
            if (room == null || _selectedId == null)
            {
                _message = "Select a box or panel first (trigger on it)";
                return;
            }
            room.DeleteObject(_selectedId);
            Select(null);
            _message = "Deleted";
        }

        void Select(string id)
        {
            _selectedId = id;
            if (room != null) room.SetHighlighted(id);
        }

        // ------------------------------------------------------------------ modes

        public void SetMode(SonaMode mode)
        {
            if (mode == _mode) return;
            SonaMode previous = _mode;
            _mode = mode;
            _dragId = null;
            Select(null);
            if (previous == SonaMode.Challenge)
            {
                if (room != null) room.EndChallenge();
                HideGuessMarker();
            }
            switch (mode)
            {
                case SonaMode.Challenge:
                    NewChallengeBox();
                    break;
                case SonaMode.CreateRoom:
                    _message = "Create room: add, drag, rotate and delete objects; change the room size";
                    break;
                default:
                    _message = "Explore";
                    break;
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
                HideGuessMarker();
                _message = string.Format("Trial {0}: box hidden. Sweep, then point at the floor and pull the trigger", _trial);
            }
            else
            {
                _message = "Random box failed: " + error;
            }
        }

        public void ToggleReveal()
        {
            if (room == null || !room.ChallengeActive)
            {
                _message = "No hidden box (choose Challenge mode)";
                return;
            }
            room.SetChallengeRevealed(!room.ChallengeRevealed);
            Vector3? box = room.MysteryBoxPosition;
            if (room.ChallengeRevealed && box.HasValue && head != null)
            {
                Vector3 d = box.Value - head.position;
                float az = BearingFromHead(box.Value);
                _message = string.Format("Box: {0:0.0} m away, {1:0}° {2}",
                                         new Vector2(d.x, d.z).magnitude, Mathf.Abs(az), az >= 0 ? "right" : "left");
            }
            else
            {
                _message = room.ChallengeRevealed ? "Box revealed" : "Box hidden";
            }
        }

        /// <summary>Hide everything, including the bar, the laser and the status text (blind trials).</summary>
        public void SetBlackout(bool on)
        {
            _blackout = on;
            ApplyView();
            if (hud != null) hud.SetVisible(!on);
            if (pointer != null) pointer.Visible = !on;
            if (settingsPanel != null && on) settingsPanel.SetVisible(false);
            if (_guessMarker != null) _guessMarker.gameObject.SetActive(!on && _guessMarker.gameObject.activeSelf);
            _message = on ? "Blackout ON (left trigger / B to turn off)" : "Blackout off";
        }

        /// <summary>Pitch black (default) or a lit room for sighted demos.</summary>
        public void SetDarkWorld(bool dark)
        {
            darkWorld = dark;
            ApplyView();
            _message = dark ? "Dark: only the sweep is visible" : "Lit: the room is visible (for sighted demos)";
        }

        public void ToggleSweepVisuals()
        {
            showSweepVisuals = !showSweepVisuals;
            ApplyView();
            if (!showSweepVisuals) _message = "Sweep visuals off: sound only";
            else if (_mode == SonaMode.Challenge) _message = "Sweep visuals on (they stay hidden in Challenge)";
            else _message = "Sweep visuals on";
        }

        /// <summary>Room look, sweep visuals and camera background for the current mode.</summary>
        void ApplyView()
        {
            if (room != null)
            {
                room.SetVisible(!_blackout);
                SonaRoomView view = SonaRoomView.Lit;
                if (darkWorld) view = _mode == SonaMode.CreateRoom ? SonaRoomView.Ghost : SonaRoomView.Hidden;
                room.SetView(view);
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

        float BearingFromHead(Vector3 world)
        {
            Vector3 f = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (f.sqrMagnitude < 1e-6f) f = Vector3.forward;
            return Vector3.SignedAngle(f.normalized, Vector3.ProjectOnPlane(world - head.position, Vector3.up), Vector3.up);
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
                if (_input.Menu && settingsPanel != null) settingsPanel.Toggle();
                if (settingsPanel != null)
                {
                    clickUsed = settingsPanel.Tick(pointer, _input.SelectDown, _input.SelectHeld, _input.SelectUp);
                }
                HandleButtons();
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

        void HandleButtons()
        {
            if (_input.Trigger) Trigger();
            if (_input.Repeat) SetRepeat(!repeat);
            if (_input.SpeedStep != 0) SetWaveSpeed(waveSpeed + 0.25f * _input.SpeedStep);
            if (_input.Mode) CycleRenderMode();
            if (_input.Random) NewChallengeBox();
            if (_input.Reveal) ToggleReveal();
            if (_input.WallStep != 0) CycleWalls(_input.WallStep);
            if (_input.Pitch) TogglePitch();
            if (_input.Blackout) SetBlackout(!_blackout);
        }

        /// <summary>Laser interactions outside the panel: drag objects (Create) or place a guess (Challenge).</summary>
        void HandlePointer(bool clickUsedByPanel)
        {
            if (pointer == null || room == null) return;
            // Where the ray meets the floor plane, and whether that spot is actually visible (not behind a wall).
            Vector3 planePoint = Vector3.zero;
            bool onPlane = !pointer.HitPanel && pointer.RayToPlane(room.FloorY, out planePoint);
            bool visible = onPlane;
            if (onPlane && pointer.HasHit && !pointer.HitIsFloor)
            {
                float toPlane = Vector3.Distance(pointer.CurrentRay.origin, planePoint);
                float toHit = Vector3.Distance(pointer.CurrentRay.origin, pointer.HitPoint);
                if (toHit < toPlane - 0.01f) visible = false;
            }
            _hasFloorPoint = visible;
            if (visible) _lastFloorPoint = planePoint;

            if (_mode == SonaMode.CreateRoom)
            {
                if (_input.SelectDown && !clickUsedByPanel)
                {
                    string id = pointer.HasHit ? room.ObjectIdOf(pointer.HitCollider) : null;
                    Select(id);
                    if (id != null && onPlane)
                    {
                        GameObject go = room.GetObject(id);
                        _dragId = id;
                        Vector3 c = go.transform.position;
                        _dragOffset = new Vector3(c.x - planePoint.x, 0f, c.z - planePoint.z);
                        StopPlayback();
                    }
                }
                if (_dragId != null)
                {
                    // Dragging follows the floor plane even when the ray passes through the object itself.
                    if (_input.SelectHeld && onPlane) room.MoveObject(_dragId, planePoint + _dragOffset);
                    if (_input.SelectUp || !_input.SelectHeld)
                    {
                        _dragId = null;
                        room.NotifyChanged();
                        _message = "Moved (trigger on the floor to deselect)";
                    }
                }
            }
            else if (_mode == SonaMode.Challenge)
            {
                if (_input.SelectDown && !clickUsedByPanel && visible) Guess(planePoint);
            }
        }

        void Guess(Vector3 floorPoint)
        {
            Vector3? boxPos = room.MysteryBoxPosition;
            if (!boxPos.HasValue || head == null) return;
            Vector3 box = boxPos.Value;
            float error = new Vector2(box.x - floorPoint.x, box.z - floorPoint.z).magnitude;
            float boxBearing = BearingFromHead(box);
            float guessBearing = BearingFromHead(floorPoint);
            float bearingError = Mathf.DeltaAngle(boxBearing, guessBearing);
            float boxDist = new Vector2(box.x - head.position.x, box.z - head.position.z).magnitude;
            float guessDist = new Vector2(floorPoint.x - head.position.x, floorPoint.z - head.position.z).magnitude;
            ShowGuessMarker(floorPoint);
            room.SetChallengeRevealed(true);
            _message = string.Format("Trial {0}: off by {1:0.00} m ({2:+0;-0}°, {3:+0.00;-0.00} m) after {4} sweep{5}. X = next box",
                                     _trial, error, bearingError, guessDist - boxDist, _sweepsThisTrial, _sweepsThisTrial == 1 ? "" : "s");
            if (logChallengeTrials && !_guessedThisTrial) LogTrial(box, floorPoint, error, boxBearing, guessBearing, boxDist, guessDist);
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

        void LogTrial(Vector3 box, Vector3 guess, float error, float boxBearing, float guessBearing, float boxDist, float guessDist)
        {
            try
            {
                if (_logPath == null) _logPath = Path.Combine(Application.persistentDataPath, "sona_challenge_log.csv");
                bool newFile = !File.Exists(_logPath);
                var inv = CultureInfo.InvariantCulture;
                var sb = new StringBuilder();
                if (newFile)
                {
                    sb.AppendLine("time,trial,renderer,pitch,wave_speed,wave_shape,walls,room_w,room_d,room_h,sweeps," +
                                  "box_x,box_z,guess_x,guess_z,error_m,box_dist_m,guess_dist_m,box_bearing_deg,guess_bearing_deg,bearing_error_deg");
                }
                sb.AppendLine(string.Join(",", new[]
                {
                    DateTime.Now.ToString("s", inv), _trial.ToString(inv), renderMode.ToString(),
                    elevationToPitch ? "elevation" : "fixed", waveSpeed.ToString("0.00", inv), sphericalWave ? "spherical" : "cylindrical",
                    room.walls.ToString(), room.width.ToString("0.0", inv), room.depth.ToString("0.0", inv), room.height.ToString("0.0", inv),
                    _sweepsThisTrial.ToString(inv),
                    box.x.ToString("0.000", inv), box.z.ToString("0.000", inv), guess.x.ToString("0.000", inv), guess.z.ToString("0.000", inv),
                    error.ToString("0.000", inv), boxDist.ToString("0.000", inv), guessDist.ToString("0.000", inv),
                    boxBearing.ToString("0.0", inv), guessBearing.ToString("0.0", inv), Mathf.DeltaAngle(boxBearing, guessBearing).ToString("0.0", inv),
                }));
                File.AppendAllText(_logPath, sb.ToString());
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SONA] Could not write the challenge log: " + e.Message);
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
                _message = "No head camera found (assign Head on the SONA Sweep Controller)";
                return null;
            }
            var quads = new List<SurfaceQuad>(64);
            var points = new List<SurfacePoint>();
            List<SonaSurface> surfaces = SonaSurface.Active;
            for (int i = 0; i < surfaces.Count; i++)
            {
                surfaces[i].AppendQuads(quads);
                surfaces[i].AppendPoints(points, samplesPerMeter);
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
                Points = points,
                Pose = ListenerPose3D.FromForward(head.position.ToVec3d(), head.forward.ToVec3d(), head.up.ToVec3d()),
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
                                                                      input.Points, null, input.Spherical);
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
                _message = "Sweep failed: " + task.Exception.GetBaseException().Message;
                Debug.LogException(task.Exception.GetBaseException(), this);
                return;
            }
            SweepResult r = task.Result;
            if (r.Id != _jobId) return;   // superseded by a newer trigger or a stop
            if (r.Error != null)
            {
                _message = "Sweep failed: " + r.Error;
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
                case SonaRenderMode.StereoFrozenPython: return "Stereo, frozen (Python-identical)";
                default: return "HRTF (Meta XR Audio)";
            }
        }

        static string ModeName(SonaMode m)
        {
            switch (m)
            {
                case SonaMode.Challenge: return "Challenge";
                case SonaMode.CreateRoom: return "Create room";
                default: return "Explore";
            }
        }

        void UpdateHud()
        {
            _hudTimer -= Time.unscaledDeltaTime;
            if (_hudTimer > 0f) return;
            _hudTimer = 0.2f;

            if (head != null && room != null && Time.time - _startTime > 3f && UnityEngine.XR.XRSettings.isDeviceActive &&
                head.position.y - room.transform.position.y < 0.5f)
            {
                _message = "Head is below 0.5 m: set OVRManager Tracking Origin Type = Floor Level";
            }

            var sb = new StringBuilder(256);
            sb.Append("SONA  ").Append(ModeName(_mode)).Append("  |  ").Append(ModeLabel(renderMode)).Append('\n');
            sb.AppendFormat("speed {0:0.00}x  |  repeat {1}  |  pitch {2}  |  {3} wave\n",
                            waveSpeed, repeat ? "ON" : "off", elevationToPitch ? "elevation" : "fixed",
                            sphericalWave ? "spherical" : "cylindrical");
            if (room != null) sb.Append(room.RoomLabel).Append('\n');
            SweepResult shown = _current;
            if (shown != null && shown.Plan != null)
            {
                sb.AppendFormat("{0} returns  |  far {1:0.0} m  |  {2:0} ms", shown.ReturnCount, shown.Encoding.Far, shown.ComputeMs);
                if (shown.Mode == SonaRenderMode.Hrtf && shown.Hrtf != null && hrtfRenderer != null)
                {
                    sb.AppendFormat("  |  {0} clusters, {1} voices, {2} dropped",
                                    shown.Hrtf.Clusters.Count, shown.Hrtf.PredictedMaxVoices, hrtfRenderer.DroppedClusters);
                }
                sb.Append('\n');
            }
            sb.Append(_message).Append('\n');
            sb.Append("A sweep   Menu controls   Trigger select   L-trigger blackout");
            if (hud != null) hud.SetText(sb.ToString());
        }
    }
}
