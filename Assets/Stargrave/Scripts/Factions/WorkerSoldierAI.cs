using System.Collections.Generic;
using UnityEngine;

/// <summary>Worker. Harvests first; fights or flees hostiles and pings allied soldiers.</summary>
public sealed class WorkerAI : MonoBehaviour
{
    enum WorkState
    {
        LookingForResource,
        GoingToResource,
        Gathering,
        GoingToDeposit,
        Building,
        FightingHostile,
        Fleeing,
        Wandering
    }

    FactionNpc _npc;
    WorkerInventory _inventory;
    ResourceNode _resource;
    ZombieAI _zombieTarget;
    FactionNpc _npcTarget;
    WorkState _state;
    FactionResourceType _gatherTask;
    bool _hasGatherTask;
    float _nextSearch;
    float _gatherAccumulator;
    float _nextAttack;
    Vector3 _prospect;
    bool _hasProspect;
    int _emptySearches;
    readonly List<Vector3> _skippedProspects = new List<Vector3>(16);
    const int EmptySearchesBeforeHop = 4;

    float _lodAccum;

    public bool HasGatherTask => _hasGatherTask;
    public FactionResourceType GatherTask => _gatherTask;

    public string DebugStatus
    {
        get
        {
            string job = _hasGatherTask ? _gatherTask.ToString() : "none";
            string carry = _inventory == null || _inventory.IsEmpty
                ? "empty"
                : $"{_inventory.Amount} {_inventory.ResourceType}";
            return $"{_state} {job} {carry}";
        }
    }

    public void AcceptGatherTask(FactionResourceType type)
    {
        if (_gatherTask != type)
        {
            _hasProspect = false;
            _emptySearches = 0;
            _skippedProspects.Clear();
        }
        _gatherTask = type;
        _hasGatherTask = true;
    }

    public void ClearGatherTask()
    {
        _hasGatherTask = false;
    }

    void Awake()
    {
        _npc = GetComponent<FactionNpc>();
        _inventory = GetComponent<WorkerInventory>();
    }

    void Update()
    {
        if (FactionNpcLod.IsFar(transform.position))
        {
            _lodAccum += Time.deltaTime;
            if (_lodAccum < FactionNpcLod.AiInterval)
                return;
            Tick(_lodAccum);
            _lodAccum = 0f;
            return;
        }

        _lodAccum = 0f;
        Tick(Time.deltaTime);
    }

    public void Tick(float deltaTime)
    {
        if (_npc == null || _npc.Faction == null || _npc.IsDead)
            return;
        if (_inventory == null)
            _inventory = GetComponent<WorkerInventory>();
        if (_inventory != null)
            _inventory.Configure(_npc.Faction.Economy.workerCarryCapacity);

        if (HandleHostiles())
            return;

        if (_state == WorkState.Building)
        {
            TickBuild();
            return;
        }

        if ((_inventory == null || _inventory.IsEmpty) && TryJoinBuild())
            return;

        if (_inventory != null && _inventory.IsFull)
        {
            if (_state != WorkState.GoingToDeposit)
                GoToDeposit();
            if (_npc.Motor.IsStopped)
                Deposit();
            return;
        }

        if (_state == WorkState.GoingToDeposit)
        {
            if (_npc.Motor.IsStopped)
                Deposit();
            else
                _npc.Motor.SetDestination(GetBasePosition(), _npc.Faction.Combat.workerMoveSpeed, 2.5f);
            return;
        }

        if (_state == WorkState.GoingToResource)
        {
            if (_resource == null || !_resource.IsAvailable)
            {
                ReleaseResource();
                _state = WorkState.LookingForResource;
            }
            else if (_npc.Motor.IsStopped)
            {
                _state = WorkState.Gathering;
            }
            else
            {
                _npc.Motor.SetDestination(_resource.transform.position, _npc.Faction.Combat.workerMoveSpeed, 2f);
            }

            if (_state != WorkState.Gathering)
                return;
        }

        if (_state == WorkState.Gathering)
        {
            Gather(deltaTime);
            return;
        }

        if (_state == WorkState.Wandering && Time.time >= _nextSearch)
        {
            _nextSearch = Time.time + _npc.Faction.Economy.workerDecisionInterval;
            if (TryClaimNearbyResource())
            {
                _emptySearches = 0;
                return;
            }
            if (!_npc.Motor.IsStopped)
                return;
            _emptySearches++;
            BeginWander();
            return;
        }

        if (Time.time < _nextSearch && _state != WorkState.LookingForResource)
            return;
        _nextSearch = Time.time + _npc.Faction.Economy.workerDecisionInterval;

        if (TryClaimNearbyResource())
        {
            _emptySearches = 0;
            return;
        }
        BeginWander();
    }

