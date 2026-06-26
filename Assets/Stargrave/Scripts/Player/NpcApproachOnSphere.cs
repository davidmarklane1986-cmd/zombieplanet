using UnityEngine;

public class NpcApproachOnSphere : MonoBehaviour
{
    [Header("References")]
    public Transform player;
    public Transform planetCenter;

    [Header("Movement")]
    public float moveSpeed = 5f;
    public float stopDistance = 6f;
    public float rotateSpeed = 10f;

    private Rigidbody rb;
    private GravityAttractor planetGravity;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        if (rb == null)
        {
            Debug.LogError("NpcApproachOnSphere requires a Rigidbody component!");
            enabled = false;
            return;
        }

        // Find planet gravity if not assigned
        if (planetCenter == null)
        {
            GameObject planetObj = GameObject.FindGameObjectWithTag("Planet");
            if (planetObj != null)
            {
                planetCenter = planetObj.transform;
                planetGravity = planetObj.GetComponent<GravityAttractor>();
            }
        }
        else
        {
            planetGravity = planetCenter.GetComponent<GravityAttractor>();
        }

        // Find player if not assigned
        if (player == null)
        {
            player = GameObject.FindGameObjectWithTag("Player")?.transform;
        }
    }

    void FixedUpdate()
    {
        if (player == null || planetCenter == null || rb == null)
            return;

        Vector3 npcPos = transform.position;
        Vector3 playerPos = player.position;
        Vector3 center = planetCenter.position;

        // Calculate direction on sphere surface
        Vector3 npcToCenter = (npcPos - center).normalized;
        Vector3 playerToCenter = (playerPos - center).normalized;

        // Distance along surface
        float distance = Vector3.Distance(npcPos, playerPos);

        if (distance > stopDistance)
        {
            // Calculate movement direction on sphere surface
            // Project the direction to player onto the tangent plane
            Vector3 toPlayer = (playerPos - npcPos).normalized;
            Vector3 tangent = Vector3.ProjectOnPlane(toPlayer, npcToCenter).normalized;

            // Move along tangent
            Vector3 moveDirection = tangent * moveSpeed;
            rb.linearVelocity = moveDirection;
        }
        else
        {
            // Stop moving
            rb.linearVelocity = Vector3.zero;
        }

        // Rotate to face player
        if (distance > 0.1f)
        {
            Vector3 lookDirection = Vector3.ProjectOnPlane((playerPos - npcPos), npcToCenter).normalized;
            if (lookDirection.magnitude > 0.01f)
            {
                Quaternion targetRotation = Quaternion.LookRotation(lookDirection, npcToCenter);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, rotateSpeed * Time.fixedDeltaTime);
            }
        }
    }
}
