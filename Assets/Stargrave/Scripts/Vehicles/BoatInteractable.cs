using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Proximity board/exit for <see cref="BoatController"/> — press E (or gamepad West) near the boat.
/// </summary>
[RequireComponent(typeof(BoatController))]
public sealed class BoatInteractable : MonoBehaviour
{
    [Min(1f)] public float interactRadius = 3.5f;
    [Tooltip("How often to scan for the player when empty.")]
    [Min(0.05f)] public float pollInterval = 0.12f;

    BoatController _boat;
    float _nextPoll;
    Transform _playerNear;

    void Awake()
    {
        _boat = GetComponent<BoatController>();
    }

    void Update()
    {
        if (_boat == null)
            return;

        if (_boat.HasOccupant)
        {
            if (WasInteractPressed())
                _boat.Disembark();
            return;
        }

        if (Time.time >= _nextPoll)
        {
            _nextPoll = Time.time + pollInterval;
            _playerNear = FindNearbyPlayer();
        }

        if (_playerNear == null)
            return;

        if (WasInteractPressed())
            _boat.TryBoard(_playerNear);
    }

    Transform FindNearbyPlayer()
    {
        if (PlayerHealth.IsDead)
            return null;

        Transform player = RuntimeSceneRefs.GetPlayerTransform(0.25f);
        if (player == null)
        {
            var health = Object.FindFirstObjectByType<PlayerHealth>(FindObjectsInactive.Exclude);
            if (health == null)
                return null;
            player = health.transform;
        }

        // Ignore players already in another boat.
        var motor = player.GetComponent<PlanetMotor_InputSystem>();
        if (motor != null && motor.IsInBoat)
            return null;

        float r = interactRadius;
        if ((player.position - transform.position).sqrMagnitude > r * r)
            return null;
        return player;
    }

    static bool WasInteractPressed()
    {
        if (Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame)
            return true;
        if (Gamepad.current != null && Gamepad.current.buttonWest.wasPressedThisFrame)
            return true;
        return false;
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.2f, 0.75f, 1f, 0.35f);
        Gizmos.DrawWireSphere(transform.position, interactRadius);
    }
}
