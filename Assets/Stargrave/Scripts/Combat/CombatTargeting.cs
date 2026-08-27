using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Lock-on acquisition, cycling, LOS, aim-assist scoring, and look-at solutions in the planet tangent frame.
/// Lives on the player root (same object as <see cref="Rigidbody"/>). <see cref="PlayerShooting"/> reads aim from here.
/// </summary>
[DefaultExecutionOrder(40)]
public class CombatTargeting : MonoBehaviour
{
    [Header("Camera")]
    [Tooltip("Gameplay camera; if unset, uses Camera.main at runtime.")]
    public Camera combatCamera;

    [Header("Lock-On")]
    [Tooltip("Devil May Cry style: tap Left Bumper (or keyboard key) to lock, tap again to release. Off = hold LB to keep lock.")]
    public bool lockOnToggleMode = true;
    [Tooltip("When toggle mode is on, also accept this key (Input System). Off = gamepad only.")]
    public bool lockOnKeyboardToggleEnabled = true;
    public Key lockOnKeyboardToggleKey = Key.Q;
    public bool lockOnEnabled = true;
    [Range(0.05f, 0.75f)] public float lockOnMaxScreenDistance = 0.42f;
    [Tooltip("Maximum lock-on distance from the camera. 0 uses aim range.")]
    public float lockOnRange = 0f;

    [Header("Aim Assist (shooting / lock candidate scoring)")]
    public bool aimAssistEnabled = true;
    [Range(0.5f, 15f)] public float aimAssistMaxAngle = 6f;
    [Tooltip("Maximum distance for aim assist. 0 uses aim range.")]
    public float aimAssistRange = 0f;

    [Header("Soft Look Assist (gamepad, optional)")]
    [Tooltip("Off by default: uses the same aim-assist zombie as shooting and can fight manual aim if mis-tuned.")]
    public bool enableSoftLookAssist = false;
    [Tooltip("Extra yaw per second toward aim-assist target when not hard-locked.")]
    public float softLookAssistYawDegreesPerSecond = 55f;
    [Range(0f, 1f)]
    [Tooltip("Blend 0 = off, 1 = full assist when stick deflection is small.")]
    public float softLookAssistStickDeadzoneBlend = 0.35f;

    [Header("Planet")]
    [Tooltip("Optional; else Planet tag / transform.")]
    public Transform planetCenterOverride;

    public float AimRangeFallback { get; set; } = 100f;

    Transform _playerRoot;
    Transform _planetCenterCached;

    readonly List<LockOnCandidate> _lockOnCandidates = new List<LockOnCandidate>();
    ZombieAI _lockOnTarget;
    Vector3 _lockOnAimPoint;
    bool _lockOnActive;
    bool _lockOnHeldLastFrame;

    public bool HasHardLock => _lockOnActive && _lockOnTarget != null;
    public ZombieAI CurrentLockOnTarget => _lockOnTarget;

    struct LockOnCandidate
    {
        public ZombieAI zombie;
        public Vector3 aimPoint;
        public Vector3 viewportPosition;
        public float centerScore;
    }

