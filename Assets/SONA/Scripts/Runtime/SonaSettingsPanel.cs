// SONA Quest port: the control bar. A small floating bar that you can move, fold and close:
//
//   [:: SONA] [Explore] [Room] [Sweep] [Tools] [_] [X]
//
//   Explore  (mode)  Explore | Challenge | Create room
//   Room             walls, size, light (dark / lit)
//   Sweep            sweep now, repeat, speed, wave shape, renderer, pitch, sweep visuals
//   Tools            what the current mode needs (challenge box, room editing, blackout, ...)
//
// Each tab opens a popup above the bar. To move the bar, hold the trigger (or the left mouse button)
// on ":: SONA" or on the bar's background and point somewhere else; it keeps facing you. "_" folds it
// to [:: SONA] [+], "X" closes it, and the left Menu button (Tab) opens it in front of you again.
using System;
using System.Collections.Generic;
using Sona.Core;
using UnityEngine;

namespace Sona
{
    /// <summary>One 3-D push button of the control bar.</summary>
    public sealed class SonaButton : MonoBehaviour
    {
        public Action OnClick;
        public Func<string> Label;
        public Func<bool> IsOn;

        internal TextMesh Text;
        internal Renderer Body;
        internal Color? NormalColor;
        MaterialPropertyBlock _mpb;
        Color _lastColor = new Color(-1f, 0f, 0f);

        internal void RefreshLabel()
        {
            if (Label == null || Text == null) return;
            string s = Label();
            if (Text.text != s) Text.text = s;
        }

        internal void RefreshColor(bool hovered, Color normal, Color hover, Color on)
        {
            bool isOn = IsOn != null && IsOn();
            Color c = isOn ? on : (NormalColor ?? normal);
            if (hovered) c = Color.Lerp(c, hover, 0.6f) * 1.15f;
            c.a = 1f;
            if (Body != null && c != _lastColor)
            {
                if (_mpb == null) _mpb = new MaterialPropertyBlock();
                _mpb.SetColor("_BaseColor", c);
                _mpb.SetColor("_Color", c);
                Body.SetPropertyBlock(_mpb);
                _lastColor = c;
            }
        }
    }

    [AddComponentMenu("SONA/SONA Control Bar")]
    public sealed class SonaSettingsPanel : MonoBehaviour
    {
        public SonaSweepController controller;
        [Tooltip("The headset camera; the bar opens in front of it and keeps facing it while you move it.")]
        public Transform head;
        [Tooltip("Unlit material for buttons and backing (SONA UI).")]
        public Material uiMaterial;
        public bool showOnStart = true;
        [Tooltip("How far in front of you the bar opens (m).")]
        public float openDistance = 0.75f;
        [Tooltip("Height of the bar relative to your eyes when it opens (m).")]
        public float openHeight = -0.28f;
        [Tooltip("For this many seconds after start the bar re-centres itself when the head jumps (headset tracking " +
                 "starting, or the desktop camera lifting to eye height), until it is first used.")]
        public float autoPlaceSeconds = 3f;
        [Range(0.5f, 1.5f)] public float barScale = 0.9f;

        public Color panelColor = new Color(0.05f, 0.07f, 0.11f);
        public Color buttonColor = new Color(0.18f, 0.22f, 0.30f);
        public Color handleColor = new Color(0.11f, 0.14f, 0.20f);
        public Color hoverColor = new Color(0.32f, 0.40f, 0.54f);
        public Color onColor = new Color(0.08f, 0.50f, 0.68f);
        public Color textColor = new Color(0.95f, 0.97f, 1f);
        public Color labelColor = new Color(0.62f, 0.72f, 0.84f);

        // Sizes in metres before barScale.
        const float BarH = 0.05f, BtnD = 0.012f, Gap = 0.008f, Pad = 0.008f;
        const float HandleW = 0.10f, TabW = 0.095f, IconW = 0.045f;
        const float PopBtnW = 0.12f, PopBtnH = 0.046f, PopLabelW = 0.075f, HintH = 0.03f, HintCharW = 0.0068f;
        const float CharSize = 0.0021f;
        const int FontSize = 64;
        const int TabCount = 4;

        enum BarState
        {
            Closed,
            Minimized,
            Expanded,
        }

