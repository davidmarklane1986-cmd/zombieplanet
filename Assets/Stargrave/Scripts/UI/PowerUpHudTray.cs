using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Bottom HUD tray: spinning 3D previews. Weapon stays on the right; extra power-ups append left.
/// </summary>
public sealed class PowerUpHudTray : MonoBehaviour
{
    public const string WeaponSlotId = "Hud_CurrentWeapon";

    const int PreviewLayer = 5; // Unity built-in "UI" layer
    const int RtSize = 256;
    const float SpinDegreesPerSecond = 72f;
    static readonly Vector3 PreviewTiltEuler = new Vector3(0f, 0f, -25f);
    const float SlotSize = 112f;

    [SerializeField] RectTransform _slotRoot;
    [SerializeField] float _slotSpacing = 10f;

    readonly List<Slot> _slots = new();
    Transform _previewWorldRoot;
    int _previewLayerMask;
    static bool _strippedMainCameras;
    string _weaponVisualKey;
    int _previewLane;
    RectTransform _statusSlot;

    struct Slot
    {
        public string buffId;
        public RectTransform root;
        public RawImage image;
        public Text timerText;
        public Text nameText;
        public RenderTexture rt;
        public Camera cam;
        public Transform modelPivot;
        public GameObject modelInstance;
    }