    void Awake()
    {
        _playerRoot = ResolvePlayerRoot();
        if (combatCamera == null)
            combatCamera = Camera.main;
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

    public Camera GetCombatCamera()
    {
        if (combatCamera != null)
            return combatCamera;
        return Camera.main;
    }

    float GetLockOnRange()
    {
        return lockOnRange > 0.05f ? Mathf.Min(lockOnRange, AimRangeFallback) : AimRangeFallback;
    }

    float GetAimAssistRange()
    {
        return aimAssistRange > 0.05f ? Mathf.Min(aimAssistRange, AimRangeFallback) : AimRangeFallback;
    }

    void CachePlanetCenterIfNeeded()
    {
        if (planetCenterOverride != null)
        {
            _planetCenterCached = planetCenterOverride;
            return;
        }

        if (_planetCenterCached != null)
            return;

        GameObject tagged = GameObject.FindGameObjectWithTag("Planet");
        if (tagged != null)
        {
            var planet = tagged.GetComponentInParent<Planet>();
            _planetCenterCached = planet != null ? planet.transform : tagged.transform;
            return;
        }

        Planet found = Object.FindFirstObjectByType<Planet>(FindObjectsInactive.Exclude);
        if (found != null)
            _planetCenterCached = found.transform;
    }

    Vector3 ResolvePlanetUpAt(Vector3 worldPos, Vector3 fallbackUp)
    {
        CachePlanetCenterIfNeeded();
        Transform center = planetCenterOverride != null ? planetCenterOverride : _planetCenterCached;
        return PlanetTangentBasis.ResolvePlanetUp(worldPos, center, fallbackUp);
    }

    bool WantsLockOn()
    {
#if ENABLE_INPUT_SYSTEM
        return lockOnEnabled && Gamepad.current != null && Gamepad.current.leftShoulder.isPressed;
#else
        return false;
#endif
    }

    /// <summary>True while lock input is held (hold mode only). Used to avoid soft aim-assist fighting lock.</summary>
    public bool IsLockOnInputHeld()
    {
        if (lockOnToggleMode)
            return false;
        return WantsLockOn();
    }

    bool ReadLockTogglePressedThisFrame()
    {
#if ENABLE_INPUT_SYSTEM
        if (!lockOnEnabled)
            return false;
        if (Gamepad.current != null && Gamepad.current.leftShoulder.wasPressedThisFrame)
            return true;
        if (lockOnKeyboardToggleEnabled && Keyboard.current != null && Keyboard.current[lockOnKeyboardToggleKey].wasPressedThisFrame)
            return true;
#endif
        return false;
    }

    int ReadLockOnCycleDirection()
    {
#if ENABLE_INPUT_SYSTEM
        if (!lockOnEnabled || Gamepad.current == null)
            return 0;

        if (Gamepad.current.dpad.right.wasPressedThisFrame || Gamepad.current.dpad.down.wasPressedThisFrame)
            return 1;
        if (Gamepad.current.dpad.left.wasPressedThisFrame || Gamepad.current.dpad.up.wasPressedThisFrame)
            return -1;
#endif
        return 0;
    }

    bool WantsLockOnPressedThisFrame()
    {
#if ENABLE_INPUT_SYSTEM
        bool held = WantsLockOn();
        bool pressed = held && !_lockOnHeldLastFrame;
        _lockOnHeldLastFrame = held;
        return pressed;
#else
        return false;
#endif
    }

    static bool IsIgnoredCrosshairCollider(Collider other, Transform playerRoot)
    {
        if (other == null)
            return true;
        if (other.gameObject.layer == 2)
            return true;
        if (other.isTrigger && other.GetComponentInParent<ZombieAI>() == null)
            return true;
        if (playerRoot != null && other.transform.IsChildOf(playerRoot))
            return true;
        if (other.GetComponentInParent<Projectile>() != null)
            return true;
        return false;
    }

    static int CompareRaycastHitsByDistance(RaycastHit a, RaycastHit b)
    {
        return a.distance.CompareTo(b.distance);
    }

    bool TryGetCrosshairHit(Camera cam, float maxDistance, out RaycastHit hit)
    {
        if (cam == null)
        {
            hit = new RaycastHit();
            return false;
        }

        Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        RaycastHit[] hits = Physics.RaycastAll(ray, maxDistance, ~0, QueryTriggerInteraction.Collide);
        if (hits == null || hits.Length == 0)
        {
            hit = new RaycastHit();
            return false;
        }

        System.Array.Sort(hits, CompareRaycastHitsByDistance);
        for (int i = 0; i < hits.Length; i++)
        {
            if (IsIgnoredCrosshairCollider(hits[i].collider, _playerRoot))
                continue;

            hit = hits[i];
            return true;
        }

        hit = new RaycastHit();
        return false;
    }

    static Collider GetZombieAimCollider(ZombieAI zombie)
    {
        if (zombie == null)
            return null;

        Collider[] colliders = zombie.GetComponentsInChildren<Collider>(false);
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider c = colliders[i];
            if (c != null && !c.isTrigger)
                return c;
        }

        return zombie.GetComponent<Collider>();
    }

