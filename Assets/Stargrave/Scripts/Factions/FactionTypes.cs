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
    Expanding,
    Claiming
}

public enum FactionResourceType
{
    Wood,
    Stone
}

public enum FactionNpcRole
{
    ResourceGatherer,
    Infantry,
    Archer,
    Merchant,
    Noble
}

public static class FactionNpcRoles
{
    public static bool IsCombatSoldier(FactionNpcRole role)
    {
        return role == FactionNpcRole.Infantry || role == FactionNpcRole.Archer;
    }
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
    [Min(0)] public int startingWood = 60;
    [Min(0)] public int startingStone = 30;
    [Min(0)] public int startingGold = 40;
    [Tooltip("Starting workers.")]
    [Min(1)] public int startingWorkers = 6;
    [Min(1)] public int workerMaxCount = 12;
    [Tooltip("Train soldiers once workers reach this count, before filling to workerMaxCount.")]
    [Min(1)] public int earlyWorkerTarget = 8;
    [Tooltip("Minimum soldiers to raise before maxing workers.")]
    [Min(0)] public int garrisonBeforeMaxWorkers = 5;
    [Min(1)] public int workerCarryCapacity = 10;

    [Header("Scattered founding")]
    [Tooltip("Workers march to a pre-assigned separated site; first arrival founds (recommended).")]
    public bool useDesignatedSiteFounding = true;
    [Tooltip("Worker must reach within this distance of the designated site to found.")]
    [Min(4f)] public float designatedSiteClaimRadius = 22f;
    [Tooltip("Seconds between roam repath picks for unfounded workers (legacy free-roam).")]
    [Min(1f)] public float roamRepathSeconds = 8f;
    [Tooltip("Same-faction roaming workers within this distance found a campus (legacy meet founding).")]
    [Min(2f)] public float workerMeetRadius = 50f;
    [Tooltip("Player must be within this distance and press Interact to found a campus.")]
    [Min(1f)] public float playerInteractRadius = 4.5f;
    [Tooltip("Refuse founding if this close to another faction's existing campus.")]
    [Min(20f)] public float foundingMinSeparationFromRivalBases = 280f;
    [Tooltip("If still unfounded after this many seconds, reassign a farther designated site.")]
    [Min(30f)] public float designatedSiteStallSeconds = 100f;
    [Tooltip("If founded but Town Hall never finishes within this many seconds, relocate the campus.")]
    [Min(30f)] public float baseBuildStallSeconds = 90f;
    [Tooltip("If still unfounded after a per-faction random delay, force-pick a far campus.")]
    public bool enableForcedFoundingFallback = true;
    [Tooltip("Minimum seconds without a campus before force-picking one.")]
    [Min(30f)] public float foundingForceAfterSecondsMin = 180f;
    [Tooltip("Maximum seconds without a campus before force-picking one (rolled per faction).")]
    [Min(30f)] public float foundingForceAfterSecondsMax = 300f;
    [Tooltip("BAR mode: force-found window if workers never arrive (travel-aware).")]
    [Min(15f)] public float barFoundingForceAfterSecondsMin = 60f;
    [Tooltip("BAR mode: max force-found delay (rolled per faction).")]
    [Min(15f)] public float barFoundingForceAfterSecondsMax = 110f;
    [Tooltip("When a founded faction has zero workers, wait this long before seeding recovery workers.")]
    [Min(1f)] public float wipeReviveDelaySeconds = 12f;
    [Tooltip("Minimum chord separation when scattering starting workers (floor; angular rule usually wins).")]
    [Min(5f)] public float scatterWorkerMinSeparation = 55f;
    [Tooltip("Minimum angular separation on the planet between starting roamers (degrees).")]
    [Range(10f, 90f)] public float scatterWorkerMinAngleDegrees = 55f;
    [Tooltip("Legacy snap tolerance (unused by dry-at-sample scatter; kept for inspector compatibility).")]
    [Range(5f, 45f)] public float scatterMaxSnapDegrees = 12f;

