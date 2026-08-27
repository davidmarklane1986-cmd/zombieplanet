using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Plays Run_Front when you press WASD, Idle_Menu when you release.
/// On Player root: finds Animator under child "CharacterModel" (cowboy). Assign Animator in Inspector to override.
/// </summary>
[DefaultExecutionOrder(200)]
public class PlayerCharacterAnimator : MonoBehaviour
{
    [Tooltip("Assign in Inspector to override. Otherwise auto-finds under child named 'CharacterModel'.")]
    public Animator animator;

    [Tooltip("Optional: used with gamepad to pick Run_Back vs Run_Front from stick vs camera. Defaults to Camera.main.")]
    public Transform movementCamera;

    [Tooltip("Animator state names (must match your controller).")]
    public string idleStateName = "root|Idle_Menu";
    public string runStateName = "root|Run_Front";
    public string runBackStateName = "root|Run_Back";
    public string deathStateName = "root|Death";

    [Tooltip("Log when run/idle is played (for debugging).")]
    public bool debugLog;

    Animator _animator;
    int _layer;
    int _idleHash;
    int _runHash;
    int _runBackHash;
    int _deathHash;
    string _idleResolvedName;
    string _runResolvedName;
    string _runBackResolvedName;
    string _deathResolvedName;
    bool _wasRunning;
    bool _wasWAndS;

    public bool IsLocomotionRunning => _wasRunning;

    PlanetMotor_InputSystem _motor;
    KennyLocomotionDriver _kennyLoco;
    PlayerCharacterLoadout _loadout;
    bool _correctKennyRunFacing;

    int ResolveStateHash(string name, out string resolvedName)
    {
        int hash = Animator.StringToHash(name);
        if (_animator != null && _animator.HasState(_layer, hash))
        {
            resolvedName = name;
            return hash;
        }

        string shortName = name.Replace("root|", "");
        if (shortName != name)
        {
            int shortHash = Animator.StringToHash(shortName);
            if (_animator != null && _animator.HasState(_layer, shortHash))
            {
                resolvedName = shortName;
                return shortHash;
            }
        }

        string rootName = name.Contains("root|") ? name : $"root|{name}";
        if (rootName != name)
        {
            int rootHash = Animator.StringToHash(rootName);
            if (_animator != null && _animator.HasState(_layer, rootHash))
            {
                resolvedName = rootName;
                return rootHash;
            }
        }

        resolvedName = name;
        return hash;
    }

    void CacheAnimator()
    {
        if (_animator != null) return;
        _animator = animator;
        if (_animator == null)
        {
            var model = transform.Find("CharacterModel");
            if (model != null)
                _animator = model.GetComponentInChildren<Animator>(true);
        }
        if (_animator == null)
            _animator = GetComponent<Animator>();
        if (_animator == null)
            _animator = GetComponentInChildren<Animator>(true);
        if (_animator == null)
        {
            var players = GameObject.FindGameObjectsWithTag("Player");
            foreach (var go in players)
            {
                var t = go.transform.Find("CharacterModel");
                if (t != null) _animator = t.GetComponentInChildren<Animator>(true);
                if (_animator == null) _animator = go.GetComponentInChildren<Animator>(true);
                if (_animator != null) break;
            }
        }
        BindKennyDriver(null);
        RefreshStateHashes();
    }

    void BindKennyDriver(Transform modelRoot)
    {
        _kennyLoco = null;
        if (modelRoot != null)
            _kennyLoco = modelRoot.GetComponentInChildren<KennyLocomotionDriver>(true);
        if (_kennyLoco == null && _animator != null)
            _kennyLoco = _animator.GetComponent<KennyLocomotionDriver>();
        if (_kennyLoco == null)
        {
            var model = transform.Find("CharacterModel");
            if (model != null)
                _kennyLoco = model.GetComponentInChildren<KennyLocomotionDriver>(true);
        }
    }

