using System.Collections.Generic;
using UnityEngine;
using CityTwin.Core;
using CityTwin.Input;
using CityTwin.Simulation;
using CityTwin.Config;
using CityTwin.UI;

namespace CityTwin.Core
{
    /// <summary>Wires TileTrackingManager and SimulationEngine for this instance. Subscribes to OSC and updates simulation. No statics.</summary>
    public class GameInstanceCoordinator : MonoBehaviour
    {
        [SerializeField] private SimulationEngine simulationEngine;
        [SerializeField] private TileTrackingManager tileTracking;
        [SerializeField] private GameConfigLoader configLoader;
        [SerializeField] private SessionTimer sessionTimer;
        [SerializeField] private BuildingSpawner buildingSpawner;
        [Tooltip("Optional. If set and valid, simulation uses prefab-driven hub positions and population instead of config.")]
        [SerializeField] private HubRegistry hubRegistry;
        [Tooltip("Optional. Draws connection lines between buildings and hubs. Assign or auto-found in children.")]
        [SerializeField] private HubConnectionRenderer hubConnectionRenderer;

        private readonly Dictionary<string, string> _oscToEngineTileId = new Dictionary<string, string>();

        /// <summary>Current budget for this instance. Decremented when placing tiles.</summary>
        public int Budget { get; private set; }

        private void Awake()
        {
            if (simulationEngine == null) simulationEngine = GetComponentInChildren<SimulationEngine>(true);
            if (tileTracking == null) tileTracking = GetComponent<TileTrackingManager>();
            if (configLoader == null) configLoader = GetComponentInChildren<GameConfigLoader>(true);
            if (sessionTimer == null) sessionTimer = GetComponentInChildren<SessionTimer>(true);
        }

        private void OnEnable()
        {
            if (buildingSpawner == null) buildingSpawner = GetComponentInChildren<BuildingSpawner>(true);
            if (configLoader != null && configLoader.Config != null)
            {
                var cfg = configLoader.Config;
                Budget = cfg.Budget?.startingBudget ?? 1000;
                simulationEngine?.SetBuildingCatalog(new List<BuildingDefinition>(cfg.Buildings ?? System.Array.Empty<BuildingDefinition>()));
                var acc = cfg.Accessibility;
                simulationEngine?.SetConfig(
                    cfg.Scoring.epsilonDistance,
                    cfg.Scoring.qolCapPerMetric,
                    acc.walkingDistance,
                    acc.roadConnectRange,
                    acc.zoneRadius,
                    acc.defaultConnectionRadius,
                    cfg.Scoring.populationScale
                );
                if (cfg.Map != null && cfg.Map.nodes != null && cfg.Map.nodes.Length > 0)
                    BuildTransitGraphFromConfig(cfg.Map);
                else
                    BuildDefaultTransitGraphIfNeeded();

                if (hubRegistry == null) hubRegistry = GetComponentInChildren<HubRegistry>(true);
                if (hubRegistry != null && hubRegistry.IsValid && hubRegistry.Hubs.Count > 0)
                {
                    var hubs = new List<(Vector2 position, float population)>();
                    foreach (var h in hubRegistry.Hubs)
                    {
                        Vector2 hubPos = buildingSpawner != null
                            ? buildingSpawner.WorldToContentLocal(h.transform.position)
                            : h.Position2D;
                        hubs.Add((hubPos, h.Population));
                        //Debug.Log($"[Coordinator] Hub '{h.HubId}' pos in content root local = ({hubPos.x:F1},{hubPos.y:F1}), pop={h.Population}");
                    }
                    simulationEngine?.SetScoringHubs(hubs);
                }
            }
            if (sessionTimer != null && configLoader?.Config != null)
            {
                sessionTimer.SetFromConfig(configLoader.Config);
                sessionTimer.StartSession();
            }
            if (tileTracking != null)
            {
                tileTracking.OnTileUpdated += OnTileUpdated;
                tileTracking.OnTileRemoved += OnTileRemoved;
            }
            if (hubConnectionRenderer == null) hubConnectionRenderer = GetComponentInChildren<HubConnectionRenderer>(true);
            if (simulationEngine != null)
                simulationEngine.OnMetricsChanged += PushHubIndicators;
        }

        private void OnDisable()
        {
            if (tileTracking != null)
            {
                tileTracking.OnTileUpdated -= OnTileUpdated;
                tileTracking.OnTileRemoved -= OnTileRemoved;
            }
            if (simulationEngine != null)
                simulationEngine.OnMetricsChanged -= PushHubIndicators;
        }

        /// <summary>Build transit graph from config map (nodes + edges). Node positions are treated as content root local space.</summary>
        private void BuildTransitGraphFromConfig(GameConfig.MapData map)
        {
            if (simulationEngine == null || map.nodes == null || map.nodes.Length == 0) return;
            var graph = new TransitGraph();
            for (int i = 0; i < map.nodes.Length; i++)
            {
                var n = map.nodes[i];
                Vector2 nodePos = new Vector2(n.x, n.y);
                graph.AddNode(nodePos, n.population > 0 ? n.population : 50000f);
            }
            if (map.edges != null)
            {
                for (int i = 0; i < map.edges.Length; i++)
                {
                    var e = map.edges[i];
                    if (e.from >= 0 && e.from < map.nodes.Length && e.to >= 0 && e.to < map.nodes.Length)
                    {
                        float len = e.length > 0 ? e.length : Vector2.Distance(graph.GetNode(e.from).Position, graph.GetNode(e.to).Position);
                        graph.AddEdge(e.from, e.to, len);
                    }
                }
            }
            simulationEngine.SetTransitGraph(graph);
            if (map.obstacles != null && map.obstacles.Length > 0)
            {
                var obstacles = new List<(Vector2 center, float radius)>();
                foreach (var o in map.obstacles)
                    obstacles.Add((new Vector2(o.x, o.y), Mathf.Max(0.1f, o.radius)));
                simulationEngine.SetObstacles(obstacles);
            }
        }

