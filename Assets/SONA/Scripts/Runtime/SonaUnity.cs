// SONA Quest port: small helpers between Unity types and the double-precision core.
using Sona.Core;
using UnityEngine;

namespace Sona
{
    public static class SonaUnity
    {
        public static Vec3d ToVec3d(this Vector3 v)
        {
            return new Vec3d(v.x, v.y, v.z);
        }

        public static Vector3 ToVector3(this Vec3d v)
        {
            return new Vector3((float)v.X, (float)v.Y, (float)v.Z);
        }

        public static QuatD ToQuatD(this Quaternion q)
        {
            return new QuatD(q.x, q.y, q.z, q.w);
        }

        /// <summary>The transform itself or its first descendant with this name (depth first), or null.</summary>
        public static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                Transform found = FindDeep(t.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>Destroy that is safe in edit mode and play mode.</summary>
        public static void SafeDestroy(Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Object.Destroy(obj);
            else Object.DestroyImmediate(obj);
        }
    }
}
