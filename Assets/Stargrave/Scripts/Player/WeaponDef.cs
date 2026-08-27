using UnityEngine;

/// <summary>
/// One gun archetype: base combat stats + fire profile + visuals.
/// Characters assign one permanently; the same defs drop from zombies as finite-ammo loot.
/// </summary>
[CreateAssetMenu(fileName = "Weapon", menuName = "Stargrave/Weapon", order = 11)]
public class WeaponDef : ScriptableObject
{
    [Header("Identity")]
    public string id = "blaster";
    public string displayName = "Blaster";

    [Header("Visuals")]
    [Tooltip("Mesh parented under the hand when this weapon is equipped as loot (or when no baked-in gun matches).")]
    public GameObject heldVisualPrefab;
    [Tooltip("World pickup prefab (Resources). If null, a simple runtime pickup is spawned.")]
    public GameObject worldPickupPrefab;
    [Tooltip("Tint applied to projectiles / trails for this weapon.")]
    public Color projectileColor = new Color(1f, 0.55f, 0.08f, 1f);
    [Tooltip("Local euler on the hand bone. Barrel should aim along aim forward; grip toward palm/floor.")]
    public Vector3 heldLocalEulerDegrees = new Vector3(-90f, 180f, 90f);
    [Tooltip("Target world-space length of the held mesh (longest AABB axis).")]
    [Min(0.05f)] public float heldWorldLength = 0.42f;
    [Tooltip("Extra local euler applied only to the HUD 3D tray preview.")]
    public Vector3 hudPreviewEulerDegrees;

    [Header("Combat (base — buffs multiply on top)")]
    [Tooltip("Seconds between shots (or between bursts when burstCount > 1).")]
    public float fireCooldown = 0.28f;
    [Tooltip("Seconds to refill the magazine after it empties.")]
    public float reloadDuration = 1.4f;
    [Min(1)] public int shotsPerMagazine = 8;
    [Min(1)] public int damagePerShot = 1;

    [Header("Shot pattern")]
    [Tooltip("How many pellets/rays per trigger (shotgun). 1 = single projectile.")]
    [Min(1)] public int pelletCount = 1;
    [Tooltip("Cone half-angle (degrees) for pellet / accuracy spread.")]
    [Min(0f)] public float spreadDegrees = 0f;

    [Header("Burst (rifle)")]
    [Tooltip("Rounds fired per trigger pull before the main fire cooldown. 1 = semi/auto single.")]
    [Min(1)] public int burstCount = 1;
    [Tooltip("Delay between rounds inside a burst.")]
    [Min(0f)] public float burstShotInterval = 0.06f;

    [Header("Damage falloff")]
    [Tooltip("Full damage out to this distance (metres).")]
    [Min(0f)] public float damageFalloffStart = 18f;
    [Tooltip("Damage reaches the minimum multiplier by this distance.")]
    [Min(0f)] public float damageFalloffEnd = 50f;
    [Tooltip("Damage multiplier at/after falloff end (buffs still apply).")]
    [Range(0.05f, 1f)] public float damageFalloffMinMultiplier = 0.4f;

    [Header("Loot")]
    [Tooltip("Minimum magazines (clips) of ammo on a zombie drop.")]
    [Min(1)] public int lootClipsMin = 1;
    [Tooltip("Maximum magazines (clips) of ammo on a zombie drop (inclusive).")]
    [Min(1)] public int lootClipsMax = 4;
    [Tooltip("Legacy / fallback total shots if clip range is invalid. Prefer lootClipsMin/Max.")]
    [Min(1)] public int lootAmmo = 20;
    [Tooltip("Relative weight when rolling a random zombie weapon drop.")]
    [Min(0f)] public float dropWeight = 1f;

    /// <summary>Random shot budget for a zombie drop: N clips × magazine size.</summary>
    public int RollLootAmmo()
    {
        int mag = Mathf.Max(1, shotsPerMagazine);
        int minClips = Mathf.Max(1, lootClipsMin);
        int maxClips = Mathf.Max(minClips, lootClipsMax);
        int clips = Random.Range(minClips, maxClips + 1);
        return Mathf.Max(1, clips * mag);
    }

    public string StatsSummaryLine()
    {
        float rof = 1f / Mathf.Max(0.01f, fireCooldown);
        string extra = pelletCount > 1 ? $"  x{pelletCount}pel" : (burstCount > 1 ? $"  burst{burstCount}" : "");
        return $"{displayName}: ROF {rof:0.#}/s  RL {reloadDuration:0.#}s  MAG {shotsPerMagazine}  DMG {damagePerShot}{extra}";
    }
}
