using UnityEngine;
using UnityEngine.UI;
using TMPro;
using CityTwin.Localization;

namespace CityTwin.UI
{
    /// <summary>Optional: put on the building marker prefab to show building name/icon. BuildingSpawner will call SetBuilding after spawn.</summary>
    public class BuildingMarkerDisplay : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI label;
        [SerializeField] private Image icon;
        [Tooltip("Optional: visual halo root to scale per building type (e.g. garden < park < recycling_plant).")]
        [SerializeField] private Transform haloRoot;
        [Tooltip("Optional: image used for the halo; color will be driven from config if assigned.")]
        [SerializeField] private Image haloImage;
        [Tooltip("ScriptableObject with per-building halo color and scale settings.")]
        [SerializeField] private BuildingVisualConfig visualConfig;
        [Tooltip("Base halo scale before applying per-building multiplier from config.")]
        [SerializeField] private float baseHaloScale = 1f;

        private void Awake()
        {
            if (label == null) label = GetComponentInChildren<TextMeshProUGUI>(true);
            if (icon == null) icon = GetComponentInChildren<Image>(true);
            if (haloRoot == null && haloImage != null) haloRoot = haloImage.transform;
            if (haloImage == null && haloRoot != null) haloImage = haloRoot.GetComponentInChildren<Image>(true);
        }

        public void SetBuilding(string buildingId)
        {
            if (label != null)
                label.text = string.IsNullOrEmpty(buildingId) ? "?" : buildingId;
            if (icon != null)
                icon.enabled = true;

            ApplyVisuals(buildingId);
        }

        /// <summary>Optionally set from config for localized name.</summary>
        public void SetBuildingWithLocalization(string buildingId, LocalizationService localization)
        {
            if (localization != null && !string.IsNullOrEmpty(buildingId))
            {
                string key = $"building.{buildingId}.name";
                string localized = localization.GetString(key);
                if (localized != key && label != null) { label.text = localized; return; }
            }
            SetBuilding(buildingId);
        }

        private void ApplyVisuals(string buildingId)
        {
            float multiplier = 1f;
            Color? haloColor = null;

            if (visualConfig != null && !string.IsNullOrEmpty(buildingId))
            {
                var entry = visualConfig.GetEntry(buildingId);
                if (entry != null)
                {
                    multiplier = entry.haloScaleMultiplier;
                    haloColor = entry.haloColor;
                }
            }

            float scale = baseHaloScale * multiplier;
            if (haloRoot != null)
                haloRoot.localScale = new Vector3(scale, scale, 1f);

            if (haloImage != null && haloColor.HasValue)
                haloImage.color = haloColor.Value;
        }
    }
}
