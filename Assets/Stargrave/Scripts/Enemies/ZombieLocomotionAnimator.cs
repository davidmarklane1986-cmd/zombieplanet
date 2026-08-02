using UnityEngine;

/// <summary>
/// Same pattern as <see cref="PlayerCharacterAnimator"/>: Animator + Play(state) when move/idle changes.
/// </summary>
public sealed class ZombieLocomotionAnimator : MonoBehaviour
{
    public Animator animator;
    public string idleStateName = "Idle";
    public string walkStateName = "Walk";
    [Tooltip("Planar speed above this plays Walk.")]
    public float moveThreshold = 0.15f;

    Animator _animator;
    int _idleHash;
    int _walkHash;
    string _idleResolved;
    string _walkResolved;
    bool _wasMoving;
    bool _ready;

    void Awake()
    {
        CacheAnimator();
    }

    static int ResolveStateHash(Animator anim, string name, out string resolved)
    {
        resolved = name;
        if (anim == null || string.IsNullOrEmpty(name))
            return Animator.StringToHash(name ?? "");

        int hash = Animator.StringToHash(name);
        if (anim.HasState(0, hash))
            return hash;

        string shortName = name.Replace("root|", "");
        if (shortName != name)
        {
            int shortHash = Animator.StringToHash(shortName);
            if (anim.HasState(0, shortHash))
            {
                resolved = shortName;
                return shortHash;
            }
        }

        string rootName = name.Contains("root|") ? name : $"root|{name}";
        if (rootName != name)
        {
            int rootHash = Animator.StringToHash(rootName);
            if (anim.HasState(0, rootHash))
            {
                resolved = rootName;
                return rootHash;
            }
        }

        return hash;
    }

    /// <summary>Pick Farmer-style state names when Idle/Walk are missing.</summary>
    public void AutoPickPackStates()
    {
        if (_animator == null)
            CacheAnimator();
        if (_animator == null || _animator.runtimeAnimatorController == null)
            return;

        string[] idles = { idleStateName, "Idle", "root|Idle_Menu", "Idle_Menu", "Idle_1", "root|Idle_Gun" };
        string[] walks = { walkStateName, "Walk", "root|Run_Front", "Run_Front", "root|Run_Shooter", "Run_Shooter", "Run" };

        foreach (var n in idles)
        {
            int h = ResolveStateHash(_animator, n, out string resolved);
            if (_animator.HasState(0, h))
            {
                idleStateName = resolved;
                break;
            }
        }
        foreach (var n in walks)
        {
            int h = ResolveStateHash(_animator, n, out string resolved);
            if (_animator.HasState(0, h))
            {
                walkStateName = resolved;
                break;
            }
        }

        _idleHash = ResolveStateHash(_animator, idleStateName, out _idleResolved);
        _walkHash = ResolveStateHash(_animator, walkStateName, out _walkResolved);
        _ready = _animator.HasState(0, _idleHash) || _animator.HasState(0, _walkHash);
    }

    void CacheAnimator()
    {
        _animator = animator;
        if (_animator == null)
            _animator = GetComponent<Animator>();
        if (_animator == null)
            _animator = GetComponentInChildren<Animator>(true);
        if (_animator == null)
            return;

        _animator.applyRootMotion = false;
        _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        _idleHash = ResolveStateHash(_animator, idleStateName, out _idleResolved);
        _walkHash = ResolveStateHash(_animator, walkStateName, out _walkResolved);
        _ready = _animator.runtimeAnimatorController != null
                 && (_animator.HasState(0, _idleHash) || _animator.HasState(0, _walkHash));
        if (!_ready && _animator.runtimeAnimatorController != null)
            AutoPickPackStates();
        if (_ready && _animator.HasState(0, _idleHash))
            _animator.Play(_idleHash, 0, 0f);
    }

    /// <summary>Called by <see cref="ZombieAI"/> each FixedUpdate with planar speed.</summary>
    public void SetPlanarSpeed(float planarSpeed, float nominalMoveSpeed, bool animate)
    {
        if (_animator == null)
            CacheAnimator();
        if (_animator == null || !_ready)
            return;

        if (!animate)
        {
            if (_animator.enabled)
                _animator.enabled = false;
            return;
        }

        if (!_animator.enabled)
            _animator.enabled = true;
        if (!_animator.isInitialized)
            return;

        _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        bool moving = planarSpeed > moveThreshold;
        if (moving != _wasMoving)
        {
            _wasMoving = moving;
            int hash = moving ? _walkHash : _idleHash;
            if (_animator.HasState(0, hash))
                _animator.Play(hash, 0, 0f);
        }
        else if (moving)
        {
            var state = _animator.GetCurrentAnimatorStateInfo(0);
            bool onWalk = state.IsName(_walkResolved) || state.IsName(walkStateName);
            if (!onWalk && _animator.HasState(0, _walkHash))
                _animator.Play(_walkHash, 0, 0f);
            float nominal = Mathf.Max(0.5f, nominalMoveSpeed);
            _animator.speed = Mathf.Clamp(planarSpeed / nominal, 0.5f, 1.75f);
        }
        else
        {
            _animator.speed = 1f;
        }
    }
}
