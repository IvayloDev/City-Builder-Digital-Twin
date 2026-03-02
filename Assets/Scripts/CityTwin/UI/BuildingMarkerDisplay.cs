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

        private void Awake()
        {
            if (label == null) label = GetComponentInChildren<TextMeshProUGUI>(true);
            if (icon == null) icon = GetComponentInChildren<Image>(true);
        }

        public void SetBuilding(string buildingId)
        {
            if (label != null)
                label.text = string.IsNullOrEmpty(buildingId) ? "?" : buildingId;
            if (icon != null)
                icon.enabled = true;
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
    }
}
