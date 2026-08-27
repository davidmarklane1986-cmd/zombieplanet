using UnityEngine;

/// <summary>
/// Owns the player's assigned character weapon and optional finite-ammo loot gun.
/// Pushes base stats into <see cref="PlayerShooting"/>; buffs multiply on top.
/// </summary>
public sealed class PlayerWeaponController : MonoBehaviour
{
    public const string RuntimeHeldName = "HeldRuntimeWeapon";

    WeaponDef _assigned;
    WeaponDef _loot;
    int _lootAmmoRemaining;
    GameObject _runtimeHeld;
    PlayerShooting _shooting;

    public WeaponDef AssignedWeapon => _assigned;
    public WeaponDef EquippedWeapon => _loot != null ? _loot : _assigned;
    public bool HasLootWeapon => _loot != null;
    public int LootAmmoRemaining => _lootAmmoRemaining;

    public static PlayerWeaponController EnsureOn(PlayerHealth player)
    {
        if (player == null)
            return null;
        var c = player.GetComponent<PlayerWeaponController>();
        if (c == null)
            c = player.gameObject.AddComponent<PlayerWeaponController>();
        return c;
    }

    void Awake()
    {
        ResolveShooting();
    }

    void ResolveShooting()
    {
        if (_shooting != null)
            return;

        // Scene layout: PlayerShooting often lives on CM_Player (camera), not the Player root.
        _shooting = GetComponent<PlayerShooting>();
        if (_shooting == null)
            _shooting = GetComponentInChildren<PlayerShooting>(true);
        if (_shooting == null)
            _shooting = GetComponentInParent<PlayerShooting>(true);
        if (_shooting == null)
            _shooting = Object.FindFirstObjectByType<PlayerShooting>(FindObjectsInactive.Include);
    }

    /// <summary>Called from character loadout — sets permanent gun and clears loot.</summary>
    public void SetAssignedWeapon(WeaponDef weapon)
    {
        _assigned = weapon;
        ClearLootInternal(dropRemaining: false);
        EquipAssigned();
        // Animator / bind poses finish later in the frame — re-seat the held mesh once bones are live.
        if (isActiveAndEnabled && Application.isPlaying)
            StartCoroutine(CoRefreshVisualsNextFrame());
    }

    System.Collections.IEnumerator CoRefreshVisualsNextFrame()
    {
        yield return null;
        if (_loot != null)
            ApplyVisuals(_loot, useRuntimeHeld: true);
        else
            EquipAssigned();
    }

    public void ResetToAssigned()
    {
        ClearLootInternal(dropRemaining: false);
        EquipAssigned();
    }

    public bool TryPickupLoot(WeaponDef weapon, int ammo)
    {
        if (weapon == null || ammo <= 0)
            return false;

        // Drop current loot with remaining ammo before swapping.
        if (_loot != null && _lootAmmoRemaining > 0)
            DropLootPickup(_loot, _lootAmmoRemaining);

        _loot = weapon;
        _lootAmmoRemaining = Mathf.Max(1, ammo);
        ApplyWeaponStats(weapon, lootMode: true);
        ApplyVisuals(weapon, useRuntimeHeld: true);
        return true;
    }

    /// <summary>
    /// Consume one loot shot. Returns false only if no ammo was left to spend.
    /// On the final shot returns true after unequipping back to the assigned weapon.
    /// </summary>
    public bool TryConsumeLootShot()
    {
        if (_loot == null)
            return true;

        if (_lootAmmoRemaining <= 0)
        {
            DiscardEmptyLoot();
            return false;
        }

        _lootAmmoRemaining--;
        if (_lootAmmoRemaining <= 0)
            DiscardEmptyLoot();
        return true;
    }

    /// <summary>Magazine refill size while holding loot (capped by remaining ammo).</summary>
    public int GetLootMagazineFill()
    {
        if (_loot == null)
            return 0;
        return Mathf.Min(Mathf.Max(1, _loot.shotsPerMagazine), Mathf.Max(0, _lootAmmoRemaining));
    }

