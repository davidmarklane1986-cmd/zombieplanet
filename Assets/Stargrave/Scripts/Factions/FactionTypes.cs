using System;
using UnityEngine;

public enum FactionState
{
    Economy,
    BuildingArmy,
    Attacking,
    Fighting,
    Retreating,
    Recovering,
    Expanding
}

public enum FactionResourceType
{
    Wood,
    Stone
}

public enum FactionNpcRole
{
    ResourceGatherer,
    Soldier,
    Merchant
}

[Serializable]
public struct FactionResourceCost
{
    [Min(0)] public int wood;
    [Min(0)] public int stone;

    public FactionResourceCost(int wood, int stone)
    {
        this.wood = Mathf.Max(0, wood);
        this.stone = Mathf.Max(0, stone);
    }
}

[Serializable]
public sealed class FactionEconomySettings
{
    [Header("Starting economy")]
    [Min(0)] public int startingWood = 0;
    [Min(0)] public int startingStone = 0;
    [Min(0)] public int startingGold = 40;
    [Tooltip("Starting workers.")]
    [Min(1)] public int startingWorkers = 6;
    [Min(1)] public int workerMaxCount = 12;
    [Min(1)] public int workerCarryCapacity = 10;

    [Header("Town Hall")]
    public FactionResourceCost townHallCost = new FactionResourceCost(40, 20);
    [Min(1f)] public float townHallBuildSeconds = 60f;
    [Min(1)] public int townHallMaxBuilders = 4;
    [Min(1f)] public float townHallFlatRadius = 12f;
    [Min(0.5f)] public float townHallBlendWidth = 12f;

    [Header("Barracks")]
    public FactionResourceCost barracksCost = new FactionResourceCost(50, 30);
    [Min(1f)] public float barracksBuildSeconds = 45f;
    [Min(1)] public int barracksMaxBuilders = 3;
    [Min(1f)] public float barracksFlatRadius = 4.5f;
    [Min(0.5f)] public float barracksBlendWidth = 8f;
    [Tooltip("Closest lot distance from Town Hall center (village ring).")]
    [Min(4f)] public float barracksMinDistance = 14f;
    [Tooltip("Farthest fallback lot if the inner ring is blocked.")]
    [Min(4f)] public float barracksMaxDistance = 22f;

    [Header("Market")]
    public FactionResourceCost marketCost = new FactionResourceCost(40, 25);
    [Min(1f)] public float marketBuildSeconds = 40f;
    [Min(1)] public int marketMaxBuilders = 3;
    [Min(1f)] public float marketFlatRadius = 4.5f;
    [Min(0.5f)] public float marketBlendWidth = 8f;
    [Min(4f)] public float marketMinDistance = 16f;
    [Min(4f)] public float marketMaxDistance = 26f;

    [Header("Trade")]
    [Min(0.1f)] public float woodGoldPrice = 2f;
    [Min(0.1f)] public float stoneGoldPrice = 3f;
    [Min(1)] public int tradeLotSize = 5;
    [Min(0)] public int tradeReserveWood = 30;
    [Min(0)] public int tradeReserveStone = 20;
    [Range(0f, 1f)] public float tradeRelationStart = 0.15f;
    [Range(0f, 1f)] public float tradeRelationGain = 0.08f;
    [Range(0f, 1f)] public float attackRelationPenalty = 0.25f;
    [Range(0f, 1f)] public float damageRelationPenalty = 0.02f;
    [Range(0.01f, 1f)] public float attackChanceFloor = 0.08f;
    [Range(0.5f, 2f)] public float priceGapInfluence = 0.35f;

    [Header("Merchant production")]
    public FactionResourceCost merchantCost = new FactionResourceCost(8, 4);
    [Min(0)] public int merchantGoldCost = 5;
    [Min(1f)] public float merchantTrainingSeconds = 15f;
    [Min(1)] public int merchantMaxCount = 3;
    [Min(1)] public int merchantCarryCapacity = 10;

    [Header("Soldier production")]
    public FactionResourceCost soldierCost = new FactionResourceCost(20, 10);
    [Min(1f)] public float soldierTrainingSeconds = 20f;
    [Min(1)] public int soldierMaxCount = 40;
    [Min(0.1f)] public float desiredSoldierPerWorker = 1.5f;

    [Header("Worker production")]
    public FactionResourceCost workerCost = new FactionResourceCost(10, 5);
    [Min(1f)] public float workerTrainingSeconds = 12f;

    [Header("Growth")]
    [Min(0.05f)] public float economyDecisionInterval = 1f;
    [Min(0.05f)] public float workerDecisionInterval = 0.5f;
    [Min(0.05f)] public float combatDecisionInterval = 0.25f;
    [Min(0.05f)] public float constructionDecisionInterval = 0.25f;
    [Min(0.01f)] public float gatherPerSecond = 1f;
    [Range(0f, 1f)] public float builderDiminishingReturns = 0.55f;
}

