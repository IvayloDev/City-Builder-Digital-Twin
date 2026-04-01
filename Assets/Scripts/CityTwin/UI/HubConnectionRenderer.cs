using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using CityTwin.Core;
using CityTwin.Simulation;

namespace CityTwin.UI
{
    /// <summary>
    /// Manages visual connection lines between placed building tiles and road snap-points,
    /// and optional hub-to-hub links.
    /// Buildings connect to nearest points on road segments (transit graph edges).
    /// Each building-road connection is drawn as two layers:
    ///   Layer 1 (bg): wide, low-opacity stroke matching road style.
    ///   Layer 2 (fg): thinner, higher-opacity stroke.
    /// After drawing, updates building marker visuals to reflect connection state.
    /// </summary>
    public class HubConnectionRenderer : MonoBehaviour
    {
        [Tooltip("Optional override. Connection lines are always parented and positioned in BuildingSpawner's content root (the table/map) so they align with buildings. Only set this if you have no BuildingSpawner.")]
        [SerializeField] private RectTransform contentRootOverride;

        [Tooltip("Prefab with a MonoBehaviour implementing IConnectionVisual (e.g. StretchedImageConnection).")]
        [SerializeField] private GameObject connectionPrefab;

        [Tooltip("Optional holder for building-road connection lines. Must be a RectTransform child of the content root. Automatically configured to stretch-fill so coordinates match. If null, lines parent directly to the content root.")]
        [SerializeField] private RectTransform buildingRoadLineHolder;

        [Tooltip("Optional holder for hub-hub connection lines. Must be a RectTransform child of the content root. Automatically configured to stretch-fill so coordinates match. If null, lines parent directly to the content root.")]
        [SerializeField] private RectTransform hubHubLineHolder;

        [SerializeField] private HubRegistry hubRegistry;
        [SerializeField] private BuildingSpawner buildingSpawner;
        [SerializeField] private SimulationEngine simulationEngine;

        [Header("Building -> Road (two-layer)")]
        [SerializeField] private Color buildingRoadBgColor = new Color(0.486f, 0.549f, 0.627f, 0.25f); // #7c8ca0 at 25%
        [SerializeField] private Color buildingRoadFgColor = new Color(0.486f, 0.549f, 0.627f, 0.50f); // #7c8ca0 at 50%
        [SerializeField] private float bgThickness = 7f;
        [SerializeField] private float fgThickness = 3f;

        [Header("Hub -> Hub")]
        [SerializeField] private bool drawHubToHubConnections = true;
        [SerializeField] private bool useHubToHubColorOverride = true;
        [SerializeField] private Color hubToHubColor = new Color(0.5f, 0.85f, 1f, 0.7f);

        private readonly Dictionary<(string tileId, int snapIndex), IConnectionVisual> _buildingRoadBg =
            new Dictionary<(string, int), IConnectionVisual>();
        private readonly Dictionary<(string tileId, int snapIndex), IConnectionVisual> _buildingRoadFg =
            new Dictionary<(string, int), IConnectionVisual>();
        private readonly Dictionary<(int hubA, int hubB), IConnectionVisual> _activeHubHub =
            new Dictionary<(int, int), IConnectionVisual>();
        private readonly List<IConnectionVisual> _pool = new List<IConnectionVisual>();
        private readonly HashSet<(string, int)> _currentBuildingRoadKeys = new HashSet<(string, int)>();
        private readonly HashSet<(int, int)> _currentHubHubKeys = new HashSet<(int, int)>();

        private void Awake()
        {
            if (hubRegistry == null) hubRegistry = GetComponentInChildren<HubRegistry>(true);
            if (buildingSpawner == null) buildingSpawner = GetComponentInChildren<BuildingSpawner>(true);
            if (simulationEngine == null) simulationEngine = GetComponentInChildren<SimulationEngine>(true);
        }

        private void OnEnable()
        {
            if (simulationEngine != null)
                simulationEngine.OnMetricsChanged += Refresh;
            Refresh();
        }

        private IEnumerator Start()
        {
            yield return null;
            Refresh();
        }

        private void OnDisable()
        {
            if (simulationEngine != null)
                simulationEngine.OnMetricsChanged -= Refresh;
        }

