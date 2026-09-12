using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace Stargrave.Rts2
{
    /// <summary>
    /// Always-on data-oriented unit simulation: orders + waypoint follow + instanced draw.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Rts2UnitSim : MonoBehaviour
    {
        public const int MaxUnits = 768;
        const int InstanceBatch = 1023;
        const float TickHz = 18f;
        const float DepositRange = 18f;
        const float MaxJobDistanceFromHome = 280f;
        const float InteractWood = 10f;
        const float InteractStone = 13f;

        public static Rts2UnitSim Instance { get; private set; }
        public static bool HasInstance => Instance != null;

        public int AliveCount { get; private set; }
        public PathService Paths { get; private set; }
        public LogicalResourceMap Resources { get; private set; }
        public PlanetNavGraph Nav { get; private set; }
        public int UnitSlotCount => _alive;

        public bool TryGetFactionSim(out FactionSimulation sim)
        {
            sim = _factionSim;
            return sim != null;
        }

        [Min(50)] public int globalUnitCap = 720;
        [Min(0.5f)] public float workerSpeed = 7f;
        [Min(0.5f)] public float soldierSpeed = 6f;
        [Min(0.5f)] public float heavySpeed = 4.2f;
        [Min(0.5f)] public float nobleSpeed = 5f;
        [Min(0.2f)] public float separationRadius = 2.2f;
        [Min(0.1f)] public float engageRangeInfantry = 18f;
        [Min(0.1f)] public float engageRangeArcher = 22f;
        [Min(1)] public int infantryDamage = 12;
        [Min(1)] public int archerDamage = 22;
        [Min(0.05f)] public float attackCooldown = 1f;
        [Tooltip("Near-camera playable models + idle/run. Medium budget (~70 within ~80m).")]
        public bool enableNearCharacterVisuals = true;
        [Tooltip("GPU billboard cards for units without a skinned model (mid/far LOD).")]
        public bool enableUnitCardLod = true;
        [Min(40f)] public float unitCardMaxDistance = 280f;
        [Tooltip("Scene View only: coloured dots for all units while playing.")]
        public bool sceneViewUnitDots = true;
        [Tooltip("When Scene dots are on, only draw combat units (raiders/heavies).")]
        public bool sceneViewUnitDotsCombatOnly = false;
        [Tooltip("Fade/shrink Scene dots with distance from the Scene camera.")]
        public bool sceneViewUnitDotsDistanceFade = true;
        [Min(40f)] public float sceneViewUnitDotsFadeStart = 120f;
        [Min(80f)] public float sceneViewUnitDotsFadeEnd = 520f;

        readonly Rts2Unit[] _units = new Rts2Unit[MaxUnits];
        readonly float[] _stuck = new float[MaxUnits];
        readonly float[] _strafeRepick = new float[MaxUnits];
        Rts2UnitVisualLayer _visuals;
        int _alive;
        float _accum;
        float _logicDt = 1f / TickHz;

        FactionSimulation _factionSim;
        Planet _planet;
        PlanetOceanLayer _ocean;
        Vector3 _planetCenter;
        Mesh _mesh;
        Material _mat;
        Texture2D _cardTex;
        readonly Texture2D[] _cardByRole = new Texture2D[5];
        bool _roleCardsReady;
        PlayerHealth _cachedPlayerHealth;
        float _playerHealthCacheUntil;
        readonly Matrix4x4[] _batch = new Matrix4x4[InstanceBatch];
        readonly Vector4[] _batchColors = new Vector4[InstanceBatch];
        MaterialPropertyBlock _mpb;
        static readonly int ColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorIdFallback = Shader.PropertyToID("_Color");

        public void Configure(
            FactionSimulation sim,
            PlanetNavGraph nav,
            PathService paths,
            LogicalResourceMap resources)
        {
            _factionSim = sim;
            Nav = nav;
            Paths = paths;
            Resources = resources;
            CachePlanet();
            if (_visuals == null)
                _visuals = new Rts2UnitVisualLayer(this);
        }

        public void InvalidateAllPaths()
        {
            for (int i = 0; i < _alive; i++)
            {
                if (_units[i].alive == 0)
                    continue;
                Rts2Unit u = _units[i];
                u.pathId = -1;
                u.pathWaypoint = 0;
                _units[i] = u;
            }
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            _mpb = new MaterialPropertyBlock();
            EnsureRenderAssets();
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            if (_visuals != null)
            {
                _visuals.ReleaseAll();
                _visuals = null;
            }
            if (_mat != null)
                Destroy(_mat);
            if (_cardTex != null)
                Destroy(_cardTex);
            for (int i = 0; i < _cardByRole.Length; i++)
            {
                if (_cardByRole[i] != null)
                    Destroy(_cardByRole[i]);
                _cardByRole[i] = null;
            }
            _roleCardsReady = false;
        }

        void CachePlanet()
        {
            if (_factionSim != null)
                _planet = _factionSim.planet;
            if (_planet != null)
            {
                _planetCenter = _planet.transform.position;
                if (_ocean == null)
                    _ocean = _planet.GetComponent<PlanetOceanLayer>();
            }
        }

        void EnsureRenderAssets()
        {
            // Mid/far LOD: humanoid silhouette billboards (alpha cutout), faction-tinted.
            if (_mesh == null)
            {
                var tmp = GameObject.CreatePrimitive(PrimitiveType.Quad);
                _mesh = tmp.GetComponent<MeshFilter>().sharedMesh;
                Destroy(tmp);
            }
            if (_cardTex == null)
                _cardTex = BuildUnitCardSilhouetteTexture();
            if (_mat == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Unlit")
                    ?? Shader.Find("Unlit/Transparent Cutout")
                    ?? Shader.Find("Unlit/Color")
                    ?? Shader.Find("Sprites/Default");
                _mat = new Material(shader != null ? shader : Shader.Find("Sprites/Default"));
                _mat.name = "Rts2UnitCardCutout";
                _mat.enableInstancing = true;
                if (_mat.HasProperty("_Surface"))
                    _mat.SetFloat("_Surface", 0f);
                if (_mat.HasProperty("_AlphaClip"))
                    _mat.SetFloat("_AlphaClip", 1f);
                if (_mat.HasProperty("_Cutoff"))
                    _mat.SetFloat("_Cutoff", 0.35f);
                if (_mat.HasProperty("_ZWrite"))
                    _mat.SetFloat("_ZWrite", 1f);
                if (_mat.HasProperty("_Cull"))
                    _mat.SetFloat("_Cull", (float)CullMode.Off);
                _mat.SetOverrideTag("RenderType", "TransparentCutout");
                _mat.renderQueue = (int)RenderQueue.AlphaTest;
                _mat.EnableKeyword("_ALPHATEST_ON");
            }
            if (_cardTex != null && _mat != null)
            {
                if (_mat.HasProperty("_BaseMap"))
                    _mat.SetTexture("_BaseMap", _cardTex);
                if (_mat.HasProperty("_MainTex"))
                    _mat.SetTexture("_MainTex", _cardTex);
            }
            EnsureBakedRoleCards();
        }

        void EnsureBakedRoleCards()
        {
            if (_roleCardsReady || _factionSim == null)
                return;
            _roleCardsReady = true; // one attempt per session even if some fail

            BakeRoleCard(Rts2Role.Worker);
            BakeRoleCard(Rts2Role.Infantry);
            BakeRoleCard(Rts2Role.Archer);
            BakeRoleCard(Rts2Role.Noble);
            BakeRoleCard(Rts2Role.Merchant);
        }

        void BakeRoleCard(Rts2Role role)
        {
            int idx = (int)role;
            if (idx < 0 || idx >= _cardByRole.Length || _cardByRole[idx] != null)
                return;
            GameObject prefab = _factionSim != null ? _factionSim.GetUnitPrefab(role) : null;
            if (prefab == null)
                return;
            Texture2D baked = Rts2UnitCardBaker.BakeSideCard(prefab, role.ToString());
            if (baked != null)
                _cardByRole[idx] = baked;
        }

        Texture2D CardTextureForRole(Rts2Role role)
        {
            int idx = (int)role;
            if (idx >= 0 && idx < _cardByRole.Length && _cardByRole[idx] != null)
                return _cardByRole[idx];
            return _cardTex;
        }

        void BindCardTexture(Texture2D tex)
        {
            if (_mat == null || tex == null)
                return;
            if (_mat.HasProperty("_BaseMap"))
                _mat.SetTexture("_BaseMap", tex);
            if (_mat.HasProperty("_MainTex"))
                _mat.SetTexture("_MainTex", tex);
        }

        /// <summary>
        /// White humanoid side silhouette on transparent — multiplied by faction colour at draw time.
        /// </summary>
        static Texture2D BuildUnitCardSilhouetteTexture()
        {
            const int w = 64;
            const int h = 128;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: true)
            {
                name = "Rts2UnitCardSilhouette",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                alphaIsTransparency = true
            };

            var pixels = new Color32[w * h];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color32(0, 0, 0, 0);

            // Side-view person: head, torso, arms, legs (white RGB so faction tint works).
            FillEllipse(pixels, w, h, w * 0.50f, h * 0.86f, w * 0.16f, h * 0.10f); // head
            FillRect(pixels, w, h, w * 0.44f, h * 0.74f, w * 0.12f, h * 0.06f); // neck
            FillRoundedTorso(pixels, w, h, w * 0.50f, h * 0.52f, w * 0.28f, h * 0.22f); // torso
            // Arms
            FillRect(pixels, w, h, w * 0.18f, h * 0.48f, w * 0.12f, h * 0.28f);
            FillRect(pixels, w, h, w * 0.70f, h * 0.48f, w * 0.12f, h * 0.28f);
            // Legs
            FillRect(pixels, w, h, w * 0.34f, h * 0.06f, w * 0.12f, h * 0.36f);
            FillRect(pixels, w, h, w * 0.54f, h * 0.06f, w * 0.12f, h * 0.36f);
            // Boots
            FillRect(pixels, w, h, w * 0.30f, h * 0.02f, w * 0.18f, h * 0.06f);
            FillRect(pixels, w, h, w * 0.52f, h * 0.02f, w * 0.18f, h * 0.06f);

            tex.SetPixels32(pixels);
            tex.Apply(updateMipmaps: true, makeNoLongerReadable: true);
            return tex;
        }

        static void FillRect(Color32[] pixels, int w, int h, float x0, float y0, float rw, float rh)
        {
            int xi0 = Mathf.Clamp(Mathf.FloorToInt(x0), 0, w - 1);
            int yi0 = Mathf.Clamp(Mathf.FloorToInt(y0), 0, h - 1);
            int xi1 = Mathf.Clamp(Mathf.CeilToInt(x0 + rw), 0, w);
            int yi1 = Mathf.Clamp(Mathf.CeilToInt(y0 + rh), 0, h);
            var col = new Color32(245, 245, 245, 255);
            for (int y = yi0; y < yi1; y++)
            for (int x = xi0; x < xi1; x++)
                pixels[y * w + x] = col;
        }

        static void FillEllipse(Color32[] pixels, int w, int h, float cx, float cy, float rx, float ry)
        {
            int xi0 = Mathf.Clamp(Mathf.FloorToInt(cx - rx), 0, w - 1);
            int yi0 = Mathf.Clamp(Mathf.FloorToInt(cy - ry), 0, h - 1);
            int xi1 = Mathf.Clamp(Mathf.CeilToInt(cx + rx), 0, w);
            int yi1 = Mathf.Clamp(Mathf.CeilToInt(cy + ry), 0, h);
            float rx2 = Mathf.Max(0.01f, rx * rx);
            float ry2 = Mathf.Max(0.01f, ry * ry);
            var col = new Color32(250, 250, 250, 255);
            for (int y = yi0; y < yi1; y++)
            {
                float dy = (y + 0.5f - cy);
                for (int x = xi0; x < xi1; x++)
                {
                    float dx = (x + 0.5f - cx);
                    if ((dx * dx) / rx2 + (dy * dy) / ry2 <= 1f)
                        pixels[y * w + x] = col;
                }
            }
        }

        static void FillRoundedTorso(Color32[] pixels, int w, int h, float cx, float cy, float halfW, float halfH)
        {
            // Soft rectangle with rounded shoulders.
            FillRect(pixels, w, h, cx - halfW, cy - halfH, halfW * 2f, halfH * 2f);
            FillEllipse(pixels, w, h, cx, cy + halfH * 0.55f, halfW * 1.05f, halfH * 0.55f);
        }

        public bool TrySpawn(int factionId, Rts2Role role, Vector3 surfaceAxis, out int unitIndex)
        {
            unitIndex = -1;
            if (AliveCount >= MaxUnits || AliveCount >= globalUnitCap)
                return false;
            CachePlanet();
            if (_planet == null)
                return false;

            // Prefer free slots so indices stay stable for claim/combat references.
            int idx = -1;
            for (int i = 0; i < _alive; i++)
            {
                if (_units[i].alive == 0)
                {
                    idx = i;
                    break;
                }
            }
            if (idx < 0)
            {
                if (_alive >= MaxUnits)
                    return false;
                idx = _alive;
                _alive++;
            }

            Vector3 axis = surfaceAxis.sqrMagnitude > 1e-8f ? surfaceAxis.normalized : Vector3.up;
            float surface = _planet.GetSurfaceRadiusWorld(axis);
            float halfH = ScaleForRole(role).y;
            // Body-center radius (matches SteerToward) so feet math stays cheap and consistent.
            float radius = surface + halfH + 0.1f;
            float maxHp = MaxHp(role);
            _units[idx] = new Rts2Unit
            {
                alive = 1,
                factionId = (byte)Mathf.Clamp(factionId, 0, 255),
                role = role,
                order = Rts2Order.Idle,
                axis = new float3(axis.x, axis.y, axis.z),
                radius = radius,
                yaw = UnityEngine.Random.Range(0f, 360f),
                hp = maxHp,
                maxHp = maxHp,
                targetUnit = -1,
                targetBuilding = -1,
                targetTown = -1,
                resourceSiteId = -1,
                pathId = -1,
                pathWaypoint = 0,
                gatherTask = 0
            };
            _stuck[idx] = 0f;
            RecountAlive();
            unitIndex = idx;
            return true;
        }

        public bool TrySpawnRoamer(int factionId, Vector3 surfaceAxis, out int unitIndex)
        {
            if (!TrySpawn(factionId, Rts2Role.Worker, surfaceAxis, out unitIndex))
                return false;
            Rts2Unit u = _units[unitIndex];
            u.order = Rts2Order.Move;
            u.gatherTask = 255;
            _units[unitIndex] = u;
            FactionController faction = FactionById(factionId);
            if (faction != null &&
                faction.Economy != null &&
                faction.Economy.useDesignatedSiteFounding)
            {
                RequestPathTo(ref u, unitIndex, faction.GetSafePosition());
                _units[unitIndex] = u;
            }
            else
                _stuck[unitIndex] = 0.01f; // force immediate roam destination
            return true;
        }

        public void ActivateFactionAfterFounding(int factionId)
        {
            Vector3 home = Vector3.zero;
            FactionController faction = FactionById(factionId);
            if (faction != null)
                home = ProjectHome(faction);

            for (int i = 0; i < _alive; i++)
            {
                if (_units[i].alive == 0 || _units[i].factionId != factionId)
                    continue;
                if (_units[i].role != Rts2Role.Worker)
                    continue;
                Rts2Unit u = _units[i];
                if (u.order == Rts2Order.Roam || u.order == Rts2Order.Move || u.gatherTask == 255)
                {
                    u.gatherTask = 0;
                    u.order = Rts2Order.Gather;
                    ClearPath(ref u);
                    if (home.sqrMagnitude > 1e-6f)
                        RequestPathTo(ref u, i, home);
                }
                _units[i] = u;
            }
        }

        public bool TrySpawn(int factionId, RtsUnitRole legacyRole, Vector3 surfaceAxis, out int unitIndex) =>
            TrySpawn(factionId, Rts2Roles.FromLegacy(legacyRole), surfaceAxis, out unitIndex);

        public int CountRole(int factionId, RtsUnitRole legacyRole) =>
            CountRole(factionId, Rts2Roles.FromLegacy(legacyRole));

        public int CountRole(int factionId, Rts2Role role)
        {
            int n = 0;
            for (int i = 0; i < _alive; i++)
                if (_units[i].alive != 0 && _units[i].factionId == factionId && _units[i].role == role)
                    n++;
            return n;
        }

        public int CountCombat(int factionId)
        {
            int n = 0;
            for (int i = 0; i < _alive; i++)
                if (_units[i].alive != 0 && _units[i].factionId == factionId && Rts2Roles.IsCombat(_units[i].role))
                    n++;
            return n;
        }

        public int CountLiving(int factionId)
        {
            int n = 0;
            for (int i = 0; i < _alive; i++)
                if (_units[i].alive != 0 && _units[i].factionId == factionId)
                    n++;
            return n;
        }

        public int CountNear(int factionId, Vector3 worldPos, float radius, bool combatOnly)
        {
            float r2 = radius * radius;
            int n = 0;
            for (int i = 0; i < _alive; i++)
            {
                if (_units[i].alive == 0 || _units[i].factionId != factionId)
                    continue;
                if (combatOnly && !Rts2Roles.IsCombat(_units[i].role))
                    continue;
                if ((GetWorldPosition(i) - worldPos).sqrMagnitude <= r2)
                    n++;
            }
            return n;
        }

        public int CountEnemyCombatNear(int friendlyFactionId, Vector3 worldPos, float radius)
        {
            float r2 = radius * radius;
            int n = 0;
            for (int i = 0; i < _alive; i++)
            {
                if (_units[i].alive == 0 || _units[i].factionId == friendlyFactionId)
                    continue;
                if (!Rts2Roles.IsCombat(_units[i].role))
                    continue;
                if ((GetWorldPosition(i) - worldPos).sqrMagnitude <= r2)
                    n++;
            }
            return n;
        }

        public Vector3 GetWorldPosition(int index)
        {
            if (index < 0 || index >= _alive)
                return _planetCenter;
            Rts2Unit u = _units[index];
            Vector3 axis = new Vector3(u.axis.x, u.axis.y, u.axis.z).normalized;
            return _planetCenter + axis * Mathf.Max(1f, u.radius);
        }

        /// <summary>
        /// Feet on the surface under the unit. Cheap: strips the body-center offset from radius
        /// (no per-frame planet elevation samples).
        /// </summary>
        public Vector3 GetFeetWorldPosition(int index)
        {
            if (index < 0 || index >= _alive)
                return _planetCenter;
            Rts2Unit u = _units[index];
            Vector3 axis = new Vector3(u.axis.x, u.axis.y, u.axis.z).normalized;
            if (axis.sqrMagnitude < 1e-8f)
                axis = Vector3.up;
            float halfH = ScaleForRole(u.role).y;
            float feetR = Mathf.Max(1f, u.radius - halfH - 0.1f);
            return _planetCenter + axis * feetR;
        }

        public bool TryGetUnit(int index, out Rts2Unit unit)
        {
            unit = default;
            if (index < 0 || index >= _alive)
                return false;
            unit = _units[index];
            return unit.alive != 0;
        }

        public void SetGatherTaskSplit(int factionId, float stoneFraction)
        {
            stoneFraction = Mathf.Clamp01(stoneFraction);
            int workers = CountRole(factionId, Rts2Role.Worker);
            if (workers <= 0)
                return;
            int stoneSlots = Mathf.Clamp(Mathf.RoundToInt(workers * stoneFraction), 0, workers);
            if (workers >= 2 && stoneFraction > 0.05f && stoneFraction < 0.95f)
                stoneSlots = Mathf.Clamp(stoneSlots, 1, workers - 1);

            int ordinal = 0;
            for (int i = 0; i < _alive; i++)
            {
                if (_units[i].alive == 0 || _units[i].factionId != factionId || _units[i].role != Rts2Role.Worker)
                    continue;
                Rts2Unit u = _units[i];
                if (u.carryAmount > 0)
                {
                    _units[i] = u;
                    ordinal++;
                    continue;
                }
                u.gatherTask = ordinal < stoneSlots ? (byte)1 : (byte)0;
                if (u.order == Rts2Order.Idle)
                    u.order = Rts2Order.Gather;
                ordinal++;
                _units[i] = u;
            }
        }

        public void SetGatherTask(int factionId, FactionResourceType type) =>
            SetGatherTaskSplit(factionId, type == FactionResourceType.Stone ? 1f : 0f);

        /// <summary>Send unfounded workers to a newly assigned designated site.</summary>
        public void RemarchUnfoundedWorkers(int factionId, Vector3 siteWorld)
        {
            for (int i = 0; i < _alive; i++)
            {
                if (_units[i].alive == 0 || _units[i].factionId != factionId)
                    continue;
                if (_units[i].role != Rts2Role.Worker)
                    continue;
                FactionController faction = FactionById(factionId);
                if (faction != null && faction.HasFoundedCampus)
                    continue;

                Rts2Unit u = _units[i];
                ClearPath(ref u);
                u.order = Rts2Order.Move;
                u.gatherTask = 255;
                RequestPathTo(ref u, i, siteWorld);
                _stuck[i] = 0.01f;
                _units[i] = u;
            }
        }

        public void SetHomeGoal(int factionId, Vector3 worldHome)
        {
            for (int i = 0; i < _alive; i++)
            {
                if (_units[i].alive == 0 || _units[i].factionId != factionId)
                    continue;
                Rts2Unit u = _units[i];
                if (u.role == Rts2Role.Worker && u.carryAmount > 0)
                {
                    u.order = Rts2Order.Deposit;
                    EnsurePath(ref u, i, worldHome);
                }
                else
                {
                    // Already fleeing with a live path — don't cancel/repath every strategy tick.
                    if (u.order == Rts2Order.Flee && u.pathId >= 0 &&
                        Paths != null && !Paths.IsPathFailed(u.pathId))
                    {
                        _units[i] = u;
                        continue;
                    }
                    u.order = Rts2Order.Flee;
                    u.targetUnit = -1;
                    EnsurePath(ref u, i, worldHome);
                }
                _units[i] = u;
            }
        }

        public void SetAssaultGoal(int factionId, Vector3 worldGoal)
        {
            SetAssaultGoal(factionId, worldGoal, default);
        }

        public void SetAssaultGoal(int factionId, Vector3 worldGoal, Vector3 homeForHeavyOffset)
        {
            bool haveHome = homeForHeavyOffset.sqrMagnitude > 1e-4f;
            for (int i = 0; i < _alive; i++)
            {
                if (_units[i].alive == 0 || _units[i].factionId != factionId)
                    continue;
                if (!Rts2Roles.IsCombat(_units[i].role))
                    continue;
                Rts2Unit u = _units[i];
                // Never yank soldiers out of a live fight or retreat.
                if (u.order == Rts2Order.AttackUnit ||
                    u.order == Rts2Order.AttackBuilding ||
                    u.order == Rts2Order.Flee)
                    continue;
                u.order = Rts2Order.Move;
                Vector3 dest = worldGoal;
                // Heavies sit slightly behind the front toward home.
                if (haveHome && u.role == Rts2Role.Archer)
                {
                    CachePlanet();
                    if (_planet != null)
                    {
                        Vector3 axisGoal = (worldGoal - _planetCenter).normalized;
                        Vector3 axisHome = (homeForHeavyOffset - _planetCenter).normalized;
                        Vector3 axis = Vector3.Slerp(axisGoal, axisHome, 0.22f).normalized;
                        dest = _planet.GetSurfacePointWorld(axis);
                    }
                    else
                        dest = Vector3.Lerp(worldGoal, homeForHeavyOffset, 0.22f);
                }
                EnsurePath(ref u, i, dest);
                _units[i] = u;
            }
        }

        public void SetClaimTown(int factionId, int townIndex)
        {
            for (int i = 0; i < _alive; i++)
            {
                if (_units[i].alive == 0 || _units[i].factionId != factionId)
                    continue;
                if (_units[i].role != Rts2Role.Noble)
                    continue;
                Rts2Unit u = _units[i];
                u.targetTown = townIndex;
                u.order = Rts2Order.Claim;
                ClaimableTown town = TownByIndex(townIndex);
                if (town != null)
                    RequestPathTo(ref u, i, town.transform.position);
                _units[i] = u;
            }
        }

        public void SetTradeGoal(int factionId, Vector3 marketPos)
        {
            for (int i = 0; i < _alive; i++)
            {
                if (_units[i].alive == 0 || _units[i].factionId != factionId)
                    continue;
                if (_units[i].role != Rts2Role.Merchant)
                    continue;
                Rts2Unit u = _units[i];
                // Don't yank merchants mid-leg; TickMerchant owns the bounce cycle.
                if (u.order == Rts2Order.Trade && u.pathId >= 0)
                    continue;
                u.order = Rts2Order.Trade;
                RequestPathTo(ref u, i, marketPos);
                _units[i] = u;
            }
        }

        public void DamageUnit(int index, int amount)
        {
            if (index < 0 || index >= _alive || amount <= 0)
                return;
            Rts2Unit u = _units[index];
            if (u.alive == 0)
                return;
            u.hp -= amount;
            if (u.hp <= 0f)
            {
                u.hp = 0f;
                u.alive = 0;
                if (u.pathId >= 0 && Paths != null)
                    Paths.ReleasePath(u.pathId);
                u.pathId = -1;
            }
            _units[index] = u;
            RecountAlive();
        }

        /// <summary>Player shot a unit — apply damage and aggro that faction to retaliate.</summary>
        public bool TryDamageUnitFromPlayer(int index, int amount, Transform player)
        {
            if (index < 0 || index >= _alive || amount <= 0 || player == null)
                return false;
            Rts2Unit u = _units[index];
            if (u.alive == 0)
                return false;

            int factionId = u.factionId;
            DamageUnit(index, amount);
            bool killed = _units[index].alive == 0;
            FactionController faction = FactionById(factionId);
            faction?.NotifyAttackedByPlayer(player, killed);
            return true;
        }

        /// <summary>
        /// Hit the closest living unit near a shot ray (for far GPU units without colliders).
        /// </summary>
        public bool TryDamageAlongPlayerRay(
            Vector3 origin,
            Vector3 direction,
            float maxRange,
            float hitRadius,
            int amount,
            Transform player)
        {
            if (player == null || amount <= 0 || maxRange <= 0f)
                return false;
            direction = direction.normalized;
            float hitR2 = hitRadius * hitRadius;
            int bestI = -1;
            float bestAlong = maxRange + 1f;
            float bestPerp2 = hitR2;

            for (int i = 0; i < _alive; i++)
            {
                if (_units[i].alive == 0)
                    continue;
                Vector3 to = GetWorldPosition(i) - origin;
                float along = Vector3.Dot(to, direction);
                if (along < 0f || along > maxRange)
                    continue;
                float perp2 = (to - direction * along).sqrMagnitude;
                if (perp2 > hitR2)
                    continue;
                if (along < bestAlong - 0.01f || (Mathf.Abs(along - bestAlong) <= 0.01f && perp2 < bestPerp2))
                {
                    bestAlong = along;
                    bestPerp2 = perp2;
                    bestI = i;
                }
            }

            if (bestI < 0)
                return false;
            return TryDamageUnitFromPlayer(bestI, amount, player);
        }

        public bool ApplyDamageAt(Vector3 worldPos, float radius, int amount, int excludeFactionId)
        {
            float best = radius * radius;
            int bestI = -1;
            for (int i = 0; i < _alive; i++)
            {
                if (_units[i].alive == 0)
                    continue;
                if (excludeFactionId >= 0 && _units[i].factionId == excludeFactionId)
                    continue;
                float d = (GetWorldPosition(i) - worldPos).sqrMagnitude;
                if (d <= best)
                {
                    best = d;
                    bestI = i;
                }
            }
            if (bestI < 0)
                return false;
            DamageUnit(bestI, amount);
            return true;
        }

        public bool ApplyPlayerDamageAt(Vector3 worldPos, float radius, int amount, Transform player)
        {
            if (player == null || amount <= 0)
                return false;
            float best = radius * radius;
            int bestI = -1;
            for (int i = 0; i < _alive; i++)
            {
                if (_units[i].alive == 0)
                    continue;
                float d = (GetWorldPosition(i) - worldPos).sqrMagnitude;
                if (d <= best)
                {
                    best = d;
                    bestI = i;
                }
            }
            if (bestI < 0)
                return false;
            return TryDamageUnitFromPlayer(bestI, amount, player);
        }

        public bool TryFindNearestUnit(Vector3 worldPos, float radius, int excludeFactionId, out int index, out float distSq)
        {
            index = -1;
            distSq = radius * radius;
            for (int i = 0; i < _alive; i++)
            {
                if (_units[i].alive == 0)
                    continue;
                if (excludeFactionId >= 0 && _units[i].factionId == excludeFactionId)
                    continue;
                float d = (GetWorldPosition(i) - worldPos).sqrMagnitude;
                if (d < distSq)
                {
                    distSq = d;
                    index = i;
                }
            }
            return index >= 0;
        }

        bool TryFindNearestHostileUnit(Vector3 worldPos, float radius, int excludeFactionId, out int index, out float distSq)
        {
            return TryFindHostileCombatTarget(worldPos, radius, excludeFactionId, -1, out index, out distSq);
        }

        /// <summary>
        /// Sticky target + soft focus fire (prefer wounded hostiles within radius).
        /// </summary>
        bool TryFindHostileCombatTarget(
            Vector3 worldPos,
            float radius,
            int excludeFactionId,
            int stickyIndex,
            out int index,
            out float distSq)
        {
            index = -1;
            distSq = radius * radius;

            if (stickyIndex >= 0 && stickyIndex < _alive &&
                _units[stickyIndex].alive != 0 &&
                (excludeFactionId < 0 || _units[stickyIndex].factionId != excludeFactionId))
            {
                FactionController stickyFaction = FactionById(_units[stickyIndex].factionId);
                if (stickyFaction == null || !stickyFaction.IsBarAssaultProtected)
                {
                    float stickyD = (GetWorldPosition(stickyIndex) - worldPos).sqrMagnitude;
                    float stickR = radius * 1.45f;
                    if (stickyD <= stickR * stickR)
                    {
                        index = stickyIndex;
                        distSq = stickyD;
                        return true;
                    }
                }
            }

            float bestScore = float.MaxValue;
            float radiusSq = radius * radius;
            for (int i = 0; i < _alive; i++)
            {
                if (_units[i].alive == 0)
                    continue;
                if (excludeFactionId >= 0 && _units[i].factionId == excludeFactionId)
                    continue;
                FactionController other = FactionById(_units[i].factionId);
                if (other != null && other.IsBarAssaultProtected)
                    continue;
                float d = (GetWorldPosition(i) - worldPos).sqrMagnitude;
                if (d > radiusSq)
                    continue;
                float maxHp = _units[i].maxHp;
                float hpFrac = maxHp > 0.01f ? Mathf.Clamp01(_units[i].hp / maxHp) : 1f;
                // Lower score wins: nearer + wounded preferred.
                float score = d * (0.4f + 0.6f * hpFrac);
                if (score < bestScore)
                {
                    bestScore = score;
                    distSq = d;
                    index = i;
                }
            }
            return index >= 0;
        }

        void Update()
        {
            if (_planet == null)
                CachePlanet();
            Paths?.TickFrame();
            TryPlayerInteractFounding();

            _accum += Time.deltaTime;
            while (_accum >= _logicDt)
            {
                _accum -= _logicDt;
                SimulationTick(_logicDt);
            }
            if (_visuals != null)
            {
                if (enableNearCharacterVisuals)
                    _visuals.Tick(Time.deltaTime);
                else if (_visuals.HasAny)
                    _visuals.ReleaseAll();
            }
            RenderInstances();
        }

        void SimulationTick(float dt)
        {
            for (int i = 0; i < _alive; i++)
            {
                if (_units[i].alive == 0)
                    continue;
                Rts2Unit u = _units[i];
                if (u.attackCooldown > 0f)
                    u.attackCooldown -= dt;

                switch (u.role)
                {
                    case Rts2Role.Worker:
                        TickWorker(ref u, i, dt);
                        break;
                    case Rts2Role.Infantry:
                    case Rts2Role.Archer:
                        TickSoldier(ref u, i, dt);
                        break;
                    case Rts2Role.Noble:
                        TickNoble(ref u, i, dt);
                        break;
                    case Rts2Role.Merchant:
                        TickMerchant(ref u, i, dt);
                        break;
                    default:
                        FollowPath(ref u, i, dt, Speed(u.role));
                        break;
                }
                _units[i] = u;
            }
            CheckRoamingWorkerMeetings();
            // Keep slot indices stable (claim/combat handles); only trim trailing dead.
            TrimTrailingDead();
        }

        void TickWorker(ref Rts2Unit u, int index, float dt)
        {
            FactionController faction = FactionById(u.factionId);
            if (faction == null)
                return;

            if (!faction.HasFoundedCampus)
            {
                if (faction.Economy != null && faction.Economy.useDesignatedSiteFounding)
                    TickMarchToDesignatedSite(ref u, index, dt, faction);
                else
                    TickRoam(ref u, index, dt, faction);
                return;
            }

            if (u.order == Rts2Order.Roam)
            {
                TickRoam(ref u, index, dt, faction);
                return;
            }

            Vector3 home = ProjectHome(faction);
            Vector3 pos = GetWorldPosition(index);

            if (u.carryAmount > 0)
            {
                u.order = Rts2Order.Deposit;
                u.resourceSiteId = -1;
                EnsurePath(ref u, index, home);
                FollowPath(ref u, index, dt, workerSpeed * 1.15f, home);
                if ((GetWorldPosition(index) - home).sqrMagnitude <= DepositRange * DepositRange)
                {
                    FactionResourceType type = u.carryType == 1
                        ? FactionResourceType.Stone
                        : FactionResourceType.Wood;
                    faction.DepositResource(type, u.carryAmount);
                    u.carryAmount = 0;
                    u.order = Rts2Order.Gather;
                    ClearPath(ref u);
                }
                return;
            }

            FactionResourceType want = u.gatherTask == 1
                ? FactionResourceType.Stone
                : FactionResourceType.Wood;

            if (Resources == null || !Resources.IsReady)
            {
                HoldNear(ref u, index, home, dt, workerSpeed * 0.35f);
                return;
            }

            if (u.resourceSiteId < 0 ||
                !Resources.TryGetSite(u.resourceSiteId, out LogicalResourceSite site) ||
                site.type != want ||
                !site.active)
            {
                u.resourceSiteId = Resources.FindBestSite(
                    pos, home, want, MaxJobDistanceFromHome, index, IsSiteClaimed);
            }

            if (u.resourceSiteId < 0 || !Resources.TryGetSite(u.resourceSiteId, out site))
            {
                HoldNear(ref u, index, home, dt, workerSpeed * 0.35f);
                Resources.RememberStreamHints(faction, home, MaxJobDistanceFromHome);
                return;
            }

            u.order = Rts2Order.Gather;
            EnsurePath(ref u, index, site.position);
            FollowPath(ref u, index, dt, workerSpeed, site.position);

            float interact = want == FactionResourceType.Stone ? InteractStone : InteractWood;
            if ((GetWorldPosition(index) - site.position).sqrMagnitude > interact * interact)
                return;

            int room = Mathf.Max(0, faction.Economy.workerCarryCapacity - u.carryAmount);
            if (room <= 0)
            {
                u.order = Rts2Order.Deposit;
                return;
            }

            int got = Resources.Gather(u.resourceSiteId, Mathf.Min(room, Mathf.Max(1, Mathf.CeilToInt(8f * dt))));
            if (got <= 0)
            {
                u.resourceSiteId = -1;
                return;
            }

            u.carryAmount = (byte)Mathf.Min(255, u.carryAmount + got);
            u.carryType = want == FactionResourceType.Stone ? (byte)1 : (byte)0;
            faction.RememberStreamFocus(site.position, true, want);
            if (u.carryAmount >= faction.Economy.workerCarryCapacity)
            {
                u.order = Rts2Order.Deposit;
                ClearPath(ref u);
            }
        }

        void TickSoldier(ref Rts2Unit u, int index, float dt)
        {
            FactionController faction = FactionById(u.factionId);
            if (faction == null)
                return;

            float speed = u.role == Rts2Role.Archer ? heavySpeed : soldierSpeed;
            if (u.order == Rts2Order.Flee)
            {
                Vector3 home = ProjectHome(faction);
                EnsurePath(ref u, index, home);
                FollowPath(ref u, index, dt, speed, home);
                return;
            }

            float retreatFrac = faction.Combat != null ? faction.Combat.soldierRetreatHealth : 0.25f;
            if (u.maxHp > 0.01f && u.hp / u.maxHp <= retreatFrac)
            {
                u.order = Rts2Order.Flee;
                u.targetUnit = -1;
                Vector3 home = ProjectHome(faction);
                EnsurePath(ref u, index, home);
                FollowPath(ref u, index, dt, speed, home);
                return;
            }

            float engage = u.role == Rts2Role.Archer ? engageRangeArcher : engageRangeInfantry;
            bool assaulting = faction.State == FactionState.Attacking ||
                              faction.State == FactionState.Fighting;
            // While marching on a raid, hunt farther so soldiers don't walk past fights.
            float hunt = assaulting ? engage * 2.6f : engage;

            // Player aggro: hunt / shoot the player like a rival unit.
            if (faction.HostileToPlayer)
            {
                Transform player = faction.PlayerAggressor;
                if (player == null)
                    player = RuntimeSceneRefs.GetPlayerTransform(0.5f);
                if (player != null)
                {
                    Vector3 playerPos = player.position;
                    float chase = engage * 3.2f;
                    if (faction.HasDefensePing || faction.HasBackupPing)
                        chase = Mathf.Max(chase, engage * 8f);
                    float dPlayerSq = (GetWorldPosition(index) - playerPos).sqrMagnitude;
                    if (dPlayerSq <= chase * chase)
                    {
                        u.order = Rts2Order.AttackUnit;
                        float shoot = u.role == Rts2Role.Archer ? engage * 0.9f : engage * 0.5f;
                        if (dPlayerSq > shoot * shoot)
                        {
                            EnsurePath(ref u, index, playerPos);
                            FollowPath(ref u, index, dt, speed, playerPos);
                        }
                        else
                        {
                            CachePlanet();
                            Vector3 selfPos = GetWorldPosition(index);
                            Vector3 up = (selfPos - _planetCenter).normalized;
                            Vector3 toPlayer = Vector3.ProjectOnPlane(playerPos - selfPos, up);
                            if (toPlayer.sqrMagnitude < 1e-6f)
                                toPlayer = Vector3.ProjectOnPlane(Vector3.right, up);
                            toPlayer.Normalize();
                            Vector3 tangent = Vector3.Cross(up, toPlayer).normalized;
                            if (((index * 17) & 1) == 0)
                                tangent = -tangent;

                            _strafeRepick[index] -= dt;
                            bool needStrafe = u.pathId < 0 ||
                                              _strafeRepick[index] <= 0f ||
                                              (Paths != null && Paths.IsPathFailed(u.pathId));
                            if (needStrafe)
                            {
                                float orbit = shoot * UnityEngine.Random.Range(0.55f, 0.9f);
                                float along = UnityEngine.Random.Range(-0.35f, 0.35f) * shoot;
                                Vector3 strafeWorld = playerPos + tangent * orbit + toPlayer * along;
                                if (_planet != null)
                                    strafeWorld = _planet.GetSurfacePointWorld((strafeWorld - _planetCenter).normalized);
                                // Reuse path slot without forcing a full repath every strafe tick.
                                if (u.pathId >= 0)
                                    ClearPath(ref u);
                                EnsurePath(ref u, index, strafeWorld);
                                _strafeRepick[index] = UnityEngine.Random.Range(1.4f, 2.4f);
                            }
                            FollowPath(ref u, index, dt, speed * 0.85f, playerPos);

                            if (u.attackCooldown <= 0f)
                            {
                                int dmg = u.role == Rts2Role.Archer ? archerDamage : infantryDamage;
                                FireBolt(index, playerPos, u.factionId, u.role);
                                PlayerHealth health = ResolvePlayerHealth(player);
                                health?.TakeDamage(dmg);
                                u.attackCooldown = attackCooldown;
                            }
                        }
                        return;
                    }
                }
            }

            // Engage hostiles: sticky target + soft fire; wider hunt while assaulting.
            if (TryFindHostileCombatTarget(
                    GetWorldPosition(index), hunt, u.factionId, u.targetUnit, out int enemy, out float dSq))
            {
                u.targetUnit = enemy;
                u.order = Rts2Order.AttackUnit;
                faction.MarkFighting();
                Vector3 enemyPos = GetWorldPosition(enemy);
                float shoot = u.role == Rts2Role.Archer ? engage * 0.85f : engage * 0.45f;
                if (dSq > shoot * shoot)
                {
                    EnsurePath(ref u, index, enemyPos);
                    FollowPath(ref u, index, dt, speed, enemyPos);
                }
                else
                {
                    // Strafe / orbit while shooting instead of standing still.
                    CachePlanet();
                    Vector3 selfPos = GetWorldPosition(index);
                    Vector3 up = (selfPos - _planetCenter).normalized;
                    Vector3 toEnemy = Vector3.ProjectOnPlane(enemyPos - selfPos, up);
                    if (toEnemy.sqrMagnitude < 1e-6f)
                        toEnemy = Vector3.ProjectOnPlane(Vector3.right, up);
                    toEnemy.Normalize();
                    Vector3 tangent = Vector3.Cross(up, toEnemy).normalized;
                    if (((index * 17) & 1) == 0)
                        tangent = -tangent;

                    _strafeRepick[index] -= dt;
                    bool needStrafe = u.pathId < 0 ||
                                      _strafeRepick[index] <= 0f ||
                                      (Paths != null && Paths.IsPathFailed(u.pathId));
                    if (needStrafe)
                    {
                        float orbit = shoot * UnityEngine.Random.Range(0.55f, 0.9f);
                        float along = UnityEngine.Random.Range(-0.35f, 0.35f) * shoot;
                        Vector3 strafeWorld = enemyPos + tangent * orbit + toEnemy * along;
                        Vector3 strafeAxis = (strafeWorld - _planetCenter).normalized;
                        if (_planet != null)
                            strafeWorld = _planet.GetSurfacePointWorld(strafeAxis);
                        ClearPath(ref u);
                        EnsurePath(ref u, index, strafeWorld);
                        _strafeRepick[index] = UnityEngine.Random.Range(1.4f, 2.4f);
                    }
                    FollowPath(ref u, index, dt, speed * 0.85f, enemyPos);

                    if (u.attackCooldown <= 0f)
                    {
                        int dmg = u.role == Rts2Role.Archer ? archerDamage : infantryDamage;
                        FireBolt(index, enemyPos, u.factionId, u.role);
                        DamageUnit(enemy, dmg);
                        u.attackCooldown = attackCooldown;
                    }
                }
                return;
            }

            // Lost sticky target — clear so next hunt is fresh.
            u.targetUnit = -1;

            Vector3 loiter = ProjectHome(faction);
            if (u.order == Rts2Order.Move || u.pathId >= 0)
            {
                // While assaulting, march the staged front; chip nearby enemy buildings.
                FactionController rival = faction.CurrentRival;
                bool rivalFair = rival != null &&
                                 !rival.IsBarAssaultProtected &&
                                 rival.TownHall != null &&
                                 rival.TownHall.IsOperational;
                if (assaulting && faction.AssaultFrontGoal.sqrMagnitude > 1e-4f)
                    loiter = faction.AssaultFrontGoal;

                if (rivalFair)
                {
                    Vector3 hall = rival.TownHall.transform.position;
                    float hallDistSq = (GetWorldPosition(index) - hall).sqrMagnitude;
                    float chipRange = engage * 0.75f;
                    if (hallDistSq <= chipRange * chipRange)
                    {
                        // Light strafe while chipping buildings.
                        _strafeRepick[index] -= dt;
                        if (u.pathId < 0 || _strafeRepick[index] <= 0f)
                        {
                            CachePlanet();
                            Vector3 selfPos = GetWorldPosition(index);
                            Vector3 up = (selfPos - _planetCenter).normalized;
                            Vector3 toHall = Vector3.ProjectOnPlane(hall - selfPos, up);
                            if (toHall.sqrMagnitude < 1e-6f)
                                toHall = Vector3.ProjectOnPlane(Vector3.forward, up);
                            toHall.Normalize();
                            Vector3 tangent = Vector3.Cross(up, toHall).normalized;
                            Vector3 pad = hall + tangent * (chipRange * UnityEngine.Random.Range(0.4f, 0.8f));
                            ClearPath(ref u);
                            EnsurePath(ref u, index, pad);
                            _strafeRepick[index] = UnityEngine.Random.Range(1.5f, 2.6f);
                        }
                        FollowPath(ref u, index, dt, speed * 0.7f, hall);
                        if (u.attackCooldown <= 0f)
                        {
                            FireBolt(index, hall, u.factionId, u.role);
                            rival.TownHall.TakeFactionDamage(
                                u.role == Rts2Role.Archer ? archerDamage : infantryDamage,
                                null);
                            u.attackCooldown = attackCooldown;
                            u.order = Rts2Order.AttackBuilding;
                        }
                        return;
                    }
                    if (!assaulting)
                        loiter = hall;
                }
                FollowPath(ref u, index, dt, speed, loiter);
            }
            else
            {
                HoldNear(ref u, index, loiter, dt, speed * 0.5f);
            }
        }

        void FireBolt(int shooterIndex, Vector3 targetPos, byte factionId, Rts2Role role)
        {
            Vector3 origin = GetWorldPosition(shooterIndex);
            Vector3 up = (origin - _planetCenter).normalized;
            float muzzle = role == Rts2Role.Archer ? 1.35f : 1.05f;
            origin += up * muzzle;

            Vector3 aim = targetPos + up * 0.9f;
            Vector3 delta = aim - origin;
            float distance = delta.magnitude;
            if (distance < 0.35f)
                return;

            Vector3 direction = delta / distance;
            origin += direction * 0.4f;

            Color bolt = WeaponCatalog.ProjectileColorForIndex(factionId);
            bolt.a = 1f;
            FactionSoldierProjectiles.Spawn(origin, direction, distance, bolt, null);

            // Face the shot.
            Rts2Unit u = _units[shooterIndex];
            Vector3 face = Vector3.ProjectOnPlane(direction, up);
            if (face.sqrMagnitude > 1e-6f)
            {
                u.yaw = Mathf.Atan2(face.x, face.z) * Mathf.Rad2Deg;
                _units[shooterIndex] = u;
            }
        }

        void TickNoble(ref Rts2Unit u, int index, float dt)
        {
            FactionController faction = FactionById(u.factionId);
            if (faction == null)
                return;

            if (u.order == Rts2Order.Flee)
            {
                Vector3 home = ProjectHome(faction);
                EnsurePath(ref u, index, home);
                FollowPath(ref u, index, dt, nobleSpeed, home);
                return;
            }

            ClaimableTown town = TownByIndex(u.targetTown);
            if (town == null)
            {
                HoldNear(ref u, index, ProjectHome(faction), dt, nobleSpeed * 0.4f);
                return;
            }

            u.order = Rts2Order.Claim;
            EnsurePath(ref u, index, town.transform.position);
            FollowPath(ref u, index, dt, nobleSpeed, town.transform.position);
            float claimR = 22f;
            if ((GetWorldPosition(index) - town.transform.position).sqrMagnitude <= claimR * claimR)
            {
                ClearPath(ref u);
                town.TryBeginClaimFromSim(faction, index);
            }
        }

        void TickMerchant(ref Rts2Unit u, int index, float dt)
        {
            FactionController faction = FactionById(u.factionId);
            if (faction == null)
                return;

            // gatherTask: 0 outbound (rival/town), 1 return home, 2 owned-town bonus leg
            Vector3 tradeDest = ProjectHome(faction);
            if (u.order != Rts2Order.Trade || u.pathId < 0)
            {
                if (u.gatherTask == 1)
                {
                    tradeDest = faction.Market != null && faction.Market.IsOperational
                        ? faction.Market.transform.position
                        : ProjectHome(faction);
                }
                else if (u.gatherTask == 2)
                {
                    tradeDest = FindOwnedTownPos(faction, ProjectHome(faction));
                }
                else
                {
                    tradeDest = faction.ResolveMerchantTradeDestination();
                    u.gatherTask = 0;
                    Vector3 homeMarket = faction.Market != null && faction.Market.IsOperational
                        ? faction.Market.transform.position
                        : ProjectHome(faction);
                    // No rival market / owned town available — loiter at home.
                    if ((tradeDest - homeMarket).sqrMagnitude < 4f)
                    {
                        u.order = Rts2Order.Trade;
                        HoldNear(ref u, index, homeMarket, dt, workerSpeed * 0.25f);
                        return;
                    }
                }
                u.order = Rts2Order.Trade;
                EnsurePath(ref u, index, tradeDest);
            }
            else if (Paths != null && Paths.TryGetPath(u.pathId, out PathService.Path existing) &&
                     existing.waypoints.Count > 0)
            {
                tradeDest = existing.waypoints[existing.waypoints.Count - 1];
            }

            Vector3 fromPos = GetWorldPosition(index);
            float ownedFrac = faction.MerchantRouteOwnedFraction(fromPos, tradeDest);
            float speedMul = 0.9f;
            if (faction.Economy != null)
            {
                float hostileMul = faction.Economy.territoryTradeHostileSpeedMul;
                // Owned corridors full speed; contested/hostile slow down.
                speedMul = Mathf.Lerp(hostileMul, 0.95f, ownedFrac);
            }
            FollowPath(ref u, index, dt, workerSpeed * speedMul, tradeDest);
            float arriveR = 16f;
            if ((fromPos - tradeDest).sqrMagnitude <= arriveR * arriveR ||
                (Paths != null && Paths.TryGetPath(u.pathId, out PathService.Path path) &&
                 u.pathWaypoint >= path.waypoints.Count - 1))
            {
                CompleteMerchantLeg(ref u, faction, tradeDest, ownedFrac);
            }
        }

        Vector3 FindOwnedTownPos(FactionController faction, Vector3 fallback)
        {
            ClaimableTown best = null;
            float bestSq = float.PositiveInfinity;
            Vector3 home = fallback;
            IReadOnlyList<ClaimableTown> towns = FactionRegistry.Towns;
            for (int i = 0; i < towns.Count; i++)
            {
                ClaimableTown town = towns[i];
                if (town == null || town.Owner != faction)
                    continue;
                float d = (town.transform.position - home).sqrMagnitude;
                if (d < bestSq)
                {
                    bestSq = d;
                    best = town;
                }
            }
            return best != null ? best.transform.position : fallback;
        }

        void CompleteMerchantLeg(ref Rts2Unit u, FactionController faction, Vector3 arrivedAt, float routeOwnedFraction = -1f)
        {
            ClearPath(ref u);
            u.order = Rts2Order.Trade;

            bool atRivalMarket = false;
            bool atOwnedTown = false;
            IReadOnlyList<FactionController> factions = FactionRegistry.Factions;
            for (int i = 0; i < factions.Count; i++)
            {
                FactionController other = factions[i];
                if (other == null || other == faction || other.Market == null || !other.Market.IsOperational)
                    continue;
                if ((other.Market.transform.position - arrivedAt).sqrMagnitude <= 22f * 22f)
                {
                    atRivalMarket = true;
                    break;
                }
            }
            IReadOnlyList<ClaimableTown> towns = FactionRegistry.Towns;
            for (int i = 0; i < towns.Count; i++)
            {
                ClaimableTown town = towns[i];
                if (town == null || town.Owner != faction)
                    continue;
                if ((town.transform.position - arrivedAt).sqrMagnitude <= 22f * 22f)
                {
                    atOwnedTown = true;
                    break;
                }
            }

            if (u.gatherTask == 0)
            {
                faction.ApplyMerchantTradeReward(atRivalMarket, atOwnedTown, routeOwnedFraction);
                // After rival market, optionally visit an owned town before returning.
                if (atRivalMarket && faction.CountOwnedTowns() > 0 && ((u.factionId + u.pathWaypoint) & 1) == 0)
                    u.gatherTask = 2;
                else
                    u.gatherTask = 1;
            }
            else if (u.gatherTask == 2)
            {
                faction.ApplyMerchantTradeReward(false, true, routeOwnedFraction);
                u.gatherTask = 1;
            }
            else
            {
                // Home unload — start a new outbound trip.
                u.gatherTask = 0;
            }
        }

        void TickMarchToDesignatedSite(ref Rts2Unit u, int index, float dt, FactionController faction)
        {
            Vector3 site = faction.GetSafePosition();
            float claimR = faction.Economy != null
                ? Mathf.Max(4f, faction.Economy.designatedSiteClaimRadius)
                : 22f;
            Vector3 pos = GetWorldPosition(index);
            CachePlanet();
            Vector3 siteAxis = (site - _planetCenter).normalized;
            Vector3 posAxis = (pos - _planetCenter).normalized;
            // Close enough on the globe (even if nav can't finish the last meters).
            bool nearSite = (pos - site).sqrMagnitude <= claimR * claimR ||
                            Vector3.Dot(posAxis, siteAxis) >= Mathf.Cos(10f * Mathf.Deg2Rad);

            if (nearSite)
            {
                if (faction.FoundCampus(site))
                    return;
                u.order = Rts2Order.Move;
                HoldNear(ref u, index, site, dt, workerSpeed * 0.25f);
                return;
            }

            u.order = Rts2Order.Move;
            _stuck[index] -= dt;
            bool pathFailed = Paths != null && Paths.IsPathFailed(u.pathId);
            bool needPath = u.pathId < 0 || _stuck[index] <= 0f || pathFailed;
            if (needPath)
            {
                ClearPath(ref u);
                // Repeated path failure: claim the pad remotely so the faction is not soft-locked.
                if (pathFailed && _stuck[index] <= 0f)
                {
                    faction.FoundCampus(site);
                    if (faction.HasFoundedCampus)
                        return;
                }
                EnsurePath(ref u, index, site);
                _stuck[index] = UnityEngine.Random.Range(5f, 9f);
            }
            FollowPath(ref u, index, dt, workerSpeed * 0.9f, site);
        }

        void TickRoam(ref Rts2Unit u, int index, float dt, FactionController faction)
        {
            u.order = Rts2Order.Roam;
            float repath = faction.Economy != null ? Mathf.Max(1f, faction.Economy.roamRepathSeconds) : 8f;
            _stuck[index] -= dt;
            bool needDest = u.pathId < 0 || _stuck[index] <= 0f ||
                            (Paths != null && Paths.IsPathFailed(u.pathId));

            if (needDest)
            {
                ClearPath(ref u);
                if (TryPickRoamDestination(GetWorldPosition(index), out Vector3 dest))
                {
                    EnsurePath(ref u, index, dest);
                    FollowPath(ref u, index, dt, workerSpeed * 0.85f, dest);
                }
                _stuck[index] = repath * UnityEngine.Random.Range(0.7f, 1.3f);
                return;
            }

            Vector3 fallback = GetWorldPosition(index);
            if (Paths != null && Paths.TryGetPath(u.pathId, out PathService.Path roamPath) &&
                roamPath.waypoints.Count > 0)
                fallback = roamPath.waypoints[roamPath.waypoints.Count - 1];
            FollowPath(ref u, index, dt, workerSpeed * 0.85f, fallback);
        }

        bool TryPickRoamDestination(Vector3 from, out Vector3 dest)
        {
            dest = from;
            CachePlanet();
            if (_planet == null || _factionSim == null)
                return false;

            Vector3 fromAxis = (from - _planetCenter).normalized;
            for (int attempt = 0; attempt < 12; attempt++)
            {
                Vector3 jitter = UnityEngine.Random.onUnitSphere;
                float ang = UnityEngine.Random.Range(0.04f, 0.18f);
                Vector3 guess = (fromAxis + jitter * ang).normalized;
                if (!_factionSim.TryFindMainlandSpawnAxis(guess, out Vector3 dry))
                    continue;
                dest = _planet.GetSurfacePointWorld(dry);
                return true;
            }
            return false;
        }

        void CheckRoamingWorkerMeetings()
        {
            // Designated-site founding replaces meet-to-found.
            if (_factionSim != null &&
                _factionSim.economy != null &&
                _factionSim.economy.useDesignatedSiteFounding)
                return;

            float meetR = 50f;
            if (_factionSim != null)
                meetR = Mathf.Max(2f, _factionSim.economy.workerMeetRadius);
            float meetSq = meetR * meetR;

            for (int i = 0; i < _alive; i++)
            {
                if (_units[i].alive == 0 || _units[i].role != Rts2Role.Worker)
                    continue;
                if (_units[i].order != Rts2Order.Roam)
                    continue;
                FactionController a = FactionById(_units[i].factionId);
                if (a == null || a.HasFoundedCampus)
                    continue;

                Vector3 pi = GetWorldPosition(i);
                for (int j = i + 1; j < _alive; j++)
                {
                    if (_units[j].alive == 0 || _units[j].role != Rts2Role.Worker)
                        continue;
                    if (_units[j].factionId != _units[i].factionId)
                        continue;
                    if (_units[j].order != Rts2Order.Roam)
                        continue;
                    Vector3 pj = GetWorldPosition(j);
                    if ((pi - pj).sqrMagnitude > meetSq)
                        continue;
                    Vector3 mid = (pi + pj) * 0.5f;
                    // Too close to another faction's campus — keep roaming for a later meet.
                    if (!a.CanFoundCampusAt(mid))
                        continue;
                    a.FoundCampus(mid);
                    return;
                }
            }
        }

        void TryPlayerInteractFounding()
        {
            if (!WasInteractPressed())
                return;

            Transform player = RuntimeSceneRefs.GetPlayerTransform(0.25f);
            if (player == null)
            {
                var health = Object.FindFirstObjectByType<PlayerHealth>(FindObjectsInactive.Exclude);
                if (health == null)
                    return;
                player = health.transform;
            }

            float radius = 4.5f;
            if (_factionSim != null)
                radius = Mathf.Max(1f, _factionSim.economy.playerInteractRadius);
            float r2 = radius * radius;
            Vector3 p = player.position;
            int best = -1;
            float bestD = r2;
            for (int i = 0; i < _alive; i++)
            {
                if (_units[i].alive == 0 || _units[i].role != Rts2Role.Worker)
                    continue;
                if (_units[i].order != Rts2Order.Roam)
                    continue;
                FactionController faction = FactionById(_units[i].factionId);
                if (faction == null || faction.HasFoundedCampus)
                    continue;
                float d = (GetWorldPosition(i) - p).sqrMagnitude;
                if (d < bestD)
                {
                    bestD = d;
                    best = i;
                }
            }
            if (best < 0)
                return;

            FactionController found = FactionById(_units[best].factionId);
            if (found == null)
                return;
            Vector3 at = GetWorldPosition(best);
            if (!found.CanFoundCampusAt(at))
                return;
            found.FoundCampus(at);
        }

        static bool WasInteractPressed()
        {
            if (Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame)
                return true;
            if (Gamepad.current != null && Gamepad.current.buttonWest.wasPressedThisFrame)
                return true;
            return false;
        }

        bool IsSiteClaimed(int siteId, int selfIndex)
        {
            for (int i = 0; i < _alive; i++)
            {
                if (i == selfIndex || _units[i].alive == 0)
                    continue;
                if (_units[i].role != Rts2Role.Worker)
                    continue;
                if (_units[i].resourceSiteId == siteId && _units[i].carryAmount == 0)
                    return true;
            }
            return false;
        }

        void HoldNear(ref Rts2Unit u, int index, Vector3 home, float dt, float speed)
        {
            Vector3 homeAxis = (home - _planetCenter).normalized;
            Vector3 side = Vector3.Cross(homeAxis, Vector3.up);
            if (side.sqrMagnitude < 1e-6f)
                side = Vector3.Cross(homeAxis, Vector3.right);
            side.Normalize();
            float ang = 6f / Mathf.Max(1f, u.radius);
            Vector3 wait = (homeAxis + side * (Mathf.Cos(index * 1.7f) * ang)).normalized;
            Vector3 waitPos = _planetCenter + wait * (_planet != null ? _planet.GetSurfaceRadiusWorld(wait) : u.radius);
            u.order = Rts2Order.Idle;
            EnsurePath(ref u, index, waitPos);
            FollowPath(ref u, index, dt, speed, waitPos);
        }

        void EnsurePath(ref Rts2Unit u, int index, Vector3 dest)
        {
            if (Paths == null || !Paths.IsReady)
                return;

            if (u.pathId >= 0)
            {
                if (Paths.IsPathFailed(u.pathId))
                {
                    ClearPath(ref u);
                }
                else if (Paths.TryGetPath(u.pathId, out PathService.Path existing))
                {
                    if (existing.waypoints.Count > 0)
                    {
                        Vector3 last = existing.waypoints[existing.waypoints.Count - 1];
                        if ((last - dest).sqrMagnitude < 100f)
                            return;
                    }
                    ClearPath(ref u);
                }
                else
                {
                    // Path still queued/solving — do not cancel (was thrashing the queue).
                    return;
                }
            }

            u.pathId = Paths.RequestPath(GetWorldPosition(index), dest);
            u.pathWaypoint = 0;
        }

        void RequestPathTo(ref Rts2Unit u, int index, Vector3 dest)
        {
            ClearPath(ref u);
            if (Paths != null && Paths.IsReady)
            {
                u.pathId = Paths.RequestPath(GetWorldPosition(index), dest);
                u.pathWaypoint = 0;
            }
        }

        void ClearPath(ref Rts2Unit u)
        {
            if (u.pathId >= 0 && Paths != null)
                Paths.ReleasePath(u.pathId);
            u.pathId = -1;
            u.pathWaypoint = 0;
        }

        void FollowPath(ref Rts2Unit u, int index, float dt, float speed, Vector3 fallbackDest)
        {
            Vector3 axis = new Vector3(u.axis.x, u.axis.y, u.axis.z).normalized;
            Vector3 goalAxis = (fallbackDest - _planetCenter);
            if (goalAxis.sqrMagnitude > 1e-8f)
                goalAxis.Normalize();
            else
                goalAxis = axis;

            if (Paths != null && u.pathId >= 0)
            {
                if (Paths.IsPathFailed(u.pathId))
                {
                    ClearPath(ref u);
                }
                else if (Paths.TryGetPath(u.pathId, out PathService.Path path) && path.waypoints.Count > 0)
                {
                    int wp = Mathf.Clamp(u.pathWaypoint, 0, path.waypoints.Count - 1);
                    Vector3 target = path.waypoints[wp];
                    goalAxis = (target - _planetCenter).normalized;
                    float arrive = Mathf.Max(16f, speed * 0.75f);
                    if ((GetWorldPosition(index) - target).sqrMagnitude < arrive * arrive)
                        u.pathWaypoint = Mathf.Min(wp + 1, path.waypoints.Count - 1);
                }
                // else: pending path — steer toward fallbackDest until waypoints arrive
            }

            SteerToward(ref u, index, axis, goalAxis, dt, speed);
        }

        void FollowPath(ref Rts2Unit u, int index, float dt, float speed)
        {
            FollowPath(ref u, index, dt, speed, GetWorldPosition(index));
        }

        void SteerToward(ref Rts2Unit u, int index, Vector3 axis, Vector3 goalAxis, float dt, float speed)
        {
            // Tangent step on sphere.
            Vector3 toGoal = goalAxis - axis;
            Vector3 planar = toGoal - axis * Vector3.Dot(toGoal, axis);
            if (planar.sqrMagnitude > 1e-10f)
            {
                float step = Mathf.Min(speed * dt / Mathf.Max(1f, u.radius), 0.25f);
                axis = (axis + planar.normalized * step).normalized;
            }

            // Soft separation.
            Vector3 push = Vector3.zero;
            int samples = 0;
            Vector3 myPos = axis * Mathf.Max(1f, u.radius);
            float minDist = separationRadius;
            float minDistSq = minDist * minDist;
            int start = Mathf.Max(0, index - 12);
            int end = Mathf.Min(_alive, index + 13);
            for (int i = start; i < end; i++)
            {
                if (i == index || _units[i].alive == 0)
                    continue;
                Vector3 oa = new Vector3(_units[i].axis.x, _units[i].axis.y, _units[i].axis.z).normalized;
                Vector3 other = oa * Mathf.Max(1f, _units[i].radius);
                Vector3 delta = myPos - other;
                float d2 = delta.sqrMagnitude;
                if (d2 > 1e-8f && d2 < minDistSq)
                {
                    float d = Mathf.Sqrt(d2);
                    push += (delta / d) * (1f - d / minDist);
                    samples++;
                }
            }
            if (samples > 0)
            {
                push /= samples;
                Vector3 tangential = push - axis * Vector3.Dot(push, axis);
                axis = (axis + tangential * (4f * dt / Mathf.Max(1f, u.radius))).normalized;
            }

            float radius = _planet != null ? _planet.GetSurfaceRadiusWorld(axis) : u.radius;
            float halfH = ScaleForRole(u.role).y;
            u.axis = new float3(axis.x, axis.y, axis.z);
            u.radius = Mathf.Max(1f, radius + halfH + 0.1f);

            if (planar.sqrMagnitude > 1e-8f)
            {
                Vector3 face = Vector3.ProjectOnPlane(planar, axis);
                if (face.sqrMagnitude > 1e-6f)
                    u.yaw = Mathf.Atan2(face.x, face.z) * Mathf.Rad2Deg;
            }
        }

        Vector3 ProjectHome(FactionController faction)
        {
            Vector3 home = faction.GetSafePosition();
            if (_planet == null)
                return home;
            Vector3 axis = (home - _planetCenter).normalized;
            float r = _planet.GetSurfaceRadiusWorld(axis);
            return _planetCenter + axis * r;
        }

        float Speed(Rts2Role role)
        {
            switch (role)
            {
                case Rts2Role.Infantry: return soldierSpeed;
                case Rts2Role.Archer: return heavySpeed;
                case Rts2Role.Noble: return nobleSpeed;
                default: return workerSpeed;
            }
        }

        float MaxHp(Rts2Role role)
        {
            if (_factionSim == null)
                return 50f;
            switch (role)
            {
                case Rts2Role.Infantry: return _factionSim.combat.soldierMaxHealth;
                case Rts2Role.Archer: return _factionSim.combat.archerMaxHealth;
                case Rts2Role.Noble: return _factionSim.combat.nobleMaxHealth;
                default: return Mathf.Max(1, _factionSim.combat.soldierMaxHealth / 2);
            }
        }

        static Vector3 ScaleForRole(Rts2Role role)
        {
            switch (role)
            {
                case Rts2Role.Archer: return new Vector3(0.95f, 1.35f, 0.95f); // Heavy silhouette
                case Rts2Role.Noble: return new Vector3(0.7f, 1.15f, 0.7f);
                case Rts2Role.Worker: return new Vector3(0.5f, 0.75f, 0.5f);
                case Rts2Role.Merchant: return new Vector3(0.55f, 0.85f, 0.55f);
                default: return new Vector3(0.6f, 1f, 0.6f);
            }
        }

        FactionController FactionById(int factionId)
        {
            IReadOnlyList<FactionController> factions = FactionRegistry.Factions;
            for (int i = 0; i < factions.Count; i++)
                if (factions[i] != null && factions[i].RuntimeIndex == factionId)
                    return factions[i];
            return null;
        }

        PlayerHealth ResolvePlayerHealth(Transform player)
        {
            if (_cachedPlayerHealth != null && Time.time < _playerHealthCacheUntil)
                return _cachedPlayerHealth;

            PlayerHealth health = null;
            if (player != null)
                health = player.GetComponentInParent<PlayerHealth>();
            if (health == null)
                health = Object.FindFirstObjectByType<PlayerHealth>(FindObjectsInactive.Exclude);

            _cachedPlayerHealth = health;
            _playerHealthCacheUntil = Time.time + 2f;
            return health;
        }

        static ClaimableTown TownByIndex(int index)
        {
            IReadOnlyList<ClaimableTown> towns = FactionRegistry.Towns;
            if (index < 0 || index >= towns.Count)
                return null;
            return towns[index];
        }

        void RecountAlive()
        {
            int n = 0;
            for (int i = 0; i < _alive; i++)
                if (_units[i].alive != 0)
                    n++;
            AliveCount = n;
        }

        void TrimTrailingDead()
        {
            while (_alive > 0 && _units[_alive - 1].alive == 0)
                _alive--;
            RecountAlive();
        }

        void RenderInstances()
        {
            if (!enableUnitCardLod || _alive <= 0)
                return;
            EnsureRenderAssets();
            EnsureBakedRoleCards();
            if (_mesh == null || _mat == null)
                return;
            CachePlanet();

            Camera cam = Camera.main;
            Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
            Vector3 camFwd = cam != null ? cam.transform.forward : Vector3.forward;
            float surfaceR = 0f;
            bool horizonCull = false;
            if (cam != null && _planet != null)
            {
                surfaceR = PlanetHorizonCulling.SampleSurfaceRadius(_planet, _planetCenter, camPos, 1f);
                horizonCull = surfaceR > 1f;
            }

            float maxDist = Mathf.Max(40f, unitCardMaxDistance);
            float maxDistSq = maxDist * maxDist;

            int filled = 0;
            Color batchColor = default;
            Rts2Role batchRole = Rts2Role.Worker;
            Texture2D batchTex = null;
            bool hasBatch = false;
            for (int i = 0; i < _alive; i++)
            {
                Rts2Unit u = _units[i];
                if (u.alive == 0)
                    continue;
                // Near units use character prefabs — skip card to avoid double-draw.
                if (_visuals != null && _visuals.HasVisual(i))
                    continue;

                Vector3 feet = GetFeetWorldPosition(i);
                if ((feet - camPos).sqrMagnitude > maxDistSq)
                    continue;
                if (horizonCull &&
                    !PlanetHorizonCulling.IsVisibleInWorld(
                        _planet, _planetCenter, surfaceR, camPos, feet, sticky: false))
                    continue;

                // Keep model look; light faction tint so armies stay readable.
                Color unitColor = Color.Lerp(Color.white, ColorForFaction(u.factionId, u.role), 0.38f);
                unitColor.a = 1f;
                Texture2D tex = CardTextureForRole(u.role);

                if (hasBatch &&
                    (filled >= InstanceBatch ||
                     u.role != batchRole ||
                     tex != batchTex ||
                     !AlmostSameColor(batchColor, unitColor)))
                {
                    FlushBatch(filled, batchColor, batchTex);
                    filled = 0;
                }

                Vector3 up = new Vector3(u.axis.x, u.axis.y, u.axis.z).normalized;
                if (up.sqrMagnitude < 1e-6f)
                    up = Vector3.up;
                Vector3 scale = CardScaleForRole(u.role);
                Vector3 pos = feet + up * (scale.y * 0.5f);

                // Billboard: face camera, stay upright on planet.
                Vector3 toCam = camPos - pos;
                Vector3 flat = Vector3.ProjectOnPlane(toCam, up);
                if (flat.sqrMagnitude < 1e-6f)
                    flat = Vector3.ProjectOnPlane(-camFwd, up);
                if (flat.sqrMagnitude < 1e-6f)
                    flat = Vector3.ProjectOnPlane(Vector3.forward, up);
                flat.Normalize();
                Quaternion rot = Quaternion.LookRotation(-flat, up);

                _batch[filled] = Matrix4x4.TRS(pos, rot, scale);
                _batchColors[filled] = unitColor;
                batchColor = unitColor;
                batchRole = u.role;
                batchTex = tex;
                hasBatch = true;
                filled++;
            }
            if (filled > 0)
                FlushBatch(filled, batchColor, batchTex);
        }

        static Vector3 CardScaleForRole(Rts2Role role)
        {
            // Silhouette is tall/narrow (person-shaped), not a square card.
            switch (role)
            {
                case Rts2Role.Archer: return new Vector3(1.15f, 2.7f, 1f); // Heavy
                case Rts2Role.Noble: return new Vector3(1.0f, 2.4f, 1f);
                case Rts2Role.Worker: return new Vector3(0.85f, 2.05f, 1f);
                case Rts2Role.Merchant: return new Vector3(0.9f, 2.15f, 1f);
                default: return new Vector3(0.95f, 2.3f, 1f); // Raider
            }
        }

        static bool AlmostSameColor(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < 0.02f &&
                   Mathf.Abs(a.g - b.g) < 0.02f &&
                   Mathf.Abs(a.b - b.b) < 0.02f;
        }

        void FlushBatch(int count, Color color, Texture2D tex)
        {
            if (count <= 0)
                return;
            BindCardTexture(tex != null ? tex : _cardTex);
            _mpb.Clear();
            if (_mat.HasProperty(ColorId))
                _mpb.SetColor(ColorId, color);
            else if (_mat.HasProperty(ColorIdFallback))
                _mpb.SetColor(ColorIdFallback, color);

            float radius = _planet != null ? _planet.GetBaseRadiusWorld() * 2.75f : 250f;
            var rp = new RenderParams(_mat)
            {
                worldBounds = new Bounds(_planetCenter, Vector3.one * (radius * 2f)),
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                lightProbeUsage = LightProbeUsage.Off,
                matProps = _mpb
            };
            Graphics.RenderMeshInstanced(rp, _mesh, 0, _batch, count);
        }

        void FlushBatch(int count, Color color) => FlushBatch(count, color, _cardTex);

        static readonly Color[] FactionPalette =
        {
            new Color(0.15f, 0.85f, 0.35f, 1f), // green
            new Color(0.95f, 0.25f, 0.20f, 1f), // red
            new Color(0.20f, 0.45f, 1.00f, 1f), // blue
            new Color(1.00f, 0.80f, 0.15f, 1f), // yellow
            new Color(0.85f, 0.25f, 0.95f, 1f), // magenta
            new Color(0.15f, 0.90f, 0.90f, 1f), // cyan
            new Color(1.00f, 0.50f, 0.10f, 1f), // orange
            new Color(0.95f, 0.95f, 0.95f, 1f)  // white
        };

        static Color ColorForFaction(int factionId, Rts2Role role)
        {
            int n = FactionPalette.Length;
            int i = ((factionId % n) + n) % n;
            Color baseC = FactionPalette[i];
            if (role == Rts2Role.Archer)
                baseC = Color.Lerp(baseC, new Color(0.15f, 0.15f, 0.18f), 0.35f); // Heavy: darker
            if (role == Rts2Role.Noble)
                baseC = Color.Lerp(baseC, Color.yellow, 0.3f);
            if (role == Rts2Role.Worker)
                baseC = Color.Lerp(baseC, new Color(0.55f, 0.4f, 0.2f), 0.28f);
            if (role == Rts2Role.Merchant)
                baseC = Color.Lerp(baseC, new Color(0.3f, 0.75f, 0.95f), 0.25f);
            return baseC;
        }

#if UNITY_EDITOR
        void OnDrawGizmos()
        {
            if (!sceneViewUnitDots || !Application.isPlaying || _alive <= 0)
                return;
            if (_planet == null)
                CachePlanet();

            UnityEditor.SceneView sceneView = UnityEditor.SceneView.lastActiveSceneView;
            Vector3 camPos = sceneView != null && sceneView.camera != null
                ? sceneView.camera.transform.position
                : Vector3.zero;
            bool useFade = sceneViewUnitDotsDistanceFade && sceneView != null && sceneView.camera != null;
            float fadeStart = Mathf.Max(10f, sceneViewUnitDotsFadeStart);
            float fadeEnd = Mathf.Max(fadeStart + 1f, sceneViewUnitDotsFadeEnd);

            for (int i = 0; i < _alive; i++)
            {
                Rts2Unit u = _units[i];
                if (u.alive == 0)
                    continue;
                if (sceneViewUnitDotsCombatOnly && !Rts2Roles.IsCombat(u.role))
                    continue;

                Vector3 pos = GetWorldPosition(i);
                Vector3 up = _planetCenter.sqrMagnitude > 1e-6f
                    ? (pos - _planetCenter).normalized
                    : Vector3.up;
                pos += up * 2.2f;

                float fade = 1f;
                if (useFade)
                {
                    float dist = Vector3.Distance(camPos, pos);
                    fade = 1f - Mathf.InverseLerp(fadeStart, fadeEnd, dist);
                    if (fade <= 0.04f)
                        continue;
                }

                Color c = ColorForFaction(u.factionId, u.role);
                if (u.order == Rts2Order.Flee)
                    c = Color.Lerp(c, Color.white, 0.45f);
                else if (u.order == Rts2Order.AttackUnit || u.order == Rts2Order.AttackBuilding)
                    c = Color.Lerp(c, Color.red, 0.25f);
                c.a = fade;
                Gizmos.color = c;

                float r = 1.35f;
                switch (u.role)
                {
                    case Rts2Role.Archer: r = 2.1f; break;
                    case Rts2Role.Noble: r = 1.8f; break;
                    case Rts2Role.Worker: r = 1.1f; break;
                    case Rts2Role.Merchant: r = 1.25f; break;
                }
                r *= Mathf.Lerp(0.55f, 1f, fade);
                Gizmos.DrawSphere(pos, r);

                // Order read: small marker above the dot.
                Vector3 tip = pos + up * (r * 1.8f);
                switch (u.order)
                {
                    case Rts2Order.AttackUnit:
                    case Rts2Order.AttackBuilding:
                        Gizmos.color = new Color(1f, 0.25f, 0.15f, fade);
                        Gizmos.DrawLine(pos, tip);
                        Gizmos.DrawWireSphere(tip, r * 0.35f);
                        break;
                    case Rts2Order.Flee:
                        Gizmos.color = new Color(1f, 1f, 1f, fade);
                        Gizmos.DrawWireCube(tip, Vector3.one * (r * 0.7f));
                        break;
                    case Rts2Order.Move:
                        Gizmos.color = new Color(c.r, c.g, c.b, fade * 0.85f);
                        Gizmos.DrawLine(pos, tip);
                        break;
                    default:
                        break;
                }
            }
        }
#endif
    }
}