    bool HandleHostiles()
    {
        FactionCombatSettings combat = _npc.Faction.Combat;
        float radius = combat.workerFightRadius;
        int zombies = ZombieAwareness.CountThreats(transform.position, radius);
        int enemies = FactionRegistry.WarfareEnabled
            ? FactionRegistry.CountEnemyNpcs(transform.position, radius, _npc.Faction)
            : 0;
        int nearby = zombies + enemies;
        ZombieAwareness.TryFindThreat(transform.position, radius, out ZombieAI nearestZombie);
        FactionNpc nearestEnemy = FactionRegistry.WarfareEnabled
            ? FactionRegistry.FindNearestNpc(transform.position, radius, _npc.Faction)
            : null;

        if (nearby <= 0)
        {
            if (_state == WorkState.FightingHostile)
            {
                _zombieTarget = null;
                _npcTarget = null;
                _state = _inventory != null && _inventory.IsFull
                    ? WorkState.GoingToDeposit
                    : WorkState.LookingForResource;
            }
            return _state == WorkState.Fleeing && ResumeAfterFlee();
        }

        _npc.Faction.RequestDefense(transform.position);
        float health = (float)_npc.CurrentHealth / Mathf.Max(1, _npc.MaxHealth);
        bool shouldFlee = nearby >= combat.workerFleeZombieCount || health <= combat.workerFleeHealth;
        if (shouldFlee)
        {
            _zombieTarget = null;
            _npcTarget = null;
            if (_state != WorkState.Fleeing)
            {
                ReleaseResource();
                LeaveBuild();
                _state = WorkState.Fleeing;
            }
            _npc.Motor.SetDestination(GetBasePosition(), combat.workerMoveSpeed, 3f);
            return true;
        }

        LeaveBuild();
        _state = WorkState.FightingHostile;
        bool useNpc = nearestEnemy != null &&
                      (nearestZombie == null ||
                       (nearestEnemy.transform.position - transform.position).sqrMagnitude <=
                       (nearestZombie.transform.position - transform.position).sqrMagnitude);
        if (useNpc)
        {
            _npcTarget = nearestEnemy;
            _zombieTarget = null;
            _npc.Motor.SetDestination(
                nearestEnemy.transform.position,
                combat.workerMoveSpeed,
                combat.workerAttackRange);
            if (_npc.Motor.IsStopped && Time.time >= _nextAttack)
            {
                _nextAttack = Time.time + combat.workerAttackCooldown;
                nearestEnemy.TakeFactionDamage(combat.workerDamage, transform);
            }
        }
        else if (nearestZombie != null)
        {
            _zombieTarget = nearestZombie;
            _npcTarget = null;
            _npc.Motor.SetDestination(
                nearestZombie.transform.position,
                combat.workerMoveSpeed,
                combat.workerAttackRange);
            if (_npc.Motor.IsStopped && Time.time >= _nextAttack)
            {
                _nextAttack = Time.time + combat.workerAttackCooldown;
                nearestZombie.TakeDamageFromNpc(combat.workerDamage, transform);
            }
        }
        return true;
    }

    bool ResumeAfterFlee()
    {
        if ((transform.position - GetBasePosition()).sqrMagnitude < 36f)
        {
            _state = _inventory != null && !_inventory.IsEmpty
                ? WorkState.GoingToDeposit
                : WorkState.LookingForResource;
            return false;
        }

        _npc.Motor.SetDestination(GetBasePosition(), _npc.Faction.Combat.workerMoveSpeed, 3f);
        return true;
    }

