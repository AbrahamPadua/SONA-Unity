// SONA Quest port: builds and edits the test room (scene.py) as Unity geometry.
//
// This object's transform is the listener spawn point: the room is placed so that the Python
// listener position (front edge for open/no-wall rooms, 0.5 m inside for closed rooms) sits at
// this transform, facing +Z. Put it at the OVRCameraRig origin and you start where the Python
// listener stood.
//
// Objects survive wall and size changes (they are clamped inside the room), like editing a room
// rather than loading a new preset.
using System;
using System.Collections.Generic;
using Sona.Core;
using UnityEngine;

namespace Sona
{
    /// <summary>How the room geometry is drawn. Sound never depends on it.</summary>
    public enum SonaRoomView
    {
        /// <summary>Lit materials (sighted demos and the Scene view).</summary>
        Lit,
        /// <summary>Dim unlit outline colours, for editing in the dark (Create room).</summary>
        Ghost,
        /// <summary>Not drawn at all: the dark world. Colliders stay on for the laser.</summary>
        Hidden,
    }

    [AddComponentMenu("SONA/SONA Room Builder")]
    public sealed class SonaRoomBuilder : MonoBehaviour
    {
        /// <summary>Room footprints of the Python presets: Compact, Demo, Large.</summary>
        public static readonly Vector2[] SizePresets = { new Vector2(4f, 4f), new Vector2(5f, 6f), new Vector2(8f, 10f) };

        [Header("Room (scene.py)")]
        [Range(2f, 20f)] public float width = 5f;
        [Range(2f, 20f)] public float depth = 6f;
        [Range(1.5f, 6f)] public float height = 3f;
        [Tooltip("No walls = floor only. Open = left, right and back walls. Closed = all four walls.")]
        public WallMode walls = WallMode.NoWalls;

        [Header("Materials")]
        public Material floorMaterial;
        public Material wallMaterial;
        public Material objectMaterial;
        public Color highlightColor = new Color(0.35f, 0.85f, 1f);

        [Header("Dark world")]
        [Tooltip("Fallback for the ghost view and the revealed challenge box when a lit material is missing (SONA UI).")]
        public Material unlitMaterial;
        [Tooltip("Create room in the dark: the room is drawn dim, in these colours, so it can be edited.")]
        public Color ghostFloorColor = new Color(0.07f, 0.09f, 0.13f);
        public Color ghostWallColor = new Color(0.13f, 0.16f, 0.23f);
        public Color ghostObjectColor = new Color(0.42f, 0.23f, 0.08f);
        [Tooltip("Colour of the challenge box once it is revealed in the dark.")]
        public Color revealColor = new Color(1f, 0.55f, 0.16f);

        [Tooltip("Rebuild the room (with Box 1 and Panel 1) when play starts. Turn off to keep hand-edited geometry.")]
        public bool rebuildOnStart = true;

        const string GeneratedName = "Generated Room";

        Transform _generated;
        Transform _objectsRoot;
        readonly Dictionary<string, GameObject> _objects = new Dictionary<string, GameObject>();
        readonly List<Renderer> _renderers = new List<Renderer>();
        readonly List<Part> _parts = new List<Part>();
        List<ObjectSpec> _savedObjects;
        string _mysteryId;
        string _highlightId;
        bool _visible = true;
        SonaRoomView _view = SonaRoomView.Lit;
        MaterialPropertyBlock _mpb;

        enum Part
        {
            Floor,
            Wall,
            Object,
        }

        public RoomLayout Layout { get; private set; }
        public SonaRoomView View { get { return _view; } }
        public bool ChallengeActive { get; private set; }
        public bool ChallengeRevealed { get; private set; }

        /// <summary>Raised after structural changes (walls, size, objects added/removed, challenge box).</summary>
        public event Action RoomChanged;

        public float FloorY { get { return transform.position.y; } }

        public static string WallsLabel(WallMode mode)
        {
            switch (mode)
            {
                case WallMode.NoWalls: return "no walls";
                case WallMode.Open: return "open (3 walls)";
                default: return "closed (4 walls)";
            }
        }

