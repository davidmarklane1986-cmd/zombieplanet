using System.Collections;
using UnityEngine;
using UnityEngine.Audio;

/// <summary>
/// Stargrave 1.3-style enemy AI (chase, attack, roam, performance tiers) on the zombie prefab.
/// Requires Rigidbody. Optional Animator "Speed" float, hit/death SFX/VFX.
/// On death: play fall-down (Death anim when available, else tip-over), sink into the ground, then despawn.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class ZombieAI : MonoBehaviour
{
    const string RuntimeHitboxRootName = "RuntimeHitboxes";

    public static event System.Action ZombieKilled;

    public static int LivingCount { get; private set; }
    public static int DiagnosticsFullAiActiveThisFrame => s_FullAiSlotsUsed;
    public static int DiagnosticsCheapAiEstimatedThisFrame => Mathf.Max(0, LivingCount - s_FullAiSlotsUsed);
    public static int DiagnosticsFullAiCapLastSeen { get; private set; }
    public static float DiagnosticsFullAiNearDistanceLastSeen { get; private set; }

    public static void RecalculateLivingCountFromScene()
    {
        ZombieAI[] all = Object.FindObjectsByType<ZombieAI>(FindObjectsInactive.Exclude);
        int n = 0;
        for (int i = 0; i < all.Length; i++)
        {
            // Corpse phase keeps the component enabled for the sink coroutine — don't count those.
            if (all[i] != null && all[i].isActiveAndEnabled && !all[i]._dead && all[i]._livingCounted)
                n++;
        }
        LivingCount = n;
    }

    [Header("Movement")]
    public float moveSpeed = 3f;
    public float gravityStrength = 20f;
    public float detectionRadius = 25f;
    public float attackRadius = 2f;
    public float rotationSpeed = 5f;
    public float roamChangeInterval = 4f;
    public float surfaceStickDistance = 0.5f;
    public float surfaceStickForce = 50f;

    [Header("Performance")]
    [Range(1, 8)] public int surfaceStickRaycastPeriod = 2;
    [Range(1, 8)] public int aiDecisionPeriod = 2;
    [Min(1f)] public float farDecisionDistance = 80f;
    [Range(1f, 6f)] public float farDecisionPeriodMultiplier = 2f;
    [Min(1f)] public float fullAiNearDistance = 35f;
    [Min(1)] public int maxFullAiEnemiesNearPlayer = 120;
    [Range(0f, 1f)] public float cheapIdleSpeedMultiplier = 0.45f;
    [Range(1f, 8f)] public float cheapDecisionPeriodMultiplier = 3f;
    [Tooltip("Only play walk/idle skins within this distance of the player (saves CPU). 0 = always when visible.")]
    [Min(0f)] public float animateMaxDistance = 45f;

    [Header("Water avoidance")]
    [Tooltip("Submersion depth (world units) past which a zombie is treated as in water and will seek land. " +
             "Positive = below sea level.")]
    public float inWaterDepthThreshold = 0.35f;
    [Tooltip("How far ahead (world units along the tangent) to probe before stepping into water. " +
             "Zombies refuse to walk into the ocean but will roam along the shore.")]
    [Min(0.5f)] public float waterLookAheadDistance = 2.5f;
    [Tooltip("Extra clearance above sea level required to treat ground as dry when probing ahead.")]
    [Min(0f)] public float waterEdgeClearance = 0.5f;
    [Tooltip("Number of tangent compass directions sampled around the zombie when picking a landward heading.")]
    [Range(4, 32)] public int landSeekSampleCount = 8;
    [Tooltip("Angular look-ahead (degrees) for each land-seek terrain sample on the sphere.")]
    [Range(1f, 45f)] public float landSeekSampleAngleDegrees = 8f;
    [Tooltip("Seconds the chosen land-seek heading is held before recomputing (anti-jitter).")]
    [Min(0.05f)] public float landSeekRecomputeInterval = 0.75f;

    [Header("Aggro")]
    [Tooltip("When shot/damaged, chase the player from ANY distance for this long (refreshed while engaged " +
             "within the detection radius). 0 = no forced aggro on damage.")]
    [Min(0f)] public float provokedAggroDuration = 30f;

    [Header("Health & combat")]
    [Tooltip("Fixed max health (used if min/max equal).")]
    public int maxHealth = 3;
    [Tooltip("Random shots to kill (inclusive). If max &lt; min, uses maxHealth only.")]
    public int minShotsToKill = 3;
    public int maxShotsToKill = 5;
    public int attackDamage = 10;
    public float attackCooldown = 1f;

    [Header("Combat feedback")]
    public AudioClip hitSfx;
    [Range(0f, 1f)] public float hitSfxVolume = 0.75f;
    public AudioClip deathSfx;
    [Range(0f, 1f)] public float deathSfxVolume = 0.85f;
    public AudioMixerGroup sfxMixerGroup;
    public GameObject hitVfxPrefab;
    public GameObject deathVfxPrefab;

    [Header("Death fall / sink")]
    [Tooltip("How long to hold the fall-down before sinking (also used when a Death animator state is played).")]
    [Min(0.2f)] public float deathFallHoldSeconds = 1.35f;
    [Tooltip("Tip-over duration when no Death animator state exists on this zombie.")]
    [Min(0.15f)] public float proceduralFallSeconds = 0.85f;
    [Tooltip("How far the corpse sinks into the planet along gravity before despawn.")]
    [Min(0.25f)] public float deathSinkDistance = 2.8f;
    [Tooltip("Seconds from lying on the ground until fully buried. Linear sink (no ease) so it stays slow the whole way.")]
    [Min(5f)] public float deathSinkSeconds = 35f;
    [Tooltip("Tiny gap above the terrain so the mesh sits on the ground without z-fighting (not a hover height).")]
    [Min(0f)] public float deathSurfaceClearance = 0.01f;

    [Header("Animation (optional)")]
    public Animator animator;
    [Tooltip("Unused leftover.")]
    public Animation locomotion;
    [Tooltip("Unused leftover (was SampleAnimation / Playables).")]
    public AnimationClip locomotionIdle;
    [Tooltip("Unused leftover (was SampleAnimation / Playables).")]
    public AnimationClip locomotionRun;
    static readonly int SpeedId = Animator.StringToHash("Speed");

    ZombieLocomotionAnimator _locoPlay;
    ZombieProceduralLimbWalk _procWalk;
    static int _locomotionLogCount;

    Transform player;
    Rigidbody rb;
    ZombieVoice _voice;
    Vector3 currentDirection;
    float roamTimer;
    bool isAttacking;

    Transform planet;
    MeshCollider planetCollider;
    Planet _planetComp;
    PlanetOceanLayer _oceanLayer;
    float nextAttackTime;
    int _surfaceStickPhase;
    int _fixedStepCounter;
    float _nextPlayerResolveTime;
    float _nextPlanetResolveTime;
    int _aiDecisionPhase;
    bool _cachedShouldAttack;
    Vector3 _cachedTargetPos;
    bool _cachedFullAiActive;
    bool _cachedChasing;

    bool _provoked;
    float _provokedUntilTime;
    Vector3 _landSeekDir;
    float _nextLandSeekRecomputeTime;

    static int s_FullAiSlotsFrame = -1;
    static int s_FullAiSlotsUsed;

    int currentHealth;
    bool _dead;
    bool _livingCounted;

    public int CurrentHealth => currentHealth;
    public bool IsDead => _dead;

    void OnEnable()
    {
        LivingCount++;
        _livingCounted = true;
    }

    void OnDestroy()
    {
        ReleaseLivingSlot();
    }

    void ReleaseLivingSlot()
    {
        if (!_livingCounted)
            return;
        LivingCount = Mathf.Max(0, LivingCount - 1);
        _livingCounted = false;
    }

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        rb.useGravity = false;
        rb.constraints = RigidbodyConstraints.FreezeRotation;

        if (maxShotsToKill >= minShotsToKill && minShotsToKill > 0)
            currentHealth = Random.Range(minShotsToKill, maxShotsToKill + 1);
        else
            currentHealth = Mathf.Max(1, maxHealth);

        _dead = false;
        player = RuntimeSceneRefs.GetPlayerTransform();
        ResolvePlanet(force: true);

        int period = Mathf.Max(1, surfaceStickRaycastPeriod);
        _surfaceStickPhase = Random.Range(0, period);
        _aiDecisionPhase = Random.Range(0, Mathf.Max(1, aiDecisionPeriod));

        if (animator == null)
            animator = GetComponentInChildren<Animator>();
        _culler = GetComponent<ZombieVisibilityCuller>();
        CacheVisualModelScale();
        foreach (var old in GetComponentsInChildren<Animation>(true))
            Destroy(old);
        foreach (var old in GetComponentsInChildren<KennyLocomotionDriver>(true))
            Destroy(old);
        locomotion = null;
        _locoPlay = GetComponentInChildren<ZombieLocomotionAnimator>(true);
        _procWalk = GetComponentInChildren<ZombieProceduralLimbWalk>(true);
        if (_locoPlay == null && animator != null && animator.runtimeAnimatorController != null)
        {
            _locoPlay = animator.gameObject.AddComponent<ZombieLocomotionAnimator>();
            _locoPlay.animator = animator;
            _locoPlay.AutoPickPackStates();
        }
        if (animator != null)
        {
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            if (animator.runtimeAnimatorController != null)
            {
                animator.Rebind();
                animator.Update(0f);
            }
        }
        // Kenny/GAMWILL exports embed 100×–1000× scales; clamp if a prefab slipped through oversized.
        ClampOversizedVisualModel();
        CacheVisualModelScale();
        if (_locomotionLogCount < 6)
        {
            _locomotionLogCount++;
            if (_locoPlay != null && animator != null && animator.runtimeAnimatorController != null)
                Debug.Log($"[ZombieAI] {name}: Animator.Play loco ctrl={animator.runtimeAnimatorController.name}");
            else if (_procWalk != null)
                Debug.Log($"[ZombieAI] {name}: procedural limb walk");
            else if (animator != null && animator.runtimeAnimatorController != null)
                Debug.Log($"[ZombieAI] {name}: Animator Speed param loco ctrl={animator.runtimeAnimatorController.name}");
            else
                Debug.LogWarning($"[ZombieAI] {name}: no locomotion animator (static mesh or missing controller).");
        }
        // 3D spatial voice (periodic groans + attack snarl). Auto-added so it works on any prefab/scene zombie.
        _voice = GetComponent<ZombieVoice>();
        if (_voice == null)
            _voice = gameObject.AddComponent<ZombieVoice>();

        EnsureRuntimeHitboxes();
        ApplyMatteMaterials();

        PickNewRoamDirection();
        _cachedTargetPos = transform.position + currentDirection;
    }

    Transform _visualModel;
    Vector3 _lockedVisualScale = Vector3.one;
    bool _hasLockedVisualScale;
    ZombieVisibilityCuller _culler;

    void CacheVisualModelScale()
    {
        _visualModel = transform.Find("CharacterModel");
        if (_visualModel == null && transform.childCount > 0)
            _visualModel = transform.GetChild(0);
        if (_visualModel == null)
            return;
        _lockedVisualScale = _visualModel.localScale;
        _hasLockedVisualScale = _lockedVisualScale.x > 1e-6f;
    }

    void LateUpdate()
    {
        // Re-assert fitted scale — Animator/align quirks must not restore ×100 planet size.
        if (_hasLockedVisualScale && _visualModel != null &&
            (_visualModel.localScale - _lockedVisualScale).sqrMagnitude > 1e-6f)
            _visualModel.localScale = _lockedVisualScale;
    }

    void ClampOversizedVisualModel()
    {
        Transform model = transform.Find("CharacterModel");
        if (model == null && transform.childCount > 0)
            model = transform.GetChild(0);
        if (model == null)
            return;

        const float targetHeight = 1.7f;
        const float maxOkHeight = 2.8f;

        // If CharacterModel is ~1 but children are ×100, force the known Kenny fit (~0.01 * target is WRONG;
        // measured human size is CharacterModel ≈ target/(meshWorldAtScale1) ≈ 0.4 for Kenny).
        float embedded = 1f;
        foreach (var smr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr == null)
                continue;
            Transform t = smr.transform;
            while (t != null && t != model)
            {
                embedded = Mathf.Max(embedded, MegaUniformScale(t));
                t = t.parent;
            }
            if (smr.rootBone != null)
            {
                t = smr.rootBone;
                while (t != null && t != model)
                {
                    embedded = Mathf.Max(embedded, MegaUniformScale(t));
                    t = t.parent;
                }
            }
        }

        var renderers = model.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
            return;

        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            if (renderers[i] == null)
                continue;
            if (renderers[i].name.IndexOf("muzzle", System.StringComparison.OrdinalIgnoreCase) >= 0)
                continue;
            b.Encapsulate(renderers[i].bounds);
        }

        float height = b.size.y;
        // Prefab scale wiped to 1 → ×100 children show as ~4m+; shrink to target.
        if (height > maxOkHeight && height > 1e-4f)
        {
            model.localScale *= targetHeight / height;
            return;
        }

        // Scale near 1 with embedded ×100 but bounds still look "ok" briefly — still dangerous.
        if (embedded >= 50f && model.localScale.x > 0.2f)
        {
            // Re-measure at scale 1 equivalent: height / localScale
            float heightAtOne = height / Mathf.Max(1e-4f, model.localScale.x);
            if (heightAtOne > maxOkHeight)
                model.localScale = Vector3.one * (targetHeight / heightAtOne);
        }
    }

    static float MegaUniformScale(Transform t)
    {
        if (t == null)
            return 1f;
        string n = t.name;
        if (n.IndexOf("muzzle", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
            n.IndexOf("weapon", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return 1f;
        Vector3 ls = t.localScale;
        float m = Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z));
        if (m < 50f)
            return 1f;
        if (Mathf.Abs(Mathf.Abs(ls.x) - m) < m * 0.15f &&
            Mathf.Abs(Mathf.Abs(ls.y) - m) < m * 0.15f &&
            Mathf.Abs(Mathf.Abs(ls.z) - m) < m * 0.15f)
            return m;
        return 1f;
    }

    void ApplyMatteMaterials()
    {
        foreach (var r in GetComponentsInChildren<Renderer>(true))
        {
            if (r == null)
                continue;
            var mats = r.materials;
            for (int i = 0; i < mats.Length; i++)
                ModelMatteLighting.MakeMatte(mats[i]);
            r.materials = mats;
        }
    }

    void EnsureRuntimeHitboxes()
    {
        if (transform.Find(RuntimeHitboxRootName) != null)
            return;

        if (!TryGetLocalVisualBounds(out Bounds localBounds))
            return;

        var root = new GameObject(RuntimeHitboxRootName);
        Transform hitboxRoot = root.transform;
        hitboxRoot.SetParent(transform, false);
        hitboxRoot.localPosition = Vector3.zero;
        hitboxRoot.localRotation = Quaternion.identity;
        hitboxRoot.localScale = Vector3.one;

        Vector3 size = localBounds.size;
        float width = Mathf.Clamp(size.x * 0.84f, 0.34f, 0.56f);
        float depth = Mathf.Clamp(size.z * 0.84f, 0.34f, 0.56f);
        float fullHeight = Mathf.Max(1.35f, size.y);
        float torsoWidth = Mathf.Clamp(size.x * 0.72f, 0.3f, 0.48f);
        float torsoDepth = Mathf.Clamp(size.z * 0.7f, 0.28f, 0.44f);
        float headRadius = Mathf.Clamp(fullHeight * 0.082f, 0.12f, 0.19f);
        float bodyHeight = Mathf.Clamp(fullHeight * 0.62f, 0.84f, 1.12f);
        float torsoCenterY = Mathf.Lerp(localBounds.center.y, localBounds.max.y, 0.14f);
        float armRadius = Mathf.Clamp(width * 0.22f, 0.08f, 0.12f);
        float armHeight = Mathf.Clamp(fullHeight * 0.36f, 0.42f, 0.68f);
        float armOffsetX = Mathf.Max(width * 0.72f, 0.2f);
        float armCenterY = Mathf.Lerp(localBounds.center.y, localBounds.max.y, 0.3f);
        float legRadius = Mathf.Clamp(width * 0.24f, 0.09f, 0.13f);
        float legHeight = Mathf.Clamp(fullHeight * 0.4f, 0.5f, 0.78f);
        float legOffsetX = Mathf.Max(width * 0.2f, 0.09f);
        float legCenterY = localBounds.min.y + legHeight * 0.5f;

        var body = hitboxRoot.gameObject.AddComponent<BoxCollider>();
        body.isTrigger = true;
        body.center = new Vector3(localBounds.center.x, torsoCenterY, localBounds.center.z);
        body.size = new Vector3(torsoWidth, bodyHeight, torsoDepth);

        var head = hitboxRoot.gameObject.AddComponent<SphereCollider>();
        head.isTrigger = true;
        head.center = new Vector3(localBounds.center.x, localBounds.max.y - headRadius, localBounds.center.z);
        head.radius = headRadius;

        CreateTriggerCapsule(hitboxRoot, "LeftArmHitbox",
            new Vector3(localBounds.center.x - armOffsetX, armCenterY, localBounds.center.z),
            armRadius,
            armHeight);
        CreateTriggerCapsule(hitboxRoot, "RightArmHitbox",
            new Vector3(localBounds.center.x + armOffsetX, armCenterY, localBounds.center.z),
            armRadius,
            armHeight);
        CreateTriggerCapsule(hitboxRoot, "LeftLegHitbox",
            new Vector3(localBounds.center.x - legOffsetX, legCenterY, localBounds.center.z),
            legRadius,
            legHeight);
        CreateTriggerCapsule(hitboxRoot, "RightLegHitbox",
            new Vector3(localBounds.center.x + legOffsetX, legCenterY, localBounds.center.z),
            legRadius,
            legHeight);
    }

    static void CreateTriggerCapsule(Transform parent, string name, Vector3 localCenter, float radius, float height)
    {
        var go = new GameObject(name);
        Transform t = go.transform;
        t.SetParent(parent, false);
        t.localPosition = localCenter;
        t.localRotation = Quaternion.identity;
        t.localScale = Vector3.one;

        var capsule = go.AddComponent<CapsuleCollider>();
        capsule.isTrigger = true;
        capsule.direction = 1;
        capsule.center = Vector3.zero;
        capsule.radius = radius;
        capsule.height = Mathf.Max(height, radius * 2f + 0.01f);
    }

    bool TryGetLocalVisualBounds(out Bounds localBounds)
    {
        Renderer[] renderers = GetComponentsInChildren<Renderer>(false);
        bool hasBounds = false;
        Bounds combined = new Bounds(transform.position, Vector3.zero);

        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null)
                continue;

            if (!hasBounds)
            {
                combined = renderer.bounds;
                hasBounds = true;
            }
            else
            {
                combined.Encapsulate(renderer.bounds);
            }
        }

        if (!hasBounds)
        {
            CapsuleCollider capsule = GetComponent<CapsuleCollider>();
            if (capsule != null)
            {
                localBounds = new Bounds(capsule.center, new Vector3(capsule.radius * 2f, capsule.height, capsule.radius * 2f));
                return true;
            }

            localBounds = new Bounds();
            return false;
        }

        Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

        for (int ix = 0; ix < 2; ix++)
        {
            for (int iy = 0; iy < 2; iy++)
            {
                for (int iz = 0; iz < 2; iz++)
                {
                    Vector3 corner = new Vector3(
                        ix == 0 ? combined.min.x : combined.max.x,
                        iy == 0 ? combined.min.y : combined.max.y,
                        iz == 0 ? combined.min.z : combined.max.z);

                    Vector3 localCorner = transform.InverseTransformPoint(corner);
                    min = Vector3.Min(min, localCorner);
                    max = Vector3.Max(max, localCorner);
                }
            }
        }

        localBounds = new Bounds((min + max) * 0.5f, max - min);
        return localBounds.size.sqrMagnitude > 0.0001f;
    }

    void ResolvePlanet(bool force)
    {
        if (!force && Time.time < _nextPlanetResolveTime && planet != null)
            return;

        _nextPlanetResolveTime = Time.time + 0.5f;
        planet = PlanetReferenceResolver.ResolvePlanetTransform();
        planetCollider = ResolvePrimaryTerrainMeshCollider(planet);

        // Cache the analytic terrain + ocean queries used for land-seek (Part A). These live on the
        // planet root (PlanetOceanLayer requires a Planet on the same GameObject); fall back to a scene
        // scan so it still resolves if the tagged transform differs from the Planet component holder.
        if (planet != null)
        {
            _planetComp = planet.GetComponent<Planet>();
            _oceanLayer = planet.GetComponent<PlanetOceanLayer>();
        }
        if (_planetComp == null)
            _planetComp = Object.FindAnyObjectByType<Planet>();
        if (_oceanLayer == null)
            _oceanLayer = Object.FindAnyObjectByType<PlanetOceanLayer>();
    }

    public static MeshCollider ResolvePrimaryTerrainMeshCollider(Transform planetRoot)
    {
        if (planetRoot == null)
            return null;
        MeshCollider onRoot = planetRoot.GetComponent<MeshCollider>();
        if (onRoot != null && onRoot.enabled)
            return onRoot;
        foreach (MeshCollider mc in planetRoot.GetComponentsInChildren<MeshCollider>(false))
        {
            if (mc == null || !mc.enabled || mc.isTrigger)
                continue;
            string n = mc.gameObject.name;
            if (n.Contains("Water") || n.Contains("Atmosphere") || n.Contains("Clouds"))
                continue;
            return mc;
        }
        return null;
    }

    public void TakeDamage(int amount)
    {
        if (_dead)
            return;
        currentHealth -= amount;

        // Part B: any damage forces aggro on the player, ignoring the normal detection radius.
        Provoke();

        // Punchy generated impact thud, localised at the zombie (3D, pooled so it survives if the zombie dies).
        AudioManager.PlayHit(transform.position);

        if (hitSfx != null)
            AudioOneShotPool.PlayClip(hitSfx, transform.position, hitSfxVolume, sfxMixerGroup);
        if (hitVfxPrefab != null)
            TransientFxPool.Play(hitVfxPrefab, transform.position, Quaternion.identity, 2f);

        if (currentHealth <= 0)
            Die();
    }

    void Die()
    {
        if (_dead)
            return;
        _dead = true;

        // Free the population slot immediately so respawns aren't blocked by sinking corpses.
        ReleaseLivingSlot();

        // Generated death rattle (3D, detached so it outlives this GameObject). The optional inspector
        // deathSfx still plays too if a designer assigned one.
        if (_voice != null)
            _voice.PlayDeathGroan(deathSfxVolume);
        if (deathSfx != null)
            AudioOneShotPool.PlayClip(deathSfx, transform.position, deathSfxVolume, sfxMixerGroup);
        if (deathVfxPrefab != null)
            TransientFxPool.Play(deathVfxPrefab, transform.position, Quaternion.identity, 2f);

        ZombieKilled?.Invoke();
        var spawner = ZombieSpawner.Instance;
        if (spawner != null && spawner.isActiveAndEnabled)
            spawner.OnZombieKilled(this); // schedules respawn; corpse despawns itself after fall+sink

        StartCoroutine(CoDeathFallAndSink());
    }

    void PrepareCorpsePhysics()
    {
        if (rb != null)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic = true;
            rb.detectCollisions = false;
        }

        foreach (var col in GetComponentsInChildren<Collider>(true))
        {
            if (col != null)
                col.enabled = false;
        }

        if (_locoPlay != null)
            _locoPlay.enabled = false;
        if (_procWalk != null)
            _procWalk.enabled = false;
        if (_voice != null)
            _voice.enabled = false;
    }

    bool TryPlayDeathAnimation()
    {
        if (animator == null)
            animator = GetComponentInChildren<Animator>(true);
        if (animator == null || animator.runtimeAnimatorController == null)
            return false;

        // Same naming as the player cowboy Death (root|Death), plus short aliases.
        string[] candidates = { "root|Death", "Death", "root|Die", "Die" };
        for (int i = 0; i < candidates.Length; i++)
        {
            int hash = Animator.StringToHash(candidates[i]);
            if (!animator.HasState(0, hash))
                continue;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.applyRootMotion = false;
            animator.Play(hash, 0, 0f);
            animator.Update(0f);
            return true;
        }

        return false;
    }

    Vector3 ResolveGravityUp()
    {
        ResolvePlanet(force: false);
        Vector3 planetCenter = planet != null ? planet.position : Vector3.zero;
        Vector3 up = (transform.position - planetCenter).normalized;
        return up.sqrMagnitude > 1e-6f ? up : Vector3.up;
    }

    /// <summary>
    /// Rotation that lays the body flat on the planet surface (tangent plane), not tipped toward the planet center.
    /// Face-plant: nose into the ground. On-back: face toward the sky.
    /// </summary>
    static Quaternion ComputeSurfaceFlatFallenRotation(Vector3 surfaceUp, Vector3 facingHint, bool fallForward)
    {
        if (surfaceUp.sqrMagnitude < 1e-6f)
            surfaceUp = Vector3.up;
        surfaceUp.Normalize();

        // Head/feet axis along the surface in the direction they were facing.
        Vector3 alongSurface = Vector3.ProjectOnPlane(facingHint, surfaceUp);
        if (alongSurface.sqrMagnitude < 1e-6f)
            alongSurface = Vector3.ProjectOnPlane(Vector3.forward, surfaceUp);
        if (alongSurface.sqrMagnitude < 1e-6f)
            alongSurface = Vector3.ProjectOnPlane(Vector3.right, surfaceUp);
        alongSurface.Normalize();

        // LookRotation(forward, upwards):
        //  face-plant → look into the ground, head along the surface
        //  on back    → look at the sky, head along the surface
        Vector3 lookFwd = fallForward ? -surfaceUp : surfaceUp;
        return Quaternion.LookRotation(lookFwd, alongSurface);
    }

    bool TryGetVisualBounds(out Bounds bounds)
    {
        bounds = default;
        var renderers = GetComponentsInChildren<Renderer>(true);
        bool any = false;
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null || !r.enabled)
                continue;
            if (r.name.IndexOf("muzzle", System.StringComparison.OrdinalIgnoreCase) >= 0)
                continue;
            if (!any)
            {
                bounds = r.bounds;
                any = true;
            }
            else
                bounds.Encapsulate(r.bounds);
        }
        return any;
    }

    static bool IsTerrainCollider(Collider col)
    {
        if (col == null || col.isTrigger)
            return false;
        string n = col.gameObject.name;
        if (n.IndexOf("Water", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return false;
        if (n.IndexOf("Ocean", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return false;
        if (n.IndexOf("Atmosphere", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return false;
        if (n.IndexOf("Cloud", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return false;
        return true;
    }

    /// <summary>
    /// Terrain contact from a downward ray. Normal is the mesh surface normal (slope), not planet-center radial.
    /// </summary>
    bool TryGetTerrainContact(Vector3 nearPos, Vector3 fallbackUp, out Vector3 point, out Vector3 normal)
    {
        point = nearPos;
        normal = fallbackUp.sqrMagnitude > 1e-6f ? fallbackUp.normalized : Vector3.up;

        Vector3 up = normal;
        Vector3 origin = nearPos + up * 10f;
        RaycastHit[] hits = Physics.RaycastAll(origin, -up, 30f, ~0, QueryTriggerInteraction.Ignore);
        float bestDist = float.MaxValue;
        bool found = false;
        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit hit = hits[i];
            if (!IsTerrainCollider(hit.collider))
                continue;
            if (hit.distance >= bestDist)
                continue;
            bestDist = hit.distance;
            point = hit.point;
            normal = hit.normal;
            found = true;
        }

        if (found)
        {
            // Keep normal pointing "out" of the planet (same hemisphere as fallback up).
            if (Vector3.Dot(normal, up) < 0f)
                normal = -normal;
            return true;
        }

        ResolvePlanet(force: false);
        if (_planetComp != null && planet != null)
        {
            Vector3 dir = (nearPos - planet.position).normalized;
            if (dir.sqrMagnitude < 1e-6f)
                dir = up;
            float r = _planetComp.GetSurfaceRadiusWorld(dir);
            point = planet.position + dir * r;
            normal = dir;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Seat the fallen corpse on the terrain mesh via spine raycasts (not loose renderer AABBs — those cause hover).
    /// </summary>
    void SeatFallenCorpseOnSurface(Vector3 surfaceUp)
    {
        if (surfaceUp.sqrMagnitude < 1e-6f)
            return;
        surfaceUp.Normalize();

        Vector3 along = Vector3.ProjectOnPlane(transform.up, surfaceUp);
        if (along.sqrMagnitude < 1e-6f)
            along = Vector3.ProjectOnPlane(transform.forward, surfaceUp);
        if (along.sqrMagnitude < 1e-6f)
            return;
        along.Normalize();

        float bodyLen = 1.4f;
        if (TryGetVisualBounds(out Bounds bounds))
        {
            // Project AABB size onto the head-feet axis; shrink — world bounds are usually oversized for skinned meshes.
            float alongExtent = Mathf.Abs(along.x) * bounds.extents.x
                                + Mathf.Abs(along.y) * bounds.extents.y
                                + Mathf.Abs(along.z) * bounds.extents.z;
            bodyLen = Mathf.Clamp(alongExtent * 1.4f, 0.8f, 2.4f);
        }

        // Sample along the lying body and average how far each point is from the real terrain.
        float[] samples = { -0.3f, -0.05f, 0.2f, 0.45f };
        float sumAdj = 0f;
        int count = 0;
        float clearance = Mathf.Max(0f, deathSurfaceClearance);
        for (int i = 0; i < samples.Length; i++)
        {
            Vector3 sample = transform.position + along * (bodyLen * samples[i]);
            if (!TryGetTerrainContact(sample, surfaceUp, out Vector3 ground, out _))
                continue;
            // Positive = sample is below ground (need lift); negative = hovering (need lower).
            sumAdj += Vector3.Dot(ground - sample, surfaceUp) + clearance;
            count++;
        }

        if (count <= 0)
            return;

        float adjust = sumAdj / count;
        if (Mathf.Abs(adjust) > 1e-4f)
            transform.position += surfaceUp * adjust;
    }

    IEnumerator CoDeathFallAndSink()
    {
        PrepareCorpsePhysics();

        Vector3 radialUp = ResolveGravityUp();
        // Flat to the TERRAIN (mesh normal), not merely 90° from the planet centre.
        if (!TryGetTerrainContact(transform.position, radialUp, out _, out Vector3 surfaceUp))
            surfaceUp = radialUp;

        TryPlayDeathAnimation();

        Quaternion startRot = transform.rotation;
        Vector3 startPos = transform.position;
        bool fallForward = Random.value < 0.5f;
        Quaternion fallenRot = ComputeSurfaceFlatFallenRotation(surfaceUp, transform.forward, fallForward);

        float fallDur = Mathf.Max(0.15f, proceduralFallSeconds);
        float t = 0f;
        while (t < fallDur)
        {
            t += Time.deltaTime;
            float u = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / fallDur));
            transform.SetPositionAndRotation(startPos, Quaternion.Slerp(startRot, fallenRot, u));
            yield return null;
        }

        transform.SetPositionAndRotation(startPos, fallenRot);
        FreezeFallenPose();

        // Re-sample terrain normal at the corpse, re-flatten to that slope, then seat on the mesh.
        radialUp = ResolveGravityUp();
        if (!TryGetTerrainContact(transform.position, radialUp, out _, out surfaceUp))
            surfaceUp = radialUp;

        Vector3 headHint = Vector3.ProjectOnPlane(transform.up, surfaceUp);
        if (headHint.sqrMagnitude < 1e-6f)
            headHint = transform.forward;
        fallenRot = ComputeSurfaceFlatFallenRotation(surfaceUp, headHint, fallForward);
        transform.rotation = fallenRot;
        if (animator != null)
            animator.Update(0f);
        SeatFallenCorpseOnSurface(surfaceUp);
        SeatFallenCorpseOnSurface(surfaceUp);

        Vector3 sinkStart = transform.position;
        Vector3 sinkEnd = sinkStart - surfaceUp * Mathf.Max(0.25f, deathSinkDistance);
        float sinkDur = Mathf.Max(32f, deathSinkSeconds);
        t = 0f;
        while (t < sinkDur)
        {
            t += Time.deltaTime;
            float u = Mathf.Clamp01(t / sinkDur);
            transform.SetPositionAndRotation(Vector3.Lerp(sinkStart, sinkEnd, u), fallenRot);
            yield return null;
        }

        Destroy(gameObject);
    }

    /// <summary>
    /// Hold the current skinned pose without disabling the Animator (disable can snap back to bind pose).
    /// </summary>
    void FreezeFallenPose()
    {
        if (animator == null)
            animator = GetComponentInChildren<Animator>(true);
        if (animator == null)
            return;

        // Sample whatever frame we're on, then freeze so limbs stay as they were when they hit the ground.
        animator.speed = 0f;
        animator.Update(0f);
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
    }

    void FixedUpdate()
    {
        if (_dead || rb == null)
            return;

        _fixedStepCounter++;
        TryRefreshSceneReferences();

        Vector3 planetCenter = planet != null ? planet.position : Vector3.zero;
        Vector3 gravityUp = (transform.position - planetCenter).normalized;
        if (gravityUp.sqrMagnitude < 1e-6f)
            gravityUp = Vector3.up;

        if (player == null)
        {
            // Still drive Idle/Walk while the player ref is resolving.
            UpdateAnimator(gravityUp);
            return;
        }

        rb.AddForce(-gravityUp * gravityStrength, ForceMode.Acceleration);

        Vector3 toPlayerWorld = player.position - transform.position;
        float distanceToPlayerSq = toPlayerWorld.sqrMagnitude;
        float attackRadiusSq = attackRadius * attackRadius;
        float detectionRadiusSq = detectionRadius * detectionRadius;
        float farSq = farDecisionDistance * farDecisionDistance;
        float fullAiNearSq = fullAiNearDistance * fullAiNearDistance;
        bool wantsFullAi = distanceToPlayerSq <= fullAiNearSq;
        bool hasFullAiSlot = wantsFullAi && TryAcquireFullAiSlot();
        _cachedFullAiActive = hasFullAiSlot;
        int decisionPeriod = Mathf.Max(1, aiDecisionPeriod);
        if (distanceToPlayerSq > farSq)
            decisionPeriod = Mathf.Max(decisionPeriod, Mathf.RoundToInt(decisionPeriod * Mathf.Max(1f, farDecisionPeriodMultiplier)));
        if (!_cachedFullAiActive)
            decisionPeriod = Mathf.Max(decisionPeriod, Mathf.RoundToInt(decisionPeriod * Mathf.Max(1f, cheapDecisionPeriodMultiplier)));
        bool runDecision = (_fixedStepCounter + _aiDecisionPhase) % decisionPeriod == 0;

        if (runDecision)
        {
            bool provoked = IsProvoked();
            // Detection does NOT require a full-AI performance slot — if the player is within range, aggro.
            bool detectsPlayer = distanceToPlayerSq <= detectionRadiusSq;
            bool chasing = provoked || detectsPlayer;

            // Keep an engaged provoked zombie locked on once it closes within detection range.
            if (provoked && detectsPlayer)
                _provokedUntilTime = Time.time + Mathf.Max(0f, provokedAggroDuration);

            // In water: always seek land first (even if aggro'd). Zombies hate water and will not chase
            // through the ocean — they climb out, then resume chase/roam on dry ground.
            if (IsUnderwater() && TryGetLandSeekTarget(gravityUp, out Vector3 landTarget))
            {
                _cachedShouldAttack = false;
                // Preserve chase intent so speed stays full while escaping water toward the player side.
                _cachedChasing = chasing;
                _cachedTargetPos = landTarget;
            }
            else if (chasing && distanceToPlayerSq <= attackRadiusSq)
            {
                _cachedShouldAttack = true;
                _cachedChasing = true;
                _cachedTargetPos = player.position;
            }
            else if (chasing)
            {
                _cachedShouldAttack = false;
                _cachedChasing = true;
                _cachedTargetPos = player.position;
            }
            else
            {
                _cachedShouldAttack = false;
                _cachedChasing = false;
                roamTimer += Time.fixedDeltaTime * decisionPeriod;
                if (roamTimer >= roamChangeInterval)
                {
                    PickNewRoamDirection();
                    roamTimer = 0f;
                }
                _cachedTargetPos = transform.position + currentDirection;
            }
        }

        if (_cachedShouldAttack)
            Attack();
        else
        {
            isAttacking = false;
            // Chasing (incl. provoked) moves at full speed; idle roam/land-seek throttles when far/off-screen.
            float speedScale = _cachedChasing ? 1f : (_cachedFullAiActive ? 1f : cheapIdleSpeedMultiplier);
            MoveTowards(_cachedTargetPos, gravityUp, speedScale);
        }

        int stickPeriod = Mathf.Max(1, surfaceStickRaycastPeriod);
        if ((_fixedStepCounter + _surfaceStickPhase) % stickPeriod == 0
            && Physics.Raycast(transform.position, -gravityUp, out RaycastHit hit, 5f, ~0, QueryTriggerInteraction.Ignore))
        {
            float dist = hit.distance;
            if (dist > surfaceStickDistance)
            {
                Vector3 pull = -gravityUp * (dist - surfaceStickDistance);
                rb.AddForce(pull * surfaceStickForce);
            }
        }

        UpdateAnimator(gravityUp);
    }

    void UpdateAnimator(Vector3 gravityUp)
    {
        bool shown = _culler == null || _culler.IsShown;
        float speed = Vector3.ProjectOnPlane(rb.linearVelocity, gravityUp).magnitude;
        float nominal = Mathf.Max(0.5f, moveSpeed);

        // Same as PlayerCharacterAnimator: Play Idle/Walk when move state changes.
        if (_locoPlay != null)
        {
            _locoPlay.SetPlanarSpeed(speed, nominal, shown);
            return;
        }

        if (_procWalk != null)
        {
            _procWalk.SetPlanarSpeed(speed, nominal, shown);
            return;
        }

        bool near = animateMaxDistance <= 1f || player == null;
        if (!near)
        {
            float maxSq = animateMaxDistance * animateMaxDistance;
            near = (player.position - transform.position).sqrMagnitude <= maxSq;
        }
        bool want = shown && near;

        if (animator == null)
            return;
        if (animator.enabled != want)
            animator.enabled = want;
        if (!want)
            return;

        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        animator.SetFloat(SpeedId, speed);
        animator.speed = Mathf.Clamp(speed / nominal, 0.35f, 1.6f);
    }

    void MoveTowards(Vector3 targetPos, Vector3 gravityUp, float speedScale)
    {
        Vector3 desiredDir = (targetPos - transform.position).normalized;
        Vector3 tangentDir = Vector3.ProjectOnPlane(desiredDir, gravityUp).normalized;

        // Refuse to walk into the ocean: if the next step is wet, deflect landward (roam/chase along shore).
        if (tangentDir.sqrMagnitude > 1e-6f && WouldStepIntoWater(tangentDir, gravityUp))
        {
            Vector3 land = ComputeLandwardTangent(gravityUp);
            if (land.sqrMagnitude > 1e-6f)
            {
                // Blend toward land so they still make progress along the coastline toward the target.
                Vector3 alongShore = Vector3.ProjectOnPlane(tangentDir, land);
                if (alongShore.sqrMagnitude > 1e-6f)
                    tangentDir = (land * 0.65f + alongShore.normalized * 0.35f).normalized;
                else
                    tangentDir = land;
            }
            else
            {
                tangentDir = Vector3.zero; // stop at the waterline rather than wade in
            }
        }

        if (!isAttacking && tangentDir.sqrMagnitude > 1e-6f)
        {
            float scale = Mathf.Clamp01(speedScale);
            rb.linearVelocity = tangentDir * (moveSpeed * scale) + Vector3.Project(rb.linearVelocity, gravityUp);
        }
        else if (!isAttacking)
        {
            rb.linearVelocity = Vector3.Project(rb.linearVelocity, gravityUp);
        }

        if (tangentDir.sqrMagnitude > 0.001f)
        {
            Quaternion targetRot = Quaternion.LookRotation(tangentDir, gravityUp);
            rb.MoveRotation(Quaternion.Slerp(rb.rotation, targetRot, rotationSpeed * Time.fixedDeltaTime));
        }
    }

    /// <summary>True if walking along <paramref name="tangentDir"/> would put the feet below sea level.</summary>
    bool WouldStepIntoWater(Vector3 tangentDir, Vector3 gravityUp)
    {
        if (_planetComp == null || _oceanLayer == null || tangentDir.sqrMagnitude < 1e-6f)
            return false;

        Vector3 center = _planetComp.transform.position;
        Vector3 radial = (transform.position - center).normalized;
        if (radial.sqrMagnitude < 1e-6f)
            return false;

        float oceanR = _oceanLayer.ResolveOceanRadiusWorld() + Mathf.Max(0f, waterEdgeClearance);
        float look = Mathf.Max(0.5f, waterLookAheadDistance);
        float planetR = Mathf.Max(1f, Vector3.Distance(transform.position, center));
        float angle = look / planetR; // radians along the sphere
        Vector3 sampleDir = (radial + tangentDir.normalized * angle).normalized;
        float terrainR = _planetComp.GetSurfaceRadiusWorld(sampleDir);
        return terrainR < oceanR;
    }

    void TryRefreshSceneReferences()
    {
        if (player == null && Time.time >= _nextPlayerResolveTime)
        {
            player = RuntimeSceneRefs.GetPlayerTransform();
            _nextPlayerResolveTime = Time.time + 0.35f;
        }

        if (planet == null && Time.time >= _nextPlanetResolveTime)
            ResolvePlanet(force: true);
    }

    void Attack()
    {
        if (!isAttacking)
        {
            isAttacking = true;
            rb.linearVelocity = Vector3.zero;
            if (_voice != null)
                _voice.PlayAttackSnarl();
        }

        if (Time.time < nextAttackTime)
            return;

        nextAttackTime = Time.time + Mathf.Max(0.1f, attackCooldown);
        if (player != null && !PlayerHealth.IsDead)
            player.SendMessage("TakeDamage", attackDamage, SendMessageOptions.DontRequireReceiver);
    }

    void PickNewRoamDirection()
    {
        Vector3 planetCenter = planet != null ? planet.position : Vector3.zero;
        Vector3 gravityUp = (transform.position - planetCenter).normalized;
        if (gravityUp.sqrMagnitude < 1e-6f)
            gravityUp = Vector3.up;

        // Prefer a dry roam heading so idle zombies hug shorelines instead of walking into the sea.
        for (int i = 0; i < 8; i++)
        {
            Vector3 randomDir = Random.onUnitSphere;
            Vector3 candidate = Vector3.ProjectOnPlane(randomDir, gravityUp).normalized;
            if (candidate.sqrMagnitude < 1e-6f)
                continue;
            if (!WouldStepIntoWater(candidate, gravityUp))
            {
                currentDirection = candidate;
                return;
            }
        }

        // All random picks were wet — face landward.
        Vector3 land = ComputeLandwardTangent(gravityUp);
        currentDirection = land.sqrMagnitude > 1e-6f
            ? land
            : Vector3.ProjectOnPlane(Random.onUnitSphere, gravityUp).normalized;
    }

    /// <summary>Part B: force this zombie to aggro on the player from any distance (called from <see cref="TakeDamage"/>).</summary>
    public void Provoke()
    {
        if (provokedAggroDuration <= 0f)
            return;
        _provoked = true;
        _provokedUntilTime = Time.time + provokedAggroDuration;
        if (player == null)
            player = RuntimeSceneRefs.GetPlayerTransform();
    }

    bool IsProvoked()
    {
        if (!_provoked)
            return false;
        if (Time.time >= _provokedUntilTime)
        {
            _provoked = false;
            return false;
        }
        return true;
    }

    /// <summary>Part A: true when submerged past the threshold (analytic ocean-surface query, no physics).</summary>
    bool IsUnderwater()
    {
        if (_oceanLayer == null)
            return false;
        return _oceanLayer.GetDepthBelowSurface(transform.position) > inWaterDepthThreshold;
    }

    /// <summary>
    /// Part A: resolve a movement target toward the nearest land above sea level. The chosen heading is
    /// persisted for <see cref="landSeekRecomputeInterval"/> seconds to avoid per-frame jitter.
    /// </summary>
    bool TryGetLandSeekTarget(Vector3 gravityUp, out Vector3 target)
    {
        target = transform.position;
        if (_planetComp == null || _oceanLayer == null)
            return false;

        if (Time.time >= _nextLandSeekRecomputeTime || _landSeekDir.sqrMagnitude < 1e-6f)
        {
            _landSeekDir = ComputeLandwardTangent(gravityUp);
            _nextLandSeekRecomputeTime = Time.time + Mathf.Max(0.05f, landSeekRecomputeInterval);
        }

        if (_landSeekDir.sqrMagnitude < 1e-6f)
            return false;

        target = transform.position + _landSeekDir;
        return true;
    }

    /// <summary>
    /// Samples a ring of tangent compass directions around the zombie and returns the unit tangent heading
    /// whose analytic terrain radius is highest (the strongest rise toward land). This always yields a
    /// landward heading even when fully surrounded by water, because the highest sampled terrain is the
    /// closest route up out of the ocean. Pure analytic calls (no allocations, no raycasts).
    /// </summary>
    Vector3 ComputeLandwardTangent(Vector3 gravityUp)
    {
        Vector3 center = _planetComp.transform.position;
        Vector3 radial = (transform.position - center).normalized;
        if (radial.sqrMagnitude < 1e-6f)
            return Vector3.zero;

        Vector3 t1 = Vector3.Cross(radial, Vector3.up);
        if (t1.sqrMagnitude < 1e-6f)
            t1 = Vector3.Cross(radial, Vector3.right);
        t1.Normalize();
        Vector3 t2 = Vector3.Cross(radial, t1);

        int samples = Mathf.Clamp(landSeekSampleCount, 4, 32);
        float lookAhead = Mathf.Deg2Rad * Mathf.Clamp(landSeekSampleAngleDegrees, 1f, 45f);
        float cosLook = Mathf.Cos(lookAhead);
        float sinLook = Mathf.Sin(lookAhead);

        Vector3 bestTangent = Vector3.zero;
        float bestRadius = float.NegativeInfinity;

        for (int i = 0; i < samples; i++)
        {
            float a = (i / (float)samples) * Mathf.PI * 2f;
            Vector3 tangent = Mathf.Cos(a) * t1 + Mathf.Sin(a) * t2;
            // Rotate the radial direction toward this heading by the look-ahead angle, then sample terrain there.
            Vector3 sampleDir = (radial * cosLook + tangent * sinLook).normalized;
            float terrainR = _planetComp.GetSurfaceRadiusWorld(sampleDir);
            if (terrainR > bestRadius)
            {
                bestRadius = terrainR;
                bestTangent = tangent;
            }
        }

        if (bestTangent.sqrMagnitude < 1e-6f)
            return Vector3.zero;

        return Vector3.ProjectOnPlane(bestTangent, gravityUp).normalized;
    }

    bool TryAcquireFullAiSlot()
    {
        DiagnosticsFullAiCapLastSeen = Mathf.Max(1, maxFullAiEnemiesNearPlayer);
        DiagnosticsFullAiNearDistanceLastSeen = Mathf.Max(1f, fullAiNearDistance);
        int frame = Time.frameCount;
        if (s_FullAiSlotsFrame != frame)
        {
            s_FullAiSlotsFrame = frame;
            s_FullAiSlotsUsed = 0;
        }

        int cap = DiagnosticsFullAiCapLastSeen;
        if (s_FullAiSlotsUsed >= cap)
            return false;
        s_FullAiSlotsUsed++;
        return true;
    }
}
