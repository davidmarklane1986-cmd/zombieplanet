using UnityEngine;

public class PlanetGravity : MonoBehaviour
{
    public static Transform PlanetTransform;

    private void Awake()
    {
        PlanetTransform = transform;
    }
}
