// SONA Quest port: one-click setup and a Python-parity export.
//
//   SONA > Set Up URP for Quest             assigns a Quest-tuned URP asset (fixes pink materials) and
//                                           points the SONA materials at SONA/Simple Lit
//   SONA > Create Quest Test Scene          builds Assets/SONA/Scenes/SonaTestRoom.unity
//   SONA > Export Python-Parity sweep.wav   renders the Open Demo room exactly like render.py
using System.Collections.Generic;
using System.IO;
using Sona.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Sona.EditorTools
{
    public static class SonaSetupMenu
    {
        const string Root = "Assets/SONA";
        const string SceneDir = Root + "/Scenes";
        const string MaterialDir = Root + "/Materials";
        const string SettingsDir = Root + "/Settings";
        const string ScenePath = SceneDir + "/SonaTestRoom.unity";
        const string UrpAssetPath = SettingsDir + "/SONA_URP_Quest.asset";
        const string UrpRendererPath = SettingsDir + "/SONA_URP_Quest_Renderer.asset";
        const string RigPrefabPath = "Packages/com.meta.xr.sdk.core/Prefabs/OVRCameraRig.prefab";
        const string PostProcessDataPath = "Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset";

        // ------------------------------------------------------------------ render pipeline

        [MenuItem("SONA/Set Up URP for Quest (fixes pink materials)", false, 0)]
        public static void SetUpUrpMenu()
        {
            UniversalRenderPipelineAsset asset = EnsureUrp();
            if (asset == null) return;
            RefreshMaterialShaders();
            Debug.Log("[SONA] Render pipeline: " + AssetDatabase.GetAssetPath(asset) +
                      " (MSAA 4x, HDR off, depth/opaque textures off). SONA materials now use " + LitShader().name + ".");
        }

        /// <summary>
        /// The project has the URP package but no pipeline asset assigned, so Unity falls back to the
        /// (deprecated) Built-in pipeline and URP materials draw pink. Create and assign one tuned for Quest.
        /// </summary>
        public static UniversalRenderPipelineAsset EnsureUrp()
        {
            var asset = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            if (asset == null) asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(UrpAssetPath);
            if (asset == null)
            {
                EnsureFolder(SettingsDir);
                var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(UrpRendererPath);
                if (data == null)
                {
                    data = ScriptableObject.CreateInstance<UniversalRendererData>();
                    data.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>(PostProcessDataPath);
                    data.intermediateTextureMode = IntermediateTextureMode.Auto;
                    AssetDatabase.CreateAsset(data, UrpRendererPath);
                }
                asset = UniversalRenderPipelineAsset.Create(data);
                AssetDatabase.CreateAsset(asset, UrpAssetPath);
            }
            // Quest-friendly defaults (what Meta's Project Setup Tool recommends).
            asset.msaaSampleCount = 4;
            asset.supportsHDR = false;
            asset.supportsCameraDepthTexture = false;
            asset.supportsCameraOpaqueTexture = false;
            asset.renderScale = 1f;
            EditorUtility.SetDirty(asset);

            GraphicsSettings.defaultRenderPipeline = asset;
            int current = QualitySettings.GetQualityLevel();
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = asset;
            }
            QualitySettings.SetQualityLevel(current, false);
            AssetDatabase.SaveAssets();
            return asset;
        }

        static bool UsingUrp
        {
            get { return GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset; }
        }

        /// <summary>
        /// SONA/Simple Lit: URP's Simple Lit compiled at full precision. URP Lit and Simple Lit use half
        /// precision when the Android target is active, which draws white dots in the editor on Intel Iris Xe
        /// with Direct3D 11 (see the shader). Falls back to the URP shaders if it is missing.
        /// </summary>
        static Shader LitShader()
        {
            if (UsingUrp)
            {
                Shader s = Shader.Find("SONA/Simple Lit");
                if (s == null) s = Shader.Find("Universal Render Pipeline/Simple Lit");
                if (s == null) s = Shader.Find("Universal Render Pipeline/Lit");
                if (s != null) return s;
            }
            return Shader.Find("Standard");
        }

        /// <summary>Matte lit material: no specular, receives shadows.</summary>
        static Material MakeLitMaterial(string name, Color color)
        {
            Material mat = MakeMaterial(name, LitShader(), color);
            // Simple Lit's _SpecularHighlights is an enum: 0 = specular on, 1 = no specular.
            if (mat.HasProperty("_SpecularHighlights")) mat.SetFloat("_SpecularHighlights", 1f);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.2f);
            if (mat.HasProperty("_ReceiveShadows")) mat.SetFloat("_ReceiveShadows", 1f);
            mat.DisableKeyword("_SPECULAR_COLOR");
            mat.DisableKeyword("_SPECGLOSSMAP");
            mat.DisableKeyword("_RECEIVE_SHADOWS_OFF");
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            return mat;
        }

        static Shader UnlitShader()
        {
            Shader s = UsingUrp ? Shader.Find("Universal Render Pipeline/Unlit") : null;
            return s != null ? s : Shader.Find("Unlit/Color");
        }

        static Shader EffectShader()
        {
            Shader s = UsingUrp ? Shader.Find("Universal Render Pipeline/Particles/Unlit") : Shader.Find("Particles/Standard Unlit");
            return s != null ? s : Shader.Find("Sprites/Default");
        }

        /// <summary>Point existing SONA materials at shaders of the active pipeline.</summary>
        static void RefreshMaterialShaders()
        {
            MakeLitMaterial("SONA Floor", new Color(0.16f, 0.18f, 0.22f));
            MakeLitMaterial("SONA Wall", new Color(0.70f, 0.77f, 0.86f));
            MakeLitMaterial("SONA Object", new Color(0.95f, 0.56f, 0.22f));
            MakeMaterial("SONA Wave FX", EffectShader(), Color.white);
            MakeMaterial("SONA UI", UnlitShader(), Color.white);
        }

        // ------------------------------------------------------------------ scene

        [MenuItem("SONA/Create Quest Test Scene", false, 1)]
        public static void CreateTestScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            if (!UsingUrp) EnsureUrp();
            EnsureFolder(SceneDir);
            EnsureFolder(MaterialDir);

            Material floorMat = MakeLitMaterial("SONA Floor", new Color(0.16f, 0.18f, 0.22f));
            Material wallMat = MakeLitMaterial("SONA Wall", new Color(0.70f, 0.77f, 0.86f));
            Material objectMat = MakeLitMaterial("SONA Object", new Color(0.95f, 0.56f, 0.22f));
            Material fxMat = MakeMaterial("SONA Wave FX", EffectShader(), Color.white);
            Material uiMat = MakeMaterial("SONA UI", UnlitShader(), Color.white);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            Transform rightHand;
            Transform head = CreateRig(out rightHand);

            var sona = new GameObject("SONA");
            var roomGo = new GameObject("SONA Room");
            roomGo.transform.SetParent(sona.transform, false);
            var room = roomGo.AddComponent<SonaRoomBuilder>();
            room.walls = WallMode.NoWalls;   // default: floor only
            room.width = 5f;
            room.depth = 6f;
            room.height = 3f;
            room.floorMaterial = floorMat;
            room.wallMaterial = wallMat;
            room.objectMaterial = objectMat;
            room.unlitMaterial = uiMat;
            room.Rebuild();   // lit in the Scene view; in Play the world is dark (SONA Sweep Controller > View)

            var audioGo = new GameObject("SONA Audio");
            audioGo.transform.SetParent(sona.transform, false);
            audioGo.AddComponent<AudioSource>();
            var stereo = audioGo.AddComponent<SonaStereoRenderer>();
            var hrtfGo = new GameObject("SONA HRTF Voices");
            hrtfGo.transform.SetParent(sona.transform, false);
            var hrtf = hrtfGo.AddComponent<SonaHrtfRenderer>();

            var vizGo = new GameObject("SONA Wave Visualizer");
            vizGo.transform.SetParent(sona.transform, false);
            var viz = vizGo.AddComponent<SonaWaveVisualizer>();
            viz.effectMaterial = fxMat;

            var hudGo = new GameObject("SONA HUD");
            hudGo.transform.SetParent(sona.transform, false);
            hudGo.transform.position = new Vector3(0f, 1.3f, 1.3f);
            var hud = hudGo.AddComponent<SonaHud>();
            hud.head = head;

            var pointerGo = new GameObject("SONA Pointer");
            pointerGo.transform.SetParent(sona.transform, false);
            var pointer = pointerGo.AddComponent<SonaPointer>();
            pointer.rayOrigin = rightHand;
            pointer.viewCamera = head.GetComponent<Camera>();
            pointer.laserMaterial = fxMat;
            pointer.reticleMaterial = uiMat;

            var panelGo = new GameObject("SONA Control Bar");
            panelGo.transform.SetParent(sona.transform, false);
            var panel = panelGo.AddComponent<SonaSettingsPanel>();
            panel.head = head;
            panel.uiMaterial = uiMat;

            var controller = sona.AddComponent<SonaSweepController>();
            controller.head = head;
            controller.room = room;
            controller.stereoRenderer = stereo;
            controller.hrtfRenderer = hrtf;
            controller.visualizer = viz;
            controller.hud = hud;
            controller.pointer = pointer;
            controller.settingsPanel = panel;
            panel.controller = controller;

            if (head.root != head && head.root.GetComponent<SonaDesktopRig>() == null)
            {
                head.root.gameObject.AddComponent<SonaDesktopRig>();
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AddSceneToBuild(ScenePath);
            int voices = RaiseRealVoices(96);
            Selection.activeGameObject = sona;

            Debug.Log("[SONA] Created " + ScenePath + " (first in the build list, URP active, Max Real Voices = " + voices +
                      "). Press Play to test with the mouse, or Build And Run to the Quest.");
        }

        [MenuItem("SONA/Export Python-Parity sweep.wav", false, 20)]
        public static void ExportParityWav()
        {
            // Mirrors render.py: default scene (Open Demo), 5 samples/m, max_range = far, near 0.4, 1.2 s, sqrt foveation.
            RoomLayout layout = SonaRoomModel.MakePreset(SonaRoomModel.DefaultPresetId);
            double far = SonaRoomModel.FarthestCornerDistance(layout);
            ListenerPose3D pose = SonaRoomModel.ListenerPose(layout);
            List<Return3D> returns = SonaFrontend.SurfaceEmission(SonaRoomModel.SurfaceQuads(layout), pose, 5.0, far);
            var ep = new EncodingParams { Near = 0.4, Far = far, SweepDuration = 1.2, FoveationExponent = 0.5 };
            List<ScheduledReturn> plan = SonaEncoder.Encode(returns, ep);
            var bank = new GrainBank(new GranularParams { SampleRate = 44100 });
            double gain, peak;
            float[] stereo = GranularRenderer.RenderOffline(plan, ep.SweepDuration, bank, new PitchMapper(), out gain, out peak);
            string path = Path.GetFullPath("SONA_unity_sweep.wav");
            WriteWav16(path, stereo, 44100);
            Debug.Log(string.Format(
                "[SONA] surface points: {0}   scheduled: {1}   wrote {2} ({3:0.00}s). first onset {4:0} ms -> last onset {5:0} ms. " +
                "Compare with the Python sweep.wav from render.py: the samples should be identical.",
                returns.Count, plan.Count, path, stereo.Length / 2 / 44100.0,
                plan.Count > 0 ? plan[0].Onset * 1000 : 0, plan.Count > 0 ? plan[plan.Count - 1].Onset * 1000 : 0));
            EditorUtility.RevealInFinder(path);
        }

        // ------------------------------------------------------------------ helpers

        static Transform CreateRig(out Transform rightHand)
        {
            rightHand = null;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RigPrefabPath);
            if (prefab == null)
            {
                foreach (string guid in AssetDatabase.FindAssets("OVRCameraRig t:Prefab"))
                {
                    string p = AssetDatabase.GUIDToAssetPath(guid);
                    if (Path.GetFileNameWithoutExtension(p) == "OVRCameraRig")
                    {
                        prefab = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                        break;
                    }
                }
            }
            if (prefab != null)
            {
                var rig = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                rig.transform.position = Vector3.zero;
                rig.transform.rotation = Quaternion.identity;
                // The prefab ships with Eye Level tracking; SONA's room has its floor at y = 0.
                Component manager = rig.GetComponent("OVRManager");
                if (manager != null)
                {
                    var so = new SerializedObject(manager);
                    SerializedProperty origin = so.FindProperty("_trackingOriginType");
                    if (origin != null)
                    {
                        origin.intValue = 1;   // OVRManager.TrackingOrigin.FloorLevel
                        so.ApplyModifiedPropertiesWithoutUndo();
                    }
                    else
                    {
                        Debug.LogWarning("[SONA] Set OVRManager > Tracking Origin Type = Floor Level by hand.");
                    }
                }
                rightHand = FindDeep(rig.transform, "RightHandAnchor");
                Transform center = FindDeep(rig.transform, "CenterEyeAnchor");
                if (center != null) return center;
                Debug.LogWarning("[SONA] CenterEyeAnchor not found; using the rig root as the head.");
                return rig.transform;
            }

            Debug.LogWarning("[SONA] OVRCameraRig prefab not found (is the Meta XR Core SDK installed?). Using a plain camera.");
            var rigGo = new GameObject("Desktop Rig");
            var camGo = new GameObject("Main Camera");
            camGo.transform.SetParent(rigGo.transform, false);
            camGo.tag = "MainCamera";
            camGo.AddComponent<Camera>();
            camGo.AddComponent<AudioListener>();
            return camGo.transform;
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

        static Material MakeMaterial(string name, Shader shader, Color color)
        {
            EnsureFolder(MaterialDir);
            string path = MaterialDir + "/" + name + ".mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            else if (shader != null && mat.shader != shader)
            {
                mat.shader = shader;
                mat.shaderKeywords = new string[0]; // drop keywords that belonged to the old shader
            }
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            return mat;
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        static void AddSceneToBuild(string path)
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            scenes.RemoveAll(s => s.path == path);
            scenes.Insert(0, new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        static int RaiseRealVoices(int minimum)
        {
            Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/AudioManager.asset");
            if (assets == null || assets.Length == 0) return -1;
            var so = new SerializedObject(assets[0]);
            SerializedProperty real = so.FindProperty("m_RealVoiceCount");
            if (real == null) return -1;
            if (real.intValue < minimum)
            {
                real.intValue = minimum;
                so.ApplyModifiedProperties();
            }
            return real.intValue;
        }

        static void WriteWav16(string path, float[] interleavedStereo, int sampleRate)
        {
            using (var w = new BinaryWriter(File.Create(path)))
            {
                int dataBytes = interleavedStereo.Length * 2;
                w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
                w.Write(36 + dataBytes);
                w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
                w.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                w.Write(16);
                w.Write((short)1);              // PCM
                w.Write((short)2);              // stereo
                w.Write(sampleRate);
                w.Write(sampleRate * 2 * 2);    // byte rate
                w.Write((short)4);              // block align
                w.Write((short)16);
                w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
                w.Write(dataBytes);
                for (int i = 0; i < interleavedStereo.Length; i++)
                {
                    // render.py: (np.clip(stereo, -1, 1) * 32767).astype("<i2") (float32 math, truncation)
                    float v = Mathf.Clamp(interleavedStereo[i], -1f, 1f) * 32767f;
                    w.Write((short)v);
                }
            }
        }
    }
}
