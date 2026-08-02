using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.InputSystem.UI;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Primary one-scene game-loop controller for gameplay scenes. It boots with a 1.3-style humorous loading screen,
/// reveals a menu over the loaded world, starts runs in-place, and routes death to a game-over -> menu flow.
/// </summary>
public sealed class StargraveFrontendBootstrap : MonoBehaviour
{
    enum FrontendState
    {
        BootLoading,
        Menu,
        Playing,
        Paused,
        GameOver
    }

    // Semi-transparent so the frozen world stays visible behind the pause menu ("resume where you left off").
    static readonly Color PausedBackgroundColor = new Color(0f, 0f, 0f, 0.72f);

    static bool s_AutoStartNextBoot;
    static InputActionAsset s_RuntimeUiActions;
    static readonly string[] SplashWhilePlanetBuilding =
    {
        "Sculpting a round world - flat maps are still wrong, sorry.",
        "Terrain mesh: teaching triangles which way is up.",
        "Continents: plate tectonics cosplay, no refunds.",
        "Heightfields: stacking excuses until it looks like a planet.",
        "Collision mesh: so your boots stop at dirt instead of destiny.",
        "Normal maps: baking detail until the GPU says chef's kiss."
    };

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void TrySpawnForGameplayScene()
    {
        if (FindAnyObjectByType<StargraveFrontendBootstrap>() != null)
            return;
        if (FindAnyObjectByType<ZombieSpawner>() == null)
            return;

        var go = new GameObject("Stargrave_FrontendBootstrap");
        go.AddComponent<StargraveFrontendBootstrap>();
    }

    public static void AutoStartNextBoot()
    {
        s_AutoStartNextBoot = true;
    }

    /// <summary>MCP / automated play-test entry: same as pressing Play on the main menu.</summary>
    public void McpStartRun()
    {
        StartRun();
    }

    Canvas _canvas;
    CanvasGroup _group;
    Image _backgroundImage;
    GameObject _menuPanel;
    GameObject _settingsPanel;
    GameObject _loadingPanel;
    GameObject _gameOverPanel;
    Image _loadingBarFill;
    Text _loadingStatusText;
    Text _menuSubtitleText;
    Text _menuStatsText;
    Text _settingsProfileText;
    Text _gameOverStatsText;
    Button _menuResumeButton;
    Button _menuPlayButton;
    Button _menuSettingsButton;
    Button _settingsPerformanceButton;
    Button _settingsBalancedButton;
    Button _settingsQualityButton;
    Button _settingsBackButton;
    Button _gameOverMenuButton;
    Coroutine _loadingStatusQueueCoroutine;
    readonly Queue<string> _loadingStatusQueue = new Queue<string>();
    string _currentLoadingStatus;
    string _lastQueuedLoadingStatus;
    PlayerHealth _player;
    StargraveGameHud _hud;
    FrontendState _state;
    bool _runResetRequired;

    const float PlanetWaitTimeoutSeconds = 45f;
    const float MinimumJokeSecondsPerLine = 1.5f;
    void Awake()
    {
        BuildRuntimeUi();
        EnsureEventSystem();
        ResolveRuntimeRefs();
        EnterBootLoadingState();
        StartCoroutine(CoBootWaitForPlanetThenAdvance());
    }

    void OnEnable()
    {
        PlayerHealth.Died += OnPlayerDied;
    }

    void OnDisable()
    {
        PlayerHealth.Died -= OnPlayerDied;
    }

    void OnDestroy()
    {
        if (_loadingStatusQueueCoroutine != null)
            StopCoroutine(_loadingStatusQueueCoroutine);
        SetPaused(false);
    }

    void Update()
    {
        HandlePauseInput();

        if (_state == FrontendState.Playing || _state == FrontendState.BootLoading)
            return;

        EventSystem current = EventSystem.current;
        if (current == null || current.currentSelectedGameObject != null)
            return;

        FocusButton(GetDefaultButtonForVisiblePanel());
    }

    static void EnsureEventSystem()
    {
        EventSystem eventSystem = FindFirstObjectByType<EventSystem>();
        if (eventSystem == null)
        {
            var go = new GameObject("EventSystem");
            eventSystem = go.AddComponent<EventSystem>();
        }

        eventSystem.sendNavigationEvents = true;
        InputSystemUIInputModule module = eventSystem.GetComponent<InputSystemUIInputModule>();
        if (module == null)
            module = eventSystem.gameObject.AddComponent<InputSystemUIInputModule>();
        ConfigureUiInputModule(module);
    }