    public void EnsureBuilt(Transform parent, Font font)
    {
        if (_slotRoot != null)
            return;

        _slotRoot = parent as RectTransform;
        if (_slotRoot == null)
        {
            var go = new GameObject("PowerUpTray", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            _slotRoot = go.GetComponent<RectTransform>();
            _slotRoot.anchorMin = new Vector2(1f, 0.5f);
            _slotRoot.anchorMax = new Vector2(1f, 0.5f);
            _slotRoot.pivot = new Vector2(1f, 0.5f);
            _slotRoot.anchoredPosition = Vector2.zero;
            _slotRoot.sizeDelta = new Vector2(620f, 124f);
        }

        if (_slotRoot.GetComponent<HorizontalLayoutGroup>() == null)
        {
            var layout = _slotRoot.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleRight;
            layout.spacing = _slotSpacing;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.padding = new RectOffset(4, 4, 4, 4);
        }

        EnsurePreviewWorldRoot();
        _previewLayerMask = 1 << PreviewLayer;
        StripPreviewLayerFromSceneCameras();
    }

    public void BindStatusSlot(RectTransform status)
    {
        _statusSlot = status;
    }

    void OnDestroy()
    {
        ClearSlots();
        if (_previewWorldRoot != null)
        {
            if (Application.isPlaying)
                Destroy(_previewWorldRoot.gameObject);
            else
                DestroyImmediate(_previewWorldRoot.gameObject);
        }
    }

    void LateUpdate()
    {
        for (int i = 0; i < _slots.Count; i++)
        {
            Slot s = _slots[i];
            if (s.modelPivot != null)
                s.modelPivot.Rotate(Vector3.up, SpinDegreesPerSecond * Time.unscaledDeltaTime, Space.Self);
            if (s.cam != null && s.rt != null && s.modelInstance != null)
                s.cam.Render();
        }
    }

    public void SyncFromBuffs(
        PlayerBuffController.BuffStatus[] statuses,
        Font font,
        int storedHealthPacks = 0,
        WeaponDef equippedWeapon = null,
        int ammoCount = -1)
    {
        if (_slotRoot == null)
            return;

        EnsureWeaponSlot(equippedWeapon, ammoCount, font);

        statuses ??= System.Array.Empty<PlayerBuffController.BuffStatus>();

        // Merge stocked health packs into the tray as a count slot.
        var merged = new List<PlayerBuffController.BuffStatus>(statuses.Length + 1);
        for (int i = 0; i < statuses.Length; i++)
            merged.Add(statuses[i]);
        if (storedHealthPacks > 0)
        {
            merged.Add(new PlayerBuffController.BuffStatus
            {
                buffId = "PowerUp_HealthStock",
                displayName = "Health",
                remainingSeconds = storedHealthPacks,
                drainWhileUsed = false
            });
        }

        statuses = merged.ToArray();

        // Remove slots for buffs that expired (never remove the weapon slot).
        for (int i = _slots.Count - 1; i >= 0; i--)
        {
            if (_slots[i].buffId == WeaponSlotId)
                continue;

            bool stillActive = false;
            for (int j = 0; j < statuses.Length; j++)
            {
                if (statuses[j].buffId == _slots[i].buffId)
                {
                    stillActive = true;
                    break;
                }
            }

            if (!stillActive)
            {
                DestroySlot(_slots[i]);
                _slots.RemoveAt(i);
            }
        }

        // Add / refresh buffs.
        for (int i = 0; i < statuses.Length; i++)
        {
            PlayerBuffController.BuffStatus status = statuses[i];
            int slotIndex = FindSlotIndex(status.buffId);
            if (slotIndex < 0)
            {
                Slot created = CreateSlot(status.buffId, AccentForBuff(status.buffId), font);
                AttachBuffPreview(ref created, status.buffId);
                _slots.Add(created);
                slotIndex = _slots.Count - 1;
            }

            Slot slot = _slots[slotIndex];
            int value = Mathf.Max(0, Mathf.CeilToInt(status.remainingSeconds));
            if (slot.timerText != null)
                slot.timerText.text = value.ToString();
            if (slot.nameText != null)
                slot.nameText.gameObject.SetActive(false);
            _slots[slotIndex] = slot;
        }

        RestackSlots(statuses);
    }

    void RestackSlots(PlayerBuffController.BuffStatus[] statuses)
    {
        // Left → right: power-ups, stats card, weapon (far right).
        int insert = 0;
        for (int i = 0; i < statuses.Length; i++)
        {
            int idx = FindSlotIndex(statuses[i].buffId);
            if (idx >= 0 && _slots[idx].root != null)
                _slots[idx].root.SetSiblingIndex(insert++);
        }

        if (_statusSlot != null)
            _statusSlot.SetSiblingIndex(insert++);

        int weaponIdx = FindSlotIndex(WeaponSlotId);
        if (weaponIdx >= 0 && _slots[weaponIdx].root != null)
            _slots[weaponIdx].root.SetSiblingIndex(insert);
    }

    void EnsureWeaponSlot(WeaponDef weapon, int ammoCount, Font font)
    {
        int idx = FindSlotIndex(WeaponSlotId);
        if (idx < 0)
        {
            Slot created = CreateSlot(WeaponSlotId, new Color(1f, 0.82f, 0.35f, 1f), font);
            _slots.Insert(0, created);
            idx = 0;
        }

        Slot slot = _slots[idx];
        string visualKey = WeaponVisualKey(weapon);
        if (!string.Equals(_weaponVisualKey, visualKey, System.StringComparison.Ordinal))
        {
            if (slot.modelInstance != null)
            {
                Destroy(slot.modelInstance);
                slot.modelInstance = null;
            }

            if (slot.modelPivot != null)
                slot.modelInstance = SpawnWeaponPreview(weapon, slot.modelPivot);
            _weaponVisualKey = visualKey;
        }

        if (slot.timerText != null)
            slot.timerText.text = ammoCount >= 0 ? ammoCount.ToString() : "—";
        if (slot.nameText != null)
            slot.nameText.gameObject.SetActive(false);
        _slots[idx] = slot;
    }

    static string WeaponVisualKey(WeaponDef weapon)
    {
        if (weapon == null)
            return "none";
        if (!string.IsNullOrEmpty(weapon.id))
            return weapon.id;
        return weapon.name;
    }

    int FindSlotIndex(string buffId)
    {
        for (int i = 0; i < _slots.Count; i++)
        {
            if (_slots[i].buffId == buffId)
                return i;
        }
        return -1;
    }

    Slot CreateSlot(string slotId, Color accent, Font font)
    {
        EnsurePreviewWorldRoot();
        const float size = SlotSize;

        var slotGo = new GameObject($"Slot_{slotId}", typeof(RectTransform));
        slotGo.transform.SetParent(_slotRoot, false);
        var rt = slotGo.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(size, size);

        var le = slotGo.AddComponent<LayoutElement>();
        le.preferredWidth = size;
        le.preferredHeight = size;
        le.minWidth = size;
        le.minHeight = size;
        le.flexibleWidth = 0f;
        le.flexibleHeight = 0f;

        var bg = slotGo.AddComponent<Image>();
        StargraveHudStyle.ApplyCard(bg, StargraveHudStyle.CardFillWeapon);

        var pipGo = new GameObject("Accent", typeof(RectTransform));
        pipGo.transform.SetParent(slotGo.transform, false);
        var pipRt = pipGo.GetComponent<RectTransform>();
        pipRt.anchorMin = new Vector2(0.18f, 1f);
        pipRt.anchorMax = new Vector2(0.82f, 1f);
        pipRt.pivot = new Vector2(0.5f, 1f);
        pipRt.anchoredPosition = new Vector2(0f, -8f);
        pipRt.sizeDelta = new Vector2(0f, 5f);
        var pip = pipGo.AddComponent<Image>();
        pip.sprite = StargraveHudStyle.CardSprite();
        pip.type = Image.Type.Sliced;
        pip.color = new Color(accent.r, accent.g, accent.b, 0.85f);
        pip.raycastTarget = false;

        var rawGo = new GameObject("Preview", typeof(RectTransform));
        rawGo.transform.SetParent(slotGo.transform, false);
        var rawRt = rawGo.GetComponent<RectTransform>();
        rawRt.anchorMin = new Vector2(0.5f, 0.5f);
        rawRt.anchorMax = new Vector2(0.5f, 0.5f);
        rawRt.pivot = new Vector2(0.5f, 0.5f);
        rawRt.anchoredPosition = new Vector2(0f, 6f);
        rawRt.sizeDelta = new Vector2(size - 20f, size - 28f);
        var raw = rawGo.AddComponent<RawImage>();
        raw.raycastTarget = false;
        raw.color = Color.white;

        Text nameText = null;

        var timerGo = new GameObject("Timer", typeof(RectTransform));
        timerGo.transform.SetParent(slotGo.transform, false);
        var timerRt = timerGo.GetComponent<RectTransform>();
        timerRt.anchorMin = new Vector2(0f, 0f);
        timerRt.anchorMax = new Vector2(1f, 0f);
        timerRt.pivot = new Vector2(0.5f, 0f);
        timerRt.anchoredPosition = new Vector2(0f, 6f);
        timerRt.sizeDelta = new Vector2(0f, 18f);
        var timerText = timerGo.AddComponent<Text>();
        timerText.font = font;
        timerText.fontSize = 16;
        timerText.fontStyle = FontStyle.Bold;
        timerText.alignment = TextAnchor.MiddleCenter;
        timerText.color = StargraveHudStyle.Cream;
        timerText.text = "0";
        timerText.raycastTarget = false;

        var renderTex = new RenderTexture(RtSize, RtSize, 16, RenderTextureFormat.ARGB32)
        {
            antiAliasing = 2,
            name = $"PowerUpPreview_{slotId}"
        };
        renderTex.Create();
        raw.texture = renderTex;

        int slotIndex = ++_previewLane;
        Vector3 basePos = new Vector3(slotIndex * 3f, 0f, 0f);

        var camGo = new GameObject($"PreviewCam_{slotId}");
        camGo.transform.SetParent(_previewWorldRoot, false);
        camGo.transform.localPosition = basePos + new Vector3(0f, 0.45f, -1.75f);
        camGo.transform.localRotation = Quaternion.LookRotation((basePos - (basePos + new Vector3(0f, 0.45f, -1.75f))).normalized, Vector3.up);
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.05f, 0.05f, 0.07f, 0f);
        cam.orthographic = true;
        cam.orthographicSize = 0.55f;
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 8f;
        cam.cullingMask = _previewLayerMask;
        cam.targetTexture = renderTex;
        cam.depth = -100;
        cam.enabled = false; // we call Render() manually
        cam.allowHDR = false;
        cam.allowMSAA = false;

        var lightGo = new GameObject("HudPreviewOnlyLight");
        lightGo.transform.SetParent(camGo.transform, false);
        lightGo.transform.localPosition = new Vector3(-0.4f, 0.8f, -0.5f);
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.35f;
        light.color = Color.white;
        light.cullingMask = _previewLayerMask;
        light.shadows = LightShadows.None;

        var pivotGo = new GameObject("ModelPivot");
        pivotGo.transform.SetParent(_previewWorldRoot, false);
        pivotGo.transform.localPosition = basePos;
        pivotGo.transform.localRotation = Quaternion.identity;
        SetLayerRecursively(pivotGo, PreviewLayer);

        return new Slot
        {
            buffId = slotId,
            root = rt,
            image = raw,
            timerText = timerText,
            nameText = nameText,
            rt = renderTex,
            cam = cam,
            modelPivot = pivotGo.transform,
            modelInstance = null
        };
    }

