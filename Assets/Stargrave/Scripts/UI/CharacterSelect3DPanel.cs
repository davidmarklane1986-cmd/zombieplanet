using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Character picker as a 3D carousel: the model closest to camera and facing the screen is the selection.
/// Drag, click, arrows / A-D, or the side chevrons to rotate. Parent UI owns Continue / Back.
/// </summary>
public sealed class CharacterSelect3DPanel : MonoBehaviour
{
    const int PreviewLayer = 5; // UI layer
    const int RtMinEdge = 64;
    const int RtMaxEdge = 1920;
    const float TargetModelHeight = 1.1f;
    const float RingRadius = 1.45f;
    const float CamDistance = 2.15f;
    const float CamHeight = 0.95f;
    const float CamLookHeight = 0.62f;
    const float CamFov = 36f;
    const float YawSmoothTime = 0.16f;
    const float DragDegreesPerPixel = 0.28f;
    const float HoldRepeatDelay = 0.38f;
    const float HoldRepeatRate = 0.16f;

    public System.Action<string> SelectionChanged;

    readonly List<Slot> _slots = new();
    Transform _previewWorldRoot;
    Transform _carouselRoot;
    Camera _cam;
    RenderTexture _rt;
    RawImage _view;
    RectTransform _viewRt;
    Text _nameText;
    Text _statsText;
    GameObject _infoCard;
    GameObject _leftChevron;
    GameObject _rightChevron;
    int _previewLayerMask;
    string _selectedId;
    int _selectedIndex;
    float _currentYaw;
    float _targetYaw;
    float _yawVel;
    bool _dragging;
    bool _dragMoved;
    float _holdTimer;
    int _holdDir;
    static bool _strippedMainCameras;

    struct Slot
    {
        public string id;
        public Transform mount;
        public GameObject modelInstance;
        public PlayableCharacterDef def;
        public float homeAngle;
    }

    public string SelectedId => _selectedId;

    public void Build(Transform parent)
    {
        Clear();

        Font font = BuiltinFont();
        BuildView(parent, font);
        BuildChevrons(parent, font);

        EnsurePreviewWorldRoot();
        _previewLayerMask = 1 << PreviewLayer;
        StripPreviewLayerFromSceneCameras();

        var carouselGo = new GameObject("CarouselRoot");
        carouselGo.transform.SetParent(_previewWorldRoot, false);
        carouselGo.transform.localPosition = Vector3.zero;
        carouselGo.transform.localRotation = Quaternion.identity;
        _carouselRoot = carouselGo.transform;

        PlayableCharacterDef[] defs = PlayableCharacterCatalog.All;
        _selectedId = PlayableCharacterCatalog.GetSelectedId();
        if (defs == null || defs.Length == 0)
            return;

        int count = 0;
        for (int i = 0; i < defs.Length; i++)
        {
            if (defs[i] != null)
                count++;
        }
        if (count == 0)
            return;

        float step = 360f / count;
        int written = 0;
        int selectedIndex = 0;
        for (int i = 0; i < defs.Length; i++)
        {
            PlayableCharacterDef def = defs[i];
            if (def == null)
                continue;
            float home = written * step;
            _slots.Add(CreateSlot(def, home));
            if (def.id == _selectedId)
                selectedIndex = written;
            written++;
        }

        BuildCamera();
        Canvas.ForceUpdateCanvases();
        SyncRenderTargetToView();

        _selectedIndex = selectedIndex;
        _currentYaw = selectedIndex * step;
        _targetYaw = _currentYaw;
        _carouselRoot.localRotation = Quaternion.Euler(0f, _currentYaw, 0f);
        CommitSelection(_slots[_selectedIndex].id, invoke: false);
        RefreshLabels();
    }

    public void SetActivePreviews(bool active)
    {
        enabled = active;
        if (_previewWorldRoot != null)
            _previewWorldRoot.gameObject.SetActive(active);
        if (!active)
        {
            _dragging = false;
            _holdDir = 0;
        }
    }

