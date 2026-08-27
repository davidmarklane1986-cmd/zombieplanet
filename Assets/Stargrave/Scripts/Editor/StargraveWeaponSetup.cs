#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds WeaponDef assets + held/world pickup prefabs, and wires playable characters.
/// Run: Tools/Stargrave/Build Weapon Prefabs
/// </summary>
public static class StargraveWeaponSetup
{
    const string BlasterKitFbx = "Assets/ThirdParty/Kenny/BlasterKit/Models/FBX format";
    const string FarmerPrefabPath = "Assets/GAMWILL/Zombie Shooter Series Farmer Cowboy/Prefab/T_Pose.prefab";

    const string ResourcesDir = "Assets/Stargrave/Resources/Weapons";
    const string HeldDir = ResourcesDir + "/Held";
    const string PickupDir = ResourcesDir + "/Pickups";

    [MenuItem("Tools/Stargrave/Build Weapon Prefabs")]
    public static void BuildWeaponPrefabs()
    {
        EnsureFolder(ResourcesDir);
        EnsureFolder(HeldDir);
        EnsureFolder(PickupDir);

        GameObject heldShotgun = BuildHeldFromFarmerGun(HeldDir + "/Held_Shotgun.prefab");
        GameObject heldBlaster = BuildHeldFromFbx(BlasterKitFbx + "/blaster-a.fbx", HeldDir + "/Held_Blaster.prefab", 0.38f);
        GameObject heldRifle = BuildHeldFromFbx(BlasterKitFbx + "/blaster-r.fbx", HeldDir + "/Held_Rifle.prefab", 0.55f);
        GameObject heldHandgun = BuildHeldFromFbx(BlasterKitFbx + "/blaster-c.fbx", HeldDir + "/Held_Handgun.prefab", 0.28f);
        GameObject heldSmg = BuildHeldFromFbx(BlasterKitFbx + "/blaster-e.fbx", HeldDir + "/Held_SMG.prefab", 0.42f);

        // Fallbacks if specific FBX missing
        if (heldRifle == null)
            heldRifle = heldBlaster;
        if (heldHandgun == null)
            heldHandgun = heldBlaster;
        if (heldSmg == null)
            heldSmg = heldHandgun != null ? heldHandgun : heldBlaster;
        if (heldShotgun == null)
            heldShotgun = heldRifle != null ? heldRifle : heldBlaster;

        GameObject pickupShotgun = BuildPickupPrefab("WeaponPickup_Shotgun", heldShotgun, PickupDir + "/WeaponPickup_Shotgun.prefab", 0.9f);
        GameObject pickupBlaster = BuildPickupPrefab("WeaponPickup_Blaster", heldBlaster, PickupDir + "/WeaponPickup_Blaster.prefab", 0.85f);
        GameObject pickupRifle = BuildPickupPrefab("WeaponPickup_Rifle", heldRifle, PickupDir + "/WeaponPickup_Rifle.prefab", 1.0f);
        GameObject pickupHandgun = BuildPickupPrefab("WeaponPickup_Handgun", heldHandgun, PickupDir + "/WeaponPickup_Handgun.prefab", 0.7f);
        GameObject pickupSmg = BuildPickupPrefab("WeaponPickup_SMG", heldSmg, PickupDir + "/WeaponPickup_SMG.prefab", 0.8f);

        WeaponDef shotgun = CreateOrUpdateWeapon(
            ResourcesDir + "/Weapon_Shotgun.asset",
            "shotgun", "Shotgun",
            fireCooldown: 0.65f, reload: 2.2f, mag: 2, damage: 1, lootAmmo: 10, dropWeight: 0.85f,
            heldShotgun, pickupShotgun,
            projectileColor: new Color(1f, 0.72f, 0.15f, 1f),
            pelletCount: 6, spreadDegrees: 9f, burstCount: 1, burstShotInterval: 0.06f,
            falloffStart: 8f, falloffEnd: 22f, falloffMin: 0.25f);

        WeaponDef blaster = CreateOrUpdateWeapon(
            ResourcesDir + "/Weapon_Blaster.asset",
            "blaster", "Blaster",
            fireCooldown: 0.28f, reload: 1.4f, mag: 8, damage: 1, lootAmmo: 28, dropWeight: 1.1f,
            heldBlaster, pickupBlaster,
            projectileColor: new Color(0.35f, 0.95f, 1f, 1f),
            pelletCount: 1, spreadDegrees: 0.6f, burstCount: 1, burstShotInterval: 0.06f,
            falloffStart: 20f, falloffEnd: 55f, falloffMin: 0.45f);

        WeaponDef rifle = CreateOrUpdateWeapon(
            ResourcesDir + "/Weapon_Rifle.asset",
            "rifle", "Rifle",
            fireCooldown: 0.42f, reload: 1.8f, mag: 12, damage: 2, lootAmmo: 36, dropWeight: 1f,
            heldRifle, pickupRifle,
            projectileColor: new Color(0.35f, 1f, 0.45f, 1f),
            pelletCount: 1, spreadDegrees: 1.2f, burstCount: 3, burstShotInterval: 0.07f,
            falloffStart: 25f, falloffEnd: 70f, falloffMin: 0.55f);

        WeaponDef handgun = CreateOrUpdateWeapon(
            ResourcesDir + "/Weapon_Handgun.asset",
            "handgun", "Handgun",
            fireCooldown: 0.14f, reload: 1.1f, mag: 10, damage: 1, lootAmmo: 30, dropWeight: 1.2f,
            heldHandgun, pickupHandgun,
            projectileColor: new Color(1f, 0.35f, 0.2f, 1f),
            pelletCount: 1, spreadDegrees: 2f, burstCount: 1, burstShotInterval: 0.06f,
            falloffStart: 12f, falloffEnd: 35f, falloffMin: 0.35f);

        WeaponDef smg = CreateOrUpdateWeapon(
            ResourcesDir + "/Weapon_SMG.asset",
            "smg", "SMG",
            fireCooldown: 0.09f, reload: 1.6f, mag: 18, damage: 1, lootAmmo: 40, dropWeight: 1.05f,
            heldSmg, pickupSmg,
            projectileColor: new Color(0.85f, 0.35f, 1f, 1f),
            pelletCount: 1, spreadDegrees: 3.5f, burstCount: 1, burstShotInterval: 0.05f,
            falloffStart: 10f, falloffEnd: 30f, falloffMin: 0.3f);

        WireCharacters(shotgun, blaster, rifle, handgun, smg);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        WeaponCatalog.InvalidateCache();

        Debug.Log($"Stargrave: Build Weapon Prefabs done. " +
                  $"Shotgun={(shotgun != null)}, Blaster={(blaster != null)}, " +
                  $"Rifle={(rifle != null)}, Handgun={(handgun != null)}, SMG={(smg != null)}.");
    }

