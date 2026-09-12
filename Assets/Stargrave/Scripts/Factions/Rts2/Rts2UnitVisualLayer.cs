using UnityEngine;

namespace Stargrave.Rts2
{
    /// <summary>
    /// Near-camera GameObject visuals for Rts2 units using PlayableCharacter prefabs + idle/run anims.
    /// Far units stay as GPU capsules in <see cref="Rts2UnitSim"/>.
    /// Budgeted hard so Instantiates / skinned meshes don't hitch the frame.
    /// </summary>
    public sealed class Rts2UnitVisualLayer
    {
        struct Slot
        {
            public GameObject root;
            public Transform model;
            public GameObject visual;
            public Animator animator;
            public Rts2Role role;
            public byte factionId;
            public Vector3 lastPos;
            public bool running;
            public int idleHash;
            public int runHash;
            public bool hashesReady;
            public bool seated;
            public byte seatAttempts;
        }

        readonly Rts2UnitSim _sim;
        readonly Slot[] _slots = new Slot[Rts2UnitSim.MaxUnits];
        readonly int[] _realized = new int[MaxRealized];
        int _realizedCount;
        Transform _root;
        float _nextRescan;
        Camera _cam;

        // Keep near models rare — Instantiating skinned prefabs is a hitch source.
        const int MaxRealized = 8;
        const int MaxRealizePerRescan = 1;
        const float RescanInterval = 1.25f;
        const float RealizeDistance = 32f;
        const float ReleaseDistance = 48f;
        const float RunSpeedThreshold = 1.2f;
        const float FeetPlantSink = 0.05f;
        const float AnkleToSole = 0.09f;

        static readonly int FallbackIdle = Animator.StringToHash("Idle_Menu");
        static readonly int FallbackRun = Animator.StringToHash("Run_Front");

        public Rts2UnitVisualLayer(Rts2UnitSim sim)
        {
            _sim = sim;
            var go = new GameObject("Rts2UnitVisuals");
            go.transform.SetParent(sim.transform, false);
            _root = go.transform;
        }

        public bool HasVisual(int index) =>
            index >= 0 && index < _slots.Length && _slots[index].root != null;

        public bool HasAny => _realizedCount > 0;

        Vector3 PlantedFeetPosition(int index, Vector3 up)
        {
            Vector3 surface = _sim.GetFeetWorldPosition(index);
            if (up.sqrMagnitude < 1e-8f)
                up = Vector3.up;
            return surface - up.normalized * FeetPlantSink;
        }

        public void Tick(float dt)
        {
            if (_sim == null || !_sim.TryGetFactionSim(out FactionSimulation factionSim))
                return;

            if (_cam == null)
                _cam = Camera.main;
            Vector3 camPos = _cam != null ? _cam.transform.position : Vector3.zero;

            if (Time.time >= _nextRescan)
            {
                _nextRescan = Time.time + RescanInterval;
                RescanRealize(factionSim, camPos);
            }

            for (int r = 0; r < _realizedCount; r++)
            {
                int i = _realized[r];
                if (!_sim.TryGetUnit(i, out Rts2Unit u) || u.alive == 0)
                {
                    Release(i);
                    r--;
                    continue;
                }

                Vector3 bodyPos = _sim.GetWorldPosition(i);
                float dist = (bodyPos - camPos).sqrMagnitude;
                if (dist > ReleaseDistance * ReleaseDistance)
                {
                    Release(i);
                    r--;
                    continue;
                }

                SyncPose(i, ref u, dt);
            }
        }

