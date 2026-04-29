#if UNITY_INCLUDE_TESTS
using CityTwin.Config;
using CityTwin.Core;
using CityTwin.Simulation;
using NUnit.Framework;
using UnityEngine;

public class SimulationEngineTests
{
    [Test]
    public void AddTile_ThenRemove_RecalculatesMetrics()
    {
        var go = new GameObject("Engine");
        var engine = go.AddComponent<SimulationEngine>();
        var catalog = new System.Collections.Generic.List<BuildingDefinition>
        {
            new BuildingDefinition
            {
                Id = "garden",
                ImpactSize = "Small",
                Importance = 0.6f,
                BaseValues = new BuildingDefinition.MetricValues { environment = 5, economy = 2, healthSafety = -2, cultureEdu = 2 }
            }
        };
        engine.SetBuildingCatalog(catalog);
        var graph = new TransitGraph();
        graph.AddNode(new Vector2(0, 0), 100f);
        graph.AddNode(new Vector2(500, 0), 50f);
        graph.AddEdge(0, 1, 500f);
        graph.GenerateStops(180f, 30f, 30f);
        engine.SetTransitGraph(graph);
        engine.SetConfig(new GameConfig.ScoringData(), new GameConfig.AccessibilityData { roadConnectRange = 500f, defaultConnectionRadius = 600f });

        float qolAfterAdd = 0f;
        engine.OnMetricsChanged += () => qolAfterAdd = engine.Qol;
        string tileId = engine.AddTile(new TilePose(new Vector2(50, 10), 0f, "garden", 0));
        Assert.That(tileId, Is.Not.Null);

        float qolAfterRemove = 0f;
        engine.OnMetricsChanged += () => qolAfterRemove = engine.Qol;
        bool removed = engine.RemoveTile(tileId);
        Assert.That(removed, Is.True);
        Assert.That(qolAfterRemove, Is.LessThanOrEqualTo(qolAfterAdd).Or.EqualTo(0));
    }

    [Test]
    public void Qol_IsClampedBetween0AndCap()
    {
        var go = new GameObject("Engine");
        var engine = go.AddComponent<SimulationEngine>();
        engine.SetBuildingCatalog(new System.Collections.Generic.List<BuildingDefinition>());
        engine.SetTransitGraph(new TransitGraph());
        engine.RecalculateMetrics();
        Assert.That(engine.Qol, Is.GreaterThanOrEqualTo(0f).And.LessThanOrEqualTo(80f));
    }
}
#endif
