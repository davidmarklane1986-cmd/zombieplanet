using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Far crowd is GPU-instanced Kenny meshes that roam on the sphere to the horizon.
/// Nearby / crosshair agents promote to full AI bodies.
/// </summary>
public sealed class ZombieHordeSim : MonoBehaviour
{
    public const int MaxAgents = 10000;
    public const int MaxRealized = 24;
    const int MaxInstanceDraw = 1200;
    const int InstanceBatch = 1023;
    const int LatBands = 12;
    const int LonBands = 24;
    const int CellCount = LatBands * LonBands;
    const int HordeMin = 4;
    const float ChaseRange = 70f;
    const float SimSpeed = 3.6f;
    const float RealizeRange = 28f;
    const float RealizeKeepRange = 48f;
    const float AimRealizeRange = 160f;
    const float AimScreenRadius = 0.14f;
    const float HorizonDot = -0.02f; // sim only
    const float SilhouetteHeight = 1.7f;
    const int DrawBands = 4;
    // Prefer keeping last-frame draws so walking/turning doesn't thrash band slots.
    const float DrawStickyBias = 0.55f;

    public static ZombieHordeSim Instance { get; private set; }
    public static bool HasInstance => Instance != null;
    public int Alive => _alive;

    struct Agent
    {
        public Vector3 dir;
        public float radius;
        public float yaw;
        public float wanderYaw;
        public float phase;
        public byte variant;
        public sbyte body; // -1 = instanced
        public short cell;
    }

    struct HordeCell
    {
        public int count;
        public Vector3 steer;
        public float yaw;
        public bool packed;
    }

    readonly Agent[] _agents = new Agent[MaxAgents];
    int _alive;
    int _simCursor;
    readonly HordeCell[] _cells = new HordeCell[CellCount];
    int _cellRebuild;

    ZombieSpawner _spawner;
    Transform _planet;
    Planet _planetComp;
    PlanetOceanLayer _ocean;
    Vector3 _planetCenter;
    LayerMask _groundMask = ~0;

    GameObject[] _bodies;
    ZombieAI[] _bodyAi;
    byte[] _bodyBusy; // 0 free, 1 realized, 2 corpse
    int[] _bodyAgent;
    Mesh _instanceMesh;
    Material _instanceMat;
    readonly Matrix4x4[] _batch = new Matrix4x4[InstanceBatch];
    readonly bool[] _drawnLast = new bool[MaxAgents];
    static readonly Plane[] s_Frustum = new Plane[6];
    static readonly int[] s_NearestScratch = new int[MaxRealized];
    static readonly float[] s_NearestDist = new float[MaxRealized];
    static readonly int[] s_DrawScratch = new int[MaxInstanceDraw];
    static readonly float[] s_DrawDist = new float[MaxInstanceDraw];

    public void Configure(ZombieSpawner spawner)
    {
        _spawner = spawner;
        if (spawner != null)
            _groundMask = spawner.groundMask;
        EnsurePool();
    }

    void Awake()
    {
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
        if (_instanceMesh != null && _instanceMesh.name == "HordeInstance")
            Destroy(_instanceMesh);
        if (_instanceMat != null)
            Destroy(_instanceMat);
    }

    public void Clear()
    {
        _alive = 0;
        _simCursor = 0;
        if (_bodies == null)
            return;
        for (int i = 0; i < _bodies.Length; i++)
        {
            _bodyBusy[i] = 0;
            _bodyAgent[i] = -1;
            if (_bodies[i] != null)
                _bodies[i].SetActive(false);
        }
    }

    public bool TryAddAgent(Vector3 worldPos, byte variant)
    {
        if (_alive >= MaxAgents || _alive >= (_spawner != null ? _spawner.maxAliveZombies : MaxAgents))
            return false;
        if (_planet == null)
            CachePlanet();
        Vector3 center = _planetCenter;
        Vector3 radial = worldPos - center;
        float radius = radial.magnitude;
        if (radius < 1f)
            return false;

        Vector3 dir = radial / radius;
        // Slight angular jitter so kill-bursts don't land on the exact same point.
        dir = (dir + Random.onUnitSphere * 0.04f).normalized;
        radius = SampleSurfaceRadius(dir, radius);
        if (!IsAboveWater(dir, ref radius))
        {
            if (!TryRescueToDry(ref dir, ref radius, dir))
                return false;
        }

        _agents[_alive] = new Agent
        {
            dir = dir,
            radius = radius,
            yaw = Random.Range(0f, 360f),
            wanderYaw = Random.Range(0f, 360f),
            phase = Random.Range(0f, 6.28f),
            variant = variant,
            body = -1,
            cell = -1
        };
        _alive++;
        return true;
    }

