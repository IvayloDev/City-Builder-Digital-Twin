using System;
using System.Collections.Generic;
using UnityEngine;
using CityTwin.Core;

namespace CityTwin.Simulation
{
    /// <summary>Per-instance simulation engine. Spec formulas: MetricScore = (BaseValue x Pop) / RadialDist; Accessibility = (Importance x Pop) / TransitDist; QOL = sum of 5 metrics capped at 20 each.</summary>
    public class SimulationEngine : MonoBehaviour
    {
        [SerializeField] private float epsilonDistance = 0.1f;
        [SerializeField] private float qolCapPerMetric = 20f;

        private readonly List<PlacedTile> _placedTiles = new List<PlacedTile>();
        private List<BuildingDefinition> _buildingCatalog = new List<BuildingDefinition>();
        private TransitGraph _transitGraph = new TransitGraph();
        private List<(Vector2 center, float radius)> _obstacles = new List<(Vector2, float)>();
        /// <summary>When set (prefab-driven hubs), use (BaseValue x Population) / RadialDistance for metrics. Empty = use TransitGraph nodes.</summary>
        private List<(Vector2 position, float population)> _scoringHubs = new List<(Vector2, float)>();
        private int _nextTileId;

        private float _walkingDistance = 200f;
        private float _roadConnectRange = 200f;
        private float _zoneRadius = 200f;
        private float _defaultConnectionRadius = 500f;
        private float _populationScale = 1000f;

        private const float RadiusSmall = 400f;
        private const float RadiusMedium = 500f;
        private const float RadiusLarge = 600f;

        private float _qol;
        private float _environment;
        private float _economy;
        private float _healthSafety;
        private float _cultureEdu;
        private float _accessibility;

        public float Qol => _qol;
        public float Environment => _environment;
        public float Economy => _economy;
        public float HealthSafety => _healthSafety;
        public float CultureEdu => _cultureEdu;
        public float Accessibility => _accessibility;

        public struct HubMetricSnapshot
        {
            public int HubIndex;
            public float Environment;
            public float Economy;
            public float HealthSafety;
            public float CultureEdu;
            public float Accessibility;
        }

        private readonly List<HubMetricSnapshot> _hubMetrics = new List<HubMetricSnapshot>();
        public IReadOnlyList<HubMetricSnapshot> HubMetrics => _hubMetrics;

        public struct TileHubConnection
        {
            public string TileId;
            public int HubIndex;
        }

        private readonly List<TileHubConnection> _activeConnections = new List<TileHubConnection>();
        public IReadOnlyList<TileHubConnection> ActiveConnections => _activeConnections;

        public struct TileRoadConnection
        {
            public string TileId;
            public Vector2 SnapPoint;
            public float Distance;
        }

        private readonly List<TileRoadConnection> _activeRoadConnections = new List<TileRoadConnection>();
        public IReadOnlyList<TileRoadConnection> ActiveRoadConnections => _activeRoadConnections;

        public struct TilePlacementState
        {
            public string TileId;
            public string BuildingId;
            public Vector2 Position;
            public bool Connected;
            public bool Inactive;
            public bool OverlapInvalid;
        }

        private readonly List<TilePlacementState> _tileStates = new List<TilePlacementState>();
        public IReadOnlyList<TilePlacementState> TileStates => _tileStates;

        public TransitGraph TransitGraph => _transitGraph;

        public event Action OnMetricsChanged;

        [Tooltip("Log current metrics to console whenever they are recalculated (e.g. when no UI yet).")]
        [SerializeField] private bool logMetricsWhenChanged = true;

        public void SetBuildingCatalog(List<BuildingDefinition> catalog)
        {
            _buildingCatalog = catalog ?? new List<BuildingDefinition>();
        }

        public void SetConfig(float epsilon, float qolCap, float walkingDist, float roadConnectRange = 200f, float zoneRadius = 200f, float defaultConnectionRadius = 500f, float populationScale = 1000f)
        {
            epsilonDistance = Mathf.Max(0.01f, epsilon);
            qolCapPerMetric = Mathf.Max(1f, qolCap);
            _walkingDistance = Mathf.Max(1f, walkingDist);
            _roadConnectRange = Mathf.Max(1f, roadConnectRange);
            _zoneRadius = Mathf.Max(1f, zoneRadius);
            _defaultConnectionRadius = Mathf.Max(1f, defaultConnectionRadius);
            _populationScale = Mathf.Max(1f, populationScale);
        }

        public void SetTransitGraph(TransitGraph graph)
        {
            _transitGraph = graph ?? new TransitGraph();
        }

        /// <summary>Set obstacles (e.g. water, mountains). Buildings placed on obstacles are inactive and do not affect QOL.</summary>
        public void SetObstacles(List<(Vector2 center, float radius)> obstacles)
        {
            _obstacles = obstacles ?? new List<(Vector2, float)>();
        }

