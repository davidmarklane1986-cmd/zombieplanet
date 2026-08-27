using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Spherical canoe: floats on the ocean surface and drives from WASD/stick while occupied.
/// Occupant stays unparented; seat follow is done with transform/Rigidbody snaps each FixedUpdate
/// and LateUpdate (parenting a 3D Rigidbody under the boat desyncs the character).
/// </summary>
[DefaultExecutionOrder(-5)]
[RequireComponent(typeof(Rigidbody))]
public sealed class BoatController : MonoBehaviour
{
    [Header("Drive")]
    [Min(0.5f)] public float moveSpeed = 9.5f;
    [Tooltip("How quickly the hull yaws to match look / move direction.")]
    [Min(30f)] public float alignDegreesPerSecond = 220f;
    [Min(0f)] public float waterDrag = 2.2f;
    [Tooltip("Invert gamepad stick X. DualSense/PS4 left-right is opposite WASD unless this is on.")]
    public bool invertGamepadStrafe = true;
    [Tooltip("Flip W/S and stick Y so look-relative drive matches walking.")]
    public bool invertForwardBack = false;

    [Header("Float")]
    [Tooltip("How deep the boat settles below the animated wave surface.")]
    public float buoyancyTargetDepth = 0.35f;
    public float buoyancyStrength = 22f;
    public float buoyancyDamping = 5f;
    [Range(0f, 1f)] public float waveFollowStrength = 1f;
    [Tooltip("Minimum water column (ocean radius - terrain) required to keep driving. Blocks beaching.")]
    [Min(0.05f)] public float minWaterDepth = 1.15f;
    [Tooltip("How far ahead (world units) to probe for shoreline before applying drive.")]
    [Min(0.5f)] public float shoreProbeDistance = 4.2f;

    [Header("Seat")]
    public Transform seat;
    public Transform exitPoint;
    [Tooltip("World offset from boat center used when exitPoint is null (boat local +right).")]
    public float exitSideOffset = 2.2f;

    Rigidbody _rb;
    Planet _planet;
    Transform _planetCenter;
    PlanetOceanLayer _ocean;
    Transform _occupant;
    Rigidbody _occupantRb;
    CapsuleCollider _occupantCap;
    GravityBody _occupantGravity;
    bool _occupantGravityWasEnabled;
    Collider[] _boatColliders;
    bool _wasOccupantKinematic;
    RigidbodyInterpolation _wasOccupantInterpolation;
    bool _wasOccupantDetectCollisions;

    public bool HasOccupant => _occupant != null;
    public Transform Occupant => _occupant;

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _rb.useGravity = false;
        _rb.interpolation = RigidbodyInterpolation.Interpolate;
        _rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
        _rb.constraints = RigidbodyConstraints.FreezeRotation;

        if (seat == null)
        {
            var seatGo = new GameObject("Seat");
            seatGo.transform.SetParent(transform, false);
            seatGo.transform.localPosition = new Vector3(0f, 0.55f, 0f);
            seat = seatGo.transform;
        }