    static void WireCharacters(WeaponDef shotgun, WeaponDef blaster, WeaponDef rifle, WeaponDef handgun, WeaponDef smg)
    {
        AssignCharacterWeapon("Assets/Stargrave/Resources/PlayableCharacters/Char_Cowboy.asset", shotgun);
        AssignCharacterWeapon("Assets/Stargrave/Resources/PlayableCharacters/Char_Skater.asset", handgun);
        AssignCharacterWeapon("Assets/Stargrave/Resources/PlayableCharacters/Char_Cyborg.asset", rifle);
        AssignCharacterWeapon("Assets/Stargrave/Resources/PlayableCharacters/Char_Criminal.asset", blaster);
        AssignCharacterWeapon("Assets/Stargrave/Resources/PlayableCharacters/Char_Survivor.asset", smg);
    }

    static void AssignCharacterWeapon(string characterPath, WeaponDef weapon)
    {
        var def = AssetDatabase.LoadAssetAtPath<PlayableCharacterDef>(characterPath);
        if (def == null || weapon == null)
            return;
        def.assignedWeapon = weapon;
        EditorUtility.SetDirty(def);
    }

    static WeaponDef CreateOrUpdateWeapon(
        string assetPath,
        string id,
        string displayName,
        float fireCooldown,
        float reload,
        int mag,
        int damage,
        int lootAmmo,
        float dropWeight,
        GameObject held,
        GameObject pickup,
        Color projectileColor,
        int pelletCount,
        float spreadDegrees,
        int burstCount,
        float burstShotInterval,
        float falloffStart,
        float falloffEnd,
        float falloffMin)
    {
        WeaponDef def = AssetDatabase.LoadAssetAtPath<WeaponDef>(assetPath);
        if (def == null)
        {
            def = ScriptableObject.CreateInstance<WeaponDef>();
            AssetDatabase.CreateAsset(def, assetPath);
        }

        def.id = id;
        def.displayName = displayName;
        def.fireCooldown = fireCooldown;
        def.reloadDuration = reload;
        def.shotsPerMagazine = mag;
        def.damagePerShot = damage;
        def.lootAmmo = lootAmmo;
        def.lootClipsMin = Mathf.Max(1, lootAmmo / Mathf.Max(1, mag));
        def.lootClipsMax = Mathf.Max(def.lootClipsMin, Mathf.CeilToInt(lootAmmo / (float)Mathf.Max(1, mag)) + 2);
        def.dropWeight = dropWeight;
        def.heldVisualPrefab = held;
        def.worldPickupPrefab = pickup;
        def.projectileColor = projectileColor;
        def.heldLocalEulerDegrees = new Vector3(-90f, 180f, 90f);
        def.heldWorldLength = 0.42f;
        if (id == "shotgun")
            def.hudPreviewEulerDegrees = new Vector3(180f, 0f, 0f);
        def.pelletCount = Mathf.Max(1, pelletCount);
        def.spreadDegrees = Mathf.Max(0f, spreadDegrees);
        def.burstCount = Mathf.Max(1, burstCount);
        def.burstShotInterval = Mathf.Max(0f, burstShotInterval);
        def.damageFalloffStart = Mathf.Max(0f, falloffStart);
        def.damageFalloffEnd = Mathf.Max(falloffStart + 0.01f, falloffEnd);
        def.damageFalloffMinMultiplier = Mathf.Clamp(falloffMin, 0.05f, 1f);
        EditorUtility.SetDirty(def);
        return def;
    }

