// SONA Quest port: the Study mode for Proposal 1.2 (does the learned delay cue fuse with DRR?).
//
// Seated and head-locked: Core (SonaStudy.cs) renders each trial into one buffer, played as a single AudioClip,
// so every onset is sample-exact. Nothing visible changes while a trial plays. Oddity blocks run Negen's
// interleaved staircases and take the answer on three spheres; training plays one sound, takes a distance on a
// floor line and then shows where the box was. Opening the menu or taking the headset off pauses the block and
// throws away the trial in progress. Every answered trial is appended to sona_study_log.csv.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Sona.Core;
using UnityEngine;

namespace Sona
{
    public enum StudyBlock
    {
        None,
        Fusion,       // congruent and opposite changes, both signs (Negen et al. 2023's four tracks)
        SingleCues,   // delay alone (dry) and DRR alone (no click), both signs
        CueAxes,      // one cue changes while the other stays, both cues present, both signs
        Training,     // one sound, distance answer, feedback
    }

    [AddComponentMenu("SONA/SONA Study")]
    public sealed class SonaStudyController : MonoBehaviour
    {
        public SonaSweepController controller;
        [Tooltip("Trials per staircase in an oddity block (Negen et al. 2023 used 65).")]
        [Min(1)] public int trialsPerStaircase = 65;
        [Tooltip("Training trials: half delay alone, half delay with DRR.")]
        [Min(2)] public int trainingTrials = 120;
        [Tooltip("Silence after a trial before answers are taken (s).")]
        public float answerDelay = 0.2f;
        [Tooltip("Pause between an answer and the next trial (s).")]
        public float interTrialPause = 0.8f;
        [Tooltip("How long training feedback stays up (s).")]
        public float feedbackSeconds = 2f;
        public string logFileName = "sona_study_log.csv";
        public int participant = 1;
        public int session = 1;

        enum Phase { Idle, Waiting, Playing, Answering, Feedback, Paused, Done }

        sealed class Track
        {
            public StudyCondition Condition;
            public int Sign;
            public Staircase Stairs;
            public string Name { get { return Condition.Name + (Sign > 0 ? "+" : "-"); } }
        }

        static readonly Color SphereLocked = new Color(0.16f, 0.18f, 0.22f);
        static readonly Color SphereOpen = new Color(0.45f, 0.68f, 0.85f);
        static readonly Color SphereHover = new Color(0.95f, 0.97f, 1f);

        StudyParams _params;
        StudyStimulus _stimulus;
        Task<StudyStimulus> _building;
        string _buildError;
        AudioSource _source;
        AudioClip _clip;

        Phase _phase = Phase.Idle;
        StudyBlock _block;
        bool _waitingForSounds;
        StudyRandom _rng;
        ulong _seed;
        readonly List<Track> _tracks = new List<Track>();
        StudyInterval[] _training;
        int _done;
        int _total;
        Track _track;
        StudyInterval[] _intervals;
        int _odd;
        double _answerDsp;
        float _answersOpenedAt;
        float _phaseUntil;
        int _volume;
        string _output;
        float _buildStarted;
        string _feedback;
        string _hint;
        string _logPath;
        readonly string _launch = DateTime.Now.ToString("s", CultureInfo.InvariantCulture);

        Transform _objects;
        readonly Transform[] _spheres = new Transform[3];
        Transform _line, _box, _marker;
        Vector3 _origin, _forward;

        public StudyBlock Block { get { return _block; } }
        public bool Running { get { return _block != StudyBlock.None && _phase != Phase.Done; } }

        float FloorY { get { return controller != null && controller.room != null ? controller.room.FloorY : 0f; } }

        void Awake()
        {
            var audioGo = new GameObject("SONA Study Audio");
            audioGo.transform.SetParent(transform, false);
            _source = audioGo.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.loop = false;
            _source.spatialize = false;
            _source.spatialBlend = 0f;
            _source.bypassEffects = true;
            _source.bypassListenerEffects = true;
            _source.bypassReverbZones = true;
            _source.priority = 0;
            _source.volume = 1f;
        }

        // ------------------------------------------------------------------ mode, blocks, settings

