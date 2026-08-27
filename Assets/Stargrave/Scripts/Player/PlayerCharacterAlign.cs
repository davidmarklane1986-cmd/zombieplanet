using UnityEngine;

/// <summary>
/// Aligns the visible character model with the Player's CapsuleCollider and lets you tune the model height.
/// Add to the Player (same GameObject as CapsuleCollider). Disables colliders on the model so only the root capsule is used.
/// </summary>
[RequireComponent(typeof(CapsuleCollider))]
public class PlayerCharacterAlign : MonoBehaviour
{
    [Tooltip("Child transform to align (e.g. CharacterModel). If unset, auto-finds by name 'CharacterModel' or first child with Animator/SkinnedMeshRenderer.")]
    public Transform characterModel;

    [Tooltip("Adjust how high the model appears. Negative = lower the model, positive = raise it. Physics (capsule) is unchanged.")]
    [Range(-2f, 2f)]
    public float modelHeightOffset = 0f;

    [Tooltip("Rotate the model this many degrees clockwise (from above). 0 = default facing.")]
    [Range(-30f, 30f)]
    public float rotationOffsetDegrees = 0f;

    CapsuleCollider _cap;

    void Awake()
    {
        _cap = GetComponent<CapsuleCollider>();
        RealignNow();
    }

    /// <summary>Re-run alignment after a character loadout swaps the visual under CharacterModel.</summary>
    public void RealignNow()
    {
        if (_cap == null)
            _cap = GetComponent<CapsuleCollider>();
        if (_cap == null)
            return;

        Transform model = characterModel;
        if (model == null)
        {
            var t = transform.Find("CharacterModel");
            if (t != null) model = t;
            if (model == null)
            {
                for (int i = 0; i < transform.childCount; i++)
                {
                    var c = transform.GetChild(i);
                    if (c.GetComponent<Animator>() != null || c.GetComponentInChildren<SkinnedMeshRenderer>() != null)
                    {
                        model = c;
                        break;
                    }
                }
            }
        }

        if (model == null) return;

        // Place model so feet sit at capsule bottom (account for capsule center and direction)
        int dir = _cap.direction;
        Vector3 axis = dir == 0 ? Vector3.right : (dir == 1 ? Vector3.up : Vector3.forward);
        float halfH = _cap.height * 0.5f;
        model.localPosition = _cap.center - axis * halfH + axis * modelHeightOffset;
        float yaw = 180f - rotationOffsetDegrees; // face away from camera; negative offset = clockwise
        model.localRotation = Quaternion.Euler(0f, yaw, 0f);
        // Keep whatever scale the loadout/prefab set on the visual child; only normalize the root if empty.
        if (model.childCount == 0)
            model.localScale = Vector3.one;

        // Match planet matte shading (no skybox silver fill at night).
        foreach (var r in model.GetComponentsInChildren<Renderer>(true))
        {
            var mats = r.materials;
            for (int i = 0; i < mats.Length; i++)
                ModelMatteLighting.MakeMatte(mats[i], ambientFill: ModelMatteLighting.PlayerAmbientFill);
            r.materials = mats;
        }

        // Disable colliders on model hierarchy so only the root CapsuleCollider is used
        foreach (var col in model.GetComponentsInChildren<Collider>(true))
        {
            if (col != _cap)
                col.enabled = false;
        }
    }

    void OnValidate()
    {
        if (_cap == null) _cap = GetComponent<CapsuleCollider>();
        if (_cap == null) return;

        Transform model = characterModel != null ? characterModel : transform.Find("CharacterModel");
        if (model == null && transform.childCount > 0)
        {
            for (int i = 0; i < transform.childCount; i++)
            {
                var c = transform.GetChild(i);
                if (c.GetComponent<Animator>() != null || c.GetComponentInChildren<SkinnedMeshRenderer>() != null)
                {
                    model = c;
                    break;
                }
            }
        }
        if (model == null) return;

        int d = _cap.direction;
        Vector3 ax = d == 0 ? Vector3.right : (d == 1 ? Vector3.up : d == 2 ? Vector3.forward : Vector3.up);
        float half = _cap.height * 0.5f;
        model.localPosition = _cap.center - ax * half + ax * modelHeightOffset;
        float yaw = 180f - rotationOffsetDegrees;
        model.localRotation = Quaternion.Euler(0f, yaw, 0f);
    }
}
