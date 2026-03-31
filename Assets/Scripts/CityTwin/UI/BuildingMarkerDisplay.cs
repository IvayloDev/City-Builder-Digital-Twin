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
        [Tooltip("Fallback halo radius used when no halo image/rect is available.")]
        [SerializeField] private float fallbackHaloRadius = 24f;

        private string _currentBuildingId;
        private bool _isPlacementInvalid;
        private Color _invalidHaloColor = Color.red;
        private Color _configuredHaloColor = Color.white;
        private bool _hasConfiguredHaloColor;

        private void Awake()
        {
            EnsureReferences();
        }

        public void SetBuilding(string buildingId)
        {
            _currentBuildingId = buildingId;

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
            EnsureReferences();

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

            if (haloColor.HasValue)
            {
                _configuredHaloColor = haloColor.Value;
                _hasConfiguredHaloColor = true;
            }
            else if (haloImage != null)
            {
                _configuredHaloColor = haloImage.color;
                _hasConfiguredHaloColor = true;
            }

            ApplyHaloColorState();
        }

        public void SetPlacementInvalid(bool isInvalid, Color invalidColor)
        {
            _isPlacementInvalid = isInvalid;
            _invalidHaloColor = invalidColor;
            ApplyHaloColorState();
        }

        public float GetVisualRadiusForBuilding(string buildingId)
        {
            EnsureReferences();
            float multiplier = GetHaloScaleMultiplier(buildingId);
            float radius = GetBaseHaloRadius();
            float scale = Mathf.Max(0.01f, baseHaloScale * multiplier);
            return Mathf.Max(1f, radius * scale);
        }

        public bool TryGetCurrentVisualRadius(RectTransform inSpace, out float radius)
        {
            radius = 0f;
            EnsureReferences();

            if (inSpace == null)
            {
                radius = GetVisualRadiusForBuilding(_currentBuildingId);
                return true;
            }

            if (haloImage != null)
            {
                var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(inSpace, haloImage.rectTransform);
                radius = Mathf.Max(bounds.extents.x, bounds.extents.y);
                if (radius > 0.001f) return true;
            }

            if (haloRoot is RectTransform haloRect)
            {
                var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(inSpace, haloRect);
                radius = Mathf.Max(bounds.extents.x, bounds.extents.y);
                if (radius > 0.001f) return true;
            }

            radius = GetVisualRadiusForBuilding(_currentBuildingId);
            return true;
        }

        private void EnsureReferences()
        {
            if (label == null) label = GetComponentInChildren<TextMeshProUGUI>(true);
            if (icon == null) icon = GetComponentInChildren<Image>(true);
            if (haloRoot == null && haloImage != null) haloRoot = haloImage.transform;
            if (haloImage == null && haloRoot != null) haloImage = haloRoot.GetComponentInChildren<Image>(true);
        }

        private float GetHaloScaleMultiplier(string buildingId)
        {
            float multiplier = 1f;
            if (visualConfig == null || string.IsNullOrEmpty(buildingId))
                return multiplier;

            var entry = visualConfig.GetEntry(buildingId);
            if (entry != null)
                multiplier = entry.haloScaleMultiplier;
            return Mathf.Max(0.01f, multiplier);
        }

        private float GetBaseHaloRadius()
        {
            if (haloImage != null && haloImage.rectTransform != null)
            {
                var rect = haloImage.rectTransform.rect;
                float r = Mathf.Max(rect.width, rect.height) * 0.5f;
                if (r > 0.001f) return r;
            }

            if (haloRoot is RectTransform haloRect)
            {
                var rect = haloRect.rect;
                float r = Mathf.Max(rect.width, rect.height) * 0.5f;
                if (r > 0.001f) return r;
            }

            return Mathf.Max(1f, fallbackHaloRadius);
        }

        private void ApplyHaloColorState()
        {
            if (haloImage == null) return;
            if (_isPlacementInvalid)
            {
                haloImage.color = _invalidHaloColor;
                return;
            }

            if (_hasConfiguredHaloColor)
                haloImage.color = _configuredHaloColor;
        }
    }
}
