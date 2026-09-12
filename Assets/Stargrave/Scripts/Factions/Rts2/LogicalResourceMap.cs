using System.Collections.Generic;
using UnityEngine;

namespace Stargrave.Rts2
{
    public struct LogicalResourceSite
    {
        public int id;
        public FactionResourceType type;
        public Vector3 position;
        public Vector3 axis;
        public int remaining;
        public int capacity;
        public bool active;
    }

    /// <summary>
    /// Wood/stone sites baked from biome rules — gatherable even when foliage is not streamed.
    /// </summary>
    public sealed class LogicalResourceMap
    {
        public readonly List<LogicalResourceSite> Sites = new List<LogicalResourceSite>(512);
        Vector3 _planetCenter;
        int _nextId = 1;

        public bool IsReady { get; private set; }

        public static LogicalResourceMap Bake(Planet planet, PlanetOceanLayer ocean, int attempts = 900)
        {
            var map = new LogicalResourceMap();
            map.BakeInternal(planet, ocean, attempts);
            return map;
        }

        void BakeInternal(Planet planet, PlanetOceanLayer ocean, int attempts)
        {
            Sites.Clear();
            IsReady = false;
            if (planet == null)
                return;

            _planetCenter = planet.transform.position;
            float water = ocean != null ? ocean.ResolveOceanRadiusWorld() + 0.5f : 0f;
            int wood = 0;
            int stone = 0;
            const int woodTarget = 180;
            const int stoneTarget = 120;

            for (int i = 0; i < attempts && (wood < woodTarget || stone < stoneTarget); i++)
            {
                Vector3 axis = Random.onUnitSphere;
                float r = planet.GetSurfaceRadiusWorld(axis);
                if (r < water)
                    continue;
                Vector3 pos = _planetCenter + axis * r;

                if (wood < woodTarget &&
                    FactionController.SurfaceSupportsResource(planet, pos, FactionResourceType.Wood) &&
                    !TooClose(pos, FactionResourceType.Wood, 22f))
                {
                    AddSite(FactionResourceType.Wood, pos, axis, 80);
                    wood++;
                }

                if (stone < stoneTarget &&
                    FactionController.SurfaceSupportsResource(planet, pos, FactionResourceType.Stone) &&
                    !TooClose(pos, FactionResourceType.Stone, 28f))
                {
                    AddSite(FactionResourceType.Stone, pos, axis, 60);
                    stone++;
                }
            }

            IsReady = Sites.Count > 0;
        }

        void AddSite(FactionResourceType type, Vector3 pos, Vector3 axis, int capacity)
        {
            Sites.Add(new LogicalResourceSite
            {
                id = _nextId++,
                type = type,
                position = pos,
                axis = axis.normalized,
                remaining = capacity,
                capacity = capacity,
                active = true
            });
        }

        bool TooClose(Vector3 pos, FactionResourceType type, float minDist)
        {
            float minSq = minDist * minDist;
            for (int i = 0; i < Sites.Count; i++)
            {
                if (Sites[i].type != type || !Sites[i].active)
                    continue;
                if ((Sites[i].position - pos).sqrMagnitude < minSq)
                    return true;
            }
            return false;
        }

        public int FindBestSite(
            Vector3 from,
            Vector3 home,
            FactionResourceType type,
            float maxDistFromHome,
            int skipClaimedBy,
            System.Func<int, int, bool> isClaimed)
        {
            float maxHomeSq = maxDistFromHome * maxDistFromHome;
            float best = float.PositiveInfinity;
            int bestId = -1;
            for (int i = 0; i < Sites.Count; i++)
            {
                LogicalResourceSite s = Sites[i];
                if (!s.active || s.remaining <= 0 || s.type != type)
                    continue;
                if ((s.position - home).sqrMagnitude > maxHomeSq)
                    continue;
                if (isClaimed != null && isClaimed(s.id, skipClaimedBy))
                    continue;
                float d = (s.position - from).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    bestId = s.id;
                }
            }
            return bestId;
        }