        public string RoomLabel
        {
            get { return string.Format("{0:0.#} x {1:0.#} x {2:0.#} m, {3}", width, depth, height, WallsLabel(walls)); }
        }

        void Start()
        {
            if (rebuildOnStart || transform.Find(GeneratedName) == null)
            {
                Rebuild();
            }
            else
            {
                Layout = SonaRoomModel.MakeRoom(width, depth, height, walls, false);
                _generated = transform.Find(GeneratedName);
                _objectsRoot = _generated != null ? _generated.Find("Objects") : null;
                CollectRenderers();
                ApplyVisibility();
            }
        }

        // ------------------------------------------------------------------ room

        /// <summary>Fresh room from the current size/walls with the Python starter objects (Box 1, Panel 1).</summary>
        [ContextMenu("Rebuild Room")]
        public void Rebuild()
        {
            ClampDimensions();
            Layout = SonaRoomModel.MakeRoom(width, depth, height, walls, true);
            ChallengeActive = false;
            ChallengeRevealed = false;
            _savedObjects = null;
            _mysteryId = null;
            BuildAll();
        }

        public void SetWalls(WallMode mode)
        {
            walls = mode;
            ApplyRoomChange();
        }

        public void SetSize(float newWidth, float newDepth)
        {
            width = newWidth;
            depth = newDepth;
            ApplyRoomChange();
        }

        public void SetHeight(float newHeight)
        {
            height = newHeight;
            ApplyRoomChange();
        }

        void ClampDimensions()
        {
            width = Mathf.Clamp(Mathf.Round(width * 2f) / 2f, (float)SonaRoomModel.MinCustomWidth, (float)SonaRoomModel.MaxCustomWidth);
            depth = Mathf.Clamp(Mathf.Round(depth * 2f) / 2f, (float)SonaRoomModel.MinCustomDepth, (float)SonaRoomModel.MaxCustomDepth);
            height = Mathf.Clamp(Mathf.Round(height * 2f) / 2f, (float)SonaRoomModel.MinCustomHeight, (float)SonaRoomModel.MaxCustomHeight);
        }

        void ApplyRoomChange()
        {
            ClampDimensions();
            if (Layout == null)
            {
                Rebuild();
                return;
            }
            SonaRoomModel.ChangeRoom(Layout, width, depth, height, walls);
            if (_savedObjects != null)
            {
                // Keep the objects saved during a challenge consistent with the new room as well.
                var tmp = new RoomLayout { Room = Layout.Room, Objects = _savedObjects };
                SonaRoomModel.ChangeRoom(tmp, width, depth, height, walls);
            }
            if (_mysteryId != null && Layout.Find(_mysteryId) == null) _mysteryId = null;
            BuildAll();
        }

        void BuildAll()
        {
            ClearGenerated();
            _generated = new GameObject(GeneratedName).transform;
            _generated.SetParent(transform, false);
            _objectsRoot = new GameObject("Objects").transform;
            _objectsRoot.SetParent(_generated, false);
            List<PrimitivePlacement> placements = SonaRoomModel.Placements(Layout);
            for (int i = 0; i < placements.Count; i++)
            {
                if (!placements[i].IsObject) Spawn(placements[i]);
            }
            for (int i = 0; i < Layout.Objects.Count; i++) SpawnObject(Layout.Objects[i]);
            CollectRenderers();
            ApplyVisibility();
            NotifyChanged();
        }

        public void NotifyChanged()
        {
            if (RoomChanged != null) RoomChanged();
        }

        // ------------------------------------------------------------------ objects (create-room mode)

        public GameObject GetObject(string id)
        {
            GameObject go;
            return id != null && _objects.TryGetValue(id, out go) ? go : null;
        }

        /// <summary>The object id behind a collider, or null for walls, floor and anything else.</summary>
        public string ObjectIdOf(Collider c)
        {
            if (c == null) return null;
            foreach (KeyValuePair<string, GameObject> kv in _objects)
            {
                if (kv.Value != null && (c.gameObject == kv.Value || c.transform.IsChildOf(kv.Value.transform))) return kv.Key;
            }
            return null;
        }

