using System.Collections.Generic;
using UnityEngine;

/// <summary>Common planet-aware root for autonomous workers and soldiers.</summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider))]
public sealed class FactionNpc : MonoBehaviour, IFactionDamageable
{
    public FactionNpcRole Role { get; private set; }
    public FactionController Faction { get; private set; }
    public FactionNpcMotor Motor { get; private set; }
    public FactionHealth Health { get; private set; }
    public WorkerAI Worker { get; private set; }
    public SoldierAI Soldier { get; private set; }
    public MerchantAI Merchant { get; private set; }
    public NobleAI Noble { get; private set; }
    public bool IsDead => Health == null || Health.IsDead;
    public int CurrentHealth => Health != null ? Health.CurrentHealth : 0;
    public int MaxHealth => Health != null ? Health.MaxHealth : 1;
    public bool IsFactionTargetable => !IsDead && isActiveAndEnabled;
    public FactionController OwningFaction => Faction;
    public Transform TargetTransform => transform;

    public static FactionNpc CreateResourceGatherer(FactionController faction, Vector3 surfaceAxis)
    {
        if (faction == null || faction.Simulation == null)
            return null;

        return Create(
            faction,
            FactionNpcRole.ResourceGatherer,
            faction.Simulation.workerCharacter,
            surfaceAxis);
    }

    public static FactionNpc CreateInfantry(FactionController faction, Vector3 surfaceAxis)
    {
        if (faction == null || faction.Simulation == null)
            return null;

        return Create(
            faction,
            FactionNpcRole.Infantry,
            faction.Simulation.soldierCharacter,
            surfaceAxis);
    }

    public static FactionNpc CreateArcher(FactionController faction, Vector3 surfaceAxis)
    {
        if (faction == null || faction.Simulation == null)
            return null;

        PlayableCharacterDef visual = faction.Simulation.archerCharacter != null
            ? faction.Simulation.archerCharacter
            : faction.Simulation.soldierCharacter;
        return Create(
            faction,
            FactionNpcRole.Archer,
            visual,
            surfaceAxis);
    }

    public static FactionNpc CreateNoble(FactionController faction, Vector3 surfaceAxis)
    {
        if (faction == null || faction.Simulation == null)
            return null;

        PlayableCharacterDef visual = faction.Simulation.nobleCharacter != null
            ? faction.Simulation.nobleCharacter
            : faction.Simulation.workerCharacter;
        return Create(
            faction,
            FactionNpcRole.Noble,
            visual,
            surfaceAxis);
    }

    /// <summary>Legacy alias — trains infantry.</summary>
    public static FactionNpc CreateSoldier(FactionController faction, Vector3 surfaceAxis)
    {
        return CreateInfantry(faction, surfaceAxis);
    }

    public static FactionNpc CreateMerchant(FactionController faction, Vector3 surfaceAxis)
    {
        if (faction == null || faction.Simulation == null)
            return null;

        return Create(
            faction,
            FactionNpcRole.Merchant,
            faction.Simulation.workerCharacter,
            surfaceAxis);
    }

    static int MaxHealthForRole(FactionController faction, FactionNpcRole role)
    {
        FactionCombatSettings combat = faction.Combat;
        switch (role)
        {
            case FactionNpcRole.Infantry:
                return combat.soldierMaxHealth;
            case FactionNpcRole.Archer:
                return combat.archerMaxHealth;
            case FactionNpcRole.Noble:
                return combat.nobleMaxHealth;
            default:
                return Mathf.Max(1, combat.soldierMaxHealth / 2);
        }
    }

