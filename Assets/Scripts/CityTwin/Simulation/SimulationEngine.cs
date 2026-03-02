using System;
using System.Collections.Generic;
using UnityEngine;
using CityTwin.Core;

namespace CityTwin.Simulation
{
    /// <summary>Per-instance simulation engine. Matches HTML reference: only buildings connected to roads affect hubs; inverse-square influence; accessibility = proximity + service blend + connection bonus.</summary>
    public class SimulationEngine : MonoBehaviour
    {
        [SerializeField] private float epsilonDistance = 0.1f;
        [SerializeField] private float qolCapPerMetric = 20f;

        private readonly List<PlacedTile> _placedTiles = new List<PlacedTile>();
        private List<BuildingDefinition> _buildingCatalog = new List<BuildingDefinition>();
        private TransitGraph _transitGraph = new TransitGraph();
        private List<(Vector2 center, float radius)> _obstacles = new List<(Vector2, float)>();
        /// <summary>When set (prefab-driven hubs), use (BaseValue × Population) / RadialDistance for metrics. Empty = use TransitGraph nodes.</summary>
        private List<(Vector2 position, float population)> _scoringHubs = new List<(Vector2, float)>();
        private int _nextTileId;

        private float _roadConnectRange = 200f;
        private float _zoneRadius = 200f;
        private float _defaultConnectionRadius = 500f;

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

        public event Action OnMetricsChanged;

        [Tooltip("Log current metrics to console whenever they are recalculated (e.g. when no UI yet).")]
        [SerializeField] private bool logMetricsWhenChanged = true;

        public void SetBuildingCatalog(List<BuildingDefinition> catalog)
        {
            _buildingCatalog = catalog ?? new List<BuildingDefinition>();
        }

        public void SetConfig(float epsilon, float qolCap, float walkingDist, float roadConnectRange = 200f, float zoneRadius = 200f, float defaultConnectionRadius = 500f)
        {
            epsilonDistance = Mathf.Max(0.01f, epsilon);
            qolCapPerMetric = Mathf.Max(1f, qolCap);
            _roadConnectRange = Mathf.Max(1f, roadConnectRange);
            _zoneRadius = Mathf.Max(1f, zoneRadius);
            _defaultConnectionRadius = Mathf.Max(1f, defaultConnectionRadius);
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
                Debug.LogWarning($"[SimulationEngine] AddTile: no building in catalog for id '{pose.BuildingId}'. Check TUIO classId mapping or add this id to game_config buildings.");
                return null;
            }
            string tileId = $"tile_{_nextTileId++}";
            bool inactive = IsOnObstacle(pose.Position);
            _placedTiles.Add(new PlacedTile
            {
                TileId = tileId,
                BuildingId = pose.BuildingId,
                Position = pose.Position,
                Rotation = pose.Rotation,
                Inactive = inactive
            });
            RecalculateMetrics();
            return tileId;
        }

