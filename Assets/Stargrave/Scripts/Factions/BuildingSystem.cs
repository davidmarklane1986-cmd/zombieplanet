using System.Collections.Generic;
using UnityEngine;

public enum BuildingKind
{
    TownHall,
    Barracks
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

public sealed class Barracks : Building
{
    enum TrainKind
    {
        None,
        ResourceGatherer,
        Soldier
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

        float duration = _queuedKind == TrainKind.ResourceGatherer
            ? Faction.Economy.workerTrainingSeconds
            : Faction.Economy.soldierTrainingSeconds;
        _trainingTimer += deltaTime * modifier;
        if (_trainingTimer < duration)
            return;

        _trainingTimer = 0f;
        _queued--;
        TrainKind kind = _queuedKind;
        _queuedKind = TrainKind.None;
        SpawnTrainedUnit(kind);
    }

    void TryQueueUnit()
    {
        int workerTarget = Faction.State == FactionState.Recovering
            ? Faction.Economy.startingWorkers
            : Faction.Economy.workerMaxCount;
        if (Faction.WorkerCount < workerTarget &&
            Faction.HasResources(Faction.Economy.workerCost) &&
            Faction.TrySpendAvailableCost(Faction.Economy.workerCost))
        {
            _queued = 1;
            _queuedKind = TrainKind.ResourceGatherer;
            _trainingTimer = 0f;
            return;
        }

        if (Faction.State == FactionState.Recovering &&
            Faction.WorkerCount < Faction.Economy.startingWorkers)
            return;

        if (Faction.SoldierCount < Faction.Economy.soldierMaxCount &&
            Faction.HasResources(Faction.Economy.soldierCost) &&
            Faction.TrySpendAvailableCost(Faction.Economy.soldierCost))
        {
            _queued = 1;
            _queuedKind = TrainKind.Soldier;
            _trainingTimer = 0f;
        }
    }

    void SpawnTrainedUnit(TrainKind kind)
    {
        if (kind == TrainKind.None || Faction.Simulation.planet == null)
            return;
        if (kind == TrainKind.ResourceGatherer &&
            Faction.WorkerCount >= Faction.Economy.workerMaxCount)
            return;
        if (kind == TrainKind.Soldier &&
            Faction.SoldierCount >= Faction.Economy.soldierMaxCount)
            return;

        Vector3 axis = (transform.position - Faction.Simulation.planet.transform.position).normalized;
        Vector3 tangent = Vector3.Cross(axis, Mathf.Abs(Vector3.Dot(axis, Vector3.up)) > 0.9f
            ? Vector3.right
            : Vector3.up).normalized;
        axis = (axis + Quaternion.AngleAxis(Random.Range(0f, 360f), axis) *
            tangent * 0.004f).normalized;

        FactionNpc npc = kind == TrainKind.ResourceGatherer
            ? FactionNpc.CreateResourceGatherer(Faction, axis)
            : FactionNpc.CreateSoldier(Faction, axis);
        if (npc == null)
            return;

        Faction.RegisterNpc(npc);
        if (Faction.Simulation.verboseEvents)
        {
            string label = kind == TrainKind.ResourceGatherer ? "worker" : "soldier";
            Debug.Log($"[FactionSimulation] {Faction.DisplayName} trained a {label}.", Faction);
        }
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
            _visual.transform.localPosition = new Vector3(0f, kind == BuildingKind.TownHall ? 6f : 2.5f, 0f);
            _visual.transform.localScale = kind == BuildingKind.TownHall
                ? new Vector3(6f, 12f, 6f)
                : new Vector3(5f, 5f, 5f);
            Collider col = _visual.GetComponent<Collider>();
            if (col != null)
                Object.Destroy(col);
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
        if (IsComplete || !MaterialsDelivered || _builders.Count == 0)
            return;

        int activeBuilders = 0;
        for (int i = _builders.Count - 1; i >= 0; i--)
        {
            if (_builders[i] == null || _builders[i].IsDead)
                _builders.RemoveAt(i);
            else
                activeBuilders++;
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
            _visual.transform.localScale = Vector3.one * scale;
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
        Bounds bounds;
        Renderer[] renderers = _visual.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
            return;
        bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            bounds.Encapsulate(renderers[i].bounds);
        float targetHeight = kind == BuildingKind.TownHall ? 24f : 10f;
        if (bounds.size.y > 0.01f)
            _visual.transform.localScale *= targetHeight / bounds.size.y;
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
                        faction, BuildingKind.Barracks, prefab, candidate, flatRadius, blendWidth,
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
                float clearance = other == faction && kind == BuildingKind.Barracks
                    ? other.Economy.townHallFlatRadius + 2f
                    : padRadius * 1.5f + 12f;
                if (hallDistance < clearance)
                    return true;
            }
            if (other.Barracks != null && other.Barracks.IsOperational)
            {
                // Own barracks sits on the village ring; it must not block rebuilding the hall.
                if (other == faction && kind == BuildingKind.TownHall)
                    continue;
                if (Vector3.Distance(other.Barracks.transform.position, position) < padRadius + 8f)
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