    bool TryClaimNearbyResource()
    {
        if (!_hasGatherTask)
            _npc.Faction.AssignGatherTask(_npc);

        float radius = _npc.Faction.Combat.workerResourceSearchRadius;
        ResourceNode nearby = FactionRegistry.FindNearestAvailableResource(
            transform.position, _gatherTask, _npc.Faction, radius);
        if (nearby != null)
            _npc.Faction.RememberResourceSite(_gatherTask, nearby.transform.position);

        ResourceNode node = nearby;
        if (node == null &&
            _npc.Faction.TryGetNearestResourceSite(_gatherTask, transform.position, out Vector3 knownSite))
        {
            node = FactionRegistry.FindNearestAvailableResource(
                knownSite, _gatherTask, _npc.Faction, radius);
            if (node != null)
                _npc.Faction.RememberResourceSite(_gatherTask, node.transform.position);
        }
        if (node == null || !node.Reserve(_npc))
            return false;

        _resource = node;
        _state = WorkState.GoingToResource;
        _npc.Motor.SetDestination(node.transform.position, _npc.Faction.Combat.workerMoveSpeed, 2f);
        _npc.Faction.RememberResourceSite(_gatherTask, node.transform.position);
        _npc.Faction.RememberStreamFocus(node.transform.position, true, _gatherTask);
        return true;
    }

    void BeginWander()
    {
        if (!_hasGatherTask)
            _npc.Faction.AssignGatherTask(_npc);

        Vector3 destination;
        if (_npc.Faction.TryGetNearestResourceSite(_gatherTask, transform.position, out Vector3 known) &&
            _emptySearches < EmptySearchesBeforeHop)
        {
            destination = _npc.Faction.JitterAround(known, 15f, 40f);
        }
        else
        {
            if (_emptySearches >= EmptySearchesBeforeHop)
            {
                if (_hasProspect)
                    _skippedProspects.Add(_prospect);
                _hasProspect = false;
                _emptySearches = 0;
            }

            if (!_hasProspect)
            {
                FactionSimulation sim = _npc.Faction.Simulation;
                Vector3 origin = _npc.Faction.GetSafePosition();
                if (sim == null ||
                    !sim.TryGetNextProspect(_gatherTask, origin, _skippedProspects, out _prospect))
                {
                    _skippedProspects.Clear();
                    if (sim == null ||
                        !sim.TryGetNextProspect(_gatherTask, origin, _skippedProspects, out _prospect))
                        return;
                }
                _hasProspect = true;
            }

            destination = _npc.Faction.JitterAround(_prospect, 15f, 40f);
        }

        _state = WorkState.Wandering;
        _npc.Motor.SetDestination(destination, _npc.Faction.Combat.workerMoveSpeed, 2f);
        _npc.Faction.RememberStreamFocus(destination, true, _gatherTask);
        if (_hasProspect)
            _npc.Faction.RememberStreamFocus(_prospect, true, _gatherTask);
    }

    void Gather(float deltaTime)
    {
        if (_inventory != null && _inventory.IsFull)
        {
            GoToDeposit();
            return;
        }

        if (_resource == null || !_resource.IsAvailable)
        {
            ReleaseResource();
            if (_inventory != null && _inventory.IsFull)
                GoToDeposit();
            else
                _state = WorkState.LookingForResource;
            return;
        }

        _npc.Motor.Stop();
        _gatherAccumulator += deltaTime * _npc.Faction.Economy.gatherPerSecond *
                              _npc.Faction.GrowthModifier;
        int request = Mathf.FloorToInt(_gatherAccumulator);
        if (request <= 0)
            return;
        _gatherAccumulator -= request;

        int room = _inventory != null
            ? _inventory.Capacity - _inventory.Amount
            : 0;
        int added = _resource.Gather(_npc, Mathf.Min(request, room));
        if (added > 0)
            _inventory.Add(_resource.ResourceType, added);

        if (_inventory != null && _inventory.IsFull)
        {
            ReleaseResource();
            GoToDeposit();
            return;
        }

        if (_resource == null || !_resource.IsAvailable)
        {
            ReleaseResource();
            _state = WorkState.LookingForResource;
        }
    }

