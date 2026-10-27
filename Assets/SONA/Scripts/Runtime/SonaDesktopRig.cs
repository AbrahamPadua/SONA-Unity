// SONA Quest port: desktop fallback for testing in the editor without a headset. When no XR device is
// active it lifts the camera rig to standing eye height; hold the right mouse button to look around and
// use W/A/S/D to walk. It does nothing while a headset (or the Meta XR Simulator) is running.
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Sona
{
    [AddComponentMenu("SONA/SONA Desktop Rig")]
    public sealed class SonaDesktopRig : MonoBehaviour
    {
        public float eyeHeight = 1.6f;
        public float moveSpeed = 1.5f;
        public float lookDegreesPerPixel = 0.15f;
        [Tooltip("Seconds to wait for a headset before switching to desktop mode.")]
        public float startDelay = 1.0f;

        bool _active;
        Vector3 _originalPosition;
        Quaternion _originalRotation;
        float _yaw, _pitch;
        float _enabledAt;

        void OnEnable()
        {
            _enabledAt = Time.time;
        }

        void Update()
        {
            if (UnityEngine.XR.XRSettings.isDeviceActive)
            {
                if (_active)
                {
                    transform.position = _originalPosition;
                    transform.rotation = _originalRotation;
                    _active = false;
                }
                return;
            }
            if (!_active)
            {
                if (Time.time - _enabledAt < startDelay) return;
                _originalPosition = transform.position;
                _originalRotation = transform.rotation;
                transform.position = _originalPosition + Vector3.up * eyeHeight;
                _yaw = transform.eulerAngles.y;
                _pitch = 0f;
                _active = true;
            }

            Vector2 look = Vector2.zero;
            Vector2 move = Vector2.zero;
            bool looking = false;
#if ENABLE_INPUT_SYSTEM
            Mouse mouse = Mouse.current;
            Keyboard kb = Keyboard.current;
            if (mouse != null && mouse.rightButton.isPressed)
            {
                looking = true;
                look = mouse.delta.ReadValue();
            }
            if (kb != null)
            {
                move.x = (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f);
                move.y = (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f);
            }
#else
            if (Input.GetMouseButton(1))
            {
                looking = true;
                look = new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) * 10f;
            }
            move.x = (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f);
            move.y = (Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.S) ? 1f : 0f);
#endif
            if (looking)
            {
                _yaw += look.x * lookDegreesPerPixel;
                _pitch = Mathf.Clamp(_pitch - look.y * lookDegreesPerPixel, -80f, 80f);
            }
            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            if (move.sqrMagnitude > 0f)
            {
                Quaternion yawOnly = Quaternion.Euler(0f, _yaw, 0f);
                Vector3 delta = yawOnly * new Vector3(move.x, 0f, move.y).normalized * moveSpeed * Time.deltaTime;
                transform.position += delta;
            }
        }
    }
}
