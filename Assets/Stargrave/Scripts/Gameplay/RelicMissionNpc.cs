using UnityEngine;

public class RelicMissionNpc : MonoBehaviour
{
    [Header("Timing")]
    public float despawnAfterNoInteractionSeconds = 600f;

    [Header("Interaction")]
    public float talkRange = 7f;
    [Range(0f, 1f)]
    public float centerScreenDot = 0.8f;

    [Header("Mission")]
    public int minRelicsPerMission = 1;
    public int maxRelicsPerMission = 3;

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
        int relicsNeeded = Random.Range(minRelicsPerMission, maxRelicsPerMission + 1);
        Debug.Log($"Relic Mission NPC: Need {relicsNeeded} relics!");
    }
}
