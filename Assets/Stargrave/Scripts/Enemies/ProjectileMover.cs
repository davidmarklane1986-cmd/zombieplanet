using UnityEngine;

/// <summary>
/// Moves the transform in a direction at constant speed. Used by PlayerShooting when the projectile has no Rigidbody.
/// </summary>
public class ProjectileMover : MonoBehaviour
{
    public Vector3 direction = Vector3.forward;
    public float speed = 40f;

    void Update()
    {
        transform.position += direction.normalized * (speed * Time.deltaTime);
    }
}