    public static bool TryGetZombieAimPoint(ZombieAI zombie, out Vector3 aimPoint)
    {
        aimPoint = Vector3.zero;
        if (zombie == null)
            return false;

        Transform runtimeHitboxes = zombie.transform.Find("RuntimeHitboxes");
        if (runtimeHitboxes != null)
        {
            BoxCollider torso = runtimeHitboxes.GetComponent<BoxCollider>();
            if (torso != null && torso.enabled)
            {
                float chestOffset = Mathf.Min(torso.bounds.extents.y * 0.45f, 0.22f);
                aimPoint = torso.bounds.center + zombie.transform.up * chestOffset;
                return true;
            }
        }

        Collider[] colliders = zombie.GetComponentsInChildren<Collider>(false);
        bool hasBounds = false;
        Bounds bounds = new Bounds(zombie.transform.position, Vector3.zero);

        for (int i = 0; i < colliders.Length; i++)
        {
            Collider collider = colliders[i];
            if (collider == null || !collider.enabled)
                continue;

            if (!hasBounds)
            {
                bounds = collider.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(collider.bounds);
            }
        }

        if (!hasBounds)
        {
            Collider fallback = GetZombieAimCollider(zombie);
            if (fallback == null)
                return false;
            bounds = fallback.bounds;
        }

        aimPoint = bounds.center;
        return true;
    }

    public static bool TryGetZombieCameraAimPoint(ZombieAI zombie, out Vector3 aimPoint)
    {
        aimPoint = Vector3.zero;
        if (zombie == null)
            return false;

        Transform runtimeHitboxes = zombie.transform.Find("RuntimeHitboxes");
        if (runtimeHitboxes != null)
        {
            SphereCollider head = runtimeHitboxes.GetComponent<SphereCollider>();
            if (head != null && head.enabled)
            {
                aimPoint = head.bounds.center;
                return true;
            }

            BoxCollider torso = runtimeHitboxes.GetComponent<BoxCollider>();
            if (torso != null && torso.enabled)
            {
                float upperTorsoOffset = Mathf.Min(torso.bounds.extents.y * 0.75f, 0.32f);
                aimPoint = torso.bounds.center + zombie.transform.up * upperTorsoOffset;
                return true;
            }
        }

        return TryGetZombieAimPoint(zombie, out aimPoint);
    }

    bool HasLineOfSightToZombie(Camera cam, ZombieAI targetZombie, Vector3 aimPoint)
    {
        if (cam == null || targetZombie == null)
            return false;

        Vector3 origin = cam.transform.position;
        Vector3 toTarget = aimPoint - origin;
        float distance = toTarget.magnitude;
        if (distance <= 0.001f)
            return false;

        RaycastHit[] hits = Physics.RaycastAll(origin, toTarget / distance, distance + 0.05f, ~0, QueryTriggerInteraction.Collide);
        if (hits == null || hits.Length == 0)
            return false;

        System.Array.Sort(hits, CompareRaycastHitsByDistance);
        for (int i = 0; i < hits.Length; i++)
        {
            Collider hitCollider = hits[i].collider;
            if (IsIgnoredCrosshairCollider(hitCollider, _playerRoot))
                continue;

            ZombieAI hitZombie = hitCollider != null ? hitCollider.GetComponentInParent<ZombieAI>() : null;
            return hitZombie == targetZombie;
        }

        return false;
    }

    /// <summary>
    /// Keeps an existing lock while the player steers the camera to center the target. Strict acquisition requires
    /// the aim point inside the 0–1 viewport; during centering the point can sit outside that rect briefly, so we
    /// only require in-front-of-camera, range, and LOS.
    /// </summary>
    bool TryValidateSustainedLockOn(Camera cam, ZombieAI zombie, out Vector3 aimPoint)
    {
        aimPoint = Vector3.zero;
        if (cam == null || zombie == null || !zombie.isActiveAndEnabled || zombie.IsDead)
            return false;

        if (!TryGetZombieAimPoint(zombie, out aimPoint))
            return false;

        Vector3 toCandidate = aimPoint - cam.transform.position;
        float distance = toCandidate.magnitude;
        if (distance <= 0.001f || distance > GetLockOnRange())
            return false;

        Vector3 viewport = cam.WorldToViewportPoint(aimPoint);
        if (viewport.z <= 0.02f)
            return false;

        if (!HasLineOfSightToZombie(cam, zombie, aimPoint))
            return false;

        return true;
    }

