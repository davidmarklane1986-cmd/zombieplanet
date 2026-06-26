using UnityEngine;

public class VaultMissionNpc : MonoBehaviour
{
    [Header("Timing")]
    public float despawnAfterNoInteractionSeconds = 600f;
    public float vaultMissionCooldownSeconds = 180f;

    [Header("Interaction")]
    public float talkRange = 7f;
    [Range(0f, 1f)]
    public float centerScreenDot = 0.8f;

    private float spawnTime;
    private float lastInteractionTime = -999f;

    void Start()
    {
        spawnTime = Time.time;
    }

    void Update()
    {
        // Despawn if no interaction for too long
        if (Time.time - spawnTime > despawnAfterNoInteractionSeconds)
        {
            if (Time.time - lastInteractionTime > despawnAfterNoInteractionSeconds)
            {
                Destroy(gameObject);
            }
        }
    }

    public void OnInteract()
    {
        lastInteractionTime = Time.time;
        // Mission logic would go here
        Debug.Log("Vault Mission NPC interacted!");
    }

    public bool CanInteract()
    {
        // Check if cooldown has passed
        return Time.time - lastInteractionTime >= vaultMissionCooldownSeconds;
    }
}