        /// <summary>World point to scene.py room coordinates (x right, y forward).</summary>
        public void WorldToRoom(Vector3 world, out double x, out double y)
        {
            Vector3 local = transform.InverseTransformPoint(world);
            x = local.x + Layout.ListenerX;
            y = local.z + Layout.ListenerY;
        }

        public ObjectSpec AddObject(ObjectKind kind, Vector3 worldPoint, out string error)
        {
            error = null;
            if (Layout == null) Rebuild();
            double x, y;
            WorldToRoom(worldPoint, out x, out y);
            ObjectSpec o;
            try
            {
                o = SonaRoomModel.AddObject(Layout, kind, x, y);
            }
            catch (ArgumentException e)
            {
                error = e.Message;
                return null;
            }
            SpawnObject(o);
            CollectRenderers();
            ApplyVisibility();
            NotifyChanged();
            return o;
        }

        /// <summary>Drag an object to a floor point (clamped inside the room). Does not raise RoomChanged.</summary>
        public void MoveObject(string id, Vector3 worldPoint)
        {
            ObjectSpec o = Layout != null ? Layout.Find(id) : null;
            GameObject go = GetObject(id);
            if (o == null || go == null) return;
            double x, y;
            WorldToRoom(worldPoint, out x, out y);
            o.X = x;
            o.Y = y;
            try { SonaRoomModel.ClampObject(Layout.Room, o); } catch (ArgumentException) { }
            ApplyTransform(go, o);
        }

        public void RotateObject(string id, float degrees)
        {
            ObjectSpec o = Layout != null ? Layout.Find(id) : null;
            GameObject go = GetObject(id);
            if (o == null || go == null) return;
            o.YawDegrees = SonaMath.FloorMod(o.YawDegrees + degrees, 360.0);
            try { SonaRoomModel.ClampObject(Layout.Room, o); } catch (ArgumentException) { }
            ApplyTransform(go, o);
            NotifyChanged();
        }

        public bool DeleteObject(string id)
        {
            ObjectSpec o = Layout != null ? Layout.Find(id) : null;
            if (o == null) return false;
            Layout.Objects.Remove(o);
            DestroyObject(id);
            CollectRenderers();
            NotifyChanged();
            return true;
        }

        public void ClearObjects()
        {
            if (Layout == null) return;
            Layout.Objects.Clear();
            foreach (string id in new List<string>(_objects.Keys)) DestroyObject(id);
            CollectRenderers();
            NotifyChanged();
        }

        /// <summary>Put back the Python starter objects (Box 1 and Panel 1) for the current room.</summary>
        public void ResetObjects()
        {
            if (ChallengeActive) EndChallenge();
            RoomLayout fresh = SonaRoomModel.MakeRoom(width, depth, height, walls, true);
            Layout.Objects.Clear();
            Layout.Objects.AddRange(fresh.Objects);
            Layout.NextBox = fresh.NextBox;
            Layout.NextPanel = fresh.NextPanel;
            BuildAll();
        }

        public void SetHighlighted(string id)
        {
            if (_highlightId == id) return;
            _highlightId = id;
            ApplyVisibility();
        }

        // ------------------------------------------------------------------ challenge (Python "Random Box")

        /// <summary>
        /// Replace the objects with one hidden 1 x 1 x 1 m box at least 1.5 m (planar) from
        /// <paramref name="listenerWorld"/>. The previous objects come back when the challenge ends.
        /// </summary>
        public bool NewChallengeBox(Vector3 listenerWorld, System.Random rng, out string error)
        {
            error = null;
            if (Layout == null) Rebuild();
            double px, py;
            WorldToRoom(listenerWorld, out px, out py);
            ObjectSpec box;
            try
            {
                box = SonaRoomModel.MakeRandomBox(Layout.Room, px, py, rng);
            }
            catch (ArgumentException e)
            {
                error = e.Message;
                return false;
            }
            if (!ChallengeActive) _savedObjects = CloneObjects(Layout.Objects);
            box.Id = "mystery-box";
            Layout.Objects.Clear();
            Layout.Objects.Add(box);
            _mysteryId = box.Id;
            ChallengeActive = true;
            ChallengeRevealed = false;
            BuildAll();
            return true;
        }