        /// <summary>Set scoring hubs from HubRegistry (prefab-driven). When non-empty, metrics use (BaseValue × Population) / max(RadialDistance, epsilon). No dependency on config for hub positions or population.</summary>
        public void SetScoringHubs(List<(Vector2 position, float population)> hubs)
        {
            _scoringHubs = hubs ?? new List<(Vector2, float)>();
        }

        public string AddTile(TilePose pose)
        {
            var def = GetBuilding(pose.BuildingId);
            if (def == null)
            {
                Debug.LogWarning($"[SimEngine:AddTile] FAILED — no building in catalog for id '{pose.BuildingId}'. " +
                                 $"Catalog has {_buildingCatalog.Count} entries: [{string.Join(", ", _buildingCatalog.ConvertAll(b => b.Id))}]");
                return null;
            }
            string tileId = $"tile_{_nextTileId++}";
            bool inactive = IsOnObstacle(pose.Position);
            var roadConns = inactive ? new List<TransitGraph.ConnectionPoint>()
                                     : _transitGraph.GetRoadConnections(pose.Position, _roadConnectRange);

            float nearestSegDist = _transitGraph.Edges.Count > 0
                ? _transitGraph.DistanceToNearestSegment(pose.Position)
                : -1f;

            Debug.Log($"[SimEngine:AddTile] {tileId} building={pose.BuildingId} pos=({pose.Position.x:F1},{pose.Position.y:F1}) " +
                      $"onObstacle={inactive} roadConns={roadConns.Count} roadConnectRange={_roadConnectRange:F0} " +
                      $"nearestSegDist={nearestSegDist:F1} graphNodes={_transitGraph.Nodes.Count} graphEdges={_transitGraph.Edges.Count}");

            _placedTiles.Add(new PlacedTile
            {
                TileId = tileId,
                BuildingId = pose.BuildingId,
                Position = pose.Position,
                Rotation = pose.Rotation,
                Inactive = inactive,
                OverlapInvalid = false,
                Connected = roadConns.Count > 0,
                RoadConnections = roadConns
            });
            RecalculateMetrics();
            return tileId;
        }

        public bool UpdateTilePosition(string tileId, Vector2 position, float rotation, bool overlapInvalid = false)
        {
            int idx = _placedTiles.FindIndex(t => t.TileId == tileId);
            if (idx < 0) return false;
            var t = _placedTiles[idx];
            t.Position = position;
            t.Rotation = rotation;
            t.Inactive = IsOnObstacle(position);
            t.OverlapInvalid = overlapInvalid;
            t.RoadConnections = t.Inactive
                ? new List<TransitGraph.ConnectionPoint>()
                : _transitGraph.GetRoadConnections(position, _roadConnectRange);
            t.Connected = t.RoadConnections.Count > 0;
            _placedTiles[idx] = t;
            RecalculateMetrics();
            return true;
        }

        public bool RemoveTile(string tileId)
        {
            int idx = _placedTiles.FindIndex(t => t.TileId == tileId);
            if (idx < 0) return false;
            _placedTiles.RemoveAt(idx);
            RecalculateMetrics();
            return true;
        }

        /// <summary>True if the tile is placed on an obstacle (e.g. water) and does not affect QOL. Use for UI feedback.</summary>
        public bool IsTileInactive(string tileId)
        {
            int idx = _placedTiles.FindIndex(t => t.TileId == tileId);
            return idx >= 0 && _placedTiles[idx].Inactive;
        }

        /// <summary>True if the tile is connected to at least one road segment within roadConnectRange.</summary>
        public bool IsTileConnected(string tileId)
        {
            int idx = _placedTiles.FindIndex(t => t.TileId == tileId);
            return idx >= 0 && _placedTiles[idx].Connected;
        }

        /// <summary>Returns the building id for a placed tile, or null if not found. Use e.g. for refund on remove.</summary>
        public string GetBuildingIdForTile(string engineTileId)
        {
            int idx = _placedTiles.FindIndex(t => t.TileId == engineTileId);
            return idx >= 0 ? _placedTiles[idx].BuildingId : null;
        }

