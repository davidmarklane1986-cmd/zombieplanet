using UnityEngine;

/// <summary>
/// Spectator BAR/Zero-K director: mex → energy → factory → market → pocket expand; assault when raiders ready.
/// Metal uses the wood pool; energy uses the stone pool so existing construction delivery works.
/// </summary>
public static class BarFactionDirector
{
    public static bool IsEnabled(FactionController faction) =>
        faction != null &&
        faction.Simulation != null &&
        faction.Simulation.useBarEconomyMode;

    public static void ApplyStartingStock(FactionController faction)
    {
        if (faction == null || faction.Economy == null)
            return;
        faction.SetBarStartingStock(
            faction.Economy.startingMetal,
            faction.Economy.startingEnergy);
    }

    public static void TickIncome(FactionController faction, float deltaTime)
    {
        if (!IsEnabled(faction) || deltaTime <= 0f || faction.Economy == null)
            return;
        float metal = faction.MexCount * faction.Economy.mexMetalPerSecond * deltaTime;
        if (TerritorySystem.HasInstance)
            metal += TerritorySystem.Instance.PocketIncomeBonus(faction) * deltaTime;
        float energy = faction.EnergyGenCount * faction.Economy.energyGenPerSecond * deltaTime;
        if (metal > 0f)
            faction.AddBarMetal(metal);
        if (energy > 0f)
            faction.AddBarEnergy(energy);
    }

    public static bool TryStartNextConstruction(FactionController faction)
    {
        if (!IsEnabled(faction) || !faction.HasFoundedCampus)
            return false;
        if (faction.ActiveConstruction != null)
            return true;
        if (faction.TownHall == null)
            return false; // HQ rebuild still uses village town-hall path

        FactionEconomySettings eco = faction.Economy;
        FactionPersonality personality = faction.Personality;
        int mexTarget = personality.MexTarget(eco);
        int energyTarget = personality.EnergyTarget(eco);
        bool factoryRush = personality.buildBias == FactionBuildBias.FactoryRush;

        // Factory-rush: open with minimal eco then factory, else mex → energy → factory.
        if (factoryRush)
        {
            if (faction.MexCount < Mathf.Max(1, mexTarget))
                return TryPlace(faction, BuildingKind.Mex, eco.mexCost, eco.mexBuildSeconds);
            if (faction.EnergyGenCount < Mathf.Max(1, energyTarget))
                return TryPlace(faction, BuildingKind.EnergyGen, eco.energyGenCost, eco.energyGenBuildSeconds);
            if (faction.Factory == null)
                return TryPlace(faction, BuildingKind.Factory, eco.factoryCost, eco.factoryBuildSeconds);
        }
        else
        {
            if (faction.MexCount < mexTarget)
                return TryPlace(faction, BuildingKind.Mex, eco.mexCost, eco.mexBuildSeconds);
            if (faction.EnergyGenCount < energyTarget)
                return TryPlace(faction, BuildingKind.EnergyGen, eco.energyGenCost, eco.energyGenBuildSeconds);
            if (faction.Factory == null)
                return TryPlace(faction, BuildingKind.Factory, eco.factoryCost, eco.factoryBuildSeconds);
        }

        // Market enables merchants / town claim support.
        if (faction.Market == null)
            return TryPlace(faction, BuildingKind.Market, eco.marketCost, eco.marketBuildSeconds);

        // Expand onto unowned metal pockets (territory race).
        if (TryPlaceMexOnPocket(faction))
            return true;

        // Fallback campus eco.
        if (faction.MexCount < mexTarget + 2)
            return TryPlace(faction, BuildingKind.Mex, eco.mexCost, eco.mexBuildSeconds);
        if (faction.EnergyGenCount < energyTarget + 1)
            return TryPlace(faction, BuildingKind.EnergyGen, eco.energyGenCost, eco.energyGenBuildSeconds);
        return false;
    }

    static bool TryPlaceMexOnPocket(FactionController faction)
    {
        if (!TerritorySystem.HasInstance || !TerritorySystem.Instance.IsReady)
            return false;
        FactionEconomySettings eco = faction.Economy;
        if (!faction.HasResources(eco.mexCost))
            return false;
        if (!TerritorySystem.Instance.TryFindBestPocketFor(faction, out int pocketIndex))
            return false;
        if (!TerritorySystem.Instance.TryGetPocket(pocketIndex, out TerritorySystem.Pocket pocket))
            return false;

        if (!BuildingPlacementSystem.TryCreateConstructionSite(
                faction,
                BuildingKind.Mex,
                faction.Simulation != null ? faction.Simulation.GetBuildingPrefab(BuildingKind.Mex) : null,
                pocket.axis,
                eco.barBuildingFlatRadius,
                eco.barBuildingBlendWidth,
                eco.mexCost,
                eco.mexBuildSeconds,
                Mathf.Max(1, eco.townHallMaxBuilders),
                out BuildingConstructionSite site,
                siteSearchAttempts: 48,
                maxDistanceFromPreferred: eco.territoryPocketClaimRadius,
                allowDryFallback: true))
            return false;

        if (!TerritorySystem.Instance.TryReservePocket(pocketIndex, faction))
        {
            // Extremely rare race; abandon site so another faction isn't blocked forever.
            if (site != null)
                Object.Destroy(site.gameObject);
            return false;
        }

        if (site.Building is Mex mex)
            mex.LinkedPocketId = pocket.id;

        faction.BeginConstruction(site);
        if (faction.Simulation != null && faction.Simulation.verboseEvents)
            Debug.Log($"[BAR] {faction.DisplayName} expanding mex onto pocket {pocket.id}.", faction);
        return true;
    }

