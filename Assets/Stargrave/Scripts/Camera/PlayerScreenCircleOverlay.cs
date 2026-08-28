using UnityEngine;
using UnityEngine.UI;

namespace Stargrave.CameraOcclusion
{
    /// <summary>
    /// Draws a screen-space circle around where the player body appears on screen.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    [RequireComponent(typeof(Camera))]
    [DisallowMultipleComponent]
    public sealed class PlayerScreenCircleOverlay : MonoBehaviour
    {
        [SerializeField] Transform player;
        [SerializeField] Transform cameraTarget;
        [Tooltip("Circle diameter on screen in pixels.")]
        [SerializeField] float diameterPixels = 288f;
        [SerializeField] bool showDebugRing = true;
        [SerializeField] Color color = new Color(1f, 1f, 1f, 0.9f);
        [SerializeField] float ringThicknessPixels = 2f;
        [SerializeField] int textureResolution = 128;

        Camera _camera;
        Canvas _canvas;
        RawImage _ringImage;
        Texture2D _ringTexture;

        public float DiameterPixels => diameterPixels;

        public void Configure(Transform playerRoot, Transform aimPivot)
        {
            if (playerRoot != null)
                player = playerRoot;
            if (aimPivot != null)
                cameraTarget = aimPivot;
        }

        void Awake()
        {
            _camera = GetComponent<Camera>();
            ResolveReferences();
            BuildRingTexture();
            BuildOverlayUi();
            EnsureOcclusionPass();
        }

        void EnsureOcclusionPass()
        {
            if (GetComponent<ScreenCircleOcclusion>() == null)
                gameObject.AddComponent<ScreenCircleOcclusion>();
        }

        void OnEnable()
        {
            if (_ringImage != null)
                _ringImage.enabled = showDebugRing;
        }

        void OnDisable()
        {
            if (_ringImage != null)
                _ringImage.enabled = false;
        }

        void OnDestroy()
        {
            if (_ringTexture != null)
                Destroy(_ringTexture);
            if (_canvas != null)
                Destroy(_canvas.gameObject);
        }

        void LateUpdate()
        {
            ResolveReferences();
            UpdateRingPosition();
        }

        void ResolveReferences()
        {
            if (player == null)
            {
                var tagged = GameObject.FindGameObjectWithTag("Player");
                if (tagged != null)
                    player = tagged.transform;
            }

            if (cameraTarget == null && player != null)
            {
                var look = player.GetComponent<MouseLook_Gravity>();
                if (look != null && look.cameraTarget != null)
                    cameraTarget = look.cameraTarget;
            }
        }

        void BuildRingTexture()
        {
            int size = Mathf.Clamp(textureResolution, 32, 256);
            _ringTexture = new Texture2D(size, size, TextureFormat.ARGB32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            float center = size * 0.5f;
            float outer = center - 1f;
            float inner = Mathf.Max(0f, outer - Mathf.Max(1f, ringThicknessPixels));

            Color clear = Color.clear;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x, y), new Vector2(center, center));
                    bool onRing = dist <= outer && dist >= inner;
                    _ringTexture.SetPixel(x, y, onRing ? color : clear);
                }
            }

            _ringTexture.Apply();
        }

        void BuildOverlayUi()
        {
            var canvasGo = new GameObject("PlayerScreenCircleOverlay")
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = short.MaxValue;

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;

            canvasGo.AddComponent<GraphicRaycaster>();

            var imageGo = new GameObject("Ring");
            imageGo.transform.SetParent(canvasGo.transform, false);

            _ringImage = imageGo.AddComponent<RawImage>();
            _ringImage.texture = _ringTexture;
            _ringImage.raycastTarget = false;
            _ringImage.color = Color.white;

            var rt = _ringImage.rectTransform;
            rt.sizeDelta = new Vector2(diameterPixels, diameterPixels);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
        }

        void UpdateRingPosition()
        {
            if (_ringImage == null || _camera == null || player == null)
                return;

            if (!TryGetPlayerBodyWorldCenter(out Vector3 worldCenter))
                return;

            Vector3 viewport = _camera.WorldToViewportPoint(worldCenter);
            if (viewport.z <= 0f)
            {
                _ringImage.enabled = false;
                return;
            }

            _ringImage.enabled = showDebugRing;
            float size = Mathf.Max(8f, diameterPixels);
            var rt = _ringImage.rectTransform;
            rt.sizeDelta = new Vector2(size, size);
            rt.position = new Vector3(viewport.x * Screen.width, viewport.y * Screen.height, 0f);
        }

        public bool TryGetPlayerBodyWorldCenter(out Vector3 worldCenter)
        {
            worldCenter = default;
            if (player == null)
                return false;

            if (TryGetPlayerBodyBounds(out Bounds bounds))
            {
                worldCenter = bounds.center;
                return true;
            }

            if (cameraTarget != null)
            {
                worldCenter = cameraTarget.position - cameraTarget.up * 0.35f;
                return true;
            }

            worldCenter = player.position + player.up * 1.1f;
            return true;
        }

        bool TryGetPlayerBodyBounds(out Bounds bounds)
        {
            bounds = default;
            bool any = false;

            var renderers = player.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null)
                    continue;

                if (cameraTarget != null && r.transform.IsChildOf(cameraTarget))
                    continue;

                if (!any)
                {
                    bounds = r.bounds;
                    any = true;
                }
                else
                    bounds.Encapsulate(r.bounds);
            }

            return any;
        }

        public float GetScreenRadiusViewport()
        {
            return diameterPixels / (2f * Mathf.Max(1f, Screen.height));
        }

        public bool TryGetOcclusionFrame(
            Camera cam,
            float minDistance,
            out Vector3 camPos,
            out Vector3 playerCenter,
            out Vector2 screenCenterViewport,
            out float screenRadiusViewport)
        {
            camPos = default;
            playerCenter = default;
            screenCenterViewport = default;
            screenRadiusViewport = 0f;

            if (cam == null || player == null)
                return false;

            if (!TryGetPlayerBodyWorldCenter(out playerCenter))
                return false;

            camPos = cam.transform.position;

            Vector3 viewport = cam.WorldToViewportPoint(playerCenter);
            if (viewport.z <= 0f)
                return false;

            screenCenterViewport = new Vector2(viewport.x, viewport.y);
            screenRadiusViewport = GetScreenRadiusViewport();

            if (Vector3.Distance(camPos, playerCenter) < minDistance)
                return false;

            return screenRadiusViewport > 0f;
        }
    }
}
