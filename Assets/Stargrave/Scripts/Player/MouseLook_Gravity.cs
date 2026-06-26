using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Cinemachine;

/// <summary>
/// Runs <b>before</b> <see cref="PlayerLookController"/> (script order) to set <see cref="IsAimingDownSights"/> and smooth ADS FOV
/// on the Cinemachine camera. Actual yaw/pitch live in <see cref="PlayerLookController"/>. Assign PlanetMotor.cameraTransform to Camera Target.
/// Optional ADS priority and 1P/3P follow morph are implemented here (no separate component type) so a single script always compiles.
/// </summary>
[DefaultExecutionOrder(-20)]
public class MouseLook_Gravity : MonoBehaviour
{
    [Header("References")]
    public Transform cameraTarget;
    public Camera gameplayCamera;
    [Tooltip("CM_Player — for ADS lens + optional camera stack; auto-found when Tracking Target == Camera Target.")]
    public CinemachineCamera cinemachineCamera;

    [Header("ADS")]
    public bool adsEnabled = true;
    public float adsFieldOfView = 48f;
    [Range(0.15f, 1f)]
    public float adsLookSensitivityMultiplier = 0.62f;
    public float adsZoomSmoothing = 14f;
    [Range(0.05f, 0.95f)]
    public float adsTriggerThreshold = 0.35f;
    public bool adsHoldLeftAlt = true;

    [Header("Camera Stability")]
    [Tooltip("If the vcam LookAt is the player body and RotationComposer has a body offset, aim fights the rig (crosshair drifts off targets). This aligns CM to the same pivot as tracking and clears composer offset for hip.")]
    public bool alignCinemachineAimWithPivotOnStart = true;
    [Tooltip("Reduce Cinemachine lag so the camera feels more planted while moving.")]
    public bool applySteadyCameraPreset = true;
    [Range(0f, 1f)] public float steadyComposerDamping = 0.05f;
    [Range(0f, 2f)] public float steadyFollowPositionDamping = 0.1f;
    [Range(0f, 2f)] public float steadyFollowRotationDamping = 0.1f;
    [Range(0f, 2f)] public float steadyFollowQuaternionDamping = 0.1f;
    [Range(0f, 1f)] public float steadyDecollisionDamping = 0.05f;
    [Range(0f, 1f)] public float steadyTerrainDamping = 0.02f;

    [Header("Combat camera stack (on vcam)")]
    [Tooltip("Extra vcam priority while ADS (0 = leave priority unchanged).")]
    public int adsPriorityBoost = 0;
    [Header("First / third person (single vcam morph)")]
    [Tooltip("Gamepad: Triangle (△ on PlayStation) / Y (Xbox). Keyboard: T. Requires a Cinemachine vcam following Camera Target.")]
    public bool enableFirstPersonToggle = true;
    public float firstPersonOffsetLerpSpeed = 10f;
    [Tooltip("Follow offset when in first person (local space of follow target).")]
    public Vector3 firstPersonFollowOffset = new Vector3(0f, 1.52f, 0.22f);
    [Tooltip("Rotation composer target offset in first person.")]
    public Vector3 firstPersonTargetOffset = new Vector3(0f, 1.45f, 0.05f);

    [Header("Options")]
    public bool lockCursor = true;

    float _hipFieldOfView = 60f;
    Camera _resolvedCamera;
    bool _hipFovCaptured;

    bool _aimingThisFrame;

    public bool IsAimingDownSights { get; private set; }

    bool _steadyPresetApplied;
    bool _aimPivotAligned;

    CinemachineFollow _stackFollow;
    CinemachineRotationComposer _stackComposer;
    Vector3 _stackCapturedFollowOffset;
    Vector3 _stackCapturedTargetOffset;
    int _stackBasePriority;
    bool _stackCaptured;
    bool _firstPerson;

    void Awake()
    {
        _resolvedCamera = gameplayCamera;
        if (_resolvedCamera == null && cameraTarget != null)
            _resolvedCamera = cameraTarget.GetComponentInChildren<Camera>(true);
        if (_resolvedCamera == null)
            _resolvedCamera = GetComponentInChildren<Camera>(true);
        EnsurePlayerLookController();
    }

