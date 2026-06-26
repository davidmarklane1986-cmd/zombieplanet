using UnityEngine;

/// <summary>
/// Persistent run statistics for the Stargrave loop. Tracks current kills plus high kills and furthest distance
/// across runs via PlayerPrefs.
/// </summary>
public class GameStatsManager : MonoBehaviour
{
    const string HighKillScoreKey = "HighKillScore";
    const string HighDistanceMetersKey = "HighDistanceMeters";

    public static GameStatsManager Instance { get; private set; }
    public static event System.Action<int, int> ScoresChanged;

    public int CurrentKills { get; private set; }
    public int HighKillScore { get; private set; }
    public float HighDistanceMeters { get; private set; }

    float _lastSavedHighDistanceMeters;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Bootstrap()
    {
        if (Instance != null)
            return;

        var root = new GameObject("GameStatsManager");
        root.AddComponent<GameStatsManager>();
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        HighKillScore = PlayerPrefs.GetInt(HighKillScoreKey, 0);
        HighDistanceMeters = Mathf.Max(0f, PlayerPrefs.GetFloat(HighDistanceMetersKey, 0f));
        _lastSavedHighDistanceMeters = HighDistanceMeters;
        RaiseScoresChanged();
    }

    void OnEnable()
    {
        ZombieAI.ZombieKilled += OnZombieKilled;
    }

    void OnDisable()
    {
        ZombieAI.ZombieKilled -= OnZombieKilled;
    }

    void OnApplicationPause(bool paused)
    {
        if (paused)
            SaveHighDistance();
    }

    void OnApplicationQuit()
    {
        SaveHighDistance();
    }

    void OnZombieKilled()
    {
        CurrentKills++;
        if (CurrentKills > HighKillScore)
        {
            HighKillScore = CurrentKills;
            PlayerPrefs.SetInt(HighKillScoreKey, HighKillScore);
            PlayerPrefs.Save();
        }

        RaiseScoresChanged();
    }

    public void ResetCurrentRunStats()
    {
        CurrentKills = 0;
        RaiseScoresChanged();
    }

    public void ReportDistanceTravelled(float meters)
    {
        if (meters <= HighDistanceMeters)
            return;

        HighDistanceMeters = Mathf.Max(0f, meters);
        if (HighDistanceMeters - _lastSavedHighDistanceMeters >= 5f)
            SaveHighDistance();
    }

    void SaveHighDistance()
    {
        if (HighDistanceMeters <= _lastSavedHighDistanceMeters)
            return;

        _lastSavedHighDistanceMeters = HighDistanceMeters;
        PlayerPrefs.SetFloat(HighDistanceMetersKey, HighDistanceMeters);
        PlayerPrefs.Save();
    }

    void RaiseScoresChanged()
    {
        ScoresChanged?.Invoke(CurrentKills, HighKillScore);
    }
}