    bool TryBuildLockOnCandidate(Camera cam, ZombieAI zombie, out LockOnCandidate candidate)
    {
        candidate = new LockOnCandidate();

        if (cam == null || zombie == null || !zombie.isActiveAndEnabled || zombie.IsDead)
            return false;

        Vector3 aimPoint;
        if (!TryGetZombieAimPoint(zombie, out aimPoint))
            return false;

        Vector3 toCandidate = aimPoint - cam.transform.position;
        float distance = toCandidate.magnitude;
        if (distance <= 0.001f || distance > GetLockOnRange())
            return false;

        Vector3 viewport = cam.WorldToViewportPoint(aimPoint);
        if (viewport.z <= 0f)
            return false;
        if (viewport.x < 0f || viewport.x > 1f || viewport.y < 0f || viewport.y > 1f)
            return false;

        float screenDx = viewport.x - 0.5f;
        float screenDy = viewport.y - 0.5f;
        float centerDistance = Mathf.Sqrt(screenDx * screenDx + screenDy * screenDy);

        if (!HasLineOfSightToZombie(cam, zombie, aimPoint))
            return false;

        float centerPenalty = centerDistance;
        if (centerDistance > lockOnMaxScreenDistance)
            centerPenalty += (centerDistance - lockOnMaxScreenDistance) * 0.5f;
        float distancePenalty = distance * 0.0025f;

        candidate.zombie = zombie;
        candidate.aimPoint = aimPoint;
        candidate.viewportPosition = viewport;
        candidate.centerScore = centerPenalty + distancePenalty;
        return true;
    }

    void RebuildLockOnCandidates(Camera cam)
    {
        _lockOnCandidates.Clear();
        if (cam == null || !lockOnEnabled)
            return;

        ZombieAI[] zombies = Object.FindObjectsByType<ZombieAI>(FindObjectsInactive.Exclude);
        for (int i = 0; i < zombies.Length; i++)
        {
            LockOnCandidate candidate;
            if (TryBuildLockOnCandidate(cam, zombies[i], out candidate))
                _lockOnCandidates.Add(candidate);
        }
    }

    static int CompareLockOnCandidatesForCycle(LockOnCandidate a, LockOnCandidate b)
    {
        int x = a.viewportPosition.x.CompareTo(b.viewportPosition.x);
        if (x != 0)
            return x;
        return a.viewportPosition.y.CompareTo(b.viewportPosition.y);
    }

    int GetLockOnCandidateIndex(ZombieAI zombie)
    {
        if (zombie == null)
            return -1;

        for (int i = 0; i < _lockOnCandidates.Count; i++)
        {
            if (_lockOnCandidates[i].zombie == zombie)
                return i;
        }

        return -1;
    }

    void ApplyLockOnCandidate(LockOnCandidate candidate)
    {
        _lockOnTarget = candidate.zombie;
        _lockOnAimPoint = candidate.aimPoint;
        _lockOnActive = _lockOnTarget != null;
    }

    void ClearLockOnTarget()
    {
        _lockOnTarget = null;
        _lockOnAimPoint = Vector3.zero;
        _lockOnActive = false;
    }

    void SelectBestLockOnCandidate()
    {
        if (_lockOnCandidates.Count == 0)
        {
            ClearLockOnTarget();
            return;
        }

        int bestIndex = 0;
        float bestScore = float.PositiveInfinity;
        for (int i = 0; i < _lockOnCandidates.Count; i++)
        {
            if (_lockOnCandidates[i].centerScore < bestScore)
            {
                bestScore = _lockOnCandidates[i].centerScore;
                bestIndex = i;
            }
        }

        ApplyLockOnCandidate(_lockOnCandidates[bestIndex]);
    }