    static FactionNpc Create(
        FactionController faction,
        FactionNpcRole role,
        PlayableCharacterDef character,
        Vector3 surfaceAxis)
    {
        string label = role == FactionNpcRole.ResourceGatherer
            ? "Worker"
            : role.ToString();
        var go = new GameObject($"{faction.DisplayName}_{label}");
        go.transform.SetParent(faction.transform, false);
        var npc = go.AddComponent<FactionNpc>();
        npc.Faction = faction;
        npc.Role = role;

        var cap = go.GetComponent<CapsuleCollider>();
        cap.height = 1.8f;
        cap.radius = 0.42f;
        cap.center = new Vector3(0f, 0.9f, 0f);
        cap.direction = 1;
        cap.isTrigger = false;

        var rb = go.GetComponent<Rigidbody>();
        rb.useGravity = false;
        rb.isKinematic = true;
        rb.detectCollisions = true;
        rb.constraints = RigidbodyConstraints.FreezeRotation;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

        var modelRoot = new GameObject("CharacterModel").transform;
        modelRoot.SetParent(go.transform, false);
        if (character != null && character.characterPrefab != null)
        {
            GameObject visual = Object.Instantiate(character.characterPrefab, modelRoot);
            visual.name = character.characterPrefab.name;
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one;
        }

        go.AddComponent<CharacterAlign>();
        npc.Motor = go.AddComponent<FactionNpcMotor>();
        npc.Health = go.AddComponent<FactionHealth>();
        npc.Health.Configure(MaxHealthForRole(faction, role));

        if (role == FactionNpcRole.ResourceGatherer)
        {
            npc.Worker = go.AddComponent<WorkerAI>();
            go.AddComponent<WorkerInventory>();
        }
        else if (role == FactionNpcRole.Merchant)
        {
            npc.Merchant = go.AddComponent<MerchantAI>();
            go.AddComponent<WorkerInventory>();
        }
        else if (role == FactionNpcRole.Noble)
        {
            npc.Noble = go.AddComponent<NobleAI>();
        }
        else
        {
            npc.Soldier = go.AddComponent<SoldierAI>();
        }

        CharacterAlign alignment = go.GetComponent<CharacterAlign>();
        if (alignment != null)
            alignment.Align();
        var marker = go.AddComponent<FactionVisualMarker>();
        marker.Apply(faction.UiColor);
        npc.Motor.PlaceOnSurface(surfaceAxis);
        return npc;
    }

    void Start()
    {
        if (Faction == null)
            return;
        FactionRegistry.RegisterNpc(this);
        Faction.RegisterNpc(this);
    }

    public void SimulationTick(float deltaTime)
    {
        if (IsDead)
            return;
    }

    public void TakeFactionDamage(int amount, Transform attacker)
    {
        if (Health != null)
            Health.TakeFactionDamage(amount, attacker);
        if (FactionNpcRoles.IsCombatSoldier(Role) && Soldier != null && !IsDead)
            Soldier.NotifyUnderFire(attacker);
    }

    public void Die()
    {
        if (Health != null)
            Health.Die();
    }

    void OnDestroy()
    {
        FactionRegistry.UnregisterNpc(this);
        if (Faction != null)
            Faction.UnregisterNpc(this);
    }
}

static class FactionNpcLod
{
    public const float Distance = 180f;
    public const float AiInterval = 0.4f;
    const float DistanceSq = Distance * Distance;
    static int s_Frame = -1;
    static Vector3 s_CamPos;
    static bool s_HasCam;

    public static float PhaseOffset(int seed, float interval)
    {
        unchecked
        {
            uint hash = (uint)seed * 747796405u + 2891336453u;
            hash ^= hash >> 16;
            float u = (hash & 2047u) * (1f / 2048f);
            return u * Mathf.Max(0.01f, interval);
        }
    }

    public static int PhaseSeed(UnityEngine.Object obj)
    {
        return obj == null ? 0 : (int)EntityId.ToULong(obj.GetEntityId());
    }

    public static bool IsFar(Vector3 worldPosition)
    {
        EnsureCamera();
        if (!s_HasCam)
            return false;
        return (worldPosition - s_CamPos).sqrMagnitude > DistanceSq;
    }

    static void EnsureCamera()
    {
        int frame = Time.frameCount;
        if (s_Frame == frame)
            return;
        s_Frame = frame;
        Camera cam = Camera.main;
        s_HasCam = cam != null;
        if (s_HasCam)
            s_CamPos = cam.transform.position;
    }
}

