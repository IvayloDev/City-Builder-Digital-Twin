#if UNITY_INCLUDE_TESTS
using NUnit.Framework;
using UnityEngine;
using CityTwin.Simulation;

namespace CityTwin.Tests
{
    public class TransitGraphTests
    {
        [Test]
        public void Dijkstra_SingleNode_ReturnsZero()
        {
            var g = new TransitGraph();
            g.AddNode(Vector2.zero, 1f);
            var dist = g.Dijkstra(0);
            Assert.That(dist.Count, Is.EqualTo(1));
            Assert.That(dist[0], Is.EqualTo(0f));
        }

        [Test]
        public void Dijkstra_TwoNodesConnected_ReturnsLength()
        {
            var g = new TransitGraph();
            g.AddNode(Vector2.zero, 1f);
            g.AddNode(Vector2.right * 10f, 1f);
            g.AddEdge(0, 1, 10f);
            var dist = g.Dijkstra(0);
            Assert.That(dist[1], Is.EqualTo(10f));
        }

        [Test]
        public void Dijkstra_Chain_ReturnsCumulativeDistance()
        {
            var g = new TransitGraph();
            g.AddNode(Vector2.zero, 1f);
            g.AddNode(Vector2.right, 1f);
            g.AddNode(Vector2.right * 2f, 1f);
            g.AddEdge(0, 1, 1f);
            g.AddEdge(1, 2, 2f);
            var dist = g.Dijkstra(0);
            Assert.That(dist[0], Is.EqualTo(0f));
            Assert.That(dist[1], Is.EqualTo(1f));
            Assert.That(dist[2], Is.EqualTo(3f));
        }

        [Test]
        public void ShortestPathDistance_Unreachable_ReturnsMaxValue()
        {
            var g = new TransitGraph();
            g.AddNode(Vector2.zero, 1f);
            g.AddNode(Vector2.right, 1f);
            float d = g.ShortestPathDistance(0, 1);
            Assert.That(d, Is.GreaterThanOrEqualTo(1e30f));
        }
    }
}
#endif