        void RescanRealize(FactionSimulation factionSim, Vector3 camPos)
        {
            for (int r = _realizedCount - 1; r >= 0; r--)
            {
                int i = _realized[r];
                if (!_sim.TryGetUnit(i, out Rts2Unit u) || u.alive == 0)
                {
                    Release(i);
                    continue;
                }
                float d = (_sim.GetWorldPosition(i) - camPos).sqrMagnitude;
                if (d > ReleaseDistance * ReleaseDistance)
                    Release(i);
            }

            if (_realizedCount >= MaxRealized)
                return;

            float realizeSq = RealizeDistance * RealizeDistance;
            int spawned = 0;
            while (spawned < MaxRealizePerRescan && _realizedCount < MaxRealized)
            {
                float bestSq = realizeSq;
                int best = -1;
                int alive = _sim.UnitSlotCount;
                for (int i = 0; i < alive; i++)
                {
                    if (_slots[i].root != null)
                        continue;
                    if (!_sim.TryGetUnit(i, out Rts2Unit u) || u.alive == 0)
                        continue;
                    float d = (_sim.GetWorldPosition(i) - camPos).sqrMagnitude;
                    if (d < bestSq)
                    {
                        bestSq = d;
                        best = i;
                    }
                }
                if (best < 0)
                    break;
                if (!TryRealize(best, factionSim))
                    break;
                spawned++;
            }
        }

        bool TryRealize(int index, FactionSimulation factionSim)
        {
            if (!_sim.TryGetUnit(index, out Rts2Unit u) || u.alive == 0)
                return false;
            GameObject prefab = factionSim.GetUnitPrefab(u.role);
            if (prefab == null)
                return false;

            var root = new GameObject($"UnitVis_{index}_{u.role}");
            root.transform.SetParent(_root, false);

            var cap = root.AddComponent<CapsuleCollider>();
            cap.height = 1.8f;
            cap.radius = 0.42f;
            cap.center = new Vector3(0f, 0.9f, 0f);
            cap.direction = 1;
            cap.enabled = true;
            cap.isTrigger = false;

            var proxy = root.AddComponent<Rts2UnitHitProxy>();
            proxy.Bind(index, u.factionId);

            var modelRoot = new GameObject("CharacterModel").transform;
            modelRoot.SetParent(root.transform, false);
            modelRoot.localPosition = Vector3.zero;
            modelRoot.localRotation = Quaternion.identity;

            GameObject visual = Object.Instantiate(prefab, modelRoot);
            visual.name = prefab.name;
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = prefab.transform.localScale.sqrMagnitude > 1e-6f
                ? prefab.transform.localScale
                : Vector3.one;

            // Strip physics only — avoid GetComponentsInChildren for renderers here.
            var cols = visual.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < cols.Length; i++)
                cols[i].enabled = false;
            var rbs = visual.GetComponentsInChildren<Rigidbody>(true);
            for (int i = 0; i < rbs.Length; i++)
                Object.Destroy(rbs[i]);

            Animator anim = visual.GetComponentInChildren<Animator>(true);
            if (anim != null)
            {
                anim.cullingMode = AnimatorCullingMode.CullCompletely;
                anim.applyRootMotion = false;
            }

            Vector3 up = new Vector3(u.axis.x, u.axis.y, u.axis.z).normalized;
            if (up.sqrMagnitude < 1e-6f)
                up = Vector3.up;
            Vector3 feetPos = PlantedFeetPosition(index, up);
            root.transform.SetPositionAndRotation(
                feetPos,
                Quaternion.FromToRotation(Vector3.up, up) * Quaternion.Euler(0f, u.yaw, 0f));

            // One cheap seat attempt now; finish after animator poses (max 2 more).
            SeatModelFeet(modelRoot, anim);

            var slot = new Slot
            {
                root = root,
                model = modelRoot,
                visual = visual,
                animator = anim,
                role = u.role,
                factionId = u.factionId,
                lastPos = feetPos,
                running = false,
                hashesReady = false,
                seated = false,
                seatAttempts = 1
            };
            CacheAnimHashes(ref slot);
            _slots[index] = slot;

            if (_realizedCount < MaxRealized)
                _realized[_realizedCount++] = index;
            return true;
        }

        void CacheAnimHashes(ref Slot slot)
        {
            if (slot.animator == null || slot.animator.runtimeAnimatorController == null)
                return;
            slot.idleHash = ResolveHash(slot.animator, "root|Idle_Menu", "Idle_Menu", FallbackIdle);
            slot.runHash = ResolveHash(slot.animator, "root|Run_Front", "Run_Front", FallbackRun);
            slot.hashesReady = slot.idleHash != 0 || slot.runHash != 0;
        }

