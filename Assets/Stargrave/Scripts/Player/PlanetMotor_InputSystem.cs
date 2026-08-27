using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(CapsuleCollider))]
public class PlanetMotor_InputSystem : MonoBehaviour
{
    [Tooltip("Camera-relative movement. Use Camera Target (child of Player) so move matches look.")]
    public Transform cameraTransform;

    [Header("Move")]
    [Tooltip("DS / DualSense often feel inverted vs WASD with the same math; turn off for Xbox / Switch-style pads that match keyboard.")]
    public bool invertGamepadLeftStick = true;
    public float moveSpeed = 8f;
    [HideInInspector] public float externalSpeedMultiplier = 1f;
    [Tooltip("Fortnite-style: hold Shift or click left stick (L3) while moving.")]
    public float sprintSpeedMultiplier = 1.45f;
    [Range(0f, 1f)] public float airControl = 0.35f;

    [Header("Lock-On move")]
    [Tooltip("While hard-locked (CombatTargeting), left/right strafe orbits the lock target on the surface; forward/back moves in/out relative to them (DMC-style).")]
    public bool enableLockOnOrbitStrafe = true;
    [Tooltip("Below this horizontal separation from the aim point, fall back to camera-relative move.")]
    [Min(0.05f)] public float lockOnOrbitMinRadius = 0.28f;
    [Tooltip("Flip left/right orbit direction if strafe feels inverted for your planet orientation.")]
    public bool invertLockOnOrbitStrafe = false;

    [Header("Jump")]
    public float jumpImpulse = 6.15f;
    [HideInInspector] public float externalJumpMultiplier = 1f;
    public float coyoteTime = 0.08f;
    public float jumpLockTime = 0.12f;

    [Header("Jump Feel")]
    [Tooltip("Extra gravity multiplier while rising and still holding jump.")]
    public float jumpRiseGravityMultiplier = 1.12f;
    [Tooltip("Extra gravity multiplier while rising after letting go of jump.")]
    public float jumpCutGravityMultiplier = 2.05f;
    [Tooltip("Extra gravity multiplier while falling.")]
    public float fallGravityMultiplier = 2.45f;

    [Header("Grounding")]
    public float groundCheckDistance = 1.1f;
    public LayerMask groundMask = ~0;

    [Header("Swimming")]
    [Tooltip("Master switch for float-on-surface water swimming.")]
    public bool enableSwimming = true;
    [Tooltip("Horizontal swim speed (tangent to the ocean sphere) while floating. Replaces walk speed in water.")]
    public float swimSpeed = 6f;
    [Tooltip("How deep the player root settles below the ocean surface while floating, in world units. " +
             "Larger = body sits lower (more submerged). Tune so the water sits around chest/waist.")]
    public float buoyancyTargetDepth = 0.9f;
    [Tooltip("Spring stiffness pulling the player toward the float line. Higher = snappier surfacing.")]
    public float buoyancyStrength = 18f;
    [Tooltip("Vertical damping (along up) while floating, to stop bobbing forever.")]
    public float buoyancyDamping = 4f;
    [Tooltip("General water resistance applied to velocity while swimming, so movement feels heavier than air.")]
    public float waterDrag = 2.5f;
    [Tooltip("Root depth (below surface) at which wading turns into floating/swimming. Below this you keep walking.")]
    public float wadeDepthThreshold = 0.6f;
    [Tooltip("Root depth at which swimming releases back to walking (hysteresis; should be < wadeDepthThreshold).")]
    public float swimExitDepth = 0.45f;
    [Tooltip("Optional dive: vertical speed when holding the dive key (Left Ctrl) while swimming. " +
             "The buoyancy spring still surfaces you when released, so you can't settle on the seabed.")]
    public float swimDiveSpeed = 2.5f;
    [Range(0f, 1f)]
    [Tooltip("How strongly the float line rides the wave height (0 = static sea level, 1 = full bob with the swell).")]
    public float waveFollowStrength = 1f;
    [Tooltip("Log swim diagnostics ~2x/sec (ocean found?, ocean radius, distance from centre, depth, swim state). " +
             "Enable this if swimming isn't engaging and report the numbers.")]
    public bool debugSwim = false;
    [Tooltip("Set by PlayerSwimStamina when empty (slower swim while drowning).")]
    [HideInInspector] public float swimStaminaSpeedMultiplier = 1f;

