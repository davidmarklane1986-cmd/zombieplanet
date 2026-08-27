using UnityEngine;

/// <summary>
/// Kenny Humanoid clips do not retarget farmer <c>Weapon_Bone</c>. Poses a runtime bone
/// so idle sits on the back (camera-facing torso) and run follows the right hand.
/// </summary>
[DefaultExecutionOrder(350)]
public sealed class RuntimeWeaponBoneDriver : MonoBehaviour
{
    static readonly Vector3 FarmerGunLocalPos = new Vector3(-0.010876f, 0.38236f, -0.11518f);
    static readonly Quaternion FarmerGunLocalRot = new Quaternion(0.5211406f, 0.45324838f, 0.32258314f, 0.6472392f);

    Transform _bone;
    Transform _gunSocket;
    Animator _animator;
    PlayerCharacterAnimator _locomotion;
    Transform _player;

    public void Configure(
        Transform bone,
        Transform gunSocket,
        Animator animator,
        PlayerCharacterAnimator locomotion,
        Transform playerRoot)
    {
        _bone = bone;
        _gunSocket = gunSocket;
        _animator = animator;
        _locomotion = locomotion;
        _player = playerRoot;
        SnapNow();
    }

    bool _wasInHand;

    public bool SnapNow()
    {
        bool inHand = WantCarryInHand();
        bool ok = Pose(inHand);
        if (inHand)
            RefreshMuzzleIfNeeded();
        _wasInHand = inHand;
        return ok;
    }

    void LateUpdate()
    {
        bool inHand = WantCarryInHand();
        Pose(inHand);
        if (inHand && !_wasInHand)
            RefreshMuzzleIfNeeded();
        _wasInHand = inHand;
    }

    void RefreshMuzzleIfNeeded()
    {
        if (_player == null)
            return;
        var weapons = _player.GetComponent<PlayerWeaponController>();
        if (weapons != null)
            weapons.RefreshHeldMuzzle();
    }

    bool WantCarryInHand()
    {
        // Same signal Cowboy uses: idle clip = back, run clip = hand.
        if (_locomotion != null)
            return _locomotion.IsLocomotionRunning;
        return false;
    }

    bool Pose(bool inHand)
    {
        if (_bone == null || _gunSocket == null)
            return false;

        Transform vis = ResolveVisual();
        Vector3 up = _player != null ? _player.up : vis.up;

        if (inHand)
        {
            Transform hand = ResolveRightHand();
            if (hand == null)
                return false;
            Quaternion holdRot = hand.rotation * Quaternion.Euler(-90f, 180f, 90f)
                * Quaternion.Euler(0f, 180f, 0f);
            Vector3 aim = ResolveAim(up);
            PoseBoneSoGunMatches(hand.position, holdRot);
            ShiftHeldSoGripInPalm(hand.position, aim);
            _bone.position += aim * 0.32f;
            return true;
        }

        Transform chest = ResolveChest();
        if (chest == null)
            chest = _bone.parent != null ? _bone.parent : _bone;

        Vector3 back = ResolveBackDirection(chest.position, up);
        Vector3 targetCenter = chest.position + back * 0.14f + up * 0.20f;
        if (_animator != null && _animator.isHuman)
        {
            Transform head = _animator.GetBoneTransform(HumanBodyBones.Head);
            if (head != null)
                targetCenter = Vector3.Lerp(chest.position, head.position, 0.72f) + back * 0.14f;
        }

        Quaternion gunRot = Quaternion.AngleAxis(180f, up) * Quaternion.LookRotation(up, back);
        PoseBoneSoGunMatches(targetCenter, gunRot);
        // Mesh hangs off the socket — put the visible centre on the upper back, not the pivot.
        ShiftHeldSoCenterAt(targetCenter);
        return true;
    }

