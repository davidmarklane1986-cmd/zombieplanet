using UnityEngine;

/// <summary>
/// Stargrave-style player HP container: zombies call <see cref="TakeDamage"/> via SendMessage.
/// Death is reported outward so the frontend loop can decide whether to show game over, respawn, or reset the run.
/// </summary>
public class PlayerHealth : MonoBehaviour
{
    public static bool IsDead { get; private set; }
    public static event System.Action<PlayerHealth> Died;

    [Header("Health")]
    [Min(1)] public int maxHealth = 100;
    [Tooltip("Frontend/game-over delay tuning reference.")]
    public float respawnDelaySeconds = 2.5f;
    [Tooltip("Invulnerability after respawn (seconds).")]
    public float respawnInvulnerabilitySeconds = 1.25f;
    [Tooltip("Brief i-frames after each hit so a pack cannot dump every zombie's attack on the same frame.")]
    [Min(0f)] public float hitInvulnerabilitySeconds = 0.85f;

    [Header("Spawn / Reset")]
    [Tooltip("If set, teleport here on respawn. Otherwise uses position/rotation captured at Start.")]
    public Transform respawnAnchor;
    [Tooltip("Call ZombieSpawner.HardResetPopulationToInitial on externally requested run reset.")]
    public bool resetZombiesOnRespawn = true;

    int _currentHealth;
    bool _dead;
    float _invulnerableUntil;

    /// <summary>Current hit points (0 while dead until respawn refills).</summary>
    public int CurrentHealth => _currentHealth;
    Vector3 _spawnPosition;
    Quaternion _spawnRotation;
    Rigidbody _rb;
    PlayerCharacterAnimator _anim;

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _anim = GetComponent<PlayerCharacterAnimator>();
    }

    void Start()
    {
        if (respawnAnchor != null)
        {
            _spawnPosition = respawnAnchor.position;
            _spawnRotation = respawnAnchor.rotation;
        }
        else
        {
            _spawnPosition = transform.position;
            _spawnRotation = transform.rotation;
        }

        _currentHealth = maxHealth;
        _dead = false;
        IsDead = false;
    }

    /// <summary>Instant heal (e.g. health pack pickup). Ignored while dead.</summary>
    public void Heal(int amount)
    {
        if (_dead || amount <= 0)
            return;
        _currentHealth = Mathf.Min(maxHealth, _currentHealth + amount);
    }

    /// <summary>Extends invulnerability from power-ups / shield (stacks by taking the later expiry time).</summary>
    public void ExtendInvulnerability(float extraSeconds)
    {
        if (_dead || extraSeconds <= 0f)
            return;
        _invulnerableUntil = Mathf.Max(_invulnerableUntil, Time.time + extraSeconds);
    }

    /// <summary>Called by <see cref="ZombieAI"/> via SendMessage — do not rename.</summary>
    public void TakeDamage(int amount)
    {
        if (_dead || Time.time < _invulnerableUntil)
            return;
        if (amount <= 0)
            return;

        _currentHealth -= amount;
        if (hitInvulnerabilitySeconds > 0f)
            _invulnerableUntil = Mathf.Max(_invulnerableUntil, Time.time + hitInvulnerabilitySeconds);

        // Distinct 2D hurt cue when the player actually takes damage (reuses the impact thud).
        AudioManager.PlayHit2D();

        if (_currentHealth > 0)
            return;
        Die();
    }

    void Die()
    {
        if (_dead)
            return;

        _currentHealth = 0;
        _dead = true;
        IsDead = true;
        StopRigidbodyMotion();
        SetGameplayEnabled(false);

        if (_anim != null)
            _anim.PlayDeathFromDamage();
        Died?.Invoke(this);
    }

    public void RespawnNow(bool resetZombies)
    {
        transform.SetPositionAndRotation(_spawnPosition, _spawnRotation);
        StopRigidbodyMotion();

        _currentHealth = maxHealth;
        _invulnerableUntil = Time.time + Mathf.Max(0f, respawnInvulnerabilitySeconds);
        _dead = false;
        IsDead = false;

        if (TryGetComponent(out PlayerBuffController buff))
            buff.ClearAllBuffs();

        SetGameplayEnabled(true);
        if (_anim != null)
            _anim.ResetLocomotionAfterRespawn();

        if (resetZombies && ZombieSpawner.Instance != null)
            ZombieSpawner.Instance.HardResetPopulationToInitial();

        RuntimeSceneRefs.InvalidatePlayer();
    }

    public void ResetForNewRun()
    {
        RespawnNow(resetZombiesOnRespawn);
    }

    public void SetGameplayControlEnabled(bool on)
    {
        if (!on)
            StopRigidbodyMotion();
        SetGameplayEnabled(on);
    }

    void StopRigidbodyMotion()
    {
        if (_rb == null)
            return;
        // Must clear velocity while non-kinematic — Unity errors (and can Error-Pause play) otherwise.
        if (_rb.isKinematic)
            _rb.isKinematic = false;
        _rb.linearVelocity = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
    }

    void SetGameplayEnabled(bool on)
    {
        if (_rb != null)
        {
            if (!on)
                StopRigidbodyMotion();
            _rb.isKinematic = !on;
        }

        if (TryGetComponent(out PlanetMotor_InputSystem motor))
            motor.enabled = on;
        if (TryGetComponent(out MouseLook_Gravity look))
            look.enabled = on;
        if (TryGetComponent(out PlayerLookController plc))
            plc.enabled = on;
        if (TryGetComponent(out PlayerCharacterAnimator pca))
            pca.enabled = on;
        if (TryGetComponent(out PlayerBuffController buff))
            buff.enabled = on;
        if (TryGetComponent(out PlayerCharacterAlign align))
            align.enabled = on;

        var shooting = Object.FindAnyObjectByType<PlayerShooting>(FindObjectsInactive.Include);
        if (shooting != null)
            shooting.enabled = on;
    }
}
