// SONA Quest port: controller + keyboard/mouse bindings. Three controls; everything else is in the menu.
//
//   Right A / Space               sweep (restarts any sweep)
//   Right trigger / left mouse    select what you point at: menu buttons, a guess (Challenge), the next box
//   Left Menu / Tab               open or close the menu (also ends blackout)
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
        public bool Sweep, Menu;
        public bool SelectDown, SelectHeld, SelectUp;
        public bool HasMouse;
        public Vector2 MousePosition;

#if ENABLE_INPUT_SYSTEM
        InputAction _sweep, _menu, _select;

        static InputAction Button(string name, params string[] bindings)
        {
            var a = new InputAction(name, InputActionType.Button);
            for (int i = 0; i < bindings.Length; i++) a.AddBinding(bindings[i]);
            return a;
        }

        public SonaInput()
        {
            _sweep = Button("SONA Sweep", "<XRController>{RightHand}/{PrimaryButton}", "<Keyboard>/space");
            _select = Button("SONA Select", "<XRController>{RightHand}/{TriggerButton}", "<Mouse>/leftButton");
            _menu = Button("SONA Menu", "<XRController>{LeftHand}/{MenuButton}", "<Keyboard>/tab");
            foreach (InputAction a in All()) a.Enable();
        }

        InputAction[] All()
        {
            return new[] { _sweep, _menu, _select };
        }

        public void Poll()
        {
            Sweep = _sweep.WasPressedThisFrame();
            Menu = _menu.WasPressedThisFrame();
            SelectDown = _select.WasPressedThisFrame();
            SelectUp = _select.WasReleasedThisFrame();
            SelectHeld = _select.IsPressed();
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
        bool _pA, _pMenu, _pSelect;

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

        public void Poll()
        {
            Sweep = Pressed(XRNode.RightHand, CommonUsages.primaryButton, ref _pA) || Input.GetKeyDown(KeyCode.Space);
            Menu = Pressed(XRNode.LeftHand, CommonUsages.menuButton, ref _pMenu) || Input.GetKeyDown(KeyCode.Tab);
            bool select = Read(XRNode.RightHand, CommonUsages.triggerButton) || Input.GetMouseButton(0);
            SelectDown = select && !_pSelect;
            SelectUp = !select && _pSelect;
            SelectHeld = select;
            _pSelect = select;
            HasMouse = Input.mousePresent;
            MousePosition = Input.mousePosition;
        }

        public void Dispose() { }
#endif
    }
}