/// <summary>Planet tangent movement shared by faction workers and soldiers.</summary>
[RequireComponent(typeof(Rigidbody))]
public sealed class FactionNpcMotor : MonoBehaviour
{
    [Min(0.1f)] public float surfaceStickDistance = 0.55f;
    [Min(1f)] public float surfaceStickForce = 40f;
    [Min(0.05f)] public float steeringResponsiveness = 8f;
    [Min(0.1f)] public float swimSpeed = 5f;
    [Min(0.2f)] public float swimFloatDepth = 0.9f;
    [Min(0.1f)] public float swimEnterDepth = 0.6f;
    [Min(0.05f)] public float swimExitDepth = 0.45f;

    Rigidbody _body;
    FactionNpc _npc;
    CapsuleCollider _capsule;
    Planet _planet;
    PlanetOceanLayer _ocean;
    Vector3 _target;
    float _speed;
    float _stopDistance = 1f;
    bool _hasTarget;
    bool _stopped;
    bool _isSwimming;
    float _lastPlanarSpeed;
    Animator _animator;
    ZombieLocomotionAnimator _locomotion;

    public bool HasTarget => _hasTarget;
    public bool IsStopped => _stopped;
    public bool IsSwimming => _isSwimming;

    void Awake()
    {
        _body = GetComponent<Rigidbody>();
        _npc = GetComponent<FactionNpc>();
        _capsule = GetComponent<CapsuleCollider>();
        _planet = FindFirstObjectByType<Planet>();
        _ocean = _planet != null ? _planet.GetComponent<PlanetOceanLayer>() : null;
        _animator = GetComponentInChildren<Animator>(true);
        _locomotion = gameObject.AddComponent<ZombieLocomotionAnimator>();
        _locomotion.animator = _animator;
    }

    public void PlaceOnSurface(Vector3 axis)
    {
        if (_planet == null)
            _planet = FindFirstObjectByType<Planet>();
        if (_planet == null)
            return;
        if (_ocean == null)
            _ocean = _planet.GetComponent<PlanetOceanLayer>();

        axis = axis.sqrMagnitude > 1e-6f ? axis.normalized : Vector3.up;
        Vector3 center = _planet.transform.position;
        MeshCollider collider = ZombieAI.ResolvePrimaryTerrainMeshCollider(_planet.transform);
        Vector3 position = PlanetSurfaceSampler.GetDrySurfacePosition(
            axis,
            center,
            collider,
            _planet,
            _ocean,
            ~0,
            64,
            0.1f,
            _planet.GetBaseRadiusWorld(),
            1.25f);
        if (_ocean != null && _ocean.GetDepthBelowSurface(position) > 0f)
        {
            // PlanetSurfaceSampler has a last-resort shell fallback for all-water
            // planets. This world has dry terrain, so make the NPC retry analytically
            // rather than accepting that fallback below the ocean.
            for (int i = 0; i < 128; i++)
            {
                Vector3 candidateAxis = i == 0 ? axis : Random.onUnitSphere;
                float surfaceRadius = _planet.GetSurfaceRadiusWorld(candidateAxis);
                if (surfaceRadius < _ocean.ResolveOceanRadiusWorld() + 1.25f)
                    continue;
                position = center + candidateAxis.normalized * (surfaceRadius + 0.1f);
                break;
            }
        }
        transform.position = position;
        Vector3 surfaceUp = (position - center).normalized;
        transform.rotation = Quaternion.FromToRotation(Vector3.up, surfaceUp);
        _body.position = position;
    }

    public void SetDestination(Vector3 target, float speed, float stopDistance)
    {
        _target = target;
        _speed = Mathf.Max(0.1f, speed);
        _stopDistance = Mathf.Max(0.2f, stopDistance);
        _hasTarget = true;
        _stopped = false;
    }

    public void Stop()
    {
        _hasTarget = false;
        _stopped = true;
        _lastPlanarSpeed = 0f;
        if (_body != null)
            _body.linearVelocity = Vector3.zero;
    }