        static int ResolveHash(Animator anim, string full, string shortName, int fallback)
        {
            int h = Animator.StringToHash(full);
            if (anim.HasState(0, h))
                return h;
            h = Animator.StringToHash(shortName);
            if (anim.HasState(0, h))
                return h;
            if (anim.HasState(0, fallback))
                return fallback;
            return h;
        }

        void SyncPose(int index, ref Rts2Unit u, float dt)
        {
            Slot slot = _slots[index];
            if (slot.root == null)
                return;

            Vector3 up = new Vector3(u.axis.x, u.axis.y, u.axis.z).normalized;
            if (up.sqrMagnitude < 1e-6f)
                up = Vector3.up;
            Vector3 feetPos = PlantedFeetPosition(index, up);
            Quaternion rot = Quaternion.FromToRotation(Vector3.up, up) * Quaternion.Euler(0f, u.yaw, 0f);
            slot.root.transform.SetPositionAndRotation(feetPos, rot);

            if (!slot.seated && slot.model != null && slot.seatAttempts < 3)
            {
                slot.seatAttempts++;
                SeatModelFeet(slot.model, slot.animator);
                if (slot.seatAttempts >= 3)
                    slot.seated = true;
            }

            float speed = 0f;
            if (dt > 1e-4f)
                speed = Vector3.Distance(feetPos, slot.lastPos) / dt;
            slot.lastPos = feetPos;

            bool wantRun = speed >= RunSpeedThreshold &&
                           u.order != Rts2Order.Idle &&
                           u.order != Rts2Order.Claim;
            if (slot.animator != null && slot.hashesReady && wantRun != slot.running)
            {
                slot.running = wantRun;
                int hash = wantRun ? slot.runHash : slot.idleHash;
                if (hash != 0)
                    slot.animator.CrossFade(hash, 0.12f, 0, 0f);
            }

            _slots[index] = slot;
        }

        /// <summary>
        /// Align soles via humanoid foot bones only (no mesh/AABB scans — those hitch and float).
        /// </summary>
        static void SeatModelFeet(Transform modelRoot, Animator anim)
        {
            if (modelRoot == null)
                return;

            modelRoot.localPosition = Vector3.zero;
            if (!TryFootSoleLocalY(modelRoot, anim, out float soleLocalY))
                return;

            float deltaY = -soleLocalY;
            if (Mathf.Abs(deltaY) < 0.0005f || Mathf.Abs(deltaY) > 5f)
                return;
            Vector3 lp = modelRoot.localPosition;
            lp.y += deltaY;
            modelRoot.localPosition = lp;
        }

        static bool TryFootSoleLocalY(Transform modelRoot, Animator anim, out float soleLocalY)
        {
            soleLocalY = 0f;
            if (anim == null || !anim.isHuman)
                return false;

            float minY = float.PositiveInfinity;
            ConsiderBone(modelRoot, anim, HumanBodyBones.LeftToes, ref minY);
            ConsiderBone(modelRoot, anim, HumanBodyBones.RightToes, ref minY);
            bool hadToes = !float.IsInfinity(minY);
            ConsiderBone(modelRoot, anim, HumanBodyBones.LeftFoot, ref minY);
            ConsiderBone(modelRoot, anim, HumanBodyBones.RightFoot, ref minY);
            if (float.IsInfinity(minY))
                return false;

            soleLocalY = hadToes ? minY : (minY - AnkleToSole);
            return true;
        }

        static void ConsiderBone(Transform modelRoot, Animator anim, HumanBodyBones bone, ref float minY)
        {
            Transform t = anim.GetBoneTransform(bone);
            if (t == null)
                return;
            float y = modelRoot.InverseTransformPoint(t.position).y;
            if (y < minY)
                minY = y;
        }

        void Release(int index)
        {
            if (index < 0 || index >= _slots.Length)
                return;
            Slot slot = _slots[index];
            if (slot.root != null)
                Object.Destroy(slot.root);
            _slots[index] = default;

            for (int r = 0; r < _realizedCount; r++)
            {
                if (_realized[r] != index)
                    continue;
                _realized[r] = _realized[_realizedCount - 1];
                _realizedCount--;
                break;
            }
        }

        public void ReleaseAll()
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].root != null)
                    Object.Destroy(_slots[i].root);
                _slots[i] = default;
            }
            _realizedCount = 0;
        }
    }
}