    void RefreshStateHashes()
    {
        if (_animator == null)
            return;
        _idleHash = ResolveStateHash(idleStateName, out _idleResolvedName);
        _runHash = ResolveStateHash(runStateName, out _runResolvedName);
        _runBackHash = ResolveStateHash(runBackStateName, out _runBackResolvedName);
        _deathHash = ResolveStateHash(deathStateName, out _deathResolvedName);
        if (debugLog) Debug.Log($"[PlayerCharacterAnimator] Found Animator on {_animator.gameObject.name}");
        if (debugLog && !_animator.HasState(_layer, _idleHash))
            Debug.LogWarning($"[PlayerCharacterAnimator] Idle state not found: {idleStateName}");
        if (debugLog && !_animator.HasState(_layer, _runHash))
            Debug.LogWarning($"[PlayerCharacterAnimator] Run state not found: {runStateName}");
        if (debugLog && !_animator.HasState(_layer, _runBackHash))
            Debug.LogWarning($"[PlayerCharacterAnimator] Run back state not found: {runBackStateName}");
        if (debugLog && !_animator.HasState(_layer, _deathHash))
            Debug.LogWarning($"[PlayerCharacterAnimator] Death state not found: {deathStateName}");
    }

    void Awake()
    {
        _layer = 0;
        _motor = GetComponent<PlanetMotor_InputSystem>();
        _loadout = GetComponent<PlayerCharacterLoadout>();
        CacheAnimator();
        CacheKennyFacing();
    }

