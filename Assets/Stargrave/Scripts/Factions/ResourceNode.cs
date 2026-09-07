using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Harvest state attached to a proxy for an existing runtime tree or rock.
/// The source visual is never destroyed or disabled by this component.
/// </summary>
[DisallowMultipleComponent]
public sealed class ResourceNode : MonoBehaviour
{
    [Header("Configured by FoliageResourceAdapter")]
    public FactionResourceType resourceType;
    [Min(1)] public int maximumAmount = 100;
    [Min(0.01f)] public float gatherPerSecond = 1f;
    [Min(0.1f)] public float cooldownSeconds = 120f;

    readonly List<FactionNpc> _workers = new List<FactionNpc>(2);
    float _cooldownUntil;

    public FactionResourceType ResourceType => resourceType;
    public int CurrentAmount { get; private set; }
    public bool IsAvailable => CurrentAmount > 0 && Time.time >= _cooldownUntil;
    public int WorkerCount => _workers.Count;

    void Awake()
    {
        CurrentAmount = Mathf.Max(1, maximumAmount);
    }

    void OnEnable()
    {
        FactionRegistry.RegisterResource(this);
    }

    void OnDisable()
    {
        FactionRegistry.UnregisterResource(this);
        _workers.Clear();
    }

    public void Configure(FactionResourceType type, int amount, float rate, float cooldown)
    {
        resourceType = type;
        maximumAmount = Mathf.Max(1, amount);
        gatherPerSecond = Mathf.Max(0.01f, rate);
        cooldownSeconds = Mathf.Max(0.1f, cooldown);
        CurrentAmount = maximumAmount;
        _cooldownUntil = 0f;
    }

    public bool CanReserve(FactionController owner)
    {
        if (!IsAvailable || owner == null)
            return false;
        for (int i = 0; i < _workers.Count; i++)
            if (_workers[i] != null && _workers[i].Faction == owner)
                return true;
        return _workers.Count < 2;
    }

    public bool Reserve(FactionNpc worker)
    {
        if (worker == null || worker.Faction == null || !CanReserve(worker.Faction))
            return false;
        if (!_workers.Contains(worker))
            _workers.Add(worker);
        return true;
    }

    public void Release(FactionNpc worker)
    {
        if (worker != null)
            _workers.Remove(worker);
    }

    public int Gather(FactionNpc worker, float requestedAmount)
    {
        if (worker == null || !_workers.Contains(worker) || !IsAvailable)
            return 0;

        int amount = Mathf.Clamp(Mathf.FloorToInt(requestedAmount), 0, CurrentAmount);
        if (amount <= 0)
            return 0;

        CurrentAmount -= amount;
        if (CurrentAmount <= 0)
        {
            CurrentAmount = 0;
            _cooldownUntil = Time.time + cooldownSeconds;
            _workers.Clear();
        }
        return amount;
    }

    void Update()
    {
        if (_cooldownUntil > 0f && Time.time >= _cooldownUntil)
        {
            CurrentAmount = maximumAmount;
            _cooldownUntil = 0f;
        }
    }
}

/// <summary>Fixed-capacity worker inventory.</summary>
public sealed class WorkerInventory : MonoBehaviour
{
    public int Capacity { get; private set; } = 10;
    public FactionResourceType ResourceType { get; private set; }
    public int Amount { get; private set; }
    public bool IsFull => Amount >= Capacity;
    public bool IsEmpty => Amount <= 0;

    public void Configure(int capacity)
    {
        Capacity = Mathf.Max(1, capacity);
    }

    public int Add(FactionResourceType type, int amount)
    {
        if (amount <= 0 || (!IsEmpty && ResourceType != type))
            return 0;
        ResourceType = type;
        int added = Mathf.Min(amount, Capacity - Amount);
        Amount += added;
        return added;
    }

    public int TakeAll(out FactionResourceType type)
    {
        type = ResourceType;
        int result = Amount;
        Amount = 0;
        return result;
    }
}

/// <summary>
/// Bridges FoliageByColour's runtime pooled visuals to lightweight economic proxies.
/// </summary>
[DisallowMultipleComponent]
public sealed class FoliageResourceAdapter : MonoBehaviour
{
    public static FoliageResourceAdapter Instance { get; private set; }

    [Header("Registration")]
    [Min(1)] public int maximumRegisteredNodes = 800;
    [Min(1f)] public float rescanInterval = 5f;
    public string treeRuleText = "tree";
    public string palmRuleText = "palm";
    public string pineRuleText = "pine";
    public string woodlandRuleText = "woodland";
    public string groveRuleText = "grove";
    public string rockRuleText = "rock";
    public string stoneRuleText = "stone";
    [Min(1)] public int nodeCapacity = 100;
    [Min(0.01f)] public float gatherRate = 1f;
    [Min(0.1f)] public float treeCooldownSeconds = 120f;
    [Min(0.1f)] public float rockCooldownSeconds = 180f;

    FoliageByColour _foliage;
    float _nextScan;
    readonly Dictionary<GameObject, ResourceNode> _sources = new Dictionary<GameObject, ResourceNode>(512);

    public static FoliageResourceAdapter EnsureExists(FoliageByColour foliage)
    {
        if (Instance != null)
        {
            Instance.Attach(foliage);
            return Instance;
        }

        GameObject go = new GameObject("FoliageResourceAdapter");
        Instance = go.AddComponent<FoliageResourceAdapter>();
        Instance.Attach(foliage);
        return Instance;
    }

    void OnDestroy()
    {
        if (_foliage != null)
            _foliage.PooledInstanceCreated -= OnPooledInstanceCreated;
        if (Instance == this)
            Instance = null;
    }

