using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// In-game HUD: health plus run/best kills.
/// Run distance is still tracked in the background for menu and game-over summaries.
/// </summary>
public sealed class StargraveGameHud : MonoBehaviour
{
    public static StargraveGameHud Instance { get; private set; }

    [Header("Optional references (filled at runtime if null)")]
    [SerializeField] Canvas _canvas;
    [SerializeField] Image _healthFill;
    [SerializeField] Text _healthText;
    [SerializeField] Text _killsText;
    [SerializeField] Text _powerUpsText;
    [SerializeField] RectTransform _crosshairRoot;
    [SerializeField] Image[] _crosshairCoreImages;
    [SerializeField] Image[] _crosshairOutlineImages;

    PlayerHealth _health;
    Transform _playerTransform;
    Transform _planetTransform;
    Vector3 _lastPlayerPos;
    float _distanceTravelled;
    CanvasGroup _group;
    PlayerShooting _shooter;
    float _crosshairShotPulseUntil;
    float _crosshairHitFlashUntil;

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
        if (Instance == this)
            Instance = null;
    }

    void OnEnable()
    {
        GameStatsManager.ScoresChanged += OnScoresChanged;
        PlayerShooting.ShotFired += OnShotFired;
        PlayerShooting.HitConfirmed += OnHitConfirmed;
        PlayerBuffController.PowerUpsChanged += OnPowerUpsChanged;
        ResolvePlayerHealth();
        if (GameStatsManager.Instance != null)
            OnScoresChanged(GameStatsManager.Instance.CurrentKills, GameStatsManager.Instance.HighKillScore);
    }

    void OnDisable()
    {
        GameStatsManager.ScoresChanged -= OnScoresChanged;
        PlayerShooting.ShotFired -= OnShotFired;
        PlayerShooting.HitConfirmed -= OnHitConfirmed;
        PlayerBuffController.PowerUpsChanged -= OnPowerUpsChanged;
    }

    public void ResetRunStats()
    {
        _distanceTravelled = 0f;
        ResolvePlayerHealth();
    }

    void OnScoresChanged(int currentKills, int bestKills)
    {
        if (_killsText != null)
            _killsText.text = $"Kills  {currentKills} | Best  {bestKills}";
    }

    void OnPowerUpsChanged(string[] activePowerUps)
    {
        if (_powerUpsText == null)
            return;

        if (activePowerUps == null || activePowerUps.Length == 0)
        {
            _powerUpsText.text = "Power-Ups  None";
            return;
        }

        _powerUpsText.text = $"Power-Ups  {string.Join(", ", activePowerUps)}";
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

        if (_health != null && _healthFill != null)
        {
            if (_healthFill.sprite == null)
                _healthFill.sprite = UiWhiteSprite();
            float t = _health.maxHealth > 0 ? (float)_health.CurrentHealth / _health.maxHealth : 0f;
            _healthFill.fillAmount = Mathf.Clamp01(t);
        }

        if (_healthText != null && _health != null)
            _healthText.text = $"HP  {_health.CurrentHealth} / {_health.maxHealth}";

        UpdateDistance();
        UpdateCrosshair();
    }

    void ResolvePlayerHealth()
    {
        Transform t = RuntimeSceneRefs.GetPlayerTransform(0.05f);
        if (t == null)
            return;
        _health = t.GetComponent<PlayerHealth>();
        if (_health == null)
            _health = t.GetComponentInParent<PlayerHealth>();
        _playerTransform = _health != null ? _health.transform : t;
        _shooter = _playerTransform != null ? _playerTransform.GetComponent<PlayerShooting>() : null;
        if (_shooter == null && _playerTransform != null)
            _shooter = _playerTransform.GetComponentInParent<PlayerShooting>();
        if (_shooter == null)
            _shooter = FindFirstObjectByType<PlayerShooting>(FindObjectsInactive.Include);
        if (_playerTransform != null)
        {
            PlayerBuffController buffs = _playerTransform.GetComponent<PlayerBuffController>();
            if (buffs != null)
                OnPowerUpsChanged(buffs.GetActivePowerUpNames());
            else
                OnPowerUpsChanged(null);
        }
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

        var hudRoot = new GameObject("HudRoot", typeof(RectTransform));
        hudRoot.transform.SetParent(transform, false);
        var rootRt = hudRoot.GetComponent<RectTransform>();
        rootRt.anchorMin = new Vector2(0f, 1f);
        rootRt.anchorMax = new Vector2(0f, 1f);
        rootRt.pivot = new Vector2(0f, 1f);
        rootRt.anchoredPosition = new Vector2(28f, -24f);
        rootRt.sizeDelta = new Vector2(520f, 164f);

        Font font = BuiltinUIFont();

        _crosshairRoot = CreateCrosshair(transform);
        CacheCrosshairPieces();

        _healthFill = CreateBar(hudRoot.transform, "HealthBar", new Vector2(0f, 0f), new Vector2(320f, 22f));
        _healthText = CreateLabel(hudRoot.transform, "HealthLabel", new Vector2(0f, -36f), new Vector2(360f, 32f), font, 22);
        _killsText = CreateLabel(hudRoot.transform, "KillsLabel", new Vector2(0f, -72f), new Vector2(360f, 30f), font, 20);
        _powerUpsText = CreateLabel(hudRoot.transform, "PowerUpsLabel", new Vector2(0f, -106f), new Vector2(520f, 46f), font, 18);
        _powerUpsText.alignment = TextAnchor.UpperLeft;
        _powerUpsText.horizontalOverflow = HorizontalWrapMode.Wrap;
        _powerUpsText.verticalOverflow = VerticalWrapMode.Overflow;
        _powerUpsText.text = "Power-Ups  None";
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

    static Image CreateBar(Transform parent, string name, Vector2 anchoredPos, Vector2 size)
    {
        Sprite white = UiWhiteSprite();

        var bg = new GameObject(name + "_Bg", typeof(RectTransform));
        bg.transform.SetParent(parent, false);
        var bgRt = bg.GetComponent<RectTransform>();
        bgRt.anchorMin = new Vector2(0f, 1f);
        bgRt.anchorMax = new Vector2(0f, 1f);
        bgRt.pivot = new Vector2(0f, 1f);
        bgRt.anchoredPosition = anchoredPos;
        bgRt.sizeDelta = size;
        var bgImg = bg.AddComponent<Image>();
        bgImg.sprite = white;
        bgImg.color = new Color(0.1f, 0.11f, 0.14f, 0.85f);

        var fill = new GameObject(name + "_Fill", typeof(RectTransform));
        fill.transform.SetParent(bg.transform, false);
        var fillRt = fill.GetComponent<RectTransform>();
        fillRt.anchorMin = Vector2.zero;
        fillRt.anchorMax = Vector2.one;
        fillRt.offsetMin = new Vector2(3f, 3f);
        fillRt.offsetMax = new Vector2(-3f, -3f);
        var fillImg = fill.AddComponent<Image>();
        fillImg.sprite = white;
        fillImg.color = new Color(0.55f, 0.2f, 0.22f, 0.95f);
        fillImg.type = Image.Type.Filled;
        fillImg.fillMethod = Image.FillMethod.Horizontal;
        fillImg.fillOrigin = (int)Image.OriginHorizontal.Left;
        fillImg.fillAmount = 1f;
        return fillImg;
    }

    static Sprite UiWhiteSprite()
    {
        // Filled Images need a sprite; without one, fillAmount does nothing visually.
        return Sprite.Create(Texture2D.whiteTexture, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 100f);
    }

    static Text CreateLabel(Transform parent, string name, Vector2 anchoredPos, Vector2 size, Font font, int fontSize)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;
        var text = go.AddComponent<Text>();
        text.font = font;
        text.fontSize = fontSize;
        text.alignment = TextAnchor.UpperLeft;
        text.color = new Color(0.92f, 0.93f, 0.96f, 0.95f);
        text.text = string.Empty;
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
