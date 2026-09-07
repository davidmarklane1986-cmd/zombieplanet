using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Small runtime registry used by the simulation instead of repeated scene-wide searches.
/// </summary>
public static class FactionRegistry
{
    static readonly List<FactionController> s_Factions = new List<FactionController>(4);
    static readonly List<FactionNpc> s_Npcs = new List<FactionNpc>(128);
    static readonly List<ResourceNode> s_Resources = new List<ResourceNode>(512);
    static bool s_WarfareEnabled;

    public static bool WarfareEnabled => s_WarfareEnabled;
    public static IReadOnlyList<FactionController> Factions => s_Factions;
    public static IReadOnlyList<FactionNpc> Npcs => s_Npcs;
    public static IReadOnlyList<ResourceNode> Resources => s_Resources;

    public static void RegisterFaction(FactionController faction)
    {
        if (faction != null && !s_Factions.Contains(faction))
            s_Factions.Add(faction);
    }

    public static void UnregisterFaction(FactionController faction)
    {
        if (faction != null)
            s_Factions.Remove(faction);
    }

    public static void RegisterNpc(FactionNpc npc)
    {
        if (npc != null && !s_Npcs.Contains(npc))
            s_Npcs.Add(npc);
    }

    public static void UnregisterNpc(FactionNpc npc)
    {
        if (npc != null)
            s_Npcs.Remove(npc);
    }

    public static void RegisterResource(ResourceNode node)
    {
        if (node != null && !s_Resources.Contains(node))
            s_Resources.Add(node);
    }

    public static void UnregisterResource(ResourceNode node)
    {
        if (node != null)
            s_Resources.Remove(node);
    }

    public static void EnableWarfare()
    {
        if (s_WarfareEnabled)
            return;
        s_WarfareEnabled = true;
        Debug.Log("[FactionSimulation] Warfare enabled.");
    }

    public static void Reset()
    {
        s_Factions.Clear();
        s_Npcs.Clear();
        s_Resources.Clear();
        s_WarfareEnabled = false;
    }

    public static FactionNpc FindNearestNpc(Vector3 position, float radius, FactionController excludeFaction = null)
    {
        float bestSq = radius * radius;
        FactionNpc best = null;
        for (int i = s_Npcs.Count - 1; i >= 0; i--)
        {
            FactionNpc npc = s_Npcs[i];
            if (npc == null || !npc.isActiveAndEnabled || npc.IsDead)
            {
                s_Npcs.RemoveAt(i);
                continue;
            }
            if (excludeFaction != null && npc.Faction == excludeFaction)
                continue;

            float d = (npc.transform.position - position).sqrMagnitude;
            if (d <= bestSq)
            {
                bestSq = d;
                best = npc;
            }
        }
        return best;
    }

    public static int CountEnemyNpcs(Vector3 position, float radius, FactionController owner)
    {
        float radiusSq = radius * radius;
        int count = 0;
        for (int i = s_Npcs.Count - 1; i >= 0; i--)
        {
            FactionNpc npc = s_Npcs[i];
            if (npc == null || !npc.isActiveAndEnabled || npc.IsDead)
            {
                s_Npcs.RemoveAt(i);
                continue;
            }
            if (owner != null && npc.Faction == owner)
                continue;
            if ((npc.transform.position - position).sqrMagnitude <= radiusSq)
                count++;
        }
        return count;
    }

    public static void CountSoldiersNear(
        Vector3 position,
        float radius,
        FactionController faction,
        out int friends,
        out int foes)
    {
        friends = 0;
        foes = 0;
        float radiusSq = radius * radius;
        for (int i = s_Npcs.Count - 1; i >= 0; i--)
        {
            FactionNpc npc = s_Npcs[i];
            if (npc == null || !npc.isActiveAndEnabled || npc.IsDead)
            {
                s_Npcs.RemoveAt(i);
                continue;
            }
            if (npc.Role != FactionNpcRole.Soldier)
                continue;
            if ((npc.transform.position - position).sqrMagnitude > radiusSq)
                continue;
            if (faction != null && npc.Faction == faction)
                friends++;
            else
                foes++;
        }
    }

    public static FactionController FindOpposingFaction(FactionController faction)
    {
        if (faction == null)
            return null;
        if (faction.CurrentRival != null && faction.CurrentRival != faction)
            return faction.CurrentRival;

        Vector3 home = faction.GetSafePosition();
        FactionController best = null;
        float bestSq = float.PositiveInfinity;
        for (int i = 0; i < s_Factions.Count; i++)
        {
            FactionController candidate = s_Factions[i];
            if (candidate == null || candidate == faction)
                continue;
            float d = (candidate.GetSafePosition() - home).sqrMagnitude;
            if (d < bestSq)
            {
                bestSq = d;
                best = candidate;
            }
        }
        return best;
    }

    public static ResourceNode FindNearestAvailableResource(
        Vector3 position,
        FactionResourceType type,
        FactionController owner,
        float maxDistance)
    {
        float bestSq = maxDistance * maxDistance;
        ResourceNode best = null;
        for (int i = s_Resources.Count - 1; i >= 0; i--)
        {
            ResourceNode node = s_Resources[i];
            if (node == null)
            {
                s_Resources.RemoveAt(i);
                continue;
            }
            if (node.ResourceType != type || !node.CanReserve(owner))
                continue;

            float d = (node.transform.position - position).sqrMagnitude;
            if (d <= bestSq)
            {
                bestSq = d;
                best = node;
            }
        }
        return best;
    }
}