    [Header("BAR / Zero-K economy (Metal=Wood, Energy=Stone pools)")]
    [Min(0)] public int startingMetal = 80;
    [Min(0)] public int startingEnergy = 80;
    [Min(0.1f)] public float mexMetalPerSecond = 4f;
    [Min(0.1f)] public float energyGenPerSecond = 4f;
    [Min(1)] public int mexTargetCount = 3;
    [Min(1)] public int energyTargetCount = 2;
    [Min(1)] public int constructorTargetCount = 6;
    [Tooltip("Minimum combat units before a BAR faction may declare war.")]
    [Min(1)] public int raiderAssaultThreshold = 28;
    [Tooltip("After founding, factions cannot be targeted (and will not attack) until this elapses.")]
    [Min(0f)] public float barAssaultGraceSeconds = 120f;
    [Tooltip("Extra protect window after leaving Recovering so a rebuild is not instantly re-stomped.")]
    [Min(0f)] public float barPostRecoveryProtectSeconds = 90f;
    [Tooltip("Refuse BAR assaults when own army / rival army exceeds this (stomping).")]
    [Min(1.1f)] public float barAssaultMaxStrengthRatio = 1.75f;
    [Tooltip("Refuse BAR assaults when own army / rival army is below this (suicide).")]
    [Range(0.2f, 1f)] public float barAssaultMinStrengthRatio = 0.7f;
    [Tooltip("Do not attack (or keep attacking) a faction whose living workers+soldiers are below this.")]
    [Min(1)] public int barProtectedLivingFloor = 14;
    public FactionResourceCost mexCost = new FactionResourceCost(25, 10);
    [Min(1f)] public float mexBuildSeconds = 20f;
    public FactionResourceCost energyGenCost = new FactionResourceCost(15, 25);
    [Min(1f)] public float energyGenBuildSeconds = 18f;
    public FactionResourceCost factoryCost = new FactionResourceCost(50, 40);
    [Min(1f)] public float factoryBuildSeconds = 35f;
    public FactionResourceCost raiderCost = new FactionResourceCost(12, 8);
    [Min(0.5f)] public float raiderBuildSeconds = 4f;
    [Tooltip("BAR Heavy = Archer role. Costlier / slower / tankier than raiders.")]
    public FactionResourceCost heavyCost = new FactionResourceCost(28, 18);
    [Min(0.5f)] public float heavyBuildSeconds = 7f;
    [Tooltip("Produce one Heavy after this many Raiders (Infantry).")]
    [Min(1)] public int raidersPerHeavy = 3;
    [Tooltip("Don't mix Heavies until the faction has at least this many Raiders.")]
    [Min(0)] public int heavyUnlockRaiders = 10;
    [Min(4f)] public float barBuildMinDistance = 18f;
    [Min(4f)] public float barBuildMaxDistance = 40f;
    [Min(1f)] public float barBuildingFlatRadius = 4f;
    [Min(0.5f)] public float barBuildingBlendWidth = 6f;

    [Header("Territory (BAR pockets / borders / trade)")]
    [Min(8)] public int territoryPocketCount = 24;
    [Min(40f)] public float territoryPocketMinSeparation = 90f;
    [Min(8f)] public float territoryPocketClaimRadius = 28f;
    [Min(5f)] public float territoryPocketReclaimCooldownSeconds = 45f;
    [Min(0f)] public float territoryPocketMetalBonusPerSecond = 2.5f;
    [Min(10f)] public float territoryInfluenceStartRadius = 55f;
    [Min(40f)] public float territoryInfluenceMaxRadius = 220f;
    [Min(0.1f)] public float territoryInfluenceGrowthPerSecond = 2.5f;
    [Range(0.02f, 0.35f)] public float territoryContestedInfluenceEpsilon = 0.08f;
    [Min(0.25f)] public float territoryCellAssignInterval = 1.5f;
    [Tooltip("Extra merchant payout multiplier when route is fully owned (scales 1 → 1+bonus).")]
    [Range(0f, 2f)] public float territoryTradeOwnedBonus = 0.75f;
    [Tooltip("Merchant move speed multiplier on mostly hostile/contested corridors.")]
    [Range(0.4f, 1f)] public float territoryTradeHostileSpeedMul = 0.7f;

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

    [Header("Mint")]
    public FactionResourceCost mintCost = new FactionResourceCost(45, 30);
    [Min(1f)] public float mintBuildSeconds = 40f;
    [Min(1)] public int mintMaxBuilders = 3;
    [Min(1f)] public float mintFlatRadius = 4.5f;
    [Min(0.5f)] public float mintBlendWidth = 8f;
    [Min(4f)] public float mintMinDistance = 18f;
    [Min(4f)] public float mintMaxDistance = 28f;
    [Min(1)] public int mintWoodPerGold = 8;
    [Min(1)] public int mintStonePerGold = 4;
    [Min(1)] public int mintGoldPerTick = 1;
    [Min(0)] public int mintReserveWood = 40;
    [Min(0)] public int mintReserveStone = 25;

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

    [Header("Infantry production")]
    public FactionResourceCost infantryCost = new FactionResourceCost(20, 10);
    [Min(1f)] public float infantryTrainingSeconds = 20f;
    [Min(1)] public int soldierMaxCount = 120;
    [Min(0.1f)] public float desiredSoldierPerWorker = 1.5f;
    [Min(1)] public int infantryPerArcher = 2;

    [Header("Archer production")]
    public FactionResourceCost archerCost = new FactionResourceCost(16, 12);
    [Min(1f)] public float archerTrainingSeconds = 18f;

