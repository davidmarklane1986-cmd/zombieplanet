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
    public float fireCooldown = 0.35f;
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

    float _nextFireTime;
    PlayerBuffController _buffs;
    Transform _playerRoot;
    CombatTargeting _combatTargeting;
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

        _playerRoot = ResolvePlayerRoot();
        if (preferCharacterModelMuzzle && (firePoint == null || IsCameraAnchoredFirePoint(_playerRoot, firePoint)))
        {
            Transform characterMuzzle = FindCharacterModelMuzzle(_playerRoot);
            if (characterMuzzle != null)
                firePoint = characterMuzzle;
        }

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

        Transform muzzle = FindDescendantByName(searchRoot, "Muzzle_Bone");
        if (muzzle != null)
            return muzzle;

        Transform weaponBone = FindDescendantByName(searchRoot, "Weapon_Bone");
        if (weaponBone != null)
        {
            muzzle = FindDescendantByName(weaponBone, "Muzzle_Bone");
            if (muzzle != null)
                return muzzle;
            return EnsureRuntimeMuzzle(weaponBone, "GunMuzzle_Runtime", new Vector3(0f, 0f, 0.35f));
        }

        Animator anim = searchRoot.GetComponentInChildren<Animator>(true);
        if (anim != null && anim.isHuman)
        {
            Transform rightHand = anim.GetBoneTransform(HumanBodyBones.RightHand);
            if (rightHand != null)
                return EnsureRuntimeMuzzle(rightHand, "GunMuzzle_Runtime", new Vector3(0.1f, 0f, 0.22f));
        }

        muzzle = FindDescendantByName(searchRoot, "GunMuzzle");
        if (muzzle != null)
            return muzzle;

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
        for (int i = 0; i < hits.Length; i++)
        {
            if (IsIgnoredCrosshairCollider(hits[i].collider))
                continue;

            hit = hits[i];
            return true;
        }

        hit = new RaycastHit();
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

        float rof = _buffs != null ? Mathf.Max(0.05f, _buffs.CombinedFireRateMultiplier) : 1f;
        float cooldown = fireCooldown / rof;
        bool rapidFireActive = HasRapidFirePowerUp();
        bool wantsFire = rapidFireActive ? WantsFireHeld() : WantsFirePressedThisFrame();

        if (!wantsFire || Time.time < _nextFireTime)
            return;

        _nextFireTime = Time.time + cooldown;
        ShotFired?.Invoke();

        if (projectilePrefab != null)
            FireProjectile();
        else
            FireRaycast();
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

    /// <summary>Hit-scan: ray from camera through screen center (like PlayerShoot.cs). No prefab needed.</summary>
    void FireRaycast()
    {
        Camera cam = GetShootCamera();
        ZombieAI lockOnZombie;
        Vector3 lockOnAimPoint;
        if (TryGetLockOnTarget(cam, out lockOnZombie, out lockOnAimPoint) && lockOnZombie != null)
        {
            lockOnZombie.TakeDamage(GetBuffedDamage(damagePerShot));
            NotifyHitConfirmed();
            return;
        }

        if (TryGetCrosshairHit(out RaycastHit hit))
        {
            var zombie = hit.collider.GetComponentInParent<ZombieAI>();
            if (zombie != null)
            {
                zombie.TakeDamage(GetBuffedDamage(damagePerShot));
                NotifyHitConfirmed();
            }
            return;
        }

        ZombieAI assistedZombie;
        Vector3 assistedAimPoint;
        if (TryGetAimAssistTarget(cam, out assistedZombie, out assistedAimPoint) && assistedZombie != null)
        {
            assistedZombie.TakeDamage(GetBuffedDamage(damagePerShot));
            NotifyHitConfirmed();
        }
    }

    void FireProjectile()
    {
        Camera cam = GetShootCamera();

        Vector3 origin;
        if (firePoint != null)
            origin = firePoint.position;
        else if (cam != null)
            origin = cam.transform.position;
        else
            origin = transform.position + transform.forward * 2f;

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
        GameObject p = Instantiate(projectilePrefab, origin, Quaternion.LookRotation(dir));
        var proj = p.GetComponent<Projectile>();
        if (proj != null)
            proj.damage = GetBuffedDamage(proj.damage);

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