    void LateUpdate()
    {
        if (_carouselRoot == null || _slots.Count == 0)
            return;

        HandleRotateInput();

        float dt = Time.unscaledDeltaTime;
        if (_dragging)
            _currentYaw = _targetYaw;
        else
            _currentYaw = Mathf.SmoothDampAngle(_currentYaw, _targetYaw, ref _yawVel, YawSmoothTime, 720f, dt);

        _carouselRoot.localRotation = Quaternion.Euler(0f, _currentYaw, 0f);

        // Label tracks whoever is actually front (avoids adjacent name/look mismatch while lerping).
        int nearest = FindNearestIndex(_currentYaw);
        if (nearest != _selectedIndex)
        {
            _selectedIndex = nearest;
            CommitSelection(_slots[nearest].id, invoke: true);
        }

        SyncRenderTargetToView();
        if (_cam != null && _rt != null)
            _cam.Render();
    }

    void OnDestroy()
    {
        Clear();
        if (_previewWorldRoot != null)
        {
            if (Application.isPlaying)
                Destroy(_previewWorldRoot.gameObject);
            else
                DestroyImmediate(_previewWorldRoot.gameObject);
            _previewWorldRoot = null;
        }
    }

    void BuildView(Transform parent, Font font)
    {
        var viewGo = new GameObject("CarouselView", typeof(RectTransform));
        viewGo.transform.SetParent(parent, false);
        _viewRt = viewGo.GetComponent<RectTransform>();
        _viewRt.anchorMin = new Vector2(0.16f, 0.28f);
        _viewRt.anchorMax = new Vector2(0.84f, 0.80f);
        _viewRt.offsetMin = Vector2.zero;
        _viewRt.offsetMax = Vector2.zero;

        _view = viewGo.AddComponent<RawImage>();
        _view.color = Color.white;
        _view.raycastTarget = true;
        SyncRenderTargetToView();

        var pointer = viewGo.AddComponent<CarouselPointerRelay>();
        pointer.Bind(this);

        BuildInfoCard(parent, font);
    }

    void BuildInfoCard(Transform parent, Font font)
    {
        _infoCard = new GameObject("SelectionInfoCard", typeof(RectTransform));
        _infoCard.transform.SetParent(parent, false);
        var cardRt = _infoCard.GetComponent<RectTransform>();
        cardRt.anchorMin = new Vector2(0.5f, 0.90f);
        cardRt.anchorMax = new Vector2(0.5f, 0.90f);
        cardRt.pivot = new Vector2(0.5f, 0.5f);
        cardRt.sizeDelta = new Vector2(560f, 100f);

        var bg = _infoCard.AddComponent<Image>();
        StargraveHudStyle.ApplyCard(bg, StargraveHudStyle.CardFill);

        var layout = _infoCard.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(18, 18, 12, 12);
        layout.spacing = 4f;
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        var nameGo = new GameObject("SelectedName", typeof(RectTransform));
        nameGo.transform.SetParent(_infoCard.transform, false);
        var nameLe = nameGo.AddComponent<LayoutElement>();
        nameLe.preferredHeight = 34f;
        nameLe.minHeight = 34f;
        _nameText = nameGo.AddComponent<Text>();
        _nameText.font = font;
        _nameText.fontSize = 26;
        _nameText.fontStyle = FontStyle.Bold;
        _nameText.alignment = TextAnchor.MiddleCenter;
        _nameText.raycastTarget = false;
        _nameText.color = StargraveHudStyle.Cream;
        var nameOutline = nameGo.AddComponent<Outline>();
        nameOutline.effectColor = StargraveHudStyle.CardOutline;
        nameOutline.effectDistance = new Vector2(1.25f, -1.25f);

        var statsGo = new GameObject("SelectedStats", typeof(RectTransform));
        statsGo.transform.SetParent(_infoCard.transform, false);
        var statsLe = statsGo.AddComponent<LayoutElement>();
        statsLe.preferredHeight = 36f;
        statsLe.minHeight = 28f;
        statsLe.flexibleHeight = 1f;
        _statsText = statsGo.AddComponent<Text>();
        _statsText.font = font;
        _statsText.fontSize = 15;
        _statsText.fontStyle = FontStyle.Bold;
        _statsText.alignment = TextAnchor.MiddleCenter;
        _statsText.horizontalOverflow = HorizontalWrapMode.Wrap;
        _statsText.verticalOverflow = VerticalWrapMode.Overflow;
        _statsText.raycastTarget = false;
        _statsText.color = new Color(
            StargraveHudStyle.Cream.r,
            StargraveHudStyle.Cream.g,
            StargraveHudStyle.Cream.b,
            0.88f);
    }

