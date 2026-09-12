using System.Collections.Generic;
using UnityEngine;
using Stargrave.Rts2;

/// <summary>
/// BAR territory loop: metal pockets, growing influence, coarse nav-cell ownership, trade corridor queries.
/// V1 uses debug markers only (no territory shader).
/// </summary>
[DisallowMultipleComponent]
public sealed class TerritorySystem : MonoBehaviour
{
    public static TerritorySystem Instance { get; private set; }
    public static bool HasInstance => Instance != null;

    public struct Pocket
    {
        public int id;
        public Vector3 axis;
        public Vector3 position;
        public int richness; // 1..3
        public int ownerFactionId; // -1 neutral
        public int mexInstanceId; // linked Mex entity id, 0 if none
        public float clearedAt; // Time.time when ownership cleared
        public int reservedFactionId; // -1 none; expansion mex under construction
        public float reservedAt;
    }

    public struct Cell
    {
        public int ownerFactionId; // -1 neutral, -2 contested
        public float bestInfluence;
        public float secondInfluence;
    }

    public readonly List<Pocket> Pockets = new List<Pocket>(64);
    Cell[] _cells = System.Array.Empty<Cell>();
    float[] _factionRadius = new float[32];
    PlanetNavGraph _nav;
    FactionSimulation _sim;
    Planet _planet;
    Vector3 _planetCenter;
    float _nextCellAssign;
    float _nextMarkerRefresh;
    readonly List<GameObject> _markers = new List<GameObject>(128);
    readonly List<Renderer> _reservedPulse = new List<Renderer>(32);
    Transform _markerRoot;
    int _nextPocketId = 1;

    public bool IsReady { get; private set; }

    const int ContestedId = -2;
    const int NeutralId = -1;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    void OnDestroy()
    {
        ClearMarkers();
        if (Instance == this)
            Instance = null;
    }

    public void Configure(FactionSimulation sim, PlanetNavGraph nav)
    {
        _sim = sim;
        _nav = nav;
        _planet = sim != null ? sim.planet : null;
        _planetCenter = _planet != null ? _planet.transform.position : Vector3.zero;
        if (nav != null && nav.Nodes != null && _cells.Length != nav.Nodes.Length)
            _cells = new Cell[nav.Nodes.Length];
        EnsureFactionRadiusCapacity();
    }

    public void BakePockets(FactionSimulation sim, PlanetNavGraph nav)
    {
        Configure(sim, nav);
        Pockets.Clear();
        IsReady = false;
        if (sim == null || sim.planet == null || sim.economy == null)
            return;

        FactionEconomySettings eco = sim.economy;
        int target = Mathf.Clamp(eco.territoryPocketCount, 8, 80);
        float minSep = Mathf.Max(40f, eco.territoryPocketMinSeparation);
        PlanetOceanLayer ocean = sim.planet.GetComponent<PlanetOceanLayer>();
        float water = ocean != null ? ocean.ResolveOceanRadiusWorld() + 1.25f : 0f;

        int attempts = target * 40;
        for (int i = 0; i < attempts && Pockets.Count < target; i++)
        {
            Vector3 axis = Random.onUnitSphere;
            float r = sim.planet.GetSurfaceRadiusWorld(axis);
            if (r < water)
                continue;
            if (!BuildingPlacementSystem.IsDryMainlandCampus(sim.planet, axis, campusRadius: 40f))
                continue;
            Vector3 pos = _planetCenter + axis.normalized * r;
            if (TooClose(pos, minSep))
                continue;

            int richness = 1 + (i % 3);
            Pockets.Add(new Pocket
            {
                id = _nextPocketId++,
                axis = axis.normalized,
                position = pos,
                richness = richness,
                ownerFactionId = NeutralId,
                mexInstanceId = 0,
                clearedAt = 0f,
                reservedFactionId = NeutralId,
                reservedAt = 0f
            });
        }

        IsReady = Pockets.Count > 0;
        EnsureFactionRadiusCapacity();
        RefreshMarkers(force: true);
        if (sim.verboseEvents)
            Debug.Log($"[Territory] Baked {Pockets.Count} metal pockets.", this);
    }

    bool TooClose(Vector3 pos, float minSep)
    {
        float minSq = minSep * minSep;
        for (int i = 0; i < Pockets.Count; i++)
        {
            if ((Pockets[i].position - pos).sqrMagnitude < minSq)
                return true;
        }
        return false;
    }

    void EnsureFactionRadiusCapacity()
    {
        int n = Mathf.Max(8, FactionRegistry.Factions.Count + 4);
        if (_factionRadius.Length < n)
            System.Array.Resize(ref _factionRadius, n);
    }

