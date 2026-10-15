// SONA Quest port: shared additive "glow" materials (Resources/SonaGlow.shader) for the sweep
// visualization and the laser. Made at runtime so existing scenes need no new material assets.
using UnityEngine;

namespace Sona
{
    public static class SonaGlow
    {
        static Shader s_shader;
        static bool s_searched;
        static Material s_dot;
        static Material s_line;

        static Shader GlowShader
        {
            get
            {
                if (!s_searched)
                {
                    s_searched = true;
                    s_shader = Resources.Load<Shader>("SonaGlow");
                    if (s_shader == null) s_shader = Shader.Find("SONA/Glow");
                }
                return s_shader;
            }
        }

        /// <summary>Soft round dot for particle billboards, or null if the shader is unavailable.</summary>
        public static Material Dot
        {
            get
            {
                if (s_dot == null) s_dot = Make(0f, "SONA Glow Dot");
                return s_dot;
            }
        }

        /// <summary>Soft-edged line for LineRenderers, or null if the shader is unavailable.</summary>
        public static Material Line
        {
            get
            {
                if (s_line == null) s_line = Make(1f, "SONA Glow Line");
                return s_line;
            }
        }

        static Material Make(float shape, string name)
        {
            Shader sh = GlowShader;
            if (sh == null || !sh.isSupported) return null;
            var m = new Material(sh) { name = name, hideFlags = HideFlags.DontSave };
            m.SetFloat("_Shape", shape);
            return m;
        }
    }
}
