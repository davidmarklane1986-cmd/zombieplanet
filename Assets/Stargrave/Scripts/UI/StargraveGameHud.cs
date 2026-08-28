using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Bottom-center cartoon HUD. Weapon and stats stay put; power-ups grow left from a locked origin.
/// </summary>
public sealed class StargraveGameHud : MonoBehaviour
{
    public static StargraveGameHud Instance { get; private set; }

    [Header("Optional references (filled at runtime if null)")]
    [SerializeField] Canvas _canvas;
    [SerializeField] Text _healthText;
    [SerializeField] Text _staminaText;
    [SerializeField] Text _killsText;
    [SerializeField] RectTransform _crosshairRoot;
    [SerializeField] Image[] _crosshairCoreImages;
    [SerializeField] Image[] _crosshairOutlineImages;
    [SerializeField] PowerUpHudTray _powerUpTray;
    [SerializeField] RectTransform _hudDock;
    [SerializeField] RectTransform _statsCard;

    PlayerHealth _health;
    PlayerSwimStamina _swimStamina;
    PlayerBuffController _buffs;
    Transform _playerTransform;
    Transform _planetTransform;
    Vector3 _lastPlayerPos;
    float _distanceTravelled;
    CanvasGroup _group;
    PlayerShooting _shooter;
    float _crosshairShotPulseUntil;
    float _crosshairHitFlashUntil;
    Font _font;

    public float CurrentDistanceTravelled => _distanceTravelled;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void TrySpawnForGameScene()
    {
        if (FindAnyObjectByType<StargraveGameHud>() != null)
            return;
        if (FindAnyObjectByType<ZombieSpawner>() == null)
            return;

        var go = new GameObject("Stargrave_GameHud");
        go.AddComponent<StargraveGameHud>();
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        if (_canvas == null)
            BuildRuntimeUi();
        PruneLegacyHudElements();
        if (_group == null)
            _group = gameObject.AddComponent<CanvasGroup>();
        SetHudVisible(false);
    }

    void OnDestroy()
    {
        GameStatsManager.ScoresChanged -= OnScoresChanged;
        PlayerShooting.ShotFired -= OnShotFired;
        PlayerShooting.HitConfirmed -= OnHitConfirmed;
        PlayerBuffController.PowerUpsChanged -= OnPowerUpsChanged;
        PlayerHealth.HealthPacksChanged -= OnHealthPacksChanged;
        if (Instance == this)
            Instance = null;
    }

    void OnEnable()
    {
        GameStatsManager.ScoresChanged += OnScoresChanged;
        PlayerShooting.ShotFired += OnShotFired;
        PlayerShooting.HitConfirmed += OnHitConfirmed;
        PlayerBuffController.PowerUpsChanged += OnPowerUpsChanged;
        PlayerHealth.HealthPacksChanged += OnHealthPacksChanged;
        ResolvePlayerHealth();
        if (GameStatsManager.Instance != null)
            OnScoresChanged(GameStatsManager.Instance.CurrentKills, GameStatsManager.Instance.HighKillScore);
        RefreshPowerUpTray();
    }

    void OnDisable()
    {
        GameStatsManager.ScoresChanged -= OnScoresChanged;
        PlayerShooting.ShotFired -= OnShotFired;
        PlayerShooting.HitConfirmed -= OnHitConfirmed;
        PlayerBuffController.PowerUpsChanged -= OnPowerUpsChanged;
        PlayerHealth.HealthPacksChanged -= OnHealthPacksChanged;
    }

    void OnHealthPacksChanged(int _)
    {
        RefreshPowerUpTray();
    }

    public void ResetRunStats()
    {
        _distanceTravelled = 0f;
        ResolvePlayerHealth();
        RefreshPowerUpTray();
    }

    void OnScoresChanged(int currentKills, int bestKills)
    {
        if (_killsText != null)
            _killsText.text = currentKills.ToString();
    }

    void OnPowerUpsChanged(string[] activePowerUps)
    {
        RefreshPowerUpTray();
    }

