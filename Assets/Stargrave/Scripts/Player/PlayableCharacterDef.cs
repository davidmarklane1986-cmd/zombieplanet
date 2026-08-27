using UnityEngine;

/// <summary>
/// One selectable playable character: identity, move/survivability, assigned weapon, and visual prefab
/// parented under <c>Player/CharacterModel</c> at run start.
/// Combat ROF / reload / mag / damage come from <see cref="assignedWeapon"/>.
/// </summary>
[CreateAssetMenu(fileName = "PlayableCharacter", menuName = "Stargrave/Playable Character", order = 10)]
public class PlayableCharacterDef : ScriptableObject
{
    [Header("Identity")]
    public string id = "cowboy";
    public string displayName = "Cowboy";
    [TextArea(1, 3)]
    public string blurb = "Baseline gunslinger.";

    [Header("Movement")]
    public float moveSpeed = 8f;
    public float sprintMultiplier = 1.45f;

    [Header("Assigned weapon")]
    [Tooltip("Permanent gun for this character. Loot drops temporarily replace it until ammo runs out.")]
    public WeaponDef assignedWeapon;

    [Header("Survivability")]
    [Min(1)] public int maxHealth = 100;

    [Header("Visual")]
    [Tooltip("Instanced under Player/CharacterModel. Leave null to keep the scene model.")]
    public GameObject characterPrefab;

    [Tooltip("PlayerCharacterAlign yaw: positive turns the mesh left of capsule forward.")]
    public float modelYawOffsetDegrees;

    [Tooltip("Extra yaw (degrees) on Kenny hips after the Animator while running. Positive yaws toward capsule forward.")]
    public float hipsYawOffsetDegrees;

    [Tooltip("Optional UI tint for the select card.")]
    public Color accentColor = new Color(0.85f, 0.75f, 0.35f, 1f);

    [Header("Animator states (must exist on the prefab controller)")]
    public string idleStateName = "root|Idle_Menu";
    public string runStateName = "root|Run_Front";
    public string runBackStateName = "root|Run_Back";
    public string deathStateName = "root|Death";

    public string StatsSummaryLine()
    {
        if (assignedWeapon != null)
            return $"SPD {moveSpeed:0.#}  HP {maxHealth}  |  {assignedWeapon.StatsSummaryLine()}";
        return $"SPD {moveSpeed:0.#}  HP {maxHealth}";
    }
}