    void EnsurePlayerLookController()
    {
        if (cameraTarget == null)
            return;
        if (GetComponent<PlayerLookController>() != null)
            return;
        var pl = gameObject.AddComponent<PlayerLookController>();
        pl.cameraTarget = cameraTarget;
        pl.mouseLook = this;
        pl.gameplayCamera = gameplayCamera;
    }

    void Start()
    {
        if (lockCursor)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        TryBindCinemachineVcam();
        ApplySteadyCameraPresetIfNeeded();
        CaptureHipFieldOfView();
        CaptureCombatStackBaseline();
    }

    void CaptureCombatStackBaseline()
    {
        if (_stackCaptured || cinemachineCamera == null)
            return;
        _stackFollow = cinemachineCamera.GetComponent<CinemachineFollow>();
        _stackComposer = cinemachineCamera.GetComponent<CinemachineRotationComposer>();
        if (_stackFollow == null)
            return;
        _stackCapturedFollowOffset = _stackFollow.FollowOffset;
        if (_stackComposer != null)
            _stackCapturedTargetOffset = _stackComposer.TargetOffset;
        _stackBasePriority = cinemachineCamera.Priority.Value;
        _stackCaptured = true;
    }

    void SetAdsPriority(bool ads)
    {
        if (cinemachineCamera == null || adsPriorityBoost == 0)
            return;
        CaptureCombatStackBaseline();
        if (!_stackCaptured)
            return;
        var p = cinemachineCamera.Priority;
        p.Value = _stackBasePriority + (ads ? adsPriorityBoost : 0);
        cinemachineCamera.Priority = p;
    }

    void UpdateFirstPersonMorph()
    {
        if (!enableFirstPersonToggle || !Application.isPlaying)
            return;

        bool toggle = false;
        if (Keyboard.current != null && Keyboard.current.tKey.wasPressedThisFrame)
            toggle = true;
        // North = Triangle (DualSense/DS4) / Y (Xbox).
        if (Gamepad.current != null && Gamepad.current.buttonNorth.wasPressedThisFrame)
            toggle = true;

        if (toggle)
            _firstPerson = !_firstPerson;

        if (cinemachineCamera == null)
            return;

        CaptureCombatStackBaseline();
        if (_stackFollow == null)
            return;

        float dt = Time.unscaledDeltaTime;
        float t = 1f - Mathf.Exp(-firstPersonOffsetLerpSpeed * dt);

        Vector3 targetFollow = _firstPerson ? firstPersonFollowOffset : _stackCapturedFollowOffset;
        _stackFollow.FollowOffset = Vector3.Lerp(_stackFollow.FollowOffset, targetFollow, t);

        if (_stackComposer != null)
        {
            Vector3 targetComposer = _firstPerson ? firstPersonTargetOffset : _stackCapturedTargetOffset;
            _stackComposer.TargetOffset = Vector3.Lerp(_stackComposer.TargetOffset, targetComposer, t);
        }
    }

    void TryBindCinemachineVcam()
    {
        if (cameraTarget == null)
            return;

        if (cinemachineCamera == null)
        {
            var vcams = FindObjectsByType<CinemachineCamera>(FindObjectsSortMode.None);
            foreach (var v in vcams)
            {
                if (v != null && v.Target.TrackingTarget == cameraTarget)
                {
                    cinemachineCamera = v;
                    break;
                }
            }
        }

        if (cinemachineCamera != null)
        {
            if (alignCinemachineAimWithPivotOnStart && (!_aimPivotAligned || IsCinemachineAimMisaligned()))
                AlignCinemachineAimWithCameraPivot();
            _aimPivotAligned = true;
        }
    }

    void AlignCinemachineAimWithCameraPivot()
    {
        if (!alignCinemachineAimWithPivotOnStart || cinemachineCamera == null || cameraTarget == null)
            return;

        var t = cinemachineCamera.Target;
        if (t.TrackingTarget != cameraTarget)
            return;

        t.LookAtTarget = cameraTarget;
        t.CustomLookAtTarget = false;
        cinemachineCamera.Target = t;

        var composer = cinemachineCamera.GetComponent<CinemachineRotationComposer>();
        if (composer != null)
        {
            composer.TargetOffset = Vector3.zero;
            composer.CenterOnActivate = false;
            var c = composer.Composition;
            c.ScreenPosition = Vector2.zero;
            composer.Composition = c;
        }
    }

