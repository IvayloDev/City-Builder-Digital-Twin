using System;
using System.Collections.Generic;
using UnityEngine;

namespace CityTwin.Simulation
{
    /// <summary>Transit graph for shortest-path (Dijkstra). Nodes = stops/hubs, edges = segments with lengths.</summary>
    public class TransitGraph
    {
        private readonly List<TransitNode> _nodes = new List<TransitNode>();
        private readonly List<TransitEdge> _edges = new List<TransitEdge>();
        private readonly Dictionary<int, List<TransitEdge>> _outgoing = new Dictionary<int, List<TransitEdge>>();

        public IReadOnlyList<TransitNode> Nodes => _nodes;
        public IReadOnlyList<TransitEdge> Edges => _edges;

        public struct TransitNode
        {
            public int Id;
            public Vector2 Position;
            public float Population;
        }

        public struct TransitEdge
        {
            public int FromId;
            public int ToId;
            public float Length;
        }

        public void Clear()
        {
            _nodes.Clear();
            _edges.Clear();
            _outgoing.Clear();
        }

        public int AddNode(Vector2 position, float population = 0f)
        {
            int id = _nodes.Count;
            _nodes.Add(new TransitNode { Id = id, Position = position, Population = population });
            _outgoing[id] = new List<TransitEdge>();
            return id;
        }

        public void AddEdge(int fromId, int toId, float length)
        {
            if (fromId < 0 || fromId >= _nodes.Count || toId < 0 || toId >= _nodes.Count)
                return;
            var edge = new TransitEdge { FromId = fromId, ToId = toId, Length = length };
            _edges.Add(edge);
            _outgoing[fromId].Add(edge);
        }

        /// <summary>Shortest path from startId to all nodes. Returns distances (key = node id, value = distance). Use float.MaxValue for unreachable.</summary>
        public Dictionary<int, float> Dijkstra(int startId)
        {
            var dist = new Dictionary<int, float>();
            var pq = new SortedSet<(float d, int id)>(Comparer<(float d, int id)>.Create((a, b) =>
            {
                int c = a.d.CompareTo(b.d);
                return c != 0 ? c : a.id.CompareTo(b.id);
            }));

            foreach (var n in _nodes)
                dist[n.Id] = float.MaxValue;
            dist[startId] = 0;
            pq.Add((0, startId));

            while (pq.Count > 0)
            {
                var (d, u) = pq.Min;
                pq.Remove(pq.Min);
                if (d > dist[u]) continue;
                if (!_outgoing.TryGetValue(u, out var edges)) continue;
                foreach (var e in edges)
                {
                    float alt = dist[u] + e.Length;
                    if (alt < dist[e.ToId])
                    {
                        dist[e.ToId] = alt;
                        pq.Add((alt, e.ToId));
                    }
                }
            }
            return dist;
        }

        /// <summary>Shortest path distance from fromId to toId. Returns float.MaxValue if unreachable.</summary>
        public float ShortestPathDistance(int fromId, int toId)
        {
            var dist = Dijkstra(fromId);
            return dist.TryGetValue(toId, out float d) ? d : float.MaxValue;
        }

        public TransitNode GetNode(int id)
        {
            return id >= 0 && id < _nodes.Count ? _nodes[id] : default;
        }

        /// <summary>Distance from point to nearest road segment (edge). Used for HTML-style "connected to road" check.</summary>
        public float DistanceToNearestSegment(Vector2 point)
        {
            float best = float.MaxValue;
            foreach (var e in _edges)
            {
                var a = GetNode(e.FromId).Position;
                var b = GetNode(e.ToId).Position;
                float d = DistancePointToSegment(point, a, b);
                if (d < best) best = d;
            }
            return _edges.Count == 0 ? float.MaxValue : best;
        }

        private static float DistancePointToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 v = b - a;
            Vector2 w = p - a;
            float c1 = Vector2.Dot(w, v);
            float c2 = Vector2.Dot(v, v);
            if (c2 <= 0.0001f) return Vector2.Distance(p, a);
            if (c1 <= 0) return Vector2.Distance(p, a);
            if (c1 >= c2) return Vector2.Distance(p, b);
            float t = c1 / c2;
            Vector2 closest = a + t * v;
            return Vector2.Distance(p, closest);
        }
    }
}
