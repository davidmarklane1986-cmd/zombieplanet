using UnityEngine;

/// <summary>
/// Resolves the active planet transform (SebLague <see cref="Planet"/> project).
/// </summary>
public static class PlanetReferenceResolver
{
    public static Transform ResolvePlanetTransform()
    {
        GameObject tagged = GameObject.FindGameObjectWithTag("Planet");
        if (tagged != null)
            return tagged.transform;

        Planet p = Object.FindAnyObjectByType<Planet>();
        return p != null ? p.transform : null;
    }
}