    void SelectPreferredLockOnCandidate(Camera cam)
    {
        if (_lockOnCandidates.Count == 0)
        {
            ClearLockOnTarget();
            return;
        }

        if (cam != null && TryGetCrosshairHit(cam, GetLockOnRange(), out RaycastHit hit))
        {
            ZombieAI aimedZombie = hit.collider != null ? hit.collider.GetComponentInParent<ZombieAI>() : null;
            if (aimedZombie != null)
            {
                int aimedIndex = GetLockOnCandidateIndex(aimedZombie);
                if (aimedIndex >= 0)
                {
                    ApplyLockOnCandidate(_lockOnCandidates[aimedIndex]);
                    return;
                }
            }
        }

        SelectBestLockOnCandidate();
    }

    void CycleLockOnTarget(int direction)
    {
        if (_lockOnCandidates.Count == 0)
        {
            ClearLockOnTarget();
            return;
        }

        _lockOnCandidates.Sort(CompareLockOnCandidatesForCycle);
        int currentIndex = GetLockOnCandidateIndex(_lockOnTarget);
        if (currentIndex < 0)
        {
            SelectBestLockOnCandidate();
            return;
        }

        int nextIndex = (currentIndex + direction + _lockOnCandidates.Count) % _lockOnCandidates.Count;
        ApplyLockOnCandidate(_lockOnCandidates[nextIndex]);
    }

    void UpdateLockOnState(Camera cam)
    {
        if (!lockOnEnabled || cam == null)
        {
            ClearLockOnTarget();
            return;
        }

        if (lockOnToggleMode)
        {
            UpdateLockOnStateToggle(cam);
            return;
        }

        bool wantsLockOn = WantsLockOn();
        bool pressedThisFrame = WantsLockOnPressedThisFrame();
        if (!wantsLockOn)
        {
            ClearLockOnTarget();
            return;
        }

        RebuildLockOnCandidates(cam);
        Vector3 sustainedLockAim;
        if (_lockOnCandidates.Count == 0)
        {
            if (_lockOnTarget != null && TryValidateSustainedLockOn(cam, _lockOnTarget, out sustainedLockAim))
            {
                _lockOnAimPoint = sustainedLockAim;
                _lockOnActive = true;
                return;
            }

            ClearLockOnTarget();
            return;
        }

        int cycleDirection = ReadLockOnCycleDirection();
        if (cycleDirection != 0)
        {
            CycleLockOnTarget(cycleDirection);
            return;
        }

        if (pressedThisFrame)
        {
            SelectPreferredLockOnCandidate(cam);
            return;
        }

        int currentIndex = GetLockOnCandidateIndex(_lockOnTarget);
        if (currentIndex >= 0)
        {
            ApplyLockOnCandidate(_lockOnCandidates[currentIndex]);
            return;
        }

        if (_lockOnTarget != null && TryValidateSustainedLockOn(cam, _lockOnTarget, out sustainedLockAim))
        {
            _lockOnAimPoint = sustainedLockAim;
            _lockOnActive = true;
            return;
        }

        ClearLockOnTarget();
    }