    static void ConfigureUiInputModule(InputSystemUIInputModule module)
    {
        if (module == null)
            return;

        if (s_RuntimeUiActions == null)
            s_RuntimeUiActions = CreateRuntimeUiActions();
        s_RuntimeUiActions.Enable();

        InputActionMap map = s_RuntimeUiActions.FindActionMap("UI", true);
        module.move = InputActionReference.Create(map.FindAction("Navigate", true));
        module.submit = InputActionReference.Create(map.FindAction("Submit", true));
        module.cancel = InputActionReference.Create(map.FindAction("Cancel", true));
        module.point = InputActionReference.Create(map.FindAction("Point", true));
        module.leftClick = InputActionReference.Create(map.FindAction("Click", true));
        module.rightClick = InputActionReference.Create(map.FindAction("RightClick", true));
        module.middleClick = InputActionReference.Create(map.FindAction("MiddleClick", true));
        module.scrollWheel = InputActionReference.Create(map.FindAction("ScrollWheel", true));
    }

    static InputActionAsset CreateRuntimeUiActions()
    {
        var asset = ScriptableObject.CreateInstance<InputActionAsset>();
        asset.hideFlags = HideFlags.HideAndDontSave;

        var map = new InputActionMap("UI");
        asset.AddActionMap(map);

        var navigate = map.AddAction("Navigate", InputActionType.PassThrough);
        navigate.expectedControlType = "Vector2";
        navigate.AddCompositeBinding("2DVector")
            .With("Up", "<Gamepad>/dpad/up")
            .With("Down", "<Gamepad>/dpad/down")
            .With("Left", "<Gamepad>/dpad/left")
            .With("Right", "<Gamepad>/dpad/right");
        navigate.AddCompositeBinding("2DVector")
            .With("Up", "<Gamepad>/leftStick/up")
            .With("Down", "<Gamepad>/leftStick/down")
            .With("Left", "<Gamepad>/leftStick/left")
            .With("Right", "<Gamepad>/leftStick/right");
        navigate.AddCompositeBinding("2DVector")
            .With("Up", "<Keyboard>/w")
            .With("Down", "<Keyboard>/s")
            .With("Left", "<Keyboard>/a")
            .With("Right", "<Keyboard>/d");
        navigate.AddCompositeBinding("2DVector")
            .With("Up", "<Keyboard>/upArrow")
            .With("Down", "<Keyboard>/downArrow")
            .With("Left", "<Keyboard>/leftArrow")
            .With("Right", "<Keyboard>/rightArrow");

        var submit = map.AddAction("Submit", InputActionType.Button);
        submit.expectedControlType = "Button";
        submit.AddBinding("<Gamepad>/buttonSouth");
        submit.AddBinding("<Keyboard>/enter");
        submit.AddBinding("<Keyboard>/numpadEnter");
        submit.AddBinding("<Keyboard>/space");

        var cancel = map.AddAction("Cancel", InputActionType.Button);
        cancel.expectedControlType = "Button";
        cancel.AddBinding("<Gamepad>/buttonEast");
        cancel.AddBinding("<Keyboard>/escape");
        cancel.AddBinding("<Keyboard>/backspace");

        var point = map.AddAction("Point", InputActionType.PassThrough);
        point.expectedControlType = "Vector2";
        point.AddBinding("<Mouse>/position");

        var click = map.AddAction("Click", InputActionType.PassThrough);
        click.expectedControlType = "Button";
        click.AddBinding("<Mouse>/leftButton");

        var rightClick = map.AddAction("RightClick", InputActionType.PassThrough);
        rightClick.expectedControlType = "Button";
        rightClick.AddBinding("<Mouse>/rightButton");

        var middleClick = map.AddAction("MiddleClick", InputActionType.PassThrough);
        middleClick.expectedControlType = "Button";
        middleClick.AddBinding("<Mouse>/middleButton");

        var scrollWheel = map.AddAction("ScrollWheel", InputActionType.PassThrough);
        scrollWheel.expectedControlType = "Vector2";
        scrollWheel.AddBinding("<Mouse>/scroll");

        return asset;
    }

    void ResolveRuntimeRefs()
    {
        if (_player == null)
        {
            Transform t = RuntimeSceneRefs.GetPlayerTransform(0.05f);
            if (t != null)
            {
                _player = t.GetComponent<PlayerHealth>();
                if (_player == null)
                    _player = t.GetComponentInParent<PlayerHealth>();
            }

            if (_player == null)
                _player = FindFirstObjectByType<PlayerHealth>(FindObjectsInactive.Include);
        }

        if (_hud == null)
            _hud = StargraveGameHud.Instance ?? FindFirstObjectByType<StargraveGameHud>(FindObjectsInactive.Include);
    }

