// SONA Quest port: a laser pointer from the right controller (or the mouse when no headset is active).
// It only finds what is being pointed at; the control bar and the controller decide what a
// trigger press does with it.
using Sona.Core;
using UnityEngine;

namespace Sona
{
    [AddComponentMenu("SONA/SONA Pointer")]
    public sealed class SonaPointer : MonoBehaviour
    {
        [Tooltip("Right controller anchor (OVRCameraRig > TrackingSpace > RightHandAnchor). The laser starts here.")]
        public Transform rayOrigin;
        [Tooltip("Camera for mouse rays when no headset is active. Defaults to Camera.main.")]
        public Camera viewCamera;
        [Tooltip("Vertex-colour material for the laser (SONA Wave FX).")]
        public Material laserMaterial;
        [Tooltip("Unlit material for the reticle (SONA UI).")]
        public Material reticleMaterial;
        public float maxDistance = 20f;
        public Color laserColor = new Color(0.55f, 0.9f, 1f, 1f);
        public Color laserActiveColor = new Color(1f, 0.82f, 0.3f, 1f);

        public Ray CurrentRay { get; private set; }
        public bool HasHit { get; private set; }
        public Vector3 HitPoint { get; private set; }
        public Collider HitCollider { get; private set; }
        public SonaButton HitButton { get; private set; }
        public bool HitPanel { get; private set; }
        public SonaSurface HitSurface { get; private set; }
        public bool UsingMouse { get; private set; }
        public bool Active { get; private set; }

        /// <summary>Set false to hide the laser and reticle (blackout).</summary>
        public bool Visible = true;

        readonly RaycastHit[] _hits = new RaycastHit[32];
        LineRenderer _line;
        Transform _reticle;
        MaterialPropertyBlock _mpb;

        void Awake()
        {
            var lineGo = new GameObject("SONA Laser");
            lineGo.transform.SetParent(transform, false);
            _line = lineGo.AddComponent<LineRenderer>();
            _line.useWorldSpace = true;
            _line.positionCount = 2;
            _line.numCapVertices = 2;
            _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _line.receiveShadows = false;
            // A soft glowing laser reads well in the dark world; fall back to the assigned material.
            Material lm = SonaGlow.Line;
            _line.widthMultiplier = lm != null ? 0.012f : 0.004f;
            if (lm == null) lm = laserMaterial != null ? laserMaterial : SonaMaterialUtil.FallbackEffect();
            if (lm != null) _line.sharedMaterial = lm;
            _line.enabled = false;

            GameObject reticle = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            reticle.name = "SONA Reticle";
            Destroy(reticle.GetComponent<Collider>());
            reticle.transform.SetParent(transform, false);
            reticle.transform.localScale = Vector3.one * 0.018f;
            Material rm = reticleMaterial != null ? reticleMaterial : SonaMaterialUtil.FallbackUnlit();
            if (rm != null) reticle.GetComponent<Renderer>().sharedMaterial = rm;
            _reticle = reticle.transform;
            _reticle.gameObject.SetActive(false);
        }

        /// <summary>Update the ray and what it hits. Call once per frame before using the results.</summary>
        public void Tick(SonaInput input, bool xrActive)
        {
            HasHit = false;
            HitButton = null;
            HitSurface = null;
            HitCollider = null;
            HitPanel = false;
            Active = false;

            if (xrActive && rayOrigin != null)
            {
                CurrentRay = new Ray(rayOrigin.position, rayOrigin.forward);
                UsingMouse = false;
                Active = true;
            }
            else if (input != null && input.HasMouse)
            {
                Camera cam = viewCamera != null ? viewCamera : Camera.main;
                if (cam != null)
                {
                    CurrentRay = cam.ScreenPointToRay(new Vector3(input.MousePosition.x, input.MousePosition.y, 0f));
                    UsingMouse = true;
                    Active = true;
                }
            }

            if (Active)
            {
                int n = Physics.RaycastNonAlloc(CurrentRay, _hits, maxDistance, ~0, QueryTriggerInteraction.Collide);
                float best = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    if (_hits[i].distance < best)
                    {
                        best = _hits[i].distance;
                        HitCollider = _hits[i].collider;
                        HitPoint = _hits[i].point;
                        HasHit = true;
                    }
                }
                if (HasHit)
                {
                    HitButton = HitCollider.GetComponent<SonaButton>();
                    HitPanel = HitButton != null || HitCollider.GetComponent<SonaPanelBackground>() != null;
                    HitSurface = HitCollider.GetComponent<SonaSurface>();
                }
            }
            UpdateVisuals(input != null && input.SelectHeld);
        }