        /// <summary>Build a simple default hub+road layout when no map config is provided. Uses content root local space (center-origin).</summary>
        private void BuildDefaultTransitGraphIfNeeded()
        {
            if (simulationEngine == null) return;
            var graph = new TransitGraph();
            float half = 90f;
            graph.AddNode(new Vector2(-half, -half), 50000f);
            graph.AddNode(new Vector2( half, -half), 50000f);
            graph.AddNode(new Vector2( half,  half), 50000f);
            graph.AddNode(new Vector2(-half,  half), 50000f);
            float len = Vector2.Distance(graph.GetNode(0).Position, graph.GetNode(1).Position);
            graph.AddEdge(0, 1, len);
            graph.AddEdge(1, 2, len);
            graph.AddEdge(2, 3, len);
            graph.AddEdge(3, 0, len);
            graph.AddEdge(0, 2, len * 1.4f);
            graph.AddEdge(1, 3, len * 1.4f);
            simulationEngine.SetTransitGraph(graph);
        }

        [Tooltip("Scale TUIO 0-1 coordinates to simulation space (default graph uses 0-300).")]
        [SerializeField] private float tableScale = 300f;

        private void OnTileUpdated(TilePose pose)
        {
            if (simulationEngine == null) { Debug.LogWarning("[Coordinator] simulationEngine is null, skipping."); return; }

            Vector2 simPos = buildingSpawner != null
                ? buildingSpawner.TuioToLocalPosition(pose.Position)
                : pose.Position * tableScale;
            var simPose = new TilePose(simPos, pose.Rotation, pose.BuildingId, pose.SourceId, pose.TileId);

            if (_oscToEngineTileId.TryGetValue(pose.TileId, out string engineId))
            {
                simulationEngine.UpdateTilePosition(engineId, simPose.Position, simPose.Rotation);
                buildingSpawner?.MoveBuilding(pose, engineId);
                //Debug.Log($"[Coordinator] Move tile {engineId} → simPos=({simPose.Position.x:F0},{simPose.Position.y:F0})");
                return;
            }

            //Debug.Log($"[Coordinator] OnTileUpdated (new) buildingId={pose.BuildingId} budget={Budget}");
            int price = 0;
            if (configLoader?.Config?.Buildings != null)
            {
                foreach (var b in configLoader.Config.Buildings)
                {
                    if (b.Id == pose.BuildingId) { price = b.Price; break; }
                }
            }
            if (price > 0 && Budget < price) { Debug.Log($"[Coordinator] Not enough budget: need {price}, have {Budget}. Skipping."); return; }
            if (price > 0) Budget -= price;
            engineId = simulationEngine.AddTile(simPose);
            //Debug.Log($"[Coordinator] AddTile returned engineId={engineId ?? "(null)"}");
            if (!string.IsNullOrEmpty(pose.TileId) && !string.IsNullOrEmpty(engineId))
                _oscToEngineTileId[pose.TileId] = engineId;
            if (buildingSpawner == null) Debug.LogWarning("[Coordinator] buildingSpawner is null, no visual will be spawned.");
            if (!string.IsNullOrEmpty(engineId)) buildingSpawner?.SpawnBuilding(pose, engineId);
        }

        private void OnTileRemoved(string oscTileId)
        {
            if (simulationEngine == null) return;
            if (_oscToEngineTileId.TryGetValue(oscTileId, out string engineId))
            {
                string buildingId = simulationEngine.GetBuildingIdForTile(engineId);
                _oscToEngineTileId.Remove(oscTileId);
                buildingSpawner?.RemoveBuilding(engineId);
                simulationEngine.RemoveTile(engineId);
                RefundBudgetForBuilding(buildingId);
                //Debug.Log($"[Coordinator] Tile removed oscTileId={oscTileId} engineId={engineId} → refunded; budget now {Budget}");
            }
        }

        private void PushHubIndicators()
        {
            if (hubRegistry == null || simulationEngine == null) return;
            var hubs = hubRegistry.Hubs;
            var metrics = simulationEngine.HubMetrics;
            int count = Mathf.Min(hubs.Count, metrics.Count);
            for (int i = 0; i < count; i++)
            {
                var m = metrics[i];
                hubs[i].SetMetricState(m.Environment, m.Economy, m.HealthSafety, m.CultureEdu);
            }
        }

        private void RefundBudgetForBuilding(string buildingId)
        {
            if (configLoader?.Config?.Buildings == null || string.IsNullOrEmpty(buildingId)) return;
            foreach (var b in configLoader.Config.Buildings)
            {
                if (b.Id == buildingId)
                {
                    Budget += b.Price;
                    return;
                }
            }
        }
    }
}