    void Attach(FoliageByColour foliage)
    {
        if (foliage == null || _foliage == foliage)
            return;
        if (_foliage != null)
            _foliage.PooledInstanceCreated -= OnPooledInstanceCreated;
        _foliage = foliage;
        _foliage.PooledInstanceCreated += OnPooledInstanceCreated;
        ScanExisting();
    }

    void Update()
    {
        if (Time.time < _nextScan)
            return;
        _nextScan = Time.time + Mathf.Max(1f, rescanInterval);
        ScanExisting();
        PruneDestroyedSources();
    }

    void OnPooledInstanceCreated(GameObject source, string ruleName)
    {
        RegisterSource(source, ruleName);
    }

    void ScanExisting()
    {
        if (_foliage == null || _sources.Count >= maximumRegisteredNodes)
            return;
        // FoliageByColour parents each pooled source directly under a Foliage_<rule> container.
        // Inspect only those shallow containers; walking every model bone would defeat the
        // bounded registration and can be very expensive on a large streamed planet.
        Transform foliageRoot = _foliage.transform;
        for (int c = 0; c < foliageRoot.childCount && _sources.Count < maximumRegisteredNodes; c++)
        {
            Transform container = foliageRoot.GetChild(c);
            if (container == null || container.name.IndexOf("Foliage_", System.StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            for (int i = 0; i < container.childCount && _sources.Count < maximumRegisteredNodes; i++)
                RegisterSource(container.GetChild(i).gameObject, container.name, container.GetChild(i));
        }

        if (_sources.Count == 0)
        {
            // Be tolerant of future foliage wrappers that insert one or more transforms
            // between the rule container and the pooled visual. Register only each
            // top-level pooled object, never its model bones.
            Transform[] descendants = _foliage.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < descendants.Length && _sources.Count < maximumRegisteredNodes; i++)
            {
                Transform child = descendants[i];
                Transform container = child != null ? child.parent : null;
                while (container != null && container != foliageRoot &&
                       container.name.IndexOf("Foliage_", System.StringComparison.OrdinalIgnoreCase) < 0)
                    container = container.parent;
                if (container == null || container == foliageRoot ||
                    container.name.IndexOf("Foliage_", System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                Transform topLevel = child;
                while (topLevel.parent != null && topLevel.parent != container)
                    topLevel = topLevel.parent;
                RegisterSource(topLevel.gameObject, container.name, topLevel);
            }
        }
    }

    void RegisterSource(GameObject source, string ruleName, Transform sourceTransform = null)
    {
        if (source == null || _sources.ContainsKey(source))
            return;
        if (!TryResolveType(ruleName, out FactionResourceType type))
            return;

        Vector3 position = sourceTransform != null ? sourceTransform.position : source.transform.position;
        if (_sources.Count >= maximumRegisteredNodes && !TryEvictLeastUsefulNode(position))
            return;

        // The proxy is independent from the foliage object so camera culling does not make
        // economic state disappear. It is harmless and has no collider or renderer.
        GameObject proxy = new GameObject($"Resource_{type}_{_sources.Count:000}");
        proxy.transform.SetParent(transform, false);
        proxy.transform.position = position;
        var node = proxy.AddComponent<ResourceNode>();
        node.Configure(type, nodeCapacity, gatherRate,
            type == FactionResourceType.Wood ? treeCooldownSeconds : rockCooldownSeconds);
        _sources.Add(source, node);
    }

    bool TryEvictLeastUsefulNode(Vector3 incomingPosition)
    {
        float incomingFactionSq = NearestFactionDistanceSq(incomingPosition);
        GameObject worst = null;
        ResourceNode worstNode = null;
        float worstFactionSq = -1f;
        foreach (var pair in _sources)
        {
            if (pair.Key == null || pair.Value == null)
                continue;
            float d = NearestFactionDistanceSq(pair.Value.transform.position);
            if (d > worstFactionSq)
            {
                worstFactionSq = d;
                worst = pair.Key;
                worstNode = pair.Value;
            }
        }

        if (worst == null || worstFactionSq <= incomingFactionSq)
            return false;
        if (worstNode != null)
            Destroy(worstNode.gameObject);
        _sources.Remove(worst);
        return true;
    }

    static float NearestFactionDistanceSq(Vector3 position)
    {
        float best = float.PositiveInfinity;
        IReadOnlyList<FactionController> factions = FactionRegistry.Factions;
        for (int i = 0; i < factions.Count; i++)
        {
            FactionController faction = factions[i];
            if (faction == null)
                continue;
            float d = (faction.GetSafePosition() - position).sqrMagnitude;
            if (d < best)
                best = d;
        }
        return best;
    }

    bool TryResolveType(string ruleName, out FactionResourceType type)
    {
        string value = ruleName ?? string.Empty;
        if (ContainsAny(value, treeRuleText, palmRuleText, pineRuleText, woodlandRuleText, groveRuleText))
        {
            type = FactionResourceType.Wood;
            return true;
        }
        if (ContainsAny(value, rockRuleText, stoneRuleText))
        {
            type = FactionResourceType.Stone;
            return true;
        }
        type = default;
        return false;
    }

    static bool ContainsAny(string value, params string[] tokens)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];
            if (string.IsNullOrEmpty(token))
                continue;
            if (value.IndexOf(token, System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    void PruneDestroyedSources()
    {
        var dead = new List<GameObject>();
        foreach (var pair in _sources)
        {
            if (pair.Key == null)
            {
                if (pair.Value != null)
                    Destroy(pair.Value.gameObject);
                dead.Add(pair.Key);
            }
            else if (pair.Value != null)
            {
                pair.Value.transform.position = pair.Key.transform.position;
            }
        }
        for (int i = 0; i < dead.Count; i++)
            _sources.Remove(dead[i]);
    }

    public int RegisteredNodeCount => _sources.Count;
}
