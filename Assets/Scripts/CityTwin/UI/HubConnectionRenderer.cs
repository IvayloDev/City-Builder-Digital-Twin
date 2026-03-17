using System.Collections.Generic;
using UnityEngine;
using CityTwin.Core;
using CityTwin.Simulation;

namespace CityTwin.UI
{
    /// <summary>
    /// Manages visual connection lines between placed building tiles and the hubs they affect.
    /// Uses a pooled prefab with an IConnectionVisual implementation so the visual style
    /// (Image, LineRenderer, particles, etc.) can be swapped by changing the prefab.
    /// </summary>
    public class HubConnectionRenderer : MonoBehaviour
    {
        [Tooltip("RectTransform that all connection visuals are parented under.")]
        [SerializeField] private RectTransform contentRoot;

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

            var connections = simulationEngine.ActiveConnections;
            var hubs = hubRegistry.Hubs;

            _currentKeys.Clear();
            for (int i = 0; i < connections.Count; i++)
            {
                var c = connections[i];
                if (c.HubIndex < 0 || c.HubIndex >= hubs.Count) continue;
                if (!buildingSpawner.TryGetMarkerPosition(c.TileId, out Vector2 buildingPos)) continue;

                Vector2 hubPos = GetHubLocalPosition(hubs[c.HubIndex]);
                var key = (c.TileId, c.HubIndex);
                _currentKeys.Add(key);

                if (!_active.TryGetValue(key, out IConnectionVisual visual))
                {
                    visual = Acquire();
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

        private IConnectionVisual Acquire()
        {
            for (int i = _pool.Count - 1; i >= 0; i--)
            {
                var v = _pool[i];
                if (v != null)
                {
                    _pool.RemoveAt(i);
                    return v;
                }
                _pool.RemoveAt(i);
            }

            var go = Instantiate(connectionPrefab, contentRoot);
            var visual = go.GetComponent<IConnectionVisual>();
            if (visual == null)
            {
                Debug.LogError("[HubConnectionRenderer] connectionPrefab is missing an IConnectionVisual component.");
                Destroy(go);
                return null;
            }
            return visual;
        }

        private Vector2 GetHubLocalPosition(ResidentialHubMono hub)
        {
            if (contentRoot == null) return Vector2.zero;
            if (hub.transform is RectTransform hubRt)
            {
                Vector3 worldPos = hubRt.position;
                Vector2 localPos;
                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    contentRoot, RectTransformUtility.WorldToScreenPoint(null, worldPos), null, out localPos);
                return localPos;
            }
            Vector3 local3d = contentRoot.InverseTransformPoint(hub.transform.position);
            return new Vector2(local3d.x, local3d.y);
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
