using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Threat (notoriety) + temporary balance-of-power coalitions vs a dominant faction.
/// </summary>
public static class FactionBalanceSystem
{
    static readonly Dictionary<int, float> s_Threat = new Dictionary<int, float>(8);
    static int s_CoalitionTargetRuntimeIndex = -1;
    static float s_CoalitionUntil;
    static float s_CoalitionCooldownUntil;

    public static void Reset()
    {
        s_Threat.Clear();
        s_CoalitionTargetRuntimeIndex = -1;
        s_CoalitionUntil = 0f;
        s_CoalitionCooldownUntil = 0f;
    }

    public static float GetThreat(FactionController faction)
    {
        if (faction == null)
            return 0f;
        return s_Threat.TryGetValue(faction.RuntimeIndex, out float t) ? Mathf.Clamp01(t) : 0f;
    }

    public static void AddThreat(FactionController faction, float delta)
    {
        if (faction == null || Mathf.Abs(delta) < 1e-6f)
            return;
        float t = GetThreat(faction) + delta;
        s_Threat[faction.RuntimeIndex] = Mathf.Clamp01(t);
    }

    public static bool HasActiveCoalition =>
        s_CoalitionTargetRuntimeIndex >= 0 && Time.time < s_CoalitionUntil;

    public static FactionController CoalitionTarget
    {
        get
        {
            if (!HasActiveCoalition)
                return null;
            IReadOnlyList<FactionController> factions = FactionRegistry.Factions;
            for (int i = 0; i < factions.Count; i++)
            {
                if (factions[i] != null && factions[i].RuntimeIndex == s_CoalitionTargetRuntimeIndex)
                    return factions[i];
            }
            return null;
        }
    }

    public static bool IsCoalitionTarget(FactionController faction) =>
        HasActiveCoalition &&
        faction != null &&
        faction.RuntimeIndex == s_CoalitionTargetRuntimeIndex;

    public static bool AreCoalitionAllies(FactionController a, FactionController b)
    {
        if (!HasActiveCoalition || a == null || b == null || a == b)
            return false;
        if (IsCoalitionTarget(a) || IsCoalitionTarget(b))
            return false;
        // Any two non-target founded factions are temporary allies against the tyrant.
        return a.HasFoundedCampus && b.HasFoundedCampus;
    }

    public static void Tick(FactionSimulation sim)
    {
        if (sim == null)
            return;

        IReadOnlyList<FactionController> factions = FactionRegistry.Factions;
        if (factions.Count < 2)
            return;

        FactionWarfareSettings war = sim.warfare;
        float decay = Mathf.Max(0f, war.threatDecayPerSecond) * Time.deltaTime;

        FactionController strongest = null;
        FactionController second = null;
        float best = float.NegativeInfinity;
        float secondBest = float.NegativeInfinity;

        for (int i = 0; i < factions.Count; i++)
        {
            FactionController f = factions[i];
            if (f == null || !f.HasFoundedCampus)
                continue;

            float strength = Mathf.Max(0f, f.Strength);
            float rank01 = 0f;
            // Relative threat from strength vs average peers.
            float peerSum = 0f;
            int peerN = 0;
            for (int j = 0; j < factions.Count; j++)
            {
                FactionController o = factions[j];
                if (o == null || o == f || !o.HasFoundedCampus)
                    continue;
                peerSum += o.Strength;
                peerN++;
            }
            float peerAvg = peerN > 0 ? peerSum / peerN : strength;
            if (peerAvg > 1f)
                rank01 = Mathf.Clamp01((strength / peerAvg - 1f) / Mathf.Max(0.25f, war.coalitionLeadFraction));

            float towns = 0f;
            IReadOnlyList<ClaimableTown> allTowns = FactionRegistry.Towns;
            for (int t = 0; t < allTowns.Count; t++)
            {
                if (allTowns[t] != null && allTowns[t].Owner == f)
                    towns += 0.04f;
            }

            float desired = Mathf.Clamp01(rank01 * 0.75f + towns + GetThreat(f) * 0.15f);
            float current = GetThreat(f);
            // Ease toward desired, then apply quiet decay.
            current = Mathf.MoveTowards(current, desired, war.threatChasePerSecond * Time.deltaTime);
            current = Mathf.Max(0f, current - decay);
            s_Threat[f.RuntimeIndex] = Mathf.Clamp01(current);

            if (strength > best)
            {
                second = strongest;
                secondBest = best;
                strongest = f;
                best = strength;
            }
            else if (strength > secondBest)
            {
                second = f;
                secondBest = strength;
            }
        }

        if (HasActiveCoalition)
        {
            FactionController target = CoalitionTarget;
            if (target == null || !target.HasFoundedCampus || target.TownHall == null)
                EndCoalition(war.coalitionCooldownSeconds);
            return;
        }

        if (Time.time < s_CoalitionCooldownUntil)
            return;
        if (strongest == null || second == null)
            return;
        if (secondBest <= 1f)
            return;

        float lead = (best - secondBest) / secondBest;
        float threatGate = war.coalitionThreatThreshold;
        if (lead < war.coalitionLeadFraction && GetThreat(strongest) < threatGate)
            return;

        // Need at least two other founded factions to form a pack.
        int allies = 0;
        for (int i = 0; i < factions.Count; i++)
        {
            FactionController f = factions[i];
            if (f == null || f == strongest || !f.HasFoundedCampus)
                continue;
            if (f.State == FactionState.Retreating)
                continue;
            allies++;
        }
        if (allies < 2)
            return;

        s_CoalitionTargetRuntimeIndex = strongest.RuntimeIndex;
        s_CoalitionUntil = Time.time + Mathf.Max(30f, war.coalitionDurationSeconds);
        if (sim.verboseEvents)
        {
            Debug.Log(
                $"[FactionSimulation] Coalition formed vs {strongest.DisplayName} " +
                $"(lead {lead:0%} threat {GetThreat(strongest):0.00}) for {war.coalitionDurationSeconds:0}s.");
        }
    }

    static void EndCoalition(float cooldownSeconds)
    {
        s_CoalitionTargetRuntimeIndex = -1;
        s_CoalitionUntil = 0f;
        s_CoalitionCooldownUntil = Time.time + Mathf.Max(0f, cooldownSeconds);
    }

    public static float GrowthBias(FactionController faction)
    {
        if (faction == null)
            return 1f;
        float threat = GetThreat(faction);
        // High threat slows; low threat slightly accelerates catch-up.
        if (threat > 0.55f)
            return 1f - Mathf.Clamp01((threat - 0.55f) / 0.45f) * 0.35f;
        if (threat < 0.2f && faction.HasFoundedCampus)
            return 1f + (0.2f - threat) * 0.5f; // up to +10%
        return 1f;
    }

    /// <summary>Legacy facade used by decision helpers — defers to the simulation director.</summary>
    public static float GrowthModifier(FactionController faction)
    {
        return faction != null && faction.Simulation != null
            ? faction.Simulation.GetGrowthModifier(faction)
            : 1f;
    }
}
