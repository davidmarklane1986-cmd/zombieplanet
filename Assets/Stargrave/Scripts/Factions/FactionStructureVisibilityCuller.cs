using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Limb + terrain LoS visibility for faction buildings and claimable towns.
/// Disables renderers when occluded (colliders stay for gameplay).
/// Scene view: green = drawn, red = culled, cyan = claim town.
/// </summary>
[DisallowMultipleComponent]
[ExecuteAlways]
public sealed class FactionStructureVisibilityCuller : MonoBehaviour
{
    const float CheckInterval = 0.2f;
    const float MarkerHeight = 10f;
    const float MarkerRadius = 4.5f;
    const float DefaultBuildingDrawDistance = 1100f;
    const float LosProbeHeight = 8f;

    [Tooltip("Draw Scene-view dots for culled/visible structures.")]
    public bool drawSceneMarkers = true;

    [Tooltip("Also draw markers while playing in the Game view Scene overlay.")]
    public bool drawMarkersInPlayMode = true;

    [Tooltip("Max camera distance (world units) for faction buildings / claim towns / construction. " +
             "Much farther than foliage so HQs and markets stay visible across valleys. 0 = built-in default (1100).")]
    public float buildingDrawDistance = DefaultBuildingDrawDistance;

    struct Entry
    {
        public Transform root;
        public Renderer[] renderers;
        public bool visible;
        public bool isTown;
        public string label;
        public float nextCheck;
    }

    readonly List<Entry> _entries = new List<Entry>(64);
    float _nextRescan;
    Planet _planet;
    Vector3 _planetCenter;
    float _surfaceRadius;
    Camera _cam;

    public static FactionStructureVisibilityCuller Ensure(FactionSimulation sim)
    {
        if (sim == null)
            return null;
        var culler = sim.GetComponent<FactionStructureVisibilityCuller>();
        if (culler == null)
            culler = sim.gameObject.AddComponent<FactionStructureVisibilityCuller>();
        return culler;
    }

    void OnEnable()
    {
        _nextRescan = 0f;
    }