        private void Refresh()
        {
            if (buildingSpawner == null || connectionPrefab == null)
                return;

            RectTransform root = buildingSpawner.ContentRoot != null ? buildingSpawner.ContentRoot : contentRootOverride;
            if (root == null)
                return;

            _currentBuildingRoadKeys.Clear();
            _currentHubHubKeys.Clear();
            bool useTableSpace = (root == buildingSpawner.ContentRoot);

            RectTransform brParent = buildingRoadLineHolder != null ? buildingRoadLineHolder : root;
            RectTransform hhParent = hubHubLineHolder != null ? hubHubLineHolder : root;
            EnsureHolderSetup(buildingRoadLineHolder);
            EnsureHolderSetup(hubHubLineHolder);

            // --- Building -> Road snap-point lines (two layers) ---
            if (simulationEngine != null)
            {
                var roadConnections = simulationEngine.ActiveRoadConnections;
                var perTileSnapIndex = new Dictionary<string, int>();

                for (int i = 0; i < roadConnections.Count; i++)
                {
                    var rc = roadConnections[i];

                    Vector2 buildingPos;
                    bool gotBuilding = useTableSpace
                        ? buildingSpawner.TryGetMarkerPosition(rc.TileId, out buildingPos)
                        : buildingSpawner.TryGetMarkerPositionIn(rc.TileId, root, out buildingPos);
                    if (!gotBuilding) continue;

                    if (!perTileSnapIndex.TryGetValue(rc.TileId, out int snapIdx))
                        snapIdx = 0;
                    perTileSnapIndex[rc.TileId] = snapIdx + 1;

                    var key = (rc.TileId, snapIdx);
                    _currentBuildingRoadKeys.Add(key);

                    Vector2 brFrom = RootToHolderSpace(buildingPos, root, brParent);
                    Vector2 brTo = RootToHolderSpace(rc.SnapPoint, root, brParent);

                    // Layer 1: Background (wide, dim)
                    if (!_buildingRoadBg.TryGetValue(key, out IConnectionVisual bgVisual))
                    {
                        bgVisual = Acquire(brParent);
                        if (bgVisual != null)
                            _buildingRoadBg[key] = bgVisual;
                    }
                    if (bgVisual != null)
                    {
                        bgVisual.UpdateEndpoints(brFrom, brTo);
                        ApplyStyle(bgVisual, buildingRoadBgColor, bgThickness);
                        bgVisual.SetActive(true);
                    }

                    // Layer 2: Foreground (thin, brighter)
                    if (!_buildingRoadFg.TryGetValue(key, out IConnectionVisual fgVisual))
                    {
                        fgVisual = Acquire(brParent);
                        if (fgVisual != null)
                            _buildingRoadFg[key] = fgVisual;
                    }
                    if (fgVisual != null)
                    {
                        fgVisual.UpdateEndpoints(brFrom, brTo);
                        ApplyStyle(fgVisual, buildingRoadFgColor, fgThickness);
                        fgVisual.SetActive(true);
                    }
                }
            }

            // --- Hub -> Hub lines (optional, kept for backward compat) ---
            if (drawHubToHubConnections && hubRegistry != null)
            {
                hubRegistry.FetchHubs();
                var hubs = hubRegistry.Hubs;
                if (hubs.Count >= 2)
                {
                    for (int i = 0; i < hubs.Count - 1; i++)
                    {
                        Vector2 a = RootToHolderSpace(GetHubLocalPosition(hubs[i], root), root, hhParent);
                        for (int j = i + 1; j < hubs.Count; j++)
                        {
                            Vector2 b = RootToHolderSpace(GetHubLocalPosition(hubs[j], root), root, hhParent);
                            var key = (i, j);
                            _currentHubHubKeys.Add(key);

                            if (!_activeHubHub.TryGetValue(key, out IConnectionVisual visual))
                            {
                                visual = Acquire(hhParent);
                                if (visual == null) continue;
                                _activeHubHub[key] = visual;
                            }

                            visual.UpdateEndpoints(a, b);
                            if (useHubToHubColorOverride)
                                ApplyColor(visual, hubToHubColor);
                            visual.SetActive(true);
                        }
                    }
                }
            }

            // --- Deactivate unused building-road visuals (bg layer) ---
            RecycleStale(_buildingRoadBg, _currentBuildingRoadKeys);
            // --- Deactivate unused building-road visuals (fg layer) ---
            RecycleStale(_buildingRoadFg, _currentBuildingRoadKeys);

            // --- Deactivate unused hub-hub visuals ---
            var toRemoveHubHub = new List<(int, int)>();
            foreach (var kv in _activeHubHub)
            {
                if (!_currentHubHubKeys.Contains(kv.Key))
                {
                    kv.Value.SetActive(false);
                    _pool.Add(kv.Value);
                    toRemoveHubHub.Add(kv.Key);
                }
            }
            for (int i = 0; i < toRemoveHubHub.Count; i++)
                _activeHubHub.Remove(toRemoveHubHub[i]);

            // --- Update building marker connection states ---
            UpdateMarkerConnectionStates();
        }

