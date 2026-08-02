using UnityEngine;

/// <summary>
/// Fake walk for Kenny GraveyardKit rigid modular characters (root/leg-left/leg-right/torso/arm-*).
/// Their packs have no skinned walk clips, so Kenny Survivor animations cannot be remapped onto them.
/// </summary>
public sealed class ZombieProceduralLimbWalk : MonoBehaviour
{
    public Transform modelRoot;
    public float moveThreshold = 0.12f;
    public float strideHz = 2.2f;
    public float legSwingDegrees = 28f;
    public float armSwingDegrees = 22f;
    public float bobDegrees = 4f;

    Transform _leftLeg, _rightLeg, _leftArm, _rightArm, _torso;
    Quaternion _leftLeg0, _rightLeg0, _leftArm0, _rightArm0, _torso0;
    bool _cached;
    float _phase;

    void Awake() => CacheLimbs();

    void CacheLimbs()
    {
        if (_cached)
            return;

        Transform root = modelRoot != null ? modelRoot : transform;
        // Prefab CharacterModel often has a child "root" from the FBX.
        Transform kit = root.Find("root");
        if (kit == null)
            kit = root;

        _leftLeg = FindChild(kit, "leg-left");
        _rightLeg = FindChild(kit, "leg-right");
        _torso = FindChild(kit, "torso");
        if (_torso != null)
        {
            _leftArm = FindChild(_torso, "arm-left");
            _rightArm = FindChild(_torso, "arm-right");
        }

        if (_leftLeg != null) _leftLeg0 = _leftLeg.localRotation;
        if (_rightLeg != null) _rightLeg0 = _rightLeg.localRotation;
        if (_leftArm != null) _leftArm0 = _leftArm.localRotation;
        if (_rightArm != null) _rightArm0 = _rightArm.localRotation;
        if (_torso != null) _torso0 = _torso.localRotation;

        _cached = _leftLeg != null || _rightLeg != null;
    }

    static Transform FindChild(Transform parent, string name)
    {
        if (parent == null)
            return null;
        var direct = parent.Find(name);
        if (direct != null)
            return direct;
        foreach (var t in parent.GetComponentsInChildren<Transform>(true))
        {
            if (t.name == name)
                return t;
        }
        return null;
    }

    public void SetPlanarSpeed(float planarSpeed, float nominalMoveSpeed, bool animate)
    {
        if (!_cached)
            CacheLimbs();
        if (!_cached)
            return;

        if (!animate || planarSpeed <= moveThreshold)
        {
            ResetPose();
            return;
        }

        float nominal = Mathf.Max(0.5f, nominalMoveSpeed);
        float rate = strideHz * Mathf.Clamp(planarSpeed / nominal, 0.55f, 1.8f);
        _phase += Time.fixedDeltaTime * rate * Mathf.PI * 2f;
        float s = Mathf.Sin(_phase);
        float c = Mathf.Cos(_phase);

        if (_leftLeg != null)
            _leftLeg.localRotation = _leftLeg0 * Quaternion.Euler(s * legSwingDegrees, 0f, 0f);
        if (_rightLeg != null)
            _rightLeg.localRotation = _rightLeg0 * Quaternion.Euler(-s * legSwingDegrees, 0f, 0f);
        if (_leftArm != null)
            _leftArm.localRotation = _leftArm0 * Quaternion.Euler(-s * armSwingDegrees, 0f, 0f);
        if (_rightArm != null)
            _rightArm.localRotation = _rightArm0 * Quaternion.Euler(s * armSwingDegrees, 0f, 0f);
        if (_torso != null)
            _torso.localRotation = _torso0 * Quaternion.Euler(0f, c * bobDegrees, 0f);
    }

    void ResetPose()
    {
        if (_leftLeg != null) _leftLeg.localRotation = _leftLeg0;
        if (_rightLeg != null) _rightLeg.localRotation = _rightLeg0;
        if (_leftArm != null) _leftArm.localRotation = _leftArm0;
        if (_rightArm != null) _rightArm.localRotation = _rightArm0;
        if (_torso != null) _torso.localRotation = _torso0;
    }
}