    public void NotifyBodyDied(ZombieAI ai)
    {
        int slot = FindBody(ai);
        if (slot < 0)
            return;
        int agent = _bodyAgent[slot];
        _bodyBusy[slot] = 2;
        if (agent >= 0 && agent < _alive)
            RemoveAgent(agent);
        _bodyAgent[slot] = -1;
    }

    public bool TryRecycleBody(ZombieAI ai)
    {
        int slot = FindBody(ai);
        if (slot < 0)
            return false;
        if (_bodyAi[slot] != null)
            _bodyAi[slot].ResetForHordeReuse();
        _bodies[slot].SetActive(false);
        _bodyBusy[slot] = 0;
        _bodyAgent[slot] = -1;
        return true;
    }

    public bool Owns(ZombieAI ai) => FindBody(ai) >= 0;

    void LateUpdate()
    {
        if (_alive <= 0)
            return;
        CachePlanet();
        Transform player = RuntimeSceneRefs.GetPlayerTransform(0.05f);
        Vector3 playerPos = player != null ? player.position : _planetCenter + Vector3.up * 40f;
        Camera cam = Camera.main;
        if (cam != null)
            GeometryUtility.CalculateFrustumPlanes(cam, s_Frustum);
        if ((_cellRebuild++ & 7) == 0)
            RebuildHordeCells(playerPos);
        Simulate(playerPos);
        SyncRealized(playerPos, cam);
        DrawInstances(playerPos, cam);
    }

    void RebuildHordeCells(Vector3 playerPos)
    {
        // Cells are only used for density bookkeeping / cheap updates — not shared world steering
        // (shared steer made packs rush one vector and stack into a pile).
        for (int c = 0; c < CellCount; c++)
        {
            _cells[c].count = 0;
            _cells[c].packed = false;
            _cells[c].steer = Vector3.zero;
        }

        for (int i = 0; i < _alive; i++)
        {
            Agent a = _agents[i];
            int cell = HashCell(a.dir);
            a.cell = (short)cell;
            _agents[i] = a;
            if (cell >= 0 && cell < CellCount)
                _cells[cell].count++;
        }

        for (int c = 0; c < CellCount; c++)
            _cells[c].packed = _cells[c].count >= HordeMin;
    }

    static int HashCell(Vector3 dir)
    {
        float lat = Mathf.Acos(Mathf.Clamp(dir.y, -1f, 1f));
        float lon = Mathf.Atan2(dir.z, dir.x);
        int iy = Mathf.Clamp((int)(lat / Mathf.PI * LatBands), 0, LatBands - 1);
        int ix = (int)((lon + Mathf.PI) / (Mathf.PI * 2f) * LonBands);
        if (ix < 0)
            ix = 0;
        if (ix >= LonBands)
            ix = LonBands - 1;
        return iy * LonBands + ix;
    }

    void Simulate(Vector3 playerPos)
    {
        Vector3 center = _planetCenter;
        Vector3 playerDir = playerPos - center;
        if (playerDir.sqrMagnitude > 1e-6f)
            playerDir.Normalize();

        float waterLine = GetWaterLine();
        float dt = Time.deltaTime;
        int stride = _alive > 2000 ? 16 : (_alive > 600 ? 8 : (_alive > 150 ? 4 : 1));
        int start = _simCursor % Mathf.Max(1, stride);
        _simCursor++;

        for (int i = start; i < _alive; i += stride)
        {
            Agent a = _agents[i];
            if (a.body >= 0)
                continue;

            // Pull anyone sitting on the seabed onto dry land.
            if (waterLine > 1e-3f && a.radius < waterLine)
            {
                Vector3 d = a.dir;
                float r = a.radius;
                if (TryRescueToDry(ref d, ref r, a.dir))
                {
                    a.dir = d;
                    a.radius = r;
                    a.cell = (short)HashCell(a.dir);
                }
                _agents[i] = a;
            }

            float facing = Vector3.Dot(a.dir, playerDir);
            // Deep far-side only: skip. Everyone else roams so the horizon isn't a frozen prop field.
            if (facing < -0.55f)
                continue;

            Vector3 pos = center + a.dir * a.radius;
            Vector3 up = a.dir;
            Vector3 toPlayer = Vector3.ProjectOnPlane(playerPos - pos, up);
            float toPlayerSq = toPlayer.sqrMagnitude;
            float arc = Vector3.Angle(a.dir, playerDir) * Mathf.Deg2Rad * a.radius;
            bool chase = facing > 0.42f && arc < ChaseRange && toPlayerSq > 0.25f;

            Vector3 step;
            float speed;
            if (chase)
            {
                step = toPlayer / Mathf.Sqrt(toPlayerSq);
                speed = SimSpeed;
            }
            else
            {
                a.wanderYaw += (Mathf.PerlinNoise(i * 0.17f, Time.time * 0.08f) - 0.5f) * 110f * dt * stride;
                Vector3 east = Vector3.Cross(up, Vector3.up);
                if (east.sqrMagnitude < 1e-6f)
                    east = Vector3.Cross(up, Vector3.right);
                east.Normalize();
                Vector3 north = Vector3.Cross(east, up);
                float rad = a.wanderYaw * Mathf.Deg2Rad;
                step = (north * Mathf.Cos(rad) + east * Mathf.Sin(rad)).normalized;
                if (facing > 0.1f && toPlayerSq > 1f)
                    step = (step + toPlayer.normalized * 0.22f).normalized;
                speed = SimSpeed * (0.28f + 0.35f * Mathf.Clamp01(facing + 0.35f));
            }

            int cell = a.cell;
            if (cell >= 0 && cell < CellCount && _cells[cell].packed)
            {
                Vector3 side = Vector3.Cross(up, step);
                if (side.sqrMagnitude > 1e-6f)
                {
                    side.Normalize();
                    float lane = ((i * 0.6180339887f) % 1f) * 2f - 1f;
                    step = (step + side * (lane * 0.55f)).normalized;
                }
            }

            if (!TryStepOnDryLand(pos, center, up, ref step, speed * dt * stride, waterLine, out Vector3 nextDir, out float nextR))
            {
                // Shore hug: turn and stay put rather than walk into the ocean.
                a.wanderYaw += 80f + (i % 11) * 17f;
                _agents[i] = a;
                continue;
            }

            a.dir = nextDir;
            a.radius = nextR;
            Vector3 look = Vector3.ProjectOnPlane(step, a.dir);
            if (look.sqrMagnitude > 1e-6f)
                a.yaw = Quaternion.LookRotation(look, a.dir).eulerAngles.y;
            a.phase += dt * stride * (chase ? 7f : 5f);
            a.cell = (short)HashCell(a.dir);
            _agents[i] = a;
        }
    }

