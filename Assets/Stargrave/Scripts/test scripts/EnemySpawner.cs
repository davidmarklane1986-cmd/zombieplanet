
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class EnemySpawner : MonoBehaviour
{
    public static EnemySpawner Instance;

    [Header("Spawning")]
    public GameObject enemyPrefab;
    public int initialEnemyCount = 3;

    private List<EnemyController> activeEnemies = new List<EnemyController>();

    void Awake()
    {
        Instance = this;
    }

    void Start()
    {
        StartCoroutine(SpawnInitialEnemiesWhenReady());
    }

    private IEnumerator SpawnInitialEnemiesWhenReady()
    {
        var planet = Object.FindFirstObjectByType<Planet>();
        if (planet != null)
        {
            while (!planet.IsGenerated)
                yield return null;
        }
        else
        {
            yield return new WaitForSeconds(0.5f);
        }

        yield return null;

        for (int i = 0; i < initialEnemyCount; i++)
        {
            SpawnEnemyAtRandomPosition();
        }
    }

    private void SpawnEnemyAtRandomPosition()
    {
        Vector3 dir = Random.onUnitSphere;
        Vector3 spawnPos = GetSurfacePosition(dir);
        GameObject newEnemy = Instantiate(enemyPrefab, spawnPos, Quaternion.identity);
        EnemyController ec = newEnemy.GetComponent<EnemyController>();
        if (ec != null)
        {
            activeEnemies.Add(ec);
        }
    }

    public void RespawnEnemy(EnemyController deadEnemy)
    {
        activeEnemies.Remove(deadEnemy);

        Transform player = GameObject.FindGameObjectWithTag("Player")?.transform;
        if (player == null) return;

        Vector3 oppositeDir = -player.position.normalized + Random.onUnitSphere * 0.05f;
        Vector3 spawnPos = GetSurfacePosition(oppositeDir);

        deadEnemy.transform.position = spawnPos;
        deadEnemy.currentHealth = deadEnemy.maxHealth;
        activeEnemies.Add(deadEnemy);

        SpawnEnemyAtRandomPosition();
    }

    private Vector3 GetSurfacePosition(Vector3 direction)
    {
        direction.Normalize();
        Ray ray = new Ray(direction * 1000f, -direction);

        if (Physics.Raycast(ray, out RaycastHit hit, 2000f, LayerMask.GetMask("Default")))
        {
            return hit.point + hit.normal * 1.0f;
        }

        return direction * 51f;
    }
}
