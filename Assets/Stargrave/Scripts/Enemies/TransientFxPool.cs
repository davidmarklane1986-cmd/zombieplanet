using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Reuses short-lived VFX prefab instances (particles, simple effects) to avoid Instantiate/Destroy churn.
/// </summary>
public static class TransientFxPool
{
    sealed class PoolRoot : MonoBehaviour { }

    sealed class PoolBucket
    {
        public readonly Queue<GameObject> Inactive = new Queue<GameObject>();
        public Transform Root;
    }

    static readonly Dictionary<GameObject, PoolBucket> Buckets = new Dictionary<GameObject, PoolBucket>();
    static PoolRoot _poolRoot;
    public static int TotalBuckets => Buckets.Count;
    public static int TotalInstancesCreated { get; private set; }
    public static int TotalInactiveInstances { get; private set; }
    public static int TotalPlayRequests { get; private set; }

    public static void Play(GameObject prefab, Vector3 position, Quaternion rotation, float fallbackLifetime = 2f)
    {
        if (prefab == null)
            return;
        TotalPlayRequests++;

        EnsureRoot();
        PoolBucket bucket = GetOrCreateBucket(prefab);
        GameObject instance = bucket.Inactive.Count > 0 ? bucket.Inactive.Dequeue() : CreateInstance(prefab, bucket.Root);
        if (instance == null)
            return;

        instance.transform.SetPositionAndRotation(position, rotation);
        instance.SetActive(true);

        PooledFxAutoRelease autoRelease = instance.GetComponent<PooledFxAutoRelease>();
        if (autoRelease == null)
            autoRelease = instance.AddComponent<PooledFxAutoRelease>();
        autoRelease.Configure(prefab, ResolveLifetime(instance, fallbackLifetime));
        RefreshStats();
    }

    static void EnsureRoot()
    {
        if (_poolRoot != null)
            return;

        GameObject go = new GameObject("TransientFxPool");
        Object.DontDestroyOnLoad(go);
        _poolRoot = go.AddComponent<PoolRoot>();
    }

    static PoolBucket GetOrCreateBucket(GameObject prefab)
    {
        if (Buckets.TryGetValue(prefab, out PoolBucket existing))
            return existing;

        PoolBucket bucket = new PoolBucket();
        GameObject root = new GameObject($"Pool_{prefab.name}");
        root.transform.SetParent(_poolRoot.transform, false);
        bucket.Root = root.transform;
        Buckets.Add(prefab, bucket);
        return bucket;
    }

    static GameObject CreateInstance(GameObject prefab, Transform parent)
    {
        if (prefab == null)
            return null;
        GameObject go = Object.Instantiate(prefab, parent);
        go.SetActive(false);
        TotalInstancesCreated++;
        return go;
    }

    static float ResolveLifetime(GameObject instance, float fallbackLifetime)
    {
        float best = Mathf.Max(0.05f, fallbackLifetime);
        ParticleSystem[] systems = instance.GetComponentsInChildren<ParticleSystem>(true);
        for (int i = 0; i < systems.Length; i++)
        {
            ParticleSystem ps = systems[i];
            if (ps == null)
                continue;
            var main = ps.main;
            float duration = main.duration;
            float lifetime = duration;
            if (main.startLifetime.mode == ParticleSystemCurveMode.TwoConstants)
                lifetime += main.startLifetime.constantMax;
            else
                lifetime += main.startLifetime.constant;
            if (lifetime > best)
                best = lifetime;
        }
        return best;
    }

    static void Return(GameObject prefab, GameObject instance)
    {
        if (instance == null || prefab == null)
            return;

        if (!Buckets.TryGetValue(prefab, out PoolBucket bucket))
            return;

        instance.SetActive(false);
        instance.transform.SetParent(bucket.Root, false);
        bucket.Inactive.Enqueue(instance);
        RefreshStats();
    }

    static void RefreshStats()
    {
        int inactive = 0;
        foreach (KeyValuePair<GameObject, PoolBucket> kv in Buckets)
            inactive += kv.Value.Inactive.Count;
        TotalInactiveInstances = inactive;
    }

    sealed class PooledFxAutoRelease : MonoBehaviour
    {
        GameObject _prefab;
        float _releaseAt;

        public void Configure(GameObject prefab, float lifetime)
        {
            _prefab = prefab;
            _releaseAt = Time.time + Mathf.Max(0.05f, lifetime);
        }

        void Update()
        {
            if (Time.time < _releaseAt)
                return;
            TransientFxPool.Return(_prefab, gameObject);
        }
    }
}
