using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Central, lazily-loaded catalogue of the game's sound effects. All clips live under
/// <c>Assets/Stargrave/Resources/Audio/</c> so they load at runtime with
/// <see cref="Resources.Load{T}(string)"/> without any scene/prefab wiring.
///
/// Clips are loaded on first access and cached for the lifetime of the app. Missing clips
/// resolve to null and are skipped safely by <see cref="AudioManager"/> (no exceptions).
/// </summary>
public static class SfxLibrary
{
    const string Root = "Audio/";

    // --- Player blaster (generated) ---
    static AudioClip[] _shoot;

    // --- Footsteps -------------------------------------------------------------------------------
    // Prefer short single-step clips (procedural / Kenney). HorrorSFX packs are multi-step walking
    // loops and must not be used as per-stride one-shots — they keep sounding after the player stops.
    static AudioClip[] _footsteps;                                   // Kenney rpg generic fallback
    static Dictionary<FootstepSurfaceKind, AudioClip[]> _footstepsBySurface;
    static Dictionary<string, AudioClip[]> _namedSets;              // cache for any "<prefix>_N" set

    // Real recorded footstep material sets (HorrorSFX). Exposed so the user can remap surfaces easily.
    public static AudioClip[] FootstepsLeaves => NamedSet("footstep_leaves_");
    public static AudioClip[] FootstepsMud => NamedSet("footstep_mud_");
    public static AudioClip[] FootstepsGravel => NamedSet("footstep_gravel_");
    public static AudioClip[] FootstepsConcrete => NamedSet("footstep_concrete_");
    public static AudioClip[] FootstepsWooden => NamedSet("footstep_wooden_");
    public static AudioClip[] FootstepsMetal => NamedSet("footstep_metal_");
    public static AudioClip[] FootstepsStairs => NamedSet("footstep_stairs_");
    public static AudioClip[] FootstepsCarpet => NamedSet("footstep_carpet_");
    public static AudioClip[] FootstepsWind => NamedSet("footstep_wind_");

    // Other HorrorSFX sets (available for the user to wire up; not used by default except Ambient Wind).
    public static AudioClip[] AmbientWind => NamedSet("ambient_wind_", 16);
    public static AudioClip[] MonsterGrowls => NamedSet("monster_growl_", 16);
    public static AudioClip[] CreakingDoors => NamedSet("creaking_door_", 16);

    // Flip to true to drive the zombie voice from the HorrorSFX "Monster Growl" clips instead of
    // ZombieSoundPack01 (ZombieSFX01..20). Falls back to the pack/procedural if growls are missing.
    public static bool UseMonsterGrowlForZombies = false;

    // --- UI (Kenney ui audio) ---
    static AudioClip[] _uiClicks;
    static AudioClip[] _uiRollovers;

    // --- Zombie vocals ---------------------------------------------------------------------------
    // Real recorded pack (Kenney ZombieSoundPack01), copied into Resources/Audio as ZombieSFX01..20.
    // We don't know which clip is a groan vs attack vs death, so by default ALL of them form one shared
    // pool used for idle groans, attacks and deaths (attack/death are pitched slightly for emphasis in
    // ZombieVoice). If the pack is missing we fall back to the procedural zombie_* clips.
    //
    // TO RE-MAP once you know which is which: list the 1-based indices below. Empty array = use the full
    // pool. e.g. ZombieAttackIndices = { 5, 12 }; ZombieDeathIndices = { 18, 20 };
    static readonly int[] ZombieAttackIndices = { };
    static readonly int[] ZombieDeathIndices = { };
    const int ZombiePackCount = 20;

    static AudioClip[] _zombiePack;          // real recorded pack (preferred)
    static AudioClip[] _zombieGroans;        // procedural fallback
    static AudioClip[] _zombieAttack;        // procedural fallback
    static AudioClip[] _zombieDeath;         // procedural fallback
    static AudioClip[] _zombieAttackPicks;   // resolved from pack indices (if any)
    static AudioClip[] _zombieDeathPicks;    // resolved from pack indices (if any)

    // --- Impact / hit (generated) ---
    static AudioClip[] _hit;

    public static AudioClip[] Shoot => _shoot ??= LoadSet("blaster_shoot_1", "blaster_shoot_2", "blaster_shoot_3");

