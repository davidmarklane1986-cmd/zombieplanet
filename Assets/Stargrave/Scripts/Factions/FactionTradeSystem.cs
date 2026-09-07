using System.Collections.Generic;
using UnityEngine;

/// <summary>Pairwise trade relation, prices, and deal settlement between factions.</summary>
public static class FactionTradeSystem
{
    static readonly Dictionary<long, float> s_Relation = new Dictionary<long, float>(8);

    public static void Reset()
    {
        s_Relation.Clear();
    }

    public static float GetRelation(FactionController a, FactionController b)
    {
        if (a == null || b == null || a == b)
            return 0f;
        FactionEconomySettings economy = ResolveEconomy(a, b);
        float start = economy != null ? economy.tradeRelationStart : 0.15f;
        return s_Relation.TryGetValue(PairKey(a, b), out float value)
            ? value
            : Mathf.Clamp01(start);
    }

    public static void NotifyDeal(FactionController a, FactionController b)
    {
        if (a == null || b == null || a == b)
            return;
        FactionEconomySettings economy = ResolveEconomy(a, b);
        float gain = economy != null ? economy.tradeRelationGain : 0.08f;
        AddRelation(a, b, gain);
    }

    public static void NotifyAttackDeclared(FactionController a, FactionController b)
    {
        if (a == null || b == null || a == b)
            return;
        FactionEconomySettings economy = ResolveEconomy(a, b);
        float penalty = economy != null ? economy.attackRelationPenalty : 0.25f;
        AddRelation(a, b, -penalty);
    }

    public static void NotifyHostileDamage(FactionController victim, Transform attacker)
    {
        if (victim == null || attacker == null)
            return;
        FactionNpc npc = attacker.GetComponentInParent<FactionNpc>();
        FactionController other = npc != null ? npc.Faction : null;
        if (other == null)
        {
            Building building = attacker.GetComponentInParent<Building>();
            other = building != null ? building.Faction : null;
        }
        if (other == null || other == victim)
            return;
        FactionEconomySettings economy = ResolveEconomy(victim, other);
        float penalty = economy != null ? economy.damageRelationPenalty : 0.02f;
        AddRelation(victim, other, -penalty);
    }

    public static bool AreFighting(FactionController a, FactionController b)
    {
        if (a == null || b == null || a == b)
            return false;
        return a.IsAtWarWith(b) || b.IsAtWarWith(a);
    }

    public static bool ShouldLaunchDespiteTrade(FactionController attacker, FactionController rival)
    {
        if (attacker == null || rival == null)
            return false;

        float relation = GetRelation(attacker, rival);
        FactionEconomySettings economy = attacker.Economy;
        float floor = economy != null ? economy.attackChanceFloor : 0.08f;
        float desperation = 1f;
        if (attacker.AvailableWood < 10 && attacker.AvailableStone < 10)
            desperation += 0.4f;
        if (attacker.Strength > rival.Strength * 1.4f)
            desperation += 0.3f;

        float chance = Mathf.Clamp((1f - relation) * desperation, Mathf.Max(0.01f, floor), 1f);
        return Random.value <= chance;
    }

    public static bool TryPlanDeal(
        FactionController visitor,
        FactionController host,
        out TradeOffer offer)
    {
        offer = default;
        if (visitor == null || host == null || visitor == host)
            return false;
        if (visitor.Market == null || !visitor.Market.IsOperational)
            return false;
        if (host.Market == null || !host.Market.IsOperational)
            return false;
        if (AreFighting(visitor, host))
            return false;

        FactionEconomySettings economy = visitor.Economy;
        int lot = economy != null ? Mathf.Max(1, economy.tradeLotSize) : 5;
        int carry = economy != null ? Mathf.Max(1, economy.merchantCarryCapacity) : 10;
        int units = Mathf.Min(lot, carry);

        if (TrySell(visitor, host, FactionResourceType.Wood, units, out offer) ||
            TrySell(visitor, host, FactionResourceType.Stone, units, out offer) ||
            TryBuy(visitor, host, FactionResourceType.Wood, units, out offer) ||
            TryBuy(visitor, host, FactionResourceType.Stone, units, out offer))
            return true;

        return false;
    }

    public static bool TryCompleteCarriedSale(
        FactionController sellerHome,
        FactionController buyer,
        FactionResourceType type,
        int units,
        out int goldReceived)
    {
        goldReceived = 0;
        if (sellerHome == null || buyer == null || units <= 0)
            return false;
        if (AreFighting(sellerHome, buyer))
            return false;
        int gold = Mathf.Max(1, Mathf.RoundToInt(units * UnitGoldPrice(type, sellerHome, buyer)));
        if (!buyer.TrySpendGold(gold))
            return false;
        buyer.AddResource(type, units);
        goldReceived = gold;
        NotifyDeal(sellerHome, buyer);
        return true;
    }