    public void Tick(float deltaTime)
    {
        if (!IsReady || _sim == null || !_sim.useBarEconomyMode || deltaTime <= 0f)
            return;

        GrowInfluence(deltaTime);
        if (Time.time >= _nextCellAssign)
        {
            _nextCellAssign = Time.time + Mathf.Max(0.5f, _sim.economy.territoryCellAssignInterval);
            AssignCells();
        }
        if (Time.time >= _nextMarkerRefresh)
        {
            _nextMarkerRefresh = Time.time + 2.5f;
            RefreshMarkers(force: true);
        }
        PulseReservedMarkers();
    }

    void GrowInfluence(float deltaTime)
    {
        EnsureFactionRadiusCapacity();
        FactionEconomySettings eco = _sim.economy;
        float baseGrowth = Mathf.Max(0.1f, eco.territoryInfluenceGrowthPerSecond);
        float maxR = Mathf.Max(40f, eco.territoryInfluenceMaxRadius);
        float startR = Mathf.Max(10f, eco.territoryInfluenceStartRadius);

        IReadOnlyList<FactionController> factions = FactionRegistry.Factions;
        for (int i = 0; i < factions.Count; i++)
        {
            FactionController f = factions[i];
            if (f == null || !f.HasFoundedCampus)
                continue;
            int id = f.RuntimeIndex;
            if (id < 0 || id >= _factionRadius.Length)
                continue;
            if (_factionRadius[id] <= 0f)
                _factionRadius[id] = startR;

            float growth = baseGrowth * f.Personality.InfluenceGrowthMul;
            // Extra growth when holding pockets / towns.
            growth *= 1f + 0.08f * CountOwnedPockets(id) + 0.05f * f.CountOwnedTowns();
            _factionRadius[id] = Mathf.Min(maxR, _factionRadius[id] + growth * deltaTime);
        }
    }

    public float GetInfluenceRadius(int factionId)
    {
        if (factionId < 0 || factionId >= _factionRadius.Length)
            return 0f;
        return _factionRadius[factionId];
    }

    public float SampleInfluence(int factionId, Vector3 worldPos)
    {
        FactionController faction = FactionById(factionId);
        if (faction == null || !faction.HasFoundedCampus)
            return 0f;

        float radius = GetInfluenceRadius(factionId);
        if (radius <= 1f)
            return 0f;

        float best = 0f;
        void Acc(Vector3 seedPos, float weight)
        {
            float dist = Vector3.Distance(seedPos, worldPos);
            if (dist >= radius)
                return;
            float t = 1f - dist / radius;
            best = Mathf.Max(best, t * t * weight);
        }

        Acc(faction.GetSafePosition(), 1f);
        if (faction.TownHall != null)
            Acc(faction.TownHall.transform.position, 1.1f);
        IReadOnlyList<ClaimableTown> towns = FactionRegistry.Towns;
        for (int i = 0; i < towns.Count; i++)
        {
            if (towns[i] != null && towns[i].Owner == faction)
                Acc(towns[i].transform.position, 0.85f);
        }
        for (int i = 0; i < Pockets.Count; i++)
        {
            if (Pockets[i].ownerFactionId == factionId)
                Acc(Pockets[i].position, 0.7f + 0.15f * Pockets[i].richness);
        }
        return best;
    }

    void AssignCells()
    {
        if (_nav == null || _nav.Nodes == null || _nav.Nodes.Length == 0)
            return;
        if (_cells.Length != _nav.Nodes.Length)
            _cells = new Cell[_nav.Nodes.Length];

        FactionEconomySettings eco = _sim.economy;
        float stallBand = Mathf.Clamp(eco.territoryContestedInfluenceEpsilon, 0.02f, 0.35f);
        IReadOnlyList<FactionController> factions = FactionRegistry.Factions;

        // Coarser stride — full densify is not worth a hitch every interval.
        int stride = Mathf.Max(8, _nav.Nodes.Length / 120);
        float t0 = Time.realtimeSinceStartup;
        const float budgetMs = 1.5f;

        for (int i = 0; i < _nav.Nodes.Length; i += stride)
        {
            if (((i / stride) & 15) == 15 &&
                (Time.realtimeSinceStartup - t0) * 1000f > budgetMs)
                break;

            Vector3 pos = _nav.WorldPosition(i);
            float best = 0f;
            float second = 0f;
            int bestId = NeutralId;
            for (int f = 0; f < factions.Count; f++)
            {
                FactionController fc = factions[f];
                if (fc == null || !fc.HasFoundedCampus)
                    continue;
                float inf = SampleInfluence(fc.RuntimeIndex, pos);
                if (inf > best)
                {
                    second = best;
                    best = inf;
                    bestId = fc.RuntimeIndex;
                }
                else if (inf > second)
                {
                    second = inf;
                }
            }

            int owner = NeutralId;
            if (best > 0.05f)
            {
                if (best - second <= stallBand && second > 0.04f)
                    owner = ContestedId;
                else
                    owner = bestId;
            }

            var cell = new Cell
            {
                ownerFactionId = owner,
                bestInfluence = best,
                secondInfluence = second
            };
            for (int j = i; j < Mathf.Min(_nav.Nodes.Length, i + stride); j++)
                _cells[j] = cell;
        }
    }