    [Header("Footsteps")]
    [Tooltip("Play footstep SFX while grounded and moving. Distance-based, so cadence speeds up automatically when running.")]
    public bool enableFootsteps = true;
    [Tooltip("Metres of horizontal travel between footstep sounds. Smaller = faster cadence.")]
    [Min(0.3f)] public float footstepStrideDistance = 2.1f;
    [Tooltip("Minimum horizontal speed (m/s) before footsteps trigger (ignores micro-drift).")]
    [Min(0f)] public float footstepMinSpeed = 0.6f;
    [Tooltip("Hard floor on the time between footsteps so very high speeds can't machine-gun the sound.")]
    [Min(0.05f)] public float footstepMinInterval = 0.22f;
    [Range(0f, 1f)] public float footstepVolume = 0.5f;
    [Tooltip("Pick the procedural footstep timbre from the surface/area under the player (grass/sand/snow/rock/water). " +
             "Turn off to always use the generic Kenney footstep clips.")]
    public bool footstepSurfaceVariation = true;
    [Tooltip("Submersion depth (world units) at which grounded steps switch to a wet splash. Below this = dry land step.")]
    [Min(0f)] public float footstepWaterDepth = 0.06f;
    [Range(0f, 1f)] public float footstepWaterVolume = 0.55f;

    float _distanceSinceStep;
    float _lastFootstepTime;

    Rigidbody rb;
    CapsuleCollider cap;
    Planet _planet;
    Transform _planetCenter;
    GravityAttractor _gravityAttractor;
    CombatTargeting _combatTargeting;
    PlayerSwimStamina _stamina;
    PlanetOceanLayer _ocean;
    bool _isSwimming;
    float _nextSwimLogTime;

    /// <summary>True while the player is floating/swimming in water (rather than walking on land).</summary>
    public bool IsSwimming => _isSwimming;

    /// <summary>True while seated in a boat — locomotion is suppressed; boat owns motion.</summary>
    public bool IsInBoat => _inBoat;

    bool _inBoat;
    bool _lockOnOrbitMoveEngaged;

    public void SetInBoat(bool inBoat)
    {
        _inBoat = inBoat;
        if (inBoat)
        {
            _isSwimming = false;
            jumpQueued = false;
            AudioManager.StopFootsteps();
            if (rb != null && !rb.isKinematic)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }
    }

    bool jumpQueued;
    float lastGroundedTime;
    float jumpLockTimer;

    public void SetExternalBuffMultipliers(float speedMultiplier, float jumpMultiplier)
    {
        externalSpeedMultiplier = Mathf.Max(0.01f, speedMultiplier);
        externalJumpMultiplier = Mathf.Max(0.01f, jumpMultiplier);
    }

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        cap = GetComponent<CapsuleCollider>();

        rb.useGravity = false;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;