        EnsureHullColliders();
        _boatColliders = GetComponentsInChildren<Collider>(true);
        ResolvePlanet();
        invertForwardBack = false;
        invertGamepadStrafe = true;
    }

    /// <summary>
    /// Kenny canoe FBX often has no colliders — add convex MeshColliders on mesh filters when missing.
    /// Root already has a CapsuleCollider fallback for buoyancy contact.
    /// </summary>
    void EnsureHullColliders()
    {
        MeshFilter[] filters = GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < filters.Length; i++)
        {
            MeshFilter mf = filters[i];
            if (mf == null || mf.sharedMesh == null)
                continue;
            // Skip Unity builtin primitives on stand-in hulls.
            if (mf.sharedMesh.name == "Capsule" || mf.sharedMesh.name == "Cube" || mf.sharedMesh.name == "Sphere")
                continue;
            MeshCollider mc = mf.GetComponent<MeshCollider>();
            if (mc == null)
                mc = mf.gameObject.AddComponent<MeshCollider>();
            mc.sharedMesh = mf.sharedMesh;
            mc.convex = true;
            mc.isTrigger = false;
        }
    }

    void ResolvePlanet()
    {
        if (_planet != null && _planetCenter != null && _ocean != null)
            return;

        _planet = Object.FindFirstObjectByType<Planet>(FindObjectsInactive.Exclude);
        if (_planet != null)
        {
            _planetCenter = _planet.transform;
            _ocean = _planet.GetComponent<PlanetOceanLayer>()
                     ?? _planet.GetComponentInChildren<PlanetOceanLayer>(true);
        }

        if (_ocean == null)
            _ocean = Object.FindFirstObjectByType<PlanetOceanLayer>(FindObjectsInactive.Exclude);
    }

    public bool TryBoard(Transform player)
    {
        if (player == null || _occupant != null)
            return false;

        var health = player.GetComponent<PlayerHealth>() ?? player.GetComponentInParent<PlayerHealth>();
        if (health == null || PlayerHealth.IsDead)
            return false;

        Transform root = health.transform;
        _occupant = root;
        _occupantRb = root.GetComponent<Rigidbody>();
        _occupantCap = root.GetComponent<CapsuleCollider>();

        var stamina = PlayerSwimStamina.EnsureOn(health);
        stamina?.SetInBoat(true);
        PlayerHealth.Died += OnOccupantDied;

        _occupantGravity = root.GetComponent<GravityBody>();
        if (_occupantGravity != null)
        {
            _occupantGravityWasEnabled = _occupantGravity.enabled;
            _occupantGravity.enabled = false;
        }

        if (_occupantRb != null)
        {
            _wasOccupantKinematic = _occupantRb.isKinematic;
            _wasOccupantInterpolation = _occupantRb.interpolation;
            _wasOccupantDetectCollisions = _occupantRb.detectCollisions;
            _occupantRb.linearVelocity = Vector3.zero;
            _occupantRb.angularVelocity = Vector3.zero;
            // 3D Rigidbody has no .simulated (that is Rigidbody2D) — use kinematic + no collisions.
            _occupantRb.isKinematic = true;
            _occupantRb.interpolation = RigidbodyInterpolation.None;
            _occupantRb.detectCollisions = false;
        }

        IgnoreOccupantCollision(true);
        SnapOccupantToSeat(alignToSeatYaw: true);
        return true;
    }

    public void Disembark()
    {
        if (_occupant == null)
            return;

        Transform root = _occupant;
        Vector3 keepFwd = root.forward;
        var health = root.GetComponent<PlayerHealth>();
        var stamina = health != null ? health.GetComponent<PlayerSwimStamina>() : null;

        Vector3 exitPos = ResolveExitWorldPosition();
        Quaternion exitRot = BuildStandRotation(exitPos, keepFwd);
        root.SetPositionAndRotation(exitPos, exitRot);

        IgnoreOccupantCollision(false);

        if (_occupantRb != null)
        {
            _occupantRb.isKinematic = _wasOccupantKinematic;
            _occupantRb.interpolation = _wasOccupantInterpolation;
            _occupantRb.detectCollisions = _wasOccupantDetectCollisions;
            _occupantRb.position = exitPos;
            _occupantRb.rotation = exitRot;
            _occupantRb.linearVelocity = Vector3.zero;
            _occupantRb.angularVelocity = Vector3.zero;
        }

        if (_occupantGravity != null)
        {
            _occupantGravity.enabled = _occupantGravityWasEnabled;
            _occupantGravity = null;
        }

        PlayerHealth.Died -= OnOccupantDied;
        stamina?.SetInBoat(false);

        _occupant = null;
        _occupantRb = null;
        _occupantCap = null;
    }

    Vector3 ResolveExitWorldPosition()
    {
        if (exitPoint != null)
            return exitPoint.position;

        Vector3 up = (_planetCenter != null)
            ? (transform.position - _planetCenter.position).normalized
            : transform.up;
        Vector3 side = Vector3.ProjectOnPlane(transform.right, up).normalized;
        if (side.sqrMagnitude < 1e-6f)
            side = Vector3.ProjectOnPlane(transform.forward, up).normalized;
        return transform.position + side * exitSideOffset + up * 0.4f;
    }

    Quaternion BuildStandRotation(Vector3 worldPos, Vector3 worldForward)
    {
        Vector3 up = (_planetCenter != null)
            ? (worldPos - _planetCenter.position).normalized
            : transform.up;
        if (up.sqrMagnitude < 1e-8f)
            up = Vector3.up;
        up.Normalize();

        Vector3 fwd = Vector3.ProjectOnPlane(worldForward, up);
        if (fwd.sqrMagnitude < 1e-6f)
            fwd = Vector3.ProjectOnPlane(worldForward, Vector3.up);
        if (fwd.sqrMagnitude < 1e-6f)
            fwd = Vector3.ProjectOnPlane(Vector3.forward, up);
        if (fwd.sqrMagnitude < 1e-6f)
            fwd = Vector3.ProjectOnPlane(Vector3.right, up);
        return Quaternion.LookRotation(fwd.normalized, up);
    }

    void IgnoreOccupantCollision(bool ignore)
    {
        if (_occupantCap == null || _boatColliders == null)
            return;
        for (int i = 0; i < _boatColliders.Length; i++)
        {
            if (_boatColliders[i] != null)
                Physics.IgnoreCollision(_occupantCap, _boatColliders[i], ignore);
        }
    }

    void FixedUpdate()
    {
        ResolvePlanet();
        if (_planetCenter == null || _ocean == null)
            return;

        Vector3 pos = transform.position;
        Vector3 up = (pos - _planetCenter.position).normalized;
        float depth = _ocean.GetDepthBelowSurface(pos);
        float wave = _ocean.GetWaveHeightAtPosition(pos, Time.time);

        // Align upright to planet radial.
        Quaternion upright = Quaternion.FromToRotation(transform.up, up) * transform.rotation;
        _rb.MoveRotation(Quaternion.Slerp(transform.rotation, upright, 12f * Time.fixedDeltaTime));

        // No GravityBody on boats — buoyancy spring alone holds sea level (do not cancel a force that was never applied).
        Vector3 vel = _rb.linearVelocity;
        float verticalSpeed = Vector3.Dot(vel, up);
        float animatedDepth = depth + wave * waveFollowStrength;
        float depthError = animatedDepth - buoyancyTargetDepth;
        float buoyancyAccel = depthError * buoyancyStrength - verticalSpeed * buoyancyDamping;
        _rb.AddForce(up * buoyancyAccel, ForceMode.Acceleration);
        _rb.AddForce(-vel * waterDrag, ForceMode.Acceleration);

        PushOffShore(pos, up);

        if (!HasOccupant)
        {
            Vector3 lateralIdle = Vector3.ProjectOnPlane(_rb.linearVelocity, up);
            if (!IsNavigableWater(pos + lateralIdle.normalized * shoreProbeDistance * 0.5f))
                _rb.AddForce(-lateralIdle, ForceMode.VelocityChange);
            else
                _rb.AddForce(-lateralIdle * 1.5f, ForceMode.Acceleration);
            return;
        }

        // Mouse look aims; WASD/stick travel in look space (camera-relative on the water plane).
        Vector2 move = ReadMove();
        if (invertForwardBack)
            move.y = -move.y;
        Vector3 lookFwd = ResolveOccupantLookForward(up);
        Vector3 lookRight = Vector3.Cross(up, lookFwd).normalized;
        if (lookRight.sqrMagnitude < 1e-6f)
            lookRight = Vector3.ProjectOnPlane(transform.right, up).normalized;

        Vector3 wish = lookFwd * move.y + lookRight * move.x;
        if (wish.sqrMagnitude > 1f)
            wish.Normalize();

        wish = ClampWishToWater(pos, up, wish);

        Vector3 faceDir = wish.sqrMagnitude > 0.04f ? wish : lookFwd;
        if (faceDir.sqrMagnitude > 1e-6f)
        {
            Quaternion want = Quaternion.LookRotation(faceDir.normalized, up);
            _rb.MoveRotation(Quaternion.RotateTowards(
                transform.rotation, want, alignDegreesPerSecond * Time.fixedDeltaTime));
        }

        Vector3 lateral = Vector3.ProjectOnPlane(_rb.linearVelocity, up);
        if (!IsNavigableWater(pos + lateral.normalized * shoreProbeDistance * 0.5f))
        {
            _rb.AddForce(-lateral, ForceMode.VelocityChange);
            lateral = Vector3.zero;
        }

        Vector3 target = wish * moveSpeed;
        _rb.AddForce(target - lateral, ForceMode.VelocityChange);
        SnapOccupantPositionToSeat();
    }

    void PushOffShore(Vector3 pos, Vector3 up)
    {
        float depth = WaterColumn(pos);
        if (depth >= minWaterDepth)
            return;

        Vector3 waterward = FindDeeperWaterDirection(pos, up);
        if (waterward.sqrMagnitude < 1e-6f)
            return;

        Vector3 lateral = Vector3.ProjectOnPlane(_rb.linearVelocity, up);
        float intoLand = Vector3.Dot(lateral, -waterward);
        if (intoLand > 0f)
            _rb.AddForce(waterward * intoLand, ForceMode.VelocityChange);

        float push = (minWaterDepth - depth) * 2.5f;
        _rb.AddForce(waterward * Mathf.Max(push, 1.2f), ForceMode.VelocityChange);
        _rb.MovePosition(pos + waterward * Mathf.Min(0.35f, minWaterDepth - depth));
    }

    Vector3 FindDeeperWaterDirection(Vector3 pos, Vector3 up)
    {
        Vector3 t1 = Vector3.Cross(up, Vector3.up);
        if (t1.sqrMagnitude < 1e-6f)
            t1 = Vector3.Cross(up, Vector3.right);
        t1.Normalize();
        Vector3 t2 = Vector3.Cross(up, t1);

        float best = WaterColumn(pos);
        Vector3 bestDir = Vector3.zero;
        const int dirs = 8;
        for (int i = 0; i < dirs; i++)
        {
            float a = (Mathf.PI * 2f * i) / dirs;
            Vector3 dir = (t1 * Mathf.Cos(a) + t2 * Mathf.Sin(a)).normalized;
            float d = WaterColumn(pos + dir * shoreProbeDistance);
            if (d > best)
            {
                best = d;
                bestDir = dir;
            }
        }

        return bestDir;
    }

    Vector3 ClampWishToWater(Vector3 pos, Vector3 up, Vector3 wish)
    {
        if (wish.sqrMagnitude < 1e-6f)
            return Vector3.zero;

        Vector3 probe = pos + wish.normalized * shoreProbeDistance;
        if (IsNavigableWater(probe))
            return wish;

        // Slide along the shoreline: drop the landward component.
        Vector3 tangent = Vector3.Cross(up, wish.normalized);
        if (tangent.sqrMagnitude < 1e-6f)
            return Vector3.zero;
        tangent.Normalize();

        Vector3 slideA = tangent * Vector3.Dot(wish, tangent);
        Vector3 slideB = -slideA;
        bool aOk = IsNavigableWater(pos + slideA.normalized * shoreProbeDistance);
        bool bOk = IsNavigableWater(pos + slideB.normalized * shoreProbeDistance);
        if (aOk && !bOk)
            return slideA;
        if (bOk && !aOk)
            return slideB;
        if (aOk && bOk)
            return Vector3.Dot(wish, tangent) >= 0f ? slideA : slideB;
        return Vector3.zero;
    }

    bool IsNavigableWater(Vector3 worldPos)
    {
        return WaterColumn(worldPos) >= minWaterDepth;
    }

    float WaterColumn(Vector3 worldPos)
    {
        if (_ocean == null || _planet == null || _planetCenter == null)
            return minWaterDepth;

        Vector3 axis = worldPos - _planetCenter.position;
        if (axis.sqrMagnitude < 1e-8f)
            return -1f;
        axis.Normalize();

        float oceanR = _ocean.OceanSurfaceRadiusWorld;
        float terrainR = _planet.GetSurfaceRadiusWorld(axis);
        return oceanR - terrainR;
    }

    Vector3 ResolveOccupantLookForward(Vector3 up)
    {
        if (_occupant == null)
            return Vector3.ProjectOnPlane(transform.forward, up).normalized;

        var motor = _occupant.GetComponent<PlanetMotor_InputSystem>();
        Transform cam = motor != null ? motor.cameraTransform : null;
        Vector3 raw = cam != null ? cam.forward : _occupant.forward;
        Vector3 fwd = Vector3.ProjectOnPlane(raw, up);
        if (fwd.sqrMagnitude < 1e-6f)
            fwd = Vector3.ProjectOnPlane(_occupant.forward, up);
        if (fwd.sqrMagnitude < 1e-6f)
            fwd = Vector3.ProjectOnPlane(transform.forward, up);
        return fwd.normalized;
    }

    void LateUpdate()
    {
        // After look/camera: keep root on the seat without fighting mouse yaw.
        if (HasOccupant)
            SnapOccupantPositionToSeat();
    }

    /// <summary>Board pose — same radial stand as ongoing seat follow.</summary>
    void SnapOccupantToSeat(bool alignToSeatYaw)
    {
        SnapOccupantPositionToSeat();
    }

    /// <summary>Seat the player on the hull and stand them on planet radial (LookRotation, not accumulated FromTo).</summary>
    void SnapOccupantPositionToSeat()
    {
        if (_occupant == null || seat == null)
            return;

        Vector3 pos = seat.position;
        Vector3 planetUp = (_planetCenter != null)
            ? (pos - _planetCenter.position).normalized
            : seat.up;
        if (planetUp.sqrMagnitude < 1e-8f)
            planetUp = Vector3.up;
        planetUp.Normalize();

        Vector3 fwd = Vector3.ProjectOnPlane(_occupant.forward, planetUp);
        if (fwd.sqrMagnitude < 1e-6f)
            fwd = Vector3.ProjectOnPlane(seat.forward, planetUp);
        if (fwd.sqrMagnitude < 1e-6f)
            fwd = Vector3.ProjectOnPlane(Vector3.forward, planetUp);
        if (fwd.sqrMagnitude < 1e-6f)
            fwd = Vector3.ProjectOnPlane(Vector3.right, planetUp);
        Quaternion rot = Quaternion.LookRotation(fwd.normalized, planetUp);

        _occupant.SetPositionAndRotation(pos, rot);
        if (_occupantRb != null)
        {
            _occupantRb.position = pos;
            _occupantRb.rotation = rot;
            if (_occupantRb.isKinematic)
            {
                _occupantRb.MovePosition(pos);
                _occupantRb.MoveRotation(rot);
            }
        }
    }

    Vector2 ReadMove()
    {
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
            if (invertGamepadStrafe)
                stick.x = -stick.x;
            move.x += stick.x;
            move.y += stick.y;
        }
        return Vector2.ClampMagnitude(move, 1f);
    }

    void OnOccupantDied(PlayerHealth _)
    {
        if (_occupant != null)
            Disembark();
    }

    void OnDisable()
    {
        if (_occupant != null)
            Disembark();
    }
}