        sealed class PopRow
        {
            public Transform Root;
            public TextMesh Label;
            public Func<string> LabelText;
            public Func<bool> Shown;
            public readonly List<SonaButton> Buttons = new List<SonaButton>();
        }

        sealed class Popup
        {
            public Transform Root;
            public Transform Backing;
            public TextMesh Hint;
            public Func<string> HintText;
            public readonly List<PopRow> Rows = new List<PopRow>();
        }

        readonly List<SonaButton> _buttons = new List<SonaButton>();
        readonly SonaButton[] _tabs = new SonaButton[TabCount];
        readonly Popup[] _popups = new Popup[TabCount];
        Transform _root;
        Transform _barBacking;
        SonaButton _handle, _minimize, _expand, _close;
        Font _font;
        BarState _state = BarState.Closed;
        int _openPopup = -1;
        bool _built;
        bool _userPlaced;
        float _startTime;
        float _labelTimer;
        Vector3 _placedFrom;
        bool _dragging;
        float _grabDistance;
        Vector3 _grabLocal;

        public bool Visible { get { return _state != BarState.Closed; } }
        public bool Minimized { get { return _state == BarState.Minimized; } }
        public bool Dragging { get { return _dragging; } }

        Transform Head
        {
            get { return head != null ? head : (Camera.main != null ? Camera.main.transform : null); }
        }

        static float ExpandedWidth
        {
            get { return 2f * Pad + HandleW + Gap + TabCount * (TabW + Gap) + IconW + Gap + IconW; }
        }

        static float MinimizedWidth
        {
            get { return 2f * Pad + HandleW + Gap + IconW + Gap + IconW; }
        }

        void Start()
        {
            _startTime = Time.time;
            EnsureBuilt();
            if (showOnStart) Open();
            else SetState(BarState.Closed);
        }

        void LateUpdate()
        {
            if (_state == BarState.Closed || _userPlaced || _dragging || Time.time - _startTime > autoPlaceSeconds) return;
            Transform h = Head;
            if (h != null && (h.position - _placedFrom).sqrMagnitude > 0.3f * 0.3f) PlaceInFront();
        }

        void EnsureBuilt()
        {
            if (_built) return;
            _built = true;
            if (controller == null) controller = FindAnyObjectByType<SonaSweepController>();
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (uiMaterial == null) uiMaterial = SonaMaterialUtil.FallbackUnlit();
            Build();
        }

        // ------------------------------------------------------------------ open / close / move

        /// <summary>Menu button: open the bar in front of you, bring it back if it is out of reach, or close it.</summary>
        public void Toggle()
        {
            _userPlaced = true;
            if (_state == BarState.Closed)
            {
                Open();
                return;
            }
            if (!InFrontOfHead())
            {
                PlaceInFront();
                return;
            }
            Close();
        }

        public void Open()
        {
            EnsureBuilt();
            SetState(BarState.Expanded);
            PlaceInFront();
        }

        public void Close()
        {
            EnsureBuilt();
            SetState(BarState.Closed);
        }

        public void Minimize()
        {
            SetState(BarState.Minimized);
        }

        public void Expand()
        {
            SetState(BarState.Expanded);
        }

        /// <summary>Kept for callers of the old panel: true opens in front of you, false closes.</summary>
        public void SetVisible(bool visible)
        {
            if (visible) Open();
            else Close();
        }

        void SetState(BarState state)
        {
            _state = state;
            _dragging = false;
            if (state != BarState.Expanded) _openPopup = -1;
            if (_root != null) _root.gameObject.SetActive(state != BarState.Closed);
            Layout();
        }

        bool InFrontOfHead()
        {
            Transform h = Head;
            if (h == null) return true;
            Vector3 toBar = transform.position - h.position;
            return toBar.magnitude < 2.5f && Vector3.Angle(h.forward, toBar) < 55f;
        }

        public void PlaceInFront()
        {
            Transform h = Head;
            if (h == null || _root == null) return;
            _placedFrom = h.position;
            Vector3 flat = Vector3.ProjectOnPlane(h.forward, Vector3.up);
            if (flat.sqrMagnitude < 1e-4f) flat = Vector3.ProjectOnPlane(h.up, Vector3.up);
            if (flat.sqrMagnitude < 1e-4f) flat = Vector3.forward;
            flat.Normalize();
            Vector3 pos = h.position + flat * openDistance + Vector3.up * openHeight;
            transform.SetPositionAndRotation(pos, FacingRotation(pos));
        }