    public static AudioClip[] Footsteps => _footsteps ??= LoadSet(
        "footstep00", "footstep01", "footstep02", "footstep03", "footstep04",
        "footstep05", "footstep06", "footstep07", "footstep08", "footstep09");

    public static AudioClip[] UiClicks => _uiClicks ??= LoadSet("click1", "click2", "click3");
    public static AudioClip[] UiRollovers => _uiRollovers ??= LoadSet("rollover1", "rollover2", "rollover3");

    /// <summary>Real recorded zombie pack (ZombieSFX01..20). Empty if the pack isn't in Resources.</summary>
    public static AudioClip[] ZombiePack => _zombiePack ??= LoadPaddedSet("ZombieSFX", ZombiePackCount, 2);

    // Procedural fallbacks (kept on disk; used only when the real pack is missing).
    public static AudioClip[] ZombieGroans => _zombieGroans ??= LoadSet("zombie_groan_1", "zombie_groan_2");
    public static AudioClip[] ZombieAttack => _zombieAttack ??= LoadSet("zombie_attack_1");
    public static AudioClip[] ZombieDeath => _zombieDeath ??= LoadSet("zombie_death_1");
    public static AudioClip[] Hit => _hit ??= LoadSet("hit_1", "hit_2");

    public static AudioClip RandomShoot() => Random(Shoot);

    /// <summary>Random Kenney footstep (generic fallback / surface variation disabled).</summary>
    public static AudioClip RandomFootstep() => Random(Footsteps);

    /// <summary>
    /// Random procedural footstep for a surface category (loads <c>footstep_&lt;kind&gt;_N</c> on first use).
    /// Falls back to the generic Kenney footsteps if a category has no generated clips.
    /// </summary>
    public static AudioClip RandomFootstep(FootstepSurfaceKind kind)
    {
        AudioClip[] set = FootstepsFor(kind);
        if (set == null || set.Length == 0)
            set = Footsteps;
        return Random(set);
    }

    static AudioClip[] FootstepsFor(FootstepSurfaceKind kind)
    {
        _footstepsBySurface ??= new Dictionary<FootstepSurfaceKind, AudioClip[]>();
        if (_footstepsBySurface.TryGetValue(kind, out AudioClip[] cached))
            return cached;

        // Per-surface priority: short single-step clips only (procedural), then Kenney generic.
        AudioClip[] set = kind switch
        {
            FootstepSurfaceKind.Grass => FirstNonEmpty("footstep_grass_"),
            FootstepSurfaceKind.Sand => FirstNonEmpty("footstep_sand_"),
            FootstepSurfaceKind.Rock => FirstNonEmpty("footstep_rock_"),
            FootstepSurfaceKind.Snow => FirstNonEmpty("footstep_snow_"),
            FootstepSurfaceKind.Water => FirstNonEmpty("footstep_water_"),
            FootstepSurfaceKind.Default => null, // Kenney generic below
            _ => null
        };

        if (set == null || set.Length == 0)
            set = Footsteps; // Kenney generic fallback

        _footstepsBySurface[kind] = set;
        return set;
    }

    // Returns the first numbered set (from the given prefixes) that has any clips; empty if none.
    static AudioClip[] FirstNonEmpty(params string[] prefixes)
    {
        for (int i = 0; i < prefixes.Length; i++)
        {
            AudioClip[] set = NamedSet(prefixes[i]);
            if (set != null && set.Length > 0)
                return set;
        }
        return System.Array.Empty<AudioClip>();
    }

    // Cached loader for any "<prefix>N" numbered set.
    static AudioClip[] NamedSet(string prefix, int max = 8)
    {
        _namedSets ??= new Dictionary<string, AudioClip[]>();
        if (_namedSets.TryGetValue(prefix, out AudioClip[] cached))
            return cached;
        AudioClip[] set = LoadNumberedSet(prefix, max);
        _namedSets[prefix] = set;
        return set;
    }
    public static AudioClip RandomUiClick() => Random(UiClicks);
    public static AudioClip RandomUiRollover() => Random(UiRollovers);
    /// <summary>The active zombie vocal pool: Monster Growl (if toggled) -> ZombieSoundPack01 -> procedural.</summary>
    static AudioClip[] ZombieVoicePool()
    {
        if (UseMonsterGrowlForZombies)
        {
            AudioClip[] growls = MonsterGrowls;
            if (growls != null && growls.Length > 0)
                return growls;
        }
        AudioClip[] pack = ZombiePack;
        if (pack != null && pack.Length > 0)
            return pack;
        return ZombieGroans;
    }

