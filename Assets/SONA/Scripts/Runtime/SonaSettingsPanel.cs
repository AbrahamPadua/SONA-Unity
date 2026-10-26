// SONA Quest port: the menu. A floating bar that you can move and close:
//
//   [Move] [Mode] [Trial] [Sweep] [Room] [Help] [Close]
//
//   Mode    Explore | Challenge
//   Trial   next box, reveal, blackout (Next box starts Challenge)
//   Sweep   sweep now, repeat, speed, visuals; research: renderer, pitch, diagnostics
//   Room    walls, size, light, reset objects
//   Help    the controls and what each mode is for (open on first launch)
//
// Each tab opens a popup above the bar (press the tab again to close it). To move the bar, hold the
// trigger (or the left mouse button) on "Move" or on the bar's background and point somewhere else; it
// keeps facing you. The left Menu button (Tab) opens and closes it.
using System;
using System.Collections.Generic;
using Sona.Core;
using UnityEngine;

namespace Sona
{
    /// <summary>One 3-D push button of the menu.</summary>
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
        [Tooltip("Open the menu, with Help showing, when the app starts.")]
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
        const float HandleW = 0.08f, TabW = 0.095f, CloseW = 0.075f;
        const float PopBtnW = 0.12f, PopBtnH = 0.046f, PopLabelW = 0.095f;
        const float TextCharW = 0.008f, TextLineH = 0.015f;
        const float CharSize = 0.0021f;
        const int FontSize = 64;
        const int TabCount = 5;
        const int HelpTab = 4;

        sealed class PopRow
        {
            public Transform Root;
            public TextMesh Label;
            public Func<string> LabelText;
            public readonly List<SonaButton> Buttons = new List<SonaButton>();
        }

        sealed class Popup
        {
            public Transform Root;
            public Transform Backing;
            public TextMesh Text;
            public Func<string> TextFunc;
            public readonly List<PopRow> Rows = new List<PopRow>();
        }

        readonly List<SonaButton> _buttons = new List<SonaButton>();
        readonly SonaButton[] _tabs = new SonaButton[TabCount];
        readonly Popup[] _popups = new Popup[TabCount];
        Transform _root;
        Transform _barBacking;
        SonaButton _handle, _close;
        Font _font;
        bool _open;
        int _openPopup = -1;
        bool _built;
        bool _userPlaced;
        float _startTime;
        float _labelTimer;
        Vector3 _placedFrom;
        bool _dragging;
        float _grabDistance;
        Vector3 _grabLocal;

        public bool Visible { get { return _open; } }
        public bool Dragging { get { return _dragging; } }

        Transform Head
        {
            get { return head != null ? head : (Camera.main != null ? Camera.main.transform : null); }
        }

        static float BarWidth
        {
            get { return 2f * Pad + HandleW + Gap + TabCount * (TabW + Gap) + CloseW; }
        }

        void Start()
        {
            _startTime = Time.time;
            EnsureBuilt();
            if (showOnStart)
            {
                Open();
                ShowPopup(HelpTab);
            }
            else
            {
                Close();
            }
        }

        void LateUpdate()
        {
            if (!_open || _userPlaced || _dragging || Time.time - _startTime > autoPlaceSeconds) return;
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
            if (!_open)
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
            SetOpen(true);
            PlaceInFront();
        }

        public void Close()
        {
            EnsureBuilt();
            SetOpen(false);
        }