    void BuildRuntimeUi()
    {
        _canvas = gameObject.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 40000;

        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        gameObject.AddComponent<GraphicRaycaster>();

        _group = gameObject.AddComponent<CanvasGroup>();
        _group.alpha = 1f;
        _group.blocksRaycasts = true;
        _group.interactable = true;

        Font font = BuiltinUIFont();

        var panel = CreateUiObject("Panel", transform);
        Stretch(panel.GetComponent<RectTransform>());
        _backgroundImage = panel.AddComponent<Image>();
        _backgroundImage.color = Color.black;
        _backgroundImage.raycastTarget = false;

        _menuPanel = CreateUiObject("MenuPanel", panel.transform);
        Stretch(_menuPanel.GetComponent<RectTransform>());

        var title = CreateText(_menuPanel.transform, "Title", "STARGRAVE", 54, new Vector2(0.5f, 0.70f), new Vector2(900f, 80f), font);
        title.color = new Color(0.95f, 0.96f, 1f, 1f);

        _menuSubtitleText = CreateText(_menuPanel.transform, "Subtitle", "Main menu", 24, new Vector2(0.5f, 0.63f), new Vector2(900f, 40f), font);
        _menuSubtitleText.color = new Color(0.72f, 0.78f, 0.88f, 1f);

        _menuStatsText = CreateText(_menuPanel.transform, "MenuStats", "", 22, new Vector2(0.5f, 0.56f), new Vector2(960f, 80f), font);
        _menuStatsText.color = new Color(0.84f, 0.87f, 0.93f, 1f);

        // Resume sits at the top of the button stack but is only shown in the pause context (see SetResumeButtonVisible).
        _menuResumeButton = CreateButton(_menuPanel.transform, "ResumeButton", "Resume", new Vector2(0.5f, 0.46f), new Color(0.20f, 0.52f, 0.46f, 1f), font);
        _menuPlayButton = CreateButton(_menuPanel.transform, "PlayButton", "Play", new Vector2(0.5f, 0.38f), new Color(0.22f, 0.55f, 0.32f, 1f), font);
        _menuSettingsButton = CreateButton(_menuPanel.transform, "SettingsButton", "Settings", new Vector2(0.5f, 0.30f), new Color(0.22f, 0.36f, 0.55f, 1f), font);
        var quit = CreateButton(_menuPanel.transform, "QuitButton", "Quit", new Vector2(0.5f, 0.22f), new Color(0.45f, 0.22f, 0.22f, 1f), font);
        _menuResumeButton.onClick.AddListener(OnResumeClicked);
        _menuPlayButton.onClick.AddListener(OnPlayClicked);
        _menuSettingsButton.onClick.AddListener(OnSettingsClicked);
        quit.onClick.AddListener(OnQuitClicked);
        SetResumeButtonVisible(false);

        _settingsPanel = CreateUiObject("SettingsPanel", panel.transform);
        Stretch(_settingsPanel.GetComponent<RectTransform>());
        var settingsCard = CreateUiObject("SettingsCard", _settingsPanel.transform);
        var settingsCardRt = settingsCard.GetComponent<RectTransform>();
        settingsCardRt.anchorMin = new Vector2(0.5f, 0.5f);
        settingsCardRt.anchorMax = new Vector2(0.5f, 0.5f);
        settingsCardRt.pivot = new Vector2(0.5f, 0.5f);
        settingsCardRt.sizeDelta = new Vector2(720f, 520f);
        var settingsCardImg = settingsCard.AddComponent<Image>();
        settingsCardImg.color = new Color(0.05f, 0.06f, 0.1f, 0.96f);

        var settingsTitle = CreateText(settingsCard.transform, "SettingsTitle", "SETTINGS", 42, new Vector2(0.5f, 0.84f), new Vector2(560f, 70f), font);
        settingsTitle.color = Color.white;
        var settingsBody = CreateText(settingsCard.transform, "SettingsBody", "Choose how hard the shell leans on visuals versus frame rate.", 22, new Vector2(0.5f, 0.72f), new Vector2(620f, 70f), font);
        settingsBody.color = new Color(0.8f, 0.84f, 0.91f, 1f);
        _settingsProfileText = CreateText(settingsCard.transform, "SettingsProfile", "", 24, new Vector2(0.5f, 0.60f), new Vector2(620f, 40f), font);
        _settingsProfileText.color = new Color(0.94f, 0.95f, 0.98f, 1f);
        _settingsPerformanceButton = CreateButton(settingsCard.transform, "PerformanceButton", "Performance", new Vector2(0.5f, 0.46f), new Color(0.4f, 0.24f, 0.18f, 1f), font);
        _settingsBalancedButton = CreateButton(settingsCard.transform, "BalancedButton", "Balanced", new Vector2(0.5f, 0.34f), new Color(0.23f, 0.42f, 0.3f, 1f), font);
        _settingsQualityButton = CreateButton(settingsCard.transform, "QualityButton", "Quality", new Vector2(0.5f, 0.22f), new Color(0.2f, 0.3f, 0.5f, 1f), font);
        _settingsBackButton = CreateButton(settingsCard.transform, "BackButton", "Back", new Vector2(0.5f, 0.08f), new Color(0.26f, 0.26f, 0.3f, 1f), font);
        _settingsPerformanceButton.onClick.AddListener(OnPerformancePresetClicked);
        _settingsBalancedButton.onClick.AddListener(OnBalancedPresetClicked);
        _settingsQualityButton.onClick.AddListener(OnQualityPresetClicked);
        _settingsBackButton.onClick.AddListener(OnSettingsBackClicked);

        _loadingPanel = CreateUiObject("LoadingPanel", panel.transform);
        Stretch(_loadingPanel.GetComponent<RectTransform>());

        var loadTitle = CreateText(_loadingPanel.transform, "LoadingTitle", "STARGRAVE", 52, new Vector2(0.5f, 0.68f), new Vector2(800f, 90f), font);
        loadTitle.color = Color.white;

        _loadingStatusText = CreateText(_loadingPanel.transform, "LoadingStatus", "", 22, new Vector2(0.5f, 0.56f), new Vector2(1080f, 120f), font);
        _loadingStatusText.color = new Color(0.95f, 0.95f, 0.98f, 1f);

        var barBg = CreateUiObject("LoadingBarBackground", _loadingPanel.transform);
        var barBgRt = barBg.GetComponent<RectTransform>();
        barBgRt.anchorMin = new Vector2(0.5f, 0.44f);
        barBgRt.anchorMax = new Vector2(0.5f, 0.44f);
        barBgRt.pivot = new Vector2(0.5f, 0.5f);
        barBgRt.sizeDelta = new Vector2(560f, 26f);
        var barBgImg = barBg.AddComponent<Image>();
        barBgImg.color = new Color(0.12f, 0.12f, 0.14f, 1f);

        var fill = CreateUiObject("LoadingBarFill", barBg.transform);
        var fillRt = fill.GetComponent<RectTransform>();
        fillRt.anchorMin = Vector2.zero;
        fillRt.anchorMax = Vector2.one;
        fillRt.offsetMin = new Vector2(3f, 3f);
        fillRt.offsetMax = new Vector2(-3f, -3f);
        _loadingBarFill = fill.AddComponent<Image>();
        _loadingBarFill.color = new Color(0.28f, 0.62f, 0.95f, 1f);
        _loadingBarFill.type = Image.Type.Filled;
        _loadingBarFill.fillMethod = Image.FillMethod.Horizontal;
        _loadingBarFill.fillOrigin = (int)Image.OriginHorizontal.Left;
        _loadingBarFill.fillAmount = 0.12f;

        _gameOverPanel = CreateUiObject("GameOverPanel", panel.transform);
        Stretch(_gameOverPanel.GetComponent<RectTransform>());
        var goTitle = CreateText(_gameOverPanel.transform, "GameOverTitle", "GAME OVER", 52, new Vector2(0.5f, 0.60f), new Vector2(800f, 90f), font);
        goTitle.color = Color.white;
        var goSub = CreateText(_gameOverPanel.transform, "GameOverSub", "The planet won that round.", 24, new Vector2(0.5f, 0.52f), new Vector2(900f, 40f), font);
        goSub.color = new Color(0.78f, 0.82f, 0.9f, 1f);
        _gameOverStatsText = CreateText(_gameOverPanel.transform, "GameOverStats", "", 24, new Vector2(0.5f, 0.44f), new Vector2(920f, 70f), font);
        _gameOverStatsText.color = new Color(0.92f, 0.93f, 0.97f, 1f);
        _gameOverMenuButton = CreateButton(_gameOverPanel.transform, "ReturnToMenuButton", "Return To Menu", new Vector2(0.5f, 0.31f), new Color(0.48f, 0.18f, 0.18f, 1f), font);
        _gameOverMenuButton.onClick.AddListener(OnReturnToMenuClicked);

        _menuPanel.SetActive(false);
        _settingsPanel.SetActive(false);
        _loadingPanel.SetActive(false);
        _gameOverPanel.SetActive(false);
    }

