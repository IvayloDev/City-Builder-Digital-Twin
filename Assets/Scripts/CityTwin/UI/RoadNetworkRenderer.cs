using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using CityTwin.Simulation;

namespace CityTwin.UI
{
    /// <summary>
    /// Renders transit graph edges as static lines on the content root.
    /// Uses the same IConnectionVisual / connectionPrefab system as building connection lines.
    /// </summary>
    public class RoadNetworkRenderer : MonoBehaviour
    {
        [SerializeField] private SimulationEngine simulationEngine;
        [SerializeField] private BuildingSpawner buildingSpawner;
        [SerializeField] private GameObject connectionPrefab;

        [Tooltip("Optional holder for road line visuals. Must be a RectTransform child of the content root. Automatically configured to stretch-fill so coordinates match. If null, lines parent directly to the content root.")]
        [SerializeField] private RectTransform roadLineHolder;

        [Header("Visual")]
        [SerializeField] private Color roadColor = new Color(0.486f, 0.549f, 0.627f, 0.25f); // #7c8ca0 at 25%
        [SerializeField] private float roadThickness = 7f;

        private readonly List<IConnectionVisual> _roadVisuals = new List<IConnectionVisual>();
        private bool _rendered;

        private void Awake()
        {
            if (simulationEngine == null) simulationEngine = GetComponentInChildren<SimulationEngine>(true);
            if (buildingSpawner == null) buildingSpawner = GetComponentInChildren<BuildingSpawner>(true);
        }

        private IEnumerator Start()
        {
            yield return null;
            RenderRoads();
        }

        public void RenderRoads()
        {
            ClearRoads();

            if (simulationEngine == null || buildingSpawner == null || connectionPrefab == null)
                return;

            var graph = simulationEngine.TransitGraph;
            if (graph == null) return;

            RectTransform root = buildingSpawner.ContentRoot;
            if (root == null) return;

            RectTransform lineParent = roadLineHolder != null ? roadLineHolder : root;
            EnsureHolderSetup(roadLineHolder);

            var edges = graph.Edges;
            for (int i = 0; i < edges.Count; i++)
            {
                var e = edges[i];
                Vector2 from = graph.GetNode(e.FromId).Position;
                Vector2 to = graph.GetNode(e.ToId).Position;

                var go = Instantiate(connectionPrefab, lineParent);
                go.name = $"Road_{e.FromId}_{e.ToId}";

                var visual = go.GetComponent<IConnectionVisual>();
                if (visual == null)
                {
                    Destroy(go);
                    continue;
                }

                visual.UpdateEndpoints(
                    RootToHolderSpace(from, root, lineParent),
                    RootToHolderSpace(to, root, lineParent));
                visual.SetActive(true);

                if (visual is MonoBehaviour mb)
                {
                    var graphic = mb.GetComponent<Graphic>();
                    if (graphic != null) graphic.color = roadColor;

                    if (mb.transform is RectTransform rt)
                        rt.sizeDelta = new Vector2(rt.sizeDelta.x, roadThickness);
                }

                _roadVisuals.Add(visual);
            }

            _rendered = true;
        }

        public void ClearRoads()
        {
            for (int i = 0; i < _roadVisuals.Count; i++)
            {
                if (_roadVisuals[i] is MonoBehaviour mb && mb != null)
                    Destroy(mb.gameObject);
            }
            _roadVisuals.Clear();
            _rendered = false;
        }

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

        private static Vector2 RootToHolderSpace(Vector2 pos, RectTransform root, RectTransform holder)
        {
            if (holder == null || holder == root) return pos;
            Vector2 rootLocal = pos + (Vector2)root.rect.center;
            Vector3 world = root.TransformPoint(new Vector3(rootLocal.x, rootLocal.y, 0f));
            Vector3 hl = holder.InverseTransformPoint(world);
            return new Vector2(hl.x, hl.y) - (Vector2)holder.rect.center;
        }

        public bool IsRendered => _rendered;
    }
}