    public int GetOwnerAt(Vector3 worldPos)
    {
        if (_nav == null || _cells.Length == 0)
            return NeutralId;
        int node = _nav.FindNearestNode(worldPos);
        if (node < 0 || node >= _cells.Length)
            return NeutralId;
        return _cells[node].ownerFactionId;
    }

    public bool IsOwnedBy(Vector3 worldPos, int factionId) =>
        GetOwnerAt(worldPos) == factionId;

    public bool IsContested(Vector3 worldPos) =>
        GetOwnerAt(worldPos) == ContestedId;

    /// <summary>Fraction of samples owned by faction (0..1). Contested/hostile reduce score.</summary>
    public float OwnedFractionAlong(int factionId, IReadOnlyList<Vector3> waypoints)
    {
        if (waypoints == null || waypoints.Count == 0 || factionId < 0)
            return 0f;
        int owned = 0;
        int total = 0;
        for (int i = 0; i < waypoints.Count; i++)
        {
            int owner = GetOwnerAt(waypoints[i]);
            total++;
            if (owner == factionId)
                owned++;
        }
        return total > 0 ? owned / (float)total : 0f;
    }

    public float OwnedFractionAlongSegment(int factionId, Vector3 from, Vector3 to, int samples = 6)
    {
        samples = Mathf.Clamp(samples, 2, 16);
        int owned = 0;
        for (int i = 0; i < samples; i++)
        {
            float t = i / (float)(samples - 1);
            Vector3 p = Vector3.Slerp(from.normalized, to.normalized, t);
            // Prefer world positions if we have planet.
            Vector3 world = from;
            if (_planet != null)
            {
                Vector3 axis = ((1f - t) * (from - _planetCenter) + t * (to - _planetCenter)).normalized;
                world = _planet.GetSurfacePointWorld(axis);
            }
            else
                world = Vector3.Lerp(from, to, t);
            if (GetOwnerAt(world) == factionId)
                owned++;
        }
        return owned / (float)samples;
    }

    public bool TryFindBestPocketFor(FactionController faction, out int pocketIndex)
    {
        pocketIndex = -1;
        if (faction == null || !IsReady)
            return false;

        float greed = faction.Personality.PocketGreed;
        Vector3 home = faction.GetSafePosition();
        float best = float.PositiveInfinity;
        float cd = Mathf.Max(5f, faction.Economy.territoryPocketReclaimCooldownSeconds);
        const float reserveTimeout = 180f;

        for (int i = 0; i < Pockets.Count; i++)
        {
            Pocket p = Pockets[i];
            if (p.ownerFactionId == faction.RuntimeIndex)
                continue;
            if (p.ownerFactionId >= 0)
                continue; // only unowned for expansion claim (rivals need mex destroyed first)
            if (p.clearedAt > 0f && Time.time < p.clearedAt + cd)
                continue;
            // Another faction already building here.
            if (p.reservedFactionId >= 0 &&
                p.reservedFactionId != faction.RuntimeIndex &&
                Time.time < p.reservedAt + reserveTimeout)
                continue;
            // Stale self-reservation: allow re-pick.
            if (p.reservedFactionId == faction.RuntimeIndex &&
                Time.time >= p.reservedAt + reserveTimeout)
            {
                p.reservedFactionId = NeutralId;
                p.reservedAt = 0f;
                Pockets[i] = p;
            }

            float d = (p.position - home).sqrMagnitude;
            // Prefer richer pockets slightly (greed).
            float score = d / Mathf.Max(0.5f, greed * (0.75f + 0.25f * p.richness));
            if (score < best)
            {
                best = score;
                pocketIndex = i;
            }
        }
        return pocketIndex >= 0;
    }