    void OnPlayClicked()
    {
        StartRun();
    }

    void OnReturnToMenuClicked()
    {
        EnterMenuState(true);
    }

    void OnResumeClicked()
    {
        ResumeFromPause();
    }

    /// <summary>
    /// Esc (keyboard) or Start/Options (gamepad — on a PS4 DualShock the Options button maps to
    /// <c>&lt;Gamepad&gt;/start</c>) toggles pause while a run is in progress. Read directly off the devices to
    /// match the rest of the project's input handling (no PlayerInput / .inputactions asset is used at runtime).
    /// </summary>
    void HandlePauseInput()
    {
        if (!PauseTogglePressedThisFrame())
            return;

        if (_state == FrontendState.Playing)
        {
            AudioManager.PlayUiClick();
            EnterPausedState();
        }
        else if (_state == FrontendState.Paused)
        {
            AudioManager.PlayUiClick();
            ResumeFromPause();
        }
    }

    static bool PauseTogglePressedThisFrame()
    {
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            return true;
        if (Gamepad.current != null && Gamepad.current.startButton.wasPressedThisFrame)
            return true;
        return false;
    }

    void SetResumeButtonVisible(bool visible)
    {
        if (_menuResumeButton != null)
            _menuResumeButton.gameObject.SetActive(visible);
    }