    void RefreshPowerUpTray()
    {
        if (_powerUpTray == null)
            return;
        if (_buffs == null && _playerTransform != null)
            _buffs = _playerTransform.GetComponent<PlayerBuffController>();
        if (_buffs == null)
            _buffs = FindFirstObjectByType<PlayerBuffController>(FindObjectsInactive.Include);

        PlayerBuffController.BuffStatus[] statuses = _buffs != null
            ? _buffs.GetActiveBuffStatuses()
            : System.Array.Empty<PlayerBuffController.BuffStatus>();
        int packs = _health != null ? _health.StoredHealthPacks : 0;

        WeaponDef equipped = null;
        int ammo = -1;
        PlayerWeaponController weapons = null;
        if (_health != null)
            weapons = _health.GetComponent<PlayerWeaponController>();
        if (weapons == null && _playerTransform != null)
            weapons = _playerTransform.GetComponent<PlayerWeaponController>();
        if (weapons != null)
        {
            equipped = weapons.EquippedWeapon;
            if (weapons.HasLootWeapon)
                ammo = weapons.LootAmmoRemaining;
            else
            {
                if (_shooter == null)
                    _shooter = FindFirstObjectByType<PlayerShooting>(FindObjectsInactive.Include);
                if (_shooter != null)
                    ammo = _shooter.ShotsRemainingInMagazine;
            }
        }

        _powerUpTray.SyncFromBuffs(
            statuses,
            _font != null ? _font : BuiltinUIFont(),
            packs,
            equipped,
            ammo);
    }

    void OnShotFired()
    {
        _crosshairShotPulseUntil = Time.unscaledTime + 0.08f;
    }

    void OnHitConfirmed()
    {
        _crosshairHitFlashUntil = Time.unscaledTime + 0.12f;
    }

    public void SetHudVisible(bool visible)
    {
        if (_group == null)
            _group = GetComponent<CanvasGroup>() ?? gameObject.AddComponent<CanvasGroup>();
        _group.alpha = visible ? 1f : 0f;
        _group.blocksRaycasts = visible;
        _group.interactable = visible;
    }

    void Update()
    {
        if (_health == null)
            ResolvePlayerHealth();

        if (_healthText != null && _health != null)
        {
            _healthText.text = _health.CurrentHealth.ToString();
            float t = _health.maxHealth > 0 ? (float)_health.CurrentHealth / _health.maxHealth : 0f;
            _healthText.color = Color.Lerp(StargraveHudStyle.Health, new Color(0.45f, 0.9f, 0.42f, 1f), t);
        }

        RefreshPowerUpTray();
        UpdateSwimStaminaHud();
        UpdateDistance();
        UpdateCrosshair();
    }

    void UpdateSwimStaminaHud()
    {
        if (_staminaText == null)
            return;

        if (_swimStamina == null && _health != null)
            _swimStamina = PlayerSwimStamina.EnsureOn(_health);

        if (_swimStamina == null)
        {
            _staminaText.text = "100";
            _staminaText.color = StargraveHudStyle.Swim;
            return;
        }

        float n = _swimStamina.Normalized;
        _staminaText.text = Mathf.RoundToInt(n * 100f).ToString();
        PlanetMotor_InputSystem motor = _playerTransform != null
            ? _playerTransform.GetComponent<PlanetMotor_InputSystem>()
            : null;
        bool swimming = motor != null && motor.IsSwimming;
        if (n <= 0.01f || _swimStamina.IsSprintLocked)
            _staminaText.color = StargraveHudStyle.Health;
        else if (!swimming && n > 0.98f)
            _staminaText.color = new Color(StargraveHudStyle.Swim.r, StargraveHudStyle.Swim.g, StargraveHudStyle.Swim.b, 0.55f);
        else
            _staminaText.color = StargraveHudStyle.Swim;
    }

    void ResolvePlayerHealth()
    {
        Transform t = RuntimeSceneRefs.GetPlayerTransform(0.05f);
        if (t == null)
            return;
        _health = t.GetComponent<PlayerHealth>();
        if (_health == null)
            _health = t.GetComponentInParent<PlayerHealth>();
        if (_health != null)
            _swimStamina = PlayerSwimStamina.EnsureOn(_health);
        _playerTransform = _health != null ? _health.transform : t;
        _buffs = _playerTransform != null ? _playerTransform.GetComponent<PlayerBuffController>() : null;
        _shooter = _playerTransform != null ? _playerTransform.GetComponent<PlayerShooting>() : null;
        if (_shooter == null && _playerTransform != null)
            _shooter = _playerTransform.GetComponentInParent<PlayerShooting>();
        if (_shooter == null)
            _shooter = FindFirstObjectByType<PlayerShooting>(FindObjectsInactive.Include);
        if (_buffs != null)
            OnPowerUpsChanged(_buffs.GetActivePowerUpNames());
        else
            OnPowerUpsChanged(null);
        if (_playerTransform != null)
            _lastPlayerPos = _playerTransform.position;
    }