        public void EndChallenge()
        {
            if (!ChallengeActive) return;
            ChallengeActive = false;
            ChallengeRevealed = false;
            _mysteryId = null;
            Layout.Objects.Clear();
            if (_savedObjects != null) Layout.Objects.AddRange(_savedObjects);
            _savedObjects = null;
            BuildAll();
        }

        public void SetChallengeRevealed(bool revealed)
        {
            ChallengeRevealed = revealed;
            ApplyVisibility();
        }

        /// <summary>Mystery box centre in world space (for scoring guesses), or null.</summary>
        public Vector3? MysteryBoxPosition
        {
            get
            {
                GameObject go = ChallengeActive ? GetObject(_mysteryId) : null;
                return go != null ? go.transform.position : (Vector3?)null;
            }
        }

        // ------------------------------------------------------------------ visibility

        /// <summary>Show or hide all room geometry (sound is unaffected). Used for blackout.</summary>
        public void SetVisible(bool visible)
        {
            _visible = visible;
            ApplyVisibility();
        }

        /// <summary>Lit, ghost (dim outline) or hidden. A revealed challenge box always shows.</summary>
        public void SetView(SonaRoomView view)
        {
            _view = view;
            ApplyVisibility();
        }

        void ApplyVisibility()
        {
            GameObject mystery = GetObject(_mysteryId);
            GameObject highlighted = GetObject(_highlightId);
            Material unlit = unlitMaterial != null ? unlitMaterial : SonaMaterialUtil.FallbackUnlit();
            for (int i = 0; i < _renderers.Count; i++)
            {
                Renderer r = _renderers[i];
                if (r == null) continue;
                Part part = i < _parts.Count ? _parts[i] : Part.Wall;
                bool isMystery = ChallengeActive && mystery != null && r.gameObject == mystery;
                bool show;
                Material mat = null;
                Color? tint = null;
                if (!_visible)
                {
                    show = false;
                }
                else if (isMystery)
                {
                    // Lit and shaded so it reads as a box, in a bright colour so it stands out in the dark.
                    show = ChallengeRevealed;
                    mat = OrFallback(LitMaterial(part), unlit);
                    if (_view != SonaRoomView.Lit) tint = revealColor;
                }
                else if (_view == SonaRoomView.Lit)
                {
                    show = true;
                    mat = LitMaterial(part);
                    if (highlighted != null && r.gameObject == highlighted) tint = highlightColor;
                }
                else if (_view == SonaRoomView.Ghost)
                {
                    // The lit material with a dark colour: dim, but shaded, so boxes still read as 3-D.
                    show = true;
                    mat = OrFallback(LitMaterial(part), unlit);
                    tint = highlighted != null && r.gameObject == highlighted ? highlightColor : GhostColor(part);
                }
                else
                {
                    show = false;
                }

                r.enabled = show;
                if (!show) continue;
                if (mat != null && r.sharedMaterial != mat) r.sharedMaterial = mat;
                if (tint.HasValue)
                {
                    if (_mpb == null) _mpb = new MaterialPropertyBlock();
                    _mpb.Clear();
                    _mpb.SetColor("_BaseColor", tint.Value);
                    _mpb.SetColor("_Color", tint.Value);
                    r.SetPropertyBlock(_mpb);
                }
                else
                {
                    r.SetPropertyBlock(null);
                }
            }
            if (mystery != null)
            {
                // A hidden box must not stop the laser, or the laser would give its position away.
                var col = mystery.GetComponent<Collider>();
                if (col != null) col.enabled = ChallengeRevealed;
            }
        }

        Material LitMaterial(Part part)
        {
            switch (part)
            {
                case Part.Floor: return floorMaterial;
                case Part.Object: return objectMaterial;
                default: return wallMaterial;
            }
        }

