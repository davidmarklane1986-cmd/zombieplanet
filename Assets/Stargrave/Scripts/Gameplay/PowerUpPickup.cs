using System.Collections;
using UnityEngine;

/// <summary>
/// Stargrave-style floating pickup: trigger touch applies a timed buff, heal, or shield on the player.
/// Use a trigger collider (e.g. sphere), layer that collides with the player capsule, and optional <see cref="AudioClip"/>.
/// </summary>
public class PowerUpPickup : MonoBehaviour
{
    public enum Kind
    {
        SpeedBoost,
        JumpBoost,
        DamageBoost,
        FireRateBoost,
        HealthPack,
        Shield
    }

    [Header("Effect")]
    public Kind kind = Kind.SpeedBoost;
    [Tooltip("Timed buff duration (seconds). For HealthPack ignored. For Shield = invulnerability seconds.")]
    public float durationSeconds = 15f;
    [Tooltip("Multiplier for speed / jump / damage / fire-rate buffs.")]
    public float multiplier = 1.35f;
    [Tooltip("Flat heal for HealthPack.")]
    public int healAmount = 35;
    [Tooltip("If greater than 0, HealthPack heals this fraction of max health instead of the flat amount.")]
    [Range(0f, 1f)] public float healFractionOfMaxHealth = 0f;

    [Header("Pickup behaviour")]
    public bool destroyOnPickup = true;
    [Min(0f)]
    public float respawnDelaySeconds = 40f;
    [Tooltip("Only one pickup per player per cooldown window (prevents rapid re-trigger).")]
    public float pickupCooldownSeconds = 0.35f;

    [Header("Cosmetic")]
    public float spinSpeedDegrees = 72f;
    public AudioClip pickupClip;
    [Range(0f, 1f)] public float pickupVolume = 0.85f;

    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");

    Collider _collider;
    Renderer[] _renderers;
    float _nextPickupAllowedTime;
    bool _respawning;

    void Awake()
    {
        _collider = GetComponent<Collider>();
        if (_collider != null && !_collider.isTrigger)
            _collider.isTrigger = true;

        _renderers = GetComponentsInChildren<Renderer>(true);
        ApplyKindTint();
    }

    public void RefreshVisuals()
    {
        _renderers = GetComponentsInChildren<Renderer>(true);
        ApplyKindTint();
    }

    void Update()
    {
        if (_respawning)
            return;
        transform.Rotate(Vector3.up, spinSpeedDegrees * Time.deltaTime, Space.Self);
    }

    void OnTriggerEnter(Collider other)
    {
        if (_respawning || Time.time < _nextPickupAllowedTime)
            return;

        var health = other.GetComponentInParent<PlayerHealth>();
        if (health == null)
            return;

        Transform root = health.transform;
        ApplyEffect(root, health);

        _nextPickupAllowedTime = Time.time + pickupCooldownSeconds;

        if (pickupClip != null)
            AudioSource.PlayClipAtPoint(pickupClip, transform.position, pickupVolume);

        if (destroyOnPickup)
            Destroy(gameObject);
        else if (respawnDelaySeconds > 0f)
            StartCoroutine(CoRespawn());
    }

    void ApplyEffect(Transform playerRoot, PlayerHealth health)
    {
        float d = Mathf.Max(0.05f, durationSeconds);
        float m = Mathf.Max(0.05f, multiplier);

        switch (kind)
        {
            case Kind.SpeedBoost:
                ApplyBuff(playerRoot, "PowerUp_Speed", "Speed Boost", d, m, 1f, 1f, 1f);
                break;
            case Kind.JumpBoost:
                ApplyBuff(playerRoot, "PowerUp_Jump", "Jump Boost", d, 1f, m, 1f, 1f);
                break;
            case Kind.DamageBoost:
                ApplyBuff(playerRoot, "PowerUp_Damage", "Damage Boost", d, 1f, 1f, m, 1f);
                break;
            case Kind.FireRateBoost:
                ApplyBuff(playerRoot, "PowerUp_RapidFire", "Rapid Fire", d, 1f, 1f, 1f, m);
                break;
            case Kind.HealthPack:
                int resolvedHeal = healAmount;
                if (healFractionOfMaxHealth > 0f)
                    resolvedHeal = Mathf.Max(resolvedHeal, Mathf.CeilToInt(health.maxHealth * healFractionOfMaxHealth));
                health.Heal(resolvedHeal);
                break;
            case Kind.Shield:
                health.ExtendInvulnerability(d);
                break;
        }
    }

    static void ApplyBuff(Transform playerRoot, string buffId, string displayName, float duration, float spd, float jmp, float dmg, float rof)
    {
        var buffs = playerRoot.GetComponent<PlayerBuffController>();
        if (buffs != null)
            buffs.ApplyTimedBuff(buffId, duration, spd, jmp, dmg, rof, displayName);
    }

    IEnumerator CoRespawn()
    {
        _respawning = true;
        SetPhysicsAndVisuals(false);
        yield return new WaitForSeconds(respawnDelaySeconds);
        SetPhysicsAndVisuals(true);
        _respawning = false;
    }

    void SetPhysicsAndVisuals(bool on)
    {
        if (_collider != null)
            _collider.enabled = on;
        for (int i = 0; i < _renderers.Length; i++)
        {
            if (_renderers[i] != null)
                _renderers[i].enabled = on;
        }
    }

    void ApplyKindTint()
    {
        if (_renderers == null || _renderers.Length == 0)
            return;

        Color c = kind switch
        {
            Kind.SpeedBoost => new Color(0.35f, 0.85f, 0.45f, 1f),
            Kind.JumpBoost => new Color(0.45f, 0.65f, 1f, 1f),
            Kind.DamageBoost => new Color(1f, 0.45f, 0.25f, 1f),
            Kind.FireRateBoost => new Color(1f, 0.85f, 0.25f, 1f),
            Kind.HealthPack => new Color(0.95f, 0.35f, 0.45f, 1f),
            Kind.Shield => new Color(0.55f, 0.85f, 1f, 1f),
            _ => Color.white
        };

        for (int i = 0; i < _renderers.Length; i++)
        {
            var r = _renderers[i];
            if (r == null)
                continue;
            foreach (var mat in r.materials)
            {
                if (mat == null)
                    continue;
                if (mat.HasProperty(BaseColorId))
                    mat.SetColor(BaseColorId, c);
                else if (mat.HasProperty(ColorId))
                    mat.SetColor(ColorId, c);
            }
        }
    }
}
