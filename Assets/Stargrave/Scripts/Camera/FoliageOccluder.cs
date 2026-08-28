using System.Collections.Generic;
using UnityEngine;

namespace Stargrave.CameraOcclusion
{
    [DisallowMultipleComponent]
    public sealed class FoliageOccluder : MonoBehaviour
    {
        public const string FoliageLayerName = "Foliage";

        static readonly List<FoliageOccluder> Active = new List<FoliageOccluder>(256);

        public static IReadOnlyList<FoliageOccluder> ActiveOccluders => Active;

        [SerializeField] Renderer[] renderers;
        [SerializeField] float bakedMaxWorldSize;

        BoxCollider _trigger;

        public Renderer[] Renderers => renderers;
        public float BakedMaxWorldSize => bakedMaxWorldSize;

        void OnEnable()
        {
            if (!Active.Contains(this))
                Active.Add(this);
            if (renderers == null || renderers.Length == 0)
                Refresh();
        }

        void OnDisable()
        {
            Active.Remove(this);
        }

        public static FoliageOccluder EnsureOn(GameObject root)
        {
            if (root == null)
                return null;

            var occluder = root.GetComponent<FoliageOccluder>();
            if (occluder == null)
                occluder = root.AddComponent<FoliageOccluder>();
            occluder.Refresh();
            return occluder;
        }

        public void Refresh()
        {
            renderers = GetComponentsInChildren<Renderer>(true);
            bakedMaxWorldSize = 0f;

            if (renderers != null)
            {
                for (int i = 0; i < renderers.Length; i++)
                {
                    var r = renderers[i];
                    if (r == null)
                        continue;
                    float s = r.bounds.size.magnitude;
                    if (s > bakedMaxWorldSize)
                        bakedMaxWorldSize = s;
                }
            }

            EnsureTriggerCollider();
        }

        void EnsureTriggerCollider()
        {
            if (!TryGetWorldBounds(out Bounds bounds))
                return;

            _trigger = GetComponent<BoxCollider>();
            if (_trigger == null)
                _trigger = gameObject.AddComponent<BoxCollider>();
            _trigger.isTrigger = true;

            int foliageLayer = LayerMask.NameToLayer(FoliageLayerName);
            if (foliageLayer >= 0)
                gameObject.layer = foliageLayer;

            Vector3 localCenter = transform.InverseTransformPoint(bounds.center);
            Vector3 lossy = transform.lossyScale;
            Vector3 localSize = new Vector3(
                bounds.size.x / Mathf.Max(0.0001f, lossy.x),
                bounds.size.y / Mathf.Max(0.0001f, lossy.y),
                bounds.size.z / Mathf.Max(0.0001f, lossy.z));
            _trigger.center = localCenter;
            _trigger.size = localSize + Vector3.one * 0.15f;

            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null)
                    continue;
                r.gameObject.layer = LayerMask.NameToLayer("Default");
            }
        }

        public bool TryGetWorldBounds(out Bounds bounds)
        {
            bounds = default;
            if (renderers == null || renderers.Length == 0)
                return false;

            bool any = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null || !r)
                    continue;
                if (!any)
                {
                    bounds = r.bounds;
                    any = true;
                }
                else
                    bounds.Encapsulate(r.bounds);
            }

            return any;
        }
    }
}
