using System.Collections.Generic;
using UnityEngine;

namespace Stargrave.Rts2
{
    /// <summary>Queued A* pathfinding on <see cref="PlanetNavGraph"/> with a hard per-frame budget.</summary>
    public sealed class PathService
    {
        public struct Path
        {
            public int id;
            public bool ready;
            public bool failed;
            public List<Vector3> waypoints;
        }

        // Keep this tiny — each Solve can walk thousands of nav nodes.
        const int MaxPathsPerFrame = 2;
        const float MaxSolveMilliseconds = 1.25f;
        const int MaxOpen = 4096;

        readonly PlanetNavGraph _graph;
        readonly Queue<Request> _queue = new Queue<Request>(64);
        readonly Dictionary<int, Path> _paths = new Dictionary<int, Path>(128);
        float[] _gScore = System.Array.Empty<float>();
        float[] _fScore = System.Array.Empty<float>();
        int[] _cameFrom = System.Array.Empty<int>();
        int[] _openHeap = System.Array.Empty<int>();
        int _openCount;
        byte[] _openMarker = System.Array.Empty<byte>();
        byte[] _closedMarker = System.Array.Empty<byte>();
        byte _stamp = 1;
        int _nextId = 1;
        readonly List<int> _stackScratch = new List<int>(64);

        struct Request
        {
            public int pathId;
            public Vector3 from;
            public Vector3 to;
        }

        public PathService(PlanetNavGraph graph)
        {
            _graph = graph;
        }

        public bool IsReady => _graph != null && _graph.IsReady;

        public int QueueCount => _queue.Count;

        public int RequestPath(Vector3 from, Vector3 to)
        {
            if (!IsReady)
                return -1;
            int id = _nextId++;
            _paths[id] = new Path
            {
                id = id,
                ready = false,
                failed = false,
                waypoints = new List<Vector3>(16)
            };
            _queue.Enqueue(new Request { pathId = id, from = from, to = to });
            return id;
        }

        public bool TryGetPath(int pathId, out Path path)
        {
            return _paths.TryGetValue(pathId, out path) && path.ready && !path.failed;
        }

        public bool IsPathFailed(int pathId)
        {
            return _paths.TryGetValue(pathId, out Path path) && path.ready && path.failed;
        }

        public void ReleasePath(int pathId)
        {
            _paths.Remove(pathId);
        }

        public void TickFrame()
        {
            if (!IsReady || _queue.Count == 0)
                return;

            float deadline = Time.realtimeSinceStartup + MaxSolveMilliseconds * 0.001f;
            int solved = 0;
            while (solved < MaxPathsPerFrame && _queue.Count > 0)
            {
                if (solved > 0 && Time.realtimeSinceStartup >= deadline)
                    break;
                Request req = _queue.Dequeue();
                Solve(req);
                solved++;
            }
        }

        void Solve(Request req)
        {
            if (!_paths.TryGetValue(req.pathId, out Path path))
                return;

            int start = _graph.FindNearestNode(req.from);
            int goal = _graph.FindNearestNode(req.to);
            if (start < 0 || goal < 0)
            {
                path.ready = true;
                path.failed = true;
                _paths[req.pathId] = path;
                return;
            }

            if (start == goal)
            {
                path.waypoints.Clear();
                path.waypoints.Add(req.to);
                path.ready = true;
                path.failed = false;
                _paths[req.pathId] = path;
                return;
            }

            EnsureBuffers(_graph.Nodes.Length);
            NextStamp();
            _openCount = 0;

            _gScore[start] = 0f;
            _fScore[start] = Heuristic(start, goal);
            _cameFrom[start] = -1;
            HeapPush(start);

            bool found = false;
            int guard = 0;
            while (_openCount > 0 && guard++ < MaxOpen)
            {
                int current = HeapPop();
                if (_closedMarker[current] == _stamp)
                    continue;
                _closedMarker[current] = _stamp;

                if (current == goal)
                {
                    found = true;
                    break;
                }

                PlanetNavGraph.Node node = _graph.Nodes[current];
                for (int e = 0; e < node.edgeCount; e++)
                {
                    PlanetNavGraph.Edge edge = _graph.Edges[node.edgeStart + e];
                    int neighbor = edge.to;
                    if (_closedMarker[neighbor] == _stamp)
                        continue;

                    float tentative = _gScore[current] + edge.cost;
                    bool seen = _openMarker[neighbor] == _stamp;
                    if (seen && tentative >= _gScore[neighbor])
                        continue;

                    _cameFrom[neighbor] = current;
                    _gScore[neighbor] = tentative;
                    _fScore[neighbor] = tentative + Heuristic(neighbor, goal);
                    // Lazy heap: allow duplicates; closed check on pop keeps it correct.
                    HeapPush(neighbor);
                }
            }

            path.waypoints.Clear();
            if (!found)
            {
                path.ready = true;
                path.failed = true;
                _paths[req.pathId] = path;
                return;
            }

            _stackScratch.Clear();
            for (int c = goal; c >= 0; c = _cameFrom[c])
            {
                _stackScratch.Add(c);
                if (c == start)
                    break;
            }
            for (int i = _stackScratch.Count - 1; i >= 0; i--)
                path.waypoints.Add(_graph.WorldPosition(_stackScratch[i]));
            path.waypoints.Add(req.to);
            path.ready = true;
            path.failed = false;
            _paths[req.pathId] = path;
        }

        float Heuristic(int a, int b)
        {
            Vector3 pa = _graph.WorldPosition(a);
            Vector3 pb = _graph.WorldPosition(b);
            return Vector3.Distance(pa, pb);
        }

        void EnsureBuffers(int n)
        {
            if (_gScore.Length >= n && _openHeap.Length >= MaxOpen)
                return;
            _gScore = new float[n];
            _fScore = new float[n];
            _cameFrom = new int[n];
            _openHeap = new int[MaxOpen];
            _openMarker = new byte[n];
            _closedMarker = new byte[n];
        }

        void NextStamp()
        {
            _stamp++;
            if (_stamp != 0)
                return;
            // Wrap — clear markers once every 255 searches.
            _stamp = 1;
            System.Array.Clear(_openMarker, 0, _openMarker.Length);
            System.Array.Clear(_closedMarker, 0, _closedMarker.Length);
        }

        void HeapPush(int node)
        {
            if (_openCount >= _openHeap.Length)
                return;
            _openMarker[node] = _stamp;
            int i = _openCount++;
            _openHeap[i] = node;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (_fScore[_openHeap[parent]] <= _fScore[_openHeap[i]])
                    break;
                int tmp = _openHeap[parent];
                _openHeap[parent] = _openHeap[i];
                _openHeap[i] = tmp;
                i = parent;
            }
        }

        int HeapPop()
        {
            int result = _openHeap[0];
            int last = _openHeap[--_openCount];
            if (_openCount == 0)
                return result;

            int i = 0;
            _openHeap[0] = last;
            while (true)
            {
                int left = (i << 1) + 1;
                if (left >= _openCount)
                    break;
                int right = left + 1;
                int best = left;
                if (right < _openCount && _fScore[_openHeap[right]] < _fScore[_openHeap[left]])
                    best = right;
                if (_fScore[_openHeap[i]] <= _fScore[_openHeap[best]])
                    break;
                int tmp = _openHeap[i];
                _openHeap[i] = _openHeap[best];
                _openHeap[best] = tmp;
                i = best;
            }
            return result;
        }
    }
}
