// SONA Quest port: rendering diagnostic (kept outside Assets so Unity does not compile it).
// To use it, copy this file into Assets/SONA/Editor and run SONA > Diagnostics > Measure surface sparkles.
// It renders the test room with several material setups and counts isolated bright pixels ("sparkles"),
// which is how the white dots of URP Lit on Intel Iris Xe + Direct3D 11 + Android target were pinned down.
// Results: Logs/SonaSparkleCheck/summary.txt (+ one PNG per setup) in the project folder.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Sona.EditorTools
{
    static class SonaSparkleCheck
    {
        const string LitName = "Universal Render Pipeline/Lit";
        const string SimpleLitName = "Universal Render Pipeline/Simple Lit";
        const string BakedLitName = "Universal Render Pipeline/Baked Lit";
        const string UnlitName = "Universal Render Pipeline/Unlit";

        sealed class Variant
        {
            public string Name;
            public string ShaderName; // null = keep the current materials
            public Action<Material> Configure;
        }

        [MenuItem("SONA/Diagnostics/Measure surface sparkles", false, 200)]
        static void Run()
        {
            var builder = Object.FindAnyObjectByType<SonaRoomBuilder>();
            if (builder == null)
            {
                Debug.LogError("[SONA] Sparkle check: no SONA Room Builder in the open scene.");
                return;
            }

            var renderers = new List<MeshRenderer>(builder.GetComponentsInChildren<MeshRenderer>(true));
            if (renderers.Count == 0)
            {
                Debug.LogError("[SONA] Sparkle check: the room has no generated renderers.");
                return;
            }

            var originals = new Dictionary<MeshRenderer, Material[]>();
            Bounds bounds = renderers[0].bounds;
            foreach (MeshRenderer r in renderers)
            {
                originals[r] = r.sharedMaterials;
                bounds.Encapsulate(r.bounds);
            }

            var variants = new List<Variant>
            {
                new Variant { Name = "00_sona_simplelit", ShaderName = "SONA/Simple Lit" },
                new Variant { Name = "00_sona_simplelit_spec", ShaderName = "SONA/Simple Lit", Configure = m => { m.SetFloat("_SpecularHighlights", 1f); m.EnableKeyword("_SPECULAR_COLOR"); } },
                new Variant { Name = "01_current_assets", ShaderName = null },
                new Variant { Name = "02_lit_default", ShaderName = LitName },
                new Variant { Name = "03_lit_no_spec_no_refl", ShaderName = LitName, Configure = m => { NoSpecular(m); NoReflections(m); } },
                new Variant { Name = "04_lit_no_spec", ShaderName = LitName, Configure = NoSpecular },
                new Variant { Name = "05_lit_no_refl", ShaderName = LitName, Configure = NoReflections },
                new Variant { Name = "06_lit_smooth0", ShaderName = LitName, Configure = m => m.SetFloat("_Smoothness", 0f) },
                new Variant { Name = "07_lit_no_shadows", ShaderName = LitName, Configure = m => { m.SetFloat("_ReceiveShadows", 0f); m.EnableKeyword("_RECEIVE_SHADOWS_OFF"); } },
                new Variant { Name = "08_simplelit", ShaderName = SimpleLitName },
                new Variant { Name = "09_simplelit_spec", ShaderName = SimpleLitName, Configure = m => { m.SetFloat("_SpecularHighlights", 1f); m.EnableKeyword("_SPECULAR_COLOR"); } },
                new Variant { Name = "10_bakedlit", ShaderName = BakedLitName },
                new Variant { Name = "11_unlit", ShaderName = UnlitName },
                new Variant { Name = "12_lit_default_again", ShaderName = LitName },
            };

            string outDir = Path.Combine(Directory.GetCurrentDirectory(), "Logs", "SonaSparkleCheck");
            Directory.CreateDirectory(outDir);

            var sb = new StringBuilder();
            sb.AppendLine("SONA sparkle check  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("GPU: " + SystemInfo.graphicsDeviceName + " | " + SystemInfo.graphicsDeviceType + " | " + SystemInfo.graphicsDeviceVersion);
            sb.AppendLine("Build target: " + EditorUserBuildSettings.activeBuildTarget + " | pipeline: " +
                          (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null ? UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline.name : "Built-in"));
            foreach (KeyValuePair<MeshRenderer, Material[]> kv in originals)
            {
                Material m = kv.Value.Length > 0 ? kv.Value[0] : null;
                if (m == null) continue;
                sb.AppendLine("  " + kv.Key.name + ": " + m.name + " [" + (m.shader != null ? m.shader.name : "?") + "] keywords: " +
                              string.Join(" ", m.shaderKeywords));
            }

            var camGo = new GameObject("SONA Sparkle Camera") { hideFlags = HideFlags.HideAndDontSave };
            var cam = camGo.AddComponent<Camera>();
            cam.enabled = false;
            cam.fieldOfView = 60f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 200f;
            cam.clearFlags = CameraClearFlags.Skybox;
            Vector3 target = bounds.center;
            camGo.transform.position = new Vector3(bounds.center.x - 0.6f, bounds.min.y + 2.4f, bounds.min.z - 1.8f);
            camGo.transform.LookAt(target);

            const int width = 1280, height = 720;
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1, name = "SONA Sparkle RT" };
            rt.Create();
            cam.targetTexture = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);

            bool asyncBefore = ShaderUtil.allowAsyncCompilation;
            ShaderUtil.allowAsyncCompilation = false;
            var made = new List<Material>();
            try
            {
                foreach (Variant v in variants)
                {
                    if (v.ShaderName != null && Shader.Find(v.ShaderName) == null)
                    {
                        sb.AppendLine(v.Name.PadRight(26) + " (shader not found)");
                        continue;
                    }
                    Apply(v, originals, made);
                    // Render twice: the first pass can still be compiling variants on some setups.
                    cam.Render();
                    cam.Render();
                    RenderTexture prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    tex.Apply();
                    RenderTexture.active = prev;

                    int sparkles = CountSparkles(tex.GetPixels32(), width, height);
                    File.WriteAllBytes(Path.Combine(outDir, v.Name + ".png"), tex.EncodeToPNG());
                    sb.AppendLine(v.Name.PadRight(26) + " sparkles = " + sparkles);
                }
            }
            finally
            {
                foreach (KeyValuePair<MeshRenderer, Material[]> kv in originals)
                {
                    if (kv.Key != null) kv.Key.sharedMaterials = kv.Value;
                }
                foreach (Material m in made) Object.DestroyImmediate(m);
                ShaderUtil.allowAsyncCompilation = asyncBefore;
                cam.targetTexture = null;
                rt.Release();
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(tex);
                Object.DestroyImmediate(camGo);
            }

            File.WriteAllText(Path.Combine(outDir, "summary.txt"), sb.ToString());
            Debug.Log("[SONA] Sparkle check written to " + outDir + "\n" + sb);
        }

        static void NoSpecular(Material m)
        {
            m.SetFloat("_SpecularHighlights", 0f);
            m.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
        }

        static void NoReflections(Material m)
        {
            m.SetFloat("_EnvironmentReflections", 0f);
            m.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
        }

        static void Apply(Variant v, Dictionary<MeshRenderer, Material[]> originals, List<Material> made)
        {
            var cache = new Dictionary<Material, Material>();
            foreach (KeyValuePair<MeshRenderer, Material[]> kv in originals)
            {
                if (v.ShaderName == null)
                {
                    kv.Key.sharedMaterials = kv.Value;
                    continue;
                }
                var mats = new Material[kv.Value.Length];
                for (int i = 0; i < mats.Length; i++)
                {
                    Material src = kv.Value[i];
                    if (src == null) continue;
                    Material dst;
                    if (!cache.TryGetValue(src, out dst))
                    {
                        Shader shader = Shader.Find(v.ShaderName);
                        dst = new Material(shader) { name = src.name + " (" + v.Name + ")", hideFlags = HideFlags.HideAndDontSave };
                        Color c = src.HasProperty("_BaseColor") ? src.GetColor("_BaseColor") : Color.gray;
                        if (dst.HasProperty("_BaseColor")) dst.SetColor("_BaseColor", c);
                        if (dst.HasProperty("_Color")) dst.SetColor("_Color", c);
                        if (v.Configure != null) v.Configure(dst);
                        cache[src] = dst;
                        made.Add(dst);
                    }
                    mats[i] = dst;
                }
                kv.Key.sharedMaterials = mats;
            }
        }

        /// <summary>Isolated bright pixels: a local 3x3 maximum at least 0.2 brighter than its neighbours' mean.</summary>
        static int CountSparkles(Color32[] px, int w, int h)
        {
            var lum = new float[px.Length];
            for (int i = 0; i < px.Length; i++)
            {
                lum[i] = (0.2126f * px[i].r + 0.7152f * px[i].g + 0.0722f * px[i].b) / 255f;
            }
            int count = 0;
            for (int y = 1; y < h - 1; y++)
            {
                for (int x = 1; x < w - 1; x++)
                {
                    int i = y * w + x;
                    float c = lum[i];
                    if (c < 0.45f) continue;
                    float sum = 0f, max = 0f;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            float n = lum[i + dy * w + dx];
                            sum += n;
                            if (n > max) max = n;
                        }
                    }
                    if (c >= max && c - sum / 8f > 0.2f) count++;
                }
            }
            return count;
        }
    }
}
