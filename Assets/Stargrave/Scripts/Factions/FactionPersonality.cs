using UnityEngine;

public enum FactionBuildBias : byte
{
    Balanced = 0,
    MexHeavy = 1,
    EnergyHeavy = 2,
    FactoryRush = 3
}

public enum FactionAggression : byte
{
    Cautious = 0,
    Balanced = 1,
    Aggressive = 2
}

/// <summary>Runtime identity pack that makes each faction play and read differently.</summary>
public struct FactionPersonality
{
    public string flavorName;
    public Color color;
    public FactionBuildBias buildBias;
    public FactionAggression aggression;

    static readonly string[] Prefixes =
    {
        "Crimson", "Azure", "Verdant", "Amber", "Violet", "Ivory", "Obsidian", "Copper",
        "Iron", "Silver", "Ember", "Frost", "Storm", "Dust", "Tide", "Ash"
    };

    static readonly string[] Suffixes =
    {
        "Legion", "Consortium", "Dominion", "Cartel", "Syndicate", "Covenant",
        "Vanguard", "Tribunal", "Collective", "Order", "Clade", "Host"
    };

    public static FactionPersonality CreateForIndex(int runtimeIndex, FactionDefinition definition)
    {
        int i = Mathf.Max(0, runtimeIndex);
        var p = new FactionPersonality
        {
            buildBias = (FactionBuildBias)(i % 4),
            aggression = (FactionAggression)(i % 3),
            color = ResolveColor(i, definition),
            flavorName = ResolveName(i, definition)
        };
        return p;
    }

    static Color ResolveColor(int i, FactionDefinition definition)
    {
        if (definition != null && definition.uiColor.a > 0.01f &&
            (definition.uiColor.r + definition.uiColor.g + definition.uiColor.b) > 0.05f &&
            definition.uiColor != Color.white)
            return definition.uiColor;

        Color[] palette =
        {
            new Color(0.15f, 0.85f, 0.35f, 1f),
            new Color(0.95f, 0.25f, 0.20f, 1f),
            new Color(0.20f, 0.45f, 1.00f, 1f),
            new Color(1.00f, 0.80f, 0.15f, 1f),
            new Color(0.85f, 0.25f, 0.95f, 1f),
            new Color(0.15f, 0.90f, 0.90f, 1f),
            new Color(1.00f, 0.50f, 0.10f, 1f),
            new Color(0.95f, 0.95f, 0.95f, 1f)
        };
        return palette[((i % palette.Length) + palette.Length) % palette.Length];
    }

    static string ResolveName(int i, FactionDefinition definition)
    {
        if (definition != null && !string.IsNullOrWhiteSpace(definition.displayName) &&
            definition.displayName != "Faction")
            return definition.displayName.Trim();

        string prefix = Prefixes[i % Prefixes.Length];
        string suffix = Suffixes[(i * 3 + 1) % Suffixes.Length];
        return prefix + " " + suffix;
    }

    public int MexTarget(FactionEconomySettings eco)
    {
        int baseTarget = Mathf.Max(1, eco.mexTargetCount);
        switch (buildBias)
        {
            case FactionBuildBias.MexHeavy: return baseTarget + 2;
            case FactionBuildBias.FactoryRush: return Mathf.Max(1, baseTarget - 1);
            default: return baseTarget;
        }
    }

    public int EnergyTarget(FactionEconomySettings eco)
    {
        int baseTarget = Mathf.Max(1, eco.energyTargetCount);
        switch (buildBias)
        {
            case FactionBuildBias.EnergyHeavy: return baseTarget + 2;
            case FactionBuildBias.FactoryRush: return Mathf.Max(1, baseTarget - 1);
            default: return baseTarget;
        }
    }

    public int AssaultThreshold(FactionEconomySettings eco)
    {
        int baseThreshold = Mathf.Max(1, eco.raiderAssaultThreshold);
        switch (aggression)
        {
            case FactionAggression.Aggressive: return Mathf.Max(8, Mathf.RoundToInt(baseThreshold * 0.75f));
            case FactionAggression.Cautious: return Mathf.RoundToInt(baseThreshold * 1.25f);
            default: return baseThreshold;
        }
    }

    public float MaxStrengthRatio(FactionEconomySettings eco)
    {
        float baseRatio = Mathf.Max(1.1f, eco.barAssaultMaxStrengthRatio);
        switch (aggression)
        {
            case FactionAggression.Aggressive: return baseRatio + 0.35f;
            case FactionAggression.Cautious: return Mathf.Max(1.2f, baseRatio - 0.25f);
            default: return baseRatio;
        }
    }

    public float ReclaimGreed =>
        aggression == FactionAggression.Aggressive ? 1.35f :
        aggression == FactionAggression.Cautious ? 0.75f : 1f;

    public float InfluenceGrowthMul =>
        aggression == FactionAggression.Aggressive ? 1.25f :
        aggression == FactionAggression.Cautious ? 0.8f : 1f;

    public float PocketGreed =>
        aggression == FactionAggression.Aggressive ? 1.4f :
        aggression == FactionAggression.Cautious ? 0.85f :
        buildBias == FactionBuildBias.MexHeavy ? 1.2f : 1f;
}
