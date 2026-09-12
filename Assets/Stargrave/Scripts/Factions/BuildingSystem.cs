using System.Collections.Generic;
using UnityEngine;

public enum BuildingKind
{
    TownHall,
    Barracks,
    Market,
    Mint,
    Mex,
    EnergyGen,
    Factory
}

[DisallowMultipleComponent]
public class Building : MonoBehaviour, IFactionDamageable
{
    public BuildingKind kind;
    public FactionController Faction { get; private set; }
    public bool IsOperational { get; private set; }
    public BuildingKind Kind => kind;
    [Min(1)] public int maxHealth = 500;
    public int CurrentHealth { get; private set; }
    public bool IsDestroyed { get; private set; }
    public bool IsFactionTargetable => IsOperational && !IsDestroyed;
    public FactionController OwningFaction => Faction;
    public Transform TargetTransform => transform;

    public void Configure(FactionController faction, BuildingKind buildingKind)
    {
        Faction = faction;
        kind = buildingKind;
        IsOperational = false;
        CurrentHealth = Mathf.Max(1, maxHealth);
        IsDestroyed = false;
    }

    public void SetOperational()
    {
        IsOperational = true;
    }

    public void TakeFactionDamage(int amount, Transform attacker)
    {
        if (!IsFactionTargetable || amount <= 0)
            return;
        CurrentHealth = Mathf.Max(0, CurrentHealth - amount);
        if (Faction != null)
            FactionTradeSystem.NotifyHostileDamage(Faction, attacker);
        if (CurrentHealth == 0)
        {
            IsDestroyed = true;
            IsOperational = false;
            if (Faction != null)
                Faction.NotifyBuildingDestroyed(this);
            Collider[] colliders = GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null)
                    colliders[i].enabled = false;
            }
            gameObject.SetActive(false);
            Destroy(gameObject);
        }
    }
}

public sealed class TownHall : Building
{
    public Vector3 RetreatPoint => transform.position;
}

public sealed class Market : Building
{
}

public sealed class Mint : Building
{
    public void TickConversion(float deltaTime)
    {
        if (!IsOperational || Faction == null || deltaTime <= 0f)
            return;

        FactionEconomySettings eco = Faction.Economy;
        int woodNeed = Mathf.Max(1, eco.mintWoodPerGold);
        int stoneNeed = Mathf.Max(1, eco.mintStonePerGold);
        int goldOut = Mathf.Max(1, eco.mintGoldPerTick);
        if (Faction.AvailableWood < eco.mintReserveWood + woodNeed)
            return;
        if (Faction.AvailableStone < eco.mintReserveStone + stoneNeed)
            return;
        if (!Faction.TrySpendAvailableCost(new FactionResourceCost(woodNeed, stoneNeed)))
            return;
        Faction.AddGold(goldOut);
    }
}

/// <summary>BAR/ZK metal extractor — passive metal (wood pool) income.</summary>
public sealed class Mex : Building
{
    /// <summary>Territory pocket id when this mex claimed a hotspot; 0 if campus-ring mex.</summary>
    public int LinkedPocketId { get; set; }
}

/// <summary>BAR/ZK energy generator — passive energy (stone pool) income.</summary>
public sealed class EnergyGen : Building
{
}

/// <summary>BAR/ZK lab — continuous raider + heavy production, plus secondary noble/merchant.</summary>
public sealed class Factory : Building
{
    float _timer;
    int _supportCycle;
    int _raidersSinceHeavy;

    public void TickProduction(float deltaTime)
    {
        if (!IsOperational || Faction == null || deltaTime <= 0f)
            return;
        if (!Stargrave.Rts2.Rts2UnitSim.HasInstance)
            return;

        FactionEconomySettings eco = Faction.Economy;
        _timer += deltaTime;
        while (true)
        {
            // Secondary: nobles / merchants when Market is up and claim/trade wants them.
            if (TryProduceSupportUnit())
            {
                _timer -= Mathf.Max(0.5f, eco.raiderBuildSeconds);
                continue;
            }

            bool heavy = ShouldProduceHeavy(eco);
            FactionResourceCost cost = heavy ? eco.heavyCost : eco.raiderCost;
            float need = Mathf.Max(0.5f, heavy ? eco.heavyBuildSeconds : eco.raiderBuildSeconds);
            if (_timer < need)
                break;
            if (!Faction.HasResources(cost))
                break;
            if (!Faction.TrySpendAvailableCost(cost))
                break;

            Vector3 axis = Faction.SpawnAxis;
            if (Faction.Simulation != null && Faction.Simulation.planet != null)
            {
                Vector3 from = transform.position - Faction.Simulation.planet.transform.position;
                if (from.sqrMagnitude > 1e-6f)
                    axis = from.normalized;
            }

            RtsUnitRole role = heavy ? RtsUnitRole.Archer : RtsUnitRole.Infantry;
            if (!Stargrave.Rts2.Rts2UnitSim.Instance.TrySpawn(
                    Faction.RuntimeIndex, role, axis, out _))
            {
                // Refund on spawn fail.
                Faction.AddBarMetal(cost.wood);
                Faction.AddBarEnergy(cost.stone);
                break;
            }

            _timer -= need;
            if (heavy)
                _raidersSinceHeavy = 0;
            else
                _raidersSinceHeavy++;

            if (Faction.Simulation != null && Faction.Simulation.verboseEvents)
            {
                Debug.Log(
                    heavy
                        ? $"[FactionSimulation] {Faction.DisplayName} factory produced a heavy."
                        : $"[FactionSimulation] {Faction.DisplayName} factory produced a raider.",
                    Faction);
            }
        }
    }