        // Unity objects: compare with == null (not ??) so unassigned or destroyed materials count as missing.
        static Material OrFallback(Material preferred, Material fallback)
        {
            return preferred != null ? preferred : fallback;
        }

        Color GhostColor(Part part)
        {
            switch (part)
            {
                case Part.Floor: return ghostFloorColor;
                case Part.Object: return ghostObjectColor;
                default: return ghostWallColor;
            }
        }

        void CollectRenderers()
        {
            _renderers.Clear();
            _parts.Clear();
            if (_generated == null) return;
            _generated.GetComponentsInChildren(true, _renderers);
            for (int i = 0; i < _renderers.Count; i++)
            {
                Renderer r = _renderers[i];
                Part part;
                if (_objectsRoot != null && r.transform.IsChildOf(_objectsRoot))
                {
                    part = Part.Object;
                }
                else
                {
                    var s = r.GetComponent<SonaSurface>();
                    part = s != null && SonaMaterials.IsFloor(s.surfaceTag) ? Part.Floor : Part.Wall;
                }
                _parts.Add(part);
            }
        }

        // ------------------------------------------------------------------ helpers

        static List<ObjectSpec> CloneObjects(List<ObjectSpec> source)
        {
            var list = new List<ObjectSpec>(source.Count);
            for (int i = 0; i < source.Count; i++) list.Add(source[i].Clone());
            return list;
        }

        void ClearGenerated()
        {
            Transform existing = transform.Find(GeneratedName);
            while (existing != null)
            {
                existing.gameObject.SetActive(false);   // unregisters its SonaSurfaces immediately
                existing.SetParent(null, false);
                SonaUnity.SafeDestroy(existing.gameObject);
                existing = transform.Find(GeneratedName);
            }
            _generated = null;
            _objectsRoot = null;
            _objects.Clear();
            _renderers.Clear();
            _parts.Clear();
            _highlightId = null;
        }

        void DestroyObject(string id)
        {
            GameObject go = GetObject(id);
            _objects.Remove(id);
            if (_highlightId == id) _highlightId = null;
            if (go != null)
            {
                go.SetActive(false);
                SonaUnity.SafeDestroy(go);
            }
        }

        GameObject SpawnObject(ObjectSpec o)
        {
            GameObject go = Spawn(SonaRoomModel.ObjectPlacement(Layout, o));
            _objects[o.Id] = go;
            return go;
        }

        void ApplyTransform(GameObject go, ObjectSpec o)
        {
            PrimitivePlacement p = SonaRoomModel.ObjectPlacement(Layout, o);
            go.transform.localPosition = p.LocalPosition.ToVector3();
            go.transform.localRotation = Quaternion.Euler((float)p.EulerDegrees.X, (float)p.EulerDegrees.Y, (float)p.EulerDegrees.Z);
            go.transform.localScale = p.Scale.ToVector3();
        }

        GameObject Spawn(PrimitivePlacement p)
        {
            bool isQuad = p.Shape == PrimitiveShape.Quad;
            GameObject go = GameObject.CreatePrimitive(isQuad ? PrimitiveType.Quad : PrimitiveType.Cube);
            go.name = p.Name;
            go.transform.SetParent(p.IsObject ? _objectsRoot : _generated, false);
            go.transform.localPosition = p.LocalPosition.ToVector3();
            go.transform.localRotation = Quaternion.Euler((float)p.EulerDegrees.X, (float)p.EulerDegrees.Y, (float)p.EulerDegrees.Z);
            go.transform.localScale = p.Scale.ToVector3();
            Material mat = p.IsObject ? objectMaterial : (SonaMaterials.IsFloor(p.Tag) ? floorMaterial : wallMaterial);
            if (mat != null) go.GetComponent<Renderer>().sharedMaterial = mat;
            var s = go.AddComponent<SonaSurface>();
            s.shape = isQuad ? SonaSurfaceShape.Quad : SonaSurfaceShape.Box;
            s.surfaceTag = p.Tag ?? "";
            s.sourceId = p.SourceId;
            return go;
        }
    }
}
