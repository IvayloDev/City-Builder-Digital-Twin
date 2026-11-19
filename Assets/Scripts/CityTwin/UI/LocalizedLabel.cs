using UnityEngine;
using UnityEngine.UI;
using TMPro;
using CityTwin.Core;
using CityTwin.Localization;

namespace CityTwin.UI
{
    /// <summary>
    /// Attach to a button or any GameObject with text. Shows the localized string for
    /// <see cref="localizationKey"/> using the currently chosen language and refreshes
    /// when language changes. Resolves LocalizationService from GameInstanceRoot (one parent walk).
    /// </summary>
    [DisallowMultipleComponent]
    public class LocalizedLabel : MonoBehaviour
    {
        [SerializeField] private LocalizationService localization;
        [Tooltip("Key in your localization table (e.g. start.english, ui.timer).")]
        [SerializeField] private string localizationKey;

        [Header("Target (optional)")]
        [Tooltip("If set, this text is updated. Otherwise uses TextMeshProUGUI or Text on this GameObject.")]
        [SerializeField] private TextMeshProUGUI targetTmp;

        private void Awake() => Bind();

        private void OnEnable()
        {
            Bind();
            Refresh();
        }

        private void Start()
        {
            // Config often finishes loading between OnEnable and Start. Refresh again so a label
            // that only saw the raw key on the first pass picks up the real string.
            Bind();
            Refresh();
        }

        private void OnDisable()
        {
            if (localization != null)
                localization.OnLanguageChanged -= Refresh;
        }

        private void Bind()
        {
            if (localization == null)
            {
                var root = GetComponentInParent<GameInstanceRoot>(true);
                if (root != null)
                    localization = root.LocalizationService;
            }
            if (targetTmp == null)
                targetTmp = GetComponent<TextMeshProUGUI>();
            if (localization == null) return;
            localization.OnLanguageChanged -= Refresh;
            localization.OnLanguageChanged += Refresh;
        }

        /// <summary>Set the key and refresh (e.g. from code).</summary>
        public void SetKey(string key)
        {
            localizationKey = key ?? "";
            Refresh();
        }

        /// <summary>Apply the current language string to the target text. An unresolved key leaves the authored label alone.</summary>
        public void Refresh()
        {
            if (targetTmp == null || string.IsNullOrEmpty(localizationKey)) return;
            if (localization == null) Bind();
            if (localization == null) return;
            string text = localization.GetString(localizationKey);
            if (string.IsNullOrEmpty(text) || text == localizationKey) return;
            targetTmp.text = text;
        }
    }
}
