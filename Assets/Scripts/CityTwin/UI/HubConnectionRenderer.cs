using System.Collections.Generic;
using UnityEngine;
using CityTwin.Core;
using CityTwin.Simulation;

namespace CityTwin.UI
{
    /// <summary>
    /// Manages visual connection lines between placed building tiles and the hubs they affect.
    /// A building in range of multiple hubs gets one line to each such hub.
    /// Uses a pooled prefab with an IConnectionVisual implementation so the visual style
    /// (Image, LineRenderer, particles, etc.) can be swapped by changing the prefab.
    /// </summary>
    public class HubConnectionRenderer : MonoBehaviour
    {
        [Tooltip("Optional override. Connection lines are always parented and positioned in BuildingSpawner's content root (the table/map) so they align with buildings. Only set this if you have no BuildingSpawner.")]
        [SerializeField] private RectTransform contentRootOverride;

        [Tooltip("Prefab with a MonoBehaviour implementing IConnectionVisual (e.g. StretchedImageConnection).")]
        [SerializeField] private GameObject connectionPrefab;

        [SerializeField] private HubRegistry hubRegistry;
        [SerializeField] private BuildingSpawner buildingSpawner;
        [SerializeField] private SimulationEngine simulationEngine;

        private readonly Dictionary<(string tileId, int hubIndex), IConnectionVisual> _active =
            new Dictionary<(string, int), IConnectionVisual>();
        private readonly List<IConnectionVisual> _pool = new List<IConnectionVisual>();
        private readonly HashSet<(string, int)> _currentKeys = new HashSet<(string, int)>();

        private void OnEnable()
        {
            if (simulationEngine != null)
                simulationEngine.OnMetricsChanged += Refresh;
                
        }

        private void OnDisable()
        {
            if (simulationEngine != null)
                simulationEngine.OnMetricsChanged -= Refresh;
        }

        private void Refresh()
        {
            if (simulationEngine == null || hubRegistry == null || buildingSpawner == null || connectionPrefab == null)
                return;

            // Always use the table (BuildingSpawner's root) so lines draw on the map with the buildings
            RectTransform root = buildingSpawner.ContentRoot != null ? buildingSpawner.ContentRoot : contentRootOverride;
            if (root == null)
                return;

            var connections = simulationEngine.ActiveConnections;
            var hubs = hubRegistry.Hubs;

            _currentKeys.Clear();
            bool useTableSpace = (root == buildingSpawner.ContentRoot);

            for (int i = 0; i < connections.Count; i++)
            {
                var c = connections[i];
                if (c.HubIndex < 0 || c.HubIndex >= hubs.Count) continue;

                Vector2 buildingPos;
                bool gotBuilding = useTableSpace
                    ? buildingSpawner.TryGetMarkerPosition(c.TileId, out buildingPos)
                    : buildingSpawner.TryGetMarkerPositionIn(c.TileId, root, out buildingPos);
                if (!gotBuilding) continue;

                Vector2 hubPos = GetHubLocalPosition(hubs[c.HubIndex], root);
                var key = (c.TileId, c.HubIndex);
                _currentKeys.Add(key);

                if (!_active.TryGetValue(key, out IConnectionVisual visual))
                {
                    visual = Acquire(root);
                    _active[key] = visual;
                }

                visual.UpdateEndpoints(buildingPos, hubPos);
                visual.SetActive(true);
            }

            // Deactivate visuals no longer needed
            var toRemove = new List<(string, int)>();
            foreach (var kv in _active)
            {
                if (!_currentKeys.Contains(kv.Key))
                {
                    kv.Value.SetActive(false);
                    _pool.Add(kv.Value);
                    toRemove.Add(kv.Key);
                }
            }
            for (int i = 0; i < toRemove.Count; i++)
                _active.Remove(toRemove[i]);
        }

        private IConnectionVisual Acquire(RectTransform root)
        {
            for (int i = _pool.Count - 1; i >= 0; i--)
            {
                var v = _pool[i];
                if (v != null)
                {
                    _pool.RemoveAt(i);
                    if (v is MonoBehaviour mb && mb.transform.parent != root)
                        mb.transform.SetParent(root, false);
                    return v;
                }
                _pool.RemoveAt(i);
            }

            var go = Instantiate(connectionPrefab, root);
            var visual = go.GetComponent<IConnectionVisual>();
            if (visual == null)
            {
                Debug.LogError("[HubConnectionRenderer] connectionPrefab is missing an IConnectionVisual component.");
                Destroy(go);
                return null;
            }
            return visual;
        }

        /// <summary>Hub position in the center-anchored space of root (same space as building markers).
        /// Corrects for root pivot so (0,0) = center of root rect, matching TuioToLocal and marker anchoredPositions.</summary>
        private Vector2 GetHubLocalPosition(ResidentialHubMono hub, RectTransform root)
        {
            if (root == null) return Vector2.zero;
            Vector3 local3d = root.InverseTransformPoint(hub.transform.position);
            Vector2 pivotCorrection = (new Vector2(0.5f, 0.5f) - root.pivot) * root.rect.size;
            return new Vector2(local3d.x, local3d.y) - pivotCorrection;
        }

        /// <summary>Remove all visuals and return them to pool. Call on reset.</summary>
        public void ClearAll()
        {
            foreach (var kv in _active)
            {
                if (kv.Value != null)
                {
                    kv.Value.SetActive(false);
                    _pool.Add(kv.Value);
                }
            }
            _active.Clear();
        }
    }
}
