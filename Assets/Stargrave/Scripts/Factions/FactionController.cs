using System.Collections.Generic;
using UnityEngine;

/// <summary>Runtime state and economy for one autonomous faction.</summary>
[DisallowMultipleComponent]
public sealed class FactionController : MonoBehaviour
{
    FactionSimulation _simulation;
    FactionDefinition _definition;
    string _fallbackName;
    readonly List<FactionNpc> _members = new List<FactionNpc>(64);
    readonly List<FactionNpc> _workers = new List<FactionNpc>(16);
    readonly List<FactionNpc> _soldiers = new List<FactionNpc>(48);
    readonly List<FactionNpc> _merchants = new List<FactionNpc>(8);
    readonly List<FactionNpc> _nobles = new List<FactionNpc>(4);
    readonly List<Vector3> _streamFoci = new List<Vector3>(8);
    readonly List<float> _streamFocusUsed = new List<float>(8);
    readonly List<bool> _streamFocusHarvest = new List<bool>(8);
    readonly List<FactionResourceType> _streamFocusType = new List<FactionResourceType>(8);
    readonly List<Vector3> _woodSites = new List<Vector3>(8);
    readonly List<float> _woodSiteUsed = new List<float>(8);
    readonly List<Vector3> _stoneSites = new List<Vector3>(8);
    readonly List<float> _stoneSiteUsed = new List<float>(8);
    const int MaxStreamFoci = 6;
    const int MaxResourceSites = 8;
    const int MaxPerceivedThreats = 8;
    const float StreamFocusMergeDistance = 45f;
    const float ResourceSiteMergeDistance = 45f;
    FactionStateMachine _stateMachine;
    readonly FactionNpc[] _enemyThreats = new FactionNpc[MaxPerceivedThreats];
    readonly float[] _enemyThreatDist = new float[MaxPerceivedThreats];
    int _enemyThreatCount;
    readonly ZombieAI[] _zombieThreats = new ZombieAI[MaxPerceivedThreats];
    readonly float[] _zombieThreatDist = new float[MaxPerceivedThreats];
    int _zombieThreatCount;
    ResourceNode _perceivedWood;
    ResourceNode _perceivedStone;
    float _nextPerception;
    bool _perceptionPhased;
    int _resourceScanIndex;
    ResourceNode _scanWood;
    ResourceNode _scanStone;
    float _scanWoodSq;
    float _scanStoneSq;
    readonly Vector3[] _senseAnchors = new Vector3[8];
    int _senseAnchorCount;
    readonly Vector3[] _gatherAnchors = new Vector3[12];
    int _gatherAnchorCount;