    void UpdateDistance()
    {
        if (_playerTransform == null)
        {
            Transform t = RuntimeSceneRefs.GetPlayerTransform(0.05f);
            if (t == null)
                return;
            _playerTransform = t;
            _lastPlayerPos = _playerTransform.position;
        }

        Vector3 currentPos = _playerTransform.position;
        Vector3 frameDelta = currentPos - _lastPlayerPos;
        if (_planetTransform == null)
        {
            Planet planet = FindFirstObjectByType<Planet>(FindObjectsInactive.Exclude);
            if (planet != null)
                _planetTransform = planet.transform;
        }

        if (_planetTransform != null)
        {
            Vector3 radialOut = (currentPos - _planetTransform.position).normalized;
            Vector3 tangentialDelta = Vector3.ProjectOnPlane(frameDelta, radialOut);
            _distanceTravelled += tangentialDelta.magnitude;
        }
        else
        {
            _distanceTravelled += frameDelta.magnitude;
        }

        _lastPlayerPos = currentPos;
        if (GameStatsManager.Instance != null)
            GameStatsManager.Instance.ReportDistanceTravelled(_distanceTravelled);
    }

    void BuildRuntimeUi()
    {
        _canvas = gameObject.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 100;

        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        gameObject.AddComponent<GraphicRaycaster>();

        _font = BuiltinUIFont();
        _crosshairRoot = CreateCrosshair(transform);
        CacheCrosshairPieces();

        const float slotSize = 112f;
        const float spacing = 10f;
        const int lockedPowerUpSlots = 2;
        // Keep weapon + stats where they sit when 2 power-up cards are in the row.
        float lockedClusterWidth = lockedPowerUpSlots * slotSize + StatsCardWidth + slotSize
            + (lockedPowerUpSlots + 1) * spacing;

        var dockGo = new GameObject("HudDock", typeof(RectTransform));
        dockGo.transform.SetParent(transform, false);
        _hudDock = dockGo.GetComponent<RectTransform>();
        _hudDock.anchorMin = new Vector2(0.5f, 0f);
        _hudDock.anchorMax = new Vector2(0.5f, 0f);
        _hudDock.pivot = new Vector2(1f, 0f);
        _hudDock.anchoredPosition = new Vector2(lockedClusterWidth * 0.5f, 14f);
        _hudDock.sizeDelta = new Vector2(1180f, 124f);

        var layout = dockGo.AddComponent<HorizontalLayoutGroup>();
        layout.childAlignment = TextAnchor.MiddleRight;
        layout.spacing = spacing;
        layout.childControlWidth = false;
        layout.childControlHeight = false;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        layout.padding = new RectOffset(0, 0, 8, 4);

        _statsCard = BuildStatsCard(dockGo.transform, _font);

        var trayHost = dockGo.AddComponent<PowerUpHudTray>();
        trayHost.EnsureBuilt(_hudDock, _font);
        trayHost.BindStatusSlot(_statsCard);
        _powerUpTray = trayHost;
    }

    const float StatsCardWidth = 236f;
    const float StatsCardHeight = 112f;
    const int StatsFontSize = 22;

    RectTransform BuildStatsCard(Transform parent, Font font)
    {
        var card = CreatePanel(parent, "StatsCard",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            Vector2.zero, new Vector2(StatsCardWidth, StatsCardHeight));
        var le = card.gameObject.AddComponent<LayoutElement>();
        le.preferredWidth = StatsCardWidth;
        le.preferredHeight = StatsCardHeight;
        le.minWidth = StatsCardWidth;
        le.minHeight = StatsCardHeight;
        le.flexibleWidth = 0f;
        le.flexibleHeight = 0f;

        var bg = card.gameObject.AddComponent<Image>();
        StargraveHudStyle.ApplyCard(bg, StargraveHudStyle.CardFill);

        var rows = card.gameObject.AddComponent<VerticalLayoutGroup>();
        rows.padding = new RectOffset(14, 14, 10, 10);
        rows.spacing = 2f;
        rows.childAlignment = TextAnchor.MiddleCenter;
        rows.childControlWidth = true;
        rows.childControlHeight = true;
        rows.childForceExpandWidth = true;
        rows.childForceExpandHeight = false;

        _healthText = CreateStatRow(card, "HealthRow", "HEALTH", "100", StargraveHudStyle.Health, font);
        _staminaText = CreateStatRow(card, "StaminaRow", "STAMINA", "100", StargraveHudStyle.Swim, font);
        _killsText = CreateStatRow(card, "KillsRow", "KILLS", "0", StargraveHudStyle.Kills, font);
        return card;
    }