    void AttachBuffPreview(ref Slot slot, string buffId)
    {
        if (slot.modelPivot == null)
            return;
        slot.modelInstance = SpawnPreviewModel(buffId, slot.modelPivot);
    }

    GameObject SpawnWeaponPreview(WeaponDef weapon, Transform parent)
    {
        GameObject prefab = null;
        if (weapon != null)
        {
            if (weapon.heldVisualPrefab != null)
                prefab = weapon.heldVisualPrefab;
            else if (weapon.worldPickupPrefab != null)
                prefab = weapon.worldPickupPrefab;
        }

        GameObject instance;
        if (prefab != null)
        {
            instance = Instantiate(prefab, parent);
            instance.name = (weapon != null ? weapon.id : prefab.name) + "_HudPreview";
            StripGameplay(instance);
        }
        else
        {
            instance = GameObject.CreatePrimitive(PrimitiveType.Cube);
            instance.transform.SetParent(parent, false);
            instance.name = "FallbackWeaponCube";
            var col = instance.GetComponent<Collider>();
            if (col != null)
                Destroy(col);
            var r = instance.GetComponent<Renderer>();
            if (r != null)
                r.sharedMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"))
                {
                    color = new Color(1f, 0.75f, 0.3f, 1f)
                };
        }

        FitModelInView(instance.transform);
        // Ground WeaponPickup visual tilt — spin is on the upright pivot parent.
        Vector3 extra = weapon != null ? weapon.hudPreviewEulerDegrees : Vector3.zero;
        instance.transform.localRotation = Quaternion.Euler(extra) * Quaternion.Euler(PreviewTiltEuler);
        SetLayerRecursively(instance, PreviewLayer);
        return instance;
    }