        /// <summary>The controller entered Study: start preparing the sounds (once per launch).</summary>
        public void Enter()
        {
            EnsureObjects();
            if (_stimulus != null || _building != null) return;
            var p = new StudyParams { SampleRate = AudioSettings.outputSampleRate };
            _params = p;
            _buildError = null;
            _buildStarted = Time.realtimeSinceStartup;
            _building = Task.Run(() => new StudyStimulus(p));
        }

        /// <summary>The controller left Study.</summary>
        public void Exit()
        {
            StopBlock();
            if (_objects != null) _objects.gameObject.SetActive(false);
        }

        public void StartBlock(StudyBlock block)
        {
            StopBlock();
            if (block == StudyBlock.None) return;
            _block = block;
            _done = 0;
            _seed = (ulong)DateTime.UtcNow.Ticks;
            _rng = new StudyRandom(_seed);
            _waitingForSounds = _stimulus == null;
            if (!_waitingForSounds) BeginBlock();
        }

        public void StopBlock()
        {
            StopSound();
            _block = StudyBlock.None;
            _phase = Phase.Idle;
            _waitingForSounds = false;
            _tracks.Clear();
            _training = null;
            ShowObjects();
        }

        public void ChangeParticipant(int step) { participant = Mathf.Clamp(participant + step, 1, 99); }
        public void SetSession(int s) { session = Mathf.Clamp(s, 1, 9); }

        void BeginBlock()
        {
            _waitingForSounds = false;
            _tracks.Clear();
            if (_block == StudyBlock.Training)
            {
                _training = StudyTrials.TrainingSchedule(_params, Mathf.Max(1, trainingTrials / 2), _rng);
                _total = _training.Length;
            }
            else
            {
                foreach (StudyCondition c in StudyCondition.All)
                {
                    if (!InBlock(c, _block)) continue;
                    foreach (int sign in new[] { 1, -1 })
                    {
                        double cap = StudyTrials.MaxDelta(_params, c, sign);
                        _tracks.Add(new Track { Condition = c, Sign = sign, Stairs = new Staircase(0.2, 0.9, 3, cap, trialsPerStaircase) });
                    }
                }
                _total = _tracks.Count * trialsPerStaircase;
            }
            PlaceObjects();
            Wait(interTrialPause);
        }

        static bool InBlock(StudyCondition c, StudyBlock block)
        {
            switch (block)
            {
                case StudyBlock.Fusion: return c.Name == "congruent" || c.Name == "opposite";
                case StudyBlock.SingleCues: return c.Name == "delay-only" || c.Name == "drr-only";
                case StudyBlock.CueAxes: return c.Name == "delay-axis" || c.Name == "drr-axis";
                default: return false;
            }
        }

        public static string BlockLabel(StudyBlock block)
        {
            switch (block)
            {
                case StudyBlock.Fusion: return "Fusion";
                case StudyBlock.SingleCues: return "Single cues";
                case StudyBlock.CueAxes: return "Cue axes";
                case StudyBlock.Training: return "Training";
                default: return "No block";
            }
        }

        // ------------------------------------------------------------------ frame loop

        void Update()
        {
            if (_building != null && _building.IsCompleted)
            {
                if (_building.IsFaulted)
                {
                    _buildError = "Could not prepare the sounds: " + _building.Exception.GetBaseException().Message;
                    Debug.LogException(_building.Exception.GetBaseException(), this);
                }
                else
                {
                    _stimulus = _building.Result;
                    Debug.Log(string.Format("[SONA] Study sounds ready: {0} Hz, prepared in {1:0.0} s",
                                            _params.SampleRate, Time.realtimeSinceStartup - _buildStarted));
                }
                _building = null;
                if (_stimulus != null && _waitingForSounds) BeginBlock();
            }
            if (_block == StudyBlock.None || _waitingForSounds || _phase == Phase.Done) return;

            bool menuOpen = controller != null && controller.settingsPanel != null && controller.settingsPanel.Visible;
            if (menuOpen)
            {
                if (_phase != Phase.Paused) Pause();
                return;
            }
            switch (_phase)
            {
                case Phase.Paused:
                    Wait(interTrialPause);
                    break;
                case Phase.Waiting:
                    if (Time.unscaledTime >= _phaseUntil) NextTrial();
                    break;
                case Phase.Playing:
                    if (AudioSettings.dspTime >= _answerDsp) OpenAnswers();
                    break;
                case Phase.Answering:
                    if (_block != StudyBlock.Training) HoverSpheres();
                    break;
                case Phase.Feedback:
                    if (Time.unscaledTime >= _phaseUntil)
                    {
                        _feedback = null;
                        ShowObjects();
                        Wait(interTrialPause);
                    }
                    break;
            }
        }