    Transform FindHeld()
    {
        Transform search = _bone != null ? _bone : _player;
        if (search == null)
            return null;
        Transform[] all = search.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] != null && all[i].name == PlayerWeaponController.RuntimeHeldName)
                return all[i];
        }
        if (_player != null && search != _player)
        {
            all = _player.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].name == PlayerWeaponController.RuntimeHeldName)
                    return all[i];
            }
        }
        return null;
    }

    bool TryHeldWorldBounds(out Bounds bounds)
    {
        bounds = default;
        Transform held = FindHeld();
        if (held == null)
            return false;
        Renderer[] rs = held.GetComponentsInChildren<Renderer>(true);
        bool any = false;
        for (int i = 0; i < rs.Length; i++)
        {
            if (rs[i] == null || !rs[i].enabled)
                continue;
            if (!any)
            {
                bounds = rs[i].bounds;
                any = true;
            }
            else
                bounds.Encapsulate(rs[i].bounds);
        }
        return any;
    }

    void ShiftHeldSoCenterAt(Vector3 worldCenter)
    {
        if (!TryHeldWorldBounds(out Bounds b))
            return;
        _bone.position += worldCenter - b.center;
    }

    void ShiftHeldSoGripInPalm(Vector3 palm, Vector3 aim)
    {
        if (!TryHeldWorldBounds(out Bounds b) || aim.sqrMagnitude < 1e-6f)
            return;
        aim.Normalize();
        float half = Mathf.Abs(aim.x) * b.extents.x
            + Mathf.Abs(aim.y) * b.extents.y
            + Mathf.Abs(aim.z) * b.extents.z;
        Vector3 muzzle = b.center + aim * half;
        Vector3 stock = b.center - aim * half;
        Vector3 grip = Vector3.Lerp(stock, muzzle, 0.10f);
        _bone.position += palm - grip;
    }

    Vector3 ResolveBackDirection(Vector3 from, Vector3 up)
    {
        Transform cam = null;
        if (_player != null)
        {
            var motor = _player.GetComponent<PlanetMotor_InputSystem>();
            if (motor != null && motor.cameraTransform != null)
                cam = motor.cameraTransform;
        }
        if (cam == null && Camera.main != null)
            cam = Camera.main.transform;

        if (cam != null)
        {
            Vector3 toCam = Vector3.ProjectOnPlane(cam.position - from, up);
            if (toCam.sqrMagnitude > 1e-5f)
                return toCam.normalized;
        }

        // CharacterModel yaw 180: mesh back is player.forward, not visual.forward.
        if (_player != null)
        {
            Vector3 fwd = Vector3.ProjectOnPlane(_player.forward, up);
            if (fwd.sqrMagnitude > 1e-5f)
                return fwd.normalized;
        }
        return Vector3.ProjectOnPlane(ResolveVisual().forward, up).normalized;
    }

    Vector3 ResolveAim(Vector3 up)
    {
        Transform cam = null;
        if (_player != null)
        {
            var motor = _player.GetComponent<PlanetMotor_InputSystem>();
            if (motor != null && motor.cameraTransform != null)
                cam = motor.cameraTransform;
        }
        if (cam == null && Camera.main != null)
            cam = Camera.main.transform;

        if (cam != null)
        {
            Vector3 fwd = Vector3.ProjectOnPlane(cam.forward, up);
            if (fwd.sqrMagnitude > 1e-5f)
                return fwd.normalized;
        }
        if (_player != null)
        {
            Vector3 fwd = Vector3.ProjectOnPlane(_player.forward, up);
            if (fwd.sqrMagnitude > 1e-5f)
                return fwd.normalized;
        }
        return up;
    }

    Transform ResolveVisual()
    {
        if (_animator != null)
        {
            Transform t = _animator.transform;
            if (t.parent != null && t.parent.name == PlayerCharacterLoadout.CharacterModelChildName)
                return t.parent;
            return t;
        }
        if (_player != null)
        {
            Transform model = _player.Find(PlayerCharacterLoadout.CharacterModelChildName);
            if (model != null)
                return model;
        }
        return _bone != null ? _bone : transform;
    }

    void PoseBoneSoGunMatches(Vector3 gunWorldPos, Quaternion gunWorldRot)
    {
        Quaternion gunLocal = _gunSocket.localRotation;
        if (gunLocal.w == 0f && gunLocal.x == 0f && gunLocal.y == 0f && gunLocal.z == 0f)
            gunLocal = FarmerGunLocalRot;

        Vector3 gunLocalPos = _gunSocket.localPosition;
        if (gunLocalPos.sqrMagnitude < 1e-8f)
            gunLocalPos = FarmerGunLocalPos;

        _bone.rotation = gunWorldRot * Quaternion.Inverse(gunLocal);
        _bone.position = gunWorldPos - _bone.TransformVector(gunLocalPos);
    }

    Transform ResolveRightHand()
    {
        if (_animator != null && _animator.isHuman)
        {
            Transform bone = _animator.GetBoneTransform(HumanBodyBones.RightHand);
            if (bone != null)
                return bone;
        }
        Transform search = _player != null ? _player : transform;
        return FindByName(search, "RightHand") ?? FindByName(search, "hand.r");
    }

    Transform ResolveChest()
    {
        if (_animator == null || !_animator.isHuman)
            return null;
        Transform chest = _animator.GetBoneTransform(HumanBodyBones.UpperChest);
        if (chest == null)
            chest = _animator.GetBoneTransform(HumanBodyBones.Chest);
        if (chest == null)
            chest = _animator.GetBoneTransform(HumanBodyBones.Spine);
        return chest;
    }

    static Transform FindByName(Transform root, string name)
    {
        if (root == null)
            return null;
        if (root.name == name)
            return root;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform found = FindByName(root.GetChild(i), name);
            if (found != null)
                return found;
        }
        return null;
    }
}