    static Text CreateStatRow(Transform parent, string name, string label, string value, Color accent, Font font)
    {
        var row = new GameObject(name, typeof(RectTransform));
        row.transform.SetParent(parent, false);
        var rowLe = row.AddComponent<LayoutElement>();
        rowLe.preferredHeight = 28f;
        rowLe.minHeight = 28f;
        rowLe.flexibleHeight = 0f;

        var rowLayout = row.AddComponent<HorizontalLayoutGroup>();
        rowLayout.childAlignment = TextAnchor.MiddleCenter;
        rowLayout.spacing = 8f;
        rowLayout.childControlWidth = true;
        rowLayout.childControlHeight = true;
        rowLayout.childForceExpandWidth = true;
        rowLayout.childForceExpandHeight = true;

        var labelText = CreateLabel(row.transform, "Label",
            new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
            Vector2.zero, new Vector2(96f, 28f), font, StatsFontSize);
        labelText.text = label;
        labelText.alignment = TextAnchor.MiddleLeft;
        labelText.fontStyle = FontStyle.Bold;
        labelText.color = accent;
        labelText.horizontalOverflow = HorizontalWrapMode.Overflow;
        var labelLe = labelText.gameObject.AddComponent<LayoutElement>();
        labelLe.preferredWidth = 96f;
        labelLe.minWidth = 80f;
        labelLe.flexibleWidth = 0f;

        var valueText = CreateLabel(row.transform, "Value",
            new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
            Vector2.zero, new Vector2(96f, 28f), font, StatsFontSize);
        valueText.text = value;
        valueText.alignment = TextAnchor.MiddleRight;
        valueText.fontStyle = FontStyle.Bold;
        valueText.color = StargraveHudStyle.Cream;
        valueText.horizontalOverflow = HorizontalWrapMode.Overflow;
        var valueLe = valueText.gameObject.AddComponent<LayoutElement>();
        valueLe.preferredWidth = 96f;
        valueLe.flexibleWidth = 1f;
        return valueText;
    }

    static RectTransform CreatePanel(Transform parent, string name,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot,
        Vector2 anchoredPos, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.pivot = pivot;
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;
        return rt;
    }

    void CacheCrosshairPieces()
    {
        if (_crosshairRoot == null)
            return;

        var core = new System.Collections.Generic.List<Image>();
        var outline = new System.Collections.Generic.List<Image>();
        Image[] images = _crosshairRoot.GetComponentsInChildren<Image>(true);
        for (int i = 0; i < images.Length; i++)
        {
            Image image = images[i];
            if (image == null)
                continue;
            if (image.gameObject.name.IndexOf("Outline", System.StringComparison.OrdinalIgnoreCase) >= 0)
                outline.Add(image);
            else
                core.Add(image);
        }

        _crosshairCoreImages = core.ToArray();
        _crosshairOutlineImages = outline.ToArray();
    }

