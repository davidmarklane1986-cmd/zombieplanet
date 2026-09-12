using System.Collections.Generic;
using UnityEngine;

namespace Stargrave.Rts2
{
    /// <summary>Baked dry-land navigation graph on the planet surface.</summary>
    public sealed class PlanetNavGraph
    {
        public struct Node
        {
            public Vector3 axis;
            public float radius;
            public int edgeStart;
            public int edgeCount;
        }

        public struct Edge
        {
            public int to;
            public float cost;
        }

        public Node[] Nodes = System.Array.Empty<Node>();
        public Edge[] Edges = System.Array.Empty<Edge>();
        public Vector3 PlanetCenter;
        public bool IsReady;

        public Vector3 WorldPosition(int nodeIndex)
        {
            Node n = Nodes[nodeIndex];
            return PlanetCenter + n.axis.normalized * Mathf.Max(1f, n.radius);
        }

        public int FindNearestNode(Vector3 worldPos, int hint = -1)
        {
            if (Nodes == null || Nodes.Length == 0)
                return -1;

            Vector3 axis = (worldPos - PlanetCenter).normalized;
            int best = hint >= 0 && hint < Nodes.Length ? hint : 0;
            float bestDot = Vector3.Dot(Nodes[best].axis, axis);

            // Sample a stride then refine locally — good enough for RTS scale.
            int stride = Mathf.Max(1, Nodes.Length / 64);
            for (int i = 0; i < Nodes.Length; i += stride)
            {
                float d = Vector3.Dot(Nodes[i].axis, axis);
                if (d > bestDot)
                {
                    bestDot = d;
                    best = i;
                }
            }

            int start = Mathf.Max(0, best - stride * 2);
            int end = Mathf.Min(Nodes.Length, best + stride * 2 + 1);
            for (int i = start; i < end; i++)
            {
                float d = Vector3.Dot(Nodes[i].axis, axis);
                if (d > bestDot)
                {
                    bestDot = d;
                    best = i;
                }
            }

            return best;
        }
    }

    /// <summary>Builds a coarse Fibonacci-like sphere sample of dry nodes, then densifies near anchors.</summary>
    public static class PlanetNavBaker
    {
        public static PlanetNavGraph Bake(
            Planet planet,
            PlanetOceanLayer ocean,
            IReadOnlyList<Vector3> denseAnchors,
            int coarseCount = 720,
            int densePerAnchor = 48,
            float denseRadius = 90f)
        {
            var graph = new PlanetNavGraph
            {
                PlanetCenter = planet != null ? planet.transform.position : Vector3.zero,
                IsReady = false
            };
            if (planet == null)
                return graph;

            float water = ocean != null ? ocean.ResolveOceanRadiusWorld() + 1.25f : 0f;
            var axes = new List<Vector3>(coarseCount + (denseAnchors?.Count ?? 0) * densePerAnchor);
            var radii = new List<float>(axes.Capacity);

            // Coarse golden-spiral samples.
            float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
            for (int i = 0; i < coarseCount; i++)
            {
                float y = 1f - (i / (float)(coarseCount - 1)) * 2f;
                float radiusAtY = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                float theta = golden * i;
                Vector3 axis = new Vector3(Mathf.Cos(theta) * radiusAtY, y, Mathf.Sin(theta) * radiusAtY).normalized;
                float r = planet.GetSurfaceRadiusWorld(axis);
                if (r < water)
                    continue;
                axes.Add(axis);
                radii.Add(r);
            }

            // Dense patches around bases / towns.
            if (denseAnchors != null)
            {
                for (int a = 0; a < denseAnchors.Count; a++)
                {
                    Vector3 anchorAxis = (denseAnchors[a] - graph.PlanetCenter).normalized;
                    if (anchorAxis.sqrMagnitude < 1e-8f)
                        continue;
                    float anchorR = planet.GetSurfaceRadiusWorld(anchorAxis);
                    float angular = denseRadius / Mathf.Max(1f, anchorR);
                    for (int i = 0; i < densePerAnchor; i++)
                    {
                        Vector3 jitter = Random.onUnitSphere;
                        Vector3 axis = (anchorAxis + jitter * angular * Random.Range(0.05f, 1f)).normalized;
                        float r = planet.GetSurfaceRadiusWorld(axis);
                        if (r < water)
                            continue;
                        axes.Add(axis);
                        radii.Add(r);
                    }
                }
            }

            int n = axes.Count;
            if (n < 8)
                return graph;

            // Link spacing must scale with planet size — fixed 55 breaks large worlds.
            float radiusSum = 0f;
            for (int i = 0; i < n; i++)
                radiusSum += radii[i];
            float avgRadius = radiusSum / n;
            float meanChord = avgRadius * Mathf.Sqrt(4f * Mathf.PI / n);
            float maxLink = Mathf.Clamp(meanChord * 2.75f, 40f, avgRadius * 0.35f);
            const int kNeighbors = 8;
            var edgeLists = new List<PlanetNavGraph.Edge>[n];
            for (int i = 0; i < n; i++)
                edgeLists[i] = new List<PlanetNavGraph.Edge>(kNeighbors);

            var scratch = new List<(float dist, int j)>(32);
            for (int i = 0; i < n; i++)
            {
                scratch.Clear();
                Vector3 pi = graph.PlanetCenter + axes[i] * radii[i];
                for (int j = 0; j < n; j++)
                {
                    if (i == j)
                        continue;
                    Vector3 pj = graph.PlanetCenter + axes[j] * radii[j];
                    float d = Vector3.Distance(pi, pj);
                    if (d > maxLink)
                        continue;
                    // Reject links that dip underwater (midpoint sample).
                    Vector3 midAxis = Vector3.Slerp(axes[i], axes[j], 0.5f).normalized;
                    if (planet.GetSurfaceRadiusWorld(midAxis) < water)
                        continue;
                    scratch.Add((d, j));
                }

                scratch.Sort((a, b) => a.dist.CompareTo(b.dist));
                int take = Mathf.Min(kNeighbors, scratch.Count);
                for (int t = 0; t < take; t++)
                {
                    int j = scratch[t].j;
                    float cost = scratch[t].dist;
                    edgeLists[i].Add(new PlanetNavGraph.Edge { to = j, cost = cost });
                    // Ensure undirected connectivity.
                    bool has = false;
                    for (int e = 0; e < edgeLists[j].Count; e++)
                    {
                        if (edgeLists[j][e].to == i)
                        {
                            has = true;
                            break;
                        }
                    }
                    if (!has && edgeLists[j].Count < kNeighbors + 4)
                        edgeLists[j].Add(new PlanetNavGraph.Edge { to = i, cost = cost });
                }
            }

            int edgeTotal = 0;
            for (int i = 0; i < n; i++)
                edgeTotal += edgeLists[i].Count;

            var nodes = new PlanetNavGraph.Node[n];
            var edges = new PlanetNavGraph.Edge[edgeTotal];
            int cursor = 0;
            for (int i = 0; i < n; i++)
            {
                nodes[i] = new PlanetNavGraph.Node
                {
                    axis = axes[i],
                    radius = radii[i],
                    edgeStart = cursor,
                    edgeCount = edgeLists[i].Count
                };
                for (int e = 0; e < edgeLists[i].Count; e++)
                    edges[cursor++] = edgeLists[i][e];
            }

            graph.Nodes = nodes;
            graph.Edges = edges;
            graph.IsReady = true;
            return graph;
        }
    }
}