    void OnSettingsClicked()
    {
        RefreshSettingsUi();
        _settingsPanel.SetActive(true);
        _menuPanel.SetActive(false);
        SetBackgroundColor(Color.black);
        QueueFocusButton(GetPreferredSettingsButton());
    }

    void OnSettingsBackClicked()
    {
        bool paused = _state == FrontendState.Paused;
        _settingsPanel.SetActive(false);
        _menuPanel.SetActive(true);
        SetResumeButtonVisible(paused);
        RefreshMenuSummary(_runResetRequired);
        if (paused && _menuSubtitleText != null)
            _menuSubtitleText.text = "Paused";
        SetBackgroundColor(paused ? PausedBackgroundColor : Color.black);
        QueueFocusButton(paused ? _menuResumeButton : _menuPlayButton);
    }

    void OnPerformancePresetClicked()
    {
        PerformancePresetBootstrap.SetProfile(PerformancePresetBootstrap.GraphicsProfile.Performance);
        RefreshSettingsUi();
    }

    void OnBalancedPresetClicked()
    {
        PerformancePresetBootstrap.SetProfile(PerformancePresetBootstrap.GraphicsProfile.Balanced);
        RefreshSettingsUi();
    }

    void OnQualityPresetClicked()
    {
        PerformancePresetBootstrap.SetProfile(PerformancePresetBootstrap.GraphicsProfile.Quality);
        RefreshSettingsUi();
    }

