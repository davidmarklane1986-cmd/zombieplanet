using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Stargrave
{
    public class FoliageSpawner : MonoBehaviour
    {
        [Header("Assets")]
        public List<GameObject> foliagePrefabs;
        public List<GameObject> rockPrefabs;

        [Header("Settings")]
        public float spawnRadius = 30f;
        public int foliageCount = 150;
        public int rockCount = 60;
        public LayerMask groundLayer = -1; // Default to Everything
        public Vector2 scaleRange = new Vector2(0.8f, 1.2f);
        public bool alignToNormal = true;
        
        [Header("Distribution")]
        public float minDistanceBetweenObjects = 1.5f;
        public int maxSpawnAttempts = 100; // Per object, to avoid infinite loops
        public bool usePoissonDistribution = true; // Better coverage distribution
        
        [Header("Spherical Mode")]
        public bool sphericalMode = true;
        public Vector3 planetCenter = Vector3.zero;
        public float planetRadiusEstimate = 100f; // Used to ensure we start raycasting from SPACE, not underground
        
        [Header("Color Filtering")]
        [Tooltip("Uses same elevation + biome logic as planet shader. Dense foliage where green (valleys, grasslands), sparse on peaks/ocean.")]
        public bool useDensityBasedOnGreen = true;
        public bool foliageOnlyOnGreen = false; // Legacy binary check - prefer useDensityBasedOnGreen
        public float greenThreshold = 0.4f; // Minimum green component to consider area "green" (legacy)
        public float minGreennessForSpawn = 0.1f; // Minimum greenness (0-1) to allow spawning at all
        public Planet planet; // Reference to Planet component for color checking

        [Header("Terrain Constraints")]
        public float minHeightAbovePlanetRadius = 0f; // Only spawn above this height relative to planet radius
        [Range(0f, 90f)]
        public float maxSlopeAngle = 45f; // Only spawn on slopes flatter than this

        [Header("Runtime")]
        [Tooltip("If true, automatically spawn foliage when the planet finishes generating. Otherwise use Context Menu.")]
        public bool spawnWhenPlanetReady = false;

        void Start()
        {
            if (spawnWhenPlanetReady)
                StartCoroutine(SpawnWhenPlanetReady());
        }

        IEnumerator SpawnWhenPlanetReady()
        {
            var p = planet != null ? planet : Object.FindFirstObjectByType<Planet>();
            if (p != null)
            {
                while (!p.IsGenerated)
                    yield return null;
                yield return null; // Extra frame for colliders
            }
            SpawnAll();
        }

        [ContextMenu("Spawn Foliage & Rocks")]
        public void SpawnAll()
        {
            // Try to find planet if not assigned
            if (planet == null && sphericalMode)
            {
                GameObject planetObj = GameObject.FindGameObjectWithTag("Planet");
                if (planetObj != null)
                {
                    planet = planetObj.GetComponent<Planet>();
                }
            }
            
            SpawnObjects(foliagePrefabs, foliageCount, "Foliage", checkGreen: true);
            SpawnObjects(rockPrefabs, rockCount, "Rocks", checkGreen: false);
        }

        [ContextMenu("Clear All")]
        public void ClearAll()
        {
            // Simple clear by destroying children
            int childCount = transform.childCount;
            for (int i = childCount - 1; i >= 0; i--)
            {
                DestroyImmediate(transform.GetChild(i).gameObject);
            }
        }

        private void SpawnObjects(List<GameObject> prefabs, int count, string containerName, bool checkGreen = false)
        {
            if (prefabs == null || prefabs.Count == 0)
            {
                Debug.LogWarning($"No prefabs assigned for {containerName}");
                return;
            }

            GameObject container = new GameObject(containerName);
            container.transform.parent = transform;
            container.transform.localPosition = Vector3.zero;

            // Track spawned positions for minimum distance checking
            List<Vector3> spawnedPositions = new List<Vector3>();
            int successfullySpawned = 0;
            
            // Failure counters
            int failTooLow = 0;
            int failTooSteep = 0;
            int failNotGreen = 0;
            int failTooClose = 0;
            int failNoRaycast = 0;


            for (int i = 0; i < count; i++)
            {
                bool spawned = false;
                int attempts = 0;

                while (!spawned && attempts < maxSpawnAttempts)
                {
                    attempts++;
                    Vector3 rayOrigin;
                    Vector3 rayDir;
                    Vector3 candidatePosition = Vector3.zero;

                    if (sphericalMode)
                    {
                        // Spherical Logic: Raycast towards planet center
                        Vector3 randomOffset;
                        
                        if (usePoissonDistribution && spawnedPositions.Count > 0)
                        {
                            // Try to find a position away from existing objects
                            randomOffset = GetPoissonSample(spawnedPositions, spawnRadius);
                        }
                        else
                        {
                            randomOffset = Random.insideUnitSphere * spawnRadius;
                        }
                        
                        Vector3 targetPos = transform.position + randomOffset;
                        Vector3 dirFromCenter = (targetPos - planetCenter).normalized;
                        
                        // Start ray from significantly above the estimated planet surface
                        float currentDist = Vector3.Distance(transform.position, planetCenter);
                        float startHeight = Mathf.Max(currentDist, planetRadiusEstimate) + 50f;
                        
                        rayOrigin = planetCenter + (dirFromCenter * startHeight);
                        rayDir = -dirFromCenter; // Down towards center
                    }
                    else
                    {
                        // Flat/Local Logic
                        Vector2 randomCircle;
                        
                        if (usePoissonDistribution && spawnedPositions.Count > 0)
                        {
                            // Convert 3D positions to 2D local space for Poisson sampling
                            List<Vector2> localPositions = new List<Vector2>();
                            foreach (var pos in spawnedPositions)
                            {
                                Vector3 localPos = transform.InverseTransformPoint(pos);
                                localPositions.Add(new Vector2(localPos.x, localPos.z));
                            }
                            randomCircle = GetPoissonSample2D(localPositions, spawnRadius);
                        }
                        else
                        {
                            randomCircle = Random.insideUnitCircle * spawnRadius;
                        }
                        
                        Vector3 localStart = new Vector3(randomCircle.x, 0f, randomCircle.y);
                        rayOrigin = transform.TransformPoint(localStart) + (transform.up * 50f);
                        rayDir = -transform.up;
                    }

                    // Debug Visualization
                    Debug.DrawRay(rayOrigin, rayDir * 120f, Color.red, 5f);

                    if (Physics.Raycast(rayOrigin, rayDir, out RaycastHit hit, 120f, groundLayer))
                    {
                        candidatePosition = hit.point;

                        // Check Terrain Constraints
                        // 1. Height Check
                        float currentPlanetRadius = planetRadiusEstimate;
                        if (planet != null && planet.shapeSettings != null)
                        {
                            currentPlanetRadius = planet.shapeSettings.planetRadius;
                        }
                        
                        float distFromCenter = Vector3.Distance(candidatePosition, planetCenter);
                        if (distFromCenter < currentPlanetRadius + minHeightAbovePlanetRadius)
                        {
                             failTooLow++;
                             continue;
                        }

                        // 2. Slope Check
                        Vector3 upDir = (candidatePosition - planetCenter).normalized;
                        float slopeAngle = Vector3.Angle(hit.normal, upDir);
                        if (slopeAngle > maxSlopeAngle)
                        {
                            failTooSteep++;
                            continue;
                        }
                        
                        // Check minimum distance from other spawned objects
                        bool tooClose = false;
                        if (minDistanceBetweenObjects > 0)
                        {
                            foreach (var existingPos in spawnedPositions)
                            {
                                if (Vector3.Distance(candidatePosition, existingPos) < minDistanceBetweenObjects)
                                {
                                    tooClose = true;
                                    break;
                                }
                            }
                        }
                        
                        if (tooClose)
                        {
                            failTooClose++;
                            continue;
                        }
                        else
                        {
                            // Check greenness and spawn probability (for foliage only)

                            bool shouldSpawn = true;
                            float greenness = 1f;
                            
                            if (checkGreen && planet != null)
                            {
                                if (useDensityBasedOnGreen)
                                {
                                    // Get greenness using same height/biome logic as planet shader - dense where green (valleys, grass)
                                    greenness = planet.GetFoliageGreennessAtPosition(candidatePosition);
                                    
                                    // Check minimum threshold
                                    if (greenness < minGreennessForSpawn)
                                    {
                                        // Too little green, skip this position
                                        failNotGreen++;
                                        continue;
                                    }
                                    
                                    // Use greenness as probability (more green = higher chance to spawn)
                                    // This creates density variation: most green = always spawn, less green = sometimes spawn
                                    if (Random.value > greenness)
                                    {
                                        // Failed probability check, try again
                                        failNotGreen++;
                                        continue;
                                    }
                                }
                                else if (foliageOnlyOnGreen)
                                {
                                    // Legacy binary check
                                    shouldSpawn = planet.IsPositionGreen(candidatePosition, greenThreshold);
                                    if (!shouldSpawn)
                                    {
                                        failNotGreen++;
                                        continue;
                                    }
                                }

                            }

                            // Visualize: green = high greenness, yellow = medium, red = low
                            Color debugColor = Color.Lerp(Color.red, Color.green, greenness);
                            Debug.DrawLine(rayOrigin, hit.point, debugColor, 5f);
                            
                            GameObject prefab = prefabs[Random.Range(0, prefabs.Count)];
#if UNITY_EDITOR
                            GameObject instance = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab, container.transform);
#else
                            GameObject instance = Instantiate(prefab, container.transform);
#endif
                            
                            instance.transform.position = candidatePosition;
                            
                            // Rotation
                            if (alignToNormal)
                            {
                                instance.transform.up = hit.normal;
                            }
                            else
                            {
                                instance.transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(Random.onUnitSphere, hit.normal), hit.normal);
                            }

                            // Scale
                            float scale = Random.Range(scaleRange.x, scaleRange.y);
                            instance.transform.localScale = Vector3.one * scale;
                            
                            spawnedPositions.Add(candidatePosition);
                            successfullySpawned++;
                            spawned = true;
                        }
                    }
                    else
                    {
                         failNoRaycast++;
                    }
                }
            }
            
            Debug.Log($"[{containerName}] Success: {successfullySpawned}/{count}. Failures -> Low: {failTooLow}, Steep: {failTooSteep}, NotGreen: {failNotGreen}, Close: {failTooClose}, Miss: {failNoRaycast}");

        }
        
        // Poisson disk sampling helper for 3D (spherical)
        private Vector3 GetPoissonSample(List<Vector3> existingPoints, float radius)
        {
            // Try to find a point that's not too close to existing points
            for (int attempt = 0; attempt < 30; attempt++)
            {
                Vector3 candidate = Random.insideUnitSphere * radius;
                bool valid = true;
                
                foreach (var point in existingPoints)
                {
                    if (Vector3.Distance(candidate, point) < minDistanceBetweenObjects * 0.7f)
                    {
                        valid = false;
                        break;
                    }
                }
                
                if (valid)
                {
                    return candidate;
                }
            }
            
            // Fallback to random if we can't find a good spot
            return Random.insideUnitSphere * radius;
        }
        
        // Poisson disk sampling helper for 2D (flat)
        private Vector2 GetPoissonSample2D(List<Vector2> existingPoints, float radius)
        {
            // Try to find a point that's not too close to existing points
            for (int attempt = 0; attempt < 30; attempt++)
            {
                Vector2 candidate = Random.insideUnitCircle * radius;
                bool valid = true;
                
                foreach (var point in existingPoints)
                {
                    if (Vector2.Distance(candidate, point) < minDistanceBetweenObjects * 0.7f)
                    {
                        valid = false;
                        break;
                    }
                }
                
                if (valid)
                {
                    return candidate;
                }
            }
            
            // Fallback to random if we can't find a good spot
            return Random.insideUnitCircle * radius;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(transform.position, spawnRadius);
        }
    }
}