    bool ShouldProduceHeavy(FactionEconomySettings eco)
    {
        if (eco == null)
            return false;
        if (Faction.InfantryCount < Mathf.Max(0, eco.heavyUnlockRaiders))
            return false;
        int per = Mathf.Max(1, eco.raidersPerHeavy);
        if (_raidersSinceHeavy < per)
            return false;
        // Soft cap: don't flood heavies past ~1/4 of combat force.
        int heavies = Faction.ArcherCount;
        int raiders = Faction.InfantryCount;
        if (heavies * per > raiders + per)
            return false;
        return true;
    }

    bool TryProduceSupportUnit()
    {
        if (Faction.Market == null || !Faction.Market.IsOperational)
            return false;

        // Keep pumping combat most of the time; every 4th cycle try support.
        _supportCycle = (_supportCycle + 1) % 4;
        if (_supportCycle != 0 && Faction.SoldierCount < Faction.Personality.AssaultThreshold(Faction.Economy))
            return false;

        Vector3 axis = Faction.SpawnAxis;
        if (Faction.Simulation != null && Faction.Simulation.planet != null)
        {
            Vector3 from = transform.position - Faction.Simulation.planet.transform.position;
            if (from.sqrMagnitude > 1e-6f)
                axis = from.normalized;
        }

        if (Faction.WantsNobleTraining() &&
            Faction.NobleCount < Faction.Economy.nobleMaxCount &&
            Faction.TrySpendNobleTraining())
        {
            if (Stargrave.Rts2.Rts2UnitSim.Instance.TrySpawn(
                    Faction.RuntimeIndex, RtsUnitRole.Noble, axis, out _))
            {
                if (Faction.Simulation != null && Faction.Simulation.verboseEvents)
                    Debug.Log($"[FactionSimulation] {Faction.DisplayName} factory produced a noble.", Faction);
                return true;
            }
            // Refund metal/energy (gold only spent in village mode).
            Faction.AddBarMetal(Faction.Economy.nobleCost.wood);
            Faction.AddBarEnergy(Faction.Economy.nobleCost.stone);
            return false;
        }

        if (Faction.WantsMerchantTrade() &&
            Faction.MerchantCount < Faction.Economy.merchantMaxCount &&
            Faction.TrySpendMerchantTraining())
        {
            if (Stargrave.Rts2.Rts2UnitSim.Instance.TrySpawn(
                    Faction.RuntimeIndex, RtsUnitRole.Merchant, axis, out _))
            {
                if (Faction.Simulation != null && Faction.Simulation.verboseEvents)
                    Debug.Log($"[FactionSimulation] {Faction.DisplayName} factory produced a merchant.", Faction);
                return true;
            }
            Faction.AddBarMetal(Faction.Economy.merchantCost.wood);
            Faction.AddBarEnergy(Faction.Economy.merchantCost.stone);
            return false;
        }

        return false;
    }
}

public sealed class Barracks : Building
{
    enum TrainKind
    {
        None,
        ResourceGatherer,
        Infantry,
        Archer,
        Merchant,
        Noble
    }

    float _trainingTimer;
    int _queued;
    TrainKind _queuedKind;

    public int QueueLength => _queued;

    public void TickProduction(float deltaTime)
    {
        if (!IsOperational || Faction == null || Faction.Simulation == null)
            return;

        float modifier = Mathf.Max(0.1f, Faction.GrowthModifier);
        if (_queued == 0)
            TryQueueUnit();

        if (_queued <= 0)
            return;

        float duration = TrainingSeconds(_queuedKind);
        _trainingTimer += deltaTime * modifier;
        if (_trainingTimer < duration)
            return;

        _trainingTimer = 0f;
        _queued--;
        TrainKind kind = _queuedKind;
        _queuedKind = TrainKind.None;
        SpawnTrainedUnit(kind);
    }

    float TrainingSeconds(TrainKind kind)
    {
        switch (kind)
        {
            case TrainKind.ResourceGatherer:
                return Faction.Economy.workerTrainingSeconds;
            case TrainKind.Merchant:
                return Faction.Economy.merchantTrainingSeconds;
            case TrainKind.Archer:
                return Faction.Economy.archerTrainingSeconds;
            case TrainKind.Noble:
                return Faction.Economy.nobleTrainingSeconds;
            default:
                return Faction.Economy.infantryTrainingSeconds;
        }
    }