    void FixedUpdate()
    {
        if (_body == null)
            return;
        if (_planet == null)
        {
            _planet = FindFirstObjectByType<Planet>();
            if (_planet != null)
                _ocean = _planet.GetComponent<PlanetOceanLayer>();
        }
        if (_planet == null)
            return;

        bool far = FactionNpcLod.IsFar(transform.position);
        Vector3 center = _planet.transform.position;
        Vector3 up = (transform.position - center).normalized;
        if (up.sqrMagnitude < 1e-6f)
            up = Vector3.up;

        float currentRadius = Vector3.Distance(transform.position, center);
        float terrainRadius = _planet.GetSurfaceRadiusWorld(up);
        if (terrainRadius > 0f && currentRadius < terrainRadius - 0.5f)
        {
            Vector3 corrected = center + up * (terrainRadius + 0.1f);
            transform.position = corrected;
            _body.position = corrected;
        }

        if (!_hasTarget)
        {
            if (_isSwimming || (_ocean != null && _ocean.GetDepthBelowSurface(transform.position) > swimEnterDepth))
            {
                Vector3 dryDirection = FindDryTangent(up);
                if (dryDirection.sqrMagnitude > 1e-6f)
                    AdvanceOnSurface(dryDirection, SwimSpeed(), up, !far);
                else
                {
                    SnapToTravelSurface(transform.position);
                    _lastPlanarSpeed = 0f;
                }
            }
            else
            {
                _lastPlanarSpeed = 0f;
                if (!far)
                    SeparateFromCrowding(up);
            }
            if (!far)
                UpdateAnimation(up);
            return;
        }

        Vector3 toTarget = _target - transform.position;
        Vector3 tangent = Vector3.ProjectOnPlane(toTarget, up);
        float distance = tangent.magnitude;
        if (distance <= _stopDistance)
        {
            _stopped = true;
            _lastPlanarSpeed = 0f;
            if (!far)
                SeparateFromCrowding(up);
            if (!far)
                UpdateAnimation(up);
            return;
        }

        _stopped = false;
        Vector3 direction = tangent / Mathf.Max(0.001f, distance);
        bool gatherer = _npc != null && _npc.Role == FactionNpcRole.ResourceGatherer;
        if (!gatherer && WouldEnterWater(direction, up) && TrySteerOnDryLand(direction, up, out Vector3 dry))
            direction = dry;
        float moveSpeed = _isSwimming || WouldEnterWater(direction, up) ? SwimSpeed() : _speed;
        AdvanceOnSurface(direction, moveSpeed, up, !far);

        Vector3 facing = Vector3.ProjectOnPlane(direction, up);
        if (facing.sqrMagnitude > 1e-5f)
            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                Quaternion.LookRotation(facing.normalized, up),
                Mathf.Clamp01(Time.fixedDeltaTime * steeringResponsiveness));
        if (!far)
            UpdateAnimation(up);
    }

    void AdvanceOnSurface(Vector3 direction, float speed, Vector3 up, bool avoidCrowding)
    {
        if (_planet == null || direction.sqrMagnitude < 1e-8f)
            return;

        float radius = Mathf.Max(1f, Vector3.Distance(transform.position, _planet.transform.position));
        float angularStep = Mathf.Clamp(speed * Time.fixedDeltaTime / radius, 0f, 0.1f);
        Vector3 nextAxis = (up * Mathf.Cos(angularStep) +
                            direction.normalized * Mathf.Sin(angularStep)).normalized;
        Vector3 next = TravelPoint(nextAxis);
        SnapToTravelSurface(avoidCrowding ? SteerAroundCrowding(next, up) : next);
        _body.linearVelocity = Vector3.zero;
        _lastPlanarSpeed = speed;
    }

    float SwimSpeed()
    {
        float configured = _npc != null && _npc.Faction != null
            ? _npc.Faction.Combat.npcSwimSpeed
            : swimSpeed;
        return Mathf.Max(0.1f, configured);
    }

    Vector3 TravelPoint(Vector3 axis)
    {
        Vector3 center = _planet.transform.position;
        if (axis.sqrMagnitude < 1e-6f)
            axis = Vector3.up;
        else
            axis = axis.normalized;
        float radius = TravelRadius(axis);
        return center + axis * radius;
    }

    float TravelRadius(Vector3 axis)
    {
        float terrain = _planet.GetSurfaceRadiusWorld(axis) + 0.1f;
        if (_ocean == null)
        {
            _isSwimming = false;
            return terrain;
        }

        Vector3 sample = _planet.transform.position + axis * terrain;
        float wave = _ocean.GetWaveHeightAtPosition(sample, Time.time);
        float oceanRadius = _ocean.ResolveOceanRadiusWorld() + wave;
        float landDepth = oceanRadius - terrain;
        if (_isSwimming)
            _isSwimming = landDepth > swimExitDepth;
        else
            _isSwimming = landDepth > swimEnterDepth;

        if (_isSwimming)
            return Mathf.Max(terrain, oceanRadius - swimFloatDepth);
        return terrain;
    }

    void SnapToTravelSurface(Vector3 candidate)
    {
        if (_planet == null)
        {
            transform.position = candidate;
            if (_body != null)
                _body.position = candidate;
            return;
        }

        Vector3 center = _planet.transform.position;
        Vector3 axis = (candidate - center).normalized;
        Vector3 next = TravelPoint(axis);
        transform.position = next;
        if (_body != null)
            _body.position = next;
    }

    float SeparationRadius()
    {
        float capsule = _capsule != null ? _capsule.radius : 0.42f;
        float configured = _npc != null && _npc.Faction != null
            ? _npc.Faction.Combat.npcSeparationRadius
            : 0.85f;
        return Mathf.Max(capsule + 0.15f, configured);
    }

    Vector3 SteerAroundCrowding(Vector3 desired, Vector3 up)
    {
        Vector3 from = transform.position;
        Vector3 move = Vector3.ProjectOnPlane(desired - from, up);
        Vector3 push = CrowdingPush(desired, up);
        if (push.sqrMagnitude < 1e-6f)
            return desired;

        Vector3 planarPush = Vector3.ProjectOnPlane(push, up);
        if (move.sqrMagnitude > 1e-6f && Vector3.Dot(move, planarPush) < 0f)
        {
            Vector3 slide = Vector3.ProjectOnPlane(move + planarPush, up);
            if (slide.sqrMagnitude < 1e-6f)
                slide = Vector3.Cross(up, move).normalized * planarPush.magnitude;
            return from + slide;
        }

        return desired + planarPush;
    }

    void SeparateFromCrowding(Vector3 up)
    {
        Vector3 push = CrowdingPush(transform.position, up);
        if (push.sqrMagnitude < 1e-8f)
            return;
        SnapToTravelSurface(transform.position + Vector3.ProjectOnPlane(push, up));
    }

    Vector3 CrowdingPush(Vector3 position, Vector3 up)
    {
        float radius = SeparationRadius();
        float diameter = radius * 2f;
        float diameterSq = diameter * diameter;
        Vector3 push = Vector3.zero;
        FactionCrowdGrid.AddSeparation(_npc, position, up, diameter, diameterSq, ref push);
        return Vector3.ClampMagnitude(push, radius);
    }

    void SnapToSurface(Vector3 candidate)
    {
        SnapToTravelSurface(candidate);
    }

    bool WouldEnterWater(Vector3 tangent, Vector3 up)
    {
        if (_ocean == null || _planet == null)
            return false;
        Vector3 radial = (transform.position - _planet.transform.position).normalized;
        if (radial.sqrMagnitude < 1e-6f)
            return false;
        float radius = Mathf.Max(1f, Vector3.Distance(transform.position, _planet.transform.position));
        Vector3 sample = (radial + tangent.normalized * (2.5f / radius)).normalized;
        return _planet.GetSurfaceRadiusWorld(sample) <
               _ocean.ResolveOceanRadiusWorld() + 0.75f;
    }

    bool TrySteerOnDryLand(Vector3 desired, Vector3 up, out Vector3 steered)
    {
        steered = desired;
        if (desired.sqrMagnitude < 1e-8f)
            return false;

        Vector3 goal = desired.normalized;
        Vector3 best = Vector3.zero;
        float bestDot = -2f;
        const int steps = 10;
        for (int i = 1; i <= steps; i++)
        {
            float angle = i * 18f;
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 candidate = Vector3.ProjectOnPlane(
                    Quaternion.AngleAxis(angle * side, up) * goal, up);
                if (candidate.sqrMagnitude < 1e-6f)
                    continue;
                candidate.Normalize();
                if (WouldEnterWater(candidate, up))
                    continue;
                float dot = Vector3.Dot(candidate, goal);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    best = candidate;
                }
            }
        }

        if (best.sqrMagnitude < 1e-6f)
            return false;
        steered = best;
        return true;
    }

    Vector3 FindDryTangent(Vector3 up)
    {
        Vector3 best = Vector3.zero;
        float bestRadius = float.NegativeInfinity;
        Vector3 reference = Mathf.Abs(Vector3.Dot(up, Vector3.up)) > 0.9f
            ? Vector3.right
            : Vector3.up;
        Vector3 tangent = Vector3.Cross(up, reference).normalized;
        Vector3 other = Vector3.Cross(up, tangent).normalized;
        Vector3 radial = (transform.position - _planet.transform.position).normalized;
        float currentRadius = Mathf.Max(1f, Vector3.Distance(
            transform.position, _planet.transform.position));
        for (int pass = 0; pass < 3; pass++)
        {
            float probeDistance = pass == 0 ? 3f : (pass == 1 ? 30f : 100f);
            for (int i = 0; i < 8; i++)
            {
                float angle = (i / 8f) * Mathf.PI * 2f;
                Vector3 candidate = (tangent * Mathf.Cos(angle) + other * Mathf.Sin(angle)).normalized;
                Vector3 sample = (radial + candidate * (probeDistance / currentRadius)).normalized;
                float surface = _planet.GetSurfaceRadiusWorld(sample);
                if (surface > bestRadius &&
                    (_ocean == null || surface >= _ocean.ResolveOceanRadiusWorld() + 0.75f))
                {
                    bestRadius = surface;
                    best = candidate;
                }
            }
            if (best.sqrMagnitude > 1e-6f)
                break;
        }
        return best;
    }

    void UpdateAnimation(Vector3 up)
    {
        if (_locomotion == null || _body == null)
            return;
        _locomotion.SetPlanarSpeed(_lastPlanarSpeed, Mathf.Max(0.5f, _speed), true);
    }
}