    void GoToDeposit()
    {
        ReleaseResource();
        _state = WorkState.GoingToDeposit;
        _npc.Motor.SetDestination(GetBasePosition(), _npc.Faction.Combat.workerMoveSpeed, 2.5f);
    }

    void Deposit()
    {
        if (_inventory != null && !_inventory.IsEmpty)
        {
            int amount = _inventory.TakeAll(out FactionResourceType type);
            _npc.Faction.DepositResource(type, amount);
        }
        if (TryJoinBuild())
            return;
        _hasGatherTask = false;
        _npc.Faction.AssignGatherTask(_npc);
        _state = WorkState.LookingForResource;
        _nextSearch = 0f;
    }

    bool TryJoinBuild()
    {
        BuildingConstructionSite site = _npc.Faction.ActiveConstruction;
        if (site == null || site.IsComplete || !site.MaterialsDelivered)
            return false;
        if (!site.IsBuilder(_npc) && !site.TryReserveBuilder(_npc))
            return false;

        ReleaseResource();
        _state = WorkState.Building;
        _npc.Motor.SetDestination(site.GetBuildPosition(_npc), _npc.Faction.Combat.workerMoveSpeed, 2f);
        return true;
    }

    void TickBuild()
    {
        BuildingConstructionSite site = _npc.Faction.ActiveConstruction;
        if (site == null || site.IsComplete)
        {
            LeaveBuild();
            _state = WorkState.LookingForResource;
            _nextSearch = 0f;
            return;
        }

        if (!site.IsBuilder(_npc) && !site.TryReserveBuilder(_npc))
        {
            _state = WorkState.LookingForResource;
            return;
        }

        _npc.Motor.SetDestination(site.GetBuildPosition(_npc), _npc.Faction.Combat.workerMoveSpeed, 2f);
    }

    void LeaveBuild()
    {
        BuildingConstructionSite site = _npc != null && _npc.Faction != null
            ? _npc.Faction.ActiveConstruction
            : null;
        if (site != null)
            site.ReleaseBuilder(_npc);
    }

    Vector3 GetBasePosition()
    {
        if (_npc.Faction.ActiveConstruction != null &&
            !_npc.Faction.ActiveConstruction.IsComplete)
            return _npc.Faction.ActiveConstruction.transform.position;
        return _npc.Faction.GetSafePosition();
    }

    void ReleaseResource()
    {
        if (_resource == null)
            return;
        _resource.Release(_npc);
        _resource = null;
    }
}

public sealed class SoldierAI : MonoBehaviour
{
    FactionNpc _npc;
    IFactionDamageable _factionTarget;
    ZombieAI _zombieTarget;
    float _nextDecision;
    float _nextAttack;
    float _nextSkirmish;
    bool _safe;
    bool _hasDuty;
    bool _lastShotHit;
    float _lodAccum;

    public bool IsSafeAtBase => _safe;
    public bool IsInCombat =>
        (_zombieTarget != null && !_zombieTarget.IsDead) ||
        (_factionTarget != null && _factionTarget.IsFactionTargetable);

    void Awake()
    {
        _npc = GetComponent<FactionNpc>();
    }

    void Update()
    {
        if (FactionNpcLod.IsFar(transform.position))
        {
            _lodAccum += Time.deltaTime;
            if (_lodAccum < FactionNpcLod.AiInterval)
                return;
            Tick(_lodAccum);
            _lodAccum = 0f;
            return;
        }

        _lodAccum = 0f;
        Tick(Time.deltaTime);
    }