    void TryQueueUnit()
    {
        FactionEconomySettings eco = Faction.Economy;
        bool recovering = Faction.State == FactionState.Recovering;
        int workers = Faction.WorkerCount;
        int soldiers = Faction.SoldierCount;

        // Recovery: rebuild the gather loop first.
        int recoveryWorkers = Mathf.Max(1, eco.startingWorkers);
        if (recovering && workers < recoveryWorkers)
        {
            TryQueueWorker();
            return;
        }

        int earlyWorkers = Mathf.Clamp(eco.earlyWorkerTarget, eco.startingWorkers, eco.workerMaxCount);
        int garrison = Mathf.Clamp(eco.garrisonBeforeMaxWorkers, 0, eco.soldierMaxCount);

        // 1) Early economy shell.
        if (workers < earlyWorkers && TryQueueWorker())
            return;

        // 2) Small garrison before maxing workers / luxury units.
        if (soldiers < garrison && TryQueueCombat())
            return;

        // 3) Claim / trade specials once the campus can defend a bit.
        if (Faction.WantsNobleTraining() &&
            Faction.NobleCount < eco.nobleMaxCount &&
            Faction.TrySpendNobleTraining())
        {
            Queue(TrainKind.Noble);
            return;
        }

        int minSoldiers = Faction.Warfare.minSoldiersToPropose > 0
            ? Faction.Warfare.minSoldiersToPropose
            : 6;
        bool armyReady = soldiers >= minSoldiers;
        if (armyReady &&
            Faction.Market != null &&
            Faction.Market.IsOperational &&
            Faction.MerchantCount < eco.merchantMaxCount &&
            Faction.WantsMerchantTrade() &&
            Faction.TrySpendMerchantTraining())
        {
            Queue(TrainKind.Merchant);
            return;
        }

        // 4) Finish worker cap, then keep pumping the army.
        if (workers < eco.workerMaxCount && TryQueueWorker())
            return;

        TryQueueCombat();
    }

    bool TryQueueWorker()
    {
        if (Faction.WorkerCount >= Faction.Economy.workerMaxCount)
            return false;
        if (!Faction.HasResources(Faction.Economy.workerCost))
            return false;
        if (!Faction.TrySpendAvailableCost(Faction.Economy.workerCost))
            return false;
        Queue(TrainKind.ResourceGatherer);
        return true;
    }

    bool TryQueueCombat()
    {
        if (Faction.SoldierCount >= Faction.Economy.soldierMaxCount)
            return false;

        int infantry = Faction.InfantryCount;
        int archers = Faction.ArcherCount;
        int ratio = Mathf.Max(1, Faction.Economy.infantryPerArcher);
        bool preferArcher = archers * ratio < infantry;

        if (preferArcher &&
            Faction.HasResources(Faction.Economy.archerCost) &&
            Faction.TrySpendAvailableCost(Faction.Economy.archerCost))
        {
            Queue(TrainKind.Archer);
            return true;
        }

        if (Faction.HasResources(Faction.Economy.infantryCost) &&
            Faction.TrySpendAvailableCost(Faction.Economy.infantryCost))
        {
            Queue(TrainKind.Infantry);
            return true;
        }

        if (!preferArcher &&
            Faction.HasResources(Faction.Economy.archerCost) &&
            Faction.TrySpendAvailableCost(Faction.Economy.archerCost))
        {
            Queue(TrainKind.Archer);
            return true;
        }

        return false;
    }

    void Queue(TrainKind kind)
    {
        _queued = 1;
        _queuedKind = kind;
        _trainingTimer = 0f;
    }

    void SpawnTrainedUnit(TrainKind kind)
    {
        if (kind == TrainKind.None || Faction.Simulation.planet == null)
            return;
        if (kind == TrainKind.ResourceGatherer &&
            Faction.WorkerCount >= Faction.Economy.workerMaxCount)
            return;
        if ((kind == TrainKind.Infantry || kind == TrainKind.Archer) &&
            Faction.SoldierCount >= Faction.Economy.soldierMaxCount)
            return;
        if (kind == TrainKind.Merchant &&
            Faction.MerchantCount >= Faction.Economy.merchantMaxCount)
            return;
        if (kind == TrainKind.Noble &&
            Faction.NobleCount >= Faction.Economy.nobleMaxCount)
            return;

        Vector3 axis = (transform.position - Faction.Simulation.planet.transform.position).normalized;
        Vector3 tangent = Vector3.Cross(axis, Mathf.Abs(Vector3.Dot(axis, Vector3.up)) > 0.9f
            ? Vector3.right
            : Vector3.up).normalized;
        axis = (axis + Quaternion.AngleAxis(Random.Range(0f, 360f), axis) *
            tangent * 0.004f).normalized;

        RtsUnitRole role;
        string label;
        if (kind == TrainKind.ResourceGatherer)
        {
            role = RtsUnitRole.Worker;
            label = "worker";
        }
        else if (kind == TrainKind.Merchant)
        {
            role = RtsUnitRole.Merchant;
            label = "merchant";
        }
        else if (kind == TrainKind.Archer)
        {
            role = RtsUnitRole.Archer;
            label = "archer";
        }
        else if (kind == TrainKind.Noble)
        {
            role = RtsUnitRole.Noble;
            label = "noble";
        }
        else
        {
            role = RtsUnitRole.Infantry;
            label = "infantry";
        }

        if (!Stargrave.Rts2.Rts2UnitSim.HasInstance ||
            !Stargrave.Rts2.Rts2UnitSim.Instance.TrySpawn(Faction.RuntimeIndex, role, axis, out _))
            return;

        if (role == RtsUnitRole.Worker)
            Stargrave.Rts2.Rts2UnitSim.Instance.SetGatherTask(Faction.RuntimeIndex, FactionResourceType.Wood);

        if (Faction.Simulation.verboseEvents)
            Debug.Log($"[FactionSimulation] {Faction.DisplayName} trained a {label} (swarm).", Faction);
    }
}