    void BuildChevrons(Transform parent, Font font)
    {
        _leftChevron = CreateChevron(parent, "CarouselLeft", "<", new Vector2(0.07f, 0.54f), font, -1);
        _rightChevron = CreateChevron(parent, "CarouselRight", ">", new Vector2(0.93f, 0.54f), font, 1);
        _leftChevron.transform.SetAsLastSibling();
        _rightChevron.transform.SetAsLastSibling();
    }

    GameObject CreateChevron(Transform parent, string name, string label, Vector2 anchor, Font font, int dir)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(72f, 78f);

        var image = go.AddComponent<Image>();
        var textGo = new GameObject("Label", typeof(RectTransform));
        textGo.transform.SetParent(go.transform, false);
        var textRt = textGo.GetComponent<RectTransform>();
        textRt.anchorMin = Vector2.zero;
        textRt.anchorMax = Vector2.one;
        textRt.offsetMin = Vector2.zero;
        textRt.offsetMax = Vector2.zero;
        var text = textGo.AddComponent<Text>();
        text.font = font;
        text.fontSize = 40;
        text.alignment = TextAnchor.MiddleCenter;
        text.text = label;
        text.raycastTarget = false;

        StargraveHudStyle.ApplyMenuButton(
            image,
            null,
            text,
            new Color(0.32f, 0.36f, 0.48f, 1f));
        image.raycastTarget = true;

