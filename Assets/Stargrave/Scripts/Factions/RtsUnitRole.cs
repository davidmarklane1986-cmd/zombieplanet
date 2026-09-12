/// <summary>Shared RTS unit role labels used by buildings/faction brain and mapped into Rts2.</summary>
public enum RtsUnitRole : byte
{
    Worker = 0,
    Infantry = 1,
    Archer = 2,
    Merchant = 3,
    Noble = 4
}

public static class RtsUnitRoles
{
    public static bool IsCombat(RtsUnitRole role) =>
        role == RtsUnitRole.Infantry || role == RtsUnitRole.Archer;

    public static FactionNpcRole ToLegacy(RtsUnitRole role)
    {
        switch (role)
        {
            case RtsUnitRole.Worker: return FactionNpcRole.ResourceGatherer;
            case RtsUnitRole.Infantry: return FactionNpcRole.Infantry;
            case RtsUnitRole.Archer: return FactionNpcRole.Archer;
            case RtsUnitRole.Merchant: return FactionNpcRole.Merchant;
            default: return FactionNpcRole.Noble;
        }
    }

    public static RtsUnitRole FromLegacy(FactionNpcRole role)
    {
        switch (role)
        {
            case FactionNpcRole.ResourceGatherer: return RtsUnitRole.Worker;
            case FactionNpcRole.Infantry: return RtsUnitRole.Infantry;
            case FactionNpcRole.Archer: return RtsUnitRole.Archer;
            case FactionNpcRole.Merchant: return RtsUnitRole.Merchant;
            default: return RtsUnitRole.Noble;
        }
    }
}
