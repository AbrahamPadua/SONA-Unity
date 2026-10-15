// SONA Quest port: a small floating status panel (replaces the Tk editor's status line/inspector).
using UnityEngine;

namespace Sona
{
    [AddComponentMenu("SONA/SONA HUD")]
    public sealed class SonaHud : MonoBehaviour
    {
        [Tooltip("The headset camera (CenterEyeAnchor). Defaults to Camera.main.")]
        public Transform head;
        [Tooltip("Where the text floats relative to your head (metres). Above eye level so it never covers the " +
                 "settings panel, which opens below eye level.")]
        public Vector3 viewOffset = new Vector3(0f, 0.27f, 1.5f);
        [Range(1f, 20f)] public float followSpeed = 4f;
        public float characterSize = 0.0085f;
        public int fontSize = 48;
        public Color color = new Color(0.92f, 0.96f, 1f, 1f);

        TextMesh _text;
        MeshRenderer _renderer;
        bool _visible = true;

        void Awake()
        {
            var go = new GameObject("SONA HUD Text");
            go.transform.SetParent(transform, false);
            _text = go.AddComponent<TextMesh>();
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _text.font = font;
            _text.fontSize = fontSize;
            _text.characterSize = characterSize;
            _text.anchor = TextAnchor.UpperCenter;
            _text.alignment = TextAlignment.Center;
            _text.color = color;
            _renderer = go.GetComponent<MeshRenderer>();
            if (font != null) _renderer.sharedMaterial = font.material;
        }

        public void SetText(string text)
        {
            if (_text != null && _text.text != text) _text.text = text;
        }

        public void SetVisible(bool visible)
        {
            _visible = visible;
            if (_renderer != null) _renderer.enabled = visible;
        }

        void LateUpdate()
        {
            if (!_visible) return;
            Transform h = head;
            if (h == null && Camera.main != null) h = Camera.main.transform;
            if (h == null) return;
            // Lazy follow: stays readable without being glued to the face.
            Vector3 flatForward = Vector3.ProjectOnPlane(h.forward, Vector3.up);
            if (flatForward.sqrMagnitude < 1e-4f) flatForward = transform.forward;
            Quaternion yaw = Quaternion.LookRotation(flatForward.normalized, Vector3.up);
            Vector3 target = h.position + yaw * viewOffset;
            float t = 1f - Mathf.Exp(-followSpeed * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, target, t);
            Vector3 look = transform.position - h.position;
            if (look.sqrMagnitude > 1e-6f) transform.rotation = Quaternion.LookRotation(look, Vector3.up);
        }
    }
}