        public bool UpdateTilePosition(string tileId, Vector2 position, float rotation)
        {
            int idx = _placedTiles.FindIndex(t => t.TileId == tileId);
            if (idx < 0) return false;
            var t = _placedTiles[idx];
            t.Position = position;
            t.Rotation = rotation;
            t.Inactive = IsOnObstacle(position);
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
            bool useSpecFormula;

            if (_scoringHubs != null && _scoringHubs.Count > 0)
            {
                hubCount = _scoringHubs.Count;
                hubPositions = new Vector2[hubCount];
                hubPopulations = new float[hubCount];
                for (int i = 0; i < hubCount; i++)
                {
                    hubPositions[i] = _scoringHubs[i].position;
                    hubPopulations[i] = _scoringHubs[i].population;
                }
                useSpecFormula = true;
            }
            else
            {
                var nodes = _transitGraph.Nodes;
                if (nodes.Count == 0)
                {
                    _environment = _economy = _healthSafety = _cultureEdu = _accessibility = 0f;
                    _qol = 0f;
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
                    hubPopulations[i] = nodes[i].Population;
                }
                useSpecFormula = false;
            }

            float[] hubEnv = new float[hubCount];
            float[] hubEco = new float[hubCount];
            float[] hubSaf = new float[hubCount];
            float[] hubCul = new float[hubCount];

            int connectedCount = 0;
            foreach (var t in _placedTiles)
            {
                if (t.Inactive) continue;
                float distToRoad = _transitGraph.DistanceToNearestSegment(t.Position);
                if (distToRoad <= _roadConnectRange) connectedCount++;
            }

            float globalConnectionBonus = connectedCount == 0 ? 0 : Mathf.Min(15f, 5f + Mathf.Round((float)(Math.Log(connectedCount + 1) / Math.Log(2)) * 5f));

            foreach (var t in _placedTiles)
            {
                if (t.Inactive) continue;
                float distToRoad = _transitGraph.DistanceToNearestSegment(t.Position);
                bool connected = distToRoad <= _roadConnectRange;
                if (!connected) continue;

                var b = GetBuilding(t.BuildingId);
                if (b == null || b.BaseValues == null) continue;

                float connectionRadius = b.ConnectionRadius > 0 ? b.ConnectionRadius : _defaultConnectionRadius;
                var v = b.BaseValues;

                for (int i = 0; i < hubCount; i++)
                {
                    var hubPos = hubPositions[i];
                    float dist = Vector2.Distance(t.Position, hubPos);
                    if (dist > connectionRadius) continue;

                    float radialDist = Mathf.Max(dist, epsilonDistance);

                    if (useSpecFormula)
                    {
                        float pop = hubPopulations[i];
                        float scale = pop / radialDist;
                        hubEnv[i] += v.environment * scale;
                        hubEco[i] += v.economy * scale;
                        hubSaf[i] += v.healthSafety * scale;
                        hubCul[i] += v.cultureEdu * scale;
                        if (v.economy != 0) hubEnv[i] -= v.economy * 0.3f * scale;
                        if (v.environment != 0) hubEco[i] -= v.environment * 0.3f * scale;
                    }
                    else
                    {
                        float normalizedDist = dist / connectionRadius;
                        float influence = 1f - (normalizedDist * normalizedDist);

                        if (b.Id == "factory")
                        {
                            const float innerRadius = 70f;
                            if (dist < innerRadius)
                            {
                                hubEco[i] -= 4f * influence;
                                hubEnv[i] -= 4f * influence;
                                hubSaf[i] -= 3f * influence;
                                hubCul[i] -= 3f * influence;
                            }
                            else
                            {
                                float donutPower = Mathf.Clamp01((dist - innerRadius) / Mathf.Max(1f, connectionRadius - innerRadius)) * influence;
                                hubEco[i] += 15f * donutPower;
                                hubEnv[i] -= 5f * donutPower;
                            }
                        }
                        else
                        {
                            hubEnv[i] += v.environment * influence;
                            hubEco[i] += v.economy * influence;
                            hubSaf[i] += v.healthSafety * influence;
                            hubCul[i] += v.cultureEdu * influence;
                            if (v.economy != 0) hubEnv[i] -= v.economy * 0.3f * influence;
                            if (v.environment != 0) hubEco[i] -= v.environment * 0.3f * influence;
                        }
                    }
                }
            }

            float sumEnv = 0, sumEco = 0, sumSaf = 0, sumCul = 0, sumAcc = 0;
            for (int i = 0; i < hubCount; i++)
            {
                float ampEnv = Mathf.Clamp(hubEnv[i] * (useSpecFormula ? 1f : 20f), 0f, 100f);
                float ampEco = Mathf.Clamp(hubEco[i] * (useSpecFormula ? 1f : 20f), 0f, 100f);
                float ampSaf = Mathf.Clamp(hubSaf[i] * (useSpecFormula ? 1f : 20f), 0f, 100f);
                float ampCul = Mathf.Clamp(hubCul[i] * (useSpecFormula ? 1f : 20f), 0f, 100f);

                var hubPos = hubPositions[i];
                int zoneBuildings = 0, connectedNearHub = 0;
                foreach (var t in _placedTiles)
                {
                    if (t.Inactive) continue;
                    float d = Vector2.Distance(t.Position, hubPos);
                    if (d >= _zoneRadius) continue;
                    zoneBuildings++;
                    if (_transitGraph.DistanceToNearestSegment(t.Position) <= _roadConnectRange)
                        connectedNearHub++;
                }
                float proximityRatio = zoneBuildings > 0 ? (float)connectedNearHub / zoneBuildings : 0f;
                float proximityScore = Mathf.Round(proximityRatio * 70f);
                float serviceBlend = (ampEnv + ampEco + ampSaf + ampCul) / 4f;
                float serviceContribution = Mathf.Round(serviceBlend * 0.3f);
                float acc = Mathf.Min(100f, proximityScore + serviceContribution + globalConnectionBonus);

                sumEnv += ampEnv;
                sumEco += ampEco;
                sumSaf += ampSaf;
                sumCul += ampCul;
                sumAcc += acc;
            }

            float n = hubCount;
            _environment = Mathf.Clamp(sumEnv / n, 0f, qolCapPerMetric);
            _economy = Mathf.Clamp(sumEco / n, 0f, qolCapPerMetric);
            _healthSafety = Mathf.Clamp(sumSaf / n, 0f, qolCapPerMetric);
            _cultureEdu = Mathf.Clamp(sumCul / n, 0f, qolCapPerMetric);
            _accessibility = sumAcc / n;

            float qolScore = (_environment + _economy + _healthSafety + _cultureEdu + _accessibility) / 5f;
            float minStat = Mathf.Min(_environment, _economy, _healthSafety, _cultureEdu, _accessibility);
            _qol = minStat < 20f ? qolScore * 0.9f : qolScore;
            _qol = Mathf.Clamp(Mathf.Round(_qol), 0f, 100f);

            if (logMetricsWhenChanged)
            {
                string posInfo = _placedTiles.Count > 0
                    ? $" | tile0 pos=({_placedTiles[0].Position.x:F0},{_placedTiles[0].Position.y:F0})"
                    : "";
                Debug.Log($"[Metrics] QOL={_qol:F0} | Env={_environment:F1} Eco={_economy:F1} Safe={_healthSafety:F1} Cul={_cultureEdu:F1} Access={_accessibility:F1} | tiles={_placedTiles.Count}{posInfo}");
            }

            OnMetricsChanged?.Invoke();
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
        }
    }
}