    bool IsCinemachineAimMisaligned()
    {
        if (cinemachineCamera == null || cameraTarget == null)
            return false;

        var t = cinemachineCamera.Target;
        if (t.TrackingTarget != cameraTarget)
            return false;
        if (t.LookAtTarget != cameraTarget || t.CustomLookAtTarget)
            return true;

        var composer = cinemachineCamera.GetComponent<CinemachineRotationComposer>();
        return composer != null && composer.TargetOffset.sqrMagnitude > 0.0001f;
    }

    void ApplySteadyCameraPresetIfNeeded()
    {
        if (!applySteadyCameraPreset || _steadyPresetApplied || cinemachineCamera == null)
            return;

        var composer = cinemachineCamera.GetComponent<CinemachineRotationComposer>();
        if (composer != null)
            composer.Damping = new Vector2(steadyComposerDamping, steadyComposerDamping);

        var follow = cinemachineCamera.GetComponent<CinemachineFollow>();
        if (follow != null)
        {
            var tracker = follow.TrackerSettings;
            tracker.PositionDamping = new Vector3(
                steadyFollowPositionDamping,
                steadyFollowPositionDamping,
                steadyFollowPositionDamping);
            tracker.RotationDamping = new Vector3(
                steadyFollowRotationDamping,
                steadyFollowRotationDamping,
                steadyFollowRotationDamping);
            tracker.QuaternionDamping = steadyFollowQuaternionDamping;
            follow.TrackerSettings = tracker;
        }

        var decollider = cinemachineCamera.GetComponent<CinemachineDecollider>();
        if (decollider != null)
        {
            var decollision = decollider.Decollision;
            decollision.Damping = steadyDecollisionDamping;
            decollider.Decollision = decollision;

            var terrain = decollider.TerrainResolution;
            terrain.Damping = steadyTerrainDamping;
            decollider.TerrainResolution = terrain;
        }

        _steadyPresetApplied = true;
    }

    void CaptureHipFieldOfView()
    {
        if (_hipFovCaptured) return;

        TryBindCinemachineVcam();

        if (cinemachineCamera != null)
        {
            _hipFieldOfView = cinemachineCamera.Lens.FieldOfView;
            _hipFovCaptured = true;
            return;
        }

        ResolveCameraIfNeeded();
        if (_resolvedCamera != null)
        {
            _hipFieldOfView = _resolvedCamera.fieldOfView;
            _hipFovCaptured = true;
        }
    }

    void ResolveCameraIfNeeded()
    {
        if (_resolvedCamera != null) return;
        _resolvedCamera = gameplayCamera;
        if (_resolvedCamera == null && cameraTarget != null)
            _resolvedCamera = cameraTarget.GetComponentInChildren<Camera>(true);
        if (_resolvedCamera == null)
            _resolvedCamera = GetComponentInChildren<Camera>(true);
        if (_resolvedCamera == null && Camera.main != null)
            _resolvedCamera = Camera.main;

        if (_resolvedCamera != null && !_hipFovCaptured)
        {
            _hipFieldOfView = _resolvedCamera.fieldOfView;
            _hipFovCaptured = true;
        }
    }

    bool ReadWantsAim()
    {
        if (!adsEnabled) return false;
        if (Mouse.current != null && Mouse.current.rightButton.isPressed)
            return true;
        if (adsHoldLeftAlt && Keyboard.current != null && Keyboard.current.leftAltKey.isPressed)
            return true;
        if (Gamepad.current != null && Gamepad.current.leftTrigger.ReadValue() >= adsTriggerThreshold)
            return true;
        return false;
    }