    void UpdateCrosshair()
    {
        if (_crosshairRoot == null)
            return;

        if (_shooter == null)
            ResolvePlayerHealth();

        bool onEnemy = false;
        if (_shooter != null && _shooter.TryGetCrosshairHit(out RaycastHit hit))
            onEnemy = hit.collider != null && hit.collider.GetComponentInParent<ZombieAI>() != null;
        if (!onEnemy && _shooter != null && _shooter.HasLockOnTarget)
            onEnemy = true;

        bool hitFlash = Time.unscaledTime < _crosshairHitFlashUntil;
        bool shotPulse = Time.unscaledTime < _crosshairShotPulseUntil;

        Color coreColor = onEnemy ? new Color(0.95f, 0.28f, 0.28f, 0.98f) : new Color(0.96f, 0.96f, 0.98f, 0.95f);
        if (hitFlash)
            coreColor = new Color(1f, 0.88f, 0.32f, 1f);

        Color outlineColor = hitFlash ? new Color(0.18f, 0.08f, 0f, 0.95f) : new Color(0f, 0f, 0f, 0.8f);
        float scale = 1f;
        if (onEnemy)
            scale = 1.08f;
        if (shotPulse)
            scale = Mathf.Max(scale, 1.16f);
        if (hitFlash)
            scale = Mathf.Max(scale, 1.22f);

        _crosshairRoot.localScale = Vector3.Lerp(_crosshairRoot.localScale, Vector3.one * scale, 18f * Time.unscaledDeltaTime);

        if (_crosshairCoreImages != null)
        {
            for (int i = 0; i < _crosshairCoreImages.Length; i++)
            {
                if (_crosshairCoreImages[i] != null)
                    _crosshairCoreImages[i].color = coreColor;
            }
        }

        if (_crosshairOutlineImages != null)
        {
            for (int i = 0; i < _crosshairOutlineImages.Length; i++)
            {
                if (_crosshairOutlineImages[i] != null)
                    _crosshairOutlineImages[i].color = outlineColor;
            }
        }
    }

    void PruneLegacyHudElements()
    {
        RemoveLegacyHudChild("DistanceLabel");
        RemoveLegacyHudChild("HordeLabel");
        RemoveLegacyHudChild("DistanceText");
        RemoveLegacyHudChild("HordeText");
        RemoveLegacyHudChild("HudRoot");
        RemoveLegacyHudChild("DoomBar");
        RemoveLegacyHudChild("HealthPanel");
        RemoveLegacyHudChild("KillsPanel");
        RemoveLegacyHudChild("StaminaPanel");
        RemoveLegacyHudChild("PowerPanel");
        RemoveLegacyHudChild("PowerUpsFallback");

        RectTransform[] all = GetComponentsInChildren<RectTransform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            RectTransform rt = all[i];
            if (rt == null || rt == transform)
                continue;

            string n = rt.gameObject.name;
            if (n.IndexOf("Distance", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("Horde", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                DestroyHudObject(rt.gameObject);
            }
        }
    }

    void RemoveLegacyHudChild(string childName)
    {
        Transform[] all = GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            Transform t = all[i];
            if (t == null || t == transform)
                continue;
            if (t.name == childName)
                DestroyHudObject(t.gameObject);
        }
    }

    void DestroyHudObject(GameObject go)
    {
        if (go == null)
            return;

        if (Application.isPlaying)
            Destroy(go);
        else
            DestroyImmediate(go);
    }

    static Font BuiltinUIFont()
    {
        Font f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (f == null)
            f = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return f;
    }

    static Text CreateLabel(Transform parent, string name,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot,
        Vector2 anchoredPos, Vector2 size, Font font, int fontSize)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.pivot = pivot;
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;
        var text = go.AddComponent<Text>();
        text.font = font;
        text.fontSize = fontSize;
        text.alignment = TextAnchor.UpperLeft;
        text.color = new Color(0.92f, 0.93f, 0.96f, 0.95f);
        text.text = string.Empty;
        text.raycastTarget = false;
        return text;
    }

    static RectTransform CreateCrosshair(Transform parent)
    {
        var root = new GameObject("Crosshair", typeof(RectTransform));
        root.transform.SetParent(parent, false);
        var rt = root.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(48f, 48f);
        rt.anchoredPosition = Vector2.zero;

        CreateCrosshairBar(root.transform, "H_Outline", new Vector2(0f, 0f), new Vector2(22f, 4f), new Color(0f, 0f, 0f, 0.8f));
        CreateCrosshairBar(root.transform, "V_Outline", new Vector2(0f, 0f), new Vector2(4f, 22f), new Color(0f, 0f, 0f, 0.8f));
        CreateCrosshairBar(root.transform, "H_Core", new Vector2(0f, 0f), new Vector2(18f, 2f), new Color(0.96f, 0.96f, 0.98f, 0.95f));
        CreateCrosshairBar(root.transform, "V_Core", new Vector2(0f, 0f), new Vector2(2f, 18f), new Color(0.96f, 0.96f, 0.98f, 0.95f));
        return rt;
    }

    static Image CreateCrosshairBar(Transform parent, string name, Vector2 anchoredPos, Vector2 size, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;
        var image = go.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        return image;
    }
}
