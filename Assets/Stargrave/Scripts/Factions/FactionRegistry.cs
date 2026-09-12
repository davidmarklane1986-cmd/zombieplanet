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
    static readonly List<ClaimableTown> s_Towns = new List<ClaimableTown>(16);
    static bool s_WarfareEnabled;

    public static bool WarfareEnabled => s_WarfareEnabled;
    public static IReadOnlyList<FactionController> Factions => s_Factions;
    public static IReadOnlyList<FactionNpc> Npcs => s_Npcs;
    public static IReadOnlyList<ResourceNode> Resources => s_Resources;
    public static IReadOnlyList<ClaimableTown> Towns => s_Towns;

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

    public static void RegisterTown(ClaimableTown town)
    {
        if (town != null && !s_Towns.Contains(town))
            s_Towns.Add(town);
    }

    public static void UnregisterTown(ClaimableTown town)
    {
        if (town != null)
            s_Towns.Remove(town);
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
        s_Towns.Clear();
        s_WarfareEnabled = false;
        FactionTradeSystem.Reset();
        FactionBalanceSystem.Reset();
    }

    public static FactionNpc FindNearestNpc(Vector3 position, float radius, FactionController excludeFaction = null)
    {
        FindEnemyNpcs(position, radius, excludeFaction, out FactionNpc nearest, out _);
        return nearest;
    }

    public static int CountEnemyNpcs(Vector3 position, float radius, FactionController owner)
    {
        FindEnemyNpcs(position, radius, owner, out _, out int count);
        return count;
    }

    public static void FindEnemyNpcs(
        Vector3 position,
        float radius,
        FactionController owner,
        out FactionNpc nearest,
        out int count)
    {
        nearest = null;
        count = 0;
        float radiusSq = radius * radius;
        float bestSq = radiusSq;
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
            if (!IsCombatTarget(npc, owner))
                continue;

            float d = (npc.transform.position - position).sqrMagnitude;
            if (d > radiusSq)
                continue;
            count++;
            if (d <= bestSq)
            {
                bestSq = d;
                nearest = npc;
            }
        }
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
            if (!FactionNpcRoles.IsCombatSoldier(npc.Role))
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

    public static bool IsWarfareProtected(FactionController faction)
    {
        if (faction == null)
            return false;
        return faction.State == FactionState.Recovering ||
               faction.State == FactionState.Retreating;
    }

    public static bool IsCombatTarget(FactionNpc npc, FactionController seeker)
    {
        if (npc == null || npc.IsDead)
            return false;
        if (IsWarfareProtected(npc.Faction))
            return false;
        if (npc.Role != FactionNpcRole.Merchant)
            return true;
        return FactionTradeSystem.AreFighting(seeker, npc.Faction);
    }

    public static bool IsCombatTarget(Building building, FactionController seeker)
    {
        if (building == null || !building.IsFactionTargetable)
            return false;
        if (seeker != null && building.OwningFaction == seeker)
            return false;
        return !IsWarfareProtected(building.OwningFaction);
    }
}

/// <summary>
/// World-space hash of living NPCs and zombies, rebuilt once per frame for cheap local crowding.
/// </summary>
static class FactionCrowdGrid
{
    const float CellSize = 4f;
    const int HashSize = 512;
    static int s_Frame = -1;
    static int s_Count;
    static bool s_HeadsInited;
    static readonly int[] s_Head = new int[HashSize];
    static readonly List<int> s_UsedBuckets = new List<int>(128);
    static int[] s_Next = new int[256];
    static Vector3[] s_Pos = new Vector3[256];
    static object[] s_Owner = new object[256];
    static int[] s_Cx = new int[256];
    static int[] s_Cy = new int[256];
    static int[] s_Cz = new int[256];

    public static void AddSeparation(
        object skip,
        Vector3 position,
        Vector3 up,
        float diameter,
        float diameterSq,
        ref Vector3 push)
    {
        EnsureBuilt();
        int cx = CellCoord(position.x);
        int cy = CellCoord(position.y);
        int cz = CellCoord(position.z);
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int qx = cx + dx;
                    int qy = cy + dy;
                    int qz = cz + dz;
                    int bucket = CellHash(qx, qy, qz);
                    for (int i = s_Head[bucket]; i >= 0; i = s_Next[i])
                    {
                        if (s_Cx[i] != qx || s_Cy[i] != qy || s_Cz[i] != qz)
                            continue;
                        if (s_Owner[i] == skip)
                            continue;
                        Vector3 planar = Vector3.ProjectOnPlane(position - s_Pos[i], up);
                        float distSq = planar.sqrMagnitude;
                        if (distSq > diameterSq)
                            continue;
                        float dist = Mathf.Sqrt(Mathf.Max(distSq, 1e-6f));
                        Vector3 away = distSq > 1e-6f
                            ? planar / dist
                            : Vector3.Cross(up, Vector3.right).normalized;
                        push += away * (diameter - dist);
                    }
                }
            }
        }
    }

    static void EnsureBuilt()
    {
        int stamp = Time.frameCount >> 1;
        if (s_Frame == stamp)
            return;
        s_Frame = stamp;
        if (!s_HeadsInited)
        {
            for (int i = 0; i < HashSize; i++)
                s_Head[i] = -1;
            s_HeadsInited = true;
        }
        else
        {
            for (int i = 0; i < s_UsedBuckets.Count; i++)
                s_Head[s_UsedBuckets[i]] = -1;
        }
        s_UsedBuckets.Clear();
        s_Count = 0;

        IReadOnlyList<FactionNpc> npcs = FactionRegistry.Npcs;
        for (int i = 0; i < npcs.Count; i++)
        {
            FactionNpc npc = npcs[i];
            if (npc == null || npc.IsDead)
                continue;
            Insert(npc, npc.transform.position);
        }

        IReadOnlyList<ZombieAI> zombies = ZombieAI.Active;
        for (int i = 0; i < zombies.Count; i++)
        {
            ZombieAI zombie = zombies[i];
            if (zombie == null || zombie.IsDead)
                continue;
            Insert(zombie, zombie.transform.position);
        }
    }

    static void Insert(object owner, Vector3 position)
    {
        if (s_Count >= s_Next.Length)
            Grow();
        int index = s_Count++;
        s_Pos[index] = position;
        s_Owner[index] = owner;
        int x = CellCoord(position.x);
        int y = CellCoord(position.y);
        int z = CellCoord(position.z);
        s_Cx[index] = x;
        s_Cy[index] = y;
        s_Cz[index] = z;
        int bucket = CellHash(x, y, z);
        if (s_Head[bucket] < 0)
            s_UsedBuckets.Add(bucket);
        s_Next[index] = s_Head[bucket];
        s_Head[bucket] = index;
    }

    static void Grow()
    {
        int size = s_Next.Length * 2;
        System.Array.Resize(ref s_Next, size);
        System.Array.Resize(ref s_Pos, size);
        System.Array.Resize(ref s_Owner, size);
        System.Array.Resize(ref s_Cx, size);
        System.Array.Resize(ref s_Cy, size);
        System.Array.Resize(ref s_Cz, size);
    }

    static int CellCoord(float value)
    {
        return Mathf.FloorToInt(value / CellSize);
    }

    static int CellHash(int x, int y, int z)
    {
        unchecked
        {
            uint hash = (uint)(x * 73856093 ^ y * 19349663 ^ z * 83492791);
            return (int)(hash & (HashSize - 1));
        }
    }
}
