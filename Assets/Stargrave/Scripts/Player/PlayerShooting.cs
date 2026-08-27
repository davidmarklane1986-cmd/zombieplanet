using System.Collections;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Left click / gamepad RT: raycast hit-scan when <see cref="projectilePrefab"/> is unset; otherwise spawns a visible
/// projectile (prefab should include MeshRenderer + collider + <see cref="Projectile"/>). Damages <see cref="ZombieAI"/>.
/// </summary>
[DefaultExecutionOrder(55)]
public class PlayerShooting : MonoBehaviour
{
    public static event System.Action ShotFired;
    public static event System.Action HitConfirmed;

    [Header("Camera (for raycast / aim)")]
    [Tooltip("Camera to raycast from when using hit-scan. If unset, uses Camera.main.")]
    public Camera playerCamera;

    [Header("Raycast (used when no projectile prefab assigned)")]
    public float shootRange = 100f;
    public int damagePerShot = 1;

    [Header("Projectile (optional)")]
    [Tooltip("If set, spawns this prefab instead of using raycast. Use a mesh + MeshRenderer on the prefab so shots are visible.")]
    public GameObject projectilePrefab;
    [Tooltip("Projectile spawn position. When possible this is resolved from the animated character rig (Muzzle_Bone / Weapon_Bone) instead of the camera.")]
    public Transform firePoint;
    public float projectileSpeed = 160f;
    [Tooltip("Time between shots while the trigger is held (within a magazine burst).")]
    public float fireCooldown = 0.18f;
    [Header("Auto Fire / Magazine")]
    [Tooltip("Hold LMB / RT to keep firing. Release to stop immediately.")]
    public bool holdToFire = true;
    [Tooltip("Shots fired in a burst before the reload pause.")]
    [Min(1)] public int shotsPerMagazine = 6;
    [Tooltip("Cooldown after a magazine empties before the next burst can start (while still holding).")]
    [Min(0.05f)] public float reloadCooldown = 1.5f;
    [Tooltip("Spawn slightly in front of the fire point / camera so the orb does not clip inside geometry.")]
    public float spawnForwardOffset = 0.35f;
    [Tooltip("When enabled, the projectile is aimed at the exact crosshair contact point from the muzzle.")]
    public bool aimProjectilesWithCamera = true;
    [Tooltip("Prefer the player model's muzzle / weapon bone over the camera-target muzzle when available.")]
    public bool preferCharacterModelMuzzle = true;

    [Header("Aim Assist")]
    [Tooltip("Helps catch zombies near the crosshair when the camera sway makes you narrowly miss.")]
    public bool aimAssistEnabled = true;
    [Tooltip("Maximum angle from the crosshair ray for a zombie to qualify for aim assist.")]
    [Range(0.5f, 15f)] public float aimAssistMaxAngle = 6f;
    [Tooltip("Maximum distance from the camera for aim assist candidates. 0 uses shoot range.")]
    public float aimAssistRange = 0f;
    [Tooltip("When on, lightly pulls gamepad look toward the aim-assist zombie with a small right-stick deflection. Off by default.")]
    public bool enableSoftLookAssist = false;

    [Header("Lock-On")]
    [Tooltip("DMC-style: tap LB or key to lock, tap again to release. Off = hold LB.")]
    public bool lockOnToggleMode = true;
    public bool lockOnKeyboardToggleEnabled = true;
    public Key lockOnKeyboardToggleKey = Key.Q;
    [Tooltip("When on, lock-on is available.")]
    public bool lockOnEnabled = true;
    [Range(0.05f, 0.75f)] public float lockOnMaxScreenDistance = 0.42f;
    [Tooltip("Maximum lock-on distance from the camera. 0 uses shoot range.")]
    public float lockOnRange = 0f;

    [Header("Boat Accuracy")]
    [Tooltip("Weapon spread is multiplied by this while seated in a boat.")]
    [Min(1f)] public float boatSpreadMultiplier = 3f;
    [Tooltip("Extra cone half-angle (degrees) added while seated in a boat.")]
    [Min(0f)] public float boatExtraSpreadDegrees = 8f;

    float _nextFireTime;
    float _reloadReadyTime;
    int _shotsRemaining;
    PlayerBuffController _buffs;
    PlayerWeaponController _weapons;
    PlanetMotor_InputSystem _motor;
    Transform _playerRoot;
    CombatTargeting _combatTargeting;
    bool _lootAmmoMode;
    Color _projectileColor = new Color(1f, 0.55f, 0.08f, 1f);
    int _pelletCount = 1;
    float _spreadDegrees;
    int _burstCount = 1;
    float _burstShotInterval = 0.06f;
    float _damageFalloffStart = 18f;
    float _damageFalloffEnd = 50f;
    float _damageFalloffMinMultiplier = 0.4f;
    bool _burstInProgress;
    Coroutine _burstCoroutine;
    const float TriggerHeldThreshold = 0.35f;

