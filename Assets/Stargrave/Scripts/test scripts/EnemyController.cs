using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class EnemyController : MonoBehaviour
{
    public float moveSpeed = 3f;
    public float gravityStrength = 20f;
    public float detectionRadius = 25f;
    public float attackRadius = 2f;
    public float rotationSpeed = 5f;
    public float roamChangeInterval = 4f;
    public float surfaceStickDistance = 0.5f;
    public float surfaceStickForce = 50f;

    public int maxHealth = 3;
    [HideInInspector] public int currentHealth;

    private Transform player;
    private Rigidbody rb;
    private Vector3 currentDirection;
    private float roamTimer;
    private bool isAttacking = false;

    void Start()
    {
        currentHealth = maxHealth;
        rb = GetComponent<Rigidbody>();
        rb.useGravity = false;
        rb.constraints = RigidbodyConstraints.FreezeRotation;

        player = GameObject.FindGameObjectWithTag("Player")?.transform;
        PickNewRoamDirection();
    }

    void FixedUpdate()
    {
        if (player == null) return;

        Vector3 gravityUp = (transform.position - Vector3.zero).normalized;
        Vector3 gravity = -gravityUp * gravityStrength;
        rb.AddForce(gravity, ForceMode.Acceleration);

        float distanceToPlayer = Vector3.Distance(transform.position, player.position);

        if (distanceToPlayer <= attackRadius)
        {
            Attack();
        }
        else if (distanceToPlayer <= detectionRadius)
        {
            isAttacking = false;
            MoveTowards(player.position, gravityUp);
        }
        else
        {
            isAttacking = false;
            roamTimer += Time.fixedDeltaTime;
            if (roamTimer >= roamChangeInterval)
            {
                PickNewRoamDirection();
                roamTimer = 0f;
            }
            MoveTowards(transform.position + currentDirection, gravityUp);
        }

        // Sticky to surface
        if (Physics.Raycast(transform.position, -gravityUp, out RaycastHit hit, 5f))
        {
            float dist = hit.distance;
            if (dist > surfaceStickDistance)
            {
                Vector3 pull = -gravityUp * (dist - surfaceStickDistance);
                rb.AddForce(pull * surfaceStickForce);
            }
        }
    }

    void MoveTowards(Vector3 targetPos, Vector3 gravityUp)
    {
        Vector3 desiredDir = (targetPos - transform.position).normalized;
        Vector3 tangentDir = Vector3.ProjectOnPlane(desiredDir, gravityUp).normalized;

        if (!isAttacking)
        {
            rb.linearVelocity = tangentDir * moveSpeed + Vector3.Project(rb.linearVelocity, gravityUp);
        }

        if (tangentDir.sqrMagnitude > 0.001f)
        {
            Quaternion targetRot = Quaternion.LookRotation(tangentDir, gravityUp);
            rb.MoveRotation(Quaternion.Slerp(rb.rotation, targetRot, rotationSpeed * Time.fixedDeltaTime));
        }
    }

    void Attack()
    {
        if (!isAttacking)
        {
            isAttacking = true;
            rb.linearVelocity = Vector3.zero;
            Debug.Log($"{name} attacks the player!");
            // TODO: Trigger animation or apply damage to player
        }
    }

    void PickNewRoamDirection()
    {
        Vector3 randomDir = Random.onUnitSphere;
        Vector3 gravityUp = (transform.position - Vector3.zero).normalized;
        currentDirection = Vector3.ProjectOnPlane(randomDir, gravityUp).normalized;
    }

    public void TakeDamage(int amount)
    {
        currentHealth -= amount;
        if (currentHealth <= 0)
        {
            Die();
        }
    }

    public void Die()
    {
        EnemySpawner.Instance?.RespawnEnemy(this);
    }
}