    void OnQuitClicked()
    {
        SetPaused(false);
#if UNITY_EDITOR
        EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    void OnPlayerDied(PlayerHealth deadPlayer)
    {
        if (_state != FrontendState.Playing)
            return;

        _player = deadPlayer;
        _runResetRequired = true;
        StartCoroutine(CoShowGameOver());
    }

    IEnumerator CoShowGameOver()
    {
        float delay = _player != null ? Mathf.Max(0.25f, _player.respawnDelaySeconds * 0.5f) : 1.25f;
        yield return new WaitForSecondsRealtime(delay);
        EnterGameOverState();
    }

    void EnterBootLoadingState()
    {
        _state = FrontendState.BootLoading;
        ResolveRuntimeRefs();
        if (_player != null)
            _player.SetGameplayControlEnabled(false);
        if (_hud != null)
            _hud.SetHudVisible(false);

        SetPaused(true);
        if (_loadingStatusQueueCoroutine != null)
        {
            StopCoroutine(_loadingStatusQueueCoroutine);
            _loadingStatusQueueCoroutine = null;
        }

        _loadingStatusQueue.Clear();
        _currentLoadingStatus = null;
        _lastQueuedLoadingStatus = null;
        _menuPanel.SetActive(false);
        _settingsPanel.SetActive(false);
        _loadingPanel.SetActive(true);
        _gameOverPanel.SetActive(false);
        SetBackgroundColor(Color.black);
        SetLoadingProgress(0.12f);
        SetLoadingStatus("Loading...");
    }

    void EnterMenuState(bool afterGameOver)
    {
        _state = FrontendState.Menu;
        ResolveRuntimeRefs();
        if (_player != null)
            _player.SetGameplayControlEnabled(false);
        if (_hud != null)
            _hud.SetHudVisible(false);

        SetPaused(true);
        _loadingPanel.SetActive(false);
        _gameOverPanel.SetActive(false);
        _settingsPanel.SetActive(false);
        _menuPanel.SetActive(true);
        SetResumeButtonVisible(false);
        SetBackgroundColor(Color.black);
        RefreshMenuSummary(afterGameOver);
        RefreshSettingsUi();
        QueueFocusButton(_menuPlayButton);
    }

    /// <summary>
    /// Pause an in-progress run: freeze gameplay (<see cref="SetPaused"/> sets <c>Time.timeScale = 0</c>), disable
    /// player control, and show the main-menu panel with the Resume button over the still-visible frozen world.
    /// Game state is untouched, so resuming continues exactly where the player left off.
    /// </summary>
    void EnterPausedState()
    {
        _state = FrontendState.Paused;
        ResolveRuntimeRefs();
        if (_player != null)
            _player.SetGameplayControlEnabled(false);
        if (_hud != null)
            _hud.SetHudVisible(false);

        SetPaused(true);
        _loadingPanel.SetActive(false);
        _gameOverPanel.SetActive(false);
        _settingsPanel.SetActive(false);
        _menuPanel.SetActive(true);
        SetResumeButtonVisible(true);
        SetBackgroundColor(PausedBackgroundColor);
        RefreshMenuSummary(false);
        if (_menuSubtitleText != null)
            _menuSubtitleText.text = "Paused";
        RefreshSettingsUi();
        QueueFocusButton(_menuResumeButton);
    }

    /// <summary>
    /// Leave the pause menu and hand control straight back to the running game (no respawn / reset / scene reload),
    /// restoring <c>Time.timeScale</c> via <see cref="SetPaused"/>.
    /// </summary>
    void ResumeFromPause()
    {
        ResolveRuntimeRefs();
        _loadingPanel.SetActive(false);
        _settingsPanel.SetActive(false);
        _menuPanel.SetActive(false);
        _gameOverPanel.SetActive(false);
        SetResumeButtonVisible(false);
        SetBackgroundColor(new Color(0f, 0f, 0f, 0f));

        SetPaused(false);
        if (_player != null)
            _player.SetGameplayControlEnabled(true);
        if (_hud != null)
            _hud.SetHudVisible(true);

        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(null);
        _state = FrontendState.Playing;
    }

    void EnterGameOverState()
    {
        _state = FrontendState.GameOver;
        ResolveRuntimeRefs();
        if (_hud != null)
            _hud.SetHudVisible(false);

        SetPaused(true);
        _loadingPanel.SetActive(false);
        _settingsPanel.SetActive(false);
        _menuPanel.SetActive(false);
        _gameOverPanel.SetActive(true);
        SetBackgroundColor(new Color(0f, 0f, 0f, 0.94f));
        RefreshGameOverSummary();
        QueueFocusButton(_gameOverMenuButton);
    }

    void StartRun()
    {
        ResolveRuntimeRefs();
        if (GameStatsManager.Instance != null)
            GameStatsManager.Instance.ResetCurrentRunStats();
        if (_player != null)
            _player.RespawnNow(_runResetRequired);
        if (ItemSpawner.Instance != null)
            ItemSpawner.Instance.ResetForNewRun();
        _runResetRequired = false;

        if (_hud != null)
        {
            _hud.ResetRunStats();
            _hud.SetHudVisible(true);
        }

        _loadingPanel.SetActive(false);
        _settingsPanel.SetActive(false);
        _menuPanel.SetActive(false);
        _gameOverPanel.SetActive(false);
        SetBackgroundColor(new Color(0f, 0f, 0f, 0f));
        SetPaused(false);
        _state = FrontendState.Playing;
    }

    IEnumerator CoBootWaitForPlanetThenAdvance()
    {
        float pulse = 0f;
        float waited = 0f;
        Planet planet = FindFirstObjectByType<Planet>(FindObjectsInactive.Exclude);
        EnqueueJokeSet(SplashWhilePlanetBuilding);

        while (planet != null && !planet.IsGenerated && waited < PlanetWaitTimeoutSeconds)
        {
            float dt = Time.unscaledDeltaTime;
            waited += dt;
            pulse += dt * 2.2f;
            if (_loadingBarFill != null)
                _loadingBarFill.fillAmount = 0.12f + 0.38f * (0.5f + 0.5f * Mathf.Sin(pulse));
            yield return null;
        }

        if (planet != null && !planet.IsGenerated)
            SetLoadingStatus("Planet is shy - opening menu anyway. Bring snacks.");

        SetLoadingProgress(0.72f);
        SetLoadingStatus("Planet mesh & collider: locked in. Your boots have opinions.");
        yield return new WaitForSecondsRealtime(0.25f);

        SetLoadingProgress(0.82f);
        SetLoadingStatus("Water shell, sky candy, lights - the wet glam squad.");
        yield return new WaitForSecondsRealtime(0.25f);

        SetLoadingProgress(0.94f);
        SetLoadingStatus("GPU sync - asking silicon to hold our juice boxes.");
        yield return new WaitForEndOfFrame();

        SetLoadingProgress(1f);
        SetLoadingStatus("Ready. Remember: the ground is down-ish.");
        yield return CoWaitForLoadingStatusQueueIdle();

        if (s_AutoStartNextBoot)
        {
            s_AutoStartNextBoot = false;
            StartRun();
        }
        else
        {
            EnterMenuState(false);
        }
    }

    void EnqueueJokeSet(string[] lines)
    {
        if (lines == null)
            return;
        for (int i = 0; i < lines.Length; i++)
            SetLoadingStatus(lines[i]);
    }

    void SetLoadingStatus(string message)
    {
        if (_loadingStatusText == null)
            return;

        string next = string.IsNullOrEmpty(message) ? "Loading..." : message;
        if (next == _currentLoadingStatus || (_loadingStatusQueue.Count > 0 && next == _lastQueuedLoadingStatus))
            return;

        _loadingStatusQueue.Enqueue(next);
        _lastQueuedLoadingStatus = next;
        if (_loadingStatusQueueCoroutine == null)
            _loadingStatusQueueCoroutine = StartCoroutine(CoProcessLoadingStatusQueue());
    }

    IEnumerator CoProcessLoadingStatusQueue()
    {
        while (_loadingStatusQueue.Count > 0)
        {
            string next = _loadingStatusQueue.Dequeue();
            _currentLoadingStatus = next;
            if (_loadingStatusText != null)
                _loadingStatusText.text = next;
            yield return new WaitForSecondsRealtime(MinimumJokeSecondsPerLine);
        }

        _lastQueuedLoadingStatus = null;
        _loadingStatusQueueCoroutine = null;
    }

    void SetLoadingProgress(float t)
    {
        if (_loadingBarFill != null)
            _loadingBarFill.fillAmount = Mathf.Clamp01(t);
    }

    IEnumerator CoWaitForLoadingStatusQueueIdle()
    {
        while (_loadingStatusQueueCoroutine != null || _loadingStatusQueue.Count > 0)
            yield return null;
    }

    void SetPaused(bool paused)
    {
        Time.timeScale = paused ? 0f : 1f;
        AudioListener.pause = paused;
        Cursor.visible = paused;
        Cursor.lockState = paused ? CursorLockMode.None : CursorLockMode.Locked;
    }

    void RefreshMenuSummary(bool afterGameOver)
    {
        if (_menuSubtitleText != null)
            _menuSubtitleText.text = afterGameOver ? "Main menu - ready for another run" : "Main menu";

        if (_menuStatsText == null)
            return;

        GameStatsManager stats = GameStatsManager.Instance;
        if (stats == null)
        {
            _menuStatsText.text = "Highest Kills  0\nFurthest Distance  0.0m";
            return;
        }

        _menuStatsText.text = $"Highest Kills  {stats.HighKillScore}\nFurthest Distance  {FormatDistance(stats.HighDistanceMeters)}";
    }

    void RefreshSettingsUi()
    {
        if (_settingsProfileText == null)
            return;

        PerformancePresetBootstrap.GraphicsProfile profile = PerformancePresetBootstrap.GetCurrentProfile();
        string description = "default balance between frame rate and shell polish";
        if (profile == PerformancePresetBootstrap.GraphicsProfile.Performance)
            description = "lighter shadows, stricter zombie AI budgets";
        else if (profile == PerformancePresetBootstrap.GraphicsProfile.Quality)
            description = "richer shadows, looser zombie AI budgets";
        _settingsProfileText.text = $"Current Profile  {profile}\n{description}";
    }

    void RefreshGameOverSummary()
    {
        if (_gameOverStatsText == null)
            return;

        GameStatsManager stats = GameStatsManager.Instance;
        int kills = stats != null ? stats.CurrentKills : 0;
        float distance = _hud != null ? _hud.CurrentDistanceTravelled : 0f;
        _gameOverStatsText.text = $"Run Kills  {kills}\nRun Distance  {FormatDistance(distance)}";
    }

    static string FormatDistance(float meters)
    {
        return $"{Mathf.Max(0f, meters):0.0}m";
    }

    void SetBackgroundColor(Color color)
    {
        if (_backgroundImage != null)
            _backgroundImage.color = color;
    }

    Button GetDefaultButtonForVisiblePanel()
    {
        if (_settingsPanel != null && _settingsPanel.activeInHierarchy)
            return GetPreferredSettingsButton();
        if (_gameOverPanel != null && _gameOverPanel.activeInHierarchy)
            return _gameOverMenuButton;
        if (_menuPanel != null && _menuPanel.activeInHierarchy)
            return _state == FrontendState.Paused ? _menuResumeButton : _menuPlayButton;
        return null;
    }

    Button GetPreferredSettingsButton()
    {
        PerformancePresetBootstrap.GraphicsProfile profile = PerformancePresetBootstrap.GetCurrentProfile();
        if (profile == PerformancePresetBootstrap.GraphicsProfile.Performance)
            return _settingsPerformanceButton;
        if (profile == PerformancePresetBootstrap.GraphicsProfile.Quality)
            return _settingsQualityButton;
        return _settingsBalancedButton;
    }

    void QueueFocusButton(Button button)
    {
        if (button == null)
            return;
        StartCoroutine(CoFocusButtonNextFrame(button));
    }

    IEnumerator CoFocusButtonNextFrame(Button button)
    {
        yield return null;
        FocusButton(button);
    }

    static void FocusButton(Button button)
    {
        if (button == null || !button.isActiveAndEnabled)
            return;

        EventSystem current = EventSystem.current;
        if (current == null)
            return;

        current.SetSelectedGameObject(null);
        current.SetSelectedGameObject(button.gameObject);
        button.Select();
    }

    static Font BuiltinUIFont()
    {
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null)
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return font;
    }

