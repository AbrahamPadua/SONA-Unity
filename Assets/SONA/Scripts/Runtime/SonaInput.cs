// SONA Quest port: controller + keyboard/mouse bindings.
//
//   Right trigger / left mouse    select: press panel buttons, drag objects (Create), guess (Challenge)
//   Left menu button / Tab        show or hide the settings panel
//   Right A / Space               trigger sweep (restart)
//   Right B / R                   repeat on/off
//   Right stick up/down / arrows  wave speed +/- 0.25x
//   Right stick click / M         renderer: Stereo live head -> Stereo frozen (Python) -> HRTF
//   Left X / N                    challenge: new hidden box
//   Left Y / V                    reveal / hide the box
//   Left stick left/right / [ ]   wall layout: no walls / open / closed
//   Left stick click / P          elevation->pitch on/off
//   Left trigger / B              blackout (hide all visuals for blind trials)
using System;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#else
using UnityEngine.XR;
#endif

namespace Sona
{
    public sealed class SonaInput : IDisposable
    {
        public bool Trigger, Repeat, Random, Reveal, Mode, Pitch, Blackout, Menu;
        public bool SelectDown, SelectHeld, SelectUp;
        public int SpeedStep, WallStep;
        public bool HasMouse;
        public Vector2 MousePosition;

        const float StickThreshold = 0.7f;
        bool _rightStickWasActive, _leftStickWasActive;

#if ENABLE_INPUT_SYSTEM
        InputAction _trigger, _repeat, _random, _reveal, _mode, _pitch, _blackout, _menu, _select;
        InputAction _speedUp, _speedDown, _wallNext, _wallPrev, _rightStick, _leftStick;

        static InputAction Button(string name, params string[] bindings)
        {
            var a = new InputAction(name, InputActionType.Button);
            for (int i = 0; i < bindings.Length; i++) a.AddBinding(bindings[i]);
            return a;
        }

        public SonaInput()
        {
            _trigger = Button("SONA Trigger", "<XRController>{RightHand}/{PrimaryButton}", "<Keyboard>/space");
            _repeat = Button("SONA Repeat", "<XRController>{RightHand}/{SecondaryButton}", "<Keyboard>/r");
            _mode = Button("SONA Mode", "<XRController>{RightHand}/{Primary2DAxisClick}", "<Keyboard>/m");
            _select = Button("SONA Select", "<XRController>{RightHand}/{TriggerButton}", "<Mouse>/leftButton");
            _random = Button("SONA Random Box", "<XRController>{LeftHand}/{PrimaryButton}", "<Keyboard>/n");
            _reveal = Button("SONA Reveal", "<XRController>{LeftHand}/{SecondaryButton}", "<Keyboard>/v");
            _pitch = Button("SONA Pitch", "<XRController>{LeftHand}/{Primary2DAxisClick}", "<Keyboard>/p");
            _blackout = Button("SONA Blackout", "<XRController>{LeftHand}/{TriggerButton}", "<Keyboard>/b");
            _menu = Button("SONA Menu", "<XRController>{LeftHand}/{MenuButton}", "<Keyboard>/tab");
            _speedUp = Button("SONA Speed Up", "<Keyboard>/upArrow");
            _speedDown = Button("SONA Speed Down", "<Keyboard>/downArrow");
            _wallNext = Button("SONA Next Walls", "<Keyboard>/rightBracket");
            _wallPrev = Button("SONA Previous Walls", "<Keyboard>/leftBracket");
            _rightStick = new InputAction("SONA Right Stick", InputActionType.Value, "<XRController>{RightHand}/{Primary2DAxis}");
            _leftStick = new InputAction("SONA Left Stick", InputActionType.Value, "<XRController>{LeftHand}/{Primary2DAxis}");
            foreach (InputAction a in All()) a.Enable();
        }

        InputAction[] All()
        {
            return new[] { _trigger, _repeat, _random, _reveal, _mode, _pitch, _blackout, _menu, _select, _speedUp,
                           _speedDown, _wallNext, _wallPrev, _rightStick, _leftStick };
        }

