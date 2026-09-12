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
    float _nextHostileScan;
    float _gatherAccumulator;
    float _nextAttack;
    Vector3 _prospect;
    bool _hasProspect;
    int _emptySearches;
    readonly List<Vector3> _skippedProspects = new List<Vector3>(16);
    const int EmptySearchesBeforeHop = 4;

    float _lodNext;
    bool _phased;

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
        EnsurePhases();
        if (FactionNpcLod.IsFar(transform.position))
        {
            if (Time.time < _lodNext)
                return;
            _lodNext = Time.time + FactionNpcLod.AiInterval;
            Tick(FactionNpcLod.AiInterval);
            return;
        }

        Tick(Time.deltaTime);
    }

    void EnsurePhases()
    {
        if (_phased || _npc == null || _npc.Faction == null)
            return;
        _phased = true;
        int seed = FactionNpcLod.PhaseSeed(this);
        float combat = Mathf.Max(0.05f, _npc.Faction.Economy.combatDecisionInterval);
        float work = Mathf.Max(0.05f, _npc.Faction.Economy.workerDecisionInterval);
        _nextSearch = Time.time + FactionNpcLod.PhaseOffset(seed, work);
        _nextHostileScan = Time.time + FactionNpcLod.PhaseOffset(seed ^ 0x9e3779, combat);
        _lodNext = Time.time + FactionNpcLod.PhaseOffset(seed ^ unchecked((int)0x85ebca6b), FactionNpcLod.AiInterval);
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
        if (Time.time >= _nextHostileScan)
            ScanHostiles();

        if (_state == WorkState.Fleeing)
            return ResumeAfterFlee();
        if (_state == WorkState.FightingHostile)
            return TickFightHostiles();
        return false;
    }

    void ScanHostiles()
    {
        float interval = _npc.Faction.Economy.combatDecisionInterval > 0f
            ? _npc.Faction.Economy.combatDecisionInterval
            : 0.25f;
        _nextHostileScan = Time.time + interval;

        FactionCombatSettings combat = _npc.Faction.Combat;
        float radius = combat.workerFightRadius;
        _npc.Faction.TryGetPerceivedThreat(
            transform.position,
            radius,
            out FactionNpc nearestEnemy,
            out ZombieAI nearestZombie,
            out int nearby);
        bool canFightFactions = FactionRegistry.WarfareEnabled &&
                                !FactionRegistry.IsWarfareProtected(_npc.Faction);
        if (!canFightFactions)
        {
            nearestEnemy = null;
            nearby = nearestZombie != null ? 1 : 0;
        }

        if (nearby <= 0)
        {
            _zombieTarget = null;
            _npcTarget = null;
            if (_state == WorkState.FightingHostile)
            {
                _state = _inventory != null && _inventory.IsFull
                    ? WorkState.GoingToDeposit
                    : WorkState.LookingForResource;
            }
            return;
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
            return;
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
        }
        else
        {
            _zombieTarget = nearestZombie;
            _npcTarget = null;
        }
    }

    bool TickFightHostiles()
    {
        FactionCombatSettings combat = _npc.Faction.Combat;
        if (_npcTarget != null && !_npcTarget.IsDead && _npcTarget.isActiveAndEnabled)
        {
            _npc.Motor.SetDestination(
                _npcTarget.transform.position,
                combat.workerMoveSpeed,
                combat.workerAttackRange);
            if (_npc.Motor.IsStopped && Time.time >= _nextAttack)
            {
                _nextAttack = Time.time + combat.workerAttackCooldown;
                _npcTarget.TakeFactionDamage(combat.workerDamage, transform);
            }
            return true;
        }
        if (_zombieTarget != null && !_zombieTarget.IsDead)
        {
            _npc.Motor.SetDestination(
                _zombieTarget.transform.position,
                combat.workerMoveSpeed,
                combat.workerAttackRange);
            if (_npc.Motor.IsStopped && Time.time >= _nextAttack)
            {
                _nextAttack = Time.time + combat.workerAttackCooldown;
                _zombieTarget.TakeDamageFromNpc(combat.workerDamage, transform);
            }
            return true;
        }

        _npcTarget = null;
        _zombieTarget = null;
        _nextHostileScan = 0f;
        _state = _inventory != null && _inventory.IsFull
            ? WorkState.GoingToDeposit
            : WorkState.LookingForResource;
        return false;
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

        ResourceNode node = null;
        if (_npc.Faction.TryGetPerceivedResource(_gatherTask, out ResourceNode perceived))
            node = perceived;
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
    float _nextBackup;
    bool _safe;
    bool _hasDuty;
    bool _lastShotHit;
    float _lodNext;
    bool _phased;

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
        EnsurePhases();
        if (FactionNpcLod.IsFar(transform.position))
        {
            if (Time.time < _lodNext)
                return;
            _lodNext = Time.time + FactionNpcLod.AiInterval;
            Tick(FactionNpcLod.AiInterval);
            return;
        }

        Tick(Time.deltaTime);
    }

    void EnsurePhases()
    {
        if (_phased || _npc == null || _npc.Faction == null)
            return;
        _phased = true;
        int seed = FactionNpcLod.PhaseSeed(this);
        float combat = Mathf.Max(0.05f, _npc.Faction.Economy.combatDecisionInterval);
        _nextDecision = Time.time + FactionNpcLod.PhaseOffset(seed, combat);
        _nextBackup = Time.time + FactionNpcLod.PhaseOffset(seed ^ 0x9e3779, combat);
        _lodNext = Time.time + FactionNpcLod.PhaseOffset(seed ^ unchecked((int)0x85ebca6b), FactionNpcLod.AiInterval);
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
            _npc.Faction.TryGetPerceivedThreat(
                transform.position,
                _npc.Faction.Combat.zombieThreatRadius,
                out _,
                out ZombieAI homeZombie,
                out _);
            if (homeZombie != null && !homeZombie.IsDead)
            {
                _zombieTarget = homeZombie;
                EngageZombie(deltaTime);
                _safe = (transform.position - _npc.Faction.GetSafePosition()).sqrMagnitude < 64f;
                return;
            }

            _zombieTarget = null;
            _npc.Motor.SetDestination(_npc.Faction.GetSafePosition(), GetMoveSpeed(), 4f);
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
        if (_factionTarget != null &&
            FactionRegistry.IsWarfareProtected(_factionTarget.OwningFaction))
            _factionTarget = null;
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
                GetMoveSpeed(),
                4f);
            return;
        }

        if (_npc.Faction.HasDefensePing)
        {
            _hasDuty = false;
            _npc.Motor.SetDestination(
                _npc.Faction.DefensePoint,
                GetMoveSpeed(),
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
        _npc.Motor.SetDestination(destination, GetMoveSpeed(), 3f);
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
            _npc.Faction.TryGetPerceivedThreat(
                transform.position,
                engage,
                out FactionNpc nearbyEnemy,
                out ZombieAI nearbyZombie,
                out _);
            if (nearbyEnemy != null)
            {
                _factionTarget = nearbyEnemy;
                _npc.Faction.RememberHostileContact(nearbyEnemy.transform.position);
                return;
            }
            if (nearbyZombie != null)
            {
                _zombieTarget = nearbyZombie;
                return;
            }
        }
        else
        {
            _npc.Faction.TryGetPerceivedThreat(
                transform.position,
                _npc.Faction.Combat.zombieThreatRadius,
                out _,
                out ZombieAI nearbyZombie,
                out _);
            if (nearbyZombie != null)
            {
                _zombieTarget = nearbyZombie;
                return;
            }
        }

        if (_npc.Faction.HasDefensePing)
        {
            Vector3 ping = _npc.Faction.DefensePoint;
            float defend = _npc.Faction.Combat.soldierDefendRadius;
            _npc.Faction.TryGetPerceivedThreat(ping, defend, out FactionNpc enemy, out ZombieAI pingZombie, out _);
            if (FactionRegistry.WarfareEnabled && enemy != null)
            {
                _factionTarget = enemy;
                _npc.Faction.RememberHostileContact(enemy.transform.position);
                return;
            }
            if (pingZombie != null)
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
            _npc.Faction.TryGetPerceivedThreat(ping, defend, out FactionNpc enemy, out ZombieAI backupZombie, out _);
            if (FactionRegistry.WarfareEnabled && enemy != null)
            {
                _factionTarget = enemy;
                _npc.Faction.RememberHostileContact(enemy.transform.position);
                return;
            }
            if (backupZombie != null)
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
        if (opposing == null || FactionRegistry.IsWarfareProtected(opposing))
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
        float interval = _npc.Faction.Economy.combatDecisionInterval > 0f
            ? _npc.Faction.Economy.combatDecisionInterval
            : 0.25f;
        if (Time.time < _nextBackup)
            return;
        _nextBackup = Time.time + interval;

        float radius = _npc.Faction.Combat.soldierEngageRadius > 0f
            ? _npc.Faction.Combat.soldierEngageRadius
            : 40f;
        _npc.Faction.CountPerceivedSoldiersNear(fight, radius, out int friends, out int foes);
        int extra = 2 + Mathf.Max(0, foes - friends) * 2;
        _npc.Faction.RequestSkirmishBackup(fight, extra);
    }

    void EngageTarget(Transform target)
    {
        if (target == null)
            return;

        float range = GetShootRange();
        int damage = GetDamage();
        float cooldown = GetAttackCooldown();
        float moveSpeed = GetMoveSpeed();
        Vector3 toTarget = target.position - transform.position;
        bool inRange = toTarget.sqrMagnitude <= range * range;
        if (inRange && Time.time >= _nextAttack)
        {
            _nextAttack = Time.time + cooldown;
            _lastShotHit = FactionSoldierHitscan.TryFire(
                _npc,
                target,
                range,
                damage);
        }

        if (!inRange)
        {
            _npc.Motor.SetDestination(target.position, moveSpeed, range * 0.7f);
            return;
        }

        if (Time.time >= _nextSkirmish || _npc.Motor.IsStopped)
        {
            _nextSkirmish = Time.time + Mathf.Max(0.35f, _npc.Faction.Economy.combatDecisionInterval);
            Vector3 point = SkirmishPoint(target.position, range);
            _npc.Motor.SetDestination(point, moveSpeed, 2f);
        }
    }

    float GetShootRange()
    {
        return _npc.Role == FactionNpcRole.Archer
            ? _npc.Faction.Combat.archerShootRange
            : _npc.Faction.Combat.soldierShootRange;
    }

    int GetDamage()
    {
        return _npc.Role == FactionNpcRole.Archer
            ? _npc.Faction.Combat.archerDamage
            : _npc.Faction.Combat.soldierDamage;
    }

    float GetAttackCooldown()
    {
        return _npc.Role == FactionNpcRole.Archer
            ? _npc.Faction.Combat.archerAttackCooldown
            : _npc.Faction.Combat.soldierAttackCooldown;
    }

    float GetMoveSpeed()
    {
        return _npc.Role == FactionNpcRole.Archer
            ? _npc.Faction.Combat.archerMoveSpeed
            : _npc.Faction.Combat.soldierMoveSpeed;
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

/// <summary>Noble. Travels to claimable towns and drains loyalty under escort.</summary>
public sealed class NobleAI : MonoBehaviour
{
    FactionNpc _npc;
    ClaimableTown _targetTown;
    float _nextDecision;
    float _lodNext;
    bool _phased;

    public ClaimableTown TargetTown => _targetTown;

    void Awake()
    {
        _npc = GetComponent<FactionNpc>();
    }

    void Update()
    {
        EnsurePhases();
        if (FactionNpcLod.IsFar(transform.position))
        {
            if (Time.time < _lodNext)
                return;
            _lodNext = Time.time + FactionNpcLod.AiInterval;
            Tick(FactionNpcLod.AiInterval);
            return;
        }

        Tick(Time.deltaTime);
    }

    void EnsurePhases()
    {
        if (_phased || _npc == null || _npc.Faction == null)
            return;
        _phased = true;
        int seed = FactionNpcLod.PhaseSeed(this);
        float combat = Mathf.Max(0.05f, _npc.Faction.Economy.combatDecisionInterval);
        _nextDecision = Time.time + FactionNpcLod.PhaseOffset(seed, combat);
        _lodNext = Time.time + FactionNpcLod.PhaseOffset(seed ^ unchecked((int)0x27d4eb2d), FactionNpcLod.AiInterval);
    }

    public void AssignTown(ClaimableTown town)
    {
        if (_targetTown != null && _targetTown != town)
            _targetTown.StopClaim(_npc);
        _targetTown = town;
    }

    public void ClearTown()
    {
        if (_targetTown != null)
            _targetTown.StopClaim(_npc);
        _targetTown = null;
    }

    public void Tick(float deltaTime)
    {
        if (_npc == null || _npc.Faction == null || _npc.IsDead)
            return;

        float speed = _npc.Faction.Combat.nobleMoveSpeed;
        if (_npc.Faction.State == FactionState.Retreating ||
            _npc.Faction.State == FactionState.Recovering ||
            CurrentHealthFraction() <= _npc.Faction.Combat.soldierRetreatHealth)
        {
            ClearTown();
            _npc.Motor.SetDestination(_npc.Faction.GetSafePosition(), speed, 4f);
            return;
        }

        if (Time.time >= _nextDecision)
        {
            _nextDecision = Time.time + _npc.Faction.Economy.combatDecisionInterval;
            if (_targetTown == null)
                _targetTown = _npc.Faction.ClaimTargetTown;
            if (_targetTown != null &&
                _targetTown.Owner == _npc.Faction)
                ClearTown();
        }

        if (_targetTown == null)
        {
            _npc.Motor.SetDestination(_npc.Faction.GetSafePosition(), speed, 4f);
            return;
        }

        float claimRadius = Mathf.Max(1f, _npc.Faction.Economy.townClaimRadius);
        Vector3 toTown = _targetTown.transform.position - transform.position;
        if (toTown.sqrMagnitude > claimRadius * claimRadius)
        {
            _targetTown.StopClaim(_npc);
            _npc.Motor.SetDestination(_targetTown.transform.position, speed, claimRadius * 0.5f);
            return;
        }

        _targetTown.TryBeginClaim(_npc);
        _npc.Motor.SetDestination(_targetTown.transform.position, speed * 0.35f, 1.5f);
    }

    float CurrentHealthFraction()
    {
        return (float)_npc.CurrentHealth / Mathf.Max(1, _npc.MaxHealth);
    }
}

static class FactionSoldierProjectiles
{
    static GameObject s_Prefab;
    static float s_Speed = 160f;
    static bool s_Resolved;

    public static void Spawn(Vector3 origin, Vector3 direction, float travelDistance, Color tint, Transform ignoreRoot)
    {
        if (direction.sqrMagnitude < 1e-8f)
            return;
        direction.Normalize();
        EnsurePrefab();
        GameObject instance;
        if (s_Prefab != null)
            instance = Object.Instantiate(s_Prefab, origin, Quaternion.LookRotation(direction));
        else
            instance = CreateFallbackBolt(origin, direction);

        var proj = instance.GetComponent<Projectile>();
        if (proj == null)
            proj = instance.AddComponent<Projectile>();
        proj.ConfigureFromWeapon(tint, 0, false, 0f, 1f, 1f);
        proj.SetIgnoreRoot(ignoreRoot);
        proj.lifetime = Mathf.Clamp(travelDistance / Mathf.Max(40f, s_Speed) + 0.12f, 0.12f, 2f);

        Rigidbody rb = instance.GetComponent<Rigidbody>();
        if (rb != null)
            rb.linearVelocity = direction * s_Speed;
        else
        {
            var mover = instance.GetComponent<ProjectileMover>();
            if (mover == null)
                mover = instance.AddComponent<ProjectileMover>();
            mover.direction = direction;
            mover.speed = s_Speed;
        }
    }

    static void EnsurePrefab()
    {
        if (s_Resolved)
            return;
        s_Resolved = true;
        PlayerShooting shooting = Object.FindFirstObjectByType<PlayerShooting>(FindObjectsInactive.Include);
        if (shooting != null)
        {
            s_Prefab = shooting.projectilePrefab;
            if (shooting.projectileSpeed > 1f)
                s_Speed = shooting.projectileSpeed;
        }
        if (s_Prefab == null)
            s_Prefab = Resources.Load<GameObject>("Projectile");
    }

    static GameObject CreateFallbackBolt(Vector3 origin, Vector3 direction)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "FactionProjectile";
        go.transform.position = origin;
        go.transform.rotation = Quaternion.LookRotation(direction);
        go.transform.localScale = Vector3.one * 0.3f;
        SphereCollider col = go.GetComponent<SphereCollider>();
        if (col != null)
            col.isTrigger = true;
        var rb = go.AddComponent<Rigidbody>();
        rb.useGravity = false;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
        return go;
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
        Color bolt = shooter.Faction != null ? shooter.Faction.UiColor : Color.white;
        bolt.a = 1f;
        if (!Physics.Raycast(
                origin,
                direction,
                out RaycastHit hit,
                range,
                ~0,
                QueryTriggerInteraction.Ignore))
        {
            FactionSoldierProjectiles.Spawn(origin, direction, range, bolt, shooter.transform);
            return false;
        }

        FactionSoldierProjectiles.Spawn(origin, direction, hit.distance, bolt, shooter.transform);
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
            if (!FactionRegistry.IsCombatTarget(building, shooter.Faction))
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

/// <summary>Shared zombie lookup for faction NPCs.</summary>
public static class ZombieAwareness
{
    public static IReadOnlyList<ZombieAI> CachedZombies => ZombieAI.Active;

    public static bool TryFindThreat(Vector3 position, float radius, out ZombieAI result)
    {
        CountThreats(position, radius, out result);
        return result != null;
    }

    public static int CountThreats(Vector3 position, float radius)
    {
        return CountThreats(position, radius, out _);
    }

    public static int CountThreats(Vector3 position, float radius, out ZombieAI nearest)
    {
        nearest = null;
        float radiusSq = radius * radius;
        float bestSq = radiusSq;
        int count = 0;
        IReadOnlyList<ZombieAI> zombies = ZombieAI.Active;
        for (int i = 0; i < zombies.Count; i++)
        {
            ZombieAI zombie = zombies[i];
            if (zombie == null || zombie.IsDead)
                continue;
            float d = (zombie.transform.position - position).sqrMagnitude;
            if (d > radiusSq)
                continue;
            count++;
            if (d <= bestSq)
            {
                bestSq = d;
                nearest = zombie;
            }
        }
        return count;
    }
}