[Serializable]
public sealed class FactionCombatSettings
{
    [Header("NPC combat")]
    [Min(1)] public int soldierMaxHealth = 100;
    [Min(1)] public int soldierDamage = 12;
    [Min(0.1f)] public float soldierAttackRange = 2.2f;
    [Min(1f)] public float soldierShootRange = 22f;
    [Min(0.05f)] public float soldierAttackCooldown = 1f;
    [Min(0.1f)] public float soldierMoveSpeed = 4.5f;
    [Min(0.1f)] public float workerMoveSpeed = 3.7f;
    [Min(0.1f)] public float npcSwimSpeed = 5f;
    [Min(1)] public int workerDamage = 8;
    [Min(0.1f)] public float workerAttackRange = 2.2f;
    [Min(0.05f)] public float workerAttackCooldown = 1.1f;
    [Min(1f)] public float workerFightRadius = 8f;
    [Min(1)] public int workerFleeZombieCount = 2;
    [Range(0.05f, 1f)] public float workerFleeHealth = 0.35f;
    [Min(0.4f)] public float npcSeparationRadius = 0.85f;
    [Min(1f)] public float zombieThreatRadius = 24f;
    [Min(1f)] public float workerFleeRadius = 28f;
    [Min(1f)] public float workerWanderRadius = 40f;
    [Min(0.5f)] public float workerWanderInterval = 4f;
    [Min(5f)] public float workerResourceSearchRadius = 90f;
    [Min(10f)] public float workerFoliageRadius = 140f;
    [Min(20f)] public float workerBiomeScoutRadius = 180f;
    [Range(0.05f, 1f)] public float soldierRetreatHealth = 0.25f;
    [Range(0.05f, 1f)] public float soldierFightHealth = 0.35f;
    [Min(1)] public int workerSafeSoldierAdvantage = 1;
    [Min(0.1f)] public float attackFormationRadius = 12f;
    [Min(5f)] public float soldierDefendRadius = 80f;
    [Min(10f)] public float soldierPatrolRadius = 32f;
    [Min(10f)] public float soldierEngageRadius = 40f;
    [Min(1)] public int soldierScoutCount = 3;
    [Min(2)] public int soldierSquadSize = 4;
}

[Serializable]
public sealed class FactionWarfareSettings
{
    [Header("Engagement")]
    [Tooltip("Preferred first-wave size. Attacks may fire below or above this.")]
    [Min(1)] public int minimumSoldiersForWar = 10;
    [Tooltip("Fewest soldiers a faction will send when proposing a fight.")]
    [Min(1)] public int minSoldiersToPropose = 6;
    [Tooltip("Fewest soldiers a faction needs before it will accept a fight.")]
    [Min(1)] public int minSoldiersToAcceptFight = 5;
    [Tooltip("Upper cap when waiting for extra troops after a failed wave.")]
    [Min(1)] public int maxMusterWaitSoldiers = 16;
    [Min(0)] public int extraSoldiersAfterDefeat = 3;
    [Min(1f)] public float musterPatienceSeconds = 18f;
    [Range(0.4f, 1f)] public float attackIfOutnumberedFraction = 0.7f;
    [Min(1f)] public float approachFlankDistance = 22f;
    [Min(1f)] public float minimumEngagementStrength = 500f;
    [Range(0f, 1f)] public float strengthBalanceBuffer = 0.2f;
    [Range(0f, 1f)] public float maximumStrongFactionSlowdown = 0.5f;
    [Min(1f)] public float attackStrengthMultiplier = 1.25f;
    [Range(0.1f, 1f)] public float retreatLossFraction = 0.5f;
    [Range(0.1f, 1f)] public float recoveryStrengthFraction = 0.75f;
    [Min(1f)] public float attackReplanSeconds = 10f;

    [Header("Strength weights")]
    [Min(0f)] public float soldierStrength = 100f;
    [Min(0f)] public float workerStrength = 10f;
    [Min(0f)] public float townHallStrength = 200f;
    [Min(0f)] public float barracksStrength = 100f;
    [Min(0f)] public float marketStrength = 80f;
    [Min(0f)] public float resourceStrengthPerUnit = 0.1f;
}

public struct FactionStrengthBreakdown
{
    public int livingWorkers;
    public int livingSoldiers;
    public float soldierContribution;
    public float workerContribution;
    public float buildingContribution;
    public float resourceContribution;
    public float total;
}

public interface IFactionDamageable
{
    bool IsFactionTargetable { get; }
    FactionController OwningFaction { get; }
    Transform TargetTransform { get; }
    int CurrentHealth { get; }
    void TakeFactionDamage(int amount, Transform attacker);
}