        public void Poll()
        {
            Trigger = _trigger.WasPressedThisFrame();
            Repeat = _repeat.WasPressedThisFrame();
            Random = _random.WasPressedThisFrame();
            Reveal = _reveal.WasPressedThisFrame();
            Mode = _mode.WasPressedThisFrame();
            Pitch = _pitch.WasPressedThisFrame();
            Blackout = _blackout.WasPressedThisFrame();
            Menu = _menu.WasPressedThisFrame();
            SelectDown = _select.WasPressedThisFrame();
            SelectUp = _select.WasReleasedThisFrame();
            SelectHeld = _select.IsPressed();
            SpeedStep = (_speedUp.WasPressedThisFrame() ? 1 : 0) - (_speedDown.WasPressedThisFrame() ? 1 : 0);
            WallStep = (_wallNext.WasPressedThisFrame() ? 1 : 0) - (_wallPrev.WasPressedThisFrame() ? 1 : 0);
            Vector2 r = _rightStick.ReadValue<Vector2>();
            Vector2 l = _leftStick.ReadValue<Vector2>();
            SpeedStep += StickEdge(r.y, ref _rightStickWasActive);
            WallStep += StickEdge(l.x, ref _leftStickWasActive);
            Mouse mouse = Mouse.current;
            HasMouse = mouse != null;
            MousePosition = mouse != null ? mouse.position.ReadValue() : Vector2.zero;
        }

        public void Dispose()
        {
            foreach (InputAction a in All())
            {
                a.Disable();
                a.Dispose();
            }
        }
#else
        bool _pA, _pB, _pRClick, _pX, _pY, _pLClick, _pLTrig, _pMenu, _pSelect;

        public SonaInput() { }

        static bool Read(XRNode node, InputFeatureUsage<bool> usage)
        {
            bool now = false;
            InputDevice d = InputDevices.GetDeviceAtXRNode(node);
            if (d.isValid) d.TryGetFeatureValue(usage, out now);
            return now;
        }

        static bool Pressed(XRNode node, InputFeatureUsage<bool> usage, ref bool previous)
        {
            bool now = Read(node, usage);
            bool edge = now && !previous;
            previous = now;
            return edge;
        }

        static Vector2 Axis(XRNode node)
        {
            Vector2 v = Vector2.zero;
            InputDevice d = InputDevices.GetDeviceAtXRNode(node);
            if (d.isValid) d.TryGetFeatureValue(CommonUsages.primary2DAxis, out v);
            return v;
        }

        public void Poll()
        {
            Trigger = Pressed(XRNode.RightHand, CommonUsages.primaryButton, ref _pA) || Input.GetKeyDown(KeyCode.Space);
            Repeat = Pressed(XRNode.RightHand, CommonUsages.secondaryButton, ref _pB) || Input.GetKeyDown(KeyCode.R);
            Mode = Pressed(XRNode.RightHand, CommonUsages.primary2DAxisClick, ref _pRClick) || Input.GetKeyDown(KeyCode.M);
            Random = Pressed(XRNode.LeftHand, CommonUsages.primaryButton, ref _pX) || Input.GetKeyDown(KeyCode.N);
            Reveal = Pressed(XRNode.LeftHand, CommonUsages.secondaryButton, ref _pY) || Input.GetKeyDown(KeyCode.V);
            Pitch = Pressed(XRNode.LeftHand, CommonUsages.primary2DAxisClick, ref _pLClick) || Input.GetKeyDown(KeyCode.P);
            Blackout = Pressed(XRNode.LeftHand, CommonUsages.triggerButton, ref _pLTrig) || Input.GetKeyDown(KeyCode.B);
            Menu = Pressed(XRNode.LeftHand, CommonUsages.menuButton, ref _pMenu) || Input.GetKeyDown(KeyCode.Tab);
            bool select = Read(XRNode.RightHand, CommonUsages.triggerButton) || Input.GetMouseButton(0);
            SelectDown = select && !_pSelect;
            SelectUp = !select && _pSelect;
            SelectHeld = select;
            _pSelect = select;
            SpeedStep = (Input.GetKeyDown(KeyCode.UpArrow) ? 1 : 0) - (Input.GetKeyDown(KeyCode.DownArrow) ? 1 : 0);
            WallStep = (Input.GetKeyDown(KeyCode.RightBracket) ? 1 : 0) - (Input.GetKeyDown(KeyCode.LeftBracket) ? 1 : 0);
            SpeedStep += StickEdge(Axis(XRNode.RightHand).y, ref _rightStickWasActive);
            WallStep += StickEdge(Axis(XRNode.LeftHand).x, ref _leftStickWasActive);
            HasMouse = Input.mousePresent;
            MousePosition = Input.mousePosition;
        }

        public void Dispose() { }
#endif

        static int StickEdge(float value, ref bool wasActive)
        {
            bool active = Mathf.Abs(value) > StickThreshold;
            int step = active && !wasActive ? (value > 0f ? 1 : -1) : 0;
            if (Mathf.Abs(value) < 0.3f) wasActive = false;   // hysteresis
            else if (active) wasActive = true;
            return step;
        }
    }
}