        void OnApplicationPause(bool paused)
        {
            // Taking the headset off: the trial in progress no longer counts.
            if (paused && Running && _phase != Phase.Paused) Pause();
        }

        void OnDisable()
        {
            StopSound();
        }

        void Pause()
        {
            StopSound();
            _feedback = null;
            _phase = Phase.Paused;
            ShowObjects();
        }

        void Wait(float seconds)
        {
            _phase = Phase.Waiting;
            _phaseUntil = Time.unscaledTime + seconds;
            ShowObjects();
        }

        void NextTrial()
        {
            _hint = null;
            if (_block == StudyBlock.Training)
            {
                if (_done >= _training.Length)
                {
                    Finish();
                    return;
                }
                _intervals = new[] { _training[_done] };
            }
            else
            {
                List<Track> open = _tracks.FindAll(t => !t.Stairs.Done);
                if (open.Count == 0)
                {
                    Finish();
                    return;
                }
                _track = open[_rng.Next(open.Count)];
                _intervals = StudyTrials.Plan(_params, _track.Condition, _track.Stairs.Value, _track.Sign, _rng, out _odd);
            }
            Play(_stimulus.RenderTrial(_intervals));
            _phase = Phase.Playing;
            ShowObjects();
        }

        void Play(float[] interleaved)
        {
            int frames = interleaved.Length / 2;
            if (_clip != null) Destroy(_clip);
            _clip = AudioClip.Create("SONA study trial", frames, 2, _params.SampleRate, false);
            _clip.SetData(interleaved, 0);
            _source.clip = _clip;
            double start = AudioSettings.dspTime + 0.1;
            _source.PlayScheduled(start);
            _answerDsp = start + frames / (double)_params.SampleRate + answerDelay;
            _volume = MediaVolume();
            _output = OutputDevice();
        }

        void StopSound()
        {
            if (_source != null) _source.Stop();
        }

        void OpenAnswers()
        {
            _phase = Phase.Answering;
            _answersOpenedAt = Time.realtimeSinceStartup;
            ShowObjects();
        }

        void Finish()
        {
            _phase = Phase.Done;
            ShowObjects();
        }

        // ------------------------------------------------------------------ answers

        /// <summary>The trigger (or a click) in Study mode, after the menu had its chance at it.</summary>
        public void Select()
        {
            if (_phase != Phase.Answering) return;
            SonaPointer p = controller != null ? controller.pointer : null;
            if (_block == StudyBlock.Training)
            {
                Vector3 hit;
                if (p != null && p.RayToPlane(FloorY, out hit))
                {
                    float d = Vector3.Dot(hit - _origin, _forward);
                    if (d > 0.1f)
                    {
                        AnswerDistance(d);
                        return;
                    }
                }
                _hint = "Point at the floor line in front of you";
                return;
            }
            for (int k = 0; k < 3; k++)
            {
                if (p != null && p.HitCollider != null && p.HitCollider.transform == _spheres[k])
                {
                    Answer(k);
                    return;
                }
            }
            _hint = "Point at one of the three spheres";
        }

        /// <summary>Oddity answer: which interval (0, 1 or 2) sounded different.</summary>
        public void Answer(int interval)
        {
            if (_phase != Phase.Answering || _block == StudyBlock.Training || interval < 0 || interval > 2) return;
            double delta = _track.Stairs.Value;
            bool correct = interval == _odd;
            _track.Stairs.Update(correct);
            _done++;
            Log(delta, interval, correct, double.NaN);
            Wait(interTrialPause);
        }

        /// <summary>Training answer: how far ahead the box starts, in metres.</summary>
        public void AnswerDistance(float metres)
        {
            if (_phase != Phase.Answering || _block != StudyBlock.Training) return;
            double target = _intervals[0].DelayDistance;
            _done++;
            Log(double.NaN, -1, false, metres);
            double error = 100.0 * (metres - target) / target;
            _feedback = string.Format(CultureInfo.InvariantCulture, "The box was at {0:0.0} m. You were {1:0}% too {2}.",
                                      target, Math.Abs(error), error >= 0 ? "far" : "near");
            _phase = Phase.Feedback;
            _phaseUntil = Time.unscaledTime + feedbackSeconds;
            ShowObjects();
            _box.position = _origin + _forward * (float)(target + 0.5) + Vector3.up * 0.01f;
            _box.rotation = Quaternion.LookRotation(_forward, Vector3.up);
            _marker.position = _origin + _forward * metres + Vector3.up * 0.03f;
        }