    GameObject SpawnPreviewModel(string buffId, Transform parent)
    {
        string resourcePath = ResourcePathForBuff(buffId);
        GameObject prefab = string.IsNullOrEmpty(resourcePath) ? null : Resources.Load<GameObject>(resourcePath);
        GameObject instance;
        if (prefab != null)
        {
            instance = Instantiate(prefab, parent);
            instance.name = prefab.name + "_HudPreview";
            // Strip gameplay components so the tray never picks up / spins as a world item.
            StripGameplay(instance);
        }
        else
        {
            instance = GameObject.CreatePrimitive(PrimitiveType.Cube);
            instance.transform.SetParent(parent, false);
            instance.name = "FallbackCube";
            var col = instance.GetComponent<Collider>();
            if (col != null)
                Destroy(col);
            var r = instance.GetComponent<Renderer>();
            if (r != null)
                r.sharedMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"))
                {
                    color = AccentForBuff(buffId)
                };
        }

        FitModelInView(instance.transform);
        instance.transform.localRotation = Quaternion.Euler(PreviewTiltEuler);
        SetLayerRecursively(instance, PreviewLayer);
        return instance;
    }

    static void StripGameplay(GameObject root)
    {
        var pickups = root.GetComponentsInChildren<PowerUpPickup>(true);
        for (int i = 0; i < pickups.Length; i++)
            Object.Destroy(pickups[i]);

        var weapons = root.GetComponentsInChildren<WeaponPickup>(true);
        for (int i = 0; i < weapons.Length; i++)
            Object.Destroy(weapons[i]);

        var cols = root.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < cols.Length; i++)
            Object.Destroy(cols[i]);

