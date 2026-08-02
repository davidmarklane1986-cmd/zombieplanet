using UnityEngine;

/// <summary>
/// Render-only view culling for a zombie, mirroring the foliage system's rule
/// (<see cref="FoliageByColour"/>): an object draws when it is inside the gameplay camera's frustum
/// expanded by a small border, otherwise it is culled. Here that toggles ONLY rendering — the zombie's
/// AI / movement / surface-stick / attack (driven by <see cref="ZombieAI"/> in FixedUpdate) keep running
/// off-screen, exactly like vegetation keeps existing while not drawn.
///
/// What it does when OUT of view:
///  • disables the zombie's Renderer(s) (SkinnedMeshRenderer / MeshRenderer) so nothing is drawn, and
///  • the Animator is set to <see cref="AnimatorCullingMode.CullCompletely"/> so it stops spending CPU
///    animating off-screen. This is safe ONLY because zombie movement is code-driven (Rigidbody forces /
///    velocity in <see cref="ZombieAI.FixedUpdate"/>) with root motion OFF — pausing animation does not
///    freeze movement. (If movement were root-motion driven we'd use CullUpdateTransforms instead.)
///
/// The check is THROTTLED (a few times a second, with a per-instance random phase) and the camera +
/// frustum planes are computed ONCE PER FRAME and shared across every zombie, so this never does
/// expensive per-frame work per zombie. It deliberately does NOT touch the GameObject's active state,
/// the ZombieAI behaviour, or the performance tiers — purely a render-visibility optimization.
/// </summary>
[DisallowMultipleComponent]
public class ZombieVisibilityCuller : MonoBehaviour
{
    // Frustum border (world units). Matches FoliageByColour's pooled-object border (DefaultObjectFrustumMargin = 4):
    // a zombie shows when inside the camera frustum expanded by this small margin, else it is culled. The
    // margin activates it slightly BEFORE it enters the visible frustum so it doesn't pop in when you turn.
    const float OnMargin = 4f;
    // Larger border to LEAVE view than to enter it — same hysteresis idea as FoliageByColour.CullPooled,
    // so a zombie on the exact view edge doesn't flicker on/off.
    const float OffMargin = OnMargin + 4f;

    // Re-evaluate visibility a few times a second (staggered). Animator stays AlwaysAnimate.
    const float CheckInterval = 0.15f;

    Renderer[] _renderers;
    float _nextCheckTime;
    bool _visible = true;
    bool _initialized;

    // ---- Shared per-frame camera + frustum cache (computed once per frame, reused by ALL zombies) ----
    static int s_FrameStamp = -1;
    static Camera s_Cam;
    static bool s_HasCam;
    static readonly Plane[] s_Planes = new Plane[6];

    public bool IsShown => _visible;

    void OnEnable()
    {
        EnsureInit();
        ApplyVisibility(true, force: true);
        _nextCheckTime = Time.time + Random.value * CheckInterval;
    }

    void EnsureInit()
    {
        if (_initialized)
            return;
        _initialized = true;

        _renderers = GetComponentsInChildren<Renderer>(true);
    }

    void Update()
    {
        if (Time.time < _nextCheckTime)
            return;
        _nextCheckTime = Time.time + CheckInterval;

        if (!TryGetSharedFrustum(out Plane[] planes))
        {
            ApplyVisibility(true, force: false);
            return;
        }

        Bounds b = ComputeWorldBounds();
        float margin = _visible ? OffMargin : OnMargin;
        b.Expand(2f * margin);
        bool want = GeometryUtility.TestPlanesAABB(planes, b);
        ApplyVisibility(want, force: false);
    }

    Bounds ComputeWorldBounds()
    {
        if (_renderers != null)
        {
            bool has = false;
            Bounds combined = new Bounds(transform.position, Vector3.zero);
            for (int i = 0; i < _renderers.Length; i++)
            {
                var r = _renderers[i];
                if (r == null)
                    continue;
                if (!has) { combined = r.bounds; has = true; }
                else combined.Encapsulate(r.bounds);
            }
            if (has)
                return combined;
        }
        return new Bounds(transform.position, Vector3.one * 2f);
    }

    void ApplyVisibility(bool visible, bool force)
    {
        if (!force && visible == _visible)
            return;
        _visible = visible;
        if (_renderers == null)
            return;
        for (int i = 0; i < _renderers.Length; i++)
        {
            var r = _renderers[i];
            if (r != null)
                r.enabled = visible;
        }
        // Animator enable/culling is owned by ZombieAI (distance + AlwaysAnimate while playing).
    }

    // Resolves the gameplay camera and its frustum planes ONCE per frame, shared by every zombie instance.
    static bool TryGetSharedFrustum(out Plane[] planes)
    {
        int frame = Time.frameCount;
        if (s_FrameStamp != frame)
        {
            s_FrameStamp = frame;
            s_Cam = ResolveCamera(s_Cam);
            s_HasCam = s_Cam != null;
            if (s_HasCam)
                GeometryUtility.CalculateFrustumPlanes(s_Cam, s_Planes);
        }
        planes = s_Planes;
        return s_HasCam;
    }

    // Same camera selection as FoliageByColour: keep the cached camera if still valid, else Camera.main,
    // else the highest-depth on-screen camera (the one drawn on top). Duplicated (a few lines) on purpose
    // so the foliage system is left completely untouched.
    static Camera ResolveCamera(Camera prev)
    {
        if (prev != null && prev.isActiveAndEnabled && prev.targetTexture == null)
            return prev;
        var cam = Camera.main;
        if (cam == null)
            cam = PickBestOnScreenCamera();
        return cam;
    }

    static Camera PickBestOnScreenCamera()
    {
        var cams = Camera.allCameras; // enabled cameras only
        Camera best = null;
        float bestDepth = float.NegativeInfinity;
        for (int i = 0; i < cams.Length; i++)
        {
            var c = cams[i];
            if (c == null || c.targetTexture != null)
                continue;
            if (c.depth >= bestDepth) { bestDepth = c.depth; best = c; }
        }
        return best;
    }
}