    void SyncRealized(Vector3 playerPos, Camera cam)
    {
        if (_bodies == null)
            return;
        Vector3 center = _planetCenter;
        Vector3 playerDir = playerPos - center;
        if (playerDir.sqrMagnitude > 1e-6f)
            playerDir.Normalize();

        float realizeSq = RealizeRange * RealizeRange;
        float keepSq = RealizeKeepRange * RealizeKeepRange;
        float aimRangeSq = AimRealizeRange * AimRealizeRange;
        int want = Mathf.Min(MaxRealized, _alive);
        for (int n = 0; n < want; n++)
        {
            s_NearestScratch[n] = -1;
            s_NearestDist[n] = float.PositiveInfinity;
        }

        Ray aimRay = default;
        bool hasAim = cam != null;
        if (hasAim)
            aimRay = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));

        for (int i = 0; i < _alive; i++)
        {
            if (Vector3.Dot(_agents[i].dir, playerDir) < HorizonDot)
                continue;

            Vector3 pos = center + _agents[i].dir * _agents[i].radius;
            Vector3 aimPos = pos + _agents[i].dir * 0.9f;
            float distSq = (pos - playerPos).sqrMagnitude;
            float score = float.PositiveInfinity;
            bool already = _agents[i].body >= 0;

            // Hysteresis: once realized, keep the slot until farther than RealizeKeepRange.
            if (already && distSq <= keepSq)
                score = 40f + Mathf.Sqrt(distSq);
            else if (distSq <= realizeSq)
                score = 100f + Mathf.Sqrt(distSq);

            if (hasAim && distSq <= aimRangeSq)
            {
                Vector3 to = aimPos - aimRay.origin;
                float along = Vector3.Dot(to, aimRay.direction);
                if (along > 1f)
                {
                    Vector3 closest = aimRay.origin + aimRay.direction * along;
                    float offAxis = (aimPos - closest).magnitude;
                    float screenProxy = offAxis / Mathf.Max(1f, along);
                    if (screenProxy <= AimScreenRadius)
                    {
                        float aimScore = screenProxy * 40f + Mathf.Sqrt(distSq) * 0.02f;
                        if (already)
                            aimScore *= 0.7f;
                        if (aimScore < score)
                            score = aimScore;
                    }
                }
            }

            if (float.IsPositiveInfinity(score))
                continue;

            int worst = 0;
            for (int n = 1; n < want; n++)
            {
                if (s_NearestDist[n] > s_NearestDist[worst])
                    worst = n;
            }
            if (score < s_NearestDist[worst])
            {
                s_NearestDist[worst] = score;
                s_NearestScratch[worst] = i;
            }
        }

        bool[] keep = new bool[MaxRealized];
        for (int n = 0; n < want; n++)
        {
            int agent = s_NearestScratch[n];
            if (agent < 0)
                continue;
            int body = _agents[agent].body;
            if (body >= 0 && body < _bodies.Length && _bodyBusy[body] == 1 && _bodyAgent[body] == agent)
                keep[body] = true;
        }

        for (int b = 0; b < _bodies.Length; b++)
        {
            if (_bodyBusy[b] != 1 || keep[b])
                continue;
            int agent = _bodyAgent[b];
            if (agent >= 0 && agent < _alive)
            {
                Agent a = _agents[agent];
                Vector3 bodyPos = _bodies[b].transform.position;
                Vector3 radial = bodyPos - center;
                if (radial.sqrMagnitude > 1e-4f)
                {
                    a.dir = radial.normalized;
                    a.radius = SampleSurfaceRadius(a.dir, radial.magnitude);
                }
                a.body = -1;
                _agents[agent] = a;
            }
            _bodies[b].SetActive(false);
            _bodyBusy[b] = 0;
            _bodyAgent[b] = -1;
        }

        for (int n = 0; n < want; n++)
        {
            int agent = s_NearestScratch[n];
            if (agent < 0 || _agents[agent].body >= 0)
                continue;
            int slot = FreeBodySlot();
            if (slot < 0)
                break;
            BindBody(slot, agent, center);
        }
    }

    void DrawInstances(Vector3 playerPos, Camera cam)
    {
        if (_instanceMesh == null || _instanceMat == null || _alive <= 0)
            return;

        Vector3 center = _planetCenter;
        Vector3 camPos = cam != null ? cam.transform.position : playerPos;
        float camRadius = Mathf.Max(1f, (camPos - center).magnitude);
        float surfaceR = camRadius;
        if (_planetComp != null)
            surfaceR = Mathf.Max(1f, _planetComp.GetSurfaceRadiusWorld((camPos - center).normalized));
        else if (_alive > 0)
            surfaceR = Mathf.Max(1f, _agents[0].radius);

        // Slightly smaller occlusion sphere + pad so limb doesn't flicker as you walk.
        float under = Mathf.Max(0f, camRadius * camRadius - surfaceR * surfaceR * 0.88f);
        float horizonDist = Mathf.Sqrt(under) + surfaceR * 0.12f;
        horizonDist = Mathf.Max(horizonDist, surfaceR * 0.85f);
        float horizonSq = horizonDist * horizonDist;
        float waterLine = GetWaterLine();

        int want = MaxInstanceDraw;
        for (int n = 0; n < want; n++)
        {
            s_DrawScratch[n] = -1;
            s_DrawDist[n] = float.PositiveInfinity;
        }

        int perBand = Mathf.Max(1, want / DrawBands);
        Vector3 cullSize = new Vector3(4.5f, 5.5f, 4.5f);
        for (int i = 0; i < _alive; i++)
        {
            if (_agents[i].body >= 0)
                continue;

            // Never draw agents that somehow sat below the waterline.
            if (waterLine > 1e-3f && _agents[i].radius < waterLine)
                continue;

            Vector3 pos = center + _agents[i].dir * _agents[i].radius;
            bool sticky = _drawnLast[i];
            if (!IsOverHorizon(camPos, center, surfaceR, pos, sticky))
                continue;

            float d = (pos - camPos).sqrMagnitude;
            if (d > horizonSq)
                continue;
            if (cam != null && !GeometryUtility.TestPlanesAABB(s_Frustum, new Bounds(pos, cullSize)))
                continue;

            // Sticky bias keeps last-frame silhouettes from losing their band slot when you move.
            float rank = sticky ? d * DrawStickyBias : d;
            float dist = Mathf.Sqrt(d);
            int band = Mathf.Clamp((int)(dist / Mathf.Max(1f, horizonDist) * DrawBands), 0, DrawBands - 1);
            // Soft band edges: sticky agents near a boundary can also compete one band closer.
            int bandLo = sticky ? Mathf.Max(0, band - 1) : band;
            int bestWorst = -1;
            float bestWorstDist = -1f;
            for (int bandTry = bandLo; bandTry <= band; bandTry++)
            {
                int slot0 = bandTry * perBand;
                int slot1 = Mathf.Min(want, slot0 + perBand);
                int worst = slot0;
                for (int n = slot0 + 1; n < slot1; n++)
                {
                    if (s_DrawDist[n] > s_DrawDist[worst])
                        worst = n;
                }
                if (s_DrawDist[worst] > bestWorstDist)
                {
                    bestWorstDist = s_DrawDist[worst];
                    bestWorst = worst;
                }
            }

            if (bestWorst >= 0 && rank < s_DrawDist[bestWorst])
            {
                s_DrawDist[bestWorst] = rank;
                s_DrawScratch[bestWorst] = i;
            }
        }

        for (int i = 0; i < _alive; i++)
            _drawnLast[i] = false;

        int filled = 0;
        for (int n = 0; n < want; n++)
        {
            int i = s_DrawScratch[n];
            if (i < 0)
                continue;
            _drawnLast[i] = true;
            Agent a = _agents[i];
            Vector3 up = a.dir;
            float bob = Mathf.Abs(Mathf.Sin(a.phase)) * 0.04f;
            Vector3 pos = center + up * (a.radius + bob);
            Quaternion rot = Quaternion.AngleAxis(a.yaw, up) * Quaternion.FromToRotation(Vector3.up, up);
            float dist = (pos - camPos).magnitude;
            float lod = Mathf.Lerp(1f, 0.72f, Mathf.InverseLerp(30f, horizonDist, dist));
            float s = SilhouetteHeight * lod;
            Vector3 scale = new Vector3(s, s, s);
            _batch[filled++] = Matrix4x4.TRS(pos, rot, scale);
            if (filled == InstanceBatch)
            {
                Graphics.DrawMeshInstanced(_instanceMesh, 0, _instanceMat, _batch, filled, null,
                    ShadowCastingMode.Off, false);
                filled = 0;
            }
        }
        if (filled > 0)
        {
            Graphics.DrawMeshInstanced(_instanceMesh, 0, _instanceMat, _batch, filled, null,
                ShadowCastingMode.Off, false);
        }
    }

    static bool IsOverHorizon(Vector3 camPos, Vector3 center, float surfaceR, Vector3 agentPos, bool sticky)
    {
        Vector3 camRel = camPos - center;
        float camR2 = camRel.sqrMagnitude;
        if (camR2 < 1e-4f)
            return true;
        // Shrink occlusion sphere so the geometric limb doesn't flicker with small camera moves.
        float occR = surfaceR * (sticky ? 0.90f : 0.94f);
        Vector3 toAgent = agentPos - camPos;
        float dist = toAgent.magnitude;
        if (dist < 0.01f)
            return true;
        Vector3 dir = toAgent / dist;
        float b = Vector3.Dot(camRel, dir);
        float c = camR2 - occR * occR;
        float disc = b * b - c;
        if (disc <= 0f)
            return true;
        float tHit = -b - Mathf.Sqrt(disc);
        float margin = sticky ? 2.5f : 1.0f;
        if (tHit > 0.35f && tHit < dist - margin)
            return false;
        return true;
    }
    void BindBody(int slot, int agent, Vector3 center)
    {
        Agent a = _agents[agent];
        a.radius = SampleSurfaceRadius(a.dir, a.radius);
        if (!IsAboveWater(a.dir, ref a.radius))
        {
            Vector3 d = a.dir;
            float r = a.radius;
            if (TryRescueToDry(ref d, ref r, a.dir))
            {
                a.dir = d;
                a.radius = r;
            }
        }
        Vector3 pos = center + a.dir * a.radius;
        Vector3 up = a.dir;
        // Seat feet on mesh (ray) so they don't pop in mid-air then fall.
        if (TryRaySurface(pos, up, out Vector3 ground, out Vector3 normal))
        {
            pos = ground;
            up = normal;
            a.dir = (pos - center).normalized;
            a.radius = (pos - center).magnitude;
        }
        Quaternion rot = Quaternion.AngleAxis(a.yaw, up) * Quaternion.FromToRotation(Vector3.up, up);
        GameObject go = _bodies[slot];
        go.transform.SetPositionAndRotation(pos, rot);
        if (_bodyAi[slot] != null)
            _bodyAi[slot].ResetForHordeReuse();
        go.SetActive(true);
        a.body = (sbyte)slot;
        _agents[agent] = a;
        _bodyBusy[slot] = 1;
        _bodyAgent[slot] = agent;
    }

    float SampleSurfaceRadius(Vector3 dir, float fallback)
    {
        if (dir.sqrMagnitude < 1e-6f)
            return fallback;
        dir.Normalize();
        if (_planetComp != null)
        {
            float r = _planetComp.GetSurfaceRadiusWorld(dir);
            if (r > 1f)
                return r;
        }
        if (TryRaySurface(_planetCenter + dir * Mathf.Max(fallback, 10f), dir, out Vector3 ground, out _))
            return (ground - _planetCenter).magnitude;
        return fallback;
    }

    float DryClearance => _spawner != null ? Mathf.Max(0f, _spawner.spawnDryClearance) : 0.75f;

    float GetWaterLine()
    {
        if (_ocean != null)
            return _ocean.ResolveOceanRadiusWorld() + DryClearance;
        if (_planetComp != null)
            return _planetComp.GetBaseRadiusWorld() + DryClearance;
        return 0f;
    }

    bool IsAboveWater(Vector3 dir, ref float radius)
    {
        float waterLine = GetWaterLine();
        if (waterLine <= 1e-3f)
            return true;
        radius = SampleSurfaceRadius(dir, radius);
        return radius >= waterLine;
    }

    bool TryStepOnDryLand(
        Vector3 pos,
        Vector3 center,
        Vector3 up,
        ref Vector3 step,
        float moveDist,
        float waterLine,
        out Vector3 nextDir,
        out float nextRadius)
    {
        nextDir = up;
        nextRadius = (pos - center).magnitude;
        if (step.sqrMagnitude < 1e-8f || moveDist <= 0f)
            return true;

        Vector3 side = Vector3.Cross(up, step);
        if (side.sqrMagnitude > 1e-6f)
            side.Normalize();
        else
            side = Vector3.zero;

        // Prefer forward; if that is ocean, slide along the shore instead of wading in.
        Vector3[] candidates =
        {
            step,
            (step + side * 0.85f).normalized,
            (step - side * 0.85f).normalized,
            side,
            -side
        };

        for (int c = 0; c < candidates.Length; c++)
        {
            Vector3 cand = candidates[c];
            if (cand.sqrMagnitude < 1e-6f)
                continue;
            Vector3 next = pos + cand * moveDist;
            Vector3 dir = (next - center).normalized;
            float r = SampleSurfaceRadius(dir, nextRadius);
            if (waterLine > 1e-3f && r < waterLine)
                continue;
            step = cand;
            nextDir = dir;
            nextRadius = r;
            return true;
        }

        return false;
    }

    bool TryRescueToDry(ref Vector3 dir, ref float radius, Vector3 preferred)
    {
        float waterLine = GetWaterLine();
        if (waterLine <= 1e-3f)
            return true;

        if (preferred.sqrMagnitude > 1e-6f)
            preferred.Normalize();
        else
            preferred = dir.sqrMagnitude > 1e-6f ? dir.normalized : Random.onUnitSphere;

        for (int k = 0; k < 14; k++)
        {
            Vector3 probe = k == 0
                ? preferred
                : (preferred + Random.onUnitSphere * (0.15f + k * 0.08f)).normalized;
            float r = SampleSurfaceRadius(probe, radius);
            if (r >= waterLine)
            {
                dir = probe;
                radius = r;
                return true;
            }
        }

        for (int k = 0; k < 20; k++)
        {
            Vector3 probe = Random.onUnitSphere;
            float r = SampleSurfaceRadius(probe, radius);
            if (r >= waterLine)
            {
                dir = probe;
                radius = r;
                return true;
            }
        }

        return false;
    }

    bool TryRaySurface(Vector3 nearPos, Vector3 up, out Vector3 point, out Vector3 normal)
    {
        point = nearPos;
        normal = up.sqrMagnitude > 1e-6f ? up.normalized : Vector3.up;
        Vector3 origin = nearPos + normal * 12f;
        if (!Physics.Raycast(origin, -normal, out RaycastHit hit, 40f, _groundMask, QueryTriggerInteraction.Ignore))
            return false;
        // Skip water / non-terrain if possible
        string n = hit.collider != null ? hit.collider.gameObject.name : "";
        if (n.IndexOf("Water", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
            n.IndexOf("Ocean", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
            n.IndexOf("Atmosphere", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return false;
        point = hit.point;
        normal = hit.normal;
        if (Vector3.Dot(normal, up) < 0f)
            normal = -normal;
        return true;
    }

    int FreeBodySlot()
    {
        for (int i = 0; i < _bodies.Length; i++)
        {
            if (_bodyBusy[i] == 0 && _bodies[i] != null)
                return i;
        }
        return -1;
    }

    void RemoveAgent(int index)
    {
        if (index < 0 || index >= _alive)
            return;
        int last = _alive - 1;
        if (index != last)
        {
            _agents[index] = _agents[last];
            int body = _agents[index].body;
            if (body >= 0 && body < _bodyAgent.Length)
                _bodyAgent[body] = index;
        }
        _alive = last;
    }

    int FindBody(ZombieAI ai)
    {
        if (ai == null || _bodyAi == null)
            return -1;
        for (int i = 0; i < _bodyAi.Length; i++)
        {
            if (_bodyAi[i] == ai)
                return i;
        }
        return -1;
    }

    void EnsurePool()
    {
        if (_bodies != null || _spawner == null)
            return;
        GameObject prefab = _spawner.PickPrefab();
        if (prefab == null)
            return;

        _bodies = new GameObject[MaxRealized];
        _bodyAi = new ZombieAI[MaxRealized];
        _bodyBusy = new byte[MaxRealized];
        _bodyAgent = new int[MaxRealized];
        int layer = LayerMask.NameToLayer(_spawner.zombieLayerName);

        for (int i = 0; i < MaxRealized; i++)
        {
            GameObject go = Instantiate(prefab, transform);
            go.name = prefab.name + "_HordeBody_" + i;
            go.SetActive(false);
            if (layer >= 0)
                SetLayer(go, layer);
            if (go.GetComponent<ZombieVisibilityCuller>() == null)
                go.AddComponent<ZombieVisibilityCuller>();
            _bodies[i] = go;
            _bodyAi[i] = go.GetComponent<ZombieAI>();
            _bodyAgent[i] = -1;
        }

        BakeInstanceMesh(_bodies[0]);
    }

    void BakeInstanceMesh(GameObject template)
    {
        if (template == null)
        {
            EnsureFallbackVisual();
            return;
        }

        bool wasActive = template.activeSelf;
        template.SetActive(true);

        var smrs = template.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        if (smrs == null || smrs.Length == 0)
        {
            template.SetActive(wasActive);
            EnsureFallbackVisual();
            return;
        }

        var combine = new List<CombineInstance>(smrs.Length);
        Material sourceMat = null;
        Matrix4x4 inv = template.transform.worldToLocalMatrix;
        for (int i = 0; i < smrs.Length; i++)
        {
            SkinnedMeshRenderer smr = smrs[i];
            if (smr == null || smr.sharedMesh == null || !smr.enabled)
                continue;
            if (sourceMat == null && smr.sharedMaterial != null)
                sourceMat = smr.sharedMaterial;

            var baked = new Mesh();
            smr.BakeMesh(baked, true);
            Matrix4x4 xform = inv * smr.transform.localToWorldMatrix;
            // Negative scale (common on mirrored Kenny pieces) flips winding → see-through edges.
            if (xform.determinant < 0f)
                ReverseTriangles(baked);
            combine.Add(new CombineInstance
            {
                mesh = baked,
                transform = xform
            });
        }

        template.SetActive(wasActive);
        if (combine.Count == 0)
        {
            EnsureFallbackVisual();
            return;
        }

        var combined = new Mesh { name = "HordeZombieBake" };
        combined.CombineMeshes(combine.ToArray(), true, true);
        for (int i = 0; i < combine.Count; i++)
            Destroy(combine[i].mesh);

        // Unit height, pivot at feet — instances sit on terrain at SilhouetteHeight.
        Bounds b = combined.bounds;
        float height = Mathf.Max(0.01f, b.size.y);
        var verts = combined.vertices;
        float invH = 1f / height;
        Vector3 foot = new Vector3(b.center.x, b.min.y, b.center.z);
        for (int i = 0; i < verts.Length; i++)
            verts[i] = (verts[i] - foot) * invH;
        combined.vertices = verts;
        combined.RecalculateBounds();
        FixWindingIfInsideOut(combined);
        combined.RecalculateNormals();
        combined.RecalculateTangents();

        if (_instanceMesh != null)
            Destroy(_instanceMesh);
        _instanceMesh = combined;

        BuildInstanceMaterial(sourceMat);
    }

    static void ReverseTriangles(Mesh mesh)
    {
        if (mesh == null)
            return;
        int[] tris = mesh.triangles;
        for (int i = 0; i < tris.Length; i += 3)
        {
            int tmp = tris[i];
            tris[i] = tris[i + 2];
            tris[i + 2] = tmp;
        }
        mesh.triangles = tris;
    }

    static void FixWindingIfInsideOut(Mesh mesh)
    {
        Vector3[] verts = mesh.vertices;
        int[] tris = mesh.triangles;
        if (verts == null || tris == null || verts.Length == 0 || tris.Length < 3)
            return;

        Vector3 center = mesh.bounds.center;
        float vote = 0f;
        int faceCount = tris.Length / 3;
        int step = Mathf.Max(1, faceCount / 96);
        for (int f = 0; f < faceCount; f += step)
        {
            int i = f * 3;
            Vector3 a = verts[tris[i]];
            Vector3 b = verts[tris[i + 1]];
            Vector3 c = verts[tris[i + 2]];
            Vector3 n = Vector3.Cross(b - a, c - a);
            Vector3 face = (a + b + c) * (1f / 3f);
            vote += Vector3.Dot(n, face - center);
        }

        if (vote >= 0f)
            return;

        ReverseTriangles(mesh);
    }

    void BuildInstanceMaterial(Material sourceMat)
    {
        if (_instanceMat != null)
            Destroy(_instanceMat);

        Shader lit = Shader.Find("Universal Render Pipeline/Lit");
        if (lit == null)
        {
            EnsureFallbackVisual();
            return;
        }

        // Fresh opaque URP Lit — cloning Kenny mats can keep bad surface/cull state under instancing.
        _instanceMat = new Material(lit);
        _instanceMat.name = "HordeZombieInstance";
        _instanceMat.enableInstancing = true;
        _instanceMat.SetFloat("_Surface", 0f);
        _instanceMat.SetFloat("_Blend", 0f);
        _instanceMat.SetFloat("_AlphaClip", 0f);
        _instanceMat.SetFloat("_Cull", 2f); // Back
        _instanceMat.SetFloat("_ZWrite", 1f);
        _instanceMat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
        _instanceMat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
        _instanceMat.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
        _instanceMat.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.Zero);
        _instanceMat.SetOverrideTag("RenderType", "Opaque");
        _instanceMat.renderQueue = (int)RenderQueue.Geometry;
        _instanceMat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
        _instanceMat.DisableKeyword("_ALPHATEST_ON");

        if (sourceMat != null)
        {
            if (sourceMat.HasProperty("_BaseMap") && _instanceMat.HasProperty("_BaseMap"))
                _instanceMat.SetTexture("_BaseMap", sourceMat.GetTexture("_BaseMap"));
            else if (sourceMat.mainTexture != null)
                _instanceMat.mainTexture = sourceMat.mainTexture;

            if (sourceMat.HasProperty("_BaseColor") && _instanceMat.HasProperty("_BaseColor"))
                _instanceMat.SetColor("_BaseColor", sourceMat.GetColor("_BaseColor"));
            else
                _instanceMat.color = sourceMat.color;

            if (sourceMat.HasProperty("_Smoothness") && _instanceMat.HasProperty("_Smoothness"))
                _instanceMat.SetFloat("_Smoothness", sourceMat.GetFloat("_Smoothness"));
            if (sourceMat.HasProperty("_Metallic") && _instanceMat.HasProperty("_Metallic"))
                _instanceMat.SetFloat("_Metallic", sourceMat.GetFloat("_Metallic"));
        }
        else
        {
            Color c = new Color(0.25f, 0.32f, 0.18f, 1f);
            if (_instanceMat.HasProperty("_BaseColor"))
                _instanceMat.SetColor("_BaseColor", c);
            _instanceMat.color = c;
        }
    }

    void EnsureFallbackVisual()
    {
        if (_instanceMesh == null)
        {
            _instanceMesh = new Mesh { name = "HordeFallback" };
            _instanceMesh.vertices = new[]
            {
                new Vector3(-0.2f, 0f, -0.12f), new Vector3(0.2f, 0f, -0.12f),
                new Vector3(0.2f, 0f, 0.12f), new Vector3(-0.2f, 0f, 0.12f),
                new Vector3(-0.28f, 0.55f, -0.14f), new Vector3(0.28f, 0.55f, -0.14f),
                new Vector3(0.28f, 0.55f, 0.14f), new Vector3(-0.28f, 0.55f, 0.14f),
                new Vector3(-0.16f, 1f, -0.12f), new Vector3(0.16f, 1f, -0.12f),
                new Vector3(0.16f, 1f, 0.12f), new Vector3(-0.16f, 1f, 0.12f)
            };
            _instanceMesh.triangles = new[]
            {
                0, 4, 5, 0, 5, 1, 1, 5, 6, 1, 6, 2, 2, 6, 7, 2, 7, 3, 3, 7, 4, 3, 4, 0,
                4, 8, 9, 4, 9, 5, 5, 9, 10, 5, 10, 6, 6, 10, 11, 6, 11, 7, 7, 11, 8, 7, 8, 4,
                8, 11, 10, 8, 10, 9
            };
            _instanceMesh.RecalculateNormals();
            _instanceMesh.RecalculateBounds();
        }

        if (_instanceMat == null)
        {
            Shader sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Sprites/Default");
            _instanceMat = new Material(sh != null ? sh : Shader.Find("Hidden/InternalErrorShader"));
            _instanceMat.enableInstancing = true;
            Color c = new Color(0.25f, 0.32f, 0.18f, 1f);
            if (_instanceMat.HasProperty("_BaseColor"))
                _instanceMat.SetColor("_BaseColor", c);
            _instanceMat.color = c;
        }
        else
            _instanceMat.enableInstancing = true;
    }

    void CachePlanet()
    {
        if (_spawner == null)
            return;
        if (_planet == null)
        {
            var tagged = GameObject.FindGameObjectWithTag("Planet");
            _planet = tagged != null ? tagged.transform : null;
            if (_planet == null)
            {
                Planet p = FindFirstObjectByType<Planet>();
                if (p != null)
                    _planet = p.transform;
            }
        }
        if (_planet != null)
        {
            _planetCenter = _planet.position;
            if (_planetComp == null)
                _planetComp = _planet.GetComponent<Planet>();
            if (_ocean == null)
            {
                _ocean = _planet.GetComponent<PlanetOceanLayer>();
                if (_ocean == null)
                    _ocean = FindFirstObjectByType<PlanetOceanLayer>();
            }
        }
    }

    static void SetLayer(GameObject go, int layer)
    {
        go.layer = layer;
        Transform t = go.transform;
        for (int i = 0; i < t.childCount; i++)
            SetLayer(t.GetChild(i).gameObject, layer);
    }
}
