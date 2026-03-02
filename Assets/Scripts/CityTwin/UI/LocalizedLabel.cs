using UnityEngine;
using UnityEngine.UI;
using TMPro;
using CityTwin.Localization;

namespace CityTwin.UI
{
    /// <summary>Sets a UI label from a localization key. Supports both UnityEngine.UI.Text and TextMeshProUGUI (TMP_Text). Assign one of them and a LocalizationService.</summary>
    public class LocalizedLabel : MonoBehaviour
    {
        [Tooltip("Key in game_config.json localization (e.g. ui.timer, building.park).")]
        [SerializeField] private string localizationKey;

        [Header("Target (assign one)")]
        [SerializeField] private Text textTarget;
        [SerializeField] private TMP_Text tmpTarget;

        [Header("Services")]
        [SerializeField] private LocalizationService localization;

        private void Awake()
        {
            if (localization == null) localization = GetComponentInParent<LocalizationService>(true) ?? GetComponentInChildren<LocalizationService>(true);
            if (textTarget == null) textTarget = GetComponent<Text>();
            if (tmpTarget == null) tmpTarget = GetComponent<TMP_Text>();
        }

        private void OnEnable()
        {
            Refresh();
        }

        /// <summary>Apply current language string to the assigned text target. Call when language changes.</summary>
        public void Refresh()
        {
            string value = GetLocalizedString();
            if (textTarget != null)
                textTarget.text = value;
            if (tmpTarget != null)
                tmpTarget.text = value;
        }

        /// <summary>Change the key at runtime and refresh.</summary>
        public void SetKey(string key)
        {
            localizationKey = key ?? "";
            Refresh();
        }

        private string GetLocalizedString()
        {
            if (string.IsNullOrEmpty(localizationKey)) return "";
            if (localization != null) return localization.GetString(localizationKey);
            return localizationKey;
        }
    }
}
