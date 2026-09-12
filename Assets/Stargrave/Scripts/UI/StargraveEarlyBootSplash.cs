using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Tiny DontDestroyOnLoad splash created at BeforeSceneLoad so the first painted frame
/// already shows loading jokes — before Planet.GeneratePlanet / faction boot freeze the main thread.
/// <see cref="StargraveFrontendBootstrap"/> dismisses this once its own loading UI is live.
/// </summary>
public sealed class StargraveEarlyBootSplash : MonoBehaviour
{
    public static StargraveEarlyBootSplash Instance { get; private set; }

    static readonly string[] EarlyJokes =
    {
        "Warming up the sarcasm engines...",
        "Convincing Unity to draw something before the planet eats the frame.",
        "Sculpting a round world - flat maps are still wrong, sorry.",
        "Loading Pwee... Unpredictable with a chance of denial.",
        "Jamie Wingfield calls it 'average height.' Everyone else calls it altitude.",
        "Teaching triangles which way is up.",
        "Please hold - factions are arguing about spawn sites."
    };

    Text _status;
    Coroutine _loop;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Install()
    {
        if (Instance != null)
            return;
        var go = new GameObject("Stargrave_EarlyBootSplash");
        DontDestroyOnLoad(go);
        go.AddComponent<StargraveEarlyBootSplash>();
    }

    public static void Dismiss()
    {
        if (Instance == null)
            return;
        Destroy(Instance.gameObject);
        Instance = null;
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        BuildUi();
        _loop = StartCoroutine(CoJokeLoop());
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    void BuildUi()
    {
        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 52000;

        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        gameObject.AddComponent<GraphicRaycaster>();

        var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(transform, false);
        var panelRt = panel.GetComponent<RectTransform>();
        panelRt.anchorMin = Vector2.zero;
        panelRt.anchorMax = Vector2.one;
        panelRt.offsetMin = Vector2.zero;
        panelRt.offsetMax = Vector2.zero;
        panel.GetComponent<Image>().color = new Color(0.02f, 0.02f, 0.06f, 0.96f);

        var titleGo = new GameObject("Title", typeof(RectTransform), typeof(Text));
        titleGo.transform.SetParent(panel.transform, false);
        var titleRt = titleGo.GetComponent<RectTransform>();
        titleRt.anchorMin = new Vector2(0.5f, 0.62f);
        titleRt.anchorMax = new Vector2(0.5f, 0.62f);
        titleRt.sizeDelta = new Vector2(900f, 90f);
        var title = titleGo.GetComponent<Text>();
        title.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (title.font == null)
            title.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        title.fontSize = 52;
        title.alignment = TextAnchor.MiddleCenter;
        title.color = Color.white;
        title.text = "STARGRAVE";

        var statusGo = new GameObject("Status", typeof(RectTransform), typeof(Text));
        statusGo.transform.SetParent(panel.transform, false);
        var statusRt = statusGo.GetComponent<RectTransform>();
        statusRt.anchorMin = new Vector2(0.5f, 0.42f);
        statusRt.anchorMax = new Vector2(0.5f, 0.42f);
        statusRt.sizeDelta = new Vector2(1100f, 140f);
        _status = statusGo.GetComponent<Text>();
        _status.font = title.font;
        _status.fontSize = 22;
        _status.alignment = TextAnchor.MiddleCenter;
        _status.color = new Color(0.95f, 0.95f, 0.98f, 1f);
        _status.text = $"\"{EarlyJokes[0]}\"";

        var footGo = new GameObject("Footer", typeof(RectTransform), typeof(Text));
        footGo.transform.SetParent(panel.transform, false);
        var footRt = footGo.GetComponent<RectTransform>();
        footRt.anchorMin = new Vector2(0.5f, 0.22f);
        footRt.anchorMax = new Vector2(0.5f, 0.22f);
        footRt.sizeDelta = new Vector2(800f, 40f);
        var foot = footGo.GetComponent<Text>();
        foot.font = title.font;
        foot.fontSize = 18;
        foot.alignment = TextAnchor.MiddleCenter;
        foot.color = new Color(0.7f, 0.75f, 0.85f, 1f);
        foot.text = "BOOT SEQUENCE...";
    }

    IEnumerator CoJokeLoop()
    {
        int i = 0;
        while (true)
        {
            if (_status != null)
                _status.text = $"\"{EarlyJokes[i % EarlyJokes.Length]}\"";
            i++;
            yield return new WaitForSecondsRealtime(1.55f);
        }
    }
}
