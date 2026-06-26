using UnityEngine;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Main menu: Play runs async load into the configured game scene via <see cref="StargraveLoadOverlay"/>.
/// </summary>
public sealed class StargraveMainMenuController : MonoBehaviour
{
    [Tooltip("Scene name as listed in Build Settings (e.g. SampleScene).")]
    public string gameSceneName = "SampleScene";

    [SerializeField] Button _playButton;
    [SerializeField] Button _quitButton;

    void Awake()
    {
        AutoWireButtonsIfMissing();
    }

    void OnEnable()
    {
        if (_playButton != null)
            _playButton.onClick.AddListener(OnPlayClicked);
        if (_quitButton != null)
            _quitButton.onClick.AddListener(OnQuitClicked);
    }

    void OnDisable()
    {
        if (_playButton != null)
            _playButton.onClick.RemoveListener(OnPlayClicked);
        if (_quitButton != null)
            _quitButton.onClick.RemoveListener(OnQuitClicked);
    }

    void AutoWireButtonsIfMissing()
    {
        if (_playButton == null || _quitButton == null)
        {
            foreach (var b in GetComponentsInChildren<Button>(true))
            {
                string n = b.gameObject.name;
                if (_playButton == null && n.IndexOf("Play", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    _playButton = b;
                if (_quitButton == null && n.IndexOf("Quit", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    _quitButton = b;
            }
        }
    }

    public void OnPlayClicked()
    {
        if (string.IsNullOrWhiteSpace(gameSceneName))
        {
            Debug.LogError("StargraveMainMenuController: assign gameSceneName.");
            return;
        }

        StargraveFrontendBootstrap.AutoStartNextBoot();
        StargraveLoadOverlay.Ensure().LoadSceneAsync(gameSceneName.Trim());
    }

    public void OnQuitClicked()
    {
#if UNITY_EDITOR
        EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}