    void CacheKennyFacing()
    {
        _correctKennyRunFacing = false;
        if (_loadout == null)
            _loadout = GetComponent<PlayerCharacterLoadout>();
        PlayableCharacterDef def = _loadout != null ? _loadout.AppliedCharacter : null;
        if (def == null || string.IsNullOrEmpty(def.id))
            return;
        _correctKennyRunFacing = !def.id.Equals("cowboy", System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Re-find Animator under CharacterModel after a visual swap and refresh state hashes.</summary>
    public void RebindToModel(Transform modelRoot = null)
    {
        _animator = null;
        animator = null;
        _kennyLoco = null;
        if (modelRoot != null)
            _animator = modelRoot.GetComponentInChildren<Animator>(true);
        if (_animator == null)
            CacheAnimator();
        else
        {
            BindKennyDriver(modelRoot);
            RefreshStateHashes();
        }
        _wasRunning = false;
        _wasWAndS = false;
        CacheKennyFacing();
        if (_kennyLoco == null && _animator != null && _animator.HasState(_layer, _idleHash))
            _animator.Play(_idleHash, _layer, 0f);
    }

    bool HasMoveInput()
    {
        if (Keyboard.current != null)
        {
            if (Keyboard.current.wKey.isPressed || Keyboard.current.sKey.isPressed ||
                Keyboard.current.aKey.isPressed || Keyboard.current.dKey.isPressed)
                return true;
        }
        if (Gamepad.current != null && Gamepad.current.leftStick.ReadValue().sqrMagnitude > 0.04f)
            return true;
        return false;
    }

    bool HasLocomotionMoveInput()
    {
        if (_motor != null && _motor.IsInBoat)
            return false;
        return HasMoveInput();
    }

    /// <summary>True when moving backward (S, S+A, or S+D). Used to pick Run_Back vs Run_Front.</summary>
    bool IsBackwardInput()
    {
        if (Keyboard.current != null)
        {
            bool s = Keyboard.current.sKey.isPressed;
            bool w = Keyboard.current.wKey.isPressed;
            if (s && !w) return true;
        }

        if (Gamepad.current == null)
            return false;

        Vector2 stick = Gamepad.current.leftStick.ReadValue();
        if (_motor != null && _motor.invertGamepadLeftStick)
            stick = -stick;

        if (stick.sqrMagnitude < 0.04f)
            return false;

        if (_motor != null && _motor.LockOnOrbitMoveEngaged)
            return stick.y < -0.2f;

        Transform cam =
            (_motor != null && _motor.cameraTransform != null) ? _motor.cameraTransform :
            movementCamera != null ? movementCamera :
            Camera.main != null ? Camera.main.transform : null;

        if (cam == null)
            return false;

        Vector3 up = transform.up;
        Vector3 camF = Vector3.ProjectOnPlane(cam.forward, up);
        if (camF.sqrMagnitude < 1e-6f) return false;
        camF.Normalize();
        Vector3 camR = Vector3.ProjectOnPlane(cam.right, up).normalized;

        // Same formula as PlanetMotor_InputSystem wishDir (camera-relative move).
        Vector3 wish = camF * stick.y + camR * stick.x;
        if (wish.sqrMagnitude < 1e-4f) return false;
        wish.Normalize();
        return Vector3.Dot(wish, camF) < -0.2f;
    }

    /// <summary>True when both W and S are pressed. Plays death once.</summary>
    static bool IsWAndSPressed()
    {
        if (Keyboard.current != null)
            return Keyboard.current.wKey.isPressed && Keyboard.current.sKey.isPressed;
        return false;
    }

    void Update()
    {
        if (_animator == null) CacheAnimator();
        if (_animator == null && _kennyLoco == null) return;

        if (_kennyLoco != null)
        {
            bool moving = HasLocomotionMoveInput();
            float nominal = _motor != null ? Mathf.Max(0.5f, _motor.moveSpeed) : 8f;
            float planar = moving ? nominal : 0f;
            _kennyLoco.SetLocomotion(planar, nominal, animate: true);
            _wasRunning = moving;
            return;
        }

        if (_animator == null) return;
        if (!_animator.isInitialized) return;

        bool wAndS = IsWAndSPressed();
        if (wAndS && _animator.HasState(_layer, _deathHash))
        {
            if (!_wasWAndS)
            {
                _wasWAndS = true;
                _animator.Play(_deathHash, _layer, 0f);
                if (debugLog) Debug.Log("[PlayerCharacterAnimator] Play: " + _deathResolvedName + " (W+S, once)");
            }
            return;
        }
        _wasWAndS = false;

        bool running = HasLocomotionMoveInput();
        bool backward = IsBackwardInput();
        bool useRunBack = running && backward && _animator.HasState(_layer, _runBackHash);
        int runHash = useRunBack ? _runBackHash : _runHash;
        string runResolvedName = useRunBack ? _runBackResolvedName : _runResolvedName;

        if (running != _wasRunning)
        {
            _wasRunning = running;
            int hash = running ? runHash : _idleHash;
            _animator.Play(hash, _layer, 0f);
            if (debugLog) Debug.Log($"[PlayerCharacterAnimator] Play: {(running ? runResolvedName : _idleResolvedName)}");
        }
        else if (running)
        {
            var state = _animator.GetCurrentAnimatorStateInfo(_layer);
            if (!state.IsName(runResolvedName))
                _animator.Play(runHash, _layer, 0f);
            else if (state.normalizedTime >= 1f)
                _animator.Play(runHash, _layer, 0f);
        }
    }

    void LateUpdate()
    {
        AlignKennyLegsForward();
    }

    /// <summary>
    /// Farmer run clips retargeted onto Kenny twist the hips/legs off capsule forward.
    /// Yaw hips so the left-right leg axis is square to movement. Fixed-degree offsets made this worse.
    /// </summary>
    void AlignKennyLegsForward()
    {
        if (!_correctKennyRunFacing || !_wasRunning || _animator == null || !_animator.isHuman)
            return;

        Transform hips = _animator.GetBoneTransform(HumanBodyBones.Hips);
        Transform leftLeg = _animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
        Transform rightLeg = _animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
        if (hips == null || leftLeg == null || rightLeg == null)
            return;

        Vector3 up = transform.up;
        Vector3 want = Vector3.ProjectOnPlane(transform.forward, up);
        if (want.sqrMagnitude < 1e-6f)
            return;
        want.Normalize();

        Vector3 across = Vector3.ProjectOnPlane(rightLeg.position - leftLeg.position, up);
        if (across.sqrMagnitude < 1e-8f)
            return;
        Vector3 hipFwd = Vector3.Cross(up, across.normalized);
        if (hipFwd.sqrMagnitude < 1e-8f)
            return;
        hipFwd.Normalize();
        if (Vector3.Dot(hipFwd, want) < 0f)
            hipFwd = -hipFwd;

        float yaw = Vector3.SignedAngle(hipFwd, want, up);
        if (Mathf.Abs(yaw) < 0.2f)
            return;
        hips.Rotate(up, yaw, Space.World);
    }

    /// <summary>Called by <see cref="PlayerHealth"/> when the player dies from damage (not W+S debug).</summary>
    public void PlayDeathFromDamage()
    {
        CacheAnimator();
        if (_animator == null || !_animator.HasState(_layer, _deathHash))
            return;
        _wasWAndS = false;
        _wasRunning = false;
        _animator.Play(_deathHash, _layer, 0f);
    }

    /// <summary>After respawn, return locomotion to idle so run state can resume cleanly.</summary>
    public void ResetLocomotionAfterRespawn()
    {
        _wasWAndS = false;
        _wasRunning = false;
        CacheAnimator();
        if (_animator == null || !_animator.HasState(_layer, _idleHash))
            return;
        _animator.Play(_idleHash, _layer, 0f);
    }
}
