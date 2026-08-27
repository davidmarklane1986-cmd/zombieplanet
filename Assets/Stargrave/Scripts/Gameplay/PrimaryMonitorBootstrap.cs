using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Standalone player: move the game window onto the Windows primary monitor at launch.
/// Unity otherwise remembers the last display (often a secondary screen after Build And Run).
/// </summary>
public static class PrimaryMonitorBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
    static void ApplyBeforeSplash()
    {
        MoveToPrimaryMonitor();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void ApplyAfterScene()
    {
        MoveToPrimaryMonitor();
    }

    static void MoveToPrimaryMonitor()
    {
#if UNITY_EDITOR
        return;
#else
        var displays = new List<DisplayInfo>(4);
        Screen.GetDisplayLayout(displays);
        if (displays.Count == 0)
            return;

        DisplayInfo primary = displays[0];
        int best = int.MaxValue;
        for (int i = 0; i < displays.Count; i++)
        {
            // Windows primary desktop origin is (0,0). Secondaries are offset (often negative X).
            int score = Mathf.Abs(displays[i].workArea.x) + Mathf.Abs(displays[i].workArea.y);
            if (score < best)
            {
                best = score;
                primary = displays[i];
            }
        }

        PlayerPrefs.SetInt("UnitySelectMonitor", 0);
        Screen.MoveMainWindowTo(primary, Vector2Int.zero);
        int w = Mathf.Max(640, primary.width);
        int h = Mathf.Max(480, primary.height);
        Screen.SetResolution(w, h, FullScreenMode.FullScreenWindow);
#endif
    }
}