    /// <summary>Idle/ambient groan: any clip from the active zombie pool (pack/growls), procedural fallback.</summary>
    public static AudioClip RandomZombieGroan() => Random(ZombieVoicePool());

    /// <summary>
    /// Attack snarl: reserved pack indices if configured, else any pack clip, else procedural attack.
    /// </summary>
    public static AudioClip RandomZombieAttack()
    {
        AudioClip[] picks = ResolvePackPicks(ZombieAttackIndices, ref _zombieAttackPicks);
        if (picks != null && picks.Length > 0)
            return Random(picks);
        AudioClip[] pool = ZombieVoicePool();
        return (pool != null && pool.Length > 0) ? Random(pool) : Random(ZombieAttack);
    }

    /// <summary>
    /// Death rattle: reserved pack indices if configured, else any pack clip, else procedural death.
    /// </summary>
    public static AudioClip RandomZombieDeath()
    {
        AudioClip[] picks = ResolvePackPicks(ZombieDeathIndices, ref _zombieDeathPicks);
        if (picks != null && picks.Length > 0)
            return Random(picks);
        AudioClip[] pool = ZombieVoicePool();
        return (pool != null && pool.Length > 0) ? Random(pool) : Random(ZombieDeath);
    }

    public static AudioClip RandomHit() => Random(Hit);

    // Resolves the 1-based pack indices into a cached clip array (skips out-of-range / missing).
    static AudioClip[] ResolvePackPicks(int[] indices, ref AudioClip[] cache)
    {
        if (cache != null)
            return cache;
        if (indices == null || indices.Length == 0)
        {
            cache = System.Array.Empty<AudioClip>();
            return cache;
        }

        AudioClip[] pack = ZombiePack;
        var list = new List<AudioClip>(indices.Length);
        for (int i = 0; i < indices.Length; i++)
        {
            int idx = indices[i] - 1; // 1-based -> 0-based
            if (pack != null && idx >= 0 && idx < pack.Length && pack[idx] != null)
                list.Add(pack[idx]);
        }
        cache = list.ToArray();
        return cache;
    }

    static AudioClip Random(AudioClip[] set)
    {
        if (set == null || set.Length == 0)
            return null;
        if (set.Length == 1)
            return set[0];
        return set[UnityEngine.Random.Range(0, set.Length)];
    }

    // Loads "<prefix>01".."<prefix>NN" (zero-padded to <pad> digits). Tolerant: skips missing, no warnings.
    static AudioClip[] LoadPaddedSet(string prefix, int count, int pad)
    {
        var list = new List<AudioClip>(count);
        for (int i = 1; i <= count; i++)
        {
            AudioClip clip = Resources.Load<AudioClip>(Root + prefix + i.ToString().PadLeft(pad, '0'));
            if (clip != null)
                list.Add(clip);
        }
        return list.ToArray();
    }

    // Loads "<prefix>1", "<prefix>2", ... until a gap is hit. No warnings (variation counts vary).
    static AudioClip[] LoadNumberedSet(string prefix, int max)
    {
        var list = new List<AudioClip>(max);
        for (int i = 1; i <= max; i++)
        {
            AudioClip clip = Resources.Load<AudioClip>(Root + prefix + i);
            if (clip == null)
                break;
            list.Add(clip);
        }
        return list.ToArray();
    }

    static AudioClip[] LoadSet(params string[] names)
    {
        var list = new System.Collections.Generic.List<AudioClip>(names.Length);
        for (int i = 0; i < names.Length; i++)
        {
            AudioClip clip = Resources.Load<AudioClip>(Root + names[i]);
            if (clip != null)
                list.Add(clip);
            else
                Debug.LogWarning($"[SfxLibrary] Missing audio clip at Resources/{Root}{names[i]}");
        }
        return list.ToArray();
    }
}
