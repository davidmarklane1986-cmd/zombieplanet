#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

/// <summary>
/// One-shot setup: builds a compatibility <c>MainMenu.unity</c> and registers the one-scene loop
/// with the gameplay scene first.
/// </summary>
public static class StargraveMenuSceneSetup
{
    const string MenuScenePath = "Assets/Stargrave/Scenes/MainMenu.unity";
    const string DefaultGameScenePath = "Assets/Scenes/SampleScene.unity";

    [MenuItem("Tools/Stargrave/Setup Main Menu + Build Order")]
    public static void SetupMainMenuAndBuildOrder()
    {
        EnsureFolder("Assets/Stargrave/Scenes");

        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        var camera = Object.FindFirstObjectByType<Camera>();
        if (camera != null)
        {
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.04f, 0.05f, 0.09f, 1f);
        }

        var eventGo = new GameObject("EventSystem");
        eventGo.AddComponent<EventSystem>();
        eventGo.AddComponent<InputSystemUIInputModule>();

        var canvasGo = new GameObject("Canvas");
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        canvasGo.AddComponent<GraphicRaycaster>();

        var panel = CreateUiObject("Panel", canvasGo.transform);
        Stretch(panel.GetComponent<RectTransform>());
        var panelImg = panel.AddComponent<Image>();
        panelImg.color = new Color(0.04f, 0.05f, 0.08f, 0.55f);

        Font font = BuiltinUIFont();

        var title = CreateUiObject("Title", panel.transform);
        Place(title.GetComponent<RectTransform>(), new Vector2(0.5f, 0.72f), new Vector2(900, 72), Vector2.zero);
        var titleText = title.AddComponent<Text>();
        titleText.font = font;
        titleText.fontSize = 52;
        titleText.alignment = TextAnchor.MiddleCenter;
        titleText.color = new Color(0.95f, 0.96f, 1f, 1f);
        titleText.text = "Stargrave";

        var subtitle = CreateUiObject("Subtitle", panel.transform);
        Place(subtitle.GetComponent<RectTransform>(), new Vector2(0.5f, 0.64f), new Vector2(900, 40), Vector2.zero);
        var subText = subtitle.AddComponent<Text>();
        subText.font = font;
        subText.fontSize = 22;
        subText.alignment = TextAnchor.MiddleCenter;
        subText.color = new Color(0.7f, 0.76f, 0.86f, 1f);
        subText.text = "Main menu";

        Button play = CreateMenuButton(panel.transform, "PlayButton", "Play", new Vector2(0.5f, 0.42f), new Vector2(300, 56), new Color(0.22f, 0.55f, 0.32f, 1f), font);
        Button quit = CreateMenuButton(panel.transform, "QuitButton", "Quit", new Vector2(0.5f, 0.32f), new Vector2(300, 56), new Color(0.45f, 0.22f, 0.22f, 1f), font);

        var menuRoot = new GameObject("Stargrave_MainMenu");
        var menu = menuRoot.AddComponent<StargraveMainMenuController>();
        menu.gameSceneName = "SampleScene";

        var so = new SerializedObject(menu);
        so.FindProperty("_playButton").objectReferenceValue = play;
        so.FindProperty("_quitButton").objectReferenceValue = quit;
        so.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.SaveScene(scene, MenuScenePath);
        RegisterBuildScenes();
        EditorSceneManager.playModeStartScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(DefaultGameScenePath);
        AssetDatabase.Refresh();
        Debug.Log($"Stargrave: saved compatibility menu to {MenuScenePath}. Play mode now starts from {DefaultGameScenePath} for the 1.3-style one-scene loop.");
    }

    static Font BuiltinUIFont()
    {
        Font f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (f == null)
            f = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return f;
    }

    static void RegisterBuildScenes()
    {
        var game = new EditorBuildSettingsScene(DefaultGameScenePath, true);
        var menu = new EditorBuildSettingsScene(MenuScenePath, false);
        var merged = new List<EditorBuildSettingsScene> { game, menu };
        if (EditorBuildSettings.scenes != null)
        {
            foreach (var s in EditorBuildSettings.scenes)
            {
                if (s == null)
                    continue;
                if (s.path == MenuScenePath || s.path == DefaultGameScenePath)
                    continue;
                merged.Add(s);
            }
        }

        EditorBuildSettings.scenes = merged.ToArray();
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
            return;
        string parent = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
        string leaf = System.IO.Path.GetFileName(path);
        if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent ?? "Assets", leaf);
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

    static void Place(RectTransform rt, Vector2 anchorCenter, Vector2 size, Vector2 anchoredPos)
    {
        rt.anchorMin = anchorCenter;
        rt.anchorMax = anchorCenter;
        rt.pivot = anchorCenter;
        rt.sizeDelta = size;
        rt.anchoredPosition = anchoredPos;
    }

    static Button CreateMenuButton(Transform parent, string objectName, string label, Vector2 anchor, Vector2 size, Color color, Font font)
    {
        var go = CreateUiObject(objectName, parent);
        Place(go.GetComponent<RectTransform>(), anchor, size, Vector2.zero);
        var img = go.AddComponent<Image>();
        img.color = color;
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;

        var textGo = CreateUiObject("Text", go.transform);
        Stretch(textGo.GetComponent<RectTransform>());
        var text = textGo.AddComponent<Text>();
        text.font = font;
        text.fontSize = 26;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        text.text = label;
        return btn;
    }
}
#endif