    public void Tick(float deltaTime)
    {
        if (_npc == null || _npc.Faction == null || _npc.IsDead)
            return;

        if (_npc.Faction.State == FactionState.Retreating ||
            _npc.Faction.State == FactionState.Recovering ||
            CurrentHealthFraction() <= _npc.Faction.Combat.soldierRetreatHealth)
        {
            _factionTarget = null;
            _hasDuty = false;
            if (ZombieAwareness.TryFindThreat(
                    transform.position,
                    _npc.Faction.Combat.zombieThreatRadius,
                    out ZombieAI homeZombie) &&
                homeZombie != null &&
                !homeZombie.IsDead)
            {
                _zombieTarget = homeZombie;
                EngageZombie(deltaTime);
                _safe = (transform.position - _npc.Faction.GetSafePosition()).sqrMagnitude < 64f;
                return;
            }

            _zombieTarget = null;
            _npc.Motor.SetDestination(_npc.Faction.GetSafePosition(), _npc.Faction.Combat.soldierMoveSpeed, 4f);
            _safe = (transform.position - _npc.Faction.GetSafePosition()).sqrMagnitude < 64f;
            if (_safe && _npc.Faction.State == FactionState.Retreating)
                _npc.Faction.BeginRecovery();
            return;
        }

        _safe = false;
        if (Time.time >= _nextDecision)
        {
            _nextDecision = Time.time + _npc.Faction.Economy.combatDecisionInterval;
            SelectTarget();
        }

        if (_zombieTarget != null && !_zombieTarget.IsDead)
        {
            _hasDuty = false;
            EngageZombie(deltaTime);
            return;
        }
        if (_factionTarget != null && _factionTarget.IsFactionTargetable)
        {
            _hasDuty = false;
            EngageFaction(deltaTime);
            return;
        }

        if (_npc.Faction.HasBackupPing && _npc.Faction.ShouldAnswerBackup(_npc))
        {
            _hasDuty = false;
            _npc.Motor.SetDestination(
                _npc.Faction.BackupPoint,
                _npc.Faction.Combat.soldierMoveSpeed,
                4f);
            return;
        }

        if (_npc.Faction.HasDefensePing)
        {
            _hasDuty = false;
            _npc.Motor.SetDestination(
                _npc.Faction.DefensePoint,
                _npc.Faction.Combat.soldierMoveSpeed,
                4f);
            return;
        }

        FollowDuty();
    }

    void FollowDuty()
    {
        if (_hasDuty && !_npc.Motor.IsStopped)
            return;

        if (!_npc.Faction.TryPickSoldierDutyPoint(_npc, out Vector3 destination))
            return;
        _hasDuty = true;
        _npc.Motor.SetDestination(destination, _npc.Faction.Combat.soldierMoveSpeed, 3f);
    }

    void SelectTarget()
    {
        _zombieTarget = null;
        _factionTarget = null;

        float engage = _npc.Faction.Combat.soldierEngageRadius > 0f
            ? _npc.Faction.Combat.soldierEngageRadius
            : 40f;
        bool ceasefire = _npc.Faction.State == FactionState.Recovering ||
                         _npc.Faction.State == FactionState.Retreating;
        if (FactionRegistry.WarfareEnabled && !ceasefire)
        {
            FactionNpc nearbyEnemy = FactionRegistry.FindNearestNpc(transform.position, engage, _npc.Faction);
            if (nearbyEnemy != null)
            {
                _factionTarget = nearbyEnemy;
                _npc.Faction.RememberHostileContact(nearbyEnemy.transform.position);
                return;
            }
        }

        if (ZombieAwareness.TryFindThreat(transform.position, _npc.Faction.Combat.zombieThreatRadius, out ZombieAI zombie))
        {
            _zombieTarget = zombie;
            return;
        }

        if (_npc.Faction.HasDefensePing)
        {
            Vector3 ping = _npc.Faction.DefensePoint;
            float defend = _npc.Faction.Combat.soldierDefendRadius;
            if (FactionRegistry.WarfareEnabled)
            {
                FactionNpc enemy = FactionRegistry.FindNearestNpc(ping, defend, _npc.Faction);
                if (enemy != null)
                {
                    _factionTarget = enemy;
                    _npc.Faction.RememberHostileContact(enemy.transform.position);
                    return;
                }
            }
            if (ZombieAwareness.TryFindThreat(ping, defend, out ZombieAI pingZombie))
            {
                _zombieTarget = pingZombie;
                return;
            }
            _npc.Faction.ClearDefense();
        }

        if (_npc.Faction.HasBackupPing)
        {
            Vector3 ping = _npc.Faction.BackupPoint;
            float defend = _npc.Faction.Combat.soldierEngageRadius > 0f
                ? _npc.Faction.Combat.soldierEngageRadius
                : 40f;
            if (FactionRegistry.WarfareEnabled)
            {
                FactionNpc enemy = FactionRegistry.FindNearestNpc(ping, defend, _npc.Faction);
                if (enemy != null)
                {
                    _factionTarget = enemy;
                    _npc.Faction.RememberHostileContact(enemy.transform.position);
                    return;
                }
            }
            if (ZombieAwareness.TryFindThreat(ping, defend, out ZombieAI backupZombie))
            {
                _zombieTarget = backupZombie;
                return;
            }
        }

        bool attacking = _npc.Faction.State == FactionState.Attacking ||
                         _npc.Faction.State == FactionState.Fighting;
        if (!FactionRegistry.WarfareEnabled || !attacking || !_npc.Faction.CanSendAssault)
            return;

        FactionController opposing = FactionRegistry.FindOpposingFaction(_npc.Faction);
        if (opposing == null)
            return;
        FactionNpc target = opposing.FindNearestWorker(transform.position);
        FactionNpc soldier = opposing.FindNearestSoldier(transform.position);
        if (soldier != null &&
            (target == null ||
             (soldier.transform.position - transform.position).sqrMagnitude <
             (target.transform.position - transform.position).sqrMagnitude))
            target = soldier;
        if (target != null)
        {
            _factionTarget = target;
            _npc.Faction.RememberHostileContact(target.transform.position);
        }
        else if (opposing.Barracks != null && opposing.Barracks.IsOperational)
            _factionTarget = opposing.Barracks;
        else if (opposing.Market != null && opposing.Market.IsOperational)
            _factionTarget = opposing.Market;
        else if (opposing.TownHall != null && opposing.TownHall.IsOperational)
            _factionTarget = opposing.TownHall;
    }