    void OnDisable()
    {
        // Restore renderers so disabling the culler never leaves buildings invisible.
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].root == null)
                continue;
            ApplyRenderers(_entries[i].renderers, true);
        }
    }

    void LateUpdate()
    {
        if (!Application.isPlaying)
            return;

        if (Time.time >= _nextRescan)
        {
            _nextRescan = Time.time + 1.25f;
            Rescan();
        }

        _cam = ResolveCamera(_cam);
        if (_cam == null)
            return;

        CachePlanet();
        Vector3 camPos = _cam.transform.position;
        bool horizonOk = _planet != null && _surfaceRadius > 1f;

        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            Entry e = _entries[i];
            if (e.root == null)
            {
                _entries.RemoveAt(i);
                continue;
            }

            if (Time.time < e.nextCheck)
                continue;

            e.nextCheck = Time.time + CheckInterval + Random.value * 0.05f;
            Vector3 pos = e.root.position;
            Vector3 up = e.root.up.sqrMagnitude > 1e-6f ? e.root.up : Vector3.up;
            // Probe mid-building so roofs can stay visible farther than ground-point LoS allowed.
            Vector3 losPoint = pos + up * LosProbeHeight;

            float maxD = buildingDrawDistance > 0f ? buildingDrawDistance : DefaultBuildingDrawDistance;
            float distSq = (losPoint - camPos).sqrMagnitude;
            bool want = distSq <= maxD * maxD;
            if (want && horizonOk)
            {
                // sticky: keep far structures once seen; only drop when clearly behind a ridge/limb.
                want = PlanetHorizonCulling.IsVisibleInWorld(
                    _planet, _planetCenter, _surfaceRadius, camPos, losPoint, sticky: true);
            }

            if (want != e.visible)
            {
                ApplyRenderers(e.renderers, want);
                e.visible = want;
            }
            _entries[i] = e;
        }
    }

    void Rescan()
    {
        // Keep existing entries when possible; rebuild from scene contents.
        var seen = new HashSet<Transform>();
        Building[] buildings = Object.FindObjectsByType<Building>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < buildings.Length; i++)
        {
            Building b = buildings[i];
            if (b == null || b.IsDestroyed)
                continue;
            Track(b.transform, isTown: false, b.Kind.ToString(), seen);
        }

        ClaimableTown[] towns = Object.FindObjectsByType<ClaimableTown>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < towns.Length; i++)
        {
            ClaimableTown t = towns[i];
            if (t == null)
                continue;
            Track(t.transform, isTown: true, "ClaimTown", seen);
        }

        BuildingConstructionSite[] sites = Object.FindObjectsByType<BuildingConstructionSite>(
            FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < sites.Length; i++)
        {
            BuildingConstructionSite s = sites[i];
            if (s == null)
                continue;
            Track(s.transform, isTown: false, "Construction", seen);
        }

        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            if (_entries[i].root == null || !seen.Contains(_entries[i].root))
                _entries.RemoveAt(i);
        }
    }

    void Track(Transform root, bool isTown, string label, HashSet<Transform> seen)
    {
        if (root == null || !seen.Add(root))
            return;

        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].root == root)
            {
                Entry e = _entries[i];
                e.isTown = isTown;
                e.label = label;
                _entries[i] = e;
                return;
            }
        }

        var renderers = root.GetComponentsInChildren<Renderer>(true);
        _entries.Add(new Entry
        {
            root = root,
            renderers = renderers,
            visible = true,
            isTown = isTown,
            label = label,
            nextCheck = Time.time + Random.value * CheckInterval
        });
    }

    static void ApplyRenderers(Renderer[] renderers, bool visible)
    {
        if (renderers == null)
            return;
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] != null)
                renderers[i].enabled = visible;
        }
    }

    void CachePlanet()
    {
        if (_planet == null)
            _planet = Object.FindFirstObjectByType<Planet>();
        if (_planet == null || _cam == null)
            return;
        _planetCenter = _planet.transform.position;
        _surfaceRadius = PlanetHorizonCulling.SampleSurfaceRadius(
            _planet, _planetCenter, _cam.transform.position, 1f);
    }

    static Camera ResolveCamera(Camera prev)
    {
        if (prev != null && prev.isActiveAndEnabled && prev.targetTexture == null)
            return prev;
        Camera cam = Camera.main;
        if (cam != null)
            return cam;
        var cams = Camera.allCameras;
        Camera best = null;
        float bestDepth = float.NegativeInfinity;
        for (int i = 0; i < cams.Length; i++)
        {
            Camera c = cams[i];
            if (c == null || c.targetTexture != null)
                continue;
            if (c.depth >= bestDepth)
            {
                bestDepth = c.depth;
                best = c;
            }
        }
        return best;
    }

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if (!drawSceneMarkers)
            return;
        if (!drawMarkersInPlayMode && Application.isPlaying)
            return;
        if (!Application.isPlaying && _entries.Count == 0)
            Rescan();
        DrawMarkers(selectedOnly: false);
    }

    void OnDrawGizmosSelected()
    {
        if (!drawSceneMarkers)
            return;
        if (!Application.isPlaying && _entries.Count == 0)
            Rescan();
        DrawMarkers(selectedOnly: true);
    }

    void DrawMarkers(bool selectedOnly)
    {
        // Prefer Scene camera when not playing so edit-mode dots still make sense.
        Camera sceneCam = null;
        if (!Application.isPlaying && SceneView.lastActiveSceneView != null)
            sceneCam = SceneView.lastActiveSceneView.camera;
        Camera cam = Application.isPlaying ? ResolveCamera(_cam) : sceneCam;
        if (cam != null)
        {
            _cam = cam;
            CachePlanet();
        }

        for (int i = 0; i < _entries.Count; i++)
        {
            Entry e = _entries[i];
            if (e.root == null)
                continue;

            // Live LoS preview in Scene view (does not toggle renderers outside play).
            bool shown = e.visible;
            if (cam != null && _planet != null && _surfaceRadius > 1f)
            {
                shown = PlanetHorizonCulling.IsVisibleInWorld(
                    _planet, _planetCenter, _surfaceRadius,
                    cam.transform.position, e.root.position + (e.root.up.sqrMagnitude > 1e-6f ? e.root.up : Vector3.up) * LosProbeHeight,
                    sticky: true);
                if (!Application.isPlaying)
                {
                    e.visible = shown;
                    _entries[i] = e;
                }
            }

            Vector3 up = e.root.up.sqrMagnitude > 1e-6f ? e.root.up : Vector3.up;
            Vector3 pos = e.root.position + up * MarkerHeight;

            if (shown)
                Gizmos.color = e.isTown
                    ? new Color(0.2f, 0.95f, 1f, 0.95f)
                    : new Color(0.25f, 1f, 0.35f, 0.95f);
            else
                Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.9f);

            Gizmos.DrawSphere(pos, MarkerRadius);
            Gizmos.DrawLine(e.root.position, pos);

            if (selectedOnly || e.root == Selection.activeTransform)
            {
                string state = shown ? "VISIBLE" : "CULLED";
                Handles.Label(pos + up * (MarkerRadius + 2f), $"{e.label} [{state}]");
            }
        }
    }
#endif
}
