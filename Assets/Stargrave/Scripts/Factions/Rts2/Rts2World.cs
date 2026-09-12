using System.Collections.Generic;
using UnityEngine;

namespace Stargrave.Rts2
{
    /// <summary>Owns nav, resources, path service, and unit sim for the modular RTS restart.</summary>
    [DisallowMultipleComponent]
    public sealed class Rts2World : MonoBehaviour
    {
        public static Rts2World Instance { get; private set; }

        public PlanetNavGraph Nav { get; private set; }
        public PathService Paths { get; private set; }
        public LogicalResourceMap Resources { get; private set; }
        public Rts2UnitSim Units { get; private set; }
        public TerritorySystem Territory { get; private set; }
        public bool IsReady { get; private set; }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        public void BakeAndStart(FactionSimulation sim, IReadOnlyList<Vector3> denseAnchors)
        {
            if (sim == null || sim.planet == null)
                return;

            PlanetOceanLayer ocean = sim.planet.GetComponent<PlanetOceanLayer>();
            Resources = LogicalResourceMap.Bake(sim.planet, ocean);
            Nav = PlanetNavBaker.Bake(sim.planet, ocean, denseAnchors);
            Paths = new PathService(Nav);

            Units = GetComponent<Rts2UnitSim>();
            if (Units == null)
                Units = gameObject.AddComponent<Rts2UnitSim>();
            Units.Configure(sim, Nav, Paths, Resources);

            Territory = GetComponent<TerritorySystem>();
            if (Territory == null)
                Territory = gameObject.AddComponent<TerritorySystem>();
            if (sim.useBarEconomyMode)
                Territory.BakePockets(sim, Nav);
            else
                Territory.Configure(sim, Nav);

            IsReady = Nav != null && Nav.IsReady && Resources != null && Resources.IsReady;
            if (sim.verboseEvents)
            {
                Debug.Log(
                    $"[Rts2] Ready — nav nodes {(Nav != null ? Nav.Nodes.Length : 0)}, " +
                    $"resource sites {(Resources != null ? Resources.Sites.Count : 0)}, " +
                    $"pockets {(Territory != null ? Territory.Pockets.Count : 0)}.",
                    this);
            }
        }

        public void RebuildNavDense(FactionSimulation sim, IReadOnlyList<Vector3> anchors)
        {
            if (sim == null || sim.planet == null)
                return;
            PlanetOceanLayer ocean = sim.planet.GetComponent<PlanetOceanLayer>();
            if (Resources != null)
                Resources.EnsureSitesNear(sim.planet, ocean, anchors);
            Nav = PlanetNavBaker.Bake(sim.planet, ocean, anchors);
            Paths = new PathService(Nav);
            if (Units != null)
            {
                Units.Configure(sim, Nav, Paths, Resources);
                Units.InvalidateAllPaths();
            }
            if (Territory != null)
                Territory.Configure(sim, Nav);
        }
    }
}