    /// <summary>Tap to lock / tap to clear (DMC-style). D-pad cycles while locked.</summary>
    void UpdateLockOnStateToggle(Camera cam)
    {
        if (ReadLockTogglePressedThisFrame())
        {
            if (_lockOnActive && _lockOnTarget != null)
            {
                ClearLockOnTarget();
                return;
            }

            RebuildLockOnCandidates(cam);
            if (_lockOnCandidates.Count > 0)
            {
                SelectPreferredLockOnCandidate(cam);
                return;
            }

            ClearLockOnTarget();
            return;
        }

        if (!_lockOnActive || _lockOnTarget == null)
            return;

        RebuildLockOnCandidates(cam);
        Vector3 sustainedLockAim;

        int cycleDirection = ReadLockOnCycleDirection();
        if (cycleDirection != 0 && _lockOnCandidates.Count > 0)
        {
            CycleLockOnTarget(cycleDirection);
            return;
        }

        if (_lockOnCandidates.Count == 0)
        {
            if (TryValidateSustainedLockOn(cam, _lockOnTarget, out sustainedLockAim))
            {
                _lockOnAimPoint = sustainedLockAim;
                _lockOnActive = true;
                return;
            }

            ClearLockOnTarget();
            return;
        }

        int currentIndex = GetLockOnCandidateIndex(_lockOnTarget);
        if (currentIndex >= 0)
        {
            ApplyLockOnCandidate(_lockOnCandidates[currentIndex]);
            return;
        }

        if (TryValidateSustainedLockOn(cam, _lockOnTarget, out sustainedLockAim))
        {
            _lockOnAimPoint = sustainedLockAim;
            _lockOnActive = true;
            return;
        }

        ClearLockOnTarget();
    }

    void Update()
    {
        Camera cam = GetCombatCamera();
        UpdateLockOnState(cam);
    }

    public bool TryGetLockOnTarget(Camera cam, out ZombieAI zombie, out Vector3 aimPoint)
    {
        zombie = null;
        aimPoint = Vector3.zero;

        if (!_lockOnActive || _lockOnTarget == null || cam == null)
            return false;

        if (TryValidateSustainedLockOn(cam, _lockOnTarget, out aimPoint))
        {
            zombie = _lockOnTarget;
            _lockOnAimPoint = aimPoint;
            return true;
        }

        return false;
    }

    public bool TryGetAimAssistTarget(Camera cam, out ZombieAI zombie, out Vector3 aimPoint)
    {
        zombie = null;
        aimPoint = Vector3.zero;

        if (TryGetLockOnTarget(cam, out zombie, out aimPoint))
            return true;

        if (!aimAssistEnabled || cam == null)
            return false;

        ZombieAI[] zombies = Object.FindObjectsByType<ZombieAI>(FindObjectsInactive.Exclude);
        if (zombies == null || zombies.Length == 0)
            return false;

        float maxRange = GetAimAssistRange();
        float bestScore = float.PositiveInfinity;
        Vector3 camPos = cam.transform.position;
        Vector3 camForward = cam.transform.forward;

        for (int i = 0; i < zombies.Length; i++)
        {
            ZombieAI candidateZombie = zombies[i];
            if (candidateZombie == null || !candidateZombie.isActiveAndEnabled || candidateZombie.IsDead)
                continue;

            Vector3 candidatePoint;
            if (!TryGetZombieAimPoint(candidateZombie, out candidatePoint))
                continue;
            Vector3 toCandidate = candidatePoint - camPos;
            float distance = toCandidate.magnitude;
            if (distance <= 0.001f || distance > maxRange)
                continue;

            float angle = Vector3.Angle(camForward, toCandidate);
            if (angle > aimAssistMaxAngle)
                continue;

            if (!HasLineOfSightToZombie(cam, candidateZombie, candidatePoint))
                continue;

            float score = angle + distance * 0.01f;
            if (score >= bestScore)
                continue;

            bestScore = score;
            zombie = candidateZombie;
            aimPoint = candidatePoint;
        }

        return zombie != null;
    }

    public bool TryGetLockOnCameraAimWorld(Camera cam, out Vector3 aimPoint)
    {
        aimPoint = Vector3.zero;
        ZombieAI zombie;
        Vector3 bulletAim;
        if (!TryGetLockOnTarget(cam, out zombie, out bulletAim) || zombie == null)
            return false;

        // Match shooting aim (chest/torso) so lock-on does not yank the camera toward the head.
        aimPoint = bulletAim;
        return true;
    }