    static bool TryPlace(
        FactionController faction,
        BuildingKind kind,
        FactionResourceCost cost,
        float buildSeconds)
    {
        if (!faction.HasResources(cost))
            return false;

        FactionEconomySettings eco = faction.Economy;
        GameObject prefab = faction.Simulation != null
            ? faction.Simulation.GetBuildingPrefab(kind)
            : null;
        if (!BuildingPlacementSystem.TryCreateNearTownHall(
                faction,
                kind,
                prefab,
                eco.barBuildMinDistance,
                eco.barBuildMaxDistance,
                eco.barBuildingFlatRadius,
                eco.barBuildingBlendWidth,
                cost,
                buildSeconds,
                Mathf.Max(1, eco.townHallMaxBuilders),
                out BuildingConstructionSite site))
            return false;

        faction.BeginConstruction(site);
        return true;
    }

    /// <summary>
    /// Continuous worker production while HQ is up and WorkerCount &lt; startingWorkers.
    /// Paid with workerCost; if at 0 workers and broke, allow one free spawn.
    /// </summary>
    public static void TickWorkerProduction(FactionController faction, float deltaTime)
    {
        if (!IsEnabled(faction) || faction.Economy == null || deltaTime <= 0f)
            return;
        if (faction.TownHall == null || !faction.TownHall.IsOperational)
            return;
        if (!Stargrave.Rts2.Rts2UnitSim.HasInstance)
            return;

        FactionEconomySettings eco = faction.Economy;
        int target = Mathf.Clamp(eco.startingWorkers, 1, eco.workerMaxCount);
        if (faction.WorkerCount >= target)
        {
            faction.WorkerBuildTimer = 0f;
            return;
        }

        faction.WorkerBuildTimer += deltaTime;
        float need = Mathf.Max(0.5f, eco.workerTrainingSeconds);
        while (faction.WorkerBuildTimer >= need && faction.WorkerCount < target)
        {
            bool canPay = faction.HasResources(eco.workerCost);
            bool freeRescue = !canPay && faction.WorkerCount == 0;
            if (!canPay && !freeRescue)
                break;

            if (canPay && !faction.TrySpendAvailableCost(eco.workerCost))
                break;

            if (!faction.TrySpawnWorkerAtBase(free: freeRescue))
            {
                // Refund if we spent but failed to spawn.
                if (canPay && !freeRescue)
                {
                    faction.AddBarMetal(eco.workerCost.wood);
                    faction.AddBarEnergy(eco.workerCost.stone);
                }
                break;
            }

            faction.WorkerBuildTimer -= need;
            if (freeRescue)
                break; // one free rescue per empty-crew episode
        }
    }

    public static void TickCombatPressure(FactionController faction)
    {
        if (!IsEnabled(faction) || faction.Economy == null)
            return;
        if (faction.State == FactionState.Attacking ||
            faction.State == FactionState.Fighting ||
            faction.State == FactionState.Retreating ||
            faction.State == FactionState.Recovering)
            return;
        if (!faction.AttackReplanReady)
            return;
        if (!CanLaunchAssault(faction))
            return;

        FactionController best = null;
        float bestScore = float.PositiveInfinity;
        Vector3 home = faction.GetSafePosition();
        var factions = FactionRegistry.Factions;
        for (int i = 0; i < factions.Count; i++)
        {
            FactionController other = factions[i];
            if (!IsFairAssaultTarget(faction, other))
                continue;

            int ours = Mathf.Max(1, faction.SoldierCount);
            int theirs = Mathf.Max(1, other.SoldierCount);
            float ratio = ours / (float)theirs;
            // Prefer even fights; break ties by distance.
            float fairness = Mathf.Abs(Mathf.Log(ratio));
            float distSq = (other.GetSafePosition() - home).sqrMagnitude;
            float score = fairness * 1e6f + distSq;
            if (score < bestScore)
            {
                bestScore = score;
                best = other;
            }
        }

        if (best != null)
            faction.BeginAttack(best);
    }

    static bool CanLaunchAssault(FactionController faction)
    {
        FactionEconomySettings eco = faction.Economy;
        FactionPersonality personality = faction.Personality;
        if (faction.IsInBarAssaultGrace)
            return false;
        if (!faction.IsBarWarReady)
            return false;
        // Finish the opening eco before rushing (personality-scaled targets).
        if (faction.MexCount < personality.MexTarget(eco) ||
            faction.EnergyGenCount < personality.EnergyTarget(eco))
            return false;
        if (faction.SoldierCount < personality.AssaultThreshold(eco))
            return false;
        return true;
    }

    static bool IsFairAssaultTarget(FactionController attacker, FactionController other)
    {
        if (other == null || other == attacker || !other.HasFoundedCampus)
            return false;
        if (other.IsBarAssaultProtected)
            return false;
        if (!other.IsBarWarReady)
            return false;
        if (FactionBalanceSystem.AreCoalitionAllies(attacker, other))
            return false;

        FactionEconomySettings eco = attacker.Economy;
        int ours = Mathf.Max(1, attacker.SoldierCount);
        int theirs = Mathf.Max(1, other.SoldierCount);
        float ratio = ours / (float)theirs;
        float minRatio = Mathf.Clamp(eco.barAssaultMinStrengthRatio, 0.2f, 1f);
        float maxRatio = attacker.Personality.MaxStrengthRatio(eco);
        // Let weak factions grow until they are a real threat; don't stomp or suicide.
        if (ratio > maxRatio && !FactionBalanceSystem.IsCoalitionTarget(other))
            return false;
        if (ratio < minRatio)
            return false;
        return true;
    }
}