    void EngageZombie(float deltaTime)
    {
            EngageTarget(_zombieTarget.transform);
    }

    void EngageFaction(float deltaTime)
    {
        if (_factionTarget == null || _factionTarget.TargetTransform == null)
            return;
        if ((_factionTarget.TargetTransform.position - transform.position).sqrMagnitude < 1600f)
            _npc.Faction.MarkFighting();
        RequestFightBackup(_factionTarget.TargetTransform.position);
        EngageTarget(_factionTarget.TargetTransform);
    }

    public void NotifyUnderFire(Transform attacker)
    {
        if (_npc == null || _npc.Faction == null || attacker == null)
            return;
        Vector3 fight = attacker.position;
        if (_factionTarget != null && _factionTarget.TargetTransform != null)
            fight = _factionTarget.TargetTransform.position;
        else if (_zombieTarget != null)
            fight = _zombieTarget.transform.position;
        RequestFightBackup(fight);
    }

    void RequestFightBackup(Vector3 fight)
    {
        float radius = _npc.Faction.Combat.soldierEngageRadius > 0f
            ? _npc.Faction.Combat.soldierEngageRadius
            : 40f;
        FactionRegistry.CountSoldiersNear(fight, radius, _npc.Faction, out int friends, out int foes);
        int extra = 2 + Mathf.Max(0, foes - friends) * 2;
        _npc.Faction.RequestSkirmishBackup(fight, extra);
    }

    void EngageTarget(Transform target)
    {
        if (target == null)
            return;

        float range = _npc.Faction.Combat.soldierShootRange;
        Vector3 toTarget = target.position - transform.position;
        bool inRange = toTarget.sqrMagnitude <= range * range;
        if (inRange && Time.time >= _nextAttack)
        {
            _nextAttack = Time.time + _npc.Faction.Combat.soldierAttackCooldown;
            _lastShotHit = FactionSoldierHitscan.TryFire(
                _npc,
                target,
                range,
                _npc.Faction.Combat.soldierDamage);
        }

        if (!inRange)
        {
            _npc.Motor.SetDestination(target.position, _npc.Faction.Combat.soldierMoveSpeed, range * 0.7f);
            return;
        }

        if (Time.time >= _nextSkirmish || _npc.Motor.IsStopped)
        {
            _nextSkirmish = Time.time + Mathf.Max(0.35f, _npc.Faction.Economy.combatDecisionInterval);
            Vector3 point = SkirmishPoint(target.position, range);
            _npc.Motor.SetDestination(point, _npc.Faction.Combat.soldierMoveSpeed, 2f);
        }
    }