        void SetOpen(bool open)
        {
            _open = open;
            _dragging = false;
            if (!open) _openPopup = -1;
            if (_root != null) _root.gameObject.SetActive(open);
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
            if (!_open || _root == null)
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

            _handle = MakeButton(_root, () => "Move", null, () => _dragging);
            _handle.name = "Handle (drag)";
            _handle.NormalColor = handleColor;

            string[] tabNames = { "Mode", "Trial", "Sweep", "Room", "Help" };
            for (int i = 0; i < TabCount; i++)
            {
                int tab = i;
                _tabs[i] = MakeButton(_root, () => tabNames[tab], () => TogglePopup(tab), () => _openPopup == tab);
            }
            _close = MakeButton(_root, () => "Close", Close, () => false);
            _close.name = "Close";

            // Mode
            Popup mode = MakePopup("Mode", null);
            PopRow modes = AddRow(mode, null);
            AddButton(modes, () => "Explore", () => ChooseMode(SonaMode.Explore), () => c != null && c.Mode == SonaMode.Explore);
            AddButton(modes, () => "Challenge", () => ChooseMode(SonaMode.Challenge), () => c != null && c.Mode == SonaMode.Challenge);
            _popups[0] = mode;

            // Trial
            Popup trial = MakePopup("Trial", () => c != null && c.Mode != SonaMode.Challenge ? "Next box starts Challenge" : "");
            PopRow box = AddRow(trial, () => "Box");
            AddButton(box, () => "Next box", () => Do(c.NewChallengeBox), () => false);
            AddButton(box, () => room != null && room.ChallengeRevealed ? "Hide box" : "Reveal box", () => Do(c.ToggleReveal),
                      () => room != null && room.ChallengeRevealed);
            PopRow screen = AddRow(trial, () => "Screen");
            AddButton(screen, () => "Blackout", () => Do(() => c.SetBlackout(true)), () => false);
            _popups[1] = trial;

            // Sweep
            Popup sweep = MakePopup("Sweep", null);
            PopRow play = AddRow(sweep, () => "Play");
            AddButton(play, () => "Sweep now", () => Do(c.Trigger), () => false);
            AddButton(play, () => c != null && c.repeat ? "Repeat: on" : "Repeat: off", () => Do(() => c.SetRepeat(!c.repeat)), () => c != null && c.repeat);
            PopRow speed = AddRow(sweep, () => c != null ? string.Format("Speed {0:0.00}x", c.waveSpeed) : "Speed");
            AddButton(speed, () => "Slower", () => Do(() => c.SetWaveSpeed(c.waveSpeed - 0.25f)), () => false);
            AddButton(speed, () => "Faster", () => Do(() => c.SetWaveSpeed(c.waveSpeed + 0.25f)), () => false);
            PopRow show = AddRow(sweep, () => "Show");
            AddButton(show, () => VisualsLabel(c), () => Do(c.ToggleSweepVisuals), () => c != null && c.SweepVisualsShown);
            PopRow research = AddRow(sweep, () => "Research");
            AddButton(research, () => c != null && c.renderMode == SonaRenderMode.Hrtf ? "HRTF" : "Stereo", () => Do(c.CycleRenderMode), () => false);
            AddButton(research, () => c != null && c.elevationToPitch ? "Pitch: height" : "Pitch: fixed", () => Do(c.TogglePitch), () => c != null && c.elevationToPitch);
            AddButton(research, () => "Diagnostics", () => Do(() => c.showDiagnostics = !c.showDiagnostics), () => c != null && c.showDiagnostics);
            _popups[2] = sweep;

            // Room
            Popup roomPop = MakePopup("Room", null);
            PopRow walls = AddRow(roomPop, () => "Walls");
            AddButton(walls, () => "No walls", () => Do(() => c.SetWalls(WallMode.NoWalls)), () => room != null && room.walls == WallMode.NoWalls);
            AddButton(walls, () => "Open (3)", () => Do(() => c.SetWalls(WallMode.Open)), () => room != null && room.walls == WallMode.Open);
            AddButton(walls, () => "Closed (4)", () => Do(() => c.SetWalls(WallMode.Closed)), () => room != null && room.walls == WallMode.Closed);
            PopRow size = AddRow(roomPop, () => "Size");
            for (int i = 0; i < SonaRoomBuilder.SizePresets.Length; i++)
            {
                Vector2 s = SonaRoomBuilder.SizePresets[i];
                AddButton(size, () => string.Format("{0:0} x {1:0} m", s.x, s.y), () => Do(() => c.SetRoomSize(s.x, s.y)),
                          () => room != null && Mathf.Approximately(room.width, s.x) && Mathf.Approximately(room.depth, s.y));
            }
            PopRow light = AddRow(roomPop, () => "Light");
            AddButton(light, () => "Dark", () => Do(() => c.SetDarkWorld(true)), () => c != null && c.darkWorld);
            AddButton(light, () => "Lit", () => Do(() => c.SetDarkWorld(false)), () => c != null && !c.darkWorld);
            PopRow objects = AddRow(roomPop, () => "Objects");
            AddButton(objects, () => "Reset", () => Do(c.ResetObjects), () => false);
            _popups[3] = roomPop;

            // Help
            _popups[HelpTab] = MakePopup("Help", HelpText);

            Layout();
        }

        static string HelpText()
        {
            bool xr = UnityEngine.XR.XRSettings.isDeviceActive;
            string a = xr ? "A" : "Space", trigger = xr ? "Trigger" : "Click", menu = xr ? "Menu" : "Tab";
            string text =
                "CONTROLS\n" +
                a + ":  send a sweep. Each surface sounds when the wave reaches it,\n" +
                "    so later sounds are farther away.\n" +
                trigger + ":  press the button you point at. In Challenge, point at\n" +
                "    the floor where you think the box is to guess, then again\n" +
                "    for the next box.\n" +
                menu + ":  open or close this menu. It also ends blackout.\n" +
                "\n" +
                "MODES\n" +
                "Explore:  sweep and listen. Gold dots show what you heard.\n" +
                "Challenge:  a hidden box. Find it by ear, then guess.\n" +
                "    Close the menu before guessing.";
            if (!xr) text += "\n\nDesktop: hold the right mouse button to look, W A S D to walk.";
            return text;
        }