    void DiscardEmptyLoot()
    {
        WeaponDef spent = _loot;
        _loot = null;
        _lootAmmoRemaining = 0;
        // Brief empty discard on the ground (ammo 0 — cannot be picked up).
        if (spent != null)
            WeaponPickup.Spawn(spent, 0, transform.position + transform.up * 0.6f + transform.forward * 0.4f, lifetimeSeconds: 2.5f);

        EquipAssigned();
    }

    void ClearLootInternal(bool dropRemaining)
    {
        if (dropRemaining && _loot != null && _lootAmmoRemaining > 0)
            DropLootPickup(_loot, _lootAmmoRemaining);

        _loot = null;
        _lootAmmoRemaining = 0;
        DestroyRuntimeHeld();
    }

    void DropLootPickup(WeaponDef def, int ammo)
    {
        Vector3 pos = transform.position + transform.up * 0.5f + transform.forward * 0.6f;
        WeaponPickup.Spawn(def, ammo, pos);
    }

    void EquipAssigned()
    {
        DestroyRuntimeHeld();
        // Farmer/cowboy baked Gun is authored larger than the shared runtime held length.
        if (ShouldKeepBakedAssignedGun(_assigned))
        {
            SetDefaultGunMeshesVisible(true);
            if (_assigned != null)
                ApplyWeaponStats(_assigned, lootMode: false);
            else
                ApplyFallbackCharacterStats();
            RebindMuzzle();
            return;
        }

        bool useRuntimeHeld = NeedsRuntimeHeldForAssigned(_assigned);
        if (!useRuntimeHeld)
            SetDefaultGunMeshesVisible(true);
        if (_assigned != null)
            ApplyWeaponStats(_assigned, lootMode: false);
        else
            ApplyFallbackCharacterStats();
        ApplyVisuals(_assigned, useRuntimeHeld: useRuntimeHeld);
    }

    /// <summary>Kenny kits use runtime held meshes. Farmer shotgun keeps the baked Gun.</summary>
    bool NeedsRuntimeHeldForAssigned(WeaponDef weapon)
    {
        return weapon != null && weapon.heldVisualPrefab != null;
    }

    bool ShouldKeepBakedAssignedGun(WeaponDef weapon)
    {
        if (weapon == null)
            return true;

        Transform model = transform.Find(PlayerCharacterLoadout.CharacterModelChildName);
        Transform search = model != null ? model : transform;
        Transform heldBlaster = FindDescendantByName(search, "HeldBlaster");
        if (heldBlaster != null && !IsUnderRuntimeHeld(heldBlaster))
            return false;

        Transform gun = FindDescendantByName(search, "Gun");
        if (gun == null || IsUnderRuntimeHeld(gun))
            return false;

        string id = weapon.id != null ? weapon.id.ToLowerInvariant() : "";
        return id == "shotgun" || string.IsNullOrEmpty(id);
    }

    void ApplyWeaponStats(WeaponDef weapon, bool lootMode)
    {
        ResolveShooting();
        if (_shooting == null || weapon == null)
            return;

        _shooting.fireCooldown = Mathf.Max(0.01f, weapon.fireCooldown);
        _shooting.reloadCooldown = Mathf.Max(0.05f, weapon.reloadDuration);
        _shooting.shotsPerMagazine = Mathf.Max(1, weapon.shotsPerMagazine);
        _shooting.damagePerShot = Mathf.Max(1, weapon.damagePerShot);
        _shooting.ApplyWeaponFireProfile(weapon);
        _shooting.SetLootAmmoMode(lootMode);
        _shooting.OnCharacterLoadoutApplied();
    }

    void ApplyFallbackCharacterStats()
    {
        ResolveShooting();
        if (_shooting == null)
            return;
        _shooting.SetLootAmmoMode(false);
        _shooting.OnCharacterLoadoutApplied();
    }