    [Header("Noble production")]
    public FactionResourceCost nobleCost = new FactionResourceCost(30, 20);
    [Min(0)] public int nobleGoldCost = 25;
    [Min(1f)] public float nobleTrainingSeconds = 35f;
    [Min(1)] public int nobleMaxCount = 2;
    [Min(1)] public int nobleEscortCount = 4;

    [Header("Worker production")]
    public FactionResourceCost workerCost = new FactionResourceCost(10, 5);
    [Min(1f)] public float workerTrainingSeconds = 12f;

    [Header("Claimable towns")]
    [Min(1)] public int claimableTownCount = 4;
    [Min(40f)] public float claimableTownMinSeparation = 120f;
    [Min(40f)] public float claimableTownFactionSeparation = 100f;
    [Min(1f)] public float townMaxLoyalty = 100f;
    [Min(0.1f)] public float townLoyaltyDrainPerSecond = 8f;
    [Min(0.01f)] public float townLoyaltyRecoverPerSecond = 1.5f;
    [Min(1f)] public float townClaimRadius = 10f;
    [Min(0)] public int townTrickleWood = 1;
    [Min(0)] public int townTrickleStone = 1;
    [Min(0)] public int townTrickleGold = 1;
    [Tooltip("After ownership flips, wait this long before another noble may start reclaiming.")]
    [Min(10f)] public float townReclaimCooldownSeconds = 100f;
    [Tooltip("Max share of all towns one faction may own (0.5 = ~50%).")]
    [Range(0.1f, 1f)] public float maxTownOwnershipFraction = 0.5f;
    [Tooltip("Extra metal/energy granted when a merchant visits an owned claimed town.")]
    [Min(0)] public int merchantTownVisitMetal = 8;
    [Min(0)] public int merchantTownVisitEnergy = 6;
    [Min(0)] public int merchantTownVisitGold = 2;
    [Tooltip("Metal/energy exchanged when merchants complete a rival-market trade.")]
    [Min(0)] public int merchantTradeMetal = 12;
    [Min(0)] public int merchantTradeEnergy = 10;

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
    [Header("Infantry / Raider combat")]
    [Min(1)] public int soldierMaxHealth = 100;
    [Min(1)] public int soldierDamage = 12;
    [Min(0.1f)] public float soldierAttackRange = 2.2f;
    [Min(1f)] public float soldierShootRange = 22f;
    [Min(0.05f)] public float soldierAttackCooldown = 1f;
    [Min(0.1f)] public float soldierMoveSpeed = 4.5f;

    [Header("Heavy combat (BAR uses Archer role)")]
    [Min(1)] public int archerMaxHealth = 180;
    [Min(1)] public int archerDamage = 22;
    [Min(1f)] public float archerShootRange = 20f;
    [Min(0.05f)] public float archerAttackCooldown = 1.4f;
    [Min(0.1f)] public float archerMoveSpeed = 3.2f;

    [Header("Noble")]
    [Min(1)] public int nobleMaxHealth = 40;
    [Min(1)] public int nobleDamage = 4;
    [Min(1f)] public float nobleShootRange = 12f;
    [Min(0.1f)] public float nobleMoveSpeed = 3.8f;

    [Header("Worker / shared")]
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

    [Header("Threat + coalitions")]
    [Tooltip("How fast threat eases toward strength-based notoriety.")]
    [Min(0.01f)] public float threatChasePerSecond = 0.08f;
    [Tooltip("Passive threat decay while quiet.")]
    [Min(0f)] public float threatDecayPerSecond = 0.015f;
    [Tooltip("Threat bump when declaring war.")]
    [Min(0f)] public float threatOnAttack = 0.08f;
    [Tooltip("Threat bump when claiming a town.")]
    [Min(0f)] public float threatOnTownClaim = 0.12f;
    [Tooltip("How long a faction stays hostile to the player after being shot.")]
    [Min(10f)] public float playerAggroSeconds = 90f;
    [Tooltip("Strength lead over #2 required to consider a coalition (e.g. 0.35 = 35% ahead).")]
    [Range(0.1f, 1f)] public float coalitionLeadFraction = 0.35f;
    [Tooltip("Or form a coalition if the leader's threat exceeds this.")]
    [Range(0.3f, 1f)] public float coalitionThreatThreshold = 0.7f;
    [Min(20f)] public float coalitionDurationSeconds = 150f;
    [Min(10f)] public float coalitionCooldownSeconds = 90f;

    [Header("Strength weights")]
    [Min(0f)] public float soldierStrength = 100f;
    [Min(0f)] public float workerStrength = 10f;
    [Min(0f)] public float townHallStrength = 200f;
    [Min(0f)] public float barracksStrength = 100f;
    [Min(0f)] public float marketStrength = 80f;
    [Min(0f)] public float mintStrength = 70f;
    [Min(0f)] public float claimableTownStrength = 120f;
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