    void Update()
    {
        TryBindCinemachineVcam();
        ApplySteadyCameraPresetIfNeeded();
        CaptureHipFieldOfView();
        CaptureCombatStackBaseline();

        _aimingThisFrame = ReadWantsAim();
        IsAimingDownSights = _aimingThisFrame;

        SetAdsPriority(_aimingThisFrame);
        UpdateFirstPersonMorph();

        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        if (Gamepad.current != null && Gamepad.current.startButton.wasPressedThisFrame)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    void LateUpdate()
    {
        if (!adsEnabled) return;

        TryBindCinemachineVcam();
        ApplySteadyCameraPresetIfNeeded();
        CaptureHipFieldOfView();
        ResolveCameraIfNeeded();

        float targetFov = _aimingThisFrame ? adsFieldOfView : _hipFieldOfView;
        float t = 1f - Mathf.Exp(-adsZoomSmoothing * Time.unscaledDeltaTime);

        if (cinemachineCamera != null)
        {
            var lens = cinemachineCamera.Lens;
            lens.FieldOfView = Mathf.Lerp(lens.FieldOfView, targetFov, t);
            cinemachineCamera.Lens = lens;
            return;
        }

        if (_resolvedCamera != null)
            _resolvedCamera.fieldOfView = Mathf.Lerp(_resolvedCamera.fieldOfView, targetFov, t);
    }
}

/// <summary>
/// Drives player yaw (rigidbody) and camera-target pitch for planet gravity. Kept in this file so the type always
/// resolves with <see cref="MouseLook_Gravity"/> in the same assembly compile pass.
/// </summary>
[DefaultExecutionOrder(100)]
[RequireComponent(typeof(Rigidbody))]
public class PlayerLookController : MonoBehaviour
{
    [Header("References")]
    public Transform cameraTarget;
    [Tooltip("Optional; uses Main Camera if unset.")]
    public Camera gameplayCamera;
    [Tooltip("ADS / lens helper on this object; optional but recommended.")]
    public MouseLook_Gravity mouseLook;

    [Header("Sensitivity")]
    public float sensitivityX = 180f;
    public float sensitivityY = 140f;
    [Header("Gamepad")]
    public float gamepadSensitivityX = 220f;
    public float gamepadSensitivityY = 180f;
    [Tooltip("Flip only right-stick horizontal look.")]
    public bool invertGamepadLookX = false;

    [Header("Lock-On Turn")]
    public float lockOnYawDegreesPerSecond = 280f;
    public float lockOnPitchDegreesPerSecond = 200f;

    [Header("Lock-On Screen Centering")]
    [Tooltip("While hard-lock is active, steer yaw (FixedUpdate) and pitch (LateUpdate) so the lock aim point reaches the screen center.")]
    public bool lockOnUseViewportCentering = false;
    [Tooltip("How fast pitch chases viewport Y error (degrees per second at full 0.5 viewport offset).")]
    public float lockOnViewportPitchDegreesPerSecond = 160f;
    [Tooltip("How fast yaw chases viewport X error (degrees per second at full 0.5 offset).")]
    public float lockOnViewportYawDegreesPerSecond = 140f;
    [Tooltip("Ignore sub-pixel viewport error to reduce jitter when nearly centered.")]
    public float lockOnViewportDeadZone = 0.02f;
    [Tooltip("Seconds of recent manual look input before lock-on stops steering the camera.")]
    public float lockOnManualInputGraceSeconds = 0.18f;

    [Header("Pitch Clamp")]
    public float minPitch = -55f;
    public float maxPitch = 65f;
    [Tooltip("Tighter vertical limit while firing so third-person aim stays readable.")]
    public bool tightenPitchWhileFiring = true;
    public float firingMinPitch = -42f;
    public float firingMaxPitch = 52f;

    [Header("Options")]
    public bool invertY = false;

    CombatTargeting _targeting;
    Rigidbody _rb;
    float _pitch;
    float _pendingYawFromMouse;
    float _manualLookInputTimer;

    float EffectiveMinPitch => tightenPitchWhileFiring && IsFiring() ? firingMinPitch : minPitch;
    float EffectiveMaxPitch => tightenPitchWhileFiring && IsFiring() ? firingMaxPitch : maxPitch;

    bool IsManualLookActive()
    {
        return _manualLookInputTimer > 0f;
    }

    static bool IsFiring()
    {
        if (Mouse.current != null && Mouse.current.leftButton.isPressed)
            return true;
        if (Gamepad.current != null && Gamepad.current.rightTrigger.ReadValue() >= 0.35f)
            return true;
        return false;
    }

    void RegisterManualLookInput(Vector2 mouseDelta, Vector2 gamepadStick)
    {
        bool moved = mouseDelta.sqrMagnitude > 0.04f || gamepadStick.sqrMagnitude > 0.0004f;
        if (moved)
            _manualLookInputTimer = lockOnManualInputGraceSeconds;
    }