        public void RecalculateMetrics()
        {
            int hubCount;
            Vector2[] hubPositions;
            float[] hubPopulations;

            if (_scoringHubs != null && _scoringHubs.Count > 0)
            {
                hubCount = _scoringHubs.Count;
                hubPositions = new Vector2[hubCount];
                hubPopulations = new float[hubCount];
                for (int i = 0; i < hubCount; i++)
                {
                    hubPositions[i] = _scoringHubs[i].position;
                    hubPopulations[i] = _scoringHubs[i].population / _populationScale;
                }
            }
            else
            {
                var nodes = _transitGraph.Nodes;
                if (nodes.Count == 0)
                {
                    _environment = _economy = _healthSafety = _cultureEdu = _accessibility = 0f;
                    _qol = 0f;
                    _activeConnections.Clear();
                    _activeRoadConnections.Clear();
                    _tileStates.Clear();
                    _hubMetrics.Clear();
                    if (logMetricsWhenChanged)
                        Debug.Log("[Metrics] QOL=0 (no hubs/graph) | tiles=" + _placedTiles.Count);
                    OnMetricsChanged?.Invoke();
                    return;
                }
                hubCount = nodes.Count;
                hubPositions = new Vector2[hubCount];
                hubPopulations = new float[hubCount];
                for (int i = 0; i < hubCount; i++)
                {
                    hubPositions[i] = nodes[i].Position;
                    hubPopulations[i] = nodes[i].Population / _populationScale;
                }
            }

            float[] hubEnv = new float[hubCount];
            float[] hubEco = new float[hubCount];
            float[] hubSaf = new float[hubCount];
            float[] hubCul = new float[hubCount];
            float[] hubAcc = new float[hubCount];

            _activeConnections.Clear();
            _activeRoadConnections.Clear();
            _tileStates.Clear();

            // Populate tile placement states and road connection visuals
            foreach (var t in _placedTiles)
            {
                _tileStates.Add(new TilePlacementState
                {
                    TileId = t.TileId,
                    BuildingId = t.BuildingId,
                    Position = t.Position,
                    Connected = t.Connected,
                    Inactive = t.Inactive,
                    OverlapInvalid = t.OverlapInvalid
                });

                if (t.Inactive) continue;
                if (t.RoadConnections == null) continue;
                foreach (var rc in t.RoadConnections)
                {
                    _activeRoadConnections.Add(new TileRoadConnection
                    {
                        TileId = t.TileId,
                        SnapPoint = rc.Position,
                        Distance = rc.Distance
                    });
                }
            }

            // --- Standard metrics ---
            // Both paths now gate on road connectivity: unconnected buildings contribute nothing.
            foreach (var t in _placedTiles)
            {
                if (t.Inactive || t.OverlapInvalid || !t.Connected) continue;
                var b = GetBuilding(t.BuildingId);
                if (b == null || b.BaseValues == null) continue;

                float connectionRadius = GetConnectionRadius(b);
                var v = b.BaseValues;

                for (int i = 0; i < hubCount; i++)
                {
                    float dist = Vector2.Distance(t.Position, hubPositions[i]);
                    if (dist > connectionRadius) continue;

                    _activeConnections.Add(new TileHubConnection { TileId = t.TileId, HubIndex = i });

                    float normalizedDist = dist / connectionRadius;
                    float influence = 1f - (normalizedDist * normalizedDist);

                    hubEnv[i] += v.environment * influence;
                    hubEco[i] += v.economy * influence;
                    hubSaf[i] += v.healthSafety * influence;
                    hubCul[i] += v.cultureEdu * influence;
                }
            }

            // --- Accessibility: (Importance x Population) / TransitDistance ---
            // Transit distance = walk from tile to nearest transit segment + shortest graph path to hub.
            // Map each hub to its nearest transit node for Dijkstra lookups.
            bool hasTransit = _transitGraph.Nodes.Count > 0 && _transitGraph.Edges.Count > 0;
            int[] hubTransitNodeIds = new int[hubCount];
            if (hasTransit)
            {
                for (int i = 0; i < hubCount; i++)
                    hubTransitNodeIds[i] = _transitGraph.NearestNodeId(hubPositions[i]);
            }

            foreach (var t in _placedTiles)
            {
                if (t.Inactive || t.OverlapInvalid || !t.Connected) continue;
                var b = GetBuilding(t.BuildingId);
                if (b == null) continue;

                if (!hasTransit)
                {
                    // No transit graph: fall back to radial distance for accessibility
                    for (int i = 0; i < hubCount; i++)
                    {
                        float dist = Vector2.Distance(t.Position, hubPositions[i]);
                        float connectionRadius = GetConnectionRadius(b);
                        if (dist > connectionRadius) continue;
                        float radialDist = Mathf.Max(dist, epsilonDistance);
                        hubAcc[i] += (b.Importance * hubPopulations[i]) / radialDist;
                    }
                    continue;
                }

                float walkDist = _transitGraph.DistanceToNearestSegment(t.Position);
                if (walkDist > _walkingDistance) continue;

                int nearestNode = _transitGraph.NearestNodeId(t.Position);
                if (nearestNode < 0) continue;

                var dijkDist = _transitGraph.Dijkstra(nearestNode);

                for (int i = 0; i < hubCount; i++)
                {
                    int hubNodeId = hubTransitNodeIds[i];
                    if (!dijkDist.TryGetValue(hubNodeId, out float graphDist) || graphDist >= float.MaxValue)
                        continue;

                    float transitDist = Mathf.Max(walkDist + graphDist, epsilonDistance);
                    hubAcc[i] += (b.Importance * hubPopulations[i]) / transitDist;
                }
            }

            // --- Aggregate per-hub scores, average across hubs, clamp each metric to [0, qolCapPerMetric] ---
            float sumEnv = 0, sumEco = 0, sumSaf = 0, sumCul = 0, sumAcc = 0;
            for (int i = 0; i < hubCount; i++)
            {
                sumEnv += Mathf.Clamp(hubEnv[i], 0f, 100f);
                sumEco += Mathf.Clamp(hubEco[i], 0f, 100f);
                sumSaf += Mathf.Clamp(hubSaf[i], 0f, 100f);
                sumCul += Mathf.Clamp(hubCul[i], 0f, 100f);
                sumAcc += Mathf.Clamp(hubAcc[i], 0f, 100f);
            }

            float n = hubCount;
            _environment = Mathf.Clamp(sumEnv / n, 0f, qolCapPerMetric);
            _economy = Mathf.Clamp(sumEco / n, 0f, qolCapPerMetric);
            _healthSafety = Mathf.Clamp(sumSaf / n, 0f, qolCapPerMetric);
            _cultureEdu = Mathf.Clamp(sumCul / n, 0f, qolCapPerMetric);
            _accessibility = Mathf.Clamp(sumAcc / n, 0f, qolCapPerMetric);

            // QOL = sum of 5 metrics (each capped at 20), giving 0-100
            _qol = Mathf.Clamp(
                Mathf.Round(_environment + _economy + _healthSafety + _cultureEdu + _accessibility),
                0f, 100f);

            _hubMetrics.Clear();
            for (int i = 0; i < hubCount; i++)
            {
                _hubMetrics.Add(new HubMetricSnapshot
                {
                    HubIndex = i,
                    Environment = Mathf.Clamp(hubEnv[i], 0f, 100f),
                    Economy = Mathf.Clamp(hubEco[i], 0f, 100f),
                    HealthSafety = Mathf.Clamp(hubSaf[i], 0f, 100f),
                    CultureEdu = Mathf.Clamp(hubCul[i], 0f, 100f),
                    Accessibility = Mathf.Clamp(hubAcc[i], 0f, 100f)
                });
            }

            if (logMetricsWhenChanged)
            {
                string posInfo = "";
                if (_placedTiles.Count > 0)
                {
                    var t0 = _placedTiles[0];
                    posInfo = $" | tile0 pos=({t0.Position.x:F1},{t0.Position.y:F1})";
                    for (int i = 0; i < hubCount; i++)
                    {
                        float d = Vector2.Distance(t0.Position, hubPositions[i]);
                        posInfo += $" d[hub{i}]={d:F1}";
                    }
                }
                string hubInfo = "";
                for (int i = 0; i < hubCount; i++)
                    hubInfo += $" hub{i}=({hubPositions[i].x:F1},{hubPositions[i].y:F1}) pop={hubPopulations[i]:F1}";
                Debug.Log($"[Metrics] QOL={_qol:F0} | Env={_environment:F1} Eco={_economy:F1} Safe={_healthSafety:F1} Cul={_cultureEdu:F1} Access={_accessibility:F1} | tiles={_placedTiles.Count} conns={_activeConnections.Count} roadConns={_activeRoadConnections.Count}{posInfo} |{hubInfo}");
            }

            OnMetricsChanged?.Invoke();
        }

        private float GetConnectionRadius(BuildingDefinition b)
        {
            if (b.ConnectionRadius > 0) return b.ConnectionRadius;
            switch (b.ImpactSize)
            {
                case "Small":  return RadiusSmall;
                case "Medium": return RadiusMedium;
                case "Large":  return RadiusLarge;
                default:       return _defaultConnectionRadius;
            }
        }

        private BuildingDefinition GetBuilding(string buildingId)
        {
            foreach (var b in _buildingCatalog)
                if (b.Id == buildingId) return b;
            return null;
        }

        private bool IsOnObstacle(Vector2 position)
        {
            foreach (var (center, radius) in _obstacles)
                if (Vector2.Distance(position, center) <= radius) return true;
            return false;
        }

        private struct PlacedTile
        {
            public string TileId;
            public string BuildingId;
            public Vector2 Position;
            public float Rotation;
            public bool Inactive;
            public bool OverlapInvalid;
            public bool Connected;
            public List<TransitGraph.ConnectionPoint> RoadConnections;
        }
    }
}