    Vector3 SkirmishPoint(Vector3 target, float range)
    {
        Vector3 toTarget = target - transform.position;
        Vector3 up = transform.up;
        Vector3 planar = Vector3.ProjectOnPlane(toTarget, up);
        Vector3 side = Vector3.Cross(up, planar.sqrMagnitude > 1e-4f ? planar.normalized : transform.right);
        if (side.sqrMagnitude < 1e-6f)
            side = transform.right;
        side.Normalize();
        int id = (int)EntityId.ToULong(GetEntityId());
        if ((id & 1) == 0)
            side = -side;

        bool kite = CurrentHealthFraction() <= _npc.Faction.Combat.soldierFightHealth;
        float frac = kite ? 0.82f : 0.68f;
        if ((id & 2) != 0)
            frac -= 0.08f;
        if (!_lastShotHit)
            frac = Mathf.Min(0.85f, frac + 0.12f);
        frac = Mathf.Clamp(frac, 0.55f, 0.85f);

        Vector3 offset = side * (range * frac);
        if (!_lastShotHit)
            offset += planar.sqrMagnitude > 1e-4f
                ? Vector3.Cross(up, side).normalized * (range * 0.2f)
                : side * (range * 0.2f);
        return _npc.Faction.OffsetOnSurface(target, offset);
    }

    float CurrentHealthFraction()
    {
        return (float)_npc.CurrentHealth / Mathf.Max(1, _npc.MaxHealth);
    }
}

static class FactionShotTracers
{
    const float Lifetime = 0.08f;
    const float Width = 0.1f;

    static Transform _root;
    static Material _material;
    static readonly List<LineRenderer> _idle = new List<LineRenderer>(16);

    public static void Show(Vector3 from, Vector3 to, Color color)
    {
        LineRenderer line = Take();
        if (line == null)
            return;
        line.enabled = true;
        line.startColor = color;
        line.endColor = color;
        line.SetPosition(0, from);
        line.SetPosition(1, to);
        TracerLease lease = line.GetComponent<TracerLease>();
        if (lease == null)
            lease = line.gameObject.AddComponent<TracerLease>();
        lease.ReleaseAt = Time.time + Lifetime;
        lease.enabled = true;
    }

    public static void Recycle(LineRenderer line)
    {
        if (line == null)
            return;
        line.enabled = false;
        _idle.Add(line);
    }

    static LineRenderer Take()
    {
        EnsureRoot();
        while (_idle.Count > 0)
        {
            int last = _idle.Count - 1;
            LineRenderer reuse = _idle[last];
            _idle.RemoveAt(last);
            if (reuse != null)
                return reuse;
        }

        var go = new GameObject("Tracer");
        go.transform.SetParent(_root, false);
        LineRenderer line = go.AddComponent<LineRenderer>();
        line.positionCount = 2;
        line.useWorldSpace = true;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.textureMode = LineTextureMode.Stretch;
        line.numCapVertices = 2;
        line.startWidth = Width;
        line.endWidth = Width * 0.55f;
        line.material = SharedMaterial();
        line.enabled = false;
        return line;
    }

    static void EnsureRoot()
    {
        if (_root != null)
            return;
        var go = new GameObject("FactionShotTracers");
        Object.DontDestroyOnLoad(go);
        _root = go.transform;
    }

    static Material SharedMaterial()
    {
        if (_material != null)
            return _material;
        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null)
            shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
            shader = Shader.Find("Hidden/Internal-Colored");
        if (shader != null)
            _material = new Material(shader) { name = "FactionShotTracer" };
        return _material;
    }

    sealed class TracerLease : MonoBehaviour
    {
        public float ReleaseAt;
        LineRenderer _line;

        void Awake()
        {
            _line = GetComponent<LineRenderer>();
        }

        void Update()
        {
            if (Time.time < ReleaseAt)
                return;
            enabled = false;
            FactionShotTracers.Recycle(_line);
        }

    }
}