        // ------------------------------------------------------------------ text

        /// <summary>The HUD in Study mode. It only changes between trials, never while a trial plays.</summary>
        public string HudText(bool xr)
        {
            string trigger = xr ? "Trigger" : "Click", menu = xr ? "Menu" : "Tab";
            var sb = new StringBuilder(160);
            sb.AppendFormat("<b>Study  ·  P{0:00}  ·  session {1}", participant, session);
            if (_block != StudyBlock.None) sb.AppendFormat("  ·  {0}  ·  {1} of {2}", BlockLabel(_block), _done, _total);
            sb.Append("</b>\n");
            if (_buildError != null) sb.Append(_buildError);
            else if (_block == StudyBlock.None) sb.Append(_stimulus == null ? "Preparing the sounds..." : "No block running");
            else if (_waitingForSounds) sb.Append("Preparing the sounds...");
            else
            {
                switch (_phase)
                {
                    case Phase.Waiting:
                    case Phase.Playing:
                        sb.Append(_block == StudyBlock.Training ? "Listen" : "Listen to the three sounds");
                        break;
                    case Phase.Answering:
                        sb.Append(_block == StudyBlock.Training
                            ? "Point at the floor where the box starts, then " + trigger
                            : "Which sound was different? Point at its sphere, then " + trigger);
                        if (_hint != null) sb.Append('\n').Append(_hint);
                        break;
                    case Phase.Feedback:
                        sb.Append("<color=#ffd27a>").Append(_feedback).Append("</color>");
                        break;
                    case Phase.Paused:
                        sb.Append("Paused. Close the menu to go on.");
                        break;
                    case Phase.Done:
                        sb.AppendFormat("Block done: {0} trials saved", _done);
                        break;
                }
            }
            sb.Append("\n<color=#9fb3c8>").Append(menu).Append(_block == StudyBlock.None || _phase == Phase.Done
                ? ": menu, then Study to start a block" : ": pause and open the menu").Append("</color>");
            return sb.ToString();
        }

        /// <summary>Status line for the Study tab.</summary>
        public string MenuStatus()
        {
            if (_buildError != null) return _buildError;
            if (_block == StudyBlock.None) return _stimulus == null && _building != null ? "Preparing the sounds..." : "No block running";
            var sb = new StringBuilder();
            sb.AppendFormat("{0}: {1} of {2} trials{3}", BlockLabel(_block), _done, _total, _phase == Phase.Done ? ", done" : "");
            foreach (Track t in _tracks)
                sb.AppendFormat(CultureInfo.InvariantCulture, "\n{0}: {1:0.000} after {2} trials{3}", t.Name, t.Stairs.Value, t.Stairs.Trials,
                                t.Stairs.CapHits > 0 ? ", " + t.Stairs.CapHits + " at the cap" : "");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ objects

        void EnsureObjects()
        {
            if (_objects != null)
            {
                _objects.gameObject.SetActive(true);
                return;
            }
            _objects = new GameObject("SONA Study Objects").transform;
            _objects.SetParent(transform, false);
            for (int k = 0; k < 3; k++)
            {
                // The spheres keep their colliders: the laser picks the answer by hitting one.
                _spheres[k] = MakePrimitive(PrimitiveType.Sphere, "Answer " + (k + 1), true, SphereLocked);
                _spheres[k].localScale = Vector3.one * 0.2f;
            }
            _line = new GameObject("Distance line").transform;
            _line.SetParent(_objects, false);
            Color lineColor = new Color(0.35f, 0.45f, 0.6f);
            Transform strip = MakePrimitive(PrimitiveType.Cube, "Strip", false, lineColor);
            strip.SetParent(_line, false);
            float near = (float)new StudyParams().MinDistance, far = (float)new StudyParams().Far;
            strip.localPosition = new Vector3(0f, 0.002f, (near + far) / 2f);
            strip.localScale = new Vector3(0.02f, 0.004f, far - near);
            for (int m = 1; m <= (int)far; m++)
            {
                Transform tick = MakePrimitive(PrimitiveType.Cube, m + " m", false, lineColor);
                tick.SetParent(_line, false);
                tick.localPosition = new Vector3(0f, 0.002f, m);
                tick.localScale = new Vector3(0.16f, 0.004f, 0.015f);
            }
            // The box's footprint, flat, so a guess that lands inside it stays visible on top.
            _box = MakePrimitive(PrimitiveType.Cube, "Box", false, new Color(1f, 0.55f, 0.15f));
            _box.localScale = new Vector3(1f, 0.02f, 1f);
            _marker = MakePrimitive(PrimitiveType.Cylinder, "Guess", false, new Color(0.3f, 1f, 0.45f));
            _marker.localScale = new Vector3(0.25f, 0.005f, 0.25f);
            ShowObjects();
        }

        Transform MakePrimitive(PrimitiveType type, string name, bool keepCollider, Color color)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(_objects, false);
            if (!keepCollider) Destroy(go.GetComponent<Collider>());
            var r = go.GetComponent<Renderer>();
            Material mat = SonaMaterialUtil.FallbackUnlit();
            if (mat != null) r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Tint(r, color);
            return go.transform;
        }

