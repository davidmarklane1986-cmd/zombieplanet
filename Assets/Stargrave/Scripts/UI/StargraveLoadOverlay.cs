using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Full-screen loading UI on a DontDestroyOnLoad root. Used when transitioning from the main menu into the game
/// (or any async <see cref="SceneManager.LoadSceneAsync"/> with progress).
/// </summary>
public sealed class StargraveLoadOverlay : MonoBehaviour
{
    public static StargraveLoadOverlay Instance { get; private set; }

    [SerializeField] Canvas _canvas;
    [SerializeField] CanvasGroup _group;
    [SerializeField] Image _fill;
    [SerializeField] Text _statusText;

    bool _busy;

    public static StargraveLoadOverlay Ensure()
    {
        if (Instance != null)
            return Instance;

        var go = new GameObject("Stargrave_LoadOverlay");
        DontDestroyOnLoad(go);
        var comp = go.AddComponent<StargraveLoadOverlay>();
        comp.BuildRuntimeUi();
        return comp;
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    void BuildRuntimeUi()
    {
        _canvas = gameObject.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 50000;

        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        gameObject.AddComponent<GraphicRaycaster>();

        _group = gameObject.AddComponent<CanvasGroup>();
        _group.blocksRaycasts = true;
        _group.interactable = true;

        var panel = CreateUiObject("Panel", transform);
        var panelRt = panel.GetComponent<RectTransform>();
        StretchFullScreen(panelRt);
        var panelImg = panel.AddComponent<Image>();
        panelImg.color = new Color(0.02f, 0.02f, 0.06f, 0.94f);

        var barBg = CreateUiObject("BarBackground", panel.transform);
        var barBgRt = barBg.GetComponent<RectTransform>();
        barBgRt.anchorMin = new Vector2(0.5f, 0.44f);
        barBgRt.anchorMax = new Vector2(0.5f, 0.44f);
        barBgRt.pivot = new Vector2(0.5f, 0.5f);
        barBgRt.sizeDelta = new Vector2(560f, 26f);
        barBgRt.anchoredPosition = Vector2.zero;
        var barBgImg = barBg.AddComponent<Image>();
        barBgImg.color = new Color(0.12f, 0.12f, 0.14f, 1f);

        var barFill = CreateUiObject("BarFill", barBg.transform);
        var fillRt = barFill.GetComponent<RectTransform>();
        fillRt.anchorMin = Vector2.zero;
        fillRt.anchorMax = Vector2.one;
        fillRt.offsetMin = new Vector2(3f, 3f);
        fillRt.offsetMax = new Vector2(-3f, -3f);
        _fill = barFill.AddComponent<Image>();
        _fill.color = new Color(0.28f, 0.62f, 0.95f, 1f);
        _fill.type = Image.Type.Filled;
        _fill.fillMethod = Image.FillMethod.Horizontal;
        _fill.fillOrigin = (int)Image.OriginHorizontal.Left;
        _fill.fillAmount = 0.12f;

        var title = CreateUiObject("Title", panel.transform);
        var titleRt = title.GetComponent<RectTransform>();
        titleRt.anchorMin = new Vector2(0.5f, 0.68f);
        titleRt.anchorMax = new Vector2(0.5f, 0.68f);
        titleRt.sizeDelta = new Vector2(800f, 90f);
        titleRt.anchoredPosition = Vector2.zero;
        var titleText = title.AddComponent<Text>();
        titleText.font = BuiltinUIFont();
        titleText.fontSize = 52;
        titleText.alignment = TextAnchor.MiddleCenter;
        titleText.color = Color.white;
        titleText.text = "STARGRAVE";

        var status = CreateUiObject("Status", panel.transform);
        var statusRt = status.GetComponent<RectTransform>();
        statusRt.anchorMin = new Vector2(0.5f, 0.56f);
        statusRt.anchorMax = new Vector2(0.5f, 0.56f);
        statusRt.sizeDelta = new Vector2(1080f, 120f);
        statusRt.anchoredPosition = Vector2.zero;
        _statusText = status.AddComponent<Text>();
        _statusText.font = BuiltinUIFont();
        _statusText.fontSize = 22;
        _statusText.alignment = TextAnchor.MiddleCenter;
        _statusText.color = new Color(0.95f, 0.95f, 0.98f, 1f);
        _statusText.text = string.Empty;

        HideImmediate();
    }

    static Font BuiltinUIFont()
    {
        Font f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (f == null)
            f = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return f;
    }

    static GameObject CreateUiObject(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go;
    }

    static void StretchFullScreen(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    public void LoadSceneAsync(string sceneName)
    {
        if (_busy)
            return;
        StartCoroutine(CoLoad(sceneName));
    }

    IEnumerator CoLoad(string sceneName)
    {
        _busy = true;
        ShowImmediate();
        SetProgress(0.12f, "Loading...");

        var op = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
        if (op == null)
        {
            Debug.LogError($"StargraveLoadOverlay: could not start load for scene '{sceneName}'. Add it to File > Build Profiles > Scenes In Build.");
            HideImmediate();
            _busy = false;
            yield break;
        }

        op.allowSceneActivation = false;
        while (op.progress < 0.9f)
        {
            float t = Mathf.Clamp01(op.progress / 0.9f);
            string status =
                t < 0.45f ? "Planet mesh & collider: locked in. Your boots have opinions." :
                t < 0.7f ? "Water shell, sky candy, lights - the wet glam squad." :
                t < 0.92f ? "GPU sync - asking silicon to hold our juice boxes." :
                "Ready. Remember: the ground is down-ish.";
            SetProgress(Mathf.Lerp(0.12f, 0.98f, t), status);
            yield return null;
        }

        SetProgress(1f, "Ready. Remember: the ground is down-ish.");
        op.allowSceneActivation = true;
        yield return op;

        HideImmediate();
        _busy = false;
    }

    void SetProgress(float t, string status)
    {
        if (_fill != null)
            _fill.fillAmount = Mathf.Clamp01(t);
        if (_statusText != null)
            _statusText.text = status;
    }

    void ShowImmediate()
    {
        if (_group != null)
        {
            _group.alpha = 1f;
            _group.blocksRaycasts = true;
            _group.interactable = true;
        }
    }

    void HideImmediate()
    {
        if (_group != null)
        {
            _group.alpha = 0f;
            _group.blocksRaycasts = false;
            _group.interactable = false;
        }

        if (_fill != null)
            _fill.fillAmount = 0f;
        if (_statusText != null)
            _statusText.text = string.Empty;
    }
}
