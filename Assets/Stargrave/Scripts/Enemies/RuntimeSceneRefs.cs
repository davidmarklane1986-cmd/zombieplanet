using UnityEngine;

/// <summary>
/// Lightweight cache for common scene lookups (player, camera).
/// When multiple objects share the "Player" tag (e.g. Cinemachine "CM_*" rigs), picks the real body (motor / rigidbody), not the camera.
/// </summary>
public static class RuntimeSceneRefs
{
    static Transform _cachedPlayer;
    static Camera _cachedMainCamera;
    static float _nextPlayerLookupTime;
    static float _nextCameraLookupTime;

    public static Transform GetPlayerTransform(float retryIntervalSeconds = 0.5f)
    {
        if (_cachedPlayer != null && _cachedPlayer.gameObject != null)
            return _cachedPlayer;
        _cachedPlayer = null;

        if (Time.time < _nextPlayerLookupTime)
            return null;

        _cachedPlayer = ResolveBestPlayerTaggedTransform();
        _nextPlayerLookupTime = Time.time + Mathf.Max(0.05f, retryIntervalSeconds);
        return _cachedPlayer;
    }

    /// <summary>Chooses the best "Player"-tagged transform when several exist (common with Cinemachine helpers).</summary>
    public static Transform ResolveBestPlayerTaggedTransform()
    {
        GameObject[] tagged = GameObject.FindGameObjectsWithTag("Player");
        if (tagged == null || tagged.Length == 0)
            return null;
        if (tagged.Length == 1)
            return tagged[0].transform;

        Transform best = null;
        int bestScore = int.MinValue;
        for (int i = 0; i < tagged.Length; i++)
        {
            GameObject go = tagged[i];
            if (go == null)
                continue;
            int score = ScorePlayerTaggedObject(go);
            if (score > bestScore)
            {
                bestScore = score;
                best = go.transform;
            }
        }
        return best;
    }

    static int ScorePlayerTaggedObject(GameObject go)
    {
        int s = 0;
        string n = go.name;
        if (n.StartsWith("CM_", System.StringComparison.Ordinal) || n.IndexOf("Cinemachine", System.StringComparison.OrdinalIgnoreCase) >= 0)
            s -= 400;
        if (go.GetComponent<Camera>() != null)
            s -= 120;
        if (go.GetComponent<AudioListener>() != null)
            s -= 40;

        if (go.GetComponent<PlanetMotor_InputSystem>() != null)
            s += 500;
        else if (go.GetComponentInParent<PlanetMotor_InputSystem>() != null)
            s += 350;

        if (go.GetComponent<Rigidbody>() != null)
            s += 200;
        if (go.GetComponent<CapsuleCollider>() != null)
            s += 80;
        if (go.GetComponent<GravityBody>() != null)
            s += 60;

        return s;
    }

    public static Camera GetMainCamera(float retryIntervalSeconds = 0.5f)
    {
        if (_cachedMainCamera != null)
            return _cachedMainCamera;
        if (Time.time < _nextCameraLookupTime)
            return null;

        _cachedMainCamera = Camera.main;
        _nextCameraLookupTime = Time.time + Mathf.Max(0.05f, retryIntervalSeconds);
        return _cachedMainCamera;
    }

    public static void InvalidatePlayer()
    {
        _cachedPlayer = null;
        _nextPlayerLookupTime = 0f;
    }

    public static void InvalidateMainCamera()
    {
        _cachedMainCamera = null;
        _nextCameraLookupTime = 0f;
    }
}