    void DecayManualLookInput(float dt)
    {
        if (_manualLookInputTimer > 0f)
            _manualLookInputTimer = Mathf.Max(0f, _manualLookInputTimer - dt);
    }

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        if (!TryGetComponent(out _targeting))
            _targeting = gameObject.AddComponent<CombatTargeting>();
        if (mouseLook == null)
            TryGetComponent(out mouseLook);
    }

    void Start()
    {
        if (cameraTarget != null)
        {
            _pitch = cameraTarget.localEulerAngles.x;
            if (_pitch > 180f) _pitch -= 360f;
        }

        if (gameplayCamera == null)
            gameplayCamera = Camera.main;
        if (_targeting != null && _targeting.combatCamera == null)
            _targeting.combatCamera = gameplayCamera != null ? gameplayCamera : Camera.main;
    }

    float GetAdsMultiplier()
    {
        if (mouseLook == null || !mouseLook.adsEnabled)
            return 1f;
        return mouseLook.IsAimingDownSights ? mouseLook.adsLookSensitivityMultiplier : 1f;
    }

    void Update()
    {
        if (cameraTarget == null)
            return;

        float dt = Time.unscaledDeltaTime;
        float pitchDelta = 0f;
        // invertY true = flight-sim style (push up to look down); false = typical FPS (push up to look up).
        float vertSign = invertY ? -1f : 1f;

        Vector2 mouseDelta = Vector2.zero;
        Vector2 gamepadStick = Vector2.zero;
        if (Mouse.current != null)
            mouseDelta = Mouse.current.delta.ReadValue();
        if (Gamepad.current != null)
            gamepadStick = Gamepad.current.rightStick.ReadValue();
        RegisterManualLookInput(mouseDelta, gamepadStick);
        DecayManualLookInput(dt);

        if (Mouse.current != null)
        {
            _pendingYawFromMouse += mouseDelta.x * sensitivityX * dt;
            pitchDelta += mouseDelta.y * sensitivityY * dt * vertSign;
        }

        if (Gamepad.current != null)
        {
            if (gamepadStick.sqrMagnitude > 0.0004f)
                pitchDelta += gamepadStick.y * gamepadSensitivityY * dt * vertSign;
        }

        float adsMul = GetAdsMultiplier();
        pitchDelta *= adsMul;

        if (_rb == null)
        {
            float yaw = _pendingYawFromMouse * adsMul;
            _pendingYawFromMouse = 0f;
            Vector3 yawAxis = PlanetTangentBasis.ResolvePlanetUp(transform.position, null, transform.up);
            transform.Rotate(yawAxis, yaw, Space.World);
        }

        float effMin = EffectiveMinPitch;
        float effMax = EffectiveMaxPitch;

        Vector3 desiredYawForward = Vector3.zero;
        float desiredPitch = 0f;
        bool locking = !IsManualLookActive()
            && _targeting != null
            && _targeting.TryComputeLockLookTurn(cameraTarget, transform, effMin, effMax, out desiredYawForward, out desiredPitch);
        if (locking)
            _pitch = Mathf.MoveTowardsAngle(_pitch, desiredPitch, lockOnPitchDegreesPerSecond * dt);
        else
            _pitch = Mathf.Clamp(_pitch + pitchDelta, effMin, effMax);

        cameraTarget.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
    }

    void LateUpdate()
    {
        if (cameraTarget == null || _targeting == null)
            return;

        if (!lockOnUseViewportCentering || !_targeting.HasHardLock || IsManualLookActive())
            return;

        ApplyLockOnViewportPitchCorrection(Time.deltaTime);
        cameraTarget.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
    }

    void ApplyLockOnViewportPitchCorrection(float dt)
    {
        if (!lockOnUseViewportCentering || _targeting == null)
            return;

        Camera cam = gameplayCamera != null ? gameplayCamera : _targeting.GetCombatCamera();
        if (cam == null || !_targeting.TryGetLockOnCameraAimWorld(cam, out Vector3 aimWorld))
            return;

        Vector3 vp = cam.WorldToViewportPoint(aimWorld);
        if (vp.z <= 0.02f)
            return;

        float errY = vp.y - 0.5f;
        if (Mathf.Abs(errY) <= lockOnViewportDeadZone)
            return;

        float vertSign = invertY ? -1f : 1f;
        // Aim above center (errY>0): pull view down = opposite of mouse-up pitch sense tested with vertSign.
        float step = -errY * lockOnViewportPitchDegreesPerSecond * dt * vertSign;
        float cap = lockOnPitchDegreesPerSecond * dt;
        step = Mathf.Clamp(step, -cap, cap);
        _pitch = Mathf.Clamp(_pitch + step, EffectiveMinPitch, EffectiveMaxPitch);
    }