static class FactionSoldierHitscan
{
    public static bool TryFire(FactionNpc shooter, Transform target, float range, int damage)
    {
        if (shooter == null || target == null || damage <= 0)
            return false;

        Vector3 origin = shooter.transform.position + shooter.transform.up * 1.25f;
        Vector3 aim = target.position + target.up * 0.9f;
        Vector3 delta = aim - origin;
        float distance = delta.magnitude;
        if (distance < 0.2f || distance > range + 0.5f)
            return false;

        Vector3 direction = delta / distance;
        origin += direction * 0.55f;
        Color tracer = shooter.Faction != null ? shooter.Faction.UiColor : Color.white;
        tracer.a = 1f;
        if (!Physics.Raycast(
                origin,
                direction,
                out RaycastHit hit,
                range,
                ~0,
                QueryTriggerInteraction.Ignore))
        {
            Color miss = tracer;
            miss.a = 0.45f;
            FactionShotTracers.Show(origin, origin + direction * range, miss);
            return false;
        }

        FactionShotTracers.Show(origin, hit.point, tracer);
        if (hit.collider.GetComponentInParent<PlanetMotor_InputSystem>() != null)
            return false;
        if (hit.collider.GetComponentInParent<PlayerHealth>() != null)
            return false;

        FactionNpc npc = hit.collider.GetComponentInParent<FactionNpc>();
        if (npc == shooter)
            return false;
        if (npc != null)
        {
            if (npc.Faction == shooter.Faction || npc.IsDead)
                return false;
            if (!FactionRegistry.WarfareEnabled)
                return false;
            if (!FactionRegistry.IsCombatTarget(npc, shooter.Faction))
                return false;
            npc.TakeFactionDamage(damage, shooter.transform);
            return true;
        }

        Building building = hit.collider.GetComponentInParent<Building>();
        if (building != null && building.OwningFaction != shooter.Faction)
        {
            if (!FactionRegistry.WarfareEnabled)
                return false;
            building.TakeFactionDamage(damage, shooter.transform);
            return true;
        }

        ZombieAI zombie = hit.collider.GetComponentInParent<ZombieAI>();
        if (zombie != null && !zombie.IsDead)
        {
            zombie.TakeDamageFromNpc(damage, shooter.transform);
            return true;
        }

        return false;
    }
}

/// <summary>Shared, throttled zombie lookup for faction NPCs.</summary>
public static class ZombieAwareness
{
    static ZombieAI[] s_Zombies = new ZombieAI[0];
    static float s_NextRefresh;

    public static IReadOnlyList<ZombieAI> CachedZombies
    {
        get
        {
            RefreshIfNeeded();
            return s_Zombies;
        }
    }

    public static bool TryFindThreat(Vector3 position, float radius, out ZombieAI result)
    {
        RefreshIfNeeded();

        float bestSq = radius * radius;
        result = null;
        for (int i = 0; i < s_Zombies.Length; i++)
        {
            ZombieAI zombie = s_Zombies[i];
            if (zombie == null || zombie.IsDead)
                continue;
            float d = (zombie.transform.position - position).sqrMagnitude;
            if (d <= bestSq)
            {
                bestSq = d;
                result = zombie;
            }
        }
        return result != null;
    }

    public static int CountThreats(Vector3 position, float radius)
    {
        RefreshIfNeeded();
        float radiusSq = radius * radius;
        int count = 0;
        for (int i = 0; i < s_Zombies.Length; i++)
        {
            ZombieAI zombie = s_Zombies[i];
            if (zombie == null || zombie.IsDead)
                continue;
            if ((zombie.transform.position - position).sqrMagnitude <= radiusSq)
                count++;
        }
        return count;
    }

    static void RefreshIfNeeded()
    {
        if (Time.time < s_NextRefresh)
            return;
        s_NextRefresh = Time.time + 0.75f;
        s_Zombies = Object.FindObjectsByType<ZombieAI>(
            FindObjectsInactive.Exclude, FindObjectsSortMode.None);
    }
}