    public float CurrentAimRange => shootRange;
    public bool HasLockOnTarget => _combatTargeting != null && _combatTargeting.HasHardLock;
    public ZombieAI CurrentLockOnTarget => _combatTargeting != null ? _combatTargeting.CurrentLockOnTarget : null;

    void Start()
    {
        ResolveCombatTargeting();
        SyncCombatTargetingParams();
    }

    void ResolveCombatTargeting()
    {
        if (_combatTargeting != null)
            return;
        _combatTargeting = FindFirstObjectByType<CombatTargeting>(FindObjectsInactive.Include);
    }

    void SyncCombatTargetingParams()
    {
        ResolveCombatTargeting();
        if (_combatTargeting == null)
            return;

        _combatTargeting.AimRangeFallback = shootRange;
        _combatTargeting.lockOnToggleMode = lockOnToggleMode;
        _combatTargeting.lockOnKeyboardToggleEnabled = lockOnKeyboardToggleEnabled;
        _combatTargeting.lockOnKeyboardToggleKey = lockOnKeyboardToggleKey;
        _combatTargeting.lockOnEnabled = lockOnEnabled;
        _combatTargeting.lockOnMaxScreenDistance = lockOnMaxScreenDistance;
        _combatTargeting.lockOnRange = lockOnRange;
        _combatTargeting.aimAssistEnabled = aimAssistEnabled;
        _combatTargeting.aimAssistMaxAngle = aimAssistMaxAngle;
        _combatTargeting.aimAssistRange = aimAssistRange;
        _combatTargeting.enableSoftLookAssist = enableSoftLookAssist;

        Camera cam = GetShootCamera();
        if (cam != null)
            _combatTargeting.combatCamera = cam;
    }

    void Awake()
    {
        if (playerCamera == null)
            playerCamera = Camera.main;
        projectileSpeed = Mathf.Max(projectileSpeed, 160f);
        _shotsRemaining = Mathf.Max(1, shotsPerMagazine);

        _playerRoot = ResolvePlayerRoot();
        RebindFirePointToCharacterMuzzle(force: true);

        if (firePoint == null && _playerRoot != null)
        {
            Transform gun = _playerRoot.Find("CameraTarget/GunMuzzle");
            if (gun == null)
                gun = _playerRoot.Find("GunMuzzle");
            if (gun != null)
                firePoint = gun;
            else
            {
                Transform ct = _playerRoot.Find("CameraTarget");
                if (ct != null)
                    firePoint = ct;
            }
        }
    }

    /// <summary>Shots left in the current magazine (HUD).</summary>
    public int ShotsRemainingInMagazine => Mathf.Max(0, _shotsRemaining);
    /// <summary>Configured magazine size (HUD).</summary>
    public int MagazineCapacity => Mathf.Max(1, shotsPerMagazine);

    /// <summary>Called after a character loadout changes mag size / model — refill and re-find muzzle.</summary>
    public void OnCharacterLoadoutApplied()
    {
        ResolveWeapons();
        if (_lootAmmoMode && _weapons != null)
            _shotsRemaining = Mathf.Max(1, _weapons.GetLootMagazineFill());
        else
            _shotsRemaining = Mathf.Max(1, shotsPerMagazine);
        _playerRoot = ResolvePlayerRoot();
        RebindFirePointToCharacterMuzzle(force: true);
    }

    /// <summary>Finite loot ammo mode: magazine refill is capped by remaining loot; empty loot unequips.</summary>
    public void SetLootAmmoMode(bool enabled)
    {
        _lootAmmoMode = enabled;
        ResolveWeapons();
    }

    /// <summary>Copy fire profile (colour, spread, burst, falloff) from the equipped weapon.</summary>
    public void ApplyWeaponFireProfile(WeaponDef weapon)
    {
        if (weapon == null)
            return;

        _projectileColor = weapon.projectileColor;
        _pelletCount = Mathf.Max(1, weapon.pelletCount);
        _spreadDegrees = Mathf.Max(0f, weapon.spreadDegrees);
        _burstCount = Mathf.Max(1, weapon.burstCount);
        _burstShotInterval = Mathf.Max(0f, weapon.burstShotInterval);
        _damageFalloffStart = Mathf.Max(0f, weapon.damageFalloffStart);
        _damageFalloffEnd = Mathf.Max(_damageFalloffStart + 0.01f, weapon.damageFalloffEnd);
        _damageFalloffMinMultiplier = Mathf.Clamp(weapon.damageFalloffMinMultiplier, 0.05f, 1f);

        if (_burstCoroutine != null)
        {
            StopCoroutine(_burstCoroutine);
            _burstCoroutine = null;
        }
        _burstInProgress = false;
    }