        Quaternion FacingRotation(Vector3 pos)
        {
            Transform h = Head;
            Vector3 forward = h != null ? pos - h.position : transform.forward;
            if (forward.sqrMagnitude < 1e-6f) forward = transform.forward;
            return Quaternion.LookRotation(forward.normalized, Vector3.up);
        }

        void BeginDrag(SonaPointer pointer)
        {
            if (pointer == null || !pointer.Active || !pointer.HasHit) return;
            _dragging = true;
            _userPlaced = true;
            _grabDistance = Mathf.Clamp(Vector3.Distance(pointer.CurrentRay.origin, pointer.HitPoint), 0.25f, 4f);
            _grabLocal = transform.InverseTransformPoint(pointer.HitPoint);
        }

        void UpdateDrag(SonaPointer pointer)
        {
            Ray ray = pointer.CurrentRay;
            Vector3 grab = ray.origin + ray.direction * _grabDistance;
            Vector3 scaled = Vector3.Scale(_grabLocal, transform.lossyScale);
            // Face the head from where the bar is going, then put the grabbed spot back under the ray.
            Quaternion rot = FacingRotation(grab - transform.rotation * scaled);
            transform.SetPositionAndRotation(grab - rot * scaled, rot);
        }

        // ------------------------------------------------------------------ per frame

        /// <summary>
        /// Hover, clicks and dragging for this frame. Returns true when the bar used the pointer
        /// (it is over the bar or a popup, or the bar is being dragged), so the click goes no further.
        /// </summary>
        public bool Tick(SonaPointer pointer, bool selectDown, bool selectHeld, bool selectUp)
        {
            if (_state == BarState.Closed || _root == null)
            {
                _dragging = false;
                return false;
            }

            if (_dragging)
            {
                if (selectHeld && pointer != null && pointer.Active) UpdateDrag(pointer);
                if (!selectHeld || selectUp) _dragging = false;
                RefreshColors(null);
                return true;
            }

            bool onBar = pointer != null && pointer.HasHit && pointer.HitCollider != null &&
                         pointer.HitCollider.transform.IsChildOf(transform);
            SonaButton hovered = null;
            if (onBar && pointer.HitButton != null && pointer.HitButton.gameObject.activeInHierarchy &&
                _buttons.Contains(pointer.HitButton))
            {
                hovered = pointer.HitButton;
            }

            if (selectDown && onBar)
            {
                _userPlaced = true;
                bool onBarBacking = hovered == null && _barBacking != null && pointer.HitCollider.transform == _barBacking;
                if (hovered == _handle || onBarBacking)
                {
                    BeginDrag(pointer);
                }
                else if (hovered != null && hovered.OnClick != null)
                {
                    hovered.OnClick();
                    _labelTimer = 0f;   // show the result right away
                }
            }

            _labelTimer -= Time.unscaledDeltaTime;
            if (_labelTimer <= 0f)
            {
                _labelTimer = 0.15f;
                Layout();
            }
            RefreshColors(hovered);
            return onBar;
        }

        void RefreshColors(SonaButton hovered)
        {
            for (int i = 0; i < _buttons.Count; i++)
            {
                SonaButton b = _buttons[i];
                if (b.gameObject.activeInHierarchy) b.RefreshColor(b == hovered, buttonColor, hoverColor, onColor);
            }
        }

        // ------------------------------------------------------------------ construction