        public bool TryGetSite(int id, out LogicalResourceSite site)
        {
            for (int i = 0; i < Sites.Count; i++)
            {
                if (Sites[i].id == id)
                {
                    site = Sites[i];
                    return site.active;
                }
            }
            site = default;
            return false;
        }

        public int Gather(int id, int amount)
        {
            for (int i = 0; i < Sites.Count; i++)
            {
                if (Sites[i].id != id || !Sites[i].active)
                    continue;
                LogicalResourceSite s = Sites[i];
                int take = Mathf.Min(amount, s.remaining);
                s.remaining -= take;
                if (s.remaining <= 0)
                    s.active = false;
                Sites[i] = s;
                return take;
            }
            return 0;
        }

        public void RememberStreamHints(FactionController faction, Vector3 home, float radius)
        {
            if (faction == null)
                return;
            float r2 = radius * radius;
            int hints = 0;
            for (int i = 0; i < Sites.Count && hints < 6; i++)
            {
                if (!Sites[i].active)
                    continue;
                if ((Sites[i].position - home).sqrMagnitude > r2)
                    continue;
                faction.RememberStreamFocus(Sites[i].position, true, Sites[i].type);
                hints++;
            }
        }

        /// <summary>Plant extra wood/stone sites around bases so every campus has gather targets.</summary>
        public void EnsureSitesNear(
            Planet planet,
            PlanetOceanLayer ocean,
            IReadOnlyList<Vector3> anchors,
            int woodPerAnchor = 6,
            int stonePerAnchor = 4,
            float ringRadius = 70f)
        {
            if (planet == null || anchors == null || anchors.Count == 0)
                return;

            _planetCenter = planet.transform.position;
            float water = ocean != null ? ocean.ResolveOceanRadiusWorld() + 0.5f : 0f;

            for (int a = 0; a < anchors.Count; a++)
            {
                Vector3 home = anchors[a];
                Vector3 homeAxis = (home - _planetCenter).normalized;
                if (homeAxis.sqrMagnitude < 1e-8f)
                    continue;

                int wood = 0;
                int stone = 0;
                for (int i = 0; i < Sites.Count; i++)
                {
                    if (!Sites[i].active)
                        continue;
                    if ((Sites[i].position - home).sqrMagnitude > ringRadius * ringRadius * 2.25f)
                        continue;
                    if (Sites[i].type == FactionResourceType.Wood) wood++;
                    else if (Sites[i].type == FactionResourceType.Stone) stone++;
                }

                int guard = 0;
                while ((wood < woodPerAnchor || stone < stonePerAnchor) && guard++ < 120)
                {
                    Vector3 jitter = Random.onUnitSphere;
                    float ang = (ringRadius * Random.Range(0.35f, 1.15f)) / Mathf.Max(1f, planet.GetSurfaceRadiusWorld(homeAxis));
                    Vector3 axis = (homeAxis + jitter * ang).normalized;
                    float r = planet.GetSurfaceRadiusWorld(axis);
                    if (r < water)
                        continue;
                    Vector3 pos = _planetCenter + axis * r;

                    if (wood < woodPerAnchor &&
                        FactionController.SurfaceSupportsResource(planet, pos, FactionResourceType.Wood) &&
                        !TooClose(pos, FactionResourceType.Wood, 14f))
                    {
                        AddSite(FactionResourceType.Wood, pos, axis, 80);
                        wood++;
                    }
                    else if (stone < stonePerAnchor &&
                             FactionController.SurfaceSupportsResource(planet, pos, FactionResourceType.Stone) &&
                             !TooClose(pos, FactionResourceType.Stone, 18f))
                    {
                        AddSite(FactionResourceType.Stone, pos, axis, 60);
                        stone++;
                    }
                }
            }

            IsReady = Sites.Count > 0;
        }
    }
}