        var audios = root.GetComponentsInChildren<AudioSource>(true);
        for (int i = 0; i < audios.Length; i++)
            Object.Destroy(audios[i]);

        var lights = root.GetComponentsInChildren<Light>(true);
        for (int i = 0; i < lights.Length; i++)
            Object.Destroy(lights[i]);

        // Remove runtime glow/halo cosmetics created by PowerUpPickup.Awake.
        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        for (int i = all.Length - 1; i >= 0; i--)
        {
            Transform t = all[i];
            if (t == null || t == root.transform)
                continue;
            string n = t.name;
            if (n.IndexOf("Halo", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("Glow", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Object.Destroy(t.gameObject);
            }
        }
    }

    static void FitModelInView(Transform root)
    {
        root.localPosition = Vector3.zero;
        root.localRotation = Quaternion.identity;
        root.localScale = Vector3.one;

        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        if (renderers == null || renderers.Length == 0)
        {
            root.localScale = Vector3.one * 0.35f;
            return;
        }

        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            if (renderers[i] != null)
                b.Encapsulate(renderers[i].bounds);
        }

        float maxExtent = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
        if (maxExtent < 1e-4f)
            maxExtent = 1f;
        float scale = 0.9f / maxExtent;
        root.localScale = Vector3.one * scale;

        // Re-measure after scale and center on the pivot.
        b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            if (renderers[i] != null)
                b.Encapsulate(renderers[i].bounds);
        }

        Vector3 worldCenter = b.center;
        Vector3 pivot = root.position;
        root.position += pivot - worldCenter;
    }

    static string ResourcePathForBuff(string buffId)
    {
        if (string.IsNullOrEmpty(buffId))
            return null;
        if (buffId.IndexOf("Rapid", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return "PowerUps/PowerUp_RapidFire";
        if (buffId.IndexOf("Speed", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return "PowerUps/PowerUp_Speed";
        if (buffId.IndexOf("Jump", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return "PowerUps/PowerUp_Speed";
        if (buffId.IndexOf("Damage", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return "PowerUps/PowerUp_RapidFire";
        if (buffId.IndexOf("Health", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return "PowerUps/PowerUp_Health";
        return "PowerUps/PowerUp_Speed";
    }

    static Color AccentForBuff(string buffId)
    {
        if (string.IsNullOrEmpty(buffId))
            return new Color(0.85f, 0.85f, 0.55f, 1f);
        if (buffId.IndexOf("Rapid", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return new Color(1f, 0.55f, 0.2f, 1f);
        if (buffId.IndexOf("Speed", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return new Color(0.35f, 0.85f, 1f, 1f);
        if (buffId.IndexOf("Jump", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return new Color(0.55f, 1f, 0.45f, 1f);
        if (buffId.IndexOf("Damage", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return new Color(1f, 0.35f, 0.35f, 1f);
        if (buffId.IndexOf("Health", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return new Color(0.4f, 1f, 0.45f, 1f);
        return new Color(0.9f, 0.85f, 0.4f, 1f);
    }

    void EnsurePreviewWorldRoot()
    {
        if (_previewWorldRoot != null)
            return;
        var go = new GameObject("PowerUpHud_PreviewWorld");
        DontDestroyOnLoad(go);
        go.transform.position = new Vector3(0f, -2500f, 0f);
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
            // Leave overlay/UI cameras alone if they only render UI already.
            c.cullingMask &= ~maskBit;
        }
    }

    void ClearSlots()
    {
        for (int i = 0; i < _slots.Count; i++)
            DestroySlot(_slots[i]);
        _slots.Clear();
        _weaponVisualKey = null;
    }

    void DestroySlot(Slot slot)
    {
        if (slot.modelInstance != null)
            Destroy(slot.modelInstance);
        if (slot.modelPivot != null)
            Destroy(slot.modelPivot.gameObject);
        if (slot.cam != null)
            Destroy(slot.cam.gameObject);
        if (slot.rt != null)
        {
            slot.rt.Release();
            Destroy(slot.rt);
        }
        if (slot.root != null)
            Destroy(slot.root.gameObject);
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
}