        void Build()
        {
            transform.localScale = Vector3.one * barScale;
            _root = new GameObject("Bar").transform;
            _root.SetParent(transform, false);

            _barBacking = MakeBacking("Bar Backing", _root);

            SonaSweepController c = controller;
            SonaRoomBuilder room = c != null ? c.room : null;

            _handle = MakeButton(_root, () => ":: SONA", null, () => _dragging);
            _handle.name = "Handle (drag)";
            _handle.NormalColor = handleColor;

            _tabs[0] = MakeButton(_root, () => c != null ? ModeName(c.Mode) : "Mode", () => TogglePopup(0), () => _openPopup == 0);
            _tabs[1] = MakeButton(_root, () => "Room", () => TogglePopup(1), () => _openPopup == 1);
            _tabs[2] = MakeButton(_root, () => "Sweep", () => TogglePopup(2), () => _openPopup == 2);
            _tabs[3] = MakeButton(_root, () => "Tools", () => TogglePopup(3), () => _openPopup == 3);
            _minimize = MakeButton(_root, () => "_", Minimize, () => false);
            _expand = MakeButton(_root, () => "+", Expand, () => false);
            _close = MakeButton(_root, () => "X", Close, () => false);
            _minimize.name = "Minimize";
            _expand.name = "Expand";
            _close.name = "Close";

            // Mode
            Popup mode = MakePopup("Mode", null);
            PopRow modes = AddRow(mode, null, null);
            AddButton(modes, () => "Explore", () => ChooseMode(SonaMode.Explore), () => c != null && c.Mode == SonaMode.Explore);
            AddButton(modes, () => "Challenge", () => ChooseMode(SonaMode.Challenge), () => c != null && c.Mode == SonaMode.Challenge);
            AddButton(modes, () => "Create room", () => ChooseMode(SonaMode.CreateRoom), () => c != null && c.Mode == SonaMode.CreateRoom);
            _popups[0] = mode;

            // Room
            Popup roomPop = MakePopup("Room", null);
            PopRow walls = AddRow(roomPop, () => "Walls", null);
            AddButton(walls, () => "No walls", () => Do(() => c.SetWalls(WallMode.NoWalls)), () => room != null && room.walls == WallMode.NoWalls);
            AddButton(walls, () => "Open (3)", () => Do(() => c.SetWalls(WallMode.Open)), () => room != null && room.walls == WallMode.Open);
            AddButton(walls, () => "Closed (4)", () => Do(() => c.SetWalls(WallMode.Closed)), () => room != null && room.walls == WallMode.Closed);
            PopRow size = AddRow(roomPop, () => "Size", null);
            for (int i = 0; i < SonaRoomBuilder.SizePresets.Length; i++)
            {
                Vector2 s = SonaRoomBuilder.SizePresets[i];
                AddButton(size, () => string.Format("{0:0} x {1:0} m", s.x, s.y), () => Do(() => c.SetRoomSize(s.x, s.y)),
                          () => room != null && Mathf.Approximately(room.width, s.x) && Mathf.Approximately(room.depth, s.y));
            }
            PopRow light = AddRow(roomPop, () => "Light", null);
            AddButton(light, () => "Dark", () => Do(() => c.SetDarkWorld(true)), () => c != null && c.darkWorld);
            AddButton(light, () => "Lit", () => Do(() => c.SetDarkWorld(false)), () => c != null && !c.darkWorld);
            _popups[1] = roomPop;

            // Sweep
            Popup sweep = MakePopup("Sweep", null);
            PopRow play = AddRow(sweep, () => "Play", null);
            AddButton(play, () => "Sweep (A)", () => Do(c.Trigger), () => false);
            AddButton(play, () => c != null && c.repeat ? "Repeat: ON" : "Repeat: off", () => Do(() => c.SetRepeat(!c.repeat)), () => c != null && c.repeat);
            AddButton(play, () => c != null ? string.Format("Speed {0:0.00}x", c.waveSpeed) : "-", () => Do(c.CycleWaveSpeed), () => false);
            PopRow sound = AddRow(sweep, () => "Sound", null);
            AddButton(sound, () => c != null ? ShortRenderer(c.renderMode) : "-", () => Do(c.CycleRenderMode), () => false);
            AddButton(sound, () => c != null && c.elevationToPitch ? "Pitch: elev" : "Pitch: fixed", () => Do(c.TogglePitch), () => c != null && c.elevationToPitch);
            AddButton(sound, () => c != null && c.sphericalWave ? "Wave: sphere" : "Wave: cylinder", () => Do(c.ToggleSphericalWave), () => c != null && c.sphericalWave);
            PopRow show = AddRow(sweep, () => "Show", null);
            AddButton(show, () => VisualsLabel(c), () => Do(c.ToggleSweepVisuals), () => c != null && c.SweepVisualsShown);
            _popups[2] = sweep;

            // Tools (depends on the mode)
            Popup tools = MakePopup("Tools", () => c != null ? c.PanelHint : "");
            PopRow explore = AddRow(tools, () => "Explore", () => c != null && c.Mode == SonaMode.Explore);
            AddButton(explore, () => "Reset objects", () => Do(c.ResetObjects), () => false);
            AddButton(explore, () => "Blackout", () => Do(() => c.SetBlackout(true)), () => false);

            PopRow trial = AddRow(tools, () => "Trial", () => c != null && c.Mode == SonaMode.Challenge);
            AddButton(trial, () => "New box (X)", () => Do(c.NewChallengeBox), () => false);
            AddButton(trial, () => room != null && room.ChallengeRevealed ? "Hide box (Y)" : "Reveal (Y)", () => Do(c.ToggleReveal),
                      () => room != null && room.ChallengeRevealed);
            AddButton(trial, () => "Blackout", () => Do(() => c.SetBlackout(true)), () => false);

            PopRow objects = AddRow(tools, () => "Objects", () => c != null && c.Mode == SonaMode.CreateRoom);
            AddButton(objects, () => "Add box", () => Do(() => c.AddObject(ObjectKind.Box)), () => false);
            AddButton(objects, () => "Add panel", () => Do(() => c.AddObject(ObjectKind.Panel)), () => false);
            AddButton(objects, () => "Rotate 15°", () => Do(c.RotateSelected), () => false);
            AddButton(objects, () => "Delete", () => Do(c.DeleteSelected), () => false);

            PopRow footprint = AddRow(tools, () => room != null ? string.Format("{0:0.#}x{1:0.#}", room.width, room.depth) : "",
                                      () => c != null && c.Mode == SonaMode.CreateRoom);
            AddButton(footprint, () => "Width -", () => Do(() => c.ChangeRoomSize(-0.5f, 0f, 0f)), () => false);
            AddButton(footprint, () => "Width +", () => Do(() => c.ChangeRoomSize(0.5f, 0f, 0f)), () => false);
            AddButton(footprint, () => "Depth -", () => Do(() => c.ChangeRoomSize(0f, -0.5f, 0f)), () => false);
            AddButton(footprint, () => "Depth +", () => Do(() => c.ChangeRoomSize(0f, 0.5f, 0f)), () => false);

            PopRow heightRow = AddRow(tools, () => room != null ? string.Format("h {0:0.#} m", room.height) : "",
                                      () => c != null && c.Mode == SonaMode.CreateRoom);
            AddButton(heightRow, () => "Height -", () => Do(() => c.ChangeRoomSize(0f, 0f, -0.5f)), () => false);
            AddButton(heightRow, () => "Height +", () => Do(() => c.ChangeRoomSize(0f, 0f, 0.5f)), () => false);
            AddButton(heightRow, () => "Clear all", () => Do(c.ClearObjects), () => false);
            AddButton(heightRow, () => "Reset", () => Do(c.ResetObjects), () => false);
            _popups[3] = tools;

            Layout();
        }

