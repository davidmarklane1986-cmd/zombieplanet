using UnityEngine;

[CreateAssetMenu(menuName = "Stargrave/Factions/Faction Definition", fileName = "FactionDefinition")]
public class FactionDefinition : ScriptableObject
{
    public string factionId = "faction_01";
    public string displayName = "Faction";

    [TextArea] public string description = "Wants to rule over everyone.";

    public Color uiColor = Color.white;

    [Header("World Pressure")]
    [Tooltip("How fast this faction gains influence over time (per minute).")]
    public float influencePerMinute = 1.0f;

    [Tooltip("How much influence is gained when completing a mission for them.")]
    public float influencePerMission = 3.0f;

    [Tooltip("How much influence is lost when you oppose them (optional later).")]
    public float influenceLossOnOppose = 2.0f;
}