    void FixedUpdate()
    {
        if (cameraTarget == null || _rb == null)
            return;

        DecayManualLookInput(Time.fixedDeltaTime);

        float yawGamepad = 0f;
        if (Gamepad.current != null)
        {
            Vector2 stick = Gamepad.current.rightStick.ReadValue();
            if (invertGamepadLookX)
                stick.x = -stick.x;
            if (stick.sqrMagnitude > 0.0004f)
                yawGamepad += stick.x * gamepadSensitivityX * Time.fixedDeltaTime;
        }

        float adsMul = GetAdsMultiplier();
        float yawMouse = _pendingYawFromMouse;
        _pendingYawFromMouse = 0f;
        float yaw = (yawMouse + yawGamepad) * adsMul;

        float effMin = EffectiveMinPitch;
        float effMax = EffectiveMaxPitch;

        Vector3 desiredYawForward;
        float desiredPitch;
        if (!IsManualLookActive()
            && _targeting != null
            && _targeting.TryComputeLockLookTurn(cameraTarget, transform, effMin, effMax, out desiredYawForward, out desiredPitch))
        {
            // Yaw-only around planet up. Quaternion.RotateTowards(full body, LookRotation(flat, planetUp)) takes a
            // shortest 4D path that fights slope/tilt on the rigidbody and often reads as a wrong horizontal turn (e.g. always left).
            Vector3 planetUp = PlanetTangentBasis.ResolvePlanetUp(transform.position, _targeting.planetCenterOverride, transform.up);
            // Match lens horizontal aim (body.forward can disagree a lot with pitched CM camera).
            Camera lookCam = gameplayCamera != null ? gameplayCamera : (_targeting != null ? _targeting.GetCombatCamera() : null);
            Vector3 flatCurrent = lookCam != null
                ? PlanetTangentBasis.GetTangentForward(lookCam.transform.forward, planetUp, transform.forward)
                : PlanetTangentBasis.GetTangentForward(transform.forward, planetUp, transform.forward);
            float signed = PlanetTangentBasis.SignedYawDegrees(flatCurrent, desiredYawForward, planetUp);
            float maxStep = lockOnYawDegreesPerSecond * Time.fixedDeltaTime;
            float step = Mathf.Clamp(signed, -maxStep, maxStep);
            if (lockOnUseViewportCentering && lookCam != null && _targeting.TryGetLockOnCameraAimWorld(lookCam, out Vector3 aimVp))
            {
                Vector3 vp = lookCam.WorldToViewportPoint(aimVp);
                if (vp.z > 0.02f)
                {
                    float errX = vp.x - 0.5f;
                    if (Mathf.Abs(errX) > lockOnViewportDeadZone)
                    {
                        float vpStep = Mathf.Clamp(-errX * lockOnViewportYawDegreesPerSecond * Time.fixedDeltaTime, -maxStep * 0.35f, maxStep * 0.35f);
                        step = Mathf.Clamp(step + vpStep, -maxStep, maxStep);
                    }
                }
            }

            if (Mathf.Abs(step) > 1e-5f)
                _rb.MoveRotation(Quaternion.AngleAxis(step, planetUp) * _rb.rotation);
            return;
        }

        if (_targeting != null)
        {
            Vector3 planetUp = PlanetTangentBasis.ResolvePlanetUp(transform.position, _targeting.planetCenterOverride, transform.up);
            Vector3 flatFwd = PlanetTangentBasis.GetTangentForward(cameraTarget.forward, planetUp, transform.forward);
            float softYaw;
            if (_targeting.TryGetSoftLookYawDelta(transform, cameraTarget, flatFwd, Time.fixedDeltaTime, out softYaw))
                yaw += softYaw;
        }

        if (Mathf.Abs(yaw) < 1e-6f)
            return;

        Vector3 yawAxis = PlanetTangentBasis.ResolvePlanetUp(transform.position, _targeting != null ? _targeting.planetCenterOverride : null, transform.up);
        _rb.MoveRotation(Quaternion.AngleAxis(yaw, yawAxis) * _rb.rotation);
    }
}