        void TogglePopup(int index)
        {
            _openPopup = _openPopup == index ? -1 : index;
            Layout();
        }

        void ChooseMode(SonaMode mode)
        {
            if (controller != null) controller.SetMode(mode);
            _openPopup = -1;
            Layout();
        }

        static void Do(Action action)
        {
            if (action != null) action();
        }

        static string ModeName(SonaMode m)
        {
            switch (m)
            {
                case SonaMode.Challenge: return "Challenge";
                case SonaMode.CreateRoom: return "Create";
                default: return "Explore";
            }
        }

        static string ShortRenderer(SonaRenderMode m)
        {
            switch (m)
            {
                case SonaRenderMode.StereoLiveHead: return "Stereo live";
                case SonaRenderMode.StereoFrozenPython: return "Stereo frozen";
                default: return "HRTF";
            }
        }

        static string VisualsLabel(SonaSweepController c)
        {
            if (c == null) return "-";
            if (!c.showSweepVisuals) return "Visuals: off";
            return c.Mode == SonaMode.Challenge ? "Visuals: hidden" : "Visuals: on";
        }

        Transform MakeBacking(string name, Transform parent)
        {
            GameObject back = GameObject.CreatePrimitive(PrimitiveType.Cube);
            back.name = name;
            back.transform.SetParent(parent, false);
            back.AddComponent<SonaPanelBackground>();
            var r = back.GetComponent<Renderer>();
            if (uiMaterial != null) r.sharedMaterial = uiMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            Tint(r, panelColor);
            return back.transform;
        }

