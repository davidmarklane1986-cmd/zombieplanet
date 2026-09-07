using UnityEngine;

/// <summary>Centralized strength calculation facade for UI, AI, and future save systems.</summary>
public static class FactionStrengthSystem
{
    public static FactionStrengthBreakdown Calculate(FactionController faction)
    {
        return faction != null ? faction.CalculateStrength() : default;
    }
}

/// <summary>Centralized balance facade; the director owns the actual comparison between factions.</summary>
public static class FactionBalanceSystem
{
    public static float GrowthModifier(FactionController faction)
    {
        return faction != null && faction.Simulation != null
            ? faction.Simulation.GetGrowthModifier(faction)
            : 1f;
    }
}

/// <summary>
/// Keeps state transitions in one small service. Combat units may request retreat, while the
/// director remains responsible for global engagement and economy transitions.
/// </summary>
public sealed class FactionStateMachine : MonoBehaviour
{
    public FactionController Faction { get; private set; }

    public void Initialize(FactionController faction)
    {
        Faction = faction;
    }

    public void RequestRetreat()
    {
        if (Faction != null)
            Faction.BeginRetreat();
    }

    public void RequestRecovery()
    {
        if (Faction != null)
            Faction.BeginRecovery();
    }
}
