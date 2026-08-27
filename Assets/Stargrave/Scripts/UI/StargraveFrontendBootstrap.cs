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
        CharacterSelect,
        Playing,
        Paused,
        GameOver
    }

    static readonly Color MenuBackgroundColor = Color.black;

    static bool s_AutoStartNextBoot;
    static InputActionAsset s_RuntimeUiActions;
    static readonly string[] SplashWhilePlanetBuilding =
    {
        "Sculpting a round world - flat maps are still wrong, sorry.",
        "Terrain mesh: teaching triangles which way is up.",
        "Continents: plate tectonics cosplay, no refunds.",
        "Heightfields: stacking excuses until it looks like a planet.",
        "Collision mesh: so your boots stop at dirt instead of destiny.",
        "Normal maps: baking detail until the GPU says chef's kiss.",
        "Loading Pwee... Unpredictable with a chance of denial.",
        "Jamie Wingfield calls it 'average height.' Everyone else calls it altitude."
    };

    static readonly string[] SplashFinishingTouches =
    {
        "Planet mesh & collider: locked in. Your boots have opinions.",
        "Water shell, sky candy, lights - the wet glam squad.",
        "GPU sync - asking silicon to hold our juice boxes."
    };

    static readonly string[] SplashReadyLines =
    {
        "Ready. Remember: the ground is down-ish.",
        "Systems online. Gravity is still a suggestion.",
        "All clear. Try not to trip on the curvature."
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
    GameObject _characterSelectPanel;
    Image _loadingSpinnerArc;
    RectTransform _loadingSpinnerRt;
    Coroutine _loadingSpinnerCoroutine;
    RectTransform _loadingJokeStackRt;
    RectTransform _loadingJokeCardFrontRt;
    RectTransform _loadingJokeCardBackRt;
    CanvasGroup _loadingJokeCardFrontGroup;
    CanvasGroup _loadingJokeCardBackGroup;
    Text _loadingJokeCardFrontText;
    Text _loadingJokeCardBackText;
    bool _loadingJokeCardHasShown;
    Text _loadingTaglineText;
    Text _loadingFooterText;
    Coroutine _loadingTaglinePulseCoroutine;
    Text _menuSubtitleText;
    Text _menuStatsText;
    Text _settingsProfileText;
    Text _settingsMouseSensText;
    Text _gameOverStatsText;
    Button _menuResumeButton;
    Button _menuPlayButton;
    Button _menuSettingsButton;
    Font _uiFont;
    Button _settingsPerformanceButton;
    Button _settingsBalancedButton;
    Button _settingsQualityButton;
    Button _settingsMouseSensDownButton;
    Button _settingsMouseSensUpButton;
    Button _settingsBackButton;
    Button _gameOverMenuButton;
    Button _characterContinueButton;
    Button _characterBackButton;
    CharacterSelect3DPanel _characterSelect3D;
    Coroutine _loadingStatusQueueCoroutine;
    readonly Queue<string> _loadingStatusQueue = new Queue<string>();
    string _currentLoadingStatus;
    string _lastQueuedLoadingStatus;
    PlayerHealth _player;
    StargraveGameHud _hud;
    GraphicRaycaster _graphicRaycaster;
    InputSystemUIInputModule _uiInputModule;
    GameObject _manualPointerHoverGo;
    FrontendState _state;
    bool _runResetRequired;

    const float PlanetWaitTimeoutSeconds = 45f;
    const float MinimumJokeSecondsPerLine = 1.5f;
    const float LoadingJokeCardLiftSeconds = 0.3f;
    const float LoadingJokeCardLandSeconds = 0.32f;
    const float LoadingCardHeight = 150f;
    static readonly Vector2 LoadingCardFrontPos = Vector2.zero;
    static readonly Vector2 LoadingCardBackPos = new Vector2(18f, -36f);
    static readonly Vector2 LoadingCardPeekPos = new Vector2(8f, 168f);
    const float LoadingCardFrontScale = 1f;
    const float LoadingCardPeekScale = 0.97f;
    const float LoadingCardBackScale = 0.88f;
    const float LoadingCardFrontRot = 0f;
    const float LoadingCardPeekRot = -1.5f;
    const float LoadingCardBackRot = -4.5f;
    void Awake()
    {
        BuildRuntimeUi();
        EnsureEventSystem();
        _uiInputModule = FindFirstObjectByType<InputSystemUIInputModule>();
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
        StopLoadingTaglinePulse();
        StopLoadingSpinner();
        SetPaused(false);
    }

    void Update()
    {
        HandlePauseInput();

        if (_state == FrontendState.Playing)
            return;

        if (_state == FrontendState.BootLoading)
            return;

        EventSystem current = EventSystem.current;
        if (current == null || current.currentSelectedGameObject != null)
            return;

        FocusButton(GetDefaultButtonForVisiblePanel());
    }

    void LateUpdate()
    {
        if (_state == FrontendState.Playing)
            return;

        SyncMenuUiInputActions();
        EnsureMenuPointerAccess();
        if (_state != FrontendState.BootLoading)
            UpdateManualUiPointer();
        HandleFrontendKeyboardSubmit();
    }

    void SyncMenuUiInputActions()
    {
        if (s_RuntimeUiActions == null)
            return;

        InputAction click = s_RuntimeUiActions.FindAction("UI/Click", false);
        InputAction point = s_RuntimeUiActions.FindAction("UI/Point", false);
        if (click == null || point == null)
            return;

        bool manualMouse = IsFrontendUiInteractive();
        if (manualMouse)
        {
            if (click.enabled)
                click.Disable();
            if (point.enabled)
                point.Disable();
        }
        else
        {
            if (!click.enabled)
                click.Enable();
            if (!point.enabled)
                point.Enable();
            _manualPointerHoverGo = null;
        }
    }

    static void EnsureEventSystem()
    {
        EventSystem eventSystem = FindFirstObjectByType<EventSystem>();
        InputSystemUIInputModule module = null;
        if (eventSystem == null)
        {
            var go = new GameObject("EventSystem");
            eventSystem = go.AddComponent<EventSystem>();
            module = go.AddComponent<InputSystemUIInputModule>();
        }
        else
        {
            module = eventSystem.GetComponent<InputSystemUIInputModule>();
            if (module == null)
                module = eventSystem.gameObject.AddComponent<InputSystemUIInputModule>();
        }

        eventSystem.sendNavigationEvents = true;
        ConfigureUiInputModule(module);
    }

    void EnsureMenuPointerAccess()
    {
        if (!IsFrontendUiInteractive())
            return;

        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
        DismissBlockingOverlay();

        if (_uiInputModule != null && !_uiInputModule.enabled)
            _uiInputModule.enabled = true;
    }

    void HandleFrontendKeyboardSubmit()
    {
        if (!IsFrontendUiInteractive())
            return;

        EventSystem eventSystem = EventSystem.current;
        if (eventSystem == null || eventSystem.currentSelectedGameObject == null)
            return;

        bool submit = false;
        if (Keyboard.current != null)
        {
            submit = Keyboard.current.enterKey.wasPressedThisFrame
                || Keyboard.current.numpadEnterKey.wasPressedThisFrame
                || Keyboard.current.spaceKey.wasPressedThisFrame;
        }

        if (!submit && Gamepad.current != null)
            submit = Gamepad.current.buttonSouth.wasPressedThisFrame;

        if (!submit)
            return;

        var submitData = new BaseEventData(eventSystem);
        ExecuteEvents.Execute(eventSystem.currentSelectedGameObject, submitData, ExecuteEvents.submitHandler);
    }

    bool IsFrontendUiInteractive()
    {
        return _state == FrontendState.Menu
            || _state == FrontendState.Paused
            || _state == FrontendState.CharacterSelect
            || _state == FrontendState.GameOver;
    }

    static void DismissBlockingOverlay()
    {
        if (StargraveLoadOverlay.Instance != null)
            StargraveLoadOverlay.Instance.HideIfIdle();
    }

    void UpdateManualUiPointer()
    {
        if (_graphicRaycaster == null)
            return;

        EventSystem eventSystem = EventSystem.current;
        if (eventSystem == null)
            return;

        if (!TryReadMenuPointer(out Vector2 position, out bool leftPressedThisFrame))
            return;

        var pointerData = new PointerEventData(eventSystem)
        {
            position = position,
            pointerId = -1,
            pressPosition = position,
        };

        var hits = new List<RaycastResult>(16);
        _graphicRaycaster.Raycast(pointerData, hits);
        if (hits.Count == 0)
            eventSystem.RaycastAll(pointerData, hits);

        GameObject topHit = null;
        int bestHoverPriority = -1;
        for (int i = 0; i < hits.Count; i++)
        {
            GameObject go = hits[i].gameObject;
            if (go == null || !go.activeInHierarchy)
                continue;

            GameObject resolved = ResolveManualPointerTarget(go);
            int priority = GetManualPointerHoverPriority(resolved);
            if (priority > bestHoverPriority)
            {
                bestHoverPriority = priority;
                topHit = resolved;
            }
        }

        if (topHit != _manualPointerHoverGo)
        {
            if (_manualPointerHoverGo != null)
            {
                ExecuteEvents.Execute(_manualPointerHoverGo, pointerData, ExecuteEvents.pointerExitHandler);
                ExecuteEvents.Execute(_manualPointerHoverGo, pointerData, ExecuteEvents.deselectHandler);
            }

            _manualPointerHoverGo = topHit;

            if (_manualPointerHoverGo != null)
            {
                ExecuteEvents.Execute(_manualPointerHoverGo, pointerData, ExecuteEvents.pointerEnterHandler);
                ExecuteEvents.Execute(_manualPointerHoverGo, pointerData, ExecuteEvents.selectHandler);
            }
        }

        if (!IsFrontendUiInteractive() || !leftPressedThisFrame)
            return;

        pointerData.button = PointerEventData.InputButton.Left;
        TryManualPointerClick(hits, pointerData);
    }

    static bool TryReadMenuPointer(out Vector2 position, out bool leftPressedThisFrame)
    {
        position = Vector2.zero;
        leftPressedThisFrame = false;
        bool hasPosition = false;

        if (Pointer.current != null)
        {
            position = Pointer.current.position.ReadValue();
            hasPosition = true;
            leftPressedThisFrame = Pointer.current.press.wasPressedThisFrame;
        }

        Mouse mouse = Mouse.current;
        if (mouse != null)
        {
            position = mouse.position.ReadValue();
            hasPosition = true;
            if (!leftPressedThisFrame)
                leftPressedThisFrame = mouse.leftButton.wasPressedThisFrame;
        }

        if (!hasPosition)
        {
            mouse = InputSystem.GetDevice<Mouse>();
            if (mouse != null)
            {
                if (!mouse.enabled)
                    InputSystem.EnableDevice(mouse);
                position = mouse.position.ReadValue();
                hasPosition = true;
                if (!leftPressedThisFrame)
                    leftPressedThisFrame = mouse.leftButton.wasPressedThisFrame;
            }
        }

        return hasPosition;
    }

    static GameObject ResolveManualPointerTarget(GameObject rawHit)
    {
        if (rawHit == null)
            return null;

        Transform t = rawHit.transform;
        while (t != null)
        {
            GameObject go = t.gameObject;
            if (go.GetComponent<FrontendButtonScaleFx>() != null || go.GetComponent<Button>() != null)
                return go;
            if (IsCarouselChevronGo(go))
                return go;
            t = t.parent;
        }

        return rawHit;
    }

    static int GetManualPointerHoverPriority(GameObject go)
    {
        if (go == null)
            return -1;
        if (go.GetComponent<FrontendButtonScaleFx>() != null || go.GetComponent<Button>() != null)
            return 3;
        if (IsCarouselChevronGo(go))
            return 3;
        if (go.name == "CarouselView")
            return 0;
        return 1;
    }

    static bool IsCarouselChevronGo(GameObject go) =>
        go != null && (go.name == "CarouselLeft" || go.name == "CarouselRight");

    static bool IsCarouselPointerRelay(IPointerClickHandler handler) =>
        handler != null && handler.GetType().Name == "CarouselPointerRelay";

    static void TryManualPointerClick(List<RaycastResult> hits, PointerEventData pointerData)
    {
        IPointerClickHandler deferredCarouselClick = null;

        for (int i = 0; i < hits.Count; i++)
        {
            Transform t = hits[i].gameObject != null ? hits[i].gameObject.transform : null;
            while (t != null)
            {
                GameObject go = t.gameObject;
                Button button = go.GetComponent<Button>();
                if (button != null && button.isActiveAndEnabled && button.interactable)
                {
                    button.onClick.Invoke();
                    return;
                }

                IPointerClickHandler clickHandler = go.GetComponent<IPointerClickHandler>();
                if (clickHandler != null)
                {
                    if (IsCarouselPointerRelay(clickHandler))
                        deferredCarouselClick ??= clickHandler;
                    else
                    {
                        ExecuteEvents.Execute(go, pointerData, ExecuteEvents.pointerDownHandler);
                        ExecuteEvents.Execute(go, pointerData, ExecuteEvents.pointerUpHandler);
                        clickHandler.OnPointerClick(pointerData);
                        return;
                    }
                    break;
                }

                t = t.parent;
            }
        }

        if (deferredCarouselClick != null)
            deferredCarouselClick.OnPointerClick(pointerData);
    }

    static void ConfigureUiInputModule(InputSystemUIInputModule module)
    {
        if (module == null)
            return;

        if (s_RuntimeUiActions == null)
            s_RuntimeUiActions = CreateRuntimeUiActions();
        s_RuntimeUiActions.Enable();

        InputActionMap uiMap = s_RuntimeUiActions.FindActionMap("UI", true);
        if (!uiMap.enabled)
            uiMap.Enable();

        module.actionsAsset = s_RuntimeUiActions;
        module.move = InputActionReference.Create(uiMap.FindAction("Navigate", true));
        module.submit = InputActionReference.Create(uiMap.FindAction("Submit", true));
        module.cancel = InputActionReference.Create(uiMap.FindAction("Cancel", true));
        module.point = InputActionReference.Create(uiMap.FindAction("Point", true));
        module.leftClick = InputActionReference.Create(uiMap.FindAction("Click", true));
        module.rightClick = InputActionReference.Create(uiMap.FindAction("RightClick", true));
        module.middleClick = InputActionReference.Create(uiMap.FindAction("MiddleClick", true));
        module.scrollWheel = InputActionReference.Create(uiMap.FindAction("ScrollWheel", true));
        module.enabled = true;

        var bootstrap = FindFirstObjectByType<StargraveFrontendBootstrap>();
        if (bootstrap != null)
            bootstrap._uiInputModule = module;
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

        var click = map.AddAction("Click", InputActionType.Button);
        click.expectedControlType = "Button";
        click.AddBinding("<Mouse>/leftButton");

        var rightClick = map.AddAction("RightClick", InputActionType.Button);
        rightClick.expectedControlType = "Button";
        rightClick.AddBinding("<Mouse>/rightButton");

        var middleClick = map.AddAction("MiddleClick", InputActionType.Button);
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
        _graphicRaycaster = gameObject.AddComponent<GraphicRaycaster>();

        _group = gameObject.AddComponent<CanvasGroup>();
        _group.alpha = 1f;
        _group.blocksRaycasts = true;
        _group.interactable = true;

        Font font = BuiltinUIFont();
        _uiFont = font;

        var panel = CreateUiObject("Panel", transform);
        Stretch(panel.GetComponent<RectTransform>());
        _backgroundImage = panel.AddComponent<Image>();
        _backgroundImage.color = Color.black;
        _backgroundImage.raycastTarget = false;

        _menuPanel = CreateUiObject("MenuPanel", panel.transform);
        Stretch(_menuPanel.GetComponent<RectTransform>());

        var title = CreateText(_menuPanel.transform, "Title", "STARGRAVE", 54, new Vector2(0.5f, 0.78f), new Vector2(900f, 80f), font);
        title.color = new Color(0.95f, 0.96f, 1f, 1f);

        _menuSubtitleText = CreateText(_menuPanel.transform, "Subtitle", "Main menu", 24, new Vector2(0.5f, 0.71f), new Vector2(900f, 40f), font);
        _menuSubtitleText.color = new Color(0.72f, 0.78f, 0.88f, 1f);

        _menuStatsText = BuildHighKillCard(_menuPanel.transform, font);

        // Resume sits at the top of the button stack but is only shown in the pause context (see SetResumeButtonVisible).
        _menuResumeButton = CreateButton(_menuPanel.transform, "ResumeButton", "Resume", new Vector2(0.5f, 0.42f), new Color(0.20f, 0.52f, 0.46f, 1f), font);
        _menuPlayButton = CreateButton(_menuPanel.transform, "PlayButton", "Play", new Vector2(0.5f, 0.34f), new Color(0.22f, 0.55f, 0.32f, 1f), font);
        _menuSettingsButton = CreateButton(_menuPanel.transform, "SettingsButton", "Settings", new Vector2(0.5f, 0.26f), new Color(0.22f, 0.36f, 0.55f, 1f), font);
        var quit = CreateButton(_menuPanel.transform, "QuitButton", "Quit", new Vector2(0.5f, 0.18f), new Color(0.45f, 0.22f, 0.22f, 1f), font);
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
        settingsCardRt.sizeDelta = new Vector2(720f, 620f);
        var settingsCardImg = settingsCard.AddComponent<Image>();
        settingsCardImg.color = new Color(0.05f, 0.06f, 0.1f, 0.96f);
        settingsCardImg.raycastTarget = false;

        var settingsTitle = CreateText(settingsCard.transform, "SettingsTitle", "SETTINGS", 42, new Vector2(0.5f, 0.90f), new Vector2(560f, 70f), font);
        settingsTitle.color = Color.white;
        var settingsBody = CreateText(settingsCard.transform, "SettingsBody", "Graphics profile and mouse look for gameplay.", 22, new Vector2(0.5f, 0.80f), new Vector2(620f, 50f), font);
        settingsBody.color = new Color(0.8f, 0.84f, 0.91f, 1f);
        _settingsProfileText = CreateText(settingsCard.transform, "SettingsProfile", "", 22, new Vector2(0.5f, 0.70f), new Vector2(620f, 50f), font);
        _settingsProfileText.color = new Color(0.94f, 0.95f, 0.98f, 1f);
        _settingsPerformanceButton = CreateButton(settingsCard.transform, "PerformanceButton", "Performance", new Vector2(0.5f, 0.58f), new Color(0.4f, 0.24f, 0.18f, 1f), font);
        _settingsBalancedButton = CreateButton(settingsCard.transform, "BalancedButton", "Balanced", new Vector2(0.5f, 0.48f), new Color(0.23f, 0.42f, 0.3f, 1f), font);
        _settingsQualityButton = CreateButton(settingsCard.transform, "QualityButton", "Quality", new Vector2(0.5f, 0.38f), new Color(0.2f, 0.3f, 0.5f, 1f), font);
        _settingsMouseSensText = CreateText(settingsCard.transform, "MouseSensLabel", "", 22, new Vector2(0.5f, 0.28f), new Vector2(620f, 36f), font);
        _settingsMouseSensText.color = new Color(0.94f, 0.95f, 0.98f, 1f);
        _settingsMouseSensDownButton = CreateButton(settingsCard.transform, "MouseSensDown", "Mouse Sens -", new Vector2(0.32f, 0.18f), new Color(0.28f, 0.28f, 0.34f, 1f), font);
        _settingsMouseSensUpButton = CreateButton(settingsCard.transform, "MouseSensUp", "Mouse Sens +", new Vector2(0.68f, 0.18f), new Color(0.28f, 0.28f, 0.34f, 1f), font);
        _settingsBackButton = CreateButton(settingsCard.transform, "BackButton", "Back", new Vector2(0.5f, 0.06f), new Color(0.26f, 0.26f, 0.3f, 1f), font);
        _settingsPerformanceButton.onClick.AddListener(OnPerformancePresetClicked);
        _settingsBalancedButton.onClick.AddListener(OnBalancedPresetClicked);
        _settingsQualityButton.onClick.AddListener(OnQualityPresetClicked);
        _settingsMouseSensDownButton.onClick.AddListener(OnMouseSensDownClicked);
        _settingsMouseSensUpButton.onClick.AddListener(OnMouseSensUpClicked);
        _settingsBackButton.onClick.AddListener(OnSettingsBackClicked);

        BuildLoadingPanel(panel.transform, font);

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

        BuildCharacterSelectPanel(panel.transform, font);

        _menuPanel.SetActive(false);
        _settingsPanel.SetActive(false);
        _loadingPanel.SetActive(false);
        _gameOverPanel.SetActive(false);
        if (_characterSelectPanel != null)
            _characterSelectPanel.SetActive(false);
    }

    void BuildLoadingPanel(Transform parent, Font font)
    {
        _loadingPanel = CreateUiObject("LoadingPanel", parent);
        Stretch(_loadingPanel.GetComponent<RectTransform>());

        var loadTitle = CreateText(_loadingPanel.transform, "LoadingTitle", "STARGRAVE", 58, new Vector2(0.5f, 0.76f), new Vector2(900f, 90f), font);
        loadTitle.color = StargraveHudStyle.Cream;
        loadTitle.fontStyle = FontStyle.Bold;
        var titleOutline = loadTitle.gameObject.AddComponent<Outline>();
        titleOutline.effectColor = StargraveHudStyle.CardOutline;
        titleOutline.effectDistance = new Vector2(2f, -2f);

        _loadingTaglineText = CreateText(_loadingPanel.transform, "LoadingTagline", "BOOT SEQUENCE", 20, new Vector2(0.5f, 0.69f), new Vector2(700f, 34f), font);
        _loadingTaglineText.color = StargraveHudStyle.Swim;
        _loadingTaglineText.fontStyle = FontStyle.Bold;

        var stack = CreateUiObject("LoadingJokeStack", _loadingPanel.transform);
        _loadingJokeStackRt = stack.GetComponent<RectTransform>();
        _loadingJokeStackRt.anchorMin = new Vector2(0.5f, 0.54f);
        _loadingJokeStackRt.anchorMax = new Vector2(0.5f, 0.54f);
        _loadingJokeStackRt.pivot = new Vector2(0.5f, 0.5f);
        _loadingJokeStackRt.sizeDelta = new Vector2(920f, 170f);

        BuildLoadingJokeCard(stack.transform, "LoadingJokeCardBack", font, out _loadingJokeCardBackRt, out _loadingJokeCardBackGroup, out _loadingJokeCardBackText);
        BuildLoadingJokeCard(stack.transform, "LoadingJokeCardFront", font, out _loadingJokeCardFrontRt, out _loadingJokeCardFrontGroup, out _loadingJokeCardFrontText);
        SetLoadingCardPose(_loadingJokeCardBackRt, _loadingJokeCardBackGroup, LoadingCardBackPos, LoadingCardBackScale, LoadingCardBackRot, 0.72f);
        SetLoadingCardPose(_loadingJokeCardFrontRt, _loadingJokeCardFrontGroup, LoadingCardFrontPos, LoadingCardFrontScale, LoadingCardFrontRot, 1f);
        _loadingJokeCardFrontRt.SetAsLastSibling();

        var spinnerRoot = CreateUiObject("LoadingSpinner", _loadingPanel.transform);
        _loadingSpinnerRt = spinnerRoot.GetComponent<RectTransform>();
        _loadingSpinnerRt.anchorMin = new Vector2(0.5f, 0.38f);
        _loadingSpinnerRt.anchorMax = new Vector2(0.5f, 0.38f);
        _loadingSpinnerRt.pivot = new Vector2(0.5f, 0.5f);
        _loadingSpinnerRt.sizeDelta = new Vector2(58f, 58f);

        Sprite spinnerSprite = UiCircleSprite();
        var spinnerTrack = CreateUiObject("SpinnerTrack", spinnerRoot.transform);
        Stretch(spinnerTrack.GetComponent<RectTransform>());
        var trackImg = spinnerTrack.AddComponent<Image>();
        trackImg.sprite = spinnerSprite;
        trackImg.type = Image.Type.Simple;
        trackImg.color = new Color(0.14f, 0.13f, 0.17f, 0.95f);
        trackImg.raycastTarget = false;

        var spinnerArc = CreateUiObject("SpinnerArc", spinnerRoot.transform);
        Stretch(spinnerArc.GetComponent<RectTransform>());
        _loadingSpinnerArc = spinnerArc.AddComponent<Image>();
        _loadingSpinnerArc.sprite = spinnerSprite;
        _loadingSpinnerArc.type = Image.Type.Filled;
        _loadingSpinnerArc.fillMethod = Image.FillMethod.Radial360;
        _loadingSpinnerArc.fillOrigin = (int)Image.Origin360.Top;
        _loadingSpinnerArc.fillClockwise = true;
        _loadingSpinnerArc.fillAmount = 0.24f;
        _loadingSpinnerArc.color = StargraveHudStyle.Kills;
        _loadingSpinnerArc.raycastTarget = false;

        _loadingFooterText = CreateText(_loadingPanel.transform, "LoadingFooter", "Press nothing. We're doing the thing.", 18, new Vector2(0.5f, 0.28f), new Vector2(900f, 36f), font);
        _loadingFooterText.color = new Color(0.62f, 0.7f, 0.82f, 0.92f);
        _loadingFooterText.fontStyle = FontStyle.Italic;
    }

    void BuildLoadingJokeCard(Transform parent, string name, Font font, out RectTransform rt, out CanvasGroup group, out Text text)
    {
        var card = CreateUiObject(name, parent);
        rt = card.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(920f, 150f);
        rt.anchoredPosition = Vector2.zero;

        var cardImg = card.AddComponent<Image>();
        StargraveHudStyle.ApplyCard(cardImg, new Color(0.14f, 0.12f, 0.18f, 0.9f));

        group = card.AddComponent<CanvasGroup>();
        group.alpha = 1f;
        group.blocksRaycasts = false;
        group.interactable = false;

        text = CreateText(card.transform, "Text", "", 24, new Vector2(0.5f, 0.5f), new Vector2(860f, 120f), font);
        text.color = StargraveHudStyle.Cream;
        text.fontStyle = FontStyle.Italic;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
    }

    static Sprite UiCircleSprite()
    {
        Sprite sprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/Knob.psd");
        if (sprite != null)
            return sprite;
        return Resources.GetBuiltinResource<Sprite>("UI/Skin/UISprite.psd");
    }

    static void SetLoadingCardPose(RectTransform rt, CanvasGroup group, Vector2 pos, float scale, float rotZ, float alpha)
    {
        if (rt == null)
            return;
        rt.anchoredPosition = pos;
        rt.localScale = Vector3.one * scale;
        rt.localRotation = Quaternion.Euler(0f, 0f, rotZ);
        if (group != null)
            group.alpha = alpha;
    }

    static void LerpLoadingCardPose(
        RectTransform rt,
        CanvasGroup group,
        Vector2 fromPos,
        float fromScale,
        float fromRot,
        float fromAlpha,
        Vector2 toPos,
        float toScale,
        float toRot,
        float toAlpha,
        float t)
    {
        if (rt == null)
            return;
        rt.anchoredPosition = Vector2.Lerp(fromPos, toPos, t);
        float scale = Mathf.Lerp(fromScale, toScale, t);
        rt.localScale = Vector3.one * scale;
        float rot = Mathf.Lerp(fromRot, toRot, t);
        rt.localRotation = Quaternion.Euler(0f, 0f, rot);
        if (group != null)
            group.alpha = Mathf.Lerp(fromAlpha, toAlpha, t);
    }

    static float LoadingCardHalfHeight(float scale) => LoadingCardHeight * scale * 0.5f;

    static float FrontCardTopY() => LoadingCardFrontPos.y + LoadingCardHalfHeight(LoadingCardFrontScale);

    static bool IncomingCardClearsFront(Vector2 incomingPos, float incomingScale)
    {
        float incomingTop = incomingPos.y + LoadingCardHalfHeight(incomingScale);
        return incomingTop >= FrontCardTopY() + 4f;
    }

    static float EaseOutCubic(float t) => 1f - Mathf.Pow(1f - t, 3f);

    static float EaseInOutCubic(float t) =>
        t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) * 0.5f;

    void SwapLoadingJokeCards()
    {
        RectTransform rt = _loadingJokeCardFrontRt;
        CanvasGroup group = _loadingJokeCardFrontGroup;
        Text text = _loadingJokeCardFrontText;

        _loadingJokeCardFrontRt = _loadingJokeCardBackRt;
        _loadingJokeCardFrontGroup = _loadingJokeCardBackGroup;
        _loadingJokeCardFrontText = _loadingJokeCardBackText;

        _loadingJokeCardBackRt = rt;
        _loadingJokeCardBackGroup = group;
        _loadingJokeCardBackText = text;

        _loadingJokeCardFrontRt.SetAsLastSibling();
    }

    void BuildCharacterSelectPanel(Transform parent, Font font)
    {
        _characterSelectPanel = CreateUiObject("CharacterSelectPanel", parent);
        Stretch(_characterSelectPanel.GetComponent<RectTransform>());

        var dim = _characterSelectPanel.AddComponent<Image>();
        dim.color = MenuBackgroundColor;
        dim.raycastTarget = false;

        _characterSelect3D = _characterSelectPanel.AddComponent<CharacterSelect3DPanel>();
        _characterSelect3D.Build(_characterSelectPanel.transform);

        _characterContinueButton = CreateButton(_characterSelectPanel.transform, "ContinueButton", "Continue",
            new Vector2(0.5f, 0.14f), new Color(0.22f, 0.55f, 0.32f, 1f), font);
        _characterBackButton = CreateButton(_characterSelectPanel.transform, "BackButton", "Back",
            new Vector2(0.5f, 0.06f), new Color(0.28f, 0.28f, 0.32f, 1f), font);
        _characterContinueButton.onClick.AddListener(OnCharacterContinueClicked);
        _characterBackButton.onClick.AddListener(OnCharacterBackClicked);
    }

    void OnPlayClicked()
    {
        // Any Play → character select → Continue starts a fresh run.
        _runResetRequired = true;
        EnterCharacterSelectState();
    }
    void OnCharacterContinueClicked()
    {
        _runResetRequired = true;
        StartRun();
    }

    void OnCharacterBackClicked()
    {
        EnterMenuState(_runResetRequired);
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
        else if (_state == FrontendState.CharacterSelect)
        {
            AudioManager.PlayUiClick();
            EnterMenuState(_runResetRequired);
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

    void SetPlayButtonLabel(string label)
    {
        if (_menuPlayButton == null)
            return;
        Text text = _menuPlayButton.GetComponentInChildren<Text>(true);
        if (text != null)
            text.text = label;
    }

    void OnSettingsClicked()
    {
        RefreshSettingsUi();
        _settingsPanel.SetActive(true);
        _menuPanel.SetActive(false);
        SetBackgroundColor(MenuBackgroundColor);
        QueueFocusButton(GetPreferredSettingsButton());
    }

    void OnSettingsBackClicked()
    {
        bool paused = _state == FrontendState.Paused;
        _settingsPanel.SetActive(false);
        _menuPanel.SetActive(true);
        SetResumeButtonVisible(paused);
        if (paused)
            SetPlayButtonLabel("New Game");
        else
            SetPlayButtonLabel("Play");
        RefreshMenuSummary(_runResetRequired);
        if (paused && _menuSubtitleText != null)
            _menuSubtitleText.text = "Paused";
        SetBackgroundColor(MenuBackgroundColor);
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

    void OnMouseSensDownClicked()
    {
        float next = PlayerLookController.GetMouseSensitivityMultiplier() - 0.25f;
        PlayerLookController.SetMouseSensitivityMultiplier(next);
        RefreshSettingsUi();
    }

    void OnMouseSensUpClicked()
    {
        float next = PlayerLookController.GetMouseSensitivityMultiplier() + 0.25f;
        PlayerLookController.SetMouseSensitivityMultiplier(next);
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
        if (_characterSelectPanel != null)
            _characterSelectPanel.SetActive(false);
        if (_characterSelect3D != null)
            _characterSelect3D.SetActivePreviews(false);
        SetBackgroundColor(MenuBackgroundColor);
        _loadingJokeCardHasShown = false;
        _loadingJokeCardFrontText.text = string.Empty;
        _loadingJokeCardBackText.text = string.Empty;
        SetLoadingCardPose(_loadingJokeCardBackRt, _loadingJokeCardBackGroup, LoadingCardBackPos, LoadingCardBackScale, LoadingCardBackRot, 0.72f);
        SetLoadingCardPose(_loadingJokeCardFrontRt, _loadingJokeCardFrontGroup, LoadingCardBackPos, LoadingCardBackScale, LoadingCardBackRot, 0f);
        _loadingJokeCardBackRt.SetSiblingIndex(0);
        _loadingJokeCardFrontRt.SetAsLastSibling();
        SetLoadingStatus("Warming up the sarcasm engines...");
        StartLoadingTaglinePulse();
        StartLoadingSpinner();
    }

    void EnterMenuState(bool afterGameOver)
    {
        _state = FrontendState.Menu;
        DismissBlockingOverlay();
        ResolveRuntimeRefs();
        if (_player != null)
            _player.SetGameplayControlEnabled(false);
        if (_hud != null)
            _hud.SetHudVisible(false);

        SetPaused(true);
        StopLoadingTaglinePulse();
        StopLoadingSpinner();
        _loadingPanel.SetActive(false);
        _gameOverPanel.SetActive(false);
        _settingsPanel.SetActive(false);
        if (_characterSelectPanel != null)
            _characterSelectPanel.SetActive(false);
        if (_characterSelect3D != null)
            _characterSelect3D.SetActivePreviews(false);
        _menuPanel.SetActive(true);
        SetResumeButtonVisible(false);
        SetPlayButtonLabel("Play");
        SetBackgroundColor(MenuBackgroundColor);
        RefreshMenuSummary(afterGameOver);
        RefreshSettingsUi();
        QueueFocusButton(_menuPlayButton);
    }

    /// <summary>
    /// Pause an in-progress run: freeze gameplay (<see cref="SetPaused"/> sets <c>Time.timeScale = 0</c>), disable
    /// player control, and show the main-menu panel with the Resume button on a solid black background.
    /// Game state is untouched, so resuming continues exactly where the player left off.
    /// </summary>
    void EnterPausedState()
    {
        _state = FrontendState.Paused;
        DismissBlockingOverlay();
        ResolveRuntimeRefs();
        if (_player != null)
            _player.SetGameplayControlEnabled(false);
        if (_hud != null)
            _hud.SetHudVisible(false);

        SetPaused(true);
        StopLoadingTaglinePulse();
        StopLoadingSpinner();
        _loadingPanel.SetActive(false);
        _gameOverPanel.SetActive(false);
        _settingsPanel.SetActive(false);
        if (_characterSelectPanel != null)
            _characterSelectPanel.SetActive(false);
        if (_characterSelect3D != null)
            _characterSelect3D.SetActivePreviews(false);
        _menuPanel.SetActive(true);
        SetResumeButtonVisible(true);
        SetPlayButtonLabel("New Game");
        SetBackgroundColor(MenuBackgroundColor);
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
        if (_characterSelectPanel != null)
            _characterSelectPanel.SetActive(false);
        if (_characterSelect3D != null)
            _characterSelect3D.SetActivePreviews(false);
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
        DismissBlockingOverlay();
        ResolveRuntimeRefs();
        if (_hud != null)
            _hud.SetHudVisible(false);

        SetPaused(true);
        StopLoadingTaglinePulse();
        StopLoadingSpinner();
        _loadingPanel.SetActive(false);
        _settingsPanel.SetActive(false);
        _menuPanel.SetActive(false);
        if (_characterSelectPanel != null)
            _characterSelectPanel.SetActive(false);
        if (_characterSelect3D != null)
            _characterSelect3D.SetActivePreviews(false);
        _gameOverPanel.SetActive(true);
        SetBackgroundColor(new Color(0f, 0f, 0f, 0.94f));
        RefreshGameOverSummary();
        QueueFocusButton(_gameOverMenuButton);
    }

    void StartRun()
    {
        ResolveRuntimeRefs();
        ApplySelectedCharacterLoadout();
        if (GameStatsManager.Instance != null)
            GameStatsManager.Instance.ResetCurrentRunStats();

        // Every character Continue is a new run: new random location + fresh zombie population.
        if (_player != null)
        {
            SurfaceSpawner surface = _player.GetComponent<SurfaceSpawner>();
            if (surface != null)
                surface.RelocateToRandomSurface();
            _player.RespawnNow(true);
        }

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
        if (_characterSelectPanel != null)
            _characterSelectPanel.SetActive(false);
        if (_characterSelect3D != null)
            _characterSelect3D.SetActivePreviews(false);
        SetBackgroundColor(new Color(0f, 0f, 0f, 0f));
        SetPaused(false);
        if (_player != null)
            _player.SetGameplayControlEnabled(true);
        _state = FrontendState.Playing;
    }

    void ApplySelectedCharacterLoadout()
    {
        ResolveRuntimeRefs();
        if (_player == null)
            return;
        PlayerCharacterLoadout loadout = PlayerCharacterLoadout.EnsureOn(_player);
        if (loadout != null)
            loadout.ApplySelected();
    }

    void EnterCharacterSelectState()
    {
        _state = FrontendState.CharacterSelect;
        DismissBlockingOverlay();
        ResolveRuntimeRefs();
        if (_player != null)
            _player.SetGameplayControlEnabled(false);
        if (_hud != null)
            _hud.SetHudVisible(false);

        SetPaused(true);
        StopLoadingTaglinePulse();
        StopLoadingSpinner();
        _loadingPanel.SetActive(false);
        _gameOverPanel.SetActive(false);
        _settingsPanel.SetActive(false);
        _menuPanel.SetActive(false);
        if (_characterSelectPanel != null)
            _characterSelectPanel.SetActive(true);
        if (_characterSelect3D != null)
            _characterSelect3D.SetActivePreviews(true);
        SetBackgroundColor(MenuBackgroundColor);
        QueueFocusButton(_characterContinueButton);
    }

    IEnumerator CoBootWaitForPlanetThenAdvance()
    {
        float waited = 0f;
        Planet planet = FindFirstObjectByType<Planet>(FindObjectsInactive.Exclude);
        EnqueueJokeSet(SplashWhilePlanetBuilding, shuffle: true);

        while (planet != null && !planet.IsGenerated && waited < PlanetWaitTimeoutSeconds)
        {
            waited += Time.unscaledDeltaTime;
            yield return null;
        }

        if (planet != null && !planet.IsGenerated)
            SetLoadingStatus("Planet is shy - opening menu anyway. Bring snacks.");

        string[] finishing = CopyAndShuffle(SplashFinishingTouches);
        for (int i = 0; i < finishing.Length; i++)
            SetLoadingStatus(finishing[i]);

        SetLoadingStatus(PickRandomLine(SplashReadyLines));
        yield return CoWaitForLoadingStatusQueueIdle();
        StopLoadingTaglinePulse();
        StopLoadingSpinner();

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

    void EnqueueJokeSet(string[] lines, bool shuffle)
    {
        if (lines == null || lines.Length == 0)
            return;

        string[] ordered = shuffle ? CopyAndShuffle(lines) : (string[])lines.Clone();
        for (int i = 0; i < ordered.Length; i++)
            SetLoadingStatus(ordered[i]);
    }

    static string[] CopyAndShuffle(string[] source)
    {
        var copy = (string[])source.Clone();
        for (int i = copy.Length - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            string tmp = copy[i];
            copy[i] = copy[j];
            copy[j] = tmp;
        }

        return copy;
    }

    static string PickRandomLine(string[] lines)
    {
        if (lines == null || lines.Length == 0)
            return "Loading...";
        return lines[Random.Range(0, lines.Length)];
    }

    void StartLoadingTaglinePulse()
    {
        StopLoadingTaglinePulse();
        _loadingTaglinePulseCoroutine = StartCoroutine(CoLoadingTaglinePulse());
    }

    void StopLoadingTaglinePulse()
    {
        if (_loadingTaglinePulseCoroutine == null)
            return;
        StopCoroutine(_loadingTaglinePulseCoroutine);
        _loadingTaglinePulseCoroutine = null;
    }

    IEnumerator CoLoadingTaglinePulse()
    {
        const string baseText = "BOOT SEQUENCE";
        int dot = 0;
        while (true)
        {
            if (_loadingTaglineText != null)
                _loadingTaglineText.text = baseText + new string('.', dot + 1);
            dot = (dot + 1) % 3;
            yield return new WaitForSecondsRealtime(0.45f);
        }
    }

    void StartLoadingSpinner()
    {
        StopLoadingSpinner();
        _loadingSpinnerCoroutine = StartCoroutine(CoLoadingSpinnerSpin());
    }

    void StopLoadingSpinner()
    {
        if (_loadingSpinnerCoroutine == null)
            return;
        StopCoroutine(_loadingSpinnerCoroutine);
        _loadingSpinnerCoroutine = null;
    }

    IEnumerator CoLoadingSpinnerSpin()
    {
        while (true)
        {
            if (_loadingSpinnerRt != null)
                _loadingSpinnerRt.Rotate(0f, 0f, -220f * Time.unscaledDeltaTime);
            yield return null;
        }
    }

    void SetLoadingStatus(string message)
    {
        if (_loadingJokeCardFrontText == null)
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
            string quoted = $"\"{next}\"";

            _loadingJokeCardBackText.text = quoted;
            yield return CoDealLoadingJokeCard(buryPreviousFront: _loadingJokeCardHasShown);
            _loadingJokeCardHasShown = true;

            float dealSeconds = LoadingJokeCardLiftSeconds + LoadingJokeCardLandSeconds;
            float hold = Mathf.Max(0.35f, MinimumJokeSecondsPerLine - dealSeconds);
            yield return new WaitForSecondsRealtime(hold);
        }

        _lastQueuedLoadingStatus = null;
        _loadingStatusQueueCoroutine = null;
    }

    IEnumerator CoDealLoadingJokeCard(bool buryPreviousFront)
    {
        RectTransform incoming = _loadingJokeCardBackRt;
        CanvasGroup incomingGroup = _loadingJokeCardBackGroup;
        RectTransform top = _loadingJokeCardFrontRt;
        CanvasGroup topGroup = _loadingJokeCardFrontGroup;

        incoming.SetSiblingIndex(0);
        SetLoadingCardPose(incoming, incomingGroup, LoadingCardBackPos, LoadingCardBackScale, LoadingCardBackRot, buryPreviousFront ? 0.9f : 1f);

        if (buryPreviousFront)
            SetLoadingCardPose(top, topGroup, LoadingCardFrontPos, LoadingCardFrontScale, LoadingCardFrontRot, 1f);

        bool broughtToFront = !buryPreviousFront;
        if (broughtToFront)
        {
            incoming.SetAsLastSibling();
            if (incomingGroup != null)
                incomingGroup.alpha = 1f;
        }

        float elapsed = 0f;
        while (elapsed < LoadingJokeCardLiftSeconds)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = EaseOutCubic(Mathf.Clamp01(elapsed / LoadingJokeCardLiftSeconds));
            Vector2 pos = Vector2.Lerp(LoadingCardBackPos, LoadingCardPeekPos, t);
            float scale = Mathf.Lerp(LoadingCardBackScale, LoadingCardPeekScale, t);
            float rot = Mathf.Lerp(LoadingCardBackRot, LoadingCardPeekRot, t);
            float alpha = Mathf.Lerp(buryPreviousFront ? 0.9f : 1f, 1f, t);
            SetLoadingCardPose(incoming, incomingGroup, pos, scale, rot, alpha);

            if (!broughtToFront && IncomingCardClearsFront(pos, scale))
            {
                incoming.SetAsLastSibling();
                broughtToFront = true;
                if (incomingGroup != null)
                    incomingGroup.alpha = 1f;
            }

            yield return null;
        }

        if (!broughtToFront)
        {
            incoming.SetAsLastSibling();
            if (incomingGroup != null)
                incomingGroup.alpha = 1f;
        }

        SetLoadingCardPose(incoming, incomingGroup, LoadingCardPeekPos, LoadingCardPeekScale, LoadingCardPeekRot, 1f);

        elapsed = 0f;
        while (elapsed < LoadingJokeCardLandSeconds)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = EaseInOutCubic(Mathf.Clamp01(elapsed / LoadingJokeCardLandSeconds));
            LerpLoadingCardPose(
                incoming,
                incomingGroup,
                LoadingCardPeekPos,
                LoadingCardPeekScale,
                LoadingCardPeekRot,
                1f,
                LoadingCardFrontPos,
                LoadingCardFrontScale,
                LoadingCardFrontRot,
                1f,
                t);

            if (buryPreviousFront)
            {
                LerpLoadingCardPose(
                    top,
                    topGroup,
                    LoadingCardFrontPos,
                    LoadingCardFrontScale,
                    LoadingCardFrontRot,
                    1f,
                    LoadingCardBackPos,
                    LoadingCardBackScale,
                    LoadingCardBackRot,
                    0.72f,
                    t);
            }

            yield return null;
        }

        SetLoadingCardPose(incoming, incomingGroup, LoadingCardFrontPos, LoadingCardFrontScale, LoadingCardFrontRot, 1f);
        if (buryPreviousFront)
            SetLoadingCardPose(top, topGroup, LoadingCardBackPos, LoadingCardBackScale, LoadingCardBackRot, 0.72f);

        SwapLoadingJokeCards();
        _loadingJokeCardBackText.text = string.Empty;
        SetLoadingCardPose(_loadingJokeCardBackRt, _loadingJokeCardBackGroup, LoadingCardBackPos, LoadingCardBackScale, LoadingCardBackRot, 0.72f);
        _loadingJokeCardFrontRt.SetAsLastSibling();
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
        _menuStatsText.text = stats != null ? stats.HighKillScore.ToString() : "0";
    }

    void RefreshSettingsUi()
    {
        if (_settingsProfileText != null)
        {
            PerformancePresetBootstrap.GraphicsProfile profile = PerformancePresetBootstrap.GetCurrentProfile();
            string description = "default balance between frame rate and shell polish";
            if (profile == PerformancePresetBootstrap.GraphicsProfile.Performance)
                description = "lighter shadows, stricter zombie AI budgets";
            else if (profile == PerformancePresetBootstrap.GraphicsProfile.Quality)
                description = "richer shadows, looser zombie AI budgets";
            _settingsProfileText.text = $"Current Profile  {profile}\n{description}";
        }

        if (_settingsMouseSensText != null)
        {
            float sens = PlayerLookController.GetMouseSensitivityMultiplier();
            _settingsMouseSensText.text = $"Mouse Sensitivity  {sens:0.00}x";
        }
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
        if (_characterSelectPanel != null && _characterSelectPanel.activeInHierarchy)
            return _characterContinueButton;
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

    static Text BuildHighKillCard(Transform parent, Font font)
    {
        var card = CreateUiObject("HighKillCard", parent);
        var rt = card.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.56f);
        rt.anchorMax = new Vector2(0.5f, 0.56f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(280f, 96f);

        var bg = card.AddComponent<Image>();
        StargraveHudStyle.ApplyCard(bg, StargraveHudStyle.CardFill);

        var layout = card.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(16, 16, 10, 10);
        layout.spacing = 2f;
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        var labelGo = CreateUiObject("Label", card.transform);
        var labelLe = labelGo.AddComponent<LayoutElement>();
        labelLe.preferredHeight = 28f;
        labelLe.minHeight = 28f;
        var label = labelGo.AddComponent<Text>();
        label.font = font;
        label.fontSize = 20;
        label.fontStyle = FontStyle.Bold;
        label.alignment = TextAnchor.MiddleCenter;
        label.raycastTarget = false;
        label.color = StargraveHudStyle.Kills;
        label.text = "HIGH KILLS";
        var labelOutline = labelGo.AddComponent<Outline>();
        labelOutline.effectColor = StargraveHudStyle.CardOutline;
        labelOutline.effectDistance = new Vector2(1.25f, -1.25f);

        var valueGo = CreateUiObject("Value", card.transform);
        var valueLe = valueGo.AddComponent<LayoutElement>();
        valueLe.preferredHeight = 40f;
        valueLe.minHeight = 36f;
        var value = valueGo.AddComponent<Text>();
        value.font = font;
        value.fontSize = 36;
        value.fontStyle = FontStyle.Bold;
        value.alignment = TextAnchor.MiddleCenter;
        value.raycastTarget = false;
        value.color = StargraveHudStyle.Cream;
        value.text = "0";
        var valueOutline = valueGo.AddComponent<Outline>();
        valueOutline.effectColor = StargraveHudStyle.CardOutline;
        valueOutline.effectDistance = new Vector2(1.25f, -1.25f);
        return value;
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
        rt.sizeDelta = new Vector2(320f, 58f);
        rt.anchoredPosition = Vector2.zero;

        var image = go.AddComponent<Image>();
        var button = go.AddComponent<Button>();
        var navigation = button.navigation;
        navigation.mode = Navigation.Mode.Automatic;
        button.navigation = navigation;

        // Every menu button clicks on press (plays even while paused via AudioManager.PlayUi -> ignoreListenerPause).
        button.onClick.AddListener(() => AudioManager.PlayUiClick());

        var text = CreateText(go.transform, "Text", label, 26, new Vector2(0.5f, 0.5f), rt.sizeDelta, font);
        StargraveHudStyle.ApplyMenuButton(image, button, text, color);
        go.AddComponent<FrontendButtonScaleFx>();
        return button;
    }
}

/// <summary>Shared menu-button hover scale + bold label (main menu, settings, carousel chevrons).</summary>
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
