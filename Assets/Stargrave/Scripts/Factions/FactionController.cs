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
    readonly List<Vector3> _streamFoci = new List<Vector3>(8);
    readonly List<float> _streamFocusUsed = new List<float>(8);
    readonly List<bool> _streamFocusHarvest = new List<bool>(8);
    readonly List<Vector3> _woodSites = new List<Vector3>(8);
    readonly List<float> _woodSiteUsed = new List<float>(8);
    readonly List<Vector3> _stoneSites = new List<Vector3>(8);
    readonly List<float> _stoneSiteUsed = new List<float>(8);
    const int MaxStreamFoci = 6;
    const int MaxResourceSites = 8;
    const float StreamFocusMergeDistance = 45f;
    const float ResourceSiteMergeDistance = 45f;
    FactionStateMachine _stateMachine;

    public int RuntimeIndex { get; private set; }
    public string RuntimeId { get; private set; }
    public string DisplayName => !string.IsNullOrWhiteSpace(_fallbackName)
        ? _fallbackName
        : (_definition != null && !string.IsNullOrWhiteSpace(_definition.displayName)
            ? _definition.displayName
            : $"Faction {RuntimeIndex + 1}");
    public Color UiColor => WeaponCatalog.ProjectileColorForIndex(RuntimeIndex);
    public FactionSimulation Simulation => _simulation;
    public Vector3 SpawnAxis { get; private set; }
    public Transform BaseOrigin { get; private set; }
    public FactionState State { get; private set; } = FactionState.Economy;
    public TownHall TownHall { get; private set; }
    public Barracks Barracks { get; private set; }
    public BuildingConstructionSite ActiveConstruction { get; private set; }
    Vector3 _townHallAxis;
    Vector3 _barracksAxis;
    bool _townHallBuiltOnce;
    bool _barracksBuiltOnce;
    public int Wood { get; private set; }
    public int Stone { get; private set; }
    public int WoodGathered { get; private set; }
    public int StoneGathered { get; private set; }
    public int ReservedWood { get; private set; }
    public int ReservedStone { get; private set; }
    public int WorkerCount => CountLiving(_workers);
    public IReadOnlyList<FactionNpc> Workers => _workers;
    public int SoldierCount => CountLiving(_soldiers);
    public IReadOnlyList<FactionNpc> Members => _members;
    public float Strength => CalculateStrength().total;
    public float GrowthModifier => _simulation != null ? _simulation.GetGrowthModifier(this) : 1f;
    public FactionController CurrentRival { get; private set; }
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
    Vector3 _hostileContact;
    float _hostileContactTime;
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
        _fallbackName = string.IsNullOrWhiteSpace(fallbackName)
            ? $"Faction {runtimeIndex + 1}"
            : fallbackName;
        SpawnAxis = spawnAxis.sqrMagnitude > 1e-6f ? spawnAxis.normalized : Vector3.up;
        Wood = Mathf.Max(0, Economy.startingWood);
        Stone = Mathf.Max(0, Economy.startingStone);
        name = DisplayName;
        _stateMachine = gameObject.AddComponent<FactionStateMachine>();
        _stateMachine.Initialize(this);
        EnsureBaseArea();
    }

    void EnsureBaseArea()
    {
        if (BaseOrigin != null || _simulation == null || _simulation.planet == null)
            return;

        Planet planet = _simulation.planet;
        Vector3 axis = SpawnAxis.sqrMagnitude > 1e-6f ? SpawnAxis.normalized : Vector3.up;
        const float dryClearance = 1.25f;
        float waterLine = BuildingPadSiteEvaluator.ResolveWaterLine(planet, dryClearance);
        bool aboveWater = planet.GetSurfaceRadiusWorld(axis) >= waterLine;
        if (!aboveWater || !BuildingPlacementSystem.IsDryMainlandCampus(planet, axis))
        {
            if (!_simulation.TryFindMainlandSpawnAxis(axis, out Vector3 relocated))
            {
                Debug.LogError($"[FactionSimulation] {DisplayName} could not place a mainland dry base.", this);
                return;
            }

            axis = relocated;
            SpawnAxis = axis;
        }

        if (planet.GetSurfaceRadiusWorld(axis) < waterLine)
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

    public void SpawnInitialWorkers()
    {
        int spawnCount = Mathf.Min(Economy.startingWorkers, Economy.workerMaxCount);
        for (int i = 0; i < spawnCount; i++)
        {
            Vector3 axis = SpawnAxis;
            if (i > 0)
            {
                Vector3 tangent = Vector3.Cross(axis, Mathf.Abs(Vector3.Dot(axis, Vector3.up)) > 0.9f
                    ? Vector3.right
                    : Vector3.up).normalized;
                axis = (axis + tangent * ((i % 3) - 1) * 0.0025f).normalized;
            }

            FactionNpc worker = FactionNpc.CreateResourceGatherer(this, axis);
            if (worker != null)
                RegisterNpc(worker);
        }

        TryStartNextConstruction();
    }

    public void SimulationTick(float deltaTime)
    {
        PruneMembers();
        TickDefenseExpiry();
        if (HasReachedEngagementStrength == false &&
            Strength >= Warfare.minimumEngagementStrength)
        {
            HasReachedEngagementStrength = true;
            if (_simulation.verboseEvents)
                Debug.Log($"[FactionSimulation] {DisplayName} reached engagement strength.", this);
        }

        if (State == FactionState.Economy && TownHall != null && Barracks == null)
            State = FactionState.BuildingArmy;

        if ((State == FactionState.Economy || State == FactionState.BuildingArmy) && NeedsRecoup())
            BeginRecovery();

        if (State == FactionState.BuildingArmy && Barracks != null)
        {
            if (_plannedMuster <= 0)
                EnsureMusterPlan(true);
            if (FactionRegistry.WarfareEnabled && ShouldLaunchAttack(out FactionController rival))
                BeginAttack(rival);
        }

        if (State == FactionState.Attacking || State == FactionState.Fighting)
            TickAttackRetarget();

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
            for (int i = 0; i < _soldiers.Count; i++)
            {
                if (_soldiers[i] != null && _soldiers[i].Soldier != null &&
                    _soldiers[i].Soldier.IsSafeAtBase)
                    safe++;
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
        }

        if (ActiveConstruction == null)
            TryStartNextConstruction();
        FeedConstructionFromStock();

        if (Barracks != null && Barracks.IsOperational)
            Barracks.TickProduction(deltaTime * GrowthModifier);

        for (int i = 0; i < _members.Count; i++)
        {
            if (_members[i] != null)
                _members[i].SimulationTick(deltaTime);
        }
    }

    void TryStartNextConstruction()
    {
        if (TownHall == null)
        {
            Vector3 hallAxis = ResolveHallAxis();
            BuildingConstructionSite site;
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
                    out site, 8, 0f, true))
            {
                BeginConstruction(site);
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
                    this, _simulation.barracksPrefab, Economy.barracksMinDistance,
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
    }

    void BeginConstruction(BuildingConstructionSite site)
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
        else
            _soldiers.Add(npc);
    }

    public void UnregisterNpc(FactionNpc npc)
    {
        _members.Remove(npc);
        _workers.Remove(npc);
        _soldiers.Remove(npc);
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
        if (TownHall == building)
            TownHall = null;
        if (Barracks == building)
            Barracks = null;
        if (_simulation.verboseEvents)
            Debug.Log($"[FactionSimulation] {DisplayName} lost {building.Kind}; rebuilding is enabled.", this);
        if (State != FactionState.Retreating)
            BeginRecovery();
    }

    public void ClearActiveConstruction(BuildingConstructionSite site)
    {
        if (ActiveConstruction == site)
            ActiveConstruction = null;
    }

    public void BeginAttack(FactionController rival)
    {
        CurrentRival = rival;
        if (rival != null && (rival.CurrentRival == null || rival.CurrentRival == this))
            rival.BindRival(this);
        PreviousAttackStrength = SoldierCount * Warfare.soldierStrength;
        PreviousAttackSoldiers = SoldierCount;
        _approachWave++;
        _plannedMuster = 0;
        State = FactionState.Attacking;
        if (_simulation.verboseEvents)
            Debug.Log(
                $"[FactionSimulation] {DisplayName} attack started on {(rival != null ? rival.DisplayName : "none")} " +
                $"with {PreviousAttackSoldiers} soldiers (approach {_approachWave}).",
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
        if (_simulation.verboseEvents)
            Debug.Log($"[FactionSimulation] {DisplayName} recovery started.", this);
    }

    public bool NeedsRecoup()
    {
        if (TownHall == null || !TownHall.IsOperational)
            return true;
        if (Barracks == null || !Barracks.IsOperational)
            return true;
        return WorkerCount < Economy.startingWorkers;
    }

    public bool CanEndRecovery()
    {
        if (TownHall == null || !TownHall.IsOperational)
            return false;
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

    public bool TryPickSoldierDutyPoint(FactionNpc soldier, out Vector3 destination)
    {
        destination = GetSafePosition();
        float patrol = Combat.soldierPatrolRadius > 0f ? Combat.soldierPatrolRadius : 32f;
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
    public void RememberStreamFocus(Vector3 position, bool harvest)
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
            _streamFocusUsed[existing] = Time.time;
            return;
        }

        if (_streamFoci.Count >= MaxStreamFoci)
        {
            int evict = FindStreamFocusToEvict(harvest);
            if (evict < 0)
                return;
            _streamFoci[evict] = position;
            _streamFocusUsed[evict] = Time.time;
            _streamFocusHarvest[evict] = harvest;
            return;
        }

        _streamFoci.Add(position);
        _streamFocusUsed.Add(Time.time);
        _streamFocusHarvest.Add(harvest);
    }

    int FindStreamFocusToEvict(bool incomingHarvest)
    {
        int bestScout = -1;
        float oldestScout = float.PositiveInfinity;
        int bestAny = -1;
        float oldestAny = float.PositiveInfinity;
        for (int i = 0; i < _streamFoci.Count; i++)
        {
            if (_streamFocusUsed[i] < oldestAny)
            {
                oldestAny = _streamFocusUsed[i];
                bestAny = i;
            }
            if (_streamFocusHarvest[i])
                continue;
            if (_streamFocusUsed[i] < oldestScout)
            {
                oldestScout = _streamFocusUsed[i];
                bestScout = i;
            }
        }

        if (bestScout >= 0)
            return bestScout;
        return incomingHarvest ? bestAny : -1;
    }

    public FactionStrengthBreakdown CalculateStrength()
    {
        FactionStrengthBreakdown result = default;
        result.livingWorkers = WorkerCount;
        result.livingSoldiers = SoldierCount;
        for (int i = 0; i < _soldiers.Count; i++)
        {
            FactionNpc soldier = _soldiers[i];
            if (soldier == null || soldier.IsDead)
                continue;
            result.soldierContribution += Warfare.soldierStrength *
                Mathf.Clamp01((float)soldier.CurrentHealth / Mathf.Max(1, soldier.MaxHealth));
        }
        result.workerContribution = result.livingWorkers * Warfare.workerStrength;
        result.buildingContribution = (TownHall != null ? Warfare.townHallStrength : 0f) +
                                      (Barracks != null ? Warfare.barracksStrength : 0f);
        result.resourceContribution = (Wood + Stone) * Warfare.resourceStrengthPerUnit;
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
        rival = FindWillingRivalByDistance();
        return rival != null;
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
        if (CurrentRival != null && CurrentRival != challenger && IsActiveCombatant(CurrentRival))
            return false;
        if ((State == FactionState.Attacking || State == FactionState.Fighting) &&
            CurrentRival != null &&
            CurrentRival != challenger)
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
        if (GrowthModifier < 0.8f && Strength > rival.Strength * 1.25f)
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
    }

    static int CountLiving(List<FactionNpc> list)
    {
        int count = 0;
        for (int i = 0; i < list.Count; i++)
            if (list[i] != null && !list[i].IsDead)
                count++;
        return count;
    }
}