    static GameObject CreateUiObject(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go;
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    static Text CreateText(Transform parent, string name, string value, int fontSize, Vector2 anchor, Vector2 size, Font font)
    {
        var go = CreateUiObject(name, parent);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = anchor;
        rt.sizeDelta = size;
        rt.anchoredPosition = Vector2.zero;

        var text = go.AddComponent<Text>();
        text.font = font;
        text.fontSize = fontSize;
        text.alignment = TextAnchor.MiddleCenter;
        text.text = value;
        text.raycastTarget = false;
        return text;
    }

    static Button CreateButton(Transform parent, string name, string label, Vector2 anchor, Color color, Font font)
    {
        var go = CreateUiObject(name, parent);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = anchor;
        rt.sizeDelta = new Vector2(300f, 56f);
        rt.anchoredPosition = Vector2.zero;

        var image = go.AddComponent<Image>();
        image.color = color;
        var button = go.AddComponent<Button>();
        button.targetGraphic = image;
        button.transition = Selectable.Transition.ColorTint;
        var colors = button.colors;
        colors.normalColor = color;
        colors.highlightedColor = MultiplyColor(color, 1.08f);
        colors.selectedColor = MultiplyColor(color, 1.16f);
        colors.pressedColor = MultiplyColor(color, 0.92f);
        colors.disabledColor = new Color(color.r * 0.5f, color.g * 0.5f, color.b * 0.5f, 0.7f);
        colors.colorMultiplier = 1f;
        colors.fadeDuration = 0.08f;
        button.colors = colors;
        var navigation = button.navigation;
        navigation.mode = Navigation.Mode.Automatic;
        button.navigation = navigation;

        // Every menu button clicks on press (plays even while paused via AudioManager.PlayUi -> ignoreListenerPause).
        button.onClick.AddListener(() => AudioManager.PlayUiClick());

        var text = CreateText(go.transform, "Text", label, 26, new Vector2(0.5f, 0.5f), rt.sizeDelta, font);
        text.color = Color.white;
        go.AddComponent<FrontendButtonScaleFx>();
        return button;
    }

    static Color MultiplyColor(Color color, float multiplier)
    {
        return new Color(
            Mathf.Clamp01(color.r * multiplier),
            Mathf.Clamp01(color.g * multiplier),
            Mathf.Clamp01(color.b * multiplier),
            color.a);
    }

    sealed class FrontendButtonScaleFx : MonoBehaviour, ISelectHandler, IDeselectHandler, IPointerEnterHandler, IPointerExitHandler
    {
        const float SelectedScale = 1.14f;
        const float LerpSpeed = 14f;

        RectTransform _rect;
        Vector3 _baseScale;
        bool _isSelected;
        bool _isHovered;
        Text _label;

        void Awake()
        {
            _rect = transform as RectTransform;
            _baseScale = _rect != null ? _rect.localScale : Vector3.one;
            _label = GetComponentInChildren<Text>();
        }

        void OnEnable()
        {
            if (_rect == null)
                _rect = transform as RectTransform;
            _isHovered = false;
            _isSelected = EventSystem.current != null && EventSystem.current.currentSelectedGameObject == gameObject;
            if (_rect != null)
                _rect.localScale = _baseScale;
        }

        void Update()
        {
            if (_rect == null)
                return;

            bool active = _isHovered || _isSelected || (EventSystem.current != null && EventSystem.current.currentSelectedGameObject == gameObject);
            Vector3 target = _baseScale * (active ? SelectedScale : 1f);
            _rect.localScale = Vector3.Lerp(_rect.localScale, target, LerpSpeed * Time.unscaledDeltaTime);
            if (_label != null)
                _label.fontStyle = active ? FontStyle.Bold : FontStyle.Normal;
        }

        public void OnSelect(BaseEventData eventData)
        {
            // Rollover on selection-change covers BOTH mouse hover (pointer-enter selects the button)
            // and controller/keyboard navigation. Plays while paused (UI sound, ignores listener pause).
            if (!_isSelected)
                AudioManager.PlayUiRollover();
            _isSelected = true;
        }

        public void OnDeselect(BaseEventData eventData)
        {
            _isSelected = false;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            _isHovered = true;
            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(gameObject);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _isHovered = false;
        }
    }
}