        static void Tint(Renderer r, Color c)
        {
            var mpb = new MaterialPropertyBlock();
            mpb.SetColor("_BaseColor", c);
            mpb.SetColor("_Color", c);
            r.SetPropertyBlock(mpb);
        }

        /// <summary>Lay the spheres and the line out from where the participant faces when a block starts.</summary>
        void PlaceObjects()
        {
            Transform head = controller != null ? controller.head : null;
            if (head == null || _objects == null) return;
            Vector3 f = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (f.sqrMagnitude < 1e-4f) f = Vector3.ProjectOnPlane(head.up, Vector3.up);
            _forward = f.sqrMagnitude < 1e-4f ? Vector3.forward : f.normalized;
            _origin = new Vector3(head.position.x, FloorY, head.position.z);
            Vector3 right = Vector3.Cross(Vector3.up, _forward);
            for (int k = 0; k < 3; k++)
                _spheres[k].position = head.position + _forward * 1.5f + right * ((k - 1) * 0.4f) - Vector3.up * 0.15f;
            _line.SetPositionAndRotation(_origin, Quaternion.LookRotation(_forward, Vector3.up));
        }

        void ShowObjects()
        {
            if (_objects == null) return;
            bool oddity = _block != StudyBlock.None && _block != StudyBlock.Training && !_waitingForSounds;
            bool training = _block == StudyBlock.Training && !_waitingForSounds;
            for (int k = 0; k < 3; k++)
            {
                _spheres[k].gameObject.SetActive(oddity && _phase != Phase.Done);
                Tint(_spheres[k].GetComponent<Renderer>(), _phase == Phase.Answering ? SphereOpen : SphereLocked);
            }
            _line.gameObject.SetActive(training && _phase != Phase.Done);
            _box.gameObject.SetActive(training && _phase == Phase.Feedback);
            _marker.gameObject.SetActive(training && _phase == Phase.Feedback);
        }

        void HoverSpheres()
        {
            SonaPointer p = controller != null ? controller.pointer : null;
            for (int k = 0; k < 3; k++)
            {
                bool hovered = p != null && p.HitCollider != null && p.HitCollider.transform == _spheres[k];
                Tint(_spheres[k].GetComponent<Renderer>(), hovered ? SphereHover : SphereOpen);
            }
        }

        // ------------------------------------------------------------------ log

        static string Num(double v, string format)
        {
            if (double.IsNaN(v)) return "";
            if (double.IsPositiveInfinity(v)) return "inf";
            return v.ToString(format, CultureInfo.InvariantCulture);
        }

        static readonly string Header = MakeHeader();

        static string MakeHeader()
        {
            var sb = new StringBuilder("launch,time,participant,session,block,block_trial,condition,delta,odd,response,correct,rt_s,next_delta,cap_hits," +
                                       "target_m,response_m,error_pct,sample_rate,media_volume,output,seed");
            for (int k = 1; k <= 3; k++)
                sb.AppendFormat(",delay_m_{0},drr_m_{0},onset_s_{0},drr_db_{0},signal_drr_db_{0},gain_db_{0},box_rms_{0},peak_{0},box_sample_{0}", k);
            return sb.ToString();
        }

