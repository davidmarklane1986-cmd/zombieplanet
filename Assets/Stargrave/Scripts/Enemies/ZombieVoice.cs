using UnityEngine;

/// <summary>
/// Per-zombie 3D vocalisation: a spatialised <see cref="AudioSource"/> (so distance attenuates volume
/// automatically) that plays periodic idle groans at randomised intervals plus an aggressive snarl when
/// the zombie attacks/lunges. Auto-added by <see cref="ZombieAI"/> at runtime (no prefab wiring needed).
///
/// Performance: Update is a couple of cheap time/distance checks. Groans are skipped when the listener is
/// far away, and a global concurrent-groan cap stops a horde from turning into a wall of noise.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(ZombieAI))]
public sealed class ZombieVoice : MonoBehaviour
{
    [Header("Idle groans (3D)")]
    [Tooltip("Randomised seconds between idle groans (per zombie). Short so a nearby zombie groans almost constantly.")]
    public float minGroanInterval = 1.2f;
    public float maxGroanInterval = 3f;
    [Range(0f, 1f)] public float groanVolume = 0.85f;

    [Header("Attack snarl")]
    [Range(0f, 1f)] public float attackVolume = 1f;
    [Tooltip("Min seconds between attack snarls from this zombie.")]
    public float attackCooldown = 1.6f;

    [Header("3D spatialisation")]
    [Tooltip("Full volume within this distance.")]
    [Min(0.1f)] public float minDistance = 6f;
    [Tooltip("Beyond this distance the groan is effectively inaudible (rolloff floor).")]
    [Min(1f)] public float maxDistance = 45f;
    [Tooltip("Skip scheduling idle groans entirely when the listener is farther than this (saves voices).")]
    [Min(1f)] public float audibleCullDistance = 60f;

    // Global throttle so a crowd can't stack dozens of overlapping groans. Raised so groaning feels
    // near-constant around the player while still capping a full horde.
    const int MaxConcurrentGroans = 8;
    static int s_ConcurrentGroans;

    AudioSource _src;
    Transform _listener;
    float _nextGroanTime;
    float _nextAttackAllowed;
    float _groanReleaseTime;
    bool _holdingSlot;

    void Awake()
    {
        _src = gameObject.AddComponent<AudioSource>();
        _src.playOnAwake = false;
        _src.spatialBlend = 1f;                 // fully 3D -> distance affects volume
        _src.rolloffMode = AudioRolloffMode.Logarithmic;
        _src.minDistance = minDistance;
        _src.maxDistance = maxDistance;
        _src.dopplerLevel = 0f;                 // avoid pitch artefacts on fast planet movement
        _src.spread = 60f;
    }

    void Start()
    {
        _nextGroanTime = Time.time + Random.Range(0.5f, maxGroanInterval);
        ResolveListener();
    }

    void OnDisable()
    {
        ReleaseSlotIfHeld();
    }

    void ResolveListener()
    {
        var al = FindFirstObjectByType<AudioListener>();
        if (al != null)
            _listener = al.transform;
        else if (Camera.main != null)
            _listener = Camera.main.transform;
    }

    void Update()
    {
        // Release the global concurrent slot once our last groan has finished.
        if (_holdingSlot && Time.time >= _groanReleaseTime)
            ReleaseSlotIfHeld();

        if (Time.time < _nextGroanTime)
            return;

        ScheduleNextGroan();

        if (_listener == null)
        {
            ResolveListener();
            if (_listener == null)
                return;
        }

        // Distance gate: don't even bother (or take a voice slot) when far from the player/camera.
        float distSq = (transform.position - _listener.position).sqrMagnitude;
        if (distSq > audibleCullDistance * audibleCullDistance)
            return;

        if (s_ConcurrentGroans >= MaxConcurrentGroans)
            return;

        // Per-source overlap guard: never start a new groan/snarl on top of this zombie's own currently
        // playing clip. The real recorded pack clips vary in length (some longer than the groan interval),
        // so this stops a single zombie stacking its own voice. The global cap still limits the crowd.
        if (_src != null && _src.isPlaying)
            return;

        PlayGroan(SfxLibrary.RandomZombieGroan(), groanVolume);
    }

    void ScheduleNextGroan()
    {
        float lo = Mathf.Max(0.5f, minGroanInterval);
        float hi = Mathf.Max(lo + 0.5f, maxGroanInterval);
        _nextGroanTime = Time.time + Random.Range(lo, hi);
    }

    void PlayGroan(AudioClip clip, float volume)
    {
        if (clip == null || _src == null)
            return;

        _src.minDistance = minDistance;
        _src.maxDistance = maxDistance;
        _src.pitch = Random.Range(0.92f, 1.08f);
        _src.PlayOneShot(clip, Mathf.Clamp01(volume));

        if (!_holdingSlot)
        {
            s_ConcurrentGroans++;
            _holdingSlot = true;
        }
        _groanReleaseTime = Time.time + clip.length / Mathf.Max(0.01f, _src.pitch);
    }

    void ReleaseSlotIfHeld()
    {
        if (!_holdingSlot)
            return;
        _holdingSlot = false;
        s_ConcurrentGroans = Mathf.Max(0, s_ConcurrentGroans - 1);
    }

    /// <summary>Aggressive snarl when the zombie lunges/attacks (throttled per zombie).</summary>
    public void PlayAttackSnarl()
    {
        if (Time.time < _nextAttackAllowed)
            return;
        _nextAttackAllowed = Time.time + Mathf.Max(0.1f, attackCooldown);

        AudioClip clip = SfxLibrary.RandomZombieAttack();
        if (clip == null)
            clip = SfxLibrary.RandomZombieGroan();
        // Bypass the idle concurrent cap — attacks are gameplay-critical feedback and already throttled.
        if (clip != null && _src != null)
        {
            _src.minDistance = minDistance;
            _src.maxDistance = maxDistance;
            _src.pitch = Random.Range(0.98f, 1.08f);
            _src.PlayOneShot(clip, Mathf.Clamp01(attackVolume));
        }
    }

    /// <summary>
    /// Death rattle. Routed through the detached 3D one-shot pool so it survives the zombie GameObject
    /// being destroyed immediately after death.
    /// </summary>
    public void PlayDeathGroan(float volume = 0.9f)
    {
        AudioClip clip = SfxLibrary.RandomZombieDeath();
        if (clip != null)
            AudioOneShotPool.PlayClip(clip, transform.position, Mathf.Clamp01(volume), null, 1f);
    }
}