    void ResolveWeapons()
    {
        if (_weapons != null)
            return;
        if (_playerRoot == null)
            _playerRoot = ResolvePlayerRoot();
        if (_playerRoot != null)
            _weapons = _playerRoot.GetComponent<PlayerWeaponController>();
        if (_weapons == null)
            _weapons = GetComponentInParent<PlayerWeaponController>();
        if (_weapons == null)
            _weapons = Object.FindFirstObjectByType<PlayerWeaponController>(FindObjectsInactive.Include);
    }

    /// <summary>
    /// Always prefer a live CharacterModel muzzle. After a visual swap the old firePoint is often a
    /// destroyed transform (Unity null), which made shots spawn at the camera.
    /// </summary>
    void RebindFirePointToCharacterMuzzle(bool force)
    {
        if (!preferCharacterModelMuzzle && !force)
            return;

        Transform characterMuzzle = FindCharacterModelMuzzle(_playerRoot);
        if (characterMuzzle != null)
        {
            firePoint = characterMuzzle;
            return;
        }

        // Never keep a destroyed or camera-anchored point when we expected a model muzzle.
        if (firePoint == null || IsCameraAnchoredFirePoint(_playerRoot, firePoint))
        {
            // Last resort: still avoid pure camera origin if CameraTarget/GunMuzzle exists.
            if (_playerRoot != null)
            {
                Transform gun = _playerRoot.Find("CameraTarget/GunMuzzle");
                if (gun != null)
                    firePoint = gun;
            }
        }
    }

    void LateUpdate()
    {
        // If the bound muzzle was destroyed by a model swap mid-frame, recover before next shot.
        if (preferCharacterModelMuzzle && (firePoint == null || IsCameraAnchoredFirePoint(_playerRoot, firePoint)))
            RebindFirePointToCharacterMuzzle(force: true);
    }

    Transform ResolvePlayerRoot()
    {
        if (CompareTag("Player"))
            return transform;

        GameObject player = GameObject.FindGameObjectWithTag("Player");
        if (player != null)
            return player.transform;

        return transform;
    }

    static bool IsCameraAnchoredFirePoint(Transform playerRoot, Transform point)
    {
        if (playerRoot == null || point == null)
            return false;

        Transform cameraTarget = playerRoot.Find("CameraTarget");
        if (cameraTarget == null)
            return false;

        return point == cameraTarget || point.IsChildOf(cameraTarget);
    }

    static Transform FindCharacterModelMuzzle(Transform playerRoot)
    {
        if (playerRoot == null)
            return null;

        Transform modelRoot = playerRoot.Find("CharacterModel");
        Transform searchRoot = modelRoot != null ? modelRoot : playerRoot;

        // 0) Live runtime held weapon wins over hidden baked Gun / Weapon_Bone muzzles.
        Transform runtimeHeld = FindDescendantByName(searchRoot, PlayerWeaponController.RuntimeHeldName);
        if (runtimeHeld != null && runtimeHeld.gameObject.activeInHierarchy)
        {
            Transform runtimeMuzzle = FindDescendantByName(runtimeHeld, "Muzzle_Bone");
            if (runtimeMuzzle != null)
                return runtimeMuzzle;
            Transform runtimeGunMuzzle = FindDescendantByName(runtimeHeld, "GunMuzzle");
            if (runtimeGunMuzzle != null)
                return runtimeGunMuzzle;
        }

        // 1) Active HeldBlaster (Kenny default visual).
        Transform heldBlaster = FindDescendantByName(searchRoot, "HeldBlaster");
        if (heldBlaster != null && heldBlaster.gameObject.activeInHierarchy)
        {
            Transform heldMuzzle = FindDescendantByName(heldBlaster, "Muzzle_Bone");
            if (heldMuzzle != null)
                return heldMuzzle;
            Transform heldGunMuzzle = FindDescendantByName(heldBlaster, "GunMuzzle");
            if (heldGunMuzzle != null)
                return heldGunMuzzle;
        }

        // 2) Active baked Gun only when no runtime held is showing.
        Transform bakedGun = FindDescendantByName(searchRoot, "Gun");
        if (bakedGun != null && bakedGun.gameObject.activeInHierarchy
            && (runtimeHeld == null || !runtimeHeld.gameObject.activeInHierarchy))
        {
            Transform gunMuzzle = FindDescendantByName(bakedGun, "Muzzle_Bone");
            if (gunMuzzle != null)
                return gunMuzzle;
            gunMuzzle = FindDescendantByName(bakedGun, "GunMuzzle");
            if (gunMuzzle != null)
                return gunMuzzle;
        }

        // 3) Weapon_Bone muzzles only if active (skip hidden baked sockets).
        Transform weaponBone = FindDescendantByName(searchRoot, "Weapon_Bone");
        if (weaponBone != null)
        {
            Transform weaponMuzzle = FindActiveDescendantByName(weaponBone, "Muzzle_Bone");
            if (weaponMuzzle != null)
                return weaponMuzzle;
            Transform weaponGunMuzzle = FindActiveDescendantByName(weaponBone, "GunMuzzle");
            if (weaponGunMuzzle != null)
                return weaponGunMuzzle;
        }

        // 4) Any authored active Muzzle_Bone under the character.
        Transform muzzle = FindPreferredMuzzleBone(searchRoot);
        if (muzzle != null)
            return muzzle;

        // 5) Humanoid right hand.
        Animator anim = searchRoot.GetComponentInChildren<Animator>(true);
        if (anim != null && anim.isHuman)
        {
            Transform rightHand = anim.GetBoneTransform(HumanBodyBones.RightHand);
            if (rightHand != null)
                return EnsureRuntimeMuzzle(rightHand, "GunMuzzle_Runtime", new Vector3(0.1f, 0f, 0.22f));
        }

        // 6) Kenny Generic RightHand.
        Transform genericHand = FindFirstDescendantMatching(searchRoot,
            "RightHand", "hand.R", "Hand_R", "mixamorig:RightHand", "Right_Hand");
        if (genericHand != null)
        {
            muzzle = FindActiveDescendantByName(genericHand, "Muzzle_Bone");
            if (muzzle != null)
                return muzzle;
            muzzle = FindActiveDescendantByName(genericHand, "GunMuzzle");
            if (muzzle != null)
                return muzzle;
            return EnsureRuntimeMuzzle(genericHand, "GunMuzzle_Runtime", new Vector3(0.08f, 0.02f, 0.18f));
        }

        muzzle = FindActiveDescendantByName(searchRoot, "GunMuzzle");
        if (muzzle != null && !IsUnderName(muzzle, "CameraTarget"))
            return muzzle;

        return null;
    }

