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

    [Header("Swim — PlanetWaterLayer (surface bob + tangent steer, Stargrave 1.3)")]
    [Tooltip("If set, used instead of auto-find on Planet.")]
    public PlanetWaterLayer waterLayerOverride;
    [Tooltip("When a PlanetWaterLayer exists on the planet and water is enabled, uses shoreline hysteresis + radial spring + bob.")]
    public bool enablePlanetWaterLayerSwim = true;
    public float swimMoveSpeed = 3.5f;
    [Tooltip("Capsule center on water shell (half in / half out). Positive = deeper toward core (world units).")]
    public float swimSurfaceOffset = 0f;
    public float bobAmplitude = 0.12f;
    public float bobFrequency = 1.1f;
    public float swimSurfaceSpring = 16f;
    public float swimRadialDamping = 5f;
    [Range(0f, 1f)]
    [Tooltip("Reduces inward gravity from GravityAttractor while surface swimming.")]
    public float swimGravityScale = 0.12f;
    [Tooltip("Swim when capsule center is within this distance outside the water shell.")]
    public float swimZonePadding = 0.55f;
    [Min(0f)]
    public float swimZoneExitBuffer = 0.55f;
    [Tooltip("No capsule: radial offset from water shell for approximate half-body.")]
    public float swimFootDepthFallback = 0.85f;
    public float swimSteerAcceleration = 42f;
    public float swimTangentialDrag = 14f;

    [Header("Swim — legacy (Planet.GetWaterRadiusWorld only, no PlanetWaterLayer)")]
    [Tooltip("If false, disables ocean swim. When true without PlanetWaterLayer, uses buoyancy below analytical water radius.")]
    public bool enableSwim = true;
    public float waterMargin = 0.5f;
    public float swimBuoyancyAcceleration = 15f;
    public float swimSinkAcceleration = 0f;
    [Range(0.15f, 1.25f)]
    public float swimSpeedMultiplier = 0.75f;
    [Range(0.15f, 1f)]
    public float swimMoveControl = 0.85f;

    Rigidbody rb;
    CapsuleCollider cap;
    Planet _planet;
    Transform _planetCenter;
    PlanetWaterLayer _waterLayer;
    GravityAttractor _gravityAttractor;
    CombatTargeting _combatTargeting;

    bool _surfaceSwimming;
    bool _lockOnOrbitMoveEngaged;

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
        ResolveWaterLayer();
        CacheGravityAttractor();
        ApplyJumpFeelPresetIfNeeded();
    }

    void ResolveCombatTargetingIfNeeded()
    {
        if (_combatTargeting == null)
            TryGetComponent(out _combatTargeting);
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

    void ResolveWaterLayer()
    {
        if (waterLayerOverride != null)
        {
            _waterLayer = waterLayerOverride;
            return;
        }

        if (_planet == null)
        {
            _waterLayer = null;
            return;
        }

        _waterLayer = _planet.GetComponent<PlanetWaterLayer>() ?? _planet.GetComponentInChildren<PlanetWaterLayer>(true);
    }

    bool IsInWaterLegacy(Vector3 worldPosition)
    {
        if (!enableSwim || _planet == null || _planetCenter == null) return false;
        float waterR = _planet.GetWaterRadiusWorld();
        if (waterR <= 0f) return false;
        float dist = Vector3.Distance(worldPosition, _planetCenter.position);
        return dist < waterR + waterMargin;
    }

    static Vector3 GravityUpFromPlanet(Transform planetCenter, Vector3 worldPosition)
    {
        if (planetCenter == null) return Vector3.up;
        return (worldPosition - planetCenter.position).normalized;
    }

    void ApplySwimSurfaceBobbing()
    {
        if (_waterLayer == null || _planetCenter == null) return;

        Vector3 waterCenter = _waterLayer.GetWaterShellWorldCenter();
        Vector3 foot = rb.position;
        Vector3 fromWater = foot - waterCenter;
        if (fromWater.sqrMagnitude < 1e-8f)
            return;

        Vector3 radial = fromWater.normalized;
        float footR = Vector3.Dot(foot - waterCenter, radial);

        float k;
        if (cap != null)
        {
            Vector3 capWorld = cap.transform.TransformPoint(cap.center);
            float capR = Vector3.Dot(capWorld - waterCenter, radial);
            k = capR - footR;
        }
        else
            k = swimFootDepthFallback;

        float shell = _waterLayer.GetWorldWaterShellRadius();
        float bob = Mathf.Sin(Time.fixedTime * bobFrequency * Mathf.PI * 2f) * bobAmplitude;
        float targetFootR = shell - k + swimSurfaceOffset + bob;

        float radialError = footR - targetFootR;
        float radialSpeed = Vector3.Dot(rb.linearVelocity, radial);
        Vector3 spring = -radial * (radialError * swimSurfaceSpring + radialSpeed * swimRadialDamping);
        rb.AddForce(spring, ForceMode.Acceleration);
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
        ResolveWaterLayer();

        Vector3 pos = transform.position;
        Vector3 gravityUp = GravityUpFromPlanet(_planetCenter, pos);
        Vector3 up = transform.up;

        bool planetHasWater = _planet != null && _planet.colourSettings != null && _planet.colourSettings.useWater && _planet.GetWaterRadiusWorld() > 0f;
        bool useWaterLayerSwim = enableSwim && enablePlanetWaterLayerSwim && _waterLayer != null && planetHasWater;

        PlayerPlanetSwimStateUtil.SwimGroundState layerState = default;
        if (useWaterLayerSwim)
        {
            layerState = PlayerPlanetSwimStateUtil.Resolve(
                transform,
                rb.position,
                _planetCenter,
                _waterLayer,
                cap,
                _surfaceSwimming,
                swimZonePadding,
                swimZoneExitBuffer,
                1.2f);
            _surfaceSwimming = layerState.Swimming;

            if (_surfaceSwimming)
            {
                ApplySwimSurfaceBobbing();
                if (_gravityAttractor != null && swimGravityScale < 1f)
                    rb.AddForce(gravityUp * (-_gravityAttractor.gravity) * (1f - swimGravityScale), ForceMode.Acceleration);
            }
        }
        else
            _surfaceSwimming = false;

        bool inWaterLegacy = enableSwim && !useWaterLayerSwim && IsInWaterLegacy(pos);
        if (inWaterLegacy && swimBuoyancyAcceleration > 0f)
            rb.AddForce(gravityUp * swimBuoyancyAcceleration, ForceMode.Acceleration);
        if (inWaterLegacy && swimSinkAcceleration > 0f)
            rb.AddForce(-gravityUp * swimSinkAcceleration, ForceMode.Acceleration);

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

        float swimMult = (_surfaceSwimming || inWaterLegacy) ? swimSpeedMultiplier : 1f;
        float speed = moveSpeed * externalSpeedMultiplier * swimMult * (sprintHeld && move.sqrMagnitude > 0.01f ? sprintSpeedMultiplier : 1f);

        Vector3 camF = cameraTransform ? cameraTransform.forward : transform.forward;
        Vector3 camR = cameraTransform ? cameraTransform.right : transform.right;

        camF = Vector3.ProjectOnPlane(camF, up).normalized;
        camR = Vector3.ProjectOnPlane(camR, up).normalized;

        Vector3 wishDir = camF * move.y + camR * move.x;
        if (wishDir.sqrMagnitude > 1f) wishDir.Normalize();

        if (TryBuildLockOnOrbitWishDir(move, up, camF, ref wishDir))
            _lockOnOrbitMoveEngaged = true;

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

        if (!grounded && !inWaterLegacy && !(useWaterLayerSwim && _surfaceSwimming))
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

        if (useWaterLayerSwim && _surfaceSwimming)
        {
            Vector3 radialOut = gravityUp;
            float inputMag = Mathf.Clamp01(move.magnitude);
            Vector3 tangentDir = Vector3.ProjectOnPlane(wishDir, radialOut);
            float tangentMag = tangentDir.magnitude;
            if (tangentMag > 1e-5f)
                tangentDir /= tangentMag;

            Vector3 v = rb.linearVelocity;
            float vAlongRadial = Vector3.Dot(v, radialOut);
            Vector3 vRadial = radialOut * vAlongRadial;
            Vector3 vTan = v - vRadial;

            float sSpeed = swimMoveSpeed * externalSpeedMultiplier * (sprintHeld && inputMag > 0.01f ? sprintSpeedMultiplier : 1f);
            Vector3 wishTangent = tangentDir * (sSpeed * inputMag);
            float maxDv = swimSteerAcceleration * Time.fixedDeltaTime;

            if (inputMag > 0.01f)
            {
                Vector3 dv = wishTangent - vTan;
                if (dv.magnitude > maxDv)
                    dv = dv.normalized * maxDv;
                rb.AddForce(dv, ForceMode.VelocityChange);
            }
            else
            {
                Vector3 damp = Vector3.ClampMagnitude(-vTan, swimTangentialDrag * Time.fixedDeltaTime);
                rb.AddForce(damp, ForceMode.VelocityChange);
            }

            jumpQueued = false;
            return;
        }

        Vector3 vel = rb.linearVelocity;
        Vector3 lateral = Vector3.ProjectOnPlane(vel, up);

        Vector3 target = wishDir * speed;
        float control = grounded ? 1f : (inWaterLegacy ? swimMoveControl : airControl);

        Vector3 delta = (target - lateral) * control;
        rb.AddForce(delta, ForceMode.VelocityChange);

        bool allowJump = useWaterLayerSwim
            ? (layerState.Grounded && !_surfaceSwimming)
            : (grounded || coyoteOk);

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