        void TogglePopup(int index)
        {
            ShowPopup(_openPopup == index ? -1 : index);
        }

        void ShowPopup(int index)
        {
            _openPopup = index;
            Layout();
        }

        void ChooseMode(SonaMode mode)
        {
            if (controller != null) controller.SetMode(mode);
            ShowPopup(-1);
        }

        static void Do(Action action)
        {
            if (action != null) action();
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

        Popup MakePopup(string name, Func<string> text)
        {
            var p = new Popup { TextFunc = text };
            p.Root = new GameObject("Popup " + name).transform;
            p.Root.SetParent(_root, false);
            p.Backing = MakeBacking("Popup Backing", p.Root);
            if (text != null) p.Text = MakeText("Text", p.Root, CharSize, TextAnchor.UpperLeft, labelColor);
            p.Root.gameObject.SetActive(false);
            return p;
        }

        PopRow AddRow(Popup popup, Func<string> label)
        {
            var row = new PopRow { LabelText = label };
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
            t.alignment = anchor == TextAnchor.MiddleCenter ? TextAlignment.Center : TextAlignment.Left;
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

        // ------------------------------------------------------------------ layout

        void Layout()
        {
            if (_root == null) return;
            float left = -BarWidth / 2f;

            float x = left + Pad;
            Place(_handle, x + HandleW / 2f, 0f, HandleW, BarH);
            x += HandleW + Gap;
            for (int i = 0; i < TabCount; i++)
            {
                Place(_tabs[i], x + TabW / 2f, 0f, TabW, BarH);
                x += TabW + Gap;
            }
            Place(_close, x + CloseW / 2f, 0f, CloseW, BarH);

            _barBacking.localPosition = new Vector3(0f, 0f, BtnD);
            _barBacking.localScale = new Vector3(BarWidth, BarH + 2f * Pad, 0.006f);

            for (int i = 0; i < TabCount; i++)
            {
                Popup p = _popups[i];
                bool open = _open && _openPopup == i;
                if (p.Root.gameObject.activeSelf != open) p.Root.gameObject.SetActive(open);
                if (open) LayoutPopup(p, _tabs[i].transform.localPosition.x - TabW / 2f, left, BarWidth);
            }

            for (int i = 0; i < _buttons.Count; i++)
            {
                if (_buttons[i].gameObject.activeInHierarchy) _buttons[i].RefreshLabel();
            }
        }

        void LayoutPopup(Popup p, float tabLeft, float barLeft, float barWidth)
        {
            int rows = p.Rows.Count, cols = 0;
            bool labels = false;
            for (int r = 0; r < rows; r++)
            {
                cols = Mathf.Max(cols, p.Rows[r].Buttons.Count);
                if (p.Rows[r].Label != null) labels = true;
            }
            string text = p.TextFunc != null ? p.TextFunc() : null;
            bool hasText = p.Text != null && !string.IsNullOrEmpty(text);
            int lines = 0, longest = 0;
            if (hasText)
            {
                string[] split = text.Split('\n');
                lines = split.Length;
                for (int i = 0; i < split.Length; i++) longest = Mathf.Max(longest, split[i].Length);
            }
            float textH = hasText ? lines * TextLineH + Pad : 0f;

            float labelW = labels ? PopLabelW : 0f;
            float width = 2f * Pad + labelW + cols * PopBtnW + Mathf.Max(0, cols - 1) * Gap;
            if (hasText) width = Mathf.Max(width, 2f * Pad + longest * TextCharW);
            float height = 2f * Pad + rows * PopBtnH + Mathf.Max(0, rows - 1) * Gap + textH;

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
            for (int r = 0; r < rows; r++)
            {
                PopRow row = p.Rows[r];
                if (row.Label != null)
                {
                    string label = row.LabelText != null ? row.LabelText() : "";
                    if (row.Label.text != label) row.Label.text = label;
                    row.Label.transform.localPosition = new Vector3(left + Pad, y, -BtnD);
                }
                for (int b = 0; b < row.Buttons.Count; b++)
                {
                    float bx = left + Pad + labelW + b * (PopBtnW + Gap) + PopBtnW / 2f;
                    Place(row.Buttons[b], bx, y, PopBtnW, PopBtnH);
                }
                y -= PopBtnH + Gap;
            }

            if (p.Text != null)
            {
                if (p.Text.gameObject.activeSelf != hasText) p.Text.gameObject.SetActive(hasText);
                if (hasText)
                {
                    if (p.Text.text != text) p.Text.text = text;
                    p.Text.transform.localPosition = new Vector3(left + Pad, bottom + Pad + textH - Pad / 2f, -BtnD);
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