    public int RuntimeIndex { get; private set; }
    public string RuntimeId { get; private set; }
    public FactionPersonality Personality { get; private set; }
    public string DisplayName => !string.IsNullOrWhiteSpace(Personality.flavorName)
        ? Personality.flavorName
        : (!string.IsNullOrWhiteSpace(_fallbackName)
            ? _fallbackName
            : (_definition != null && !string.IsNullOrWhiteSpace(_definition.displayName)
                ? _definition.displayName
                : $"Faction {RuntimeIndex + 1}"));
    public Color UiColor => Personality.color.a > 0.01f ? Personality.color : Color.white;
    public FactionSimulation Simulation => _simulation;
    public Vector3 SpawnAxis { get; private set; }
    public Transform BaseOrigin { get; private set; }
    public bool HasFoundedCampus { get; private set; }
    public float CampusFoundedAt { get; private set; }
    /// <summary>Absolute Time.time when forced founding may begin (per-faction stagger).</summary>
    float _forceFoundDeadline;
    float _nextForceFoundAttempt;
    bool _wipeReviveArmed;
    float _wipeReviveAt;
    float _barProtectUntil;
    float _workerBuildTimer;
    float _foundingStruggleStartedAt;
    float _baseBuildStruggleStartedAt;
    public FactionState State { get; private set; } = FactionState.Economy;
    public TownHall TownHall { get; private set; }
    public Barracks Barracks { get; private set; }
    public Market Market { get; private set; }
    public Mint Mint { get; private set; }
    public Factory Factory { get; private set; }
    public BuildingConstructionSite ActiveConstruction { get; private set; }
    readonly System.Collections.Generic.List<Mex> _mexes = new System.Collections.Generic.List<Mex>(8);
    readonly System.Collections.Generic.List<EnergyGen> _energyGens = new System.Collections.Generic.List<EnergyGen>(8);
    public int MexCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _mexes.Count; i++)
                if (_mexes[i] != null && _mexes[i].IsOperational)
                    n++;
            return n;
        }
    }
    public int EnergyGenCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _energyGens.Count; i++)
                if (_energyGens[i] != null && _energyGens[i].IsOperational)
                    n++;
            return n;
        }
    }
    /// <summary>BAR metal pool (backed by wood).</summary>
    public int Metal => Wood;
    /// <summary>BAR energy pool (backed by stone).</summary>
    public int Energy => Stone;
    Vector3 _townHallAxis;
    Vector3 _barracksAxis;
    Vector3 _marketAxis;
    Vector3 _mintAxis;
    bool _townHallBuiltOnce;
    bool _barracksBuiltOnce;
    bool _marketBuiltOnce;
    bool _mintBuiltOnce;
    public int Wood { get; private set; }
    public int Stone { get; private set; }
    public int Gold { get; private set; }
    public int WoodGathered { get; private set; }
    public int StoneGathered { get; private set; }
    public int ReservedWood { get; private set; }
    public int ReservedStone { get; private set; }
    float _barMetalCarry;
    float _barEnergyCarry;
    public int WorkerCount => Stargrave.Rts2.Rts2UnitSim.HasInstance
        ? Stargrave.Rts2.Rts2UnitSim.Instance.CountRole(RuntimeIndex, RtsUnitRole.Worker)
        : CountLiving(_workers);
    public IReadOnlyList<FactionNpc> Workers => _workers;
    public int SoldierCount => Stargrave.Rts2.Rts2UnitSim.HasInstance
        ? Stargrave.Rts2.Rts2UnitSim.Instance.CountCombat(RuntimeIndex)
        : CountLiving(_soldiers);
    public int LivingUnitCount => Stargrave.Rts2.Rts2UnitSim.HasInstance
        ? Stargrave.Rts2.Rts2UnitSim.Instance.CountLiving(RuntimeIndex)
        : WorkerCount + SoldierCount + MerchantCount + NobleCount;
    public int InfantryCount => Stargrave.Rts2.Rts2UnitSim.HasInstance
        ? Stargrave.Rts2.Rts2UnitSim.Instance.CountRole(RuntimeIndex, RtsUnitRole.Infantry)
        : CountLivingRole(FactionNpcRole.Infantry);
    public int ArcherCount => Stargrave.Rts2.Rts2UnitSim.HasInstance
        ? Stargrave.Rts2.Rts2UnitSim.Instance.CountRole(RuntimeIndex, RtsUnitRole.Archer)
        : CountLivingRole(FactionNpcRole.Archer);
    public int MerchantCount => Stargrave.Rts2.Rts2UnitSim.HasInstance
        ? Stargrave.Rts2.Rts2UnitSim.Instance.CountRole(RuntimeIndex, RtsUnitRole.Merchant)
        : CountLiving(_merchants);
    public int NobleCount => Stargrave.Rts2.Rts2UnitSim.HasInstance
        ? Stargrave.Rts2.Rts2UnitSim.Instance.CountRole(RuntimeIndex, RtsUnitRole.Noble)
        : CountLiving(_nobles);
    public IReadOnlyList<FactionNpc> Members => _members;
    public ClaimableTown ClaimTargetTown { get; private set; }
    public bool WantsClaimMission { get; private set; }
    public int AvailableWood => Mathf.Max(0, Wood - ReservedWood);
    public int AvailableStone => Mathf.Max(0, Stone - ReservedStone);
    public float Strength => CalculateStrength().total;
    public float GrowthModifier => _simulation != null ? _simulation.GetGrowthModifier(this) : 1f;
    public FactionController CurrentRival { get; private set; }
    /// <summary>True while this faction treats the player as an enemy (shot/killed their units).</summary>
    public bool HostileToPlayer => Time.time < _hostileToPlayerUntil;
    public Transform PlayerAggressor { get; private set; }
    public bool HasReachedEngagementStrength { get; private set; }
    public float PreviousAttackStrength { get; private set; }
    public int PreviousAttackSoldiers { get; private set; }
    public Vector3 DefensePoint { get; private set; }
    public bool HasDefensePing { get; private set; }
    public Vector3 BackupPoint { get; private set; }
    public bool HasBackupPing { get; private set; }
    public int BackupQuota { get; private set; }
    float _defenseUntil;
    float _backupUntil;
    float _nextAttackAllowed;
    float _nextAssaultOrderAt;
    int _assaultStage;
    bool _assaultStageArrived;
    float _assaultStageHoldUntil;
    Vector3 _hostileContact;
    float _hostileContactTime;
    float _hostileToPlayerUntil;
    int _plannedMuster;
    float _musterStartedAt;
    int _failedWaves;
    int _approachWave;

    public FactionEconomySettings Economy => _simulation.economy;
    public FactionCombatSettings Combat => _simulation.combat;
    public FactionWarfareSettings Warfare => _simulation.warfare;

    public void Initialize(
        FactionSimulation simulation,
        int runtimeIndex,
        string runtimeId,
        FactionDefinition definition,
        Vector3 spawnAxis,
        string fallbackName = null)
    {
        _simulation = simulation;
        RuntimeIndex = runtimeIndex;
        RuntimeId = runtimeId;
        _definition = definition;
        Personality = FactionPersonality.CreateForIndex(runtimeIndex, definition);
        if (definition != null && definition.usePreferredPersonality)
        {
            var p = Personality;
            p.buildBias = definition.preferredBuildBias;
            p.aggression = definition.preferredAggression;
            Personality = p;
        }
        _fallbackName = !string.IsNullOrWhiteSpace(Personality.flavorName)
            ? Personality.flavorName
            : (string.IsNullOrWhiteSpace(fallbackName)
                ? $"Faction {runtimeIndex + 1}"
                : fallbackName);
        SpawnAxis = spawnAxis.sqrMagnitude > 1e-6f ? spawnAxis.normalized : Vector3.up;
        Wood = Mathf.Max(0, Economy.startingWood);
        Stone = Mathf.Max(0, Economy.startingStone);
        Gold = Mathf.Max(0, Economy.startingGold);
        if (_simulation != null && _simulation.useBarEconomyMode)
        {
            Wood = Mathf.Max(Wood, Economy.startingMetal);
            Stone = Mathf.Max(Stone, Economy.startingEnergy);
        }
        name = DisplayName;
        _stateMachine = gameObject.AddComponent<FactionStateMachine>();
        _stateMachine.Initialize(this);
        // Campus is founded later when roaming workers meet or the player interacts.
        HasFoundedCampus = false;
        CampusFoundedAt = 0f;
        float minForce;
        float maxForce;
        if (_simulation != null && _simulation.useBarEconomyMode && Economy != null)
        {
            minForce = Mathf.Max(15f, Economy.barFoundingForceAfterSecondsMin);
            maxForce = Mathf.Max(minForce, Economy.barFoundingForceAfterSecondsMax);
        }
        else
        {
            minForce = Economy != null ? Mathf.Max(30f, Economy.foundingForceAfterSecondsMin) : 180f;
            maxForce = Economy != null ? Mathf.Max(minForce, Economy.foundingForceAfterSecondsMax) : 300f;
        }
        _forceFoundDeadline = Time.time + UnityEngine.Random.Range(minForce, maxForce);
        _wipeReviveArmed = false;
        _foundingStruggleStartedAt = Time.time;
        _baseBuildStruggleStartedAt = 0f;
    }

    void Update()
    {
        if (_simulation == null)
            return;
        RefreshResourcePerceptionSlice();
        float interval = Mathf.Max(0.05f, Economy.combatDecisionInterval);
        if (!_perceptionPhased)
        {
            _perceptionPhased = true;
            _nextPerception = Time.time + FactionNpcLod.PhaseOffset(RuntimeIndex * 997 + 13, interval);
            return;
        }
        if (Time.time < _nextPerception)
            return;
        _nextPerception = Time.time + interval;
        RefreshThreatPerception();
    }

    void EnsureBaseArea(bool forceCreate = false)
    {
        if (BaseOrigin != null || _simulation == null || _simulation.planet == null)
            return;

        Planet planet = _simulation.planet;
        Vector3 axis = SpawnAxis.sqrMagnitude > 1e-6f ? SpawnAxis.normalized : Vector3.up;
        const float dryClearance = 1.25f;
        float waterLine = BuildingPadSiteEvaluator.ResolveWaterLine(planet, dryClearance);
        bool aboveWater = planet.GetSurfaceRadiusWorld(axis) >= waterLine;
        bool designated = forceCreate || (Economy != null && Economy.useDesignatedSiteFounding);

        if (!aboveWater || !BuildingPlacementSystem.IsDryMainlandCampus(planet, axis))
        {
            // Prefer a dry snap near the assigned pad — never wander to another continent.
            if (TryFindDryAxisNear(axis, designated ? 28f : 75f, out Vector3 nearDry))
            {
                axis = nearDry;
                SpawnAxis = axis;
            }
            else if (!designated)
            {
                if (!_simulation.TryFindMainlandSpawnAxis(axis, out Vector3 relocated))
                {
                    Debug.LogError($"[FactionSimulation] {DisplayName} could not place a mainland dry base.", this);
                    return;
                }

                Vector3 relocatedWorld = planet.GetSurfacePointWorld(relocated);
                if (!CanFoundCampusAt(relocatedWorld))
                {
                    Debug.LogWarning($"[FactionSimulation] {DisplayName} dry-pad relocate was too close to a rival; keeping original axis.", this);
                }
                else
                {
                    axis = relocated;
                    SpawnAxis = axis;
                }
            }
            // Designated / forceCreate: keep original axis and force the pad even if imperfect.
        }

        // Last-chance dry cone before giving up (non-designated only).
        if (!designated && planet.GetSurfaceRadiusWorld(axis) < waterLine)
            return;

        var root = new GameObject($"{DisplayName}_Base");
        root.transform.SetParent(transform, false);
        root.transform.position = planet.GetSurfacePointWorld(axis);
        root.transform.rotation = Quaternion.FromToRotation(Vector3.up, axis);

        var pad = root.AddComponent<BuildingPad>();
        pad.flatRadius = Mathf.Max(8f, Economy.townHallFlatRadius);
        pad.blendWidth = Economy.townHallBlendWidth;
        pad.dryClearance = dryClearance;
        pad.requireSuitableSite = false;
        pad.skipBakeIfUnsuitable = false;
        pad.suppressFoliage = true;
        pad.ApplyPoseOnAxis(planet, axis);

        BaseOrigin = root.transform;
        PlanetBuildingPads.BakeFromScene(planet);
        PlanetBuildingPads.RegeneratePlanetWithPads();
    }

    bool TryFindDryAxisNear(Vector3 preferred, float maxDegrees, out Vector3 dry)
    {
        dry = default;
        if (_simulation == null || _simulation.planet == null)
            return false;
        preferred = preferred.normalized;
        float waterLine = BuildingPadSiteEvaluator.ResolveWaterLine(_simulation.planet, 1.25f);
        float minDot = Mathf.Cos(Mathf.Clamp(maxDegrees, 5f, 90f) * Mathf.Deg2Rad);
        for (int i = 0; i < 48; i++)
        {
            Vector3 guess = i == 0
                ? preferred
                : (preferred + UnityEngine.Random.onUnitSphere * (maxDegrees / 90f)).normalized;
            if (Vector3.Dot(guess, preferred) < minDot)
                continue;
            if (_simulation.planet.GetSurfaceRadiusWorld(guess) < waterLine)
                continue;
            if (!BuildingPlacementSystem.IsDryMainlandCampus(_simulation.planet, guess, campusRadius: 55f))
                continue;
            dry = guess.normalized;
            return true;
        }
        return false;
    }

    public void SpawnInitialWorkers()
    {
        int spawnCount = Mathf.Min(Economy.startingWorkers, Economy.workerMaxCount);
        float minAngle = Economy.scatterWorkerMinAngleDegrees;
        float minChord = Mathf.Max(5f, Economy.scatterWorkerMinSeparation);
        if (_simulation != null && _simulation.planet != null)
        {
            float r = Mathf.Max(50f, _simulation.planet.GetBaseRadiusWorld());
            // Chord matching the angular rule — keeps globe-scale spacing even if the angle field is low.
            float angularChord = 2f * r * Mathf.Sin(0.5f * minAngle * Mathf.Deg2Rad);
            minChord = Mathf.Max(minChord, angularChord * 0.85f);
        }

        for (int i = 0; i < spawnCount; i++)
        {
            Vector3 axis = SpawnAxis;
            bool placed = false;
            int already = _simulation != null ? _simulation.RoamerScatterCount : 0;
            // Soften slightly as the globe fills, but never collapse to a local clump.
            float fill = Mathf.Clamp01(already / 64f);
            float angleNow = Mathf.Lerp(minAngle, minAngle * 0.65f, fill);
            float chordNow = Mathf.Lerp(minChord, minChord * 0.65f, fill);

            if (_simulation != null)
            {
                // Maximin dry samples — farthest from existing roamers, no ocean→coast snap.
                for (int attempt = 0; attempt < 6 && !placed; attempt++)
                {
                    if (!_simulation.TryFindMaximinRoamerAxis(
                            angleNow, chordNow, out Vector3 dry, candidateBudget: 48))
                        continue;
                    if (!_simulation.IsRoamerScatterAxisFree(dry, angleNow * 0.85f, chordNow * 0.85f) &&
                        attempt < 4)
                        continue;

                    axis = dry;
                    _simulation.RegisterRoamerScatterAxis(dry);
                    placed = true;
                }
            }

            if (!placed)
            {
                // Skip rather than dump another worker on the same coast clump.
                if (_simulation != null && _simulation.verboseEvents)
                    Debug.LogWarning(
                        $"[FactionSimulation] {DisplayName} could not place scattered roamer {i + 1}/{spawnCount}.",
                        this);
                continue;
            }

            if (Stargrave.Rts2.Rts2UnitSim.HasInstance)
                Stargrave.Rts2.Rts2UnitSim.Instance.TrySpawnRoamer(RuntimeIndex, axis, out _);
        }
    }

    public bool FoundCampus(Vector3 worldPos)
    {
        if (HasFoundedCampus || _simulation == null || _simulation.planet == null)
            return false;

        Planet planet = _simulation.planet;
        Vector3 axis = worldPos - planet.transform.position;
        if (axis.sqrMagnitude < 1e-8f)
            axis = SpawnAxis;
        else
            axis.Normalize();

        bool designated =
            Economy != null &&
            Economy.useDesignatedSiteFounding &&
            SpawnAxis.sqrMagnitude > 1e-6f &&
            Vector3.Dot(axis, SpawnAxis.normalized) > 0.92f;

        // Boot-packed pads are allowed even if a rival later founded nearby-ish;
        // only hard-block when almost on top of another campus.
        float minSep = Economy != null
            ? Mathf.Max(20f, Economy.foundingMinSeparationFromRivalBases)
            : 180f;
        if (designated)
        {
            if (NearestRivalCampusDistSq(planet.GetSurfacePointWorld(SpawnAxis)) <
                (minSep * 0.45f) * (minSep * 0.45f))
                return false;
        }
        else if (!CanFoundCampusAt(worldPos))
        {
            return false;
        }

        // Keep the chosen site when it is already a valid mainland pad.
        // Always re-running TryFindMainlandSpawnAxis can hop onto a rival continent.
        Vector3 dry = designated ? SpawnAxis.normalized : axis;
        bool alreadyPad =
            BuildingPlacementSystem.IsDryMainlandCampus(planet, dry) &&
            BuildingPlacementSystem.IsSuitableFactionAnchor(
                planet, dry, Economy.townHallFlatRadius, _simulation.spawnSearchRadius) &&
            BuildingPlacementSystem.IsSwarmFriendlyOpsArea(planet, dry, 80f);
        if (!alreadyPad)
        {
            if (designated)
            {
                dry = SpawnAxis.normalized;
                TrySnapDesignatedAxisToNearbyDry(ref dry);
            }
            else if (!_simulation.TryFindMainlandSpawnAxis(axis, out dry))
            {
                return false;
            }
        }

        Vector3 proposed = planet.GetSurfacePointWorld(dry);
        if (!designated && !CanFoundCampusAt(proposed))
            return false;
        if (designated &&
            NearestRivalCampusDistSq(proposed) < (minSep * 0.45f) * (minSep * 0.45f))
            return false;

        SpawnAxis = dry;
        _townHallAxis = dry;
        EnsureBaseArea(forceCreate: designated);
        if (BaseOrigin == null)
            return false;
        if (!designated && !CanFoundCampusAt(BaseOrigin.position))
        {
            Destroy(BaseOrigin.gameObject);
            BaseOrigin = null;
            return false;
        }

        HasFoundedCampus = true;
        CampusFoundedAt = Time.time;
        _baseBuildStruggleStartedAt = Time.time;
        if (Economy != null && BarFactionDirector.IsEnabled(this))
            ArmBarProtect(Economy.barAssaultGraceSeconds);

        // Guarantee first HQ can start (workers may not have deposited yet).
        if (Economy != null)
        {
            Wood = Mathf.Max(Wood, Economy.townHallCost.wood);
            Stone = Mathf.Max(Stone, Economy.townHallCost.stone);
            if (_simulation != null && _simulation.useBarEconomyMode)
            {
                Wood = Mathf.Max(Wood, Economy.startingMetal);
                Stone = Mathf.Max(Stone, Economy.startingEnergy);
            }
        }

        if (Stargrave.Rts2.Rts2UnitSim.HasInstance)
        {
            Stargrave.Rts2.Rts2UnitSim.Instance.ActivateFactionAfterFounding(RuntimeIndex);
            Stargrave.Rts2.Rts2UnitSim.Instance.SetGatherTask(RuntimeIndex, FactionResourceType.Wood);
        }

        TryStartNextConstruction();

        if (_simulation.verboseEvents)
            Debug.Log($"[FactionSimulation] {DisplayName} founded campus and started gathering.", this);
        return true;
    }

    bool TrySnapDesignatedAxisToNearbyDry(ref Vector3 axis)
    {
        if (_simulation == null || _simulation.planet == null)
            return false;
        Planet planet = _simulation.planet;
        float waterLine = BuildingPadSiteEvaluator.ResolveWaterLine(planet, 1.25f);
        Vector3 best = axis;
        bool found = planet.GetSurfaceRadiusWorld(axis) >= waterLine;
        for (int i = 0; i < 48; i++)
        {
            Vector3 guess = (axis + UnityEngine.Random.onUnitSphere * 0.2f).normalized;
            if (planet.GetSurfaceRadiusWorld(guess) < waterLine)
                continue;
            if (!BuildingPlacementSystem.IsDryMainlandCampus(planet, guess, campusRadius: 40f))
                continue;
            if (Vector3.Dot(guess, SpawnAxis.normalized) < 0.9f)
                continue;
            best = guess;
            found = true;
            break;
        }
        if (found)
            axis = best;
        return found;
    }

    /// <summary>
    /// If still unfounded past a per-faction random deadline (3–5 min by default),
    /// keep searching for a far campus. Never places on top of rivals — retries later if needed.
    /// </summary>
    public void TickForcedFoundingFallback()
    {
        if (HasFoundedCampus || Economy == null || !Economy.enableForcedFoundingFallback)
            return;
        if (Time.time < _forceFoundDeadline)
            return;
        if (Time.time < _nextForceFoundAttempt)
            return;
        // Spread expensive mainland probes; many unfounded factions used to hitch together.
        _nextForceFoundAttempt = Time.time + 5f;
        TryForceFoundCampus();
    }

    void TickDesignatedSiteStall()
    {
        if (HasFoundedCampus || Economy == null || _simulation == null)
            return;
        if (!Economy.useDesignatedSiteFounding)
            return;
        float stall = Mathf.Max(30f, Economy.designatedSiteStallSeconds);
        if (_foundingStruggleStartedAt <= 0f)
            _foundingStruggleStartedAt = Time.time;
        if (Time.time < _foundingStruggleStartedAt + stall)
            return;
        if (TryReassignDesignatedSite())
            _foundingStruggleStartedAt = Time.time;
    }

    void TickBaseBuildStall()
    {
        if (!HasFoundedCampus || Economy == null)
            return;
        if (TownHall != null && TownHall.IsOperational)
        {
            _baseBuildStruggleStartedAt = 0f;
            return;
        }
        if (_baseBuildStruggleStartedAt <= 0f)
            _baseBuildStruggleStartedAt = Time.time;
        float stall = Mathf.Max(30f, Economy.baseBuildStallSeconds);
        if (Time.time < _baseBuildStruggleStartedAt + stall)
            return;

        if (_simulation != null && _simulation.verboseEvents)
            Debug.Log($"[FactionSimulation] {DisplayName} stalled building HQ — relocating campus.", this);
        if (ActiveConstruction != null)
        {
            Destroy(ActiveConstruction.gameObject);
            ActiveConstruction = null;
        }
        BeginRelocateAndRebuild();
        _baseBuildStruggleStartedAt = Time.time;
    }

    bool TryReassignDesignatedSite()
    {
        if (_simulation == null || _simulation.planet == null)
            return false;

        float minSep = Economy != null
            ? Mathf.Max(20f, Economy.foundingMinSeparationFromRivalBases)
            : 180f;
        minSep = Mathf.Max(minSep, _simulation.minimumFactionSeparationFloor);
        minSep = Mathf.Max(minSep, _simulation.minimumFactionSeparation * 0.85f);

        Vector3 best = default;
        float bestDistSq = -1f;
        for (int i = 0; i < 48; i++)
        {
            if (!_simulation.TryFindMainlandSpawnAxis(UnityEngine.Random.onUnitSphere, out Vector3 dry, maxInnerAttempts: 16))
                continue;
            Vector3 world = _simulation.planet.GetSurfacePointWorld(dry);
            float nearestSq = NearestRivalSiteDistSq(world);
            if (nearestSq < minSep * minSep)
                continue;
            if (nearestSq > bestDistSq)
            {
                bestDistSq = nearestSq;
                best = dry;
            }
        }

        if (best.sqrMagnitude < 1e-8f)
            return false;

        SpawnAxis = best.normalized;
        _townHallAxis = SpawnAxis;
        if (BaseOrigin != null)
        {
            Destroy(BaseOrigin.gameObject);
            BaseOrigin = null;
        }

        if (Stargrave.Rts2.Rts2UnitSim.HasInstance)
            Stargrave.Rts2.Rts2UnitSim.Instance.RemarchUnfoundedWorkers(RuntimeIndex, GetSafePosition());

        if (_simulation.verboseEvents)
            Debug.Log($"[FactionSimulation] {DisplayName} reassigned designated site after stall.", this);
        return true;
    }

    float NearestRivalSiteDistSq(Vector3 worldPos)
    {
        float nearest = float.PositiveInfinity;
        IReadOnlyList<FactionController> factions = FactionRegistry.Factions;
        for (int i = 0; i < factions.Count; i++)
        {
            FactionController other = factions[i];
            if (other == null || other == this)
                continue;
            float d = (other.GetSafePosition() - worldPos).sqrMagnitude;
            if (d < nearest)
                nearest = d;
        }
        return float.IsPositiveInfinity(nearest) ? float.MaxValue : nearest;
    }

    /// <summary>Search hard for a legal pad; never ignore rival separation.</summary>
    void TryForceFoundCampus()
    {
        if (HasFoundedCampus || _simulation == null || _simulation.planet == null)
            return;

        // Designated-site mode: found on the pre-assigned pad first (no random continent hop).
        if (Economy != null && Economy.useDesignatedSiteFounding)
        {
            Vector3 site = GetSafePosition();
            if (FoundCampus(site))
            {
                if (_simulation.verboseEvents)
                    Debug.Log($"[FactionSimulation] Force-founded {DisplayName} at designated site.", this);
                return;
            }
        }

        float minSep = Economy != null
            ? Mathf.Max(20f, Economy.foundingMinSeparationFromRivalBases)
            : 180f;
        minSep = Mathf.Max(minSep, _simulation.minimumFactionSeparationFloor);
        // Prefer the same preferred separation used for initial faction scatter when possible.
        minSep = Mathf.Max(minSep, _simulation.minimumFactionSeparation);

        Vector3 best = default;
        float bestDistSq = -1f;
        // Budgeted probes per tick — full 96×inner searches stalled Play Mode.
        const int attempts = 16;
        for (int i = 0; i < attempts; i++)
        {
            if (!_simulation.TryFindMainlandSpawnAxis(UnityEngine.Random.onUnitSphere, out Vector3 dry, maxInnerAttempts: 12))
                continue;
            Vector3 candidate = _simulation.planet.GetSurfacePointWorld(dry);
            float nearestSq = NearestRivalCampusDistSq(candidate);
            if (nearestSq > bestDistSq)
            {
                bestDistSq = nearestSq;
                best = candidate;
            }
            if (nearestSq < minSep * minSep)
                continue;
            if (FoundCampus(candidate))
            {
                if (_simulation.verboseEvents)
                    Debug.Log($"[FactionSimulation] Force-founded {DisplayName} (staggered fallback).", this);
                return;
            }
        }

        if (bestDistSq >= minSep * minSep && FoundCampus(best))
        {
            if (_simulation.verboseEvents)
                Debug.Log($"[FactionSimulation] Force-founded {DisplayName} at farthest legal site.", this);
        }
    }

    float NearestRivalCampusDistSq(Vector3 worldPos)
    {
        float nearest = float.PositiveInfinity;
        IReadOnlyList<FactionController> factions = FactionRegistry.Factions;
        for (int i = 0; i < factions.Count; i++)
        {
            FactionController other = factions[i];
            if (other == null || other == this || !other.HasFoundedCampus)
                continue;
            float d = (other.GetSafePosition() - worldPos).sqrMagnitude;
            if (d < nearest)
                nearest = d;
        }
        return float.IsPositiveInfinity(nearest) ? float.MaxValue : nearest;
    }

    /// <summary>True when founding here would not sit on top of another faction's campus.</summary>
    public bool CanFoundCampusAt(Vector3 worldPos)
    {
        float minSep = Economy != null
            ? Mathf.Max(20f, Economy.foundingMinSeparationFromRivalBases)
            : 180f;
        if (_simulation != null)
            minSep = Mathf.Max(minSep, _simulation.minimumFactionSeparationFloor);
        return CanFoundCampusAt(worldPos, minSep);
    }

    public bool CanFoundCampusAt(Vector3 worldPos, float minSeparation)
    {
        float minSq = Mathf.Max(1f, minSeparation) * Mathf.Max(1f, minSeparation);
        IReadOnlyList<FactionController> factions = FactionRegistry.Factions;
        for (int i = 0; i < factions.Count; i++)
        {
            FactionController other = factions[i];
            if (other == null || other == this || !other.HasFoundedCampus)
                continue;
            if ((other.GetSafePosition() - worldPos).sqrMagnitude < minSq)
                return false;
        }
        return true;
    }

    public void SimulationTick(float deltaTime)
    {
        PruneMembers();
        TickDefenseExpiry();

        if (!HasFoundedCampus)
        {
            TickForcedFoundingFallback();
            TickDesignatedSiteStall();
            return;
        }

        TickSoftImmortality();
        TickBaseBuildStall();

        // 1) Faction strategy
        TickStrategy();

        // 2) Buildings execute
        if (ActiveConstruction == null)
        {
            if (TownHall == null)
                TryStartNextConstruction(); // HQ / town hall rebuild shared path
            else if (BarFactionDirector.IsEnabled(this))
                BarFactionDirector.TryStartNextConstruction(this);
            else
                TryStartNextConstruction();
        }
        FeedConstructionFromStock();
        if (BarFactionDirector.IsEnabled(this))
            BarFactionDirector.TickIncome(this, deltaTime * GrowthModifier);
        if (Mint != null && Mint.IsOperational && !BarFactionDirector.IsEnabled(this))
            Mint.TickConversion(deltaTime * GrowthModifier);
        if (Factory != null && Factory.IsOperational && BarFactionDirector.IsEnabled(this))
            Factory.TickProduction(deltaTime * GrowthModifier);
        else if (Barracks != null && Barracks.IsOperational && !BarFactionDirector.IsEnabled(this))
            Barracks.TickProduction(deltaTime * GrowthModifier);

        if (BarFactionDirector.IsEnabled(this))
        {
            BarFactionDirector.TickWorkerProduction(this, deltaTime * GrowthModifier);
            BarFactionDirector.TickCombatPressure(this);
        }

        // 3) NPC orders
        IssueNpcOrders();
        for (int i = 0; i < _members.Count; i++)
        {
            if (_members[i] != null)
                _members[i].SimulationTick(deltaTime);
        }
    }

    void TickStrategy()
    {
        if (HasReachedEngagementStrength == false &&
            Strength >= Warfare.minimumEngagementStrength)
        {
            HasReachedEngagementStrength = true;
            if (_simulation.verboseEvents)
                Debug.Log($"[FactionSimulation] {DisplayName} reached engagement strength.", this);
        }

        EvaluateClaimStrategy();
        DefendThreatenedTowns();
        RefreshSwarmGatherBias();

        if (State == FactionState.Economy && TownHall != null && !WantsClaimMission)
            State = FactionState.BuildingArmy;

        if ((State == FactionState.Economy || State == FactionState.BuildingArmy || State == FactionState.Claiming) &&
            NeedsRecoup())
            BeginRecovery();

        if (!BarFactionDirector.IsEnabled(this) &&
            State == FactionState.BuildingArmy && Barracks != null)
        {
            if (_plannedMuster <= 0)
                EnsureMusterPlan(true);
            if (FactionRegistry.WarfareEnabled && ShouldLaunchAttack(out FactionController rival))
                BeginAttack(rival);
        }

        if (State == FactionState.Attacking || State == FactionState.Fighting)
        {
            // Break off stomps: once a rival drops below the living-unit floor, pull back.
            if (BarFactionDirector.IsEnabled(this) &&
                (CurrentRival == null || CurrentRival.IsBarAssaultProtected))
            {
                BeginRetreat();
            }
            else
                TickAttackRetarget();
        }

        if ((State == FactionState.Attacking || State == FactionState.Fighting) &&
            PreviousAttackSoldiers > 0 &&
            SoldierCount <= Mathf.FloorToInt(PreviousAttackSoldiers * (1f - Warfare.retreatLossFraction)))
            BeginRetreat();

        if (State == FactionState.Retreating)
        {
            if (SoldierCount == 0)
            {
                BeginRecovery();
            }
            int safe = 0;
            if (Stargrave.Rts2.Rts2UnitSim.HasInstance)
            {
                safe = Stargrave.Rts2.Rts2UnitSim.Instance.CountNear(
                    RuntimeIndex, GetSafePosition(), 12f, true);
            }
            else
            {
                for (int i = 0; i < _soldiers.Count; i++)
                {
                    if (_soldiers[i] != null && _soldiers[i].Soldier != null &&
                        _soldiers[i].Soldier.IsSafeAtBase)
                        safe++;
                }
            }
            if (safe >= Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(1, SoldierCount) * 0.5f)))
                BeginRecovery();
        }

        if (State == FactionState.Recovering && CanEndRecovery())
        {
            ClearRivalIfPaired();
            PreviousAttackStrength = 0f;
            PreviousAttackSoldiers = 0;
            State = FactionState.BuildingArmy;
            EnsureMusterPlan(true);
            // Soft landing: don't get farmed the instant the rebuild completes.
            if (BarFactionDirector.IsEnabled(this) && Economy != null)
                ArmBarProtect(Economy.barPostRecoveryProtectSeconds);
            if (_simulation != null && _simulation.verboseEvents)
                Debug.Log($"[FactionSimulation] {DisplayName} left recovery — rebuild complete.", this);
        }
    }

    void RefreshSwarmGatherBias()
    {
        if (!Stargrave.Rts2.Rts2UnitSim.HasInstance || WorkerCount <= 0)
            return;
        int woodGap = StockGap(FactionResourceType.Wood);
        int stoneGap = StockGap(FactionResourceType.Stone);
        float stoneFraction;
        int totalGap = woodGap + stoneGap;
        if (totalGap > 0)
            stoneFraction = stoneGap / (float)totalGap;
        else
            // Even stocks: keep a minority on stone so rocks stream, majority on wood.
            stoneFraction = 0.35f;
        Stargrave.Rts2.Rts2UnitSim.Instance.SetGatherTaskSplit(RuntimeIndex, stoneFraction);
    }

    void EvaluateClaimStrategy()
    {
        if (State == FactionState.Attacking ||
            State == FactionState.Fighting ||
            State == FactionState.Retreating ||
            State == FactionState.Recovering)
        {
            WantsClaimMission = false;
            return;
        }

        bool bar = BarFactionDirector.IsEnabled(this);
        if (Market == null || !Market.IsOperational)
        {
            WantsClaimMission = false;
            ClaimTargetTown = null;
            return;
        }

        if (!CanOwnMoreTowns())
        {
            WantsClaimMission = false;
            ClaimTargetTown = null;
            if (State == FactionState.Claiming)
                State = FactionState.BuildingArmy;
            return;
        }

        ClaimableTown best = FindBestClaimTarget();
        ClaimTargetTown = best;
        bool canAffordNoble = HasResources(Economy.nobleCost) && Gold >= Economy.nobleGoldCost;
        if (bar && !canAffordNoble && Factory != null && Factory.IsOperational)
            canAffordNoble = HasResources(Economy.nobleCost);
        bool hasNoble = NobleCount > 0;
        int escortNeed = Economy.nobleEscortCount;
        if (bar)
            escortNeed = Mathf.Max(2, escortNeed / 2);
        bool escortReady = SoldierCount >= escortNeed;
        WantsClaimMission = best != null && (hasNoble || canAffordNoble) && escortReady;

        if (WantsClaimMission &&
            State != FactionState.Economy &&
            ActiveConstruction == null)
            State = FactionState.Claiming;
        else if (State == FactionState.Claiming && !WantsClaimMission)
            State = FactionState.BuildingArmy;
    }

    public int CountOwnedTowns()
    {
        int n = 0;
        IReadOnlyList<ClaimableTown> towns = FactionRegistry.Towns;
        for (int i = 0; i < towns.Count; i++)
        {
            if (towns[i] != null && towns[i].Owner == this)
                n++;
        }
        return n;
    }

    public int MaxOwnedTownsAllowed()
    {
        int total = FactionRegistry.Towns.Count;
        if (total <= 0)
            return 0;
        float frac = Economy != null ? Mathf.Clamp(Economy.maxTownOwnershipFraction, 0.1f, 1f) : 0.5f;
        return Mathf.Max(1, Mathf.FloorToInt(total * frac));
    }

    public bool CanOwnMoreTowns() => CountOwnedTowns() < MaxOwnedTownsAllowed();

    ClaimableTown FindBestClaimTarget()
    {
        if (!CanOwnMoreTowns())
            return null;

        ClaimableTown bestNeutral = null;
        ClaimableTown bestRival = null;
        float bestNeutralSq = float.PositiveInfinity;
        float bestRivalSq = float.PositiveInfinity;
        Vector3 home = GetSafePosition();
        float greed = Personality.ReclaimGreed;
        IReadOnlyList<ClaimableTown> towns = FactionRegistry.Towns;
        for (int i = 0; i < towns.Count; i++)
        {
            ClaimableTown town = towns[i];
            if (town == null || !town.CanStartClaim(this))
                continue;
            float d = (town.transform.position - home).sqrMagnitude;
            if (town.IsNeutral)
            {
                if (d < bestNeutralSq)
                {
                    bestNeutralSq = d;
                    bestNeutral = town;
                }
            }
            else if (SoldierCount >= Mathf.Max(2, Warfare.minSoldiersToPropose / 2))
            {
                float score = d / Mathf.Max(0.35f, greed);
                if (score < bestRivalSq)
                {
                    bestRivalSq = score;
                    bestRival = town;
                }
            }
        }
        return bestNeutral != null ? bestNeutral : bestRival;
    }

    void DefendThreatenedTowns()
    {
        IReadOnlyList<ClaimableTown> towns = FactionRegistry.Towns;
        float radius = Combat.soldierDefendRadius > 0f ? Combat.soldierDefendRadius : 80f;
        for (int i = 0; i < towns.Count; i++)
        {
            ClaimableTown town = towns[i];
            if (town == null || town.Owner != this)
                continue;
            FactionRegistry.CountSoldiersNear(
                town.transform.position, radius, this, out _, out int foes);
            if (Stargrave.Rts2.Rts2UnitSim.HasInstance)
                foes += Stargrave.Rts2.Rts2UnitSim.Instance.CountEnemyCombatNear(RuntimeIndex, town.transform.position, radius);
            if (foes <= 0)
                continue;
            RequestDefense(town.transform.position);
            return;
        }
    }

    public bool WantsNobleTraining()
    {
        return WantsClaimMission && NobleCount < Economy.nobleMaxCount;
    }

    public bool TrySpendNobleTraining()
    {
        bool bar = BarFactionDirector.IsEnabled(this);
        if (!HasResources(Economy.nobleCost))
            return false;
        if (!bar && Gold < Economy.nobleGoldCost)
            return false;
        if (!TrySpendAvailableCost(Economy.nobleCost))
            return false;
        if (!bar)
            Gold -= Mathf.Max(0, Economy.nobleGoldCost);
        return true;
    }

    public void NotifyTownClaimed(ClaimableTown town)
    {
        if (town == null)
            return;
        FactionBalanceSystem.AddThreat(this, Warfare.threatOnTownClaim);
        if (ClaimTargetTown == town)
        {
            ClaimTargetTown = null;
            WantsClaimMission = false;
            if (State == FactionState.Claiming)
                State = FactionState.BuildingArmy;
        }
        for (int i = 0; i < _nobles.Count; i++)
        {
            FactionNpc noble = _nobles[i];
            if (noble != null && noble.Noble != null && noble.Noble.TargetTown == town)
                noble.Noble.ClearTown();
        }
    }

    public void NotifyTownLost(ClaimableTown town)
    {
        if (town == null)
            return;
        if (ClaimTargetTown == town)
            ClaimTargetTown = null;
    }

    public bool WantsMerchantTrade()
    {
        if (Market == null || !Market.IsOperational)
            return false;
        if (MerchantCount >= Economy.merchantMaxCount)
            return false;
        if (BarFactionDirector.IsEnabled(this))
            return true;
        return TradeSurplus(FactionResourceType.Wood) > 0 ||
               TradeSurplus(FactionResourceType.Stone) > 0 ||
               TradeNeed(FactionResourceType.Wood) > 0 ||
               TradeNeed(FactionResourceType.Stone) > 0;
    }

    void IssueNpcOrders()
    {
        if (!Stargrave.Rts2.Rts2UnitSim.HasInstance)
            return;

        // Soft survival: don't yank workers home just because the first Town Hall is still building.
        if (State == FactionState.Retreating ||
            (State == FactionState.Recovering && _townHallBuiltOnce && TownHall == null))
        {
            Stargrave.Rts2.Rts2UnitSim.Instance.SetHomeGoal(RuntimeIndex, GetSafePosition());
            return;
        }

        if (State == FactionState.Recovering)
        {
            // First base under construction — keep gather loop alive.
            return;
        }

        if (State == FactionState.Attacking || State == FactionState.Fighting)
        {
            // Throttle assault goal push — soldiers keep AttackUnit between refreshes.
            if (Time.time >= _nextAssaultOrderAt)
            {
                _nextAssaultOrderAt = Time.time + 1.35f;
                FactionController rival = CurrentRival;
                if (rival != null &&
                    !(BarFactionDirector.IsEnabled(this) && rival.IsBarAssaultProtected))
                {
                    TickAssaultFront(rival);
                    Stargrave.Rts2.Rts2UnitSim.Instance.SetAssaultGoal(
                        RuntimeIndex, AssaultFrontGoal, GetSafePosition());
                }
                else if (BarFactionDirector.IsEnabled(this))
                    Stargrave.Rts2.Rts2UnitSim.Instance.SetHomeGoal(RuntimeIndex, GetSafePosition());
            }
        }
        else if (FactionBalanceSystem.HasActiveCoalition &&
                 !FactionBalanceSystem.IsCoalitionTarget(this))
        {
            if (Time.time >= _nextAssaultOrderAt)
            {
                _nextAssaultOrderAt = Time.time + 1.35f;
                FactionController tyrant = FactionBalanceSystem.CoalitionTarget;
                if (tyrant != null &&
                    tyrant.HasFoundedCampus &&
                    !tyrant.IsBarAssaultProtected &&
                    SoldierCount >= MinSoldiersToPropose)
                {
                    TickAssaultFront(tyrant);
                    Stargrave.Rts2.Rts2UnitSim.Instance.SetAssaultGoal(
                        RuntimeIndex, AssaultFrontGoal, GetSafePosition());
                }
            }
        }

        if (ClaimTargetTown != null && NobleCount > 0)
        {
            IReadOnlyList<ClaimableTown> towns = FactionRegistry.Towns;
            int townIndex = -1;
            for (int i = 0; i < towns.Count; i++)
            {
                if (towns[i] == ClaimTargetTown)
                {
                    townIndex = i;
                    break;
                }
            }
            if (townIndex >= 0 && ClaimTargetTown.Owner != this)
                Stargrave.Rts2.Rts2UnitSim.Instance.SetClaimTown(RuntimeIndex, townIndex);
        }

        if (MerchantCount > 0 && WantsMerchantTrade())
        {
            Vector3 tradeDest = ResolveMerchantTradeDestination();
            Stargrave.Rts2.Rts2UnitSim.Instance.SetTradeGoal(RuntimeIndex, tradeDest);
        }
    }

    void TryStartNextConstruction()
    {
        if (!HasFoundedCampus)
            return;
        if (TownHall == null)
        {
            Vector3 hallAxis = ResolveHallAxis();
            BuildingConstructionSite site;
            // First HQ: prefer the designated / base axis even if the pad is imperfect.
            if (!_townHallBuiltOnce &&
                BuildingPlacementSystem.TryCreateConstructionSite(
                    this, BuildingKind.TownHall, _simulation.townHallPrefab,
                    hallAxis, Economy.townHallFlatRadius, Economy.townHallBlendWidth,
                    Economy.townHallCost, Economy.townHallBuildSeconds, Economy.townHallMaxBuilders,
                    out site, 1, 0f, true, true))
            {
                BeginConstruction(site);
                return;
            }
            if (_townHallBuiltOnce &&
                BuildingPlacementSystem.TryCreateConstructionSite(
                    this, BuildingKind.TownHall, _simulation.townHallPrefab,
                    hallAxis, Economy.townHallFlatRadius, Economy.townHallBlendWidth,
                    Economy.townHallCost, Economy.townHallBuildSeconds, Economy.townHallMaxBuilders,
                    out site, 1, 0f, true, true))
            {
                BeginConstruction(site);
                return;
            }
            if (BuildingPlacementSystem.TryCreateConstructionSite(
                    this, BuildingKind.TownHall, _simulation.townHallPrefab,
                    hallAxis, Economy.townHallFlatRadius, Economy.townHallBlendWidth,
                    Economy.townHallCost, Economy.townHallBuildSeconds, Economy.townHallMaxBuilders,
                    out site, 72, Economy.townHallFlatRadius * 4f))
            {
                BeginConstruction(site);
                return;
            }

            if (BuildingPlacementSystem.TryCreateConstructionSite(
                    this, BuildingKind.TownHall, _simulation.townHallPrefab,
                    hallAxis, Economy.townHallFlatRadius, Economy.townHallBlendWidth,
                    Economy.townHallCost, Economy.townHallBuildSeconds, Economy.townHallMaxBuilders,
                    out site, 24, 0f, true))
            {
                BeginConstruction(site);
            }
            else if (_simulation.verboseEvents && Time.frameCount % 180 == 0)
            {
                Debug.LogWarning($"[FactionSimulation] {DisplayName} could not place first Town Hall.", this);
            }
        }
        else if (Barracks == null)
        {
            BuildingConstructionSite site;
            if (_barracksBuiltOnce &&
                BuildingPlacementSystem.TryCreateConstructionSite(
                    this, BuildingKind.Barracks, _simulation.barracksPrefab,
                    _barracksAxis, Economy.barracksFlatRadius, Economy.barracksBlendWidth,
                    Economy.barracksCost, Economy.barracksBuildSeconds, Economy.barracksMaxBuilders,
                    out site, 1, 0f, true, true))
            {
                BeginConstruction(site);
                return;
            }
            if (BuildingPlacementSystem.TryCreateNearTownHall(
                    this, BuildingKind.Barracks, _simulation.barracksPrefab,
                    Economy.barracksMinDistance,
                    Economy.barracksMaxDistance, Economy.barracksFlatRadius,
                    Economy.barracksBlendWidth, Economy.barracksCost,
                    Economy.barracksBuildSeconds, Economy.barracksMaxBuilders,
                    out site))
            {
                BeginConstruction(site);
            }
            else if (_simulation.verboseEvents && Time.frameCount % 120 == 0)
            {
                Debug.LogWarning($"[FactionSimulation] {DisplayName} could not place Barracks beside Town Hall.", this);
            }
        }
        else if (Market == null)
        {
            BuildingConstructionSite site;
            if (_marketBuiltOnce &&
                BuildingPlacementSystem.TryCreateConstructionSite(
                    this, BuildingKind.Market, _simulation.marketPrefab,
                    _marketAxis, Economy.marketFlatRadius, Economy.marketBlendWidth,
                    Economy.marketCost, Economy.marketBuildSeconds, Economy.marketMaxBuilders,
                    out site, 1, 0f, true, true))
            {
                BeginConstruction(site);
                return;
            }
            if (BuildingPlacementSystem.TryCreateNearTownHall(
                    this, BuildingKind.Market, _simulation.marketPrefab,
                    Economy.marketMinDistance,
                    Economy.marketMaxDistance, Economy.marketFlatRadius,
                    Economy.marketBlendWidth, Economy.marketCost,
                    Economy.marketBuildSeconds, Economy.marketMaxBuilders,
                    out site))
            {
                BeginConstruction(site);
            }
            else if (_simulation.verboseEvents && Time.frameCount % 120 == 0)
            {
                Debug.LogWarning($"[FactionSimulation] {DisplayName} could not place Market beside Town Hall.", this);
            }
        }
        else if (Mint == null)
        {
            BuildingConstructionSite site;
            if (_mintBuiltOnce &&
                BuildingPlacementSystem.TryCreateConstructionSite(
                    this, BuildingKind.Mint, _simulation.mintPrefab,
                    _mintAxis, Economy.mintFlatRadius, Economy.mintBlendWidth,
                    Economy.mintCost, Economy.mintBuildSeconds, Economy.mintMaxBuilders,
                    out site, 1, 0f, true, true))
            {
                BeginConstruction(site);
                return;
            }
            if (BuildingPlacementSystem.TryCreateNearTownHall(
                    this, BuildingKind.Mint, _simulation.mintPrefab,
                    Economy.mintMinDistance,
                    Economy.mintMaxDistance, Economy.mintFlatRadius,
                    Economy.mintBlendWidth, Economy.mintCost,
                    Economy.mintBuildSeconds, Economy.mintMaxBuilders,
                    out site))
            {
                BeginConstruction(site);
            }
            else if (_simulation.verboseEvents && Time.frameCount % 120 == 0)
            {
                Debug.LogWarning($"[FactionSimulation] {DisplayName} could not place Mint beside Town Hall.", this);
            }
        }
    }

    public void BeginConstruction(BuildingConstructionSite site)
    {
        ActiveConstruction = site;
        if (State != FactionState.Recovering && State != FactionState.Retreating)
            State = FactionState.Economy;
    }

    Vector3 ResolveHallAxis()
    {
        if (_townHallBuiltOnce && _townHallAxis.sqrMagnitude > 1e-6f)
            return _townHallAxis;
        if (BaseOrigin != null && _simulation != null && _simulation.planet != null)
        {
            Vector3 axis = BaseOrigin.position - _simulation.planet.transform.position;
            if (axis.sqrMagnitude > 1e-6f)
                return axis.normalized;
        }
        return SpawnAxis;
    }

    Vector3 AxisFromBuilding(Building building)
    {
        if (building == null || _simulation == null || _simulation.planet == null)
            return SpawnAxis;
        Vector3 axis = building.transform.position - _simulation.planet.transform.position;
        return axis.sqrMagnitude > 1e-6f ? axis.normalized : SpawnAxis;
    }

    public void SetBarStartingStock(int metal, int energy)
    {
        Wood = Mathf.Max(Wood, Mathf.Max(0, metal));
        Stone = Mathf.Max(Stone, Mathf.Max(0, energy));
    }

    public void AddBarMetal(float amount)
    {
        if (amount <= 0f) return;
        _barMetalCarry += amount;
        int whole = (int)_barMetalCarry;
        if (whole > 0) { Wood += whole; _barMetalCarry -= whole; }
    }

    public void AddBarEnergy(float amount)
    {
        if (amount <= 0f) return;
        _barEnergyCarry += amount;
        int whole = (int)_barEnergyCarry;
        if (whole > 0) { Stone += whole; _barEnergyCarry -= whole; }
    }
    public bool HasResources(FactionResourceCost cost)
    {
        return Wood - ReservedWood >= cost.wood && Stone - ReservedStone >= cost.stone;
    }

    public bool TryReserveCost(FactionResourceCost cost)
    {
        if (!HasResources(cost))
            return false;
        ReservedWood += cost.wood;
        ReservedStone += cost.stone;
        return true;
    }

    public bool TryReserveConstructionCost(FactionResourceCost cost)
    {
        if (Wood - ReservedWood < cost.wood)
            return false;

        ReservedWood += cost.wood;
        int availableStone = Mathf.Max(0, Stone - ReservedStone);
        ReservedStone += Mathf.Min(cost.stone, availableStone);
        return true;
    }

    public bool TrySpendAvailableCost(FactionResourceCost cost)
    {
        if (!HasResources(cost))
            return false;
        Wood -= cost.wood;
        Stone -= cost.stone;
        return true;
    }

    public int WithdrawReservedResource(FactionResourceType type, int amount)
    {
        if (amount <= 0)
            return 0;

        if (type == FactionResourceType.Wood)
        {
            int withdrawn = Mathf.Min(amount, Wood);
            Wood -= withdrawn;
            ReservedWood = Mathf.Max(0, ReservedWood - withdrawn);
            return withdrawn;
        }

        int stoneWithdrawn = Mathf.Min(amount, Stone);
        Stone -= stoneWithdrawn;
        ReservedStone = Mathf.Max(0, ReservedStone - stoneWithdrawn);
        return stoneWithdrawn;
    }

    public void CommitReservedCost(FactionResourceCost cost)
    {
        // Any stock still reserved was not physically carried to the site. Consume only
        // that remainder; resources already withdrawn by workers are already paid.
        int woodRemainder = Mathf.Min(Wood, ReservedWood);
        int stoneRemainder = Mathf.Min(Stone, ReservedStone);
        Wood -= woodRemainder;
        Stone -= stoneRemainder;
        ReservedWood = Mathf.Max(0, ReservedWood - cost.wood);
        ReservedStone = Mathf.Max(0, ReservedStone - cost.stone);
    }

    public void ReleaseReservedCost(FactionResourceCost cost)
    {
        ReservedWood = Mathf.Max(0, ReservedWood - cost.wood);
        ReservedStone = Mathf.Max(0, ReservedStone - cost.stone);
    }

    public void AddResource(FactionResourceType type, int amount)
    {
        if (amount <= 0)
            return;
        if (type == FactionResourceType.Wood)
            Wood += amount;
        else
            Stone += amount;
    }

    public void AddGold(int amount)
    {
        if (amount > 0)
            Gold += amount;
    }

    public bool TrySpendGold(int amount)
    {
        if (amount <= 0)
            return true;
        if (Gold < amount)
            return false;
        Gold -= amount;
        return true;
    }

    public int GetAvailable(FactionResourceType type)
    {
        return type == FactionResourceType.Wood ? AvailableWood : AvailableStone;
    }

    public bool TryWithdrawResource(FactionResourceType type, int amount, out int taken)
    {
        taken = 0;
        if (amount <= 0)
            return false;
        int available = GetAvailable(type);
        taken = Mathf.Min(amount, available);
        if (taken <= 0)
            return false;
        if (type == FactionResourceType.Wood)
            Wood -= taken;
        else
            Stone -= taken;
        return true;
    }

    public int TradeSurplus(FactionResourceType type)
    {
        return TradeableAmount(type);
    }

    public int TradeableAmount(FactionResourceType type)
    {
        int available = GetAvailable(type);
        BuildingConstructionSite site = ActiveConstruction;
        if (site != null && !site.IsComplete)
        {
            int owed = type == FactionResourceType.Wood
                ? Mathf.Max(0, site.Cost.wood - site.DeliveredWood)
                : Mathf.Max(0, site.Cost.stone - site.DeliveredStone);
            available -= owed;
        }
        return Mathf.Max(0, available);
    }

    public int TradeNeed(FactionResourceType type)
    {
        int available = GetAvailable(type);
        int reserve = type == FactionResourceType.Wood
            ? Economy.tradeReserveWood
            : Economy.tradeReserveStone;
        int gap = StockGap(type);
        return Mathf.Max(gap, Mathf.Max(0, reserve - available));
    }

    public bool TrySpendMerchantTraining()
    {
        bool bar = BarFactionDirector.IsEnabled(this);
        if (!HasResources(Economy.merchantCost))
            return false;
        if (!bar && Gold < Economy.merchantGoldCost)
            return false;
        if (!TrySpendAvailableCost(Economy.merchantCost))
            return false;
        if (!bar)
            Gold -= Mathf.Max(0, Economy.merchantGoldCost);
        return true;
    }

    public bool IsAtWarWith(FactionController other)
    {
        return other != null &&
               CurrentRival == other &&
               (State == FactionState.Attacking || State == FactionState.Fighting);
    }

    public void FeedConstructionFromStock(BuildingConstructionSite site = null)
    {
        if (site == null)
            site = ActiveConstruction;
        if (site == null || site.IsComplete)
            return;

        int needWood = Mathf.Max(0, site.Cost.wood - site.DeliveredWood);
        int takeWood = Mathf.Min(needWood, Wood);
        if (takeWood > 0)
        {
            Wood -= takeWood;
            ReservedWood = Mathf.Max(0, ReservedWood - takeWood);
            site.TryDeliver(FactionResourceType.Wood, takeWood, out _);
        }

        int needStone = Mathf.Max(0, site.Cost.stone - site.DeliveredStone);
        int takeStone = Mathf.Min(needStone, Stone);
        if (takeStone > 0)
        {
            Stone -= takeStone;
            ReservedStone = Mathf.Max(0, ReservedStone - takeStone);
            site.TryDeliver(FactionResourceType.Stone, takeStone, out _);
        }
    }

    public bool DepositResource(FactionResourceType type, int amount)
    {
        if (amount <= 0)
            return false;

        if (type == FactionResourceType.Wood)
            WoodGathered += amount;
        else
            StoneGathered += amount;

        AddResource(type, amount);
        return true;
    }

    public void RegisterNpc(FactionNpc npc)
    {
        if (npc == null || _members.Contains(npc))
            return;
        _members.Add(npc);
        if (npc.Role == FactionNpcRole.ResourceGatherer)
            _workers.Add(npc);
        else if (npc.Role == FactionNpcRole.Merchant)
            _merchants.Add(npc);
        else if (npc.Role == FactionNpcRole.Noble)
            _nobles.Add(npc);
        else if (FactionNpcRoles.IsCombatSoldier(npc.Role))
            _soldiers.Add(npc);
    }

    public void UnregisterNpc(FactionNpc npc)
    {
        _members.Remove(npc);
        _workers.Remove(npc);
        _soldiers.Remove(npc);
        _merchants.Remove(npc);
        _nobles.Remove(npc);
    }

    public void NotifyBuildingCompleted(Building building)
    {
        if (building == null)
            return;
        if (building.Kind == BuildingKind.TownHall)
        {
            TownHall = building as TownHall;
            _townHallBuiltOnce = true;
            _townHallAxis = AxisFromBuilding(building);
        }
        else if (building.Kind == BuildingKind.Barracks)
        {
            Barracks = building as Barracks;
            _barracksBuiltOnce = true;
            _barracksAxis = AxisFromBuilding(building);
        }
        else if (building.Kind == BuildingKind.Market)
        {
            Market = building as Market;
            _marketBuiltOnce = true;
            _marketAxis = AxisFromBuilding(building);
        }
        else if (building.Kind == BuildingKind.Mint)
        {
            Mint = building as Mint;
            _mintBuiltOnce = true;
            _mintAxis = AxisFromBuilding(building);
        }
        else if (building.Kind == BuildingKind.Mex)
        {
            var mex = building as Mex;
            if (mex != null && !_mexes.Contains(mex))
                _mexes.Add(mex);
            if (mex != null && TerritorySystem.HasInstance)
                TerritorySystem.Instance.TryClaimPocketWithMex(this, mex);
        }
        else if (building.Kind == BuildingKind.EnergyGen)
        {
            var gen = building as EnergyGen;
            if (gen != null && !_energyGens.Contains(gen))
                _energyGens.Add(gen);
        }
        else if (building.Kind == BuildingKind.Factory)
        {
            Factory = building as Factory;
        }

        if (ActiveConstruction != null && ActiveConstruction.Building == building)
            ActiveConstruction = null;

        if (_simulation.verboseEvents)
            Debug.Log($"[FactionSimulation] {DisplayName} completed {building.Kind}.", this);
        if (building.Kind == BuildingKind.TownHall &&
            State != FactionState.Recovering &&
            State != FactionState.Retreating)
            State = FactionState.BuildingArmy;
        ReservedWood = 0;
        ReservedStone = 0;
    }

    public void NotifyBuildingDestroyed(Building building)
    {
        if (building == null)
            return;
        bool lostHall = TownHall == building;
        if (TownHall == building)
            TownHall = null;
        if (Barracks == building)
            Barracks = null;
        if (Market == building)
            Market = null;
        if (Mint == building)
            Mint = null;
        if (Factory == building)
            Factory = null;
        if (building is Mex mex)
        {
            if (TerritorySystem.HasInstance)
                TerritorySystem.Instance.NotifyMexDestroyed(mex);
            _mexes.Remove(mex);
        }
        if (building is EnergyGen gen)
            _energyGens.Remove(gen);
        if (_simulation.verboseEvents)
            Debug.Log($"[FactionSimulation] {DisplayName} lost {building.Kind}; rebuilding is enabled.", this);

        // Soft survival: town-hall loss relocates the campus instead of wiping the faction.
        if (lostHall)
        {
            BeginRelocateAndRebuild();
            return;
        }

        if (State != FactionState.Retreating)
            BeginRecovery();
    }

    /// <summary>
    /// Prefer rebuilding HQ near surviving campus buildings; otherwise far from rivals.
    /// </summary>
    public void BeginRelocateAndRebuild()
    {
        if (_simulation == null || _simulation.planet == null)
        {
            if (State != FactionState.Retreating)
                BeginRecovery();
            return;
        }

        Planet planet = _simulation.planet;
        Vector3 preferred;
        bool nearCampus = TryGetSurvivingCampusAxis(out Vector3 campusAxis);
        if (nearCampus)
        {
            preferred = campusAxis;
        }
        else
        {
            preferred = -SpawnAxis;
            IReadOnlyList<FactionController> factions = FactionRegistry.Factions;
            Vector3 threatSum = Vector3.zero;
            int threatCount = 0;
            for (int i = 0; i < factions.Count; i++)
            {
                FactionController other = factions[i];
                if (other == null || other == this)
                    continue;
                if (other.TownHall == null || !other.TownHall.IsOperational)
                    continue;
                threatSum += other.GetSafePosition();
                threatCount++;
            }
            if (threatCount > 0)
            {
                Vector3 threatAxis = (threatSum / threatCount - planet.transform.position).normalized;
                if (threatAxis.sqrMagnitude > 1e-6f)
                    preferred = -threatAxis;
            }
        }

        Vector3 newAxis = preferred;
        bool found = false;
        if (nearCampus)
        {
            // Cone search near surviving campus; still respect rival HQ separation.
            float minSep = Mathf.Max(80f, _simulation.minimumFactionSeparationFloor * 0.55f);
            for (int attempt = 0; attempt < 10 && !found; attempt++)
            {
                float cone = 12f + attempt * 8f;
                if (!TryFindDryAxisNear(preferred, cone, out Vector3 candidate))
                    continue;
                if (!IsAxisFarEnoughFromRivalHalls(candidate, minSep))
                    continue;
                newAxis = candidate;
                found = true;
            }
        }

        if (!found)
            found = _simulation.TryFindMainlandSpawnAxis(preferred, out newAxis);

        if (found)
        {
            SpawnAxis = newAxis;
            _townHallAxis = newAxis;
            _townHallBuiltOnce = true;
            if (BaseOrigin != null)
            {
                Destroy(BaseOrigin.gameObject);
                BaseOrigin = null;
            }
            EnsureBaseArea();

            // Seed enough stock to restart Town Hall construction.
            if (BarFactionDirector.IsEnabled(this))
            {
                Wood = Mathf.Max(Wood, Economy.startingMetal + Economy.townHallCost.wood);
                Stone = Mathf.Max(Stone, Economy.startingEnergy + Economy.townHallCost.stone);
            }
            else
            {
                Wood = Mathf.Max(Wood, Economy.townHallCost.wood + Economy.startingWood);
                Stone = Mathf.Max(Stone, Economy.townHallCost.stone + Economy.startingStone);
            }
        }

        if (State != FactionState.Retreating)
            BeginRecovery();

        // Fresh growth window after soft relocate so bullies cannot instantly re-stomp.
        CampusFoundedAt = Time.time;
        _baseBuildStruggleStartedAt = Time.time;
        if (Economy != null)
            ArmBarProtect(Mathf.Max(Economy.barAssaultGraceSeconds, Economy.barPostRecoveryProtectSeconds));

        if (WorkerCount <= 0)
            SeedRecoveryWorkers(immediate: true);

        if (Stargrave.Rts2.Rts2UnitSim.HasInstance)
            Stargrave.Rts2.Rts2UnitSim.Instance.SetHomeGoal(RuntimeIndex, GetSafePosition());

        if (_simulation.verboseEvents)
            Debug.Log(
                nearCampus
                    ? $"[FactionSimulation] {DisplayName} rebuilt HQ near surviving campus."
                    : $"[FactionSimulation] {DisplayName} relocated after Town Hall loss.",
                this);
    }

    bool TryGetSurvivingCampusAxis(out Vector3 axis)
    {
        axis = default;
        if (_simulation == null || _simulation.planet == null)
            return false;

        Vector3 sum = Vector3.zero;
        int count = 0;
        void Acc(Building b)
        {
            if (b == null)
                return;
            Vector3 from = b.transform.position - _simulation.planet.transform.position;
            if (from.sqrMagnitude < 1e-6f)
                return;
            sum += from.normalized;
            count++;
        }

        Acc(Barracks);
        Acc(Market);
        Acc(Mint);
        Acc(Factory);
        for (int i = 0; i < _mexes.Count; i++)
            Acc(_mexes[i]);
        for (int i = 0; i < _energyGens.Count; i++)
            Acc(_energyGens[i]);
        if (ActiveConstruction != null)
        {
            Vector3 fromSite = ActiveConstruction.transform.position - _simulation.planet.transform.position;
            if (fromSite.sqrMagnitude > 1e-6f)
            {
                sum += fromSite.normalized;
                count++;
            }
        }

        if (count <= 0)
            return false;
        axis = sum.normalized;
        return axis.sqrMagnitude > 1e-6f;
    }

    bool IsAxisFarEnoughFromRivalHalls(Vector3 axis, float minWorldSeparation)
    {
        if (_simulation == null || _simulation.planet == null)
            return true;
        Vector3 pos = _simulation.planet.GetSurfacePointWorld(axis.normalized);
        float minSq = minWorldSeparation * minWorldSeparation;
        IReadOnlyList<FactionController> factions = FactionRegistry.Factions;
        for (int i = 0; i < factions.Count; i++)
        {
            FactionController other = factions[i];
            if (other == null || other == this || other.TownHall == null)
                continue;
            if ((other.TownHall.transform.position - pos).sqrMagnitude < minSq)
                return false;
        }
        return true;
    }

    public Vector3 ResolveMerchantTradeDestination()
    {
        // Prefer rival operational markets, then owned claimed towns, then home market.
        Vector3 home = Market != null && Market.IsOperational
            ? Market.transform.position
            : GetSafePosition();

        FactionController bestRival = null;
        float bestSq = float.PositiveInfinity;
        IReadOnlyList<FactionController> factions = FactionRegistry.Factions;
        for (int i = 0; i < factions.Count; i++)
        {
            FactionController other = factions[i];
            if (other == null || other == this)
                continue;
            if (other.Market == null || !other.Market.IsOperational)
                continue;
            if (FactionBalanceSystem.AreCoalitionAllies(this, other))
                continue;
            float d = (other.Market.transform.position - home).sqrMagnitude;
            // Prefer corridors we already own more of (territory-aware trade).
            float owned = TerritorySystem.HasInstance
                ? TerritorySystem.Instance.OwnedFractionAlongSegment(RuntimeIndex, home, other.Market.transform.position)
                : 0f;
            float score = d * (1.35f - 0.7f * owned);
            if (score < bestSq)
            {
                bestSq = score;
                bestRival = other;
            }
        }
        if (bestRival != null)
            return bestRival.Market.transform.position;

        ClaimableTown bestTown = null;
        bestSq = float.PositiveInfinity;
        IReadOnlyList<ClaimableTown> towns = FactionRegistry.Towns;
        for (int i = 0; i < towns.Count; i++)
        {
            ClaimableTown town = towns[i];
            if (town == null || town.Owner != this)
                continue;
            float d = (town.transform.position - home).sqrMagnitude;
            if (d < bestSq)
            {
                bestSq = d;
                bestTown = town;
            }
        }
        if (bestTown != null)
            return bestTown.transform.position;

        return home;
    }

    public void ApplyMerchantTradeReward(bool rivalMarket, bool ownedTown, float routeOwnedFraction = -1f)
    {
        if (Economy == null)
            return;
        float owned = routeOwnedFraction;
        if (owned < 0f && TerritorySystem.HasInstance && Market != null)
        {
            Vector3 from = Market.transform.position;
            Vector3 to = ResolveMerchantTradeDestination();
            owned = TerritorySystem.Instance.OwnedFractionAlongSegment(RuntimeIndex, from, to);
        }
        owned = Mathf.Clamp01(owned < 0f ? 0f : owned);
        float mul = 1f + owned * Economy.territoryTradeOwnedBonus;

        if (rivalMarket)
        {
            AddBarMetal(Mathf.RoundToInt(Economy.merchantTradeMetal * mul));
            AddBarEnergy(Mathf.RoundToInt(Economy.merchantTradeEnergy * mul));
            if (!BarFactionDirector.IsEnabled(this))
                AddGold(Mathf.Max(1, Economy.merchantTownVisitGold));
        }
        if (ownedTown)
        {
            AddBarMetal(Mathf.RoundToInt(Economy.merchantTownVisitMetal * mul));
            AddBarEnergy(Mathf.RoundToInt(Economy.merchantTownVisitEnergy * mul));
            AddGold(Mathf.Max(0, Mathf.RoundToInt(Economy.merchantTownVisitGold * mul)));
        }
    }

    public float MerchantRouteOwnedFraction(Vector3 from, Vector3 to)
    {
        if (!TerritorySystem.HasInstance)
            return 0f;
        return TerritorySystem.Instance.OwnedFractionAlongSegment(RuntimeIndex, from, to);
    }

    /// <summary>
    /// Soft immortality:
    /// - No HQ → relocate to a safe pad and seed a worker batch.
    /// - HQ up + 0 workers → paid/free worker production handles refill (not a free batch).
    /// </summary>
    void TickSoftImmortality()
    {
        if (!HasFoundedCampus || Economy == null)
            return;

        bool hasBase = TownHall != null && TownHall.IsOperational;
        if (hasBase && BarFactionDirector.IsEnabled(this))
        {
            // BAR: continuous worker production tops up; free-at-zero when broke.
            _wipeReviveArmed = false;
            return;
        }

        if (WorkerCount > 0)
        {
            // Builders still on site rebuilding HQ — don't relocate yet.
            _wipeReviveArmed = false;
            return;
        }

        if (!_wipeReviveArmed)
        {
            _wipeReviveArmed = true;
            float delay = Mathf.Max(1f, Economy.wipeReviveDelaySeconds);
            _wipeReviveAt = Time.time + delay;
            return;
        }

        if (Time.time < _wipeReviveAt)
            return;

        if (!hasBase)
        {
            // No base and no workers: new safe campus + fresh worker batch.
            BeginRelocateAndRebuild();
            return;
        }

        // Village fallback: HQ up but crew wiped — seed a recovery batch.
        SeedRecoveryWorkers(immediate: true);
    }

    public float WorkerBuildTimer
    {
        get => _workerBuildTimer;
        set => _workerBuildTimer = Mathf.Max(0f, value);
    }

    public bool TrySpawnWorkerAtBase(bool free)
    {
        if (!Stargrave.Rts2.Rts2UnitSim.HasInstance || Economy == null)
            return false;
        if (WorkerCount >= Economy.workerMaxCount)
            return false;

        Vector3 axis = SpawnAxis.sqrMagnitude > 1e-6f ? SpawnAxis.normalized : Vector3.up;
        if (_simulation != null && _simulation.planet != null)
        {
            Vector3 home = GetSafePosition();
            Vector3 fromCenter = home - _simulation.planet.transform.position;
            if (fromCenter.sqrMagnitude > 1e-8f)
                axis = fromCenter.normalized;
        }

        if (!Stargrave.Rts2.Rts2UnitSim.Instance.TrySpawn(
                RuntimeIndex, RtsUnitRole.Worker, axis, out _))
            return false;

        Stargrave.Rts2.Rts2UnitSim.Instance.SetGatherTask(RuntimeIndex, FactionResourceType.Wood);
        if (_simulation != null && _simulation.verboseEvents)
        {
            Debug.Log(
                "[FactionSimulation] " + DisplayName +
                (free ? " produced a free recovery worker." : " produced a worker."),
                this);
        }
        return true;
    }

    void SeedRecoveryWorkers(bool immediate)
    {
        if (!Stargrave.Rts2.Rts2UnitSim.HasInstance || Economy == null)
            return;
        if (WorkerCount > 0)
        {
            _wipeReviveArmed = false;
            return;
        }

        if (!immediate && Time.time < _wipeReviveAt)
            return;

        // Do not call EnsureBaseArea here — that bakes/regenerates the planet and hitchs.
        // Relocate already rebuilds the pad; revive only needs workers + stock.
        if (BarFactionDirector.IsEnabled(this))
        {
            Wood = Mathf.Max(Wood, Economy.startingMetal + Economy.mexCost.wood + Economy.factoryCost.wood);
            Stone = Mathf.Max(Stone, Economy.startingEnergy + Economy.energyGenCost.stone + Economy.factoryCost.stone);
        }
        else
        {
            Wood = Mathf.Max(Wood, Economy.townHallCost.wood + Economy.startingWood);
            Stone = Mathf.Max(Stone, Economy.townHallCost.stone + Economy.startingStone);
        }
        if (State != FactionState.Retreating)
            BeginRecovery();

        Vector3 axis = SpawnAxis.sqrMagnitude > 1e-6f ? SpawnAxis.normalized : Vector3.up;
        if (_simulation != null && _simulation.planet != null)
        {
            Vector3 home = GetSafePosition();
            Vector3 fromCenter = home - _simulation.planet.transform.position;
            if (fromCenter.sqrMagnitude > 1e-8f)
                axis = fromCenter.normalized;
        }

        int want = Mathf.Clamp(Economy.startingWorkers, 1, Economy.workerMaxCount);
        int spawned = 0;
        for (int i = 0; i < want; i++)
        {
            Vector3 spawnAxis = axis;
            if (i > 0)
            {
                Vector3 tangent = Vector3.Cross(axis, Vector3.up);
                if (tangent.sqrMagnitude < 1e-6f)
                    tangent = Vector3.Cross(axis, Vector3.right);
                tangent.Normalize();
                float ang = (i / (float)want) * Mathf.PI * 2f;
                spawnAxis = (axis + (tangent * Mathf.Cos(ang) + Vector3.Cross(axis, tangent) * Mathf.Sin(ang)) * 0.04f)
                    .normalized;
            }

            if (Stargrave.Rts2.Rts2UnitSim.Instance.TrySpawn(
                    RuntimeIndex, RtsUnitRole.Worker, spawnAxis, out _))
                spawned++;
        }

        if (spawned > 0)
            Stargrave.Rts2.Rts2UnitSim.Instance.SetGatherTask(RuntimeIndex, FactionResourceType.Wood);

        _wipeReviveArmed = false;
        if (_simulation != null && _simulation.verboseEvents)
            Debug.Log("[FactionSimulation] " + DisplayName + " seeded " + spawned + " recovery workers.", this);
    }

    public void ClearActiveConstruction(BuildingConstructionSite site)
    {
        if (ActiveConstruction == site)
            ActiveConstruction = null;
    }

    public void BeginAttack(FactionController rival)
    {
        CurrentRival = rival;
        if (rival != null)
            FactionTradeSystem.NotifyAttackDeclared(this, rival);
        if (rival != null && (rival.CurrentRival == null || rival.CurrentRival == this))
            rival.BindRival(this);
        PreviousAttackStrength = SoldierCount * Warfare.soldierStrength;
        PreviousAttackSoldiers = SoldierCount;
        _approachWave++;
        _plannedMuster = 0;
        ResetAssaultFront();
        State = FactionState.Attacking;
        FactionBalanceSystem.AddThreat(this, Warfare.threatOnAttack);
        if (_simulation.verboseEvents)
            Debug.Log(
                $"[FactionSimulation] {DisplayName} attack started on {(rival != null ? rival.DisplayName : "none")} " +
                $"with {PreviousAttackSoldiers} soldiers (approach {_approachWave}).",
                this);
    }

    /// <summary>
    /// Player damaged or killed one of our units — treat like a rival opening fire.
    /// Soldiers will hunt the player while HostileToPlayer is true.
    /// </summary>
    public void NotifyAttackedByPlayer(Transform player, bool unitKilled)
    {
        if (player == null)
            return;

        PlayerAggressor = player;
        float hold = Warfare != null ? Mathf.Max(20f, Warfare.playerAggroSeconds) : 90f;
        if (unitKilled)
            hold *= 1.35f;
        _hostileToPlayerUntil = Mathf.Max(_hostileToPlayerUntil, Time.time + hold);

        Vector3 fight = player.position;
        RequestDefense(fight);
        RequestSkirmishBackup(fight, unitKilled ? 6 : 3);
        MarkFighting();
        if (State == FactionState.Economy || State == FactionState.BuildingArmy || State == FactionState.Claiming)
            State = FactionState.Fighting;

        FactionBalanceSystem.AddThreat(this, Warfare != null ? Warfare.threatOnAttack : 0.08f);
        if (unitKilled)
            FactionBalanceSystem.AddThreat(this, Warfare != null ? Warfare.threatOnAttack * 0.5f : 0.04f);

        if (_simulation != null && _simulation.verboseEvents)
            Debug.Log(
                $"[FactionSimulation] {DisplayName} {(unitKilled ? "lost a unit to" : "engaged by")} the player — retaliating.",
                this);
    }

    public void BindRival(FactionController rival)
    {
        CurrentRival = rival;
    }

    public void BeginRetreat()
    {
        if (State == FactionState.Retreating)
            return;
        State = FactionState.Retreating;
        ClearDefense();
        ClearBackup();
        ResetAssaultFront();
        _failedWaves++;
        _nextAttackAllowed = Time.time + Warfare.attackReplanSeconds;
        if (_simulation.verboseEvents)
            Debug.Log($"[FactionSimulation] {DisplayName} retreat started.", this);
    }

    public void BeginRecovery()
    {
        if (State == FactionState.Recovering)
            return;
        State = FactionState.Recovering;
        ClearDefense();
        ClearBackup();
        _nextAttackAllowed = Mathf.Max(_nextAttackAllowed, Time.time + Warfare.attackReplanSeconds);
        ClearRivalIfPaired();
        if (BarFactionDirector.IsEnabled(this) && Economy != null)
            ArmBarProtect(Mathf.Max(Economy.barAssaultGraceSeconds, Economy.barPostRecoveryProtectSeconds));
        if (_simulation.verboseEvents)
            Debug.Log($"[FactionSimulation] {DisplayName} recovery started.", this);
    }

    public bool NeedsRecoup()
    {
        if (!HasFoundedCampus)
            return false;
        if (TownHall == null || !TownHall.IsOperational)
            return true;
        if (BarFactionDirector.IsEnabled(this))
            return Factory == null || !Factory.IsOperational ||
                   WorkerCount < Mathf.Max(1, Economy.startingWorkers);
        if (Barracks == null || !Barracks.IsOperational)
            return true;
        return WorkerCount < Economy.startingWorkers;
    }

    public bool CanEndRecovery()
    {
        if (TownHall == null || !TownHall.IsOperational)
            return false;
        if (BarFactionDirector.IsEnabled(this))
        {
            // BAR: leave recovery once the rebuild path is online — do not gate on
            // PreviousAttackStrength (that trapped wiped factions in Recovering forever).
            if (Factory == null || !Factory.IsOperational)
                return false;
            if (WorkerCount < Mathf.Max(1, Economy.startingWorkers))
                return false;
            return true;
        }
        if (Barracks == null || !Barracks.IsOperational)
            return false;
        if (WorkerCount < Economy.startingWorkers)
            return false;
        if (PreviousAttackStrength <= 0f)
            return true;
        return Strength >= PreviousAttackStrength * Warfare.recoveryStrengthFraction;
    }

    public void RequestDefense(Vector3 position)
    {
        if (State == FactionState.Retreating)
            return;
        float radius = Combat.soldierDefendRadius;
        if ((position - GetSafePosition()).sqrMagnitude > radius * radius)
            return;
        DefensePoint = position;
        HasDefensePing = true;
        _defenseUntil = Time.time + 4f;
        RememberHostileContact(position);
    }

    public void RequestSkirmishBackup(Vector3 fight, int extraNeeded)
    {
        if (State == FactionState.Retreating || State == FactionState.Recovering || !CanSendAssault)
            return;

        extraNeeded = Mathf.Clamp((extraNeeded + 1) & ~1, 2, 8);
        bool active = HasBackupPing && Time.time < _backupUntil;
        BackupPoint = fight;
        HasBackupPing = true;
        BackupQuota = active
            ? Mathf.Min(8, Mathf.Max(BackupQuota, extraNeeded))
            : extraNeeded;
        _backupUntil = Time.time + 6f;
        RememberHostileContact(fight);
    }

    public bool ShouldAnswerBackup(FactionNpc soldier)
    {
        if (!HasBackupPing || soldier == null || soldier.IsDead || BackupQuota <= 0)
            return false;
        if (!CanSendAssault)
            return false;
        if (soldier.Soldier != null && soldier.Soldier.IsInCombat)
            return false;

        float selfSq = (soldier.transform.position - BackupPoint).sqrMagnitude;
        int closerIdle = 0;
        for (int i = 0; i < _soldiers.Count; i++)
        {
            FactionNpc other = _soldiers[i];
            if (other == null || other.IsDead || other == soldier)
                continue;
            if (other.Soldier != null && other.Soldier.IsInCombat)
                continue;
            float d = (other.transform.position - BackupPoint).sqrMagnitude;
            if (d < selfSq)
                closerIdle++;
        }
        return closerIdle < BackupQuota;
    }

    public void RememberHostileContact(Vector3 position)
    {
        _hostileContact = position;
        _hostileContactTime = Time.time;
    }

    public bool IsScoutSoldier(FactionNpc soldier)
    {
        if (soldier == null || soldier.IsDead)
            return false;
        int cap = Combat.soldierScoutCount > 0 ? Combat.soldierScoutCount : 3;
        int seen = 0;
        for (int i = 0; i < _soldiers.Count; i++)
        {
            FactionNpc npc = _soldiers[i];
            if (npc == null || npc.IsDead)
                continue;
            if (npc == soldier)
                return seen < cap;
            seen++;
        }
        return false;
    }

    public bool CanSendAssault
    {
        get
        {
            if (State != FactionState.Attacking && State != FactionState.Fighting)
                return false;
            int squad = Combat.soldierSquadSize > 0 ? Combat.soldierSquadSize : 4;
            return SoldierCount >= squad;
        }
    }

    public bool AttackReplanReady => Time.time >= _nextAttackAllowed;

    public bool IsBarWarReady =>
        TownHall != null && TownHall.IsOperational &&
        Factory != null && Factory.IsOperational;

    public bool IsInBarAssaultGrace
    {
        get
        {
            if (!HasFoundedCampus || Economy == null)
                return true;
            float grace = Mathf.Max(0f, Economy.barAssaultGraceSeconds);
            return grace > 0f && Time.time < CampusFoundedAt + grace;
        }
    }

    public bool IsBarProtectActive => Time.time < _barProtectUntil;

    public float BarProtectSecondsRemaining =>
        Mathf.Max(0f, _barProtectUntil - Time.time);

    public void ArmBarProtect(float seconds)
    {
        if (seconds <= 0f)
            return;
        _barProtectUntil = Mathf.Max(_barProtectUntil, Time.time + seconds);
    }

    /// <summary>
    /// Weak / rebuilding factions are off-limits for BAR assaults so they can regrow.
    /// </summary>
    public bool IsBarAssaultProtected
    {
        get
        {
            if (!HasFoundedCampus)
                return true;
            if (State == FactionState.Recovering || State == FactionState.Retreating)
                return true;
            if (IsInBarAssaultGrace || IsBarProtectActive)
                return true;
            if (TownHall == null || !TownHall.IsOperational)
                return true;
            if (Economy == null)
                return false;
            return LivingUnitCount < Mathf.Max(1, Economy.barProtectedLivingFloor);
        }
    }

    public bool TryPickSoldierDutyPoint(FactionNpc soldier, out Vector3 destination)
    {
        destination = GetSafePosition();
        float patrol = Combat.soldierPatrolRadius > 0f ? Combat.soldierPatrolRadius : 32f;

        if (ClaimTargetTown != null &&
            ClaimTargetTown.Owner != this &&
            NobleCount > 0)
        {
            FactionNpc noble = FindNearestLiving(_nobles, soldier.transform.position);
            if (noble != null)
            {
                destination = JitterOnSurface(noble.transform.position, 3f, 8f);
                return true;
            }
            destination = JitterOnSurface(ClaimTargetTown.transform.position, 4f, 10f);
            return true;
        }

        ClaimableTown ownedTown = FindOwnedTownNeedingGarrison(soldier.transform.position);
        if (ownedTown != null)
        {
            destination = JitterOnSurface(ownedTown.transform.position, 4f, patrol * 0.5f);
            return true;
        }

        if (!CanSendAssault)
        {
            destination = JitterOnSurface(GetSafePosition(), 8f, patrol);
            return true;
        }

        destination = JitterOnSurface(
            GetAssaultApproachPoint(soldier),
            6f,
            Mathf.Max(10f, Combat.attackFormationRadius));
        return true;
    }

    ClaimableTown FindOwnedTownNeedingGarrison(Vector3 from)
    {
        ClaimableTown best = null;
        float bestSq = float.PositiveInfinity;
        IReadOnlyList<ClaimableTown> towns = FactionRegistry.Towns;
        for (int i = 0; i < towns.Count; i++)
        {
            ClaimableTown town = towns[i];
            if (town == null || town.Owner != this)
                continue;
            float d = (town.transform.position - from).sqrMagnitude;
            if (d < bestSq)
            {
                bestSq = d;
                best = town;
            }
        }
        return best;
    }

    Vector3 GetAssaultApproachPoint(FactionNpc soldier)
    {
        Vector3 home = GetSafePosition();
        FactionController opposing = FactionRegistry.FindOpposingFaction(this);
        if (opposing == null)
            return home;

        Vector3 enemy = opposing.GetSafePosition();
        Vector3 toEnemy = enemy - home;
        if (toEnemy.sqrMagnitude < 1e-4f)
            return home;

        Vector3 forward = toEnemy.normalized;
        Vector3 up = home.sqrMagnitude > 1e-4f ? home.normalized : Vector3.up;
        Vector3 right = Vector3.Cross(up, forward);
        if (right.sqrMagnitude < 1e-6f)
            right = Vector3.Cross(Vector3.up, forward);
        right.Normalize();

        int wave = Mathf.Max(0, _approachWave);
        bool scout = IsScoutSoldier(soldier);
        float sign = ((wave + (scout ? 1 : 0)) & 1) == 0 ? -1f : 1f;
        float width = Warfare.approachFlankDistance * (1f + (wave % 3) * 0.45f);
        if (scout)
            width *= 1.25f;
        float range = Combat.soldierPatrolRadius > 0f ? Combat.soldierPatrolRadius : 32f;
        Vector3 offset = forward * range + right * (sign * width);
        return OffsetOnSurface(home, offset);
    }

    public void ClearDefense()
    {
        HasDefensePing = false;
    }

    public void ClearBackup()
    {
        HasBackupPing = false;
        BackupQuota = 0;
    }

    public void TickDefenseExpiry()
    {
        if (HasDefensePing && Time.time >= _defenseUntil)
            HasDefensePing = false;
        if (HasBackupPing && Time.time >= _backupUntil)
            ClearBackup();
    }

    public void MarkFighting()
    {
        if (State == FactionState.Attacking)
            State = FactionState.Fighting;
    }

    public void AssignGatherTask(FactionNpc worker)
    {
        if (worker == null || worker.Worker == null)
            return;

        int woodGap = StockGap(FactionResourceType.Wood);
        int stoneGap = StockGap(FactionResourceType.Stone);
        FactionResourceType task;
        if (woodGap != stoneGap)
            task = woodGap > stoneGap ? FactionResourceType.Wood : FactionResourceType.Stone;
        else
        {
            int woodWorkers = CountAssignedGatherers(FactionResourceType.Wood, worker);
            int stoneWorkers = CountAssignedGatherers(FactionResourceType.Stone, worker);
            task = woodWorkers <= stoneWorkers
                ? FactionResourceType.Wood
                : FactionResourceType.Stone;
        }

        worker.Worker.AcceptGatherTask(task);
        if (task == FactionResourceType.Stone &&
            _simulation != null &&
            _simulation.TryGetNearestProspect(task, GetSafePosition(), out Vector3 prospect))
        {
            RememberStreamFocus(prospect, true, FactionResourceType.Stone);
        }
    }

    int StockGap(FactionResourceType type)
    {
        int stock = type == FactionResourceType.Wood ? Wood : Stone;
        int need = 0;
        BuildingConstructionSite site = ActiveConstruction;
        if (site != null && !site.IsComplete)
        {
            need = type == FactionResourceType.Wood
                ? Mathf.Max(0, site.Cost.wood - site.DeliveredWood)
                : Mathf.Max(0, site.Cost.stone - site.DeliveredStone);
        }

        if (need > 0)
            return Mathf.Max(0, need - stock);

        int other = type == FactionResourceType.Wood ? Stone : Wood;
        return Mathf.Max(0, other - stock);
    }

    int CountAssignedGatherers(FactionResourceType type, FactionNpc except)
    {
        int count = 0;
        for (int i = 0; i < _workers.Count; i++)
        {
            FactionNpc npc = _workers[i];
            if (npc == null || npc == except || npc.IsDead || npc.Worker == null)
                continue;
            if (npc.Worker.HasGatherTask && npc.Worker.GatherTask == type)
                count++;
        }
        return count;
    }

    public Vector3 GetSafePosition()
    {
        if (TownHall != null)
            return TownHall.transform.position;
        if (ActiveConstruction != null)
            return ActiveConstruction.transform.position;
        if (BaseOrigin != null)
            return BaseOrigin.position;
        if (_simulation != null && _simulation.planet != null)
            return _simulation.planet.GetSurfacePointWorld(SpawnAxis);
        return transform.position;
    }

    /// <summary>
    /// Assault pressure point: prefer a rival mex (eco raid) over walking straight at HQ.
    /// </summary>
    public Vector3 GetAssaultPressurePoint(FactionController rival)
    {
        if (rival == null)
            return GetSafePosition();

        Vector3 from = GetSafePosition();
        Vector3 best = rival.GetSafePosition();
        float bestSq = (best - from).sqrMagnitude;
        for (int i = 0; i < rival._mexes.Count; i++)
        {
            Mex mex = rival._mexes[i];
            if (mex == null || !mex.IsOperational)
                continue;
            float d = (mex.transform.position - from).sqrMagnitude;
            if (d < bestSq)
            {
                bestSq = d;
                best = mex.transform.position;
            }
        }
        return best;
    }

    /// <summary>Current staged front waypoint (mid → mex → HQ).</summary>
    public Vector3 AssaultFrontGoal { get; private set; }

    /// <summary>0 = mid, 1 = pressure/mex, 2 = HQ.</summary>
    public int AssaultFrontStage => _assaultStage;

    void ResetAssaultFront()
    {
        _assaultStage = 0;
        _assaultStageArrived = false;
        _assaultStageHoldUntil = 0f;
        AssaultFrontGoal = GetSafePosition();
    }

    void TickAssaultFront(FactionController rival)
    {
        if (rival == null)
        {
            AssaultFrontGoal = GetSafePosition();
            return;
        }

        Vector3 home = GetSafePosition();
        Vector3 pressure = GetAssaultPressurePoint(rival);
        Vector3 hq = rival.GetSafePosition();
        Vector3 mid = SurfaceLerp(home, pressure, 0.48f);

        // Resolve goal for current stage.
        Vector3 goal = _assaultStage <= 0 ? mid : (_assaultStage == 1 ? pressure : hq);
        AssaultFrontGoal = goal;

        if (!Stargrave.Rts2.Rts2UnitSim.HasInstance)
            return;

        var sim = Stargrave.Rts2.Rts2UnitSim.Instance;
        float arriveR = 32f;
        float contactR = 40f;
        int near = sim.CountNear(RuntimeIndex, goal, arriveR, combatOnly: true);
        int enemies = sim.CountEnemyCombatNear(RuntimeIndex, goal, contactR);
        int need = Mathf.Max(3, Mathf.CeilToInt(Mathf.Max(1, SoldierCount) * 0.32f));

        // Contact line: hold this stage while a fight is on.
        if (enemies > 0)
        {
            _assaultStageHoldUntil = Mathf.Max(_assaultStageHoldUntil, Time.time + 2.5f);
            return;
        }

        if (near >= need)
        {
            if (!_assaultStageArrived)
            {
                _assaultStageArrived = true;
                _assaultStageHoldUntil = Time.time + 5f; // staged hold / wave feel
                if (_simulation != null && _simulation.verboseEvents)
                    Debug.Log(
                        $"[FactionSimulation] {DisplayName} holding assault stage {_assaultStage} " +
                        $"({near}/{need} near front).",
                        this);
            }

            if (_assaultStage < 2 && Time.time >= _assaultStageHoldUntil)
            {
                _assaultStage++;
                _assaultStageArrived = false;
                _assaultStageHoldUntil = 0f;
                AssaultFrontGoal = _assaultStage == 1 ? pressure : hq;
                if (_simulation != null && _simulation.verboseEvents)
                    Debug.Log(
                        $"[FactionSimulation] {DisplayName} advancing assault to stage {_assaultStage}.",
                        this);
            }
        }
    }

    Vector3 SurfaceLerp(Vector3 a, Vector3 b, float t)
    {
        t = Mathf.Clamp01(t);
        if (_simulation == null || _simulation.planet == null)
            return Vector3.Lerp(a, b, t);
        Planet planet = _simulation.planet;
        Vector3 center = planet.transform.position;
        Vector3 axisA = (a - center).normalized;
        Vector3 axisB = (b - center).normalized;
        if (axisA.sqrMagnitude < 1e-6f || axisB.sqrMagnitude < 1e-6f)
            return Vector3.Lerp(a, b, t);
        Vector3 axis = Vector3.Slerp(axisA, axisB, t).normalized;
        return planet.GetSurfacePointWorld(axis);
    }

    public int StreamFocusCount => _streamFoci.Count;

    public Vector3 GetStreamFocus(int index) => _streamFoci[index];

    public int GetResourceSiteCount(FactionResourceType type)
    {
        return type == FactionResourceType.Wood ? _woodSites.Count : _stoneSites.Count;
    }

    public Vector3 GetResourceSite(FactionResourceType type, int index)
    {
        List<Vector3> sites = type == FactionResourceType.Wood ? _woodSites : _stoneSites;
        return sites[index];
    }

    public void RememberResourceSite(FactionResourceType type, Vector3 position)
    {
        List<Vector3> sites = type == FactionResourceType.Wood ? _woodSites : _stoneSites;
        List<float> used = type == FactionResourceType.Wood ? _woodSiteUsed : _stoneSiteUsed;
        float mergeSq = ResourceSiteMergeDistance * ResourceSiteMergeDistance;
        int existing = -1;
        for (int i = 0; i < sites.Count; i++)
        {
            if ((sites[i] - position).sqrMagnitude <= mergeSq)
            {
                existing = i;
                break;
            }
        }

        if (existing >= 0)
        {
            sites[existing] = position;
            used[existing] = Time.time;
            return;
        }

        if (sites.Count >= MaxResourceSites)
        {
            Vector3 origin = GetSafePosition();
            int farthest = 0;
            float farthestSq = (sites[0] - origin).sqrMagnitude;
            for (int i = 1; i < sites.Count; i++)
            {
                float d = (sites[i] - origin).sqrMagnitude;
                if (d > farthestSq)
                {
                    farthestSq = d;
                    farthest = i;
                }
            }
            sites[farthest] = position;
            used[farthest] = Time.time;
            return;
        }

        sites.Add(position);
        used.Add(Time.time);
    }

    public bool TryGetNearestResourceSite(FactionResourceType type, Vector3 from, out Vector3 site)
    {
        List<Vector3> sites = type == FactionResourceType.Wood ? _woodSites : _stoneSites;
        site = default;
        Vector3 origin = GetSafePosition();
        float bestSq = float.PositiveInfinity;
        for (int i = 0; i < sites.Count; i++)
        {
            float d = (sites[i] - origin).sqrMagnitude;
            if (d < bestSq)
            {
                bestSq = d;
                site = sites[i];
            }
        }
        return bestSq < float.PositiveInfinity;
    }

    public bool TryPickGatherSearchPoint(FactionResourceType type, Vector3 from, out Vector3 destination)
    {
        destination = from;
        if (TryGetNearestResourceSite(type, from, out Vector3 known))
        {
            destination = JitterOnSurface(known, 15f, 40f);
            return true;
        }

        return TryScoutResourceBiome(type, from, out destination);
    }

    public Vector3 JitterAround(Vector3 origin, float minOffset, float maxOffset)
    {
        return JitterOnSurface(origin, minOffset, maxOffset);
    }

    public Vector3 OffsetOnSurface(Vector3 origin, Vector3 planarOffset)
    {
        Planet planet = _simulation != null ? _simulation.planet : null;
        if (planet == null || planarOffset.sqrMagnitude < 1e-8f)
            return origin;

        Vector3 center = planet.transform.position;
        Vector3 here = (origin - center).normalized;
        Vector3 planar = Vector3.ProjectOnPlane(planarOffset, here);
        float planetR = Mathf.Max(1f, Vector3.Distance(origin, center));
        Vector3 axis = (here + planar / planetR).normalized;
        if (TryGetDrySurfacePoint(axis, out Vector3 point))
            return point;
        return planet.GetSurfacePointWorld(axis);
    }

    bool TryScoutResourceBiome(FactionResourceType type, Vector3 from, out Vector3 destination)
    {
        destination = from;
        if (_simulation == null || !_simulation.TryGetNearestProspect(type, from, out Vector3 prospect))
            return false;

        destination = JitterOnSurface(prospect, 10f, 20f);
        return true;
    }

    Vector3 JitterOnSurface(Vector3 origin, float minOffset, float maxOffset)
    {
        Planet planet = _simulation != null ? _simulation.planet : null;
        if (planet == null)
            return origin;

        Vector3 center = planet.transform.position;
        Vector3 here = (origin - center).normalized;
        Vector3 reference = Mathf.Abs(Vector3.Dot(here, Vector3.up)) > 0.9f ? Vector3.right : Vector3.up;
        Vector3 tangent = Vector3.Cross(here, reference).normalized;
        Vector3 other = Vector3.Cross(here, tangent).normalized;
        float angle = Random.Range(0f, Mathf.PI * 2f);
        float distance = Random.Range(minOffset, maxOffset);
        float planetR = Mathf.Max(1f, Vector3.Distance(origin, center));
        Vector3 axis = (here + (tangent * Mathf.Cos(angle) + other * Mathf.Sin(angle)) *
            (distance / planetR)).normalized;
        if (TryGetDrySurfacePoint(axis, out Vector3 point))
            return point;
        return origin;
    }

    bool TryGetDrySurfacePoint(Vector3 axis, out Vector3 point)
    {
        point = default;
        Planet planet = _simulation != null ? _simulation.planet : null;
        if (planet == null)
            return false;

        Vector3 center = planet.transform.position;
        PlanetOceanLayer ocean = planet.GetComponent<PlanetOceanLayer>();
        point = planet.GetSurfacePointWorld(axis);
        if (ocean != null && ocean.GetDepthBelowSurface(point) > 0f)
        {
            point = PlanetSurfaceSampler.GetDrySurfacePosition(
                axis,
                center,
                ZombieAI.ResolvePrimaryTerrainMeshCollider(planet.transform),
                planet,
                ocean,
                ~0,
                24,
                0.1f,
                planet.GetBaseRadiusWorld(),
                1.25f);
        }

        return ocean == null || ocean.GetDepthBelowSurface(point) <= 0f;
    }

    public static bool SurfaceSupportsResource(Planet planet, Vector3 position, FactionResourceType type)
    {
        Color key = planet.GetSurfaceKeyColorAtPosition(position);
        FootstepSurfaceKind surface = planet.GetFootstepSurface(position);
        if (type == FactionResourceType.Wood)
        {
            if (ColourGenerator.IsGreenKeyColor(key))
                return true;
            if (surface == FootstepSurfaceKind.Sand)
                return true;
            return surface == FootstepSurfaceKind.Snow && key.b >= key.r;
        }

        if (surface == FootstepSurfaceKind.Rock)
            return true;
        return surface == FootstepSurfaceKind.Snow && key.r > key.b;
    }

    /// <summary>
    /// Pins a discovered or scouted location so foliage keeps streaming there for this faction.
    /// Harvest finds are kept over random wander points when the list is full.
    /// </summary>
    public void RememberStreamFocus(Vector3 position, bool harvest, FactionResourceType resourceType)
    {
        float mergeSq = StreamFocusMergeDistance * StreamFocusMergeDistance;
        int existing = -1;
        for (int i = 0; i < _streamFoci.Count; i++)
        {
            if ((_streamFoci[i] - position).sqrMagnitude <= mergeSq)
            {
                existing = i;
                break;
            }
        }

        if (existing >= 0)
        {
            if (harvest)
            {
                _streamFoci[existing] = position;
                _streamFocusHarvest[existing] = true;
            }
            _streamFocusType[existing] = resourceType;
            _streamFocusUsed[existing] = Time.time;
            return;
        }

        if (_streamFoci.Count >= MaxStreamFoci)
        {
            int evict = FindStreamFocusToEvict(harvest, resourceType);
            if (evict < 0)
                return;
            _streamFoci[evict] = position;
            _streamFocusUsed[evict] = Time.time;
            _streamFocusHarvest[evict] = harvest;
            _streamFocusType[evict] = resourceType;
            return;
        }

        _streamFoci.Add(position);
        _streamFocusUsed.Add(Time.time);
        _streamFocusHarvest.Add(harvest);
        _streamFocusType.Add(resourceType);
    }

    int FindStreamFocusToEvict(bool incomingHarvest, FactionResourceType incomingType)
    {
        int stoneCount = 0;
        for (int i = 0; i < _streamFocusType.Count; i++)
        {
            if (_streamFocusType[i] == FactionResourceType.Stone)
                stoneCount++;
        }

        int bestScoutSame = -1;
        float oldestScoutSame = float.PositiveInfinity;
        int bestSame = -1;
        float oldestSame = float.PositiveInfinity;
        int bestWoodHarvest = -1;
        float oldestWoodHarvest = float.PositiveInfinity;
        for (int i = 0; i < _streamFoci.Count; i++)
        {
            bool same = i < _streamFocusType.Count && _streamFocusType[i] == incomingType;
            if (same && !_streamFocusHarvest[i] && _streamFocusUsed[i] < oldestScoutSame)
            {
                oldestScoutSame = _streamFocusUsed[i];
                bestScoutSame = i;
            }
            if (same && _streamFocusUsed[i] < oldestSame)
            {
                oldestSame = _streamFocusUsed[i];
                bestSame = i;
            }
            if (!same &&
                _streamFocusType[i] == FactionResourceType.Wood &&
                _streamFocusHarvest[i] &&
                _streamFocusUsed[i] < oldestWoodHarvest)
            {
                oldestWoodHarvest = _streamFocusUsed[i];
                bestWoodHarvest = i;
            }
        }

        if (bestScoutSame >= 0)
            return bestScoutSame;
        if (incomingHarvest && bestSame >= 0)
            return bestSame;
        if (incomingType == FactionResourceType.Stone && bestWoodHarvest >= 0)
            return bestWoodHarvest;
        if (incomingType == FactionResourceType.Wood && stoneCount <= 1)
            return -1;
        return incomingHarvest ? bestSame : -1;
    }

    public FactionStrengthBreakdown CalculateStrength()
    {
        FactionStrengthBreakdown result = default;
        result.livingWorkers = WorkerCount;
        result.livingSoldiers = SoldierCount;
        if (Stargrave.Rts2.Rts2UnitSim.HasInstance)
        {
            result.soldierContribution = SoldierCount * Warfare.soldierStrength;
        }
        else
        {
            for (int i = 0; i < _soldiers.Count; i++)
            {
                FactionNpc soldier = _soldiers[i];
                if (soldier == null || soldier.IsDead)
                    continue;
                result.soldierContribution += Warfare.soldierStrength *
                    Mathf.Clamp01((float)soldier.CurrentHealth / Mathf.Max(1, soldier.MaxHealth));
            }
        }
        result.workerContribution = result.livingWorkers * Warfare.workerStrength;
        result.buildingContribution = (TownHall != null ? Warfare.townHallStrength : 0f) +
                                      (Barracks != null ? Warfare.barracksStrength : 0f) +
                                      (Market != null ? Warfare.marketStrength : 0f) +
                                      (Mint != null ? Warfare.mintStrength : 0f);
        IReadOnlyList<ClaimableTown> towns = FactionRegistry.Towns;
        for (int i = 0; i < towns.Count; i++)
        {
            if (towns[i] != null && towns[i].Owner == this)
                result.buildingContribution += Warfare.claimableTownStrength;
        }
        result.resourceContribution = (Wood + Stone + Gold) * Warfare.resourceStrengthPerUnit;
        result.total = result.soldierContribution + result.workerContribution +
                       result.buildingContribution + result.resourceContribution;
        return result;
    }

    public FactionNpc FindNearestWorker(Vector3 position)
    {
        return FindNearestLiving(_workers, position);
    }

    public FactionNpc FindNearestSoldier(Vector3 position)
    {
        return FindNearestLiving(_soldiers, position);
    }

    void TickAttackRetarget()
    {
        if (IsActiveCombatant(CurrentRival))
            return;

        if (ShouldLaunchAttack(out FactionController next))
        {
            if (_failedWaves > 0)
                _failedWaves--;
            BeginAttack(next);
            return;
        }

        State = FactionState.BuildingArmy;
        EnsureMusterPlan(true);
        if (_simulation.verboseEvents)
            Debug.Log($"[FactionSimulation] {DisplayName} held to remuster after rival dropped out.", this);
    }

    bool ShouldLaunchAttack(out FactionController rival)
    {
        rival = null;
        if (State == FactionState.Retreating || State == FactionState.Recovering)
            return false;
        if (Time.time < _nextAttackAllowed)
            return false;
        if (SoldierCount < MinSoldiersToPropose)
            return false;

        EnsureMusterPlan(false);

        // Balance-of-power: pile onto the coalition target when one is active.
        if (FactionBalanceSystem.HasActiveCoalition &&
            !FactionBalanceSystem.IsCoalitionTarget(this))
        {
            FactionController tyrant = FactionBalanceSystem.CoalitionTarget;
            if (tyrant != null &&
                tyrant.WouldAcceptFight(this) &&
                WillingToEngage(tyrant) &&
                FactionTradeSystem.ShouldLaunchDespiteTrade(this, tyrant))
            {
                rival = tyrant;
                return true;
            }
        }

        rival = FindWillingRivalByDistance();
        if (rival == null)
            return false;
        if (!FactionTradeSystem.ShouldLaunchDespiteTrade(this, rival))
        {
            rival = null;
            return false;
        }
        return true;
    }

    public bool WouldAcceptFight(FactionController challenger)
    {
        if (challenger == null || challenger == this)
            return false;
        if (State == FactionState.Retreating || State == FactionState.Recovering)
            return false;
        if (TownHall == null || !TownHall.IsOperational)
            return false;
        if (Barracks == null)
            return false;
        if (SoldierCount < MinSoldiersToAccept)
            return false;

        bool dogpileOk = FactionBalanceSystem.IsCoalitionTarget(this) &&
                         FactionBalanceSystem.HasActiveCoalition &&
                         !FactionBalanceSystem.IsCoalitionTarget(challenger);

        if (CurrentRival != null && CurrentRival != challenger && IsActiveCombatant(CurrentRival) && !dogpileOk)
            return false;
        if ((State == FactionState.Attacking || State == FactionState.Fighting) &&
            CurrentRival != null &&
            CurrentRival != challenger &&
            !dogpileOk)
            return false;
        return true;
    }

    FactionController FindWillingRivalByDistance()
    {
        Vector3 home = GetSafePosition();
        IReadOnlyList<FactionController> factions = FactionRegistry.Factions;
        float lastSq = -1f;
        int lastIndex = -1;
        int remaining = factions.Count;
        for (int n = 0; n < remaining; n++)
        {
            FactionController best = null;
            float bestSq = float.PositiveInfinity;
            int bestIndex = int.MaxValue;
            for (int i = 0; i < factions.Count; i++)
            {
                FactionController candidate = factions[i];
                if (candidate == null || candidate == this)
                    continue;
                float d = (candidate.GetSafePosition() - home).sqrMagnitude;
                bool afterLast = d > lastSq + 0.01f ||
                                 (Mathf.Abs(d - lastSq) <= 0.01f && i > lastIndex);
                if (!afterLast)
                    continue;
                if (d < bestSq - 0.01f || (Mathf.Abs(d - bestSq) <= 0.01f && i < bestIndex))
                {
                    best = candidate;
                    bestSq = d;
                    bestIndex = i;
                }
            }

            if (best == null)
                return null;
            lastSq = bestSq;
            lastIndex = bestIndex;
            if (best.WouldAcceptFight(this) && WillingToEngage(best))
                return best;
        }

        return null;
    }

    bool WillingToEngage(FactionController rival)
    {
        if (rival == null)
            return false;

        // Temporary allies never fight each other while dogpiling the tyrant.
        if (FactionBalanceSystem.AreCoalitionAllies(this, rival))
            return false;

        if (GrowthModifier < 0.8f && Strength > rival.Strength * 1.25f)
            return false;

        // High-threat bullies are discouraged from picking on much weaker peers.
        float threat = FactionBalanceSystem.GetThreat(this);
        if (threat > 0.65f && Strength > rival.Strength * 1.4f &&
            !FactionBalanceSystem.IsCoalitionTarget(rival))
            return false;

        int ours = SoldierCount;
        int theirs = Mathf.Max(1, rival.SoldierCount);
        float ratio = ours / (float)theirs;
        bool mustered = ours >= _plannedMuster;
        bool waited = Time.time - _musterStartedAt >= Mathf.Max(1f, Warfare.musterPatienceSeconds);
        float needRatio = Warfare.attackIfOutnumberedFraction > 0f
            ? Warfare.attackIfOutnumberedFraction
            : 0.7f;
        bool notSuicide = ratio >= needRatio || (mustered && ratio >= needRatio * 0.75f);
        if (!notSuicide)
            return false;

        // Soft survival fairness band: refuse stomping a recovering / far-weaker rival.
        float maxRatio = 1f / Mathf.Max(0.35f, needRatio);
        if (rival.State == FactionState.Recovering || rival.TownHall == null)
            return false;
        if (ratio > maxRatio * (1f + Warfare.strengthBalanceBuffer) &&
            rival.SoldierCount >= Mathf.Max(1, Warfare.minSoldiersToAcceptFight) &&
            !FactionBalanceSystem.IsCoalitionTarget(rival))
            return false;

        if (mustered)
            return true;
        if (waited && ours >= MinSoldiersToPropose)
            return true;
        return ours >= theirs && ours >= MinSoldiersToPropose;
    }

    void EnsureMusterPlan(bool reset)
    {
        if (!reset && _plannedMuster > 0)
            return;

        int prefer = Warfare.minimumSoldiersForWar > 0 ? Warfare.minimumSoldiersForWar : 10;
        int extra = _failedWaves * Mathf.Max(0, Warfare.extraSoldiersAfterDefeat);
        int spice = Mathf.Abs(RuntimeIndex * 2 + _failedWaves + _approachWave) % 3;
        int min = MinSoldiersToPropose;
        int max = Mathf.Max(min, Warfare.maxMusterWaitSoldiers > 0 ? Warfare.maxMusterWaitSoldiers : prefer + 6);
        _plannedMuster = Mathf.Clamp(prefer + extra + spice, min, max);
        _musterStartedAt = Time.time;
    }

    static bool IsActiveCombatant(FactionController faction)
    {
        if (faction == null)
            return false;
        if (faction.State == FactionState.Retreating || faction.State == FactionState.Recovering)
            return false;
        if (faction.IsBarAssaultProtected)
            return false;
        return faction.TownHall != null && faction.TownHall.IsOperational;
    }

    int MinSoldiersToPropose =>
        Mathf.Max(1, Warfare.minSoldiersToPropose > 0 ? Warfare.minSoldiersToPropose : 6);

    int MinSoldiersToAccept =>
        Mathf.Max(1, Warfare.minSoldiersToAcceptFight > 0 ? Warfare.minSoldiersToAcceptFight : 5);

    void ClearRivalIfPaired()
    {
        FactionController rival = CurrentRival;
        CurrentRival = null;
        if (rival != null && rival.CurrentRival == this)
            rival.BindRival(null);
    }

    public bool TryGetPerceivedResource(FactionResourceType type, out ResourceNode node)
    {
        node = type == FactionResourceType.Stone ? _perceivedStone : _perceivedWood;
        if (node == null || !node.IsAvailable || !node.CanReserve(this))
        {
            node = null;
            return false;
        }
        return true;
    }

    public bool TryGetPerceivedThreat(
        Vector3 from,
        float radius,
        out FactionNpc npc,
        out ZombieAI zombie,
        out int nearby)
    {
        npc = null;
        zombie = null;
        nearby = 0;
        float radiusSq = radius * radius;
        float bestNpcSq = radiusSq;
        float bestZombieSq = radiusSq;
        for (int i = 0; i < _enemyThreatCount; i++)
        {
            FactionNpc threat = _enemyThreats[i];
            if (threat == null || threat.IsDead || !threat.IsFactionTargetable)
                continue;
            if (!FactionRegistry.IsCombatTarget(threat, this))
                continue;
            float d = (threat.transform.position - from).sqrMagnitude;
            if (d > radiusSq)
                continue;
            nearby++;
            if (d <= bestNpcSq)
            {
                bestNpcSq = d;
                npc = threat;
            }
        }

        for (int i = 0; i < _zombieThreatCount; i++)
        {
            ZombieAI threat = _zombieThreats[i];
            if (threat == null || threat.IsDead)
                continue;
            float d = (threat.transform.position - from).sqrMagnitude;
            if (d > radiusSq)
                continue;
            nearby++;
            if (d <= bestZombieSq)
            {
                bestZombieSq = d;
                zombie = threat;
            }
        }

        return nearby > 0;
    }

    public void CountPerceivedSoldiersNear(
        Vector3 position,
        float radius,
        out int friends,
        out int foes)
    {
        friends = 0;
        foes = 0;
        float radiusSq = radius * radius;
        for (int i = 0; i < _soldiers.Count; i++)
        {
            FactionNpc npc = _soldiers[i];
            if (npc == null || npc.IsDead)
                continue;
            if ((npc.transform.position - position).sqrMagnitude <= radiusSq)
                friends++;
        }

        for (int i = 0; i < _enemyThreatCount; i++)
        {
            FactionNpc npc = _enemyThreats[i];
            if (npc == null || npc.IsDead || !FactionNpcRoles.IsCombatSoldier(npc.Role))
                continue;
            if ((npc.transform.position - position).sqrMagnitude <= radiusSq)
                foes++;
        }
    }

    void RefreshResourcePerceptionSlice()
    {
        IReadOnlyList<ResourceNode> resources = FactionRegistry.Resources;
        int n = resources.Count;
        if (n <= 0)
            return;

        float search = Combat.workerFoliageRadius > 0f ? Combat.workerFoliageRadius : 140f;
        float searchSq = search * search;
        if (_resourceScanIndex == 0)
        {
            CollectGatherAnchors();
            _scanWood = null;
            _scanStone = null;
            _scanWoodSq = searchSq;
            _scanStoneSq = searchSq;
        }

        const int budget = 32;
        int end = Mathf.Min(n, _resourceScanIndex + budget);
        for (int i = _resourceScanIndex; i < end; i++)
        {
            ResourceNode node = resources[i];
            if (node == null || !node.CanReserve(this))
                continue;
            float d = MinDistToAnchors(node.transform.position, _gatherAnchors, _gatherAnchorCount);
            if (node.ResourceType == FactionResourceType.Stone)
            {
                if (d <= _scanStoneSq)
                {
                    _scanStoneSq = d;
                    _scanStone = node;
                }
            }
            else if (d <= _scanWoodSq)
            {
                _scanWoodSq = d;
                _scanWood = node;
            }
        }

        _resourceScanIndex = end;
        if (_resourceScanIndex < n)
            return;

        _resourceScanIndex = 0;
        _perceivedWood = _scanWood;
        _perceivedStone = _scanStone;
        if (_perceivedWood != null)
        {
            RememberResourceSite(FactionResourceType.Wood, _perceivedWood.transform.position);
            RememberStreamFocus(_perceivedWood.transform.position, true, FactionResourceType.Wood);
        }
        if (_perceivedStone != null)
        {
            RememberResourceSite(FactionResourceType.Stone, _perceivedStone.transform.position);
            RememberStreamFocus(_perceivedStone.transform.position, true, FactionResourceType.Stone);
        }
    }

    void CollectGatherAnchors()
    {
        int n = 0;
        _gatherAnchors[n++] = GetSafePosition();
        int wood = Mathf.Min(3, _woodSites.Count);
        for (int i = 0; i < wood && n < _gatherAnchors.Length; i++)
            _gatherAnchors[n++] = _woodSites[i];
        int stone = Mathf.Min(2, _stoneSites.Count);
        for (int i = 0; i < stone && n < _gatherAnchors.Length; i++)
            _gatherAnchors[n++] = _stoneSites[i];
        int workers = CountLiving(_workers);
        int step = Mathf.Max(1, workers / 4);
        int seen = 0;
        for (int i = 0; i < _workers.Count && n < _gatherAnchors.Length; i++)
        {
            FactionNpc worker = _workers[i];
            if (worker == null || worker.IsDead)
                continue;
            if ((seen++ % step) != 0 && seen > 1)
                continue;
            _gatherAnchors[n++] = worker.transform.position;
        }
        _gatherAnchorCount = n;
    }

    void CollectSenseAnchors()
    {
        int n = 0;
        _senseAnchors[n++] = GetSafePosition();
        if (HasDefensePing && n < _senseAnchors.Length)
            _senseAnchors[n++] = DefensePoint;
        if (HasBackupPing && n < _senseAnchors.Length)
            _senseAnchors[n++] = BackupPoint;
        if (Time.time - _hostileContactTime < 8f && n < _senseAnchors.Length)
            _senseAnchors[n++] = _hostileContact;
        int added = 0;
        for (int i = 0; i < _members.Count && n < _senseAnchors.Length && added < 4; i++)
        {
            FactionNpc npc = _members[i];
            if (npc == null || npc.IsDead)
                continue;
            if ((i & 1) != 0 && _members.Count > 4)
                continue;
            _senseAnchors[n++] = npc.transform.position;
            added++;
        }
        _senseAnchorCount = n;
    }

    static float MinDistToAnchors(Vector3 position, Vector3[] anchors, int count)
    {
        float best = float.PositiveInfinity;
        for (int i = 0; i < count; i++)
        {
            float d = (position - anchors[i]).sqrMagnitude;
            if (d < best)
                best = d;
        }
        return best;
    }

    void RefreshThreatPerception()
    {
        _enemyThreatCount = 0;
        _zombieThreatCount = 0;
        float sense = Combat.soldierDefendRadius > 0f ? Combat.soldierDefendRadius : 80f;
        float senseSq = sense * sense;
        IReadOnlyList<FactionNpc> npcs = FactionRegistry.Npcs;
        CollectSenseAnchors();
        for (int i = 0; i < npcs.Count; i++)
        {
            FactionNpc npc = npcs[i];
            if (npc == null || npc.Faction == this || npc.IsDead)
                continue;
            if (!FactionRegistry.IsCombatTarget(npc, this))
                continue;
            float d = MinDistToAnchors(npc.transform.position, _senseAnchors, _senseAnchorCount);
            if (d <= senseSq)
                InsertThreat(_enemyThreats, _enemyThreatDist, ref _enemyThreatCount, npc, d);
        }

        IReadOnlyList<ZombieAI> zombies = ZombieAI.Active;
        for (int i = 0; i < zombies.Count; i++)
        {
            ZombieAI zombie = zombies[i];
            if (zombie == null || zombie.IsDead)
                continue;
            float d = MinDistToAnchors(zombie.transform.position, _senseAnchors, _senseAnchorCount);
            if (d <= senseSq)
                InsertThreat(_zombieThreats, _zombieThreatDist, ref _zombieThreatCount, zombie, d);
        }

        if (State == FactionState.Retreating || State == FactionState.Recovering)
            return;
        if (_enemyThreatCount == 0 && _zombieThreatCount == 0)
            return;

        Vector3 home = GetSafePosition();
        if (!TryGetPerceivedThreat(home, sense, out FactionNpc homeEnemy, out ZombieAI homeZombie, out int nearBase) ||
            nearBase <= 0)
            return;

        Vector3 ping = homeEnemy != null
            ? homeEnemy.transform.position
            : homeZombie != null
                ? homeZombie.transform.position
                : home;
        RequestDefense(ping);
    }

    static void InsertThreat<T>(T[] items, float[] distances, ref int count, T item, float distSq)
        where T : class
    {
        if (count < items.Length)
        {
            items[count] = item;
            distances[count] = distSq;
            count++;
            return;
        }

        int worst = 0;
        for (int i = 1; i < count; i++)
        {
            if (distances[i] > distances[worst])
                worst = i;
        }
        if (distSq >= distances[worst])
            return;
        items[worst] = item;
        distances[worst] = distSq;
    }

    static FactionNpc FindNearestLiving(List<FactionNpc> list, Vector3 position)
    {
        FactionNpc best = null;
        float bestSq = float.PositiveInfinity;
        for (int i = 0; i < list.Count; i++)
        {
            FactionNpc npc = list[i];
            if (npc == null || npc.IsDead)
                continue;
            float d = (npc.transform.position - position).sqrMagnitude;
            if (d < bestSq)
            {
                bestSq = d;
                best = npc;
            }
        }
        return best;
    }

    void PruneMembers()
    {
        for (int i = _members.Count - 1; i >= 0; i--)
        {
            if (_members[i] == null)
                _members.RemoveAt(i);
        }
        for (int i = _workers.Count - 1; i >= 0; i--)
        {
            if (_workers[i] == null)
                _workers.RemoveAt(i);
        }
        for (int i = _soldiers.Count - 1; i >= 0; i--)
        {
            if (_soldiers[i] == null)
                _soldiers.RemoveAt(i);
        }
        for (int i = _merchants.Count - 1; i >= 0; i--)
        {
            if (_merchants[i] == null)
                _merchants.RemoveAt(i);
        }
        for (int i = _nobles.Count - 1; i >= 0; i--)
        {
            if (_nobles[i] == null)
                _nobles.RemoveAt(i);
        }
    }

    static int CountLiving(List<FactionNpc> list)
    {
        int count = 0;
        for (int i = 0; i < list.Count; i++)
            if (list[i] != null && !list[i].IsDead)
                count++;
        return count;
    }

    int CountLivingRole(FactionNpcRole role)
    {
        int count = 0;
        for (int i = 0; i < _members.Count; i++)
        {
            FactionNpc npc = _members[i];
            if (npc != null && !npc.IsDead && npc.Role == role)
                count++;
        }
        return count;
    }
}