        /// <summary>Where the ray meets a horizontal plane (the floor), if it does.</summary>
        public bool RayToPlane(float planeY, out Vector3 point)
        {
            var plane = new Plane(Vector3.up, new Vector3(0f, planeY, 0f));
            float enter;
            if (Active && plane.Raycast(CurrentRay, out enter) && enter <= maxDistance * 2f)
            {
                point = CurrentRay.GetPoint(enter);
                return true;
            }
            point = Vector3.zero;
            return false;
        }

        public bool HitIsFloor
        {
            get { return HitSurface != null && SonaMaterials.IsFloor(HitSurface.surfaceTag); }
        }

        void UpdateVisuals(bool pressed)
        {
            bool show = Visible && Active;
            Color c = pressed ? laserActiveColor : laserColor;
            if (_line != null)
            {
                _line.enabled = show && !UsingMouse;
                if (_line.enabled)
                {
                    Vector3 end = HasHit ? HitPoint : CurrentRay.GetPoint(maxDistance * 0.25f);
                    _line.SetPosition(0, CurrentRay.origin);
                    _line.SetPosition(1, end);
                    _line.startColor = c;
                    _line.endColor = new Color(c.r, c.g, c.b, 0.3f);
                }
            }
            if (_reticle != null)
            {
                bool showReticle = show && HasHit;
                if (_reticle.gameObject.activeSelf != showReticle) _reticle.gameObject.SetActive(showReticle);
                if (showReticle)
                {
                    _reticle.position = HitPoint;
                    if (_mpb == null) _mpb = new MaterialPropertyBlock();
                    _mpb.SetColor("_BaseColor", c);
                    _mpb.SetColor("_Color", c);
                    _reticle.GetComponent<Renderer>().SetPropertyBlock(_mpb);
                }
            }
        }
    }

    /// <summary>Marks the control bar and popup backings so clicks between buttons do not reach the floor.</summary>
    public sealed class SonaPanelBackground : MonoBehaviour { }

    /// <summary>Material fallbacks for objects created at runtime without assigned materials.</summary>
    public static class SonaMaterialUtil
    {
        static Material s_unlit;
        static Material s_effect;

        static Shader FindFirst(params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                Shader s = Shader.Find(names[i]);
                if (s != null) return s;
            }
            return null;
        }

        public static bool UsingScriptablePipeline
        {
            get { return UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null; }
        }

        public static Material FallbackUnlit()
        {
            if (s_unlit == null)
            {
                Shader s = UsingScriptablePipeline
                    ? FindFirst("Universal Render Pipeline/Unlit", "Unlit/Color", "Sprites/Default")
                    : FindFirst("Unlit/Color", "Sprites/Default");
                if (s != null) s_unlit = new Material(s);
            }
            return s_unlit;
        }

        public static Material FallbackEffect()
        {
            if (s_effect == null)
            {
                Shader s = UsingScriptablePipeline
                    ? FindFirst("Universal Render Pipeline/Particles/Unlit", "Sprites/Default")
                    : FindFirst("Particles/Standard Unlit", "Sprites/Default");
                if (s != null) s_effect = new Material(s);
            }
            return s_effect;
        }
    }
}