/// <summary>Construction state with separate material delivery and builder progress.</summary>
[DisallowMultipleComponent]
public sealed class BuildingConstructionSite : MonoBehaviour
{
    public Building Building { get; private set; }
    public FactionController Faction { get; private set; }
    public BuildingKind Kind { get; private set; }
    public bool IsComplete { get; private set; }
    public float ConstructionProgress { get; private set; }
    public float ConstructionProgress01 => Mathf.Clamp01(ConstructionProgress / BuildSeconds);
    public float BuildSeconds { get; private set; }
    public FactionResourceCost Cost { get; private set; }
    public int DeliveredWood { get; private set; }
    public int DeliveredStone { get; private set; }
    public int MaxBuilders { get; private set; }
    public int BuilderCount => _builders.Count;
    public bool HasOpenBuilderSlot => !IsComplete && MaterialsDelivered && _builders.Count < MaxBuilders;
    public bool MaterialsDelivered => DeliveredWood >= Cost.wood && DeliveredStone >= Cost.stone;

    readonly List<FactionNpc> _builders = new List<FactionNpc>(4);
    GameObject _visual;
    Vector3 _visualFitScale = Vector3.one;

    public void Configure(
        FactionController faction,
        BuildingKind kind,
        GameObject prefab,
        FactionResourceCost cost,
        float buildSeconds,
        int maxBuilders)
    {
        Faction = faction;
        Kind = kind;
        Cost = cost;
        BuildSeconds = Mathf.Max(1f, buildSeconds);
        MaxBuilders = Mathf.Max(1, maxBuilders);

        if (kind == BuildingKind.TownHall)
            Building = gameObject.AddComponent<TownHall>();
        else if (kind == BuildingKind.Market)
            Building = gameObject.AddComponent<Market>();
        else if (kind == BuildingKind.Mint)
            Building = gameObject.AddComponent<Mint>();
        else if (kind == BuildingKind.Mex)
            Building = gameObject.AddComponent<Mex>();
        else if (kind == BuildingKind.EnergyGen)
            Building = gameObject.AddComponent<EnergyGen>();
        else if (kind == BuildingKind.Factory)
            Building = gameObject.AddComponent<Factory>();
        else
            Building = gameObject.AddComponent<Barracks>();
        Building.Configure(faction, kind);

        if (prefab != null)
        {
            _visual = Instantiate(prefab, transform);
            _visual.name = $"{kind}_Visual";
            _visual.transform.localPosition = Vector3.zero;
            _visual.transform.localRotation = Quaternion.identity;
            FitVisualToBuilding(kind);
        }
        else
        {
            _visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _visual.name = $"{kind}_FallbackVisual";
            _visual.transform.SetParent(transform, false);
            Vector3 size = FallbackCubeSize(kind);
            _visual.transform.localPosition = new Vector3(0f, size.y * 0.5f, 0f);
            _visual.transform.localScale = size;
            Collider col = _visual.GetComponent<Collider>();
            if (col != null)
                Object.Destroy(col);
            _visualFitScale = _visual.transform.localScale;
        }
    }

    static Vector3 FallbackCubeSize(BuildingKind kind)
    {
        switch (kind)
        {
            case BuildingKind.TownHall: return new Vector3(10f, 32f, 10f);
            case BuildingKind.Factory: return new Vector3(8f, 18f, 8f);
            case BuildingKind.EnergyGen: return new Vector3(6f, 16f, 6f);
            case BuildingKind.Mex: return new Vector3(4f, 6f, 4f);
            default: return new Vector3(5f, 10f, 5f);
        }
    }

    public bool TryDeliver(FactionResourceType type, int amount, out int delivered)
    {
        delivered = 0;
        if (IsComplete || amount <= 0)
            return false;

        if (type == FactionResourceType.Wood)
        {
            int room = Mathf.Max(0, Cost.wood - DeliveredWood);
            delivered = Mathf.Min(amount, room);
            DeliveredWood += delivered;
        }
        else
        {
            int room = Mathf.Max(0, Cost.stone - DeliveredStone);
            delivered = Mathf.Min(amount, room);
            DeliveredStone += delivered;
        }
        return delivered > 0;
    }

