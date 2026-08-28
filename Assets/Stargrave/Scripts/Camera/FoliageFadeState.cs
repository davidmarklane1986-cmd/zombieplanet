using UnityEngine;

namespace Stargrave.CameraOcclusion
{
    public sealed class FoliageFadeState
    {
        struct Slot
        {
            public Renderer Renderer;
            public Material[] Originals;
            public Material[] OcclusionMats;
        }

        readonly FoliageOccluder _occluder;
        readonly Slot[] _slots;
        bool _usingOcclusionMats;
        bool _canOcclude;

        public FoliageOccluder Occluder => _occluder;
        public bool WantedThisFrame { get; set; }
        public bool CanOcclude => _canOcclude;
        public bool IsAlive => _occluder != null && _occluder;

        public FoliageFadeState(FoliageOccluder occluder, FoliageFadeMaterialCache cache)
        {
            _occluder = occluder;
            var renderers = occluder != null ? occluder.Renderers : null;
            if (renderers == null || renderers.Length == 0)
            {
                _slots = System.Array.Empty<Slot>();
                return;
            }

            _slots = new Slot[renderers.Length];
            _canOcclude = false;

            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null || !r)
                    continue;

                var originals = r.sharedMaterials;
                if (originals == null || originals.Length == 0)
                    continue;

                var originalsCopy = new Material[originals.Length];
                var occMats = new Material[originals.Length];
                bool any = false;

                for (int m = 0; m < originals.Length; m++)
                {
                    originalsCopy[m] = originals[m];
                    var inst = cache.GetOrCreateOcclusionMaterial(originals[m]);
                    if (inst == null)
                    {
                        occMats[m] = originals[m];
                        continue;
                    }

                    any = true;
                    occMats[m] = inst;
                }

                _slots[i] = new Slot
                {
                    Renderer = r,
                    Originals = originalsCopy,
                    OcclusionMats = occMats
                };

                if (any)
                    _canOcclude = true;
            }
        }

        public void Apply()
        {
            if (!_canOcclude || !IsAlive)
                return;

            if (WantedThisFrame)
                SwapToOcclusionMaterials();
            else
                RestoreOriginals();
        }

        public void ForceRestore()
        {
            RestoreOriginals();
        }

        void SwapToOcclusionMaterials()
        {
            if (_usingOcclusionMats)
                return;

            for (int i = 0; i < _slots.Length; i++)
            {
                ref var slot = ref _slots[i];
                if (slot.Renderer == null || !slot.Renderer || slot.OcclusionMats == null)
                    continue;
                slot.Renderer.sharedMaterials = slot.OcclusionMats;
            }

            _usingOcclusionMats = true;
        }

        void RestoreOriginals()
        {
            if (!_usingOcclusionMats)
                return;

            for (int i = 0; i < _slots.Length; i++)
            {
                ref var slot = ref _slots[i];
                if (slot.Renderer == null || !slot.Renderer || slot.Originals == null)
                    continue;
                slot.Renderer.sharedMaterials = slot.Originals;
            }

            _usingOcclusionMats = false;
        }
    }
}
