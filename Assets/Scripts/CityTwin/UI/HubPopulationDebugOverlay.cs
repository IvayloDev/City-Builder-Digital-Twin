using System.Text;
using CityTwin.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using TMPro;

namespace CityTwin.UI
{
    /// <summary>Optional debug overlay: list each hub's HubId and Population. Toggle with F2. Assign TextMeshProUGUI or leave empty to auto-find.</summary>
    public class HubPopulationDebugOverlay : MonoBehaviour
    {
        [SerializeField] private bool showInGame = true;
        [SerializeField] private Key toggleKey = Key.F2;
        [Tooltip("Assign a TextMeshProUGUI to show hub list. If unset, uses one on this GameObject.")]
        [SerializeField] private TextMeshProUGUI debugText;
        [Tooltip("If unset, finds HubRegistry in scene.")]
        [SerializeField] private HubRegistry hubRegistry;

        private bool _visible = true;

        private void Awake()
        {
            if (debugText == null)
                debugText = GetComponent<TextMeshProUGUI>();
            if (hubRegistry == null)
                hubRegistry = GetComponentInParent<HubRegistry>(true) ?? GetComponentInChildren<HubRegistry>(true);
            if (debugText != null)
                debugText.gameObject.SetActive(_visible && showInGame);
        }

        private void Update()
        {
            if (Keyboard.current != null && Keyboard.current[toggleKey].wasPressedThisFrame)
            {
                _visible = !_visible;
                if (debugText != null)
                    debugText.gameObject.SetActive(_visible && showInGame);
            }

            if (debugText == null || !showInGame || !_visible) return;

            if (hubRegistry == null)
            {
                debugText.text = "[Hubs] No HubRegistry in scene.";
                return;
            }
            if (!hubRegistry.IsValid || hubRegistry.Hubs.Count == 0)
            {
                debugText.text = "[Hubs] No valid hubs (check HubRegistry validation).";
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("Hubs (prefab population):");
            foreach (var h in hubRegistry.Hubs)
                sb.AppendLine($"  {h.HubId}: {h.Population:N0}");
            debugText.text = sb.ToString();
        }
    }
}