/// <summary>Faction-local health contract used by zombies and opposing soldiers.</summary>
public sealed class FactionHealth : MonoBehaviour
{
    public int MaxHealth { get; private set; }
    public int CurrentHealth { get; private set; }
    public bool IsDead { get; private set; }

    FactionNpc _npc;

    public void Configure(int maxHealth)
    {
        MaxHealth = Mathf.Max(1, maxHealth);
        CurrentHealth = MaxHealth;
        IsDead = false;
    }

    void Awake()
    {
        _npc = GetComponent<FactionNpc>();
    }

    public void TakeFactionDamage(int amount, Transform attacker)
    {
        if (IsDead || amount <= 0)
            return;
        CurrentHealth = Mathf.Max(0, CurrentHealth - amount);
        if (_npc != null && _npc.Faction != null)
            FactionTradeSystem.NotifyHostileDamage(_npc.Faction, attacker);
        if (CurrentHealth == 0)
            Die();
    }

    public void Die()
    {
        if (IsDead)
            return;
        IsDead = true;
        if (_npc != null && _npc.Motor != null)
            _npc.Motor.Stop();
        if (_npc != null && _npc.Faction != null)
            _npc.Faction.UnregisterNpc(_npc);
        gameObject.SetActive(false);
    }
}

/// <summary>Applies a faction tint without creating or modifying source materials.</summary>
public sealed class FactionVisualMarker : MonoBehaviour
{
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");

    public Color factionColor = Color.white;

    public void Apply(Color color)
    {
        factionColor = color;
        Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
        var block = new MaterialPropertyBlock();
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null)
                continue;
            renderer.GetPropertyBlock(block);
            block.SetColor(BaseColorId, color);
            block.SetColor(ColorId, color);
            renderer.SetPropertyBlock(block);
        }
    }
}
