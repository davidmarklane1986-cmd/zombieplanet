using UnityEngine;
using UnityEngine.Audio;

/// <summary>
/// Stargrave 1.3-style enemy AI (chase, attack, roam, water swim, performance tiers) on the zombie prefab.
/// Requires Rigidbody. Optional Animator "Speed" float, hit/death SFX/VFX.
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
            if (all[i] != null && all[i].isActiveAndEnabled)
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
    [Min(0.05f)] public float landSeekProbeRefreshSeconds = 0.5f;
    [Range(1, 8)] public int aiDecisionPeriod = 2;
    [Min(1f)] public float farDecisionDistance = 80f;
    [Range(1f, 6f)] public float farDecisionPeriodMultiplier = 2f;
    [Min(1f)] public float fullAiNearDistance = 35f;
    [Min(1)] public int maxFullAiEnemiesNearPlayer = 120;
    [Range(0f, 1f)] public float cheapIdleSpeedMultiplier = 0.45f;
    [Range(1f, 8f)] public float cheapDecisionPeriodMultiplier = 3f;

    [Header("Water")]
    public float swimZonePadding = 0.5f;
    [Range(0f, 1f)] public float swimGravityScale = 0.15f;
    public float swimSurfaceSpring = 14f;
    public float swimRadialDamping = 4f;
    public float swimBobAmplitude = 0.08f;
    public float swimBobFrequency = 1f;
    public float landProbeDistance = 4f;
    public float landClearance = 0.35f;
    [Range(0f, 1f)] public float landSeekBlend = 0.65f;

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

    [Header("Animation (optional)")]
    public Animator animator;
    static readonly int SpeedId = Animator.StringToHash("Speed");

    Transform player;
    Rigidbody rb;
    Vector3 currentDirection;
    float roamTimer;
    bool isAttacking;

    Transform planet;
    PlanetWaterLayer waterLayer;
    MeshCollider planetCollider;
    Planet planetComp;
    bool inWater;
    float nextAttackTime;
    int _surfaceStickPhase;
    int _fixedStepCounter;
    float _nextLandProbeTime;
    Vector3 _cachedLandTangent;
    float _nextPlayerResolveTime;
    float _nextPlanetResolveTime;
    int _aiDecisionPhase;
    bool _cachedShouldAttack;
    Vector3 _cachedTargetPos;
    float _cachedLandBlend;
    bool _cachedFullAiActive;
    float _cachedWaterShell;
    Vector3 _cachedWaterCenter;
    bool _hasWaterShell;

    static int s_FullAiSlotsFrame = -1;
    static int s_FullAiSlotsUsed;

    int currentHealth;
    bool _dead;

    public int CurrentHealth => currentHealth;

    void OnEnable() => LivingCount++;

    void OnDestroy() => LivingCount--;

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
        ResolvePlanetAndWater(force: true);

        int period = Mathf.Max(1, surfaceStickRaycastPeriod);
        _surfaceStickPhase = Random.Range(0, period);
        _aiDecisionPhase = Random.Range(0, Mathf.Max(1, aiDecisionPeriod));
        _nextLandProbeTime = Time.fixedTime;

        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        EnsureRuntimeHitboxes();

        PickNewRoamDirection();
        _cachedTargetPos = transform.position + currentDirection;
        _cachedLandBlend = 0f;
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

    void ResolvePlanetAndWater(bool force)
    {
        if (!force && Time.time < _nextPlanetResolveTime && planet != null)
            return;

        _nextPlanetResolveTime = Time.time + 0.5f;
        planet = PlanetReferenceResolver.ResolvePlanetTransform();
        planetComp = planet != null ? planet.GetComponent<Planet>() : null;
        waterLayer = planet != null ? planet.GetComponentInChildren<PlanetWaterLayer>(true) : null;
        planetCollider = ResolvePrimaryTerrainMeshCollider(planet);
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
        if (deathSfx != null)
            AudioOneShotPool.PlayClip(deathSfx, transform.position, deathSfxVolume, sfxMixerGroup);
        if (deathVfxPrefab != null)
            TransientFxPool.Play(deathVfxPrefab, transform.position, Quaternion.identity, 2f);

        ZombieKilled?.Invoke();
        var spawner = ZombieSpawner.Instance;
        if (spawner != null && spawner.isActiveAndEnabled)
            spawner.OnZombieKilled(this);
        else
            Destroy(gameObject);
    }

    void FixedUpdate()
    {
        if (_dead || rb == null)
            return;

        _fixedStepCounter++;
        TryRefreshSceneReferences();

        if (player == null)
            return;

        RefreshWaterCache();

        Vector3 planetCenter = planet != null ? planet.position : Vector3.zero;
        Vector3 gravityUp = (transform.position - planetCenter).normalized;
        if (gravityUp.sqrMagnitude < 1e-6f)
            gravityUp = Vector3.up;

        inWater = ComputeInWater();

        float grav = gravityStrength * (inWater ? swimGravityScale : 1f);
        rb.AddForce(-gravityUp * grav, ForceMode.Acceleration);

        if (inWater)
            ApplySwimSurfaceForces();

        Vector3 toPlayerWorld = player.position - transform.position;
        float distanceToPlayerSq = toPlayerWorld.sqrMagnitude;
        float attackRadiusSq = attackRadius * attackRadius;
        float detectionRadiusSq = detectionRadius * detectionRadius;
        float landBlend = landSeekBlend * (inWater ? 1f : 0f);
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
            if (_cachedFullAiActive && distanceToPlayerSq <= attackRadiusSq)
                _cachedShouldAttack = true;
            else if (_cachedFullAiActive && distanceToPlayerSq <= detectionRadiusSq)
            {
                _cachedShouldAttack = false;
                _cachedTargetPos = player.position;
                _cachedLandBlend = landBlend;
            }
            else
            {
                _cachedShouldAttack = false;
                roamTimer += Time.fixedDeltaTime * decisionPeriod;
                if (roamTimer >= roamChangeInterval)
                {
                    PickNewRoamDirection();
                    roamTimer = 0f;
                }
                _cachedTargetPos = transform.position + currentDirection;
                _cachedLandBlend = _cachedFullAiActive ? landBlend : 0f;
            }
        }

        if (_cachedShouldAttack)
            Attack();
        else
        {
            isAttacking = false;
            MoveTowards(_cachedTargetPos, gravityUp, _cachedLandBlend, _cachedFullAiActive ? 1f : cheapIdleSpeedMultiplier);
        }

        int stickPeriod = Mathf.Max(1, surfaceStickRaycastPeriod);
        if (!inWater
            && (_fixedStepCounter + _surfaceStickPhase) % stickPeriod == 0
            && Physics.Raycast(transform.position, -gravityUp, out RaycastHit hit, 5f, ~0, QueryTriggerInteraction.Ignore))
        {
            float dist = hit.distance;
            if (dist > surfaceStickDistance)
            {
                Vector3 pull = -gravityUp * (dist - surfaceStickDistance);
                rb.AddForce(pull * surfaceStickForce);
            }
        }

        if (animator != null)
        {
            float speed = Vector3.ProjectOnPlane(rb.linearVelocity, gravityUp).magnitude;
            animator.SetFloat(SpeedId, speed);
        }
    }

    bool ComputeInWater()
    {
        if (!_hasWaterShell)
            return false;
        float shellWithPadding = _cachedWaterShell + swimZonePadding;
        float dSq = (rb.position - _cachedWaterCenter).sqrMagnitude;
        return dSq < shellWithPadding * shellWithPadding;
    }

    void ApplySwimSurfaceForces()
    {
        if (!_hasWaterShell)
            return;

        Vector3 wc = _cachedWaterCenter;
        Vector3 body = rb.position;
        Vector3 fromW = body - wc;
        if (fromW.sqrMagnitude < 1e-8f)
            return;

        Vector3 radial = fromW.normalized;
        float bodyR = Vector3.Dot(body - wc, radial);

        float shell = _cachedWaterShell;
        float bob = Mathf.Sin(Time.fixedTime * swimBobFrequency * Mathf.PI * 2f) * swimBobAmplitude;
        float targetR = shell + bob;
        float err = bodyR - targetR;
        float radialSpeed = Vector3.Dot(rb.linearVelocity, radial);
        Vector3 spring = -radial * (err * swimSurfaceSpring + radialSpeed * swimRadialDamping);
        rb.AddForce(spring, ForceMode.Acceleration);
    }

    Vector3 GetBestLandSeekTangent(Vector3 gravityUp)
    {
        if (!_hasWaterShell || planet == null)
            return Vector3.zero;

        float shell = _cachedWaterShell;
        Vector3 planetCenter = planet.position;

        Vector3 reference = Mathf.Abs(Vector3.Dot(gravityUp, Vector3.up)) > 0.92f ? Vector3.right : Vector3.up;
        Vector3 bitangent = Vector3.Cross(gravityUp, reference).normalized;
        Vector3 tangentU = Vector3.Cross(gravityUp, bitangent).normalized;

        Vector3 bestTang = Vector3.zero;
        float bestScore = -1f;
        const int samples = 12;
        for (int i = 0; i < samples; i++)
        {
            float ang = i * Mathf.PI * 2f / samples;
            Vector3 tang = (Mathf.Cos(ang) * tangentU + Mathf.Sin(ang) * bitangent).normalized;
            Vector3 probe = transform.position + tang * landProbeDistance;
            Vector3 radial = (probe - planetCenter).normalized;
            Ray ray = new Ray(planetCenter + radial * 6000f, -radial);
            RaycastHit hit = default;
            bool ok = planetCollider != null && planetCollider.Raycast(ray, out hit, 12000f);
            if (!ok && Physics.Raycast(ray, out hit, 12000f, ~0, QueryTriggerInteraction.Ignore) &&
                hit.collider != null && hit.collider.transform.IsChildOf(planet))
                ok = true;
            if (!ok)
                continue;

            float surfaceR = Vector3.Distance(hit.point, planetCenter);
            float score = surfaceR - shell;
            if (score > bestScore)
            {
                bestScore = score;
                bestTang = tang;
            }
        }

        if (bestScore >= landClearance)
            return bestTang;

        if (player != null)
        {
            float pr = Vector3.Distance(player.position, _cachedWaterCenter);
            if (pr > shell + landClearance * 0.5f)
            {
                Vector3 toPlayer = Vector3.ProjectOnPlane(player.position - transform.position, gravityUp).normalized;
                if (toPlayer.sqrMagnitude > 1e-4f)
                    return toPlayer;
            }
        }

        return bestTang.sqrMagnitude > 1e-4f ? bestTang : tangentU;
    }

    void MoveTowards(Vector3 targetPos, Vector3 gravityUp, float landBlend, float speedScale)
    {
        Vector3 desiredDir = (targetPos - transform.position).normalized;
        Vector3 tangentDir = Vector3.ProjectOnPlane(desiredDir, gravityUp).normalized;

        if (inWater && landBlend > 0f)
        {
            if (Time.fixedTime >= _nextLandProbeTime)
            {
                _cachedLandTangent = GetBestLandSeekTangent(gravityUp);
                _nextLandProbeTime = Time.fixedTime + Mathf.Max(0.05f, landSeekProbeRefreshSeconds);
            }

            if (_cachedLandTangent.sqrMagnitude > 1e-4f)
                tangentDir = Vector3.Slerp(tangentDir, _cachedLandTangent, landBlend).normalized;
        }

        if (!isAttacking)
        {
            float scale = Mathf.Clamp01(speedScale);
            rb.linearVelocity = tangentDir * (moveSpeed * scale) + Vector3.Project(rb.linearVelocity, gravityUp);
        }

        if (tangentDir.sqrMagnitude > 0.001f)
        {
            Quaternion targetRot = Quaternion.LookRotation(tangentDir, gravityUp);
            rb.MoveRotation(Quaternion.Slerp(rb.rotation, targetRot, rotationSpeed * Time.fixedDeltaTime));
        }
    }

    void TryRefreshSceneReferences()
    {
        if (player == null && Time.time >= _nextPlayerResolveTime)
        {
            player = RuntimeSceneRefs.GetPlayerTransform();
            _nextPlayerResolveTime = Time.time + 0.35f;
        }

        if (planet == null && Time.time >= _nextPlanetResolveTime)
            ResolvePlanetAndWater(force: true);
    }

    void RefreshWaterCache()
    {
        if (waterLayer != null)
        {
            _cachedWaterShell = waterLayer.GetWorldWaterShellRadius();
            _hasWaterShell = _cachedWaterShell > 0f;
            if (_hasWaterShell)
                _cachedWaterCenter = waterLayer.GetWaterShellWorldCenter();
            return;
        }

        if (planetComp != null && planet != null)
        {
            float w = planetComp.GetWaterRadiusWorld();
            if (w > 0f)
            {
                _cachedWaterShell = w;
                _cachedWaterCenter = planet.position;
                _hasWaterShell = true;
                return;
            }
        }

        _hasWaterShell = false;
    }

    void Attack()
    {
        if (!isAttacking)
        {
            isAttacking = true;
            rb.linearVelocity = Vector3.zero;
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

        Vector3 randomDir = Random.onUnitSphere;
        currentDirection = Vector3.ProjectOnPlane(randomDir, gravityUp).normalized;
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
