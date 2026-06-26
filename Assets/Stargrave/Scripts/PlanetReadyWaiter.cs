using UnityEngine;
using System.Collections;

/// <summary>
/// Keeps this GameObject disabled until the planet has finished generating.
/// Add to NPCs, relics, or any asset that should appear only after the planet is ready.
/// </summary>
public class PlanetReadyWaiter : MonoBehaviour
{
    void Awake()
    {
        gameObject.SetActive(false);
    }

    void Start()
    {
        StartCoroutine(WaitThenEnable());
    }

    IEnumerator WaitThenEnable()
    {

        var planet = Object.FindFirstObjectByType<Planet>();
        if (planet != null)
        {
            while (!planet.IsGenerated)
                yield return null;
            yield return null; // Extra frame for physics
        }
        else
        {
            yield return new WaitForSeconds(0.5f);
        }

        gameObject.SetActive(true);
    }
}
