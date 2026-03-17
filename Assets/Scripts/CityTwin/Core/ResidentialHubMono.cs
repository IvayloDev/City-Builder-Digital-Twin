using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CityTwin.Core
{
    /// <summary>Residential hub placed in the scene. Population comes from prefab (Inspector), not JSON. Used by HubRegistry and SimulationEngine.</summary>
    public class ResidentialHubMono : MonoBehaviour
    {
        [Tooltip("Unique id for this hub (e.g. H1, H2, H3).")]
        public string HubId = "H1";

        [Tooltip("Population for scoring formulas. Set on prefab or variant (e.g. 60000, 90000).")]
        public int Population = 50000;

        [SerializeField] private TextMeshProUGUI populationText;
        [SerializeField] private Image safetyFillImage;
        [SerializeField] private Image economyFillImage;
        [SerializeField] private Image cultureFillImage;
        [SerializeField] private Image environmentFillImage;

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
            RefreshPopulationText();
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

        /// <summary>Update indicator circles based on per-hub metric scores. Each metric caps at 20 in the simulation, so we treat 20 as "full" (0.25) for the quarter-pie.</summary>
        public void SetMetricState(float env, float eco, float safety, float culture)
        {
            const float maxFill = 0.25f;
            const float metricCap = 20f; // same as qolCapPerMetric — 20 = full slice
            if (environmentFillImage != null) environmentFillImage.fillAmount = Mathf.Clamp01(env / metricCap) * maxFill;
            if (economyFillImage != null) economyFillImage.fillAmount = Mathf.Clamp01(eco / metricCap) * maxFill;
            if (safetyFillImage != null) safetyFillImage.fillAmount = Mathf.Clamp01(safety / metricCap) * maxFill;
            if (cultureFillImage != null) cultureFillImage.fillAmount = Mathf.Clamp01(culture / metricCap) * maxFill;
        }

        private void Start()
        {
            // Start with empty indicator slices; they will be filled when metrics are pushed.
            const float empty = 0f;
            if (environmentFillImage != null) environmentFillImage.fillAmount = empty;
            if (economyFillImage != null) economyFillImage.fillAmount = empty;
            if (safetyFillImage != null) safetyFillImage.fillAmount = empty;
            if (cultureFillImage != null) cultureFillImage.fillAmount = empty;

            RefreshPopulationText();
        }

        private void RefreshPopulationText()
        {
            if (populationText != null)
                populationText.text = $"{Population / 1000}K";
        }
    }
}