    static GameObject BuildHeldFromFbx(string fbxPath, string prefabPath, float targetWorldLength)
    {
        GameObject src = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
        if (src == null)
        {
            Debug.LogWarning($"Stargrave: missing gun FBX '{fbxPath}'.");
            return null;
        }

        var root = new GameObject(Path.GetFileNameWithoutExtension(prefabPath));
        GameObject model = (GameObject)PrefabUtility.InstantiatePrefab(src);
        model.transform.SetParent(root.transform, false);
        model.transform.localPosition = Vector3.zero;
        model.transform.localRotation = Quaternion.identity;
        model.transform.localScale = Vector3.one;
        // Unpack so the held prefab owns MeshRenderers (nested FBX links can look empty at runtime).
        if (PrefabUtility.IsPartOfPrefabInstance(model))
            PrefabUtility.UnpackPrefabInstance(model, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

        foreach (var col in root.GetComponentsInChildren<Collider>(true))
            Object.DestroyImmediate(col);

        if (TryGetWorldBounds(root, out Bounds b))
        {
            float longest = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            if (longest > 1e-4f)
                root.transform.localScale = Vector3.one * (targetWorldLength / longest);
        }

        GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        Object.DestroyImmediate(root);
        return saved;
    }

    static GameObject BuildHeldFromFarmerGun(string prefabPath)
    {
        GameObject farmer = AssetDatabase.LoadAssetAtPath<GameObject>(FarmerPrefabPath);
        if (farmer == null)
        {
            Debug.LogWarning($"Stargrave: missing farmer prefab '{FarmerPrefabPath}'.");
            return null;
        }

        Transform gunTf = FindDescendant(farmer.transform, "Gun");
        if (gunTf == null)
        {
            Debug.LogWarning("Stargrave: farmer prefab has no Gun child.");
            return null;
        }

        var root = new GameObject("Held_Shotgun");
        GameObject gunCopy = Object.Instantiate(gunTf.gameObject);
        gunCopy.name = "Gun";
        gunCopy.transform.SetParent(root.transform, false);
        gunCopy.transform.localPosition = Vector3.zero;
        gunCopy.transform.localRotation = Quaternion.identity;
        gunCopy.transform.localScale = Vector3.one;

        foreach (var col in root.GetComponentsInChildren<Collider>(true))
            Object.DestroyImmediate(col);

        if (TryGetWorldBounds(root, out Bounds b))
        {
            float longest = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            if (longest > 1e-4f)
                root.transform.localScale = Vector3.one * (0.55f / longest);
        }

        GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        Object.DestroyImmediate(root);
        return saved;
    }

    static GameObject BuildPickupPrefab(string name, GameObject heldVisual, string prefabPath, float targetSize)
    {
        var root = new GameObject(name);
        if (heldVisual != null)
        {
            GameObject visual = (GameObject)PrefabUtility.InstantiatePrefab(heldVisual);
            if (visual == null)
                visual = Object.Instantiate(heldVisual);
            visual.name = "Visual";
            visual.transform.SetParent(root.transform, false);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.Euler(0f, 0f, -25f);
            visual.transform.localScale = Vector3.one;

            if (TryGetWorldBounds(visual, out Bounds bounds))
            {
                float maxDim = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
                float scale = maxDim > 1e-4f ? targetSize / maxDim : 1f;
                visual.transform.localScale = Vector3.one * scale;
                if (TryGetWorldBounds(visual, out bounds))
                    visual.transform.position += root.transform.position - bounds.center;
            }

            foreach (var col in visual.GetComponentsInChildren<Collider>(true))
                Object.DestroyImmediate(col);
        }

        var sphere = root.AddComponent<SphereCollider>();
        sphere.isTrigger = true;
        sphere.radius = 0.75f;

        var pickup = root.AddComponent<WeaponPickup>();
        pickup.sizeMultiplier = 1f;
        pickup.spinSpeedDegrees = 72f;
        pickup.lifetimeSeconds = 0f;

        GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        Object.DestroyImmediate(root);
        return saved;
    }

    static bool TryGetWorldBounds(GameObject go, out Bounds bounds)
    {
        var renderers = go.GetComponentsInChildren<Renderer>(true);
        bounds = default;
        bool found = false;
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] == null)
                continue;
            if (!found)
            {
                bounds = renderers[i].bounds;
                found = true;
            }
            else
                bounds.Encapsulate(renderers[i].bounds);
        }
        return found;
    }

    static Transform FindDescendant(Transform root, string name)
    {
        if (root.name == name)
            return root;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform found = FindDescendant(root.GetChild(i), name);
            if (found != null)
                return found;
        }
        return null;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
            return;
        string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
        string leaf = Path.GetFileName(path);
        if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            EnsureFolder(parent);
        if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(leaf))
            AssetDatabase.CreateFolder(parent, leaf);
    }
}
#endif
