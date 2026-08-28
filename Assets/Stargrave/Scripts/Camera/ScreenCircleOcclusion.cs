using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Stargrave.CameraOcclusion
{
    /// <summary>
    /// Cuts holes in foliage between the camera and the player inside the screen circle.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PlayerScreenCircleOverlay))]
    [DefaultExecutionOrder(250)]
    public sealed class ScreenCircleOcclusion : MonoBehaviour
    {
        const float DepthMargin = 0.15f;
        const float DetectionInterval = 0.08f;

        [SerializeField] Shader occlusionShader;
        [SerializeField] LayerMask occluderMask;
        [SerializeField] float minCameraDistance = 0.35f;
        [Tooltip("Small correction for render-target/overlay alignment, in pixels.")]
        [SerializeField] Vector2 holeOffsetPixels = new Vector2(6f, 0f);
        [Tooltip("Transition width at the hole edge, in pixels.")]
        [Range(1f, 160f)]
        [SerializeField] float edgeSoftnessPixels = 64f;

        readonly FoliageOcclusionDetector _detector = new FoliageOcclusionDetector();
        readonly FoliageFadeMaterialCache _cache = new FoliageFadeMaterialCache();
        readonly Dictionary<EntityId, FoliageFadeState> _states = new Dictionary<EntityId, FoliageFadeState>(64);
        readonly List<EntityId> _scratchRemove = new List<EntityId>(16);
        readonly List<FoliageOccluder> _hits = new List<FoliageOccluder>(64);
        readonly HashSet<EntityId> _wantedIds = new HashSet<EntityId>();

        PlayerScreenCircleOverlay _overlay;
        Camera _camera;
        bool _loggedShaderMissing;
        bool _frameValid;
        bool _retrofitted;
        float _nextDetectionTime;
        Vector3 _playerCenter;
        Vector2 _screenCenterViewport;
        Vector2 _holeCenterViewport;
        float _screenRadiusViewport;
        Vector3 _sightDir;
        float _playerViewDepth;

        void Awake()
        {
            _overlay = GetComponent<PlayerScreenCircleOverlay>();
            _camera = GetComponent<Camera>();

            if (occlusionShader == null)
                occlusionShader = Shader.Find("Shader Graphs/StargraveFoliageGltfOcclusion");
            if (occlusionShader == null && !_loggedShaderMissing)
            {
                _loggedShaderMissing = true;
                Debug.LogWarning(
                    "[ScreenCircleOcclusion] Assign Assets/Stargrave/Shaders/StargraveFoliageGltfOcclusion.shadergraph " +
                    "on Main Camera > Screen Circle Occlusion, or run Tools/Stargrave/Camera/Add Player Screen Circle.");
            }

            _cache.SetShader(occlusionShader);

            if (occluderMask == 0)
            {
                int foliage = LayerMask.NameToLayer(FoliageOccluder.FoliageLayerName);
                occluderMask = foliage >= 0 ? (1 << foliage) : (LayerMask)Physics.DefaultRaycastLayers;
            }
        }

        void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        }

        void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            ReleaseAll();
            ScreenCircleOcclusionShader.ClearGlobals();
        }

        void OnDestroy()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            ReleaseAll();
            _cache.Dispose();
        }

        void LateUpdate()
        {
            if (!isActiveAndEnabled || _overlay == null || _camera == null)
                return;

            if (!_retrofitted)
            {
                RetrofitOccludersOnStreamedFoliage();
                _retrofitted = true;
            }

            UpdateOcclusionTargets();
        }

        void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            if (!isActiveAndEnabled || cam == null)
                return;

            if (cam.cameraType != CameraType.Game || cam.targetTexture != null)
                return;

            if (_camera != null && cam != _camera)
                return;

            if (!RefreshFrameFromCamera(cam))
            {
                _frameValid = false;
                ScreenCircleOcclusionShader.ClearGlobals();
                return;
            }

            ApplyShaderGlobals();
        }

        void UpdateOcclusionTargets()
        {
            if (!RefreshFrameFromCamera(_camera))
            {
                _frameValid = false;
                ReleaseAll();
                ScreenCircleOcclusionShader.ClearGlobals();
                return;
            }

            if (Time.unscaledTime < _nextDetectionTime)
                return;
            _nextDetectionTime = Time.unscaledTime + DetectionInterval;

            _detector.CollectScreenCircleOccluders(
                _camera,
                _playerCenter,
                _holeCenterViewport,
                _screenRadiusViewport,
                _hits);
            _wantedIds.Clear();

            for (int i = 0; i < _hits.Count; i++)
            {
                var occluder = _hits[i];
                if (occluder == null || !occluder.isActiveAndEnabled)
                    continue;

                EntityId id = occluder.GetEntityId();
                _wantedIds.Add(id);

                if (!_states.TryGetValue(id, out var state) || state == null || !state.IsAlive)
                {
                    state = new FoliageFadeState(occluder, _cache);
                    if (!state.CanOcclude)
                        continue;
                    _states[id] = state;
                }

                state.WantedThisFrame = true;
                state.Apply();
            }

            ReleaseUnwanted();
            ApplyShaderGlobals();
        }

        bool RefreshFrameFromCamera(Camera cam)
        {
            if (cam == null || !_overlay.TryGetOcclusionFrame(cam, minCameraDistance,
                    out _, out _playerCenter, out _screenCenterViewport, out _screenRadiusViewport))
                return false;

            _holeCenterViewport = _screenCenterViewport + new Vector2(
                holeOffsetPixels.x / Mathf.Max(1f, Screen.width),
                holeOffsetPixels.y / Mathf.Max(1f, Screen.height));
            _sightDir = cam.ViewportPointToRay(
                new Vector3(_holeCenterViewport.x, _holeCenterViewport.y, 0f)).direction;
            _playerViewDepth = cam.WorldToViewportPoint(_playerCenter).z;
            _frameValid = true;
            return true;
        }

        void ApplyShaderGlobals()
        {
            if (!_frameValid)
            {
                ScreenCircleOcclusionShader.ClearGlobals();
                return;
            }

            ScreenCircleOcclusionShader.ApplyGlobals(
                _playerCenter,
                _holeCenterViewport,
                _sightDir,
                _playerViewDepth,
                _screenRadiusViewport,
                Mathf.Max(0f, edgeSoftnessPixels) / Mathf.Max(1f, Screen.height),
                DepthMargin);
        }

        void ReleaseUnwanted()
        {
            _scratchRemove.Clear();
            foreach (var kv in _states)
            {
                var state = kv.Value;
                if (state == null || !state.IsAlive)
                {
                    state?.ForceRestore();
                    _scratchRemove.Add(kv.Key);
                    continue;
                }

                if (_wantedIds.Contains(kv.Key))
                    continue;

                state.WantedThisFrame = false;
                state.Apply();
            }

            for (int i = 0; i < _scratchRemove.Count; i++)
                _states.Remove(_scratchRemove[i]);
        }

        void ReleaseAll()
        {
            foreach (var kv in _states)
                kv.Value?.ForceRestore();
            _states.Clear();
            _wantedIds.Clear();
            _frameValid = false;
        }

        void RetrofitOccludersOnStreamedFoliage()
        {
            var renderers = FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null)
                    continue;

                Transform tr = r.transform;
                if (!IsLikelyFoliageTransform(tr))
                    continue;

                FoliageOccluder.EnsureOn(FindFoliageOccluderRoot(tr).gameObject);
            }
        }

        static Transform FindFoliageOccluderRoot(Transform tr)
        {
            Transform node = tr;
            Transform best = tr;
            while (node != null)
            {
                string n = node.name;
                if (n.StartsWith("Foliage_") || n.StartsWith("tree_") || n.StartsWith("tree"))
                    best = node;
                node = node.parent;
            }

            return best;
        }

        static bool IsLikelyFoliageTransform(Transform tr)
        {
            Transform walk = tr;
            while (walk != null)
            {
                string n = walk.name;
                if (n.StartsWith("Foliage_"))
                    return true;
                if (n.StartsWith("tree_") || n.StartsWith("tree"))
                    return true;
                walk = walk.parent;
            }

            return false;
        }
    }
}
