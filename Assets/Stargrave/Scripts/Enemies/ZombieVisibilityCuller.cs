using UnityEngine;

/// <summary>
/// Render-only view culling for a zombie: frustum + planet horizon (not Unity baked occlusion).
/// AI / movement keep running off-screen.
/// </summary>
[DisallowMultipleComponent]
public class ZombieVisibilityCuller : MonoBehaviour
{
    // Frustum border (world units). Matches FoliageByColour's pooled-object border.
    const float OnMargin = 4f;
    const float OffMargin = OnMargin + 4f;
    const float CheckInterval = 0.15f;

    Renderer[] _renderers;
    float _nextCheckTime;
    bool _visible = true;
    bool _initialized;

    static int s_FrameStamp = -1;
    static Camera s_Cam;
    static bool s_HasCam;
    static readonly Plane[] s_Planes = new Plane[6];
    static Planet s_Planet;
    static Vector3 s_PlanetCenter;
    static float s_SurfaceRadius;
    static bool s_HasHorizon;

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

        if (!TryGetSharedFrustum(out Plane[] planes, out Camera cam))
        {
            ApplyVisibility(true, force: false);
            return;
        }

        Bounds b = ComputeWorldBounds();
        float margin = _visible ? OffMargin : OnMargin;
        Bounds expanded = b;
        expanded.Expand(2f * margin);
        bool want = GeometryUtility.TestPlanesAABB(planes, expanded);
        if (want && s_HasHorizon)
        {
            want = PlanetHorizonCulling.IsVisibleInWorld(
                s_Planet,
                s_PlanetCenter,
                s_SurfaceRadius,
                cam.transform.position,
                b.center,
                sticky: _visible);
        }
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
    }

    static bool TryGetSharedFrustum(out Plane[] planes, out Camera cam)
    {
        int frame = Time.frameCount;
        if (s_FrameStamp != frame)
        {
            s_FrameStamp = frame;
            s_Cam = ResolveCamera(s_Cam);
            s_HasCam = s_Cam != null;
            if (s_HasCam)
            {
                GeometryUtility.CalculateFrustumPlanes(s_Cam, s_Planes);
                if (s_Planet == null)
                    s_Planet = Object.FindFirstObjectByType<Planet>();
                if (s_Planet != null)
                {
                    s_PlanetCenter = s_Planet.transform.position;
                    s_SurfaceRadius = PlanetHorizonCulling.SampleSurfaceRadius(
                        s_Planet, s_PlanetCenter, s_Cam.transform.position, 1f);
                    s_HasHorizon = s_SurfaceRadius > 1f;
                }
                else
                {
                    s_HasHorizon = false;
                }
            }
        }
        planes = s_Planes;
        cam = s_Cam;
        return s_HasCam;
    }

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
        var cams = Camera.allCameras;
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