        Popup MakePopup(string name, Func<string> hint)
        {
            var p = new Popup { HintText = hint };
            p.Root = new GameObject("Popup " + name).transform;
            p.Root.SetParent(_root, false);
            p.Backing = MakeBacking("Popup Backing", p.Root);
            if (hint != null) p.Hint = MakeText("Hint", p.Root, CharSize * 0.85f, TextAnchor.MiddleCenter, labelColor);
            p.Root.gameObject.SetActive(false);
            return p;
        }

        PopRow AddRow(Popup popup, Func<string> label, Func<bool> shown)
        {
            var row = new PopRow { LabelText = label, Shown = shown };
            row.Root = new GameObject("Row " + popup.Rows.Count).transform;
            row.Root.SetParent(popup.Root, false);
            if (label != null) row.Label = MakeText("Label", row.Root, CharSize, TextAnchor.MiddleLeft, labelColor);
            popup.Rows.Add(row);
            return row;
        }

        SonaButton AddButton(PopRow row, Func<string> label, Action onClick, Func<bool> isOn)
        {
            SonaButton b = MakeButton(row.Root, label, onClick, isOn);
            row.Buttons.Add(b);
            return b;
        }

        SonaButton MakeButton(Transform parent, Func<string> label, Action onClick, Func<bool> isOn)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Button";
            go.transform.SetParent(parent, false);
            var r = go.GetComponent<Renderer>();
            if (uiMaterial != null) r.sharedMaterial = uiMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            var b = go.AddComponent<SonaButton>();
            b.OnClick = onClick;
            b.Label = label;
            b.IsOn = isOn;
            b.Body = r;
            b.Text = MakeText("Text", parent, CharSize, TextAnchor.MiddleCenter, textColor);
            _buttons.Add(b);
            return b;
        }

        TextMesh MakeText(string name, Transform parent, float charSize, TextAnchor anchor, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMesh>();
            t.font = _font;
            t.fontSize = FontSize;
            t.characterSize = charSize;
            t.anchor = anchor;
            t.alignment = anchor == TextAnchor.MiddleLeft ? TextAlignment.Left : TextAlignment.Center;
            t.color = color;
            if (_font != null) go.GetComponent<MeshRenderer>().sharedMaterial = _font.material;
            return t;
        }

        static void Place(SonaButton b, float x, float y, float w, float h)
        {
            b.transform.localPosition = new Vector3(x, y, 0f);
            b.transform.localScale = new Vector3(w, h, BtnD);
            b.Text.transform.localPosition = new Vector3(x, y, -BtnD * 0.75f);
        }

        static void SetActive(SonaButton b, bool on)
        {
            if (b.gameObject.activeSelf != on) b.gameObject.SetActive(on);
            if (b.Text.gameObject.activeSelf != on) b.Text.gameObject.SetActive(on);
        }

        // ------------------------------------------------------------------ layout

