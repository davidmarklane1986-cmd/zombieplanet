using UnityEngine;

/// <summary>
/// Re-asserts a fitted localScale each LateUpdate. Kenny Generic clips often key empty-path
/// m_LocalScale=100 on the Animator host, which would otherwise undo FitVisualHeight.
/// </summary>
[DisallowMultipleComponent]
public sealed class FittedVisualScaleLock : MonoBehaviour
{
    Vector3 _locked = Vector3.one;
    bool _hasLock;

    public bool HasLock => _hasLock;
    public Vector3 LockedScale => _locked;

    void Awake() => Capture();

    void OnEnable() => Capture();

    void LateUpdate()
    {
        if (!_hasLock)
            return;
        if ((transform.localScale - _locked).sqrMagnitude > 1e-6f)
            transform.localScale = _locked;
    }

    public void Capture()
    {
        Vector3 s = transform.localScale;
        if (s.x > 20f || s.y > 20f || s.z > 20f)
        {
            _locked = Vector3.one * 0.42f;
            transform.localScale = _locked;
            _hasLock = true;
            return;
        }
        if (s.x < 1e-4f)
            return;
        _locked = s;
        _hasLock = true;
    }

    public void SetLockedScale(Vector3 scale)
    {
        _locked = scale;
        _hasLock = scale.x > 1e-4f;
        if (_hasLock)
            transform.localScale = _locked;
    }
}