    /// <summary>
    /// For rotating the body/camera toward lock: flat yaw in the body's tangent frame toward the aim point;
    /// pitch from the gameplay camera eye toward that point.
    /// </summary>
    public bool TryComputeLockLookTurn(
        Transform cameraTarget,
        Transform body,
        float minPitch,
        float maxPitch,
        out Vector3 desiredYawForward,
        out float desiredPitch)
    {
        desiredYawForward = Vector3.zero;
        desiredPitch = 0f;

        if (cameraTarget == null || body == null)
            return false;

        Camera cam = GetCombatCamera();
        Vector3 aimPoint;
        if (!TryGetLockOnTarget(cam, out _, out aimPoint))
            return false;

        // One tangent frame at the body (what the rigidbody yaws in). Mixing eye position + body-up for the
        // horizontal lock vector skews "toward target" left/right, especially up close or on a sphere.
        Vector3 planetUp = ResolvePlanetUpAt(body.position, body.up);
        Vector3 toTargetHorizontal = aimPoint - body.position;
        if (toTargetHorizontal.sqrMagnitude < 0.0001f)
            return false;

        Vector3 desiredFlat = PlanetTangentBasis.ProjectOnTangentPlane(toTargetHorizontal, planetUp);
        if (desiredFlat.sqrMagnitude < 0.0001f)
            return false;

        desiredYawForward = desiredFlat.normalized;

        Vector3 eyeWorld = cam != null ? cam.transform.position : cameraTarget.position;
        Vector3 horizontalForward = cam != null
            ? PlanetTangentBasis.GetTangentForward(cam.transform.forward, planetUp, body.forward)
            : PlanetTangentBasis.GetTangentForward(cameraTarget.forward, planetUp, body.forward);
        desiredPitch = PlanetTangentBasis.ComputePitchDegreesTowardPoint(
            eyeWorld,
            planetUp,
            horizontalForward,
            aimPoint,
            minPitch,
            maxPitch);
        return true;
    }

    /// <summary>
    /// When not hard-locked, nudge yaw toward best aim-assist zombie (gamepad + small stick deflection).
    /// </summary>
    public bool TryGetSoftLookYawDelta(
        Transform body,
        Transform cameraTarget,
        Vector3 currentTangentForward,
        float dt,
        out float yawDegrees)
    {
        yawDegrees = 0f;
        if (!enableSoftLookAssist || HasHardLock || IsLockOnInputHeld() || !aimAssistEnabled || body == null || cameraTarget == null)
            return false;

#if ENABLE_INPUT_SYSTEM
        if (Gamepad.current == null)
            return false;

        Vector2 stick = Gamepad.current.rightStick.ReadValue();
        float mag = stick.magnitude;
        if (mag > 0.55f)
            return false;

        float blend = Mathf.Clamp01((0.55f - mag) / Mathf.Max(0.05f, softLookAssistStickDeadzoneBlend));
        if (blend <= 0.01f || softLookAssistYawDegreesPerSecond <= 0f)
            return false;
#else
        return false;
#endif

        Camera cam = GetCombatCamera();
        ZombieAI z;
        Vector3 aimWorld;
        if (!TryGetAimAssistTarget(cam, out z, out aimWorld))
            return false;

        Vector3 planetUp = ResolvePlanetUpAt(body.position, body.up);
        // Match aim scoring (camera forward) — cameraTarget.forward + pivot offset often disagrees with the lens and feels like the view is pushed away from the target.
        Vector3 flatCurrent = cam != null
            ? PlanetTangentBasis.GetTangentForward(cam.transform.forward, planetUp, body.forward)
            : PlanetTangentBasis.GetTangentForward(currentTangentForward, planetUp, body.forward);

        Vector3 eyeWorld = cam != null ? cam.transform.position : cameraTarget.position;
        Vector3 toFlat = PlanetTangentBasis.ProjectOnTangentPlane(aimWorld - eyeWorld, planetUp);
        if (toFlat.sqrMagnitude < 1e-8f)
            return false;

        Vector3 assistForward = toFlat.normalized;
        float signed = PlanetTangentBasis.SignedYawDegrees(flatCurrent, assistForward, planetUp);
        float maxStep = softLookAssistYawDegreesPerSecond * dt * blend;
        yawDegrees = Mathf.Clamp(signed, -maxStep, maxStep);
        return Mathf.Abs(yawDegrees) > 0.01f;
    }
}