    public bool TryReserveBuilder(FactionNpc worker)
    {
        if (!MaterialsDelivered || IsComplete || worker == null)
            return false;
        if (_builders.Contains(worker))
            return true;
        if (_builders.Count >= MaxBuilders)
            return false;
        _builders.Add(worker);
        return true;
    }

    public void ReleaseBuilder(FactionNpc worker)
    {
        if (worker != null)
            _builders.Remove(worker);
    }

    public bool IsBuilder(FactionNpc worker)
    {
        return worker != null && _builders.Contains(worker);
    }

    public Vector3 GetBuildPosition(FactionNpc worker)
    {
        if (Faction == null || Faction.Simulation == null || Faction.Simulation.planet == null)
            return transform.position;
        Vector3 axis = (transform.position - Faction.Simulation.planet.transform.position).normalized;
        Vector3 reference = Mathf.Abs(Vector3.Dot(axis, Vector3.up)) > 0.9f
            ? Vector3.right
            : Vector3.up;
        Vector3 tangent = Vector3.Cross(axis, reference).normalized;
        int index = Mathf.Max(0, _builders.IndexOf(worker));
        float angle = index * Mathf.PI * 0.5f;
        Vector3 around = Quaternion.AngleAxis(angle * Mathf.Rad2Deg, axis) * tangent;
        float angularOffset = 3.5f / Mathf.Max(1f, Vector3.Distance(
            transform.position, Faction.Simulation.planet.transform.position));
        return Faction.Simulation.planet.GetSurfacePointWorld(
            (axis + around * angularOffset).normalized);
    }

    public void Cancel()
    {
        if (IsComplete)
            return;
        if (Faction != null)
        {
            Faction.ReleaseReservedCost(Cost);
            if (Faction.ActiveConstruction == this)
                Faction.ClearActiveConstruction(this);
        }
        Destroy(gameObject);
    }

    public void TickConstruction(float deltaTime)
    {
        if (IsComplete || !MaterialsDelivered)
            return;

        int activeBuilders = 0;
        for (int i = _builders.Count - 1; i >= 0; i--)
        {
            if (_builders[i] == null || _builders[i].IsDead)
                _builders.RemoveAt(i);
            else
                activeBuilders++;
        }
        if (activeBuilders == 0 && Faction != null && Stargrave.Rts2.Rts2UnitSim.HasInstance)
        {
            // Swarm workers do not stand on sites; auto-assign virtual builders from workforce.
            activeBuilders = Mathf.Clamp(Faction.WorkerCount / 2, 0, MaxBuilders);
            if (activeBuilders == 0 && Faction.WorkerCount > 0)
                activeBuilders = 1;
        }
        if (activeBuilders == 0)
            return;

        float diminishing = 1f + Mathf.Sqrt(Mathf.Max(0, activeBuilders - 1)) *
            Mathf.Clamp01(Faction.Economy.builderDiminishingReturns);
        ConstructionProgress += deltaTime * activeBuilders / diminishing;
        if (ConstructionProgress >= BuildSeconds)
            Complete();
    }

    void Update()
    {
        if (Faction == null || IsComplete)
            return;
        TickConstruction(Time.deltaTime * Faction.GrowthModifier);
        if (_visual != null)
        {
            float scale = Mathf.Lerp(0.25f, 1f, MaterialsDelivered
                ? Mathf.Clamp01(ConstructionProgress01)
                : 0.2f);
            _visual.transform.localScale = _visualFitScale * scale;
        }
    }

    void Complete()
    {
        IsComplete = true;
        ConstructionProgress = BuildSeconds;
        for (int i = _builders.Count - 1; i >= 0; i--)
            if (_builders[i] != null)
                _builders[i].Motor.Stop();
        _builders.Clear();

        if (Building != null)
            Building.SetOperational();
        if (Faction != null)
        {
            Faction.CommitReservedCost(Cost);
            Faction.NotifyBuildingCompleted(Building);
        }
        if (Faction != null && Faction.Simulation.verboseEvents)
            Debug.Log($"[FactionSimulation] {Faction.DisplayName} {Kind} completed.", this);
    }

    void FitVisualToBuilding(BuildingKind kind)
    {
        if (_visual == null)
            return;
        if (_visual.GetComponentsInChildren<Collider>(true).Length == 0)
        {
            MeshFilter[] meshes = _visual.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < meshes.Length; i++)
            {
                if (meshes[i] == null || meshes[i].sharedMesh == null)
                    continue;
                MeshCollider collider = meshes[i].gameObject.AddComponent<MeshCollider>();
                collider.sharedMesh = meshes[i].sharedMesh;
                collider.convex = false;
            }
        }
        BuildingSpawner.ApplyTownScale(_visual, SizeClassForKind(kind));
        _visualFitScale = _visual.transform.localScale;
    }

    public static BuildingSizeClass SizeClassForKind(BuildingKind kind)
    {
        switch (kind)
        {
            case BuildingKind.TownHall:
            case BuildingKind.Factory:
            case BuildingKind.EnergyGen:
                return BuildingSizeClass.Tall;
            default:
                return BuildingSizeClass.Short;
        }
    }
}