    public static bool TryCompleteCarriedPurchase(
        FactionController buyerHome,
        FactionController seller,
        FactionResourceType type,
        int units,
        int goldOffered,
        out int unitsReceived)
    {
        unitsReceived = 0;
        if (buyerHome == null || seller == null || units <= 0 || goldOffered <= 0)
            return false;
        if (AreFighting(buyerHome, seller))
            return false;
        if (seller.TradeableAmount(type) < units)
            return false;
        if (!seller.TryWithdrawResource(type, units, out int taken) || taken < units)
        {
            if (taken > 0)
                seller.AddResource(type, taken);
            return false;
        }

        seller.AddGold(goldOffered);
        unitsReceived = taken;
        NotifyDeal(buyerHome, seller);
        return true;
    }

    public static bool TryPickPartner(FactionController from, Vector3 origin, out FactionController partner)
    {
        partner = null;
        if (from == null)
            return false;

        IReadOnlyList<FactionController> factions = FactionRegistry.Factions;
        float bestScore = float.NegativeInfinity;
        for (int i = 0; i < factions.Count; i++)
        {
            FactionController candidate = factions[i];
            if (candidate == null || candidate == from)
                continue;
            if (candidate.Market == null || !candidate.Market.IsOperational)
                continue;
            if (AreFighting(from, candidate))
                continue;
            if (!TryPlanDeal(from, candidate, out _))
                continue;

            float dist = Vector3.Distance(origin, candidate.GetSafePosition());
            float relation = GetRelation(from, candidate);
            float score = relation * 40f - dist * 0.02f;
            if (score > bestScore)
            {
                bestScore = score;
                partner = candidate;
            }
        }

        return partner != null;
    }

    public static float UnitGoldPrice(
        FactionResourceType type,
        FactionController seller,
        FactionController buyer)
    {
        FactionEconomySettings economy = ResolveEconomy(seller, buyer);
        float basePrice = type == FactionResourceType.Wood
            ? (economy != null ? economy.woodGoldPrice : 2f)
            : (economy != null ? economy.stoneGoldPrice : 3f);
        float influence = economy != null ? economy.priceGapInfluence : 0.35f;
        float sellerMod = seller != null && seller.TradeSurplus(type) > 0 ? -influence : 0f;
        float buyerMod = buyer != null && buyer.TradeNeed(type) > 0 ? influence : 0f;
        return Mathf.Max(0.25f, basePrice * (1f + sellerMod + buyerMod));
    }

    static bool TrySell(
        FactionController visitor,
        FactionController host,
        FactionResourceType type,
        int units,
        out TradeOffer offer)
    {
        offer = default;
        int surplus = visitor.TradeableAmount(type);
        if (surplus < units)
            return false;
        if (host.TradeNeed(type) <= 0 && host.GetAvailable(type) >= visitor.GetAvailable(type))
            return false;
        int gold = Mathf.Max(1, Mathf.RoundToInt(units * UnitGoldPrice(type, visitor, host)));
        if (host.Gold < gold)
            return false;
        offer = new TradeOffer
        {
            valid = true,
            seller = visitor,
            buyer = host,
            resource = type,
            units = units,
            gold = gold
        };
        return true;
    }

    static bool TryBuy(
        FactionController visitor,
        FactionController host,
        FactionResourceType type,
        int units,
        out TradeOffer offer)
    {
        offer = default;
        if (visitor.TradeNeed(type) <= 0)
            return false;
        if (host.TradeableAmount(type) < units)
            return false;
        int gold = Mathf.Max(1, Mathf.RoundToInt(units * UnitGoldPrice(type, host, visitor)));
        if (visitor.Gold < gold)
            return false;
        offer = new TradeOffer
        {
            valid = true,
            seller = host,
            buyer = visitor,
            resource = type,
            units = units,
            gold = gold
        };
        return true;
    }

    static void AddRelation(FactionController a, FactionController b, float delta)
    {
        long key = PairKey(a, b);
        float current = GetRelation(a, b);
        s_Relation[key] = Mathf.Clamp01(current + delta);
    }

    static long PairKey(FactionController a, FactionController b)
    {
        int x = a.RuntimeIndex;
        int y = b.RuntimeIndex;
        if (x > y)
        {
            int t = x;
            x = y;
            y = t;
        }
        return ((long)x << 32) | (uint)y;
    }

    static FactionEconomySettings ResolveEconomy(FactionController a, FactionController b)
    {
        if (a != null && a.Economy != null)
            return a.Economy;
        return b != null ? b.Economy : null;
    }
}

public struct TradeOffer
{
    public bool valid;
    public FactionController seller;
    public FactionController buyer;
    public FactionResourceType resource;
    public int units;
    public int gold;
}
