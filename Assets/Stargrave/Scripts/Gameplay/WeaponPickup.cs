using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Manual weapon loot: press E / gamepad West (PS4 Square) while nearby to pick up or swap.
/// Power-ups stay walk-over auto-collect.
/// </summary>
public sealed class WeaponPickup : MonoBehaviour
{
    public static readonly List<WeaponPickup> Active = new List<WeaponPickup>();
    public const int MaxDroppedWeapons = 100;

    [Header("Weapon")]
    public WeaponDef weapon;
    [Min(0)] public int ammo = 20;
    [Tooltip("0 = stay until collected (or the oldest is evicted at MaxDroppedWeapons). >0 expires after that many seconds.")]
    public float lifetimeSeconds;

    [Header("Motion")]
    public float spinSpeedDegrees = 72f;
    [Min(0.01f)] public float sizeMultiplier = 1.5f;

    [Header("Interact")]
    [Min(0.5f)] public float interactRadius = 2.4f;

    float _spawnTime;
    bool _collected;
    Collider _collider;
    static int _interactHandledFrame = -1;

    void OnEnable()
    {
        if (!Active.Contains(this))
            Active.Add(this);
    }

    void OnDisable()
    {
        Active.Remove(this);
    }

    void Awake()
    {
        transform.localScale *= Mathf.Max(0.01f, sizeMultiplier);
        _spawnTime = Time.time;
        _collider = GetComponent<Collider>();
        if (_collider != null)
            _collider.isTrigger = true;
        else
        {
            var sphere = gameObject.AddComponent<SphereCollider>();
            sphere.isTrigger = true;
            sphere.radius = 0.7f;
            _collider = sphere;
        }
    }

    void Update()
    {
        if (_collected)
            return;

        transform.Rotate(Vector3.up, spinSpeedDegrees * Time.deltaTime, Space.Self);

        if (lifetimeSeconds > 0f && Time.time - _spawnTime >= lifetimeSeconds)
        {
            Destroy(gameObject);
            return;
        }

        if (!WasInteractPressed())
            return;

        TryCollectNearestThisFrame();
    }

    static void TryCollectNearestThisFrame()
    {
        if (_interactHandledFrame == Time.frameCount)
            return;
        _interactHandledFrame = Time.frameCount;

        if (PlayerHealth.IsDead)
            return;

        Transform player = RuntimeSceneRefs.GetPlayerTransform(0.25f);
        if (player == null)
        {
            var health = Object.FindFirstObjectByType<PlayerHealth>(FindObjectsInactive.Exclude);
            if (health == null)
                return;
            player = health.transform;
        }

        var motor = player.GetComponent<PlanetMotor_InputSystem>();
        if (motor != null && motor.IsInBoat)
            return;

        WeaponPickup nearest = null;
        float bestSq = float.PositiveInfinity;
        for (int i = 0; i < Active.Count; i++)
        {
            WeaponPickup p = Active[i];
            if (p == null || p._collected || p.weapon == null || p.ammo <= 0)
                continue;
            float r = p.interactRadius;
            float sq = (player.position - p.transform.position).sqrMagnitude;
            if (sq > r * r || sq >= bestSq)
                continue;
            bestSq = sq;
            nearest = p;
        }

        if (nearest != null)
            nearest.TryCollect(player);
    }

    void TryCollect(Transform player)
    {
        if (_collected || player == null || weapon == null || ammo <= 0)
            return;

        var health = player.GetComponent<PlayerHealth>() ?? player.GetComponentInParent<PlayerHealth>();
        if (health == null)
            return;

        PlayerWeaponController weapons = PlayerWeaponController.EnsureOn(health);
        if (weapons == null)
            return;

        if (!weapons.TryPickupLoot(weapon, ammo))
            return;

        _collected = true;
        if (_collider != null)
            _collider.enabled = false;
        Destroy(gameObject);
    }

    static bool WasInteractPressed()
    {
        if (Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame)
            return true;
        if (Gamepad.current != null && Gamepad.current.buttonWest.wasPressedThisFrame)
            return true;
        return false;
    }

    public static void ClearAll()
    {
        for (int i = Active.Count - 1; i >= 0; i--)
        {
            WeaponPickup p = Active[i];
            if (p != null)
                Object.Destroy(p.gameObject);
        }

        Active.Clear();
    }

    /// <summary>
    /// Spawns a world pickup at <paramref name="worldPos"/> for the given weapon/ammo.
    /// Persistent drops (<paramref name="lifetimeSeconds"/> &lt;= 0) stay until collected,
    /// capped at <see cref="MaxDroppedWeapons"/> (oldest evicted).
    /// </summary>
    public static WeaponPickup Spawn(WeaponDef def, int ammo, Vector3 worldPos, float lifetimeSeconds = 0f)
    {
        if (def == null)
            return null;

        GameObject go;
        if (def.worldPickupPrefab != null)
        {
            go = Object.Instantiate(def.worldPickupPrefab, worldPos, Quaternion.identity);
        }
        else
        {
            go = new GameObject($"WeaponPickup_{def.id}");
            go.transform.position = worldPos;
            if (def.heldVisualPrefab != null)
            {
                GameObject visual = Object.Instantiate(def.heldVisualPrefab, go.transform);
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
                visual.name = "Visual";
                foreach (var col in visual.GetComponentsInChildren<Collider>(true))
                    Object.Destroy(col);
            }
            else
            {
                var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.transform.SetParent(go.transform, false);
                sphere.transform.localScale = Vector3.one * 0.45f;
                Object.Destroy(sphere.GetComponent<Collider>());
            }
        }

        WeaponPickup pickup = go.GetComponent<WeaponPickup>();
        if (pickup == null)
            pickup = go.AddComponent<WeaponPickup>();

        pickup.weapon = def;
        pickup.ammo = Mathf.Max(0, ammo);
        pickup.lifetimeSeconds = lifetimeSeconds;
        pickup._spawnTime = Time.time;
        pickup._collected = false;
        if (lifetimeSeconds <= 0f)
            EnforceDroppedCap();
        return pickup;
    }

    static void EnforceDroppedCap()
    {
        while (CountPersistent() > MaxDroppedWeapons)
        {
            WeaponPickup oldest = FindOldestPersistent();
            if (oldest == null)
                break;
            oldest._collected = true;
            Object.Destroy(oldest.gameObject);
            Active.Remove(oldest);
        }
    }

    static int CountPersistent()
    {
        int n = 0;
        for (int i = 0; i < Active.Count; i++)
        {
            WeaponPickup p = Active[i];
            if (p != null && !p._collected && p.lifetimeSeconds <= 0f)
                n++;
        }
        return n;
    }

    static WeaponPickup FindOldestPersistent()
    {
        for (int i = 0; i < Active.Count; i++)
        {
            WeaponPickup p = Active[i];
            if (p != null && !p._collected && p.lifetimeSeconds <= 0f)
                return p;
        }
        return null;
    }
}
