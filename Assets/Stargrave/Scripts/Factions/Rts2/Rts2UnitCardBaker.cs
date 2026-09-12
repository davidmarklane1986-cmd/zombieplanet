using UnityEngine;
using UnityEngine.Rendering;

namespace Stargrave.Rts2
{
    /// <summary>
    /// One-shot side-view bake of PlayableCharacter prefabs into alpha cutout card textures.
    /// </summary>
    public static class Rts2UnitCardBaker
    {
        const int TexWidth = 128;
        const int TexHeight = 256;
        const int BakeLayer = 31; // rarely used; camera only sees this

        public static Texture2D BakeSideCard(GameObject prefab, string nameHint)
        {
            if (prefab == null)
                return null;

            GameObject root = null;
            Camera cam = null;
            Light light = null;
            RenderTexture rt = null;
            try
            {
                // Far from gameplay so nothing interacts.
                Vector3 origin = new Vector3(0f, -5000f, 0f);
                root = new GameObject("Rts2CardBakeRoot");
                root.transform.position = origin;
                root.hideFlags = HideFlags.HideAndDontSave;

                GameObject visual = Object.Instantiate(prefab, root.transform);
                visual.name = prefab.name;
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
                visual.transform.localScale = prefab.transform.localScale.sqrMagnitude > 1e-6f
                    ? prefab.transform.localScale
                    : Vector3.one;
                SetLayerRecursive(visual, BakeLayer);

                // Strip physics / audio; keep renderers + animator for pose.
                var cols = visual.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < cols.Length; i++)
                    cols[i].enabled = false;
                var rbs = visual.GetComponentsInChildren<Rigidbody>(true);
                for (int i = 0; i < rbs.Length; i++)
                    Object.DestroyImmediate(rbs[i]);
                var audios = visual.GetComponentsInChildren<AudioSource>(true);
                for (int i = 0; i < audios.Length; i++)
                    audios[i].enabled = false;

                Animator anim = visual.GetComponentInChildren<Animator>(true);
                if (anim != null)
                {
                    anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    anim.applyRootMotion = false;
                    // Sample a standing pose if possible.
                    if (anim.runtimeAnimatorController != null)
                    {
                        int idle = Animator.StringToHash("root|Idle_Menu");
                        if (!anim.HasState(0, idle))
                            idle = Animator.StringToHash("Idle_Menu");
                        if (anim.HasState(0, idle))
                            anim.Play(idle, 0, 0f);
                        anim.Update(0f);
                    }
                }

                var skins = visual.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                for (int i = 0; i < skins.Length; i++)
                {
                    if (skins[i] != null)
                        skins[i].updateWhenOffscreen = true;
                }

                Bounds bounds = ComputeRendererBounds(visual);
                if (bounds.size.sqrMagnitude < 1e-6f)
                    bounds = new Bounds(visual.transform.position, Vector3.one * 1.8f);

                // Side camera: look from +X toward character center.
                var camGo = new GameObject("Rts2CardBakeCam");
                camGo.transform.SetParent(root.transform, false);
                cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                cam.orthographic = true;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 40f;
                cam.cullingMask = 1 << BakeLayer;
                cam.allowHDR = false;
                cam.allowMSAA = false;
                cam.enabled = false;

                float height = Mathf.Max(0.5f, bounds.size.y);
                float width = Mathf.Max(0.3f, Mathf.Max(bounds.size.x, bounds.size.z));
                float pad = 1.18f;
                cam.orthographicSize = (height * pad) * 0.5f;
                Vector3 center = bounds.center;
                float dist = Mathf.Max(2.5f, width * 2.2f + 1.5f);
                cam.transform.position = center + Vector3.right * dist;
                cam.transform.rotation = Quaternion.LookRotation(center - cam.transform.position, Vector3.up);

                // Aspect match texture.
                float aspect = TexWidth / (float)TexHeight;
                // Widen ortho if character is fat relative to tall frame.
                float frameHalfW = cam.orthographicSize * aspect;
                if (width * pad * 0.5f > frameHalfW)
                    cam.orthographicSize = (width * pad * 0.5f) / aspect;

                var lightGo = new GameObject("Rts2CardBakeLight");
                lightGo.transform.SetParent(root.transform, false);
                light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.15f;
                light.color = Color.white;
                light.cullingMask = 1 << BakeLayer;
                light.transform.rotation = Quaternion.Euler(35f, -40f, 0f);
                light.shadows = LightShadows.None;

                rt = new RenderTexture(TexWidth, TexHeight, 24, RenderTextureFormat.ARGB32)
                {
                    name = "Rts2CardBakeRT",
                    antiAliasing = 1,
                    filterMode = FilterMode.Bilinear
                };
                cam.targetTexture = rt;
                cam.Render();

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(TexWidth, TexHeight, TextureFormat.RGBA32, mipChain: true)
                {
                    name = string.IsNullOrEmpty(nameHint) ? "Rts2UnitCardBaked" : $"Rts2UnitCard_{nameHint}",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    alphaIsTransparency = true
                };
                tex.ReadPixels(new Rect(0, 0, TexWidth, TexHeight), 0, 0);
                tex.Apply(updateMipmaps: true, makeNoLongerReadable: true);
                RenderTexture.active = prev;
                return tex;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Rts2] Unit card bake failed for {prefab.name}: {e.Message}");
                return null;
            }
            finally
            {
                if (cam != null)
                    cam.targetTexture = null;
                if (rt != null)
                {
                    rt.Release();
                    Object.Destroy(rt);
                }
                if (root != null)
                    Object.Destroy(root);
            }
        }

        static Bounds ComputeRendererBounds(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            bool any = false;
            Bounds b = default;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null || !r.enabled)
                    continue;
                if (!any)
                {
                    b = r.bounds;
                    any = true;
                }
                else
                    b.Encapsulate(r.bounds);
            }
            return any ? b : default;
        }

        static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            Transform t = go.transform;
            for (int i = 0; i < t.childCount; i++)
                SetLayerRecursive(t.GetChild(i).gameObject, layer);
        }
    }
}