        ResolvePlanet();
        CacheGravityAttractor();
        ApplyJumpFeelPresetIfNeeded();
    }

    void ResolveCombatTargetingIfNeeded()
    {
        if (_combatTargeting == null)
            TryGetComponent(out _combatTargeting);
    }

    bool CanUseStaminaSprint()
    {
        if (_stamina == null)
            TryGetComponent(out _stamina);
        return _stamina == null || _stamina.CanSprint;
    }

    /// <summary>True after the last FixedUpdate used lock-on orbit strafe for planar move.</summary>
    public bool LockOnOrbitMoveEngaged => _lockOnOrbitMoveEngaged;

    void ApplyJumpFeelPresetIfNeeded()
    {
        // Upgrade older scene instances to the taller, more shooter-style jump tuning.
        if (jumpImpulse <= 5.2f)
            jumpImpulse = 6.15f;

        if (jumpRiseGravityMultiplier <= 1f || jumpRiseGravityMultiplier >= 1.3f)
            jumpRiseGravityMultiplier = 1.12f;
        if (jumpCutGravityMultiplier <= 1f || jumpCutGravityMultiplier >= 2.2f)
            jumpCutGravityMultiplier = 2.05f;
        if (fallGravityMultiplier <= 1f || fallGravityMultiplier >= 2.75f)
            fallGravityMultiplier = 2.45f;
    }

    void CacheGravityAttractor()
    {
        if (_planet == null)
            return;
        _gravityAttractor = _planet.GetComponent<GravityAttractor>();
    }

    void ResolvePlanet()
    {
        if (_planet != null && _planetCenter != null) return;

        var tagged = GameObject.FindGameObjectWithTag("Planet");
        if (tagged != null)
        {
            _planet = tagged.GetComponent<Planet>() ?? tagged.GetComponentInChildren<Planet>(true);
            if (_planet != null)
            {
                _planetCenter = _planet.transform;
                return;
            }

            _planet = tagged.GetComponentInParent<Planet>();
            if (_planet != null)
            {
                _planetCenter = _planet.transform;
                return;
            }
        }

        _planet = Object.FindFirstObjectByType<Planet>(FindObjectsInactive.Exclude);
        if (_planet != null)
            _planetCenter = _planet.transform;
    }

    void ResolveOceanIfNeeded()
    {
        if (_ocean != null) return;

        // Preferred: the PlanetOceanLayer lives on (or under) the same Planet object that provides gravity,
        // so its centre matches the gravity centre exactly. Fall back to a global search.
        if (_planet != null)
        {
            _ocean = _planet.GetComponent<PlanetOceanLayer>();
            if (_ocean == null)
                _ocean = _planet.GetComponentInChildren<PlanetOceanLayer>(true);
            if (_ocean == null && _planet.transform.parent != null)
                _ocean = _planet.transform.parent.GetComponentInChildren<PlanetOceanLayer>(true);
            if (_ocean != null)
                return;
        }

        _ocean = Object.FindFirstObjectByType<PlanetOceanLayer>(FindObjectsInactive.Exclude);
    }

    /// <summary>
    /// Enter/exit swim state from the player's submersion depth, with hysteresis so the wade/shore
    /// transition doesn't flicker: start floating once past <see cref="wadeDepthThreshold"/>, and only
    /// drop back to walking once shallower than <see cref="swimExitDepth"/>.
    /// </summary>
    void UpdateSwimState(float depth)
    {
        if (!enableSwimming || _ocean == null)
        {
            _isSwimming = false;
            return;
        }

        if (_isSwimming)
        {
            if (depth < swimExitDepth)
                _isSwimming = false;
        }
        else if (depth > wadeDepthThreshold)
        {
            _isSwimming = true;
        }
    }

    /// <summary>
    /// Spherical float-on-surface swimming. Cancels planet gravity, applies a buoyancy spring-damper
    /// toward the float line (so the body settles near the surface, never the seabed), adds water drag,
    /// and drives tangent-plane movement at <see cref="swimSpeed"/>. All vectors are radial: up is the
    /// gravity up, buoyancy is along up, and swim movement is on the tangent plane.
    /// </summary>
    void HandleSwimming(Vector3 wishDir, Vector3 up, float depth, float waveHeight)
    {
        float gravityMag = _gravityAttractor != null ? Mathf.Abs(_gravityAttractor.gravity) : 9.8f;

        // Cancel the planet gravity that GravityBody applies (Force mode, magnitude gravityMag along -up),
        // so buoyancy alone governs vertical motion while in water.
        rb.AddForce(up * gravityMag, ForceMode.Force);

        Vector3 vel = rb.linearVelocity;
        float verticalSpeed = Vector3.Dot(vel, up);

        // Depth below the ANIMATED wave surface: as the swell raises/lowers the surface above the player,
        // the equilibrium of the spring moves with it, so the player rides the crests and troughs.
        float animatedDepth = depth + waveHeight * waveFollowStrength;

        // Spring-damper toward the float line: positive depthError (too deep) pushes up; damping kills bob.
        float depthError = animatedDepth - buoyancyTargetDepth;
        float buoyancyAccel = depthError * buoyancyStrength - verticalSpeed * buoyancyDamping;
        rb.AddForce(up * buoyancyAccel, ForceMode.Acceleration);

        // General water resistance so swimming feels heavier than air.
        rb.AddForce(-vel * waterDrag, ForceMode.Acceleration);

        // Optional dive while holding Left Ctrl; the spring resurfaces you on release so you can't
        // settle on the seabed and walk underwater.
        bool diveHeld = Keyboard.current != null && Keyboard.current.leftCtrlKey.isPressed;
        if (diveHeld)
        {
            float dv = (-swimDiveSpeed) - verticalSpeed;
            rb.AddForce(up * dv, ForceMode.VelocityChange);
        }

        // Horizontal swim: drive tangent-plane velocity toward wishDir * swimSpeed (full control).
        float speedMul = Mathf.Clamp(swimStaminaSpeedMultiplier, 0.05f, 1f);
        Vector3 lateral = Vector3.ProjectOnPlane(vel, up);
        Vector3 target = wishDir * (swimSpeed * speedMul);
        rb.AddForce(target - lateral, ForceMode.VelocityChange);
    }

    /// <summary>
    /// Distance-based footsteps: accumulate horizontal travel while grounded and moving, and emit a
    /// step sound every <see cref="footstepStrideDistance"/> metres. Because cadence is tied to distance,
    /// sprinting (faster speed) naturally produces faster footsteps; idle/airborne/swimming play none.
    /// A min interval caps the rate so very high speeds can't spam the SFX.
    /// </summary>
    void UpdateFootsteps(bool grounded, float lateralSpeed, float submersionDepth)
    {
        if (!enableFootsteps)
            return;

        if (!grounded || lateralSpeed < footstepMinSpeed)
        {
            // Cut any in-flight step immediately — do not let a clip finish after the player stops.
            AudioManager.StopFootsteps();
            // Re-arm so the first step after starting to move lands almost immediately.
            _distanceSinceStep = Mathf.Max(_distanceSinceStep, footstepStrideDistance * 0.6f);
            return;
        }

        _distanceSinceStep += lateralSpeed * Time.fixedDeltaTime;
        if (_distanceSinceStep < footstepStrideDistance)
            return;

        if (Time.time - _lastFootstepTime < footstepMinInterval)
            return;

        _distanceSinceStep = 0f;
        _lastFootstepTime = Time.time;

        if (!footstepSurfaceVariation)
        {
            AudioManager.PlayFootstep(footstepVolume);
            return;
        }

        // Classify the area under our feet once per step (cheap analytic colour sample, not per-frame).
        FootstepSurfaceKind surface = ClassifyFootstepSurface(submersionDepth, out bool isWater);
        float volume = isWater ? footstepWaterVolume : footstepVolume;
        AudioManager.PlayFootstep(surface, volume);
    }

    /// <summary>
    /// Resolve the footstep surface category under the player: wading in shallow water -> Water splash,
    /// otherwise the planet's own gradient-colour classification (grass/sand/snow/rock). Falls back to
    /// Default when no planet is resolved (SfxLibrary then uses the generic Kenney clips).
    /// </summary>
    FootstepSurfaceKind ClassifyFootstepSurface(float submersionDepth, out bool isWater)
    {
        // Wading: grounded but the feet are under the ocean surface (true swimming already plays no steps).
        if (_ocean != null && submersionDepth > footstepWaterDepth)
        {
            isWater = true;
            return FootstepSurfaceKind.Water;
        }

        isWater = false;
        if (_planet != null)
            return _planet.GetFootstepSurface(transform.position);
        return FootstepSurfaceKind.Default;
    }

    static Vector3 GravityUpFromPlanet(Transform planetCenter, Vector3 worldPosition)
    {
        if (planetCenter == null) return Vector3.up;
        return (worldPosition - planetCenter.position).normalized;
    }

    void Update()
    {
        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
            jumpQueued = true;
        if (Gamepad.current != null && Gamepad.current.buttonSouth.wasPressedThisFrame)
            jumpQueued = true;
    }

    bool IsJumpHeld()
    {
        if (Keyboard.current != null && Keyboard.current.spaceKey.isPressed)
            return true;
        if (Gamepad.current != null && Gamepad.current.buttonSouth.isPressed)
            return true;
        return false;
    }

    void FixedUpdate()
    {
        ResolvePlanet();
        if (_gravityAttractor == null)
            CacheGravityAttractor();

        if (_inBoat)
        {
            // Boat owns motion; keep swim state clear so stamina regenerates.
            _isSwimming = false;
            jumpQueued = false;
            return;
        }

        Vector3 pos = transform.position;
        Vector3 gravityUp = GravityUpFromPlanet(_planetCenter, pos);
        Vector3 up = transform.up;

        Vector2 move = Vector2.zero;
        if (Keyboard.current != null)
        {
            if (Keyboard.current.wKey.isPressed) move.y += 1f;
            if (Keyboard.current.sKey.isPressed) move.y -= 1f;
            if (Keyboard.current.dKey.isPressed) move.x += 1f;
            if (Keyboard.current.aKey.isPressed) move.x -= 1f;
        }
        if (Gamepad.current != null)
        {
            Vector2 stick = Gamepad.current.leftStick.ReadValue();
            if (invertGamepadLeftStick)
                stick = -stick;
            move.x += stick.x;
            move.y += stick.y;
        }
        move = Vector2.ClampMagnitude(move, 1f);

        _lockOnOrbitMoveEngaged = false;
        ResolveCombatTargetingIfNeeded();

        bool sprintHeld =
            (Keyboard.current != null && Keyboard.current.leftShiftKey.isPressed) ||
            (Gamepad.current != null && Gamepad.current.leftStickButton.isPressed);
        bool sprintRequest = sprintHeld && move.sqrMagnitude > 0.01f;

        float sprintBuff = 1f;
        bool powerUpSprint = false;
        if (sprintRequest)
        {
            PlayerBuffController buffs = GetComponent<PlayerBuffController>();
            if (buffs != null && buffs.HasSprintActivatedSpeedBuff())
            {
                powerUpSprint = true;
                sprintBuff = buffs.GetSprintActivatedSpeedMultiplier();
                buffs.ConsumeBuffTime("PowerUp_Speed", Time.fixedDeltaTime);
            }
        }

        bool sprinting = sprintRequest && (powerUpSprint || CanUseStaminaSprint());
        float speed = moveSpeed * externalSpeedMultiplier * sprintBuff
                      * (sprinting ? sprintSpeedMultiplier : 1f);

        Vector3 camF = cameraTransform ? cameraTransform.forward : transform.forward;
        Vector3 camR = cameraTransform ? cameraTransform.right : transform.right;

        camF = Vector3.ProjectOnPlane(camF, up).normalized;
        camR = Vector3.ProjectOnPlane(camR, up).normalized;

        Vector3 wishDir = camF * move.y + camR * move.x;
        if (wishDir.sqrMagnitude > 1f) wishDir.Normalize();

        if (TryBuildLockOnOrbitWishDir(move, up, camF, ref wishDir))
            _lockOnOrbitMoveEngaged = true;

        // --- Water: float on the surface and swim, instead of walking on the seabed. ---
        ResolveOceanIfNeeded();
        // Mean (static) sea-level depth drives the swim STATE (stable across passing waves);
        // the wave height is added on top inside the buoyancy spring so the float line bobs.
        float submersionDepth = _ocean != null ? _ocean.GetDepthBelowSurface(pos) : float.NegativeInfinity;
        float waveHeight = _ocean != null ? _ocean.GetWaveHeightAtPosition(pos, Time.time) : 0f;
        UpdateSwimState(submersionDepth);

        if (debugSwim && Time.time >= _nextSwimLogTime)
        {
            _nextSwimLogTime = Time.time + 0.5f;
            Vector3 centre = _ocean != null ? _ocean.OceanCentreWorld
                : (_planetCenter != null ? _planetCenter.position : Vector3.zero);
            float distFromCentre = Vector3.Distance(pos, centre);
            float oceanRadius = _ocean != null ? _ocean.OceanSurfaceRadiusWorld : -1f;
            float shoreCalm = _ocean != null ? _ocean.GetShoreCalm01(pos) : 0f;
            // Radius the player should float at (animated surface minus the body float depth).
            float targetRadius = oceanRadius + waveHeight * waveFollowStrength - buoyancyTargetDepth;
            Debug.Log($"[Swim] oceanFound={_ocean != null} oceanRadius={oceanRadius:F2} " +
                      $"distFromCentre={distFromCentre:F3} depth={submersionDepth:F2} waveHeight={waveHeight:F3} " +
                      $"shoreCalm={shoreCalm:F2} waveFollow={waveFollowStrength:F2} targetRadius={targetRadius:F3} " +
                      $"wadeThreshold={wadeDepthThreshold:F2} enableSwimming={enableSwimming} isSwimming={_isSwimming}", this);
        }

        if (_isSwimming)
        {
            HandleSwimming(wishDir, up, submersionDepth, waveHeight);
            // No footsteps while swimming; reset so we don't emit a stale step on surfacing.
            AudioManager.StopFootsteps();
            _distanceSinceStep = 0f;
            jumpQueued = false;
            return;
        }

        float castRadius = cap.radius * 0.95f;
        Vector3 castOrigin = transform.position + up * (castRadius + 0.05f);

        bool grounded = Physics.SphereCast(
            castOrigin,
            castRadius,
            -up,
            out RaycastHit hit,
            groundCheckDistance,
            groundMask,
            QueryTriggerInteraction.Ignore
        );

        if (grounded) lastGroundedTime = Time.time;

        jumpLockTimer -= Time.fixedDeltaTime;
        if (jumpLockTimer > 0f) grounded = false;

        bool coyoteOk = (Time.time - lastGroundedTime) <= coyoteTime;

        if (!grounded)
        {
            float gravityMagnitude = _gravityAttractor != null ? Mathf.Abs(_gravityAttractor.gravity) : 9.8f;
            float verticalSpeed = Vector3.Dot(rb.linearVelocity, up);
            bool jumpHeld = IsJumpHeld();

            float gravityMultiplier = verticalSpeed > 0.05f
                ? (jumpHeld ? jumpRiseGravityMultiplier : jumpCutGravityMultiplier)
                : fallGravityMultiplier;

            float extraGravity = gravityMagnitude * Mathf.Max(0f, gravityMultiplier - 1f);
            if (extraGravity > 0f)
                rb.AddForce(-up * extraGravity, ForceMode.Acceleration);
        }

        Vector3 vel = rb.linearVelocity;
        Vector3 lateral = Vector3.ProjectOnPlane(vel, up);

        UpdateFootsteps(grounded, lateral.magnitude, submersionDepth);

        Vector3 target = wishDir * speed;
        float control = grounded ? 1f : airControl;

        Vector3 delta = (target - lateral) * control;
        rb.AddForce(delta, ForceMode.VelocityChange);

        bool allowJump = grounded || coyoteOk;

        if (jumpQueued && allowJump)
        {
            Vector3 v = rb.linearVelocity;
            float down = Vector3.Dot(v, -up);
            if (down > 0f) rb.linearVelocity = v + up * down;

            rb.AddForce(up * (jumpImpulse * externalJumpMultiplier), ForceMode.VelocityChange);

            jumpLockTimer = jumpLockTime;
        }

        jumpQueued = false;
    }

    /// <summary>
    /// When hard-locked, replace camera-relative wish with: forward = toward/away from aim in tangent plane,
    /// strafe = perpendicular (orbit). Falls back if too close or invalid.
    /// </summary>
    bool TryBuildLockOnOrbitWishDir(Vector2 move, Vector3 up, Vector3 cameraForwardFlat, ref Vector3 wishDir)
    {
        if (move.sqrMagnitude < 1e-6f)
            return false;

        if (!enableLockOnOrbitStrafe || _combatTargeting == null || !_combatTargeting.HasHardLock)
            return false;

        Camera cam = _combatTargeting.GetCombatCamera();
        if (cam == null || !_combatTargeting.TryGetLockOnCameraAimWorld(cam, out Vector3 aimWorld))
            return false;

        Vector3 pos = transform.position;
        Vector3 to = Vector3.ProjectOnPlane(aimWorld - pos, up);
        float sqr = to.sqrMagnitude;
        float minR = lockOnOrbitMinRadius;
        if (sqr < minR * minR)
            return false;

        Vector3 toward = to.normalized;
        Vector3 orbit = Vector3.Cross(up, toward);
        if (orbit.sqrMagnitude < 1e-8f)
            return false;

        orbit.Normalize();
        if (invertLockOnOrbitStrafe)
            orbit = -orbit;

        wishDir = toward * move.y + orbit * move.x;
        if (wishDir.sqrMagnitude > 1f)
            wishDir.Normalize();

        // If player is almost exactly above/below the target in view, nudge "forward" to match camera so W still feels like advancing.
        if (move.sqrMagnitude > 0.01f && cameraForwardFlat.sqrMagnitude > 1e-6f &&
            Mathf.Abs(Vector3.Dot(toward, cameraForwardFlat.normalized)) > 0.92f)
        {
            wishDir = cameraForwardFlat * move.y + orbit * move.x;
            if (wishDir.sqrMagnitude > 1f)
                wishDir.Normalize();
        }

        return true;
    }
}