/// <summary>Dry-land, flat-site search and runtime building creation.</summary>
public static class BuildingPlacementSystem
{
    public static bool IsSuitableFactionAnchor(
        Planet planet,
        Vector3 axis,
        float flatRadius,
        float searchRadius)
    {
        if (planet == null)
            return false;
        var settings = new BuildingPadSiteEvaluator.Settings
        {
            flatRadius = Mathf.Max(1f, flatRadius),
            dryClearance = 1.25f,
            maxSlopeDegrees = 14f,
            maxHeightVariation = 3.5f,
            ringSamples = 10,
            searchAttempts = 24
        };
        if (!BuildingPadSiteEvaluator.Evaluate(planet, axis, settings).isValid)
            return false;
        Vector3 position = planet.GetSurfacePointWorld(axis);
        return !IsOverlappingResource(position, flatRadius * 1.5f) &&
               !IsOverlappingSolidObject(position, flatRadius * 1.5f);
    }

    public static bool IsDryMainlandCampus(Planet planet, Vector3 axis, float campusRadius = 100f)
    {
        if (planet == null || axis.sqrMagnitude < 1e-8f)
            return false;

        axis.Normalize();
        const float dryClearance = 1.25f;
        float waterLine = BuildingPadSiteEvaluator.ResolveWaterLine(planet, dryClearance);
        float centerR = planet.GetSurfaceRadiusWorld(axis);
        if (centerR < waterLine)
            return false;

        Vector3 tangent = Vector3.Cross(axis, Vector3.up);
        if (tangent.sqrMagnitude < 1e-6f)
            tangent = Vector3.Cross(axis, Vector3.right);
        tangent.Normalize();
        Vector3 bitangent = Vector3.Cross(axis, tangent);

        int dry = 1;
        int total = 1;
        const int ringSamples = 12;
        float inner = campusRadius * 0.5f;
        for (int ring = 0; ring < 2; ring++)
        {
            float distance = ring == 0 ? inner : campusRadius;
            float angular = distance / Mathf.Max(1e-3f, centerR);
            for (int i = 0; i < ringSamples; i++)
            {
                float angle = (i / (float)ringSamples) * Mathf.PI * 2f;
                Vector3 sample = (axis + (tangent * Mathf.Cos(angle) + bitangent * Mathf.Sin(angle)) * angular)
                    .normalized;
                total++;
                if (planet.GetSurfaceRadiusWorld(sample) >= waterLine)
                    dry++;
            }
        }

        return dry / (float)total >= 0.85f;
    }

    /// <summary>
    /// Rejects cliffy / broken ops rings that pass a tiny pad check but trap swarm gatherers.
    /// High alpine bowls fail this even when the town-hall footprint itself is flat.
    /// </summary>
    public static bool IsSwarmFriendlyOpsArea(Planet planet, Vector3 axis, float opsRadius = 75f)
    {
        if (planet == null || axis.sqrMagnitude < 1e-8f)
            return false;

        axis.Normalize();
        PlanetOceanLayer ocean = planet.GetComponent<PlanetOceanLayer>();
        float waterLine = BuildingPadSiteEvaluator.ResolveWaterLine(planet, 1.25f);
        float centerR = planet.GetSurfaceRadiusWorld(axis);
        if (centerR < waterLine)
            return false;
        if (IsSteepAxis(planet, axis, centerR))
            return false;

        Vector3 tangent = Vector3.Cross(axis, Vector3.up);
        if (tangent.sqrMagnitude < 1e-6f)
            tangent = Vector3.Cross(axis, Vector3.right);
        tangent.Normalize();
        Vector3 bitangent = Vector3.Cross(axis, tangent);

        int good = 0;
        int total = 0;
        float maxDelta = 0f;
        const int rings = 4;
        const int samplesPerRing = 12;
        for (int ring = 1; ring <= rings; ring++)
        {
            float distance = opsRadius * (ring / (float)rings);
            float angular = distance / Mathf.Max(1e-3f, centerR);
            for (int i = 0; i < samplesPerRing; i++)
            {
                float angle = (i / (float)samplesPerRing) * Mathf.PI * 2f;
                Vector3 sample = (axis + (tangent * Mathf.Cos(angle) + bitangent * Mathf.Sin(angle)) * angular)
                    .normalized;
                total++;
                float r = planet.GetSurfaceRadiusWorld(sample);
                maxDelta = Mathf.Max(maxDelta, Mathf.Abs(r - centerR));
                if (r < waterLine)
                    continue;
                if (IsSteepAxis(planet, sample, r))
                    continue;
                good++;
            }
        }

        // Big radial swings across the ops bubble ⇒ bowl/ridge fortress that traps haulers.
        if (maxDelta > opsRadius * 0.42f)
            return false;

        return total > 0 && good / (float)total >= 0.72f;
    }

    /// <summary>True when the pad sits in steep/bowl terrain that swarm workers struggle with.</summary>
    public static bool IsSteepOpsHome(Planet planet, Vector3 worldPos, float probeRadius = 40f)
    {
        if (planet == null)
            return false;
        Vector3 axis = (worldPos - planet.transform.position).normalized;
        return !IsSwarmFriendlyOpsArea(planet, axis, probeRadius);
    }