    void ApplyVisuals(WeaponDef weapon, bool useRuntimeHeld)
    {
        if (!useRuntimeHeld || weapon == null || weapon.heldVisualPrefab == null)
        {
            DestroyRuntimeHeld();
            SetDefaultGunMeshesVisible(true);
            RebindMuzzle();
            return;
        }

        DestroyRuntimeHeld();

        Transform model = transform.Find(PlayerCharacterLoadout.CharacterModelChildName);
        Transform search = model != null ? model : transform;
        Transform carrySocket = EnsureCarrySocket(search);

        Transform attach = carrySocket != null && carrySocket.parent != null
            ? carrySocket.parent
            : FindAttachHand(preferWeaponBone: true);

        if (attach == null)
        {
            Transform heldBlaster = FindDescendantByName(search, "HeldBlaster");
            if (heldBlaster != null && !IsUnderRuntimeHeld(heldBlaster))
                attach = heldBlaster.parent;
        }

        if (attach == null)
        {
            SetDefaultGunMeshesVisible(true);
            RebindMuzzle();
            return;
        }

        _runtimeHeld = Instantiate(weapon.heldVisualPrefab, attach);
        _runtimeHeld.name = RuntimeHeldName;
        foreach (var col in _runtimeHeld.GetComponentsInChildren<Collider>(true))
            Object.Destroy(col);

        if (carrySocket != null)
            FitHeldToFarmerGunSocket(_runtimeHeld, carrySocket, weapon);
        else
            FitHeldWeapon(_runtimeHeld, attach, weapon);

        var driver = search.GetComponentInChildren<RuntimeWeaponBoneDriver>(true);
        if (driver != null)
            driver.SnapNow();

        EnsureAllRenderersEnabled(_runtimeHeld);
        EnsureMuzzleBone(_runtimeHeld.transform);
        ApplyMatteToHeld(_runtimeHeld);

        if (HasHeldMesh(_runtimeHeld) && RuntimeHeldLooksUsable(_runtimeHeld))
        {
            SetDefaultGunMeshesVisible(false);
        }
        else
        {
            DestroyRuntimeHeld();
            SetDefaultGunMeshesVisible(true);
        }

        RebindMuzzle();
    }

