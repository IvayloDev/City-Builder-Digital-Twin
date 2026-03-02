using UnityEngine;

namespace CityTwin.Core
{
    /// <summary>Residential hub placed in the scene. Population comes from prefab (Inspector), not JSON. Used by HubRegistry and SimulationEngine.</summary>
    public class ResidentialHubMono : MonoBehaviour
    {
        [Tooltip("Unique id for this hub (e.g. H1, H2, H3).")]
        public string HubId = "H1";

        [Tooltip("Population for scoring formulas. Set on prefab or variant (e.g. 60000, 90000).")]
        public int Population = 50000;

        [Tooltip("Draw gizmo sphere at hub position in editor / debug.")]
        public bool ShowDebugGizmos = true;

        [Tooltip("Optional: scale this transform by population for visual feedback.")]
        public Transform VisualRoot;

        /// <summary>World position as Vector2 (X,Z or X,Y depending on your map plane). Override if your hub uses a different plane.</summary>
        public virtual Vector2 Position2D
        {
            get
            {
                var p = transform.position;
                return new Vector2(p.x, p.z);
            }
        }

        private void OnValidate()
        {
            if (Population < 0) Population = 0;
        }

        private void OnDrawGizmos()
        {
            if (!ShowDebugGizmos) return;
            Gizmos.color = Color.cyan;
            var p = transform.position;
            Gizmos.DrawWireSphere(p, 0.5f);
#if UNITY_EDITOR
            UnityEditor.Handles.Label(p + Vector3.up * 15f, $"{HubId} {Population:N0}");
#endif
        }

        private void Start()
        {
            if (VisualRoot != null)
            {
                float scale = Mathf.Clamp(Population / 100000f, 0.5f, 2f);
                VisualRoot.localScale = Vector3.one * scale;
            }
        }
    }
}
