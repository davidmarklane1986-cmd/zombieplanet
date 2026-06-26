using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class EnemyRoamer : MonoBehaviour
{
    public float moveSpeed = 3f;
    public float gravityStrength = 20f;
    public float rotationSpeed = 5f;
    public float roamChangeInterval = 4f;

    private Rigidbody rb;
    private Vector3 currentDirection;
    private float roamTimer;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        rb.useGravity = false;
        rb.constraints = RigidbodyConstraints.FreezeRotation;
        PickNewRoamDirection();
    }

    void FixedUpdate()
    {
        Vector3 gravityUp = (transform.position - Vector3.zero).normalized;
        Vector3 gravity = -gravityUp * gravityStrength;
        rb.AddForce(gravity, ForceMode.Acceleration);

        roamTimer += Time.fixedDeltaTime;
        if (roamTimer >= roamChangeInterval || currentDirection == Vector3.zero)
        {
            PickNewRoamDirection();
            roamTimer = 0f;
        }

        // Project direction onto planet surface tangent
        Vector3 tangent = Vector3.ProjectOnPlane(currentDirection, gravityUp).normalized;

        // Debug
        Debug.DrawRay(transform.position, tangent * 3f, Color.red);

        rb.linearVelocity = tangent * moveSpeed;

        Quaternion targetRot = Quaternion.LookRotation(tangent, -gravityUp);
        rb.MoveRotation(Quaternion.Slerp(rb.rotation, targetRot, rotationSpeed * Time.fixedDeltaTime));

        // Surface sticking
        if (Physics.Raycast(transform.position, -gravityUp, out RaycastHit hit, 5f))
        {
            float dist = hit.distance;
            if (dist > 0.6f)
            {
                Vector3 pull = -gravityUp * (dist - 0.6f);
                rb.MovePosition(transform.position + pull * 0.5f);
            }
        }
    }

    void PickNewRoamDirection()
    {
        Vector3 random = Random.onUnitSphere;
        Vector3 gravityUp = (transform.position - Vector3.zero).normalized;
        currentDirection = Vector3.ProjectOnPlane(random, gravityUp).normalized;

        // Debug log
        Debug.Log("New roam direction: " + currentDirection);
    }
}
