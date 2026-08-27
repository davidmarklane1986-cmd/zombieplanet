using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Shared stamina: drains while swimming or sprinting, regenerates otherwise.
/// Empty sprint locks until a full recharge. Speed power-up sprint does not drain this bar.
/// Empty swim still applies drown damage (ignores hit i-frames).
/// </summary>
[DisallowMultipleComponent]
public sealed class PlayerSwimStamina : MonoBehaviour
{
    public static event System.Action<float> StaminaChanged;

    [Header("Stamina")]
    [Min(1f)] public float maxStamina = 100f;
    [Min(0.1f)] public float drainPerSecond = 8f;
    [Min(0.1f)] public float sprintDrainPerSecond = 12f;
    [Min(0.1f)] public float regenPerSecond = 14f;
    [Range(0.05f, 1f)] public float emptySwimSpeedMultiplier = 0.35f;

    [Header("Drown")]
    [Min(1)] public int drownDamagePerTick = 8;
    [Min(0.1f)] public float drownTickInterval = 0.85f;

    PlanetMotor_InputSystem _motor;
    PlayerHealth _health;
    PlayerBuffController _buffs;
    float _stamina;
    float _nextDrownTime;
    bool _inBoat;
    bool _sprintLockedUntilFull;

    public float Current => _stamina;
    public float Normalized => maxStamina > 1e-3f ? Mathf.Clamp01(_stamina / maxStamina) : 0f;
    public bool IsInBoat => _inBoat;
    public bool IsEmpty => _stamina <= 0.01f;
    public bool IsSprintLocked => _sprintLockedUntilFull;
    public bool CanSprint => !_sprintLockedUntilFull;

    public static PlayerSwimStamina EnsureOn(PlayerHealth player)
    {
        if (player == null)
            return null;
        var c = player.GetComponent<PlayerSwimStamina>();
        if (c == null)
            c = player.gameObject.AddComponent<PlayerSwimStamina>();
        return c;
    }

    void Awake()
    {
        _motor = GetComponent<PlanetMotor_InputSystem>();
        _health = GetComponent<PlayerHealth>();
        _buffs = GetComponent<PlayerBuffController>();
        _stamina = maxStamina;
        _sprintLockedUntilFull = false;
    }

    void OnEnable()
    {
        StaminaChanged?.Invoke(Normalized);
    }

    public void SetInBoat(bool inBoat)
    {
        _inBoat = inBoat;
        if (_motor != null)
            _motor.SetInBoat(inBoat);
    }

    public void ResetStaminaFull()
    {
        _stamina = maxStamina;
        _nextDrownTime = 0f;
        _sprintLockedUntilFull = false;
        ApplyMotorMultiplier();
        StaminaChanged?.Invoke(Normalized);
    }

    void Update()
    {
        if (_health != null && PlayerHealth.IsDead)
        {
            ApplyMotorMultiplier();
            return;
        }

        bool swimming = _motor != null && _motor.IsSwimming && !_inBoat;
        bool sprintHeldMoving = IsSprintHeldMoving();
        bool powerUpSprint = HasSprintPowerUp();
        bool drainSprint = !swimming && !_inBoat && CanSprint && sprintHeldMoving && !powerUpSprint;
        bool holdForPowerUp = !swimming && !_inBoat && sprintHeldMoving && powerUpSprint;
        float before = _stamina;

        if (swimming)
            _stamina = Mathf.Max(0f, _stamina - drainPerSecond * Time.deltaTime);
        else if (drainSprint)
            _stamina = Mathf.Max(0f, _stamina - sprintDrainPerSecond * Time.deltaTime);
        else if (!holdForPowerUp)
            _stamina = Mathf.Min(maxStamina, _stamina + regenPerSecond * Time.deltaTime);

        if (IsEmpty)
            _sprintLockedUntilFull = true;
        else if (_stamina >= maxStamina - 0.01f)
            _sprintLockedUntilFull = false;

        ApplyMotorMultiplier();

        if (!Mathf.Approximately(before, _stamina))
            StaminaChanged?.Invoke(Normalized);

        if (swimming && IsEmpty && _health != null && !PlayerHealth.IsDead)
        {
            if (Time.time >= _nextDrownTime)
            {
                _nextDrownTime = Time.time + drownTickInterval;
                _health.TakeDamage(drownDamagePerTick, ignoreInvulnerability: true);
            }
        }
        else
        {
            _nextDrownTime = 0f;
        }
    }

    void ApplyMotorMultiplier()
    {
        if (_motor == null)
            return;
        bool swimmingEmpty = _motor.IsSwimming && !_inBoat && IsEmpty;
        _motor.swimStaminaSpeedMultiplier = swimmingEmpty ? emptySwimSpeedMultiplier : 1f;
    }

    bool HasSprintPowerUp()
    {
        if (_buffs == null)
            _buffs = GetComponent<PlayerBuffController>();
        return _buffs != null && _buffs.HasSprintActivatedSpeedBuff();
    }

    static bool IsSprintHeldMoving()
    {
#if ENABLE_INPUT_SYSTEM
        bool sprintHeld =
            (Keyboard.current != null && Keyboard.current.leftShiftKey.isPressed) ||
            (Gamepad.current != null && Gamepad.current.leftStickButton.isPressed);
        if (!sprintHeld)
            return false;

        if (Keyboard.current != null &&
            (Keyboard.current.wKey.isPressed || Keyboard.current.sKey.isPressed ||
             Keyboard.current.aKey.isPressed || Keyboard.current.dKey.isPressed))
            return true;
        if (Gamepad.current != null && Gamepad.current.leftStick.ReadValue().sqrMagnitude > 0.01f)
            return true;
#endif
        return false;
    }
}