        var click = go.AddComponent<ChevronRelay>();
        click.Bind(this, dir);
        go.AddComponent<FrontendButtonScaleFx>();
        return go;
    }

    Slot CreateSlot(PlayableCharacterDef def, float homeAngle)
    {
        var mountGo = new GameObject($"Char_{def.id}");
        mountGo.transform.SetParent(_carouselRoot, false);
        float rad = homeAngle * Mathf.Deg2Rad;
        Vector3 outward = new Vector3(Mathf.Sin(rad), 0f, -Mathf.Cos(rad));
        mountGo.transform.localPosition = outward * RingRadius;
        mountGo.transform.localRotation = Quaternion.LookRotation(outward, Vector3.up);
        SetLayerRecursively(mountGo, PreviewLayer);

        GameObject model = SpawnModel(def, mountGo.transform);
        if (def != null)
            model.transform.localRotation = Quaternion.Euler(0f, def.modelYawOffsetDegrees, 0f);

        return new Slot
        {
            id = def.id,
            mount = mountGo.transform,
            modelInstance = model,
            def = def,
            homeAngle = homeAngle
        };
    }

    void BuildCamera()
    {
        var camGo = new GameObject("CharSelectCarouselCam");
        camGo.transform.SetParent(_previewWorldRoot, false);
        Vector3 lookTarget = _previewWorldRoot.TransformPoint(new Vector3(0f, CamLookHeight, -RingRadius));
        Vector3 camPos = _previewWorldRoot.TransformPoint(new Vector3(0f, CamHeight, -RingRadius - CamDistance));
        camGo.transform.position = camPos;
        camGo.transform.rotation = Quaternion.LookRotation(lookTarget - camPos, Vector3.up);

        _cam = camGo.AddComponent<Camera>();
        _cam.clearFlags = CameraClearFlags.SolidColor;
        _cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
        _cam.orthographic = false;
        _cam.fieldOfView = CamFov;
        _cam.nearClipPlane = 0.08f;
        _cam.farClipPlane = 18f;
        _cam.cullingMask = _previewLayerMask;
        _cam.depth = -100;
        _cam.enabled = false;
        _cam.allowHDR = false;
        _cam.allowMSAA = false;
        _cam.useOcclusionCulling = false;
        SyncRenderTargetToView();

        var lightGo = new GameObject("Key");
        lightGo.transform.SetParent(camGo.transform, false);
        lightGo.transform.localRotation = Quaternion.Euler(18f, 22f, 0f);
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.45f;
        light.color = Color.white;
        light.cullingMask = _previewLayerMask;

        var fillGo = new GameObject("Fill");
        fillGo.transform.SetParent(camGo.transform, false);
        fillGo.transform.localRotation = Quaternion.Euler(8f, -48f, 0f);
        var fill = fillGo.AddComponent<Light>();
        fill.type = LightType.Directional;
        fill.intensity = 0.42f;
        fill.color = new Color(0.7f, 0.8f, 1f, 1f);
        fill.cullingMask = _previewLayerMask;
    }

    // Camera aspect follows the RT. Stretching a fixed 16:9 RT into the wide carousel
    // rect made models look fat; match RT pixels to the on-screen view instead.
    void SyncRenderTargetToView()
    {
        int w = 1280;
        int h = 720;
        if (_viewRt != null)
        {
            Rect r = _viewRt.rect;
            float sx = Mathf.Abs(_viewRt.lossyScale.x);
            float sy = Mathf.Abs(_viewRt.lossyScale.y);
            int pw = Mathf.RoundToInt(r.width * sx);
            int ph = Mathf.RoundToInt(r.height * sy);
            if (pw >= RtMinEdge && ph >= RtMinEdge)
            {
                w = pw;
                h = ph;
            }
        }

        int maxEdge = Mathf.Max(w, h);
        if (maxEdge > RtMaxEdge)
        {
            float s = RtMaxEdge / (float)maxEdge;
            w = Mathf.Max(2, Mathf.RoundToInt(w * s));
            h = Mathf.Max(2, Mathf.RoundToInt(h * s));
        }

        w &= ~1;
        h &= ~1;
        if (w < 2)
            w = 2;
        if (h < 2)
            h = 2;

        if (_rt != null && _rt.IsCreated() && _rt.width == w && _rt.height == h)
        {
            BindRenderTarget();
            return;
        }

        if (_rt != null)
        {
            if (_cam != null && _cam.targetTexture == _rt)
                _cam.targetTexture = null;
            if (_view != null && _view.texture == _rt)
                _view.texture = null;
            _rt.Release();
            Destroy(_rt);
            _rt = null;
        }

        _rt = new RenderTexture(w, h, 16, RenderTextureFormat.ARGB32)
        {
            antiAliasing = 2,
            name = "CharSelect_Carousel"
        };
        _rt.Create();
        BindRenderTarget();
    }

    void BindRenderTarget()
    {
        if (_rt == null)
            return;
        if (_view != null)
            _view.texture = _rt;
        if (_cam != null)
        {
            _cam.targetTexture = _rt;
            _cam.aspect = (float)_rt.width / (float)Mathf.Max(1, _rt.height);
        }
    }

    GameObject SpawnModel(PlayableCharacterDef def, Transform parent)
    {
        GameObject prefab = def != null ? def.characterPrefab : null;
        GameObject instance;
        if (prefab != null)
        {
            instance = Instantiate(prefab, parent);
            instance.name = prefab.name + "_SelectPreview";
            StripNonVisual(instance);
        }
        else
        {
            instance = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            instance.transform.SetParent(parent, false);
            var col = instance.GetComponent<Collider>();
            if (col != null)
                Destroy(col);
        }

        FitModelInView(instance.transform);
        SetLayerRecursively(instance, PreviewLayer);
        TryPlayIdle(instance, def);
        return instance;
    }

    static void TryPlayIdle(GameObject root, PlayableCharacterDef def)
    {
        if (root == null)
            return;
        var animators = root.GetComponentsInChildren<Animator>(true);
        for (int i = 0; i < animators.Length; i++)
        {
            Animator a = animators[i];
            if (a == null)
                continue;
            a.enabled = true;
            a.applyRootMotion = false;
            a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            a.updateMode = AnimatorUpdateMode.UnscaledTime;
            if (def != null && !string.IsNullOrEmpty(def.idleStateName) && a.runtimeAnimatorController != null)
                a.Play(def.idleStateName, 0, Random.Range(0f, 0.85f));
        }
    }

    static void StripNonVisual(GameObject root)
    {
        StripByName(root, "HeldBlaster");
        StripByName(root, "Muzzle_Bone");
        StripByName(root, "GunMuzzle");
        StripByName(root, "GunMuzzle_Runtime");

        var cols = root.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < cols.Length; i++)
            Object.Destroy(cols[i]);

        var audios = root.GetComponentsInChildren<AudioSource>(true);
        for (int i = 0; i < audios.Length; i++)
            Object.Destroy(audios[i]);

        var lights = root.GetComponentsInChildren<Light>(true);
        for (int i = 0; i < lights.Length; i++)
            Object.Destroy(lights[i]);
    }

    static void StripByName(GameObject root, string exactName)
    {
        if (root == null || string.IsNullOrEmpty(exactName))
            return;
        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        for (int i = all.Length - 1; i >= 0; i--)
        {
            Transform t = all[i];
            if (t != null && t.name == exactName && t.gameObject != root)
                Object.Destroy(t.gameObject);
        }
    }

    static void FitModelInView(Transform root)
    {
        root.localPosition = Vector3.zero;
        root.localRotation = Quaternion.identity;
        root.localScale = Vector3.one;

        Bounds world = ComputeWorldBodyBounds(root.gameObject);
        float height = Mathf.Max(world.size.y, 0.01f);

        if (height > 0.05f)
        {
            float scale = TargetModelHeight / height;
            if (scale < 0.002f)
                scale = 0.002f;
            if (scale > 8f)
                scale = 8f;
            root.localScale = Vector3.one * scale;
            world = ComputeWorldBodyBounds(root.gameObject);
        }

        Vector3 localCenter = root.InverseTransformPoint(world.center);
        float feetY = root.InverseTransformPoint(new Vector3(world.center.x, world.min.y, world.center.z)).y;
        root.localPosition = new Vector3(-localCenter.x, -feetY, -localCenter.z);
    }

    static Bounds ComputeWorldBodyBounds(GameObject root)
    {
        bool any = false;
        Bounds b = new Bounds(root.transform.position, Vector3.zero);

        var smrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int i = 0; i < smrs.Length; i++)
        {
            SkinnedMeshRenderer smr = smrs[i];
            if (smr == null || smr.sharedMesh == null)
                continue;
            if (IsPropTransform(smr.transform))
                continue;
            smr.updateWhenOffscreen = true;
            Bounds rb = smr.bounds;
            if (rb.size.sqrMagnitude < 1e-8f)
                continue;
            if (!any)
            {
                b = rb;
                any = true;
            }
            else
                b.Encapsulate(rb);
        }

        if (!any)
        {
            var rends = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rends.Length; i++)
            {
                if (rends[i] == null || IsPropTransform(rends[i].transform))
                    continue;
                if (!any)
                {
                    b = rends[i].bounds;
                    any = true;
                }
                else
                    b.Encapsulate(rends[i].bounds);
            }
        }

        if (!any)
            return new Bounds(root.transform.position, Vector3.one);
        return b;
    }

    static bool IsPropTransform(Transform t)
    {
        for (Transform p = t; p != null; p = p.parent)
        {
            string n = p.name;
            if (n == "HeldBlaster" || n == "Muzzle_Bone" || n == "GunMuzzle" || n == "GunMuzzle_Runtime")
                return true;
        }
        return false;
    }

    void HandleRotateInput()
    {
        if (_dragging || _slots.Count < 2)
            return;

        int pressed = ReadStepPressed();
        int held = ReadStepHeld();

        if (pressed != 0)
        {
            Step(pressed);
            _holdDir = pressed;
            _holdTimer = HoldRepeatDelay;
            return;
        }

        if (held != 0 && held == _holdDir)
        {
            _holdTimer -= Time.unscaledDeltaTime;
            if (_holdTimer <= 0f)
            {
                Step(held);
                _holdTimer = HoldRepeatRate;
            }
        }
        else
        {
            _holdDir = held;
            _holdTimer = HoldRepeatDelay;
        }
    }

    static int ReadStepPressed()
    {
        Keyboard kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.aKey.wasPressedThisFrame || kb.leftArrowKey.wasPressedThisFrame)
                return -1;
            if (kb.dKey.wasPressedThisFrame || kb.rightArrowKey.wasPressedThisFrame)
                return 1;
        }

        Gamepad gp = Gamepad.current;
        if (gp != null)
        {
            if (gp.dpad.left.wasPressedThisFrame)
                return -1;
            if (gp.dpad.right.wasPressedThisFrame)
                return 1;
        }
        return 0;
    }

    static int ReadStepHeld()
    {
        Keyboard kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed)
                return -1;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed)
                return 1;
        }

        Gamepad gp = Gamepad.current;
        if (gp != null)
        {
            if (gp.dpad.left.isPressed || gp.leftStick.ReadValue().x < -0.55f)
                return -1;
            if (gp.dpad.right.isPressed || gp.leftStick.ReadValue().x > 0.55f)
                return 1;
        }
        return 0;
    }

    internal void Step(int dir)
    {
        if (dir == 0 || _slots.Count < 2)
            return;
        int n = _slots.Count;
        int next = (_selectedIndex + dir) % n;
        if (next < 0)
            next += n;
        RotateToIndex(next);
    }

    void RotateToIndex(int index)
    {
        if (index < 0 || index >= _slots.Count)
            return;
        float step = 360f / _slots.Count;
        // Mount homeAngle is +index*step; yaw must match so that slot sits at camera-front.
        float desired = index * step;
        _targetYaw = _currentYaw + Mathf.DeltaAngle(_currentYaw, desired);
        _yawVel = 0f;
        // Selection + label follow the front character in LateUpdate as the ring turns.
    }

    int FindNearestIndex(float yaw)
    {
        int best = 0;
        float bestAbs = 999f;
        float step = 360f / _slots.Count;
        for (int i = 0; i < _slots.Count; i++)
        {
            float d = Mathf.Abs(Mathf.DeltaAngle(yaw, i * step));
            if (d < bestAbs)
            {
                bestAbs = d;
                best = i;
            }
        }
        return best;
    }

    void CommitSelection(string id, bool invoke)
    {
        if (string.IsNullOrEmpty(id) || id == _selectedId)
        {
            RefreshLabels();
            return;
        }
        _selectedId = id;
        PlayableCharacterCatalog.SetSelectedId(id);
        RefreshLabels();
        if (invoke)
            SelectionChanged?.Invoke(id);
    }

    void RefreshLabels()
    {
        PlayableCharacterDef def = null;
        if (_selectedIndex >= 0 && _selectedIndex < _slots.Count)
            def = _slots[_selectedIndex].def;
        if (def == null)
            def = PlayableCharacterCatalog.FindById(_selectedId);

        if (_nameText != null)
        {
            _nameText.text = def != null ? def.displayName : "";
            _nameText.color = def != null
                ? Color.Lerp(def.accentColor, StargraveHudStyle.Cream, 0.55f)
                : StargraveHudStyle.Cream;
        }
        if (_statsText != null)
            _statsText.text = def != null ? def.StatsSummaryLine() : "";

        if (_infoCard != null)
        {
            var bg = _infoCard.GetComponent<Image>();
            if (bg != null)
            {
                Color accent = def != null ? def.accentColor : StargraveHudStyle.CardFill;
                StargraveHudStyle.ApplyCard(bg, StargraveHudStyle.MenuButtonFill(accent));
            }
        }
    }

    internal void OnViewPointerDown(PointerEventData eventData)
    {
        _dragMoved = false;
    }

    internal void OnViewBeginDrag(PointerEventData eventData)
    {
        _dragging = true;
        _dragMoved = true;
        _yawVel = 0f;
    }

    internal void OnViewDrag(PointerEventData eventData)
    {
        if (!_dragging)
            return;
        _targetYaw -= eventData.delta.x * DragDegreesPerPixel;
        _currentYaw = _targetYaw;
    }

    internal void OnViewEndDrag(PointerEventData eventData)
    {
        if (!_dragging)
            return;
        _dragging = false;
        RotateToIndex(FindNearestIndex(_currentYaw));
    }

    internal void OnViewClick(PointerEventData eventData)
    {
        if (_dragMoved)
            return;
        TryClickSelect(eventData);
    }

    internal void OnViewScroll(PointerEventData eventData)
    {
        if (eventData.scrollDelta.y > 0.1f)
            Step(-1);
        else if (eventData.scrollDelta.y < -0.1f)
            Step(1);
    }

    void TryClickSelect(PointerEventData eventData)
    {
        if (_cam == null || _viewRt == null || _slots.Count == 0)
            return;

        Camera eventCam = eventData.pressEventCamera;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_viewRt, eventData.position, eventCam, out Vector2 local))
            return;

        Rect rect = _viewRt.rect;
        float u = (local.x - rect.xMin) / Mathf.Max(1f, rect.width);
        float v = (local.y - rect.yMin) / Mathf.Max(1f, rect.height);
        if (u < 0f || u > 1f || v < 0f || v > 1f)
            return;

        int best = -1;
        float bestScore = 0.2f;
        for (int i = 0; i < _slots.Count; i++)
        {
            if (_slots[i].mount == null)
                continue;
            Vector3 vp = _cam.WorldToViewportPoint(_slots[i].mount.position + Vector3.up * 0.85f);
            if (vp.z <= 0.05f)
                continue;
            float d = Vector2.Distance(new Vector2(vp.x, vp.y), new Vector2(u, v));
            if (d < bestScore)
            {
                bestScore = d;
                best = i;
            }
        }
        if (best >= 0)
        {
            RotateToIndex(best);
            return;
        }

        if (u < 0.38f)
            Step(-1);
        else if (u > 0.62f)
            Step(1);
    }

    void Clear()
    {
        for (int i = 0; i < _slots.Count; i++)
        {
            Slot s = _slots[i];
            if (s.modelInstance != null)
                Destroy(s.modelInstance);
            if (s.mount != null)
                Destroy(s.mount.gameObject);
        }
        _slots.Clear();

        if (_cam != null)
        {
            Destroy(_cam.gameObject);
            _cam = null;
        }
        if (_carouselRoot != null)
        {
            Destroy(_carouselRoot.gameObject);
            _carouselRoot = null;
        }
        if (_rt != null)
        {
            _rt.Release();
            Destroy(_rt);
            _rt = null;
        }
        if (_view != null)
        {
            Destroy(_view.gameObject);
            _view = null;
            _viewRt = null;
        }
        if (_infoCard != null)
        {
            Destroy(_infoCard);
            _infoCard = null;
            _nameText = null;
            _statsText = null;
        }
        if (_leftChevron != null)
        {
            Destroy(_leftChevron);
            _leftChevron = null;
        }
        if (_rightChevron != null)
        {
            Destroy(_rightChevron);
            _rightChevron = null;
        }
    }

    void EnsurePreviewWorldRoot()
    {
        if (_previewWorldRoot != null)
            return;
        var go = new GameObject("CharacterSelect_PreviewWorld");
        DontDestroyOnLoad(go);
        go.transform.position = new Vector3(40f, -2800f, 0f);
        _previewWorldRoot = go.transform;
    }

    static void StripPreviewLayerFromSceneCameras()
    {
        if (_strippedMainCameras)
            return;
        _strippedMainCameras = true;
        Camera[] cams = FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        int maskBit = 1 << PreviewLayer;
        for (int i = 0; i < cams.Length; i++)
        {
            Camera c = cams[i];
            if (c == null || c.targetTexture != null)
                continue;
            c.cullingMask &= ~maskBit;
        }
    }

    static Font BuiltinFont()
    {
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null)
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return font;
    }

    static void SetLayerRecursively(GameObject go, int layer)
    {
        if (go == null)
            return;
        go.layer = layer;
        Transform t = go.transform;
        for (int i = 0; i < t.childCount; i++)
            SetLayerRecursively(t.GetChild(i).gameObject, layer);
    }

    sealed class CarouselPointerRelay : MonoBehaviour, IPointerDownHandler, IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerClickHandler, IScrollHandler
    {
        CharacterSelect3DPanel _panel;

        public void Bind(CharacterSelect3DPanel panel) => _panel = panel;

        public void OnPointerDown(PointerEventData eventData) => _panel?.OnViewPointerDown(eventData);
        public void OnBeginDrag(PointerEventData eventData) => _panel?.OnViewBeginDrag(eventData);
        public void OnDrag(PointerEventData eventData) => _panel?.OnViewDrag(eventData);
        public void OnEndDrag(PointerEventData eventData) => _panel?.OnViewEndDrag(eventData);
        public void OnPointerClick(PointerEventData eventData) => _panel?.OnViewClick(eventData);
        public void OnScroll(PointerEventData eventData) => _panel?.OnViewScroll(eventData);
    }

    sealed class ChevronRelay : MonoBehaviour, IPointerClickHandler
    {
        CharacterSelect3DPanel _panel;
        int _dir;

        public void Bind(CharacterSelect3DPanel panel, int dir)
        {
            _panel = panel;
            _dir = dir;
        }

        public void OnPointerClick(PointerEventData eventData) => _panel?.Step(_dir);
    }
}