        void Layout()
        {
            if (_root == null) return;
            bool expanded = _state == BarState.Expanded;
            float left = -ExpandedWidth / 2f;   // the left end stays put when the bar folds

            float x = left + Pad;
            Place(_handle, x + HandleW / 2f, 0f, HandleW, BarH);
            x += HandleW + Gap;
            for (int i = 0; i < TabCount; i++)
            {
                SetActive(_tabs[i], expanded);
                if (expanded)
                {
                    Place(_tabs[i], x + TabW / 2f, 0f, TabW, BarH);
                    x += TabW + Gap;
                }
            }
            SetActive(_minimize, expanded);
            SetActive(_expand, !expanded);
            if (expanded)
            {
                Place(_minimize, x + IconW / 2f, 0f, IconW, BarH);
            }
            else
            {
                Place(_expand, x + IconW / 2f, 0f, IconW, BarH);
            }
            x += IconW + Gap;
            Place(_close, x + IconW / 2f, 0f, IconW, BarH);

            float barWidth = expanded ? ExpandedWidth : MinimizedWidth;
            _barBacking.localPosition = new Vector3(left + barWidth / 2f, 0f, BtnD);
            _barBacking.localScale = new Vector3(barWidth, BarH + 2f * Pad, 0.006f);

            for (int i = 0; i < TabCount; i++)
            {
                Popup p = _popups[i];
                bool open = expanded && _openPopup == i;
                if (p.Root.gameObject.activeSelf != open) p.Root.gameObject.SetActive(open);
                if (open) LayoutPopup(p, _tabs[i].transform.localPosition.x - TabW / 2f, left, ExpandedWidth);
            }

            for (int i = 0; i < _buttons.Count; i++)
            {
                if (_buttons[i].gameObject.activeInHierarchy) _buttons[i].RefreshLabel();
            }
        }

        void LayoutPopup(Popup p, float tabLeft, float barLeft, float barWidth)
        {
            int rows = 0, cols = 0;
            bool labels = false;
            for (int r = 0; r < p.Rows.Count; r++)
            {
                PopRow row = p.Rows[r];
                bool shown = row.Shown == null || row.Shown();
                if (row.Root.gameObject.activeSelf != shown) row.Root.gameObject.SetActive(shown);
                if (!shown) continue;
                rows++;
                cols = Mathf.Max(cols, row.Buttons.Count);
                if (row.Label != null) labels = true;
            }
            string hint = p.HintText != null ? p.HintText() : null;
            bool hasHint = p.Hint != null && !string.IsNullOrEmpty(hint);

            float labelW = labels ? PopLabelW : 0f;
            float width = 2f * Pad + labelW + cols * PopBtnW + Mathf.Max(0, cols - 1) * Gap;
            if (hasHint) width = Mathf.Max(width, 2f * Pad + hint.Length * HintCharW);
            float height = 2f * Pad + rows * PopBtnH + Mathf.Max(0, rows - 1) * Gap + (hasHint ? HintH : 0f);

            // Drop-down style: start under the tab, but stay within the bar's width when possible.
            float left = tabLeft;
            if (width <= barWidth)
            {
                left = Mathf.Min(left, barLeft + barWidth - width);
                left = Mathf.Max(left, barLeft);
            }
            else
            {
                left = barLeft + barWidth / 2f - width / 2f;
            }
            float bottom = BarH / 2f + Pad + Gap;   // just above the bar

            float y = bottom + height - Pad - PopBtnH / 2f;
            for (int r = 0; r < p.Rows.Count; r++)
            {
                PopRow row = p.Rows[r];
                if (!row.Root.gameObject.activeSelf) continue;
                if (row.Label != null)
                {
                    string text = row.LabelText != null ? row.LabelText() : "";
                    if (row.Label.text != text) row.Label.text = text;
                    row.Label.transform.localPosition = new Vector3(left + Pad, y, -BtnD);
                }
                for (int b = 0; b < row.Buttons.Count; b++)
                {
                    float bx = left + Pad + labelW + b * (PopBtnW + Gap) + PopBtnW / 2f;
                    Place(row.Buttons[b], bx, y, PopBtnW, PopBtnH);
                }
                y -= PopBtnH + Gap;
            }

            if (p.Hint != null)
            {
                if (p.Hint.gameObject.activeSelf != hasHint) p.Hint.gameObject.SetActive(hasHint);
                if (hasHint)
                {
                    if (p.Hint.text != hint) p.Hint.text = hint;
                    p.Hint.transform.localPosition = new Vector3(left + width / 2f, bottom + Pad + HintH * 0.4f, -BtnD);
                }
            }

            p.Backing.localPosition = new Vector3(left + width / 2f, bottom + height / 2f, BtnD);
            p.Backing.localScale = new Vector3(width, height, 0.006f);
        }

        static void Tint(Renderer r, Color c)
        {
            if (r == null) return;
            var mpb = new MaterialPropertyBlock();
            mpb.SetColor("_BaseColor", c);
            mpb.SetColor("_Color", c);
            r.SetPropertyBlock(mpb);
        }
    }
}