    static Transform FindActiveDescendantByName(Transform root, string name)
    {
        Transform t = FindDescendantByName(root, name);
        if (t == null || !t.gameObject.activeInHierarchy)
            return null;
        return t;
    }

    /// <summary>Prefer a Muzzle_Bone parented under a gun/weapon, not a random duplicate.</summary>
    static Transform FindPreferredMuzzleBone(Transform searchRoot)
    {
        Transform[] all = searchRoot.GetComponentsInChildren<Transform>(true);
        Transform fallback = null;
        for (int i = 0; i < all.Length; i++)
        {
            Transform t = all[i];
            if (t == null || t.name != "Muzzle_Bone")
                continue;
            if (!t.gameObject.activeInHierarchy)
                continue;
            if (IsUnderName(t, "CameraTarget"))
                continue;
            if (IsUnderName(t, PlayerWeaponController.RuntimeHeldName))
                return t;
            if (IsUnderName(t, "HeldBlaster") || IsUnderName(t, "Gun") || IsUnderName(t, "Weapon_Bone"))
                return t;
            if (fallback == null)
                fallback = t;
        }
        return fallback;
    }

    static bool IsUnderName(Transform t, string ancestorName)
    {
        for (Transform p = t; p != null; p = p.parent)
        {
            if (p.name == ancestorName)
                return true;
        }
        return false;
    }

    static Transform FindFirstDescendantMatching(Transform root, params string[] exactNames)
    {
        if (root == null || exactNames == null || exactNames.Length == 0)
            return null;

        Transform[] children = root.GetComponentsInChildren<Transform>(true);
        for (int n = 0; n < exactNames.Length; n++)
        {
            string want = exactNames[n];
            if (string.IsNullOrEmpty(want))
                continue;
            for (int i = 0; i < children.Length; i++)
            {
                Transform child = children[i];
                if (child != null && child.name == want)
                    return child;
            }
        }
        return null;
    }

    static Transform FindDescendantByName(Transform root, string exactName)
    {
        if (root == null || string.IsNullOrEmpty(exactName))
            return null;

        Transform[] children = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < children.Length; i++)
        {
            Transform child = children[i];
            if (child != null && child.name == exactName)
                return child;
        }

        return null;
    }

    static Transform EnsureRuntimeMuzzle(Transform parent, string childName, Vector3 localPosition)
    {
        if (parent == null)
            return null;

        Transform existing = parent.Find(childName);
        if (existing != null)
            return existing;

        var go = new GameObject(childName);
        Transform t = go.transform;
        t.SetParent(parent, false);
        t.localPosition = localPosition;
        t.localRotation = Quaternion.identity;
        t.localScale = Vector3.one;
        return t;
    }

    bool WantsFirePressedThisFrame()
    {
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame) return true;
        if (Gamepad.current != null && Gamepad.current.rightTrigger.wasPressedThisFrame) return true;
        return false;
#else
        if (Input.GetMouseButtonDown(0)) return true;
        if (Input.GetButtonDown("Fire1")) return true;
        return false;