        void Log(double delta, int response, bool correct, double responseMetres)
        {
            var inv = CultureInfo.InvariantCulture;
            bool training = _block == StudyBlock.Training;
            try
            {
                if (_logPath == null)
                {
                    _logPath = Path.Combine(Application.persistentDataPath, logFileName);
                    KeepOldLog(_logPath);
                }
                var sb = new StringBuilder();
                if (!File.Exists(_logPath)) sb.Append(Header).Append('\n');
                StudyInterval first = _intervals[0];
                string condition = training
                    ? (double.IsPositiveInfinity(first.DrrDb) ? "train-delay" : "train-paired")
                    : _track.Name;
                var cells = new List<string>
                {
                    _launch, DateTime.Now.ToString("s", inv), participant.ToString(inv), session.ToString(inv), BlockLabel(_block),
                    _done.ToString(inv), condition, Num(delta, "0.00000"),
                    training ? "" : (_odd + 1).ToString(inv), training ? "" : (response + 1).ToString(inv), training ? "" : (correct ? "1" : "0"),
                    (Time.realtimeSinceStartup - _answersOpenedAt).ToString("0.000", inv),
                    training ? "" : Num(_track.Stairs.Value, "0.00000"), training ? "" : _track.Stairs.CapHits.ToString(inv),
                    training ? Num(first.DelayDistance, "0.000") : "", Num(responseMetres, "0.000"),
                    training ? Num(100.0 * (responseMetres - first.DelayDistance) / first.DelayDistance, "0.0") : "",
                    _params.SampleRate.ToString(inv), _volume.ToString(inv), _output, _seed.ToString(inv),
                };
                for (int k = 0; k < 3; k++)
                {
                    if (k >= _intervals.Length)
                    {
                        for (int i = 0; i < 9; i++) cells.Add("");
                        continue;
                    }
                    StudyInterval iv = _intervals[k];
                    cells.Add(Num(iv.DelayDistance, "0.0000"));
                    cells.Add(Num(iv.DrrDistance, "0.0000"));
                    cells.Add(Num(iv.Onset, "0.000000"));
                    cells.Add(Num(iv.DrrDb, "0.000"));
                    cells.Add(Num(iv.SignalDrrDb, "0.000"));
                    cells.Add(Num(iv.GainDb, "0.000"));
                    cells.Add(Num(iv.BoxRms, "0.000000"));
                    cells.Add(Num(iv.Peak, "0.000000"));
                    cells.Add(iv.BoxSample.ToString(inv));
                }
                sb.Append(string.Join(",", cells)).Append('\n');
                File.AppendAllText(_logPath, sb.ToString());
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SONA] Could not write the study log: " + e.Message);
                _hint = "Could not save this trial: " + e.Message;
            }
        }

        /// <summary>A log written with other columns is kept under another name, so new rows never land under the wrong header.</summary>
        static void KeepOldLog(string path)
        {
            if (!File.Exists(path)) return;
            string first;
            using (var reader = new StreamReader(path)) first = reader.ReadLine();
            if (first == Header) return;
            string kept = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + "_old_" +
                                       DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv");
            File.Move(path, kept);
            Debug.Log("[SONA] The study log had other columns; kept it as " + kept);
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        static AndroidJavaObject AudioService()
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                return activity.Call<AndroidJavaObject>("getSystemService", "audio");
            }
        }
#endif

        /// <summary>Android media volume step, so a bumped volume button shows up in the log; -1 off the headset.</summary>
        static int MediaVolume()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var audio = AudioService()) return audio.Call<int>("getStreamVolume", 3);   // AudioManager.STREAM_MUSIC
            }
            catch (Exception)
            {
                return -1;
            }
#else
            return -1;
#endif
        }

        /// <summary>Where the sound goes: wired, bluetooth or speaker on the headset, so a session without headphones shows up.</summary>
        static string OutputDevice()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var audio = AudioService())
                {
                    if (audio.Call<bool>("isWiredHeadsetOn")) return "wired";
                    if (audio.Call<bool>("isBluetoothA2dpOn")) return "bluetooth";
                    return "speaker";
                }
            }
            catch (Exception)
            {
                return "unknown";
            }
#else
            return "editor";
#endif
        }
    }
}
