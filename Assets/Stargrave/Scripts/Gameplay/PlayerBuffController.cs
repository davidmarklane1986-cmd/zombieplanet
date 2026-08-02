using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public class PlayerBuffController : MonoBehaviour
{
    public static event Action<string[]> PowerUpsChanged;

    [Header("Target Motor (leave empty to auto-find)")]
    public MonoBehaviour motor;

    [Header("Motor Field Names (change if yours differ)")]
    public string[] speedFieldCandidates = { "moveSpeed", "maxSpeed", "walkSpeed", "speed" };
    public string[] jumpFieldCandidates = { "jumpImpulse", "jumpForce", "jumpStrength", "jumpPower" };

    [Header("Debug")]
    public bool logOnApply = false;

    float baseSpeed = -1f;
    float baseJump = -1f;

    readonly Dictionary<string, ActiveBuff> active = new();

    struct ActiveBuff
    {
        public string displayName;
        public float speedMult;
        public float jumpMult;
        public float damageMult;
        public float fireRateMult;
        public float endTime;
        public float remainingSeconds;
        public bool drainWhileUsed;
    }

    /// <summary>Product of active timed buffs; 1 when none.</summary>
    public float CombinedDamageMultiplier { get; private set; } = 1f;

    /// <summary>Product of active timed buffs; 1 when none. Higher = faster shooting (cooldown divided by this).</summary>
    public float CombinedFireRateMultiplier { get; private set; } = 1f;

    void Awake()
    {
        EnsureResolvedMotor();
        CacheBaseValues();
    }

    void OnEnable()
    {
        EnsureResolvedMotor();
        NotifyPowerUpsChanged();
    }

    void Update()
    {
        float now = Time.time;
        var keysToRemove = new List<string>();

        foreach (var kvp in active)
        {
            ActiveBuff b = kvp.Value;
            if (b.drainWhileUsed)
            {
                if (b.remainingSeconds <= 0f)
                    keysToRemove.Add(kvp.Key);
            }
            else if (now >= b.endTime)
            {
                keysToRemove.Add(kvp.Key);
            }
        }

        for (int i = 0; i < keysToRemove.Count; i++)
            active.Remove(keysToRemove[i]);

        ApplyCombinedBuffs();
        if (keysToRemove.Count > 0)
            NotifyPowerUpsChanged();
    }

    public void ApplyTimedBuff(string buffId, float durationSeconds, float speedMultiplier, float jumpMultiplier,
        float damageMultiplier = 1f, float fireRateMultiplier = 1f, string displayName = null,
        bool drainWhileUsed = false)
    {
        EnsureResolvedMotor();
        if (motor != null && (baseSpeed < 0f || baseJump < 0f))
            CacheBaseValues();

        float dur = Mathf.Max(0.01f, durationSeconds);
        ActiveBuff b = new ActiveBuff
        {
            displayName = string.IsNullOrWhiteSpace(displayName) ? buffId : displayName,
            speedMult = Mathf.Max(0.01f, speedMultiplier),
            jumpMult = Mathf.Max(0.01f, jumpMultiplier),
            damageMult = Mathf.Max(0.01f, damageMultiplier),
            fireRateMult = Mathf.Max(0.01f, fireRateMultiplier),
            drainWhileUsed = drainWhileUsed,
            remainingSeconds = drainWhileUsed ? dur : 0f,
            endTime = drainWhileUsed ? float.PositiveInfinity : Time.time + dur
        };

        active[buffId] = b;
        NotifyPowerUpsChanged();

        if (logOnApply)
            Debug.Log($"[Buff] {buffId}: spd x{b.speedMult}, jmp x{b.jumpMult}, dmg x{b.damageMult}, rof x{b.fireRateMult} ({durationSeconds}s{(drainWhileUsed ? ", drain while used" : "")})");
    }

    /// <summary>Drains remaining time on a drain-while-used buff (e.g. Rapid Fire while holding trigger).</summary>
    public void ConsumeBuffTime(string buffId, float deltaSeconds)
    {
        if (string.IsNullOrEmpty(buffId) || deltaSeconds <= 0f)
            return;
        if (!active.TryGetValue(buffId, out ActiveBuff b) || !b.drainWhileUsed)
            return;

        int beforeCeil = Mathf.CeilToInt(b.remainingSeconds);
        b.remainingSeconds -= deltaSeconds;
        if (b.remainingSeconds <= 0f)
        {
            active.Remove(buffId);
            ApplyCombinedBuffs();
            NotifyPowerUpsChanged();
            return;
        }

        active[buffId] = b;
        if (Mathf.CeilToInt(b.remainingSeconds) != beforeCeil)
            NotifyPowerUpsChanged();
    }

    public bool HasActiveBuff(string buffId)
    {
        return !string.IsNullOrEmpty(buffId) && active.ContainsKey(buffId);
    }

    public float GetBuffRemainingSeconds(string buffId)
    {
        if (!active.TryGetValue(buffId, out ActiveBuff b))
            return 0f;
        if (b.drainWhileUsed)
            return Mathf.Max(0f, b.remainingSeconds);
        return Mathf.Max(0f, b.endTime - Time.time);
    }

    public string[] GetActivePowerUpNames()
    {
        var names = new List<string>();
        foreach (var kvp in active)
        {
            string displayName = string.IsNullOrWhiteSpace(kvp.Value.displayName) ? kvp.Key : kvp.Value.displayName;
            float remaining = kvp.Value.drainWhileUsed
                ? kvp.Value.remainingSeconds
                : (kvp.Value.endTime - Time.time);
            int secs = Mathf.Max(0, Mathf.CeilToInt(remaining));
            if (!string.IsNullOrWhiteSpace(displayName))
                names.Add($"{displayName} {secs}s");
        }

        names.Sort(StringComparer.Ordinal);
        return names.ToArray();
    }

    public void ClearAllBuffs()
    {
        if (active.Count == 0)
        {
            NotifyPowerUpsChanged();
            return;
        }

        active.Clear();
        ApplyCombinedBuffs();
        NotifyPowerUpsChanged();
    }

    void CacheBaseValues()
    {
        EnsureResolvedMotor();
        if (motor == null) return;

        baseSpeed = ReadFirstFloat(motor, speedFieldCandidates, fallback: 6f);
        baseJump  = ReadFirstFloat(motor, jumpFieldCandidates,  fallback: 6f);

        if (logOnApply)
            Debug.Log($"[Buff] Base cached: speed={baseSpeed}, jump={baseJump} (motor={motor.GetType().Name})");
    }

    void ApplyCombinedBuffs()
    {
        EnsureResolvedMotor();
        float speedMult = 1f;
        float jumpMult = 1f;
        float dmgMult = 1f;
        float rofMult = 1f;

        foreach (var kvp in active)
        {
            speedMult *= kvp.Value.speedMult;
            jumpMult *= kvp.Value.jumpMult;
            dmgMult *= kvp.Value.damageMult;
            rofMult *= kvp.Value.fireRateMult;
        }

        CombinedDamageMultiplier = dmgMult;
        CombinedFireRateMultiplier = rofMult;

        PlanetMotor_InputSystem explicitMotor = motor as PlanetMotor_InputSystem;
        if (explicitMotor != null)
        {
            explicitMotor.SetExternalBuffMultipliers(speedMult, jumpMult);
        }
        else if (motor != null && baseSpeed >= 0f && baseJump >= 0f)
        {
            WriteFirstFloat(motor, speedFieldCandidates, baseSpeed * speedMult);
            WriteFirstFloat(motor, jumpFieldCandidates, baseJump * jumpMult);
        }
    }

    void NotifyPowerUpsChanged()
    {
        PowerUpsChanged?.Invoke(GetActivePowerUpNames());
    }

    void EnsureResolvedMotor()
    {
        if (HasWritableMovementFields(motor))
            return;

        motor = GetComponent<PlanetMotor_InputSystem>();
        if (!HasWritableMovementFields(motor))
            motor = GetComponent("PlanetMotor") as MonoBehaviour;
        if (!HasWritableMovementFields(motor))
            motor = FindFallbackMotor();

        if (!HasWritableMovementFields(motor))
            motor = null;
    }

    MonoBehaviour FindFallbackMotor()
    {
        MonoBehaviour[] behaviours = GetComponents<MonoBehaviour>();
        for (int i = 0; i < behaviours.Length; i++)
        {
            MonoBehaviour behaviour = behaviours[i];
            if (HasWritableMovementFields(behaviour))
                return behaviour;
        }

        return null;
    }

    bool HasWritableMovementFields(MonoBehaviour behaviour)
    {
        if (behaviour == null)
            return false;

        return HasWritableFloat(behaviour, speedFieldCandidates) || HasWritableFloat(behaviour, jumpFieldCandidates);
    }

    static bool HasWritableFloat(object obj, string[] candidates)
    {
        if (obj == null || candidates == null)
            return false;

        Type t = obj.GetType();
        for (int i = 0; i < candidates.Length; i++)
        {
            var f = t.GetField(candidates[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null && f.FieldType == typeof(float))
                return true;

            var p = t.GetProperty(candidates[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.PropertyType == typeof(float) && p.CanWrite)
                return true;
        }

        return false;
    }

    static float ReadFirstFloat(object obj, string[] candidates, float fallback)
    {
        Type t = obj.GetType();

        for (int i = 0; i < candidates.Length; i++)
        {
            var f = t.GetField(candidates[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null && f.FieldType == typeof(float))
                return (float)f.GetValue(obj);

            var p = t.GetProperty(candidates[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.PropertyType == typeof(float) && p.CanRead)
                return (float)p.GetValue(obj);
        }

        return fallback;
    }

    static void WriteFirstFloat(object obj, string[] candidates, float value)
    {
        Type t = obj.GetType();

        for (int i = 0; i < candidates.Length; i++)
        {
            var f = t.GetField(candidates[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null && f.FieldType == typeof(float))
            {
                f.SetValue(obj, value);
                return;
            }

            var p = t.GetProperty(candidates[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.PropertyType == typeof(float) && p.CanWrite)
            {
                p.SetValue(obj, value);
                return;
            }
        }
    }
}