    static void ApplyMatteToHeld(GameObject root)
    {
        if (root == null)
            return;
        Renderer[] rs = root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < rs.Length; i++)
        {
            if (rs[i] == null)
                continue;
            rs[i].enabled = true;
            var mats = rs[i].materials;
            for (int m = 0; m < mats.Length; m++)
                ModelMatteLighting.MakeMatte(mats[m], ambientFill: ModelMatteLighting.PlayerAmbientFill);
            rs[i].materials = mats;
        }
    }

    static float GetLongestMeshLocalSize(GameObject root)
    {
        float longest = 0f;
        MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < filters.Length; i++)
        {
            Mesh mesh = filters[i] != null ? filters[i].sharedMesh : null;
            if (mesh == null)
                continue;
            Vector3 s = mesh.bounds.size;
            longest = Mathf.Max(longest, Mathf.Max(s.x, Mathf.Max(s.y, s.z)));
        }

        SkinnedMeshRenderer[] skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int i = 0; i < skinned.Length; i++)
        {
            Mesh mesh = skinned[i] != null ? skinned[i].sharedMesh : null;
            if (mesh == null)
                continue;
            Vector3 s = mesh.bounds.size;
            longest = Mathf.Max(longest, Mathf.Max(s.x, Mathf.Max(s.y, s.z)));
        }

        return longest;
    }

    static bool HasHeldMesh(GameObject root)
    {
        if (root == null)
            return false;
        if (GetLongestMeshLocalSize(root) > 1e-4f)
            return true;
        Renderer[] rs = root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < rs.Length; i++)
        {
            if (rs[i] != null)
                return true;
        }
        return false;
    }

    static void EnsureAllRenderersEnabled(GameObject root)
    {
        if (root == null)
            return;
        root.SetActive(true);
        Renderer[] rs = root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < rs.Length; i++)
        {
            if (rs[i] == null)
                continue;
            rs[i].enabled = true;
            if (!rs[i].gameObject.activeSelf)
                rs[i].gameObject.SetActive(true);
        }
    }

    public void RefreshHeldMuzzle()
    {
        if (_runtimeHeld != null)
            EnsureMuzzleBone(_runtimeHeld.transform);
        RebindMuzzle();
    }

    void RebindMuzzle()
    {
        ResolveShooting();
        if (_shooting != null)
            _shooting.OnCharacterLoadoutApplied();
    }

    void DestroyRuntimeHeld()
    {
        // Must be immediate: deferred Destroy() leaves HeldRuntimeWeapon/Gun in the hierarchy for the
        // rest of the frame, so the next EquipAssigned attaches to the dying gun and then both vanish.
        if (_runtimeHeld != null)
        {
            DestroyImmediate(_runtimeHeld);
            _runtimeHeld = null;
        }

        Transform model = transform.Find(PlayerCharacterLoadout.CharacterModelChildName);
        if (model == null)
            return;
        Transform[] all = model.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] != null && all[i].name == RuntimeHeldName)
                DestroyImmediate(all[i].gameObject);
        }
    }

    void SetDefaultGunMeshesVisible(bool visible)
    {
        Transform model = transform.Find(PlayerCharacterLoadout.CharacterModelChildName);
        if (model == null)
            model = transform;

        Transform[] all = model.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            Transform t = all[i];
            if (t == null)
                continue;
            // Never toggle meshes that belong to the runtime held weapon (child named "Gun").
            if (IsUnderRuntimeHeld(t))
                continue;

            string n = t.name;
            if (n != "HeldBlaster" && n != "Gun" && n != "Weapon_Bone")
                continue;

            // Empty runtime carry socket — keep it active so Weapon_Bone posing still works.
            if (n == "Gun" && t.GetComponent<Renderer>() == null
                && t.GetComponentInChildren<Renderer>(true) == null)
                continue;

            // Weapon_Bone: only toggle child Gun renderers, keep bone active for hierarchy.
            if (n == "Weapon_Bone")
            {
                SetRenderersEnabled(t, visible);
                continue;
            }

            t.gameObject.SetActive(visible);
        }
    }

    static bool IsUnderRuntimeHeld(Transform t)
    {
        while (t != null)
        {
            if (t.name == RuntimeHeldName)
                return true;
            t = t.parent;
        }
        return false;
    }

    static void SetRenderersEnabled(Transform root, bool enabled)
    {
        Renderer[] rs = root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < rs.Length; i++)
        {
            if (rs[i] == null)
                continue;
            // Weapon_Bone is the parent of HeldRuntimeWeapon — never blank the runtime gun when hiding baked guns.
            if (IsUnderRuntimeHeld(rs[i].transform))
                continue;
            rs[i].enabled = enabled;
        }
    }

    Transform FindAttachHand(bool preferWeaponBone = false)
    {
        Transform model = transform.Find(PlayerCharacterLoadout.CharacterModelChildName);
        Transform search = model != null ? model : transform;

        if (preferWeaponBone)
        {
            Transform weaponBone = FindDescendantByName(search, "Weapon_Bone");
            if (weaponBone != null)
                return weaponBone;
        }

        Animator animator = search.GetComponentInChildren<Animator>(true);
        if (animator != null && animator.isHuman)
        {
            Transform bone = animator.GetBoneTransform(HumanBodyBones.RightHand);
            if (bone != null)
                return bone;
        }

        Transform rightHand = FindDescendantByName(search, "RightHand");
        if (rightHand != null)
            return rightHand;

        Transform handR = FindDescendantByName(search, "hand.r");
        if (handR != null)
            return handR;

        return FindDescendantByName(search, "Weapon_Bone");
    }

    static bool IsFarmerRig(Transform search)
    {
        Transform heldBlaster = FindDescendantByName(search, "HeldBlaster");
        if (heldBlaster != null && !IsUnderRuntimeHeld(heldBlaster))
            return false;
        Transform gun = FindDescendantByName(search, "Gun");
        return gun != null && !IsUnderRuntimeHeld(gun);
    }

    static Transform FindBakedFarmerGun(Transform search)
    {
        Transform gun = FindDescendantByName(search, "Gun");
        if (gun == null || IsUnderRuntimeHeld(gun))
            return null;
        return gun;
    }

    static bool HasAnyRenderer(GameObject root)
    {
        if (root == null)
            return false;
        return root.GetComponentInChildren<Renderer>(true) != null;
    }

    /// <summary>
    /// Cowboy: authored Gun on animated Weapon_Bone. Kenny: create the same socket on Hips
    /// and drive it (Humanoid retarget drops farmer Weapon_Bone).
    /// </summary>
    Transform EnsureCarrySocket(Transform search)
    {
        Transform baked = FindBakedFarmerGun(search);
        if (baked != null && HasAnyRenderer(baked.gameObject))
            return baked;

        Animator animator = search.GetComponentInChildren<Animator>(true);
        Transform hips = null;
        if (animator != null && animator.isHuman && animator.isInitialized)
            hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        // Never parent to the Animator host — Kenny clips key scale 100 there.
        if (hips == null)
            return null;
        Transform parent = hips;

        Transform bone = FindDescendantByName(search, "Weapon_Bone");
        if (bone == null)
        {
            var boneGo = new GameObject("Weapon_Bone");
            boneGo.transform.SetParent(parent, false);
            bone = boneGo.transform;
        }
        else if (bone.parent != parent)
            bone.SetParent(parent, false);

        bone.localScale = Vector3.one;

        Transform socket = bone.Find("Gun");
        if (socket == null)
        {
            var gunGo = new GameObject("Gun");
            gunGo.transform.SetParent(bone, false);
            socket = gunGo.transform;
        }

        socket.localPosition = new Vector3(-0.010876f, 0.38236f, -0.11518f);
        socket.localRotation = new Quaternion(0.5211406f, 0.45324838f, 0.32258314f, 0.6472392f);
        socket.localScale = Vector3.one;

        Transform driverHost = parent;
        var driver = driverHost.GetComponent<RuntimeWeaponBoneDriver>();
        if (driver == null)
            driver = search.GetComponentInChildren<RuntimeWeaponBoneDriver>(true);
        if (driver == null)
            driver = driverHost.gameObject.AddComponent<RuntimeWeaponBoneDriver>();

        driver.Configure(
            bone,
            socket,
            animator,
            GetComponent<PlayerCharacterAnimator>(),
            transform);
        return socket;
    }

    static Transform FindDescendantByName(Transform root, string name)
    {
        if (root == null)
            return null;
        if (root.name == name)
            return root;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform found = FindDescendantByName(root.GetChild(i), name);
            if (found != null)
                return found;
        }
        return null;
    }

    static void FitHeldWeapon(GameObject gun, Transform hand, WeaponDef weapon)
    {
        if (gun == null || hand == null)
            return;

        foreach (var col in gun.GetComponentsInChildren<Collider>(true))
            Object.Destroy(col);

        // Size from local mesh vs the scale lock's intended parent size — not world AABB.
        // Kenny clips often key Animator scale 100, then FittedVisualScaleLock snaps to 0.42;
        // fitting against the keyed 100 world bounds would shrink the gun to nothing.
        float parentLossy = EstimateStableParentLossy(hand);
        float inv = 1f / parentLossy;

        Vector3 euler = weapon != null ? weapon.heldLocalEulerDegrees : new Vector3(-90f, 180f, 90f);

        gun.transform.localPosition = new Vector3(0.025f, 0.01f, 0.04f) * inv;
        gun.transform.localRotation = Quaternion.Euler(euler);
        gun.transform.localScale = Vector3.one;

        // Swap barrel/stock on the mesh only (child local Y), keep hand grip pose.
        for (int i = 0; i < gun.transform.childCount; i++)
        {
            Transform child = gun.transform.GetChild(i);
            if (child.name == "Muzzle_Bone" || child.name == "GunMuzzle")
                continue;
            child.localRotation = child.localRotation * Quaternion.Euler(0f, 180f, 0f);
        }

        ScaleToCowboyGunWorldSize(gun.transform);
        SeatGripInHand(gun.transform, hand);
    }

    static float EstimateStableParentLossy(Transform parent)
    {
        if (parent == null)
            return 1f;

        float lossy = Mathf.Max(
            Mathf.Abs(parent.lossyScale.x),
            Mathf.Abs(parent.lossyScale.y),
            Mathf.Abs(parent.lossyScale.z));

        var scaleLock = parent.GetComponentInParent<FittedVisualScaleLock>();
        if (scaleLock != null && scaleLock.HasLock)
        {
            Vector3 cur = scaleLock.transform.localScale;
            Vector3 intended = scaleLock.LockedScale;
            float current = Mathf.Max(Mathf.Abs(cur.x), Mathf.Abs(cur.y), Mathf.Abs(cur.z));
            float want = Mathf.Max(Mathf.Abs(intended.x), Mathf.Abs(intended.y), Mathf.Abs(intended.z));
            if (current > 1e-4f && want > 1e-4f)
                lossy *= want / current;
        }
        else
        {
            Transform t = parent;
            while (t != null)
            {
                float mx = Mathf.Max(Mathf.Abs(t.localScale.x), Mathf.Abs(t.localScale.y), Mathf.Abs(t.localScale.z));
                if (mx > 20f)
                {
                    lossy *= 0.42f / mx;
                    break;
                }
                t = t.parent;
            }
        }

        return Mathf.Max(lossy, 1e-4f);
    }

    static bool RuntimeHeldLooksUsable(GameObject gun)
    {
        if (gun == null || gun.transform.parent == null)
            return false;

        float meshLen = GetLongestMeshLocalSize(gun);
        if (meshLen < 1e-4f && TryGetLocalMeshBounds(gun.transform, out Bounds localBounds))
            meshLen = Mathf.Max(localBounds.size.x, Mathf.Max(localBounds.size.y, localBounds.size.z));
        if (meshLen < 1e-4f)
            return false;

        float expectedWorld = meshLen
            * Mathf.Max(Mathf.Abs(gun.transform.localScale.x), 1e-6f)
            * EstimateStableParentLossy(gun.transform.parent);
        return expectedWorld >= 0.35f && expectedWorld <= 3.5f;
    }

    /// <summary>
    /// Farmer baked shotgun longest axis at character scale 1 (Playable_Cowboy Gun).
    /// </summary>
    const float CowboyGunWorldLength = 1.05f;

    /// <summary>
    /// Stay on Weapon_Bone (idle-on-back, cowboy Gun local pose and shotgun-sized).
    /// Size from local mesh vs cowboy shotgun length — never world AABB (Kenny scale 100 would zero it).
    /// </summary>
    static void FitHeldToFarmerGunSocket(GameObject loot, Transform bakedGun, WeaponDef weapon)
    {
        if (loot == null || bakedGun == null)
            return;

        loot.transform.localPosition = bakedGun.localPosition;
        loot.transform.localRotation = bakedGun.localRotation;
        loot.transform.localScale = Vector3.one;
        ScaleToCowboyGunWorldSize(loot.transform, bakedGun);
    }

    static void ScaleToCowboyGunWorldSize(Transform loot, Transform bakedGun = null)
    {
        if (loot == null)
            return;

        float targetLen = CowboyGunWorldLength;
        if (bakedGun != null && HasAnyRenderer(bakedGun.gameObject)
            && TryGetLocalMeshBounds(bakedGun, out Bounds bakedLocal))
        {
            float bakedMesh = Mathf.Max(bakedLocal.size.x, Mathf.Max(bakedLocal.size.y, bakedLocal.size.z));
            if (bakedMesh > 1e-4f)
            {
                float bakedLossy = EstimateStableParentLossy(bakedGun.parent != null ? bakedGun.parent : bakedGun);
                targetLen = Mathf.Max(0.5f, bakedMesh * bakedLossy);
            }
        }

        if (!TryGetLocalMeshBounds(loot, out Bounds lootLocal))
        {
            float meshLen = GetLongestMeshLocalSize(loot.gameObject);
            if (meshLen < 1e-4f)
                return;
            lootLocal = new Bounds(Vector3.zero, Vector3.one * meshLen);
        }

        float lootMesh = Mathf.Max(lootLocal.size.x, Mathf.Max(lootLocal.size.y, lootLocal.size.z));
        if (lootMesh < 1e-4f)
            return;

        float parentLossy = EstimateStableParentLossy(loot.parent);
        float s = targetLen / (lootMesh * parentLossy);
        loot.localScale = Vector3.one * Mathf.Clamp(s, 0.01f, 80f);
    }

    /// <summary>
    /// Slide the mesh so the stock/grip sits in the palm. Kenny hold was pivoting on the muzzle tip.
    /// </summary>
    static void SeatGripInHand(Transform gun, Transform hand)
    {
        if (gun == null || hand == null)
            return;
        if (!TryGetLocalMeshBounds(gun, out Bounds localBounds))
            return;

        Vector3 centerLocal = localBounds.center;
        Vector3 axis = Vector3.forward;
        float ext = localBounds.extents.z;
        if (localBounds.extents.x >= localBounds.extents.y && localBounds.extents.x >= localBounds.extents.z)
        {
            axis = Vector3.right;
            ext = localBounds.extents.x;
        }
        else if (localBounds.extents.y >= localBounds.extents.x && localBounds.extents.y >= localBounds.extents.z)
        {
            axis = Vector3.up;
            ext = localBounds.extents.y;
        }

        Vector3 tipA = centerLocal + axis * ext;
        Vector3 tipB = centerLocal - axis * ext;

        Vector3 aimWorld = ResolveHeldAim(gun);
        Vector3 centerWorld = gun.TransformPoint(centerLocal);
        Vector3 worldA = gun.TransformPoint(tipA);
        Vector3 worldB = gun.TransformPoint(tipB);
        float scoreA = Vector3.Dot((worldA - centerWorld).normalized, aimWorld);
        float scoreB = Vector3.Dot((worldB - centerWorld).normalized, aimWorld);
        // Aim-facing AABB end is the muzzle; opposite end is the stock. Palm on the grip, barrel ahead.
        Vector3 muzzleLocal = scoreA >= scoreB ? tipA : tipB;
        Vector3 stockLocal = scoreA >= scoreB ? tipB : tipA;
        Vector3 gripLocal = Vector3.Lerp(stockLocal, muzzleLocal, 0.18f);
        Vector3 palm = hand.position;
        gun.position += palm - gun.TransformPoint(gripLocal);
        // Extra slide so Kenny palms sit on the grip, not the muzzle opening.
        gun.position += aimWorld * 0.22f;
    }

    static Vector3 ResolveHeldAim(Transform gunRoot)
    {
        Transform character = gunRoot;
        while (character.parent != null && character.name != PlayerCharacterLoadout.CharacterModelChildName
               && character.GetComponent<PlayerHealth>() == null)
            character = character.parent;

        Vector3 aimWorld = character.forward;
        var look = character.GetComponentInParent<PlayerLookController>();
        if (look != null && look.cameraTarget != null)
            aimWorld = look.cameraTarget.forward;
        else
        {
            var motor = character.GetComponentInParent<PlanetMotor_InputSystem>();
            if (motor != null && motor.cameraTransform != null)
                aimWorld = motor.cameraTransform.forward;
        }
        if (aimWorld.sqrMagnitude < 1e-6f)
            aimWorld = gunRoot.forward;
        return aimWorld.normalized;
    }

    /// <summary>
    /// Place muzzle on the mesh tip that faces aim, then force fire direction toward aim
    /// (fixes stock-end spawns without rotating the grip-down hold pose).
    /// </summary>
    static void EnsureMuzzleBone(Transform gunRoot)
    {
        if (gunRoot == null)
            return;

        Transform existing = gunRoot.Find("Muzzle_Bone");
        if (existing != null)
            Object.DestroyImmediate(existing.gameObject);

        if (!TryGetLocalMeshBounds(gunRoot, out Bounds localBounds))
            localBounds = new Bounds(Vector3.zero, new Vector3(0.05f, 0.05f, 0.28f));

        Vector3 centerLocal = localBounds.center;
        // Along the longest local axis only (barrel), both ends.
        Vector3 axis = Vector3.forward;
        float ext = localBounds.extents.z;
        if (localBounds.extents.x >= localBounds.extents.y && localBounds.extents.x >= localBounds.extents.z)
        {
            axis = Vector3.right;
            ext = localBounds.extents.x;
        }
        else if (localBounds.extents.y >= localBounds.extents.x && localBounds.extents.y >= localBounds.extents.z)
        {
            axis = Vector3.up;
            ext = localBounds.extents.y;
        }

        Vector3 localTipA = centerLocal + axis * ext;
        Vector3 localTipB = centerLocal - axis * ext;

        Vector3 aimWorld = ResolveHeldAim(gunRoot);
        Vector3 upWorld = gunRoot.root != null ? gunRoot.root.up : Vector3.up;
        var health = gunRoot.GetComponentInParent<PlayerHealth>();
        if (health != null)
            upWorld = health.transform.up;

        Vector3 centerWorld = gunRoot.TransformPoint(centerLocal);
        Vector3 worldA = gunRoot.TransformPoint(localTipA);
        Vector3 worldB = gunRoot.TransformPoint(localTipB);
        float scoreA = Vector3.Dot((worldA - centerWorld).normalized, aimWorld);
        float scoreB = Vector3.Dot((worldB - centerWorld).normalized, aimWorld);
        // After child Y-180, barrel faces aim — pick the tip most aligned with aim.
        Vector3 bestLocalTip = scoreA >= scoreB ? localTipA : localTipB;

        var muzzle = new GameObject("Muzzle_Bone");
        muzzle.transform.SetParent(gunRoot, false);
        muzzle.transform.localPosition = bestLocalTip;
        muzzle.transform.rotation = Quaternion.LookRotation(aimWorld, upWorld);

        var alias = new GameObject("GunMuzzle");
        alias.transform.SetParent(muzzle.transform, false);
        alias.transform.localPosition = Vector3.zero;
        alias.transform.localRotation = Quaternion.identity;
    }

    static bool TryGetLocalMeshBounds(Transform gunRoot, out Bounds localBounds)
    {
        localBounds = default;
        bool any = false;
        MeshFilter[] filters = gunRoot.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < filters.Length; i++)
        {
            MeshFilter mf = filters[i];
            if (mf == null || mf.sharedMesh == null)
                continue;
            Bounds mb = mf.sharedMesh.bounds;
            Transform t = mf.transform;
            Vector3[] corners =
            {
                t.TransformPoint(mb.min),
                t.TransformPoint(new Vector3(mb.min.x, mb.min.y, mb.max.z)),
                t.TransformPoint(new Vector3(mb.min.x, mb.max.y, mb.min.z)),
                t.TransformPoint(new Vector3(mb.min.x, mb.max.y, mb.max.z)),
                t.TransformPoint(new Vector3(mb.max.x, mb.min.y, mb.min.z)),
                t.TransformPoint(new Vector3(mb.max.x, mb.min.y, mb.max.z)),
                t.TransformPoint(new Vector3(mb.max.x, mb.max.y, mb.min.z)),
                t.TransformPoint(mb.max)
            };
            for (int c = 0; c < corners.Length; c++)
            {
                Vector3 local = gunRoot.InverseTransformPoint(corners[c]);
                if (!any)
                {
                    localBounds = new Bounds(local, Vector3.zero);
                    any = true;
                }
                else
                    localBounds.Encapsulate(local);
            }
        }

        return any;
    }

    static Bounds GetWorldRendererBounds(GameObject root)
    {
        bool any = false;
        Bounds b = new Bounds(root.transform.position, Vector3.zero);
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null)
                continue;
            if (!any)
            {
                b = r.bounds;
                any = true;
            }
            else
                b.Encapsulate(r.bounds);
        }
        return any ? b : new Bounds(root.transform.position, Vector3.one * 0.01f);
    }
}
