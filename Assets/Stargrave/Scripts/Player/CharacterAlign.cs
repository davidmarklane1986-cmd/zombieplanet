using UnityEngine;

/// <summary>
/// Aligns the visible character model with the root's CapsuleCollider (feet at capsule bottom).
/// Use on Player, Zombie, or any character with a CapsuleCollider and a child model.
/// Disables colliders on the model so only the root capsule is used for physics.
/// </summary>
[RequireComponent(typeof(CapsuleCollider))]
public class CharacterAlign : MonoBehaviour
{
    [Tooltip("Child transform to align (e.g. CharacterModel). If unset, auto-finds by name 'CharacterModel' or first child with Animator/SkinnedMeshRenderer.")]
    public Transform characterModel;

    [Tooltip("Adjust how high the model appears. Negative = lower, positive = raise. Physics unchanged.")]
    [Range(-2f, 2f)]
    public float modelHeightOffset = -1f;

    [Tooltip("Rotate the model this many degrees around Y (from above). 0 = forward, 180 = face backward (e.g. player toward camera).")]
    [Range(-180f, 180f)]
    public float rotationOffsetDegrees = 0f;

    CapsuleCollider _cap;

    void Awake()
    {
        Align();
    }

    public void Align()
    {
        _cap = GetComponent<CapsuleCollider>();
        if (_cap == null) return;

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

        int dir = _cap.direction;
        Vector3 axis = dir == 0 ? Vector3.right : (dir == 1 ? Vector3.up : Vector3.forward);
        float halfH = _cap.height * 0.5f;
        model.localPosition = _cap.center - axis * halfH + axis * modelHeightOffset;
        model.localRotation = Quaternion.Euler(0f, rotationOffsetDegrees, 0f);
        model.localScale = Vector3.one;

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
        Vector3 ax = d == 0 ? Vector3.right : (d == 1 ? Vector3.up : Vector3.forward);
        float half = _cap.height * 0.5f;
        model.localPosition = _cap.center - ax * half + ax * modelHeightOffset;
        model.localRotation = Quaternion.Euler(0f, rotationOffsetDegrees, 0f);
    }
}
