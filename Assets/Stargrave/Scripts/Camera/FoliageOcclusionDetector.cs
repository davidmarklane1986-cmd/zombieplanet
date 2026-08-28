using System.Collections.Generic;
using UnityEngine;

namespace Stargrave.CameraOcclusion
{
    /// <summary>
    /// Finds registered foliage. The shader performs the per-pixel circle and
    /// camera-depth test, so projected bounds do not gate the cutout.
    /// </summary>
    public sealed class FoliageOcclusionDetector
    {
        const float DepthMargin = 0.08f;
        readonly HashSet<EntityId> _seenIds = new HashSet<EntityId>();

        public int CollectScreenCircleOccluders(
            Camera cam,
            Vector3 playerCenter,
            Vector2 screenCenterViewport,
            float screenRadiusViewport,
            ICollection<FoliageOccluder> results)
        {
            results.Clear();
            _seenIds.Clear();

            if (cam == null || screenRadiusViewport <= 0f)
                return 0;

            var active = FoliageOccluder.ActiveOccluders;
            for (int i = 0; i < active.Count; i++)
            {
                var occluder = active[i];
                if (occluder == null || !IsEligible(occluder))
                    continue;

                if (occluder.Renderers == null || occluder.Renderers.Length == 0)
                    continue;

                EntityId id = occluder.GetEntityId();
                if (_seenIds.Add(id))
                    results.Add(occluder);
            }

            return results.Count;
        }

        static bool BoundsOverlapsForegroundCircle(
            Camera cam,
            Bounds bounds,
            float playerDepth,
            Vector2 screenCenterViewport,
            float screenRadiusViewport)
        {
            Vector3 center = bounds.center;
            Vector3 extents = bounds.extents;
            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;
            float nearestDepth = float.MaxValue;
            bool hasProjectedPoint = false;

            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sy = -1; sy <= 1; sy += 2)
                {
                    for (int sz = -1; sz <= 1; sz += 2)
                    {
                        Vector3 corner = center + new Vector3(
                            extents.x * sx,
                            extents.y * sy,
                            extents.z * sz);
                        Vector3 viewport = cam.WorldToViewportPoint(corner);
                        if (viewport.z <= 0f)
                            continue;

                        hasProjectedPoint = true;
                        nearestDepth = Mathf.Min(nearestDepth, viewport.z);
                        float x = (viewport.x - screenCenterViewport.x) * cam.aspect;
                        float y = viewport.y - screenCenterViewport.y;
                        minX = Mathf.Min(minX, x);
                        minY = Mathf.Min(minY, y);
                        maxX = Mathf.Max(maxX, x);
                        maxY = Mathf.Max(maxY, y);
                    }
                }
            }

            if (!hasProjectedPoint || nearestDepth >= playerDepth - DepthMargin)
                return false;

            float nearestX = Mathf.Clamp(0f, minX, maxX);
            float nearestY = Mathf.Clamp(0f, minY, maxY);
            return nearestX * nearestX + nearestY * nearestY
                <= screenRadiusViewport * screenRadiusViewport;
        }

        static bool IsEligible(FoliageOccluder occluder)
        {
            var go = occluder.gameObject;
            if (!go.activeInHierarchy)
                return false;

            if (go.CompareTag("Planet") || go.CompareTag("Player") || go.CompareTag("Water"))
                return false;
            if (occluder.GetComponentInParent<PlanetMotor_InputSystem>() != null)
                return false;
            if (occluder.GetComponentInParent<PlayerHealth>() != null)
                return false;
            if (occluder.GetComponentInParent<ZombieAI>() != null)
                return false;

            return true;
        }
    }
}