    public bool TryReservePocket(int index, FactionController faction)
    {
        if (faction == null || index < 0 || index >= Pockets.Count)
            return false;
        Pocket p = Pockets[index];
        if (p.ownerFactionId >= 0 && p.ownerFactionId != faction.RuntimeIndex)
            return false;
        if (p.reservedFactionId >= 0 &&
            p.reservedFactionId != faction.RuntimeIndex &&
            Time.time < p.reservedAt + 180f)
            return false;
        p.reservedFactionId = faction.RuntimeIndex;
        p.reservedAt = Time.time;
        Pockets[index] = p;
        return true;
    }

    public int FindPocketIndexById(int pocketId)
    {
        if (pocketId <= 0)
            return -1;
        for (int i = 0; i < Pockets.Count; i++)
        {
            if (Pockets[i].id == pocketId)
                return i;
        }
        return -1;
    }

    public bool TryGetPocket(int index, out Pocket pocket)
    {
        if (index < 0 || index >= Pockets.Count)
        {
            pocket = default;
            return false;
        }
        pocket = Pockets[index];
        return true;
    }

    public int FindNearestPocketIndex(Vector3 worldPos, float maxDist)
    {
        float maxSq = maxDist * maxDist;
        int best = -1;
        float bestSq = float.PositiveInfinity;
        for (int i = 0; i < Pockets.Count; i++)
        {
            float d = (Pockets[i].position - worldPos).sqrMagnitude;
            if (d <= maxSq && d < bestSq)
            {
                bestSq = d;
                best = i;
            }
        }
        return best;
    }

    public bool TryClaimPocketWithMex(FactionController faction, Mex mex)
    {
        if (faction == null || mex == null || !IsReady)
            return false;

        int idx = -1;
        if (mex.LinkedPocketId > 0)
            idx = FindPocketIndexById(mex.LinkedPocketId);
        if (idx < 0)
        {
            float claimR = Mathf.Max(8f, faction.Economy.territoryPocketClaimRadius);
            idx = FindNearestPocketIndex(mex.transform.position, claimR);
        }
        if (idx < 0)
            return false;

        Pocket p = Pockets[idx];
        if (p.ownerFactionId >= 0 && p.ownerFactionId != faction.RuntimeIndex)
            return false;
        float cd = Mathf.Max(5f, faction.Economy.territoryPocketReclaimCooldownSeconds);
        if (p.clearedAt > 0f && Time.time < p.clearedAt + cd && p.ownerFactionId < 0)
            return false;

        p.ownerFactionId = faction.RuntimeIndex;
        p.mexInstanceId = (int)EntityId.ToULong(mex.GetEntityId());
        p.reservedFactionId = NeutralId;
        p.reservedAt = 0f;
        Pockets[idx] = p;
        mex.LinkedPocketId = p.id;
        if (_sim != null && _sim.verboseEvents)
            Debug.Log($"[Territory] {faction.DisplayName} claimed pocket {p.id} (richness {p.richness}).", this);
        return true;
    }

    public void NotifyMexDestroyed(Mex mex)
    {
        if (mex == null)
            return;
        int instanceId = (int)EntityId.ToULong(mex.GetEntityId());
        for (int i = 0; i < Pockets.Count; i++)
        {
            Pocket p = Pockets[i];
            bool matchInstance = p.mexInstanceId != 0 && p.mexInstanceId == instanceId;
            bool matchLinked = mex.LinkedPocketId > 0 && p.id == mex.LinkedPocketId;
            if (!matchInstance && !matchLinked)
                continue;
            int prevOwner = p.ownerFactionId;
            p.ownerFactionId = NeutralId;
            p.mexInstanceId = 0;
            p.reservedFactionId = NeutralId;
            p.reservedAt = 0f;
            p.clearedAt = Time.time;
            Pockets[i] = p;
            if (_sim != null && _sim.verboseEvents && prevOwner >= 0)
                Debug.Log($"[Territory] Pocket {p.id} freed (mex destroyed).", this);
        }
    }

    public int CountOwnedPockets(int factionId)
    {
        int n = 0;
        for (int i = 0; i < Pockets.Count; i++)
        {
            if (Pockets[i].ownerFactionId == factionId)
                n++;
        }
        return n;
    }

    public float PocketIncomeBonus(FactionController faction)
    {
        if (faction == null || faction.Economy == null)
            return 0f;
        float bonus = 0f;
        float per = Mathf.Max(0f, faction.Economy.territoryPocketMetalBonusPerSecond);
        for (int i = 0; i < Pockets.Count; i++)
        {
            if (Pockets[i].ownerFactionId != faction.RuntimeIndex)
                continue;
            bonus += per * (0.75f + 0.25f * Pockets[i].richness);
        }
        return bonus;
    }

