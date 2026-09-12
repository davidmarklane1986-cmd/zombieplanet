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
    public bool IsAvailable
    {
        get
        {
            TryRefill();
            return CurrentAmount > 0;
        }
    }
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

    /// <summary>Swarm sim harvest without a FactionNpc reservation slot.</summary>
    public int SwarmGather(FactionController owner, int requestedAmount)
    {
        if (owner == null || !IsAvailable || requestedAmount <= 0)
            return 0;

        // Swarm does not use MonoBehaviour reservation slots — many capsules may share a rock/tree.
        int amount = Mathf.Min(requestedAmount, CurrentAmount);
        CurrentAmount -= amount;
        if (CurrentAmount <= 0)
        {
            CurrentAmount = 0;
            _cooldownUntil = Time.time + cooldownSeconds;
            _workers.Clear();
        }
        return amount;
    }

    void TryRefill()
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
    int _scanContainer = -1;
    int _scanChild;
    int _syncCursor;
    readonly Dictionary<GameObject, ResourceNode> _sources = new Dictionary<GameObject, ResourceNode>(512);
    readonly List<GameObject> _sourceKeys = new List<GameObject>(512);
    readonly List<GameObject> _deadSources = new List<GameObject>(16);

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
        SyncSlice();
        if (_scanContainer >= 0)
        {
            ScanNextContainer();
            return;
        }
        if (Time.time < _nextScan)
            return;
        _nextScan = Time.time + Mathf.Max(1f, rescanInterval);
        _scanContainer = 0;
        ScanNextContainer();
    }

    void OnPooledInstanceCreated(GameObject source, string ruleName)
    {
        RegisterSource(source, ruleName);
    }

    void ScanNextContainer()
    {
        if (_foliage == null)
        {
            _scanContainer = -1;
            _scanChild = 0;
            return;
        }

        Transform foliageRoot = _foliage.transform;
        const int childBudget = 12;
        int done = 0;
        while (_scanContainer < foliageRoot.childCount && done < childBudget)
        {
            Transform container = foliageRoot.GetChild(_scanContainer);
            if (container == null ||
                container.name.IndexOf("Foliage_", System.StringComparison.OrdinalIgnoreCase) < 0)
            {
                _scanContainer++;
                _scanChild = 0;
                continue;
            }

            while (_scanChild < container.childCount && done < childBudget)
            {
                RegisterSource(container.GetChild(_scanChild).gameObject, container.name, container.GetChild(_scanChild));
                _scanChild++;
                done++;
            }

            if (_scanChild >= container.childCount)
            {
                _scanContainer++;
                _scanChild = 0;
            }
        }

        if (_scanContainer >= foliageRoot.childCount)
        {
            _scanContainer = -1;
            _scanChild = 0;
        }
    }

    void ScanExisting()
    {
        if (_foliage == null)
            return;
        // FoliageByColour parents each pooled source directly under a Foliage_<rule> container.
        // Inspect only those shallow containers; walking every model bone would defeat the
        // bounded registration and can be very expensive on a large streamed planet.
        Transform foliageRoot = _foliage.transform;
        for (int c = 0; c < foliageRoot.childCount; c++)
        {
            Transform container = foliageRoot.GetChild(c);
            if (container == null || container.name.IndexOf("Foliage_", System.StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            for (int i = 0; i < container.childCount; i++)
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
        if (_sources.Count >= maximumRegisteredNodes && !TryEvictLeastUsefulNode(position, type))
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
        _sourceKeys.Add(source);
    }

    bool TryEvictLeastUsefulNode(Vector3 incomingPosition, FactionResourceType incomingType)
    {
        float incomingFactionSq = NearestFactionDistanceSq(incomingPosition);
        int stoneQuota = Mathf.Max(40, maximumRegisteredNodes / 8);
        int stoneCount = 0;
        foreach (var pair in _sources)
        {
            if (pair.Value != null && pair.Value.ResourceType == FactionResourceType.Stone)
                stoneCount++;
        }

        bool needStoneSlots = incomingType == FactionResourceType.Stone && stoneCount < stoneQuota;
        GameObject worst = null;
        ResourceNode worstNode = null;
        float worstFactionSq = -1f;
        foreach (var pair in _sources)
        {
            if (pair.Key == null || pair.Value == null)
                continue;
            if (needStoneSlots && pair.Value.ResourceType == FactionResourceType.Stone)
                continue;

            float d = NearestFactionDistanceSq(pair.Value.transform.position);
            if (needStoneSlots && pair.Value.ResourceType == FactionResourceType.Wood)
                d += 1e10f;
            if (d > worstFactionSq)
            {
                worstFactionSq = d;
                worst = pair.Key;
                worstNode = pair.Value;
            }
        }

        if (worst == null)
            return false;
        if (!needStoneSlots && worstFactionSq <= incomingFactionSq)
            return false;
        if (worstNode != null)
            Destroy(worstNode.gameObject);
        _sources.Remove(worst);
        _sourceKeys.Remove(worst);
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

    void SyncSlice()
    {
        int n = _sourceKeys.Count;
        if (n == 0)
            return;
        if (_syncCursor >= n)
            _syncCursor = 0;

        const int budget = 16;
        _deadSources.Clear();
        for (int b = 0; b < budget && _sourceKeys.Count > 0; b++)
        {
            if (_syncCursor >= _sourceKeys.Count)
                _syncCursor = 0;
            GameObject key = _sourceKeys[_syncCursor];
            if (key == null || !_sources.TryGetValue(key, out ResourceNode node) || node == null)
            {
                _deadSources.Add(key);
                _syncCursor++;
                continue;
            }

            node.transform.position = key.transform.position;
            _syncCursor++;
        }

        for (int i = 0; i < _deadSources.Count; i++)
        {
            GameObject key = _deadSources[i];
            if (key != null && _sources.TryGetValue(key, out ResourceNode node) && node != null)
                Destroy(node.gameObject);
            _sources.Remove(key);
            _sourceKeys.Remove(key);
        }
    }

    public int RegisteredNodeCount => _sources.Count;
}