#endif
    }

    bool WantsFireHeld()
    {
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null && Mouse.current.leftButton.isPressed) return true;
        if (Gamepad.current != null && Gamepad.current.rightTrigger.ReadValue() >= TriggerHeldThreshold) return true;
        return false;
#else
        if (Input.GetMouseButton(0)) return true;
        if (Input.GetButton("Fire1")) return true;
        return false;
#endif
    }

    bool HasRapidFirePowerUp()
    {
        return _buffs != null && _buffs.HasActiveBuff("PowerUp_RapidFire");
    }

    Camera GetShootCamera()
    {
        if (playerCamera != null) return playerCamera;
        return Camera.main;
    }

    public Camera GetCurrentShootCamera()
    {
        return GetShootCamera();
    }

    public static void NotifyHitConfirmed()
    {
        HitConfirmed?.Invoke();
    }

    public bool TryGetLockOnAimPoint(out Vector3 aimPoint)
    {
        ZombieAI zombie;
        return TryGetLockOnTarget(GetShootCamera(), out zombie, out aimPoint);
    }

    public bool TryGetLockOnCameraAimPoint(out Vector3 aimPoint)
    {
        aimPoint = Vector3.zero;
        Camera cam = GetShootCamera();
        ZombieAI zombie;
        Vector3 bulletAimPoint;
        if (!TryGetLockOnTarget(cam, out zombie, out bulletAimPoint) || zombie == null)
            return false;

        return CombatTargeting.TryGetZombieCameraAimPoint(zombie, out aimPoint);
    }

    public bool TryGetCrosshairHit(out RaycastHit hit)
    {
        Camera cam = GetShootCamera();
        if (cam == null)
        {
            hit = new RaycastHit();
            return false;
        }

        Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        RaycastHit[] hits = Physics.RaycastAll(ray, shootRange, ~0, QueryTriggerInteraction.Collide);
        if (hits == null || hits.Length == 0)
        {
            hit = new RaycastHit();
            return false;
        }

        System.Array.Sort(hits, CompareRaycastHitsByDistance);

        // Prefer the first zombie under the crosshair. World blockers (terrain/props) only
        // stop the shot if they are closer than that zombie.
        RaycastHit? firstWorld = null;
        for (int i = 0; i < hits.Length; i++)
        {
            Collider col = hits[i].collider;
            if (IsIgnoredCrosshairCollider(col))
                continue;

            if (col.GetComponentInParent<ZombieAI>() != null)
            {
                if (firstWorld.HasValue && firstWorld.Value.distance + 0.05f < hits[i].distance)
                {
                    hit = firstWorld.Value;
                    return true;
                }

                hit = hits[i];
                return true;
            }

            if (!firstWorld.HasValue && !col.isTrigger)
                firstWorld = hits[i];
        }

        if (firstWorld.HasValue)
        {
            hit = firstWorld.Value;
            return true;
        }

        hit = new RaycastHit();
        return false;
    }

    /// <summary>
    /// Damages the zombie under the screen-center crosshair (camera ray). Returns true if a zombie was hit.
    /// </summary>
    bool TryDamageZombieFromCrosshair()
    {
        Camera cam = GetShootCamera();
        ZombieAI lockOnZombie;
        Vector3 lockOnAimPoint;
        if (TryGetLockOnTarget(cam, out lockOnZombie, out lockOnAimPoint) && lockOnZombie != null)
        {
            lockOnZombie.TakeDamage(GetBuffedDamage(damagePerShot));
            NotifyHitConfirmed();
            return true;
        }

        if (TryGetCrosshairHit(out RaycastHit hit))
        {
            var zombie = hit.collider.GetComponentInParent<ZombieAI>();
            if (zombie != null)
            {
                zombie.TakeDamage(GetBuffedDamage(damagePerShot));
                NotifyHitConfirmed();
                return true;
            }
        }

        ZombieAI assistedZombie;
        Vector3 assistedAimPoint;
        if (TryGetAimAssistTarget(cam, out assistedZombie, out assistedAimPoint) && assistedZombie != null)
        {
            assistedZombie.TakeDamage(GetBuffedDamage(damagePerShot));
            NotifyHitConfirmed();
            return true;
        }

        return false;
    }

    Vector3 GetCrosshairAimPoint(Camera cam, out bool foundSurface)
    {
        if (cam == null)
        {
            foundSurface = false;
            return transform.position + transform.forward * shootRange;
        }

        ZombieAI lockOnZombie;
        Vector3 lockOnAimPoint;
        if (TryGetLockOnTarget(cam, out lockOnZombie, out lockOnAimPoint))
        {
            foundSurface = true;
            return lockOnAimPoint;
        }

        if (TryGetCrosshairHit(out RaycastHit hit))
        {
            foundSurface = true;
            return hit.point;
        }

        foundSurface = false;
        Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        return ray.origin + ray.direction * shootRange;
    }

    static int CompareRaycastHitsByDistance(RaycastHit a, RaycastHit b)
    {
        return a.distance.CompareTo(b.distance);
    }

    bool IsIgnoredCrosshairCollider(Collider other)
    {
        if (other == null)
            return true;
        if (other.gameObject.layer == 2)
            return true;
        if (other.isTrigger && other.GetComponentInParent<ZombieAI>() == null)
            return true;
        if (_playerRoot != null && other.transform.IsChildOf(_playerRoot))
            return true;
        if (other.GetComponentInParent<Projectile>() != null)
            return true;
        return false;
    }

    bool TryGetLockOnTarget(Camera cam, out ZombieAI zombie, out Vector3 aimPoint)
    {
        ResolveCombatTargeting();
        if (_combatTargeting != null)
            return _combatTargeting.TryGetLockOnTarget(cam, out zombie, out aimPoint);
        zombie = null;
        aimPoint = Vector3.zero;
        return false;
    }

    bool TryGetAimAssistTarget(Camera cam, out ZombieAI zombie, out Vector3 aimPoint)
    {
        ResolveCombatTargeting();
        if (_combatTargeting != null)
            return _combatTargeting.TryGetAimAssistTarget(cam, out zombie, out aimPoint);
        zombie = null;
        aimPoint = Vector3.zero;
        return false;
    }

    Vector3 GetProjectileDirectionToCrosshair(Camera cam, ref Vector3 origin)
    {
        if (cam == null)
            return firePoint != null ? firePoint.forward : transform.forward;

        bool foundSurface;
        Vector3 aimPoint = GetCrosshairAimPoint(cam, out foundSurface);
        if (!foundSurface)
        {
            ZombieAI assistedZombie;
            Vector3 assistedAimPoint;
            if (TryGetAimAssistTarget(cam, out assistedZombie, out assistedAimPoint))
            {
                foundSurface = true;
                aimPoint = assistedAimPoint;
            }
        }

        Vector3 toAim = aimPoint - origin;
        if (toAim.sqrMagnitude < 0.0001f)
            return cam.transform.forward;

        Vector3 dir = toAim.normalized;
        float distanceToAim = toAim.magnitude;
        float safeOffset = Mathf.Min(spawnForwardOffset, Mathf.Max(0f, distanceToAim - 0.02f));
        if (safeOffset > 0f)
        {
            origin += dir * safeOffset;
            Vector3 refined = aimPoint - origin;
            if (refined.sqrMagnitude > 0.0001f)
                return refined.normalized;
        }

        if (!foundSurface)
            return cam.transform.forward;
        return dir;
    }

    void Update()
    {
        SyncCombatTargetingParams();
        ResolveBuffController();

        bool rapidFireActive = HasRapidFirePowerUp();
        bool triggerHeld = WantsFireHeld();
        if (rapidFireActive && triggerHeld && _buffs != null)
            _buffs.ConsumeBuffTime("PowerUp_RapidFire", Time.deltaTime);

        if (_burstInProgress)
            return;

        float rof = _buffs != null ? Mathf.Max(0.05f, _buffs.CombinedFireRateMultiplier) : 1f;
        float reloadMult = _buffs != null ? Mathf.Max(0.05f, _buffs.CombinedReloadMultiplier) : 1f;
        float shotInterval = fireCooldown / rof;
        float reloadInterval = Mathf.Max(0.05f, reloadCooldown / reloadMult);

        bool wantsFire = (holdToFire || rapidFireActive) ? triggerHeld : WantsFirePressedThisFrame();
        if (!wantsFire)
            return;

        if (!CanFireWeapons())
            return;

        if (Time.time < _nextFireTime)
            return;

        ResolveWeapons();

        if (_lootAmmoMode && (_weapons == null || _weapons.LootAmmoRemaining <= 0))
        {
            if (_weapons != null)
                _weapons.ResetToAssigned();
            return;
        }

        // Rifle-style burst: several rounds, then the main fire cooldown.
        if (!rapidFireActive && _burstCount > 1)
        {
            _burstCoroutine = StartCoroutine(CoFireBurst(shotInterval, reloadInterval));
            return;
        }

        if (!TryPrepareAndConsumeRound(rapidFireActive, shotInterval, reloadInterval, setNextFireTime: true))
            return;

        FireOneRound();
        if (_lootAmmoMode && _weapons != null)
            _weapons.TryConsumeLootShot();
    }

    IEnumerator CoFireBurst(float shotInterval, float reloadInterval)
    {
        _burstInProgress = true;
        int planned = Mathf.Max(1, _burstCount);
        for (int i = 0; i < planned; i++)
        {
            if (_lootAmmoMode && (_weapons == null || _weapons.LootAmmoRemaining <= 0))
                break;

            if (!CanFireWeapons())
                break;

            if (!TryPrepareAndConsumeRound(rapidFireActive: false, shotInterval, reloadInterval, setNextFireTime: false))
                break;

            FireOneRound();
            if (_lootAmmoMode && _weapons != null)
                _weapons.TryConsumeLootShot();

            if (i + 1 < planned && _burstShotInterval > 0f)
                yield return new WaitForSeconds(_burstShotInterval);
        }

        _nextFireTime = Time.time + Mathf.Max(0.01f, shotInterval);
        _burstInProgress = false;
        _burstCoroutine = null;
    }

    bool TryPrepareAndConsumeRound(bool rapidFireActive, float shotInterval, float reloadInterval, bool setNextFireTime)
    {
        if (rapidFireActive)
        {
            if (setNextFireTime)
                _nextFireTime = Time.time + shotInterval;
            if (!_lootAmmoMode)
                _shotsRemaining = Mathf.Max(1, shotsPerMagazine);
            _reloadReadyTime = 0f;
            return true;
        }

        if (_shotsRemaining <= 0)
        {
            if (Time.time < _reloadReadyTime)
                return false;
            if (_lootAmmoMode)
            {
                int fill = _weapons != null ? _weapons.GetLootMagazineFill() : 0;
                if (fill <= 0)
                {
                    if (_weapons != null)
                        _weapons.ResetToAssigned();
                    return false;
                }
                _shotsRemaining = fill;
            }
            else
            {
                _shotsRemaining = Mathf.Max(1, shotsPerMagazine);
            }
        }

        _shotsRemaining--;
        if (_shotsRemaining <= 0)
        {
            _reloadReadyTime = Time.time + reloadInterval;
            if (setNextFireTime)
                _nextFireTime = _reloadReadyTime;
        }
        else if (setNextFireTime)
        {
            _nextFireTime = Time.time + shotInterval;
        }

        return true;
    }

    void FireOneRound()
    {
        ShotFired?.Invoke();
        AudioManager.PlayShoot();
        FirePelletVolley();
    }

    void ResolveBuffController()
    {
        if (_buffs != null)
            return;
        if (firePoint != null)
            _buffs = firePoint.GetComponentInParent<PlayerBuffController>();
        if (_buffs == null)
        {
            var p = GameObject.FindGameObjectWithTag("Player");
            if (p != null)
                _buffs = p.GetComponent<PlayerBuffController>();
        }
    }

    int GetBuffedDamage(int baseDamage)
    {
        if (_buffs == null)
            return Mathf.Max(1, baseDamage);
        return Mathf.Max(1, Mathf.RoundToInt(baseDamage * _buffs.CombinedDamageMultiplier));
    }

    int GetDamageAtDistance(float distance)
    {
        int buffed = GetBuffedDamage(damagePerShot);
        if (distance <= _damageFalloffStart)
            return buffed;
        float t = Mathf.InverseLerp(_damageFalloffStart, _damageFalloffEnd, distance);
        float mult = Mathf.Lerp(1f, _damageFalloffMinMultiplier, t);
        return Mathf.Max(1, Mathf.RoundToInt(buffed * mult));
    }

    void ResolveMotor()
    {
        if (_motor != null)
            return;
        if (_playerRoot == null)
            _playerRoot = ResolvePlayerRoot();
        if (_playerRoot != null)
            _motor = _playerRoot.GetComponent<PlanetMotor_InputSystem>();
        if (_motor == null)
            _motor = GetComponentInParent<PlanetMotor_InputSystem>();
    }

    bool CanFireWeapons()
    {
        ResolveMotor();
        return _motor == null || !_motor.IsSwimming;
    }

    bool IsFiringFromBoat()
    {
        ResolveMotor();
        return _motor != null && _motor.IsInBoat;
    }

    float GetEffectiveSpreadDegrees()
    {
        float spread = Mathf.Max(0f, _spreadDegrees);
        if (!IsFiringFromBoat())
            return spread;
        return spread * Mathf.Max(1f, boatSpreadMultiplier) + Mathf.Max(0f, boatExtraSpreadDegrees);
    }

    void FirePelletVolley()
    {
        Camera cam = GetShootCamera();
        Vector3 origin = ResolveMuzzleOrigin(cam);
        Vector3 aimDir = ResolveAimDirection(cam, ref origin);
        int pellets = Mathf.Max(1, _pelletCount);
        float spread = GetEffectiveSpreadDegrees();
        bool allowMagichit = pellets <= 1 && spread < 0.5f;

        for (int i = 0; i < pellets; i++)
        {
            Vector3 dir = ApplySpread(aimDir, spread);
            bool hit = TryDamageAlongDirection(origin, dir, allowMagichit);
            if (projectilePrefab != null)
                SpawnProjectile(origin, dir, visualOnly: hit);
            else if (!hit && allowMagichit)
                TryDamageZombieFromCrosshairWithFalloff();
        }
    }

    Vector3 ResolveMuzzleOrigin(Camera cam)
    {
        if (firePoint != null)
            return firePoint.position;
        if (cam != null)
            return cam.transform.position;
        return transform.position + transform.forward * 2f;
    }

    Vector3 ResolveAimDirection(Camera cam, ref Vector3 origin)
    {
        Vector3 dir;
        if (aimProjectilesWithCamera && cam != null)
            dir = GetProjectileDirectionToCrosshair(cam, ref origin);
        else if (firePoint != null)
            dir = firePoint.forward;
        else if (cam != null)
            dir = cam.transform.forward;
        else
            dir = transform.forward;

        if (dir.sqrMagnitude < 0.0001f)
            dir = firePoint != null ? firePoint.forward : transform.forward;
        return dir.normalized;
    }

    static Vector3 ApplySpread(Vector3 forward, float spreadDegrees)
    {
        if (spreadDegrees <= 0.01f)
            return forward.normalized;

        float yaw = Random.Range(-spreadDegrees, spreadDegrees);
        float pitch = Random.Range(-spreadDegrees, spreadDegrees);
        Quaternion aim = Quaternion.LookRotation(forward.normalized, Vector3.up);
        return (aim * Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward).normalized;
    }

    bool TryDamageAlongDirection(Vector3 origin, Vector3 dir, bool allowMagichit)
    {
        // Prefer lock-on / aim-assist only for tight single-pellet shots.
        if (allowMagichit)
        {
            if (TryDamageZombieFromCrosshairWithFalloff())
                return true;
        }

        Ray ray = new Ray(origin, dir.normalized);
        RaycastHit[] hits = Physics.RaycastAll(ray, shootRange, ~0, QueryTriggerInteraction.Collide);
        if (hits == null || hits.Length == 0)
            return false;

        System.Array.Sort(hits, CompareRaycastHitsByDistance);
        RaycastHit? firstWorld = null;
        for (int i = 0; i < hits.Length; i++)
        {
            Collider col = hits[i].collider;
            if (IsIgnoredCrosshairCollider(col))
                continue;

            var zombie = col.GetComponentInParent<ZombieAI>();
            if (zombie != null)
            {
                if (firstWorld.HasValue && firstWorld.Value.distance + 0.05f < hits[i].distance)
                    return false;

                zombie.TakeDamage(GetDamageAtDistance(hits[i].distance));
                NotifyHitConfirmed();
                return true;
            }

            if (!firstWorld.HasValue && !col.isTrigger)
                firstWorld = hits[i];
        }

        return false;
    }

    bool TryDamageZombieFromCrosshairWithFalloff()
    {
        Camera cam = GetShootCamera();
        ZombieAI lockOnZombie;
        Vector3 lockOnAimPoint;
        if (TryGetLockOnTarget(cam, out lockOnZombie, out lockOnAimPoint) && lockOnZombie != null)
        {
            float dist = cam != null ? Vector3.Distance(cam.transform.position, lockOnAimPoint) : 0f;
            lockOnZombie.TakeDamage(GetDamageAtDistance(dist));
            NotifyHitConfirmed();
            return true;
        }

        if (TryGetCrosshairHit(out RaycastHit hit))
        {
            var zombie = hit.collider.GetComponentInParent<ZombieAI>();
            if (zombie != null)
            {
                zombie.TakeDamage(GetDamageAtDistance(hit.distance));
                NotifyHitConfirmed();
                return true;
            }
        }

        ZombieAI assistedZombie;
        Vector3 assistedAimPoint;
        if (TryGetAimAssistTarget(cam, out assistedZombie, out assistedAimPoint) && assistedZombie != null)
        {
            float dist = cam != null ? Vector3.Distance(cam.transform.position, assistedAimPoint) : 0f;
            assistedZombie.TakeDamage(GetDamageAtDistance(dist));
            NotifyHitConfirmed();
            return true;
        }

        return false;
    }

    void SpawnProjectile(Vector3 origin, Vector3 dir, bool visualOnly)
    {
        GameObject p = Instantiate(projectilePrefab, origin, Quaternion.LookRotation(dir));
        var proj = p.GetComponent<Projectile>();
        if (proj != null)
        {
            int dmg = visualOnly ? 0 : GetBuffedDamage(Mathf.Max(1, damagePerShot));
            proj.ConfigureFromWeapon(
                _projectileColor,
                dmg,
                falloff: !visualOnly,
                _damageFalloffStart,
                _damageFalloffEnd,
                _damageFalloffMinMultiplier);
        }

        Rigidbody rb = p.GetComponent<Rigidbody>();
        if (rb != null)
            rb.linearVelocity = dir * projectileSpeed;
        else
        {
            var mover = p.GetComponent<ProjectileMover>();
            if (mover == null) mover = p.AddComponent<ProjectileMover>();
            mover.direction = dir;
            mover.speed = projectileSpeed;
        }
    }
}