        private void RecycleStale(Dictionary<(string, int), IConnectionVisual> dict, HashSet<(string, int)> currentKeys)
        {
            var toRemove = new List<(string, int)>();
            foreach (var kv in dict)
            {
                if (!currentKeys.Contains(kv.Key))
                {
                    kv.Value.SetActive(false);
                    _pool.Add(kv.Value);
                    toRemove.Add(kv.Key);
                }
            }
            for (int i = 0; i < toRemove.Count; i++)
                dict.Remove(toRemove[i]);
        }

        private void UpdateMarkerConnectionStates()
        {
            if (simulationEngine == null || buildingSpawner == null) return;

            var graph = simulationEngine.TransitGraph;
            bool hasRoads = graph != null && graph.Edges.Count > 0;

            var tileStates = simulationEngine.TileStates;
            for (int i = 0; i < tileStates.Count; i++)
            {
                var ts = tileStates[i];
                MarkerConnectionState state;

                if (ts.Inactive || ts.OverlapInvalid)
                    state = MarkerConnectionState.Inactive;
                else if (hasRoads && !ts.Connected)
                    state = MarkerConnectionState.Disconnected;
                else
                    state = MarkerConnectionState.Connected;

                buildingSpawner.SetMarkerConnectionState(ts.TileId, state);
            }
        }

        /// <summary>Force a holder RectTransform to stretch-fill its parent with center pivot.</summary>
        private static void EnsureHolderSetup(RectTransform holder)
        {
            if (holder == null) return;
            holder.anchorMin = Vector2.zero;
            holder.anchorMax = Vector2.one;
            holder.offsetMin = Vector2.zero;
            holder.offsetMax = Vector2.zero;
            holder.pivot = new Vector2(0.5f, 0.5f);
            holder.localScale = Vector3.one;
            holder.localRotation = Quaternion.identity;
        }

        /// <summary>Convert a position from content root center-origin space to a holder's
        /// center-origin space via world-space, so the holder can live anywhere in the hierarchy.</summary>
        private static Vector2 RootToHolderSpace(Vector2 pos, RectTransform root, RectTransform holder)
        {
            if (holder == null || holder == root) return pos;
            Vector2 rootLocal = pos + (Vector2)root.rect.center;
            Vector3 world = root.TransformPoint(new Vector3(rootLocal.x, rootLocal.y, 0f));
            Vector3 hl = holder.InverseTransformPoint(world);
            return new Vector2(hl.x, hl.y) - (Vector2)holder.rect.center;
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

        private static void ApplyColor(IConnectionVisual visual, Color color)
        {
            if (!(visual is MonoBehaviour mb) || mb == null) return;
            var graphic = mb.GetComponent<Graphic>();
            if (graphic != null) graphic.color = color;
        }

        private static void ApplyStyle(IConnectionVisual visual, Color color, float thickness)
        {
            if (!(visual is MonoBehaviour mb) || mb == null) return;
            var graphic = mb.GetComponent<Graphic>();
            if (graphic != null) graphic.color = color;
            if (mb.transform is RectTransform rt)
                rt.sizeDelta = new Vector2(rt.sizeDelta.x, thickness);
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
            ClearDict(_buildingRoadBg);
            ClearDict(_buildingRoadFg);

            foreach (var kv in _activeHubHub)
            {
                if (kv.Value != null)
                {
                    kv.Value.SetActive(false);
                    _pool.Add(kv.Value);
                }
            }
            _activeHubHub.Clear();
        }

        private void ClearDict(Dictionary<(string, int), IConnectionVisual> dict)
        {
            foreach (var kv in dict)
            {
                if (kv.Value != null)
                {
                    kv.Value.SetActive(false);
                    _pool.Add(kv.Value);
                }
            }
            dict.Clear();
        }
    }
}