    static bool IsSteepAxis(Planet planet, Vector3 axis, float radiusAtAxis)
    {
        axis.Normalize();
        float r0 = radiusAtAxis > 1f ? radiusAtAxis : planet.GetSurfaceRadiusWorld(axis);
        Vector3 tangent = Vector3.Cross(axis, Mathf.Abs(Vector3.Dot(axis, Vector3.up)) > 0.9f
            ? Vector3.right
            : Vector3.up).normalized;
        float angular = 2.5f / Mathf.Max(1f, r0);
        Vector3 a1 = (axis + tangent * angular).normalized;
        Vector3 a2 = (axis - tangent * angular).normalized;
        float r1 = planet.GetSurfaceRadiusWorld(a1);
        float r2 = planet.GetSurfaceRadiusWorld(a2);
        float chord = angular * r0;
        float slope = Mathf.Max(Mathf.Abs(r1 - r0), Mathf.Abs(r2 - r0)) / Mathf.Max(0.01f, chord);
        // Was 1.2 (~50°); tighter so alpine pads are rejected at spawn.
        return slope > 0.95f;
    }

    public static bool TryCreateConstructionSite(
        FactionController faction,
        BuildingKind kind,
        GameObject prefab,
        Vector3 preferredAxis,
        float flatRadius,
        float blendWidth,
        FactionResourceCost cost,
        float buildSeconds,
        int maxBuilders,
        out BuildingConstructionSite site,
        int siteSearchAttempts = 72,
        float maxDistanceFromPreferred = 0f,
        bool allowDryFallback = false,
        bool rebuildAtPreferred = false)
    {
        site = null;
        if (faction == null || faction.Simulation == null || faction.Simulation.planet == null)
            return false;

        Planet planet = faction.Simulation.planet;
        Vector3 axis;
        if (rebuildAtPreferred)
        {
            axis = preferredAxis.sqrMagnitude > 1e-8f ? preferredAxis.normalized : Vector3.up;
        }
        else
        {
            var settings = new BuildingPadSiteEvaluator.Settings
            {
                flatRadius = flatRadius,
                dryClearance = 1.25f,
                maxSlopeDegrees = 14f,
                maxHeightVariation = 3.5f,
                ringSamples = 12,
                searchAttempts = Mathf.Max(1, siteSearchAttempts)
            };
            if (!BuildingPadSiteEvaluator.TryFindSuitableSite(
                    planet, preferredAxis, settings, out axis, out var report) ||
                !report.isValid)
            {
                if (!allowDryFallback)
                    return false;
                axis = preferredAxis.sqrMagnitude > 1e-8f ? preferredAxis.normalized : Vector3.up;
            }
        }

        Vector3 position = planet.GetSurfacePointWorld(axis);
        if (!rebuildAtPreferred)
        {
            PlanetOceanLayer ocean = planet.GetComponent<PlanetOceanLayer>();
            if (ocean != null && ocean.GetDepthBelowSurface(position) > 0f)
                return false;
        }
        if (maxDistanceFromPreferred > 0f)
        {
            Vector3 preferredPos = planet.GetSurfacePointWorld(
                preferredAxis.sqrMagnitude > 1e-8f ? preferredAxis.normalized : Vector3.up);
            if (Vector3.Distance(position, preferredPos) > maxDistanceFromPreferred)
                return false;
        }
        if (IsOverlappingExistingStructure(faction, position, flatRadius, kind))
            return false;

        var root = new GameObject($"{faction.DisplayName}_{kind}_ConstructionSite");
        root.transform.SetParent(faction.transform, false);
        root.transform.position = position;
        root.transform.rotation = Quaternion.FromToRotation(Vector3.up, axis);

        var pad = root.AddComponent<BuildingPad>();
        pad.flatRadius = flatRadius;
        pad.blendWidth = blendWidth;
        pad.requireSuitableSite = !rebuildAtPreferred;
        pad.skipBakeIfUnsuitable = !rebuildAtPreferred;
        pad.suppressFoliage = true;
        pad.ApplyPoseOnAxis(planet, axis);

        site = root.AddComponent<BuildingConstructionSite>();
        site.Configure(faction, kind, prefab, cost, buildSeconds, maxBuilders);
        faction.FeedConstructionFromStock(site);
        PlanetBuildingPads.BakeFromScene(planet);
        // Apply the existing hybrid pad deformation once for this structural change. This is
        // deliberately outside worker ticks so terrain generation is never performed per frame.
        PlanetBuildingPads.RegeneratePlanetWithPads();
        if (faction.Simulation.verboseEvents)
            Debug.Log($"[FactionSimulation] {faction.DisplayName} {kind} site placed.", root);
        return true;
    }

