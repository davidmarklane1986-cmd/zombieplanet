using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class PlayerShoot : MonoBehaviour
{
    public Camera playerCamera;
    public float shootRange = 100f;
    public int damage = 1;

    void Update()
    {
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
            Shoot();
#else
        if (Input.GetMouseButtonDown(0))
            Shoot();
#endif
    }

    void Shoot()
    {
        if (playerCamera == null) return;
#if ENABLE_INPUT_SYSTEM
        Ray ray = playerCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
#else
        Ray ray = playerCamera.ScreenPointToRay(Input.mousePosition);
#endif
        if (Physics.Raycast(ray, out RaycastHit hit, shootRange))
        {
            EnemyController enemy = hit.collider.GetComponent<EnemyController>();
            if (enemy != null)
            {
                enemy.TakeDamage(damage);
            }
        }
    }
}