    static FactionController FactionById(int id)
    {
        IReadOnlyList<FactionController> factions = FactionRegistry.Factions;
        for (int i = 0; i < factions.Count; i++)
        {
            if (factions[i] != null && factions[i].RuntimeIndex == id)
                return factions[i];
        }
        return null;
    }

    void ClearMarkers()
    {
        _reservedPulse.Clear();
        for (int i = 0; i < _markers.Count; i++)
        {
            if (_markers[i] != null)
                Destroy(_markers[i]);
        }
        _markers.Clear();
        if (_markerRoot != null)
            Destroy(_markerRoot.gameObject);
        _markerRoot = null;
    }

    void PulseReservedMarkers()
    {
        if (_reservedPulse.Count == 0)
            return;
        float pulse = 0.45f + 0.55f * Mathf.Abs(Mathf.Sin(Time.time * 3.4f));
        Color c = new Color(1f, 0.88f, 0.12f, pulse);
        for (int i = 0; i < _reservedPulse.Count; i++)
        {
            Renderer rend = _reservedPulse[i];
            if (rend != null)
                rend.material.color = c;
        }
    }

    void RefreshMarkers(bool force)
    {
        if (_sim == null || !_sim.useBarEconomyMode || !IsReady)
            return;

        // Always rebuild on the Tick interval (force:true). Cheap enough for V1 debug markers
        // and avoids the old early-out that left pocket colours stuck on Neutral forever.
        ClearMarkers();
        var rootGo = new GameObject("TerritoryMarkers");
        rootGo.transform.SetParent(transform, false);
        _markerRoot = rootGo.transform;

        // Pocket markers (always) — oversized so they read from orbit.
        for (int i = 0; i < Pockets.Count; i++)
        {
            Pocket p = Pockets[i];
            var m = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            m.name = $"Pocket_{p.id}";
            m.transform.SetParent(_markerRoot, false);
            m.transform.position = p.position + (p.axis * (4f + p.richness * 1.5f));
            float s = 6.5f + p.richness * 2.2f;
            m.transform.localScale = Vector3.one * s;
            Object.Destroy(m.GetComponent<Collider>());
            var rend = m.GetComponent<Renderer>();
            if (rend != null)
            {
                if (p.ownerFactionId >= 0)
                    rend.material.color = ColorForOwner(p.ownerFactionId, pocket: true);
                else if (p.reservedFactionId >= 0)
                {
                    rend.material.color = new Color(1f, 0.88f, 0.12f, 0.95f);
                    _reservedPulse.Add(rend);
                }
                else
                    rend.material.color = ColorForOwner(NeutralId, pocket: true);
            }
            _markers.Add(m);
        }

        // Contested / border cell markers — denser + brighter for spectator read.
        if (_nav != null && _cells.Length == _nav.Nodes.Length)
        {
            int step = Mathf.Max(5, _nav.Nodes.Length / 80);
            for (int i = 0; i < _nav.Nodes.Length; i += step)
            {
                int owner = _cells[i].ownerFactionId;
                if (owner == NeutralId)
                    continue;
                // Skip quiet interior; keep contested + soft borders.
                if (owner != ContestedId && _cells[i].secondInfluence < 0.035f)
                    continue;

                bool contested = owner == ContestedId;
                var m = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                m.name = contested ? "BorderContested" : "BorderOwned";
                m.transform.SetParent(_markerRoot, false);
                m.transform.position = _nav.WorldPosition(i) + _nav.Nodes[i].axis * 2.2f;
                m.transform.localScale = Vector3.one * (contested ? 3.4f : 2.1f);
                Object.Destroy(m.GetComponent<Collider>());
                var rend = m.GetComponent<Renderer>();
                if (rend != null)
                    rend.material.color = ColorForOwner(owner, pocket: false);
                _markers.Add(m);
            }
        }
    }

    static Color ColorForOwner(int ownerFactionId, bool pocket)
    {
        if (ownerFactionId == ContestedId)
            return new Color(1f, 0.45f, 0.05f, 0.95f);
        if (ownerFactionId < 0)
            return pocket
                ? new Color(0.72f, 0.72f, 0.68f, 0.92f)
                : new Color(0.45f, 0.45f, 0.45f, 0.55f);
        FactionController f = FactionById(ownerFactionId);
        Color c = f != null ? f.UiColor : Color.white;
        // Punch saturation/brightness so faction colour reads at distance.
        Color.RGBToHSV(c, out float h, out float s, out float v);
        s = Mathf.Clamp01(s * 1.15f + 0.12f);
        v = Mathf.Clamp01(Mathf.Max(v, 0.55f) * 1.1f);
        c = Color.HSVToRGB(h, s, v);
        c.a = pocket ? 0.95f : 0.8f;
        return c;
    }
}
