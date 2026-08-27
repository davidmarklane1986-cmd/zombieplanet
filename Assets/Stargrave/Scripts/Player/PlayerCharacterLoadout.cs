using UnityEngine;

/// <summary>
/// Applies a <see cref="PlayableCharacterDef"/> to the scene player: swaps CharacterModel visuals
/// and writes move/combat/health stats. Called from the frontend before each run.
/// </summary>
public class PlayerCharacterLoadout : MonoBehaviour
{
    public const string CharacterModelChildName = "CharacterModel";

    [Tooltip("Optional override; otherwise uses PlayableCharacterCatalog selection.")]
    public PlayableCharacterDef overrideCharacter;

    PlayableCharacterDef _applied;
    GameObject _spawnedVisual;

    public PlayableCharacterDef AppliedCharacter => _applied;

    /// <summary>Apply the currently selected (or override) character. Safe to call repeatedly.</summary>
    public PlayableCharacterDef ApplySelected()
    {
        PlayableCharacterDef def = overrideCharacter != null
            ? overrideCharacter
            : PlayableCharacterCatalog.GetSelected();
        return Apply(def);
    }

    public PlayableCharacterDef Apply(PlayableCharacterDef def)
    {
        if (def == null)
        {
            Debug.LogWarning("[PlayerCharacterLoadout] No character def to apply.", this);
            return null;
        }

        _applied = def;
        ApplyVisual(def);
        ApplyStats(def);
        return def;
    }

    void ApplyVisual(PlayableCharacterDef def)
    {
        if (def.characterPrefab == null)
            return;

        Transform modelRoot = transform.Find(CharacterModelChildName);
        if (modelRoot == null)
        {
            var go = new GameObject(CharacterModelChildName);
            go.transform.SetParent(transform, false);
            modelRoot = go.transform;
        }

        // DestroyImmediate so the old mesh (and any gun parented to its hand) is gone before we equip.
        // Deferred Destroy() left the old hand alive for the frame — SetAssignedWeapon attached there,
        // then the old model+gun vanished and default guns on the new model stayed hidden.
        for (int i = modelRoot.childCount - 1; i >= 0; i--)
        {
            Transform child = modelRoot.GetChild(i);
            if (child != null)
                DestroyImmediate(child.gameObject);
        }
        _spawnedVisual = null;

        GameObject instance = Instantiate(def.characterPrefab, modelRoot);
        instance.name = def.characterPrefab.name;
        instance.transform.localPosition = Vector3.zero;
        instance.transform.localRotation = Quaternion.identity;
        instance.transform.localScale = Vector3.one;
        _spawnedVisual = instance;

        // Kenny Humanoid: Mecanim farmer retarget — strip any leftover Playables driver.
        EnsureKennyHumanoidMecanim(instance, def);

        var anim = GetComponent<PlayerCharacterAnimator>();
        if (anim != null)
        {
            anim.idleStateName = string.IsNullOrWhiteSpace(def.idleStateName) ? anim.idleStateName : def.idleStateName;
            anim.runStateName = string.IsNullOrWhiteSpace(def.runStateName) ? anim.runStateName : def.runStateName;
            anim.runBackStateName = string.IsNullOrWhiteSpace(def.runBackStateName) ? anim.runBackStateName : def.runBackStateName;
            anim.deathStateName = string.IsNullOrWhiteSpace(def.deathStateName) ? anim.deathStateName : def.deathStateName;
            anim.RebindToModel(modelRoot);
        }

        var align = GetComponent<PlayerCharacterAlign>();
        if (align != null)
        {
            align.rotationOffsetDegrees = 0f;
            align.RealignNow();
        }
    }

    /// <summary>
    /// Kenny playables use Humanoid farmer clips + <see cref="PlayerCharacterAnimator"/> (same as cowboy).
    /// Remove any leftover <see cref="KennyLocomotionDriver"/> from older prefabs.
    /// </summary>
    static void EnsureKennyHumanoidMecanim(GameObject instance, PlayableCharacterDef def)
    {
        if (instance == null || def == null)
            return;
        if (!string.IsNullOrEmpty(def.id) && def.id.Equals("cowboy", System.StringComparison.OrdinalIgnoreCase))
            return;

        Animator animator = instance.GetComponentInChildren<Animator>(true);
        if (animator == null)
            return;

        RuntimeAnimatorController ctrl = animator.runtimeAnimatorController;
        KennyLocomotionDriver[] drivers = instance.GetComponentsInChildren<KennyLocomotionDriver>(true);
        for (int i = 0; i < drivers.Length; i++)
        {
            KennyLocomotionDriver d = drivers[i];
            if (d == null)
                continue;
            if (ctrl == null && d.SavedController != null)
                ctrl = d.SavedController;
            Object.DestroyImmediate(d);
        }

        if (ctrl != null)
            animator.runtimeAnimatorController = ctrl;

        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        if (!animator.enabled)
            animator.enabled = true;

        FittedVisualScaleLock scaleLock = animator.GetComponent<FittedVisualScaleLock>();
        if (scaleLock == null)
            scaleLock = animator.gameObject.AddComponent<FittedVisualScaleLock>();
        scaleLock.Capture();
    }

    void ApplyStats(PlayableCharacterDef def)
    {
        var motor = GetComponent<PlanetMotor_InputSystem>();
        if (motor != null)
        {
            motor.moveSpeed = def.moveSpeed;
            motor.sprintSpeedMultiplier = def.sprintMultiplier;
        }

        var health = GetComponent<PlayerHealth>();
        if (health != null)
            health.ApplyCharacterMaxHealth(def.maxHealth);

        PlayerWeaponController weapons = health != null
            ? PlayerWeaponController.EnsureOn(health)
            : GetComponent<PlayerWeaponController>();
        if (weapons == null)
            weapons = gameObject.AddComponent<PlayerWeaponController>();

        if (health != null)
            PlayerSwimStamina.EnsureOn(health);

        WeaponDef assigned = def.assignedWeapon != null ? def.assignedWeapon : ResolveFallbackWeapon(def);
        weapons.SetAssignedWeapon(assigned);

        var buffs = GetComponent<PlayerBuffController>();
        if (buffs != null)
            buffs.RecacheBaseValues();
    }

    static WeaponDef ResolveFallbackWeapon(PlayableCharacterDef def)
    {
        if (def == null)
            return WeaponCatalog.GetById("blaster");

        string id = def.id != null ? def.id.ToLowerInvariant() : "";
        string want = id switch
        {
            "cowboy" => "shotgun",
            "skater" => "handgun",
            "cyborg" => "rifle",
            "criminal" => "blaster",
            "survivor" => "smg",
            _ => "blaster"
        };

        return WeaponCatalog.GetById(want)
               ?? WeaponCatalog.GetById("blaster")
               ?? WeaponCatalog.GetById("handgun");
    }

    /// <summary>Find or add loadout on the given player health root.</summary>
    public static PlayerCharacterLoadout EnsureOn(PlayerHealth player)
    {
        if (player == null)
            return null;
        var loadout = player.GetComponent<PlayerCharacterLoadout>();
        if (loadout == null)
            loadout = player.gameObject.AddComponent<PlayerCharacterLoadout>();
        return loadout;
    }
}