    public static bool TryCreateNearTownHall(
        FactionController faction,
        BuildingKind kind,
        GameObject prefab,
        float minDistance,
        float maxDistance,
        float flatRadius,
        float blendWidth,
        FactionResourceCost cost,
        float buildSeconds,
        int maxBuilders,
        out BuildingConstructionSite site)
    {
        site = null;
        if (faction == null || faction.TownHall == null || faction.Simulation == null ||
            faction.Simulation.planet == null)
            return false;

        Planet planet = faction.Simulation.planet;
        Vector3 hallPos = faction.TownHall.transform.position;
        Vector3 axis = (hallPos - planet.transform.position);
        axis = axis.sqrMagnitude > 1e-6f ? axis.normalized : faction.SpawnAxis;

        float street = 3f;
        float lotDistance = faction.Economy.townHallFlatRadius + flatRadius + street;
        minDistance = Mathf.Min(minDistance, Mathf.Max(4f, lotDistance - 2f));
        maxDistance = Mathf.Max(maxDistance, lotDistance + 12f);
        lotDistance = Mathf.Clamp(lotDistance, minDistance, maxDistance);

        const int slots = 8;
        const int rings = 8;
        for (int ring = 0; ring < rings; ring++)
        {
            float dist = lotDistance + ring * 2f;
            if (dist > maxDistance + 1f)
                break;
            for (int slot = 0; slot < slots; slot++)
            {
                float yaw = slot * (Mathf.PI * 2f / slots);
                Vector3 candidate = OffsetOnSurface(planet, axis, yaw, dist);
                if (!TryCreateConstructionSite(
                        faction, kind, prefab, candidate, flatRadius, blendWidth,
                        cost, buildSeconds, maxBuilders, out site, 16, maxDistance + 8f))
                    continue;
                return true;
            }
        }
        return false;
    }

    static bool IsOverlappingExistingStructure(
        FactionController faction,
        Vector3 position,
        float padRadius,
        BuildingKind kind)
    {
        for (int i = 0; i < FactionRegistry.Factions.Count; i++)
        {
            FactionController other = FactionRegistry.Factions[i];
            if (other == null)
                continue;
            if (other.TownHall != null && other.TownHall.IsOperational)
            {
                float hallDistance = Vector3.Distance(other.TownHall.transform.position, position);
                float clearance = other == faction &&
                    (kind == BuildingKind.Barracks || kind == BuildingKind.Market || kind == BuildingKind.Mint)
                    ? other.Economy.townHallFlatRadius + 2f
                    : padRadius * 1.5f + 12f;
                if (hallDistance < clearance)
                    return true;
            }
            if (other.Barracks != null && other.Barracks.IsOperational)
            {
                if (other == faction && kind == BuildingKind.TownHall)
                    continue;
                if (Vector3.Distance(other.Barracks.transform.position, position) < padRadius + 8f)
                    return true;
            }
            if (other.Market != null && other.Market.IsOperational)
            {
                if (other == faction && kind == BuildingKind.TownHall)
                    continue;
                if (Vector3.Distance(other.Market.transform.position, position) < padRadius + 8f)
                    return true;
            }
            if (other.Mint != null && other.Mint.IsOperational)
            {
                if (other == faction && kind == BuildingKind.TownHall)
                    continue;
                if (Vector3.Distance(other.Mint.transform.position, position) < padRadius + 8f)
                    return true;
            }
            if (other.ActiveConstruction != null &&
                Vector3.Distance(other.ActiveConstruction.transform.position, position) < padRadius + 8f)
                return true;
        }
        return false;
    }

    static bool IsOverlappingResource(Vector3 position, float radius)
    {
        float radiusSq = radius * radius;
        for (int i = 0; i < FactionRegistry.Resources.Count; i++)
        {
            ResourceNode node = FactionRegistry.Resources[i];
            if (node != null && (node.transform.position - position).sqrMagnitude < radiusSq)
                return true;
        }
        return false;
    }

    static bool IsOverlappingSolidObject(Vector3 position, float radius)
    {
        Collider[] hits = Physics.OverlapSphere(position, radius, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits.Length; i++)
        {
            Collider hit = hits[i];
            if (hit == null)
                continue;
            if (hit.GetComponentInParent<Planet>() != null)
                continue;
            if (hit.GetComponentInParent<FactionNpc>() != null)
                continue;
            return true;
        }
        return false;
    }

    static Vector3 OffsetOnSurface(Planet planet, Vector3 axis, float yawRadians, float worldDistance)
    {
        axis = axis.sqrMagnitude > 1e-6f ? axis.normalized : Vector3.up;
        Vector3 reference = Mathf.Abs(Vector3.Dot(axis, Vector3.up)) > 0.9f
            ? Vector3.right
            : Vector3.up;
        Vector3 tangent = Vector3.Cross(axis, reference).normalized;
        Vector3 other = Vector3.Cross(axis, tangent).normalized;
        Vector3 heading = tangent * Mathf.Cos(yawRadians) + other * Mathf.Sin(yawRadians);
        float radius = Mathf.Max(1f, planet.GetSurfaceRadiusWorld(axis));
        float ang = worldDistance / radius;
        return (axis * Mathf.Cos(ang) + heading * Mathf.Sin(ang)).normalized;
    }
}
